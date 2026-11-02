using Avalonia.Threading;

namespace TestLoom.App.Services;

/// <summary>Auto-scadenza dei messaggi puramente informativi (feedback utente: banner "fissi e non
/// cancellabili"). Usa un DispatcherTimer una tantum; in ambienti senza dispatcher (test unitari) il timer
/// non parte e il messaggio resta finche' non viene chiuso a mano o sostituito.</summary>
public static class AutoScadenzaMessaggio
{
    public static readonly TimeSpan Durata = TimeSpan.FromSeconds(8);

    /// <summary>Esegue <paramref name="alScadere"/> dopo la durata indicata. Il chiamante verifica che il
    /// messaggio sia ancora quello di partenza.</summary>
    public static void Programma(Action alScadere, TimeSpan? durata = null)
    {
        try
        {
            if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Programma(alScadere, durata)); return; }
            var timer = new DispatcherTimer { Interval = durata ?? Durata };
            timer.Tick += (_, _) => { timer.Stop(); alScadere(); };
            timer.Start();
        }
        catch { /* nessun dispatcher disponibile: il messaggio resta chiudibile a mano */ }
    }
}
