using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
public sealed class SearchFilterTests : UiTestBase
{
    public SearchFilterTests() : base(TestWorkspace.CreateLoggedIn(nameof(SearchFilterTests))) { }

    [Fact]
    public void RicercaTestoLibero_ConCorrispondenza_MostraSoloIlTestCreato()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            CreateTestCaseFlow.Run(App, shell, "QA smoke - filtro cercabile", TestWorkspace.DefaultEcuName);

            var testList = shell.GoToTestList();
            testList.Search("cercabile");
            Assert.False(testList.IsEmpty);
            Assert.Equal(1, testList.RowCount());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(RicercaTestoLibero_ConCorrispondenza_MostraSoloIlTestCreato), ex);
        }
    }

    [Fact]
    public void RicercaTestoLibero_SenzaCorrispondenza_MostraElencoVuoto()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            CreateTestCaseFlow.Run(App, shell, "QA smoke - test reale presente", TestWorkspace.DefaultEcuName);

            var testList = shell.GoToTestList();
            testList.Search("stringa-che-non-esiste-in-nessun-test-xyz");
            Assert.True(testList.IsEmpty);
            Assert.Equal(0, testList.RowCount());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(RicercaTestoLibero_SenzaCorrispondenza_MostraElencoVuoto), ex);
        }
    }
}
