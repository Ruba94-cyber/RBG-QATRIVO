using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Bug segnalato dall'utente (2026-09-18): "chiudendo e riaprendo l'app anche in audit mi torna al
/// selettore veicolo iniziale e non a quello precedentemente selezionato, tutte le viste dovrebbero
/// rimanere esattamente come quando l'app e' stata chiusa". Un riavvio dell'app e' una NUOVA istanza di
/// AppState (config ricaricata da disco) e una NUOVA istanza del ViewModel: questi test simulano esattamente
/// quello, non una semplice rivisita della pagina nella stessa sessione (che gia' funzionava tramite
/// _pagineCache di MainViewModel).
/// </summary>
public sealed class ViewStatePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ViewState_" + Guid.NewGuid().ToString("N"));
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "TestLoom_ViewState_Config_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_configDir, recursive: true); } catch { }
    }

    /// <summary>A differenza di TestStateFactory.Create() (un GUID nuovo ad ogni chiamata, quindi config
    /// isolata e SEPARATA fra le due "sessioni"), qui la STESSA config.json va condivisa fra apertura 1 e
    /// apertura 2: e' esattamente il file che sopravvive a un riavvio reale dell'app.</summary>
    private AppState NuovaSessione() =>
        new(new AppConfigService(Path.Combine(_configDir, "config.json")), runBackgroundMaintenance: false);

    [Fact]
    public void Audit_SelezioneSottoveicolo_SopravviveAUnRiavvioDellApp()
    {
        var sessione1 = NuovaSessione();
        Directory.CreateDirectory(_dir);
        sessione1.InizializzaCartella(_dir);
        sessione1.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = "Daily 24" },
            new() { Nome = "eDaily 24" },
        });
        var audit1 = new AuditViewModel(sessione1);
        audit1.SottoveicoloSelezionato = "eDaily 24";
        sessione1.Dispose();

        // "Riavvio": nuova AppState (stessa config.json su disco) e nuovo AuditViewModel, non una rivisita
        // della stessa istanza.
        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var audit2 = new AuditViewModel(sessione2);

        Assert.Equal("eDaily 24", audit2.SottoveicoloSelezionato);
    }

    [Fact]
    public void Audit_SottoveicoloNonPiuDisponibile_RicadeSulPrimoDisponibileSenzaLanciare()
    {
        var sessione1 = NuovaSessione();
        Directory.CreateDirectory(_dir);
        sessione1.InizializzaCartella(_dir);
        sessione1.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily 24" }, new() { Nome = "eDaily 24" } });
        var audit1 = new AuditViewModel(sessione1);
        audit1.SottoveicoloSelezionato = "eDaily 24";
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        // Il veicolo scelto nella sessione precedente e' stato archiviato nel frattempo (es. da un altro PC
        // sulla stessa cartella condivisa): la persistenza non deve mai bloccare l'apertura pagina. Archiviata
        // = true su una riga esistente, non un'omissione dalla lista: un Save senza expectedSnapshot tratta
        // di proposito un'omissione come ambigua (protezione contro liste filtrate/paginate che
        // diventerebbero cancellazioni), quindi rimuovere la riga non basterebbe a simulare questo scenario.
        var tutti = sessione2.Master.Veicoli.Load();
        tutti.Single(v => v.Nome == "eDaily 24").Archiviata = true;
        sessione2.Master.Veicoli.Save(tutti);
        Assert.DoesNotContain("eDaily 24", sessione2.Master.Veicoli.Load().Where(v => !v.Archiviata).Select(v => v.Nome));
        var audit2 = new AuditViewModel(sessione2);

        Assert.Equal("Daily 24", audit2.SottoveicoloSelezionato);
    }

    [Fact]
    public void Report_AssiMatrice_SopravviveAUnRiavvioDellApp()
    {
        var sessione1 = NuovaSessione();
        Directory.CreateDirectory(_dir);
        sessione1.InizializzaCartella(_dir);
        var report1 = new ReportViewModel(sessione1);
        report1.AsseXMatrice = report1.OpzioniDimensioniMatrice.First(d => d.Id == DimensioniMatrice.Requisito);
        report1.AsseYMatrice = report1.OpzioniDimensioniMatrice.First(d => d.Id == DimensioniMatrice.Esito);
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var report2 = new ReportViewModel(sessione2);

        Assert.Equal(DimensioniMatrice.Requisito, report2.AsseXMatrice?.Id);
        Assert.Equal(DimensioniMatrice.Esito, report2.AsseYMatrice?.Id);
    }

    [Fact]
    public void Home_IntervalloDate_SopravviveAUnRiavvioDellApp()
    {
        var sessione1 = NuovaSessione();
        Directory.CreateDirectory(_dir);
        sessione1.InizializzaCartella(_dir);
        var home1 = new HomeViewModel(sessione1, _ => { });
        var da = new DateTimeOffset(new DateTime(2026, 3, 1));
        var a = new DateTimeOffset(new DateTime(2026, 3, 31));
        home1.DashboardDataDa = da;
        home1.DashboardDataA = a;
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var home2 = new HomeViewModel(sessione2, _ => { });

        Assert.Equal(da, home2.DashboardDataDa);
        Assert.Equal(a, home2.DashboardDataA);
    }

    [Fact]
    public void Home_ReimpostaFiltri_PersisteIlRitornoAiDefault()
    {
        var sessione1 = NuovaSessione();
        Directory.CreateDirectory(_dir);
        sessione1.InizializzaCartella(_dir);
        var home1 = new HomeViewModel(sessione1, _ => { });
        home1.DashboardDataDa = new DateTimeOffset(new DateTime(2026, 3, 1));
        home1.ReimpostaFiltriDashboardCommand.Execute(null);
        var defaultAttesa = home1.DashboardDataDa;
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var home2 = new HomeViewModel(sessione2, _ => { });

        Assert.Equal(defaultAttesa, home2.DashboardDataDa);
    }

    /// <summary>Copertura del follow-up dell'utente (2026-09-18): "tutte quante le viste devono mantenere
    /// filtri ecc ecc non solo alcune" — non solo il selettore Audit e gli assi/date gia' coperti sopra, ma
    /// TUTTI i filtri a scelta multipla di Home (fissi + custom) e la selezione dei grafici.</summary>
    [Fact]
    public void Home_FiltroGammaEFiltroCustom_SopravviveAUnRiavvioDellApp()
    {
        var sessione1 = NuovaSessione();
        Directory.CreateDirectory(_dir);
        sessione1.InizializzaCartella(_dir);
        sessione1.Master.Gamma.Save(new List<Gamma> { new() { Nome = "MY24" }, new() { Nome = "MY25" } });
        var home1 = new HomeViewModel(sessione1, _ => { });
        home1.FiltroGammaDashboard.Opzioni.Single(o => o.Valore == "MY25").Selezionato = true;
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var home2 = new HomeViewModel(sessione2, _ => { });

        Assert.Equal(new[] { "MY25" }, home2.FiltroGammaDashboard.Selezionati);
    }

    [Fact]
    public void Home_SelezioneGrafici_SopravviveAUnRiavvioDellApp()
    {
        var sessione1 = NuovaSessione();
        Directory.CreateDirectory(_dir);
        sessione1.InizializzaCartella(_dir);
        var home1 = new HomeViewModel(sessione1, _ => { });
        home1.ChartOptions.Single(c => c.Id == "andamento").Selezionato = true;
        home1.ChartOptions.Single(c => c.Id == "donut").Selezionato = false;
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var home2 = new HomeViewModel(sessione2, _ => { });

        Assert.True(home2.ChartOptions.Single(c => c.Id == "andamento").Selezionato);
        Assert.False(home2.ChartOptions.Single(c => c.Id == "donut").Selezionato);
    }

    [Fact]
    public void Report_FiltroEcuVeicoloDateETemplate_SopravviveAUnRiavvioDellApp()
    {
        var sessione1 = NuovaSessione();
        Directory.CreateDirectory(_dir);
        sessione1.InizializzaCartella(_dir);
        sessione1.Master.TestTemplate.Save(new List<TestTemplate> { new() { Nome = "Standard" } });
        var report1 = new ReportViewModel(sessione1);
        report1.FiltroEcu = "BCM";
        report1.FiltroVeicolo = "Daily 24";
        report1.DataDa = new DateTimeOffset(new DateTime(2026, 1, 1));
        report1.DataA = new DateTimeOffset(new DateTime(2026, 12, 31));
        // Save() sui master preserva le righe seminate automaticamente (TestTemplateDefaults.Seed) quando non
        // c'e' un edit esplicito su una riga preesistente: Opzioni contiene quelle PIU' la mia, non solo la mia.
        report1.FiltroTemplate.Opzioni.Single(o => o.Etichetta == "Standard").Selezionato = true;
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var report2 = new ReportViewModel(sessione2);

        Assert.Equal("BCM", report2.FiltroEcu);
        Assert.Equal("Daily 24", report2.FiltroVeicolo);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 1, 1)), report2.DataDa);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 12, 31)), report2.DataA);
        Assert.Equal("01/01/2026", report2.DataDaTesto);
        Assert.Single(report2.FiltroTemplate.Selezionati);
    }

    [Fact]
    public void Report_FiltroDimensioneMatrice_SopravviveAUnRiavvioDellApp()
    {
        var sessione1 = NuovaSessione();
        Directory.CreateDirectory(_dir);
        sessione1.InizializzaCartella(_dir);
        // Le opzioni di ogni dimensione della matrice derivano SOLO dai Test esistenti
        // (ReportMatriceService.ValoriDisponibiliPerDimensioni espande i Test, non legge un catalogo master
        // a parte): senza almeno un Test con un esito, "Esito" resta vuota qualunque sia la persistenza.
        sessione1.TestCases.Save(new TestCase
        {
            Id = "TC-1",
            Esecuzioni = new List<Esecuzione> { new() { Iterazione = 1, Esito = Esito.Conforme } },
        }, true);
        var report1 = new ReportViewModel(sessione1);
        var filtroEsito = report1.FiltriMatrice.First(f => f.Dimensione.Id == DimensioniMatrice.Esito).Filtro;
        filtroEsito.Opzioni.First().Selezionato = true;
        var selezioneAttesa = filtroEsito.Selezionati;
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var report2 = new ReportViewModel(sessione2);
        var filtroEsito2 = report2.FiltriMatrice.First(f => f.Dimensione.Id == DimensioniMatrice.Esito).Filtro;

        Assert.Equal(selezioneAttesa, filtroEsito2.Selezionati);
    }

    /// <summary>Copertura dell'ulteriore follow-up dell'utente (2026-09-18): "la stabilita' deve rimanere
    /// anche in test, cr/anomalie ecc, tutte le pagine non solo in home e report".</summary>
    [Fact]
    public void TestList_FiltriScalariETemplate_SopravviveAUnRiavvioDellApp()
    {
        var sessione1 = NuovaSessione();
        Directory.CreateDirectory(_dir);
        sessione1.InizializzaCartella(_dir);
        sessione1.Master.TestTemplate.Save(new List<TestTemplate> { new() { Nome = "Standard" } });
        // Il filtro componente e' una scelta fra i valori esistenti: serve almeno un Test con quell'componente.
        sessione1.TestCases.Save(new TestCase { Id = "TC-BCM", Componente = "BCM", CreatoDa = "Test" }, isNew: true);
        var lista1 = new TestCaseListViewModel(sessione1, _ => { });
        lista1.FiltroEcu.Seleziona("BCM");
        lista1.FiltroEsito.Seleziona(nameof(Esito.Conforme));
        lista1.FiltroTemplate.Seleziona(lista1.TemplatesDisponibili.Single(t => t.Nome == "Standard").Uid.ToString());
        lista1.FiltroDataDa = new DateTimeOffset(new DateTime(2026, 1, 1));
        // Testo libero ESCLUSO di proposito: verifica che NON sopravviva (comportamento voluto, non un bug).
        lista1.FiltroTesto = "non deve persistere";
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var lista2 = new TestCaseListViewModel(sessione2, _ => { });

        Assert.Equal(new[] { "BCM" }, lista2.FiltroEcu.Selezionati);
        Assert.Equal(new[] { nameof(Esito.Conforme) }, lista2.FiltroEsito.Selezionati);
        Assert.Equal(new[] { "Standard" }, lista2.FiltroTemplate.Opzioni.Where(o => o.Selezionato).Select(o => o.Etichetta));
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 1, 1)), lista2.FiltroDataDa);
        Assert.Equal("", lista2.FiltroTesto);
    }

    [Fact]
    public void TestList_ReimpostaFiltri_PersisteLoStatoAzzerato()
    {
        var sessione1 = NuovaSessione();
        Directory.CreateDirectory(_dir);
        sessione1.InizializzaCartella(_dir);
        sessione1.TestCases.Save(new TestCase { Id = "TC-BCM", Componente = "BCM", CreatoDa = "Test" }, isNew: true);
        var lista1 = new TestCaseListViewModel(sessione1, _ => { });
        lista1.FiltroEcu.Seleziona("BCM");
        lista1.ReimpostaFiltriCommand.Execute(null);
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var lista2 = new TestCaseListViewModel(sessione2, _ => { });

        Assert.False(lista2.FiltroEcu.HaSelezione);
    }

    [Fact]
    public void AnomalieCr_FiltriScalariEMultiSelect_SopravviveAUnRiavvioDellApp()
    {
        var sessione1 = NuovaSessione();
        Directory.CreateDirectory(_dir);
        sessione1.InizializzaCartella(_dir);
        sessione1.Master.Gamma.Save(new List<Gamma> { new() { Nome = "MY24" } });
        sessione1.NonConformita.Save(new NonConformita { Id = "NC-OWN", Owner = "Mario Rossi" });
        var nc1 = new NonConformitaViewModel(sessione1, _ => { });
        nc1.FiltroTipo = FiltroTipoAnomaliaOCr.Anomalie;
        nc1.FiltroStato = StatoNonConformita.Chiusa;
        nc1.FiltroAssegnatario.Opzioni.Single(o => o.Valore == "Mario Rossi").Selezionato = true;
        nc1.FiltroGamma.Opzioni.Single().Selezionato = true;
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var nc2 = new NonConformitaViewModel(sessione2, _ => { });

        Assert.Equal(FiltroTipoAnomaliaOCr.Anomalie, nc2.FiltroTipo);
        Assert.Equal(StatoNonConformita.Chiusa, nc2.FiltroStato);
        Assert.Equal(new[] { "Mario Rossi" }, nc2.FiltroAssegnatario.Selezionati);
        Assert.Equal(new[] { "MY24" }, nc2.FiltroGamma.Selezionati);
    }

    [Fact]
    public void ReportEAnomalie_PersistonoFiltriIndipendentiNellaStessaSessione()
    {
        var sessione1 = NuovaSessione();
        Directory.CreateDirectory(_dir);
        sessione1.InizializzaCartella(_dir);
        sessione1.Master.TestTemplate.Save(new List<TestTemplate> { new() { Nome = "Standard" } });
        sessione1.NonConformita.Save(new NonConformita { Id = "NC-OWN", Owner = "Mario Rossi" });

        var report1 = new ReportViewModel(sessione1);
        report1.FiltroEcu = "BCM";
        var anomalie1 = new NonConformitaViewModel(sessione1, _ => { });
        anomalie1.FiltroStato = StatoNonConformita.Chiusa;
        anomalie1.FiltroAssegnatario.Opzioni.Single(o => o.Valore == "Mario Rossi").Selezionato = true;
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var report2 = new ReportViewModel(sessione2);
        var anomalie2 = new NonConformitaViewModel(sessione2, _ => { });

        Assert.Equal("BCM", report2.FiltroEcu);
        Assert.Equal(StatoNonConformita.Chiusa, anomalie2.FiltroStato);
        Assert.Equal(new[] { "Mario Rossi" }, anomalie2.FiltroAssegnatario.Selezionati);
    }

    [Fact]
    public void ViewStateService_NonScriveSuDisco_SeIlValoreNonECambiato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        ViewStateService.Scrivi(state, "Test.Chiave", "valore");
        var scrittureAlPrimoGiro = File.ReadAllText(Path.Combine(state.ConfigService.CartellaLocale, "config.json")).Length;

        ViewStateService.Scrivi(state, "Test.Chiave", "valore"); // stesso valore

        var scrittureAlSecondoGiro = File.ReadAllText(Path.Combine(state.ConfigService.CartellaLocale, "config.json")).Length;
        Assert.Equal(scrittureAlPrimoGiro, scrittureAlSecondoGiro);
        Assert.Equal("valore", ViewStateService.Leggi(state, "Test.Chiave"));
    }
}
