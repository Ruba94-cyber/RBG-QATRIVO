using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura P1 per la cronologia multi-revisione e il menu REV (TestCaseEditorView, commento:
/// "una nuova revisione si crea da sola al salvataggio se Dettagli/Operazioni sono cambiati rispetto
/// all'ultimo salvataggio"): finora nessun test riapriva un Test salvato, cambiava l'esito di
/// un'operazione e salvava di nuovo per verificare che nascesse davvero una seconda revisione, ne'
/// navigava tra le revisioni dal menu REV (Editor_MenuRev_Combo, 0 AutomationId prima di questo commit).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class TestEditorRevisioniTests : UiTestBase
{
    public TestEditorRevisioniTests() : base(TestWorkspace.CreateLoggedIn(nameof(TestEditorRevisioniTests))) { }

    [Fact]
    public void CambiareEsitoESalvareDiNuovo_CreaUnaSecondaRevisione_NavigabileDalMenuRev()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);

            var testList = shell.GoToTestList();
            var editor = testList.ClickNew();
            editor.ChooseTemplate();
            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            editor.SetDescrizioneBreve("Editor revisioni - QA");
            editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);

            // ChooseTemplate() ha gia' creato la prima reiterazione/condizione (SceglieTemplate ->
            // AggiungiIterazione+AggiungiCondizione, comportamento dell'app dalla 3.4.0): nessun "Avvia prima
            // esecuzione" da premere qui, il bottone non esiste piu' una volta scelto il tipo test.
            editor.GoToTab(2);
            editor.SetEsitoCondizione(0, "Conforme");

            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();
            // Salva() torna sempre alla Lista Test dopo un salvataggio riuscito: si riapre da lì, non c'è un
            // "Editor_Title_Text" da attendere sulla stessa finestra (non è più l'editor).

            var lista = new TestListPage(App.WaitForMainWindow(), App.WaitForMainWindow());
            var riaperto = lista.OpenOnlyRow();
            riaperto.EnterEditMode();
            // Niente GoToTab qui: la striscia di schede "Editor_Tab_N_Button" esiste SOLO in creazione
            // (TestCaseEditorViewModel.MostraSchedeCreazione => TipoTestScelto && IsNew, vedi anche il
            // commento su MostraSchedaProve/MostraSchedaRiferimenti). Un test ESISTENTE riaperto mostra già
            // tutte le sezioni insieme (MostraSchedaProve/MostraSchedaRiferimenti => !IsNew || ... sono vere
            // a prescindere), quindi Reiterazioni e Riferimenti sono già nell'albero UI senza bisogno di
            // cambiare scheda.

            // Cambia l'esito dell'unica operazione della revisione corrente: per il commento del ViewModel
            // questo e' un cambiamento di "Operazioni" rispetto all'ultimo salvataggio, quindi Salva deve
            // creare una seconda revisione invece di sovrascrivere la prima.
            riaperto.SetEsitoCondizione(0, "Non Conforme");
            // "Non Conforme" rende obbligatorio "Problema o risultato osservato" (Condizione.RichiedeDescrizioneProblema
            // => Esito == NonConforme, TestCase.cs riga 541): senza compilarlo Salva() rifiuta il salvataggio e
            // resta sull'editor, mai tornando alla Lista Test attesa subito sotto.
            riaperto.SetDescrizioneProblema(0, "Non conforme al secondo giro - QA");

            // Stesso principio già corretto nel commit 3569178 (ClassificazioneSegnalazioneNelTestTests):
            // ValidaPerSalvataggio (TestCaseEditorViewModel.Salva) rifiuta SEMPRE il salvataggio mentre esiste una
            // segnalazione non classificata/collegata/esclusa (ValClassificaPrimaConclusione), indipendentemente
            // dallo stato del Test. "Non Conforme" genera dal vivo una segnalazione nel pannello Anomalie
            // (SincronizzaEsitoCondizioni -> CaricaPannelloAnomalie): va classificata qui, PRIMA di Salva(), non
            // dopo (un Salva "intermedio" con la segnalazione ancora da classificare non esisterebbe mai per un
            // utente reale).
            Wait.ForCondition(() => riaperto.HaSegnalazioniDaClassificare,
                "l'operazione Non Conforme genera una segnalazione da classificare nel pannello Anomalie del Test");
            riaperto.SetSeveritaSegnalazione(0, "Media");
            riaperto.CreaAnomaliaDaSegnalazione(0);
            var confermaSegnalazione = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            confermaSegnalazione.ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura del dialog di conferma");

            riaperto.Save();

            var listaDopoSecondoSalvataggio = new TestListPage(App.WaitForMainWindow(), App.WaitForMainWindow());
            var conDueRevisioni = listaDopoSecondoSalvataggio.OpenOnlyRow();
            // Idem: nessun GoToTab, "conDueRevisioni" e' ancora un test esistente riaperto dalla lista.

            Wait.ForCondition(() => conDueRevisioni.HaMenuRevisioni, "il menu REV compare con piu' di una revisione");
            Wait.ForCondition(() => conDueRevisioni.NumeroRevisioniInMenu() == 2, "il menu REV elenca esattamente 2 revisioni");

            conDueRevisioni.SelezionaRevisione(1);
            Wait.ForCondition(() => conDueRevisioni.NumeroCondizioni() == 1 &&
                                    conDueRevisioni.EsitoCondizioneTesto(0) == "Conforme",
                "Rev. 1 mostra la propria unica condizione dopo la navigazione dal menu REV");
            Assert.Equal("Conforme", conDueRevisioni.EsitoCondizioneTesto(0));
            Assert.False(conDueRevisioni.EsitoCondizioneAbilitato(0), "l'esito della revisione storica è in sola lettura");
            Assert.False(conDueRevisioni.CanSave, "una revisione storica non espone il salvataggio");

            conDueRevisioni.SelezionaRevisione(2);
            Wait.ForCondition(() => conDueRevisioni.NumeroCondizioni() == 1 &&
                                    conDueRevisioni.EsitoCondizioneTesto(0) == "Non Conforme",
                "Rev. 2 mostra la propria unica condizione dopo la navigazione dal menu REV");
            Assert.Equal("Non Conforme", conDueRevisioni.EsitoCondizioneTesto(0));
            Assert.True(conDueRevisioni.NumeroRevisioniInMenu() == 2,
                "la consultazione della revisione storica non ne crea una nuova");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(CambiareEsitoESalvareDiNuovo_CreaUnaSecondaRevisione_NavigabileDalMenuRev), ex);
        }
    }
}
