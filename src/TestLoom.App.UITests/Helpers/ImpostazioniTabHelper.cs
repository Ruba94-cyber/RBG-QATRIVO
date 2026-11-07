using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.Helpers;

/// <summary>Le Impostazioni generali sono organizzate in schede (Aspetto, TICKET, Cartella, Conflitti, Manutenzione, Info):
/// un controllo vive in una sola scheda. Il helper seleziona la scheda che contiene l'elemento cercato, senza dipendere
/// dal testo tradotto delle intestazioni.</summary>
internal static class ImpostazioniTabHelper
{
    public static void RendiVisibile(Window window, string automationId, TimeSpan? timeout = null)
    {
        Wait.ForCondition(() => ElementFinders.ExistsById(window, "Impostazioni_Generale_Tab")
                || ElementFinders.ExistsById(window, automationId),
            $"Impostazioni caricate (cerco {automationId})", timeout ?? TimeSpan.FromSeconds(15));
        if (ElementFinders.ExistsById(window, automationId)) return;
        var tab = ElementFinders.WaitById(window, "Impostazioni_Generale_Tab");
        var schede = tab.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem));
        foreach (var scheda in schede)
        {
            scheda.AsTabItem().Select();
            if (Wait.TryForCondition(() => ElementFinders.ExistsById(window, automationId), TimeSpan.FromSeconds(2))) return;
        }
        // Nessuna scheda lo contiene: l'attesa standard produce il messaggio di errore con l'AutomationId.
        ElementFinders.WaitById(window, automationId);
    }
}
