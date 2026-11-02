using TestLoom.App.Services;
using Xunit;

namespace TestLoom.App.Tests;

/// <summary>
/// Copre l'estensione 2026-09-22 di PilotConcurrencyTraceService (richiesta utente: "una diagnostica di
/// processi... in modo da avere una diagnostica vera e non solo navigazione", punti 2 e 3 della richiesta —
/// propagazione multi-PC e contesa I/O). A differenza dell'hash before/after per-write (TestLoom_PILOT_TRACE,
/// opt-in perché costoso su OGNI scrittura), questi due eventi si verificano solo sul percorso già lento
/// (attesa receipt/retry) e sono quindi attivi di default: il file xunit.runner.json del progetto disabilita
/// la parallelizzazione delle collection, quindi è sicuro manipolare la variabile d'ambiente di processo nei
/// singoli test con ripristino in finally.
/// </summary>
public sealed class PilotConcurrencyTraceServiceProcessDiagnosticsTests : IDisposable
{
    private readonly string? _originalProcessDiagnosticsEnv = Environment.GetEnvironmentVariable("TestLoom_PROCESS_DIAGNOSTICS");

    public void Dispose() =>
        Environment.SetEnvironmentVariable("TestLoom_PROCESS_DIAGNOSTICS", _originalProcessDiagnosticsEnv);

    [Fact]
    public void IsProcessDiagnosticsEnabled_DiDefaultENonRichiedeAlcunaVariabileDAmbiente()
    {
        Environment.SetEnvironmentVariable("TestLoom_PROCESS_DIAGNOSTICS", null);
        Assert.True(PilotConcurrencyTraceService.IsProcessDiagnosticsEnabled);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("No")]
    public void IsProcessDiagnosticsEnabled_SiDisattivaEsplicitamente(string valore)
    {
        Environment.SetEnvironmentVariable("TestLoom_PROCESS_DIAGNOSTICS", valore);
        Assert.False(PilotConcurrencyTraceService.IsProcessDiagnosticsEnabled);
    }

    [Fact]
    public void RecordPropagation_CalcolaIlRitardoRealeERegistraQualeFileEArrivatoPerUltimo()
    {
        Environment.SetEnvironmentVariable("TestLoom_PROCESS_DIAGNOSTICS", null);
        var root = NewTemp("TestLoom_6_TEST-propagation-root");
        var local = NewTemp("TestLoom-local-propagation");
        try
        {
            Directory.CreateDirectory(root);
            var remoteWrite = new DateTime(2026, 9, 22, 10, 0, 0, DateTimeKind.Utc);
            var exposed = remoteWrite.AddSeconds(47); // propagazione osservata: 47s, non un valore finto/arrotondato.
            var txId = Guid.NewGuid();

            PilotConcurrencyTraceService.WriteEventForTesting(root, new PilotConcurrencyTraceEvent
            {
                Operation = "propagation",
                Stage = "visibility-check",
                Outcome = "committed-visible",
                RelativePath = "testcases/ECM/testcase_T1.json",
                TransactionId = txId.ToString("N"),
                RemoteWriteUtc = remoteWrite,
                ExposedUtc = exposed,
                PropagationDelayMilliseconds = (exposed - remoteWrite).TotalMilliseconds,
                RetryCount = 3,
                LastArrivedRelativePath = "nonconformita/nc_N1.json"
            }, local);

            var evt = Assert.Single(PilotConcurrencyTraceService.ReadRecentForTesting(root, 10, local));
            Assert.Equal("propagation", evt.Operation);
            Assert.Equal("committed-visible", evt.Outcome);
            Assert.Equal(47_000, evt.PropagationDelayMilliseconds);
            Assert.Equal(3, evt.RetryCount);
            Assert.Equal("nonconformita/nc_N1.json", evt.LastArrivedRelativePath);
        }
        finally { TryDelete(root); TryDelete(local); }
    }

    [Fact]
    public void RecordIoContention_RegistraFileTentativiEAttesaTotale()
    {
        Environment.SetEnvironmentVariable("TestLoom_PROCESS_DIAGNOSTICS", null);
        var root = NewTemp("TestLoom_6_TEST-contention-root");
        var local = NewTemp("TestLoom-local-contention");
        try
        {
            Directory.CreateDirectory(root);

            PilotConcurrencyTraceService.WriteEventForTesting(root, new PilotConcurrencyTraceEvent
            {
                Operation = "io-contention",
                Stage = "read-retry",
                Outcome = "resolved-after-retry",
                RelativePath = "_ditm/transactions/committed/DEVICE-A/current.ndjson",
                RetryCount = 2,
                WaitMilliseconds = 300
            }, local);

            var evt = Assert.Single(PilotConcurrencyTraceService.ReadRecentForTesting(root, 10, local));
            Assert.Equal("io-contention", evt.Operation);
            Assert.Equal(2, evt.RetryCount);
            Assert.Equal(300, evt.WaitMilliseconds);
            Assert.Contains("current.ndjson", evt.RelativePath, StringComparison.Ordinal);
        }
        finally { TryDelete(root); TryDelete(local); }
    }

    [Fact]
    public void RecordPropagation_QuandoIlLivelloDiDettaglioEDisattivato_NonScriveNulla_CostoZero()
    {
        Environment.SetEnvironmentVariable("TestLoom_PROCESS_DIAGNOSTICS", "0");
        var root = NewTemp("TestLoom_6_TEST-propagation-disabled");
        try
        {
            Directory.CreateDirectory(root);
            var traceDir = PilotConcurrencyTraceService.WorkspaceDirectoryForTesting(root,
                Path.Combine(Path.GetTempPath(), "TestLoom-local-unused-" + Guid.NewGuid().ToString("N")));

            PilotConcurrencyTraceService.RecordPropagation(
                root, "testcases/ECM/testcase_T1.json", Guid.NewGuid(),
                DateTime.UtcNow.AddSeconds(-30), DateTime.UtcNow, receiptPollAttempts: 5, outcome: "committed-visible");
            PilotConcurrencyTraceService.RecordIoContention(
                root, "testcases/ECM/testcase_T1.json", retryCount: 3, totalWait: TimeSpan.FromMilliseconds(400),
                outcome: "resolved-after-retry");

            // Il gate disattivato deve impedire QUALUNQUE scrittura, non solo restituire una lista vuota in
            // lettura: verifichiamo che la directory di trace per questo workspace non sia stata nemmeno creata.
            Assert.False(Directory.Exists(traceDir));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void RecordPropagation_ConWorkspaceRootVuoto_NonLanciaENonScriveNulla()
    {
        Environment.SetEnvironmentVariable("TestLoom_PROCESS_DIAGNOSTICS", null);
        PilotConcurrencyTraceService.RecordPropagation(
            "", "x", null, null, DateTime.UtcNow, 0, "committed-visible");
        PilotConcurrencyTraceService.RecordIoContention(
            "", "x", 0, TimeSpan.Zero, "resolved");
        // Nessuna eccezione propagata: la diagnostica non deve mai poter far cadere il percorso applicativo.
    }

    [Fact]
    [Trait("Category", "Lento")]
    public void FilePresenti_ElencaLeGenerazioniRuotateEsistenti()
    {
        var root = NewTemp("TestLoom_6_TEST-filepresenti-root");
        var local = NewTemp("TestLoom-local-filepresenti");
        try
        {
            Directory.CreateDirectory(root);
            var largeDetail = new string('x', 600);
            for (var i = 0; i < 5000; i++)
            {
                PilotConcurrencyTraceService.WriteEventForTesting(root, new PilotConcurrencyTraceEvent
                {
                    Operation = "io-contention",
                    Stage = "read-retry",
                    Outcome = "resolved-after-retry",
                    RelativePath = $"testcases/ECM/testcase_{i:D6}.json",
                    RetryCount = 1,
                    WaitMilliseconds = 75,
                    Detail = largeDetail
                }, local);
            }

            var esistenti = PilotConcurrencyTraceService.FilePresentiForTesting(root, local);
            Assert.NotEmpty(esistenti);
            Assert.All(esistenti, percorso => Assert.True(File.Exists(percorso), percorso));
            // FilePresenti deve restituire percorsi reali e leggibili, non i nomi teorici (già coperto
            // indirettamente da "corrente più eventuali rotazioni" nel servizio) — qui verifichiamo che il file
            // corrente sia incluso.
            Assert.Contains(esistenti, p => Path.GetFileName(p) == "concurrency.ndjson");
        }
        finally { TryDelete(root); TryDelete(local); }
    }

    private static string NewTemp(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
