using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using Keyboard = FlaUI.Core.Input.Keyboard;
using FlaUI.Core.WindowsAPI;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// UIA3: the Quick editor's compact operation rows. Keyboard outcomes (1-4), arrow navigation, "next to do", the "only to do"
/// filter and "mark the rest OK" (with confirmation) all drive the same quick outcomes as the buttons. Synthetic data only.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "EditorQuick")]
public sealed class TestEditorQuickFlowUiTests : UiTestBase
{
    public TestEditorQuickFlowUiTests()
        : base(TestWorkspace.CreateLoggedIn(nameof(TestEditorQuickFlowUiTests))) { }

    private static string Ok(string uid) => $"Editor_Condizione_{uid}_QuickOk_Button";

    private static string? FocusedId(FlaUI.Core.AutomationElements.Window window) =>
        window.Automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault;

    [Fact]
    public void KeyboardArrowsNextTodoFilterAndMarkRestOkWorkOnCompactRows()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToTestList().ClickNew();
            editor.ChooseTemplate();
            editor.SwitchToQuickMode();
            Wait.ForCondition(() => editor.NumeroCondizioni() > 0, "first synthetic operation ready");
            editor.AggiungiCondizione();
            editor.AggiungiCondizione();
            Wait.ForCondition(() => editor.UidRigheRapide().Count == 3, "three compact rows");
            var uid = editor.UidRigheRapide();

            // Key 2 on a focused row = NOK, with the reason field appearing right in the row.
            ElementFinders.WaitById(window, Ok(uid[0])).Focus();
            Keyboard.Type(VirtualKeyShort.KEY_2);
            Wait.ForCondition(() => ElementFinders.ExistsById(window, $"Editor_Condizione_{uid[0]}_QuickDescrizioneProblema_TextBox"),
                "key 2 sets NOK and shows the problem field in the row");

            // "Next to do" skips the executed row and lands on the OK button of the next one.
            ElementFinders.WaitButtonById(window, "Editor_Quick_NextTodo_Button").SafeInvoke();
            Wait.ForCondition(() => FocusedId(window) == Ok(uid[1]), "focus moves to the next operation still to do");

            // Arrow down keeps the same button on the following row.
            Keyboard.Type(VirtualKeyShort.DOWN);
            Wait.ForCondition(() => FocusedId(window) == Ok(uid[2]), "arrow down moves to the same button of the next row");

            // Filter: only operations still to do remain (the NOK one disappears), and it can be switched off again.
            var filtro = ElementFinders.WaitById(window, "Editor_Quick_OnlyTodo_Toggle");
            filtro.Patterns.Toggle.Pattern.Toggle();
            Wait.ForCondition(() => editor.UidRigheRapide().Count == 2, "filter hides the executed operation");
            filtro.Patterns.Toggle.Pattern.Toggle();
            Wait.ForCondition(() => editor.UidRigheRapide().Count == 3, "filter off shows every operation again");

            // "Mark the rest OK" asks for confirmation, then only the two operations still to do become OK.
            ElementFinders.WaitButtonById(window, "Editor_Quick_MarkRestOk_Button").SafeInvoke();
            var dialog = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            dialog.ConfirmYes();
            Wait.ForCondition(() => !ElementFinders.WaitButtonById(window, "Editor_Quick_MarkRestOk_Button").IsEnabled,
                "nothing left to mark once every operation has an outcome");
            filtro.Patterns.Toggle.Pattern.Toggle();
            Wait.ForCondition(() => editor.UidRigheRapide().Count == 0, "no operation is left to do");
            Assert.True(ElementFinders.ExistsById(window, "Editor_Quick_OnlyTodo_Toggle"));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(KeyboardArrowsNextTodoFilterAndMarkRestOkWorkOnCompactRows), ex);
        }
    }

    [Fact]
    public void ExpandingARowShowsTheFullCardAndCollapsingRestoresTheCompactFields()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToTestList().ClickNew();
            editor.ChooseTemplate();
            editor.SwitchToQuickMode();
            Wait.ForCondition(() => editor.NumeroCondizioni() > 0, "operation row ready");
            var prefix = $"Editor_Condizione_{editor.GetUidCondizione(0)}_";

            Assert.True(ElementFinders.ExistsById(window, prefix + "QuickNota_TextBox"));
            Assert.False(ElementFinders.ExistsById(window, prefix + "Esito_Combo"), "the full card is not built until the row is expanded");

            var espandi = ElementFinders.WaitById(window, prefix + "Espandi_Button");
            espandi.Patterns.Toggle.Pattern.Toggle();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, prefix + "Esito_Combo"), "expanded row shows the full operation card");
            Assert.False(ElementFinders.ExistsById(window, prefix + "QuickNota_TextBox"), "no duplicated note field while expanded");

            espandi.Patterns.Toggle.Pattern.Toggle();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, prefix + "QuickNota_TextBox"), "collapsing restores the compact fields");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ExpandingARowShowsTheFullCardAndCollapsingRestoresTheCompactFields), ex);
        }
    }
}
