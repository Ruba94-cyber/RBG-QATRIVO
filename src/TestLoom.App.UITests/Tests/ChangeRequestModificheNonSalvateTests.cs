using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Modifiche non salvate sull'editor Change Request (ChangeRequestEditorViewModel.NavigationSafety:
/// TornaAllaListaSicuro chiede conferma se ModificheNonSalvate) — finora mai esercitato da un test FlaUI,
/// l'editor CR non aveva nemmeno un AutomationId prima di questa iterazione.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Dialogs")]
public sealed class ChangeRequestModificheNonSalvateTests : UiTestBase
{
    private const string TemplateCrNome = "CR Template QA";

    public ChangeRequestModificheNonSalvateTests() : base(CreaWorkspaceConTemplateCr()) { }

    private static TestWorkspace CreaWorkspaceConTemplateCr()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(ChangeRequestModificheNonSalvateTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        var templates = state.Master.TestTemplate.Load();
        templates.Add(new TestTemplate { Nome = TemplateCrNome, IsChangeRequest = true, ModificatoDa = "UITests" });
        state.Master.TestTemplate.Save(templates);
        return workspace;
    }

    [Fact]
    public void TornaAllaLista_ConTitoloDigitato_ChiedeConfermaEAnnullaRestaNellEditor()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var anomalie = shell.GoToAnomalie();
            var editor = anomalie.NuovaChangeRequest();

            editor.SetTitolo("CR QA - modifiche non salvate");
            editor.TornaAllaLista();

            var confirmWindow = App.WaitForWindowByContent("Confirm_Yes_Button");
            var confirm = new ConfirmDialogPage(confirmWindow);
            Assert.False(string.IsNullOrWhiteSpace(confirm.Message));
            confirm.ConfirmNo();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura del dialog dopo 'No'");

            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Cr_Titolo_TextBox"),
                "dopo 'No' resta nell'editor CR (il titolo digitato e' ancora presente)");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(TornaAllaLista_ConTitoloDigitato_ChiedeConfermaEAnnullaRestaNellEditor), ex);
        }
    }
}
