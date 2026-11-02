using System.Diagnostics;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Micro-harness in-process richiesto dall'Addendum prestazioni 2026-09-25: misura col solo Stopwatch, su un
/// banco ridotto (non i 100k del banco reale, per restare eseguibile come test unitario), quanto costano N
/// rivisite consecutive di <see cref="HomeViewModel.Aggiorna"/> a dati invariati DOPO il consolidamento in una
/// sola lettura SQLite (<see cref="LocalTestArchiveQueryService.GetHomeAggiornaBatch"/>) e il confronto di
/// contenuto in <see cref="FiltroMultiSelect.Ripopola"/>. Non sostituisce la misura a macchina ferma sul banco
/// 100k (strumento perf-ui, di competenza del coordinatore): serve a dimostrare la direzione dell'ottimizzazione
/// e a far da guardrail contro una regressione monotona fra rivisite successive, sullo stesso principio di
/// WuStatusRicaricaCacheTests/ReportAggiornaCacheTests.
/// </summary>
[Collection("Scale performance")]
public sealed class HomeAggiornaBatchMicroBenchmarkTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public HomeAggiornaBatchMicroBenchmarkTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Scale")]
    public void Home_RivisiteConsecutive_NonCresconoMonotonicamenteERestanoUnaFrazioneDellaPrimaApertura()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_HomeBatchBench", Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(root, "local", "config.json");
        var dataRoot = Path.Combine(root, "data");
        try
        {
            var paths = new DataPaths(dataRoot);
            paths.EnsureStructure();
            const int testCount = 15_000;
            const int catalogSize = 300;

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
                stateFreddo.Master.Gamma.Save(Enumerable.Range(0, 20).Select(i => new Gamma { Nome = $"Gamma-{i:D2}" }).ToList());
                stateFreddo.Master.Veicoli.Save(Enumerable.Range(0, catalogSize).Select(i => new Veicolo { Nome = $"Veicolo-{i:D4}", GammaNome = $"Gamma-{i % 20:D2}" }).ToList());
                stateFreddo.Master.Componente.Save(Enumerable.Range(0, catalogSize).Select(i => new Componente { Nome = $"Componente-{i:D4}", VeicoliAssociati = { $"Veicolo-{i % catalogSize:D4}" } }).ToList());
                stateFreddo.Master.Funzionalita.Save(Enumerable.Range(0, catalogSize).Select(i => new Funzionalita { Nome = $"Funzione-{i:D4}", Categorie = new() { $"Categoria-{i % 20:D2}" } }).ToList());
                stateFreddo.Master.Requisiti.Save(Enumerable.Range(0, catalogSize).Select(i => new Requisito { Id = $"REQ-{i:D5}", Testo = $"Requisito {i}" }).ToList());
            }
            SqliteConnection.ClearAllPools();

            using var state = new AppState(configService, runBackgroundMaintenance: false);
            Assert.True(state.TestCases.HasPersistentSnapshot);

            var sw = Stopwatch.StartNew();
            var vm = new HomeViewModel(state, _ => { });
            var primaAperturaMs = sw.Elapsed.TotalMilliseconds;
            _output.WriteLine($"BENCHMARK|Home prima apertura ({testCount} test)|ms={primaAperturaMs:F1}");

            const int rivisite = 24;
            var tempi = new List<double>(rivisite);
            for (var i = 0; i < rivisite; i++)
            {
                sw.Restart();
                vm.Aggiorna();
                tempi.Add(sw.Elapsed.TotalMilliseconds);
            }
            _output.WriteLine($"BENCHMARK|Home {rivisite} rivisite|rivisite_ms=[{string.Join(",", tempi.Select(t => t.ToString("F1")))}]");
            _output.WriteLine($"BENCHMARK|Home rivisita media|ms={tempi.Average():F1}|max={tempi.Max():F1}");

            // Guardrail anti-regressione (sullo stesso principio di WuStatusRicaricaCacheTests): le rivisite a
            // dati invariati non devono crescere in modo monotono ne' costare quanto la prima apertura a freddo.
            Assert.True(tempi[^1] <= tempi[0] * 3 + 50,
                $"Le rivisite crescono nel tempo (prima={tempi[0]:F1}ms, ultima={tempi[^1]:F1}ms).");
            Assert.True(tempi.Average() < primaAperturaMs,
                $"Le rivisite (media={tempi.Average():F1}ms) non dovrebbero costare quanto la prima apertura ({primaAperturaMs:F1}ms).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
