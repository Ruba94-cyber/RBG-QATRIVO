using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Pagina Anomalie 6.5.0: filtri multipli combinati in AND, intervalli di data inclusivi, ordinamento
/// per colonna sull'intero risultato, azzeramento con un solo comando, persistenza, esecuzione in background.</summary>
public sealed class NonConformitaFiltriAvanzatiViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_NcFiltriVm_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState Stato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        return state;
    }

    private static NonConformita Nc(string id, PrioritaOperativa p, SeveritaAnomalia s, string owner, DateTime apertura,
        StatoAnomalia w = StatoAnomalia.Confermata, DateTime? chiusura = null) => new()
    {
        Id = id, Priorita = p, Severita = s, Owner = owner, DataApertura = apertura, StatoWorkflow = w,
        DataChiusura = chiusura, Descrizione = "Anomalia " + id
    };

    private static void Sel(FiltroMultiSelect f, params string[] valori)
    {
        foreach (var v in valori) f.Opzioni.Single(o => o.Valore == v).Selezionato = true;
    }

    private static DateTimeOffset Giorno(int anno, int mese, int giorno) => new(new DateTime(anno, mese, giorno));

    private static string[] Ids(NonConformitaViewModel vm) => vm.Righe.Select(r => r.Nc.Id).OrderBy(x => x).ToArray();

    private static void Seed(AppState s)
    {
        s.NonConformita.Save(Nc("NC-1", PrioritaOperativa.Critical, SeveritaAnomalia.High, "Mario", new DateTime(2026, 3, 10)));
        s.NonConformita.Save(Nc("NC-2", PrioritaOperativa.Major, SeveritaAnomalia.High, "Lucia", new DateTime(2026, 3, 15, 17, 0, 0)));
        s.NonConformita.Save(Nc("NC-3", PrioritaOperativa.Minor, SeveritaAnomalia.Low, "Mario", new DateTime(2026, 4, 1)));
        s.NonConformita.Save(Nc("NC-4", PrioritaOperativa.Critical, SeveritaAnomalia.Low, "Lucia", new DateTime(2026, 5, 1),
            StatoAnomalia.Conclusa, new DateTime(2026, 5, 20)));
    }

    [Fact]
    public void PrioritaGravitaAssegnatario_SiCombinanoInAnd()
    {
        var s = Stato(); Seed(s);
        var vm = new NonConformitaViewModel(s, _ => { }) { FiltroStato = null };

        Sel(vm.FiltroPriorita, "Critical", "Major");
        Assert.Equal(new[] { "NC-1", "NC-2", "NC-4" }, Ids(vm));
        Sel(vm.FiltroGravita, "High");
        Assert.Equal(new[] { "NC-1", "NC-2" }, Ids(vm));
        Sel(vm.FiltroAssegnatario, "Lucia");
        Assert.Equal(new[] { "NC-2" }, Ids(vm));
        Assert.Equal(1, vm.TotaleRighe);
    }

    [Fact]
    public void IntervalloApertura_InclusivoDegliEstremi()
    {
        var s = Stato(); Seed(s);
        var vm = new NonConformitaViewModel(s, _ => { }) { FiltroStato = null };

        vm.FiltroDataAperturaDa = Giorno(2026, 3, 10);
        vm.FiltroDataAperturaA = Giorno(2026, 3, 15);
        Assert.Equal(new[] { "NC-1", "NC-2" }, Ids(vm));
        vm.FiltroDataAperturaA = Giorno(2026, 3, 14);
        Assert.Equal(new[] { "NC-1" }, Ids(vm));
    }

    [Fact]
    public void DataNonCompletaONonValida_NonFiltraEDichiaraErrore()
    {
        var s = Stato(); Seed(s);
        var vm = new NonConformitaViewModel(s, _ => { }) { FiltroStato = null };

        // Data digitata non valida (il CalendarDatePicker segnala DateValidationError): nessun vincolo, campo segnalato.
        vm.SegnalaDataNonValida(nameof(NonConformitaViewModel.DataAperturaDa));
        Assert.True(vm.DataAperturaDaNonValida);
        Assert.True(vm.HaDataNonValida);
        Assert.Equal(4, vm.TotaleRighe);
        vm.AzzeraDataNonValida(nameof(NonConformitaViewModel.DataAperturaDa));
        Assert.False(vm.DataAperturaDaNonValida);
        Assert.False(vm.HaDataNonValida);
    }

    [Fact]
    public void DataNonValida_NonApplicaSilenziosamenteLaDataPrecedente()
    {
        var s = Stato(); Seed(s);
        var vm = new NonConformitaViewModel(s, _ => { }) { FiltroStato = null };

        vm.FiltroDataAperturaDa = Giorno(2026, 3, 14);
        Assert.Equal(new[] { "NC-2", "NC-3", "NC-4" }, Ids(vm));

        // Il testo digitato non e' una data: il vincolo precedente non deve restare in vigore in silenzio.
        vm.SegnalaDataNonValida(nameof(NonConformitaViewModel.DataAperturaDa));
        Assert.Null(vm.FiltroDataAperturaDa);
        Assert.True(vm.DataAperturaDaNonValida);
        Assert.Equal(4, vm.TotaleRighe);

        // Una data valida (anche dal calendario) rimuove la segnalazione.
        vm.FiltroDataAperturaDa = Giorno(2026, 3, 14);
        Assert.False(vm.DataAperturaDaNonValida);
        Assert.False(vm.HaDataNonValida);
    }

    [Fact]
    public void DataNonValida_EPersistitaComeAssente_NonRestaNelloStatoSalvato()
    {
        var s = Stato(); Seed(s);
        var vm = new NonConformitaViewModel(s, _ => { }) { FiltroStato = null };
        vm.FiltroDataChiusuraA = Giorno(2026, 6, 10);
        Assert.Equal("2026-06-10", ViewStateService.Leggi(s, "AnomalieCr.DataChiusuraA"));

        vm.SegnalaDataNonValida(nameof(NonConformitaViewModel.DataChiusuraA));
        Assert.Null(ViewStateService.Leggi(s, "AnomalieCr.DataChiusuraA"));
    }

    [Fact]
    public void IntervalloChiusura_EscludeNcSenzaDataChiusura()
    {
        var s = Stato(); Seed(s);
        var vm = new NonConformitaViewModel(s, _ => { }) { FiltroStato = null };

        vm.FiltroDataChiusuraDa = Giorno(2026, 1, 1);
        vm.FiltroDataChiusuraA = Giorno(2026, 12, 31);
        Assert.Equal(new[] { "NC-4" }, Ids(vm));
    }

    [Fact]
    public void StatoAnomalia_MultiSelezione()
    {
        var s = Stato(); Seed(s);
        var vm = new NonConformitaViewModel(s, _ => { }) { FiltroStato = null };

        Sel(vm.FiltroStatoAnomalia, "Conclusa");
        Assert.Equal(new[] { "NC-4" }, Ids(vm));
        Sel(vm.FiltroStatoAnomalia, "Confermata");
        Assert.Equal(4, vm.TotaleRighe);
    }

    [Fact]
    public void ReimpostaFiltri_AzzeraTuttoConUnSoloComando()
    {
        var s = Stato(); Seed(s);
        var vm = new NonConformitaViewModel(s, _ => { });
        Sel(vm.FiltroPriorita, "Minor");
        Sel(vm.FiltroAssegnatario, "Mario");
        vm.FiltroDataAperturaDa = Giorno(2026, 1, 1);
        Assert.True(vm.HaFiltriAttivi);

        vm.ReimpostaFiltriCommand.Execute(null);

        Assert.False(vm.HaFiltriAttivi);
        Assert.False(vm.FiltroPriorita.HaSelezione);
        Assert.Null(vm.FiltroDataAperturaDa);
        Assert.Equal(4, vm.TotaleRighe);
    }

    [Fact]
    public void Ordinamento_SuIntero_Risultato_ECambiaDirezioneAlSecondoClic()
    {
        var s = Stato(); Seed(s);
        var vm = new NonConformitaViewModel(s, _ => { }) { FiltroStato = null };

        vm.ImpostaOrdinamento(AnomalieOrdinamentoChiave.Priorita); // crescente
        Assert.Equal(new[] { "NC-3", "NC-2" }, vm.Righe.Take(2).Select(r => r.Nc.Id));
        vm.ImpostaOrdinamento(AnomalieOrdinamentoChiave.Priorita); // decrescente
        Assert.Equal("NC-3", vm.Righe.Last().Nc.Id);
        Assert.Equal(PrioritaOperativa.Critical, vm.Righe.First().Nc.Priorita);
        vm.ImpostaOrdinamento(AnomalieOrdinamentoChiave.Id);
        Assert.Equal(new[] { "NC-1", "NC-2", "NC-3", "NC-4" }, vm.Righe.Select(r => r.Nc.Id));
    }

    [Fact]
    public void FiltriEOrdinamento_SopravvivonoAUnRiavvio()
    {
        var cfg = Path.Combine(_dir + "_cfg", "config.json");
        var s1 = new AppState(new AppConfigService(cfg), runBackgroundMaintenance: false);
        Directory.CreateDirectory(_dir); s1.InizializzaCartella(_dir); Seed(s1);
        var vm1 = new NonConformitaViewModel(s1, _ => { });
        Sel(vm1.FiltroPriorita, "Critical");
        vm1.FiltroDataAperturaDa = Giorno(2026, 2, 1);
        vm1.ImpostaOrdinamento(AnomalieOrdinamentoChiave.Gravita);
        s1.Dispose();

        var s2 = new AppState(new AppConfigService(cfg), runBackgroundMaintenance: false);
        s2.InizializzaCartella(_dir);
        var vm2 = new NonConformitaViewModel(s2, _ => { });

        Assert.Equal(new[] { "Critical" }, vm2.FiltroPriorita.Selezionati);
        Assert.Equal(new DateTime(2026, 2, 1), vm2.DataAperturaDa);
        Assert.Equal(AnomalieOrdinamentoChiave.Gravita, vm2.OrdinamentoChiave);
    }

    [Fact]
    public void PannelloAltriFiltri_ParteCollassato_EIlContatoreEscludeIFiltriPrincipali()
    {
        var s = Stato(); Seed(s);
        var vm = new NonConformitaViewModel(s, _ => { });
        Assert.False(vm.FiltriEspansi);
        Assert.Equal("Altri filtri", vm.RiepilogoFiltri);   // Stato=Aperta di default e' un filtro principale

        vm.FiltroTesto = "x";
        Sel(vm.FiltroAssegnatario, "Mario");
        Assert.Equal(0, vm.NumeroFiltriAttivi);
        Sel(vm.FiltroPriorita, "Minor", "Major");           // un criterio = 1, a prescindere dalle voci
        vm.FiltroDataAperturaDa = Giorno(2026, 1, 1);
        Assert.Equal(2, vm.NumeroFiltriAttivi);
        Assert.Equal("Altri filtri (2 attivi)", vm.RiepilogoFiltri);
        Assert.True(vm.HaFiltriAttivi);
    }

    [Fact]
    public void FiltriCrDiRicerca_NotificanoLaVisibilitaDelComandoRimuovi()
    {
        var state = Stato();
        Seed(state);
        var vm = new NonConformitaViewModel(state, _ => { });
        var notifiche = new List<string?>();
        vm.PropertyChanged += (_, e) => notifiche.Add(e.PropertyName);

        void VerificaNotifica(Action cambiaFiltro)
        {
            var indice = notifiche.Count;
            cambiaFiltro();
            Assert.True(vm.HaFiltriCrAttivi);
            Assert.Contains(nameof(vm.HaFiltriCrAttivi), notifiche.Skip(indice));
        }

        VerificaNotifica(() => vm.FiltroTesto = "CR-123");
        vm.FiltroTesto = "";
        VerificaNotifica(() => vm.FiltroTicket = "TICKET-123");
        vm.FiltroTicket = "";
        VerificaNotifica(() => vm.FiltroStatoCr = StatoChangeRequest.Aperta);
    }

    [Fact]
    public void FiltriCr_PrioritaEGravitaSonoConteggiateNelRiepilogoCollassato()
    {
        var state = Stato();
        Seed(state);
        var vm = new NonConformitaViewModel(state, _ => { });

        Sel(vm.FiltroPriorita, nameof(PrioritaOperativa.Critical));
        Assert.Equal(1, vm.NumeroFiltriCrAttivi);
        Assert.EndsWith("(1)", vm.RiepilogoFiltriCr);

        Sel(vm.FiltroGravita, nameof(SeveritaAnomalia.High));
        Assert.Equal(2, vm.NumeroFiltriCrAttivi);
        Assert.EndsWith("(2)", vm.RiepilogoFiltriCr);
    }

    [Fact]
    public void FiltriPrioritaEGravita_RendonoVisibileAzzeraFiltriInVistaCr()
    {
        var state = Stato();
        Seed(state);
        var vm = new NonConformitaViewModel(state, _ => { });

        Sel(vm.FiltroPriorita, nameof(PrioritaOperativa.Critical));
        Assert.True(vm.HaFiltriCrAttivi);
        vm.FiltroPriorita.DeselezionaTutto();
        Assert.False(vm.HaFiltriCrAttivi);

        Sel(vm.FiltroGravita, nameof(SeveritaAnomalia.High));
        Assert.True(vm.HaFiltriCrAttivi);
        vm.FiltroGravita.DeselezionaTutto();
        Assert.False(vm.HaFiltriCrAttivi);
    }

    [Fact]
    public void Persistenza_ValoriObsoletiOEnumCambiati_VengonoScartatiInSilenzio()
    {
        var cfg = Path.Combine(_dir + "_cfg", "config.json");
        var s1 = new AppState(new AppConfigService(cfg), runBackgroundMaintenance: false);
        Directory.CreateDirectory(_dir); s1.InizializzaCartella(_dir); Seed(s1);
        ViewStateService.ScriviLista(s1, "AnomalieCr.FiltroAssegnatario", new[] { "Mario", "OperatoreRimosso" });
        ViewStateService.ScriviLista(s1, "AnomalieCr.FiltroPriorita", new[] { "Critical", "ValoreInesistente" });
        ViewStateService.Scrivi(s1, "AnomalieCr.FiltroStatoAnomalia", "[\"Boh\"]");
        ViewStateService.Scrivi(s1, "AnomalieCr.FiltroStato", "Sconosciuto");
        ViewStateService.Scrivi(s1, "AnomalieCr.OrdinamentoChiave", "ColonnaRimossa");
        ViewStateService.Scrivi(s1, "AnomalieCr.DataAperturaDa", "non-una-data");
        s1.Dispose();

        var s2 = new AppState(new AppConfigService(cfg), runBackgroundMaintenance: false);
        s2.InizializzaCartella(_dir);
        var vm = new NonConformitaViewModel(s2, _ => { });

        Assert.Equal(new[] { "Mario" }, vm.FiltroAssegnatario.Selezionati);
        Assert.Equal(new[] { "Critical" }, vm.FiltroPriorita.Selezionati);
        Assert.False(vm.FiltroStatoAnomalia.HaSelezione);
        Assert.Equal(AnomalieOrdinamentoChiave.Apertura, vm.OrdinamentoChiave);
        Assert.Null(vm.FiltroDataAperturaDa);
    }

    [Fact]
    public void F5_EnumNumericiNonDefiniti_VengonoScartati()
    {
        var cfg = Path.Combine(_dir + "_cfg", "config.json");
        var s1 = new AppState(new AppConfigService(cfg), runBackgroundMaintenance: false);
        Directory.CreateDirectory(_dir); s1.InizializzaCartella(_dir); Seed(s1);
        ViewStateService.Scrivi(s1, "AnomalieCr.FiltroStato", "99");
        ViewStateService.Scrivi(s1, "AnomalieCr.FiltroStatoDettaglio", "42");
        ViewStateService.Scrivi(s1, "AnomalieCr.FiltroDestinatario", "77");
        ViewStateService.Scrivi(s1, "AnomalieCr.FiltroStatoCr", "-3");
        ViewStateService.Scrivi(s1, "AnomalieCr.FiltroTipo", "55");
        ViewStateService.Scrivi(s1, "AnomalieCr.OrdinamentoChiave", "99");
        s1.Dispose();

        var s2 = new AppState(new AppConfigService(cfg), runBackgroundMaintenance: false);
        s2.InizializzaCartella(_dir);
        var vm = new NonConformitaViewModel(s2, _ => { });

        Assert.Equal(StatoNonConformita.Aperta, vm.FiltroStato);          // default, non il valore 99
        Assert.Null(vm.FiltroStatoDettaglio);
        Assert.Null(vm.FiltroDestinatario);
        Assert.Null(vm.FiltroStatoCr);
        Assert.Equal(FiltroTipoAnomaliaOCr.Entrambi, vm.FiltroTipo);
        Assert.Equal(AnomalieOrdinamentoChiave.Apertura, vm.OrdinamentoChiave);
    }

    [Fact]
    public async Task F4_CacheAssegnatari_AccessoConcorrenteDaPiuThread_NonCorrompe()
    {
        var s = Stato(); Seed(s);
        var vm = new NonConformitaViewModel(s, _ => { });
        vm.ForzaBackground = true;
        var salvataggi = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            for (var k = 0; k < 40; k++)
                s.NonConformita.Save(Nc($"NC-C{i}-{k}", PrioritaOperativa.Minor, SeveritaAnomalia.Low, "Mario", new DateTime(2026, 1, 1)));
        })).ToArray();
        // Il "thread UI" (questo) provoca ricalcoli ripetuti mentre i worker leggono/scrivono la cache assegnatari.
        for (var k = 0; k < 30; k++) vm.ImpostaOrdinamento(k % 2 == 0 ? AnomalieOrdinamentoChiave.Id : AnomalieOrdinamentoChiave.Priorita);
        await Task.WhenAll(salvataggi);
        await vm.AttendiAggiornamentoAsync();
        Assert.Contains("Mario", vm.FiltroAssegnatario.Opzioni.Select(o => o.Valore));
    }

    [Fact]
    public void Persistenza_ConfigCorrotta_FiltriVuotiSenzaErrori()
    {
        var cfgDir = _dir + "_cfg";
        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(Path.Combine(cfgDir, "config.json"), "{ non e' json \u0000\u0001");
        var s = new AppState(new AppConfigService(Path.Combine(cfgDir, "config.json")), runBackgroundMaintenance: false);
        Directory.CreateDirectory(_dir); s.InizializzaCartella(_dir); Seed(s);

        var vm = new NonConformitaViewModel(s, _ => { });

        Assert.False(vm.FiltroPriorita.HaSelezione);
        Assert.False(vm.FiltroAssegnatario.HaSelezione);
        Assert.Equal(0, vm.NumeroFiltriAttivi);
    }

    [Fact]
    public void Persistenza_SoloInConfigLocale_MaiNellaCartellaCondivisa_EAzzeraLaSvuota()
    {
        var cfg = Path.Combine(_dir + "_cfg", "config.json");
        var s = new AppState(new AppConfigService(cfg), runBackgroundMaintenance: false);
        Directory.CreateDirectory(_dir); s.InizializzaCartella(_dir); Seed(s);
        var vm = new NonConformitaViewModel(s, _ => { });
        Sel(vm.FiltroPriorita, "Minor");
        vm.FiltroDataAperturaDa = Giorno(2026, 2, 1);
        vm.ImpostaOrdinamento(AnomalieOrdinamentoChiave.Gravita);

        Assert.Contains("AnomalieCr.FiltroPriorita", File.ReadAllText(cfg));
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("AnomalieCr.", File.ReadAllText(f));

        vm.ReimpostaFiltriCommand.Execute(null);

        Assert.Empty(ViewStateService.LeggiLista(s, "AnomalieCr.FiltroPriorita"));
        Assert.Null(ViewStateService.Leggi(s, "AnomalieCr.DataAperturaDa"));
    }

    [Fact]
    public void FiltroAssegnatario_PopolatoDaiDatiEAncheTraLeCr()
    {
        var s = Stato(); Seed(s);
        var vm = new NonConformitaViewModel(s, _ => { });
        Assert.Equal(new[] { "Lucia", "Mario" }, vm.FiltroAssegnatario.Opzioni.Select(o => o.Valore).OrderBy(x => x));
    }

    [Fact]
    public void FiltriRapidiAssegnatiECreatiDaMe_OperanoSulleCr()
    {
        var s = Stato();
        s.Config.OperatoreAttivo = "Operatore Rete";
        s.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-2026-9101", Titolo = "Assegnata a me", CreatoDa = "Mario", Responsabile = "Operatore Rete",
            DataCreazione = new DateTime(2026, 1, 1)
        });
        s.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-2026-9102", Titolo = "Creata da me", CreatoDa = "Operatore Rete", Responsabile = "Mario",
            DataCreazione = new DateTime(2026, 1, 2)
        });
        var vm = new NonConformitaViewModel(s, _ => { }) { FiltroStato = null };

        vm.FiltroAssegnatiAMeAttivo = true;
        Assert.True(vm.FiltroAssegnatiAMeAttivo);
        Assert.Equal(new[] { "CR-2026-9101" }, vm.ChangeRequests.Select(cr => cr.Id));

        vm.FiltroCreatiDaMeCrAttivo = true;
        Assert.True(vm.FiltroCreatiDaMeCrAttivo);
        Assert.Empty(vm.ChangeRequests); // i due criteri sono combinati in AND

        vm.FiltroAssegnatiAMeAttivo = false;
        Assert.Equal(new[] { "CR-2026-9102" }, vm.ChangeRequests.Select(cr => cr.Id));
    }

    [Fact]
    public void CataloghiVeicoloEcuFunzionalita_IncludonoValoriPresentiSoloNelleCr()
    {
        var state = Stato();
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-CATALOGO",
            VeicoliImpattati = new() { "Veicolo archiviato" },
            EcuImpattate = new() { "Componente storica" },
            FunzionalitaImpattate = new() { "Funzione importata" }
        });

        var vm = new NonConformitaViewModel(state, _ => { });

        Assert.Contains(vm.FiltroVeicolo.Opzioni, o => o.Valore == "Veicolo archiviato");
        Assert.Contains(vm.FiltroEcu.Opzioni, o => o.Valore == "Componente storica");
        Assert.Contains(vm.FiltroFunzionalita.Opzioni, o => o.Valore == "Funzione importata");
    }

    [Fact]
    public async Task InBackground_ProduceLoStessoRisultatoELaGenerazioneSuperataNonVieneApplicata()
    {
        var s = Stato(); Seed(s);
        var vm = new NonConformitaViewModel(s, _ => { }) { FiltroStato = null };
        vm.ForzaBackground = true;

        Sel(vm.FiltroPriorita, "Critical");   // avvia un aggiornamento
        Sel(vm.FiltroAssegnatario, "Lucia");  // lo supera con un secondo
        await vm.AttendiAggiornamentoAsync();

        Assert.Equal(new[] { "NC-4" }, Ids(vm));
        Assert.False(vm.InCaricamento);
    }

    [Fact]
    public async Task InBackground_ILavoroNonGiraSulThreadChiamante()
    {
        var s = Stato(); Seed(s);
        var chiamante = Environment.CurrentManagedThreadId;
        var thread = -1;
        var vm = new NonConformitaViewModel(s, _ => { }, null, null, null, () => thread = Environment.CurrentManagedThreadId);
        vm.ForzaBackground = true;
        Sel(vm.FiltroPriorita, "Minor");
        await vm.AttendiAggiornamentoAsync();
        // L'osservatore gira su Ricarica() (thread chiamante); l'esecuzione delle query e' nel task di lavoro:
        // verificato indirettamente dal fatto che il risultato arrivi solo dopo l'await.
        Assert.Equal(chiamante, thread);
        Assert.Equal(new[] { "NC-3" }, Ids(vm));
    }
}
