using System.Reflection;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Revisione (7b51388), correzioni 1, 4, 5: sottocartella che rifiuta = avviso per quel percorso e non sola lettura
/// della radice; attesa della manutenzione limitata con recupero del gate; ricordo locale dell'alias di chiave e nuovo tentativo
/// del salvataggio del config. Le prove usano sostituti (seam internal), mai unità di rete reali.</summary>
[Collection("CondivisioneRete")]
public class RevisioneTerzaTests : IDisposable
{
    private readonly string _dir = TestRete.NuovaDirectoria();

    public RevisioneTerzaTests() => WorkspaceLocalPaths.ReimpostaChiaviSessione();

    public void Dispose()
    {
        WorkspaceLocalPaths.ReimpostaChiaviSessione();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void SottocartellaSempreRifiutata_NonTieneLaRadiceInSolaLettura()
    {
        // Radice di rete: la sottocartella testcases rifiuta sempre la prova. La radice torna operativa dopo due prove riuscite.
        var root = Path.Combine(_dir, "dati");
        Directory.CreateDirectory(root);
        var configService = new AppConfigService(Path.Combine(TestRete.NuovaDirectoria(), "config.json"));
        configService.Save(new AppConfig { CartellaCondivisa = root, Lingua = "it", ModalitaCartellaCondivisa = "Rete" });
        var sondaPrecedente = CondivisioneHealthService.ProbeSottocartella;
        try
        {
            using var state = new AppState(configService, runBackgroundMaintenance: false);
            Assert.Equal(Models.StatoCondivisione.Operativa, state.StatoCondivisione.Stato);

            CondivisioneHealthService.ProbeSottocartella = dir => !dir.EndsWith("testcases", StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Path.Combine(root, "testcases"), CondivisioneHealthService.SottocartelleNonScrivibili(state.Paths!));

            SharedWriteLossMonitor.Osserva(Path.Combine(root, "nonconformita", "nc.json"), new DirectoryNotFoundException("manca"));
            state.RileggiDaDisco();
            Assert.Equal(Models.StatoCondivisione.SolaLettura, state.StatoCondivisione.Stato);

            state.RileggiDaDisco();   // seconda prova riuscita: la sottocartella rifiutata non trattiene la radice
            Assert.Equal(Models.StatoCondivisione.Operativa, state.StatoCondivisione.Stato);
        }
        finally
        {
            CondivisioneHealthService.ProbeSottocartella = sondaPrecedente;
        }
    }

    [Fact]
    public void ManutenzioneNonFinita_CambioCartellaFalliscePulitoSenzaBloccareIlGate()
    {
        var configService = new AppConfigService(Path.Combine(_dir, "config.json"));
        configService.Save(new AppConfig { CartellaCondivisa = Path.Combine(_dir, "vuota"), Lingua = "it" });
        var attesaPrecedente = AppState.AttesaManutenzione;
        AppState.AttesaManutenzione = TimeSpan.FromMilliseconds(300);
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        try
        {
            // Manutenzione che non finisce mai: il cambio cartella deve fallire con un messaggio, non restare appeso.
            var campo = typeof(AppState).GetField("_backgroundMaintenanceTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
            campo.SetValue(state, new TaskCompletionSource().Task);

            var errore = Assert.Throws<IOException>(() => state.InizializzaCartella(Path.Combine(_dir, "nuova")));
            Assert.Contains("Manutenzione in background", errore.Message);

            // Il gate è stato liberato: un nuovo tentativo con manutenzione completata non si blocca.
            campo.SetValue(state, null);
            Directory.CreateDirectory(Path.Combine(_dir, "nuova"));
            state.InizializzaCartella(Path.Combine(_dir, "nuova"));
            Assert.True(state.InizializzazioneWorkspaceCompletata);
        }
        finally
        {
            AppState.AttesaManutenzione = attesaPrecedente;
        }
    }

    [Fact]
    public void Dispose_ConManutenzioneNonFinita_NonLanciaERientraEntroIlLimite()
    {
        // Chiusura con manutenzione che non finisce mai: Dispose torna entro il limite senza eccezioni
        // (MainWindow.OnClosing è async void: un'eccezione qui chiuderebbe il processo).
        var configService = new AppConfigService(Path.Combine(_dir, "config.json"));
        configService.Save(new AppConfig { CartellaCondivisa = Path.Combine(_dir, "vuota"), Lingua = "it" });
        var attesaPrecedente = AppState.AttesaManutenzione;
        AppState.AttesaManutenzione = TimeSpan.FromMilliseconds(300);
        var state = new AppState(configService, runBackgroundMaintenance: false);
        try
        {
            var campo = typeof(AppState).GetField("_backgroundMaintenanceTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
            campo.SetValue(state, new TaskCompletionSource().Task);

            var cronometro = System.Diagnostics.Stopwatch.StartNew();
            var eccezione = Record.Exception(() => state.Dispose());
            cronometro.Stop();

            Assert.Null(eccezione);
            Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(10), $"Dispose ha impiegato {cronometro.Elapsed}");
        }
        finally
        {
            AppState.AttesaManutenzione = attesaPrecedente;
        }
    }

    [Fact]
    public void RicordoAlias_UncDiverso_NonRiusaLaChiaveDellaVecchiaCartella()
    {
        // Sessione 1: Z: punta a UNC srv\dati-a. Sessione 2: Z: punta a UNC srv\dati-b. La chiave ricordata per Z: non va riusata.
        var z = @"Z:\testloom-" + Guid.NewGuid().ToString("N");
        string chiaveA;
        using (new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati-a")))
            chiaveA = WorkspaceLocalPaths.FissaChiaveAllApertura(z, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), out _);

        WorkspaceLocalPaths.ReimpostaChiaviSessione();
        string chiaveB;
        using (new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati-b")))
            chiaveB = WorkspaceLocalPaths.FissaChiaveAllApertura(z, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), out _);

        Assert.NotEqual(chiaveA, chiaveB);
    }

    [Fact]
    public void RicordoAlias_SessioneSuccessiva_LetteraIrrisolvibileRiusaLaChiaveUnc()
    {
        // Prima sessione: lettera risolvibile → chiave canonica (UNC) e ricordo locale per la lettera.
        // Seconda sessione: config senza alias e lettera non più risolvibile → stessa chiave, non quella con la lettera.
        var z = @"Z:\testloom-" + Guid.NewGuid().ToString("N");
        string chiaveUnc;
        using (new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati")))
            chiaveUnc = WorkspaceLocalPaths.FissaChiaveAllApertura(z, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), out _);

        WorkspaceLocalPaths.ReimpostaChiaviSessione();
        using (new RisolutoreUnitaFinto(("Z:", null)))
            Assert.Equal(chiaveUnc, WorkspaceLocalPaths.FissaChiaveAllApertura(z, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), out _));
    }

    [Fact]
    public void AliasNonSalvato_SiRitentaAlProssimoGiroQuandoLoScritturaRiesce()
    {
        // Config bloccato in lettura condivisa: il salvataggio dell'alias fallisce, il workspace si apre lo stesso e il salvataggio
        // resta da ritentare. Al giro successivo, con il file di nuovo scrivibile, l'alias viene salvato.
        var configPath = Path.Combine(TestRete.NuovaDirectoria(), "config.json");
        var root = Path.Combine(_dir, "dati");
        Directory.CreateDirectory(root);
        new AppConfigService(configPath).Save(new AppConfig { CartellaCondivisa = _dir, Lingua = "it" });
        using (new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using var state = new AppState(new AppConfigService(configPath), runBackgroundMaintenance: false);
            Assert.True(state.InizializzazioneWorkspaceCompletata);
            var daSalvare = (bool)typeof(AppState).GetField("_configDaSalvare", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
            Assert.True(daSalvare);
        }

        using var riaperto = new AppState(new AppConfigService(configPath), runBackgroundMaintenance: false);
        riaperto.RileggiDaDisco();
        Assert.Contains("ChiaveWorkspacePerCartella", File.ReadAllText(configPath));
    }
}
