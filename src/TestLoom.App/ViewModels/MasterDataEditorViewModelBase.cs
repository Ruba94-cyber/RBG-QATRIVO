using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Interfaccia non generica per poter chiedere "hai modifiche non salvate?" a un qualunque editor
/// master data da ImpostazioniViewModel senza conoscere il tipo T concreto (Requisito, Operatore, ecc.).</summary>
public interface IEditorConModifiche
{
    bool RilascioLockInAttesa { get; }
    bool HaModificheNonSalvate();

    /// <summary>True se l'avvio dell'editing di questa cella va annullato: le chiavi di una voce esistente
    /// vanno cambiate soltanto attraverso il comando Unisci/Rinomina, che aggiorna anche i riferimenti.</summary>
    bool BloccaRinominaDiretta(object? elemento, string? percorsoProprieta);

    /// <summary>Rilascia il lock del file master data se l'editor lo detiene (InModifica), senza chiedere
    /// conferma né ricaricare i dati — usato alla chiusura dell'intera applicazione (feedback utente
    /// 2026-08-24, punto 5), dove l'eventuale conferma di abbandono modifiche è già stata gestita a monte
    /// (MainWindow.OnClosing) e non ha senso ricaricare una finestra che sta per sparire. Senza questo, il
    /// lock restava attivo fino al timeout di 20 minuti anche quando l'app era già chiusa.</summary>
    Task RilasciaLockSeAttivoAsync();
}

/// <summary>Base per i 6 editor master data (§7.5): tabella con modifica diretta sulla riga + "+Nuovo",
/// changelog automatico per-campo (via confronto riflessivo con lo snapshot pre-modifica), e lock applicativo
/// dell'intero file durante la modifica (§3.3) — sola lettura libera, lock acquisito solo a inizio modifica.
/// Lo snapshot è anche indicizzato per chiave: i controlli di autorizzazione/changelog/import devono restare
/// lineari con cataloghi da 100k+ righe, non degradare a scansioni annidate O(n²).</summary>
public abstract partial class MasterDataEditorViewModelBase<T> : ViewModelBase, IEditorConModifiche, IDisposable, IScorciatoiePagina where T : MasterEntity, new()
{
    private const string TestLockBarrierEnvironmentVariable = "TestLoom_UI_TEST_MASTER_DATA_LOCK_BARRIER";
    private static readonly TimeSpan TimeoutBarrieraLockTest = TimeSpan.FromSeconds(20);
    private static readonly AsyncLocal<bool> ContestoCreazioneRapidaLockTest = new();

    private readonly AppState _state;
    private readonly MasterDataRepository<T> _repo;
    private readonly string _filePath;
    private readonly Func<T, string> _keyOf;
    private List<T> _snapshot = new();
    private readonly Dictionary<string, T> _snapshotPerChiave = new(StringComparer.Ordinal);

    // BulkObservableCollection (non ObservableCollection semplice): un ripopolamento massivo con Clear() +
    // N singole Add() (un catalogo paginato può ricaricarne 200 in un colpo) genera altrettanti eventi
    // CollectionChanged separati, che possono lasciare la DataGrid virtualizzante disallineata sull'estensione
    // reale di scroll (feedback utente: "vedo solo una ventina di righe con scrollbar" su un catalogo da 104+).
    // ReplaceWith() solleva un solo Reset, stesso principio già usato da TestCaseListViewModel.Righe e
    // HomeViewModel.DaFare/Fatti per lo stesso motivo.
    public BulkObservableCollection<T> Items { get; } = new();

    [ObservableProperty] private string filtroTesto = "";

    /// <summary>Vero se c'è del testo di ricerca da rimuovere (feedback utente 2026-08-20, punti 3/9/20):
    /// pilota la visibilità del pulsante "Rimuovi filtri", stesso pattern già usato in Test/NonConformità.</summary>
    public bool HaFiltriAttivi => !string.IsNullOrWhiteSpace(FiltroTesto);

    partial void OnFiltroTestoChanged(string value)
    {
        OnPropertyChanged(nameof(HaFiltriAttivi));
        NotificaStatoVuoto();
        if (UsaPaginazione && InModifica && HaModifiche() && value != _testoRicercaApplicato)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataSalvaPaginaPrimaDiNavigare");
            FiltroTesto = _testoRicercaApplicato;
            return;
        }
        if (UsaPaginazione)
        {
            // Cataloghi paginati: la query parte dopo il debounce (vedi MasterDataEditorPaging), non a ogni tasto.
            ProgrammaRicercaPaginata(value);
        }
        else
        {
            RicalcolaItemsAttivi();
        }
    }

    [RelayCommand]
    private void ReimpostaFiltri() => FiltroTesto = "";

    /// <summary>Sottoinsieme di Items senza le righe archiviate: è questa la collezione mostrata nella tabella
    /// di lavoro di ciascun master data, non più Items direttamente (feedback utente 2026-08-18, punto 6 —
    /// "sparisce dalla tabella, vive solo in Archivio"). Items resta comunque la fonte piena salvata su
    /// disco: filtrare qui invece che lì evita di perdere le righe archiviate al primo Salva(). Nei cataloghi
    /// 100k configurati per paging, durante la sola consultazione Items/ItemsAttivi contengono invece soltanto
    /// la pagina SQLite corrente; prima di entrare in modifica vengono sempre sostituiti dal JSON completo.</summary>
    public BulkObservableCollection<T> ItemsAttivi { get; } = new();

    // Protetto (non più privato) solo per permettere a una sottoclasse di richiedere un ricalcolo dopo aver
    // mutato l'Ordine di alcuni elementi con CampoOrdinamentoService (StrumentiViewModel.SpostaSuStrumento/
    // SpostaGiuStrumento, spec riordino campi 2026-09-23 §2, seguito) — nessun altro cambio di comportamento.
    protected void RicalcolaItemsAttivi()
    {
        IEnumerable<T> query = Items.Where(i => !EArchiviata(i) && IncludiNellaVista(i));
        if (!string.IsNullOrWhiteSpace(FiltroTesto))
        {
            var q = FiltroTesto.Trim();
            query = query.Where(i => CorrispondeRicerca(i, q));
        }
        // Ordinamento naturale sulla chiave (stesso NaturalStringComparer gia' usato da AuditViewModel/
        // AuditMatriceService, Addendum feedback utente originale: "vedo solo fino a ST99 nonostante arrivino
        // a 1700+"): l'ordine alfabetico puro mescola ST100 prima di ST20, questo lo mette dove ce lo aspetta
        // l'intuizione numerica. Ordina solo la vista, non Items: non tocca l'ordine salvato su disco.
        ItemsAttivi.ReplaceWith(OrdinaPerVisualizzazione(query.OrderBy(Chiave, NaturalStringComparer.Instance)));
    }

    /// <summary>Punto di estensione per un catalogo che vuole un ordine di visualizzazione proprio invece del
    /// solo ordine alfabetico naturale sulla chiave (spec riordino campi 2026-09-23 §2, seguito — vedi
    /// StrumentiViewModel): di default non cambia nulla (lo stesso comportamento di ogni altro master data).
    /// <paramref name="ordinatoPerChiave"/> arriva già ordinato alfabeticamente: un override che applica un
    /// ordinamento stabile sopra (es. CampoOrdinamentoService.OrdinePerVisualizzazione) preserva quell'ordine
    /// come base per gli elementi senza un Ordine esplicito ancora impostato — nessun cambio visibile finché
    /// l'utente non riordina.</summary>
    protected virtual IEnumerable<T> OrdinaPerVisualizzazione(IEnumerable<T> ordinatoPerChiave) => ordinatoPerChiave;

    /// <summary>Punto di estensione (6.5.0): una sottoclasse puo restringere la vista a un sottoinsieme (es. solo
    /// requisiti Standard oppure solo Extra). Di default tutto incluso: nessun cambio per gli altri master data.</summary>
    protected virtual bool IncludiNellaVista(T item) => true;

    /// <summary>Punto di estensione (6.5.0): inizializza una riga appena creata (Nuovo / import Excel), es. per
    /// impostare il Tipo del requisito in base alla scheda in cui si crea.</summary>
    protected virtual void InizializzaNuovo(T nuovo) { }

    /// <summary>Punto di estensione (6.5.0): vero mentre una operazione asincrona dell'editor (es. import guasto) e in corso:
    /// Salva e Annulla sono bloccati.</summary>
    protected virtual bool OperazioneInCorso => false;

    /// <summary>Punto di estensione (6.5.0): ultima possibilita di fermare Salva (es. conferma esplicita per righe
    /// scartate dall'import). Ritorna false per interrompere, dopo aver impostato un messaggio.</summary>
    protected virtual bool PuoProcedereAlSalvataggio() => true;

    /// <summary>Punto di estensione (6.5.0): differenze di campi NON semplici (liste) tra snapshot e riga corrente, come
    /// riepilogo testuale per il Changelog (es. "+3/-1 guasto"); null = nessuna differenza.</summary>
    protected virtual string? DifferenzeExtra(T prev, T item) => null;

    private static IEnumerable<PropertyInfo>? _proprietaRicerca;

    /// <summary>Ogni proprietà stringa/enum/bool/lista-di-stringhe leggibile, campi sensibili esclusi
    /// (NomiCampiSensibiliEsclusi) — ricerca "trova qualunque cosa" (feedback utente 2026-08-20, punto 3:
    /// "ricerca all'interno di operatori anche per chiave"), generica per tutti i 6 master data.</summary>
    private static IEnumerable<PropertyInfo> ProprietaRicerca() => _proprietaRicerca ??= typeof(T)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0
                    && p.Name is not nameof(MasterEntity.Changelog) && p.Name != "ExtensionData"
                    && (p.PropertyType == typeof(string) || p.PropertyType == typeof(bool) || p.PropertyType.IsEnum
                        || (typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType) && p.PropertyType != typeof(string))))
        .ToList();

    private static bool CorrispondeRicerca(T item, string q)
    {
        foreach (var prop in ProprietaRicerca())
        {
            var valore = prop.GetValue(item);
            if (valore == null) continue;
            if (valore is System.Collections.IEnumerable elenco && valore is not string)
            {
                foreach (var elemento in elenco)
                    if (elemento?.ToString()?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) return true;
                continue;
            }
            if (valore.ToString()?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) return true;
        }
        return false;
    }

    /// <summary>Righe come risultavano all'ultimo Ricarica()/Salva(): usato da ValidaPrimaDiSalvare() nelle
    /// sottoclassi per confrontare un valore prima/dopo (es. CampoSistemaDefinizioneViewModel controlla se
    /// Tipo è cambiato rispetto a qui).</summary>
    protected IReadOnlyList<T> Snapshot => _snapshot;

    protected AppState StatoApp => _state;
    protected Func<T, string> ChiaveDi => _keyOf;

    /// <summary>Nome della proprietà che identifica la voce nel catalogo. La maggior parte delle anagrafiche
    /// usa Nome; Requisito usa Id. Le view passano il percorso della colonna chiave all'handler di editing.</summary>
    protected virtual string CampoChiaveDaRinominare => "Nome";

    /// <summary>Blocca già all'apertura della cella la rinomina diretta di una riga esistente. Le righe appena
    /// create restano nominabili; i cataloghi senza comando Rinomina non cambiano comportamento. Il controllo
    /// finale in Salva resta comunque attivo come protezione fail-closed contro edit/paste non intercettati.</summary>
    public bool BloccaRinominaDiretta(object? elemento, string? percorsoProprieta)
    {
        if (!Unione.Disponibile || elemento is not T item ||
            !string.Equals(percorsoProprieta, CampoChiaveDaRinominare, StringComparison.OrdinalIgnoreCase) ||
            !_chiaveInizialePerRiferimento.TryGetValue(item, out var chiaveIniziale))
            return false;

        MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("MasterDataRinominaDirettaBloccataFmt"),
            chiaveIniziale, Chiave(item));
        return true;
    }

    /// <summary>Unisci/Rinomina/Dividi con propagazione (spec 2026-09-23, spec_unisci_master.md — stesso
    /// motore di WuStatusViewModel.Unisci, generalizzato a qualunque catalogo master registrato in
    /// MasterRiferimentiService.Registro). Un solo campo qui, riusato da tutti i 8+ editor derivati da questa
    /// base class invece di essere riscritto in ciascuno: la View si lega a Unione.CoppieSospette/Unisci/
    /// Rinomina/Dividi solo se Unione.Disponibile è vero. Ricarica automaticamente questa pagina dopo ogni
    /// operazione riuscita (OperazioneCompletata), stesso schema "muta poi Ricarica" del resto della classe.</summary>
    public MasterUnioneViewModel<T> Unione { get; }

    /// <summary>Riga selezionata nella griglia per il pannello Unisci/Rinomina/Dividi (stesso pattern di
    /// WuStatusViewModel.Selezionata): la View lega DataGrid.SelectedItem qui, il setter propaga la sola chiave
    /// a Unione.ChiaveSelezionata — separata da Archivia/EliminaDefinitivamente, che restano legate alla riga
    /// cliccata dal pulsante di quella colonna.</summary>
    [ObservableProperty] private T? elementoSelezionato;
    partial void OnElementoSelezionatoChanged(T? value) => Unione.ChiaveSelezionata = value == null ? null : Chiave(value);

    [ObservableProperty] private bool inModifica;
    [ObservableProperty] private bool salvataggioInCorso;
    public bool InterazioneEditorAbilitata => !SalvataggioInCorso;
    partial void OnSalvataggioInCorsoChanged(bool value) => OnPropertyChanged(nameof(InterazioneEditorAbilitata));

    /// <summary>Catalogo paginato in modifica: la tabella mostra solo la finestra corrente (~200 righe), non
    /// l'intero catalogo. Pilota un avviso visibile nelle pagine paginabili (Requisiti/Funzionalita/componente).</summary>
    public bool MostraAvvisoModificaLimitata => InModifica && _modificaPatchBounded && _modificaFinestraParziale;

    /// <summary>Vero solo se il lock di questa sessione di modifica e' stato acquisito subito dopo un Forza
    /// Sblocco (vedi LockResult.SeguitoDaForzatura). Di norma un Master non deve mai vedere un automerge
    /// silenzioso di Phase D al Salva: qualunque scostamento dalla fotografia con cui e' entrato in modifica
    /// deve bloccare l'intero salvataggio. Dopo una forzatura esplicita, invece, recuperare quanto possibile
    /// da entrambe le parti (automerge per-riga) e' il comportamento voluto (feedback utente 2026-09-08).
    /// Si azzera automaticamente quando la sessione di modifica finisce, qualunque sia l'esito.</summary>
    private bool _modificaSeguitaAForzatura;

    /// <summary>Impronta economica dell'intero catalogo Phase D catturata all'apertura della sessione di
    /// modifica (vedi PhaseDMasterRepositoryEngine.GetCatalogSourceFingerprint): permette a Salva() di rilevare
    /// una modifica remota avvenuta ovunque nel catalogo, anche fuori dalla finestra bounded/filtrata caricata
    /// da questa sessione. Null per workspace non ancora su Phase D. Si azzera insieme a _modificaSeguitaAForzatura
    /// quando la sessione di modifica finisce.</summary>
    private string? _baselineSourceFingerprint;

    [ObservableProperty] private string? lockInfoTesto;
    [ObservableProperty] private bool lockDetenutoDaAltri;
    [ObservableProperty] private bool rilascioLockInAttesa;
    private bool _rilascioLockDopoSalvataggio;
    private readonly object _statoSessioneLockGate = new();
    private string? _sessioneLockModifica;
    private string? _sessioneLockRilascioInAttesa;
    private int _generazioneSessioneLock;
    private int _generazioneRilascioLock;
    internal Task? RilascioSessioneLockInCorso { get; private set; }
    internal Func<LockService, string, string, Task<LockEnsureResult>>? RinnovaLockHeartbeatPerTest { get; set; }
    internal Func<LockService, string, string, EsitoIo>? RilascioSessioneLockPerTest { get; set; }
    internal Func<TimeSpan, Task>? AttesaRetryLockPerTest { get; set; }
    partial void OnRilascioLockInAttesaChanged(bool value)
    {
        OnPropertyChanged(nameof(MostraModifica));
        OnPropertyChanged(nameof(MostraModificaPerCreazione));
    }
    // Avviso persistente del lock non confermato (P2.1): compare dopo MonitorRinnovoLock.SogliaAvviso rinnovi falliti di seguito.
    [ObservableProperty] private bool avvisoLockNonConfermato;
    [ObservableProperty] private string? avvisoLockNonConfermatoTesto;
    [ObservableProperty] private string? messaggioErrore;
    public bool HaMessaggioErrore => !string.IsNullOrEmpty(MessaggioErrore);
    partial void OnMessaggioErroreChanged(string? value) => OnPropertyChanged(nameof(HaMessaggioErrore));

    [ObservableProperty] private string? messaggio;
    public bool HaMessaggio => !string.IsNullOrEmpty(Messaggio);
    partial void OnMessaggioChanged(string? value) => OnPropertyChanged(nameof(HaMessaggio));

    /// <summary>Chiude il messaggio di esito (pulsante "x" del banner di successo): nessun timer, il messaggio
    /// resta finche' l'utente non lo chiude o una nuova azione lo sostituisce.</summary>
    [RelayCommand]
    private void CancellaMessaggio() => Messaggio = null;

    /// <summary>Un'azione non disponibile nella modalita' corrente va nascosta, non mostrata disabilitata:
    /// "Modifica" compare solo a chi puo' davvero entrare in modifica (PuoModificare / PuoCreare).</summary>
    public bool MostraModifica => !InModifica && !RilascioLockInAttesa && PuoModificare;
    public bool MostraModificaPerCreazione => !InModifica && !RilascioLockInAttesa && PuoCreare;

    [RelayCommand]
    private async Task RiprovaRilascioLock()
    {
        if (!RilascioLockInAttesa) return;
        string? sessione;
        int generazione;
        lock (_statoSessioneLockGate)
        {
            sessione = _sessioneLockRilascioInAttesa;
            generazione = ++_generazioneRilascioLock;
        }
        if (string.IsNullOrWhiteSpace(sessione))
        {
            CrashLogService.Registra("Riprova rilascio lock master senza SessionId esatto", new InvalidOperationException(_filePath));
            MessaggioErrore = LocalizationService.Instance.Traduci(
                _rilascioLockDopoSalvataggio ? "SalvatoSbloccoLockNonRiuscito" : "LockSbloccoNonRiuscito");
            return;
        }
        await CompletaRilascioSessioneEsattaAsync(_filePath, sessione, generazione, _rilascioLockDopoSalvataggio, mostraErrore: true);
    }

    // Stato vuoto della tabella principale: titolo "nessun elemento" (o "nessun risultato" se c'e' un filtro) e
    // suggerimento per aggiungere il primo elemento solo a chi puo' farlo e non e' gia' in modifica.
    // Sospeso durante un caricamento/ricarica di pagina (Clear + ReplaceWith): evita che lo stato vuoto lampeggi
    // fra lo svuotamento e il ripopolamento della lista.
    private int _sospensioneStatoVuoto;
    public bool ElencoVuoto => _sospensioneStatoVuoto == 0 && ItemsAttivi.Count == 0;

    private IDisposable SospendiStatoVuoto()
    {
        _sospensioneStatoVuoto++;
        return new RilascioStatoVuoto(this);
    }

    private sealed class RilascioStatoVuoto(MasterDataEditorViewModelBase<T> owner) : IDisposable
    {
        private bool _rilasciato;
        public void Dispose()
        {
            if (_rilasciato) return;
            _rilasciato = true;
            owner._sospensioneStatoVuoto--;
            owner.NotificaStatoVuoto();
        }
    }
    public bool MostraElencoVuoto => ElencoVuoto && !HaFiltriAttivi;
    public bool MostraElencoVuotoFiltrato => ElencoVuoto && HaFiltriAttivi;
    public bool MostraSuggerimentoElencoVuoto => MostraElencoVuoto && !InModifica && PuoCreare;

    private void NotificaStatoVuoto()
    {
        OnPropertyChanged(nameof(ElencoVuoto));
        OnPropertyChanged(nameof(MostraElencoVuoto));
        OnPropertyChanged(nameof(MostraElencoVuotoFiltrato));
        OnPropertyChanged(nameof(MostraSuggerimentoElencoVuoto));
    }

    /// <summary>Tutti i master data sono modificabili solo da utenti Master (Addendum §17.3/§17.9); la
    /// tabella resta comunque visibile in sola lettura a chiunque.</summary>
    public bool PuoModificare => _state.EMaster && _state.PuoScrivere;

    /// <summary>Chi può entrare in modalità modifica per AGGIUNGERE nuove voci — per default coincide con
    /// PuoModificare (comportamento invariato per Requisiti/Veicoli/Strumenti/Operatori/Categorie/Gamma/Tipi di
    /// test/Campi di sistema). Punto di estensione usato solo da FunzionalitaViewModel/EcuViewModel (feedback
    /// utente 2026-08-20, punto 11): lì uno Standard può creare nuove voci ma non toccare quelle esistenti —
    /// eccezione mirata alla regola uniforme "tutti i master data solo Master", non un rilassamento generale.</summary>
    public virtual bool PuoCreare => PuoModificare;

    protected MasterDataEditorViewModelBase(
        AppState state,
        MasterDataRepository<T> repo,
        string filePath,
        Func<T, string> keyOf,
        MasterDataPagedReadSource<T>? pagedRead = null)
    {
        _state = state;
        _repo = repo;
        _filePath = filePath;
        _keyOf = keyOf;
        _pagedRead = pagedRead ?? MasterDataPagedReadSources.TryCreate<T>(state.Paths);
        ItemsAttivi.CollectionChanged += (_, _) => NotificaStatoVuoto();
        Unione = new MasterUnioneViewModel<T>(state, repo, keyOf, null, () => InModifica);
        Unione.OperazioneCompletata += Ricarica;
        Ricarica();
        _state.DatiRicaricati += OnDatiRicaricati;
    }

    /// <summary>Aggiornamento live del ruolo mentre una pagina di editing è già aperta (feedback utente
    /// 2026-08-20, punto 1): senza questo, PuoModificare restava congelato al valore letto alla creazione
    /// della pagina finché l'utente non ri-navigava altrove e tornava indietro — un Master che declassava un
    /// operatore mentre questo aveva già la pagina aperta non gli revocava subito i pulsanti di modifica.
    /// Nessun logout forzato (scelta esplicita): i permessi si aggiornano da soli al prossimo giro di ricarica
    /// dati (periodico o forzato), la sessione resta aperta.</summary>
    protected virtual void OnDatiRicaricati()
    {
        OnPropertyChanged(nameof(PuoModificare));
        OnPropertyChanged(nameof(PuoCreare));
        OnPropertyChanged(nameof(MostraModifica));
        OnPropertyChanged(nameof(MostraModificaPerCreazione));
        NotificaStatoVuoto();
        ForzaSbloccoCommand.NotifyCanExecuteChanged();

        // La consultazione paginata si riallinea solo su un evento dati esplicito, non ad ogni pressione di
        // tasto nel filtro. Durante una modifica non si tocca mai la fotografia completa/snapshot dell'utente.
        if (_pagedRead != null && !InModifica)
        {
            if (!ProvaAttivaVisualizzazionePaginata(refreshIndice: true, reimpostaPagina: false))
            {
                try
                {
                    _visualizzazionePaginata = false;
                    ApplicaListaAutorevole(_repo.Load());
                    AggiornaSnapshotDaItems();
                }
                catch (Exception ex) { CrashLogService.Registra($"Ricarica master {typeof(T).Name} dopo DatiRicaricati", ex); }
            }
            else
            {
                // Un'altra postazione può aver scritto il master data nel frattempo: le voci disponibili per
                // Unisci devono riallinearsi anche qui, non solo sul percorso di fallback non paginato.
                AggiornaVociUnioneDisponibili();
            }
        }
    }

    /// <summary>Riallinea la lista di un catalogo non paginato (es. Veicoli) dopo una scrittura di un'altra pagina o postazione:
    /// la base rilegge solo i cataloghi paginati, quindi senza questo la griglia resterebbe sulla lettura di apertura fino alla
    /// prossima navigazione. Agisce solo fuori dalla modifica: una modifica in corso non viene toccata (il salvataggio successivo
    /// la confronta con la nuova fotografia e rileva il conflitto). La voce selezionata viene ricollegata per chiave.</summary>
    protected void RiallineaListaNonPaginata()
    {
        if (_pagedRead != null || InModifica) return;
        try
        {
            var chiaveSelezionata = ElementoSelezionato is { } selezionato ? Chiave(selezionato) : null;
            ApplicaListaAutorevole(_repo.Load());
            AggiornaSnapshotDaItems();
            if (chiaveSelezionata != null) ElementoSelezionato = Items.FirstOrDefault(i => Chiave(i) == chiaveSelezionata);
        }
        catch (Exception ex) { CrashLogService.Registra($"Ricarica master {typeof(T).Name} dopo DatiRicaricati", ex); }
    }

    /// <summary>Disiscrizione dall'evento AppState.DatiRicaricati (di durata pari all'intera sessione app):
    /// senza, ogni pagina master-data visitata e poi abbandonata resterebbe agganciata per sempre, un leak
    /// piccolo ma inutile dato che ImpostazioniViewModel ricrea un'istanza nuova ad ogni navigazione.</summary>
    public virtual void Dispose()
    {
        _disposto = true; // prima di tutto: un'acquisizione del lock ancora in corso (IniziaModifica) lo rilascia
        _state.DatiRicaricati -= OnDatiRicaricati;
        Unione.OperazioneCompletata -= Ricarica;
        _ricercaCts?.Cancel();
        _ricercaCts?.Dispose();
        _ricercaCts = null;
        // Uscendo dalla pagina in modifica (cambio sotto-sezione/navigazione: ImpostazioniViewModel e
        // MainViewModel.ImpostaCurrentPage chiamano Dispose) il lock va rilasciato, altrimenti gli altri PC
        // vedono "in modifica da X" fino alla scadenza. Ordine: prima si spegne la sessione di modifica (il
        // flag blocca una callback dell'heartbeat gia' in volo), poi Release (solo con il nostro SessionId).
        // Dispose dei nested (Gamme/Categorie) e' chiamato dal Dispose del parent.
        var inModifica = InModifica;
        var sessione = FermaHeartbeat();
        if (inModifica || RilascioLockInAttesa)
        {
            lock (_statoSessioneLockGate)
                sessione ??= _sessioneLockRilascioInAttesa;

            if (!string.IsNullOrWhiteSpace(sessione))
                _ = AvviaRilascioSessioneEsatta(_filePath, sessione);
            else
                CrashLogService.Registra("Dispose editor master senza SessionId esatto per il rilascio lock", new InvalidOperationException(_filePath));
        }
    }

    private string Chiave(T item) => _keyOf(item) ?? string.Empty;

    /// <summary>Chiave (Nome/Id) con cui ciascuna riga PREESISTENTE si trovava all'ultimo Ricarica()/Salva(),
    /// tenuta per riferimento d'oggetto (non per valore, a differenza di _snapshotPerChiave): serve solo a
    /// SoloRigheModificateTramiteRinomina per distinguere "questa riga è stata rinominata scrivendo direttamente
    /// nella cella Nome della griglia" da "questa è una riga nuova, il suo Nome può ancora cambiare liberamente
    /// prima del primo Salva". Ripopolata per intero a ogni AggiornaSnapshotDaItems, mai accumulata tra un giro
    /// e l'altro (nessun leak su cataloghi grandi/sessioni lunghe).</summary>
    private readonly Dictionary<T, string> _chiaveInizialePerRiferimento = new();

    private void AggiornaSnapshotDaItems()
    {
        _snapshot = Clona(Items);
        _snapshotPerChiave.Clear();
        foreach (var item in _snapshot)
            _snapshotPerChiave.TryAdd(Chiave(item), item);

        _chiaveInizialePerRiferimento.Clear();
        foreach (var item in Items)
            _chiaveInizialePerRiferimento[item] = Chiave(item);
        AggiornaVociUnioneDisponibili();
    }

    /// <summary>Aggiorna le voci disponibili per il picker "Unisci in..." con TUTTE le voci attive del catalogo,
    /// anche quando la vista è paginata e Items contiene solo la pagina corrente (bug utente: il combo "Unisci
    /// in" restava vuoto/incompleto sui cataloghi ad alta crescita perché Ricarica() usciva prima di
    /// AggiornaSnapshotDaItems quando la visualizzazione paginata si attivava). Usa una lettura leggera per
    /// sole chiavi (nessuna deserializzazione JSON riga per riga) quando il catalogo è paginato; ripiega sulle
    /// righe già in memoria se il catalogo non è paginato o se la lettura leggera fallisce (degrado di
    /// completezza, mai un crash).</summary>
    private void AggiornaVociUnioneDisponibili()
    {
        if (_visualizzazionePaginata && _pagedRead != null)
        {
            // Lettura lazy: le chiavi di tutto il catalogo servono al picker solo quando l'utente sceglie una riga, non a
            // ogni apertura dell'editor (con 50k voci erano decine di ms sull'apertura). Il degrado in caso di errore
            // resta la pagina corrente, catturata adesso: stesso contenuto del vecchio fallback immediato.
            var sorgente = _pagedRead;
            var paginaCorrente = Items.Select(Chiave).ToList();
            Unione.AggiornaVociDisponibiliDaFornitore(() =>
            {
                try { return sorgente.ReadAllActiveKeys(); }
                catch (Exception ex)
                {
                    CrashLogService.Registra($"Voci Unisci master {typeof(T).Name}: fallback su pagina corrente", ex);
                    return paginaCorrente;
                }
            });
            return;
        }
        Unione.AggiornaVociDisponibili(Items.Select(Chiave));
    }

    private void ApplicaListaAutorevole(IEnumerable<T> items)
    {
        Items.ReplaceWith(items);
        RicalcolaItemsAttivi();
    }

    private void Ricarica()
    {
        // Una ricarica durante la modifica (per esempio al termine di un Unisci avviato prima della modifica) riscriverebbe
        // le righe digitate: le si conserva e si avvisa. La lista aggiornata arriva a fine modifica (Salva/Annulla).
        if (InModifica)
        {
            SegnalaDatiAggiornatiDuranteModifica();
            return;
        }
        ChiudiAvvisoDatiAggiornati();
        if (ProvaAttivaVisualizzazionePaginata(refreshIndice: true, reimpostaPagina: true))
        {
            AggiornaVociUnioneDisponibili();
            AggiornaStatoLock();
            return;
        }

        _visualizzazionePaginata = false;
        ApplicaListaAutorevole(_repo.Load());
        AggiornaSnapshotDaItems();
        AggiornaStatoLock();
        OnPropertyChanged(nameof(UsaPaginazione));
        OnPropertyChanged(nameof(MostraControlliPaginazione));
        // Coppie sospette per Unisci rapido (spec punto 5): ricalcolate solo sulla fotografia completa, mai
        // sulla singola pagina SQLite di un catalogo paginato a 100k — coerente con CoppieSospette di WU Status.
        if (!_visualizzazionePaginata) Unione.RicalcolaCoppieSospette(Items);
    }

    // Vero se una ricarica e' stata rinviata durante la modifica: le righe digitate restano, l'avviso resta visibile.
    private bool _datiAggiornatiDuranteModifica;

    private void SegnalaDatiAggiornatiDuranteModifica()
    {
        _datiAggiornatiDuranteModifica = true;
        Messaggio = LocalizationService.Instance.Traduci("MasterDatiAggiornatiDuranteModifica");
    }

    /// <summary>Chiude l'avviso di ricarica rinviata; il messaggio viene tolto solo se e' ancora quello dell'avviso.</summary>
    private void ChiudiAvvisoDatiAggiornati()
    {
        if (!_datiAggiornatiDuranteModifica) return;
        _datiAggiornatiDuranteModifica = false;
        if (Messaggio == LocalizationService.Instance.Traduci("MasterDatiAggiornatiDuranteModifica")) Messaggio = null;
    }

    private void RifiutaModificaPerUnioneInCorso() =>
        MessaggioErrore = LocalizationService.Instance.Traduci("MasterUnioneInCorsoModificaRifiutata");

    private void AggiornaStatoLock()
    {
        // Proprietà = sessione (SessionId), non nome operatore: un lock creato da questa sessione non è mai "di
        // un'altra postazione", e uno vivo di un'altra sessione lo è anche con lo stesso nome operatore.
        ApplicaStatoLock(_state.Locks.GetForeignActiveLock(_filePath, _state.Config.OperatoreAttivo));
    }

    /// <summary>Lettura del lock di un'altra sessione fuori dal thread UI (share SMB lento): stesso esito di
    /// <see cref="AggiornaStatoLock"/>, ma senza bloccare il dispatcher. Il chiamante applica il risultato con
    /// <see cref="ApplicaStatoLock"/> sul thread UI.</summary>
    private Task<LetturaLockAltrui> LeggiLockAltroAsync()
    {
        var percorso = _filePath;
        var operatore = _state.Config.OperatoreAttivo;
        return Task.Run(() => _state.Locks.GetForeignActiveLock(percorso, operatore));
    }

    /// <summary>Acquisizione del lock fuori dal thread UI. Separata come metodo virtuale interno solo per poter
    /// simulare nei test un share lento; in produzione è la chiamata reale a <see cref="LockService.TryAcquire"/>.</summary>
    internal virtual async Task<LockResult> AcquisisciLockAsync(string percorso, string operatore)
    {
        if (!ContestoCreazioneRapidaLockTest.Value || !TryLeggiBarrieraLockTest(out var nomeEvento, out var operatoreAtteso))
            return await Task.Run(() => _state.Locks.TryAcquireNuovaSessione(percorso, operatore));

        // Il preflight di IniziaModificaAsync è già passato e LockGate è già acquisito. Il test crea ora il
        // sidecar .lock prima che parta il tentativo reale, così non simula il risultato dell'acquisizione.
        if (!await SegnalaEAttendiBarrieraLockTestAsync(nomeEvento, "ready", "start"))
            return new LockResult();

        var risultato = await Task.Run(() => _state.Locks.TryAcquireNuovaSessione(percorso, operatore));
        if (risultato.Acquisito || !string.Equals(risultato.DetenutoDa?.Operatore, operatoreAtteso, StringComparison.Ordinal))
        {
            // In caso di configurazione errata del test, non permettere che il percorso rapido salvi una voce.
            // Se il tentativo ha acquisito il lock, rilascia solo la sua sessione esatta sotto il gate corrente.
            if (risultato.Acquisito && risultato.DetenutoDa?.SessionId is { Length: > 0 } sessione)
                await LockGate.RilasciaSessioneEsattaConRetrySottoGateAsync(
                    _state.Locks, percorso, sessione);
            return new LockResult { DetenutoDa = risultato.DetenutoDa };
        }

        // Questo segnale viene emesso solo dopo che il servizio ha restituito il lock holder creato dal test.
        // Il test mantiene in sospeso la continuation UI per esercitare Escape e il light-dismiss.
        await SegnalaEAttendiBarrieraLockTestAsync(nomeEvento, "entered", "release");
        return risultato;
    }

    private static bool TryLeggiBarrieraLockTest(out string nomeEvento, out string operatore)
    {
        nomeEvento = string.Empty;
        operatore = string.Empty;
        if (!OperatingSystem.IsWindows()) return false;

        var valore = Environment.GetEnvironmentVariable(TestLockBarrierEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(valore)) return false;
        var separatore = valore.IndexOf('|');
        if (separatore <= 0 || separatore == valore.Length - 1) return false;

        nomeEvento = valore[..separatore];
        operatore = valore[(separatore + 1)..];
        return true;
    }

    private static async Task<bool> SegnalaEAttendiBarrieraLockTestAsync(string nomeEvento, string segnale, string rilascio)
    {
        if (!OperatingSystem.IsWindows()) return false;

        using var eventoSegnale = EventWaitHandle.OpenExisting($"{nomeEvento}.{segnale}");
        using var eventoRilascio = EventWaitHandle.OpenExisting($"{nomeEvento}.{rilascio}");
        eventoSegnale.Set();
        return await Task.Run(() => eventoRilascio.WaitOne(TimeoutBarrieraLockTest));
    }

    protected static IDisposable AttivaContestoCreazioneRapidaLockTest() => new AmbitoCreazioneRapidaLockTest();

    private sealed class AmbitoCreazioneRapidaLockTest : IDisposable
    {
        private readonly bool _valorePrecedente = ContestoCreazioneRapidaLockTest.Value;
        private bool _disposto;

        public AmbitoCreazioneRapidaLockTest() => ContestoCreazioneRapidaLockTest.Value = true;

        public void Dispose()
        {
            if (_disposto) return;
            ContestoCreazioneRapidaLockTest.Value = _valorePrecedente;
            _disposto = true;
        }
    }

    private void ApplicaStatoLock(LetturaLockAltrui lettura)
    {
        switch (lettura.Stato)
        {
            case StatoLockAltrui.Occupato:
                LockDetenutoDaAltri = true;
                LockInfoTesto = string.Format(LocalizationService.Instance.Traduci("LockInModificaDaFmt"),
                    lettura.Info!.Operatore, lettura.Info.AcquisitoIl);
                break;
            case StatoLockAltrui.Sconosciuto:
                // Lettura fallita: lo stato non è "libero". Blocca l'avvio della modifica senza attribuire il lock a un
                // altro operatore (P2.2).
                LockDetenutoDaAltri = true;
                LockInfoTesto = LocalizationService.Instance.Traduci("LockStatoSconosciuto");
                break;
            default:
                LockDetenutoDaAltri = false;
                LockInfoTesto = null;
                break;
        }
    }

    /// <summary>Tiene vivo il lock durante una sessione di modifica lunga sulla tabella master data,
    /// altrimenti il timeout di inattività (20 minuti) scadrebbe comunque a prescindere dall'attività reale.</summary>
    private System.Timers.Timer? _heartbeatTimer;

    private readonly MonitorRinnovoLock _monitorRinnovo = new();
    private int _heartbeatInEsecuzione;

    // Vero soltanto mentre la sessione di modifica è aperta. FermaHeartbeat lo azzera PRIMA di ogni Release:
    // una callback del timer già in volo non può quindi ricreare un lock dopo il rilascio.
    private volatile bool _sessioneModificaAttiva;

    // Vero dopo Dispose: una IniziaModifica ancora in attesa del lock non deve lasciarlo acquisito.
    private volatile bool _disposto;

    private void AvviaHeartbeat(string sessioneLock)
    {
        if (string.IsNullOrWhiteSpace(sessioneLock))
            throw new InvalidOperationException("Acquisizione del lock master senza SessionId.");

        _heartbeatTimer?.Stop();
        _heartbeatTimer?.Dispose();
        lock (_statoSessioneLockGate)
        {
            if (_disposto) throw new ObjectDisposedException(GetType().Name);
            _generazioneSessioneLock++;
            _sessioneLockModifica = sessioneLock;
            _sessioneModificaAttiva = true;
        }
        _heartbeatTimer = new System.Timers.Timer(LockService.IntervalloHeartbeat.TotalMilliseconds) { AutoReset = true };
        _heartbeatTimer.Elapsed += (_, _) => { _ = RinnovaOInRiacquisisciLock(); };
        _heartbeatTimer.Start();
    }

    /// <summary>Callback del timer (thread pool, non deve mai lanciare): rinnova il lock proprio o lo riacquisisce
    /// in modo trasparente se era scaduto/sparito senza che nessun altro lo abbia preso. Il gate del percorso resta
    /// occupato fino al completamento dell'I/O tracciato; un callback superato rilascia solo il SessionId che ha
    /// restituito, prima di cedere il gate a una chiusura o a una nuova acquisizione.</summary>
    private async Task RinnovaOInRiacquisisciLock()
    {
        if (Interlocked.CompareExchange(ref _heartbeatInEsecuzione, 1, 0) != 0) return;
        try
        {
            string? sessioneAttesa;
            int generazione;
            lock (_statoSessioneLockGate)
            {
                if (!_sessioneModificaAttiva || _disposto) return;
                sessioneAttesa = _sessioneLockModifica;
                generazione = _generazioneSessioneLock;
            }
            if (string.IsNullOrWhiteSpace(sessioneAttesa)) return;

            LockEnsureResult? esitoDaPubblicare = null;
            string? sessioneDaPubblicare = null;
            await LockGate.EseguiAsync(_filePath, async () =>
            {
                esitoDaPubblicare = await RinnovaLockSottoGateAsync(_filePath, sessioneAttesa, generazione)
                    .ConfigureAwait(false);
                if (esitoDaPubblicare != null)
                {
                    lock (_statoSessioneLockGate) sessioneDaPubblicare = _sessioneLockModifica;
                }
            });

            if (esitoDaPubblicare == null) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                lock (_statoSessioneLockGate)
                {
                    if (!SessioneLockAttiva(sessioneDaPubblicare, generazione) || !_sessioneModificaAttiva ||
                        !StringComparer.Ordinal.Equals(_sessioneLockModifica, sessioneDaPubblicare))
                        return;
                }
                if (InModifica) RegistraEsitoRinnovo(esitoDaPubblicare);
            });
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Modifica master data (heartbeat lock)", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _heartbeatInEsecuzione, 0);
        }
    }

    /// <summary>Rinnova sotto LockGate un token catturato prima di entrare nel gate. Se la sessione si chiude o
    /// viene riaperta durante l'I/O, un eventuale token appena riacquisito viene rimosso esattamente sotto lo stesso
    /// gate, prima che un altro editor possa acquisirlo.</summary>
    private async Task<LockEnsureResult?> RinnovaLockSottoGateAsync(string percorso, string sessioneAttesa, int generazione)
    {
        lock (_statoSessioneLockGate)
            if (!SessioneLockAttiva(sessioneAttesa, generazione) || !InModifica) return null;

        var esito = RinnovaLockHeartbeatPerTest is { } rinnovaPerTest
            ? await rinnovaPerTest(_state.Locks, percorso, _state.Config.OperatoreAttivo).ConfigureAwait(false)
            : await _state.Locks.EnsureOwnedTrackedAsync(percorso, _state.Config.OperatoreAttivo).ConfigureAwait(false);

        bool sessioneChiusaDuranteRinnovo;
        lock (_statoSessioneLockGate)
        {
            sessioneChiusaDuranteRinnovo = !SessioneLockAttiva(sessioneAttesa, generazione) || !InModifica;
            if (!sessioneChiusaDuranteRinnovo && !string.IsNullOrWhiteSpace(esito.SessionId))
                _sessioneLockModifica = esito.SessionId;
        }

        if (sessioneChiusaDuranteRinnovo)
        {
            if (!string.IsNullOrWhiteSpace(esito.SessionId) &&
                (esito.Riacquisito || !StringComparer.Ordinal.Equals(esito.SessionId, sessioneAttesa)))
                await LockGate.RilasciaSessioneEsattaConRetrySottoGateAsync(
                    _state.Locks, percorso, esito.SessionId, RilascioSessioneLockPerTest, AttesaRetryLockPerTest)
                    .ConfigureAwait(false);
            return null;
        }

        return esito;
    }

    /// <summary>Controllo pre-salvataggio dell'ownership della sessione, serializzato con heartbeat e rilascio.
    /// Il comando attende l'I/O tracked e non abbandona una scrittura che potrebbe terminare dopo il rilascio o una nuova acquisizione.</summary>
    private async Task<LockEnsureResult?> RinnovaLockModificaPrimaDelSalvataggioAsync()
    {
        string? sessioneAttesa;
        int generazione;
        lock (_statoSessioneLockGate)
        {
            if (!_sessioneModificaAttiva || _disposto || string.IsNullOrWhiteSpace(_sessioneLockModifica)) return null;
            sessioneAttesa = _sessioneLockModifica;
            generazione = _generazioneSessioneLock;
        }

        LockEnsureResult? esito = null;
        await LockGate.EseguiAsync(_filePath, async () =>
        {
            esito = await RinnovaLockSottoGateAsync(_filePath, sessioneAttesa, generazione).ConfigureAwait(false);
        });
        return esito;
    }

    private bool SessioneLockAttiva(string? sessione, int generazione) =>
        !_disposto && _sessioneModificaAttiva && generazione == _generazioneSessioneLock &&
        !string.IsNullOrWhiteSpace(sessione) && StringComparer.Ordinal.Equals(_sessioneLockModifica, sessione);

    /// <summary>Interpreta l'esito di un rinnovo sul thread UI (P2.1). Un lock vivo di un'altra sessione aggiorna il banner
    /// di conflitto; un rinnovo non confermato viene registrato e, dopo la soglia, diventa un avviso persistente finché un
    /// rinnovo non torna confermato.</summary>
    private void RegistraEsitoRinnovo(LockEnsureResult esito)
    {
        switch (_monitorRinnovo.Registra(esito))
        {
            case EsitoRinnovoLock.NonPosseduto:
                if (esito.Holder is { } detentore)
                    ApplicaStatoLock(new LetturaLockAltrui(StatoLockAltrui.Occupato, detentore));
                break;
            case EsitoRinnovoLock.NonConfermato:
                CrashLogService.Registra("Modifica master data (rinnovo lock non confermato)", null);
                break;
        }
        AggiornaAvvisoLockNonConfermato();
    }

    private void AggiornaAvvisoLockNonConfermato()
    {
        AvvisoLockNonConfermato = _monitorRinnovo.AvvisoAttivo;
        AvvisoLockNonConfermatoTesto = AvvisoLockNonConfermato
            ? LocalizationService.Instance.Traduci("LockNonConfermatoSalvaOChiudi")
            : null;
    }

    private string? FermaHeartbeat()
    {
        string? sessione;
        lock (_statoSessioneLockGate)
        {
            _sessioneModificaAttiva = false;
            _generazioneSessioneLock++;
            sessione = _sessioneLockModifica;
            _sessioneLockModifica = null;
        }
        _heartbeatTimer?.Stop();
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        _monitorRinnovo.Azzera();
        AggiornaAvvisoLockNonConfermato();
        return sessione;
    }

    private Task<EsitoIo> AvviaRilascioSessioneEsatta(string percorso, string sessione) =>
        LockGate.RilasciaSessioneEsattaConRetryAsync(
            _state.Locks, percorso, sessione, RilascioSessioneLockPerTest, AttesaRetryLockPerTest);

    private void ProgrammaRilascioSessioneEsatta(string? sessione, bool dopoSalvataggio, bool mostraErrore = true)
    {
        if (string.IsNullOrWhiteSpace(sessione))
        {
            lock (_statoSessioneLockGate)
            {
                _sessioneLockRilascioInAttesa = null;
                _generazioneRilascioLock++;
            }
            RilascioLockInAttesa = true;
            RilascioSessioneLockInCorso = Task.CompletedTask;
            if (mostraErrore)
                MessaggioErrore = LocalizationService.Instance.Traduci(
                    dopoSalvataggio ? "SalvatoSbloccoLockNonRiuscito" : "LockSbloccoNonRiuscito");
            CrashLogService.Registra("Rilascio lock master senza SessionId esatto", new InvalidOperationException(_filePath));
            return;
        }

        int generazione;
        lock (_statoSessioneLockGate)
        {
            _sessioneLockRilascioInAttesa = sessione;
            generazione = ++_generazioneRilascioLock;
            _rilascioLockDopoSalvataggio = dopoSalvataggio;
        }
        RilascioLockInAttesa = true;
        RilascioSessioneLockInCorso = CompletaRilascioSessioneEsattaAsync(
            _filePath, sessione, generazione, dopoSalvataggio, mostraErrore);
    }

    private async Task CompletaRilascioSessioneEsattaAsync(
        string percorso,
        string sessione,
        int generazione,
        bool dopoSalvataggio,
        bool mostraErrore)
    {
        EsitoIo esito;
        try { esito = await AvviaRilascioSessioneEsatta(percorso, sessione).ConfigureAwait(false); }
        catch (Exception ex)
        {
            CrashLogService.Registra("Rilascio sessione lock master", ex);
            esito = EsitoIo.Errore;
        }

        if (_disposto) return;
        // Lo stato letto durante il salvataggio può descrivere il lease appena rilasciato.
        // Rileggilo fuori dal dispatcher: un lock realmente acquisito da un'altra postazione
        // o una lettura incerta restano visibili e non vengono considerati liberi.
        var statoDopoRilascio = esito == EsitoIo.Ok ? await LeggiLockAltroAsync().ConfigureAwait(false) : null;
        try
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                lock (_statoSessioneLockGate)
                {
                    if (generazione != _generazioneRilascioLock ||
                        !StringComparer.Ordinal.Equals(_sessioneLockRilascioInAttesa, sessione)) return;
                    if (esito == EsitoIo.Ok)
                    {
                        _sessioneLockRilascioInAttesa = null;
                        if (StringComparer.Ordinal.Equals(_sessioneLockModifica, sessione)) _sessioneLockModifica = null;
                    }
                }

                if (_disposto) return;
                if (esito == EsitoIo.Ok)
                {
                    if (statoDopoRilascio != null) ApplicaStatoLock(statoDopoRilascio);
                    RilascioLockInAttesa = false;
                    if (mostraErrore) MessaggioErrore = null;
                }
                else
                {
                    RilascioLockInAttesa = true;
                    if (mostraErrore)
                        MessaggioErrore = LocalizationService.Instance.Traduci(
                            dopoSalvataggio ? "SalvatoSbloccoLockNonRiuscito" : "LockSbloccoNonRiuscito");
                }
            });
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Aggiornamento stato rilascio lock master", ex);
        }
    }

    /// <summary>Avvia la modifica. Un'operazione Unisci/Rinomina/Dividi in corso la rifiuta (P2.10): la fotografia letta
    /// durante l'operazione non sarebbe coerente. Lettura del lock, acquisizione e lettura della fotografia avvengono fuori dal
    /// thread UI (uno share SMB lento può bloccare decine di secondi); applicazione dei dati e stato sul thread UI.
    /// AsyncRelayCommand senza esecuzioni concorrenti: CanExecute resta falso finché è in corso. Se il lock viene
    /// acquisito ma la modifica non parte (editor chiuso, errore di lettura), questo tentativo lo rilascia: senza
    /// mai staccare un lock creato da un'altra apertura (ReleaseIfSession).</summary>
    [RelayCommand]
    private async Task IniziaModifica()
    {
        if (RilascioLockInAttesa) return;
        if (!PuoCreare) return;
        if (Unione.IsBusy) { RifiutaModificaPerUnioneInCorso(); return; }
        LockResult? acquisito = null;
        try
        {
            ApplicaStatoLock(await LeggiLockAltroAsync());
            if (_disposto || LockDetenutoDaAltri) return;

            var risultato = await LockGate.EseguiAsync(_filePath, () => AcquisisciLockAsync(_filePath, _state.Config.OperatoreAttivo));
            if (!risultato.Acquisito)
            {
                if (!_disposto) ApplicaStatoLock(await LeggiLockAltroAsync());
                return;
            }
            acquisito = risultato;
            if (_disposto) return;

            // Letture sul thread pool: capability e stato UI vengono letti qui prima dell'await.
            var lettura = await LeggiFotografiaAsync(_pagedRead?.SupportsBoundedWrite == true, _testoRicercaApplicato, PaginaCorrente, DimensionePaginaMaster);
            if (_disposto) return;
            // Un'operazione avviata durante la lettura renderebbe la fotografia incoerente: si rinuncia come all'avvio.
            if (Unione.IsBusy) { RifiutaModificaPerUnioneInCorso(); return; }

            ApplicaFotografiaModifica(lettura);
            _modificaSeguitaAForzatura = risultato.SeguitoDaForzatura;
            AvviaHeartbeat(acquisito.DetenutoDa?.SessionId ??
                throw new InvalidOperationException("Acquisizione del lock master senza SessionId."));
            MessaggioErrore = null;
            InModifica = true;
            ChiudiAvvisoDatiAggiornati();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Modifica master data (IniziaModifica)", ex);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
        }
        finally
        {
            if (acquisito != null && !InModifica)
            {
                FermaHeartbeat(); // prima del rilascio: vedi _sessioneModificaAttiva
                await RilasciaLockDelTentativoAsync(acquisito);
            }
        }
    }

    /// <summary>Lettura della fotografia da applicare in modifica, fuori dal thread UI. Seam di test: un test può
    /// sostituirla per simulare una lettura lenta, chiusa a metà o fallita.</summary>
    internal virtual Task<FotografiaModifica> LeggiFotografiaAsync(bool bounded, string testo, long pagina, int dimensione) =>
        Task.Run(() => LeggiFotografiaModifica(bounded, testo, pagina, dimensione));

    /// <summary>Rilascia il lock acquisito da un singolo tentativo di modifica, legato alla sua sessione.</summary>
    private Task RilasciaLockDelTentativoAsync(LockResult tentativo)
    {
        var sessione = tentativo.DetenutoDa?.SessionId;
        if (string.IsNullOrEmpty(sessione)) return Task.CompletedTask;
        var percorso = _filePath;
        return AvviaRilascioSessioneEsatta(percorso, sessione);
    }

    /// <summary>Variante asincrona per i pochi chiamanti legacy che devono sapere subito se la modifica è iniziata.
    /// Stessa semantica di <see cref="IniziaModifica"/>, ma sul thread UI.</summary>
    protected async Task IniziaModificaAsincrona()
    {
        if (RilascioLockInAttesa) return;
        if (!PuoCreare) return;
        if (Unione.IsBusy) { RifiutaModificaPerUnioneInCorso(); return; }
        AggiornaStatoLock();
        if (LockDetenutoDaAltri) return;
        var risultato = await LockGate.EseguiAsync(
            _filePath,
            () => Task.Run(() => _state.Locks.TryAcquireNuovaSessione(_filePath, _state.Config.OperatoreAttivo)));
        if (!risultato.Acquisito)
        {
            AggiornaStatoLock();
            return;
        }
        try
        {
            ApplicaFotografiaModifica(LeggiFotografiaModifica(_pagedRead?.SupportsBoundedWrite == true, _testoRicercaApplicato, PaginaCorrente, DimensionePaginaMaster));
            _modificaSeguitaAForzatura = risultato.SeguitoDaForzatura;
            AvviaHeartbeat(risultato.DetenutoDa?.SessionId ??
                throw new InvalidOperationException("Acquisizione del lock master senza SessionId."));
            MessaggioErrore = null;
            InModifica = true;
            ChiudiAvvisoDatiAggiornati();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Modifica master data (IniziaModificaAsincrona)", ex);
            FermaHeartbeat(); // prima del rilascio: vedi _sessioneModificaAttiva
            var sessione = risultato.DetenutoDa?.SessionId;
            ProgrammaRilascioSessioneEsatta(sessione, dopoSalvataggio: false, mostraErrore: false);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
            AggiornaStatoLock();
        }
    }

    private bool PuoForzareSblocco() => PuoModificare;

    /// <summary>Rompe la modifica in corso di qualcun altro, non la propria. È un'azione amministrativa:
    /// soltanto un Master in workspace ancora scrivibile può eseguirla. I controlli vengono ripetuti dopo il
    /// dialog perché ruolo/stato condivisione possono cambiare mentre la conferma resta aperta.</summary>
    [RelayCommand(CanExecute = nameof(PuoForzareSblocco))]
    private async Task ForzaSblocco()
    {
        if (!PuoModificare) return;

        var info = LockInfoTesto;
        if (!await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("ForzaSbloccoTitoloConferma"),
            info != null ? string.Format(LocalizationService.Instance.Traduci("ForzaSbloccoConInfo"), info)
                         : LocalizationService.Instance.Traduci("ForzaSbloccoSenzaInfo"),
            LocalizationService.Instance.Traduci("ForzaSbloccoBtn"), LocalizationService.Instance.Traduci("Annulla"))) return;

        if (!PuoModificare) return;
        try
        {
            WorkspaceWritePolicy.EnsureCanWrite(_state.Paths, "Forzatura lock master data");
            MessaggioErrore = _state.Locks.ForceUnlock(_filePath) == EsitoIo.Ok
                ? null
                : LocalizationService.Instance.Traduci("LockSbloccoNonRiuscito");
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
        }
        AggiornaStatoLock();
    }

    /// <summary>Notifica la View subito dopo l'aggiunta di una riga, così può scorrere/selezionare la nuova
    /// riga invece di lasciarla fuori vista in fondo a una tabella lunga (Addendum redesign 2026-08).</summary>
    public event Action<T>? RigaCreata;

    [RelayCommand]
    private void Nuovo()
    {
        if (!InModifica) return;
        // Con un filtro testuale attivo la riga nuova (nome vuoto) non lo soddisferebbe e resterebbe invisibile:
        // "+Nuovo" sembrerebbe non aver fatto nulla. Il filtro viene quindi azzerato prima (in modifica il cambio
        // filtro ricalcola solo la vista sulle righe gia' in memoria, non ricarica nulla).
        if (HaFiltriAttivi) FiltroTesto = "";
        var nuovo = new T { UltimaModifica = DateTime.Now, ModificatoDa = _state.Config.OperatoreAttivo, CreatoDa = _state.Config.OperatoreAttivo };
        InizializzaNuovo(nuovo);
        nuovo.Changelog.Add(new ChangelogEntry { Data = DateTime.Now, Operatore = _state.Config.OperatoreAttivo, Campo = "(riga)", ValoreNuovo = "creata" });
        Items.Add(nuovo);
        // Un'aggiunta singola non deve ricostruire l'intera collezione attiva a 100k righe.
        ItemsAttivi.Add(nuovo);
        ElementoSelezionato = nuovo;
        RigaCreata?.Invoke(nuovo);
    }

    private static readonly string[] NomiFlagArchiviazione = { "Archiviata", "Disattivo" };

    /// <summary>Letto per riflessione sul nome della proprietà (stesso principio già usato da
    /// ProprietaSemplici/RegistraModifiche più sotto), invece di un'interfaccia dedicata: i master data usano
    /// nomi diversi per lo stesso concetto (Archiviata/Disattivo) e non condividono un'interfaccia
    /// comune, ma introdurne una solo per questo sarebbe più invasivo che leggerla così.</summary>
    private static bool EArchiviata(T item)
    {
        foreach (var nome in NomiFlagArchiviazione)
        {
            var prop = typeof(T).GetProperty(nome);
            if (prop?.PropertyType == typeof(bool)) return (bool)prop.GetValue(item)!;
        }
        return false;
    }

    private static void SetArchiviata(T item, bool valore)
    {
        foreach (var nome in NomiFlagArchiviazione)
        {
            var prop = typeof(T).GetProperty(nome);
            if (prop?.PropertyType == typeof(bool)) { prop.SetValue(item, valore); return; }
        }
    }

    /// <summary>Vero se item non esisteva ancora all'ultimo Ricarica()/Salva() — usato per distinguere, quando
    /// PuoCreare è concesso ma PuoModificare no (feedback utente 2026-08-20, punto 11: Standard su Funzionalità/
    /// componente), le righe che l'operatore può toccare (le proprie, appena create) da quelle preesistenti che restano
    /// riservate a un Master. Il lookup è O(1) anche su cataloghi molto grandi.</summary>
    private bool EInRigaNuova(T item) => !_snapshotPerChiave.ContainsKey(Chiave(item));

    /// <summary>Vero se l'operatore corrente può archiviare/eliminare QUESTA riga (feedback utente
    /// 2026-09-18: "gli standard... non possono eliminare voci create da un master o da un altro standard...
    /// dovrebbero poter creare ed eliminare esclusivamente le cose create da loro stessi"). Un Master può
    /// sempre (PuoModificare, invariato). Chi ha solo PuoCreare (Standard su Componente/Veicoli/Strumenti/
    /// Funzionalità) può anche su una riga preesistente, ma SOLO se l'ha creata lui: CreatoDa vuoto (righe
    /// esistenti prima di questa modifica, nessuna attribuzione retroattiva possibile) o di un altro operatore
    /// non passa mai — l'errore resta cautelativo, non permissivo.</summary>
    private bool PuoEliminareRiga(T item) =>
        PuoModificare || EInRigaNuova(item) ||
        (PuoCreare && !string.IsNullOrWhiteSpace(item.CreatoDa) && item.CreatoDa.Equals(_state.Config.OperatoreAttivo, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sposta una riga in Archivio: sparisce da ItemsAttivi (quindi dalla tabella di lavoro), resta
    /// in Items/su disco al prossimo Salva (feedback utente 2026-08-18, punto 6). Su una riga preesistente
    /// richiede PuoModificare (Master), non solo PuoCreare — chi può solo creare non può archiviare/eliminare
    /// voci che non ha aggiunto lui.
    /// Rimozione mirata invece di RicalcolaItemsAttivi() (feedback utente 2026-08-25): un ricalcolo completo fa
    /// Clear()+riaggiungi tutto, che azzera lo scroll della DataGrid e riporta sempre in cima alla lista. La
    /// riga archiviata non può mai ricomparire nel filtro corrente (EArchiviata la esclude sempre), quindi un
    /// semplice Remove è equivalente al ricalcolo ma senza il reset della collezione.</summary>
    [RelayCommand]
    private void Archivia(T? item)
    {
        if (!InModifica || item == null || EArchiviata(item)) return;
        if (!PuoEliminareRiga(item))
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataSoloCreazioneConsentita");
            return;
        }
        SetArchiviata(item, true);
        ItemsAttivi.Remove(item);
    }

    /// <summary>Riporta in tabella una riga archiviata (usato dal pannello Archivio unificato, che pilota
    /// IniziaModifica/Ripristina/Salva su questa stessa istanza invece di duplicare la logica di lock/
    /// changelog per ogni tipo di master data).</summary>
    [RelayCommand]
    private void Ripristina(T? item)
    {
        if (!InModifica || item == null || !EArchiviata(item)) return;
        SetArchiviata(item, false);
        RicalcolaItemsAttivi();
    }

    /// <summary>Vero se item è ancora referenziato da qualche TestCase (o da un altro master data) e quindi
    /// non eliminabile definitivamente. Fail-closed di default (Addendum: grill-me 2026-08-18, terza
    /// follow-up, punto 4): finché una sottoclasse non dichiara esplicitamente come si verifica il
    /// riferimento per il proprio tipo T, EliminaDefinitivamente resta bloccato invece di rischiare
    /// un'eliminazione silenziosamente non sicura.</summary>
    protected virtual bool HaRiferimenti(T item, IReadOnlyList<TestCase> testCases) => true;

    /// <summary>Testo che spiega perche' una riga referenziata non si elimina: nomina ciascun riferimento e dove trovarlo.
    /// Null (default) = messaggio generico <c>MasterDataEliminaReferenziata</c>.</summary>
    protected virtual string? DescriviRiferimenti(T item, IReadOnlyList<TestCase> testCases) => null;

    /// <summary>Elimina definitivamente una riga già archiviata (Addendum: grill-me 2026-08-18,
    /// terza follow-up, punto 4 — "dovrebbe essere possibile cancellarle definitivamente dall'archivio"):
    /// rimuove solo dalla lista in memoria, come ogni altra modifica diventa permanente solo al successivo
    /// Salva (Annulla la scarta come qualunque altra modifica in corso). Bloccata se la riga non è ancora
    /// archiviata (si archivia prima, si elimina dopo, mai direttamente da uno stato attivo) o se
    /// qualche TestCase la referenzia ancora, stesso principio del guard già usato per il cambio Tipo su
    /// CampoSistemaDefinizione, generalizzato qui per tutti i master data.</summary>
    [RelayCommand]
    private void EliminaDefinitivamente(T? item)
    {
        if (!InModifica || item == null) return;
        MessaggioErrore = null;
        if (!PuoEliminareRiga(item))
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataSoloCreazioneConsentita");
            return;
        }
        if (!EArchiviata(item))
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataEliminaNonArchiviata");
            return;
        }
        var testCases = _state.CartellaValida ? _state.TestCases.GetAll() : new List<TestCase>();
        if (HaRiferimenti(item, testCases))
        {
            MessaggioErrore = DescriviRiferimenti(item, testCases) ?? LocalizationService.Instance.Traduci("MasterDataEliminaReferenziata");
            return;
        }
        Items.Remove(item);
        ItemsAttivi.Remove(item);
    }

    /// <summary>Ordine posizionale delle colonne Excel importabili (feedback utente 2026-08-20, punto 8):
    /// colonna A dell'Excel = ColonneImportOrdine[0], B = [1], ecc. — non più intestazioni che dovevano
    /// corrispondere per nome esatto alla proprietà C# interna (es. "GammaNome" invece di "Gamma" come mostrato
    /// a schermo in Veicoli), causa più probabile del "non funziona" segnalato. Ogni sottoclasse la cui view
    /// espone davvero il pulsante "Importa Excel" (Componente/Funzionalita/Veicoli/Strumenti/Requisiti) dichiara qui
    /// l'ordine che corrisponde alle proprie colonne a schermo effettivamente editabili — le altre (Operatori,
    /// Categorie, Gamma, Tipi di test, Campi di sistema) non espongono quel pulsante, il default vuoto le
    /// lascia semplicemente inoperanti se mai richiamate.</summary>
    protected virtual IReadOnlyList<string> ColonneImportOrdine => Array.Empty<string>();

    /// <summary>Validazione atomica delle righe importate. Se almeno una riga restituisce errori, l'intero
    /// import viene bloccato prima di modificare Items, così l'utente non resta con un'importazione parziale.</summary>
    protected virtual IReadOnlyList<string> ValidaRigaImport(T item) => Array.Empty<string>();

    /// <summary>Consente ai master molti-a-molti (per esempio componente→Veicoli) di fondere in modo additivo
    /// righe Excel con la stessa chiave. Il default conserva la regola storica: duplicati saltati.</summary>
    protected virtual bool ProvaUnisciRigaImport(T destinazione, T sorgente) => false;

    /// <summary>Import massivo da .xlsx (richiesta utente: l'app parte vuota, ricreare a mano decine di righe
    /// master data è lento). Righe che risolvono a una chiave (_keyOf) già presente vengono saltate, mai
    /// sovrascritte: un import non deve mai poter cancellare in silenzio una modifica manuale già fatta sulla
    /// stessa riga. Foglio e riga di intestazione (feedback utente 2026-08-25, punto 5) sono scelti dall'utente
    /// nel dialog ImportExcelOpzioniWindow prima di arrivare qui, non più dedotti automaticamente.</summary>
    // 6.6.0 - import Excel annullabile e report finale. Lo stato dell'import vive in editor fino a Salva: nessun file cambia prima.
    private ImportAnnullabile? _ultimoImport;
    private Func<Task>? _riprovaImport;
    private bool _ultimoImportFallito;

    /// <summary>Report finale dell'ultimo import (conteggi e stato), visibile finche' la modifica resta aperta.</summary>
    [ObservableProperty] private string? reportImport;
    partial void OnReportImportChanged(string? value) => OnPropertyChanged(nameof(HaReportImport));
    public bool HaReportImport => !string.IsNullOrEmpty(ReportImport);

    /// <summary>Vero se l'ultimo import applicato si puo' ancora annullare (righe non cambiate, modifica aperta).</summary>
    public bool PuoAnnullareUltimoImport => _ultimoImport != null && InModifica;

    /// <summary>Vero se l'ultimo tentativo di import e' fallito: la richiesta (file) resta disponibile per "Riprova import".</summary>
    public bool PuoRiprovareImport => _riprovaImport != null && _ultimoImportFallito && InModifica;

    /// <summary>Report finale dell'import: un nuovo report chiude l'eventuale stato di "non applicato".</summary>
    protected void ImpostaReportImport(string testo)
    {
        _ultimoImportFallito = false;
        ReportImport = testo;
        NotificaComandiImport();
    }

    /// <summary>Azione di "Riprova import" per l'ultimo import: riusa lo stesso file (o la stessa richiesta).</summary>
    protected void ImpostaRiprovaImport(Func<Task> riprova) => _riprovaImport = riprova;

    /// <summary>Import non applicato (nessuna riga aggiunta): mostra il report e offre "Riprova import".</summary>
    protected void SegnaImportFallito(string report)
    {
        ImpostaReportImport(report);
        _ultimoImportFallito = true;
        NotificaComandiImport();
    }

    private void NotificaComandiImport()
    {
        OnPropertyChanged(nameof(PuoAnnullareUltimoImport));
        OnPropertyChanged(nameof(PuoRiprovareImport));
    }

    /// <summary>Fine della sessione di modifica (Salva, Annulla, uscita): l'import non e' piu' annullabile e il report sparisce.</summary>
    private void AzzeraImport()
    {
        _ultimoImport = null;
        _riprovaImport = null;
        _ultimoImportFallito = false;
        ReportImport = null;
        NotificaComandiImport();
    }

    /// <summary>Import Excel applicato all'editor e non ancora salvato: righe aggiunte e righe esistenti unite con il loro stato
    /// precedente. I fingerprint di fine import servono ad "Annulla ultimo import" per verificare che nulla sia cambiato.</summary>
    private sealed class ImportAnnullabile
    {
        public required List<(T Riga, string Fingerprint)> Aggiunte { get; init; }
        public required List<(T Viva, T Prima, string Fingerprint)> Unite { get; init; }
    }

    [RelayCommand]
    private Task ImportaDaExcel(ImportExcelRichiesta? richiesta) =>
        ComandoAsincrono.EseguiAsync(() => ImportaDaExcelCoreAsync(richiesta), "Importazione da Excel", m => MessaggioErrore = m);

    private async Task ImportaDaExcelCoreAsync(ImportExcelRichiesta? richiesta)
    {
        if (!InModifica || richiesta == null || string.IsNullOrWhiteSpace(richiesta.PercorsoFile)) return;
        var loc = LocalizationService.Instance;
        // La richiesta resta disponibile per "Riprova import" anche se questo tentativo fallisce (file bloccato, righe errate).
        _riprovaImport = () => ImportaDaExcel(richiesta);
        _ultimoImportFallito = false;
        var righeLette = 0;
        var righeErrate = 0;
        var avvisiLettura = new List<string>();
        try
        {
            List<List<string>> righe;
            using (var fs = File.OpenRead(richiesta.PercorsoFile)) righe = ExcelImportService.LeggiRigheOrdinate(fs, richiesta.Foglio, richiesta.RigaIntestazione, avvisiLettura);
            righeLette = righe.Count;

            var proprietaPerNome = ProprietaSemplici().ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
            var colonne = ColonneImportOrdine;

            var candidati = new List<(int Riga, T Item)>();
            var errori = new List<string>();
            var numeroRiga = 0;
            foreach (var riga in righe)
            {
                numeroRiga++;
                var nuovo = new T { UltimaModifica = DateTime.Now, ModificatoDa = _state.Config.OperatoreAttivo, CreatoDa = _state.Config.OperatoreAttivo };
                InizializzaNuovo(nuovo);
                for (var i = 0; i < colonne.Count && i < riga.Count; i++)
                {
                    var valore = riga[i];
                    if (string.IsNullOrWhiteSpace(valore) || !proprietaPerNome.TryGetValue(colonne[i], out var prop)) continue;
                    if (prop.PropertyType == typeof(bool))
                        prop.SetValue(nuovo, valore.Trim().ToLowerInvariant() is "1" or "true" or "si" or "sì" or "x" or "yes");
                    else if (prop.PropertyType.IsEnum)
                    {
                        if (Enum.TryParse(prop.PropertyType, valore.Trim(), true, out var valoreEnum)) prop.SetValue(nuovo, valoreEnum);
                    }
                    else prop.SetValue(nuovo, valore.Trim());
                }

                var chiave = Chiave(nuovo);
                if (string.IsNullOrWhiteSpace(chiave)) errori.Add(string.Format(loc.Traduci("MasterDataRigaChiaveMancanteFmt"), numeroRiga));
                foreach (var errore in ValidaRigaImport(nuovo)) errori.Add(string.Format(loc.Traduci("MasterDataRigaErroreFmt"), numeroRiga, errore));
                candidati.Add((numeroRiga, nuovo));
            }

            if (errori.Count > 0)
            {
                // Validazione prima di toccare l'editor: un errore in una riga blocca l'intero import (nessuna riga aggiunta).
                righeErrate = errori.Count;
                throw new InvalidDataException(string.Join(Environment.NewLine, errori));
            }

            // Un dizionario evita Items.FirstOrDefault per ogni riga importata: da O(n*m) a O(n+m).
            var itemsPerChiave = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Items) itemsPerChiave.TryAdd(Chiave(item), item);

            // Anteprima non mutante (UX-4): conta nuove/saltate/unite su copie, poi chiede conferma. Le righe
            // finiscono comunque solo nell'editor: nulla è salvato finché l'utente non preme Salva.
            var anteprima = AnalizzaImport(candidati.Select(c => c.Item).ToList(), itemsPerChiave);
            if (!await ConfirmService.Chiedi(loc.Traduci("MasterDataAnteprimaExcelTitolo"),
                    string.Format(loc.Traduci("MasterDataRigheLetteFmt"), candidati.Count) + "\n" +
                    string.Format(loc.Traduci("MasterDataNuoveDaAggiungereFmt"), anteprima.Nuove) + "\n" +
                    string.Format(loc.Traduci("MasterDataGiaPresentiSaltateFmt"), anteprima.Saltate) + "\n" +
                    (anteprima.Unite > 0 ? string.Format(loc.Traduci("MasterDataGiaPresentiUniteFmt"), anteprima.Unite) + "\n" : "") +
                    (anteprima.EsempiNuove.Count > 0 ? "\n" + string.Format(loc.Traduci("MasterDataEsempiNuoveFmt"), string.Join(", ", anteprima.EsempiNuove)) + "\n" : "") +
                    "\n" + loc.Traduci("MasterDataRigheSoloEditorMsg"),
                    loc.Traduci("ImportaBtnSemplice"), loc.Traduci("Annulla")))
            {
                Messaggio = loc.Traduci("MasterDataImportazioneAnnullataMsg");
                MessaggioErrore = null;
                ImpostaReportImport(loc.Traduci("ImportReportAnnullatoUtente"));
                return;
            }
            // L'anteprima può aver caricato nella finestra righe esistenti fuori pagina: riallinea il dizionario,
            // altrimenti il ciclo sotto le ricaricherebbe una seconda volta.
            itemsPerChiave.Clear();
            foreach (var item in Items) itemsPerChiave.TryAdd(Chiave(item), item);

            var aggiunti = 0;
            var saltati = 0;
            var uniti = 0;
            // Stato precedente di ogni riga esistente toccata (catturato prima della prima unione) e righe aggiunte: servono
            // sia all'annullamento sia al ripristino se l'applicazione fallisce a meta' (import tutto-o-niente nell'editor).
            var aggiuntePerImport = new List<T>();
            var aggiunteSet = new HashSet<T>(ReferenceEqualityComparer.Instance);
            var statoPrecedente = new Dictionary<T, T>(ReferenceEqualityComparer.Instance);
            try
            {
                foreach (var (_, nuovo) in candidati)
                {
                    var chiave = Chiave(nuovo);
                    if (!itemsPerChiave.TryGetValue(chiave, out var esistente) && CaricaRigaEsistenteFuoriFinestra(chiave) is { } daIndice)
                    {
                        // Catalogo paginato in modifica: la finestra caricata non contiene tutto il catalogo, ma la chiave
                        // esiste gia' (anche archiviata). Va trattata come una riga in finestra (salta/unisci), non
                        // aggiunta come nuova: sarebbe una collisione di chiave business al Salva.
                        esistente = daIndice;
                        itemsPerChiave[chiave] = daIndice;
                    }
                    if (esistente != null)
                    {
                        if (!aggiunteSet.Contains(esistente) && !statoPrecedente.ContainsKey(esistente))
                            statoPrecedente[esistente] = Clona(new[] { esistente })[0];
                        if (ProvaUnisciRigaImport(esistente, nuovo)) uniti++;
                        else saltati++;
                        continue;
                    }
                    nuovo.Changelog.Add(new ChangelogEntry { Data = DateTime.Now, Operatore = _state.Config.OperatoreAttivo, Campo = "(riga)", ValoreNuovo = "importata da Excel" });
                    Items.Add(nuovo);
                    itemsPerChiave[chiave] = nuovo;
                    aggiuntePerImport.Add(nuovo);
                    aggiunteSet.Add(nuovo);
                    aggiunti++;
                }
            }
            catch
            {
                RiportaRighe(new HashSet<T>(aggiuntePerImport, ReferenceEqualityComparer.Instance),
                    statoPrecedente.Select(kv => (kv.Key, kv.Value)));
                throw;
            }

            RicalcolaItemsAttivi();
            MessaggioErrore = null;
            Messaggio = string.Format(loc.Traduci("ImportExcelEsitoFmt"), aggiunti, saltati)
                + (uniti > 0 ? string.Format(loc.Traduci("MasterDataRelazioniUniteFmt"), uniti) : "")
                + string.Concat(avvisiLettura.Select(avviso => " " + avviso));
            _ultimoImport = aggiuntePerImport.Count > 0 || statoPrecedente.Count > 0
                ? CreaImportAnnullabile(aggiuntePerImport, statoPrecedente)
                : null;
            ImpostaReportImport(string.Format(loc.Traduci("ImportReportExcelFmt"), candidati.Count, aggiunti, saltati, uniti, 0));
        }
        catch (Exception ex)
        {
            Messaggio = null;
            MessaggioErrore = string.Format(loc.Traduci("ImportExcelErroreFmt"), ex.Message);
            SegnaImportFallito(righeErrate > 0
                ? string.Format(loc.Traduci("ImportReportExcelErroriFmt"), righeLette, righeErrate)
                : string.Format(loc.Traduci("ImportReportExcelFallitoFmt"), ex.Message));
        }
        finally
        {
            NotificaComandiImport();
        }
    }

    private static ImportAnnullabile CreaImportAnnullabile(IReadOnlyList<T> aggiunte, IReadOnlyDictionary<T, T> statoPrecedente) => new()
    {
        Aggiunte = aggiunte.Select(r => (r, EntityStateFingerprint.Compute(r))).ToList(),
        Unite = statoPrecedente.Select(kv => (kv.Key, kv.Value, EntityStateFingerprint.Compute(kv.Key))).ToList()
    };

    /// <summary>Annulla l'ultimo import Excel applicato all'editor (non salvato): rimuove le righe che ha aggiunto e riporta le
    /// righe esistenti unite al loro stato precedente. Se una di quelle righe e' stata modificata dopo l'import non annulla
    /// nulla (nessun dato perso): le altre modifiche dell'editor restano comunque intatte.</summary>
    [RelayCommand]
    private void AnnullaUltimoImport()
    {
        var imp = _ultimoImport;
        if (imp == null || !InModifica) return;
        var loc = LocalizationService.Instance;
        var presenti = new HashSet<T>(Items, ReferenceEqualityComparer.Instance);
        var cambiate = imp.Aggiunte.Count(a => !presenti.Contains(a.Riga) || EntityStateFingerprint.Compute(a.Riga) != a.Fingerprint)
            + imp.Unite.Count(u => !presenti.Contains(u.Viva) || EntityStateFingerprint.Compute(u.Viva) != u.Fingerprint);
        if (cambiate > 0)
        {
            _ultimoImport = null;
            MessaggioErrore = string.Format(loc.Traduci("ImportUndoNonDisponibileFmt"), cambiate);
            NotificaComandiImport();
            return;
        }
        var daRimuovere = new HashSet<T>(imp.Aggiunte.Select(a => a.Riga), ReferenceEqualityComparer.Instance);
        RiportaRighe(daRimuovere, imp.Unite.Select(u => (u.Viva, u.Prima)));
        _ultimoImport = null;
        MessaggioErrore = null;
        var esito = string.Format(loc.Traduci("ImportUndoFattoFmt"), daRimuovere.Count, imp.Unite.Count);
        Messaggio = esito;
        ImpostaReportImport(esito);
        NotificaComandiImport();
    }

    [RelayCommand]
    private Task RiprovaImport() =>
        ComandoAsincrono.EseguiAsync(RiprovaImportCoreAsync, "Nuovo tentativo di importazione", m => MessaggioErrore = m);

    private async Task RiprovaImportCoreAsync()
    {
        if (PuoRiprovareImport) await _riprovaImport!();
    }

    [RelayCommand]
    private async Task CopiaReportImport()
    {
        var testo = ReportImport;
        if (string.IsNullOrEmpty(testo)) return;
        if (await ClipboardService.CopiaTestoAsync(testo))
            Messaggio = LocalizationService.Instance.Traduci("ImportReportCopiato");
        else
            MessaggioErrore = LocalizationService.Instance.Traduci("ImportReportCopiaFallita");
    }

    /// <summary>Riporta l'editor allo stato precedente: toglie le righe in <paramref name="daRimuovere"/> e riporta ogni riga
    /// unita (Viva) allo stato <c>Prima</c>, sulla stessa istanza, cosi' UI e indici per riferimento restano validi.</summary>
    private void RiportaRighe(HashSet<T> daRimuovere, IEnumerable<(T Viva, T Prima)> daRipristinare)
    {
        if (daRimuovere.Count > 0)
            Items.ReplaceWith(Items.Where(i => !daRimuovere.Contains(i)).ToList());
        foreach (var (viva, prima) in daRipristinare) CopiaStato(prima, viva);
        if (ElementoSelezionato is { } selezionato && daRimuovere.Contains(selezionato)) ElementoSelezionato = null;
        RicalcolaItemsAttivi();
    }

    /// <summary>Copia le proprieta' persistite di <paramref name="da"/> su <paramref name="a"/>. Esclude le proprieta' non
    /// serializzate (JsonIgnore) e i campi sensibili, che il clone non porta e non devono essere azzerati.</summary>
    private static void CopiaStato(T da, T a)
    {
        foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            // Solo l'ignore incondizionato: WhenWritingNull (es. VeicoliEcuD4D, Guasto) e' un campo persistito da ripristinare.
            if (p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is { Condition: System.Text.Json.Serialization.JsonIgnoreCondition.Always }) continue;
            if (NomiCampiSensibiliEsclusi.Contains(p.Name)) continue;
            p.SetValue(a, p.GetValue(da));
        }
    }

    internal sealed record AnteprimaImport(int Nuove, int Saltate, int Unite, IReadOnlyList<string> EsempiNuove);

    /// <summary>Simula l'import senza toccare le righe esistenti: le unioni sono provate su cloni.</summary>
    internal AnteprimaImport AnalizzaImport(IReadOnlyList<T> candidati, IReadOnlyDictionary<string, T> esistentiPerChiave)
    {
        var visti = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        int nuove = 0, saltate = 0, unite = 0;
        var esempi = new List<string>();
        foreach (var candidato in candidati)
        {
            var chiave = Chiave(candidato);
            if (!visti.TryGetValue(chiave, out var esistente))
            {
                esistente = esistentiPerChiave.TryGetValue(chiave, out var inFinestra) ? inFinestra
                    : CaricaRigaEsistenteFuoriFinestra(chiave);
                if (esistente != null) visti[chiave] = EntityStateFingerprint.Clone(esistente);
            }
            if (esistente == null || !visti.ContainsKey(chiave))
            {
                nuove++;
                if (esempi.Count < 5) esempi.Add(chiave);
                visti[chiave] = EntityStateFingerprint.Clone(candidato);
                continue;
            }
            if (ProvaUnisciRigaImport(visti[chiave], EntityStateFingerprint.Clone(candidato))) unite++;
            else saltate++;
        }
        return new AnteprimaImport(nuove, saltate, unite, esempi);
    }

    /// <summary>Solo in modifica bounded (catalogo paginato: Items e' una finestra da ~200 righe): cerca per chiave
    /// nell'indice la riga che la finestra non contiene. Se esiste, la aggiunge a Items e alla baseline
    /// (snapshot/chiavi iniziali) senza toccare lo snapshot delle altre righe, cosi' un'eventuale modifica
    /// resta un normale patch per UID. Null se non paginata, non trovata o indice non leggibile (in quel caso
    /// resta comunque il controllo del repository al Salva).</summary>
    protected T? CaricaRigaEsistenteFuoriFinestra(string chiave)
    {
        var esistente = TrovaInIndiceFuoriFinestra(chiave);
        if (esistente == null) return null;
        var baseline = Clona(new[] { esistente })[0];
        Items.Add(esistente);
        _snapshot = new List<T>(_snapshot) { baseline };
        _snapshotPerChiave.TryAdd(Chiave(baseline), baseline);
        _chiaveInizialePerRiferimento[esistente] = Chiave(esistente);
        return esistente;
    }

    private T? TrovaInIndiceFuoriFinestra(string chiave)
    {
        if (!_modificaPatchBounded || _pagedRead == null || string.IsNullOrWhiteSpace(chiave)) return null;
        try { return _pagedRead.TryGetByKey(chiave.Trim()); }
        catch (Exception ex)
        {
            CrashLogService.Registra($"Verifica chiave master {typeof(T).Name} fuori finestra", ex);
            return null;
        }
    }

    /// <summary>Nomi esclusi dai campi "semplici" riflessi (import Excel, changelog automatico): campi
    /// transitori di editing (NuovoPinTemp) o che non devono lasciare traccia in cronologia/essere importabili
    /// da un file esterno (PinChiaro/RecoveryCodeChiaro e i relativi blob cifrati — visibili sempre
    /// nell'editor, ma un import Excel o un changelog che li registrasse esporrebbe PIN storici oltre a quello
    /// attuale, più di quanto richiesto).</summary>
    private static readonly string[] NomiCampiSensibiliEsclusi =
        { "NuovoPinTemp", "PinChiaro", "RecoveryCodeChiaro", "PinCifrato", "RecoveryCodeCifrato" };

    private static IEnumerable<PropertyInfo> ProprietaSemplici() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.Name is not nameof(MasterEntity.Changelog) && p.Name != "ExtensionData" && !NomiCampiSensibiliEsclusi.Contains(p.Name)
                        && (p.PropertyType == typeof(string) || p.PropertyType == typeof(bool) || p.PropertyType.IsEnum));

    /// <summary>Blocca il salvataggio con un messaggio comprensibile PRIMA di arrivare al repository, se due
    /// righe della tabella hanno la stessa chiave (es. due Requisiti con lo stesso ID): senza questo controllo
    /// l'utente vedeva l'eccezione tecnica di basso livello di PhaseDMasterRepositoryEngine.ValidateBusinessKeys
    /// ("Collisione Phase D... UID ... e ..."), che non dice quale riga correggere. Stesso confronto case
    /// insensitive/trim di PhaseDMasterBusinessKeyService, così questo controllo intercetta esattamente la
    /// stessa collisione che il repository rifiuterebbe comunque.</summary>
    private bool ValidaChiaviUniche()
    {
        var viste = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
        {
            var chiave = Chiave(item).Trim();
            if (chiave.Length == 0)
            {
                // Una riga attiva senza Nome/Id non e' salvabile (chiave business vuota); un'archiviata vuota
                // e' invece innocua e non deve bloccare il salvataggio.
                if (EArchiviata(item)) continue;
                MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataNomeObbligatorio");
                return false;
            }
            if (!viste.Add(chiave) || (!_snapshotPerChiave.ContainsKey(Chiave(item)) && ChiaveEsisteFuoriFinestra(item, chiave)))
            {
                MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("MasterDataChiaveDuplicataFmt"), chiave);
                return false;
            }
        }
        return true;
    }

    /// <summary>In modifica bounded la finestra non contiene l'intero catalogo: una riga nuova (o con chiave
    /// cambiata) puo' collidere con una voce fuori finestra, che Phase D rifiuterebbe al Salva con un errore
    /// tecnico. Stessa identita' (Uid) = la riga stessa, non un duplicato.</summary>
    private bool ChiaveEsisteFuoriFinestra(T item, string chiave)
    {
        var esistente = TrovaInIndiceFuoriFinestra(chiave);
        if (esistente == null) return false;
        return !(MasterEntityIdentityService.TryGet(item, out var uidRiga)
                 && MasterEntityIdentityService.TryGet(esistente, out var uidIndice)
                 && uidRiga == uidIndice);
    }

    /// <summary>Punto di estensione per un guard extra oltre al changelog automatico (Addendum: grill-me
    /// 2026-08-18, follow-up — CampoSistemaDefinizioneViewModel lo usa per bloccare il cambio di Tipo su un
    /// campo con dati già salvati). Restituire false e valorizzare MessaggioErrore per bloccare il salvataggio;
    /// di default nessun controllo aggiuntivo.</summary>
    protected virtual bool ValidaPrimaDiSalvare() => true;

    /// <summary>Blocca il Salva() se una riga PREESISTENTE ha cambiato Nome/Id scrivendo direttamente nella
    /// cella della griglia, invece di passare da Unione.Rinomina (spec 2026-09-23, terzo giro coordinatore:
    /// "la modifica del Nome di una voce esistente in uso nella griglia deve passare dal motore di rinomina con
    /// anteprima, niente riferimenti orfani"). Una modifica diretta cambierebbe la chiave sotto ai piedi di
    /// TestCase/NonConformita/ChangeRequest che la referenziano per nome, senza aggiornarli — esattamente il
    /// riferimento orfano che l'intera funzionalità Unisci/Rinomina/Dividi esiste per evitare. Le righe NUOVE
    /// (aggiunte in questa stessa sessione con "+Nuovo"/Import Excel) non compaiono in
    /// _chiaveInizialePerRiferimento e restano libere di essere nominate come si vuole prima del primo Salva.</summary>
    private bool SoloRigheModificateTramiteRinomina()
    {
        // Il guard vale solo per i cataloghi effettivamente coperti da Unisci/Rinomina/Dividi (Componente, Veicolo,
        // Funzionalita, Gamma, Categoria, StrumentoDiagnostico, Operatore, Requisito): un catalogo non
        // registrato (es. CampoSistemaDefinizione, TestTemplate) non ha un motore di rinomina alternativo da
        // imporre, e bloccare lì la modifica diretta del Nome romperebbe un flusso di editing legittimo che
        // esisteva prima di questa funzionalità.
        if (!Unione.Disponibile) return true;
        foreach (var item in Items)
        {
            if (!_chiaveInizialePerRiferimento.TryGetValue(item, out var chiaveIniziale)) continue; // riga nuova
            var chiaveAttuale = Chiave(item);
            if (!chiaveIniziale.Equals(chiaveAttuale, StringComparison.Ordinal))
            {
                MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("MasterDataRinominaDirettaBloccataFmt"), chiaveIniziale, chiaveAttuale);
                return false;
            }
        }
        return true;
    }

    /// <summary>Vero se ogni riga preesistente è rimasta identica a com'era all'ultimo Ricarica()/Salva() e
    /// nessuna è stata rimossa (feedback utente 2026-08-20, punto 11): la vera barriera "Standard può solo
    /// creare, non toccare l'esistente" — l'IsEnabled sui pulsanti in UI è solo comodità, questo è il controllo
    /// che conta davvero, altrimenti un salvataggio potrebbe comunque persistere una modifica a una riga
    /// preesistente. Il controllo resta per contenuto ma usa lookup indicizzati, quindi è O(n) e non O(n²).</summary>
    /// <summary>Gate finale al Salva() per chi ha solo PuoCreare (feedback utente 2026-09-18: la stessa
    /// eccezione ora estesa anche ad archiviare/eliminare le proprie righe — vedi PuoEliminareRiga). Una riga
    /// preesistente modificata o sparita è ammessa SOLO se era di proprietà di questo operatore: PuoEliminareRiga
    /// applicata alla fotografia (non alla riga corrente, che potrebbe non esistere più se è stata eliminata
    /// definitivamente) verifica esattamente CreatoDa, dato che qui PuoModificare è sempre false (altrimenti
    /// Salva() non chiamerebbe affatto questo controllo) e la riga non è mai "nuova" per definizione (è nello
    /// snapshot).</summary>
    private bool SoloRigheNuoveModificate()
    {
        var chiaviCorrenti = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            var chiave = Chiave(item);
            chiaviCorrenti.Add(chiave);
            if (_snapshotPerChiave.TryGetValue(chiave, out var prima)
                && JsonSerializer.Serialize(item, JsonIo.Options) != JsonSerializer.Serialize(prima, JsonIo.Options)
                && !PuoEliminareRiga(prima))
                return false;
        }
        // Chiavi sparite (EliminaDefinitivamente ha rimosso la riga da Items): ammesse solo se la riga
        // scomparsa era mia.
        return _snapshotPerChiave.Where(kv => !chiaviCorrenti.Contains(kv.Key)).All(kv => PuoEliminareRiga(kv.Value));
    }

    /// <summary>Commit esplicito della sola finestra bounded, senza chiudere la sessione di modifica. Serve a
    /// scorrere i cataloghi grandi: una pagina gia' committata non e' piu' annullabile, ma il lock resta attivo
    /// e la pagina successiva puo' essere modificata nella stessa sessione.</summary>
    [RelayCommand]
    private async Task SalvaPagina()
    {
        if (SalvataggioInCorso) return;
        SalvataggioInCorso = true;
        try { await SalvaPaginaCoreAsync(); }
        finally { SalvataggioInCorso = false; }
    }

    private async Task SalvaPaginaCoreAsync()
    {
        if (!InModifica || !_modificaPatchBounded || _pagedRead == null) return;
        if (!PuoProcedereAlSalvataggio()) return;
        if (!HaModifiche())
        {
            Messaggio = LocalizationService.Instance.Traduci("MasterDataPaginaSenzaModifiche");
            return;
        }
        if (!PuoModificare && !PuoCreare)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataSoloCreazioneConsentita");
            return;
        }
        if (!PuoModificare && PuoCreare && !SoloRigheNuoveModificate())
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataSoloCreazioneConsentita");
            return;
        }
        var lockEsito = await RinnovaLockModificaPrimaDelSalvataggioAsync();
        if (lockEsito == null) return;
        // L'attesa del rinnovo è asincrona: ricontrolla permessi e dati sullo snapshot che verrà committato.
        if (_disposto || !InModifica || !_modificaPatchBounded || _pagedRead == null || !PuoProcedereAlSalvataggio()) return;
        if (!HaModifiche())
        {
            Messaggio = LocalizationService.Instance.Traduci("MasterDataPaginaSenzaModifiche");
            return;
        }
        if (!PuoModificare && !PuoCreare)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataSoloCreazioneConsentita");
            return;
        }
        if (!PuoModificare && PuoCreare && !SoloRigheNuoveModificate())
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataSoloCreazioneConsentita");
            return;
        }
        if (!ValidaChiaviUniche() || !SoloRigheModificateTramiteRinomina() || !ValidaPrimaDiSalvare()) return;
        RegistraEsitoRinnovo(lockEsito);

        var baseline = Clona(_snapshot);
        var daSalvare = Clona(Items);
        RegistraModifiche(daSalvare);
        try
        {
            PrimaDiPersistere(daSalvare);
            _repo.Save(daSalvare, baseline, allowSilentRemoteMerge: false, _baselineSourceFingerprint);
            DopoPersistenza(daSalvare, baseline);
        }
        catch (Exception ex)
        {
            var conflitto = ex as PhaseDMasterBusinessKeyConflictException ?? ex.InnerException as PhaseDMasterBusinessKeyConflictException;
            MessaggioErrore = conflitto != null
                ? string.Format(LocalizationService.Instance.Traduci("MasterDataChiaveDuplicataFmt"), conflitto.BusinessKey)
                : string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
            return;
        }

        Items.ReplaceWith(daSalvare);
        RicalcolaItemsAttivi();
        AggiornaSnapshotDaItems();
        try { _baselineSourceFingerprint = _repo.CatturaImpronteCatalogoCorrente(); }
        catch (Exception ex)
        {
            // Il commit e' gia' riuscito. La baseline per-UID continua a proteggere i dati, anche se non si
            // riesce a catturare il fingerprint globale per il successivo commit della stessa sessione.
            _baselineSourceFingerprint = null;
            CrashLogService.Registra($"Impronta master {typeof(T).Name} dopo salvataggio pagina", ex);
        }
        _modificaFinestraParziale = PaginaPrecedenteDisponibile || PaginaSuccessivaDisponibile;
        OnPropertyChanged(nameof(MostraAvvisoModificaLimitata));
        MessaggioErrore = null;
        Messaggio = LocalizationService.Instance.Traduci("MasterDataPaginaSalvata");
    }

    [RelayCommand]
    private async Task Salva()
    {
        if (SalvataggioInCorso) return;
        SalvataggioInCorso = true;
        try { await SalvaCoreAsync(); }
        finally { SalvataggioInCorso = false; }
    }

    private async Task SalvaCoreAsync()
    {
        if (!InModifica) return;
        if (!PuoProcedereAlSalvataggio()) return;

        // I permessi vengono rivalutati al click di Salva, non soltanto quando l'editor viene aperto. Se nel
        // frattempo il ruolo e' stato revocato, il percorso deve fallire chiuso: !PuoModificare da solo non
        // significa automaticamente "solo aggiunte", perche' puo' anche significare "nessun diritto di write".
        if (!PuoModificare && !PuoCreare)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataSoloCreazioneConsentita");
            return;
        }

        // Prima del salvataggio il lock viene rinnovato/riacquisito in modo trasparente (era potuto scadere per
        // heartbeat saltati su OneDrive). Il salvataggio non e' bloccato dal lock — revision/hash restano la
        // garanzia — ma un lock vivo di un'altra sessione viene segnalato dal banner.
        var lockEsito = await RinnovaLockModificaPrimaDelSalvataggioAsync();
        if (lockEsito == null) return;
        // L'attesa del rinnovo è asincrona: ripete autorizzazione e validazione appena prima di clonare e persistere.
        if (_disposto || !InModifica || !PuoProcedereAlSalvataggio()) return;
        if (!PuoModificare && !PuoCreare)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataSoloCreazioneConsentita");
            return;
        }
        var soloAggiunte = !PuoModificare && PuoCreare;
        if (soloAggiunte && !SoloRigheNuoveModificate())
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataSoloCreazioneConsentita");
            return;
        }
        if (!ValidaChiaviUniche() || !SoloRigheModificateTramiteRinomina() || !ValidaPrimaDiSalvare()) return;
        RegistraEsitoRinnovo(lockEsito);

        // Changelog/timestamp vengono preparati su una copia staged. Un conflitto OneDrive o un errore I/O non
        // deve sporcare gli oggetti live: l'utente mantiene esattamente le proprie modifiche editabili e puo'
        // riprovare dopo aver risolto il conflitto senza accumulare voci di changelog mai realmente persistite.
        var daSalvare = Clona(Items);
        RegistraModifiche(daSalvare);
        IReadOnlyList<T>? risultatoAutorevole = null;
        try
        {
            // Lo snapshot aperto dall'editor è una precondizione autorevole: il lock OneDrive è solo un guard
            // UX e non basta contro repliche offline/delayed. Una modifica remota non può essere sovrascritta.
            PrimaDiPersistere(daSalvare);
            _repo.Save(daSalvare, _snapshot, _modificaSeguitaAForzatura, _baselineSourceFingerprint);
            DopoPersistenza(daSalvare, _snapshot);
            if (_pagedRead == null) risultatoAutorevole = daSalvare;
        }
        catch (MasterDataConflictException) when (soloAggiunte)
        {
            try
            {
                // Gli Standard abilitati su Funzionalità/componente possono solo aggiungere. In quel caso è sicuro
                // preservare integralmente il remoto e accodare esclusivamente nuove chiavi locali distinte.
                var merged = _repo.SaveAdditionsPreservingRemote(daSalvare, _snapshot, _keyOf);
                DopoPersistenza(daSalvare, _snapshot);
                if (_pagedRead == null) risultatoAutorevole = merged;
            }
            catch (Exception mergeEx)
            {
                MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), mergeEx.Message);
                return;
            }
        }
        catch (Exception ex)
        {
            // Collisione di chiave business rilevata dal repository (es. duplicato fuori dalla finestra caricata):
            // messaggio localizzato che nomina la chiave, non l'errore tecnico Phase D.
            var conflitto = ex as PhaseDMasterBusinessKeyConflictException ?? ex.InnerException as PhaseDMasterBusinessKeyConflictException;
            MessaggioErrore = conflitto != null
                ? string.Format(LocalizationService.Instance.Traduci("MasterDataChiaveDuplicataFmt"), conflitto.BusinessKey)
                : string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
            return;
        }

        MessaggioErrore = null;
        if (_pagedRead == null)
        {
            // I cataloghi piccoli conservano esattamente il comportamento storico: la collection live viene
            // riallineata al risultato persistito e ne diventa subito il nuovo snapshot di editing.
            ApplicaListaAutorevole(risultatoAutorevole ?? daSalvare);
            AggiornaSnapshotDaItems();
        }

        var sessione = FermaHeartbeat(); // prima del rilascio: vedi _sessioneModificaAttiva
        InModifica = false;
        ProgrammaRilascioSessioneEsatta(sessione, dopoSalvataggio: true);
        ChiudiAvvisoDatiAggiornati(); // il risultato appena salvato e' la lista aggiornata

        if (_pagedRead != null)
        {
            // Nei cataloghi 100k non copiare MAI di nuovo la fotografia completa dentro ObservableCollection
            // né clonarla una seconda volta nello snapshot dopo che il commit è già riuscito. Quelle strutture
            // servivano soltanto durante la sessione di modifica e verrebbero scartate immediatamente da
            // Ricarica(). Rilasciamo prima i riferimenti grandi e torniamo direttamente alla pagina SQLite da
            // 200 righe: evita il picco post-save storico Items(100k)+snapshot(100k)+clone(100k).
            using (SospendiStatoVuoto())
            {
                Items.Clear();
                ItemsAttivi.Clear();
                _snapshot = new List<T>();
                _snapshotPerChiave.Clear();
                Ricarica();
            }
        }
        else AggiornaStatoLock();
    }

    /// <summary>Percorso "atomico" per l'Archivio unificato (feedback utente 2026-08-18, punto 6): acquisisce
    /// il lock, applica la mutazione (una Ripristina/EliminaDefinitivamenteCommand.Execute), salva e rilascia
    /// subito, senza il ciclo IniziaModifica/Annulla pensato per una sessione di modifica interattiva — Annulla
    /// mostrerebbe una conferma di abbandono modifiche fuori luogo per un'azione one-shot come questa. Ritorna
    /// null se riuscita, altrimenti il messaggio d'errore da mostrare (lock occupato, non Master, riga ancora
    /// referenziata...). Anche qui lo snapshot è una precondizione: un'azione archivio non può scavalcare una
    /// modifica remota arrivata dopo il caricamento della pagina.</summary>
    public async Task<string?> EseguiAzioneArchivioAsync(Action mutazione)
    {
        if (!PuoModificare) return LocalizationService.Instance.Traduci("ArchivioAzioneNonAutorizzata");
        AggiornaStatoLock();
        if (LockDetenutoDaAltri) return LockInfoTesto;
        var risultato = await LockGate.EseguiAsync(
            _filePath,
            () => Task.Run(() => _state.Locks.TryAcquireNuovaSessione(_filePath, _state.Config.OperatoreAttivo)));
        if (!risultato.Acquisito)
        {
            AggiornaStatoLock();
            return LockInfoTesto ?? LocalizationService.Instance.Traduci("ArchivioAzioneNonAutorizzata");
        }

        string? errore = null;
        var ripristinaDaAutorevole = false;
        MessaggioErrore = null;
        InModifica = true;
        try
        {
            mutazione();
            errore = MessaggioErrore;
            if (errore == null)
            {
                RegistraModifiche(Items);
                try
                {
                    // Azione one-shot su una singola voce (chiave/riferimento noto): il merge per-UID di Phase D
                    // resta corretto qui, la policy "Master mai automerge silenzioso" si applica solo alla
                    // sessione di modifica multi-riga (IniziaModifica/Salva), non a un'azione mirata come questa.
                    var prima = _snapshot;
                    var dopo = Items.ToList();
                    _repo.Save(dopo, _snapshot);
                    DopoPersistenza(dopo, prima);
                    AggiornaSnapshotDaItems();
                }
                catch (Exception ex)
                {
                    errore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
                    MessaggioErrore = errore;
                    ripristinaDaAutorevole = true;
                }
            }
        }
        catch (Exception ex)
        {
            errore = ex.Message;
            MessaggioErrore = errore;
            ripristinaDaAutorevole = true;
        }
        finally
        {
            InModifica = false;
            FermaHeartbeat(); // Archivio usa il token del TryAcquire, non una sessione heartbeat
            ProgrammaRilascioSessioneEsatta(risultato.DetenutoDa?.SessionId, dopoSalvataggio: false);
        }

        if (ripristinaDaAutorevole)
        {
            try { Ricarica(); }
            catch (Exception ex) { CrashLogService.Registra("Ricarica master dopo azione archivio fallita", ex); }
        }
        return errore;
    }

    /// <summary>Vero se righe sono state aggiunte/modificate dall'ultimo Ricarica(): un confronto seriale
    /// grezzo basta perché a questo punto (prima di Salva) nessuna riga toccata ha ancora ricevuto Changelog/
    /// UltimaModifica, quindi una tabella davvero intoccata produce lo stesso JSON dello snapshot. Pubblico
    /// perché ImpostazioniViewModel deve poterlo controllare prima di cambiare sotto-sezione (Addendum §18):
    /// senza, passare da questa tabella a un'altra scartava le modifiche in corso senza nessun avviso.</summary>
    public virtual bool HaModificheNonSalvate() => InModifica && HaModifiche();

    /// <summary>Vedi IEditorConModifiche.RilasciaLockSeAttivoAsync. Rilascia anche se InModifica è vero senza
    /// modifiche effettive (HaModifiche()==false): il lock è acquisito appena si entra in modifica, non solo
    /// quando qualcosa cambia davvero, quindi va rilasciato in entrambi i casi. L'uscita attende il rilascio
    /// esatto prima di chiudere il processo.</summary>
    public async Task RilasciaLockSeAttivoAsync()
    {
        if (!InModifica)
        {
            if (RilascioLockInAttesa && RilascioSessioneLockInCorso is { } rilascioGiaAvviato)
                await rilascioGiaAvviato;
            return;
        }
        try { PrimaDiAnnullare(); } catch (Exception ex) { CrashLogService.Registra("Ripristino dati collegati al rilascio del lock", ex); }
        var sessione = FermaHeartbeat(); // prima del rilascio: vedi _sessioneModificaAttiva
        InModifica = false;
        ProgrammaRilascioSessioneEsatta(sessione, dopoSalvataggio: false);
        if (RilascioSessioneLockInCorso is { } rilascio)
            await rilascio;
    }

    /// <summary>Punto di estensione chiamato da Annulla (dopo la conferma) PRIMA che InModifica diventi false: annulla gli effetti
    /// gia persistiti altrove da un Salva fallito. Ritorna false (con MessaggioErrore) per NON chiudere l'editing.</summary>
    protected virtual bool PrimaDiAnnullare() => true;

    /// <summary>Punto di estensione: dati collegati alle righe ma persistiti altrove (es. archivio guasto dei requisiti) vengono
    /// scritti qui, PRIMA del commit delle righe, sulle copie staged. Un'eccezione annulla il salvataggio.</summary>
    protected virtual void PrimaDiPersistere(List<T> daSalvare) { }

    /// <summary>Chiamato SOLO dopo l'esito OK del commit delle righe. <paramref name="snapshotPrecedente"/> e la fotografia
    /// di apertura (righe rimosse = presenti li e assenti in <paramref name="salvate"/>). Non deve lanciare.</summary>
    protected virtual void DopoPersistenza(List<T> salvate, IReadOnlyList<T> snapshotPrecedente) { }

    protected virtual bool HaModifiche() =>
        JsonSerializer.Serialize(Items.ToList(), JsonIo.Options) != JsonSerializer.Serialize(_snapshot, JsonIo.Options);

    [RelayCommand]
    private async Task Annulla()
    {
        if (OperazioneInCorso) return; // es. import guasto in corso: prima annullare/attendere l'import
        if (HaModifiche() && !await ConfirmService.ConfermaAbbandonoModifiche()) return;
        // Dati collegati gia scritti altrove (es. archivio guasto di un Salva il cui commit e fallito) vanno riportati all'originale
        // PRIMA di chiudere la sessione; se non ci si riesce l'editing resta aperto (con avviso) e la copia di lavoro e intatta.
        if (!PrimaDiAnnullare()) return;

        var sessione = FermaHeartbeat(); // prima del rilascio: vedi _sessioneModificaAttiva
        InModifica = false;
        ProgrammaRilascioSessioneEsatta(sessione, dopoSalvataggio: false);
        Ricarica();
    }

    // --- Scorciatoie da tastiera (Addendum: audit UX 2026-08-26, punto 6) ---------------------------------
    // Valgono per TUTTI gli editor master data insieme (Veicoli, componente, Operatori, Requisiti...) perche' stanno
    // sulla classe base: una tabella nuova le eredita senza dover ricordarsi di agganciarle.

    /// <summary>Ctrl+S. Il comando Salva controlla gia' InModifica, permessi e validazione: qui si filtra solo
    /// per non marcare il tasto come gestito quando non c'e' nessuna sessione di modifica aperta.</summary>
    bool IScorciatoiePagina.SalvaDaTastiera()
    {
        if (!InModifica) return false;
        _ = SalvaCommand.ExecuteAsync(null);
        return true;
    }

    /// <summary>Ctrl+N: aggiunge una riga alla tabella. Solo dentro una sessione di modifica, come il pulsante.</summary>
    bool IScorciatoiePagina.NuovoDaTastiera()
    {
        if (!InModifica) return false;
        NuovoCommand.Execute(null);
        return true;
    }

    /// <summary>Esc. Passa da AnnullaCommand, che chiede conferma se ci sono modifiche pendenti.</summary>
    bool IScorciatoiePagina.AnnullaDaTastiera()
    {
        if (!InModifica) return false;
        AnnullaCommand.Execute(null);
        return true;
    }

    private void RegistraModifiche(IEnumerable<T> items)
    {
        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite
                        && p.Name is not (nameof(MasterEntity.Changelog)) && p.Name is not (nameof(MasterEntity.UltimaModifica)) && p.Name is not (nameof(MasterEntity.ModificatoDa))
                        && p.Name != "ExtensionData" && !NomiCampiSensibiliEsclusi.Contains(p.Name)
                        && (p.PropertyType.IsPrimitive || p.PropertyType == typeof(string) || p.PropertyType == typeof(bool)
                            || p.PropertyType == typeof(DateTime) || p.PropertyType.IsEnum))
            .ToList();

        foreach (var item in items)
        {
            if (!_snapshotPerChiave.TryGetValue(Chiave(item), out var prev))
                continue; // riga nuova, changelog già registrato in Nuovo()/ImportaDaExcel().

            var cambiato = false;
            foreach (var prop in props)
            {
                var vOld = prop.GetValue(prev)?.ToString() ?? "";
                var vNew = prop.GetValue(item)?.ToString() ?? "";
                if (vOld == vNew) continue;
                item.Changelog.Add(new ChangelogEntry
                {
                    Data = DateTime.Now,
                    Operatore = _state.Config.OperatoreAttivo,
                    Campo = prop.Name,
                    ValorePrecedente = vOld,
                    ValoreNuovo = vNew
                });
                cambiato = true;
            }
            var extra = DifferenzeExtra(prev, item);
            if (extra != null)
            {
                item.Changelog.Add(new ChangelogEntry
                {
                    Data = DateTime.Now, Operatore = _state.Config.OperatoreAttivo, Campo = "Guasto",
                    ValorePrecedente = "", ValoreNuovo = extra
                });
                cambiato = true;
            }
            if (cambiato)
            {
                item.UltimaModifica = DateTime.Now;
                item.ModificatoDa = _state.Config.OperatoreAttivo;
            }
        }
    }

    private static List<T> Clona(IEnumerable<T> items) =>
        JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(items.ToList(), JsonIo.Options), JsonIo.Options) ?? new();
}
