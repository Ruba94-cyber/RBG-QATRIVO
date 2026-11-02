using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Benchmark diagnostico (stesso metodo di HomeColdFilteredScaleDiagnosticTests): misura
/// Anomalie/CR a 100k Test + 100k NC con un filtro Veicolo già persistito PRIMA della primissima apertura
/// della pagina. NonConformitaViewModel.Ricarica usa PeekAllReadOnly() e clona soltanto la pagina esposta
/// anche quando un filtro Ticket/Veicolo/Componente/Gamma è attivo (ottimizzazione v6.2.5).</summary>
[Collection("Scale performance")]
public class NonConformitaColdFilteredScaleDiagnosticTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public NonConformitaColdFilteredScaleDiagnosticTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Scale")]
    public void AnomalieCr_100kTestE100kNc_ConFiltroVeicoloPersistitoPrimaDelPrimoOpen()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_NcColdFilteredDiag", Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(root, "local", "config.json");
        var dataRoot = Path.Combine(root, "data");
        try
        {
            var paths = new DataPaths(dataRoot);
            paths.EnsureStructure();
            const int testCount = 100_000;
            const int ncCount = 100_000;
            const int catalogSize = 1_000;

            CreaTestRealistici(paths, testCount, catalogSize);
            CreaNonConformitaRealistiche(paths, ncCount, testCount);

            var configService = new AppConfigService(configPath);
            configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Operatore Prova Anomalie" });

            using (var stateFreddo = new AppState(configService, runBackgroundMaintenance: false))
            {
                Assert.Equal(testCount, stateFreddo.TestCases.Count());
                Assert.Equal(ncCount, stateFreddo.NonConformita.CountAll);
                stateFreddo.Master.Gamma.Save(Enumerable.Range(0, 50).Select(i => new Gamma { Nome = $"Gamma-{i:D2}" }).ToList());
                stateFreddo.Master.Veicoli.Save(Enumerable.Range(0, catalogSize).Select(i => new Veicolo { Nome = $"Veicolo-{i:D4}", GammaNome = $"Gamma-{i % 50:D2}" }).ToList());
                stateFreddo.Master.Componente.Save(Enumerable.Range(0, catalogSize).Select(i => new Componente { Nome = $"Componente-{i:D4}", VeicoliAssociati = { $"Veicolo-{i % catalogSize:D4}" } }).ToList());
            }
            SqliteConnection.ClearAllPools();

            using var state = new AppState(configService, runBackgroundMaintenance: false);
            Assert.True(state.TestCases.HasPersistentSnapshot);

            // Filtro persistito PRIMA di costruire il primo NonConformitaViewModel: LegaPersistenza lo ritrova
            // e lo riapplica subito, esattamente come al riavvio dell'app con un filtro già salvato.
            ViewStateService.ScriviLista(state, "AnomalieCr.FiltroVeicolo", new List<string> { "Veicolo-0000" });

            _output.WriteLine("=== Primissima apertura Anomalie/CR, filtro Veicolo già persistito ===");
            var sw = Stopwatch.StartNew();
            var vm = new NonConformitaViewModel(state, _ => { });
            _output.WriteLine($"BENCHMARK|Anomalie/CR primo open con filtro persistito|ms={sw.Elapsed.TotalMilliseconds:F0}");
            Assert.True(vm.FiltroVeicolo.HaSelezione, "Il filtro persistito deve essere stato ripristinato.");

            _output.WriteLine("=== Seconda apertura, stesso filtro ancora attivo ===");
            sw.Restart();
            var vm2 = new NonConformitaViewModel(state, _ => { });
            _output.WriteLine($"BENCHMARK|Anomalie/CR (ritorno) con filtro persistito|ms={sw.Elapsed.TotalMilliseconds:F0}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CreaTestRealistici(DataPaths paths, int count, int catalogSize)
    {
        for (var indice = 0; indice < count; indice++)
        {
            var componente = indice % catalogSize;
            var dir = paths.TestCaseDir($"Componente-{componente:D4}");
            Directory.CreateDirectory(dir);
            var id = $"TC-{indice:D6}";
            var veicolo = indice % catalogSize;
            var funzione = indice % catalogSize;
            var requisito = indice % catalogSize;
            var json = $$"""
            {"SchemaVersion":4,"Uid":"{{Guid.NewGuid()}}","RevisioneDati":1,"Id":"{{id}}","DescrizioneBreve":"Diagnostica scenario {{indice % 137}}","Descrizione":"Verifica realistica funzione {{funzione}}","DataCreazione":"2026-08-01T08:00:00","UltimaModifica":"2026-08-01T12:00:00","ModificatoDa":"Operatore-01","Componente":["Componente-{{componente:D4}}"],"Veicolo":"Veicolo-{{veicolo:D4}}","VeicoliEffettivi":["Veicolo-{{veicolo:D4}}"],"StatoLavoro":"Concluso","Funzionalita":["Funzione-{{funzione:D4}}"],"RequisitoId":["REQ-{{requisito:D5}}"],"CampiCustom":{},"Esecuzioni":[],"Allegati":[],"Changelog":[]}
            """;
            File.WriteAllText(Path.Combine(dir, $"testcase_{id}.json"), json, Encoding.UTF8);
        }
    }

    private static void CreaNonConformitaRealistiche(DataPaths paths, int count, int testCaseCount)
    {
        Directory.CreateDirectory(paths.NonConformitaDir);
        for (var indice = 0; indice < count; indice++)
        {
            var id = $"NC-{indice:D6}";
            // Metà delle NC collegate a un Test reale (più severo del 10% usato in FullScaleNavigationTests,
            // apposta per stressare davvero la risoluzione TestCorrente+filtro Veicolo di questo benchmark).
            var testCaseId = indice % 2 == 0 ? $"TC-{(indice % testCaseCount):D6}" : "";
            var chiusa = indice % 3 == 0;
            var nc = new NonConformita
            {
                Uid = Guid.NewGuid(),
                RevisioneDati = 1,
                Id = id,
                TestCaseId = testCaseId,
                DataApertura = new DateTime(2026, 1, 1).AddDays(indice % 200),
                DataChiusura = chiusa ? new DateTime(2026, 6, 1).AddDays(indice % 100) : null,
                Owner = $"Operatore-{indice % 12:D2}",
                StatoWorkflow = chiusa ? StatoAnomalia.Conclusa : StatoAnomalia.Confermata,
                Severita = (SeveritaAnomalia)(indice % 3),
                Descrizione = $"Anomalia riscontrata su scenario {indice % 137}",
                Azione = "In lavorazione",
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(nc, JsonIo.Options);
            File.WriteAllBytes(paths.NonConformitaFile(id), bytes);
        }
    }
}
