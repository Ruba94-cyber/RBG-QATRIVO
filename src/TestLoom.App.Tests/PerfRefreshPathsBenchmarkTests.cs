using System.Diagnostics;
using System.Globalization;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>
/// Banco del refresh incrementale (TestCaseRepository.RefreshPaths -> LocalTestIndexStore.ApplyBatch) su un dataset di Test
/// sintetico. Per ogni campione si modificano k file sul disco (non misurato) e si misura RefreshPaths sui soli k percorsi,
/// con k = 1, 100, 1000. Gating: TestLoom_PERF_REFRESH=1. TestLoom_PERF_REFRESH_SCALE (default 20000), TestLoom_PERF_REFRESH_CAMPIONI
/// (default 5), TestLoom_PERF_REFRESH_LABEL per il file in .ai/artifacts/perf, TestLoom_PERF_REFRESH_OUT per un file di uscita. Ogni campione usa percorsi distinti.
/// Per il confronto fra versioni si eseguono due binari (base e corrente) alternati: il banco non dipende dal codice modificato.
/// </summary>
[Collection("Scale performance")]
[Trait("Category", "Benchmark")]
public sealed class PerfRefreshPathsBenchmarkTests
{
    private static readonly int[] DimensioniDefault = { 1, 10, 100, 1000 };

    /// <summary>Dimensioni misurate: TestLoom_PERF_REFRESH_DIMENSIONI=1,10 limita il banco (per campioni piu' numerosi su una sola dimensione).</summary>
    private static int[] Dimensioni => Environment.GetEnvironmentVariable("TestLoom_PERF_REFRESH_DIMENSIONI") is { Length: > 0 } v
        ? v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray()
        : DimensioniDefault;

    private readonly ITestOutputHelper _output;
    public PerfRefreshPathsBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void MisuraRefreshPaths()
    {
        if (Environment.GetEnvironmentVariable("TestLoom_PERF_REFRESH") != "1")
        {
            _output.WriteLine("TestLoom_PERF_REFRESH non impostata a 1: benchmark escluso (non eseguito).");
            return;
        }
        var scala = int.TryParse(Environment.GetEnvironmentVariable("TestLoom_PERF_REFRESH_SCALE"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 20_000;
        var campioni = int.TryParse(Environment.GetEnvironmentVariable("TestLoom_PERF_REFRESH_CAMPIONI"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var c) && c > 0 ? c : 5;
        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_REFRESH_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";
        var radice = TrovaRadiceRepo();
        var temporanea = Path.Combine(radice ?? Path.GetTempPath(), ".ai", "tmp", "perf-refresh-" + Guid.NewGuid().ToString("N")[..8]);
        var misure = new Dictionary<int, List<double>>();
        try
        {
            var dataRoot = Path.Combine(temporanea, "data");
            var paths = new DataPaths(dataRoot);
            paths.EnsureStructure();
            PerfRiconciliazioneIndiceTests.CreaDataset(paths, scala);
            var elenco = PerfRiconciliazioneIndiceTests.ElencaTest(paths);
            var repo = new TestCaseRepository(paths, new LocalTestIndexStore(dataRoot, Path.Combine(temporanea, "local")));
            repo.RefreshIndex();

            var offset = 0;
            foreach (var k in Dimensioni)
            {
                misure[k] = new List<double>(campioni);
                for (var i = 0; i < campioni; i++)
                {
                    var percorsi = new List<string>(k);
                    for (var j = 0; j < k; j++)
                    {
                        var percorso = elenco[(offset + j) % elenco.Count].Path;
                        PerfRiconciliazioneIndiceTests.MutaTest(percorso, $"refresh-{k}-{i}-{j}");
                        percorsi.Add(percorso);
                    }
                    offset += k;
                    GC.Collect();
                    var sw = Stopwatch.StartNew();
                    repo.RefreshPaths(percorsi);
                    sw.Stop();
                    misure[k].Add(sw.Elapsed.TotalMilliseconds);
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(temporanea, recursive: true); } catch { }
        }
        Scrivi(radice, etichetta, scala, campioni, misure);
    }

    private void Scrivi(string? radice, string etichetta, int scala, int campioni, Dictionary<int, List<double>> misure)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# RefreshPaths: {etichetta}, {scala:N0} Test, {campioni} campioni per dimensione");
        sb.AppendLine();
        sb.AppendLine("| File modificati | p50 ms | max ms |");
        sb.AppendLine("|---:|---:|---:|");
        foreach (var k in Dimensioni)
        {
            var ordinati = misure[k].OrderBy(x => x).ToArray();
            sb.AppendLine($"| {k} | {ordinati[ordinati.Length / 2]:F1} | {ordinati[^1]:F1} |");
        }
        _output.WriteLine(sb.ToString());
        var uscita = Environment.GetEnvironmentVariable("TestLoom_PERF_REFRESH_OUT");
        if (!string.IsNullOrWhiteSpace(uscita)) File.WriteAllText(uscita, sb.ToString(), Encoding.UTF8);
        if (radice == null) return;
        var cartella = Path.Combine(radice, ".ai", "artifacts", "perf");
        Directory.CreateDirectory(cartella);
        File.WriteAllText(Path.Combine(cartella, $"refresh-{etichetta}-{scala / 1000}k.md"), sb.ToString(), Encoding.UTF8);
    }

    private static string? TrovaRadiceRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TestLoom.slnx"))) return dir.FullName;
        return null;
    }
}
