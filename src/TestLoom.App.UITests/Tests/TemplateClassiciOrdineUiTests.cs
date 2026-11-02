using FlaUI.Core.AutomationElements;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura FlaUI dedicata al riordino di un campo classico nell'editor del Test Template e al suo
/// riflesso nel modulo Test reale (spec riordino campi 2026-09-23 §2, seguito coordinamento 2026-09-24):
/// riordina Stato lavoro/Scadenza in Impostazioni → Tipi di test → Gestisci campi (Pianificazione), poi apre
/// un nuovo Test con quel template e verifica che il campo spostato compaia PRIMA dell'altro sullo schermo
/// reale (posizione Y del contenitore), non solo che il valore del ViewModel sia cambiato — quello è già
/// coperto da TestCaseEditorBlocchiClassiciOrdineTests/CampiClassiciOrdineServiceTests (unit).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
public sealed class TemplateClassiciOrdineUiTests : UiTestBase
{
    public TemplateClassiciOrdineUiTests() : base(CreaWorkspaceConCampiPianificazione()) { }

    private static TestWorkspace CreaWorkspaceConCampiPianificazione()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(TemplateClassiciOrdineUiTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        // Il template minimo seminato da TestWorkspace ha CampiPianificazione vuoto: lo popoliamo con lo
        // stesso ordine di default del seed 6.3.0 (TestTemplateDefaults.CampiPianificazioneStandard) —
        // Stato lavoro prima di Scadenza — così il test dimostra davvero un cambiamento rispetto al default.
        var templates = state.Master.TestTemplate.Load();
        var template = templates.First(t => t.Nome == TestWorkspace.DefaultTemplateName);
        template.CampiPianificazione = new List<CampoTemplate>
        {
            new() { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.StatoLavoro, Nome = "Stato lavoro", Ordine = 0 },
            new() { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.Scadenza, Nome = "Scadenza", Ordine = 1 },
        };
        state.Master.TestTemplate.Save(templates);
        return workspace;
    }

    [Fact]
    public void RiordinaStatoLavoroEScadenzaNelTemplate_SiRifletteNelModuloTestReale()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.NavigateTo("impostazioni");
            ElementFinders.WaitById(window, "Impostazioni_Sub_tipiditest").AsListBoxItem().Select();
            ElementFinders.WaitButtonById(window, "Tpl_Modifica_Button").SafeInvoke();
            ElementFinders.WaitButtonById(window, "TplGestisci_Button").SafeInvoke();

            var tplEditor = App.WaitForWindowByContent("TplGestisciCampi_Pianificazione");
            ElementFinders.WaitButtonById(tplEditor, "TplGestisciCampi_Pianificazione").SafeInvoke();

            var campiEditor = App.WaitForWindowByContent("TplCampoSu_Scadenza");
            // Sposta Scadenza su: dopo questo click precede Stato lavoro (ordine di partenza: StatoLavoro,
            // Scadenza — un solo passo basta a scambiarli, essendo adiacenti).
            ElementFinders.WaitButtonById(campiEditor, "TplCampoSu_Scadenza").SafeInvoke();
            ElementFinders.WaitButtonById(campiEditor, "CampiEditor_Chiudi_Button").SafeInvoke();
            ElementFinders.WaitButtonById(tplEditor, "TplEditor_Chiudi_Button").SafeInvoke();

            ElementFinders.WaitButtonById(window, "Tpl_Salva_Button").SafeInvoke();

            var testList = shell.GoToTestList();
            var editor = testList.ClickNew();
            editor.ChooseTemplate();

            // Le etichette reali nel form (EtichettaStatoLavoro/EtichettaScadenza) sono testo fisso più
            // descrittivo del semplice nome del campo di sistema — verificato dall'albero UI salvato da un
            // primo tentativo fallito con le etichette "corte". Con solo 2 campi su 4 celle riservate
            // ((0,0)/(0,1)/(1,0)/(1,1)) restano sulla stessa riga (rango 0 e 1): il riordino sposta la
            // colonna, non la riga — verificato con uno screenshot di un run precedente (Scadenza in colonna
            // 0, Stato del lavoro in colonna 1, esattamente lo scambio atteso).
            // A 1280x800 (Compatto, 6.6.1) l'editor del Test e' a colonna unica: i due campi stanno uno sopra
            // l'altro e il riordino si legge in verticale. In entrambi i casi l'ordine di lettura e' la verifica.
            var statoLavoro = TrovaPerTesto(window, "Stato del lavoro").BoundingRectangle;
            var scadenza = TrovaPerTesto(window, "Scadenza operativa (opzionale)").BoundingRectangle;
            var stessaRiga = Math.Abs(scadenza.Top - statoLavoro.Top) < Math.Min(scadenza.Height, statoLavoro.Height) / 2;
            Assert.True(stessaRiga ? scadenza.Left < statoLavoro.Left : scadenza.Top < statoLavoro.Top,
                "Dopo il riordino nel template, 'Scadenza' deve precedere 'Stato lavoro' nel modulo Test reale (stessa riga: colonna scambiata; colonna unica: riga precedente).");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(RiordinaStatoLavoroEScadenzaNelTemplate_SiRifletteNelModuloTestReale), ex);
        }
    }

    private static FlaUI.Core.AutomationElements.AutomationElement TrovaPerTesto(FlaUI.Core.AutomationElements.AutomationElement scope, string testo) =>
        scope.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
            .FirstOrDefault(e => e.Name == testo)
        ?? throw new InvalidOperationException($"Testo '{testo}' non trovato nel modulo Test.");
}
