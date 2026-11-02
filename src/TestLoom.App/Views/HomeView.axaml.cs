using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using System.Globalization;

namespace TestLoom.App.Views;

public partial class HomeView : UserControl
{
    private const string ChiaveLarghezzaPannelloGrafici = "Home.Grafici.PannelloOpzioniLarghezza";
    private const double LarghezzaPannelloGraficiPredefinita = 190;
    private const double LarghezzaPannelloGraficiMinima = 150;
    private const double LarghezzaPannelloGraficiMassima = 440;
    private HomeViewModel? _vm;

    public HomeView()
    {
        InitializeComponent();
        // L'altezza della pagina (viewport dello ScrollViewer) limita l'altezza delle card dei grafici.
        SizeChanged += (_, e) => _vm?.ImpostaAltezzaVisibile(e.NewSize.Height);
        Loaded += (_, _) => ApplicaLarghezzaPannelloGraficiSalvata();
        SplitterPannelloGrafici.DragCompleted += (_, _) => SalvaLarghezzaPannelloGrafici();
        SplitterPannelloGrafici.DoubleTapped += (_, _) => RipristinaLarghezzaPannelloGrafici();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Collega(DataContext as HomeViewModel);
    }

    // Il ViewModel della Home sopravvive alle visite (cache di MainViewModel) mentre la vista viene ricreata: la
    // sottoscrizione al riordino segue il ciclo di vita della vista nell'albero visuale, altrimenti ogni visita
    // lascerebbe agganciata al ViewModel una vista ormai staccata.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Collega(DataContext as HomeViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Collega(null);
    }

    private void Collega(HomeViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        if (_vm != null) _vm.LayoutCambiato -= ApplicaOrdineSezioni;
        _vm = vm;
        if (_vm == null) return;
        _vm.LayoutCambiato += ApplicaOrdineSezioni;
        ApplicaOrdineSezioni();
    }

    /// <summary>
    /// Riordina i contenitori delle sezioni secondo l'ordine del ViewModel. Visibilita' e contenuto sono legati
    /// direttamente nello XAML: qui conta solo la posizione. Le sezioni si portano nella posizione finale da
    /// sinistra a destra, quindi una posizione sistemata non viene mai rimescolata.
    /// </summary>
    private void ApplicaOrdineSezioni()
    {
        if (_vm == null || this.FindControl<StackPanel>("SezioniHome") is not { } contenitore) return;
        for (var posizione = 0; posizione < _vm.OrdineSezioni.Count; posizione++)
        {
            var automationId = HomeSezioni.AutomationId(_vm.OrdineSezioni[posizione]);
            var sezione = contenitore.Children.FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == automationId);
            if (sezione == null) continue;
            var attuale = contenitore.Children.IndexOf(sezione);
            if (attuale != posizione) contenitore.Children.Move(attuale, posizione);
        }
    }

    private void OnApriFiltroMultiSelect(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not FiltroMultiSelect filtro) return;
        FiltroMultiSelectFlyoutHelper.Apri(button, filtro);
    }

    private void ApplicaLarghezzaPannelloGraficiSalvata()
    {
        if (GrigliaPannelloGrafici.ColumnDefinitions.Count < 3) return;
        var testo = ViewStateService.LeggiPerOperatore(ChiaveLarghezzaPannelloGrafici);
        if (!double.TryParse(testo, NumberStyles.Float, CultureInfo.InvariantCulture, out var larghezza))
            larghezza = LarghezzaPannelloGraficiPredefinita;
        GrigliaPannelloGrafici.ColumnDefinitions[2].Width = new GridLength(
            Math.Clamp(larghezza, LarghezzaPannelloGraficiMinima, LarghezzaPannelloGraficiMassima));
    }

    private void SalvaLarghezzaPannelloGrafici()
    {
        if (GrigliaPannelloGrafici.ColumnDefinitions.Count < 3) return;
        var larghezza = GrigliaPannelloGrafici.ColumnDefinitions[2].ActualWidth;
        if (larghezza <= 0) return;
        ScriviLarghezzaPannelloGrafici(Math.Clamp(larghezza, LarghezzaPannelloGraficiMinima, LarghezzaPannelloGraficiMassima));
    }

    private void RipristinaLarghezzaPannelloGrafici()
    {
        if (GrigliaPannelloGrafici.ColumnDefinitions.Count < 3) return;
        GrigliaPannelloGrafici.ColumnDefinitions[2].Width = new GridLength(LarghezzaPannelloGraficiPredefinita);
        ScriviLarghezzaPannelloGrafici(LarghezzaPannelloGraficiPredefinita);
    }

    private static void ScriviLarghezzaPannelloGrafici(double larghezza)
    {
        try
        {
            ViewStateService.ScriviPerOperatore(ChiaveLarghezzaPannelloGrafici,
                larghezza.ToString("0.##", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Salvataggio larghezza pannello grafici Home", ex);
        }
    }
}
