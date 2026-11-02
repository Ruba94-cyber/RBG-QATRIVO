using System.Diagnostics;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Benchmark piano 6.5.0 punto 5 (tipo grafico Home) a 100k Test + 1k cataloghi: apertura Home con
/// il tipo predefinito (confrontabile con HomeDiagnosticScaleTests, baseline pre-modifica), apertura con un tipo
/// diverso persistito e costo del solo cambio tipo (riuso degli aggregati, nessuna query).</summary>
[Collection("Scale performance")]
public class HomeGraficiTipoBenchmarkTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public HomeGraficiTipoBenchmarkTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    /// <summary>Costo del solo render PNG (SkiaSharp) con aggregati realistici a 100k test: 1000 sottoveicoli,
    /// 365 punti di andamento. Il refresh sincrono della Home paga questo costo (+ decodifica Bitmap) per il tipo
    /// non predefinito quando i dati cambiano.</summary>
    [Fact]
    [Trait("Category", "Scale")]
    public void RenderPng_PerTipo_CostoMediano()
    {
        var voci = Enumerable.Range(0, 1000).Select(i => ($"Veicolo-{i:D4}", 100L + (i * 37 % 500))).ToList();
        var esiti = new List<(Esito Esito, long Valore)> { (Esito.Conforme, 80000), (Esito.NonConforme, 5000), (Esito.NonEseguitoNA, 15000) };
        var punti = Enumerable.Range(0, 365).Select(i => (new DateTime(2026, 1, 1).AddDays(i), 10 + i % 40, 5 + i % 30)).ToList();
        double Mediana(Action a)
        {
            a();
            var t = new List<double>();
            for (var i = 0; i < 9; i++) { var sw = Stopwatch.StartNew(); a(); t.Add(sw.Elapsed.TotalMilliseconds); }
            t.Sort(); return t[t.Count / 2];
        }
        _output.WriteLine($"BENCHMARK|render PNG Ciambella|mediana ms={Mediana(() => ChartRenderer.DonutEsitiPerSottoveicolo("t", voci, esiti, 900, 480)):F1}");
        foreach (var tipo in new[] { TipoGrafico.Torta, TipoGrafico.BarreOrizzontali, TipoGrafico.Treemap })
            _output.WriteLine($"BENCHMARK|render PNG {tipo}|mediana ms={Mediana(() => ChartRenderer.SottoveicoloPerTipo(tipo, "t", voci)):F1}");
        _output.WriteLine($"BENCHMARK|render PNG Andamento Linea|mediana ms={Mediana(() => ChartRenderer.AndamentoNonConformita("t", punti, 900, 380, false)):F1}");
        _output.WriteLine($"BENCHMARK|render PNG Andamento Area|mediana ms={Mediana(() => ChartRenderer.AndamentoNonConformita("t", punti, 900, 380, true)):F1}");
        var bucket = new CoverageBucket { Conformi = 80000, ConformiConDeviazioni = 3000, NonConformi = 5000, NonTestati = 12000 };
        foreach (var tipo in new[] { TipoGrafico.BarreVerticali, TipoGrafico.Torta, TipoGrafico.Treemap })
            _output.WriteLine($"BENCHMARK|render PNG Copertura {tipo}|mediana ms={Mediana(() => ChartRenderer.CoperturaPerTipo(tipo, "t", bucket)):F1}");
    }

    [Fact]
    [Trait("Category", "Scale")]
    public async Task Home_100kTest1kCataloghi_TipoGrafico_Benchmark()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_HomeTipoBench", Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(root, "local", "config.json");
        var dataRoot = Path.Combine(root, "data");
        try
        {
            var paths = new DataPaths(dataRoot);
            paths.EnsureStructure();
            const int testCount = 100_000;
            const int catalogSize = 1_000;
            for (var indice = 0; indice < testCount; indice++)
            {
                var ecuN = indice % catalogSize;
                var dir = paths.TestCaseDir($"Componente-{ecuN:D4}");
                Directory.CreateDirectory(dir);
                var id = $"TC-{indice:D6}";
                var json = $$"""
                {"SchemaVersion":4,"Uid":"{{Guid.NewGuid()}}","RevisioneDati":1,"Id":"{{id}}","DescrizioneBreve":"D {{indice % 137}}","Descrizione":"V","DataCreazione":"2026-08-01T08:00:00","UltimaModifica":"2026-08-01T12:00:00","ModificatoDa":"Operatore-01","Componente":["Componente-{{ecuN:D4}}"],"Veicolo":"Veicolo-{{indice % catalogSize:D4}}","VeicoliEffettivi":["Veicolo-{{indice % catalogSize:D4}}"],"StatoLavoro":"Concluso","Funzionalita":["Funzione-{{indice % catalogSize:D4}}"],"RequisitoId":["REQ-{{indice % catalogSize:D5}}"],"CampiCustom":{},"Esecuzioni":[],"Allegati":[],"Changelog":[]}
                """;
                File.WriteAllText(Path.Combine(dir, $"testcase_{id}.json"), json, Encoding.UTF8);
            }

            var configService = new AppConfigService(configPath);
            configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Operatore Prova Home" });
            using (var stateFreddo = new AppState(configService, runBackgroundMaintenance: false))
            {
                Assert.Equal(testCount, stateFreddo.TestCases.Count());
                stateFreddo.Master.Gamma.Save(Enumerable.Range(0, 50).Select(i => new Gamma { Nome = $"Gamma-{i:D2}" }).ToList());
                stateFreddo.Master.Veicoli.Save(Enumerable.Range(0, catalogSize).Select(i => new Veicolo { Nome = $"Veicolo-{i:D4}", GammaNome = $"Gamma-{i % 50:D2}" }).ToList());
                stateFreddo.Master.Componente.Save(Enumerable.Range(0, catalogSize).Select(i => new Componente { Nome = $"Componente-{i:D4}", VeicoliAssociati = { $"Veicolo-{i % catalogSize:D4}" } }).ToList());
                stateFreddo.Master.Funzionalita.Save(Enumerable.Range(0, catalogSize).Select(i => new Funzionalita { Nome = $"Funzione-{i:D4}", Categorie = new() { $"Categoria-{i % 50:D2}" } }).ToList());
                stateFreddo.Master.Requisiti.Save(Enumerable.Range(0, catalogSize).Select(i => new Requisito { Id = $"REQ-{i:D5}", Testo = $"Requisito {i}" }).ToList());
            }
            SqliteConnection.ClearAllPools();

            using var state = new AppState(configService, runBackgroundMaintenance: false);
            Assert.True(state.TestCases.HasPersistentSnapshot);

            // 1) apertura con tipo predefinito (stessa misura della baseline HomeDiagnosticScaleTests)
            var sw = Stopwatch.StartNew();
            var vm = new HomeViewModel(state, _ => { });
            _output.WriteLine($"BENCHMARK|Home apertura (Ciambella predefinita)|ms={sw.Elapsed.TotalMilliseconds:F0}");
            sw.Restart();
            var vm2 = new HomeViewModel(state, _ => { });
            _output.WriteLine($"BENCHMARK|Home ritorno (Ciambella predefinita)|ms={sw.Elapsed.TotalMilliseconds:F0}");

            // 2) solo cambio tipo: aggregati riusati, nessuna query
            var donut = vm2.ChartOptions.Single(o => o.Id == "donut");
            foreach (var tipo in new[] { TipoGrafico.Treemap, TipoGrafico.BarreOrizzontali, TipoGrafico.Torta, TipoGrafico.Ciambella })
            {
                var prima = PerfContatori.LeggiSnapshot();
                sw.Restart();
                donut.TipoSelezionato = donut.TipiDisponibili.Single(t => t.Tipo == tipo);
                var ritornoUi = sw.Elapsed.TotalMilliseconds;           // costo sul thread chiamante (UI)
                await vm2.UltimoCambioTipoTask;
                var totale = sw.Elapsed.TotalMilliseconds;               // fino a bitmap pronto
                var dopo = PerfContatori.LeggiSnapshot();
                _output.WriteLine($"BENCHMARK|Cambio tipo -> {tipo}|thread UI ms={ritornoUi:F1}|fino a pronto ms={totale:F0}|query Test/Master/Cataloghi={dopo.TestCaseGetAll - prima.TestCaseGetAll}/{dopo.MasterLoad - prima.MasterLoad}/{dopo.LoadCatalog - prima.LoadCatalog}");
                Assert.Equal(prima.TestCaseGetAll, dopo.TestCaseGetAll);
                Assert.Equal(prima.MasterLoad, dopo.MasterLoad);
                Assert.Equal(prima.LoadCatalog, dopo.LoadCatalog);
                Assert.True(totale < 3000, $"cambio tipo {tipo} troppo lento: {totale:F0} ms");
            }

            // 3) apertura con un tipo non predefinito persistito (Treemap su 1000 sottoveicoli -> "Altri")
            donut.TipoSelezionato = donut.TipiDisponibili.Single(t => t.Tipo == TipoGrafico.Treemap);
            await vm2.UltimoCambioTipoTask;
            sw.Restart();
            var vm3 = new HomeViewModel(state, _ => { });
            _output.WriteLine($"BENCHMARK|Home ritorno (Treemap persistito)|ms={sw.Elapsed.TotalMilliseconds:F0}");
            Assert.Equal(TipoGrafico.Treemap, Assert.Single(vm3.ChartsAttivi).Tipo);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
