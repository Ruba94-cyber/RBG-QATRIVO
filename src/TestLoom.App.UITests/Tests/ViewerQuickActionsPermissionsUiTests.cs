using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.Tests;

/// <summary>Le quick action di modifica e cambio stato di NC e CR restano inaccessibili a un Viewer.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Permissions")]
[Trait("Category", "Regression")]
public sealed class ViewerQuickActionsPermissionsUiTests : UiTestBase
{
    private const string NcId = "NC-QA-VIEW-ACTIONS";
    private const string CrId = "CR-QA-VIEW-ACTIONS";
    private const string TemplateCrNome = "CR Template Viewer Actions QA";

    public ViewerQuickActionsPermissionsUiTests() : base(CreaWorkspaceViewerConNcECr()) { }

    private static TestWorkspace CreaWorkspaceViewerConNcECr()
    {
        var workspace = TestWorkspace.CreateLoggedInAs(
            nameof(ViewerQuickActionsPermissionsUiTests), RuoloOperatore.Viewer, seedTestFixtures: false);
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        TestCoverageSeed.SeedNonConformitaAperta(state, NcId, "NC per verificare le quick action del Viewer");

        var templateCr = new TestTemplate
        {
            Nome = TemplateCrNome,
            IsChangeRequest = true,
            ModificatoDa = "UITests",
        };
        var templates = state.Master.TestTemplate.Load();
        templates.Add(templateCr);
        state.Master.TestTemplate.Save(templates);
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = CrId,
            Titolo = "CR per verificare le quick action del Viewer",
            Descrizione = "Fixture sintetica per i permessi di sola lettura.",
            TemplateUid = templateCr.Uid,
            Stato = StatoChangeRequest.Aperta,
            Responsabile = "QA Viewer",
        });
        return workspace;
    }

    [Fact]
    public void Viewer_NonPuoInvocareModificaOCambiaStatoDalleRigheNcECr()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            anomalie.Cerca(NcId);
            Wait.ForCondition(() => anomalie.TotaleFiltrato() == 1,
                "la NC sintetica compare filtrando il suo Id");

            var rigaNc = RigaNc(window, NcId);
            var apriNc = ElementFinders.WaitByIdWithinRow(rigaNc, "Anomalie_Riga_Dettaglio_Button").AsButton();
            Assert.True(apriNc.IsEnabled, "il Viewer può aprire il dettaglio della NC");
            var modificaNc = ElementFinders.WaitByIdWithinRow(rigaNc, "Anomalie_Riga_ModificaRapida_Button").AsButton();
            Assert.True(modificaNc.IsEnabled, "la modifica rapida della NC è sempre invocabile: per il Viewer si apre in sola lettura");
            modificaNc.SafeInvoke();
            var rapidaNc = App.WaitForWindowByContent("Anomalie_NcModificaRapida_Salva_Button");
            Assert.True(TestoVisibile(rapidaNc, "Sola lettura"), "la modifica rapida NC spiega che il Viewer è in sola lettura");
            Assert.False(ElementFinders.WaitComboBoxById(rapidaNc, "Anomalie_NcModificaRapida_Stato_Combo").IsEnabled,
                "lo stato della NC non è modificabile dalla modifica rapida del Viewer");
            Assert.False(ElementFinders.WaitButtonById(rapidaNc, "Anomalie_NcModificaRapida_Salva_Button").IsEnabled,
                "la modifica rapida NC del Viewer non ha Salva attivo");
            Assert.False(VisibileById(rapidaNc, "Anomalie_NcStato_Chiudi_Button"), "il Viewer non vede Chiudi nella modifica rapida NC");
            Assert.False(VisibileById(rapidaNc, "Anomalie_NcStato_Riapri_Button"), "il Viewer non vede Riapri nella modifica rapida NC");
            ElementFinders.WaitButtonById(rapidaNc, "Anomalie_NcModificaRapida_Annulla_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Anomalie_NcModificaRapida_Salva_Button"),
                "Annulla chiude la modifica rapida NC in sola lettura");

            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Cr_FiltroTesto_Input"),
                "la scheda Change Request è visibile");
            ElementFinders.WaitTextBoxById(window, "Anomalie_Cr_FiltroTesto_Input").SetText(CrId);
            Wait.ForCondition(() => IdCr(window).SequenceEqual(new[] { CrId }),
                "il filtro lascia la sola CR sintetica");

            var rigaCr = RigaCr(window, CrId);
            var modificaCr = ElementFinders.WaitByIdWithinRow(rigaCr, "Anomalie_Cr_ModificaRapida_Button").AsButton();
            Assert.True(modificaCr.BoundingRectangle.Width > 0 && modificaCr.BoundingRectangle.Height > 0,
                "la quick action Modifica rapida CR è presente nella riga");
            Assert.True(modificaCr.IsEnabled, "la modifica rapida CR è sempre invocabile: per il Viewer si apre in sola lettura");
            modificaCr.SafeInvoke();
            var rapidaCr = App.WaitForWindowByContent("Anomalie_Cr_ModificaRapida_Responsabile_ComboBox");
            Assert.False(ElementFinders.WaitComboBoxById(rapidaCr, "Anomalie_Cr_ModificaRapida_Responsabile_ComboBox").IsEnabled,
                "la CR non si riassegna dalla modifica rapida del Viewer");
            Assert.True(TestoVisibile(rapidaCr, "Sola lettura"), "la modifica rapida CR spiega che il Viewer è in sola lettura");
            foreach (var id in new[]
            {
                "Anomalie_Cr_ModificaRapida_Implementa_Button",
                "Anomalie_Cr_ModificaRapida_Chiudi_Button",
                "Anomalie_Cr_ModificaRapida_Riapri_Button",
                "Anomalie_Cr_ModificaRapida_Salva_Button",
            })
            {
                Assert.False(VisibileById(rapidaCr, id), $"nessuna azione di scrittura per il Viewer: {id}");
            }
            ElementFinders.WaitButtonById(rapidaCr, "Anomalie_Cr_ModificaRapida_Annulla_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Anomalie_Cr_ModificaRapida_Responsabile_ComboBox"),
                "Annulla chiude la modifica rapida CR in sola lettura");

            var apriCr = ElementFinders.WaitByIdWithinRow(rigaCr, "Anomalie_Cr_Apri_Button").AsButton();
            Assert.True(apriCr.IsEnabled, "il Viewer può ancora aprire la CR");
            apriCr.SafeInvoke();
            // Apri mostra l'editor della CR in sola lettura: il Viewer non vede Modifica e i dati non sono modificabili.
            var editorCr = new ChangeRequestEditorPage(window);
            Wait.ForCondition(() => editorCr.InSolaLettura, "il Viewer apre l'editor della CR in sola lettura");
            Assert.False(editorCr.ModificaVisibile, "il Viewer non vede Modifica nell'editor della CR");
            Assert.False(ElementFinders.WaitTextBoxById(window, "Cr_Titolo_TextBox").IsEnabled,
                "i dati della CR restano non modificabili per il Viewer");
            Assert.Equal("CR per verificare le quick action del Viewer",
                ElementFinders.WaitTextBoxById(window, "Cr_Titolo_TextBox").Text);
            editorCr.TornaAllaLista();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "Torna alla lista riporta alla pagina Anomalie");

            var paths = new DataPaths(Workspace.WorkspaceDir);
            var nc = JsonIo.TryRead<NonConformita>(paths.NonConformitaFile(NcId), out var erroreNc)
                ?? throw new InvalidOperationException($"NC {NcId} non leggibile: {erroreNc}");
            var cr = JsonIo.TryRead<ChangeRequest>(paths.ChangeRequestFile(CrId), out var erroreCr)
                ?? throw new InvalidOperationException($"CR {CrId} non leggibile: {erroreCr}");
            Assert.Equal(StatoAnomalia.InVerifica, nc.StatoWorkflow);
            Assert.Equal(StatoChangeRequest.Aperta, cr.Stato);
            Assert.Equal("CR per verificare le quick action del Viewer", cr.Titolo);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Viewer_NonPuoInvocareModificaOCambiaStatoDalleRigheNcECr), ex);
        }
    }

    private static AutomationElement RigaNc(Window window, string id)
    {
        var griglia = ElementFinders.WaitById(window, "Anomalie_Righe_DataGrid");
        return Wait.ForElement(() => griglia.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
                .FirstOrDefault(r => r.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Any(t => t.Name == id)),
            $"riga NC {id}");
    }

    private static AutomationElement RigaCr(Window window, string id)
    {
        var lista = ElementFinders.WaitById(window, "Anomalie_Cr_Lista_ListBox");
        return Wait.ForElement(() => lista.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
                .FirstOrDefault(r => r.FindFirstDescendant(cf => cf.ByName(id)) != null),
            $"riga CR {id}");
    }

    private static IReadOnlyList<string> IdCr(Window window) =>
        ElementFinders.WaitById(window, "Anomalie_Cr_Lista_ListBox")
            .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Where(t => (t.Name ?? "").StartsWith("CR-", StringComparison.Ordinal) && t.BoundingRectangle.Height > 0)
            .OrderBy(t => t.BoundingRectangle.Top).Select(t => t.Name!).ToList();

    private static bool VisibileById(FlaUI.Core.AutomationElements.AutomationElement scope, string automationId)
    {
        var el = ElementFinders.TryFindById(scope, automationId);
        return el != null && el.BoundingRectangle.Width > 0 && el.BoundingRectangle.Height > 0;
    }

    private static bool TestoVisibile(FlaUI.Core.AutomationElements.AutomationElement scope, string frammento) =>
        scope.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
            .Any(t => (t.Name ?? "").Contains(frammento, StringComparison.Ordinal) && t.BoundingRectangle.Width > 0 && t.BoundingRectangle.Height > 0);
}
