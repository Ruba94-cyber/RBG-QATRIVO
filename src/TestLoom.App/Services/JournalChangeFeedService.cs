using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Usa il journal bounded come change-feed per un warm-start senza enumerare tutti i testcase_*.json.
/// Lo stato di consumo vive nello stesso journal.db LOCALE: SharePoint non riceve alcun file aggiuntivo.
///
/// Il feed e solo un'accelerazione: watcher + deep reconciliation periodica restano la rete di sicurezza per
/// modifiche manuali esterne a TestLoom, file OneDrive temporaneamente indisponibili o metadati non leggibili.
/// </summary>
public sealed class JournalChangeFeedService
{
    private const int BusyTimeoutMs = 500;
    private const int ProcessedRetentionDays = 60;
    private readonly string _root;
    private readonly string _journalRoot;
    private readonly string _connectionString;
    private readonly string _indexConnectionString;
    private readonly JsonSerializerOptions _options = new(JsonIo.Options) { WriteIndented = false };

    public JournalChangeFeedService(DataPaths paths, string localWorkspaceDirectory)
    {
        _root = paths.Root;
        _journalRoot = Path.Combine(paths.Root, "_ditm", "journal");
        // Cache privata per connessione: la cache condivisa farebbe fallire le letture concorrenti sullo stesso file
        // con SQLITE_LOCKED, ignorando busy_timeout. Journal e indice locali sono file del solo PC.
        var journalDb = Path.Combine(localWorkspaceDirectory, "journal.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = journalDb,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true
        }.ToString();
        _indexConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(localWorkspaceDirectory, "index.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = true
        }.ToString();
        Initialize();
    }

    /// <summary>
    /// Vero se la baseline e' stata stabilita. Un journal locale illeggibile equivale a "nessuna baseline": il chiamante
    /// ripete la ricostruzione completa invece di far cadere l'avvio.
    /// </summary>
    public bool HasBaseline
    {
        get
        {
            try { return string.Equals(ReadMeta("baseline"), "1", StringComparison.Ordinal); }
            catch (Exception ex)
            {
                CrashLogService.Registra("Journal change-feed: lettura baseline", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Fotografia del journal da prendere PRIMA di una ricostruzione autorevole dell'indice (RefreshIndex). La
    /// ricostruzione legge il disco dopo questo istante: gli eventi gia' nella fotografia sono coperti da essa, mentre
    /// quelli scritti durante la ricostruzione restano non consumati e il Poll successivo li applica. Null se il journal
    /// non e' leggibile: il chiamante non stabilisce la baseline e ripete la ricostruzione al giro successivo.
    /// </summary>
    public JournalFeedSnapshot? CatturaFotografia()
    {
        try { return ReadChangedFiles(ignoreFileState: true); }
        catch (Exception ex)
        {
            CrashLogService.Registra("Journal change-feed: fotografia baseline", ex);
            return null;
        }
    }

    /// <summary>
    /// Dopo una full reconciliation autorevole, il journal contenuto nella <paramref name="fotografia"/> (presa prima
    /// della ricostruzione, vedi <see cref="CatturaFotografia"/>) e' coperto dall'indice appena ricostruito: lo marchiamo
    /// consumato, cosi' gli avvii successivi leggono solo delta. Gli eventi scritti dopo la fotografia restano pendenti.
    /// </summary>
    public bool EstablishBaseline(JournalFeedSnapshot? fotografia)
    {
        if (fotografia == null) return false;
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var evt in fotografia.Events) MarkProcessed(connection, transaction, evt);
            foreach (var file in fotografia.Files) WriteFileState(connection, transaction, file);
            WriteMeta(connection, transaction, "baseline", "1");
            transaction.Commit();
            CleanupProcessed();
            return true;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Journal change-feed: baseline", ex);
            return false;
        }
    }

    /// <summary>
    /// Come <see cref="EstablishBaseline(JournalFeedSnapshot?)"/> con una fotografia presa adesso: corretto solo quando
    /// nessuna ricostruzione dell'indice avviene tra la fotografia e la baseline (per esempio nei test).
    /// </summary>
    public bool EstablishBaseline() => EstablishBaseline(CatturaFotografia());

    /// <summary>
    /// Ritorna i path TestCase da riallineare ma NON avanza il cursore. Il chiamante deve chiamare Ack solo
    /// dopo che RefreshPaths e terminato: un crash/errore prima dell'Ack fara semplicemente riprocessare il
    /// delta, operazione idempotente.
    /// </summary>
    public JournalFeedBatch Poll()
    {
        if (!HasBaseline) return JournalFeedBatch.FullRequired();
        try
        {
            var snapshot = ReadChangedFiles(ignoreFileState: false);
            if (snapshot.Files.Count == 0) return new JournalFeedBatch();

            HashSet<string> processed;
            using (var connection = Open()) processed = LoadProcessed(connection, snapshot.Events.Select(e => e.EventId));

            var pending = snapshot.Events.Where(e => !processed.Contains(e.EventId)).ToList();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var evt in pending)
            {
                AddRelative(paths, evt.OldRelativePath);
                AddRelative(paths, evt.NewRelativePath);
                // Se l'evento e stato compattato nel checkpoint, il suo OldRelativePath puo non essere il
                // path che questo PC aveva prima di restare offline. UID -> path dall'indice locale elimina
                // anche quel record stale senza dover conservare una storia infinita di rename nel journal.
                var localPath = FindIndexedPath(evt.EntityUid);
                if (!string.IsNullOrWhiteSpace(localPath)) paths.Add(localPath);
            }

            return new JournalFeedBatch
            {
                Paths = paths.ToList(),
                Events = pending,
                Files = snapshot.Files
            };
        }
        catch (Exception ex)
        {
            // Non bloccare UI e non azzerare la cache buona. Una deep reconciliation periodica ripara anche
            // questo caso; fino ad allora watcher e JSON-save checks continuano a proteggere la correttezza.
            CrashLogService.Registra("Journal change-feed: poll", ex);
            return new JournalFeedBatch { HadReadError = true };
        }
    }

    public void Ack(JournalFeedBatch batch)
    {
        if (batch.RequiresFullReconciliation) return;
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var evt in batch.Events) MarkProcessed(connection, transaction, evt);
            foreach (var file in batch.Files) WriteFileState(connection, transaction, file);
            transaction.Commit();
            CleanupProcessed();
        }
        catch (Exception ex) { CrashLogService.Registra("Journal change-feed: ack", ex); }
    }

    private JournalFeedSnapshot ReadChangedFiles(bool ignoreFileState)
    {
        var result = new JournalFeedSnapshot();
        if (!Directory.Exists(_journalRoot)) return result;

        Dictionary<string, JournalFeedFileState> known = new(StringComparer.OrdinalIgnoreCase);
        if (!ignoreFileState)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path, mtime_ticks, length FROM feed_file_state;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) known[reader.GetString(0)] = new JournalFeedFileState(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2));
        }

        foreach (var deviceDir in SafeDirectories(_journalRoot))
        {
            var checkpoint = Path.Combine(deviceDir, "checkpoint.json.gz");
            ReadIfChanged(checkpoint, isCheckpoint: true, known, result, ignoreFileState);
            foreach (var file in SafeFiles(deviceDir, "*.ndjson"))
                ReadIfChanged(file, isCheckpoint: false, known, result, ignoreFileState);
        }
        return result;
    }

    private void ReadIfChanged(
        string path,
        bool isCheckpoint,
        IReadOnlyDictionary<string, JournalFeedFileState> known,
        JournalFeedSnapshot destination,
        bool ignoreState)
    {
        if (!File.Exists(path)) return;
        FileInfo info;
        try { info = new FileInfo(path); }
        catch { return; }
        var state = new JournalFeedFileState(path, info.LastWriteTimeUtc.Ticks, info.Length);
        if (!ignoreState && known.TryGetValue(path, out var old) && old.MtimeTicks == state.MtimeTicks && old.Length == state.Length)
            return;

        var before = destination.Events.Count;
        if (isCheckpoint)
        {
            var checkpoint = ReadCheckpoint(path);
            if (checkpoint == null) return; // non ack: verra ritentato
            destination.Events.AddRange(checkpoint.Heads.Values.Where(Valid));
        }
        else
        {
            if (!TryReadEvents(path, out var events)) return; // non ack su file parziale/malformato
            destination.Events.AddRange(events.Where(Valid));
        }
        destination.Files.Add(state);

        // Deduplica nello stesso batch (current + segmento/checkpoint possono sovrapporsi durante rotazione).
        if (destination.Events.Count > before)
        {
            destination.Events = destination.Events
                .GroupBy(e => e.EventId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(e => e.TimestampUtc).First())
                .ToList();
        }
    }

    private bool TryReadEvents(string path, out List<TestJournalEvent> result)
    {
        result = new List<TestJournalEvent>();
        try
        {
            // ReadLinesShared: un handle di OneDrive sul journal non deve far perdere il delta del giro.
            foreach (var line in JsonIo.ReadLinesShared(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var evt = JsonSerializer.Deserialize<TestJournalEvent>(line, _options);
                    if (evt == null) return false;
                    result.Add(evt);
                }
                catch
                {
                    // current.ndjson può essere osservato mentre OneDrive sta ancora consegnando l'ultima
                    // riga. Una sola riga non deserializzabile rende incompleta l'intera fotografia del file:
                    // non aggiungiamo il suo file-state all'Ack, così il prossimo Poll rilegge tutto il file
                    // anche se le righe precedenti erano già valide. Nessun evento viene quindi saltato.
                    return false;
                }
            }
            return true;
        }
        catch { return false; }
    }

    private JournalCheckpoint? ReadCheckpoint(string path)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<JournalCheckpoint>(gzip, _options);
        }
        catch { return null; }
    }

    private string? FindIndexedPath(Guid uid)
    {
        if (uid == Guid.Empty || !File.Exists(new SqliteConnectionStringBuilder(_indexConnectionString).DataSource)) return null;
        try
        {
            using var connection = new SqliteConnection(_indexConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path FROM test_case_cache WHERE uid = $uid ORDER BY indexed_utc_ticks DESC LIMIT 1;";
            command.Parameters.AddWithValue("$uid", uid.ToString("N"));
            return command.ExecuteScalar() as string;
        }
        catch { return null; }
    }

    private void AddRelative(HashSet<string> paths, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return;
        try { paths.Add(Path.GetFullPath(Path.Combine(_root, relative))); }
        catch { }
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS feed_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS feed_processed_event(
                event_id TEXT PRIMARY KEY,
                event_utc_ticks INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_feed_processed_age ON feed_processed_event(event_utc_ticks);
            CREATE TABLE IF NOT EXISTS feed_file_state(
                path TEXT PRIMARY KEY,
                mtime_ticks INTEGER NOT NULL,
                length INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout={BusyTimeoutMs};";
        command.ExecuteNonQuery();
        return connection;
    }

    private string? ReadMeta(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM feed_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void WriteMeta(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO feed_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static HashSet<string> LoadProcessed(SqliteConnection connection, IEnumerable<string> eventIds)
    {
        var ids = eventIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Bounded journal files keep this list modest. One point-query per id avoids constructing dynamic SQL
        // and is served by the PK index in journal.db.
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM feed_processed_event WHERE event_id = $id LIMIT 1;";
        var p = command.Parameters.Add("$id", SqliteType.Text);
        command.Prepare();
        foreach (var id in ids)
        {
            p.Value = id;
            if (command.ExecuteScalar() != null) result.Add(id);
        }
        return result;
    }

    private static void MarkProcessed(SqliteConnection connection, SqliteTransaction transaction, TestJournalEvent evt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO feed_processed_event(event_id,event_utc_ticks) VALUES($id,$ticks);";
        command.Parameters.AddWithValue("$id", evt.EventId);
        command.Parameters.AddWithValue("$ticks", evt.TimestampUtc.ToUniversalTime().Ticks);
        command.ExecuteNonQuery();
    }

    private static void WriteFileState(SqliteConnection connection, SqliteTransaction transaction, JournalFeedFileState file)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO feed_file_state(path,mtime_ticks,length) VALUES($path,$mtime,$length)
            ON CONFLICT(path) DO UPDATE SET mtime_ticks=excluded.mtime_ticks,length=excluded.length;
            """;
        command.Parameters.AddWithValue("$path", file.Path);
        command.Parameters.AddWithValue("$mtime", file.MtimeTicks);
        command.Parameters.AddWithValue("$length", file.Length);
        command.ExecuteNonQuery();
    }

    private void CleanupProcessed()
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM feed_processed_event WHERE event_utc_ticks < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddDays(-ProcessedRetentionDays).Ticks);
            command.ExecuteNonQuery();
        }
        catch { }
    }

    private static bool Valid(TestJournalEvent evt) =>
        evt.SchemaVersion == SharedTestJournalService.JournalSchemaVersion && !string.IsNullOrWhiteSpace(evt.EventId);

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

    /// <summary>Fotografia del journal condiviso: eventi e stato dei file letti in un unico istante.</summary>
    public sealed class JournalFeedSnapshot
    {
        public List<TestJournalEvent> Events { get; set; } = new();
        public List<JournalFeedFileState> Files { get; } = new();
    }
}

public sealed class JournalFeedBatch
{
    public bool RequiresFullReconciliation { get; init; }
    public bool HadReadError { get; init; }
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<TestJournalEvent> Events { get; init; } = Array.Empty<TestJournalEvent>();
    public IReadOnlyList<JournalFeedFileState> Files { get; init; } = Array.Empty<JournalFeedFileState>();

    public static JournalFeedBatch FullRequired() => new() { RequiresFullReconciliation = true };
}

public sealed record JournalFeedFileState(string Path, long MtimeTicks, long Length);
