using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App;

public partial class App : Application
{
    private TrayIcons? _trayIcons;

    /// <summary>Soglia sotto la quale il bootstrap non mostra alcuna finestra di avvio. Con indice locale già
    /// presente il bootstrap dura pochi secondi e la schermata non deve comparire né sfarfallare; sopra la
    /// soglia il bootstrap può durare minuti su un workspace nuovo/grande (misurato fino a 5-7 minuti su
    /// 100k+ Test/Non Conformità senza indice locale, feedback utente: "sembra ferma ed è un problema grosso")
    /// e l'utente deve vedere SUBITO che l'app sta lavorando davvero.</summary>
    private static readonly TimeSpan SogliaComparsaAvvio = TimeSpan.FromMilliseconds(500);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ToolTipLayoutBehavior.Configure();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Ultima rete di sicurezza prima di creare ViewModel e finestre: senza questa chiamata gli handler
        // globali di CrashLogService restavano definiti ma mai registrati, lasciando senza evidenza le
        // eccezioni impreviste in background o durante lo shutdown.
        CrashLogService.Installa();
        CrashLogService.InstallaDispatcherUi();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ChiusuraDiagnostica.Registra("processo: uscita (ProcessExit)");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested += (_, _) => ChiusuraDiagnostica.Registra("lifetime: ShutdownRequested");
            // --config <path>: usato SOLO per collaudi isolati (benchmark, verifica manuale) su un config.json
            // dedicato, mai sul file reale in %APPDATA%\TestLoom.App\config.json. Senza questa opzione l'unico
            // modo per provare l'app vera su dati sintetici sarebbe toccare la configurazione dell'utente.
            var configOverride = TrovaArgomentoConfig(desktop.Args);

            // Il costruttore di AppState è potenzialmente il passo più lento dell'intero avvio (ricostruzione
            // dell'indice locale su un workspace condiviso grande/nuovo): farlo qui in modo sincrono lasciava
            // l'app senza ALCUNA finestra per minuti, indistinguibile da un blocco. Da qui in poi il bootstrap
            // gira in background e il thread UI resta reattivo; RunStartupAsync orchestra comparsa (soglia) e
            // chiusura della schermata di avvio.
            _ = RunStartupAsync(desktop, configOverride);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task RunStartupAsync(IClassicDesktopStyleApplicationLifetime desktop, string? configOverride)
    {
        // Dichiarata fuori dal try: se il bootstrap fallisce DOPO averla mostrata, il ramo catch deve poterla
        // chiudere esplicitamente invece di lasciarla aperta indefinitamente (Closing è bloccato finché non
        // viene chiamato ConsentiChiusura, vedi AvvioWindow).
        AvvioWindow? avvio = null;
        // Revisione indipendente (FAIL critico): sul percorso veloce (bootstrap sotto SogliaComparsaAvvio)
        // l'AvvioWindow non viene MAI creata — la prima Window ad esistere e' quella (invisibile, fuori
        // schermo) usata da ScaldaViewFuoriSchermoAsync per scaldare la View "Test"/Editor DENTRO
        // PreparaAvvioAsync, prima che MainWindow esista. Con lo ShutdownMode di default di Avalonia
        // (OnLastWindowClose, non impostato altrove nel repo) il suo host.Close() la renderebbe l'ULTIMA
        // finestra a chiudersi e terminerebbe l'intero processo prima di mostrare MainWindow — stessa classe
        // di bug che AvvioWindow.axaml.cs evita con la guardia Closing/ConsentiChiusura, ma qui senza alcuna
        // guardia possibile perche' la finestra di scaldamento non deve MAI essere vista dall'utente (nessun
        // ConsentiChiusura esplicito, si apre e si chiude sola). ShutdownModeTemporaneo forza quindi
        // OnExplicitShutdown per tutta la finestra temporale in cui possono esistere SOLO finestre non
        // definitive (scaldamento, poi eventualmente AvvioWindow) e ripristina il valore effettivo precedente
        // (mai un valore assunto) SOLO dopo che MainWindow e' stata mostrata: vedi Dispose() sotto, dentro un
        // finally dedicato a mainWindow.Show(). Da quel momento la corretta semantica di chiusura (chiusura
        // utente, riduzione a tray via RiduciInTrayAllaChiusura/AvvioRidotto) torna a dipendere dallo
        // ShutdownMode reale dell'app, invariato rispetto a prima di questa modifica.
        var shutdownModeTemporaneo = new ShutdownModeTemporaneo(
            () => desktop.ShutdownMode, v => desktop.ShutdownMode = v, ShutdownMode.OnExplicitShutdown);
        // Diventa true quando MainWindow e' visibile: da quel momento la sessione e' viva e un errore non deve chiuderla.
        var avviata = false;
        try
        {
            var avvioViewModel = new AvvioViewModel();
            // Costruito QUI, sul thread UI: Progress<T> cattura il SynchronizationContext del dispatcher
            // Avalonia al momento della creazione, quindi ogni Report() successivo da un thread di background
            // arriva già marshallato sul thread giusto — AppState e AvvioViewModel non devono sapere nulla di
            // Dispatcher.
            var progress = new Progress<BootstrapProgress>(avvioViewModel.Aggiorna);

            // Bootstrap di AppState + costruzione di MainViewModel + scaldamento delle pagine più costose alla
            // prima visita (piano 6.4.0 "scaldamento all'avvio"), tutto in UN SOLO Task raced contro la soglia
            // qui sotto: la soglia deve valere per il tempo TOTALE, non solo per AppState. Un bootstrap già
            // veloce (indice locale presente) seguito da uno scaldamento lento (banco enorme, nessun indice
            // scalabile) deve comunque far comparire la finestra di avvio, non lasciare l'utente senza alcun
            // segnale per i secondi che lo scaldamento impiega davvero.
            var lavoroTask = PreparaAvvioAsync(configOverride, progress);

            var soglia = Task.Delay(SogliaComparsaAvvio);
            var primoCompletato = await Task.WhenAny(lavoroTask, soglia);
            if (primoCompletato == soglia && !lavoroTask.IsCompleted)
            {
                // Bootstrap+scaldamento ancora in corso oltre la soglia breve: SOLO ora mostriamo la finestra
                // di avvio. Su un workspace piccolo/già indicizzato questo ramo non viene mai eseguito, per
                // requisito esplicito: niente comparsa/sfarfallio quando non serve.
                avvio = new AvvioWindow { DataContext = avvioViewModel };
                avvio.Show();
            }

            var (state, mainViewModel) = await lavoroTask;

            var mainWindow = new MainWindow { DataContext = mainViewModel, RiduciInTrayAllaChiusura = state.Config.AvvioRidotto };
            desktop.MainWindow = mainWindow;
            // One routed root covers all main-page clicks, filter changes and focused
            // edits (including dynamically generated Test/NC/CR controls).
            UiInteractionTraceService.Attach(mainWindow, "Main");

            var apri = new NativeMenuItem("Apri RBG - QATRIVO");
            apri.Click += (_, _) => mainWindow.MostraDaTray();
            var esci = new NativeMenuItem("Esci");
            esci.Click += async (_, _) => await UiExceptionGuard.Esegui(async () =>
            {
                if (await mainWindow.RichiediUscitaDefinitivaAsync()) desktop.Shutdown();
            }, "Uscita dalla tray (Esci)", mostra: () => ErroreOperazioneDialogo.MostraAsync(mainWindow));
            var menu = new NativeMenu(); menu.Add(apri); menu.Add(new NativeMenuItemSeparator()); menu.Add(esci);
            var tray = new TrayIcon
            {
                ToolTipText = "RBG - QATRIVO · Gestione test e qualità",
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://TestLoom/Assets/rbg-qatrivo.ico"))),
                Menu = menu, IsVisible = true
            };
            tray.Clicked += (_, _) => mainWindow.MostraDaTray();

            // Avviso notifiche Windows (opzionale, Impostazioni: "Abilita notifiche Windows", disattivato di default):
            // tooltip della tray con il conteggio, e lampeggio della barra solo per una notifica nuova con finestra non in primo piano.
            void AggiornaAvvisoNotifiche(int precedente, int nonLette)
            {
                var attivo = state.Config.NotificheWindows && nonLette > 0;
                var nelRiepilogo = mainViewModel.RiepilogoElementi.Count;
                tray.ToolTipText = !attivo ? "TestLoom · Test diagnostici"
                    : nelRiepilogo > 0 ? $"TestLoom · {nonLette} non lette: riepilogo di {nelRiepilogo} notifiche"
                    : nonLette == 1 ? "TestLoom · 1 notifica non letta"
                    : $"TestLoom · {nonLette} notifiche non lette";
                if (state.Config.NotificheWindows && precedente >= 0 && nonLette > precedente)
                    mainWindow.SegnalaNuoveNotifiche();
            }
            // Riepilogo notifiche emesso in questo avvio: conta come aumento da zero, quindi un solo avviso per riepilogo.
            AggiornaAvvisoNotifiche(mainViewModel.NuoveNotificheRiepilogoAllAvvio > 0 ? 0 : -1, mainViewModel.NotificheNonLette);
            mainViewModel.ConteggioNotificheRilevantiCambiato += AggiornaAvvisoNotifiche;
            _trayIcons = new TrayIcons { tray };
            TrayIcon.SetIcons(this, _trayIcons);

            if (state.Config.AvvioRidotto)
                mainWindow.Opened += (_, _) => mainWindow.Hide();

            // La finestra principale va mostrata esplicitamente: a differenza dell'avvio sincrono originale,
            // qui MainWindow viene assegnata DOPO che il ciclo Dispatcher è già partito, quindi il consueto
            // "mostra automaticamente la MainWindow" della lifetime classica non si applica più.
            //
            // try/finally dedicato: da qui in avanti esiste sempre almeno una finestra definitiva (MainWindow),
            // quindi il valore effettivo precedente va ripristinato SUBITO dopo Show() — riuscito o non
            // (un'eventuale eccezione qui non deve lasciare l'app bloccata su OnExplicitShutdown per sempre).
            // Da questo momento la chiusura utente e la riduzione a tray (RiduciInTrayAllaChiusura/AvvioRidotto,
            // gestite da MainWindow stessa) tornano a comportarsi esattamente come prima di questa modifica, e
            // chiudere sotto l'eventuale AvvioWindow (avvio.Close(), sotto) e' sicuro: non e' mai l'ultima
            // finestra rimasta.
            try { mainWindow.Show(); avviata = true; }
            finally { shutdownModeTemporaneo.Dispose(); }

            if (avvio != null)
            {
                avvio.ConsentiChiusura();
                avvio.Close();
            }

            SingleInstanceService.ListenForActivation(mainViewModel.RichiediAttivazione);

            // Il bootstrap può aprire una fotografia locale: riconciliare subito in background evita
            // di attendere il timer periodico per vedere le modifiche avvenute a programma chiuso.
            // Il comando usa le stesse esclusioni degli editor e la stessa sonda del refresh manuale.
            if (state.CartellaValida && state.InizializzazioneWorkspaceCompletata)
                _ = mainViewModel.AggiornaOraCommand.ExecuteAsync(null);

            // Backup giornaliero (6.4.1): pianificato SOLO da qui, mai dentro InizializzaCartella/AppState —
            // MainWindow è già mostrata e lo scaldamento (dentro PreparaAvvioAsync/lavoroTask sopra) è già
            // concluso, quindi il thread dedicato (con il suo ritardo cancellabile) non può competere per I/O
            // con le primissime navigazioni dell'utente (revisione indipendente su fc7e51b, punto 2).
            state.AvviaBackupGiornalieroDopoAvvio();
        }
        catch (Exception ex)
        {
            // Bootstrap o creazione delle finestre falliti in modo imprevisto: l'app non deve restare aperta
            // senza alcuna finestra e senza spiegazione (prima di questo cambiamento un'eccezione qui usciva
            // sincrona da OnFrameworkInitializationCompleted e terminava il processo con AppDomain.UnhandledException;
            // ora siamo dopo il primo await, quindi un'eccezione non gestita diventerebbe solo un
            // UnobservedTaskException silenzioso — la logghiamo esplicitamente e chiudiamo in modo pulito).
            CrashLogService.Registra("Avvio applicazione fallito (RunStartupAsync)", ex);
            // Dopo Show() la sessione e' viva: l'errore resta nel log e l'app continua, senza alcuna chiusura.
            if (avviata) return;
        }
        finally
        {
            // Lo Shutdown dell'avvio fallito e' garantito da questo finally, anche se la pulizia lancia un'eccezione.
            if (!avviata) ChiudiAvvioFallito(desktop, shutdownModeTemporaneo, avvio);
        }
    }

    /// <summary>Avvio fallito prima che la finestra principale sia visibile: si chiude la schermata di avvio, si indica
    /// all'utente dove sono i dettagli e si termina con codice 1. Ogni passo e' protetto, cosi' lo Shutdown non manca.
    /// Il ripristino di ShutdownMode e' esplicito: lo Shutdown(1) funziona con qualunque valore, ma il campo non deve
    /// restare su OnExplicitShutdown se qualcosa lo legge prima dello Shutdown. Idempotente.</summary>
    private static void ChiudiAvvioFallito(IClassicDesktopStyleApplicationLifetime desktop,
        ShutdownModeTemporaneo shutdownModeTemporaneo, AvvioWindow? avvio)
    {
        try { shutdownModeTemporaneo.Dispose(); }
        catch (Exception ex) { CrashLogService.Registra("Ripristino ShutdownMode (avvio fallito)", ex); }
        try
        {
            if (avvio != null)
            {
                avvio.ConsentiChiusura();
                avvio.Close();
            }
        }
        catch (Exception ex) { CrashLogService.Registra("Chiusura schermata di avvio (avvio fallito)", ex); }
        MessaggioNativo.MostraErroreAvvio();
        desktop.Shutdown(1);
    }

    /// <summary>Bootstrap di AppState (thread di pool, invariato) seguito dalla costruzione di MainViewModel e
    /// dallo scaldamento delle pagine (entrambi sul thread UI, cioè quello da cui RunStartupAsync ha chiamato
    /// questo metodo): nessuna delle due cose richiede un thread diverso — MainViewModel non tocca alcun
    /// oggetto Avalonia nel costruttore, e ScaldaPagineAsync sposta SOLO il lavoro pesante vero (NavItem.Crea)
    /// su Task.Run, vedi MainViewModel.PreparaPaginaAsync.</summary>
    private static async Task<(AppState State, MainViewModel MainViewModel)> PreparaAvvioAsync(
        string? configOverride, IProgress<BootstrapProgress> progress)
    {
        var state = await Task.Run(() => configOverride == null
            ? new AppState(progress: progress)
            : new AppState(new AppConfigService(configOverride), progress: progress));

        LocalizationService.Instance.Lingua = state.Config.Lingua;
        var mainViewModel = new MainViewModel(state);

        await ScaldaPagineAsync(state, mainViewModel, progress);

        return (state, mainViewModel);
    }

    /// <summary>
    /// Scaldamento all'avvio (piano 6.4.0, richiesta utente: "le pagine devono stare sotto 700ms anche alla
    /// PRIMA visita"). Le pagine scaldate sono quelle che l'audit prestazioni 2026-09-25 (banco 100k) ha
    /// misurato come uniche a pagare un vero ricalcolo sull'intero archivio alla primissima apertura (WU Status
    /// 2986ms, Report 1527ms, Test 846ms) — Home e le altre pagine non hanno lo stesso costo e non sono qui.
    /// Ogni pagina passa da <see cref="MainViewModel.PreparaPaginaAsync"/>, quindi dallo stesso percorso
    /// (NavItem.Crea) che una prima navigazione reale userebbe: nessuna cache o logica di invalidazione
    /// duplicata qui.
    ///
    /// Reattività dell'AvvioWindow: fra una pagina e l'altra si cede il controllo al thread UI
    /// (Dispatcher.UIThread.InvokeAsync in priorità Background) così, se la finestra è visibile, ha modo di
    /// ridipingersi invece di restare congelata dall'inizio alla fine dello scaldamento. Il lavoro pesante di
    /// ciascuna pagina gira comunque già fuori dal thread UI (vedi PreparaPaginaAsync), quindi questo cedere il
    /// controllo qui serve solo a far arrivare a video il messaggio "Preparazione pagine… X" aggiornato prima
    /// di attendere la pagina successiva, non a spezzare un blocco che altrimenti non ci sarebbe.
    ///
    /// Un fallimento di scaldamento (qualunque pagina, o la View dell'editor sotto) non deve MAI impedire
    /// l'avvio (piano 6.4.0, punto 4): PreparaPaginaAsync già intercetta e registra le proprie eccezioni (con un
    /// budget PER PAGINA, vedi MainViewModel.TimeoutScaldamentoPagina), e lo scaldamento della View dell'editor
    /// è avvolto nello stesso modo qui sotto.
    ///
    /// Tetto totale (revisione indipendente, piano 6.4.0): <see cref="TimeoutTotaleScaldamento"/> limita quanto
    /// TUTTO lo scaldamento (le tre pagine + la View dell'editor) può costare in totale, anche se ogni singola
    /// pagina rispettasse il proprio budget individuale — con 3 pagine da 15s ciascuna il caso peggiore
    /// (15s+15s+15s=45s) supererebbe altrimenti un tetto di 30s. Controllato PRIMA di iniziare ogni pagina (non
    /// interrompe una pagina già in corso, che resta comunque limitata dal proprio budget individuale): una
    /// volta superato, le pagine restanti vengono saltate e loggate, mai attese.
    /// </summary>
    private static async Task ScaldaPagineAsync(AppState state, MainViewModel mainViewModel, IProgress<BootstrapProgress> progress)
    {
        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (navId, titolo) in PagineDaScaldare)
        {
            if (cronometro.Elapsed >= TimeoutTotaleScaldamento)
            {
                CrashLogService.Registra("Scaldamento pagine (avvio)", new TimeoutException(
                    $"Tetto totale di {TimeoutTotaleScaldamento.TotalSeconds:F0}s superato dopo {cronometro.Elapsed.TotalSeconds:F1}s: pagina '{navId}' saltata, non attesa."));
                break;
            }
            progress.Report(new BootstrapProgress
            {
                Fase = FaseBootstrap.PreparazionePagine,
                Messaggio = $"Preparazione pagine… {titolo}",
                Trascorso = cronometro.Elapsed
            });
            var vm = await mainViewModel.PreparaPaginaAsync(navId);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Pre-costruzione della View "Test" (piano 6.4.0, punto 2): a VM già caldo, il costo residuo alla
            // prima visita è nella View stessa (costruzione, JIT dei template DataGrid, primo layout) — non nel
            // ViewModel. Usiamo lo STESSO ViewLocator della navigazione reale (Application.DataTemplates, vedi
            // App.axaml "<local:ViewLocator />") così la View che costruisce qui è la STESSA istanza che
            // ViewLocator.Build restituirà alla prima navigazione reale su "Test" (cache keyed sul ViewModel,
            // vedi ViewLocator._viewCache) — mai una copia scartata. Solo "test": le altre due pagine scaldate
            // (WU Status, Report) non sono nella lista di --profile-page misurata sotto i 700ms già solo con lo
            // scaldamento del ViewModel; l'audit 6.4.0 ha isolato il residuo View sulla sola pagina Test.
            if (navId == "test" && vm != null && cronometro.Elapsed < TimeoutTotaleScaldamento)
            {
                try
                {
                    var locator = Application.Current?.DataTemplates.OfType<ViewLocator>().FirstOrDefault();
                    if (locator?.Build(vm) is Control view && view.Parent is null && view.GetVisualParent() is null)
                        await ScaldaViewFuoriSchermoAsync(view, new Size(1400, 860));
                }
                catch (Exception ex) { CrashLogService.Registra("Scaldamento View pagina Test (avvio)", ex); }

                // Pre-memoizzazione di GetAllOrdinatiPerId (audit prestazioni 6.4.0, dati reali 2026-09-26):
                // TestCaseRepository la calcola pigramente e la tiene in cache (_attiviOrdinatiPerId ??= ...,
                // vedi TestCaseRepository.GetAllOrdinatiPerId, commento "ordinare 104k identificativi... ~200ms
                // ogni volta"), ma prima di questo fix nessun punto dello scaldamento la toccava: il warm-up di
                // "test" sopra usa solo GetAll(), quindi la PRIMA apertura reale dell'editor di un Test pagava
                // ancora quell'ordinamento intero, non solo il costo del proprio costruttore. Lettura pura,
                // nessun ViewModel/DataContext coinvolto. Eseguita FUORI dal thread UI (ordinamento ~200 ms su
                // 100k Test: sul thread UI bloccherebbe il ridisegno della schermata di avvio); il repository la
                // protegge con lo stesso lock (_gate) delle scritture e la invalida su ogni scrittura/refresh.
                try { if (state.CartellaValida) await Task.Run(() => state.TestCases.GetAllOrdinatiPerId()); }
                catch (Exception ex) { CrashLogService.Registra("Scaldamento indice Test ordinati per Id (avvio)", ex); }
            }
        }

        // Editor Test (piano 6.4.0, punto 3): un vero TestCaseEditorViewModel si sottoscrive a
        // AppState.DatiRicaricati e richiede una dismissione esplicita per non restare agganciato per tutta la
        // vita dell'app — costruirne uno "usa e getta" solo per scaldare JIT/template avrebbe bisogno di un
        // audit più ampio di quel costruttore per garantire zero effetti collaterali (nessuna bozza, nessun
        // lock). Ci si limita quindi, come esplicitamente concesso dal piano quando non esiste un modo privo di
        // effetti collaterali, al solo caricamento di TIPO/XAML della View: costruire TestCaseEditorView senza
        // alcun DataContext forza il JIT di InitializeComponent e il parsing del markup compilato (1534 righe)
        // senza toccare alcun Test reale, aprire lock o creare bozze — la view viene scartata subito dopo,
        // senza mai essere aggiunta a un albero visuale o mostrata. Anche questo passo rispetta il tetto totale:
        // è sincrono sul thread UI (nessun Task.Run, nessun I/O di rete), quindi non ha un proprio timeout
        // dedicato, ma non deve partire se il budget è già esaurito.
        if (cronometro.Elapsed >= TimeoutTotaleScaldamento)
        {
            CrashLogService.Registra("Scaldamento pagine (avvio)", new TimeoutException(
                $"Tetto totale di {TimeoutTotaleScaldamento.TotalSeconds:F0}s superato dopo {cronometro.Elapsed.TotalSeconds:F1}s: scaldamento View editor Test saltato."));
            return;
        }
        progress.Report(new BootstrapProgress
        {
            Fase = FaseBootstrap.PreparazionePagine,
            Messaggio = "Preparazione pagine… Editor Test",
            Trascorso = cronometro.Elapsed
        });
        // Solo Measure/Arrange fuori schermo, MAI un TestCaseEditorViewModel vero (revisione indipendente
        // piano 6.4.0, punto 4): TestCaseEditorViewModel.Dispose() elimina INCONDIZIONATAMENTE la bozza
        // autosave dell'operatore corrente quando IsNew (vedi Dispose, "_state.BozzaAutosave.Elimina(...)")
        // — costruire e poi scartare un editor "di prova" per un test nuovo cancellerebbe silenziosamente una
        // bozza reale e non salvata di un'altra sessione interrotta dello stesso operatore. Nessun modo sicuro
        // di scaldare anche il ramo con dati reali (ItemsSource popolate, colonne DataGrid materializzate)
        // senza toccare quel percorso: qui ci si limita, come già prima, alla sola View SENZA DataContext, ma
        // con in più il layout reale (misure prima/dopo riportate nel report di performance, non solo JIT).
        try
        {
            var editorView = new TestCaseEditorView();
            await ScaldaViewFuoriSchermoAsync(editorView, new Size(1400, 860));
        }
        catch (Exception ex) { CrashLogService.Registra("Scaldamento View editor Test (avvio)", ex); }

        // Master.Archivio (piano 6.4.0, punto 4): stessa idea dell'editor sopra, ma qui non esiste NEMMENO un
        // ViewModel da poter riusare — a differenza di "test"/wustatus/report, "impostazioni" non passa mai da
        // _pagineCache (OttieniPagina lo esclude esplicitamente, vedi MainViewModel.OttieniPagina: "if (item.Id
        // == "impostazioni") { primaApertura = true; return item.Crea(); }") e ogni sua sotto-sezione, Archivio
        // incluso, nasce sempre da zero alla visita reale. Scaldare qui un ArchivioViewModel vero comporterebbe
        // costruire i suoi 11 sotto-editor master data (RequisitiViewModel, EcuViewModel, ecc.) senza alcun
        // beneficio di riuso — l'istanza verrebbe scartata e la visita reale ne costruirebbe comunque una
        // propria. Ci si limita quindi, come per l'editor Test, al solo JIT/parsing del markup compilato di
        // ArchivioView (DataGrid con le sue colonne/template) — costo di sola-tipo pagato una volta qui invece
        // che alla prima vera apertura, MAI un ArchivioViewModel/DataContext reale.
        if (cronometro.Elapsed >= TimeoutTotaleScaldamento)
        {
            CrashLogService.Registra("Scaldamento pagine (avvio)", new TimeoutException(
                $"Tetto totale di {TimeoutTotaleScaldamento.TotalSeconds:F0}s superato dopo {cronometro.Elapsed.TotalSeconds:F1}s: scaldamento View Master.Archivio saltato."));
            return;
        }
        progress.Report(new BootstrapProgress
        {
            Fase = FaseBootstrap.PreparazionePagine,
            Messaggio = "Preparazione pagine… Master.Archivio",
            Trascorso = cronometro.Elapsed
        });
        try
        {
            var archivioView = new ArchivioView();
            await ScaldaViewFuoriSchermoAsync(archivioView, new Size(1400, 860));
        }
        catch (Exception ex) { CrashLogService.Registra("Scaldamento View Master.Archivio (avvio)", ex); }

        // Master (Impostazioni) e Master.TipiDiTest (audit prestazioni 6.4.0, dati reali 2026-09-26: 1364ms e
        // 739ms alla prima visita, uniche due pagine sopra i 700ms — vedi perf_datireali-71224a2.csv). Stessa
        // tecnica di ArchivioView/TestCaseEditorView sopra e per lo stesso motivo: ImpostazioniViewModel non
        // passa MAI da _pagineCache (OttieniPagina la esclude esplicitamente) e ogni sotto-sezione di Master
        // (compreso TipiDiTest) nasce sempre da zero a ogni visita (ImpostazioniViewModel.SostituisciPaginaCorrente
        // chiama sempre item.Crea(), mai una cache) — scaldare qui un ViewModel vero non aiuterebbe la prima
        // visita reale, che ne costruirebbe comunque uno proprio. La telemetria interna (navigazione.costruzione)
        // mostra un ImpostazioniViewModel già rapido (~60-100ms): il grosso dei 1364ms non misurati da quella
        // telemetria è nella View (ImpostazioniView.axaml, 323 righe, mai scaldata finora — a differenza di
        // ArchivioView/TestCaseEditorView che lo erano già). Ci si limita quindi, come sopra, al solo
        // JIT/parsing del markup compilato di ImpostazioniView e TestTemplateView, MAI un ViewModel/DataContext
        // reale: nessuna cache aggiuntiva, nessun cambio alla logica di OttieniPagina/SostituisciPaginaCorrente.
        if (cronometro.Elapsed >= TimeoutTotaleScaldamento)
        {
            CrashLogService.Registra("Scaldamento pagine (avvio)", new TimeoutException(
                $"Tetto totale di {TimeoutTotaleScaldamento.TotalSeconds:F0}s superato dopo {cronometro.Elapsed.TotalSeconds:F1}s: scaldamento View Master/Master.TipiDiTest saltato."));
            return;
        }
        progress.Report(new BootstrapProgress
        {
            Fase = FaseBootstrap.PreparazionePagine,
            Messaggio = "Preparazione pagine… Master",
            Trascorso = cronometro.Elapsed
        });
        try
        {
            var impostazioniView = new ImpostazioniView();
            await ScaldaViewFuoriSchermoAsync(impostazioniView, new Size(1400, 860));
        }
        catch (Exception ex) { CrashLogService.Registra("Scaldamento View Master (avvio)", ex); }

        if (cronometro.Elapsed >= TimeoutTotaleScaldamento)
        {
            CrashLogService.Registra("Scaldamento pagine (avvio)", new TimeoutException(
                $"Tetto totale di {TimeoutTotaleScaldamento.TotalSeconds:F0}s superato dopo {cronometro.Elapsed.TotalSeconds:F1}s: scaldamento View Master.TipiDiTest saltato."));
            return;
        }
        progress.Report(new BootstrapProgress
        {
            Fase = FaseBootstrap.PreparazionePagine,
            Messaggio = "Preparazione pagine… Master.TipiDiTest",
            Trascorso = cronometro.Elapsed
        });
        try
        {
            var testTemplateView = new TestTemplateView();
            await ScaldaViewFuoriSchermoAsync(testTemplateView, new Size(1400, 860));
        }
        catch (Exception ex) { CrashLogService.Registra("Scaldamento View Master.TipiDiTest (avvio)", ex); }
    }

    /// <summary>Attacca temporaneamente <paramref name="view"/> a una Window fuori schermo reale (mai
    /// visibile: Opacity=0, posizionata ben oltre i bordi di qualunque desktop reale, senza barra/decorazioni)
    /// per farle eseguire un vero passaggio di styling/Measure/Arrange — a differenza di una View mai agganciata
    /// a un TopLevel, che Avalonia non stila mai (i ControlTheme di FluentTheme si applicano solo con un
    /// StyleHost/TopLevel reale sopra nell'albero). Al termine stacca SEMPRE la View (Content=null prima di
    /// Close, mai il contrario) cosi' Parent/GetVisualParent tornano a null: e' la stessa guardia con cui
    /// ViewLocator decide se una View in cache e' riusabile (ViewLocator.PuoEssereRiusata) — se la lasciassimo
    /// agganciata alla Window di scaldamento, la prima navigazione reale la troverebbe ancora "occupata" e ne
    /// costruirebbe una nuova, vanificando il punto di questo metodo. Cede il controllo un paio di volte allo
    /// stesso modo del cedimento fra una pagina e l'altra sopra, per lasciar decantare eventuali binding/layout
    /// a cascata prima di richiudere.</summary>
    private static async Task ScaldaViewFuoriSchermoAsync(Control view, Size dimensione)
    {
        Window? host = null;
        try
        {
            host = new Window
            {
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowDecorations = WindowDecorations.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(-32000, -32000),
                Opacity = 0,
                Width = dimensione.Width,
                Height = dimensione.Height,
                Content = view,
            };
            host.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        finally
        {
            if (host != null)
            {
                host.Content = null;
                host.Close();
            }
        }
    }

    private static readonly (string NavId, string Titolo)[] PagineDaScaldare =
    {
        ("wustatus", "WU Status"),
        ("report", "Report"),
        ("test", "Test"),
    };

    /// <summary>Tetto totale per l'intero scaldamento (revisione indipendente, piano 6.4.0): vedi il commento
    /// su ScaldaPagineAsync per il perché non è semplicemente "3 × TimeoutScaldamentoPagina". Il bootstrap vero
    /// di AppState (PreparaAvvioAsync, PRIMA di ScaldaPagineAsync) non è mai coperto da questo tetto.</summary>
    private static readonly TimeSpan TimeoutTotaleScaldamento = TimeSpan.FromSeconds(30);

    private static string? TrovaArgomentoConfig(string[]? args)
    {
        if (args == null) return null;
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--config")
            {
                // Un --config tronco non deve ricadere sul config reale del PC durante i collaudi.
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    throw new ArgumentException("--config richiede il percorso di un config.json isolato.");
                return args[i + 1];
            }
        return null;
    }
}
