using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura P1 per la classificazione di una segnalazione "provvisoria" dentro l'editor Test
/// (TestCaseEditorViewModel.CaricaPannelloAnomalie/CreaAnomaliaDaTest): un'operazione con esito Non Conforme
/// genera automaticamente una riga da classificare nel pannello "Anomalie" del Test stesso (non nella pagina
/// Anomalie/CR, come ipotizzato nella prima versione della matrice — la card visibile li' e' disattivata di
/// proposito, vedi commento in NonConformitaView.axaml). Finora 0 AutomationId su questo pannello e nessun
/// test lo classificava.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class ClassificazioneSegnalazioneNelTestTests : UiTestBase
{
    public ClassificazioneSegnalazioneNelTestTests() : base(TestWorkspace.CreateLoggedIn(nameof(ClassificazioneSegnalazioneNelTestTests))) { }

    [Fact]
    public void EsitoNonConforme_GeneraSegnalazioneDaClassificare_CreaAnomaliaConfermata()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);

            var testList = shell.GoToTestList();
            var editor = testList.ClickNew();
            editor.ChooseTemplate();
            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            editor.SetDescrizioneBreve("Editor classificazione segnalazione - QA");
            editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);

            // ChooseTemplate() ha gia' creato la prima reiterazione/condizione (SceglieTemplate ->
            // AggiungiIterazione+AggiungiCondizione): nessun "Avvia prima esecuzione" da premere qui.
            editor.GoToTab(2);
            editor.SetEsitoCondizione(0, "Non Conforme");
            // Non Conforme rende obbligatoria la descrizione del problema (RichiedeDescrizioneProblema):
            // un utente reale la compila prima di salvare, altrimenti Salva() rifiuta il salvataggio.
            editor.SetDescrizioneProblema(0, "Problema osservato durante l'esecuzione - QA");

            // Il pannello "Anomalie" del Test si popola dal vivo (SincronizzaEsitoCondizioni ->
            // CaricaPannelloAnomalie), non solo dopo un Salva: non serve salvare/riaprire per classificare la
            // segnalazione appena generata. Anzi NON si potrebbe: ValidaPerSalvataggio rifiuta SEMPRE il
            // salvataggio mentre esiste una segnalazione non classificata/collegata/esclusa
            // (ValClassificaPrimaConclusione, TestCaseEditorViewModel.cs), indipendentemente dallo stato del
            // Test — un Salva "intermedio" con la segnalazione ancora da classificare non e' mai stato
            // possibile, quindi un utente reale classifica qui, PRIMA di salvare la prima volta.
            Wait.ForCondition(() => editor.HaSegnalazioniDaClassificare,
                "un'operazione Non Conforme genera una segnalazione da classificare nel pannello Anomalie del Test");
            Assert.Equal(1, editor.NumeroSegnalazioniDaClassificare());

            editor.SetSeveritaSegnalazione(0, "Media");
            editor.CreaAnomaliaDaSegnalazione(0);

            var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            Assert.False(string.IsNullOrWhiteSpace(conferma.Message));
            conferma.ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura del dialog di conferma");

            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();
            // Salva() torna sempre alla Lista Test dopo un salvataggio riuscito.

            // La nuova anomalia deve comparire nella pagina Anomalie/CR, collegata a questo Test.
            var anomalie = shell.GoToAnomalie();
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 300) == 1,
                "l'anomalia creata dalla classificazione compare in Anomalie/CR");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(EsitoNonConforme_GeneraSegnalazioneDaClassificare_CreaAnomaliaConfermata), ex);
        }
    }
}
