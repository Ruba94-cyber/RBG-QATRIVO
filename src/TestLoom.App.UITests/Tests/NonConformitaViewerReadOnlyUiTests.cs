using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Tests;

/// <summary>Il Viewer può aprire il dettaglio di una NC dalla riga, ma non modificarla né salvarla.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Permissions")]
public sealed class NonConformitaViewerReadOnlyUiTests : UiTestBase
{
    private const string NcId = "NC-QA-VIEW-1";

    public NonConformitaViewerReadOnlyUiTests() : base(CreaWorkspaceViewerConNc()) { }

    private static TestWorkspace CreaWorkspaceViewerConNc()
    {
        var workspace = TestWorkspace.CreateLoggedInAs(
            nameof(NonConformitaViewerReadOnlyUiTests), RuoloOperatore.Viewer, seedTestFixtures: false);
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        TestCoverageSeed.SeedNonConformitaAperta(state, NcId, "NC consultabile dal Viewer");
        return workspace;
    }

    [Fact]
    public void Viewer_ApreUnaNcDallaRiga_InSolaLettura()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            anomalie.Cerca(NcId);
            Wait.ForCondition(() => anomalie.TotaleFiltrato() == 1,
                "la NC seminata compare cercandola per Id");

            var dettaglioButton = ElementFinders.WaitButtonById(window, "Anomalie_Riga_Dettaglio_Button");
            var bounds = dettaglioButton.BoundingRectangle;
            Assert.True(bounds.Width > 0 && bounds.Height > 0,
                "il pulsante Dettaglio della riga NC è visibile");
            Assert.True(dettaglioButton.IsEnabled,
                "il pulsante Dettaglio della riga NC è attivo per il Viewer");
            var modificaRapida = ElementFinders.WaitButtonById(window, "Anomalie_Riga_ModificaRapida_Button");
            Assert.True(modificaRapida.IsEnabled,
                "la modifica rapida è invocabile anche dal Viewer: si apre in sola lettura");
            modificaRapida.SafeInvoke();
            var rapida = App.WaitForWindowByContent("Anomalie_NcModificaRapida_Salva_Button");
            Assert.True(TestoVisibile(rapida, "Sola lettura"), "la modifica rapida spiega che il Viewer è in sola lettura");
            Assert.False(ElementFinders.WaitButtonById(rapida, "Anomalie_NcModificaRapida_Salva_Button").IsEnabled,
                "la modifica rapida del Viewer non ha Salva attivo");
            Assert.False(ElementFinders.WaitComboBoxById(rapida, "Anomalie_NcModificaRapida_Stato_Combo").IsEnabled,
                "lo stato della NC non è modificabile dalla modifica rapida del Viewer");
            Assert.False(VisibileById(rapida, "Anomalie_NcStato_Chiudi_Button"), "il Viewer non vede Chiudi nella modifica rapida");
            Assert.False(VisibileById(rapida, "Anomalie_NcStato_Riapri_Button"), "il Viewer non vede Riapri nella modifica rapida");
            ElementFinders.WaitButtonById(rapida, "Anomalie_NcModificaRapida_Annulla_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Anomalie_NcModificaRapida_Salva_Button"),
                "Annulla chiude la modifica rapida in sola lettura");

            // Dopo la chiusura della modifica rapida la riga viene ridisegnata: il pulsante va ritrovato.
            ElementFinders.WaitButtonById(window, "Anomalie_Riga_Dettaglio_Button").SafeInvoke();
            var dettaglio = new AnomaliaDettaglioPage(
                App.WaitForWindowByContent(AnomaliaDettaglioPage.SalvaId));
            Assert.False(ElementFinders.WaitButtonById(dettaglio.Window, AnomaliaDettaglioPage.SalvaId).IsEnabled,
                "Salva è disabilitato nel dettaglio NC del Viewer");

            dettaglio.Annulla();
            Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId),
                "la finestra NC si chiude con Annulla");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Viewer_ApreUnaNcDallaRiga_InSolaLettura), ex);
        }
    }

    private static bool VisibileById(FlaUI.Core.AutomationElements.AutomationElement scope, string automationId)
    {
        var el = ElementFinders.TryFindById(scope, automationId);
        return el != null && el.BoundingRectangle.Width > 0 && el.BoundingRectangle.Height > 0;
    }

    private static bool TestoVisibile(FlaUI.Core.AutomationElements.AutomationElement scope, string frammento) =>
        scope.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
            .Any(t => (t.Name ?? "").Contains(frammento, StringComparison.Ordinal) && t.BoundingRectangle.Width > 0 && t.BoundingRectangle.Height > 0);
}
