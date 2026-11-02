using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

[Collection("TestLoom UI")]
[Trait("Category", "Coverage")]
[Trait("Area", "Test")]
public sealed class TestSelezioneOrdineUiTests : UiTestBase
{
    public TestSelezioneOrdineUiTests() : base(TestWorkspace.CreateLoggedIn(nameof(TestSelezioneOrdineUiTests), funzionalitaNelTemplate: true, veicoloNelTemplate: true)) { }

    [Fact]
    public void NuovoTest_ScegliePrimaVeicoloPoiEcuPoiFunzionalita()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var editor = shell.GoToTestList().ClickNew();
            editor.ChooseTemplate();
            ElementFinders.WaitButtonById(window, "Editor_Tab_1_Button").SafeInvoke();

            var veicolo = ElementFinders.WaitById(window, "Editor_Veicolo_ComboBox");
            var componente = ElementFinders.WaitById(window, "Editor_ScegliEcu_Button");
            var funzionalita = ElementFinders.WaitById(window, "Editor_ScegliFunzionalita_Button");
            Wait.ForCondition(() => veicolo.BoundingRectangle.Height > 0 && componente.BoundingRectangle.Height > 0 && funzionalita.BoundingRectangle.Height > 0,
                "i tre campi della selezione sono visibili");

            Assert.True(veicolo.BoundingRectangle.Top < componente.BoundingRectangle.Top,
                "Veicolo precede componente nel modulo");
            Assert.True(componente.BoundingRectangle.Top < funzionalita.BoundingRectangle.Top,
                "Componente precede Funzionalità nel modulo");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(NuovoTest_ScegliePrimaVeicoloPoiEcuPoiFunzionalita), ex); }
    }
}
