using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Piattaforma headless per i test Category=LayoutHeadless: la stessa App dell'app reale, senza ciclo di avvio
/// classico (niente bootstrap dei dati, niente crash log) e senza finestre a schermo.
/// <para>
/// <see cref="Dispatcher.UIThread"/> appartiene al thread di <see cref="DispatcherTestPump"/> (il suo ModuleInitializer gira
/// per primo nel processo di test). Per questo la piattaforma viene inizializzata e il codice UI viene eseguito dentro
/// quel dispatcher con <see cref="Dispatcher.Invoke{TResult}(Func{TResult})"/>: un secondo thread UI fallirebbe con
/// "a different thread owns it".
/// </para>
/// <para>
/// I test della categoria sono nella collezione "LayoutHeadless", senza parallelismo: token e modo di layout sono stato
/// globale.
/// </para>
/// </summary>
internal static class LayoutHeadlessSessione
{
    private static readonly object Blocco = new();
    private static bool _avviata;

    /// <summary>Esegue il lavoro sul thread UI e ne attende la fine; un'eccezione torna al chiamante.</summary>
    internal static void Esegui(Action lavoro) => Esegui<int>(() => { lavoro(); return 0; });

    internal static T Esegui<T>(Func<T> lavoro)
    {
        Assicura();
        return Dispatcher.UIThread.Invoke(lavoro);
    }

    /// <summary>Mostra la finestra e fa passare layout e job in attesa, come farebbe il ciclo UI reale.</summary>
    internal static void Mostra(Window finestra)
    {
        finestra.Show();
        Assesta(finestra);
    }

    /// <summary>Finestra principale della shell reale alla dimensione data. Il costruttore di MainWindow imposta gli handler
    /// statici dei servizi (conferme, appunti, dialoghi): li ripristina subito, perche' gli altri test non trovino handler di
    /// una finestra che non esiste piu'.</summary>
    internal static MainWindow CreaFinestraPrincipale(MainViewModel vm, int larghezza, int altezza)
    {
        var conferma = ConfirmService.Handler;
        var appunti = ClipboardService.Handler;
        var testo = TextEditService.Handler;
        var seleziona = SelezionaDialogService.Handler;
        var conflitti = MergeConflittoDialogService.Handler;
        try
        {
            return new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = larghezza, Height = altezza };
        }
        finally
        {
            ConfirmService.Handler = conferma;
            ClipboardService.Handler = appunti;
            TextEditService.Handler = testo;
            SelezionaDialogService.Handler = seleziona;
            MergeConflittoDialogService.Handler = conflitti;
        }
    }

    /// <summary>Esegue i job in coda e un passaggio di layout della finestra. Va chiamato sul thread UI.</summary>
    /// <summary>Attende un comando asincrono (IniziaModifica) senza bloccare il thread UI: la sua ripresa passa dal
    /// dispatcher Avalonia, quindi il ciclo pompa i job finché il Task non completa, con timeout.</summary>
    internal static void AttendiComando(System.Threading.Tasks.Task task, TimeSpan timeout)
    {
        var scadenza = DateTime.UtcNow + timeout;
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > scadenza) throw new TimeoutException("Il comando non è completato nel tempo previsto.");
            System.Threading.Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        task.GetAwaiter().GetResult();
    }

    internal static void Assesta(TopLevel finestra)
    {
        Dispatcher.UIThread.RunJobs();
        finestra.GetLayoutManager()?.ExecuteLayoutPass();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Assicura()
    {
        lock (Blocco)
        {
            if (_avviata) return;
            Dispatcher.UIThread.Invoke(() =>
                AppBuilder.Configure<TestLoom.App.App>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                    .WithInterFont()   // stesso font dell'app (Program.BuildAvaloniaApp): senza, le misure del testo sono sovrastimate
                    .SetupWithoutStarting());
            _avviata = true;
        }
    }
}

[CollectionDefinition("LayoutHeadless", DisableParallelization = true)]
public sealed class LayoutHeadlessCollection
{
}
