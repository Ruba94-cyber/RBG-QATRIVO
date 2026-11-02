using System.Globalization;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class AppConfigServiceTests
{
    [Theory]
    [InlineData("it-IT", "it")]
    [InlineData("it-CH", "it")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "en")]
    [InlineData("fr-FR", "fr")]
    [InlineData("fr-CA", "fr")]
    public void LinguaPredefinita_SegueWindowsConFallbackInglese(string cultura, string attesa)
    {
        Assert.Equal(attesa, AppConfigService.LinguaPredefinita(CultureInfo.GetCultureInfo(cultura)));
    }

    [Fact]
    public void Load_ConfigEsistente_ConservaLaLinguaScelta()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ConfigTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new AppConfigService(Path.Combine(dir, "config.json"));
            var config = service.Load();
            config.Lingua = "en";
            service.Save(config);

            Assert.Equal("en", new AppConfigService(Path.Combine(dir, "config.json")).Load().Lingua);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BackupEsterno_RestaNellaConfigurazioneLocale_ECompatibileConLaConfigurazionePrecedente()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ExtConfig_" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new AppConfigService(Path.Combine(dir, "config.json"));
            var legacy = service.Load();
            Assert.Equal("", legacy.PercorsoBackupEsterno);
            legacy.PercorsoBackupEsterno = Path.Combine(dir, "external");
            service.Save(legacy);

            var reloaded = new AppConfigService(Path.Combine(dir, "config.json")).Load();
            Assert.Equal(legacy.PercorsoBackupEsterno, reloaded.PercorsoBackupEsterno);
            // L'aggiunta è nel config LOCALE, non richiede una migrazione del dataset 6.5.3.
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_LinguaNonSupportata_RipristinaUnValoreSupportato()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ConfigTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "config.json");
            var service = new AppConfigService(path);
            service.Save(new() { Lingua = "xx" });

            Assert.Equal("it", service.Load().Lingua);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_DizionariDaJson_MantengonoIlConfrontoSenzaDistinzioneMaiuscole()
    {
        // Il JSON assegna un Dictionary nuovo: senza il ripristino nel setter, l'inizializzatore OrdinalIgnoreCase
        // andrebbe perso e una chiave scritta con altre maiuscole non verrebbe più trovata.
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ConfigTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, "{ \"VersioniOnboardingCompletate\": { \"Home|Standard\": 2 }, \"VisteSalvate\": { \"test-list\": [] } }");

            var config = new AppConfigService(path).Load();

            Assert.True(config.VersioniOnboardingCompletate.ContainsKey("home|standard"));
            Assert.True(config.VisteSalvate.ContainsKey("TEST-LIST"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
