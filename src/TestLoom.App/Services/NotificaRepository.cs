using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Repository notifiche condivise ottimizzato per OneDrive/SharePoint.
///
/// Il vecchio formato creava un file notifica_&lt;guid&gt;.json per ogni evento e riscriveva lo stesso file per
/// marcarlo letto: a lungo andare la cartella diventava una directory con migliaia di piccoli file, esattamente
/// il caso peggiore per SharePoint/OneDrive. Il formato v2 usa invece, per ogni device, un current NDJSON,
/// pochi segmenti chiusi e un checkpoint gzip. Il numero di file resta quindi limitato indipendentemente dal
/// numero di notifiche generate.
///
/// Le notifiche sono messaggistica effimera, NON fonte di audit/statistiche di dominio. Per questo hanno una
/// retention esplicita: le lette vengono eliminate dopo 30 giorni, le non lette dopo 180 giorni. I tombstone
/// Delete restano 210 giorni, oltre la retention massima di una notifica, così una vecchia Create sincronizzata
/// in ritardo non può farla riapparire. Anche se un altro PC conserva ancora un checkpoint antico, GetAll
/// applica comunque la retention sul payload originale e non rimostra notifiche scadute.
///
/// I vecchi JSON restano leggibili durante la transizione. La manutenzione automatica elimina quelli già
/// rappresentati nel ledger v2 oppure scaduti per retention, evitando che la cartella legacy continui a
/// crescere per sempre.
/// </summary>
public sealed class NotificaRepository
{
    private const int LedgerSchemaVersion = 1;
    private const long MaxCurrentBytes = 512 * 1024;
    private const int MaxCompletedSegments = 2;
    public static readonly TimeSpan RetentionLetta = TimeSpan.FromDays(30);
    public static readonly TimeSpan RetentionNonLetta = TimeSpan.FromDays(180);
    public static readonly TimeSpan RetentionTombstone = TimeSpan.FromDays(210);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(12);

    private readonly DataPaths _paths;
    private readonly string _deviceId;
    private readonly string _sharedRoot;
    private readonly string _deviceDir;
    private readonly string _currentPath;
    private readonly string _checkpointPath;
    private readonly object _gate = new();
    private DateTime _lastMaintenanceUtc = DateTime.MinValue;

    public NotificaRepository(DataPaths paths)
    {
        _paths = paths;
        _deviceId = DeviceIdentityService.GetOrCreate();
        _sharedRoot = Path.Combine(paths.Root, "_ditm", "notifications");
        _deviceDir = Path.Combine(_sharedRoot, _deviceId);
        _currentPath = Path.Combine(_deviceDir, "current.ndjson");
        _checkpointPath = Path.Combine(_deviceDir, "checkpoint.json.gz");
    }

    public List<NotificaApp> GetAll(string destinatario)
    {
        if (string.IsNullOrWhiteSpace(destinatario)) return new();
        TryRunMaintenance();
        try
        {
            var states = ReadSharedStates();
            MergeLegacy(states);
            PruneExpired(states, DateTime.UtcNow);
            return states.Values
                .Where(s => s.Operation != NotificationLedgerOperation.Delete && s.Notification != null)
                .Select(s => CloneNotification(s.Notification!))
                .Where(n => n.Destinatario.Equals(destinatario, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.Data)
                .ToList();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Lettura notifiche bounded", ex);
            return ReadLegacyOnly(destinatario);
        }
    }

    public void Add(NotificaApp item) => AddCore(item, deterministic: false);

    /// <summary>Aggiunge una notifica con UID logico stabile senza farla risorgere dopo un tombstone.</summary>
    public void AddDeterministic(NotificaApp item) => AddCore(item, deterministic: true);

    private void AddCore(NotificaApp item, bool deterministic)
    {
        TryRunMaintenance();

        // NotificaApp assegna un Guid già nel costruttore, quindi il solo valore di Uid non può distinguere una
        // normale notifica da un'identità logica stabile. Solo il percorso esplicito deterministic legge il
        // ledger globale; le notifiche normali restano append O(1), anche in stress da migliaia di eventi.
        if (deterministic && item.Uid != Guid.Empty && HasAnyState(item.Uid)) return;

        if (item.Uid == Guid.Empty) item.Uid = Guid.NewGuid();
        if (item.Data == default) item.Data = DateTime.Now;
        var evt = new NotificationLedgerEvent
        {
            EventId = Guid.NewGuid(),
            DeviceId = _deviceId,
            TimestampUtc = DateTime.UtcNow,
            NotificationUid = item.Uid,
            Operation = NotificationLedgerOperation.Create,
            Notification = CloneNotification(item)
        };

        // Una notifica non è source of truth e non deve mai precedere il commit che descrive. Dentro un Unit
        // of Work la accodiamo quindi come effetto post-commit: un rollback non produce notifiche fantasma e
        // un problema del ledger notifiche dopo il commit viene loggato senza invalidare dati già coerenti.
        if (WorkflowTransactionService.DeferAfterCommit(() => AppendPostCommit(evt))) return;
        Append(evt);
    }

    private void AppendPostCommit(NotificationLedgerEvent evt)
    {
        try { Append(evt); }
        catch (Exception ex) { CrashLogService.Registra("Notifica post-commit non scritta", ex); }
    }

    public void MarkRead(NotificaApp item)
    {
        if (item.Letta) return;

        // Non mutare la working-copy UI prima della persistenza. Se gate/OneDrive falliscono, la notifica deve
        // restare non letta anche in memoria: altrimenti l'utente vede uno stato che non esiste nel ledger.
        var updated = CloneNotification(item);
        updated.Letta = true;
        Append(new NotificationLedgerEvent
        {
            EventId = Guid.NewGuid(),
            DeviceId = _deviceId,
            TimestampUtc = DateTime.UtcNow,
            NotificationUid = item.Uid,
            Operation = NotificationLedgerOperation.Read,
            Notification = updated
        });
        item.Letta = true;
        DeleteLegacyFile(item.Uid);
    }

    /// <summary>Segna tutte le notifiche non lette del destinatario in un'unica append fisica.</summary>
    public int MarkAllRead(string destinatario)
    {
        var unread = GetAll(destinatario).Where(n => !n.Letta).ToList();
        if (unread.Count == 0) return 0;
        var events = new List<NotificationLedgerEvent>(unread.Count);
        foreach (var item in unread)
        {
            item.Letta = true;
            events.Add(new NotificationLedgerEvent
            {
                EventId = Guid.NewGuid(), DeviceId = _deviceId, TimestampUtc = DateTime.UtcNow,
                NotificationUid = item.Uid, Operation = NotificationLedgerOperation.Read,
                Notification = CloneNotification(item)
            });
        }
        AppendMany(events);
        foreach (var item in unread) DeleteLegacyFile(item.Uid);
        return unread.Count;
    }

    /// <summary>Segna come lette solo le notifiche indicate, in un'unica append fisica (riepilogo notifiche, 6.6.0).
    /// Non elimina nulla e non tocca le altre notifiche. Lo stato in memoria cambia solo dopo la persistenza riuscita.</summary>
    public int MarkReadMany(IEnumerable<NotificaApp> items)
    {
        var daLeggere = items.Where(n => !n.Letta).DistinctBy(n => n.Uid).ToList();
        if (daLeggere.Count == 0) return 0;
        var events = new List<NotificationLedgerEvent>(daLeggere.Count);
        foreach (var item in daLeggere)
        {
            var aggiornata = CloneNotification(item);
            aggiornata.Letta = true;
            events.Add(new NotificationLedgerEvent
            {
                EventId = Guid.NewGuid(), DeviceId = _deviceId, TimestampUtc = DateTime.UtcNow,
                NotificationUid = item.Uid, Operation = NotificationLedgerOperation.Read,
                Notification = aggiornata
            });
        }
        AppendMany(events);
        foreach (var item in daLeggere)
        {
            item.Letta = true;
            DeleteLegacyFile(item.Uid);
        }
        return daLeggere.Count;
    }

    /// <summary>Eliminazione esplicita dell'utente. Il tombstone resta nel checkpoint compatto affinché una
    /// vecchia copia Create proveniente dal checkpoint di un altro PC non possa far riapparire la notifica.</summary>
    public void Delete(NotificaApp item)
    {
        Append(new NotificationLedgerEvent
        {
            EventId = Guid.NewGuid(),
            DeviceId = _deviceId,
            TimestampUtc = DateTime.UtcNow,
            NotificationUid = item.Uid,
            Operation = NotificationLedgerOperation.Delete,
            Notification = null
        });
        DeleteLegacyFile(item.Uid);
    }

    /// <summary>Elimina in blocco le notifiche già lette del destinatario con una singola append fisica.</summary>
    public int DeleteRead(string destinatario)
    {
        var read = GetAll(destinatario).Where(n => n.Letta).ToList();
        if (read.Count == 0) return 0;
        var now = DateTime.UtcNow;
        AppendMany(read.Select(item => new NotificationLedgerEvent
        {
            EventId = Guid.NewGuid(), DeviceId = _deviceId, TimestampUtc = now,
            NotificationUid = item.Uid, Operation = NotificationLedgerOperation.Delete, Notification = null
        }));
        foreach (var item in read) DeleteLegacyFile(item.Uid);
        return read.Count;
    }

    /// <summary>
    /// Compatta completamente il ledger del device corrente e applica la retention. È sicuro chiamarla da
    /// manutenzione manuale o periodica: opera soltanto sui file posseduti da questo device e sui vecchi JSON
    /// legacy scaduti/già migrati. Non modifica dati di dominio.
    /// </summary>
    public NotificationMaintenanceResult RunMaintenance(bool force = true)
    {
        WorkspaceWritePolicy.EnsureCanWrite(_paths, "Manutenzione notifiche");
        lock (_gate)
        {
            WorkspaceWritePolicy.EnsureCanWrite(_paths, "Manutenzione notifiche");
            var now = DateTime.UtcNow;
            if (!force && now - _lastMaintenanceUtc < MaintenanceInterval) return new(0, 0, 0);
            var result = CompactOwnLedgerAndLegacy(now);
            _lastMaintenanceUtc = now;
            return result;
        }
    }

    private void TryRunMaintenance()
    {
        // GetAll deve restare una lettura pura quando il workspace è sola lettura/incompatibile. Non provare
        // nemmeno a creare directory/checkpoint in background: la manutenzione riprenderà appena torna Operativo.
        if (!WorkspaceWritePolicy.CanWrite(_paths)) return;
        try { RunMaintenance(force: false); }
        catch (Exception ex) { CrashLogService.Registra("Manutenzione automatica notifiche", ex); }
    }

    private bool HasAnyState(Guid uid)
    {
        if (uid == Guid.Empty) return false;
        try
        {
            var states = ReadSharedStates();
            MergeLegacy(states);
            PruneExpired(states, DateTime.UtcNow);
            return states.ContainsKey(uid);
        }
        catch
        {
            // In dubbio non blocchiamo la creazione di una notifica funzionale: il merge per UID resta
            // idempotente e la compattazione successiva elimina eventuali duplicati fisici.
            return false;
        }
    }

    private void Append(NotificationLedgerEvent evt) => AppendMany([evt]);

    private void AppendMany(IEnumerable<NotificationLedgerEvent> source)
    {
        var events = source.ToList();
        if (events.Count == 0) return;
        WorkspaceWritePolicy.EnsureCanWrite(_paths, "Aggiornamento notifiche");
        lock (_gate)
        {
            WorkspaceWritePolicy.EnsureCanWrite(_paths, "Aggiornamento notifiche");
            Directory.CreateDirectory(_deviceDir);
            // Riparazione della coda e append sotto il mutex cross-processo (stesso contratto di PhaseDMasterJournalService):
            // una coda parziale da crash viene spostata in current.ndjson.torn-*, mai troncata alla cieca.
            using var lockCrossProcesso = new NdjsonCodaRiparazione.LockCrossProcesso("Notifiche", _currentPath);
            var prefissoRiga = NdjsonCodaRiparazione.PreparaAccodamento(
                _currentPath, RigaEventoValida, "Notifiche",
                "Ledger notifiche current.ndjson con coda parziale non riparabile: append bloccata senza modificare il file.");

            using (var stream = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                       64 * 1024, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(prefissoRiga);
                foreach (var evt in events)
                    writer.WriteLine(JsonSerializer.Serialize(evt, JsonIo.CompactOptions));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (new FileInfo(_currentPath).Length >= MaxCurrentBytes)
                RotateAndCompact();
        }
    }

    private Dictionary<Guid, NotificationLedgerEvent> ReadSharedStates()
    {
        var states = new Dictionary<Guid, NotificationLedgerEvent>();
        if (!Directory.Exists(_sharedRoot)) return states;

        foreach (var deviceDir in SafeDirectories(_sharedRoot))
        {
            ReadCheckpointPrefixSafe(Path.Combine(deviceDir, "checkpoint.json.gz"), states);
            foreach (var segment in SafeFiles(deviceDir, "segment-*.ndjson")) ReadNdjsonPrefixSafe(segment, states);
            ReadNdjsonPrefixSafe(Path.Combine(deviceDir, "current.ndjson"), states);
        }
        return states;
    }

    /// <summary>
    /// Reader di consultazione prefix-safe: mantiene gli eventi completi che precedono una coda parziale ma si
    /// ferma al primo record non interpretabile/futuro. Non viene usato da compaction o delete delle sorgenti.
    /// </summary>
    private static void ReadNdjsonPrefixSafe(string path, Dictionary<Guid, NotificationLedgerEvent> states)
    {
        if (!File.Exists(path)) return;
        var riga = 0;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                riga++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                NotificationLedgerEvent? evt;
                try { evt = JsonSerializer.Deserialize<NotificationLedgerEvent>(line, JsonIo.Options); }
                catch (JsonException ex) { SegnalaLetturaInterrotta(path, riga, ex); break; }
                if (!IsValidLedgerEvent(evt)) { SegnalaLetturaInterrotta(path, riga, null); break; }
                MergeState(states, evt!);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SegnalaLetturaInterrotta(path, riga, ex);
        }
    }

    /// <summary>Lettura prefix-safe interrotta a una riga non valida: il prefisso resta visibile, ma la perdita va registrata
    /// con file e riga. Una sola volta per file e riga, perché il registro si rilegge a ogni consultazione.</summary>
    private static void SegnalaLetturaInterrotta(string path, int riga, Exception? ex)
    {
        var chiave = $"{path}#{riga}";
        lock (InterruzioniSegnalateGate)
        {
            if (!InterruzioniSegnalate.Add(chiave)) return;
            InterruzioniSegnalateOrdine.Enqueue(chiave);
            if (InterruzioniSegnalateOrdine.Count > MassimeInterruzioniMemorizzate)
                InterruzioniSegnalate.Remove(InterruzioniSegnalateOrdine.Dequeue());
        }
        CrashLogService.Registra($"Registro notifiche: lettura interrotta alla riga {riga} di {path}", ex);
    }

    private static readonly HashSet<string> InterruzioniSegnalate = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> InterruzioniSegnalateOrdine = new();
    private static readonly object InterruzioniSegnalateGate = new();
    // Evita crescita permanente se nuove righe corrotte compaiono nel tempo su cartelle sincronizzate.
    private const int MassimeInterruzioniMemorizzate = 1024;

    private static void ReadCheckpointPrefixSafe(string path, Dictionary<Guid, NotificationLedgerEvent> states)
    {
        if (!TryReadCheckpoint(path, out var checkpoint) || checkpoint == null) return;
        foreach (var evt in checkpoint.Heads.Values) MergeState(states, evt);
    }

    /// <summary>All-or-nothing per ogni sorgente che potrà essere cancellata dopo la compattazione.</summary>
    private static bool TryReadNdjsonStrict(string path, out List<NotificationLedgerEvent> events)
    {
        events = new List<NotificationLedgerEvent>();
        if (!File.Exists(path)) return true;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                NotificationLedgerEvent? evt;
                try { evt = JsonSerializer.Deserialize<NotificationLedgerEvent>(line, JsonIo.Options); }
                catch (JsonException) { return false; }
                if (!IsValidLedgerEvent(evt)) return false;
                events.Add(evt!);
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// true: checkpoint assente oppure valido nello schema corrente. false: file presente ma parziale, corrotto
    /// o di schema futuro; i writer devono preservarlo e rinunciare a qualunque compattazione distruttiva.
    /// </summary>
    private static bool TryReadCheckpoint(string path, out NotificationLedgerCheckpoint? checkpoint)
    {
        checkpoint = null;
        if (!File.Exists(path)) return true;
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var gzip = new GZipStream(fs, CompressionMode.Decompress);
            checkpoint = JsonSerializer.Deserialize<NotificationLedgerCheckpoint>(gzip, JsonIo.Options);
            if (checkpoint == null || checkpoint.SchemaVersion != LedgerSchemaVersion || checkpoint.Heads == null)
                return false;
            foreach (var pair in checkpoint.Heads)
                if (pair.Key == Guid.Empty || !IsValidLedgerEvent(pair.Value) || pair.Value.NotificationUid != pair.Key)
                    return false;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (InvalidDataException) { return false; }
        catch (JsonException) { return false; }
    }

    private static bool IsValidLedgerEvent(NotificationLedgerEvent? evt)
    {
        if (evt == null || evt.SchemaVersion != LedgerSchemaVersion || evt.EventId == Guid.Empty ||
            evt.NotificationUid == Guid.Empty || evt.TimestampUtc == default || !Enum.IsDefined(evt.Operation))
            return false;

        if (evt.Operation == NotificationLedgerOperation.Delete) return true;
        return evt.Notification != null && evt.Notification.Uid == evt.NotificationUid;
    }

    /// <summary>Riga del ledger valida come evento: usata solo dalla riparazione della coda parziale prima dell'append.</summary>
    private static bool RigaEventoValida(string riga)
    {
        try { return IsValidLedgerEvent(JsonSerializer.Deserialize<NotificationLedgerEvent>(riga, JsonIo.Options)); }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// Per uno stesso UID Create può esistere su un device e Read/Delete su un altro. Gli UID non vengono
    /// riutilizzati, quindi Delete domina sempre Read/Create e Read domina Create. A parità di operazione
    /// vince l'evento più recente.
    /// </summary>
    private static void MergeState(Dictionary<Guid, NotificationLedgerEvent> states, NotificationLedgerEvent candidate)
    {
        if (!states.TryGetValue(candidate.NotificationUid, out var current))
        {
            states[candidate.NotificationUid] = candidate;
            return;
        }

        var candidatePriority = Priority(candidate.Operation);
        var currentPriority = Priority(current.Operation);
        if (candidatePriority > currentPriority ||
            candidatePriority == currentPriority && candidate.TimestampUtc > current.TimestampUtc ||
            candidatePriority == currentPriority && candidate.TimestampUtc == current.TimestampUtc && candidate.EventId.CompareTo(current.EventId) > 0)
            states[candidate.NotificationUid] = candidate;
    }

    private static int Priority(NotificationLedgerOperation operation) => operation switch
    {
        NotificationLedgerOperation.Delete => 3,
        NotificationLedgerOperation.Read => 2,
        _ => 1
    };

    private void RotateAndCompact()
    {
        if (!File.Exists(_currentPath) || new FileInfo(_currentPath).Length == 0) return;
        Directory.CreateDirectory(_deviceDir);
        var segment = Path.Combine(_deviceDir, $"segment-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.ndjson");
        File.Move(_currentPath, segment, overwrite: false);

        var segments = SafeFiles(_deviceDir, "segment-*.ndjson").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        if (segments.Count <= MaxCompletedSegments) return;

        var toCompact = segments.Take(segments.Count - MaxCompletedSegments).ToList();
        var parsed = new List<NotificationLedgerEvent>();
        foreach (var old in toCompact)
        {
            if (!TryReadNdjsonStrict(old, out var events)) return;
            parsed.AddRange(events);
        }
        if (!TryReadCheckpoint(_checkpointPath, out var checkpoint)) return;
        checkpoint ??= new NotificationLedgerCheckpoint();
        foreach (var evt in parsed) MergeState(checkpoint.Heads, evt);
        PruneExpired(checkpoint.Heads, DateTime.UtcNow);
        checkpoint.SchemaVersion = LedgerSchemaVersion;
        checkpoint.UpdatedUtc = DateTime.UtcNow;
        WriteCheckpointAtomic(checkpoint);

        foreach (var old in toCompact) TryDeleteFile(old);
    }

    private NotificationMaintenanceResult CompactOwnLedgerAndLegacy(DateTime nowUtc)
    {
        if (!TryReadCheckpoint(_checkpointPath, out var checkpoint))
            throw new InvalidDataException("Checkpoint notifiche presente ma non integralmente leggibile: manutenzione annullata senza mutazioni.");

        var own = checkpoint == null
            ? new Dictionary<Guid, NotificationLedgerEvent>()
            : new Dictionary<Guid, NotificationLedgerEvent>(checkpoint.Heads);
        var sourceFiles = SafeFiles(_deviceDir, "segment-*.ndjson")
            .Concat(File.Exists(_currentPath) ? [_currentPath] : Array.Empty<string>())
            .ToList();
        foreach (var source in sourceFiles)
        {
            if (!TryReadNdjsonStrict(source, out var events))
                throw new InvalidDataException($"Ledger notifiche non integralmente leggibile: {Path.GetFileName(source)}. Manutenzione annullata senza cancellare sorgenti.");
            foreach (var evt in events) MergeState(own, evt);
        }

        var before = own.Count;
        PruneExpired(own, nowUtc);
        var pruned = before - own.Count;
        var checkpointExists = File.Exists(_checkpointPath);

        // Non riscrivere un checkpoint gia' compatto e invariato a ogni maintenance interval: su OneDrive
        // sarebbe solo churn di versioni/sync. Si materializza un nuovo checkpoint soltanto quando dobbiamo
        // assorbire current/segmenti oppure quando la retention ha davvero rimosso stati.
        var mustCompact = sourceFiles.Count > 0 || pruned > 0;
        if (own.Count == 0)
        {
            // Se tutto e' scaduto, elimina direttamente il vecchio checkpoint: scriverne uno vuoto per poi
            // cancellarlo produrrebbe due mutazioni condivise inutili. Ci arriviamo solo dopo lettura strict di
            // checkpoint e sorgenti, quindi nessun evento parziale può essere scambiato per stato scaduto.
            foreach (var file in sourceFiles) TryDeleteFile(file);
            if (checkpointExists) TryDeleteFile(_checkpointPath);
        }
        else if (mustCompact || !checkpointExists)
        {
            WriteCheckpointAtomic(new NotificationLedgerCheckpoint
            {
                SchemaVersion = LedgerSchemaVersion,
                UpdatedUtc = nowUtc,
                Heads = own
            });
            foreach (var file in sourceFiles) TryDeleteFile(file);
        }

        var globalLedger = ReadSharedStates();
        var legacyDeleted = CleanupLegacyFiles(nowUtc, globalLedger);
        return new NotificationMaintenanceResult(legacyDeleted, pruned, sourceFiles.Count);
    }

    private void WriteCheckpointAtomic(NotificationLedgerCheckpoint checkpoint)
    {
        checkpoint.SchemaVersion = LedgerSchemaVersion;
        Directory.CreateDirectory(_deviceDir);
        var temp = _checkpointPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            using (var gzip = new GZipStream(fs, CompressionLevel.SmallestSize))
                JsonSerializer.Serialize(gzip, checkpoint, JsonIo.Options);
            File.Move(temp, _checkpointPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private void MergeLegacy(Dictionary<Guid, NotificationLedgerEvent> states)
    {
        if (!Directory.Exists(_paths.NotificheDir)) return;
        foreach (var file in SafeFiles(_paths.NotificheDir, "notifica_*.json"))
        {
            var item = JsonIo.TryRead<NotificaApp>(file, out _);
            if (item == null || item.Uid == Guid.Empty) continue;
            MergeState(states, LegacyEvent(item));
        }
    }

    private List<NotificaApp> ReadLegacyOnly(string destinatario)
    {
        if (!Directory.Exists(_paths.NotificheDir)) return new();
        var now = DateTime.UtcNow;
        var result = new List<NotificaApp>();
        foreach (var file in SafeFiles(_paths.NotificheDir, "notifica_*.json"))
        {
            var n = JsonIo.TryRead<NotificaApp>(file, out _);
            if (n != null && !IsExpired(n, now) && n.Destinatario.Equals(destinatario, StringComparison.OrdinalIgnoreCase)) result.Add(n);
        }
        return result.OrderByDescending(n => n.Data).ToList();
    }

    private int CleanupLegacyFiles(DateTime nowUtc, IReadOnlyDictionary<Guid, NotificationLedgerEvent> ledgerV2)
    {
        if (!Directory.Exists(_paths.NotificheDir)) return 0;
        var deleted = 0;
        foreach (var file in SafeFiles(_paths.NotificheDir, "notifica_*.json"))
        {
            try
            {
                var item = JsonIo.TryRead<NotificaApp>(file, out _);
                if (item == null) continue;
                if (!IsExpired(item, nowUtc) && !ledgerV2.ContainsKey(item.Uid)) continue;
                if (TryDeleteFile(file)) deleted++;
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Pulizia delle notifiche legacy: file non elaborabile", ex);
            }
        }
        return deleted;
    }

    private void DeleteLegacyFile(Guid uid)
    {
        if (!Directory.Exists(_paths.NotificheDir)) return;
        var exact = Path.Combine(_paths.NotificheDir, $"notifica_{uid:N}.json");
        TryDeleteFile(exact);
    }

    private static NotificationLedgerEvent LegacyEvent(NotificaApp item) => new()
    {
        SchemaVersion = LedgerSchemaVersion,
        EventId = item.Uid,
        DeviceId = "legacy",
        TimestampUtc = ToUtc(item.Data),
        NotificationUid = item.Uid,
        Operation = item.Letta ? NotificationLedgerOperation.Read : NotificationLedgerOperation.Create,
        Notification = item
    };

    private static void PruneExpired(Dictionary<Guid, NotificationLedgerEvent> states, DateTime nowUtc)
    {
        foreach (var uid in states.Where(kv => IsExpired(kv.Value, nowUtc)).Select(kv => kv.Key).ToList())
            states.Remove(uid);
    }

    private static bool IsExpired(NotificationLedgerEvent evt, DateTime nowUtc)
    {
        if (evt.Operation == NotificationLedgerOperation.Delete)
            return nowUtc - ToUtc(evt.TimestampUtc) > RetentionTombstone;

        if (evt.Notification == null)
        {
            var retention = evt.Operation == NotificationLedgerOperation.Read ? RetentionLetta : RetentionNonLetta;
            return nowUtc - ToUtc(evt.TimestampUtc) > retention;
        }
        return IsExpired(evt.Notification, nowUtc);
    }

    private static bool IsExpired(NotificaApp item, DateTime nowUtc)
    {
        var date = item.Data == default ? nowUtc : ToUtc(item.Data);
        return nowUtc - date > (item.Letta ? RetentionLetta : RetentionNonLetta);
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
    };

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).ToList() : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToList() : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return !File.Exists(path);
        }
        catch { return false; }
    }

    private static NotificaApp CloneNotification(NotificaApp source) => new()
    {
        Uid = source.Uid,
        Data = source.Data,
        Destinatario = source.Destinatario,
        Tipo = source.Tipo,
        Titolo = source.Titolo,
        Messaggio = source.Messaggio,
        ElementoId = source.ElementoId,
        Letta = source.Letta
    };
}

public sealed record NotificationMaintenanceResult(int LegacyJsonEliminati, int StatiScadutiEliminati, int FileLedgerCompattati);

public enum NotificationLedgerOperation
{
    Create,
    Read,
    Delete
}

public sealed class NotificationLedgerEvent
{
    public int SchemaVersion { get; set; } = 1;
    public Guid EventId { get; set; }
    public string DeviceId { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public Guid NotificationUid { get; set; }
    public NotificationLedgerOperation Operation { get; set; }
    public NotificaApp? Notification { get; set; }
}

public sealed class NotificationLedgerCheckpoint
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime UpdatedUtc { get; set; }
    public Dictionary<Guid, NotificationLedgerEvent> Heads { get; set; } = new();
}
