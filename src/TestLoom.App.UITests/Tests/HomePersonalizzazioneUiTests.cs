using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Tests;

/// <summary>Home personalizzabile (sezioni nascoste e riordinate, preferenza locale al PC) e sezione "Recenti" nella
/// vera UI: un Test aperto dalla Lista Test compare in Recenti e riapre il suo editor dalla Home.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Home")]
[Trait("Category", "Regression")]
public sealed class HomePersonalizzazioneUiTests : UiTestBase
{
    private const string TestRecenteId = "TC-RECENTI-QA";

    public HomePersonalizzazioneUiTests() : base(CreaWorkspaceConTestDaAprire()) { }

    private static TestWorkspace CreaWorkspaceConTestDaAprire()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(HomePersonalizzazioneUiTests));
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        var template = state.Master.TestTemplate.Load().Single(t => t.Nome == TestWorkspace.DefaultTemplateName);
        state.TestCases.Save(new TestCase
        {
            Id = TestRecenteId,
            DescrizioneBreve = "Test QA recente",
            Componente = new EcuSelection(new[] { TestWorkspace.DefaultEcuName }),
            TestTemplateUid = template.Uid,
            ModificatoDa = "UITests",
        }, isNew: true);
        return workspace;
    }

    /// <summary>La sezione e' visibile a schermo: presente nell'albero UI Automation, con area e non fuori schermo.
    /// Una sezione nascosta non ha un'area utile.</summary>
    private static bool SezioneVisibile(Window window, string id)
    {
        var sezione = ElementFinders.TryFindById(window, "Home_Sezione_" + id);
        return sezione != null && !sezione.Properties.IsOffscreen.ValueOrDefault && sezione.BoundingRectangle.Height > 1;
    }

    /// <summary>Apre il pannello "Personalizza Home" solo se non e' gia' aperto (il suo stato vive nel ViewModel).</summary>
    private static void ApriPersonalizzazioneSeChiusa(Window window)
    {
        if (ElementFinders.ExistsById(window, "Home_Layout_grafici_CheckBox")) return;
        ElementFinders.WaitButtonById(window, "Home_Personalizza_Button").SafeInvoke();
        Wait.ForCondition(() => ElementFinders.ExistsById(window, "Home_Layout_grafici_CheckBox"), "pannello Personalizza Home aperto");
    }

    /// <summary>Posizione verticale della sezione nella pagina: confronto relativo fra sezioni, lo scroll le sposta tutte insieme.</summary>
    private static int PosizioneSezione(Window window, string id) =>
        ElementFinders.WaitById(window, "Home_Sezione_" + id).BoundingRectangle.Top;

    [Fact]
    public void TestAperto_DallaListaTest_ComparInRecenti_ERiapreLEditorDallaHome()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var lista = shell.GoToTestList();
            lista.OpenRowById(TestRecenteId);
            Wait.ForCondition(() => ElementFinders.TryFindById(window, "Editor_Title_Text") is { } titolo && titolo.AsLabel().Text == TestRecenteId,
                "editor del Test aperto dalla Lista Test");

            shell.GoToHome();
            var idPulsante = $"Home_Recente_test_{TestRecenteId}_Button";
            Wait.ForCondition(() => ElementFinders.ExistsById(window, idPulsante), "il Test aperto compare in Recenti");

            ElementFinders.WaitButtonById(window, idPulsante).SafeInvoke();
            Wait.ForCondition(() => ElementFinders.TryFindById(window, "Editor_Title_Text") is { } titolo && titolo.AsLabel().Text == TestRecenteId,
                "il click su Recenti riapre l'editor del Test");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(TestAperto_DallaListaTest_ComparInRecenti_ERiapreLEditorDallaHome), ex); }
    }
}
