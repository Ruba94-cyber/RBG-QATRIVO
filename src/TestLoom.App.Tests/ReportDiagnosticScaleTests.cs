using System.Diagnostics;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Benchmark diagnostico TEMPORANEO (da rimuovere insieme alla strumentazione Console.WriteLine in
/// ReportViewModel) per isolare quale sotto-passo di Aggiorna()/EseguiRigenerazioniPendenti domina il tempo
/// di apertura Report a 100k Test + 1k cataloghi.</summary>
[Collection("Scale performance")]
public class ReportDiagnosticScaleTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public ReportDiagnosticScaleTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Scale")]
    public void Report_100kTest1kCataloghi_Diagnostica()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_ReportDiag", Guid.NewGuid().ToString("N"));
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
            configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Operatore Prova Report" });

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

            var dateQuery = new LocalTestArchiveQueryService(state.TestCases.LocalIndexDatabasePath!);
            var swDate = Stopwatch.StartNew();
            var afterFixtureDate = dateQuery.QueryAll(new LocalTestArchiveQuery
            {
                CreatedAtOrAfter = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Unspecified)
            });
            _output.WriteLine($"BENCHMARK|Report filtro data raw indicizzato 100k|ms={swDate.Elapsed.TotalMilliseconds:F0}|totale={afterFixtureDate.Count}");
            Assert.Empty(afterFixtureDate);
            Assert.True(swDate.Elapsed < TimeSpan.FromMilliseconds(700),
                $"Filtro data Report 100k troppo lento: {swDate.Elapsed.TotalMilliseconds:F0}ms");

            _output.WriteLine("=== Prima apertura Report ===");
            var sw = Stopwatch.StartNew();
            var vm = new ReportViewModel(state);
            _output.WriteLine($"BENCHMARK|Report totale|ms={sw.Elapsed.TotalMilliseconds:F0}");

            _output.WriteLine("=== Seconda apertura Report ===");
            sw.Restart();
            var vm2 = new ReportViewModel(state);
            _output.WriteLine($"BENCHMARK|Report (ritorno) totale|ms={sw.Elapsed.TotalMilliseconds:F0}");

            _output.WriteLine("=== Filtro componente reale dopo l'apertura ===");
            sw.Restart();
            vm.FiltroEcu = vm.EcuDisponibili.First();
            _output.WriteLine($"BENCHMARK|Report con filtro componente attivo|ms={sw.Elapsed.TotalMilliseconds:F0}|totale={vm.TotaleRisultatiAnteprima}");
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(700), $"Report con filtro componente troppo lento: {sw.Elapsed.TotalMilliseconds:F0}ms");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
