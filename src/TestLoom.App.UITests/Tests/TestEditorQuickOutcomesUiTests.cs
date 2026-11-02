using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>UIA3 regression: quick outcome actions use the same operation and keep Full mode intact.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "EditorQuick")]
public sealed class TestEditorQuickOutcomesUiTests : UiTestBase
{
    public TestEditorQuickOutcomesUiTests()
        : base(TestWorkspace.CreateLoggedIn(nameof(TestEditorQuickOutcomesUiTests))) { }

    [Fact]
    public void QuickOutcomeButtonsUpdateOperationAndHideInFullMode()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToTestList().ClickNew();
            editor.ChooseTemplate();
            editor.SwitchToQuickMode();
            Wait.ForCondition(() => editor.NumeroCondizioni() > 0, "first synthetic operation ready");
            var uid = editor.GetUidCondizione(0);
            var prefix = $"Editor_Condizione_{uid}_";
            var ok = prefix + "QuickOk_Button";
            var nok = prefix + "QuickNok_Button";
            var deviation = prefix + "QuickDeviation_Button";
            var anomalyLink = prefix + "QuickAnomalyLink_Button";
            var screenshot = prefix + "QuickPasteScreenshot_Button";

            Assert.True(ElementFinders.ExistsById(window, ok));
            Assert.True(ElementFinders.ExistsById(window, nok));
            Assert.True(ElementFinders.ExistsById(window, deviation));
            Assert.False(ElementFinders.ExistsById(window, anomalyLink));
            Assert.True(ElementFinders.ExistsById(window, screenshot));

            ElementFinders.WaitButtonById(window, nok).SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, prefix + "QuickDescrizioneProblema_TextBox"),
                "NOK quick button reveals the problem field right in the row");
            Wait.ForCondition(() => ElementFinders.ExistsById(window, anomalyLink),
                "NOK offers a direct jump to the anomaly workflow");
            ElementFinders.WaitButtonById(window, anomalyLink).SafeInvoke();
            // Return to the operations card after the contextual scroll.
            ElementFinders.WaitButtonById(window, "Editor_Quick_Nav_Operations").SafeInvoke();

            ElementFinders.WaitButtonById(window, deviation).SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, prefix + "QuickDescrizioneDeviazione_TextBox"),
                "deviation quick button reveals the deviation field right in the row");
            Assert.True(ElementFinders.ExistsById(window, anomalyLink));

            ElementFinders.WaitButtonById(window, ok).SafeInvoke();
            Wait.ForCondition(() => !ElementFinders.ExistsById(window, prefix + "QuickDescrizioneDeviazione_TextBox"),
                "Conforme quick button hides deviation field, as the standard selector does");
            Assert.False(ElementFinders.ExistsById(window, anomalyLink));

            editor.SwitchToFullMode();
            Wait.ForCondition(() => !ElementFinders.ExistsById(window, ok),
                "quick outcome shortcuts are absent from Full editor");
            // A new Test opens the Full wizard on Planning, so show the Operations tab.
            editor.GoToTab(2);
            Assert.True(ElementFinders.ExistsById(window, prefix + "Esito_Combo"));
            Assert.False(ElementFinders.ExistsById(window, screenshot));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(QuickOutcomeButtonsUpdateOperationAndHideInFullMode), ex);
        }
    }
}
