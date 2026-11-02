using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Real UIA3 coverage of the two-step Quick Excel/note paste; isolated synthetic data.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "EditorQuick")]
public sealed class TestEditorQuickPastePreviewUiTests : UiTestBase
{
    public TestEditorQuickPastePreviewUiTests()
        : base(TestWorkspace.CreateLoggedIn(nameof(TestEditorQuickPastePreviewUiTests))) { }

    [Fact]
    public void PreviewRequiresConfirmationAndCanBeCancelled()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToTestList().ClickNew();
            editor.ChooseTemplate();
            editor.SwitchToQuickMode();
            if (ElementFinders.ExistsById(window, "Editor_AvviaPrimaEsecuzione_Button"))
                ElementFinders.WaitButtonById(window, "Editor_AvviaPrimaEsecuzione_Button").SafeInvoke();

            ElementFinders.WaitById(window, "Editor_QuickPaste_Expander").SafeInvoke();
            var pastedText = ElementFinders.WaitTextBoxById(window, "Editor_QuickPaste_TextBox");
            pastedText.SetText("Synthetic first step\nSynthetic second step");
            Assert.False(ElementFinders.ExistsById(window, "Editor_QuickPaste_PreviewSummary_Text"));

            ElementFinders.WaitButtonById(window, "Editor_QuickPaste_Preview_Button").SafeInvoke();
            Wait.ForCondition(
                () => ElementFinders.ExistsById(window, "Editor_QuickPaste_PreviewSummary_Text"),
                "validated quick-paste preview appears before Test changes");
            Assert.True(ElementFinders.WaitButtonById(window, "Editor_QuickPaste_Add_Button").IsEnabled);

            ElementFinders.WaitButtonById(window, "Editor_QuickPaste_CancelPreview_Button").SafeInvoke();
            Wait.ForCondition(
                () => !ElementFinders.ExistsById(window, "Editor_QuickPaste_PreviewSummary_Text"),
                "cancel hides preview without inserting");
            Assert.Equal("Synthetic first step\nSynthetic second step", pastedText.Text);

            ElementFinders.WaitButtonById(window, "Editor_QuickPaste_Preview_Button").SafeInvoke();
            ElementFinders.WaitButtonById(window, "Editor_QuickPaste_Add_Button").SafeInvoke();
            Wait.ForCondition(
                () => !ElementFinders.ExistsById(window, "Editor_QuickPaste_PreviewSummary_Text"),
                "confirmation clears preview after insertion");
            Assert.Equal("", ElementFinders.WaitTextBoxById(window, "Editor_QuickPaste_TextBox").Text);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(PreviewRequiresConfirmationAndCanBeCancelled), ex);
        }
    }
}
