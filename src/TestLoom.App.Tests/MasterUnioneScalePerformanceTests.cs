using System.Diagnostics;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Benchmark a scala per Unisci sui cataloghi master (spec 2026-09-23, spec_unisci_master.md, punto 8:
/// "deve restare usabile su archivi da 100k test... misurare e riportare i tempi"), stesso schema di
/// WuStatusScalePerformanceTests: 100k TestCase reali su disco, un catalogo componente a scala realistica (non 100k
/// voci, mai visto altrove nell'app), e la sola sottoparte referenziata dal componente unita viene riscritta.</summary>
[Collection("Scale performance")]
public class MasterUnioneScalePerformanceTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public MasterUnioneScalePerformanceTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Scale")]
    public void Unisci_100kTestCase_EcuCoinvoltaSuUnaFrazioneRealistica_TempoMisurato()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_MasterUnioneScale", Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(root, "local", "config.json");
        var dataRoot = Path.Combine(root, "data");
        try
        {
            var paths = new DataPaths(dataRoot);
            paths.EnsureStructure();

            const int testCount = 100_000;
            const int numeroEcu = 100; // ogni componente coinvolge quindi ~1000 test, scala realistica di un impianto grande.

            CreaTestCases(paths, testCount, numeroEcu);

            var configService = new AppConfigService(configPath);
            configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Master Prova" });

            using (var stateSeed = new AppState(configService, runBackgroundMaintenance: false))
            {
                Assert.Equal(testCount, stateSeed.TestCases.Count());
                var operatore = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
                stateSeed.Master.Operatori.Save(new List<Operatore> { operatore });
                stateSeed.Master.Componente.Save(Enumerable.Range(0, numeroEcu).Select(i => new Componente { Nome = $"Componente-{i:D3}" }).ToList());
            }
            SqliteConnection.ClearAllPools();

            using var state = new AppState(configService, runBackgroundMaintenance: false);
            Assert.Equal(testCount, state.TestCases.Count());
            Assert.True(state.ImpostaOperatore("Master Prova", "1234"));

            var avvisi = new List<string>();
            var sw = Stopwatch.StartNew();
            MasterUnioneService.Unisci(state, state.Master.Componente, "Componente", "Componente-000", "Componente-001", e => e.Nome, avvisi);
            var tempo = sw.Elapsed;

            var attesi = testCount / numeroEcu; // i test che referenziavano componente-000
            _output.WriteLine($"BENCHMARK|test_totali={testCount}|ecu_catalogo={numeroEcu}|test_aggiornati~={attesi}|unisci_ms={tempo.TotalMilliseconds:F0}|avvisi={avvisi.Count}");

            // NOTA PRESTAZIONI (misurata, tempi indicativi su macchina condivisa/con carico variabile):
            // - primo giro (solo pre-filtro economico, niente clonazione JSON dei record non coinvolti): ~450-720s;
            // - dopo aver corretto due costi per-chiamata REALI trovati in TestCaseRepository.SaveCore
            //   (RicostruisciLookup: rebuild O(n log n) — con tanto di OrderBy — di TUTTI i lookup Id/Uid ad OGNI
            //   singolo Save, sostituito da un aggiornamento incrementale O(1) con fallback al rebuild completo
            //   solo sul raro caso di collisione Id/Uid reale; NuovoPathFor: ricaricava da zero l'intero catalogo
            //   Veicoli via `new MasterDataRepository<Veicolo>(...).Load()` ad ogni chiamata, ora cache con
            //   invalidazione su mtime del file) — misurato a ~150-160s, un fattore ~3-4x su questa macchina.
            // Non e' l'ordine di grandezza sperato: un salvataggio bulk/batch che raggruppi le scritture Test/NC/
            // CR di un'intera operazione Unisci/Rinomina/Dividi in un solo passaggio journal/indice resta il
            // follow-up raccomandato per un guadagno strutturale ulteriore (vedi report).
            Assert.True(tempo < TimeSpan.FromMinutes(10), $"Unisci fuori scala anche con soglia larga su 100k test: {tempo.TotalMilliseconds:F0}ms");

            Assert.Equal(numeroEcu - 1, state.Master.Componente.Load().Count);
            Assert.DoesNotContain(state.TestCases.GetAll(), tc => tc.Componente.Contains("Componente-000", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CreaTestCases(DataPaths paths, int count, int numeroEcu)
    {
        for (var indice = 0; indice < count; indice++)
        {
            var componente = $"Componente-{indice % numeroEcu:D3}";
            var dir = paths.TestCaseDir(componente);
            Directory.CreateDirectory(dir);
            var id = $"TC-{indice:D6}";
            var testCase = new TestCase
            {
                Uid = Guid.NewGuid(),
                RevisioneDati = 1,
                Id = id,
                DescrizioneBreve = $"Test {indice}",
                Descrizione = "Verifica",
                ModificatoDa = "Operatore-01",
                Componente = { componente },
                Veicolo = $"Veicolo-{indice % 50:D3}",
                Veicoli = { $"Veicolo-{indice % 50:D3}" },
                StatoLavoro = StatoLavoroTest.Concluso,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(testCase, JsonIo.Options);
            File.WriteAllBytes(Path.Combine(dir, $"testcase_{id}.json"), bytes);
        }
    }
}
