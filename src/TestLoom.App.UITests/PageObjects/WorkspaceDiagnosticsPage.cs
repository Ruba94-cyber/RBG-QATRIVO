using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>Page Object read-only; verifica l'inventario locale, NON la sincronizzazione cloud.</summary>
public sealed class WorkspaceDiagnosticsPage
{
    private readonly Window _window;
    public WorkspaceDiagnosticsPage(Window window)
    {
        _window = window;
        ElementFinders.WaitById(window, "Integrity_Title_Text");
        ElementFinders.WaitById(window, "Integrity_Details_TextBox");
    }

    public string Status => ElementFinders.WaitById(_window, "Integrity_Status_Text").AsLabel().Text;
    public string Details => ElementFinders.WaitTextBoxById(_window, "Integrity_Details_TextBox").Text;
    public bool CanRefresh => ElementFinders.WaitButtonById(_window, "Integrity_Refresh_Button").IsEnabled;

    public void Refresh() =>
        ElementFinders.WaitButtonById(_window, "Integrity_Refresh_Button").SafeInvoke();

    public void Close() =>
        ElementFinders.WaitButtonById(_window, "Integrity_Close_Button").SafeInvoke();
}
