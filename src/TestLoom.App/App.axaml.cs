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
        // Tema locale applicato PRIMA di qualsiasi finestra (scaldamento, schermata di avvio): senza questo la barra del
        // titolo nativa della prima finestra nasceva chiara (tema Light di App.axaml) anche con preferenza Scuro.
        try { ThemeAppearanceService.Apply(new AppConfigService(configOverride).Load().TemaInterfaccia); }
        catch (Exception ex) { CrashLogService.Registra("Tema iniziale (lettura config locale)", ex); }
        // Durante il bootstrap l'unica finestra eventualmente visibile e' lo splash.
        // Manteniamo OnExplicitShutdown finche' MainWindow non e' mostrata, cosi'
        // la chiusura di una finestra transitoria non termina accidentalmente il processo.
        // Il precaricamento di altre View avviene DOPO Show, senza host temporanei qui.
        // Il valore precedente viene ripristinato dal finally attorno a MainWindow.Show().
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

            // Mostrare il primo segnale VISIBILE prima di avviare l'indicizzazione:
            // anche la soglia di 500ms precedentemente usata qui faceva percepire
            // che il programma non fosse partito. Il bootstrap rimane identico, ma
            // la finestra di progresso viene creata e mostrata PRIMA di AppState.
            var startupClock = System.Diagnostics.Stopwatch.StartNew();
            avvio = new AvvioWindow { DataContext = avvioViewModel };
            avvio.Show();
            var tempoPrimoSegnale = Program.MainStartTimestamp != 0
                ? System.Diagnostics.Stopwatch.GetElapsedTime(Program.MainStartTimestamp)
                : TimeSpan.Zero;
            // Lascia al dispatcher la prima opportunita' di disegnare lo splash:
            // non avviare qui altro lavoro sincrono sul thread UI.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var lavoroTask = PreparaAvvioAsync(configOverride, progress);
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
                tray.ToolTipText = !attivo ? "RBG - QATRIVO · Gestione test e qualità"
                    : nelRiepilogo > 0 ? $"RBG - QATRIVO · {nonLette} non lette: riepilogo di {nelRiepilogo} notifiche"
                    : nonLette == 1 ? "RBG - QATRIVO · 1 notifica non letta"
                    : $"RBG - QATRIVO · {nonLette} notifiche non lette";
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

            // Fotografia del tempo fino a Show: le scritture diagnostiche (WriteThrough)
            // NON devono rallentare il primo frame o tenere aperto lo splash.
            var tempoFinestra = startupClock.Elapsed;
            var cartellaDiagnostica = state.TestCases?.LocalWorkspaceDirectory;
            if (avvio != null)
            {
                avvio.ConsentiChiusura();
                avvio.Close();
            }
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                PerformanceDiagnosticsService.Record(cartellaDiagnostica,
                    "avvio.primo_segnale", tempoPrimoSegnale,
                    detail: "Program.Main -> AvvioWindow.Show (non include creazione processo OS o paint)");
                PerformanceDiagnosticsService.Record(cartellaDiagnostica,
                    "avvio.finestra_pronta", tempoFinestra, detail: "bootstrap+shell (prima del primo paint)");
                DiagnosticTraceService.Registra("Avvio", "MainWindow visibile; precaricamento pagine differito");
            });

            SingleInstanceService.ListenForActivation(mainViewModel.RichiediAttivazione);

            // Il bootstrap può aprire una fotografia locale: riconciliare subito in background evita
            // di attendere il timer periodico per vedere le modifiche avvenute a programma chiuso.
            // Il comando usa le stesse esclusioni degli editor e la stessa sonda del refresh manuale.
            if (state.CartellaValida && state.InizializzazioneWorkspaceCompletata)
                _ = mainViewModel.AggiornaOraCommand.ExecuteAsync(null);

            // Backup come prima: il suo scheduler possiede gia' un ritardo autonomo.
            state.AvviaBackupGiornalieroDopoAvvio();

            // TICKET read-only e browser handoff conservano il loro comportamento
            // e intervallo, ma NON partono durante il bootstrap o il primo paint.
            _ = AvviaServiziBackgroundDopoPrimaFinestraAsync(mainViewModel);

            // Precache opzionale, senza rallentare il primo frame. Non parte durante
            // aggiornamento iniziale, navigazione, onboarding o uso attivo dell'editor.
            _ = ScaldaPagineDopoAvvioAsync(state, mainViewModel, mainViewModel.SelectedNavItem?.Id);
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

    /// <summary>Parte critica dell'avvio: AppState sul thread di pool e MainViewModel sul dispatcher.
    /// Le pagine non iniziali non devono aggiungere secondi al tempo per mostrare MainWindow:
    /// vengono preparate separatamente, quando la Home e' disponibile e la sessione e' inattiva.</summary>
    private static async Task<(AppState State, MainViewModel MainViewModel)> PreparaAvvioAsync(
        string? configOverride, IProgress<BootstrapProgress> progress)
    {
        var state = await Task.Run(() => configOverride == null
            ? new AppState(progress: progress)
            : new AppState(new AppConfigService(configOverride), progress: progress));

        LocalizationService.Instance.Lingua = state.Config.Lingua;
        ThemeAppearanceService.Apply(state.Config.TemaInterfaccia);
        // I due worker read-only TICKET/Browser vengono avviati soltanto dopo
        // il primo frame. Le stesse logiche rimangono attive, cambia il timing.
        var mainViewModel = new MainViewModel(state, avviaServiziBackground: false);
        return (state, mainViewModel);
    }

    private static async Task AvviaServiziBackgroundDopoPrimaFinestraAsync(MainViewModel mainViewModel)
    {
        try
        {
            // Il normale loop Avalonia ha gia' mostrato la shell; il ritardo
            // evita la contesa I/O con la prima navigazione/Home. Se la finestra
            // e' stata chiusa nel frattempo il ViewModel non riavvia i worker.
            await Task.Delay(1000);
            mainViewModel.AvviaServiziBackground();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Avvio differito monitor TICKET e browser", ex);
        }
    }

    /// <summary>
    /// La schermata iniziale deve essere utilizzabile prima di investire CPU/I/O nello
    /// scaldamento delle pagine secondarie. I dati condivisi e i controlli di sicurezza
    /// rimangono nel bootstrap: questa e' soltanto una cache di navigazione.
    /// </summary>
    private static async Task ScaldaPagineDopoAvvioAsync(AppState state, MainViewModel mainViewModel, string? paginaIniziale)
    {
        try
        {
            bool PuoScaldare() => !mainViewModel.Disposto && AvvioPrecachePolicy.PuoScaldare(
                state.CartellaValida, state.InizializzazioneWorkspaceCompletata,
                mainViewModel.MostraPrimoAvvio, mainViewModel.MostraOnboarding,
                mainViewModel.CaricamentoPagina,
                mainViewModel.AggiornamentoInCorso,
                mainViewModel.HaModificheNonSalvate(),
                mainViewModel.CurrentPage != null);

            // Attende la prima finestra e un momento adatto (refresh iniziale
            // terminato). Se l'avvio richiede login, non abbandona definitivamente
            // il precache dopo i primi 8 secondi: lo potra' fare dopo l'accesso.
            // Tetto massimo: nessun polling illimitato durante la sessione.
            for (var tentativo = 0; tentativo < 90; tentativo++)
            {
                await Task.Delay(tentativo == 0 ? 2000 : 1500);
                if (mainViewModel.Disposto) return;
                if (!state.CartellaValida || !state.InizializzazioneWorkspaceCompletata ||
                    mainViewModel.MostraPrimoAvvio || mainViewModel.MostraOnboarding)
                    continue;
                paginaIniziale ??= mainViewModel.SelectedNavItem?.Id;
                // Nessuna pagina iniziale certa: non precachare accidentalmente
                // Home durante il login o un cambio di sessione.
                if (paginaIniziale == null || !PuoScaldare()) continue;

                var tempoScaldamento = System.Diagnostics.Stopwatch.StartNew();
                await ScaldaPagineAsync(state, mainViewModel, progress: null,
                    puoContinuare: PuoScaldare, scaldaView: false,
                    paginaIniziale: paginaIniziale);
                var durata = tempoScaldamento.Elapsed;
                var cartellaLog = state.TestCases?.LocalWorkspaceDirectory;
                _ = Task.Run(() => PerformanceDiagnosticsService.Record(cartellaLog,
                    "avvio.precache_pagine", durata, detail: "post-main-window"));
                return;
            }
            DiagnosticTraceService.Registra("Avvio", "Precache secondaria rinviata: sessione non pronta entro il budget");
        }
        catch (Exception ex)
        {
            // Come il vecchio warmup: il fallimento della cache non e' un fallimento
            // dell'app, e non modifica lo stato dei Test/NC/CR.
            CrashLogService.Registra("Precache pagine dopo avvio", ex);
        }
    }

    /// <summary>
    /// Precaricamento differito di tutte le pagine della sidebar eccetto quella
    /// iniziale, DOPO MainWindow.Show(). L'elenco non e' hard-coded e segue
    /// l'ordine di navigazione dell'utente. I VM pesanti vengono preparati fuori
    /// dal dispatcher (guasto e' creato sul dispatcher per il suo await interno) e
    /// le View in piccoli slot Background tramite ViewLocator, senza layout
    /// fuori schermo o finestre temporanee. Impostazioni riscalda solo l'XAML.
    ///
    /// Il precache e' best-effort: non deve interferire con modifiche, refresh,
    /// onboarding o caricamenti. Ogni pagina e' limitata dal proprio timeout e
    /// il ciclo complessivo dal TimeoutTotaleScaldamento. Gli errori rimangono
    /// nei log locali e non impediscono mai la navigazione reale.
    /// </summary>
    private static async Task ScaldaPagineAsync(AppState state, MainViewModel mainViewModel,
        IProgress<BootstrapProgress>? progress, Func<bool>? puoContinuare = null,
        bool scaldaView = true, string? paginaIniziale = null)
    {
        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        // Tutte le destinazioni disponibili nella sidebar, non soltanto le tre
        // storicamente lente. La pagina iniziale e' gia' montata: non duplicarla.
        // L'ordine rispetta la disposizione configurata dall'utente.
        foreach (var navId in AvvioPrecachePolicy.CreaCodaPagine(
                     mainViewModel.NavItems.Select(item => item.Id),
                     paginaIniziale ?? mainViewModel.SelectedNavItem?.Id))
        {
            if (mainViewModel.Disposto) return;
            var titolo = mainViewModel.NavItems.First(item => item.Id == navId).Titolo;
            // Se l'operatore cambia pagina o modifica un editor, non competere
            // con l'interazione: sospendere temporaneamente il passo successivo.
            // Il precache riprende quando l'app torna inattiva, senza duplicare
            // le pagine gia' preparate. L'attesa ha sempre un tetto finito.
            if (puoContinuare != null && !puoContinuare())
            {
                var pronto = false;
                for (var attesa = 0; attesa < 40; attesa++)
                {
                    await Task.Delay(1000);
                    if (mainViewModel.Disposto) return;
                    if (!puoContinuare()) continue;
                    pronto = true;
                    break;
                }
                if (!pronto) return;
            }
            if (cronometro.Elapsed >= TimeoutTotaleScaldamento)
            {
                CrashLogService.Registra("Scaldamento pagine (avvio)", new TimeoutException(
                    $"Tetto totale di {TimeoutTotaleScaldamento.TotalSeconds:F0}s superato dopo {cronometro.Elapsed.TotalSeconds:F1}s: pagina '{navId}' saltata, non attesa."));
                break;
            }
            progress?.Report(new BootstrapProgress
            {
                Fase = FaseBootstrap.PreparazionePagine,
                Messaggio = $"Preparazione pagine… {titolo}",
                Trascorso = cronometro.Elapsed
            });
            // Impostazioni e' volutamente una pagina NON cachata da OttieniPagina:
            // istanziarne il ViewModel per precache cambierebbe la sua semantica
            // (editor/master rinfrescati a ogni ingresso). Scaldare soltanto
            // l'XAML senza DataContext, senza costruire editor o creare cache.
            if (navId == "impostazioni")
            {
                if (puoContinuare == null || puoContinuare())
                {
                    try
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            _ = new ImpostazioniView();
                        }, DispatcherPriority.Background);
                    }
                    catch (Exception ex)
                    {
                        CrashLogService.Registra("Precache View Impostazioni", ex);
                    }
                }
                continue;
            }

            var durataPagina = System.Diagnostics.Stopwatch.StartNew();
            var vm = await mainViewModel.PreparaPaginaAsync(navId);
            var tempo = durataPagina.Elapsed;
            var cartellaPrestazioni = state.TestCases?.LocalWorkspaceDirectory;
            var esito = vm == null ? $"{navId}:skipped" : $"{navId}:ready";
            _ = Task.Run(() => PerformanceDiagnosticsService.Record(cartellaPrestazioni,
                "avvio.precache_pagina", tempo, detail: esito));

            // Il lavoro di dati/VM e' stato fatto fuori dal thread UI. Avalonia
            // impone invece che le View siano CREATE sul dispatcher: farlo in un
            // piccolo slot a priorita' Background, DOPO il primo render, prepara
            // la vera View riutilizzabile del ViewLocator senza mostrarla o
            // costruire finestre temporanee. Nessun Measure/Arrange sul primo
            // frame e nessun doppio VM quando l'utente apre quella pagina.
            if (vm != null && (puoContinuare == null || puoContinuare()))
            {
                await Task.Delay(120);
                if (puoContinuare == null || puoContinuare())
                {
                    try
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            // Un utente puo' aprire la pagina fra la verifica
                            // PuoScaldare() e questo slot di dispatcher. Non
                            // sostituire una View gia' montata, ne' costruire
                            // un duplicato se un'altra navigazione l'ha usata.
                            if (ReferenceEquals(mainViewModel.CurrentPage, vm) ||
                                mainViewModel.SelectedNavItem?.Id == navId ||
                                mainViewModel.HaModificheNonSalvate())
                                return;
                            var locator = Application.Current?.DataTemplates.OfType<ViewLocator>().FirstOrDefault();
                            locator?.PreparaInCacheSeAssente(vm);
                        }, DispatcherPriority.Background);
                    }
                    catch (Exception ex)
                    {
                        CrashLogService.Registra($"Precache View '{navId}'", ex);
                    }
                }
            }
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Pre-costruzione della View "Test" (piano 6.4.0, punto 2): a VM già caldo, il costo residuo alla
            // prima visita è nella View stessa (costruzione, JIT dei template DataGrid, primo layout) — non nel
            // ViewModel. Usiamo lo STESSO ViewLocator della navigazione reale (Application.DataTemplates, vedi
            // App.axaml "<local:ViewLocator />") così la View che costruisce qui è la STESSA istanza che
            // ViewLocator.Build restituirà alla prima navigazione reale su "Test" (cache keyed sul ViewModel,
            // vedi ViewLocator._viewCache) — mai una copia scartata. Solo "test": le altre due pagine scaldate
            // (WU Status, Report) non sono nella lista di --profile-page misurata sotto i 700ms già solo con lo
            // scaldamento del ViewModel; l'audit 6.4.0 ha isolato il residuo View sulla sola pagina Test.
            if (scaldaView && (puoContinuare == null || puoContinuare()) &&
                navId == "test" && vm != null && cronometro.Elapsed < TimeoutTotaleScaldamento)
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

        // Con la finestra principale gia' visibile NON fare Measure/Arrange delle View
        // secondarie sul dispatcher: lo styling/layout sincrono rallenterebbe i primi input.
        // Le VM preparate restano riutilizzabili e aggiornate alla vera navigazione.
        if (!scaldaView || (puoContinuare != null && !puoContinuare())) return;

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
        progress?.Report(new BootstrapProgress
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
        progress?.Report(new BootstrapProgress
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
        progress?.Report(new BootstrapProgress
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
        progress?.Report(new BootstrapProgress
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

    /// <summary>Tetto totale per l'intero scaldamento (revisione indipendente, piano 6.4.0): vedi il commento
    /// su ScaldaPagineAsync per il perché non è semplicemente "3 × TimeoutScaldamentoPagina". Il bootstrap vero
    /// di AppState (PreparaAvvioAsync, PRIMA di ScaldaPagineAsync) non è mai coperto da questo tetto.</summary>
    // Dopo MainWindow.Show: non blocca piu' il lancio. Budget per tutte le
    // destinazioni secondarie (ciascuna ha anche il proprio timeout).
    private static readonly TimeSpan TimeoutTotaleScaldamento = TimeSpan.FromSeconds(105);

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
