using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using static TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Riepilogo notifiche (6.6.0) dal centro notifiche: con modalità Giornaliera e notifiche seminate prima dell'avvio compare
/// una sola voce di riepilogo con i conteggi per tipo; "Apri" mostra gli elementi raccolti; "Segna riepilogo come letto"
/// segna solo quelli raccolti. Il conflitto di sincronizzazione dati resta una riga immediata. L'esito è verificato anche
/// sul ledger su disco. Il riepilogo attivato da Impostazioni e il riavvio sono coperti da <see cref="NotificheRiepilogoImpostazioniUiTests"/>.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Coverage")]
[Trait("Area", "Notifiche")]
public sealed class NotificheRiepilogoUiTests : UiTestBase
{
    private const string TitoloCr1 = "QA riepilogo change request uno";
    private const string TitoloCr2 = "QA riepilogo change request due";
    private const string TitoloConflitto = "QA riepilogo conflitto di salvataggio";
    private const string TitoloSincronizzazione = "QA riepilogo conflitto sincronizzazione";
    private const string TitoloLetta = "QA riepilogo gia letta";

    public NotificheRiepilogoUiTests() : base(CreaWorkspaceConRiepilogo()) { }

    private static TestWorkspace CreaWorkspaceConRiepilogo()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(NotificheRiepilogoUiTests));
        var configService = new AppConfigService(ws.ConfigPath);
        var cfg = configService.Load();
        NotificaRiepilogoService.ImpostaModo(cfg, NotificaRiepilogoService.ModoRiepilogo.Giornaliero);
        configService.Save(cfg);

        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var ora = DateTime.Now;
        state.Notifiche.Add(Nuova("ChangeRequest", TitoloCr1, ora.AddMinutes(-30)));
        state.Notifiche.Add(Nuova("ChangeRequest", TitoloCr2, ora.AddMinutes(-25)));
        state.Notifiche.Add(Nuova("Conflitto", TitoloConflitto, ora.AddMinutes(-20)));
        state.Notifiche.Add(Nuova(NotificaPreferenzeService.TipoConflittoSincronizzazione, TitoloSincronizzazione, ora.AddMinutes(-10)));
        var letta = Nuova("Informazione", TitoloLetta, ora.AddMinutes(-5));
        state.Notifiche.Add(letta);
        state.Notifiche.MarkRead(letta);
        return ws;
    }

    internal static NotificaApp Nuova(string tipo, string titolo, DateTime data) => new()
    {
        Destinatario = TestWorkspace.DefaultOperatorName,
        Tipo = tipo,
        Titolo = titolo,
        Messaggio = "Messaggio di prova per il riepilogo notifiche",
        Data = data,
    };

    private NotificaRepository Ledger() => new(new DataPaths(Workspace.WorkspaceDir));

    [Fact]
    public void Riepilogo_GiornalieroAllAvvio_MostraUnaVoceConIConteggiPerTipo_EApreIElementi()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).DismissNotificationsIfShown();
            ApriCentroNotifiche(window);

            // Tre notifiche riepilogabili non lette: una sola voce, con i conteggi per tipo. Il conflitto di sincronizzazione
            // resta una riga a sé, come prima del riepilogo.
            Wait.ForCondition(() => TestoVisibile(window, "3 nuove notifiche") != null,
                "voce di riepilogo con '3 nuove notifiche'", TimeSpan.FromSeconds(15));
            Assert.True(TestoContieneVisibile(window, "2 Change request"), "il riepilogo conta le change request per tipo");
            Assert.True(TestoContieneVisibile(window, "1 Conflitto di salvataggio"), "il riepilogo conta i conflitti di salvataggio per tipo");
            Assert.Null(TestoVisibile(window, TitoloCr1));
            Assert.True(VisibileInUia(BottoneRigaNotifica(window, TitoloSincronizzazione, "Main_Notifiche_SegnaLetta_Button")),
                "il conflitto di sincronizzazione dati resta immediato, fuori dal riepilogo");

            // Apri: compaiono gli elementi raccolti.
            ElementFinders.WaitById(window, "Main_Notifiche_Riepilogo_Apri_Button").SafeInvoke();
            Wait.ForCondition(() => TestoVisibile(window, TitoloCr1) != null && TestoVisibile(window, TitoloConflitto) != null,
                "elementi del riepilogo visibili dopo 'Apri'", TimeSpan.FromSeconds(15));

            // Nulla eliminato né segnato come letto: stesse 5 notifiche, di cui 4 non lette.
            var ledger = Ledger().GetAll(TestWorkspace.DefaultOperatorName);
            Assert.Equal(5, ledger.Count);
            Assert.Equal(4, ledger.Count(n => !n.Letta));
            Assert.Contains(ledger, n => n.Titolo == TitoloLetta && n.Letta);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Riepilogo_GiornalieroAllAvvio_MostraUnaVoceConIConteggiPerTipo_EApreIElementi), ex); }
    }

    [Fact]
    public void Riepilogo_SegnaRiepilogoLetto_SegnaSoloLeNotificheRaccolte()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).DismissNotificationsIfShown();
            ApriCentroNotifiche(window);
            Wait.ForCondition(() => TestoVisibile(window, "3 nuove notifiche") != null,
                "voce di riepilogo con '3 nuove notifiche'", TimeSpan.FromSeconds(15));

            ElementFinders.WaitById(window, "Main_Notifiche_Riepilogo_SegnaLetto_Button").SafeInvoke();
            Wait.ForCondition(() => TestoVisibile(window, "3 nuove notifiche") == null,
                "la voce di riepilogo sparisce dopo 'Segna riepilogo come letto'", TimeSpan.FromSeconds(15));
            Assert.True(VisibileInUia(BottoneRigaNotifica(window, TitoloSincronizzazione, "Main_Notifiche_SegnaLetta_Button")),
                "il conflitto di sincronizzazione dati resta non letto");

            var ledger = Ledger().GetAll(TestWorkspace.DefaultOperatorName);
            Assert.Contains(ledger, n => n.Titolo == TitoloCr1 && n.Letta);
            Assert.Contains(ledger, n => n.Titolo == TitoloCr2 && n.Letta);
            Assert.Contains(ledger, n => n.Titolo == TitoloConflitto && n.Letta);
            Assert.Contains(ledger, n => n.Titolo == TitoloSincronizzazione && !n.Letta);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Riepilogo_SegnaRiepilogoLetto_SegnaSoloLeNotificheRaccolte), ex); }
    }
}

/// <summary>
/// Riepilogo notifiche abilitato da Impostazioni: il modo "Giornaliero" e un tipo deselezionato si salvano nel config locale;
/// dopo il riavvio il primo avvio del giorno emette il riepilogo, senza il tipo deselezionato. Il primo avvio del test parte con
/// il riepilogo disattivato, come un PC che non l'ha mai attivato: così il riavvio successivo è il primo avvio del giorno.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Coverage")]
[Trait("Area", "Notifiche")]
public sealed class NotificheRiepilogoImpostazioniUiTests : UiTestBase
{
    private const string TitoloCr1 = "QA impostazioni riepilogo change request uno";
    private const string TitoloCr2 = "QA impostazioni riepilogo change request due";
    private const string TitoloConflitto = "QA impostazioni riepilogo conflitto di salvataggio";
    private const string TitoloAssegnazione = "QA impostazioni riepilogo test assegnato";

    private TestLoomApplication? _riavviata;

    public NotificheRiepilogoImpostazioniUiTests() : base(TestWorkspace.CreateLoggedIn(nameof(NotificheRiepilogoImpostazioniUiTests))) { }

    public override void Dispose()
    {
        _riavviata?.Dispose();
        base.Dispose();
    }

    private string ConfigPath => Workspace.ConfigPath;

    private NotificaRepository Ledger() => new(new DataPaths(Workspace.WorkspaceDir));

    [Fact]
    public void Riepilogo_AbilitatoInImpostazioni_RiavvioMostraUnaVoceEFuoriIlTipoDeselezionato()
    {
        try
        {
            // 1) Impostazioni -> Generale: modo Giornaliero, Assegnazione fuori dal riepilogo, poi "Salva preferenze".
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.DismissNotificationsIfShown();
            shell.GoToImpostazioni().VaiA("generale");

            ElementFinders.WaitById(window, "Preferenze_Riepilogo_Modo").AsComboBox().SelectByText("Giornaliero");
            Assert.False(ElementFinders.WaitById(window, "Preferenze_Riepilogo_Tipo_ConflittoSincronizzazione_CheckBox").IsEnabled,
                "il conflitto di sincronizzazione dati non è modificabile nel riepilogo");
            var assegnazione = ElementFinders.WaitById(window, "Preferenze_Riepilogo_Tipo_Assegnazione_CheckBox").AsCheckBox();
            if (assegnazione.IsChecked == true) assegnazione.SafeInvoke();
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Preferenze_Riepilogo_Tipo_Assegnazione_CheckBox").AsCheckBox().IsChecked == false,
                "Assegnazione deselezionata nel riepilogo", TimeSpan.FromSeconds(10));

            ElementFinders.WaitById(window, "Impostazioni_SalvaPreferenze_Button").SafeInvoke();
            Wait.ForCondition(() => (ElementFinders.TryFindById(window, "Impostazioni_Messaggio_Text")?.Name ?? "")
                    .StartsWith("Preferenze locali salvate", StringComparison.Ordinal),
                "messaggio 'Preferenze locali salvate'", TimeSpan.FromSeconds(15));

            using (var doc = LeggiJson(ConfigPath))
            {
                var radice = doc.RootElement;
                Assert.Equal("Giornaliero", Proprieta(radice, "NotificheRiepilogoModo")!.Value.GetString());
                Assert.Contains("Assegnazione", Proprieta(radice, "NotificheRiepilogoTipiEsclusi")!.Value.EnumerateArray().Select(x => x.GetString()));
            }

            // 2) Chiusura, semina del ledger con il programma fermo, poi riavvio: primo avvio del giorno con il riepilogo attivo.
            App.Dispose();
            using (var state = new AppState(new AppConfigService(ConfigPath), runBackgroundMaintenance: false))
            {
                var ora = DateTime.Now;
                state.Notifiche.Add(NotificheRiepilogoUiTests.Nuova("ChangeRequest", TitoloCr1, ora.AddMinutes(-30)));
                state.Notifiche.Add(NotificheRiepilogoUiTests.Nuova("ChangeRequest", TitoloCr2, ora.AddMinutes(-25)));
                state.Notifiche.Add(NotificheRiepilogoUiTests.Nuova("Conflitto", TitoloConflitto, ora.AddMinutes(-20)));
                state.Notifiche.Add(NotificheRiepilogoUiTests.Nuova("Assegnazione", TitoloAssegnazione, ora.AddMinutes(-15)));
            }

            _riavviata = TestLoomApplication.Launch(Workspace);
            var finestra = _riavviata.WaitForMainWindow();
            new MainShellPage(finestra).DismissNotificationsIfShown();
            ApriCentroNotifiche(finestra);

            // 3) Una sola voce con le tre notifiche del riepilogo (due change request e un conflitto di salvataggio).
            //    L'assegnazione, deselezionata in Impostazioni, resta una riga a sé con il suo pulsante.
            Wait.ForCondition(() => TestoVisibile(finestra, "3 nuove notifiche") != null,
                "voce di riepilogo con '3 nuove notifiche' dopo il riavvio", TimeSpan.FromSeconds(20));
            Assert.Null(TestoVisibile(finestra, TitoloCr1));
            Assert.True(VisibileInUia(BottoneRigaNotifica(finestra, TitoloAssegnazione, "Main_Notifiche_SegnaLetta_Button")),
                "il tipo deselezionato resta immediato, fuori dal riepilogo");

            var ledger = Ledger().GetAll(TestWorkspace.DefaultOperatorName);
            Assert.Equal(4, ledger.Count(n => !n.Letta));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Riepilogo_AbilitatoInImpostazioni_RiavvioMostraUnaVoceEFuoriIlTipoDeselezionato), ex); }
    }
}
