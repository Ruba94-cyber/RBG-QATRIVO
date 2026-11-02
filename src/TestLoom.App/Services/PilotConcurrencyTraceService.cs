using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TestLoom.App.Services;

/// <summary>
/// Traccia tecnica LOCALE e bounded per diagnosticare race/concorrenza durante i pilot OneDrive.
/// Non contiene payload applicativi e non scrive mai nella root condivisa: per ogni workspace usa
/// %LOCALAPPDATA%/TestLoom/pilot-trace/&lt;sha-root&gt;. Il tracing con hash before/after di OGNI write è opt-in
/// tramite TestLoom_PILOT_TRACE=1/true (<see cref="IsEnabled"/>), così benchmark e uso ordinario non pagano
/// letture/hash aggiuntivi su ogni scrittura.
///
/// Estensione 2026-09-22 (richiesta utente: "una diagnostica di processi... in modo da avere una diagnostica
/// vera e non solo navigazione"): <see cref="RecordPropagation"/> e <see cref="RecordIoContention"/> registrano,
/// nello stesso file/rotazione, i due eventi che oggi non si possono spiegare senza riprodurre il caso — quanto
/// tempo passa fra la scrittura remota e l'esposizione locale di un record, e quante volte/quanto a lungo una
/// lettura ha dovuto attendere un file occupato dal client di sincronizzazione. A differenza dell'hash
/// before/after (costoso, quindi opt-in), questi due eventi si verificano SOLO sul percorso già lento
/// (attesa receipt / retry per contesa I/O): registrarli non aggiunge alcun costo al percorso normale, quindi
/// sono attivi di default e disattivabili con <c>TestLoom_PROCESS_DIAGNOSTICS=0</c> (<see cref="IsProcessDiagnosticsEnabled"/>)
/// per chi preferisse comunque escluderli.
/// </summary>
public static class PilotConcurrencyTraceService
{
    private const int CurrentSchemaVersion = 1;
    private const long MaxFileBytes = 2L * 1024 * 1024;
    private const int MaxRotations = 3;
    private const int MaxDetailLength = 512;
    private static readonly object Gate = new();

    public static bool IsEnabled => IsTruthy(Environment.GetEnvironmentVariable("TestLoom_PILOT_TRACE"));

    /// <summary>
    /// Livello di dettaglio per gli eventi "a costo zero sul percorso normale" (propagazione multi-PC, contesa
    /// I/O): attivo di default perché si verificano solo quando qualcosa è già lento/in attesa, non su ogni
    /// lettura. A differenza di <see cref="IsEnabled"/> (opt-in, perché comporta hash su ogni write), qui il
    /// default è "acceso" e l'utente/supporto può spegnerlo con TestLoom_PROCESS_DIAGNOSTICS=0/false/no.
    /// </summary>
    public static bool IsProcessDiagnosticsEnabled => !IsFalsy(Environment.GetEnvironmentVariable("TestLoom_PROCESS_DIAGNOSTICS"));

    /// <summary>
    /// Registra l'esito di una write JSON senza propagare mai errori diagnostici al percorso applicativo.
    /// Gli hash before/after vengono calcolati solo quando il pilot trace è attivo.
    /// </summary>
    public static void RecordAtomicWrite(
        string path,
        bool destinationExistedAtEntry,
        bool overwriteRequested,
        ReadOnlySpan<byte> candidateBytes,
        string outcome,
        string? observedBeforeSha256 = null,
        string? exceptionType = null)
    {
        if (!IsEnabled) return;
        try
        {
            var workspaceRoot = TryLocateWorkspaceRoot(path);
            if (workspaceRoot == null) return;

            var candidateSha = Hash(candidateBytes);
            var observedAfterSha = TryHashFile(path);
            var evt = new PilotConcurrencyTraceEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Operation = "json-write",
                Stage = "atomic-replace",
                Outcome = Normalize(outcome),
                RelativePath = NormalizeRelativePath(workspaceRoot, path),
                TransactionId = WorkflowTransactionService.ActiveId?.ToString("N") ?? "",
                DeviceId = SafeDeviceId(),
                Machine = Environment.MachineName,
                ProcessId = Environment.ProcessId,
                ThreadId = Environment.CurrentManagedThreadId,
                ObservedBeforeSha256 = NormalizeHash(observedBeforeSha256),
                CandidateAfterSha256 = candidateSha,
                ObservedAfterSha256 = observedAfterSha ?? "",
                DestinationExistedAtEntry = destinationExistedAtEntry,
                OverwriteRequested = overwriteRequested,
                Detail = Truncate(string.IsNullOrWhiteSpace(exceptionType)
                    ? ""
                    : "exception=" + exceptionType)
            };
            WriteEvent(workspaceRoot, evt, localBaseDirectory: null);
        }
        catch (Exception ex)
        {
            try { CrashLogService.Registra("Pilot concurrency trace", ex); }
            catch { }
        }
    }

    /// <summary>
    /// Registra una fotografia before della write. Va chiamata solo con tracing attivo perché l'hash del file
    /// esistente è intenzionalmente costoso e non deve influire sui benchmark ordinari.
    /// </summary>
    public static string? CaptureBeforeHash(string path)
    {
        if (!IsEnabled) return null;
        return TryHashFile(path);
    }

    /// <summary>
    /// Evento tecnico generico per transaction/lock/merge. Il chiamante passa la root autorevole così non serve
    /// dedurla da path tecnici locali. Nessun campo deve contenere payload Test/NC/CR/master.
    /// </summary>
    public static void Record(
        string workspaceRoot,
        string operation,
        string stage,
        string outcome,
        string? path = null,
        Guid? transactionId = null,
        string? observedBeforeSha256 = null,
        string? expectedSha256 = null,
        string? candidateAfterSha256 = null,
        string? observedAfterSha256 = null,
        string? revision = null,
        string? lockOwner = null,
        string? detail = null)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(workspaceRoot)) return;
        try
        {
            var evt = new PilotConcurrencyTraceEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Operation = Normalize(operation),
                Stage = Normalize(stage),
                Outcome = Normalize(outcome),
                RelativePath = string.IsNullOrWhiteSpace(path) ? "" : NormalizeRelativePath(workspaceRoot, path),
                TransactionId = transactionId?.ToString("N") ?? WorkflowTransactionService.ActiveId?.ToString("N") ?? "",
                DeviceId = SafeDeviceId(),
                Machine = Environment.MachineName,
                ProcessId = Environment.ProcessId,
                ThreadId = Environment.CurrentManagedThreadId,
                ObservedBeforeSha256 = NormalizeHash(observedBeforeSha256),
                ExpectedSha256 = NormalizeHash(expectedSha256),
                CandidateAfterSha256 = NormalizeHash(candidateAfterSha256),
                ObservedAfterSha256 = NormalizeHash(observedAfterSha256),
                Revision = Truncate(revision),
                LockOwner = Truncate(lockOwner),
                Detail = Truncate(detail)
            };
            WriteEvent(workspaceRoot, evt, localBaseDirectory: null);
        }
        catch (Exception ex)
        {
            try { CrashLogService.Registra("Pilot concurrency trace", ex); }
            catch { }
        }
    }

    public static IReadOnlyList<PilotConcurrencyTraceEvent> ReadRecent(string workspaceRoot, int max = 500)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || max <= 0) return Array.Empty<PilotConcurrencyTraceEvent>();
        return ReadRecentCore(workspaceRoot, max, localBaseDirectory: null);
    }

    /// <summary>
    /// Registra la propagazione multi-PC di UN record: da quando la scrittura remota è arrivata su disco
    /// (mtime del file, proxy della scrittura sull'altro PC) a quando TestLoom l'ha davvero esposta (o quanto è
    /// ancora in attesa). Dalla 6.4.2 non esiste più un'attesa della receipt in lettura (PIANO_6.4.2 §1): il
    /// punto di aggancio naturale è la rilettura del record nel repository.
    /// </summary>
    public static void RecordPropagation(
        string workspaceRoot,
        string relativePath,
        Guid? transactionId,
        DateTime? remoteWriteUtc,
        DateTime exposedUtc,
        int receiptPollAttempts,
        string outcome,
        string? lastArrivedRelativePath = null,
        string? detail = null)
    {
        if (!IsProcessDiagnosticsEnabled || string.IsNullOrWhiteSpace(workspaceRoot)) return;
        try
        {
            var evt = new PilotConcurrencyTraceEvent
            {
                Operation = "propagation",
                Stage = "visibility-check",
                Outcome = Normalize(outcome),
                RelativePath = Truncate(relativePath, 1024),
                TransactionId = transactionId?.ToString("N") ?? "",
                RemoteWriteUtc = remoteWriteUtc,
                ExposedUtc = exposedUtc,
                PropagationDelayMilliseconds = remoteWriteUtc.HasValue
                    ? Math.Max(0, (exposedUtc - remoteWriteUtc.Value).TotalMilliseconds)
                    : null,
                RetryCount = Math.Max(0, receiptPollAttempts),
                LastArrivedRelativePath = string.IsNullOrWhiteSpace(lastArrivedRelativePath)
                    ? "" : Truncate(lastArrivedRelativePath, 1024),
                DeviceId = SafeDeviceId(),
                Machine = Environment.MachineName,
                ProcessId = Environment.ProcessId,
                ThreadId = Environment.CurrentManagedThreadId,
                Detail = Truncate(detail)
            };
            WriteEvent(workspaceRoot, evt, localBaseDirectory: null);
        }
        catch (Exception ex)
        {
            try { CrashLogService.Registra("Process diagnostics: propagation", ex); }
            catch { }
        }
    }

    /// <summary>
    /// Registra una contesa I/O: una lettura che ha dovuto ritentare (o ha fallito) perché il file era in uso
    /// (tipicamente il client di sincronizzazione). Punti di aggancio previsti: il retry loop di
    /// <c>JsonIo.TryRead&lt;T&gt;</c> (ogni JSON di dominio), quello di
    /// <c>SharedWorkflowTransactionGuard.ReadMarker</c> (marker di transazione) e la lettura del ledger commit
    /// in <c>WorkflowCommitLedgerService.ReadNdjsonCached</c> — righe esatte nel referto, tutti file fuori
    /// perimetro di questa modifica.
    /// </summary>
    public static void RecordIoContention(
        string workspaceRoot,
        string relativePath,
        int retryCount,
        TimeSpan totalWait,
        string outcome,
        string? detail = null)
    {
        if (!IsProcessDiagnosticsEnabled || string.IsNullOrWhiteSpace(workspaceRoot)) return;
        try
        {
            var evt = new PilotConcurrencyTraceEvent
            {
                Operation = "io-contention",
                Stage = "read-retry",
                Outcome = Normalize(outcome),
                RelativePath = Truncate(relativePath, 1024),
                RetryCount = Math.Max(0, retryCount),
                WaitMilliseconds = Math.Max(0, totalWait.TotalMilliseconds),
                DeviceId = SafeDeviceId(),
                Machine = Environment.MachineName,
                ProcessId = Environment.ProcessId,
                ThreadId = Environment.CurrentManagedThreadId,
                Detail = Truncate(detail)
            };
            WriteEvent(workspaceRoot, evt, localBaseDirectory: null);
        }
        catch (Exception ex)
        {
            try { CrashLogService.Registra("Process diagnostics: io-contention", ex); }
            catch { }
        }
    }

    /// <summary>Percorsi dei file concurrency*.ndjson effettivamente presenti per questo workspace, più recente
    /// prima — usato da DiagnosticExportService per allegarli al pacchetto diagnostico. A differenza di
    /// ReadRecent copia i file grezzi invece di deserializzarli, più economico per l'export.</summary>
    public static IReadOnlyList<string> FilePresenti(string workspaceRoot) =>
        FilePresentiCore(workspaceRoot, localBaseDirectory: null);

    internal static IReadOnlyList<string> FilePresentiForTesting(string workspaceRoot, string localBaseDirectory) =>
        FilePresentiCore(workspaceRoot, localBaseDirectory);

    private static IReadOnlyList<string> FilePresentiCore(string workspaceRoot, string? localBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return Array.Empty<string>();
        try
        {
            var directory = WorkspaceDirectory(workspaceRoot, localBaseDirectory);
            var risultato = new List<string>();
            var current = Path.Combine(directory, "concurrency.ndjson");
            if (File.Exists(current)) risultato.Add(current);
            for (var i = 1; i <= MaxRotations; i++)
            {
                var path = Path.Combine(directory, $"concurrency.{i}.ndjson");
                if (File.Exists(path)) risultato.Add(path);
            }
            return risultato;
        }
        catch { return Array.Empty<string>(); }
    }

    internal static string? TryHashFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch { return null; }
    }

    internal static void WriteEventForTesting(
        string workspaceRoot,
        PilotConcurrencyTraceEvent evt,
        string localBaseDirectory) =>
        WriteEvent(workspaceRoot, evt, localBaseDirectory);

    internal static IReadOnlyList<PilotConcurrencyTraceEvent> ReadRecentForTesting(
        string workspaceRoot,
        int max,
        string localBaseDirectory) =>
        ReadRecentCore(workspaceRoot, max, localBaseDirectory);

    internal static string WorkspaceDirectoryForTesting(string workspaceRoot, string localBaseDirectory) =>
        WorkspaceDirectory(workspaceRoot, localBaseDirectory);

    private static void WriteEvent(string workspaceRoot, PilotConcurrencyTraceEvent evt, string? localBaseDirectory)
    {
        evt.SchemaVersion = CurrentSchemaVersion;
        evt.TimestampUtc = evt.TimestampUtc == default ? DateTime.UtcNow : evt.TimestampUtc;
        evt.Operation = Normalize(evt.Operation);
        evt.Stage = Normalize(evt.Stage);
        evt.Outcome = Normalize(evt.Outcome);
        evt.RelativePath = Truncate(evt.RelativePath, 1024);
        evt.Detail = Truncate(evt.Detail);
        evt.Revision = Truncate(evt.Revision);
        evt.LockOwner = Truncate(evt.LockOwner);

        var directory = WorkspaceDirectory(workspaceRoot, localBaseDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "concurrency.ndjson");

        lock (Gate)
        {
            RotateIfNeeded(path);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                16 * 1024, FileOptions.WriteThrough | FileOptions.SequentialScan);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(evt, JsonIo.CompactOptions));
            writer.Flush();
        }
    }

    private static IReadOnlyList<PilotConcurrencyTraceEvent> ReadRecentCore(
        string workspaceRoot,
        int max,
        string? localBaseDirectory)
    {
        var result = new List<PilotConcurrencyTraceEvent>();
        try
        {
            var directory = WorkspaceDirectory(workspaceRoot, localBaseDirectory);
            var files = Enumerable.Range(0, MaxRotations + 1)
                .Select(i => i == 0
                    ? Path.Combine(directory, "concurrency.ndjson")
                    : Path.Combine(directory, $"concurrency.{i}.ndjson"))
                .Where(File.Exists)
                .ToList();

            foreach (var file in files)
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var evt = JsonSerializer.Deserialize<PilotConcurrencyTraceEvent>(line, JsonIo.Options);
                        if (evt?.SchemaVersion == CurrentSchemaVersion) result.Add(evt);
                    }
                    catch (JsonException) { }
                }
            }
        }
        catch { }

        return result.OrderByDescending(x => x.TimestampUtc).Take(max).ToList();
    }

    internal static string WorkspaceDirectory(string workspaceRoot, string? localBaseDirectory)
    {
        // Traccia diagnostica: stessa chiave di LocalTestIndexStore. Non migra la traccia della vecchia chiave (non e' dato autorevole).
        var key = WorkspaceLocalPaths.ComputeWorkspaceKey(workspaceRoot);
        var baseDirectory = localBaseDirectory ?? Path.Combine(
            WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", "pilot-trace");
        return Path.Combine(baseDirectory, key);
    }

    private static string? TryLocateWorkspaceRoot(string path)
    {
        try
        {
            var configured = Environment.GetEnvironmentVariable("TestLoom_PILOT_TRACE_WORKSPACE_ROOT");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var root = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var full = Path.GetFullPath(path);
                var prefix = root + Path.DirectorySeparatorChar;
                if (full.Equals(root, PathComparison) || full.StartsWith(prefix, PathComparison)) return root;
            }

            var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
            for (var depth = 0; directory != null && depth < 32; depth++, directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "version-manifest.json")) ||
                    Directory.Exists(Path.Combine(directory.FullName, "_ditm")))
                    return directory.FullName;
            }
        }
        catch { }
        return null;
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaxFileBytes) return;
        var directory = Path.GetDirectoryName(path)!;
        var oldest = Path.Combine(directory, $"concurrency.{MaxRotations}.ndjson");
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = MaxRotations - 1; i >= 1; i--)
        {
            var from = Path.Combine(directory, $"concurrency.{i}.ndjson");
            var to = Path.Combine(directory, $"concurrency.{i + 1}.ndjson");
            if (File.Exists(from)) File.Move(from, to, overwrite: true);
        }
        File.Move(path, Path.Combine(directory, "concurrency.1.ndjson"), overwrite: true);
    }

    private static string NormalizeRelativePath(string workspaceRoot, string path)
    {
        try
        {
            var root = Path.GetFullPath(workspaceRoot);
            var full = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Equals("..", StringComparison.Ordinal) || relative.StartsWith("../", StringComparison.Ordinal))
                return "<outside-workspace>/" + Path.GetFileName(full);
            return relative;
        }
        catch { return Path.GetFileName(path); }
    }


    private static string SafeDeviceId()
    {
        try { return DeviceIdentityService.GetOrCreate(); }
        catch { return "unavailable"; }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string NormalizeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Trim().ToUpperInvariant();
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

    private static string Truncate(string? value, int max = MaxDetailLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= max ? clean : clean[..max];
    }

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>Il contrario di IsTruthy, ma con default "acceso": usato per flag on-by-default come
    /// TestLoom_PROCESS_DIAGNOSTICS, dove l'assenza della variabile non deve disattivare la diagnostica.</summary>
    private static bool IsFalsy(string? value) =>
        string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

public sealed class PilotConcurrencyTraceEvent
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime TimestampUtc { get; set; }
    public string Operation { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Machine { get; set; } = "";
    public int ProcessId { get; set; }
    public int ThreadId { get; set; }
    public string ObservedBeforeSha256 { get; set; } = "";
    public string ExpectedSha256 { get; set; } = "";
    public string CandidateAfterSha256 { get; set; } = "";
    public string ObservedAfterSha256 { get; set; } = "";
    public string Revision { get; set; } = "";
    public string LockOwner { get; set; } = "";
    public bool DestinationExistedAtEntry { get; set; }
    public bool OverwriteRequested { get; set; }
    public string Detail { get; set; } = "";

    // --- Campi 2026-09-22: propagazione multi-PC (RecordPropagation) e contesa I/O (RecordIoContention). ---
    // Restano vuoti/0/null per tutti gli eventi "json-write"/generici esistenti: non ne cambiano il formato.

    /// <summary>Numero di tentativi/poll effettuati (retry di lettura per contesa I/O, oppure giri di refresh
    /// in attesa della receipt di commit per la propagazione).</summary>
    public int RetryCount { get; set; }

    /// <summary>Attesa totale (somma dei backoff) spesa nei retry di una singola lettura contesa.</summary>
    public double? WaitMilliseconds { get; set; }

    /// <summary>Istante (UTC) della scrittura remota che ha originato il record, tipicamente il mtime del file
    /// arrivato via sync — proxy di "quando l'altro PC ha scritto", non un timestamp applicativo.</summary>
    public DateTime? RemoteWriteUtc { get; set; }

    /// <summary>Istante (UTC) in cui TestLoom ha effettivamente esposto/potuto esporre il record su questo PC.</summary>
    public DateTime? ExposedUtc { get; set; }

    /// <summary>ExposedUtc - RemoteWriteUtc in millisecondi: il ritardo di propagazione percepito per questo
    /// singolo file/record.</summary>
    public double? PropagationDelayMilliseconds { get; set; }

    /// <summary>Percorso relativo dell'ultimo file della stessa transazione arrivato, quando la receipt aveva
    /// più entry (es. Test + Non Conformità nella stessa Unit of Work) — risponde a "quale file è arrivato per
    /// ultimo".</summary>
    public string LastArrivedRelativePath { get; set; } = "";
}
