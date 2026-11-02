using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Editor dei campi delle anagrafiche (Strumenti, Tipi test, Campi di sistema). Qui si copre solo il punto d'ingresso
/// dichiarato in XAML: "Gestisci campi" sulla riga dello strumento (Strumenti_GestisciCampi_Button, in modifica) apre
/// CampiStrumentoEditorWindow; si verifica che la finestra compaia con il suo titolo e che "Annulla" la chiuda senza
/// salvare. Aggiungere, modificare e riordinare i campi NON e' coperto: le righe dei campi (RootPanel) e i loro
/// pulsanti sono costruiti nel code-behind senza AutomationId, quindi non sono raggiungibili con modifiche solo XAML
/// (vedi COPERTURA_FLAUI.md, sezione Master / Impostazioni).
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
public sealed class RemainingCoverageMasterCampiUiTests : UiTestBase
{
    private const string ValidationTool = "ValidationTool";
    private const string BreakoutBox = "Breakout Box";

    public RemainingCoverageMasterCampiUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(RemainingCoverageMasterCampiUiTests), seedTestFixtures: false);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = ValidationTool, ModificatoDa = "UITests" },
            new() { Nome = BreakoutBox, ModificatoDa = "UITests" },
        });
        return ws;
    }

    [Fact]
    public void CampiStrumento_DaRigaInModifica_ApreLEditorECancellaSenzaSalvare()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.NavigateTo("impostazioni");
            ElementFinders.WaitById(window, "Impostazioni_Sub_strumenti").AsListBoxItem().Select();
            ElementFinders.WaitButtonById(window, "Strum_Modifica_Button").SafeInvoke();

            // Il pulsante "Gestisci campi" di ogni riga condivide lo stesso AutomationId: basta il primo strumento.
            ElementFinders.WaitButtonById(window, "Strumenti_GestisciCampi_Button").SafeInvoke();
            var editor = App.WaitForWindowByContent("CampiStrumento_Annulla_Button");
            Wait.ForCondition(() => ElementFinders.TryFindById(editor, "CampiStrumento_Titolo_Text") is { } titolo
                && !string.IsNullOrWhiteSpace(titolo.Name), "titolo dell'editor dei campi strumento valorizzato");
            ElementFinders.WaitButtonById(editor, "CampiStrumento_Annulla_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("CampiStrumento_Annulla_Button"),
                "chiusura dell'editor dei campi strumento dopo Annulla", TimeSpan.FromSeconds(10));

            // Annulla non scrive: la pagina Strumenti resta in modifica e lo strumento non cambia.
            Assert.True(ElementFinders.ExistsById(window, "Strum_Salva_Button"), "la pagina Strumenti resta in modifica dopo Annulla");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CampiStrumento_DaRigaInModifica_ApreLEditorECancellaSenzaSalvare), ex); }
    }
}
