using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Permessi del Viewer sul modulo Test (lista ed editor), non esercitati dalla matrice Viewer generale: un Test
/// aperto e' in sola lettura (niente Modifica, Salva, Archivia, allegati), Esc non apre conferme, "Archivia
/// selezionati" e' disabilitato nella lista. Nessuna verifica produce scritture sul disco del workspace.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Permissions")]
public sealed class TestViewerCoverageUiTests : UiTestBase
{
    private const string IdTest = "TC-QA-VIEW-1";

    public TestViewerCoverageUiTests() : base(CreaWorkspaceViewerConTest()) { }

    /// <summary>Un Test seminato dal Master: il Viewer non puo' crearne, quindi il dato deve gia' esistere.</summary>
    private static TestWorkspace CreaWorkspaceViewerConTest()
    {
        var workspace = TestWorkspace.CreateLoggedInAs(nameof(TestViewerCoverageUiTests), RuoloOperatore.Viewer);
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        TestCoverageSeed.SeedTest(state, IdTest, "Test consultabile dal Viewer", TestCoverageSeed.UidTemplatePrincipale(state),
            TestWorkspace.DefaultEcuName, Esito.Conforme);
        return workspace;
    }

    private Window Finestra() => App.WaitForMainWindow();

    /// <summary>Seleziona la riga del Test per Id (SelectionItem della riga, in fallback il click sulla cella).</summary>
    private void SelezionaRiga(string id)
    {
        var cella = Wait.Until(
            () => Finestra().FindAllDescendants(cf => cf.ByAutomationId("TestList_Row_Id_Text")).FirstOrDefault(e => e.AsLabel().Text == id),
            e => e != null, null, $"riga Test '{id}'")!;
        var contenitore = cella.Parent;
        for (var i = 0; i < 10 && contenitore != null; i++, contenitore = contenitore.Parent)
        {
            if (contenitore.FindFirstDescendant(cf => cf.ByAutomationId("TestList_Open_Button")) != null) break;
        }
        var selezione = (contenitore ?? cella).Patterns.SelectionItem.PatternOrDefault;
        if (selezione != null) selezione.Select();
        else cella.Click();
    }

    [Fact]
    public void Viewer_ApreUnTest_InSolaLetturaSenzaModificaNeSalvataggio()
    {
        try
        {
            var lista = new MainShellPage(Finestra()).GoToTestList();
            Wait.ForCondition(() => lista.RowCount() == 1, "il Test seminato e' visibile al Viewer");
            var percorso = Assert.Single(TestCoverageSeed.FileTestCase(Workspace));
            var hashPrima = TestCoverageSeed.HashFile(percorso);

            var editor = lista.OpenOnlyRow();
            Assert.False(editor.EditModeAbilitato, "il Viewer non puo' entrare in modifica");
            Assert.False(editor.CanSave, "il Viewer non vede il pulsante Salva");
            Assert.False(editor.ArchiviaAbilitato, "il Viewer non puo' archiviare il Test");
            Assert.False(editor.ScegliFileAllegatoAbilitato, "il Viewer non puo' aggiungere allegati");

            // Esc in sola lettura non chiede conferme e non esce dalla scheda (nessuna modifica da abbandonare).
            ElementActions.PressKey(Finestra(), VirtualKeyShort.ESCAPE);
            Assert.True(App.IsWindowClosedByContent("Confirm_Yes_Button"), "Esc in sola lettura non deve chiedere conferme");
            Assert.Equal(hashPrima, TestCoverageSeed.HashFile(percorso));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Viewer_ApreUnTest_InSolaLetturaSenzaModificaNeSalvataggio), ex); }
    }

    [Fact]
    public void Viewer_ArchiviaSelezionati_E_Disabilitato_NonCambiaIlTestSulDisco()
    {
        try
        {
            var lista = new MainShellPage(Finestra()).GoToTestList();
            Wait.ForCondition(() => lista.RowCount() == 1, "il Test seminato e' visibile al Viewer");
            var percorso = Assert.Single(TestCoverageSeed.FileTestCase(Workspace));
            var hashPrima = TestCoverageSeed.HashFile(percorso);

            SelezionaRiga(IdTest);
            Wait.ForCondition(() => ElementFinders.ExistsById(Finestra(), "TestList_ArchiviaSelezionati_Button"),
                "barra di selezione con le azioni di massa");
            Assert.False(ElementFinders.WaitButtonById(Finestra(), "TestList_ArchiviaSelezionati_Button").IsEnabled,
                "'Archivia selezionati' e' disabilitato per il Viewer");

            Assert.False(TestCoverageSeed.EArchiviato(Workspace, IdTest), "il Viewer non ha archiviato il Test (nessun tombstone sul disco)");
            Assert.Equal(hashPrima, TestCoverageSeed.HashFile(percorso));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Viewer_ArchiviaSelezionati_E_Disabilitato_NonCambiaIlTestSulDisco), ex); }
    }
}
