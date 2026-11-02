using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Rendering di un grafico alla dimensione richiesta, in modo asincrono e con debounce dei ridimensionamenti:
/// una nuova richiesta annulla la precedente, il PNG si genera e si decodifica su un thread di lavoro, e il risultato
/// torna al contesto del chiamante (il thread UI nell'app) per essere applicato. Risultati superati vengono scartati.
/// Un fallimento (eccezione di rendering o di decodifica, PNG nullo) non e' silenzioso: viene registrato nel log
/// diagnostico e notificato con <c>alFallimento</c>; la stessa dimensione puo' essere richiesta di nuovo.</summary>
public sealed class RenderGraficoAsincrono<T> : IDisposable where T : class
{
    private readonly Func<DimensioniGrafico, byte[]> _render;
    private readonly Func<byte[], T?> _decodifica;
    private readonly Action<T> _applica;
    private readonly Action? _alFallimento;
    private readonly TimeSpan _ritardo;
    private CancellationTokenSource? _cts;
    private DimensioniGrafico? _richiesta;

    public RenderGraficoAsincrono(Func<DimensioniGrafico, byte[]> render, Func<byte[], T?> decodifica, Action<T> applica,
        TimeSpan? ritardo = null, Action? alFallimento = null)
    {
        _render = render;
        _decodifica = decodifica;
        _applica = applica;
        _alFallimento = alFallimento;
        _ritardo = ritardo ?? TimeSpan.FromMilliseconds(150);
    }

    /// <summary>Ultimo rendering richiesto (completato o in corso).</summary>
    private Task UltimoRender { get; set; } = Task.CompletedTask;

    /// <summary>Richiede il rendering alla dimensione data, dopo il ritardo di debounce. Restituisce false se la dimensione
    /// coincide con l'ultima richiesta riuscita o in corso; una richiesta diversa annulla quella pendente.</summary>
    public bool Richiedi(DimensioniGrafico dimensioni)
    {
        if (_richiesta is { } precedente && Uguali(precedente, dimensioni)) return false;
        _richiesta = dimensioni;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        UltimoRender = EseguiAsync(dimensioni, _ritardo, _cts.Token);
        return true;
    }

    private async Task EseguiAsync(DimensioniGrafico dimensioni, TimeSpan ritardo, CancellationToken ct)
    {
        try
        {
            if (ritardo > TimeSpan.Zero) await Task.Delay(ritardo, ct);
            var risultato = await Task.Run(() => DecodificaSicura(dimensioni), ct);
            if (ct.IsCancellationRequested)
            {
                (risultato as IDisposable)?.Dispose();
                return;
            }
            if (risultato == null)
            {
                Fallisci("PNG del grafico non decodificato", null, dimensioni);
                return;
            }
            _applica(risultato);
        }
        catch (OperationCanceledException)
        {
            // richiesta superata da una dimensione piu' recente: nessun risultato da applicare
        }
        catch (Exception ex)
        {
            Fallisci("Rendering del grafico", ex, dimensioni);
        }
    }

    private T? DecodificaSicura(DimensioniGrafico dimensioni) => _decodifica(_render(dimensioni));

    /// <summary>Registra il fallimento e consente una nuova richiesta della stessa dimensione (al prossimo ridimensionamento).</summary>
    private void Fallisci(string origine, Exception? ex, DimensioniGrafico dimensioni)
    {
        if (_richiesta is { } r && Uguali(r, dimensioni)) _richiesta = null;
        CrashLogService.Registra($"{origine} ({dimensioni.Larghezza:0}x{dimensioni.Altezza:0} DIP, scala {dimensioni.SicuraScala:0.##})",
            ex ?? new InvalidOperationException("Decodifica PNG del grafico non riuscita."));
        _alFallimento?.Invoke();
    }

    private static bool Uguali(DimensioniGrafico a, DimensioniGrafico b) =>
        Math.Abs(a.Larghezza - b.Larghezza) < 0.5 && Math.Abs(a.Altezza - b.Altezza) < 0.5 && Math.Abs(a.SicuraScala - b.SicuraScala) < 0.01;

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
