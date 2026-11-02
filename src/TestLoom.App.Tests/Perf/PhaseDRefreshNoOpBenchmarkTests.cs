using System.Diagnostics;
using System.Globalization;
using System.Text;
using TestLoom.App.Services;
using Xunit.Abstractions;

namespace TestLoom.App.Tests.Perf;

/// <summary>
/// Costo di un <see cref="PhaseDMasterMaterializer.Refresh"/> NO-OP (nessuna scrittura fra un giro e l'altro)
/// sul banco a piena scala (Piano 6.4.0, ottimizzazione 2026-09-25). Prima dell'ottimizzazione ogni Refresh
/// costava ~90-190ms anche a nulla cambiato, dominato da DeleteUnreferencedMutations (un NOT IN su ~100k+ righe
/// di phase_d_mutation) rieseguito ad ogni giro anche quando in quel Refresh non era mai stata rimossa una sola
/// mappatura — quindi garantito a vuoto. Vedi PhaseDMasterMaterializer.Refresh per la scomposizione per fase.
///
/// SKIPPATO di default: attivo solo con TestLoom_PERF_REFRESH_PROFILE_DATASET puntata su un banco di prova (es.
/// quello di BancoPerf100kGeneratoreTests). La cartella indicata va gia' passata come COPIA usa-e-getta: il
/// read-model SQLite locale vive sotto %LOCALAPPDATA%/TestLoom/workspaces/&lt;hash del percorso&gt; e va ripulito
/// da chi lancia il test, non da questo harness.
///
/// Comando:
///   TestLoom_PERF_REFRESH_PROFILE_DATASET=/home/user/testloom-bench/&lt;copia-di-lavoro&gt; \
///   dotnet test src/TestLoom.App.Tests -c Release --filter FullyQualifiedName~PhaseDRefreshNoOpBenchmarkTests
/// </summary>
public sealed class PhaseDRefreshNoOpBenchmarkTests
{
    private readonly ITestOutputHelper _output;
    public PhaseDRefreshNoOpBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void RefreshNoOp_MedianaSu20Giri()
    {
        var dataset = Environment.GetEnvironmentVariable("TestLoom_PERF_REFRESH_PROFILE_DATASET");
        if (string.IsNullOrWhiteSpace(dataset) || !Directory.Exists(dataset))
        {
            _output.WriteLine("TestLoom_PERF_REFRESH_PROFILE_DATASET non impostata o inesistente: harness auto-escluso (non fallito).");
            return;
        }

        var local = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseDRefreshBench_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        try
        {
            var materializer = new PhaseDMasterMaterializer(dataset, local);

            var swFreddo = Stopwatch.StartNew();
            materializer.Refresh(dataset);
            swFreddo.Stop();
            _output.WriteLine($"FREDDO totale={swFreddo.Elapsed.TotalMilliseconds:F1}ms");

            var tempi = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                var sw = Stopwatch.StartNew();
                materializer.Refresh(dataset);
                sw.Stop();
                tempi.Add(sw.Elapsed.TotalMilliseconds);
            }
            tempi.Sort();
            var mediana = tempi.Count % 2 == 1
                ? tempi[tempi.Count / 2]
                : (tempi[tempi.Count / 2 - 1] + tempi[tempi.Count / 2]) / 2.0;
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"CALDO mediana(20)={mediana:F1}ms min={tempi[0]:F1}ms max={tempi[^1]:F1}ms"));

            var outputPath = Environment.GetEnvironmentVariable("TestLoom_PERF_REFRESH_OUTPUT");
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                var sb = new StringBuilder();
                sb.AppendLine($"# Refresh no-op — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine();
                sb.AppendLine("| Freddo (ms) | Caldo mediana/20 (ms) | Min | Max |");
                sb.AppendLine("|---:|---:|---:|---:|");
                sb.AppendLine($"| {swFreddo.Elapsed.TotalMilliseconds:F1} | {mediana:F1} | {tempi[0]:F1} | {tempi[^1]:F1} |");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(local, recursive: true); } catch { /* best-effort */ }
        }
    }
}
