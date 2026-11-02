using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Notifiche configurabili per PC/operatore (Impostazioni: "Mostra nel contatore" e "Abilita notifiche Windows").
/// Il badge e l'avviso Windows escludono i tipi silenziati; centro e repository no. Il conflitto di sincronizzazione
/// dati non è mai silenziabile.</summary>
public sealed class NotificaPreferenzeTests : IDisposable
{
    private const string Operatore = "Operatore Prova A";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_NotificaPreferenze_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Operatori.Save(new List<Operatore> { TestOperatori.Sigilla(new() { Nome = Operatore, Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash("1234") }) });
        Assert.True(state.ImpostaOperatore(Operatore, "1234"));
        // Il tour guidato rimanda il popup notifiche: i test partono da un operatore che lo ha già completato.
        OnboardingService.SegnaCompletato(state.Config, Operatore, RuoloOperatore.Standard);
        return state;
    }

    private static NotificaApp Nota(string tipo, bool letta = false) =>
        new() { Destinatario = Operatore, Tipo = tipo, Titolo = "Titolo", Messaggio = "Messaggio", Letta = letta };

    [Fact]
    public void TipoSilenziato_NonEntraNelBadge_EPuoEssereRiattivato()
    {
        var cfg = new AppConfig();
        var notifiche = new List<NotificaApp> { Nota("Assegnazione"), Nota("ChangeRequest"), Nota("Assegnazione", letta: true) };

        NotificaPreferenzeService.ImpostaMostraNelContatore(cfg, "Assegnazione", false);
        Assert.Equal(1, NotificaPreferenzeService.NonLetteRilevanti(notifiche, cfg));
        Assert.False(NotificaPreferenzeService.ConteggiaNelBadge(notifiche[0], cfg));
        Assert.True(NotificaPreferenzeService.ConteggiaNelBadge(notifiche[1], cfg));

        NotificaPreferenzeService.ImpostaMostraNelContatore(cfg, "Assegnazione", true);
        Assert.Empty(cfg.NotificheTipiSilenziati);
        Assert.Equal(2, NotificaPreferenzeService.NonLetteRilevanti(notifiche, cfg));
    }

    [Fact]
    public void ConflittoSincronizzazione_NonEMaiSilenziabile()
    {
        var cfg = new AppConfig();
        const string tipo = NotificaPreferenzeService.TipoConflittoSincronizzazione;
        Assert.False(NotificaPreferenzeService.ESilenziabile(tipo));
        Assert.False(NotificaPreferenzeService.ESilenziabile("conflittosincronizzazione"));
        Assert.True(NotificaPreferenzeService.ESilenziabile("Assegnazione"));

        NotificaPreferenzeService.ImpostaMostraNelContatore(cfg, tipo, false);
        Assert.Empty(cfg.NotificheTipiSilenziati);

        // Anche una voce scritta a mano nel config.json non nasconde il conflitto.
        cfg.NotificheTipiSilenziati.Add(tipo);
        var conflitto = Nota(tipo);
        Assert.True(NotificaPreferenzeService.ConteggiaNelBadge(conflitto, cfg));
        Assert.Equal(1, NotificaPreferenzeService.NonLetteRilevanti(new[] { conflitto }, cfg));
    }

    [Fact]
    public void TipiNoti_CopronoITipiRealmenteGenerati()
    {
        var attesi = new[] { "ConflittoSincronizzazione", "Conflitto", "ChangeRequest", "Assegnazione", "Classificazione", "WuDaDecidere", "Informazione" };

        Assert.Equal(attesi.OrderBy(t => t), NotificaPreferenzeService.TipiNoti.Select(t => t.Tipo).OrderBy(t => t));
        Assert.All(NotificaPreferenzeService.TipiNoti, t => Assert.False(string.IsNullOrWhiteSpace(t.Etichetta)));
    }

    [Fact]
    public void AvvisoWindows_DisattivatoDiDefault()
    {
        Assert.False(new AppConfig().NotificheWindows);
    }

    [Fact]
    public void ConfigSenzaCampoNuovo_SiCaricaConListaVuota()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");
        // Formato precedente a NotificheTipiSilenziati: il campo manca del tutto nel file.
        File.WriteAllText(path, "{\r\n  \"CartellaCondivisa\": \"\",\r\n  \"OperatoreAttivo\": \"" + Operatore + "\",\r\n  \"NotificheWindows\": true,\r\n  \"Lingua\": \"it\"\r\n}");

        var cfg = new AppConfigService(path).Load();

        Assert.NotNull(cfg.NotificheTipiSilenziati);
        Assert.Empty(cfg.NotificheTipiSilenziati);
        Assert.True(cfg.NotificheWindows);
    }

    [Fact]
    public void SceltaSalvata_SiRiapplicaDopoIlRiavvio()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");
        var service = new AppConfigService(path);
        var cfg = service.Load();
        NotificaPreferenzeService.ImpostaMostraNelContatore(cfg, "ChangeRequest", false);
        service.Save(cfg);

        var riletta = new AppConfigService(path).Load();

        Assert.Equal(new[] { "ChangeRequest" }, riletta.NotificheTipiSilenziati);
    }

    [Fact]
    public void NotificheSilenziate_RestanoNelRepositoryENelCentro_MaNonNelBadge()
    {
        var state = CreaStato();
        state.Notifiche.Add(Nota("Assegnazione"));
        state.Notifiche.Add(Nota("ChangeRequest"));
        NotificaPreferenzeService.ImpostaMostraNelContatore(state.Config, "Assegnazione", false);

        var main = new MainViewModel(state);

        Assert.Equal(2, state.Notifiche.GetAll(Operatore).Count);
        Assert.Equal(2, main.NotificheRecenti.Count);
        Assert.Contains(main.NotificheRecenti, n => n.Tipo == "Assegnazione");
        Assert.Equal(1, main.NotificheNonLette);
    }

    [Fact]
    public void AvvisoDiAumento_SoloPerNotificheCheContanoNelBadge()
    {
        var state = CreaStato();
        NotificaPreferenzeService.ImpostaMostraNelContatore(state.Config, "Assegnazione", false);
        var main = new MainViewModel(state);
        var avvisi = new List<(int Precedente, int Nuovo)>();
        main.ConteggioNotificheRilevantiCambiato += (precedente, nuovo) => avvisi.Add((precedente, nuovo));

        // Un aumento vero è un avviso con precedente noto e nuovo maggiore: solo quello fa lampeggiare la barra.
        List<(int Precedente, int Nuovo)> Aumenti() => avvisi.Where(a => a.Precedente >= 0 && a.Nuovo > a.Precedente).ToList();

        // Una notifica di tipo silenziato non è un aumento del conteggio rilevante.
        state.Notifiche.Add(Nota("Assegnazione"));
        state.NotificaDatiRicaricati();
        Assert.Empty(Aumenti());
        Assert.Equal(0, main.NotificheNonLette);

        // Una notifica che conta: aumento da 0 a 1.
        state.Notifiche.Add(Nota("ChangeRequest"));
        state.NotificaDatiRicaricati();
        Assert.Equal((0, 1), Assert.Single(Aumenti()));
        Assert.Equal(1, main.NotificheNonLette);

        // Il conflitto di sincronizzazione dati conta sempre: secondo aumento.
        state.Notifiche.Add(Nota(NotificaPreferenzeService.TipoConflittoSincronizzazione));
        state.NotificaDatiRicaricati();
        Assert.Equal(new[] { (0, 1), (1, 2) }, Aumenti());

        // Segna tutte come lette: l'ultimo avviso è un decremento a zero, non un aumento.
        main.SegnaTutteNotificheLetteCommand.Execute(null);
        Assert.Equal(0, main.NotificheNonLette);
        Assert.Equal((2, 0), avvisi[avvisi.Count - 1]);
        Assert.Equal(2, Aumenti().Count);
    }

    [Fact]
    public void Impostazioni_ConflittoNonDisattivabile_ESceltaSalvataConSalvaPreferenze()
    {
        var state = CreaStato();
        var vm = new ImpostazioniViewModel(state, () => { });

        var assegnazione = vm.TipiNotificaContatore.Single(v => v.Tipo == "Assegnazione");
        var conflitto = vm.TipiNotificaContatore.Single(v => v.Tipo == NotificaPreferenzeService.TipoConflittoSincronizzazione);

        Assert.Equal("Preferenze_NotificaTipo_Assegnazione_CheckBox", assegnazione.AutomationId);
        Assert.True(assegnazione.MostraNelContatore);
        Assert.False(conflitto.Modificabile);
        Assert.True(conflitto.MostraNelContatore);

        assegnazione.MostraNelContatore = false;
        conflitto.MostraNelContatore = false; // ignorato: il tipo non è silenziabile
        Assert.Equal(new[] { "Assegnazione" }, state.Config.NotificheTipiSilenziati);
        Assert.True(conflitto.MostraNelContatore);

        vm.SalvaPreferenzeLocaliCommand.Execute(null);
        var salvata = new AppConfigService(Path.Combine(state.ConfigService.CartellaLocale, "config.json")).Load();
        Assert.Equal(new[] { "Assegnazione" }, salvata.NotificheTipiSilenziati);
    }
}
