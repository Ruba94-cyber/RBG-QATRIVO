using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

[Collection("TestLoom UI")]
[Trait("Category", "Dialogs")]
public sealed class DialogTests : UiTestBase
{
    public DialogTests() : base(TestWorkspace.CreateLoggedIn(nameof(DialogTests))) { }

    [Fact]
    public void DialogSceltaEcu_Annulla_NonAssegnaNessunaEcuAlTest()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);

            var testList = shell.GoToTestList();
            var editor = testList.ClickNew();
            editor.ChooseTemplate();
            editor.GoToTab(1);

            var scegliEcu = window.FindFirstDescendant(cf => cf.ByAutomationId("Editor_ScegliEcu_Button"));
            scegliEcu!.AsButton().Invoke();
            var dialog = new SelectDialogPage(App.WaitForWindowByContent("SelectDialog_Items_ListBox"));
            dialog.SelectItem(TestWorkspace.DefaultEcuName);
            dialog.Cancel();

            Wait.ForCondition(() => App.IsWindowClosedByContent("SelectDialog_Items_ListBox"), "chiusura del dialog dopo Annulla");

            // Nessuna componente selezionata: Salva deve quindi restare bloccato dalla validazione (componente obbligatoria
            // nel template minimo di QA), la stessa garanzia usata dal test di validazione sulla Descrizione
            // breve — qui a dimostrare che Annulla non ha lasciato per errore un'assegnazione parziale.
            editor.SetDescrizioneBreve("QA smoke - annulla scelta componente");
            editor.GoToTab(4);
            editor.Save();

            Wait.ForCondition(() => editor.HasValidationErrors, "errore di validazione componente dopo Annulla nel dialog di scelta");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(DialogSceltaEcu_Annulla_NonAssegnaNessunaEcuAlTest), ex);
        }
    }
}
