using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Repository generico dei master condivisi. Quando il workspace contiene il manifest Phase D, Load/Save non
/// usano piu' il JSON monolitico come source of truth: il contratto storico resta invariato per i caller, ma
/// viene tradotto in mutation per UID con version vector e materializzazione SQLite locale. Il percorso JSON
/// storico rimane disponibile soltanto per workspace non ancora inizializzati Phase D e per bootstrap/import.
///
/// Ogni List restituita direttamente da Load() porta inoltre, tramite ConditionalWeakTable, il fingerprint dei
/// byte da cui e' stata letta nel percorso legacy. In Phase D la List porta invece la fotografia causale delle
/// UID lette; una lista filtrata mantiene il token per-entita' sugli oggetti, così Strumenti e altri caller
/// derivati non trasformano omissioni UI in delete e non perdono la baseline distribuita.
/// </summary>
public class MasterDataRepository<T> where T : MasterEntity
{
    private sealed class LoadedListState
    {
        public required bool FileExisted { get; init; }
        public byte[]? Sha256 { get; init; }
        public byte[]? SourceBytes { get; init; }
    }

    private sealed class LoadedEntityState
    {
        public required byte[] Sha256 { get; init; }
        public required string OriginalKey { get; init; }
        public required byte[] SnapshotBytes { get; init; }
    }

    private readonly string _path;
    private readonly Func<T, string>? _preserveOmittedKeyOf;
    private readonly Predicate<T>? _preserveWhenOmitted;
    private readonly bool _fileExistedAtConstruction;
    private int _successfulWriteSinceConstruction;
    private (DateTime Mtime, byte[] Json)? _cache;
    private readonly object _gate = new();
    private readonly ConditionalWeakTable<List<T>, LoadedListState> _loadedLists = new();
    private readonly ConditionalWeakTable<T, LoadedEntityState> _loadedEntities = new();
    private readonly ConditionalWeakTable<List<T>, PhaseDLoadedListState> _phaseDLoadedLists = new();
    private readonly ConditionalWeakTable<T, PhaseDLoadedEntityState> _phaseDLoadedEntities = new();
    private PhaseDMasterRepositoryEngine<T>? _phaseDEngine;
    private string? _phaseDRoot;
    private string? _phaseDCatalog;

    public MasterDataRepository(
        string path,
        Func<T, string>? preserveOmittedKeyOf = null,
        Predicate<T>? preserveWhenOmitted = null)
    {
        if ((preserveOmittedKeyOf == null) != (preserveWhenOmitted == null))
            throw new ArgumentException("Chiave e predicato di preservazione delle righe omesse devono essere specificati insieme.");
        _path = path;
        _preserveOmittedKeyOf = preserveOmittedKeyOf;
        _preserveWhenOmitted = preserveWhenOmitted;
        _fileExistedAtConstruction = File.Exists(path);
    }

    public List<T> Load()
    {
        PerfContatori.IncrementaMasterLoad();
        if (TryGetPhaseDEngine(out var phaseD))
        {
            var loaded = phaseD.Load();
            TrackPhaseDLoadedList(loaded.Items, loaded.State);
            return loaded.Items;
        }

        if (!File.Exists(_path))
        {
            lock (_gate) _cache = null;
            return TrackLoadedList(new List<T>(), fileExisted: false, sourceBytes: null);
        }

        DateTime mtime;
        try { mtime = File.GetLastWriteTimeUtc(_path); }
        catch (Exception ex)
        {
            lock (_gate) _cache = null;
            throw new MasterDataReadException(_path, "Impossibile leggere la data di modifica del file master.", ex);
        }

        byte[] json;
        lock (_gate)
        {
            if (_cache is not { } cached || cached.Mtime != mtime)
            {
                byte[] bytes;
                try { bytes = JsonIo.ReadAllBytesShared(_path); }
                catch (Exception ex)
                {
                    _cache = null;
                    throw new MasterDataReadException(_path, "Il file master esiste ma non e leggibile.", ex);
                }
                cached = (mtime, bytes);
                _cache = cached;
            }
            json = cached.Json;
        }

        var list = JsonIo.TryParse<List<T>>(json, out var error);
        if (list == null)
        {
            lock (_gate) _cache = null;
            throw new MasterDataReadException(_path, $"Il file master contiene JSON non valido: {error}");
        }
        return TrackLoadedList(list, fileExisted: true, json);
    }

    /// <summary>
    /// Compatibilita' per i caller storici. In Phase D la provenienza della lista/entita' viene usata per
    /// produrre mutation per-UID e consentire merge automatici solo quando le modifiche sono causalmente
    /// disgiunte. Nel percorso legacy resta la rete di sicurezza raw-SHA/three-way preesistente.
    /// </summary>
    public void Save(List<T> items)
    {
        // Il percorso di scrittura non passa MAI da un refresh accorpato: deve vedere lo stato
        // condiviso piu' aggiornato prima di decidere merge o conflitto (vedi MasterLetturaScope).
        using var scritturaFuoriDaLetturaAccorpata = MasterLetturaScope.Sospendi();
        if (TryGetPhaseDEngine(out var phaseD))
        {
            // Nessun contesto di sessione/lock qui (percorso storico Load -> modifica diretta della List ->
            // Save). Le aggiunte pure restano riconciliabili automaticamente (stesso contratto del three-way
            // legacy che questo percorso sostituisce), ma un edit a una riga gia' presente nella fotografia di
            // apertura non ha un Forza Sblocco a giustificarlo: se la base non e' piu' aggiornata deve restare
            // un conflitto pieno, indistinguibile altrimenti da una vera perdita di dati.
            SavePhaseD(phaseD, items, expectedSnapshot: null, allowSilentRemoteMerge: !HaEditARigaPreesistente(items));
            return;
        }

        var loadedState = TryGetLoadedState(items);
        var derivedSourceHash = loadedState == null ? TryGetDerivedSourceHash(items) : null;
        try
        {
            SaveInternal(items, expectedSnapshot: null, loadedState, derivedSourceHash);
        }
        catch (MasterDataConflictException)
        {
            if (derivedSourceHash != null && _preserveOmittedKeyOf != null)
            {
                SaveDerivedLegacyThreeWay(items);
                return;
            }

            if (loadedState is { FileExisted: true, SourceBytes: not null } && _preserveOmittedKeyOf != null)
            {
                var baseSnapshot = JsonIo.TryParse<List<T>>(loadedState.SourceBytes, out var error)
                    ?? throw new MasterDataReadException(_path,
                        $"La fotografia master di apertura non e piu decodificabile; merge additivo annullato: {error}");
                SaveAdditionsPreservingRemote(items, baseSnapshot, _preserveOmittedKeyOf);
                return;
            }

            throw;
        }
    }

    public virtual void Save(List<T> items, IReadOnlyList<T>? expectedSnapshot, bool allowSilentRemoteMerge = true, string? baselineSourceFingerprint = null)
    {
        // Il percorso di scrittura non passa MAI da un refresh accorpato: deve vedere lo stato
        // condiviso piu' aggiornato prima di decidere merge o conflitto (vedi MasterLetturaScope).
        using var scritturaFuoriDaLetturaAccorpata = MasterLetturaScope.Sospendi();
        if (TryGetPhaseDEngine(out var phaseD))
        {
            SavePhaseD(phaseD, items, expectedSnapshot, allowSilentRemoteMerge, baselineSourceFingerprint);
            return;
        }

        var loadedState = TryGetLoadedState(items);
        // L'overload esplicito e' il percorso moderno: expectedSnapshot descrive intenzionalmente l'intero
        // risultato desiderato, incluse eventuali cancellazioni. Non applicare qui la preservazione automatica
        // pensata esclusivamente per vecchi caller che perdono righe tramite un filtro UI.
        SaveInternal(items, expectedSnapshot, loadedState, derivedSourceHash: null);
    }

    /// <summary>
    /// Impronta economica (revisione+conteggio del catalogo Phase D, nessuna materializzazione integrale) da
    /// catturare a inizio di una sessione di modifica e ripassare a Save/SavePatch come baseline esplicita:
    /// necessaria quando la finestra caricata dall'editor (bounded/filtrata) e' piu' piccola dell'intero
    /// catalogo, dove un confronto sul solo conteggio/contenuto della finestra produrrebbe falsi conflitti o,
    /// peggio, non rileverebbe affatto una modifica remota avvenuta fuori da quella finestra. Null per workspace
    /// non ancora su Phase D: in quel caso il percorso legacy tre-vie resta l'unica rete di sicurezza.
    /// </summary>
    public string? CatturaImpronteCatalogoCorrente() =>
        TryGetPhaseDEngine(out var phaseD) ? phaseD.GetCatalogSourceFingerprint() : null;

    /// <summary>Impronta SHA-256 dei byte correnti, utilizzabile come guardia di sola lettura prima della pubblicazione di un export.</summary>
    public string CatturaImprontaLettura()
    {
        if (TryGetPhaseDEngine(out var phaseD)) return phaseD.GetCatalogSourceFingerprint() ?? "phaseD:none";
        if (!File.Exists(_path)) return "missing";
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_path)));
    }

    /// <summary>Calcolo, senza pubblicarle, delle mutation di un patch sul catalogo Phase D: serve al salvataggio atomico su piu'
    /// cataloghi, che pubblica le mutation di tutti i cataloghi in un solo batch. Null se il catalogo non e' ancora su Phase D:
    /// il chiamante ripiega sul percorso legacy transazionale.</summary>
    internal PhaseDMasterPatchPianificato<T>? PianificaPatchPhaseD(
        IReadOnlyList<T> desiredWindow,
        IReadOnlyList<T> baselineWindow,
        bool allowSilentRemoteMerge,
        string? baselineSourceFingerprint)
    {
        // Come Save: il percorso di scrittura non passa da un refresh accorpato (vedi MasterLetturaScope).
        using var scritturaFuoriDaLetturaAccorpata = MasterLetturaScope.Sospendi();
        if (!TryGetPhaseDEngine(out var phaseD)) return null;
        var mutations = phaseD.PianificaPatch(desiredWindow, baselineWindow, allowSilentRemoteMerge, baselineSourceFingerprint);
        return new PhaseDMasterPatchPianificato<T>(phaseD, mutations);
    }

    /// <summary>Vero se almeno un item tra quelli passati corrisponde (per riferimento, via _phaseDLoadedEntities)
    /// a una riga effettivamente caricata da questo repository il cui contenuto e' stato modificato da allora:
    /// una riga mai caricata qui (nuova) non conta mai come "edit". La stessa serializzazione canonica usata da
    /// PhaseDMasterRepositoryEngine (JsonIo.CompactOptions) garantisce che il confronto coincida con quello che
    /// l'engine stesso userebbe per decidere se la riga rappresenta un intento locale.</summary>
    private bool HaEditARigaPreesistente(List<T> items)
    {
        foreach (var item in items)
        {
            if (!_phaseDLoadedEntities.TryGetValue(item, out var state)) continue;
            if (JsonSerializer.Serialize(item, JsonIo.CompactOptions) != state.BaselinePayloadJson) return true;
        }
        return false;
    }

    private void SavePhaseD(
        PhaseDMasterRepositoryEngine<T> phaseD,
        List<T> items,
        IReadOnlyList<T>? expectedSnapshot,
        bool allowSilentRemoteMerge = true,
        string? baselineSourceFingerprint = null)
    {
        _phaseDLoadedLists.TryGetValue(items, out var listState);
        Dictionary<Guid, PhaseDLoadedEntityState>? derived = null;
        if (listState == null)
        {
            derived = new Dictionary<Guid, PhaseDLoadedEntityState>();
            foreach (var item in items)
                if (_phaseDLoadedEntities.TryGetValue(item, out var state))
                    derived[state.Uid] = state;
        }

        try
        {
            phaseD.Save(items, listState, expectedSnapshot, derived, allowSilentRemoteMerge, baselineSourceFingerprint);
            DiagnosticTraceService.Registra("MasterData.Save", $"{typeof(T).Name} righe={items.Count}");
        }
        finally
        {
            _phaseDLoadedLists.Remove(items);
            foreach (var item in items) _phaseDLoadedEntities.Remove(item);
        }
    }

    private bool TryGetPhaseDEngine(out PhaseDMasterRepositoryEngine<T> engine)
    {
        engine = null!;
        if (!PhaseDMasterCatalog.TryResolve(_path, out var root, out var catalog) ||
            !PhaseDMasterJournalService.IsInitialized(root))
            return false;

        if (_phaseDEngine == null ||
            !string.Equals(_phaseDRoot, root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_phaseDCatalog, catalog, StringComparison.OrdinalIgnoreCase))
        {
            _phaseDRoot = root;
            _phaseDCatalog = catalog;
            _phaseDEngine = new PhaseDMasterRepositoryEngine<T>(root, catalog, _preserveWhenOmitted);
        }
        engine = _phaseDEngine;
        return true;
    }

    private void TrackPhaseDLoadedList(List<T> list, PhaseDLoadedListState state)
    {
        _phaseDLoadedLists.Remove(list);
        _phaseDLoadedLists.Add(list, state);
        foreach (var item in list)
        {
            var uid = MasterEntityIdentityService.GetRequired(item);
            if (!state.Entities.TryGetValue(uid, out var entityState)) continue;
            _phaseDLoadedEntities.Remove(item);
            _phaseDLoadedEntities.Add(item, entityState);
        }
    }

    private void SaveInternal(
        List<T> items,
        IReadOnlyList<T>? expectedSnapshot,
        LoadedListState? loadedState,
        byte[]? derivedSourceHash)
    {
        EnsureWritable();
        ValidateDomain(items);

        // Rilettura, confronto con la fotografia attesa e scrittura avvengono sotto il lock di commit condiviso del
        // catalogo (come CR/NC): senza, due PC che superano il confronto nello stesso istante si sovrascrivono e le
        // righe scritte dall'altro PC vanno perse. Il lock non viene mai preso dentro _gate.
        using var lockCommit = DomainRecordCommitLock.Acquire("Anagrafica", Path.GetFileName(_path), _path);

        var fileExists = File.Exists(_path);
        var guardedConstructedFirstWrite = loadedState == null && expectedSnapshot == null && derivedSourceHash == null &&
                                           !_fileExistedAtConstruction &&
                                           System.Threading.Volatile.Read(ref _successfulWriteSinceConstruction) == 0;

        // I seed/default storici spesso fanno `if (!File.Exists) Save(listaNuova)` senza Load. Il repository è
        // però costruito prima di quel controllo e può ricordare che il file era assente. Se un altro writer ha
        // creato nel frattempo, accettiamo SOLO lo stesso stato canonico (race idempotente); mai sovrascrivere un
        // file diverso che potrebbe già contenere personalizzazioni dell'altro PC.
        if (guardedConstructedFirstWrite && fileExists)
        {
            var current = ParseCurrentForWrite(ReadCurrentBytesForWrite());
            if (!CanonicalEquals(current, items))
                throw new MasterDataConflictException(Path.GetFileName(_path));
            System.Threading.Volatile.Write(ref _successfulWriteSinceConstruction, 1);
            lock (_gate) _cache = null;
            return;
        }

        if (loadedState != null && loadedState.FileExisted != fileExists)
            throw new MasterDataConflictException(Path.GetFileName(_path));
        if (derivedSourceHash != null && !fileExists)
            throw new MasterDataConflictException(Path.GetFileName(_path));

        if (fileExists)
        {
            var currentBytes = ReadCurrentBytesForWrite();
            var current = ParseCurrentForWrite(currentBytes);
            byte[]? currentHash = null;

            // Fingerprint raw: oltre a costare molto meno di un secondo parse/clone da 100k righe, rileva
            // qualsiasi riscrittura del file avvenuta dopo QUELLA specifica Load(), anche se un'altra Load()
            // sullo stesso repository e' avvenuta nel frattempo per aggiornare un'altra schermata.
            if (loadedState is { FileExisted: true, Sha256: not null })
            {
                currentHash = SHA256.HashData(currentBytes);
                if (!currentHash.AsSpan().SequenceEqual(loadedState.Sha256))
                    throw new MasterDataConflictException(Path.GetFileName(_path));
            }

            // Una lista filtrata perde l'identita' della List originale ma non quella degli oggetti contenuti:
            // il token per-entita' permette di applicare la stessa precondizione alla versione da cui deriva.
            if (derivedSourceHash != null)
            {
                currentHash ??= SHA256.HashData(currentBytes);
                if (!currentHash.AsSpan().SequenceEqual(derivedSourceHash))
                    throw new MasterDataConflictException(Path.GetFileName(_path));
                RestoreDeclaredOmittedRows(items, current);
                ValidateDomain(items);
            }
            else if (expectedSnapshot == null && _preserveWhenOmitted != null && loadedState == null && items.Count == 0 && current.Any(x => _preserveWhenOmitted(x)))
            {
                // Caso limite di una UI che filtra TUTTE le righe (per esempio catalogo composto solo da
                // elementi archiviati): non esistono entita' rimaste nella lista derivata da cui recuperare il
                // token. Preserviamo comunque le sole righe dichiarate come nascoste. Una cancellazione reale
                // passa dagli editor master con expectedSnapshot esplicito e non usa questo percorso legacy.
                RestoreDeclaredOmittedRows(items, current);
                ValidateDomain(items);
            }

            if (expectedSnapshot != null && !CanonicalEquals(current, expectedSnapshot))
                throw new MasterDataConflictException(Path.GetFileName(_path));

            try { WritePreviousSnapshotDurable(currentBytes); }
            catch (Exception ex)
            {
                throw new IOException($"Impossibile aggiornare la copia di sicurezza di '{Path.GetFileName(_path)}'; salvataggio annullato.", ex);
            }
        }
        else if (expectedSnapshot != null && expectedSnapshot.Count > 0)
        {
            // Una rimozione remota del file dopo l'apertura non deve essere interpretata come autorizzazione a
            // ricrearlo con una fotografia vecchia. Lo snapshot non vuoto dimostra che il caller lo aveva letto.
            throw new MasterDataConflictException(Path.GetFileName(_path));
        }

        var createOnly = loadedState?.FileExisted == false || guardedConstructedFirstWrite;
        try
        {
            // Se Load() aveva osservato il file assente, oppure il repository stesso era nato quando mancava,
            // CreateNew/overwrite:false chiude la finestra TOCTOU sullo stesso filesystem. Su repliche OneDrive
            // fisicamente separate resta necessaria la riconciliazione cloud/eventuale.
            JsonIo.WriteAtomic(_path, items, overwrite: !createOnly);
            if (guardedConstructedFirstWrite)
                System.Threading.Volatile.Write(ref _successfulWriteSinceConstruction, 1);
        }
        catch (IOException) when (createOnly && File.Exists(_path))
        {
            if (guardedConstructedFirstWrite)
            {
                // Il file può essere comparso tra il controllo sopra e il CreateNew. Stesso contenuto = l'altro
                // writer ha già completato esattamente il nostro seed, quindi successo idempotente senza rewrite.
                var current = ParseCurrentForWrite(ReadCurrentBytesForWrite());
                if (CanonicalEquals(current, items))
                {
                    System.Threading.Volatile.Write(ref _successfulWriteSinceConstruction, 1);
                    lock (_gate) _cache = null;
                    return;
                }
            }
            throw new MasterDataConflictException(Path.GetFileName(_path));
        }
        finally
        {
            // Dopo un Save la stessa List/nonche' i suoi oggetti non conservano automaticamente un nuovo token:
            // tra File.Move e una eventuale rilettura la replica cloud potrebbe gia' cambiare. Per un secondo
            // ciclo di modifica il caller deve fare Load() (oppure usare l'expectedSnapshot esplicito), evitando
            // falsa sicurezza su una fotografia ormai superata.
            _loadedLists.Remove(items);
            foreach (var item in items) _loadedEntities.Remove(item);
        }

        lock (_gate) _cache = null;
        DiagnosticTraceService.Registra("MasterData.Save", $"{typeof(T).Name} righe={items.Count} (legacy)");
    }

    /// <summary>
    /// Three-way ristretto al percorso legacy di una lista filtrata proveniente da Load(). Parte SEMPRE dalla
    /// fotografia remota corrente e sostituisce una riga remota con quella locale solo se la stessa riga remota
    /// è ancora identica alla fotografia canonica di apertura. Modifiche remote ad altre righe e nuove righe
    /// remote vengono quindi conservate; se locale e remoto hanno toccato la stessa chiave in modo diverso il
    /// merge è rifiutato. Le omissioni della lista filtrata non sono mai interpretate come delete.
    /// </summary>
    private void SaveDerivedLegacyThreeWay(List<T> localItems)
    {
        if (_preserveOmittedKeyOf == null)
            throw new MasterDataConflictException(Path.GetFileName(_path));

        EnsureWritable();
        ValidateDomain(localItems);

        var currentBytes = ReadCurrentBytesForWrite();
        var remoteCurrent = ParseCurrentForWrite(currentBytes);
        var remoteIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < remoteCurrent.Count; i++)
        {
            var key = NormalizeKey(_preserveOmittedKeyOf(remoteCurrent[i]));
            if (string.IsNullOrWhiteSpace(key) || !remoteIndex.TryAdd(key, i))
                throw new InvalidDataException($"Chiave master vuota o duplicata '{key}' nella replica remota; merge annullato.");
        }

        foreach (var local in localItems)
        {
            var localKey = NormalizeKey(_preserveOmittedKeyOf(local));
            if (string.IsNullOrWhiteSpace(localKey))
                throw new InvalidDataException("Chiave master locale vuota; merge annullato.");

            if (_loadedEntities.TryGetValue(local, out var loaded))
            {
                // Il rename di una chiave business non è un edit per-entità sicuro nel percorso JSON legacy.
                if (!string.Equals(localKey, loaded.OriginalKey, StringComparison.OrdinalIgnoreCase))
                    throw new MasterDataConflictException(Path.GetFileName(_path));

                var baseline = JsonIo.TryParse<T>(loaded.SnapshotBytes, out var baselineError)
                    ?? throw new MasterDataReadException(_path,
                        $"Snapshot per-entita' non decodificabile; merge annullato: {baselineError}");
                if (!remoteIndex.TryGetValue(loaded.OriginalKey, out var remotePosition))
                    throw new MasterDataConflictException(Path.GetFileName(_path));

                var remote = remoteCurrent[remotePosition];
                var localChanged = !CanonicalItemEquals(local, baseline);
                var remoteChanged = !CanonicalItemEquals(remote, baseline);

                if (!localChanged) continue;
                if (remoteChanged && !CanonicalItemEquals(local, remote))
                    throw new MasterDataConflictException(Path.GetFileName(_path));

                remoteCurrent[remotePosition] = local;
                continue;
            }

            if (remoteIndex.TryGetValue(localKey, out var existingPosition))
            {
                if (!CanonicalItemEquals(remoteCurrent[existingPosition], local))
                    throw new MasterDataConflictException(Path.GetFileName(_path));
                continue;
            }

            remoteIndex.Add(localKey, remoteCurrent.Count);
            remoteCurrent.Add(local);
        }

        ValidateDomain(remoteCurrent);
        var remoteSnapshot = ParseCurrentForWrite(currentBytes);
        Save(remoteCurrent, remoteSnapshot);
    }

    /// <summary>
    /// Risolve in modo automatico esclusivamente un conflitto composto da aggiunte pure locali nel percorso
    /// legacy. In Phase D la stessa API viene tradotta in un normale diff per UID contro baseSnapshot.
    /// </summary>
    public List<T> SaveAdditionsPreservingRemote(
        IReadOnlyList<T> localItems,
        IReadOnlyList<T> baseSnapshot,
        Func<T, string> keyOf,
        StringComparer? keyComparer = null)
    {
        ArgumentNullException.ThrowIfNull(localItems);
        ArgumentNullException.ThrowIfNull(baseSnapshot);
        ArgumentNullException.ThrowIfNull(keyOf);

        if (TryGetPhaseDEngine(out var phaseD))
        {
            var mutable = localItems.ToList();
            SavePhaseD(phaseD, mutable, baseSnapshot);
            return phaseD.Load().Items;
        }

        keyComparer ??= StringComparer.OrdinalIgnoreCase;
        EnsureWritable();
        ValidateDomain(localItems);

        var baseByKey = BuildUniqueKeyMap(baseSnapshot, keyOf, keyComparer, "snapshot di apertura");
        var localByKey = BuildUniqueKeyMap(localItems, keyOf, keyComparer, "modifiche locali");

        foreach (var (key, baseItem) in baseByKey)
        {
            if (!localByKey.TryGetValue(key, out var localItem) || !CanonicalItemEquals(localItem, baseItem))
                throw new MasterDataConflictException(Path.GetFileName(_path));
        }

        var localAdditions = localItems
            .Where(item => !baseByKey.ContainsKey(NormalizeKey(keyOf(item))))
            .ToList();

        if (localAdditions.Count == 0)
            throw new MasterDataConflictException(Path.GetFileName(_path));

        var currentBytes = ReadCurrentBytesForWrite();
        var remoteCurrent = ParseCurrentForWrite(currentBytes);
        var remoteByKey = BuildUniqueKeyMap(remoteCurrent, keyOf, keyComparer, "replica remota");

        foreach (var added in localAdditions)
        {
            var key = NormalizeKey(keyOf(added));
            if (remoteByKey.TryGetValue(key, out var remoteExisting))
            {
                if (!CanonicalItemEquals(remoteExisting, added))
                    throw new MasterDataConflictException(Path.GetFileName(_path));
                continue;
            }

            remoteCurrent.Add(added);
            remoteByKey.Add(key, added);
        }

        ValidateDomain(remoteCurrent);
        var remoteCurrentBase = ParseCurrentForWrite(currentBytes);
        Save(remoteCurrent, remoteCurrentBase);
        return remoteCurrent;
    }

    public List<T>? LoadPreviousVerified()
    {
        if (TryGetPhaseDEngine(out _))
            return null; // Phase D recupera dal journal/checkpoint; .prev appartiene solo al monolite legacy.

        var previousPath = _path + ".prev";
        if (!File.Exists(previousPath)) return null;
        try
        {
            var bytes = JsonIo.ReadAllBytesShared(previousPath);
            return JsonIo.TryParse<List<T>>(bytes, out var error)
                ?? throw new MasterDataReadException(previousPath, $"La copia di sicurezza master non e valida: {error}");
        }
        catch (MasterDataReadException) { throw; }
        catch (Exception ex)
        {
            throw new MasterDataReadException(previousPath, "La copia di sicurezza master non e leggibile.", ex);
        }
    }

    private List<T> TrackLoadedList(List<T> list, bool fileExisted, byte[]? sourceBytes)
    {
        var sha = sourceBytes == null ? null : SHA256.HashData(sourceBytes);
        _loadedLists.Remove(list);
        _loadedLists.Add(list, new LoadedListState
        {
            FileExisted = fileExisted,
            Sha256 = sha,
            SourceBytes = _preserveOmittedKeyOf != null && sourceBytes != null ? sourceBytes.ToArray() : null
        });

        if (sha != null && _preserveWhenOmitted != null && _preserveOmittedKeyOf != null)
        {
            foreach (var item in list)
            {
                var originalKey = NormalizeKey(_preserveOmittedKeyOf(item));
                if (string.IsNullOrWhiteSpace(originalKey)) continue;
                _loadedEntities.Remove(item);
                _loadedEntities.Add(item, new LoadedEntityState
                {
                    Sha256 = sha,
                    OriginalKey = originalKey,
                    SnapshotBytes = JsonSerializer.SerializeToUtf8Bytes(item, JsonIo.Options)
                });
            }
        }
        return list;
    }

    private LoadedListState? TryGetLoadedState(List<T> items) =>
        _loadedLists.TryGetValue(items, out var state) ? state : null;

    private byte[]? TryGetDerivedSourceHash(List<T> items)
    {
        if (_preserveWhenOmitted == null || items.Count == 0) return null;
        byte[]? sourceHash = null;
        foreach (var item in items)
        {
            if (!_loadedEntities.TryGetValue(item, out var state)) continue;
            if (sourceHash == null) sourceHash = state.Sha256;
            else if (!sourceHash.AsSpan().SequenceEqual(state.Sha256)) return null;
        }
        return sourceHash;
    }

    private void RestoreDeclaredOmittedRows(List<T> localItems, IReadOnlyList<T> current)
    {
        if (_preserveOmittedKeyOf == null || _preserveWhenOmitted == null) return;

        var localKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in localItems)
        {
            var key = NormalizeKey(_preserveOmittedKeyOf(item));
            if (!string.IsNullOrWhiteSpace(key)) localKeys.Add(key);
        }

        foreach (var item in current)
        {
            if (!_preserveWhenOmitted(item)) continue;
            var key = NormalizeKey(_preserveOmittedKeyOf(item));
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidDataException("Chiave master vuota in una riga omessa da preservare; salvataggio annullato.");
            if (localKeys.Add(key)) localItems.Add(item);
        }
    }

    private void EnsureWritable()
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(_path))
            ?? throw new IOException("Impossibile determinare la radice del workspace master data.");
        WorkspaceWritePolicy.EnsureCanWrite(new DataPaths(root), $"Salvataggio {Path.GetFileName(_path)}");
    }

    private byte[] ReadCurrentBytesForWrite()
    {
        try { return JsonIo.ReadAllBytesShared(_path); }
        catch (Exception ex)
        {
            throw new MasterDataReadException(_path, "Il file master corrente non e leggibile; salvataggio annullato.", ex);
        }
    }

    private List<T> ParseCurrentForWrite(byte[] currentBytes) =>
        JsonIo.TryParse<List<T>>(currentBytes, out var error)
        ?? throw new MasterDataReadException(_path, $"Il file master corrente non e valido e non verra sovrascritto: {error}");

    private static Dictionary<string, T> BuildUniqueKeyMap(
        IEnumerable<T> items,
        Func<T, string> keyOf,
        StringComparer comparer,
        string source)
    {
        var map = new Dictionary<string, T>(comparer);
        foreach (var item in items)
        {
            var key = NormalizeKey(keyOf(item));
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidDataException($"Chiave master vuota in {source}; riconciliazione automatica annullata.");
            if (!map.TryAdd(key, item))
                throw new InvalidDataException($"Chiave master duplicata '{key}' in {source}; riconciliazione automatica annullata.");
        }
        return map;
    }

    private static string NormalizeKey(string? key) => key?.Trim() ?? string.Empty;

    private void WritePreviousSnapshotDurable(ReadOnlySpan<byte> currentBytes)
    {
        var previousPath = _path + ".prev";
        var dir = Path.GetDirectoryName(previousPath)!;
        Directory.CreateDirectory(dir);
        var temp = previousPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                       FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                stream.Write(currentBytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, previousPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static bool CanonicalEquals(IReadOnlyList<T> left, IReadOnlyList<T> right)
    {
        var a = JsonSerializer.SerializeToUtf8Bytes(left, JsonIo.Options);
        var b = JsonSerializer.SerializeToUtf8Bytes(right, JsonIo.Options);
        return a.AsSpan().SequenceEqual(b);
    }

    private static bool CanonicalItemEquals(T left, T right)
    {
        var a = JsonSerializer.SerializeToUtf8Bytes(left, JsonIo.Options);
        var b = JsonSerializer.SerializeToUtf8Bytes(right, JsonIo.Options);
        return a.AsSpan().SequenceEqual(b);
    }

    private static void ValidateDomain(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            // 6.5.0: sottoveicoli/componente/guasto di un Extra sono FACOLTATIVI (la regola "almeno un sottoveicolo" e' stata
            // rimossa su decisione utente): senza sottoveicoli il Extra vale per tutti i sottoveicoli.
        }
    }
}

public sealed class MasterDataReadException : IOException
{
    public string PathMaster { get; }

    public MasterDataReadException(string path, string message, Exception? inner = null)
        : base($"{message} File: {Path.GetFileName(path)}", inner)
    {
        PathMaster = path;
    }
}

public class MasterDataConflictException : IOException
{
    public MasterDataConflictException(string fileName)
        : base($"'{fileName}' e stato modificato da un'altra postazione dopo l'apertura. Nessun dato e stato sovrascritto: la riconciliazione deve essere gestita da un Master.") { }

    /// <summary>Marker per distinguere il costruttore "messaggio gia' pronto" (sottoclassi) da quello
    /// "fileName" sopra, che invece compone sempre lo stesso messaggio generico "altra postazione".</summary>
    protected enum MessaggioPreformattato { Si }

    /// <summary>Costruttore per le sottoclassi che devono sostituire il messaggio generico "altra postazione"
    /// con una diagnosi piu' precisa, restando comunque un MasterDataConflictException affinche' tutti i catch
    /// fail-closed esistenti (MasterDataRepository.Save, MasterDataEditorViewModelBase, RevisioneUdtService)
    /// continuino a intercettarla senza modifiche.</summary>
    protected MasterDataConflictException(string message, MessaggioPreformattato _) : base(message) { }
}

/// <summary>
/// Sottoclasse di MasterDataConflictException per il caso in cui SavePatch/Save rilevano che la testa remota
/// candidata al confronto ha un payload storico non piu' deserializzabile nel modello corrente (dato
/// danneggiato, oppure scritto da uno schema incompatibile). Il messaggio generico "e' stato modificato da
/// un'altra postazione" e' fuorviante qui: non si tratta di una modifica concorrente rilevata per contenuto
/// diverso, ma dell'impossibilita' di leggere affatto il payload storico. Resta comunque fail-closed: nessuna
/// mutazione viene scritta finche' la causa non e' chiarita.
/// </summary>
public sealed class MasterDataUnreadableHistoricalPayloadException : MasterDataConflictException
{
    public MasterDataUnreadableHistoricalPayloadException(string catalog, Guid uid, string? entityLabel)
        : base(BuildMessage(catalog, uid, entityLabel), MessaggioPreformattato.Si) { }

    private static string BuildMessage(string catalog, Guid uid, string? entityLabel)
    {
        var voce = string.IsNullOrWhiteSpace(entityLabel) ? uid.ToString("N") : $"{entityLabel} ({uid:N})";
        return $"La voce '{voce}' del catalogo '{catalog}' ha nel journal condiviso una versione che questa " +
            "build non riesce a leggere (dato danneggiato oppure scritto da una versione piu' recente di " +
            "TestLoom). Nessun dato e stato sovrascritto. Come sbloccare: aggiornare TestLoom all'ultima versione su " +
            "tutte le postazioni e riprovare; se il problema persiste, contattare il supporto indicando il " +
            $"catalogo '{catalog}' e l'Uid {uid:N}.";
    }
}

public class MasterDataStore
{
    public MasterDataRepository<Requisito> Requisiti { get; }
    public MasterDataRepository<Funzionalita> Funzionalita { get; }
    public MasterDataRepository<Componente> Componente { get; }
    public MasterDataRepository<Gamma> Gamma { get; }
    public MasterDataRepository<Categoria> Categorie { get; }
    public MasterDataRepository<Veicolo> Veicoli { get; }
    public MasterDataRepository<StrumentoDiagnostico> Strumenti { get; }
    public MasterDataRepository<Operatore> Operatori { get; }
    public MasterDataRepository<TestTemplate> TestTemplate { get; }
    public MasterDataRepository<CampoSistemaDefinizione> CampiSistemaPianificazione { get; }
    public MasterDataRepository<CampoSistemaDefinizione> CampiSistemaDettagli { get; }
    public MasterDataRepository<RisoluzioneConflittoAudit> RisoluzioniConflittoAudit { get; }
    public MasterDataRepository<RevisioneUdt> RevisioniUdt { get; }
    public MasterDataRepository<ValutazioneRequisitoEcuVeicolo> ValutazioniRequisitoEcuVeicolo { get; }
    public MasterDataRepository<StatoFunzionalitaEcuVeicolo> StatiFunzionalitaEcuVeicolo { get; }
    /// <summary>Archivio separato dei guasti dei requisiti Extra (un documento per requisito nel journal Phase D).</summary>
    public RequisitoDtcArchivio RequisitiDtc { get; }

    public MasterDataStore(DataPaths paths)
    {
        Requisiti = new MasterDataRepository<Requisito>(paths.RequisitiFile);
        Funzionalita = new MasterDataRepository<Funzionalita>(paths.FunzionalitaFile);
        Componente = new MasterDataRepository<Componente>(paths.EcuFile);
        Gamma = new MasterDataRepository<Gamma>(paths.GammaFile);
        Categorie = new MasterDataRepository<Categoria>(paths.CategorieFile);
        Veicoli = new MasterDataRepository<Veicolo>(paths.VeicoliFile);
        // Il Test editor lavora storicamente su una Where(!Archiviata).ToList(): senza una regola esplicita
        // il successivo Save trasformerebbe il filtro UI in cancellazione delle righe archiviate. In Phase D
        // il predicato resta utile per distinguere omissione filtrata da delete intenzionale.
        Strumenti = new MasterDataRepository<StrumentoDiagnostico>(paths.StrumentiFile, s => s.Nome, s => s.Archiviata);
        Operatori = new MasterDataRepository<Operatore>(paths.OperatoriFile);
        TestTemplate = new MasterDataRepository<TestTemplate>(paths.TestTemplateFile);
        CampiSistemaPianificazione = new MasterDataRepository<CampoSistemaDefinizione>(paths.CampiSistemaPianificazioneFile);
        CampiSistemaDettagli = new MasterDataRepository<CampoSistemaDefinizione>(paths.CampiSistemaDettagliFile);
        RisoluzioniConflittoAudit = new MasterDataRepository<RisoluzioneConflittoAudit>(paths.RisoluzioniConflittoAuditFile);
        RevisioniUdt = new MasterDataRepository<RevisioneUdt>(paths.RevisioniUdtFile);
        ValutazioniRequisitoEcuVeicolo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(paths.ValutazioniRequisitoEcuVeicoloFile);
        StatiFunzionalitaEcuVeicolo = new MasterDataRepository<StatoFunzionalitaEcuVeicolo>(paths.StatiFunzionalitaEcuVeicoloFile);
        RequisitiDtc = new RequisitoDtcArchivio(paths);
    }
}
