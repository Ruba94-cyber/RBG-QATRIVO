using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Smoke test TEMPORANEO a scala minuscola (100 Test + 100 NC) per isolare se il filtro Veicolo
/// persistito prima del primo open di Anomalie/CR causa un crash/hang indipendente dalla scala, oppure se il
/// problema di NonConformitaColdFilteredScaleDiagnosticTests è solo di performance/contesa a 100k.</summary>
public class NonConformitaColdFilteredSmokeTests
{
    [Fact]
    public void AnomalieCr_100Test100Nc_ConFiltroVeicoloPersistitoPrimaDelPrimoOpen_NonCrasha()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_NcColdFilteredSmoke", Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(root, "local", "config.json");
        var dataRoot = Path.Combine(root, "data");
        try
        {
            var paths = new DataPaths(dataRoot);
            paths.EnsureStructure();
            const int testCount = 100;
            const int ncCount = 100;

            var testUids = new Guid[testCount];
            for (var indice = 0; indice < testCount; indice++)
            {
                testUids[indice] = Guid.NewGuid();
                var dir = paths.TestCaseDir("Componente-0000");
                Directory.CreateDirectory(dir);
                var id = $"TC-{indice:D6}";
                var json = $$"""
                {"SchemaVersion":4,"Uid":"{{testUids[indice]}}","RevisioneDati":1,"Id":"{{id}}","DescrizioneBreve":"D","Descrizione":"V","DataCreazione":"2026-08-01T08:00:00","UltimaModifica":"2026-08-01T12:00:00","ModificatoDa":"Operatore-01","Componente":["Componente-0000"],"Veicolo":"Veicolo-0000","VeicoliEffettivi":["Veicolo-0000"],"StatoLavoro":"Concluso","Funzionalita":[],"RequisitoId":[],"CampiCustom":{},"Esecuzioni":[],"Allegati":[],"Changelog":[]}
                """;
                File.WriteAllText(Path.Combine(dir, $"testcase_{id}.json"), json, System.Text.Encoding.UTF8);
            }

            Directory.CreateDirectory(paths.NonConformitaDir);
            for (var indice = 0; indice < ncCount; indice++)
            {
                var id = $"NC-{indice:D6}";
                var nc = new NonConformita
                {
                    Uid = Guid.NewGuid(), RevisioneDati = 1, Id = id,
                    TestCaseId = $"TC-{(indice % testCount):D6}", TestUid = testUids[indice % testCount],
                    DataApertura = new DateTime(2026, 1, 1).AddDays(indice % 30),
                    Owner = "Operatore-01", StatoWorkflow = StatoAnomalia.Confermata,
                    Severita = SeveritaAnomalia.Medium, Descrizione = "D", Azione = "A",
                };
                var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(nc, JsonIo.Options);
                File.WriteAllBytes(paths.NonConformitaFile(id), bytes);
            }

            var configService = new AppConfigService(configPath);
            configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Operatore Prova" });

            using (var stateFreddo = new AppState(configService, runBackgroundMaintenance: false))
            {
                Assert.Equal(testCount, stateFreddo.TestCases.Count());
                Assert.Equal(ncCount, stateFreddo.NonConformita.CountAll);
                stateFreddo.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Veicolo-0000", GammaNome = "Gamma-00" } });
            }
            SqliteConnection.ClearAllPools();

            using var state = new AppState(configService, runBackgroundMaintenance: false);
            ViewStateService.ScriviLista(state, "AnomalieCr.FiltroVeicolo", new List<string> { "Veicolo-0000" });

            var vm = new NonConformitaViewModel(state, _ => { });
            Assert.True(vm.FiltroVeicolo.HaSelezione);
            Assert.NotEmpty(vm.Righe);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
