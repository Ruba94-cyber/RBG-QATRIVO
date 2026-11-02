using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Home personalizzabile (ordine e visibilita' delle sezioni) e sezione "Recenti": preferenze LOCALI al PC, salvate
/// in config.json. Ogni "riavvio" e' una nuova AppState sulla stessa config.json (vedi ViewStatePersistenceTests).
/// </summary>
/// <remarks>[Collection("Localization")]: legge le traduzioni di sezione dal dizionario condiviso, come le altre suite
/// che toccano la Home.</remarks>
[Collection("Localization")]
public sealed class HomePersonalizzazioneTests : IDisposable
{
    private const string TipoTest = RecentItemsService.TipoTest;
    private const string TipoChangeRequest = RecentItemsService.TipoChangeRequest;
    private const string TipoSegnalazioneRete = RecentItemsService.TipoSegnalazioneRete;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_HomePers_" + Guid.NewGuid().ToString("N"));
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "TestLoom_HomePers_Config_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_configDir, recursive: true); } catch { }
    }

    /// <summary>Una nuova "sessione" dell'app: stessa config.json locale, AppState nuova.</summary>
    private AppState NuovaSessione() =>
        new(new AppConfigService(Path.Combine(_configDir, "config.json")), runBackgroundMaintenance: false);

    private AppState CreaStatoComeMaster()
    {
        var state = NuovaSessione();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        return state;
    }

    // ------------------------------------------------------------ RecentItemsService

    [Fact]
    public void Recenti_NuovoElemento_EMessoInCima()
    {
        var state = CreaStatoComeMaster();

        RecentItemsService.Registra(state, TipoTest, "TC-1", "Primo");
        RecentItemsService.Registra(state, TipoTest, "TC-2", "Secondo");

        var elenco = RecentItemsService.Leggi(state);
        Assert.Equal(new[] { "TC-2", "TC-1" }, elenco.Select(e => e.Id));
        Assert.Equal("Secondo", elenco[0].Titolo);
    }

    [Fact]
    public void Recenti_StessoTipoEId_SpostaInCimaSenzaDuplicare()
    {
        var state = CreaStatoComeMaster();
        RecentItemsService.Registra(state, TipoTest, "TC-1", "A");
        RecentItemsService.Registra(state, TipoTest, "TC-2", "B");

        RecentItemsService.Registra(state, TipoTest, "TC-1", "A aggiornato");

        var elenco = RecentItemsService.Leggi(state);
        Assert.Equal(new[] { "TC-1", "TC-2" }, elenco.Select(e => e.Id));
        Assert.Equal("A aggiornato", elenco[0].Titolo);
    }

    [Fact]
    public void Recenti_StessoIdTipoDiverso_SonoDueVoci()
    {
        var state = CreaStatoComeMaster();

        RecentItemsService.Registra(state, TipoTest, "X-1", "come test");
        RecentItemsService.Registra(state, TipoChangeRequest, "X-1", "come change request");

        Assert.Equal(2, RecentItemsService.Leggi(state).Count);
    }

    [Fact]
    public void Recenti_OltreLaCapacita_RestanoSoloIQuindiciPiuRecenti()
    {
        var state = CreaStatoComeMaster();
        for (var i = 1; i <= 20; i++)
            RecentItemsService.Registra(state, TipoTest, $"TC-{i:D2}", "titolo " + i);

        var elenco = RecentItemsService.Leggi(state);

        Assert.Equal(RecentItemsService.Capacita, elenco.Count);
        Assert.Equal(15, RecentItemsService.Capacita);
        Assert.Equal("TC-20", elenco[0].Id);
        Assert.Equal("TC-06", elenco[^1].Id);
    }

    [Fact]
    public void Recenti_VociMalformate_VengonoSaltateSenzaPerdereLeAltre()
    {
        var state = CreaStatoComeMaster();
        ViewStateService.ScriviLista(state, RecentItemsService.ChiaveVista, new[]
        {
            "test|TC-1|Valida|100",
            "spazzatura",
            "xyz|ALTRO|tipo sconosciuto|200",
            "test||senza id|300",
            "test|TC-2|ticks non numerici|abc",
            "test|TC-3|ticks negativi|-5",
            "cr|CR-1|Valida CR|400",
            "cr|CR-1|duplicata piu' vecchia|50",
        });

        var elenco = RecentItemsService.Leggi(state);

        Assert.Equal(new[] { "CR-1", "TC-1" }, elenco.Select(e => e.Id));
        Assert.Equal("Valida CR", elenco[0].Titolo);
    }

    [Fact]
    public void Recenti_TitoloConSeparatoriOACapiRiga_NonRompeIlFormato()
    {
        var state = CreaStatoComeMaster();

        RecentItemsService.Registra(state, TipoSegnalazioneRete, "NC-1", "Parte A|parte B\nfine");

        var voce = Assert.Single(RecentItemsService.Leggi(state));
        Assert.Equal("NC-1", voce.Id);
        Assert.Equal("Parte A parte B fine", voce.Titolo);
    }

    [Fact]
    public void Recenti_IdONomeTipoNonValidi_NonVengonoRegistrati()
    {
        var state = CreaStatoComeMaster();

        RecentItemsService.Registra(state, TipoTest, "", "id vuoto");
        RecentItemsService.Registra(state, TipoTest, "A|B", "id con separatore");
        RecentItemsService.Registra(state, "sconosciuto", "TC-9", "tipo sconosciuto");

        Assert.Empty(RecentItemsService.Leggi(state));
        Assert.Empty(ViewStateService.LeggiLista(state, RecentItemsService.ChiaveVista));
    }

    [Fact]
    public void Recenti_SopravvivonoAlRiavvioDellApp()
    {
        var sessione1 = CreaStatoComeMaster();
        RecentItemsService.Registra(sessione1, TipoTest, "TC-1", "Persistito");
        sessione1.Dispose();

        var sessione2 = NuovaSessione();

        var elenco = RecentItemsService.Leggi(sessione2);
        var voce = Assert.Single(elenco);
        Assert.Equal("TC-1", voce.Id);
        Assert.Equal("Persistito", voce.Titolo);
    }

    // ------------------------------------------------------------ Home: layout (ordine e visibilita')

    [Fact]
    public void Layout_Predefinito_TutteLeSezioniVisibiliNellOrdineStorico()
    {
        var home = new HomeViewModel(CreaStatoComeMaster(), _ => { });

        Assert.Equal(new[] { "riepilogo", "avvisi", "kpi", "grafici", "liste", "recenti" }, home.OrdineSezioni);
        Assert.Equal(new[] { "riepilogo", "avvisi", "kpi", "grafici", "liste", "recenti" }, home.VociLayout.Select(v => v.Id));
        Assert.All(HomeSezioni.Predefinite, id => Assert.True(home.SezioneVisibile(id)));
    }

    [Fact]
    public void Layout_NascondiSezione_RestaNascostoDopoIlRiavvio()
    {
        var sessione1 = CreaStatoComeMaster();
        var home1 = new HomeViewModel(sessione1, _ => { });
        home1.ImpostaSezioneVisibile(HomeSezioni.Grafici, false);
        Assert.False(home1.SezioneVisibile(HomeSezioni.Grafici));
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var home2 = new HomeViewModel(sessione2, _ => { });

        Assert.False(home2.SezioneVisibile(HomeSezioni.Grafici));
        Assert.True(home2.SezioneVisibile(HomeSezioni.Kpi));
        Assert.False(home2.VociLayout.Single(v => v.Id == HomeSezioni.Grafici).Visibile);
        Assert.Equal(new[] { "riepilogo", "avvisi", "kpi", "grafici", "liste", "recenti" }, home2.OrdineSezioni);
    }

    [Fact]
    public void Layout_RiPubblicaSezione_TornaVisibileEVienePersistito()
    {
        var sessione1 = CreaStatoComeMaster();
        var home1 = new HomeViewModel(sessione1, _ => { });
        home1.ImpostaSezioneVisibile(HomeSezioni.Kpi, false);
        home1.ImpostaSezioneVisibile(HomeSezioni.Kpi, true);
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var home2 = new HomeViewModel(sessione2, _ => { });

        Assert.True(home2.SezioneVisibile(HomeSezioni.Kpi));
    }

    [Fact]
    public void Layout_SpostaSu_PersisteAlRiavvio()
    {
        var sessione1 = CreaStatoComeMaster();
        var home1 = new HomeViewModel(sessione1, _ => { });
        home1.SpostaSezione(HomeSezioni.Liste, -1);
        Assert.Equal(new[] { "riepilogo", "avvisi", "kpi", "liste", "grafici", "recenti" }, home1.OrdineSezioni);
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var home2 = new HomeViewModel(sessione2, _ => { });

        Assert.Equal(new[] { "riepilogo", "avvisi", "kpi", "liste", "grafici", "recenti" }, home2.OrdineSezioni);
        Assert.Equal(new[] { "riepilogo", "avvisi", "kpi", "liste", "grafici", "recenti" }, home2.VociLayout.Select(v => v.Id));
    }

    [Fact]
    public void Layout_SpostaGiu_PersisteAlRiavvio()
    {
        var sessione1 = CreaStatoComeMaster();
        var home1 = new HomeViewModel(sessione1, _ => { });
        home1.SpostaSezione(HomeSezioni.Riepilogo, 1);
        sessione1.Dispose();

        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var home2 = new HomeViewModel(sessione2, _ => { });

        Assert.Equal(new[] { "avvisi", "riepilogo", "kpi", "grafici", "liste", "recenti" }, home2.OrdineSezioni);
    }

    [Fact]
    public void Layout_SpostaOltreIiLimiti_NonCambiaNienteENonSalva()
    {
        var state = CreaStatoComeMaster();
        var home = new HomeViewModel(state, _ => { });

        home.SpostaSezione(HomeSezioni.Riepilogo, -1);
        home.SpostaSezione(HomeSezioni.Recenti, 1);
        home.SpostaSezione("sconosciuta", 1);

        Assert.Equal(HomeSezioni.Predefinite, home.OrdineSezioni);
        Assert.Empty(ViewStateService.LeggiLista(state, "Home.Layout"));
    }

    [Fact]
    public void Layout_Ripristina_RiportaIlPredefinitoERimuoveLaPreferenzaSalvata()
    {
        var state = CreaStatoComeMaster();
        var home = new HomeViewModel(state, _ => { });
        home.ImpostaSezioneVisibile(HomeSezioni.Avvisi, false);
        home.SpostaSezione(HomeSezioni.Recenti, -1);
        Assert.NotEmpty(ViewStateService.LeggiLista(state, "Home.Layout"));

        home.RipristinaLayoutPredefinito();

        Assert.Equal(HomeSezioni.Predefinite, home.OrdineSezioni);
        Assert.True(home.SezioneVisibile(HomeSezioni.Avvisi));
        Assert.Equal(HomeSezioni.Predefinite.Count, home.VociLayout.Count);
        Assert.Empty(ViewStateService.LeggiLista(state, "Home.Layout"));
    }

    [Fact]
    public void Layout_VociMalformateESconosciute_NonRompono_EUsanoIlPredefinitoPerLeMancanti()
    {
        var state = CreaStatoComeMaster();
        ViewStateService.ScriviLista(state, "Home.Layout", new[]
        {
            "liste:0", "sconosciuta:1", "kpi:forse", "riepilogo:1", "liste:1", "", "grafici",
        });

        var home = new HomeViewModel(state, _ => { });

        Assert.Equal(new[] { "liste", "riepilogo", "avvisi", "kpi", "grafici", "recenti" }, home.OrdineSezioni);
        Assert.False(home.SezioneVisibile(HomeSezioni.Liste));
        Assert.True(home.SezioneVisibile(HomeSezioni.Riepilogo));
    }

    [Fact]
    public void Layout_TogglePersonalizzazione_ApreELChiudeIlPannello()
    {
        var home = new HomeViewModel(CreaStatoComeMaster(), _ => { });
        Assert.False(home.MostraPersonalizzazione);

        home.TogglePersonalizzazioneCommand.Execute(null);
        Assert.True(home.MostraPersonalizzazione);

        home.TogglePersonalizzazioneCommand.Execute(null);
        Assert.False(home.MostraPersonalizzazione);
    }

    // ------------------------------------------------------------ Home: sezione "Recenti"

    [Fact]
    public void Recenti_LaHomeMostraSoloGliElementiCheEsistono_SenzaCancellarneGliAltri()
    {
        var state = CreaStatoComeMaster();
        state.TestCases.Save(new TestCase { Id = "TC-1", DescrizioneBreve = "Test esistente", Componente = new EcuSelection(new[] { "BCM" }) }, isNew: true);
        RecentItemsService.Registra(state, TipoTest, "TC-FANTASMA", "Non esiste piu'");
        RecentItemsService.Registra(state, TipoTest, "TC-1", "Titolo vecchio");

        var home = new HomeViewModel(state, _ => { });

        Assert.Equal(new[] { "TC-1" }, home.Recenti.Select(r => r.Id));
        Assert.Equal("Test esistente", home.Recenti[0].TitoloVisualizzato);
        Assert.False(home.RecentiVuoti);
        Assert.Equal(2, RecentItemsService.Leggi(state).Count);
    }

    [Fact]
    public void Recenti_ElencoVuoto_SegnalaLaSezioneVuota()
    {
        var home = new HomeViewModel(CreaStatoComeMaster(), _ => { });

        Assert.Empty(home.Recenti);
        Assert.True(home.RecentiVuoti);
    }

    [Fact]
    public void Recenti_ComandoApri_InvocaIlCallbackConTipoEId()
    {
        var state = CreaStatoComeMaster();
        state.ChangeRequests.Save(new ChangeRequest { Id = "CR-1", Titolo = "Modifica", CreatoDa = "Operatore Prova A" }, null);
        RecentItemsService.Registra(state, TipoChangeRequest, "CR-1", "Modifica");
        RecentItemsService.ElementoRecente? aperto = null;
        var home = new HomeViewModel(state, _ => { }, null, elemento => aperto = elemento);

        var voce = Assert.Single(home.Recenti);
        home.ApriRecenteCommand.Execute(voce);

        Assert.NotNull(aperto);
        Assert.Equal(TipoChangeRequest, aperto!.Tipo);
        Assert.Equal("CR-1", aperto.Id);
    }

    [Fact]
    public void Recenti_OrdinatiDalPiuRecente()
    {
        var state = CreaStatoComeMaster();
        state.TestCases.Save(new TestCase { Id = "TC-1", DescrizioneBreve = "uno", Componente = new EcuSelection(new[] { "BCM" }) }, isNew: true);
        state.TestCases.Save(new TestCase { Id = "TC-2", DescrizioneBreve = "due", Componente = new EcuSelection(new[] { "BCM" }) }, isNew: true);
        RecentItemsService.Registra(state, TipoTest, "TC-1", "uno");
        RecentItemsService.Registra(state, TipoTest, "TC-2", "due");

        var home = new HomeViewModel(state, _ => { });

        Assert.Equal(new[] { "TC-2", "TC-1" }, home.Recenti.Select(r => r.Id));
    }

    // ------------------------------------------------------------ Home: KPI "Change request aperte"

    [Fact]
    public void KpiChangeRequestAperte_ContaSoloLeNonArchiviateENonChiuse()
    {
        var state = CreaStatoComeMaster();
        state.ChangeRequests.Save(new ChangeRequest { Id = "CR-1", Titolo = "aperta", Stato = StatoChangeRequest.Aperta, CreatoDa = "Operatore Prova A" }, null);
        state.ChangeRequests.Save(new ChangeRequest { Id = "CR-2", Titolo = "validata", Stato = StatoChangeRequest.Validata, CreatoDa = "Operatore Prova A" }, null);
        state.ChangeRequests.Save(new ChangeRequest { Id = "CR-3", Titolo = "chiusa", Stato = StatoChangeRequest.Chiusa, CreatoDa = "Operatore Prova A" }, null);
        state.ChangeRequests.Save(new ChangeRequest { Id = "CR-4", Titolo = "annullata", Stato = StatoChangeRequest.Annullata, CreatoDa = "Operatore Prova A" }, null);
        state.ChangeRequests.Save(new ChangeRequest { Id = "CR-5", Titolo = "archiviata", Stato = StatoChangeRequest.Aperta, Archiviata = true, CreatoDa = "Operatore Prova A" }, null);

        var home = new HomeViewModel(state, _ => { });

        Assert.Equal(2, home.ChangeRequestAperte);
        Assert.Equal(2, state.ChangeRequests.CountAperte());
    }

    [Fact]
    public void KpiChangeRequestAperte_ConteggioSQLite_EscludeArchiviateEChiuseSenzaDeserializzare()
    {
        // Indice dedicato in cartelle temporanee: verifica il conteggio SQL direttamente, senza fallback JSON.
        var cartella = Path.Combine(_dir, "condivisa");
        Directory.CreateDirectory(cartella);
        var indice = new LocalEntityIndexStore(cartella, Path.Combine(_configDir, "indice-locale"));
        void Inserisci(string id, StatoChangeRequest stato, bool archiviata)
        {
            var cr = new ChangeRequest { Id = id, Titolo = id, Stato = stato, Archiviata = archiviata, CreatoDa = "Operatore Prova A" };
            indice.Upsert(ChangeRequestRepository.LocalEntityKind, Path.Combine(cartella, id + ".json"), DateTime.UtcNow, 100, cr,
                x => x.Id, x => x.Uid, archived: archiviata, modified: cr.UltimaModifica);
        }
        Inserisci("CR-1", StatoChangeRequest.Aperta, archiviata: false);
        Inserisci("CR-2", StatoChangeRequest.Implementata, archiviata: false);
        Inserisci("CR-3", StatoChangeRequest.Chiusa, archiviata: false);
        Inserisci("CR-4", StatoChangeRequest.Annullata, archiviata: false);
        Inserisci("CR-5", StatoChangeRequest.Aperta, archiviata: true);

        var aperte = indice.CountJsonTextNotIn(ChangeRequestRepository.LocalEntityKind, "Stato",
            new[] { nameof(StatoChangeRequest.Chiusa), nameof(StatoChangeRequest.Annullata) }, archived: false);

        Assert.Equal(2, aperte);
        Assert.Equal(5, indice.Count(ChangeRequestRepository.LocalEntityKind));
    }

    [Fact]
    public void KpiChangeRequestAperte_ComandoVaiAAnomalie_InvocaIlCallback()
    {
        var chiamato = 0;
        var home = new HomeViewModel(CreaStatoComeMaster(), _ => { }, null, null, () => chiamato++);

        home.VaiAAnomalieCommand.Execute(null);

        Assert.Equal(1, chiamato);
    }
}
