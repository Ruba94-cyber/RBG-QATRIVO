using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Schermata di accesso (§7.9 + Addendum §18): chiede cartella condivisa + operatore (nome+PIN) da
/// un elenco predefinito estendibile. Il primo operatore mai creato in una cartella diventa automaticamente
/// Master (bootstrap), altrimenti nessuno potrebbe mai gestire gli altri (Addendum §17.2). Serve sia al
/// primissimo avvio sia, riutilizzata identica, come schermata di login dopo un logout esplicito — è per
/// questo che il testo distingue i due casi con EPrimoAccesso invece di avere due schermate separate.</summary>
public partial class FirstRunViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly Action _onCompletato;

    [ObservableProperty] private string cartella = "";
    [ObservableProperty] private string? operatoreSelezionato;
    [ObservableProperty] private string pinInserito = "";
    [ObservableProperty] private string nuovoOperatoreNome = "";
    [ObservableProperty] private string nuovoOperatorePin = "";
    [ObservableProperty] private string? messaggioErrore;
    public bool MostraErrore => !string.IsNullOrEmpty(MessaggioErrore);

    /// <summary>Codice di recupero mostrato una tantum subito dopo la creazione di un operatore o un reset
    /// riuscito, da salvare offline: unico modo per reimpostare il PIN senza un Master (Addendum redesign
    /// 2026-08) — copre in particolare il caso dell'unico Master che dimentica il proprio PIN.</summary>
    [ObservableProperty] private string? codiceRecuperoMostrato;
    public bool MostraCodiceRecupero => !string.IsNullOrEmpty(CodiceRecuperoMostrato);
    partial void OnCodiceRecuperoMostratoChanged(string? value) => OnPropertyChanged(nameof(MostraCodiceRecupero));

    [ObservableProperty] private bool mostraRecuperoPin;
    [ObservableProperty] private string? recuperoOperatoreSelezionato;
    [ObservableProperty] private string recuperoCodiceInserito = "";
    [ObservableProperty] private string recuperoNuovoPin = "";
    [ObservableProperty] private string? recuperoMessaggioErrore;
    public bool MostraRecuperoErrore => !string.IsNullOrEmpty(RecuperoMessaggioErrore);
    partial void OnRecuperoMessaggioErroreChanged(string? value) => OnPropertyChanged(nameof(MostraRecuperoErrore));

    /// <summary>Vero solo quando non esiste ancora nessun operatore in questa cartella: distingue il
    /// primissimo setup (testi/onboarding estesi) da un login successivo (dopo un logout), dove la cartella
    /// e gli operatori esistono già e l'utente deve solo scegliersi e inserire il PIN.</summary>
    public bool EPrimoAccesso => OperatoriDisponibili.Count == 0;

    /// <summary>Cartella configurata ma non ancora aperta (per esempio una cartella di rete non raggiungibile all'avvio, 6.6.1 e):
    /// la schermata lo spiega e offre Riprova, invece di sembrare un primo accesso senza motivo.</summary>
    public bool MostraRiprovaCartella => !string.IsNullOrWhiteSpace(_state.Config.CartellaCondivisa) && !_state.CartellaValida;

    /// <summary>Motivo leggibile dell'ultimo controllo sulla cartella configurata (esito di StatoCondivisione).</summary>
    public string TestoStatoCartella => _state.StatoCondivisione.Messaggio;

    /// <summary>Riprova l'apertura della cartella configurata senza riavviare l'app. Non scrive nulla finché la cartella non
    /// risponde (vedi AppState.RiprovaInizializzazione). Eseguito fuori dal thread UI.</summary>
    [RelayCommand]
    private async Task RiprovaCartella()
    {
        if (AccessoInCorso) return;
        AccessoInCorso = true;
        bool aperta;
        try
        {
            aperta = await Task.Run(() => _state.RiprovaInizializzazione());
        }
        finally
        {
            AccessoInCorso = false;
        }
        MessaggioErrore = aperta ? null : LocalizationService.Instance.Traduci("CartellaNonAncoraRaggiungibile");
        OnPropertyChanged(nameof(MostraRiprovaCartella));
        OnPropertyChanged(nameof(TestoStatoCartella));
    }

    /// <summary>Vero quando la cartella ha operatori ma NESSUNO di quelli attivi ha un ruolo Master con
    /// sigillo valido (revisione piano 6.4.2, §4b, punto 3): nessuno può più entrare come Master per
    /// correggere la situazione dall'app. Calcolato in RicaricaOperatori, mai qui, per non ricaricare il
    /// repository ad ogni binding. Deliberatamente NESSUNA auto-promozione a Master da qui: sarebbe un
    /// bypass del sigillo stesso. L'unica via d'uscita indicata all'utente è lo strumento
    /// tools/data-fix-6.4.2 (o il referente) — vedi RicaricaOperatori per il log CrashLog/DiagnosticTrace.</summary>
    [ObservableProperty] private bool lockoutRuoliNonSigillati;
    public bool MostraLockoutRuoliNonSigillati => LockoutRuoliNonSigillati;
    partial void OnLockoutRuoliNonSigillatiChanged(bool value) => OnPropertyChanged(nameof(MostraLockoutRuoliNonSigillati));

    /// <summary>Il cambio cartella resta disponibile ma collassato di default in un login di ritorno
    /// (non serve quasi mai): al primissimo avvio, invece, è l'unica cosa da fare e resta sempre visibile.</summary>
    [ObservableProperty] private bool mostraCambioCartella = true;
    [ObservableProperty] private bool cartellaVuota;
    [ObservableProperty] private bool operazioneDemoInCorso;
    [ObservableProperty] private string? messaggioDemo;
    [ObservableProperty] private bool richiedeCambioPin;
    [ObservableProperty] private string nuovoPinObbligatorio = "";
    [ObservableProperty] private string confermaNuovoPinObbligatorio = "";
    [ObservableProperty] private bool attendeConfermaCodiceRecupero;
    public bool MostraLoginStandard => !RichiedeCambioPin && !AttendeConfermaCodiceRecupero;
    partial void OnRichiedeCambioPinChanged(bool value) => OnPropertyChanged(nameof(MostraLoginStandard));
    partial void OnAttendeConfermaCodiceRecuperoChanged(bool value) => OnPropertyChanged(nameof(MostraLoginStandard));

    public bool PuoInizializzareDemo => CartellaVuota && !OperazioneDemoInCorso;
    public bool MostraMessaggioDemo => !string.IsNullOrWhiteSpace(MessaggioDemo);
    public bool MostraDemoInCorso => OperazioneDemoInCorso;

    public List<string> OperatoriDisponibili { get; private set; } = new();

    /// <summary>Modalità di accesso della cartella dati SELEZIONATA (Cartella), non necessariamente ancora
    /// quella aperta in AppState — stesso motivo/schema di OperatoriDisponibili sotto: letta da un DataPaths
    /// temporaneo su Cartella in RicaricaOperatori, così la UI di login riflette subito la cartella digitata
    /// invece di quella (eventualmente diversa) aperta l'ultima volta. Vero = "Accesso senza PIN": nasconde il
    /// campo PIN/recupero PIN e Login non lo richiede più. RuoloSigilloService.SenzaPinEffettivo resta il
    /// punto unico di calcolo (sigillo verificato qui esattamente come in AppState.AccessoSenzaPin).</summary>
    public bool AccessoSenzaPin { get; private set; }

    public FirstRunViewModel(AppState state, Action onCompletato)
    {
        _state = state;
        _onCompletato = onCompletato;
        Cartella = state.Config.CartellaCondivisa;
        RicaricaOperatori();
        MostraCambioCartella = EPrimoAccesso;
    }

    [RelayCommand]
    private void ToggleCambioCartella() => MostraCambioCartella = !MostraCambioCartella;

    partial void OnCartellaChanged(string value) => AggiornaStatoCartella();
    partial void OnCartellaVuotaChanged(bool value) => OnPropertyChanged(nameof(PuoInizializzareDemo));
    partial void OnOperazioneDemoInCorsoChanged(bool value)
    {
        OnPropertyChanged(nameof(PuoInizializzareDemo));
        OnPropertyChanged(nameof(MostraDemoInCorso));
    }
    partial void OnMessaggioDemoChanged(string? value) => OnPropertyChanged(nameof(MostraMessaggioDemo));

    /// <summary>Cartella vuota? Calcolato fuori dal thread UI con la sonda a tetto di tempo: l'enumerazione di una cartella di
    /// rete in stallo non deve bloccare la schermata (6.6.1, correzione 1). Il valore viene applicato solo se la cartella non è
    /// nel frattempo cambiata.</summary>
    private async void AggiornaStatoCartella()
    {
        var cartella = Cartella;
        var vuota = await Task.Run(() =>
        {
            try
            {
                var esito = RadiceCondivisaSonda.Verifica(cartella);
                return esito is RadiceCondivisaSonda.Esito.Presente or RadiceCondivisaSonda.Esito.Offline
                    && !Directory.EnumerateFileSystemEntries(cartella).Any();
            }
            catch
            {
                return false;
            }
        });
        if (cartella == Cartella) CartellaVuota = vuota;
    }

    [RelayCommand]
    private async Task CaricaDemoCompleta()
    {
        await EseguiInizializzazioneDemo(
            () => SeedDemoDataService.GeneraCompletoPerPrimoAvvio(Cartella),
            LocalizationService.Instance.Traduci("DemoCompletaCaricata"),
            selezionaOperatoreDemo: true);
    }

    [RelayCommand]
    private async Task CaricaDemoConfigurazione()
    {
        await EseguiInizializzazioneDemo(
            () => SeedDemoDataService.GeneraConfigurazionePerPrimoAvvio(Cartella),
            LocalizationService.Instance.Traduci("DemoConfigurazioneCaricata"),
            selezionaOperatoreDemo: false);
    }

    private async Task EseguiInizializzazioneDemo(Action genera, string messaggioRiuscita, bool selezionaOperatoreDemo)
    {
        if (!PuoInizializzareDemo) return;
        OperazioneDemoInCorso = true;
        MessaggioErrore = null;
        MessaggioDemo = null;
        try
        {
            await Task.Run(genera);
            // Su cartelle sincronizzate (OneDrive/SharePoint) i file appena scritti possono essere ancora
            // bloccati: una sola lettura fallita lasciava elenco e messaggio vuoti fino al ↻ manuale.
            await RicaricaOperatoriDopoDemoAsync(attesiOperatori: selezionaOperatoreDemo);
            if (selezionaOperatoreDemo)
            {
                OperatoreSelezionato = "Operatore Prova A";
                PinInserito = "1234";
            }
            MessaggioDemo = messaggioRiuscita;
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("CaricamentoDemoNonRiuscitoFmt"), ex.Message);
        }
        finally
        {
            OperazioneDemoInCorso = false;
            AggiornaStatoCartella();
        }
    }

    [RelayCommand]
    private void ToggleRecuperoPin()
    {
        MostraRecuperoPin = !MostraRecuperoPin;
        RecuperoOperatoreSelezionato = null;
        RecuperoCodiceInserito = "";
        RecuperoNuovoPin = "";
        RecuperoMessaggioErrore = null;
    }

    /// <summary>Reimposta il PIN verificando il codice di recupero salvato offline dall'operatore, senza
    /// bisogno di un Master — genera anche un nuovo codice (il precedente è a uso singolo) e lo mostra una
    /// tantum, esattamente come alla creazione. Lo snapshot e' una precondizione: il recovery code e' a uso
    /// singolo e una modifica concorrente dell'operatore non puo' essere cancellata da una replica in ritardo.</summary>
    [RelayCommand]
    private void ConfermaRecuperoPin()
    {
        // Verifica della cartella tramite la sonda non bloccante (come AggiungiOperatore): un Directory.Exists sincrono su una
        // cartella di rete in stallo bloccherebbe il thread UI senza tetto di tempo.
        if (string.IsNullOrWhiteSpace(RecuperoOperatoreSelezionato) || string.IsNullOrWhiteSpace(RecuperoCodiceInserito) || string.IsNullOrWhiteSpace(RecuperoNuovoPin) || !RadiceCondivisaSonda.PresenteUi(Cartella))
        {
            RecuperoMessaggioErrore = LocalizationService.Instance.Traduci("RecuperoCampiObbligatori");
            return;
        }
        try
        {
            var paths = new DataPaths(Cartella);
            var repo = new MasterDataRepository<Operatore>(paths.OperatoriFile);
            var lista = repo.Load();
            var expectedSnapshot = EntityStateFingerprint.Clone(lista);
            var operatore = lista.FirstOrDefault(o => o.Nome == RecuperoOperatoreSelezionato);
            if (operatore == null || !PinService.Verifica(RecuperoCodiceInserito.Trim(), operatore.RecoveryCodeHash))
            {
                RecuperoMessaggioErrore = LocalizationService.Instance.Traduci("RecuperoCodiceNonValido");
                return;
            }
            operatore.PinHash = PinService.Hash(RecuperoNuovoPin);
            operatore.PinChiaro = RecuperoNuovoPin;
            var nuovoCodice = PinService.GeneraCodiceRecupero();
            operatore.RecoveryCodeHash = PinService.Hash(nuovoCodice);
            operatore.RecoveryCodeChiaro = nuovoCodice;
            operatore.UltimaModifica = DateTime.Now;
            operatore.ModificatoDa = operatore.Nome;
            repo.Save(lista, expectedSnapshot);

            RecuperoMessaggioErrore = null;
            MostraRecuperoPin = false;
            OperatoreSelezionato = operatore.Nome;
            PinInserito = RecuperoNuovoPin;
            CodiceRecuperoMostrato = nuovoCodice;
            RecuperoCodiceInserito = "";
            RecuperoNuovoPin = "";
        }
        catch (Exception ex)
        {
            RecuperoMessaggioErrore = string.Format(LocalizationService.Instance.Traduci("RecuperoNonRiuscitoFmt"), ex.Message);
        }
    }

    partial void OnMessaggioErroreChanged(string? value) => OnPropertyChanged(nameof(MostraErrore));

    /// <summary>Rilegge gli operatori dopo un caricamento demo riprovando brevemente se l'elenco risulta ancora
    /// vuoto (file in scrittura/sincronizzazione), invece di una sola lettura che in caso di errore transitorio
    /// lasciava l'elenco vuoto fino al pulsante di ricarica.</summary>
    private async Task RicaricaOperatoriDopoDemoAsync(bool attesiOperatori)
    {
        for (var tentativo = 0; tentativo < 5; tentativo++)
        {
            RicaricaOperatori();
            if (!attesiOperatori || OperatoriDisponibili.Count > 0) return;
            if (tentativo < 4) await Task.Delay(200 * (tentativo + 1));
        }
    }

    /// <summary>Esito della lettura di operatori e modalità di accesso dalla cartella digitata.</summary>
    private sealed record LetturaOperatori(bool Raggiungibile, List<Operatore> Tutti, bool SenzaPin, bool Errore);

    private int _sequenzaRicarica;

    /// <summary>Ricarica operatori e modalità di accesso dalla cartella digitata. Le letture avvengono fuori dal thread UI: la
    /// cartella può essere una cartella di rete in stallo (6.6.1, correzione 1). Viene applicata solo la richiesta più recente.</summary>
    private async void RicaricaOperatori()
    {
        var sequenza = Interlocked.Increment(ref _sequenzaRicarica);
        var cartella = Cartella;
        var lettura = await Task.Run(() => LeggiOperatori(cartella));
        if (sequenza != _sequenzaRicarica) return;
        ApplicaLettura(lettura);
        AggiornaStatoCartella();
    }

    private static LetturaOperatori LeggiOperatori(string cartella)
    {
        var esito = RadiceCondivisaSonda.Verifica(cartella);
        if (esito is not (RadiceCondivisaSonda.Esito.Presente or RadiceCondivisaSonda.Esito.Offline))
            return new LetturaOperatori(false, new(), false, false);
        try
        {
            var tempPaths = new DataPaths(cartella);
            var store = new MasterDataRepository<Operatore>(tempPaths.OperatoriFile);
            var tutti = store.Load();
            return new LetturaOperatori(true, tutti, RuoloSigilloService.SenzaPinEffettivo(AccessModeConfigService.Load(tempPaths)), false);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("FirstRun RicaricaOperatori", ex);
            return new LetturaOperatori(true, new(), false, true);
        }
    }

    private void ApplicaLettura(LetturaOperatori lettura)
    {
        if (!lettura.Raggiungibile || lettura.Errore)
        {
            OperatoriDisponibili = new();
            OnPropertyChanged(nameof(OperatoriDisponibili));
            AccessoSenzaPin = false;
            LockoutRuoliNonSigillati = false;
        }
        else
        {
            OperatoriDisponibili = lettura.Tutti.Where(o => !o.Disattivo).Select(o => o.Nome).ToList();
            OnPropertyChanged(nameof(OperatoriDisponibili));
            AccessoSenzaPin = lettura.SenzaPin;
            AggiornaLockoutRuoliNonSigillati(lettura.Tutti);
        }
        OnPropertyChanged(nameof(EPrimoAccesso));
        OnPropertyChanged(nameof(AccessoSenzaPin));
    }

    /// <summary>Rileva il lockout descritto in piano 6.4.2, §4b, punto 3: la cartella ha operatori ma nessuno
    /// di quelli ATTIVI ha un ruolo Master con sigillo valido — un Master Disattivo non aiuta, perché non può
    /// comunque accedere per correggere la situazione. Registra l'evento in CrashLog e DiagnosticTrace (una
    /// sola volta per stato rilevato, non a ogni RicaricaOperatori, per non intasare i log a ogni digitazione
    /// nel campo Cartella) così il problema resta visibile anche fuori dall'interfaccia.</summary>
    private void AggiornaLockoutRuoliNonSigillati(List<Operatore> tutti)
    {
        var inLockout = tutti.Count > 0 && !tutti.Any(o => !o.Disattivo && o.RuoloEffettivo == RuoloOperatore.Master);
        if (inLockout && !LockoutRuoliNonSigillati)
        {
            var messaggio = $"Cartella '{Cartella}': {tutti.Count} operatori, nessun Master con sigillo valido. Eseguire tools/data-fix-6.4.2 sulla cartella dati o contattare il referente.";
            DiagnosticTraceService.Registra("Accesso", messaggio);
            CrashLogService.Registra("LockoutRuoliNonSigillati: " + messaggio, null);
        }
        LockoutRuoliNonSigillati = inLockout;
    }

    /// <summary>Richiamato da MainViewModel dopo un logout: la stessa istanza viene riusata come schermata
    /// di login, quindi PIN e selezione precedenti vanno cancellati e l'elenco operatori ricaricato (nel
    /// frattempo un Master potrebbe aver aggiunto/disattivato qualcuno).</summary>
    public void ResetPerNuovoAccesso()
    {
        OperatoreSelezionato = null;
        PinInserito = "";
        MessaggioErrore = null;
        RichiedeCambioPin = false;
        AttendeConfermaCodiceRecupero = false;
        NuovoPinObbligatorio = "";
        ConfermaNuovoPinObbligatorio = "";
        CodiceRecuperoMostrato = null;
        MostraRecuperoPin = false;
        RecuperoOperatoreSelezionato = null;
        RecuperoCodiceInserito = "";
        RecuperoNuovoPin = "";
        RecuperoMessaggioErrore = null;
        MessaggioDemo = null;
        RicaricaOperatori();
        MostraCambioCartella = EPrimoAccesso;
    }

    [RelayCommand]
    private void RicaricaDopoScelta() => RicaricaOperatori();

    [RelayCommand]
    private void AggiungiOperatore()
    {
        if (string.IsNullOrWhiteSpace(NuovoOperatoreNome) || string.IsNullOrWhiteSpace(NuovoOperatorePin) || !RadiceCondivisaSonda.PresenteUi(Cartella)) return;
        try
        {
            var paths = new DataPaths(Cartella);
            paths.EnsureStructure();
            var repo = new MasterDataRepository<Operatore>(paths.OperatoriFile);
            var lista = repo.Load();
            var expectedSnapshot = EntityStateFingerprint.Clone(lista);
            // Unicità del Nome, inclusi i Disattivo (revisione piano 6.4.2, §4b, punto 2), stessa regola e
            // stesso messaggio dell'editor Operatori (MasterDataEditors.NomiDuplicati): qui in più era un
            // return silenzioso, che lasciava l'utente senza spiegazione del perché "Aggiungi" non facesse
            // nulla — ora un errore localizzato come per ogni altro fallimento di questo comando.
            if (lista.Any(o => o.Nome.Trim().Equals(NuovoOperatoreNome.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("OperatoreNomeDuplicatoFmt"), NuovoOperatoreNome.Trim());
                return;
            }

            // bootstrap: il primissimo operatore creato in questa cartella diventa Master, altrimenti nessuno
            // potrebbe mai gestire ruoli/PIN/gamma degli altri. Chi si registra da sé qui parte come Viewer,
            // privilegio minimo (feedback utente 2026-08-24): un Master promuove esplicitamente a Standard chi
            // deve davvero eseguire ed editare test, invece che concedere scrittura di default a chiunque si
            // crei un profilo dalla schermata di accesso. Il Save condizionale e' fondamentale anche per la
            // sicurezza: due PC che vedono insieme una lista vuota non devono poter creare due "primi Master".
            var ruolo = lista.Count == 0 ? RuoloOperatore.Master : RuoloOperatore.Viewer;
            var codiceRecupero = PinService.GeneraCodiceRecupero();

            var nuovoOperatore = new Operatore
            {
                Nome = NuovoOperatoreNome,
                Iniziali = IdGenerator.IniziliDa(NuovoOperatoreNome),
                PinHash = PinService.Hash(NuovoOperatorePin),
                PinChiaro = NuovoOperatorePin,
                RecoveryCodeHash = PinService.Hash(codiceRecupero),
                RecoveryCodeChiaro = codiceRecupero,
                Ruolo = ruolo,
                UltimaModifica = DateTime.Now,
                ModificatoDa = NuovoOperatoreNome
            };
            // Sigillato subito, non solo per il bootstrap Master (piano 6.4.2, §4b, punto 2: "Il primo
            // operatore creato al primo avvio nasce sigillato") — anche il Viewer creato per auto-registrazione
            // ha un sigillo valido fin da subito, così un futuro editing del ruolo da un Master (che risigilla
            // comunque ad ogni Salva) non è l'unico momento in cui l'operatore risulta protetto.
            RuoloSigilloService.Sigilla(nuovoOperatore);
            lista.Add(nuovoOperatore);
            repo.Save(lista, expectedSnapshot);
            CodiceRecuperoMostrato = codiceRecupero;
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("CreazioneOperatoreNonRiuscitaFmt"), ex.Message);
            return;
        }
        MessaggioErrore = null;
        RicaricaOperatori();
        OperatoreSelezionato = NuovoOperatoreNome;
        PinInserito = NuovoOperatorePin;
        NuovoOperatoreNome = "";
        NuovoOperatorePin = "";
    }

    /// <summary>ViewModel dello stesso popup di avanzamento (AvvioWindow) già usato dal bootstrap iniziale in
    /// App.axaml.cs: al primo accesso l'inizializzazione di una cartella nuova/grande può richiedere minuti
    /// esattamente come l'apertura di una cartella già definita, quindi merita la stessa finestra invece di
    /// bloccare in silenzio la UI (feedback utente: "ci mette tanto perché inizializza la nuova cartella, ma
    /// dovrebbe essere visibile... con pop-up"). Il code-behind della view osserva <see cref="AccessoInCorso"/>
    /// per mostrare/nascondere la finestra; il ViewModel non conosce Avalonia.Window.</summary>
    public AvvioViewModel AvvioProgress { get; } = new();

    /// <summary>Vero durante l'inizializzazione della cartella innescata da "Accedi": il comando è comunque
    /// non rientrante di suo (RelayCommand), questa proprietà serve solo a pilotare il popup dalla view.</summary>
    [ObservableProperty] private bool accessoInCorso;

    [RelayCommand]
    private async Task Conferma()
    {
        if (string.IsNullOrWhiteSpace(Cartella))
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("SelezionaCartellaCondivisa");
            return;
        }
        if (string.IsNullOrWhiteSpace(OperatoreSelezionato))
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("SelezionaNomeOperatore");
            return;
        }
        try
        {
            AccessoInCorso = true;
            // Lavoro potenzialmente lungo (bootstrap/riconciliazione di una cartella nuova o grande) spostato
            // fuori dal thread UI: prima di questo cambiamento girava sincrono dentro il comando e congelava
            // la finestra senza alcun feedback, a differenza dell'apertura con cartella già definita che passa
            // per App.axaml.cs e mostra AvvioWindow. Progress<T> è costruito qui, sul thread UI, quindi ogni
            // Report() da Task.Run arriva già marshallato sul dispatcher Avalonia.
            var progress = new Progress<BootstrapProgress>(AvvioProgress.Aggiorna);
            await Task.Run(() => _state.ImpostaCartella(Cartella, progress));
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ImpossibileUsareCartellaFmt"), ex.Message);
            return;
        }
        finally
        {
            AccessoInCorso = false;
        }
        // Dopo ImpostaCartella la cartella è realmente aperta: AppState.AccessoSenzaPin (sigillo verificato sul
        // workspace ora attivo) è la fonte autorevole, più affidabile della lettura provvisoria fatta da
        // RicaricaOperatori prima ancora di aprire la cartella.
        AccessoSenzaPin = _state.AccessoSenzaPin;
        OnPropertyChanged(nameof(AccessoSenzaPin));
        var accessoRiuscito = AccessoSenzaPin
            ? _state.ImpostaOperatoreSenzaPin(OperatoreSelezionato)
            : _state.ImpostaOperatore(OperatoreSelezionato, PinInserito);
        if (!accessoRiuscito)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci(AccessoSenzaPin ? "OperatoreNonValido" : "PinNonCorretto");
            return;
        }
        MessaggioErrore = null;
        // Il cambio PIN obbligatorio (account demo PIN 1234) non deve bloccare l'accesso in modalità senza PIN
        // (piano 6.4.2, §4b, decisione utente): in quella modalità il PIN non viene comunque mai chiesto/letto.
        if (!AccessoSenzaPin && _state.OperatoreCorrente?.RichiedeCambioPin == true)
        {
            RichiedeCambioPin = true;
            PinInserito = "";
            return;
        }
        _onCompletato();
    }

    [RelayCommand]
    private void ConfermaCambioPinObbligatorio()
    {
        if (NuovoPinObbligatorio.Length != 4 || !NuovoPinObbligatorio.All(char.IsDigit))
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("PinQuattroCifre");
            return;
        }
        if (NuovoPinObbligatorio == "1234")
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("PinDemoNonConsentito");
            return;
        }
        if (NuovoPinObbligatorio != ConfermaNuovoPinObbligatorio)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("PinConfermaNonCoincide");
            return;
        }
        try
        {
            CodiceRecuperoMostrato = _state.CompletaCambioPinObbligatorio(NuovoPinObbligatorio);
            MessaggioErrore = null;
            RichiedeCambioPin = false;
            AttendeConfermaCodiceRecupero = true;
            NuovoPinObbligatorio = "";
            ConfermaNuovoPinObbligatorio = "";
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("CambioPinNonRiuscitoFmt"), ex.Message);
        }
    }

    [RelayCommand]
    private void ContinuaDopoCodiceRecupero()
    {
        if (!AttendeConfermaCodiceRecupero || !MostraCodiceRecupero) return;
        AttendeConfermaCodiceRecupero = false;
        CodiceRecuperoMostrato = null;
        _onCompletato();
    }
}
