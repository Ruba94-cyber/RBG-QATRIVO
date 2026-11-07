using System.Linq;
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
        Wait.ForElement(() => TryFindById(scope, automationId),
            $"elemento con AutomationId='{automationId}'", timeout);

    public static AutomationElement? TryFindById(AutomationElement scope, string automationId) =>
        scope.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) ?? TryFindInSettingsTabs(scope, automationId)
            ?? TryFindInCampiStrumentoExpander(scope, automationId);

    /// <summary>Espande (idempotente, via pattern ExpandCollapse) i riquadri "Parametri tecnici dello strumento" di ogni operazione, cosi'
    /// i campi dello strumento per operazione sono presenti nell'albero UIA. Come farebbe l'utente aprendoli.</summary>
    public static void EspandiParametriOperazioni(AutomationElement scope)
    {
        try
        {
            foreach (var g in scope.FindAllDescendants(cf => cf.ByControlType(ControlType.Group)))
            {
                var id = g.Properties.AutomationId.ValueOrDefault ?? "";
                if (!id.EndsWith("_Parametri_Expander", StringComparison.Ordinal)) continue;
                var pattern = g.Patterns.ExpandCollapse.PatternOrDefault;
                if (pattern != null && pattern.ExpandCollapseState.Value != ExpandCollapseState.Expanded) pattern.Expand();
            }
            System.Threading.Thread.Sleep(150);
        }
        catch (Exception) { /* UIA transitorio: la verifica successiva fallisce con il proprio messaggio */ }
    }

    /// <summary>I campi degli strumenti ("Editor_Campo_*") stanno in riquadri compressi dell'editor Test (campi del Test e "Parametri tecnici
    /// dello strumento" di ogni operazione): se il campo non e' visibile i riquadri chiusi vengono espansi, uno alla volta e una sola
    /// volta ciascuno (come farebbe l'utente), cercando il campo dopo ogni espansione.</summary>
    private static AutomationElement? TryFindInCampiStrumentoExpander(AutomationElement scope, string automationId)
    {
        if (!automationId.StartsWith("Editor_Campo_", StringComparison.Ordinal)) return null;
        try
        {
            var riquadri = scope.FindAllDescendants(cf => cf.ByControlType(ControlType.Group))
                .Where(g =>
                {
                    var id = g.Properties.AutomationId.ValueOrDefault ?? "";
                    return id == "Editor_CampiStrumentiSpecifici_Expander" || id.EndsWith("_Parametri_Expander", StringComparison.Ordinal);
                }).ToList();
            foreach (var riquadro in riquadri)
            {
                // Stato letto dal pattern ExpandCollapse del riquadro (idempotente: mai un secondo clic che lo richiuda).
                var pattern = riquadro.Patterns.ExpandCollapse.PatternOrDefault;
                if (pattern != null)
                {
                    if (pattern.ExpandCollapseState.Value == ExpandCollapseState.Expanded) continue;
                    pattern.Expand();
                }
                else
                {
                    var intestazione = riquadro.FindFirstDescendant(cf => cf.ByAutomationId("ExpanderHeader"));
                    if (intestazione == null) continue;
                    var invoke = intestazione.Patterns.Invoke.PatternOrDefault;
                    if (invoke != null) invoke.Invoke(); else intestazione.Click();
                }
                for (var i = 0; i < 12; i++)
                {
                    var trovato = scope.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                    if (trovato != null) return trovato;
                    System.Threading.Thread.Sleep(100);
                }
            }
            return scope.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Le Impostazioni generali sono organizzate in schede: un controllo "Impostazioni_*" non visibile viene cercato
    /// selezionando in sequenza le schede (la scheda resta quella in cui l'elemento e' stato trovato). Solo per scope finestra.</summary>
    private static AutomationElement? TryFindInSettingsTabs(AutomationElement scope, string automationId)
    {
        // Qualsiasi controllo della pagina Impostazioni generali (anche con prefisso diverso, es. RecordFork_*, MasterConflict_*).
        if (automationId is "Impostazioni_Generale_Tab" or "Impostazioni_Sidebar_List" or "Impostazioni_Title_Text") return null;
        try
        {
            var tab = scope.FindFirstDescendant(cf => cf.ByAutomationId("Impostazioni_Generale_Tab"));
            if (tab == null) return null;
            foreach (var scheda in tab.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem)))
            {
                scheda.AsTabItem().Select();
                var trovato = scope.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                if (trovato != null) return trovato;
            }
        }
        catch (Exception) { /* UIA transitorio: l'attesa riprova */ }
        return null;
    }

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
