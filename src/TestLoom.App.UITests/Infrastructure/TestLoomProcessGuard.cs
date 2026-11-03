using System.Diagnostics;

namespace TestLoom.App.UITests.Infrastructure;

/// <summary>
/// TestLoom applica un mutex a livello di intero PC (SingleInstanceService, "TestLoom_SingleInstance_Mutex_v1"): una
/// seconda istanza avviata mentre la prima è già in esecuzione non apre nessuna finestra, chiede alla prima di
/// portarsi in primo piano ed esce subito con codice 0 — questo è corretto per un utente reale, ma per la
/// suite UI produrrebbe un timeout "MainWindow non trovata" totalmente incomprensibile se sulla stessa
/// macchina è già aperta una sessione TestLoom reale (es. l'operatore che sta usando l'app mentre girano i test).
/// Questo guard lo rileva PRIMA di lanciare il processo di test e fallisce con un messaggio chiaro invece di
/// lasciare che il sintomo emerga confuso più a valle.
/// <para>
/// Un RBG - QATRIVO.exe avviato da un'altra esecuzione di test (una cartella <c>src\TestLoom.App\bin</c> di un worktree o del
/// checkout) non è un'istanza dell'utente: il guard ne attende la fine per un tempo limitato (fino a 20 minuti), senza mai terminarla. Un
/// RBG - QATRIVO.exe di altra origine fa fallire subito, come prima.
/// </para>
/// </summary>
public static class TestLoomProcessGuard
{
    private static readonly TimeSpan AttesaMassima = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan IntervalloVerifica = TimeSpan.FromMilliseconds(500);

    public static void EnsureNoRunningInstance()
    {
        var inizio = DateTime.UtcNow;
        while (true)
        {
            var running = Process.GetProcessesByName("RBG - QATRIVO");
            if (running.Length == 0) return;

            var istanzeReali = running.Where(p => !IsIstanzaDiTest(p)).ToArray();
            if (istanzeReali.Length > 0 || DateTime.UtcNow - inizio > AttesaMassima)
            {
                var pids = string.Join(", ", running.Select(p => p.Id));
                throw new InvalidOperationException(
                    $"È già in esecuzione un'istanza reale di RBG - QATRIVO.exe (PID: {pids}). TestLoom applica un lock singolo per " +
                    "PC (SingleInstanceService): un secondo avvio non aprirebbe alcuna finestra e i test UI andrebbero " +
                    "in timeout in modo incomprensibile. Chiudi ogni istanza di TestLoom aperta su questo PC (compresa " +
                    "un'eventuale icona nella tray/area di notifica: TestLoom può restare in esecuzione minimizzato) prima " +
                    "di eseguire la suite TestLoom.App.UITests.");
            }

            // Istanza di un'altra esecuzione di test: si attende la sua chiusura, senza mai terminarla.
            Thread.Sleep(IntervalloVerifica);
        }
    }

    /// <summary>Vero se il processo è un RBG - QATRIVO.exe di una build di test (cartella src\TestLoom.App\bin) oppure è già uscito
    /// (il ciclo lo rivede e lo scarta). Un processo vivo il cui percorso non è leggibile conta come istanza reale.</summary>
    private static bool IsIstanzaDiTest(Process processo)
    {
        try
        {
            if (processo.HasExited) return true;
            var percorso = processo.MainModule?.FileName;
            return percorso != null && percorso.Contains(@"\src\TestLoom.App\bin\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Uscito proprio durante la verifica (race con la chiusura dell'altra esecuzione): non è più un ostacolo.
            try { return processo.HasExited; }
            catch (Exception) { return false; }
        }
    }
}
