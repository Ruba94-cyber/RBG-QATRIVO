using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

public partial class NavItem : ObservableObject
{
    /// <summary>Identificativo stabile per persistere l'ordine scelto dall'utente (Addendum: grill-me
    /// 2026-08-18, follow-up): a differenza di Titolo, che per alcune voci segue l'Etichetta rinominabile del
    /// catalogo campi di sistema, questo non cambia mai — l'ordine salvato non si "perde" se un campo viene
    /// rinominato.</summary>
    public string Id { get; init; } = "";
    public string Titolo { get; init; } = "";
    /// <summary>Icona PREDEFINITA della voce; quella mostrata è <see cref="IconaEffettiva"/>.</summary>
    public string Icona { get; init; } = "";
    /// <summary>"main" o "impostazioni": assegnato da IconeSezioniService.Applica.</summary>
    public string Ambito { get; set; } = "";
    public string ChiaveIcona => IconeSezioniService.Chiave(Ambito, Id);
    /// <summary>Override personale per PC (AppConfig.IconeSezioni); null = predefinita.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconaEffettiva), nameof(HaIconaPersonalizzata))]
    private string? iconaPersonalizzata;
    public string IconaEffettiva => IconaPersonalizzata ?? Icona;
    public bool HaIconaPersonalizzata => IconaPersonalizzata != null;
    public Func<ViewModelBase> Crea { get; init; } = null!;
    [ObservableProperty] private bool evidenziatoTour;
}

public sealed record RolePreviewOption(RuoloOperatore Ruolo, string Etichetta);

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly AppState _state;
    private readonly System.Timers.Timer _refreshTimer;
    private readonly PolarionBackgroundSyncService _polarionBackgroundSync;
    private readonly PolarionBrowserLinkMonitor _polarionBrowserLinkMonitor;
    private readonly System.Timers.Timer _sondaRadiceTimer;
    private readonly MonitorRaggiungibilitaRadice _monitorRadice = new();
    private bool _sondaRadiceInCorso;
    private readonly System.Timers.Timer _debounceRefreshWatcher;
    private readonly Action _cambiamentiSuDiscoRilevati;
    private readonly PropertyChangedEventHandler _linguaCambiata;
    private readonly Action _datiRicaricati;
    private bool _disposed;
    private bool _controlloSessioneOperatore;
    private bool _mantieniPaginaDopoAccesso;
    private long _richiestaNavigazione;
    private long _generazioneCachePagine;

    /// <summary>Intervallo di assestamento (debounce) tra un evento del FileSystemWatcher e il refresh che
    /// innesca (C4, diagnosi_sync/diagnosi.md): una sincronizzazione OneDrive che consegna molti file di fila
    /// (un salvataggio Test tocca 3+ path, vedi diagnosi §1) deve produrre UN SOLO refresh, non uno per
    /// evento. `internal` e non `const`/`readonly` apposta: i test possono accorciarlo (impostarlo PRIMA di
    /// costruire il MainViewModel, che lo legge una volta sola nel costruttore) per non dover davvero
    /// attendere secondi reali ad ogni corsa della suite.</summary>
    internal static TimeSpan DebounceRefreshWatcher = TimeSpan.FromSeconds(3);

    /// <summary>Budget massimo che PreparaPaginaAsync attende per UNA SINGOLA pagina prima di rinunciare
    /// (revisione indipendente, piano 6.4.0: "non esiste un timeout... se NavItem.Crea() si blocca, l'avvio non
    /// mostra mai la MainWindow"). NavItem.Crea() gira su un thread di pool e non su un thread annullabile: un
    /// I/O bloccato su una share degradata o un deadlock non possono essere interrotti da qui, quindi questo
    /// budget non "ferma" il lavoro — smette solo di ASPETTARLO, lasciando l'avvio proseguire. `internal` e non
    /// `const`/`readonly`: i test possono accorciarlo per verificare il comportamento di scadenza senza dover
    /// attendere secondi reali.</summary>
    internal static TimeSpan TimeoutScaldamentoPagina = TimeSpan.FromSeconds(15);

    /// <summary>Seam di SOLI test (piano 6.4.0, revisione timeout): quando <see cref="SostituzioneCreaPaginaPerTestNavId"/>
    /// coincide con il navId richiesto, PreparaPaginaAsync usa questo delegato AL POSTO di NavItem.Crea() —
    /// serve a simulare un Crea() che non termina mai (o che termina tardi) senza dover produrre un vero
    /// deadlock/I-O bloccato nella suite. Entrambi i campi restano null in produzione: il comportamento reale
    /// (sempre NavItem.Crea()) non cambia.</summary>
    internal static Func<ViewModelBase>? SostituzioneCreaPaginaPerTest;
    internal static string? SostituzioneCreaPaginaPerTestNavId;

    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty] private ViewModelBase? currentPage;
    [ObservableProperty] private bool caricamentoPagina;
    [ObservableProperty] private string? erroreNavigazione;
    public bool MostraErroreNavigazione => !string.IsNullOrWhiteSpace(ErroreNavigazione);
    partial void OnErroreNavigazioneChanged(string? value) => OnPropertyChanged(nameof(MostraErroreNavigazione));

    // P4 1: errore UI non gestito. L'app resta in funzione; l'avviso indica il crash.log (al massimo uno per intervallo).
    private Action<string>? _erroreUiSegnalato;
    [ObservableProperty] private string? percorsoLogErroreUi;
    public bool MostraErroreUi => !string.IsNullOrWhiteSpace(PercorsoLogErroreUi);
    partial void OnPercorsoLogErroreUiChanged(string? value) => OnPropertyChanged(nameof(MostraErroreUi));

    /// <summary>Chiude l'avviso di errore UI: l'app non si ferma e il crash.log resta dov'è.</summary>
    [RelayCommand]
    private void ChiudiErroreUi() => PercorsoLogErroreUi = null;

    /// <summary>Le pagine conservate in cache restano vive tra una navigazione e l'altra; quelle temporanee
    /// (editor e pagine non memorizzate) vengono invece disposte quando si abbandonano.</summary>
    private void ImpostaCurrentPage(ViewModelBase? nuova)
    {
        var precedente = CurrentPage;
        RegistraPosizionePrecedente(precedente, nuova);
        if (precedente is IDisposable disposabile && !PaginaInCache(precedente)) disposabile.Dispose();
        CurrentPage = nuova;
        if (nuova != null) DiagnosticTraceService.Registra("Navigazione", nuova.GetType().Name);
    }

    private bool PaginaInCache(ViewModelBase pagina) => _pagineCache.Values.Any(candidata => ReferenceEquals(candidata, pagina));

    /// <summary>Rilascia le pagine cache e quella corrente, evitando doppi Dispose quando la pagina corrente
    /// è anche una delle istanze cache.</summary>
    private void DisposePagineCache()
    {
        var visitate = new HashSet<ViewModelBase>(ReferenceEqualityComparer.Instance);
        var pagine = CurrentPage is { } corrente ? _pagineCache.Values.Append(corrente) : _pagineCache.Values;
        foreach (var pagina in pagine)
            if (visitate.Add(pagina) && pagina is IDisposable disposabile) disposabile.Dispose();
        _pagineCache.Clear();
    }
    [ObservableProperty] private NavItem? selectedNavItem;
    [ObservableProperty] private bool mostraPrimoAvvio;
    [ObservableProperty] private bool mostraAiuto;
    [ObservableProperty] private bool mostraFileConProblemi;
    [ObservableProperty] private int numeroFileConProblemi;
    [ObservableProperty] private bool mostraDuplicatiId;
    [ObservableProperty] private int numeroDuplicatiId;
    [ObservableProperty] private string operatoreAttivo = "";
    [ObservableProperty] private string ruoloAttivo = "";
    [ObservableProperty] private bool mostraStatoCondivisione;
    [ObservableProperty] private string testoStatoCondivisione = "";
    [ObservableProperty] private bool mostraAvvisoCartellaIncerta;
    [ObservableProperty] private string testoAvvisoCartellaIncerta = "";
    [ObservableProperty] private bool mostraNotifiche;
    [ObservableProperty] private int notificheNonLette;
    [ObservableProperty] private bool mostraOnboarding;
    [ObservableProperty] private bool mostraRicercaGlobale;
    [ObservableProperty] private int indiceOnboarding;
    [ObservableProperty] private RolePreviewOption? anteprimaRuoloOnboarding;
    public ObservableCollection<NotificaApp> NotificheRecenti { get; } = new();
    public IReadOnlyList<OnboardingStep> PassiOnboarding { get; private set; } = Array.Empty<OnboardingStep>();
    public OnboardingStep? PassoOnboardingCorrente => PassiOnboarding.ElementAtOrDefault(IndiceOnboarding);
    public string ProgressoOnboarding => PassiOnboarding.Count == 0 ? "" : $"{IndiceOnboarding + 1} / {PassiOnboarding.Count}";
    public double ValoreProgressoOnboarding => PassiOnboarding.Count == 0 ? 0 : (IndiceOnboarding + 1d) / PassiOnboarding.Count * 100d;
    public bool PuoTornareOnboarding => IndiceOnboarding > 0;
    public bool EUltimoPassoOnboarding => IndiceOnboarding == PassiOnboarding.Count - 1;
    public bool PuoAnteprimaRuolo => _state.EMaster;
    public IReadOnlyList<RolePreviewOption> RuoliAnteprimaOnboarding =>
    [
        new(RuoloOperatore.Master, "Master"),
        new(RuoloOperatore.Standard, LocalizationService.Instance.Scegli("Operatore", "Operator", "Opérateur")),
        new(RuoloOperatore.Viewer, "Viewer")
    ];
    public string RuoloOnboardingLabel => AnteprimaRuoloOnboarding?.Etichetta ?? RuoloAttivo;
    public string TestoMenuAccount => $"👤 {OperatoreAttivo}  ·  🔔 {NotificheNonLette}";
    public string FocusOnboarding => string.Format(LocalizationService.Instance.Traduci("OnboardingFocusFmt"),
        LocalizationService.Instance.Traduci(PassoOnboardingCorrente?.NavId switch
        {
            "test" => "NavTest", "anomalie" => "NavAnomalie", "report" => "NavReport",
            "audit" => "NavAudit", "impostazioni" => "NavImpostazioni", _ => "NavHome"
        }));

    public string TestoFileConProblemi => NumeroFileConProblemi == 1
        ? LocalizationService.Instance.Traduci("FileNonLeggibileSingolare")
        : string.Format(LocalizationService.Instance.Traduci("FileNonLeggibiliPluraleFmt"), NumeroFileConProblemi);
    public string TestoDuplicatiId => NumeroDuplicatiId == 1
        ? LocalizationService.Instance.Traduci("IdDuplicatoSingolare")
        : string.Format(LocalizationService.Instance.Traduci("IdDuplicatiPluraleFmt"), NumeroDuplicatiId);

    public FirstRunViewModel FirstRun { get; }
    public HelpViewModel Help { get; }
    public GlobalSearchViewModel RicercaGlobale { get; }

    public MainViewModel() : this(new AppState()) { }

    public MainViewModel(AppState state)
    {
        _state = state;
        TableLayoutPreferences.Configure(_state);
        ViewStateService.Configura(_state);
        Help = new HelpViewModel(() => _state.RuoloAttivo);
        RicercaGlobale = new GlobalSearchViewModel(_state, ApriRisultatoRicerca);
        FirstRun = new FirstRunViewModel(_state, OnPrimoAvvioCompletato);

        var navDefault = new List<NavItem>
        {
            new() { Id = "home", Titolo = "Home", Icona = "🏠", Crea = () => new HomeViewModel(_state, NavigaATestCase, NavigaATestSenzaEcu, ApriElementoRecente, VaiAAnomalie, aggiornaInBackground: true) },
            new() { Id = "test", Titolo = "Test", Icona = "🧪", Crea = () => new TestCaseListViewModel(_state, NavigaATestCase) },
            new() { Id = "anomalie", Titolo = "Anomalie / CR", Icona = "⚠️", Crea = () => new NonConformitaViewModel(_state, NavigaATestCase, NavigaATestCaseInModifica, NavigaAChangeRequest, NavigaASegnalazioneRete)
                { ApriChangeRequestSolaLettura = NavigaAChangeRequestSolaLettura, ApriSegnalazioneReteSolaLettura = NavigaASegnalazioneReteSolaLettura } },
            new() { Id = "guasto", Titolo = "Guasto", Icona = "🔧", Crea = () => new DtcRegistroViewModel(_state, NavigaATestCase) },
            new() { Id = "report", Titolo = "Report", Icona = "📊", Crea = () => new ReportViewModel(_state, aggiornaInBackground: true) },
            new() { Id = "audit", Titolo = "Audit", Icona = "🛡️", Crea = () => new AuditViewModel(_state, NavigaATestCase) },
            new() { Id = "impostazioni", Titolo = "Impostazioni", Icona = "🔩", Crea = () => new ImpostazioniViewModel(_state, AggiornaStatoBarra, NavItems) },
        };
        IconeSezioniService.Applica(navDefault, IconeSezioniService.AmbitoPrincipale, _state.Config);
        NavItems = new ObservableCollection<NavItem>(NavOrderService.Ordina(navDefault,_state.Config.OrdineNavPrincipale));

        AggiornaStatoBarra();

        _linguaCambiata = (_, e) =>
        {
            if (e.PropertyName is not (nameof(LocalizationService.EInglese) or nameof(LocalizationService.Lingua))) return;
            OnPropertyChanged(nameof(TestoFileConProblemi));
            OnPropertyChanged(nameof(TestoDuplicatiId));
            OnPropertyChanged(nameof(TestoAggiornamento));
            AggiornaStatoBarra();
            if (MostraOnboarding)
            {
                var ruoloTour = AnteprimaRuoloOnboarding?.Ruolo ?? _state.RuoloAttivo;
                AnteprimaRuoloOnboarding = RuoliAnteprimaOnboarding.First(x => x.Ruolo == ruoloTour);
                PassiOnboarding = OnboardingService.CreaPassi(ruoloTour, LocalizationService.Instance.Lingua);
                NotificaPassoOnboarding();
            }
        };
        LocalizationService.Instance.PropertyChanged += _linguaCambiata;

        // Config.OperatoreAttivo è una preferenza locale e può restare stale quando un Master rinomina,
        // disattiva o rimuove l'account condiviso. In tal caso AppState non risolve OperatoreCorrente:
        // RuoloAttivo diventa Viewer e tutte le scritture restano disabilitate. Aprire comunque la shell
        // lasciava l'utente senza un modo chiaro per autenticarsi con un account valido.
        if (!_state.CartellaValida || _state.OperatoreCorrente is null)
        {
            MostraPrimoAvvio = true;
            if (_state.CartellaValida && !string.IsNullOrWhiteSpace(_state.Config.OperatoreAttivo))
            {
                FirstRun.MessaggioErrore = LocalizationService.Instance.Traduci("OperatoreMemorizzatoNonDisponibile");
                DiagnosticTraceService.Registra("Operatore.SessioneNonValida",
                    "L'account memorizzato localmente non corrisponde a un operatore attivo nella cartella condivisa.");
            }
        }
        else
        {
            SelectedNavItem = NavItems[0];
            AvviaOnboardingSeNecessario();
            MostraNotificheNonLetteAllAvvioSeNecessario();
        }

        _datiRicaricati = AggiornaStatoBarra;
        _state.DatiRicaricati += _datiRicaricati;
        _controlloSessioneOperatore = true;
        _erroreUiSegnalato = percorso => PercorsoLogErroreUi = percorso;
        CrashLogService.ErroreUiSegnalato += _erroreUiSegnalato;

        // Rete di sicurezza (C4, diagnosi_sync/diagnosi.md): resta l'unico meccanismo per i casi in cui il
        // watcher non può aiutare (evento perso, cartella smontata al momento dell'evento, watcher stesso in
        // errore su una generation che richiede full reconciliation ma senza nuovi eventi che la innneschino).
        // Allungato da 1 a 5 minuti perché ora NON è più l'unico modo per accorgersi di una modifica: il
        // refresh guidato dall'evento sotto copre il caso comune con una latenza di pochi secondi invece che
        // fino a 60s, quindi un giro periodico più fitto sarebbe solo lavoro ridondante sullo stesso workspace.
        _refreshTimer = new System.Timers.Timer(TimeSpan.FromMinutes(5)) { AutoReset = true };
        _refreshTimer.Elapsed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = AggiornaDatiInBackground());
        _refreshTimer.Start();

        // 6.7.1: sonda periodica della radice condivisa. Il banner "cartella non disponibile" non dipende più solo dal giro di 5
        // minuti: una cartella smontata (o riattaccata) viene vista entro pochi secondi. Ogni sonda gira fuori dal thread UI.
        _sondaRadiceTimer = new System.Timers.Timer(MonitorRaggiungibilitaRadice.IntervalloSonda) { AutoReset = true };
        _sondaRadiceTimer.Elapsed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = SondaRadiceInBackground());
        _sondaRadiceTimer.Start();

        // Refresh guidato dagli eventi del watcher (C4, diagnosi_sync/diagnosi.md): prima di questa aggiunta
        // TestCaseChangeTracker/AppState accumulavano soltanto i path "sporchi", consumati al giro successivo
        // del timer sopra — un ritardo fino a 60s anche nel caso più semplice. Il debounce (AutoReset=false,
        // riavviato ad ogni nuovo evento) raccoglie una raffica di eventi ravvicinati — una sincronizzazione
        // OneDrive che consegna in sequenza i 3+ file di un singolo salvataggio, o molti salvataggi di fila —
        // in un solo refresh quando la raffica si placa, invece di un refresh per ogni singolo evento.
        _debounceRefreshWatcher = new System.Timers.Timer(DebounceRefreshWatcher) { AutoReset = false };
        _debounceRefreshWatcher.Elapsed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = AggiornaDatiInBackground());
        _cambiamentiSuDiscoRilevati = () =>
        {
            // Arriva SEMPRE dal thread del FileSystemWatcher, mai dal thread UI (vedi TestCaseChangeTracker/
            // AppState.CambiamentiSuDiscoRilevati): Start()/Stop() su System.Timers.Timer sono thread-safe e
            // possono essere chiamati da più thread watcher concorrenti (uno per cartella sorvegliata) senza
            // sincronizzazione aggiuntiva — nel peggior caso una race qui sposta di poco il momento in cui il
            // debounce scatta, mai la sua correttezza.
            try { _debounceRefreshWatcher.Stop(); _debounceRefreshWatcher.Start(); }
            catch (ObjectDisposedException) { /* Dispose() corso in mezzo: nessun refresh da accodare. */ }
        };
        _state.CambiamentiSuDiscoRilevati += _cambiamentiSuDiscoRilevati;

        // Read-only TICKET polling is independent of TestLoom domain refresh and never blocks startup.
        _polarionBackgroundSync = new PolarionBackgroundSyncService(_state);
        _polarionBackgroundSync.Start();
        // Browser handoff auto-linking requires only GET; no automatic Ticketing POST.
        _polarionBrowserLinkMonitor = new PolarionBrowserLinkMonitor(_state);
        _polarionBrowserLinkMonitor.Start();
    }

    [ObservableProperty] private bool aggiornamentoInCorso;
    [ObservableProperty] private DateTime? ultimoAggiornamento;
    [ObservableProperty] private bool aggiornamentoSospeso;

    public string TestoAggiornamento => AggiornamentoInCorso
        ? LocalizationService.Instance.Traduci("AggiornamentoInCorso")
        : AggiornamentoSospeso
            ? LocalizationService.Instance.Traduci("AggiornamentoSospesoModifica")
            : UltimoAggiornamento.HasValue
                ? string.Format(LocalizationService.Instance.Traduci("AggiornatoAlleFmt"), UltimoAggiornamento.Value)
                : LocalizationService.Instance.Traduci("AggiornaOraBtn");

    partial void OnAggiornamentoInCorsoChanged(bool value) => OnPropertyChanged(nameof(TestoAggiornamento));
    partial void OnAggiornamentoSospesoChanged(bool value) => OnPropertyChanged(nameof(TestoAggiornamento));
    partial void OnUltimoAggiornamentoChanged(DateTime? value) => OnPropertyChanged(nameof(TestoAggiornamento));

    /// <summary>Sostituisce il vecchio ModificaInCorso "tutto o niente" (C5, diagnosi_sync/diagnosi.md): prima
    /// di questa correzione un QUALSIASI editor con modifiche non salvate faceva SALTARE l'intero giro di
    /// aggiornamento — non solo il record in modifica, anche tutti gli altri Test/NC/CR/Segnalazioni restavano
    /// bloccati per un tempo illimitato (finché l'editor non veniva chiuso/salvato). VINCOLO ASSOLUTO
    /// rispettato comunque a monte: ogni editor di questo repository lavora sempre su un CLONE del proprio
    /// record, mai su un riferimento condiviso alla cache (TestCaseEditorViewModel.TestCase = Clona(...),
    /// ChangeRequestEditorViewModel.ChangeRequest = Clona(...), MasterDataEditorViewModelBase&lt;T&gt;.Clona(Items)),
    /// quindi il refresh sotto non può MAI sovrascrivere o perdere quel lavoro, a prescindere da quanto
    /// venga effettivamente escluso qui. Le esclusioni sono comunque calcolate, per prudenza in più e per UX:
    /// il record sotto modifica non deve ricomparire/sparire da "file con problemi" o cambiare stato proprio
    /// mentre l'utente lo guarda. Gli editor su dati master (Impostazioni/IEditorConModifiche) non richiedono
    /// nessuna esclusione: RileggiDaDisco non tocca mai le loro entità (Operatori/Veicoli/CampiSistema sono
    /// letti on-demand da Master.*.Load(), non tenuti in una cache che un refresh possa rimpiazzare).</summary>
    private AppState.RefreshEsclusioni CalcolaEsclusioniRefresh()
    {
        if (CurrentPage is TestCaseEditorViewModel { InModifica: true } editorTest)
        {
            string? percorso = null;
            try { percorso = _state.TestCases?.PercorsoPer(editorTest.TestCase); }
            catch (Exception ex) { CrashLogService.Registra("Calcolo percorso protetto (editor Test in modifica)", ex); }
            return new AppState.RefreshEsclusioni { PercorsoTestCaseProtetto = percorso };
        }
        if (CurrentPage is ChangeRequestEditorViewModel { ModificheNonSalvate: true })
            return new AppState.RefreshEsclusioni { EscludiChangeRequest = true };
        return AppState.RefreshEsclusioni.Nessuna;
    }

    /// <summary>Attesa tra due tentativi di riapertura della cartella condivisa: parte da 5 minuti, raddoppia fino a 30.</summary>
    private TimeSpan _ritardoRiapertura = TimeSpan.FromMinutes(5);
    private DateTime _prossimoTentativoRiapertura = DateTime.MinValue;

    private async Task AggiornaDatiInBackground()
    {
        if (AggiornamentoInCorso) return;
        if (!_state.CartellaValida)
        {
            // 6.6.1 (e): cartella condivisa non raggiungibile all'avvio. Il giro periodico riprova l'apertura senza riavviare
            // l'app. RiprovaInizializzazione esce subito se la cartella non risponde, quindi il costo di ogni giro è minimo.
            // 6.6.1 (decisione B): la riapertura automatica vale solo per la cartella di rete; per OneDrive resta il comportamento 6.6.0.
            // Non attende la rete sul thread UI (P4): una unità ancora in risoluzione rinvia la riapertura al giro successivo.
            if (!ModalitaCondivisioneService.ERetePerInterfaccia(_state.Config.CartellaCondivisa)) return;
            // 6.6.1 (correzione E): la riapertura non gira durante la configurazione di primo avvio; se la cartella resta
            // irraggiungibile o non si inizializza, si ritenta con attesa crescente (5, 10, 20, poi al massimo 30 minuti).
            if (MostraPrimoAvvio) return;
            if (_state.InizializzazioneWorkspaceCompletata || string.IsNullOrWhiteSpace(_state.Config.CartellaCondivisa)) return;
            if (DateTime.Now < _prossimoTentativoRiapertura) return;

            AggiornamentoInCorso = true;
            var aperta = false;
            try
            {
                aperta = await Task.Run(() => _state.RiprovaInizializzazione());
                if (aperta)
                {
                    AggiornaStatoBarra();
                    _state.NotificaDatiRicaricati();
                }
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Riprova apertura cartella condivisa", ex);
            }
            finally
            {
                AggiornamentoInCorso = false;
            }
            _ritardoRiapertura = aperta
                ? TimeSpan.FromMinutes(5)
                : TimeSpan.FromTicks(Math.Min((_ritardoRiapertura * 2).Ticks, TimeSpan.FromMinutes(30).Ticks));
            _prossimoTentativoRiapertura = DateTime.Now + _ritardoRiapertura;
            return;
        }

        var esclusioni = CalcolaEsclusioniRefresh();
        AggiornamentoSospeso = false;
        AggiornamentoInCorso = true;
        try
        {
            await Task.Run(() => _state.RileggiDaDisco(esclusioni));
            _state.NotificaDatiRicaricati();
            UltimoAggiornamento = DateTime.Now;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Aggiornamento periodico dati", ex);
        }
        finally
        {
            AggiornamentoInCorso = false;
        }
    }

    [RelayCommand]
    private async Task AggiornaOra()
    {
        // 6.7.1: non esce più su CartellaValida (cache). "Aggiorna ora" ricontrolla SEMPRE la radice, poi rilegge solo se risponde.
        if (AggiornamentoInCorso || !_state.InizializzazioneWorkspaceCompletata) return;
        var esclusioni = CalcolaEsclusioniRefresh();
        AggiornamentoInCorso = true;
        AggiornamentoSospeso = false;
        try
        {
            var passo = await Task.Run(() => _state.SondaRadiceCondivisa(_monitorRadice, forza: true));
            if (passo.Utilizzabile)
            {
                await Task.Run(() => _state.RileggiDaDisco(esclusioni));
                _state.NotificaDatiRicaricati();
                UltimoAggiornamento = DateTime.Now;
            }
            AggiornaBannerCondivisione();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Aggiornamento manuale dati", ex);
        }
        finally
        {
            AggiornamentoInCorso = false;
        }
    }

    private bool _suppressAutoNav;
    private NavItem? _navCorrente;
    /// <summary>Navigazione avviata dall'ultimo cambio di SelectedNavItem (o dalla conferma di abbandono che lo precede). In UI
    /// reale completa quando CurrentPage e' gia' la pagina richiesta: chi agisce sulla pagina appena aperta la attende.</summary>
    private Task _navigazioneInCorso = Task.CompletedTask;
    private readonly Dictionary<string, ViewModelBase> _pagineCache = new();

    private ViewModelBase OttieniPagina(NavItem item) => OttieniPagina(item, out _);

    /// <summary>primaApertura=true quando questa chiamata ha costruito la pagina da zero (Crea(), il ramo
    /// potenzialmente lento e sincrono): false quando ha solo richiamato Aggiorna() su una pagina in cache.
    /// Distinguere le due misure e' esplicitamente richiesto dal piano ("misura separatamente prima apertura e
    /// rivisita, non dichiarare raggiunto un target senza misura reale").</summary>
    private ViewModelBase OttieniPagina(NavItem item, out bool primaApertura)
    {
        if (item.Id == "impostazioni") { primaApertura = true; return item.Crea(); }
        if (_pagineCache.TryGetValue(item.Id, out var esistente))
        {
            if (esistente is IPaginaAggiornabile aggiornabile) aggiornabile.Aggiorna();
            primaApertura = false;
            return esistente;
        }
        var nuova = item.Crea();
        _pagineCache[item.Id] = nuova;
        primaApertura = true;
        return nuova;
    }

    /// <summary>
    /// Scaldamento all'avvio (piano 6.4.0, richiesta utente: "le pagine devono stare sotto 700ms anche alla
    /// PRIMA visita"): costruisce e METTE IN CACHE la pagina indicata usando esattamente lo stesso percorso
    /// della prima navigazione reale (OttieniPagina/NavItem.Crea, mai una copia della logica), chiamato da
    /// App.axaml.cs dopo aver creato questo MainViewModel e PRIMA di mostrare la MainWindow. Nessuna cache/
    /// invalidazione duplicata: la vera prima visita dell'utente troverà comunque una pagina già in
    /// _pagineCache e passerà dal ramo "cache hit" di OttieniPagina, che chiama IPaginaAggiornabile.Aggiorna()
    /// come farebbe oggi una qualunque rivisita — quindi una modifica ai dati arrivata fra lo scaldamento e la
    /// visita (salvataggio locale, o da un'altra postazione tramite watcher/refresh) resta visibile esattamente
    /// come oggi, perché la firma d'invalidazione di ciascuna pagina (vedi WuStatusViewModel.Ricarica,
    /// ReportViewModel.Aggiorna) viene comunque riverificata a quel momento.
    ///
    /// Thread: NavItem.Crea() — il lavoro pesante vero e proprio, lo stesso che una prima navigazione farebbe
    /// in modo sincrono sul thread UI (fino a ~3s su WU Status a 100k voci) — gira su un thread di pool
    /// (Task.Run) e NON sul thread UI, così l'AvvioWindow resta reattiva mentre scalda le pagine. Questo è
    /// sicuro perché: (1) il ViewModel appena costruito non è ancora agganciato a nessuna View/binding — nessun
    /// listener osserva le sue ObservableCollection finché non viene assegnato a CurrentPage, quindi popolarle
    /// da un thread diverso da quello UI non solleva alcuna race con un renderer; (2) i repository letti dentro
    /// Crea() sono già usati da un thread di background durante il bootstrap stesso (App.axaml.cs incapsula la
    /// costruzione di AppState in un Task.Run) e sono protetti da lock espliciti (TestCaseRepository.GetAll,
    /// MasterDataRepository.Load: `lock (_gate)`); (3) MasterLetturaScope (usato da ReportViewModel.Aggiorna)
    /// è [ThreadStatic] — la sua unica regola è che Apri()/Dispose() eseguano sullo STESSO thread OS senza
    /// alcun await in mezzo, condizione già garantita perché Crea() gira per intero, in modo sincrono, dentro
    /// il delegato di questo singolo Task.Run. ConfigureAwait(false): l'inserimento in _pagineCache può quindi
    /// avvenire sul thread di pool stesso invece di dover rimbalzare sul thread UI — comunque sicuro perché
    /// nessun altro thread legge/scrive _pagineCache in questa finestra (nessuna navigazione reale è ancora
    /// possibile, la MainWindow non è stata creata).
    ///
    /// Se lo scaldamento di questa pagina fallisce per qualunque motivo, l'eccezione viene loggata e MAI
    /// propagata: non deve poter impedire l'avvio (piano 6.4.0, punto 4).
    ///
    /// Timeout (revisione indipendente, piano 6.4.0): senza un budget, un Crea() bloccato (deadlock, I/O
    /// bloccato su una share degradata) farebbe restare `await lavoroTask` in App.axaml.cs.RunStartupAsync in
    /// attesa PER SEMPRE — la MainWindow non comparirebbe mai. <see cref="TimeoutScaldamentoPagina"/> limita
    /// quanto questo metodo ASPETTA quella singola pagina: scaduto il budget, l'avvio prosegue e l'evento va nel
    /// log diagnostico — il Task di background continua comunque a girare (non esiste un modo per "uccidere" un
    /// thread di pool bloccato in .NET), quindi va gestito separatamente quando/se finirà davvero: vedi
    /// <see cref="GestisciCompletamentoTardivo"/>. Il bootstrap di AppState (App.axaml.cs.PreparaAvvioAsync,
    /// `Task.Run(() => new AppState(...))`) NON passa da questo metodo e non è quindi mai coperto da questo
    /// timeout: solo lo scaldamento delle pagine lo è.
    /// </summary>
    public async Task<ViewModelBase?> PreparaPaginaAsync(string navId)
    {
        var item = NavItems.FirstOrDefault(n => n.Id == navId);
        if (item == null) return null;
        if (_pagineCache.TryGetValue(item.Id, out var esistente)) return esistente;
        var generazioneCache = Interlocked.Read(ref _generazioneCachePagine);

        // Seam di test: vedi SostituzioneCreaPaginaPerTest. In produzione entrambi i campi sono null e
        // creaFn è sempre item.Crea, invariato rispetto a prima di questa revisione.
        var creaFn = SostituzioneCreaPaginaPerTest != null && SostituzioneCreaPaginaPerTestNavId == item.Id
            ? SostituzioneCreaPaginaPerTest
            : item.Crea;

        Task<ViewModelBase> lavoro;
        try
        {
            lavoro = Task.Run(creaFn);
        }
        catch (Exception ex)
        {
            // Task.Run stesso non dovrebbe mai lanciare in modo sincrono, ma un fallimento qui non deve avere
            // un trattamento diverso da un fallimento del lavoro stesso (vedi catch sotto).
            CrashLogService.Registra($"Scaldamento pagina '{navId}' (avvio)", ex);
            return null;
        }

        var timeout = Task.Delay(TimeoutScaldamentoPagina);
        var primoCompletato = await Task.WhenAny(lavoro, timeout).ConfigureAwait(false);

        if (primoCompletato == timeout && !lavoro.IsCompleted)
        {
            CrashLogService.Registra($"Scaldamento pagina '{navId}' (avvio)",
                new TimeoutException($"Superato il budget di {TimeoutScaldamentoPagina.TotalSeconds:F0}s in attesa di questa pagina: l'avvio prosegue senza attenderla."));
            // Fire-and-forget deliberato: nessuno aspetta più questo lavoro, ma se/quando finirà il suo
            // risultato va comunque gestito (mai perso in un UnobservedTaskException, mai inserito a sproposito
            // in cache). Vedi GestisciCompletamentoTardivo.
            _ = GestisciCompletamentoTardivo(navId, lavoro, generazioneCache);
            return null;
        }

        try
        {
            var vm = await lavoro.ConfigureAwait(false);
            await InserisciInCacheSeAssenteAsync(navId, vm, generazioneCache).ConfigureAwait(false);
            return _pagineCache.TryGetValue(navId, out var finale) ? finale : null;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra($"Scaldamento pagina '{navId}' (avvio)", ex);
            return null;
        }
    }

    /// <summary>Osserva l'esito di un Task di scaldamento abbandonato dopo un timeout (vedi PreparaPaginaAsync):
    /// evita un UnobservedTaskException silenzioso se il lavoro finirà per fallire, e — se finirà con successo —
    /// applica lo stesso "inserisci solo se assente" di un completamento in tempo. A questo punto la MainWindow
    /// potrebbe già essere visibile e l'utente potrebbe aver già navigato su questa stessa pagina (creando una
    /// SUA istanza tramite OttieniPagina): il controllo "solo se la chiave è ancora assente" dentro
    /// InserisciInCacheSeAssenteAsync, eseguito sul thread UI, è l'unica cosa che impedisce due istanze vive
    /// dello stesso ViewModel — quella tardiva viene scartata (Dispose se IDisposable) invece di sovrascrivere
    /// quella che l'utente sta già guardando.</summary>
    private async Task GestisciCompletamentoTardivo(string navId, Task<ViewModelBase> lavoro, long generazioneCache)
    {
        ViewModelBase vm;
        try
        {
            vm = await lavoro.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra($"Scaldamento pagina '{navId}' (completato in ritardo dopo il timeout)", ex);
            return;
        }
        await InserisciInCacheSeAssenteAsync(navId, vm, generazioneCache).ConfigureAwait(false);
    }

    /// <summary>Unico punto di scrittura in _pagineCache per un risultato di scaldamento, sia arrivato in tempo
    /// sia tardivo dopo un timeout (vedi PreparaPaginaAsync/GestisciCompletamentoTardivo). Applicato SEMPRE sul
    /// thread UI quando esiste una vera Application (produzione): OttieniPagina — l'unico altro scrittore di
    /// _pagineCache, durante una navigazione reale — gira SEMPRE sul thread UI (le proprietà bindate lo
    /// impongono), quindi far passare anche questo inserimento dal thread UI è l'unico modo per evitare la race
    /// fra "il lavoro di scaldamento finisce" (può essere un thread di pool, per un completamento tardivo dopo
    /// il timeout) e "l'utente naviga", senza introdurre un lock su un Dictionary altrimenti sempre accesso da
    /// un solo thread.
    ///
    /// Nei test headless (Application.Current nullo) si applica in modo SINCRONO sul thread chiamante, stesso
    /// principio già usato da MostraPaginaDopoFrame per la stessa ragione: qui i test guidano la navigazione
    /// reale (OttieniPagina) direttamente sul thread di test, quindi "sincrono sul thread chiamante" è
    /// esattamente ciò che la mantiene serializzata rispetto a quella — instradare invece verso il pump
    /// Dispatcher dedicato ai test (DispatcherTestPump, un thread ancora diverso) romperebbe quella
    /// serializzazione invece di preservarla. La sicurezza in questo ramo si ottiene quindi con la sequenza
    /// temporale scelta dal test (vedi i test su un "risultato tardivo": il completamento del lavoro
    /// abbandonato viene rilasciato DOPO la navigazione reale, non in vera concorrenza).</summary>
    private Task InserisciInCacheSeAssenteAsync(string navId, ViewModelBase vm, long generazioneCache)
    {
        void Applica()
        {
            if (_disposed || generazioneCache != Interlocked.Read(ref _generazioneCachePagine) || _pagineCache.ContainsKey(navId))
            {
                if (vm is IDisposable disposabile) disposabile.Dispose();
                return;
            }
            _pagineCache[navId] = vm;
        }

        if (Application.Current != null) return Dispatcher.UIThread.InvokeAsync(Applica).GetTask();
        Applica();
        return Task.CompletedTask;
    }

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (_suppressAutoNav) { _navCorrente = value; return; }
        if (value == null) return;

        if (HaModificheNonSalvate())
        {
            var destinazione = value;
            var precedente = _navCorrente;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ImpostaNavSenzaRicaricare(precedente));
            _navigazioneInCorso = NavigaDopoConferma(destinazione);
            return;
        }

        _navCorrente = value;
        Help.Contesto = value.Id;
        _navigazioneInCorso = MostraPaginaDopoFrame(value);
    }

    private async Task NavigaDopoConferma(NavItem destinazione)
    {
        if (!await ConfermaAbbandonoPaginaCorrenteSeNecessario()) return;
        ImpostaNavSenzaRicaricare(destinazione);
        await MostraPaginaDopoFrame(destinazione);
    }

    /// <summary>
    /// Costruttori/Aggiorna dei ViewModel toccano Avalonia e restano sul dispatcher. Prima di eseguirli
    /// pubblichiamo il placeholder e cediamo un frame: altrimenti IsLoading=true seguito dal lavoro
    /// sincrono non viene mai dipinto (feedback utente: il cambio pagina sembra bloccare la finestra).
    /// Una richiesta successiva invalida quella ancora in attesa, evitando A→B→A applicato fuori ordine.
    /// Nei test headless manteniamo l'esecuzione sincrona storica per verificare la semantica di navigazione.
    /// </summary>
    private async Task MostraPaginaDopoFrame(NavItem pagina)
    {
        var richiesta = ++_richiestaNavigazione;
        CaricamentoPagina = true;
        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ErroreNavigazione = null;
            if (Application.Current != null)
            {
                await Task.Delay(16);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            if (_disposed || richiesta != _richiestaNavigazione) return;
            var primaCostruzione = cronometro.Elapsed;
            var vm = OttieniPagina(pagina, out var primaApertura);
            ImpostaCurrentPage(vm);
            // "navigazione.pagina" arriva fino alla rimozione logica del placeholder (il rendering e'
            // separato); "navigazione.costruzione" isola la sola OttieniPagina/Aggiorna
            // sincrona (costruttore ViewModel o Aggiorna(), la parte che puo' davvero bloccare la UI), con
            // Detail che distingue prima-apertura (Crea()) da rivisita (Aggiorna() su pagina in cache).
            PerformanceDiagnosticsService.Record(_state.TestCases?.LocalWorkspaceDirectory, "navigazione.costruzione",
                cronometro.Elapsed - primaCostruzione, 0, $"{pagina.Id}:{(primaApertura ? "prima-apertura" : "rivisita")}");
        }
        catch (Exception ex)
        {
            CrashLogService.Registra($"Navigazione pagina {pagina.Id}", ex);
            if (richiesta == _richiestaNavigazione) ErroreNavigazione = MessaggioErroreNavigazione(pagina.Titolo);
        }
        finally
        {
            if (richiesta == _richiestaNavigazione) CaricamentoPagina = false;
            PerformanceDiagnosticsService.Record(_state.TestCases?.LocalWorkspaceDirectory, "navigazione.pagina",
                cronometro.Elapsed, 0, pagina.Id);
        }
    }

    /// <summary>Testo del banner quando una pagina non si apre: localizzato e senza il messaggio grezzo dell'eccezione (che
    /// per un NullReferenceException e' in inglese e non dice nulla all'operatore). Il dettaglio tecnico resta nel crash log.</summary>
    internal static string MessaggioErroreNavigazione(string titoloPagina) =>
        string.Format(LocalizationService.Instance.Traduci("NavigazionePaginaErroreFmt"), titoloPagina);

    private async Task<bool> ConfermaAbbandonoPaginaCorrenteSeNecessario()
    {
        if (CurrentPage is SegnalazioneReteEditorViewModel segnalazione)
            await segnalazione.AttendiCaricamentoAllegatoAsync();
        if (!HaModificheNonSalvate()) return true;
        return await ConfirmService.ConfermaAbbandonoModifiche(CurrentPage);
    }

    private void ImpostaNavSenzaRicaricare(NavItem? item)
    {
        _suppressAutoNav = true;
        SelectedNavItem = item;
        _suppressAutoNav = false;
        _navCorrente = item;
        if (item != null) Help.Contesto = item.Id;
    }

    private void NavigaATestCase(string? testCaseId)
    {
        if (HaModificheNonSalvate()) { _ = NavigaATestCaseDopoConferma(testCaseId); return; }
        NavigaATestCaseCore(testCaseId);
    }

    /// <summary>Piano 6.4.0 punto 5(3): dall'avviso "Test senza componente" della Home apre direttamente la pagina
    /// Test con il filtro dedicato già attivo, invece di lasciare l'operatore a impostarlo a mano. Stessa
    /// pagina in cache di una normale visita a "Test" (Id "test"): se già aperta viene solo rifiltrata.</summary>
    private void NavigaATestSenzaEcu()
    {
        if (HaModificheNonSalvate()) { _ = NavigaATestSenzaEcuDopoConferma(); return; }
        NavigaATestSenzaEcuCore();
    }

    private async Task NavigaATestSenzaEcuDopoConferma()
    {
        if (!await ConfermaAbbandonoPaginaCorrenteSeNecessario()) return;
        NavigaATestSenzaEcuCore();
    }

    /// <summary>Apre un elemento della sezione "Recenti" della Home con la stessa navigazione (e la stessa conferma di
    /// abbandono delle modifiche) della lista da cui proviene.</summary>
    private void ApriElementoRecente(RecentItemsService.ElementoRecente elemento)
    {
        switch (elemento.Tipo)
        {
            case RecentItemsService.TipoTest: NavigaATestCase(elemento.Id); break;
            case RecentItemsService.TipoChangeRequest: NavigaAChangeRequest(elemento.Id); break;
            case RecentItemsService.TipoSegnalazioneRete: NavigaASegnalazioneRete(elemento.Id); break;
        }
    }

    /// <summary>KPI "Change request aperte" della Home: stessa destinazione della voce di menu Anomalie / CR.</summary>
    private void VaiAAnomalie() => SelectedNavItem = NavItems.First(n => n.Id == "anomalie");

    private void NavigaATestSenzaEcuCore()
    {
        var item = NavItems.First(n => n.Id == "test");
        ImpostaNavSenzaRicaricare(item);
        var vm = OttieniPagina(item, out _);
        if (vm is TestCaseListViewModel lista) lista.ImpostaSoloSenzaEcu();
        ImpostaCurrentPage(vm);
    }

    private async Task NavigaATestCaseDopoConferma(string? testCaseId)
    {
        if (!await ConfermaAbbandonoPaginaCorrenteSeNecessario()) return;
        NavigaATestCaseCore(testCaseId);
    }

    private void NavigaATestCaseCore(string? testCaseId, Guid? segnalazioneDaEvidenziareUid = null)
    {
        ImpostaNavSenzaRicaricare(NavItems.First(n => n.Titolo == "Test"));
        RegistraRecente(RecentItemsService.TipoTest, testCaseId);

        if (testCaseId == null)
        {
            var bozza = _state.BozzaAutosave.Carica(_state.Config.OperatoreAttivo);
            if (bozza != null)
            {
                _ = OffriRipristinoBozza(bozza);
                return;
            }
        }

        ImpostaCurrentPage(new TestCaseEditorViewModel(_state, testCaseId, TornaAllaListaTestCase, NavigaATestCase,
            navigaATestInModifica: NavigaATestCaseInModifica,
            segnalazioneDaEvidenziareUid: segnalazioneDaEvidenziareUid));
    }

    private async Task OffriRipristinoBozza(BozzaAutosalvata bozza)
    {
        var loc = LocalizationService.Instance;
        var ripristina = await ConfirmService.Chiedi(
            loc.Traduci("BozzaTrovataTitolo"),
            string.Format(loc.Traduci("BozzaTrovataBodyFmt"), bozza.SalvataIl),
            loc.Traduci("RipristinaBozzaBtn"), loc.Traduci("ScartaBozzaBtn"));

        if (ripristina) _state.BozzaPerNuovoTest = bozza.TestCase;
        else _state.BozzaAutosave.Elimina(_state.Config.OperatoreAttivo);

        ImpostaCurrentPage(new TestCaseEditorViewModel(_state, null, TornaAllaListaTestCase, NavigaATestCase, navigaATestInModifica: NavigaATestCaseInModifica));
    }

    private void NavigaATestCaseInModifica(string? testCaseId)
    {
        if (HaModificheNonSalvate()) { _ = NavigaATestCaseInModificaDopoConferma(testCaseId); return; }
        NavigaATestCaseInModificaCore(testCaseId);
    }

    private async Task NavigaATestCaseInModificaDopoConferma(string? testCaseId)
    {
        if (!await ConfermaAbbandonoPaginaCorrenteSeNecessario()) return;
        NavigaATestCaseInModificaCore(testCaseId);
    }

    private void NavigaATestCaseInModificaCore(string? testCaseId)
    {
        ImpostaNavSenzaRicaricare(NavItems.First(n => n.Titolo == "Test"));
        RegistraRecente(RecentItemsService.TipoTest, testCaseId);
        var vm = new TestCaseEditorViewModel(_state, testCaseId, TornaAllaListaTestCase, NavigaATestCase, navigaATestInModifica: NavigaATestCaseInModifica);
        // Asincrono: il lock viene acquisito fuori dal thread UI. Nel frattempo la pagina è in sola lettura: i campi
        // sono IsEnabled="{Binding InModifica}" e Salva è visibile solo in modifica (MostraAzioniModifica), quindi
        // nessun input può andare perso. Chiudere la pagina prima della fine rilascia un lock acquisito tardi (Dispose).
        vm.IniziaModificaCommand.Execute(null);
        ImpostaCurrentPage(vm);
    }

    private void TornaAllaListaTestCase()
    {
        var voceTest = NavItems.First(n => n.Titolo == "Test");
        ImpostaNavSenzaRicaricare(voceTest);
        ImpostaCurrentPage(OttieniPagina(voceTest));
    }

    private void TornaAllaListaAnomalie()
    {
        var voceAnomalie = NavItems.First(n => n.Id == "anomalie");
        ImpostaNavSenzaRicaricare(voceAnomalie);
        ImpostaCurrentPage(OttieniPagina(voceAnomalie));
    }

    private void TornaDallaChangeRequest()
    {
        if (CurrentPage is ChangeRequestEditorViewModel { ModificheNonSalvate: true, AbbandonoConfermato: false })
        {
            _ = TornaDallaChangeRequestDopoConferma();
            return;
        }
        TornaAllaListaAnomalie();
    }

    private async Task TornaDallaChangeRequestDopoConferma()
    {
        if (!await ConfermaAbbandonoPaginaCorrenteSeNecessario()) return;
        TornaAllaListaAnomalie();
    }

    private void NavigaAChangeRequest(string? id)
    {
        if (HaModificheNonSalvate()) { _ = NavigaAChangeRequestDopoConferma(id); return; }
        NavigaAChangeRequestCore(id);
    }

    private async Task NavigaAChangeRequestDopoConferma(string? id)
    {
        if (!await ConfermaAbbandonoPaginaCorrenteSeNecessario()) return;
        NavigaAChangeRequestCore(id);
    }

    /// <summary>"Apri" dalla lista: lo stesso editor della creazione, prima in sola lettura; "Modifica" apre un editor
    /// nuovo (riletto dall'archivio) in modifica.</summary>
    private void NavigaAChangeRequestSolaLettura(string? id)
    {
        if (HaModificheNonSalvate()) { _ = NavigaAChangeRequestSolaLetturaDopoConferma(id); return; }
        NavigaAChangeRequestCore(id, soloLettura: true);
    }

    private async Task NavigaAChangeRequestSolaLetturaDopoConferma(string? id)
    {
        if (!await ConfermaAbbandonoPaginaCorrenteSeNecessario()) return;
        NavigaAChangeRequestCore(id, soloLettura: true);
    }

    private void NavigaAChangeRequestCore(string? id, bool soloLettura = false)
    {
        ImpostaNavSenzaRicaricare(NavItems.First(n => n.Id == "anomalie"));
        RegistraRecente(RecentItemsService.TipoChangeRequest, id);
        var editor = new ChangeRequestEditorViewModel(_state, id, TornaDallaChangeRequest, NavigaATestCase);
        if (soloLettura && id != null) editor.ImpostaSolaLetturaIniziale(() => SostituisciPosizione(() => NavigaAChangeRequestCore(id)));
        ImpostaCurrentPage(editor);
    }

    /// <summary>Editor per una NC senza Test (feedback utente 2026-09-08): stessa meccanica di navigazione/
    /// conferma abbandono di NavigaAChangeRequest, pagina "anomalie" di partenza e di ritorno.</summary>
    private void TornaDallaSegnalazioneRete()
    {
        if (CurrentPage is SegnalazioneReteEditorViewModel { ModificheNonSalvate: true })
        {
            _ = TornaDallaSegnalazioneReteDopoConferma();
            return;
        }
        TornaAllaListaAnomalie();
    }

    private async Task TornaDallaSegnalazioneReteDopoConferma()
    {
        if (!await ConfermaAbbandonoPaginaCorrenteSeNecessario()) return;
        TornaAllaListaAnomalie();
    }

    private void NavigaASegnalazioneRete(string? id)
    {
        if (HaModificheNonSalvate()) { _ = NavigaASegnalazioneReteDopoConferma(id); return; }
        NavigaASegnalazioneReteCore(id);
    }

    private async Task NavigaASegnalazioneReteDopoConferma(string? id)
    {
        if (!await ConfermaAbbandonoPaginaCorrenteSeNecessario()) return;
        NavigaASegnalazioneReteCore(id);
    }

    /// <summary>"Apri" dalla lista per una NC da segnalazione di rete: stesso editor, prima in sola lettura.</summary>
    private void NavigaASegnalazioneReteSolaLettura(string? id)
    {
        if (HaModificheNonSalvate()) { _ = NavigaASegnalazioneReteSolaLetturaDopoConferma(id); return; }
        NavigaASegnalazioneReteCore(id, soloLettura: true);
    }

    private async Task NavigaASegnalazioneReteSolaLetturaDopoConferma(string? id)
    {
        if (!await ConfermaAbbandonoPaginaCorrenteSeNecessario()) return;
        NavigaASegnalazioneReteCore(id, soloLettura: true);
    }

    private void NavigaASegnalazioneReteCore(string? id, bool soloLettura = false)
    {
        ImpostaNavSenzaRicaricare(NavItems.First(n => n.Id == "anomalie"));
        RegistraRecente(RecentItemsService.TipoSegnalazioneRete, id);
        var editor = new SegnalazioneReteEditorViewModel(_state, id, TornaDallaSegnalazioneRete, NavigaASegnalazioneRete, NavigaATestCase);
        if (soloLettura && id != null) editor.ImpostaSolaLetturaIniziale(() => SostituisciPosizione(() => NavigaASegnalazioneReteCore(id)));
        ImpostaCurrentPage(editor);
    }

    [RelayCommand]
    private async Task VaiAImpostazioniGenerale()
    {
        SelectedNavItem = NavItems.First(n => n.Id == "impostazioni");
        // In UI reale la pagina arriva dopo un frame: quando il setter ritorna, CurrentPage e' ancora la pagina precedente.
        // Si attende la navigazione appena avviata (o la conferma di abbandono che puo' precederla), poi si applica la sotto-sezione.
        await _navigazioneInCorso;
        if (CurrentPage is ImpostazioniViewModel pagina) pagina.VaiAGenerale();
    }

    private void OnPrimoAvvioCompletato()
    {
        MostraPrimoAvvio = false;
        AggiornaStatoBarra();
        // Se l'account è stato disattivato mentre un editor conteneva modifiche, la schermata di accesso
        // si è sovrapposta senza disporre la pagina. Dopo una nuova autenticazione la lasciamo aperta per
        // permettere all'utente di completare o salvare il lavoro con la nuova identità.
        if (_mantieniPaginaDopoAccesso && CurrentPage is not null)
        {
            _mantieniPaginaDopoAccesso = false;
            return;
        }
        _mantieniPaginaDopoAccesso = false;
        SelectedNavItem = NavItems[0];
        AvviaOnboardingSeNecessario();
        MostraNotificheNonLetteAllAvvioSeNecessario();
    }

    /// <summary>Popup notifiche non lette all'avvio (feedback utente 2026-09-18: "aggiungi anche un pop-up
    /// che mostri le notifiche non lette all'avvio dell'applicazione"), non ad ogni AggiornaStatoBarra():
    /// quel metodo viene richiamato molto più spesso di quanto sembri — ogni minuto dal timer periodico,
    /// ad ogni Salva/refresh tramite AppState.DatiRicaricati, ad ogni cambio lingua — e non deve riaprire il
    /// popup ogni volta, solo la prima volta dopo che l'app si è aperta (o dopo il primo accesso/login).
    /// Riusa lo stesso centro notifiche già esistente (MostraNotifiche, aperto finora solo dal campanello),
    /// non un dialog nuovo.</summary>
    private bool _notificheAvvioVerificate;
    private void MostraNotificheNonLetteAllAvvioSeNecessario()
    {
        if (_notificheAvvioVerificate) return;
        _notificheAvvioVerificate = true;
        if (NotificheNonLette <= 0) return;
        // G2 (6.4.5): con il tour guidato in corso il popup notifiche si sovrapponeva al tour. Si rimanda a
        // dopo il tour (CompletaOnboarding): le notifiche non lette restano comunque mostrate una volta.
        if (MostraOnboarding) _notificheAvvioInAttesaTour = true;
        else MostraNotifiche = true;
    }
    private bool _notificheAvvioInAttesaTour;

    private void AvviaOnboardingSeNecessario(bool forza = false)
    {
        var operatore = _state.OperatoreCorrente;
        if (operatore == null || (!forza && OnboardingService.ECompletato(_state.Config, operatore.Nome, operatore.Ruolo))) return;

        AnteprimaRuoloOnboarding = RuoliAnteprimaOnboarding.First(x => x.Ruolo == operatore.Ruolo);
        PassiOnboarding = OnboardingService.CreaPassi(operatore.Ruolo, LocalizationService.Instance.Lingua);
        IndiceOnboarding = 0;
        MostraAiuto = false;
        MostraNotifiche = false;
        MostraOnboarding = true;
        MostraPassoOnboarding();
    }

    private void MostraPassoOnboarding()
    {
        var passo = PassoOnboardingCorrente;
        if (passo == null) return;
        var destinazione = NavItems.FirstOrDefault(n => n.Id == passo.NavId);
        foreach (var item in NavItems) item.EvidenziatoTour = item == destinazione;
        if (destinazione != null && _navCorrente?.Id != destinazione.Id)
        {
            ImpostaNavSenzaRicaricare(destinazione);
            ImpostaCurrentPage(OttieniPagina(destinazione));
        }
        Spotlight.TargetId = passo.TargetId;
        NotificaPassoOnboarding();
    }

    private void NotificaPassoOnboarding()
    {
        OnPropertyChanged(nameof(PassiOnboarding));
        OnPropertyChanged(nameof(PassoOnboardingCorrente));
        OnPropertyChanged(nameof(ProgressoOnboarding));
        OnPropertyChanged(nameof(ValoreProgressoOnboarding));
        OnPropertyChanged(nameof(PuoTornareOnboarding));
        OnPropertyChanged(nameof(EUltimoPassoOnboarding));
        OnPropertyChanged(nameof(FocusOnboarding));
        OnPropertyChanged(nameof(RuoloOnboardingLabel));
    }

    [RelayCommand]
    private async Task RiavviaOnboarding()
    {
        if (!await ConfermaAbbandonoPaginaCorrenteSeNecessario()) return;
        AvviaOnboardingSeNecessario(true);
    }

    [RelayCommand]
    private void PassoOnboardingPrecedente()
    {
        if (IndiceOnboarding <= 0) return;
        IndiceOnboarding--;
        MostraPassoOnboarding();
    }

    [RelayCommand]
    private void PassoOnboardingSuccessivo()
    {
        if (EUltimoPassoOnboarding) { CompletaOnboarding(); return; }
        IndiceOnboarding++;
        MostraPassoOnboarding();
    }

    [RelayCommand]
    private void SaltaOnboarding() => CompletaOnboarding();

    private void CompletaOnboarding()
    {
        var operatore = _state.OperatoreCorrente;
        if (operatore != null)
        {
            OnboardingService.SegnaCompletato(_state.Config, operatore.Nome, operatore.Ruolo);
            try { _state.ConfigService.Save(_state.Config); }
            catch (Exception ex) { CrashLogService.Registra("Salvataggio completamento onboarding", ex); }
        }
        MostraOnboarding = false;
        Spotlight.TargetId = null;
        foreach (var item in NavItems) item.EvidenziatoTour = false;
        if (_notificheAvvioInAttesaTour)
        {
            _notificheAvvioInAttesaTour = false;
            RicaricaCentroNotifiche();
            if (NotificheNonLette > 0) MostraNotifiche = true;
        }
    }

    partial void OnAnteprimaRuoloOnboardingChanged(RolePreviewOption? value)
    {
        OnPropertyChanged(nameof(RuoloOnboardingLabel));
        if (!MostraOnboarding || !_state.EMaster || value == null) return;
        PassiOnboarding = OnboardingService.CreaPassi(value.Ruolo, LocalizationService.Instance.Lingua);
        IndiceOnboarding = 0;
        MostraPassoOnboarding();
    }

    partial void OnNumeroFileConProblemiChanged(int value) => OnPropertyChanged(nameof(TestoFileConProblemi));
    partial void OnNumeroDuplicatiIdChanged(int value) => OnPropertyChanged(nameof(TestoDuplicatiId));

    private void AggiornaStatoBarra()
    {
        // Il catalogo operatori può cambiare mentre l'app è aperta (per esempio il Master disattiva
        // l'account usato su questo PC). Torna alla schermata di accesso al refresh successivo senza
        // alterare o scartare la pagina corrente; dopo una nuova autenticazione il flusso standard
        // riapre la Home.
        if (_controlloSessioneOperatore && !MostraPrimoAvvio && _state.InizializzazioneWorkspaceCompletata &&
            _state.OperatoreCorrente is null && !string.IsNullOrWhiteSpace(_state.Config.OperatoreAttivo))
        {
            _mantieniPaginaDopoAccesso = HaModificheNonSalvate();
            MostraOnboarding = false;
            MostraNotifiche = false;
            MostraAiuto = false;
            MostraRicercaGlobale = false;
            FirstRun.ResetPerNuovoAccesso();
            FirstRun.MessaggioErrore = LocalizationService.Instance.Traduci("OperatoreMemorizzatoNonDisponibile");
            DiagnosticTraceService.Registra("Operatore.SessioneNonValida",
                "L'account memorizzato localmente non corrisponde più a un operatore attivo nella cartella condivisa.");
            MostraPrimoAvvio = true;
        }

        OperatoreAttivo = _state.Config.OperatoreAttivo;
        RuoloAttivo = _state.OperatoreCorrente?.Ruolo.Label() ?? "";
        RicaricaCentroNotifiche();
        OnPropertyChanged(nameof(TestoMenuAccount));
        Help.AggiornaRuolo();
        AggiornaBannerCondivisione();
        AggiornaAvvisoCartellaCondivisa();
        if (_state.CartellaValida)
        {
            NumeroFileConProblemi = _state.TuttiIFileConProblemi().Count;
            MostraFileConProblemi = NumeroFileConProblemi > 0;
            NumeroDuplicatiId = _state.TestCaseDuplicati().Count;
            MostraDuplicatiId = NumeroDuplicatiId > 0;
        }
    }

    /// <summary>
    /// Banner di stato della cartella condivisa. Solo lettura di campi già calcolati (nessun I/O): può essere chiamato a ogni
    /// giro della sonda. Mostrato se il workspace è inizializzato e lo stato non è operativo (6.7.1: non più subordinato a
    /// CartellaValida, che è falso proprio quando la cartella non c'è).
    /// </summary>
    private void AggiornaBannerCondivisione()
    {
        var stato = _state.StatoCondivisione;
        MostraStatoCondivisione = MonitorRaggiungibilitaRadice.MostraBanner(_state.InizializzazioneWorkspaceCompletata, stato.Stato);
        TestoStatoCondivisione = stato.Stato == Models.StatoCondivisione.SolaLettura
            ? string.Format(LocalizationService.Instance.Traduci("SolaLetturaPrefixFmt"), stato.Messaggio)
            : string.Format(LocalizationService.Instance.Traduci("CartellaNonDisponibilePrefixFmt"), stato.Messaggio);
    }

    /// <summary>
    /// Giro della sonda della radice (6.7.1). La verifica gira in background; se la cartella torna raggiungibile dopo una
    /// perdita, la rilettura di recupero riapplica il controllo di scrittura normale e il banner si chiude.
    /// Non si sovrappone a un aggiornamento in corso né a un'altra sonda.
    /// </summary>
    private async Task SondaRadiceInBackground()
    {
        if (_sondaRadiceInCorso || AggiornamentoInCorso || MostraPrimoAvvio) return;
        if (!_state.InizializzazioneWorkspaceCompletata) return;
        _sondaRadiceInCorso = true;
        try
        {
            var passo = await Task.Run(() => _state.SondaRadiceCondivisa(_monitorRadice, forza: false));
            if (passo.Ricalcola && !AggiornamentoInCorso)
            {
                var esclusioni = CalcolaEsclusioniRefresh();
                AggiornamentoInCorso = true;
                try
                {
                    await Task.Run(() => _state.RileggiDaDisco(esclusioni));
                    _state.NotificaDatiRicaricati();
                    UltimoAggiornamento = DateTime.Now;
                }
                finally
                {
                    AggiornamentoInCorso = false;
                }
            }
            AggiornaBannerCondivisione();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Sonda periodica cartella condivisa", ex);
        }
        finally
        {
            _sondaRadiceInCorso = false;
        }
    }

    /// <summary>
    /// Avviso non bloccante "cartella incerta": legge solo il valore calcolato dal giro periodico in background (nessun I/O
    /// sul thread UI). Compare solo se il gate esistente considera la cartella operativa: se è già in sola lettura o non
    /// disponibile, lo spiega già il banner di stato.
    /// </summary>
    private void AggiornaAvvisoCartellaCondivisa()
    {
        var salute = _state.SaluteCartellaCondivisa;
        var mostra = _state.CartellaValida
                     && salute is { Stato: SharedFolderHealthState.Incerta }
                     && _state.StatoCondivisione.Stato == Models.StatoCondivisione.Operativa;
        MostraAvvisoCartellaIncerta = mostra;
        // Solo i due motivi più rilevanti: la spiegazione generica sta nel tooltip del banner.
        TestoAvvisoCartellaIncerta = mostra && salute != null ? string.Join(" ", salute.Motivi.Take(2)) : "";
    }

    /// <summary>
    /// Attiva il visualizzatore locale (stesso interruttore manuale delle Impostazioni, WorkspaceReadOnlyService) e riallinea
    /// subito la barra. È una scelta locale di questo PC: nessun file condiviso viene modificato.
    /// </summary>
    [RelayCommand]
    private async Task AttivaVisualizzatoreDaAvvisoCartella()
    {
        if (!_state.CartellaValida) return;
        var loc = LocalizationService.Instance;
        var confermato = await ConfirmService.Chiedi(
            loc.Traduci("AttivaVisualizzatoreConfermaTitolo"),
            loc.Traduci("AttivaVisualizzatoreConfermaTesto"),
            loc.Traduci("AttivaVisualizzatoreConfermaBtn"), loc.Traduci("AnnullaSemplice"));
        if (!confermato) return;
        try { WorkspaceReadOnlyService.SetEnabled(_state.Paths.Root, true); }
        catch (Exception ex)
        {
            CrashLogService.Registra("Attivazione visualizzatore da avviso cartella incerta", ex);
            return;
        }
        await AggiornaOra();
    }

    [RelayCommand]
    private async Task Logout()
    {
        if (!await ConfermaAbbandonoPaginaCorrenteSeNecessario()) return;
        // Invalida anche gli scaldamenti ancora in corso. Un risultato tardivo della sessione precedente non
        // deve ripopolare la cache e finire in uso dopo il login di un altro operatore.
        Interlocked.Increment(ref _generazioneCachePagine);
        ImpostaCurrentPage(null);
        DisposePagineCache();
        _state.Logout();
        MostraOnboarding = false;
        Help.Reset();
        FirstRun.ResetPerNuovoAccesso();
        ImpostaNavSenzaRicaricare(null);
        AggiornaStatoBarra();
        MostraPrimoAvvio = true;
    }

    [RelayCommand]
    private void ApriAiuto() => MostraAiuto = true;

    [RelayCommand]
    private void ApriRicercaGlobale()
    {
        if (MostraPrimoAvvio) return;
        MostraAiuto = false;
        MostraNotifiche = false;
        RicercaGlobale.PreparaApertura();
        MostraRicercaGlobale = true;
    }

    [RelayCommand]
    private void ChiudiRicercaGlobale()
    {
        MostraRicercaGlobale = false;
        RicercaGlobale.Reset();
    }

    public void ApriPrimoRisultatoRicerca() => RicercaGlobale.ApriPrimo();

    private void ApriRisultatoRicerca(GlobalSearchResult risultato)
    {
        MostraRicercaGlobale = false;
        RicercaGlobale.Reset();
        if (risultato.Destinazione == "segnalazione" && !string.IsNullOrWhiteSpace(risultato.ElementoId) &&
            risultato.ElementoUid is { } segnalazioneUid)
        {
            ApriSegnalazioneDaRicerca(risultato.ElementoId, segnalazioneUid);
            return;
        }
        if (risultato.Destinazione == "test" && !string.IsNullOrWhiteSpace(risultato.ElementoId))
        {
            NavigaATestCase(risultato.ElementoId);
            return;
        }
        if (risultato.Destinazione == "change-request" && !string.IsNullOrWhiteSpace(risultato.ElementoId))
        {
            NavigaAChangeRequest(risultato.ElementoId);
            return;
        }
        var destinazione = NavItems.FirstOrDefault(n => n.Id == risultato.Destinazione);
        if (destinazione != null) SelectedNavItem = destinazione;
    }

    private void ApriSegnalazioneDaRicerca(string testId, Guid segnalazioneUid)
    {
        if (HaModificheNonSalvate())
        {
            _ = ApriSegnalazioneDaRicercaDopoConferma(testId, segnalazioneUid);
            return;
        }
        NavigaATestCaseCore(testId, segnalazioneUid);
    }

    private async Task ApriSegnalazioneDaRicercaDopoConferma(string testId, Guid segnalazioneUid)
    {
        if (!await ConfermaAbbandonoPaginaCorrenteSeNecessario()) return;
        NavigaATestCaseCore(testId, segnalazioneUid);
    }

    [RelayCommand]
    private void ChiudiAiuto() => MostraAiuto = false;

    [RelayCommand]
    private void ApriNotifiche()
    {
        _notificheAvvioInAttesaTour = false;
        MostraNotifiche = true;
    }

    [RelayCommand]
    private void ChiudiNotifiche() => MostraNotifiche = false;

    [RelayCommand]
    private void SegnaTutteLette()
    {
        foreach (var n in NotificheRecenti.Where(n => !n.Letta)) _state.Notifiche.MarkRead(n);
        NotificheNonLette = 0;
        OnPropertyChanged(nameof(TestoMenuAccount));
    }

    public bool HaModificheNonSalvate() =>
        CurrentPage is TestCaseEditorViewModel { ModificheNonSalvate: true } ||
        CurrentPage is ChangeRequestEditorViewModel { ModificheNonSalvate: true } ||
        CurrentPage is SegnalazioneReteEditorViewModel { ModificheNonSalvate: true } ||
        CurrentPage is SegnalazioneReteEditorViewModel { CaricamentoAllegatoInCorso: true } ||
        (CurrentPage is ImpostazioniViewModel impostazioni && impostazioni.PaginaCorrente is IEditorConModifiche editor && editor.HaModificheNonSalvate());

    public async Task<bool> RilasciaLockEditorAttivoAsync()
    {
        if (CurrentPage is ImpostazioniViewModel impostazioni && impostazioni.PaginaCorrente is IEditorConModifiche editor)
        {
            await editor.RilasciaLockSeAttivoAsync();
            return !editor.RilascioLockInAttesa;
        }
        if (CurrentPage is TestCaseEditorViewModel testEditor)
            return await testEditor.RilasciaLockPerUscitaAsync();
        return true;
    }

    public void EliminaBozzaEditorAttivo()
    {
        if (CurrentPage is TestCaseEditorViewModel { IsNew: true })
            _state.BozzaAutosave.Elimina(_state.Config.OperatoreAttivo);
    }

    /// <summary>"Esci" dalla tray (MainWindow.RichiediUscitaDefinitivaAsync). Con modifiche non salvate chiede conferma:
    /// Sì scarta la bozza dell'editor attivo; No restituisce false e non tocca nulla, l'uscita non avviene.
    /// Ogni uscita confermata rilascia il lock dell'editor attivo. La conferma e' iniettabile solo per i test;
    /// in produzione usa il dialogo standard di ConfirmService.</summary>
    public async Task<bool> PreparaUscitaDefinitivaAsync(Func<Task<bool>>? confermaAbbandono = null)
    {
        if (CurrentPage is SegnalazioneReteEditorViewModel segnalazione)
            await segnalazione.AttendiCaricamentoAllegatoAsync();
        var haModificheNonSalvate = HaModificheNonSalvate();
        if (haModificheNonSalvate)
        {
            var conferma = confermaAbbandono ?? (() => ConfirmService.ConfermaAbbandonoModifiche(CurrentPage));
            if (!await conferma()) return false;
        }
        if (!await RilasciaLockEditorAttivoAsync()) return false;
        if (haModificheNonSalvate) EliminaBozzaEditorAttivo();
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _polarionBrowserLinkMonitor.Dispose();
        _polarionBackgroundSync.Dispose();
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _sondaRadiceTimer.Stop();
        _sondaRadiceTimer.Dispose();
        _debounceRefreshWatcher.Stop();
        _debounceRefreshWatcher.Dispose();
        _state.CambiamentiSuDiscoRilevati -= _cambiamentiSuDiscoRilevati;
        _richiestaNavigazione++;
        LocalizationService.Instance.PropertyChanged -= _linguaCambiata;
        _state.DatiRicaricati -= _datiRicaricati;
        CrashLogService.ErroreUiSegnalato -= _erroreUiSegnalato;
        DisposePagineCache();
        ChiusuraDiagnostica.Registra("MainViewModel: pagina corrente disposta");
        _state.Dispose();
        ChiusuraDiagnostica.Registra("MainViewModel: stato applicativo disposto");
        GC.SuppressFinalize(this);
    }

    private IScorciatoiePagina? PaginaScorciatoie => CurrentPage is ImpostazioniViewModel impostazioni
        ? impostazioni.PaginaCorrente as IScorciatoiePagina
        : CurrentPage as IScorciatoiePagina;

    public bool SalvaDaTastiera() => PaginaScorciatoie?.SalvaDaTastiera() ?? false;
    public bool NuovoDaTastiera() => PaginaScorciatoie?.NuovoDaTastiera() ?? false;
    public bool AnnullaDaTastiera() => PaginaScorciatoie?.AnnullaDaTastiera() ?? false;

    public bool VaiASezioneDaTastiera(int numero)
    {
        var indice = numero - 1;
        if (indice < 0 || indice >= NavItems.Count) return false;
        if (MostraPrimoAvvio || MostraOnboarding) return false;
        SelectedNavItem = NavItems[indice];
        return true;
    }

    public bool ChiudiOverlayDaTastiera()
    {
        if (MostraRicercaGlobale) { ChiudiRicercaGlobaleCommand.Execute(null); return true; }
        if (MostraNotifiche) { ChiudiNotificheCommand.Execute(null); return true; }
        if (MostraAiuto) { ChiudiAiutoCommand.Execute(null); return true; }
        return false;
    }

    private Action? _richiestaAttivazione;
    public void RichiestaAttivazioneSottoscrivi(Action callback) => _richiestaAttivazione = callback;
    public void RichiediAttivazione() => _richiestaAttivazione?.Invoke();
}
