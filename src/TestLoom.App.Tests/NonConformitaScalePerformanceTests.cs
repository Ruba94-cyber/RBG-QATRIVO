using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Guardrail di scala per la pagina Anomalie/CR (§5): stesso principio di ScalePerformanceTests per i
/// Test, ma sul percorso NonConformitaViewModel, che ha due strade distinte in Ricarica() — quella "rapida"
/// (solo filtri propri della NC) e quella che deve risolvere il TestCase corrente per ogni candidato quando è
/// attivo un filtro Veicolo/componente/Gamma/Ticket. Richiesto esplicitamente per il deploy: qualunque apertura pagina
/// deve restare sotto 700ms anche con 100.000 Non Conformità.</summary>
[Collection("Scale performance")]
public class NonConformitaScalePerformanceTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public NonConformitaScalePerformanceTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Scale")]
    public void NonConformita100k_AperturaPaginaEFiltroVeicolo_RestanoSotto700ms()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_NcScale", Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(root, "local", "config.json");
        var dataRoot = Path.Combine(root, "data");
        try
        {
            var paths = new DataPaths(dataRoot);
            paths.EnsureStructure();

            const int testCaseCount = 500;
            const int ncCount = 100_000;
            CreaTestRealistici(paths, testCaseCount);
            CreaNonConformitaRealistiche(paths, ncCount, testCaseCount);

            var configService = new AppConfigService(configPath);
            configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Operatore Prova NC" });
            using var state = new AppState(configService, runBackgroundMaintenance: false);
            Assert.Equal(ncCount, state.NonConformita.CountAll);

            state.Master.Gamma.Save(Enumerable.Range(0, 10).Select(i => new Gamma { Nome = $"Gamma-{i:D2}" }).ToList());
            state.Master.Veicoli.Save(Enumerable.Range(0, 50).Select(i => new Veicolo { Nome = $"Veicolo-{i:D3}", GammaNome = $"Gamma-{i % 10:D2}" }).ToList());
            state.Master.Componente.Save(Enumerable.Range(0, 50).Select(i => new Componente { Nome = $"Componente-{i:D3}", VeicoliAssociati = { $"Veicolo-{i % 50:D3}" } }).ToList());

            // 1. Apertura pagina "a freddo": percorso rapido, solo filtri propri della NC (default: Aperta).
            var sw = Stopwatch.StartNew();
            var vm = new NonConformitaViewModel(state, _ => { });
            var aperturaTempo = sw.Elapsed;
            _output.WriteLine($"BENCHMARK|nc_count={ncCount}|apertura_ms={aperturaTempo.TotalMilliseconds:F0}|totale_righe={vm.TotaleRighe}");
            Assert.True(aperturaTempo < TimeSpan.FromMilliseconds(700), $"Apertura pagina Anomalie troppo lenta: {aperturaTempo.TotalMilliseconds}ms");

            // 2. Mostra tutte (Aperte+Chiuse): stesso percorso rapido, ma senza filtro Stato che scarta metà dei candidati.
            sw.Restart();
            vm.FiltroStato = null;
            var tuttiTempo = sw.Elapsed;
            _output.WriteLine($"BENCHMARK|nc_count={ncCount}|filtro_stato_null_ms={tuttiTempo.TotalMilliseconds:F0}|totale_righe={vm.TotaleRighe}");
            Assert.Equal(ncCount, vm.TotaleRighe);
            Assert.True(tuttiTempo < TimeSpan.FromMilliseconds(700), $"Rimozione filtro Stato troppo lenta: {tuttiTempo.TotalMilliseconds}ms");

            // 3. Percorso "lento": un filtro Veicolo forza la risoluzione del TestCase corrente per OGNI NC
            // candidata prima di poter paginare (Ricarica(), richiedeTestPerFiltro=true).
            sw.Restart();
            vm.FiltroVeicolo.Opzioni.First(o => o.Valore == "Veicolo-000").Selezionato = true;
            var filtroVeicoloTempo = sw.Elapsed;
            _output.WriteLine($"BENCHMARK|nc_count={ncCount}|filtro_veicolo_ms={filtroVeicoloTempo.TotalMilliseconds:F0}|totale_righe={vm.TotaleRighe}");
            Assert.True(filtroVeicoloTempo < TimeSpan.FromMilliseconds(700), $"Filtro Veicolo (percorso lento) troppo lento: {filtroVeicoloTempo.TotalMilliseconds}ms");

            // 4. Cambio pagina all'interno del risultato filtrato.
            vm.FiltroVeicolo.DeselezionaTutto();
            sw.Restart();
            vm.PaginaRigheSuccessivaCommand.Execute(null);
            var paginaTempo = sw.Elapsed;
            _output.WriteLine($"BENCHMARK|nc_count={ncCount}|pagina_successiva_ms={paginaTempo.TotalMilliseconds:F0}");
            Assert.True(paginaTempo < TimeSpan.FromMilliseconds(700), $"Cambio pagina troppo lento: {paginaTempo.TotalMilliseconds}ms");

            var ramMiB = Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0;
            _output.WriteLine($"BENCHMARK|nc_count={ncCount}|ram_mib={ramMiB:F1}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CreaTestRealistici(DataPaths paths, int count)
    {
        for (var indice = 0; indice < count; indice++)
        {
            var componente = indice % 50;
            var dir = paths.TestCaseDir($"Componente-{componente:D3}");
            Directory.CreateDirectory(dir);
            var id = $"TC-{indice:D6}";
            var json = $$"""
            {"SchemaVersion":4,"Uid":"{{Guid.NewGuid()}}","RevisioneDati":1,"Id":"{{id}}","DescrizioneBreve":"Diagnostica scenario {{indice % 137}}","Descrizione":"Verifica realistica","DataCreazione":"2026-08-01T08:00:00","UltimaModifica":"2026-08-01T12:00:00","ModificatoDa":"Operatore-01","Componente":["Componente-{{componente:D3}}"],"Veicolo":"Veicolo-{{indice % 50:D3}}","VeicoliEffettivi":["Veicolo-{{indice % 50:D3}}"],"StatoLavoro":"Concluso","Funzionalita":[],"RequisitoId":[],"CampiCustom":{},"Esecuzioni":[],"Allegati":[],"Changelog":[]}
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
            // 1 NC su 10 e' collegata a un Test reale (il resto simula segnalazioni di rete/anomalie senza
            // Test ancora eseguito): con 100k NC risolvere il TestCase per ognuna resterebbe comunque il costo
            // dominante del percorso filtrato, indipendentemente da quante abbiano davvero un collegamento.
            var testCaseId = indice % 10 == 0 ? $"TC-{(indice % testCaseCount):D6}" : "";
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
