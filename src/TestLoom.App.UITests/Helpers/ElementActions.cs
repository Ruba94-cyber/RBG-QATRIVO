using System.Globalization;
using TestLoom.App.UITests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace TestLoom.App.UITests.Helpers;

/// <summary>
/// Azioni robuste sugli elementi. <see cref="SafeInvoke"/> prova, in quest'ordine: InvokePattern; SelectionItem,
/// Toggle o ExpandCollapse secondo i pattern esposti; focus più tasto (Spazio, Invio per i MenuItem). Il click di
/// coordinate, che dipende da posizione a schermo e dimensione della finestra, è solo l'ultima risorsa e ogni suo uso
/// scrive un avviso con l'AutomationId: una copertura AutomationPeer incompleta in Avalonia 12 non deve tradursi in un
/// test fragile né in un fallimento silenzioso. Se anche il click fallisce, l'eccezione risale al chiamante (che la
/// registra via CaptureFailureAndRethrow).
/// </summary>
public static class ElementActions
{
    public static void SafeInvoke(this AutomationElement element)
    {
        element.WaitUntilEnabled();
        if (TryInvokePattern(element)) return;
        if (TryPatternActivation(element)) return;
        if (TryKeyboardActivation(element)) return;
        WarnCoordinateClick(element);
        element.Click();
    }

    /// <summary>1. InvokePattern. Se il pattern manca o lancia un'eccezione si passa al passo successivo.</summary>
    private static bool TryInvokePattern(AutomationElement element)
    {
        try
        {
            if (!element.Patterns.Invoke.TryGetPattern(out var invokePattern)) return false;
            invokePattern.Invoke();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 2. SelectionItem, Toggle o ExpandCollapse, secondo i pattern esposti. ExpandCollapse alterna lo stato come farebbe
    /// un click (Comprimi se aperto, Espandi altrimenti); un nodo foglia non ha stato da alternare. I MenuItem non passano
    /// da qui: si attivano da tastiera (Invio), come in ReportPage.
    /// </summary>
    private static bool TryPatternActivation(AutomationElement element)
    {
        try
        {
            if (element.Patterns.SelectionItem.TryGetPattern(out var selectPattern))
            {
                selectPattern.Select();
                return true;
            }
            if (element.Patterns.Toggle.TryGetPattern(out var togglePattern))
            {
                togglePattern.Toggle();
                return true;
            }
            if (element.ControlType != ControlType.MenuItem
                && element.Patterns.ExpandCollapse.TryGetPattern(out var expandPattern))
            {
                var stato = expandPattern.ExpandCollapseState.Value;
                if (stato == ExpandCollapseState.LeafNode) return false;
                if (stato == ExpandCollapseState.Expanded) expandPattern.Collapse();
                else expandPattern.Expand();
                return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 3. Focus sull'elemento e tasto di attivazione: Invio per i MenuItem, Spazio altrimenti. Il tasto parte solo se la
    /// finestra dell'elemento è in primo piano (verificato subito, senza attese) e l'elemento ha davvero il focus:
    /// altrimenti finirebbe su un altro controllo.
    /// </summary>
    private static bool TryKeyboardActivation(AutomationElement element)
    {
        try
        {
            var hwnd = HandleFinestra(element);
            if (hwnd == IntPtr.Zero) return false;
            SetForegroundWindow(hwnd);
            if (GetForegroundWindow() != hwnd) return false;
            element.Focus();
            if (!element.FrameworkAutomationElement.HasKeyboardFocus) return false;
            Keyboard.Type(element.ControlType == ControlType.MenuItem ? VirtualKeyShort.ENTER : VirtualKeyShort.SPACE);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 4. Ultima risorsa: il click di coordinate dipende da posizione e dimensione della finestra. Ogni uso scrive una
    /// riga con l'AutomationId, così i controlli da correggere si trovano nei log.
    /// </summary>
    private static void WarnCoordinateClick(AutomationElement element) =>
        Console.WriteLine($"WARNING [UITests] SafeInvoke: click di coordinate come ultima risorsa su AutomationId='{AutomationIdSafe(element)}' (nessun Invoke, SelectionItem, Toggle, ExpandCollapse o tasto applicabile).");

    /// <summary>Handle nativo della finestra che contiene l'elemento, risalendo l'albero; zero se non lo trova.</summary>
    private static IntPtr HandleFinestra(AutomationElement element)
    {
        for (var nodo = element; nodo != null; nodo = nodo.Parent)
        {
            var hwnd = nodo.FrameworkAutomationElement.NativeWindowHandle;
            if (hwnd != IntPtr.Zero) return hwnd;
        }
        return IntPtr.Zero;
    }

    /// <summary>AutomationId letto senza eccezioni: l'elemento potrebbe essere già stantio.</summary>
    private static string AutomationIdSafe(AutomationElement element)
    {
        try { return element.AutomationId; }
        catch { return "?"; }
    }

    /// <summary>La stringa di descrizione era costruita leggendo element.AutomationId PRIMA di entrare nel
    /// polling di Wait.ForCondition (un argomento C# si valuta sempre prima della chiamata): se il peer UIA
    /// dell'elemento non supporta ancora quella proprietà in questo istante — visto sia su liste
    /// virtualizzate sia su un semplice Button, quindi non specifico alla virtualizzazione — l'eccezione
    /// usciva IMMEDIATAMENTE, senza nessuno dei retry che Wait.ForCondition applica invece al probe
    /// IsEnabled. Il nome va letto dentro un try/catch, mai lasciato propagare da qui.</summary>
    private static void WaitUntilEnabled(this AutomationElement element)
    {
        Infrastructure.Wait.ForCondition(() => element.IsEnabled, $"elemento abilitato ({AutomationIdSafe(element)})", TimeSpan.FromSeconds(10));
    }

    public static void SetText(this AutomationElement element, string text)
    {
        var textBox = element.AsTextBox();
        textBox.Focus();
        textBox.Text = text;
    }


    /// <summary>Scrive una data nel testo di un CalendarDatePicker tramite ValuePattern: il controllo la interpreta subito,
    /// come una digitazione confermata. Nessuna coordinata: e' la stessa via del campo testo.</summary>
    public static void ImpostaTestoData(this AutomationElement picker, string testo) =>
        picker.Patterns.Value.Pattern.SetValue(testo);

    /// <summary>Digitazione reale nel campo di testo di un CalendarDatePicker: il campo interno (Edit) prende il fuoco,
    /// il contenuto si seleziona con Ctrl+A, si digitano i caratteri e si conferma con Invio (il commit della tastiera).</summary>
    public static void DigitaDataConTastiera(this AutomationElement picker, string testo)
    {
        var casella = picker.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))
            ?? throw new InvalidOperationException("campo di testo del CalendarDatePicker non trovato");
        casella.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(testo);
        Keyboard.Type(VirtualKeyShort.ENTER);
    }

    /// <summary>Apre il calendario di un CalendarDatePicker (ExpandCollapse) e sceglie il giorno indicato del mese mostrato.
    /// Ogni pulsante-giorno ha come nome il numero del giorno. Il calendario e' un popup: lo si cerca tra le finestre
    /// dell'app. Selezione per pattern (SelectionItem, Invoke di riserva), senza coordinate.</summary>
    public static void SceglieGiornoDalCalendario(this AutomationElement picker, TestLoomApplication app, int giorno)
    {
        picker.Patterns.ExpandCollapse.Pattern.Expand();
        var nome = giorno.ToString(CultureInfo.InvariantCulture);
        var finestraGiorno = app.WaitForWindow(finestra =>
                finestra.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(nome))) != null,
            TimeSpan.FromSeconds(10), $"finestra del calendario con il giorno {giorno}");
        var pulsante = finestraGiorno.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(nome)))
            ?? throw new InvalidOperationException($"pulsante del giorno {giorno} non trovato nel calendario");
        // SelectionItem prima: il pulsante del giorno imposta la data del Calendar, da cui il CalendarDatePicker si aggiorna
        // (un Invoke non passa da quel percorso di selezione).
        var selezione = pulsante.Patterns.SelectionItem.PatternOrDefault;
        if (selezione != null) selezione.Select();
        else pulsante.Patterns.Invoke.Pattern.Invoke();
    }

    /// <summary>Seleziona una voce di una ComboBox non editabile per il suo testo visibile esatto — accettabile
    /// qui perché il testo è un valore dati che il test stesso ha seminato (es. il nome di un componente/Operatore
    /// di fixture), non una stringa di interfaccia tradotta.</summary>
    public static void SelectByText(this ComboBox comboBox, string text)
    {
        comboBox.Expand();
        Infrastructure.Wait.ForCondition(() => comboBox.Items.Any(i => i.Text == text),
            $"voce ComboBox '{text}' disponibile", TimeSpan.FromSeconds(10));
        var item = comboBox.Items.First(i => i.Text == text);
        item.Select();
        comboBox.Collapse();
    }

    public static void SelectByText(this ListBox listBox, string text)
    {
        Infrastructure.Wait.ForCondition(() => listBox.Items.Any(i => i.Text == text),
            $"voce ListBox '{text}' disponibile", TimeSpan.FromSeconds(10));
        listBox.Items.First(i => i.Text == text).Select();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public static void PressKey(Window window, FlaUI.Core.WindowsAPI.VirtualKeyShort key)
    {
        var hwnd = window.FrameworkAutomationElement.NativeWindowHandle;
        if (hwnd != IntPtr.Zero)
        {
            try { SetForegroundWindow(hwnd); } catch { }
            Thread.Sleep(50);
        }

        try
        {
            Keyboard.Press(key);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            if (hwnd != IntPtr.Zero)
            {
                const uint WM_KEYDOWN = 0x0100;
                const uint WM_KEYUP = 0x0101;
                PostMessage(hwnd, WM_KEYDOWN, (IntPtr)(int)key, (IntPtr)0x000F0001);
                Thread.Sleep(20);
                PostMessage(hwnd, WM_KEYUP, (IntPtr)(int)key, unchecked((IntPtr)0xC00F0001));
            }
        }
    }
}
