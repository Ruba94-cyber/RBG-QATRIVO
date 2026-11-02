using TestLoom.App.Models;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// FlaUI UIA3 su un account Viewer reale seminato nei master TestLoom.
/// Verifica i permessi dalla UI, senza alterare politiche/autorizzazioni applicative.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Permissions")]
[Trait("Category", "Regression")]
public sealed class ViewerPermissionsMatrixUiTests : UiTestBase
{
    public ViewerPermissionsMatrixUiTests()
        : base(TestWorkspace.CreateLoggedInAs(nameof(ViewerPermissionsMatrixUiTests), RuoloOperatore.Viewer)) { }

    [Fact]
    public void Viewer_LeggeLeListeMaNonPuoCreareTestCase()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.GoToTestList();
            var newButton = ElementFinders.WaitButtonById(window, "TestList_New_Button");
            Assert.False(newButton.IsEnabled, "Il Viewer non deve poter creare TestCase.");
            Assert.False(shell.HasNavigationError);
            shell.GoToAnomalie();
            Assert.True(ElementFinders.ExistsById(window, "Anomalie_Title_Text"));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Viewer_LeggeLeListeMaNonPuoCreareTestCase), ex); }
    }

    [Fact]
    public void Viewer_NonPuoModificareIlCatalogoOperatoriNeVedereIPinAltrui()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var settings = shell.GoToImpostazioni();
            Assert.False(settings.HaSottoSezione("recovery"));
            var operatori = settings.VaiAOperatori();
            Assert.False(operatori.PuoModificare());
            Assert.False(operatori.PuoSalvare());
            Assert.False(ElementFinders.ExistsById(window, "Oper_PinBlockMaster_Border"));
            Assert.True(ElementFinders.ExistsById(window, "Oper_PinRiservatoMaster_Text"));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Viewer_NonPuoModificareIlCatalogoOperatoriNeVedereIPinAltrui), ex); }
    }

    [Fact]
    public void Viewer_NonPuoSalvareMasterDataEcuERequisiti()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var settings = shell.GoToImpostazioni();

            var componente = settings.VaiAEcu();
            Assert.False(componente.PuoSalvare());
            var requisiti = settings.VaiARequisiti();
            Assert.False(requisiti.PuoSalvare());
            Assert.False(shell.HasNavigationError);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Viewer_NonPuoSalvareMasterDataEcuERequisiti), ex); }
    }
}
