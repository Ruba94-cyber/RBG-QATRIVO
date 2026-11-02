using System.Diagnostics;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Benchmark diagnostico TEMPORANEO: misura Home a 100k Test + 1k cataloghi con un filtro dashboard
/// realmente attivo (feedback utente 2026-09-20, "la maggior parte delle persone terrà i filtri sempre
/// attivi"), non solo nel caso peggiore senza filtro già coperto da HomeDiagnosticScaleTests. Un filtro attivo
/// fa uscire TryAggiornaDashboardScalabile dal percorso SQL (vedi il guard su HaSelezione in
/// HomeViewModel.Scalable.cs) verso il fallback in-memory GetAll()+DashboardFilter.Applica.</summary>
[Collection("Scale performance")]
public class HomeFilteredScaleDiagnosticTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public HomeFilteredScaleDiagnosticTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Scale")]
    public void Home_100kTest1kCataloghi_ConFiltroEcuAttivo()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_HomeFilteredDiag", Guid.NewGuid().ToString("N"));
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

            var vm = new HomeViewModel(state, _ => { });
            Assert.True(vm.FiltroEcuDashboard.Opzioni.Count > 0, "La tendina componente deve essere popolata prima di poter selezionare un valore.");

            _output.WriteLine("=== Attivazione filtro componente (simula un operatore con filtro sempre attivo) ===");
            var sw = Stopwatch.StartNew();
            vm.FiltroEcuDashboard.Opzioni.First().Selezionato = true;
            _output.WriteLine($"BENCHMARK|Home con filtro Componente attivo|ms={sw.Elapsed.TotalMilliseconds:F0}");

            _output.WriteLine("=== Seconda attivazione (rimuovo e riattivo, per un secondo campione) ===");
            vm.FiltroEcuDashboard.Opzioni.First().Selezionato = false;
            sw.Restart();
            vm.FiltroEcuDashboard.Opzioni.First().Selezionato = true;
            _output.WriteLine($"BENCHMARK|Home con filtro Componente attivo (ripetizione)|ms={sw.Elapsed.TotalMilliseconds:F0}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
