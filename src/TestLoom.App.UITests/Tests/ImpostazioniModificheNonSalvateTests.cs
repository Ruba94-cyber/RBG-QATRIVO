using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Modifiche non salvate cambiando sotto-sezione di Impostazioni (ImpostazioniViewModel.
/// CambiaDopoConferma/IEditorConModifiche.HaModificheNonSalvate): finora la sola conferma "modifiche non
/// salvate" testata via FlaUI era quella dell'editor Test (UnsavedChangesTests) o della navigazione tra le
/// pagine principali (implicita in MainShellPage.NavigateTo, mai esercitata da un test dedicato con dati
/// realmente modificati). Qui: entra in modifica su Requisiti, aggiunge una riga nuova (Requis_Nuovo_Button,
/// il modo piu' affidabile per rendere HaModifiche() vero senza editing di celle DataGrid) e prova a cambiare
/// sotto-sezione.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Dialogs")]
public sealed class ImpostazioniModificheNonSalvateTests : UiTestBase
{
    public ImpostazioniModificheNonSalvateTests() : base(TestWorkspace.CreateLoggedIn(nameof(ImpostazioniModificheNonSalvateTests))) { }

    [Fact]
    public void CambiareSottoSezione_ConModificheNonSalvate_ChiedeConfermaEAnnullaRestaSullaStessaPagina()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var impostazioni = shell.GoToImpostazioni();
            var requisiti = impostazioni.VaiARequisiti();

            requisiti.IniziaModifica();
            ElementFinders.WaitButtonById(window, "Requis_Nuovo_Button").SafeInvoke();

            impostazioni.VaiA("componente");

            var confirmWindow = App.WaitForWindowByContent("Confirm_Yes_Button");
            var confirm = new ConfirmDialogPage(confirmWindow);
            Assert.False(string.IsNullOrWhiteSpace(confirm.Message));
            confirm.ConfirmNo();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura del dialog dopo 'No'");

            // Rispondendo 'No' si resta su Requisiti, ancora in modifica (con la riga nuova non salvata).
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Requis_Salva_Button"),
                "dopo 'No' resta visibile 'Salva' su Requisiti: la navigazione e' stata annullata");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(CambiareSottoSezione_ConModificheNonSalvate_ChiedeConfermaEAnnullaRestaSullaStessaPagina), ex);
        }
    }
}
