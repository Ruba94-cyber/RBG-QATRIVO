using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using Keyboard = FlaUI.Core.Input.Keyboard;
using FlaUI.Core.WindowsAPI;

namespace TestLoom.App.UITests.Tests;

/// <summary>Regressione FlaUI per le azioni di massa della Lista Test: stato e assegnatario vengono applicati a
/// entrambe le righe selezionate e verificati rileggendo i record persistiti nel workspace isolato.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
[Trait("Area", "TestList")]
public sealed class TestCaseBulkActionsUiTests : UiTestBase
{
    private const string PrimoTest = "TC-QA-BULK-01";
    private const string SecondoTest = "TC-QA-BULK-02";
    private const string NotaAssegnazione = "Assegnazione bulk verificata da FlaUI";

    public TestCaseBulkActionsUiTests() : base(CreaWorkspaceConDueTest()) { }

    private static TestWorkspace CreaWorkspaceConDueTest()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(TestCaseBulkActionsUiTests));
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        var template = TestCoverageSeed.UidTemplatePrincipale(state);
        TestCoverageSeed.SeedTest(state, PrimoTest, "Primo test per le azioni di massa", template,
            TestWorkspace.DefaultEcuName, Esito.DaEseguire, StatoLavoroTest.Pianificato);
        TestCoverageSeed.SeedTest(state, SecondoTest, "Secondo test per le azioni di massa", template,
            TestWorkspace.DefaultEcuName, Esito.DaEseguire, StatoLavoroTest.Pianificato);
        return workspace;
    }

    private Window Finestra() => App.WaitForMainWindow();

    private void SelezionaRiga(string id, bool aggiungi = false)
    {
        var cella = Wait.Until(
            () => Finestra().FindAllDescendants(cf => cf.ByAutomationId("TestList_Row_Id_Text"))
                .FirstOrDefault(e => e.AsLabel().Text == id),
            e => e != null, null, $"riga Test '{id}'");

        AutomationElement? contenitore = cella?.Parent;
        for (var i = 0; i < 10 && contenitore != null; i++, contenitore = contenitore.Parent)
        {
            if (contenitore.FindFirstDescendant(cf => cf.ByAutomationId("TestList_Open_Button")) != null) break;
        }

        var selezione = (contenitore ?? cella!).Patterns.SelectionItem.PatternOrDefault;
        if (selezione != null)
        {
            if (aggiungi) selezione.AddToSelection();
            else selezione.Select();
        }
        else if (aggiungi)
        {
            using (FlaUI.Core.Input.Keyboard.Pressing(VirtualKeyShort.CONTROL)) cella!.Click();
        }
        else cella!.Click();
    }

    private void SelezionaDueTest()
    {
        SelezionaRiga(PrimoTest);
        SelezionaRiga(SecondoTest, aggiungi: true);
        Wait.ForCondition(() =>
        {
            var summary = ElementFinders.TryFindById(Finestra(), "TestList_SelectionSummary_Text");
            return summary?.Name?.Contains('2') == true;
        }, "riepilogo con due Test selezionati");
    }

    private void ChiudiRiepilogoAzioneMassa()
    {
        var dialogo = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
        dialogo.ConfirmYes();
        Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"),
            "chiusura del riepilogo dell'azione di massa");
    }

    private TestCase LeggiTestPersistito(string id) =>
        TestCoverageSeed.TrovaTestCase(Workspace, id)
        ?? throw new InvalidOperationException($"Il Test '{id}' non e' stato trovato nel workspace persistito.");

    [Fact]
    public void DueTestSelezionati_CambioStatoEAssegnazioneDiMassaSonoPersistiti()
    {
        try
        {
            var lista = new MainShellPage(Finestra()).GoToTestList();
            Wait.ForCondition(() => lista.RowCount() == 2, "i due Test di prova sono visibili nella lista");

            SelezionaDueTest();
            ElementFinders.WaitButtonById(Finestra(), "TestList_CambiaStatoSelezionati_Button").SafeInvoke();
            var voceStato = Wait.ForElement(() => App.TopLevelWindows()
                .Select(w => w.FindFirstDescendant(cf => cf.ByAutomationId("TestList_CambiaStatoSelezionati_InCorso_MenuItem")))
                .FirstOrDefault(e => e != null), "voce InCorso nel menu di stato");
            voceStato.Focus();
            Keyboard.Type(VirtualKeyShort.ENTER);
            ChiudiRiepilogoAzioneMassa();

            Assert.Equal(StatoLavoroTest.InCorso, LeggiTestPersistito(PrimoTest).StatoLavoro);
            Assert.Equal(StatoLavoroTest.InCorso, LeggiTestPersistito(SecondoTest).StatoLavoro);

            SelezionaDueTest();
            ElementFinders.WaitButtonById(Finestra(), "TestList_AssegnaSelezionati_Button").SafeInvoke();

            var dialogoAssegnatario = new SelectDialogPage(App.WaitForWindowByContent("SelectDialog_Items_ListBox"));
            dialogoAssegnatario.SelectItem(TestWorkspace.DefaultOperatorName);
            dialogoAssegnatario.Confirm();
            Wait.ForCondition(() => App.IsWindowClosedByContent("SelectDialog_Items_ListBox"),
                "chiusura del selettore dell'assegnatario");

            var dialogoNota = App.WaitForWindowByContent("TextEdit_Content_TextBox");
            ElementFinders.WaitTextBoxById(dialogoNota, "TextEdit_Content_TextBox").SetText(NotaAssegnazione);
            ElementFinders.WaitButtonById(dialogoNota, "TextEdit_Save_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("TextEdit_Content_TextBox"),
                "chiusura del dialogo della nota di pianificazione");
            ChiudiRiepilogoAzioneMassa();

            foreach (var id in new[] { PrimoTest, SecondoTest })
            {
                var salvato = LeggiTestPersistito(id);
                Assert.Equal(StatoLavoroTest.InCorso, salvato.StatoLavoro);
                Assert.Equal(TestWorkspace.DefaultOperatorName, salvato.Assegnatario);
                Assert.Contains(salvato.NotePianificazione, nota => nota.Nota == NotaAssegnazione);
            }
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(DueTestSelezionati_CambioStatoEAssegnazioneDiMassaSonoPersistiti), ex);
        }
    }
}
