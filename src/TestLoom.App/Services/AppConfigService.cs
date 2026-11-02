using TestLoom.App.Models;
using System.Globalization;

namespace TestLoom.App.Services;

/// <summary>Configurazione locale dell'istanza, salvata in %AppData%, mai nella cartella condivisa (§7.9).</summary>
public class AppConfigService
{
    /// <summary>Serializza mutazioni e scritture di config.json condivise fra preferenze UI e salvataggi in background.</summary>
    internal static object ConfigSync { get; } = new();

    private readonly string _configPath;

    public AppConfigService(string? configPath = null)
    {
        // Override intenzionale per collaudi end-to-end e postazioni portabili: consente di avviare una
        // sessione completamente isolata senza rinominare o toccare il config reale dell'utente.
        if (configPath == null)
        {
            var overridePath = Environment.GetEnvironmentVariable("TestLoom_CONFIG_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath)) configPath = overridePath;
        }
        var dir = configPath == null
            ? Path.Combine(TestLoomDataRoot.AppDataRoot, "TestLoom")
            : Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        Directory.CreateDirectory(dir);
        _configPath = configPath == null ? Path.Combine(dir, "config.json") : Path.GetFullPath(configPath);
    }

    public AppConfig Load()
    {
        if (!File.Exists(_configPath)) return new AppConfig { Lingua = LinguaPredefinita(CultureInfo.CurrentUICulture) };
        var config = JsonIo.TryRead<AppConfig>(_configPath, out _) ?? new AppConfig { Lingua = LinguaPredefinita(CultureInfo.CurrentUICulture) };
        config.Lingua = LocalizationService.NormalizzaLingua(config.Lingua);
        return config;
    }

    /// <summary>TestLoom supporta italiano, inglese e francese (scelta in Impostazioni). Al primo avvio segue la lingua
    /// dell'interfaccia di Windows: le culture italiane usano "it", le francesi "fr", le altre il fallback inglese "en".
    /// Un valore salvato non riconosciuto torna a "it" (vedi Load).</summary>
    public static string LinguaPredefinita(CultureInfo cultura) =>
        cultura.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "it" => "it",
            "fr" => "fr",
            _ => "en",
        };

    public void Save(AppConfig config)
    {
        lock (ConfigSync) JsonIo.WriteAtomic(_configPath, config);
    }

    /// <summary>Cartella locale dell'istanza (stessa di config.json), esposta per altri file locali al PC che
    /// non devono vivere nella cartella condivisa — es. la bozza di autosave di un test non ancora salvato
    /// (Addendum: audit UX 2026-08-26, nota minore 1). Riusa la stessa risoluzione di percorso di config.json,
    /// incluso l'override TestLoom_CONFIG_PATH usato nei collaudi, così un test isolato non tocca mai la cartella
    /// reale dell'utente.</summary>
    public string CartellaLocale => Path.GetDirectoryName(_configPath)!;
}
