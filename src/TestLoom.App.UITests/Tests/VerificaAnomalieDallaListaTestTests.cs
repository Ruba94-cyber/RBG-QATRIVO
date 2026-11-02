using System.Linq;
using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>"Verifica anomalie" nella Lista Test: selezionando un test con anomalie collegate compare il pulsante
/// accanto al cambio stato; con piu' anomalie apre un elenco compatto virtualizzato con "Modifica stato" per riga,
/// Caso d'uso: segnalazioni dalla rete testate, si chiude l'anomalia
/// senza entrare nell'editor.</summary>
internal static class ListaTestHelper
{
    public const string BottoneId = "TestList_VerificaAnomalie_Button";

    public static void SelezionaRiga(Window window, string id)
    {
        var cella = Wait.Until(
            () => window.FindAllDescendants(cf => cf.ByAutomationId("TestList_Row_Id_Text")).FirstOrDefault(e => e.AsLabel().Text == id),
            e => e != null, null, $"riga Test '{id}'")!;
        cella.Click();
    }
}

[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class VerificaAnomalieDallaListaTestTests : UiTestBase
{
    public VerificaAnomalieDallaListaTestTests() : base(AnomalieCollegateNelTestTests.CreaWorkspace(2)) { }

    private Window Finestra() => App.WaitForMainWindow();

    private void ConfermaEAttendi()
    {
        new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
        Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura del dialog di conferma");
    }

    [Fact]
    public void SelezionandoUnTestConAnomalie_CompareVerificaAnomalie_ElencoCompatto_ChiudeUnaAnomaliaSenzaEditor()
    {
        try
        {
            var window = Finestra();
            new MainShellPage(window).GoToTestList();
            Assert.False(ElementFinders.ExistsById(window, ListaTestHelper.BottoneId), "senza selezione il pulsante non c'e'");

            ListaTestHelper.SelezionaRiga(window, "TC-QA-ANOM");
            Wait.ForCondition(() => ElementFinders.ExistsById(window, ListaTestHelper.BottoneId), "il pulsante compare con un test con anomalie selezionato");
            ElementFinders.WaitButtonById(window, ListaTestHelper.BottoneId).SafeInvoke();

            var finestraElenco = App.WaitForWindowByContent("ListaTest_Anomalie_List");
            var elenco = TestAnomaliePanel.DaFinestraListaTest(finestraElenco);
            elenco.AttendiPrimaRiga("NC-QA-0002", "l'elenco compatto mostra le anomalie del test (piu' recente per prima)");
            Wait.ForCondition(() => elenco.NumeroRigheRealizzate == 2, "entrambe le anomalie nell'elenco");
            Assert.Equal(1, elenco.NumeroContenitoriElenco());

            // Chiusura dell'anomalia piu' recente dal pop-up, con motivazione obbligatoria (stessi controlli dell'editor).
            var dettaglio = elenco.ApriDettaglioDi(App, "NC-QA-0002");
            dettaglio.Chiudi();
            Wait.ForCondition(() => TestAnomaliePanel.ErroreDialogo(dettaglio).Length > 0, "senza motivazione la chiusura e' rifiutata");
            dettaglio.ImpostaMotivoChiusura("Segnalazione dalla rete verificata, nessun altro test necessario");
            dettaglio.Chiudi();
            ConfermaEAttendi();
            Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId), "il pop-up si chiude dopo la chiusura");

            // L'elenco si aggiorna: la riga chiusa mostra lo stato chiuso (non scompare), l'altra resta invariata.
            Wait.ForCondition(() => elenco.TrySnapshot("NC-QA-0002")?.Stato != elenco.TrySnapshot("NC-QA-0001")?.Stato
                                    && elenco.TrySnapshot("NC-QA-0002") != null, "stato aggiornato nell'elenco dopo la chiusura");
            Assert.Equal("Mario", elenco.TrySnapshot("NC-QA-0001")!.Owner);
            ElementFinders.WaitButtonById(finestraElenco, "ListaTest_Anomalie_Chiudi_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("ListaTest_Anomalie_List"), "chiusura dell'elenco");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(SelezionandoUnTestConAnomalie_CompareVerificaAnomalie_ElencoCompatto_ChiudeUnaAnomaliaSenzaEditor), ex);
        }
    }
}

[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class VerificaAnomalieDallaListaTestUnaSolaTests : UiTestBase
{
    public VerificaAnomalieDallaListaTestUnaSolaTests() : base(AnomalieCollegateNelTestTests.CreaWorkspace(1)) { }

    [Fact]
    public void ConUnaSolaAnomalia_ElencoEPopup_ModificaAssegnatarioESalva()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToTestList();
            ListaTestHelper.SelezionaRiga(window, "TC-QA-ANOM");
            Wait.ForCondition(() => ElementFinders.ExistsById(window, ListaTestHelper.BottoneId), "il pulsante compare");
            ElementFinders.WaitButtonById(window, ListaTestHelper.BottoneId).SafeInvoke();

            var finestraElenco = App.WaitForWindowByContent("ListaTest_Anomalie_List");
            var elenco = TestAnomaliePanel.DaFinestraListaTest(finestraElenco);
            elenco.AttendiPrimaRiga("NC-QA-0001", "l'elenco mostra la singola anomalia");
            var dettaglio = elenco.ApriDettaglioDi(App, "NC-QA-0001");
            Assert.Equal("Mario", dettaglio.Owner);
            dettaglio.ImpostaOwner("Giulia");
            dettaglio.Salva();
            Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId), "il pop-up si chiude dopo Salva");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ConUnaSolaAnomalia_ElencoEPopup_ModificaAssegnatarioESalva), ex);
        }
    }
}

[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class VerificaAnomalieDallaListaTestSenzaAnomalieTests : UiTestBase
{
    public VerificaAnomalieDallaListaTestSenzaAnomalieTests() : base(AnomalieCollegateNelTestTests.CreaWorkspace(0)) { }

    [Fact]
    public void TestSenzaAnomalie_IlPulsanteNonCompare()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToTestList();
            ListaTestHelper.SelezionaRiga(window, "TC-QA-ANOM");
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "TestList_Open_Button"), "riga selezionata");
            System.Threading.Thread.Sleep(500);
            Assert.False(ElementFinders.ExistsById(window, ListaTestHelper.BottoneId));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(TestSenzaAnomalie_IlPulsanteNonCompare), ex);
        }
    }
}
