using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Home con sezioni nascoste: una sezione nascosta non legge l'indice ne' i cataloghi dei propri filtri, anche alla
/// costruzione; quella appena resa visibile si calcola una sola volta e mostra gli stessi valori di una Home con tutte le
/// sezioni visibili fin dall'inizio. Le letture per sezione sono contate con <see cref="PerfContatori"/>, come nei test
/// della cache grafici; xunit.runner.json disattiva il parallelismo, quindi i contatori statici sono affidabili.
/// </summary>
[Trait("Category", "Lento")]
public sealed class HomeSezioniLazyTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;
    private readonly List<AppState> _riferimenti = new();

    public HomeSezioniLazyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoom_HomeSezioniLazy_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(_dir);
        foreach (var path in Directory.GetFiles(_dir, "testcase_CORROTTO-TEST-99.json", SearchOption.AllDirectories))
            File.Delete(path);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_dir);
        _state.TestCases.RefreshIndex();
    }

    public void Dispose()
    {
        foreach (var riferimento in _riferimenti) riferimento.Dispose();
        _state.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Home di riferimento: stesso archivio su un altro config, quindi con tutte le sezioni visibili (layout predefinito).</summary>
    private HomeViewModel CreaRiferimento()
    {
        var stato = new AppState(new AppConfigService(Path.Combine(_dir + "_rif_" + Guid.NewGuid().ToString("N"), "config.json")),
            runBackgroundMaintenance: false);
        stato.InizializzaCartella(_dir);
        _riferimenti.Add(stato);
        return new HomeViewModel(stato, _ => { });
    }

    private void NascondiTutteTranneRecenti(HomeViewModel home)
    {
        foreach (var id in HomeSezioni.Predefinite)
            if (id != HomeSezioni.Recenti) home.ImpostaSezioneVisibile(id, false);
    }

    /// <summary>Con le sezioni nascoste anche la costruzione della Home non legge nulla di quelle sezioni: resta solo la sonda
    /// minima che tiene aggiornato il banner dell'indice.</summary>
    [Fact]
    public void SezioniNascoste_CostruzioneEAggiorna_NonInterrogano_LeSezioniSoloLaSondaDelBanner()
    {
        var preparazione = new HomeViewModel(_state, _ => { });
        NascondiTutteTranneRecenti(preparazione);

        PerfContatori.Reset();
        var home = new HomeViewModel(_state, _ => { });
        var costruzione = PerfContatori.LeggiSnapshot();
        Assert.Equal(0, costruzione.HomeLetturaSommario);
        Assert.Equal(0, costruzione.HomeLetturaCopertura);
        Assert.Equal(0, costruzione.HomeLetturaListe);
        Assert.Equal(0, costruzione.HomeLetturaFiltri);
        Assert.Equal(0, costruzione.HomeLetturaGrafici);
        Assert.Equal(1, costruzione.HomeSondaIndice);

        PerfContatori.Reset();
        home.Aggiorna();
        var refresh = PerfContatori.LeggiSnapshot();
        Assert.Equal(0, refresh.HomeLetturaSommario + refresh.HomeLetturaCopertura + refresh.HomeLetturaListe +
            refresh.HomeLetturaFiltri + refresh.HomeLetturaGrafici);
        Assert.Equal(1, refresh.HomeSondaIndice);
    }

    /// <summary>Con tutte le sezioni visibili ogni refresh calcola ciascuna sezione con una sola lettura: nessuna sonda.</summary>
    [Fact]
    public void SezioniVisibili_UnRefreshCalcolaOgniSezioneUnaVolta()
    {
        var home = new HomeViewModel(_state, _ => { });

        PerfContatori.Reset();
        home.Aggiorna();

        var letture = PerfContatori.LeggiSnapshot();
        Assert.Equal(1, letture.HomeLetturaSommario);
        Assert.Equal(1, letture.HomeLetturaCopertura);
        Assert.Equal(1, letture.HomeLetturaListe);
        Assert.Equal(1, letture.HomeLetturaFiltri);
        Assert.Equal(1, letture.HomeLetturaGrafici);
        Assert.Equal(0, letture.HomeSondaIndice);
    }

    /// <summary>Liste nascoste: nessuna lettura di Da fare/Fatti; alla visualizzazione una sola lettura e gli stessi elenchi
    /// di una Home completa.</summary>
    [Fact]
    public void Liste_NascosteNonLeggonoLaCoda_AllaVisualizzazioneUnaSolaLetturaStessiElenchi()
    {
        var preparazione = new HomeViewModel(_state, _ => { });
        preparazione.ImpostaSezioneVisibile(HomeSezioni.Liste, false);

        PerfContatori.Reset();
        var home = new HomeViewModel(_state, _ => { });
        Assert.Equal(0, PerfContatori.LeggiSnapshot().HomeLetturaListe);
        Assert.Empty(home.DaFare);

        PerfContatori.Reset();
        home.ImpostaSezioneVisibile(HomeSezioni.Liste, true);
        Assert.Equal(1, PerfContatori.LeggiSnapshot().HomeLetturaListe);

        var atteso = CreaRiferimento();
        Assert.True(atteso.DaFare.Count + atteso.Fatti.Count > 0, "Il seed demo deve avere Test in Da fare o Fatti.");
        Assert.Equal(atteso.TotaleDaFare, home.TotaleDaFare);
        Assert.Equal(atteso.TotaleFatti, home.TotaleFatti);
        Assert.Equal(atteso.DaFare.Select(t => t.Id), home.DaFare.Select(t => t.Id));
        Assert.Equal(atteso.Fatti.Select(t => t.Id), home.Fatti.Select(t => t.Id));
    }

    /// <summary>Riepilogo, Avvisi e KPI nascosti: il sommario non viene letto. Mostrando i KPI si calcolano sommario e copertura
    /// una sola volta e i valori coincidono con una Home completa, CR KPI inclusa.</summary>
    [Fact]
    public void Testata_NascostaPoiMostrata_CalcolaUnaVoltaEValoriCoincidonoConUnaHomeCompleta()
    {
        var preparazione = new HomeViewModel(_state, _ => { });
        preparazione.ImpostaSezioneVisibile(HomeSezioni.Riepilogo, false);
        preparazione.ImpostaSezioneVisibile(HomeSezioni.Avvisi, false);
        preparazione.ImpostaSezioneVisibile(HomeSezioni.Kpi, false);

        PerfContatori.Reset();
        var home = new HomeViewModel(_state, _ => { });
        var costruzione = PerfContatori.LeggiSnapshot();
        Assert.Equal(0, costruzione.HomeLetturaSommario);
        Assert.Equal(0, costruzione.HomeLetturaCopertura);

        PerfContatori.Reset();
        home.ImpostaSezioneVisibile(HomeSezioni.Kpi, true);
        var mostrato = PerfContatori.LeggiSnapshot();
        Assert.Equal(1, mostrato.HomeLetturaSommario);
        Assert.Equal(1, mostrato.HomeLetturaCopertura);

        var atteso = CreaRiferimento();
        Assert.Equal(atteso.TotaleTestCase, home.TotaleTestCase);
        Assert.Equal(atteso.TotaleNcAperte, home.TotaleNcAperte);
        Assert.Equal(atteso.ChangeRequestAperte, home.ChangeRequestAperte);
        Assert.Equal(atteso.DaClassificare, home.DaClassificare);
        Assert.Equal(atteso.DaRitestare, home.DaRitestare);
        Assert.Equal(atteso.MieiAttivi, home.MieiAttivi);
        Assert.Equal(atteso.TestSenzaEcu, home.TestSenzaEcu);
        Assert.Equal(atteso.TassoConformitaPercentuale, home.TassoConformitaPercentuale, 6);
        Assert.Equal(atteso.CoperturaRequisitiPercentuale, home.CoperturaRequisitiPercentuale, 6);
    }

    /// <summary>Un Test scritto da un'altra postazione mentre la testata e' nascosta: il refresh non legge nulla e, alla
    /// visualizzazione, i KPI mostrano il dato nuovo invece di un valore stantio.</summary>
    [Fact]
    public void DatoCambiatoMentreTestataNascosta_AllaVisualizzazioneMostraIlValoreNuovo()
    {
        var home = new HomeViewModel(_state, _ => { });
        var totalePrima = home.TotaleTestCase;
        var senzaEcuPrima = home.TestSenzaEcu;
        home.ImpostaSezioneVisibile(HomeSezioni.Riepilogo, false);
        home.ImpostaSezioneVisibile(HomeSezioni.Avvisi, false);
        home.ImpostaSezioneVisibile(HomeSezioni.Kpi, false);

        var configService2 = new AppConfigService(Path.Combine(_dir + "_local_B", "config.json"));
        using (var stato2 = new AppState(configService2, runBackgroundMaintenance: false))
        {
            stato2.InizializzaCartella(_dir);
            stato2.TestCases.Save(new TestCase
            {
                Id = "T-HOME-LAZY-NASCOSTA",
                ModificatoDa = "Seconda Postazione",
                DataCreazione = DateTime.Now,
                UltimaModifica = DateTime.Now,
            }, isNew: true);
        }
        _state.TestCases.RefreshIndex();

        PerfContatori.Reset();
        home.Aggiorna();
        Assert.Equal(0, PerfContatori.LeggiSnapshot().HomeLetturaSommario);

        home.ImpostaSezioneVisibile(HomeSezioni.Kpi, true);

        Assert.Equal(totalePrima + 1, home.TotaleTestCase);
        Assert.Equal(senzaEcuPrima + 1, home.TestSenzaEcu); // il Test appena scritto non ha componente
        var atteso = CreaRiferimento();
        Assert.Equal(atteso.TotaleTestCase, home.TotaleTestCase);
        Assert.Equal(atteso.TestSenzaEcu, home.TestSenzaEcu);
    }

    /// <summary>Grafici nascosti: ne' la dashboard ne' gli elenchi dei filtri (componente, campi custom) vengono letti. Alla
    /// visualizzazione una sola lettura per ciascuno e gli stessi valori di una Home completa.</summary>
    [Fact]
    public void Grafici_NascostiNonCaricanoFiltriNeDashboard_AllaVisualizzazioneUnaVolta()
    {
        var preparazione = new HomeViewModel(_state, _ => { });
        preparazione.ImpostaSezioneVisibile(HomeSezioni.Grafici, false);

        PerfContatori.Reset();
        var home = new HomeViewModel(_state, _ => { });
        var costruzione = PerfContatori.LeggiSnapshot();
        Assert.Equal(0, costruzione.HomeLetturaFiltri);
        Assert.Equal(0, costruzione.HomeLetturaGrafici);
        Assert.Empty(home.FiltroEcuDashboard.Opzioni);
        Assert.Empty(home.ChartsAttivi);

        PerfContatori.Reset();
        home.ImpostaSezioneVisibile(HomeSezioni.Grafici, true);
        var mostrato = PerfContatori.LeggiSnapshot();
        Assert.Equal(1, mostrato.HomeLetturaFiltri);
        Assert.Equal(1, mostrato.HomeLetturaGrafici);

        var atteso = CreaRiferimento();
        Assert.NotEmpty(atteso.FiltroEcuDashboard.Opzioni);
        Assert.Equal(atteso.FiltroEcuDashboard.Opzioni.Select(o => o.Valore), home.FiltroEcuDashboard.Opzioni.Select(o => o.Valore));
        Assert.Equal(atteso.TestCaseNelFiltro, home.TestCaseNelFiltro);
        Assert.Equal(atteso.KpiIterazioniTesto, home.KpiIterazioniTesto);
        Assert.Equal(atteso.ChartsAttivi.Count, home.ChartsAttivi.Count);
    }

    /// <summary>La selezione salvata di un filtro del grafici si riapplica quando il grafici torna visibile, anche se alla
    /// costruzione era nascosto: i cataloghi non c'erano ancora e la selezione non deve andare persa.</summary>
    [Fact]
    public void FiltroSalvato_ConGraficiNascostiAllApertura_SiRiapplicaAllaVisualizzazione()
    {
        var atteso = CreaRiferimento();
        var componente = atteso.FiltroEcuDashboard.Opzioni.Select(o => o.Valore).First();

        var precedente = new HomeViewModel(_state, _ => { });
        precedente.ImpostaSezioneVisibile(HomeSezioni.Grafici, false);
        ViewStateService.ScriviLista(_state, "Home.FiltroEcu", new[] { componente });

        var riapertura = new HomeViewModel(_state, _ => { });
        Assert.Empty(riapertura.FiltroEcuDashboard.Opzioni);

        riapertura.ImpostaSezioneVisibile(HomeSezioni.Grafici, true);
        Assert.Contains(componente, riapertura.FiltroEcuDashboard.Selezionati);
        Assert.True(riapertura.HaFiltriDashboardAttivi);
    }

    /// <summary>Il ripristino del layout predefinito rende visibile la testata: solo la copertura, che era rimasta da calcolare,
    /// viene letta; il sommario, sempre visibile dalla riapertura, non viene riletto.</summary>
    [Fact]
    public void RipristinoLayout_CalcolaSoloLeParti_DaCalcolareEValoriCorretti()
    {
        var preparazione = new HomeViewModel(_state, _ => { });
        preparazione.ImpostaSezioneVisibile(HomeSezioni.Kpi, false);

        var home = new HomeViewModel(_state, _ => { });
        PerfContatori.Reset();
        home.RipristinaLayoutPredefinitoCommand.Execute(null);

        var letture = PerfContatori.LeggiSnapshot();
        Assert.Equal(0, letture.HomeLetturaSommario);
        Assert.Equal(1, letture.HomeLetturaCopertura);
        var atteso = CreaRiferimento();
        Assert.Equal(atteso.CoperturaRequisitiPercentuale, home.CoperturaRequisitiPercentuale, 6);
        Assert.Equal(atteso.TotaleNcAperte, home.TotaleNcAperte);
    }

    /// <summary>Click-through del KPI Change request: la sezione KPI e' ancora collegata alla pagina Anomalie / CR dopo una
    /// visualizzazione lazy.</summary>
    [Fact]
    public void KpiChangeRequest_ClickThrough_RestaCollegatoDopoLaVisualizzazione()
    {
        var chiamate = 0;
        var home = new HomeViewModel(_state, _ => { }, vaiAAnomalie: () => chiamate++);
        home.ImpostaSezioneVisibile(HomeSezioni.Kpi, false);
        home.ImpostaSezioneVisibile(HomeSezioni.Kpi, true);

        home.VaiAAnomalieCommand.Execute(null);

        Assert.Equal(1, chiamate);
    }

    /// <summary>Indice corrotto con tutte le sezioni nascoste: la sola sonda del refresh rileva il guasto e avvia la ricostruzione,
    /// come farebbe il batch con le sezioni visibili (il banner non dipende dalle sezioni).</summary>
    [Fact]
    public void IndiceCorrotto_ConSezioniNascoste_LaSondaDelBannerRilevaIlGuasto()
    {
        var preparazione = new HomeViewModel(_state, _ => { });
        NascondiTutteTranneRecenti(preparazione);
        var home = new HomeViewModel(_state, _ => { });

        var coordinatore = Assert.IsType<LocalIndexRecoveryCoordinator>(_state.TestCases.IndexRecovery);
        var ricostruzioniPrima = coordinatore.RicostruzioniEseguite;
        var database = Assert.IsType<string>(_state.TestCases.LocalIndexDatabasePath);

        IndiceSqliteCorruzioneTestHelper.CheckpointWalEChiudiPool(database);
        using (var file = new FileStream(database, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            file.Position = 0;
            file.Write(new byte[100]);
            file.Flush(flushToDisk: true);
        }

        PerfContatori.Reset();
        home.Aggiorna();
        Assert.Equal(1, PerfContatori.LeggiSnapshot().HomeSondaIndice);

        Assert.True(coordinatore.AttendiRecupero(TimeSpan.FromSeconds(15)), coordinatore.Motivo);
        Assert.Equal(LocalIndexHealth.Healthy, coordinatore.Salute);
        Assert.Equal(ricostruzioniPrima + 1, coordinatore.RicostruzioniEseguite);
    }
}
