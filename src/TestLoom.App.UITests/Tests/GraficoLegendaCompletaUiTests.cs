using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

[Collection("TestLoom UI")]
[Trait("Category", "Coverage")]
[Trait("Area", "Home")]
public sealed class GraficoLegendaCompletaUiTests : UiTestBase
{
    public GraficoLegendaCompletaUiTests() : base(CreaWorkspaceConMoltiModelli()) { }

    [Fact]
    public void Torta_EspandeLaLegendaCompletaConModelloEStatiPerOgniSpicchio()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var home = new MainShellPage(window).GoToHome();
            home.SelezionaGrafico("donut");
            home.ImpostaTipo("donut", "Torta");
            Wait.ForCondition(() => home.TipoRenderizzato("donut") == "Torta", "il grafico esiti viene reso come torta");

            var expander = ElementFinders.WaitById(window, "Grafico_LegendaCompleta_Expander");
            Assert.True(expander.Patterns.ExpandCollapse.TryGetPattern(out var pattern), "la legenda espandibile espone ExpandCollapse");
            pattern!.Expand();

            Wait.ForCondition(() => expander.FindAllDescendants().Any(elemento => elemento.Name.Contains("Altri", StringComparison.Ordinal)),
                "la legenda completa mostra anche il gruppo aggregato e i suoi stati");
            var testi = expander.FindAllDescendants().Select(elemento => elemento.Name).Where(nome => !string.IsNullOrWhiteSpace(nome)).ToList();
            Assert.Contains(testi, nome => nome.Contains("Modello 11", StringComparison.Ordinal));
            Assert.Contains(testi, nome => nome.Contains("Non conforme", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(testi, nome => nome.Contains("Altri", StringComparison.Ordinal));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Torta_EspandeLaLegendaCompletaConModelloEStatiPerOgniSpicchio), ex); }
    }

    private static TestWorkspace CreaWorkspaceConMoltiModelli()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(GraficoLegendaCompletaUiTests), seedTestFixtures: false);
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        var indice = 0;
        foreach (var modello in Enumerable.Range(0, 13).Select(i => $"Modello {i:00}"))
        foreach (var esito in Enum.GetValues<Esito>())
        {
            state.TestCases.Save(new TestCase
            {
                Id = $"TC-GRAFICO-LEGENDA-{indice++:000}",
                DescrizioneBreve = $"Esito {esito}",
                Veicolo = modello,
                Veicoli = new List<string> { modello },
                ModificatoDa = TestWorkspace.DefaultOperatorName,
                Esecuzioni = new List<Esecuzione>
                {
                    new() { Iterazione = 1, Data = DateTime.Today, Operatore = TestWorkspace.DefaultOperatorName, Esito = esito },
                },
            }, isNew: true);
        }
        return workspace;
    }
}
