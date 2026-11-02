using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.Tests;

/// <summary>First run on an empty folder: the user picks a starting point for the kind of testing and gets its templates.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
public sealed class StartingPointUiTests : UiTestBase
{
    public StartingPointUiTests() : base(TestWorkspace.CreateEmptyFirstRun(nameof(StartingPointUiTests))) { }

    [Fact]
    public void ChoosingAStartingPoint_CreatesItsTemplatesInTheEmptyFolder()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var vuota = Path.Combine(Workspace.RootDir, "vuota");
            Directory.CreateDirectory(vuota);
            ElementFinders.WaitTextBoxById(window, "Login_Cartella_TextBox").SetText(vuota);

            Wait.ForCondition(() => ElementFinders.ExistsById(window, "FirstRun_PuntoDiPartenza_Combo"),
                "starting point choice shown for an empty folder");
            ElementFinders.WaitComboBoxById(window, "FirstRun_PuntoDiPartenza_Combo").Select(1);
            ElementFinders.WaitButtonById(window, "FirstRun_PuntoDiPartenza_Button").SafeInvoke();

            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Login_DemoMessaggio_Text"),
                "result message after creating the starting templates", TimeSpan.FromSeconds(60));
            Wait.ForCondition(() => Directory.EnumerateFiles(vuota, "*", SearchOption.AllDirectories).Any(),
                "template files written in the chosen folder", TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ChoosingAStartingPoint_CreatesItsTemplatesInTheEmptyFolder), ex);
        }
    }
}
