using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.Helpers;

/// <summary>
/// Locator con l'ordine di preferenza richiesto: AutomationId, poi AutomationId+ControlType quando serve
/// disambiguare tra elementi con lo stesso Id, poi Name/ClassName+ControlType solo per i pochi controlli
/// riusabili senza Id proprio (es. i due Button di ConfirmDialog hanno già un AutomationId dedicato: qui
/// servono principalmente per gli elementi ripetuti di un template — es. il bottone "Apri" di ogni riga della
/// DataGrid Test, che condivide lo stesso AutomationId in tutte le righe e va quindi cercato con Wait
/// (compare quando la griglia ha finito di popolarsi) scoped a un preciso elemento riga, mai per intera app.
/// </summary>
public static class ElementFinders
{
    public static AutomationElement WaitById(AutomationElement scope, string automationId, TimeSpan? timeout = null) =>
        Wait.ForElement(() => scope.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            $"elemento con AutomationId='{automationId}'", timeout);

    public static AutomationElement? TryFindById(AutomationElement scope, string automationId) =>
        scope.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    public static bool ExistsById(AutomationElement scope, string automationId) =>
        TryFindById(scope, automationId) != null;

    public static Button WaitButtonById(AutomationElement scope, string automationId, TimeSpan? timeout = null) =>
        WaitById(scope, automationId, timeout).AsButton();

    public static TextBox WaitTextBoxById(AutomationElement scope, string automationId, TimeSpan? timeout = null) =>
        WaitById(scope, automationId, timeout).AsTextBox();

    public static ComboBox WaitComboBoxById(AutomationElement scope, string automationId, TimeSpan? timeout = null) =>
        WaitById(scope, automationId, timeout).AsComboBox();

    public static ListBox WaitListBoxById(AutomationElement scope, string automationId, TimeSpan? timeout = null) =>
        WaitById(scope, automationId, timeout).AsListBox();

    /// <summary>Un elemento ripetuto per riga di una lista/griglia (es. AutomationId condiviso da ogni riga
    /// della DataGrid Test): cerca tra i discendenti di UNA riga specifica, non della griglia intera.</summary>
    public static AutomationElement WaitByIdWithinRow(AutomationElement row, string automationId, TimeSpan? timeout = null) =>
        Wait.ForElement(() => row.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            $"elemento con AutomationId='{automationId}' dentro la riga", timeout);

    /// <summary>Trova un discendente per AutomationId qualificato anche dal ControlType (secondo gradino
    /// dell'ordine di locator) — usato quando un contenitore riusabile ospita più elementi con lo stesso Id
    /// ma ControlType diverso (es. un template con TextBlock ed Button che condividessero per errore l'Id).</summary>
    public static AutomationElement WaitByIdAndControlType(AutomationElement scope, string automationId, ControlType controlType, TimeSpan? timeout = null) =>
        Wait.ForElement(() => scope.FindFirstDescendant(cf => cf.ByAutomationId(automationId).And(cf.ByControlType(controlType))),
            $"elemento con AutomationId='{automationId}' e ControlType={controlType}", timeout);
}
