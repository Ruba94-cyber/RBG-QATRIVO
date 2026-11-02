using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura P1 per "Crea test collegato" (TestCaseEditorViewModel.CreaTestCollegati): finora
/// CreaTestCollegatoWindow aveva 0 AutomationId (a parte i pulsanti gia' raggiungibili dal loro x:Name) e
/// nessun test lo apriva. Con un solo template di destinazione scelto (il caso comune) la creazione naviga
/// direttamente al nuovo Test collegato appena creato (TestCaseEditorViewModel.CreaTestCollegati, ultima riga):
/// verificato qui insieme al pulsante che compare nel test di partenza per riaprirlo.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class TestCollegatoTests : UiTestBase
{
    public TestCollegatoTests() : base(TestWorkspace.CreateLoggedIn(nameof(TestCollegatoTests))) { }

    [Fact]
    public void CreaTestCollegato_ConUnSoloTemplate_NavigaAlNuovoTest_EComparNelTestDiPartenza()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);

            var testList = shell.GoToTestList();
            var editor = testList.ClickNew();
            editor.ChooseTemplate();
            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            editor.SetDescrizioneBreve("Test di partenza - QA collegati");
            editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);
            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();

            // Salva() torna sempre alla Lista Test al termine di un salvataggio riuscito (mai resta
            // sull'editor, vedi il commento di CreateTestCaseFlow.Run): si riapre l'unica riga appena creata
            // invece di chiamare Refresh() sull'editor, che qui non e' piu' la finestra corrente.
            var listaDopoCreazione = new TestListPage(App.WaitForMainWindow(), App.WaitForMainWindow());
            editor = listaDopoCreazione.OpenOnlyRow();

            var testDiPartenzaId = editor.Title;

            editor.EnterEditMode();
            var dialogoCollegato = editor.CreaTestCollegato(App);
            dialogoCollegato.SelezionaTemplate(TestWorkspace.DefaultTemplateName);
            dialogoCollegato.Crea();

            // Un solo template di destinazione: naviga direttamente al nuovo Test collegato appena creato,
            // nella stessa finestra (CreaTestCollegati chiama _navigaATest, non apre una finestra separata).
            var nuovoEditor = new TestEditorPage(App.WaitForMainWindow());
            Wait.ForCondition(() => nuovoEditor.Title != testDiPartenzaId, "l'editor mostra ora il nuovo test collegato, non quello di partenza");
            var testCollegatoId = nuovoEditor.Title;
            Assert.NotEqual(testDiPartenzaId, testCollegatoId);

            // Il test di partenza deve ora elencare il collegamento appena creato. Si e' rimasti sull'editor
            // del NUOVO test collegato (mai tornati alla lista): "Editor_Cancel_Button" ("Torna alla lista")
            // e' il percorso diretto, invece della voce di navigazione della sidebar (che qui non risultava
            // affidabile per tornare alla lista con modifiche non salvate ancora pendenti).
            nuovoEditor.Cancel();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "TestList_Title_Text") || !App.IsWindowClosedByContent("Confirm_Yes_Button"),
                "Lista Test o dialog 'Modifiche non salvate' dopo 'Torna alla lista'");
            if (!ElementFinders.ExistsById(window, "TestList_Title_Text"))
            {
                var confermaNavigazione = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
                confermaNavigazione.ConfirmYes();
                Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura del dialog 'Modifiche non salvate'");
            }
            var listaDopoCollegamento = new TestListPage(App.WaitForMainWindow(), App.WaitForMainWindow());
            var testDiPartenzaRiaperto = listaDopoCollegamento.OpenRowById(testDiPartenzaId);
            Wait.ForCondition(() => testDiPartenzaRiaperto.HaTestCollegato(testCollegatoId),
                $"il test di partenza elenca il test collegato appena creato ({testCollegatoId})");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(CreaTestCollegato_ConUnSoloTemplate_NavigaAlNuovoTest_EComparNelTestDiPartenza), ex);
        }
    }

    /// <summary>Percorso "sporco": con modifiche non salvate, "Crea test collegato" chiede "Salva e crea";
    /// confermando si esegue il Salva normale e solo dopo si apre la creazione e si naviga al nuovo test.</summary>
    [Fact]
    public void CreaTestCollegato_ConModifichePendenti_ChiedeSalvaECrea_Poi_NavigaAlNuovoTest()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);

            var testList = shell.GoToTestList();
            var editor = testList.ClickNew();
            editor.ChooseTemplate();
            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            editor.SetDescrizioneBreve("Test di partenza - sporco");
            editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);
            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();

            var listaDopoCreazione = new TestListPage(App.WaitForMainWindow(), App.WaitForMainWindow());
            editor = listaDopoCreazione.OpenOnlyRow();
            var testDiPartenzaId = editor.Title;

            editor.EnterEditMode();
            editor.SetDescrizioneBreve("Test di partenza - sporco, modificato");

            ElementFinders.WaitButtonById(window, "Editor_CreaTestCollegato_Button").SafeInvoke();
            var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            conferma.ConfirmYes();
            var dialogoCollegato = new CreaTestCollegatoPage(App.WaitForWindowByContent("CreaTestCollegato_Crea_Button"));
            dialogoCollegato.SelezionaTemplate(TestWorkspace.DefaultTemplateName);
            dialogoCollegato.Crea();

            var nuovoEditor = new TestEditorPage(App.WaitForMainWindow());
            Wait.ForCondition(() => nuovoEditor.Title != testDiPartenzaId, "l'editor mostra il nuovo test collegato dopo 'Salva e crea'");
            // Nessun secondo dialogo "Modifiche non salvate": il test di partenza e' stato salvato prima.
            Assert.True(App.IsWindowClosedByContent("Confirm_Yes_Button"));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(CreaTestCollegato_ConModifichePendenti_ChiedeSalvaECrea_Poi_NavigaAlNuovoTest), ex);
        }
    }
}
