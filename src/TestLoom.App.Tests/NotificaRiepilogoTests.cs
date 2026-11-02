using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using ModoRiepilogo = TestLoom.App.Services.NotificaRiepilogoService.ModoRiepilogo;

namespace TestLoom.App.Tests;

/// <summary>
/// Riepilogo notifiche (6.6.0): raggruppamento per tipo, tipi sempre immediati, pianificazione all'avvio con orologio
/// iniettato, badge con una sola voce di riepilogo, nessuna eliminazione o lettura automatica, "segna come letto" limitato
/// al riepilogo e configurazione compatibile con i config.json precedenti.
/// </summary>
public sealed class NotificaRiepilogoTests : IDisposable
{
    private const string Operatore = "Operatore Prova Riepilogo";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_NotificaRiepilogo_" + Guid.NewGuid().ToString("N"));

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

    /// <summary>Istante locale del 2026-10-<paramref name="giorno"/>: NotificaApp.Data è in ora locale (DateTime.Now).</summary>
    private static DateTime L(int giorno, int ore, int minuti) => new(2026, 10, giorno, ore, minuti, 0, DateTimeKind.Local);

    private static DateTimeOffset O(int giorno, int ore, int minuti) => new(L(giorno, ore, minuti));

    private static NotificaApp Nota(string tipo, DateTime data, bool letta = false) =>
        new() { Destinatario = Operatore, Tipo = tipo, Titolo = "Titolo " + tipo, Messaggio = "Messaggio", Data = data, Letta = letta };

    private static AppConfig Cfg(ModoRiepilogo modo, params string[] esclusi) =>
        new() { NotificheRiepilogoModo = NotificaRiepilogoService.NomeModo(modo), NotificheRiepilogoTipiEsclusi = esclusi.ToList() };

    [Fact]
    public void Pianificazione_Giornaliero_EmetteUnaVoltaPerGiornoLocale()
    {
        Assert.True(NotificaRiepilogoService.DeveEmettere(ModoRiepilogo.Giornaliero, null, O(11, 8, 0)));
        Assert.False(NotificaRiepilogoService.DeveEmettere(ModoRiepilogo.Giornaliero, O(11, 8, 0), O(11, 17, 30)));
        Assert.True(NotificaRiepilogoService.DeveEmettere(ModoRiepilogo.Giornaliero, O(10, 23, 0), O(11, 0, 5)));
    }

    [Fact]
    public void Pianificazione_AllAvvio_EmetteAOgniAvvio_EDisattivatoMai()
    {
        Assert.True(NotificaRiepilogoService.DeveEmettere(ModoRiepilogo.AllAvvio, O(11, 8, 0), O(11, 8, 1)));
        Assert.True(NotificaRiepilogoService.DeveEmettere(ModoRiepilogo.AllAvvio, null, O(11, 8, 1)));
        Assert.False(NotificaRiepilogoService.DeveEmettere(ModoRiepilogo.Disattivato, null, O(11, 8, 1)));
        Assert.False(NotificaRiepilogoService.DeveEmettere(ModoRiepilogo.Disattivato, O(10, 8, 0), O(11, 8, 1)));
    }

    [Fact]
    public void Modo_ValoriAssentiOSconosciuti_SonoDisattivato_IgnoraMaiuscole()
    {
        Assert.Equal(ModoRiepilogo.Disattivato, NotificaRiepilogoService.Modo(new AppConfig()));
        Assert.Equal(ModoRiepilogo.Disattivato, NotificaRiepilogoService.Modo(new AppConfig { NotificheRiepilogoModo = "Settimanale" }));
        Assert.Equal(ModoRiepilogo.Giornaliero, NotificaRiepilogoService.Modo(new AppConfig { NotificheRiepilogoModo = " giornaliero " }));
        Assert.Equal(ModoRiepilogo.AllAvvio, NotificaRiepilogoService.Modo(new AppConfig { NotificheRiepilogoModo = "AllAvvio" }));
    }

    [Fact]
    public void Calcola_RaggruppaPerTipo_SoloNonLetteRiepilogabiliArrivateEntroIlRiepilogo()
    {
        var cfg = Cfg(ModoRiepilogo.Giornaliero);
        var notifiche = new List<NotificaApp>
        {
            Nota("ChangeRequest", L(10, 10, 0)),
            Nota("ChangeRequest", L(11, 8, 0)),
            Nota("Assegnazione", L(11, 8, 30)),
            Nota("Conflitto", L(11, 8, 45)),
            Nota(NotificaPreferenzeService.TipoConflittoSincronizzazione, L(11, 8, 10)),   // sempre immediata
            Nota(NotificaPreferenzeService.TipoWuDaDecidere, L(11, 8, 20)),                // sempre immediata
            Nota("Informazione", L(11, 8, 50), letta: true),                              // già letta
            Nota("ChangeRequest", L(11, 9, 30)),                                          // arrivata dopo il riepilogo
        };

        var riepilogo = NotificaRiepilogoService.Calcola(notifiche, cfg, O(11, 9, 0));

        Assert.NotNull(riepilogo);
        Assert.Equal(4, riepilogo!.Elementi.Count);
        Assert.DoesNotContain(riepilogo.Elementi, n => n.Tipo == NotificaPreferenzeService.TipoConflittoSincronizzazione);
        Assert.DoesNotContain(riepilogo.Elementi, n => n.Tipo == NotificaPreferenzeService.TipoWuDaDecidere);
        Assert.DoesNotContain(riepilogo.Elementi, n => n.Letta);
        var primo = riepilogo.PerTipo[0];
        Assert.Equal(("ChangeRequest", "Change request", 2), (primo.Tipo, primo.Etichetta, primo.Conteggio));
        Assert.Equal(new[] { "Assegnazione", "Conflitto" }, riepilogo.PerTipo.Skip(1).Select(v => v.Tipo).OrderBy(t => t).ToArray());
    }

    [Fact]
    public void TipiCriticiNonEntranoMai_NeanchePerConfigModificatoAMano_NeSilenziatiNeEsclusi()
    {
        var cfg = Cfg(ModoRiepilogo.Giornaliero);
        cfg.NotificheRiepilogoTipiEsclusi.Clear();
        NotificaRiepilogoService.ImpostaIncluso(cfg, NotificaPreferenzeService.TipoWuDaDecidere, true);
        cfg.NotificheRiepilogoTipiEsclusi.Add(NotificaPreferenzeService.TipoConflittoSincronizzazione);

        Assert.True(NotificaRiepilogoService.ESempreImmediato(NotificaPreferenzeService.TipoConflittoSincronizzazione));
        Assert.True(NotificaRiepilogoService.ESempreImmediato(NotificaPreferenzeService.TipoWuDaDecidere));
        Assert.False(NotificaRiepilogoService.EnterRiepilogo(NotificaPreferenzeService.TipoConflittoSincronizzazione, cfg));
        Assert.False(NotificaRiepilogoService.EnterRiepilogo(NotificaPreferenzeService.TipoWuDaDecidere, cfg));

        // Un tipo silenziato nel contatore non entra nel riepilogo, un tipo escluso sì: nessuno dei due conta una per una.
        NotificaPreferenzeService.ImpostaMostraNelContatore(cfg, "Assegnazione", false);
        Assert.False(NotificaRiepilogoService.EnterRiepilogo("Assegnazione", cfg));
        Assert.True(NotificaRiepilogoService.EnterRiepilogo("ChangeRequest", cfg));
        NotificaRiepilogoService.ImpostaIncluso(cfg, "ChangeRequest", false);
        Assert.False(NotificaRiepilogoService.EnterRiepilogo("ChangeRequest", cfg));
        NotificaRiepilogoService.ImpostaIncluso(cfg, "ChangeRequest", true);
        Assert.True(NotificaRiepilogoService.EnterRiepilogo("ChangeRequest", cfg));
    }

    [Fact]
    public void Badge_SenzaRiepilogoInVigore_EUgualeAlConteggioDiSempre()
    {
        var notifiche = new List<NotificaApp>
        {
            Nota("ChangeRequest", L(11, 8, 0)),
            Nota("ChangeRequest", L(11, 8, 10)),
            Nota("Conflitto", L(11, 8, 20)),
            Nota("Informazione", L(11, 8, 30), letta: true),
        };
        var atteso = NotificaPreferenzeService.NonLetteRilevanti(notifiche, new AppConfig());

        Assert.Equal(3, atteso);
        // Disattivato, anche con un istante di riepilogo già salvato: nessuna differenza rispetto a prima della funzione.
        Assert.Equal(atteso, NotificaRiepilogoService.ConteggioBadge(notifiche, Cfg(ModoRiepilogo.Disattivato), O(11, 9, 0)));
        // Giornaliero ma nessun riepilogo ancora emesso: ogni notifica conta come sempre, nulla sparisce dal badge.
        Assert.Equal(atteso, NotificaRiepilogoService.ConteggioBadge(notifiche, Cfg(ModoRiepilogo.Giornaliero), null));
    }

    [Fact]
    public void Badge_ConRiepilogoInVigore_ValeUnaSolaVocaPerLeRiepilogabili()
    {
        var cfg = Cfg(ModoRiepilogo.Giornaliero);
        var ultimo = O(11, 9, 0);
        var notifiche = new List<NotificaApp>
        {
            Nota("ChangeRequest", L(11, 8, 0)),
            Nota("ChangeRequest", L(11, 8, 10)),
            Nota("Conflitto", L(11, 8, 20)),
            Nota(NotificaPreferenzeService.TipoConflittoSincronizzazione, L(11, 9, 40)),   // immediata, dopo il riepilogo
            Nota("ChangeRequest", L(11, 9, 50)),                                          // riepilogabile ma in arrivo
        };

        // Riepilogo (1) + conflitto di sincronizzazione (1). Le tre riepilogabili precedenti e quella in arrivo non contano.
        Assert.Equal(2, NotificaRiepilogoService.ConteggioBadge(notifiche, cfg, ultimo));

        // Segnato come letto il riepilogo, resta il solo conflitto immediato: la riepilogabile in arrivo non conta ancora.
        foreach (var n in notifiche.Where(n => (n.Tipo is "ChangeRequest" or "Conflitto") && n.Data <= ultimo.LocalDateTime)) n.Letta = true;
        Assert.Equal(1, NotificaRiepilogoService.ConteggioBadge(notifiche, cfg, ultimo));
    }

    [Fact]
    public void Emetti_NuoveSonoLeRiepilogabiliArrivateDopoIlRiepilogoPrecedente()
    {
        var cfg = Cfg(ModoRiepilogo.Giornaliero);
        var notifiche = new List<NotificaApp>
        {
            Nota("ChangeRequest", L(10, 10, 0)),                                          // già nel riepilogo precedente
            Nota("ChangeRequest", L(10, 15, 0)),                                          // nuova
            Nota("Conflitto", L(11, 8, 0)),                                               // nuova
            Nota(NotificaPreferenzeService.TipoConflittoSincronizzazione, L(11, 8, 5)),   // immediata: non riepilogabile
            Nota("Assegnazione", L(11, 9, 30)),                                           // dopo l'istante di emissione
        };

        var esito = NotificaRiepilogoService.Emetti(cfg, notifiche, O(10, 12, 0), O(11, 9, 0));

        Assert.True(esito.Emesso);
        Assert.Equal(O(11, 9, 0), esito.Ultimo);
        Assert.Equal(2, esito.Nuove);
    }

    [Fact]
    public void Emetti_NonModificaNessunaNotifica_NeNonEmetteSeGiaFattoOggi()
    {
        var cfg = Cfg(ModoRiepilogo.Giornaliero);
        var notifiche = new List<NotificaApp> { Nota("ChangeRequest", L(11, 8, 0)), Nota("Conflitto", L(11, 8, 5), letta: true) };
        var prima = notifiche.Select(n => (n.Uid, n.Letta, n.Tipo, n.Data)).ToList();

        var emesso = NotificaRiepilogoService.Emetti(cfg, notifiche, null, O(11, 9, 0));
        Assert.True(emesso.Emesso);
        _ = NotificaRiepilogoService.Calcola(notifiche, cfg, emesso.Ultimo);
        _ = NotificaRiepilogoService.ConteggioBadge(notifiche, cfg, emesso.Ultimo);
        Assert.Equal(prima, notifiche.Select(n => (n.Uid, n.Letta, n.Tipo, n.Data)).ToList());

        var stessoGiorno = NotificaRiepilogoService.Emetti(cfg, notifiche, O(11, 9, 0), O(11, 17, 0));
        Assert.False(stessoGiorno.Emesso);
        Assert.Equal(0, stessoGiorno.Nuove);
        Assert.Equal(O(11, 9, 0), stessoGiorno.Ultimo);
    }

    [Fact]
    public void ConfigSenzaCampiRiepilogo_SiCaricaConDefaultEConfigConListaNullaNonRompe()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");
        // Formato precedente al riepilogo: nessuno dei due campi nuovi nel file.
        File.WriteAllText(path, "{\r\n  \"CartellaCondivisa\": \"\",\r\n  \"OperatoreAttivo\": \"" + Operatore + "\",\r\n  \"NotificheWindows\": true,\r\n  \"Lingua\": \"it\"\r\n}");

        var cfg = new AppConfigService(path).Load();

        Assert.Equal("Disattivato", cfg.NotificheRiepilogoModo);
        Assert.NotNull(cfg.NotificheRiepilogoTipiEsclusi);
        Assert.Empty(cfg.NotificheRiepilogoTipiEsclusi);
        Assert.Equal(ModoRiepilogo.Disattivato, NotificaRiepilogoService.Modo(cfg));
        Assert.True(cfg.NotificheWindows);

        // Valore sconosciuto e lista esplicitamente nulla (modifica a mano): nessuna eccezione, comportamento di default.
        File.WriteAllText(path, "{\r\n  \"NotificheRiepilogoModo\": \"Settimanale\",\r\n  \"NotificheRiepilogoTipiEsclusi\": null\r\n}");
        var manuale = new AppConfigService(path).Load();
        Assert.Equal(ModoRiepilogo.Disattivato, NotificaRiepilogoService.Modo(manuale));
        Assert.True(NotificaRiepilogoService.EnterRiepilogo("Assegnazione", manuale));
        NotificaRiepilogoService.ImpostaIncluso(manuale, "Assegnazione", false);
        Assert.False(NotificaRiepilogoService.EnterRiepilogo("Assegnazione", manuale));
    }

    [Fact]
    public void AvvioGiornaliero_EmetteIlRiepilogoSenzaTogliereNulla_DalLedgerEDalBadge()
    {
        var state = CreaStato();
        NotificaRiepilogoService.ImpostaModo(state.Config, ModoRiepilogo.Giornaliero);
        var ora = DateTime.Now;
        state.Notifiche.Add(Nota("ChangeRequest", ora.AddMinutes(-30)));
        state.Notifiche.Add(Nota("ChangeRequest", ora.AddMinutes(-25)));
        state.Notifiche.Add(Nota("Conflitto", ora.AddMinutes(-20)));
        state.Notifiche.Add(Nota(NotificaPreferenzeService.TipoConflittoSincronizzazione, ora.AddMinutes(-10)));
        state.Notifiche.Add(Nota("Informazione", ora.AddMinutes(-5), letta: true));

        var main = new MainViewModel(state);

        // Primo avvio con riepilogo attivo: le tre riepilogabili non lette formano la voce di riepilogo ed è il primo riepilogo.
        Assert.Equal(3, main.NuoveNotificheRiepilogoAllAvvio);
        Assert.Equal(3, main.RiepilogoElementi.Count);
        Assert.True(main.HaRiepilogoNotifiche);
        // Badge: una voce di riepilogo + il conflitto di sincronizzazione immediato.
        Assert.Equal(2, main.NotificheNonLette);
        // Il centro mostra il conflitto immediato e la notifica letta, non le tre raccolte nel riepilogo.
        Assert.Equal(2, main.NotificheRecenti.Count);
        Assert.DoesNotContain(main.NotificheRecenti, n => n.Tipo == "ChangeRequest" || n.Tipo == "Conflitto");

        // Il ledger non è stato toccato: stesse 5 notifiche, con le stesse letture.
        var ledger = state.Notifiche.GetAll(Operatore);
        Assert.Equal(5, ledger.Count);
        Assert.Equal(4, ledger.Count(n => !n.Letta));
        Assert.True(ViewStateService.LeggiData(state, NotificaRiepilogoService.ChiaveUltimo(Operatore)).HasValue);
    }

    [Fact]
    public void AvvioGiornalieroStessoGiorno_NonRiemetteMaRiepilogaLoStessoInsieme()
    {
        var state = CreaStato();
        NotificaRiepilogoService.ImpostaModo(state.Config, ModoRiepilogo.Giornaliero);
        // Riepilogo già emesso oggi, con un istante futuro di un minuto: le notifiche di prova sono tutte entro quell'istante.
        ViewStateService.ScriviData(state, NotificaRiepilogoService.ChiaveUltimo(Operatore), DateTimeOffset.Now.AddMinutes(1));
        state.Notifiche.Add(Nota("ChangeRequest", DateTime.Now.AddMinutes(-30)));
        state.Notifiche.Add(Nota("Assegnazione", DateTime.Now.AddMinutes(-20)));

        var main = new MainViewModel(state);

        Assert.Equal(0, main.NuoveNotificheRiepilogoAllAvvio);
        Assert.Equal(2, main.RiepilogoElementi.Count);
        Assert.Equal(1, main.NotificheNonLette);
    }

    [Fact]
    public void AllAvvio_AdOgniAvvio_NuoveSonoSoloLeArrivateDopoIlRiepilogoPrecedente()
    {
        var state = CreaStato();
        NotificaRiepilogoService.ImpostaModo(state.Config, ModoRiepilogo.AllAvvio);
        state.Notifiche.Add(Nota("ChangeRequest", DateTime.Now.AddMinutes(-30)));
        // Avvio precedente di dieci minuti fa: il riepilogo emesso allora contiene la CR, ma non ciò che arriva dopo.
        ViewStateService.ScriviData(state, NotificaRiepilogoService.ChiaveUltimo(Operatore), DateTimeOffset.Now.AddMinutes(-10));
        state.Notifiche.Add(Nota("Assegnazione", DateTime.Now.AddMinutes(-5)));

        var primo = new MainViewModel(state);
        Assert.Equal(1, primo.NuoveNotificheRiepilogoAllAvvio);
        Assert.Equal(2, primo.RiepilogoElementi.Count);

        // Avvio successivo senza novità: il riepilogo si riemette, ma nulla è nuovo da segnalare.
        var secondo = new MainViewModel(state);
        Assert.Equal(0, secondo.NuoveNotificheRiepilogoAllAvvio);
        Assert.Equal(2, secondo.RiepilogoElementi.Count);
    }

    [Fact]
    public void SegnaRiepilogoLetto_SegnaSoloIElementiRaccolti_E_NonToccaIlResto()
    {
        var state = CreaStato();
        NotificaRiepilogoService.ImpostaModo(state.Config, ModoRiepilogo.Giornaliero);
        var ora = DateTime.Now;
        state.Notifiche.Add(Nota("ChangeRequest", ora.AddMinutes(-30)));
        state.Notifiche.Add(Nota("Conflitto", ora.AddMinutes(-20)));
        state.Notifiche.Add(Nota(NotificaPreferenzeService.TipoConflittoSincronizzazione, ora.AddMinutes(-10)));
        var main = new MainViewModel(state);
        Assert.Equal(2, main.RiepilogoElementi.Count);

        // Arriva una riepilogabile dopo il riepilogo: non è nel riepilogo e non deve essere segnata come letta.
        state.Notifiche.Add(Nota("ChangeRequest", DateTime.Now.AddMinutes(1)));

        main.SegnaRiepilogoLettoCommand.Execute(null);

        Assert.False(main.HaRiepilogoNotifiche);
        Assert.Equal(1, main.NotificheNonLette);
        var ledger = state.Notifiche.GetAll(Operatore);
        Assert.Equal(2, ledger.Count(n => n.Letta));
        Assert.Equal(2, ledger.Count(n => !n.Letta));
        Assert.Contains(ledger, n => !n.Letta && n.Tipo == NotificaPreferenzeService.TipoConflittoSincronizzazione);
        Assert.Contains(ledger, n => !n.Letta && n.Tipo == "ChangeRequest" && n.Data > ora);
    }

    [Fact]
    public void MarkReadMany_SegnaSoloLeNotificheIndicate_EIdempotente_SenzaEliminare()
    {
        var state = CreaStato();
        var a = Nota("ChangeRequest", DateTime.Now.AddMinutes(-3));
        var b = Nota("Assegnazione", DateTime.Now.AddMinutes(-2));
        var c = Nota("Conflitto", DateTime.Now.AddMinutes(-1));
        state.Notifiche.Add(a);
        state.Notifiche.Add(b);
        state.Notifiche.Add(c);

        var lette = state.Notifiche.GetAll(Operatore).Where(n => n.Tipo != "Conflitto").ToList();
        Assert.Equal(2, state.Notifiche.MarkReadMany(lette));
        Assert.Equal(0, state.Notifiche.MarkReadMany(lette));   // già lette: nessuna nuova scrittura

        var ledger = state.Notifiche.GetAll(Operatore);
        Assert.Equal(3, ledger.Count);
        Assert.Equal(2, ledger.Count(n => n.Letta));
        Assert.False(ledger.Single(n => n.Tipo == "Conflitto").Letta);
    }

    [Fact]
    public void Impostazioni_ModoETipi_SalvanoNelConfigLocale_ECriticiRestanoDisabilitati()
    {
        var state = CreaStato();
        var vm = new ImpostazioniViewModel(state, () => { });

        Assert.Equal(3, vm.OpzioniModoRiepilogo.Count);
        Assert.Equal(ModoRiepilogo.Disattivato, vm.ModoRiepilogoSelezionato!.Modo);

        vm.ModoRiepilogoSelezionato = vm.OpzioniModoRiepilogo.Single(o => o.Modo == ModoRiepilogo.Giornaliero);
        var assegnazione = vm.TipiRiepilogoNotifiche.Single(v => v.Tipo == "Assegnazione");
        var conflitto = vm.TipiRiepilogoNotifiche.Single(v => v.Tipo == NotificaPreferenzeService.TipoConflittoSincronizzazione);
        var wu = vm.TipiRiepilogoNotifiche.Single(v => v.Tipo == NotificaPreferenzeService.TipoWuDaDecidere);

        Assert.Equal("Preferenze_Riepilogo_Tipo_Assegnazione_CheckBox", assegnazione.AutomationId);
        Assert.True(assegnazione.IncludiNelRiepilogo);
        Assert.False(conflitto.Modificabile);
        Assert.False(conflitto.IncludiNelRiepilogo);
        Assert.False(wu.Modificabile);
        conflitto.IncludiNelRiepilogo = true;   // ignorato: il tipo non è modificabile
        Assert.False(conflitto.IncludiNelRiepilogo);

        assegnazione.IncludiNelRiepilogo = false;
        Assert.Equal(new[] { "Assegnazione" }, state.Config.NotificheRiepilogoTipiEsclusi);

        vm.SalvaPreferenzeLocaliCommand.Execute(null);
        var salvata = new AppConfigService(Path.Combine(state.ConfigService.CartellaLocale, "config.json")).Load();
        Assert.Equal("Giornaliero", salvata.NotificheRiepilogoModo);
        Assert.Equal(new[] { "Assegnazione" }, salvata.NotificheRiepilogoTipiEsclusi);
    }
}
