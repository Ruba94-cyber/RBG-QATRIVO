using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

/// <summary>Finestra "Apri" di un grafico: ridimensionabile e massimizzabile, registrata in <see cref="AdattamentoDialogo"/>,
/// con sfondo opaco (token WindowBackgroundBrush). Il grafico si ridisegna alla dimensione dell'area della finestra; Esc la chiude.</summary>
public partial class GraficoWindow : Window
{
    private readonly GraficoWindowViewModel _vm;
    private readonly Action<string>? _registraEsportazione;

    /// <summary>Solo per il designer Avalonia (AVLN3001): grafico segnaposto vuoto. L'app apre sempre la finestra con lo
    /// snapshot del grafico reale (GraficoAzioni), quindi questo costruttore non viene usato a runtime.</summary>
    public GraficoWindow()
        : this(new GraficoWindowViewModel(new GraficoSnapshot("segnaposto", string.Empty, new DatiCopertura(0, 0, 0, 0), TipoGrafico.BarreVerticali), string.Empty), null)
    {
    }

    public GraficoWindow(GraficoWindowViewModel vm, Action<string>? registraEsportazione)
    {
        InitializeComponent();
        _vm = vm;
        _registraEsportazione = registraEsportazione;
        DataContext = vm;
        AdattamentoDialogo.Registra(this);
        KeyDown += OnTastoPremuto;
        Closed += (_, _) => _vm.Dispose();
        // La dimensione viene dal contenitore (Border): l'Image senza sorgente vale 0x0, quindi le sue Bounds non possono
        // far partire il primo render. Render iniziale quando la finestra e' aperta; poi ogni cambio di area lo richiede.
        Opened += (_, _) => RidisegnaAlContenitore();
        ActualThemeVariantChanged += (_, _) => RidisegnaAlContenitore();
        Contenitore.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty) RidisegnaAlContenitore();
        };
    }

    /// <summary>Area utile del grafico (contenitore meno padding): il render segue la dimensione della finestra.</summary>
    private void RidisegnaAlContenitore()
    {
        var padding = Contenitore.Padding;
        var larghezza = Contenitore.Bounds.Width - padding.Left - padding.Right;
        var altezza = Contenitore.Bounds.Height - padding.Top - padding.Bottom;
        if (larghezza <= 0 || altezza <= 0) return;
        _vm.ImpostaTemaScuro(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
        _vm.RichiediRender(larghezza, altezza, RenderScaling);
    }

    private void OnTastoPremuto(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }

    private async void OnEsportaPng(object? sender, RoutedEventArgs e)
    {
        try
        {
            var percorso = await GraficoAzioni.ScegliPercorsoPngAsync(this,
                LocalizationService.Instance.Traduci("GraficoSalvaPngTitolo"),
                GraficoRenderer.NomeFileProposto(_vm.Snapshot.Titolo, DateTime.Now));
            if (string.IsNullOrEmpty(percorso)) return;
            var quando = DateTime.Now;
            var png = await Task.Run(() => _vm.CreaPngEsportazione(quando));
            await Task.Run(() => EsportazioneFile.ScriviBytesAtomico(percorso, png));
            _registraEsportazione?.Invoke(percorso);
        }
        catch (Exception ex) { CrashLogService.Registra("Esportazione PNG grafico (finestra)", ex); }
    }

    private async void OnCopia(object? sender, RoutedEventArgs e)
    {
        try
        {
            var quando = DateTime.Now;
            var png = await Task.Run(() => _vm.CreaPngEsportazione(quando));
            await GraficoAzioni.CopiaPngAsync(this, png);
        }
        catch (Exception ex) { CrashLogService.Registra("Copia grafico negli appunti (finestra)", ex); }
    }
}
