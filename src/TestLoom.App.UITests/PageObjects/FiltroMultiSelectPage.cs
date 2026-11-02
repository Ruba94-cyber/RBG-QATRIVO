using System;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>Contenuto del flyout di FiltroMultiSelectVirtualizzato (condiviso da Home/Report/Anomalie-CR):
/// come ogni Avalonia Flyout, FlaUI lo vede come una finestra di primo livello separata (stesso principio di
/// AuditPage/ReportPage — vedi i loro commenti). Apparire' senza casella di ricerca sotto la soglia di 10
/// opzioni (FiltroMultiSelectFlyoutHelper.SogliaRicerca): FiltroMultiSelect_Ricerca_Input esiste solo sopra
/// soglia.</summary>
public sealed class FiltroMultiSelectPage
{
    private const string ListaId = "FiltroMultiSelect_Lista";
    private readonly TestLoomApplication _app;
    private readonly Window _window;

    public FiltroMultiSelectPage(TestLoomApplication app)
    {
        _app = app;
        _window = app.WaitForWindowByContent(ListaId);
    }

    public bool HaRicerca => ElementFinders.ExistsById(_window, "FiltroMultiSelect_Ricerca_Input");

    public void Cerca(string testo) =>
        ElementFinders.WaitTextBoxById(_window, "FiltroMultiSelect_Ricerca_Input").SetText(testo);

    public bool VoceVisibile(string etichetta) =>
        ElementFinders.ExistsById(_window, $"FiltroMultiSelect_Voce_{etichetta}");

    public void SelezionaVoce(string etichetta)
    {
        // La lista virtualizzata espone una ScrollBar UIA distinta dalla List. Invocare PageDown realizza
        // progressivamente le righe fuori schermo; il limite evita di restare bloccati se la lista non avanza.
        var id = $"FiltroMultiSelect_Voce_{etichetta}";
        for (var i = 0; i < 12 && !ElementFinders.ExistsById(_window, id); i++)
        {
            var pageDown = ElementFinders.TryFindById(_window, "PART_PageDownButton");
            var invoke = pageDown?.Patterns.Invoke.PatternOrDefault;
            if (invoke == null) break;
            invoke.Invoke();
        }
        var riga = ElementFinders.WaitById(_window, id);
        var checkbox = riga.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.CheckBox))
            ?? throw new InvalidOperationException($"Nessun CheckBox trovato nella voce '{etichetta}'.");
        checkbox.SafeInvoke();
    }

    public void SelezionaTutto() =>
        ElementFinders.WaitButtonById(_window, "FiltroMultiSelect_SelezionaTutto_Button").SafeInvoke();

    public void DeselezionaTutto() =>
        ElementFinders.WaitButtonById(_window, "FiltroMultiSelect_DeselezionaTutto_Button").SafeInvoke();

    /// <summary>Chiude il selettore con Escape dopo aver dato il focus alla voce selezionata. SafeInvoke usa i
    /// pattern UIA e non garantisce che il checkbox abbia il focus; inviare Escape dalla finestra principale senza
    /// questo passaggio può lasciare aperto il flyout.</summary>
    public void ChiudiConEscape(string etichettaSelezionata)
    {
        if (!_app.IsWindowClosedByContent(ListaId))
        {
            var riga = ElementFinders.WaitById(_window, $"FiltroMultiSelect_Voce_{etichettaSelezionata}");
            var checkbox = riga.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.CheckBox))
                ?? throw new InvalidOperationException($"Nessun CheckBox trovato nella voce '{etichettaSelezionata}'.");
            checkbox.Focus();
            Wait.ForCondition(
                () => checkbox.FrameworkAutomationElement.HasKeyboardFocus,
                $"focus UIA sul checkbox '{etichettaSelezionata}' prima di Escape",
                TimeSpan.FromSeconds(3));

            var hwnd = _window.FrameworkAutomationElement.NativeWindowHandle;
            if (hwnd == IntPtr.Zero || GetForegroundWindow() != hwnd)
                throw new InvalidOperationException("Il selettore multiplo non è la finestra TestLoom in primo piano dopo il focus UIA.");

            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        }

        Wait.ForCondition(() => _app.IsWindowClosedByContent(ListaId), "chiusura del selettore multiplo");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
