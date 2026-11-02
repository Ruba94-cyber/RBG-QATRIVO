using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Dialogo dettaglio NC: ogni input che cambia "Modifiche non salvate" (HaModificheNonSalvate) notifica la
/// view, anche per i campi del modello Nc che la view scrive direttamente (SegnalaModificaCampo), e l'indicatore si
/// spegne quando i valori tornano quelli salvati.</summary>
[Collection("Localization")]
public sealed class AnomaliaDettaglioModificheNonSalvateTests : IDisposable
{
    private const string Dirty = nameof(AnomaliaDettaglioViewModel.HaModificheNonSalvate);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_AnomDirty_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    private static void SalvaNc(AppState state, string testCaseId = "", List<RiferimentoProva>? prove = null)
    {
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-0001", TestCaseId = testCaseId, EsecuzioneIterazione = 1, Owner = "Mario", Descrizione = "KO",
            Severita = SeveritaAnomalia.Medium, StatoWorkflow = StatoAnomalia.Confermata, DataApertura = DateTime.Now,
            ProveCollegate = prove ?? new()
        });
    }

    /// <summary>Test T-1 con due condizioni e NC NC-0001 collegata alla prima: serve per la riga "Esito" del Test.</summary>
    private static void SalvaTestECollegamento(AppState state)
    {
        var c1 = new Condizione { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Diagnosi", Esito = Esito.NonConforme };
        var c2 = new Condizione { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Lettura", Esito = Esito.Conforme };
        var rev = new Esecuzione { Iterazione = 1, Data = new DateTime(2026, 9, 1), Operatore = "Mario", Matricola = "Matricola1", Condizioni = { c1, c2 } };
        rev.SincronizzaEsito();
        state.TestCases.Save(new TestCase
        {
            Id = "T-1", Componente = { "BCM" }, Veicolo = "V1", Veicoli = { "V1" }, Assegnatario = "Luigi",
            StatoLavoro = StatoLavoroTest.InCorso, Esecuzioni = new List<Esecuzione> { rev }
        }, isNew: true);
        SalvaNc(state, "T-1", new() { new RiferimentoProva { TestId = "T-1", EsecuzioneIterazione = 1, CondizioneUid = c1.Uid } });
    }

    private static AnomaliaDettaglioViewModel Apri(AppState state) => new(state, state.NonConformita.GetById("NC-0001")!);

    /// <summary>Nomi delle proprieta notificate: la view si aggiorna solo su queste notifiche.</summary>
    private static List<string> Osserva(AnomaliaDettaglioViewModel vm)
    {
        var notifiche = new List<string>();
        vm.PropertyChanged += (_, e) => notifiche.Add(e.PropertyName ?? "");
        return notifiche;
    }

    [Fact]
    public void NcAperta_SenzaModifiche_IndicatoreSpento()
    {
        using var state = CreaStato();
        SalvaNc(state);

        Assert.False(Apri(state).HaModificheNonSalvate);
    }

    [Fact]
    public void RiferimentoTicketNuovo_NotificaELAccendeESpegne()
    {
        using var state = CreaStato();
        SalvaNc(state);
        var vm = Apri(state);
        var notifiche = Osserva(vm);

        vm.NuovoTicketRiferimento = "TICKET-123";
        Assert.Contains(Dirty, notifiche);
        Assert.True(vm.HaModificheNonSalvate);

        notifiche.Clear();
        vm.NuovoTicketRiferimento = "";
        Assert.Contains(Dirty, notifiche);
        Assert.False(vm.HaModificheNonSalvate);
    }

    [Fact]
    public void MotivoChiusura_NotificaLIndicatore()
    {
        using var state = CreaStato();
        SalvaNc(state);
        var vm = Apri(state);
        var notifiche = Osserva(vm);

        vm.MotivoChiusura = "Risolta sul banco";

        Assert.Contains(Dirty, notifiche);
        Assert.True(vm.HaModificheNonSalvate);
    }

    [Fact]
    public void TestDaCollegare_SuNcSenzaTest_NotificaLIndicatore()
    {
        using var state = CreaStato();
        SalvaNc(state);
        var vm = Apri(state);
        var notifiche = Osserva(vm);

        vm.TestIdDaCollegare = "T-9";

        Assert.Contains(Dirty, notifiche);
        Assert.True(vm.HaModificheNonSalvate);
    }

    [Fact]
    public void CampoDelModelloNc_ScrittoDallaView_SegnalaLIndicatoreESiSpegneAlRipristino()
    {
        using var state = CreaStato();
        SalvaNc(state);
        var vm = Apri(state);
        var notifiche = Osserva(vm);

        // La view scrive direttamente nel modello Nc (TextBox legato a Nc.Owner): il ViewModel non riceve alcun evento
        // finche la view non chiama SegnalaModificaCampo.
        vm.Nc.Owner = "Luigi";
        Assert.DoesNotContain(Dirty, notifiche);
        vm.SegnalaModificaCampo();
        Assert.Contains(Dirty, notifiche);
        Assert.True(vm.HaModificheNonSalvate);

        notifiche.Clear();
        vm.Nc.Owner = "Mario";
        vm.SegnalaModificaCampo();
        Assert.Contains(Dirty, notifiche);
        Assert.False(vm.HaModificheNonSalvate);
    }

    [Fact]
    public void EsitoDiUnaRigaDelTestCollegato_NotificaLIndicatore()
    {
        using var state = CreaStato();
        SalvaTestECollegamento(state);
        var vm = Apri(state);
        Assert.False(vm.HaModificheNonSalvate);
        var notifiche = Osserva(vm);

        var riga = vm.Condizioni.First();
        riga.EsitoNuovo = riga.EsitoNuovo == Esito.Conforme ? Esito.NonConforme : Esito.Conforme;

        Assert.Contains(Dirty, notifiche);
        Assert.True(vm.HaModificheNonSalvate);
    }

    [Fact]
    public void CausaProbabileAltro_NotificaMostraCausaAltroELAccendeLIndicatore()
    {
        using var state = CreaStato();
        SalvaNc(state);
        var vm = Apri(state);
        Assert.False(vm.MostraCausaAltro);
        var notifiche = Osserva(vm);

        // La ComboBox scrive tramite CausaProbabileSelezionata: il campo causa libera deve comparire senza riaprire il dialogo.
        vm.CausaProbabileSelezionata = CausaProbabileUdt.Altro;

        Assert.Contains(nameof(AnomaliaDettaglioViewModel.MostraCausaAltro), notifiche);
        Assert.Contains(Dirty, notifiche);
        Assert.True(vm.MostraCausaAltro);
        Assert.Equal(CausaProbabileUdt.Altro, vm.Nc.CausaProbabile);

        notifiche.Clear();
        vm.CausaProbabileSelezionata = CausaProbabileUdt.Componente;

        Assert.Contains(nameof(AnomaliaDettaglioViewModel.MostraCausaAltro), notifiche);
        Assert.False(vm.MostraCausaAltro);
    }
}
