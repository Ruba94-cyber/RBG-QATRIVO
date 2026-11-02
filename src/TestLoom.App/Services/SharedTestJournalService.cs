using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Journal condiviso BOUNDED per rilevare fork/conflitti tra cartelle OneDrive sincronizzate in momenti
/// diversi. NON è la fonte delle statistiche e NON contiene lo storico funzionale del Test: revisioni,
/// operazioni e tentativi restano nel TestCase JSON e nel read-model SQLite locale.
///
/// Ogni device scrive soltanto nella propria directory:
///   _ditm/journal/&lt;device-id&gt;/current.ndjson
///   _ditm/journal/&lt;device-id&gt;/segment-*.ndjson       (pochi file)
///   _ditm/journal/&lt;device-id&gt;/checkpoint.json.gz
///
/// current viene ruotato e i segmenti vecchi compattati nel checkpoint. Il numero di file condivisi resta
/// quindi limitato per device, indipendentemente dal numero di salvataggi. Snapshot completi vengono pubblicati
/// in _ditm/conflicts soltanto quando un fork è realmente rilevato; i salvataggi normali non creano sidecar.
///
/// Anche outbox e recovery locali sono in un singolo journal.db SQLite, non migliaia di piccoli file.
/// </summary>
public sealed class SharedTestJournalService
{
    public const int JournalSchemaVersion = 1;
    private const long DefaultRotateAfterBytes = 768 * 1024;
    private const int DefaultCompletedSegmentsToKeep = 3;
    private const int LocalRecoveryRetentionDays = 90;
    private const int LocalBusyTimeoutMs = 500;

    /// <summary>Merge automatico fork (6.4.1, punto B, decisione orchestratore): quante basi locali tenere per
    /// singolo Test in <c>base_snapshot</c>, oltre alla finestra temporale di <see cref="LocalRecoveryRetentionDays"/>
    /// condivisa con la recovery. Solo dati LOCALI del singolo PC (journal.db), mai nella cartella condivisa:
    /// nessun cambio al formato dei file condivisi.</summary>
    private const int MaxLocalBasesPerEntity = 5;

    private readonly object _gate = new();
    private readonly DataPaths _paths;
    private readonly string _journalRoot;
    private readonly string _deviceDirectory;
    private readonly string _currentFile;
    private readonly string _checkpointFile;
    private readonly string _localConnectionString;
    private readonly long _rotateAfterBytes;
    private readonly int _completedSegmentsToKeep;
    private readonly JsonSerializerOptions _options = new(JsonIo.Options) { WriteIndented = false };
    private IReadOnlyList<JournalForkConflict> _knownForks = Array.Empty<JournalForkConflict>();
    private HashSet<string> _knownRevisionHashes = new(StringComparer.OrdinalIgnoreCase);

    public string DeviceId { get; }

    /// <summary>Vero se <paramref name="hash"/> (TestCaseFingerprint) è una versione di questo Test salvata da una
    /// qualunque postazione e registrata nel journal condiviso all'ultima <see cref="RefreshKnownForks"/>. Serve a
    /// riconoscere una copia di conflitto OneDrive che contiene solo un salvataggio già noto (6.4.2).</summary>
    public bool IsKnownRevision(Guid uid, string hash)
    {
        lock (_gate) return _knownRevisionHashes.Contains($"{uid:N}|{hash}");
    }
    public string LocalDatabasePath { get; }
    public IReadOnlyList<JournalForkConflict> KnownForks { get { lock (_gate) return _knownForks; } }

    /// <summary>
    /// Il costruttore tocca SOLO storage locale. Non enumera né crea cartelle OneDrive: il journal non deve
    /// entrare nel percorso critico del warm-start. Gli override sono usati dai test per forzare rotazioni
    /// rapide senza generare megabyte di dati.
    /// </summary>
    public SharedTestJournalService(
        DataPaths paths,
        string localWorkspaceDirectory,
        string? deviceIdOverride = null,
        long? rotateAfterBytesOverride = null,
        int? completedSegmentsToKeepOverride = null)
    {
        _paths = paths;
        DeviceId = string.IsNullOrWhiteSpace(deviceIdOverride) ? DeviceIdentityService.GetOrCreate() : deviceIdOverride.Trim();
        _rotateAfterBytes = Math.Max(256, rotateAfterBytesOverride ?? DefaultRotateAfterBytes);
        _completedSegmentsToKeep = Math.Max(1, completedSegmentsToKeepOverride ?? DefaultCompletedSegmentsToKeep);
        _journalRoot = Path.Combine(paths.Root, "_ditm", "journal");
        _deviceDirectory = Path.Combine(_journalRoot, DeviceId);
        _currentFile = Path.Combine(_deviceDirectory, "current.ndjson");
        _checkpointFile = Path.Combine(_deviceDirectory, "checkpoint.json.gz");

        Directory.CreateDirectory(localWorkspaceDirectory);
        LocalDatabasePath = Path.Combine(localWorkspaceDirectory, "journal.db");
        _localConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = LocalDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
        InitializeLocalDatabase();
    }

    /// <summary>
    /// Registra localmente, PRIMA del JSON write, tutto ciò che serve a pubblicare il commit dopo un crash.
    /// Il recoveryJson resta in journal.db e non intasa SharePoint. newVersion deve avere già RevisioneDati e
    /// stamp definitivi che verranno scritti nel JSON autorevole. Per una delete definitiva, prima di qualunque
    /// rimozione viene inoltre verificata una copia completa nel recovery-bin condiviso.
    /// </summary>
    public JournalPendingWrite Prepare(
        JournalOperation operation,
        TestCase? baseVersion,
        TestCase? newVersion,
        string? oldPath,
        string? newPath)
    {
        var eventId = Guid.NewGuid().ToString("N");
        var entity = newVersion ?? baseVersion ?? throw new ArgumentException("Serve almeno una versione del TestCase.");

        if (operation == JournalOperation.Delete)
        {
            if (baseVersion == null || string.IsNullOrWhiteSpace(oldPath))
                throw new InvalidOperationException("Una cancellazione Test definitiva richiede versione base e percorso autorevole.");
            DomainRecoveryService.ArchiveOnly(
                _paths, oldPath, DomainRecoveryCategory.TestCase,
                string.IsNullOrWhiteSpace(baseVersion.ModificatoDa) ? null : baseVersion.ModificatoDa);
        }

        var evt = new TestJournalEvent
        {
            SchemaVersion = JournalSchemaVersion,
            EventId = eventId,
            DeviceId = DeviceId,
            TimestampUtc = DateTime.UtcNow,
            Operation = operation,
            EntityUid = entity.Uid,
            EntityId = entity.Id,
            BaseRevision = baseVersion?.RevisioneDati,
            BaseHash = baseVersion == null ? null : TestCaseFingerprint.Compute(baseVersion),
            NewRevision = newVersion?.RevisioneDati,
            NewHash = newVersion == null ? null : TestCaseFingerprint.Compute(newVersion),
            OldRelativePath = Relative(oldPath),
            NewRelativePath = Relative(newPath)
        };

        using var connection = OpenLocal();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO journal_entry(event_id, state, created_utc_ticks, event_json, recovery_json)
            VALUES($id, 0, $created, $event, $recovery);
            """;
        command.Parameters.AddWithValue("$id", eventId);
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.Ticks);
        command.Parameters.AddWithValue("$event", JsonSerializer.Serialize(evt, _options));
        var recoveryVersion = newVersion ?? (operation == JournalOperation.Delete ? baseVersion : null);
        command.Parameters.AddWithValue("$recovery", recoveryVersion == null
            ? DBNull.Value
            : JsonSerializer.Serialize(recoveryVersion, JsonIo.Options));
        command.ExecuteNonQuery();

        var pending = new JournalPendingWrite(evt, eventId);
        if (WorkflowTransactionService.IsActive)
            WorkflowTransactionService.DeferAfterRollback(() => Cancel(pending));
        return pending;
    }

    public void Cancel(JournalPendingWrite? pending)
    {
        if (pending == null) return;
        try
        {
            using var connection = OpenLocal();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM journal_entry WHERE event_id = $id AND state = 0;";
            command.Parameters.AddWithValue("$id", pending.LocalKey);
            command.ExecuteNonQuery();
        }
        catch { /* local DB: il record verrà ignorato dal flush se il JSON non corrisponde */ }
    }

    /// <summary>
    /// Pubblica l'evento nel singolo current.ndjson del device. Se OneDrive è occupato/offline la riga resta
    /// pending in journal.db: il salvataggio JSON autorevole non viene trasformato in errore per colpa del journal.
    /// Un workspace diventato sola lettura tra il commit del Test e la pubblicazione non viene mai mutato: la
    /// outbox locale conserva l'evento e lo ripubblica solo quando il gate condiviso torna operativo.
    /// </summary>
    public bool TryPublish(JournalPendingWrite pending)
    {
        if (!WorkspaceWritePolicy.CanWrite(_paths)) return false;
        lock (_gate)
        {
            try
            {
                // Ripeti il gate dentro la sezione serializzata: una transaction straniera può essere comparsa
                // mentre questo thread aspettava _gate. La transaction corrente è ignorata tramite ActiveId.
                if (!WorkspaceWritePolicy.CanWrite(_paths)) return false;
                EnsureSharedDirectory();
                AppendEvent(pending.Event);
                MarkPublished(pending.LocalKey);
                TryRotateAndCompact();
                CleanupLocalRecovery();
                return true;
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Journal TestCase: publish", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Riprova gli eventi rimasti nell'outbox dopo crash/offline. Un evento viene pubblicato solo se il JSON
    /// corrente conferma esattamente UID/revisione/hash attesi; altrimenti resta locale per diagnosi/recovery e
    /// non inventa una transazione che non è mai stata completata.
    /// </summary>
    public void FlushOutbox()
    {
        foreach (var pending in LoadPending())
        {
            try
            {
                if (!AuthoritativeStateMatches(pending.Event)) continue;
                TryPublish(pending);
            }
            catch (Exception ex) { CrashLogService.Registra("Journal TestCase: flush outbox", ex); }
        }
    }

    /// <summary>
    /// Merge automatico fork (6.4.1, punto B, decisione orchestratore): registra SOLO in journal.db LOCALE
    /// (mai nella cartella condivisa, nessun cambio al formato dei file condivisi) la versione BASE da cui è
    /// partito un salvataggio — lo stesso oggetto di cui <see cref="Prepare"/> calcola già BaseHash/BaseRevision
    /// per l'evento journal, cosi' un fork rilevato più tardi può essere sottoposto a un vero three-way merge
    /// (<see cref="TestCaseThreeWayMerge.Prepara"/>) invece del solo confronto a due rami senza base
    /// (<see cref="TestCaseThreeWayMerge.PreparaFork"/>) usato quando la base non è (più) disponibile.
    /// Best-effort: un fallimento qui non deve mai compromettere il salvataggio autorevole del Test.
    /// </summary>
    public void PersistLocalBase(TestCase baseVersion)
    {
        if (baseVersion.Uid == Guid.Empty) return;
        try
        {
            var hash = TestCaseFingerprint.GetStored(baseVersion) ?? TestCaseFingerprint.Compute(baseVersion);
            var uidKey = baseVersion.Uid.ToString("N");
            using var connection = OpenLocal();
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT OR REPLACE INTO base_snapshot(entity_uid, base_hash, base_revision, created_utc_ticks, base_json)
                    VALUES($uid, $hash, $rev, $created, $json);
                    """;
                insert.Parameters.AddWithValue("$uid", uidKey);
                insert.Parameters.AddWithValue("$hash", hash);
                insert.Parameters.AddWithValue("$rev", baseVersion.RevisioneDati);
                insert.Parameters.AddWithValue("$created", DateTime.UtcNow.Ticks);
                insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(baseVersion, JsonIo.Options));
                insert.ExecuteNonQuery();
            }

            // Retention per numero (oltre alla finestra temporale applicata da CleanupLocalRecovery): non deve
            // crescere indefinitamente per un Test salvato molto spesso sullo stesso PC.
            using var trim = connection.CreateCommand();
            trim.CommandText = """
                DELETE FROM base_snapshot
                WHERE entity_uid = $uid AND base_hash NOT IN (
                    SELECT base_hash FROM base_snapshot WHERE entity_uid = $uid
                    ORDER BY created_utc_ticks DESC LIMIT $keep
                );
                """;
            trim.Parameters.AddWithValue("$uid", uidKey);
            trim.Parameters.AddWithValue("$keep", MaxLocalBasesPerEntity);
            trim.ExecuteNonQuery();
        }
        catch (Exception ex) { CrashLogService.Registra("Journal TestCase: persist local base", ex); }
    }

    /// <summary>Legge una base salvata localmente da <see cref="PersistLocalBase"/>. Restituisce false se questo
    /// PC non ha (più, per retention) la base con quell'hash: in quel caso il merge automatico del fork non è
    /// eleggibile su questo PC (resta al Master, comportamento invariato).</summary>
    public bool TryGetLocalBase(Guid entityUid, string baseHash, out TestCase? baseVersion)
    {
        baseVersion = null;
        if (entityUid == Guid.Empty || string.IsNullOrWhiteSpace(baseHash)) return false;
        try
        {
            using var connection = OpenLocal();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT base_json FROM base_snapshot WHERE entity_uid = $uid AND base_hash = $hash LIMIT 1;";
            command.Parameters.AddWithValue("$uid", entityUid.ToString("N"));
            command.Parameters.AddWithValue("$hash", baseHash);
            var json = command.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(json)) return false;
            baseVersion = JsonSerializer.Deserialize<TestCase>(json, _options);
            return baseVersion != null;
        }
        catch (Exception ex) { CrashLogService.Registra("Journal TestCase: read local base", ex); return false; }
    }

    /// <summary>
    /// Scansione condivisa esplicita, da chiamare in background (mai nel costruttore/startup critico).
    /// Legge checkpoint + pochi segmenti per device, deduplica gli eventi e rileva figli divergenti della
    /// stessa base. I fork già noti nei checkpoint restano visibili anche dopo la compattazione, SALVO quelli
    /// per cui il Test JSON autorevole contiene già lo stamp di risoluzione Master. In sola lettura la scansione
    /// resta pienamente disponibile, ma non pubblica recovery/checkpoint condivisi.
    /// </summary>
    public IReadOnlyList<JournalForkConflict> RefreshKnownForks()
    {
        lock (_gate)
        {
            var events = new Dictionary<string, TestJournalEvent>(StringComparer.OrdinalIgnoreCase);
            var persistedForks = new Dictionary<string, JournalForkConflict>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(_journalRoot))
            {
                foreach (var deviceDir in SafeDirectories(_journalRoot))
                {
                    if (TryReadCheckpoint(Path.Combine(deviceDir, "checkpoint.json.gz"), out var checkpoint) && checkpoint != null)
                    {
                        foreach (var head in checkpoint.Heads.Values) events[head.EventId] = head;
                        foreach (var fork in checkpoint.DetectedForks) persistedForks[ForkKey(fork)] = fork;
                    }

                    foreach (var file in SafeFiles(deviceDir, "*.ndjson"))
                    {
                        // Un file parziale/di schema futuro non viene interpretato a metà. La scansione fork può
                        // proseguire sugli altri device/file; questo file sarà ritentato quando sarà leggibile.
                        if (!TryReadEvents(file, out var fileEvents)) continue;
                        foreach (var evt in fileEvents) events[evt.EventId] = evt;
                    }
                }
            }

            _knownRevisionHashes = events.Values
                .Where(e => !string.IsNullOrWhiteSpace(e.NewHash))
                .Select(e => $"{e.EntityUid:N}|{e.NewHash}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var detected = DetectForks(events.Values);
            foreach (var fork in detected) persistedForks[ForkKey(fork)] = fork;

            _knownForks = persistedForks.Values
                .Where(fork => !IsResolvedByAuthoritativeState(fork, events.Values))
                .OrderByDescending(f => f.DetectedUtc)
                .ToList();

            if (WorkspaceWritePolicy.CanWrite(_paths))
            {
                foreach (var fork in _knownForks) PublishLocalRecoveryForFork(fork);
                TryPersistOwnCheckpointForks();
            }

            // journal.db è locale/disposable e può essere manutenuto anche se OneDrive è temporaneamente
            // congelato: non modifica alcun file condiviso e non altera l'autorità dei JSON.
            CleanupLocalRecovery();
            return _knownForks;
        }
    }

    public bool HasKnownFork(Guid uid)
    {
        lock (_gate) return _knownForks.Any(f => f.EntityUid == uid);
    }

    private void InitializeLocalDatabase()
    {
        using var connection = OpenLocal();
        SqliteWalGuard.CheckpointIfOversized(connection, LocalDatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout={LocalBusyTimeoutMs};
            CREATE TABLE IF NOT EXISTS journal_entry(
                event_id TEXT PRIMARY KEY,
                state INTEGER NOT NULL,
                created_utc_ticks INTEGER NOT NULL,
                event_json TEXT NOT NULL,
                recovery_json TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_journal_entry_state_created
                ON journal_entry(state, created_utc_ticks);
            CREATE TABLE IF NOT EXISTS base_snapshot(
                entity_uid TEXT NOT NULL,
                base_hash TEXT NOT NULL,
                base_revision INTEGER,
                created_utc_ticks INTEGER NOT NULL,
                base_json TEXT NOT NULL,
                PRIMARY KEY(entity_uid, base_hash)
            );
            CREATE INDEX IF NOT EXISTS ix_base_snapshot_entity_created
                ON base_snapshot(entity_uid, created_utc_ticks);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenLocal()
    {
        var connection = new SqliteConnection(_localConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={LocalBusyTimeoutMs};";
        command.ExecuteNonQuery();
        return connection;
    }

    private IReadOnlyList<JournalPendingWrite> LoadPending()
    {
        using var connection = OpenLocal();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id, event_json FROM journal_entry WHERE state = 0 ORDER BY created_utc_ticks;";
        using var reader = command.ExecuteReader();
        var result = new List<JournalPendingWrite>();
        while (reader.Read())
        {
            var evt = JsonSerializer.Deserialize<TestJournalEvent>(reader.GetString(1), _options);
            if (evt != null) result.Add(new JournalPendingWrite(evt, reader.GetString(0)));
        }
        return result;
    }

    private void MarkPublished(string eventId)
    {
        using var connection = OpenLocal();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE journal_entry SET state = 1 WHERE event_id = $id;";
        command.Parameters.AddWithValue("$id", eventId);
        command.ExecuteNonQuery();
    }

    private string? LoadRecoveryJson(string eventId)
    {
        using var connection = OpenLocal();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT recovery_json FROM journal_entry WHERE event_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", eventId);
        return command.ExecuteScalar() as string;
    }

    private bool AuthoritativeStateMatches(TestJournalEvent evt)
    {
        if (evt.NewHash == null)
        {
            if (string.IsNullOrWhiteSpace(evt.OldRelativePath)) return true;
            return !File.Exists(Path.Combine(_paths.Root, evt.OldRelativePath));
        }
        if (string.IsNullOrWhiteSpace(evt.NewRelativePath)) return false;
        var path = Path.Combine(_paths.Root, evt.NewRelativePath);
        if (!File.Exists(path)) return false;
        var tc = JsonIo.TryRead<TestCase>(path, out _);
        return tc != null && tc.Uid == evt.EntityUid && tc.RevisioneDati == evt.NewRevision &&
               TestCaseFingerprint.Compute(tc).Equals(evt.NewHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Append dell'evento sul journal condiviso. Un errore di radice persa è osservato dal monitor di sola lettura
    /// (6.6.1, correzione C) e poi rilanciato come prima: il chiamante non cambia comportamento.</summary>
    private void AppendEvent(TestJournalEvent evt)
    {
        try
        {
            AppendEventCore(evt);
        }
        catch (Exception ex)
        {
            var sostitutiva = SharedWriteLossMonitor.Osserva(_currentFile, ex);
            if (sostitutiva != null) throw sostitutiva;
            throw;
        }
    }

    private void AppendEventCore(TestJournalEvent evt)
    {
        Directory.CreateDirectory(_deviceDirectory);
        // Riparazione della coda e append sotto il mutex cross-processo (stesso contratto di PhaseDMasterJournalService):
        // una coda parziale da crash viene spostata in current.ndjson.torn-*, mai troncata alla cieca.
        using var lockCrossProcesso = new NdjsonCodaRiparazione.LockCrossProcesso("TestJournal", _currentFile);
        var prefissoRiga = NdjsonCodaRiparazione.PreparaAccodamento(
            _currentFile, RigaJournalValida, "TestJournal",
            "Journal TestCase current.ndjson termina con una riga parziale non riparabile: append rinviato senza modificare il file.");

        var line = prefissoRiga + JsonSerializer.Serialize(evt, _options) + Environment.NewLine;
        using var stream = new FileStream(_currentFile, FileMode.Append, FileAccess.Write, FileShare.Read,
            64 * 1024, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(line);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Riga del journal valida come evento dello schema corrente: usata solo dalla riparazione della coda parziale.</summary>
    private bool RigaJournalValida(string riga)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<TestJournalEvent>(riga, _options);
            return evt != null && evt.SchemaVersion == JournalSchemaVersion;
        }
        catch (JsonException) { return false; }
    }

    private void TryRotateAndCompact()
    {
        try
        {
            if (File.Exists(_currentFile) && new FileInfo(_currentFile).Length >= _rotateAfterBytes)
            {
                var segment = Path.Combine(_deviceDirectory,
                    $"segment-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.ndjson");
                File.Move(_currentFile, segment, overwrite: false);
            }

            var segments = SafeFiles(_deviceDirectory, "segment-*.ndjson")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (segments.Count <= _completedSegmentsToKeep) return;

            var toCompact = segments.Take(segments.Count - _completedSegmentsToKeep).ToList();
            var parsedByFile = new List<(string File, IReadOnlyList<TestJournalEvent> Events)>(toCompact.Count);
            foreach (var file in toCompact)
            {
                if (!TryReadEvents(file, out var events))
                {
                    // Mai cancellare un segmento se non sappiamo di averne interpretato ogni riga. Può essere
                    // una consegna OneDrive parziale oppure uno schema futuro che questo client non conosce.
                    return;
                }
                parsedByFile.Add((file, events));
            }

            if (!TryReadCheckpoint(_checkpointFile, out var checkpoint))
            {
                // Presente ma non leggibile/incompatibile: non sostituirlo con un checkpoint vuoto.
                return;
            }
            checkpoint ??= new JournalCheckpoint();
            foreach (var (_, events) in parsedByFile)
            {
                foreach (var evt in events)
                {
                    var key = evt.EntityUid.ToString("N");
                    if (!checkpoint.Heads.TryGetValue(key, out var current) || IsNewer(evt, current))
                        checkpoint.Heads[key] = evt;
                }
            }
            checkpoint.GeneratedUtc = DateTime.UtcNow;
            checkpoint.DeviceId = DeviceId;
            checkpoint.DetectedForks = MergeForks(checkpoint.DetectedForks, _knownForks);
            WriteCheckpointAtomic(_checkpointFile, checkpoint);

            foreach (var file in toCompact)
                try { File.Delete(file); } catch { }
        }
        catch (Exception ex) { CrashLogService.Registra("Journal TestCase: rotation/compaction", ex); }
    }

    private void TryPersistOwnCheckpointForks()
    {
        try
        {
            if (!Directory.Exists(_deviceDirectory)) return;
            if (!TryReadCheckpoint(_checkpointFile, out var checkpoint))
            {
                // Un client vecchio non deve riscrivere un checkpoint di schema futuro o corrotto.
                return;
            }
            checkpoint ??= new JournalCheckpoint { DeviceId = DeviceId };

            var replacement = _knownForks.ToList();
            var oldKeys = checkpoint.DetectedForks.Select(ForkKey).OrderBy(x => x).ToArray();
            var newKeys = replacement.Select(ForkKey).OrderBy(x => x).ToArray();
            if (oldKeys.SequenceEqual(newKeys, StringComparer.OrdinalIgnoreCase)) return;
            checkpoint.GeneratedUtc = DateTime.UtcNow;
            checkpoint.DetectedForks = replacement;
            WriteCheckpointAtomic(_checkpointFile, checkpoint);
        }
        catch (Exception ex) { CrashLogService.Registra("Journal TestCase: persist fork checkpoint", ex); }
    }

    private static List<JournalForkConflict> DetectForks(IEnumerable<TestJournalEvent> source)
    {
        var events = source.Where(e => e.EntityUid != Guid.Empty && !string.IsNullOrWhiteSpace(e.NewHash)).ToList();
        var result = new Dictionary<string, JournalForkConflict>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in events.Where(e => e.BaseRevision.HasValue && !string.IsNullOrWhiteSpace(e.BaseHash))
                     .GroupBy(e => new { e.EntityUid, e.BaseRevision, BaseHash = e.BaseHash!.ToUpperInvariant() }))
        {
            var branches = group.GroupBy(e => new { e.NewRevision, Hash = e.NewHash!.ToUpperInvariant() })
                .Select(g => g.OrderByDescending(e => e.TimestampUtc).First()).ToList();
            if (branches.Count <= 1) continue;
            var fork = new JournalForkConflict
            {
                EntityUid = group.Key.EntityUid,
                EntityId = branches.Select(b => b.EntityId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "",
                BaseRevision = group.Key.BaseRevision,
                BaseHash = group.Key.BaseHash,
                DetectedUtc = DateTime.UtcNow,
                Branches = branches
            };
            result[ForkKey(fork)] = fork;
        }

        foreach (var group in events.Where(e => e.NewRevision.HasValue)
                     .GroupBy(e => new { e.EntityUid, e.NewRevision }))
        {
            var branches = group.GroupBy(e => e.NewHash!, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(e => e.TimestampUtc).First()).ToList();
            if (branches.Count <= 1) continue;
            var baseRevisions = branches.Select(b => b.BaseRevision).Where(x => x.HasValue).Select(x => x!.Value).ToList();
            long? baseRevision = baseRevisions.Count == 0 ? null : baseRevisions.Min();
            var baseHash = branches.Select(b => b.BaseHash).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
            var fork = new JournalForkConflict
            {
                EntityUid = group.Key.EntityUid,
                EntityId = branches.Select(b => b.EntityId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "",
                BaseRevision = baseRevision,
                BaseHash = baseHash,
                DetectedUtc = DateTime.UtcNow,
                Branches = branches
            };
            result[ForkKey(fork)] = fork;
        }
        return result.Values.ToList();
    }

    private void PublishLocalRecoveryForFork(JournalForkConflict fork)
    {
        var localIds = fork.Branches.Where(b => b.DeviceId == DeviceId).Select(b => b.EventId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (localIds.Count == 0) return;

        foreach (var eventId in localIds)
        {
            try
            {
                var recoveryJson = LoadRecoveryJson(eventId);
                if (string.IsNullOrWhiteSpace(recoveryJson)) continue;
                var dir = Path.Combine(_paths.Root, "_ditm", "conflicts", fork.EntityUid.ToString("N"));
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, eventId + ".json.gz");
                if (File.Exists(target)) continue;
                var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
                using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
                    writer.Write(recoveryJson);
                File.Move(temp, target, overwrite: false);
            }
            catch (Exception ex) { CrashLogService.Registra("Journal TestCase: publish conflict recovery", ex); }
        }
    }

    private void CleanupLocalRecovery()
    {
        var cutoff = DateTime.UtcNow.AddDays(-LocalRecoveryRetentionDays).Ticks;
        var protectedIds = _knownForks.SelectMany(f => f.Branches).Select(b => b.EventId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var connection = OpenLocal();
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT event_id FROM journal_entry WHERE state = 1 AND created_utc_ticks < $cutoff;";
            read.Parameters.AddWithValue("$cutoff", cutoff);
            var removable = new List<string>();
            using (var reader = read.ExecuteReader())
                while (reader.Read())
                    if (!protectedIds.Contains(reader.GetString(0))) removable.Add(reader.GetString(0));

            if (removable.Count == 0) return;
            using var transaction = connection.BeginTransaction();
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM journal_entry WHERE event_id = $id;";
            var id = delete.Parameters.Add("$id", SqliteType.Text);
            delete.Prepare();
            foreach (var eventId in removable)
            {
                id.Value = eventId;
                delete.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch (Exception ex) { CrashLogService.Registra("Journal TestCase: cleanup locale", ex); }

        CleanupLocalBaseSnapshots(cutoff);
    }

    /// <summary>Stessa finestra di retention della recovery locale (90 giorni), ma protegge sempre le basi di
    /// un fork ancora aperto (BaseHash), anche se più vecchie: il merge automatico deve poterle ancora usare
    /// finché il fork non è chiuso.</summary>
    private void CleanupLocalBaseSnapshots(long cutoffTicks)
    {
        var basiProtette = _knownForks
            .Where(f => !string.IsNullOrWhiteSpace(f.BaseHash))
            .Select(f => (Uid: f.EntityUid.ToString("N"), Hash: f.BaseHash.ToUpperInvariant()))
            .ToHashSet();
        try
        {
            using var connection = OpenLocal();
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT entity_uid, base_hash FROM base_snapshot WHERE created_utc_ticks < $cutoff;";
            read.Parameters.AddWithValue("$cutoff", cutoffTicks);
            var removable = new List<(string Uid, string Hash)>();
            using (var reader = read.ExecuteReader())
                while (reader.Read())
                {
                    var uid = reader.GetString(0);
                    var hash = reader.GetString(1);
                    if (!basiProtette.Contains((uid, hash.ToUpperInvariant()))) removable.Add((uid, hash));
                }
            if (removable.Count == 0) return;

            using var transaction = connection.BeginTransaction();
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM base_snapshot WHERE entity_uid = $uid AND base_hash = $hash;";
            var uidParam = delete.Parameters.Add("$uid", SqliteType.Text);
            var hashParam = delete.Parameters.Add("$hash", SqliteType.Text);
            delete.Prepare();
            foreach (var (uid, hash) in removable)
            {
                uidParam.Value = uid;
                hashParam.Value = hash;
                delete.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch (Exception ex) { CrashLogService.Registra("Journal TestCase: cleanup base_snapshot locale", ex); }
    }

    private bool TryReadEvents(string file, out List<TestJournalEvent> result)
    {
        result = new List<TestJournalEvent>();
        try
        {
            // ReadLinesShared: la fork detection non deve restare incompleta per una contesa I/O.
            foreach (var line in JsonIo.ReadLinesShared(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                TestJournalEvent? evt;
                try { evt = JsonSerializer.Deserialize<TestJournalEvent>(line, _options); }
                catch { return false; }
                if (evt == null || evt.SchemaVersion != JournalSchemaVersion) return false;
                result.Add(evt);
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// true significa che il path è assente oppure contiene un checkpoint integro dello schema corrente.
    /// false significa che un file esiste ma è parziale, corrotto o di schema futuro: i writer devono
    /// preservarlo byte-per-byte e rinunciare alla compattazione/persistenza invece di ricrearlo da zero.
    /// </summary>
    private bool TryReadCheckpoint(string path, out JournalCheckpoint? checkpoint)
    {
        checkpoint = null;
        if (!File.Exists(path)) return true;
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            checkpoint = JsonSerializer.Deserialize<JournalCheckpoint>(gzip, _options);
            if (checkpoint == null || checkpoint.SchemaVersion != JournalSchemaVersion)
            {
                checkpoint = null;
                return false;
            }
            return true;
        }
        catch
        {
            checkpoint = null;
            return false;
        }
    }

    private void WriteCheckpointAtomic(string path, JournalCheckpoint checkpoint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            JsonSerializer.Serialize(gzip, checkpoint, _options);
        File.Move(temp, path, overwrite: true);
    }

    private bool IsResolvedByAuthoritativeState(JournalForkConflict fork, IEnumerable<TestJournalEvent> events)
    {
        var candidatePaths = events
            .Where(e => e.EntityUid == fork.EntityUid && !string.IsNullOrWhiteSpace(e.NewRelativePath))
            .OrderByDescending(e => e.NewRevision ?? -1)
            .ThenByDescending(e => e.TimestampUtc)
            .Select(e => e.NewRelativePath!)
            .Concat(fork.Branches.Where(b => !string.IsNullOrWhiteSpace(b.NewRelativePath)).Select(b => b.NewRelativePath!))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var relative in candidatePaths)
        {
            try
            {
                var path = Path.Combine(_paths.Root, relative);
                if (!File.Exists(path)) continue;
                var current = JsonIo.TryRead<TestCase>(path, out _);
                if (current == null || current.Uid != fork.EntityUid) continue;
                if (TestCaseForkResolution.IsResolved(current, fork)) return true;
            }
            catch
            {
            }
        }

        return false;
    }

    private static List<JournalForkConflict> MergeForks(IEnumerable<JournalForkConflict> a, IEnumerable<JournalForkConflict> b) =>
        a.Concat(b).GroupBy(ForkKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.DetectedUtc).First()).ToList();

    private static bool IsNewer(TestJournalEvent candidate, TestJournalEvent current)
    {
        if (candidate.NewRevision != current.NewRevision)
            return (candidate.NewRevision ?? -1) > (current.NewRevision ?? -1);
        return candidate.TimestampUtc > current.TimestampUtc;
    }

    private static string ForkKey(JournalForkConflict fork) =>
        $"{fork.EntityUid:N}|{fork.BaseRevision}|{fork.BaseHash}|{string.Join(',', fork.Branches.Select(b => b.NewHash).OrderBy(x => x))}";

    private string? Relative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetRelativePath(_paths.Root, path); }
        catch { return path; }
    }

    private void EnsureSharedDirectory() => Directory.CreateDirectory(_deviceDirectory);

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
}

public enum JournalOperation
{
    Create,
    Save,
    Move,
    SoftDelete,
    Restore,
    Delete,
    Merge
}

public sealed class TestJournalEvent
{
    public int SchemaVersion { get; set; }
    public string EventId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public JournalOperation Operation { get; set; }
    public Guid EntityUid { get; set; }
    public string EntityId { get; set; } = "";
    public long? BaseRevision { get; set; }
    public string? BaseHash { get; set; }
    public long? NewRevision { get; set; }
    public string? NewHash { get; set; }
    public string? OldRelativePath { get; set; }
    public string? NewRelativePath { get; set; }
}

public sealed record JournalPendingWrite(TestJournalEvent Event, string LocalKey);

public sealed class JournalCheckpoint
{
    public int SchemaVersion { get; set; } = SharedTestJournalService.JournalSchemaVersion;
    public string DeviceId { get; set; } = "";
    public DateTime GeneratedUtc { get; set; }
    public Dictionary<string, TestJournalEvent> Heads { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<JournalForkConflict> DetectedForks { get; set; } = new();
}

public sealed class JournalForkConflict
{
    public Guid EntityUid { get; set; }
    public string EntityId { get; set; } = "";
    public long? BaseRevision { get; set; }
    public string BaseHash { get; set; } = "";
    public DateTime DetectedUtc { get; set; }
    public List<TestJournalEvent> Branches { get; set; } = new();
}
