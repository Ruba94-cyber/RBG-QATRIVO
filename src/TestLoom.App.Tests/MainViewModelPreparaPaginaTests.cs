using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using static TestLoom.App.Tests.NavigazioneTestHelper;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Copertura di MainViewModel.PreparaPaginaAsync (piano 6.4.0, "scaldamento all'avvio"): App.axaml.cs lo chiama
/// per WU Status/Report/Test dopo aver costruito MainViewModel e PRIMA di mostrare la MainWindow, così la prima
/// visita reale dell'operatore trova la pagina già in cache invece di pagare il ricalcolo completo a finestra
/// visibile (misurato fino a ~3s su WU Status a 100k voci). Questi test verificano che lo scaldamento usi
/// esattamente lo stesso percorso di una prima navigazione (nessuna cache duplicata) e che l'invalidazione dei
/// dati resti quella di sempre: una modifica arrivata fra lo scaldamento e la vera prima visita — anche da una
/// seconda postazione sulla stessa cartella condivisa — deve comunque comparire, perché la prima visita reale
/// passa dal ramo "cache hit" di OttieniPagina, che richiama IPaginaAggiornabile.Aggiorna() come farebbe oggi
/// una qualunque rivisita.
/// </summary>
public sealed class MainViewModelPreparaPaginaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PreparaPagina_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private AppState CreateMasterState()
    {
        var dataRoot = Path.Combine(_root, "data");
        var configPath = Path.Combine(_root, "local", "config.json");
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

    [Theory]
    [InlineData("test")]
    [InlineData("report")]
    public async Task PreparaPaginaAsync_LaPrimaVisitaRealeRiusaLIstanzaScaldata(string navId)
    {
        var state = CreateMasterState();
        using var _dispose = state;
        var main = new MainViewModel(state);

        var scaldata = await main.PreparaPaginaAsync(navId);
        Assert.NotNull(scaldata);

        // La prima visita "reale" dell'operatore non deve costruire una seconda istanza (Crea() richiamato una
        // sola volta): OttieniPagina deve trovarla già in cache e limitarsi a richiamare Aggiorna() su di essa.
        await NavigaAsync(main, navId);

        Assert.Same(scaldata, main.CurrentPage);
    }

    [Fact]
    public async Task PreparaPaginaAsync_NavIdInesistente_NonLanciaEDaNullo()
    {
        var state = CreateMasterState();
        using var _dispose = state;
        var main = new MainViewModel(state);

        var risultato = await main.PreparaPaginaAsync("pagina-che-non-esiste");

        Assert.Null(risultato);
    }

    [Fact]
    public async Task PreparaPaginaAsync_ChiamataDuePolte_NonRicostruisceLaPaginaGiaCache()
    {
        var state = CreateMasterState();
        using var _dispose = state;
        var main = new MainViewModel(state);

        var prima = await main.PreparaPaginaAsync("test");
        var seconda = await main.PreparaPaginaAsync("test");

        Assert.Same(prima, seconda);
    }

    /// <summary>Requisito esplicito del piano 6.4.0: "una modifica ai dati fra lo scaldamento e la visita deve
    /// essere rilevata come oggi". Qui un Test viene salvato DOPO lo scaldamento di "Test" e PRIMA della prima
    /// visita reale: deve comparire nella lista, esattamente come nelle rivisite di pagine già in cache
    /// (MainViewModelPageCachingTests.CambioScheda_RivisitaTest_RifletteUnTestAppenaCreatoAltrove).</summary>
    [Fact]
    public async Task PreparaPaginaAsync_TestModificatoDopoLoScaldamento_VisibileAllaPrimaVisitaReale()
    {
        var state = CreateMasterState();
        using var _dispose = state;
        var main = new MainViewModel(state);

        var scaldata = await main.PreparaPaginaAsync("test");
        var lista = Assert.IsType<TestCaseListViewModel>(scaldata);
        Assert.Empty(lista.Righe);

        state.TestCases.Save(new TestCase { Id = "TC-00001", Componente = "Componente-000", Veicolo = "Veicolo-000" }, isNew: true);
        state.RicaricaTutto();

        await NavigaAsync(main, "test");

        Assert.Same(lista, main.CurrentPage);
        Assert.Single(lista.Righe);
    }

    /// <summary>Stessa garanzia per Report: l'anteprima deve riflettere un Test creato dopo lo scaldamento.</summary>
    [Fact]
    public async Task PreparaPaginaAsync_ReportModificatoDopoLoScaldamento_VisibileAllaPrimaVisitaReale()
    {
        var state = CreateMasterState();
        using var _dispose = state;
        var main = new MainViewModel(state);

        var scaldata = await main.PreparaPaginaAsync("report");
        var report = Assert.IsType<ReportViewModel>(scaldata);
        await report.AttendiAggiornamentoAsync();
        Assert.Empty(report.Anteprima);

        state.TestCases.Save(new TestCase { Id = "TC-00001", Componente = "Componente-000", Veicolo = "Veicolo-000" }, isNew: true);
        state.RicaricaTutto();

        await NavigaAsync(main, "report");
        await report.AttendiAggiornamentoAsync();

        Assert.Same(report, main.CurrentPage);
        Assert.Single(report.Anteprima);
    }

    /// <summary>Revisione indipendente (piano 6.4.0): senza un budget, un NavItem.Crea() bloccato (deadlock,
    /// I/O bloccato su una share degradata) farebbe restare PreparaPaginaAsync in attesa PER SEMPRE — e con lui
    /// App.axaml.cs.RunStartupAsync, che non mostrerebbe mai la MainWindow. Il Crea() "bloccato" qui è simulato
    /// con il seam di soli test MainViewModel.SostituzioneCreaPaginaPerTest (mai usato in produzione, dove
    /// resta null): nessun deadlock reale, nessuna modifica al comportamento di produzione.
    ///
    /// Verifica anche il requisito più delicato: quando il lavoro abbandonato finirà DAVVERO (qui, quando il
    /// test decide di sbloccarlo) DOPO che l'utente ha già navigato per conto suo su quella stessa pagina, il
    /// risultato tardivo NON deve sovrascrivere l'istanza già in CurrentPage — altrimenti l'operatore si
    /// ritroverebbe con due istanze vive dello stesso ViewModel.</summary>
    [Fact]
    public async Task PreparaPaginaAsync_CreaCheNonTerminaEntroIlBudget_ProseguelSenzaAttendereENonSovrascriveLaPaginaGiaNavigata()
    {
        var state = CreateMasterState();
        using var _dispose = state;
        var main = new MainViewModel(state);

        var timeoutOriginale = MainViewModel.TimeoutScaldamentoPagina;
        var gate = new ManualResetEventSlim(false);
        MainViewModel.TimeoutScaldamentoPagina = TimeSpan.FromMilliseconds(200);
        MainViewModel.SostituzioneCreaPaginaPerTestNavId = "test";
        MainViewModel.SostituzioneCreaPaginaPerTest = () =>
        {
            // Si sblocca SOLO quando il test lo decide (dopo aver già verificato scadenza + navigazione reale):
            // simula un Crea() bloccato senza dipendere da timing reali di I/O.
            gate.Wait(TimeSpan.FromSeconds(10));
            return new TestCaseListViewModel(state, _ => { });
        };
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var risultato = await main.PreparaPaginaAsync("test");
            sw.Stop();

            Assert.Null(risultato);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
                $"PreparaPaginaAsync non ha rispettato il budget di scadenza (durata effettiva {sw.Elapsed}).");

            // La vera prima visita dell'utente, mentre il lavoro di scaldamento abbandonato è ancora bloccato:
            // deve costruire una pagina VERA (NavItem.Crea reale, non intercettato dal seam) esattamente come
            // se lo scaldamento non fosse mai stato tentato.
            await NavigaAsync(main, "test");
            var listaReale = Assert.IsType<TestCaseListViewModel>(main.CurrentPage);

            // Solo ORA si sblocca il lavoro tardivo: il suo risultato non deve mai raggiungere _pagineCache
            // sovrascrivendo la pagina che l'utente sta già guardando.
            gate.Set();
            await Task.Delay(500); // margine perché la continuazione tardiva abbia modo di eseguire

            Assert.Same(listaReale, main.CurrentPage);
        }
        finally
        {
            MainViewModel.TimeoutScaldamentoPagina = timeoutOriginale;
            MainViewModel.SostituzioneCreaPaginaPerTest = null;
            MainViewModel.SostituzioneCreaPaginaPerTestNavId = null;
            gate.Set(); // non lasciare un thread di pool bloccato per sempre se qualcosa sopra è fallito
        }
    }

    [Fact]
    public async Task PreparaPaginaAsync_CompletamentoDopoLogout_NonRipopolaLaCacheDellaSessioneSuccessiva()
    {
        var state = CreateMasterState();
        using var main = new MainViewModel(state);
        var timeoutOriginale = MainViewModel.TimeoutScaldamentoPagina;
        using var gate = new ManualResetEventSlim(false);
        var vmCreato = new TaskCompletionSource<ViewModelDiProva>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainViewModel.TimeoutScaldamentoPagina = TimeSpan.FromMilliseconds(100);
        MainViewModel.SostituzioneCreaPaginaPerTestNavId = "guasto";
        MainViewModel.SostituzioneCreaPaginaPerTest = () =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            var vm = new ViewModelDiProva();
            vmCreato.TrySetResult(vm);
            return vm;
        };

        try
        {
            Assert.Null(await main.PreparaPaginaAsync("guasto"));
            await main.LogoutCommand.ExecuteAsync(null);

            // Il risultato del vecchio scaldamento arriva solo dopo che il logout ha svuotato la cache.
            gate.Set();
            var vmTardivo = await vmCreato.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await vmTardivo.Disposto.WaitAsync(TimeSpan.FromSeconds(3));

            MainViewModel.SostituzioneCreaPaginaPerTest = null;
            MainViewModel.SostituzioneCreaPaginaPerTestNavId = null;
            MainViewModel.TimeoutScaldamentoPagina = timeoutOriginale;
            Assert.True(state.ImpostaOperatore("Master Test", "1234"));

            await NavigaAsync(main, "guasto");

            Assert.IsType<DtcRegistroViewModel>(main.CurrentPage);
        }
        finally
        {
            MainViewModel.TimeoutScaldamentoPagina = timeoutOriginale;
            MainViewModel.SostituzioneCreaPaginaPerTest = null;
            MainViewModel.SostituzioneCreaPaginaPerTestNavId = null;
            gate.Set();
        }
    }

    private sealed class ViewModelDiProva : ViewModelBase, IDisposable
    {
        private readonly TaskCompletionSource _disposto = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Disposto => _disposto.Task;
        public void Dispose() => _disposto.TrySetResult();
    }

    /// <summary>Revisione indipendente (piano 6.4.0, punto 2): lo scaldamento deve restare corretto e non
    /// lanciare eccezioni anche quando gira mentre la manutenzione di sfondo REALE (runBackgroundMaintenance:
    /// true, non il solito "false" degli altri test di questo file — qui avvia davvero
    /// AppState.EseguiManutenzioneStorage su un proprio Task.Run) e il watcher reale della prima postazione sono
    /// entrambi attivi, e una seconda postazione scrive concorrentemente sulla stessa cartella condivisa. Alla
    /// prima visita reale, dopo l'aggiornamento (watcher o refresh esplicito, stesso schema già usato per la
    /// "seconda postazione" negli altri test), i dati devono essere quelli aggiornati.</summary>
    [Fact]
    public async Task Scaldamento_ConManutenzioneEWatcherRealiAttivi_NonLanciaEDatiAggiornatiAllaPrimaVisita()
    {
        var dataRoot = Path.Combine(_root, "data_concorrenza");
        var configPath = Path.Combine(_root, "local_concorrenza", "config.json");
        var paths = new DataPaths(dataRoot);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);

        var configService = new AppConfigService(configPath);
        configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Master Test" });

        using (var seed = new AppState(configService, runBackgroundMaintenance: false))
        {
            var ops = seed.Master.Operatori.Load().ToList();
            ops.Add(TestOperatori.Sigilla(new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" }));
            seed.Master.Operatori.Save(ops);
        }
        SqliteConnection.ClearAllPools();

        // Prima postazione: manutenzione di sfondo E watcher REALI (non simulati/disattivati).
        var stato1 = new AppState(configService, runBackgroundMaintenance: true);
        using var _dispose1 = stato1;
        Assert.True(stato1.ImpostaOperatore("Master Test", "1234"));
        var main = new MainViewModel(stato1);

        // Scaldamento avviato SENZA attenderlo subito: resta in corso mentre la manutenzione di sfondo (avviata
        // dal costruttore di stato1 sopra) e la scrittura della seconda postazione sotto girano concorrentemente.
        var scaldamentoTask = main.PreparaPaginaAsync("test");

        var configService2 = new AppConfigService(Path.Combine(_root, "local_concorrenza_B", "config.json"));
        var stato2 = new AppState(configService2, runBackgroundMaintenance: false);
        stato2.InizializzaCartella(dataRoot);
        stato2.TestCases.Save(new TestCase { Id = "T-CONCORRENZA", Componente = "BCM", ModificatoDa = "Seconda Postazione" }, isNew: true);
        stato2.Dispose();

        // Nessuna eccezione deve emergere dallo scaldamento nonostante la concorrenza sopra.
        var scaldata = await scaldamentoTask;
        var lista = Assert.IsType<TestCaseListViewModel>(scaldata);

        // Meccanismo reale (watcher o, in mancanza, lo stesso refresh esplicito che farebbe comunque il timer
        // periodico): la prima postazione deve riallineare l'indice dell'archivio Test.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (stato1.TestCases.Count() == 0 && sw.ElapsedMilliseconds < 5000)
        {
            stato1.TestCases.RefreshIndex();
            await Task.Delay(50);
        }

        // Prima visita reale: la pagina già scaldata deve riflettere il Test della seconda postazione.
        await NavigaAsync(main, "test");
        Assert.Same(lista, main.CurrentPage);
        Assert.Single(lista.Righe);
    }
}
