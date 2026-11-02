using System.IO.Compression;
using System.Reflection;

namespace TestLoom.App.Services;

/// <summary>
/// Pacchetto diagnostico da inviare in caso di problemi (feedback utente 2026-09-17: "è presente un log da
/// poter inviare... genera un sistema di logging circolare... non solo per crash proprio per bug in
/// generale"). Include i log applicativi bounded/circolari del progetto: CrashLogService (2 MiB × 3
/// generazioni, solo eccezioni), DiagnosticTraceService (2 MiB × 3 generazioni, operazioni applicative
/// principali anche quando vanno a buon fine — utile per un bug senza eccezione), PerformanceDiagnosticsService
/// (1 MiB × 2 generazioni, durate/fasi/trigger dei cicli di refresh e dell'avvio a freddo — "non contiene
/// payload Test né dati funzionali") e, quando presente, la traccia tecnica di
/// PilotConcurrencyTraceService (2 MiB × 3 generazioni: contesa I/O e propagazione multi-PC, richiesta utente
/// 2026-09-22 "una diagnostica di processi... molto più esplicativa" — solo percorsi relativi/hash/timestamp,
/// mai contenuto). Il pacchetto non contiene mai i JSON autorevoli (Test/NC/master).
/// </summary>
public static class DiagnosticExportService
{
    /// <summary><paramref name="ct"/> viene controllato a ogni file aggiunto allo ZIP (vedi EsportazioneInBackground).</summary>
    public static byte[] GeneraZip(AppState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            ScriviManifesto(archive, state);

            foreach (var path in CrashLogService.FilePresenti().ConAnnullamento(ct))
                AggiungiFile(archive, "crash-log/" + Path.GetFileName(path), path);

            foreach (var path in DiagnosticTraceService.FilePresenti().ConAnnullamento(ct))
                AggiungiFile(archive, "trace-log/" + Path.GetFileName(path), path);

            foreach (var path in UiInteractionTraceService.FilePresenti().ConAnnullamento(ct))
                AggiungiFile(archive, "ui-actions/" + Path.GetFileName(path), path);

            var workspaceDir = state.TestCases.LocalWorkspaceDirectory;
            if (!string.IsNullOrWhiteSpace(workspaceDir) && Directory.Exists(workspaceDir))
                foreach (var path in Directory.EnumerateFiles(workspaceDir, "performance*.ndjson").ConAnnullamento(ct))
                    AggiungiFile(archive, "performance-log/" + Path.GetFileName(path), path);

            // PilotConcurrencyTraceService chiave la propria cartella sulla root della cartella condivisa
            // (state.Paths.Root), non sulla cartella di lavoro locale dell'indice TestCase: le due possono
            // differire, quindi va interrogato separatamente.
            var workspaceRoot = state.Paths?.Root;
            if (!string.IsNullOrWhiteSpace(workspaceRoot))
                foreach (var path in PilotConcurrencyTraceService.FilePresenti(workspaceRoot).ConAnnullamento(ct))
                    AggiungiFile(archive, "process-diagnostics-log/" + Path.GetFileName(path), path);
        }
        return ms.ToArray();
    }

    private static void ScriviManifesto(ZipArchive archive, AppState state)
    {
        var entry = archive.CreateEntry("manifest.txt", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.WriteLine("TestLoom - Pacchetto diagnostico");
        writer.WriteLine($"Generato il: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine($"Versione app: {Assembly.GetExecutingAssembly().GetName().Version}");
        writer.WriteLine($"Sistema operativo: {Environment.OSVersion}");
        writer.WriteLine($"Ruolo operatore: {state.OperatoreCorrente?.Ruolo}");
        writer.WriteLine($"Cartella condivisa valida: {state.CartellaValida}");
        writer.WriteLine($"Stato condivisione: {state.StatoCondivisione.Stato}");
        writer.WriteLine($"Modalità cartella condivisa: {ModalitaCondivisioneService.Effettiva(state.Config.CartellaCondivisa)} " +
            $"(override PC: {ModalitaCondivisioneService.OverrideRadice(state.Config.CartellaCondivisa)})");
        if (state.CartellaValida)
        {
            writer.WriteLine($"Test totali: {state.TestCases.Count(includeEliminati: true)}");
            writer.WriteLine($"Non Conformità totali: {state.NonConformita.CountAll}");
        }
        writer.WriteLine($"Diagnostica di processo (propagazione multi-PC/contesa I/O, TestLoom_PROCESS_DIAGNOSTICS): {(PilotConcurrencyTraceService.IsProcessDiagnosticsEnabled ? "attiva" : "disattivata")}");
        writer.WriteLine($"Traccia hash before/after per ogni scrittura (TestLoom_PILOT_TRACE): {(PilotConcurrencyTraceService.IsEnabled ? "attiva" : "disattivata")}");
        writer.WriteLine();
        writer.WriteLine("Contenuto: audit/tracce e log UI giornalieri (senza testi digitati), crash e telemetria locale.");
        writer.WriteLine("Nessun file JSON autorevole (Test/NC/master) è incluso in questo pacchetto.");
    }

    private static void AggiungiFile(ZipArchive archive, string entryName, string sourcePath)
    {
        try
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var destination = entry.Open();
            source.CopyTo(destination);
        }
        catch (Exception ex) { CrashLogService.Registra($"Pacchetto diagnostico: aggiunta file {sourcePath}", ex); }
    }
}
