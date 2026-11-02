using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Selettore a elenco lungo (SelezionaDialog, usato per la scelta componente): ricerca digitata, freccia giu' e
/// Invio, senza mouse. Verifica il percorso da tastiera del selettore con ricerca a debounce (PickerRicerca).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Dialogs")]
public sealed class PickerRicercaSelezionaDialogUiTests : UiTestBase
{
    public PickerRicercaSelezionaDialogUiTests() : base(TestWorkspace.CreateLoggedIn(nameof(PickerRicercaSelezionaDialogUiTests))) { }

    [Fact]
    public void PickerEcu_DigitaFrecciaEInvio_SelezionaLaVoceFiltrata()
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

            // Bersaglio = ultima voce dell'elenco, mai la prima: se la freccia agisse sull'elenco prima della ricerca
            // il test sceglierebbe la prima voce e fallirebbe. Digitazione e tasti arrivano senza attendere il
            // debounce: la ricerca in attesa deve essere allineata prima della freccia e di Invio.
            var nomi = dialog.NomiVoci();
            Assert.True(nomi.Count >= 2, "servono almeno due componente per distinguere la voce cercata dalla prima dell'elenco");
            var bersaglio = nomi[^1];

            dialog.Filter(bersaglio);
            dialog.ConfirmWithKeyboard();

            Wait.ForCondition(() => App.IsWindowClosedByContent("SelectDialog_Items_ListBox"), "chiusura del dialog dopo Invio da tastiera");
            Wait.ForCondition(() => window.FindFirstDescendant(cf => cf.ByName(bersaglio)) != null,
                "Componente scelta da tastiera visibile nell'editor del test");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(PickerEcu_DigitaFrecciaEInvio_SelezionaLaVoceFiltrata), ex);
        }
    }
}
