namespace TestLoom.App.Services;

/// <summary>
/// Radice dati isolata per i collaudi automatici (suite xUnit, Headless, host MultiPcHost, suite FlaUI). Il valore arriva
/// dalla variabile d'ambiente <c>TestLoom_DATA_ROOT</c>, letta una sola volta quando il processo usa per la prima volta un
/// percorso locale. Senza la variabile (produzione) non cambia nulla: %APPDATA% e %LOCALAPPDATA% restano quelli reali
/// dell'utente. Con la variabile impostata:
/// <list type="bullet">
/// <item>&lt;radice&gt;\AppData sostituisce %APPDATA% (crash.log, trace.log, config.json);</item>
/// <item>&lt;radice&gt;\LocalAppData sostituisce %LOCALAPPDATA% (TestLoom\workspaces, workflow-transactions, recovery-drafts, ...).</item>
/// </list>
/// Un processo figlio (per esempio RBG - QATRIVO.exe lanciato da FlaUI) eredita la variabile e quindi la stessa radice. Il formato dei
/// file non cambia: cambia solo la cartella di partenza.
/// </summary>
public static class TestLoomDataRoot
{
    /// <summary>Nome della variabile d'ambiente che attiva l'isolamento.</summary>
    public const string NomeVariabile = "TestLoom_DATA_ROOT";

    private static string? _radice = InterpretaRadice(Environment.GetEnvironmentVariable(NomeVariabile));

    /// <summary>Radice isolata del processo, o null in produzione.</summary>
    public static string? Radice => _radice;

    /// <summary>Cartella equivalente a %APPDATA% (crash.log, trace.log, config.json).</summary>
    public static string AppDataRoot =>
        RadiceAppData(_radice, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>Cartella equivalente a %LOCALAPPDATA% (base di TestLoom\...).</summary>
    public static string LocalAppDataRoot =>
        RadiceLocalAppData(_radice, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// Solo host di test (suite xUnit, Headless, MultiPcHost): fissa la radice del processo senza passare dall'ambiente.
    /// Va chiamata prima del primo uso dei percorsi locali. Null ripristina il comportamento di produzione.
    /// </summary>
    internal static void ImpostaRadiceProcesso(string? radice) => _radice = InterpretaRadice(radice);

    /// <summary>Valore della variabile normalizzato; vuoto o non valido significa produzione (null).</summary>
    internal static string? InterpretaRadice(string? valore)
    {
        if (string.IsNullOrWhiteSpace(valore)) return null;
        try { return Path.GetFullPath(valore.Trim().Trim('"')); }
        catch (Exception) { return null; }
    }

    internal static string RadiceAppData(string? radice, string appDataReale) =>
        radice is null ? appDataReale : Path.Combine(radice, "AppData");

    internal static string RadiceLocalAppData(string? radice, string localAppDataReale) =>
        radice is null ? localAppDataReale : Path.Combine(radice, "LocalAppData");
}
