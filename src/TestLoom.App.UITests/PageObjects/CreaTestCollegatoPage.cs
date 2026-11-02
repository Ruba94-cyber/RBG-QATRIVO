using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>Finestra "Crea test collegato" (CreaTestCollegatoWindow), aperta modale da
/// TestEditorPage.CreaTestCollegato(). A differenza dei Flyout (Confirm/SelezionaDialog), e' una vera Window
/// (ShowDialog): FlaUI la trova come qualunque altra finestra di primo livello.</summary>
public sealed class CreaTestCollegatoPage
{
    private readonly Window _window;

    public CreaTestCollegatoPage(Window window) => _window = window;

    public void SelezionaTemplate(string nomeTemplate) =>
        ElementFinders.WaitById(_window, $"CreaTestCollegato_Template_{nomeTemplate}").SafeInvoke();

    /// <summary>Le due azioni dichiarano AutomationId espliciti nel markup della finestra.</summary>
    public void Crea() => ElementFinders.WaitButtonById(_window, "CreaTestCollegato_Crea_Button").SafeInvoke();

    public void Annulla() => ElementFinders.WaitButtonById(_window, "CreaTestCollegato_Annulla_Button").SafeInvoke();
}
