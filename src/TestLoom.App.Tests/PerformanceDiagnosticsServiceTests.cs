using TestLoom.App.Services;
using Xunit;

namespace TestLoom.App.Tests;

/// <summary>
/// Copre l'estensione 2026-09-22 di PerformanceDiagnosticsService (richiesta utente: "aggiungere i tempi di
/// tracciamento reali... una diagnostica di processi... molto più esplicativa"): i nuovi campi FileCount/
/// ByteCount/Trigger, il contesto ambientale RefreshTriggerContext che permette a chi scatena un refresh di
/// etichettarlo senza cambiare le firme di AppState, e la rotazione bounded (già presente, verificata qui con
/// il nuovo schema per assicurarsi che non sia stata rotta).
/// </summary>
public sealed class PerformanceDiagnosticsServiceTests
{
    [Fact]
    public void Record_ScriveDurataRealeEConteggiFileEByte()
    {
        var dir = NewTemp("TestLoom-perf-fields");
        try
        {
            PerformanceDiagnosticsService.Record(
                dir,
                "testcases.bootstrap.read-json",
                TimeSpan.FromMilliseconds(1234.5),
                itemCount: 100_000,
                detail: "cold-path",
                fileCount: 100_000,
                byteCount: 512_000_000);

            var eventi = PerformanceDiagnosticsService.ReadRecent(dir, 10);
            var evt = Assert.Single(eventi);
            Assert.Equal("testcases.bootstrap.read-json", evt.Operation);
            Assert.Equal(100_000, evt.ItemCount);
            Assert.Equal(100_000, evt.FileCount);
            Assert.Equal(512_000_000, evt.ByteCount);
            // La durata deve essere quella reale passata dal chiamante (Stopwatch.Elapsed nel codice reale),
            // non un valore fisso o arrotondato a zero: la richiesta utente era esplicitamente "tempi di
            // tracciamento reali".
            Assert.InRange(evt.ElapsedMilliseconds, 1234.0, 1235.0);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Record_SenzaFileCountEByteCount_RestanoZero_NessunaRegressioneSuiChiamantiEsistenti()
    {
        var dir = NewTemp("TestLoom-perf-legacy-call");
        try
        {
            // Firma identica a quella usata oggi da AppState/MainViewModel: deve continuare a compilare e
            // funzionare invariata dopo l'estensione.
            PerformanceDiagnosticsService.Record(dir, "workspace.bootstrap.totale", TimeSpan.FromMilliseconds(700), 4300);

            var evt = Assert.Single(PerformanceDiagnosticsService.ReadRecent(dir, 10));
            Assert.Equal(0, evt.FileCount);
            Assert.Equal(0, evt.ByteCount);
            Assert.Equal("", evt.Trigger);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void RefreshTriggerContext_Push_EtichettaGliEventiRegistratiDentroIlBlocco()
    {
        var dir = NewTemp("TestLoom-perf-trigger");
        try
        {
            using (RefreshTriggerContext.Push("timer"))
            {
                PerformanceDiagnosticsService.Record(dir, "testcases.full-reconciliation", TimeSpan.FromMilliseconds(50), 10);
            }

            // Fuori dal blocco: nessun trigger ambientale, come prima dell'estensione.
            PerformanceDiagnosticsService.Record(dir, "storage.maintenance", TimeSpan.FromMilliseconds(5), 0);

            var eventi = PerformanceDiagnosticsService.ReadRecent(dir, 10);
            Assert.Equal(2, eventi.Count);
            var dentro = eventi.Single(e => e.Operation == "testcases.full-reconciliation");
            var fuori = eventi.Single(e => e.Operation == "storage.maintenance");
            Assert.Equal("timer", dentro.Trigger);
            Assert.Equal("", fuori.Trigger);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void RefreshTriggerContext_BlocchiAnnidati_RipristinanoIlValorePrecedenteAlDispose()
    {
        using (RefreshTriggerContext.Push("timer"))
        {
            Assert.Equal("timer", RefreshTriggerContext.Current);
            using (RefreshTriggerContext.Push("watcher-filesystem"))
            {
                Assert.Equal("watcher-filesystem", RefreshTriggerContext.Current);
            }
            Assert.Equal("timer", RefreshTriggerContext.Current);
        }
        Assert.Equal("", RefreshTriggerContext.Current);
    }

    [Fact]
    public void Record_TriggerEsplicito_HaPrecedenzaSulContestoAmbientale()
    {
        var dir = NewTemp("TestLoom-perf-trigger-override");
        try
        {
            using (RefreshTriggerContext.Push("timer"))
            {
                PerformanceDiagnosticsService.Record(dir, "testcases.incremental-refresh", TimeSpan.FromMilliseconds(1), 1,
                    trigger: "utente");
            }

            var evt = Assert.Single(PerformanceDiagnosticsService.ReadRecent(dir, 10));
            Assert.Equal("utente", evt.Trigger);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    [Trait("Category", "Lento")]
    public void Record_RuotaERestaBoundedConINuoviCampi()
    {
        var dir = NewTemp("TestLoom-perf-rotation");
        try
        {
            // 1 MiB di soglia; con dettagli di ~200 caratteri servono alcune migliaia di righe per superarla
            // più volte e osservare la rotazione a 2 generazioni.
            var dettaglioLungo = new string('x', 200);
            for (var i = 0; i < 8000; i++)
            {
                PerformanceDiagnosticsService.Record(
                    dir, "testcases.incremental-refresh", TimeSpan.FromMilliseconds(i % 50), i % 10,
                    detail: dettaglioLungo, fileCount: i, byteCount: i * 1024L);
            }

            var file0 = Path.Combine(dir, "performance.ndjson");
            var file1 = Path.Combine(dir, "performance.1.ndjson");
            var file2 = Path.Combine(dir, "performance.2.ndjson");
            Assert.True(File.Exists(file0));
            // Deve essere avvenuta almeno una rotazione reale.
            Assert.True(File.Exists(file1) || File.Exists(file2));
            foreach (var f in new[] { file0, file1, file2 }.Where(File.Exists))
                Assert.True(new FileInfo(f).Length <= 1_100_000, $"{f} supera la soglia bounded attesa.");

            Assert.True(PerformanceDiagnosticsService.ReadRecent(dir, 25).Count <= 25);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Record_WorkspaceDirectoryVuota_NonScriveNulla_CostoNulloQuandoNonApplicabile()
    {
        // Copre esplicitamente il requisito "nessuna misura che rallenti il percorso normale": quando il
        // chiamante non ha ancora una cartella di lavoro valida, Record deve limitarsi a un controllo di
        // stringa e tornare, senza creare directory né file.
        PerformanceDiagnosticsService.Record(null, "workspace.bootstrap.totale", TimeSpan.FromSeconds(1), 0);
        PerformanceDiagnosticsService.Record("", "workspace.bootstrap.totale", TimeSpan.FromSeconds(1), 0);
        PerformanceDiagnosticsService.Record("   ", "workspace.bootstrap.totale", TimeSpan.FromSeconds(1), 0);
        // Nessuna eccezione: è l'unica cosa verificabile qui senza un workspace, la garanzia di "return
        // immediato" è nel codice sorgente (string.IsNullOrWhiteSpace prima di qualunque I/O).
    }

    private static string NewTemp(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
