using TestLoom.App.Models;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using Keyboard = FlaUI.Core.Input.Keyboard;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Copertura FlaUI dell'editor Test non ancora esercitata: Annulla (con e senza modifiche, risposta a conferma),
/// Esc e scorciatoie Ctrl+S/Ctrl+N/Ctrl+F, validazione dei campi obbligatori senza scrittura su disco, Ripeti e
/// Rimuovi operazione persistiti nel JSON. Ogni test verifica l'esito visibile e l'effetto sul filesystem.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class TestEditorCoverageUiTests : UiTestBase
{
    public TestEditorCoverageUiTests() : base(TestWorkspace.CreateLoggedIn(nameof(TestEditorCoverageUiTests))) { }

    private Window Finestra() => App.WaitForMainWindow();

    private TestEditorPage NuovoTest(string descrizione, bool conEcu = true)
    {
        var editor = new MainShellPage(Finestra()).GoToTestList().ClickNew();
        editor.ChooseTemplate();
        editor.GoToTab(CreateTestCaseFlow.TabDettagli);
        editor.SetDescrizioneBreve(descrizione);
        if (conEcu) editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);
        return editor;
    }

    private TestEditorPage ApriInModifica(TestListPage lista)
    {
        var editor = lista.OpenOnlyRow();
        editor.EnterEditMode();
        return editor;
    }

    private TestListPage RicaricaLista() => new(Finestra(), Finestra());

    private string UnicoFileTestCase() => Assert.Single(TestCoverageSeed.FileTestCase(Workspace));

    private void ChiudiPopupCampiMancantiSeComparso()
    {
        if (!ElementFinders.ExistsById(Finestra(), "MissingFields_Close_Button")) return;
        ElementFinders.WaitButtonById(App.WaitForWindowByContent("MissingFields_Close_Button"), "MissingFields_Close_Button").SafeInvoke();
        Wait.ForCondition(() => App.IsWindowClosedByContent("MissingFields_Close_Button"), "chiusura popup 'Campi obbligatori mancanti'");
    }

    [Fact]
    public void Annulla_InModificaSenzaModifiche_TornaAllaListaSenzaScrivereIlFile()
    {
        try
        {
            var lista = CreateTestCaseFlow.Run(App, new MainShellPage(Finestra()), "QA annulla - senza modifiche", TestWorkspace.DefaultEcuName);
            var percorso = UnicoFileTestCase();
            var hashPrima = TestCoverageSeed.HashFile(percorso);

            var editor = ApriInModifica(lista);
            editor.Cancel();

            var listaDopo = RicaricaLista();
            Assert.Equal(1, listaDopo.RowCount());
            Assert.True(App.IsWindowClosedByContent("Confirm_Yes_Button"), "Senza modifiche non deve comparire alcuna conferma di abbandono.");
            Assert.Equal(hashPrima, TestCoverageSeed.HashFile(percorso));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Annulla_InModificaSenzaModifiche_TornaAllaListaSenzaScrivereIlFile), ex); }
    }

    [Fact]
    public void Annulla_ConModificheNonSalvate_RispondendoNo_RestaNellEditorSenzaScrivere()
    {
        try
        {
            var lista = CreateTestCaseFlow.Run(App, new MainShellPage(Finestra()), "QA annulla - risposta No", TestWorkspace.DefaultEcuName);
            var percorso = UnicoFileTestCase();
            var hashPrima = TestCoverageSeed.HashFile(percorso);

            var editor = ApriInModifica(lista);
            const string modificata = "QA annulla - modifica non confermata";
            editor.SetDescrizioneBreve(modificata);
            editor.Cancel();

            var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            Assert.False(string.IsNullOrWhiteSpace(conferma.Message));
            conferma.ConfirmNo();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura conferma dopo 'No'");

            Assert.True(editor.CanSave, "Dopo 'No' l'editor resta in modifica, con Salva visibile.");
            Assert.Equal(modificata, ElementFinders.WaitTextBoxById(Finestra(), "Editor_DescrizioneBreve_TextBox").Text);
            Assert.Equal(hashPrima, TestCoverageSeed.HashFile(percorso));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Annulla_ConModificheNonSalvate_RispondendoNo_RestaNellEditorSenzaScrivere), ex); }
    }

    [Fact]
    public void Annulla_ConModificheNonSalvate_RispondendoSi_ScartaLeModificheSenzaScrivere()
    {
        try
        {
            var lista = CreateTestCaseFlow.Run(App, new MainShellPage(Finestra()), "QA annulla - risposta Si", TestWorkspace.DefaultEcuName);
            var percorso = UnicoFileTestCase();
            var hashPrima = TestCoverageSeed.HashFile(percorso);

            var editor = ApriInModifica(lista);
            editor.SetDescrizioneBreve("QA annulla - da scartare");
            editor.Cancel();
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura conferma dopo 'Si'");

            var listaDopo = RicaricaLista();
            Assert.Equal(1, listaDopo.RowCount());
            Assert.Equal(hashPrima, TestCoverageSeed.HashFile(percorso));
            Assert.Equal("QA annulla - risposta Si", TestCoverageSeed.LeggiTestCase(percorso).DescrizioneBreve);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Annulla_ConModificheNonSalvate_RispondendoSi_ScartaLeModificheSenzaScrivere), ex); }
    }

    [Fact]
    public void Esc_InModificaConModificheNonSalvate_ChiedeConferma_NoRestaNellEditor()
    {
        try
        {
            var lista = CreateTestCaseFlow.Run(App, new MainShellPage(Finestra()), "QA esc - originale", TestWorkspace.DefaultEcuName);
            var percorso = UnicoFileTestCase();
            var hashPrima = TestCoverageSeed.HashFile(percorso);

            var editor = ApriInModifica(lista);
            editor.SetDescrizioneBreve("QA esc - modifica");
            ElementActions.PressKey(Finestra(), VirtualKeyShort.ESCAPE);

            var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            conferma.ConfirmNo();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura conferma Esc dopo 'No'");

            Assert.True(editor.CanSave, "Esc con modifiche e risposta 'No' non deve far uscire dall'editor.");
            Assert.Equal(hashPrima, TestCoverageSeed.HashFile(percorso));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Esc_InModificaConModificheNonSalvate_ChiedeConferma_NoRestaNellEditor), ex); }
    }

    [Fact]
    public void CtrlS_InModifica_SalvaSenzaPulsanteEAggiornaIlFile()
    {
        try
        {
            var lista = CreateTestCaseFlow.Run(App, new MainShellPage(Finestra()), "QA ctrl-s - originale", TestWorkspace.DefaultEcuName);
            var percorso = UnicoFileTestCase();

            var editor = ApriInModifica(lista);
            const string salvata = "QA ctrl-s - salvato da tastiera";
            editor.SetDescrizioneBreve(salvata);
            Finestra().Focus();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S);

            var listaDopo = RicaricaLista();
            Assert.Equal(1, listaDopo.RowCount());
            Wait.ForCondition(() => TestCoverageSeed.LeggiTestCase(percorso).DescrizioneBreve == salvata,
                "descrizione salvata sul disco dopo Ctrl+S");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CtrlS_InModifica_SalvaSenzaPulsanteEAggiornaIlFile), ex); }
    }

    [Fact]
    public void CtrlN_DallaListaApreIlWizardDiNuovoTest_EsSenzaSalvataggioNonScriveNulla()
    {
        try
        {
            var lista = new MainShellPage(Finestra()).GoToTestList();
            Assert.Equal(0, lista.RowCount());
            Finestra().Focus();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_N);

            Wait.ForCondition(() => ElementFinders.ExistsById(Finestra(), "Editor_TemplatePicker_Button"),
                "wizard di creazione Test aperto con Ctrl+N");

            ElementActions.PressKey(Finestra(), VirtualKeyShort.ESCAPE);
            var listaDopo = RicaricaLista();
            Assert.Equal(0, listaDopo.RowCount());
            Assert.Empty(TestCoverageSeed.FileTestCase(Workspace));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CtrlN_DallaListaApreIlWizardDiNuovoTest_EsSenzaSalvataggioNonScriveNulla), ex); }
    }

    [Fact]
    public void CtrlF_DallaListaPortaIlFuocoAlCampoDiRicerca()
    {
        try
        {
            new MainShellPage(Finestra()).GoToTestList();
            Finestra().Focus();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_F);

            Wait.ForCondition(() => App.SafeFocusedElementId() == "TestList_Search_TextBox",
                "il campo di ricerca della Lista Test riceve il fuoco con Ctrl+F");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CtrlF_DallaListaPortaIlFuocoAlCampoDiRicerca), ex); }
    }

    [Fact]
    public void Salva_SenzaEcu_MostraCampiMancantiEVieteLaScrittura()
    {
        try
        {
            var editor = NuovoTest("QA validazione - senza componente", conEcu: false);
            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();

            var popup = App.WaitForWindowByContent("MissingFields_Close_Button");
            ElementFinders.WaitButtonById(popup, "MissingFields_Close_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("MissingFields_Close_Button"), "chiusura popup 'Campi obbligatori mancanti'");

            Assert.True(editor.HasValidationErrors, "Il banner di validazione resta visibile dopo la chiusura del popup.");
            Assert.True(editor.CanSave, "Senza componente il Test non viene salvato: l'editor resta aperto.");
            Assert.Empty(TestCoverageSeed.FileTestCase(Workspace));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Salva_SenzaEcu_MostraCampiMancantiEVieteLaScrittura), ex); }
    }

    [Fact]
    public void Salva_NonConformeSenzaDescrizioneDelProblema_BloccaIlSalvataggioSenzaFile()
    {
        try
        {
            var editor = NuovoTest("QA validazione - NC senza problema");
            editor.GoToTab(2);
            Wait.ForCondition(() => editor.NumeroCondizioni() == 1, "operazione iniziale presente");
            editor.SetEsitoCondizione(0, "Non Conforme");
            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();

            Wait.ForCondition(() => editor.HasValidationErrors || ElementFinders.ExistsById(Finestra(), "MissingFields_Close_Button"),
                "errore di validazione: il problema osservato e' obbligatorio per Non Conforme");
            ChiudiPopupCampiMancantiSeComparso();

            Assert.True(editor.CanSave, "Il salvataggio e' rifiutato: l'editor resta aperto.");
            Assert.Empty(TestCoverageSeed.FileTestCase(Workspace));
            Assert.Empty(TestCoverageSeed.FileSegnalazioni(Workspace));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Salva_NonConformeSenzaDescrizioneDelProblema_BloccaIlSalvataggioSenzaFile), ex); }
    }

    [Fact]
    public void Ripeti_SalvataDopoIlSalvataggio_RegistraLOrigineNelJson()
    {
        try
        {
            var editor = NuovoTest("QA ripeti - persistenza");
            editor.GoToTab(2);
            Wait.ForCondition(() => editor.NumeroCondizioni() == 1, "operazione iniziale presente");
            editor.RipetiCondizione(0);
            Wait.ForCondition(() => editor.NumeroCondizioni() == 2, "ripetizione aggiunta");
            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();

            var riaperto = RicaricaLista().OpenOnlyRow();
            Wait.ForCondition(() => riaperto.NumeroCondizioni() == 2, "due operazioni recuperate dal disco");

            var condizioni = TestCoverageSeed.LeggiTestCase(UnicoFileTestCase()).Esecuzioni[^1].Condizioni;
            Assert.Equal(2, condizioni.Count);
            Assert.Null(condizioni[0].OperazioneOrigineUid);
            Assert.Equal(condizioni[0].Uid, condizioni[1].OperazioneOrigineUid);
            Assert.Equal(Esito.DaEseguire, condizioni[1].Esito);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Ripeti_SalvataDopoIlSalvataggio_RegistraLOrigineNelJson), ex); }
    }

    [Fact]
    public void Rimuovi_OperazioneSalvata_LaJsonContieneSoloLaRimasta()
    {
        try
        {
            var editor = NuovoTest("QA rimuovi - persistenza");
            editor.GoToTab(2);
            Wait.ForCondition(() => editor.NumeroCondizioni() == 1, "operazione iniziale presente");
            var uidPrimo = editor.GetUidCondizione(0);
            editor.AggiungiCondizione();
            Wait.ForCondition(() => editor.NumeroCondizioni() == 2, "seconda operazione aggiunta");
            editor.RimuoviCondizione(1);
            Wait.ForCondition(() => editor.NumeroCondizioni() == 1, "seconda operazione rimossa");
            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();

            var condizioni = TestCoverageSeed.LeggiTestCase(UnicoFileTestCase()).Esecuzioni[^1].Condizioni;
            var uidSalvati = condizioni.Select(c => c.Uid).ToList();
            Assert.Single(uidSalvati);
            Assert.Equal(uidPrimo, uidSalvati[0].ToString());
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Rimuovi_OperazioneSalvata_LaJsonContieneSoloLaRimasta), ex); }
    }
}
