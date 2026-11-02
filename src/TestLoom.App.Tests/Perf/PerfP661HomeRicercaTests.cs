using System.Diagnostics;
using System.Globalization;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Xunit.Abstractions;

namespace TestLoom.App.Tests.Perf;

/// <summary>
/// Banco ciclo prestazioni 6.6.1, ricerca testuale della Home (Da fare / Fatti). Per ogni termine misura:
/// il tempo che il setter di TestoRicerca tiene bloccato il thread chiamante (un tasto, percorso indicizzato);
/// il ricalcolo delle due liste con il testo applicato, A/B nello stesso processo tra percorso in memoria
/// (PercorsoIndicizzatoAttivo = false) e indicizzato (true), campioni alternati. Parita': totali uguali e prima pagina
/// di dimensione min(100, totale). Dataset deterministico con note e deviazioni nelle esecuzioni (un Test ogni 4).
///
/// Attivo solo con TestLoom_PERF_P661_HOME=1. TestLoom_PERF_MEDIA_SCALE limita la scala, TestLoom_PERF_P661_LABEL etichetta l'output.
/// Output: .ai/artifacts/perf/p661-home-{etichetta}-{N}k.tsv nella radice del repository (non versionata).
/// </summary>
[Collection("Scale performance")]
[Trait("Category", "Benchmark")]
public sealed class PerfP661HomeRicercaTests
{
    private const int CampioniMisurati = 5;
    private const int DimensionePaginaHome = 100;
    private static readonly string[] Termini =
    {
        "banco scenario 17", "Componente-03", "scenario 42", "nota banco 7", "REQ-", "Operatore-Bench-07", "deviazione 3", "TC-0001",
    };

    private readonly ITestOutputHelper _output;
    public PerfP661HomeRicercaTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(5_000)]
    [InlineData(20_000)]
    public async Task MisuraRicercaTestoHomeAScala(int scala)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_P661_HOME"), "1", StringComparison.Ordinal)) return;
        var scalaSolo = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_SCALE");
        if (!string.IsNullOrWhiteSpace(scalaSolo) && scalaSolo != scala.ToString(CultureInfo.InvariantCulture)) return;
        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_P661_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";

        var radice = TrovaRadiceRepo() ?? Path.GetTempPath();
        var root = Path.Combine(radice, ".ai", "tmp", "perf-p661-home", $"{scala}_{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(root, "data");
        var configPath = Path.Combine(root, "local", "config.json");
        var righe = new List<string>();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var paths = new DataPaths(dataRoot);
            paths.EnsureStructure();
            PerfP661ListaTestTests.DatasetP661.Genera(paths, scala, conEsecuzioni: true);

            var configService = new AppConfigService(configPath);
            configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "" });
            using var state = new AppState(configService, runBackgroundMaintenance: false);
            state.TestCases.RefreshIndex();

            var memoria = new HomeViewModel(state, _ => { }) { PercorsoIndicizzatoAttivo = false };
            var indicizzato = new HomeViewModel(state, _ => { }) { PercorsoIndicizzatoAttivo = true };
            await AttendiQuieto(memoria);
            await AttendiQuieto(indicizzato);

            foreach (var termine in Termini)
            {
                // Tasto: tempo bloccante del setter sul thread chiamante, percorso indicizzato.
                var sw = Stopwatch.StartNew();
                indicizzato.TestoRicerca = termine;
                sw.Stop();
                var bloccoTasto = sw.Elapsed.TotalMilliseconds;
                memoria.TestoRicerca = termine;
                await Task.Delay(HomeViewModel.DebounceTestoRicerca + TimeSpan.FromMilliseconds(120));
                await AttendiQuieto(memoria);
                await AttendiQuieto(indicizzato);

                // Ricalcolo A/B con il testo applicato: riscaldamento + campioni alternati.
                var tempiMemoria = new List<double>();
                var tempiIndicizzato = new List<double>();
                for (var k = 0; k <= CampioniMisurati; k++)
                {
                    var ordine = k % 2 == 0 ? new[] { false, true } : new[] { true, false };
                    foreach (var usaIndice in ordine)
                    {
                        var vm = usaIndice ? indicizzato : memoria;
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        var s2 = Stopwatch.StartNew();
                        vm.AggiornaListeSincrono();
                        s2.Stop();
                        if (k == 0) continue;
                        (usaIndice ? tempiIndicizzato : tempiMemoria).Add(s2.Elapsed.TotalMilliseconds);
                    }
                }

                var parita = memoria.DaFare.Count == indicizzato.TotaleDaFare
                    && memoria.Fatti.Count == indicizzato.TotaleFatti
                    && indicizzato.DaFare.Count == Math.Min(DimensionePaginaHome, memoria.DaFare.Count)
                    && indicizzato.Fatti.Count == Math.Min(DimensionePaginaHome, memoria.Fatti.Count);
                var (p50M, p95M) = Percentili(tempiMemoria);
                var (p50I, p95I) = Percentili(tempiIndicizzato);
                var esito = parita ? "OK" : "KO";
                _output.WriteLine($"{termine} ({scala} Test): blocco tasto {bloccoTasto:F2} ms; ricalcolo in memoria p50 {p50M:F1} ms; " +
                    $"indicizzato p50 {p50I:F1} ms (p95 {p95I:F1}); Da fare {memoria.DaFare.Count}, Fatti {memoria.Fatti.Count}; parita {esito}");
                righe.Add(string.Join('\t', termine, bloccoTasto.ToString("F2", CultureInfo.InvariantCulture),
                    p50M.ToString("F2", CultureInfo.InvariantCulture), p95M.ToString("F2", CultureInfo.InvariantCulture),
                    p50I.ToString("F2", CultureInfo.InvariantCulture), p95I.ToString("F2", CultureInfo.InvariantCulture),
                    memoria.DaFare.Count, memoria.Fatti.Count, indicizzato.TotaleDaFare, indicizzato.TotaleFatti, esito));
            }

            var tsv = new StringBuilder();
            tsv.AppendLine("termine\tblocco_tasto_indicizzato_ms\tricalcolo_memoria_p50_ms\tricalcolo_memoria_p95_ms" +
                "\tricalcolo_indicizzato_p50_ms\tricalcolo_indicizzato_p95_ms\tda_fare_memoria\tfatti_memoria\tda_fare_totale_indicizzato" +
                "\tfatti_totale_indicizzato\tparita");
            foreach (var r in righe) tsv.AppendLine(r);
            var cartella = Path.Combine(radice, ".ai", "artifacts", "perf");
            Directory.CreateDirectory(cartella);
            File.WriteAllText(Path.Combine(cartella, $"p661-home-{etichetta}-{scala / 1000}k.tsv"), tsv.ToString());
            Assert.DoesNotContain(righe, r => r.EndsWith("\tKO", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        }
    }

    private static (double P50, double P95) Percentili(List<double> campioni)
    {
        var ordinati = campioni.OrderBy(x => x).ToArray();
        var p50 = ordinati[(ordinati.Length - 1) / 2];
        var p95 = ordinati[Math.Min(ordinati.Length - 1, (int)Math.Ceiling(0.95 * ordinati.Length) - 1)];
        return (p50, p95);
    }

    /// <summary>Attende che il giro di aggiornamento in background della Home sia terminato (massimo 20 s).</summary>
    private static async Task AttendiQuieto(HomeViewModel vm)
    {
        for (var i = 0; i < 2000 && vm.CaricamentoInCorso; i++) await Task.Delay(10);
    }

    private static string? TrovaRadiceRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TestLoom.slnx"))) return dir.FullName;
        return null;
    }
}
