using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.6.1 (correzioni B, C, D, E, F, G): regole per OneDrive invariate, gate di inizializzazione serializzato, gate
/// di scrittura limitato nel tempo ma fail-closed, lock abbandonato rimosso solo dalla sessione che l'ha creato, marker di
/// sola lettura legato all'identità condivisa, percorsi UNC lunghi nei DataPaths.</summary>
[Collection("CondivisioneRete")]
public class ChiaveDiffusioneEGateTests : IDisposable
{
    private readonly string _dir = TestRete.NuovaDirectoria();

    public ChiaveDiffusioneEGateTests() => WorkspaceLocalPaths.ReimpostaChiaviSessione();

    public void Dispose()
    {
        WorkspaceLocalPaths.ReimpostaChiaviSessione();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void PercorsoUncLungo_DataPathsERisorseRestanoIntegri()
    {
        // 6.6.1 (i): un percorso UNC oltre 260 caratteri non viene troncato nella composizione dei file né nella chiave.
        var segmento = new string('d', 60);
        var root = @"\" + @"\srv\share\" + segmento + @"\" + segmento + @"\" + segmento + @"\" + segmento + @"\" + segmento + @"\TestLoom";
        Assert.True(root.Length > 260);

        var paths = new DataPaths(root);
        var file = paths.TestCaseFile("Componente1", "TC-1");
        Assert.StartsWith(root + Path.DirectorySeparatorChar, file, StringComparison.OrdinalIgnoreCase);
        Assert.True(file.Length > 260);
        Assert.True(ModalitaCondivisioneService.EPercorsoUncRemoto(file));

        var chiave = WorkspaceLocalPaths.ComputeWorkspaceKey(root);
        Assert.Equal(32, chiave.Length);
        Assert.Equal(chiave, WorkspaceLocalPaths.ComputeWorkspaceKey(root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void MarkerDiVisualizzatore_ValeAncheSullaIdentitaCondivisa()
    {
        var z = @"Z:\testloom-" + Guid.NewGuid().ToString("N");
        var unc = @"\" + @"\srv\dati\" + Path.GetFileName(z);
        var baseDir = TestRete.NuovaDirectoria();
        var identita = Guid.NewGuid();
        using var _ = new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati"));

        WorkspaceReadOnlyService.RegistraIdentita(z, identita);
        WorkspaceReadOnlyService.SetEnabled(z, true, baseDir);
        Assert.True(File.Exists(Path.Combine(baseDir, "id-" + identita.ToString("N") + ".lock")));
        Assert.True(WorkspaceReadOnlyService.IsEnabled(unc, baseDir));   // l'altra ortografia non lo aggira

        WorkspaceReadOnlyService.SetEnabled(z, false, baseDir);
        Assert.False(WorkspaceReadOnlyService.IsEnabled(unc, baseDir));
        Assert.False(WorkspaceReadOnlyService.IsEnabled(z, baseDir));
    }

    [Fact]
    public void OneDrive_NonRegistraLaPerditaDiScrittura()
    {
        // 6.6.1 (decisione B): il monitor e il gate valgono solo per le cartelle di rete. Una cartella OneDrive resta com'era.
        var locale = Path.Combine(_dir, "onedrive");
        Directory.CreateDirectory(locale);
        SharedWriteLossMonitor.Registra(locale);
        SharedWriteLossMonitor.Osserva(Path.Combine(locale, "x.json"), new DirectoryNotFoundException("manca"));
        Assert.Null(SharedWriteLossMonitor.MotivoPerdita(locale));
        Assert.Null(SharedWriteLossMonitor.EsitoPerdita(locale));
    }

    [Fact]
    public void OneDrive_IlProcessoAttribNonVieneTerminatoAlTimeout()
    {
        // Decisione B: in OneDrive il comportamento storico non cambia (nessuna terminazione). Il processo è breve: si attende la
        // fine naturale e il test verifica solo l'esito dichiarato.
        var esito = AllegatiSyncService.EseguiProcesso("ping.exe", "-n 3 127.0.0.1", 200, terminaAlTimeout: false);
        Assert.Equal(AllegatiSyncService.EsitoEsecuzione.Scaduto, esito);
    }

    [Fact]
    public void GateConTetto_PropagaLeEccezioniDelGate()
    {
        Assert.Throws<IOException>(() => IoTimeoutGuard.EseguiPropagando<bool>(
            () => throw new IOException("blocco del gate"), TimeSpan.FromSeconds(5), () => false));
    }

    [Fact]
    public void GateConTetto_SuScadenzaRestituisceIlFallbackSenzaAttendere()
    {
        var sw = Stopwatch.StartNew();
        var esito = IoTimeoutGuard.EseguiPropagando(() => { Thread.Sleep(3000); return true; }, TimeSpan.FromMilliseconds(200), () => false);
        sw.Stop();
        Assert.False(esito);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"il gate ha atteso {sw.Elapsed}");
    }

    [Fact]
    public void Sottocartelle_InsiemeVuotoOAssente_NonFalliscono()
    {
        // Radice appena creata: nessuna sottocartella ancora. La prova delle sottocartelle non deve bloccare il ripristino.
        var paths = new DataPaths(_dir);
        Assert.True(CondivisioneHealthService.ProvaSottocartelleScrittura(paths));
    }

    [Fact]
    public void RiprovaEImpostaCartellaInParallelo_Lasciano_IlWorkspaceAperto()
    {
        // Gate unico (correzione E): riapertura e cambio cartella concorrenti non si rompono e lasciano il workspace consistente.
        var root = Path.Combine(_dir, "rete", "dati");
        var configService = new AppConfigService(Path.Combine(TestRete.NuovaDirectoria(), "config.json"));
        configService.Save(new AppConfig { CartellaCondivisa = root, Lingua = "it" });
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        Assert.False(state.InizializzazioneWorkspaceCompletata);

        Directory.CreateDirectory(root);
        Parallel.Invoke(
            () => { try { state.RiprovaInizializzazione(); } catch (Exception) { } },
            () => { try { state.ImpostaCartella(root); } catch (Exception) { } });

        Assert.True(state.InizializzazioneWorkspaceCompletata);
        Assert.True(state.CartellaValida);
    }

    [Fact]
    public void LockAbbandonato_SiRimuoveSoloSeLaSessioneCoincide()
    {
        // Correzione F: il lock creato da un tentativo scaduto va rimosso solo se porta la sessione di quel tentativo.
        var svc = new LockService("PC-X");
        var lockPath = Path.Combine(_dir, "testcase_1.json.lock");
        JsonIo.WriteAtomic(lockPath, new LockInfo
        {
            Operatore = "Operatore",
            AcquisitoIl = DateTime.Now,
            UltimoHeartbeat = DateTime.Now,
            DeviceId = "PC-X",
            SessionId = "sessione-abc",
        });

        svc.RimuoviLockAbbandonato(lockPath, "altra-sessione");
        Assert.True(File.Exists(lockPath));           // lock di un'altra sessione: intatto

        svc.RimuoviLockAbbandonato(lockPath, "sessione-abc");
        Assert.False(File.Exists(lockPath));          // lock orfano del tentativo scaduto: rimosso
    }
}
