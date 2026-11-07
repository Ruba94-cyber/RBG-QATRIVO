using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

public class StrumentoOption : ViewModelBase
{
    public string Valore { get; init; } = "";
    public string Label => Valore;
    private bool _selezionato;
    public bool Selezionato { get => _selezionato; set => SetProperty(ref _selezionato, value); }

    /// <summary>Filtrato dal campo ricerca sopra l'elenco, mostrato solo oltre 10 strumenti (feedback utente
    /// 2026-08-21): resta true finché la lista è corta, così non introduce nessun cambiamento visivo per i
    /// casi comuni.</summary>
    private bool _visibile = true;
    public bool Visibile { get => _visibile; set => SetProperty(ref _visibile, value); }

    /// <summary>Campi per operazione di questo strumento che si possono mostrare/nascondere in questo Test (menu della rotellina).</summary>
    public System.Collections.ObjectModel.ObservableCollection<VoceVistaCampo> VociCampi { get; } = new();
    private bool _haVociCampi;
    public bool HaVociCampi { get => _haVociCampi; set => SetProperty(ref _haVociCampi, value); }
    /// <summary>La rotellina apre la finestra dello strumento: serve a chi gestisce i campi oppure a chi puo' sceglierne la visibilita' nel Test.</summary>
    private bool _mostraRotellina;
    public bool MostraRotellina { get => _mostraRotellina; set => SetProperty(ref _mostraRotellina, value); }
}

public class CampoCustomRiga
{
    public string Chiave { get; set; } = "";
    public string Valore { get; set; } = "";
}

/// <summary>Parametro unico per CreaTicketCommand (RelayCommand accetta un solo argomento): cartella scelta
/// col picker nativo e sezioni scelte nel dialogo di anteprima (Addendum §3/§4, feedback QA).</summary>
public record RichiestaPacchettoPolarion(string Cartella, HashSet<string> Sezioni);

/// <summary>Un campo obbligatorio mancante rilevato al salvataggio (feedback utente 2026-08-20, punto 15):
/// Chiave è la stessa CampiSistema usata da Spotlight.TargetId ("campo-" + Chiave) sul controllo reale nella
/// view, così CampiMancantiDialog può portare l'utente direttamente lì invece di lasciargli solo il testo.</summary>
public record CampoMancante(string Messaggio, string Chiave);

/// <summary>Readonly view of one validated pasted operation; not persisted.</summary>
public sealed record RigaAnteprimaIncollaRapido(int Numero, string Funzionalita, string Nota);

/// <summary>Una riga del riepilogo componente×Funzionalità mostrato in fondo alla revisione corrente (feedback
/// utente 2026-08-25, punto 9, seconda ondata), al posto del singolo badge "esito dalla condizione peggiore"
/// di tutto il test — vedi TestCaseEditorViewModel.RiepilogoEcuFunzionalita.</summary>
public record RiepilogoEcuFunzionalitaRiga(string Componente, string Funzionalita, Esito Esito);

/// <summary>Una riga dell'anteprima di "Copia dalla precedente" (anteprima e conferma condivise da entrambe le copie). Nella copia
/// verso una nuova operazione: solo Campo e Valore. Nella copia di campi dentro un'operazione esistente (6.6.1): anche il valore
/// attuale della destinazione, la chiave "Strumento::Campo" e la spunta che decide se il campo viene copiato.</summary>
public sealed class CampoCopiaPrecedente : ObservableObject
{
    private bool _selezionato = true;

    public CampoCopiaPrecedente(string campo, string valore, string? attuale = null, string? chiave = null)
    {
        Campo = campo;
        Valore = valore;
        Attuale = attuale;
        Chiave = chiave;
    }

    /// <summary>Nome del campo mostrato (nella copia su operazione: "Strumento · Campo"; nella copia verso nuova operazione: la chiave).</summary>
    public string Campo { get; }

    /// <summary>Valore che la copia scrive (quello della precedente).</summary>
    public string Valore { get; }

    /// <summary>Valore attuale della destinazione; null nella copia verso una nuova operazione.</summary>
    public string? Attuale { get; }

    /// <summary>Chiave "Strumento::Campo" nel dizionario dell'operazione; null nella copia verso una nuova operazione.</summary>
    public string? Chiave { get; }

    /// <summary>Vero nella copia di campi dentro un'operazione esistente: mostra la spunta e il valore attuale.</summary>
    public bool EModificaOperazione => Chiave != null;

    /// <summary>Spunta: un campo non selezionato non viene copiato.</summary>
    public bool Selezionato { get => _selezionato; set => SetProperty(ref _selezionato, value); }

    /// <summary>Vero se la copia sostituirebbe un valore non vuoto e diverso: serve la conferma esplicita.</summary>
    public bool Sostituisce => EModificaOperazione && !string.IsNullOrWhiteSpace(Attuale) &&
        !string.Equals(Attuale.Trim(), Valore.Trim(), StringComparison.Ordinal);

    /// <summary>Testo della riga: "attuale → nuovo" nella copia su operazione, il solo valore altrimenti.</summary>
    public string Descrizione => EModificaOperazione
        ? $"{(string.IsNullOrWhiteSpace(Attuale) ? "(vuoto)" : Attuale)} → {Valore}{(Sostituisce ? "  (sostituisce)" : "")}"
        : Valore;
}

/// <summary>Decisione operativa su una condizione negativa effettiva. Puo restare transitoria durante la
/// compilazione, ma deve essere classificata, collegata o esclusa prima di concludere il Test.
/// Un clic (feedback utente 2026-10-10): il pulsante primario della riga collega <see cref="AnomaliaSelezionata"/>
/// se e' valorizzata (di norma l'anomalia suggerita da SuggerimentoAnomaliaService), altrimenti crea una NC nuova.
/// La pre-selezione non scrive nulla: il collegamento avviene solo al clic.</summary>
public partial class SegnalazioneTestRiga : ViewModelBase
{
    public SegnalazioneDaClassificare Segnalazione { get; init; } = null!;
    public Condizione Condizione { get; init; } = null!;
    public int Iterazione { get; init; }
    public bool EvidenziataDaRicerca { get; init; }
    public string Etichetta => EtichettaOperazione(Condizione, Iterazione);
    public string Risultato => Condizione.Nota;
    [ObservableProperty] private SeveritaAnomalia? severita;
    [ObservableProperty] private string motivazioneEsclusione = "";
    [ObservableProperty] private NonConformita? anomaliaSelezionata;
    /// <summary>Menu "Altre azioni" della riga aperto (scelta di un'altra NC, creazione esplicita, esclusione).</summary>
    [ObservableProperty] private bool altreAzioniAperte;
    public List<NonConformita> AnomalieCandidate { get; init; } = new();
    public bool PuoCollegareAnomaliaEsistente { get; init; } = true;
    /// <summary>Suggerimento di SuggerimentoAnomaliaService; null = nessuna anomalia chiaramente uguale.</summary>
    /// <summary>Si ricalcola mentre l'operatore compila l'operazione (vedi AggiornaSuggerimentiSegnalazioni).</summary>
    [ObservableProperty] private SuggerimentoAnomalia? suggerimento;
    /// <summary>Uid dell'anomalia pre-selezionata automaticamente. Se la selezione corrente ha un altro Uid, l'ha scelta
    /// l'operatore a mano e il ricalcolo non la tocca.</summary>
    public Guid? AnomaliaAutomaticaUid { get; set; }
    public bool Esclusa => Segnalazione.Stato == StatoSegnalazione.Esclusa;

    /// <summary>Vero quando il pulsante primario collega un'anomalia esistente invece di crearne una nuova.</summary>
    public bool AzioneCollega => !Esclusa && PuoCollegareAnomaliaEsistente && AnomaliaSelezionata != null;
    public bool AzioneCrea => !Esclusa && !AzioneCollega;

    /// <summary>Etichetta del pulsante primario: "Crea NC" oppure "Collega a NC-xxxx".</summary>
    public string EtichettaAzionePrimaria => AzioneCollega
        ? string.Format(LocalizationService.Instance.Traduci("CollegaANcFmt"), AnomaliaSelezionata!.Id)
        : LocalizationService.Instance.Traduci("CreaNcBtn");

    /// <summary>Motivo del suggerimento in chiaro. Compare solo finche' la pre-selezione e' ancora quella suggerita.</summary>
    public string TestoSuggerimento => Suggerimento == null || AnomaliaSelezionata?.Uid != Suggerimento.AnomaliaUid ? "" :
        string.Format(LocalizationService.Instance.Traduci("SuggerimentoAnomaliaFmt"), Suggerimento.AnomaliaId, TestoMotivo(Suggerimento.Motivo));
    public bool HaTestoSuggerimento => TestoSuggerimento.Length > 0;

    partial void OnAnomaliaSelezionataChanged(NonConformita? value)
    {
        OnPropertyChanged(nameof(AzioneCollega));
        OnPropertyChanged(nameof(AzioneCrea));
        OnPropertyChanged(nameof(EtichettaAzionePrimaria));
        OnPropertyChanged(nameof(TestoSuggerimento));
        OnPropertyChanged(nameof(HaTestoSuggerimento));
    }

    partial void OnSuggerimentoChanged(SuggerimentoAnomalia? value)
    {
        OnPropertyChanged(nameof(TestoSuggerimento));
        OnPropertyChanged(nameof(HaTestoSuggerimento));
    }

    public static string EtichettaOperazione(Condizione condizione, int iterazione) =>
        string.IsNullOrWhiteSpace(condizione.Funzionalita) ? string.Format(LocalizationService.Instance.Traduci("TcOperazioneNumeroFmt"), iterazione) : condizione.Funzionalita;

    private static string TestoMotivo(MotivoSuggerimentoAnomalia motivo) => LocalizationService.Instance.Traduci(motivo switch
    {
        MotivoSuggerimentoAnomalia.StessaOperazione => "MotivoSuggerimentoStessaOperazione",
        MotivoSuggerimentoAnomalia.StessaEcuFunzionalita => "MotivoSuggerimentoEcuFunzionalita",
        _ => "MotivoSuggerimentoEcuFunzionalitaDescrizione"
    });
}

public partial class TestCaseEditorViewModel : ViewModelBase, IDisposable, IScorciatoiePagina
{
    private readonly AppState _state;
    /// <summary>Read-only access for the Test's TICKET handoff UI; persistence stays in repositories.</summary>
    public AppState PolarionHandoffState => _state;
    private readonly Action _tornaAllaLista;
    private readonly Action<string?> _navigaATest;

    /// <summary>Vero solo durante il costruttore, fino alla chiamata esplicita finale a
    /// CaricaPannelloAnomalie() (feedback utente 2026-09-17, volumi 100k): popolare Esecuzioni fa scattare
    /// SincronizzaDaCambioEsecuzioni → SincronizzaCondizioniVisual → SincronizzaEsitoCondizioni, che richiama
    /// CaricaPannelloAnomalie ANCHE durante la costruzione — il cui risultato viene comunque sovrascritto
    /// dalla chiamata esplicita di fine costruttore. Con 100k Non Conformità quel giro in più (clone
    /// dell'intero archivio NC) raddoppiava inutilmente il tempo di apertura di un singolo test. Non altera
    /// il comportamento per ogni altro chiamante (modifica interattiva dell'Esito, Salva, ecc.), solo la
    /// primissima esecuzione nel costruttore.</summary>
    private bool _inCostruzione = true;

    /// <summary>Vero solo dentro SostituisciEsecuzioni: sospende l'handler di Esecuzioni.CollectionChanged
    /// durante la ricostruzione della collezione (vedi lì).</summary>
    private bool _sostituzioneEsecuzioniInCorso;

    /// <summary>Solo sull'istanza principale (finestra Test): permette a un test correlato aperto in
    /// visualizzazione in una finestra separata di "richiedere" la modifica, scavalcando questa istanza
    /// (feedback utente 2026-08-21 — vedi RichiediModificaCorrelato).</summary>
    private readonly Action<string>? _navigaATestInModifica;

    /// <summary>Solo sull'istanza secondaria (finestra di visualizzazione): sostituisce l'IniziaModifica
    /// normale, che altrimenti acquisirebbe un lock e modificherebbe in loco dentro una finestra pensata per
    /// restare di sola visualizzazione.</summary>
    private readonly Func<Task>? _richiediModificaEsterna;

    // Vero dopo Dispose: una IniziaModifica ancora in attesa del lock non deve lasciarlo acquisito.
    private volatile bool _disposto;
    private bool _annullamentoInCorso;
    private readonly Guid? _segnalazioneDaEvidenziareUid;
    private TestCase? _snapshot;
    // Non più readonly: AggiornaCatalogoCampiSistema lo rilegge quando il catalogo cambia mentre il Test è aperto.
    private List<CampoSistemaDefinizione> _catalogoCampiSistema;
    private readonly List<SegnalazioneDaClassificare> _segnalazioniNuove = new();
    /// <summary>SegnalazioneUid che l'operazione aveva prima che questa sessione le assegnasse una segnalazione nuova:
    /// se l'esito torna conforme prima del Salva la segnalazione nuova si scarta e l'operazione riprende il riferimento.</summary>
    private readonly Dictionary<Guid, Guid?> _segnalazioneUidPrecedente = new();
    private readonly Dictionary<Guid, long> _segnalazioniDaAggiornare = new();
    private readonly List<NonConformita> _anomalieNuove = new();
    /// <summary>NC esistenti collegate in questa sessione a una segnalazione del Test: copia modificata (con la prova
    /// di questo Test) tenuta in memoria fino al Salva. Senza questa mappa la prova non veniva mai salvata e la NC
    /// spariva dal pannello subito dopo il collegamento. Una NC collegata piu' volte ne accumula le prove qui.</summary>
    private readonly Dictionary<Guid, NonConformita> _anomalieCollegate = new();
    /// <summary>Descrittori e indice delle anomalie aperte candidate, dall'ultima ricostruzione del pannello: servono al
    /// ricalcolo dei suggerimenti mentre l'operatore compila l'operazione (vedi AggiornaSuggerimentiSegnalazioni).</summary>
    private List<CandidataAnomalia> _descrittoriCandidate = new();
    private Dictionary<Guid, NonConformita> _candidatePerUid = new();
    /// <summary>Condizioni delle righe segnalazione osservate per il ricalcolo dei suggerimenti (cambio di problema,
    /// Funzionalita o componente): riallineate a ogni ricostruzione del pannello.</summary>
    private readonly List<Condizione> _condizioniOsservate = new();
    /// <summary>Uid di NC/segnalazioni nuove già scritte da un Salva parziale (Test salvato, passo successivo
    /// fallito): il retry non le riscrive né rinotifica.</summary>
    private readonly HashSet<Guid> _salvatiInQuestoSalva = new();
    private readonly Dictionary<Guid, long> _anomalieDaAggiornare = new();
    private readonly HashSet<Guid> _anomalieDaEliminare = new();

    public ObservableCollection<NcRiga> AnomalieTestAperte { get; } = new();
    public ObservableCollection<NcRiga> AnomalieTestChiuse { get; } = new();
    public ObservableCollection<SegnalazioneTestRiga> SegnalazioniTest { get; } = new();
    public bool HaAnomalieTestAperte => AnomalieTestAperte.Count > 0;
    public bool HaAnomalieTestChiuse => AnomalieTestChiuse.Count > 0;
    public bool HaSegnalazioniTest => SegnalazioniTest.Count > 0;
    public Array StatiWorkflowAnomalia => Enum.GetValues(typeof(StatoAnomalia));

    public bool IsNew { get; }

    // === Schede solo in creazione (Addendum: audit UX 2026-08-26, punto 1) =================================
    // Su un test NUOVO l'editor mostra una scheda alla volta invece dell'intero form scorrevole — la richiesta
    // esplicita è che questo valga SOLO in creazione: aprire o modificare un test esistente deve continuare a
    // mostrare tutto insieme, perché lì serve la visione d'insieme (storico, revisioni, stato completo).
    // IsNew non cambia mai durante la vita di questo ViewModel (fissato in costruttore da testCaseId==null) —
    // Salva() chiama sempre _tornaAllaLista() al termine di un salvataggio riuscito, quindi non esiste il caso
    // "un test nuovo diventa esistente restando aperto nella stessa istanza": l'utente che vuole rivedere il
    // test appena creato lo riapre dalla lista, con IsNew=false e il form intero, come qualunque altro test.
    //
    // Le sei proprietà sotto NON duplicano nessun contenuto XAML: ogni sezione esistente resta esattamente
    // dov'è nel file, si aggiunge solo una condizione di visibilità in più. Quando !IsNew tutte tornano vere
    // (nessun cambiamento rispetto a prima di questo intervento); quando IsNew, solo quella corrispondente a
    // SchedaCorrente è vera.
    [ObservableProperty] private int schedaCorrente;

    // Quick and Full are two presentations of the same TestCase, not two storage formats.
    private const string PreferenzaModalitaEditor = "TestEditor.Mode";
    private string ChiavePreferenzaModalitaEditor => PreferenzaModalitaEditor + "|" + _state.Config.OperatoreAttivo;
    private bool _modoEditorRapido;
    private bool _rapidaPianificazione, _rapidaAllegati, _rapidaRiferimenti, _rapidaRevisioni;
    public bool ModoEditorRapido
    {
        get => _modoEditorRapido;
        set
        {
            if (!SetProperty(ref _modoEditorRapido, value)) return;
            NotificaLayoutModalitaEditor();
            // Personal, per-PC preference; never persisted in the shared TestCase or workspace.
            // A local settings I/O failure must never interrupt editing a Test.
            try { ViewStateService.Scrivi(_state, ChiavePreferenzaModalitaEditor, value ? "quick" : "full"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrashLogService.Registra("Preferenza modalità editor Test", ex);
            }
        }
    }

    [RelayCommand] private void AttivaModalitaRapida() => ModoEditorRapido = true;
    [RelayCommand] private void AttivaModalitaCompleta() => ModoEditorRapido = false;

    // Navigation reveals on-demand areas in the Quick editor, without switching the editor mode.
    public void MostraSezioneRapida(string? sezione)
    {
        if (!ModoEditorRapido || !TipoTestScelto || (sezione == "Revisioni" && IsNew)) return;
        switch (sezione)
        {
            case "Pianificazione": _rapidaPianificazione = true; break;
            case "Allegati": _rapidaAllegati = true; break;
            case "Riferimenti": _rapidaRiferimenti = true; break;
            case "Revisioni": _rapidaRevisioni = true; break;
        }
        NotificaLayoutModalitaEditor();
    }

    private void NotificaLayoutModalitaEditor()
    {
        OnPropertyChanged(nameof(MostraSchedaPianificazione));
        OnPropertyChanged(nameof(MostraSchedaDettagli));
        OnPropertyChanged(nameof(MostraSchedaProve));
        OnPropertyChanged(nameof(MostraSchedaAllegati));
        OnPropertyChanged(nameof(MostraSchedaRiferimenti));
        OnPropertyChanged(nameof(MostraSchedaRevisioni));
        OnPropertyChanged(nameof(MostraCronologiaEditor));
        OnPropertyChanged(nameof(EspandiParametriOperazione));
        OnPropertyChanged(nameof(PuoAnnullareUltimoIncollaRapido));
        OnPropertyChanged(nameof(PuoConfermareAnteprimaRapida));
        OnPropertyChanged(nameof(MostraAzioniModifica));
        OnPropertyChanged(nameof(MostraBarraSaltaSezione));
        OnPropertyChanged(nameof(MostraLinkSezioni));
        OnPropertyChanged(nameof(MostraMenuVaiA));
        OnPropertyChanged(nameof(MostraSchedeCreazione));
        NotificaCampiRapidiEssenziali();
        NotificaFlussoRapido();
    }

    /// <summary>Ultima scheda con contenuto reale durante la creazione (Addendum: audit UX 2026-08-26,
    /// correzione post-collaudo — "salva solo nel penultimo form riferimenti"): Revisioni è strutturalmente
    /// sempre vuota per un test nuovo (HaStorico richiede esecuzioni passate, il Changelog è già gated
    /// !IsNew altrove), quindi non fa parte del percorso Avanti/Indietro né della striscia schede in
    /// creazione — resta comunque una scheda vera per un test ESISTENTE (MostraBarraSaltaSezione).</summary>
    private const int UltimaSchedaCreazione = 4;

    public bool MostraSchedaPianificazione => ModoEditorRapido ? _rapidaPianificazione : !IsNew || SchedaCorrente == 0;
    public bool MostraSchedaDettagli => ModoEditorRapido ? TipoTestScelto : !IsNew || SchedaCorrente == 1;
    public bool MostraSchedaProve => ModoEditorRapido ? TipoTestScelto : !IsNew || SchedaCorrente == 2;
    public bool MostraSchedaAllegati => ModoEditorRapido ? _rapidaAllegati : !IsNew || SchedaCorrente == 3;
    public bool MostraSchedaRiferimenti => ModoEditorRapido ? _rapidaRiferimenti : !IsNew || SchedaCorrente == 4;
    public bool MostraSchedaRevisioni => ModoEditorRapido ? _rapidaRevisioni : !IsNew || SchedaCorrente == 5;
    public bool MostraCronologiaEditor => !IsNew && (!ModoEditorRapido || _rapidaRevisioni);
    private bool _parametriApertiDaValidazione;
    // I dettagli tecnici delle singole operazioni non devono occupare tutta la pagina
    // all'apertura, ne' nella modalita' completa ne' in quella rapida/read-only.
    // Il controllo Expander e' sempre apribile manualmente; la validazione puo' forzarne
    // l'apertura quando occorre completare un campo obbligatorio.
    public bool EspandiParametriOperazione => _parametriApertiDaValidazione;

    /// <summary>Reveal required technical fields when validation navigates to a missing field.
    /// Presentation only: does not mutate TestCase, revisions, locks or persisted JSON.</summary>
    public void ApriParametriPerValidazione()
    {
        if (_parametriApertiDaValidazione) return;
        _parametriApertiDaValidazione = true;
        OnPropertyChanged(nameof(EspandiParametriOperazione));
    }

    public bool PuoSchedaIndietro => SchedaCorrente > 0;
    public bool PuoSchedaAvanti => SchedaCorrente < UltimaSchedaCreazione;

    partial void OnSchedaCorrenteChanged(int value)
    {
        OnPropertyChanged(nameof(MostraSchedaPianificazione));
        OnPropertyChanged(nameof(MostraSchedaDettagli));
        OnPropertyChanged(nameof(MostraSchedaProve));
        OnPropertyChanged(nameof(MostraSchedaAllegati));
        OnPropertyChanged(nameof(MostraSchedaRiferimenti));
        OnPropertyChanged(nameof(MostraSchedaRevisioni));
        OnPropertyChanged(nameof(PuoSchedaIndietro));
        OnPropertyChanged(nameof(PuoSchedaAvanti));
        SchedaIndietroCommand.NotifyCanExecuteChanged();
        SchedaAvantiCommand.NotifyCanExecuteChanged();
        // Salva compare solo sull'ultima scheda con contenuto reale in creazione (vedi MostraAzioniModifica):
        // cambiare scheda può farlo apparire/sparire, non solo InModifica/TipoTestScelto come prima.
        OnPropertyChanged(nameof(MostraAzioniModifica));
    }

    [RelayCommand]
    private void VaiAScheda(string? indice)
    {
        if (int.TryParse(indice, out var i)) SchedaCorrente = Math.Clamp(i, 0, IsNew ? UltimaSchedaCreazione : 5);
    }

    /// <summary>Avanti/Indietro (Addendum: audit UX 2026-08-26, correzione post-collaudo — "dovrebbe propormi
    /// anche avanti indietro come pulsanti per spostarmi nel form"): la striscia schede da sola richiedeva di
    /// puntare l'header giusto ogni volta; compilando in sequenza è più naturale avanzare di un passo alla
    /// volta. Nessuna nuova logica di visibilità/validazione: spostano solo SchedaCorrente, stessa strada di
    /// un clic sull'header.</summary>
    [RelayCommand(CanExecute = nameof(PuoSchedaIndietro))]
    private void SchedaIndietro() => SchedaCorrente = Math.Max(0, SchedaCorrente - 1);

    [RelayCommand(CanExecute = nameof(PuoSchedaAvanti))]
    private void SchedaAvanti() => SchedaCorrente = Math.Min(UltimaSchedaCreazione, SchedaCorrente + 1);

    /// <summary>Distinto da IsNew (che resta vero per tutta la sessione dell'editor, usato per i suggerimenti
    /// di duplicato/correlato e altre logiche "sessione iniziata come nuovo test"): questo invece traccia se
    /// il record esiste già su disco ADESSO, per non passare isNew:true una seconda volta a
    /// TestCaseRepository.Save dopo un salvataggio implicito anticipato (vedi AssicuraTestSalvato) — altrimenti
    /// il controllo id-già-esistente rigenererebbe un nuovo Id, orfanizzando il file appena scritto.</summary>
    private bool _salvatoSuDisco;

    [ObservableProperty] private TestCase testCase = null!;

    /// <summary>TestCase.Id è "" (non null) per un test nuovo finché Salva() non genera l'ID vero — un
    /// binding diretto su TestCase.Id con TargetNullValue non scattava mai, lasciando il titolo pagina
    /// vuoto per tutta la creazione del test invece di mostrare "Nuovo test".</summary>
    public string TitoloPagina => string.IsNullOrEmpty(TestCase.Id) ? LocalizationService.Instance.Traduci("NuovoTestTitolo") : TestCase.Id;

    /// <summary>Gli allegati vivono nella cartella condivisa sotto allegati/&lt;componente&gt;/&lt;Id&gt;/, e il
    /// pacchetto Ticketing ha bisogno dello stesso Id per i nomi file/manifest, quindi entrambi richiedono
    /// almeno il componente — l'Id, se ancora mancante, viene assegnato e il test salvato in silenzio al primo
    /// allegato o pacchetto Ticketing (vedi AssicuraTestSalvato), non è più un requisito separato da comunicare
    /// all'utente (feedback utente 2026-08-19, punti 1 e 2 — prima richiedevano un salvataggio manuale
    /// esplicito prima).</summary>
    public bool TestRichiedeEcu => TestCase.Componente.Count == 0;

    /// <summary>Spiega nel tooltip perché "Prepara dati per Ticketing" è disabilitato (feedback utente
    /// 2026-08-19, punto 2 — prima il bottone era semplicemente disabilitato su un test nuovo, senza dire
    /// perché); null quando il bottone è già abilitato, nessun tooltip da mostrare.</summary>
    public string? PreparaPolarionTooltip => TestRichiedeEcu ? LocalizationService.Instance.Traduci("EcuRichiestaPrima") : null;

    /// <summary>Assegna un Id e salva subito il test se non esiste ancora su disco, così un allegato può
    /// essere aggiunto fin dal primo momento invece di richiedere un salvataggio manuale prima (feedback
    /// utente 2026-08-19, punto 1). Idempotente: no-op se il test è già su disco. Ritorna false (e valorizza
    /// ErroriValidazione) solo se manca il componente, l'unico dato davvero indispensabile per sapere dove salvare.</summary>
    private bool AssicuraTestSalvato()
    {
        if (_salvatoSuDisco) return true;
        if (TestCase.Componente.Count == 0)
        {
            ErroriValidazione = new List<string> { LocalizationService.Instance.Traduci("EcuRichiestaPrima") };
            return false;
        }
        var iniziali = IdGenerator.IniziliDa(_state.Config.OperatoreAttivo);
        TestCase.Id = IdGenerator.Genera(_state.TestCases.GetAllIds(true), iniziali);
        TestCase.UltimaModifica = DateTime.Now;
        TestCase.ModificatoDa = _state.Config.OperatoreAttivo;
        _state.TestCases.Save(TestCase, isNew: true, null);
        _salvatoSuDisco = true;
        // Come per un test esistente aperto in modifica: senza uno snapshot qui, il Salva "vero" successivo
        // passerebbe revisioneAttesa=null (nessun controllo conflitti) e RegistraChangelog() non avrebbe nulla
        // da confrontare (esce subito se _snapshot è null).
        _snapshot = Clona(TestCase);
        OnPropertyChanged(nameof(MostraNotaPianificazione));
        OnPropertyChanged(nameof(TitoloPagina));
        OnPropertyChanged(nameof(TestRichiedeEcu));
        OnPropertyChanged(nameof(PreparaPolarionTooltip));
        return true;
    }

    [ObservableProperty] private bool inModifica;
    [ObservableProperty] private bool salvataggioInCorso;
    public bool InterazioneEditorAbilitata => !SalvataggioInCorso && !_chiusuraInCorso;
    partial void OnSalvataggioInCorsoChanged(bool value) => OnPropertyChanged(nameof(InterazioneEditorAbilitata));
    [ObservableProperty] private bool rilascioLockInAttesa;
    [ObservableProperty] private string? messaggioRilascioLock;
    [ObservableProperty] private bool rilascioLockCatalogoInAttesa;
    [ObservableProperty] private string? messaggioRilascioLockCatalogo;
    private bool _rilascioLockDopoSalvataggio;
    private bool _tornaAllaListaDopoRilascio;
    private string? _percorsoRilascioSessioneLockInAttesa;
    private string? _sessioneRilascioLockInAttesa;
    private string? _percorsoRilascioLockCatalogoInAttesa;
    private string? _sessioneRilascioLockCatalogoInAttesa;
    private readonly object _statoAcquisizioneLockGate = new();
    private bool _acquisizioneLockInCorso;
    private string? _percorsoLockSessioneAcquisita;
    private string? _sessioneLockAcquisita;
    private TaskCompletionSource? _iniziaModificaInCorsoPerUscita;
    private TaskCompletionSource? _promozioneCampoCustomInCorsoPerUscita;
    private TaskCompletionSource? _salvataggioInCorsoPerUscita;
    private TaskCompletionSource? _annullamentoInCorsoPerUscita;
    private bool _chiusuraInCorso;
    internal Task? RilascioLockDopoDisposePerTest { get; private set; }
    internal Func<string, string, Task<LockResult>>? AcquisisciLockPerTest { get; set; }
    internal Func<LockService, string, string, EsitoIo>? RilascioSessioneLockPerTest { get; set; }
    internal Func<TimeSpan, Task>? AttesaRetryLockPerTest { get; set; }
    internal Func<LockService, string, string, Task<LockEnsureResult>>? RinnovaLockHeartbeatPerTest { get; set; }
    internal Func<LockService, string, string, Task<LockEnsureResult>>? RinnovaLockPreSalvataggioPerTest { get; set; }
    partial void OnRilascioLockInAttesaChanged(bool value) => OnPropertyChanged(nameof(MostraPulsanteModifica));
    [ObservableProperty] private string? lockInfoTesto;
    [ObservableProperty] private bool lockDetenutoDaAltri;
    [ObservableProperty] private bool modificheNonSalvate;
    partial void OnModificheNonSalvateChanged(bool value)
    {
        if (!value && (_copiaPrecedenteUndo != null || _annulloCampiCopia != null))
        {
            _copiaPrecedenteUndo = null;
            _fingerprintCopiaPrecedenteUndo = null;
            _fingerprintTestPrimaCopiaPrecedente = null;
            _annulloCampiCopia = null;
            _destinazioneAnnulloCampiCopia = null;
            OnPropertyChanged(nameof(PuoAnnullareCopiaPrecedente));
        }
    }

    /// <summary>Banner non bloccante (6.4.1, concorrenza multi-PC): vero quando è visibile — arrivato via sync
    /// OneDrive/SharePoint — un lock/heartbeat ATTIVO di un'altra postazione/operatore sullo stesso Test. Non
    /// scatta mai per il proprio lock (stesso operatore E stessa postazione) né per lock scaduti (le regole di
    /// scadenza restano quelle di <see cref="LockService"/>). A differenza di <see cref="LockDetenutoDaAltri"/>
    /// non impedisce di continuare a modificare: serve solo a invitare a coordinarsi prima di salvare.</summary>
    [ObservableProperty] private bool avvisoAltraPostazioneVisibile;
    // Avviso persistente del lock non confermato (P2.1): compare dopo MonitorRinnovoLock.SogliaAvviso rinnovi falliti di seguito.
    [ObservableProperty] private bool avvisoLockNonConfermato;
    [ObservableProperty] private string? avvisoLockNonConfermatoTesto;
    private readonly MonitorRinnovoLock _monitorRinnovo = new();
    [ObservableProperty] private string? avvisoAltraPostazioneTesto;

    /// <summary>Il resto del form (e i pulsanti Salva) non ha senso finché, in un test nuovo, il passo 1 del
    /// wizard (scelta del Tipo test) non è stato completato — vedi TipoTestScelto più sotto. In creazione
    /// (Addendum: audit UX 2026-08-26, correzione post-collaudo — "salva solo nel penultimo form riferimenti")
    /// Salva compare solo sull'ultima scheda con contenuto reale (Riferimenti): con una scheda alla volta a
    /// video, lasciarlo visibile su ogni scheda invitava a salvare a metà compilazione senza aver ancora
    /// visto Allegati/Riferimenti. Su un test esistente il comportamento resta quello di sempre (nessuna
    /// scheda, form intero, Salva sempre visibile durante la modifica).</summary>
    public bool MostraAzioniModifica => InModifica && TipoTestScelto && (ModoEditorRapido || !IsNew || SchedaCorrente == UltimaSchedaCreazione);
    public bool MostraPulsanteModifica => !InModifica && !RilascioLockInAttesa;
    partial void OnInModificaChanged(bool value)
    {
        OnPropertyChanged(nameof(EspandiParametriOperazione));
        OnPropertyChanged(nameof(PuoAnnullareUltimoIncollaRapido));
        OnPropertyChanged(nameof(PuoConfermareAnteprimaRapida));
        OnPropertyChanged(nameof(MostraAzioniModifica));
        OnPropertyChanged(nameof(MostraPulsanteModifica));
        OnPropertyChanged(nameof(PuoModificareContenuto));
        CopiaCampiDallaPrecedenteOperazioneCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(MostraBloccoAggiungiDtc));
        // matricola/Baseline dipendono da PuoModificareContenuto: senza questa notifica, aprendo un test esistente in
        // modifica il campo matricola restava disabilitato fino al primo Salva (matricola obbligatorio con stato Concluso /
        // Da ritestare impostato subito -> 'matricola mancante' anche se compilabile dall'utente).
        OnPropertyChanged(nameof(PuoModificareCampiRevisione));
        if (value)
        {
            // I campi costruiti in sola lettura non materializzano i default. Ricostruendoli quando si entra
            // davvero in modifica, i default vengono applicati solo alla revisione corrente editabile.
            AggiornaCampiStrumenti();
            NotificaBloccoAggiungiDtc();
        }
    }

    [ObservableProperty] private List<string> avvisi = new();
    [ObservableProperty] private List<string> erroriValidazione = new();

    /// <summary>Chiusura manuale dei banner (feedback utente: restavano fissi e non cancellabili). Gli errori di
    /// validazione sono chiudibili ma ricompaiono al successivo Salva se il problema persiste.</summary>
    [RelayCommand] private void RimuoviAvviso(string? testo) { if (testo != null) Avvisi = Avvisi.Where(a => a != testo).ToList(); }

    /// <summary>Errore di un'azione del Test (P4 2): resta tra gli avvisi finché l'utente non lo chiude, senza scadenza.</summary>
    private void AggiungiAvvisoErrore(string messaggio) => Avvisi = Avvisi.Append(messaggio).ToList();

    /// <summary>Explicit three-language feedback when a Quick editor clipboard import has no image.</summary>
    public void SegnalaScreenshotNonDisponibile() =>
        AggiungiAvvisoErrore(LocalizationService.Instance.Scegli(
            "Gli appunti non contengono un'immagine. Copia prima uno screenshot, poi riprova.",
            "The clipboard contains no image. Copy a screenshot first, then try again.",
            "Le presse-papiers ne contient pas d'image. Copiez d'abord une capture d'écran, puis réessayez."));

    /// <summary>Show an actionable warning; never surface any dropped file path or attachment contents.</summary>
    public void SegnalaAllegatiRapidiNonValidi(bool troppiFile) =>
        AggiungiAvvisoErrore(troppiFile
            ? LocalizationService.Instance.Scegli(
                "Puoi trascinare al massimo 20 file per volta.",
                "You can drop at most 20 files at once.",
                "Vous pouvez déposer au maximum 20 fichiers à la fois.")
            : LocalizationService.Instance.Scegli(
                "Uno o più allegati non sono file locali o hanno un formato non supportato. Verifica la selezione e riprova.",
                "Some attachments are not local files or have an unsupported format. Check the selection and try again.",
                "Certaines pièces jointes ne sont pas des fichiers locaux ou ont un format non pris en charge. Vérifiez la sélection puis réessayez."));
    [RelayCommand] private void RimuoviErroreValidazione(string? testo) { if (testo != null) ErroriValidazione = ErroriValidazione.Where(e => e != testo).ToList(); }

    private Task<EsitoIo> AvviaRilascioSessioneEsatta(string percorso, string sessione) =>
        LockGate.RilasciaSessioneEsattaConRetryAsync(
            _state.Locks, percorso, sessione, RilascioSessioneLockPerTest, AttesaRetryLockPerTest);

    private async Task<EsitoIo> RilasciaLockSessioneAttivaAsync()
    {
        string? percorso;
        string? sessione;
        lock (_statoAcquisizioneLockGate)
        {
            var haLockDaRilasciare = RilascioLockInAttesa
                || !string.IsNullOrWhiteSpace(_percorsoRilascioSessioneLockInAttesa)
                || !string.IsNullOrWhiteSpace(_sessioneRilascioLockInAttesa)
                || !string.IsNullOrWhiteSpace(_percorsoLockSessioneAcquisita)
                || !string.IsNullOrWhiteSpace(_sessioneLockAcquisita);
            // Un editor di sola lettura non possiede un lease. Navigare fuori da quella vista non deve
            // trasformare l'assenza di SessionId in un rilascio fallito (e bloccare il ritorno alla lista).
            if (!haLockDaRilasciare) return EsitoIo.Ok;

            percorso = _percorsoRilascioSessioneLockInAttesa ?? _percorsoLockSessioneAcquisita ?? PercorsoFile;
            sessione = _sessioneRilascioLockInAttesa ?? _sessioneLockAcquisita;
        }
        if (string.IsNullOrWhiteSpace(percorso)) return EsitoIo.Ok;
        if (string.IsNullOrWhiteSpace(sessione))
        {
            CrashLogService.Registra("Rilascio lock test senza SessionId esatto", new InvalidOperationException(percorso));
            lock (_statoAcquisizioneLockGate)
            {
                _percorsoRilascioSessioneLockInAttesa = percorso;
                _sessioneRilascioLockInAttesa = null;
            }
            return EsitoIo.Errore;
        }

        EsitoIo esito;
        try { esito = await AvviaRilascioSessioneEsatta(percorso, sessione); }
        catch (Exception ex)
        {
            CrashLogService.Registra("Rilascio lock test sessione attiva", ex);
            esito = EsitoIo.Errore;
        }

        lock (_statoAcquisizioneLockGate)
        {
            if (esito == EsitoIo.Ok)
            {
                if (StringComparer.Ordinal.Equals(_sessioneLockAcquisita, sessione) &&
                    StringComparer.OrdinalIgnoreCase.Equals(_percorsoLockSessioneAcquisita, percorso))
                {
                    _percorsoLockSessioneAcquisita = null;
                    _sessioneLockAcquisita = null;
                }
                if (StringComparer.Ordinal.Equals(_sessioneRilascioLockInAttesa, sessione) &&
                    StringComparer.OrdinalIgnoreCase.Equals(_percorsoRilascioSessioneLockInAttesa, percorso))
                {
                    _percorsoRilascioSessioneLockInAttesa = null;
                    _sessioneRilascioLockInAttesa = null;
                }
            }
            else
            {
                _percorsoRilascioSessioneLockInAttesa = percorso;
                _sessioneRilascioLockInAttesa = sessione;
            }
        }
        return esito;
    }

    /// <summary>Attende le operazioni che possono acquisire lock e rilascia ogni sessione posseduta prima
    /// della chiusura dell'applicazione. La finestra resta aperta se un rilascio esatto fallisce, così l'utente
    /// può usare il comando di retry invece di lasciare il lease attivo fino alla scadenza.</summary>
    public async Task<bool> RilasciaLockPerUscitaAsync()
    {
        Task[] operazioniInCorso;
        lock (_statoAcquisizioneLockGate)
        {
            _chiusuraInCorso = true;
            operazioniInCorso = new[]
            {
                _iniziaModificaInCorsoPerUscita?.Task,
                _promozioneCampoCustomInCorsoPerUscita?.Task,
                _salvataggioInCorsoPerUscita?.Task,
                _annullamentoInCorsoPerUscita?.Task,
            }.Where(task => task != null).Cast<Task>().ToArray();
        }
        OnPropertyChanged(nameof(InterazioneEditorAbilitata));

        if (operazioniInCorso.Length > 0) await Task.WhenAll(operazioniInCorso);

        FermaHeartbeat();
        var rilascioTest = await RilasciaLockSessioneAttivaAsync();
        if (rilascioTest == EsitoIo.Ok)
        {
            InModifica = false;
            RilascioLockInAttesa = false;
            MessaggioRilascioLock = null;
        }
        else
        {
            InModifica = false;
            RilascioLockInAttesa = true;
            _rilascioLockDopoSalvataggio = false;
            _tornaAllaListaDopoRilascio = false;
            MessaggioRilascioLock = LocalizationService.Instance.Traduci("LockSbloccoNonRiuscito");
        }

        var rilascioCatalogo = await RiprovaRilascioLockCatalogoAsync();
        var rilasciato = rilascioTest == EsitoIo.Ok && rilascioCatalogo == EsitoIo.Ok;
        if (!rilasciato)
        {
            lock (_statoAcquisizioneLockGate) _chiusuraInCorso = false;
            OnPropertyChanged(nameof(InterazioneEditorAbilitata));
        }
        return rilasciato;
    }

    [RelayCommand]
    private async Task RiprovaRilascioLock()
    {
        if (!RilascioLockInAttesa) return;
        var percorso = _percorsoRilascioSessioneLockInAttesa ?? PercorsoFile;
        var sessione = _sessioneRilascioLockInAttesa;
        EsitoIo rilascio;
        try
        {
            if (percorso == null) rilascio = EsitoIo.Ok;
            else if (string.IsNullOrWhiteSpace(sessione))
            {
                CrashLogService.Registra("Riprova rilascio lock test senza SessionId esatto", new InvalidOperationException(percorso));
                rilascio = EsitoIo.Errore;
            }
            else
            {
                rilascio = await AvviaRilascioSessioneEsatta(percorso, sessione);
            }
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Riprova rilascio lock test", ex);
            rilascio = EsitoIo.Errore;
        }

        if (rilascio != EsitoIo.Ok)
        {
            MessaggioRilascioLock = LocalizationService.Instance.Traduci(
                _rilascioLockDopoSalvataggio ? "SalvatoSbloccoLockNonRiuscito" : "LockSbloccoNonRiuscito");
            return;
        }

        lock (_statoAcquisizioneLockGate)
        {
            _percorsoRilascioSessioneLockInAttesa = null;
            _sessioneRilascioLockInAttesa = null;
            if (StringComparer.Ordinal.Equals(_sessioneLockAcquisita, sessione) &&
                StringComparer.OrdinalIgnoreCase.Equals(_percorsoLockSessioneAcquisita, percorso))
            {
                _percorsoLockSessioneAcquisita = null;
                _sessioneLockAcquisita = null;
            }
        }
        RilascioLockInAttesa = false;
        MessaggioRilascioLock = null;
        if (!_tornaAllaListaDopoRilascio) return;

        // Un Annulla precedente può aver lasciato pendente anche il lock del catalogo. Prima di
        // disporre l'editor, completa qui il cleanup oppure lascia che la chiusura proprietaria lo faccia.
        lock (_statoAcquisizioneLockGate)
        {
            if (_chiusuraInCorso)
            {
                _tornaAllaListaDopoRilascio = false;
                return;
            }
        }

        var rilascioCatalogo = await RiprovaRilascioLockCatalogoAsync();
        if (rilascioCatalogo != EsitoIo.Ok) return;

        // Serializza il callback di navigazione con l'avvio del drain: se la chiusura è partita,
        // l'editor resta vivo finché il drain non ha verificato entrambi i lease.
        lock (_statoAcquisizioneLockGate)
        {
            if (_chiusuraInCorso || _disposto)
            {
                _tornaAllaListaDopoRilascio = false;
                return;
            }
            if (!_tornaAllaListaDopoRilascio) return;
            _tornaAllaListaDopoRilascio = false;
            _tornaAllaLista();
        }
    }

    [RelayCommand]
    private async Task RiprovaRilascioLockCatalogo()
        => await RiprovaRilascioLockCatalogoAsync();

    private async Task<EsitoIo> RiprovaRilascioLockCatalogoAsync()
    {
        if (!RilascioLockCatalogoInAttesa) return EsitoIo.Ok;
        var percorso = _percorsoRilascioLockCatalogoInAttesa;
        var sessione = _sessioneRilascioLockCatalogoInAttesa;
        if (string.IsNullOrWhiteSpace(percorso) || string.IsNullOrWhiteSpace(sessione))
        {
            CrashLogService.Registra(
                "Riprova rilascio lock catalogo senza SessionId esatto",
                new InvalidOperationException(percorso ?? "Percorso catalogo mancante."));
            MessaggioRilascioLockCatalogo = LocalizationService.Instance.Traduci("LockSbloccoNonRiuscito");
            return EsitoIo.Errore;
        }

        EsitoIo rilascio;
        try { rilascio = await AvviaRilascioSessioneEsatta(percorso, sessione); }
        catch (Exception ex)
        {
            CrashLogService.Registra("Riprova rilascio lock catalogo", ex);
            rilascio = EsitoIo.Errore;
        }

        if (rilascio != EsitoIo.Ok)
        {
            MessaggioRilascioLockCatalogo = LocalizationService.Instance.Traduci("LockSbloccoNonRiuscito");
            return rilascio;
        }

        _percorsoRilascioLockCatalogoInAttesa = null;
        _sessioneRilascioLockCatalogoInAttesa = null;
        RilascioLockCatalogoInAttesa = false;
        MessaggioRilascioLockCatalogo = null;
        return EsitoIo.Ok;
    }

    /// <summary>Mostra il pulsante "Importa da log UnitLogger" solo quando lo strumento è davvero in uso (feedback
    /// utente 2026-08-24, punto 7) — aggiornato da AggiornaCampiStrumenti ad ogni cambio di selezione.</summary>
    [ObservableProperty] private bool haStrumentoUdtSelezionato;

    [ObservableProperty] private List<TestCaseCandidato> candidatiIterazione = new();
    [ObservableProperty] private List<TestCase> candidatiCorrelati = new();
    public bool HaCandidatiIterazione => CandidatiIterazione.Count > 0;
    public bool HaCandidatiCorrelati => CandidatiCorrelati.Count > 0;

    partial void OnCandidatiIterazioneChanged(List<TestCaseCandidato> value) => OnPropertyChanged(nameof(HaCandidatiIterazione));
    partial void OnCandidatiCorrelatiChanged(List<TestCase> value) => OnPropertyChanged(nameof(HaCandidatiCorrelati));

    /// <summary>Conferma richiesta quando il veicolo scelto non rientra nella/e Gamma assegnate all'operatore
    /// rilevante (assegnatario se presente, altrimenti chi sta creando il test) — mai bloccato, sempre
    /// segnalato e tracciato come eccezione (Addendum §17.3). Mostrata con un popup (Salva/ConfirmService),
    /// non più con un banner in cima alla pagina (feedback utente 2026-08-20): un banner in cima a un form
    /// lungo, con l'utente scrollato altrove, sembrava semplicemente non succedere nulla al Salva.</summary>
    [ObservableProperty] private string? messaggioFuoriCategoria;

    // Requisito/Test correlato scelti dai due AutoCompleteBox di ricerca (Addendum: collaudo live 2026-08-17,
    // punto 3 del grill-me — prima un ComboBox semplice e un TextBox a digitazione libera, sostituiti da una
    // ricerca vera che mostra anche il testo del requisito/la descrizione breve del test).
    [ObservableProperty] private Requisito? requisitoSelezionato;
    [ObservableProperty] private string nuovaFunzionalita = "";

    /// <summary>Interruttore "mostra tutte" per FunzionalitaDisponibili (feedback utente 2026-09-23): il
    /// filtro per componente scelta non deve MAI bloccare l'operatore quando il popolamento di Funzionalita.
    /// EcuAssociate è incompleto o sbagliato — attivarlo torna al comportamento precedente (catalogo intero,
    /// nessun filtro). Riparte sempre da false all'apertura di un test: un filtro "dimenticato attivo" da
    /// un'apertura precedente non deve nascondere l'elenco filtrato in quella successiva.</summary>
    [ObservableProperty] private bool mostraTutteLeFunzionalita;
    partial void OnMostraTutteLeFunzionalitaChanged(bool value) => OnPropertyChanged(nameof(FunzionalitaDisponibili));
    [ObservableProperty] private string nuovoCampoCustomChiave = "";
    [ObservableProperty] private string nuovoCampoCustomValore = "";
    [ObservableProperty] private TestCase? testCorrelatoSelezionato;
    [ObservableProperty] private string nuovoStrumentoNome = "";
    [ObservableProperty] private string nuovoTicketRiferimento = "";
    [ObservableProperty] private string nuovoTicketUrl = "";
    [ObservableProperty] private string? messaggioTicket;
    public bool HaMessaggioTicket => !string.IsNullOrEmpty(MessaggioTicket);

    partial void OnMessaggioTicketChanged(string? value) => OnPropertyChanged(nameof(HaMessaggioTicket));

    /// <summary>Nota obbligatoria richiesta solo quando un Master salva una modifica ad Area di validazione,
    /// Scadenza o Assegnatario (Addendum §7): transiente, non su TestCase, perché diventa una nuova voce di
    /// TestCase.NotePianificazione solo al momento del salvataggio, non prima.</summary>
    [ObservableProperty] private string notaPianificazioneTemp = "";

    public List<StrumentoOption> StrumentiOptions { get; private set; } = new();

    /// <summary>Ricerca sopra l'elenco strumenti, mostrata solo oltre 10 voci (feedback utente 2026-08-21: la
    /// regola combobox-pura/pulsante-con-ricerca vale per qualunque lista, non solo i campi Lista) — qui la
    /// selezione resta comunque le checkbox inline (con eventuali campi dello strumento annidati), la ricerca
    /// filtra solo quali righe restano visibili, non sostituisce l'elenco con un dialogo separato.</summary>
    public bool MostraRicercaStrumenti => StrumentiOptions.Count > 10;

    /// <summary>Elenco mostrato dalla lista VIRTUALIZZATA quando MostraRicercaStrumenti è vera (profilazione
    /// 2026-09-25, commit 1a28198: 18000 dei 18316 visuali dell'editor sul banco 100k venivano dalla WrapPanel
    /// non virtualizzata con un CheckBox+Button per ognuno dei ~1000 strumenti a catalogo). La ricerca filtra
    /// qui su TUTTE le StrumentiOptions (mai troncata, come FiltroMultiSelectRicerca): una voce fuori dal
    /// filtro resta raggiungibile cancellando il testo di ricerca, e una voce già selezionata resta comunque
    /// visibile nel riepilogo sopra la lista anche quando il filtro la nasconde. Con ≤10 strumenti la view usa
    /// invece l'ItemsControl/WrapPanel invariata legata a StrumentiOptions.</summary>
    public IReadOnlyList<StrumentoOption> StrumentiOptionsFiltrate =>
        string.IsNullOrWhiteSpace(FiltroStrumenti)
            ? StrumentiOptions
            : StrumentiOptions.Where(o => o.Label.Contains(FiltroStrumenti, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Riepilogo (conteggio + nomi) degli strumenti selezionati, mostrato sopra la lista virtualizzata
    /// così la selezione resta visibile anche quando una voce è fuori vista (scroll) o nascosta dal filtro di
    /// ricerca corrente.</summary>
    public string StrumentiSelezionatiRiepilogo
    {
        get
        {
            var selezionati = StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Label).ToList();
            var loc = LocalizationService.Instance;
            return selezionati.Count == 0
                ? loc.Traduci("StrumentiNessunoSelezionatoLbl")
                : string.Format(loc.Traduci("StrumentiSelezionatiRiepilogoFmt"), selezionati.Count, string.Join(", ", selezionati));
        }
    }

    [ObservableProperty] private string filtroStrumenti = "";
    partial void OnFiltroStrumentiChanged(string value)
    {
        if (MostraRicercaStrumenti)
        {
            // Lista virtualizzata: la vista lega StrumentiOptionsFiltrate, non serve (ed è controproducente,
            // sarebbero fino a ~1000 notifiche Visibile inutili) toccare Visibile voce per voce.
            OnPropertyChanged(nameof(StrumentiOptionsFiltrate));
            return;
        }
        foreach (var opt in StrumentiOptions)
            opt.Visibile = string.IsNullOrWhiteSpace(value) || opt.Label.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Veicolo intero" (Addendum §18): voce speciale in cima all'elenco componente che non punta a una
    /// componente specifica — un solo test copre l'intero veicolo invece di elencare i componenti secondarie a mano.</summary>
    public static string EcuVeicoloIntero => LocalizationService.Instance.Traduci("EcuVeicoloIntero");

    private List<Componente> _tutteLeEcu = new();

    /// <summary>Solo i componenti montate su almeno uno dei Veicoli già scelti (feedback utente 2026-08-24): prima
    /// mostrava tutto il catalogo, i componenti del veicolo solo in cima ma "mai nascoste" — scelta deliberata
    /// quando un test copriva un solo Veicolo, per non perdere di vista un componente condivisa tra gamme diverse
    /// (discussione 2026-08-19). Da quando un test può coprire più Veicoli/gamme insieme (VeicoliEffettivi),
    /// quel caso si risolve aggiungendo anche l'altro Veicolo al test invece di tenere sempre visibile
    /// l'intero catalogo — la lista lunga e non filtrata era il problema reale, non un vantaggio. Nessun
    /// veicolo ancora scelto: nulla su cui filtrare, resta il catalogo intero.</summary>
    /// <summary>componente dell'anagrafica montate su almeno uno dei veicoli dati (nessun veicolo = catalogo intero). Unica sorgente del filtro
    /// Veicolo -> componente, condivisa da <see cref="EcuDisponibili"/> e dal blocco "Aggiungi guasto".</summary>
    private IEnumerable<string> EcuAnagraficaPerVeicoli(IEnumerable<string> veicoli)
    {
        var v = veicoli.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return v.Count == 0 ? _tutteLeEcu.Select(e => e.Nome) : _tutteLeEcu.Where(e => e.VeicoliAssociati.Any(v.Contains)).Select(e => e.Nome);
    }

    public List<string> EcuDisponibili
    {
        get
        {
            if (_tutteLeEcu.Count == 0) return new List<string> { EcuVeicoloIntero };
            var filtrate = EcuAnagraficaPerVeicoli(TestCase.VeicoliEffettivi);
            var risultato = new List<string> { EcuVeicoloIntero }.Concat(filtrate).ToList();
            // il componente già salvata sul test resta sempre visibile anche se non più associata al Veicolo scelto
            // (es. l'associazione è cambiata dopo la creazione del test): altrimenti il campo appare vuoto pur
            // avendo un valore, perché la ComboBox non trova corrispondenza in EcuDisponibili per SelectedItem.
            foreach (var ecuCorrente in TestCase.Componente)
                if (!risultato.Contains(ecuCorrente, StringComparer.OrdinalIgnoreCase)) risultato.Add(ecuCorrente);
            return risultato;
        }
    }

    /// <summary>componente assegnate automaticamente a una nuova operazione indipendente (feedback utente 2026-08-25,
    /// punto 9): a differenza di FunzionalitaAutoSeSingola, qui il default è sempre "tutti i componenti del test",
    /// non solo con una sola — con una singola componente coincide comunque con l'unica scelta possibile, con più componente
    /// parte "aperta" (tutte incluse) e l'operatore restringe solo dove serve, invece di dover spuntare ogni
    /// volta da zero.</summary>
    private EcuSelection EcuAutoSuNuovaOperazione => new(TestCase.Componente);

    /// <summary>Funzionalità assegnata automaticamente a una nuova operazione quando il test ne collega una
    /// sola — nessuna scelta possibile né necessaria; vuota con 0 o 2+ Funzionalità, dove l'operatore deve
    /// scegliere esplicitamente (feedback utente 2026-08-24, terza revisione).</summary>
    private string FunzionalitaAutoSeSingola => TestCase.Funzionalita.Count == 1 ? TestCase.Funzionalita[0] : "";

    /// <summary>Obbligatoria solo quando il test collega 2+ Funzionalità: con una sola non c'è ambiguità da
    /// risolvere (è comunque valorizzata automaticamente, vedi FunzionalitaAutoSeSingola).</summary>
    public bool FunzionalitaOperazioneObbligatoria => DtcOperazioneRegole.FunzionalitaObbligatoria(MostraDtcOperazione, TestCase.Funzionalita.Count);

    private bool? _haRequisitoD4dConDtc;
    /// <summary>Regola 6.5.0: il selettore guasto compare sulle operazioni quando il test e collegato a un requisito Extra che
    /// HA guasto (non per template ne per strumento); in quel caso la Funzionalita non e obbligatoria. Un Extra senza guasto non
    /// attiva nulla (nessun avanzamento guasto, nessun errore).</summary>
    public bool HaRequisitoD4dConDtc => _haRequisitoD4dConDtc ??= RisolviConteggiDtc().Any(r =>
        TestCase.RequisitoId.Contains(r.Id) && RequisitoApplicabilita.ED4D(r) && r.DtcTotale > 0);

    private bool _conteggiDtcRisolti;
    /// <summary>DtcConteggio null/0 e IGNOTO per i Extra: una sola verifica economica sugli archivi (SQL, nessun payload) per editor.</summary>
    private IEnumerable<Requisito> RisolviConteggiDtc()
    {
        if (!_conteggiDtcRisolti)
        {
            _conteggiDtcRisolti = true;
            _state.Master.RequisitiDtc.Risolvi(RequisitiDisponibili);
        }
        return RequisitiDisponibili;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, IReadOnlyList<DtcRequisito>> _dtcArchiviCache = new();
    /// <summary>guasto di un requisito: embedded se presenti, altrimenti letti (una volta per editor) dall'archivio separato.
    /// Non lancia: archivio mancante/illeggibile = nessun guasto.</summary>
    private IReadOnlyList<DtcRequisito> VociDtc(Requisito r)
    {
        if (r.Guasto != null) return r.Guasto;
        if (r.DtcTotale == 0 || !RequisitoDtcArchivio.TryUid(r, out var uid)) return Array.Empty<DtcRequisito>();
        return _dtcArchiviCache.GetOrAdd(uid, _ => _state.Master.RequisitiDtc.Leggi(r).Voci);
    }

    /// <summary>Oltre questo numero (noto dai conteggi, senza leggere gli archivi) il conteggio opzioni non viene raffinato per
    /// componente sul thread UI: e gia certo che serve il picker con ricerca.</summary>
    private const int SogliaConteggioEsattoDtc = 2000;
    /// <summary>Strumento guasto Injection selezionato (selezione live, anche non ancora salvata).</summary>
    public bool UsaStrumentoDtcInjection => DtcOperazioneRegole.UsaDtcInjection(
        StrumentiOptions.Count > 0 ? StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Valore) : TestCase.StrumentiUsati);
    /// <summary>Selettore guasto: requisito Extra con guasto collegato OPPURE strumento guasto Injection (Funzionalita mai obbligatoria).</summary>
    public bool MostraDtcOperazione => DtcOperazioneRegole.MostraSelettore(HaRequisitoD4dConDtc, UsaStrumentoDtcInjection);
    /// <summary>Codice guasto digitato (formato validato): con guasto Injection; si abbina al Extra per codice+componente se collegato.</summary>
    public bool MostraDtcCodiceLibero => UsaStrumentoDtcInjection;

    /// <summary>Almeno uno strumento "Da testare" (UnitLogger, Easy...) selezionato: solo allora la Funzionalita ha senso come proprieta delle SUE operazioni.</summary>
    public bool HaStrumentoDaTestare => NomiStrumentiSelezionati().Any(n => TipoDiStrumento(n) == TipoStrumento.DaTestare);
    /// <summary>Se sono stati scelti strumenti e sono tutti di supporto, il Test deve comunque indicare l'area
    /// generale a cui si riferiscono, anche se non generano operazioni. Il percorso guasto Injection resta regolato
    /// dalla sua regola dedicata.</summary>
    public bool SoloStrumentiSupporto
    {
        get
        {
            var selezionati = NomiStrumentiSelezionati().ToList();
            return selezionati.Count > 0 && !UsaStrumentoDtcInjection
                && selezionati.All(n => TipoDiStrumento(n) == TipoStrumento.Supporto);
        }
    }
    /// <summary>Nomi degli strumenti Da testare (UnitLogger, Easy...) noti all'editor: proprietari per cui la Funzionalita di riga ha senso.</summary>
    public IReadOnlyList<string> StrumentiDaTestareNomi =>
        StrumentiOptions.Select(o => o.Valore).Concat(TestCase.StrumentiUsati).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(n => TipoDiStrumento(n) == TipoStrumento.DaTestare).ToList();
    /// <summary>6.5.2: guasto Injection selezionato e NESSUNO strumento Da testare. Il test non ha operazioni libere ne Funzionalita: le uniche
    /// operazioni sono le coppie Validazione/Devalidazione create da "Aggiungi guasto" (anche con ValidationTool/CaptureTool, che non generano operazioni).</summary>
    public bool SoloOperazioniDtc => UsaStrumentoDtcInjection && !HaStrumentoDaTestare;
    /// <summary>Il "+ Operazione" libero non ha senso quando le uniche operazioni ammesse sono le coppie guasto.</summary>
    public bool MostraAggiungiOperazioneLibera => !SoloOperazioniDtc;
    public bool DtcMostraCombo => HaRequisitoD4dConDtc && !DtcUsaPicker;
    public bool DtcMostraPicker => HaRequisitoD4dConDtc && DtcUsaPicker;
    public Array PassiDtc => Enum.GetValues(typeof(PassoDtc));

    /// <summary>Soglia sotto cui le etichette guasto stanno in una ComboBox; oltre si usa il picker con ricerca. Con 100k guasto
    /// le etichette NON vengono costruite sul thread UI.</summary>
    public const int SogliaPickerDtc = 10;

    private int? _dtcConteggioCache;
    /// <summary>Numero di guasto proponibili, contato senza costruire etichette (O(voci), nessuna allocazione per voce).</summary>
    public int DtcConteggioOpzioni
    {
        get
        {
            if (_dtcConteggioCache is { } c) return c;
            var ecuTest = TestCase.Componente.Where(e => !string.IsNullOrWhiteSpace(e)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var n = 0;
            var collegati = RisolviConteggiDtc().Where(r => TestCase.RequisitoId.Contains(r.Id) && RequisitoApplicabilita.ED4D(r)).ToList();
            if (ecuTest.Count == 0 || collegati.Sum(r => r.DtcTotale) > SogliaConteggioEsattoDtc)
                n = collegati.Sum(r => r.DtcTotale); // dal conteggio (limite superiore se filtrato per componente): nessuna lettura archivi sul thread UI
            else
                foreach (var req in collegati)
                    foreach (var v in VociDtc(req))
                        if (ecuTest.Contains(v.Componente)) n++;
            // Storico: guasto gia referenziati dalle operazioni (al massimo qualche decina).
            n += TestCase.Esecuzioni.SelectMany(x => x.Condizioni).Count(x => x.DtcEtichetta != null);
            return (_dtcConteggioCache = n).Value;
        }
    }
    public bool DtcUsaPicker => DtcConteggioOpzioni > SogliaPickerDtc;

    /// <summary>Etichette per la ComboBox, SOLO se l'elenco e piccolo; vuoto altrimenti (si usa il picker).</summary>
    public IReadOnlyList<string> DtcOpzioniPiccole => DtcUsaPicker ? Array.Empty<string>() : DtcOpzioniOperazione;

    /// <summary>Etichette dei guasti selezionabili (requisiti Extra collegati, filtrate per componente del test, piu lo storico delle
    /// operazioni). Pura e riusabile fuori dal thread UI (usata dal picker in Task.Run).</summary>
    internal static List<string> CostruisciEtichetteDtc(IEnumerable<Requisito> requisiti, ICollection<string> idCollegati,
        ICollection<string> ecuTest, IEnumerable<string?> storico, Func<Requisito, IReadOnlyList<DtcRequisito>>? voci = null)
    {
        voci ??= r => r.DtcVoci;
        var componente = ecuTest.Where(e => !string.IsNullOrWhiteSpace(e)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var etichette = new List<string>();
        var viste = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in requisiti.Where(r => idCollegati.Contains(r.Id) && RequisitoApplicabilita.ED4D(r)))
            foreach (var v in voci(req))
            {
                if (componente.Count > 0 && !componente.Contains(v.Componente)) continue;
                var e = DtcRequisito.Etichetta(req.Id, v.Codice, v.Componente, v.Veicolo);
                if (viste.Add(e)) etichette.Add(e);
            }
        foreach (var e in storico)
            if (e != null && viste.Add(e)) etichette.Add(e);
        return etichette;
    }

    private List<string>? _dtcOpzioniCache;
    public IReadOnlyList<string> DtcOpzioniOperazione =>
        _dtcOpzioniCache ??= CostruisciEtichetteDtc(RequisitiDisponibili, TestCase.RequisitoId, TestCase.Componente,
            TestCase.Esecuzioni.SelectMany(x => x.Condizioni).Select(c => c.DtcEtichetta), VociDtc);

    /// <summary>Elenco grande (fino a 100k): picker con ricerca (stesso dialog di Componente/Veicolo); etichette e voci costruite
    /// in un Task.Run, mai sul thread UI.</summary>
    [RelayCommand]
    private async Task ScegliDtcOperazione(Condizione? condizione)
    {
        if (condizione == null || !PuoModificareContenuto) return;
        var req = RequisitiDisponibili.ToList(); var ids = TestCase.RequisitoId.ToList(); var componente = TestCase.Componente.ToList();
        var storico = TestCase.Esecuzioni.SelectMany(x => x.Condizioni).Select(c => c.DtcEtichetta).ToList();
        var voci = await Task.Run(() => CostruisciEtichetteDtc(req, ids, componente, storico, VociDtc)
            .Select(e => new VoceSelezionabile { Valore = e, TestoPrincipale = e, TestoRicerca = e }).ToList());
        var scelta = await SelezionaDialogService.Scegli(LocalizationService.Instance.Traduci("DtcOperazionePlaceholder"), voci);
        if (scelta != null) { ImpostaDtcOperazione(condizione, (string)scelta.Valore); SincronizzaEsitoCondizioni(); }
    }

    /// <summary>Da chiamare quando cambiano requisiti/componente del test: invalida cache e ricalcola visibilita.</summary>
    private void NotificaDtcOpzioni()
    {
        _dtcOpzioniCache = null; _haRequisitoD4dConDtc = null; _dtcConteggioCache = null;
        NotificaBloccoAggiungiDtc();
        OnPropertyChanged(nameof(DtcOpzioniOperazione));
        OnPropertyChanged(nameof(DtcOpzioniPiccole));
        OnPropertyChanged(nameof(DtcUsaPicker));
        OnPropertyChanged(nameof(DtcMostraCombo));
        OnPropertyChanged(nameof(DtcMostraPicker));
        OnPropertyChanged(nameof(UsaStrumentoDtcInjection));
        OnPropertyChanged(nameof(MostraDtcCodiceLibero));
        OnPropertyChanged(nameof(HaStrumentoDaTestare));
        OnPropertyChanged(nameof(StrumentiDaTestareNomi));
        OnPropertyChanged(nameof(SoloOperazioniDtc));
        OnPropertyChanged(nameof(SoloStrumentiSupporto));
        OnPropertyChanged(nameof(MostraAggiungiOperazioneLibera));
        OnPropertyChanged(nameof(MostraCampoFunzionalita));
        NotificaCoperturaCambiata();
        RiabbinaDtcOperazioni();
        _ = AggiornaStatoDtcOperazioniAsync();
        OnPropertyChanged(nameof(HaRequisitoD4dConDtc));
        OnPropertyChanged(nameof(MostraDtcOperazione));
        OnPropertyChanged(nameof(FunzionalitaOperazioneObbligatoria));
        NotificaObbligatorieta();
    }

    /// <summary>Applica la scelta del guasto a un'operazione: Requisito+guasto dalla voce, componente dell'operazione = componente del guasto
    /// (se tra quelle del test), passo di default Validazione.</summary>
    public void ImpostaDtcOperazione(Condizione condizione, string etichetta)
    {
        foreach (var req in RequisitiDisponibili.Where(r => etichetta.EndsWith(" · " + r.Id, StringComparison.OrdinalIgnoreCase)))
            foreach (var v in VociDtc(req))
                if (DtcRequisito.Etichetta(req.Id, v.Codice, v.Componente, v.Veicolo).Equals(etichetta, StringComparison.OrdinalIgnoreCase))
                {
                    var vecchio = condizione.Guasto;
                    condizione.Guasto = v.Codice;
                    condizione.DtcRequisitoVuotoScelto = null;
                    condizione.DtcRequisitoId = req.Id;
                    condizione.DtcVeicolo = v.Veicolo;
                    condizione.DtcPasso ??= PassoDtc.Validazione;
                    SincronizzaAttesoDtcInjection(condizione, vecchio);
                    if (TestCase.Componente.Count == 0 || TestCase.Componente.Contains(v.Componente, StringComparer.OrdinalIgnoreCase))
                        condizione.Componente = new EcuSelection(new[] { v.Componente });
                    ModificheNonSalvate = true;
                    _ = AggiornaStatoDtcOperazioniAsync();
                    return;
                }
    }

    /// <summary>"Guasto - Atteso" dello strumento guasto Injection e SOLO una precompilazione dal guasto dell'operazione (non un secondo dato
    /// indipendente): si aggiorna se vuoto o se coincideva con il guasto precedente; un valore diverso scelto a mano resta, ma vale
    /// sempre Condizione.Guasto (la divergenza e segnalata dall'avviso dell'operazione).</summary>
    private void SincronizzaAttesoDtcInjection(Condizione condizione, string? dtcPrecedente)
    {
        if (!UsaStrumentoDtcInjection || string.IsNullOrWhiteSpace(condizione.Guasto)) return;
        if (!condizione.ValoriStrumenti.TryGetValue(DtcOperazioneRegole.ChiaveAtteso, out var atteso)
            || string.IsNullOrWhiteSpace(atteso) || atteso.Trim().Equals((dtcPrecedente ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
        {
            condizione.ValoriStrumenti[DtcOperazioneRegole.ChiaveAtteso] = condizione.Guasto;
            AggiornaCampiVisualCondizione(condizione);
        }
    }

    /// <summary>Codice guasto digitato (strumento guasto Injection). Formato validato; abbinato per codice+componente dell'operazione ai guasti dei
    /// requisiti Extra collegati (conta nell'avanzamento Audit Extra), altrimenti resta senza requisito (guasto libero: fuori da Audit/copertura,
    /// nessun errore ne avviso). Codice vuoto = rimuove l'operazione guasto. Ritorna false se il formato non e valido (nulla modificato).</summary>
    public bool ImpostaDtcCodiceLibero(Condizione condizione, string? codice)
    {
        var c = (codice ?? "").Trim();
        if (c.Length == 0)
        {
            if (!condizione.RiguardaDtc) return true;
            RimuoviDtcOperazione(condizione); _ = AggiornaStatoDtcOperazioniAsync(); return true;
        }
        if (!DtcOperazioneRegole.CodiceValido(c)) return false;
        if (string.Equals(condizione.Guasto, c, StringComparison.Ordinal)) return true;
        var vecchio = condizione.Guasto;
        condizione.Guasto = c;
        condizione.DtcRequisitoVuotoScelto = null;   // nuovo codice digitato: l'abbinamento automatico torna applicabile
        // Cascata Veicolo/componente/guasto: il veicolo dell'operazione descriveva il guasto PRECEDENTE. Si rivalida col nuovo codice: se esiste una voce per
        // quel veicolo lo si mantiene, altrimenti si abbina senza vincolo di veicolo e il veicolo diventa quello (unico) della voce trovata, o nessuno.
        var collegati = RequisitiDisponibili.Where(r => TestCase.RequisitoId.Contains(r.Id)).ToList();
        var ecuOp = OperationalSemantics.EcuUnits(TestCase, condizione);
        var req = string.IsNullOrWhiteSpace(condizione.DtcVeicolo) ? null : DtcOperazioneRegole.Abbina(collegati, c, ecuOp, VociDtc, condizione.DtcVeicolo);
        if (req == null)
        {
            condizione.DtcVeicolo = null;
            req = DtcOperazioneRegole.Abbina(collegati, c, ecuOp, VociDtc);
            if (req != null)
            {
                var veicoliVoce = collegati.Where(r => r.Id.Equals(req, StringComparison.OrdinalIgnoreCase)).SelectMany(r => VociDtc(r))
                    .Where(v => v.Codice.Trim().Equals(c, StringComparison.OrdinalIgnoreCase) && (ecuOp.Count == 0 || ecuOp.Contains(v.Componente.Trim(), StringComparer.OrdinalIgnoreCase)))
                    .Select(v => v.VeicoloNorm).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (veicoliVoce.Count == 1) condizione.DtcVeicolo = veicoliVoce[0];
            }
        }
        condizione.DtcRequisitoId = req;
        condizione.DtcPasso ??= PassoDtc.Validazione;
        SincronizzaAttesoDtcInjection(condizione, vecchio);
        ModificheNonSalvate = true;
        _ = AggiornaStatoDtcOperazioniAsync();
        return true;
    }

    /// <summary>Quando cambiano requisiti/componente/strumenti: le operazioni guasto senza requisito della REV CORRENTE si riabbinano ai Extra
    /// ora collegati, ma SOLO se non ancora eseguite: le gia eseguite (e tutto lo storico) non cambiano requisito in silenzio,
    /// altrimenti entrerebbero in Audit retroattivamente. Abbinamento ambiguo = nessun abbinamento. Le operazioni che l'utente ha
    /// lasciato deliberatamente "(nessuno)" (<see cref="Condizione.DtcRequisitoVuotoScelto"/>) non si riabbinano mai.</summary>
    private void RiabbinaDtcOperazioni()
    {
        if (RevisioneCorrente == null) return;
        foreach (var c in RevisioneCorrente.Condizioni.Where(x => x.RiguardaDtc && string.IsNullOrEmpty(x.DtcRequisitoId) && x.DtcRequisitoVuotoScelto != true && !x.Esito.EEseguito()))
        {
            var req = DtcOperazioneRegole.Abbina(RequisitiDisponibili.Where(r => TestCase.RequisitoId.Contains(r.Id)), c.Guasto!, OperationalSemantics.EcuUnits(TestCase, c), VociDtc);
            if (req != null) { c.DtcRequisitoId = req; ModificheNonSalvate = true; }
        }
    }

    /// <summary>Conferma della deselezione di guasto Injection con operazioni guasto (true = rimuovere i guasti, false = mantenere lo
    /// strumento). Iniettabile nei test; di default dialog Si/No.</summary>
    public Func<int, Task<bool>>? ConfermaRimozioneDtc { get; set; }
    /// <summary>Ultima gestione (async) della deselezione di guasto Injection; attendibile dai test.</summary>
    public Task DeselezioneDtcInCorso { get; private set; } = Task.CompletedTask;

    /// <summary>Deselezione di guasto Injection senza requisito Extra con guasto: il selettore sparirebbe lasciando operazioni guasto
    /// "orfane". Se ce ne sono (escluse quelle di PROPRIETA dello strumento, gestite da <see cref="GestisciDeselezioneStrumentoAsync"/>
    /// e rimosse con esso) chiede: rimuovere i guasti o mantenere lo strumento. Rimozione: azzera Guasto/Requisito/Passo SOLO sulle
    /// operazioni non ancora eseguite; le eseguite mantengono i dati (storico) e la loro Funzionalita mancante non blocca.
    /// Ritorna false se l'utente mantiene lo strumento (nulla modificato).</summary>
    private async Task<bool> ConfermaDeselezioneDtcInjectionAsync(Func<Condizione, bool> daEscludere)
    {
        if (HaRequisitoD4dConDtc) return true;
        var n = TestCase.Esecuzioni.SelectMany(x => x.Condizioni).Count(c => c.RiguardaDtc && !daEscludere(c));
        if (n == 0) return true;
        var loc = LocalizationService.Instance;
        var rimuovi = ConfermaRimozioneDtc != null ? await ConfermaRimozioneDtc(n)
            : await ConfirmService.Chiedi(loc.Traduci("DtcDeselezioneTitolo"), string.Format(loc.Traduci("DtcDeselezioneMessaggioFmt"), n),
                loc.Traduci("DtcDeselezioneRimuovi"), loc.Traduci("DtcDeselezioneMantieni"));
        if (!rimuovi) return false;
        foreach (var c in TestCase.Esecuzioni.SelectMany(x => x.Condizioni).Where(c => c.RiguardaDtc && !c.Esito.EEseguito() && !daEscludere(c)))
            RimuoviDtcOperazione(c);
        ModificheNonSalvate = true;
        return true;
    }

    // ----- Operazioni di proprieta degli strumenti (6.5.1, fase 3) --------------------------------------------------
    // Assunzione: oggi le operazioni NON nascono da sole per funzionalita/strumento (si aggiungono a mano con "+ Operazione", con
    // Funzionalita/componente preselezionate se una sola). Selezionando uno strumento DaTestare/Comportamento l'editor crea subito, nella REV
    // corrente, le operazioni MANCANTI di quello strumento (Condizione.Strumento valorizzato); Supporto non ne crea. Le operazioni
    // legacy (Strumento == null) e le REV storiche non si toccano mai.

    /// <summary>Conferma dell'eliminazione delle operazioni di uno strumento deselezionato (nome strumento, numero operazioni).
    /// Iniettabile nei test; di default dialog Si/No (senza handler di dialog = conferma).</summary>
    public Func<string, int, Task<bool>>? ConfermaRimozioneOperazioniStrumento { get; set; }
    /// <summary>Deselezione di uno strumento bloccata (esiti registrati / REV registrate): messaggio mostrato nell'editor.</summary>
    [ObservableProperty] private string? avvisoStrumentiOperazioni;
    public bool HaAvvisoStrumentiOperazioni => !string.IsNullOrEmpty(AvvisoStrumentiOperazioni);
    partial void OnAvvisoStrumentiOperazioniChanged(string? value) => OnPropertyChanged(nameof(HaAvvisoStrumentiOperazioni));

    private bool _ripristinoSelezioneStrumento;
    private void RiselezionaStrumento(StrumentoOption opzione)
    {
        _ripristinoSelezioneStrumento = true;
        try { opzione.Selezionato = true; }
        finally { _ripristinoSelezioneStrumento = false; }
        AggiornaCampiStrumenti();
        OnPropertyChanged(nameof(StrumentiSelezionatiRiepilogo));
        NotificaDtcOpzioni();
    }

    private TipoStrumento TipoDiStrumento(string nome) =>
        TrovaStrumento(nome)?.TipoEffettivo ?? StrumentoDiagnostico.TipoPerNome(nome) ?? TipoStrumento.Supporto;

    /// <summary>Operazioni della REV corrente di proprieta dello strumento.</summary>
    private List<Condizione> OperazioniDelloStrumento(string nome) =>
        RevisioneCorrente?.Condizioni.Where(c => OperazioniStrumentoRegole.Appartiene(c, nome)).ToList() ?? new();

    /// <summary>Strumento appena selezionato: crea nella REV corrente le operazioni mancanti (DaTestare: una per funzionalita collegata
    /// al test; Comportamento: la coppia Validazione+Devalidazione per ogni guasto gia presente nelle operazioni; Supporto: nessuna).
    /// Idempotente: non duplica cio che esiste gia. Sulle REV storiche (sfogliate) non fa nulla.</summary>
    private void AllineaOperazioniStrumentoSelezionato(string nome)
    {
        if (RevisioneCorrente == null || VisualizzaStorico) return;
        var tipo = TipoDiStrumento(nome);
        var nuove = new List<Condizione>();
        foreach (var f in OperazioniStrumentoRegole.FunzionalitaMancanti(tipo, nome, RevisioneCorrente.Condizioni, TestCase.Funzionalita))
            nuove.Add(new Condizione { Esito = Esito.DaEseguire, Strumento = nome, Funzionalita = f, Componente = EcuAutoSuNuovaOperazione });
        foreach (var d in OperazioniStrumentoRegole.CoppieDtcMancanti(tipo, nome, RevisioneCorrente.Condizioni))
            nuove.Add(new Condizione
            {
                Esito = Esito.DaEseguire, Strumento = nome, Funzionalita = d.Sorgente.Funzionalita, Componente = new EcuSelection(d.Sorgente.Componente),
                Guasto = d.Attesa.Codice, DtcRequisitoId = d.Sorgente.DtcRequisitoId, DtcRequisitoVuotoScelto = d.Sorgente.DtcRequisitoVuotoScelto,
                DtcVeicolo = d.Sorgente.DtcVeicolo, DtcPasso = d.Attesa.Passo,
            });
        if (nuove.Count == 0) return;
        InserisciOperazioniStrumento(nome, nuove);
        AvvisoStrumentiOperazioni = null;
    }

    /// <summary>Prima operazione davvero vuota e non toccata della REV corrente (nessuno strumento proprietario, nessun guasto, esito "Da eseguire",
    /// nessun testo/valore/catena/segnalazione, Funzionalita ed componente quelle automatiche o gia quelle dell'operazione da creare); altrimenti null.
    /// Le operazioni compilate dall'utente non si adottano mai.</summary>
    private Condizione? OperazioneVuotaAdottabile(Condizione daCreare)
    {
        var ecuTest = TestCase.Componente.Select(e => (e ?? "").Trim()).Where(e => e.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool EcuIntatta(Condizione c)
        {
            var e = c.Componente.Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return e.Count == 0 || e.SetEquals(ecuTest);
        }
        bool FunzIntatta(Condizione c) => string.IsNullOrWhiteSpace(c.Funzionalita)
            || c.Funzionalita.Equals(FunzionalitaAutoSeSingola, StringComparison.OrdinalIgnoreCase)
            || c.Funzionalita.Equals(daCreare.Funzionalita, StringComparison.OrdinalIgnoreCase);
        return RevisioneCorrente?.Condizioni.FirstOrDefault(c =>
            string.IsNullOrWhiteSpace(c.Strumento) && !c.RiguardaDtc && c.Esito == Esito.DaEseguire && c.DtcPasso == null
            && string.IsNullOrWhiteSpace(c.DtcRequisitoId) && string.IsNullOrWhiteSpace(c.DtcVeicolo)
            && !DurataOperazione.HaValoriStrumenti(c.ValoriStrumenti) && EvidenzaOperazione.Leggi(c.ValoriStrumenti).Count == 0 && c.OperazioneOrigineUid == null && c.SegnalazioneUid == null && c.ExtensionData == null
            && string.IsNullOrWhiteSpace(c.Nota) && string.IsNullOrWhiteSpace(c.MotivazioneNonApplicabile)
            && string.IsNullOrWhiteSpace(c.DescrizioneDeviazione) && string.IsNullOrWhiteSpace(c.DescrizioneProblema)
            && c.ColoreNota == ColoreEvidenziazione.Nessuno && c.ColoreMotivazioneNonApplicabile == ColoreEvidenziazione.Nessuno
            && c.ColoreDescrizioneDeviazione == ColoreEvidenziazione.Nessuno && c.ColoreDescrizioneProblema == ColoreEvidenziazione.Nessuno
            && EcuIntatta(c) && FunzIntatta(c));
    }

    /// <summary>Con <see cref="SoloOperazioniDtc"/> le operazioni vuote e non toccate (tipicamente quella iniziale del wizard) non devono restare
    /// accanto alle coppie guasto: si rimuovono. Quelle compilate dall'utente o gia con esito non si toccano mai.</summary>
    private void RimuoviOperazioniVuoteSeSoloDtc()
    {
        if (RevisioneCorrente == null || VisualizzaStorico || !SoloOperazioniDtc) return;
        var rimosso = false;
        while (OperazioneVuotaAdottabile(new Condizione()) is { } vuota) { RevisioneCorrente.Condizioni.Remove(vuota); rimosso = true; }
        if (!rimosso) return;
        SincronizzaCondizioniVisual();
        NotificaCoperturaCambiata();
        ModificheNonSalvate = true;
    }

    private IEnumerable<string> NomiStrumentiSelezionati() =>
        StrumentiOptions.Count > 0 ? StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Valore) : TestCase.StrumentiUsati;

    /// <summary>Funzionalita aggiunta al test: gli strumenti DaTestare gia selezionati creano l'operazione mancante di quella funzionalita.</summary>
    private void AllineaOperazioniDopoFunzionalitaAggiunta()
    {
        if (RevisioneCorrente == null || VisualizzaStorico) return;
        foreach (var nome in NomiStrumentiSelezionati().ToList())
            if (TipoDiStrumento(nome) == TipoStrumento.DaTestare) AllineaOperazioniStrumentoSelezionato(nome);
    }

    /// <summary>Funzionalita rimossa dal test: le operazioni di quella funzionalita degli strumenti DaTestare si eliminano solo se NESSUNA ha un
    /// esito registrato (per strumento); altrimenti restano e si segnala (AvvisoStrumentiOperazioni).</summary>
    /// <summary>Operazioni degli strumenti DaTestare selezionati legate a una funzionalita (REV corrente), per strumento.</summary>
    private List<(string Nome, List<Condizione> Ops)> OperazioniStrumentoDiFunzionalita(string funzionalita)
    {
        var risultato = new List<(string, List<Condizione>)>();
        if (RevisioneCorrente == null || VisualizzaStorico) return risultato;
        foreach (var nome in NomiStrumentiSelezionati().ToList())
        {
            if (TipoDiStrumento(nome) != TipoStrumento.DaTestare) continue;
            var ops = RevisioneCorrente.Condizioni.Where(c => OperazioniStrumentoRegole.Appartiene(c, nome) && !c.RiguardaDtc
                && c.Funzionalita.Trim().Equals(funzionalita.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (ops.Count > 0) risultato.Add((nome, ops));
        }
        return risultato;
    }

    /// <summary>Conferma dell'eliminazione di operazioni che contengono dati compilati dall'utente (elenco di cio che si perde, una riga per
    /// operazione). Iniettabile nei test; di default dialog Si/No. Senza dati compilati ritorna true senza chiedere nulla.</summary>
    public Func<IReadOnlyList<string>, Task<bool>>? ConfermaPerditaDatiOperazioni { get; set; }

    private static string DescriviPerditaDati(Condizione c)
    {
        var loc = LocalizationService.Instance;
        string Etichetta(string d) => d.StartsWith("Valori:", StringComparison.Ordinal)
            ? $"{loc.Traduci("PerditaDatiValori")} ({d[7..]})" : loc.Traduci("PerditaDati" + d);
        var cosa = c.RiguardaDtc ? $"{c.Guasto} {c.DtcPasso}".Trim() : c.Funzionalita;
        var chi = string.IsNullOrWhiteSpace(c.Strumento) ? cosa : $"{c.Strumento} · {cosa}";
        return $"- {chi}: {string.Join(", ", DtcOperazioneRegole.DatiUtente(c).Select(Etichetta))}";
    }

    private async Task<bool> ConfermaPerditaDatiAsync(IEnumerable<Condizione> daEliminare)
    {
        var righe = daEliminare.Where(DtcOperazioneRegole.HaDatiUtente).Select(DescriviPerditaDati).ToList();
        if (righe.Count == 0) return true;
        if (ConfermaPerditaDatiOperazioni != null) return await ConfermaPerditaDatiOperazioni(righe);
        var loc = LocalizationService.Instance;
        var testo = string.Join("\n", righe.Take(12)) + (righe.Count > 12 ? "\n..." : "");
        return await ConfirmService.Chiedi(loc.Traduci("PerditaDatiTitolo"), string.Format(loc.Traduci("PerditaDatiMessaggioFmt"), testo),
            loc.Traduci("EliminaBtn"), loc.Traduci("Annulla"));
    }

    private void RimuoviOperazioniStrumentoDiFunzionalita(string funzionalita)
    {
        if (RevisioneCorrente == null || VisualizzaStorico) return;
        var bloccati = new List<string>();
        var rimosse = false;
        foreach (var (nome, ops) in OperazioniStrumentoDiFunzionalita(funzionalita))
        {
            if (!DtcOperazioneRegole.PuoDeselezionare(ops)) { bloccati.Add(nome); continue; }
            RevisioneCorrente.Condizioni.RemoveAll(ops.Contains);
            rimosse = true;
        }
        if (bloccati.Count > 0)
            AvvisoStrumentiOperazioni = string.Format(LocalizationService.Instance.Traduci("FunzionalitaOperazioniStrumentoBloccateFmt"), string.Join(", ", bloccati), funzionalita);
        else if (rimosse) AvvisoStrumentiOperazioni = null;
        if (!rimosse) return;
        SincronizzaCondizioniVisual();
        NotificaCoperturaCambiata();
        ModificheNonSalvate = true;
    }

    /// <summary>Inserisce nella REV corrente operazioni di proprieta dello strumento, raggruppate dopo l'ultima gia presente (altrimenti in
    /// coda), e riallinea vista, copertura e precompilazione "Guasto - Atteso". Unico punto di inserimento (selezione strumento e "Aggiungi guasto").</summary>
    private void InserisciOperazioniStrumento(string nome, IEnumerable<Condizione> nuoveOperazioni)
    {
        if (RevisioneCorrente == null) return;
        var nuove = nuoveOperazioni.ToList();
        // L'operazione vuota iniziale (wizard) viene ADOTTATA come prima operazione dello strumento invece di restare come riga vuota.
        if (nuove.Count > 0 && OperazioneVuotaAdottabile(nuove[0]) is { } vuota)
        {
            var d = nuove[0];
            nuove.RemoveAt(0);
            vuota.Strumento = d.Strumento; vuota.Funzionalita = d.Funzionalita; vuota.Componente = d.Componente;
            vuota.Guasto = d.Guasto; vuota.DtcRequisitoId = d.DtcRequisitoId; vuota.DtcRequisitoVuotoScelto = d.DtcRequisitoVuotoScelto; vuota.DtcVeicolo = d.DtcVeicolo; vuota.DtcPasso = d.DtcPasso;
            if (vuota.RiguardaDtc) SincronizzaAttesoDtcInjection(vuota, null);
            AggiornaCampiVisualCondizione(vuota);
            // TIME: l'operazione adottata cambia componente/Funzionalita/tipo guasto: la durata si propone dalla regola di copia (come ogni nuova operazione).
            AvviaCopiaDurataOperazione(vuota);
        }
        foreach (var c in nuove)
        {
            var ultimo = RevisioneCorrente.Condizioni.FindLastIndex(x => OperazioniStrumentoRegole.Appartiene(x, nome));
            RevisioneCorrente.Condizioni.Insert(ultimo < 0 ? RevisioneCorrente.Condizioni.Count : ultimo + 1, c);
            if (c.RiguardaDtc) SincronizzaAttesoDtcInjection(c, null);
            AggiornaCampiVisualCondizione(c);
            AvviaCopiaDurataOperazione(c);   // TIME: nuova operazione dello strumento (o coppia guasto): stessa regola di copia delle altre
        }
        SincronizzaCondizioniVisual();
        NotificaCoperturaCambiata();
        ModificheNonSalvate = true;
        _ = AggiornaStatoDtcOperazioniAsync();
    }

    /// <summary>Deselezione di uno strumento. 1) Se le sue operazioni (REV corrente) hanno un esito diverso da "Da eseguire" o esistono
    /// REV precedenti che le contengono: BLOCCATA (<see cref="DtcOperazioneRegole.PuoDeselezionare"/>), strumento riselezionato e messaggio.
    /// 2) Altrimenti conferma e, per guasto Injection, la gestione preesistente dei guasti non di proprieta (legacy); 3) solo se tutto e confermato
    /// le operazioni dello strumento vengono eliminate. Le operazioni legacy (Strumento == null) non si toccano.</summary>
    private async Task GestisciDeselezioneStrumentoAsync(StrumentoOption opzione)
    {
        var nome = opzione.Valore.Trim();
        var proprie = RevisioneCorrente == null || VisualizzaStorico ? new List<Condizione>() : OperazioniDelloStrumento(nome);
        var inRevPrecedenti = RevisioneCorrente != null
            && Esecuzioni.Where(e => e != RevisioneCorrente).Any(e => e.Condizioni.Any(c => OperazioniStrumentoRegole.Appartiene(c, nome)));
        var loc = LocalizationService.Instance;
        if (proprie.Count > 0 && !DtcOperazioneRegole.PuoDeselezionare(proprie, inRevPrecedenti))
        {
            RiselezionaStrumento(opzione);
            AvvisoStrumentiOperazioni = string.Format(loc.Traduci("StrumentoDeselezioneBloccataFmt"), nome);
            return;
        }
        if (proprie.Any(DtcOperazioneRegole.HaDatiUtente))
        {
            // Operazioni ancora "Da eseguire" ma con dati compilati: conferma piu esplicita con l'elenco di cio che si perde (al posto della generica).
            if (!await ConfermaPerditaDatiAsync(proprie)) { RiselezionaStrumento(opzione); return; }
        }
        else if (proprie.Count > 0)
        {
            var ok = ConfermaRimozioneOperazioniStrumento != null ? await ConfermaRimozioneOperazioniStrumento(nome, proprie.Count)
                : await ConfirmService.Chiedi(loc.Traduci("StrumentoDeselezioneConfermaTitolo"),
                    string.Format(loc.Traduci("StrumentoDeselezioneConfermaFmt"), nome, proprie.Count), loc.Traduci("EliminaBtn"), loc.Traduci("Annulla"));
            if (!ok) { RiselezionaStrumento(opzione); return; }
        }
        var proprieSet = proprie.ToHashSet();
        if (nome.Equals(DtcOperazioneRegole.StrumentoDtcInjection, StringComparison.OrdinalIgnoreCase)
            && !await ConfermaDeselezioneDtcInjectionAsync(proprieSet.Contains))
        {
            RiselezionaStrumento(opzione);
            return;
        }
        AvvisoStrumentiOperazioni = null;
        if (proprie.Count > 0 && RevisioneCorrente != null)
        {
            RevisioneCorrente.Condizioni.RemoveAll(proprieSet.Contains);
            SincronizzaCondizioniVisual();
            NotificaCoperturaCambiata();
            ModificheNonSalvate = true;
        }
        RimuoviOperazioniVuoteSeSoloDtc();
        NotificaDtcOpzioni();
        // Uno strumento di supporto può non avere operazioni guasto da rimuovere. La deselezione accettata
        // deve comunque attivare il flusso di modifica; le deselezioni annullate sono già uscite sopra.
        if (!opzione.Selezionato && (InModifica || IsNew)) ModificheNonSalvate = true;
    }

    /// <summary>Funzionalita obbligatoria per questa operazione: le operazioni con guasto non sono mai bloccate dalla Funzionalita mancante.</summary>
    private bool FunzionalitaObbligatoriaPer(Condizione c) => FunzionalitaOperazioneObbligatoria && !c.RiguardaDtc;

    /// <summary>Rimuove il riferimento guasto dall'operazione (torna un'operazione normale).</summary>
    public void RimuoviDtcOperazione(Condizione condizione)
    {
        condizione.Guasto = null; condizione.DtcRequisitoId = null; condizione.DtcRequisitoVuotoScelto = null; condizione.DtcPasso = null; condizione.DtcVeicolo = null; condizione.DtcStatoTesto = null;
        ModificheNonSalvate = true;
    }

    // ----- Blocco "Aggiungi guasto" dello strumento guasto Injection (6.5.1, fase 4) ------------------------------------------
    // Un guasto alla volta (i blocchi di codici esistono solo nell'editor del requisito Extra). Si sceglie il componente dal menu (i componenti del test,
    // altrimenti l'anagrafica del veicolo), poi un guasto DISPONIBILE (voci dei requisiti Extra collegati, filtrate per veicolo ed componente: il requisito
    // e quello di provenienza) OPPURE un codice manuale (CodiceValido; requisito vuoto o assegnabile a mano tra i Extra collegati: la voce NON
    // entra nell'anagrafica guasto del requisito). Ogni guasto genera la coppia Validazione/Devalidazione (Strumento = "Guasto Injection"), ciascuna con
    // il proprio esito. Un guasto senza requisito resta valido e salvato nel test ma non compare in Audit. Cambiare componente/codice/requisito o
    // rimuovere un guasto adegua/rimuove le sue due operazioni, ma solo se nessuna ha un esito registrato.

    public static string NessunRequisitoEtichetta => LocalizationService.Instance.Traduci("DtcNessunRequisito");

    /// <summary>Righe dei guasti aggiunti (vista derivata dalle operazioni della REV corrente).</summary>
    public ObservableCollection<DtcInseritoRiga> DtcInseriti { get; } = new();
    public bool HaDtcInseriti => DtcInseriti.Count > 0;

    /// <summary>Il blocco compare con lo strumento guasto Injection selezionato, in modifica sulla REV corrente.</summary>
    public bool MostraBloccoAggiungiDtc => UsaStrumentoDtcInjection && PuoModificareContenuto && RevisioneCorrente != null;

    [ObservableProperty] private string? dtcInsVeicolo;
    [ObservableProperty] private string? dtcInsEcu;
    [ObservableProperty] private DtcOpzione? dtcInsScelto;
    [ObservableProperty] private string dtcInsCodiceManuale = "";
    [ObservableProperty] private string dtcInsRequisitoSel = "";
    [ObservableProperty] private string? dtcInsErrore;
    public bool HaDtcInsErrore => !string.IsNullOrEmpty(DtcInsErrore);
    /// <summary>Requisito assegnabile a mano solo per un guasto manuale (da elenco = requisito di provenienza).</summary>
    public bool DtcInsRequisitoEditabile => DtcInsScelto == null;

    private List<DtcOpzione>? _dtcInsCache;
    private bool _dtcInsInAggiornamento;

    partial void OnDtcInsErroreChanged(string? value) => OnPropertyChanged(nameof(HaDtcInsErrore));

    /// <summary>Cascata Veicolo -> componente -> guasto (come Veicolo x componente x Funzionalita nei test classici): cambiando il veicolo si riazzerano
    /// componente e guasto non piu validi.</summary>
    partial void OnDtcInsVeicoloChanged(string? value)
    {
        _dtcInsCache = null;
        if (DtcInsScelto != null && !SceltoCompatibile(DtcInsScelto, value)) DtcInsScelto = null;
        var componente = DtcInsEcuOpzioni;
        if (DtcInsEcu != null && !componente.Contains(DtcInsEcu, StringComparer.OrdinalIgnoreCase))
        {
            _dtcInjectionEcuSceltaEsplicitamente = false;
            ImpostaEcuDtcInjectionAutomaticamente(null);
        }
        ApplicaDefaultEcuDtcInjection();
        if (DtcInsEcu == null && !_dtcInjectionEcuSceltaEsplicitamente)
            ImpostaEcuDtcInjectionAutomaticamente(componente.Count == 1 ? componente[0] : null);
        if (DtcInsEcu == null && !_dtcInjectionEcuSceltaEsplicitamente && componente.Count > 1)
            ImpostaEcuDtcInjectionAutomaticamente(componente.FirstOrDefault(e => !CoppiaVeicoloEcuCoperta(value ?? "", e)) ?? componente[0]);   // cascata: prima il componente ancora senza guasto
        OnPropertyChanged(nameof(DtcInsEcuOpzioni));
        NotificaElencoDtcIns();
    }

    // ----- Copertura Veicolo x componente con guasto (6.5.2) ---------------------------------------------------------------------
    // Con guasto Injection ogni coppia Veicolo x componente del test deve avere almeno un guasto (una coppia Validazione/Devalidazione). Come per
    // componente x Funzionalita: stesso calcolo mostrato mentre si compila (banner copertura) e, nei casi sotto, bloccato in Valida().

    /// <summary>OGNI coppia Veicolo x componente del test (prodotto cartesiano, senza intersezione col catalogo). <c>Associata</c> = il componente e
    /// proponibile per quel veicolo nel blocco "Aggiungi guasto" (quindi copribile); le altre restano nell'elenco ma non sono copribili da li e
    /// non bloccano il salvataggio. Test senza veicolo/componente: stringa vuota per il lato mancante (resta "almeno un guasto").</summary>
    private List<(string Veicolo, string Componente, bool Associata)> CoppieVeicoloEcuTest()
    {
        var risultato = new List<(string, string, bool)>();
        var intero = EcuVeicoloIntero;
        var ecuTest = TestCase.Componente.Select(e => (e ?? "").Trim()).Where(e => e.Length > 0 && !e.Equals(intero, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var v in DtcInsVeicoliOpzioni.Count == 0 ? new[] { "" } : DtcInsVeicoliOpzioni.ToArray())
        {
            if (ecuTest.Count == 0) { risultato.Add((v, "", true)); continue; }
            var ammesse = EcuOpzioniDtc(v);
            foreach (var e in ecuTest) risultato.Add((v, e, ammesse.Contains(e, StringComparer.OrdinalIgnoreCase)));
        }
        return risultato;
    }

    private bool CoppiaVeicoloEcuCoperta(string veicolo, string componente)
    {
        if (RevisioneCorrente == null) return false;
        // I dati legacy senza veicolo sono risolvibili solo con un unico veicolo nel test.
        // Un passo isolato, o due guasto diversi, non dimostrano una coppia completa.
        var unSoloVeicolo = DtcInsVeicoliOpzioni.Count <= 1;
        var unaSolaEcu = TestCase.Componente.Count <= 1;
        return RevisioneCorrente.Condizioni.Where(c => c.RiguardaDtc
                && OperazioniStrumentoRegole.Appartiene(c, DtcOperazioneRegole.StrumentoDtcInjection)
                && (veicolo.Length == 0 || (string.IsNullOrWhiteSpace(c.DtcVeicolo) ? unSoloVeicolo
                    : c.DtcVeicolo.Trim().Equals(veicolo, StringComparison.OrdinalIgnoreCase)))
                && (componente.Length == 0 || (c.Componente.Count == 0 ? unaSolaEcu
                    : c.Componente.Any(x => (x ?? "").Trim().Equals(componente, StringComparison.OrdinalIgnoreCase)))))
            .GroupBy(c => DtcOperazioneRegole.ChiaveDtc(c.DtcRequisitoId, c.DtcVeicolo, EcuTesto(c), c.Guasto), StringComparer.OrdinalIgnoreCase)
            .Any(g => g.Any(c => c.DtcPasso == PassoDtc.Validazione) && g.Any(c => c.DtcPasso == PassoDtc.Devalidazione));
    }

    /// <summary>Coppie Veicolo x componente del test ancora senza guasto (solo con guasto Injection), nel formato "Veicolo / componente"; le coppie non
    /// associate nel catalogo sono segnalate esplicitamente.</summary>
    public IReadOnlyList<string> VeicoloEcuSenzaDtc => ElencoVeicoloEcuSenzaDtc(soloBloccanti: false);

    /// <summary>Come <see cref="VeicoloEcuSenzaDtc"/> ma solo le coppie copribili dal blocco "Aggiungi guasto" (usato da Valida).</summary>
    private IReadOnlyList<string> VeicoloEcuSenzaDtcBloccanti => ElencoVeicoloEcuSenzaDtc(soloBloccanti: true);

    private IReadOnlyList<string> ElencoVeicoloEcuSenzaDtc(bool soloBloccanti)
    {
        if (RevisioneCorrente == null || !UsaStrumentoDtcInjection) return Array.Empty<string>();
        return CoppieVeicoloEcuTest().Where(p => (!soloBloccanti || p.Associata) && !CoppiaVeicoloEcuCoperta(p.Veicolo, p.Componente))
            .Select(p => p.Veicolo.Length == 0 && p.Componente.Length == 0 ? LocalizationService.Instance.Traduci("CoperturaDtcTestSenzaDtc")
                : string.Join(" / ", new[] { p.Veicolo, p.Componente }.Where(x => x.Length > 0))
                  + (p.Associata ? "" : " " + LocalizationService.Instance.Traduci("CoperturaDtcNonAssociataSuffisso"))).ToList();
    }

    /// <summary>Preseleziona nel blocco "Aggiungi guasto" Veicolo ed componente gia presenti nel test: la prima coppia ancora senza guasto (se tutte ne
    /// hanno, la prima). Una scelta valida dell'utente resta; con <paramref name="avanzaSeCoperta"/> (dopo un'aggiunta) si passa alla prima
    /// coppia ancora scoperta solo se quella corrente ora ha guasto e ne esiste un'altra senza.</summary>
    private void ProponiVeicoloEcuDtcIns(bool avanzaSeCoperta)
    {
        if (!UsaStrumentoDtcInjection) return;
        var coppie = CoppieVeicoloEcuTest().Where(p => p.Associata).Select(p => (p.Veicolo, p.Componente)).ToList();   // preselezione solo su coppie copribili
        if (coppie.Count == 0) return;
        var v = (DtcInsVeicolo ?? "").Trim(); var e = (DtcInsEcu ?? "").Trim();
        bool Eq(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
        var scoperte = coppie.Where(p => !CoppiaVeicoloEcuCoperta(p.Veicolo, p.Componente)).ToList();
        (string Veicolo, string Componente) scelta;
        if (v.Length > 0 && e.Length > 0 && coppie.Any(p => Eq(p.Veicolo, v) && Eq(p.Componente, e)))
        {
            if (!avanzaSeCoperta || scoperte.Count == 0 || !CoppiaVeicoloEcuCoperta(v, e)) return;
            // Una scelta esplicita resta prioritaria mentre si compila la coppia corrente. Dopo averla
            // aggiunta, però, la cascata deve poter avanzare alla prima coppia ancora scoperta.
            _dtcInjectionEcuSceltaEsplicitamente = false;
            scelta = scoperte[0];
        }
        else
        {
            var candidate = coppie.Where(p => (v.Length == 0 || Eq(p.Veicolo, v)) && (e.Length == 0 || Eq(p.Componente, e))).ToList();
            if (candidate.Count == 0) candidate = coppie;
            scelta = candidate.FirstOrDefault(p => !CoppiaVeicoloEcuCoperta(p.Veicolo, p.Componente));
            if (scelta == default) scelta = candidate[0];
        }
        if (scelta.Veicolo.Length > 0 && !Eq(scelta.Veicolo, v)) DtcInsVeicolo = scelta.Veicolo;
        if (!_dtcInjectionEcuSceltaEsplicitamente && scelta.Componente.Length > 0 && !Eq(scelta.Componente, (DtcInsEcu ?? "").Trim()))
            ImpostaEcuDtcInjectionAutomaticamente(scelta.Componente);
    }

    private static bool SceltoCompatibile(DtcOpzione o, string? veicolo) =>
        string.IsNullOrWhiteSpace(o.Veicolo) || string.Equals(o.Veicolo.Trim(), (veicolo ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    partial void OnDtcInsEcuChanged(string? value)
    {
        if (!_dtcInjectionAssegnazioneAutomaticaEcu)
            _dtcInjectionEcuSceltaEsplicitamente = true;
        _dtcInsCache = null;
        if (DtcInsScelto != null && !string.Equals(DtcInsScelto.Componente, value?.Trim(), StringComparison.OrdinalIgnoreCase)) DtcInsScelto = null;
        NotificaElencoDtcIns();
    }

    partial void OnDtcInsSceltoChanged(DtcOpzione? value)
    {
        OnPropertyChanged(nameof(DtcInsRequisitoEditabile));
        OnPropertyChanged(nameof(DtcInsSceltoEtichetta));
        if (_dtcInsInAggiornamento) return;
        _dtcInsInAggiornamento = true;
        try
        {
            if (value != null) { DtcInsCodiceManuale = ""; DtcInsRequisitoSel = value.RequisitoId; }
            else if (string.IsNullOrWhiteSpace(DtcInsCodiceManuale)) DtcInsRequisitoSel = NessunRequisitoEtichetta;
        }
        finally { _dtcInsInAggiornamento = false; }
    }

    partial void OnDtcInsCodiceManualeChanged(string value)
    {
        if (_dtcInsInAggiornamento || string.IsNullOrWhiteSpace(value) || DtcInsScelto == null) return;
        _dtcInsInAggiornamento = true;
        try { DtcInsScelto = null; DtcInsRequisitoSel = NessunRequisitoEtichetta; }
        finally { _dtcInsInAggiornamento = false; }
    }

    public string DtcInsSceltoEtichetta => DtcInsScelto?.Etichetta ?? LocalizationService.Instance.Traduci("DtcInsDtcPlaceholder");

    private void NotificaElencoDtcIns()
    {
        OnPropertyChanged(nameof(DtcInsDisponibili));
        OnPropertyChanged(nameof(DtcInsMostraCombo));
        OnPropertyChanged(nameof(DtcInsMostraPicker));
    }

    /// <summary>Da chiamare quando cambiano strumenti, requisiti o componente: ricalcola componente proponibili, elenco e visibilita del blocco.</summary>
    private void NotificaBloccoAggiungiDtc()
    {
        _dtcInsCache = null;
        // Prima si rinotificano le liste e POI si assegnano le selezioni: sostituire ItemsSource di una ComboBox azzera il suo SelectedItem
        // (e il binding a due vie scriverebbe null nel VM), cancellando la preselezione appena calcolata.
        OnPropertyChanged(nameof(DtcInsVeicoliOpzioni));
        OnPropertyChanged(nameof(DtcInsEcuOpzioni));
        OnPropertyChanged(nameof(DtcInsRequisitiOpzioni));
        var veicoli = DtcInsVeicoliOpzioni;
        ApplicaDefaultDtcInjection(veicoli);
        if (DtcInsVeicolo == null || !veicoli.Contains(DtcInsVeicolo, StringComparer.OrdinalIgnoreCase)) DtcInsVeicolo = veicoli.Count == 1 ? veicoli[0] : null;
        var componente = DtcInsEcuOpzioni;
        if (DtcInsEcu != null && !componente.Contains(DtcInsEcu, StringComparer.OrdinalIgnoreCase))
        {
            _dtcInjectionEcuSceltaEsplicitamente = false;
            ImpostaEcuDtcInjectionAutomaticamente(null);
        }
        if (DtcInsEcu == null && !_dtcInjectionEcuSceltaEsplicitamente)
            ImpostaEcuDtcInjectionAutomaticamente(componente.Count == 1 ? componente[0] : null);
        ProponiVeicoloEcuDtcIns(avanzaSeCoperta: false);
        if (string.IsNullOrWhiteSpace(DtcInsRequisitoSel)) DtcInsRequisitoSel = NessunRequisitoEtichetta;
        ApplicaDefaultCodiceDtcInjection();
        OnPropertyChanged(nameof(MostraBloccoAggiungiDtc));
        NotificaElencoDtcIns();
    }

    /// <summary>Veicoli del test tra cui scegliere (default: l'unico veicolo del test).</summary>
    // Stessa istanza finche il contenuto non cambia: una ComboBox a cui si risostituisce ItemsSource perde SelectedItem (e a due vie scrive null).
    private IReadOnlyList<string> _dtcInsVeicoliCache = Array.Empty<string>();
    public IReadOnlyList<string> DtcInsVeicoliOpzioni
    {
        get
        {
            var nuova = TestCase.VeicoliEffettivi.Select(v => (v ?? "").Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!nuova.SequenceEqual(_dtcInsVeicoliCache, StringComparer.Ordinal)) _dtcInsVeicoliCache = nuova;
            return _dtcInsVeicoliCache;
        }
    }

    /// <summary>componente proponibili per il veicolo scelto: quelle dell'anagrafica montate su quel veicolo (stessa sorgente di
    /// <see cref="EcuDisponibili"/>), ristrette ai componenti del test se ne ha; se la restrizione le azzera, i componenti del test. Nessun veicolo scelto
    /// (test con piu veicoli) = nessuna componente.</summary>
    public IReadOnlyList<string> DtcInsEcuOpzioni => EcuOpzioniDtc(DtcInsVeicolo);

    private IReadOnlyList<string> EcuOpzioniDtc(string? veicolo)
    {
        var v = (veicolo ?? "").Trim();
        if (v.Length == 0 && DtcInsVeicoliOpzioni.Count > 1) return Array.Empty<string>();
        var intero = EcuVeicoloIntero;
        var test = TestCase.Componente.Select(e => (e ?? "").Trim()).Where(e => e.Length > 0 && !e.Equals(intero, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var anagrafica = EcuAnagraficaPerVeicoli(v.Length == 0 ? Array.Empty<string>() : new[] { v }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (test.Count == 0) return anagrafica;
        var comuni = test.Where(e => anagrafica.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
        return comuni.Count > 0 ? comuni : test;
    }

    /// <summary>Requisiti Extra collegati al test (assegnabili al guasto), preceduti dalla voce "(nessuno)".</summary>
    public IReadOnlyList<string> DtcInsRequisitiOpzioni => new[] { NessunRequisitoEtichetta }.Concat(RequisitiD4dCollegatiIds()).ToList();

    private List<string> RequisitiD4dCollegatiIds() =>
        RisolviConteggiDtc().Where(r => TestCase.RequisitoId.Contains(r.Id) && RequisitoApplicabilita.ED4D(r)).Select(r => r.Id).ToList();

    private static string? RequisitoDaEtichetta(string? etichetta)
    {
        var e = (etichetta ?? "").Trim();
        return e.Length == 0 || e.Equals(NessunRequisitoEtichetta, StringComparison.OrdinalIgnoreCase) ? null : e;
    }

    private bool DtcInsListaGrande => RisolviConteggiDtc()
        .Where(r => TestCase.RequisitoId.Contains(r.Id) && RequisitoApplicabilita.ED4D(r)).Sum(r => r.DtcTotale) > SogliaConteggioEsattoDtc;

    /// <summary>guasto disponibili per il componente scelta (nessuno con elenchi molto grandi: li propone il picker, costruito fuori dal thread UI).</summary>
    public IReadOnlyList<DtcOpzione> DtcInsDisponibili => _dtcInsCache ??= DtcInsListaGrande || VeicoloDtcMancante ? new List<DtcOpzione>() : CostruisciDtcIns();
    private bool VeicoloDtcMancante => string.IsNullOrWhiteSpace(DtcInsVeicolo) && DtcInsVeicoliOpzioni.Count > 1;
    public bool DtcInsMostraCombo => !DtcInsListaGrande && DtcInsDisponibili.Count <= SogliaPickerDtc;
    public bool DtcInsMostraPicker => !DtcInsMostraCombo;

    private List<DtcOpzione> CostruisciDtcIns() => CostruisciDtcInsDaSnapshot(SnapshotDtcIns());

    /// <summary>Copia degli input dell'elenco guasto del blocco, presa sul thread UI: la costruzione dell'elenco (anche in Task.Run) non legge piu
    /// collezioni osservabili (DtcInseriti, requisiti del test) che la UI potrebbe modificare nel frattempo.</summary>
    private sealed record SnapshotDtcInsDati(List<Requisito> Requisiti, IReadOnlyList<string> Veicoli, string Veicolo, string? Componente, HashSet<string> Chiavi);

    private SnapshotDtcInsDati SnapshotDtcIns() => new(
        RequisitiDisponibili.Where(r => TestCase.RequisitoId.Contains(r.Id)).ToList(),
        string.IsNullOrWhiteSpace(DtcInsVeicolo) ? DtcInsVeicoliOpzioni.ToList() : new List<string> { DtcInsVeicolo.Trim() },
        (DtcInsVeicolo ?? "").Trim(), DtcInsEcu,
        DtcInseriti.Select(r => r.Chiave).ToHashSet(StringComparer.OrdinalIgnoreCase));

    private List<DtcOpzione> CostruisciDtcInsDaSnapshot(SnapshotDtcInsDati s) => DtcOperazioneRegole.DtcDisponibili(
        s.Requisiti, VociDtc, s.Veicoli, s.Componente,
        o => s.Chiavi.Contains(DtcOperazioneRegole.ChiaveDtc(o.RequisitoId, s.Veicolo, o.Componente, o.Codice)));

    private bool EsisteDtcInserito(string? requisitoId, string? veicolo, string? componente, string? codice, DtcInseritoRiga? esclusa = null)
    {
        var chiave = DtcOperazioneRegole.ChiaveDtc(requisitoId, veicolo, componente, codice);
        return DtcInseriti.Any(r => r != esclusa && string.Equals(r.Chiave, chiave, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Picker con ricerca per elenchi grandi: etichette costruite in un Task.Run, mai sul thread UI.</summary>
    [RelayCommand]
    private async Task ScegliDtcIns()
    {
        if (VeicoloDtcMancante) { ImpostaErroreDtcIns("DtcVeicoloMancante"); return; }
        if (string.IsNullOrWhiteSpace(DtcInsEcu)) { ImpostaErroreDtcIns("DtcEcuMancante"); return; }
        var snapshot = SnapshotDtcIns();   // sul thread UI: DtcInseriti e un'ObservableCollection, non si enumera da Task.Run
        var voci = await Task.Run(() => CostruisciDtcInsDaSnapshot(snapshot)
            .Select(o => new VoceSelezionabile { Valore = o, TestoPrincipale = o.Etichetta, TestoRicerca = o.Etichetta }).ToList());
        var scelta = await SelezionaDialogService.Scegli(LocalizationService.Instance.Traduci("DtcInsDtcPlaceholder"), voci);
        if (scelta?.Valore is DtcOpzione o) { DtcInsScelto = o; DtcInsErrore = null; }
    }

    private void ImpostaErroreDtcIns(string? chiave) =>
        DtcInsErrore = chiave == null ? null : LocalizationService.Instance.Traduci(chiave);

    /// <summary>Comando "Aggiungi guasto": dall'elenco (requisito di provenienza) oppure manuale (codice digitato).</summary>
    [RelayCommand]
    private void AggiungiDtc()
    {
        var chiave = DtcInsScelto is { } s
            ? AggiungiDtcInjection(s.Componente, s.Codice, s.RequisitoId, s.Veicolo ?? DtcInsVeicolo)
            : AggiungiDtcInjection(DtcInsEcu, DtcInsCodiceManuale, RequisitoDaEtichetta(DtcInsRequisitoSel), DtcInsVeicolo);
        ImpostaErroreDtcIns(chiave);
        if (chiave != null) return;
        _dtcInsInAggiornamento = true;
        try { DtcInsScelto = null; DtcInsCodiceManuale = ""; DtcInsRequisitoSel = NessunRequisitoEtichetta; }
        finally { _dtcInsInAggiornamento = false; }
        _dtcInsCache = null;
        ProponiVeicoloEcuDtcIns(avanzaSeCoperta: true);
        NotificaElencoDtcIns();
    }

    /// <summary>Aggiunge UN guasto al test creando nella REV corrente la coppia Validazione + Devalidazione dello strumento guasto Injection.
    /// Ritorna null se riuscito, altrimenti la chiave di localizzazione dell'errore (nulla modificato).</summary>
    public string? AggiungiDtcInjection(string? componente, string? codice, string? requisitoId, string? veicolo = null)
    {
        if (RevisioneCorrente == null || VisualizzaStorico || !UsaStrumentoDtcInjection) return "DtcInsNonDisponibile";
        var cod = (codice ?? "").Trim(); var e = (componente ?? "").Trim(); var sv = (veicolo ?? "").Trim();
        var veicoliTest = DtcInsVeicoliOpzioni;
        if (sv.Length == 0 && veicoliTest.Count == 1) sv = veicoliTest[0];
        if (sv.Length == 0 && veicoliTest.Count > 1) return "DtcVeicoloMancante";
        if (sv.Length > 0 && veicoliTest.Count > 0)
        {
            var canonico = veicoliTest.FirstOrDefault(x => x.Equals(sv, StringComparison.OrdinalIgnoreCase));
            if (canonico == null) return "DtcVeicoloNonDelTest";
            sv = canonico;
        }
        var collegati = RequisitiD4dCollegatiIds();
        var req = collegati.FirstOrDefault(x => x.Equals((requisitoId ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? (requisitoId ?? "").Trim();
        var ecuAmmesse = EcuOpzioniDtc(sv);
        e = ecuAmmesse.FirstOrDefault(x => x.Equals(e, StringComparison.OrdinalIgnoreCase)) ?? e;
        var errore = DtcOperazioneRegole.ValidaAggiuntaDtc(cod, e, ecuAmmesse, req, collegati, EsisteDtcInserito(req, sv, e, cod));
        if (errore != null) return errore;
        var nome = DtcOperazioneRegole.StrumentoDtcInjection;
        InserisciOperazioniStrumento(nome, DtcOperazioneRegole.CoppiaDtc().Select(passo => new Condizione
        {
            Esito = Esito.DaEseguire, Strumento = nome, Guasto = cod, DtcRequisitoId = req.Length == 0 ? null : req, DtcRequisitoVuotoScelto = req.Length == 0 ? true : null,
            DtcVeicolo = sv.Length == 0 ? null : sv, DtcPasso = passo, Componente = new EcuSelection(new[] { e }),
        }).ToList());
        return null;
    }

    /// <summary>Ricostruisce le righe dalle operazioni (annulla visivamente una modifica rifiutata).</summary>
    public void RiallineaDtcInseriti() => RicostruisciDtcInseriti();

    private static string EcuTesto(Condizione c) => string.Join(",", c.Componente.Select(e => (e ?? "").Trim()).Where(e => e.Length > 0));

    private void RicostruisciDtcInseriti()
    {
        DtcInseriti.Clear();
        var nome = DtcOperazioneRegole.StrumentoDtcInjection;
        if (RevisioneCorrente != null)
            foreach (var g in RevisioneCorrente.Condizioni.Where(c => c.RiguardaDtc && OperazioniStrumentoRegole.Appartiene(c, nome))
                         .GroupBy(c => DtcOperazioneRegole.ChiaveDtc(c.DtcRequisitoId, c.DtcVeicolo, EcuTesto(c), c.Guasto), StringComparer.OrdinalIgnoreCase))
            {
                var primo = g.First();
                DtcInseriti.Add(new DtcInseritoRiga(primo.Guasto!.Trim(), EcuTesto(primo),
                    string.IsNullOrWhiteSpace(primo.DtcVeicolo) ? null : primo.DtcVeicolo.Trim(), (primo.DtcRequisitoId ?? "").Trim(), g.ToList()));
            }
        _dtcInsCache = null;
        OnPropertyChanged(nameof(HaDtcInseriti));
        NotificaElencoDtcIns();
    }

    /// <summary>Le modifiche di una riga (componente, codice, requisito) e la rimozione sono ammesse solo senza esiti registrati sulle sue operazioni.</summary>
    private bool PuoModificareRigaDtc(DtcInseritoRiga riga, out string? errore)
    {
        errore = null;
        if (RevisioneCorrente == null || VisualizzaStorico || !DtcInseriti.Contains(riga)) errore = "DtcInsNonDisponibile";
        else if (!riga.Modificabile) errore = "DtcInsBloccatoEsiti";
        ImpostaErroreDtcIns(errore);
        return errore == null;
    }

    private void DopoModificaRigaDtc()
    {
        ModificheNonSalvate = true;
        RicostruisciDtcInseriti();
        NotificaCoperturaCambiata();
        _ = AggiornaStatoDtcOperazioniAsync();
    }

    /// <summary>Assegna (o toglie con null/"(nessuno)") il requisito Extra collegato al test a QUESTO guasto, su entrambe le sue operazioni.</summary>
    public string? ImpostaRequisitoDtcInserito(DtcInseritoRiga riga, string? requisitoId)
    {
        if (!PuoModificareRigaDtc(riga, out var errore)) return errore;
        var collegati = RequisitiD4dCollegatiIds();
        var richiesto = RequisitoDaEtichetta(requisitoId) ?? "";
        var req = collegati.FirstOrDefault(x => x.Equals(richiesto, StringComparison.OrdinalIgnoreCase)) ?? richiesto;
        var cambiato = !req.Equals(riga.RequisitoId, StringComparison.OrdinalIgnoreCase);
        errore = DtcOperazioneRegole.ValidaAggiuntaDtc(riga.Codice, riga.Componente, new[] { riga.Componente }, req, collegati,
            cambiato && EsisteDtcInserito(req, riga.Veicolo, riga.Componente, riga.Codice, riga));
        if (errore != null) { ImpostaErroreDtcIns(errore); return errore; }
        foreach (var o in riga.Operazioni) { o.DtcRequisitoId = req.Length == 0 ? null : req; o.DtcRequisitoVuotoScelto = req.Length == 0 ? true : null; }
        DopoModificaRigaDtc();
        return null;
    }

    /// <summary>Cambia il componente del guasto su entrambe le operazioni (il componente deve essere tra quelle proponibili).</summary>
    public string? CambiaEcuDtcInserito(DtcInseritoRiga riga, string? componente)
    {
        if (!PuoModificareRigaDtc(riga, out var errore)) return errore;
        var ammesse = EcuOpzioniDtc(riga.Veicolo);
        var e = ammesse.FirstOrDefault(x => x.Equals((componente ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? (componente ?? "").Trim();
        errore = DtcOperazioneRegole.ValidaAggiuntaDtc(riga.Codice, e, ammesse, null, Array.Empty<string>(),
            !e.Equals(riga.Componente, StringComparison.OrdinalIgnoreCase) && EsisteDtcInserito(riga.RequisitoId, riga.Veicolo, e, riga.Codice, riga));
        if (errore != null) { ImpostaErroreDtcIns(errore); return errore; }
        foreach (var o in riga.Operazioni) o.Componente = new EcuSelection(new[] { e });
        DopoModificaRigaDtc();
        return null;
    }

    /// <summary>Cambia il codice del guasto su entrambe le operazioni (formato validato, nessun duplicato); la precompilazione "Guasto - Atteso" segue.</summary>
    public string? CambiaCodiceDtcInserito(DtcInseritoRiga riga, string? codice)
    {
        if (!PuoModificareRigaDtc(riga, out var errore)) return errore;
        var c = (codice ?? "").Trim();
        errore = DtcOperazioneRegole.ValidaAggiuntaDtc(c, riga.Componente, new[] { riga.Componente }, null, Array.Empty<string>(),
            !c.Equals(riga.Codice, StringComparison.OrdinalIgnoreCase) && EsisteDtcInserito(riga.RequisitoId, riga.Veicolo, riga.Componente, c, riga));
        if (errore != null) { ImpostaErroreDtcIns(errore); return errore; }
        foreach (var o in riga.Operazioni) { var vecchio = o.Guasto; o.Guasto = c; SincronizzaAttesoDtcInjection(o, vecchio); }
        DopoModificaRigaDtc();
        return null;
    }

    /// <summary>Rimuove il guasto e le sue due operazioni dalla REV corrente (bloccato se una ha un esito registrato).</summary>
    public string? RimuoviDtcInserito(DtcInseritoRiga riga, bool perditaDatiConfermata = false)
    {
        if (!PuoModificareRigaDtc(riga, out var errore)) return errore;
        if (!perditaDatiConfermata && riga.Operazioni.Any(DtcOperazioneRegole.HaDatiUtente))
        {
            ImpostaErroreDtcIns("DtcInsConDati");
            return "DtcInsConDati";
        }
        RevisioneCorrente!.Condizioni.RemoveAll(riga.Operazioni.Contains);
        SincronizzaCondizioniVisual();
        NotificaCoperturaCambiata();
        ModificheNonSalvate = true;
        return null;
    }

    /// <summary>Rimozione del guasto dalla UI: se le sue operazioni contengono dati compilati chiede prima conferma con l'elenco di cio che si
    /// perde. Ritorna true se il guasto e stato rimosso.</summary>
    public async Task<bool> RimuoviDtcInseritoConfermaAsync(DtcInseritoRiga riga)
    {
        if (!PuoModificareRigaDtc(riga, out _)) return false;
        if (!await ConfermaPerditaDatiAsync(riga.Operazioni)) return false;
        return RimuoviDtcInserito(riga, perditaDatiConfermata: true) == null;
    }

    private List<Veicolo> _tuttiIVeicoli = new();
    private List<StrumentoDiagnostico> _tuttiGliStrumenti = new();
    private List<TestTemplate> _tuttiITemplate = new();
    private readonly bool _modalitaChangeRequest;

    [ObservableProperty] private List<CampoStrumentoValore> campiTestCostanti = new();
    [ObservableProperty] private List<GruppoCampiStrumento> gruppiTestCostanti = new();

    /// <summary>Campi con Ambito=PerRevisione (Addendum §3, feedback QA): stabili tra le operazioni della
    /// stessa revisione senza doverli reinserire ogni volta, mostrati subito sotto "Strumenti usati" — a
    /// differenza di CampiTestCostanti (mai cambiano in tutto il test) e dei campi per-operazione dentro ogni
    /// Condizione.</summary>
    [ObservableProperty] private List<CampoStrumentoValore> campiRevisioneCostanti = new();
    [ObservableProperty] private List<GruppoCampiStrumento> gruppiRevisioneCostanti = new();

    /// <summary>Un banco di integrazione non è distinto da un veicolo reale nei dati: stessa tabella, stesso
    /// elenco per ogni tipo di test. La distinzione banco/veicolo la dà solo il Tipo test scelto, non un
    /// campo sul Veicolo (Addendum redesign 2026-08, revisione feedback QA).</summary>
    public List<string> VeicoliDisponibili => _tuttiIVeicoli
        .Where(v => !v.Archiviata || TestCase.VeicoliEffettivi.Contains(v.Nome, StringComparer.OrdinalIgnoreCase))
        .Select(v => v.Nome).ToList();

    public List<string> OperatoriDisponibili { get; private set; } = new();
    /// <summary>Oggetti completi, non solo l'Id (Addendum: collaudo live 2026-08-17, punto 3 del grill-me):
    /// l'AutoCompleteBox di ricerca mostra anche Requisito.Testo nel menu a tendina, non solo l'identificativo.</summary>
    public List<Requisito> RequisitiDisponibili { get; private set; } = new();
    /// <summary>Candidati per l'AutoCompleteBox di ricerca dei Test collegati (Addendum: collaudo live
    /// 2026-08-17, punto 3 del grill-me — prima un TextBox a digitazione libera, senza nessuna validazione
    /// contro test realmente esistenti: un errore di battitura creava un riferimento silenziosamente rotto).
    /// Esclude i test già eliminati e sé stesso; non esclude i già collegati, così restano visibili/ricercabili
    /// anche se AggiungiTestCorrelato poi li ignora silenziosamente (stesso guard-clause di prima).</summary>
    public List<TestCase> TestCorrelatiDisponibili { get; }

    private List<Funzionalita> _tutteLeFunzionalita = new();

    /// <summary>Filtrata per i componenti già scelte sul test (feedback utente 2026-09-23: "le funzionalità...
    /// devono poter essere assegnate a una o più componente, in modo da non dover scorrere troppo all'interno del
    /// test"), stesso principio di EcuDisponibili sopra ma nel verso opposto (qui è Componente che restringe
    /// Funzionalita, là erano i Veicoli a restringere i componenti): con almeno un componente scelta mostra solo le
    /// funzionalità associate a QUALUNQUE di quelle componente più quelle generiche (TutteLeEcu=true, valide
    /// ovunque) — mai le une senza le altre, altrimenti una funzionalità non ancora categorizzata sparirebbe
    /// dall'elenco. MostraTutteLeFunzionalita e "nessuna componente scelta ancora" riportano al catalogo intero, così
    /// il filtro non blocca mai l'operatore anche con un popolamento di EcuAssociate incompleto o sbagliato.
    /// Le Funzionalita già collegate al test restano sempre visibili (stesso trattamento del componente corrente in
    /// EcuDisponibili), altrimenti rimuovere l'ultima componente associata a una funzionalità già scelta la
    /// farebbe sparire dal picker pur restando su TestCase.Funzionalita.</summary>
    public List<string> FunzionalitaDisponibili
    {
        get
        {
            var attive = _tutteLeFunzionalita.Where(f => !f.Archiviata);
            if (MostraTutteLeFunzionalita || TestCase.Componente.Count == 0)
                return attive.Select(f => f.Nome).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            var ecuScelte = TestCase.Componente.ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Con veicoli scelti sul test una funzionalità conta se vale su almeno una coppia (componente scelta, veicolo scelto),
            // restrizioni per componente comprese (Funzionalita.VeicoliPerEcu) intersecate con il montaggio del componente, come nella matrice:
            // una voce residua di un veicolo tolto dal componente non fa comparire la funzionalità. Senza veicoli resta la verifica sui componenti.
            var veicoliScelti = TestCase.Veicoli.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            var ecuCatalogo = _tutteLeEcu.GroupBy(e => e.Nome, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            bool ValeSuCoppia(Funzionalita f, string componente, string veicolo) =>
                ecuCatalogo.TryGetValue(componente, out var catalogo)
                    ? AssociazioniVeicoloService.ValeSuVeicoloMontato(f, catalogo, veicolo)
                    : f.ValeSu(componente, veicolo);
            var filtrate = attive
                .Where(f => ecuScelte.Any(componente => veicoliScelti.Count == 0
                    ? f.ValeSuEcu(componente)
                    : veicoliScelti.Any(veicolo => ValeSuCoppia(f, componente, veicolo))))
                .Select(f => f.Nome)
                .ToList();
            foreach (var nome in TestCase.Funzionalita)
                if (!filtrate.Contains(nome, StringComparer.OrdinalIgnoreCase)) filtrate.Add(nome);
            return filtrate.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>Elenco piatto degli strumenti master data (non i wrapper con checkbox di StrumentiOptions):
    /// usato per la ComboBox "Strumento principale" di ogni prova, così scegliere uno strumento già censito è
    /// un click invece di doverne ridigitare il nome esatto (niente typo che spezzano i confronti/i filtri).
    /// IsEditable resta true: uno strumento nuovo non ancora in anagrafica va comunque potuto scrivere a mano.</summary>
    public List<string> StrumentiNomiDisponibili { get; private set; } = new();

    /// <summary>Sostituisce il vecchio TipiTest (enum fisso): elenco dei TestTemplate non archiviati, master
    /// data modificabile da Impostazioni → Tipi di test (Addendum redesign 2026-08, punto 2). Filtrato anche
    /// per IsChangeRequest (feedback utente 2026-08-21, punto 11): il wizard "nuovo Test" non deve mai
    /// proporre un template Change Request e viceversa — sono due percorsi di creazione distinti anche se
    /// condividono lo stesso motore.</summary>
    public List<TestTemplate> TemplatesDisponibili => _tuttiITemplate
        .Where(t => (!t.Archiviata || t.Uid == TestCase.TestTemplateUid) && t.IsChangeRequest == _modalitaChangeRequest)
        .ToList();

    /// <summary>Il TestTemplate scelto per QUESTO test (via TestCase.TestTemplateUid), non necessariamente
    /// TemplateSelezionato — quest'ultimo è solo il wrapper osservabile per il binding del form.</summary>
    public TestTemplate? TemplateCorrente => _tuttiITemplate.FirstOrDefault(t => t.Uid == TestCase.TestTemplateUid);

    // ----- Visibilità dei campi Dettagli/Pianificazione guidata dal template (Addendum: collaudo live
    // 2026-08-17) — un Master può nascondere/mostrare ogni campo di sistema da Impostazioni → Tipi di test, e
    // condizionare la visibilità all'esito della revisione (es. "Descrizione problema" solo su esito
    // problematico, prima fisso nel codice). Il widget e la posizione nel form restano quelli esistenti: solo
    // visibilità/obbligatorietà/condizione sono guidati dal template — non ancora il riordino libero o i campi
    // realmente nuovi (CampoLibero, sezioni personalizzate), che restano un passo successivo.

    private CampoTemplate? TrovaCampoTemplate(string chiave) =>
        TemplateCorrente?.CampiPianificazione.FirstOrDefault(c => c.Tipo == TipoCampoTemplate.CampoSistema && c.CampoSistemaChiave == chiave)
        ?? TemplateCorrente?.Sezioni.SelectMany(s => s.Campi).FirstOrDefault(c => c.Tipo == TipoCampoTemplate.CampoSistema && c.CampoSistemaChiave == chiave)
        ?? TemplateCorrente?.CampiRiferimenti.FirstOrDefault(c => c.Tipo == TipoCampoTemplate.CampoSistema && c.CampoSistemaChiave == chiave);

    private bool MostraCampo(string chiave)
    {
        if (chiave == CampiSistema.Funzionalita && SoloStrumentiSupporto) return true;
        if (chiave == CampiSistema.Funzionalita && SoloOperazioniDtc) return false;   // 6.5.2: solo guasto = niente Funzionalita (ne mostrata ne richiesta)
        var campo = TrovaCampoTemplate(chiave);
        if (campo == null) return false;
        if (campo.CondizioneEsito.Count == 0) return true;
        // Stessa fonte già usata dal vecchio MostraDescrizioneProblema fisso: l'esito della revisione
        // CORRENTE/editabile, non quella eventualmente sfogliata dal menu REV (il form editabile di
        // Dettagli/Pianificazione è comunque nascosto durante la navigazione dello storico).
        return campo.CondizioneEsito.Contains(RevisioneCorrenteEsito);
    }

    // ----- Riordino dei campi classici (spec riordino campi 2026-09-23 §2, CampiClassiciOrdineService): il
    // layout dei gruppi Dettagli (Veicolo/Componente/Matricola/BaselineSw/BaselineHw) e Pianificazione (Stato lavoro/
    // Scadenza/Assegnatario/Assegnato da) è ora guidato da CampoTemplate.Ordine, non più una posizione fissa
    // in XAML. Calcolato una sola volta per cambio di template (RicalcolaOrdineCampiClassici, richiamata da
    // NotificaVisibilitaCampiTemplate), non ad ogni digitazione/redraw — evita di ricostruire il layout ad
    // ogni apertura del Test se il template non è cambiato (vincolo prestazioni della spec).
    private static readonly string[] ChiaviGrigliaDettagli =
        { CampiSistema.Veicolo, CampiSistema.Componente, CampiSistema.Matricola, CampiSistema.BaselineSw, CampiSistema.BaselineHw };
    private static readonly string[] ChiaviGrigliaPianificazione =
        { CampiSistema.StatoLavoro, CampiSistema.Scadenza, CampiSistema.Assegnatario, CampiSistema.AssegnatoDa };

    // Celle fisiche della griglia riservate a questi campi (le altre celle restano fisse: TipoTest in
    // Dettagli, Severità/Priorità/Data assegnazione in Pianificazione — mai stati configurabili).
    private static readonly (int Riga, int Colonna)[] CelleDettagli = { (0, 0), (0, 1), (1, 0), (1, 1), (1, 2) };
    private static readonly (int Riga, int Colonna)[] CellePianificazione = { (0, 0), (0, 1), (1, 0), (1, 1) };

    private IReadOnlyDictionary<string, int> _rangoDettagli =
        CampiClassiciOrdineService.CalcolaRango(ChiaviGrigliaDettagli, _ => null, usaOrdineTemplate: false);
    private IReadOnlyDictionary<string, int> _rangoPianificazione =
        CampiClassiciOrdineService.CalcolaRango(ChiaviGrigliaPianificazione, _ => null, usaOrdineTemplate: false);

    private void RicalcolaOrdineCampiClassici()
    {
        var template = TemplateCorrente;
        _rangoDettagli = CampiClassiciOrdineService.CalcolaRango(ChiaviGrigliaDettagli, k => TrovaCampoTemplate(k)?.Ordine,
            usaOrdineTemplate: template?.CampiClassiciDettagliRiordinati == true);
        // Pianificazione non ha l'anomalia storica di Dettagli (Componente/Veicolo): il seed 6.3.0 segue già
        // CampiSistema.ChiaviPianificazione nello stesso ordine mostrato da sempre in TestCaseEditorView, quindi
        // leggere Ordine direttamente è già retro-compatibile senza bisogno di alcun flag/migrazione.
        _rangoPianificazione = CampiClassiciOrdineService.CalcolaRango(ChiaviGrigliaPianificazione, k => TrovaCampoTemplate(k)?.Ordine,
            usaOrdineTemplate: true);
        _rangoBlocchiDettagli = CampiClassiciOrdineService.CalcolaRango(ChiaviBlocchiDettagli, k => TrovaCampoTemplate(k)?.Ordine, usaOrdineTemplate: true);
        _rangoBlocchiRiferimenti = CampiClassiciOrdineService.CalcolaRango(ChiaviBlocchiRiferimenti, k => TrovaCampoTemplate(k)?.Ordine, usaOrdineTemplate: true);

        OnPropertyChanged(nameof(RigaFunzionalita)); OnPropertyChanged(nameof(RigaStrumentiUsati));
        OnPropertyChanged(nameof(RigaDescrizioneBreve)); OnPropertyChanged(nameof(RigaDescrizione));
        OnPropertyChanged(nameof(RigaRequisitiCollegati)); OnPropertyChanged(nameof(RigaTestCorrelati)); OnPropertyChanged(nameof(RigaTicketCollegati));
        OnPropertyChanged(nameof(RigaVeicolo)); OnPropertyChanged(nameof(ColonnaVeicolo));
        OnPropertyChanged(nameof(RigaEcu)); OnPropertyChanged(nameof(ColonnaEcu));
        OnPropertyChanged(nameof(RigaVin)); OnPropertyChanged(nameof(ColonnaVin));
        OnPropertyChanged(nameof(RigaBaselineSw)); OnPropertyChanged(nameof(ColonnaBaselineSw));
        OnPropertyChanged(nameof(RigaBaselineHw)); OnPropertyChanged(nameof(ColonnaBaselineHw));
        OnPropertyChanged(nameof(RigaStatoLavoro)); OnPropertyChanged(nameof(ColonnaStatoLavoro));
        OnPropertyChanged(nameof(RigaScadenza)); OnPropertyChanged(nameof(ColonnaScadenza));
        OnPropertyChanged(nameof(RigaAssegnatario)); OnPropertyChanged(nameof(ColonnaAssegnatario));
        OnPropertyChanged(nameof(RigaAssegnatoDa)); OnPropertyChanged(nameof(ColonnaAssegnatoDa));
    }

    private static (int Riga, int Colonna) Cella((int Riga, int Colonna)[] celle, IReadOnlyDictionary<string, int> rango, string chiave)
    {
        var i = rango.TryGetValue(chiave, out var r) ? r : 0;
        return celle[Math.Clamp(i, 0, celle.Length - 1)];
    }

    public int RigaVeicolo => RigaCella(Cella(CelleDettagli, _rangoDettagli, CampiSistema.Veicolo));
    public int ColonnaVeicolo => ColonnaCella(Cella(CelleDettagli, _rangoDettagli, CampiSistema.Veicolo));
    public int RigaEcu => RigaCella(Cella(CelleDettagli, _rangoDettagli, CampiSistema.Componente));
    public int ColonnaEcu => ColonnaCella(Cella(CelleDettagli, _rangoDettagli, CampiSistema.Componente));
    public int RigaVin => RigaCella(Cella(CelleDettagli, _rangoDettagli, CampiSistema.Matricola));
    public int ColonnaVin => ColonnaCella(Cella(CelleDettagli, _rangoDettagli, CampiSistema.Matricola));
    public int RigaBaselineSw => RigaCella(Cella(CelleDettagli, _rangoDettagli, CampiSistema.BaselineSw));
    public int ColonnaBaselineSw => ColonnaCella(Cella(CelleDettagli, _rangoDettagli, CampiSistema.BaselineSw));
    public int RigaBaselineHw => RigaCella(Cella(CelleDettagli, _rangoDettagli, CampiSistema.BaselineHw));
    public int ColonnaBaselineHw => ColonnaCella(Cella(CelleDettagli, _rangoDettagli, CampiSistema.BaselineHw));

    // Card Pianificazione e campi classici di Dettagli: griglie a tre colonne con celle decise dal VM. Sotto la larghezza utile
    // (EditorCompatto) la card va a una colonna: ogni cella diventa una riga (Riga * 3 + Colonna, stessa lettura a tre colonne)
    // e il campo occupa tutta la larghezza (ColonneSpanEditor). Sopra la soglia le posizioni restano quelle di sempre.
    public int RigaStatoLavoro => RigaCella(Cella(CellePianificazione, _rangoPianificazione, CampiSistema.StatoLavoro));
    public int ColonnaStatoLavoro => ColonnaCella(Cella(CellePianificazione, _rangoPianificazione, CampiSistema.StatoLavoro));
    public int RigaScadenza => RigaCella(Cella(CellePianificazione, _rangoPianificazione, CampiSistema.Scadenza));
    public int ColonnaScadenza => ColonnaCella(Cella(CellePianificazione, _rangoPianificazione, CampiSistema.Scadenza));
    public int RigaAssegnatario => RigaCella(Cella(CellePianificazione, _rangoPianificazione, CampiSistema.Assegnatario));
    public int ColonnaAssegnatario => ColonnaCella(Cella(CellePianificazione, _rangoPianificazione, CampiSistema.Assegnatario));
    public int RigaAssegnatoDa => RigaCella(Cella(CellePianificazione, _rangoPianificazione, CampiSistema.AssegnatoDa));
    public int ColonnaAssegnatoDa => ColonnaCella(Cella(CellePianificazione, _rangoPianificazione, CampiSistema.AssegnatoDa));
    // Celle fisse fuori dalla griglia configurabile: Severita' (0,2), Data assegnazione (1,2), Priorita' (2,0) nella Pianificazione;
    // Tipo test (0,2) nei campi classici di Dettagli.
    public int RigaSeverita => RigaCella((0, 2));
    public int ColonnaSeverita => ColonnaCella((0, 2));
    public int RigaDataAssegnazione => RigaCella((1, 2));
    public int ColonnaDataAssegnazione => ColonnaCella((1, 2));
    public int RigaPriorita => RigaCella((2, 0));
    public int ColonnaPriorita => ColonnaCella((2, 0));
    public int RigaTipoTest => RigaCella((0, 2));
    public int ColonnaTipoTest => ColonnaCella((0, 2));

    /// <summary>Soglia di larghezza utile (DIP, pagina senza margini) sotto la quale le card multi-colonna dell'editor vanno a una
    /// colonna e i link di sezione diventano il menu "Vai a". Il test headless la verifica a 1280x672 (modo Compatto).</summary>
    public const double SogliaUnaColonnaEditor = 1100;

    /// <summary>True se la larghezza utile dell'editor e' sotto <see cref="SogliaUnaColonnaEditor"/>. Lo aggiorna la View.</summary>
    public bool EditorCompatto { get; private set; }

    /// <summary>ColumnSpan dei figli delle card a tre colonne: 3 (tutta la larghezza) in modo compatto, 1 altrimenti.</summary>
    public int ColonneSpanEditor => EditorCompatto ? 3 : 1;

    /// <summary>I link di sezione restano nella barra solo se c'e' spazio; altrimenti il menu "Vai a" ne prende il posto.</summary>
    /// <summary>Sotto questa larghezza utile la barra mostra il menu "Vai a" invece dei link: con i pulsanti della barra i sei
    /// link occupano circa 1000 DIP e non stanno piu' in barra.</summary>
    public const double SogliaBarraSezioniEditor = 1400;

    /// <summary>True se la barra non ha spazio per i link di sezione (menu "Vai a" al loro posto).</summary>
    public bool BarraCompatta { get; private set; }

    public bool MostraLinkSezioni => MostraBarraSaltaSezione && !BarraCompatta;
    public bool MostraMenuVaiA => MostraBarraSaltaSezione && BarraCompatta;

    /// <summary>Uid della prima operazione della revisione visualizzata: su di lei il "Copia dalla precedente" di riga e' nascosto.</summary>
    public Guid? UidPrimaCondizione => CondizioniVisual.Count > 0 ? CondizioniVisual[0].Uid : null;

    /// <summary>Aggiorna il modo dell'editor con la larghezza utile della pagina (la View lo chiama a ogni cambio di dimensione).
    /// Notifica le posizioni solo quando il modo cambia.</summary>
    public void AggiornaLarghezzaUtileEditor(double larghezza)
    {
        var compatto = larghezza < SogliaUnaColonnaEditor;
        var barraCompatta = larghezza < SogliaBarraSezioniEditor;
        if (compatto == EditorCompatto && barraCompatta == BarraCompatta) return;
        EditorCompatto = compatto;
        BarraCompatta = barraCompatta;
        OnPropertyChanged(nameof(EditorCompatto));
        OnPropertyChanged(nameof(ColonneSpanEditor));
        OnPropertyChanged(nameof(BarraCompatta));
        OnPropertyChanged(nameof(MostraLinkSezioni));
        OnPropertyChanged(nameof(MostraMenuVaiA));
        NotificaPosizioniCard();
    }

    private int RigaCella((int Riga, int Colonna) cella) => EditorCompatto ? cella.Riga * 3 + cella.Colonna : cella.Riga;

    private int ColonnaCella((int Riga, int Colonna) cella) => EditorCompatto ? 0 : cella.Colonna;

    private void NotificaPosizioniCard()
    {
        foreach (var nome in new[]
        {
            nameof(RigaStatoLavoro), nameof(ColonnaStatoLavoro), nameof(RigaScadenza), nameof(ColonnaScadenza),
            nameof(RigaAssegnatario), nameof(ColonnaAssegnatario), nameof(RigaAssegnatoDa), nameof(ColonnaAssegnatoDa),
            nameof(RigaSeverita), nameof(ColonnaSeverita), nameof(RigaDataAssegnazione), nameof(ColonnaDataAssegnazione),
            nameof(RigaPriorita), nameof(ColonnaPriorita),
            nameof(RigaTipoTest), nameof(ColonnaTipoTest),
            nameof(RigaVeicolo), nameof(ColonnaVeicolo), nameof(RigaEcu), nameof(ColonnaEcu), nameof(RigaVin), nameof(ColonnaVin),
            nameof(RigaBaselineSw), nameof(ColonnaBaselineSw), nameof(RigaBaselineHw), nameof(ColonnaBaselineHw),
        })
        {
            OnPropertyChanged(nome);
        }
    }

    // ----- Riordino dei campi classici di Dettagli/Riferimenti come BLOCCHI interi (seguito spec riordino
    // campi 2026-09-23 §2, generalizzato al coordinamento 2026-09-24): a differenza di Veicolo/Componente/Matricola/
    // Baseline (celle di una stessa griglia compatta) questi campi sono aree a piena larghezza/card, quindi
    // qui "riordinare" sposta il blocco intero fra un numero fisso di RIGHE riservate — le righe NON riservate
    // a questi campi (la griglia Veicolo/Componente/Baseline, i campi degli strumenti costanti per il test, i campi
    // custom) restano a riga fissa, come TipoTest/Severità/Priorità nella griglia compatta. Nessuna anomalia
    // storica qui (a differenza di Componente/Veicolo): il seed 6.3.0 di Funzionalita/StrumentiUsati/
    // DescrizioneBreve/Descrizione e di RequisitiCollegati/TestCorrelati/TicketCollegati segue già un ordine
    // ascendente coerente (per Strumenti usati e Ticket collegati la card era semplicemente altrove sulla
    // pagina — nessuna posizione precedente in QUESTO gruppo da preservare), quindi si legge Ordine
    // direttamente, senza flag/migrazione.
    private static readonly string[] ChiaviBlocchiDettagli =
        { CampiSistema.Funzionalita, CampiSistema.StrumentiUsati, CampiSistema.DescrizioneBreve, CampiSistema.Descrizione };
    // Righe 1-3 fisse: griglia Veicolo/Componente/Baseline, campi strumenti costanti, gruppi; riga 7 fissa: campi custom.
    private static readonly int[] RigheBlocchiDettagli = { 0, 4, 5, 6 };
    private static readonly string[] ChiaviBlocchiRiferimenti =
        { CampiSistema.RequisitiCollegati, CampiSistema.TestCorrelati, CampiSistema.TicketCollegati };
    private static readonly int[] RigheBlocchiRiferimenti = { 0, 1, 2 };

    private IReadOnlyDictionary<string, int> _rangoBlocchiDettagli =
        CampiClassiciOrdineService.CalcolaRango(ChiaviBlocchiDettagli, _ => null, usaOrdineTemplate: false);
    private IReadOnlyDictionary<string, int> _rangoBlocchiRiferimenti =
        CampiClassiciOrdineService.CalcolaRango(ChiaviBlocchiRiferimenti, _ => null, usaOrdineTemplate: false);

    private static int RigaBlocco(int[] righe, IReadOnlyDictionary<string, int> rango, string chiave)
    {
        var i = rango.TryGetValue(chiave, out var r) ? r : 0;
        return righe[Math.Clamp(i, 0, righe.Length - 1)];
    }

    public int RigaFunzionalita => RigaBlocco(RigheBlocchiDettagli, _rangoBlocchiDettagli, CampiSistema.Funzionalita);
    public int RigaStrumentiUsati => RigaBlocco(RigheBlocchiDettagli, _rangoBlocchiDettagli, CampiSistema.StrumentiUsati);
    public int RigaDescrizioneBreve => RigaBlocco(RigheBlocchiDettagli, _rangoBlocchiDettagli, CampiSistema.DescrizioneBreve);
    public int RigaDescrizione => RigaBlocco(RigheBlocchiDettagli, _rangoBlocchiDettagli, CampiSistema.Descrizione);
    public int RigaRequisitiCollegati => RigaBlocco(RigheBlocchiRiferimenti, _rangoBlocchiRiferimenti, CampiSistema.RequisitiCollegati);
    public int RigaTestCorrelati => RigaBlocco(RigheBlocchiRiferimenti, _rangoBlocchiRiferimenti, CampiSistema.TestCorrelati);
    public int RigaTicketCollegati => RigaBlocco(RigheBlocchiRiferimenti, _rangoBlocchiRiferimenti, CampiSistema.TicketCollegati);

    public ObservableCollection<CampoStrumentoValore> CampiCustomDettagliVisual { get; } = new();
    public ObservableCollection<CampoStrumentoValore> CampiCustomPianificazioneVisual { get; } = new();

    /// <summary>Campi realmente nuovi del template — CampoLibero, o CampoSistema con una chiave del catalogo
    /// che non corrisponde a nessun campo di sistema "storico" (Ruolo=Nessuno) — mai avevano un rendering nel
    /// form prima d'ora (Addendum: grill-me 2026-08-18, follow-up — gap preesistente chiuso qui): stesso
    /// motore generico Tipo-driven già usato per i campi degli Strumenti, salvati in TestCase.CampiCustom
    /// invece che nei dizionari ValoriStrumenti/ValoriStrumentiTest.</summary>
    private void AggiornaCampiCustomTemplate()
    {
        CampiCustomDettagliVisual.Clear();
        CampiCustomPianificazioneVisual.Clear();
        if (TemplateCorrente == null) return;

        foreach (var campo in TemplateCorrente.Sezioni.SelectMany(s => s.Campi))
            if (VisibileENuovo(campo)) CampiCustomDettagliVisual.Add(CostruisciCampoCustomValore(campo));

        foreach (var campo in TemplateCorrente.CampiPianificazione)
            if (VisibileENuovo(campo)) CampiCustomPianificazioneVisual.Add(CostruisciCampoCustomValore(campo));
    }

    private bool VisibileENuovo(CampoTemplate campo)
    {
        if (!EsCampoCustomRenderizzabile(campo)) return false;
        if (campo.CondizioneEsito.Count == 0) return true;
        return campo.CondizioneEsito.Contains(RevisioneCorrenteEsito);
    }

    private static bool EsCampoCustomRenderizzabile(CampoTemplate campo) => campo.Tipo switch
    {
        TipoCampoTemplate.CampoLibero => true,
        TipoCampoTemplate.CampoSistema => !string.IsNullOrEmpty(campo.CampoSistemaChiave)
            && !CampiSistema.Chiavi.Contains(campo.CampoSistemaChiave)
            && !CampiSistema.ChiaviPianificazione.Contains(campo.CampoSistemaChiave)
            && !CampiSistema.ChiaviRiferimenti.Contains(campo.CampoSistemaChiave),
        _ => false,
    };

    private CampoStrumentoValore CostruisciCampoCustomValore(CampoTemplate campo)
    {
        if (campo.Tipo == TipoCampoTemplate.CampoLibero)
        {
            var nomeLibero = string.IsNullOrWhiteSpace(campo.Nome) ? campo.Uid.ToString() : campo.Nome;
            return new CampoStrumentoValore(TestCase.CampiCustom, campo.Uid.ToString(), nomeLibero, campo.TipoLibero, campo.OpzioniListaLibero, campo.UnitaMisuraLibero);
        }

        var chiave = campo.CampoSistemaChiave!;
        var definizione = _catalogoCampiSistema.FirstOrDefault(c => c.Chiave == chiave);
        var nome = !string.IsNullOrWhiteSpace(campo.Nome) ? campo.Nome : definizione?.Etichetta ?? chiave;
        return new CampoStrumentoValore(TestCase.CampiCustom, chiave, nome, definizione?.Tipo ?? TipoCampoStrumento.Testo, definizione?.OpzioniLista ?? new List<string>(), definizione?.UnitaMisura);
    }

    public bool MostraRequisitiCollegati => MostraCampo(CampiSistema.RequisitiCollegati);
    public bool MostraTestCorrelati => MostraCampo(CampiSistema.TestCorrelati);
    /// <summary>Nascosto quando il Test ha già una Non Conformità aperta (feedback utente 2026-08-20): il
    /// riferimento TICKET/Ticketing vive già sull'anomalia stessa (CollegamentiPolarion), un secondo elenco di
    /// ticket a livello Test sarebbe ridondante e confuso. Resta utile solo quando non c'è una NC aperta a cui
    /// agganciare il riferimento — es. una Change Request non legata a un'anomalia.</summary>
    public bool MostraTicketCollegati => MostraCampo(CampiSistema.TicketCollegati) && !HaAnomalieTestAperte;
    /// <summary>Rapida: mostra solo i campi obbligatori; "Mostra tutti i campi" li riapre tutti. Solo presentazione: validazione,
    /// dati e modalita' Completa non cambiano (un campo obbligatorio e' sempre visibile).</summary>
    [ObservableProperty] private bool mostraTuttiICampiRapido;

    private static readonly string[] ChiaviDettagliEssenziali =
    {
        CampiSistema.Componente, CampiSistema.Veicolo, CampiSistema.Funzionalita, CampiSistema.StrumentiUsati, CampiSistema.DescrizioneBreve,
        CampiSistema.Descrizione, CampiSistema.Matricola, CampiSistema.BaselineSw, CampiSistema.BaselineHw
    };

    private bool VistaRapidaConsente(string chiave) => !ModoEditorRapido || MostraTuttiICampiRapido || CampoObbligatorio(chiave);

    /// <summary>I campi strumento a livello Test/revisione non hanno mai un flag obbligatorio: in Rapida compaiono solo a richiesta.</summary>
    public bool MostraCampiStrumentoDettagli => !ModoEditorRapido || MostraTuttiICampiRapido;

    /// <summary>Quanti campi opzionali la Rapida sta tenendo nascosti (per l'etichetta dell'interruttore).</summary>
    public int CampiOpzionaliNascostiRapido => !ModoEditorRapido || MostraTuttiICampiRapido ? 0
        : ChiaviDettagliEssenziali.Count(k => MostraCampo(k) && !CampoObbligatorio(k)) +
          (CampiTestCostanti.Count + GruppiTestCostanti.Sum(g => g.Campi.Count) +
           CampiRevisioneCostanti.Count + GruppiRevisioneCostanti.Sum(g => g.Campi.Count));

    public bool HaCampiOpzionaliRapido => ModoEditorRapido && (MostraTuttiICampiRapido || CampiOpzionaliNascostiRapido > 0);

    partial void OnMostraTuttiICampiRapidoChanged(bool value)
    {
        NotificaCampiRapidiEssenziali();
        SalvaPreferenzaTuttiICampi(value);
    }

    private void NotificaCampiRapidiEssenziali()
    {
        foreach (var nome in new[]
        {
            nameof(MostraCampoEcu), nameof(MostraCampoVeicolo), nameof(MostraCampoFunzionalita), nameof(MostraCampoStrumentiUsati),
            nameof(MostraCampoDescrizioneBreve), nameof(MostraCampoDescrizione), nameof(MostraCampoVin), nameof(MostraCampoBaselineSw),
            nameof(MostraCampoBaselineHw), nameof(MostraCampiStrumentoDettagli), nameof(CampiOpzionaliNascostiRapido), nameof(HaCampiOpzionaliRapido)
        }) OnPropertyChanged(nome);
    }

    public bool MostraCampoEcu => MostraCampo(CampiSistema.Componente) && VistaRapidaConsente(CampiSistema.Componente);
    public bool MostraCampoVeicolo => MostraCampo(CampiSistema.Veicolo) && VistaRapidaConsente(CampiSistema.Veicolo);
    public bool MostraCampoFunzionalita => MostraCampo(CampiSistema.Funzionalita) && VistaRapidaConsente(CampiSistema.Funzionalita);
    public bool MostraCampoStrumentiUsati => MostraCampo(CampiSistema.StrumentiUsati) && VistaRapidaConsente(CampiSistema.StrumentiUsati);
    public bool MostraCampoDescrizioneBreve => MostraCampo(CampiSistema.DescrizioneBreve) && VistaRapidaConsente(CampiSistema.DescrizioneBreve);
    public bool MostraCampoDescrizione => MostraCampo(CampiSistema.Descrizione) && VistaRapidaConsente(CampiSistema.Descrizione);
    public bool MostraCampoVin => MostraCampo(CampiSistema.Matricola) && VistaRapidaConsente(CampiSistema.Matricola);
    public bool MostraCampoBaselineSw => MostraCampo(CampiSistema.BaselineSw) && VistaRapidaConsente(CampiSistema.BaselineSw);
    public bool MostraCampoBaselineHw => MostraCampo(CampiSistema.BaselineHw) && VistaRapidaConsente(CampiSistema.BaselineHw);

    public bool MostraCampoStatoLavoro => MostraCampo(CampiSistema.StatoLavoro);
    public bool MostraCampoScadenza => MostraCampo(CampiSistema.Scadenza);
    public bool MostraCampoAssegnatario => MostraCampo(CampiSistema.Assegnatario);
    public bool MostraAssegnatarioEditabile => MostraCampoAssegnatario && PuoAssegnare;
    public bool MostraAssegnatarioReadOnly => MostraCampoAssegnatario && !PuoAssegnare;

    /// <summary>Sostituisce i controlli obbligatorio fissi (Componente/Veicolo/Assegnatario/Descrizione sempre, matricola
    /// solo se non Pianificato) con una lettura generica del template (Addendum: collaudo live 2026-08-17,
    /// feedback QA — "alcuni campi dovrebbero essere obbligatori o meno anche in base allo Stato lavoro"): i 5
    /// campi seminati di default riproducono esattamente lo stesso comportamento di prima, ma ora è un dato
    /// configurabile da Impostazioni → Tipi di test, non più fisso nel codice. Un campo nascosto da
    /// MostraCampo non viene mai richiesto: non ha senso obbligare a compilare qualcosa che non si vede.</summary>
    /// <summary>Elenco strutturato (etichetta + chiave, per la navigazione) dei campi obbligatori mancanti
    /// all'ultimo tentativo di salvataggio (feedback utente 2026-08-20, punto 15): popolato da Salva(),
    /// consumato da CampiMancantiDialog per portare l'utente direttamente sul campo cliccato invece di
    /// lasciargli solo un messaggio di testo generico da interpretare.</summary>
    public List<CampoMancante> CampiObbligatoriMancanti { get; private set; } = new();

    /// <summary>Con strumento UnitLogger, almeno un componente e almeno un Veicolo selezionati la Funzionalita diventa obbligatoria
    /// (richiesta utente 2026-11): ma e' una obbligatorieta' "morbida", al Salva un pop-up permette di proseguire
    /// comunque senza (vedi SalvaCoreAsync). Nessun effetto se uno dei tre manca o se il Test e' solo guasto/Guasto Injection.</summary>
    public bool FunzionalitaObbligatoriaConUdtEcuVeicolo =>
        !SoloOperazioniDtc && !MostraDtcOperazione
        && TestCase.Componente.Count > 0 && TestCase.VeicoliEffettivi.Count > 0
        && NomiStrumentiSelezionati().Any(n => string.Equals(n, "UnitLogger", StringComparison.OrdinalIgnoreCase));

    /// <summary>Vero quando la Funzionalita e' obbligatoria per la regola UnitLogger+componente+Veicolo e non e' stata inserita.</summary>
    private bool FunzionalitaMancanteConUdtEcuVeicolo =>
        TestCase.Funzionalita.Count == 0 && FunzionalitaObbligatoriaConUdtEcuVeicolo;

    /// <summary>Sollevato quando, dopo "Torna indietro" dal pop-up della Funzionalita mancante, la view deve portare il
    /// focus sul campo con questa chiave (CampiSistema). La bozza resta intatta: nessuna mutazione avviene prima.</summary>
    public event Action<string>? RichiestaVaiAlCampo;

    /// <summary>Sollevato da Salva() quando il blocco è per campi obbligatori mancanti: la view apre
    /// CampiMancantiDialog invece di lasciare solo il banner di testo (feedback utente 2026-08-20, punto 15).</summary>
    public event Action? RichiestaMostraCampiMancanti;

    private IEnumerable<CampoMancante> ValidaCampiObbligatoriDaTemplate()
    {
        var loc = LocalizationService.Instance;
        var campi = (TemplateCorrente?.CampiPianificazione ?? new List<CampoTemplate>())
            .Concat(TemplateCorrente?.Sezioni.SelectMany(s => s.Campi) ?? Enumerable.Empty<CampoTemplate>())
            .Where(c => c.Tipo == TipoCampoTemplate.CampoSistema && !string.IsNullOrEmpty(c.CampoSistemaChiave) && c.Obbligatorio);

        foreach (var campo in campi)
        {
            var chiave = campo.CampoSistemaChiave!;
            if (campo.StatiObbligatorio.Count > 0 && !campo.StatiObbligatorio.Contains(TestCase.StatoLavoro)) continue;
            if (!MostraCampo(chiave)) continue;
            if (ValoreCampoSistemaVuoto(chiave))
            {
                var etichetta = string.IsNullOrWhiteSpace(campo.Nome) ? CampiSistema.LabelDefault(chiave) : campo.Nome;
                yield return new CampoMancante(string.Format(loc.Traduci("ValCampoObblFmt"), etichetta), chiave);
            }
        }

        // Anche i template senza questo campo (o che lo rendono facoltativo) devono richiederlo nel caso
        // specifico in cui il Test usa soltanto strumenti di supporto.
        var funzionalitaGiaValidataDaTemplate = campi.Any(c => c.CampoSistemaChiave == CampiSistema.Funzionalita
            && (c.StatiObbligatorio.Count == 0 || c.StatiObbligatorio.Contains(TestCase.StatoLavoro))
            && MostraCampo(CampiSistema.Funzionalita));
        if (SoloStrumentiSupporto && TestCase.Funzionalita.Count == 0 && !funzionalitaGiaValidataDaTemplate)
            yield return new CampoMancante(
                string.Format(loc.Traduci("ValCampoObblFmt"), EtichettaFunzionalita),
                CampiSistema.Funzionalita);
    }

    /// <summary>Vero se il campo di sistema con questa chiave è attualmente obbligatorio nel template scelto,
    /// tenendo conto dello Stato lavoro corrente (feedback utente 2026-08-20, punto 15: "* sempre evidenziati").
    /// Pubblico: la view lo lega direttamente per mostrare/nascondere l'asterisco accanto all'etichetta.</summary>
    public bool CampoObbligatorio(string chiave)
    {
        if (chiave == CampiSistema.Funzionalita && SoloStrumentiSupporto) return true;
        if (chiave == CampiSistema.Funzionalita && FunzionalitaObbligatoriaConUdtEcuVeicolo) return true;
        // 6.5.0 punto 7: template "Funzionalita non obbligatoria" -> mai obbligatoria.
        if (chiave == CampiSistema.Funzionalita && (MostraDtcOperazione || SoloOperazioniDtc)) return false;
        var campo = (TemplateCorrente?.CampiPianificazione ?? new List<CampoTemplate>())
            .Concat(TemplateCorrente?.Sezioni.SelectMany(s => s.Campi) ?? Enumerable.Empty<CampoTemplate>())
            .FirstOrDefault(c => c.Tipo == TipoCampoTemplate.CampoSistema && c.CampoSistemaChiave == chiave);
        if (campo is not { Obbligatorio: true }) return false;
        return campo.StatiObbligatorio.Count == 0 || campo.StatiObbligatorio.Contains(TestCase.StatoLavoro);
    }

    public bool EcuObbligatorio => CampoObbligatorio(CampiSistema.Componente);
    public bool VeicoloObbligatorio => CampoObbligatorio(CampiSistema.Veicolo);
    public bool FunzionalitaObbligatoria => CampoObbligatorio(CampiSistema.Funzionalita);
    public bool DescrizioneBreveObbligatoria => CampoObbligatorio(CampiSistema.DescrizioneBreve);
    public bool DescrizioneObbligatoria => CampoObbligatorio(CampiSistema.Descrizione);
    public bool DescrizioneProblemaObbligatoria => CampoObbligatorio(CampiSistema.DescrizioneProblema);
    public bool VinObbligatorio => HaRevisioneCorrente && CampoObbligatorio(CampiSistema.Matricola);
    public bool BaselineSwObbligatorio => HaRevisioneCorrente && CampoObbligatorio(CampiSistema.BaselineSw);
    public bool BaselineHwObbligatorio => HaRevisioneCorrente && CampoObbligatorio(CampiSistema.BaselineHw);
    public bool ScadenzaObbligatoria => CampoObbligatorio(CampiSistema.Scadenza);
    public bool AssegnatarioObbligatorio => CampoObbligatorio(CampiSistema.Assegnatario);
    public bool StatoLavoroObbligatorio => CampoObbligatorio(CampiSistema.StatoLavoro);

    private void NotificaObbligatorieta()
    {
        OnPropertyChanged(nameof(EcuObbligatorio)); OnPropertyChanged(nameof(VeicoloObbligatorio));
        OnPropertyChanged(nameof(FunzionalitaObbligatoria)); OnPropertyChanged(nameof(DescrizioneBreveObbligatoria));
        OnPropertyChanged(nameof(DescrizioneObbligatoria)); OnPropertyChanged(nameof(DescrizioneProblemaObbligatoria));
        OnPropertyChanged(nameof(VinObbligatorio)); OnPropertyChanged(nameof(BaselineSwObbligatorio));
        OnPropertyChanged(nameof(BaselineHwObbligatorio)); OnPropertyChanged(nameof(ScadenzaObbligatoria));
        OnPropertyChanged(nameof(AssegnatarioObbligatorio)); OnPropertyChanged(nameof(StatoLavoroObbligatorio));
    }

    private bool ValoreCampoSistemaVuoto(string chiave) => chiave switch
    {
        CampiSistema.Componente => TestCase.Componente.Count == 0,
        CampiSistema.Veicolo => TestCase.VeicoliEffettivi.Count == 0,
        CampiSistema.Funzionalita => TestCase.Funzionalita.Count == 0,
        CampiSistema.DescrizioneBreve => string.IsNullOrWhiteSpace(TestCase.DescrizioneBreve),
        CampiSistema.Descrizione => string.IsNullOrWhiteSpace(TestCase.Descrizione),
        CampiSistema.DescrizioneProblema => string.IsNullOrWhiteSpace(TestCase.DescrizioneProblema),
        // matricola/Baseline vivono sulla REV: senza REV (test pianificato, "Avvia prima esecuzione" non ancora premuto)
        // il campo non e' compilabile, quindi non puo' risultare "obbligatorio mancante" (non bloccare il Salva).
        CampiSistema.Matricola => RevisioneCorrente != null && string.IsNullOrWhiteSpace(RevisioneCorrente.Matricola),
        CampiSistema.BaselineSw => RevisioneCorrente != null && string.IsNullOrWhiteSpace(RevisioneCorrente.BaselineSw),
        CampiSistema.BaselineHw => RevisioneCorrente != null && string.IsNullOrWhiteSpace(RevisioneCorrente.BaselineHw),
        CampiSistema.StrumentiUsati => !StrumentiOptions.Any(o => o.Selezionato),
        CampiSistema.StatoLavoro => false, // sempre valorizzato, un ComboBox senza selezione vuota non esiste qui
        CampiSistema.Scadenza => TestCase.Scadenza == null,
        CampiSistema.Assegnatario => string.IsNullOrWhiteSpace(TestCase.Assegnatario),
        CampiSistema.AssegnatoDa => string.IsNullOrWhiteSpace(TestCase.CreatoDa),
        _ => false,
    };

    private void NotificaVisibilitaCampiTemplate()
    {
        OnPropertyChanged(nameof(MostraCampoEcu));
        OnPropertyChanged(nameof(MostraCampoVeicolo)); OnPropertyChanged(nameof(MostraCampoFunzionalita));
        OnPropertyChanged(nameof(MostraCampoStrumentiUsati)); OnPropertyChanged(nameof(MostraCampoDescrizioneBreve));
        OnPropertyChanged(nameof(MostraCampoDescrizione)); OnPropertyChanged(nameof(MostraDescrizioneProblema));
        OnPropertyChanged(nameof(MostraCampoVin)); OnPropertyChanged(nameof(MostraCampoBaselineSw));
        OnPropertyChanged(nameof(MostraCampoBaselineHw)); OnPropertyChanged(nameof(MostraCampoStatoLavoro));
        OnPropertyChanged(nameof(MostraCampoScadenza));
        OnPropertyChanged(nameof(MostraCampoAssegnatario)); OnPropertyChanged(nameof(MostraAssegnatarioEditabile));
        OnPropertyChanged(nameof(MostraAssegnatarioReadOnly));
        OnPropertyChanged(nameof(MostraRequisitiCollegati)); OnPropertyChanged(nameof(MostraTestCorrelati));
        OnPropertyChanged(nameof(MostraTicketCollegati));
        NotificaObbligatorieta();
        AggiornaCampiCustomTemplate();
        RicalcolaOrdineCampiClassici();
    }

    public Array Esiti => Enum.GetValues(typeof(Esito));
    public Array ValoriPunteggioConformita => Enum.GetValues(typeof(PunteggioConformita));

    public string EtichettaStatoLavoro { get; }
    public string EtichettaScadenza { get; }
    public string EtichettaAssegnatario { get; }
    public string EtichettaAssegnatarioReadOnly { get; }
    public string EtichettaFunzionalita { get; }
    public string EtichettaEcu { get; }
    public string EtichettaVeicolo { get; }
    public string EtichettaStrumentiUsati { get; }

    public bool PuoAssegnare => _state.EMaster;

    /// <summary>Addendum: audit UX 2026-08-26, correzione post-collaudo — "il campo nota di modifica
    /// pianificazione non ha senso esista in creazione": la nota è obbligatoria solo quando Area/Scadenza/
    /// Assegnatario cambiano rispetto a un salvataggio precedente (pianificazioneCambiata in Valida(), che
    /// richiede _snapshot — presente solo per un test già esistente). Su un test nuovo _snapshot è sempre
    /// null, quindi il campo non potrà mai diventare obbligatorio: mostrarlo comunque era pura confusione,
    /// non solo su una nota utile ma inevitabilmente inerte.</summary>
    public bool MostraNotaPianificazione => PuoAssegnare && (!IsNew || _snapshot != null);

    /// <summary>La nota è richiesta solo dove l'utente può davvero scriverla (MostraNotaPianificazione): una
    /// modifica di Assegnatario/Scadenza che non passa da un Master (auto-assegnazione di Salva a uno Standard)
    /// non deve mai diventare un errore senza via d'uscita. Su un test nuovo con _snapshot creato da
    /// AssicuraTestSalvato (allegato aggiunto prima del Salva) il campo diventa visibile e richiesto come su
    /// un test esistente.</summary>
    private bool NotaPianificazioneRichiesta => MostraNotaPianificazione;
    public bool PuoModificare => _state.PuoScrivere;

    /// <summary>I campi di uno strumento restano dati master, modificabili solo da un Master (Addendum §17.3)
    /// anche quando l'editor è aperto da dentro un'operazione e non da Impostazioni → Strumenti (Addendum §8,
    /// feedback QA).</summary>
    public bool PuoGestireStrumenti => _state.EMaster;

    /// <summary>componente e Veicoli nuovi, a differenza dei campi di uno strumento sopra, sono voci di catalogo
    /// semplici (nome, eventuale gamma) senza configurazione tecnica da proteggere: creare al volo quella
    /// mancante mentre si compila un test non richiede il ruolo Master (feedback utente 2026-09-18:
    /// "possibilità di aggiungere i campi di sistema tipo componente/veicoli... altrimenti diventa macchioso, la
    /// possibilità viene data sia ad utente master che standard") — stesso permesso di scrittura di qualunque
    /// altro campo del test, non quello più ristretto di PuoGestireStrumenti.</summary>
    public bool PuoAggiungereCampiSistemaRapidi => _state.PuoScrivere;
    [ObservableProperty] private string nuovoEcuNome = "";
    [ObservableProperty] private string nuovoVeicoloNome = "";
    /// <summary>Testo digitato nella riga "Altra funzionalita' non in elenco..." (aggiunta rapida al catalogo).
    /// Distinto da NuovaFunzionalita, che e' il valore scelto dal picker e viene svuotato da AggiungiFunzionalita.</summary>
    [ObservableProperty] private string nuovaFunzionalitaCatalogoNome = "";
    /// <summary>Avviso non bloccante dell'aggiunta rapida di una Funzionalita' (es. voce archiviata omonima).</summary>
    [ObservableProperty] private string? avvisoFunzionalitaRapida;
    public bool HaAvvisoFunzionalitaRapida => !string.IsNullOrEmpty(AvvisoFunzionalitaRapida);
    partial void OnAvvisoFunzionalitaRapidaChanged(string? value) => OnPropertyChanged(nameof(HaAvvisoFunzionalitaRapida));

    public StrumentoDiagnostico? TrovaStrumento(string nome) =>
        _tuttiGliStrumenti.FirstOrDefault(s => s.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase));

    /// <summary>Salva subito i campi dello strumento appena modificati dall'editor a tabella (Addendum §8):
    /// stesso schema "scrittura immediata sui master data" già usato da AggiungiStrumentoRapido, non serve un
    /// passo di salvataggio separato che l'utente dovrebbe ricordarsi di fare.</summary>
    public void SalvaCampiStrumento()
    {
        if (!_state.CartellaValida) return;
        _state.Master.Strumenti.Save(_tuttiGliStrumenti);
        CaricaStrumenti();
    }

    // Percorsi usati solo per abbinare i due import nella bozza corrente e ricostruire i breadcrumb Menu/Submenu.
    // 6.6.0 - import log UnitLogger sulla Condizione: annullabile fino a Salva/uscita, con report finale copiabile.
    public string ScadenzaTesto
    {
        get => TestCase.Scadenza?.ToString("dd/MM/yyyy") ?? "";
        set
        {
            if (string.IsNullOrWhiteSpace(value)) TestCase.Scadenza = null;
            else if (DateTime.TryParseExact(value.Trim(), "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture,
                         System.Globalization.DateTimeStyles.None, out var data)) TestCase.Scadenza = data;
            else return;
            OnPropertyChanged();
            ModificheNonSalvate = true;
        }
    }

    /// <summary>"Crea test collegato" è sempre disponibile per qualunque template (feedback utente 2026-08-20,
    /// punto 17): non più azioni preconfigurate dal Master su un singolo template — un menu interattivo
    /// runtime chiede ogni volta quale/i tipo/i di test creare (anche lo stesso di questo, anche più di uno
    /// insieme) e quali campi copiare, sostituendo del tutto la vecchia AzioneCollegataTemplate/CampiEreditati.</summary>
    public bool HaAzioniCollegate => true;

    /// <summary>Vincolo di collegamento a un test esistente dichiarato dal template scelto (Addendum redesign
    /// 2026-08: sostituisce il vecchio UdtTestCollegatoId fisso su UnitLogger). Facoltativo per default: nessun
    /// template lo impone finché un Master non lo attiva esplicitamente in Impostazioni → Tipi di test.</summary>
    public bool MostraVincoloCollegamento => TemplateCorrente?.VincoloCollegamento != null;
    public string EtichettaVincoloCollegamento => TemplateCorrente?.VincoloCollegamento?.Etichetta ?? "";

    /// <summary>Candidati per l'AutoCompleteBox di ricerca del "Test collegato" richiesto dal vincolo del
    /// template (Addendum: collaudo live 2026-08-18 — prima un TextBox a digitazione libera, senza nessuna
    /// validazione contro test realmente esistenti, stesso bug già corretto per Requisiti/Test correlati ma
    /// dimenticato qui). Se il vincolo limita i template ammessi, la ricerca li rispetta.</summary>
    // Candidati del vincolo memorizzati: si ricalcolano solo quando cambiano il template del Test, il catalogo dei
    // template (riassegnato a ogni ricaricamento), l'elenco dei Test collegabili o gli uid ammessi.
    private List<TestCase>? _vincoloCache;
    private object? _vincoloTemplates;
    private object? _vincoloCorrelati;
    private Guid _vincoloTemplateUid;
    private List<Guid> _vincoloAmmessi = new();

    public List<TestCase> TestVincoloDisponibili
    {
        get
        {
            var ammessi = TemplateCorrente?.VincoloCollegamento?.TemplateAmmessiUid ?? new List<Guid>();
            if (ammessi.Count == 0) return TestCorrelatiDisponibili;
            if (_vincoloCache == null || !ReferenceEquals(_vincoloTemplates, _tuttiITemplate)
                || !ReferenceEquals(_vincoloCorrelati, TestCorrelatiDisponibili)
                || _vincoloTemplateUid != TestCase.TestTemplateUid || !_vincoloAmmessi.SequenceEqual(ammessi))
            {
                _vincoloCache = TestCorrelatiDisponibili.Where(t => ammessi.Contains(t.TestTemplateUid)).ToList();
                _vincoloTemplates = _tuttiITemplate;
                _vincoloCorrelati = TestCorrelatiDisponibili;
                _vincoloTemplateUid = TestCase.TestTemplateUid;
                _vincoloAmmessi = ammessi.ToList();
            }
            return _vincoloCache;
        }
    }

    [ObservableProperty] private TestCase? testVincoloSelezionato;

    partial void OnTestVincoloSelezionatoChanged(TestCase? value) => TestCase.TestCollegatoRichiestoId = value?.Id;

    /// <summary>Vero solo durante "Salva e crea": il Salva normale non deve riportare alla lista, perche' la
    /// creazione del test collegato prosegue subito dopo sullo stesso editor.</summary>
    private bool _sopprimiRitornoAllaLista;

    /// <summary>Decisione utente: con modifiche non salvate "Crea test collegato" chiede "Salva e crea" e, se
    /// confermato, esegue il NORMALE salvataggio sicuro (validazioni, changelog, esito, notifiche, segnalazioni,
    /// Unit of Work). Restituisce true solo se si puo' proseguire con la creazione (editor pulito o appena
    /// salvato); con Annulla o errori di validazione non si crea nulla e le modifiche restano pendenti.</summary>
    public Task<bool> AssicuraTestSalvatoPerTestCollegato() => AssicuraTestSalvato("SalvaPrimaDiCollegatoBody", "SalvaECreaBtn");

    /// <summary>Stesso pattern "Salva e crea" per il dialogo di dettaglio anomalia: con modifiche non salvate si
    /// offre "Salva e apri" / Annulla (il dialogo lavora sull'archivio, non sullo stato in memoria dell'editor).</summary>
    public Task<bool> AssicuraTestSalvatoPerDettaglioAnomalia() => AssicuraTestSalvato("SalvaPrimaDiDettaglioBody", "SalvaEApriBtn");

    private async Task<bool> AssicuraTestSalvato(string chiaveCorpo, string chiaveBottone)
    {
        if (!ModificheNonSalvate) return true;
        var loc = LocalizationService.Instance;
        if (!await ConfirmService.Chiedi(loc.Traduci("ModificheNonSalvateTitolo"), loc.Traduci(chiaveCorpo),
                loc.Traduci(chiaveBottone), loc.Traduci("Annulla"))) return false;

        _sopprimiRitornoAllaLista = true;
        try { await SalvaSicuroCommand.ExecuteAsync(null); }
        finally { _sopprimiRitornoAllaLista = false; }
        return !ModificheNonSalvate;
    }

    /// <summary>Vero se la NC e stata creata in questo editor e non e ancora stata scritta su disco.</summary>
    public bool EAnomaliaNonPersistita(NcRiga riga) => _anomalieNuove.Any(n => n.Uid == riga.Nc.Uid);

    /// <summary>Dialogo unico di dettaglio anomalia (stesso della pagina Anomalie). Una NC non ancora persistita si
    /// apre solo in modalita campi (nessuna sezione REV, nessun salvataggio); una persistita lavora su una copia
    /// riletta dall'archivio.</summary>
    public AnomaliaDettaglioViewModel CreaDettaglioAnomalia(NcRiga riga)
    {
        var nuova = _anomalieNuove.FirstOrDefault(n => n.Uid == riga.Nc.Uid);
        return nuova != null ? new AnomaliaDettaglioViewModel(_state, nuova, persistita: false)
                             : new AnomaliaDettaglioViewModel(_state, riga.Nc, persistita: true);
    }

    /// <summary>Dopo il dialogo: una NC non persistita riceve i campi modificati (restano da salvare con il
    /// Test); se il dialogo ha scritto sull'archivio il Test viene ricaricato nell'editor, perche una chiusura
    /// con nuova REV (o una semplice modifica della NC) cambia i dati mostrati.</summary>
    public void DopoDettaglioAnomalia(AnomaliaDettaglioViewModel dettaglio)
    {
        if (!dettaglio.Persistita)
        {
            if (dettaglio.NcModificata == null) return;
            var live = _anomalieNuove.FirstOrDefault(n => n.Uid == dettaglio.Nc.Uid);
            if (live == null) return;
            dettaglio.ApplicaA(live);
            ModificheNonSalvate = true;
            CaricaPannelloAnomalie();
            return;
        }
        if (!dettaglio.Modificato || string.IsNullOrEmpty(TestCase.Id)) return;
        _state.RicaricaTutto();
        if (InModifica && _navigaATestInModifica != null)
        {
            FermaHeartbeat(); // prima del Release: vedi _sessioneModificaAttiva
            if (!string.IsNullOrWhiteSpace(_percorsoLockSessioneAcquisita) &&
                !string.IsNullOrWhiteSpace(_sessioneLockAcquisita))
                _ = AvviaRilascioSessioneEsatta(_percorsoLockSessioneAcquisita, _sessioneLockAcquisita);
            _navigaATestInModifica(TestCase.Id);
        }
        else _navigaATest(TestCase.Id);
    }

    /// <summary>Crea uno o più test collegati verso i template scelti nel menu interattivo (feedback utente
    /// 2026-08-20, punto 17): sostituisce la vecchia EseguiAzioneCollegata legata a un'AzioneCollegataTemplate
    /// preconfigurata — ora templatiDestinazione/campiDaCopiare sono una scelta libera fatta ogni volta a
    /// runtime (vedi CreaTestCollegatoWindow), può includere lo stesso template del test di partenza e più di
    /// un template insieme. Con un solo test creato si naviga direttamente lì (stessa UX di prima); con più di
    /// uno si resta sul test di partenza, che li elenca comunque tutti come collegati cliccabili.</summary>
    public void CreaTestCollegati(IReadOnlyList<TestTemplate> templatiDestinazione, IReadOnlyCollection<string> campiDaCopiare)
    {
        if (templatiDestinazione.Count == 0) return;
        var eraSporco = ModificheNonSalvate;

        // Il test corrente potrebbe non essere ancora mai stato salvato: gli si assegna un ID e lo si salva
        // ora, altrimenti non ci sarebbe nulla a cui collegare i test creati.
        if (!_salvatoSuDisco && string.IsNullOrEmpty(TestCase.Id))
        {
            var inizialiCorrente = IdGenerator.IniziliDa(_state.Config.OperatoreAttivo);
            TestCase.Id = IdGenerator.Genera(_state.TestCases.GetAllIds(true), inizialiCorrente);
            OnPropertyChanged(nameof(TitoloPagina));
            OnPropertyChanged(nameof(TestRichiedeEcu));
            OnPropertyChanged(nameof(PreparaPolarionTooltip));
        }
        TestCase.StrumentiUsati = StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Valore).ToList();
        TestCase.Esecuzioni = Esecuzioni.ToList();

        string? ultimoCreatoId = null;
        foreach (var destinazione in templatiDestinazione)
        {
            var iniziali = IdGenerator.IniziliDa(_state.Config.OperatoreAttivo);
            var nuovoId = IdGenerator.Genera(_state.TestCases.GetAllIds(true), iniziali);
            var nuovoTest = new TestCase
            {
                Id = nuovoId,
                DataCreazione = DateTime.Now,
                UltimaModifica = DateTime.Now,
                ModificatoDa = _state.Config.OperatoreAttivo,
                CreatoDa = _state.Config.OperatoreAttivo,
                TestTemplateUid = destinazione.Uid,
                RequisitoId = new List<string>(TestCase.RequisitoId),
                Funzionalita = new List<string>(TestCase.Funzionalita),
                Descrizione = string.Format(LocalizationService.Instance.Traduci("EscalationDescrizioneFmt"), TestCase.Id),
                Assegnatario = _state.Config.OperatoreAttivo,
            };
            var nuovaEsecuzione = new Esecuzione
            {
                Iterazione = 1, Data = DateTime.Now, Operatore = _state.Config.OperatoreAttivo, Esito = Esito.DaEseguire,
                Condizioni = new List<Condizione> { new() { Esito = Esito.DaEseguire } }
            };
            // Copia solo i campi scelti dall'utente nel menu interattivo (feedback utente 2026-08-20, punto
            // 17), non più fissi per template. Matricola/BaselineSw/BaselineHw vivono sulla Esecuzione, non su
            // TestCase, quindi vanno sulla nuova Esecuzione appena creata invece che su nuovoTest.
            foreach (var chiave in campiDaCopiare)
            {
                switch (chiave)
                {
                    case CampiSistema.Componente: nuovoTest.Componente = new EcuSelection(TestCase.Componente); break;
                    case CampiSistema.Veicolo: nuovoTest.Veicoli = new List<string>(TestCase.VeicoliEffettivi); nuovoTest.NormalizzaVeicoli(); break;
                    case CampiSistema.Funzionalita: nuovoTest.Funzionalita = new List<string>(TestCase.Funzionalita); break;
                    case CampiSistema.StrumentiUsati: nuovoTest.StrumentiUsati = new List<string>(TestCase.StrumentiUsati); break;
                    case CampiSistema.DescrizioneBreve: nuovoTest.DescrizioneBreve = TestCase.DescrizioneBreve; break;
                    case CampiSistema.Descrizione: nuovoTest.Descrizione = TestCase.Descrizione; break;
                    case CampiSistema.DescrizioneProblema: nuovoTest.DescrizioneProblema = TestCase.DescrizioneProblema; break;
                    case CampiSistema.Matricola: nuovaEsecuzione.Matricola = RevisioneCorrente?.Matricola ?? ""; break;
                    case CampiSistema.BaselineSw: nuovaEsecuzione.BaselineSw = RevisioneCorrente?.BaselineSw ?? ""; break;
                    case CampiSistema.BaselineHw: nuovaEsecuzione.BaselineHw = RevisioneCorrente?.BaselineHw ?? ""; break;
                }
            }
            nuovoTest.Esecuzioni.Add(nuovaEsecuzione);
            _state.TestCases.Save(nuovoTest, isNew: true, null);

            TestCase.TestCollegatiCreati.Add(new TestCollegatoCreato { TemplateDestinazioneNome = destinazione.Nome, TestCollegatoId = nuovoTest.Id });
            ultimoCreatoId = nuovoTest.Id;
        }

        TestCase.UltimaModifica = DateTime.Now;
        TestCase.ModificatoDa = _state.Config.OperatoreAttivo;
        _state.TestCases.Save(TestCase, !_salvatoSuDisco, _salvatoSuDisco ? _snapshot?.RevisioneDati : null);
        _salvatoSuDisco = true;
        // Il repository puo' aver esteso la storia (TestRevisionSafety: modifica tecnica in-place -> nuova REV):
        // la collezione dell'editor deve seguirla, altrimenti il Salva successivo risulterebbe "rimuovere revisioni".
        if (TestCase.Esecuzioni.Count != Esecuzioni.Count) SostituisciEsecuzioni(TestCase.Esecuzioni);
        // Il Test e' ora su disco con una nuova RevisioneDati: va riallineata la base di concorrenza, altrimenti
        // la creazione successiva o il Salva andrebbero in conflitto con questa stessa scrittura. Lo snapshot
        // resta pero' la base del diff di Salva (Changelog, DettagliCambiati) se l'editor aveva modifiche
        // pendenti: azzerarlo farebbe perdere le tracce di quelle modifiche, e il flag sporco resta (nascondeva
        // anche eventuali NC/segnalazioni ancora da salvare). Solo se l'editor era pulito si riallinea tutto,
        // cosi' la navigazione al test collegato non chiede una conferma spuria.
        if (!eraSporco || _snapshot == null) { _snapshot = Clona(TestCase); ModificheNonSalvate = false; }
        else _snapshot.RevisioneDati = TestCase.RevisioneDati;

        if (templatiDestinazione.Count == 1 && ultimoCreatoId != null) _navigaATest(ultimoCreatoId);
        else OnPropertyChanged(nameof(TestCase));
    }

    [RelayCommand]
    private void ApriTestCollegato(string? id)
    {
        if (!string.IsNullOrEmpty(id)) _navigaATest(id);
    }

    public ObservableCollection<Esecuzione> Esecuzioni { get; } = new();
    public ObservableCollection<Allegato> AllegatiVisual { get; } = new();

    /// <summary>Condizioni della revisione corrente (Addendum §19), specchiate qui per notificare la UI su
    /// aggiunta/rimozione — Esecuzione.Condizioni è una semplice List&lt;T&gt; che non lo farebbe da sola.</summary>
    public ObservableCollection<Condizione> CondizioniVisual { get; } = new();
    public bool HaPiuDiUnaCondizione => CondizioniVisual.Count > 1;

    // Read-only counters for the compact Test workspace, based exclusively on the
    // currently displayed revision. They do not change outcomes or persist data.
    public int OperazioniRapideTotali => CondizioniVisual.Count;
    public int OperazioniRapideEseguite => CondizioniVisual.Count(c => c.Esito.EEseguito());
    public int OperazioniRapideDaEseguire => OperazioniRapideTotali - OperazioniRapideEseguite;
    public ObservableCollection<CampoCopiaPrecedente> AnteprimaCopiaPrecedente { get; } = new();
    [ObservableProperty] private bool mostraAnteprimaCopiaPrecedente;
    [ObservableProperty] private string? messaggioCopiaPrecedente;
    private Condizione? _origineAnteprimaCopiaPrecedente;
    private Condizione? _snapshotOrigineAnteprimaCopiaPrecedente;
    private string? _fingerprintOrigineAnteprimaCopiaPrecedente;
    private Condizione? _copiaPrecedenteUndo;
    private string? _fingerprintCopiaPrecedenteUndo;
    private string? _fingerprintTestPrimaCopiaPrecedente;
    private bool _modificheNonSalvatePrimaCopiaPrecedente;
    public bool PuoAnnullareCopiaPrecedente => (_copiaPrecedenteUndo != null &&
        _fingerprintCopiaPrecedenteUndo != null && ModificheNonSalvate && PuoModificareContenuto) ||
        (_annulloCampiCopia != null && PuoModificareContenuto);

    // Copia di campi dentro un'operazione (6.6.1): destinazione dell'anteprima aperta (la sua origine e' quella gia' in
    // _origineAnteprimaCopiaPrecedente) e annullamento dell'ultima copia di campi, con la stessa regola del Salva.
    private Condizione? _destinazioneCampiCopia;
    private string? _fingerprintDestinazioneCampiCopia;
    private AnnulloImportUdt? _annulloCampiCopia;
    private Condizione? _destinazioneAnnulloCampiCopia;
    private bool _modificheNonSalvatePrimaCampiCopia;

    /// <summary>Etichetta del pulsante di conferma nell'anteprima: la stessa anteprima serve la copia verso nuova operazione e la
    /// copia di campi dentro un'operazione.</summary>
    public string TestoConfermaCopiaPrecedente => _destinazioneCampiCopia != null ? LocalizationService.Instance.Traduci("TcConfermaCopiaBtn") : LocalizationService.Instance.Traduci("TcConfermaEAggiungiBtn");

    /// <summary>Ricostruisce Esecuzioni sincronizzando UNA sola volta, a collezione completa. RevisioneCorrente
    /// è l'ultimo elemento della collezione: con l'handler attivo, ogni Add intermedio faceva sincronizzare
    /// (Esecuzione.SincronizzaEsito) la revisione che in quel momento era l'ultima — cioè una REV già storica —
    /// riallineandone l'Esito salvato all'EsitoCalcolato. Se i due differivano (revisione scritta con le regole
    /// di esito precedenti alla 6.4.0, Test legacy migrato in lettura) il Salva veniva poi bloccato da
    /// TestRevisionSafety ("REV storica non modificabile"). Stesso principio del popolamento nel costruttore.</summary>
    private void SostituisciEsecuzioni(IEnumerable<Esecuzione> nuove)
    {
        _sostituzioneEsecuzioniInCorso = true;
        try
        {
            Esecuzioni.Clear();
            foreach (var esecuzione in nuove) Esecuzioni.Add(esecuzione);
        }
        finally
        {
            _sostituzioneEsecuzioniInCorso = false;
        }
        SincronizzaDaCambioEsecuzioni();
    }

    /// <summary>Corpo dell'handler CollectionChanged di Esecuzioni, estratto per poterlo invocare una sola
    /// volta a fine popolamento iniziale invece che una volta per ogni Esecuzioni.Add() (vedi costruttore).</summary>
    private void SincronizzaDaCambioEsecuzioni()
    {
        OnPropertyChanged(nameof(RevisioneCorrente));
        OnPropertyChanged(nameof(HaRevisioneCorrente));
        OnPropertyChanged(nameof(HaNonConformitaCollegata));
        OnPropertyChanged(nameof(StoricoRevisioni));
        OnPropertyChanged(nameof(HaStorico));
        OnPropertyChanged(nameof(NumeroRevisione));
        OnPropertyChanged(nameof(RevisioneCorrenteOperatore));
        OnPropertyChanged(nameof(RevisioneCorrenteEsito));
        NotificaVisibilitaCampiTemplate();
        OnPropertyChanged(nameof(RevisioniPerMenu));
        RevisioneVisualizzata = RevisioneCorrente;
        NotificaCampiRevisione();
        SincronizzaCondizioniVisual();
    }

    private void SincronizzaCondizioniVisual()
    {
        CondizioniVisual.Clear();
        foreach (var c in (RevisioneVisualizzata ?? RevisioneCorrente)?.Condizioni ?? new()) { CondizioniVisual.Add(c); CollegaCopiaDurata(c); }
        OnPropertyChanged(nameof(HaPiuDiUnaCondizione));
        OnPropertyChanged(nameof(UidPrimaCondizione));
        CopiaCampiDallaPrecedenteOperazioneCommand.NotifyCanExecuteChanged(); // la prima operazione non ha precedenti
        RicostruisciDtcInseriti();
        SincronizzaEsitoCondizioni();
    }

    [ObservableProperty] private string? suggerimentoCronologiaTesto;
    [ObservableProperty] private string? suggerimentoCronologiaTestId;
    public bool HaSuggerimentoCronologia => !string.IsNullOrEmpty(SuggerimentoCronologiaTesto);
    partial void OnSuggerimentoCronologiaTestoChanged(string? value) => OnPropertyChanged(nameof(HaSuggerimentoCronologia));

    /// <summary>
    /// Copia ESPLICITA dall'ultima operazione libera della revisione corrente. A differenza
    /// di Ripeti, crea una nuova operazione INDIPENDENTE (nessun OperazioneOrigineUid).
    /// Non copia esito, evidenze, anomalie, note, log o guasto; i dati d'esecuzione restano
    /// sempre da compilare. Conserva solo configurazione e campi riutilizzabili.
    /// </summary>
    [RelayCommand]
    private void CopiaDallaPrecedente()
    {
        if (RevisioneCorrente == null || VisualizzaStorico || SoloOperazioniDtc || !PuoModificareContenuto)
            return;
        var origine = RevisioneCorrente.Condizioni.LastOrDefault(c => !c.RiguardaDtc);
        if (origine == null)
        {
            MessaggioCopiaPrecedente = LocalizationService.Instance.Traduci("TcNessunaOperazioneRiutilizzabileMsg");
            return;
        }

        PreparaAnteprimaCopiaPrecedente(origine);
    }

    private void PreparaAnteprimaCopiaPrecedente(Condizione origine)
    {
        _destinazioneCampiCopia = null;
        _fingerprintDestinazioneCampiCopia = null;
        OnPropertyChanged(nameof(TestoConfermaCopiaPrecedente));
        _origineAnteprimaCopiaPrecedente = origine;
        _snapshotOrigineAnteprimaCopiaPrecedente = EntityStateFingerprint.Clone(origine);
        _fingerprintOrigineAnteprimaCopiaPrecedente = EntityStateFingerprint.Compute(origine);
        AnteprimaCopiaPrecedente.Clear();
        var componente = origine.Componente ?? new EcuSelection();
        var valoriStrumento = origine.ValoriStrumenti ?? new Dictionary<string, string>();
        AnteprimaCopiaPrecedente.Add(new("Componente", componente.Count == 0 ? LocalizationService.Instance.Traduci("TcNessunaTesto") : string.Join(", ", componente.AsEnumerable())));
        AnteprimaCopiaPrecedente.Add(new(LocalizationService.Instance.Traduci("TcFunzionalitaEtichetta"), ValoreAnteprima(origine.Funzionalita)));
        AnteprimaCopiaPrecedente.Add(new(LocalizationService.Instance.Traduci("TcStrumentoEtichetta"), ValoreAnteprima(origine.Strumento)));
        // La durata (TIME) non e' un campo strumento: non si mostra nell'anteprima e non si copia pari pari (vedi ClonaCampiStrumenti).
        foreach (var campo in valoriStrumento.Where(x => !EvidenzaOperazione.EChiaveRiservata(x.Key)).OrderBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase))
            AnteprimaCopiaPrecedente.Add(new(campo.Key, ValoreAnteprima(campo.Value)));
        MostraAnteprimaCopiaPrecedente = true;
        MessaggioCopiaPrecedente = LocalizationService.Instance.Traduci("TcQuestiSoliCampiMsg");
    }

    private static string ValoreAnteprima(string? value) => string.IsNullOrWhiteSpace(value) ? "(vuoto)" : value;

    [RelayCommand]
    private void AnnullaAnteprimaCopiaPrecedente()
    {
        MostraAnteprimaCopiaPrecedente = false;
        AnteprimaCopiaPrecedente.Clear();
        _origineAnteprimaCopiaPrecedente = null;
        _snapshotOrigineAnteprimaCopiaPrecedente = null;
        _fingerprintOrigineAnteprimaCopiaPrecedente = null;
        _destinazioneCampiCopia = null;
        _fingerprintDestinazioneCampiCopia = null;
        OnPropertyChanged(nameof(TestoConfermaCopiaPrecedente));
        MessaggioCopiaPrecedente = null;
    }

    [RelayCommand]
    private Task ConfermaCopiaDallaPrecedente() =>
        ComandoAsincrono.EseguiAsync(ConfermaCopiaDallaPrecedenteCoreAsync, "Copia dal Test precedente", AggiungiAvvisoErrore);

    private async Task ConfermaCopiaDallaPrecedenteCoreAsync()
    {
        // Anteprima aperta da un'operazione esistente: copia di campi (vedi ConfermaCampiDallaPrecedente).
        if (_destinazioneCampiCopia != null)
        {
            await ConfermaCampiDallaPrecedente();
            return;
        }
        var revisione = RevisioneCorrente;
        var origine = _origineAnteprimaCopiaPrecedente;
        var snapshot = _snapshotOrigineAnteprimaCopiaPrecedente;
        if (revisione == null || VisualizzaStorico || SoloOperazioniDtc || !PuoModificareContenuto ||
            origine == null || snapshot == null || _fingerprintOrigineAnteprimaCopiaPrecedente == null ||
            !revisione.Condizioni.Any(x => ReferenceEquals(x, origine)))
        {
            MessaggioCopiaPrecedente = LocalizationService.Instance.Traduci("TcRevisioneOrigineCambiataMsg");
            return;
        }
        if (!string.Equals(EntityStateFingerprint.Compute(origine), _fingerprintOrigineAnteprimaCopiaPrecedente,
                StringComparison.Ordinal))
        {
            PreparaAnteprimaCopiaPrecedente(origine);
            MessaggioCopiaPrecedente = LocalizationService.Instance.Traduci("TcOperazioneOriginaleCambiataMsg");
            return;
        }

        var copia = new Condizione
        {
            Esito = Esito.DaEseguire,
            Componente = new EcuSelection(snapshot.Componente ?? new EcuSelection()),
            Funzionalita = snapshot.Funzionalita,
            Strumento = snapshot.Strumento,
            ValoriStrumenti = ClonaCampiStrumenti(snapshot.ValoriStrumenti ?? new Dictionary<string, string>())
        };
        var fingerprintPrimaCopia = EntityStateFingerprint.Compute(TestCase);
        var modificheNonSalvatePrimaCopia = ModificheNonSalvate;
        revisione.Condizioni.Add(copia);
        AvviaCopiaDurataOperazione(copia);   // la durata non si copia dall'origine: la regola di copia la propone (TIME)
        AggiornaCampiVisualCondizione(copia);
        SincronizzaCondizioniVisual();
        NotificaCoperturaCambiata();
        ModificheNonSalvate = true;
        _annulloCampiCopia = null; // un solo ultimo annullamento: la nuova copia sostituisce quello di una copia di campi
        _destinazioneAnnulloCampiCopia = null;
        _copiaPrecedenteUndo = copia;
        _fingerprintCopiaPrecedenteUndo = EntityStateFingerprint.Compute(copia);
        _fingerprintTestPrimaCopiaPrecedente = fingerprintPrimaCopia;
        _modificheNonSalvatePrimaCopiaPrecedente = modificheNonSalvatePrimaCopia;
        OnPropertyChanged(nameof(PuoAnnullareCopiaPrecedente));
        AnnullaAnteprimaCopiaPrecedente();
        MessaggioCopiaPrecedente = LocalizationService.Instance.Traduci("TcOperazioneAggiuntaMsg");
    }

    [RelayCommand]
    private void AnnullaCopiaDallaPrecedente()
    {
        if (_annulloCampiCopia != null)
        {
            AnnullaCampiDallaPrecedente();
            return;
        }
        var revisione = RevisioneCorrente;
        var copia = _copiaPrecedenteUndo;
        if (!PuoModificareContenuto || !ModificheNonSalvate || revisione == null || copia == null ||
            !revisione.Condizioni.Any(x => ReferenceEquals(x, copia)))
        {
            InvalidaUndoCopiaPrecedente(LocalizationService.Instance.Traduci("TcAnnullamentoNonDispCopiaMsg"));
            return;
        }
        if (_fingerprintCopiaPrecedenteUndo == null ||
            !string.Equals(EntityStateFingerprint.Compute(copia), _fingerprintCopiaPrecedenteUndo,
                StringComparison.Ordinal))
        {
            InvalidaUndoCopiaPrecedente(LocalizationService.Instance.Traduci("TcAnnullamentoOperazioneCambiataMsg"));
            return;
        }

        revisione.Condizioni.Remove(copia);
        SincronizzaCondizioniVisual();
        NotificaCoperturaCambiata();
        var fingerprintDopoAnnulla = EntityStateFingerprint.Compute(TestCase);
        ModificheNonSalvate = _fingerprintTestPrimaCopiaPrecedente != null &&
            string.Equals(fingerprintDopoAnnulla, _fingerprintTestPrimaCopiaPrecedente, StringComparison.Ordinal)
                ? _modificheNonSalvatePrimaCopiaPrecedente
                : true;
        InvalidaUndoCopiaPrecedente(LocalizationService.Instance.Traduci("TcUltimaOperazioneRimossaMsg"));
    }

    /// <summary>"Copia dalla precedente" sulla singola operazione (6.6.1): propaga nell'operazione indicata i suoi campi strumento
    /// (PerOperazione, degli strumenti selezionati nel Test) dall'operazione subito precedente nella stessa REV. Mostra la stessa
    /// anteprima della copia verso una nuova operazione, con una spunta per campo; niente cambia finche' non si conferma. Non copia
    /// esito, note, guasto, funzionalita', componente, allegati, ripetizioni o campi File, e un campo vuoto nella precedente non svuota
    /// quello della destinazione.</summary>
    [RelayCommand(CanExecute = nameof(PuoCopiareCampiDallaPrecedente))]
    private void CopiaCampiDallaPrecedenteOperazione(Condizione? destinazione)
    {
        if (destinazione == null || !PuoCopiareCampiDallaPrecedente(destinazione)) return;
        var condizioni = RevisioneCorrente!.Condizioni;
        var origine = condizioni[condizioni.FindIndex(c => ReferenceEquals(c, destinazione)) - 1];
        PreparaAnteprimaCampiDallaPrecedente(destinazione, origine);
    }

    /// <summary>Disponibile solo su un'operazione che ha una precedente nella REV corrente, in modifica e senza revisioni storiche.</summary>
    private bool PuoCopiareCampiDallaPrecedente(Condizione? destinazione)
    {
        if (destinazione == null || RevisioneCorrente == null || VisualizzaStorico || !PuoModificareContenuto) return false;
        return RevisioneCorrente.Condizioni.FindIndex(c => ReferenceEquals(c, destinazione)) > 0;
    }

    /// <summary>Campi strumento visibili su un'operazione: sono quelli su cui agisce la copia (diretti e dentro i gruppi).</summary>
    private static IEnumerable<CampoStrumentoValore> CampiVisualiOperazione(Condizione condizione) =>
        (condizione.CampiVisual ?? new List<CampoStrumentoValore>())
            .Concat((condizione.GruppiVisual ?? new List<GruppoCampiStrumento>()).SelectMany(g => g.Campi));

    private void PreparaAnteprimaCampiDallaPrecedente(Condizione destinazione, Condizione origine)
    {
        AnnullaAnteprimaCopiaPrecedente();
        // Solo i campi configurabili dell'operazione (non File, non personalizzati) per cui la precedente ha un valore non vuoto.
        var righe = CampiVisualiOperazione(destinazione)
            .Where(c => c.Tipo != TipoCampoStrumento.File && !string.IsNullOrWhiteSpace(c.Strumento))
            .Select(c => (Campo: c, Chiave: $"{c.Strumento}::{c.Nome}"))
            .Where(x => !string.IsNullOrWhiteSpace(origine.ValoriStrumenti.GetValueOrDefault(x.Chiave)))
            .ToList();
        if (righe.Count == 0)
        {
            MessaggioCopiaPrecedente = LocalizationService.Instance.Traduci("TcNessunCampoStrumentoMsg");
            return;
        }
        _origineAnteprimaCopiaPrecedente = origine;
        _fingerprintOrigineAnteprimaCopiaPrecedente = EntityStateFingerprint.Compute(origine);
        _destinazioneCampiCopia = destinazione;
        _fingerprintDestinazioneCampiCopia = EntityStateFingerprint.Compute(destinazione);
        foreach (var (campo, chiave) in righe)
            AnteprimaCopiaPrecedente.Add(new CampoCopiaPrecedente($"{campo.Strumento} · {campo.Nome}", origine.ValoriStrumenti[chiave],
                destinazione.ValoriStrumenti.GetValueOrDefault(chiave), chiave));
        MostraAnteprimaCopiaPrecedente = true;
        OnPropertyChanged(nameof(TestoConfermaCopiaPrecedente));
        MessaggioCopiaPrecedente = LocalizationService.Instance.Traduci("TcSpuntaCampiParte1")
            + LocalizationService.Instance.Traduci("TcSpuntaCampiParte2");
    }

    /// <summary>Conferma della copia di campi: applica solo le righe spuntate, con la conferma esplicita se sostituisce valori
    /// non vuoti diversi. Riusa il piano e l'annullamento dell'import UnitLogger (PianoImportUdt, AnnulloImportUdt).</summary>
    private async Task ConfermaCampiDallaPrecedente()
    {
        var destinazione = _destinazioneCampiCopia;
        var origine = _origineAnteprimaCopiaPrecedente;
        var revisione = RevisioneCorrente;
        if (destinazione == null || origine == null || revisione == null || VisualizzaStorico || !PuoModificareContenuto ||
            _fingerprintOrigineAnteprimaCopiaPrecedente == null || _fingerprintDestinazioneCampiCopia == null ||
            !revisione.Condizioni.Any(c => ReferenceEquals(c, destinazione)) || !revisione.Condizioni.Any(c => ReferenceEquals(c, origine)))
        {
            AnnullaAnteprimaCopiaPrecedente();
            MessaggioCopiaPrecedente = LocalizationService.Instance.Traduci("TcRevisioneOperazioneCambiataMsg");
            return;
        }
        if (!string.Equals(EntityStateFingerprint.Compute(origine), _fingerprintOrigineAnteprimaCopiaPrecedente, StringComparison.Ordinal) ||
            !string.Equals(EntityStateFingerprint.Compute(destinazione), _fingerprintDestinazioneCampiCopia, StringComparison.Ordinal))
        {
            PreparaAnteprimaCampiDallaPrecedente(destinazione, origine);
            MessaggioCopiaPrecedente = LocalizationService.Instance.Traduci("TcOperazioneCambiataMsg");
            return;
        }

        var selezionati = AnteprimaCopiaPrecedente.Where(r => r.Selezionato && r.Chiave != null).ToList();
        if (selezionati.Count == 0)
        {
            MessaggioCopiaPrecedente = LocalizationService.Instance.Traduci("TcNessunCampoSelezionatoMsg");
            return;
        }
        var sostituzioni = selezionati.Where(r => r.Sostituisce).ToList();
        if (sostituzioni.Count > 0)
        {
            var righe = sostituzioni.Take(PianoImportUdt.MaxRigheAnteprima).Select(r => $"- {r.Campo}: {r.Attuale} → {r.Valore}").ToList();
            if (sostituzioni.Count > PianoImportUdt.MaxRigheAnteprima) righe.Add($"(altri {sostituzioni.Count - PianoImportUdt.MaxRigheAnteprima})");
            var messaggio = LocalizationService.Instance.Traduci("TcCopiaSostituisceMsg") + "\n"
                + string.Join("\n", righe) + "\n\n" + LocalizationService.Instance.Traduci("TcCampiVuotiSoloCompilatiMsg");
            if (!await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("CopiaDallaPrecedenteBtn"), messaggio, LocalizationService.Instance.Traduci("TcSovrascriviBtn"), LocalizationService.Instance.Traduci("Annulla")))
            {
                MessaggioCopiaPrecedente = LocalizationService.Instance.Traduci("TcCopiaAnnullataMsg");
                return;
            }
        }

        var piano = PianoImportUdt.DaVoci(selezionati.Select(r => new VoceImportUdt(r.Campo,
            ScritturaCampoUdt.Su(r.Campo, destinazione.ValoriStrumenti, r.Chiave!, r.Valore))));
        var modificheNonSalvatePrima = ModificheNonSalvate;
        var annullo = piano.Esegui();
        AggiornaCampiVisualCondizione(destinazione);
        ModificheNonSalvate = true;
        _copiaPrecedenteUndo = null;
        _fingerprintCopiaPrecedenteUndo = null;
        _fingerprintTestPrimaCopiaPrecedente = null;
        _annulloCampiCopia = annullo;
        _destinazioneAnnulloCampiCopia = destinazione;
        _modificheNonSalvatePrimaCampiCopia = modificheNonSalvatePrima;
        AnnullaAnteprimaCopiaPrecedente();
        MessaggioCopiaPrecedente = string.Format(LocalizationService.Instance.Traduci("TcCopiatiCampiFmt"), selezionati.Count);
        OnPropertyChanged(nameof(PuoAnnullareCopiaPrecedente));
    }

    /// <summary>Annulla l'ultima copia di campi: riporta ogni chiave allo stato precedente, ma solo se nessun campo copiato è
    /// stato cambiato dopo la copia (stessa regola dell'annullamento dell'import UnitLogger). Nessun dato viene rimosso in caso contrario.</summary>
    private void AnnullaCampiDallaPrecedente()
    {
        var annullo = _annulloCampiCopia;
        var destinazione = _destinazioneAnnulloCampiCopia;
        if (annullo == null || destinazione == null || !PuoModificareContenuto) return;
        if (!annullo.InvariatoDopoImport)
        {
            _annulloCampiCopia = null;
            _destinazioneAnnulloCampiCopia = null;
            MessaggioCopiaPrecedente = LocalizationService.Instance.Traduci("TcAnnullamentoCampoCambiatoMsg");
            OnPropertyChanged(nameof(PuoAnnullareCopiaPrecedente));
            return;
        }
        annullo.Annulla();
        _annulloCampiCopia = null;
        _destinazioneAnnulloCampiCopia = null;
        ModificheNonSalvate = _modificheNonSalvatePrimaCampiCopia;
        AggiornaCampiVisualCondizione(destinazione);
        OnPropertyChanged(nameof(PuoAnnullareCopiaPrecedente));
        MessaggioCopiaPrecedente = LocalizationService.Instance.Traduci("TcUltimaCopiaCampiAnnullataMsg");
    }

    private void InvalidaUndoCopiaPrecedente(string messaggio)
    {
        _copiaPrecedenteUndo = null;
        _fingerprintCopiaPrecedenteUndo = null;
        _fingerprintTestPrimaCopiaPrecedente = null;
        OnPropertyChanged(nameof(PuoAnnullareCopiaPrecedente));
        MessaggioCopiaPrecedente = messaggio;
    }

    [ObservableProperty] private string testoOperazioniRapide = "";

    // Preview and identity snapshots are editor-local. Never modify the Test while previewing.
    private string? _testoAnteprimaRapida;
    private Esecuzione? _revisioneAnteprimaRapida;
    private IReadOnlyList<TestQuickPasteService.Operation> _operazioniAnteprimaRapida =
        Array.Empty<TestQuickPasteService.Operation>();
    public IReadOnlyList<RigaAnteprimaIncollaRapido> RigheAnteprimaRapida { get; private set; } =
        Array.Empty<RigaAnteprimaIncollaRapido>();
    public string RiepilogoAnteprimaRapida { get; private set; } = "";
    public string ErroreAnteprimaRapida { get; private set; } = "";
    public bool HaErroreAnteprimaRapida => !string.IsNullOrWhiteSpace(ErroreAnteprimaRapida);

    private void MostraErroreAnteprimaRapida(string message)
    {
        ErroreAnteprimaRapida = message;
        OnPropertyChanged(nameof(ErroreAnteprimaRapida));
        OnPropertyChanged(nameof(HaErroreAnteprimaRapida));
    }

    public bool MostraAnteprimaOperazioniRapide => _testoAnteprimaRapida is not null;
    public bool PuoConfermareAnteprimaRapida => MostraAnteprimaOperazioniRapide &&
        ModoEditorRapido && PuoModificareContenuto && !SoloOperazioniDtc &&
        ReferenceEquals(_revisioneAnteprimaRapida, RevisioneCorrente);

    partial void OnTestoOperazioniRapideChanged(string value)
    {
        if (_testoAnteprimaRapida is not null &&
            !string.Equals(_testoAnteprimaRapida, value, StringComparison.Ordinal))
            ResettaAnteprimaOperazioniRapide();
        else if (HaErroreAnteprimaRapida)
            MostraErroreAnteprimaRapida("");
    }

    private void NotificaAnteprimaOperazioniRapide()
    {
        OnPropertyChanged(nameof(RigheAnteprimaRapida));
        OnPropertyChanged(nameof(RiepilogoAnteprimaRapida));
        OnPropertyChanged(nameof(MostraAnteprimaOperazioniRapide));
        OnPropertyChanged(nameof(PuoConfermareAnteprimaRapida));
    }

    private void ResettaAnteprimaOperazioniRapide()
    {
        _testoAnteprimaRapida = null;
        _revisioneAnteprimaRapida = null;
        _operazioniAnteprimaRapida = Array.Empty<TestQuickPasteService.Operation>();
        RigheAnteprimaRapida = Array.Empty<RigaAnteprimaIncollaRapido>();
        RiepilogoAnteprimaRapida = "";
        MostraErroreAnteprimaRapida("");
        NotificaAnteprimaOperazioniRapide();
    }

    private static string NotaBreveAnteprima(string text)
    {
        var line = text.Replace("\r\n", " ↵ ", StringComparison.Ordinal)
            .Replace('\r', ' ').Replace('\n', '↵').Replace('\t', ' ');
        return line.Length <= 140 ? line : line[..140] + "…";
    }

    private static string MessaggioErroreIncollaRapido(TestQuickPasteService.Result parsed)
    {
        var key = parsed.Error switch
        {
            TestQuickPasteService.ParseError.TooManyRows => "EditorQuickMaxOperazioni",
            TestQuickPasteService.ParseError.RowTooLong => "EditorQuickRigaLunga",
            TestQuickPasteService.ParseError.UnknownFunction => "EditorQuickFunzioneNonValida",
            _ => "EditorQuickColonneNonValide"
        };
        var loc = LocalizationService.Instance;
        var message = string.Format(loc.Traduci(key), parsed.Detail);
        return parsed.RowNumber > 0
            ? string.Format(loc.Traduci("EditorQuickErrorAtRow"), parsed.RowNumber, message)
            : message;
    }

    [RelayCommand]
    private void PreparaAnteprimaOperazioniRapide()
    {
        if (!ModoEditorRapido || !PuoModificareContenuto || RevisioneCorrente is null || SoloOperazioniDtc)
            return;
        ResettaAnteprimaOperazioniRapide();
        var parsed = TestQuickPasteService.Parse(TestoOperazioniRapide, TestCase.Funzionalita,
            FunzionalitaAutoSeSingola);
        if (!parsed.Success)
        {
            var errorMessage = MessaggioErroreIncollaRapido(parsed);
            MostraErroreAnteprimaRapida(errorMessage);
            AggiungiAvvisoErrore(errorMessage);
            return;
        }
        if (parsed.Operations.Count == 0)
        {
            var message = LocalizationService.Instance.Traduci("EditorQuickPreviewEmpty");
            MostraErroreAnteprimaRapida(message);
            AggiungiAvvisoErrore(message);
            return;
        }
        _testoAnteprimaRapida = TestoOperazioniRapide;
        _revisioneAnteprimaRapida = RevisioneCorrente;
        _operazioniAnteprimaRapida = parsed.Operations.ToArray();
        RigheAnteprimaRapida = parsed.Operations.Take(8)
            .Select((operation, index) => new RigaAnteprimaIncollaRapido(
                index + 1, operation.Function, NotaBreveAnteprima(operation.Note)))
            .ToArray();
        RiepilogoAnteprimaRapida = string.Format(
            LocalizationService.Instance.Traduci("EditorQuickPreviewSummary"),
            parsed.Operations.Count, RigheAnteprimaRapida.Count);
        NotificaAnteprimaOperazioniRapide();
    }

    [RelayCommand]
    private void AnnullaAnteprimaOperazioniRapide() => ResettaAnteprimaOperazioniRapide();

    // Session-only undo. Never delete a batch that the operator has already edited
    // or associated with an anomaly, even if only one of its rows has changed.
    private Esecuzione? _revisioneUltimoIncollaRapido;
    private List<(Condizione Condizione, string Firma)> _ultimoIncollaRapido = new();
    public bool PuoAnnullareUltimoIncollaRapido => ModoEditorRapido && PuoModificareContenuto &&
        ReferenceEquals(_revisioneUltimoIncollaRapido, RevisioneCorrente) && _ultimoIncollaRapido.Count > 0;

    [RelayCommand]
    private void AnnullaUltimoIncollaRapido()
    {
        if (!PuoAnnullareUltimoIncollaRapido || RevisioneCorrente is null) return;
        if (_ultimoIncollaRapido.Any(item =>
            !RevisioneCorrente.Condizioni.Any(c => ReferenceEquals(c, item.Condizione)) ||
            !string.Equals(FirmaOperazione(item.Condizione), item.Firma, StringComparison.Ordinal) ||
            // Do not orphan a repeated attempt that points to this operation's root.
            RevisioneCorrente.Condizioni.Any(other => !ReferenceEquals(other, item.Condizione) &&
                !_ultimoIncollaRapido.Any(pasted => ReferenceEquals(pasted.Condizione, other)) &&
                (other.OperazioneOrigineUid ?? other.Uid) ==
                    (item.Condizione.OperazioneOrigineUid ?? item.Condizione.Uid)) ||
            ContaCollegamentiCondizione(item.Condizione) > 0))
        {
            AggiungiAvvisoErrore(LocalizationService.Instance.Traduci("EditorQuickUndoModified"));
            return;
        }
        foreach (var item in _ultimoIncollaRapido)
        {
            RevisioneCorrente.Condizioni.Remove(item.Condizione);
            item.Condizione.ScollegaDurataTesto();
        }
        _ultimoIncollaRapido.Clear();
        _revisioneUltimoIncollaRapido = null;
        SincronizzaCondizioniVisual();
        NotificaCoperturaCambiata();
        ModificheNonSalvate = true;
        OnPropertyChanged(nameof(PuoAnnullareUltimoIncollaRapido));
    }

    /// <summary>Batch insertion of unexecuted independent operations; never copies results or anomalies.</summary>
    [RelayCommand]
    private void IncollaOperazioniRapide()
    {
        if (!ModoEditorRapido || !PuoModificareContenuto || RevisioneCorrente is null || SoloOperazioniDtc)
            return;
        if (_testoAnteprimaRapida is null)
        {
            var message = LocalizationService.Instance.Traduci("EditorQuickPreviewRequired");
            MostraErroreAnteprimaRapida(message);
            AggiungiAvvisoErrore(message);
            return;
        }
        var parsed = TestQuickPasteService.Parse(TestoOperazioniRapide, TestCase.Funzionalita,
            FunzionalitaAutoSeSingola);
        if (!parsed.Success)
        {
            ResettaAnteprimaOperazioniRapide();
            var message = MessaggioErroreIncollaRapido(parsed);
            MostraErroreAnteprimaRapida(message);
            AggiungiAvvisoErrore(message);
            return;
        }
        if (_testoAnteprimaRapida is not null &&
            (!ReferenceEquals(_revisioneAnteprimaRapida, RevisioneCorrente) ||
             !string.Equals(_testoAnteprimaRapida, TestoOperazioniRapide, StringComparison.Ordinal) ||
             !_operazioniAnteprimaRapida.SequenceEqual(parsed.Operations)))
        {
            ResettaAnteprimaOperazioniRapide();
            var message = LocalizationService.Instance.Traduci("EditorQuickPreviewChanged");
            MostraErroreAnteprimaRapida(message);
            AggiungiAvvisoErrore(message);
            return;
        }
        if (parsed.Operations.Count == 0) return;
        var nuove = parsed.Operations.Select(row => new Condizione
        {
            Esito = Esito.DaEseguire,
            Componente = EcuAutoSuNuovaOperazione,
            Funzionalita = row.Function,
            Nota = row.Note
        }).ToList();
        foreach (var condizione in nuove)
        {
            RevisioneCorrente.Condizioni.Add(condizione);
            AvviaCopiaDurataOperazione(condizione);
            AggiornaCampiVisualCondizione(condizione);
        }
        SincronizzaCondizioniVisual();
        NotificaCoperturaCambiata();
        ModificheNonSalvate = true;
        _revisioneUltimoIncollaRapido = RevisioneCorrente;
        _ultimoIncollaRapido = nuove.Select(c => (Condizione: c, Firma: FirmaOperazione(c))).ToList();
        OnPropertyChanged(nameof(PuoAnnullareUltimoIncollaRapido));
        ResettaAnteprimaOperazioniRapide();
        TestoOperazioniRapide = "";
    }

    [RelayCommand]
    private void AggiungiCondizione()
    {
        if (RevisioneCorrente == null || SoloOperazioniDtc) return;   // con guasto Injection senza strumento Da testare niente righe libere: solo coppie da "Aggiungi guasto"
        // "Nuova operazione" e intenzionalmente indipendente: non copia l'operazione precedente e non puo
        // risolverla. Per riprovare la stessa operazione si usa il comando dedicato Ripeti, che registra la
        // radice della catena e permette al calcolo di considerare soltanto l'ultimo tentativo.
        var nuovaCondizione = new Condizione { Esito = Esito.DaEseguire, Componente = EcuAutoSuNuovaOperazione, Funzionalita = FunzionalitaAutoSeSingola };
        RevisioneCorrente.Condizioni.Add(nuovaCondizione);
        AvviaCopiaDurataOperazione(nuovaCondizione);
        AggiornaCampiVisualCondizione(nuovaCondizione);
        SincronizzaCondizioniVisual();
        NotificaCoperturaCambiata();
        ModificheNonSalvate = true;
    }

    /// <summary>TIME (6.6.1): +/-5 minuti sulla durata dell'operazione, mai negativa. Da vuoto, "+" porta a 5 e "-" resta vuoto.</summary>
    [RelayCommand] private void IncrementaDurataOperazione(Condizione? condizione) => RegolaDurataOperazione(condizione, DurataOperazione.PassoRapido);

    [RelayCommand] private void DecrementaDurataOperazione(Condizione? condizione) => RegolaDurataOperazione(condizione, -DurataOperazione.PassoRapido);

    private void RegolaDurataOperazione(Condizione? condizione, int passo)
    {
        if (condizione == null) return;
        condizione.SegnaDurataModificataDallOperatore();   // +/- e' un intervento dell'operatore: la regola di copia non lo sovrascrive
        condizione.DurataMinuti = DurataOperazione.Passo(condizione.DurataMinuti, passo);
        ModificheNonSalvate = true;
    }

    /// <summary>Richiesta della regola di copia per una operazione (TIME, 6.6.1). Il risultato si applica solo se e' ancora la richiesta
    /// piu' recente per quella operazione (vedi PrenotaCopiaDurata e ApplicaDurataCopiata).</summary>
    internal sealed record RichiestaCopiaDurata(int Generazione, string Chiave, IReadOnlyList<string> Componente, string? Funzionalita, int? PassoDtc, IReadOnlyList<string> Veicoli);

    private readonly Dictionary<Condizione, int> _generazioneCopiaPerOperazione = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Condizione> _condizioniCollegateCopia = new(ReferenceEqualityComparer.Instance);
    private readonly List<Task> _copieDurataInCorso = new();
    private int _generazioneCopiaDurata;
    private bool _copiaDurataAttiva;

    /// <summary>Consegna del risultato della copia sul thread UI. In produzione va al dispatcher Avalonia; nei test puo' essere
    /// sostituita per applicare il risultato subito (vedi AttendiCopieDurataInCorso).</summary>
    internal Action<Action> PubblicaRisultatoCopia { get; set; } = azione => Avalonia.Threading.Dispatcher.UIThread.Post(azione);

    /// <summary>Attende le copie della durata avviate finora. Il risultato arriva comunque tramite PubblicaRisultatoCopia.</summary>
    internal Task AttendiCopieDurataInCorso() => Task.WhenAll(_copieDurataInCorso.ToArray());

    /// <summary>TIME (6.6.1), chiave della regola di copia: componente (insieme ordinato) e Funzionalita per le operazioni normali, componente e passo
    /// per le operazioni guasto. Null = chiave incompleta, nessuna copia. Il veicolo non fa parte della chiave: e' un criterio di preferenza
    /// gia' gestito dalla query (vedi LocalTestIndexStore.TrovaDurataCopia).</summary>
    private static string? ChiaveCopia(Condizione condizione)
    {
        var componente = string.Join("|", condizione.Componente.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(e => e, StringComparer.OrdinalIgnoreCase));
        if (componente.Length == 0) return null;
        if (condizione.RiguardaDtc) return "Guasto\u0001" + componente + "\u0001" + (int)(condizione.DtcPasso ?? PassoDtc.Validazione);
        return string.IsNullOrWhiteSpace(condizione.Funzionalita) ? null : "OP\u0001" + componente + "\u0001" + condizione.Funzionalita.Trim();
    }

    /// <summary>Prenota la copia della durata per l'operazione. Ritorna null quando non serve: l'operatore ha gia' una durata digitata
    /// (la prenotazione in volo viene annullata) oppure la chiave e' incompleta. Ogni prenotazione rende superata la precedente per la
    /// stessa operazione, cosi' un risultato arrivato in ritardo non viene applicato.</summary>
    internal RichiestaCopiaDurata? PrenotaCopiaDurata(Condizione condizione)
    {
        CollegaCopiaDurata(condizione);
        // Un valore (anche vuoto) digitato dall'operatore non si propone mai: la copia non lo sovrascrive.
        if (condizione.DurataModificataDallOperatore || (condizione.DurataMinuti is not null && !condizione.DurataDaCopiaAutomatica))
        {
            _generazioneCopiaPerOperazione.Remove(condizione);
            return null;
        }
        var generazione = ++_generazioneCopiaDurata;
        _generazioneCopiaPerOperazione[condizione] = generazione;
        var chiave = ChiaveCopia(condizione);
        if (chiave is null)
        {
            if (condizione.DurataDaCopiaAutomatica) condizione.ImpostaDurataDaCopia(null);   // la chiave non c'e' piu': la copia precedente non vale
            return null;
        }
        var componente = condizione.Componente.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var funzionalita = condizione.RiguardaDtc ? null : condizione.Funzionalita.Trim();
        int? passoDtc = condizione.RiguardaDtc ? (int)(condizione.DtcPasso ?? PassoDtc.Validazione) : null;
        return new RichiestaCopiaDurata(generazione, chiave, componente, funzionalita, passoDtc, TestCase.VeicoliEffettivi.ToList());
    }

    /// <summary>TIME (6.6.1), regola di copia per una nuova operazione o per una con componente, Funzionalita o tipo guasto cambiati: la durata piu'
    /// recente della stessa chiave (vedi LocalTestIndexStore.TrovaDurataCopia). Eseguita fuori dal thread UI; il valore copiato resta
    /// modificabile e non e' una nuova misura.</summary>
    private void AvviaCopiaDurataOperazione(Condizione condizione)
    {
        var richiesta = PrenotaCopiaDurata(condizione);
        if (richiesta is null) return;
        var repository = _state.TestCases;
        var attivita = Task.Run(() => repository.TrovaDurataCopia(richiesta.Componente, richiesta.Funzionalita, richiesta.Veicoli, richiesta.PassoDtc))
            .ContinueWith(t =>
            {
                // Un errore della query non deve sparire: la durata non viene proposta, ma il motivo resta nel log.
                if (t.IsFaulted) CrashLogService.Registra("Durata copia (editor Test)", t.Exception);
                if (!t.IsCompletedSuccessfully) return;
                PubblicaRisultatoCopia(() => ApplicaDurataCopiata(condizione, richiesta.Generazione, richiesta.Chiave, t.Result));
            }, TaskScheduler.Default);
        _copieDurataInCorso.RemoveAll(c => c.IsCompleted);
        _copieDurataInCorso.Add(attivita);
    }

    /// <summary>Applica la durata proposta dalla regola di copia solo se il risultato e' ancora valido: e' la richiesta piu' recente per
    /// l'operazione, l'operazione e' ancora nella revisione corrente, l'operatore non ha digitato una durata (nemmeno un testo non valido
    /// in corso) e la chiave componente/Funzionalita (o guasto) non e' cambiata. Un risultato si applica una sola volta.</summary>
    internal bool ApplicaDurataCopiata(Condizione condizione, int generazione, string chiave, int? minuti)
    {
        if (!_generazioneCopiaPerOperazione.TryGetValue(condizione, out var attesa) || attesa != generazione) return false;
        if (RevisioneCorrente?.Condizioni.Any(c => ReferenceEquals(c, condizione)) != true) return false;
        if (condizione.DurataTesto.HaErrore) return false;
        // Un vuoto digitato dall'operatore durante la copia in volo resta vuoto: la copia non lo sovrascrive (vedi PrenotaCopiaDurata).
        if (condizione.DurataModificataDallOperatore) return false;
        if (condizione.DurataMinuti is not null && !condizione.DurataDaCopiaAutomatica) return false;
        if (!string.Equals(ChiaveCopia(condizione), chiave, StringComparison.Ordinal)) return false;
        _generazioneCopiaPerOperazione.Remove(condizione);
        if (minuti is null)
        {
            if (condizione.DurataDaCopiaAutomatica) condizione.ImpostaDurataDaCopia(null);
            return false;
        }
        condizione.ImpostaDurataDaCopia(minuti);
        ModificheNonSalvate = true;
        return true;
    }

    /// <summary>Collega una sola volta l'operazione alla regola di copia: cambiare componente, Funzionalita o tipo guasto la rimette in copia (solo
    /// se la durata e' ancora vuota o proposta dalla copia). Attivo dopo il caricamento dell'editor.</summary>
    private void CollegaCopiaDurata(Condizione condizione)
    {
        if (!_copiaDurataAttiva || !_condizioniCollegateCopia.Add(condizione)) return;
        condizione.PropertyChanged += OnChiaveOperazioneCambiata;
    }

    private void OnChiaveOperazioneCambiata(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not Condizione condizione) return;
        if (e.PropertyName is not (nameof(Condizione.Componente) or nameof(Condizione.Funzionalita) or nameof(Condizione.Guasto) or nameof(Condizione.DtcPasso))) return;
        if (RevisioneCorrente?.Condizioni.Any(c => ReferenceEquals(c, condizione)) != true) return;   // revisioni storiche: nessuna copia
        AvviaCopiaDurataOperazione(condizione);
    }

    /// <summary>Firma dei campi che definiscono l'operazione, durata inclusa, con ordine stabile: dice se una operazione e' nuova o
    /// modificata rispetto all'ultimo salvataggio (durata obbligatoria, vedi Salva).</summary>
    private static string FirmaOperazione(Condizione c)
    {
        // Le evidenze collegate non definiscono l'operazione: collegarle/scollegarle non la rende "modificata" (durata obbligatoria).
        var strumenti = string.Join(';', c.ValoriStrumenti.Where(kv => kv.Key != EvidenzaOperazione.ChiaveValoriStrumenti).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
        return string.Join('\u0002', new[]
        {
            $"{c.Esito}", string.Join(',', c.Componente.OrderBy(e => e, StringComparer.Ordinal)), $"{c.Funzionalita}", $"{c.Guasto}", $"{c.DtcPasso}",
            $"{c.DtcRequisitoId}", $"{c.DtcRequisitoVuotoScelto}", $"{c.DtcVeicolo}", $"{c.Strumento}", $"{c.Nota}",
            $"{c.MotivazioneNonApplicabile}", $"{c.DescrizioneDeviazione}", $"{c.DescrizioneProblema}", strumenti, $"{c.DurataMinuti}",
        });
    }

    /// <summary>Vero se l'operazione e' nuova o diversa dall'ultima versione salvata (test nuovo: sempre nuova). Le operazioni salvate e
    /// invariate restano fuori dall'obbligo della durata: nessun valore storico viene inventato.</summary>
    internal bool OperazioneNuovaOModificata(Condizione condizione)
    {
        // Stessa identita del Report (Condizione.Uid): vale l'ULTIMA revisione salvata in cui l'operazione compare. Con la prima
        // occorrenza una revisione storica (senza durata o con un'altra nota) renderebbe "modificata" un'operazione invariata, e
        // una durata cancellata dopo una revisione precedente non verrebbe confrontata con l'ultimo valore salvato.
        var salvata = _snapshot?.Esecuzioni.SelectMany(e => e.Condizioni).LastOrDefault(c => c.Uid == condizione.Uid);
        return salvata is null || !string.Equals(FirmaOperazione(salvata), FirmaOperazione(condizione), StringComparison.Ordinal);
    }

    /// <summary>Applica direttamente il suggerimento mostrato nel banner (feedback utente 2026-08-20, punto
    /// 22: "i suggerimenti sono spariti" — il meccanismo storico esisteva ma solo come effetto collaterale
    /// silenzioso di "+ Aggiungi condizione" quando non c'era già nulla da copiare, difficile da notare. Ora il
    /// banner compare proattivamente appena Funzionalità/Tipo test permettono una ricerca, con un'azione
    /// esplicita invece di doverlo scoprire per caso). Riempie la condizione vuota già presente (es. quella
    /// iniziale del wizard) invece di aggiungerne un'altra, se ce n'è una; altrimenti ne aggiunge una nuova.</summary>
    [RelayCommand]
    private void UsaSuggerimentoCronologia()
    {
        if (RevisioneCorrente == null || SoloOperazioniDtc) return;
        var suggerita = TrovaCondizioneSuggeritaDallaCronologia();
        if (suggerita == null) return;
        // Il suggerimento copia solo i campi strumento: la durata (TIME) resta propria di ogni operazione.
        var campi = ClonaCampiStrumenti(suggerita.Value.Condizione.ValoriStrumenti);

        var vuota = RevisioneCorrente.Condizioni.FirstOrDefault(c => !DurataOperazione.HaValoriStrumenti(c.ValoriStrumenti));
        var destinazione = vuota;
        if (destinazione == null)
        {
            destinazione = new Condizione { Esito = Esito.DaEseguire, Componente = EcuAutoSuNuovaOperazione, Funzionalita = FunzionalitaAutoSeSingola };
            RevisioneCorrente.Condizioni.Add(destinazione);
        }
        var durataPropria = destinazione.DurataMinuti;
        // Il suggerimento copia solo i campi strumento: le evidenze gia' collegate a questa operazione restano al loro posto.
        var evidenzeProprie = destinazione.ValoriStrumenti.TryGetValue(EvidenzaOperazione.ChiaveValoriStrumenti, out var ev) ? ev : null;
        destinazione.ValoriStrumenti = campi;
        if (evidenzeProprie is not null) destinazione.ValoriStrumenti[EvidenzaOperazione.ChiaveValoriStrumenti] = evidenzeProprie;
        destinazione.DurataMinuti = durataPropria;
        AvviaCopiaDurataOperazione(destinazione);   // destinazione nuova o ancora senza durata: la regola di copia la propone (TIME)
        AggiornaCampiVisualCondizione(destinazione);
        SincronizzaCondizioniVisual();
        NotificaCoperturaCambiata();
        SuggerimentoCronologiaTesto = null;
        SuggerimentoCronologiaTestId = null;
        ModificheNonSalvate = true;
    }

    /// <summary>Ricalcola il banner suggerimento ogni volta che Funzionalità o Tipo test cambiano (feedback
    /// utente 2026-08-20, punto 22), non solo alla prima "+ Aggiungi condizione" — così è visibile appena si
    /// hanno abbastanza dati per una ricerca utile, prima ancora che l'utente pensi ad aggiungere una
    /// reiterazione. Non ha senso se esiste già una condizione con dati reali in questa revisione: a quel
    /// punto "+ Aggiungi condizione" copia comunque da quella, un punto di partenza più pertinente.</summary>
    private void AggiornaSuggerimentoCronologia()
    {
        var haDatiReali = RevisioneCorrente?.Condizioni.Any(c => DurataOperazione.HaValoriStrumenti(c.ValoriStrumenti)) == true;
        if (haDatiReali)
        {
            SuggerimentoCronologiaTesto = null;
            SuggerimentoCronologiaTestId = null;
            return;
        }
        var suggerita = TrovaCondizioneSuggeritaDallaCronologia();
        SuggerimentoCronologiaTestId = suggerita?.TestId;
        SuggerimentoCronologiaTesto = suggerita != null
            ? string.Format(LocalizationService.Instance.Traduci("CampiPrecompilatiFmt"), suggerita.Value.TestId)
            : null;
    }

    private (string TestId, Condizione Condizione)? TrovaCondizioneSuggeritaDallaCronologia()
    {
        if (!_state.CartellaValida || TestCase.Funzionalita.Count == 0) return null;
        var funSet = TestCase.Funzionalita.ToHashSet(StringComparer.OrdinalIgnoreCase);
        TestCase? bestTest = null;
        Condizione? bestCond = null;
        DateTime bestDate = DateTime.MinValue;

        foreach (var t in _state.TestCases.GetAll())
        {
            if (t.Id == TestCase.Id || t.TestTemplateUid != TestCase.TestTemplateUid || t.Esecuzioni.Count == 0 || t.UltimaModifica <= bestDate)
                continue;

            if (!t.Funzionalita.Any(funSet.Contains))
                continue;

            var cond = t.Esecuzioni[^1].Condizioni.LastOrDefault();
            if (cond != null)
            {
                bestTest = t;
                bestCond = cond;
                bestDate = t.UltimaModifica;
            }
        }

        return (bestTest != null && bestCond != null) ? (bestTest.Id, bestCond) : null;
    }

    /// <summary>Punto di partenza rapido per il caso più comune (Addendum §19): stesso servizio/PIN della
    /// condizione esistente, da correggere solo dove cambia la condizione/risposta, invece di reinserire tutto.</summary>
    [RelayCommand]
    private void RipetiCondizione(Condizione? origine)
    {
        if (RevisioneCorrente == null || origine == null) return;
        var indice = RevisioneCorrente.Condizioni.IndexOf(origine);
        var copia = new Condizione
        {
            Esito = Esito.DaEseguire,
            Componente = new EcuSelection(origine.Componente),
            Funzionalita = origine.Funzionalita,
            Guasto = origine.Guasto, DtcRequisitoId = origine.DtcRequisitoId, DtcRequisitoVuotoScelto = origine.DtcRequisitoVuotoScelto, DtcPasso = origine.DtcPasso,
            DtcVeicolo = origine.DtcVeicolo, Strumento = origine.Strumento,
            OperazioneOrigineUid = origine.OperazioneOrigineUid ?? origine.Uid,
            ValoriStrumenti = ClonaCampiStrumenti(origine.ValoriStrumenti)
        };
        RevisioneCorrente.Condizioni.Insert(indice < 0 ? RevisioneCorrente.Condizioni.Count : indice + 1, copia);
        AvviaCopiaDurataOperazione(copia);
        AggiornaCampiVisualCondizione(copia);
        SincronizzaCondizioniVisual();
        NotificaCoperturaCambiata();
        ModificheNonSalvate = true;
    }

    [RelayCommand]
    private async Task RimuoviCondizione(Condizione? condizione)
    {
        if (RevisioneCorrente == null || condizione == null) return;
        // Un'operazione con segnalazioni/anomalie collegate (CondizioneUid) non sparisce in silenzio: i record
        // collegati resterebbero senza l'operazione di origine visibile (le segnalazioni non compaiono più nel
        // pannello). Conferma esplicita prima di rimuovere; il pannello tollera comunque i riferimenti orfani
        // (condizione == null -> riga saltata, RisolviProva -> null).
        var collegamenti = ContaCollegamentiCondizione(condizione);
        if (collegamenti > 0)
        {
            var loc = LocalizationService.Instance;
            if (!await ConfirmService.Chiedi(loc.Traduci("RimuoviOperazioneCollegataTitolo"),
                    string.Format(loc.Traduci("RimuoviOperazioneCollegataBodyFmt"), collegamenti),
                    loc.Traduci("EliminaBtn"), loc.Traduci("Annulla")))
                return;
        }
        RevisioneCorrente.Condizioni.Remove(condizione);
        condizione.ScollegaDurataTesto();   // TIME: l'operazione rimossa non resta sottoscritta al proprio testo della durata
        SincronizzaCondizioniVisual();
        NotificaCoperturaCambiata();
        ModificheNonSalvate = true;
    }

    /// <summary>Segnalazioni e prove di anomalie (del pannello o nuove) che puntano a questa operazione.</summary>
    private int ContaCollegamentiCondizione(Condizione condizione)
    {
        var uid = condizione.Uid;
        var segnalazioni = SegnalazioniTest.Select(r => r.Segnalazione).Concat(_segnalazioniNuove)
            .Where(x => x.CondizioneUid == uid).Select(x => x.Uid).Distinct().Count();
        var anomalie = AnomalieTestAperte.Concat(AnomalieTestChiuse).Select(r => r.Nc).Concat(_anomalieNuove)
            .Where(n => n.ProveCollegate.Any(p => p.CondizioneUid == uid)).Select(n => n.Uid).Distinct().Count();
        return segnalazioni + anomalie;
    }

    /// <summary>La revisione più recente = quella attualmente modificabile (Addendum §17.7). Le revisioni
    /// precedenti restano visibili in sola lettura nello storico, mai più modificate.</summary>
    /// <summary>matricola/Baseline mostrati nel form: la revisione sfogliata (sola lettura se storica), ma la
    /// SCRITTURA va sempre e solo sulla revisione corrente e solo se modificabile. E' la stessa istanza che
    /// legge la validazione dei campi obbligatori (ValoreCampoSistemaVuoto): prima il TextBox scriveva su
    /// RevisioneVisualizzata e la validazione leggeva RevisioneCorrente, due oggetti diversi quando si era
    /// sfogliata una REV storica (matricola visibile a schermo ma "obbligatorio mancante" al Salva).</summary>
    public string VinRevisione
    {
        get => (RevisioneVisualizzata ?? RevisioneCorrente)?.Matricola ?? "";
        set => ImpostaCampoRevisione(RevisioneCorrente?.Matricola, v => RevisioneCorrente!.Matricola = v, value, nameof(VinRevisione));
    }
    public string BaselineSwRevisione
    {
        get => (RevisioneVisualizzata ?? RevisioneCorrente)?.BaselineSw ?? "";
        set => ImpostaCampoRevisione(RevisioneCorrente?.BaselineSw, v => RevisioneCorrente!.BaselineSw = v, value, nameof(BaselineSwRevisione));
    }
    public string BaselineHwRevisione
    {
        get => (RevisioneVisualizzata ?? RevisioneCorrente)?.BaselineHw ?? "";
        set => ImpostaCampoRevisione(RevisioneCorrente?.BaselineHw, v => RevisioneCorrente!.BaselineHw = v, value, nameof(BaselineHwRevisione));
    }

    private void ImpostaCampoRevisione(string? attuale, Action<string> scrivi, string? nuovo, string nomeProprieta)
    {
        if (RevisioneCorrente == null || !PuoModificareContenuto) return;
        nuovo ??= "";
        if ((attuale ?? "") == nuovo) return;
        scrivi(nuovo);
        OnPropertyChanged(nomeProprieta);
        ModificheNonSalvate = true;
    }

    /// <summary>matricola/Baseline si scrivono solo su una REV esistente e modificabile. Senza REV (test pianificato
    /// non ancora avviato) il campo e' disabilitato e spiega come abilitarlo: creare la REV in silenzio al primo
    /// carattere altererebbe conteggi/pianificazione, scartare il valore sarebbe perdita dati silenziosa.</summary>
    public bool PuoModificareCampiRevisione => PuoModificareContenuto && HaRevisioneCorrente;
    public string VinPlaceholder => HaRevisioneCorrente
        ? LocalizationService.Instance.Traduci("VinORiferimentoBancoPlaceholder")
        : LocalizationService.Instance.Traduci("VinSenzaRevPlaceholder");

    private void NotificaCampiRevisione()
    {
        OnPropertyChanged(nameof(PuoModificareCampiRevisione));
        OnPropertyChanged(nameof(VinPlaceholder));
        OnPropertyChanged(nameof(VinRevisione));
        OnPropertyChanged(nameof(BaselineSwRevisione));
        OnPropertyChanged(nameof(BaselineHwRevisione));
    }

    public Esecuzione? RevisioneCorrente => Esecuzioni.Count > 0 ? Esecuzioni[^1] : null;
    public bool HaRevisioneCorrente => RevisioneCorrente != null;

    /// <summary>Menu REV in alto alla pagina (Addendum §6, feedback QA): naviga qualunque revisione, non solo
    /// la corrente — a differenza del vecchio storico in sola lettura in fondo alla pagina (che mostrava solo
    /// Iterazione/Data/Operatore/Esito), qui le sezioni Dettagli/Operazioni sotto si aggiornano per mostrare
    /// matricola/baseline/campi per intero. Più recenti prime, così la revisione corrente è sempre in cima.</summary>
    public List<Esecuzione> RevisioniPerMenu => Esecuzioni.Reverse().ToList();

    [ObservableProperty] private Esecuzione? revisioneVisualizzata;

    /// <summary>Sola navigazione: Dettagli/Operazioni restano modificabili solo sulla revisione corrente. Si
    /// riallinea automaticamente ogni volta che si entra in modifica, così non si rischia di editare "sopra"
    /// una revisione storica per sbaglio dopo averla sfogliata in sola lettura.</summary>
    public bool VisualizzaStorico => RevisioneVisualizzata != null && RevisioneVisualizzata != RevisioneCorrente;

    /// <summary>Dettagli/Operazioni sono editabili solo su InModifica E quando si sta guardando la revisione
    /// corrente: sfogliare lo storico dal menu REV resta possibile in qualunque momento, senza dover uscire
    /// dalla modifica, ma non deve mai permettere di scrivere "sopra" una revisione congelata.</summary>
    public bool PuoModificareContenuto => InModifica && !VisualizzaStorico;

    /// <summary>Snapshot completo della revisione sfogliata, non solo matricola/Baseline/Operazioni (Addendum §1,
    /// feedback QA): null sulle revisioni create prima di questo aggiornamento.</summary>
    public DettagliTestSnapshot? DettagliStorico => RevisioneVisualizzata?.Dettagli;
    public bool DettagliStoriciNonDisponibili => VisualizzaStorico && RevisioneVisualizzata?.Dettagli == null;

    /// <summary>Etichette dei campi che differiscono tra la revisione sfogliata e quella più recente (Addendum
    /// §1, feedback QA: "evidenziarmi i cambiamenti rispetto alla rev più recente"). Vuoto se non si sta
    /// sfogliando lo storico, o se manca lo snapshot su uno dei due lati (nulla da confrontare con certezza).</summary>
    public List<string> CampiCambiatiRispettoARecente { get; private set; } = new();
    public bool HaCampiCambiati => CampiCambiatiRispettoARecente.Count > 0;
    public string TestoCampiCambiati => string.Format(LocalizationService.Instance.Traduci("CampiCambiatiRispettoARecenteFmt"), string.Join(", ", CampiCambiatiRispettoARecente));

    private void AggiornaConfrontoConRevisionePiuRecente()
    {
        CampiCambiatiRispettoARecente = new List<string>();
        var storica = RevisioneVisualizzata;
        var recente = RevisioneCorrente;
        if (VisualizzaStorico && storica?.Dettagli != null && recente?.Dettagli != null)
        {
            var d1 = storica.Dettagli; var d2 = recente.Dettagli;
            void Confronta(bool diverso, string etichetta) { if (diverso) CampiCambiatiRispettoARecente.Add(etichetta); }
            Confronta(!d1.Componente.SequenceEqual(d2.Componente, StringComparer.OrdinalIgnoreCase), "Componente");
            Confronta(!d1.Veicoli.OrderBy(x => x).SequenceEqual(d2.Veicoli.OrderBy(x => x)), LocalizationService.Instance.Traduci("TcConfrontaVeicoliTxt"));
            Confronta(d1.TestTemplateUid != d2.TestTemplateUid, LocalizationService.Instance.Traduci("TcConfrontaTipoTestTxt"));
            Confronta(!d1.Funzionalita.OrderBy(x => x).SequenceEqual(d2.Funzionalita.OrderBy(x => x)), LocalizationService.Instance.Traduci("TcFunzionalitaEtichetta"));
            Confronta(!d1.StrumentiUsati.OrderBy(x => x).SequenceEqual(d2.StrumentiUsati.OrderBy(x => x)), LocalizationService.Instance.Traduci("TcConfrontaStrumentiUsatiTxt"));
            Confronta(d1.DescrizioneBreve != d2.DescrizioneBreve, "Descrizione breve");
            Confronta(d1.Descrizione != d2.Descrizione, "Descrizione");
            Confronta(d1.DescrizioneProblema != d2.DescrizioneProblema, "Descrizione problema");
            Confronta(storica.Matricola != recente.Matricola, "Matricola");
            Confronta(storica.BaselineSw != recente.BaselineSw, "Baseline SW");
            Confronta(storica.BaselineHw != recente.BaselineHw, "Baseline HW");
        }
        OnPropertyChanged(nameof(CampiCambiatiRispettoARecente));
        OnPropertyChanged(nameof(HaCampiCambiati));
        OnPropertyChanged(nameof(TestoCampiCambiati));
    }

    partial void OnRevisioneVisualizzataChanged(Esecuzione? value)
    {
        NotificaCampiRevisione();
        SincronizzaCondizioniVisual();
        AggiornaCampiRevisioneCostanti();
        AggiornaConfrontoConRevisionePiuRecente();
        OnPropertyChanged(nameof(VisualizzaStorico));
        OnPropertyChanged(nameof(PuoModificareContenuto));
        OnPropertyChanged(nameof(MostraBloccoAggiungiDtc));
        OnPropertyChanged(nameof(DettagliStorico));
        OnPropertyChanged(nameof(DettagliStoriciNonDisponibili));
        NotificaVisibilitaCampiTemplate();
    }

    /// <summary>Wrapper sull'Operatore della revisione corrente: ogni volta che cambia, l'Assegnatario si
    /// allinea automaticamente (Addendum §18) — chi esegue il test ne diventa il responsabile corrente, anche
    /// se in precedenza era stato assegnato a qualcun altro (es. Master assegna a B, B non lo fa, C se lo
    /// prende ed esegue: C diventa il nuovo assegnatario). Un'assegnazione manuale pre-esecuzione resta
    /// comunque possibile tramite AssegnatarioSelezionato (solo Master, PuoAssegnare) finché nessuno esegue.</summary>
    public string RevisioneCorrenteOperatore
    {
        get => RevisioneCorrente?.Operatore ?? "";
        set
        {
            // Vedi _riallineamentoListeMaster: durante il ricaricamento periodico dei master data la ComboBox
            // riscrive un valore transitorio (anche in sola lettura) che altrimenti sporcherebbe il Test e
            // sovrascriverebbe l'Operatore della revisione.
            if (_riallineamentoListeMaster) return;
            if (RevisioneCorrente == null || RevisioneCorrente.Operatore == value) return;
            RevisioneCorrente.Operatore = value;
            AssegnatarioSelezionato = value;
            OnPropertyChanged();
            ModificheNonSalvate = true;
        }
    }

    /// <summary>Esito effettivo mostrato/modificabile: di norma è la condizione peggiore tra quelle inserite
    /// (Addendum §19) e non è editabile a mano — solo RevisioneCorrenteEsitoSovrascritto lo sblocca. Wrapper
    /// per notificare MostraDescrizioneProblema in tempo reale (Esecuzione è un POCO senza notifiche proprie).</summary>
    public Esito RevisioneCorrenteEsito
    {
        get => RevisioneCorrente == null ? Esito.DaEseguire
            : RevisioneCorrente.EsitoSovrascritto ? RevisioneCorrente.Esito : RevisioneCorrente.EsitoCalcolato;
        set
        {
            if (RevisioneCorrente == null || !RevisioneCorrente.EsitoSovrascritto || RevisioneCorrente.Esito == value) return;
            RevisioneCorrente.Esito = value;
            OnPropertyChanged();
            NotificaVisibilitaCampiTemplate();
            ModificheNonSalvate = true;
        }
    }

    public Esito RevisioneCorrenteEsitoCalcolato => RevisioneCorrente?.EsitoCalcolato ?? Esito.DaEseguire;

    /// <summary>Riepilogo per componente×Funzionalità della revisione corrente (feedback utente 2026-08-25, punto 9,
    /// seconda ondata: "l'esito complessivo del test non ha più senso ormai... dovrebbe essere un esito per
    /// componente x funzionalità in fondo al test come riepilogo, al posto di dove adesso abbiamo esito dalla
    /// condizione peggiore di tutto il test"): un'operazione con più componente contribuisce a una riga per ciascuna;
    /// più operazioni sulla stessa coppia componente+Funzionalità si aggregano con la condizione peggiore, stesso
    /// principio di Esecuzione.EsitoCalcolato. RevisioneCorrenteEsito resta comunque calcolato/disponibile
    /// sotto (serve al workflow anomalie/punteggio conformità), solo non più il badge principale mostrato qui.</summary>
    public IReadOnlyList<RiepilogoEcuFunzionalitaRiga> RiepilogoEcuFunzionalita
    {
        get
        {
            if (RevisioneCorrente == null) return Array.Empty<RiepilogoEcuFunzionalitaRiga>();
            var gruppi = new Dictionary<(string Componente, string Funzionalita), List<Esito>>();
            foreach (var c in RevisioneCorrente.CondizioniEffettive)
            {
                var ecuElenco = c.Componente.Count > 0 ? (IEnumerable<string>)c.Componente : new[] { "" };
                foreach (var componente in ecuElenco)
                {
                    var chiave = (componente, c.Funzionalita);
                    if (!gruppi.TryGetValue(chiave, out var lista)) gruppi[chiave] = lista = new List<Esito>();
                    lista.Add(c.Esito);
                }
            }
            // Decisione utente 6.4.0, punto 1: solo le operazioni eseguite concorrono al riepilogo — altrimenti
            // "Da eseguire"/N.A. (gravità intermedia, vedi Esecuzione.GravitaEsito) potrebbero dominare un
            // Conforme realmente presente sulla stessa coppia componente/Funzionalità. Nessuna operazione eseguita su
            // quella coppia => "Da eseguire", coerente con EsitoCalcolato.
            return gruppi
                .Select(kv =>
                {
                    var eseguiti = kv.Value.Where(e => e.EEseguito()).ToList();
                    var esito = eseguiti.Count > 0 ? Esecuzione.Peggiore(eseguiti) : Esito.DaEseguire;
                    return new RiepilogoEcuFunzionalitaRiga(kv.Key.Componente, kv.Key.Funzionalita, esito);
                })
                .OrderBy(r => r.Componente, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Funzionalita, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Componenti collegati al test senza ancora un'operazione che le copra davvero (Addendum: audit UX
    /// 2026-08-26, punto 8 ridotto): stesso identico calcolo del controllo in Valida() (ValEcuSenzaOperazioneFmt),
    /// spostato qui perché la lista compaia mentre si compila, non solo come errore al momento di Salvare. Non
    /// una nuova regola — la stessa, mostrata prima invece che dopo.</summary>
    public IReadOnlyList<string> EcuNonCoperte
    {
        get
        {
            if (RevisioneCorrente == null || TestCase.Componente.Count == 0) return Array.Empty<string>();
            var ecuCoperte = RevisioneCorrente.CondizioniEffettive.SelectMany(c => c.Componente).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return TestCase.Componente.Where(e => !ecuCoperte.Contains(e)).ToList();
        }
    }

    /// <summary>Funzionalità collegate al test senza ancora un'operazione che le copra davvero — stesso calcolo
    /// di ValFunzionalitaSenzaOperazioneFmt in Valida(). A differenza di EcuNonCoperte questa lista è quasi
    /// sempre quella che conta nella pratica: una nuova operazione nasce con tutti i componenti del test già
    /// preselezionate (vedi EcuAutoSuNuovaOperazione), ma con la Funzionalità vuota quando il test ne collega
    /// 2+ (vedi FunzionalitaAutoSeSingola) — è la parte che l'operatore deve davvero ricordarsi di compilare
    /// per ciascuna operazione.</summary>
    public IReadOnlyList<string> FunzionalitaNonCoperte
    {
        get
        {
            if (RevisioneCorrente == null || !FunzionalitaOperazioneObbligatoria || RevisioneCorrente.Condizioni.Any(c => c.RiguardaDtc)) return Array.Empty<string>();
            var funzionalitaCoperte = RevisioneCorrente.CondizioniEffettive.Select(c => c.Funzionalita).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return TestCase.Funzionalita.Where(f => !funzionalitaCoperte.Contains(f)).ToList();
        }
    }

    public bool HaCoperturaIncompleta => EcuNonCoperte.Count > 0 || FunzionalitaNonCoperte.Count > 0 || VeicoloEcuSenzaDtc.Count > 0;

    /// <summary>Le tre proprietà sopra sono getter puri sulle stesse Condizioni, quindi vanno rinotificate
    /// insieme ad ogni punto in cui operazioni/componente/Funzionalità del test cambiano davvero — stesso insieme di
    /// punti che già notificava solo RiepilogoEcuFunzionalita prima di questo intervento.</summary>
    private void NotificaCoperturaCambiata()
    {
        OnPropertyChanged(nameof(OperazioniRapideTotali));
        NotificaFlussoRapido();
        OnPropertyChanged(nameof(OperazioniRapideEseguite));
        OnPropertyChanged(nameof(OperazioniRapideDaEseguire));
        OnPropertyChanged(nameof(RiepilogoEcuFunzionalita));
        OnPropertyChanged(nameof(EcuNonCoperte));
        OnPropertyChanged(nameof(FunzionalitaNonCoperte));
        OnPropertyChanged(nameof(VeicoloEcuSenzaDtc));
        OnPropertyChanged(nameof(HaCoperturaIncompleta));
    }

    /// <summary>Sblocca la correzione manuale dell'esito calcolato dalle condizioni (Addendum §19): chi ha in
    /// carico il test può correggerlo, ma solo fornendo una motivazione (obbligatoria, verificata in Salva()).
    /// Disattivarla fa tornare l'esito ad allinearsi automaticamente alla condizione peggiore.</summary>
    public bool RevisioneCorrenteEsitoSovrascritto
    {
        get => RevisioneCorrente?.EsitoSovrascritto ?? false;
        set
        {
            if (RevisioneCorrente == null || RevisioneCorrente.EsitoSovrascritto == value) return;
            RevisioneCorrente.EsitoSovrascritto = value;
            if (!value)
            {
                RevisioneCorrente.Esito = RevisioneCorrente.EsitoCalcolato;
                RevisioneCorrente.MotivazioneOverride = "";
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(RevisioneCorrenteEsito));
            OnPropertyChanged(nameof(RevisioneCorrenteMotivazioneOverride));
            NotificaVisibilitaCampiTemplate();
            ModificheNonSalvate = true;
        }
    }

    public string RevisioneCorrenteMotivazioneOverride
    {
        get => RevisioneCorrente?.MotivazioneOverride ?? "";
        set
        {
            if (RevisioneCorrente == null || RevisioneCorrente.MotivazioneOverride == value) return;
            RevisioneCorrente.MotivazioneOverride = value;
            OnPropertyChanged();
            ModificheNonSalvate = true;
        }
    }

    /// <summary>Da richiamare ogni volta che una condizione viene aggiunta/rimossa/duplicata o il suo esito
    /// cambia: tiene l'esito della revisione allineato alla condizione peggiore finché non è sovrascritto a
    /// mano. Sicura da chiamare spesso (es. a ogni modifica di campo nel form, vedi View code-behind) perché
    /// non fa nulla se non c'è una revisione corrente o l'esito è già allineato.</summary>
    /// <summary>Riepilogo per strumento (solo raggruppamento + contatore "UnitLogger 2/3 OK" delle operazioni di proprieta di uno strumento, REV
    /// visualizzata): NON e un esito aggregato, l'esito del test resta calcolato per operazione.</summary>
    public IReadOnlyList<GruppoOperazioniStrumento> GruppiOperazioniStrumento { get; private set; } = Array.Empty<GruppoOperazioniStrumento>();
    public bool HaGruppiOperazioniStrumento => GruppiOperazioniStrumento.Count > 0;

    private void AggiornaGruppiOperazioniStrumento()
    {
        var rev = RevisioneVisualizzata ?? RevisioneCorrente;
        GruppiOperazioniStrumento = rev == null ? Array.Empty<GruppoOperazioniStrumento>()
            : OperazioniStrumentoRegole.Raggruppa(rev.Condizioni, rev.CondizioniEffettive);
        OnPropertyChanged(nameof(GruppiOperazioniStrumento));
        OnPropertyChanged(nameof(HaGruppiOperazioniStrumento));
    }

    public void SincronizzaEsitoCondizioni()
    {
        if (RevisioneCorrente == null) return;
        RevisioneCorrente.SincronizzaEsito();
        AggiornaGruppiOperazioniStrumento();
        OnPropertyChanged(nameof(RevisioneCorrenteEsito));
        OnPropertyChanged(nameof(RevisioneCorrenteEsitoCalcolato));
        NotificaCoperturaCambiata();
        NotificaVisibilitaCampiTemplate();
        PunteggioConformitaService.SincronizzaPunteggi(RevisioneCorrente, TestCase.RequisitoId);
        AggiornaPunteggiRequisitiVisual();
        CaricaPannelloAnomalie(creaMancanti: true);
    }

    /// <summary>Rispecchia RevisioneCorrente.PunteggiRequisiti nella collezione bindabile dalla View — va
    /// richiamata ogni volta che la lista sottostante può essere cambiata (Esito, Requisiti collegati, o
    /// cambio di revisione corrente), stesso principio di RequisitiVisual/CondizioniVisual.</summary>
    private void AggiornaPunteggiRequisitiVisual()
    {
        PunteggiRequisitiVisual.Clear();
        if (RevisioneCorrente == null) return;
        foreach (var p in RevisioneCorrente.PunteggiRequisiti)
            PunteggiRequisitiVisual.Add(new PunteggioRequisitoRowViewModel(p, () => ModificheNonSalvate = true));
    }

    /// <summary>Campo "Descrizione problema": per default visibile solo quando l'esito della revisione corrente
    /// indica un problema, ora configurabile da template (Addendum: collaudo live 2026-08-17 — era fisso nel
    /// codice, il seed riproduce lo stesso comportamento di prima come default).</summary>
    public bool MostraDescrizioneProblema => RevisioneCorrente != null && MostraCampo(CampiSistema.DescrizioneProblema);
    public bool HaNonConformitaCollegata => !string.IsNullOrEmpty(RevisioneCorrente?.NonConformitaId);
    public bool ChangelogVuoto => TestCase.Changelog.Count == 0;
    public IEnumerable<Esecuzione> StoricoRevisioni => Esecuzioni.Count > 1 ? Esecuzioni.Take(Esecuzioni.Count - 1).Reverse() : Enumerable.Empty<Esecuzione>();

    // ----- Finestra dedicata per campi di testo lunghi (richiesta utente: il TextBox multilinea annegato nel
    // form è scomodo quando c'è tanto da scrivere) — TestCase è un POCO, quindi dopo aver mutato un campo
    // annidato serve OnPropertyChanged sulla radice del percorso di binding ("TestCase"/"RevisioneCorrente")
    // perché Avalonia rivaluti i binding multilivello agganciati (es. "TestCase.Descrizione").

    [RelayCommand]
    private async Task EspandiDescrizione()
    {
        var risultato = await TextEditService.Modifica(LocalizationService.Instance.Traduci("DescrizioneObbl"), TestCase.Descrizione);
        if (risultato == null) return;
        TestCase.Descrizione = risultato;
        OnPropertyChanged(nameof(TestCase));
        ModificheNonSalvate = true;
    }

    [RelayCommand]
    private async Task EspandiDescrizioneProblema()
    {
        var risultato = await TextEditService.Modifica(LocalizationService.Instance.Traduci("DescrizioneProblemaLabel"), TestCase.DescrizioneProblema ?? "");
        if (risultato == null) return;
        TestCase.DescrizioneProblema = risultato;
        OnPropertyChanged(nameof(TestCase));
        ModificheNonSalvate = true;
    }

    [RelayCommand]
    private async Task EspandiNote()
    {
        if (RevisioneCorrente == null) return;
        var risultato = await TextEditService.Modifica(LocalizationService.Instance.Traduci("NoteLabel"), RevisioneCorrente.Note);
        if (risultato == null) return;
        RevisioneCorrente.Note = risultato;
        OnPropertyChanged(nameof(RevisioneCorrente));
        ModificheNonSalvate = true;
    }
    public bool HaStorico => Esecuzioni.Count > 1;
    public int NumeroRevisione => Esecuzioni.Count;

    // Wrapper ObservableCollection per le liste "a tag" del TestCase (List<string> semplice non notifica la
    // UI su Add/Remove): tenute sincronizzate con i campi reali del modello ad ogni Aggiungi/Rimuovi, così i
    // badge appaiono/spariscono subito invece di richiedere una ricarica della pagina per essere visibili.
    /// <summary>Flusso a 2 passi per un test nuovo (Addendum §18): prima si sceglie il Tipo test da solo
    /// (determina quali campi esisteranno), poi si compila tutto il resto insieme sullo stesso schermo. Per
    /// un test esistente il tipo è già deciso, quindi si salta direttamente al form completo.</summary>
    [ObservableProperty] private bool tipoTestScelto;
    partial void OnTipoTestSceltoChanged(bool value)
    {
        OnPropertyChanged(nameof(MostraAzioniModifica));
        OnPropertyChanged(nameof(MostraBarraSaltaSezione));
        OnPropertyChanged(nameof(MostraLinkSezioni));
        OnPropertyChanged(nameof(MostraMenuVaiA));
        OnPropertyChanged(nameof(MostraSchedeCreazione));
        NotificaLayoutModalitaEditor();
        // Autosave locale (Addendum: audit UX 2026-08-26, nota minore 1): parte solo quando c'è davvero
        // qualcosa di significativo da salvare — un test nuovo appena scelto il Tipo (passo 1 del wizard),
        // non la pagina vuota prima ancora di sapere che genere di test si sta creando.
        if (value && IsNew) AvviaAutosave();
    }

    /// <summary>La barra "salta alla sezione" (scroll, non cambia scheda) resta quella di sempre solo per un
    /// test esistente: su un test nuovo il contenuto non scelto dalla scheda corrente è nascosto, quindi
    /// scorrere fino a un ancoraggio nascosto non mostrerebbe nulla — vedi MostraSchedeCreazione per la sua
    /// controparte.</summary>
    public bool MostraBarraSaltaSezione => TipoTestScelto && (ModoEditorRapido || !IsNew);

    /// <summary>La striscia di schede (Addendum: audit UX 2026-08-26, punto 1) — solo in creazione, come
    /// richiesto esplicitamente: aprire o modificare un test esistente mostra sempre tutto insieme.</summary>
    public bool MostraSchedeCreazione => TipoTestScelto && IsNew && !ModoEditorRapido;

    [RelayCommand]
    private void SceglieTemplate(TestTemplate template)
    {
        TemplateSelezionato = template;
        TipoTestScelto = true;
        // Preselezione strumenti dichiarata dal template (Addendum redesign 2026-08, punto 2: sostituisce lo
        // switch fisso sul vecchio enum) — solo al passo 1 del wizard, mai su un test già esistente riaperto,
        // l'utente resta libero di deselezionare.
        foreach (var nome in template.StrumentiPreselezionati)
        {
            var opt = StrumentiOptions.FirstOrDefault(o => o.Valore.Equals(nome, StringComparison.OrdinalIgnoreCase));
            if (opt != null) opt.Selezionato = true;
        }

        // La prima reiterazione si apre da sola appena scelto il tipo test (Addendum redesign 2026-08, revisione
        // feedback QA): prima servivano due click separati ("Avvia prima esecuzione" poi "Aggiungi reiterazione")
        // prima di poter scrivere qualunque cosa.
        AggiungiIterazione();
        AggiungiCondizione();
        foreach (var nome in NomiStrumentiSelezionati().ToList()) AllineaOperazioniStrumentoSelezionato(nome);   // strumenti preselezionati: adottano l'operazione vuota
    }

    public ObservableCollection<string> RequisitiVisual { get; } = new();
    public ObservableCollection<PunteggioRequisitoRowViewModel> PunteggiRequisitiVisual { get; } = new();
    public ObservableCollection<string> FunzionalitaVisual { get; } = new();

    /// <summary>Voci valide per il selettore Funzionalita dentro una singola operazione. Oltre alle
    /// Funzionalita attualmente collegate al Test include quelle fotografate nelle operazioni di tutte le
    /// revisioni: una revisione storica puo legittimamente riferirsi a una Funzionalita rimossa in seguito
    /// dai dettagli correnti e deve continuare a mostrarla. Il valore viene calcolato direttamente dal
    /// modello persistito, quindi al primo render non dipende dall'ordine con cui le collezioni visuali
    /// osservabili vengono riempite dal costruttore.</summary>
    public IReadOnlyList<string> FunzionalitaOperazioniDisponibili => TestCase.Funzionalita
        .Concat(TestCase.Esecuzioni.SelectMany(e => e.Condizioni).Select(c => c.Funzionalita))
        .Where(f => !string.IsNullOrWhiteSpace(f))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public ObservableCollection<string> EcuVisual { get; } = new();
    public ObservableCollection<string> VeicoliSelezionatiVisual { get; } = new();
    public ObservableCollection<string> TestCorrelatiVisual { get; } = new();
    public ObservableCollection<CampoCustomRiga> CampiCustomVisual { get; } = new();
    public ObservableCollection<TicketRif> TicketCollegatiVisual { get; } = new();
    public Array StatiLavoro => Enum.GetValues(typeof(StatoLavoroTest));

    public Array AmbientiProva => Enum.GetValues(typeof(AmbienteProva));
    // "Da eseguire" (decisione utente 6.4.0, punto 1) sostituisce il legacy NonEseguitoNA come opzione
    // proposta per le nuove operazioni: unica scelta che nasce già selezionata e che NON conta come tentativo
    // svolto (Esito.EEseguito()) finché l'operatore non registra un vero esito.
    public Array EsitiTecnici => new[] { Esito.DaEseguire, Esito.Conforme, Esito.NonConforme, Esito.ConformeConDeviazioni, Esito.NonApplicabile };

    // Wrapper osservabili: TestCase è un semplice POCO (serializzato su disco), quindi le modifiche ai suoi
    // campi non notificano di per sé la UI. Questi wrapper tengono sincronizzati i campi che devono guidare
    // visibilità condizionale (sezione campi del template) o rilevamento candidati in tempo reale.
    [ObservableProperty] private TestTemplate? templateSelezionato;
    [ObservableProperty] private StatoLavoroTest statoLavoroSelezionato;
    [ObservableProperty] private string veicoloSelezionato = "";
    [ObservableProperty] private string? assegnatarioSelezionato;

    // AppState ricarica i master data ogni minuto. ItemsSource viene quindi sostituito con nuove istanze e,
    // mentre Avalonia riallinea le ComboBox, SelectedItem puo transitare brevemente da null/stringa vuota.
    // Senza questo gate i callback sottostanti copiavano quel valore transitorio nel TestCase, cancellando
    // Tipo test, componente o Veicolo mentre l'utente stava semplicemente leggendo/scorrendo il form.
    private bool _riallineamentoListeMaster;
    internal bool RiallineamentoListeMasterInCorso => _riallineamentoListeMaster;

    /// <summary>Severità del test (feedback utente 2026-08-20, punto 18): compilabile fin dalla pianificazione,
    /// non solo al momento di aprire una Non Conformità.</summary>
    [ObservableProperty] private SeveritaAnomalia? severitaSelezionata;
    partial void OnSeveritaSelezionataChanged(SeveritaAnomalia? value) { TestCase.Severita = value; ModificheNonSalvate = true; }
    public Array SeveritaDisponibili => Enum.GetValues(typeof(SeveritaAnomalia));

    /// <summary>Assegnabile solo da un Master (feedback utente 2026-08-20, punto 18): riusa PuoAssegnare, già
    /// lo stesso gate applicato ad Assegnatario/Scadenza per lo stesso motivo.</summary>
    [ObservableProperty] private PrioritaOperativa prioritaSelezionata = PrioritaOperativa.Normal;
    partial void OnPrioritaSelezionataChanged(PrioritaOperativa value) { TestCase.Priorita = value; ModificheNonSalvate = true; }
    public Array PrioritaDisponibili => Enum.GetValues(typeof(PrioritaOperativa));

    /// <summary>Classificazione completa dell'anomalia (Destinatario/Causa probabile/Stato dettaglio):
    /// esistevano già sul modello NonConformita ma non erano collegate a nessun controllo, in nessuna vista
    /// (feedback utente 2026-08-20, punto 5) — esposte qui accanto a StatoWorkflow/Severità/Priorità, così
    /// un'anomalia si classifica per intero senza uscire dal Test che l'ha generata.</summary>
    public Array DestinatariDisponibili => Enum.GetValues(typeof(DestinatarioSegnalazione));
    public Array CauseProbabiliDisponibili => Enum.GetValues(typeof(CausaProbabileUdt));
    public Array StatoDettaglioDisponibili => Enum.GetValues(typeof(StatoDettaglioNc));

    private void NotificaVisibilitaCampiSpecifici()
    {
        OnPropertyChanged(nameof(HaAzioniCollegate));
        OnPropertyChanged(nameof(MostraVincoloCollegamento));
        OnPropertyChanged(nameof(EtichettaVincoloCollegamento));
        OnPropertyChanged(nameof(TestVincoloDisponibili));
        OnPropertyChanged(nameof(TemplateCorrente));
        OnPropertyChanged(nameof(VeicoliDisponibili));
        OnPropertyChanged(nameof(MostraDtcOperazione));
        NotificaDtcOpzioni();
        OnPropertyChanged(nameof(FunzionalitaOperazioneObbligatoria));
        NotificaObbligatorieta();
        NotificaVisibilitaCampiTemplate();
    }

    partial void OnTemplateSelezionatoChanged(TestTemplate? value)
    {
        if (_riallineamentoListeMaster) return;
        if (value == null && TestCase.TestTemplateUid != Guid.Empty)
        {
            templateSelezionato = TemplateCorrente;
            OnPropertyChanged(nameof(TemplateSelezionato));
            return;
        }

        TestCase.TestTemplateUid = value?.Uid ?? Guid.Empty;
        OnPropertyChanged(nameof(TestCase));
        NotificaVisibilitaCampiSpecifici();
        ValutaCandidati();
        AggiornaSuggerimentoCronologia();
        ModificheNonSalvate = true;
    }

    /// <summary>L'obbligatorietà della matricola è già segnalata dall'asterisco (VinObbligatorio, guidato dal
    /// template/StatoLavoro tramite CampoTemplate.StatiObbligatorio) — l'etichetta resta un testo fisso
    /// invece di duplicare quel segnale con parole proprie: le due fonti potevano disallinearsi da quando i
    /// template sono diventati editabili dal Master, mostrando insieme "si sceglie più avanti" e l'asterisco
    /// sullo stesso campo (feedback utente 2026-08-21, punto 1: il testo era ridondante e, in quel caso,
    /// anche contraddittorio).</summary>
    public string VinLabelTesto => LocalizationService.Instance.Traduci("VinPredefinitoObbl");

    partial void OnStatoLavoroSelezionatoChanged(StatoLavoroTest value)
    {
        TestCase.StatoLavoro = value;
        // Un test pianificato non ha ancora nessuna REV, quindi matricola/Baseline non sono compilabili: portandolo subito
        // a uno stato di esecuzione (In corso / Da rivedere / Da ritestare / Concluso) si crea la prima REV come con
        // "Avvia prima esecuzione", altrimenti l'utente non puo' inserire la matricola obbligatorio e il Salva lo blocca
        // (segnalazione 2026-10-06). L'assegnatario resta quello scelto: cambiare stato non riassegna il test.
        if (InModifica && PuoModificareContenuto && Esecuzioni.Count == 0
            && value is StatoLavoroTest.InCorso or StatoLavoroTest.DaRivedere or StatoLavoroTest.DaRitestare or StatoLavoroTest.Concluso)
        {
            var assegnatario = AssegnatarioSelezionato;
            AggiungiIterazione();
            AssegnatarioSelezionato = assegnatario;
        }
        OnPropertyChanged(nameof(VinLabelTesto));
        NotificaObbligatorieta();
        ModificheNonSalvate = true;
    }

    partial void OnVeicoloSelezionatoChanged(string value)
    {
        if (_riallineamentoListeMaster) return;
        if (string.IsNullOrWhiteSpace(value))
        {
            if (!string.IsNullOrWhiteSpace(TestCase.Veicolo)) SetProperty(ref veicoloSelezionato, TestCase.Veicolo, nameof(VeicoloSelezionato));
            OnPropertyChanged(nameof(EcuDisponibili));
            OnPropertyChanged(nameof(FunzionalitaDisponibili));
            return;
        }
        if (!TestCase.Veicoli.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            TestCase.Veicoli.Add(value);
            VeicoliSelezionatiVisual.Add(value);
        }
        TestCase.NormalizzaVeicoli();
        if (string.IsNullOrWhiteSpace(TestCase.ContestoPredefinito.Veicolo)) TestCase.ContestoPredefinito.Veicolo = value;
        OnPropertyChanged(nameof(EcuDisponibili));
        OnPropertyChanged(nameof(FunzionalitaDisponibili));
        ValutaFuoriCategoria();
        ModificheNonSalvate = true;
    }

    partial void OnAssegnatarioSelezionatoChanged(string? value)
    {
        if (_riallineamentoListeMaster) return;
        TestCase.Assegnatario = value;
        ValutaFuoriCategoria();
        ModificheNonSalvate = true;
    }

    /// <summary>modalitaChangeRequest: true solo quando si apre il wizard dalla pagina Anomalie/Change Request
    /// per crearne una nuova (feedback utente 2026-08-21, punto 11) — per un test esistente viene comunque
    /// ricavata dal template già scelto, il parametro conta solo per un test nuovo dove non c'è ancora nulla
    /// da cui dedurla.</summary>
    public TestCaseEditorViewModel(AppState state, string? testCaseId, Action tornaAllaLista, Action<string?> navigaATest, bool modalitaChangeRequest = false,
        Action<string>? navigaATestInModifica = null, Func<Task>? richiediModificaEsterna = null,
        Guid? segnalazioneDaEvidenziareUid = null)
    {
        _state = state;
        // Versione completa di default (feedback utente 2026-11-02): la rapida e' una scelta esplicita dell'operatore,
        // ricordata per operatore; senza preferenza salvata si parte dalla completa.
        _modoEditorRapido = string.Equals(
            ViewStateService.Leggi(state, ChiavePreferenzaModalitaEditor), "quick",
            StringComparison.OrdinalIgnoreCase);
        CaricaPreferenzaTuttiICampi();
        _tornaAllaLista = tornaAllaLista;
        _navigaATest = navigaATest;
        _navigaATestInModifica = navigaATestInModifica;
        _richiediModificaEsterna = richiediModificaEsterna;
        _segnalazioneDaEvidenziareUid = segnalazioneDaEvidenziareUid;
        // Apertura di un Test: RicaricaListeMaster()+CaricaStrumenti() interrogano 7 cataloghi Phase D distinti
        // in sequenza (Componente, Veicoli, TestTemplate, Operatori, Requisiti, Funzionalita, Strumenti). Senza uno
        // scope accorpato (vedi MasterLetturaScope, gia' usato da Home/Report/Audit/Anomalie-CR) ognuno di
        // questi enumera di nuovo la cartella journal condivisa e riscrive le meta SQLite anche a contenuto
        // invariato: 7 scansioni I/O invece di una sola per aprire un singolo Test (causa condivisa individuata
        // in fase di profilazione 6.4.0, stesso meccanismo di dedup gia' verificato su Home/Report).
        using var letturaMaster = MasterLetturaScope.Apri();
        RicaricaListeMaster();
        _state.DatiRicaricati += OnDatiRicaricati;

        // Etichette dei campi di ruolo (Addendum: grill-me 2026-08-18, follow-up): seguono l'Etichetta
        // rinominata dal Master nel catalogo campi di sistema, fallback al testo storico se non rinominata.
        // Calcolate una volta alla costruzione, come TitoloPagina/altri testi statici di questo VM — non si
        // riallineano da sole se la lingua cambia mentre l'editor è aperto, stesso limite già presente per
        // ogni altro testo VM-side di questa classe (solo i binding diretti a LocalizationService.Instance
        // in XAML sono reattivi al cambio lingua a caldo).
        _catalogoCampiSistema = state.CatalogoCampiSistemaCompleto();
        var loc = LocalizationService.Instance;
        EtichettaStatoLavoro = RuoloCampoSistemaLookup.EtichettaLocalizzataDelRuolo(_catalogoCampiSistema, RuoloCampoSistema.StatoLavoro, loc.Traduci("StatoDelLavoroLabel"));
        EtichettaScadenza = RuoloCampoSistemaLookup.EtichettaLocalizzataDelRuolo(_catalogoCampiSistema, RuoloCampoSistema.Scadenza, loc.Traduci("ScadenzaOperativaLabel"));
        EtichettaAssegnatario = RuoloCampoSistemaLookup.EtichettaLocalizzataDelRuolo(_catalogoCampiSistema, RuoloCampoSistema.Assegnatario, loc.Traduci("AssegnatoAObbl"));
        EtichettaAssegnatarioReadOnly = RuoloCampoSistemaLookup.EtichettaLocalizzataDelRuolo(_catalogoCampiSistema, RuoloCampoSistema.Assegnatario, loc.Traduci("AssegnatoALabel"));
        EtichettaFunzionalita = RuoloCampoSistemaLookup.EtichettaLocalizzataDelRuolo(_catalogoCampiSistema, RuoloCampoSistema.Funzionalita, loc.Traduci("FunzionalitaCollegateLabel"));
        EtichettaEcu = RuoloCampoSistemaLookup.EtichettaLocalizzataDelRuolo(_catalogoCampiSistema, RuoloCampoSistema.Componente, loc.Traduci("EcuObbl"));
        EtichettaVeicolo = RuoloCampoSistemaLookup.EtichettaLocalizzataDelRuolo(_catalogoCampiSistema, RuoloCampoSistema.Veicolo, loc.Traduci("VeicoloObbl"));
        EtichettaStrumentiUsati = RuoloCampoSistemaLookup.EtichettaLocalizzataDelRuolo(_catalogoCampiSistema, RuoloCampoSistema.StrumentiUsati, loc.Traduci("StrumentiUsatiTitolo"));

        IsNew = testCaseId == null;
        if (IsNew)
        {
            // Se arriviamo da "Duplica come nuovo test" (Addendum §19) la bozza è già pronta: si salta il
            // passo 1 del wizard perché il Tipo test è già deciso, esattamente come per un test esistente.
            if (state.BozzaPerNuovoTest != null)
            {
                TestCase = state.BozzaPerNuovoTest;
                state.BozzaPerNuovoTest = null;
                tipoTestScelto = true;
                // Assegnato al campo, non alla proprietà (nessun OnTipoTestSceltoChanged qui, vedi commento
                // sopra su tipoTestScelto): l'autosave va avviata esplicitamente per questo ramo — copre sia
                // "Duplica come nuovo test" sia una bozza ripristinata da BozzaAutosaveService (Addendum: audit
                // UX 2026-08-26, nota minore 1), entrambi partono già con TipoTestScelto=true.
                AvviaAutosave();
            }
            else
            {
                TestCase = new TestCase { DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now, ModificatoDa = state.Config.OperatoreAttivo, CreatoDa = state.Config.OperatoreAttivo };
                tipoTestScelto = false; // passo 1 del wizard: il tipo va scelto esplicitamente prima di vedere il resto del form
            }
            InModifica = true; // un test nuovo è già "in costruzione", nessun lock da acquisire finché non esiste su disco
        }
        else
        {
            var caricato = state.TestCases.GetById(testCaseId!);
            TestCase = caricato != null ? Clona(caricato) : new TestCase();
            _snapshot = Clona(TestCase);
            _salvatoSuDisco = true;
            AggiornaAvvisi();
            AggiornaStatoLock();
            tipoTestScelto = true; // tipo già deciso in precedenza, il wizard si applica solo alla creazione
        }
        _modalitaChangeRequest = !IsNew
            ? (_tuttiITemplate.FirstOrDefault(t => t.Uid == TestCase.TestTemplateUid)?.IsChangeRequest ?? modalitaChangeRequest)
            : modalitaChangeRequest;

        CaricaStrumenti();

        // Va popolata prima di esporre le Condizioni alla view. Con SelectedItem bidirezionale, creare i
        // ComboBox delle operazioni quando il relativo ItemsSource e ancora vuoto puo lasciare a video una
        // selezione bianca (e alcune versioni di Avalonia possono anche propagare il null al modello), pur
        // avendo Condizione.Funzionalita correttamente salvata su disco.
        foreach (var f in TestCase.Funzionalita) FunzionalitaVisual.Add(f);

        // Popolamento iniziale SENZA l'handler agganciato (feedback utente 2026-09-17, volumi 100k): un test
        // con molte revisioni storiche innescava l'intero handler (9 notifiche + SincronizzaCondizioniVisual)
        // una volta per OGNI Esecuzioni.Add(), invece che una sola volta a fine popolamento — il costo di
        // aprire un singolo test scalava con quante revisioni ha, non con quanto lavoro serve davvero.
        foreach (var e in TestCase.Esecuzioni) Esecuzioni.Add(e);
        Esecuzioni.CollectionChanged += (_, _) => { if (!_sostituzioneEsecuzioniInCorso) SincronizzaDaCambioEsecuzioni(); };
        SincronizzaDaCambioEsecuzioni(); // stessa sincronizzazione di prima, una sola volta, sui dati finali
        AggiornaCampiStrumenti(); // ricalcola ora che Esecuzioni/Condizioni esistenti sono caricate
        NotificaBloccoAggiungiDtc(); // componente proponibili e componente preselezionata del blocco "Aggiungi guasto"
        if (IsNew)
        {
            TestCase.Assegnatario ??= state.Config.OperatoreAttivo; // chi crea il test ne è responsabile finché non lo riassegna un Master o lo esegue qualcun altro
            TestCase.DataAssegnazione ??= DateTime.Now;
        }
        if (string.IsNullOrWhiteSpace(TestCase.ContestoPredefinito.Componente)) TestCase.ContestoPredefinito.Componente = TestCase.Componente;
        if (string.IsNullOrWhiteSpace(TestCase.ContestoPredefinito.Veicolo)) TestCase.ContestoPredefinito.Veicolo = TestCase.Veicolo;

        foreach (var a in TestCase.Allegati) AllegatiVisual.Add(a);
        // TIME (6.6.1): da qui le operazioni reagiscono a componente, Funzionalita e tipo guasto (regola di copia). Un test nuovo o duplicato riceve
        // la copia per le operazioni senza durata; un test esistente no: i valori salvati non si riscrivono da soli.
        _copiaDurataAttiva = true;
        foreach (var condizione in (RevisioneCorrente?.Condizioni ?? Enumerable.Empty<Condizione>()).ToList())
        {
            CollegaCopiaDurata(condizione);
            if (IsNew) AvviaCopiaDurataOperazione(condizione);
        }
        AggiornaAvvisoPoliticaDurata();
        AggiornaEvidenzeOperazioni();

        templateSelezionato = TemplateCorrente;
        statoLavoroSelezionato = TestCase.StatoLavoro;
        veicoloSelezionato = TestCase.Veicolo;
        assegnatarioSelezionato = TestCase.Assegnatario;
        severitaSelezionata = TestCase.Severita;
        prioritaSelezionata = TestCase.Priorita;

        // L'ordinamento per Id e' memoizzato nel repository e invalidato dalle scritture (feedback utente
        // 2026-09-17, volumi 100k): qui resta solo il filtro, che su 104k Test costa ~30ms invece dei ~220ms
        // che costava riordinare l'intero archivio ad ogni apertura di un Test.
        TestCorrelatiDisponibili = state.CartellaValida
            ? state.TestCases.GetAllOrdinatiPerId().Where(t => t.Id != TestCase.Id).ToList()
            : new();
        testVincoloSelezionato = string.IsNullOrEmpty(TestCase.TestCollegatoRichiestoId)
            ? null
            : TestCorrelatiDisponibili.FirstOrDefault(t => t.Id == TestCase.TestCollegatoRichiestoId);

        foreach (var r in TestCase.RequisitoId) RequisitiVisual.Add(r);
        foreach (var e in TestCase.Componente) EcuVisual.Add(e);
        TestCase.NormalizzaVeicoli();
        foreach (var v in TestCase.Veicoli) VeicoliSelezionatiVisual.Add(v);
        foreach (var c in TestCase.TestCorrelati) TestCorrelatiVisual.Add(c);
        foreach (var kv in TestCase.CampiCustom) CampiCustomVisual.Add(new CampoCustomRiga { Chiave = kv.Key, Valore = kv.Value });
        foreach (var t in TestCase.TicketCollegati) TicketCollegatiVisual.Add(t);

        // templateSelezionato è assegnato direttamente al campo backing qui sopra (non passa per la property
        // pubblica), quindi il partial OnTemplateSelezionatoChanged non scatta: senza questa chiamata esplicita
        // le collezioni restano quelle (vuote) calcolate troppo presto durante Esecuzioni.CollectionChanged.
        AggiornaCampiCustomTemplate();
        _inCostruzione = false;
        // creaMancanti:true riproduce esattamente l'effetto della chiamata di costruzione saltata sopra
        // (stesso flag che SincronizzaEsitoCondizioni le passava): senza, riaprire un test con un esito Non
        // Conforme/Conforme con deviazioni non ancora classificato smetteva di generare da solo la
        // segnalazione mancante (10 test falliti quando questo è stato scoperto e corretto).
        CaricaPannelloAnomalie(creaMancanti: true);
        _ = AggiornaStatoDtcOperazioniAsync();
        CompletaCostruzioneStateSafety();
        AggiornaStatoEstaLocale();
    }

    private CancellationTokenSource? _statoDtcCts;

    /// <summary>Riepilogo guasto nelle operazioni (sola lettura, leggero): per ogni operazione con guasto mostra lo stato
    /// corrente del guasto e l'avanzamento N/M del requisito Extra collegato. Calcolo FUORI dal thread UI
    /// (DtcAvanzamentoProvider: SQLite in streaming limitato al requisito quando ne e coinvolto uno solo).</summary>
    private async Task AggiornaStatoDtcOperazioniAsync()
    {
        _statoDtcCts?.Cancel();
        var conDtc = TestCase.Esecuzioni.SelectMany(e => e.Condizioni).Where(c => c.RiguardaDtc).ToList();
        // Avvisi SINCRONI (nessun calcolo): requisito non piu collegato al test, operazione guasto senza componente in test multi-componente.
        var loc = LocalizationService.Instance;
        foreach (var c in conDtc)
        {
            var ecuOp = OperationalSemantics.EcuUnits(TestCase, c);
            c.DtcAvviso = !string.IsNullOrEmpty(c.DtcRequisitoId) && !TestCase.RequisitoId.Contains(c.DtcRequisitoId, StringComparer.OrdinalIgnoreCase)
                ? string.Format(loc.Traduci("DtcAvvisoRequisitoScollegatoFmt"), c.DtcRequisitoId)
                : ecuOp.Count == 0 ? (string.IsNullOrEmpty(c.DtcRequisitoId) && DtcOperazioneRegole.AbbinaConEsito(
                        RequisitiDisponibili.Where(r => TestCase.RequisitoId.Contains(r.Id)), c.Guasto!, ecuOp, VociDtc).Ambiguo
                    ? string.Format(loc.Traduci("DtcAvvisoAmbiguoFmt"), c.Guasto) : loc.Traduci("DtcAvvisoSenzaEcu"))
                : !string.IsNullOrWhiteSpace(c.Funzionalita) ? loc.Traduci("DtcAvvisoFunzionalitaIgnorata") : null;
        }
        // guasto liberi (senza requisito Extra): validi e salvati nel test, ma fuori da Audit/copertura guasto e senza alcuna segnalazione.
        foreach (var c in conDtc.Where(x => string.IsNullOrEmpty(x.DtcRequisitoId)))
            c.DtcStatoTesto = null;
        foreach (var c in conDtc.Where(x => string.IsNullOrEmpty(x.DtcAvviso) && UsaStrumentoDtcInjection
                     && x.ValoriStrumenti.TryGetValue(DtcOperazioneRegole.ChiaveAtteso, out var at) && !string.IsNullOrWhiteSpace(at)
                     && !at.Trim().Equals(x.Guasto!.Trim(), StringComparison.OrdinalIgnoreCase)))
            c.DtcAvviso = string.Format(loc.Traduci("DtcAvvisoAttesoDiversoFmt"), c.Guasto);
        if (!MostraDtcOperazione || !_state.CartellaValida || RevisioneCorrente == null || conDtc.Count == 0) return;
        var reqIds = conDtc.Select(c => c.DtcRequisitoId ?? "").Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var reqs = RisolviConteggiDtc().Where(r => reqIds.Contains(r.Id, StringComparer.OrdinalIgnoreCase) && r.DtcTotale > 0).ToList();
        if (reqs.Count == 0) return;
        var cts = _statoDtcCts = new CancellationTokenSource();
        var testSnapshot = TestCase;
        try
        {
            var risultato = await Task.Run(() => DtcAvanzamentoProvider.Calcola(_state, reqs, reqs.Count == 1 ? reqs[0].Id : null, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(testSnapshot, TestCase)) return;
            var perCella = risultato.PerDtc.ToDictionary(a => a.Chiave, StringComparer.Ordinal);
            var perTripla = risultato.PerDtc.GroupBy(a => DtcAvanzamentoService.ChiaveCella(a.RequisitoId, a.Guasto, a.Componente), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var perReq = risultato.PerRequisito.ToDictionary(r => r.RequisitoId, StringComparer.OrdinalIgnoreCase);
            foreach (var c in conDtc)
            {
                // Stato per OGNUNA dei componenti dell'operazione (non solo la prima).
                var componenti = OperationalSemantics.EcuUnits(TestCase, c);
                var parti = componenti.Count == 0 ? new List<string>()
                    : componenti.Select(componente =>
                    {
                        // Stessa regola di abbinamento di DtcAvanzamentoService.Calcola: sottoveicolo esatto, poi voce senza sottoveicolo,
                        // poi (operazione senza sottoveicolo) l'unica cella con quel requisito/componente/guasto.
                        var sv = DtcAvanzamentoService.VeicoloOperazione(TestCase, c);
                        var a = perCella.GetValueOrDefault(DtcAvanzamentoService.ChiaveCella(c.DtcRequisitoId ?? "", c.Guasto ?? "", componente, sv));
                        if (a == null && sv.Length > 0) a = perCella.GetValueOrDefault(DtcAvanzamentoService.ChiaveCella(c.DtcRequisitoId ?? "", c.Guasto ?? "", componente));
                        if (a == null && sv.Length == 0 && perTripla.TryGetValue(DtcAvanzamentoService.ChiaveCella(c.DtcRequisitoId ?? "", c.Guasto ?? "", componente), out var cand) && cand.Count == 1) a = cand[0];
                        var st = a != null ? DtcAvanzamentoProvider.StatoEtichetta(a) : loc.Traduci("DtcStatoDaFare");
                        return componenti.Count > 1 ? $"{componente}: {st}" : st;
                    }).ToList();
                var req = perReq.TryGetValue(c.DtcRequisitoId ?? "", out var r) ? $" · {c.DtcRequisitoId} {r.Completi}/{r.Totale}" : "";
                c.DtcStatoTesto = string.Join(" · ", parti) + req;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CrashLogService.Registra("Stato guasto operazioni", ex); }
    }

    /// <summary>Ricarica le liste master data usate dai menu a tendina di questo editor. Chiamata sia dal
    /// costruttore sia da OnDatiRicaricati, cosi una nuova voce aggiunta da un'altra pagina (es. Impostazioni
    /// → Operatori/Funzionalità) compare subito anche in un test già aperto, senza dover salvare, navigare
    /// altrove e tornare indietro per far ricostruire la VM da zero.</summary>
    private void RicaricaListeMaster()
    {
        _tutteLeEcu = _state.CartellaValida ? _state.Master.Componente.Load().Where(e => !e.Archiviata).ToList() : new();
        _tuttiIVeicoli = _state.CartellaValida ? _state.Master.Veicoli.Load() : new();
        _tuttiITemplate = _state.CartellaValida ? _state.Master.TestTemplate.Load() : new();
        OperatoriDisponibili = _state.CartellaValida ? _state.Master.Operatori.Load().Where(o => !o.Disattivo).Select(o => o.Nome).ToList() : new();
        RequisitiDisponibili = _state.CartellaValida ? _state.Master.Requisiti.Load().Where(r => !r.Archiviata).OrderBy(r => r.Id).ToList() : new();
        _dtcOpzioniCache = null; _haRequisitoD4dConDtc = null; _dtcConteggioCache = null; _dtcInsCache = null;
        _tutteLeFunzionalita = _state.CartellaValida ? _state.Master.Funzionalita.Load() : new();
    }

    /// <summary>Rilegge il catalogo campi di sistema (opzioni, tipo ed etichetta dei campi custom) dopo un salvataggio del
    /// catalogo o una ricarica dati. Senza, un Test già aperto mostrerebbe per sempre le opzioni di quando è stato aperto.
    /// I campi custom si ricostruiscono solo se il catalogo è davvero cambiato, così una ricarica senza modifiche non tocca
    /// un valore che l'operatore sta compilando (il valore resta comunque in TestCase.CampiCustom).</summary>
    private void AggiornaCatalogoCampiSistema()
    {
        var nuovo = _state.CatalogoCampiSistemaCompleto();
        if (JsonSerializer.Serialize(nuovo, JsonIo.CompactOptions) == JsonSerializer.Serialize(_catalogoCampiSistema, JsonIo.CompactOptions)) return;
        _catalogoCampiSistema = nuovo;
        AggiornaCampiCustomTemplate();
    }

    private void OnDatiRicaricati()
    {
        _riallineamentoListeMaster = true;
        try
        {
            using var letturaMaster = MasterLetturaScope.Apri();
            RicaricaListeMaster();
            AggiornaCatalogoCampiSistema();
            OnPropertyChanged(nameof(OperatoriDisponibili));
            OnPropertyChanged(nameof(RequisitiDisponibili));
            OnPropertyChanged(nameof(FunzionalitaDisponibili));
            OnPropertyChanged(nameof(EcuDisponibili));
            OnPropertyChanged(nameof(VeicoliDisponibili));
            OnPropertyChanged(nameof(TemplatesDisponibili));
            OnPropertyChanged(nameof(TemplateCorrente));
            AggiornaStatoEstaLocale();

            // Ripristina i wrapper dalle proprieta persistenti DOPO aver sostituito gli ItemsSource. Assegnare
            // i backing field evita di marcare il test come modificato e impedisce ai valori transitori emessi
            // dalla UI durante il refresh di diventare dati reali.
            TemplateSelezionato = TemplateCorrente;
            VeicoloSelezionato = TestCase.Veicolo;
            AssegnatarioSelezionato = TestCase.Assegnatario;
            // Riallinea la ComboBox Operatore (il cui writeback e' stato ignorato) al valore persistente.
            OnPropertyChanged(nameof(RevisioneCorrenteOperatore));
        }
        finally
        {
            _riallineamentoListeMaster = false;
        }
    }

    /// <summary>Rilascia il lock se l'istanza viene scartata mentre è ancora in modifica senza passare da
    /// Salva/Annulla (es. abbandono modifiche dalla barra laterale, che smaltisce la pagina uscente con
    /// ImpostaCurrentPage → Dispose): senza questo il file .lock restava orfano fino al timeout di 20 minuti
    /// (bug scoperto nel 2026-08-21 investigando la finestra di visualizzazione dei test correlati). Rilasciare
    /// qui è sempre sicuro anche quando il lock è già stato rilasciato esplicitamente da Salva/Annulla — Release
    /// è già idempotente (LockService.cs) — e non richiede conferma: chi ha navigato via ha già confermato
    /// l'abbandono modifiche prima che Dispose venisse chiamato.</summary>
    public void Dispose()
    {
        bool acquisizioneInCorso;
        string? percorsoSessione;
        string? sessioneLock;
        string? percorsoLockCatalogo;
        string? sessioneLockCatalogo;
        lock (_statoAcquisizioneLockGate)
        {
            _disposto = true; // una IniziaModifica ancora in attesa del lock lo rilascia invece di tenerlo
            acquisizioneInCorso = _acquisizioneLockInCorso;
            percorsoSessione = _percorsoRilascioSessioneLockInAttesa ?? _percorsoLockSessioneAcquisita;
            sessioneLock = _sessioneRilascioLockInAttesa ?? _sessioneLockAcquisita;
            percorsoLockCatalogo = _percorsoRilascioLockCatalogoInAttesa;
            sessioneLockCatalogo = _sessioneRilascioLockCatalogoInAttesa;
            _percorsoRilascioLockCatalogoInAttesa = null;
            _sessioneRilascioLockCatalogoInAttesa = null;
            RilascioLockCatalogoInAttesa = false;
            if (!acquisizioneInCorso && !string.IsNullOrWhiteSpace(percorsoSessione) && !string.IsNullOrWhiteSpace(sessioneLock))
            {
                // La proprietà del retry passa al coordinatore; Dispose ripetuti non devono rilasciare una sessione nuova.
                _percorsoRilascioSessioneLockInAttesa = null;
                _sessioneRilascioLockInAttesa = null;
                _percorsoLockSessioneAcquisita = null;
                _sessioneLockAcquisita = null;
            }
        }
        _state.DatiRicaricati -= OnDatiRicaricati;
        foreach (var osservata in _condizioniOsservate) osservata.PropertyChanged -= OnCondizioneCambiata;
        _condizioniOsservate.Clear();
        var recuperoInCorso = _existingDraftRecoveryTaskInCorso;
        if (!acquisizioneInCorso && !string.IsNullOrWhiteSpace(percorsoSessione) && !string.IsNullOrWhiteSpace(sessioneLock))
        {
            // Il coordinatore statico mantiene il gate per tutta la cleanup; il finally del recovery condivide
            // lo stesso task exact-session invece di lasciare il lock occupato mentre il dialogo termina.
            FermaHeartbeat();
            RilascioLockDopoDisposePerTest = LockGate.RilasciaSessioneEsattaConRetryAsync(
                _state.Locks, percorsoSessione, sessioneLock);
        }
        else if (acquisizioneInCorso || recuperoInCorso)
            FermaHeartbeat(); // l'acquisizione ancora sotto gate o il finally del recovery gestiscono il rilascio esatto.
        else
        {
            if (InModifica || RilascioLockInAttesa)
            {
                FermaHeartbeat();
                CrashLogService.Registra("Dispose editor test senza SessionId esatto", new InvalidOperationException(PercorsoFile ?? TestCase.Id));
            }
        }
        if (!string.IsNullOrWhiteSpace(percorsoLockCatalogo) && !string.IsNullOrWhiteSpace(sessioneLockCatalogo))
        {
            var rilascioCatalogo = AvviaRilascioSessioneEsatta(percorsoLockCatalogo, sessioneLockCatalogo);
            RilascioLockDopoDisposePerTest = RilascioLockDopoDisposePerTest is { } rilascioTest
                ? Task.WhenAll(rilascioTest, rilascioCatalogo)
                : rilascioCatalogo;
        }
        FermaAutosaveModificaEsistente();
        FermaAutosave(); // indipendente dal lock: un test nuovo non ne acquisisce mai uno (PercorsoFile è sempre null qui)

        // La bozza va eliminata qui, non solo la fermata del timer sopra (Addendum: audit UX 2026-08-26, nota
        // minore 1): Dispose() viene chiamato SOLO da un'uscita concorde — salvataggio riuscito (già scritto
        // sul vero file, la copia locale è obsoleta) o abbandono con conferma esplicita dell'utente (stesso
        // gate ConfirmService.ConfermaAbbandonoModifiche usato ovunque nell'app per lasciare un editor
        // sporco). Un crash o una chiusura brusca del processo NON passano da qui: la bozza resta sul disco
        // esattamente per quel caso, il solo che questa funzionalità deve coprire.
        if (IsNew) _state.BozzaAutosave.Elimina(_state.Config.OperatoreAttivo);
    }

    private IReadOnlyList<NonConformita> TutteLeAnomalieCorrenti() => _state.NonConformita.GetAll()
        .Concat(_anomalieNuove).DistinctBy(n => n.Uid).Where(n => !_anomalieDaEliminare.Contains(n.Uid)).ToList();

    private Dictionary<string, TestCase> RisolviTestNecessari(IEnumerable<NonConformita> anomalie)
    {
        var result = new Dictionary<string, TestCase>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(TestCase.Id)) result[TestCase.Id] = TestCase;
        void Aggiungi(Guid? uid, string codice)
        {
            if (!uid.HasValue && string.IsNullOrWhiteSpace(codice)) return;
            var correnteEditor = uid.HasValue ? uid == TestCase.Uid : string.Equals(codice, TestCase.Id, StringComparison.OrdinalIgnoreCase);
            var tc = correnteEditor ? TestCase : uid.HasValue ? _state.TestCases.GetByUid(uid.Value) : _state.TestCases.GetById(codice);
            if (tc != null) result[tc.Id] = tc;
        }
        foreach (var nc in anomalie)
        {
            Aggiungi(nc.TestUid, nc.TestCaseId);
            foreach (var p in nc.ProveCollegate)
            {
                var effective = AnomaliaWorkflowService.ProvaConIdentita(nc, p);
                Aggiungi(effective.TestUid, effective.TestId);
            }
        }
        return result;
    }

    /// <summary>Ricalcola anomalie e segnalazioni del Test usando soltanto l'ultimo tentativo di ogni catena.
    /// Se creaMancanti e true, materializza in memoria la segnalazione per ogni problema ancora effettivo;
    /// la persistenza avviene insieme al Test, non durante la digitazione.</summary>
    private void CaricaPannelloAnomalie(bool creaMancanti = false)
    {
        if (_inCostruzione) return; // vedi commento su _inCostruzione: la chiamata finale del costruttore rifà comunque tutto
        RevisioneCorrente?.AggiornaDevalidazioniNascoste();   // regola guasto: devalidazione solo dopo validazione conforme

        // Il pannello si ricostruisce da zero a ogni chiamata (anche solo per un cambio di selezione altrove
        // nella pagina, es. l'Esito di una condizione: OnCampoSelezioneModificato in code-behind reagisce a
        // QUALUNQUE SelectingItemsControl del form e richiama SincronizzaEsitoCondizioni -> questo metodo).
        // Senza questa cache, scegliere la Severità o digitare un riferimento TICKET veniva cancellato dalla
        // ricostruzione scatenata dalla stessa selezione appena fatta, prima ancora di poterla usare (feedback
        // utente 2026-08-20, punto 4: "non posso inserire la severità", "clicco nuova anomalia e non fa
        // niente" — la severità scelta spariva subito, quindi il comando falliva la validazione in silenzio).
        var segnalazioniPrecedenti = SegnalazioniTest.ToDictionary(r => r.Segnalazione.Uid);
        var ncPrecedenti = AnomalieTestAperte.Concat(AnomalieTestChiuse).ToDictionary(r => r.Nc.Uid);

        AnomalieTestAperte.Clear();
        AnomalieTestChiuse.Clear();
        foreach (var osservata in _condizioniOsservate) osservata.PropertyChanged -= OnCondizioneCambiata;
        _condizioniOsservate.Clear();
        SegnalazioniTest.Clear();

        // L'archivio completo serve solo alle "anomalie candidate" piu' sotto, che a loro volta servono solo
        // se questo test ha segnalazioni da classificare: qui viene quindi materializzato pigramente
        // (feedback utente 2026-09-17, volumi 100k: GetAll() clona ~100k NC e costa ~540ms, GetByTestIds ne
        // filtra una manciata prima di clonarle e costa ~48ms).
        // PeekAllReadOnly() invece di TutteLeAnomalieCorrenti()/GetAll() (Addendum prestazioni 2026-09-20):
        // "candidate" più sotto è puro materiale di visualizzazione per il picker (Id/Descrizione/Owner/Data,
        // vedi ScegliAnomalia e il ComboBox in TestCaseEditorView.axaml) e non viene mai mutato qui — l'unica
        // NC che può essere mutata è quella che l'operatore sceglie davvero, ed è clonata esplicitamente al
        // momento della scelta in CollegaSegnalazioneAdAnomalia, esattamente come richiesto dal contratto di
        // PeekAllReadOnly (mai mutare né trattenere oltre l'uso sincrono). _anomalieNuove restano invece gli
        // oggetti live di sempre (creati in questa sessione, non ancora nella cache del repository).
        IReadOnlyList<NonConformita>? tutteAnomalieCache = null;
        // Con uno scope di working-copy attivo (ripristino del form dopo un rollback in SalvaSicuro) il Peek
        // esporrebbe le working-copy interne dello scope, che qui verrebbero trattenute dalla UI: in quel caso
        // raro si torna al percorso che clona sempre (review 2026-09-25).
        IReadOnlyList<NonConformita> TutteAnomalie() => tutteAnomalieCache ??= (WorkflowWorkingCopyScope.Attivo
                ? _state.NonConformita.GetAll()
                : _state.NonConformita.PeekAllReadOnly())
            .Concat(_anomalieNuove).DistinctBy(n => n.Uid).Where(n => !_anomalieDaEliminare.Contains(n.Uid)).ToList();

        // GetByTestIds confronta gli Id senza distinguere maiuscole/minuscole, quindi restituisce un
        // soprainsieme di cio' che PerTest (confronto esatto) seleziona: PerTest resta applicato sopra e il
        // risultato e' identico a quello che si otteneva filtrando l'intero archivio.
        // Le copie collegate in questa sessione precedono la lettura del repository: DistinctBy tiene la prima occorrenza,
        // quindi la NC mostrata nel pannello e' quella con la prova appena aggiunta, non la versione ancora sul disco.
        var collegate = _anomalieCollegate.Values.Where(n => !_anomalieDaEliminare.Contains(n.Uid));
        var anomalieDelTest = !string.IsNullOrWhiteSpace(TestCase.Id)
            ? AnomaliaWorkflowService.PerTest(TestCase.Id, collegate.Concat(_state.NonConformita.GetByTestIds(new[] { TestCase.Id }))
                .Concat(_anomalieNuove).DistinctBy(n => n.Uid).Where(n => !_anomalieDaEliminare.Contains(n.Uid)), _ => TestCase)
            : collegate.Concat(_anomalieNuove).DistinctBy(n => n.Uid).ToList();
        if (anomalieDelTest.Count > 0)
        {
            // Dizionario costruito una sola volta per l'intero pannello (feedback utente 2026-09-17, volumi
            // 100k): prima ogni anomalia collegata a QUESTO test ricostruiva da zero un Dictionary/HashSet su
            // tutti i Test esistenti (via .Append(TestCase).DistinctBy + il ToDictionary interno ai due
            // metodi sotto) — con 100k Test il costo di aprire un singolo Test diventava multi secondo, e
            // scalava con quante anomalie sono collegate, non con quanto lavoro serve davvero. Stesso schema
            // già corretto altrove (NonConformitaViewModel.Ricarica) per lo stesso motivo. TryAdd riproduce
            // esattamente la semantica precedente di Append(TestCase).DistinctBy(t => t.Id) (mantiene la prima
            // occorrenza, cioè il TestCase persistito da GetAll() se lo stesso Id esiste già).
            var testPerId = RisolviTestNecessari(anomalieDelTest);

            // Ordine stabile anche per anomalie create nello stesso istante (feedback utente 2026-08-21,
            // punto 8/2: senza un secondo criterio deterministico, l'ordine di enumerazione del dizionario
            // sottostante (NonConformitaRepository) poteva cambiare tra una ricostruzione e l'altra dello
            // stesso pannello, facendo "saltare" righe/valori senza che l'utente avesse toccato nulla.
            foreach (var nc in anomalieDelTest.OrderByDescending(n => n.DataApertura).ThenByDescending(n => n.Id))
            {
                var testCorrente = AnomaliaWorkflowService.TestCorrente(nc, testPerId);
                var motivi = AnomaliaWorkflowService.MotiviPerCuiRestaAperta(nc, testPerId);
                var ncPrecedente = ncPrecedenti.GetValueOrDefault(nc.Uid);
                // TutteLeAnomalieCorrenti()/_state.NonConformita.GetAll() non garantisce lo stesso riferimento
                // tra una ricostruzione e l'altra del pannello: senza questo riporto, il motivo di chiusura che
                // l'operatore ha appena digitato (ancora non persistito) sparirebbe alla prima ricostruzione
                // successiva — compreso quella che Salva() esegue su se stesso subito prima di validare
                // (feedback: "scrivo il motivo di chiusura, premo Salva, mi richiede di nuovo il motivo").
                if (string.IsNullOrWhiteSpace(nc.MotivoChiusura) && !string.IsNullOrWhiteSpace(ncPrecedente?.Nc.MotivoChiusura))
                    nc.MotivoChiusura = ncPrecedente.Nc.MotivoChiusura;
                // Stesso motivo del riporto MotivoChiusura sopra: Salva() puo' ricostruire questo pannello PIU'
                // VOLTE nella stessa chiamata (es. creare una nuova revisione quando i Dettagli cambiano
                // riassegna Esecuzioni, il cui CollectionChanged rifà scattare SincronizzaCondizioniVisual ->
                // CaricaPannelloAnomalie) DOPO che la chiusura automatica per conformità
                // (AnomalieChiudibiliDagliEsiti/NonConformitaRulesService.Chiudi) ha già mutato l'oggetto nc di
                // QUESTA sessione. La fresh clone di TutteLeAnomalieCorrenti() riflette ancora lo stato
                // persistito su disco (non ancora Conclusa): senza questo riporto la chiusura in sessione,
                // corretta un istante prima, sparirebbe silenziosamente dal pannello prima ancora di essere
                // salvata (l'anomalia risultava "Aperta" dopo un Salva riuscito senza errori).
                if (ncPrecedente != null && ncPrecedente.Nc.StatoWorkflow == StatoAnomalia.Conclusa && nc.StatoWorkflow != StatoAnomalia.Conclusa)
                {
                    nc.StatoWorkflow = ncPrecedente.Nc.StatoWorkflow;
                    nc.DataChiusura = ncPrecedente.Nc.DataChiusura;
                }
                var riga = new NcRiga
                {
                    Nc = nc,
                    TestCase = testCorrente,
                    TestCaseIdVisualizzato = testCorrente?.Id ?? nc.TestCaseId,
                    TestCaseEliminato = testCorrente?.Eliminato ?? true,
                    MotiviAperturaTesto = string.Join(Environment.NewLine, motivi),
                    NuovoTicketRiferimento = ncPrecedente?.NuovoTicketRiferimento ?? "",
                    NuovoTicketUrl = ncPrecedente?.NuovoTicketUrl ?? ""
                };
                if (nc.Stato == StatoNonConformita.Aperta) AnomalieTestAperte.Add(riga);
                else AnomalieTestChiuse.Add(riga);
            }
        }

        var corrente = RevisioneCorrente;
        if (corrente != null)
        {
            var effettive = corrente.CondizioniEffettive
                .Where(c => c.Esito is Esito.NonConforme or Esito.ConformeConDeviazioni).ToList();
            if (creaMancanti)
            {
                foreach (var condizione in effettive.Where(c => c.SegnalazioneUid == null || ENuovoPassaggioANok(c, TutteAnomalie)).ToList())
                {
                    var seg = new SegnalazioneDaClassificare
                    {
                        TestId = TestCase.Id, TestUid = TestCase.Uid,
                        EsecuzioneIterazione = corrente.Iterazione,
                        CondizioneUid = condizione.Uid,
                        CreataDa = _state.Config.OperatoreAttivo,
                        CreataIl = DateTime.Now
                    };
                    _segnalazioneUidPrecedente[seg.Uid] = condizione.SegnalazioneUid;
                    condizione.SegnalazioneUid = seg.Uid;
                    _segnalazioniNuove.Add(seg);
                }
            }

            // Esito corretto prima del Salva (es. Non Conforme messo per errore e riportato a Conforme): la
            // segnalazione nata in questa sessione e mai scritta non rappresenta un fatto osservato, quindi si
            // scarta invece di salvarla come "risolta da ripetizione". Quelle già scritte restano tracciate sotto.
            foreach (var seg in _segnalazioniNuove.Where(s => s.Stato == StatoSegnalazione.DaClassificare &&
                         !_salvatiInQuestoSalva.Contains(s.Uid)).ToList())
            {
                // Dalla revisione corrente, la stessa che la UI modifica: una nuova iterazione creata in sessione non e' ancora
                // in TestCase.Esecuzioni (si riallinea solo al Salva), quindi la sola ricerca li' non la trovava.
                var condizioneSeg = corrente.Condizioni.FirstOrDefault(c => c.Uid == seg.CondizioneUid)
                    ?? TestCase.Esecuzioni.SelectMany(e => e.Condizioni).LastOrDefault(c => c.Uid == seg.CondizioneUid);
                if (condizioneSeg == null || condizioneSeg.Esito is Esito.NonConforme or Esito.ConformeConDeviazioni) continue;
                _segnalazioniNuove.Remove(seg);
                if (condizioneSeg.SegnalazioneUid == seg.Uid)
                    condizioneSeg.SegnalazioneUid = _segnalazioneUidPrecedente.GetValueOrDefault(seg.Uid);
                _segnalazioneUidPrecedente.Remove(seg.Uid);
            }

            // GetByTestIdOrSenzaTest filtra PRIMA di clonare (come GetByTestIds per le NC sopra): stesso
            // sovrainsieme di prima (TestId di questo test, o nessun Test associato), senza pagare il costo di
            // clonare l'intero archivio Segnalazioni solo per scartarne quasi tutte (Addendum prestazioni
            // 2026-09-27, editor Test a 100k).
            var segnalazioni = _state.Segnalazioni.GetByTestIdOrSenzaTest(TestCase.Id, TestCase).Concat(_segnalazioniNuove)
                .DistinctBy(s => s.Uid).ToList();

            // Una ripetizione conforme risolve una segnalazione provvisoria senza cancellarne la traccia.
            foreach (var seg in segnalazioni.Where(s => s.Stato == StatoSegnalazione.DaClassificare).ToList())
            {
                var origine = TestCase.Esecuzioni.SelectMany(e => e.Condizioni)
                    .FirstOrDefault(c => c.Uid == seg.CondizioneUid);
                if (origine == null) continue;
                var ultimo = AnomaliaWorkflowService.UltimoTentativo(TestCase, AnomaliaWorkflowService.Radice(origine));
                if (ultimo?.Condizione.Esito == Esito.Conforme)
                {
                    TracciaSegnalazioneDaAggiornare(seg);
                    seg.Stato = StatoSegnalazione.RisoltaDaRipetizione;
                    seg.MotivazioneEsclusione = $"Risolta da ripetizione Conforme · REV {ultimo.Value.Esecuzione.Iterazione}";
                }
                else if (ultimo != null && ultimo.Value.Condizione.Uid != seg.CondizioneUid)
                {
                    TracciaSegnalazioneDaAggiornare(seg);
                    seg.CondizioneUid = ultimo.Value.Condizione.Uid;
                    seg.EsecuzioneIterazione = ultimo.Value.Esecuzione.Iterazione;
                    ultimo.Value.Condizione.SegnalazioneUid = seg.Uid;
                }
            }

            // Calcolato solo se davvero serve (feedback utente 2026-09-17, volumi 100k): prima veniva sempre
            // costruito, anche quando questo test non ha alcuna segnalazione da classificare/esclusa —
            // ordinare ~91k anomalie aperte (chiamando PerTest una volta per anomalia) per un test che non ne
            // ha bisogno era puro lavoro sprecato. Lo stesso identico calcolo di prima, solo differito.
            List<NonConformita>? candidate = null;
            var testDiValidazioneCr = !string.IsNullOrEmpty(TestCase.Id) && _state.ChangeRequests.GetByTestValidazioneId(TestCase.Id).Any(cr => cr.TestValidazione.Any(l => l.TestId == TestCase.Id));
            // Include anche le escluse (non solo DaClassificare): un'esclusione va mostrata con un modo per
            // annullarla (feedback utente 2026-08-20, punto 5), non far sparire la riga per sempre.
            foreach (var seg in segnalazioni.Where(s => s.Stato == StatoSegnalazione.DaClassificare || s.Stato == StatoSegnalazione.Esclusa))
            {
                var condizione = corrente.Condizioni.FirstOrDefault(c => c.Uid == seg.CondizioneUid)
                    ?? TestCase.Esecuzioni.SelectMany(e => e.Condizioni).LastOrDefault(c => c.Uid == seg.CondizioneUid);
                if (condizione == null) continue;
                if (candidate == null)
                {
                    candidate = TutteAnomalie().Where(n => n.Stato == StatoNonConformita.Aperta)
                        .OrderByDescending(n => AnomaliaWorkflowService.PerTest(TestCase.Id, new[] { n }, _ => TestCase).Count > 0)
                        .ThenByDescending(n => n.DataApertura).ToList();
                    _candidatePerUid = candidate.ToDictionary(n => n.Uid);
                    _descrittoriCandidate = candidate.Select(DescrittoreCandidata).ToList();
                }
                var segPrecedente = segnalazioniPrecedenti.GetValueOrDefault(seg.Uid);
                var puoCollegare = !testDiValidazioneCr && seg.Stato != StatoSegnalazione.Esclusa;
                var suggerimento = puoCollegare ? SuggerisciPer(condizione, seg.EsecuzioneIterazione) : null;
                // Una scelta fatta a mano sulla riga (diversa dalla pre-selezione automatica) prevale sul suggerimento quando il
                // pannello viene ricostruito; altrimenti la pre-selezione segue il suggerimento attuale.
                var sceltaPrecedente = segPrecedente?.AnomaliaSelezionata;
                var manuale = puoCollegare && sceltaPrecedente != null && sceltaPrecedente.Uid != segPrecedente!.AnomaliaAutomaticaUid
                    && _candidatePerUid.ContainsKey(sceltaPrecedente.Uid);
                var uidAutomatico = manuale ? segPrecedente!.AnomaliaAutomaticaUid : suggerimento?.AnomaliaUid;
                var uidSelezionato = manuale ? sceltaPrecedente!.Uid : uidAutomatico;
                var selezionata = puoCollegare && uidSelezionato is { } uidSel && _candidatePerUid.TryGetValue(uidSel, out var anomaliaSel) ? anomaliaSel : null;
                SegnalazioniTest.Add(new SegnalazioneTestRiga
                {
                    Segnalazione = seg,
                    EvidenziataDaRicerca = seg.Uid == _segnalazioneDaEvidenziareUid,
                    Condizione = condizione,
                    Iterazione = seg.EsecuzioneIterazione,
                    Severita = segPrecedente?.Severita ?? TestCase.Severita,
                    AnomaliaSelezionata = selezionata,
                    AnomaliaAutomaticaUid = puoCollegare ? uidAutomatico : null,
                    AltreAzioniAperte = seg.Stato != StatoSegnalazione.Esclusa && (segPrecedente?.AltreAzioniAperte ?? false),
                    MotivazioneEsclusione = segPrecedente?.MotivazioneEsclusione ?? "",
                    AnomalieCandidate = candidate,
                    PuoCollegareAnomaliaEsistente = puoCollegare,
                    Suggerimento = suggerimento
                });
                condizione.PropertyChanged += OnCondizioneCambiata;
                _condizioniOsservate.Add(condizione);
            }
        }

        OnPropertyChanged(nameof(HaAnomalieTestAperte));
        OnPropertyChanged(nameof(HaAnomalieTestChiuse));
        OnPropertyChanged(nameof(HaSegnalazioniTest));
        OnPropertyChanged(nameof(MostraTicketCollegati));
    }

    /// <summary>Anomalia suggerita per un'operazione non conforme. Stessa regola per la riga creata e per il ricalcolo
    /// mentre l'operatore scrive il problema.</summary>
    private static string ChiaveDtcCondizione(Condizione c) =>
        DtcOperazioneRegole.ChiaveDtc(c.DtcRequisitoId, c.DtcVeicolo, EcuTesto(c), c.Guasto);

    /// <summary>NC aperta gia' collegata (tramite segnalazione classificata) all'altro passo dello STESSO guasto nella revisione corrente.</summary>
    private SuggerimentoAnomalia? SuggerisciNcDelloStessoDtc(Condizione condizione)
    {
        if (!condizione.RiguardaDtc || RevisioneCorrente == null || _candidatePerUid.Count == 0) return null;
        var chiave = ChiaveDtcCondizione(condizione);
        var fratelli = RevisioneCorrente.Condizioni
            .Where(c => !ReferenceEquals(c, condizione) && c.RiguardaDtc && c.SegnalazioneUid != null
                && ChiaveDtcCondizione(c).Equals(chiave, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.SegnalazioneUid!.Value).ToHashSet();
        if (fratelli.Count == 0) return null;
        var segnalazioni = _state.Segnalazioni.GetByTestIdOrSenzaTest(TestCase.Id, TestCase).Concat(_segnalazioniNuove);
        foreach (var seg in segnalazioni)
            if (fratelli.Contains(seg.Uid) && seg.Stato == StatoSegnalazione.Classificata && seg.AnomaliaUid is { } uid
                && _candidatePerUid.TryGetValue(uid, out var nc))
                return new SuggerimentoAnomalia(nc.Uid, nc.Id, MotivoSuggerimentoAnomalia.StessaOperazione, 1.0);
        return null;
    }

    private SuggerimentoAnomalia? SuggerisciPer(Condizione condizione, int iterazione)
    {
        // Regola guasto: per ogni guasto esiste UNA sola NC (sul comportamento generale del guasto), non una per validazione e una per
        // devalidazione. Se l'altro passo dello stesso guasto ha gia' una NC aperta, la si propone come collegamento.
        if (SuggerisciNcDelloStessoDtc(condizione) is { } stessoDtc) return stessoDtc;
        var operazione = new OperazioneAnomalia(AnomaliaWorkflowService.Radice(condizione), condizione.Componente, condizione.Funzionalita);
        var titolo = NonConformitaRulesService.TitoloDaOperazione(condizione.Funzionalita, condizione.Componente, condizione.DescrizioneProblema,
            SegnalazioneTestRiga.EtichettaOperazione(condizione, iterazione));
        return SuggerimentoAnomaliaService.Migliore(operazione, titolo, _descrittoriCandidate);
    }

    /// <summary>Ricalcola il suggerimento di ogni riga mentre l'operatore compila l'operazione (problema, Funzionalita o
    /// componente). Senza questo la riga resterebbe con il suggerimento calcolato quando l'esito e' diventato Non Conforme, cioe'
    /// prima di aver scritto il problema. Una scelta fatta a mano sulla riga non viene toccata.</summary>
    private void AggiornaSuggerimentiSegnalazioni()
    {
        if (_inCostruzione) return;
        foreach (var riga in SegnalazioniTest)
        {
            if (riga.Esclusa || !riga.PuoCollegareAnomaliaEsistente) continue;
            var suggerimento = SuggerisciPer(riga.Condizione, riga.Iterazione);
            riga.Suggerimento = suggerimento;
            var scelta = riga.AnomaliaSelezionata;
            if (scelta != null && scelta.Uid != riga.AnomaliaAutomaticaUid) continue; // scelta a mano: non si tocca
            riga.AnomaliaAutomaticaUid = suggerimento?.AnomaliaUid;
            riga.AnomaliaSelezionata = suggerimento != null && _candidatePerUid.TryGetValue(suggerimento.AnomaliaUid, out var nuova) ? nuova : null;
        }
    }

    private void OnCondizioneCambiata(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Condizione.DescrizioneProblema) or nameof(Condizione.Funzionalita) or nameof(Condizione.Componente))
            AggiornaSuggerimentiSegnalazioni();
    }

    /// <summary>Dati che SuggerimentoAnomaliaService confronta: le operazioni note di un'anomalia aperta. Le operazioni
    /// del Test corrente si risolvono dalle sue prove (radice compresa); per le altre NC restano il titolo e le
    /// operazioni segnalate, senza caricare altri Test (costo proibitivo con migliaia di NC aperte).</summary>
    private CandidataAnomalia DescrittoreCandidata(NonConformita nc)
    {
        var operazioni = new List<OperazioneAnomalia>();
        foreach (var prova in nc.ProveCollegate)
        {
            if (!AnomaliaWorkflowService.ProvaAppartieneAlTest(TestCase, prova)) continue;
            if (AnomaliaWorkflowService.RisolviProva(TestCase, prova) is { } risolta)
                operazioni.Add(new OperazioneAnomalia(AnomaliaWorkflowService.Radice(risolta.Condizione), risolta.Condizione.Componente, risolta.Condizione.Funzionalita));
        }
        operazioni.AddRange(nc.OperazioniSegnalate.Select(o => new OperazioneAnomalia(null, o.Componente, o.Funzionalita)));
        var collegataAlTest = AnomaliaWorkflowService.AppartieneAlTest(nc, TestCase.Id, TestCase);
        return new CandidataAnomalia(nc.Uid, nc.Id, nc.Descrizione, collegataAlTest, operazioni);
    }

    /// <summary>Vero se l'operazione NOK/con deviazioni eredita il SegnalazioneUid di un passaggio precedente ormai
    /// concluso e va quindi tracciata con una segnalazione nuova. Le condizioni di una REV creata da Salva sono
    /// cloni della REV precedente (stesso Uid e stesso SegnalazioneUid): NOK in REV1 (segnalazione + anomalia poi
    /// chiusa), Conforme in REV2, di nuovo NOK in REV3 ereditava la vecchia segnalazione e non produceva ne'
    /// segnalazione ne' anomalia. Serve un vero nuovo passaggio (lo stato PERSISTITO dell'operazione non era NOK/
    /// con deviazioni) e una segnalazione precedente non piu' operativa (esclusa, risolta da ripetizione, oppure
    /// classificata con anomalia ormai chiusa). Con l'anomalia precedente ancora aperta resta deduplicata.</summary>
    private bool ENuovoPassaggioANok(Condizione condizione, Func<IReadOnlyList<NonConformita>> tutteAnomalie)
    {
        if (condizione.SegnalazioneUid is not { } segUid || _snapshot == null) return false;
        var persistita = _snapshot.Esecuzioni.SelectMany(e => e.Condizioni).LastOrDefault(c => c.Uid == condizione.Uid);
        if (persistita == null || persistita.Esito is Esito.NonConforme or Esito.ConformeConDeviazioni) return false;

        // Una segnalazione nata in questa sessione appartiene al passaggio NOK in corso, anche se nel frattempo e' stata
        // esclusa o classificata e non e' ancora salvata: non e' un passaggio precedente concluso e non ne genera un altro
        // (altrimenti Escludi + Salva creava una seconda segnalazione "Da classificare" e il Salva veniva rifiutato).
        if (_segnalazioniNuove.Any(x => x.Uid == segUid)) return false;
        var seg = _state.Segnalazioni.Get(segUid);
        if (seg == null) return false;
        switch (seg.Stato)
        {
            case StatoSegnalazione.Esclusa:
            case StatoSegnalazione.RisoltaDaRipetizione:
                return true;
            case StatoSegnalazione.Classificata:
                var anomalia = seg.AnomaliaUid is { } anomaliaUid ? tutteAnomalie().FirstOrDefault(n => n.Uid == anomaliaUid) : null;
                return anomalia != null && anomalia.Stato != StatoNonConformita.Aperta;
            default:
                return false;
        }
    }

    private void TracciaSegnalazioneDaAggiornare(SegnalazioneDaClassificare seg)
    {
        // Una segnalazione nuova già scritta da un salvataggio parziale (retry) e poi modificata va riscritta con la
        // sua revisione corrente (aggiornata in place dal Save), non saltata: altrimenti la modifica andrebbe persa.
        if (!_segnalazioniNuove.Contains(seg) || _salvatiInQuestoSalva.Contains(seg.Uid)) _segnalazioniDaAggiornare.TryAdd(seg.Uid, seg.RevisioneDati);
    }

    private void TracciaAnomaliaDaAggiornare(NonConformita nc)
    {
        // Una NC nuova già scritta da un salvataggio parziale esce da _anomalieNuove (vedi Salva): da lì in poi è
        // una NC esistente e passa da qui con la sua revisione corrente.
        if (!_anomalieNuove.Contains(nc)) _anomalieDaAggiornare.TryAdd(nc.Uid, nc.RevisioneDati);
    }

    private string ProssimoIdAnomalia()
    {
        var baseNext = _state.NonConformita.NextId();
        var maxNum = int.TryParse(baseNext.StartsWith("NC-", StringComparison.OrdinalIgnoreCase) ? baseNext[3..] : baseNext, out var b) ? b : 1;
        foreach (var a in _anomalieNuove)
        {
            if (int.TryParse(a.Id.StartsWith("NC-", StringComparison.OrdinalIgnoreCase) ? a.Id[3..] : a.Id, out var ai) && ai >= maxNum)
                maxNum = ai + 1;
        }
        return $"NC-{maxNum:D4}";
    }

    /// <summary>
    /// Two operators may classify separate NCs before either saves: both can temporarily receive
    /// the same suggested NC number. Never overwrite another NC. Only a NEW (still staged) NC
    /// can receive another number; persisted NCs always keep their identity and revision checks.
    /// </summary>
    private bool IdNuovaNcOccupatoDaAltra(NonConformita nuova)
    {
        var path = _state.Paths.NonConformitaFile(nuova.Id);
        if (!File.Exists(path)) return false;
        // Un ID tombstoned resta occupato per sempre: non tentare neppure di leggerlo
        // come una NC ordinaria e non riciclarlo con l'identita' della nuova.
        if (WorkflowTransactionVisibilityService.IsCommittedTombstone(_state.Paths, path)) return true;
        var esistente = JsonIo.TryRead<NonConformita>(path, out var errore)
            ?? throw new InvalidDataException(LocalizationService.Instance.Scegli(
                $"Il file NC {nuova.Id} non è leggibile. Salvataggio interrotto: {errore}",
                $"The NC file {nuova.Id} cannot be read. Save stopped: {errore}",
                $"Le fichier NC {nuova.Id} est illisible. Enregistrement interrompu : {errore}"));
        return esistente.Uid != nuova.Uid;
    }

    private void SalvaNcNuovaRisolvendoCollisioneId(NonConformita nuova)
    {
        for (var tentativo = 0; tentativo < 3; tentativo++)
        {
            if (IdNuovaNcOccupatoDaAltra(nuova))
            {
                DiagnosticTraceService.Registra("Test.Workflow", "create-nc ID collision: selecting new ID");
                nuova.Id = ProssimoIdAnomalia();
            }
            try
            {
                _state.NonConformita.Save(nuova);
                return;
            }
            catch (IOException) when (tentativo < 2 && IdNuovaNcOccupatoDaAltra(nuova))
            {
                // A second workstation may have published the same number AFTER our preflight.
                // Retry only if the ID actually belongs to a different UID, never after a
                // generic I/O failure or an incomplete write of this very NC.
                DiagnosticTraceService.Registra("Test.Workflow", "create-nc concurrent ID collision: retry");
                nuova.Id = ProssimoIdAnomalia();
            }
        }
    }

    [RelayCommand]
    private Task CreaAnomaliaDaTest(SegnalazioneTestRiga? riga) =>
        ComandoAsincrono.EseguiAsync(() => CreaAnomaliaDaTestCoreAsync(riga), "Creazione di un'anomalia dal Test", AggiungiAvvisoErrore);

    private async Task CreaAnomaliaDaTestCoreAsync(SegnalazioneTestRiga? riga)
    {
        if (riga == null || !riga.Severita.HasValue)
        {
            // Popup, non solo il banner in cima alla pagina (feedback utente 2026-08-20, punto 4): il pannello
            // Anomalie è molto più in basso di una scheda Test lunga, il banner passava inosservato e sembrava
            // che il pulsante non facesse nulla.
            var loc = LocalizationService.Instance;
            await ConfirmService.Chiedi(loc.Traduci("ValSeveritaNuovaAnomaliaTitolo"), loc.Traduci("ValSeveritaNuovaAnomalia"), loc.Traduci("OkBtn"), "");
            return;
        }

        // Conferma prima di creare (feedback utente 2026-08-21, punto 3): una volta creata, l'anomalia compare
        // subito nel pannello e non c'era modo di annullare la creazione — un click involontario non deve
        // produrre un'anomalia fantasma senza che l'operatore l'abbia voluto davvero.
        var locConferma = LocalizationService.Instance;
        if (!await ConfirmService.Chiedi(locConferma.Traduci("CreaAnomaliaConfermaTitolo"), locConferma.Traduci("CreaAnomaliaConfermaBody"),
                locConferma.Traduci("CreaBtn"), locConferma.Traduci("Annulla")))
            return;

        // Il dialogo e' asincrono: nel frattempo la riga puo' essere stata ricostruita,
        // classificata altrove o resa conforme. Non creare una NC da una selezione obsoleta.
        var corrente = SegnalazioniTest.FirstOrDefault(x => x.Segnalazione.Uid == riga.Segnalazione.Uid);
        if (_disposto || _annullamentoInCorso || corrente == null
            || corrente.Segnalazione.Stato != StatoSegnalazione.DaClassificare
            || !corrente.Severita.HasValue)
        {
            DiagnosticTraceService.Registra("Test.Workflow",
                "create-nc skipped: editor closed or pending issue changed during confirmation");
            return;
        }

        CreaAnomaliaInMemoria(corrente);
        DiagnosticTraceService.Registra("Test.Workflow",
            "create-nc draft prepared (not persisted; save Test to commit)");
        CaricaPannelloAnomalie();
        ModificheNonSalvate = true;
    }

    /// <summary>Crea in memoria la NC della segnalazione: nessuna conferma, nessuna ricostruzione del pannello (lo fa
    /// il chiamante). La severita' deve essere gia' valorizzata. La NC viene scritta solo dal Salva del Test.</summary>
    private void CreaAnomaliaInMemoria(SegnalazioneTestRiga riga)
    {
        var nc = new NonConformita
        {
            Id = ProssimoIdAnomalia(),
            TestCaseId = TestCase.Id, TestUid = TestCase.Uid,
            EsecuzioneIterazione = riga.Iterazione,
            DataApertura = DateTime.Now,
            Owner = _state.Config.OperatoreAttivo,
            Severita = riga.Severita,
            Priorita = TestCase.Priorita,
            StatoWorkflow = StatoAnomalia.Confermata,
            Descrizione = NonConformitaRulesService.TitoloDaOperazione(riga.Condizione.Funzionalita, riga.Condizione.Componente, riga.Condizione.DescrizioneProblema, riga.Etichetta),
            CondizioneOrigineUid = riga.Condizione.Uid,
            ChangeRequestIds = string.IsNullOrEmpty(TestCase.Id) ? new() : _state.ChangeRequests.GetByTestValidazioneId(TestCase.Id).Where(cr => cr.TestValidazione.Any(l => l.TestId == TestCase.Id)).Select(cr => cr.Id).ToList(),
            ProveCollegate = new() { new RiferimentoProva { TestId = TestCase.Id, TestUid = TestCase.Uid, EsecuzioneIterazione = riga.Iterazione, CondizioneUid = riga.Condizione.Uid } }
        };
        _anomalieNuove.Add(nc);
        riga.Segnalazione.Stato = StatoSegnalazione.Classificata;
        riga.Segnalazione.AnomaliaUid = nc.Uid;
        TracciaSegnalazioneDaAggiornare(riga.Segnalazione);
    }

    /// <summary>Pulsante primario della riga: collega l'anomalia suggerita o scelta, altrimenti crea una NC nuova
    /// (con la conferma di sempre).</summary>
    [RelayCommand]
    private Task AzionePrimariaSegnalazione(SegnalazioneTestRiga? riga) =>
        ComandoAsincrono.EseguiAsync(() => AzionePrimariaSegnalazioneCoreAsync(riga), "Azione sulla segnalazione del Test", AggiungiAvvisoErrore);

    private async Task AzionePrimariaSegnalazioneCoreAsync(SegnalazioneTestRiga? riga)
    {
        if (riga == null) return;
        if (riga.AzioneCollega) CollegaSegnalazioneAdAnomalia(riga);
        else await CreaAnomaliaDaTest(riga);
    }

    /// <summary>"Gestisci tutte": applica a ogni segnalazione ancora da gestire la sua azione predefinita (collega
    /// l'anomalia suggerita o scelta, altrimenti crea una NC nuova con la severita' gia' indicata), dopo un'unica
    /// conferma che elenca cosa succedera'. Senza severita' una NC nuova non si crea: la segnalazione resta da
    /// classificare. Idempotente: una segnalazione gestita esce dal pannello, quindi un secondo clic non fa nulla.</summary>
    [RelayCommand]
    private Task GestisciTutteSegnalazioni() =>
        ComandoAsincrono.EseguiAsync(GestisciTutteSegnalazioniCoreAsync, "Gestione di tutte le segnalazioni del Test", AggiungiAvvisoErrore);

    private async Task GestisciTutteSegnalazioniCoreAsync()
    {
        var loc = LocalizationService.Instance;
        var daGestire = SegnalazioniTest.Where(r => !r.Esclusa).ToList();
        var collegamenti = daGestire.Where(r => r.AzioneCollega).ToList();
        var nuove = daGestire.Where(r => r.AzioneCrea && r.Severita.HasValue).ToList();
        var saltate = daGestire.Count - collegamenti.Count - nuove.Count;
        if (collegamenti.Count + nuove.Count == 0)
        {
            if (saltate > 0) ErroriValidazione = new() { loc.Traduci("ValSeveritaNuovaAnomalia") };
            return;
        }
        var messaggio = string.Format(loc.Traduci("GestisciTutteConfermaBodyFmt"), nuove.Count, collegamenti.Count);
        if (saltate > 0) messaggio += Environment.NewLine + string.Format(loc.Traduci("GestisciTutteSaltateFmt"), saltate);
        if (!await ConfirmService.Chiedi(loc.Traduci("GestisciTutteConfermaTitolo"), messaggio, loc.Traduci("GestisciTutteApplicaBtn"), loc.Traduci("Annulla")))
            return;

        foreach (var riga in collegamenti) CollegaAnomaliaInMemoria(riga, riga.AnomaliaSelezionata!);
        // Regola guasto: una sola NC per guasto. Se validazione e devalidazione dello stesso guasto sono entrambe da creare,
        // la prima crea la NC e la seconda vi si collega.
        var ncPerDtc = new Dictionary<string, NonConformita>(StringComparer.OrdinalIgnoreCase);
        foreach (var riga in nuove)
        {
            var chiaveDtc = riga.Condizione.RiguardaDtc ? ChiaveDtcCondizione(riga.Condizione) : null;
            if (chiaveDtc != null && ncPerDtc.TryGetValue(chiaveDtc, out var ncDelDtc))
            {
                CollegaAnomaliaInMemoria(riga, ncDelDtc);
                continue;
            }
            CreaAnomaliaInMemoria(riga);
            if (chiaveDtc != null && riga.Segnalazione.AnomaliaUid is { } uidCreata
                && _anomalieNuove.FirstOrDefault(n => n.Uid == uidCreata) is { } ncCreata)
                ncPerDtc[chiaveDtc] = ncCreata;
        }
        CaricaPannelloAnomalie();
        ModificheNonSalvate = true;
    }

    [RelayCommand]
    private void CollegaSegnalazioneAdAnomalia(SegnalazioneTestRiga? riga)
    {
        if (!string.IsNullOrEmpty(TestCase.Id) && _state.ChangeRequests.GetByTestValidazioneId(TestCase.Id).Any(cr => cr.TestValidazione.Any(l => l.TestId == TestCase.Id)))
        {
            ErroriValidazione = new() { LocalizationService.Instance.Traduci("ErrCrValidazioneNuovaNc") };
            return;
        }
        if (riga?.AnomaliaSelezionata == null)
        {
            ErroriValidazione = new() { LocalizationService.Instance.Traduci("ValSelezionaAnomaliaAperta") };
            return;
        }
        CollegaAnomaliaInMemoria(riga, riga.AnomaliaSelezionata);
        CaricaPannelloAnomalie();
        ModificheNonSalvate = true;
    }

    /// <summary>Collega in memoria la segnalazione a un'anomalia (nessuna ricostruzione del pannello). Una NC esistente
    /// si copia una sola volta per sessione: piu' segnalazioni che puntano alla stessa NC ne accumulano le prove invece
    /// di sovrascriversi. La copia resta nel pannello fino al Salva del Test (vedi _anomalieCollegate). Una NC creata
    /// in questa sessione si modifica direttamente: viene scritta insieme al Test.</summary>
    private void CollegaAnomaliaInMemoria(SegnalazioneTestRiga riga, NonConformita scelta)
    {
        NonConformita nc;
        if (_anomalieNuove.FirstOrDefault(n => n.Uid == scelta.Uid) is { } nuova) nc = nuova;
        else if (_anomalieCollegate.TryGetValue(scelta.Uid, out var collegata)) nc = collegata;
        else
        {
            // Clonata qui: AnomaliaSelezionata puo' essere l'istanza effettiva della cache del repository (PeekAllReadOnly),
            // mutarla sul posto la esporrebbe modificata a qualunque altro lettore prima ancora che Save() la scriva.
            nc = scelta.Clona();
            _anomalieCollegate[nc.Uid] = nc;
        }
        riga.AnomaliaSelezionata = nc;
        TracciaAnomaliaDaAggiornare(nc);
        if (!nc.ProveCollegate.Any(p => AnomaliaWorkflowService.ProvaAppartieneAlTest(TestCase, p) && p.EsecuzioneIterazione == riga.Iterazione && p.CondizioneUid == riga.Condizione.Uid))
            nc.ProveCollegate.Add(new RiferimentoProva { TestId = TestCase.Id, TestUid = TestCase.Uid, EsecuzioneIterazione = riga.Iterazione, CondizioneUid = riga.Condizione.Uid });
        riga.Segnalazione.Stato = StatoSegnalazione.Classificata;
        riga.Segnalazione.AnomaliaUid = nc.Uid;
        TracciaSegnalazioneDaAggiornare(riga.Segnalazione);
    }

    [RelayCommand]
    private void EscludiSegnalazioneDaTest(SegnalazioneTestRiga? riga)
    {
        if (riga == null || string.IsNullOrWhiteSpace(riga.MotivazioneEsclusione))
        {
            ErroriValidazione = new() { LocalizationService.Instance.Traduci("ValMotivazioneEsclusione") };
            return;
        }
        TracciaSegnalazioneDaAggiornare(riga.Segnalazione);
        riga.Segnalazione.Stato = StatoSegnalazione.Esclusa;
        riga.Segnalazione.MotivazioneEsclusione = riga.MotivazioneEsclusione.Trim();
        CaricaPannelloAnomalie();
        ModificheNonSalvate = true;
    }

    /// <summary>Riporta una segnalazione esclusa per errore a "Da classificare" (feedback utente 2026-08-20,
    /// punto 5): prima l'esclusione era a senso unico, un click sbagliato perdeva la segnalazione per sempre —
    /// ora resta visibile in questo pannello finché non viene classificata/collegata/annullata davvero.</summary>
    [RelayCommand]
    private void AnnullaEsclusioneSegnalazione(SegnalazioneTestRiga? riga)
    {
        if (riga == null) return;
        TracciaSegnalazioneDaAggiornare(riga.Segnalazione);
        riga.Segnalazione.Stato = StatoSegnalazione.DaClassificare;
        riga.Segnalazione.MotivazioneEsclusione = "";
        CaricaPannelloAnomalie();
        ModificheNonSalvate = true;
    }

    /// <summary>Persistenza immediata e indipendente dal salvataggio del Test, riservata alle azioni di
    /// workflow decisive (oggi solo Chiudi) — un click di chiusura deve restare durevole subito, non finire
    /// nel limbo se l'operatore poi non salva o abbandona il Test (feedback utente 2026-08-21, punto 8/9).
    /// La modifica dei semplici campi (stato/severità/priorità/ticket) NON passa più di qui: viaggia con il
    /// salvataggio del Test, vedi CommitAnomalieDalTest.</summary>
    private bool SalvaAnomaliaDalTest(NcRiga? riga)
    {
        if (riga == null || !riga.Nc.Severita.HasValue)
        {
            ErroriValidazione = new() { LocalizationService.Instance.Traduci("ValSeveritaAnomalia") };
            return false;
        }
        var nc = riga.Nc;
        if (_anomalieNuove.Contains(nc))
        {
            // Una NC in bozza si persiste solo con il Test: la chiusura non e'
            // ancora durevole, ma puo' essere confermata con il prossimo Salva Test.
            ModificheNonSalvate = true;
            CaricaPannelloAnomalie();
            DiagnosticTraceService.Registra("Test.Workflow", "close-nc prepared in draft (Test save pending)");
            return true;
        }
        try
        {
            _state.NonConformita.Save(nc, nc.RevisioneDati);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Test.Workflow: close-nc persistence failed", ex);
            DiagnosticTraceService.Registra("Test.Workflow", "close-nc failed: " + ex.GetType().Name);
            ErroriValidazione = new() { string.Format(LocalizationService.Instance.Traduci("TcSalvataggioAnomaliaFallitoFmt"), ex.Message) };
            return false;
        }
        // Da questo momento la NC e' persistita: un errore di refresh NON e' un
        // fallimento del Save e non deve causare un nuovo tentativo di chiusura.
        _anomalieDaAggiornare.Remove(nc.Uid);
        ErroriValidazione = new();
        DiagnosticTraceService.Registra("Test.Workflow", "close-nc committed");
        try
        {
            _state.RicaricaTutto();
            CaricaPannelloAnomalie();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Test.Workflow: close-nc refresh after commit", ex);
            DiagnosticTraceService.Registra("Test.Workflow", "close-nc committed; UI refresh failed");
            Avvisi = Avvisi.Append(LocalizationService.Instance.Scegli(
                "La NC e' stata salvata, ma non e' stato possibile aggiornare la vista. Riapri il Test per visualizzare lo stato aggiornato.",
                "The NC was saved, but the view could not be refreshed. Reopen the Test to see the updated status.",
                "La NC a été enregistrée, mais la vue n'a pas pu être actualisée. Rouvrez le test pour voir l'état mis à jour.")).ToList();
        }
        return true;
    }

    /// <summary>Committa nel modello un riferimento ticket TICKET digitato ma non ancora aggiunto — chiamato dal
    /// salvataggio del Test (non più da un bottone dedicato, feedback utente 2026-08-21, punto 8/1: due bottoni
    /// distinti "Salva anomalia"/"Collega TICKET" facevano perdere in silenzio il ticket digitato se si cliccava
    /// quello sbagliato). Campo vuoto = niente da fare, non un errore.</summary>
    private bool CommitTicketPendente(NcRiga riga, List<string> errori)
    {
        if (!AnomaliaAzioniService.TentaAggiungiTicket(riga.Nc, riga.NuovoTicketRiferimento, riga.NuovoTicketUrl,
                _state.Config.OperatoreAttivo, out var chiaveErrore, out var aggiunto, _state.Paths))
        {
            errori.Add(LocalizationService.Instance.Traduci(chiaveErrore!));
            return false;
        }
        if (aggiunto)
        {
            riga.NuovoTicketRiferimento = "";
            riga.NuovoTicketUrl = "";
        }
        return true;
    }

    /// <summary>Committa i ticket TICKET digitati e marca ogni anomalia del Test come da salvare insieme al Test
    /// stesso (feedback utente 2026-08-21, punto 8: un unico salvataggio affidabile, niente più bottoni
    /// separati che salvano solo una parte dei campi). Ritorna gli errori di validazione dei ticket, se ce ne
    /// sono, così il chiamante può bloccare l'intero salvataggio del Test come per ogni altro campo.</summary>
    private List<string> CommitAnomalieDalTest()
    {
        var errori = new List<string>();
        foreach (var riga in AnomalieTestAperte.Concat(AnomalieTestChiuse))
        {
            CommitTicketPendente(riga, errori);
            TracciaAnomaliaDaAggiornare(riga.Nc);
        }
        return errori;
    }

    [RelayCommand]
    private Task ChiudiAnomaliaDalTest(NcRiga? riga) =>
        ComandoAsincrono.EseguiAsync(() => ChiudiAnomaliaDalTestCoreAsync(riga), "Chiusura di un'anomalia dal Test", AggiungiAvvisoErrore);

    private async Task ChiudiAnomaliaDalTestCoreAsync(NcRiga? riga)
    {
        if (riga == null || string.IsNullOrWhiteSpace(riga.Nc.MotivoChiusura))
        {
            ErroriValidazione = new() { LocalizationService.Instance.Traduci("ChiudereAnomaliaMotivo") };
            return;
        }
        var loc = LocalizationService.Instance;
        if (!await ConfirmService.Chiedi(loc.Traduci("ChiudiAnomaliaConfermaTitolo"), loc.Traduci("ChiudiAnomaliaConfermaBody"),
                loc.Traduci("ChiudiAnomaliaBtn"), loc.Traduci("Annulla")))
            return;

        var statoPrima = riga.Nc.StatoWorkflow;
        var chiusuraPrima = riga.Nc.DataChiusura;
        NonConformitaRulesService.Chiudi(riga.Nc);
        if (!SalvaAnomaliaDalTest(riga))
        {
            // Un conflitto/errore I/O non deve lasciare la NC apparentemente
            // chiusa nella riga locale, ne' proporre "Concluso" per il Test.
            riga.Nc.StatoWorkflow = statoPrima;
            riga.Nc.DataChiusura = chiusuraPrima;
            CaricaPannelloAnomalie();
            return;
        }

        // L'avanzamento automatico dello StatoLavoro del Test è una conseguenza più grande della sola chiusura
        // di questa anomalia (feedback utente 2026-08-21, punto 9/chiudi): merita una conferma dedicata e
        // separata, non va nascosto dentro la conferma di chiusura appena data.
        await ProponiAvanzamentoAConcluso();
    }

    /// <summary>Alla chiusura dell'ULTIMA anomalia (nessuna aperta, nessuna segnalazione da gestire) propone, con
    /// conferma dedicata, di portare lo StatoLavoro a Concluso. Unico punto: usato sia dalla chiusura inline sia
    /// dopo il pop-up di dettaglio anomalia. Ritorna true se lo stato e stato avanzato.</summary>
    private async Task<bool> ProponiAvanzamentoAConcluso()
    {
        if (StatoLavoroSelezionato == StatoLavoroTest.Annullato || StatoLavoroSelezionato == StatoLavoroTest.Concluso
            || AnomalieTestAperte.Count != 0 || SegnalazioniTest.Any(s => !s.Esclusa))
            return false;
        var loc = LocalizationService.Instance;
        if (!await ConfirmService.Chiedi(loc.Traduci("AvanzaStatoLavoroConfermaTitolo"), loc.Traduci("AvanzaStatoLavoroConfermaBody"),
                loc.Traduci("SiProcediSegnalato"), loc.Traduci("Annulla")))
            return false;
        StatoLavoroSelezionato = StatoLavoroTest.Concluso;
        return true;
    }

    /// <summary>Dopo il pop-up di dettaglio: se in modifica, il pop-up ha chiuso l'ULTIMA anomalia senza toccare il
    /// Test (stessa RevisioneDati) e l'operatore accetta l'avanzamento, il Test resta aperto qui con lo stato
    /// Concluso da salvare (come la chiusura inline); altrimenti ricarica come <see cref="DopoDettaglioAnomalia"/>.</summary>
    public async Task DopoDettaglioAnomaliaAsync(AnomaliaDettaglioViewModel dettaglio, bool? eraSporcoPrima = null)
    {
        if (dettaglio.Persistita && dettaglio.Modificato && InModifica && !string.IsNullOrEmpty(TestCase.Id)
            && _state.TestCases.GetById(TestCase.Id)?.RevisioneDati == TestCase.RevisioneDati)
        {
            // Il pop-up ha gia scritto sull'archivio: la ricostruzione del pannello non deve lasciare "modifiche non
            // salvate" fittizie (altrimenti la navigazione di ricarica chiederebbe di scartarle).
            var eraSporco = eraSporcoPrima ?? ModificheNonSalvate;
            _state.RicaricaTutto();
            CaricaPannelloAnomalie();
            ModificheNonSalvate = eraSporco;
            // I gestori reattivi del ricaricamento girano in coda sul thread UI: si ripristina dopo il loro passaggio.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            ModificheNonSalvate = eraSporco;
            var avanzato = await ProponiAvanzamentoAConcluso();
            ModificheNonSalvate = eraSporco || avanzato;
            return; // il Test resta aperto in modifica: nessuna navigazione di ricarica (niente dialogo 'modifiche non salvate')
        }
        DopoDettaglioAnomalia(dettaglio);
    }

    public bool PuoEliminareAnomalie => _state.EMaster;

    /// <summary>Annulla definitivamente un'anomalia, sia appena creata (mai scritta su disco: rimossa subito
    /// dalla lista in memoria) sia già salvata (rimossa dalla lista, la cancellazione del file avviene con il
    /// salvataggio del Test come ogni altra modifica — Annulla sul Test la scarta come qualunque altra riga
    /// aggiunta per errore). Master-only, come le eliminazioni definitive sui master data (feedback utente
    /// 2026-08-21, punto 3: prima non esisteva alcun modo per tornare indietro da un'anomalia creata per errore).</summary>
    [RelayCommand]
    private Task EliminaAnomaliaDalTest(NcRiga? riga) =>
        ComandoAsincrono.EseguiAsync(() => EliminaAnomaliaDalTestCoreAsync(riga), "Eliminazione di un'anomalia dal Test", AggiungiAvvisoErrore);

    private async Task EliminaAnomaliaDalTestCoreAsync(NcRiga? riga)
    {
        if (riga == null || !_state.EMaster) return;
        var loc = LocalizationService.Instance;
        if (!await ConfirmService.Chiedi(loc.Traduci("EliminaAnomaliaConfermaTitolo"), loc.Traduci("EliminaAnomaliaConfermaBody"),
                loc.Traduci("EliminaBtn"), loc.Traduci("Annulla")))
            return;

        var nc = riga.Nc;
        if (_anomalieNuove.Contains(nc)) _anomalieNuove.Remove(nc);
        else { _anomalieDaEliminare.Add(nc.Uid); _anomalieDaAggiornare.Remove(nc.Uid); }
        CaricaPannelloAnomalie();
        ModificheNonSalvate = true;
    }

    /// <summary>Riporta un'anomalia chiusa per errore allo stato aperto — Master-only, coerente con
    /// EliminaAnomaliaDalTest. La persistenza segue il percorso ordinario (CommitAnomalieDalTest al Salva),
    /// non è un'azione decisiva a sé come Chiudi: riaprire è "correggere un campo", non un nuovo evento di
    /// workflow che debba restare durevole a prescindere dal salvataggio del Test.</summary>
    [RelayCommand]
    private Task RiapriAnomaliaDalTest(NcRiga? riga) =>
        ComandoAsincrono.EseguiAsync(() => RiapriAnomaliaDalTestCoreAsync(riga), "Riapertura di un'anomalia dal Test", AggiungiAvvisoErrore);

    private async Task RiapriAnomaliaDalTestCoreAsync(NcRiga? riga)
    {
        if (riga == null || !_state.EMaster) return;
        var loc = LocalizationService.Instance;
        if (!await ConfirmService.Chiedi(loc.Traduci("RiapriAnomaliaConfermaTitolo"), loc.Traduci("RiapriAnomaliaConfermaBody"),
                loc.Traduci("RiapriAnomaliaBtn"), loc.Traduci("Annulla")))
            return;

        NonConformitaRulesService.Riapri(riga.Nc);
        TracciaAnomaliaDaAggiornare(riga.Nc);
        CaricaPannelloAnomalie();
        ModificheNonSalvate = true;
    }

    private List<NonConformita> AnomalieChiudibiliDagliEsiti()
    {
        if (string.IsNullOrWhiteSpace(TestCase.Id)) return new();
        var testPerId = RisolviTestNecessari(AnomalieTestAperte.Select(r => r.Nc));
        var chiudibili = new List<NonConformita>();
        // Riusa gli stessi riferimenti già in AnomalieTestAperte (appena popolati da CaricaPannelloAnomalie,
        // unico chiamante di questo metodo) invece di rifare un fetch indipendente: TutteLeAnomalieCorrenti()/
        // _state.NonConformita.GetAll() non garantisce lo stesso riferimento a ogni chiamata, quindi un fetch
        // separato qui perderebbe sia il MotivoChiusura appena riportato sul pannello sia le ProveCollegate
        // aggiunte più sotto, visto che CommitAnomalieDalTest persiste proprio riga.Nc del pannello.
        foreach (var nc in AnomalieTestAperte.Select(r => r.Nc))
        {
            // Registra automaticamente come nuova prova l'ultimo tentativo delle operazioni di questo Test
            // gia collegate all'anomalia. La storia precedente resta intatta.
            foreach (var prova in nc.ProveCollegate.Where(p => AnomaliaWorkflowService.ProvaAppartieneAlTest(TestCase, p)).ToList())
            {
                var risolta = AnomaliaWorkflowService.RisolviProva(TestCase, prova);
                if (risolta == null) continue;
                var ultimo = AnomaliaWorkflowService.UltimoTentativo(TestCase, AnomaliaWorkflowService.Radice(risolta.Value.Condizione));
                if (ultimo == null) continue;
                if (!nc.ProveCollegate.Any(p => AnomaliaWorkflowService.ProvaAppartieneAlTest(TestCase, p) && p.EsecuzioneIterazione == ultimo.Value.Esecuzione.Iterazione && p.CondizioneUid == ultimo.Value.Condizione.Uid))
                {
                    TracciaAnomaliaDaAggiornare(nc);
                    nc.ProveCollegate.Add(new RiferimentoProva
                    {
                        TestId = TestCase.Id, TestUid = TestCase.Uid,
                        EsecuzioneIterazione = ultimo.Value.Esecuzione.Iterazione,
                        CondizioneUid = ultimo.Value.Condizione.Uid
                    });
                }
            }
            if (AnomaliaWorkflowService.PuoChiudersiPerConformita(nc, testPerId)) chiudibili.Add(nc);
        }
        return chiudibili;
    }

    private void CaricaStrumenti()
    {
        // Ordine (spec riordino campi 2026-09-23 §2, seguito): CampoOrdinamentoService.OrdinePerVisualizzazione
        // è uno stable sort, quindi dati legacy con Ordine tutto a zero restano nell'ordine grezzo già
        // mostrato oggi (quello di Load(), mai stato alfabetico qui — a differenza della pagina master
        // Strumenti) — nessun cambio visibile finché un Master non riordina esplicitamente con ▲/▼ in
        // Impostazioni → Strumenti.
        _tuttiGliStrumenti = _state.CartellaValida
            ? CampoOrdinamentoService.OrdinePerVisualizzazione(_state.Master.Strumenti.Load().Where(s => !s.Archiviata))
            : new();
        var elenco = _tuttiGliStrumenti.Select(s => s.Nome).ToList();
        foreach (var nome in TestCase.StrumentiUsati.Where(s => !elenco.Contains(s))) elenco.Add(nome);
        // TestCase.StrumentiUsati si aggiorna solo al salvataggio (vedi riga ~990/~3715): se StrumentiOptions
        // esiste già, la selezione di partenza per ogni voce è quella corrente in memoria, non quella
        // persistita — altrimenti spunte non ancora salvate sparivano ogni volta che qualcosa richiamava
        // CaricaStrumenti prima di Salva (AggiungiStrumentoRapido dopo aver creato lo strumento, e
        // SalvaCampiStrumento dopo aver modificato i campi di uno strumento esistente dalla tabella master).
        var selezionatiPrecedenti = StrumentiOptions.ToDictionary(o => o.Valore, o => o.Selezionato, StringComparer.OrdinalIgnoreCase);
        StrumentiOptions = elenco.Select(nome => new StrumentoOption
        {
            Valore = nome,
            Selezionato = selezionatiPrecedenti.TryGetValue(nome, out var giaSelezionato) ? giaSelezionato : TestCase.StrumentiUsati.Contains(nome)
        }).ToList();
        StrumentiNomiDisponibili = elenco;
        OnPropertyChanged(nameof(StrumentiNomiDisponibili));
        // spuntare/despuntare uno strumento deve far comparire/sparire subito i suoi campi (visibilità
        // additiva rispetto al Tipo test, Addendum §18 / revisione feedback QA), non solo al salvataggio.
        foreach (var opt in StrumentiOptions)
            opt.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != nameof(StrumentoOption.Selezionato)) return;
                AggiornaCampiStrumenti();
                OnPropertyChanged(nameof(StrumentiSelezionatiRiepilogo));
                NotificaDtcOpzioni();
                if (s is not StrumentoOption o || _ripristinoSelezioneStrumento) return;
                if (o.Selezionato)
                {
                    if (InModifica || IsNew) ModificheNonSalvate = true;
                    AllineaOperazioniStrumentoSelezionato(o.Valore);
                    RimuoviOperazioniVuoteSeSoloDtc();
                }
                else DeselezioneDtcInCorso = GestisciDeselezioneStrumentoAsync(o);
            };
        OnPropertyChanged(nameof(StrumentiOptions));
        OnPropertyChanged(nameof(StrumentiOptionsFiltrate));
        OnPropertyChanged(nameof(StrumentiSelezionatiRiepilogo));
        OnPropertyChanged(nameof(MostraRicercaStrumenti));
        AggiornaCampiStrumenti();
    }

    /// <summary>Ricalcola i campi renderizzabili per ogni strumento selezionato: per il TestCase quelli con
    /// CampiCostantiPerTest=true (valori uguali per l'intero test, es. dati anagrafici del banco), per ogni
    /// Condizione quelli con CampiCostantiPerTest=false (es. ValidationTool/CaptureTool/UnitLogger: comando/risposta o esiti,
    /// variano per reiterazione — coerente per tutti gli strumenti, feedback QA §2).
    /// Richiamato ad ogni cambio di Strumenti usati così i campi compaiono/spariscono subito, non solo al
    /// salvataggio (Addendum redesign 2026-08, revisione feedback QA).</summary>
    private void AggiornaCampiStrumenti()
    {
        var selezionati = StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Valore).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var strumentiSelezionati = _tuttiGliStrumenti.Where(s => selezionati.Contains(s.Nome)).ToList();

        var (direttiTest, gruppiTest) = SpaccaPerGruppo(strumentiSelezionati, TestCase.ValoriStrumentiTest,
            TipoAmbitoCampo.PerTest, materializzaDefault: PuoModificareContenuto);
        CampiTestCostanti = direttiTest;
        GruppiTestCostanti = gruppiTest;

        AggiornaCampiRevisioneCostanti(strumentiSelezionati);

        _cacheValoriReali = new();
        try
        {
            foreach (var esecuzione in Esecuzioni)
            {
                var strumentiEsecuzione = StrumentiStoriciDi(esecuzione, strumentiSelezionati);
                foreach (var condizione in esecuzione.Condizioni)
                    AggiornaCampiVisualCondizione(condizione, strumentiEsecuzione);
            }
        }
        finally { _cacheValoriReali = null; }
        CostruisciVociVistaCampi();
    }

    /// <summary>Una revisione già congelata (non quella corrente, ancora in costruzione) porta il proprio
    /// strumentario in Dettagli.StrumentiUsati: usarlo invece della selezione live evita che sfogliare una
    /// vecchia revisione mostri campi vuoti per uno strumento aggiunto dopo, o nasconda i campi (con dati
    /// realmente compilati) di uno strumento poi rimosso dalla selezione corrente (feedback utente 2026-08-19,
    /// punto 3 — "nelle rev precedenti... cambiando strumento vedo dati vuoti invece di compilati"). Dettagli
    /// nullo (dati precedenti a questa funzione) o la revisione corrente ricadono sulla selezione live.</summary>
    private List<StrumentoDiagnostico> StrumentiStoriciDi(Esecuzione esecuzione, List<StrumentoDiagnostico> selezioneLive)
    {
        if (esecuzione == RevisioneCorrente || esecuzione.Dettagli == null) return selezioneLive;
        var nomi = esecuzione.Dettagli.StrumentiUsati;
        return _tuttiGliStrumenti.Where(s => nomi.Contains(s.Nome, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Ricalcolato anche solo alla navigazione del menu REV (Addendum §3/§6), non solo al cambio di
    /// Strumenti usati: i campi PerRevisione mostrati devono riflettere la revisione sfogliata, non sempre
    /// quella corrente — compreso lo strumentario usato per selezionarli (vedi StrumentiStoriciDi).</summary>
    private void AggiornaCampiRevisioneCostanti(List<StrumentoDiagnostico>? strumentiSelezionati = null)
    {
        strumentiSelezionati ??= _tuttiGliStrumenti.Where(s => StrumentiOptions.Any(o => o.Selezionato && o.Valore.Equals(s.Nome, StringComparison.OrdinalIgnoreCase))).ToList();
        var revisione = RevisioneVisualizzata ?? RevisioneCorrente;
        var strumentiRevisione = revisione != null ? StrumentiStoriciDi(revisione, strumentiSelezionati) : strumentiSelezionati;
        var valori = revisione?.ValoriStrumenti ?? new Dictionary<string, string>();
        var (diretti, gruppi) = SpaccaPerGruppo(strumentiRevisione, valori, TipoAmbitoCampo.PerRevisione,
            materializzaDefault: PuoModificareContenuto && ReferenceEquals(revisione, RevisioneCorrente));
        CampiRevisioneCostanti = diretti;
        GruppiRevisioneCostanti = gruppi;
    }

    private void AggiornaCampiVisualCondizione(Condizione condizione) => AggiornaCampiVisualCondizione(condizione,
        _tuttiGliStrumenti.Where(s => StrumentiOptions.Any(o => o.Selezionato && o.Valore.Equals(s.Nome, StringComparison.OrdinalIgnoreCase))).ToList());

    private void AggiornaCampiVisualCondizione(Condizione condizione, List<StrumentoDiagnostico> strumentiSelezionati)
    {
        var appartieneAllaCorrente = RevisioneCorrente?.Condizioni.Contains(condizione) == true;
        var (diretti, gruppi) = SpaccaPerGruppo(strumentiSelezionati, condizione.ValoriStrumenti, TipoAmbitoCampo.PerOperazione,
            materializzaDefault: PuoModificareContenuto && appartieneAllaCorrente);
        (diretti, gruppi) = ApplicaVistaCampi(diretti, gruppi);
        condizione.CampiVisual = diretti;
        condizione.GruppiVisual = gruppi;
    }

    /// <summary>Divide i campi con l'Ambito richiesto tra quelli diretti (Gruppo vuoto, sempre visibili)
    /// e quelli raggruppati (stesso Gruppo, dietro un pannello a tendina collassato con quel titolo) — Addendum
    /// redesign 2026-08, revisione feedback QA: es. i campi guasto di ValidationTool, non il punto principale delle prove.
    /// Non più static (feedback utente 2026-08-24, punto 3): risolve qui il default personale dell'operatore
    /// attivo, che vince su quello del Master quando presente.</summary>
    private (List<CampoStrumentoValore> Diretti, List<GruppoCampiStrumento> Gruppi) SpaccaPerGruppo(
        IEnumerable<StrumentoDiagnostico> strumenti, Dictionary<string, string> valori, TipoAmbitoCampo ambito,
        bool materializzaDefault = true) =>
        CampoStrumentoValoreLayout.SpaccaPerGruppo(strumenti, valori, ambito,
            _state.OperatoreCorrente?.ValoriDefaultCampi, materializzaDefault);

    /// <summary>"Ricorda come mio default" (feedback utente 2026-08-24, punto 3): il valore corrente del campo
    /// diventa il default personale dell'operatore attivo per quel campo strumento, salvato sul suo profilo
    /// nella cartella condivisa — non richiede il ciclo di lock/modifica completo di Operatori, stesso pattern
    /// "auto-scrittura" già usato per il cambio PIN obbligatorio (AppState.CompletaCambioPinObbligatorio).
    /// Non applicabile ai campi custom (Strumento vuoto, secondo costruttore di CampoStrumentoValore).</summary>
    [RelayCommand]
    private void SalvaValoreComeMioDefault(CampoStrumentoValore? campo)
    {
        if (campo == null || !campo.PuoRicordareDefault) return;
        _state.SalvaValoreDefaultCampo($"{campo.Strumento}::{campo.Nome}", campo.Valore);
        campo.ImpostaDefaultPersonale(campo.Valore);
        // Conferma visibile e transitoria (feedback utente 6.4.x: il click non dava alcun riscontro).
        campo.ConfermaDefaultSalvato = true;
        _ = Task.Delay(2500).ContinueWith(_ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => campo.ConfermaDefaultSalvato = false));
    }

    /// <summary>Rimuove il default personale dell'operatore per il campo (SalvaValoreDefaultCampo con valore
    /// vuoto elimina la chiave): da li' in poi vale di nuovo l'eventuale default del Master. Il valore corrente
    /// del campo nel test non viene toccato.</summary>
    [RelayCommand]
    private void RimuoviMioDefault(CampoStrumentoValore? campo)
    {
        if (campo == null || !campo.PuoRicordareDefault || !campo.HaDefaultPersonale) return;
        _state.SalvaValoreDefaultCampo($"{campo.Strumento}::{campo.Nome}", null);
        campo.ImpostaDefaultPersonale(null);
    }

    /// <summary>"Altro" -> aggiunta rapida di un nuovo strumento: l'eccezione "disponibile a tutti i ruoli"
    /// documentata dall'Addendum §17.5 è stata rimossa (feedback utente 2026-08-19, punto 2 — "solo master...
    /// slave può solo compilare e creare nuovi test"), ora richiede PuoGestireStrumenti come il resto della
    /// gestione strumenti da dentro un test.</summary>
    [RelayCommand]
    private void AggiungiStrumentoRapido()
    {
        if (!PuoGestireStrumenti || string.IsNullOrWhiteSpace(NuovoStrumentoNome) || !_state.CartellaValida) return;
        var nome = NuovoStrumentoNome.Trim();
        var repo = _state.Master.Strumenti;
        var lista = repo.Load();
        if (!lista.Any(s => s.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase)))
        {
            lista.Add(new StrumentoDiagnostico { Nome = nome, UltimaModifica = DateTime.Now, ModificatoDa = _state.Config.OperatoreAttivo, CreatoDa = _state.Config.OperatoreAttivo });
            repo.Save(lista);
        }
        NuovoStrumentoNome = "";
        CaricaStrumenti();
        var opt = StrumentiOptions.FirstOrDefault(o => o.Valore.Equals(nome, StringComparison.OrdinalIgnoreCase));
        if (opt != null) opt.Selezionato = true;
    }

    /// <summary>"Altro" -> aggiunta rapida di una nuova componente al catalogo master (feedback utente 2026-09-18,
    /// vedi PuoAggiungereCampiSistemaRapidi): disponibile a Master e Standard, non solo a Master come
    /// AggiungiStrumentoRapido — un componente è solo un nome di catalogo, non porta configurazione tecnica da
    /// proteggere. OnDatiRicaricati ricarica EcuDisponibili dal master appena scritto, poi AggiungiEcu (lo
    /// stesso comando già usato dal picker ScegliEcu) la aggiunge alla selezione del test corrente.
    /// Associa automaticamente i veicoli attualmente selezionati nel test corrente alla nuova componente.</summary>
    [RelayCommand]
    private void AggiungiEcuRapido()
    {
        if (!PuoAggiungereCampiSistemaRapidi || string.IsNullOrWhiteSpace(NuovoEcuNome) || !_state.CartellaValida) return;
        var nome = NuovoEcuNome.Trim();
        var repo = _state.Master.Componente;
        var lista = repo.Load();
        var esistente = lista.FirstOrDefault(e => e.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase));
        if (esistente == null)
        {
            var veicoliAssociati = VeicoliSelezionatiVisual.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            lista.Add(new Componente
            {
                Nome = nome,
                VeicoliAssociati = veicoliAssociati,
                UltimaModifica = DateTime.Now,
                ModificatoDa = _state.Config.OperatoreAttivo,
                CreatoDa = _state.Config.OperatoreAttivo
            });
            repo.Save(lista);
        }
        else if (VeicoliSelezionatiVisual.Count > 0)
        {
            var nuovi = VeicoliSelezionatiVisual.Where(v => !esistente.VeicoliAssociati.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList();
            if (nuovi.Count > 0)
            {
                esistente.VeicoliAssociati.AddRange(nuovi);
                esistente.UltimaModifica = DateTime.Now;
                esistente.ModificatoDa = _state.Config.OperatoreAttivo;
                repo.Save(lista);
            }
        }
        NuovoEcuNome = "";
        OnDatiRicaricati();
        AggiungiEcu(nome);
    }

    /// <summary>Aggiunta rapida di una Funzionalita' al catalogo master dal Test (stesso principio di
    /// AggiungiEcuRapido: disponibile a Master e Standard). Invariante TutteLeEcu/EcuAssociate: un test con
    /// componente crea una funzionalita' associata a quelle componente, senza componente una generica. Una funzionalita' esistente
    /// (confronto OrdinalIgnoreCase sul nome trimmato) non viene duplicata: si usa il nome del catalogo e
    /// solo un Master puo' estenderne i componenti associate (Standard puo' creare ma non modificare, come in
    /// FunzionalitaViewModel.PuoCreare). Una voce ARCHIVIATA omonima non viene ne' ricreata ne' ripristinata
    /// (scelta conservativa: il ripristino e' una decisione del Master): si mostra solo un avviso.
    /// NuovaFunzionalita assegnata DOPO OnDatiRicaricati (fuori dal guard _riallineamentoListeMaster).</summary>
    [RelayCommand]
    private void AggiungiFunzionalitaRapida()
    {
        if (!PuoAggiungereCampiSistemaRapidi || string.IsNullOrWhiteSpace(NuovaFunzionalitaCatalogoNome) || !_state.CartellaValida) return;
        var nome = NuovaFunzionalitaCatalogoNome.Trim();
        AvvisoFunzionalitaRapida = null;
        var repo = _state.Master.Funzionalita;
        var lista = repo.Load();
        var atteso = EntityStateFingerprint.Clone(lista);
        var esistente = lista.FirstOrDefault(f => f.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase));
        if (esistente is { Archiviata: true })
        {
            AvvisoFunzionalitaRapida = string.Format(LocalizationService.Instance.Traduci("FunzionalitaArchiviataAvvisoFmt"), esistente.Nome);
            return;
        }
        var ecuTest = TestCase.Componente.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var operatore = _state.Config.OperatoreAttivo;
        if (esistente == null)
        {
            lista.Add(new Funzionalita
            {
                Nome = nome,
                TutteLeEcu = ecuTest.Count == 0,
                EcuAssociate = ecuTest.Count == 0 ? new List<string>() : ecuTest,
                UltimaModifica = DateTime.Now,
                ModificatoDa = operatore,
                CreatoDa = operatore
            });
            repo.Save(lista, atteso);
        }
        else if (!esistente.TutteLeEcu && ecuTest.Count > 0 && _state.EMaster)
        {
            var mancanti = ecuTest.Where(e => !esistente.EcuAssociate.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
            if (mancanti.Count > 0)
            {
                var prima = string.Join(", ", esistente.EcuAssociate);
                esistente.EcuAssociate.AddRange(mancanti);
                esistente.Changelog.Add(new ChangelogEntry
                {
                    Data = DateTime.Now, Operatore = operatore, Campo = nameof(Funzionalita.EcuAssociate),
                    ValorePrecedente = prima, ValoreNuovo = string.Join(", ", esistente.EcuAssociate)
                });
                esistente.UltimaModifica = DateTime.Now;
                esistente.ModificatoDa = operatore;
                repo.Save(lista, atteso);
            }
        }
        var nomeCatalogo = esistente?.Nome ?? nome;
        NuovaFunzionalitaCatalogoNome = "";
        OnDatiRicaricati();
        NuovaFunzionalita = nomeCatalogo;
        AggiungiFunzionalita();
    }

    /// <summary>Stesso principio di AggiungiEcuRapido sopra, per i Veicoli. VeicoloSelezionato assegnato DOPO
    /// OnDatiRicaricati (che nel frattempo lo riporta al valore gia' salvato sul test, dentro il guard
    /// _riallineamentoListeMaster): l'assegnazione qui fuori dal guard passa dal normale
    /// OnVeicoloSelezionatoChanged, che aggiunge davvero il nuovo veicolo a TestCase.Veicoli.</summary>
    [RelayCommand]
    private void AggiungiVeicoloRapido()
    {
        if (!PuoAggiungereCampiSistemaRapidi || string.IsNullOrWhiteSpace(NuovoVeicoloNome) || !_state.CartellaValida) return;
        var nome = NuovoVeicoloNome.Trim();
        var repo = _state.Master.Veicoli;
        var lista = repo.Load();
        var esistente = lista.FirstOrDefault(v => v.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase));
        if (esistente == null)
        {
            lista.Add(new Veicolo
            {
                Nome = nome,
                UltimaModifica = DateTime.Now,
                ModificatoDa = _state.Config.OperatoreAttivo,
                CreatoDa = _state.Config.OperatoreAttivo
            });
            repo.Save(lista);
        }
        NuovoVeicoloNome = "";
        OnDatiRicaricati();
        VeicoloSelezionato = nome;
    }

    private static TestCase Clona(TestCase tc) => JsonSerializer.Deserialize<TestCase>(JsonSerializer.Serialize(tc, JsonIo.Options), JsonIo.Options)!;
    private static Dictionary<string, string> ClonaCampi(Dictionary<string, string> c) => new(c);

    /// <summary>Copia dei soli campi strumento: la durata (TIME) non e' un campo da riportare su un'altra operazione.</summary>
    private static Dictionary<string, string> ClonaCampiStrumenti(Dictionary<string, string> c) => DurataOperazione.CopiaSenzaDurata(c);
    private static Esecuzione ClonaEsecuzione(Esecuzione e) => JsonSerializer.Deserialize<Esecuzione>(JsonSerializer.Serialize(e, JsonIo.Options), JsonIo.Options)!;

    /// <summary>Confronto "a grana grossa" (serializza e confronta, come HaModifiche negli editor master data)
    /// tra lo snapshot pre-modifica e lo stato attuale, solo sui campi Dettagli (Addendum §6/§7): la
    /// Pianificazione non conta qui, ha già la sua nota obbligatoria sopra come traccia del cambiamento.</summary>
    private bool DettagliCambiati()
    {
        if (_snapshot == null) return false;
        object Rilevanti(TestCase tc)
        {
            var rev = tc.Esecuzioni.LastOrDefault();
            return new
            {
                tc.Componente, tc.Veicolo, tc.Veicoli, tc.TestTemplateUid, tc.Funzionalita, tc.StrumentiUsati,
                tc.DescrizioneBreve, tc.Descrizione, tc.DescrizioneProblema, tc.CampiCustom,
                Matricola = rev?.Matricola, BaselineSw = rev?.BaselineSw, BaselineHw = rev?.BaselineHw, ValoriStrumentiRevisione = rev?.ValoriStrumenti,
                EsitoSovrascritto = rev?.EsitoSovrascritto, MotivazioneOverride = rev?.MotivazioneOverride,
                Condizioni = rev?.Condizioni.Select(c => new { c.Funzionalita, c.Guasto, c.DtcRequisitoId, c.DtcRequisitoVuotoScelto, c.DtcPasso, c.DtcVeicolo, c.Strumento, c.Esito, c.ValoriStrumenti, c.Nota, c.MotivazioneNonApplicabile, c.DescrizioneDeviazione, c.DescrizioneProblema })
            };
        }
        return JsonSerializer.Serialize(Rilevanti(_snapshot), JsonIo.Options) != JsonSerializer.Serialize(Rilevanti(TestCase), JsonIo.Options);
    }

    /// <summary>Fotografia dei Dettagli correnti del test, salvata dentro ogni revisione (Addendum §1,
    /// feedback QA): senza, sfogliare una revisione storica dal menu REV mostrava comunque i Dettagli
    /// ATTUALI del test, non quelli dell'epoca.</summary>
    private DettagliTestSnapshot CostruisciSnapshotDettagli() => new()
    {
        Componente = new EcuSelection(TestCase.Componente),
        Veicolo = TestCase.Veicolo,
        Veicoli = new List<string>(TestCase.VeicoliEffettivi),
        TestTemplateUid = TestCase.TestTemplateUid,
        TestTemplateNome = _state.NomeTestTemplate(TestCase.TestTemplateUid),
        Funzionalita = new List<string>(TestCase.Funzionalita),
        StrumentiUsati = StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Valore).ToList(),
        DescrizioneBreve = TestCase.DescrizioneBreve,
        Descrizione = TestCase.Descrizione,
        DescrizioneProblema = TestCase.DescrizioneProblema,
        CampiCustom = new Dictionary<string, string>(TestCase.CampiCustom),
        ValoriStrumentiTest = new Dictionary<string, string>(TestCase.ValoriStrumentiTest),
    };

    // ----- Lock -----

    private string? PercorsoFile => IsNew || !_state.CartellaValida ? null : _state.TestCases.PercorsoPer(TestCase);

    /// <summary>Tiene vivo il lock durante una sessione di modifica lunga: senza questo, il timeout di
    /// inattività del lock (20 minuti) scadrebbe comunque anche se l'operatore sta ancora attivamente
    /// compilando il form, permettendo a qualcun altro di rubare il lock e sovrascrivere in silenzio.</summary>
    private System.Timers.Timer? _heartbeatTimer;

    // Vero soltanto mentre la sessione di modifica è aperta. FermaHeartbeat lo azzera PRIMA di ogni Release:
    // una callback del timer già in volo non può quindi ricreare un lock dopo il rilascio.
    private volatile bool _sessioneModificaAttiva;

    private void AvviaHeartbeat()
    {
        if (PercorsoFile == null) return;
        FermaHeartbeat();
        _sessioneModificaAttiva = true;
        _heartbeatTimer = new System.Timers.Timer(LockService.IntervalloHeartbeat.TotalMilliseconds) { AutoReset = true };
        _heartbeatTimer.Elapsed += async (_, _) => await RinnovaOInRiacquisisciLock();
        _heartbeatTimer.Start();
    }

    /// <summary>Callback del timer (thread pool, non deve mai lanciare): rinnova il lock proprio o lo riacquisisce
    /// in modo trasparente se era scaduto/sparito senza che nessun altro lo abbia preso. Solo un lock vivo di
    /// un'altra sessione (conflitto reale) aggiorna il banner, sul thread UI.</summary>
    private async Task RinnovaOInRiacquisisciLock()
    {
        try
        {
            string? percorso;
            string? sessioneAttesa;
            lock (_statoAcquisizioneLockGate)
            {
                if (!_sessioneModificaAttiva || _disposto) return;
                percorso = PercorsoFile;
                sessioneAttesa = _sessioneLockAcquisita;
            }
            if (percorso == null) return;
            if (string.IsNullOrWhiteSpace(sessioneAttesa)) return;

            LockEnsureResult? esitoDaPubblicare = null;
            await LockGate.EseguiAsync(percorso, async () =>
            {
                lock (_statoAcquisizioneLockGate)
                {
                    if (!_sessioneModificaAttiva || _disposto ||
                        !StringComparer.Ordinal.Equals(_sessioneLockAcquisita, sessioneAttesa)) return;
                }

                var esito = RinnovaLockHeartbeatPerTest is { } rinnovaPerTest
                    ? await rinnovaPerTest(_state.Locks, percorso, _state.Config.OperatoreAttivo).ConfigureAwait(false)
                    : await _state.Locks.EnsureOwnedTrackedAsync(percorso, _state.Config.OperatoreAttivo).ConfigureAwait(false);

                bool sessioneChiusaDuranteCallback;
                lock (_statoAcquisizioneLockGate)
                {
                    sessioneChiusaDuranteCallback = !_sessioneModificaAttiva || _disposto ||
                        !StringComparer.Ordinal.Equals(_sessioneLockAcquisita, sessioneAttesa);
                    if (!sessioneChiusaDuranteCallback && !string.IsNullOrWhiteSpace(esito.SessionId))
                    {
                        // Dispose legge lo stesso token sotto questo lock: potrà quindi rilasciare il lease
                        // appena riacquisito invece di lasciare un lock orfano con la sessione precedente.
                        _percorsoLockSessioneAcquisita = percorso;
                        _sessioneLockAcquisita = esito.SessionId;
                    }
                }

                if (sessioneChiusaDuranteCallback)
                {
                    // La callback ha riacquisito un lease dopo la chiusura: rimuove solo il token restituito
                    // dalla stessa operazione e attende l'I/O effettivo prima di cedere il gate.
                    if (!string.IsNullOrWhiteSpace(esito.SessionId) &&
                        (esito.Riacquisito || !StringComparer.Ordinal.Equals(esito.SessionId, sessioneAttesa)))
                        await LockGate.RilasciaSessioneEsattaConRetrySottoGateAsync(
                            _state.Locks, percorso, esito.SessionId,
                            RilascioSessioneLockPerTest, AttesaRetryLockPerTest).ConfigureAwait(false);
                    return;
                }

                esitoDaPubblicare = esito;
            }).ConfigureAwait(false);

            if (esitoDaPubblicare != null)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (InModifica && !_disposto) RegistraEsitoRinnovo(esitoDaPubblicare);
                });
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Modifica test (heartbeat lock)", ex);
        }
    }

    /// <summary>Interpreta l'esito di un rinnovo sul thread UI (P2.1). Un lock vivo di un'altra sessione mostra il banner di
    /// conflitto; un rinnovo non confermato viene registrato e, dopo la soglia, diventa un avviso persistente finché un
    /// rinnovo non torna confermato. Non richiama la lettura del lock: il banner usa l'holder già restituito.</summary>
    private void RegistraEsitoRinnovo(LockEnsureResult esito)
    {
        switch (_monitorRinnovo.Registra(esito))
        {
            case EsitoRinnovoLock.NonPosseduto:
                ApplicaAvvisoAltraPostazione(esito.Holder);
                break;
            case EsitoRinnovoLock.NonConfermato:
                CrashLogService.Registra("Modifica test (rinnovo lock non confermato)", null);
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

    private void FermaHeartbeat()
    {
        _sessioneModificaAttiva = false;
        _heartbeatTimer?.Stop();
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        _monitorRinnovo.Azzera();
        AggiornaAvvisoLockNonConfermato();
    }

    // === Autosave locale di un test nuovo (Addendum: audit UX 2026-08-26, nota minore 1) ==================
    // Un test non ancora salvato vive solo in memoria (BozzaPerNuovoTest, usato per "Duplica come nuovo
    // test"): un crash dell'app o una chiusura brusca lo perde per intero, anche dopo minuti di compilazione.
    // Salva una copia locale periodica — mai nella cartella condivisa (vedi BozzaAutosaveService) — proposta
    // in ripristino la prossima volta che si apre un test nuovo con lo stesso operatore.
    private System.Timers.Timer? _autosaveTimer;

    private void AvviaAutosave()
    {
        if (!IsNew) return; // solo in creazione: un test esistente ha già la sua copia sicura su disco
        FermaAutosave();
        _autosaveTimer = new System.Timers.Timer(TimeSpan.FromSeconds(30).TotalMilliseconds) { AutoReset = true };
        // Marshalling sul thread UI (Addendum: audit UX 2026-08-26, punto 4 — stesso principio):
        // TestCase e le sue collezioni non sono thread-safe, e questo timer gira in background; leggerle da
        // un thread diverso da quello che le sta eventualmente mutando (l'utente che digita) sarebbe una
        // race condition. Salva solo se c'è davvero qualcosa di non salvato, altrimenti riscrive ad ogni giro
        // un file identico al precedente senza motivo.
        _autosaveTimer.Elapsed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ModificheNonSalvate) _state.BozzaAutosave.Salva(_state.Config.OperatoreAttivo, TestCase);
        });
        _autosaveTimer.Start();
    }

    private void FermaAutosave()
    {
        _autosaveTimer?.Stop();
        _autosaveTimer?.Dispose();
        _autosaveTimer = null;
    }

    private void AggiornaStatoLock()
    {
        if (PercorsoFile == null) { LockDetenutoDaAltri = false; LockInfoTesto = null; return; }
        // Proprietà = sessione (SessionId), non nome operatore: vedi MasterDataEditorViewModelBase.AggiornaStatoLock.
        ApplicaStatoLock(_state.Locks.GetForeignActiveLock(PercorsoFile, _state.Config.OperatoreAttivo));
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

    /// <summary>Lock attivo (non scaduto) di un'ALTRA postazione/operatore, o null se non ce n'è uno visibile
    /// oppure se il lock visibile è il proprio (stesso operatore E stessa postazione: mai un avviso per sé
    /// stessi). Confronta anche il device, non solo l'operatore: due postazioni possono condividere lo stesso
    /// nome operatore (es. turno/reparto), caso che il solo confronto su Operatore non distinguerebbe.</summary>
    private async Task<LockInfo?> VerificaLockAltraPostazionePrimaDelSalvataggioAsync()
    {
        var percorso = PercorsoFile;
        if (percorso == null) return null;
        // Prima di decidere se mostrare l'avviso il lock viene rinnovato/riacquisito in modo trasparente: se era
        // scaduto per heartbeat saltati (OneDrive) e nessuno lo ha preso, torna nostro e non c'è nulla da avvisare.
        // Un lock vivo di un'altra sessione (anche stesso operatore/stesso nome postazione) resta un conflitto
        // reale; l'identità è il SessionId di LockService, mai Operatore/DeviceId.
        // Anche il Salva rinnova e rivaluta il possesso: serializza la chiamata con heartbeat/Dispose e mantiene
        // il token aggiornato se il lock va riacquisito (P2.1).
        string? sessioneAttesa;
        lock (_statoAcquisizioneLockGate)
            sessioneAttesa = InModifica && _sessioneModificaAttiva && !_disposto ? _sessioneLockAcquisita : null;

        if (!string.IsNullOrWhiteSpace(sessioneAttesa))
        {
            LockEnsureResult? esitoDaRegistrare = null;
            await LockGate.EseguiAsync(percorso, async () =>
            {
                lock (_statoAcquisizioneLockGate)
                {
                    if (!InModifica || !_sessioneModificaAttiva || _disposto ||
                        !StringComparer.Ordinal.Equals(_sessioneLockAcquisita, sessioneAttesa)) return;
                }

                var esito = RinnovaLockPreSalvataggioPerTest is { } rinnovaPerTest
                    ? await rinnovaPerTest(_state.Locks, percorso, _state.Config.OperatoreAttivo).ConfigureAwait(false)
                    : await _state.Locks.EnsureOwnedTrackedAsync(percorso, _state.Config.OperatoreAttivo)
                        .ConfigureAwait(false);
                var haToken = !string.IsNullOrWhiteSpace(esito.SessionId);
                bool sessioneChiusaDuranteRinnovo;
                lock (_statoAcquisizioneLockGate)
                {
                    sessioneChiusaDuranteRinnovo = !InModifica || !_sessioneModificaAttiva || _disposto ||
                        !StringComparer.Ordinal.Equals(_sessioneLockAcquisita, sessioneAttesa);
                    if (!sessioneChiusaDuranteRinnovo && haToken)
                    {
                        _percorsoLockSessioneAcquisita = percorso;
                        _sessioneLockAcquisita = esito.SessionId;
                    }
                }

                if (sessioneChiusaDuranteRinnovo && haToken &&
                    (esito.Riacquisito || !StringComparer.Ordinal.Equals(esito.SessionId, sessioneAttesa)))
                    await LockGate.RilasciaSessioneEsattaConRetrySottoGateAsync(
                        _state.Locks, percorso, esito.SessionId!,
                        RilascioSessioneLockPerTest, AttesaRetryLockPerTest).ConfigureAwait(false);

                if (!sessioneChiusaDuranteRinnovo) esitoDaRegistrare = esito;
            });

            if (esitoDaRegistrare != null) RegistraEsitoRinnovo(esitoDaRegistrare);
        }
        // Esclude già i lock scaduti e quelli propri; un lock non leggibile non è un conflitto dimostrato (P2.2).
        var lettura = _state.Locks.GetForeignActiveLock(percorso, _state.Config.OperatoreAttivo);
        return lettura.Stato == StatoLockAltrui.Occupato ? lettura.Info : null;
    }

    /// <summary>Acquisizione del lock fuori dal thread UI. Separata come metodo virtuale interno solo per poter
    /// simulare nei test un share lento; in produzione è la chiamata reale a <see cref="LockService.TryAcquire"/>.</summary>
    internal virtual Task<LockResult> AcquisisciLockAsync(string percorso, string operatore) =>
        AcquisisciLockPerTest?.Invoke(percorso, operatore)
        ?? Task.Run(() => _state.Locks.TryAcquireNuovaSessione(percorso, operatore));

    /// <summary>Come il controllo pre-salvataggio ma fuori dal thread UI. Non rinnova il lock: chi la
    /// chiama subito dopo un'acquisizione riuscita lo ha appena creato, quindi il rinnovo sarebbe un no-op.</summary>
    internal virtual async Task<LockInfo?> RilevaLockAltraPostazioneAsync()
    {
        var percorso = PercorsoFile;
        if (percorso == null) return null;
        var lettura = await LeggiLockAltroAsync(percorso);
        return lettura.Stato == StatoLockAltrui.Occupato ? lettura.Info : null;
    }

    /// <summary>Lettura del lock di un'altra sessione fuori dal thread UI: stesso esito di GetForeignActiveLock
    /// usato da <see cref="AggiornaStatoLock"/>, senza bloccare il dispatcher su uno share lento.</summary>
    private Task<LetturaLockAltrui> LeggiLockAltroAsync(string percorso)
    {
        var operatore = _state.Config.OperatoreAttivo;
        return Task.Run(() => _state.Locks.GetForeignActiveLock(percorso, operatore));
    }

    /// <summary>Aggiorna il banner non bloccante di AvvisoAltraPostazioneVisibile/Testo. Chiamato all'apertura
    /// in modifica e di nuovo subito prima del Salva (decisione utente 6.4.1 "Avviso + merge automatico",
    /// punto A): solo lettura di un file locale già sincronizzato, nessun lavoro pesante sul thread UI.</summary>
    private async Task AggiornaAvvisoAltraPostazioneAsync() =>
        ApplicaAvvisoAltraPostazione(await VerificaLockAltraPostazionePrimaDelSalvataggioAsync());

    private void ApplicaAvvisoAltraPostazione(LockInfo? attivo)
    {
        if (attivo == null) { AvvisoAltraPostazioneVisibile = false; AvvisoAltraPostazioneTesto = null; return; }
        AvvisoAltraPostazioneVisibile = true;
        var postazione = string.IsNullOrWhiteSpace(attivo.DeviceId)
            ? "?"
            : attivo.DeviceId.Length <= 8 ? attivo.DeviceId : attivo.DeviceId[..8];
        var dalleOre = (attivo.AcquisitoIl == default ? DateTime.Now : attivo.AcquisitoIl).ToString("HH:mm");
        AvvisoAltraPostazioneTesto = string.Format(
            LocalizationService.Instance.Traduci("AvvisoAltraPostazioneFmt"), attivo.Operatore, postazione, dalleOre);
    }

    /// <summary>Avvia la modifica. Lettura del lock, acquisizione, rilettura del test su disco e banner di conflitto
    /// avvengono fuori dal thread UI (share SMB lento); l'applicazione dei dati resta sul thread UI. AsyncRelayCommand
    /// senza esecuzioni concorrenti: CanExecute resta falso finché è in corso. Se il lock viene acquisito ma la
    /// modifica non parte (editor chiuso, errore di lettura), questo tentativo lo rilascia legandosi alla propria
    /// sessione (ReleaseIfSession), senza mai staccare un lock creato da un'altra apertura.</summary>
    [RelayCommand]
    private async Task IniziaModifica()
    {
        TaskCompletionSource operazione;
        lock (_statoAcquisizioneLockGate)
        {
            if (_disposto || _chiusuraInCorso || RilascioLockInAttesa || _iniziaModificaInCorsoPerUscita != null) return;
            operazione = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _iniziaModificaInCorsoPerUscita = operazione;
        }

        try { await IniziaModificaCorePerComandoAsync(); }
        finally
        {
            lock (_statoAcquisizioneLockGate)
            {
                if (ReferenceEquals(_iniziaModificaInCorsoPerUscita, operazione))
                    _iniziaModificaInCorsoPerUscita = null;
            }
            operazione.TrySetResult();
        }
    }

    private async Task IniziaModificaCorePerComandoAsync()
    {
        if (_disposto || RilascioLockInAttesa) return;
        // Finestra di visualizzazione di un test correlato: inoltra la richiesta alla finestra principale.
        if (_richiediModificaEsterna != null) { await _richiediModificaEsterna(); return; }
        if (!PuoModificare) return;

        var recupero = await AssicuraRecuperoBozzaModificaAsync();
        if (_disposto || InModifica) return;
        if (recupero == EsitoRecuperoBozzaModifica.DaRitentare) return;

        await IniziaModificaCoreAsync();
    }

    private async Task<(LockResult? Lock, bool Ready)> IniziaModificaCoreAsync(bool abilitaModifica = true)
    {
        if (_disposto || RilascioLockInAttesa || !PuoModificare) return (null, false);

        var percorso = PercorsoFile;
        LockResult? acquisito = null;
        var avvioCompletato = false;
        try
        {
            var letturaLock = percorso == null
                ? new LetturaLockAltrui(StatoLockAltrui.Libero, null)
                : await LeggiLockAltroAsync(percorso);
            if (_disposto) return (null, false);
            ApplicaStatoLock(letturaLock);
            if (LockDetenutoDaAltri) return (null, false);
            if (percorso != null)
            {
                lock (_statoAcquisizioneLockGate) _acquisizioneLockInCorso = true;
                var r = await LockGate.EseguiAsync(percorso, async () =>
                {
                    try
                    {
                        if (_disposto)
                        {
                            lock (_statoAcquisizioneLockGate) _acquisizioneLockInCorso = false;
                            return new LockResult { Acquisito = false };
                        }
                        var risultato = await AcquisisciLockAsync(percorso, _state.Config.OperatoreAttivo);
                        if (!risultato.Acquisito)
                        {
                            lock (_statoAcquisizioneLockGate) _acquisizioneLockInCorso = false;
                            return risultato;
                        }

                        acquisito = risultato;
                        var sessione = risultato.DetenutoDa?.SessionId;
                        bool rilasciaDentroGate;
                        lock (_statoAcquisizioneLockGate)
                        {
                            _percorsoLockSessioneAcquisita = percorso;
                            _sessioneLockAcquisita = sessione;
                            rilasciaDentroGate = _disposto;
                            if (!rilasciaDentroGate) _acquisizioneLockInCorso = false;
                        }

                        if (rilasciaDentroGate)
                        {
                            if (!string.IsNullOrWhiteSpace(sessione))
                                await LockGate.RilasciaSessioneEsattaConRetrySottoGateAsync(
                                    _state.Locks, percorso, sessione, RilascioSessioneLockPerTest, AttesaRetryLockPerTest);
                            lock (_statoAcquisizioneLockGate)
                            {
                                _acquisizioneLockInCorso = false;
                                acquisito = null; // il coordinator ha concluso il ciclo, anche se esaurito; niente secondo cleanup.
                                _percorsoLockSessioneAcquisita = null;
                                _sessioneLockAcquisita = null;
                            }
                        }

                        return risultato;
                    }
                    catch
                    {
                        lock (_statoAcquisizioneLockGate) _acquisizioneLockInCorso = false;
                        throw;
                    }
                });
                if (_disposto) return (null, false);
                if (!r.Acquisito)
                {
                    var statoLock = await LeggiLockAltroAsync(percorso);
                    if (_disposto) return (null, false);
                    ApplicaStatoLock(statoLock);
                    return (null, false);
                }
                if (_disposto) return (null, false);
                AvviaHeartbeat();
            }

            // Il test può essere cambiato su disco (sync di un'altra postazione) dopo l'apertura in sola lettura:
            // senza rilettura si modificherebbe una copia vecchia e il Salva finirebbe subito in conflitto. Solo se
            // non ci sono modifiche locali non salvate (altrimenti resta il percorso normale di conflitto/merge).
            // Una rilettura fallita non viene ignorata: si modificherebbe una copia non verificata.
            if (!IsNew && _salvatoSuDisco && !ModificheNonSalvate && _snapshot != null)
            {
                var riferimento = TestCase;
                var revisioneVista = _snapshot.RevisioneDati;
                var aggiornato = await Task.Run(() => _state.TestCases.LoadCurrent(riferimento));
                if (_disposto) return (null, false);
                if (aggiornato != null && aggiornato.RevisioneDati != revisioneVista)
                {
                    ApplicaMergeNelForm(aggiornato, aggiornato);
                    CaricaPannelloAnomalie();
                    ModificheNonSalvate = false;
                }
            }
            LockInfo? lockAltraPostazione;
            try
            {
                lockAltraPostazione = await RilevaLockAltraPostazioneAsync();
            }
            catch (Exception ex)
            {
                // Il banner è informativo: un errore di lettura non deve annullare l'apertura in modifica né
                // far rilasciare un lock valido appena acquisito.
                CrashLogService.Registra("Lettura banner lock in apertura test", ex);
                ErroriValidazione = new List<string>
                {
                    string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message)
                };
                lockAltraPostazione = null;
            }
            if (_disposto) return (null, false);
            RevisioneVisualizzata = RevisioneCorrente; // mai iniziare a modificare sopra una revisione storica in sola lettura
            // Avviso non bloccante: un'altra sessione con lo stesso operatore può restare visibile via sync.
            ApplicaAvvisoAltraPostazione(lockAltraPostazione);
            if (abilitaModifica) InModifica = true;
            avvioCompletato = true;
            return (acquisito, true);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Apertura modifica test (IniziaModifica)", ex);
            if (!_disposto)
                ErroriValidazione = new List<string> { string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message) };
            return (null, false);
        }
        finally
        {
            // Lock acquisito da questo tentativo ma apertura non completata: rilascia solo la sua sessione.
            if (acquisito != null && !avvioCompletato)
            {
                FermaHeartbeat();
                var esitoRilascio = await RilasciaLockDelTentativoAsync(acquisito, percorso!);
                if (esitoRilascio != EsitoIo.Ok)
                    SegnalaRilascioLockInAttesa(acquisito, percorso!, tornaAllaLista: false);
            }
        }
    }

    /// <summary>Rilascia il lock acquisito da un singolo tentativo di modifica, legato alla sua sessione.</summary>
    private async Task<EsitoIo> RilasciaLockDelTentativoAsync(LockResult tentativo, string percorso)
    {
        var sessione = tentativo.DetenutoDa?.SessionId;
        if (string.IsNullOrEmpty(sessione)) return EsitoIo.Ok;
        // Il task statico possiede tutto il cleanup e trattiene il gate del path anche durante i backoff.
        var esito = await LockGate.RilasciaSessioneEsattaConRetryAsync(
            _state.Locks, percorso, sessione, RilascioSessioneLockPerTest, AttesaRetryLockPerTest);
        if (esito == EsitoIo.Ok)
        {
            lock (_statoAcquisizioneLockGate)
            {
                if (StringComparer.Ordinal.Equals(_sessioneLockAcquisita, sessione) &&
                    StringComparer.OrdinalIgnoreCase.Equals(_percorsoLockSessioneAcquisita, percorso))
                {
                    _percorsoLockSessioneAcquisita = null;
                    _sessioneLockAcquisita = null;
                }
                if (StringComparer.Ordinal.Equals(_sessioneRilascioLockInAttesa, sessione) &&
                    StringComparer.OrdinalIgnoreCase.Equals(_percorsoRilascioSessioneLockInAttesa, percorso))
                {
                    _percorsoRilascioSessioneLockInAttesa = null;
                    _sessioneRilascioLockInAttesa = null;
                }
            }
        }
        return esito;
    }

    private void SegnalaRilascioLockInAttesa(LockResult tentativo, string percorso, bool tornaAllaLista)
    {
        FermaHeartbeat();
        InModifica = false;
        lock (_statoAcquisizioneLockGate)
        {
            _percorsoRilascioSessioneLockInAttesa = percorso;
            _sessioneRilascioLockInAttesa = tentativo.DetenutoDa?.SessionId;
        }
        RilascioLockInAttesa = true;
        _rilascioLockDopoSalvataggio = false;
        _tornaAllaListaDopoRilascio = tornaAllaLista;
        MessaggioRilascioLock = LocalizationService.Instance.Traduci("LockSbloccoNonRiuscito");
    }

    /// <summary>Chiamato dalla finestra di visualizzazione di un test correlato quando l'utente chiede di
    /// modificarlo davvero (feedback utente 2026-08-21): scarta questa istanza (il test aperto nella finestra
    /// principale, comprese eventuali modifiche non salvate) e passa la finestra principale a modificare
    /// testCaseId — la stessa identica pipeline di salvataggio/nuova revisione di un'apertura normale, non
    /// serve nessuna logica di persistenza aggiuntiva. Sempre confermato con un pop-up dedicato, non il
    /// generico "modifiche non salvate": qui si abbandona l'intera sessione di lavoro sul test corrente, non
    /// solo qualche campo non salvato.</summary>
    public async Task<bool> RichiediModificaCorrelato(string testCaseId)
    {
        if (_navigaATestInModifica == null) return false;
        var loc = LocalizationService.Instance;
        if (!await ConfirmService.Chiedi(loc.Traduci("ApriCorrelatoInModificaConfermaTitolo"), loc.Traduci("ApriCorrelatoInModificaConfermaBody"),
                loc.Traduci("SiProcediSegnalato"), loc.Traduci("Annulla")))
            return false;
        FermaHeartbeat(); // prima del Release: vedi _sessioneModificaAttiva
        await RilasciaLockSessioneAttivaAsync();
        _navigaATestInModifica(testCaseId);
        return true;
    }

    /// <summary>Nuova istanza indipendente per la finestra di visualizzazione di un test correlato (feedback
    /// utente 2026-08-21): stesso motore/editor, di sola visualizzazione per default (nessun lock finché non
    /// si chiede la modifica, vedi IniziaModifica/richiediModificaEsterna) — la navigazione interna alla
    /// finestra (torna alla lista, apri un altro test) resta no-op, non ha senso dentro una finestra secondaria
    /// pensata per restare aperta accanto a quella principale.</summary>
    public TestCaseEditorViewModel CreaVisualizzatoreCorrelato(string testCaseId, Func<Task> richiediModificaEsterna) =>
        new(_state, testCaseId, () => { }, _ => { }, richiediModificaEsterna: richiediModificaEsterna);

    /// <summary>Rompe la modifica in corso di qualcun altro, non la propria: senza conferma un click impulsivo
    /// rischia di far perdere lavoro a un collega, non a chi clicca.</summary>
    [RelayCommand]
    private Task ForzaSblocco() =>
        ComandoAsincrono.EseguiAsync(ForzaSbloccoCoreAsync, "Sblocco forzato del Test", AggiungiAvvisoErrore);

    private async Task ForzaSbloccoCoreAsync()
    {
        var info = LockInfoTesto;
        if (!await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("ForzaSbloccoTitoloConferma"),
            info != null ? string.Format(LocalizationService.Instance.Traduci("ForzaSbloccoConInfo"), info)
                         : LocalizationService.Instance.Traduci("ForzaSbloccoSenzaInfo"),
            LocalizationService.Instance.Traduci("ForzaSbloccoBtn"), LocalizationService.Instance.Traduci("Annulla"))) return;

        if (PercorsoFile != null) _state.Locks.ForceUnlock(PercorsoFile);
        AggiornaStatoLock();
    }

    // ----- Avvisi -----

    private void AggiornaAvvisi()
    {
        var lista = new List<string>();
        if (!_state.CartellaValida) { Avvisi = lista; return; }

        if (TestCase.Esecuzioni.Count > 0)
        {
            var ultima = TestCase.Esecuzioni[^1];

            var requisiti = _state.Master.Requisiti.Load();
            foreach (var rid in TestCase.RequisitoId)
            {
                var req = requisiti.FirstOrDefault(r => r.Id == rid);
                if (req != null && req.UltimaModifica > ultima.Data)
                    lista.Add(string.Format(LocalizationService.Instance.Traduci("AvvisoRequisitoModificatoFmt"), rid));
            }

            var baselinePiuRecente = _state.TestCases.GetBaselinePiuRecente(TestCase.Componente, TestCase.Id)?.Esecuzioni[^1].BaselineSw;
            if (!string.IsNullOrEmpty(baselinePiuRecente) && baselinePiuRecente != ultima.BaselineSw)
                lista.Add(string.Format(LocalizationService.Instance.Traduci("AvvisoBaselineRecenteFmt"), TestCase.Componente, baselinePiuRecente));
        }

        if (!IsNew && TestCase.ModificatoDa != _state.Config.OperatoreAttivo && (DateTime.Now - TestCase.UltimaModifica) < TimeSpan.FromHours(1))
            lista.Add(string.Format(LocalizationService.Instance.Traduci("AvvisoModificatoPocoFaFmt"), TestCase.ModificatoDa, TestCase.UltimaModifica));

        if (TestCase.TestCorrelati.Any(id => _state.TestCases.GetById(id) is { Eliminato: true }))
            lista.Add(LocalizationService.Instance.Traduci("AvvisoCorrelatiEliminati"));

        if (TestCase.FuoriCategoria)
            lista.Add(LocalizationService.Instance.Traduci("AvvisoFuoriCategoria"));

        Avvisi = lista;
        AggiornaAvvisoPoliticaDurata();
    }

    /// <summary>Avviso non bloccante quando il file della politica durata non si legge: la durata resta facoltativa finche' il Master non
    /// ripara il file. Sparisce appena la politica torna leggibile; non tocca gli altri avvisi.</summary>
    private void AggiornaAvvisoPoliticaDurata()
    {
        var messaggio = LocalizationService.Instance.Traduci("PoliticaDurataIllegibileAvviso");
        var altri = Avvisi.Where(a => a != messaggio).ToList();
        Avvisi = _state.PoliticaDurata.ErroreCaricamento != null ? altri.Append(messaggio).ToList() : altri;
    }

    // ----- Scoping Gamma / eccezioni (Addendum §17.3) -----

    private void ValutaFuoriCategoria()
    {
        if (!_state.CartellaValida || TestCase.VeicoliEffettivi.Count == 0) { MessaggioFuoriCategoria = null; return; }

        var operatoreRilevanteNome = !string.IsNullOrEmpty(AssegnatarioSelezionato) ? AssegnatarioSelezionato : _state.Config.OperatoreAttivo;
        var operatoreRilevante = _state.Master.Operatori.Load().FirstOrDefault(o => o.Nome == operatoreRilevanteNome);
        if (operatoreRilevante == null || operatoreRilevante.GammeAssegnate.Count == 0) { MessaggioFuoriCategoria = null; return; }

        var fuori = _state.Master.Veicoli.Load().Where(v => TestCase.VeicoliEffettivi.Contains(v.Nome, StringComparer.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(v.GammaNome) && !operatoreRilevante.GammeAssegnate.Contains(v.GammaNome)).ToList();
        if (fuori.Count > 0)
        {
            var dettaglio = string.Join(", ", fuori.Select(v => $"{v.Nome} ({v.GammaNome})"));
            MessaggioFuoriCategoria = !string.IsNullOrEmpty(AssegnatarioSelezionato) && AssegnatarioSelezionato != _state.Config.OperatoreAttivo
                ? $"{operatoreRilevanteNome}: veicoli fuori categoria: {dettaglio}"
                : $"Veicoli fuori categoria: {dettaglio}";
        }
        else
        {
            MessaggioFuoriCategoria = null;
        }
    }

    // ----- Rilevamento duplicati / correlati (§6.2/§6.3) -----

    [RelayCommand]
    private void ValutaCandidati()
    {
        if (!IsNew || !_state.CartellaValida || TestCase.Componente.Count == 0) { CandidatiIterazione = new(); CandidatiCorrelati = new(); return; }
        var archivio = _state.TestCases.GetAll();
        var baselineSw = RevisioneCorrente?.BaselineSw ?? "";
        var baselineHw = RevisioneCorrente?.BaselineHw ?? "";
        CandidatiIterazione = DuplicateDetectionService.TrovaCandidatiIterazione(
            archivio, TestCase.Componente.AsEnumerable(), TestCase.RequisitoId, TestCase.Funzionalita, TestCase.TestTemplateUid, baselineSw, baselineHw, TestCase.Id);
        CandidatiCorrelati = DuplicateDetectionService.TrovaCandidatiCorrelati(
            archivio, TestCase.Componente.AsEnumerable(), TestCase.RequisitoId, TestCase.Funzionalita, TestCase.TestTemplateUid, TestCase.Id);
    }

    [RelayCommand]
    private void ApriCandidato(TestCase? candidato)
    {
        if (candidato == null) return;
        _tornaAllaLista(); // torna alla lista; l'utente riapre il candidato da lì per aggiungere l'iterazione
    }

    [RelayCommand]
    private void AggiungiCorrelato(TestCase? candidato)
    {
        if (candidato == null || TestCase.TestCorrelati.Contains(candidato.Id)) return;
        TestCase.TestCorrelati.Add(candidato.Id);
        TestCorrelatiVisual.Add(candidato.Id);
        CandidatiCorrelati = CandidatiCorrelati.Where(c => c.Id != candidato.Id).ToList();
        ModificheNonSalvate = true;
    }

    // ----- Picker (SelezionaDialogService): sostituisce le liste sempre visibili per i campi con elenchi
    // potenzialmente enormi (feedback utente 2026-08-20) — un pulsante/valore selezionato al posto di
    // TextBox+ListBox permanenti, che allungavano ogni riga del form anche a riposo. -----

    private static VoceSelezionabile VoceStringa(string s) => new() { Valore = s, TestoPrincipale = s, TestoRicerca = s };

    private static VoceSelezionabile VoceTest(TestCase t) => new()
    {
        Valore = t, TestoPrincipale = $"{t.Componente} · {t.Id}", TestoSecondario = t.DescrizioneBreve,
        TestoRicerca = $"{t.Id} {t.Componente} {t.DescrizioneBreve}"
    };

    [RelayCommand]
    private async Task ScegliEcu()
    {
        var scelte = await SelezionaDialogService.ScegliMultipli(LocalizationService.Instance.Traduci("ScegliEcuTitolo"), EcuDisponibili.Select(VoceStringa).ToList());
        foreach (var scelta in scelte) AggiungiEcu((string)scelta.Valore);
    }

    [RelayCommand]
    private async Task ScegliVeicolo()
    {
        var voce = await SelezionaDialogService.Scegli(LocalizationService.Instance.Traduci("ScegliVeicoloTitolo"), VeicoliDisponibili.Select(VoceStringa).ToList());
        if (voce != null) VeicoloSelezionato = (string)voce.Valore;
    }

    /// <summary>Cambio di esito di un'operazione, chiamato dalla View a ogni modifica dell'Esito. Il pannello Anomalie si
    /// ricostruisce SUBITO con il nuovo esito: il dialogo "Cambio esito" che segue e' modale e attende l'utente, e prima la
    /// ricostruzione aspettava la sua risposta, cosi' la segnalazione nata in sessione restava nel pannello fino al Salva.
    /// Se la risposta svuota i testi o riporta l'esito precedente, il pannello si ricostruisce di nuovo.</summary>
    public async Task GestisciCambioEsitoCondizione(Condizione condizione)
    {
        SincronizzaEsitoCondizioni();
        if (condizione.Esito != Esito.Conforme || string.IsNullOrWhiteSpace(condizione.MotivazioneNonApplicabile)
            && string.IsNullOrWhiteSpace(condizione.DescrizioneDeviazione) && string.IsNullOrWhiteSpace(condizione.DescrizioneProblema)) return;
        var conferma = await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("TcCambioEsitoTitolo"), LocalizationService.Instance.Traduci("TcEsitoConformeMotivazioneMsg"), LocalizationService.Instance.Traduci("TcRimuoviBtn"), LocalizationService.Instance.Traduci("TcMantieniEsitoPrecedenteBtn"));
        if (conferma)
        {
            condizione.MotivazioneNonApplicabile = "";
            condizione.DescrizioneDeviazione = "";
            condizione.DescrizioneProblema = "";
        }
        else condizione.Esito = condizione.EsitoPrecedente;
        SincronizzaEsitoCondizioni();
    }

    [RelayCommand]
    private void RimuoviVeicolo(string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) return;
        TestCase.Veicoli.RemoveAll(v => v.Equals(nome, StringComparison.OrdinalIgnoreCase));
        VeicoliSelezionatiVisual.Remove(nome);
        TestCase.NormalizzaVeicoli();
        VeicoloSelezionato = "";
        OnPropertyChanged(nameof(EcuDisponibili));
        OnPropertyChanged(nameof(FunzionalitaDisponibili));
        ValutaFuoriCategoria();
        ModificheNonSalvate = true;
    }

    [RelayCommand]
    private void SelezionaGamma(string? gamma)
    {
        if (string.IsNullOrWhiteSpace(gamma)) return;
        foreach (var v in _tuttiIVeicoli.Where(v => v.GammaNome.Equals(gamma, StringComparison.OrdinalIgnoreCase) && !v.Archiviata))
            if (!TestCase.Veicoli.Contains(v.Nome, StringComparer.OrdinalIgnoreCase))
            {
                TestCase.Veicoli.Add(v.Nome);
                VeicoliSelezionatiVisual.Add(v.Nome);
            }
        TestCase.NormalizzaVeicoli();
        OnPropertyChanged(nameof(EcuDisponibili));
        OnPropertyChanged(nameof(FunzionalitaDisponibili));
        ValutaFuoriCategoria();
        ModificheNonSalvate = true;
    }

    // Gamme memorizzate: il catalogo veicoli cambia solo quando _tuttiIVeicoli viene riassegnata dal ricaricamento
    // (RicaricaListeMaster); il binding le legge a ogni notifica e non le deve ricostruire.
    private List<Veicolo>? _gammeSorgente;
    private List<string> _gammeDisponibili = new();

    public List<string> GammeDisponibili
    {
        get
        {
            if (!ReferenceEquals(_gammeSorgente, _tuttiIVeicoli))
            {
                _gammeDisponibili = _tuttiIVeicoli.Where(v => !v.Archiviata && !string.IsNullOrWhiteSpace(v.GammaNome))
                    .Select(v => v.GammaNome).Distinct().OrderBy(x => x).ToList();
                _gammeSorgente = _tuttiIVeicoli;
            }
            return _gammeDisponibili;
        }
    }

    /// <summary>Picker con ricerca per i campi Lista di strumenti/template con più di 10 opzioni (feedback
    /// utente 2026-08-21, punto 2): stesso meccanismo già usato per Componente/Veicolo/Template, riapplicato ai campi
    /// dinamici del form dove una ComboBox pura diventa scomoda con tante voci. Le liste con 10 opzioni o meno
    /// restano una ComboBox normale (vedi ListaOltreSogliaConverter/ListaEntroSogliaConverter in Converters.cs)
    /// per non introdurre un pulsante dove non serve.</summary>
    [RelayCommand]
    private async Task ScegliOpzioneLista(CampoStrumentoValore? campo)
    {
        if (campo == null) return;
        var voce = await SelezionaDialogService.Scegli(campo.Nome, campo.OpzioniVisualizzate.Select(VoceStringa).ToList());
        if (voce != null) campo.Valore = (string)voce.Valore;
    }

    [RelayCommand]
    private async Task ScegliTemplate()
    {
        var voci = TemplatesDisponibili.Select(t => new VoceSelezionabile { Valore = t, TestoPrincipale = t.Nome, TestoRicerca = t.Nome }).ToList();
        var voce = await SelezionaDialogService.Scegli(LocalizationService.Instance.Traduci("ScegliTipoTestTitolo"), voci);
        if (voce != null) TemplateSelezionato = (TestTemplate)voce.Valore;
    }

    [RelayCommand]
    private async Task ScegliTestVincolo()
    {
        var voce = await SelezionaDialogService.Scegli(LocalizationService.Instance.Traduci("ScegliTestCollegatoTitolo"), TestVincoloDisponibili.Select(VoceTest).ToList());
        if (voce != null) TestVincoloSelezionato = (TestCase)voce.Valore;
    }

    [RelayCommand]
    private async Task ScegliAssegnatario()
    {
        var voce = await SelezionaDialogService.Scegli(LocalizationService.Instance.Traduci("ScegliAssegnatarioTitolo"), OperatoriDisponibili.Select(VoceStringa).ToList());
        if (voce != null) AssegnatarioSelezionato = (string)voce.Valore;
    }

    [RelayCommand]
    private async Task ScegliRevisioneOperatore()
    {
        var voce = await SelezionaDialogService.Scegli(LocalizationService.Instance.Traduci("ScegliOperatoreTitolo"), OperatoriDisponibili.Select(VoceStringa).ToList());
        if (voce != null) RevisioneCorrenteOperatore = (string)voce.Valore;
    }

    [RelayCommand]
    private async Task ScegliRequisito()
    {
        var voci = RequisitiDisponibili.Select(r => new VoceSelezionabile { Valore = r, TestoPrincipale = r.Id, TestoSecondario = r.Testo, TestoRicerca = $"{r.Id} {r.Testo}" }).ToList();
        var scelti = await SelezionaDialogService.ScegliMultipli(LocalizationService.Instance.Traduci("ScegliRequisitiTitolo"), voci);
        // Selezione multipla (feedback utente 2026-08-20): niente più un secondo click su "Aggiungi" per
        // ciascuna voce, si scelgono tutte insieme nel dialog e si aggiungono qui in un solo passaggio —
        // riusa AggiungiRequisito() (già testato) invece di duplicarne la logica.
        foreach (var voce in scelti)
        {
            RequisitoSelezionato = (Requisito)voce.Valore;
            AggiungiRequisito();
        }
    }

    [RelayCommand]
    private async Task ScegliFunzionalita()
    {
        var scelti = await SelezionaDialogService.ScegliMultipli(LocalizationService.Instance.Traduci("ScegliFunzionalitaTitolo"), FunzionalitaDisponibili.Select(VoceStringa).ToList());
        foreach (var voce in scelti)
        {
            NuovaFunzionalita = (string)voce.Valore;
            AggiungiFunzionalita();
        }
    }

    [RelayCommand]
    private async Task ScegliTestCorrelato()
    {
        var scelti = await SelezionaDialogService.ScegliMultipli(LocalizationService.Instance.Traduci("ScegliTestCorrelatiTitolo"), TestCorrelatiDisponibili.Select(VoceTest).ToList());
        foreach (var voce in scelti)
        {
            TestCorrelatoSelezionato = (TestCase)voce.Valore;
            AggiungiTestCorrelato();
        }
    }

    [RelayCommand]
    private async Task ScegliAnomalia(SegnalazioneTestRiga? riga)
    {
        if (riga == null) return;
        var voci = riga.AnomalieCandidate.Select(n => new VoceSelezionabile { Valore = n, TestoPrincipale = n.Id, TestoSecondario = n.Descrizione, TestoRicerca = $"{n.Id} {n.Descrizione} {n.Owner}" }).ToList();
        var voce = await SelezionaDialogService.Scegli(LocalizationService.Instance.Traduci("ScegliAnomaliaTitolo"), voci);
        if (voce != null) riga.AnomaliaSelezionata = (NonConformita)voce.Valore;
    }

    // ----- Liste editabili -----

    [RelayCommand]
    private void AggiungiRequisito()
    {
        var id = RequisitoSelezionato?.Id;
        if (string.IsNullOrWhiteSpace(id) || TestCase.RequisitoId.Contains(id)) return;
        TestCase.RequisitoId.Add(id);
        NotificaDtcOpzioni();
        RequisitiVisual.Add(id);
        RequisitoSelezionato = null;
        ModificheNonSalvate = true;
        ValutaCandidati();
        if (RevisioneCorrente != null)
        {
            PunteggioConformitaService.SincronizzaPunteggi(RevisioneCorrente, TestCase.RequisitoId);
            AggiornaPunteggiRequisitiVisual();
        }
    }
    [RelayCommand]
    private void RimuoviRequisito(string? id)
    {
        if (id == null) return;
        TestCase.RequisitoId.Remove(id);
        NotificaDtcOpzioni();
        RequisitiVisual.Remove(id);
        ModificheNonSalvate = true;
        ValutaCandidati();
        if (RevisioneCorrente != null)
        {
            PunteggioConformitaService.SincronizzaPunteggi(RevisioneCorrente, TestCase.RequisitoId);
            AggiornaPunteggiRequisitiVisual();
        }
    }

    [RelayCommand]
    private void AggiungiFunzionalita()
    {
        if (string.IsNullOrWhiteSpace(NuovaFunzionalita) || TestCase.Funzionalita.Contains(NuovaFunzionalita)) return;
        var valore = NuovaFunzionalita.Trim();
        TestCase.Funzionalita.Add(valore);
        FunzionalitaVisual.Add(valore);
        // L'ItemsSource del ComboBox per-operazione va aggiornato PRIMA di riallineare Condizione.Funzionalita:
        // il SelectedItem (Mode=OneWay) risolve il nuovo valore contro l'ItemsSource corrente, e se questa
        // contiene ancora la lista precedente il match fallisce e la selezione resta vuota a video (il modello
        // è comunque corretto, si "ripara" solo al prossimo rebuild completo di CondizioniVisual).
        OnPropertyChanged(nameof(FunzionalitaOperazioniDisponibili));
        RiallineaFunzionalitaOperazioniDopoCambioElenco();
        AllineaOperazioniDopoFunzionalitaAggiunta();
        NuovaFunzionalita = "";
        ModificheNonSalvate = true;
        ValutaCandidati();
        AggiornaSuggerimentoCronologia();
    }
    [RelayCommand]
    private async Task RimuoviFunzionalita(string? nome)
    {
        if (nome == null) return;
        // Le operazioni "Da eseguire" degli strumenti ma con dati gia compilati non si cancellano in silenzio: conferma con l'elenco di cio che si perde.
        var daEliminare = OperazioniStrumentoDiFunzionalita(nome).Where(x => DtcOperazioneRegole.PuoDeselezionare(x.Ops)).SelectMany(x => x.Ops).ToList();
        if (daEliminare.Count > 0 && !await ConfermaPerditaDatiAsync(daEliminare)) return;
        TestCase.Funzionalita.Remove(nome);
        FunzionalitaVisual.Remove(nome);
        OnPropertyChanged(nameof(FunzionalitaOperazioniDisponibili));
        RimuoviOperazioniStrumentoDiFunzionalita(nome);
        if (RevisioneCorrente != null)   // le operazioni di proprieta di uno strumento (rimaste perche con esito) conservano la loro funzionalita
            foreach (var condizione in RevisioneCorrente.Condizioni.Where(c => string.IsNullOrWhiteSpace(c.Strumento) && c.Funzionalita.Equals(nome, StringComparison.OrdinalIgnoreCase)))
                condizione.Funzionalita = FunzionalitaAutoSeSingola;
        RiallineaFunzionalitaOperazioniDopoCambioElenco();
        ModificheNonSalvate = true;
        ValutaCandidati();
        AggiornaSuggerimentoCronologia();
    }

    private void RiallineaFunzionalitaOperazioniDopoCambioElenco()
    {
        if (RevisioneCorrente != null && TestCase.Funzionalita.Count == 1)
            foreach (var condizione in RevisioneCorrente.Condizioni.Where(c => string.IsNullOrWhiteSpace(c.Funzionalita)))
                condizione.Funzionalita = FunzionalitaAutoSeSingola;
        OnPropertyChanged(nameof(FunzionalitaOperazioneObbligatoria));
    }

    [RelayCommand]
    private void AggiungiEcu(string? valore)
    {
        if (string.IsNullOrWhiteSpace(valore) || TestCase.Componente.Contains(valore, StringComparer.OrdinalIgnoreCase)) return;
        var vecchioElenco = new EcuSelection(TestCase.Componente);
        TestCase.Componente.Add(valore.Trim());
        NotificaDtcOpzioni();
        EcuVisual.Add(valore.Trim());
        if (string.IsNullOrWhiteSpace(TestCase.ContestoPredefinito.Componente)) TestCase.ContestoPredefinito.Componente = valore.Trim();
        RiallineaEcuOperazioniDopoCambioElenco(vecchioElenco);
        ValutaCandidati();
        ModificheNonSalvate = true;
        OnPropertyChanged(nameof(TestRichiedeEcu));
        OnPropertyChanged(nameof(PreparaPolarionTooltip));
        OnPropertyChanged(nameof(FunzionalitaDisponibili));
        NotificaCoperturaCambiata();
    }
    [RelayCommand]
    private void RimuoviEcu(string? nome)
    {
        if (nome == null) return;
        TestCase.Componente.RemoveAll(e => e.Equals(nome, StringComparison.OrdinalIgnoreCase));
        NotificaDtcOpzioni();
        EcuVisual.Remove(nome);
        if (TestCase.ContestoPredefinito.Componente.Equals(nome, StringComparison.OrdinalIgnoreCase))
            TestCase.ContestoPredefinito.Componente = TestCase.Componente.FirstOrDefault() ?? "";
        // Un componente rimossa dal test non deve restare come scelta pendente su un'operazione (feedback utente
        // 2026-08-25, punto 9): sarebbe un riferimento a un componente non più collegata al test. Riassegnata (non
        // filtrata in-place) perché Componente ha ora un setter con notifica (vedi Condizione.Componente) — una RemoveAll
        // diretta sulla lista esistente non farebbe scattare l'aggiornamento del pulsante componente a video.
        if (RevisioneCorrente != null)
            foreach (var condizione in RevisioneCorrente.Condizioni.Where(c => c.Componente.Contains(nome, StringComparison.OrdinalIgnoreCase)))
                condizione.Componente = new EcuSelection(condizione.Componente.Where(e => !e.Equals(nome, StringComparison.OrdinalIgnoreCase)));
        ValutaCandidati();
        ModificheNonSalvate = true;
        OnPropertyChanged(nameof(TestRichiedeEcu));
        OnPropertyChanged(nameof(FunzionalitaDisponibili));
        NotificaCoperturaCambiata();
    }

    /// <summary>Le operazioni ancora "di default" (vuote — create prima che il test avesse Componenti selezionati —
    /// oppure ancora esattamente uguali a Tutti i componenti del test prima di quest'aggiunta) seguono l'elenco componente
    /// del test man mano che cresce, stesso principio di RiallineaFunzionalitaOperazioniDopoCambioElenco: se il
    /// test parte con una sola componente e poi ne aggiunge una seconda, un'operazione mai toccata a mano deve
    /// includere anche la nuova, non restare ferma alla prima. Un'operazione già ristretta dall'utente a un
    /// sottoinsieme diverso dal "tutte" precedente non viene mai toccata qui.</summary>
    private void RiallineaEcuOperazioniDopoCambioElenco(EcuSelection vecchioElenco)
    {
        if (RevisioneCorrente == null) return;
        foreach (var condizione in RevisioneCorrente.Condizioni)
        {
            var seguivaDefault = condizione.Componente.Count == 0
                || (condizione.Componente.Count == vecchioElenco.Count && condizione.Componente.All(e => vecchioElenco.Contains(e, StringComparison.OrdinalIgnoreCase)));
            if (seguivaDefault) condizione.Componente = EcuAutoSuNuovaOperazione;
        }
    }

    [RelayCommand]
    private void AggiungiCampoCustom()
    {
        if (string.IsNullOrWhiteSpace(NuovoCampoCustomChiave)) return;
        var chiave = NuovoCampoCustomChiave.Trim();
        TestCase.CampiCustom[chiave] = NuovoCampoCustomValore;
        var esistente = CampiCustomVisual.FirstOrDefault(c => c.Chiave == chiave);
        if (esistente != null) esistente.Valore = NuovoCampoCustomValore;
        else CampiCustomVisual.Add(new CampoCustomRiga { Chiave = chiave, Valore = NuovoCampoCustomValore });
        NuovoCampoCustomChiave = ""; NuovoCampoCustomValore = "";
        ModificheNonSalvate = true;
    }
    [RelayCommand]
    private void RimuoviCampoCustom(string? chiave)
    {
        if (chiave == null) return;
        TestCase.CampiCustom.Remove(chiave);
        var riga = CampiCustomVisual.FirstOrDefault(c => c.Chiave == chiave);
        if (riga != null) CampiCustomVisual.Remove(riga);
        ModificheNonSalvate = true;
    }

    /// <summary>Solo un Master può gestire il catalogo campi di sistema (§4 della guida), stesso permesso di
    /// PuoGestireStrumenti sopra.</summary>
    public bool PuoPromuovereCampiCustom => _state.EMaster;

    /// <summary>"Promuovi nel catalogo" (Addendum: audit UX 2026-08-26, nota minore 3 — "due percorsi diversi
    /// per i campi custom su un test... un valore inserito lì non comparirà mai come filtro Home/Report né nel
    /// catalogo", §18.12 della guida operativa, debito noto e deliberato non ancora risolto). Crea un campo
    /// libero (Ruolo=Nessuno) nel catalogo Dettagli con lo stesso nome e già marcato "usabile come filtro" —
    /// la stessa operazione che un Master farebbe a mano da Impostazioni → Tipi di test → Gestisci campi, solo
    /// senza dover uscire dal test per farla. NON tocca né migra il valore già scritto su questo test: la
    /// promozione riguarda il catalogo, il valore resta comunque solo in TestCase.CampiCustom finché qualcuno
    /// non lo riscrive nel nuovo campo — stessa scelta deliberata già documentata (§18.12), qui solo resa più
    /// facile da correggere quando serve davvero, non automatizzata silenziosamente.</summary>
    [RelayCommand]
    private async Task PromuoviCampoCustom(string? chiave)
    {
        TaskCompletionSource operazione;
        lock (_statoAcquisizioneLockGate)
        {
            if (chiave == null || _disposto || _chiusuraInCorso || !_state.EMaster || !_state.CartellaValida ||
                RilascioLockCatalogoInAttesa || _promozioneCampoCustomInCorsoPerUscita != null) return;
            operazione = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _promozioneCampoCustomInCorsoPerUscita = operazione;
        }

        try { await PromuoviCampoCustomCoreAsync(chiave); }
        finally
        {
            lock (_statoAcquisizioneLockGate)
            {
                if (ReferenceEquals(_promozioneCampoCustomInCorsoPerUscita, operazione))
                    _promozioneCampoCustomInCorsoPerUscita = null;
            }
            operazione.TrySetResult();
        }
    }

    private async Task PromuoviCampoCustomCoreAsync(string chiave)
    {

        // Stesso lock di modifica dell'editor del catalogo (stesso file): senza, la lettura e la scrittura qui sotto
        // sovrascriverebbero le modifiche di un altro PC in corso sul catalogo. Errori e lock occupato: messaggio.
        var percorso = _state.Paths.CampiSistemaDettagliFile;
        var operatore = _state.Config.OperatoreAttivo;
        LockResult? risultato = null;
        var rilascioFallito = false;
        try
        {
            risultato = await LockGate.EseguiAsync(percorso, async () =>
            {
                if (_disposto) return new LockResult { Acquisito = false };
                return await Task.Run(() => _state.Locks.TryAcquireNuovaSessione(percorso, operatore))
                    .ConfigureAwait(false);
            });
            // La finestra può essere stata chiusa mentre il comando attendeva il gate/acquisiva il lock.
            // Non deve leggere o scrivere il catalogo dopo Dispose; il finally rilascia l'eventuale token.
            if (_disposto) return;
            if (!risultato.Acquisito)
            {
                ErroriValidazione = new List<string> { MessaggioCatalogoBloccato(risultato.DetenutoDa) };
                return;
            }

            var catalogo = _state.Master.CampiSistemaDettagli.Load();
            // Idempotente: promuovere due volte la stessa chiave non crea due voci duplicate nel catalogo, si
            // limita a garantire che quella già esistente sia marcata filtrabile.
            var esistente = catalogo.FirstOrDefault(c => c.Ruolo == RuoloCampoSistema.Nessuno && !c.Archiviata
                && c.Etichetta.Equals(chiave, StringComparison.OrdinalIgnoreCase));
            if (esistente != null)
            {
                if (!esistente.UsabileComeFiltro)
                {
                    esistente.UsabileComeFiltro = true;
                    _state.Master.CampiSistemaDettagli.Save(catalogo);
                }
            }
            else
            {
                var chiaviEsistenti = catalogo.Select(c => c.Chiave);
                catalogo.Add(new CampoSistemaDefinizione
                {
                    Chiave = CampoSistemaCatalogoService.GeneraChiaveDaEtichetta(chiave, chiaviEsistenti),
                    Etichetta = chiave,
                    Gruppo = GruppoCampoSistema.Dettagli,
                    UsabileComeFiltro = true,
                    ModificatoDa = _state.Config.OperatoreAttivo,
                    UltimaModifica = DateTime.Now,
                });
                _state.Master.CampiSistemaDettagli.Save(catalogo);
            }
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Promozione campo custom", ex);
            ErroriValidazione = new List<string> { string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoFmt"), ex.Message) };
            return;
        }
        finally
        {
            if (risultato?.Acquisito == true)
            {
                var sessione = risultato.DetenutoDa?.SessionId;
                if (!string.IsNullOrWhiteSpace(sessione))
                {
                    var esitoRilascio = await LockGate.RilasciaSessioneEsattaConRetryAsync(
                        _state.Locks, percorso, sessione, RilascioSessioneLockPerTest, AttesaRetryLockPerTest);
                    if (esitoRilascio != EsitoIo.Ok)
                    {
                        rilascioFallito = true;
                        CrashLogService.Registra(
                            "Rilascio lock dopo promozione campo custom fallito",
                            new IOException($"Rilascio sessione {sessione} fallito."));
                        _percorsoRilascioLockCatalogoInAttesa = percorso;
                        _sessioneRilascioLockCatalogoInAttesa = sessione;
                        RilascioLockCatalogoInAttesa = true;
                        MessaggioRilascioLockCatalogo = LocalizationService.Instance.Traduci("LockSbloccoNonRiuscito");
                    }
                }
            }
        }

        AggiornaCatalogoCampiSistema(); // il catalogo è stato scritto direttamente qui, non dal suo editor
        if (rilascioFallito)
        {
            ErroriValidazione = new List<string>
            {
                MessaggioRilascioLockCatalogo ?? LocalizationService.Instance.Traduci("SalvatoSbloccoLockNonRiuscito")
            };
            return;
        }

        await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("CampoPromossoTitolo"),
            string.Format(LocalizationService.Instance.Traduci("CampoPromossoBodyFmt"), chiave),
            LocalizationService.Instance.Traduci("OkBtn"), "");
    }

    private static string MessaggioCatalogoBloccato(LockInfo? detenutoDa) =>
        detenutoDa == null
            ? LocalizationService.Instance.Traduci("CatalogoBloccatoDaAltroPc")
            : string.Format(LocalizationService.Instance.Traduci("LockInModificaDaFmt"), detenutoDa.Operatore, detenutoDa.AcquisitoIl);

    [RelayCommand]
    private void AggiungiTestCorrelato()
    {
        var id = TestCorrelatoSelezionato?.Id;
        if (string.IsNullOrWhiteSpace(id) || TestCase.TestCorrelati.Contains(id)) return;
        TestCase.TestCorrelati.Add(id);
        TestCorrelatiVisual.Add(id);
        TestCorrelatoSelezionato = null;
        ModificheNonSalvate = true;
    }
    [RelayCommand]
    private void RimuoviTestCorrelato(string? id)
    {
        if (id == null) return;
        TestCase.TestCorrelati.Remove(id);
        TestCorrelatiVisual.Remove(id);
        ModificheNonSalvate = true;
    }

    [RelayCommand]
    private void AggiungiTicket()
    {
        if (string.IsNullOrWhiteSpace(NuovoTicketRiferimento)) return;
        var riferimento = NuovoTicketRiferimento.Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(riferimento, "^TICKET-[A-Z0-9_-]+$"))
        {
            ErroriValidazione = new List<string> { LocalizationService.Instance.Traduci("FormatoPolarionNonValido") };
            return;
        }
        if (!string.IsNullOrWhiteSpace(NuovoTicketUrl) && !Uri.TryCreate(NuovoTicketUrl.Trim(), UriKind.Absolute, out _))
        {
            ErroriValidazione = new List<string> { LocalizationService.Instance.Traduci("LinkPolarionNonValido") };
            return;
        }
        var ticket = new TicketRif
        {
            Riferimento = riferimento,
            Url = string.IsNullOrWhiteSpace(NuovoTicketUrl) ? PolarionTicketLinkService.DerivaUrl(_state.Paths, riferimento) : NuovoTicketUrl.Trim(),
            CollegatoIl = DateTime.Now,
            CollegatoDa = _state.Config.OperatoreAttivo
        };
        TestCase.TicketCollegati.Add(ticket);
        TicketCollegatiVisual.Add(ticket);
        DiagnosticTraceService.Registra("Test.TICKET.Reference", "added test=" + TestCase.Id +
            " ticket=" + riferimento);
        NuovoTicketRiferimento = "";
        NuovoTicketUrl = "";
        ModificheNonSalvate = true;
    }

    [RelayCommand]
    private void ApriTicket(TicketRif? ticket)
    {
        if (ticket == null || string.IsNullOrWhiteSpace(ticket.Url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ticket.Url) { UseShellExecute = true }); }
        catch { ErroriValidazione = new List<string> { LocalizationService.Instance.Traduci("ApriPolarionErrore") }; }
    }

    [RelayCommand]
    private void RimuoviTicket(TicketRif? ticket)
    {
        if (ticket == null) return;
        TestCase.TicketCollegati.Remove(ticket);
        TicketCollegatiVisual.Remove(ticket);
        DiagnosticTraceService.Registra("Test.TICKET.Reference", "removed test=" + TestCase.Id +
            " ticket=" + ticket.Riferimento);
        ModificheNonSalvate = true;
    }

    /// <summary>Sezioni proponibili nel dialogo di esportazione per il Tipo di test del test corrente: se il
    /// template non ne ha ristrette esplicitamente (TestTemplate.SezioniPolarionDisponibili vuoto) restano
    /// disponibili tutte, come prima (feedback utente 2026-08-18, punto 3). A queste si aggiunge sempre, in
    /// coda, una voce dinamica per ciascuna sezione custom del template (feedback utente 2026-08-21, punto
    /// 10): non narrowabili singolarmente dal Master ancora (solo il catalogo fisso qui sopra lo è), ma
    /// sempre selezionabili una per una invece di un unico bucket generico "Campi custom".</summary>
    public List<string> SezioniPolarionDisponibili()
    {
        var delTemplate = TemplateCorrente?.SezioniPolarionDisponibili;
        var fisse = delTemplate is { Count: > 0 } ? delTemplate : PolarionSezioni.Tutte.ToList();
        var dinamiche = TemplateCorrente?.Sezioni.Select(PolarionSezioni.ChiaveSezioneDinamica) ?? Enumerable.Empty<string>();
        return fisse.Concat(dinamiche).ToList();
    }

    /// <summary>Etichetta leggibile per una chiave PolarionSezioni: quelle fisse hanno una traduzione dedicata,
    /// quelle dinamiche (una per SezioneTemplate) mostrano direttamente il nome che il Master ha dato alla
    /// sezione — non esiste una chiave di localizzazione pre-registrata per un nome scelto a runtime.</summary>
    public string EtichettaSezionePolarion(string chiave) =>
        PolarionSezioni.EDinamica(chiave)
            ? TemplateCorrente?.Sezioni.FirstOrDefault(s => PolarionSezioni.ChiaveSezioneDinamica(s) == chiave)?.Nome ?? chiave
            : LocalizationService.Instance.Traduci("PolarionSezione" + chiave);

    /// <summary>Default condiviso delle sezioni preselezionate (Addendum §4, feedback QA): un Master lo tiene
    /// minimo, ogni operatore può comunque spuntarne altre nel dialogo di anteprima senza cambiarlo per tutti.
    /// Filtrato sulle sole sezioni disponibili per questo template, altrimenti un default condiviso più ampio
    /// del template corrente preselezionerebbe una sezione poi non proponibile.</summary>
    public List<string> SezioniPolarionDiDefault()
    {
        var condiviso = _state.CartellaValida ? PolarionConfigService.Load(_state.Paths).SezioniDiDefault : PolarionSezioni.Minime.ToList();
        var disponibili = SezioniPolarionDisponibili();
        return condiviso.Where(disponibili.Contains).ToList();
    }

    public string GeneraAnteprimaPolarion(HashSet<string> sezioni) =>
        TicketExportService.GeneraTesto(CreaSnapshotExportPolarion(), _state, sezioni);

    /// <summary>Fotografia coerente dell'editor per anteprima e pacchetto Ticketing. L'export legge il TestCase
    /// come modello; l'editor però mantiene selezione strumenti ed esecuzioni correnti nelle proprie collezioni
    /// fino al salvataggio. Copiamo questi valori senza persisterli né mutare il modello visualizzato.</summary>
    private TestCase CreaSnapshotExportPolarion()
    {
        var snapshot = Clona(TestCase);
        snapshot.StrumentiUsati = StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Valore).ToList();
        snapshot.Esecuzioni = Esecuzioni.Select(ClonaEsecuzione).ToList();
        return snapshot;
    }

    /// <summary>Esporta tutti i campi compilati del test in un .txt/.pdf/.zip pensato per essere allegato
    /// all'apertura di una Claim, e aggiunge subito un riferimento in Ticket collegati col riferimento reale
    /// da compilare appena la claim esiste (formato tipico TICKET-xxxxx). Disponibile su qualunque test già
    /// salvato, non solo in modifica (Addendum §3, feedback QA: prima richiedeva di entrare in modifica senza
    /// motivo, dato che non tocca il test). Su un test ancora nuovo, salva subito in automatico appena il componente è
    /// nota (AssicuraTestSalvato, stesso principio degli allegati) invece di richiedere un salvataggio manuale
    /// esplicito prima (feedback utente 2026-08-19, punto 2). Sezioni e cartella di destinazione li sceglie
    /// l'utente nei dialoghi di code-behind (OnCreaTicket): null qui significa "annullato".</summary>
    [RelayCommand]
    private void CreaTicket(RichiestaPacchettoPolarion? richiesta)
    {
        if (!_state.CartellaValida || richiesta == null || string.IsNullOrWhiteSpace(richiesta.Cartella)) return;
        if (!AssicuraTestSalvato()) return;
        string percorso;
        try
        {
            percorso = PolarionPackageService.Crea(_state.Paths, CreaSnapshotExportPolarion(), _state, richiesta.Cartella, richiesta.Sezioni);
        }
        catch (Exception ex)
        {
            ErroriValidazione = new List<string> { string.Format(LocalizationService.Instance.Traduci("EsportazioneTicketErroreFmt"), ex.Message) };
            return;
        }

        MessaggioTicket = string.Format(LocalizationService.Instance.Traduci("PacchettoZipProntoFmt"), Path.GetFileName(percorso));
    }

    /// <summary>Punto di partenza rapido per un test simile (Addendum §19): copia classificazione, strumenti e
    /// le condizioni dell'ultima revisione (esiti riportati a "non eseguito", tecnici sì) in un test NUOVO,
    /// senza toccare quello aperto — a differenza di AggiungiIterazione, che aggiunge una revisione allo stesso
    /// test. La matricola non si copia mai: identifica un veicolo/banco fisico specifico, copiarlo trarrebbe in
    /// inganno. Disponibile solo su un test già salvato (duplicare una bozza non ancora esistente non ha senso).</summary>
    [RelayCommand]
    private Task DuplicaTest() =>
        ComandoAsincrono.EseguiAsync(DuplicaTestCoreAsync, "Duplicazione del Test", AggiungiAvvisoErrore);

    private async Task DuplicaTestCoreAsync()
    {
        if (IsNew) return;
        if (ModificheNonSalvate && !await ConfirmService.ConfermaAbbandonoModifiche()) return;

        var bozza = new TestCase
        {
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
            ModificatoDa = _state.Config.OperatoreAttivo,
            CreatoDa = _state.Config.OperatoreAttivo,
            Componente = new EcuSelection(TestCase.Componente),
            Veicolo = TestCase.Veicolo,
            Veicoli = new List<string>(TestCase.VeicoliEffettivi),
            TestTemplateUid = TestCase.TestTemplateUid,
            Impegno = TestCase.Impegno,
            RequisitoId = new List<string>(TestCase.RequisitoId),
            Funzionalita = new List<string>(TestCase.Funzionalita),
            Descrizione = TestCase.Descrizione,
            StrumentiUsati = new List<string>(TestCase.StrumentiUsati),
            CampiCustom = new Dictionary<string, string>(TestCase.CampiCustom),
            Assegnatario = _state.Config.OperatoreAttivo
        };
        if (RevisioneCorrente != null)
        {
            bozza.Esecuzioni.Add(new Esecuzione
            {
                Iterazione = 1,
                Data = DateTime.Now,
                Operatore = _state.Config.OperatoreAttivo,
                Esito = Esito.DaEseguire,
                BaselineSw = RevisioneCorrente.BaselineSw,
                BaselineHw = RevisioneCorrente.BaselineHw,
                Condizioni = RevisioneCorrente.Condizioni.Select(c => new Condizione { Esito = Esito.DaEseguire, Componente = new EcuSelection(c.Componente), Funzionalita = c.Funzionalita, Guasto = c.Guasto, DtcRequisitoId = c.DtcRequisitoId, DtcRequisitoVuotoScelto = c.DtcRequisitoVuotoScelto, DtcPasso = c.DtcPasso, DtcVeicolo = c.DtcVeicolo, Strumento = c.Strumento, ValoriStrumenti = ClonaCampiStrumenti(c.ValoriStrumenti) }).ToList()
            });
        }
        _state.BozzaPerNuovoTest = bozza;
        _navigaATest(null);
    }

    /// <summary>Nuova iterazione = nuova revisione (Addendum §17.7): copia matricola/baseline/condizioni dalla
    /// revisione corrente così da non dover reinserire tutto — comprese le condizioni che restano stabili da
    /// un tentativo all'altro (Addendum §19) — editabili solo dove serve modificare qualcosa.</summary>
    [RelayCommand]
    private void AggiungiIterazione()
    {
        var precedente = RevisioneCorrente;
        var n = Esecuzioni.Count + 1;
        Esecuzioni.Add(new Esecuzione
        {
            Iterazione = n,
            Data = DateTime.Now,
            Operatore = _state.Config.OperatoreAttivo,
            Esito = Esito.DaEseguire,
            Matricola = precedente?.Matricola ?? "",
            BaselineSw = precedente?.BaselineSw ?? "",
            BaselineHw = precedente?.BaselineHw ?? "",
            ValoriStrumenti = precedente != null ? ClonaCampi(precedente.ValoriStrumenti) : new(),
            // Le condizioni (campi tecnici) si riportano pari pari — è il punto della funzione — ma l'esito
            // di ciascuna riparte da "non eseguito": una nuova iterazione è un nuovo tentativo da riverificare,
            // non un test già superato per definizione solo perché lo era nel tentativo precedente.
            Condizioni = precedente?.Condizioni.Select(c => new Condizione { Esito = Esito.DaEseguire, Componente = new EcuSelection(c.Componente), Funzionalita = c.Funzionalita, Guasto = c.Guasto, DtcRequisitoId = c.DtcRequisitoId, DtcRequisitoVuotoScelto = c.DtcRequisitoVuotoScelto, DtcPasso = c.DtcPasso, DtcVeicolo = c.DtcVeicolo, Strumento = c.Strumento, ValoriStrumenti = ClonaCampiStrumenti(c.ValoriStrumenti) }).ToList() ?? new(),
            Dettagli = CostruisciSnapshotDettagli()
        });
        // La durata non si copia pari pari: ogni operazione della nuova iterazione riceve la regola di copia (vedi AvviaCopiaDurataOperazione).
        foreach (var c in Esecuzioni[^1].Condizioni)
        {
            AggiornaCampiVisualCondizione(c);
            AvviaCopiaDurataOperazione(c);
        }
        AssegnatarioSelezionato = _state.Config.OperatoreAttivo; // chi crea la nuova iterazione ne diventa il responsabile corrente (Addendum §18)
        OnPropertyChanged(nameof(RevisioneCorrenteOperatore));
        ModificheNonSalvate = true;
        ValutaCandidati();
    }

    // ----- Allegati (Addendum §17.0) -----

    [RelayCommand]
    private async Task AggiungiAllegato(string? percorsoFile)
    {
        if (string.IsNullOrEmpty(percorsoFile) || !_state.CartellaValida) return;
        if (!AssicuraTestSalvato()) return;
        try
        {
            var allegato = await AttachmentService.SalvaAsync(_state.Paths, TestCase, percorsoFile, _state.Config.OperatoreAttivo);
            TestCase.Allegati.Add(allegato);
            AllegatiVisual.Add(allegato);
            if (allegato.DimensioneByte > AttachmentService.SogliaAvvisoByte)
                Avvisi = Avvisi.Append(string.Format(LocalizationService.Instance.Traduci("AllegatoGrandeFmt"), allegato.NomeFile, allegato.DimensioneByte / 1024 / 1024)).ToList();
        }
        catch (Exception ex)
        {
            ErroriValidazione = new List<string> { ex.Message };
        }
    }

    [RelayCommand]
    private void RimuoviAllegato(Allegato? allegato)
    {
        if (allegato == null) return;
        _rimozioniAllegati.Accoda(allegato); // binario fisico solo dopo il Salva riuscito (vedi AttachmentRemovalQueue)
        TestCase.Allegati.Remove(allegato);
        AllegatiVisual.Remove(allegato);
        RimuoviCollegamentiEvidenzaAllegato(allegato);
        AggiornaEvidenzeOperazioni();
    }

    [RelayCommand]
    private void ApriAllegato(Allegato? allegato)
    {
        if (allegato == null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AttachmentService.PercorsoCompleto(_state.Paths, TestCase, allegato)) { UseShellExecute = true }); }
        catch { /* file non apribile: nessuna azione, non blocca il resto dell'app */ }
    }

    // ----- Campi strumento di tipo File (Addendum: collaudo live 2026-08-17, punto 2 del grill-me) -----
    // Riusa l'infrastruttura Allegati esistente (stesso AttachmentService, stessa cartella su disco, stesso
    // TestCase.Allegati) invece di un secondo meccanismo di storage file: il valore del campo strumento
    // diventa semplicemente il NomeFile dell'Allegato risultante, stesso principio già usato per Testo/
    // Numero/Lista (un Dictionary&lt;string,string&gt; generico) — nessun nuovo schema di dati, e il file
    // resta visibile anche nell'elenco Allegati generale (unica fonte, non duplicata).

    /// <summary>Chiamato dal code-behind dopo il picker nativo (stesso schema di OnSceglieFileAllegato):
    /// non è un [RelayCommand] perché il code-behind lo invoca direttamente con due argomenti già pronti.</summary>
    public async Task CollegaFileCampoStrumento(CampoStrumentoValore? campo, string? percorsoFile)
    {
        if (campo == null || string.IsNullOrEmpty(percorsoFile) || !_state.CartellaValida) return;
        if (!AssicuraTestSalvato()) return;
        try
        {
            var allegato = await AttachmentService.SalvaAsync(_state.Paths, TestCase, percorsoFile, _state.Config.OperatoreAttivo);
            TestCase.Allegati.Add(allegato);
            AllegatiVisual.Add(allegato);
            campo.Valore = allegato.NomeFile;
        }
        catch (Exception ex)
        {
            ErroriValidazione = new List<string> { ex.Message };
        }
    }

    [RelayCommand]
    private void ApriFileCampoStrumento(CampoStrumentoValore? campo)
    {
        if (campo?.Valore == null) return;
        var allegato = TestCase.Allegati.FirstOrDefault(a => a.NomeFile == campo.Valore);
        if (allegato != null) ApriAllegato(allegato);
    }

    [RelayCommand]
    private void RimuoviFileCampoStrumento(CampoStrumentoValore? campo)
    {
        if (campo == null) return;
        campo.Valore = null;
    }

    // ----- Salvataggio -----

    [RelayCommand]
    private async Task Salva()
    {
        TaskCompletionSource operazione;
        lock (_statoAcquisizioneLockGate)
        {
            if (_chiusuraInCorso || SalvataggioInCorso || _salvataggioInCorsoPerUscita != null || _annullamentoInCorso) return;
            operazione = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _salvataggioInCorsoPerUscita = operazione;
        }

        SalvataggioInCorso = true;
        try { await SalvaCoreAsync(); }
        finally
        {
            SalvataggioInCorso = false;
            lock (_statoAcquisizioneLockGate)
            {
                if (ReferenceEquals(_salvataggioInCorsoPerUscita, operazione))
                    _salvataggioInCorsoPerUscita = null;
            }
            operazione.TrySetResult();
        }
    }

    private async Task SalvaCoreAsync()
    {
        if (_disposto || _annullamentoInCorso) return;
        var modificaAttivaAlSalvataggio = InModifica;
        // Un test senza assegnatario (creato prima che il campo diventasse obbligatorio, o mai assegnato)
        // non deve diventare un vicolo cieco per uno Standard: solo un Master può scegliere un assegnatario
        // diverso da sé (PuoAssegnare), ma chiunque salvi un test ancora privo di assegnatario se lo prende
        // implicitamente, coerente con la regola "chi esegue/tocca il test ne diventa il responsabile".
        if (string.IsNullOrWhiteSpace(TestCase.Assegnatario) && !PuoAssegnare)
            AssegnatarioSelezionato = _state.Config.OperatoreAttivo;

        // Promemoria non bloccante (feedback utente 2026-08-24, punto 3): un campo strumento costante per il
        // test rimasto esattamente sul valore precompilato non impedisce il salvataggio, ma vale la pena di
        // un avviso — potrebbe essere passato inosservato invece di essere stato verificato davvero. Solo i
        // campi PerTest (CampiTestCostanti/GruppiTestCostanti): sono quelli pensati per restare costanti,
        // a differenza di PerRevisione/PerOperazione dove "uguale al default" è la norma, non un'anomalia.
        // AggiornaAvvisi() prima di riaggiungerli, così un default già corretto in un salvataggio precedente
        // non resta agganciato per sempre alla lista.
        AggiornaAvvisi();
        var campiDaVerificare = CampiTestCostanti.Concat(GruppiTestCostanti.SelectMany(g => g.Campi));
        Avvisi = Avvisi.Concat(campiDaVerificare.Where(c => c.EDefaultNonModificato)
            .Select(c => string.Format(LocalizationService.Instance.Traduci("AvvisoDefaultNonModificatoFmt"), c.Nome, c.Valore))).ToList();

        // Garantisce che l'esito persistito rifletta le condizioni anche se qualche modifica di campo non ha
        // attraversato il percorso di sincronizzazione live della UI (rete di sicurezza, non il percorso normale).
        RevisioneCorrente?.SincronizzaEsito();
        CaricaPannelloAnomalie(creaMancanti: true);

        // Annullare il Test risolve le segnalazioni provvisorie con la motivazione di annullamento: non crea
        // anomalie operative, ma conserva il fatto osservato e la ragione della sua chiusura.
        // L'esclusione viene APPLICATA solo dopo che la validazione è passata (vedi sotto): se il Salva
        // fallisce e l'utente cambia di nuovo lo stato, le segnalazioni non restano escluse per sbaglio.
        var annullaTest = TestCase.StatoLavoro == StatoLavoroTest.Annullato && !string.IsNullOrWhiteSpace(TestCase.MotivoAnnullamento);
        var segnalazioniDaEscludere = annullaTest
            ? SegnalazioniTest.Select(r => r.Segnalazione.Uid).ToHashSet()
            : new HashSet<Guid>();

        var anomalieChiudibili = AnomalieChiudibiliDagliEsiti();

        // Un Test gia' Concluso su disco a cui l'operatore, in modifica, apre una NUOVA anomalia (operazione NOK) non e'
        // piu' concluso: senza questo riporto restava bloccato da "ci sono anomalie ancora aperte" anche con l'anomalia
        // correttamente creata/salvata, senza alcuna indicazione su cosa cambiare. Solo se il Concluso era gia' quello
        // persistito: scegliere a mano Concluso su un Test con anomalie aperte resta un blocco duro (vedi sotto).
        if (TestCase.StatoLavoro == StatoLavoroTest.Concluso && _snapshot?.StatoLavoro == StatoLavoroTest.Concluso && AnomalieTestAperte.Count > 0)
        {
            StatoLavoroSelezionato = StatoLavoroTest.DaRitestare;
            Avvisi = Avvisi.Append(LocalizationService.Instance.Traduci("AvvisoStatoRiportatoDaRitestare")).ToList();
        }

        var loc = LocalizationService.Instance;
        var errori = new List<string>();
        if (RevisioneCorrente is { EsitoSovrascritto: true } rc && string.IsNullOrWhiteSpace(rc.MotivazioneOverride))
            errori.Add(loc.Traduci("ValMotivazioneObblCorrezione"));
        if (RevisioneCorrente != null)
            foreach (var p in RevisioneCorrente.PunteggiRequisiti)
                if (p.Sovrascritto && string.IsNullOrWhiteSpace(p.MotivazioneOverride))
                    errori.Add(string.Format(loc.Traduci("ValPunteggioMotivazioneObblCorrezioneFmt"), p.RequisitoId));
        // Il valore validato deve essere quello che l'operatore vede/edita: se era sfogliata una REV storica
        // (sola lettura) si riallinea alla corrente prima di validare, cosi' un eventuale "Matricola mancante" e'
        // riferito al campo davvero modificabile e non a quello storico mostrato.
        if (VisualizzaStorico) RevisioneVisualizzata = RevisioneCorrente;
        CampiObbligatoriMancanti = ValidaCampiObbligatoriDaTemplate().ToList();
        // Ogni Test che espone Funzionalita riceve un avviso non bloccante se il campo e' vuoto,
        // anche nella modalita Rapida. I soli strumenti di supporto mantengono invece il vincolo
        // rigido preesistente; guasto-only non prevede la Funzionalita e non richiede l'avviso.
        var funzionalitaMancanteSoft = TestCase.Funzionalita.Count == 0
            && (MostraCampo(CampiSistema.Funzionalita) || FunzionalitaMancanteConUdtEcuVeicolo);
        if (funzionalitaMancanteSoft && !SoloStrumentiSupporto)
            CampiObbligatoriMancanti.RemoveAll(c => c.Chiave == CampiSistema.Funzionalita);
        errori.AddRange(CampiObbligatoriMancanti.Select(c => c.Messaggio));
        // Un errore per-operazione porta comunque al popup "campi mancanti" con salto diretto (invece del
        // banner generico) — la sezione Operazioni non ha uno Spotlight per singola riga, ma la vista
        // aggregata resta di gran lunga meglio di uno scroll cieco in cima alla pagina (feedback utente).
        void AggiungiCampoMancanteOperazione(string messaggio)
        {
            errori.Add(messaggio);
            CampiObbligatoriMancanti.Add(new CampoMancante(messaggio, CampiSistema.OperazioneCondizione));
        }
        if (RevisioneCorrente != null)
            foreach (var cond in RevisioneCorrente.Condizioni)
            {
                // Ogni operazione deve appartenere ad almeno un componente tra quelle del test (feedback utente
                // 2026-08-25, punto 9): parte già valorizzata dal default (EcuAutoSuNuovaOperazione), questo
                // controllo copre solo il caso in cui l'operatore abbia deselezionato tutto a mano.
                if (TestCase.Componente.Count > 0 && cond.Componente.Count == 0)
                    AggiungiCampoMancanteOperazione(loc.Traduci("ValCondizioneEcuObbl"));
                // Obbligatoria solo con 2+ Funzionalità collegate al test (feedback utente 2026-08-24, terza
                // revisione): con una sola non c'è scelta da fare, FunzionalitaAutoSeSingola l'ha già valorizzata.
                if (FunzionalitaObbligatoriaPer(cond) && string.IsNullOrWhiteSpace(cond.Funzionalita))
                    AggiungiCampoMancanteOperazione(loc.Traduci("ValCondizioneFunzionalitaObbl"));
                if (cond.Esito == Esito.NonApplicabile && string.IsNullOrWhiteSpace(cond.MotivazioneNonApplicabile))
                    AggiungiCampoMancanteOperazione(loc.Traduci("ValCondizioneMotivazioneNAObbl"));
                if (cond.Esito == Esito.ConformeConDeviazioni && string.IsNullOrWhiteSpace(cond.DescrizioneDeviazione) && string.IsNullOrWhiteSpace(cond.Nota))
                    AggiungiCampoMancanteOperazione(loc.Traduci("ValDescrizioneDeviazioneObbl"));
                if (cond.Esito == Esito.NonConforme && string.IsNullOrWhiteSpace(cond.DescrizioneProblema) && string.IsNullOrWhiteSpace(cond.Nota))
                    AggiungiCampoMancanteOperazione(loc.Traduci("ValProblemaOsservatoObbl"));
                // TIME (6.6.1): un testo di durata non valido blocca il salvataggio; il modello non assorbe mai il valore sbagliato.
                if (cond.DurataTesto.HaErrore)
                    AggiungiCampoMancanteOperazione(loc.Traduci("DurataNonValidaMsg"));
            }
        // TIME (6.6.1): politica "durata obbligatoria" (Master). Solo le operazioni eseguite e attive della revisione corrente
        // (CondizioniEffettive) nuove o modificate in questo salvataggio: le operazioni salvate e invariate non vengono toccate.
        // Non si applica ai Test importati da UnitLogger; in un test nuovo tutte le operazioni sono nuove.
        if (RevisioneCorrente != null && TestCase.Origine != OrigineTest.ImportStatoUdt && PoliticaDurataService.RichiedeDurata(_state.PoliticaDurata))
            foreach (var cond in RevisioneCorrente.CondizioniEffettive)
                if (cond.Esito.EEseguito() && cond.DurataMinuti is null && OperazioneNuovaOModificata(cond))
                    AggiungiCampoMancanteOperazione(loc.Traduci("ValDurataOperazioneObbl"));
        // Ogni componente collegata al test deve avere almeno un'operazione che la copre davvero (feedback utente
        // 2026-08-25, punto 9), simmetrico al controllo per-operazione sopra e allo stesso principio già
        // applicato a Funzionalita sotto — altrimenti un componente risulterebbe collegata al test ma mai verificata
        // da nessuna operazione, invisibile al calcolo di CoverageService.PerEcu.
        // 6.5.2: su un test GIA salvato "solo guasto" (nato prima di guasto Injection, operazione vuota rimossa) i guasti non ci sono ancora: la
        // copertura componente resta avviso nel banner, non un blocco che renderebbe il test non salvabile. In creazione il blocco resta.
        if (RevisioneCorrente != null && TestCase.Componente.Count > 0 && !(SoloOperazioniDtc && !IsNew))
        {
            var ecuCoperte = RevisioneCorrente.CondizioniEffettive.SelectMany(c => c.Componente).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ecuNonCoperte = TestCase.Componente.Where(e => !ecuCoperte.Contains(e)).ToList();
            if (ecuNonCoperte.Count > 0)
                errori.Add(string.Format(loc.Traduci("ValEcuSenzaOperazioneFmt"), string.Join(", ", ecuNonCoperte)));
        }
        // Simmetrico al controllo sopra (feedback utente 2026-08-24, quarta revisione — "non posso avere
        // funzionalità diverse nello stesso test e avere una funzionalità senza che sia in operazioni"): non
        // basta che ogni operazione abbia una Funzionalità, ogni Funzionalità collegata al test deve avere
        // almeno un'operazione che la copre davvero, altrimenti risulterebbe collegata al test ma mai
        // verificata da nessuna operazione — invisibile al calcolo di copertura per-Funzionalità.
        if (RevisioneCorrente != null && FunzionalitaOperazioneObbligatoria && !RevisioneCorrente.Condizioni.Any(c => c.RiguardaDtc))
        {
            var funzionalitaCoperte = RevisioneCorrente.CondizioniEffettive.Select(c => c.Funzionalita).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var funzionalitaNonCoperte = TestCase.Funzionalita.Where(f => !funzionalitaCoperte.Contains(f)).ToList();
            if (funzionalitaNonCoperte.Count > 0)
                errori.Add(string.Format(loc.Traduci("ValFunzionalitaSenzaOperazioneFmt"), string.Join(", ", funzionalitaNonCoperte)));
        }
        // 6.5.2: con guasto Injection ogni coppia Veicolo x componente del test deve avere almeno un guasto. Blocca solo in creazione; sui test
        // gia salvati (bozze nate prima dell'aggiunta di guasto Injection) resta un AVVISO non bloccante nel banner di copertura, per non
        // impedire di salvare un lavoro in corso: i guasti si aggiungono quando servono.
        if (RevisioneCorrente != null && UsaStrumentoDtcInjection && IsNew)
        {
            var senzaDtc = VeicoloEcuSenzaDtcBloccanti;
            if (senzaDtc.Count > 0)
                errori.Add(string.Format(loc.Traduci("ValVeicoloEcuSenzaDtcFmt"), string.Join(", ", senzaDtc)));
        }
        if (TestCase.StatoLavoro == StatoLavoroTest.Annullato && string.IsNullOrWhiteSpace(TestCase.MotivoAnnullamento))
            errori.Add(loc.Traduci("ValMotivoAnnullamentoObbl"));
        // I problemi non ancora classificati devono restare salvabili durante l'esecuzione,
        // senza perdere la bozza o forzare una classificazione prematura. Sono bloccanti solo alla conclusione.
        if (TestCase.StatoLavoro == StatoLavoroTest.Concluso &&
            SegnalazioniTest.Any(s => !s.Esclusa && !segnalazioniDaEscludere.Contains(s.Segnalazione.Uid)))
            errori.Add(loc.Traduci("ValClassificaPrimaConclusione"));
        if (TestCase.StatoLavoro == StatoLavoroTest.Concluso && AnomalieTestAperte.Count > 0)
        {
            // L'NC e' stata classificata, ma rimane APERTA: e' corretto impedire "Concluso".
            // Indicare l'azione concreta evita che l'operatore interpreti il blocco come
            // un salvataggio guasto e perda le modifiche/NC in bozza.
            errori.Add(loc.Traduci("ValAnomalieAperteConclusione"));
            var ids = AnomalieTestAperte.Select(x => x.Nc.Id)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
            var dettaglio = ids.Length == 0 ? "" : string.Join(", ", ids);
            if (AnomalieTestAperte.Count > ids.Length) dettaglio += ", ...";
            errori.Add(loc.Scegli(
                $"NC aperte ({AnomalieTestAperte.Count}): {dettaglio}. Per salvare il Test senza chiudere queste NC, seleziona 'Da ritestare' (oppure 'In corso') e premi Salva. 'Concluso' sara' disponibile dopo la chiusura delle NC.",
                $"Open NCs ({AnomalieTestAperte.Count}): {dettaglio}. To save the Test while these NCs remain open, select 'To retest' (or 'In progress') and Save. 'Completed' requires closing the NCs.",
                $"NC ouvertes ({AnomalieTestAperte.Count}) : {dettaglio}. Pour enregistrer le test en conservant ces NC ouvertes, choisissez 'À retester' (ou 'En cours') puis Enregistrer. La clôture nécessite de fermer les NC."));
        }
        // Decisione utente 6.4.0, punto 1: un Test Concluso non può lasciare operazioni ancora "Da eseguire"
        // (esito mai inserito) — N.A. resta invece un esito legittimo di chiusura, non blocca. Stesso pattern
        // di blocco duro già usato sopra per anomalie aperte/segnalazioni da classificare, coerente con
        // l'idea che "Concluso" descriva un lavoro davvero finito.
        if (TestCase.StatoLavoro == StatoLavoroTest.Concluso && RevisioneCorrente != null &&
            RevisioneCorrente.CondizioniEffettive.Any(c => !c.Esito.EEseguito() && c.Esito != Esito.NonApplicabile && !c.DevalidazioneNonRichiesta))
            errori.Add(loc.Traduci("ValOperazioniNonEseguiteConclusione"));
        foreach (var nc in anomalieChiudibili.Where(n => string.IsNullOrWhiteSpace(n.MotivoChiusura)))
            errori.Add(string.Format(loc.Traduci("ValMotivoChiusuraConformeFmt"), nc.Id));
        errori.AddRange(CommitAnomalieDalTest());

        // Nota obbligatoria solo se un Master sta davvero cambiando area/scadenza/assegnatario rispetto
        // all'ultimo salvataggio (Addendum §7) — mai richiesta alla creazione, dove questi campi si impostano
        // per la prima volta, non si "modificano".
        var pianificazioneCambiata = _snapshot != null && (
            _snapshot.Scadenza != TestCase.Scadenza ||
            _snapshot.Assegnatario != TestCase.Assegnatario);
        if (pianificazioneCambiata && NotaPianificazioneRichiesta && string.IsNullOrWhiteSpace(NotaPianificazioneTemp))
            errori.Add(loc.Traduci("ValNotaPianificazioneObbl"));

        ErroriValidazione = errori;
        if (errori.Count > 0)
        {
            // Solo metadati di flusso: niente note, descrizioni, nomi o valori dei campi
            // nei trace locali. Le eccezioni di I/O/conflitto sono registrate separatamente.
            DiagnosticTraceService.Registra("Test.Workflow",
                $"save blocked by validation: requested={TestCase.StatoLavoro}; " +
                $"issues={errori.Count}; openNc={AnomalieTestAperte.Count}; " +
                $"unclassified={SegnalazioniTest.Count(x => !x.Esclusa)}; " +
                $"newNcPending={_anomalieNuove.Count}");
            // Sempre un popup, mai solo il banner in cima alla pagina (feedback utente 2026-08-20): con un
            // form lungo e l'utente scrollato sull'ultima sezione, un banner in cima sembra "il pulsante non fa
            // niente". Il dialog dedicato ai campi mancanti (con salto diretto al campo) resta prioritario
            // quando applicabile; per gli altri motivi di blocco un avviso semplice con l'elenco basta.
            if (CampiObbligatoriMancanti.Count > 0) RichiestaMostraCampiMancanti?.Invoke();
            else await ConfirmService.Chiedi(loc.Traduci("ImpossibileSalvareTitolo"), string.Join("\n", errori), loc.Traduci("OkBtn"), "");
            return;
        }

        // Funzionalita' assente: conferma prima di salvare ogni Test nel quale il campo e' disponibile.
        // Nessuna mutazione di TestCase prima di questo punto: tornando indietro la bozza resta intatta.
        if (funzionalitaMancanteSoft)
        {
            var prosegui = await ConfirmService.Chiedi(
                loc.Traduci("FunzionalitaMancanteTitolo"),
                loc.Traduci("FunzionalitaMancanteMsg"),
                loc.Traduci("FunzionalitaMancanteProsegui"),
                loc.Traduci("FunzionalitaMancanteIndietro"));
            if (_disposto || _annullamentoInCorso || (modificaAttivaAlSalvataggio && !InModifica)) return;
            if (!prosegui)
            {
                CampiObbligatoriMancanti = new List<CampoMancante>
                {
                    new(string.Format(loc.Traduci("ValCampoObblFmt"), EtichettaFunzionalita), CampiSistema.Funzionalita)
                };
                NotificaObbligatorieta();
                NotificaCampiRapidiEssenziali();
                RichiestaVaiAlCampo?.Invoke(CampiSistema.Funzionalita);
                return;
            }
        }

        // Avviso non bloccante (punto A, 6.4.1 "Avviso + merge automatico"): subito prima del Salva, di nuovo
        // — non solo all'apertura — perché nel frattempo può essere arrivato via sync un lock/heartbeat attivo
        // di un'altra postazione/operatore. Nessun blocco rigido: solo conferma esplicita ("Salva comunque" /
        // "Annulla"). Se annullato, la bozza resta intatta: nessuna mutazione di TestCase è ancora avvenuta a
        // questo punto del metodo.
        await AggiornaAvvisoAltraPostazioneAsync();
        if (_disposto || _annullamentoInCorso || (modificaAttivaAlSalvataggio && !InModifica)) return;
        if (!PuoModificare)
        {
            ErroriValidazione = new List<string> { loc.Traduci("ArchivioAzioneNonAutorizzata") };
            return;
        }
        if (AvvisoAltraPostazioneVisibile)
        {
            var procediComunque = await ConfirmService.Chiedi(
                loc.Traduci("AvvisoAltraPostazioneTitoloConferma"),
                AvvisoAltraPostazioneTesto ?? loc.Traduci("AvvisoAltraPostazioneGenerico"),
                loc.Traduci("SalvaComunqueBtn"),
                loc.Traduci("Annulla"));
            if (!procediComunque) return;
        }

        // La finestra può essere stata chiusa mentre era aperta la conferma modale: in quel caso Dispose ha
        // trasferito il rilascio del lease al coordinatore e il comando non deve più committare la bozza.
        if (_disposto || _annullamentoInCorso || (modificaAttivaAlSalvataggio && !InModifica)) return;
        if (!PuoModificare)
        {
            ErroriValidazione = new List<string> { loc.Traduci("ArchivioAzioneNonAutorizzata") };
            return;
        }

        if (annullaTest)
        {
            foreach (var riga in SegnalazioniTest.ToList())
            {
                TracciaSegnalazioneDaAggiornare(riga.Segnalazione);
                riga.Segnalazione.Stato = StatoSegnalazione.Esclusa;
                riga.Segnalazione.MotivazioneEsclusione = string.Format(loc.Traduci("MotivoSegnalazioneTestAnnullatoFmt"), TestCase.MotivoAnnullamento!.Trim());
            }
        }

        foreach (var nc in anomalieChiudibili)
        {
            TracciaAnomaliaDaAggiornare(nc);
            NonConformitaRulesService.Chiudi(nc);
            nc.Changelog.Add(new ChangelogEntry
            {
                Data = DateTime.Now,
                Operatore = _state.Config.OperatoreAttivo,
                Campo = "chiusura automatica per conformita",
                ValoreNuovo = nc.MotivoChiusura.Trim()
            });
        }
        if (annullaTest) CaricaPannelloAnomalie();

        if (pianificazioneCambiata)
        {
            var campi = new List<string>();
            if (_snapshot!.Scadenza != TestCase.Scadenza) campi.Add("scadenza");
            if (_snapshot.Assegnatario != TestCase.Assegnatario) { campi.Add("assegnatario"); TestCase.DataAssegnazione = DateTime.Now; }
            // Senza nota (auto-assegnazione di Salva a uno Standard) resta comunque il Changelog di
            // RegistraChangelog: nessuna voce vuota nello storico delle note di pianificazione.
            if (!string.IsNullOrWhiteSpace(NotaPianificazioneTemp))
                TestCase.NotePianificazione.Add(new NotaPianificazione
                {
                    Data = DateTime.Now, Operatore = _state.Config.OperatoreAttivo,
                    CampiModificati = string.Join(", ", campi), Nota = NotaPianificazioneTemp.Trim()
                });
            NotaPianificazioneTemp = "";
        }

        TestCase.StrumentiUsati = StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Valore).ToList();
        TestCase.Esecuzioni = Esecuzioni.ToList();
        TestCase.Allegati = AllegatiVisual.ToList();

        // Ogni modifica ai Dettagli/Operazioni crea una nuova revisione, non più solo un tasto manuale
        // "+ Nuova iterazione" (Addendum §6, feedback QA): la Pianificazione è uno strato amministrativo
        // trasversale (tracciata invece con la nota obbligatoria sopra), i Dettagli sono per-revisione. Non
        // scatta alla primissima esecuzione (niente da confrontare) né su un test ancora nuovo.
        // Un Test Pianificato senza alcuna revisione (Esecuzioni vuote) non ha una REV da copiare: i Dettagli restano
        // sul Test e la prima REV la creerà l'avvio dell'esecuzione. Senza questa guardia Esecuzioni[^1] lanciava
        // "Index was out of range" a ogni Salva con un Dettaglio cambiato (es. valore di un campo di sistema).
        if (!IsNew && _snapshot != null && TestCase.Esecuzioni.Count > 0 && DettagliCambiati())
        {
            var storiche = _snapshot.Esecuzioni.Select(ClonaEsecuzione).ToList();
            var nuova = ClonaEsecuzione(TestCase.Esecuzioni[^1]);
            // Le evidenze appartengono alla REV in cui sono state raccolte: la nuova REV parte senza collegamenti.
            foreach (var cond in nuova.Condizioni) if (cond.ValoriStrumenti is not null) EvidenzaOperazione.Svuota(cond.ValoriStrumenti);
            nuova.Iterazione = storiche.Count + 1;
            nuova.Data = DateTime.Now;
            nuova.Operatore = _state.Config.OperatoreAttivo;
            nuova.Dettagli = CostruisciSnapshotDettagli();
            storiche.Add(nuova);
            TestCase.Esecuzioni = storiche;
            SostituisciEsecuzioni(TestCase.Esecuzioni);
            AggiornaEvidenzeOperazioni();
        }
        else if (TestCase.Esecuzioni.Count > 0)
        {
            // Nessuna nuova revisione creata: rinfresca comunque lo snapshot della revisione corrente, così
            // un test salvato per la prima volta dopo questo aggiornamento (Dettagli=null ereditato da prima)
            // si autoripara invece di restare "non disponibile" per sempre (Addendum §1, feedback QA).
            TestCase.Esecuzioni[^1].Dettagli = CostruisciSnapshotDettagli();
        }

        ValutaFuoriCategoria();
        if (MessaggioFuoriCategoria != null)
        {
            var loc2 = LocalizationService.Instance;
            var procedi = await ConfirmService.Chiedi(loc2.Traduci("FuoriCategoriaTitolo"), MessaggioFuoriCategoria,
                loc2.Traduci("SiProcediSegnalato"), loc2.Traduci("Annulla"));
            if (!procedi) return;
            if (_disposto || _annullamentoInCorso || (modificaAttivaAlSalvataggio && !InModifica) || !PuoModificare) return;
            TestCase.FuoriCategoria = true;
        }

        if (!_salvatoSuDisco && string.IsNullOrEmpty(TestCase.Id))
        {
            var iniziali = IdGenerator.IniziliDa(_state.Config.OperatoreAttivo);
            TestCase.Id = IdGenerator.Genera(_state.TestCases.GetAllIds(true), iniziali);
            OnPropertyChanged(nameof(TitoloPagina));
            OnPropertyChanged(nameof(TestRichiedeEcu));
            OnPropertyChanged(nameof(PreparaPolarionTooltip));
        }

        RegistraChangelog();
        try
        {
            TestCase.UltimaModifica = DateTime.Now;
            TestCase.ModificatoDa = _state.Config.OperatoreAttivo;
            var assegnatarioPrima = _snapshot?.Assegnatario;
            _state.TestCases.Save(TestCase, !_salvatoSuDisco, _salvatoSuDisco ? _snapshot?.RevisioneDati : null);
            _salvatoSuDisco = true;
            // Test persistito senza gli allegati staccati: solo ora i relativi binari possono lasciare il bucket attivo.
            var paths = _state.Paths;
            var salvato = TestCase;
            _rimozioniAllegati.ApplicaDopoPersistenza(salvato.Allegati, a => AttachmentService.Elimina(paths, salvato, a));
            // Il Test è ormai su disco con la nuova RevisioneDati: se un salvataggio successivo (NC,
            // segnalazioni, CR) fallisce e l'utente riprova, senza questo riallineamento il retry userebbe la
            // revisione vecchia (conflitto con la nostra stessa scrittura) e rifarebbe Changelog/nota/nuova
            // revisione già applicati.
            _snapshot = Clona(TestCase);
            if (assegnatarioPrima != TestCase.Assegnatario && NotificaRegole.DeveNotificareAssegnazione(_state.Config.OperatoreAttivo, TestCase.Assegnatario))
                _state.Notifiche.Add(new NotificaApp { Destinatario = TestCase.Assegnatario!, Tipo = "Assegnazione", Titolo = LocalizationService.Instance.Traduci("NotificaAssegnazioneTitolo"), Messaggio = string.Format(LocalizationService.Instance.Traduci("NotificaAssegnazioneBodyFmt"), TestCase.Id), ElementoId = TestCase.Id });
            // Auto-popolamento catalogo WU Status (feedback utente 2026-08-24): ogni "Pacchetto + rev."
            // trovata nelle condizioni di questo test entra nel catalogo se non c'era già.
            RevisioneUdtService.Sincronizza(_state.Master.RevisioniUdt, TestCase, _state.Config.OperatoreAttivo);
            // Dopo la creazione automatica della nuova revisione riallinea i riferimenti alla revisione piu
            // recente che contiene la CondizioneUid e assegna l'Id definitivo ai record creati su un Test nuovo.
            foreach (var segnalazione in _segnalazioniNuove.Where(x => !_salvatiInQuestoSalva.Contains(x.Uid)).ToList())
            {
                segnalazione.TestId = TestCase.Id;
                segnalazione.TestUid = TestCase.Uid;
                segnalazione.EsecuzioneIterazione = TestCase.Esecuzioni.LastOrDefault(e => e.Condizioni.Any(c => c.Uid == segnalazione.CondizioneUid))?.Iterazione ?? segnalazione.EsecuzioneIterazione;
                _state.Segnalazioni.Save(segnalazione);
                _salvatiInQuestoSalva.Add(segnalazione.Uid);
                if (segnalazione.Stato == StatoSegnalazione.DaClassificare)
                    _state.Notifiche.Add(new NotificaApp { Destinatario = _state.Config.OperatoreAttivo, Tipo = "Classificazione", Titolo = LocalizationService.Instance.Traduci("NotificaClassificazioneTitolo"), Messaggio = string.Format(LocalizationService.Instance.Traduci("NotificaClassificazioneBodyFmt"), TestCase.Id), ElementoId = TestCase.Id });
            }
            // I repository di NC espongono deliberatamente snapshot clonati da GetAll() (nessun ViewModel deve
            // poter mutare la cache globale prima del Save): TutteLeAnomalieCorrenti() darebbe quindi qui una
            // copia diversa, non mutata, da quella già aggiornata nel pannello (MotivoChiusura, StatoWorkflow,
            // ProveCollegate...), perdendo silenziosamente tutto il lavoro fatto sopra. Le anomalie esistenti
            // vanno risolte sugli stessi riferimenti di AnomalieTestAperte/Chiuse; _anomalieNuove restano invece
            // gli oggetti live (TutteLeAnomalieCorrenti() li espone senza clonarli).
            var anomalieDelPannello = AnomalieTestAperte.Concat(AnomalieTestChiuse).Select(r => r.Nc).Concat(_anomalieNuove).ToList();
            var testNecessariSalvataggio = RisolviTestNecessari(anomalieDelPannello);
            foreach (var nc in anomalieDelPannello.Where(n => _anomalieNuove.Contains(n) || _anomalieDaAggiornare.ContainsKey(n.Uid)))
            {
                foreach (var prova in nc.ProveCollegate.Where(p => AnomaliaWorkflowService.ProvaAppartieneAlTest(TestCase, p)))
                {
                    // Collegamento fatto su un Test ancora senza Id: l'Id definitivo esiste solo adesso.
                    if (string.IsNullOrEmpty(prova.TestId)) prova.TestId = TestCase.Id;
                    prova.EsecuzioneIterazione = TestCase.Esecuzioni.LastOrDefault(e => e.Condizioni.Any(c => c.Uid == prova.CondizioneUid))?.Iterazione ?? prova.EsecuzioneIterazione;
                }
                var corrente = AnomaliaWorkflowService.TestCorrente(nc, testNecessariSalvataggio);
                if (corrente != null) { nc.TestCaseId = corrente.Id; nc.TestUid = corrente.Uid; }
            }
            foreach (var nc in _anomalieNuove.Where(x => !_salvatiInQuestoSalva.Contains(x.Uid)).ToList())
            {
                nc.TestCaseId = TestCase.Id;
                nc.TestUid = TestCase.Uid;
                foreach (var prova in nc.ProveCollegate.Where(p => string.IsNullOrEmpty(p.TestId))) prova.TestId = TestCase.Id;
                SalvaNcNuovaRisolvendoCollisioneId(nc);
                _salvatiInQuestoSalva.Add(nc.Uid);
                // Già su disco (RevisioneDati aggiornata in place dal Save): sposta la NC nel percorso "esistenti".
                // Un retry dopo un fallimento successivo non ne crea una seconda e, se l'utente la modifica prima di
                // riprovare, viene riscritta con la revisione corrente invece di essere saltata (modifica persa).
                _anomalieNuove.Remove(nc);
            }
            foreach (var coppia in _anomalieDaAggiornare.ToList())
            {
                var nc = anomalieDelPannello.FirstOrDefault(n => n.Uid == coppia.Key)
                    ?? TutteLeAnomalieCorrenti().FirstOrDefault(n => n.Uid == coppia.Key);
                if (nc != null && !_anomalieNuove.Contains(nc)) _state.NonConformita.Save(nc, coppia.Value);
                _anomalieDaAggiornare.Remove(coppia.Key); // già scritta: un retry userebbe la revisione vecchia
            }
            foreach (var uid in _anomalieDaEliminare)
            {
                var nc = _state.NonConformita.GetByUid(uid);
                if (nc != null) _state.NonConformita.Elimina(nc);
            }
            _anomalieDaEliminare.Clear();
            foreach (var coppia in _segnalazioniDaAggiornare.ToList())
            {
                var seg = _segnalazioniNuove.FirstOrDefault(x => x.Uid == coppia.Key) ?? _state.Segnalazioni.Get(coppia.Key);
                if (seg != null && (!_segnalazioniNuove.Contains(seg) || _salvatiInQuestoSalva.Contains(seg.Uid))) _state.Segnalazioni.Save(seg, coppia.Value);
                _segnalazioniDaAggiornare.Remove(coppia.Key);
            }
            var crCollegate = string.IsNullOrEmpty(TestCase.Id)
                ? new List<ChangeRequest>()
                : _state.ChangeRequests.GetByTestValidazioneId(TestCase.Id).Where(cr => cr.TestValidazione.Any(l => l.TestId == TestCase.Id)).ToList();
            foreach (var cr in crCollegate)
            {
                var testCr = new Dictionary<string, TestCase>(StringComparer.OrdinalIgnoreCase) { [TestCase.Id] = TestCase };
                foreach (var link in cr.TestValidazione)
                {
                    if (!string.IsNullOrWhiteSpace(link.TestId) && !testCr.ContainsKey(link.TestId))
                    {
                        var t = _state.TestCases.GetById(link.TestId);
                        if (t != null) testCr[link.TestId] = t;
                    }
                }
                var revCr = cr.RevisioneDati;
                var statoPrima = cr.Stato;
                if (!ChangeRequestWorkflowService.RicalcolaStato(cr, testCr.Values)) continue;
                cr.UltimaModifica = DateTime.Now;
                cr.Changelog.Add(new ChangelogEntry { Data = DateTime.Now, Operatore = _state.Config.OperatoreAttivo, Campo = "stato-validazione", ValorePrecedente = statoPrima.ToString(), ValoreNuovo = cr.Stato.ToString() });
                _state.ChangeRequests.Save(cr, revCr);
                if (!string.IsNullOrWhiteSpace(cr.Responsabile))
                    _state.Notifiche.Add(new NotificaApp { Destinatario = cr.Responsabile, Tipo = "ChangeRequest", Titolo = $"{cr.Id}: {cr.Stato}", Messaggio = string.Format(LocalizationService.Instance.Traduci("NotificaCrValidazioneBodyFmt"), TestCase.Id), ElementoId = cr.Id });
            }
        }
        catch (ConflittoRevisioneException ex)
        {
            DiagnosticTraceService.Registra("Test.Workflow", "save conflict: optimistic revision mismatch");
            _state.Notifiche.Add(new NotificaApp { Destinatario = _state.Config.OperatoreAttivo, Tipo = "Conflitto", Titolo = LocalizationService.Instance.Traduci("NotificaConflittoTitolo"), Messaggio = string.Format(LocalizationService.Instance.Traduci("NotificaConflittoBodyFmt"), TestCase.Id), ElementoId = TestCase.Id });
            if (!_state.EMaster)
            {
                ErroriValidazione = new List<string>
                {
                    string.Format(LocalizationService.Instance.Traduci("ConflittoModificaFmt"), ex.Message),
                    LocalizationService.Instance.Traduci("MergeSoloMaster"),
                    LocalizationService.Instance.Traduci("BozzaAncoraAperta")
                };
                return;
            }

            var loroVersione = _state.TestCases.LoadCurrent(TestCase);
            if (_snapshot == null || loroVersione == null)
            {
                ErroriValidazione = new List<string>
                {
                    string.Format(LocalizationService.Instance.Traduci("ConflittoModificaFmt"), ex.Message),
                    LocalizationService.Instance.Traduci("MergeRiletturaFallita")
                };
                return;
            }

            var merge = TestCaseThreeWayMerge.Prepara(_snapshot, TestCase, loroVersione);
            var scelte = merge.Conflitti.Count == 0
                ? new Dictionary<string, SceltaMerge>()
                : await MergeConflittoDialogService.Risolvi(merge.Conflitti);
            if (scelte == null)
            {
                ErroriValidazione = new List<string> { LocalizationService.Instance.Traduci("MergeAnnullato") };
                return;
            }

            ApplicaMergeNelForm(merge.CreaVersioneRisolta(scelte, _state.Config.OperatoreAttivo), loroVersione);
            ErroriValidazione = new List<string>
            {
                merge.Conflitti.Count == 0
                    ? LocalizationService.Instance.Traduci("MergeAutomaticoPronto")
                    : LocalizationService.Instance.Traduci("MergeMasterPronto")
            };
            return;
        }
        catch (TestCaseSaveLockTimeoutException ex)
        {
            CrashLogService.Registra("Test.Save: timeout lock", ex);
            DiagnosticTraceService.Registra("Test.Workflow", "save failed: lock-timeout");
            // Non è un conflitto di revisione (il contenuto non è mai stato confrontato): un'altra
            // postazione/processo sta scrivendo esattamente questo Test in questo istante. Nessuna mutazione di
            // TestCase è avvenuta (l'eccezione viene lanciata prima di qualunque rilettura/scrittura), quindi la
            // bozza locale resta intatta: mostriamo solo un messaggio comprensibile e lasciamo l'editor aperto,
            // senza instradare al percorso di merge (qui non c'è nulla da confrontare, basta riprovare).
            ErroriValidazione = new List<string> { LocalizationService.Instance.Traduci("SalvataggioTestLockOccupato") };
            return;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Test.Save: persistence/workflow", ex);
            DiagnosticTraceService.Registra("Test.Workflow", "save failed: " + ex.GetType().Name);
            ErroriValidazione = new List<string> { string.Format(LocalizationService.Instance.Traduci("SalvataggioTestErroreFmt"), ex.Message) };
            return;
        }

        // Salvataggio completato: il tracciamento dei record già scritti serviva solo ai retry di un salvataggio
        // parziale. Lasciarlo attivo farebbe saltare in un eventuale Salva successivo le modifiche a NC/segnalazioni
        // nuove ormai persistite.
        _salvatiInQuestoSalva.Clear();
        _anomalieNuove.Clear();
        _anomalieCollegate.Clear(); // ormai scritte: da qui le letture del repository riflettono i collegamenti
        _segnalazioniNuove.Clear();
        FermaHeartbeat(); // prima del Release: vedi _sessioneModificaAttiva
        var rilascio = await RilasciaLockSessioneAttivaAsync();
        if (rilascio != EsitoIo.Ok)
        {
            InModifica = false;
            RilascioLockInAttesa = true;
            _rilascioLockDopoSalvataggio = true;
            _tornaAllaListaDopoRilascio = !_sopprimiRitornoAllaLista;
            MessaggioRilascioLock = LocalizationService.Instance.Traduci("SalvatoSbloccoLockNonRiuscito");
            _state.RicaricaTutto();
            ModificheNonSalvate = false;
            _snapshot = Clona(TestCase);
            return;
        }
        _state.RicaricaTutto();
        ModificheNonSalvate = false;
        InModifica = false;
        _snapshot = Clona(TestCase);
        DiagnosticTraceService.Registra("Test.Workflow",
            WorkflowTransactionService.ActiveId is null
                ? $"save completed (direct): status={TestCase.StatoLavoro}"
                : $"save staged (awaiting transaction commit): status={TestCase.StatoLavoro}");
        if (!_sopprimiRitornoAllaLista) _tornaAllaLista();
    }

    /// <summary>Porta nel form il risultato del merge senza scriverlo ancora: il Master puo ricontrollarlo e
    /// il successivo Salva riesegue le normali validazioni e persiste la nuova revisione sopra quella remota.</summary>
    private void ApplicaMergeNelForm(TestCase risolto, TestCase loroVersione)
    {
        TestCase = risolto;
        _snapshot = Clona(loroVersione);
        SostituisciEsecuzioni(TestCase.Esecuzioni);
        AllegatiVisual.Clear();
        foreach (var allegato in TestCase.Allegati) AllegatiVisual.Add(allegato);
        AggiornaEvidenzeOperazioni();
        CaricaStrumenti();
        RevisioneVisualizzata = RevisioneCorrente;
        OnPropertyChanged(nameof(TitoloPagina));
        OnPropertyChanged(nameof(NumeroRevisione));
        OnPropertyChanged(nameof(StoricoRevisioni));
        OnPropertyChanged(nameof(ChangelogVuoto));
        ModificheNonSalvate = true;
    }

    private void RegistraChangelog()
    {
        if (_snapshot == null) return;
        void Diff(string campo, string? oldV, string? newV)
        {
            if (oldV == newV) return;
            TestCase.Changelog.Add(new ChangelogEntry { Data = DateTime.Now, Operatore = _state.Config.OperatoreAttivo, Campo = campo, ValorePrecedente = oldV, ValoreNuovo = newV });
        }
        Diff("componente", _snapshot.Componente, TestCase.Componente);
        Diff("veicoli", string.Join(", ", _snapshot.VeicoliEffettivi), string.Join(", ", TestCase.VeicoliEffettivi));
        Diff("descrizione", _snapshot.Descrizione, TestCase.Descrizione);
        Diff("tipoTest", _state.NomeTestTemplate(_snapshot.TestTemplateUid), _state.NomeTestTemplate(TestCase.TestTemplateUid));
        Diff("assegnatario", _snapshot.Assegnatario, TestCase.Assegnatario);
        if (Esecuzioni.Count != _snapshot.Esecuzioni.Count)
            Diff("esecuzioni.count", _snapshot.Esecuzioni.Count.ToString(), Esecuzioni.Count.ToString());
    }

    /// <summary>Il pulsante "Torna alla lista" più cliccato per errore al posto di "Salva": senza conferma
    /// qui, era il modo più comune di perdere in silenzio del lavoro appena fatto.</summary>
    [RelayCommand]
    private async Task Annulla()
    {
        TaskCompletionSource operazione;
        lock (_statoAcquisizioneLockGate)
        {
            if (_chiusuraInCorso || SalvataggioInCorso || _annullamentoInCorso) return;
            _annullamentoInCorso = true;
            operazione = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _annullamentoInCorsoPerUscita = operazione;
        }
        try
        {
            if (RilascioLockInAttesa)
            {
                await RiprovaRilascioLock();
                return;
            }
            if (ModificheNonSalvate && !await ConfirmService.ConfermaAbbandonoModifiche(this)) return;
            FermaHeartbeat(); // prima del Release: vedi _sessioneModificaAttiva
            var rilascio = await RilasciaLockSessioneAttivaAsync();
            // Se la chiusura è partita mentre la conferma o il rilascio erano in attesa, il drain dell'uscita
            // possiede la navigazione e il rilascio del catalogo. Non disporre l'editor con cleanup fire-and-forget.
            lock (_statoAcquisizioneLockGate)
            {
                if (_chiusuraInCorso) return;
            }
            if (rilascio != EsitoIo.Ok)
            {
                InModifica = false;
                RilascioLockInAttesa = true;
                _rilascioLockDopoSalvataggio = false;
                _tornaAllaListaDopoRilascio = true;
                MessaggioRilascioLock = LocalizationService.Instance.Traduci("LockSbloccoNonRiuscito");
                return;
            }
            _tornaAllaLista();
        }
        finally
        {
            lock (_statoAcquisizioneLockGate)
            {
                _annullamentoInCorso = false;
                if (ReferenceEquals(_annullamentoInCorsoPerUscita, operazione))
                    _annullamentoInCorsoPerUscita = null;
            }
            operazione.TrySetResult();
        }
    }

    [RelayCommand]
    private async Task EliminaTestCase()
    {
        if (IsNew) return;
        if (!await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("ArchiviaTestTitoloConferma"),
            string.Format(LocalizationService.Instance.Traduci("ArchiviaTestBodyFmt"), TestCase.Id),
            LocalizationService.Instance.Traduci("Archivia"), LocalizationService.Instance.Traduci("Annulla"))) return;

        try
        {
            _state.TestCases.SoftDelete(TestCase, _state.Config.OperatoreAttivo);
        }
        catch (Exception ex)
        {
            ErroriValidazione = new List<string> { string.Format(LocalizationService.Instance.Traduci("ArchiviazioneTestErroreFmt"), ex.Message) };
            return;
        }
        FermaHeartbeat(); // prima del Release: vedi _sessioneModificaAttiva
        await RilasciaLockSessioneAttivaAsync();
        _state.RicaricaTutto();
        _tornaAllaLista();
    }

    // --- Scorciatoie da tastiera (Addendum: audit UX 2026-08-26, punto 6) ---------------------------------
    // Entrambe passano dai comandi già esistenti invece di duplicarne la logica: Salva mantiene quindi tutta
    // la validazione e la creazione di revisione, e Annulla mantiene la conferma di abbandono modifiche —
    // indispensabile qui, perché Esc è facilissimo da premere per sbaglio mentre si compila un campo.

    /// <summary>Ctrl+S. Vincolata alla stessa condizione che rende visibile il pulsante Salva: premerla in
    /// sola lettura o prima che il Tipo test sia stato scelto non deve fare nulla.</summary>
    bool IScorciatoiePagina.SalvaDaTastiera()
    {
        if (!MostraAzioniModifica) return false;
        SalvaCommand.Execute(null);
        return true;
    }

    /// <summary>Esc. Attiva solo mentre si sta modificando: su un test aperto in sola lettura, Esc non deve
    /// far uscire dalla scheda a sorpresa (per tornare alla lista c'è il pulsante dedicato).</summary>
    bool IScorciatoiePagina.AnnullaDaTastiera()
    {
        if (!InModifica) return false;
        AnnullaCommand.Execute(null);
        return true;
    }
}
