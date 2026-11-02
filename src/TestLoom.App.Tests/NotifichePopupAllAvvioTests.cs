using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Richiesta utente (2026-09-18): "aggiungi anche un pop-up che mostri le notifiche non lette
/// all'avvio dell'applicazione". Riusa il centro notifiche già esistente (MostraNotifiche, finora apribile
/// solo dal campanello) invece di un dialog nuovo, mostrato in automatico una sola volta per avvio.</summary>
public sealed class NotifichePopupAllAvvioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_NotifichePopup_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private AppState CreaStato(string operatore = "Operatore Prova A", bool tourCompletato = true)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Operatori.Save(new List<Operatore> { TestOperatori.Sigilla(new() { Nome = operatore, Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash("1234") }) });
        Assert.True(state.ImpostaOperatore(operatore, "1234"));
        // Il tour guidato parte al primo accesso e rimanda il popup notifiche (G2 6.4.5): i test sul solo popup
        // partono da un operatore che ha gia' completato il tour.
        if (tourCompletato) OnboardingService.SegnaCompletato(state.Config, operatore, RuoloOperatore.Standard);
        return state;
    }

    [Fact]
    public void ConNotificaNonLetta_SiApreDaSolaAllaCostruzione()
    {
        var state = CreaStato();
        state.Notifiche.Add(new NotificaApp { Destinatario = "Operatore Prova A", Titolo = "Ciao", Messaggio = "Prova", Letta = false });

        var main = new MainViewModel(state);

        Assert.True(main.MostraNotifiche);
        Assert.Equal(1, main.NotificheNonLette);
    }

    [Fact]
    public void SenzaNotificheNonLette_NonSiApreDaSola()
    {
        var state = CreaStato();
        state.Notifiche.Add(new NotificaApp { Destinatario = "Operatore Prova A", Titolo = "Ciao", Messaggio = "Prova", Letta = true });

        var main = new MainViewModel(state);

        Assert.False(main.MostraNotifiche);
    }

    /// <summary>Non deve riaprirsi ad ogni refresh periodico: AggiornaStatoBarra() viene richiamato molto più
    /// spesso di quanto sembri (timer ogni minuto, AppState.DatiRicaricati, cambio lingua) — solo la prima
    /// volta dopo l'apertura dell'app.</summary>
    [Fact]
    public void ChiusaManualmente_NonSiRiapreAlProssimoAggiornamentoStatoBarra()
    {
        var state = CreaStato();
        state.Notifiche.Add(new NotificaApp { Destinatario = "Operatore Prova A", Titolo = "Ciao", Messaggio = "Prova", Letta = false });
        var main = new MainViewModel(state);
        Assert.True(main.MostraNotifiche);

        main.ChiudiNotificheCommand.Execute(null);
        Assert.False(main.MostraNotifiche);

        // Simula un refresh periodico/da AppState.DatiRicaricati: MostraNotifiche deve restare chiusa.
        state.NotificaDatiRicaricati();
        Assert.False(main.MostraNotifiche);
    }

    [Fact]
    public void SegnaTutteLette_AzzeraIlContatoreEIlPopupNonSiRiapre()
    {
        var state = CreaStato();
        state.Notifiche.Add(new NotificaApp { Destinatario = "Operatore Prova A", Titolo = "Ciao", Messaggio = "Prova", Letta = false });
        var main = new MainViewModel(state);
        Assert.True(main.MostraNotifiche);

        main.SegnaTutteNotificheLetteCommand.Execute(null);
        main.ChiudiNotificheCommand.Execute(null);

        Assert.Equal(0, main.NotificheNonLette);
        state.NotificaDatiRicaricati();
        Assert.False(main.MostraNotifiche);
    }

    /// <summary>G2 (6.4.5): con il tour in corso il popup notifiche non deve sovrapporsi; compare quando il tour
    /// finisce (o viene saltato), una sola volta.</summary>
    [Fact]
    public void ConTourInCorso_LeNotificheNonSiApronoSubitoEComparisconoAFineTour()
    {
        var state = CreaStato(tourCompletato: false);
        state.Notifiche.Add(new NotificaApp { Destinatario = "Operatore Prova A", Titolo = "Ciao", Messaggio = "Prova", Letta = false });

        var main = new MainViewModel(state);

        Assert.True(main.MostraOnboarding);
        Assert.False(main.MostraNotifiche);

        main.SaltaOnboardingCommand.Execute(null);

        Assert.False(main.MostraOnboarding);
        Assert.True(main.MostraNotifiche);
        Assert.Equal(1, main.NotificheNonLette);
    }

    [Fact]
    public void EscChiudeIlPannelloNotifiche()
    {
        var state = CreaStato();
        state.Notifiche.Add(new NotificaApp { Destinatario = "Operatore Prova A", Titolo = "Ciao", Messaggio = "Prova", Letta = false });
        var main = new MainViewModel(state);
        Assert.True(main.MostraNotifiche);

        Assert.True(main.ChiudiOverlayDaTastiera());

        Assert.False(main.MostraNotifiche);
    }
}
