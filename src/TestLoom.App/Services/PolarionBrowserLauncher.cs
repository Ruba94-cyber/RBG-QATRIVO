using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Browser usato per il profilo TestLoom dedicato (compilazione automatica di Ticketing), scelto per PC in Impostazioni
/// (AppConfig.BrowserPolarion): assente = automatico (Edge, poi Chrome); "Edge", "Chrome" oppure "Percorso" con
/// l'eseguibile Chromium indicato in AppConfig.BrowserPolarionPercorso.
/// </summary>
public static class PolarionBrowserLauncher
{
    public const string Automatico = "Automatico";
    public const string Edge = "Edge";
    public const string Chrome = "Chrome";
    public const string Percorso = "Percorso";

    public static string Normalizza(string? valore) => valore switch
    {
        Edge => Edge, Chrome => Chrome, Percorso => Percorso, _ => Automatico
    };

    private static string ProgramFiles(bool x86) =>
        Environment.GetEnvironmentVariable(x86 ? "ProgramFiles(x86)" : "ProgramFiles") ?? "";

    internal static IEnumerable<string> CandidatiEdge() => new[]
    {
        Path.Combine(ProgramFiles(true), "Microsoft", "Edge", "Application", "msedge.exe"),
        Path.Combine(ProgramFiles(false), "Microsoft", "Edge", "Application", "msedge.exe")
    };

    internal static IEnumerable<string> CandidatiChrome() => new[]
    {
        Path.Combine(ProgramFiles(false), "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(ProgramFiles(true), "Google", "Chrome", "Application", "chrome.exe")
    };

    /// <summary>Eseguibile esplicitamente scelto, o null se la scelta e' automatica o l'eseguibile non esiste su questo PC.</summary>
    public static string? EseguibileScelto(AppConfig? config)
    {
        switch (Normalizza(config?.BrowserPolarion))
        {
            case Edge: return CandidatiEdge().FirstOrDefault(File.Exists);
            case Chrome: return CandidatiChrome().FirstOrDefault(File.Exists);
            case Percorso:
                var p = (config?.BrowserPolarionPercorso ?? "").Trim().Trim('"');
                return p.Length > 0 && File.Exists(p) ? p : null;
            default: return null;
        }
    }

    /// <summary>Eseguibile per il profilo TestLoom: la scelta dell'utente, altrimenti Edge poi Chrome.</summary>
    public static string? EseguibileProfiloDitm(AppConfig? config) =>
        EseguibileScelto(config) ?? CandidatiEdge().Concat(CandidatiChrome()).FirstOrDefault(File.Exists);
}
