using System.Diagnostics;
using System.Reflection;
using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>Benchmark riproducibile dell'origine bounded a scala 100k Test (dataset sintetico deterministico):
/// tutte le righe vincitrici + limite aggiuntive corrente, scansione selezione e scrittura del foglio/salvataggio. Da eseguire da solo
/// (Category=Benchmark). Il picco è il PeakWorkingSet64 del processo di test (limite superiore: include
/// il dataset stesso e l'host xUnit).</summary>
public sealed class ReportOrigineExportBenchmarkTests
{
    private readonly ITestOutputHelper _output;
    public ReportOrigineExportBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Origine_CentomilaTest_TempoEPicco()
    {
        const int n = 100_000;
        var funz = Enumerable.Range(0, 50).Select(i => new Funzionalita { Nome = $"Funz-{i:D2}", Categorie = [$"Cat-{i % 5}"] }).ToList();
        var casi = new List<TestCase>(n);
        for (var i = 0; i < n; i++)
        {
            var tc = new TestCase
            {
                Id = $"T-{i:D6}", DescrizioneBreve = $"Test sintetico {i}",
                Componente = new EcuSelection([$"Componente-{i % 40:D2}"]), Veicoli = [$"Veicolo-{i % 60:D2}"], Funzionalita = [$"Funz-{i % 50:D2}", $"Funz-{(i + 7) % 50:D2}", $"Funz-{(i + 13) % 50:D2}"]
            };
            var componente = new EcuSelection([$"Componente-{i % 40:D2}"]);
            tc.Esecuzioni.Add(new Esecuzione
            {
                Data = new DateTime(2026, 1, 1).AddMinutes(i), Operatore = "Op", Note = "nota",
                Condizioni = tc.Funzionalita.Select(f => new Condizione { Funzionalita = f, Componente = componente, Esito = i % 7 == 0 ? Esito.NonConforme : Esito.Conforme }).ToList()
            });
            casi.Add(tc);
        }
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.CategoriaFunzionalita };

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var baseBytes = GC.GetTotalMemory(true);
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var picco0 = proc.PeakWorkingSet64;
        var alloc0 = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();

        var impronteWatch = Stopwatch.StartNew();
        var allocImpronte0 = GC.GetTotalAllocatedBytes(true);
        var impronte = ReportService.CatturaFirmeTestCases(casi);
        var allocImpronte = GC.GetTotalAllocatedBytes(true) - allocImpronte0;
        var impronteMs = impronteWatch.Elapsed.TotalMilliseconds;

        var (righe, mostrate, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(casi, config, [], funz, [],
            ReportOrigineExportService.LimiteRigheAggiuntive, []);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vincitori = 0;
        foreach (var riga in righe)
        {
            ids.Add(riga.TestId);
            if (riga.DeterminaStato) vincitori++;
        }
        using var wb = new XLWorkbook();
        typeof(ReportOrigineExportService).GetMethod("ScriviOrigine", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [wb, righe, mostrate, totale, config, System.Threading.CancellationToken.None, 1_048_571]);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);

        watch.Stop();
        proc.Refresh();
        var picco1 = proc.PeakWorkingSet64;
        _output.WriteLine($"ORIGINE_EXPORT|test={n}|candidati={totale}|winner={vincitori}|righe={mostrate}|escluse={totale - mostrate}|limite_aggiuntive={ReportOrigineExportService.LimiteRigheAggiuntive}|ids={ids.Count}|tempo_s={watch.Elapsed.TotalSeconds:F1}" +
            $"|alloc_mib={(GC.GetTotalAllocatedBytes(true) - alloc0) / 1048576.0:F0}|dataset_mib={baseBytes / 1048576.0:F0}" +
            $"|picco_ws_prima_mib={picco0 / 1048576.0:F0}|picco_ws_dopo_mib={picco1 / 1048576.0:F0}|xlsx_mib={ms.Length / 1048576.0:F1}" +
            $"|fingerprint_100k_ms={impronteMs:F0}|fingerprint_100k_alloc_mib={allocImpronte / 1048576.0:F0}|fingerprint_100k_entries={impronte.Count}");
        Assert.True(vincitori > 0);
        Assert.True(mostrate >= vincitori);
        Assert.True(totale >= mostrate);
    }
}
