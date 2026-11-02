using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

public sealed class PhaseDLoadedEntityState
{
    public required Guid Uid { get; init; }
    public required string BaselinePayloadJson { get; init; }
    public required IReadOnlyDictionary<string, long> VersionVector { get; init; }

    // Normalizzazione differita (review bfbd590, F1): Load() itera l'intero catalogo, ma nella stragrande
    // maggioranza delle aperture l'utente non modifica quasi nessuna riga. Calcolare subito lo sha normalizzato
    // per OGNI riga pagherebbe una seconda deserializzazione dell'intero catalogo anche quando SavePatch/Save
    // toccheranno poi solo poche UID. Il valore viene quindi calcolato al primo accesso effettivo (dentro
    // SavePatch/Save, quando una UID e' davvero confrontata con la sua baseline) e cacheato qui una sola volta;
    // la cache per-istanza dell'engine (chiave storedSha256) evita comunque di ripetere il calcolo fra righe
    // diverse con lo stesso payload storico.
    private readonly Lazy<string> _baselinePayloadSha256 = null!; // impostato da BaselinePayloadSha256Factory (required init)

    public required Func<string> BaselinePayloadSha256Factory
    {
        init => _baselinePayloadSha256 = new Lazy<string>(value, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string BaselinePayloadSha256 => _baselinePayloadSha256.Value;
}

public sealed class PhaseDLoadedListState
{
    public required string SourceFingerprint { get; init; }
    public required IReadOnlyDictionary<Guid, PhaseDLoadedEntityState> Entities { get; init; }
}

/// <summary>Mutation di un patch gia' pianificate (verifiche superate) ma non ancora pubblicate nel journal, con l'engine del
/// catalogo che le ha prodotte: il salvataggio atomico su piu' cataloghi le pubblica tutte insieme in un solo batch.</summary>
internal sealed record PhaseDMasterPatchPianificato<T>(PhaseDMasterRepositoryEngine<T> Engine, IReadOnlyList<PhaseDMasterMutation> Mutations) where T : MasterEntity;

/// <summary>
/// Adapter Phase D per il contratto storico Load()/Save(List). Il caller continua a modificare liste normali,
/// ma il commit viene trasformato in un batch contenente esclusivamente UID create/modificate/eliminate.
/// Modifiche remote a UID diverse vengono conservate automaticamente; una modifica concorrente alla stessa
/// UID viene rifiutata se il payload remoto non coincide piu' con la baseline osservata dal caller.
///
/// Load/Save completi restano soltanto compatibilita per caller non ancora migrati. I percorsi ad alta crescita
/// usano SavePatch: il suo costo RAM/I/O di confronto e' proporzionale alla finestra/delta modificato, non alla
/// cardinalita totale del catalogo. Qualunque Save con expectedSnapshot esplicito viene automaticamente trattato
/// come patch di QUELLA baseline: una lista paginata non puo quindi diventare per errore una replacement globale.
/// </summary>
public sealed class PhaseDMasterRepositoryEngine<T> where T : MasterEntity
{
    private readonly DataPaths _paths;
    private readonly string _catalog;
    private readonly PhaseDMasterMaterializer _materializer;
    private readonly PhaseDMasterScalableReader _scalableReader;
    private readonly Predicate<T>? _preserveWhenOmitted;
    private readonly string? _localBaseDirectory;

    // Chiave: PayloadSha256 storico registrato nella testa (identifica univocamente il PayloadJson sorgente).
    // Valore: sha256 dello stesso payload ricalcolato con CanonicalPayload sul modello CORRENTE. Evita di
    // deserializzare/riserializzare/ri-hashare la stessa testa piu' volte quando Load() itera l'intero catalogo.
    private readonly ConcurrentDictionary<string, string> _normalizedShaCache = new(StringComparer.Ordinal);

    // Sottoinsieme delle chiavi di _normalizedShaCache per cui TryNormalize non e' riuscita a deserializzare il
    // payload storico nel modello corrente (vedi commento su NormalizedSha/TryNormalize): permette ai chiamanti
    // che rilevano una discrepanza di distinguere "vero conflitto concorrente" da "payload storico illeggibile"
    // e restituire un messaggio d'errore corretto invece del generico "altra postazione".
    private readonly ConcurrentDictionary<string, byte> _normalizedShaUnreadable = new(StringComparer.Ordinal);

    /// <summary>Solo test (6.6.0, fault injection): invocato per ogni UID elaborato da <see cref="SavePatch"/>, PRIMA di accodare
    /// il batch al journal. Un'eccezione simula un guasto alla riga k del set: deve lasciare il journal invariato (atomicita').
    /// Parametri: radice del workspace e UID. Filtrato per radice dai test, per non interferire con altri workspace in parallelo.</summary>
    internal static Action<string, Guid>? FaultInjectionPerUid;

    public PhaseDMasterRepositoryEngine(
        string root,
        string catalog,
        Predicate<T>? preserveWhenOmitted = null,
        string? localBaseDirectory = null)
    {
        _paths = new DataPaths(root);
        _catalog = catalog;
        // Il read-model resta sempre locale e ricostruibile. L'override serve ai test/diagnostica per evitare
        // di contaminare %LOCALAPPDATA% e non cambia in alcun modo il formato condiviso Phase D. Lo stesso
        // override viene passato anche al contatore locale del DeviceSequence (NextDeviceSequence), che vive
        // sotto %LOCALAPPDATA% nello stesso senso.
        _materializer = new PhaseDMasterMaterializer(root, localBaseDirectory);
        _scalableReader = new PhaseDMasterScalableReader(root, localBaseDirectory);
        _localBaseDirectory = localBaseDirectory;
        _preserveWhenOmitted = preserveWhenOmitted;
    }

    public (List<T> Items, PhaseDLoadedListState State) Load()
    {
        var snapshot = _materializer.LoadCatalog<T>(_paths.Root, _catalog);
        // tollerante:true — vedi commento su ValidateBusinessKeys: questo valida lo stato GIA' PERSISTITO del
        // catalogo (letto dal journal condiviso), non una lista che il chiamante sta per scrivere. Per il
        // catalogo tollerato una collisione preesistente non ancora riparata non deve mai bloccare la lettura.
        ValidateBusinessKeys(snapshot.Items, tollerante: true);
        var states = snapshot.Heads
            .Where(x => x.Value.Operation == PhaseDMasterMutationOperation.Upsert && !x.Value.HasConflict)
            .ToDictionary(
                x => x.Key,
                x => new PhaseDLoadedEntityState
                {
                    Uid = x.Key,
                    BaselinePayloadJson = x.Value.PayloadJson,
                    // Non calcolare qui: vedi commento su PhaseDLoadedEntityState.BaselinePayloadSha256Factory.
                    BaselinePayloadSha256Factory = () => NormalizedHeadSha(x.Value),
                    VersionVector = new Dictionary<string, long>(x.Value.VersionVector, StringComparer.Ordinal)
                });
        // Proiezione in lettura: l'ordine delle teste (ORDER BY entity_uid) non e' un ordine per l'utente. Nessun dato cambia.
        return (MasterCatalogOrdering.Ordina(snapshot.Items), new PhaseDLoadedListState
        {
            SourceFingerprint = snapshot.SourceFingerprint,
            Entities = states
        });
    }

    /// <summary>
    /// Commit bounded per editor/pagina. desiredWindow e baselineWindow descrivono SOLO la finestra che il
    /// caller ha aperto in modifica: omissione di una UID presente nella baseline significa delete di quella
    /// UID, ma nessuna entita fuori dalla baseline puo essere cancellata. Nuove UID e rename vengono verificati
    /// contro i proprietari globali delle sole chiavi business interessate. Il journal continua a essere la
    /// source of truth e la rilettura forte finale riguarda esclusivamente le UID mutate.
    /// </summary>
    /// <summary>Impronta economica (revisione+conteggio, nessuna materializzazione) dell'intero catalogo Phase D
    /// corrente: usata per rilevare una modifica remota avvenuta ovunque nel catalogo da quando una sessione di
    /// modifica e' iniziata, anche quando la finestra caricata da quella sessione (bounded/filtrata) e' molto
    /// piu' piccola dell'intero catalogo.</summary>
    public string GetCatalogSourceFingerprint() => _materializer.GetCatalogState(_paths.Root, _catalog).SourceFingerprint;

    /// <param name="recreateOverTombstoneUids">Uid per cui una creazione (nessuna baseline) sopra una testa tombstone e consentita:
    /// la nuova versione parte dal version vector del tombstone (stesso meccanismo del percorso Save senza snapshot, riga ~404).
    /// Opt-in del chiamante che sa che l'entita e legittimamente ricreata (es. archivio guasto di un requisito il cui Uid ritorna).</param>
    public void SavePatch(IReadOnlyList<T> desiredWindow, IReadOnlyList<T> baselineWindow, bool allowSilentRemoteMerge = true, string? baselineSourceFingerprint = null, IReadOnlySet<Guid>? recreateOverTombstoneUids = null)
    {
        var mutations = PianificaPatch(desiredWindow, baselineWindow, allowSilentRemoteMerge, baselineSourceFingerprint, recreateOverTombstoneUids);
        if (mutations.Count == 0) return;
        AppendMutations(mutations);
        VerifyCommittedMutations(mutations);
    }

    /// <summary>Calcolo delle mutation di un patch senza pubblicarle: stesse verifiche di SavePatch (fingerprint di catalogo,
    /// testa remota, baseline, chiavi business). Lo usa anche il salvataggio atomico su piu' cataloghi, che pubblica le
    /// mutation di tutti i cataloghi in un solo batch del journal.</summary>
    internal List<PhaseDMasterMutation> PianificaPatch(IReadOnlyList<T> desiredWindow, IReadOnlyList<T> baselineWindow, bool allowSilentRemoteMerge = true, string? baselineSourceFingerprint = null, IReadOnlySet<Guid>? recreateOverTombstoneUids = null)
    {
        ArgumentNullException.ThrowIfNull(desiredWindow);
        ArgumentNullException.ThrowIfNull(baselineWindow);
        WorkspaceWritePolicy.EnsureCanWrite(_paths, $"Salvataggio patch master Phase D {_catalog}");

        // L'UID delle nuove entita viene assegnata alla copia staged del caller; le baseline devono invece avere
        // identita persistita: senza non esiste una precondizione per-entita affidabile.
        foreach (var item in desiredWindow) MasterEntityIdentityService.GetOrCreate(item);
        MasterEntityIdentityService.EnsureUnique(desiredWindow, assignMissing: false);
        MasterEntityIdentityService.EnsureUnique(baselineWindow, assignMissing: false);
        // tollerante:true su ENTRAMBE — vedi commento su ValidateBusinessKeys. 'baselineWindow' e' la fotografia
        // di apertura del chiamante (tipicamente da un Load() gia' tollerante: puo' quindi legittimamente
        // contenere ancora una collisione preesistente non riparata). 'desiredWindow' e' cio' che il chiamante
        // vuole scrivere DOPO la propria mutazione: per il catalogo tollerato ConsolidaCollisioni/RemoveAll+Add
        // (ValutazioneRequisitoEcuVeicoloService) garantiscono che non introduca mai un duplicato al suo
        // interno, quindi tollerarlo qui non nasconde un vero bug del chiamante per QUESTO catalogo — e per gli
        // altri cataloghi il comportamento resta comunque fail-closed (tollerante si applica solo se
        // PhaseDMasterKeyCollisionTolerance.IsTolerant(_catalog)). La rete di sicurezza contro un chiamante che
        // sottomette due righe duplicate nella STESSA lista (qualunque catalogo) resta il controllo sempre
        // fail-closed su 'localItems' nel percorso Save() senza expectedSnapshot (vedi sotto).
        ValidateBusinessKeys(desiredWindow, tollerante: true);
        ValidateBusinessKeys(baselineWindow, tollerante: true);

        var desiredByUid = desiredWindow.ToDictionary(MasterEntityIdentityService.GetRequired);
        var baselineByUid = baselineWindow.ToDictionary(MasterEntityIdentityService.GetRequired);

        if (!allowSilentRemoteMerge)
        {
            if (baselineSourceFingerprint != null)
                EnsureCatalogoNonCambiatoDApertura(baselineSourceFingerprint);
            else
                EnsureNessunaModificaRemotaFuoriDallEditor(baselineByUid);
        }

        var candidateUids = new HashSet<Guid>(desiredByUid.Keys);
        candidateUids.UnionWith(baselineByUid.Keys);

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in desiredWindow)
        {
            var key = BusinessKey(item);
            if (!string.IsNullOrWhiteSpace(key)) keys.Add(key);
        }
        foreach (var item in baselineWindow)
        {
            var key = BusinessKey(item);
            if (!string.IsNullOrWhiteSpace(key)) keys.Add(key);
        }

        var remote = _scalableReader.LoadPatchState(_catalog, candidateUids, keys);
        var mutations = new List<PhaseDMasterMutation>();

        foreach (var uid in candidateUids)
        {
            FaultInjectionPerUid?.Invoke(_paths.Root, uid);
            desiredByUid.TryGetValue(uid, out var desired);
            baselineByUid.TryGetValue(uid, out var baseline);
            remote.Heads.TryGetValue(uid, out var remoteHead);

            if (baseline != null)
            {
                var baselinePayload = CanonicalPayload(baseline);
                var baselineSha = PhaseDMasterMutation.HashPayload(baselinePayload);
                var baselineKey = BusinessKey(baseline);

                if (desired == null)
                {
                    // Delete mirato: se e' gia cancellata e' idempotente; altrimenti la testa remota deve essere
                    // ESATTAMENTE quella osservata nella baseline locale.
                    if (remoteHead == null || remoteHead.Operation == PhaseDMasterMutationOperation.Delete) continue;
                    EnsureNonToccaTestaConcorrente(uid, remoteHead, intendedValue: null);
                    EnsureHeadMatchesBaseline(uid, remoteHead, baselineSha, baselineKey);
                    mutations.Add(CreateDelete(uid, remoteHead.VersionVector));
                    continue;
                }

                var desiredPayload = CanonicalPayload(desired);
                var desiredSha = PhaseDMasterMutation.HashPayload(desiredPayload);
                var desiredKey = BusinessKey(desired);

                // Nessun intento locale: non bloccare una modifica remota disgiunta arrivata nel frattempo.
                if (desiredSha.Equals(baselineSha, StringComparison.OrdinalIgnoreCase) &&
                    desiredKey.Equals(baselineKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (remoteHead != null) EnsureNonToccaTestaConcorrente(uid, remoteHead, desired);
                if (remoteHead == null || remoteHead.Operation == PhaseDMasterMutationOperation.Delete)
                    throw new MasterDataConflictException(_catalog);

                if (NormalizedHeadSha(remoteHead).Equals(desiredSha, StringComparison.OrdinalIgnoreCase) &&
                    remoteHead.BusinessKey.Equals(desiredKey, StringComparison.OrdinalIgnoreCase))
                    continue; // altro device ha gia pubblicato esattamente il nostro risultato.

                EnsureHeadMatchesBaseline(uid, remoteHead, baselineSha, baselineKey);
                EnsureBusinessKeyAvailable(remote.BusinessKeyOwners, desiredKey, uid);
                mutations.Add(CreateUpsert(desired, desiredPayload, desiredSha, remoteHead.VersionVector));
                continue;
            }

            if (desired == null) continue;
            if (remoteHead != null) EnsureNonToccaTestaConcorrente(uid, remoteHead, desired);

            // Nuova UID locale. Collisione UID o chiave business con un'entita diversa => fail closed.
            var newPayload = CanonicalPayload(desired);
            var newSha = PhaseDMasterMutation.HashPayload(newPayload);
            var newKey = BusinessKey(desired);
            if (remoteHead != null && remoteHead.Operation == PhaseDMasterMutationOperation.Delete && recreateOverTombstoneUids?.Contains(uid) == true)
            {
                EnsureBusinessKeyAvailable(remote.BusinessKeyOwners, newKey, uid);
                mutations.Add(CreateUpsert(desired, newPayload, newSha, remoteHead.VersionVector));
                continue;
            }
            if (remoteHead != null)
            {
                if (remoteHead.Operation == PhaseDMasterMutationOperation.Upsert &&
                    NormalizedHeadSha(remoteHead).Equals(newSha, StringComparison.OrdinalIgnoreCase) &&
                    remoteHead.BusinessKey.Equals(newKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                throw new MasterDataConflictException(_catalog);
            }

            EnsureBusinessKeyAvailable(remote.BusinessKeyOwners, newKey, uid);
            mutations.Add(CreateUpsert(
                desired,
                newPayload,
                newSha,
                new Dictionary<string, long>(StringComparer.Ordinal)));
        }

        return mutations;
    }

    /// <summary>
    /// Policy Master (feedback utente 2026-09-08): finche' la sessione di modifica non e' seguita da un Forza
    /// Sblocco, il salvataggio deve restare pessimistico rispetto a QUALUNQUE modifica remota avvenuta ovunque
    /// nel catalogo da quando la sessione e' iniziata — non solo alle righe che l'editor ha effettivamente
    /// visto. Confronta l'impronta economica dell'intero catalogo (revisione+conteggio, GetCatalogSourceFingerprint)
    /// con quella catturata dal caller all'apertura della sessione: qualunque scostamento, ovunque nel catalogo,
    /// blocca il salvataggio con MasterDataConflictException.
    /// </summary>
    private void EnsureCatalogoNonCambiatoDApertura(string baselineSourceFingerprint)
    {
        if (!GetCatalogSourceFingerprint().Equals(baselineSourceFingerprint, StringComparison.Ordinal))
            throw new MasterDataConflictException(_catalog);
    }

    /// <summary>
    /// Fallback usato solo quando il caller non e' in grado di fornire un'impronta di catalogo catturata
    /// all'apertura (baselineSourceFingerprint nullo, es. caller non ancora migrati). Piu' debole della
    /// verifica per impronta: rileva soltanto modifiche/rimozioni alle UID effettivamente presenti nella
    /// baseline passata, non un'aggiunta remota completamente fuori da quella finestra. Dal refactor "Edit
    /// Phase D master pages without full-catalog promotion" (91fc21f) la baseline di una sessione di modifica
    /// NON e' piu' garantita coincidere con l'intero catalogo (puo' essere una finestra filtrata/paginata): un
    /// confronto sul conteggio totale del catalogo produrrebbe falsi conflitti ogni volta che il catalogo e'
    /// piu' grande della finestra caricata, anche senza alcuna modifica remota reale.
    /// </summary>
    private void EnsureNessunaModificaRemotaFuoriDallEditor(IReadOnlyDictionary<Guid, T> baselineByUid)
    {
        var current = _materializer.LoadCatalog<T>(_paths.Root, _catalog).Items;
        var currentByUid = current.ToDictionary(MasterEntityIdentityService.GetRequired);

        foreach (var (uid, baselineItem) in baselineByUid)
        {
            if (!currentByUid.TryGetValue(uid, out var currentItem))
                throw new MasterDataConflictException(_catalog);
            var baselineSha = PhaseDMasterMutation.HashPayload(CanonicalPayload(baselineItem));
            var currentSha = PhaseDMasterMutation.HashPayload(CanonicalPayload(currentItem));
            if (!baselineSha.Equals(currentSha, StringComparison.OrdinalIgnoreCase))
                throw new MasterDataConflictException(_catalog);
        }
    }

    public void Save(
        List<T> localItems,
        PhaseDLoadedListState? loadedState,
        IReadOnlyList<T>? expectedSnapshot,
        IReadOnlyDictionary<Guid, PhaseDLoadedEntityState>? derivedEntityStates = null,
        bool allowSilentRemoteMerge = true,
        string? baselineSourceFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(localItems);
        WorkspaceWritePolicy.EnsureCanWrite(_paths, $"Salvataggio master Phase D {_catalog}");

        // Un caller esplicito ha priorita' sull'impronta gia' catturata da Load() per la stessa List (loadedState):
        // permette a un editor bounded di fornire la propria baseline anche quando la List passata non e'
        // quella tracciata da Load().
        var effectiveBaselineFingerprint = baselineSourceFingerprint ?? loadedState?.SourceFingerprint;

        // Un expectedSnapshot esplicito definisce esattamente l'insieme sul quale il caller autorizza mutate/
        // delete. Questo vale sia per una fotografia completa sia per una pagina bounded: SavePatch non deduce
        // mai cancellazioni da UID che non erano presenti nella baseline passata dal caller.
        if (expectedSnapshot != null)
        {
            SavePatch(localItems, expectedSnapshot, allowSilentRemoteMerge, effectiveBaselineFingerprint);
            return;
        }

        MasterEntityIdentityService.EnsureUnique(localItems, assignMissing: true);
        // SEMPRE fail-closed (nessuna tolleranza, per QUALUNQUE catalogo incluso quello tollerato): rifiuta un
        // chiamante che sottomette due righe diverse con la stessa chiave business nella STESSA lista in UNA
        // sola chiamata Save — un bug del chiamante (import, refactor futuro), mai la race multi-postazione che
        // questo catalogo tollera (che nasce SEMPRE da due Save SEPARATE). Rete di sicurezza verificata
        // direttamente da ValutazioneRequisitoEcuVeicoloUnisciDividiTests.ValidateBusinessKeys_ReteDiSicurezza_
        // RestaAttivaSuUnSaveCheAggiraIlConsolidamento.
        ValidateBusinessKeys(localItems);
        var current = _materializer.LoadCatalog<T>(_paths.Root, _catalog);
        // tollerante:true — stesso motivo di Load(): 'current.Items' e' lo stato GIA' PERSISTITO (prima di
        // applicare 'localItems'), non l'input del chiamante.
        ValidateBusinessKeys(current.Items, tollerante: true);
        var currentByUid = current.Items.ToDictionary(MasterEntityIdentityService.GetRequired);
        var currentHeads = current.Heads;

        // Confronta l'impronta dell'intero catalogo catturata all'apertura della sessione (Load() o baseline
        // esplicita del caller) con quella corrente: rileva una modifica remota avvenuta OVUNQUE nel catalogo,
        // non solo alle UID presenti nella baseline. Se non e' disponibile alcuna impronta (caller legacy senza
        // loadedState/baseline esplicita), ripiega sul confronto per-UID piu' debole.
        IReadOnlyDictionary<Guid, PhaseDLoadedEntityState>? baselineStates = loadedState?.Entities;
        if (!allowSilentRemoteMerge)
        {
            if (effectiveBaselineFingerprint != null)
            {
                if (!current.SourceFingerprint.Equals(effectiveBaselineFingerprint, StringComparison.Ordinal))
                    throw new MasterDataConflictException(_catalog);
            }
            else if (baselineStates != null)
            {
                foreach (var (uid, state) in baselineStates)
                {
                    if (!currentByUid.TryGetValue(uid, out var currentItem))
                        throw new MasterDataConflictException(_catalog);
                    var currentSha = PhaseDMasterMutation.HashPayload(CanonicalPayload(currentItem));
                    if (!state.BaselinePayloadSha256.Equals(currentSha, StringComparison.OrdinalIgnoreCase))
                        throw new MasterDataConflictException(_catalog);
                }
            }
        }
        var localByUid = localItems.ToDictionary(MasterEntityIdentityService.GetRequired);
        var mutations = new List<PhaseDMasterMutation>();
        var finalActive = new Dictionary<Guid, T>(currentByUid);
        var candidateUids = new HashSet<Guid>(localByUid.Keys);

        // Senza expectedSnapshot, soltanto una lista caricata integralmente autorizza delete per omissione.
        // Una lista derivata/filtrata puo' invece fare upsert/add senza trasformare il filtro in delete.
        var fullReplacementIntent = loadedState != null;
        if (fullReplacementIntent)
        {
            var baselineUids = baselineStates?.Keys ?? Array.Empty<Guid>();
            candidateUids.UnionWith(baselineUids);
        }

        foreach (var uid in candidateUids)
        {
            localByUid.TryGetValue(uid, out var local);
            currentByUid.TryGetValue(uid, out var remoteItem);
            currentHeads.TryGetValue(uid, out var remoteHead);

            var baseline = ResolveBaseline(uid, baselineStates, expectedPayloadByUid: null, derivedEntityStates);
            if (local == null)
            {
                if (!fullReplacementIntent || baseline == null) continue;
                if (remoteItem == null || remoteHead == null || remoteHead.Operation == PhaseDMasterMutationOperation.Delete)
                {
                    finalActive.Remove(uid);
                    continue;
                }
                if (_preserveWhenOmitted != null && _preserveWhenOmitted(remoteItem)) continue;
                EnsureNonToccaTestaConcorrente(uid, remoteHead, intendedValue: null);
                EnsureRemoteStillMatchesBaseline(uid, remoteHead, baseline);
                mutations.Add(CreateDelete(uid, remoteHead.VersionVector));
                finalActive.Remove(uid);
                continue;
            }

            var localPayload = CanonicalPayload(local);
            var localSha = PhaseDMasterMutation.HashPayload(localPayload);
            if (baseline == null)
            {
                // Nuova UID locale. Se la stessa UID e' comparsa nel remoto, accettiamo solo identico payload.
                if (remoteHead != null) EnsureNonToccaTestaConcorrente(uid, remoteHead, local);
                if (remoteHead != null && remoteHead.Operation != PhaseDMasterMutationOperation.Delete)
                {
                    if (NormalizedHeadSha(remoteHead).Equals(localSha, StringComparison.OrdinalIgnoreCase))
                    {
                        finalActive[uid] = local;
                        continue;
                    }
                    throw new MasterDataConflictException(_catalog);
                }
                var baseVector = remoteHead?.VersionVector ?? new Dictionary<string, long>(StringComparer.Ordinal);
                mutations.Add(CreateUpsert(local, localPayload, localSha, baseVector));
                finalActive[uid] = local;
                continue;
            }

            if (localSha.Equals(baseline.BaselinePayloadSha256, StringComparison.OrdinalIgnoreCase))
            {
                // Nessun intento locale su questa UID: qualunque versione remota resta autorevole.
                continue;
            }

            if (remoteHead != null) EnsureNonToccaTestaConcorrente(uid, remoteHead, local);
            if (remoteHead == null || remoteHead.Operation == PhaseDMasterMutationOperation.Delete)
                throw new MasterDataConflictException(_catalog);

            if (NormalizedHeadSha(remoteHead).Equals(localSha, StringComparison.OrdinalIgnoreCase))
            {
                // L'altro device ha gia' prodotto esattamente lo stesso risultato.
                finalActive[uid] = local;
                continue;
            }

            EnsureRemoteStillMatchesBaseline(uid, remoteHead, baseline);
            mutations.Add(CreateUpsert(local, localPayload, localSha, remoteHead.VersionVector));
            finalActive[uid] = local;
        }

        // tollerante:true — 'finalActive' e' il risultato del MERGE fra stato remoto e intento locale (non la
        // lista sottomessa dal chiamante, gia' validata fail-closed sopra come 'localItems'): due Save separate
        // di due postazioni diverse che aggiungono indipendentemente una riga nuova con la stessa chiave
        // business possono qui coesistere temporaneamente per il catalogo tollerato, esattamente come
        // nell'equivalente EnsureBusinessKeyAvailable di SavePatch.
        ValidateBusinessKeys(finalActive.Values, tollerante: true);
        if (mutations.Count == 0) return;

        AppendMutations(mutations);

        // Percorso storico: mantiene la rilettura completa finche' i caller legacy non sono migrati a SavePatch.
        var after = _materializer.LoadCatalog<T>(_paths.Root, _catalog);
        foreach (var mutation in mutations)
        {
            if (!after.Heads.TryGetValue(mutation.EntityUid, out var head) || head.HasConflict)
                throw new PhaseDMasterConflictException(_catalog, new[] { mutation.EntityUid });
            // Confronto volutamente RAW, senza NormalizedSha (review bfbd590, F4): 'mutation' e' la mutation
            // appena creata da QUESTO stesso processo con CreateUpsert/CanonicalPayload sul modello CORRENTE, e
            // 'head' e' la rilettura immediata di cio' che questo stesso commit ha appena scritto. Non c'e' uno
            // storico di versioni precedenti in mezzo: i due sha sono gia' calcolati con lo stesso schema, quindi
            // normalizzare non aggiungerebbe nulla. La normalizzazione serve solo quando si confronta con una
            // testa remota che potrebbe provenire da un payload scritto da una versione PRECEDENTE del modello
            // (vedi NormalizedHeadSha sopra) — qui non e' questo il caso.
            if (mutation.Operation == PhaseDMasterMutationOperation.Upsert &&
                !head.PayloadSha256.Equals(mutation.PayloadSha256, StringComparison.OrdinalIgnoreCase) &&
                !PhaseDVersionVector.Dominates(head.VersionVector, mutation.VersionVector))
                throw new PhaseDMasterConflictException(_catalog, new[] { mutation.EntityUid });
        }
    }

    private void AppendMutations(List<PhaseDMasterMutation> mutations) =>
        PhaseDMasterBatchWriter.Pubblica(_paths, _localBaseDirectory, mutations);

    /// <summary>Percorsi del workspace e directory locale del contatore: servono al salvataggio atomico su piu' cataloghi per
    /// pubblicare un solo batch con le mutation di tutti i cataloghi coinvolti.</summary>
    internal DataPaths Percorsi => _paths;
    internal string? DirectoriaLocaleBase => _localBaseDirectory;

    /// <summary>Verifica post-pubblicazione delle sole mutation di questo catalogo (quando il batch e' condiviso con altri cataloghi).</summary>
    internal void VerificaPubblicate(IReadOnlyList<PhaseDMasterMutation> mutations)
    {
        if (mutations.Count > 0) VerifyCommittedMutations(mutations);
    }

    private void VerifyCommittedMutations(IReadOnlyList<PhaseDMasterMutation> mutations)
    {
        var affected = mutations.Select(x => x.EntityUid).Distinct().ToArray();
        var keys = mutations
            .Where(x => x.Operation == PhaseDMasterMutationOperation.Upsert && !string.IsNullOrWhiteSpace(x.BusinessKey))
            .Select(x => x.BusinessKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var after = _scalableReader.LoadPatchState(_catalog, affected, keys);
        foreach (var mutation in mutations)
        {
            if (!after.Heads.TryGetValue(mutation.EntityUid, out var head) || head.HasConflict)
                throw new PhaseDMasterConflictException(_catalog, new[] { mutation.EntityUid });

            if (mutation.Operation == PhaseDMasterMutationOperation.Delete)
            {
                if (head.Operation != PhaseDMasterMutationOperation.Delete &&
                    !PhaseDVersionVector.Dominates(head.VersionVector, mutation.VersionVector))
                    throw new PhaseDMasterConflictException(_catalog, new[] { mutation.EntityUid });
                continue;
            }

            if (head.Operation != PhaseDMasterMutationOperation.Upsert)
                throw new PhaseDMasterConflictException(_catalog, new[] { mutation.EntityUid });
            // Confronto volutamente RAW (review bfbd590, F4): stessa motivazione del blocco analogo in Save()
            // sopra. 'mutation' e' stata prodotta da questo processo su modello corrente e 'head' e' la
            // rilettura immediata dello stesso commit appena pubblicato via AppendMutations — non uno storico
            // di una versione precedente del modello, quindi non serve NormalizedSha qui.
            if (!head.PayloadSha256.Equals(mutation.PayloadSha256, StringComparison.OrdinalIgnoreCase) &&
                !PhaseDVersionVector.Dominates(head.VersionVector, mutation.VersionVector))
                throw new PhaseDMasterConflictException(_catalog, new[] { mutation.EntityUid });
        }
    }

    private void EnsureBusinessKeyAvailable(
        IReadOnlyDictionary<string, Guid> owners,
        string key,
        Guid uid)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        // Vedi PhaseDMasterKeyCollisionTolerance: per il catalogo tollerato la responsabilita' di non
        // introdurre/lasciare una collisione di chiave business e' del servizio (ConsolidaCollisioni prima
        // di Save), non di questo guard generico — che qui diventa volutamente un no-op.
        if (PhaseDMasterKeyCollisionTolerance.IsTolerant(_catalog)) return;
        if (owners.TryGetValue(key, out var owner) && owner != uid)
            throw new PhaseDMasterBusinessKeyConflictException(typeof(T).Name, key, owner, uid);
    }

    /// <summary>
    /// Guardia dei conflitti PER VOCE. Una scrittura che CAMBIA una voce con teste concorrenti non risolte (o la elimina)
    /// lancia <see cref="PhaseDMasterConflictException"/>: non e' last-writer-wins, la voce resta in conflitto finche' un
    /// Master non sceglie la versione (<see cref="PhaseDMasterConflictResolver"/>). Una scrittura che lascia la voce
    /// esattamente come la mostra la lettura (testa deterministica) non produce mutation ed e' accettata, cosi' un Save
    /// dell'intera lista non fallisce per una voce che nessuno ha toccato. Le altre voci non sono coinvolte.
    /// </summary>
    private void EnsureNonToccaTestaConcorrente(Guid uid, PhaseDMaterializedHead remoteHead, T? intendedValue)
    {
        if (!remoteHead.HasConflict) return;
        if (intendedValue == null)
        {
            // Eliminazione (o assenza) richiesta: e' un no-op solo se la lettura mostra gia' la voce come eliminata.
            if (remoteHead.Operation == PhaseDMasterMutationOperation.Delete) return;
            throw new PhaseDMasterConflictException(_catalog, new[] { uid });
        }

        if (remoteHead.Operation == PhaseDMasterMutationOperation.Upsert &&
            NormalizedHeadSha(remoteHead).Equals(PhaseDMasterMutation.HashPayload(CanonicalPayload(intendedValue)), StringComparison.OrdinalIgnoreCase) &&
            remoteHead.BusinessKey.Equals(BusinessKey(intendedValue), StringComparison.OrdinalIgnoreCase))
            return;
        throw new PhaseDMasterConflictException(_catalog, new[] { uid });
    }

    private void EnsureHeadMatchesBaseline(Guid uid, PhaseDMaterializedHead remoteHead, string baselineSha, string baselineKey)
    {
        var normalizedHeadSha = NormalizedHeadSha(remoteHead);
        if (remoteHead.Operation != PhaseDMasterMutationOperation.Upsert ||
            !normalizedHeadSha.Equals(baselineSha, StringComparison.OrdinalIgnoreCase) ||
            !remoteHead.BusinessKey.Equals(baselineKey, StringComparison.OrdinalIgnoreCase))
        {
            if (remoteHead.Operation == PhaseDMasterMutationOperation.Upsert && IsHeadPayloadUnreadable(remoteHead))
                throw new MasterDataUnreadableHistoricalPayloadException(_catalog, uid, baselineKey);
            throw new MasterDataConflictException(_catalog);
        }
    }

    private PhaseDLoadedEntityState? ResolveBaseline(
        Guid uid,
        IReadOnlyDictionary<Guid, PhaseDLoadedEntityState>? listState,
        IReadOnlyDictionary<Guid, string>? expectedPayloadByUid,
        IReadOnlyDictionary<Guid, PhaseDLoadedEntityState>? derivedStates)
    {
        if (listState != null && listState.TryGetValue(uid, out var listBaseline)) return listBaseline;
        if (derivedStates != null && derivedStates.TryGetValue(uid, out var derived)) return derived;
        if (expectedPayloadByUid != null && expectedPayloadByUid.TryGetValue(uid, out var payload))
            return new PhaseDLoadedEntityState
            {
                Uid = uid,
                BaselinePayloadJson = payload,
                BaselinePayloadSha256Factory = () => NormalizedSha(payload, PhaseDMasterMutation.HashPayload(payload)),
                VersionVector = new Dictionary<string, long>(StringComparer.Ordinal)
            };
        return null;
    }

    private void EnsureRemoteStillMatchesBaseline(Guid uid, PhaseDMaterializedHead remoteHead, PhaseDLoadedEntityState baseline)
    {
        if (!NormalizedHeadSha(remoteHead).Equals(baseline.BaselinePayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (IsHeadPayloadUnreadable(remoteHead))
                throw new MasterDataUnreadableHistoricalPayloadException(_catalog, uid, BaselineLabel(baseline));
            throw new MasterDataConflictException(_catalog);
        }
        if (baseline.VersionVector.Count > 0 && !PhaseDVersionVector.Equivalent(remoteHead.VersionVector, baseline.VersionVector))
            throw new MasterDataConflictException(_catalog);
    }

    private PhaseDMasterMutation CreateUpsert(
        T item,
        string payload,
        string payloadSha,
        IReadOnlyDictionary<string, long> baseVector)
    {
        var deviceId = DeviceIdentityService.GetOrCreate();
        return new PhaseDMasterMutation
        {
            MutationId = Guid.NewGuid(),
            Catalog = _catalog,
            EntityUid = MasterEntityIdentityService.GetRequired(item),
            Operation = PhaseDMasterMutationOperation.Upsert,
            VersionVector = PhaseDVersionVector.Increment(baseVector, deviceId),
            PayloadJson = payload,
            PayloadSha256 = payloadSha,
            BusinessKey = BusinessKey(item),
            TimestampUtc = DateTime.UtcNow
        };
    }

    private PhaseDMasterMutation CreateDelete(Guid uid, IReadOnlyDictionary<string, long> baseVector)
    {
        var deviceId = DeviceIdentityService.GetOrCreate();
        return new PhaseDMasterMutation
        {
            MutationId = Guid.NewGuid(),
            Catalog = _catalog,
            EntityUid = uid,
            Operation = PhaseDMasterMutationOperation.Delete,
            VersionVector = PhaseDVersionVector.Increment(baseVector, deviceId),
            PayloadJson = "",
            PayloadSha256 = "",
            BusinessKey = "",
            TimestampUtc = DateTime.UtcNow
        };
    }

    private static string CanonicalPayload(T item) => JsonSerializer.Serialize(item, JsonIo.CompactOptions);

    /// <summary>
    /// Un payload Phase D storico puo' precedere l'aggiunta di un campo al modello (es. CreatoDa, introdotto il
    /// 2026-09-18): il PayloadSha256 registrato nella sua testa resta calcolato sul JSON dell'epoca, che non
    /// contiene il campo nuovo. Confrontare quello sha grezzo con quello ricalcolato OGGI da CanonicalPayload
    /// (che include "CreatoDa":"" o qualunque altro campo aggiunto in futuro) produce SEMPRE una discrepanza
    /// anche a payload logicamente identico: qualunque modifica di una riga scritta prima dell'aggiunta del
    /// campo veniva quindi rifiutata come falso MasterDataConflictException, anche senza alcuna modifica remota
    /// reale (bug presente anche in 6.2.5). La correzione: ri-derivare lo sha "di oggi" anche per la testa
    /// remota, deserializzando il suo payload storico nel modello CORRENTE (i campi mancanti prendono il default
    /// C#, esattamente come avviene per l'oggetto locale) e ricalcolando l'hash sulla stessa forma canonica usata
    /// per baseline/desired. Generalizza a qualunque campo futuro, non solo CreatoDa. Un vero conflitto
    /// concorrente resta rilevato: se due device hanno scritto payload logicamente diversi, gli sha normalizzati
    /// restano diversi.
    ///
    /// Fail-safe (review bfbd590, F2): un payload storico puo' in teoria non essere piu' deserializzabile nel
    /// modello corrente (es. un campo diventato 'required' senza default, o un cambio di tipo incompatibile in
    /// una versione futura/precedente incompatibile) — un caso ben piu' raro del semplice "campo mancante" gia'
    /// gestito sopra, ma che PRIMA di questo fix non poteva mai verificarsi perche' SavePatch confrontava solo
    /// sha grezzi senza mai deserializzare. Se la normalizzazione fallisce, non deve mai propagare
    /// un'eccezione tecnica (JsonException/NotSupportedException) che spacchi l'intero SavePatch anche per righe
    /// che l'utente non stava toccando, e non deve MAI trattare silenziosamente la riga come "combacia" (questo
    /// mascererebbe un conflitto vero). Il fallback e' quindi il comportamento pre-fix per la sola riga
    /// incriminata: si ripiega sullo sha grezzo storico, che quasi certamente NON combacera' con lo sha
    /// canonico ricalcolato oggi per baseline/desired, e il chiamante (EnsureHeadMatchesBaseline,
    /// EnsureRemoteStillMatchesBaseline, o il confronto diretto in SavePatch/Save) traduce la discrepanza in un
    /// MasterDataConflictException gestito e con messaggio chiaro, mai in un crash.
    /// </summary>
    private string NormalizedSha(string payloadJson, string storedSha256)
    {
        if (_normalizedShaCache.TryGetValue(storedSha256, out var cached)) return cached;
        var normalized = TryNormalize(payloadJson);
        if (normalized == null)
        {
            _normalizedShaUnreadable[storedSha256] = 0;
            normalized = storedSha256;
        }
        _normalizedShaCache[storedSha256] = normalized;
        return normalized;
    }

    private string? TryNormalize(string payloadJson)
    {
        try
        {
            var item = JsonSerializer.Deserialize<T>(payloadJson, JsonIo.Options);
            return item == null ? null : PhaseDMasterMutation.HashPayload(CanonicalPayload(item));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException)
        {
            return null;
        }
    }

    private string NormalizedHeadSha(PhaseDMaterializedHead head) => NormalizedSha(head.PayloadJson, head.PayloadSha256);

    /// <summary>Vero se l'ultima normalizzazione del payload storico di questa testa (NormalizedHeadSha) non e'
    /// riuscita a deserializzarlo nel modello corrente. Richiede che NormalizedHeadSha sia gia' stata invocata
    /// per questa testa nello stesso confronto (lo sono sempre entrambi i chiamanti fail-closed sotto).</summary>
    private bool IsHeadPayloadUnreadable(PhaseDMaterializedHead head) =>
        _normalizedShaUnreadable.ContainsKey(head.PayloadSha256);

    /// <summary>Etichetta leggibile (chiave business) per il messaggio d'errore, derivata dal payload di
    /// baseline: e' sempre lo stesso payload scritto da QUESTO processo/sessione, quindi sempre deserializzabile
    /// nel modello corrente. Il try/catch resta puramente difensivo.</summary>
    private string? BaselineLabel(PhaseDLoadedEntityState baseline)
    {
        try
        {
            var item = JsonSerializer.Deserialize<T>(baseline.BaselinePayloadJson, JsonIo.Options);
            return item == null ? null : BusinessKey(item);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Rifiuta due entita' diverse con la stessa chiave business ALL'INTERNO di <paramref name="items"/>.
    /// <paramref name="tollerante"/> distingue DUE usi ben diversi di questo stesso controllo (fix 6.4.0, review
    /// concorrenza — vedi ValutazioneRequisitoEcuVeicoloUnisciDividiTests.ValidateBusinessKeys_ReteDiSicurezza_
    /// RestaAttivaSuUnSaveCheAggiraIlConsolidamento, che questo parametro esiste per non rompere):
    /// - tollerante:true — SOLO per validare lo stato GIA' PERSISTITO del catalogo tollerato appena letto dal
    ///   journal condiviso (Load(), o 'current.Items' prima di applicare un Save senza expectedSnapshot): una
    ///   collisione preesistente non ancora riparata da ConsolidaCollisioni non deve mai bloccare la lettura,
    ///   altrimenti il catalogo resterebbe illeggibile esattamente come nel bug originale.
    /// - tollerante:false (default) — per qualunque lista/finestra SOTTOMESSA dal chiamante (SavePatch
    ///   desiredWindow/baselineWindow, il 'finalActive' finale di Save senza expectedSnapshot): due entita'
    ///   diverse con la stessa chiave nello STESSO Save sono sempre un bug del chiamante (import, bug futuro),
    ///   non la race multi-postazione che questo catalogo tollera — resta fail-closed per QUALUNQUE catalogo,
    ///   incluso quello tollerato (Propaga/ImpostaManuale/PropagaMassiva non producono mai questo caso: ognuno
    ///   scrive al piu' una riga nuova per cella, RemoveAll+Add, e ConsolidaCollisioni ripulisce prima di tutto
    ///   quanto gia' presente nella finestra caricata).</summary>
    private void ValidateBusinessKeys(IEnumerable<T> items, bool tollerante = false)
    {
        if (tollerante && PhaseDMasterKeyCollisionTolerance.IsTolerant(_catalog)) return;
        var seen = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var key = BusinessKey(item);
            if (string.IsNullOrWhiteSpace(key)) continue;
            var uid = MasterEntityIdentityService.GetRequired(item);
            if (seen.TryGetValue(key, out var other) && other != uid)
                throw new PhaseDMasterBusinessKeyConflictException(typeof(T).Name, key, other, uid);
            seen[key] = uid;
        }
    }

    private static string BusinessKey(T item) => PhaseDMasterBusinessKeyService.Get(item);
}

public sealed class PhaseDMasterBusinessKeyConflictException : IOException
{
    /// <summary>Chiave business in collisione: permette alla UI di nominarla in un messaggio localizzato.</summary>
    public string BusinessKey { get; }

    public PhaseDMasterBusinessKeyConflictException(string catalog, string key, Guid first, Guid second)
        : base($"Collisione Phase D sulla chiave business '{key}' del catalogo {catalog}: UID {first:N} e {second:N}. Nessun merge automatico applicato.")
    {
        BusinessKey = key;
    }
}
