using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Card di un grafico (Home). L'immagine e' renderizzata alla dimensione della card (DIP x scala di
/// schermo), cosi' i caratteri restano leggibili; la bitmap condivisa della cache (dimensione di default) e' solo la
/// prima immagine, poi la card possiede la propria bitmap e la rilascia con <see cref="Dispose"/>. L'altezza iniziale
/// segue larghezza e viewport, mentre le dimensioni scelte dall'utente restano indipendenti.</summary>
public sealed class ChartRenderizzato : ViewModelBase, IDisposable
{
    /// <summary>Limiti delle dimensioni scelte dall'utente per la card (larghezza esterna e altezza del grafico).</summary>
    public const double LarghezzaCardMinima = 320;
    public const double LarghezzaCardMassima = 1600;
    public const double AltezzaCardMinima = 160;
    public const double AltezzaCardMassima = 900;

    /// <summary>Altezza massima della card in rapporto all'altezza visibile della pagina.</summary>
    public const double FrazioneAltezzaMassima = 0.45;

    private Bitmap? _immagine;
    private bool _immagineDellaCard;
    private double _larghezza;
    private double _altezzaVisibile = double.PositiveInfinity;
    private RenderGraficoAsincrono<Bitmap>? _render;
    private DimensioniGrafico? _ultimaRichiesta;
    private bool _disposto;
    private bool _nonDisponibile;
    private IReadOnlyList<VoceLegendaCompletaGrafico>? _legendaTortaCompleta;
    private double _larghezzaCard = double.NaN;
    private double _larghezzaCardAdattiva = double.NaN;
    private double _altezzaPersonalizzata = double.NaN;

    public string Id { get; init; } = "";
    public TipoGrafico Tipo { get; init; }
    public string Titolo { get; init; } = "";

    /// <summary>Dati snapshot del grafico: base di Apri, Esporta PNG e Copia (nessuna nuova query).</summary>
    public DatiGrafico? Dati { get; init; }

    /// <summary>Riepilogo dei filtri attivi al momento del calcolo (titolo della finestra ed export).</summary>
    public string Filtri { get; init; } = "";

    /// <summary>Registra un file esportato nella Cronologia export della pagina che ospita la card.</summary>
    public Action<string>? Registra { get; init; }

    private string? _messaggio;

    /// <summary>Esito dell'ultima esportazione o copia della card (mostrato sotto il grafico).</summary>
    public string? Messaggio
    {
        get => _messaggio;
        private set => SetProperty(ref _messaggio, value);
    }

    /// <summary>True quando l'ultimo rendering e' fallito (eccezione o PNG non decodificato): la card mostra
    /// "Grafico non disponibile" invece di un'immagine vuota. Torna false al prossimo rendering riuscito.</summary>
    public bool NonDisponibile
    {
        get => _nonDisponibile;
        private set => SetProperty(ref _nonDisponibile, value);
    }

    /// <summary>Pagina che ospita la card ("Home" o "Report"): prefisso degli AutomationId.</summary>
    public string Pagina { get; init; } = "Home";

    /// <summary>Immagine mostrata dalla card (bitmap della cache alla creazione, poi propria alla dimensione della card).</summary>
    public Bitmap? Immagine
    {
        get => _immagine;
        init => _immagine = value;
    }

    /// <summary>Esposti via UI Automation (AutomationId stabile + tipo corrente nel HelpText) per i test FlaUI.</summary>
    public string AutomationId => $"{Pagina}_Chart_{Id}";
    public string TipoTesto => Tipo.ToString();

    /// <summary>Nome breve del grafico per gli AutomationId dei pulsanti (Grafico_{Nome}_Apri_Button, ...).</summary>
    public string Nome => Id switch
    {
        HomeChartCatalog.IdCopertura => "Copertura",
        HomeChartCatalog.IdAndamento => "Andamento",
        HomeChartCatalog.IdDonut => "Esiti",
        _ => Id,
    };

    public string ApriAutomationId => $"{Pagina}_Grafico_{Nome}_Apri_Button";
    public string EsportaAutomationId => $"{Pagina}_Grafico_{Nome}_EsportaPng_Button";
    public string CopiaAutomationId => $"{Pagina}_Grafico_{Nome}_Copia_Button";

    /// <summary>Legenda completa scorrevole: la legenda raster della card può essere accorciata quando ci sono molti settori.</summary>
    public IReadOnlyList<VoceLegendaCompletaGrafico> LegendaTortaCompleta =>
        _legendaTortaCompleta ??= GraficoLegendaCompleta.Crea(Dati, Tipo);

    public bool HaLegendaTortaCompleta => LegendaTortaCompleta.Count > 0;

    /// <summary>Snapshot per la finestra grafico e l'export; null se la card non ha dati.</summary>
    public GraficoSnapshot? Snapshot => Dati == null ? null : new GraficoSnapshot(Id, Titolo, Dati, Tipo);

    /// <summary>Larghezza esterna scelta per la card; NaN mantiene la larghezza adattiva del contenitore.</summary>
    public double LarghezzaCard
    {
        get => _larghezzaCard;
        private set => SetProperty(ref _larghezzaCard, value);
    }

    /// <summary>Larghezza della card: scelta dall'utente oppure adattiva alla larghezza disponibile nella WrapPanel.</summary>
    public double LarghezzaCardVisualizzata => double.IsFinite(LarghezzaCard) ? LarghezzaCard : _larghezzaCardAdattiva;

    public double AltezzaGrafico => double.IsFinite(_altezzaPersonalizzata)
        ? _altezzaPersonalizzata
        : Math.Max(160, Math.Min(_larghezza > 0 ? _larghezza * 0.5 : 320, _altezzaVisibile * FrazioneAltezzaMassima));

    /// <summary>Imposta entrambe le dimensioni in modo indipendente. I resize UI passano sempre da questo metodo,
    /// così anche la larghezza inizialmente adattiva diventa esplicita al primo trascinamento.</summary>
    public void ImpostaDimensioniPersonalizzate(double larghezzaCard, double altezzaGrafico)
    {
        if (double.IsFinite(larghezzaCard))
        {
            var larghezza = Math.Clamp(larghezzaCard, LarghezzaCardMinima, LarghezzaCardMassima);
            if (!double.IsFinite(LarghezzaCard) || Math.Abs(LarghezzaCard - larghezza) >= 0.5)
            {
                LarghezzaCard = larghezza;
                OnPropertyChanged(nameof(LarghezzaCardVisualizzata));
            }
        }

        if (double.IsFinite(altezzaGrafico))
        {
            var altezza = Math.Clamp(altezzaGrafico, AltezzaCardMinima, AltezzaCardMassima);
            if (!double.IsFinite(_altezzaPersonalizzata) || Math.Abs(_altezzaPersonalizzata - altezza) >= 0.5)
            {
                _altezzaPersonalizzata = altezza;
                OnPropertyChanged(nameof(AltezzaGrafico));
            }
        }
    }

    /// <summary>Rimuove le dimensioni personalizzate e torna al layout adattivo della pagina.</summary>
    public void ReimpostaDimensioniPersonalizzate()
    {
        if (!double.IsNaN(LarghezzaCard))
        {
            LarghezzaCard = double.NaN;
            OnPropertyChanged(nameof(LarghezzaCardVisualizzata));
        }
        if (!double.IsNaN(_altezzaPersonalizzata))
        {
            _altezzaPersonalizzata = double.NaN;
            OnPropertyChanged(nameof(AltezzaGrafico));
        }
    }

    /// <summary>Aggiorna la larghezza di default quando cambia la larghezza disponibile; non sovrascrive scelte salvate.</summary>
    public void ImpostaLarghezzaCardAdattiva(double larghezza)
    {
        if (double.IsFinite(LarghezzaCard) || !double.IsFinite(larghezza) || larghezza <= 0 ||
            Math.Abs(_larghezzaCardAdattiva - larghezza) < 0.5) return;
        _larghezzaCardAdattiva = larghezza;
        OnPropertyChanged(nameof(LarghezzaCardVisualizzata));
    }

    public void ImpostaLarghezza(double larghezza)
    {
        if (larghezza <= 0 || Math.Abs(_larghezza - larghezza) < 0.5) return;
        _larghezza = larghezza;
        OnPropertyChanged(nameof(AltezzaGrafico));
    }

    public void ImpostaAltezzaVisibile(double altezza)
    {
        // Un'altezza nulla e' una misura transitoria del layout: si conserva l'ultima misura valida.
        if (altezza <= 0 || Math.Abs(_altezzaVisibile - altezza) < 0.5) return;
        _altezzaVisibile = altezza;
        OnPropertyChanged(nameof(AltezzaGrafico));
    }

    /// <summary>Rigenera l'immagine alla dimensione data (debounce, render fuori dal thread UI, applicazione sul thread UI).</summary>
    public void RichiediRender(double larghezza, double altezza, double scala)
    {
        if (_disposto || Dati == null || larghezza <= 0 || altezza <= 0) return;
        _render ??= new RenderGraficoAsincrono<Bitmap>(
            dimensioni => GraficoRenderer.Render(Dati, Tipo, Titolo, dimensioni),
            GraficoBitmap.Decodifica,
            AplicaImmagine,
            alFallimento: () => NonDisponibile = true);
        _ultimaRichiesta = new DimensioniGrafico(larghezza, altezza, scala);
        _render.Richiedi(_ultimaRichiesta.Value);
    }

    /// <summary>Dimensione dell'ultimo render applicato (per i test: indipendente dai pixel della bitmap).</summary>
    internal DimensioniGrafico? UltimaDimensione { get; private set; }

    /// <summary>PNG ad alta risoluzione della card (dimensione visualizzata, scala >= 2, almeno 1600 px), con titolo, filtri e data.</summary>
    public Task<byte[]> PngAsync()
    {
        var snapshot = Snapshot ?? throw new InvalidOperationException("Il grafico non ha dati da esportare.");
        var larghezza = _larghezza;
        var altezza = AltezzaGrafico;
        var filtri = Filtri;
        var quando = DateTime.Now;
        return Task.Run(() => GraficoRenderer.EsportaPng(snapshot, larghezza, altezza, filtri, quando));
    }

    /// <summary>Scrive il PNG della card nel percorso scelto (atomico) e lo registra nella Cronologia export.</summary>
    public async Task<bool> SalvaPngAsync(string percorso)
    {
        try
        {
            var png = await PngAsync();
            await Task.Run(() => EsportazioneFile.ScriviBytesAtomico(percorso, png));
            Registra?.Invoke(percorso);
            Messaggio = string.Format(LocalizationService.Instance.Traduci("GraficoPngSalvatoFmt"), Path.GetFileName(percorso));
            return true;
        }
        catch (Exception ex)
        {
            Messaggio = string.Format(LocalizationService.Instance.Traduci("GraficoPngNonRiuscitoFmt"), ex.Message);
            CrashLogService.Registra("Esportazione PNG grafico", ex);
            return false;
        }
    }

    /// <summary>Copia il PNG della card negli appunti tramite l'azione della vista (che ha accesso al sistema di appunti).</summary>
    public async Task<bool> CopiaAppuntiAsync(Func<byte[], Task> copia)
    {
        try
        {
            var png = await PngAsync();
            await copia(png);
            Messaggio = LocalizationService.Instance.Traduci("GraficoCopiatoNegliAppunti");
            return true;
        }
        catch (Exception ex)
        {
            Messaggio = string.Format(LocalizationService.Instance.Traduci("GraficoCopiaNonRiuscitaFmt"), ex.Message);
            CrashLogService.Registra("Copia grafico negli appunti", ex);
            return false;
        }
    }

    private void AplicaImmagine(Bitmap nuova)
    {
        if (_disposto)
        {
            nuova.Dispose();
            return;
        }
        var vecchia = _immagine;
        var eraDellaCard = _immagineDellaCard;
        _immagine = nuova;
        _immagineDellaCard = true;
        UltimaDimensione = _ultimaRichiesta;
        NonDisponibile = false;
        OnPropertyChanged(nameof(Immagine));
        if (eraDellaCard) vecchia?.Dispose();
    }

    public void Dispose()
    {
        if (_disposto) return;
        _disposto = true;
        _render?.Dispose();
        if (_immagineDellaCard) _immagine?.Dispose();
        _immagine = null;
        _immagineDellaCard = false;
    }
}
