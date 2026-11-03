using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Paginazione della Lista Test oltre la pagina singola (250 risultati per pagina): con 255 Test seminati le pagine
/// sono due. Si verifica il testo di paginazione e il passaggio con "Successiva", "Precedente" e "Prima pagina". Le
/// righe della griglia sono virtualizzate, quindi il conteggio si basa sul testo e sugli Id visibili, non su tutte le
/// righe. La semina avviene prima dell'avvio di RBG - QATRIVO.exe (255 file JSON sintetici nel workspace isolato).
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class TestListaPaginazioneUiTests : UiTestBase
{
    private const int TotaleTest = 255;

    public TestListaPaginazioneUiTests() : base(CreaWorkspaceConTantiTest()) { }

    private static TestWorkspace CreaWorkspaceConTantiTest()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(TestListaPaginazioneUiTests));
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        var template = TestCoverageSeed.UidTemplatePrincipale(state);
        for (var i = 1; i <= TotaleTest; i++)
        {
            TestCoverageSeed.SeedTest(state, $"TC-PAG-{i:D4}", $"Test di paginazione {i:D4}", template,
                TestWorkspace.DefaultEcuName, Esito.Conforme);
        }
        return workspace;
    }

    private Window Finestra() => App.WaitForMainWindow();

    private string TestoPaginazione() =>
        ElementFinders.WaitById(Finestra(), "TestList_Pagination_Text").AsLabel().Text ?? "";

    private void AttendiPagina(int numero, string motivo) =>
        Wait.ForCondition(() => TestoPaginazione().Contains($"Pagina {numero} / 2", StringComparison.Ordinal), motivo);

    [Fact]
    public void Paginazione_255Test_DuePagineConSuccessivaPrecedenteEPrimaPagina()
    {
        try
        {
            var lista = new MainShellPage(Finestra()).GoToTestList();
            AttendiPagina(1, "prima pagina di due dopo il caricamento");
            Assert.Contains($"{TotaleTest} test", TestoPaginazione(), StringComparison.Ordinal);
            var primaPagina = lista.VisibleRowIds().ToList();
            Assert.NotEmpty(primaPagina);

            ElementFinders.WaitButtonById(Finestra(), "TestList_PaginaSuccessiva_Button").SafeInvoke();
            AttendiPagina(2, "seconda pagina dopo 'Successiva'");
            var secondaPagina = lista.VisibleRowIds().ToList();
            Assert.NotEmpty(secondaPagina);
            Assert.Empty(primaPagina.Intersect(secondaPagina));

            ElementFinders.WaitButtonById(Finestra(), "TestList_PaginaPrecedente_Button").SafeInvoke();
            AttendiPagina(1, "di nuovo la prima pagina dopo 'Precedente'");

            ElementFinders.WaitButtonById(Finestra(), "TestList_PaginaSuccessiva_Button").SafeInvoke();
            AttendiPagina(2, "seconda pagina prima di 'Prima pagina'");
            ElementFinders.WaitButtonById(Finestra(), "TestList_PrimaPagina_Button").SafeInvoke();
            AttendiPagina(1, "'Prima pagina' riporta alla pagina 1");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Paginazione_255Test_DuePagineConSuccessivaPrecedenteEPrimaPagina), ex); }
    }
}
