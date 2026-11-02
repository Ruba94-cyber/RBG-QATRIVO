using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace TestLoom.App.Services;

/// <summary>Ricerca di un AutoCompleteBox con FilterMode=None (ReportView: componente e veicoli, elenchi che possono superare
/// le migliaia di voci). Il menu a tendina mostra l'elenco calcolato dal motore: PickerMotore cerca e il controller
/// scarta i risultati superati. Su Invio e frecce, prima che il controllo agisca, Allinea calcola subito il risultato
/// per il testo corrente (stessa regola di SelezionaDialog.AllineaRicercaPrimaDellAzione).</summary>
public sealed class AutoCompleteRicerca<T> : IDisposable where T : class
{
    private readonly PickerMotore<T> _motore;
    private readonly PickerRicercaController<T> _controller;
    private readonly Action<IReadOnlyList<T>> _applica;
    private string? _testoApplicato;

    public AutoCompleteRicerca(
        IEnumerable<T> elementi,
        Func<T, string> testoDiRicerca,
        Action<IReadOnlyList<T>> applica,
        int debounceMs = 0,
        Func<int, CancellationToken, Task>? attesa = null,
        Action<Action>? postaSulThreadUi = null)
    {
        _motore = new PickerMotore<T>(elementi, testoDiRicerca);
        _applica = applica;
        _controller = new PickerRicercaController<T>(_motore, Applica, debounceMs, attesa, postaSulThreadUi);
        _ = _motore.PreparaAsync();
    }

    /// <summary>Come sopra, ma sul motore gia' costruito da chi lo possiede (es. PickerTestDisponibili del ViewModel): le chiavi
    /// di ricerca non vengono preparate una seconda volta. Chi passa il motore ne cura la preparazione.</summary>
    public AutoCompleteRicerca(
        PickerMotore<T> motore,
        Action<IReadOnlyList<T>> applica,
        int debounceMs = 0,
        Func<int, CancellationToken, Task>? attesa = null,
        Action<Action>? postaSulThreadUi = null)
    {
        _motore = motore;
        _applica = applica;
        _controller = new PickerRicercaController<T>(_motore, Applica, debounceMs, attesa, postaSulThreadUi);
    }

    /// <summary>Ultima ricerca avviata: completa quando il suo risultato e' stato applicato o scartato (per i test).</summary>
    internal Task UltimaRicerca => _controller.UltimaRicerca;

    /// <summary>Chiamare dal thread UI a ogni variazione del testo dell'AutoCompleteBox.</summary>
    public void Digita(string? testo) => _controller.Digita(testo);

    /// <summary>Chiamare dal thread UI prima di Invio o frecce: se la ricerca e' ancora in attesa, annulla quella
    /// precedente e applica subito il risultato per il testo corrente, cosi' la scelta non avviene su un elenco superato.</summary>
    public void Allinea(string? testo)
    {
        var testoCorrente = testo ?? string.Empty;
        if (testoCorrente == _testoApplicato) return;
        _controller.Annulla();
        Applica(_motore.Cerca(testoCorrente));
    }

    private void Applica(PickerRisultato<T> risultato)
    {
        _testoApplicato = risultato.Query;
        _applica(risultato.Voci);
    }

    public void Dispose() => _controller.Dispose();
}

/// <summary>Collega un AutoCompleteBox con FilterMode=None al motore di ricerca: il menu a tendina riceve l'elenco
/// calcolato da <see cref="AutoCompleteRicerca{T}"/> tramite ItemsSource + PopulateComplete, come documentato da
/// Avalonia per la popolazione in più passi. Il filtro interno viene annullato (Populating.Cancel). La sorgente si
/// rilegge a ogni digitazione: se la lista cambia istanza (Aggiorna del report) il motore viene ricostruito.
/// Debounce 0: a 10k voci il calcolo costa circa 0,3 ms sul thread UI (AutoCompleteRicercaBenchmarkTests); un ritardo
/// di 200 ms cambierebbe il menu senza un beneficio misurato.</summary>
public sealed class AutoCompleteRicercaCollegamento : IDisposable
{
    private readonly AutoCompleteBox _box;
    private readonly Func<IReadOnlyList<string>> _sorgente;
    private readonly EventHandler<KeyEventArgs> _tastieraTunnel;
    private IReadOnlyList<string>? _fonte;
    private AutoCompleteRicerca<string>? _ricerca;

    public AutoCompleteRicercaCollegamento(AutoCompleteBox box, Func<IReadOnlyList<string>> sorgente)
    {
        _box = box;
        _sorgente = sorgente;
        _tastieraTunnel = OnKeyDownTunnel;
        _box.Populating += OnPopulating;
        _box.AddHandler(InputElement.KeyDownEvent, _tastieraTunnel, RoutingStrategies.Tunnel);
    }

    private void OnPopulating(object? sender, PopulatingEventArgs e)
    {
        e.Cancel = true; // il menu lo popola il motore, non il filtro interno di AutoCompleteBox
        Motore().Digita(e.Parameter);
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Enter) Motore().Allinea(_box.Text);
    }

    private AutoCompleteRicerca<string> Motore()
    {
        var lista = _sorgente();
        if (_ricerca == null || !ReferenceEquals(lista, _fonte))
        {
            _ricerca?.Dispose();
            _fonte = lista;
            _ricerca = new AutoCompleteRicerca<string>(lista, voce => voce, Applica);
        }
        return _ricerca;
    }

    private void Applica(IReadOnlyList<string> voci)
    {
        _box.ItemsSource = voci;
        _box.PopulateComplete();
    }

    public void Dispose()
    {
        _box.Populating -= OnPopulating;
        _box.RemoveHandler(InputElement.KeyDownEvent, _tastieraTunnel);
        _ricerca?.Dispose();
        _ricerca = null;
        _fonte = null;
    }
}
