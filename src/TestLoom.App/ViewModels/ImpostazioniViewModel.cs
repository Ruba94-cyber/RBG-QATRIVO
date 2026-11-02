using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Impostazioni: hub con sotto-navigazione (Addendum §17.9) che raccoglie tutte le funzioni
/// amministrative/richiamate raramente — Cestino, gestione utenze, ed editor dei master data —
/// invece di occupare voci separate nella barra laterale principale.</summary>
public partial class ImpostazioniViewModel : ViewModelBase, IDisposable
{
    private readonly AppState _state;
    private readonly Action _onCambiato;

    [ObservableProperty] private string cartella;
    [ObservableProperty] private string? messaggio;
    public bool HaMessaggio => !string.IsNullOrEmpty(Messaggio);

    /// <summary>Distinto da Messaggio (mostrato sempre in verde come conferma di successo): un percorso
    /// cartella non valido non deve apparire con lo stesso colore di "aggiornata con successo".</summary>
    [ObservableProperty] private string? messaggioErrore;
    public bool HaMessaggioErrore => !string.IsNullOrEmpty(MessaggioErrore);
    public bool HaFileConProblemi => NumeroFileConProblemi > 0;

    public string Versione => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    public string CartellaAttuale => _state.Config.CartellaCondivisa;
    public bool CartellaValida => _state.CartellaValida;
    public int NumeroFileConProblemi => _state.CartellaValida
        ? _state.TuttiIFileConProblemi().Count
        : 0;
    public List<Models.FileConProblemi> FileConProblemi => _state.CartellaValida
        ? _state.TuttiIFileConProblemi().ToList()
        : new();

    /// <summary>Badge di stato sincronizzazione (Addendum §4, feedback QA): prima esisteva solo un banner
    /// d'avviso quando qualcosa non andava, niente che confermasse a colpo d'occhio che è tutto allineato.
    /// "Forza sincronizzazione" richiama lo stesso ricontrollo/ricarica già usato al cambio cartella, reso
    /// disponibile anche come azione esplicita per quando la cartella condivisa sembra disallineata.</summary>
    // Non equivale alla conferma cloud: il controllo vede soltanto il filesystem locale.
    public bool SincronizzazioneOk => _state.StatoCondivisione.Stato == Models.StatoCondivisione.Operativa;
    public bool ModalitaLocaleSolaLetturaAttiva =>
        _state.Paths != null && WorkspaceReadOnlyService.IsEnabled(_state.Paths.Root);

    /// <summary>Opzione del selettore "Modalità cartella condivisa" (6.6.1, a). Valore = valore di AppConfig.</summary>
    public sealed record OpzioneModalitaCondivisione(string Valore, string Etichetta)
    {
        public override string ToString() => Etichetta;
    }

    /// <summary>Opzioni del selettore: rilevamento automatico, OneDrive/SharePoint sincronizzata, cartella di rete.</summary>
    public IReadOnlyList<OpzioneModalitaCondivisione> OpzioniModalitaCondivisione { get; } = CreaOpzioniModalitaCondivisione();

    [ObservableProperty] private OpzioneModalitaCondivisione? modalitaCondivisioneSelezionata;

    /// <summary>Vero se questo PC usa la cartella condivisa come cartella di rete (UNC o unità mappata, o override manuale).</summary>
    // Non attende la rete (P4): per un'unità mappata ancora in risoluzione mostra l'ultimo esito noto e rilegge la modalità
    // quando la risoluzione in background termina (vedi AlClassificata).
    public bool EModalitaRete => ModalitaCondivisioneService.ERetePerInterfaccia(_state.Config.CartellaCondivisa);

    /// <summary>Attributi e pulsanti Cloud Files (OneDrive Files On-Demand) visibili solo fuori dalla modalità di rete (6.6.1, b).</summary>
    public bool MostraAzioniCloudFiles => !EModalitaRete;

    /// <summary>Riga "Modalità in uso": cosa usa TestLoom per questo PC e perché (rilevazione o scelta manuale).</summary>
    public string ModalitaCondivisioneEffettivaTesto
    {
        get
        {
            var loc = LocalizationService.Instance;
            var etichetta = EModalitaRete
                ? loc.Traduci("ModalitaCondivisioneRete")
                : loc.Traduci("ModalitaCondivisioneOneDrive");
            var motivo = ModalitaCondivisioneService.OverrideRadice(_state.Config.CartellaCondivisa) == ModalitaCondivisioneService.ValoreAutomatica
                ? loc.Traduci("ModalitaCondivisioneMotivoAuto")
                : loc.Traduci("ModalitaCondivisioneMotivoManuale");
            return string.Format(loc.Traduci("ModalitaCondivisioneEffettivaFmt"), etichetta, motivo);
        }
    }

    private static IReadOnlyList<OpzioneModalitaCondivisione> CreaOpzioniModalitaCondivisione()
    {
        var loc = LocalizationService.Instance;
        return new[]
        {
            new OpzioneModalitaCondivisione(ModalitaCondivisioneService.ValoreAutomatica, loc.Traduci("ModalitaCondivisioneAutomatica")),
            new OpzioneModalitaCondivisione(ModalitaCondivisioneService.ValoreOneDrive, loc.Traduci("ModalitaCondivisioneOneDrive")),
            new OpzioneModalitaCondivisione(ModalitaCondivisioneService.ValoreRete, loc.Traduci("ModalitaCondivisioneRete")),
        };
    }

    /// <summary>Salva per questo PC la modalità scelta e la applica subito. Automatica non viene scritta nel config.json
    /// (resta come prima dell'aggiornamento). Locale a questo PC: nessun file della cartella condivisa cambia.</summary>
    [RelayCommand]
    private void ApplicaModalitaCondivisione()
    {
        var valore = ModalitaCondivisioneService.NormalizzaValore(ModalitaCondivisioneSelezionata?.Valore);
        _state.Config.ModalitaCartellaCondivisa = valore == ModalitaCondivisioneService.ValoreAutomatica ? null : valore;
        _state.ConfigService.Save(_state.Config);
        ModalitaCondivisioneService.ImpostaOverrideRadice(_state.Config.CartellaCondivisa, valore);
        NotificaModalitaCondivisione();
        MessaggioErrore = null;
        Messaggio = LocalizationService.Instance.Traduci("ModalitaCondivisioneSalvata");
        _onCambiato();
    }

    private void NotificaModalitaCondivisione()
    {
        OnPropertyChanged(nameof(ModalitaCondivisioneEffettivaTesto));
        OnPropertyChanged(nameof(EModalitaRete));
        OnPropertyChanged(nameof(MostraAzioniCloudFiles));
        OnPropertyChanged(nameof(StatoSincronizzazioneTesto));
    }

    /// <summary>
    /// Consenso esplicito raccolto dalla view con un dialogo. Il blocco viene mantenuto
    /// FUORI dal workspace sincronizzato e preservato al riavvio, anche durante il
    /// cleanup degli indici. Nessun formato di dati 6.5.3 viene modificato.
    /// </summary>
    public void ImpostaModalitaLocaleSolaLettura(bool enabled)
    {
        if (!_state.CartellaValida)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("ImpCartellaNonInizializzataMsg");
            return;
        }
        try
        {
            // Prima fissare lo stato locale, poi rileggerlo tramite preflight senza
            // scritture condivise quando il blocco è attivo.
            WorkspaceReadOnlyService.SetEnabled(_state.Paths.Root, enabled);
            _state.RicaricaTutto();
            MessaggioErrore = null;
            Messaggio = enabled
                ? LocalizationService.Instance.Traduci("ImpVisLocaleAttivatoMsg")
                : (_state.PuoScrivere
                    ? LocalizationService.Instance.Traduci("ImpBloccoDisattivatoScrivibileMsg")
                    : LocalizationService.Instance.Traduci("ImpBloccoDisattivatoSolaLetturaMsg"));
            OnPropertyChanged(nameof(ModalitaLocaleSolaLetturaAttiva));
            OnPropertyChanged(nameof(StatoSincronizzazioneTesto));
            OnPropertyChanged(nameof(SincronizzazioneOk));
            _onCambiato();
        }
        catch (Exception ex)
        {
            // Il marker viene riletto anche in caso di refresh fallito.
            OnPropertyChanged(nameof(ModalitaLocaleSolaLetturaAttiva));
            MessaggioErrore = LocalizationService.Instance.Traduci("ImpImpossibileCambiareModalitaPrefisso") + ex.Message;
            CrashLogService.Registra("Cambio modalità visualizzatore locale", ex);
        }
    }

    public string EtichettaVerificaLocale => LocalizationService.Instance.Scegli(
        "Verifica dati locali", "Check local files", "Vérifier les données locales");
    [ObservableProperty] private bool disponibilitaDatiInCorso;

    /// <summary>Vero mentre il cambio cartella (inizializzazione del workspace) gira fuori dal thread UI: indicatore occupato.</summary>
    [ObservableProperty] private bool operazioneCartellaInCorso;

    /// <summary>
    /// Non-mutante sui dati TestLoom: richiede a Windows di mantenere presenti i JSON, i journal
    /// e i cataloghi condivisi. Conferma esplicita gestita dalla View; gli allegati restano online-only.
    /// Una richiesta accettata NON prova il download completo o l'allineamento cloud.
    /// </summary>
    public async Task RichiediDatiLocaliAsync()
    {
        if (DisponibilitaDatiInCorso) return;
        if (EModalitaRete)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("CloudFilesNonApplicabiliRete");
            return;
        }
        if (!_state.CartellaValida)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("ImpCartellaNonDispCopiaLocaleMsg");
            return;
        }
        DisponibilitaDatiInCorso = true;
        try
        {
            var result = await LocalDataPinService.RequestLocalAvailabilityAsync(_state.Paths);
            if (result.Accepted)
            {
                MessaggioErrore = null;
                Messaggio = string.Format(LocalizationService.Instance.Traduci("ImpRichiestaDisponibilitaFmt"), result.Requests) +
                    LocalizationService.Instance.Traduci("ImpOneDriveScaricaTesto");
            }
            else
            {
                MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ImpDisponibilitaNonVerificataFmt"), result.Errors.Count) +
                    string.Join("; ", result.Errors.Take(3));
            }
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ImpRichiestaNonRiuscitaFmt"), ex.Message);
            CrashLogService.Registra("Richiesta pinning dati operativi", ex);
        }
        finally { DisponibilitaDatiInCorso = false; }
    }
    public string StatoSincronizzazioneTesto => _state.StatoCondivisione.Stato switch
    {
        // Cartella di rete (6.6.1): nessun "cloud" da verificare, solo la prova di scrittura già eseguita.
        Models.StatoCondivisione.Operativa when EModalitaRete => LocalizationService.Instance.Traduci("SyncReteOk"),
        Models.StatoCondivisione.Operativa => LocalizationService.Instance.Scegli(
            "Cartella locale disponibile (cloud non verificato)",
            "Local folder accessible (cloud sync not verified)",
            "Dossier local accessible (cloud non vérifié)"),
        Models.StatoCondivisione.SolaLettura => LocalizationService.Instance.Traduci("SyncSolaLettura"),
        _ => LocalizationService.Instance.Traduci("SyncNonDisponibile"),
    };
    public string UltimaVerificaSincronizzazioneTesto => string.Format(
        LocalizationService.Instance.Traduci("SyncUltimaVerificaFmt"), _state.StatoCondivisione.VerificatoIl);

    /// <summary>Default condiviso delle sezioni del pacchetto Ticketing (Addendum §4, feedback QA): un Master
    /// lo tiene minimo, poi ogni operatore può comunque aggiungerne altre nel dialogo di anteprima senza
    /// toccare questo default. Sola visualizzazione per chi non è Master (EMaster gate già sul comando salva).</summary>
    public ObservableCollection<FiltroOpzione> PolarionSezioniOpzioni { get; } = new();
    private long _polarionRevisioneAttesa;
    private string? _polarionFingerprintAtteso;

    private void CaricaSezioniPolarion()
    {
        PolarionSezioniOpzioni.Clear();
        var config = _state.CartellaValida ? PolarionConfigService.Load(_state.Paths) : new PolarionExportConfig();
        _polarionRevisioneAttesa = config.RevisioneDati;
        _polarionFingerprintAtteso = config.SourceFingerprint;
        foreach (var chiave in PolarionSezioni.Tutte)
            PolarionSezioniOpzioni.Add(new FiltroOpzione { Valore = chiave, Etichetta = LocalizationService.Instance.Traduci("PolarionSezione" + chiave), Selezionato = config.SezioniDiDefault.Contains(chiave) });
    }

    [RelayCommand]
    private void SalvaSezioniPolarion()
    {
        if (!EMaster || !_state.CartellaValida) return;
        var config = new PolarionExportConfig
        {
            RevisioneDati = _polarionRevisioneAttesa,
            SourceFingerprint = _polarionFingerprintAtteso,
            SezioniDiDefault = PolarionSezioniOpzioni.Where(o => o.Selezionato).Select(o => o.Valore).ToList()
        };
        try
        {
            PolarionConfigService.Save(_state.Paths, config);
            _polarionRevisioneAttesa = config.RevisioneDati;
            _polarionFingerprintAtteso = config.SourceFingerprint;
            Messaggio = LocalizationService.Instance.Traduci("SezioniPolarionSalvateMsg");
            MessaggioErrore = null;
        }
        catch (Exception ex)
        {
            // Non ricaricare automaticamente: le scelte locali restano visibili affinché il Master possa
            // confrontarle con la versione remota dopo un refresh, senza perdere ciò che aveva selezionato.
            Messaggio = null;
            MessaggioErrore = ex.Message;
        }
    }

    private bool _polarionRefreshInCorso;
    private CancellationTokenSource? _polarionActionCts;
    private string? _polarionApiFeedback;
    private bool _polarionApiFeedbackIsError;
    private string _polarionRefreshProgressText = "";
    internal Func<HttpClient> PolarionHttpFactory { get; set; } = PolarionReadClient.CreateWindowsProxyHttpClient;
    private static string PolarionText(string it, string en, string fr) => LocalizationService.Instance.Scegli(it, en, fr);
    public string? PolarionApiFeedback { get => _polarionApiFeedback; private set => SetProperty(ref _polarionApiFeedback, value); }
    public bool PolarionApiFeedbackIsError { get => _polarionApiFeedbackIsError; private set => SetProperty(ref _polarionApiFeedbackIsError, value); }
    public bool PolarionApiFeedbackIsSuccess => !PolarionApiFeedbackIsError;
    public bool HasPolarionApiFeedback => !string.IsNullOrWhiteSpace(PolarionApiFeedback);
    public bool HasPolarionApiError => HasPolarionApiFeedback && PolarionApiFeedbackIsError;
    public bool HasPolarionApiSuccess => HasPolarionApiFeedback && !PolarionApiFeedbackIsError;
    public string PolarionTestLabel => PolarionText("Verifica connessione (sola lettura)", "Test connection (read-only)", "Tester la connexion (lecture seule)");
    public string PolarionCancelLabel => PolarionText("Annulla", "Cancel", "Annuler");
    public string PolarionRefreshProgressText { get => _polarionRefreshProgressText; private set => SetProperty(ref _polarionRefreshProgressText, value); }
    private void SetPolarionFeedback(string message, bool error)
    {
        PolarionApiFeedbackIsError = error;
        OnPropertyChanged(nameof(PolarionApiFeedbackIsSuccess));
        PolarionApiFeedback = message;
        OnPropertyChanged(nameof(HasPolarionApiFeedback));
        OnPropertyChanged(nameof(HasPolarionApiError));
        OnPropertyChanged(nameof(HasPolarionApiSuccess));
    }
    public bool PolarionRefreshInCorso
    {
        get => _polarionRefreshInCorso;
        private set => SetProperty(ref _polarionRefreshInCorso, value);
    }

    [RelayCommand]
    private async Task AggiornaPolarionOraAsync()
    {
        if (PolarionRefreshInCorso || !_state.CartellaValida || _state.OperatoreCorrente is null) return;
        if (PolarionApiHasUnsavedChanges)
        {
            SetPolarionFeedback(PolarionText("Salva prima le modifiche TICKET.", "Save TICKET changes first.", "Enregistrez d’abord les modifications TICKET."), true);
            return;
        }
        PolarionRefreshInCorso = true;
        using var cts = _polarionActionCts = new CancellationTokenSource();
        PolarionRefreshProgressText = PolarionText("Preparazione aggiornamento…", "Preparing refresh…", "Préparation de l’actualisation…");
        try
        {
            var progress = new Progress<PolarionSyncProgress>(p =>
            {
                if (!ReferenceEquals(_polarionActionCts, cts) || cts.IsCancellationRequested) return;
                PolarionRefreshProgressText = PolarionText("Ticket elaborati", "Tickets processed", "Tickets traités") +
                    $": {p.Completed}/{p.Total} — {p.TicketId}";
            });
            using var sync = new PolarionBackgroundSyncService(_state, httpFactory: PolarionHttpFactory);
            var result = await Task.Run(() => sync.RefreshOnceAsync(force: true, token: cts.Token, progress: progress));
            var unprocessed = Math.Max(0, result.Total - result.Refreshed - result.Failed - result.Skipped);
            var counts = PolarionText("Salvati", "Saved", "Enregistrés") + $": {result.Refreshed}; " +
                PolarionText("cache mantenuta", "cache retained", "cache conservé") + $": {result.Skipped}; " +
                PolarionText("errori", "errors", "erreurs") + $": {result.Failed}; " +
                PolarionText("non elaborati", "not processed", "non traités") + $": {unprocessed}; " +
                PolarionText("totale", "total", "total") + $": {result.Total}.";
            var message = result.Status switch
            {
                PolarionSyncStatus.Success => PolarionText("Aggiornamento TICKET completato.", "TICKET refresh completed.", "Actualisation TICKET terminée.") + " " + counts,
                PolarionSyncStatus.NoTickets => PolarionText("Nessun ticket TICKET collegato da aggiornare.", "No linked TICKET tickets to refresh.", "Aucun ticket TICKET lié à actualiser."),
                PolarionSyncStatus.NotConfigured => PolarionText("Configurazione TICKET salvata incompleta o lettura disabilitata.", "Saved TICKET configuration is incomplete or reading is disabled.", "Configuration TICKET enregistrée incomplète ou lecture désactivée."),
                PolarionSyncStatus.WorkspaceUnavailable => PolarionText("Workspace non disponibile per l’aggiornamento.", "Workspace unavailable for refresh.", "Espace de travail indisponible pour l’actualisation."),
                PolarionSyncStatus.Deferred => PolarionText("Aggiornamento rinviato.", "Refresh deferred.", "Actualisation reportée."),
                _ => PolarionText("Aggiornamento incompleto.", "Refresh incomplete.", "Actualisation incomplète.") +
                    " " + counts + " " + PolarionHttpError(result.HttpStatus)
            };
            SetPolarionFeedback(message, result.Status is not (PolarionSyncStatus.Success or PolarionSyncStatus.NoTickets));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetPolarionFeedback(PolarionText("Aggiornamento annullato.", "Refresh cancelled.", "Actualisation annulée."), true);
        }
        catch (Exception) { SetPolarionFeedback(PolarionText("Aggiornamento TICKET non disponibile.", "TICKET refresh unavailable.", "Actualisation TICKET indisponible."), true); }
        finally { _polarionActionCts = null; PolarionRefreshProgressText = ""; PolarionRefreshInCorso = false; }
    }

    [RelayCommand]
    private void AnnullaPolarion() => _polarionActionCts?.Cancel();

    private static string PolarionHttpError(System.Net.HttpStatusCode? status) => status switch
    {
        System.Net.HttpStatusCode.Unauthorized => PolarionText("Autenticazione rifiutata (401).", "Authentication rejected (401).", "Authentification refusée (401)."),
        System.Net.HttpStatusCode.Forbidden => PolarionText("Accesso negato (403).", "Access denied (403).", "Accès refusé (403)."),
        System.Net.HttpStatusCode.NotFound => PolarionText("Progetto o endpoint non trovato (404).", "Project or endpoint not found (404).", "Projet ou point d’accès introuvable (404)."),
        System.Net.HttpStatusCode.TooManyRequests => PolarionText("Limite richieste raggiunto (429).", "Request limit reached (429).", "Limite de requêtes atteinte (429)."),
        _ when status.HasValue && (int)status.Value >= 500 => PolarionText("Errore del server Ticketing.", "Ticketing server error.", "Erreur du serveur Ticketing."),
        _ => PolarionText("Controllare rete, configurazione e cache.", "Check network, configuration and cache.", "Vérifiez le réseau, la configuration et le cache.")
    };

    [RelayCommand]
    private async Task TestaConnessionePolarionAsync()
    {
        if (PolarionRefreshInCorso || !_state.CartellaValida || _state.OperatoreCorrente is null) return;
        if (PolarionApiHasUnsavedChanges)
        {
            SetPolarionFeedback(PolarionText("Salva prima le modifiche TICKET.", "Save TICKET changes first.", "Enregistrez d’abord les modifications TICKET."), true);
            return;
        }
        PolarionApiSettings settings;
        try { settings = PolarionApiSettingsService.Load(_state.Paths); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetPolarionFeedback(PolarionText("Configurazione TICKET non leggibile.", "TICKET configuration cannot be read.", "La configuration TICKET ne peut pas être lue."), true);
            return;
        }
        if (settings.ErroreCaricamento is not null || !settings.LetturaAbilitata || !settings.HaToken)
        {
            SetPolarionFeedback(PolarionText("Salva una configurazione TICKET valida e abilita la lettura prima del test.", "Save a valid TICKET configuration and enable reading before testing.", "Enregistrez une configuration TICKET valide et activez la lecture avant le test."), true);
            return;
        }
        PolarionRefreshInCorso = true;
        using var cts = _polarionActionCts = new CancellationTokenSource();
        PolarionRefreshProgressText = PolarionText("Verifica in lettura…", "Testing read access…", "Vérification de l’accès en lecture…");
        try
        {
            using var http = PolarionHttpFactory();
            var client = new PolarionReadClient(http, new Uri(settings.ApiRoot, UriKind.Absolute), settings.LeggiToken());
            await client.ProbeProjectAsync(settings.ProjectId, cts.Token);
            SetPolarionFeedback(PolarionText("Connessione e lettura progetto riuscite.", "Connection and project read succeeded.", "Connexion et lecture du projet réussies."), false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { SetPolarionFeedback(PolarionText("Test annullato.", "Test cancelled.", "Test annulé."), true); }
        catch (HttpRequestException ex) { SetPolarionFeedback(PolarionHttpError(ex.StatusCode), true); }
        catch (OperationCanceledException) { SetPolarionFeedback(PolarionText("Tempo di connessione scaduto.", "Connection timed out.", "Délai de connexion dépassé."), true); }
        catch (Exception) { SetPolarionFeedback(PolarionText("Risposta non valida o connessione non disponibile.", "Invalid response or connection unavailable.", "Réponse invalide ou connexion indisponible."), true); }
        finally { _polarionActionCts = null; PolarionRefreshProgressText = ""; PolarionRefreshInCorso = false; }
    }

    public ObservableCollection<FiltroOpzione> PolarionGammeNotifiche { get; } = new();
    public ObservableCollection<FiltroOpzione> PolarionVeicoliNotifiche { get; } = new();
    private bool _polarionIncludiNonAssociati;
    public bool PolarionIncludiNonAssociati
    {
        get => _polarionIncludiNonAssociati;
        set => SetProperty(ref _polarionIncludiNonAssociati, value);
    }

    private void CaricaFiltriNotifichePolarion()
    {
        PolarionGammeNotifiche.Clear();
        PolarionVeicoliNotifiche.Clear();
        if (!_state.CartellaValida || _state.OperatoreCorrente is null) return;
        try
        {
            var prefs = new PolarionNotificationPreferencesService(_state.Paths, _state.Config.OperatoreAttivo).Load();
            PolarionIncludiNonAssociati = prefs.IncludiNonAssociati;
            var gamme = _state.Master.Gamma.Load().Where(x => !x.Archiviata)
                .Select(x => x.Nome).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            foreach (var gamma in gamme)
                PolarionGammeNotifiche.Add(new FiltroOpzione
                {
                    Valore = gamma, Etichetta = gamma,
                    Selezionato = prefs.Gamme.Contains(gamma, StringComparer.OrdinalIgnoreCase)
                });
            var veicoli = _state.Master.Veicoli.Load().Where(x => !x.Archiviata)
                .Select(x => x.Nome).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            foreach (var veicolo in veicoli)
                PolarionVeicoliNotifiche.Add(new FiltroOpzione
                {
                    Valore = veicolo, Etichetta = veicolo,
                    Selezionato = prefs.Veicoli.Contains(veicolo, StringComparer.OrdinalIgnoreCase)
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessaggioErrore = LocalizationService.Instance.Scegli("Filtri TICKET locali non disponibili",
                "Local TICKET filters unavailable", "Filtres TICKET locaux indisponibles");
        }
    }

    [RelayCommand]
    private void SalvaFiltriNotifichePolarion()
    {
        if (!_state.CartellaValida || _state.OperatoreCorrente is null) return;
        try
        {
            new PolarionNotificationPreferencesService(_state.Paths, _state.Config.OperatoreAttivo)
                .Save(new PolarionNotificationPreferences
                {
                    Gamme = PolarionGammeNotifiche.Where(x => x.Selezionato).Select(x => x.Valore).ToList(),
                    Veicoli = PolarionVeicoliNotifiche.Where(x => x.Selezionato).Select(x => x.Valore).ToList(),
                    IncludiNonAssociati = PolarionIncludiNonAssociati
                });
            MessaggioErrore = null;
            Messaggio = LocalizationService.Instance.Scegli("Filtri notifiche TICKET salvati sul PC",
                "TICKET notification filters saved locally", "Filtres de notification TICKET enregistrés localement");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Messaggio = null;
            MessaggioErrore = ex.Message;
        }
    }

    // Dedicated API settings; independent of the manual Ticketing export sections above.
    private PolarionApiSettings _polarionApiLoaded = new();
    private string _polarionApiRoot = "";
    private string _polarionApiNewToken = "";
    private bool _polarionApiReadEnabled;
    private string _polarionBrowserCreateUrl = "";
    private string _polarionAssigneeOne = "";
    private string _polarionAssigneeTwo = "";
    public string PolarionBrowserCreateUrl
    {
        get => _polarionBrowserCreateUrl;
        set { if (SetProperty(ref _polarionBrowserCreateUrl, value ?? "")) OnPropertyChanged(nameof(PolarionApiPendingStatus)); }
    }
    public string PolarionAssigneeOne
    {
        get => _polarionAssigneeOne;
        set { if (SetProperty(ref _polarionAssigneeOne, value ?? "")) OnPropertyChanged(nameof(PolarionApiPendingStatus)); }
    }
    public string PolarionAssigneeTwo
    {
        get => _polarionAssigneeTwo;
        set { if (SetProperty(ref _polarionAssigneeTwo, value ?? "")) OnPropertyChanged(nameof(PolarionApiPendingStatus)); }
    }
    /// <summary>Legacy UI compatibility; the REST write switch cannot be enabled.</summary>
    public bool PolarionApiWriteEnabled { get => false; set { } }
    public string PolarionApiRoot
    {
        get => _polarionApiRoot;
        set { if (SetProperty(ref _polarionApiRoot, value ?? "")) OnPropertyChanged(nameof(PolarionApiPendingStatus)); }
    }
    /// <summary>Never initialized with the current token: replacing it requires explicit Master input.</summary>
    public string PolarionApiNewToken
    {
        get => _polarionApiNewToken;
        set { if (SetProperty(ref _polarionApiNewToken, value ?? "")) { OnPropertyChanged(nameof(PolarionApiTokenIndicator)); OnPropertyChanged(nameof(PolarionApiPendingStatus)); } }
    }
    public bool PolarionApiReadEnabled
    {
        get => _polarionApiReadEnabled;
        set { if (SetProperty(ref _polarionApiReadEnabled, value)) OnPropertyChanged(nameof(PolarionApiPendingStatus)); }
    }
    public string PolarionApiTokenIndicator => _polarionApiLoaded.ErroreCaricamento is not null
        ? PolarionText("Stato token non disponibile: configurazione illeggibile.", "Token status unavailable: configuration unreadable.", "État du jeton indisponible : configuration illisible.")
        : !string.IsNullOrWhiteSpace(PolarionApiNewToken)
            ? PolarionText("Nuovo token inserito: non ancora salvato.", "New token entered: not saved yet.", "Nouveau jeton saisi : pas encore enregistré.")
            : _polarionApiLoaded.HaToken
                ? PolarionText("Token salvato; il valore non viene mostrato.", "Token saved; its value is not displayed.", "Jeton enregistré ; sa valeur n’est pas affichée.")
                : PolarionText("Token non configurato.", "Token not configured.", "Jeton non configuré.");

    public bool PolarionApiHasUnsavedChanges =>
        !string.IsNullOrWhiteSpace(PolarionApiNewToken) ||
        !string.Equals(PolarionApiRoot.Trim(), _polarionApiLoaded.ApiRoot, StringComparison.Ordinal) ||
        PolarionApiReadEnabled != _polarionApiLoaded.LetturaAbilitata ||
        PolarionApiWriteEnabled != _polarionApiLoaded.ScritturaAbilitata ||
        !string.Equals(PolarionBrowserCreateUrl.Trim(), _polarionApiLoaded.BrowserCreateUrl, StringComparison.Ordinal) ||
        !string.Equals(PolarionAssigneeOne.Trim(), _polarionApiLoaded.AssegnatariEsta.ElementAtOrDefault(0) ?? "", StringComparison.Ordinal) ||
        !string.Equals(PolarionAssigneeTwo.Trim(), _polarionApiLoaded.AssegnatariEsta.ElementAtOrDefault(1) ?? "", StringComparison.Ordinal);

    public string PolarionApiPendingStatus => PolarionApiHasUnsavedChanges
            ? PolarionText("Modifiche TICKET non salvate: salva prima di verificare o aggiornare.", "TICKET changes are unsaved: save before testing or refreshing.", "Modifications TICKET non enregistrées : enregistrez avant le test ou l’actualisation.")
            : _polarionApiLoaded.ErroreCaricamento is not null
                ? PolarionText("Configurazione TICKET illeggibile.", "TICKET configuration unreadable.", "Configuration TICKET illisible.")
                : string.IsNullOrWhiteSpace(_polarionApiLoaded.ApiRoot) || !_polarionApiLoaded.HaToken || !_polarionApiLoaded.LetturaAbilitata
                    ? PolarionText("Configurazione TICKET salvata incompleta o lettura disabilitata.", "Saved TICKET configuration is incomplete or reading is disabled.", "Configuration TICKET enregistrée incomplète ou lecture désactivée.")
                    : PolarionText("Configurazione TICKET salvata.", "TICKET configuration saved.", "Configuration TICKET enregistrée.");

    private void CaricaConfigurazionePolarionApi()
    {
        _polarionApiLoaded = _state.CartellaValida ? PolarionApiSettingsService.Load(_state.Paths) : new PolarionApiSettings();
        PolarionApiRoot = _polarionApiLoaded.ApiRoot;
        PolarionApiReadEnabled = _polarionApiLoaded.LetturaAbilitata;
        PolarionApiWriteEnabled = _polarionApiLoaded.ScritturaAbilitata;
        PolarionBrowserCreateUrl = _polarionApiLoaded.BrowserCreateUrl;
        PolarionAssigneeOne = _polarionApiLoaded.AssegnatariEsta.ElementAtOrDefault(0) ?? "";
        PolarionAssigneeTwo = _polarionApiLoaded.AssegnatariEsta.ElementAtOrDefault(1) ?? "";
        PolarionApiNewToken = "";
        OnPropertyChanged(nameof(PolarionApiTokenIndicator));
        OnPropertyChanged(nameof(PolarionApiPendingStatus));
        if (_polarionApiLoaded.ErroreCaricamento is not null)
            SetPolarionFeedback(PolarionText("Configurazione TICKET illeggibile; nessuna modifica è stata salvata.", "TICKET configuration unreadable; no changes were saved.", "Configuration TICKET illisible ; aucune modification n’a été enregistrée."), true);
    }

    [RelayCommand]
    private void SalvaConfigurazionePolarionApi()
    {
        if (!EMaster || !_state.CartellaValida) return;
        try
        {
            // Preserve the loaded revision/fingerprint; never re-read a newer configuration and
            // silently overwrite it with this dialog's stale values.
            var settings = new PolarionApiSettings
            {
                RevisioneDati = _polarionApiLoaded.RevisioneDati,
                SourceFingerprint = _polarionApiLoaded.SourceFingerprint,
                ProjectId = _polarionApiLoaded.ProjectId,
                IntervalloMinuti = _polarionApiLoaded.IntervalloMinuti,
                AutomazioniSospese = _polarionApiLoaded.AutomazioniSospese,
                ApiRoot = PolarionApiRoot.Trim(),
                LetturaAbilitata = PolarionApiReadEnabled,
                ScritturaAbilitata = false,
                TokenOffuscato = _polarionApiLoaded.TokenOffuscato,
                BrowserCreateUrl = PolarionBrowserCreateUrl.Trim(),
                AssegnatariEsta = new[] { PolarionAssigneeOne, PolarionAssigneeTwo }
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList()
            };
            if (!string.IsNullOrWhiteSpace(PolarionApiNewToken))
                settings.ImpostaToken(PolarionApiNewToken);
            PolarionApiSettingsService.Save(_state.Paths, settings, _state.EMaster);
            _polarionApiLoaded = settings;
            PolarionApiNewToken = "";
            OnPropertyChanged(nameof(PolarionApiTokenIndicator));
            OnPropertyChanged(nameof(PolarionApiPendingStatus));
            SetPolarionFeedback(PolarionText("Impostazioni API TICKET salvate.", "TICKET API settings saved.", "Paramètres API TICKET enregistrés."), false);
        }
        catch (Exception ex)
        {
            // Do not display exception messages: paths or external input may contain credentials.
            SetPolarionFeedback(ex is ConflittoRevisioneException
                ? PolarionText("Configurazione modificata su un’altra postazione: ricarica prima di salvare.", "Configuration changed on another PC: reload before saving.", "Configuration modifiée sur un autre PC : rechargez avant d’enregistrer.")
                : PolarionText("Salvataggio non riuscito: verifica URL HTTPS, token e permessi del workspace.", "Save failed: check HTTPS URL, token and workspace permissions.", "Échec de l’enregistrement : vérifiez l’URL HTTPS, le jeton et les autorisations."), true);
        }
    }

    /// <summary>Modalità di accesso (piano 6.4.2, §4b, decisione utente 2026-09-28): impostazione UNICA per
    /// tutta la cartella dati, modificabile solo da un Master. Stesso schema di CaricaSezioniPolarion/
    /// SalvaSezioniPolarion sopra: fotografia (revisione+fingerprint) caricata qui, salvata in modo ottimistico.</summary>
    private long _accessoRevisioneAttesa;
    private string? _accessoFingerprintAtteso;
    private bool _accessoSenzaPinSelezionato;

    public bool AccessoSenzaPinSelezionato
    {
        get => _accessoSenzaPinSelezionato;
        set { if (SetProperty(ref _accessoSenzaPinSelezionato, value)) OnPropertyChanged(nameof(AccessoConPinSelezionato)); }
    }

    /// <summary>Comodità per il RadioButton "Accesso con PIN" (i due si escludono a vicenda sullo stesso
    /// gruppo logico, senza bisogno di un converter enum->bool dedicato).</summary>
    public bool AccessoConPinSelezionato
    {
        get => !_accessoSenzaPinSelezionato;
        set => AccessoSenzaPinSelezionato = !value;
    }

    /// <summary>Vero se il file esiste ma il suo sigillo non è (più) valido: il valore letto è già ricaduto su
    /// "con PIN" (RuoloSigilloService.SenzaPinEffettivo), questo serve solo a mostrare l'avviso esplicito
    /// "manomesso" invece di far sembrare che qualcuno abbia semplicemente scelto "con PIN".</summary>
    public bool ModalitaAccessoManomessa { get; private set; }

    private void CaricaModalitaAccesso()
    {
        var config = _state.CartellaValida ? AccessModeConfigService.Load(_state.Paths) : new AccessModeConfig();
        _accessoRevisioneAttesa = config.RevisioneDati;
        _accessoFingerprintAtteso = config.SourceFingerprint;
        ModalitaAccessoManomessa = config.SenzaPin && !RuoloSigilloService.SenzaPinEffettivo(config);
        _accessoSenzaPinSelezionato = RuoloSigilloService.SenzaPinEffettivo(config);
        OnPropertyChanged(nameof(AccessoSenzaPinSelezionato));
        OnPropertyChanged(nameof(AccessoConPinSelezionato));
        OnPropertyChanged(nameof(ModalitaAccessoManomessa));
        CaricaPoliticaDurata();
    }

    // ---- TIME (6.6.1): politica condivisa "durata obbligatoria" per le operazioni eseguite. Modifica solo Master. ----
    private long _durataRevisioneAttesa;
    private string? _durataFingerprintAtteso;
    private bool _durataObbligatoriaSelezionata;

    public bool DurataObbligatoriaSelezionata
    {
        get => _durataObbligatoriaSelezionata;
        set => SetProperty(ref _durataObbligatoriaSelezionata, value);
    }

    /// <summary>Messaggio di un file della politica illeggibile: il salvataggio resta bloccato finche' il file non e' riparato.</summary>
    public string? ErrorePoliticaDurata { get; private set; }

    public bool HaErrorePoliticaDurata => ErrorePoliticaDurata != null;

    private void CaricaPoliticaDurata()
    {
        var politica = _state.CartellaValida ? PoliticaDurataService.Load(_state.Paths) : new PoliticaDurataConfig();
        _durataRevisioneAttesa = politica.RevisioneDati;
        _durataFingerprintAtteso = politica.SourceFingerprint;
        _durataObbligatoriaSelezionata = politica.DurataObbligatoria && politica.ErroreCaricamento == null;
        ErrorePoliticaDurata = politica.ErroreCaricamento;
        OnPropertyChanged(nameof(DurataObbligatoriaSelezionata));
        OnPropertyChanged(nameof(ErrorePoliticaDurata));
        OnPropertyChanged(nameof(HaErrorePoliticaDurata));
    }

    [RelayCommand]
    private void SalvaPoliticaDurata()
    {
        if (!EMaster || !_state.CartellaValida) return;
        var politica = new PoliticaDurataConfig
        {
            RevisioneDati = _durataRevisioneAttesa,
            SourceFingerprint = _durataFingerprintAtteso,
            DurataObbligatoria = DurataObbligatoriaSelezionata,
        };
        try
        {
            PoliticaDurataService.Save(_state.Paths, politica, _state.EMaster);
            _durataRevisioneAttesa = politica.RevisioneDati;
            _durataFingerprintAtteso = politica.SourceFingerprint;
            ErrorePoliticaDurata = null;
            OnPropertyChanged(nameof(ErrorePoliticaDurata));
            OnPropertyChanged(nameof(HaErrorePoliticaDurata));
            _state.RicaricaTutto();
            _state.RegistraAudit("CambioPoliticaDurata", "workspace", DurataObbligatoriaSelezionata ? "Obbligatoria" : "Facoltativa");
            Messaggio = LocalizationService.Instance.Traduci("PoliticaDurataSalvata");
            MessaggioErrore = null;
        }
        catch (Exception ex)
        {
            Messaggio = null;
            MessaggioErrore = ex.Message;
        }
    }

    [RelayCommand]
    private void SalvaModalitaAccesso()
    {
        if (!EMaster || !_state.CartellaValida) return;
        var config = new AccessModeConfig
        {
            RevisioneDati = _accessoRevisioneAttesa,
            SourceFingerprint = _accessoFingerprintAtteso,
            SenzaPin = AccessoSenzaPinSelezionato
        };
        RuoloSigilloService.SigillaModalitaAccesso(config);
        try
        {
            AccessModeConfigService.Save(_state.Paths, config, _state.EMaster);
            _accessoRevisioneAttesa = config.RevisioneDati;
            _accessoFingerprintAtteso = config.SourceFingerprint;
            _state.RicaricaTutto();
            ModalitaAccessoManomessa = false;
            OnPropertyChanged(nameof(ModalitaAccessoManomessa));
            _state.RegistraAudit("CambioModalitaAccesso", "workspace", AccessoSenzaPinSelezionato ? "SenzaPin" : "ConPin");
            Messaggio = LocalizationService.Instance.Traduci("ModalitaAccessoSalvataMsg");
            MessaggioErrore = null;
        }
        catch (Exception ex)
        {
            Messaggio = null;
            MessaggioErrore = ex.Message;
        }
    }

    [RelayCommand]
    private void ForzaSincronizzazione()
    {
        _state.RicaricaTutto();
        CaricaSezioniPolarion();
        CaricaConfigurazionePolarionApi();
        CaricaFiltriNotifichePolarion();
        CaricaModalitaAccesso();
        // RicaricaTutto aggiorna le repliche LOCALI; non invia alcun comando OneDrive
        // e non puo certificare che gli altri PC abbiano ricevuto i file.
        Messaggio = EModalitaRete
            ? LocalizationService.Instance.Scegli(
                "Dati locali riletti e controllati; sincronizzazione della cartella di rete NON verificata.",
                "Local data reloaded and checked; Shared folder synchronization has NOT been verified.",
                "Données locales relues et vérifiées ; la synchronisation du dossier partagé n'a PAS été vérifiée.")
            : LocalizationService.Instance.Scegli(
                "Dati locali riletti e controllati; sincronizzazione cloud OneDrive NON verificata.",
                "Local data reloaded and checked; OneDrive cloud synchronization has NOT been verified.",
                "Données locales relues et vérifiées ; la synchronisation cloud OneDrive n'a PAS été vérifiée.");
        MessaggioErrore = null;
        OnPropertyChanged(nameof(SincronizzazioneOk));
        OnPropertyChanged(nameof(StatoSincronizzazioneTesto));
        OnPropertyChanged(nameof(UltimaVerificaSincronizzazioneTesto));
        OnPropertyChanged(nameof(CartellaValida));
        OnPropertyChanged(nameof(NumeroFileConProblemi));
        OnPropertyChanged(nameof(HaFileConProblemi));
        OnPropertyChanged(nameof(FileConProblemi));
        OnPropertyChanged(nameof(NumeroDuplicatiId));
        OnPropertyChanged(nameof(HaDuplicatiId));
        OnPropertyChanged(nameof(DuplicatiId));
        OnPropertyChanged(nameof(NumeroCopieDiConflitto));
        OnPropertyChanged(nameof(HaCopieDiConflitto));
        OnPropertyChanged(nameof(CopieDiConflitto));
        _onCambiato();
    }

    public bool HaDuplicatiId => NumeroDuplicatiId > 0;
    public int NumeroDuplicatiId => _state.CartellaValida ? _state.TestCaseDuplicati().Count : 0;
    public List<TestCaseDuplicato> DuplicatiId => _state.CartellaValida ? _state.TestCaseDuplicati().ToList() : new();

    /// <summary>Copie di conflitto OneDrive/SharePoint (fix revisione release/6.4.2: prima calcolate dal
    /// repository ma mai lette da nessuna ViewModel/XAML, quindi invisibili all'utente — un Master non aveva
    /// modo di sapere che esistevano file "da verificare" con contenuto diverso dall'originale). "Ignorati" è
    /// solo di sessione (nessuna persistenza): riappaiono al prossimo avvio/refresh se ancora presenti sul
    /// disco, cosi' un Master non perde la visibilità solo perché un altro operatore li aveva chiusi.</summary>
    private readonly HashSet<string> _copieDiConflittoIgnorate = new(StringComparer.OrdinalIgnoreCase);
    public bool HaCopieDiConflitto => NumeroCopieDiConflitto > 0;
    public int NumeroCopieDiConflitto => CopieDiConflitto.Count;
    public List<Models.FileConProblemi> CopieDiConflitto => _state.CartellaValida
        ? _state.TutteLeCopieDiConflitto().Where(c => !_copieDiConflittoIgnorate.Contains(c.Percorso)).ToList()
        : new();

    [RelayCommand]
    private void IgnoraCopiaDiConflitto(Models.FileConProblemi? file)
    {
        if (file == null) return;
        _copieDiConflittoIgnorate.Add(file.Percorso);
        OnPropertyChanged(nameof(NumeroCopieDiConflitto));
        OnPropertyChanged(nameof(HaCopieDiConflitto));
        OnPropertyChanged(nameof(CopieDiConflitto));
    }

    public bool EMaster => _state.EMaster;
    public string RuoloAttivoTesto => _state.OperatoreCorrente?.Ruolo.Label() ?? "";
    public bool AvvioRidotto { get => _state.Config.AvvioRidotto; set { _state.Config.AvvioRidotto = value; OnPropertyChanged(); } }
    public bool NotificheWindows { get => _state.Config.NotificheWindows; set { _state.Config.NotificheWindows = value; OnPropertyChanged(); } }

    private IReadOnlyList<VoceNotificaContatore>? _tipiNotificaContatore;

    /// <summary>Un checkbox per tipo di notifica ("Mostra nel contatore"). Scrive nella configurazione in memoria: il salvataggio
    /// è quello di "Salva preferenze". Il conflitto di sincronizzazione dati è sempre incluso e non disattivabile.</summary>
    public IReadOnlyList<VoceNotificaContatore> TipiNotificaContatore => _tipiNotificaContatore ??= NotificaPreferenzeService.TipiNoti
        .Select(t => new VoceNotificaContatore(
            t.Tipo,
            t.Etichetta,
            mostra: !NotificaPreferenzeService.EStaSilenziato(t.Tipo, _state.Config),
            modificabile: NotificaPreferenzeService.ESilenziabile(t.Tipo),
            onMostraCambiato: (tipo, mostra) => NotificaPreferenzeService.ImpostaMostraNelContatore(_state.Config, tipo, mostra)))
        .ToList();

    private IReadOnlyList<OpzioneModoRiepilogo>? _opzioniModoRiepilogo;

    /// <summary>Voci del menu "Riepilogo notifiche" (6.6.0). Scrive nella configurazione in memoria: il salvataggio è quello di "Salva preferenze".</summary>
    public IReadOnlyList<OpzioneModoRiepilogo> OpzioniModoRiepilogo => _opzioniModoRiepilogo ??= new[]
    {
        new OpzioneModoRiepilogo(NotificaRiepilogoService.ModoRiepilogo.Disattivato, LocalizationService.Instance.Traduci("RiepilogoModoDisattivato")),
        new OpzioneModoRiepilogo(NotificaRiepilogoService.ModoRiepilogo.Giornaliero, LocalizationService.Instance.Traduci("RiepilogoModoGiornaliero")),
        new OpzioneModoRiepilogo(NotificaRiepilogoService.ModoRiepilogo.AllAvvio, LocalizationService.Instance.Traduci("RiepilogoModoAllAvvio")),
    };

    /// <summary>Modo selezionato per il riepilogo notifiche.</summary>
    public OpzioneModoRiepilogo? ModoRiepilogoSelezionato
    {
        get => OpzioniModoRiepilogo.FirstOrDefault(o => o.Modo == NotificaRiepilogoService.Modo(_state.Config));
        set
        {
            if (value is null) return;
            NotificaRiepilogoService.ImpostaModo(_state.Config, value.Modo);
            OnPropertyChanged();
        }
    }

    private IReadOnlyList<VoceRiepilogoTipo>? _tipiRiepilogoNotifiche;

    /// <summary>Un checkbox per tipo: "Include nel riepilogo". I tipi che non entrano mai nel riepilogo restano visibili ma disabilitati.</summary>
    public IReadOnlyList<VoceRiepilogoTipo> TipiRiepilogoNotifiche => _tipiRiepilogoNotifiche ??= NotificaPreferenzeService.TipiNoti
        .Select(t => new VoceRiepilogoTipo(
            t.Tipo,
            t.Etichetta,
            incluso: !NotificaRiepilogoService.ESempreImmediato(t.Tipo) && !NotificaRiepilogoService.EEsclusoNelRiepilogo(t.Tipo, _state.Config),
            modificabile: !NotificaRiepilogoService.ESempreImmediato(t.Tipo),
            onInclusoCambiato: (tipo, incluso) => NotificaRiepilogoService.ImpostaIncluso(_state.Config, tipo, incluso)))
        .ToList();

    /// <summary>A differenza di AvvioRidotto/NotificheWindows, cambia ed effettua subito il salvataggio: la
    /// lingua deve avere effetto immediato sulla UI (LocalizationService.Instance), non restare in sospeso
    /// finché qualcuno non preme "Salva preferenze".</summary>
    public bool EItaliano
    {
        get => _state.Config.Lingua == "it";
        set { if (value) ImpostaLingua("it"); }
    }

    public bool EInglese
    {
        get => _state.Config.Lingua == "en";
        set { if (value) ImpostaLingua("en"); }
    }

    public bool EFrancese
    {
        get => _state.Config.Lingua == "fr";
        set { if (value) ImpostaLingua("fr"); }
    }

    private void ImpostaLingua(string nuova)
    {
        if (_state.Config.Lingua == nuova) return;
        _state.Config.Lingua = nuova;
        LocalizationService.Instance.Lingua = nuova;
        _state.ConfigService.Save(_state.Config);
        foreach (var opzione in PolarionSezioniOpzioni)
            opzione.Etichetta = LocalizationService.Instance.Traduci("PolarionSezione" + opzione.Valore);
        OnPropertyChanged();
        OnPropertyChanged(nameof(StatoSincronizzazioneTesto));
        OnPropertyChanged(nameof(UltimaVerificaSincronizzazioneTesto));
        OnPropertyChanged(nameof(RuoloAttivoTesto));
        OnPropertyChanged(nameof(UltimaManutenzioneStorageTesto));
        OnPropertyChanged(nameof(EItaliano));
        OnPropertyChanged(nameof(EInglese));
        OnPropertyChanged(nameof(EFrancese));
        OnPropertyChanged(nameof(EtichettaVerificaLocale));
        _tipiNotificaContatore = null;
        _tipiRiepilogoNotifiche = null;
        OnPropertyChanged(nameof(TipiNotificaContatore));
        OnPropertyChanged(nameof(TipiRiepilogoNotifiche));
        NotificaForkProperties();
    }

    public ObservableCollection<NavItem> SottoSezioni { get; }

    /// <summary>Barra di navigazione principale (Addendum: grill-me 2026-08-18, follow-up): riferimento
    /// passato da MainViewModel così i comandi di riordino qui sotto possono spostarne le voci in-place,
    /// senza che MainViewModel debba ricostruirsi per riflettere il nuovo ordine.</summary>
    public ObservableCollection<NavItem>? NavPrincipale { get; }

    [ObservableProperty] private NavItem? sottoSezioneSelezionata;
    [ObservableProperty] private ViewModelBase? paginaCorrente;
    public bool EGenerale => SottoSezioneSelezionata?.Id is null or "generale";

    private NavItem? _sottoSezioneCorrente;
    private bool _sopprimiNav;

    partial void OnMessaggioChanged(string? value) => OnPropertyChanged(nameof(HaMessaggio));
    partial void OnMessaggioErroreChanged(string? value) => OnPropertyChanged(nameof(HaMessaggioErrore));

    /// <summary>Come per la barra laterale principale (Addendum §18): cambiare tabella con modifiche in corso
    /// nell'editor corrente scartava tutto senza avviso, perché qui si ricrea semplicemente un ViewModel
    /// nuovo al volo. Si ripristina la voce precedente e si completa il cambio solo dopo conferma — il
    /// ripristino è rimandato al prossimo ciclo della UI perché farlo subito, dentro lo stesso handler
    /// scatenato dal click, veniva sovrascritto dalla sincronizzazione interna di selezione del ListBox.</summary>
    partial void OnSottoSezioneSelezionataChanged(NavItem? value)
    {
        if (_sopprimiNav) { _sottoSezioneCorrente = value; OnPropertyChanged(nameof(EGenerale)); return; }
        if (value == null) return;

        if (PaginaCorrente is IEditorConModifiche editor && editor.HaModificheNonSalvate())
        {
            var destinazione = value;
            var precedente = _sottoSezioneCorrente;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ImpostaSenzaRicaricare(precedente));
            _ = CambiaDopoConferma(destinazione);
            return;
        }

        _sottoSezioneCorrente = value;
        OnPropertyChanged(nameof(EGenerale));
        SostituisciPaginaCorrente(value);
    }

    private async Task CambiaDopoConferma(NavItem destinazione)
    {
        if (!await ConfirmService.ConfermaAbbandonoModifiche()) return;
        ImpostaSenzaRicaricare(destinazione);
        OnPropertyChanged(nameof(EGenerale));
        SostituisciPaginaCorrente(destinazione);
    }

    /// <summary>Rilascia la sottoscrizione a DatiRicaricati (vedi MasterDataEditorViewModelBase.Dispose) della
    /// pagina abbandonata prima di crearne una nuova — senza, ogni sotto-sezione visitata resterebbe agganciata
    /// per sempre a AppState.DatiRicaricati, un leak piccolo ma inutile dato che qui si ricrea sempre un
    /// ViewModel nuovo ad ogni navigazione invece di riusare quello vecchio. "Generale" è this stesso (Crea
    /// per quell'Id ritorna semplicemente (ViewModelBase)this) e questa classe implementa anch'essa IDisposable
    /// (per il caso in cui MainViewModel dismetta l'intero hub), quindi va esclusa esplicitamente col
    /// ReferenceEquals — senza, disposabile.Dispose() richiamerebbe se stesso all'infinito (stack overflow).</summary>
    private void SostituisciPaginaCorrente(NavItem item)
    {
        if (PaginaCorrente is IDisposable disposabile && !ReferenceEquals(disposabile, this)) disposabile.Dispose();

        // Una lettura logica sola per l'intera costruzione della sotto-pagina (perf: profilo rivisita
        // Veicoli/Funzionalità 2026-09-25): il ctor di un editor master data puo' leggere piu' di un catalogo
        // (Veicoli + GammeDisponibili/EcuDisponibili + CatalogoCampiSistemaCompleto), ognuno dei quali passerebbe
        // altrimenti da un proprio giro pieno di PhaseDMasterMaterializer.Refresh() sulla STESSA cartella
        // condivisa — stesso I/O di rete ripetuto senza informazione nuova. Dentro MasterLetturaScope il primo
        // Refresh vale per tutti (vedi MasterLetturaScope), i successivi sono cache-hit. Il metodo resta
        // interamente sincrono (nessun await tra Apri() e il Dispose() dello scope), come richiesto da
        // MasterLetturaScope.Apri().
        using (MasterLetturaScope.Apri())
            PaginaCorrente = item.Crea();
    }

    /// <summary>Richiamato da MainViewModel quando si esce dall'intero hub Impostazioni verso un'altra sezione
    /// di primo livello: senza, la sotto-pagina corrente (es. OperatoriViewModel) resterebbe agganciata per
    /// sempre a AppState.DatiRicaricati, stesso principio di SostituisciPaginaCorrente qui sopra.</summary>
    public void Dispose()
    {
        _polarionActionCts?.Cancel();
        if (PaginaCorrente is IDisposable disposabile && !ReferenceEquals(disposabile, this)) disposabile.Dispose();
        _state.BackupGiornalieroStatoCambiato -= _backupGiornalieroStatoCambiato;
        UnitaRete.Classificata -= AlClassificata;
    }

    /// <summary>Esito di una risoluzione di unità in background: la modalità mostrata si rilegge sul thread UI (P4).</summary>
    private void AlClassificata(string _) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(EModalitaRete)));

    private void ImpostaSenzaRicaricare(NavItem? item)
    {
        _sopprimiNav = true;
        SottoSezioneSelezionata = item;
        _sopprimiNav = false;
        _sottoSezioneCorrente = item;
    }

    /// <summary>Punto d'ingresso per i banner cliccabili "File con problemi"/"ID duplicati" di MainWindow
    /// (Addendum: audit UX 2026-08-26, punto 7): prima erano solo testo informativo, l'unico modo per
    /// arrivare alla sezione che li risolve era ricordarsi che vive in fondo a Impostazioni → Generale e
    /// scorrere fin lì a mano. Passa dal setter di SottoSezioneSelezionata (non da un'assegnazione diretta a
    /// PaginaCorrente) perché è l'unico punto che tiene coerenti insieme EGenerale e PaginaCorrente — serve
    /// anche quando l'ordine delle sotto-sezioni scelto dall'utente non ha "Generale" in prima posizione.</summary>
    public void VaiAGenerale()
    {
        var generale = SottoSezioni.FirstOrDefault(s => s.Id == "generale");
        if (generale != null) SottoSezioneSelezionata = generale;
    }

    /// <summary>Arriva sempre dal thread dedicato del backup giornaliero (AppState.BackupGiornalieroStatoCambiato),
    /// mai dal thread UI: il marshalling verso il Dispatcher è responsabilità di questo sottoscrittore, non del
    /// chiamante (stesso contratto di MainViewModel._cambiamentiSuDiscoRilevati).</summary>
    private readonly Action _backupGiornalieroStatoCambiato;

    public ImpostazioniViewModel(AppState state, Action onCambiato, ObservableCollection<NavItem>? navPrincipale = null)
    {
        _state = state;
        _onCambiato = onCambiato;
        NavPrincipale = navPrincipale;
        UnitaRete.Classificata += AlClassificata;
        Cartella = state.Config.CartellaCondivisa;
        var valoreSalvato = ModalitaCondivisioneService.NormalizzaValore(state.Config.ModalitaCartellaCondivisa);
        ModalitaCondivisioneSelezionata = OpzioniModalitaCondivisione.FirstOrDefault(o => o.Valore == valoreSalvato);
        _backupGiornalieroStatoCambiato = () => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => OnPropertyChanged(nameof(BackupGiornalieroStatoTesto)));
        _state.BackupGiornalieroStatoCambiato += _backupGiornalieroStatoCambiato;

        // Il titolo delle 5 sezioni "gemelle" di un ruolo di CampoSistemaDefinizione segue l'Etichetta
        // rinominata dal Master nel catalogo campi di sistema (Addendum: grill-me 2026-08-18, follow-up) —
        // fallback al nome storico se nessun campo porta quel ruolo o il catalogo non è ancora caricabile.
        var catalogoCampi = state.CatalogoCampiSistemaCompleto();
        string Etichetta(RuoloCampoSistema ruolo, string fallback) => RuoloCampoSistemaLookup.EtichettaLocalizzataDelRuolo(catalogoCampi, ruolo, fallback);

        var sottoSezioniDefault = new List<NavItem>
        {
            new() { Id = "generale", Titolo = LocalizationService.Instance.Traduci("NavGenerale"), Icona = "🔩", Crea = () => (ViewModelBase)this },
            new() { Id = "archivio", Titolo = LocalizationService.Instance.Traduci("NavArchivio"), Icona = "📦", Crea = () => new ArchivioViewModel(_state) },
            new() { Id = "operatori", Titolo = LocalizationService.Instance.Traduci("NavOperatori"), Icona = "👤", Crea = () => new OperatoriViewModel(_state) },
            new() { Id = "requisiti", Titolo = LocalizationService.Instance.Traduci("NavRequisiti"), Icona = "📋", Crea = () => new RequisitiViewModel(_state) },
            new() { Id = "requisitid4d", Titolo = LocalizationService.Instance.Traduci("NavRequisitiD4D"), Icona = "🧩", Crea = () => new RequisitiViewModel(_state, TipoRequisito.Extra) },
            new() { Id = "funzionalita", Titolo = Etichetta(RuoloCampoSistema.Funzionalita, LocalizationService.Instance.Traduci("NavFunzionalità")), Icona = "⚙️", Crea = () => new FunzionalitaViewModel(_state) },
            new() { Id = "componente", Titolo = Etichetta(RuoloCampoSistema.Componente, "Componente"), Icona = "🖥️", Crea = () => new EcuViewModel(_state) },
            // Gamma veicolari non è più una sezione a parte (Addendum redesign 2026-08, revisione feedback
            // QA): gestita dentro Veicoli, dove viene comunque scelta per ogni sottoveicolo.
            new() { Id = "veicoli", Titolo = Etichetta(RuoloCampoSistema.Veicolo, LocalizationService.Instance.Traduci("NavVeicoli")), Icona = "🚚", Crea = () => new VeicoliViewModel(_state) },
            new() { Id = "strumenti", Titolo = Etichetta(RuoloCampoSistema.StrumentiUsati, LocalizationService.Instance.Traduci("NavStrumenti")), Icona = "🧰", Crea = () => new StrumentiViewModel(_state) },
            // "Campi Pianificazione"/"Campi Dettagli" non sono più sotto-sezioni separate (Addendum: grill-me
            // 2026-08-18, follow-up — "sposta tutto dentro Tipi di test": la gestione del catalogo campi di
            // sistema si apre ora da dentro l'editor di Tipi di test → Gestisci campi, dove il contesto di
            // "a cosa serve una chiave" è più chiaro perché si vede subito come viene usata in un template.
            new() { Id = "tipiditest", Titolo = LocalizationService.Instance.Traduci("NavTipi di test / CR"), Icona = "🧪", Crea = () => new TestTemplateViewModel(_state) },
        };
        if (state.EMaster)
            sottoSezioniDefault.Insert(2, new NavItem { Id = "recovery", Titolo = LocalizationService.Instance.Traduci("NavRecovery"), Icona = "🛟", Crea = () => new RecoveryBinViewModel(_state) });
        IconeSezioniService.Applica(sottoSezioniDefault, IconeSezioniService.AmbitoImpostazioni, _state.Config);
        SottoSezioni = new ObservableCollection<NavItem>(NavOrderService.Ordina(sottoSezioniDefault, _state.Config.OrdineImpostazioni));
        sottoSezioneSelezionata = SottoSezioni[0];
        paginaCorrente = this;
        CaricaSezioniPolarion();
        CaricaConfigurazionePolarionApi();
        CaricaFiltriNotifichePolarion();
        CaricaModalitaAccesso();
        _ = CaricaConflittiMasterAsync();
    }

    /// <summary>Persiste l'ordine corrente delle due barre (Addendum: grill-me 2026-08-18, follow-up:
    /// "tutti i pannelli sia nella barra delle impostazioni che nella barra della home... devono essere
    /// ordinabili", "Locale per PC/operatore") — stessa AppConfig locale già usata per Lingua/AvvioRidotto.</summary>
    private void SalvaOrdineNav()
    {
        _state.Config.OrdineNavPrincipale = (NavPrincipale ?? new ObservableCollection<NavItem>()).Select(n => n.Id).ToList();
        _state.Config.OrdineImpostazioni = SottoSezioni.Select(n => n.Id).ToList();
        _state.ConfigService.Save(_state.Config);
    }

    [RelayCommand]
    private void SpostaSuNavPrincipale(NavItem item)
    {
        if (NavPrincipale == null) return;
        NavOrderService.Muovi(NavPrincipale, NavPrincipale.IndexOf(item), -1);
        SalvaOrdineNav();
    }

    [RelayCommand]
    private void SpostaGiuNavPrincipale(NavItem item)
    {
        if (NavPrincipale == null) return;
        NavOrderService.Muovi(NavPrincipale, NavPrincipale.IndexOf(item), 1);
        SalvaOrdineNav();
    }

    [RelayCommand]
    private void SpostaSuSottoSezione(NavItem item)
    {
        NavOrderService.Muovi(SottoSezioni, SottoSezioni.IndexOf(item), -1);
        SalvaOrdineNav();
    }

    [RelayCommand]
    private void SpostaGiuSottoSezione(NavItem item)
    {
        NavOrderService.Muovi(SottoSezioni, SottoSezioni.IndexOf(item), 1);
        SalvaOrdineNav();
    }

    // --- Icone di menu e sezioni (personali per PC, AppConfig.IconeSezioni) ---

    public IReadOnlyList<string> GalleriaIcone => IconeSezioniService.Galleria;

    /// <summary>Voci personalizzabili: barra principale + sotto-sezioni di Impostazioni.</summary>
    public IReadOnlyList<NavItem> VociIcone => (NavPrincipale ?? new ObservableCollection<NavItem>()).Concat(SottoSezioni).ToList();

    [ObservableProperty] private NavItem? voceIconaSelezionata;
    [ObservableProperty] private string iconaLibera = "";
    public bool HaVoceIconaSelezionata => VoceIconaSelezionata != null;
    partial void OnVoceIconaSelezionataChanged(NavItem? value)
    {
        IconaLibera = value?.IconaEffettiva ?? "";
        OnPropertyChanged(nameof(HaVoceIconaSelezionata));
    }

    private void ImpostaIcona(string? glifo)
    {
        if (VoceIconaSelezionata == null) return;
        if (!IconeSezioniService.Imposta(_state.Config, VoceIconaSelezionata, glifo))
        {
            Messaggio = null;
            MessaggioErrore = LocalizationService.Instance.Traduci("IconaNonValidaMsg");
            return;
        }
        SalvaIcone();
        IconaLibera = VoceIconaSelezionata.IconaEffettiva;
    }

    private void SalvaIcone()
    {
        try
        {
            _state.ConfigService.Save(_state.Config);
            MessaggioErrore = null;
            Messaggio = LocalizationService.Instance.Traduci("IconeSalvateMsg");
        }
        catch (Exception ex) { Messaggio = null; MessaggioErrore = ex.Message; }
    }

    [RelayCommand] private void ScegliIcona(string? glifo) => ImpostaIcona(glifo);
    [RelayCommand] private void ApplicaIconaLibera() => ImpostaIcona(IconaLibera);

    [RelayCommand]
    private void RipristinaIcona()
    {
        if (VoceIconaSelezionata == null) return;
        IconeSezioniService.Ripristina(_state.Config, VoceIconaSelezionata);
        IconaLibera = VoceIconaSelezionata.IconaEffettiva;
        SalvaIcone();
    }

    [RelayCommand]
    private void RipristinaTutteIcone()
    {
        IconeSezioniService.RipristinaTutte(_state.Config, VociIcone);
        IconaLibera = VoceIconaSelezionata?.IconaEffettiva ?? "";
        SalvaIcone();
    }

    [RelayCommand]
    private async Task CambiaCartella()
    {
        if (string.IsNullOrWhiteSpace(Cartella)) { MessaggioErrore = LocalizationService.Instance.Traduci("PercorsoNonValido"); return; }
        // 6.6.1 (correzione 1): esistenza con tetto di tempo e apertura fuori dal thread UI. Una cartella di rete in stallo non
        // blocca la finestra; scaduto il tetto non si crea né si semina nulla.
        var percorso = Cartella;
        var esito = await Task.Run(() => RadiceCondivisaSonda.Verifica(percorso));
        if (esito is RadiceCondivisaSonda.Esito.Assente or RadiceCondivisaSonda.Esito.Inaccessibile)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("CartellaNonEsiste");
            return;
        }
        if (esito == RadiceCondivisaSonda.Esito.Lenta)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("CartellaNonRispondeFmt"), TimeoutCondivisione.Per(percorso).TotalSeconds);
            return;
        }
        OperazioneCartellaInCorso = true;
        try
        {
            await Task.Run(() => _state.ImpostaCartella(percorso));
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ImpossibileUsareCartellaFmt"), ex.Message);
            return;
        }
        finally
        {
            OperazioneCartellaInCorso = false;
        }
        // Stessa regola dell'avvio (revisione indipendente su fc7e51b, punto 2): il backup giornaliero per la
        // NUOVA cartella parte solo da qui, con il suo ritardo cancellabile, mai dentro ImpostaCartella/
        // InizializzaCartella — non deve competere per I/O con le navigazioni subito dopo il cambio cartella.
        _state.AvviaBackupGiornalieroDopoAvvio();
        CaricaSezioniPolarion();
        CaricaConfigurazionePolarionApi();
        CaricaFiltriNotifichePolarion();
        CaricaModalitaAccesso();
        MessaggioErrore = null;
        Messaggio = LocalizationService.Instance.Traduci("CartellaAggiornata");
        _onCambiato();
    }

    [RelayCommand]
    private void SalvaPreferenzeLocali()
    {
        _state.ConfigService.Save(_state.Config);
        MessaggioErrore = null;
        Messaggio = LocalizationService.Instance.Traduci("PreferenzeLocaliSalvate");
        // Il contatore notifiche dipende dai tipi scelti qui: si ricalcola subito, senza attendere il prossimo refresh.
        _onCambiato();
    }

    /// <summary>Configurazione solo locale di questa postazione: non finisce nel dataset 6.5.3.</summary>
    public string PercorsoBackupEsterno
    {
        get => _state.Config.PercorsoBackupEsterno;
        set
        {
            if (_state.Config.PercorsoBackupEsterno == value) return;
            _state.Config.PercorsoBackupEsterno = value ?? "";
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private bool verificaBackupLocaleInCorso;

    /// <summary>
    /// Verifica lo ZIP locale piu' recente senza estrarlo. Non richiede permessi
    /// di scrittura e funziona anche con il workspace in sola lettura.
    /// Il controllo NON copre gli allegati on-demand e NON certifica OneDrive.
    /// </summary>
    [RelayCommand]
    private async Task VerificaUltimoBackup()
    {
        if (VerificaBackupLocaleInCorso) return;
        VerificaBackupLocaleInCorso = true;
        Messaggio = null;
        MessaggioErrore = null;
        try
        {
            var directory = WorkspaceLocalPaths.GetBackupDirectory(
                _state.Paths.Root, AppState.TestLocalBackupBaseDirectory);
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException(LocalizationService.Instance.Traduci("ImpNessunBackupLocaleMsg"));
            var latest = Directory.EnumerateFiles(directory, "TestLoom_snapshot_*.zip",
                    SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName
                ?? throw new FileNotFoundException(LocalizationService.Instance.Traduci("ImpNessunZipLocaleMsg"));
            var result = await Task.Run(() => BackupArchiveAuditService.Verify(latest));
            if (!result.Valid)
            {
                MessaggioErrore = LocalizationService.Instance.Traduci("ImpVerificaZipNonSuperataPrefisso") + result.Summary +
                    LocalizationService.Instance.Traduci("ImpVerificaZipNonEstrattoSuffisso");
                return;
            }
            Messaggio = string.Format(LocalizationService.Instance.Traduci("ImpBackupLocaleVerificatoFmt"), Path.GetFileName(latest)) +
                result.Summary + " SHA-256: " + result.ArchiveSha256;
        }
        catch (Exception ex)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("ImpVerificaBackupNonRiuscitaPrefisso") +
                ex.Message;
            CrashLogService.Registra("Audit ZIP backup locale", ex);
        }
        finally { VerificaBackupLocaleInCorso = false; }
    }

    [ObservableProperty] private bool copiaBackupEsternoInCorso;

    /// <summary>
    /// Copia l'ultimo ZIP locale esistente sulla destinazione aggiuntiva verificandone SHA-256.
    /// L'assenza del percorso esterno o il suo errore NON pregiudicano il backup locale.
    /// Gli allegati rimangono fuori dallo ZIP (protezione da progettare separatamente).
    /// </summary>
    [RelayCommand]
    private Task CopiaBackupEsterno() =>
        ComandoAsincrono.EseguiAsync(CopiaBackupEsternoCoreAsync, "Copia del backup esterno", m => MessaggioErrore = m);

    private async Task CopiaBackupEsternoCoreAsync()
    {
        if (CopiaBackupEsternoInCorso) return;
        if (string.IsNullOrWhiteSpace(PercorsoBackupEsterno))
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("ImpConfiguraPercorsoBackupMsg");
            return;
        }
        CopiaBackupEsternoInCorso = true;
        try
        {
            var directory = WorkspaceLocalPaths.GetBackupDirectory(_state.Paths.Root, AppState.TestLocalBackupBaseDirectory);
            var latest = Directory.EnumerateFiles(directory, "TestLoom_snapshot_*.zip")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName
                ?? throw new FileNotFoundException(LocalizationService.Instance.Traduci("ImpNessunoSnapshotMsg"));
            var target = PercorsoBackupEsterno;
            var sharedRoot = _state.Paths.Root;
            var external = await Task.Run(() => ExternalBackupCopyService.CopyVerified(latest, target, sharedRoot));
            MessaggioErrore = null;
            Messaggio = string.Format(LocalizationService.Instance.Traduci("ImpCopiaZipVerificataFmt"), external);
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ImpBackupAggiuntivoNonRiuscitoFmt"), ex.Message);
            CrashLogService.Registra("Copia backup su destinazione esterna", ex);
        }
        finally { CopiaBackupEsternoInCorso = false; }
    }

    [RelayCommand]
    private void CreaBackupManuale()
    {
        if (!_state.PuoScrivere) { MessaggioErrore = LocalizationService.Instance.Traduci("BackupNonDisponibile"); return; }
        try
        {
            var percorso = BackupService.CreaOra(_state.Paths, DateTime.Now, AppState.TestLocalBackupBaseDirectory);
            MessaggioErrore = null;
            Messaggio = string.Format(LocalizationService.Instance.Traduci("BackupCreatoFmt"), Path.GetFileName(percorso));
        }
        catch (Exception ex) { MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("BackupNonRiuscitoFmt"), ex.Message); }
    }

    /// <summary>Apre la cartella backup LOCALE (decisione utente 6.4.2: gli snapshot non vivono più nella
    /// cartella condivisa). Stessa politica di sola lettura di <see cref="ApriCartellaAllegati"/>: non crea la
    /// cartella come side-effect se non esiste ancora nessuno snapshot.</summary>
    [RelayCommand]
    private void ApriCartellaBackup()
    {
        try
        {
            var cartellaBackup = WorkspaceLocalPaths.GetBackupDirectory(_state.Paths.Root, AppState.TestLocalBackupBaseDirectory);
            if (!Directory.Exists(cartellaBackup))
                throw new DirectoryNotFoundException(LocalizationService.Instance.Traduci("ImpCartellaBackupNonEsisteMsg"));

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cartellaBackup) { UseShellExecute = true });
            MessaggioErrore = null;
        }
        catch (Exception ex) { MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("BackupCartellaApriErroreFmt"), ex.Message); }
    }

    [RelayCommand]
    private void ApriBozzeEmergenza()
    {
        try
        {
            if (!_state.CartellaValida)
                throw new DirectoryNotFoundException(LocalizationService.Instance.Traduci("ImpApriCartellaDatiValidaMsg"));
            var path = FailedSaveDraftService.LocalDirectory(_state.Paths.Root);
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException(LocalizationService.Instance.Traduci("ImpNessunaBozzaEmergenzaMsg"));

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            MessaggioErrore = null;
            Messaggio = LocalizationService.Instance.Traduci("ImpBozzeNonAutorevoliMsg");
        }
        catch (Exception ex)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("ImpImpossibileApriBozzePrefisso") + ex.Message;
        }
    }

    /// <summary>Apre la cartella allegati direttamente in Esplora risorse (Addendum: grill-me 2026-08-18,
    /// quinta follow-up). È intenzionalmente una LETTURA pura del workspace: se nessun allegato ha ancora
    /// creato il bucket condiviso, il comando informa l'utente ma non crea directory come side-effect di una
    /// semplice consultazione (importante soprattutto in modalità sola lettura/frozen).</summary>
    [RelayCommand]
    private async Task ApriCartellaAllegati()
    {
        try
        {
            var cartellaAllegati = _state.Paths.AllegatiRootDir;
            // 6.6.1 (correzione 1): esistenza con tetto di tempo, fuori dal thread UI.
            var esito = await Task.Run(() => RadiceCondivisaSonda.Verifica(cartellaAllegati));
            if (esito == RadiceCondivisaSonda.Esito.Lenta)
                throw new IOException(LocalizationService.Instance.Traduci("ImpCartellaNonRispondeMsg"));
            if (esito is RadiceCondivisaSonda.Esito.Assente or RadiceCondivisaSonda.Esito.Inaccessibile)
                throw new DirectoryNotFoundException(LocalizationService.Instance.Traduci("ImpCartellaAllegatiNonEsisteMsg"));

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cartellaAllegati) { UseShellExecute = true });
            MessaggioErrore = null;
        }
        catch (Exception ex) { MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("AllegatiApriCartellaErroreFmt"), ex.Message); }
    }

    /// <summary>Apre un file segnalato in "File con problemi" con l'app predefinita del sistema, per la
    /// modifica manuale quando la riparazione automatica non basta (feedback utente 2026-08-18, punto 5).</summary>
    [RelayCommand]
    private void ApriFileConProblemi(Models.FileConProblemi? file)
    {
        if (file == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file.Percorso) { UseShellExecute = true });
            MessaggioErrore = null;
        }
        catch (Exception ex) { MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("FileConProblemiApriErroreFmt"), ex.Message); }
    }

    /// <summary>Tenta una riparazione automatica best-effort del file (FileRepairService: solo correzioni di
    /// struttura JSON verificate prima della scrittura, con backup dell'originale) e, se riesce, ricarica
    /// l'indice così la riga sparisce dalla lista se il file torna leggibile (feedback utente 2026-08-18,
    /// punto 5).</summary>
    [RelayCommand]
    private void ProvaRiparaFileConProblemi(Models.FileConProblemi? file)
    {
        if (file == null) return;
        var risultato = FileRepairService.ProvaRipara(file.Percorso);
        if (!risultato.Riuscita)
        {
            MessaggioErrore = risultato.Messaggio;
            return;
        }
        Messaggio = string.Format(LocalizationService.Instance.Traduci("FileConProblemiRiparatoFmt"), Path.GetFileName(file.Percorso), Path.GetFileName(risultato.Messaggio));
        MessaggioErrore = null;
        _state.RicaricaTutto();
        OnPropertyChanged(nameof(NumeroFileConProblemi));
        OnPropertyChanged(nameof(HaFileConProblemi));
        OnPropertyChanged(nameof(FileConProblemi));
    }

    /// <summary>Prova a marcare tutti gli allegati come "solo online" per liberare spazio locale su OneDrive/
    /// SharePoint Files On-Demand (Addendum: grill-me 2026-08-18, quarta follow-up): utile ripeterlo dopo che
    /// OneDrive ha ri-scaricato tutto in locale (nuovo PC, resync, ecc.).</summary>
    [RelayCommand]
    private void LiberaSpazioAllegati()
    {
        if (EModalitaRete)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("CloudFilesNonApplicabiliRete");
            return;
        }
        AllegatiSyncService.ProvaSegnaCartellaSoloOnline(_state.Paths.AllegatiRootDir);
        MessaggioErrore = null;
        Messaggio = LocalizationService.Instance.Traduci("AllegatiLiberaSpazioFattoMsg");
    }
}
