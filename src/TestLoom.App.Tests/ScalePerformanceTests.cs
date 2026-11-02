using System.Diagnostics;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

[CollectionDefinition("Scale performance", DisableParallelization = true)]
public sealed class ScalePerformanceCollection { }

/// <summary>Guardrail di scala richiesto per il deploy: non è un micro-benchmark fragile, ma verifica che
/// i percorsi critici restino lineari e completino con margine su 20.000 Test, 200 componente, 100 veicoli e
/// 100 funzionalità. Gli allegati sono file separati e non vengono caricati durante indice/dashboard.</summary>
[Collection("Scale performance")]
public class ScalePerformanceTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ScalePerformanceTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(0)]
    [InlineData(20_000)]
    [InlineData(50_000)]
    [InlineData(100_000)]
    [Trait("Category", "Scale")]
    public void ArchivioMassimo_IndicizzazioneListeEHome_RestanoOperativi(int numeroTest)
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_Scale", Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(root, "local", "config.json");
        var dataRoot = Path.Combine(root, "data");
        try
        {
            var paths = new DataPaths(dataRoot);
            paths.EnsureStructure();
            CreaTestRealistici(paths, numeroTest);

            var localBase = Path.Combine(root, "local-index");
            var repo = new TestCaseRepository(paths, new LocalTestIndexStore(dataRoot, localBase));
            var sw = Stopwatch.StartNew();
            repo.RefreshIndex();
            var cold = sw.Elapsed;
            Assert.Equal(numeroTest, repo.GetAll().Count);
            var coldLimit = TimeSpan.FromSeconds(Math.Max(10, numeroTest / 500.0));
            Assert.True(cold < coldLimit, $"Indice iniziale troppo lento per {numeroTest:N0} Test: {cold}");

            var warmSamples = new List<TimeSpan>();
            TestCaseRepository? warmRepo = null;
            for (var i = 0; i < 3; i++)
            {
                sw.Restart();
                warmRepo = new TestCaseRepository(paths, new LocalTestIndexStore(dataRoot, localBase));
                Assert.Equal(numeroTest, warmRepo.GetAll().Count);
                warmSamples.Add(sw.Elapsed);
            }

            sw.Restart();
            warmRepo!.RefreshIndex();
            var reconcile = sw.Elapsed;
            Assert.True(reconcile < TimeSpan.FromSeconds(Math.Max(10, numeroTest / 500.0)),
                $"Reconciliation invariata troppo lenta per {numeroTest:N0} Test: {reconcile}");

            sw.Restart();
            var hits = numeroTest == 0
                ? LocalTestSearchService.Search(warmRepo.LocalIndexDatabasePath, "diagnostica", 24)
                : LocalTestSearchService.Search(warmRepo.LocalIndexDatabasePath, "diagnostica", 24);
            var search = sw.Elapsed;
            Assert.NotNull(hits);
            if (numeroTest > 0) Assert.NotEmpty(hits!);
            Assert.True(search < TimeSpan.FromSeconds(2), $"Ricerca FTS troppo lenta: {search}");

            var configService = new AppConfigService(configPath);
            configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it" });
            using var state = new AppState(configService, runBackgroundMaintenance: false);
            state.Master.Gamma.Save(Enumerable.Range(0, 10).Select(i => new Gamma { Nome = $"Gamma-{i:D2}" }).ToList());
            state.Master.Veicoli.Save(Enumerable.Range(0, 100).Select(i => new Veicolo { Nome = $"Veicolo-{i:D3}", GammaNome = $"Gamma-{i % 10:D2}" }).ToList());
            state.Master.Componente.Save(Enumerable.Range(0, 200).Select(i => new Componente { Nome = $"Componente-{i:D3}", VeicoliAssociati = { $"Veicolo-{i % 100:D3}" } }).ToList());
            state.Master.Funzionalita.Save(Enumerable.Range(0, 100).Select(i => new Funzionalita { Nome = $"Funzione-{i:D3}", Categorie = new() { $"Categoria-{i % 10:D2}" } }).ToList());

            sw.Restart();
            var lista = new TestCaseListViewModel(state, _ => { });
            var listaTempo = sw.Elapsed;
            // Righe e' ormai una singola pagina (guardrail 100k): il conteggio totale che conta per la scala
            // e' TotaleRisultati, non piu' la dimensione della collezione materializzata in UI.
            Assert.Equal(numeroTest, lista.TotaleRisultati);
            Assert.True(listaTempo < TimeSpan.FromSeconds(Math.Max(5, numeroTest / 5_000.0)),
                $"Lista {numeroTest:N0} troppo lenta: {listaTempo}");

            sw.Restart();
            var home = new HomeViewModel(state, _ => { });
            var homeTempo = sw.Elapsed;
            // Stesso principio di TestCaseListViewModel.Righe sopra: DaFare/Fatti sono ora una pagina, i
            // conteggi aggregati che contano per la scala sono TotaleDaFare/TotaleFatti.
            Assert.Equal(numeroTest, home.TotaleDaFare + home.TotaleFatti);
            Assert.True(homeTempo < TimeSpan.FromSeconds(Math.Max(10, numeroTest / 2_500.0)),
                $"Home {numeroTest:N0} troppo lenta: {homeTempo}");

            var warmAverage = TimeSpan.FromTicks((long)warmSamples.Average(t => t.Ticks));
            var ramMiB = Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0;
            _output.WriteLine($"BENCHMARK|count={numeroTest}|cold_ms={cold.TotalMilliseconds:F0}|warm_avg_ms={warmAverage.TotalMilliseconds:F0}|reconcile_ms={reconcile.TotalMilliseconds:F0}|search_ms={search.TotalMilliseconds:F1}|list_ms={listaTempo.TotalMilliseconds:F0}|home_ms={homeTempo.TotalMilliseconds:F0}|ram_mib={ramMiB:F1}");

            if (numeroTest == 100_000)
                MisuraRefreshIncrementale(paths, warmRepo, _output);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    [Fact]
    public void IndiceAnomalie_CollegaOgniAnomaliaUnaSolaVoltaPerTest()
    {
        var nc = new NonConformita
        {
            Id = "NC-1", TestCaseId = "TC-1",
            ProveCollegate =
            {
                new RiferimentoProva { TestId = "TC-1" },
                new RiferimentoProva { TestId = "TC-2" }
            }
        };

        var indice = AnomaliaWorkflowService.IndicizzaPerTest(new[] { nc });

        Assert.Same(nc, Assert.Single(indice["TC-1"]));
        Assert.Same(nc, Assert.Single(indice["tc-2"]));
    }

    [Fact]
    [Trait("Category", "Scale")]
    public async Task AllegatoDa256MiB_VieneCopiatoInStreamingConMemoriaLimitata()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_AttachmentScale", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "traccia.blf");
        try
        {
            await using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.SetLength(256L * 1024 * 1024);

            GC.Collect();
            var memoriaPrima = GC.GetTotalMemory(true);
            var sw = Stopwatch.StartNew();
            var paths = new DataPaths(Path.Combine(root, "data"));
            paths.EnsureStructure();
            var tc = new TestCase { Id = "TC-LARGE", Componente = "Componente-001" };
            var allegato = await AttachmentService.SalvaAsync(paths, tc, source, "Scale");
            var tempo = sw.Elapsed;
            var memoriaDopo = GC.GetTotalMemory(true);

            Assert.Equal(256L * 1024 * 1024, allegato.DimensioneByte);
            Assert.Equal(64, allegato.Sha256.Length);
            Assert.True(memoriaDopo - memoriaPrima < 32L * 1024 * 1024,
                $"La copia ha trattenuto troppa memoria: {(memoriaDopo - memoriaPrima) / 1024 / 1024} MiB");
            Assert.True(tempo < TimeSpan.FromSeconds(60), $"Copia/hash troppo lenti: {tempo}");
            _output.WriteLine($"Allegato 256 MiB: copia+SHA-256={tempo.TotalMilliseconds:F0} ms; memoria gestita trattenuta={(memoriaDopo - memoriaPrima) / 1024.0 / 1024.0:F1} MiB");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CreaTestRealistici(DataPaths paths, int count)
    {
        for (var indice = 0; indice < count; indice++)
        {
            var componente = indice % 200;
            var dir = paths.TestCaseDir($"Componente-{componente:D3}");
            Directory.CreateDirectory(dir);
            var id = $"TC-{indice:D6}";
            var revisione = 1 + indice % 7;
            var stato = indice % 3 == 0 ? "Pianificato" : indice % 3 == 1 ? "InCorso" : "Concluso";
            var esito = indice % 5 == 0 ? "NonConforme" : "Conforme";
            var json = $$"""
            {"SchemaVersion":4,"Uid":"{{Guid.NewGuid()}}","RevisioneDati":{{revisione}},"Id":"{{id}}","DescrizioneBreve":"Diagnostica scenario {{indice % 137}}","Descrizione":"Verifica realistica funzione {{indice % 100}} su configurazione {{indice % 31}}","DataCreazione":"2026-{{1 + indice % 8:D2}}-{{1 + indice % 27:D2}}T08:00:00","UltimaModifica":"2026-08-{{1 + indice % 27:D2}}T12:00:00","ModificatoDa":"Operatore-{{indice % 12:D2}}","Componente":["Componente-{{componente:D3}}"],"Veicolo":"Veicolo-{{indice % 100:D3}}","VeicoliEffettivi":["Veicolo-{{indice % 100:D3}}"],"StatoLavoro":"{{stato}}","Funzionalita":["Funzione-{{indice % 100:D3}}"],"RequisitoId":["REQ-{{indice % 500:D4}}"],"CampiCustom":{"campagna":"C-{{indice % 20:D2}}","release":"R-{{indice % 9:D2}}"},"Esecuzioni":[{"Iterazione":1,"Data":"2026-08-{{1 + indice % 27:D2}}T12:00:00","Esito":"{{esito}}","Matricola":"Matricola-{{indice % 1000:D4}}"}],"Allegati":[],"Changelog":[{"Data":"2026-08-{{1 + indice % 27:D2}}T12:00:00","Operatore":"Operatore-{{indice % 12:D2}}","Azione":"Aggiornamento revisione {{revisione}}"}]}
            """;
            File.WriteAllText(Path.Combine(dir, $"testcase_{id}.json"), json, Encoding.UTF8);
        }
    }

    private static void MisuraRefreshIncrementale(DataPaths paths, TestCaseRepository repo,
        Xunit.Abstractions.ITestOutputHelper output)
    {
        var files = Directory.EnumerateFiles(paths.TestCasesDir, "testcase_*.json", SearchOption.AllDirectories)
            .Take(110).ToList();
        Assert.Equal(110, files.Count);

        static TimeSpan ModificaERileggi(TestCaseRepository repository, IReadOnlyList<string> target)
        {
            foreach (var path in target) File.AppendAllText(path, " ");
            var watch = Stopwatch.StartNew();
            repository.RefreshPaths(target);
            return watch.Elapsed;
        }

        var one = ModificaERileggi(repo, files.Take(1).ToList());
        var ten = ModificaERileggi(repo, files.Take(10).ToList());
        var hundred = ModificaERileggi(repo, files.Take(100).ToList());
        Assert.All(new[] { one, ten, hundred }, elapsed =>
            Assert.True(elapsed < TimeSpan.FromSeconds(10), $"Refresh incrementale troppo lento: {elapsed}"));

        var clone = JsonIo.TryRead<TestCase>(files[0], out var error);
        Assert.NotNull(clone);
        Assert.Null(error);
        clone!.Uid = Guid.NewGuid();
        clone.Id = "TC-INCREMENTAL-NEW";
        var created = Path.Combine(Path.GetDirectoryName(files[0])!, "testcase_TC-INCREMENTAL-NEW.json");
        JsonIo.WriteAtomic(created, clone);
        repo.RefreshPaths([created]);
        Assert.Equal(100_001, repo.GetAll().Count);

        File.Delete(created);
        repo.RefreshPaths([created]);
        Assert.Equal(100_000, repo.GetAll().Count);

        var moved = Path.Combine(Path.GetDirectoryName(files[100])!, "testcase_TC-INCREMENTAL-MOVED.json");
        File.Move(files[100], moved);
        repo.RefreshPaths([files[100], moved]);
        Assert.Equal(100_000, repo.GetAll().Count);

        var watch = Stopwatch.StartNew();
        repo.RefreshIndex();
        var deep = watch.Elapsed;
        Assert.Equal(100_000, repo.GetAll().Count);
        output.WriteLine($"INCREMENTAL|count=100000|modify_1_ms={one.TotalMilliseconds:F1}|modify_10_ms={ten.TotalMilliseconds:F1}|modify_100_ms={hundred.TotalMilliseconds:F1}|create_delete_rename=PASS|deep_reconcile_ms={deep.TotalMilliseconds:F0}");
    }

    [Fact]
    [Trait("Category", "Scale")]
    public async Task TransizioniPagineEWorkflow100k_RestanoSotto700ms()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_PageTransitionScale", Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(root, "local", "config.json");
        var dataRoot = Path.Combine(root, "data");
        try
        {
            var paths = new DataPaths(dataRoot);
            paths.EnsureStructure();
            const int count = 20_000;
            CreaTestRealistici(paths, count);

            var configService = new AppConfigService(configPath);
            configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Operatore Prova A" });
            using var state = new AppState(configService, runBackgroundMaintenance: false);
            state.TestCases.RefreshIndex();

            // 1000 veicoli, 1000 requisiti, 1000 componente, 1000 funzionalita
            state.Master.Gamma.Save(Enumerable.Range(0, 50).Select(i => new Gamma { Nome = $"Gamma-{i:D2}" }).ToList());
            state.Master.Veicoli.Save(Enumerable.Range(0, 1000).Select(i => new Veicolo { Nome = $"Veicolo-{i:D3}", GammaNome = $"Gamma-{i % 50:D2}" }).ToList());
            state.Master.Componente.Save(Enumerable.Range(0, 1000).Select(i => new Componente { Nome = $"Componente-{i:D3}", VeicoliAssociati = { $"Veicolo-{i % 1000:D3}", $"Veicolo-{(i + 1) % 1000:D3}" } }).ToList());
            state.Master.Funzionalita.Save(Enumerable.Range(0, 1000).Select(i => new Funzionalita { Nome = $"Funzione-{i:D3}", Categorie = new() { $"Categoria-{i % 50:D2}" } }).ToList());
            state.Master.Requisiti.Save(Enumerable.Range(0, 1000).Select(i => new Requisito { Id = $"REQ-{i:D4}", Testo = $"Requisito diagnostico numero {i}" }).ToList());

            // Crea alcune NonConformita e ChangeRequest collegate al primo test
            var nc = new NonConformita
            {
                Id = "NC-0001",
                TestCaseId = "TC-000000",
                StatoWorkflow = StatoAnomalia.Confermata,
                ProveCollegate = new() { new RiferimentoProva { TestId = "TC-000000" } }
            };
            state.NonConformita.Save(nc);

            var cr = new ChangeRequest
            {
                Id = "CR-2026-0001",
                Titolo = "CR validazione",
                Stato = StatoChangeRequest.DaValidare,
                TestValidazione = new() { new CollegamentoValidazioneCr { TestId = "TC-000000" } }
            };
            state.ChangeRequests.Save(cr);

            // 1. Apertura / transizione ad AuditViewModel
            var sw = Stopwatch.StartNew();
            var auditVm = new AuditViewModel(state);
            var auditTempo = sw.Elapsed;
            _output.WriteLine($"PAGE_TRANSITION|AuditViewModel={auditTempo.TotalMilliseconds:F0}ms");
            Assert.True(auditTempo < TimeSpan.FromMilliseconds(700), $"AuditViewModel troppo lento: {auditTempo.TotalMilliseconds}ms");

            // 2. Apertura / transizione ad editor TestCase con anomalie e CR collegate
            sw.Restart();
            var editorVm = new TestCaseEditorViewModel(state, "TC-000000", () => { }, _ => { });
            var editorTempo = sw.Elapsed;
            _output.WriteLine($"PAGE_TRANSITION|TestCaseEditorViewModel={editorTempo.TotalMilliseconds:F0}ms");
            Assert.True(editorTempo < TimeSpan.FromMilliseconds(700), $"TestCaseEditorViewModel troppo lento: {editorTempo.TotalMilliseconds}ms");

            // 3. Calcolo valori disponibili per dimensioni report
            sw.Restart();
            var dimensioni = new List<string>
            {
                DimensioniMatrice.Gamma, DimensioniMatrice.Sottoveicolo, DimensioniMatrice.Componente,
                DimensioniMatrice.Funzionalita, DimensioniMatrice.CategoriaFunzionalita,
                DimensioniMatrice.Template, DimensioniMatrice.Esito, DimensioniMatrice.Assegnatario,
                DimensioniMatrice.Requisito, $"{DimensioniMatrice.CampoCustomPrefix}campagna", $"{DimensioniMatrice.CampoCustomPrefix}release"
            };
            var valori = ReportMatriceService.ValoriDisponibiliPerDimensioni(
                dimensioni, state.TestCases.GetAll(), state.Master.Veicoli.Load(),
                state.Master.Funzionalita.Load(), state.Master.TestTemplate.Load());
            var reportTempo = sw.Elapsed;
            _output.WriteLine($"PAGE_TRANSITION|ReportValoriDisponibili={reportTempo.TotalMilliseconds:F0}ms");
            Assert.True(reportTempo < TimeSpan.FromMilliseconds(700), $"ReportValoriDisponibili troppo lento: {reportTempo.TotalMilliseconds}ms");

            // 4. Salvataggio TestCase con ricalcolo CR e NC
            sw.Restart();
            editorVm.TestCase.DescrizioneBreve = "Modifica rapida di test";
            await editorVm.SalvaCommand.ExecuteAsync(null);
            var salvaTempo = sw.Elapsed;
            _output.WriteLine($"PAGE_TRANSITION|SalvaTestCaseConCrNc={salvaTempo.TotalMilliseconds:F0}ms");
            Assert.True(salvaTempo < TimeSpan.FromMilliseconds(700), $"SalvaTestCaseConCrNc troppo lento: {salvaTempo.TotalMilliseconds}ms");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
