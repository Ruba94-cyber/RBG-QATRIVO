using System.Text;
using System.Text.Json;

namespace TestLoom.App.Services;

/// <summary>
/// Telemetria PRESTAZIONALE esclusivamente locale e bounded. Non contiene payload Test né dati funzionali e
/// non viene mai scritta nella cartella condivisa. Serve a rendere misurabili warm-start, reconciliation,
/// rebuild e manutenzione durante i collaudi reali senza introdurre servizi esterni o raccolta cloud.
///
/// Estensione 2026-09-22 (richiesta utente: "aggiungere i tempi di tracciamento reali... una diagnostica di
/// processi... molto più esplicativa"): oltre a durata/conteggio elementi, un evento può ora portare quanti
/// file sono stati letti e quanti byte (avvio a freddo su archivi grandi) e cosa ha scatenato l'operazione
/// (timer periodico, evento filesystem, azione utente) tramite <see cref="RefreshTriggerContext"/>. Nessun
/// campo nuovo è obbligatorio: i chiamanti esistenti (bootstrap, reconciliation, refresh) continuano a
/// funzionare invariati e i valori non passati restano 0/"" senza costo aggiuntivo.
/// </summary>
public static class PerformanceDiagnosticsService
{
    private const long MaxFileBytes = 1024 * 1024;
    private const int MaxRotations = 2;
    private static readonly object Gate = new();

    public static void Record(
        string? workspaceDirectory,
        string operation,
        TimeSpan elapsed,
        int itemCount = 0,
        string? detail = null,
        long fileCount = 0,
        long byteCount = 0,
        string? trigger = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory) || string.IsNullOrWhiteSpace(operation)) return;
        try
        {
            Directory.CreateDirectory(workspaceDirectory);
            var path = Path.Combine(workspaceDirectory, "performance.ndjson");
            lock (Gate)
            {
                RotateIfNeeded(path);
                var evt = new PerformanceDiagnosticEvent
                {
                    TimestampUtc = DateTime.UtcNow,
                    Operation = operation,
                    ElapsedMilliseconds = Math.Max(0, elapsed.TotalMilliseconds),
                    ItemCount = Math.Max(0, itemCount),
                    Detail = detail ?? "",
                    FileCount = Math.Max(0, fileCount),
                    ByteCount = Math.Max(0, byteCount),
                    Trigger = trigger ?? RefreshTriggerContext.Current
                };
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                    16 * 1024, FileOptions.WriteThrough);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(JsonSerializer.Serialize(evt, JsonIo.CompactOptions));
                writer.Flush();
            }
        }
        catch (Exception ex)
        {
            // Diagnostica prestazioni non deve mai modificare il comportamento dell'app.
            CrashLogService.Registra("Performance diagnostics", ex);
        }
    }

    /// <summary>
    /// Variante non bloccante per gli eventi emessi sul dispatcher UI.
    /// Record() usa FileOptions.WriteThrough: fare fsync sul percorso di
    /// navigazione puo' rallentare il primo paint o il cambio pagina, anche
    /// quando il ViewModel e' gia' pronto. L'evento conserva identici campi
    /// e file locale; viene cambiato soltanto il thread di scrittura.
    /// </summary>
    public static void RecordDeferred(
        string? workspaceDirectory, string operation, TimeSpan elapsed,
        int itemCount = 0, string? detail = null, long fileCount = 0,
        long byteCount = 0, string? trigger = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory) || string.IsNullOrWhiteSpace(operation)) return;
        // Capture del trigger prima di lasciare il contesto del chiamante.
        var capturedTrigger = trigger ?? RefreshTriggerContext.Current;
        _ = Task.Run(() => Record(workspaceDirectory, operation, elapsed, itemCount,
            detail, fileCount, byteCount, capturedTrigger));
    }

    public static IReadOnlyList<PerformanceDiagnosticEvent> ReadRecent(string? workspaceDirectory, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory) || max <= 0) return Array.Empty<PerformanceDiagnosticEvent>();
        var result = new List<PerformanceDiagnosticEvent>();
        try
        {
            var files = Enumerable.Range(0, MaxRotations + 1)
                .Select(i => i == 0
                    ? Path.Combine(workspaceDirectory, "performance.ndjson")
                    : Path.Combine(workspaceDirectory, $"performance.{i}.ndjson"))
                .Where(File.Exists)
                .ToList();

            foreach (var file in files)
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var evt = JsonSerializer.Deserialize<PerformanceDiagnosticEvent>(line, JsonIo.Options);
                        if (evt != null) result.Add(evt);
                    }
                    catch (JsonException) { }
                }
            }
        }
        catch { }

        return result.OrderByDescending(x => x.TimestampUtc).Take(max).ToList();
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < MaxFileBytes) return;
            var oldest = Path.Combine(Path.GetDirectoryName(path)!, $"performance.{MaxRotations}.ndjson");
            if (File.Exists(oldest)) File.Delete(oldest);
            for (var i = MaxRotations - 1; i >= 1; i--)
            {
                var from = Path.Combine(Path.GetDirectoryName(path)!, $"performance.{i}.ndjson");
                var to = Path.Combine(Path.GetDirectoryName(path)!, $"performance.{i + 1}.ndjson");
                if (File.Exists(from)) File.Move(from, to, overwrite: true);
            }
            File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, "performance.1.ndjson"), overwrite: true);
        }
        catch { }
    }
}

public sealed class PerformanceDiagnosticEvent
{
    public DateTime TimestampUtc { get; set; }
    public string Operation { get; set; } = "";
    public double ElapsedMilliseconds { get; set; }
    public int ItemCount { get; set; }
    public string Detail { get; set; } = "";

    /// <summary>Numero di file effettivamente letti in questa fase (0 se non applicabile/non misurato dal
    /// chiamante). Serve a spiegare un avvio a freddo lungo senza dover riprodurre il caso.</summary>
    public long FileCount { get; set; }

    /// <summary>Byte totali letti in questa fase (0 se non applicabile/non misurato dal chiamante).</summary>
    public long ByteCount { get; set; }

    /// <summary>Cosa ha scatenato l'operazione: "timer", "watcher-filesystem", "utente" (pulsante "Aggiorna
    /// ora"), "login"/"apertura-workspace", ecc. Stringa libera, "" quando il chiamante non lo specifica e
    /// nessun <see cref="RefreshTriggerContext"/> è attivo sul thread corrente.</summary>
    public string Trigger { get; set; } = "";
}

/// <summary>
/// Contesto ambientale (thread-local) che identifica cosa ha scatenato il ciclo di refresh corrente, senza
/// dover cambiare le firme pubbliche di AppState/MainViewModel lungo tutta la catena di chiamata. Il punto che
/// avvia il refresh (timer periodico, watcher, pulsante "Aggiorna ora") avvolge la chiamata in un blocco
/// <c>using (RefreshTriggerContext.Push("timer")) { ... }</c>; <see cref="PerformanceDiagnosticsService.Record"/>
/// legge automaticamente il valore corrente sullo stesso thread sincrono, quindi tutte le Record già esistenti
/// dentro AppState.RileggiDaDisco/RileggiTestCases ereditano il trigger senza bisogno di nuovi parametri.
/// Non ha alcun costo se nessuno lo usa: legge un campo [ThreadStatic], nessuna allocazione né I/O.
/// </summary>
public static class RefreshTriggerContext
{
    [ThreadStatic] private static string? _current;

    /// <summary>Imposta il trigger corrente per la durata del blocco; ripristina il valore precedente al
    /// Dispose, così chiamate annidate (es. refresh dentro un refresh) restano corrette.</summary>
    public static IDisposable Push(string trigger)
    {
        var previous = _current;
        _current = string.IsNullOrWhiteSpace(trigger) ? "" : trigger.Trim();
        return new Popper(previous);
    }

    /// <summary>Trigger attivo sul thread corrente, "" se nessuno è stato impostato.</summary>
    public static string Current => _current ?? "";

    private sealed class Popper(string? previous) : IDisposable
    {
        public void Dispose() => _current = previous;
    }
}
