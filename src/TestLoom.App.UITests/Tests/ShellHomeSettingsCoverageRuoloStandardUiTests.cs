using TestLoom.App.Models;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using static TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Operatore Standard sulla Home personalizzata: il layout del team esiste ed e' applicabile, ma la pubblicazione
/// (solo Master) non deve comparire. Il controllo e' verificato sull'albero UIA reale, non solo sul ViewModel.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Coverage")]
[Trait("Area", "Home")]
[Trait("Area", "Ruoli")]
public sealed class ShellHomeSettingsCoverageRuoloStandardUiTests : UiTestBase
{
    public ShellHomeSettingsCoverageRuoloStandardUiTests() : base(CreaWorkspaceStandardConLayoutTeam()) { }

    private static TestWorkspace CreaWorkspaceStandardConLayoutTeam()
    {
        var ws = TestWorkspace.CreateLoggedInAs(nameof(ShellHomeSettingsCoverageRuoloStandardUiTests), RuoloOperatore.Standard);
        SeminaLayoutTeam(ws, "QA Team");
        return ws;
    }

    [Fact]
    public void Standard_NonVedePubblicaLayoutTeam_MaPuoApplicareQuelloDelTeam()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.DismissNotificationsIfShown();
            shell.GoToHome();
            ShellHomeSettingsCoverageHomeUiTests.ApriPersonalizzazione(window);

            Assert.False(IdVisibile(window, "Home_Layout_PubblicaTeam_Button"),
                "lo Standard non deve vedere 'Pubblica come layout del team' (solo Master)");

            ElementFinders.WaitButtonById(window, "Home_Layout_UsaTeam_Button").SafeInvoke();
            Wait.ForCondition(() => (ElementFinders.TryFindById(window, "Home_LayoutTeam_Messaggio_Text")?.Name ?? "")
                    .StartsWith("Layout del team applicato", StringComparison.Ordinal),
                "lo Standard applica il layout del team", TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Standard_NonVedePubblicaLayoutTeam_MaPuoApplicareQuelloDelTeam), ex); }
    }
}
