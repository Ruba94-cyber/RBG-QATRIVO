using System.Diagnostics;
using System.Threading;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Stato applicativo condiviso: un'unica istanza creata all'avvio e passata ai ViewModel.</summary>
public class AppState : IDisposable
{
    private static readonly TimeSpan TestCaseDeepReconciliationInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StorageMaintenanceInterval = TimeSpan.FromHours(12);
    private TestCaseChangeTracker? _testCaseChangeTracker;
    private JournalChangeFeedService? _testJournalFeed;
    private bool _testCaseNeedsStartupReconciliation;
    private readonly object _storageMaintenanceGate = new();
    private DateTime _lastStorageMaintenanceUtc = DateTime.MinValue;
    private SystemAuditService? _systemAudit;
    private readonly HashSet<Guid> _forkNotificationsAttempted = new();
    // Cache del giro di notifica WU da decidere (6.6.0): ricalcolata solo quando cambiano impronta catalogo o lista Test.
    private string? _wuDaDecidereImpronta;
    private object? _wuDaDecidereTests;
    private IReadOnlyList<(string WorkingUnitRev, string Firma)> _wuDaDecidereCorrenti = Array.Empty<(string, string)>();
    private readonly bool _runBackgroundMaintenance;
    private Task? _backgroundMaintenanceTask;
    // Annullamento cooperativo della manutenzione in background alla chiusura: fra una fase e l'altra, vedi Dispose.
    private readonly CancellationTokenSource _annullaManutenzione = new();
    private CancellationTokenSource? _backupCts;
    private Task? _backgroundBackupTask;
    private bool _disposed;

    /// <summary>Numero massimo di ritentativi del backup giornaliero NELLA SESSIONE corrente quando è rinviato
    /// da una condizione transitoria (Unit of Work attiva, dati cambiati durante lo snapshot). Esaurito il
    /// budget la sessione rinuncia: il prossimo avvio/cambio cartella lo ritenta da zero (nessun marker
    /// "fallito per oggi" persistito, vedi CreaGiornalieroSeNecessario che pubblica solo se il prefisso del
    /// giorno non esiste già). Campo statico (non istanza) SOLO perché il backup viene avviato in automatico
    /// dal costruttore/InizializzaCartella, prima che un test possa impostare un valore su un'istanza già
    /// creata: stesso motivo di <see cref="BackupRetryDelay"/> sotto.</summary>
    internal static int BackupMaxAttempts = 3;

    /// <summary>Attesa fra un ritentativo e l'altro del backup giornaliero. Sovrascrivibile SOLO dai test
    /// (campo internal statico) per non dover attendere minuti reali quando si verifica il rinvio/ritentativo;
    /// il valore di produzione resta invariato a meno che un test lo cambi esplicitamente (e lo ripristini).</summary>
    internal static TimeSpan BackupRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>Sovrascrive SOLO per i test la radice locale (normalmente %LOCALAPPDATA%) sotto cui
    /// BackupService colloca la cartella backup per-workspace (decisione utente 6.4.2: backup solo locali,
    /// vedi <see cref="WorkspaceLocalPaths"/>). Stesso motivo/pattern di <see cref="BackupRetryDelay"/>: un
    /// campo statico perché il backup giornaliero può partire dal costruttore/InizializzaCartella prima che un
    /// test possa impostarlo su un'istanza già creata. Null (default) in produzione: nessun comportamento
    /// diverso rispetto a prima di questo campo.</summary>
    internal static string? TestLocalBackupBaseDirectory;
    private IReadOnlyDictionary<Guid, string> _nomiTestTemplate = new Dictionary<Guid, string>();

    /// <summary>Canale opzionale di avanzamento verso la schermata di avvio. Attivo solo durante il bootstrap
    /// innescato dal costruttore quando chi crea AppState passa un IProgress: dopo quel bootstrap viene
    /// riportato a <see cref="BootstrapProgressReporter.Nessuno"/> così i refresh successivi (timer periodico,
    /// cambio cartella da Impostazioni) restano no-op invece di continuare a scrivere verso una schermata di
    /// avvio ormai chiusa.</summary>
    private BootstrapProgressReporter _progress = BootstrapProgressReporter.Nessuno;

    public AppConfigService ConfigService { get; }
    public AppConfig Config { get; private set; }

    public DataPaths Paths { get; private set; } = null!;
    public TestCaseRepository TestCases { get; private set; } = null!;
    public MasterDataStore Master { get; private set; } = null!;
    public NonConformitaRepository NonConformita { get; private set; } = null!;
    public ChangeRequestRepository ChangeRequests { get; private set; } = null!;
    public SegnalazioneRepository Segnalazioni { get; private set; } = null!;
    /// <summary>Journal per-device dei commit NC/CR/Segnalazioni (R9); null se non inizializzabile.</summary>
    public DomainRecordJournalService? RecordJournal { get; private set; }
    public NotificaRepository Notifiche { get; private set; } = null!;
    // Internal setter lets lock tests inject a monotonic clock before any editor creates a session.
    public LockService Locks { get; internal set; } = new();

    public BozzaAutosaveService BozzaAutosave { get; }
    public StorageMaintenanceResult? UltimaManutenzioneStorage { get; private set; }

    /// <summary>Stato del backup giornaliero eseguito in background (6.4.1). Consultabile da Impostazioni →
    /// Stato e manutenzione; mai un popup. Vedi <see cref="DailyBackupStatus"/>.</summary>
    public DailyBackupStatus BackupGiornalieroStato { get; private set; } = DailyBackupStatus.NonAvviato;

    /// <summary>Notifica un cambio di <see cref="BackupGiornalieroStato"/>. Arriva SEMPRE dal thread di backup
    /// dedicato, mai dal thread UI (stesso contratto di <see cref="CambiamentiSuDiscoRilevati"/>): chi si
    /// iscrive deve marshalling proprio (Dispatcher.UIThread.Post).</summary>
    public event Action? BackupGiornalieroStatoCambiato;

    /// <summary>Solo per i test: la Task che rappresenta l'esecuzione corrente del backup giornaliero in
    /// background, se presente. Permette di attendere con timeout limitato invece di sleep arbitrari.</summary>
    internal Task? BackupGiornalieroTaskPerTest => _backgroundBackupTask;
    public bool InizializzazioneWorkspaceCompletata { get; private set; }
    public string? ErroreInizializzazioneWorkspace { get; private set; }
    private EsitoStatoCondivisione _statoCondivisione = new()
    {
        Stato = Models.StatoCondivisione.NonDisponibile,
        Messaggio = "Cartella non configurata.",
        VerificatoIl = DateTime.Now
    };

    /// <summary>Stato della cartella condivisa. Una perdita di scrittura rilevata in questa sessione (6.6.1, d) porta lo
    /// stato operativo in sola lettura subito, senza attendere il giro periodico: vedi SharedWriteLossMonitor.</summary>
    public EsitoStatoCondivisione StatoCondivisione
    {
        get => _statoCondivisione.Stato == Models.StatoCondivisione.Operativa
            ? SharedWriteLossMonitor.EsitoPerdita(Paths?.Root) ?? _statoCondivisione
            : _statoCondivisione;
        private set => _statoCondivisione = value;
    }

    public Operatore? OperatoreCorrente { get; private set; }

    /// <summary>PUNTO UNICO da cui l'app legge il ruolo attivo: sempre RuoloEffettivo (sigillo verificato),
    /// mai Ruolo grezzo — vedi Operatore.RuoloEffettivo/RuoloSigilloService (piano 6.4.2, §4b).</summary>
    public RuoloOperatore RuoloAttivo => OperatoreCorrente?.RuoloEffettivo ?? RuoloOperatore.Viewer;
    public bool EMaster => RuoloAttivo == RuoloOperatore.Master;
    public bool EViewer => RuoloAttivo == RuoloOperatore.Viewer;
    public bool PuoScrivere => InizializzazioneWorkspaceCompletata && !EViewer &&
        StatoCondivisione.Stato == Models.StatoCondivisione.Operativa &&
        Paths != null && !WorkspaceReadOnlyService.IsEnabled(Paths.Root);

    /// <summary>Modalità di accesso effettiva della cartella dati corrente (piano 6.4.2, §4b): "senza PIN" solo
    /// se il file accesso-config.json esiste, è leggibile e il suo sigillo è valido — vedi
    /// RuoloSigilloService.SenzaPinEffettivo. Ricaricata ad ogni RileggiDaDisco, come StatoCondivisione.</summary>
    public bool AccessoSenzaPin { get; private set; }

    private volatile SharedFolderHealthReport? _saluteCartellaCondivisa;

    /// <summary>Ultima valutazione non mutante della cartella condivisa (SharedFolderHealthMonitor), aggiornata dal giro
    /// periodico in background. Alimenta solo l'avviso di barra: non entra nel gate di scrittura, che resta
    /// WorkspaceWritePolicy/PuoScrivere. Null finché non esiste un operatore attivo (primo avvio).</summary>
    public SharedFolderHealthReport? SaluteCartellaCondivisa => _saluteCartellaCondivisa;

    public event Action? DatiRicaricati;

    /// <summary>Inoltra TestCaseChangeTracker.ModificaRilevata (C4, diagnosi_sync/diagnosi.md): un evento del
    /// FileSystemWatcher che prima veniva solo accumulato e consumato al giro successivo del timer da 60s.
    /// Sopravvive a InizializzaCartella/ImpostaCartella perché è definito qui, non sul tracker che viene
    /// ricreato: il sottoscrittore (MainViewModel) si iscrive una volta sola nel costruttore. Arriva SEMPRE
    /// dal thread del watcher, mai dal thread UI: chi si iscrive deve fare il proprio marshalling.</summary>
    public event Action? CambiamentiSuDiscoRilevati;

    /// <summary>Cosa NON ricaricare durante un refresh in background perché un editor lo sta modificando (C5,
    /// diagnosi_sync/diagnosi.md). Prima di questa aggiunta MainViewModel saltava l'INTERO giro di
    /// aggiornamento se un qualsiasi editor aveva modifiche non salvate: un ritardo illimitato per TUTTI gli
    /// altri record, anche quelli che l'utente non stava toccando. Ogni editor di questo repository lavora
    /// sempre su un CLONE JSON del proprio record — mai su un riferimento condiviso alla cache
    /// (TestCaseEditorViewModel.TestCase = Clona(...), ChangeRequestEditorViewModel.ChangeRequest = Clona(...),
    /// MasterDataEditorViewModelBase&lt;T&gt;.Clona(Items)) — quindi un refresh in background non può MAI
    /// sovrascrivere o perdere quel lavoro non salvato, a prescindere da cosa venga ricaricato qui: il vincolo
    /// di sicurezza dati è già garantito strutturalmente. Le esclusioni sotto sono comunque applicate, per
    /// prudenza in più e per UX: il record sotto modifica non deve ricomparire/sparire da FileConProblemi o
    /// cambiare stato proprio mentre l'utente lo guarda.</summary>
    public sealed record RefreshEsclusioni
    {
        public static readonly RefreshEsclusioni Nessuna = new();

        /// <summary>Percorso su disco del TestCase con InModifica=true. Escluso solo dal drain INCREMENTALE
        /// guidato da watcher/journal-feed (RileggiTestCases, ramo "changed"): la riconciliazione COMPLETA
        /// (full scan periodico raro, ogni 6h, o al primo avvio del watcher) rilegge comunque tutti i path da
        /// disco, compreso questo — resta sicura per lo stesso motivo (clone, mai riferimento condiviso), ed
        /// escluderlo anche lì richiederebbe di rendere TestCaseRepository capace di un full-scan parziale,
        /// fuori perimetro per questa correzione.</summary>
        public string? PercorsoTestCaseProtetto { get; init; }

        /// <summary>Vero quando una Change Request ha modifiche non salvate: salta ChangeRequests.RefreshIndex()
        /// per l'intero repository. A differenza dei Test, ChangeRequestRepository non espone un equivalente
        /// di RefreshPaths a grana fine, quindi l'esclusione qui è a livello di intero repository invece che
        /// di singolo record — comunque "gli altri" (Test, Non Conformità, Segnalazioni) continuano ad
        /// aggiornarsi normalmente.</summary>
        public bool EscludiChangeRequest { get; init; }
    }

    /// <param name="progress">Canale opzionale (IProgress&lt;BootstrapProgress&gt;) per riportare l'avanzamento
    /// del bootstrap a una schermata di avvio. Parametro facoltativo aggiunto in coda apposta per non rompere
    /// nessuna delle firme esistenti: sia `new AppState()` sia `new AppState(configService, runBackgroundMaintenance: false)`,
    /// usate da moltissimi test, continuano a compilare ed eseguire identiche a prima.</param>
    public AppState(AppConfigService? configService = null, bool runBackgroundMaintenance = true, IProgress<BootstrapProgress>? progress = null)
    {
        _progress = progress != null ? new BootstrapProgressReporter(progress) : BootstrapProgressReporter.Nessuno;
        _progress.Fase(FaseBootstrap.Avvio, "Avvio dell'applicazione…");
        _runBackgroundMaintenance = runBackgroundMaintenance;
        ConfigService = configService ?? new AppConfigService();
        Config = ConfigService.Load();
        // 6.6.1 (a): override della modalità cartella condivisa di questo PC; assente = rilevamento automatico.
        ModalitaCondivisioneService.ImpostaOverrideRadice(Config.CartellaCondivisa, Config.ModalitaCartellaCondivisa);
        BozzaAutosave = new BozzaAutosaveService(ConfigService.CartellaLocale);
        Locks.ForceUnlocked += (target, previous) =>
        {
            try
            {
                var relative = TryRelativePath(target);
                var holder = previous == null
                    ? "lock non leggibile"
                    : $"detenuto da {previous.Operatore}; device={previous.DeviceId ?? "?"}; session={previous.SessionId ?? "legacy"}; heartbeat={previous.UltimoHeartbeat:O}";
                RegistraAudit("ForceUnlock", relative, holder);
            }
            catch (Exception ex) { CrashLogService.Registra("Audit force unlock", ex); }
        };

        if (!string.IsNullOrWhiteSpace(Config.CartellaCondivisa))
        {
            try { InizializzaCartella(Config.CartellaCondivisa); }
            catch (Exception ex)
            {
                CrashLogService.Registra("Bootstrap workspace configurato", ex);
                SegnaErroreInizializzazione(ex);
            }
        }
        _progress.Fase(FaseBootstrap.Completato, "Avvio completato.");
        // Il canale di avanzamento serve solo per QUESTO bootstrap iniziale: un cambio cartella da Impostazioni
        // o il refresh periodico del timer di MainViewModel non devono continuare a scrivere verso una
        // schermata di avvio che a quel punto è già stata chiusa (o non è mai esistita, nel caso comune).
        _progress = BootstrapProgressReporter.Nessuno;
    }

    /// <summary>
    /// "Cartella valida" significa ora workspace realmente inizializzato, non semplicemente directory esistente.
    /// Evita che la UI entri in Home con repository null/parziali dopo un bootstrap fallito.
    /// </summary>
    /// <summary>Letta da moltissimi punti della UI sul thread Avalonia: NON deve toccare la rete. Usa l'ultimo esito noto della
    /// sonda con tetto di tempo (RadiceCondivisaSonda), mentre la verifica di fondo si aggiorna da sola (6.6.1, correzione 1).</summary>
    public bool CartellaValida => InizializzazioneWorkspaceCompletata && RadiceCondivisaSonda.PresenteUi(Config.CartellaCondivisa);

    public List<CampoSistemaDefinizione> CatalogoCampiSistemaCompleto() =>
        CartellaValida && Master != null
            ? Master.CampiSistemaPianificazione.Load().Concat(Master.CampiSistemaDettagli.Load()).ToList()
            : new List<CampoSistemaDefinizione>();

    public TestCase? BozzaPerNuovoTest { get; set; }

    /// <summary>
    /// Inizializza il workspace della cartella. Inizializzazione, cambio cartella e riapertura passano dallo STESSO gate
    /// (6.6.1, correzione E): il gate è rientrante, quindi un cambio cartella che richiama l'inizializzazione non si blocca.
    /// </summary>
    public void InizializzaCartella(string cartella)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gateInizializzazione) InizializzaCartellaCore(cartella);
    }

    private void InizializzaCartellaCore(string cartella)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var swTotale = Stopwatch.StartNew();
        WaitForBackgroundMaintenance();
        // Timeout più largo di quello usato in Dispose (qui il chiamante è già fuori dal thread UI, vedi
        // ImpostaCartella/PreparaAvvioAsync), ma MAI illimitato: un backup realmente bloccato non deve poter
        // impedire per sempre un cambio cartella o un nuovo avvio.
        FermaBackupGiornalieroInBackground(TimeSpan.FromSeconds(5));
        Config.CartellaCondivisa = cartella;
        InizializzazioneWorkspaceCompletata = false;
        ErroreInizializzazioneWorkspace = null;
        _testCaseChangeTracker?.Dispose();
        _testCaseChangeTracker = null;
        _testJournalFeed = null;
        _testCaseNeedsStartupReconciliation = false;
        UltimaManutenzioneStorage = null;
        BackupGiornalieroStato = DailyBackupStatus.NonAvviato;
        _lastStorageMaintenanceUtc = DateTime.UtcNow;
        _forkNotificationsAttempted.Clear();
        _systemAudit = null;

        if (Paths != null && !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Paths.Root)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(cartella)),
                StringComparison.OrdinalIgnoreCase))
            Locks.ResetLeaseObservationsUnderRoot(Paths.Root);

        Paths = new DataPaths(cartella);

        // La modalità della cartella inizializzata è quella salvata per questo PC (se presente): vale per questa radice.
        ModalitaCondivisioneService.ImpostaOverrideRadice(cartella, Config.ModalitaCartellaCondivisa);
        // 6.6.1 (decisione A): la chiave del workspace locale viene fissata qui, prima di qualunque store locale, e poi non
        // cambia più per tutta la sessione. Non si spostano dati tra chiavi. Una modifica dei dati di config viene salvata.
        Config.ChiaveWorkspacePerCartella ??= new(StringComparer.OrdinalIgnoreCase);
        try
        {
            WorkspaceLocalPaths.FissaChiaveAllApertura(cartella, Config.ChiaveWorkspacePerCartella, out var chiaveModificata);
            if (chiaveModificata) SalvaConfigSicuro();
        }
        catch (Exception ex) { CrashLogService.Registra("Chiave workspace locale", ex); }
        // Una perdita di scrittura precedente su questa cartella si azzera: lo decide di nuovo la prova di scrittura.
        SharedWriteLossMonitor.Registra(cartella);

        _progress.Fase(FaseBootstrap.VerificaWorkspace, "Verifica della cartella condivisa…");

        // 1) PRE-FLIGHT NON MUTANTE. Una vecchia configurazione che punta a una cartella OneDrive oggi non
        // montata non deve ricrearla vuota con EnsureStructure(). Allo stesso modo un client incompatibile con
        // uno schema futuro deve scoprirlo PRIMA di creare probe, directory, manifest, seed o audit.
        var disponibilita = CondivisioneHealthService.VerificaDisponibilitaSenzaScrivere(cartella);
        StatoCondivisione = CompatibilitaVersioneService.VerificaSenzaScrivere(Paths, disponibilita);
        if (StatoCondivisione.Stato == Models.StatoCondivisione.NonDisponibile)
            return;

        // 2) Se questo PC ha lasciato una Unit of Work locale interrotta, recuperala PRIMA di qualsiasi altra
        // scrittura condivisa. RecoverPending ignora il marker della propria transazione ma rifiuta qualunque
        // marker concorrente, quindi non può interferire con un writer attivo su un altro PC.
        if (StatoCondivisione.Stato == Models.StatoCondivisione.Operativa)
        {
            try
            {
                _progress.Fase(FaseBootstrap.RipristinoTransazioni, "Controllo di eventuali salvataggi interrotti da recuperare…");
                WorkflowTransactionService.RecoverPending(Paths);
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Recovery Unit of Work allo startup", ex);
                StatoCondivisione = new EsitoStatoCondivisione
                {
                    Stato = Models.StatoCondivisione.SolaLettura,
                    // Esito indeterminato (ricevuta non leggibile): il messaggio localizzato dice che le scritture sono bloccate e ritentate.
                    Messaggio = ex is WorkflowVerificaPendenteException
                        ? ex.Message
                        : "Ripristino di un salvataggio interrotto non completato. Workspace aperto in sola lettura per proteggere gli stati.",
                    VerificatoIl = DateTime.Now
                };
            }
        }

        // 3) Dopo la recovery locale non deve restare alcun marker straniero prima di un probe, aggiornamento
        // manifest o seed. Un marker pending significa che i JSON condivisi possono essere in mezzo commit.
        if (StatoCondivisione.Stato == Models.StatoCondivisione.Operativa &&
            !SharedWorkflowTransactionGuard.CanReadConsistently(Paths, out var transactionMessage))
        {
            StatoCondivisione = new EsitoStatoCondivisione
            {
                Stato = Models.StatoCondivisione.SolaLettura,
                Messaggio = transactionMessage,
                VerificatoIl = DateTime.Now
            };
        }

        // 4) Solo se preflight, recovery e barriera sono tutti verdi facciamo la prova reale di scrittura e
        // l'eventuale aggiornamento del manifest di compatibilità.
        if (StatoCondivisione.Stato == Models.StatoCondivisione.Operativa)
        {
            var healthScrittura = CondivisioneHealthService.Verifica(cartella);
            var compat = CompatibilitaVersioneService.VerificaSenzaScrivere(Paths, healthScrittura);
            StatoCondivisione = compat.Stato == Models.StatoCondivisione.Operativa
                ? CompatibilitaVersioneService.Applica(Paths, healthScrittura)
                : compat;
        }

        var scritturaCondivisaConsentita = StatoCondivisione.Stato == Models.StatoCondivisione.Operativa;
        if (scritturaCondivisaConsentita)
            Paths.EnsureStructure();

        // Audit e watcher possono produrre artefatti condivisi/tecnici: il primo esiste soltanto in modalità
        // operativa; il watcher è read-side e viene tentato anche in sola lettura su una struttura esistente.
        if (scritturaCondivisaConsentita)
        {
            try { _systemAudit = new SystemAuditService(Paths); }
            catch (Exception ex) { _systemAudit = null; CrashLogService.Registra("Audit sistema: bootstrap", ex); }
        }

        try
        {
            _testCaseChangeTracker = new TestCaseChangeTracker(Paths);
            _testCaseChangeTracker.ModificaRilevata += () => CambiamentiSuDiscoRilevati?.Invoke();
        }
        catch (Exception ex) { CrashLogService.Registra("Watcher incrementale TestCase", ex); }

        // Identità della cartella dati condivisa (gate 6.4.2-indice-stantio): un id in _ditm/workspace-id.json,
        // confrontato con il marcatore locale accanto agli indici SQLite di questo workspace (stessa cartella
        // per Test/NC/Master/Entità, vedi WorkspaceLocalPaths.GetWorkspaceDirectory). Se non coincidono la
        // cartella dati è stata SOSTITUITA sotto lo stesso percorso (es. tools/data-fix-6.4.2, o un nuovo
        // rilascio della cartella dati copiato sullo stesso path OneDrive/SharePoint): nessun indice/journal
        // locale esistente può essere considerato attendibile, va tutto ricostruito da disco. Solo lettura di
        // due file minuscoli quando l'id non cambia: nessun costo significativo aggiunto al bootstrap.
        Guid? workspaceIdCondiviso = null;
        Guid? workspaceIdMarcatoreLocale = null;
        var workspaceIdGiaPresente = false;
        var workspaceLocalDir = WorkspaceLocalPaths.GetWorkspaceDirectory(cartella);
        try
        {
            workspaceIdGiaPresente = WorkspaceIdentityService.TryReadSharedId(Paths).HasValue;
            workspaceIdCondiviso = WorkspaceIdentityService.EnsureOrReadSharedId(Paths, allowWrite: scritturaCondivisaConsentita);
            // 6.6.1 (decisione A): il marker di sola lettura vale anche per l'identità condivisa, non solo per la chiave locale.
            if (workspaceIdCondiviso.HasValue) WorkspaceReadOnlyService.RegistraIdentita(cartella, workspaceIdCondiviso.Value);
            workspaceIdMarcatoreLocale = WorkspaceIdentityService.TryReadLocalMarker(workspaceLocalDir);
        }
        catch (Exception ex) { CrashLogService.Registra("Identita' workspace: verifica id condiviso/locale", ex); }
        var identitaWorkspaceCambiata = WorkspaceIdentityService.NeedsFullRebuild(workspaceIdCondiviso, workspaceIdMarcatoreLocale);

        // Cartella sostituita nello stesso percorso: gli indici SQLite di questo workspace (Test/NC/Master/Entità,
        // journal) appartengono alla cartella precedente e vengono eliminati prima di aprire qualunque store locale,
        // così ogni PC ricostruisce tutto dai file autorevoli senza la vecchia procedura manuale di cancellazione
        // (guida §13.2). Il resto della cartella locale, in particolare record-journal con outbox e payload delle
        // modifiche non ancora pubblicate, NON viene cancellato: va in una cartella di quarantena datata (vedi
        // WorkspaceIdentityService.SvuotaIndiciLocali). Solo se la cartella aveva GIÀ un'identità: se l'id nasce ora
        // (cartella mai aperta da una 6.4.2, es. aggiornamento sul posto) non si può sapere se sia stata sostituita e
        // resta la ricostruzione forzata di Test e NC decisa sotto, senza svuotare gli indici di una cartella
        // probabilmente invariata.
        if (identitaWorkspaceCambiata && workspaceIdGiaPresente && workspaceIdCondiviso.HasValue)
            WorkspaceIdentityService.SvuotaIndiciLocali(workspaceLocalDir);

        // Misura il percorso realmente percepito dall'utente: costruzione repository + eventuale warm load
        // SQLite, ma nessun full scan OneDrive se esiste una fotografia persistente valida.
        _progress.Fase(FaseBootstrap.CaricamentoTest, "Apertura dell'indice locale dei Test…");
        var bootstrapWatch = Stopwatch.StartNew();
        TestCases = new TestCaseRepository(Paths);
        bootstrapWatch.Stop();
        var indiceTestLocaleDisponibile = TestCases.HasPersistentSnapshot;
        PerformanceDiagnosticsService.Record(
            TestCases.LocalWorkspaceDirectory,
            "testcases.bootstrap",
            bootstrapWatch.Elapsed,
            TestCases.Count(includeEliminati: true),
            $"persistentSnapshot={indiceTestLocaleDisponibile}; identitaCambiata={identitaWorkspaceCambiata}");

        if (!string.IsNullOrWhiteSpace(TestCases.LocalWorkspaceDirectory))
        {
            try { _testJournalFeed = new JournalChangeFeedService(Paths, TestCases.LocalWorkspaceDirectory!); }
            catch (Exception ex) { CrashLogService.Registra("Journal change-feed: bootstrap", ex); }
        }

        _testCaseNeedsStartupReconciliation =
            (indiceTestLocaleDisponibile && !(_testJournalFeed?.HasBaseline ?? false)) || identitaWorkspaceCambiata;

        Master = new MasterDataStore(Paths);
        // Seed/default sono SCRITTURE condivise. Prima di Phase D il contratto storico era "se il file manca";
        // dopo il cutover il file legacy può restare assente per sempre, quindi l'esistenza fisica non descrive
        // più lo stato autorevole. In Phase D seminiamo soltanto se il catalogo materializzato è davvero vuoto.
        if (scritturaCondivisaConsentita)
        {
            var phaseD = PhaseDMasterJournalService.IsInitialized(Paths.Root);
            if (phaseD)
            {
                if (Master.TestTemplate.Load().Count == 0)
                {
                    Master.TestTemplate.Save(TestTemplateDefaults.Seed());
                    CatalogMigrationService.RegistraDopoSeed(Paths);
                }
                if (Master.CampiSistemaPianificazione.Load().Count == 0) Master.CampiSistemaPianificazione.Save(CampoSistemaDefinizioneSeed.SeedPianificazione());
                if (Master.CampiSistemaDettagli.Load().Count == 0) Master.CampiSistemaDettagli.Save(CampoSistemaDefinizioneSeed.SeedDettagli());
            }
            else
            {
                if (!File.Exists(Paths.TestTemplateFile))
                {
                    Master.TestTemplate.Save(TestTemplateDefaults.Seed());
                    CatalogMigrationService.RegistraDopoSeed(Paths);
                }
                if (!File.Exists(Paths.CampiSistemaPianificazioneFile)) Master.CampiSistemaPianificazione.Save(CampoSistemaDefinizioneSeed.SeedPianificazione());
                if (!File.Exists(Paths.CampiSistemaDettagliFile)) Master.CampiSistemaDettagli.Save(CampoSistemaDefinizioneSeed.SeedDettagli());
            }
        }

        // Il marcatore in sospeso e' legato alla cartella corrente: ad ogni bootstrap si riparte da zero.
        _marcatoreCatalogoPendente = false;
        // Migrazione idempotente del catalogo (RAS UnitLogger, release 6.6.1). Solo sessioni che possono scrivere: workspace
        // Operativa, non sola lettura, operatore persistito Master/Standard (mai Viewer). Un errore non blocca l'avvio:
        // il marcatore resta non registrato e la migrazione si ripete al prossimo avvio.
        if (scritturaCondivisaConsentita)
        {
            try
            {
                if (SessionePuoMigrareCatalogo())
                {
                    var risultatoMigrazione = CatalogMigrationService.EseguiRasUdt(Paths, Master.TestTemplate, Locks, Config.OperatoreAttivo);
                    _marcatoreCatalogoPendente = risultatoMigrazione.MarcatorePendente;
                    DiagnosticTraceService.Registra("Catalogo.Migrazione",
                        $"{CatalogMigrationService.IdRasUdtTemplateV1} esito={risultatoMigrazione.Esito} marcatorePendente={risultatoMigrazione.MarcatorePendente}");
                }
            }
            catch (Exception ex) { CrashLogService.Registra("Migrazione catalogo RAS UnitLogger", ex); }
        }

        _progress.Fase(FaseBootstrap.CaricamentoNonConformita, "Apertura dell'indice locale delle Non Conformità…");
        NonConformita = new NonConformitaRepository(Paths);
        // Aggancio NC -> Test per codice validato sul Test persistito (codici riassegnati, vedi
        // AnomaliaWorkflowService.AppartieneAlTest). Lambda su this: TestCases puo essere ricreato.
        NonConformita.TestResolver = id => TestCases.GetById(id);
        var indiceNcLocaleDisponibile = NonConformita.HasPersistentSnapshot;

        _progress.Fase(FaseBootstrap.CaricamentoDatiCorrelati, "Caricamento change request, segnalazioni e notifiche…");
        ChangeRequests = new ChangeRequestRepository(Paths);
        Segnalazioni = new SegnalazioneRepository(Paths);
        // R9: journal per-device dei commit NC/CR/Segnalazioni. Solo storage locale nel costruttore; la lettura
        // dei log condivisi avviene nel giro periodico in background (AggiornaJournalENotificaFork).
        try
        {
            RecordJournal = new DomainRecordJournalService(Paths,
                TestCases.LocalWorkspaceDirectory ?? WorkspaceLocalPaths.GetWorkspaceDirectory(Paths.Root));
            NonConformita.RecordJournal = RecordJournal;
            ChangeRequests.RecordJournal = RecordJournal;
            Segnalazioni.RecordJournal = RecordJournal;
        }
        catch (Exception ex) { CrashLogService.Registra("Record journal: inizializzazione", ex); RecordJournal = null; }
        // Un codice test referenziato da NC o Segnalazioni non viene piu riassegnato dopo l'eliminazione definitiva.
        TestCases.IdRiservatiAggiuntivi = () => NonConformita.GetAllReferencedTestIds().Concat(Segnalazioni.GetAllReferencedTestIds());
        Notifiche = new NotificaRepository(Paths);

        // Non rieseguire Applica qui: in sola lettura deve restare impossibile una scrittura accidentale del
        // manifest. La rilettura dei repository è invece consentita e serve proprio per consultare il workspace.
        // identitaWorkspaceCambiata forza SEMPRE il refresh (mai saltato) anche quando l'indice locale ha una
        // fotografia persistente apparentemente valida: quella fotografia appartiene a una cartella dati
        // diversa che ha solo occupato per caso lo stesso percorso/hash locale.
        RicaricaTutto(
            saltaRefreshTestCases: indiceTestLocaleDisponibile && !identitaWorkspaceCambiata,
            saltaRefreshNonConformita: indiceNcLocaleDisponibile && !identitaWorkspaceCambiata);
        swTotale.Stop();
        // Costo REALE percepito all'apertura app/cambio cartella (feedback utente 2026-09-17: "in qualsiasi
        // condizione" il refresh tra una pagina e l'altra dovrebbe restare sotto i ~700ms — questo è il
        // bootstrap una tantum che precede la prima pagina, non una navigazione tra pagine, ma è comunque
        // la voce dominante misurata su un workspace con migliaia di Test: ~700-1100ms a ~4300 Test, quasi
        // interamente nel caricamento/indicizzazione di TestCaseRepository). Registrato nello stesso
        // performance.ndjson già usato per warm-start/reconciliation/manutenzione, non un meccanismo nuovo.
        PerformanceDiagnosticsService.Record(TestCases.LocalWorkspaceDirectory, "workspace.bootstrap.totale",
            swTotale.Elapsed, TestCases.Count(includeEliminati: true));

        // Marcatore locale allineato SOLO ora, a valle di un bootstrap/refresh riuscito (il refresh sopra, se
        // identitaWorkspaceCambiata era vera, ha appena ricostruito Test/NC da disco): se il processo si
        // interrompe prima, al prossimo avvio il marcatore resta quello vecchio (o assente) e la ricostruzione
        // viene ritentata da zero, mai saltata per un mismatch non ancora sanato.
        if (workspaceIdCondiviso.HasValue)
        {
            try { WorkspaceIdentityService.WriteLocalMarker(workspaceLocalDir, workspaceIdCondiviso.Value); }
            catch (Exception ex) { CrashLogService.Registra("Identita' workspace: scrittura marcatore locale", ex); }
        }

        InizializzazioneWorkspaceCompletata = true;
        DiagnosticTraceService.Registra("Workspace.Apertura", $"stato={StatoCondivisione.Stato} test={TestCases.Count(includeEliminati: true)} nc={NonConformita.CountAll} durata={swTotale.ElapsedMilliseconds}ms");

        // Fuori dal warm-start: lock/notifiche/temp/backup/SQLite possono essere manutenuti senza ritardare UI.
        // ottimizzaStatisticheSqlite:false SOLO qui (vedi doc-comment su EseguiManutenzioneStorage): questa
        // manutenzione corre in background proprio mentre la primissima Home del bootstrap è già in mostra.
        if (_runBackgroundMaintenance && StatoCondivisione.Stato == Models.StatoCondivisione.Operativa)
            _backgroundMaintenanceTask = Task.Run(() => EseguiManutenzioneStorage(force: true, ottimizzaStatisticheSqlite: false));

        // Il backup giornaliero (6.4.1) NON viene più avviato da qui (revisione indipendente su fc7e51b, punto
        // 2): anche partendo su un thread separato, farlo nascere DENTRO InizializzaCartella lo fa competere per
        // I/O con le primissime navigazioni dell'utente subito dopo il bootstrap/cambio cartella. Il chiamante
        // (App.axaml.cs dopo che MainWindow è mostrata e lo scaldamento è finito; ImpostazioniViewModel dopo un
        // cambio cartella riuscito) richiama esplicitamente AvviaBackupGiornalieroDopoAvvio(), che aggiunge un
        // ritardo cancellabile prima di iniziare davvero a leggere/hashare il workspace.
    }

    /// <summary>Sessione che può scrivere i cataloghi condivisi durante il bootstrap: non sola lettura e operatore
    /// persistito Master o Standard (il Viewer non scrive mai). Non usa OperatoreCorrente perché il bootstrap lo riallinea
    /// solo più avanti: Config.OperatoreAttivo è l'ultimo operatore accettato su questo PC. Senza operatore la migrazione
    /// attende un avvio successivo: nessuna scrittura dal login, che gira sul thread UI.</summary>
    private bool SessionePuoMigrareCatalogo()
    {
        if (WorkspaceReadOnlyService.IsEnabled(Paths.Root)) return false;
        var nome = Config.OperatoreAttivo;
        if (string.IsNullOrWhiteSpace(nome)) return false;
        var operatore = Master.Operatori.Load().FirstOrDefault(o => o.Nome == nome && !o.Disattivo);
        return operatore != null && operatore.RuoloEffettivo != RuoloOperatore.Viewer;
    }

    /// <summary>Marcatore RAS UnitLogger rimasto in sospeso dopo la migrazione di questo bootstrap (vedi CatalogMigrationService).</summary>
    private volatile bool _marcatoreCatalogoPendente;

    /// <summary>Riprova, nel corso della sessione, la sola scrittura del marcatore RAS UnitLogger in sospeso. Non tocca il catalogo.
    /// Gira dal giro periodico in background (AggiornaJournalENotificaFork) con la stessa gate di scrittura dell'avvio.</summary>
    private void TentaMarcatoreCatalogoPendente()
    {
        if (!_marcatoreCatalogoPendente || Paths == null || !InizializzazioneWorkspaceCompletata || SulThreadUi()) return;
        try
        {
            if (!SessionePuoMigrareCatalogo()) return;
            if (CatalogMigrationService.RiprovaMarcatore(Paths)) _marcatoreCatalogoPendente = false;
        }
        catch (Exception ex) { CrashLogService.Registra("Migrazione catalogo: nuovo tentativo del marcatore RAS UnitLogger", ex); }
    }

    /// <summary>Riprova nel corso della sessione l'alzamento dello schema del manifest condiviso rimasto in sospeso (P2.12).
    /// Come il marcatore catalogo: fuori dal thread UI e solo con workspace operativo, mai in sola lettura.</summary>
    private void TentaAlzamentoSchemaPendente()
    {
        if (Paths == null || !InizializzazioneWorkspaceCompletata || SulThreadUi()) return;
        if (StatoCondivisione.Stato != Models.StatoCondivisione.Operativa) return;
        try { CompatibilitaVersioneService.RiprovaAlzamentoSchema(Paths); }
        catch (Exception ex) { CrashLogService.Registra("Compatibilita: nuovo tentativo dell'alzamento schema", ex); }
    }

    /// <summary>Ritardo prima che il backup giornaliero, una volta pianificato da questo metodo, inizi davvero
    /// a leggere/hashare il workspace. Campo internal statico SOLO per i test (stesso motivo di
    /// <see cref="BackupRetryDelay"/>/<see cref="BackupMaxAttempts"/>).</summary>
    internal static TimeSpan BackupStartDelay = TimeSpan.FromSeconds(60);

    /// <summary>Pianifica (o ri-pianifica, annullando prima qualunque esecuzione/attesa precedente) il backup
    /// giornaliero in background dopo <see cref="BackupStartDelay"/>. Va richiamato DAL CHIAMANTE fuori dal
    /// bootstrap vero e proprio — mai da InizializzaCartella — così il thread dedicato nasce solo quando la UI
    /// è già pronta: App.axaml.cs lo richiama dopo che MainWindow è mostrata e lo scaldamento delle pagine è
    /// concluso; ImpostazioniViewModel.CambiaCartella lo richiama dopo un cambio cartella riuscito. Sicuro da
    /// richiamare più volte: annulla sempre prima l'eventuale pianificazione/esecuzione precedente (con timeout
    /// breve, mai bloccante a lungo) prima di pianificarne una nuova per la cartella corrente.</summary>
    public void AvviaBackupGiornalieroDopoAvvio()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FermaBackupGiornalieroInBackground(TimeSpan.FromSeconds(5));
        if (!_runBackgroundMaintenance || StatoCondivisione.Stato != Models.StatoCondivisione.Operativa) return;
        AvviaBackupGiornalieroInBackground(BackupStartDelay);
    }

    private void AvviaBackupGiornalieroInBackground(TimeSpan ritardoIniziale)
    {
        var cts = new CancellationTokenSource();
        _backupCts = cts;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _backgroundBackupTask = tcs.Task;
        var paths = Paths;
        var ct = cts.Token;

        // Doppia rete di sicurezza intorno al corpo del Thread: un'eccezione NON osservata che sfugge da un
        // Thread grezzo (a differenza di un Task) termina l'intero processo .NET, IsBackground o meno. La prima
        // rete e' dentro EseguiBackupGiornaliero; questa e' l'ultima, nel caso fallisse perfino CrashLogService.
        var thread = new Thread(() =>
        {
            // THREAD_MODE_BACKGROUND_BEGIN/END (Windows): abbassa la priorità REALE di I/O/memoria del thread,
            // non solo lo scheduling CPU (quello lo fa già Priority=BelowNormal sotto). No-op su altri OS.
            try
            {
                using var modalitaBackground = WindowsBackgroundThreadMode.Enter();
                EseguiBackupGiornaliero(paths, ritardoIniziale, ct, tcs);
            }
            catch (Exception ex)
            {
                // Ultima rete: nemmeno l'attivazione della modalità di sfondo può far cadere il processo (Thread grezzo).
                CrashLogService.Registra("Backup giornaliero (thread di sfondo)", ex);
                tcs.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "TestLoom-BackupGiornaliero"
        };
        thread.Start();
    }

    private void EseguiBackupGiornaliero(DataPaths paths, TimeSpan ritardoIniziale, CancellationToken ct, TaskCompletionSource tcs)
    {
        try
        {
            // Ritardo cancellabile PRIMA di leggere/hashare qualunque cosa: il chiamante lo usa per non far
            // competere il backup con le primissime navigazioni dopo l'avvio/il cambio cartella (revisione
            // indipendente su fc7e51b, punto 2). Stesso meccanismo di annullamento del resto del ciclo: se la
            // chiusura/un nuovo cambio cartella arriva qui, il ramo catch(OperationCanceledException) sotto la
            // gestisce esattamente come un annullamento durante il backup vero e proprio.
            if (ritardoIniziale > TimeSpan.Zero)
                AttendiConCancellazione(ritardoIniziale, ct);
            ct.ThrowIfCancellationRequested();

            EseguiBackupGiornalieroConRitentativi(paths, ct);
            tcs.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            try { AggiornaStatoBackupGiornaliero(DailyBackupStatus.Rinviato("Annullato: chiusura dell'app o cambio cartella durante il backup.")); }
            catch { /* vedi rete di sicurezza esterna in AvviaBackupGiornalieroInBackground */ }
            tcs.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            // Nessun percorso deve poter lasciare la Task in uno stato Faulted non osservato: sia
            // FermaBackupGiornalieroInBackground sia Dispose la attendono (con timeout limitato) sul suo esito.
            CrashLogService.Registra("Backup giornaliero in background: errore non gestito", ex);
            AggiornaStatoBackupGiornaliero(DailyBackupStatus.Errore(ex.Message));
            tcs.TrySetResult();
        }
    }

    /// <param name="paths">Fotografia di DataPaths catturata all'avvio del thread: se nel frattempo la
    /// cartella cambia, InizializzaCartella ha già cancellato QUESTO token prima di sostituire Paths, quindi
    /// questo metodo non legge mai un DataPaths incoerente con l'oggetto AppState corrente.</param>
    private void EseguiBackupGiornalieroConRitentativi(DataPaths paths, CancellationToken ct)
    {
        var maxTentativi = Math.Max(1, BackupMaxAttempts);
        for (var tentativo = 1; tentativo <= maxTentativi; tentativo++)
        {
            ct.ThrowIfCancellationRequested();

            // Sola lettura/non disponibile: non è una condizione transitoria che valga la pena ritentare nella
            // sessione (criterio d'accettazione 5). Il prossimo InizializzaCartella/avvio riproverà da zero.
            if (!WorkspaceWritePolicy.CanWrite(paths))
            {
                AggiornaStatoBackupGiornaliero(DailyBackupStatus.Rinviato(
                    "Backup giornaliero non eseguito: la cartella condivisa è in sola lettura o non disponibile."));
                return;
            }

            // Preflight cross-thread: una Unit of Work può essere attiva sul thread UI (Salva) mentre questo
            // thread di background sta per partire. EnsureSnapshotCanStart dentro BackupService lo verificherebbe
            // comunque e lancerebbe, ma qui vogliamo RINVIARE con backoff invece di trattarlo come errore fatale
            // (criterio d'accettazione 3: un Salva concorrente non deve mai far fallire in modo definitivo il
            // backup giornaliero).
            if (WorkflowTransactionService.IsActiveAnywhere)
            {
                AggiornaStatoBackupGiornaliero(DailyBackupStatus.Rinviato(
                    $"Backup giornaliero rinviato: un salvataggio TestLoom è in corso (tentativo {tentativo}/{maxTentativi})."));
                AttendiConCancellazione(BackupRetryDelay, ct);
                continue;
            }

            AggiornaStatoBackupGiornaliero(DailyBackupStatus.InCorso());
            try
            {
                var snapshot = BackupService.CreaGiornalieroSeNecessario(paths, DateTime.Now, ct, TestLocalBackupBaseDirectory);
                string? warning = null;
                var externalPath = Config.PercorsoBackupEsterno;
                if (!string.IsNullOrWhiteSpace(externalPath))
                {
                    try
                    {
                        // Anche se oggi non è cambiato nulla, una postazione che ha appena configurato
                        // il backup aggiuntivo deve poter replicare l'ultimo ZIP locale esistente.
                        var source = snapshot ?? Directory
                            .EnumerateFiles(WorkspaceLocalPaths.GetBackupDirectory(paths.Root, TestLocalBackupBaseDirectory),
                                "TestLoom_snapshot_*.zip")
                            .Select(p => new FileInfo(p))
                            .OrderByDescending(f => f.LastWriteTimeUtc)
                            .FirstOrDefault()?.FullName
                            ?? throw new FileNotFoundException("Nessuno ZIP locale da copiare.");
                        ExternalBackupCopyService.CopyVerified(source, externalPath, paths.Root, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // Il fallimento della copia aggiuntiva NON deve invalidare né ripetere il
                        // backup locale già pubblicato e verificato.
                        warning = "Backup locale riuscito; copia esterna non riuscita: " + ex.Message;
                        CrashLogService.Registra("Backup giornaliero: copia aggiuntiva", ex);
                    }
                }
                AggiornaStatoBackupGiornaliero(warning == null
                    ? DailyBackupStatus.Completato(DateTime.UtcNow)
                    : DailyBackupStatus.CompletatoConAvviso(DateTime.UtcNow, warning));
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Include il caso "dati cambiati durante lo snapshot" (BackupService.Crea confronta before/after
                // e lancia IOException senza pubblicare nulla): non definitivo, ritenta più tardi da uno stato
                // stabile, esattamente come richiesto dal criterio d'accettazione 3.
                CrashLogService.Registra("Backup giornaliero in background", ex);
                if (tentativo >= maxTentativi)
                {
                    AggiornaStatoBackupGiornaliero(DailyBackupStatus.Errore(ex.Message));
                    return;
                }
                AggiornaStatoBackupGiornaliero(DailyBackupStatus.Rinviato(
                    $"Backup giornaliero rinviato, ritento tra qualche minuto: {ex.Message}"));
                AttendiConCancellazione(BackupRetryDelay, ct);
            }
        }
    }

    private static void AttendiConCancellazione(TimeSpan durata, CancellationToken ct) =>
        ct.WaitHandle.WaitOne(durata);

    private void AggiornaStatoBackupGiornaliero(DailyBackupStatus stato)
    {
        BackupGiornalieroStato = stato;
        try { BackupGiornalieroStatoCambiato?.Invoke(); }
        catch (Exception ex) { CrashLogService.Registra("Notifica stato backup giornaliero", ex); }
    }

    /// <summary>Annulla l'eventuale backup giornaliero pianificato/in corso e attende la sua conclusione per AL
    /// MASSIMO <paramref name="attesaMassima"/> — MAI senza limite (revisione indipendente su fc7e51b, punto 1:
    /// un GetAwaiter().GetResult() illimitato qui bloccava la chiusura dell'app sul thread UI, dato che
    /// MainWindow.OnClosing → MainViewModel.Dispose → AppState.Dispose sono tutti sincroni). Il thread di
    /// backup è comunque <c>IsBackground=true</c>: se non termina entro il limite continua semplicemente a
    /// spegnersi in modo asincrono e termina al più tardi con il processo, senza tenere in vita nulla. In quel
    /// caso il CancellationTokenSource non viene volutamente Dispose()-ato qui (il thread potrebbe star ancora
    /// leggendo il suo Token) — verrà raccolto dal GC quando il thread avrà finito di usarlo.</summary>
    private void FermaBackupGiornalieroInBackground(TimeSpan attesaMassima)
    {
        var cts = _backupCts;
        var task = _backgroundBackupTask;
        _backupCts = null;
        _backgroundBackupTask = null;
        if (cts == null) return;
        try { cts.Cancel(); } catch { }
        var terminato = task == null || AttendiTaskConLimite(task, attesaMassima);
        if (terminato) cts.Dispose();
    }

    private static bool AttendiTaskConLimite(Task task, TimeSpan timeout)
    {
        try { return task.Wait(timeout); }
        catch (AggregateException) { return true; } // terminato comunque (cancellazione/errore già gestiti e loggati sul thread di backup)
    }

    private void SegnaErroreInizializzazione(Exception ex)
    {
        InizializzazioneWorkspaceCompletata = false;
        ErroreInizializzazioneWorkspace = ex.Message;
        DiagnosticTraceService.Registra("Workspace.Apertura", $"FALLITA: {ex.GetType().Name}: {ex.Message}");
        OperatoreCorrente = null;
        StatoCondivisione = new EsitoStatoCondivisione
        {
            Stato = RadiceCondivisaSonda.PresenteUi(Config.CartellaCondivisa)
                ? Models.StatoCondivisione.SolaLettura
                : Models.StatoCondivisione.NonDisponibile,
            Messaggio = $"Workspace non inizializzato in sicurezza: {ex.Message}",
            VerificatoIl = DateTime.Now
        };
    }

    /// <summary>Attesa massima della manutenzione in background prima di inizializzare una cartella (6.6.1, correzione 5).
    /// Oltre questo tempo il cambio cartella fallisce in modo pulito invece di restare appeso sotto il gate.</summary>
    internal static TimeSpan AttesaManutenzione { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Attende la manutenzione in background per al massimo <see cref="AttesaManutenzione"/>. Ritorna false se non
    /// è terminata in tempo e non lancia mai: chi cambia cartella decide se fallire (<see cref="WaitForBackgroundMaintenance"/>),
    /// la chiusura (<see cref="Dispose"/>) si limita a registrare.</summary>
    private bool AttendiManutenzioneInBackground(TimeSpan? attesaMassima = null)
    {
        var task = _backgroundMaintenanceTask;
        if (task == null) return true;
        bool completata;
        try { completata = task.Wait(attesaMassima ?? AttesaManutenzione); }
        catch (AggregateException ex)
        {
            CrashLogService.Registra("Attesa manutenzione storage", ex);
            completata = true;
        }
        if (completata && ReferenceEquals(_backgroundMaintenanceTask, task)) _backgroundMaintenanceTask = null;
        return completata;
    }

    /// <summary>Cambio cartella: se la manutenzione non finisce in tempo l'operazione fallisce in modo pulito, senza lasciare il gate bloccato.</summary>
    private void WaitForBackgroundMaintenance()
    {
        if (!AttendiManutenzioneInBackground())
            throw new IOException($"Manutenzione in background non terminata entro {AttesaManutenzione.TotalSeconds:0} secondi: " +
                "cambio cartella annullato, riprova tra poco.");
    }

    /// <summary>Limite di attesa in Dispose() per il backup giornaliero: Dispose è quasi sempre raggiunto in
    /// modo SINCRONO dal thread UI (MainWindow.OnClosing → MainViewModel.Dispose → AppState.Dispose, nessun
    /// await in mezzo), quindi qui l'attesa deve restare impercettibile — mai i minuti che un backup può
    /// normalmente durare. Il CancellationToken nei cicli di hash/copia di BackupService rende comunque
    /// l'annullamento quasi immediato nella stragrande maggioranza dei casi; questo limite è solo la rete di
    /// sicurezza per il caso limite (I/O disco realmente bloccato) in cui anche l'annullamento tardasse.</summary>
    private static readonly TimeSpan BackupDisposeTimeout = TimeSpan.FromMilliseconds(400);

    /// <summary>Attesa massima della manutenzione in background alla chiusura dell'app. La manutenzione si annulla fra una fase
    /// e l'altra (vedi Dispose): l'attesa copre solo la fase in corso, così la chiusura non supera pochi secondi.</summary>
    internal static TimeSpan AttesaChiusuraManutenzione { get; set; } = TimeSpan.FromSeconds(2);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _testCaseChangeTracker?.Dispose();
        _testCaseChangeTracker = null;
        ChiusuraDiagnostica.Registra("AppState: osservatore modifiche disposto");
        // Annullamento PRIMA dell'attesa della manutenzione: il backup deve reagire subito (CancellationToken
        // controllato nei cicli di hash/copia) invece di lasciare che la chiusura dell'app attenda la fine di
        // uno snapshot che può normalmente durare minuti.
        FermaBackupGiornalieroInBackground(BackupDisposeTimeout);
        ChiusuraDiagnostica.Registra("AppState: backup giornaliero fermato");
        // Manutenzione: si annulla fra una fase e l'altra (la fase in corso finisce, le scritture sono atomiche per file) e la
        // chiusura attende al massimo AttesaChiusuraManutenzione. Chiusura: non deve mai lanciare (MainWindow.OnClosing è async
        // void). Oltre il limite si registra soltanto: il thread di manutenzione è in background e termina con il processo.
        _annullaManutenzione.Cancel();
        var manutenzione = _backgroundMaintenanceTask?.Status.ToString() ?? "assente";
        if (!AttendiManutenzioneInBackground(AttesaChiusuraManutenzione))
        {
            try { DiagnosticTraceService.Registra("Chiusura.Manutenzione", $"non terminata entro {AttesaChiusuraManutenzione.TotalSeconds:0.0} secondi: prosegue in background"); }
            catch { }
        }
        ChiusuraDiagnostica.Registra($"AppState: manutenzione in background (stato {manutenzione}) conclusa");
        GC.SuppressFinalize(this);
    }

    public void ImpostaCartella(string cartella) => ImpostaCartella(cartella, null);

    /// <param name="progress">Canale opzionale di avanzamento verso una schermata di avvio (stesso
    /// <see cref="BootstrapProgress"/> usato dal bootstrap iniziale in App.axaml.cs). Introdotto per il primo
    /// accesso (FirstRunViewModel.Conferma): senza questo canale l'inizializzazione di una cartella nuova o
    /// grande passava per InizializzaCartella in silenzio e, se chiamata sul thread UI, lasciava la finestra
    /// "Non risponde" per minuti senza alcun feedback (feedback utente: "ci mette tanto perché inizializza la
    /// nuova cartella"). Il chiamante è responsabile di eseguire questo metodo fuori dal thread UI (Task.Run);
    /// qui ci limitiamo a instradare l'avanzamento, esattamente come fa già il costruttore.</param>
    public void ImpostaCartella(string cartella, IProgress<BootstrapProgress>? progress)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 6.6.1 (correzione 6): configurazione, progresso e inizializzazione cambiano insieme, sotto lo stesso gate.
        lock (_gateInizializzazione) ImpostaCartellaCore(cartella, progress);
    }

    private void ImpostaCartellaCore(string cartella, IProgress<BootstrapProgress>? progress)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Il login richiama sempre ImpostaCartella anche quando il costruttore ha gia' aperto lo stesso
        // workspace: a 100k Test questo rifaceva l'intera riconciliazione sul thread UI e lasciava la
        // finestra "Non risponde" per minuti (feedback utente, collaudo Avalonia reale). Il guard vale
        // soltanto per una fotografia completata e ancora raggiungibile; un bootstrap fallito deve poter
        // essere ritentato e una cartella diversa deve continuare a passare da tutti i preflight.
        if (CartellaValida && Paths != null &&
            StessoPercorsoWorkspace(Paths.Root, cartella) &&
            StessoPercorsoWorkspace(Config.CartellaCondivisa, cartella))
            return;
        Config.CartellaCondivisa = cartella;
        ConfigService.Save(Config); // come 6.6.0: un errore di salvataggio resta visibile al chiamante
        _configDaSalvare = false;   // questo salvataggio include anche gli alias di chiave
        var progressPrecedente = _progress;
        _progress = progress != null ? new BootstrapProgressReporter(progress) : BootstrapProgressReporter.Nessuno;
        try { InizializzaCartella(cartella); }
        catch (Exception ex)
        {
            SegnaErroreInizializzazione(ex);
            throw;
        }
        finally { _progress = progressPrecedente; }
    }

    /// <summary>Gate unico di inizializzazione, cambio cartella e riapertura (6.6.1, correzione E).</summary>
    private readonly object _gateInizializzazione = new();

    /// <summary>Vero se un alias di chiave non è ancora stato salvato nel config: si ritenta al prossimo salvataggio o giro (correzione 1).</summary>
    private bool _configDaSalvare;

    /// <summary>Salvataggio del config che non fa cadere l'apertura: se fallisce, resta da ritentare.</summary>
    private void SalvaConfigSicuro()
    {
        try
        {
            ConfigService.Save(Config);
            _configDaSalvare = false;
        }
        catch (Exception ex)
        {
            _configDaSalvare = true;
            CrashLogService.Registra("Salvataggio alias chiave workspace (ritento al prossimo giro)", ex);
        }
    }

    /// <summary>Ultimo errore di riapertura registrato: lo stesso errore ripetuto non riempie il log (una volta sola).</summary>
    private string? _ultimoErroreRiapertura;

    /// <summary>
    /// Riapre il workspace configurato quando, all'avvio, la cartella condivisa non era raggiungibile (6.6.1, e). Non fa nulla
    /// se il workspace è già aperto o se la cartella non risponde, e non crea mai una struttura vuota: passa dalla stessa
    /// inizializzazione di "Cambia cartella", con i suoi preflight e sotto lo stesso gate. Va chiamato FUORI dal thread UI.
    /// </summary>
    /// <returns>Vero se il workspace risulta aperto al termine della chiamata.</returns>
    public bool RiprovaInizializzazione()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gateInizializzazione)
        {
            if (InizializzazioneWorkspaceCompletata || string.IsNullOrWhiteSpace(Config.CartellaCondivisa))
                return InizializzazioneWorkspaceCompletata;
            var disponibilita = CondivisioneHealthService.VerificaDisponibilitaSenzaScrivere(Config.CartellaCondivisa);
            if (disponibilita.Stato == Models.StatoCondivisione.NonDisponibile) return false;
            try
            {
                ImpostaCartella(Config.CartellaCondivisa, null);
                _ultimoErroreRiapertura = null;
            }
            catch (Exception ex)
            {
                // Log una sola volta per errore uguale: la condivisione raggiungibile che non si inizializza non riempie il log.
                if (!string.Equals(_ultimoErroreRiapertura, ex.Message, StringComparison.Ordinal))
                {
                    CrashLogService.Registra("Riapertura cartella condivisa", ex);
                    _ultimoErroreRiapertura = ex.Message;
                }
                return false;
            }
            return InizializzazioneWorkspaceCompletata;
        }
    }

    private static bool StessoPercorsoWorkspace(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    public bool ImpostaOperatore(string nome, string pin)
    {
        if (!InizializzazioneWorkspaceCompletata) return false;
        var operatore = Master.Operatori.Load().FirstOrDefault(o => o.Nome == nome && !o.Disattivo);
        if (operatore == null) return false;
        if (!PinService.Verifica(pin, operatore.PinHash)) return false;
        Config.OperatoreAttivo = nome;
        ConfigService.Save(Config);
        OperatoreCorrente = operatore;
        DiagnosticTraceService.Registra("Operatore.Accesso", nome);
        return true;
    }

    /// <summary>Accesso in modalità "senza PIN" (piano 6.4.2, §4b): stesso esito di ImpostaOperatore ma senza
    /// verificare alcun PIN — il chiamante (FirstRunViewModel.Conferma) deve invocare questo metodo SOLO
    /// quando AccessoSenzaPin è vero, mai come scorciatoia generale. Vale per qualunque ruolo, Master compresi.
    /// Il PIN dell'operatore resta comunque memorizzato/invariato: tornando alla modalità con PIN non va
    /// reimpostato.</summary>
    public bool ImpostaOperatoreSenzaPin(string nome)
    {
        if (!InizializzazioneWorkspaceCompletata || !AccessoSenzaPin) return false;
        var operatore = Master.Operatori.Load().FirstOrDefault(o => o.Nome == nome && !o.Disattivo);
        if (operatore == null) return false;
        Config.OperatoreAttivo = nome;
        ConfigService.Save(Config);
        OperatoreCorrente = operatore;
        DiagnosticTraceService.Registra("Operatore.Accesso", nome + " (senza PIN)");
        return true;
    }

    public string CompletaCambioPinObbligatorio(string nuovoPin)
    {
        if (OperatoreCorrente is not { RichiedeCambioPin: true } corrente)
            throw new InvalidOperationException("Nessun cambio PIN obbligatorio in corso.");

        var operatori = Master.Operatori.Load();
        var expectedSnapshot = EntityStateFingerprint.Clone(operatori);
        var operatore = operatori.FirstOrDefault(o => o.Nome == corrente.Nome)
            ?? throw new InvalidOperationException("Operatore non più disponibile.");
        var codiceRecupero = PinService.GeneraCodiceRecupero();
        operatore.PinHash = PinService.Hash(nuovoPin);
        operatore.PinChiaro = nuovoPin;
        operatore.RecoveryCodeHash = PinService.Hash(codiceRecupero);
        operatore.RecoveryCodeChiaro = codiceRecupero;
        operatore.RichiedeCambioPin = false;
        operatore.UltimaModifica = DateTime.Now;
        operatore.ModificatoDa = operatore.Nome;
        Master.Operatori.Save(operatori, expectedSnapshot);
        OperatoreCorrente = operatore;
        return codiceRecupero;
    }

    public void SalvaValoreDefaultCampo(string chiave, string? valore)
        => SalvaValoriDefaultCampi(new Dictionary<string, string?>(StringComparer.Ordinal) { [chiave] = valore });

    /// <summary>Salva in un unico aggiornamento i default personali dell'operatore attivo. Usato per preferenze
    /// che formano un insieme coerente (per esempio i quattro campi guasto Injection): un conflitto non deve
    /// lasciare metà dei valori aggiornati o rimossi.</summary>
    public void SalvaValoriDefaultCampi(IReadOnlyDictionary<string, string?> valori)
    {
        if (OperatoreCorrente == null || valori.Count == 0) return;
        var operatori = Master.Operatori.Load();
        var expectedSnapshot = EntityStateFingerprint.Clone(operatori);
        var operatore = operatori.FirstOrDefault(o => o.Nome == OperatoreCorrente.Nome);
        if (operatore == null) return;
        foreach (var (chiave, valore) in valori)
        {
            if (string.IsNullOrEmpty(valore)) operatore.ValoriDefaultCampi.Remove(chiave);
            else operatore.ValoriDefaultCampi[chiave] = valore;
        }
        Master.Operatori.Save(operatori, expectedSnapshot);
        OperatoreCorrente = operatore;
    }

    public void Logout()
    {
        DiagnosticTraceService.Registra("Operatore.Logout", Config.OperatoreAttivo);
        Config.OperatoreAttivo = "";
        ConfigService.Save(Config);
        OperatoreCorrente = null;
    }

    private void RiallineaOperatoreCorrente()
    {
        if (string.IsNullOrEmpty(Config.OperatoreAttivo)) { OperatoreCorrente = null; return; }
        var operatore = Master.Operatori.Load().FirstOrDefault(o => o.Nome == Config.OperatoreAttivo);
        OperatoreCorrente = operatore != null && !operatore.Disattivo ? operatore : null;
    }

    /// <summary>Ricarica AccessoSenzaPin da accesso-config.json (piano 6.4.2, §4b). File assente = "con PIN"
    /// (default più restrittivo, mai un'eccezione che blocchi il refresh per un file accessorio).</summary>
    private void CaricaModalitaAccesso()
    {
        if (Paths == null) { AccessoSenzaPin = false; return; }
        AccessoSenzaPin = RuoloSigilloService.SenzaPinEffettivo(AccessModeConfigService.Load(Paths));
        CaricaPoliticaDurata();
    }

    /// <summary>Politica condivisa della durata operazioni (TIME, 6.6.1). File assente o illeggibile = facoltativa.</summary>
    public PoliticaDurataConfig PoliticaDurata { get; private set; } = new();

    private void CaricaPoliticaDurata()
    {
        PoliticaDurata = Paths == null ? new PoliticaDurataConfig() : PoliticaDurataService.Load(Paths);
    }

    public void RicaricaTutto() => RicaricaTutto(saltaRefreshTestCases: false, saltaRefreshNonConformita: false);

    private void RicaricaTutto(bool saltaRefreshTestCases, bool saltaRefreshNonConformita = false)
    {
        RileggiDaDisco(saltaRefreshTestCases, saltaRefreshNonConformita, RefreshEsclusioni.Nessuna);
        DatiRicaricati?.Invoke();
    }

    public void RileggiDaDisco() => RileggiDaDisco(saltaRefreshTestCases: false, saltaRefreshNonConformita: false, RefreshEsclusioni.Nessuna);

    /// <summary>Overload usato da MainViewModel (refresh periodico E guidato dagli eventi del watcher, C4/C5,
    /// diagnosi_sync/diagnosi.md) quando un editor ha modifiche non salvate: a differenza del vecchio
    /// comportamento non salta più l'intero giro, esclude solo la porzione descritta da
    /// <paramref name="esclusioni"/>. Passare <see cref="RefreshEsclusioni.Nessuna"/> (o non passare nulla,
    /// vedi overload sopra) equivale esattamente al refresh completo di prima.</summary>
    public void RileggiDaDisco(RefreshEsclusioni esclusioni) =>
        RileggiDaDisco(saltaRefreshTestCases: false, saltaRefreshNonConformita: false, esclusioni);

    private readonly object _gateSondaRadice = new();

    /// <summary>
    /// Sonda della radice condivisa (6.7.1). Va chiamata FUORI dal thread UI: la verifica può attendere il tetto di tempo
    /// della cartella di rete. Se la radice non risponde imposta subito lo stato "non disponibile" (il banner lo mostra);
    /// se risponde dopo una perdita chiede una rilettura di recupero, che riapplica il controllo di scrittura normale.
    /// Non legge né scrive mai il workspace condiviso da sola.
    /// </summary>
    public PassoSonda SondaRadiceCondivisa(MonitorRaggiungibilitaRadice monitor, bool forza)
    {
        if (!InizializzazioneWorkspaceCompletata || Config == null)
            return new PassoSonda(false, false, null);
        lock (_gateSondaRadice)
        {
            var esito = RadiceCondivisaSonda.Verifica(Config.CartellaCondivisa);
            var passo = monitor.Passo(esito, _statoCondivisione.Stato, forza, DateTime.Now);
            if (passo.NuovoStato != null) StatoCondivisione = passo.NuovoStato;
            return passo;
        }
    }

    private void RileggiDaDisco(bool saltaRefreshTestCases, bool saltaRefreshNonConformita, RefreshEsclusioni esclusioni)
    {
        if (Paths == null || TestCases == null || Master == null || NonConformita == null || ChangeRequests == null || Segnalazioni == null)
            return;

        // alias di chiave non ancora salvato: si ritenta a ogni giro, sotto lo stesso gate di inizializzazione/cambio cartella
        if (_configDaSalvare) lock (_gateInizializzazione) SalvaConfigSicuro();
        // Il refresh periodico è una LETTURA del workspace: availability, compatibility e transaction barrier
        // vengono verificati senza creare `.testloom-write-probe` né aggiornare il manifest. Il probe forte resta
        // obbligatorio allo startup e quando si tenta di uscire da uno stato degradato, così un mount/permesso
        // tornato disponibile non viene dichiarato operativo senza aver prima dimostrato una scrittura reale.
        var statoPrecedente = StatoCondivisione.Stato;
        var disponibilita = CondivisioneHealthService.VerificaDisponibilitaSenzaScrivere(Config.CartellaCondivisa);
        var preflight = CompatibilitaVersioneService.VerificaSenzaScrivere(Paths, disponibilita);
        if (preflight.Stato != Models.StatoCondivisione.Operativa)
        {
            StatoCondivisione = preflight;
            if (StatoCondivisione.Stato == Models.StatoCondivisione.NonDisponibile) return;
        }
        else
        {
            // Prima di qualunque lettura dei repository, congela la fotografia se un altro client sta
            // committando. In stato già operativo NON eseguiamo alcun probe mutante: il timer gira ogni cinque minuti.
            if (!SharedWorkflowTransactionGuard.CanReadConsistently(Paths, out var transactionMessage))
            {
                StatoCondivisione = new EsitoStatoCondivisione
                {
                    Stato = Models.StatoCondivisione.SolaLettura,
                    Messaggio = transactionMessage,
                    VerificatoIl = DateTime.Now
                };
                return;
            }

            if (statoPrecedente == Models.StatoCondivisione.Operativa)
            {
                StatoCondivisione = preflight;
            }
            else
            {
                // Recovery da SolaLettura/NonDisponibile: prima di riabilitare i writer pretendiamo di nuovo
                // la stessa prova create/flush/delete usata allo startup. Un preflight verde da solo non basta.
                var healthScrittura = CondivisioneHealthService.Verifica(Config.CartellaCondivisa);
                // 6.6.1 (correzione 4, decisione B): solo in modalità di rete si provano le sottocartelle scritte da TestLoom. Una
                // sottocartella che rifiuta la prova è un avviso per quel percorso: non tiene la radice in sola lettura.
                if (healthScrittura.Stato == Models.StatoCondivisione.Operativa && ModalitaCondivisioneService.ERete(Paths.Root))
                {
                    foreach (var dir in CondivisioneHealthService.SottocartelleNonScrivibili(Paths))
                        DiagnosticTraceService.Registra("Condivisione.SottocartellaNonScrivibile",
                            $"percorso={dir}; avviso: scrittura rifiutata per questo percorso; la radice resta operativa");
                }
                // 6.6.1 (correzione 2, isteresi): una perdita di scrittura della radice si chiude solo dopo due prove riuscite
                // consecutive; una prova fallita azzera il conteggio. Finché la perdita resta attiva lo stato è sola lettura.
                var puoRiprendere = SharedWriteLossMonitor.PuoRipristinare(Paths.Root, healthScrittura.Stato == Models.StatoCondivisione.Operativa);
                if (healthScrittura.Stato != Models.StatoCondivisione.Operativa || puoRiprendere)
                {
                    var compat = CompatibilitaVersioneService.VerificaSenzaScrivere(Paths, healthScrittura);
                    StatoCondivisione = compat.Stato == Models.StatoCondivisione.Operativa
                        ? CompatibilitaVersioneService.Applica(Paths, healthScrittura)
                        : compat;
                }
            }
        }

        // Se il preflight ci ha lasciati in sola lettura per una ragione non legata alla disponibilità, la
        // barriera resta comunque autorevole e impedisce di leggere un mezzo stato.
        if (!SharedWorkflowTransactionGuard.CanReadConsistently(Paths, out var finalTransactionMessage))
        {
            StatoCondivisione = new EsitoStatoCondivisione
            {
                Stato = Models.StatoCondivisione.SolaLettura,
                Messaggio = finalTransactionMessage,
                VerificatoIl = DateTime.Now
            };
            return;
        }

        if (!saltaRefreshTestCases) RileggiTestCases(esclusioni.PercorsoTestCaseProtetto);
        CaricaModalitaAccesso();
        if (!saltaRefreshNonConformita)
        {
            _progress.Fase(FaseBootstrap.CaricamentoNonConformita, "Ricostruzione dell'indice locale delle Non Conformità in corso…");
            NonConformita.RefreshIndex((elaborati, totale) => _progress.Avanzamento(
                FaseBootstrap.CaricamentoNonConformita,
                "Ricostruzione dell'indice locale delle Non Conformità in corso…", elaborati, totale));
        }
        _progress.Fase(FaseBootstrap.CaricamentoDatiCorrelati, "Caricamento change request e segnalazioni…");
        // C5 (diagnosi_sync/diagnosi.md): una Change Request con modifiche non salvate esclude SOLO questo
        // repository dal refresh, non più l'intero giro — Test/NC/Segnalazioni sopra e sotto continuano ad
        // aggiornarsi normalmente.
        if (!esclusioni.EscludiChangeRequest) ChangeRequests.RefreshIndex();
        Segnalazioni.RefreshIndex();
        RiallineaOperatoreCorrente();
        var templatesVisualizzati = Master.TestTemplate.Load();
        // La mappa scoped e' sostituita atomicamente: le righe di Home/Report possono leggerla senza
        // deserializzare il master per ciascuno dei 100k Test, anche durante un refresh concorrente.
        Volatile.Write(ref _nomiTestTemplate,
            templatesVisualizzati.ToDictionary(t => t.Uid, t => t.Nome));
        TestTemplateDisplay.Aggiorna(templatesVisualizzati);
        EseguiManutenzioneStorage(force: false);
    }

    /// <summary>Nome template dalla fotografia di QUESTA istanza: Load() deserializza a ogni chiamata,
    /// quindi non va invocato per ogni riga delle pagine Home/Report a 100k Test.</summary>
    public string NomeTestTemplate(Guid uid) =>
        Volatile.Read(ref _nomiTestTemplate).TryGetValue(uid, out var nome) ? nome : "?";

    /// <summary>Fotografia della mappa dei nomi usata da <see cref="NomeTestTemplate"/>: la ricerca testuale della Home
    /// deve cercare negli stessi nomi (stessa istantanea), non in un secondo caricamento del master.</summary>
    internal IReadOnlyDictionary<Guid, string> NomiTestTemplateCorrenti => Volatile.Read(ref _nomiTestTemplate);

    /// <param name="ottimizzaStatisticheSqlite">Vedi LocalSqliteMaintenanceService.OptimizeWorkspace: false SOLO
    /// per la manutenzione avviata in coda all'avvio (costruttore, force:true), che altrimenti può completare
    /// (in background, Task.Run) mentre la Home appena mostrata dal bootstrap ha già popolato le proprie cache
    /// di copertura: un PRAGMA optimize senza scritture di dominio non le invalida (P2.6: la revisione dell'indice non
    /// avanza), ma costa I/O alla PRIMA rivisita
    /// della Home. La manutenzione periodica (force:false, ogni 12h) e quella esplicita (ForzaManutenzioneStorage)
    /// restano invariate: default true.</param>
    private void EseguiManutenzioneStorage(bool force, bool ottimizzaStatisticheSqlite = true)
    {
        if (!CartellaValida || StatoCondivisione.Stato != Models.StatoCondivisione.Operativa) return;
        var nowUtc = DateTime.UtcNow;
        if (!force && nowUtc - _lastStorageMaintenanceUtc < StorageMaintenanceInterval) return;

        lock (_storageMaintenanceGate)
        {
            nowUtc = DateTime.UtcNow;
            if (!force && nowUtc - _lastStorageMaintenanceUtc < StorageMaintenanceInterval) return;
            var watch = Stopwatch.StartNew();
            try
            {
                UltimaManutenzioneStorage = StorageMaintenanceService.Run(Paths, Locks, Notifiche, DateTime.Now, ottimizzaStatisticheSqlite, _annullaManutenzione.Token);
                _lastStorageMaintenanceUtc = nowUtc;
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Manutenzione periodica storage", ex);
                _lastStorageMaintenanceUtc = nowUtc;
            }
            finally
            {
                watch.Stop();
                PerformanceDiagnosticsService.Record(
                    TestCases?.LocalWorkspaceDirectory,
                    "storage.maintenance",
                    watch.Elapsed,
                    UltimaManutenzioneStorage?.TotaleArtefattiEliminati ?? 0,
                    UltimaManutenzioneStorage == null
                        ? "failed-or-empty"
                        : $"sqliteOptimized={UltimaManutenzioneStorage.DatabaseLocaliOttimizzati}; sqliteVacuum={UltimaManutenzioneStorage.DatabaseLocaliVacuum}; bytesFreed={UltimaManutenzioneStorage.ByteLocaliRecuperati}");
            }
        }
    }

    public StorageMaintenanceResult? ForzaManutenzioneStorage()
    {
        EseguiManutenzioneStorage(force: true);
        return UltimaManutenzioneStorage;
    }

    public void RegistraAudit(string azione, string target, string dettagli = "")
    {
        var audit = _systemAudit;
        if (audit == null) return;
        try
        {
            audit.Add(new SystemAuditEvent
            {
                Operatore = Config.OperatoreAttivo,
                Azione = azione,
                Target = target,
                Dettagli = dettagli
            });
        }
        catch (Exception ex) { CrashLogService.Registra($"Audit sistema: {azione}", ex); }
    }

    public IReadOnlyList<SystemAuditEvent> LeggiAuditSistema() =>
        _systemAudit == null || !CartellaValida ? Array.Empty<SystemAuditEvent>() : _systemAudit.GetAll(Paths);

    private string TryRelativePath(string path)
    {
        try { return Paths == null ? path : Path.GetRelativePath(Paths.Root, path); }
        catch { return path; }
    }

    private void NotificaForkApertiAiMaster()
    {
        var recordForks = RecordJournal?.OpenForks ?? Array.Empty<RecordFork>();
        if (TestCases.JournalForks.Count == 0 && recordForks.Count == 0) return;
        try
        {
            var masters = Master.Operatori.Load().Where(o => !o.Disattivo && o.Ruolo == RuoloOperatore.Master).ToList();
            foreach (var master in masters)
            {
                var existing = Notifiche.GetAll(master.Nome).Select(n => n.Uid).ToHashSet();
                foreach (var fork in recordForks)
                {
                    var uid = DeterministicGuid($"TestLoom|RecordFork|{master.Nome}|{fork.Key}");
                    if (existing.Contains(uid) || !_forkNotificationsAttempted.Add(uid)) continue;
                    Notifiche.AddDeterministic(new NotificaApp
                    {
                        Uid = uid,
                        Data = DateTime.Now,
                        Destinatario = master.Nome,
                        Tipo = "ConflittoSincronizzazione",
                        Titolo = $"Conflitto fra postazioni: {fork.EntityId}",
                        Messaggio = "Due postazioni hanno modificato lo stesso record partendo dalla stessa versione. " +
                            "Le versioni disponibili sono conservate e il record è bloccato: apri Impostazioni → Conflitti fra postazioni.",
                        ElementoId = fork.EntityId,
                        Letta = false
                    });
                    existing.Add(uid);
                }
                foreach (var fork in TestCases.JournalForks)
                {
                    var key = TestCaseForkResolution.Key(fork);
                    var uid = DeterministicGuid($"TestLoom|DelayedFork|{master.Nome}|{key}");
                    if (existing.Contains(uid) || !_forkNotificationsAttempted.Add(uid)) continue;
                    Notifiche.AddDeterministic(new NotificaApp
                    {
                        Uid = uid,
                        Data = DateTime.Now,
                        Destinatario = master.Nome,
                        Tipo = "ConflittoSincronizzazione",
                        Titolo = $"Conflitto OneDrive: {fork.EntityId}",
                        Messaggio = "Sono disponibili versioni divergenti dello stesso Test. Apri Impostazioni → Generale → Conflitti di sincronizzazione Test per risolverle.",
                        ElementoId = fork.EntityId,
                        Letta = false
                    });
                    existing.Add(uid);
                }
            }
        }
        catch (Exception ex) { CrashLogService.Registra("Notifica fork aperti ai Master", ex); }
    }

    /// <summary>
    /// Stesso schema di <see cref="NotificaForkApertiAiMaster"/> per i conflitti dei cataloghi master (teste concorrenti
    /// Phase D): una notifica per Master e per insieme di versioni, tipo ConflittoSincronizzazione (non silenziabile).
    /// La firma delle versioni nel Uid fa si' che un conflitto nuovo o cambiato venga notificato di nuovo, mentre lo stesso
    /// conflitto non viene riproposto a ogni giro periodico.
    /// </summary>
    private void NotificaConflittiMasterAiMaster()
    {
        if (!CartellaValida || Paths == null || !PhaseDMasterJournalService.IsInitialized(Paths.Root)) return;
        try
        {
            var conflitti = PhaseDMasterConflictResolver.Elenca(Paths.Root);
            if (conflitti.Count == 0) return;
            var masters = Master.Operatori.Load().Where(o => !o.Disattivo && o.Ruolo == RuoloOperatore.Master).ToList();
            foreach (var master in masters)
            {
                var existing = Notifiche.GetAll(master.Nome).Select(n => n.Uid).ToHashSet();
                foreach (var conflitto in conflitti)
                {
                    var uid = DeterministicGuid(
                        $"TestLoom|MasterConflict|{master.Nome}|{conflitto.Catalogo}|{conflitto.EntityUid:N}|{conflitto.Firma}");
                    if (existing.Contains(uid) || !_forkNotificationsAttempted.Add(uid)) continue;
                    Notifiche.AddDeterministic(new NotificaApp
                    {
                        Uid = uid,
                        Data = DateTime.Now,
                        Destinatario = master.Nome,
                        Tipo = "ConflittoSincronizzazione",
                        Titolo = $"Conflitto nel catalogo {conflitto.Catalogo}",
                        Messaggio = "Due postazioni hanno modificato la stessa voce partendo dalla stessa versione. " +
                            "La voce mostra provvisoriamente la versione più recente: apri Impostazioni → Conflitti nei cataloghi master per scegliere quella corretta.",
                        ElementoId = string.IsNullOrWhiteSpace(conflitto.BusinessKey) ? conflitto.EntityUid.ToString("N") : conflitto.BusinessKey,
                        Letta = false
                    });
                    existing.Add(uid);
                }
            }
        }
        catch (Exception ex) { CrashLogService.Registra("Notifica conflitti cataloghi master", ex); }
    }

    /// <summary>Notifica i Master per ogni Pacchetto divergente fra veicoli e non ancora decisa (6.6.0). Stesso schema di
    /// <see cref="NotificaForkApertiAiMaster"/>: uid deterministico per Master + WU + firma della divergenza, quindi la stessa
    /// divergenza non viene riproposta, mentre un esito cambiato (firma nuova) genera una nuova notifica. Il calcolo sull'intero
    /// archivio Test si rifa' solo quando cambiano il catalogo WU o la lista dei Test (stessa impronta usata dalla pagina WU Status).</summary>
    internal void NotificaWuDaDecidereAiMaster()
    {
        // Mai sul thread UI: il ricalcolo puo' scorrere l'intero archivio Test. Se il giro cade sul thread UI, la notifica
        // viene semplicemente rinviata al giro successivo (stessa regola di AggiornaSaluteCartellaCondivisa).
        if (!CartellaValida || SulThreadUi()) return;
        try
        {
            var impronta = Master.RevisioniUdt.CatturaImpronteCatalogoCorrente();
            var test = TestCases.GetAll();
            if (impronta == null || impronta != _wuDaDecidereImpronta || !ReferenceEquals(test, _wuDaDecidereTests))
            {
                _wuDaDecidereCorrenti = RevisioneUdtService.WuDaDecidere(Master.RevisioniUdt.Load(), test);
                _wuDaDecidereImpronta = impronta;
                _wuDaDecidereTests = test;
            }
            if (_wuDaDecidereCorrenti.Count == 0) return;
            var masters = Master.Operatori.Load().Where(o => !o.Disattivo && o.Ruolo == RuoloOperatore.Master).ToList();
            foreach (var master in masters)
            {
                var existing = Notifiche.GetAll(master.Nome).Select(n => n.Uid).ToHashSet();
                foreach (var (wu, firma) in _wuDaDecidereCorrenti)
                {
                    var uid = DeterministicGuid($"TestLoom|WuDaDecidere|{master.Nome}|{wu}|{firma}");
                    if (existing.Contains(uid) || !_forkNotificationsAttempted.Add(uid)) continue;
                    Notifiche.AddDeterministic(new NotificaApp
                    {
                        Uid = uid,
                        Data = DateTime.Now,
                        Destinatario = master.Nome,
                        Tipo = NotificaPreferenzeService.TipoWuDaDecidere,
                        Titolo = $"Pacchetto da decidere: {wu}",
                        Messaggio = "La Pacchetto ha esiti discordi fra veicoli (almeno un veicolo funziona e almeno un veicolo ha problemi) " +
                            "e i veicoli da decidere non hanno ancora una decisione valida. Apri WU Status e decidi per ogni veicolo.",
                        ElementoId = wu,
                        Letta = false
                    });
                    existing.Add(uid);
                }
            }
        }
        catch (Exception ex) { CrashLogService.Registra("Notifica WU da decidere", ex); }
    }

    private static Guid DeterministicGuid(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    /// <summary>Valutazione non mutante per l'avviso "cartella incerta". Gira dal giro periodico in background e non tocca il
    /// gate di scrittura: un errore imprevisto azzera l'avviso invece di mostrarne uno vecchio. Le rilettura sincrone sul
    /// thread UI (RicaricaTutto dopo un ripristino o un cambio di modalità) saltano la valutazione: l'avviso si aggiorna al
    /// giro periodico successivo e la UI non fa I/O aggiuntivo.</summary>
    private void AggiornaSaluteCartellaCondivisa()
    {
        if (SulThreadUi()) return;
        if (Paths == null || !InizializzazioneWorkspaceCompletata || OperatoreCorrente == null)
        {
            _saluteCartellaCondivisa = null;
            return;
        }
        try { _saluteCartellaCondivisa = SharedFolderHealthMonitor.Valuta(Paths); }
        catch (Exception ex)
        {
            _saluteCartellaCondivisa = null;
            CrashLogService.Registra("Valutazione salute cartella condivisa", ex);
        }
    }

    /// <summary>True solo sul thread UI di Avalonia. Senza piattaforma grafica (test, host console MultiPcHost) non esiste
    /// un thread UI: l'eccezione del dispatcher assente equivale a "non sul thread UI".</summary>
    private static bool SulThreadUi()
    {
        try { return Avalonia.Threading.Dispatcher.UIThread.CheckAccess(); }
        catch { return false; }
    }

    private void AggiornaJournalENotificaFork()
    {
        var watch = Stopwatch.StartNew();
        AggiornaSaluteCartellaCondivisa();
        TentaMarcatoreCatalogoPendente();
        TentaAlzamentoSchemaPendente();
        TestCases.RefreshJournalMetadata();
        try { RecordJournal?.Refresh(); }
        catch (Exception ex) { CrashLogService.Registra("Record journal: giro periodico", ex); }
        try
        {
            // Merge automatico dei fork eleggibili (6.4.1, punto B) — sempre in background, mai sul thread UI:
            // questo metodo gira già solo dal giro periodico/di sincronizzazione. Se questo PC ha applicato
            // almeno un merge, i fork chiusi vanno rivisti PRIMA di notificare i Master, altrimenti la notifica
            // del giro corrente elencherebbe ancora fork appena risolti.
            var mergeReport = TestCaseAutoForkMergeService.EseguiSuForkNoti(Paths, TestCases, RegistraAudit);
            if (mergeReport.Applicati > 0) TestCases.RefreshJournalMetadata();
        }
        catch (Exception ex) { CrashLogService.Registra("Merge automatico fork: giro periodico", ex); }
        try
        {
            DelayedForkRecoveryService.CleanupResolvedDirectories(
                Paths,
                TestCases.GetAll(includeEliminati: true),
                TestCases.JournalForks);
        }
        catch (Exception ex) { CrashLogService.Registra("Cleanup recovery fork risolti", ex); }
        NotificaForkApertiAiMaster();
        NotificaConflittiMasterAiMaster();
        NotificaWuDaDecidereAiMaster();
        watch.Stop();
        PerformanceDiagnosticsService.Record(
            TestCases.LocalWorkspaceDirectory,
            "journal.refresh",
            watch.Elapsed,
            TestCases.JournalForks.Count,
            "openForks");
    }

    private const string MessaggioRicostruzioneIndiceTest = "Ricostruzione dell'indice locale dei Test in corso…";

    private void ReportaAvanzamentoTest(int elaborati, int totale) =>
        _progress.Avanzamento(FaseBootstrap.CaricamentoTest, MessaggioRicostruzioneIndiceTest, elaborati, totale);

    /// <param name="percorsoProtetto">Path del TestCase attualmente in modifica in un editor con modifiche non
    /// salvate (C5, diagnosi_sync/diagnosi.md): escluso solo dal drain incrementale più sotto. Vedi
    /// <see cref="RefreshEsclusioni.PercorsoTestCaseProtetto"/> per il perché la riconciliazione completa
    /// (rami sopra) non applica la stessa esclusione.</param>
    private void RileggiTestCases(string? percorsoProtetto = null)
    {
        var tracker = _testCaseChangeTracker;
        if (tracker == null)
        {
            // Fotografia del journal PRIMA della ricostruzione: gli eventi scritti durante RefreshIndex non devono
            // essere marcati consumati dalla baseline (P2.7). Vedi JournalChangeFeedService.CatturaFotografia.
            var fotografia = _testJournalFeed?.CatturaFotografia();
            _progress.Fase(FaseBootstrap.CaricamentoTest, MessaggioRicostruzioneIndiceTest);
            var watch = Stopwatch.StartNew();
            TestCases.RefreshIndex(ReportaAvanzamentoTest);
            watch.Stop();
            PerformanceDiagnosticsService.Record(
                TestCases.LocalWorkspaceDirectory,
                "testcases.full-reconciliation",
                watch.Elapsed,
                TestCases.Count(includeEliminati: true),
                "watcher-unavailable");
            _testCaseNeedsStartupReconciliation = !(_testJournalFeed?.EstablishBaseline(fotografia) ?? true);
            AggiornaJournalENotificaFork();
            return;
        }

        var feedBatch = _testJournalFeed?.Poll() ?? new JournalFeedBatch();
        var fullRequired = _testCaseNeedsStartupReconciliation ||
                           feedBatch.RequiresFullReconciliation ||
                           tracker.RequiresFullReconciliation ||
                           TestCases.NeedsFullReconciliation(TestCaseDeepReconciliationInterval);
        if (fullRequired)
        {
            var failureGeneration = tracker.CaptureFailureGeneration();
            // Fotografia del journal PRIMA della ricostruzione (P2.7): vedi il ramo senza tracker qui sopra.
            var fotografia = _testJournalFeed?.CatturaFotografia();
            _ = tracker.Drain();
            _progress.Fase(FaseBootstrap.CaricamentoTest, MessaggioRicostruzioneIndiceTest);
            var watch = Stopwatch.StartNew();
            TestCases.RefreshIndex(ReportaAvanzamentoTest);
            watch.Stop();
            PerformanceDiagnosticsService.Record(
                TestCases.LocalWorkspaceDirectory,
                "testcases.full-reconciliation",
                watch.Elapsed,
                TestCases.Count(includeEliminati: true),
                _testCaseNeedsStartupReconciliation ? "startup-baseline" : "periodic-or-watcher-recovery");
            tracker.MarkFullReconciliationCompleted(failureGeneration);
            var baselineOk = _testJournalFeed?.EstablishBaseline(fotografia) ?? true;
            _testCaseNeedsStartupReconciliation = !baselineOk;
            AggiornaJournalENotificaFork();
            return;
        }

        var changed = tracker.Drain().Concat(feedBatch.Paths)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Piano 6.4.2 §1: rimosso il gate cross-file basato sulle receipt (un JSON di dominio sintatticamente
        // valido è sempre leggibile), quindi non serve più sorvegliare separatamente il ledger dei commit per
        // far "guarire" un Test rimasto Pending in attesa di una receipt — quel caso non esiste più. Un file
        // già in FileConProblemi (o ancora in tolleranza, vedi PercorsiConProblemiTracciati e
        // FileProblemToleranceTracker) resta comunque ririchiesto ad ogni giro incrementale: se nel frattempo
        // è stato corretto (es. da un altro PC, o da "Risolvi"), sparisce dalla lista senza dover attendere la
        // riconciliazione profonda periodica. Deve includere anche i path SOLO in tolleranza (non ancora
        // visibili): altrimenti un file troncato durante l'evento watcher che colse il primo errore, senza un
        // secondo evento watcher successivo, non riceverebbe mai il secondo tentativo che la tolleranza
        // richiede prima di (dis)comparire — resterebbe congelato sull'ultima copia buona a tempo indeterminato.
        if (TestCases.PercorsiConProblemiTracciati.Count > 0)
            changed = changed.Concat(TestCases.PercorsiConProblemiTracciati)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // C5 (diagnosi_sync/diagnosi.md): il record che un editor ha aperto con InModifica=true non viene
        // ricaricato in questo giro incrementale, cosi' non ricompare/sparisce da FileConProblemi ne' cambia
        // stato proprio mentre l'utente lo guarda. Tutti gli ALTRI path rimangono nella lista e si aggiornano
        // normalmente: prima dell'aggiunta di questa esclusione l'intero refresh veniva saltato per TUTTI.
        if (!string.IsNullOrEmpty(percorsoProtetto))
            changed = changed.Where(p => !string.Equals(p, percorsoProtetto, StringComparison.OrdinalIgnoreCase)).ToList();
        if (changed.Count > 0)
        {
            var watch = Stopwatch.StartNew();
            TestCases.RefreshPaths(changed);
            watch.Stop();
            PerformanceDiagnosticsService.Record(
                TestCases.LocalWorkspaceDirectory,
                "testcases.incremental-refresh",
                watch.Elapsed,
                changed.Count);
        }
        _testJournalFeed?.Ack(feedBatch);
        AggiornaJournalENotificaFork();
    }

    public void NotificaDatiRicaricati() => DatiRicaricati?.Invoke();

    public IReadOnlyList<FileConProblemi> TuttiIFileConProblemi() =>
        TestCases == null || NonConformita == null || ChangeRequests == null || Segnalazioni == null
            ? Array.Empty<FileConProblemi>()
            : TestCases.FileConProblemi.Concat(NonConformita.FileConProblemi).Concat(ChangeRequests.FileConProblemi).Concat(Segnalazioni.FileConProblemi).ToList();

    public IReadOnlyList<TestCaseDuplicato> TestCaseDuplicati() =>
        TestCases == null ? Array.Empty<TestCaseDuplicato>() : TestCases.DuplicatiId;

    /// <summary>Copie di conflitto OneDrive/SharePoint da mostrare all'utente (6.4.2), per Test, Non Conformità,
    /// Change Request e Segnalazioni. Restano fuori, e solo nella diagnostica, le copie che non richiedono nulla
    /// (contenuto identico, spostamento in corso, versione già assorbita da un merge) e quelle comparse da meno
    /// della tolleranza (<see cref="FileProblemToleranceTracker.MinElapsed"/>, misurata sulla data del file):
    /// durante una sincronizzazione o un merge automatico in corso non deve comparire alcun avviso.</summary>
    public IReadOnlyList<FileConProblemi> TutteLeCopieDiConflitto()
    {
        var tutte = new List<FileConProblemi>();
        if (TestCases != null) tutte.AddRange(TestCases.CopieDiConflitto);
        if (NonConformita != null) tutte.AddRange(NonConformita.CopieDiConflitto);
        if (ChangeRequests != null) tutte.AddRange(ChangeRequests.CopieDiConflitto);
        if (Segnalazioni != null) tutte.AddRange(Segnalazioni.CopieDiConflitto);
        var adesso = FileProblemToleranceTracker.NowUtcOverrideForTests?.Invoke() ?? DateTime.UtcNow;
        return tutte
            .Where(c => c.RichiedeAttenzione && File.Exists(c.Percorso) &&
                        adesso - File.GetLastWriteTimeUtc(c.Percorso) >= FileProblemToleranceTracker.MinElapsed)
            .ToList();
    }

    public bool TestCaseVisibilePerOperatore(TestCase tc)
        => FiltraTestCaseVisibiliPerOperatore([tc]).Any();

    public IEnumerable<TestCase> FiltraTestCaseVisibiliPerOperatore(IEnumerable<TestCase> testCases)
    {
        var veicoliAmmessi = VeicoliAmmessiOperatore();
        return veicoliAmmessi == null ? testCases : testCases.Where(tc => TestCaseVisibileConVeicoli(tc, veicoliAmmessi));
    }

    /// <summary>Vero se il Test ha almeno un veicolo fra quelli ammessi (null = nessuna restrizione).</summary>
    public static bool TestCaseVisibileConVeicoli(TestCase tc, IReadOnlySet<string>? veicoliAmmessi) =>
        veicoliAmmessi == null || tc.VeicoliEffettivi.Any(veicoliAmmessi.Contains);

    /// <summary>Veicoli ammessi all'operatore corrente, o null se non c'e' alcuna restrizione (nessun operatore, ruolo diverso
    /// da Standard, nessuna gamma assegnata, master non disponibile). Legge il catalogo Veicoli una sola volta: chi valuta molti
    /// Test (ricerca globale) lo calcola una volta per ricerca invece che per ogni Test.</summary>
    /// <param name="ancheRuoloGrezzo">Ricerca globale: il perimetro vale se e' Standard il ruolo effettivo O quello grezzo, cosi'
    /// la ricerca non allenta mai la regola piu' restrittiva (la ricerca dei Test leggeva il ruolo grezzo, le segnalazioni quello effettivo).</param>
    public HashSet<string>? VeicoliAmmessiOperatore(bool ancheRuoloGrezzo = false)
    {
        var op = OperatoreCorrente;
        if (op == null || op.GammeAssegnate.Count == 0 || Master == null) return null;
        // RuoloEffettivo, non Ruolo grezzo (piano 6.4.2, §4b): un ruolo manomesso a mano nel JSON non deve
        // sfuggire allo scoping per Gamma valido per Standard.
        var standard = op.RuoloEffettivo == RuoloOperatore.Standard || (ancheRuoloGrezzo && op.Ruolo == RuoloOperatore.Standard);
        if (!standard) return null;
        var gamme = op.GammeAssegnate.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Master.Veicoli.Load()
            .Where(v => gamme.Contains(v.GammaNome)).Select(v => v.Nome)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
