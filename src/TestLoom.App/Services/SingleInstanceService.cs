using System.IO.Pipes;

namespace TestLoom.App.Services;

/// <summary>Istanza singola per PC (§6.7): se l'app viene lanciata due volte, la seconda invocazione
/// segnala alla prima (già in esecuzione) di portarsi in primo piano, poi esce subito senza aprire una
/// seconda finestra — evita scritture parallele dallo stesso PC.</summary>
public static class SingleInstanceService
{
    private const string MutexName = "TestLoom_SingleInstance_Mutex_v1";
    private const string PipeName = "TestLoom_Activate_Pipe_v1";

    private static Mutex? _mutex;

    /// <summary>1 dopo il primo errore dell'ascolto, finché una connessione non riesce: un errore che si ripete ogni secondo
    /// si registra una volta sola, non a ogni tentativo.</summary>
    private static int _erroreAscoltoRegistrato;

    /// <summary>Ritorna true se questa è la prima istanza (deve procedere ad avviare l'UI).
    /// Se ritorna false, il chiamante deve terminare immediatamente il processo.</summary>
    public static bool TryBecomePrimaryInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var creataOra);
        if (creataOra) return true;

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("ACTIVATE");
        }
        catch (Exception ex)
        {
            // se la pipe non risponde l'istanza precedente potrebbe essere in uno stato anomalo:
            // usciamo comunque per non rischiare scritture concorrenti dallo stesso PC.
            CrashLogService.Registra("Attivazione della prima istanza: pipe non raggiungibile", ex);
        }
        return false;
    }

    public static void ListenForActivation(Action onActivateRequested)
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    await server.WaitForConnectionAsync();
                    Volatile.Write(ref _erroreAscoltoRegistrato, 0);
                    using var reader = new StreamReader(server);
                    await reader.ReadLineAsync();
                    onActivateRequested();
                }
                catch (Exception ex)
                {
                    if (Interlocked.Exchange(ref _erroreAscoltoRegistrato, 1) == 0)
                        CrashLogService.Registra("Ascolto dell'attivazione dalla seconda istanza", ex);
                    await Task.Delay(1000);
                }
            }
        });
    }
}
