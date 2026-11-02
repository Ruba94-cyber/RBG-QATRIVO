namespace TestLoom.App.Services;

/// <summary>
/// Traccia applicativa circolare per la diagnosi di bug in generale, non solo dei crash (feedback utente
/// 2026-09-17: "non solo per crash proprio per bug in generale"; 2026-09-18: "il logging deve comprendere
/// veramente tutte le operazioni non solo la navigazione... non si vede quando archivio ecc"). A differenza di
/// CrashLogService (alimentato solo dai blocchi catch quando qualcosa lancia un'eccezione), questo registra le
/// operazioni significative anche quando vanno a buon fine: navigazione tra pagine, salvataggi/eliminazioni/
/// archiviazioni di Test, NonConformita, ChangeRequest, Segnalazioni e master, apertura/chiusura workspace,
/// accesso/logout operatore — così un comportamento sbagliato SENZA eccezione (un valore errato, un
/// salvataggio silenzioso sul record sbagliato, un'archiviazione che sembra non succedere) lascia comunque una
/// sequenza di eventi ricostruibile in un pacchetto diagnostico da inviare. Ogni repository che aggiunge un
/// nuovo tipo di mutazione dovrebbe registrarla qui con la propria categoria (Entita.Operazione), non solo
/// quella generica di Save: un'archiviazione che passa PER un Save (ChangeRequestRepository.Archivia) va
/// distinta dal salvataggio ordinario, altrimenti dalla sola traccia non si capisce quale delle due è successa.
/// Stesso schema di rotazione bounded di CrashLogService: mai un file che cresce senza limite, mai dati di
/// business (solo Id/nomi operazione, mai il contenuto di un Test/NC), mai condiviso su OneDrive/SharePoint.
/// </summary>
public static class DiagnosticTraceService
{
    private const long MaxLogBytes = 2L * 1024 * 1024;
    private const int RotatedCopies = 3;
    private static readonly object Gate = new();
    // Proprietà (non campo statico): la radice TestLoomDataRoot viene letta al primo uso reale, non durante l'inizializzazione di questa classe.
    private static string PercorsoPredefinito => Path.Combine(TestLoomDataRoot.AppDataRoot, "TestLoom", "trace.log");

    /// <summary>Solo test: file di traccia alternativo (una cartella temporanea per test). Null in produzione, dove si
    /// usa il trace.log nel %APPDATA% dell'utente. Senza questo la suite scriveva e rileggeva il file reale, condiviso
    /// con ogni altro processo di test o app aperto sulla stessa macchina: un file tenuto aperto da un altro processo
    /// faceva fallire la lettura o la scrittura della voce.</summary>
    internal static string? PercorsoPerTest { get; set; }

    private static string LogPath => PercorsoPerTest ?? PercorsoPredefinito;

    public static void Registra(string categoria, string messaggio)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(dir);
                RotateIfNeeded();
                var testo = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{categoria}] {messaggio}\n";
                File.AppendAllText(LogPath, testo);
            }
        }
        catch { }
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
                    try { if (File.Exists(destination)) File.Delete(destination); } catch { }
                }
                var source = i == 1 ? LogPath : RotatedPath(i - 1);
                if (!File.Exists(source)) continue;
                try { File.Move(source, destination, overwrite: true); } catch { }
            }
        }
        catch { }
    }

    private static string RotatedPath(int index) => Path.Combine(Path.GetDirectoryName(LogPath)!, $"trace.{index}.log");

    /// <summary>Percorsi dei file di traccia effettivamente presenti, più recente prima — usato da
    /// DiagnosticExportService per allegarli a un pacchetto diagnostico.</summary>
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
