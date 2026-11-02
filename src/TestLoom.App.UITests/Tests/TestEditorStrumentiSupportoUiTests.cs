using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Verifica dalla UI il caso di strumenti solo di supporto: la Funzionalità generica del Test resta
/// disponibile e richiesta, gli strumenti si possono aggiungere/rimuovere in modifica e le scelte persistono.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class TestEditorStrumentiSupportoUiTests : UiTestBase
{
    private const string ValidationTool = "ValidationTool-QA";
    private const string CaptureTool = "CaptureTool-QA";
    private const string Funzionalita = "Funzionalita supporto QA";

    public TestEditorStrumentiSupportoUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(TestEditorStrumentiSupportoUiTests));
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);

        state.Master.Strumenti.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = ValidationTool, Tipo = TipoStrumento.Supporto, ModificatoDa = "UITests" },
            new() { Nome = CaptureTool, Tipo = TipoStrumento.Supporto, ModificatoDa = "UITests" },
        });
        var templates = state.Master.TestTemplate.Load();
        var template = templates.Single(t => t.Nome == TestWorkspace.DefaultTemplateName);
        template.Sezioni.Single().Campi.Add(new CampoTemplate
        {
            Tipo = TipoCampoTemplate.CampoSistema,
            CampoSistemaChiave = CampiSistema.StrumentiUsati,
            Obbligatorio = false,
        });
        state.Master.TestTemplate.Save(templates);
        return workspace;
    }

    [Fact]
    public void SoloSupporti_FunzionalitaGenericaEAggiuntaRimozionePersistono()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var editor = shell.GoToTestList().ClickNew();
            editor.ChooseTemplate();
            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            editor.SetDescrizioneBreve("Test con soli strumenti di supporto - QA");
            editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);

            Assert.False(ElementFinders.ExistsById(window, "Editor_NuovaFunzionalita_TextBox"),
                "il template base non mostra Funzionalità quando non è selezionato alcuno strumento di supporto");
            editor.SelezionaStrumento(ValidationTool);
            Assert.True(editor.StrumentoSelezionato(ValidationTool));
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Editor_NuovaFunzionalita_TextBox"),
                "la Funzionalità generica è mostrata quando il Test contiene solo strumenti di supporto");

            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();
            var missingFieldsDialog = App.WaitForWindowByContent("MissingFields_Close_Button");
            Assert.Contains(missingFieldsDialog.FindAllDescendants().Select(e => e.Name),
                nome => (nome ?? string.Empty).Contains("Funzionalità", StringComparison.OrdinalIgnoreCase));
            ElementFinders.WaitButtonById(missingFieldsDialog, "MissingFields_Close_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("MissingFields_Close_Button"), "chiusura popup 'Campi obbligatori mancanti'");

            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            editor.AddFunzionalitaRapida(Funzionalita);
            Wait.ForCondition(() => editor.TestoVisibile(Funzionalita), "la funzionalità generica viene aggiunta al Test");

            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();

            var lista = new TestListPage(App.WaitForMainWindow(), App.WaitForMainWindow());
            var riaperto = lista.OpenOnlyRow();
            riaperto.EnterEditMode();
            Assert.True(riaperto.StrumentoSelezionato(ValidationTool), "lo strumento iniziale resta selezionato dopo il salvataggio");

            riaperto.SelezionaStrumento(CaptureTool);
            Assert.True(riaperto.StrumentoSelezionato(CaptureTool), "il secondo strumento di supporto si aggiunge in modifica");
            riaperto.Save();

            lista = new TestListPage(App.WaitForMainWindow(), App.WaitForMainWindow());
            riaperto = lista.OpenOnlyRow();
            riaperto.EnterEditMode();
            riaperto.SelezionaStrumento(CaptureTool);
            Assert.False(riaperto.StrumentoSelezionato(CaptureTool), "lo strumento di supporto si rimuove in modifica");
            riaperto.Save();

            // Save avvia un RelayCommand asincrono: attendi la nuova fotografia su disco prima della lettura
            // finale, altrimenti il test può osservare il precedente elenco mentre la scrittura è ancora in corso.
            Wait.ForCondition(() =>
            {
                var file = Assert.Single(TestCoverageSeed.FileTestCase(Workspace));
                return TestCoverageSeed.LeggiTestCase(file).StrumentiUsati.SequenceEqual(new[] { ValidationTool });
            }, "rimozione del secondo strumento di supporto persistita");

            using var verifica = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
            var test = Assert.Single(verifica.TestCases.GetAll());
            Assert.Equal(new[] { ValidationTool }, test.StrumentiUsati);
            Assert.Contains(Funzionalita, test.Funzionalita);
            Assert.DoesNotContain(test.Esecuzioni[^1].Condizioni, c => c.Strumento == ValidationTool || c.Strumento == CaptureTool);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(SoloSupporti_FunzionalitaGenericaEAggiuntaRimozionePersistono), ex);
        }
    }
}
