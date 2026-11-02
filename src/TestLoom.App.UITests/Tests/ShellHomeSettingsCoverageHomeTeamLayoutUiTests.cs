using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using static TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Layout della Home del team (Master): "Usa layout del team" applica le sezioni del file condiviso e "Pubblica
/// come layout del team" (con conferma) riscrive il file incrementando la revisione. Il file e' verificato su disco.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Coverage")]
[Trait("Area", "Home")]
public sealed class ShellHomeSettingsCoverageHomeTeamLayoutUiTests : UiTestBase
{
    public ShellHomeSettingsCoverageHomeTeamLayoutUiTests() : base(CreaWorkspaceConLayoutTeam()) { }

    private static TestWorkspace CreaWorkspaceConLayoutTeam()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(ShellHomeSettingsCoverageHomeTeamLayoutUiTests));
        SeminaLayoutTeam(ws, "QA Team");
        return ws;
    }

    private static string MessaggioTeam(Window window) =>
        ElementFinders.TryFindById(window, "Home_LayoutTeam_Messaggio_Text")?.Name ?? "";

    [Fact]
    public void UsaLayoutDelTeam_ApplicaLeSezioniPubblicateNelFile()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.DismissNotificationsIfShown();
            shell.GoToHome();
            ShellHomeSettingsCoverageHomeUiTests.ApriPersonalizzazione(window);

            ElementFinders.WaitButtonById(window, "Home_Layout_UsaTeam_Button").SafeInvoke();
            Wait.ForCondition(() => MessaggioTeam(window).StartsWith("Layout del team applicato", StringComparison.Ordinal),
                "messaggio 'Layout del team applicato'", TimeSpan.FromSeconds(15));

            // Il file del team nasconde "kpi": la checkbox del pannello e la sezione devono riflettere il file.
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Home_Layout_kpi_CheckBox").AsCheckBox().IsChecked == false,
                "la checkbox KPI riflette il layout del team (nascosta)", TimeSpan.FromSeconds(15));
            Assert.False(IdVisibile(window, "Home_Sezione_kpi"), "la sezione KPI nascosta dal team non compare nella Home");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(UsaLayoutDelTeam_ApplicaLeSezioniPubblicateNelFile), ex); }
    }

    [Fact]
    public void Pubblica_ComeMaster_RiscriveIlFileConLaRevisioneSuccessiva()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.DismissNotificationsIfShown();
            shell.GoToHome();
            ShellHomeSettingsCoverageHomeUiTests.ApriPersonalizzazione(window);

            var pubblica = Wait.ForElement(() => VisibilePerId(window, "Home_Layout_PubblicaTeam_Button"),
                "pulsante 'Pubblica come layout del team' visibile per il Master", TimeSpan.FromSeconds(15));
            pubblica.AsButton().SafeInvoke();
            Assert.True(ConfermaSeCompare(App, TimeSpan.FromSeconds(15)), "la pubblicazione chiede conferma prima di scrivere");

            Wait.ForCondition(() => MessaggioTeam(window).StartsWith("Layout del team pubblicato (revisione 2)", StringComparison.Ordinal),
                "messaggio di pubblicazione con revisione 2", TimeSpan.FromSeconds(15));

            var percorso = HomeTeamLayoutService.PercorsoFile(new DataPaths(Workspace.WorkspaceDir));
            using var doc = LeggiJson(percorso);
            var radice = doc.RootElement;
            Assert.Equal(2, Proprieta(radice, "Revisione")!.Value.GetInt32());
            Assert.Equal(TestWorkspace.DefaultOperatorName, Proprieta(radice, "AggiornatoDa")!.Value.GetString());
            var kpi = Proprieta(radice, "Sezioni")!.Value.EnumerateArray()
                .First(s => string.Equals(Proprieta(s, "Id")!.Value.GetString(), "kpi", StringComparison.Ordinal));
            Assert.False(Proprieta(kpi, "Visibile")!.Value.GetBoolean(), "il file pubblicato conserva la scelta di nascondere KPI");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Pubblica_ComeMaster_RiscriveIlFileConLaRevisioneSuccessiva), ex); }
    }
}
