using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>SelezionaDialog: finestra generica riusata da tutta l'app per "Scegli componente/Veicolo/Funzionalità/…"
/// quando l'elenco supera la soglia della ComboBox inline (vedi [[project_ditm_lista_soglia_universale]]),
/// e sempre per componente indipendentemente dal conteggio.</summary>
public sealed class SelectDialogPage
{
    private readonly Window _window;

    public SelectDialogPage(Window window) => _window = window;

    public void Filter(string text) =>
        ElementFinders.WaitTextBoxById(_window, "SelectDialog_Filter_TextBox").SetText(text);

    public void SelectItem(string text) =>
        ElementFinders.WaitListBoxById(_window, "SelectDialog_Items_ListBox").SelectByText(text);

    /// <summary>Nomi delle voci realizzate nella lista, in ordine.</summary>
    public IReadOnlyList<string> NomiVoci() =>
        ElementFinders.WaitListBoxById(_window, "SelectDialog_Items_ListBox").Items.Select(i => i.Name ?? string.Empty).ToList();

    /// <summary>Testo del contatore sopra l'elenco ("N voci" / "N risultati"): cambia quando la ricerca viene applicata.</summary>
    public string ContatoreTesto() => ElementFinders.WaitById(_window, "SelectDialog_Count_Text").Name ?? string.Empty;

    /// <summary>True se la voce con quel nome e' nella lista (dopo il filtro la lista mostra solo i risultati).</summary>
    public bool HasItem(string text) =>
        ElementFinders.WaitListBoxById(_window, "SelectDialog_Items_ListBox").FindFirstDescendant(cf => cf.ByName(text)) != null;

    /// <summary>Solo tastiera: freccia giu' dal campo di ricerca porta il cursore sulla prima voce, Invio conferma.</summary>
    public void ConfirmWithKeyboard()
    {
        ElementFinders.WaitTextBoxById(_window, "SelectDialog_Filter_TextBox").Focus();
        FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.DOWN);
        FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
    }

    public void Confirm() =>
        ElementFinders.WaitButtonById(_window, "SelectDialog_Confirm_Button").SafeInvoke();

    public void Cancel() =>
        ElementFinders.WaitButtonById(_window, "SelectDialog_Cancel_Button").SafeInvoke();
}
