using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using TestLoom.App.ViewModels;
using TestLoom.App.Views.Controls;

namespace TestLoom.App.Views;

/// <summary>Flyout di selezione multipla per un FiltroMultiSelect (Addendum §9, feedback QA), condiviso tra
/// Home, Report (Addendum redesign 2026-08: il filtro per template del Report riusa lo stesso controllo) e
/// Anomalie/CR (NonConformitaView). Stesso schema collaudato di OperatoriView.OnScegliGamme
/// (Placement.BottomEdgeAlignedLeft, ShowAt sincrono).
///
/// Addendum performance 2026-09-25: il contenuto del flyout e' costruito da
/// <see cref="FiltroMultiSelectVirtualizzato"/> (ListBox virtualizzata) invece che da uno StackPanel con un
/// CheckBox per ogni opzione — con cataloghi fino a ~1000 voci evita di creare 1000 controlli ad ogni apertura.</summary>
public static class FiltroMultiSelectFlyoutHelper
{
    /// <summary>Soglia oltre la quale compare una casella di ricerca sopra l'elenco (feedback utente
    /// 2026-08-21: la regola combobox-pura/pulsante-con-ricerca deve valere per qualunque lista dell'app, non
    /// solo per gli 8 campi Lista degli strumenti). Stessa soglia di ListaOltreSogliaConverter.</summary>
    public const int SogliaRicerca = 10;

    public static void Apri(Button button, FiltroMultiSelect filtro)
    {
        var content = FiltroMultiSelectVirtualizzato.Costruisci(filtro, () => button.Content = filtro.TestoRiepilogo, SogliaRicerca);
        var flyout = new Flyout { Content = content, Placement = PlacementMode.BottomEdgeAlignedLeft };
        FlyoutBase.SetAttachedFlyout(button, flyout);
        flyout.ShowAt(button);
    }
}
