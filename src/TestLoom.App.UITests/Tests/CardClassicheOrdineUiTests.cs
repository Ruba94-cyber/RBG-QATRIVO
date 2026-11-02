using FlaUI.Core.AutomationElements;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura FlaUI dedicata alla generalizzazione del riordino a blocchi (review indipendente PASS,
/// coordinamento 2026-09-24): Strumenti usati era una card fissa separata, ora è un blocco riordinabile come
/// Funzionalità/Descrizioni dentro Dettagli. Stesso schema di TemplateClassiciOrdineUiTests (riordina nel Test
/// Template, verifica la posizione reale nel modulo Test), qui sul gruppo Dettagli invece di Pianificazione.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
public sealed class CardClassicheOrdineUiTests : UiTestBase
{
    public CardClassicheOrdineUiTests() : base(CreaWorkspaceConFunzionalitaEStrumentiUsati()) { }

    private static TestWorkspace CreaWorkspaceConFunzionalitaEStrumentiUsati()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(CardClassicheOrdineUiTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        var templates = state.Master.TestTemplate.Load();
        var template = templates.First(t => t.Nome == TestWorkspace.DefaultTemplateName);
        var sezione = template.Sezioni.First();
        // Aggiunti a quelli già seminati da TestWorkspace (DescrizioneBreve/Componente, necessari al wizard): ordine
        // ascendente, nessuna anomalia di compatibilità da preservare (Strumenti usati non era mai stato in
        // questo gruppo prima d'ora — vedi TestCaseEditorViewModel, commento su ChiaviBlocchiDettagli).
        sezione.Campi.Add(new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.Funzionalita, Nome = "Funzionalità", Ordine = 10 });
        sezione.Campi.Add(new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.StrumentiUsati, Nome = "Strumenti usati", Ordine = 11 });
        state.Master.TestTemplate.Save(templates);
        return workspace;
    }

    [Fact]
    public void RiordinaStrumentiUsatiPrimaDiFunzionalitaNelTemplate_SiRifletteNelModuloTestReale()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.NavigateTo("impostazioni");
            ElementFinders.WaitById(window, "Impostazioni_Sub_tipiditest").AsListBoxItem().Select();
            ElementFinders.WaitButtonById(window, "Tpl_Modifica_Button").SafeInvoke();
            ElementFinders.WaitButtonById(window, "TplGestisci_Button").SafeInvoke();

            var tplEditor = App.WaitForWindowByContent("TplGestisciCampi_Dettagli");
            ElementFinders.WaitButtonById(tplEditor, "TplGestisciCampi_Dettagli").SafeInvoke();

            var campiEditor = App.WaitForWindowByContent("TplCampoSu_StrumentiUsati");
            // Ordine di partenza: ..., Funzionalità, Strumenti usati (Ordine 10/11). Un solo passo basta a
            // scambiarli, essendo adiacenti nella vista (nessun altro campo del catalogo fra i due).
            ElementFinders.WaitButtonById(campiEditor, "TplCampoSu_StrumentiUsati").SafeInvoke();
            ElementFinders.WaitButtonById(campiEditor, "CampiEditor_Chiudi_Button").SafeInvoke();
            ElementFinders.WaitButtonById(tplEditor, "TplEditor_Chiudi_Button").SafeInvoke();

            ElementFinders.WaitButtonById(window, "Tpl_Salva_Button").SafeInvoke();

            var testList = shell.GoToTestList();
            var editor = testList.ClickNew();
            editor.ChooseTemplate();
            editor.GoToTab(CreateTestCaseFlow.TabDettagli);

            // Etichetta reale (EtichettaFunzionalita) e' "Funzionalità (opzionale)", non solo "Funzionalità" —
            // verificato in LocalizationService ("FunzionalitaCollegateLabel"), stesso principio già visto per
            // Stato lavoro/Scadenza in TemplateClassiciOrdineUiTests.
            var funzionalita = TrovaPerTesto(window, "Funzionalità (opzionale)");
            var strumentiUsati = TrovaPerTesto(window, "Strumenti usati");
            Assert.True(strumentiUsati.BoundingRectangle.Y < funzionalita.BoundingRectangle.Y,
                "Dopo il riordino nel template, 'Strumenti usati' deve comparire sopra 'Funzionalità' nel modulo Test reale.");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(RiordinaStrumentiUsatiPrimaDiFunzionalitaNelTemplate_SiRifletteNelModuloTestReale), ex);
        }
    }

    private static AutomationElement TrovaPerTesto(AutomationElement scope, string testo) =>
        scope.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
            .FirstOrDefault(e => e.Name == testo)
        ?? throw new InvalidOperationException($"Testo '{testo}' non trovato nel modulo Test.");
}
