using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>ConfirmDialog: il dialog Sì/No generico riusato per ogni conferma distruttiva o di abbandono
/// modifiche dell'app (es. "Ci sono modifiche non salvate, uscire comunque?").</summary>
public sealed class ConfirmDialogPage
{
    private readonly Window _window;

    public ConfirmDialogPage(Window window) => _window = window;

    public string Message => ElementFinders.WaitById(_window, "Confirm_Message_Text").AsLabel().Text;

    public void ConfirmYes() =>
        ElementFinders.WaitButtonById(_window, "Confirm_Yes_Button").SafeInvoke();

    public void ConfirmNo() =>
        ElementFinders.WaitButtonById(_window, "Confirm_No_Button").SafeInvoke();
}
