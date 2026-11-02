using System.Runtime.CompilerServices;

namespace TestLoom.App.Tests;

/// <summary>
/// Alcuni ViewModel (es. TestCaseEditorViewModel.Abandonment/StateSafety) accodano cleanup differita su
/// Avalonia.Threading.Dispatcher.UIThread.Post. Nell'app reale il loop applicativo la esegue; in xUnit
/// headless non gira alcun loop, quindi la coda non viene MAI drenata da sola. Dispatcher.UIThread e' inoltre
/// legato in modo permanente al PRIMO thread che lo tocca nell'intero processo di test: un test che provi a
/// pompare la coda da solo (Dispatcher.UIThread.RunJobs()) fallisce con "the calling thread cannot access
/// this object because a different thread owns it" non appena gira dopo un qualunque altro test che abbia
/// gia' toccato il Dispatcher su un thread diverso.
///
/// Fix una tantum per l'intero assembly: un [ModuleInitializer] gira PRIMA di qualunque test, quindi e'
/// garantito essere il primo a toccare Dispatcher.UIThread — diventa il proprietario permanente. Un thread
/// dedicato drena continuamente la coda per tutta la durata della sessione di test, cosi un
/// "await Task.Delay(...)" dopo un Post(...) funziona esattamente come si aspetterebbe un test scritto
/// pensando all'app reale, indipendentemente dall'ordine di esecuzione dei test.
/// </summary>
internal static class DispatcherTestPump
{
    [ModuleInitializer]
    internal static void Avvia()
    {
        var pump = new Thread(() =>
        {
            while (true)
            {
                try { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
                catch { /* best-effort: un job che lancia non deve fermare il pump per gli altri test */ }
                Thread.Sleep(5);
            }
        })
        {
            IsBackground = true,
            Name = "TestLoom-Test-Dispatcher-Pump"
        };
        pump.Start();
    }
}
