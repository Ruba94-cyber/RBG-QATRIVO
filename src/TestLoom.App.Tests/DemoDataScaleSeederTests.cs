using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.Tests.Perf;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>
/// Strumento riproducibile (Piano 6.4.0, FASE P1 prestazioni) per portare il banco di prova
/// <c>Desktop\DemoData</c> ai volumi target dell'obiettivo prestazioni utente 2026-09-24: ~1000 voci per
/// ogni catalogo di sistema e 100k Pacchetto (catalogo RevisioniUdt). Test/NC/CR restano quelli già
/// presenti nella cartella (misurati separatamente, vedi docs/PERF_6.4.0_BASELINE.md) — questo seeder NON li
/// tocca.
///
/// SKIPPATO di default: attivo solo con la variabile d'ambiente <c>TestLoom_SEED_SCALE_DATASET</c> puntata sulla
/// cartella dati da portare a scala (l'utente ha autorizzato l'uso diretto di Desktop\DemoData). Usa
/// esclusivamente i repository reali dell'app (MasterDataRepository&lt;T&gt; via AppState.Master, stesso
/// percorso di codice della UI) — mai un editing manuale dei file JSON. Idempotente: se un catalogo ha già
/// raggiunto il target non aggiunge altro; le righe aggiunte sono chiaramente etichettate come fittizie
/// ("-Scale-").
///
/// Comando per rigenerare il banco:
///   $env:TestLoom_SEED_SCALE_DATASET = "C:\Users\DTS\Desktop\DemoData"
///   dotnet test src/TestLoom.App.Tests --filter FullyQualifiedName~DemoDataScaleSeederTests
///
/// Controllo spazio disco (richiesta utente): si ferma con Assert.Fail se lo spazio libero sul drive della
/// cartella dati scende sotto 6 GB, sia prima di iniziare sia periodicamente durante il seed dei cataloghi
/// più grandi.
/// </summary>
public class DemoDataScaleSeederTests
{
    private const long SogliaSpazioLiberoByte = 6L * 1024 * 1024 * 1024; // 6 GB
    private const int TargetCatalogo = 1000;
    private const int TargetRevisioniUdt = 100_000;

    private readonly ITestOutputHelper _output;

    public DemoDataScaleSeederTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void PortaIlBancoDiProvaAiVolumiTarget()
    {
        var cartella = Environment.GetEnvironmentVariable("TestLoom_SEED_SCALE_DATASET");
        if (string.IsNullOrWhiteSpace(cartella) || !Directory.Exists(cartella))
        {
            _output.WriteLine("TestLoom_SEED_SCALE_DATASET non impostata o inesistente: seed scala auto-escluso (non fallito).");
            return;
        }

        BancoScaleSeedCore.VerificaSpazioLibero(cartella, SogliaSpazioLiberoByte);

        var configPath = Path.Combine(Path.GetTempPath(), "TestLoom_UITests", "ScaleSeed_" + Guid.NewGuid().ToString("N"), "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        try
        {
            var configService = new AppConfigService(configPath);
            configService.Save(new AppConfig { CartellaCondivisa = cartella, Lingua = "it", OperatoreAttivo = "" });

            using var state = new AppState(configService, runBackgroundMaintenance: false);

            BancoScaleSeedCore.SeedCataloghiEWorkingUnit(state, cartella, _output, TargetCatalogo, TargetRevisioniUdt);

            _output.WriteLine("Seed cataloghi/RevisioniUdt completato su " + cartella);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(configPath)!, recursive: true); } catch { /* best-effort */ }
        }
    }
}
