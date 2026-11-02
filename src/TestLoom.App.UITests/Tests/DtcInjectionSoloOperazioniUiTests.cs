using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
public sealed class DtcInjectionSoloOperazioniUiTests : UiTestBase
{
    public DtcInjectionSoloOperazioniUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(DtcInjectionSoloOperazioniUiTests), funzionalitaNelTemplate: true);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = "ValidationTool", Tipo = TipoStrumento.Supporto, ModificatoDa = "UITests" },
            new() { Nome = "Guasto Injection", Tipo = TipoStrumento.Comportamento, ModificatoDa = "UITests" },
            new() { Nome = "UnitLogger", Tipo = TipoStrumento.DaTestare, ModificatoDa = "UITests" },
        });
        var ecuLista = state.Master.Componente.Load();   // con un veicolo scelto il dialog propone solo i componenti associate ad esso
        foreach (var e in ecuLista.Where(e => e.Nome == TestWorkspace.DefaultEcuName)) e.VeicoliAssociati = new() { TestWorkspace.DefaultVehicleName };
        state.Master.Componente.Save(ecuLista);
        var templates = state.Master.TestTemplate.Load();
        var template = templates.Single(t => !t.Archiviata);
        template.StrumentiPreselezionati = new() { "ValidationTool", "Guasto Injection" };
        template.Sezioni[0].Campi.Add(new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.Veicolo });
        template.Sezioni[0].Campi.Add(new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.StrumentiUsati });
        template.Sezioni[0].Campi.Single(c => c.CampoSistemaChiave == CampiSistema.Funzionalita).Obbligatorio = true;
        state.Master.TestTemplate.Save(templates);
        return ws;
    }

    /// <summary>Valore mostrato dalla combo (testo del suo elemento selezionato nell'albero UIA; SelectedItem non e esposto a combo chiusa).</summary>
    private static string? TestoCombo(FlaUI.Core.AutomationElements.AutomationElement window, string id) =>
        ElementFinders.WaitComboBoxById(window, id).FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))?.Name;

    [Fact]
    public void Wizard_DtcInjectionEVenus_SoloCoppieDtc_SenzaFunzionalita_SalvaERiapre()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToTestList().ClickNew();
            editor.ChooseTemplate();
            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            editor.SetDescrizioneBreve("Guasto Injection e ValidationTool senza strumento da testare");
            Assert.False(ElementFinders.ExistsById(window, "Editor_NuovaFunzionalita_TextBox"));
            ElementFinders.WaitComboBoxById(window, "Editor_Veicolo_ComboBox").SelectByText(TestWorkspace.DefaultVehicleName);
            editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);
            editor.GoToTab(2);
            Wait.ForCondition(() => editor.NumeroCondizioni() == 0, "nessuna operazione libera con solo guasto Injection e ValidationTool");
            Assert.False(ElementFinders.ExistsById(window, "Editor_AggiungiCondizione_Button"));
            Assert.Equal(TestWorkspace.DefaultVehicleName, TestoCombo(window, "Editor_DtcInjection_Veicolo_Combo"));
            // Verifica il menu del veicolo nel blocco guasto, non solo il valore preselezionato del Test.
            ElementFinders.WaitComboBoxById(window, "Editor_DtcInjection_Veicolo_Combo")
                .SelectByText(TestWorkspace.DefaultVehicleName);
            Assert.Equal(TestWorkspace.DefaultEcuName, TestoCombo(window, "Editor_DtcInjection_Componente_Combo"));
            ElementFinders.WaitTextBoxById(window, "Editor_DtcInjection_Manuale_Text").SetText("P0100");
            ElementFinders.WaitButtonById(window, "Editor_DtcInjection_Aggiungi_Button").SafeInvoke();
            Wait.ForCondition(() => editor.NumeroCondizioni() == 2, "coppia Validazione e Devalidazione");
            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();
            Wait.ForCondition(() => !editor.CanSave, "test salvato");
            using var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
            var tc = Assert.Single(state.TestCases.GetAll());
            var condizioni = tc.Esecuzioni[^1].Condizioni;
            Assert.Equal(new[] { PassoDtc.Validazione, PassoDtc.Devalidazione }, condizioni.Select(c => c.DtcPasso!.Value));
            Assert.All(condizioni, c =>
            {
                Assert.Equal("Guasto Injection", c.Strumento);
                Assert.Equal("P0100", c.Guasto);
                Assert.Equal(TestWorkspace.DefaultVehicleName, c.DtcVeicolo);
                Assert.Equal(TestWorkspace.DefaultEcuName, Assert.Single(c.Componente));
                Assert.True(string.IsNullOrWhiteSpace(c.Funzionalita));
            });
            var riaperto = new TestListPage(window, window).OpenOnlyRow();
            // In sola lettura la barra delle tab non c'e e le operazioni sono gia visibili; in modifica servirebbe la tab Operazioni.
            if (ElementFinders.ExistsById(window, "Editor_Tab_2_Button")) riaperto.GoToTab(2);
            Wait.ForCondition(() => riaperto.NumeroCondizioni() == 2, "coppia presente alla riapertura");
            foreach (var c in condizioni)
                Assert.False(ElementFinders.ExistsById(window, $"Editor_Condizione_{c.Uid}_Funzionalita_Combo"));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Wizard_DtcInjectionEVenus_SoloCoppieDtc_SenzaFunzionalita_SalvaERiapre), ex); }
    }
}
