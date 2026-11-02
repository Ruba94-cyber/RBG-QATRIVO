using Avalonia;
using System;
using TestLoom.App.Services;

namespace TestLoom.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        CrashLogService.Installa();

        // Esecuzione esplicita senza AppState/startup: dry-run non crea directory o migra i dati.
        if (args.Length == 4 && args[0] == "--migrate-test-uids" && args[1] is "dry-run" or "apply")
        {
            var paths = new DataPaths(Path.GetFullPath(args[2]));
            var report = Path.GetFullPath(args[3]);
            var relativeReport = Path.GetRelativePath(paths.Root, report);
            if (!Path.IsPathRooted(relativeReport) && relativeReport != ".." &&
                !relativeReport.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new InvalidDataException("Il report deve essere esterno alla cartella dati analizzata.");
            if (args[1] == "dry-run")
            {
                var plan = TestUidMigrationService.Analyze(paths);
                File.WriteAllBytes(report, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(plan, JsonIo.Options));
                Console.WriteLine($"NC={plan.NonConformitaCount}; segnalazioni={plan.SegnalazioniCount}; record da aggiornare={plan.Changes.Count}");
            }
            else
            {
                var plan = JsonIo.TryRead<TestUidMigrationPlan>(report, out var error) ?? throw new InvalidDataException(error);
                var backup = TestUidMigrationService.Apply(paths, plan, Path.Combine(Path.GetDirectoryName(report)!, "migration-local-index"));
                Console.WriteLine(backup == null ? "Nessuna modifica necessaria." : $"Migrazione completata. Backup: {backup}");
            }
            return;
        }

        // Modalità nascosta per generare il dataset fittizio di verifica (§10), usata solo in sviluppo/test.
        if (args.Length == 2 && args[0] == "--seed-demo")
        {
            SeedDemoDataService.Genera(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--seed-demo-clean")
        {
            SeedDemoDataService.GeneraDemoPulita(args[1]);
            return;
        }

        // Istanza singola per PC (§6.7): se un'altra istanza è già in esecuzione, le si chiede di
        // portarsi in primo piano e questo processo esce subito senza aprire una seconda finestra.
        if (!SingleInstanceService.TryBecomePrimaryInstance())
            return;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            ChiusuraDiagnostica.Registra("Main: lifetime terminata");
        }
        catch (Exception ex)
        {
            // Avvio grafico impossibile (piattaforma o runtime grafico): l'utente deve sapere dove sono i dettagli,
            // non vedere solo il programma chiudersi.
            CrashLogService.Registra("Program.Main (avvio grafico)", ex);
            MessaggioNativo.MostraErroreAvvio();
            Environment.ExitCode = 1;
        }

    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
