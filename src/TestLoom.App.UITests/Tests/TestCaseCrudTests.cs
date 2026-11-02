using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

[Collection("TestLoom UI")]
public sealed class TestCaseCrudTests : UiTestBase
{
    public TestCaseCrudTests() : base(TestWorkspace.CreateLoggedIn(nameof(TestCaseCrudTests))) { }

    [Fact]
    [Trait("Category", "CRUD")]
    [Trait("Category", "Smoke")]
    public void CreaTestCase_ConDescrizioneEEcu_VieneSalvatoEAppareInLista()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            const string descrizione = "QA smoke - creazione test";

            // TestCaseEditorViewModel.Salva() torna sempre alla Lista Test dopo un salvataggio riuscito: il
            // flow ritorna già la TestListPage, non l'editor (vedi CreateTestCaseFlow).
            var testList = CreateTestCaseFlow.Run(App, shell, descrizione, TestWorkspace.DefaultEcuName);

            Assert.Equal(1, testList.RowCount());
            Assert.Single(testList.VisibleRowIds());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(CreaTestCase_ConDescrizioneEEcu_VieneSalvatoEAppareInLista), ex);
        }
    }

    [Fact]
    [Trait("Category", "Forms")]
    [Trait("Category", "Regression")]
    public void CreaTestCase_SenzaDescrizioneBreveObbligatoria_NonSalvaEMostraErrore()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);

            var testList = shell.GoToTestList();
            var editor = testList.ClickNew();
            editor.ChooseTemplate();
            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            // Descrizione breve lasciata vuota di proposito: è l'unico campo testo obbligatorio del template
            // minimo di QA, quindi Salva deve fallire la validazione invece di scrivere un TestCase incompleto
            // — e (a differenza di un salvataggio riuscito) NON deve tornare alla lista da solo.
            editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);
            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();

            // Un salvataggio con campi obbligatori mancanti apre sempre anche il popup dedicato (mai solo il
            // banner in pagina), con l'elenco cliccabile dei campi da completare — va chiuso prima di poter
            // interagire di nuovo con la finestra principale (è modale).
            var missingFieldsDialog = App.WaitForWindowByContent("MissingFields_Close_Button");
            missingFieldsDialog.FindFirstDescendant(cf => cf.ByAutomationId("MissingFields_Close_Button"))!.AsButton().SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("MissingFields_Close_Button"), "chiusura popup 'Campi obbligatori mancanti'");

            Assert.True(editor.HasValidationErrors, "Il banner errori in pagina deve restare visibile anche dopo aver chiuso il popup.");

            // il componente scelta prima del Salva fallito resta come modifica non salvata: uscire dalla pagina (anche
            // solo navigando in sidebar, non solo chiudendo l'app) chiede conferma — coerente con
            // UnsavedChangesTests, qui sul percorso di navigazione invece che di chiusura finestra. La voce
            // "Test" in sidebar è già selezionata mentre si crea un nuovo Test (fa parte della stessa sezione),
            // quindi non basta a far scattare un cambio pagina: serve una sezione davvero diversa, "Home".
            shell.NavigateTo("home");
            var confirmWindow = App.WaitForWindowByContent("Confirm_Yes_Button");
            new ConfirmDialogPage(confirmWindow).ConfirmYes();

            testList = shell.GoToTestList();
            Assert.Equal(0, testList.RowCount());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(CreaTestCase_SenzaDescrizioneBreveObbligatoria_NonSalvaEMostraErrore), ex);
        }
    }

    [Fact]
    [Trait("Category", "CRUD")]
    public void ModificaTestCaseEsistente_CambiaDescrizioneESalva_SiRiflette()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            CreateTestCaseFlow.Run(App, shell, "QA smoke - descrizione originale", TestWorkspace.DefaultEcuName);

            var testList = shell.GoToTestList();
            var editor = testList.OpenOnlyRow();
            // Un test ESISTENTE non ha la striscia di schede (solo in creazione): apre già tutte le sezioni,
            // ma parte in sola lettura finché non si preme "Modifica".
            editor.EnterEditMode();
            editor.SetDescrizioneBreve("QA smoke - descrizione modificata");
            editor.Save();

            testList = new TestListPage(App.WaitForMainWindow(), App.WaitForMainWindow());
            testList.Search("modificata");
            Assert.Equal(1, testList.RowCount());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ModificaTestCaseEsistente_CambiaDescrizioneESalva_SiRiflette), ex);
        }
    }
}
