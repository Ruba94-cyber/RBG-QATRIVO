using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura FlaUI dedicata al riordino degli STRUMENTI stessi (spec riordino campi 2026-09-23 §2,
/// seguito coordinamento 2026-09-24): ▲/▼ nella pagina master Impostazioni → Strumenti (StrumentiView,
/// StrumentiViewModel.SpostaSuStrumentoCommand). Copertura unitaria già presente in
/// TestLoom.App.Tests.StrumentiOrdineTests per la propagazione al modulo Test — qui verifichiamo solo che il
/// vero eseguibile Avalonia risponda al click reale su ▲ riordinando davvero la riga a schermo, cosa che un
/// test unitario sul ViewModel non può provare. Legge l'ordine confrontando la posizione Y a schermo dei due
/// nomi (nessun AutomationId sulla cella Nome, una DataGridTextColumn semplice) invece di affidarsi al
/// pattern Grid di UI Automation sulla DataGrid Avalonia, mai usato altrove in questa suite. AutomationId dei
/// pulsanti ▲/▼ parametrizzato per strumento (StrumBtnSpostaSu_{Nome}, MINOR review indipendente 2026-09-24):
/// non serve più risalire dal testo della cella al DataItem di riga.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
public sealed class StrumentiOrdineUiTests : UiTestBase
{
    private const string ValidationTool = "ValidationTool";
    private const string BreakoutBox = "Breakout Box";

    public StrumentiOrdineUiTests() : base(CreaWorkspaceConStrumenti()) { }

    private static TestWorkspace CreaWorkspaceConStrumenti()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(StrumentiOrdineUiTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        // Ordine alfabetico di partenza (nessun Ordine esplicito ancora impostato): Breakout Box, ValidationTool.
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = ValidationTool, ModificatoDa = "UITests" },
            new() { Nome = BreakoutBox, ModificatoDa = "UITests" },
        });
        return workspace;
    }

    [Fact]
    public void SpostaSuStrumento_CambiaLOrdineARigheReale()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.NavigateTo("impostazioni");
            ElementFinders.WaitById(window, "Impostazioni_Sub_strumenti").AsListBoxItem().Select();

            var griglia = ElementFinders.WaitById(window, "GridPrincipale");
            Wait.ForCondition(() => TrovaTesto(griglia, ValidationTool) != null && TrovaTesto(griglia, BreakoutBox) != null,
                "le due righe seminate compaiono in tabella");
            Assert.True(YDi(griglia, BreakoutBox) < YDi(griglia, ValidationTool), "Ordine alfabetico di partenza: Breakout Box sopra ValidationTool.");

            ElementFinders.WaitButtonById(window, "Strum_Modifica_Button").SafeInvoke();

            ElementFinders.WaitButtonById(window, $"StrumBtnSpostaSu_{ValidationTool}").SafeInvoke();

            Wait.ForCondition(() => YDi(griglia, ValidationTool) < YDi(griglia, BreakoutBox), "ValidationTool sopra Breakout Box dopo lo spostamento su");

            ElementFinders.WaitButtonById(window, "Strum_Salva_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Strum_Modifica_Button"), "torna in sola lettura dopo Salva");

            // L'ordine deve restare quello scelto anche dopo il salvataggio (persistito, non solo visuale).
            Assert.True(YDi(griglia, ValidationTool) < YDi(griglia, BreakoutBox));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(SpostaSuStrumento_CambiaLOrdineARigheReale), ex);
        }
    }

    /// <summary>6.5.1 fase 3: la combo "Tipo" dell'editor Strumenti compare (AutomationId per strumento), mostra il tipo effettivo
    /// (ValidationTool = Supporto per nome), si modifica solo in modifica e la scelta resta dopo il salvataggio.</summary>
    [Fact]
    public void ComboTipoStrumento_ScegliDaTestare_RestaDopoSalva()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.NavigateTo("impostazioni");
            ElementFinders.WaitById(window, "Impostazioni_Sub_strumenti").AsListBoxItem().Select();
            var id = $"Strum_Tipo_{ValidationTool}_Combo";
            var combo = ElementFinders.WaitComboBoxById(window, id);
            Assert.False(combo.IsEnabled, "In sola lettura la combo Tipo e disabilitata.");

            ElementFinders.WaitButtonById(window, "Strum_Modifica_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.WaitComboBoxById(window, id).IsEnabled, "la combo Tipo si abilita in modifica");
            ElementFinders.WaitComboBoxById(window, id).SelectByText("Da testare");
            Wait.ForCondition(() => TestoCombo(window, id) == "Da testare", "scelta 'Da testare' applicata");

            ElementFinders.WaitButtonById(window, "Strum_Salva_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Strum_Modifica_Button"), "torna in sola lettura dopo Salva");
            Assert.Equal("Da testare", TestoCombo(window, id));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ComboTipoStrumento_ScegliDaTestare_RestaDopoSalva), ex);
        }
    }

    // Il valore corrente non si legge da ComboBox.SelectedItem (Avalonia non lo espone in modo affidabile): si legge il testo mostrato.
    private static string? TestoCombo(AutomationElement window, string id) =>
        ElementFinders.WaitComboBoxById(window, id).FindFirstDescendant(cf => cf.ByControlType(ControlType.Text))?.Name;

    private static AutomationElement? TrovaTesto(AutomationElement scope, string testo) =>
        scope.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).FirstOrDefault(e => e.Name == testo);

    private static double YDi(AutomationElement scope, string testo) =>
        (TrovaTesto(scope, testo) ?? throw new InvalidOperationException($"Testo '{testo}' non trovato in tabella")).BoundingRectangle.Y;
}
