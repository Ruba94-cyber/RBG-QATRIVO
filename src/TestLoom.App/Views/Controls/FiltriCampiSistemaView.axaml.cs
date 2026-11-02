using System.Collections;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views.Controls;

/// <summary>Filtri dei campi di sistema (UsabileComeFiltro) di Home e Report in un viewport a altezza finita e virtualizzato
/// (vedi FiltriCampiSistemaView.axaml). Ogni riga apre il flyout di selezione multipla condiviso
/// (<see cref="FiltroMultiSelectFlyoutHelper"/>): il valore resta nel modello, non nei controlli riciclati.</summary>
public partial class FiltriCampiSistemaView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> FiltriProperty =
        AvaloniaProperty.Register<FiltriCampiSistemaView, IEnumerable?>(nameof(Filtri));

    static FiltriCampiSistemaView()
    {
        FiltriProperty.Changed.AddClassHandler<FiltriCampiSistemaView>((vista, e) => vista.Lista.ItemsSource = e.NewValue as IEnumerable);
    }

    public FiltriCampiSistemaView()
    {
        InitializeComponent();
        // Anche gestito dal pulsante della riga (handledEventsToo): il ListBox puo' gia' aver trattato Invio/Spazio come selezione.
        Lista.AddHandler(InputElement.KeyDownEvent, OnTastoRiga, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Collezione dei filtri da mostrare, per esempio <see cref="HomeViewModel.FiltriCampiCustomDashboard"/>. Una
    /// collezione osservabile tiene il viewport allineato alle aggiunte e alle rimozioni senza ricostruirlo.</summary>
    public IEnumerable? Filtri
    {
        get => GetValue(FiltriProperty);
        set => SetValue(FiltriProperty, value);
    }

    private void OnApriFiltro(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button bottone || bottone.Tag is not FiltroMultiSelect filtro) return;
        FiltroMultiSelectFlyoutHelper.Apri(bottone, filtro);
    }

    /// <summary>Tastiera: Invio o Spazio sulla riga a fuoco apre il filtro della riga. Le frecce spostano il fuoco fra le righe e il
    /// pulsante di ogni riga resta raggiungibile con Tab; senza questo la riga non si poteva aprire dalla tastiera.</summary>
    private void OnTastoRiga(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || e.Source is not ListBoxItem riga) return;
        if (riga.DataContext is not FiltroCampoCustom campo) return;
        var bottone = riga.GetVisualDescendants().OfType<Button>().FirstOrDefault();
        if (bottone == null) return;
        e.Handled = true;
        FiltroMultiSelectFlyoutHelper.Apri(bottone, campo.Filtro);
    }
}
