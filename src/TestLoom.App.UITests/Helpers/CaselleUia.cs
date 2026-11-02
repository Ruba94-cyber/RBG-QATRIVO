using TestLoom.App.UITests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.Helpers;

/// <summary>
/// Ricerca e manovra di finestre annidate, righe e caselle in UI Automation, condivise dai test delle finestre Veicoli e
/// Funzionalita'. Le righe degli elenchi ripetono gli AutomationId: si cerca per il nome dato (componente, funzionalita', veicolo),
/// che il test ha seminato, e si risale alla riga che contiene la casella.
/// </summary>
public static class CaselleUia
{
    /// <summary>La finestra più interna che contiene l'AutomationId indicato. Le finestre possedute compaiono come
    /// discendenti della principale in UI Automation: si sceglie quella senza un'altra finestra discendente con lo stesso id.</summary>
    public static AutomationElement? TrovaFinestra(AutomationElement principale, string automationId) =>
        principale.FindAllDescendants(cf => cf.ByControlType(ControlType.Window))
            .FirstOrDefault(w => ElementFinders.ExistsById(w, automationId)
                && !w.FindAllDescendants(cf => cf.ByControlType(ControlType.Window)).Any(d => ElementFinders.ExistsById(d, automationId)));

    /// <summary>Risale dalla casella alla riga dell'elenco (ListItem). Se la risalita non trova una riga, usa il genitore diretto.</summary>
    public static AutomationElement RigaDellaCasella(AutomationElement casella)
    {
        var corrente = casella;
        for (var i = 0; i < 8 && corrente != null; i++)
        {
            if (corrente.ControlType == ControlType.ListItem) return corrente;
            corrente = corrente.Parent;
        }
        return casella.Parent ?? casella;
    }

    /// <summary>Porta la casella a stato acceso/spento con il pattern Toggle (al massimo quattro tentativi, poi attesa).</summary>
    public static void ImpostaSpunta(AutomationElement casella, bool accesa)
    {
        var target = accesa ? ToggleState.On : ToggleState.Off;
        casella.Focus();
        for (var i = 0; i < 4 && StatoSpunta(casella) != target; i++)
            casella.Patterns.Toggle.Pattern.Toggle();
        Wait.ForCondition(() => StatoSpunta(casella) == target, $"spunta '{casella.Name}' nello stato {target}", TimeSpan.FromSeconds(5));
    }

    public static ToggleState StatoSpunta(AutomationElement casella) => casella.Patterns.Toggle.Pattern.ToggleState.Value;

    /// <summary>Riga di una griglia Master che contiene il testo indicato in una delle sue celle.</summary>
    public static AutomationElement? RigaConTesto(AutomationElement griglia, string testo) =>
        griglia.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
            .FirstOrDefault(r => r.FindAllDescendants().Any(d => d.Name == testo));

    /// <summary>Nomi non vuoti di tutti i discendenti dell'elemento (celle, pulsanti, caselle, testi).</summary>
    public static IReadOnlyList<string> TestiDiscendenti(AutomationElement elemento) =>
        elemento.FindAllDescendants().Select(d => d.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
}
