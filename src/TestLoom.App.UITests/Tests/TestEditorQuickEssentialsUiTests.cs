using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// UIA3 regression for the redesigned Quick editor: only required fields by default (the synthetic template has one OPTIONAL field),
/// "Mostra tutti i campi" reveals the rest, per-operation evidence actions exist only in Quick. Synthetic workspace only.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "EditorQuick")]
public sealed class TestEditorQuickEssentialsUiTests : UiTestBase
{
    public TestEditorQuickEssentialsUiTests()
        : base(TestWorkspace.CreateLoggedIn(nameof(TestEditorQuickEssentialsUiTests), funzionalitaNelTemplate: true)) { }

    [Fact]
    public void QuickHidesOptionalFieldsUntilShowAllIsToggledAndFullModeHasNoToggle()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToTestList().ClickNew();
            editor.ChooseTemplate();
            editor.SwitchToQuickMode();

            const string toggle = "Editor_Quick_AllFields_Toggle";
            const string count = "Editor_Quick_HiddenFieldsCount_Text";
            Wait.ForCondition(() => ElementFinders.ExistsById(window, toggle),
                "Quick offers the 'show all fields' switch when optional fields are hidden");
            Assert.True(ElementFinders.ExistsById(window, count));

            ElementFinders.WaitById(window, toggle).Patterns.Toggle.Pattern.Toggle();
            Wait.ForCondition(() => !ElementFinders.ExistsById(window, count),
                "no hidden-fields counter once every field is shown");

            ElementFinders.WaitById(window, toggle).Patterns.Toggle.Pattern.Toggle();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, count),
                "switching back hides the optional fields again");

            editor.SwitchToFullMode();
            Wait.ForCondition(() => !ElementFinders.ExistsById(window, toggle),
                "Full mode never shows the Quick switch: it already shows everything");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(QuickHidesOptionalFieldsUntilShowAllIsToggledAndFullModeHasNoToggle), ex);
        }
    }

    [Fact]
    public void OperationEvidenceActionsExistInQuickOnlyAndNoCounterWithoutEvidence()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToTestList().ClickNew();
            editor.ChooseTemplate();
            editor.SwitchToQuickMode();
            Wait.ForCondition(() => editor.NumeroCondizioni() > 0, "first synthetic operation ready");
            var prefix = $"Editor_Condizione_{editor.GetUidCondizione(0)}_";

            Assert.True(ElementFinders.ExistsById(window, prefix + "EvidenzaFile_Button"));
            Assert.True(ElementFinders.ExistsById(window, prefix + "QuickPasteScreenshot_Button"));
            Assert.False(ElementFinders.ExistsById(window, prefix + "EvidenzeCount_Text"),
                "no evidence is linked yet: the counter must not claim any");

            editor.SwitchToFullMode();
            editor.GoToTab(2);
            Wait.ForCondition(() => !ElementFinders.ExistsById(window, prefix + "EvidenzaFile_Button"),
                "per-operation evidence shortcuts are Quick-only");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(OperationEvidenceActionsExistInQuickOnlyAndNoCounterWithoutEvidence), ex);
        }
    }
}
