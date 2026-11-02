using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Real UIA3 smoke coverage for the Quick/Full editor presentation:
/// navigation must remain inaccessible before a Test template is chosen;
/// the history button must not point to an empty section for a new Test.
/// No shared or corporate data is used.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "EditorQuick")]
public sealed class TestEditorQuickNavigationUiTests : UiTestBase
{
    public TestEditorQuickNavigationUiTests()
        : base(TestWorkspace.CreateLoggedIn(nameof(TestEditorQuickNavigationUiTests))) { }

    [Fact]
    public void QuickNavigationIsAvailableAfterTemplateAndFullModeRemainsAccessible()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToTestList().ClickNew();
            editor.SwitchToQuickMode();
            Assert.False(ElementFinders.ExistsById(window, "Editor_Quick_Nav_Details"));

            editor.ChooseTemplate();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Editor_Quick_Nav_Details"),
                "Quick navigation displayed after template choice");
            Assert.True(ElementFinders.ExistsById(window, "Editor_Quick_Nav_Operations"));
            Assert.True(ElementFinders.ExistsById(window, "Editor_Quick_Nav_Attachments"));
            Assert.True(ElementFinders.ExistsById(window, "Editor_Quick_Nav_Ticketing"));
            Assert.True(ElementFinders.ExistsById(window, "Editor_Quick_Nav_Planning"));
            Assert.False(ElementFinders.ExistsById(window, "Editor_Quick_Nav_History"));

            ElementFinders.WaitButtonById(window, "Editor_Quick_Nav_Ticketing").SafeInvoke();
            editor.SwitchToFullMode();
            Wait.ForCondition(() => !ElementFinders.ExistsById(window, "Editor_Quick_Nav_Details"),
                "Quick navigation hidden in Full mode");
            editor.SwitchToQuickMode();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Editor_Quick_Nav_Ticketing"),
                "Quick navigation restored after switching back");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(QuickNavigationIsAvailableAfterTemplateAndFullModeRemainsAccessible), ex);
        }
    }
}
