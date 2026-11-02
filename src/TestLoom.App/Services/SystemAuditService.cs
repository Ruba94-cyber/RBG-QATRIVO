using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace TestLoom.App.Services;

/// <summary>
/// Audit tecnico durevole per azioni amministrative che non appartengono naturalmente allo storico di una
/// singola entità (es. force unlock). File-count bounded per device, nessuna retention: l'audit è informazione
/// importante e non viene eliminato automaticamente. I segmenti vecchi vengono compressi nel checkpoint.
/// </summary>
public sealed class SystemAuditService
{
    private const int SchemaVersion = 1;
    private const long MaxCurrentBytes = 256 * 1024;
    private const int MaxCompletedSegments = 2;
    private readonly DataPaths _paths;
    private readonly string _deviceId;
    private readonly string _deviceDir;
    private readonly string _currentPath;
    private readonly string _checkpointPath;
    private readonly object _gate = new();

    public SystemAuditService(DataPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _deviceId = DeviceIdentityService.GetOrCreate();
        _deviceDir = Path.Combine(paths.Root, "_ditm", "audit", _deviceId);
        _currentPath = Path.Combine(_deviceDir, "current.ndjson");
        _checkpointPath = Path.Combine(_deviceDir, "checkpoint.json.gz");
    }

    public void Add(SystemAuditEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // Anche l'audit vive sul workspace condiviso. Non deve poter creare directory/segmenti se il client è
        // incompatibile, il root è diventato non disponibile o un altro PC sta applicando una Unit of Work.
        // AppState.RegistraAudit tratta già l'audit come best-effort e registra localmente l'eventuale errore:
        // bloccare qui non può quindi trasformare un'azione amministrativa già riuscita in perdita di dati.
        WorkspaceWritePolicy.EnsureCanWrite(_paths, "Scrittura audit tecnico");

        if (evt.EventId == Guid.Empty) evt.EventId = Guid.NewGuid();
        if (evt.TimestampUtc == default) evt.TimestampUtc = DateTime.UtcNow;
        evt.SchemaVersion = SchemaVersion;
        evt.DeviceId = _deviceId;

        lock (_gate)
        {
            // Ricontrollo dentro il lock: tra il gate precedente e la mutazione potrebbe essere comparso un
            // marker concorrente. La doppia verifica privilegia la consistenza rispetto a pochi microsecondi.
            WorkspaceWritePolicy.EnsureCanWrite(_paths, "Scrittura audit tecnico");
            if (!CurrentNdjsonHasCompleteTail(_currentPath))
                throw new InvalidDataException("Audit current.ndjson con coda incompleta: append bloccata senza modificare il file.");

            Directory.CreateDirectory(_deviceDir);
            using (var stream = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                       32 * 1024, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.WriteLine(JsonSerializer.Serialize(evt, JsonIo.CompactOptions));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            if (new FileInfo(_currentPath).Length >= MaxCurrentBytes) RotateAndCompact();
        }
    }

    public IReadOnlyList<SystemAuditEvent> GetAll(DataPaths paths)
    {
        var all = new Dictionary<Guid, SystemAuditEvent>();
        var root = Path.Combine(paths.Root, "_ditm", "audit");
        if (!Directory.Exists(root)) return Array.Empty<SystemAuditEvent>();
        foreach (var dir in SafeDirectories(root))
        {
            ReadCheckpointPrefixSafe(Path.Combine(dir, "checkpoint.json.gz"), all);
            foreach (var segment in SafeFiles(dir, "segment-*.ndjson")) ReadNdjsonPrefixSafe(segment, all);
            ReadNdjsonPrefixSafe(Path.Combine(dir, "current.ndjson"), all);
        }
        return all.Values.OrderByDescending(x => x.TimestampUtc).ToList();
    }

    private void RotateAndCompact()
    {
        if (!File.Exists(_currentPath) || new FileInfo(_currentPath).Length == 0) return;
        var segment = Path.Combine(_deviceDir, $"segment-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.ndjson");
        File.Move(_currentPath, segment, overwrite: false);

        var segments = SafeFiles(_deviceDir, "segment-*.ndjson").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (segments.Count <= MaxCompletedSegments) return;

        var compact = segments.Take(segments.Count - MaxCompletedSegments).ToList();
        var parsed = new List<SystemAuditEvent>();
        foreach (var file in compact)
        {
            if (!TryReadNdjsonStrict(file, out var fileEvents)) return;
            parsed.AddRange(fileEvents);
        }

        if (!TryReadCheckpoint(_checkpointPath, out var checkpoint)) return;
        var events = checkpoint == null
            ? new Dictionary<Guid, SystemAuditEvent>()
            : checkpoint.Events.ToDictionary(x => x.EventId, x => x);
        foreach (var evt in parsed) events[evt.EventId] = evt;

        WriteCheckpointAtomic(events.Values.OrderBy(x => x.TimestampUtc).ToList());
        foreach (var file in compact) TryDelete(file);
    }

    private void WriteCheckpointAtomic(List<SystemAuditEvent> events)
    {
        Directory.CreateDirectory(_deviceDir);
        var temp = _checkpointPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            using (var gzip = new GZipStream(fs, CompressionLevel.SmallestSize))
                JsonSerializer.Serialize(gzip, new SystemAuditCheckpoint
                {
                    SchemaVersion = SchemaVersion,
                    UpdatedUtc = DateTime.UtcNow,
                    Events = events
                }, JsonIo.Options);
            File.Move(temp, _checkpointPath, overwrite: true);
        }
        finally { TryDelete(temp); }
    }

    /// <summary>Reader di consultazione prefix-safe; non autorizza mai una successiva delete della sorgente.</summary>
    private static void ReadNdjsonPrefixSafe(string path, Dictionary<Guid, SystemAuditEvent> target)
    {
        if (!File.Exists(path)) return;
        var riga = 0;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                riga++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                SystemAuditEvent? evt;
                try { evt = JsonSerializer.Deserialize<SystemAuditEvent>(line, JsonIo.Options); }
                catch (JsonException ex) { SegnalaLetturaInterrotta(path, riga, ex); break; }
                if (!IsValidEvent(evt)) { SegnalaLetturaInterrotta(path, riga, null); break; }
                target[evt!.EventId] = evt;
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
        CrashLogService.Registra($"Registro audit: lettura interrotta alla riga {riga} di {path}", ex);
    }

    private static readonly HashSet<string> InterruzioniSegnalate = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> InterruzioniSegnalateOrdine = new();
    private static readonly object InterruzioniSegnalateGate = new();
    // Evita crescita permanente se nuove righe corrotte compaiono nel tempo su cartelle sincronizzate.
    private const int MassimeInterruzioniMemorizzate = 1024;

    private static void ReadCheckpointPrefixSafe(string path, Dictionary<Guid, SystemAuditEvent> target)
    {
        if (!TryReadCheckpoint(path, out var checkpoint) || checkpoint == null) return;
        foreach (var evt in checkpoint.Events) target[evt.EventId] = evt;
    }

    private static bool TryReadNdjsonStrict(string path, out List<SystemAuditEvent> events)
    {
        events = new List<SystemAuditEvent>();
        if (!File.Exists(path)) return true;
        var ids = new HashSet<Guid>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                SystemAuditEvent? evt;
                try { evt = JsonSerializer.Deserialize<SystemAuditEvent>(line, JsonIo.Options); }
                catch (JsonException) { return false; }
                if (!IsValidEvent(evt) || !ids.Add(evt!.EventId)) return false;
                events.Add(evt);
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// true: checkpoint assente o valido nello schema corrente. false: presente ma corrotto/parziale/futuro;
    /// la compattazione deve fermarsi e preservare checkpoint + segmenti originali.
    /// </summary>
    private static bool TryReadCheckpoint(string path, out SystemAuditCheckpoint? checkpoint)
    {
        checkpoint = null;
        if (!File.Exists(path)) return true;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var gzip = new GZipStream(fs, CompressionMode.Decompress);
            checkpoint = JsonSerializer.Deserialize<SystemAuditCheckpoint>(gzip, JsonIo.Options);
            if (checkpoint == null || checkpoint.SchemaVersion != SchemaVersion || checkpoint.Events == null)
                return false;
            var ids = new HashSet<Guid>();
            return checkpoint.Events.All(evt => IsValidEvent(evt) && ids.Add(evt.EventId));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (InvalidDataException) { return false; }
        catch (JsonException) { return false; }
    }

    private static bool IsValidEvent(SystemAuditEvent? evt) =>
        evt != null && evt.SchemaVersion == SchemaVersion && evt.EventId != Guid.Empty && evt.TimestampUtc != default;

    private static bool CurrentNdjsonHasCompleteTail(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0) return true;
            stream.Seek(-1, SeekOrigin.End);
            return stream.ReadByte() == (byte)'\n';
        }
        catch { return false; }
    }

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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

public sealed class SystemAuditEvent
{
    public int SchemaVersion { get; set; } = 1;
    public Guid EventId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string DeviceId { get; set; } = "";
    public string Operatore { get; set; } = "";
    public string Azione { get; set; } = "";
    public string Target { get; set; } = "";
    public string Dettagli { get; set; } = "";
}

public sealed class SystemAuditCheckpoint
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime UpdatedUtc { get; set; }
    public List<SystemAuditEvent> Events { get; set; } = new();
}
