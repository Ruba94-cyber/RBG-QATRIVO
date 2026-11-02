using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Campi di sistema usabili come filtro, dall'editor del catalogo fino a Home e Report (Work 1, segnalazione del Master:
/// "Tipo di connessione" spuntato come "usabile come filtro" non compariva mai nei filtri). La spunta si salva nel catalogo condiviso
/// e raggiunge sia le pagine nuove sia quelle gia' in cache; il filtro filtra i test che hanno quel valore in CampiCustom. Disattivare o
/// archiviare il campo lo toglie da filtri e predicato senza toccare i valori sui test ne' gli assi della matrice.
/// Gli scenari usano l'editor del catalogo (CampoSistemaDefinizioneViewModel: Modifica, Nuovo, Etichetta, spunta, Salva), lo stesso del
/// Master. La Home e il Report sono le classi reali, con Aggiorna() come la rivisita della scheda (MainViewModel).</summary>
[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class FiltriCampiSistemaDinamiciTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_filtricampisistema_" + Guid.NewGuid().ToString("N"));
    // Config dedicato di questa istanza (isolato come TestStateFactory): cosi' un riavvio puo' rileggere la vista salvata.
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "TestLoom_config_filtri_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
        if (Directory.Exists(_configDir)) { try { Directory.Delete(_configDir, true); } catch { } }
    }

    private AppState CreaStatoComeMaster()
    {
        SeedDemoDataService.Genera(_dir);
        Directory.CreateDirectory(_configDir);
        var state = new AppState(new AppConfigService(Path.Combine(_configDir, "config.json")), runBackgroundMaintenance: false);
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var master = state.Master.Operatori.Load().Single(o => o.Ruolo == RuoloOperatore.Master);
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        return state;
    }

    /// <summary>Riavvio vero dell'app: stato nuovo sullo stesso config.json (vista e selezioni salvate) e sulla stessa cartella dati.</summary>
    private AppState Riavvia()
    {
        var riaperto = new AppState(new AppConfigService(Path.Combine(_configDir, "config.json")), runBackgroundMaintenance: false);
        riaperto.InizializzaCartella(_dir);
        return riaperto;
    }

    /// <summary>Nuovo campo dall'editor del catalogo Dettagli, con la spunta "usabile come filtro" come nella colonna della tabella.</summary>
    private static async Task<string> CreaCampoDallEditor(AppState state, string etichetta, bool usabileComeFiltro)
    {
        var vm = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        vm.NuovoCommand.Execute(null);
        var nuovo = vm.Items.Last();
        nuovo.Etichetta = etichetta;
        nuovo.UsabileComeFiltro = usabileComeFiltro;
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.False(string.IsNullOrWhiteSpace(nuovo.Chiave));
        return nuovo.Chiave;
    }

    /// <summary>Modifica di un campo esistente dall'editor: spunta "usabile come filtro" oppure archiviazione.</summary>
    private static async Task ModificaCampoDallEditor(AppState state, string chiave, bool? usabileComeFiltro = null, bool archivia = false)
    {
        var vm = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        var voce = vm.Items.Single(c => c.Chiave == chiave);
        if (usabileComeFiltro is { } usabile) voce.UsabileComeFiltro = usabile;
        if (archivia) vm.ArchiviaCommand.Execute(voce);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
    }

    /// <summary>Test con un valore nel campo di sistema (TestCase.CampiCustom e' indicizzato dalla Chiave del catalogo).</summary>
    private static void SalvaTest(AppState state, string id, params (string Chiave, string Valore)[] campiCustom)
    {
        // Veicolo ed componente reali del catalogo (come i Test del seme): un nome inventato resta fuori dai conteggi della Home.
        var veicolo = state.Master.Veicoli.Load().First(v => !v.Archiviata).Nome;
        var componente = state.Master.Componente.Load().First(e => !e.Archiviata).Nome;
        var tc = new TestCase
        {
            Id = id, Componente = { componente }, Veicolo = veicolo, Veicoli = { veicolo }, Assegnatario = "Luigi", StatoLavoro = StatoLavoroTest.InCorso,
            // Come l'editor: la data di creazione rientra nel periodo di default della Home (anno in corso).
            DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now,
        };
        // Come ogni Test reale: almeno una revisione di esecuzione (l'indice conta i Test tramite le revisioni).
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Operatore = "Luigi", Data = DateTime.Now,
            Condizioni = new List<Condizione> { new() { Esito = Esito.Conforme, Componente = new EcuSelection(new[] { componente }) } },
        });
        foreach (var (chiave, valore) in campiCustom) tc.CampiCustom[chiave] = valore;
        state.TestCases.Save(tc, isNew: true);
        state.TestCases.RefreshIndex();
    }

    /// <summary>Come SalvaTest, con una funzionalità del catalogo e un componente scelta: la matrice in Stato % raggruppa per
    /// funzionalità×componente×veicolo e mostra un solo valore per gruppo (il rappresentante corrente).</summary>
    private static void SalvaTestConFunzionalita(AppState state, string id, string componente, (string Chiave, string Valore) campoCustom)
    {
        var veicolo = state.Master.Veicoli.Load().First(v => !v.Archiviata).Nome;
        var funzionalita = state.Master.Funzionalita.Load().First(f => !f.Archiviata).Nome;
        var tc = new TestCase
        {
            Id = id, Componente = { componente }, Veicolo = veicolo, Veicoli = { veicolo }, Funzionalita = { funzionalita }, Assegnatario = "Luigi",
            StatoLavoro = StatoLavoroTest.InCorso, DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now,
        };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Operatore = "Luigi", Data = DateTime.Now,
            Condizioni = new List<Condizione> { new() { Funzionalita = funzionalita, Esito = Esito.Conforme, Componente = new EcuSelection(new[] { componente }) } },
        });
        tc.CampiCustom[campoCustom.Chiave] = campoCustom.Valore;
        state.TestCases.Save(tc, isNew: true);
        state.TestCases.RefreshIndex();
    }

    private static FiltroCampoCustom? FiltroHome(HomeViewModel home, string chiave) =>
        home.FiltriCampiCustomDashboard.SingleOrDefault(f => f.Chiave == chiave);

    private static FiltroCampoCustom? FiltroReport(ReportViewModel report, string chiave) =>
        report.FiltriCampiCustomReport.SingleOrDefault(f => f.Chiave == chiave);

    private static void Seleziona(FiltroCampoCustom filtro, string valore) =>
        filtro.Filtro.Opzioni.Single(o => o.Valore == valore).Selezionato = true;

    [Fact]
    public async Task Editor_SpuntaUsabileComeFiltro_SiSalvaNelCatalogoEntraNelCatalogoFiltri()
    {
        var state = CreaStatoComeMaster();

        var chiave = await CreaCampoDallEditor(state, "Tipo di connessione", usabileComeFiltro: true);

        Assert.Equal("tipo-di-connessione", chiave);
        var salvato = state.Master.CampiSistemaDettagli.Load().Single(c => c.Chiave == chiave);
        Assert.True(salvato.UsabileComeFiltro);
        Assert.Equal(RuoloCampoSistema.Nessuno, salvato.Ruolo);
        Assert.False(salvato.Archiviata);
        Assert.Contains(CampoSistemaCatalogoService.CampiUtilizzabiliComeFiltro(state), c => c.Chiave == chiave);

        // Riapertura del catalogo da disco (come dopo un riavvio): la spunta e' persistita, non solo in memoria.
        var riaperto = TestStateFactory.Create();
        riaperto.InizializzaCartella(_dir);
        Assert.True(riaperto.Master.CampiSistemaDettagli.Load().Single(c => c.Chiave == chiave).UsabileComeFiltro);
    }

    /// <summary>"+ Crea nuovo campo..." dal dialogo dell'editor Tipi di test: il dialogo delega a CreaNuovoRapido, quindi
    /// qui si verifica lo stesso percorso senza UI. La spunta "Usabile come filtro" è salvata nel catalogo e il campo
    /// entra nel catalogo filtri solo se spuntata (stesso significato della colonna Filtro dell'editor del catalogo).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreaNuovoRapido_RispettaLaSpuntaUsabileComeFiltro(bool usabileComeFiltro)
    {
        var state = CreaStatoComeMaster();
        var vm = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);

        var (creato, errore) = await vm.CreaNuovoRapidoAsync("Canale di test", TipoCampoStrumento.Testo, usabileComeFiltro);

        Assert.Null(errore);
        Assert.NotNull(creato);
        var chiave = creato!.Chiave;
        Assert.False(string.IsNullOrWhiteSpace(chiave));
        var salvato = state.Master.CampiSistemaDettagli.Load().Single(c => c.Chiave == chiave);
        Assert.Equal(usabileComeFiltro, salvato.UsabileComeFiltro);
        Assert.Equal(usabileComeFiltro, CampoSistemaCatalogoService.CampiUtilizzabiliComeFiltro(state).Any(c => c.Chiave == chiave));
    }

    [Fact]
    public async Task CreaNuovoRapido_AttendeIlRilascioLockEPropagaIlFallimento()
    {
        var state = CreaStatoComeMaster();
        var vm = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli)
        {
            RilascioSessioneLockPerTest = (_, _, _) => EsitoIo.Errore,
            AttesaRetryLockPerTest = _ => Task.CompletedTask
        };

        var (creato, errore) = await vm.CreaNuovoRapidoAsync("Campo con rilascio fallito", TipoCampoStrumento.Testo, false);

        Assert.Null(creato);
        Assert.Equal(LocalizationService.Instance.Traduci("SalvatoSbloccoLockNonRiuscito"), errore);
        Assert.True(vm.RilascioLockInAttesa);
        Assert.Contains(state.Master.CampiSistemaDettagli.Load(), c => c.Etichetta == "Campo con rilascio fallito");
    }

    [Fact]
    public async Task Home_E_Report_IstanzaNuovaEIstanzaInCache_MostranoIlFiltroEFiltranoIValoriDelCampo()
    {
        var state = CreaStatoComeMaster();
        var chiave = await CreaCampoDallEditor(state, "Tipo di connessione", usabileComeFiltro: false);
        // Pagine gia' aperte prima della spunta (cache della scheda): non devono restare indietro.
        var homeInCache = new HomeViewModel(state, _ => { });
        var reportInCache = new ReportViewModel(state);
        Assert.Null(FiltroHome(homeInCache, chiave));
        Assert.Null(FiltroReport(reportInCache, chiave));

        await ModificaCampoDallEditor(state, chiave, usabileComeFiltro: true);
        SalvaTest(state, "FTR-USB-1", (chiave, "USB"));
        SalvaTest(state, "FTR-USB-2", (chiave, "USB"));
        SalvaTest(state, "FTR-ETH-1", (chiave, "Ethernet"));
        SalvaTest(state, "FTR-NESSUN-VALORE");

        // Rivisita della scheda (MainViewModel: cache hit -> Aggiorna()). Le istanze nuove si creano SUBITO dopo, prima di qualunque
        // selezione: la selezione di una pagina viene salvata nella vista condivisa e riapplicata alle istanze create dopo.
        homeInCache.Aggiorna();
        reportInCache.Aggiorna();
        var homeNuova = new HomeViewModel(state, _ => { });
        var reportNuovo = new ReportViewModel(state);

        foreach (var (nome, home, report) in new[] { ("cache", homeInCache, reportInCache), ("nuova", homeNuova, reportNuovo) })
        {
            var filtroHome = FiltroHome(home, chiave);
            var filtroReport = FiltroReport(report, chiave);
            Assert.True(filtroHome != null, $"Home {nome}: il filtro del campo di sistema non compare");
            Assert.True(filtroReport != null, $"Report {nome}: il filtro del campo di sistema non compare");
            Assert.Equal(new[] { "Ethernet", "USB" }, filtroHome!.Filtro.Opzioni.Select(o => o.Valore).OrderBy(v => v, StringComparer.Ordinal));
            Assert.Equal(new[] { "Ethernet", "USB" }, filtroReport!.Filtro.Opzioni.Select(o => o.Valore).OrderBy(v => v, StringComparer.Ordinal));
        }

        // Selezione sulla pagina in cache, poi annullata (la deselezione viene salvata come le altre selezioni).
        var totaleHomeCache = homeInCache.TestCaseNelFiltro;
        Seleziona(FiltroHome(homeInCache, chiave)!, "USB");
        Assert.Equal(2, homeInCache.TestCaseNelFiltro);
        Assert.True(homeInCache.TestCaseNelFiltro < totaleHomeCache, "Home in cache: la selezione non filtra");
        FiltroHome(homeInCache, chiave)!.Filtro.DeselezionaTutto();
        var totaleReportCache = reportInCache.TotaleRisultatiAnteprima;
        Seleziona(FiltroReport(reportInCache, chiave)!, "USB");
        Assert.Equal(2, reportInCache.TotaleRisultatiAnteprima);
        Assert.True(reportInCache.TotaleRisultatiAnteprima < totaleReportCache, "Report in cache: la selezione non filtra");
        FiltroReport(reportInCache, chiave)!.Filtro.DeselezionaTutto();

        // Stessa verifica sulle istanze nuove.
        var totaleHomeNuova = homeNuova.TestCaseNelFiltro;
        Seleziona(FiltroHome(homeNuova, chiave)!, "USB");
        Assert.Equal(2, homeNuova.TestCaseNelFiltro);
        Assert.True(homeNuova.TestCaseNelFiltro < totaleHomeNuova, "Home nuova: la selezione non filtra");
        var totaleReportNuovo = reportNuovo.TotaleRisultatiAnteprima;
        Seleziona(FiltroReport(reportNuovo, chiave)!, "USB");
        Assert.Equal(2, reportNuovo.TotaleRisultatiAnteprima);
        Assert.True(reportNuovo.TotaleRisultatiAnteprima < totaleReportNuovo, "Report nuovo: la selezione non filtra");
    }

    /// <summary>Campo Lista (come "VCI Connection": USB / Bluetooth / Wi-Fi) usabile come filtro: Home e Report offrono le opzioni del catalogo
    /// anche quando nessun Test le usa ancora, poi i valori presenti nei Test non gia' tra quelle (con maiuscole diverse, una voce sola). Il
    /// filtro su un'opzione senza Test non trova nulla, senza errori; quello su un'opzione filtra i Test con il valore corrispondente.</summary>
    [Fact]
    public async Task CampoListaConOpzioni_HomeEReportOffronoLeOpzioniDelCatalogoEIValoriPresenti()
    {
        var state = CreaStatoComeMaster();
        var chiave = await CreaCampoDallEditor(state, "VCI Connection", usabileComeFiltro: true);
        // Tipo Lista con le sue opzioni, come nell'editor del catalogo: Tipo nel menu, poi "Opzioni" (ImpostaOpzioniLista + Salva).
        var catalogo = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        catalogo.IniziaModificaCommand.AttendiModifica();
        var voce = catalogo.Items.Single(c => c.Chiave == chiave);
        voce.Tipo = TipoCampoStrumento.Lista;
        Assert.True(catalogo.ImpostaOpzioniLista(voce, new[] { "USB", "Bluetooth", "Wi-Fi" }));
        await catalogo.SalvaCommand.ExecuteAsync(null);
        Assert.False(catalogo.HaMessaggioErrore, catalogo.MessaggioErrore);

        SalvaTest(state, "FTR-LST-1", (chiave, "usb"));     // grafia diversa dall'opzione del catalogo
        SalvaTest(state, "FTR-LST-2", (chiave, "Zigbee"));  // valore presente che non e' tra le opzioni

        var home = new HomeViewModel(state, _ => { });
        var report = new ReportViewModel(state);
        var attese = new[] { "USB", "Bluetooth", "Wi-Fi", "Zigbee" };
        Assert.Equal(attese, FiltroHome(home, chiave)!.Filtro.Opzioni.Select(o => o.Valore));
        Assert.Equal(attese, FiltroReport(report, chiave)!.Filtro.Opzioni.Select(o => o.Valore));

        // "Bluetooth" non e' usato da nessun Test: si puo' selezionare, senza risultati.
        Seleziona(FiltroHome(home, chiave)!, "Bluetooth");
        Assert.Equal(0, home.TestCaseNelFiltro);
        FiltroHome(home, chiave)!.Filtro.DeselezionaTutto();

        // "USB" trova il Test con "usb" (confronto senza maiuscole, come gli altri filtri), su Home e su Report.
        Seleziona(FiltroHome(home, chiave)!, "USB");
        Assert.Equal(1, home.TestCaseNelFiltro);
        Seleziona(FiltroReport(report, chiave)!, "USB");
        Assert.Equal(1, report.TotaleRisultatiAnteprima);

        // Selezione multipla come per gli altri filtri: "USB" e "Zigbee" insieme trovano i due Test.
        Seleziona(FiltroHome(home, chiave)!, "Zigbee");
        Assert.Equal(2, home.TestCaseNelFiltro);
        Seleziona(FiltroReport(report, chiave)!, "Zigbee");
        Assert.Equal(2, report.TotaleRisultatiAnteprima);
    }

    [Fact]
    public async Task VciUsbDitm_HomeIndicizzataDopoRefreshContaComeListaTest()
    {
        var state = CreaStatoComeMaster();
        var chiave = await CreaCampoDallEditor(state, "VCI Connection", usabileComeFiltro: true);
        var catalogo = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        catalogo.IniziaModificaCommand.AttendiModifica();
        var voce = catalogo.Items.Single(c => c.Chiave == chiave);
        voce.Tipo = TipoCampoStrumento.Lista;
        Assert.True(catalogo.ImpostaOpzioniLista(voce, new[] { "VCI USB", "VCI LAN" }));
        await catalogo.SalvaCommand.ExecuteAsync(null);
        Assert.False(catalogo.HaMessaggioErrore, catalogo.MessaggioErrore);

        SalvaTest(state, "VCI-USB-TestLoom", (chiave, "VCI USB"));
        var home = new HomeViewModel(state, _ => { }, aggiornaInBackground: true);
        await home.AttendiAggiornamentoAsync();
        var filtroHome = FiltroHome(home, chiave)!;
        Assert.NotNull(filtroHome);
        Seleziona(filtroHome, "VCI USB");
        await home.AttendiAggiornamentoAsync();

        var lista = new TestCaseListViewModel(state, _ => { });
        var filtroLista = lista.FiltriCampiCustomLista.Single(f => f.Chiave == chiave);
        Seleziona(filtroLista, "VCI USB");
        lista.Aggiorna();

        Assert.Equal(1, lista.TotaleRisultati);
        Assert.Equal(lista.TotaleRisultati, home.TestCaseNelFiltro);
        Assert.Equal("VCI-USB-TestLoom", Assert.Single(lista.Righe).Id);
    }

    [Fact]
    public async Task DisattivareOArchiviareUnCampo_TogliePiuFiltroEPredicato_SenzaToccareValoriENeAssi()
    {
        var state = CreaStatoComeMaster();
        var disattivato = await CreaCampoDallEditor(state, "Campo da disattivare", usabileComeFiltro: true);
        var archiviato = await CreaCampoDallEditor(state, "Campo da archiviare", usabileComeFiltro: true);
        SalvaTest(state, "FTR-1", (disattivato, "A"), (archiviato, "B"));
        SalvaTest(state, "FTR-2", (disattivato, "C"));
        var home = new HomeViewModel(state, _ => { });
        var report = new ReportViewModel(state);
        var totaleHome = home.TestCaseNelFiltro;
        var totaleReport = report.TotaleRisultatiAnteprima;
        Seleziona(FiltroHome(home, disattivato)!, "A");
        Seleziona(FiltroReport(report, archiviato)!, "B");
        Assert.Equal(1, home.TestCaseNelFiltro);
        Assert.Equal(1, report.TotaleRisultatiAnteprima);

        // Asse della matrice configurato sul campo che sta per essere disattivato: deve restare.
        var prefisso = DimensioniMatrice.CampoCustomPrefix;
        report.AggiungiCampoMatrice(righe: false);
        report.CampiXMatrice[^1].Dimensione = report.OpzioniDimensioniMatrice.First(d => d.Id == prefisso + disattivato);

        await ModificaCampoDallEditor(state, disattivato, usabileComeFiltro: false);
        await ModificaCampoDallEditor(state, archiviato, archivia: true);
        home.Aggiorna();
        report.Aggiorna();

        Assert.Null(FiltroHome(home, disattivato));
        Assert.Null(FiltroHome(home, archiviato));
        Assert.Null(FiltroReport(report, disattivato));
        Assert.Null(FiltroReport(report, archiviato));
        Assert.Equal(totaleHome, home.TestCaseNelFiltro);
        Assert.Equal(totaleReport, report.TotaleRisultatiAnteprima);
        Assert.False(home.HaFiltriDashboardAttivi);
        // Restano solo i default storici delle categorie funzionalità; nessun filtro dei campi di sistema rimossi.
        Assert.Empty(report.FiltriCampiCustomReport.Where(x => x.Filtro.HaSelezione));
        Assert.DoesNotContain(report.FiltriMatrice, x => x.Filtro.HaSelezione && x.Dimensione.Id != DimensioniMatrice.CategoriaFunzionalita);

        // Valori sui test e assi configurati intatti.
        var test1 = state.TestCases.GetById("FTR-1")!;
        Assert.Equal("A", test1.CampiCustom[disattivato]);
        Assert.Equal("B", test1.CampiCustom[archiviato]);
        Assert.Equal("C", state.TestCases.GetById("FTR-2")!.CampiCustom[disattivato]);
        Assert.Contains(report.CampiXMatrice, c => c.Dimensione.Id == prefisso + disattivato);
        Assert.Contains(report.OpzioniDimensioniMatrice, d => d.Id == prefisso + disattivato);
    }

    [Fact]
    public async Task SelezioneSuUnCampoAncoraValido_RestaDopoLaRiconciliazioneConUnCampoNuovo()
    {
        var state = CreaStatoComeMaster();
        var a = await CreaCampoDallEditor(state, "Campo A", usabileComeFiltro: true);
        var b = await CreaCampoDallEditor(state, "Campo B", usabileComeFiltro: true);
        SalvaTest(state, "FTR-1", (a, "X"));
        SalvaTest(state, "FTR-2", (a, "Y"));
        SalvaTest(state, "FTR-3", (b, "Z"));
        var home = new HomeViewModel(state, _ => { });
        var report = new ReportViewModel(state);
        Seleziona(FiltroHome(home, a)!, "X");
        Seleziona(FiltroReport(report, a)!, "X");
        Assert.Equal(1, home.TestCaseNelFiltro);
        Assert.Equal(1, report.TotaleRisultatiAnteprima);

        var c = await CreaCampoDallEditor(state, "Campo C", usabileComeFiltro: true);
        home.Aggiorna();
        report.Aggiorna();

        Assert.NotNull(FiltroHome(home, c));
        Assert.NotNull(FiltroReport(report, c));
        Assert.Contains("X", FiltroHome(home, a)!.Filtro.Selezionati);
        Assert.Contains("X", FiltroReport(report, a)!.Filtro.Selezionati);
        Assert.Equal(1, home.TestCaseNelFiltro);
        Assert.Equal(1, report.TotaleRisultatiAnteprima);
    }

    /// <summary>Asse su un campo di sistema disabilitato: dopo un riavvio vero (stato e pagina nuovi, letti dal disco) l'asse resta
    /// e la matrice raggruppa ancora per i valori del campo, non solo l'asse compare nella configurazione.</summary>
    [Fact]
    public async Task AssePersistitoSuUnCampoDisattivato_RestaDopoLaRiapertura()
    {
        var state = CreaStatoComeMaster();
        var chiave = await CreaCampoDallEditor(state, "Campo da asse", usabileComeFiltro: true);
        // Due componente diverse: in Stato % ogni gruppo funzionalità×componente×veicolo mostra un solo valore (il rappresentante).
        var componente = state.Master.Componente.Load().Where(e => !e.Archiviata).Select(e => e.Nome).Take(2).ToList();
        SalvaTestConFunzionalita(state, "AXS-ALFA", componente[0], (chiave, "Alfa"));
        SalvaTestConFunzionalita(state, "AXS-BETA", componente[1], (chiave, "Beta"));
        var report = new ReportViewModel(state);
        var prefisso = DimensioniMatrice.CampoCustomPrefix;
        report.AggiungiCampoMatrice(righe: false);
        report.CampiXMatrice[^1].Dimensione = report.OpzioniDimensioniMatrice.First(d => d.Id == prefisso + chiave);
        // Prima della disabilitazione l'asse mostra i valori del campo (precondizione dell'output della matrice).
        Assert.Contains(report.ColonneMatrice, colonna => colonna.Contains("Alfa"));
        Assert.Contains(report.ColonneMatrice, colonna => colonna.Contains("Beta"));

        await ModificaCampoDallEditor(state, chiave, usabileComeFiltro: false);
        var reportRiaperto = new ReportViewModel(Riavvia());

        Assert.Contains(reportRiaperto.CampiXMatrice, c => c.Dimensione.Id == prefisso + chiave);
        Assert.Null(FiltroReport(reportRiaperto, chiave));
        Assert.Contains(reportRiaperto.ColonneMatrice, colonna => colonna.Contains("Alfa"));
        Assert.Contains(reportRiaperto.ColonneMatrice, colonna => colonna.Contains("Beta"));
    }

    /// <summary>Un asse salvato su un campo archiviato mostra l'ultima etichetta nota del campo, non la Chiave grezza.</summary>
    [Fact]
    public async Task AssePersistitoSuUnCampoArchiviato_MostraLUltimaEtichetta()
    {
        var state = CreaStatoComeMaster();
        var chiave = await CreaCampoDallEditor(state, "Campo archiviato", usabileComeFiltro: true);
        var report = new ReportViewModel(state);
        var prefisso = DimensioniMatrice.CampoCustomPrefix;
        report.AggiungiCampoMatrice(righe: false);
        report.CampiXMatrice[^1].Dimensione = report.OpzioniDimensioniMatrice.First(d => d.Id == prefisso + chiave);
        await ModificaCampoDallEditor(state, chiave, archivia: true);

        var reportRiaperto = new ReportViewModel(Riavvia());

        Assert.Equal("Campo archiviato", reportRiaperto.OpzioniDimensioniMatrice.Single(d => d.Id == prefisso + chiave).Etichetta);
    }

    /// <summary>L'etichetta di una dimensione notifica il cambio: la combo dell'asse si aggiorna quando il campo viene rinominato.</summary>
    [Fact]
    public void DimensioneMatriceOpzione_CambioEtichettaNotificaLaCombo()
    {
        var opzione = new DimensioneMatriceOpzione { Id = "dim", Etichetta = "Vecchia" };
        var notificate = new List<string?>();
        opzione.PropertyChanged += (_, e) => notificate.Add(e.PropertyName);

        opzione.Etichetta = "Nuova";

        Assert.Contains(nameof(DimensioneMatriceOpzione.Etichetta), notificate);
    }

    /// <summary>Revisione (medio 2): un campo di sistema diventato filtrabile dopo il primo giro ha il suo filtro di dimensione collegato
    /// alla persistenza. Disabilitato e riabilitato dal catalogo, riprende la selezione che aveva.</summary>
    [Fact]
    public async Task DimensioneCampoSistema_RiabilitatoRipristinaLaSelezione()
    {
        var state = CreaStatoComeMaster();
        var chiave = await CreaCampoDallEditor(state, "Tipo di connessione", usabileComeFiltro: false);
        SalvaTest(state, "DIM-USB", (chiave, "USB"));
        SalvaTest(state, "DIM-ETH", (chiave, "Ethernet"));
        var report = new ReportViewModel(state);

        Assert.Null(FiltroReport(report, chiave));

        await ModificaCampoDallEditor(state, chiave, usabileComeFiltro: true);
        report.Aggiorna();
        Seleziona(FiltroReport(report, chiave)!, "USB");

        await ModificaCampoDallEditor(state, chiave, usabileComeFiltro: false);
        report.Aggiorna();
        Assert.Null(FiltroReport(report, chiave));

        await ModificaCampoDallEditor(state, chiave, usabileComeFiltro: true);
        report.Aggiorna();
        Assert.Equal(new[] { "USB" }, FiltroReport(report, chiave)!.Filtro.Selezionati);
    }

    /// <summary>Revisione (medio 2): la selezione fatta su un filtro di dimensione nato dopo il primo giro e' salvata e riappare dopo un
    /// riavvio vero (stato e pagina nuovi, letti dal disco) — prima non veniva scritta affatto.</summary>
    [Fact]
    public async Task DimensioneCampoSistemaAggiuntaDopoIlPrimoGiro_SelezioneSopravvieAlRiavvio()
    {
        var state = CreaStatoComeMaster();
        var chiave = await CreaCampoDallEditor(state, "Tipo di connessione", usabileComeFiltro: false);
        SalvaTest(state, "DIM-USB", (chiave, "USB"));
        SalvaTest(state, "DIM-ETH", (chiave, "Ethernet"));
        var report = new ReportViewModel(state);


        await ModificaCampoDallEditor(state, chiave, usabileComeFiltro: true);
        report.Aggiorna();
        Seleziona(FiltroReport(report, chiave)!, "Ethernet");

        var reportRiaperto = new ReportViewModel(Riavvia());
        Assert.Equal(new[] { "Ethernet" }, FiltroReport(reportRiaperto, chiave)!.Filtro.Selezionati);
    }

    /// <summary>Un giro a catalogo invariato non rilegge i campi di sistema (impronta del catalogo); un cambio di catalogo, invece, li
    /// riconcilia e il campo compare con i suoi valori.</summary>
    [Fact]
    public async Task Report_CatalogoInvariato_NonRileggeILeCampi_CambioRiconcilia()
    {
        var state = CreaStatoComeMaster();
        var report = new ReportViewModel(state);
        report.Aggiorna();
        var iniziale = report.ConciliazioniCampiSistema;

        report.Aggiorna();
        report.Aggiorna();
        Assert.Equal(iniziale, report.ConciliazioniCampiSistema);

        var chiave = await CreaCampoDallEditor(state, "Campo nuovo", usabileComeFiltro: true);
        SalvaTest(state, "CAT-1", (chiave, "Valore"));
        report.Aggiorna();
        Assert.Equal(iniziale + 1, report.ConciliazioniCampiSistema);
        Assert.NotNull(FiltroReport(report, chiave));
    }

}
