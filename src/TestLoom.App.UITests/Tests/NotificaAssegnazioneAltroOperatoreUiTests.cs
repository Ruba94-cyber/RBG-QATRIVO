using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using static TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport;

namespace TestLoom.App.UITests.Tests;

/// <summary>Regressione FlaUI per la consegna reale di una notifica di assegnazione a un operatore diverso
/// dall'autore: il test viene creato dalla UI, il destinatario riceve la sola notifica relativa al record e la
/// vede nel centro notifiche quando accede con la propria identita'.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
[Trait("Area", "Notifiche")]
public sealed class NotificaAssegnazioneAltroOperatoreUiTests : UiTestBase
{
    private const string Destinatario = "QA Operatore destinatario";
    private const string PinDestinatario = "7373";
    private const string DescrizioneBreve = "QA notifica assegnazione altro operatore";

    public NotificaAssegnazioneAltroOperatoreUiTests() : base(CreaWorkspaceConDestinatario()) { }

    private static TestWorkspace CreaWorkspaceConDestinatario()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(NotificaAssegnazioneAltroOperatoreUiTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        var config = configService.Load();
        config.VersioniOnboardingCompletate[OnboardingService.Chiave(Destinatario, RuoloOperatore.Standard)] =
            OnboardingService.VersioneCorrente;
        configService.Save(config);

        using var state = new AppState(configService, runBackgroundMaintenance: false);
        var templates = state.Master.TestTemplate.Load();
        var template = templates.Single(t => t.Nome == TestWorkspace.DefaultTemplateName);
        template.CampiPianificazione.Add(new CampoTemplate
        {
            Tipo = TipoCampoTemplate.CampoSistema,
            CampoSistemaChiave = CampiSistema.Assegnatario,
        });
        state.Master.TestTemplate.Save(templates);

        var operatori = state.Master.Operatori.Load();
        var destinatario = new Operatore
        {
            Nome = Destinatario,
            Iniziali = "QO",
            PinHash = PinService.Hash(PinDestinatario),
            RecoveryCodeHash = PinService.Hash(PinService.GeneraCodiceRecupero()),
            Ruolo = RuoloOperatore.Standard,
            RichiedeCambioPin = false,
            ModificatoDa = "UITests",
        };
        RuoloSigilloService.Sigilla(destinatario);
        operatori.Add(destinatario);
        state.Master.Operatori.Save(operatori);

        return workspace;
    }

    [Fact]
    public void CreaTest_AssegnatoAdAltroOperatore_NotificaSoloDestinatarioECompareNelSuoCentro()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window, App);
            var lista = shell.GoToTestList();
            var editor = lista.ClickNew();
            editor.ChooseTemplate();
            editor.GoToTab(0);

            var assegnatario = Wait.ForElement(
                () => ElementFinders.TryFindById(window, "Editor_Assegnatario_Combo"),
                "ComboBox Assegnatario con l'operatore creatore selezionato");
            assegnatario.AsComboBox().SelectByText(Destinatario);
            Assert.Equal(Destinatario, TestoSelezionato(assegnatario));

            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            editor.SetDescrizioneBreve(DescrizioneBreve);
            editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);
            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();

            var listaDopoSalvataggio = new TestListPage(window, window);
            Wait.ForCondition(() => listaDopoSalvataggio.RowCount() == 1,
                "un solo Test creato dalla UI", TimeSpan.FromSeconds(15));
            var testId = Assert.Single(listaDopoSalvataggio.VisibleRowIds());

            var ledger = new NotificaRepository(new DataPaths(Workspace.WorkspaceDir));
            var notificheAssegnazione = new[] { TestWorkspace.DefaultOperatorName, Destinatario }
                .SelectMany(nomeOperatore => ledger.GetAll(nomeOperatore)
                    .Where(notifica => notifica.Tipo == "Assegnazione" && notifica.ElementoId == testId))
                .ToList();
            var notifica = Assert.Single(notificheAssegnazione);
            Assert.Equal(Destinatario, notifica.Destinatario);
            Assert.False(notifica.Letta);
            Assert.Equal("Test assegnato", notifica.Titolo);
            Assert.Equal($"{testId} è stato assegnato a te.", notifica.Messaggio);

            shell.Logout();
            new LoginPage(window).LoginAs(Destinatario, PinDestinatario);
            Wait.ForCondition(() => TestoVisibile(window, Destinatario) != null,
                "identita' dell'operatore destinatario dopo il login", TimeSpan.FromSeconds(15));

            ApriCentroNotifiche(window);
            Wait.ForCondition(
                () => TestoVisibile(window, notifica.Titolo) != null && TestoVisibile(window, notifica.Messaggio) != null,
                "titolo e messaggio della notifica di assegnazione nel centro dell'operatore destinatario",
                TimeSpan.FromSeconds(15));
            Assert.NotNull(TestoVisibile(window, notifica.Titolo));
            Assert.NotNull(TestoVisibile(window, notifica.Messaggio));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(
                nameof(CreaTest_AssegnatoAdAltroOperatore_NotificaSoloDestinatarioECompareNelSuoCentro), ex);
        }
    }

    private static string? TestoSelezionato(AutomationElement comboElement)
    {
        var selected = comboElement.AsComboBox().SelectedItem;
        if (selected == null)
        {
            return comboElement.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .FirstOrDefault(element => VisibileInUia(element) && !string.IsNullOrWhiteSpace(element.Name))?.Name;
        }

        return new[] { selected.Text, selected.Name }
            .Concat(selected.FindAllDescendants().Select(element => element.Name))
            .FirstOrDefault(testo => !string.IsNullOrWhiteSpace(testo));
    }
}
