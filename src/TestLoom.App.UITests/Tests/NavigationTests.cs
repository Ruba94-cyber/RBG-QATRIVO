using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

[Collection("TestLoom UI")]
[Trait("Category", "Navigation")]
public sealed class NavigationTests : UiTestBase
{
    public NavigationTests() : base(TestWorkspace.CreateLoggedIn(nameof(NavigationTests))) { }

    [Fact]
    public void Navigazione_TraLePagineChiave_NonMostraErroriENonBloccaLApp()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);

            shell.GoToTestList();
            Assert.False(shell.HasNavigationError, "Test");

            shell.GoToAnomalie();
            Assert.False(shell.HasNavigationError, "Anomalie/CR");

            shell.GoToImpostazioni();
            Assert.False(shell.HasNavigationError, "Impostazioni");

            var home = shell.GoToHome();
            Assert.True(home.IsTitleVisible);
            Assert.False(shell.HasNavigationError, "Home (ritorno)");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Navigazione_TraLePagineChiave_NonMostraErroriENonBloccaLApp), ex);
        }
    }
}
