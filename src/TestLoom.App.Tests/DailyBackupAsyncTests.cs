using System.Diagnostics;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// 6.4.1: il backup giornaliero (BackupService.CreaGiornalieroSeNecessario) non deve più bloccare né l'avvio né
/// la chiusura dell'app, e non deve competere per I/O con le primissime navigazioni dopo l'avvio/il cambio
/// cartella. Copre: AvviaBackupGiornalieroDopoAvvio non bloccante anche con un backup molto lento, pubblicazione
/// corretta con cartella stabile, nessuno zip pubblicato se i dati cambiano durante lo snapshot (con nuovo
/// tentativo pianificato), Dispose che non attende mai oltre un limite breve anche con hashing/lettura
/// realmente bloccanti, pulizia sicura dei .tmp orfani (solo propri o più vecchi di 24h), serializzazione con
/// il backup manuale, e "un solo backup al giorno". Nessun test dipende da sleep/tempi fissi lunghi: usa
/// segnali (TaskCompletionSource) e attese con timeout limitato.
/// </summary>
[Collection("BackupWriteGate")] // BackupService.WriteGate è statico: niente esecuzione parallela fra queste classi
public sealed class DailyBackupAsyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_BackupAsync_" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly TimeSpan _backupStartDelayOriginale;

    // Rete di sicurezza: se un'asserzione fallisse PRIMA di sbloccare l'hook, il thread di backup in
    // background resterebbe altrimenti bloccato per sempre (CancellationToken spesso None in questi test), e
    // dato che BackupService.WriteGate e' STATICO condividerebbe quel blocco con OGNI test successivo del
    // processo. Ogni hook usa questo timeout come limite assoluto, mai come meccanismo di sincronizzazione
    // primario del test (quello resta il segnale esplicito via TaskCompletionSource).
    private static readonly TimeSpan SicurezzaHook = TimeSpan.FromSeconds(30);

    // Radice locale di test (mai il vero %LOCALAPPDATA%): stesso meccanismo di override giа usato da
    // LocalTestIndexStore per i suoi test, esposto per il backup tramite AppState.TestLocalBackupBaseDirectory
    // (i test guidano il backup indirettamente via AppState.AvviaBackupGiornalieroDopoAvvio, non possono passare
    // il parametro localBaseDirectory di BackupService direttamente).
    private readonly string _localAppData = Path.Combine(Path.GetTempPath(), "TestLoom_BackupAsync_LocalAppData_" + Guid.NewGuid().ToString("N"));

    public DailyBackupAsyncTests()
    {
        _backupStartDelayOriginale = AppState.BackupStartDelay;
        // Il ritardo di produzione (60s, per non competere con le prime navigazioni) renderebbe questi test
        // lenti/inutili: AvviaBackupGiornalieroDopoAvvio resta comunque non bloccante indipendentemente dal
        // valore del ritardo, quindi azzerarlo qui non nasconde nulla di quello che i test verificano.
        AppState.BackupStartDelay = TimeSpan.Zero;
        AppState.TestLocalBackupBaseDirectory = _localAppData;
    }

    private DataPaths CreaWorkspace(string sotto = "data")
    {
        var dataRoot = Path.Combine(_root, sotto);
        var paths = new DataPaths(dataRoot);
        paths.EnsureStructure();
        // EnsureStructure() su Phase D non crea piu' operatori.json: lo scriviamo qui, come BackupScopeSafetyTests,
        // cosi' resta un file JSON tracciato semplice da mutare/verificare senza dover passare dal materializer
        // Phase D (fuori perimetro per questi test, che riguardano solo l'orchestrazione async del backup).
        File.WriteAllText(paths.OperatoriFile, "[]");
        return paths;
    }

    /// <summary>Cartella backup LOCALE (decisione utente 6.4.2: non più paths.BackupDir, condiviso e non più
    /// scritto) per il workspace di test corrente, sotto la radice locale di test _localAppData.</summary>
    private string BackupDirLocale(DataPaths paths) =>
        WorkspaceLocalPaths.GetBackupDirectory(paths.Root, _localAppData);

    private AppConfigService CreaConfigService(string dataRoot, string sotto = "local")
    {
        var configPath = Path.Combine(_root, sotto, "config.json");
        var configService = new AppConfigService(configPath);
        configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it" });
        return configService;
    }

    [Fact]
    public void CostruzioneAppState_NonAvviaAlcunBackup()
    {
        var paths = CreaWorkspace();
        var configService = CreaConfigService(paths.Root);

        // Revisione indipendente su fc7e51b, punto 2: il backup giornaliero non nasce più dentro
        // InizializzaCartella/il costruttore, per non competere per I/O con le primissime navigazioni. Parte
        // SOLO quando il chiamante (App.axaml.cs dopo MainWindow.Show(), qui il test) lo richiede esplicitamente.
        using var state = new AppState(configService);
        Assert.Equal(DailyBackupState.NonAvviato, state.BackupGiornalieroStato.State);
        Assert.Null(state.BackupGiornalieroTaskPerTest);
        // Nessun backup e' mai partito: la cartella locale non e' ancora stata creata (a differenza della
        // vecchia paths.BackupDir condivisa, gia' creata da EnsureStructure()).
        var backupDir = BackupDirLocale(paths);
        Assert.False(Directory.Exists(backupDir) &&
                     Directory.EnumerateFiles(backupDir, "TestLoom_snapshot_*.zip", SearchOption.TopDirectoryOnly).Any());
    }

    [Fact]
    public void AvviaBackupGiornalieroDopoAvvio_NonBlocca_AncheSeIlBackupEMoltoLento()
    {
        var paths = CreaWorkspace();
        var configService = CreaConfigService(paths.Root);

        var backupPuoProcedere = new TaskCompletionSource();
        BackupService.TestSlowdownHook = ct => backupPuoProcedere.Task.WaitAsync(SicurezzaHook, ct);
        try
        {
            using var state = new AppState(configService);

            var sw = Stopwatch.StartNew();
            state.AvviaBackupGiornalieroDopoAvvio();
            sw.Stop();

            // Il backup lento (bloccato sull'hook) non deve MAI far attendere il chiamante: qualche secondo di
            // margine ampio rispetto alla soglia di regressione reale (l'hook sblocca solo quando lo diciamo noi).
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"AvviaBackupGiornalieroDopoAvvio ha atteso il backup: {sw.Elapsed}");

            // Il backup e' partito (stato != NonAvviato) ma non e' ancora concluso: ancora bloccato sull'hook.
            Assert.True(WaitForState(state, s => s.State != DailyBackupState.NonAvviato));
            Assert.Equal(DailyBackupState.InCorso, state.BackupGiornalieroStato.State);

            backupPuoProcedere.SetResult();
            Assert.True(AttendiTask(state.BackupGiornalieroTaskPerTest, Timeout), "Il backup giornaliero non si e' concluso in tempo dopo lo sblocco.");
            Assert.Equal(DailyBackupState.Completato, state.BackupGiornalieroStato.State);
            Assert.Single(Directory.EnumerateFiles(BackupDirLocale(paths), "TestLoom_snapshot_*.zip"));
        }
        finally { BackupService.TestSlowdownHook = null; }
    }

    [Fact]
    public void Backup_ConCartellaStabile_PubblicaUnoZipValido()
    {
        var paths = CreaWorkspace();
        var configService = CreaConfigService(paths.Root);

        using var state = new AppState(configService);
        state.AvviaBackupGiornalieroDopoAvvio();
        Assert.True(AttendiTask(state.BackupGiornalieroTaskPerTest, Timeout), "Il backup giornaliero non si e' concluso in tempo.");
        Assert.Equal(DailyBackupState.Completato, state.BackupGiornalieroStato.State);

        var zip = Directory.EnumerateFiles(BackupDirLocale(paths), "TestLoom_snapshot_*.zip").Single();
        using var archivio = ZipFile.OpenRead(zip);
        var entries = archivio.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("operatori.json", entries);
        Assert.Contains("snapshot-manifest.txt", entries);
        Assert.Empty(Directory.EnumerateFiles(BackupDirLocale(paths), "*.tmp"));
    }

    [Fact]
    public async Task Backup_DatiCambianoDuranteLoSnapshot_NessunoZipPubblicato_TmpRimosso_RitentaEPoiPubblica()
    {
        var paths = CreaWorkspace();
        var configService = CreaConfigService(paths.Root);

        var primoTentativoInCorso = new TaskCompletionSource();
        var mutazioneFatta = new TaskCompletionSource();
        var hookChiamate = 0;

        BackupService.TestSlowdownHook = async ct =>
        {
            var n = Interlocked.Increment(ref hookChiamate);
            if (n == 1)
            {
                // Sblocca il test principale, che a questo punto muta un file tracciato mentre lo snapshot
                // "before" e' gia' stato catturato: BackupService.Crea deve rilevare la differenza before/after
                // e non pubblicare nulla.
                primoTentativoInCorso.TrySetResult();
                await mutazioneFatta.Task.WaitAsync(SicurezzaHook, ct);
            }
        };

        var precedenteMax = AppState.BackupMaxAttempts;
        var precedenteDelay = AppState.BackupRetryDelay;
        AppState.BackupMaxAttempts = 2;
        AppState.BackupRetryDelay = TimeSpan.FromMilliseconds(50);
        try
        {
            using var state = new AppState(configService);
            state.AvviaBackupGiornalieroDopoAvvio();

            await primoTentativoInCorso.Task.WaitAsync(Timeout);
            File.WriteAllText(paths.OperatoriFile, "[{\"Nome\":\"mutato-durante-backup\"}]");
            mutazioneFatta.SetResult();

            Assert.True(AttendiTask(state.BackupGiornalieroTaskPerTest, Timeout), "Il backup (con ritentativo) non si e' concluso in tempo.");

            // Il secondo tentativo trova la cartella stabile (nessuna mutazione durante quel giro) e pubblica.
            Assert.Equal(DailyBackupState.Completato, state.BackupGiornalieroStato.State);
            Assert.Single(Directory.EnumerateFiles(BackupDirLocale(paths), "TestLoom_snapshot_*.zip"));
            Assert.Empty(Directory.EnumerateFiles(BackupDirLocale(paths), "*.tmp"));
            Assert.True(hookChiamate >= 2, "Ci si aspettava almeno un ritentativo dopo il fallimento per dati cambiati.");
        }
        finally
        {
            BackupService.TestSlowdownHook = null;
            AppState.BackupMaxAttempts = precedenteMax;
            AppState.BackupRetryDelay = precedenteDelay;
        }
    }

    [Fact]
    public void Dispose_NonAttendeOltreIlLimite_AncheSeIlBackupNonSiSbloccaSubito()
    {
        var paths = CreaWorkspace();
        var configService = CreaConfigService(paths.Root);

        var maiSbloccato = new TaskCompletionSource();
        BackupService.TestSlowdownHook = ct => maiSbloccato.Task.WaitAsync(SicurezzaHook, ct);
        try
        {
            var state = new AppState(configService);
            state.AvviaBackupGiornalieroDopoAvvio();
            Assert.True(WaitForState(state, s => s.State == DailyBackupState.InCorso));
            var task = state.BackupGiornalieroTaskPerTest;
            Assert.NotNull(task);

            var sw = Stopwatch.StartNew();
            state.Dispose();
            sw.Stop();

            // Revisione indipendente su fc7e51b, punto 1: Dispose (MainWindow.OnClosing -> MainViewModel.Dispose
            // -> AppState.Dispose, tutti sincroni sul thread UI) non deve MAI attendere il backup a lungo. Qui
            // usiamo un margine piu' ampio del limite interno (~400ms) per assorbire la varianza della CI, pur
            // restando MOLTO al di sotto del timeout dell'hook (30s) o della durata reale di un backup (minuti).
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5), $"Dispose ha atteso troppo il backup annullato: {sw.Elapsed}");

            // Il CancellationToken lo sblocca comunque quasi subito: il thread (IsBackground=true) termina a
            // breve anche se Dispose non lo ha aspettato fino in fondo.
            Assert.True(AttendiTask(task, Timeout), "Il thread di backup non e' terminato dopo l'annullamento.");
            Assert.Empty(Directory.EnumerateFiles(BackupDirLocale(paths), "*.tmp"));
        }
        finally { BackupService.TestSlowdownHook = null; }
    }

    [Fact]
    public async Task Dispose_ConHashingSnapshotRealmenteBloccanteAMeta_RitornaEntroIlLimite()
    {
        var paths = CreaWorkspace();
        // File abbastanza grande da avere piu' di un blocco da 128 KB: l'hashing a blocchi (ComputeSha256Blocked,
        // che sostituisce il precedente SHA256.HashData(stream) sull'intero file) deve poter essere interrotto
        // FRA un blocco e l'altro, non solo fra un file e l'altro (revisione indipendente su fc7e51b, punto 1a).
        // JSON VALIDO (array con solo whitespace dentro): un operatori.json illeggibile verrebbe altrimenti
        // "riparato"/svuotato dal bootstrap di AppState prima ancora che il backup possa leggerlo.
        File.WriteAllText(paths.OperatoriFile, "[" + new string(' ', 300 * 1024) + "]");
        var configService = CreaConfigService(paths.Root);

        var bloccoRaggiunto = new TaskCompletionSource();
        var maiSbloccato = new TaskCompletionSource();
        BackupService.TestPerBlockHook = ct =>
        {
            bloccoRaggiunto.TrySetResult();
            return maiSbloccato.Task.WaitAsync(SicurezzaHook, ct);
        };
        try
        {
            var state = new AppState(configService);
            state.AvviaBackupGiornalieroDopoAvvio();
            await bloccoRaggiunto.Task.WaitAsync(Timeout);
            var task = state.BackupGiornalieroTaskPerTest; // catturata PRIMA di Dispose: Dispose azzera il campo

            var sw = Stopwatch.StartNew();
            state.Dispose();
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5), $"Dispose ha atteso troppo durante l'hashing bloccante: {sw.Elapsed}");
            Assert.True(AttendiTask(task, Timeout), "Il backup non e' terminato dopo l'annullamento durante l'hashing.");
        }
        finally { BackupService.TestPerBlockHook = null; }
    }

    [Fact]
    public void PulisciTemporanei_RimuoveSoloIPropriOrfaniOQuelliPiuVecchiDi24h()
    {
        var paths = CreaWorkspace();
        Directory.CreateDirectory(paths.BackupDir);
        var now = new DateTime(2026, 9, 27, 12, 0, 0);
        var mioDeviceId = DeviceIdentityService.GetOrCreate();

        // Proprio, recente (dentro la grazia di 5 min): potrebbe essere un secondo processo TestLoom su QUESTO PC
        // ancora attivamente in scrittura -> non va toccato.
        var mioFresco = Path.Combine(paths.BackupDir, $"TestLoom_snapshot_20260927.{mioDeviceId}.tmp");
        File.WriteAllText(mioFresco, "x");
        File.SetLastWriteTime(mioFresco, now.AddMinutes(-1));

        // Proprio, oltre la grazia: certamente un orfano da una chiusura brusca precedente -> rimosso subito,
        // senza aspettare le 24h del fallback generico.
        var mioOrfano = Path.Combine(paths.BackupDir, $"TestLoom_snapshot_20260926.{mioDeviceId}.tmp");
        File.WriteAllText(mioOrfano, "x");
        File.SetLastWriteTime(mioOrfano, now.AddMinutes(-10));

        // Di un ALTRO PC, recente: la cartella e' CONDIVISA, potrebbe essere legittimamente in scrittura in
        // questo momento -> non va MAI toccato prima di 24h.
        var altroFrescoRecente = Path.Combine(paths.BackupDir, "TestLoom_snapshot_20260927.altro-device-id-xyz.tmp");
        File.WriteAllText(altroFrescoRecente, "x");
        File.SetLastWriteTime(altroFrescoRecente, now.AddHours(-2));

        // Di un ALTRO PC, oltre 24h: fallback di sicurezza pre-esistente, invariato.
        var altroVecchio = Path.Combine(paths.BackupDir, "TestLoom_snapshot_20260101.altro-device-id-xyz.tmp");
        File.WriteAllText(altroVecchio, "x");
        File.SetLastWriteTime(altroVecchio, now.AddDays(-2));

        var rimossi = BackupService.PulisciTemporanei(paths.BackupDir, now);

        Assert.Equal(2, rimossi);
        Assert.True(File.Exists(mioFresco), "Un .tmp proprio ma recente potrebbe essere in scrittura: non va toccato.");
        Assert.False(File.Exists(mioOrfano), "Un .tmp proprio e chiaramente vecchio e' un orfano: va rimosso subito.");
        Assert.True(File.Exists(altroFrescoRecente), "Il .tmp di un altro PC potrebbe essere in scrittura: non va MAI toccato prima di 24h.");
        Assert.False(File.Exists(altroVecchio), "Fallback esistente: un .tmp di un altro PC oltre 24h viene comunque ripulito.");
    }

    /// <summary>Mentre un backup di questo processo tiene il WriteGate, il suo .tmp può restare senza scritture
    /// oltre la grazia di 5 minuti (riverifica finale di tutti i file prima del rename su cartelle grandi): la
    /// retention concorrente (manutenzione periodica) non deve cancellarlo.</summary>
    [Fact]
    public void PulisciTemporanei_ConBackupInCorsoNelProcesso_NonTocca_IlPropriTmpOltreLaGrazia()
    {
        var paths = CreaWorkspace();
        Directory.CreateDirectory(paths.BackupDir);
        var now = new DateTime(2026, 9, 27, 12, 0, 0);
        var mioInCorso = Path.Combine(paths.BackupDir, $"TestLoom_snapshot_20260927.{DeviceIdentityService.GetOrCreate()}.tmp");
        File.WriteAllText(mioInCorso, "x");
        File.SetLastWriteTime(mioInCorso, now.AddMinutes(-30));

        var gate = (SemaphoreSlim)typeof(BackupService)
            .GetField("WriteGate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        Assert.True(gate.Wait(Timeout), "WriteGate non acquisibile nel test.");
        try
        {
            Assert.Equal(0, BackupService.PulisciTemporanei(paths.BackupDir, now));
            Assert.True(File.Exists(mioInCorso));
        }
        finally { gate.Release(); }

        // Senza backup in corso lo stesso file è un orfano e viene rimosso.
        Assert.Equal(1, BackupService.PulisciTemporanei(paths.BackupDir, now));
        Assert.False(File.Exists(mioInCorso));
    }

    [Fact]
    public void CreaGiornalieroSeNecessario_SecondaChiamataStessoGiorno_NonProducaSecondoZip()
    {
        var paths = CreaWorkspace();
        var oggi = new DateTime(2026, 9, 20, 9, 0, 0);

        var primo = BackupService.CreaGiornalieroSeNecessario(paths, oggi, CancellationToken.None, _localAppData);
        var secondo = BackupService.CreaGiornalieroSeNecessario(paths, oggi.AddHours(3), CancellationToken.None, _localAppData);

        Assert.NotNull(primo);
        Assert.Null(secondo);
        Assert.Single(Directory.EnumerateFiles(BackupDirLocale(paths), "TestLoom_snapshot_*.zip"));
    }

    /// <summary>Decisione utente 6.4.2: il giornaliero automatico crea un nuovo snapshot SOLO se i dati sono
    /// cambiati dall'ultimo pubblicato (fingerprint SHA-256 per-file, senza mtime). Qui i dati NON cambiano fra
    /// due giorni diversi: il secondo giorno non deve produrre un secondo zip nonostante il prefisso di data sia
    /// diverso (a differenza di CreaGiornalieroSeNecessario_SecondaChiamataStessoGiorno_NonProducaSecondoZip,
    /// che copre lo stesso giorno).</summary>
    [Fact]
    public void CreaGiornalieroSeNecessario_GiornoSuccessivoDatiInvariati_NonProducaSecondoZip()
    {
        var paths = CreaWorkspace();
        var giorno1 = new DateTime(2026, 9, 20, 9, 0, 0);
        var giorno2 = new DateTime(2026, 9, 21, 9, 0, 0);

        var primo = BackupService.CreaGiornalieroSeNecessario(paths, giorno1, CancellationToken.None, _localAppData);
        var secondo = BackupService.CreaGiornalieroSeNecessario(paths, giorno2, CancellationToken.None, _localAppData);

        Assert.NotNull(primo);
        Assert.Null(secondo);
        Assert.Single(Directory.EnumerateFiles(BackupDirLocale(paths), "TestLoom_snapshot_*.zip"));
    }

    /// <summary>Simmetrico al test precedente: se un JSON tracciato cambia CONTENUTO fra i due giorni, il
    /// giornaliero del secondo giorno deve invece pubblicare un nuovo zip.</summary>
    [Fact]
    public void CreaGiornalieroSeNecessario_GiornoSuccessivoContenutoCambiato_ProducaSecondoZip()
    {
        var paths = CreaWorkspace();
        var giorno1 = new DateTime(2026, 9, 20, 9, 0, 0);
        var giorno2 = new DateTime(2026, 9, 21, 9, 0, 0);

        var primo = BackupService.CreaGiornalieroSeNecessario(paths, giorno1, CancellationToken.None, _localAppData);
        File.WriteAllText(paths.OperatoriFile, "[{\"Nome\":\"nuovo-operatore\"}]");
        var secondo = BackupService.CreaGiornalieroSeNecessario(paths, giorno2, CancellationToken.None, _localAppData);

        Assert.NotNull(primo);
        Assert.NotNull(secondo);
        Assert.Equal(2, Directory.EnumerateFiles(BackupDirLocale(paths), "TestLoom_snapshot_*.zip").Count());
    }

    /// <summary>OneDrive/SharePoint può toccare la data di un file sincronizzato senza cambiarne il contenuto
    /// (§ commento OwnDeviceOrphanGrace): il fingerprint è deliberatamente SENZA mtime, quindi un tocco della
    /// sola data non deve far scattare un nuovo zip.</summary>
    [Fact]
    public void CreaGiornalieroSeNecessario_SoloDataToccataSenzaCambioContenuto_NonProducaSecondoZip()
    {
        var paths = CreaWorkspace();
        var giorno1 = new DateTime(2026, 9, 20, 9, 0, 0);
        var giorno2 = new DateTime(2026, 9, 21, 9, 0, 0);

        var primo = BackupService.CreaGiornalieroSeNecessario(paths, giorno1, CancellationToken.None, _localAppData);
        File.SetLastWriteTimeUtc(paths.OperatoriFile, DateTime.UtcNow); // stesso contenuto, solo mtime diverso
        var secondo = BackupService.CreaGiornalieroSeNecessario(paths, giorno2, CancellationToken.None, _localAppData);

        Assert.NotNull(primo);
        Assert.Null(secondo);
        Assert.Single(Directory.EnumerateFiles(BackupDirLocale(paths), "TestLoom_snapshot_*.zip"));
    }

    /// <summary>Il manuale ("Crea ora") è una richiesta esplicita dell'utente e deve creare SEMPRE, anche se i
    /// dati non sono cambiati dall'ultimo snapshot (a differenza del giornaliero automatico).</summary>
    [Fact]
    public void CreaOra_DatiInvariatiDallUltimoSnapshot_CreaComunque()
    {
        var paths = CreaWorkspace();
        var t1 = new DateTime(2026, 9, 20, 9, 0, 0);
        var t2 = new DateTime(2026, 9, 20, 9, 5, 0);

        var primo = BackupService.CreaOra(paths, t1, _localAppData);
        var secondo = BackupService.CreaOra(paths, t2, _localAppData); // nessuna mutazione fra i due

        Assert.True(File.Exists(primo));
        Assert.True(File.Exists(secondo));
        Assert.NotEqual(primo, secondo);
        Assert.Equal(2, Directory.EnumerateFiles(BackupDirLocale(paths), "TestLoom_snapshot_*.zip").Count());
    }

    /// <summary>Retention 6.4.2: solo gli ultimi 3 snapshot locali vengono conservati.</summary>
    [Fact]
    public void ApplicaRetention_ConservaSoloGliUltimiTreSnapshotLocali()
    {
        var paths = CreaWorkspace();
        var backupDir = BackupDirLocale(paths);
        Directory.CreateDirectory(backupDir);
        for (var giorno = 1; giorno <= 5; giorno++)
            File.WriteAllText(Path.Combine(backupDir, $"TestLoom_snapshot_202609{giorno:00}.zip"), "x");

        BackupService.ApplicaRetention(backupDir, new DateTime(2026, 9, 5, 12, 0, 0));

        var restanti = Directory.EnumerateFiles(backupDir, "TestLoom_snapshot_*.zip")
            .Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(3, restanti.Count);
        Assert.Equal(new[] { "TestLoom_snapshot_20260903.zip", "TestLoom_snapshot_20260904.zip", "TestLoom_snapshot_20260905.zip" }, restanti);
    }

    /// <summary>Decisione utente 6.4.2, criterio centrale: nessun file (zip, temporary o fingerprint) viene mai
    /// scritto nella vecchia cartella CONDIVISA &lt;Root&gt;/backup — né dal giornaliero né dal manuale.</summary>
    [Fact]
    public void CreaOra_NonScriveMaiNellaCartellaBackupCondivisa()
    {
        var paths = CreaWorkspace();
        BackupService.CreaOra(paths, new DateTime(2026, 9, 20, 9, 0, 0), _localAppData);

        Assert.False(Directory.Exists(paths.BackupDir),
            "La vecchia cartella backup condivisa non deve mai essere creata/scritta dalla 6.4.2.");
    }

    [Fact]
    public async Task CreaOra_MentreIlGiornalieroTieneIlGate_FalliscaSubitoConMessaggioChiaro()
    {
        var paths = CreaWorkspace();
        var giornalieroPuoProcedere = new TaskCompletionSource();
        var hookEntrato = new TaskCompletionSource();
        BackupService.TestSlowdownHook = ct =>
        {
            // Segnale INCONDIZIONATO, mai racy come ispezionare Task.Status (WaitingForActivation e' gia' vero
            // per un TaskCompletionSource appena creato, prima ancora che l'hook parta davvero).
            hookEntrato.TrySetResult();
            return giornalieroPuoProcedere.Task.WaitAsync(SicurezzaHook, ct);
        };
        try
        {
            var giornalieroTask = Task.Run(() => BackupService.CreaGiornalieroSeNecessario(paths, new DateTime(2026, 9, 21, 8, 0, 0), CancellationToken.None, _localAppData));

            // Attende che il giornaliero abbia davvero preso il gate (e sia fermo sull'hook) prima di provare il manuale.
            await hookEntrato.Task.WaitAsync(Timeout);

            var ex = Assert.Throws<IOException>(() => BackupService.CreaOra(paths, new DateTime(2026, 9, 21, 8, 0, 5), _localAppData));
            Assert.Contains("backup giornaliero", ex.Message, StringComparison.OrdinalIgnoreCase);

            giornalieroPuoProcedere.SetResult();
            var pubblicatoDalGiornaliero = await giornalieroTask.WaitAsync(Timeout);
            Assert.NotNull(pubblicatoDalGiornaliero);

            // Con il gate libero il manuale ora funziona normalmente.
            var manuale = BackupService.CreaOra(paths, new DateTime(2026, 9, 21, 8, 1, 0), _localAppData);
            Assert.True(File.Exists(manuale));
        }
        finally { BackupService.TestSlowdownHook = null; }
    }

    [Fact]
    public void WorkflowTransactionService_IsActiveAnywhere_VisibileDaUnAltroThread()
    {
        var paths = new DataPaths(Path.Combine(_root, "tx"));
        paths.EnsureStructure();

        Assert.False(WorkflowTransactionService.IsActiveAnywhere);
        using var tx = WorkflowTransactionService.Begin(paths, "test-cross-thread");
        try
        {
            // IsActive (AsyncLocal) vale solo per il flusso che ha aperto la transazione: un Thread NUOVO,
            // separato, e' esattamente lo scenario che IsActiveAnywhere deve coprire e IsActive no.
            var visibileDaAltroThread = false;
            var altroThread = new Thread(() => visibileDaAltroThread = WorkflowTransactionService.IsActiveAnywhere);
            altroThread.Start();
            Assert.True(altroThread.Join(Timeout));
            Assert.True(visibileDaAltroThread);
        }
        finally { tx.Rollback(); }

        Assert.False(WorkflowTransactionService.IsActiveAnywhere);
    }

    private static bool WaitForState(AppState state, Func<DailyBackupStatus, bool> predicate)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Timeout)
        {
            if (predicate(state.BackupGiornalieroStato)) return true;
            Thread.Sleep(10);
        }
        return predicate(state.BackupGiornalieroStato);
    }

    private static bool AttendiTask(Task? task, TimeSpan timeout)
    {
        if (task == null) return false;
        try { return task.Wait(timeout); }
        catch (AggregateException) { return true; } // il task e' comunque terminato (con eccezione osservata)
    }

    public void Dispose()
    {
        AppState.BackupStartDelay = _backupStartDelayOriginale;
        AppState.TestLocalBackupBaseDirectory = null;
        BackupService.TestSlowdownHook = null;
        BackupService.TestPerBlockHook = null;
        BackupService.TestCopyBlockHook = null;
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        try { if (Directory.Exists(_localAppData)) Directory.Delete(_localAppData, recursive: true); } catch { }
    }
}
