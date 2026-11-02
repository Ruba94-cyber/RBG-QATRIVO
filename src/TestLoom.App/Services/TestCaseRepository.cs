using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Repository dei test case. I JSON nella cartella condivisa restano la fonte autorevole; l'indice SQLite
/// locale è una cache/read-model eliminabile che consente warm-start senza dover enumerare migliaia di file
/// OneDrive prima di mostrare i dati. La riconciliazione completa continua a verificare i JSON.
/// </summary>
public class TestCaseRepository
{
    private readonly DataPaths _paths;
    private readonly LocalTestIndexStore? _localIndex;
    private readonly LocalOperationalStatisticsService? _operationalStatistics;
    private readonly SharedTestJournalService? _journal;
    private readonly string? _journalUnavailableReason;

    /// <summary>Protegge indice e viste materializzate mentre refresh in background e salvataggi possono
    /// concorrere. È un Monitor rientrante: Save può chiamare IdExists/PathFor.</summary>
    private readonly object _gate = new();

    /// <summary>Serializza full reconciliation e refresh incrementali concorrenti. Il lock _gate non può
    /// coprire I/O OneDrive perché bloccherebbe la UI.</summary>
    private readonly object _refreshGate = new();

    private Dictionary<string, (DateTime Mtime, TestCase TestCase)> _cache = new();
    private Dictionary<string, long> _fileLengths = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, TestCase> _byId = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<Guid, string> _pathByUid = new();
    private Dictionary<string, string> _pathById = new(StringComparer.OrdinalIgnoreCase);
    private List<FileConProblemi> _fileConProblemi = new();
    /// <summary>Tolleranza sugli errori di lettura JSON transitori (multi-PC 6.4.2): un file entra
    /// nell'avviso utente solo se resta illeggibile oltre la soglia, vedi FileProblemToleranceTracker.</summary>
    private readonly FileProblemToleranceTracker _tolleranzaProblemi = new();
    private List<TestCaseDuplicato> _duplicatiId = new();
    /// <summary>FASE 3 (multi-PC 6.4.2): copie di conflitto OneDrive/SharePoint riconosciute e già escluse da
    /// _cache/_duplicatiId, vedi ClassificaDuplicatiEEscludiCopieDiConflitto.</summary>
    private List<FileConProblemi> _copieDiConflitto = new();
    private IReadOnlyList<TestCase>? _tutti;
    private IReadOnlyList<TestCase>? _attivi;
    private IReadOnlyList<TestCase>? _attiviOrdinatiPerId;

    /// <summary>Pulizia opportunistica dei claim ID scaduti (P2.8): al più un avvio alla volta e una volta al giorno per PC.
    /// <see cref="_ultimaPuliziaClaimUtc"/> è letto e scritto solo sotto <see cref="_gate"/>.</summary>
    private int _puliziaClaimInCorso;
    private DateTime? _ultimaPuliziaClaimUtc;
    private Task? _puliziaClaimTask;

    /// <summary>Ultima pulizia dei claim avviata da questa istanza: serve ai test di integrazione per attenderne la fine.</summary>
    internal Task? PuliziaClaimTask
    {
        get { lock (_gate) return _puliziaClaimTask; }
    }

    /// <summary>La fotografia locale e' stata caricata o riconciliata almeno una volta in questa sessione.</summary>
    private bool _snapshotCaricato;

    private readonly LocalIndexRecoveryCoordinator? _indexRecovery;

    /// <summary>
    /// Vero solo quando esiste una fotografia locale E il coordinatore la considera ancora attendibile.
    /// Prima era un booleano che, una volta messo a true, restava true anche dopo un ReconcileFull/ApplyBatch
    /// fallito: le pagine continuavano a leggere un indice OBSOLETO credendolo valido.
    /// </summary>
    public bool HasPersistentSnapshot => _snapshotCaricato && _indexRecovery?.IsUsable == true;

    /// <summary>Coordinatore condiviso da reader, repository, refresh e manutenzione di questo workspace.</summary>
    public LocalIndexRecoveryCoordinator? IndexRecovery => _indexRecovery;

    /// <summary>
    /// Generazione corrente dell'indice. Cambia a ogni ricostruzione riuscita: i reader che la osservano
    /// sanno di dover buttare via connessioni e cache dipendenti e ripartire dal nuovo file.
    /// </summary>
    public long LocalIndexGeneration => _indexRecovery?.Generazione ?? 0;

    public string? LocalIndexDatabasePath => _localIndex?.DatabasePath;

    /// <summary>TIME (6.6.1): durata da proporre per una nuova operazione (regola di copia, vedi LocalTestIndexStore.TrovaDurataCopia).
    /// Senza indice locale restituisce null: il valore resta vuoto, mai inventato.</summary>
    public int? TrovaDurataCopia(IReadOnlyCollection<string> componente, string? funzionalita, IReadOnlyCollection<string> veicoli, int? passoDtc) =>
        _localIndex?.TrovaDurataCopia(componente, funzionalita, veicoli, passoDtc);
    public string? LocalWorkspaceDirectory => _localIndex?.WorkspaceDirectory;
    public DateTime? LastFullReconciliationUtc => _localIndex?.LastFullScanUtc;
    public LocalOperationalStatisticsService? OperationalStatistics => _operationalStatistics;
    public IReadOnlyList<JournalForkConflict> JournalForks => _journal?.KnownForks ?? Array.Empty<JournalForkConflict>();
    public bool WritesProtectedByJournal => _journal != null;

    /// <summary>DeviceId di QUESTA postazione secondo il journal locale, o null se il journal non è disponibile.
    /// Usato dal merge automatico dei fork (6.4.1, punto B) per la regola deterministica "DeviceId minore".</summary>
    public string? LocalDeviceId => _journal?.DeviceId;

    /// <summary>Base salvata localmente (journal.db di questo PC, mai nella cartella condivisa) da un Save
    /// precedente con lo stesso hash. Vedi <see cref="SharedTestJournalService.PersistLocalBase"/>.</summary>
    public bool TryGetLocalBase(Guid entityUid, string baseHash, out TestCase? baseVersion)
    {
        if (_journal == null) { baseVersion = null; return false; }
        return _journal.TryGetLocalBase(entityUid, baseHash, out baseVersion);
    }

    /// <param name="deviceIdOverride">Solo per i test: forza il DeviceId del journal locale di questa istanza
    /// invece di quello reale della macchina (<see cref="DeviceIdentityService"/>, condiviso da tutto il
    /// processo). Serve a simulare due postazioni distinte nello stesso processo di test — vedi
    /// <see cref="SharedTestJournalService"/>, che già supporta lo stesso override.</param>
    public TestCaseRepository(DataPaths paths, LocalTestIndexStore? localIndex = null, string? deviceIdOverride = null)
    {
        _paths = paths;
        LocalTestIndexStore? store = null;
        string? journalUnavailableReason = null;
        try
        {
            store = localIndex ?? new LocalTestIndexStore(paths.Root);
            if (store.HasCompletedFullScan)
            {
                // Marcatore letto PRIMA delle righe: hash riusabili solo se prodotti dall'algoritmo attuale.
                var riusabili = store.HashIndicizzatiRiusabili;
                var local = store.LoadAll();
                foreach (var item in local)
                {
                    // Identita' del file = (path, mtime, lunghezza) della stessa riga: BindUpsert scrive questi campi e l'hash
                    // in un'unica istruzione, quindi l'hash memorizzato descrive esattamente quel contenuto e si riusa senza
                    // due serializzazioni per Test. Una modifica del file passa dal refresh incrementale o dalla riconciliazione
                    // completa, che rileggono il JSON e riscrivono riga e hash. Hash assenti o malformati: ricalcolo.
                    if (riusabili && TestCaseFingerprint.IsWellFormed(item.ContentHash))
                    {
                        TestCaseFingerprint.SetStored(item.TestCase, item.ContentHash);
                        PerfContatori.IncrementaFingerprintRiusato();
                    }
                    else TestCaseFingerprint.Stamp(item.TestCase);
                    _cache[item.Path] = (item.MtimeUtc, item.TestCase);
                    _fileLengths[item.Path] = item.FileLength;
                }
                ClassificaDuplicatiEEscludiCopieDiConflitto(_cache, _fileLengths, out _duplicatiId, out _copieDiConflitto, CopiaAssorbita);
                RicostruisciLookup();
                _snapshotCaricato = true;
            }
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Indice SQLite locale TestCase: bootstrap", ex);
            journalUnavailableReason = $"Indice locale non disponibile: {ex.Message}";
            store = null;
            _snapshotCaricato = false;
        }
        _localIndex = store;

        if (store != null)
        {
            // Unico coordinatore per questo workspace/database: reader scalabili, Save, refresh incrementale e
            // riconciliazione completa passano tutti di qui, cosi' dieci accessi concorrenti in errore
            // producono una sola ricostruzione invece di dieci.
            _indexRecovery = new LocalIndexRecoveryCoordinator(
                store.DatabasePath,
                RicostruisciIndiceDaSorgenteCondivisa,
                "index.db (TestCase)");
        }

        if (store != null)
        {
            try { _operationalStatistics = new LocalOperationalStatisticsService(store.DatabasePath); }
            catch (Exception ex) { CrashLogService.Registra("Statistiche SQLite locali TestCase", ex); }
            try
            {
                _journal = new SharedTestJournalService(paths, store.WorkspaceDirectory, deviceIdOverride);
            }
            catch (Exception ex)
            {
                journalUnavailableReason = $"Journal locale non disponibile: {ex.Message}";
                CrashLogService.Registra("Journal TestCase: bootstrap locale", ex);
            }
        }
        else journalUnavailableReason ??= "Indice locale non disponibile; journal di sicurezza non inizializzato.";
        _journalUnavailableReason = journalUnavailableReason;
    }

    public IReadOnlyList<FileConProblemi> FileConProblemi { get { lock (_gate) return _fileConProblemi; } }

    /// <summary>
    /// Path attualmente illeggibili E in tolleranza (non ancora in <see cref="FileConProblemi"/>, quindi
    /// invisibili all'utente) insieme a quelli già visibili. Multi-PC (6.4.2): il drain incrementale guidato
    /// dal watcher (AppState.RileggiTestCases) deve ripianificare un nuovo tentativo di lettura per QUESTI
    /// path a ogni giro periodico, non solo per quelli già visibili — altrimenti un file ancora in tolleranza
    /// (1° errore, <2 min) per cui non arriva un secondo evento watcher resterebbe last-known-good per sempre,
    /// senza mai raggiungere la soglia che lo farebbe comparire (o sparire) nell'avviso.
    /// </summary>
    public IReadOnlyCollection<string> PercorsiConProblemiTracciati => _tolleranzaProblemi.PercorsiTracciati();
    public IReadOnlyList<TestCaseDuplicato> DuplicatiId { get { lock (_gate) return _duplicatiId; } }

    /// <summary>
    /// FASE 3 (multi-PC 6.4.2): copie di conflitto OneDrive/SharePoint — un file che condivide l'Id con un
    /// altro file dal nome canonico, ma il cui PROPRIO nome non è quello canonico per quell'Id (es.
    /// "testcase_T-0001-NOMEPC.json", "testcase_T-0001 (1).json" accanto a "testcase_T-0001.json"). Area
    /// diagnostica separata da FileConProblemi (non è un errore, è ignorabile) e da DuplicatiId (non è un
    /// vero ID duplicato ambiguo: sappiamo già qual è l'originale). Il file resta sempre intatto sul disco.
    /// </summary>
    public IReadOnlyList<FileConProblemi> CopieDiConflitto { get { lock (_gate) return _copieDiConflitto; } }

    public bool NeedsFullReconciliation(TimeSpan maxAge)
    {
        if (!HasPersistentSnapshot) return true;
        var last = LastFullReconciliationUtc;
        return !last.HasValue || DateTime.UtcNow - last.Value.ToUniversalTime() >= maxAge;
    }

    /// <summary>Flush outbox + rilevamento fork. In sola lettura il refresh del journal non deve pubblicare
    /// alcun artefatto condiviso: le letture JSON restano disponibili, la sincronizzazione tecnica riprenderà
    /// quando il workspace tornerà Operativo.</summary>
    public void RefreshJournalMetadata()
    {
        if (_journal == null || !WorkspaceWritePolicy.CanWrite(_paths)) return;
        try
        {
            _journal.FlushOutbox();
            _journal.RefreshKnownForks();
        }
        catch (Exception ex) { CrashLogService.Registra("Journal TestCase: background refresh", ex); }
    }

    /// <param name="onProgress">Callback opzionale (elaborati, totale) invocato durante la rilettura parallela
    /// dei JSON quando questa e' una riconciliazione completa (bootstrap su indice locale assente/scaduto): il
    /// totale è noto solo dopo l'enumerazione dei file, quindi la prima chiamata arriva con elaborati=0. Non
    /// altera in alcun modo l'esito o la logica di riconciliazione: e' pura osservazione a scopo di UI.</param>
    public void RefreshIndex(Action<int, int>? onProgress = null) => RefreshIndexCore(ricostruzione: false, onProgress);

    /// <summary>
    /// Ricostruzione completa dell'indice locale dai JSON condivisi. Invocata SOLO dal coordinatore, dopo che
    /// i file guasti sono stati messi in quarantena: per questo reinizializza lo schema prima di riconciliare.
    /// Se la sorgente condivisa non e' integralmente verificabile l'esito e' Incompleta e l'indice non viene
    /// marcato pronto: meglio restare sul percorso lento che pubblicare un archivio amputato.
    /// </summary>
    private LocalIndexRebuildOutcome RicostruisciIndiceDaSorgenteCondivisa()
    {
        if (_localIndex == null) return LocalIndexRebuildOutcome.Incompleta("Nessun indice locale disponibile.");
        _localIndex.ReinizializzaDopoQuarantena();
        return RefreshIndexCore(ricostruzione: true);
    }

    private LocalIndexRebuildOutcome RefreshIndexCore(bool ricostruzione, Action<int, int>? onProgress = null)
    {
        lock (_refreshGate)
        {
            Dictionary<string, (DateTime Mtime, TestCase TestCase)> fotografia;
            Dictionary<string, long> fotografiaLunghezze;
            Dictionary<string, string> fotografiaHash;
            lock (_gate)
            {
                fotografia = new(_cache);
                fotografiaLunghezze = new(_fileLengths, StringComparer.OrdinalIgnoreCase);
                fotografiaHash = _cache.ToDictionary(
                    kv => kv.Key,
                    kv => TestCaseFingerprint.GetStored(kv.Value.TestCase) ?? TestCaseFingerprint.Compute(kv.Value.TestCase),
                    StringComparer.OrdinalIgnoreCase);
            }

            var nuovaCache = new Dictionary<string, (DateTime Mtime, TestCase TestCase)>(fotografia);
            var nuoveLunghezze = new Dictionary<string, long>(fotografiaLunghezze, StringComparer.OrdinalIgnoreCase);
            var problemi = new List<FileConProblemi>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var daRileggere = new List<(string Path, DateTime Mtime, long Length)>();

            // Path enumerati sulla cartella condivisa per i quali, a fine giro, l'indice non ha NESSUNA
            // versione (ne' una lettura fresca, ne' una last-known-good). Sono la prova che la fotografia
            // sarebbe parziale: tipico di una consegna OneDrive ancora in corso.
            var pathNonRisolti = new List<string>();
            string? sorgenteIncompleta = null;

            // La cartella dei Test deve esistere: se non esiste (share non montato, OneDrive non pronto)
            // "zero file" non significa "archivio vuoto" e non puo' diventare un indice vuoto dichiarato completo.
            if (!Directory.Exists(_paths.TestCasesDir))
                sorgenteIncompleta = $"Cartella condivisa dei Test non raggiungibile: {_paths.TestCasesDir}";

            foreach (var dir in new[] { _paths.TestCasesDir, _paths.CestinoDir })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in new DirectoryInfo(dir).EnumerateFiles("testcase_*.json", SearchOption.AllDirectories))
                {
                    var path = file.FullName;
                    seenPaths.Add(path);
                    var mtime = file.LastWriteTimeUtc;
                    var length = file.Length;
                    nuoveLunghezze[path] = length;

                    // Questa è la deep/full reconciliation (startup, watcher recovery e almeno ogni 6 ore):
                    // mtime+length non sono una prova di contenuto su OneDrive. Rileggiamo quindi ogni JSON.
                    // Il percorso veloce minuto-per-minuto resta RefreshPaths(), guidato da watcher/change-feed.
                    daRileggere.Add((path, mtime, length));
                }
            }

            foreach (var stale in nuovaCache.Keys.Where(p => !seenPaths.Contains(p)).ToList())
            {
                nuovaCache.Remove(stale);
                nuoveLunghezze.Remove(stale);
            }

            if (daRileggere.Count > 0)
            {
                // Il totale reale è noto solo ora, dopo l'enumerazione: la prima chiamata (0, totale) annuncia
                // alla UI quanti elementi ci sono da leggere invece di lasciarla senza numeri fino alla fine.
                onProgress?.Invoke(0, daRileggere.Count);

                var risultati = new (TestCase? TestCase, string? Errore)[daRileggere.Count];
                var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) };
                var elaboratiCount = 0;
                var totaleDaRileggere = daRileggere.Count;
                // Non un report per file (a 100k file sarebbe rumore per il dispatcher UI): al massimo ~200
                // notifiche per l'intera passata, più sempre l'ultima quando fatti==totale.
                var reportOgni = Math.Max(1, totaleDaRileggere / 200);
                Parallel.For(0, daRileggere.Count, parallelOptions, i =>
                {
                    var letto = JsonIo.TryRead<TestCase>(daRileggere[i].Path, out var errore);
                    if (letto != null)
                    {
                        // Normalizzazione, migrazione in lettura e fingerprint dipendono solo dal singolo Test: si fanno qui,
                        // in parallelo (prima erano in sequenza nel ciclo di applicazione, circa il 15% della riconciliazione).
                        letto.NormalizzaVeicoli();
                        // Punto unico condiviso con LocalTestIndexStore (review 6.4.0, punto 1): editor/liste in
                        // memoria e proiezione SQLite devono interpretare allo stesso modo un Test legacy, mai
                        // riscritto su disco qui.
                        TestCaseLegacyMigrations.MigraInLettura(letto, daRileggere[i].Path);
                        TestCaseFingerprint.Stamp(letto);
                    }
                    risultati[i] = (letto, errore);
                    if (onProgress == null) return;
                    var fatti = Interlocked.Increment(ref elaboratiCount);
                    if (fatti % reportOgni == 0 || fatti == totaleDaRileggere) onProgress(fatti, totaleDaRileggere);
                });

                for (var i = 0; i < daRileggere.Count; i++)
                {
                    var (path, mtime, length) = daRileggere[i];
                    var (tc, errore) = risultati[i];
                    if (tc == null)
                    {
                        // Decisione 6.4.2 (docs/PIANO_6.4.2.md §1): un tombstone (_ditmDeleted) è sempre
                        // riconosciuto come tale, senza gate su ledger/receipt. Le vecchie riclassificazioni per
                        // tombstone "storicamente superato"/"da eliminazione definitiva"/"orfano" servivano solo
                        // a scavalcare un gate che qui non esiste più.
                        if (WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, path))
                        {
                            nuovaCache.Remove(path);
                            nuoveLunghezze.Remove(path);
                            continue;
                        }

                        // Il path è stato enumerato ed esiste ancora: lettura transitoria o corruzione reale non
                        // sono delete. Manteniamo la last-known-good e il vecchio fingerprint, così il giro
                        // successivo ritenta invece di considerare valido il file illeggibile.
                        if (fotografia.TryGetValue(path, out var previous))
                        {
                            nuovaCache[path] = previous;
                            if (fotografiaLunghezze.TryGetValue(path, out var previousLength))
                                nuoveLunghezze[path] = previousLength;
                            else
                                nuoveLunghezze.Remove(path);
                        }
                        else
                        {
                            nuovaCache.Remove(path);
                            nuoveLunghezze.Remove(path);
                            // Nessuna last-known-good: questo Test condiviso non sarebbe rappresentato
                            // nell'indice. La fotografia non puo' essere dichiarata completa.
                            pathNonRisolti.Add(path);
                        }
                        problemi.Add(new FileConProblemi
                        {
                            Percorso = path,
                            Errore = errore ?? "sconosciuto",
                            RilevatoIl = DateTime.Now
                        });
                        continue;
                    }
                    nuovaCache[path] = (mtime, tc);
                    nuoveLunghezze[path] = length;
                }
            }

            List<LocalIndexWriteEntry> snapshotPersistente;
            lock (_gate)
            {
                foreach (var kv in _cache)
                {
                    var currentHash = TestCaseFingerprint.GetStored(kv.Value.TestCase) ?? TestCaseFingerprint.Compute(kv.Value.TestCase);
                    if (!fotografia.TryGetValue(kv.Key, out var prima) ||
                        prima.Mtime != kv.Value.Mtime ||
                        !fotografiaHash.TryGetValue(kv.Key, out var openingHash) ||
                        !openingHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        // Un Save può completarsi mentre il full scan legge OneDrive. La versione corrente in
                        // memoria ha precedenza se è cambiata dopo la fotografia iniziale, anche con stesso mtime.
                        nuovaCache[kv.Key] = kv.Value;
                        if (_fileLengths.TryGetValue(kv.Key, out var length)) nuoveLunghezze[kv.Key] = length;
                    }
                }
                foreach (var eliminato in fotografia.Keys.Where(k => !_cache.ContainsKey(k)))
                {
                    nuovaCache.Remove(eliminato);
                    nuoveLunghezze.Remove(eliminato);
                }

                _fileConProblemi = _tolleranzaProblemi.AggiornaCompleto(problemi);
                // Muta nuovaCache/nuoveLunghezze in place (rimuove le copie di conflitto) PRIMA di assegnarle
                // ai campi, cosi' _cache non contiene mai una copia di conflitto come Test indirizzabile.
                ClassificaDuplicatiEEscludiCopieDiConflitto(nuovaCache, nuoveLunghezze, out _duplicatiId, out _copieDiConflitto, CopiaAssorbita);
                _cache = nuovaCache;
                _fileLengths = nuoveLunghezze;
                RicostruisciLookup();
                InvalidaViste();

                snapshotPersistente = _cache.Select(kv => new LocalIndexWriteEntry(
                    kv.Key,
                    kv.Value.Mtime,
                    _fileLengths.GetValueOrDefault(kv.Key),
                    kv.Value.TestCase,
                    TestCaseFingerprint.GetStored(kv.Value.TestCase) ?? TestCaseFingerprint.Compute(kv.Value.TestCase))).ToList();
            }

            if (pathNonRisolti.Count > 0)
            {
                sorgenteIncompleta ??= pathNonRisolti.Count == 1
                    ? $"Un JSON condiviso non e' leggibile e non ha una copia valida in cache: {Path.GetFileName(pathNonRisolti[0])}"
                    : $"{pathNonRisolti.Count} JSON condivisi non sono leggibili e non hanno una copia valida in cache";
            }

            if (_localIndex == null)
                return LocalIndexRebuildOutcome.Incompleta("Nessun indice locale disponibile.");

            try
            {
                // I payload letti vengono comunque riversati: sono cache utile. Ma "pronto" viene scritto solo
                // se la sorgente condivisa era integralmente verificabile.
                _localIndex.ReconcileFull(snapshotPersistente, DateTime.UtcNow,
                    markFullScanCompleted: sorgenteIncompleta == null);
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Indice SQLite locale TestCase: full reconciliation", ex);
                // Il fallimento NON puo' lasciare HasPersistentSnapshot a true: l'indice resta leggibile ma
                // obsoleto, e le pagine devono saperlo invece di continuare a fidarsene in silenzio.
                _indexRecovery?.InvalidaComeObsoleto(
                    $"Riconciliazione completa non riuscita: {LocalIndexFaultClassifier.Descrivi(LocalIndexFaultClassifier.Classify(ex))}");
                if (!ricostruzione && LocalIndexFaultClassifier.IsCorruption(ex))
                    _ = _indexRecovery?.RichiediRecupero("Riconciliazione completa: indice locale corrotto");
                return LocalIndexRebuildOutcome.Incompleta($"Riconciliazione completa non riuscita: {ex.Message}");
            }

            if (sorgenteIncompleta != null)
            {
                _indexRecovery?.InvalidaComeObsoleto(sorgenteIncompleta);
                return LocalIndexRebuildOutcome.Incompleta(sorgenteIncompleta);
            }

            _snapshotCaricato = true;
            // Durante una ricostruzione la riabilitazione dei reader spetta al coordinatore, che la esegue
            // solo a esito completo: marcare sano qui farebbe rientrare i lettori prima del tempo.
            if (!ricostruzione) _indexRecovery?.MarcaSano();
            return LocalIndexRebuildOutcome.Riuscita(snapshotPersistente.Count);
        }
    }

    public void RefreshPaths(IEnumerable<string> changedPaths)
    {
        var paths = changedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && IsManagedTestCasePath(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0) return;

        lock (_refreshGate)
        {
            var results = new (string Path, bool Exists, bool CommittedDelete, DateTime Mtime, long Length, TestCase? TestCase, string? Error)[paths.Count];
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) };
            Parallel.For(0, paths.Count, parallelOptions, i =>
            {
                var path = paths[i];
                if (!File.Exists(path))
                {
                    results[i] = (path, false, false, default, 0, null, null);
                    return;
                }

                try
                {
                    var info = new FileInfo(path);
                    var tc = JsonIo.TryRead<TestCase>(path, out var errore);
                    var committedDelete = tc == null && WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, path);
                    if (tc != null)
                    {
                        tc.NormalizzaVeicoli();
                        TestCaseLegacyMigrations.MigraInLettura(tc, path); // punto unico condiviso, vedi sopra
                        TestCaseFingerprint.Stamp(tc);
                    }
                    results[i] = (path, true, committedDelete, info.LastWriteTimeUtc, info.Length, tc, errore);
                }
                catch (Exception ex)
                {
                    results[i] = (path, true, false, default, 0, null, ex.Message);
                }
            });

            var confermatiOk = new List<string>();
            var nuoviGrezziIlleggibili = new List<FileConProblemi>();
            // Path toccati che NON risultano piu' in cache dopo la classificazione (copie di conflitto, eliminati): non entrano nell'indice.
            var copieEscluse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                foreach (var result in results)
                {
                    if (!result.Exists || result.CommittedDelete)
                    {
                        _cache.Remove(result.Path);
                        _fileLengths.Remove(result.Path);
                        confermatiOk.Add(result.Path);
                        continue;
                    }

                    if (result.TestCase == null)
                    {
                        // result.CommittedDelete sopra ha già intercettato ogni tombstone (6.4.2: nessun gate su
                        // ledger/receipt, IsCommittedTombstone riconosce _ditmDeleted direttamente): se si arriva
                        // qui il file è genuinamente non leggibile o corrotto. La tolleranza (vedi
                        // FileProblemToleranceTracker) evita che una scrittura OneDrive a metà, colta esattamente
                        // dal watcher, diventi subito un avviso visibile all'utente.
                        nuoviGrezziIlleggibili.Add(new FileConProblemi
                        {
                            Percorso = result.Path,
                            Errore = result.Error ?? "sconosciuto",
                            RilevatoIl = DateTime.Now
                        });
                        continue;
                    }

                    _cache[result.Path] = (result.Mtime, result.TestCase);
                    _fileLengths[result.Path] = result.Length;
                    confermatiOk.Add(result.Path);
                }

                _fileConProblemi = _tolleranzaProblemi.AggiornaIncrementale(confermatiOk, nuoviGrezziIlleggibili);

                // FASE 3: il refresh incrementale rilegge solo i path toccati dal watcher, non l'intera
                // cartella, quindi non può ri-scoprire da solo una copia di conflitto già nota ma non toccata
                // in questo giro (è già stata rimossa da _cache la volta scorsa). Le voci non toccate restano
                // com'erano; solo i path toccati vengono riclassificati (una copia di conflitto cancellata o
                // rinominata dal Master sparisce correttamente, una nuova viene rilevata subito).
                var toccatiOraSet = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                var copieNoteNonToccate = _copieDiConflitto.Where(c => !toccatiOraSet.Contains(c.Percorso)).ToList();
                ClassificaDuplicatiEEscludiCopieDiConflitto(_cache, _fileLengths, out _duplicatiId, out var copieRilevateOra, CopiaAssorbita);
                _copieDiConflitto = copieNoteNonToccate.Concat(copieRilevateOra).ToList();
                copieEscluse.UnionWith(paths.Where(p => !_cache.ContainsKey(p)));

                RicostruisciLookup();
                InvalidaViste();
            }

            if (_localIndex != null)
            {
                try
                {
                    var mutations = results
                        .Where(result => !result.Exists || result.CommittedDelete || result.TestCase != null)
                        .Select(result => !result.Exists || result.CommittedDelete || copieEscluse.Contains(result.Path)
                            ? LocalIndexMutation.Remove(result.Path)
                            : LocalIndexMutation.Upsert(result.Path, result.Mtime, result.Length, result.TestCase!))
                        .ToList();
                    _localIndex.ApplyBatch(mutations);
                }
                catch (Exception ex)
                {
                    CrashLogService.Registra("Indice SQLite locale TestCase: refresh incrementale batch", ex);
                    // Il batch e' transazionale: dopo il rollback l'indice non contiene queste mutazioni ed e'
                    // quindi OBSOLETO, non corrotto. Va invalidato, non messo in quarantena.
                    var tipo = LocalIndexFaultClassifier.Classify(ex);
                    _indexRecovery?.InvalidaComeObsoleto($"Refresh incrementale non applicato: {LocalIndexFaultClassifier.Descrivi(tipo)}");
                    if (tipo == LocalIndexFaultKind.Corruption)
                        _ = _indexRecovery?.RichiediRecupero("Refresh incrementale: indice locale corrotto");
                }
            }
        }
    }

    public int Count(bool includeEliminati = false)
    {
        lock (_gate)
        {
            if (includeEliminati) return _cache.Count;
            return _cache.Values.Count(v => !v.TestCase.Eliminato);
        }
    }

    public IReadOnlyList<TestCase> GetAll(bool includeEliminati = false)
    {
        PerfContatori.IncrementaTestCaseGetAll();
        lock (_gate)
        {
            if (includeEliminati) return _tutti ??= _cache.Values.Select(v => v.TestCase).ToList();
            return _attivi ??= _cache.Values.Select(v => v.TestCase).Where(tc => !tc.Eliminato).ToList();
        }
    }

    /// <summary>Stessa vista di GetAll(false) ma ordinata per Id, memoizzata come le altre e invalidata dagli
    /// stessi punti di scrittura (feedback utente 2026-09-17, volumi 100k): l'editor di un Test la usa per il
    /// selettore dei Test collegati, e ordinare 104k identificativi ad ogni apertura costava ~200ms ogni volta
    /// per un elenco che cambia solo quando cambiano i dati.</summary>
    public IReadOnlyList<TestCase> GetAllOrdinatiPerId()
    {
        lock (_gate)
            return _attiviOrdinatiPerId ??= _cache.Values.Select(v => v.TestCase).Where(tc => !tc.Eliminato)
                .OrderBy(tc => tc.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Codici ancora referenziati da altre entita (NC, Segnalazioni) anche se il test non esiste piu
    /// (eliminazione definitiva). Iniettato da AppState; contribuisce SOLO a GetAllIds(true), la sorgente
    /// degli ID passati a IdGenerator, cosi un codice referenziato non viene mai riassegnato.</summary>
    public Func<IEnumerable<string>>? IdRiservatiAggiuntivi { get; set; }

    /// <summary>Restituisce solo gli identificativi senza deserializzare o clonare TestCase. Con
    /// includeEliminati=true comprende anche i codici riservati da <see cref="IdRiservatiAggiuntivi"/>.</summary>
    public IReadOnlyCollection<string> GetAllIds(bool includeEliminati = false)
    {
        if (includeEliminati)
        {
            List<string> propri;
            lock (_gate) propri = _byId.Keys.ToList();
            var extra = IdRiservatiAggiuntivi;
            if (extra != null) propri.AddRange(extra());
            return propri;
        }
        lock (_gate)
        {
            return _cache.Values
                .Where(v => !v.TestCase.Eliminato && !string.IsNullOrWhiteSpace(v.TestCase.Id))
                .Select(v => v.TestCase.Id)
                .ToList();
        }
    }

    /// <summary>Trova la baseline SW più recente per i componenti specificate in una sola passata O(N) senza ordinamenti intermedi.</summary>
    public TestCase? GetBaselinePiuRecente(IReadOnlyCollection<string>? ecuList, string? escludiId = null)
    {
        if (ecuList == null || ecuList.Count == 0) return null;
        var ecuSet = ecuList is HashSet<string> set && set.Comparer == StringComparer.OrdinalIgnoreCase
            ? set
            : new HashSet<string>(ecuList, StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            TestCase? best = null;
            DateTime bestDate = DateTime.MinValue;

            foreach (var (_, tc) in _cache.Values)
            {
                if (tc.Eliminato || tc.Esecuzioni.Count == 0) continue;
                if (!string.IsNullOrEmpty(escludiId) && string.Equals(tc.Id, escludiId, StringComparison.OrdinalIgnoreCase)) continue;
                if (tc.DataCreazione <= bestDate) continue;

                var tcEcu = tc.Componente;
                if (tcEcu == null || tcEcu.Count == 0) continue;

                bool matches = false;
                for (int i = 0; i < tcEcu.Count; i++)
                {
                    if (ecuSet.Contains(tcEcu[i]))
                    {
                        matches = true;
                        break;
                    }
                }

                if (matches)
                {
                    best = tc;
                    bestDate = tc.DataCreazione;
                }
            }

            return best;
        }
    }

    private void InvalidaViste() { _tutti = null; _attivi = null; _attiviOrdinatiPerId = null; }

    private void RicostruisciLookup()
    {
        _byId = new Dictionary<string, TestCase>(StringComparer.OrdinalIgnoreCase);
        _pathByUid = new Dictionary<Guid, string>();
        _pathById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _cache.OrderBy(kv => kv.Value.Mtime))
        {
            var tc = kv.Value.TestCase;
            if (!string.IsNullOrWhiteSpace(tc.Id))
            {
                _byId[tc.Id] = tc;
                _pathById[tc.Id] = kv.Key;
            }
            if (tc.Uid != Guid.Empty) _pathByUid[tc.Uid] = kv.Key;
        }
    }

    public TestCase? GetById(string id)
    {
        lock (_gate) return _byId.GetValueOrDefault(id);
    }

    public TestCase? GetByUid(Guid uid)
    {
        lock (_gate) return _pathByUid.TryGetValue(uid, out var path) ? _cache.GetValueOrDefault(path).TestCase : null;
    }

    public bool IdExists(string id)
    {
        lock (_gate) return _byId.ContainsKey(id);
    }

    public TestCase? LoadCurrent(TestCase tc)
    {
        var path = PathFor(tc);
        if (!File.Exists(path) || WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, path)) return null;
        var current = JsonIo.TryRead<TestCase>(path, out _);
        if (current != null)
        {
            current.NormalizzaVeicoli();
            TestCaseLegacyMigrations.MigraInLettura(current, path); // punto unico condiviso, vedi sopra
            TestCaseFingerprint.Stamp(current);
        }
        return current;
    }

    /// <summary>Copia di lavoro di un Test (stessa revisione e stesso stamp): chi la modifica e poi la salva con la revisione
    /// attesa non tocca l'istanza condivisa in cache finche' il salvataggio non riesce (azioni di massa, P1.3).</summary>
    public TestCase CopiaPerModifica(TestCase tc) => Clone(tc);

    private string NuovoPathFor(TestCase tc) => tc.Eliminato ? _paths.CestinoFile(tc.Id) :
        _paths.TestCaseFileGerarchico(tc, VeicoliPerBucketing());

    // Cache economica del solo catalogo Veicoli usato per decidere il bucket di cartella Gamma/Sottoveicolo di
    // un Test (Addendum prestazioni 2026-09-23, feedback coordinatore: un'unione/rinomina/divisione su un
    // catalogo master può riscrivere centinaia/migliaia di Test in sequenza, e NuovoPathFor veniva chiamato più
    // volte per singolo Save — ognuna una `new MasterDataRepository<Veicolo>(...).Load()` che riparte da zero
    // (parsing JSON e, su Phase D, verifica journal). Invalidata sul timestamp di ultima scrittura del file:
    // qualunque modifica reale (anche da un'altra postazione via OneDrive, che aggiorna comunque l'mtime locale
    // alla sincronizzazione) la scarta. Un mancato invalidamento nella minuscola finestra di risoluzione del
    // filesystem sposterebbe al più il Test nel bucket Gamma "vecchio" per un istante: la Gamma è solo
    // organizzazione fisica delle cartelle, mai una fonte di verità sul contenuto del Test (vedi già i commenti
    // su GammaNome vuoto/non ancora categorizzato), quindi non è una finestra di rischio sui dati.
    private List<Veicolo>? _veicoliBucketingCache;
    private DateTime _veicoliBucketingCacheMtimeUtc;

    private List<Veicolo> VeicoliPerBucketing()
    {
        var path = _paths.VeicoliFile;
        DateTime mtimeUtc;
        try { mtimeUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { mtimeUtc = DateTime.MinValue; }

        lock (_gate)
        {
            if (_veicoliBucketingCache != null && _veicoliBucketingCacheMtimeUtc == mtimeUtc)
                return _veicoliBucketingCache;
        }

        var lista = new MasterDataRepository<Veicolo>(path).Load();
        lock (_gate)
        {
            _veicoliBucketingCache = lista;
            _veicoliBucketingCacheMtimeUtc = mtimeUtc;
        }
        return lista;
    }

    /// <summary>Percorso attuale del file del Test (per un refresh mirato dopo un rollback transazionale).</summary>
    public string FilePathOf(TestCase tc) => PathFor(tc);

    private string PathFor(TestCase tc)
    {
        if (tc.Eliminato) return _paths.CestinoFile(tc.Id);
        lock (_gate)
        {
            if (tc.Uid != Guid.Empty && _pathByUid.TryGetValue(tc.Uid, out var byUid)) return byUid;
            if (!string.IsNullOrWhiteSpace(tc.Id) && _pathById.TryGetValue(tc.Id, out var byId)) return byId;
            return NuovoPathFor(tc);
        }
    }

    public string PercorsoPer(TestCase tc) => PathFor(tc);

    public void Save(TestCase tc, bool isNew) => Save(tc, isNew, null);

    /// <param name="lockTimeoutCommit">Timeout dell'acquisizione del lock di commit cross-processo (vedi
    /// <see cref="TestCaseCommitLock"/>). Il default (<see cref="TestCaseCommitLock.DefaultTimeout"/>, 8s) è
    /// quello usato in produzione; un valore esplicito serve solo ai test che devono verificare il comportamento
    /// di timeout in tempi ragionevoli.</param>
    /// <param name="ramiGiaRisolti">Solo merge automatico di un fork: hash dei rami incorporati nella versione scritta. Vedi
    /// <c>VisibleConflictWriteGuard.EnsureNoVisibleFork</c> (parametro ramiGiaRisolti). Null per ogni altro salvataggio.</param>
    public void Save(TestCase tc, bool isNew, long? revisioneAttesa, TimeSpan? lockTimeoutCommit = null, IReadOnlySet<string>? ramiGiaRisolti = null)
    {
        // Un normale update nello stesso path resta una singola scrittura atomica. Un move, invece, modifica
        // due path e deve avere tombstone + nuova versione nella stessa receipt anche quando il repository viene
        // chiamato fuori dal SalvaSicuro dell'editor.
        if (!WorkflowTransactionService.IsActive && !isNew)
        {
            var oldPath = PathFor(tc);
            var newPath = NuovoPathFor(tc);
            if (!oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase))
            {
                WorkflowTransactionService.Execute(_paths, $"move-test:{tc.Id}", () => SaveCore(tc, isNew, revisioneAttesa, lockTimeoutCommit, ramiGiaRisolti));
                return;
            }
        }

        SaveCore(tc, isNew, revisioneAttesa, lockTimeoutCommit, ramiGiaRisolti);
    }

    private void SaveCore(TestCase tc, bool isNew, long? revisioneAttesa, TimeSpan? lockTimeoutCommit = null, IReadOnlySet<string>? ramiGiaRisolti = null)
    {
        WorkspaceWritePolicy.EnsureCanWrite(_paths, "Salvataggio Test");
        lock (_gate)
        {
            var journal = RequireJournal();
            tc.NormalizzaVeicoli();
            if (isNew && tc.DataCreazione == default) tc.DataCreazione = DateTime.Now;
            if (isNew && tc.UltimaModifica == default) tc.UltimaModifica = DateTime.Now;
            // Prenotazione esclusiva dell'ID sulla cartella condivisa (P2.8), prima di qualunque scrittura: un'altra postazione
            // con indice locale non aggiornato può aver già generato lo stesso progressivo. Il claim non confermato (salvataggio
            // fallito prima della scrittura) viene rilasciato dal Dispose qui sotto.
            using var idClaim = isNew ? PrenotaIdNuovo(tc) : null;

            var pathPrecedente = PathFor(tc);
            var path = NuovoPathFor(tc);

            // Lock di commit fra processi/postazioni (vedi TestCaseCommitLock): copre l'INTERA finestra da qui
            // in avanti — rilettura del file corrente, confronto revisione/hash, scrittura atomica — che prima
            // era un classico check-then-act senza alcuna serializzazione condivisa fra due AppState/postazioni
            // diverse puntate sulla stessa cartella. Sullo stesso PC questo lock è pieno (System.IO.FileShare.
            // None); fra PC diversi la garanzia è più debole (vedi doc della classe): per questo la rilettura
            // sotto resta comunque SEMPRE fatta qui dentro, mai riusando uno stato letto prima dell'acquisizione.
            using var commitLock = TestCaseCommitLock.AcquireAll(tc.Id, new[] { pathPrecedente, path }, lockTimeoutCommit);

            // Ricontrollare copie OneDrive materializzate DOPO l'attesa del
            // commit lock. Un cambio Gamma/componente/ID può interessare sia il file
            // di partenza sia quello di destinazione: non ignorare fork del
            // vecchio percorso mentre si sposta il TestCase.
            EnsureNoVisibleForkTc(path, tc.Uid, ramiGiaRisolti);
            if (!pathPrecedente.Equals(path, StringComparison.OrdinalIgnoreCase))
                EnsureNoVisibleForkTc(pathPrecedente, tc.Uid, ramiGiaRisolti);

            TestCase? baseVersion = null;
            var expectedHash = TestCaseFingerprint.GetStored(tc);
            var historyBeforeSafety = tc.Esecuzioni.Select(CloneRevision).ToList();
            var historyExtendedBySafety = false;
            // Id con cui il record era salvato PRIMA di questa chiamata, letto dal file autorevole su disco
            // (non dalla cache, che potrebbe già essere stata invalidata altrove): serve solo al lookup
            // incrementale post-salvataggio (AggiornaLookupDopoSalvataggio) per riconoscere un cambio Id e non
            // lasciare una voce orfana in _byId/_pathById — vedi commento lì. Resta null per un record nuovo.
            string? idPrecedenteSuDisco = null;

            // Un Test eliminato (tombstone committato) da un'altra postazione non può essere riscritto da un editor rimasto
            // aperto: la ricreazione del file sarebbe una "risurrezione" di una versione obsoleta (P1.8).
            if (!isNew && WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, pathPrecedente))
                throw new ConflittoRevisioneException(tc.Id, revisioneAttesa ?? tc.RevisioneDati, -1);

            if (!isNew && File.Exists(pathPrecedente))
            {
                var corrente = JsonIo.TryRead<TestCase>(pathPrecedente, out var errore);
                if (corrente == null)
                    throw new InvalidDataException($"Il file del test {tc.Id} non è leggibile e non verrà sovrascritto: {errore}");
                corrente.NormalizzaVeicoli();
                // Solo per il confronto hash/conflitto: "corrente" è una rilettura di sola diagnosi, mai
                // esposta all'utente né salvata — "tc" (l'oggetto vivo con le modifiche dell'utente) non va
                // MAI toccato da questa migrazione (review 6.4.0, punto 1).
                TestCaseLegacyMigrations.MigraInLettura(corrente, pathPrecedente);
                idPrecedenteSuDisco = corrente.Id;
                var currentHash = TestCaseFingerprint.Compute(corrente);

                if (revisioneAttesa.HasValue && corrente.RevisioneDati != revisioneAttesa.Value)
                    throw new ConflittoRevisioneException(tc.Id, revisioneAttesa.Value, corrente.RevisioneDati);

                if (!string.IsNullOrWhiteSpace(expectedHash) &&
                    !currentHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ConflittoRevisioneException(
                        tc.Id,
                        revisioneAttesa ?? tc.RevisioneDati,
                        corrente.RevisioneDati,
                        contenutoDifferenteAllaStessaRevisione: corrente.RevisioneDati == (revisioneAttesa ?? tc.RevisioneDati));
                }

                TestCaseFingerprint.Stamp(corrente);
                baseVersion = corrente;
                historyExtendedBySafety = TestRevisionSafety.PreserveImmutableHistory(
                    corrente, tc, DateTime.Now, string.IsNullOrWhiteSpace(tc.ModificatoDa) ? corrente.ModificatoDa : tc.ModificatoDa);
            }
            else if (!isNew && revisioneAttesa.HasValue)
            {
                // Come CR/NC: un file sparito nel frattempo (Test eliminato o spostato fuori da TestLoom mentre l'editor era
                // aperto) non autorizza a ricreare una versione obsoleta; il conflitto va mostrato all'utente (P1.8).
                throw new ConflittoRevisioneException(tc.Id, revisioneAttesa.Value, -1);
            }

            if (!pathPrecedente.Equals(path, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                if (!WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, path))
                {
                    var destinazione = JsonIo.TryRead<TestCase>(path, out _);
                    if (destinazione == null || destinazione.Uid != tc.Uid)
                        throw new IOException($"La destinazione {path} esiste già ed appartiene a un altro Test. Nessun file è stato sovrascritto.");
                }
            }

            var revisionePrecedente = tc.RevisioneDati;
            var stampPrecedente = TestCaseFingerprint.GetStored(tc);
            tc.SchemaVersion = TestCase.CurrentSchemaVersion;
            tc.RevisioneDati = isNew ? Math.Max(1, tc.RevisioneDati) : tc.RevisioneDati + 1;
            TestCaseFingerprint.Stamp(tc);

            JournalPendingWrite? journalPending = null;
            try
            {
                var operation = isNew ? JournalOperation.Create
                    : !pathPrecedente.Equals(path, StringComparison.OrdinalIgnoreCase) ? JournalOperation.Move
                    : JournalOperation.Save;
                journalPending = journal.Prepare(operation, baseVersion, tc, pathPrecedente, path);
                // Merge automatico fork (6.4.1, punto B): registra la base SOLO in locale (journal.db di questo
                // PC), con lo stesso hash che l'evento journal appena preparato userà come BaseHash. Best-effort,
                // mai bloccante: vedi SharedTestJournalService.PersistLocalBase.
                if (baseVersion != null) journal.PersistLocalBase(baseVersion);
                JsonIo.WriteAtomic(path, tc, overwrite: !isNew);
                idClaim?.Conferma();
            }
            catch (Exception ex)
            {
                tc.RevisioneDati = revisionePrecedente;
                TestCaseFingerprint.SetStored(tc, stampPrecedente);
                if (historyExtendedBySafety) tc.Esecuzioni = historyBeforeSafety;
                journal.Cancel(journalPending);

                // Punto 3 della correzione concorrenza: il lock di commit copre per intero questo PC, ma fra
                // due PC diversi (copie locali distinte della stessa cartella OneDrive/SharePoint) resta
                // possibile che la scrittura atomica (temp + File.Move) urti una scrittura concorrente
                // dell'altra postazione proprio in questa finestra. Prima di questa correzione ne usciva una
                // IOException/UnauthorizedAccessException NON gestita (osservato nella 6.3.1); ora diventa un
                // conflitto di revisione con lo stesso contratto già gestito dall'editor. I dati su disco sono
                // sempre integri: WriteAtomic scrive su un file temporaneo e rinomina solo a scrittura riuscita,
                // quindi non c'è mai un file a metà scrittura da questo lato.
                if (ex is (IOException or UnauthorizedAccessException) and not ConflittoRevisioneException)
                {
                    var revisioneCorrenteOsservata =
                        TryLeggiRevisioneCorrente(path) ??
                        TryLeggiRevisioneCorrente(pathPrecedente) ??
                        revisioneAttesa ??
                        revisionePrecedente;
                    throw new ConflittoRevisioneException(
                        tc.Id, revisioneAttesa ?? revisionePrecedente, revisioneCorrenteOsservata);
                }
                throw;
            }

            if (!pathPrecedente.Equals(path, StringComparison.OrdinalIgnoreCase) && File.Exists(pathPrecedente))
            {
                try { WorkflowTransactionService.DeleteFile(pathPrecedente); }
                catch (Exception ex)
                {
                    CrashLogService.Registra("TestCase move: eliminazione vecchio path rinviata", ex);
                    _fileConProblemi = _fileConProblemi
                        .Where(p => !p.Percorso.Equals(pathPrecedente, StringComparison.OrdinalIgnoreCase))
                        .Append(new FileConProblemi
                        {
                            Percorso = pathPrecedente,
                            Errore = "Copia precedente non eliminata dopo lo spostamento; verrà rilevata dalla riconciliazione. " + ex.Message,
                            RilevatoIl = DateTime.Now
                        }).ToList();
                }
                _cache.Remove(pathPrecedente);
                _fileLengths.Remove(pathPrecedente);
                TryLocalIndex(() => _localIndex?.Remove(pathPrecedente), "rimozione path precedente");
            }

            // La cache conserva una COPIA di cio' che e' stato scritto: l'oggetto del chiamante (editor, azioni di massa)
            // puo' essere modificato dopo il salvataggio senza che quelle modifiche non salvate diventino visibili alle
            // altre letture e vengano scritte dal salvataggio successivo (P1.3).
            var copiaInCache = Clone(tc);
            var info = new FileInfo(path);
            _cache[path] = (info.LastWriteTimeUtc, copiaInCache);
            _fileLengths[path] = info.Length;
            AggiornaLookupDopoSalvataggio(pathPrecedente, path, copiaInCache, idPrecedenteSuDisco);
            InvalidaViste();
            TryLocalIndex(() => _localIndex?.Upsert(path, info.LastWriteTimeUtc, info.Length, copiaInCache), "save");
            ScheduleJournalPublish(journalPending);
            if (isNew) AvviaPuliziaClaimSeDovuta();
            DiagnosticTraceService.Registra("TestCase.Save", $"{tc.Id} nuovo={isNew} revisione={tc.RevisioneDati}");
        }
    }

    /// <summary>Pulizia opportunistica dei claim ID scaduti (P2.8), chiamata dopo ogni nuovo Test salvato. La decisione è in
    /// memoria (nessun I/O sul thread del salvataggio); se è dovuta, la pulizia gira in background. Al più una volta al giorno
    /// per PC, best-effort: non fallisce mai un salvataggio.</summary>
    private void AvviaPuliziaClaimSeDovuta()
    {
        try
        {
            if (!TestCaseIdClaim.PuliziaDovuta(_ultimaPuliziaClaimUtc, DateTime.UtcNow)) return;
            if (Interlocked.CompareExchange(ref _puliziaClaimInCorso, 1, 0) != 0) return;
            _puliziaClaimTask = Task.Run(EseguiPuliziaClaimInBackground);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _puliziaClaimInCorso, 0);
            CrashLogService.Registra("Avvio pulizia prenotazioni ID Test", ex);
        }
    }

    /// <summary>Corpo della pulizia in background: rispetta il limite giornaliero del PC (marcatore nella cartella locale),
    /// poi rimuove i claim idonei. Un errore viene solo registrato.</summary>
    private void EseguiPuliziaClaimInBackground()
    {
        try
        {
            var adesso = DateTime.UtcNow;
            var ultima = TestCaseIdClaim.LeggiUltimaPulizia(LocalWorkspaceDirectory);
            if (TestCaseIdClaim.PuliziaDovuta(ultima, adesso))
            {
                TestCaseIdClaim.RegistraUltimaPulizia(LocalWorkspaceDirectory, adesso);
                var rimossi = TestCaseIdClaim.PulisciScaduti(_paths, IdEsisteNelDato, adesso);
                DiagnosticTraceService.Registra("TestCase.PuliziaClaimId", $"rimossi={rimossi}");
                lock (_gate) _ultimaPuliziaClaimUtc = adesso;
            }
            else
            {
                lock (_gate) _ultimaPuliziaClaimUtc = ultima;
            }
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Pulizia prenotazioni ID Test", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _puliziaClaimInCorso, 0);
        }
    }

    /// <summary>Il Test con questo ID esiste nei dati condivisi (indice locale oppure cestino): condizione per rimuovere il claim.</summary>
    private bool IdEsisteNelDato(string id) => IdExists(id) || File.Exists(_paths.CestinoFile(id));

    /// <summary>ID di un Test nuovo: scarta quelli già usati localmente (tombstone inclusi) e prenota il primo libero sulla
    /// cartella condivisa. Se un'altra postazione l'ha già prenotato, rigenera il progressivo e riprova (P2.8).</summary>
    private TestCaseIdClaim PrenotaIdNuovo(TestCase tc)
    {
        while (true)
        {
            // File.Exists include intenzionalmente i tombstone: un ID già assegnato non viene riciclato.
            while (IdExists(tc.Id) || File.Exists(NuovoPathFor(tc)))
                tc.Id = IdGenerator.RegeneraProgressivo(tc.Id);

            var claim = TestCaseIdClaim.TryPrenota(_paths, tc.Id, tc.Uid);
            if (claim != null) return claim;
            tc.Id = IdGenerator.RegeneraProgressivo(tc.Id);
        }
    }

    /// <summary>Aggiornamento incrementale O(1) di _byId/_pathByUid/_pathById dopo il salvataggio di UN SOLO
    /// Test, al posto del rebuild completo O(n log n) di RicostruisciLookup (Addendum prestazioni 2026-09-23,
    /// feedback coordinatore: un'unione/rinomina/divisione su un catalogo master può chiamare Save() centinaia
    /// o migliaia di volte in sequenza — un rebuild completo dei lookup, con tanto di OrderBy su TUTTA la
    /// cache, ad OGNI singola chiamata rendeva quel tipo di operazione bulk quadratico rispetto alla scala
    /// dell'intero archivio, non lineare rispetto ai soli record coinvolti). Preserva ESATTAMENTE la stessa
    /// regola di RicostruisciLookup ("vince il file più recente per Mtime" quando lo stesso Id/Uid compare in
    /// più file, es. un fork/duplicato da sincronizzazione ritardata): se il Save che ha appena scritto `path`
    /// scopre che un ALTRO path (diverso sia dal precedente sia dal nuovo) rivendica già lo stesso Id o Uid,
    /// il caso è quello raro/ambiguo del duplicato reale e si ricade sul rebuild completo, mai su un lookup
    /// potenzialmente incoerente.
    ///
    /// Cambio Id (revisione review 2026-09-23, CRITICAL): un Test esistente può cambiare Id in-place (stesso
    /// Uid/stesso record, es. rigenerazione da TestCaseEditorViewModel dopo un cambio di iniziali/prefisso —
    /// vedi TestCaseEditorViewModel.RisolviTestNecessari/StateSafety). In quel caso il fast path incrementale
    /// NON è sicuro: rimuovere "a mano" solo la entry del vecchio Id rischierebbe di cancellare un Id che nel
    /// frattempo un'altra postazione ha legittimamente riassegnato altrove, e lasciarla non rimossa renderebbe
    /// IdExists/GetById(vecchioId) stale rispetto al disco. Per questo singolo caso — raro, mai generato dalle
    /// operazioni bulk Unisci/Rinomina/Dividi che non toccano mai TestCase.Id — si preferisce la correttezza
    /// al risparmio: rebuild completo, esattamente come prima di questa ottimizzazione.</summary>
    private void AggiornaLookupDopoSalvataggio(string pathPrecedente, string path, TestCase tc, string? idPrecedente)
    {
        if (!string.IsNullOrWhiteSpace(idPrecedente) && !string.IsNullOrWhiteSpace(tc.Id)
            && !idPrecedente.Equals(tc.Id, StringComparison.OrdinalIgnoreCase))
        {
            RicostruisciLookup();
            return;
        }

        var idCollide = !string.IsNullOrWhiteSpace(tc.Id) && _pathById.TryGetValue(tc.Id, out var pathPerId)
            && !pathPerId.Equals(pathPrecedente, StringComparison.OrdinalIgnoreCase)
            && !pathPerId.Equals(path, StringComparison.OrdinalIgnoreCase);
        var uidCollide = tc.Uid != Guid.Empty && _pathByUid.TryGetValue(tc.Uid, out var pathPerUid)
            && !pathPerUid.Equals(pathPrecedente, StringComparison.OrdinalIgnoreCase)
            && !pathPerUid.Equals(path, StringComparison.OrdinalIgnoreCase);
        if (idCollide || uidCollide)
        {
            RicostruisciLookup();
            return;
        }

        if (!string.IsNullOrWhiteSpace(tc.Id))
        {
            _byId[tc.Id] = tc;
            _pathById[tc.Id] = path;
        }
        if (tc.Uid != Guid.Empty) _pathByUid[tc.Uid] = path;
    }

    public void SoftDelete(TestCase tc, string operatore)
    {
        if (!WorkflowTransactionService.IsActive)
        {
            WorkflowTransactionService.Execute(_paths, $"trash-test:{tc.Id}", () => SoftDeleteCore(tc, operatore));
            return;
        }
        SoftDeleteCore(tc, operatore);
    }

    private void SoftDeleteCore(TestCase tc, string operatore)
    {
        WorkspaceWritePolicy.EnsureCanWrite(_paths, "Spostamento Test nel cestino");
        lock (_gate)
        {
            var journal = RequireJournal();
            var oldPath = PathFor(tc);
            var newPath = _paths.CestinoFile(tc.Id);
            // Lock di commit come in SaveCore, PRIMA della validazione e della lettura del file corrente (P1.4): senza, un
            // Save concorrente da un'altra istanza o postazione può scrivere tra il controllo di revisione e lo spostamento.
            using var commitLock = TestCaseCommitLock.AcquireAll(tc.Id, new[] { oldPath, newPath });
            EnsureNoVisibleForkTc(oldPath, tc.Uid);
            var baseVersion = ValidateAndLoadCurrent(tc, oldPath);
            var oldRevision = tc.RevisioneDati;
            var oldStamp = TestCaseFingerprint.GetStored(tc);
            var oldEliminato = tc.Eliminato;
            var oldDataEliminazione = tc.DataEliminazione;
            var oldModificatoDa = tc.ModificatoDa;
            var oldUltimaModifica = tc.UltimaModifica;

            tc.Eliminato = true;
            tc.DataEliminazione = DateTime.Now;
            tc.ModificatoDa = operatore;
            tc.UltimaModifica = DateTime.Now;
            tc.RevisioneDati++;
            TestCaseFingerprint.Stamp(tc);

            if (File.Exists(newPath) && !WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, newPath))
            {
                var target = JsonIo.TryRead<TestCase>(newPath, out _);
                if (target == null || target.Uid != tc.Uid)
                {
                    RestoreMutationState();
                    throw new IOException("Esiste già un altro Test nella destinazione del cestino. Nessun file è stato sovrascritto.");
                }
            }

            JournalPendingWrite? pending = null;
            try
            {
                EnsureNoVisibleForkTc(newPath, tc.Uid);
                pending = journal.Prepare(JournalOperation.SoftDelete, baseVersion, tc, oldPath, newPath);
                JsonIo.WriteAtomic(newPath, tc);
            }
            catch
            {
                RestoreMutationState();
                journal.Cancel(pending);
                throw;
            }

            if (File.Exists(oldPath))
            {
                try { WorkflowTransactionService.DeleteFile(oldPath); }
                catch (Exception ex) { CrashLogService.Registra("Soft delete: eliminazione vecchio path rinviata", ex); }
            }

            _cache.Remove(oldPath);
            _fileLengths.Remove(oldPath);
            TryLocalIndex(() => _localIndex?.Remove(oldPath), "soft delete vecchio path");

            var info = new FileInfo(newPath);
            var copiaInCache = Clone(tc); // come SaveCore: la cache non condivide l'istanza del chiamante (P1.3)
            _cache[newPath] = (info.LastWriteTimeUtc, copiaInCache);
            _fileLengths[newPath] = info.Length;
            RicostruisciLookup();
            InvalidaViste();
            TryLocalIndex(() => _localIndex?.Upsert(newPath, info.LastWriteTimeUtc, info.Length, copiaInCache), "soft delete");
            ScheduleJournalPublish(pending);
            DiagnosticTraceService.Registra("TestCase.SoftDelete", tc.Id);

            void RestoreMutationState()
            {
                tc.Eliminato = oldEliminato;
                tc.DataEliminazione = oldDataEliminazione;
                tc.ModificatoDa = oldModificatoDa;
                tc.UltimaModifica = oldUltimaModifica;
                tc.RevisioneDati = oldRevision;
                TestCaseFingerprint.SetStored(tc, oldStamp);
            }
        }
    }

    public void Restore(TestCase tc, string operatore)
    {
        if (!WorkflowTransactionService.IsActive)
        {
            WorkflowTransactionService.Execute(_paths, $"restore-test:{tc.Id}", () => RestoreCore(tc, operatore));
            return;
        }
        RestoreCore(tc, operatore);
    }

    private void RestoreCore(TestCase tc, string operatore)
    {
        WorkspaceWritePolicy.EnsureCanWrite(_paths, "Ripristino Test dal cestino");
        lock (_gate)
        {
            var journal = RequireJournal();
            var oldPath = _paths.CestinoFile(tc.Id);
            // Destinazione attiva calcolata esplicitamente: NuovoPathFor(tc) sul Test ancora eliminato indicherebbe il cestino.
            var newPath = _paths.TestCaseFileGerarchico(tc, VeicoliPerBucketing());
            // Lock di commit prima della validazione (P1.4), come in SaveCore e SoftDeleteCore.
            using var commitLock = TestCaseCommitLock.AcquireAll(tc.Id, new[] { oldPath, newPath });
            EnsureNoVisibleForkTc(oldPath, tc.Uid);
            var baseVersion = ValidateAndLoadCurrent(tc, oldPath);
            var oldRevision = tc.RevisioneDati;
            var oldStamp = TestCaseFingerprint.GetStored(tc);
            var oldEliminato = tc.Eliminato;
            var oldDataEliminazione = tc.DataEliminazione;
            var oldModificatoDa = tc.ModificatoDa;
            var oldUltimaModifica = tc.UltimaModifica;

            tc.Eliminato = false;
            tc.DataEliminazione = null;
            tc.ModificatoDa = operatore;
            tc.UltimaModifica = DateTime.Now;
            tc.RevisioneDati++;
            TestCaseFingerprint.Stamp(tc);

            if (File.Exists(newPath) && !WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, newPath))
            {
                var target = JsonIo.TryRead<TestCase>(newPath, out _);
                if (target == null || target.Uid != tc.Uid)
                {
                    RestoreMutationState();
                    throw new IOException("Esiste già un altro Test nella destinazione di ripristino. Nessun file è stato sovrascritto.");
                }
            }

            JournalPendingWrite? pending = null;
            try
            {
                EnsureNoVisibleForkTc(newPath, tc.Uid);
                pending = journal.Prepare(JournalOperation.Restore, baseVersion, tc, oldPath, newPath);
                JsonIo.WriteAtomic(newPath, tc);
            }
            catch
            {
                RestoreMutationState();
                journal.Cancel(pending);
                throw;
            }

            if (File.Exists(oldPath))
            {
                try { WorkflowTransactionService.DeleteFile(oldPath); }
                catch (Exception ex) { CrashLogService.Registra("Restore: eliminazione vecchio path rinviata", ex); }
            }

            _cache.Remove(oldPath);
            _fileLengths.Remove(oldPath);
            TryLocalIndex(() => _localIndex?.Remove(oldPath), "restore vecchio path");

            var info = new FileInfo(newPath);
            var copiaInCache = Clone(tc); // come SaveCore: la cache non condivide l'istanza del chiamante (P1.3)
            _cache[newPath] = (info.LastWriteTimeUtc, copiaInCache);
            _fileLengths[newPath] = info.Length;
            RicostruisciLookup();
            InvalidaViste();
            TryLocalIndex(() => _localIndex?.Upsert(newPath, info.LastWriteTimeUtc, info.Length, copiaInCache), "restore");
            ScheduleJournalPublish(pending);

            void RestoreMutationState()
            {
                tc.Eliminato = oldEliminato;
                tc.DataEliminazione = oldDataEliminazione;
                tc.ModificatoDa = oldModificatoDa;
                tc.UltimaModifica = oldUltimaModifica;
                tc.RevisioneDati = oldRevision;
                TestCaseFingerprint.SetStored(tc, oldStamp);
            }
        }
    }

    public void EliminaDefinitivamente(TestCase tc)
    {
        if (!WorkflowTransactionService.IsActive)
        {
            WorkflowTransactionService.Execute(_paths, $"delete-test:{tc.Id}", () => EliminaDefinitivamenteCore(tc));
            return;
        }
        EliminaDefinitivamenteCore(tc);
    }

    private void EliminaDefinitivamenteCore(TestCase tc)
    {
        WorkspaceWritePolicy.EnsureCanWrite(_paths, "Eliminazione definitiva Test");
        lock (_gate)
        {
            var journal = RequireJournal();
            var path = _paths.CestinoFile(tc.Id);
            // Lock di commit prima della validazione (P1.4): un Save concorrente non deve ricreare il Test appena eliminato.
            using var commitLock = TestCaseCommitLock.AcquireAll(tc.Id, new[] { path });
            EnsureNoVisibleForkTc(path, tc.Uid);
            TestCase? baseVersion = null;
            if (File.Exists(path)) baseVersion = ValidateAndLoadCurrent(tc, path);

            JournalPendingWrite? pending = null;
            try
            {
                pending = journal.Prepare(JournalOperation.Delete, baseVersion ?? tc, null, path, null);
                if (File.Exists(path)) WorkflowTransactionService.DeleteFile(path);
            }
            catch
            {
                journal.Cancel(pending);
                throw;
            }
            _cache.Remove(path);
            _fileLengths.Remove(path);
            RicostruisciLookup();
            InvalidaViste();
            TryLocalIndex(() => _localIndex?.Remove(path), "eliminazione definitiva");
            ScheduleJournalPublish(pending);
            DiagnosticTraceService.Registra("TestCase.EliminaDefinitivamente", tc.Id);
        }
    }

    /// <summary>Lettura best-effort della sola RevisioneDati corrente, usata SOLO per arricchire il messaggio di
    /// <see cref="ConflittoRevisioneException"/> quando la scrittura atomica è fallita per contesa concorrente
    /// (vedi SaveCore). Non lancia mai: se il file non è leggibile in questo istante (probabile, è proprio la
    /// stessa contesa che ha fatto fallire la scrittura) restituisce null e il chiamante usa un valore di
    /// fallback. Non è mai usata per decidere se c'è conflitto, solo per descriverlo meglio all'utente.</summary>
    private static long? TryLeggiRevisioneCorrente(string path)
    {
        try
        {
            var tc = JsonIo.TryRead<TestCase>(path, out _);
            return tc?.RevisioneDati;
        }
        catch { return null; }
    }

    private static TestCase ValidateAndLoadCurrent(TestCase expected, string path)
    {
        var current = JsonIo.TryRead<TestCase>(path, out var error)
            ?? throw new InvalidDataException($"Il file del test {expected.Id} non è leggibile e non verrà modificato: {error}");
        // Migrazione/normalizzazione PRIMA del calcolo hash (review 6.4.0, punto 1): expectedHash è stato
        // stampato altrove (es. caricamento in cache) DOPO la stessa migrazione — calcolarlo qui su un
        // "current" ancora grezzo confronterebbe contenuti non comparabili e solleverebbe un conflitto fantasma
        // per qualunque Test legacy Pianificato ancora da risalvare. Punto unico condiviso con LocalTestIndexStore.
        current.NormalizzaVeicoli();
        TestCaseLegacyMigrations.MigraInLettura(current, path);
        var currentHash = TestCaseFingerprint.Compute(current);
        var expectedHash = TestCaseFingerprint.GetStored(expected);
        if (current.RevisioneDati != expected.RevisioneDati)
            throw new ConflittoRevisioneException(expected.Id, expected.RevisioneDati, current.RevisioneDati);
        if (!string.IsNullOrWhiteSpace(expectedHash) && !currentHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new ConflittoRevisioneException(expected.Id, expected.RevisioneDati, current.RevisioneDati, true);
        TestCaseFingerprint.Stamp(current);
        return current;
    }

    private SharedTestJournalService RequireJournal()
    {
        if (_journal != null) return _journal;
        throw new IOException("Salvataggio Test bloccato: il journal locale di sicurezza non è disponibile. " +
                              (_journalUnavailableReason ?? "Riavvia TestLoom o verifica lo spazio/permessi della cartella locale."));
    }

    private bool IsManagedTestCasePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var name = Path.GetFileName(full);
            if (!name.StartsWith("testcase_", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
            return IsUnder(full, _paths.TestCasesDir) || IsUnder(full, _paths.CestinoDir);
        }
        catch { return false; }
    }

    private static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, comparison);
    }

    /// <summary>
    /// FASE 3 (multi-PC 6.4.2, correzione orchestratore): distingue una copia di conflitto OneDrive/SharePoint
    /// da un vero ID duplicato ambiguo, e la esclude da <paramref name="cache"/> (non diventa un secondo Test
    /// indirizzabile). A differenza di un tentativo precedente (poi revertito), questo controllo è
    /// CONTENT-DRIVEN e non assume alcun formato per l'Id: un file è "canonico" per il proprio Id quando il
    /// suo nome è esattamente quello che TestCaseRepository scrive per quell'Id
    /// ("testcase_&lt;Id&gt;.json", vedi DataPaths.TestCaseFile/PercorsoPer) — qualunque Id, generato da
    /// IdGenerator o storico/importato, funziona perché il confronto è sempre con il proprio valore, mai con
    /// un pattern generico.
    ///
    /// Regola: in un gruppo di file che condividono lo stesso TestCase.Id, se ESATTAMENTE UNO ha nome
    /// canonico, almeno un altro no, E TUTTI i non canonici hanno lo STESSO Uid interno del canonico, allora i
    /// non canonici sono copie di conflitto (mai un vero duplicato ambiguo): restano intatte sul disco
    /// (nessuna scrittura, nessuna cancellazione — sempre recuperabili) e vengono segnalate UNA volta in
    /// <see cref="CopieDiConflitto"/> con l'esito del confronto di contenuto rispetto all'originale, cosi' un
    /// Master sa subito se può ignorarle o deve verificarle manualmente.
    ///
    /// Il controllo sull'Uid è essenziale: una vera copia OneDrive/SharePoint replica byte-per-byte lo stesso
    /// salvataggio (stesso Uid), mentre due Test realmente distinti che si contendono lo stesso Id per
    /// coincidenza (fork/collisione, es. <see cref="TestCaseIncrementalIndexTests"/> e
    /// TestCaseIncrementalLookupCoherenceTests) hanno Uid diversi: quel caso resta un vero ID duplicato
    /// ambiguo in <see cref="DuplicatiId"/>, con la stessa regola "vince il più recente per Mtime" già
    /// applicata da RicostruisciLookup — comportamento preesistente, invariato. Un gruppo senza un canonico
    /// univoco (0 o >=2 canonici) resta ugualmente un vero ID duplicato ambiguo.
    /// </summary>
    /// <summary>Una copia di conflitto è "assorbita" quando contiene una versione dello stesso Test salvata da
    /// una postazione e nota al journal condiviso, il Test canonico ha una revisione successiva e non c'è alcun
    /// fork aperto per quel Test: il merge (automatico o del Master) o una modifica successiva l'hanno già
    /// incorporata, quindi non c'è nulla da recuperare e non va mostrata all'utente (6.4.2).</summary>
    /// <summary>Guardia sulle copie OneDrive visibili di un Test. Una copia gia' assorbita (versione nota del journal, piu'
    /// vecchia del canonico, senza fork aperto: vedi <see cref="CopiaAssorbita"/>) non blocca piu' i salvataggi successivi
    /// alla risoluzione, anche se OneDrive l'ha consegnata dopo. Ogni altra copia divergente blocca come prima.</summary>
    private void EnsureNoVisibleForkTc(string path, Guid uid, IReadOnlySet<string>? ramiGiaRisolti = null) =>
        VisibleConflictWriteGuard.EnsureNoVisibleFork(path, uid, ramiGiaRisolti, CopiaChiusaTc);

    private bool CopiaChiusaTc(JsonElement copia, JsonElement canonica)
    {
        try
        {
            var copiaTc = copia.Deserialize<TestCase>(JsonIo.Options);
            var canonicoTc = canonica.Deserialize<TestCase>(JsonIo.Options);
            return copiaTc != null && canonicoTc != null && CopiaAssorbita(copiaTc, canonicoTc);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return false; // fail-closed: una copia non leggibile come Test continua a bloccare
        }
    }

    /// <summary>Archivia nel recovery-bin (mai cancellazione definitiva) le copie OneDrive visibili di un fork TestCase
    /// appena risolto il cui contenuto e' uno dei rami incorporati nella risoluzione (hash di contenuto). Chiamare solo
    /// dopo un salvataggio riuscito della versione risolta: solo allora il contenuto del ramo e' incluso nel canonico.</summary>
    public IReadOnlyList<DomainRecoveryRecord> ArchiviaCopieDiRamiRisolti(TestCase risolto, IReadOnlySet<string> hashRami, string? operatore)
    {
        ArgumentNullException.ThrowIfNull(risolto);
        ArgumentNullException.ThrowIfNull(hashRami);
        if (hashRami.Count == 0 || risolto.Uid == Guid.Empty) return Array.Empty<DomainRecoveryRecord>();
        return VisibleConflictWriteGuard.ArchiviaCopieRisolte(_paths, PathFor(risolto), risolto.Uid,
            copia => HashRamoRisolto(copia, hashRami), DomainRecoveryCategory.TestCase, operatore);
    }

    private static bool HashRamoRisolto(JsonElement copia, IReadOnlySet<string> hashRami)
    {
        try
        {
            var copiaTc = copia.Deserialize<TestCase>(JsonIo.Options);
            return copiaTc != null && hashRami.Contains(TestCaseFingerprint.Compute(copiaTc));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private bool CopiaAssorbita(TestCase copia, TestCase canonico) =>
        _journal != null &&
        copia.Uid == canonico.Uid &&
        copia.RevisioneDati < canonico.RevisioneDati &&
        !_journal.HasKnownFork(canonico.Uid) &&
        _journal.IsKnownRevision(copia.Uid, TestCaseFingerprint.Compute(copia));

    private static void ClassificaDuplicatiEEscludiCopieDiConflitto(
        Dictionary<string, (DateTime Mtime, TestCase TestCase)> cache,
        Dictionary<string, long>? lunghezze,
        out List<TestCaseDuplicato> duplicatiVeri,
        out List<FileConProblemi> copieDiConflitto,
        Func<TestCase, TestCase, bool>? copiaAssorbita = null)
    {
        duplicatiVeri = new List<TestCaseDuplicato>();
        copieDiConflitto = new List<FileConProblemi>();
        var duplicatiGrezzi = DuplicateDetectionService.TrovaIdDuplicati(
            cache.Select(kv => (kv.Key, kv.Value.TestCase.Id)));

        foreach (var gruppo in duplicatiGrezzi)
        {
            var canonici = gruppo.Percorsi
                .Where(p => Path.GetFileName(p).Equals($"testcase_{gruppo.Id}.json", StringComparison.OrdinalIgnoreCase))
                .ToList();

            string? pathCanonico;
            TestCase? contenutoCanonico;
            List<string> copie;

            // Sposta-in-corso (revisione release/6.4.2, scenario "cambio Gamma/componente"): il criterio "canonico"
            // guarda solo il NOME file ("testcase_<Id>.json"), identico in qualunque cartella. Durante la
            // sincronizzazione di uno spostamento di Test, un'altra postazione può avere per un po' sia il file
            // nuovo (nella cartella di destinazione) sia il vecchio non ancora tombstone/rimosso (nella cartella
            // di origine): due path "canonici per nome" con lo STESSO Uid, non un vero Id duplicato. Se accade,
            // vince la versione più recente per Mtime (stessa regola già usata da RicostruisciLookup) e l'altra
            // diventa una "copia in transito", mai un ID duplicato ambiguo.
            if (canonici.Count >= 2 &&
                canonici.All(p => cache.TryGetValue(p, out var v) && v.TestCase.Uid == cache[canonici[0]].TestCase.Uid))
            {
                var vincitore = canonici.OrderByDescending(p => cache[p].Mtime).First();
                pathCanonico = vincitore;
                contenutoCanonico = cache[vincitore].TestCase;
                copie = gruppo.Percorsi.Where(p => !p.Equals(vincitore, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else
            {
                pathCanonico = canonici.Count == 1 ? canonici[0] : null;
                contenutoCanonico = pathCanonico != null && cache.TryGetValue(pathCanonico, out var can) ? can.TestCase : null;
                copie = pathCanonico != null
                    ? gruppo.Percorsi.Where(p => !p.Equals(pathCanonico, StringComparison.OrdinalIgnoreCase)).ToList()
                    : new List<string>();
            }

            var tutteStessoUid = contenutoCanonico != null && copie.Count > 0 &&
                copie.All(p => cache.TryGetValue(p, out var v) && v.TestCase.Uid == contenutoCanonico.Uid);

            if (pathCanonico == null || !tutteStessoUid)
            {
                duplicatiVeri.Add(gruppo);
                continue;
            }

            var nomeCanonico = Path.GetFileName(pathCanonico);
            var hashCanonico = TestCaseFingerprint.Compute(contenutoCanonico!);
            foreach (var pathCopia in copie)
            {
                var identico = cache.TryGetValue(pathCopia, out var copia) &&
                    TestCaseFingerprint.Compute(copia.TestCase).Equals(hashCanonico, StringComparison.OrdinalIgnoreCase);

                // Una copia "in transito" ha lo STESSO nome file del canonico (solo la cartella differisce): è
                // il residuo non ancora sincronizzato di uno spostamento, non una copia di conflitto OneDrive
                // (che invece rinomina il file, es. "testcase_T-0001-PCNOME.json").
                var èCopiaInTransito = Path.GetFileName(pathCopia).Equals(nomeCanonico, StringComparison.OrdinalIgnoreCase);
                string messaggio;
                bool richiedeAttenzione;
                if (èCopiaInTransito)
                {
                    var mtimeCopia = cache.TryGetValue(pathCopia, out var vOld) ? vOld.Mtime : DateTime.UtcNow;
                    var oltreTolleranza = DateTime.UtcNow - mtimeCopia >= FileProblemToleranceTracker.MinElapsed;
                    richiedeAttenzione = oltreTolleranza;
                    messaggio = oltreTolleranza
                        ? $"Spostamento di Test non completato ({gruppo.Id}): esiste ancora una copia nella cartella precedente ({pathCopia}) da oltre {FileProblemToleranceTracker.MinElapsed.TotalMinutes:0} minuti. Verifica manualmente: probabile sincronizzazione bloccata su un'altra postazione. Nessun dato è stato perso, il file resta sul disco."
                        : $"Spostamento di Test in corso ({gruppo.Id}): la cartella precedente ({pathCopia}) non è ancora stata rimossa dalla sincronizzazione. Nessuna azione richiesta, si risolve da solo a breve.";
                }
                else if (identico)
                {
                    richiedeAttenzione = false;
                    messaggio = $"Copia di conflitto OneDrive/SharePoint dello stesso Test ({gruppo.Id}), contenuto identico all'originale ({nomeCanonico}). Ignorabile: il file non è stato toccato.";
                }
                else if (copia.TestCase != null && copiaAssorbita?.Invoke(copia.TestCase, contenutoCanonico!) == true)
                {
                    // La copia contiene un salvataggio già noto al journal e il Test canonico è andato oltre
                    // (merge automatico o modifica successiva), senza fork aperti: nulla da recuperare.
                    richiedeAttenzione = false;
                    messaggio = $"Copia di conflitto OneDrive/SharePoint dello stesso Test ({gruppo.Id}) con una versione precedente già incorporata nell'originale ({nomeCanonico}). Ignorabile: il file non è stato toccato.";
                }
                else
                {
                    richiedeAttenzione = true;
                    messaggio = $"Copia di conflitto OneDrive/SharePoint dello stesso Test ({gruppo.Id}), CONTENUTO DIVERSO dall'originale ({nomeCanonico}). Verifica manualmente prima di ignorarla: nessun dato è stato perso, il file resta sul disco.";
                }

                copieDiConflitto.Add(new FileConProblemi { Percorso = pathCopia, Errore = messaggio, RilevatoIl = DateTime.Now, RichiedeAttenzione = richiedeAttenzione });
                cache.Remove(pathCopia);
                lunghezze?.Remove(pathCopia);
            }
        }
    }

    private static TestCase Clone(TestCase tc) =>
        JsonSerializer.Deserialize<TestCase>(JsonSerializer.Serialize(tc, JsonIo.Options), JsonIo.Options)!;

    private static Esecuzione CloneRevision(Esecuzione revision) =>
        JsonSerializer.Deserialize<Esecuzione>(JsonSerializer.Serialize(revision, JsonIo.Options), JsonIo.Options)!;

    /// <summary>
    /// Aggiornamento dell'indice DOPO che il JSON autorevole e' stato committato. Se fallisce, il salvataggio
    /// resta valido e autorevole e NON viene mai ripetuto "per riprovare SQLite": si invalida il read model e
    /// si pianifica la riconciliazione, cosi' nessuna pagina continua a trattare l'indice vecchio come valido.
    /// </summary>
    private void TryLocalIndex(Action action, string operation)
    {
        if (_localIndex == null) return;
        if (_indexRecovery == null)
        {
            try { action(); }
            catch (Exception ex) { CrashLogService.Registra($"Indice SQLite locale TestCase: {operation}", ex); }
            return;
        }

        // Il coordinatore registra il guasto, porta l'indice a Degradato (o avvia la ricostruzione se e'
        // corruzione) e la mutazione mancante viene recuperata dalla prossima riconciliazione completa.
        _indexRecovery.TryWrite($"TestCase {operation}", action);
    }

    private void ScheduleJournalPublish(JournalPendingWrite? pending)
    {
        if (pending == null || _journal == null || !WorkspaceWritePolicy.CanWrite(_paths)) return;

        // L'evento journal non deve precedere la receipt del commit che descrive. Se siamo dentro una Unit of
        // Work viene pubblicato solo dopo Commit; fuori da essa conserva il comportamento sincrono storico.
        if (WorkflowTransactionService.DeferAfterCommit(() => _journal.TryPublish(pending))) return;
        _journal.TryPublish(pending);
    }
}
