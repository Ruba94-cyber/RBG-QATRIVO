namespace TestLoom.App.Services;

/// <summary>Collega digitazione, attesa (debounce), ricerca fuori dal thread UI e applicazione sul thread UI.
/// Ogni digitazione supera la precedente: la ricerca in attesa o in corso viene annullata, e un risultato tardivo
/// viene scartato sia prima di essere postato sia al momento dell'applicazione (stesso schema a generazione di
/// GlobalSearchViewModel). Il thread UI e' iniettabile: nei test l'applicazione avviene in linea.</summary>
public sealed class PickerRicercaController<T> : IDisposable where T : class
{
    public const int DebounceDefaultMs = 200;

    private readonly PickerMotore<T> _motore;
    private readonly Action<PickerRisultato<T>> _applica;
    private readonly int _debounceMs;
    private readonly Func<int, CancellationToken, Task> _attesa;
    private readonly Action<Action> _postaSulThreadUi;
    private int _generazione;
    private CancellationTokenSource? _cts;
    private Task _ultimaRicerca = Task.CompletedTask;
    private volatile bool _disposto;

    public PickerRicercaController(
        PickerMotore<T> motore,
        Action<PickerRisultato<T>> applica,
        int debounceMs = DebounceDefaultMs,
        Func<int, CancellationToken, Task>? attesa = null,
        Action<Action>? postaSulThreadUi = null)
    {
        _motore = motore;
        _applica = applica;
        _debounceMs = Math.Max(0, debounceMs);
        _attesa = attesa ?? ((ms, ct) => Task.Delay(ms, ct));
        _postaSulThreadUi = postaSulThreadUi ?? (azione => Avalonia.Threading.Dispatcher.UIThread.Post(azione));
    }

    /// <summary>Ultima ricerca avviata: completa quando il suo risultato e' stato applicato o scartato (per i test).</summary>
    public Task UltimaRicerca => _ultimaRicerca;

    /// <summary>Annulla la ricerca in attesa o in corso: il suo risultato non verra' piu' mostrato. Chiamare prima di
    /// calcolare il risultato in linea sul thread UI, per non applicare dopo un risultato piu' vecchio.</summary>
    public void Annulla()
    {
        Interlocked.Increment(ref _generazione);
        var corrente = _cts;
        corrente?.Cancel();
    }

    /// <summary>Chiamare dal thread UI a ogni variazione del testo di ricerca.</summary>
    public void Digita(string? testo)
    {
        if (_disposto) return;
        var generazione = Interlocked.Increment(ref _generazione);
        var precedente = _cts;
        var nuovo = new CancellationTokenSource();
        _cts = nuovo;
        precedente?.Cancel();
        _ultimaRicerca = EseguiAsync(generazione, testo ?? string.Empty, nuovo.Token);
    }

    private async Task EseguiAsync(int generazione, string testo, CancellationToken ct)
    {
        try
        {
            if (_debounceMs > 0) await _attesa(_debounceMs, ct).ConfigureAwait(false);
            var risultato = await _motore.CercaAsync(testo, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || generazione != Volatile.Read(ref _generazione)) return;
            _postaSulThreadUi(() =>
            {
                if (!_disposto && generazione == Volatile.Read(ref _generazione)) _applica(risultato);
            });
        }
        catch (OperationCanceledException)
        {
            // Superata da una digitazione successiva (o chiusura del selettore): nessun risultato da mostrare.
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Ricerca nel selettore", ex);
        }
    }

    public void Dispose()
    {
        if (_disposto) return;
        _disposto = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
