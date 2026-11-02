using System.Diagnostics;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Benchmark diagnostico TEMPORANEO: a differenza di HomeFilteredScaleDiagnosticTests, qui il filtro
/// componente e' persistito (ViewStateService.ScriviLista, stessa chiave usata da HomeViewModel.LegaPersistenza) PRIMA
/// che il primissimo HomeViewModel venga costruito — replica un operatore che riapre l'app con un filtro
/// già salvato da una sessione precedente, non un utente che filtra dopo aver già visto la pagina senza
/// filtro. Verifica se GetAll() (usato dal fallback non scalabile quando un filtro dashboard e' attivo, vedi
/// TryAggiornaDashboardScalabile) costa piu' la prima volta in assoluto rispetto a quanto misurato quando
/// l'apertura precedente (senza filtro) aveva già potenzialmente scaldato qualche cache.</summary>
[Collection("Scale performance")]
public class HomeColdFilteredScaleDiagnosticTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public HomeColdFilteredScaleDiagnosticTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Scale")]
    public void Home_100kTest1kCataloghi_ConFiltroEcuPersistitoPrimaDelPrimoOpen()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_HomeColdFilteredDiag", Guid.NewGuid().ToString("N"));
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

            // Filtro persistito PRIMA di costruire il primo HomeViewModel: LegaPersistenza (chiamata dal
            // costruttore, dopo Ripopola) lo ritrova e lo riapplica subito, esattamente come al riavvio
            // dell'app con un filtro già salvato da una sessione precedente.
            ViewStateService.ScriviLista(state, "Home.FiltroEcu", new List<string> { "Componente-0000" });

            _output.WriteLine("=== Primissima apertura Home, filtro componente già persistito ===");
            var sw = Stopwatch.StartNew();
            var vm = new HomeViewModel(state, _ => { });
            _output.WriteLine($"BENCHMARK|Home primo open con filtro persistito|ms={sw.Elapsed.TotalMilliseconds:F0}");
            Assert.True(vm.FiltroEcuDashboard.HaSelezione, "Il filtro persistito deve essere stato ripristinato.");

            _output.WriteLine("=== Seconda apertura Home, stesso filtro ancora attivo ===");
            sw.Restart();
            var vm2 = new HomeViewModel(state, _ => { });
            _output.WriteLine($"BENCHMARK|Home (ritorno) con filtro persistito|ms={sw.Elapsed.TotalMilliseconds:F0}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
