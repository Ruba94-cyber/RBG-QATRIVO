using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using System.Globalization;

namespace TestLoom.App.Views;

/// <summary>Card di un grafico (Home): immagine alla dimensione reale della card, pulsanti Apri, Esporta PNG e Copia.
/// Il DataContext e' il <see cref="ChartRenderizzato"/> della card.</summary>
public partial class GraficoCard : UserControl
{
    private ChartRenderizzato? _preferenzeCaricatePer;
    private Point _origineRidimensionamento;
    private double _larghezzaInizialeRidimensionamento;
    private double _altezzaInizialeRidimensionamento;
    private bool _ridimensionaLarghezza;
    private bool _ridimensionaAltezza;
    private WrapPanel? _pannelloGrafici;

    public GraficoCard()
    {
        InitializeComponent();
        // Render iniziale dopo il caricamento (layout gia' eseguito), poi il ridisegno segue Bounds dell'immagine:
        // SizeChanged non scatta sempre, per esempio al primo layout.
        Loaded += (_, _) =>
        {
            CaricaDimensioniSalvate();
            AggiornaLarghezzaDisponibile();
            AggiornaDaContenitore();
            AggiornaAppenaLayoutPronto();
        };
        // L'immagine occupa la cella disponibile nella card; il suo Bounds resta la misura autorevole per il render,
        // anche quando il grafico riceve una larghezza personalizzata.
        ActualThemeVariantChanged += (_, _) => AggiornaDimensione(ImmagineCard.Bounds.Size);
        Contenitore.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty) AggiornaDaContenitore();
        };
        ImmagineCard.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty) AggiornaDimensione(ImmagineCard.Bounds.Size);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _preferenzeCaricatePer = null;
        if (IsLoaded) CaricaDimensioniSalvate();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        CollegaPannelloGrafici();
        AggiornaLarghezzaDisponibile();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_pannelloGrafici != null) _pannelloGrafici.SizeChanged -= OnPannelloGraficiSizeChanged;
        _pannelloGrafici = null;
        _preferenzeCaricatePer = null;
        Card?.ReimpostaDimensioniPersonalizzate();
    }

    /// <summary>Larghezza utile della card (contenitore meno padding) e relativa altezza: richiede il render alla dimensione.</summary>
    private void AggiornaDaContenitore()
    {
        var card = Card;
        if (card == null) return;
        var larghezza = ImmagineCard.Bounds.Width;
        if (larghezza <= 0) return;
        card.ImpostaLarghezza(larghezza);
        AggiornaDimensione(new Size(larghezza, card.AltezzaGrafico));
    }

    private ChartRenderizzato? Card => DataContext as ChartRenderizzato;

    private void OnImmagineSizeChanged(object? sender, SizeChangedEventArgs e) => AggiornaDimensione(e.NewSize);

    /// <summary>Render iniziale: il caricamento puo' precedere il layout, quindi si riprova finche' l'immagine non ha una dimensione.</summary>
    private void AggiornaAppenaLayoutPronto(int tentativiRimasti = 100)
    {
        var dimensione = ImmagineCard.Bounds.Size;
        if (dimensione.Width > 0 && dimensione.Height > 0)
        {
            AggiornaDimensione(dimensione);
            return;
        }
        if (tentativiRimasti > 0)
            Dispatcher.UIThread.Post(() => AggiornaAppenaLayoutPronto(tentativiRimasti - 1));
    }

    /// <summary>Dimensione reale dell'immagine: la card ricalcola il grafico (debounce nel ViewModel).</summary>
    private void AggiornaDimensione(Size dimensione)
    {
        var card = Card;
        if (card == null || dimensione.Width <= 0 || dimensione.Height <= 0) return;
        card.ImpostaLarghezza(dimensione.Width);
        card.ImpostaTemaScuro(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
        card.RichiediRender(dimensione.Width, dimensione.Height, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
    }

    private void CollegaPannelloGrafici()
    {
        var pannello = this.GetVisualAncestors().OfType<WrapPanel>().FirstOrDefault();
        if (ReferenceEquals(pannello, _pannelloGrafici)) return;
        if (_pannelloGrafici != null) _pannelloGrafici.SizeChanged -= OnPannelloGraficiSizeChanged;
        _pannelloGrafici = pannello;
        if (_pannelloGrafici != null) _pannelloGrafici.SizeChanged += OnPannelloGraficiSizeChanged;
    }

    private void OnPannelloGraficiSizeChanged(object? sender, SizeChangedEventArgs e) => AggiornaLarghezzaDisponibile();

    private void AggiornaLarghezzaDisponibile(int tentativiRimasti = 10)
    {
        var pannello = _pannelloGrafici;
        var larghezzaDisponibile = pannello?.Bounds.Width ?? Bounds.Width;
        if (larghezzaDisponibile <= 0)
        {
            if (tentativiRimasti > 0)
                Dispatcher.UIThread.Post(() => AggiornaLarghezzaDisponibile(tentativiRimasti - 1));
            return;
        }

        var larghezzaCard = Math.Max(180, larghezzaDisponibile - Contenitore.Margin.Left - Contenitore.Margin.Right);
        Contenitore.MaxWidth = larghezzaCard;
        Card?.ImpostaLarghezzaCardAdattiva(larghezzaCard);
    }

    private void OnRidimensionamentoPremuto(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control maniglia || Card is not { } card ||
            !e.GetCurrentPoint(maniglia).Properties.IsLeftButtonPressed) return;

        var direzione = maniglia.Tag as string;
        _ridimensionaLarghezza = direzione is "larghezza" or "entrambe";
        _ridimensionaAltezza = direzione is "altezza" or "entrambe";
        _origineRidimensionamento = e.GetPosition(Contenitore);
        _larghezzaInizialeRidimensionamento = Contenitore.Bounds.Width;
        _altezzaInizialeRidimensionamento = card.AltezzaGrafico;
        e.Pointer.Capture(maniglia);
        e.Handled = true;
    }

    private void OnRidimensionamentoSpostato(object? sender, PointerEventArgs e)
    {
        if ((!_ridimensionaLarghezza && !_ridimensionaAltezza) || Card is not { } card) return;
        var posizione = e.GetPosition(Contenitore);
        var larghezza = _ridimensionaLarghezza
            ? _larghezzaInizialeRidimensionamento + posizione.X - _origineRidimensionamento.X
            : _larghezzaInizialeRidimensionamento;
        var altezza = _ridimensionaAltezza
            ? _altezzaInizialeRidimensionamento + posizione.Y - _origineRidimensionamento.Y
            : _altezzaInizialeRidimensionamento;
        ApplicaDimensioniRidimensionate(larghezza, altezza);
        e.Handled = true;
    }

    private void OnRidimensionamentoRilasciato(object? sender, PointerReleasedEventArgs e)
    {
        if (!_ridimensionaLarghezza && !_ridimensionaAltezza) return;
        _ridimensionaLarghezza = false;
        _ridimensionaAltezza = false;
        e.Pointer.Capture(null);
        SalvaDimensioniPersonalizzate();
        e.Handled = true;
    }

    private void OnRidimensionamentoCapturePerso(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_ridimensionaLarghezza && !_ridimensionaAltezza) return;
        _ridimensionaLarghezza = false;
        _ridimensionaAltezza = false;
        SalvaDimensioniPersonalizzate();
    }

    private void OnRipristinaDimensioni(object? sender, RoutedEventArgs e)
    {
        Card?.ReimpostaDimensioniPersonalizzate();
        try
        {
            ViewStateService.ScriviPerOperatore(ChiaveDimensioniSalvate(Card), null);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Ripristino dimensioni grafico Home", ex);
        }
    }

    /// <summary>Applica il risultato di un trascinamento; il render viene richiesto dalle dimensioni effettive
    /// dell'immagine dopo il layout, mantenendo il debounce asincrono esistente.</summary>
    internal void ApplicaDimensioniRidimensionate(double larghezza, double altezza)
    {
        var limiteContenitore = _pannelloGrafici?.Bounds.Width ?? Bounds.Width;
        var larghezzaMassima = Math.Min(ChartRenderizzato.LarghezzaCardMassima,
            limiteContenitore > 0 ? Math.Max(ChartRenderizzato.LarghezzaCardMinima,
                limiteContenitore - Contenitore.Margin.Left - Contenitore.Margin.Right) : ChartRenderizzato.LarghezzaCardMassima);
        Card?.ImpostaDimensioniPersonalizzate(Math.Min(larghezza, larghezzaMassima), altezza);
    }

    /// <summary>Carica una volta le dimensioni associate all'operatore e all'ID stabile del grafico.</summary>
    internal void CaricaDimensioniSalvate()
    {
        var card = Card;
        if (card == null || ReferenceEquals(_preferenzeCaricatePer, card)) return;
        _preferenzeCaricatePer = card;
        var testo = ViewStateService.LeggiPerOperatore(ChiaveDimensioniSalvate(card));
        if (string.IsNullOrWhiteSpace(testo)) return;
        var parti = testo.Split(';');
        if (parti.Length != 2 ||
            !double.TryParse(parti[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var larghezza) ||
            !double.TryParse(parti[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var altezza) ||
            !double.IsFinite(larghezza) || !double.IsFinite(altezza)) return;
        card.ImpostaDimensioniPersonalizzate(larghezza, altezza);
    }

    /// <summary>Persiste solo alla fine del trascinamento, evitando scritture su disco durante ogni movimento del mouse.</summary>
    internal void SalvaDimensioniPersonalizzate()
    {
        var card = Card;
        if (card == null || Contenitore.Bounds.Width <= 0 || !double.IsFinite(card.AltezzaGrafico)) return;
        var larghezza = double.IsFinite(card.LarghezzaCard) ? card.LarghezzaCard : Contenitore.Bounds.Width;
        card.ImpostaDimensioniPersonalizzate(larghezza, card.AltezzaGrafico);
        var testo = string.Concat(card.LarghezzaCard.ToString("0.##", CultureInfo.InvariantCulture), ";",
            card.AltezzaGrafico.ToString("0.##", CultureInfo.InvariantCulture));
        try
        {
            ViewStateService.ScriviPerOperatore(ChiaveDimensioniSalvate(card), testo);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Salvataggio dimensioni grafico Home", ex);
        }
    }

    internal static string ChiaveDimensioniSalvate(ChartRenderizzato? card) =>
        $"Home.Grafici.Card.{Uri.EscapeDataString(card?.Id ?? string.Empty)}.Dimensioni";

    private void OnApri(object? sender, RoutedEventArgs e)
    {
        var card = Card;
        if (card?.Snapshot is not { } snapshot) return;
        GraficoAzioni.ApriFinestra(TopLevel.GetTopLevel(this) as Window, snapshot, card.Filtri, card.Registra);
    }

    private async void OnEsportaPng(object? sender, RoutedEventArgs e)
    {
        var card = Card;
        if (card == null || TopLevel.GetTopLevel(this) is not { } finestra) return;
        try
        {
            var percorso = await GraficoAzioni.ScegliPercorsoPngAsync(finestra,
                LocalizationService.Instance.Traduci("GraficoSalvaPngTitolo"),
                GraficoRenderer.NomeFileProposto(card.Titolo, DateTime.Now));
            if (!string.IsNullOrEmpty(percorso)) await card.SalvaPngAsync(percorso);
        }
        catch (Exception ex) { CrashLogService.Registra("Scelta percorso PNG grafico", ex); }
    }

    private async void OnCopia(object? sender, RoutedEventArgs e)
    {
        var card = Card;
        if (card == null || TopLevel.GetTopLevel(this) is not { } finestra) return;
        await card.CopiaAppuntiAsync(png => GraficoAzioni.CopiaPngAsync(finestra, png));
    }
}
