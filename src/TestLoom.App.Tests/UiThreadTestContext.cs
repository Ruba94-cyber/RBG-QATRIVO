using System.Collections.Concurrent;

namespace TestLoom.App.Tests;

/// <summary>Contesto di sincronizzazione monothread che simula il thread UI (dispatcher) nei test: le continuazioni
/// degli await vengono accodate e eseguite solo dal thread che chiama <see cref="Esegui"/>, come il dispatcher
/// Avalonia. Serve a verificare che stati e property change avvengano sul thread UI, cosa che AttendiModifica
/// (thread pool senza contesto) non può dimostrare.</summary>
internal sealed class UiThreadTestContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _coda = new();
    private int _threadUi = -1;

    /// <summary>Id del thread su cui <see cref="Esegui"/> ha pompato la coda (il "thread UI" del test).</summary>
    public int ThreadUiId => _threadUi;

    public override void Post(SendOrPostCallback d, object? state) => _coda.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    /// <summary>Avvia <paramref name="avvio"/> sul thread corrente come thread UI e pompa la coda finché il Task
    /// completa, con timeout. Un'eccezione del Task viene rilanciata.</summary>
    public void Esegui(Func<Task> avvio, TimeSpan timeout)
    {
        var precedente = Current;
        SetSynchronizationContext(this);
        _threadUi = Environment.CurrentManagedThreadId;
        try
        {
            var task = avvio();
            var scadenza = DateTime.UtcNow + timeout;
            while (!task.IsCompleted || _coda.Count > 0)
            {
                if (_coda.TryTake(out var lavoro, 10)) lavoro.Callback(lavoro.State);
                if (DateTime.UtcNow > scadenza)
                    throw new TimeoutException("Il contesto UI di test non ha completato l'operazione nel tempo previsto.");
            }
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(precedente);
        }
    }
}
