using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using static TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Centro notifiche interno (menu account -> Notifiche): segna come letta, elimina singola, segna tutte come lette,
/// elimina lette. Le notifiche sono seminate PRIMA dell'avvio nel ledger reale di workspace (NotificaRepository), e
/// l'esito e' verificato anche sul ledger su disco, non solo sulla UI.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Coverage")]
[Trait("Area", "Notifiche")]
public sealed class ShellHomeSettingsCoverageNotificheUiTests : UiTestBase
{
    private const string TitoloNonLettaUno = "QA notifica non letta uno";
    private const string TitoloNonLettaDue = "QA notifica non letta due";
    private const string TitoloLetta = "QA notifica gia letta tre";
    private const string TitoloApribile = "QA test assegnato da aprire";
    private const string IdTestApribile = "TC-QA-NOTIFICA-APRI";

    public ShellHomeSettingsCoverageNotificheUiTests() : base(CreaWorkspaceConNotifiche()) { }

    private static TestWorkspace CreaWorkspaceConNotifiche()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(ShellHomeSettingsCoverageNotificheUiTests));
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var ora = DateTime.Now;
        state.TestCases.Save(new TestCase { Id = IdTestApribile, DescrizioneBreve = "Test notificato da aprire", Componente = { TestWorkspace.DefaultEcuName } }, isNew: true);
        state.Notifiche.Add(new NotificaApp
        {
            Destinatario = TestWorkspace.DefaultOperatorName,
            Tipo = "Assegnazione",
            Titolo = TitoloApribile,
            Messaggio = "Apri il test assegnato.",
            ElementoId = IdTestApribile,
            Data = ora,
        });
        state.Notifiche.Add(Nuova(TitoloNonLettaUno, ora.AddMinutes(-3)));
        state.Notifiche.Add(Nuova(TitoloNonLettaDue, ora.AddMinutes(-2)));
        var letta = Nuova(TitoloLetta, ora.AddMinutes(-1));
        state.Notifiche.Add(letta);
        state.Notifiche.MarkRead(letta);
        return ws;
    }

    private static NotificaApp Nuova(string titolo, DateTime data) => new()
    {
        Destinatario = TestWorkspace.DefaultOperatorName,
        Tipo = "Informazione",
        Titolo = titolo,
        Messaggio = "Messaggio di prova per la copertura del centro notifiche",
        Data = data,
    };

    private NotificaRepository Ledger() => new(new DataPaths(Workspace.WorkspaceDir));

    [Fact]
    public void Notifiche_SegnaComeLettaEEliminaSingola_AggiornanoUiELedger()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).DismissNotificationsIfShown();
            ApriCentroNotifiche(window);

            var segnaLetta = BottoneRigaNotifica(window, TitoloNonLettaUno, "Main_Notifiche_SegnaLetta_Button");
            Assert.True(VisibileInUia(segnaLetta), "la notifica non letta espone il pulsante 'Segna come letta'");
            segnaLetta!.AsButton().SafeInvoke();
            Wait.ForCondition(() => !VisibileInUia(BottoneRigaNotifica(window, TitoloNonLettaUno, "Main_Notifiche_SegnaLetta_Button")),
                "la notifica diventa letta (pulsante 'Segna come letta' sparito)", TimeSpan.FromSeconds(15));
            Assert.True(VisibileInUia(BottoneRigaNotifica(window, TitoloNonLettaDue, "Main_Notifiche_SegnaLetta_Button")),
                "la seconda notifica resta non letta");

            var elimina = BottoneRigaNotifica(window, TitoloLetta, "Main_Notifiche_Elimina_Button");
            Assert.True(VisibileInUia(elimina), "la notifica gia' letta espone il pulsante di eliminazione");
            elimina!.AsButton().SafeInvoke();
            Wait.ForCondition(() => TestoVisibile(window, TitoloLetta) == null,
                "la notifica eliminata scompare dal centro", TimeSpan.FromSeconds(15));

            var letture = Ledger().GetAll(TestWorkspace.DefaultOperatorName);
            Assert.Contains(letture, n => n.Titolo == TitoloNonLettaUno && n.Letta);
            Assert.Contains(letture, n => n.Titolo == TitoloNonLettaDue && !n.Letta);
            Assert.DoesNotContain(letture, n => n.Titolo == TitoloLetta);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Notifiche_SegnaComeLettaEEliminaSingola_AggiornanoUiELedger), ex); }
    }

    [Fact]
    public void Notifiche_SegnaTutteComeLetteEPoiEliminaLette_SvuotanoIlCentro()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).DismissNotificationsIfShown();
            ApriCentroNotifiche(window);

            ElementFinders.WaitById(window, "Main_Notifiche_SegnaTutte_Button").SafeInvoke();
            Wait.ForCondition(() => window.FindAllDescendants(cf => cf.ByAutomationId("Main_Notifiche_SegnaLetta_Button"))
                    .All(b => !VisibileInUia(b)),
                "nessuna notifica resta non letta dopo 'Segna tutte come lette'", TimeSpan.FromSeconds(15));

            ElementFinders.WaitById(window, "Main_Notifiche_EliminaLette_Button").SafeInvoke();
            Wait.ForCondition(() => TestoVisibile(window, "Nessuna notifica.") != null,
                "centro vuoto dopo 'Elimina lette'", TimeSpan.FromSeconds(15));

            var letture = Ledger().GetAll(TestWorkspace.DefaultOperatorName);
            Assert.Empty(letture);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Notifiche_SegnaTutteComeLetteEPoiEliminaLette_SvuotanoIlCentro), ex); }
    }

    [Fact]
    public void Notifica_ApriElemento_ApreTestCollegatoSenzaSegnarloComeLetto()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).DismissNotificationsIfShown();
            ApriCentroNotifiche(window);

            BottoneRigaNotifica(window, TitoloApribile, "Main_Notifiche_Apri_Button")!.AsButton().SafeInvoke();
            Wait.ForCondition(() => ElementFinders.TryFindById(window, "Editor_Title_Text")?.Name == IdTestApribile,
                "la notifica apre l'editor del Test collegato", TimeSpan.FromSeconds(15));
            Assert.Null(ElementFinders.TryFindById(window, "Main_NotificationsClose_Button"));
            Assert.Contains(Ledger().GetAll(TestWorkspace.DefaultOperatorName), n => n.Titolo == TitoloApribile && !n.Letta);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Notifica_ApriElemento_ApreTestCollegatoSenzaSegnarloComeLetto), ex); }
    }
}
