using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.Tests;

/// <summary>Modifica rapida di una CR chiusa per un operatore Standard (non Master): la finestra si apre in sola lettura
/// con il testo esplicativo e senza azioni di stato o di salvataggio. Un Master, sulla stessa CR, vede invece Riapri.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Permissions")]
public sealed class ChangeRequestQuickEditStandardReadOnlyUiTests : UiTestBase
{
    private const string CrId = "CR-QA-STD-CHIUSA";
    private const string FinestraRapidaCr = "Anomalie_Cr_ModificaRapida_Responsabile_ComboBox";

    public ChangeRequestQuickEditStandardReadOnlyUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedInAs(nameof(ChangeRequestQuickEditStandardReadOnlyUiTests), RuoloOperatore.Standard);
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        var templates = state.Master.TestTemplate.Load();
        templates.Add(new TestTemplate { Nome = "CR Template Standard QA", IsChangeRequest = true, ModificatoDa = "UITests" });
        state.Master.TestTemplate.Save(templates);
        var templateUid = templates.Single(t => t.Nome == "CR Template Standard QA").Uid;

        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = CrId,
            Titolo = "CR chiusa per l'operatore Standard",
            Descrizione = "Richiesta chiusa dal Master",
            TemplateUid = templateUid,
            Responsabile = "QA Standard",
            Stato = StatoChangeRequest.Chiusa,
            ChiusaIl = DateTime.Now,
            ChiusaDa = "QA Master",
            VeicoliImpattati = new() { TestWorkspace.DefaultVehicleName },
            EcuImpattate = new() { TestWorkspace.DefaultEcuName },
        });
        return workspace;
    }

    [Fact]
    public void CrChiusa_ModificaRapidaPerNonMaster_MostraSolaLetturaSenzaAzioni()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToAnomalie();
            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            ElementFinders.WaitTextBoxById(window, "Anomalie_Cr_FiltroTesto_Input").SetText(CrId);

            var modificaRapida = ElementFinders.WaitByIdWithinRow(RigaCr(window), "Anomalie_Cr_ModificaRapida_Button").AsButton();
            Assert.True(modificaRapida.IsEnabled, "la modifica rapida della CR chiusa è presente per l'operatore Standard");
            modificaRapida.SafeInvoke();

            var rapida = App.WaitForWindowByContent(FinestraRapidaCr);
            Assert.False(ElementFinders.WaitComboBoxById(rapida, "Anomalie_Cr_ModificaRapida_Responsabile_ComboBox").IsEnabled,
                "la CR chiusa non si riassegna dalla modifica rapida");
            Assert.True(TestoVisibile(rapida, "Sola lettura"), "la finestra spiega che la CR è in sola lettura");
            foreach (var id in new[]
            {
                "Anomalie_Cr_ModificaRapida_Implementa_Button",
                "Anomalie_Cr_ModificaRapida_Chiudi_Button",
                "Anomalie_Cr_ModificaRapida_Riapri_Button",
                "Anomalie_Cr_ModificaRapida_Salva_Button",
            })
            {
                Assert.False(Visibile(rapida, id), $"nessuna azione di stato o di salvataggio per l'operatore Standard: {id}");
            }

            ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_Annulla_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent(FinestraRapidaCr), "Annulla chiude la finestra in sola lettura");
            Assert.Equal(StatoChangeRequest.Chiusa, LeggiCr().Stato);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CrChiusa_ModificaRapidaPerNonMaster_MostraSolaLetturaSenzaAzioni), ex); }
    }

    private static AutomationElement RigaCr(Window window) =>
        Wait.ForElement(() => ElementFinders.WaitById(window, "Anomalie_Cr_Lista_ListBox")
                .FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
                .FirstOrDefault(r => r.FindFirstDescendant(cf => cf.ByName(CrId)) != null),
            $"riga CR {CrId}");

    private static bool Visibile(AutomationElement scope, string automationId)
    {
        var el = ElementFinders.TryFindById(scope, automationId);
        return el != null && el.BoundingRectangle.Width > 0 && el.BoundingRectangle.Height > 0;
    }

    private static bool TestoVisibile(AutomationElement scope, string frammento) =>
        scope.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Any(t => (t.Name ?? "").Contains(frammento, StringComparison.Ordinal) && t.BoundingRectangle.Width > 0 && t.BoundingRectangle.Height > 0);

    private ChangeRequest LeggiCr()
    {
        using var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
        return state.ChangeRequests.GetById(CrId)
            ?? throw new InvalidOperationException($"CR {CrId} non trovata nel workspace di test.");
    }
}
