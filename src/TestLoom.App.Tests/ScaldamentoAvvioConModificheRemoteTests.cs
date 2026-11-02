using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using static TestLoom.App.Tests.NavigazioneTestHelper;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Piano 6.4.0 — scaldamento all'avvio (MainViewModel.PreparaPaginaAsync, chiamato da App.axaml.cs prima di
/// mostrare la MainWindow) sotto scrittura concorrente da una SECONDA postazione. <see cref="MainViewModelPreparaPaginaTests"/>
/// copre già Test/WU Status/manutenzione+watcher reali; questo file aggiunge due angoli non ancora coperti:
/// (1) la seconda postazione scrive anche un CATALOGO MASTER (non solo Test) mentre lo scaldamento di "report"
/// è ancora in corso, con Crea() artificialmente rallentato tramite lo stesso seam di soli test già usato in
/// MainViewModelPreparaPaginaTests (MainViewModel.SostituzioneCreaPaginaPerTest, mai attivo in produzione);
/// (2) più pagine vengono scaldate IN PARALLELO (Task.WhenAll, come farebbe davvero App.axaml.cs per
/// Test/Report/WU Status in sequenza-non-bloccante) mentre la seconda postazione scrive.
/// </summary>
public sealed class ScaldamentoAvvioConModificheRemoteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_ScaldamentoRemote_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private AppState CreateMasterState(string dataRoot, string configPath)
    {
        Directory.CreateDirectory(dataRoot);
        var configService = new AppConfigService(configPath);
        configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Master Test" });
        var state = new AppState(configService, runBackgroundMaintenance: false);
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(TestOperatori.Sigilla(new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" }));
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Master Test", "1234");
        return state;
    }

    [Fact]
    public async Task ScaldamentoReportRallentato_SecondaPostazioneScriveCatalogoMasterETest_NonLanciaEConvergeAllaPrimaVisita()
    {
        var dataRoot = Path.Combine(_root, "data");
        var stato1 = CreateMasterState(dataRoot, Path.Combine(_root, "local_A", "config.json"));
        using var _dispose1 = stato1;
        var main = new MainViewModel(stato1);

        var timeoutOriginale = MainViewModel.TimeoutScaldamentoPagina;
        var gate = new ManualResetEventSlim(false);
        MainViewModel.TimeoutScaldamentoPagina = TimeSpan.FromSeconds(10); // ampio: qui vogliamo che TERMINI, non che scada.
        MainViewModel.SostituzioneCreaPaginaPerTestNavId = "report";
        MainViewModel.SostituzioneCreaPaginaPerTest = () =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return new ReportViewModel(stato1);
        };

        try
        {
            var scaldamentoTask = main.PreparaPaginaAsync("report");

            // Seconda postazione: scrive un catalogo MASTER (Componente) e un Test collegato mentre lo scaldamento di
            // "report" è ancora bloccato sul gate — esercita esplicitamente il percorso "un'altra postazione
            // scrive un CATALOGO, non solo un Test, durante lo scaldamento".
            var stato2 = new AppState(new AppConfigService(Path.Combine(_root, "local_B", "config.json")), runBackgroundMaintenance: false);
            stato2.InizializzaCartella(dataRoot);
            var ecuCatalogo = stato2.Master.Componente.Load();
            ecuCatalogo.Add(new Componente { Nome = "Componente-DA-SECONDA-POSTAZIONE", ModificatoDa = "Seconda Postazione" });
            stato2.Master.Componente.Save(ecuCatalogo);
            stato2.TestCases.Save(new TestCase
            {
                Id = "T-SCALDAMENTO-REPORT",
                Componente = "Componente-DA-SECONDA-POSTAZIONE",
                ModificatoDa = "Seconda Postazione",
                DataCreazione = DateTime.Now,
                UltimaModifica = DateTime.Now,
            }, isNew: true);
            stato2.Dispose();

            gate.Set(); // sblocca lo scaldamento SOLO dopo che la scrittura remota è già avvenuta.

            var ex = await Record.ExceptionAsync(async () => await scaldamentoTask);
            Assert.Null(ex); // nessuna eccezione non gestita dallo scaldamento nonostante la scrittura concorrente.

            var scaldata = await scaldamentoTask;
            Assert.NotNull(scaldata);

            stato1.RicaricaTutto();
            stato1.TestCases.RefreshIndex();
            await NavigaAsync(main, "report");

            Assert.Same(scaldata, main.CurrentPage);
            var report = Assert.IsType<ReportViewModel>(main.CurrentPage);
            Assert.NotEmpty(report.Anteprima); // almeno il Test della seconda postazione è visibile.
        }
        finally
        {
            MainViewModel.TimeoutScaldamentoPagina = timeoutOriginale;
            MainViewModel.SostituzioneCreaPaginaPerTest = null;
            MainViewModel.SostituzioneCreaPaginaPerTestNavId = null;
            gate.Set();
        }
    }

    [Fact]
    public async Task ScaldamentoDiPiuPagineInParallelo_ConScritturaConcorrenteDiUnaSecondaPostazione_NessunaEccezioneEDatiCoerentiOvunque()
    {
        var dataRoot = Path.Combine(_root, "data_parallelo");
        var stato1 = CreateMasterState(dataRoot, Path.Combine(_root, "local_parallelo_A", "config.json"));
        using var _dispose1 = stato1;
        var main = new MainViewModel(stato1);

        // App.axaml.cs scalda più pagine senza attendere ciascuna singolarmente prima di passare alla
        // successiva: qui le lanciamo davvero in parallelo (Task.WhenAll), come farebbe RunStartupAsync.
        var scaldamentoTest = main.PreparaPaginaAsync("test");
        var scaldamentoReport = main.PreparaPaginaAsync("report");

        var stato2 = new AppState(new AppConfigService(Path.Combine(_root, "local_parallelo_B", "config.json")), runBackgroundMaintenance: false);
        stato2.InizializzaCartella(dataRoot);
        for (var i = 0; i < 5; i++)
        {
            stato2.TestCases.Save(new TestCase
            {
                Id = $"T-PARALLELO-{i:D2}",
                Componente = "BCM",
                ModificatoDa = "Seconda Postazione",
                DataCreazione = DateTime.Now,
                UltimaModifica = DateTime.Now,
            }, isNew: true);
        }
        stato2.Dispose();

        var ex = await Record.ExceptionAsync(async () => await Task.WhenAll(scaldamentoTest, scaldamentoReport));
        Assert.Null(ex);

        Assert.NotNull(await scaldamentoTest);
        Assert.NotNull(await scaldamentoReport);

        stato1.TestCases.RefreshIndex();
        await NavigaAsync(main, "test");
        var lista = Assert.IsType<TestCaseListViewModel>(main.CurrentPage);
        Assert.True(lista.Righe.Count >= 5, $"Attesi almeno 5 Test della seconda postazione, trovati {lista.Righe.Count}.");
    }
}
