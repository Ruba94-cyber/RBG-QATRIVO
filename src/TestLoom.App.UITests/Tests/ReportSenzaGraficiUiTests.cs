using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Wait = TestLoom.App.UITests.Infrastructure.Wait;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// 6.7.2: la pagina Report non ha piu' la sezione Grafici; i tre grafici restano in Home. Workspace demo completa, dove i
/// grafici della Home compaiono davvero: sulla pagina Report non deve comparire nessuna immagine di grafico e nessuna
/// sezione "Grafici", mentre la Home continua a mostrarli.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Report")]
[Trait("Area", "Report")]
public sealed class ReportSenzaGraficiUiTests : UiTestBase
{
    public ReportSenzaGraficiUiTests() : base(TestWorkspace.CreateDemoCompleta(nameof(ReportSenzaGraficiUiTests))) { }

    [Fact]
    public void Report_NonMostraLaSezioneGrafici_IGraficiRestanoInHome()
    {
        try
        {
            var window = App.WaitForMainWindow(TimeSpan.FromSeconds(30));
            Wait.ForCondition(() => MainShellPage.IsShown(window), "shell principale visibile", TimeSpan.FromSeconds(30));
            var shell = new MainShellPage(window, App);
            shell.DismissNotificationsIfShown();

            shell.GoToHome();
            Wait.ForCondition(() => ContaImmaginiGrafico(window, "Home_Chart_") >= 1,
                "grafici visibili in Home (demo completa)", TimeSpan.FromSeconds(30));

            shell.GoToReport();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Report_MatriceAsseX_ComboBox"),
                "pagina Report caricata", TimeSpan.FromSeconds(30));
            Assert.Equal(0, ContaImmaginiGrafico(window, "Report_Chart_"));
            Assert.DoesNotContain(window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)), t => t.Name == "Grafici");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Report_NonMostraLaSezioneGrafici_IGraficiRestanoInHome), ex); }
    }

    private static int ContaImmaginiGrafico(Window finestra, string prefisso) =>
        finestra.FindAllDescendants(cf => cf.ByControlType(ControlType.Image))
            .Count(i => (i.Properties.AutomationId.ValueOrDefault ?? "").StartsWith(prefisso, StringComparison.Ordinal));
}
