using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
public sealed class TestCaseEditorBenchValidationDefaultsUiTests : UiTestBase
{
    private const string RequisitoD4d = "REQ-Extra-BENCH-DEFAULT";
    private const string AltroOperatore = "QA Other";

    public TestCaseEditorBenchValidationDefaultsUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(TestCaseEditorBenchValidationDefaultsUiTests));
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);

        state.Master.Strumenti.Save(new List<StrumentoDiagnostico>
        {
            new()
            {
                Nome = "ValidationTool", Tipo = TipoStrumento.Supporto, ModificatoDa = "UITests",
                Campi = new List<DefinizioneCampoStrumento>
                {
                    new() { Nome = "Profilo", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerTest },
                    new() { Nome = "Allegato", Tipo = TipoCampoStrumento.File, Ambito = TipoAmbitoCampo.PerTest },
                },
            },
            new() { Nome = "Guasto Injection", Tipo = TipoStrumento.Comportamento, ModificatoDa = "UITests" },
        });

        var componente = state.Master.Componente.Load();
        componente.Single(e => e.Nome == TestWorkspace.DefaultEcuName).VeicoliAssociati = new() { TestWorkspace.DefaultVehicleName };
        state.Master.Componente.Save(componente);

        var templates = state.Master.TestTemplate.Load();
        var template = templates.Single(t => t.Nome == TestWorkspace.DefaultTemplateName);
        template.StrumentiPreselezionati = new() { "ValidationTool", "Guasto Injection" };
        template.Sezioni[0].Campi.Add(new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.Veicolo });
        template.Sezioni[0].Campi.Add(new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.StrumentiUsati });
        if (!template.CampiRiferimenti.Any(c => c.CampoSistemaChiave == CampiSistema.RequisitiCollegati))
            template.CampiRiferimenti.Add(new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.RequisitiCollegati });
        state.Master.TestTemplate.Save(templates);

        var requisito = new Requisito { Id = RequisitoD4d, Testo = "Requisito Extra di collaudo", ApplicabileBanco = true, ModificatoDa = "UITests" };
        RequisitoApplicabilita.ImpostaTipo(requisito, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(requisito, new[] { TestWorkspace.DefaultVehicleName });
        state.Master.Requisiti.Save(new List<Requisito> { requisito });

        // Profilo sintetico separato: i default del test devono restare sull'operatore attivo.
        var operatori = state.Master.Operatori.Load();
        var altro = new Operatore
        {
            Nome = AltroOperatore,
            Iniziali = "QO",
            PinHash = PinService.Hash("4343"),
            RecoveryCodeHash = PinService.Hash(PinService.GeneraCodiceRecupero()),
            Ruolo = RuoloOperatore.Standard,
            RichiedeCambioPin = false,
            ModificatoDa = "UITests",
        };
        RuoloSigilloService.Sigilla(altro);
        operatori.Add(altro);
        state.Master.Operatori.Save(operatori);

        return ws;
    }

    private TestEditorPage NuovoTest(FlaUI.Core.AutomationElements.Window window, string descrizione)
    {
        var editor = new MainShellPage(window).GoToTestList().ClickNew();
        editor.ChooseTemplate();
        editor.GoToTab(CreateTestCaseFlow.TabDettagli);
        editor.SetDescrizioneBreve(descrizione);
        ElementFinders.WaitComboBoxById(window, "Editor_Veicolo_ComboBox").SelectByText(TestWorkspace.DefaultVehicleName);
        editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);
        editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
        editor.AggiungiRequisitoDaDialogo(App, RequisitoD4d);
        editor.GoToTab(2);
        return editor;
    }

    private TestEditorPage NuovoTestSenzaVeicolo(FlaUI.Core.AutomationElements.Window window, string descrizione)
    {
        var editor = new MainShellPage(window).GoToTestList().ClickNew();
        editor.ChooseTemplate();
        editor.GoToTab(CreateTestCaseFlow.TabDettagli);
        editor.SetDescrizioneBreve(descrizione);
        editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);
        editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
        editor.AggiungiRequisitoDaDialogo(App, RequisitoD4d);
        editor.GoToTab(2);
        return editor;
    }

    private void AnnullaNuovoTestEConfermaSeRichiesto(TestEditorPage editor, FlaUI.Core.AutomationElements.Window window)
    {
        editor.Cancel();
        Wait.ForCondition(() => ElementFinders.ExistsById(window, "TestList_Title_Text")
                || !App.IsWindowClosedByContent("Confirm_Yes_Button"),
            "annullamento del Test nuovo o conferma delle modifiche iniziali precompilate");
        if (!ElementFinders.ExistsById(window, "TestList_Title_Text"))
        {
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"),
                "chiusura della conferma di annullamento del Test nuovo");
        }
        Wait.ForCondition(() => ElementFinders.ExistsById(window, "TestList_Title_Text"),
            "la lista Test è visibile dopo l'annullamento");
    }

    private static string? TestoCombo(FlaUI.Core.AutomationElements.AutomationElement window, string id) =>
        ElementFinders.WaitComboBoxById(window, id).FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))?.Name;

    private static bool VisibileById(FlaUI.Core.AutomationElements.AutomationElement window, string id)
    {
        var elemento = ElementFinders.TryFindById(window, id);
        return elemento != null && elemento.BoundingRectangle.Width > 0 && elemento.BoundingRectangle.Height > 0;
    }

    [Fact]
    public void DefaultFilePresenteNelProfilo_NonMaterializzaFilenameENonEsponeAzioniDefault()
    {
        try
        {
            using (var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false))
            {
                state.SalvaValoriDefaultCampi(new Dictionary<string, string?>
                {
                    ["ValidationTool::Allegato"] = "report-precedente.pdf",
                    ["ValidationTool::Profilo"] = "Profilo-QA",
                });
            }

            var window = App.WaitForMainWindow();
            var editor = NuovoTest(window, "Default file non trasferibile");

            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            Assert.Equal("Profilo-QA", ElementFinders.WaitTextBoxById(window, "Editor_Campo_Profilo").Text);
            Assert.False(VisibileById(window, "Editor_Campo_ValidationTool_Allegato_Default_Button"));
            Assert.False(VisibileById(window, "Editor_Campo_ValidationTool_Allegato_RimuoviDefault_Button"));
            var nessunFile = LocalizationService.Instance.Traduci("NessunFileCollegato");
            Wait.ForCondition(() => window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
                    .Any(t => t.Name == nessunFile && t.BoundingRectangle.Height > 0),
                "il campo File resta vuoto nonostante il default personale storico");

            AnnullaNuovoTestEConfermaSeRichiesto(editor, window);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(DefaultFilePresenteNelProfilo_NonMaterializzaFilenameENonEsponeAzioniDefault), ex); }
    }
}
