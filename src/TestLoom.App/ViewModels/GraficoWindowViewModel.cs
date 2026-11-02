using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Finestra "Apri" di un grafico: mostra lo stesso snapshot (dati e filtri della pagina al momento dell'apertura)
/// ridisegnato alla dimensione della finestra. Il resize e' debounced; il render avviene fuori dal thread UI.</summary>
public sealed class GraficoWindowViewModel : ViewModelBase, IDisposable
{
    private readonly GraficoSnapshot _snapshot;
    private readonly string _filtri;
    private Bitmap? _immagine;
    private RenderGraficoAsincrono<Bitmap>? _render;
    private double _larghezza;
    private double _altezza;
    private bool _disposto;
    private DimensioniGrafico? _ultimaRichiesta;
    private bool _nonDisponibile;
    private IReadOnlyList<VoceLegendaCompletaGrafico>? _legendaTortaCompleta;

    public GraficoWindowViewModel(GraficoSnapshot snapshot, string filtri)
    {
        _snapshot = snapshot;
        _filtri = filtri;
    }

    public GraficoSnapshot Snapshot => _snapshot;
    public string Filtri => _filtri;
    public IReadOnlyList<VoceLegendaCompletaGrafico> LegendaTortaCompleta =>
        _legendaTortaCompleta ??= GraficoLegendaCompleta.Crea(_snapshot.Dati, _snapshot.Tipo);
    public bool HaLegendaTortaCompleta => LegendaTortaCompleta.Count > 0;

    /// <summary>Titolo della finestra: titolo del grafico seguito dai filtri attivi.</summary>
    public string Titolo => string.IsNullOrWhiteSpace(_filtri) ? _snapshot.Titolo : $"{_snapshot.Titolo} — {_filtri}";

    public Bitmap? Immagine
    {
        get => _immagine;
        private set => SetProperty(ref _immagine, value);
    }

    /// <summary>True quando l'ultimo rendering e' fallito (eccezione o PNG non decodificato): la finestra mostra
    /// "Grafico non disponibile" al posto dell'area vuota. Torna false al prossimo rendering riuscito.</summary>
    public bool NonDisponibile
    {
        get => _nonDisponibile;
        private set => SetProperty(ref _nonDisponibile, value);
    }

    /// <summary>Dimensione dell'area grafico (DIP) dall'ultimo resize: base dell'export PNG della finestra.</summary>
    public double LarghezzaArea => _larghezza;
    public double AltezzaArea => _altezza;

    public void RichiediRender(double larghezza, double altezza, double scala)
    {
        if (_disposto || larghezza <= 0 || altezza <= 0) return;
        _larghezza = larghezza;
        _altezza = altezza;
        _render ??= new RenderGraficoAsincrono<Bitmap>(
            dimensioni => GraficoRenderer.Render(_snapshot.Dati, _snapshot.Tipo, _snapshot.Titolo, dimensioni),
            GraficoBitmap.Decodifica,
            nuova =>
            {
                if (_disposto)
                {
                    nuova.Dispose();
                    return;
                }
                var vecchia = Immagine;
                Immagine = nuova;
                NonDisponibile = false;
                UltimaDimensione = _ultimaRichiesta;
                vecchia?.Dispose();
            },
            alFallimento: () =>
            {
                if (!_disposto) NonDisponibile = true;
            });
        _ultimaRichiesta = new DimensioniGrafico(larghezza, altezza, scala);
        _render.Richiedi(_ultimaRichiesta.Value);
    }

    /// <summary>Dimensione dell'ultimo render applicato (per i test: indipendente dai pixel della bitmap).</summary>
    internal DimensioniGrafico? UltimaDimensione { get; private set; }

    /// <summary>PNG ad alta risoluzione dell'area corrente, con titolo, filtri e data nell'immagine.</summary>
    public byte[] CreaPngEsportazione(DateTime quando) =>
        GraficoRenderer.EsportaPng(_snapshot, _larghezza > 0 ? _larghezza : 800, _altezza > 0 ? _altezza : 480, _filtri, quando);

    public void Dispose()
    {
        if (_disposto) return;
        _disposto = true;
        _render?.Dispose();
        Immagine?.Dispose();
        Immagine = null;
    }
}
