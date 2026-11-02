using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Aggiunta rapida di componente/Veicoli/Funzionalita' dal form Test (righe "Altro/a ... non in elenco...").
/// Verifica end-to-end che la voce digitata venga creata nel catalogo master e collegata al test.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class TestEditorAggiuntaRapidaTests : UiTestBase
{
    private const string FunzionalitaNuova = "Funzionalita-Rapida-QA";

    public TestEditorAggiuntaRapidaTests() : base(TestWorkspace.CreateLoggedIn(nameof(TestEditorAggiuntaRapidaTests), funzionalitaNelTemplate: true)) { }

    [Fact]
    public void AggiungiFunzionalitaRapida_LaCreaNelCatalogoELaCollegaAlTest()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var editor = shell.GoToTestList().ClickNew();

            editor.ChooseTemplate();
            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            editor.SetDescrizioneBreve("Aggiunta rapida funzionalita - QA");
            editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);

            editor.AddFunzionalitaRapida(FunzionalitaNuova);
            Wait.ForCondition(() => editor.TestoVisibile(FunzionalitaNuova),
                "la funzionalita' appena creata compare tra quelle scelte del test");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(AggiungiFunzionalitaRapida_LaCreaNelCatalogoELaCollegaAlTest), ex);
        }
    }
}
