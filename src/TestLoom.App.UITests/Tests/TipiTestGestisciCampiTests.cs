using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Prima copertura FlaUI per Impostazioni -> Tipi di test/CR -> "Gestisci campi" (apre
/// CampiTemplateEditorWindow, finora con un solo AutomationId — il pulsante Chiudi — e nessun test che ci
/// arrivasse). L'ordine dei campi dentro un template e' gia' coperto da TemplateClassiciOrdineUiTests/
/// CardClassicheOrdineUiTests: qui si verifica solo che la finestra si apra/chiuda correttamente dal catalogo
/// Tipi di test. L'editing dei singoli campi (aggiungi/rimuovi/rinomina un campo, catalogo campi di sistema)
/// resta GAP: CampiTemplateEditorWindow non ha altri AutomationId sui controlli di editing.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Navigation")]
public sealed class TipiTestGestisciCampiTests : UiTestBase
{
    public TipiTestGestisciCampiTests() : base(TestWorkspace.CreateLoggedIn(nameof(TipiTestGestisciCampiTests))) { }

    [Fact]
    public void GestisciCampi_SiApreESiChiude_PerIlTemplateSeminato()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var impostazioni = shell.GoToImpostazioni();
            impostazioni.VaiA("tipiditest");

            Wait.ForCondition(() => ElementFinders.ExistsById(window, "GridPrincipale"), "griglia Tipi di test caricata");
            ElementFinders.WaitButtonById(window, "Tpl_Modifica_Button").SafeInvoke();

            // "TplGestisci_Button" (riga della griglia Tipi di test) apre il TestTemplateEditorWindow
            // (TplEditor_Chiudi_Button), NON direttamente CampiTemplateEditorWindow: quest'ultimo si apre solo
            // premendo uno dei bottoni "Gestisci campi (N)" PER SEZIONE dentro l'editor del template
            // (TplGestisciCampi_Pianificazione/Dettagli/Riferimenti, TestTemplateEditorWindow.axaml.cs) — un
            // passaggio intermedio che questo test saltava.
            ElementFinders.WaitButtonById(window, "TplGestisci_Button").SafeInvoke();
            var editorTemplate = App.WaitForWindowByContent("TplEditor_Chiudi_Button");
            ElementFinders.WaitButtonById(editorTemplate, "TplGestisciCampi_Dettagli").SafeInvoke();

            var editorCampi = App.WaitForWindowByContent("CampiEditor_Chiudi_Button");
            Assert.NotNull(editorCampi);

            ElementFinders.WaitButtonById(editorCampi, "CampiEditor_Chiudi_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("CampiEditor_Chiudi_Button"), "chiusura di 'Gestisci campi'");

            ElementFinders.WaitButtonById(editorTemplate, "TplEditor_Chiudi_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("TplEditor_Chiudi_Button"), "chiusura dell'editor del template");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(GestisciCampi_SiApreESiChiude_PerIlTemplateSeminato), ex);
        }
    }
}
