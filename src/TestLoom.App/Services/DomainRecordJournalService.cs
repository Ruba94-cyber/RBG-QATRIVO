using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestLoom.App.Services;

public enum RecordJournalOperation { Create, Save, Delete, Resolve }

/// <summary>Evento immutabile di commit di un record NC/CR/Segnalazione, scritto solo dal device che lo ha prodotto.</summary>
public sealed class RecordJournalEvent
{
    public int Schema { get; set; } = DomainRecordJournalService.JournalSchemaVersion;
    public string EventId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Domain { get; set; } = "";
    public Guid EntityUid { get; set; }
    public string EntityId { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RecordJournalOperation Operation { get; set; }
    public long? BaseRevision { get; set; }
    public string? BaseHash { get; set; }
    public long? NewRevision { get; set; }
    public string? NewHash { get; set; }
    public DateTime Utc { get; set; }
    public string? Operator { get; set; }
    /// <summary>Solo per Resolve: chiave del fork chiuso dal Master e hash della versione scelta.</summary>
    public string? ResolvedForkKey { get; set; }
    public string? ChosenHash { get; set; }
}

public sealed class RecordForkBranch
{
    public string EventId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RecordJournalOperation Operation { get; set; }
    public long? NewRevision { get; set; }
    public string? NewHash { get; set; }
    public DateTime Utc { get; set; }
    public string? Operator { get; set; }
    public bool IsDelete => Operation == RecordJournalOperation.Delete;
}

/// <summary>Due o più commit (anche da postazioni diverse) partiti dalla STESSA versione base con esiti diversi:
/// almeno uno è stato sovrascritto (o resuscitato/cancellato) dalla sincronizzazione OneDrive.</summary>
public sealed class RecordFork
{
    public string Domain { get; set; } = "";
    public Guid EntityUid { get; set; }
    public string EntityId { get; set; } = "";
    public long? BaseRevision { get; set; }
    public string BaseHash { get; set; } = "";
    public List<RecordForkBranch> Branches { get; set; } = new();
    public DateTime DetectedUtc { get; set; }

    /// <summary>Chiave della risoluzione: include l'insieme dei rami. Un ramo arrivato dopo la risoluzione la riapre.</summary>
    [JsonIgnore]
    public string Key => DomainRecordJournalService.ForkKeyOf(Domain, EntityUid, BaseRevision, BaseHash,
        Branches.Select(DomainRecordJournalService.BranchIdentity));

    /// <summary>Identità stabile del fork: stessa base dello stesso record.</summary>
    [JsonIgnore]
    public string ForkId => $"{Domain}|{EntityUid:N}|{BaseRevision?.ToString() ?? "-"}|{BaseHash.ToUpperInvariant()}";
}

public sealed class RecordForkOpenException : IOException
{
    public RecordForkOpenException(string domain, string id)
        : base($"Il record {id} ({domain}) ha versioni divergenti fra postazioni. " +
               "Tutte le versioni disponibili sono conservate: un Master deve scegliere quella corretta " +
               "(Impostazioni → Conflitti fra postazioni) prima di nuove modifiche.") { }
}

/// <summary>
/// Journal per-device dei commit di Non Conformità, Change Request e Segnalazioni (stesso principio del journal
/// TestCase, R9). Non sostituisce i JSON autorevoli e non è un lock distribuito: OneDrive resta last-writer-wins
/// sul singolo file. Il journal rende però RILEVABILE e RECUPERABILE una modifica sovrascritta dopo che i log
/// dei device sono stati sincronizzati:
/// <list type="bullet">
/// <item>ogni device scrive SOLO in <c>_ditm/record-journal/&lt;device&gt;/</c> (append + rotazione bounded), quindi
/// il journal non diventa un nuovo file conteso fra PC;</item>
/// <item>il payload completo di ogni commit resta in locale (fuori OneDrive) e viene pubblicato in
/// <c>_ditm/conflicts/records/</c> solo quando un fork che lo riguarda è rilevato (nessun sidecar per i salvataggi normali);</item>
/// <item>un record con fork aperto è bloccato in scrittura finché un Master non sceglie la versione (evento Resolve).</item>
/// </list>
/// Limite dichiarato: una modifica presente solo su un PC che non ha ancora sincronizzato il proprio log resta
/// invisibile agli altri finché il log non arriva; il payload perso diventa pubblicabile solo quando quel PC
/// riapre TestLoom dopo la sincronizzazione.
/// </summary>
public sealed class DomainRecordJournalService
{
    public const int JournalSchemaVersion = 1;
    public const string NonConformitaDomain = "nonconformita";
    public const string ChangeRequestDomain = "change-request";
    public const string SegnalazioneDomain = "segnalazione";

    private const long DefaultRotateAfterBytes = 512 * 1024;
    private const int DefaultSegmentsToKeep = 8;
    private const int LocalPayloadRetentionDays = 120;

    private static readonly AsyncLocal<string?> ResolutionScope = new();
    private static readonly AsyncLocal<HashSet<string>?> ResolutionBranchHashes = new();
    private static readonly JsonSerializerOptions LineOptions = new(JsonIo.CompactOptions);

    private readonly object _gate = new();
    private readonly DataPaths _paths;
    private readonly string _journalRoot;
    private readonly string _deviceDirectory;
    private readonly string _currentFile;
    private readonly string _stateFile;
    private readonly string _localDirectory;
    private readonly string _outboxFile;
    // Eventi preparati per un salvataggio ancora in corso (Prepare -> Publish/Cancel). Finche' il record non e' stato
    // scritto, CommitWasApplied li considera non avvenuti: FlushOutbox, anche da un Refresh in background, NON deve
    // scartarli. Solo memoria: dopo un riavvio un commit non completato viene scartato come prima.
    private readonly HashSet<string> _inVolo = new(StringComparer.Ordinal);
    private readonly long _rotateAfterBytes;
    private readonly int _segmentsToKeep;
    private IReadOnlyList<RecordFork> _openForks = Array.Empty<RecordFork>();
    private HashSet<string> _blockedKeys = new(StringComparer.OrdinalIgnoreCase);
    // "domain|uidN" -> hash dei rami di fork GIA' RISOLTI (ricavati dalle chiavi di risoluzione persistite).
    private Dictionary<string, HashSet<string>> _ramiChiusi = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ((long Length, DateTime Mtime) Stamp, List<RecordJournalEvent> Events)> _fileCache =
        new(StringComparer.OrdinalIgnoreCase);

    public string DeviceId { get; }
    public IReadOnlyList<RecordFork> OpenForks { get { lock (_gate) return _openForks; } }

    public DomainRecordJournalService(DataPaths paths, string localWorkspaceDirectory,
        string? deviceIdOverride = null, long? rotateAfterBytesOverride = null, int? segmentsToKeepOverride = null)
    {
        _paths = paths;
        DeviceId = string.IsNullOrWhiteSpace(deviceIdOverride) ? DeviceIdentityService.GetOrCreate() : deviceIdOverride.Trim();
        _rotateAfterBytes = Math.Max(256, rotateAfterBytesOverride ?? DefaultRotateAfterBytes);
        _segmentsToKeep = Math.Max(1, segmentsToKeepOverride ?? DefaultSegmentsToKeep);
        _journalRoot = Path.Combine(paths.Root, "_ditm", "record-journal");
        _deviceDirectory = Path.Combine(_journalRoot, DeviceId);
        _currentFile = Path.Combine(_deviceDirectory, "current.ndjson");
        _stateFile = Path.Combine(_deviceDirectory, "forks.json");
        _localDirectory = Path.Combine(localWorkspaceDirectory, "record-journal");
        _outboxFile = Path.Combine(_localDirectory, "outbox.ndjson");
        // Solo storage locale nel costruttore: nessun accesso OneDrive nel percorso critico di avvio.
        Directory.CreateDirectory(_localDirectory);
    }

    public static string BranchIdentity(RecordForkBranch b) =>
        b.Operation == RecordJournalOperation.Delete ? "DELETE" : (b.NewHash ?? "");

    public static string ForkKeyOf(string domain, Guid uid, long? baseRevision, string baseHash, IEnumerable<string> branchIdentities) =>
        $"{domain}|{uid:N}|{baseRevision?.ToString() ?? "-"}|{baseHash}|" +
        string.Join(',', branchIdentities.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ commit

    public sealed record PendingCommit(RecordJournalEvent Event, string Line);

    /// <summary>
    /// Da chiamare sotto il commit lock del record, PRIMA della scrittura atomica del JSON autorevole: conserva in
    /// locale il payload della nuova versione (o di quella eliminata) e mette l'evento nell'outbox locale, così un
    /// crash fra scrittura e pubblicazione non perde la traccia. Non scrive su OneDrive. L'evento resta in volo
    /// (FlushOutbox non lo scarta) fino a Publish o Cancel.
    /// </summary>
    public PendingCommit Prepare(string domain, Guid uid, string id, RecordJournalOperation operation,
        object? baseVersion, long? baseRevision, object? newVersion, long? newRevision, string? operatorName)
    {
        if (operation == RecordJournalOperation.Resolve) throw new ArgumentOutOfRangeException(nameof(operation));
        var baseHash = baseVersion == null ? null : EntityStateFingerprint.Compute(baseVersion);
        var newHash = newVersion == null ? null : EntityStateFingerprint.Compute(newVersion);
        var ev = new RecordJournalEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            DeviceId = DeviceId,
            Domain = domain,
            EntityUid = uid,
            EntityId = id,
            Operation = operation,
            BaseRevision = baseRevision,
            BaseHash = baseHash,
            NewRevision = newRevision,
            NewHash = newHash,
            Utc = DateTime.UtcNow,
            Operator = operatorName
        };
        lock (_gate)
        {
            if (newVersion != null) StoreLocalPayload(domain, uid, newHash!, newVersion);
            if (baseVersion != null) StoreLocalPayload(domain, uid, baseHash!, baseVersion);
            var line = JsonSerializer.Serialize(ev, LineOptions);
            AppendLine(_outboxFile, line);
            _inVolo.Add(ev.EventId);
            return new PendingCommit(ev, line);
        }
    }

    /// <summary>Scrittura autorevole fallita: l'evento non è mai avvenuto e non deve essere pubblicato.</summary>
    public void Cancel(PendingCommit? pending)
    {
        if (pending == null) return;
        lock (_gate)
        {
            _inVolo.Remove(pending.Event.EventId);
            try { RewriteOutbox(lines => lines.Where(l => !l.Contains(pending.Event.EventId, StringComparison.Ordinal))); }
            catch (Exception ex) { CrashLogService.Registra("Record journal: cancel outbox", ex); }
        }
    }

    /// <summary>Dopo la scrittura autorevole riuscita: best-effort, non blocca mai il salvataggio. Un errore
    /// (OneDrive che tiene il file, cartella non raggiungibile) lascia l'evento nell'outbox per il giro successivo.</summary>
    public void Publish(PendingCommit pending)
    {
        lock (_gate) _inVolo.Remove(pending.Event.EventId);
        try { FlushOutbox(); }
        catch (Exception ex) { CrashLogService.Registra("Record journal: publish", ex); }
    }

    /// <summary>
    /// Create registrato fuori dai repository di dominio, per esempio dal ripristino del recovery-bin. Usa lo stesso
    /// protocollo dei repository (Prepare, poi Publish): Publish pubblica solo se il JSON canonico contiene davvero
    /// questo stato (vedi CommitWasApplied), quindi un ripristino non andato a buon fine non lascia eventi fantasma.
    /// </summary>
    public void RecordExternalCreate(string domain, Guid uid, string id, object newVersion, long? newRevision, string? operatorName)
    {
        ArgumentNullException.ThrowIfNull(newVersion);
        var pending = Prepare(domain, uid, id, RecordJournalOperation.Create, null, null, newVersion, newRevision, operatorName);
        Publish(pending);
    }

    /// <summary>Pubblica gli eventi rimasti nell'outbox locale. Un evento preparato ma la cui scrittura autorevole
    /// non è mai avvenuta (crash prima del Cancel) viene scartato se il file canonico non lo contiene. Gli eventi
    /// ancora in volo (Prepare senza Publish/Cancel) restano nell'outbox.</summary>
    public void FlushOutbox()
    {
        lock (_gate)
        {
            if (!File.Exists(_outboxFile) || !WorkspaceWritePolicy.CanWrite(_paths)) return;
            var lines = File.ReadAllLines(_outboxFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (lines.Count == 0) return;
            Directory.CreateDirectory(_deviceDirectory);
            RotateIfNeeded();
            var published = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                var ev = TryParseEvent(line);
                if (ev != null && _inVolo.Contains(ev.EventId)) continue; // salvataggio in corso: resta nell'outbox fino a Publish/Cancel
                if (ev == null || !CommitWasApplied(ev)) { published.Add(line); continue; }
                AppendLine(_currentFile, line);
                published.Add(line);
            }
            RewriteOutbox(all => all.Where(l => !published.Contains(l)));
        }
    }

    // ------------------------------------------------------------------ fork

    /// <summary>True se il record ha un fork aperto e la scrittura non avviene dentro una risoluzione Master.</summary>
    public bool IsBlocked(string domain, Guid uid)
    {
        var key = $"{domain}|{uid:N}";
        if (string.Equals(ResolutionScope.Value, key, StringComparison.OrdinalIgnoreCase)) return false;
        lock (_gate) return _blockedKeys.Contains(key);
    }

    public void EnsureNotBlocked(string domain, Guid uid, string id)
    {
        if (IsBlocked(domain, uid)) throw new RecordForkOpenException(domain, id);
    }

    /// <summary>Consente al Master di salvare la versione scelta per un record con fork aperto. Gli hash dei rami del fork
    /// sono le copie OneDrive che questa risoluzione incorpora: non bloccano il salvataggio che le risolve.</summary>
    public IDisposable BeginResolution(string domain, Guid uid, IEnumerable<string>? hashRami = null)
    {
        var previousScope = ResolutionScope.Value;
        var previousHashes = ResolutionBranchHashes.Value;
        ResolutionScope.Value = $"{domain}|{uid:N}";
        ResolutionBranchHashes.Value = hashRami == null
            ? null
            : new HashSet<string>(hashRami.Where(h => !string.IsNullOrWhiteSpace(h)), StringComparer.OrdinalIgnoreCase);
        return new Scope(() =>
        {
            ResolutionScope.Value = previousScope;
            ResolutionBranchHashes.Value = previousHashes;
        });
    }

    /// <summary>Hash dei rami che la guardia di scrittura puo' considerare gia' chiusi per questo record: rami di fork gia'
    /// risolti (journal persistito) e rami del fork in corso di risoluzione da parte del Master.</summary>
    public IReadOnlySet<string> RamiChiusiPer(string domain, Guid uid)
    {
        var key = $"{domain}|{uid:N}";
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (_ramiChiusi.TryGetValue(key, out var chiusi)) result.UnionWith(chiusi);
        }
        if (string.Equals(ResolutionScope.Value, key, StringComparison.OrdinalIgnoreCase) && ResolutionBranchHashes.Value is { } inCorso)
            result.UnionWith(inCorso);
        return result;
    }

    /// <summary>Regola di chiusura per la guardia di scrittura (VisibleConflictWriteGuard): una copia OneDrive il cui contenuto
    /// e' un ramo di un fork gia' risolto (o in risoluzione) non blocca il salvataggio. Ogni altra copia divergente blocca.</summary>
    public VisibleConflictWriteGuard.CopiaChiusaPredicate CopiaChiusaPer(string domain, Guid uid) =>
        (copia, _) => HashDiCopia(domain, copia) is { } hash && RamiChiusiPer(domain, uid).Contains(hash);

    /// <summary>Hash di contenuto (EntityStateFingerprint, stesso calcolo dei rami) di una copia JSON del dominio; null se non
    /// leggibile come record di quel dominio (fail-closed: nessuna esenzione).</summary>
    public static string? HashDiCopia(string domain, JsonElement copia)
    {
        var type = DomainType(domain);
        if (type == null) return null;
        try
        {
            var value = JsonSerializer.Deserialize(copia.GetRawText(), type, JsonIo.Options);
            return value == null ? null : EntityStateFingerprint.Compute(value);
        }
        catch (JsonException) { return null; }
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() { Interlocked.Exchange(ref _dispose, null)?.Invoke(); }
    }

    /// <summary>
    /// Legge i log di tutti i device (bounded), rileva i fork non risolti, pubblica i payload dei rami prodotti da
    /// questo device e aggiorna lo stato dei record bloccati. Pensato per il giro periodico in background.
    /// In sola lettura rileva comunque i fork (per mostrarli) ma non pubblica nulla.
    /// </summary>
    public IReadOnlyList<RecordFork> Refresh()
    {
        var canWrite = WorkspaceWritePolicy.CanWrite(_paths);
        if (canWrite)
        {
            try { FlushOutbox(); }
            catch (Exception ex) { CrashLogService.Registra("Record journal: flush outbox", ex); }
        }

        var events = ReadAllEvents(out var persistedForks, out var persistedResolved);
        var resolved = new HashSet<string>(
            events.Where(e => e.Operation == RecordJournalOperation.Resolve && !string.IsNullOrWhiteSpace(e.ResolvedForkKey))
                .Select(e => e.ResolvedForkKey!), StringComparer.OrdinalIgnoreCase);
        resolved.UnionWith(persistedResolved);

        // I fork persistiti dai device sopravvivono alla rotazione dei log; quelli rilevati ora aggiungono rami.
        var all = new Dictionary<string, RecordFork>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in persistedForks.Concat(DetectForks(events)))
            all[f.ForkId] = all.TryGetValue(f.ForkId, out var old) ? Merge(old, f) : f;
        var open = all.Values.Where(f => !resolved.Contains(f.Key))
            .OrderByDescending(f => f.DetectedUtc).ToList();

        // Rami dei fork gia' risolti: la chiave di risoluzione contiene gli hash dei rami ("domain|uid|baseRev|baseHash|h1,h2").
        var ramiChiusi = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in resolved)
        {
            var parti = key.Split('|', 5);
            if (parti.Length != 5) continue;
            var recordKey = $"{parti[0]}|{parti[1]}";
            if (!ramiChiusi.TryGetValue(recordKey, out var rami)) ramiChiusi[recordKey] = rami = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ramo in parti[4].Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (!ramo.Equals("DELETE", StringComparison.OrdinalIgnoreCase)) rami.Add(ramo);
        }

        lock (_gate)
        {
            _openForks = open;
            _blockedKeys = new HashSet<string>(open.Select(f => $"{f.Domain}|{f.EntityUid:N}"), StringComparer.OrdinalIgnoreCase);
            _ramiChiusi = ramiChiusi;
        }

        if (canWrite)
        {
            foreach (var fork in open)
            {
                try { PublishOwnBranches(fork); }
                catch (Exception ex) { CrashLogService.Registra("Record journal: pubblicazione ramo fork", ex); }
            }
            try { PersistOwnState(open, resolved); }
            catch (Exception ex) { CrashLogService.Registra("Record journal: stato fork per device", ex); }
        }
        return open;
    }

    /// <summary>Evento Master che chiude il fork. La versione scelta deve essere già il JSON autorevole.</summary>
    public void MarkResolved(RecordFork fork, string? chosenHash, string? operatorName)
    {
        WorkspaceWritePolicy.EnsureCanWrite(_paths, "Risoluzione conflitto fra postazioni");
        var ev = new RecordJournalEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            DeviceId = DeviceId,
            Domain = fork.Domain,
            EntityUid = fork.EntityUid,
            EntityId = fork.EntityId,
            Operation = RecordJournalOperation.Resolve,
            Utc = DateTime.UtcNow,
            Operator = operatorName,
            ResolvedForkKey = fork.Key,
            ChosenHash = chosenHash
        };
        lock (_gate)
        {
            AppendLine(_outboxFile, JsonSerializer.Serialize(ev, LineOptions));
        }
        FlushOutbox();
        Refresh();
    }

    /// <summary>Payload JSON di un ramo: copia locale di questo PC oppure copia pubblicata dal PC che l'ha prodotto.
    /// Null se il ramo è una cancellazione o il device che lo ha prodotto non l'ha ancora pubblicato.</summary>
    public string? TryReadBranchPayload(RecordFork fork, RecordForkBranch branch)
    {
        if (branch.IsDelete || string.IsNullOrWhiteSpace(branch.NewHash)) return null;
        var local = LocalPayloadPath(fork.Domain, fork.EntityUid, branch.NewHash);
        if (File.Exists(local)) return File.ReadAllText(local, Encoding.UTF8);
        var shared = PublishedBranchPath(fork.Domain, fork.EntityUid, branch.EventId);
        if (!File.Exists(shared)) return null;
        var text = Encoding.UTF8.GetString(JsonIo.ReadAllBytesShared(shared));
        // Il file pubblicato è accettato solo se coincide con l'hash dichiarato nel log.
        return HashOfJson(fork.Domain, text) is { } h && h.Equals(branch.NewHash, StringComparison.OrdinalIgnoreCase) ? text : null;
    }

    public string? TryReadBasePayload(RecordFork fork)
    {
        var local = LocalPayloadPath(fork.Domain, fork.EntityUid, fork.BaseHash);
        return File.Exists(local) ? File.ReadAllText(local, Encoding.UTF8) : null;
    }

    public static Type? DomainType(string domain) => domain switch
    {
        NonConformitaDomain => typeof(Models.NonConformita),
        ChangeRequestDomain => typeof(Models.ChangeRequest),
        SegnalazioneDomain => typeof(Models.SegnalazioneDaClassificare),
        _ => null
    };

    private static string? HashOfJson(string domain, string json)
    {
        var type = DomainType(domain);
        if (type == null) return null;
        try
        {
            var value = JsonSerializer.Deserialize(json, type, JsonIo.Options);
            return value == null ? null : EntityStateFingerprint.Compute(value);
        }
        catch (JsonException) { return null; }
    }

    // ------------------------------------------------------------------ interni

    private static List<RecordFork> DetectForks(IEnumerable<RecordJournalEvent> events)
    {
        var result = new List<RecordFork>();
        var groups = events
            .Where(e => e.Operation is RecordJournalOperation.Save or RecordJournalOperation.Delete &&
                        !string.IsNullOrWhiteSpace(e.BaseHash))
            .GroupBy(e => (e.Domain, e.EntityUid, e.BaseRevision, BaseHash: e.BaseHash!.ToUpperInvariant()));
        foreach (var g in groups)
        {
            // Due commit dallo stesso device sulla stessa base non sono concorrenza fra PC: il lock locale lo
            // impedisce, quindi il primo è stato annullato localmente (rollback di una transazione workflow o
            // ripristino). Conta solo l'ultimo ramo di ogni device, altrimenti un rollback sembrerebbe un fork.
            var branches = g.GroupBy(e => e.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(d => d.OrderBy(e => e.Utc).Last())
                .Select(e => new RecordForkBranch
                {
                    EventId = e.EventId, DeviceId = e.DeviceId, Operation = e.Operation,
                    NewRevision = e.NewRevision, NewHash = e.NewHash, Utc = e.Utc, Operator = e.Operator
                }).ToList();
            if (branches.Select(BranchIdentity).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) continue;
            result.Add(new RecordFork
            {
                Domain = g.Key.Domain,
                EntityUid = g.Key.EntityUid,
                EntityId = g.OrderByDescending(e => e.Utc).First().EntityId,
                BaseRevision = g.Key.BaseRevision,
                BaseHash = g.Key.BaseHash,
                Branches = branches.OrderBy(b => b.Utc).ToList(),
                DetectedUtc = DateTime.UtcNow
            });
        }
        return result;
    }

    private static RecordFork Merge(RecordFork a, RecordFork b) => new()
    {
        Domain = a.Domain, EntityUid = a.EntityUid, BaseRevision = a.BaseRevision, BaseHash = a.BaseHash,
        EntityId = string.IsNullOrWhiteSpace(b.EntityId) ? a.EntityId : b.EntityId,
        DetectedUtc = a.DetectedUtc <= b.DetectedUtc ? a.DetectedUtc : b.DetectedUtc,
        Branches = a.Branches.Concat(b.Branches).GroupBy(x => x.EventId, StringComparer.Ordinal)
            .Select(g => g.First()).OrderBy(x => x.Utc).ToList()
    };

    private sealed class DeviceState
    {
        public int Schema { get; set; } = JournalSchemaVersion;
        public string DeviceId { get; set; } = "";
        public DateTime UpdatedUtc { get; set; }
        public List<RecordFork> OpenForks { get; set; } = new();
        public List<string> ResolvedKeys { get; set; } = new();
    }

    private List<RecordJournalEvent> ReadAllEvents(out List<RecordFork> persistedForks, out List<string> persistedResolved)
    {
        var events = new List<RecordJournalEvent>();
        persistedForks = new List<RecordFork>();
        persistedResolved = new List<string>();
        if (!Directory.Exists(_journalRoot)) return events;
        foreach (var deviceDir in Directory.EnumerateDirectories(_journalRoot))
        {
            foreach (var file in Directory.EnumerateFiles(deviceDir, "*.ndjson"))
            {
                try
                {
                    // I segmenti ruotati sono immutabili e current cresce solo in append: si rilegge un file
                    // soltanto se lunghezza o mtime sono cambiati, per non riscandire MB di log a ogni giro.
                    var info = new FileInfo(file);
                    var stamp = (info.Length, info.LastWriteTimeUtc);
                    List<RecordJournalEvent> parsed;
                    lock (_gate)
                    {
                        if (_fileCache.TryGetValue(file, out var cached) && cached.Stamp == stamp)
                        {
                            events.AddRange(cached.Events);
                            continue;
                        }
                    }
                    parsed = JsonIo.ReadLinesShared(file).Select(TryParseEvent).OfType<RecordJournalEvent>().ToList();
                    lock (_gate) _fileCache[file] = (stamp, parsed);
                    events.AddRange(parsed);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    CrashLogService.Registra($"Record journal: lettura {file}", ex);
                }
            }
            var state = Path.Combine(deviceDir, "forks.json");
            if (!File.Exists(state)) continue;
            var deviceState = JsonIo.TryRead<DeviceState>(state, out _);
            if (deviceState == null || deviceState.Schema > JournalSchemaVersion) continue;
            persistedForks.AddRange(deviceState.OpenForks.Where(f => f.EntityUid != Guid.Empty && f.Branches.Count > 1));
            persistedResolved.AddRange(deviceState.ResolvedKeys.Where(k => !string.IsNullOrWhiteSpace(k)));
        }
        return events;
    }

    private void PersistOwnState(IReadOnlyList<RecordFork> open, IEnumerable<string> resolved)
    {
        var state = new DeviceState
        {
            DeviceId = DeviceId,
            UpdatedUtc = DateTime.UtcNow,
            OpenForks = open.ToList(),
            ResolvedKeys = resolved.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
        };
        var existing = File.Exists(_stateFile) ? JsonIo.TryRead<DeviceState>(_stateFile, out _) : null;
        if (existing != null &&
            existing.OpenForks.Select(f => f.Key).OrderBy(x => x).SequenceEqual(state.OpenForks.Select(f => f.Key).OrderBy(x => x)) &&
            existing.ResolvedKeys.SequenceEqual(state.ResolvedKeys, StringComparer.OrdinalIgnoreCase))
            return; // nessuna riscrittura inutile del file per-device
        Directory.CreateDirectory(_deviceDirectory);
        JsonIo.WriteAtomic(_stateFile, state);
    }

    private void PublishOwnBranches(RecordFork fork)
    {
        foreach (var branch in fork.Branches.Where(b => b.DeviceId == DeviceId && !b.IsDelete && !string.IsNullOrWhiteSpace(b.NewHash)))
        {
            var target = PublishedBranchPath(fork.Domain, fork.EntityUid, branch.EventId);
            if (File.Exists(target)) continue; // write-once
            var local = LocalPayloadPath(fork.Domain, fork.EntityUid, branch.NewHash!);
            if (!File.Exists(local)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            JsonIo.WriteBytesAtomic(target, File.ReadAllBytes(local), overwrite: false);
        }
    }

    private string PublishedBranchPath(string domain, Guid uid, string eventId) =>
        Path.Combine(_paths.Root, "_ditm", "conflicts", "records", SafeSegment(domain), uid.ToString("N"), SafeSegment(eventId) + ".json");

    private string LocalPayloadPath(string domain, Guid uid, string hash) =>
        Path.Combine(_localDirectory, "payloads", SafeSegment(domain), uid.ToString("N"), SafeSegment(hash) + ".json");

    private static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) || c is '.' or ' ' ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "_" : cleaned;
    }

    private void StoreLocalPayload(string domain, Guid uid, string hash, object value)
    {
        var path = LocalPayloadPath(domain, uid, hash);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonIo.WriteAtomic(path, value, overwrite: false);
    }

    /// <summary>Elimina payload locali più vecchi della retention, salvo quelli dei fork aperti.</summary>
    public int PruneLocalPayloads()
    {
        var root = Path.Combine(_localDirectory, "payloads");
        if (!Directory.Exists(root)) return 0;
        HashSet<string> keep;
        lock (_gate)
            keep = _openForks.SelectMany(f => f.Branches.Select(b => b.NewHash).Append(f.BaseHash))
                .Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h!.ToUpperInvariant()).ToHashSet();
        var limit = DateTime.UtcNow.AddDays(-LocalPayloadRetentionDays);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            if (keep.Contains(Path.GetFileNameWithoutExtension(file).ToUpperInvariant())) continue;
            if (File.GetLastWriteTimeUtc(file) >= limit) continue;
            try { File.Delete(file); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrashLogService.Registra("Pulizia dei record locali scaduti: file non eliminabile", ex);
            }
        }
        return removed;
    }

    private bool CommitWasApplied(RecordJournalEvent ev)
    {
        var path = ev.Domain switch
        {
            NonConformitaDomain => _paths.NonConformitaFile(ev.EntityId),
            ChangeRequestDomain => _paths.ChangeRequestFile(ev.EntityId),
            SegnalazioneDomain => _paths.SegnalazioneFile(ev.EntityUid),
            _ => null
        };
        if (path == null) return false;
        if (ev.Operation == RecordJournalOperation.Resolve) return true;
        if (ev.Operation == RecordJournalOperation.Delete) return !File.Exists(path) ||
            WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, path);
        if (!File.Exists(path)) return false;
        var type = DomainType(ev.Domain);
        if (type == null) return false;
        try
        {
            var current = JsonSerializer.Deserialize(JsonIo.ReadAllBytesShared(path), type, JsonIo.Options);
            if (current == null) return false;
            var hash = EntityStateFingerprint.Compute(current);
            if (hash.Equals(ev.NewHash, StringComparison.OrdinalIgnoreCase)) return true;
            // Sovrascritto dopo il commit (anche da un altro PC): il commit è comunque avvenuto se la revisione
            // corrente è successiva. Meglio un possibile falso positivo che perdere la traccia di un commit reale.
            var revision = current.GetType().GetProperty("RevisioneDati")?.GetValue(current) as long?;
            return revision.HasValue && ev.NewRevision.HasValue && revision.Value > ev.NewRevision.Value;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return true; // illeggibile ora: pubblicare è la scelta conservativa
        }
    }

    private static RecordJournalEvent? TryParseEvent(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var ev = JsonSerializer.Deserialize<RecordJournalEvent>(line, LineOptions);
            return ev == null || ev.Schema > JournalSchemaVersion || ev.EntityUid == Guid.Empty ||
                   string.IsNullOrWhiteSpace(ev.Domain) || string.IsNullOrWhiteSpace(ev.EventId) ? null : ev;
        }
        catch (JsonException) { return null; } // riga parziale (crash/sync a metà): ignorata, non interpretata
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_currentFile) || new FileInfo(_currentFile).Length < _rotateAfterBytes) return;
        var segment = Path.Combine(_deviceDirectory, $"segment-{DateTime.UtcNow:yyyyMMddHHmmssfff}.ndjson");
        File.Move(_currentFile, segment);
        var old = Directory.EnumerateFiles(_deviceDirectory, "segment-*.ndjson")
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(_segmentsToKeep).ToList();
        foreach (var f in old)
        {
            try { File.Delete(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrashLogService.Registra("Rotazione del journal locale: segmento non eliminabile", ex);
            }
        }
    }

    private static void AppendLine(string path, string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        // Un crash (o una sync a metà) può lasciare l'ultima riga senza a capo: chiuderla prima, altrimenti il
        // nuovo evento verrebbe incollato al frammento e scartato come riga illeggibile.
        var needsSeparator = false;
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            needsSeparator = stream.ReadByte() != '\n';
        }
        stream.Seek(0, SeekOrigin.End);
        var bytes = Encoding.UTF8.GetBytes((needsSeparator ? "\n" : "") + line + "\n");
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private void RewriteOutbox(Func<IEnumerable<string>, IEnumerable<string>> filter)
    {
        if (!File.Exists(_outboxFile)) return;
        var remaining = filter(File.ReadAllLines(_outboxFile).Where(l => !string.IsNullOrWhiteSpace(l))).ToList();
        if (remaining.Count == 0) { File.Delete(_outboxFile); return; }
        JsonIo.WriteBytesAtomic(_outboxFile, Encoding.UTF8.GetBytes(string.Join("\n", remaining) + "\n"));
    }
}
