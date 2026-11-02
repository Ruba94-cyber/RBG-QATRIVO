using System;
using System.Collections.Generic;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Flows;

/// <summary>
/// Percorso UI unico per arrivare all'editor di una Change Request dalla scheda Anomalie/CR. "Apri" nella riga apre
/// l'editor in SOLA LETTURA (Cr_Title_Text con l'id della CR, hint Cr_SolaLettura_Text, campi bloccati); "Modifica"
/// nell'editor riapre un editor nuovo, riletto dall'archivio, in modifica (l'hint sparisce e i campi si abilitano).
/// I test che devono modificare una CR passano da qui invece di ripetere la sequenza di apertura in ognuno.
/// </summary>
public static class ChangeRequestEditorFlow
{
    /// <summary>Id delle CR elencate nella scheda Change Request, nell'ordine a schermo.</summary>
    public static IReadOnlyList<string> IdCr(Window window) =>
        ElementFinders.WaitById(window, "Anomalie_Cr_Lista_ListBox")
            .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Where(t => (t.Name ?? "").StartsWith("CR-", StringComparison.Ordinal) && t.BoundingRectangle.Height > 0)
            .OrderBy(t => t.BoundingRectangle.Top).Select(t => t.Name!).ToList();

    /// <summary>Filtra la scheda CR con <paramref name="testoRicerca"/>, preme Apri nella riga della CR <paramref name="id"/>
    /// e attende l'editor in sola lettura di quella CR. Non entra in modifica.</summary>
    public static ChangeRequestEditorPage ApriInSolaLettura(Window window, string id, string testoRicerca)
    {
        new MainShellPage(window).GoToAnomalie();
        ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
        ElementFinders.WaitTextBoxById(window, "Anomalie_Cr_FiltroTesto_Input").SetText(testoRicerca);
        Wait.ForCondition(() => IdCr(window).Contains(id), $"la ricerca '{testoRicerca}' mostra la CR {id}");

        // La lista si ricarica dopo la ricerca: il pulsante si cerca di nuovo dentro la riga finche' l'editor della CR
        // richiesta non e' aperto (un clic su un riferimento gia' ricaricato non avrebbe effetto).
        Wait.ForCondition(() =>
        {
            var lista = ElementFinders.WaitById(window, "Anomalie_Cr_Lista_ListBox");
            var riga = lista.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
                .FirstOrDefault(r => r.FindFirstDescendant(cf => cf.ByName(id)) != null);
            riga?.FindFirstDescendant(cf => cf.ByAutomationId("Anomalie_Cr_Apri_Button"))?.AsButton().SafeInvoke();
            return ElementFinders.TryFindById(window, "Cr_Title_Text")?.Name == id;
        }, $"l'editor della CR {id} e' aperto");

        var editor = new ChangeRequestEditorPage(window);
        Wait.ForCondition(() => editor.InSolaLettura, $"l'editor della CR {id} si apre in sola lettura");
        return editor;
    }

    /// <summary>Preme Modifica nell'editor in sola lettura e attende l'editor in modifica: l'hint di sola lettura sparisce.</summary>
    public static ChangeRequestEditorPage EntraInModifica(ChangeRequestEditorPage editor)
    {
        editor.Modifica();
        Wait.ForCondition(() => !editor.InSolaLettura, "Modifica riapre l'editor della CR in modifica");
        return editor;
    }

    /// <summary>Porta in modifica la CR <paramref name="id"/>: Apri nella sua riga, poi Modifica nell'editor.</summary>
    public static ChangeRequestEditorPage ApriInModifica(Window window, string id, string testoRicerca) =>
        EntraInModifica(ApriInSolaLettura(window, id, testoRicerca));
}
