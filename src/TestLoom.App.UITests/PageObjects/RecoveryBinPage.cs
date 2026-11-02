using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>
/// Impostazioni → Recovery (solo Master): elenco delle copie di recupero dei record cancellati. Ogni riga mostra la
/// categoria, lo stato e il percorso originale; "Ripristina" e "Purga" sono dentro ogni riga (stesso AutomationId in
/// tutte le righe) e agiscono su quella riga, chiedendo conferma. Per questo si cercano solo dentro la riga indicata.
/// </summary>
public sealed class RecoveryBinPage
{
    private readonly Window _window;

    public RecoveryBinPage(Window window) => _window = window;

    /// <summary>Attende l'elenco del cestino (la pagina Recovery è caricata).</summary>
    public void Attendi() => ElementFinders.WaitById(_window, "Recovery_Elenco_ListBox");

    /// <summary>Riga il cui percorso originale contiene il frammento indicato (es. il nome del file del Test).</summary>
    public AutomationElement RigaPer(string frammentoPercorso)
    {
        var elenco = ElementFinders.WaitById(_window, "Recovery_Elenco_ListBox");
        return Wait.ForElement(
            () => elenco.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .FirstOrDefault(r => r.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Any(t => (t.Name ?? string.Empty).Contains(frammentoPercorso, StringComparison.Ordinal))),
            $"riga del cestino con '{frammentoPercorso}'");
    }

    public void Ripristina(AutomationElement riga) =>
        ElementFinders.WaitButtonById(riga, "Recovery_Ripristina_Button").SafeInvoke();

    public void Purga(AutomationElement riga) =>
        ElementFinders.WaitButtonById(riga, "Recovery_Purga_Button").SafeInvoke();
}
