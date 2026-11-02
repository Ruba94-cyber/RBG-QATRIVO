using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace TestLoom.App.Services;

/// <summary>Apre la lista completa di un AutoCompleteBox al primo focus/click, non solo quando si comincia a
/// scrivere (feedback utente 2026-08-20, punti 6/13: "premendo la prima volta su Funzionalità non appare
/// subito il menu a tendina ma devo prima scrivere qualcosa o cancellare"). Anche con MinimumPrefixLength="0"
/// e un filtro che su testo vuoto non esclude nulla (vedi AutoCompleteFilters), Avalonia non apre da sé il
/// popup al semplice focus — lo apre solo quando il testo cambia. Applicato globalmente via Styles.axaml
/// (Style Selector="AutoCompleteBox"), non serve toccare ogni singola view: ogni menu a tendina dell'app
/// mantiene comunque la ricerca digitando, mostra solo anche la lista intera appena ci si clicca dentro senza
/// aver ancora scritto nulla.</summary>
public static class AutoCompleteBoxBehavior
{
    public static readonly AttachedProperty<bool> ApriAlFocusProperty =
        AvaloniaProperty.RegisterAttached<AutoCompleteBox, bool>("ApriAlFocus", typeof(AutoCompleteBoxBehavior));

    static AutoCompleteBoxBehavior()
    {
        ApriAlFocusProperty.Changed.AddClassHandler<AutoCompleteBox>((box, e) =>
        {
            box.GotFocus -= OnGotFocus;
            if (e.NewValue is true) box.GotFocus += OnGotFocus;
        });
    }

    /// <summary>Impostare IsDropDownOpen=true da fuori il controllo, come qui, non attiva in modo affidabile
    /// il popolamento interno della lista filtrata di AutoCompleteBox — bug noto di Avalonia (upstream, non di
    /// questa app: AvaloniaUI/Avalonia#8903 e correlati), per cui il popup può aprirsi vuoto/non aggiornato e la
    /// selezione risultare poi inaffidabile. Il popolamento è normalmente legato ai cambi di Text, quindi un
    /// piccolo giro Text→(valore)→Text originale forza la stessa strada che imboccherebbe l'utente scrivendo,
    /// prima di aprire il popup — senza toccare API interne/riflessione.</summary>
    private static void OnGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is AutoCompleteBox { IsDropDownOpen: false } box && string.IsNullOrEmpty(box.Text))
        {
            var testoOriginale = box.Text;
            box.Text = " ";
            box.Text = testoOriginale;
            box.IsDropDownOpen = true;
        }
    }

    public static void SetApriAlFocus(AutoCompleteBox box, bool value) => box.SetValue(ApriAlFocusProperty, value);
    public static bool GetApriAlFocus(AutoCompleteBox box) => box.GetValue(ApriAlFocusProperty);
}
