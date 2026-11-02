namespace TestLoom.App.Services;

/// <summary>Ultima rete di sicurezza per eccezioni non gestite. Il log diagnostico è importante, ma non deve
/// crescere senza limite: viene ruotato localmente a 2 MiB mantenendo tre generazioni precedenti. Nessun file
/// viene condiviso su OneDrive/SharePoint.</summary>
public static class CrashLogService
{
    private static int _installato;
    private static int _dispatcherInstallato;
    private const long MaxLogBytes = 2L * 1024 * 1024;
    private const int RotatedCopies = 3;
    private static readonly object Gate = new();
    // Proprietà (non campo statico): la radice TestLoomDataRoot viene letta al primo uso reale, non durante l'inizializzazione di questa classe.
    private static string PercorsoPredefinito => Path.Combine(TestLoomDataRoot.AppDataRoot, "TestLoom", "crash.log");

    /// <summary>Solo test: file di crash alternativo (cartella temporanea per processo di test), come
    /// DiagnosticTraceService.PercorsoPerTest. Null in produzione, dove si usa il crash.log nel %APPDATA% dell'utente
    /// (o nella radice isolata TestLoom_DATA_ROOT durante i collaudi automatici).</summary>
    internal static string? PercorsoPerTest { get; set; }

    private static string LogPath => PercorsoPerTest ?? PercorsoPredefinito;

    /// <summary>Cartella locale (mai condivisa su OneDrive/SharePoint) dove vivono crash.log e trace.log,
    /// mostrata in Impostazioni con un link diretto (feedback utente 2026-09-17).</summary>
    public static string LogDirectory => Path.GetDirectoryName(LogPath)!;

    /// <summary>Intervallo minimo fra due avvisi di errore UI: una rottura che si ripete non deve coprire la schermata.</summary>
    internal static readonly TimeSpan IntervalloAvvisoErroreUi = TimeSpan.FromSeconds(60);
    private static DateTime _ultimoAvvisoErroreUi = DateTime.MinValue;

    /// <summary>Avviso non bloccante per un errore UI non gestito: l'app resta in funzione e la shell mostra il percorso
    /// del crash.log. Evento sul thread UI, al massimo una volta per <see cref="IntervalloAvvisoErroreUi"/>.</summary>
    public static event Action<string>? ErroreUiSegnalato;

    public static void Installa()
    {
        if (Interlocked.Exchange(ref _installato, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Registra("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Registra("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Un'eccezione UI non gestita non chiude l'app: viene registrata e, con il limite di frequenza, segnalata
    /// alla shell (vedi <see cref="ErroreUiSegnalato"/>).</summary>
    public static void InstallaDispatcherUi()
    {
        if (Interlocked.Exchange(ref _dispatcherInstallato, 1) != 0) return;
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            GestisciErroreUi(e.Exception, DateTime.UtcNow);
        };
    }

    /// <summary>Solo test: riporta il limite di frequenza allo stato iniziale (nessun avviso ancora mostrato).</summary>
    internal static void AzzeraLimiteAvvisoErroreUi()
    {
        lock (Gate) _ultimoAvvisoErroreUi = DateTime.MinValue;
    }

    /// <summary>Registra un errore UI e decide se segnalarlo: al massimo una volta per <see cref="IntervalloAvvisoErroreUi"/>.
    /// <paramref name="adesso"/> è esplicito per i test del limite di frequenza.</summary>
    internal static void GestisciErroreUi(Exception ex, DateTime adesso)
    {
        Registra("Avalonia.Dispatcher.UnhandledException", ex);
        bool segnala;
        lock (Gate)
        {
            segnala = adesso - _ultimoAvvisoErroreUi >= IntervalloAvvisoErroreUi;
            if (segnala) _ultimoAvvisoErroreUi = adesso;
        }
        if (!segnala) return;
        try { ErroreUiSegnalato?.Invoke(LogPath); }
        catch (Exception fallimento) { Registra("CrashLogService.ErroreUiSegnalato", fallimento); }
    }

    public static void Registra(string origine, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(dir);
                RotateIfNeeded();
                var testo = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {origine}\n{ex}\n\n";
                File.AppendAllText(LogPath, testo);
            }
        }
        catch (Exception fallimento) { SegnalaFallimentoLog("registrazione", fallimento); }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < MaxLogBytes) return;
            for (var i = RotatedCopies; i >= 1; i--)
            {
                var destination = RotatedPath(i);
                if (i == RotatedCopies)
                {
                    try { if (File.Exists(destination)) File.Delete(destination); }
                    catch (Exception fallimento) { SegnalaFallimentoLog("rotazione: eliminazione", fallimento); }
                }
                var source = i == 1 ? LogPath : RotatedPath(i - 1);
                if (!File.Exists(source)) continue;
                try { File.Move(source, destination, overwrite: true); }
                catch (Exception fallimento) { SegnalaFallimentoLog("rotazione: spostamento", fallimento); }
            }
        }
        catch (Exception fallimento) { SegnalaFallimentoLog("rotazione", fallimento); }
    }

    private static string RotatedPath(int index) => Path.Combine(Path.GetDirectoryName(LogPath)!, $"crash.{index}.log");

    /// <summary>Il log non può registrare i propri errori: finiscono sul canale di debug, mai ingoiati in silenzio.</summary>
    private static void SegnalaFallimentoLog(string dove, Exception fallimento) =>
        System.Diagnostics.Debug.WriteLine($"CrashLogService ({dove}): {fallimento}");

    /// <summary>Percorsi dei file di log effettivamente presenti (crash.log più le generazioni ruotate),
    /// più recente prima — usato da DiagnosticExportService per allegarli a un pacchetto diagnostico da
    /// inviare in caso di problemi (feedback utente 2026-09-17), senza esporre la costante LogPath privata.</summary>
    public static IReadOnlyList<string> FilePresenti()
    {
        lock (Gate)
        {
            var risultato = new List<string>();
            if (File.Exists(LogPath)) risultato.Add(LogPath);
            for (var i = 1; i <= RotatedCopies; i++)
            {
                var path = RotatedPath(i);
                if (File.Exists(path)) risultato.Add(path);
            }
            return risultato;
        }
    }
}
