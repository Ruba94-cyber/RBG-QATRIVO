using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Una riga porta con sé anche il TestCase collegato (non solo il suo ID) perché il numero di
/// claim/ticket mostrato qui non è un campo proprio della NC: è lo stesso dato di TestCase.TicketCollegati
/// (Addendum §18) — niente doppio inserimento, la NC legge/scrive direttamente lì.</summary>
public partial class NcRiga : ViewModelBase
{
    public NonConformita Nc { get; init; } = null!;
    public TestCase? TestCase { get; init; }
    public string TestCaseIdVisualizzato { get; init; } = "";
    public bool TestCaseEliminato { get; init; }
    public bool Aperta => Nc.Stato == StatoNonConformita.Aperta;
    public string DataAperturaTesto => Nc.DataApertura.ToString("dd/MM/yyyy");
    public string DataChiusuraTesto => Nc.DataChiusura?.ToString("dd/MM/yyyy") ?? "";
    public string PrioritaTesto => Nc.Priorita.Label();
    public string GravitaTesto => Nc.Severita?.Label() ?? "—";
    public string AssegnatarioTesto => string.IsNullOrWhiteSpace(Nc.Owner) ? "—" : Nc.Owner;
    public string StatoWorkflowTesto => Nc.StatoWorkflow.Label();
    /// <summary>Descrizione breve distinta dalla descrizione completa: se manca nella segnalazione, usa il campo breve
    /// del Test collegato; non duplica mai la descrizione lunga.</summary>
    public string DescrizioneBreveVisualizzata => !string.IsNullOrWhiteSpace(Nc.DescrizioneBreveSegnalata)
        ? Nc.DescrizioneBreveSegnalata
        : TestCase?.DescrizioneBreve ?? "";
    /// <summary>Veicoli della segnalazione e del Test collegato: il primo in ordine alfabetico e "+N" per gli altri.</summary>
    public string VeicoliTesto => (string)VeicoliSommarioConverter.Instance.Convert(
        AnomalieOrdinamentoConTestComparer.VeicoliAssociati(Nc, TestCase), typeof(string), null, System.Globalization.CultureInfo.CurrentCulture)!;
    public string VeicoliElencoTooltip => (string)VeicoliElencoConverter.Instance.Convert(
        AnomalieOrdinamentoConTestComparer.VeicoliAssociati(Nc, TestCase), typeof(string), null, System.Globalization.CultureInfo.CurrentCulture)!;
    public string TicketTesto => Nc.CollegamentiPolarion.Count > 0
        ? string.Join(", ", Nc.CollegamentiPolarion.Select(t => t.Codice))
        : TestCase != null && TestCase.TicketCollegati.Count > 0
            ? string.Join(", ", TestCase.TicketCollegati.Select(t => t.Riferimento)) : "—";
    public bool HaTicketPolarionNc => Nc.CollegamentiPolarion.Count > 0;
    public string MotiviAperturaTesto { get; init; } = "";
    public bool HaMotiviApertura => !string.IsNullOrWhiteSpace(MotiviAperturaTesto);

    /// <summary>Vero per una NC che non ha (ancora) alcun Test collegato — tipicamente una segnalazione dalla
    /// rete assistenziale appena creata (feedback utente 2026-09-08): l'azione "Collega a Test" ha senso solo
    /// finché questo resta vero, un secondo collegamento non sostituirebbe in modo sicuro quello esistente.</summary>
    public bool SenzaTestCollegato => string.IsNullOrWhiteSpace(Nc.TestCaseId) && Nc.ProveCollegate.Count == 0;

    [ObservableProperty] private string nuovoTicketRiferimento = "";
    [ObservableProperty] private string nuovoTicketUrl = "";
    [ObservableProperty] private string testIdDaCollegare = "";

    // Nota (6.4.x): l'editor inline espandibile per riga e' stato sostituito dal dialogo di dettaglio
    // (AnomaliaDettaglioViewModel / AnomaliaDettaglioWindow, bottone 'Dettaglio'): la riga resta ad altezza
    // fissa, cosi' la ListBox virtualizzata non cambia piu' l'altezza delle righe fuori vista (scatti di scroll).
}

public partial class SegnalazioneRiga : ViewModelBase
{
    public SegnalazioneDaClassificare Segnalazione { get; init; } = null!;
    public string TestId => Segnalazione.TestId;

    /// <summary>Etichetta della reiterazione che ha generato la segnalazione (redesign 2026-08: prima era il
    /// titolo di una Verifica, ora è direttamente la reiterazione/Condizione, granularità più fine).</summary>
    public string Reiterazione { get; init; } = "";
    public string Risultato { get; init; } = "";
    /// <summary>Titolo breve calcolato (funzionalita' + componente + sintesi problema), usato come Descrizione della NC creata.</summary>
    public string TitoloAnomalia { get; init; } = "";
    public string Contesto { get; init; } = "";
    [ObservableProperty] private SeveritaAnomalia? severita;
    [ObservableProperty] private string motivazioneEsclusione = "";
}

/// <summary>Filtro tipo della pagina Anomalie/Change Request (feedback utente 2026-08-21, punto 11): le due
/// coesistono sulla stessa pagina, non due viste separate.</summary>
public enum FiltroTipoAnomaliaOCr { Entrambi, Anomalie, ChangeRequest }

/// <summary>Vista dedicata Non Conformità (§5): aggrega da tutti i test case. Barra filtri completa e
/// StatoDettaglio (Addendum §18) aggiunti perché con NC numerose la sola vista "tutte/solo aperte" non
/// bastava più a trovare rapidamente quella cercata.</summary>
public partial class NonConformitaViewModel : ViewModelBase, IPaginaAggiornabile
{
    /// <summary>Stessa dimensione di pagina di LocalTestArchiveQueryService.DefaultPageSize, per coerenza con
    /// Test (feedback utente 2026-09-17, volumi 100k): senza un limite, un ItemsControl a schede (non una
    /// DataGrid virtualizzata) materializza subito il template completo di OGNI riga corrispondente al filtro
    /// — con decine di migliaia di NC aperte questo bloccava l'app (UI non responsive), non solo la rendeva
    /// lenta. Scoperto con un test manuale sull'app reale a scala 100k, invisibile ai soli benchmark del
    /// ViewModel perché quelli non toccano mai il rendering effettivo della UI.</summary>
    private const int RighePageSize = 250;

    private readonly AppState _state;
    /// <summary>Path access for read-only TICKET previews in the quick-edit dialogs.</summary>
    public DataPaths PolarionPaths => _state.Paths;
    public AppState PolarionState => _state;
    public string PolarionOperator => _state.Config.OperatoreAttivo;
    private readonly Action<string?> _apriTestCase;
    private readonly Action<string?> _modificaTestCase;
    private readonly Action<string?> _apriChangeRequest;
    private readonly Action<string?> _apriSegnalazioneRete;

    /// <summary>Richiesta di apertura CR in sola lettura dalla lista (finestra di dettaglio): usata solo se la navigazione
    /// diretta all'editor non e' collegata (<see cref="ApriChangeRequestSolaLettura"/>).</summary>
    public event Action<ChangeRequest>? DettaglioChangeRequestRichiesto;

    /// <summary>"Apri" su una CR: naviga all'editor completo, prima in sola lettura (collegato da MainViewModel).</summary>
    public Action<string?>? ApriChangeRequestSolaLettura { get; set; }

    /// <summary>"Apri" su una NC da segnalazione di rete: naviga all'editor della segnalazione, prima in sola lettura.</summary>
    public Action<string?>? ApriSegnalazioneReteSolaLettura { get; set; }
    private readonly Action? _osservatoreRicarica;

    /// <summary>Batching dei ricalcoli (Addendum prestazioni 2026-09-20, stesso principio già applicato a
    /// Home/Report): senza questo, il costruttore innescava fino a 4 Ricarica() complete (una per ogni
    /// LegaPersistenza con una selezione salvata, piu' quella finale esplicita) e ReimpostaFiltri fino a 8 (una
    /// per ciascuno dei 7 filtri scalari azzerati piu' i 3 multi-select) — a 100k Non Conformità con un filtro
    /// che richiede la risoluzione dei Test collegati, ogni passata e' costosa: ripeterla 4-8 volte per un solo
    /// gesto (apertura pagina con filtri persistiti, o un click su "Azzera filtri") rendeva quel gesto molto
    /// più lento del previsto, mascherato nei benchmark che non esercitavano filtri già attivi dal primo open.</summary>
    private int _refreshInCorso;
    private void RicaricaSeNonInBatch()
    {
        if (_refreshInCorso != 0) return;
        FiltroManualeCambiato(); // un cambio manuale non e' piu' la vista salvata selezionata (vedi VisteSalvate partial)
        Ricarica();
    }

    // BulkObservableCollection (feedback utente 2026-09-17, "anche le NC diventano lente con tanti record"):
    // stesso problema già risolto per Test (Addendum: audit prestazioni) — Clear()+Add() ripetuto per ogni riga
    // costringeva la DataGrid a ricalcolare la tabella una volta per ogni NC/segnalazione/CR invece che una
    // sola volta per l'intero refresh.
    public BulkObservableCollection<NcRiga> Righe { get; } = new();
    public BulkObservableCollection<SegnalazioneRiga> Segnalazioni { get; } = new();

    /// <summary>Change Request del template engine (feedback utente 2026-08-21, punto 11): stessa pagina delle
    /// Anomalie, filtrabile per tipo, non una lista/vista separata — sono TestCase a tutti gli effetti (stesso
    /// motore TestTemplate), solo con IsChangeRequest sul template, quindi qui bastano gli oggetti TestCase
    /// grezzi, nessun wrapper dedicato come NcRiga.</summary>
    public BulkObservableCollection<ChangeRequest> ChangeRequests { get; } = new();

    [ObservableProperty] private FiltroTipoAnomaliaOCr filtroTipo = FiltroTipoAnomaliaOCr.Entrambi;
    public Array TipiFiltroDisponibili => Enum.GetValues(typeof(FiltroTipoAnomaliaOCr));
    public bool MostraAnomalie => FiltroTipo is FiltroTipoAnomaliaOCr.Entrambi or FiltroTipoAnomaliaOCr.Anomalie;
    public bool MostraChangeRequest => FiltroTipo is FiltroTipoAnomaliaOCr.Entrambi or FiltroTipoAnomaliaOCr.ChangeRequest;
    public bool SchedaEntrambi => FiltroTipo == FiltroTipoAnomaliaOCr.Entrambi;
    public bool SchedaAnomalie => FiltroTipo == FiltroTipoAnomaliaOCr.Anomalie;
    public bool SchedaChangeRequest => FiltroTipo == FiltroTipoAnomaliaOCr.ChangeRequest;
    partial void OnFiltroTipoChanged(FiltroTipoAnomaliaOCr value)
    {
        ViewStateService.Scrivi(_state, ChiaveVistaFiltroTipo, value.ToString());
        OnPropertyChanged(nameof(MostraAnomalie)); OnPropertyChanged(nameof(MostraChangeRequest));
        OnPropertyChanged(nameof(SchedaEntrambi)); OnPropertyChanged(nameof(SchedaAnomalie)); OnPropertyChanged(nameof(SchedaChangeRequest));
        FiltroManualeCambiato();
    }
    [RelayCommand] private void MostraEntrambi() => FiltroTipo = FiltroTipoAnomaliaOCr.Entrambi;
    [RelayCommand] private void MostraSoloAnomalie() => FiltroTipo = FiltroTipoAnomaliaOCr.Anomalie;
    [RelayCommand] private void MostraSoloChangeRequest() => FiltroTipo = FiltroTipoAnomaliaOCr.ChangeRequest;

    [ObservableProperty] private NonConformita? selezionata;
    [ObservableProperty] private string nuovaAzione = "";
    [ObservableProperty] private string? messaggio;
    [ObservableProperty] private string? messaggioErrore;
    public bool HaMessaggio => !string.IsNullOrEmpty(Messaggio);
    public bool HaMessaggioErrore => !string.IsNullOrEmpty(MessaggioErrore);
    partial void OnMessaggioChanged(string? value) => OnPropertyChanged(nameof(HaMessaggio));
    partial void OnMessaggioErroreChanged(string? value) => OnPropertyChanged(nameof(HaMessaggioErrore));

    [ObservableProperty] private string filtroTesto = "";
    [ObservableProperty] private StatoNonConformita? filtroStato = StatoNonConformita.Aperta;
    [ObservableProperty] private StatoDettaglioNc? filtroStatoDettaglio;
    [ObservableProperty] private DestinatarioSegnalazione? filtroDestinatario;
    [ObservableProperty] private string filtroTicket = "";
    [ObservableProperty] private FiltroPresenzaTicketEsta filtroTicketEsta;
    public Array OpzioniFiltroTicketEsta => Enum.GetValues(typeof(FiltroPresenzaTicketEsta));
    [ObservableProperty] private StatoChangeRequest? filtroStatoCr;
    [ObservableProperty] private bool filtriCrEspansi;

    /// <summary>Filtri veicolo/gamma/componente (feedback utente 2026-08-24): stesso FiltroMultiSelect già usato
    /// dalla dashboard Home, qui applicati sia alle Anomalie (via il TestCase collegato) sia alle Change
    /// Request (che portano VeicoliImpattati/EcuImpattate proprie, senza bisogno di un TestCase).</summary>
    public FiltroMultiSelect FiltroGamma { get; } = new();
    public FiltroMultiSelect FiltroVeicolo { get; } = new();
    public FiltroMultiSelect FiltroEcu { get; } = new();

    /// <summary>Filtri avanzati combinabili (6.5.0): tutti in AND fra loro, OR dentro ciascuna multi-selezione.
    /// Priorita'/Gravita'/Stato anomalia/Assegnatario si valutano sull'indice SQLite locale; Funzionalita' e i
    /// filtri veicolo/componente/gamma dipendono anche dal Test collegato (vedi AnomalieQueryService). Priorita' e
    /// Gravita' non esistono sulle Change Request: con uno di questi filtri attivo nessuna CR li soddisfa.</summary>
    public FiltroMultiSelect FiltroPriorita { get; } = new();
    public FiltroMultiSelect FiltroGravita { get; } = new();
    public FiltroMultiSelect FiltroStatoAnomalia { get; } = new();
    public FiltroMultiSelect FiltroAssegnatario { get; } = new();
    public FiltroMultiSelect FiltroCreatoreCr { get; } = new();
    public FiltroMultiSelect FiltroFunzionalita { get; } = new();

    /// <summary>Intervalli di data apertura/chiusura, estremi inclusi (giorno intero). Ogni campo e' un CalendarDatePicker
    /// (calendario e scrittura a mano): il valore e' la data selezionata. Una data digitata non valida non applica alcun
    /// vincolo e il campo la segnala (<see cref="SegnalaDataNonValida"/>, messaggio sotto i filtri).</summary>
    [ObservableProperty] private DateTimeOffset? filtroDataAperturaDa;
    [ObservableProperty] private DateTimeOffset? filtroDataAperturaA;
    [ObservableProperty] private DateTimeOffset? filtroDataChiusuraDa;
    [ObservableProperty] private DateTimeOffset? filtroDataChiusuraA;
    [ObservableProperty] private string filtroDurataMinimaGiorni = "";
    [ObservableProperty] private UnitaDurataNc filtroUnitaDurata;
    public Array UnitaDurataDisponibili => Enum.GetValues(typeof(UnitaDurataNc));
    public int? DurataMinimaGiorni => string.IsNullOrWhiteSpace(FiltroDurataMinimaGiorni) ? null :
        int.TryParse(FiltroDurataMinimaGiorni, out var giorni) && giorni >= 0 && giorni <= 365000 ? giorni : -1;
    public bool DurataMinimaNonValida => DurataMinimaGiorni == -1;
    public DateTime? DataAperturaDa => FiltroDataAperturaDa?.Date;
    public DateTime? DataAperturaA => FiltroDataAperturaA?.Date;
    public DateTime? DataChiusuraDa => FiltroDataChiusuraDa?.Date;
    public DateTime? DataChiusuraA => FiltroDataChiusuraA?.Date;

    /// <summary>Segnalazione "data digitata non valida" per campo: il messaggio sotto i filtri resta visibile finche'
    /// l'utente non corregge il valore o sceglie una data dal calendario.</summary>
    [ObservableProperty] private bool dataAperturaDaNonValida;
    [ObservableProperty] private bool dataAperturaANonValida;
    [ObservableProperty] private bool dataChiusuraDaNonValida;
    [ObservableProperty] private bool dataChiusuraANonValida;
    public bool HaDataNonValida => DataAperturaDaNonValida || DataAperturaANonValida || DataChiusuraDaNonValida || DataChiusuraANonValida;

    partial void OnDataAperturaDaNonValidaChanged(bool value) => OnPropertyChanged(nameof(HaDataNonValida));
    partial void OnDataAperturaANonValidaChanged(bool value) => OnPropertyChanged(nameof(HaDataNonValida));
    partial void OnDataChiusuraDaNonValidaChanged(bool value) => OnPropertyChanged(nameof(HaDataNonValida));
    partial void OnDataChiusuraANonValidaChanged(bool value) => OnPropertyChanged(nameof(HaDataNonValida));

    /// <summary>Chiamato quando il testo digitato in un campo data non e' una data valida: il vincolo di quel campo viene
    /// azzerato (mai applicata una data diversa da quella mostrata) e la segnalazione resta visibile.</summary>
    public void SegnalaDataNonValida(string campo)
    {
        switch (campo)
        {
            case nameof(DataAperturaDa): FiltroDataAperturaDa = null; DataAperturaDaNonValida = true; break;
            case nameof(DataAperturaA): FiltroDataAperturaA = null; DataAperturaANonValida = true; break;
            case nameof(DataChiusuraDa): FiltroDataChiusuraDa = null; DataChiusuraDaNonValida = true; break;
            case nameof(DataChiusuraA): FiltroDataChiusuraA = null; DataChiusuraANonValida = true; break;
            default: throw new ArgumentOutOfRangeException(nameof(campo), campo, "Campo data sconosciuto.");
        }
    }

    /// <summary>Rimuove la segnalazione di un campo: il tasto premuto o l'uscita dal campo avviano un nuovo tentativo.</summary>
    public void AzzeraDataNonValida(string campo)
    {
        switch (campo)
        {
            case nameof(DataAperturaDa): DataAperturaDaNonValida = false; break;
            case nameof(DataAperturaA): DataAperturaANonValida = false; break;
            case nameof(DataChiusuraDa): DataChiusuraDaNonValida = false; break;
            case nameof(DataChiusuraA): DataChiusuraANonValida = false; break;
            default: throw new ArgumentOutOfRangeException(nameof(campo), campo, "Campo data sconosciuto.");
        }
    }

    private void AzzeraTutteLeDataNonValide()
    {
        DataAperturaDaNonValida = false; DataAperturaANonValida = false;
        DataChiusuraDaNonValida = false; DataChiusuraANonValida = false;
    }

    private static readonly string[] FormatiData = { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd" };

    internal static DateTime? AnalizzaData(string? testo) =>
        !string.IsNullOrWhiteSpace(testo) && DateTime.TryParseExact(testo.Trim(), FormatiData,
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? d.Date : null;

    /// <summary>Valore di un filtro data letto dallo stato salvato o da una vista: ISO yyyy-MM-dd oppure il vecchio testo
    /// gg/MM/aaaa. Un valore non leggibile non applica alcun vincolo.</summary>
    internal static DateTimeOffset? DataFiltroPersistita(string? testo) =>
        AnalizzaData(testo) is { } d ? new DateTimeOffset(d) : null;

    /// <summary>Numero di filtri attivi (ogni criterio conta 1, indipendentemente da quante voci seleziona).
    /// Mostrato nell'intestazione del pannello filtri anche quando e' collassato.</summary>
    public int NumeroFiltriAttivi =>
        Conta("Testo", !string.IsNullOrWhiteSpace(FiltroTesto)) + Conta("Stato", FiltroStato != null) +
        Conta("StatoDettaglio", FiltroStatoDettaglio != null) + Conta("Destinatario", FiltroDestinatario != null) +
        Conta("StatoCr", FiltroStatoCr != null) + Conta("Ticket", !string.IsNullOrWhiteSpace(FiltroTicket)) +
        Conta("TicketEsta", FiltroTicketEsta != FiltroPresenzaTicketEsta.Tutti) +
        Conta("Gamma", FiltroGamma.HaSelezione) + Conta("Veicolo", FiltroVeicolo.HaSelezione) + Conta("Componente", FiltroEcu.HaSelezione) +
        Conta("Priorita", FiltroPriorita.HaSelezione) + Conta("Gravita", FiltroGravita.HaSelezione) +
        Conta("StatoAnomalia", FiltroStatoAnomalia.HaSelezione) + Conta("Assegnatario", FiltroAssegnatario.HaSelezione) +
        Conta("Funzionalita", FiltroFunzionalita.HaSelezione) + Conta("CreatoreCr", FiltroCreatoreCr.HaSelezione) +
        Conta("DataAperturaDa", DataAperturaDa != null) + Conta("DataAperturaA", DataAperturaA != null) +
        Conta("DataChiusuraDa", DataChiusuraDa != null) + Conta("DataChiusuraA", DataChiusuraA != null) +
        Conta("DurataMinima", DurataMinimaGiorni != null);

    /// <summary>Filtri PRINCIPALI: sempre visibili sopra la lista, esclusi dal contatore del pannello "Altri
    /// filtri". Elenco di configurazione unico (default da confermare con l'utente): per cambiare la scelta si
    /// modifica questo insieme e si sposta il relativo controllo nello XAML. Nomi: Testo, Stato, Assegnatario.</summary>
    internal static readonly HashSet<string> FiltriPrincipali = new() { "Testo", "Stato", "Assegnatario" };

    private static int Conta(string nome, bool attivo) => attivo && !FiltriPrincipali.Contains(nome) ? 1 : 0;

    /// <summary>Vero se QUALUNQUE filtro (principale o no) e' attivo: governa il link "Azzera filtri".</summary>
    public bool HaFiltriAttivi =>
        NumeroFiltriAttivi > 0 || !string.IsNullOrWhiteSpace(FiltroTesto) || FiltroStato != null || FiltroAssegnatario.HaSelezione
        || FiltroTicketEsta != FiltroPresenzaTicketEsta.Tutti;

    /// <summary>Numero dei filtri aggiuntivi della scheda CR. Testo e stato restano sempre visibili nella riga di ricerca.</summary>
    public int NumeroFiltriCrAttivi =>
        ContaCr(FiltroAssegnatario.HaSelezione) + ContaCr(FiltroCreatoreCr.HaSelezione) + ContaCr(!string.IsNullOrWhiteSpace(FiltroTicket)) +
        ContaCr(FiltroGamma.HaSelezione) + ContaCr(FiltroVeicolo.HaSelezione) + ContaCr(FiltroEcu.HaSelezione) +
        ContaCr(FiltroFunzionalita.HaSelezione) + ContaCr(FiltroPriorita.HaSelezione) + ContaCr(FiltroGravita.HaSelezione) +
        ContaCr(DataAperturaDa != null) + ContaCr(DataAperturaA != null) +
        ContaCr(DataChiusuraDa != null) + ContaCr(DataChiusuraA != null);

    public bool HaFiltriCrAttivi => NumeroFiltriCrAttivi > 0 || !string.IsNullOrWhiteSpace(FiltroTesto) || FiltroStatoCr != null
        || FiltroPriorita.HaSelezione || FiltroGravita.HaSelezione;

    public string RiepilogoFiltriCr
    {
        get
        {
            var titolo = LocalizationService.Instance.Traduci("TestListFiltriBtn");
            return NumeroFiltriCrAttivi > 0 ? $"{titolo} ({NumeroFiltriCrAttivi})" : titolo;
        }
    }

    private static int ContaCr(bool attivo) => attivo ? 1 : 0;

    public bool FiltroAssegnatiAMeAttivo
    {
        get => SoloSelezionato(FiltroAssegnatario, _state.Config.OperatoreAttivo);
        set { if (value != FiltroAssegnatiAMeAttivo) ImpostaQuickFilter(FiltroAssegnatario, value); }
    }

    public bool FiltroCreatiDaMeCrAttivo
    {
        get => SoloSelezionato(FiltroCreatoreCr, _state.Config.OperatoreAttivo);
        set { if (value != FiltroCreatiDaMeCrAttivo) ImpostaQuickFilter(FiltroCreatoreCr, value); }
    }

    private static bool SoloSelezionato(FiltroMultiSelect filtro, string? valore) =>
        !string.IsNullOrWhiteSpace(valore) && filtro.Selezionati is { Count: 1 } selezionati
        && selezionati[0].Equals(valore, StringComparison.OrdinalIgnoreCase);

    private void ImpostaQuickFilter(FiltroMultiSelect filtro, bool attivo)
    {
        _refreshInCorso++;
        try
        {
            if (!attivo) filtro.DeselezionaTutto();
            else
            {
                var operatore = _state.Config.OperatoreAttivo;
                foreach (var opzione in filtro.Opzioni) opzione.Selezionato = opzione.Valore.Equals(operatore, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { _refreshInCorso--; }
        PaginaRighe = 1;
        PaginaCr = 1;
        FiltroManualeCambiato();
        RicaricaSeNonInBatch();
        NotificaFiltriAttivi();
        OnPropertyChanged(nameof(FiltroAssegnatiAMeAttivo));
        OnPropertyChanged(nameof(FiltroCreatiDaMeCrAttivo));
    }

    public string RiepilogoFiltri
    {
        get
        {
            var n = NumeroFiltriAttivi;
            var loc = LocalizationService.Instance;
            if (n == 0) return loc.Scegli("Altri filtri", "More filters", "Autres filtres");
            return loc.Scegli($"Altri filtri ({n} attivi)", $"More filters ({n} active)", $"Autres filtres ({n} actifs)");
        }
    }

    /// <summary>Pannello filtri espanso o collassato. Parte SEMPRE collassato (non occupa spazio) e lo stato
    /// aperto/chiuso NON viene ricordato; i VALORI dei filtri invece si mantengono fra una sessione e l'altra
    /// (ViewStateService, config.json locale del PC, mai nel file condiviso). Collassato, i filtri restano
    /// applicati e l'intestazione riporta "Filtri (N attivi)".</summary>
    [ObservableProperty] private bool filtriEspansi;

    private void NotificaFiltriAttivi()
    {
        OnPropertyChanged(nameof(HaFiltriAttivi));
        OnPropertyChanged(nameof(NumeroFiltriAttivi));
        OnPropertyChanged(nameof(RiepilogoFiltri));
        OnPropertyChanged(nameof(HaFiltriCrAttivi));
        OnPropertyChanged(nameof(NumeroFiltriCrAttivi));
        OnPropertyChanged(nameof(RiepilogoFiltriCr));
    }

    /// <summary>Ordinamento per colonna sull'intero risultato filtrato (non sulla sola pagina). Un secondo clic
    /// sulla stessa colonna inverte la direzione.</summary>
    [ObservableProperty] private AnomalieOrdinamentoChiave ordinamentoChiave = AnomalieOrdinamentoChiave.Apertura;
    [ObservableProperty] private bool ordinamentoDecrescente = true;
    public AnomalieOrdinamento Ordinamento => new(OrdinamentoChiave, OrdinamentoDecrescente);

    public void ImpostaOrdinamento(AnomalieOrdinamentoChiave chiave)
    {
        // Un solo ricalcolo anche quando cambiano insieme chiave e direzione.
        _refreshInCorso++;
        try
        {
            if (OrdinamentoChiave == chiave) OrdinamentoDecrescente = !OrdinamentoDecrescente;
            else { OrdinamentoChiave = chiave; OrdinamentoDecrescente = false; }
        }
        finally { _refreshInCorso--; }
        PaginaRighe = 1;
        Ricarica();
    }

    partial void OnOrdinamentoChiaveChanged(AnomalieOrdinamentoChiave value)
    {
        ViewStateService.Scrivi(_state, ChiaveVistaOrdinamentoChiave, value.ToString());
        OnPropertyChanged(nameof(Ordinamento));
        PaginaRighe = 1;
        RicaricaSeNonInBatch();
    }

    partial void OnOrdinamentoDecrescenteChanged(bool value)
    {
        ViewStateService.Scrivi(_state, ChiaveVistaOrdinamentoDecrescente, value ? "1" : "0");
        OnPropertyChanged(nameof(Ordinamento));
        PaginaRighe = 1;
        RicaricaSeNonInBatch();
    }

    [RelayCommand] private void OrdinaPer(string? chiave)
    {
        if (TryEnum<AnomalieOrdinamentoChiave>(chiave, out var k)) ImpostaOrdinamento(k);
    }

    /// <summary>Ordinamento della tabella Change Request sull'intero risultato filtrato. Un secondo clic sulla stessa colonna inverte la
    /// direzione; la scelta resta salvata per operatore insieme agli altri stati di vista.</summary>
    [ObservableProperty] private ChangeRequestOrdinamentoChiave ordinamentoCrChiave = ChangeRequestOrdinamentoChiave.Apertura;
    [ObservableProperty] private bool ordinamentoCrDecrescente = true;

    public void ImpostaOrdinamentoCr(ChangeRequestOrdinamentoChiave chiave)
    {
        // Un solo ricalcolo anche quando cambiano insieme chiave e direzione.
        _refreshInCorso++;
        try
        {
            if (OrdinamentoCrChiave == chiave) OrdinamentoCrDecrescente = !OrdinamentoCrDecrescente;
            else { OrdinamentoCrChiave = chiave; OrdinamentoCrDecrescente = chiave is ChangeRequestOrdinamentoChiave.Apertura
                or ChangeRequestOrdinamentoChiave.Implementazione or ChangeRequestOrdinamentoChiave.Chiusura; }
        }
        finally { _refreshInCorso--; }
        PaginaCr = 1;
        RicaricaChangeRequest();
    }

    partial void OnOrdinamentoCrChiaveChanged(ChangeRequestOrdinamentoChiave value) =>
        ViewStateService.Scrivi(_state, ChiaveVistaOrdinamentoCrChiave, value.ToString());

    partial void OnOrdinamentoCrDecrescenteChanged(bool value) =>
        ViewStateService.Scrivi(_state, ChiaveVistaOrdinamentoCrDecrescente, value ? "1" : "0");

    /// <summary>Vero mentre un aggiornamento in background dell'elenco e' in corso.</summary>
    [ObservableProperty] private bool inCaricamento;

    public Array Destinatari => Enum.GetValues(typeof(DestinatarioSegnalazione));
    public Array CauseProbabili => Enum.GetValues(typeof(CausaProbabileUdt));
    public Array StatiNonConformita => Enum.GetValues(typeof(StatoNonConformita));
    public Array StatiDettaglio => Enum.GetValues(typeof(StatoDettaglioNc));
    public Array SeveritaDisponibili => Enum.GetValues(typeof(SeveritaAnomalia));
    public Array PrioritaDisponibili => Enum.GetValues(typeof(PrioritaOperativa));
    public Array StatiWorkflow => Enum.GetValues(typeof(StatoAnomalia));
    public Array StatiChangeRequest => Enum.GetValues(typeof(StatoChangeRequest));
    public bool PuoModificare => _state.PuoScrivere;

    /// <summary>La Priorità interna è assegnabile solo da un Master (feedback utente 2026-08-20, punto 18),
    /// in qualunque momento del ciclo di vita — non solo alla creazione.</summary>
    public bool EMaster => _state.EMaster;
    public bool HaSegnalazioni => TotaleSegnalazioni > 0;

    [ObservableProperty] private long paginaRighe = 1;
    public long TotaleRighe { get; private set; }
    public long PagineTotaliRighe => Math.Max(1L, checked((TotaleRighe + RighePageSize - 1L) / RighePageSize));
    public bool PuoPaginaRighePrecedente => PaginaRighe > 1;
    public bool PuoPaginaRigheSuccessiva => PaginaRighe < PagineTotaliRighe;
    public string TestoPaginazioneRighe => LocalizationService.Instance.Scegli(
        $"Pagina {PaginaRighe} / {PagineTotaliRighe} · {TotaleRighe:N0} non conformità",
        $"Page {PaginaRighe} / {PagineTotaliRighe} · {TotaleRighe:N0} non-conformities",
        $"Page {PaginaRighe} / {PagineTotaliRighe} · {TotaleRighe:N0} non-conformités");

    private void NotificaPaginazioneRighe()
    {
        OnPropertyChanged(nameof(TotaleRighe));
        OnPropertyChanged(nameof(PagineTotaliRighe));
        OnPropertyChanged(nameof(PuoPaginaRighePrecedente));
        OnPropertyChanged(nameof(PuoPaginaRigheSuccessiva));
        OnPropertyChanged(nameof(TestoPaginazioneRighe));
    }

    [RelayCommand]
    private void PaginaRighePrecedente()
    {
        if (!PuoPaginaRighePrecedente) return;
        PaginaRighe--;
        Ricarica();
    }

    [RelayCommand]
    private void PaginaRigheSuccessiva()
    {
        if (!PuoPaginaRigheSuccessiva) return;
        PaginaRighe++;
        Ricarica();
    }

    [RelayCommand]
    private void PrimaPaginaRighe()
    {
        if (PaginaRighe == 1) return;
        PaginaRighe = 1;
        Ricarica();
    }

    /// <summary>Paginazione della lista Change Request, stesso schema e stessa dimensione pagina di Righe
    /// (feedback utente 2026-09-24, coerenza con la correzione prestazioni Anomalie): prima RicaricaChangeRequest
    /// materializzava l'intero elenco filtrato in un ItemsControl non virtualizzato — con poche centinaia di CR
    /// il costo è modesto rispetto all'editor pesante delle Anomalie, ma senza un limite esplicito lo stesso
    /// problema si ripresenterebbe a volumi maggiori.</summary>
    [ObservableProperty] private long paginaCr = 1;
    public long TotaleCr { get; private set; }
    public long PagineTotaliCr => Math.Max(1L, checked((TotaleCr + RighePageSize - 1L) / RighePageSize));
    public bool PuoPaginaCrPrecedente => PaginaCr > 1;
    public bool PuoPaginaCrSuccessiva => PaginaCr < PagineTotaliCr;
    public string TestoPaginazioneCr => LocalizationService.Instance.Scegli(
        $"Pagina {PaginaCr} / {PagineTotaliCr} · {TotaleCr:N0} change request",
        $"Page {PaginaCr} / {PagineTotaliCr} · {TotaleCr:N0} change requests",
        $"Page {PaginaCr} / {PagineTotaliCr} · {TotaleCr:N0} change requests");

    private void NotificaPaginazioneCr()
    {
        OnPropertyChanged(nameof(TotaleCr));
        OnPropertyChanged(nameof(PagineTotaliCr));
        OnPropertyChanged(nameof(PuoPaginaCrPrecedente));
        OnPropertyChanged(nameof(PuoPaginaCrSuccessiva));
        OnPropertyChanged(nameof(TestoPaginazioneCr));
    }

    [RelayCommand]
    private void PaginaCrPrecedente()
    {
        if (!PuoPaginaCrPrecedente) return;
        PaginaCr--;
        RicaricaChangeRequest();
    }

    [RelayCommand]
    private void PaginaCrSuccessiva()
    {
        if (!PuoPaginaCrSuccessiva) return;
        PaginaCr++;
        RicaricaChangeRequest();
    }

    [RelayCommand]
    private void PrimaPaginaCr()
    {
        if (PaginaCr == 1) return;
        PaginaCr = 1;
        RicaricaChangeRequest();
    }

    // ------------------------------------------------------------------ segnalazioni da classificare: pagina nel VM

    /// <summary>Righe per pagina della lista Segnalazioni (la vista riceve solo la pagina corrente, non l'intero elenco).</summary>
    public const int SegnalazioniPerPagina = 100;

    /// <summary>Numero di segnalazioni da classificare nell'intero elenco, non solo nella pagina visibile.</summary>
    public long TotaleSegnalazioni { get; private set; }
    [ObservableProperty] private long paginaSegnalazioni = 1;
    public long PagineTotaliSegnalazioni => Math.Max(1L, checked((TotaleSegnalazioni + SegnalazioniPerPagina - 1L) / SegnalazioniPerPagina));
    public bool PuoPaginaSegnalazioniPrecedente => PaginaSegnalazioni > 1;
    public bool PuoPaginaSegnalazioniSuccessiva => PaginaSegnalazioni < PagineTotaliSegnalazioni;
    public string TestoPaginazioneSegnalazioni => LocalizationService.Instance.Scegli(
        $"Pagina {PaginaSegnalazioni} / {PagineTotaliSegnalazioni} · {TotaleSegnalazioni:N0} segnalazioni da classificare",
        $"Page {PaginaSegnalazioni} / {PagineTotaliSegnalazioni} · {TotaleSegnalazioni:N0} reports to classify",
        $"Page {PaginaSegnalazioni} / {PagineTotaliSegnalazioni} · {TotaleSegnalazioni:N0} signalements à classer");

    private void NotificaPaginazioneSegnalazioni()
    {
        OnPropertyChanged(nameof(TotaleSegnalazioni));
        OnPropertyChanged(nameof(PagineTotaliSegnalazioni));
        OnPropertyChanged(nameof(PuoPaginaSegnalazioniPrecedente));
        OnPropertyChanged(nameof(PuoPaginaSegnalazioniSuccessiva));
        OnPropertyChanged(nameof(TestoPaginazioneSegnalazioni));
    }

    [RelayCommand]
    private void PaginaSegnalazioniPrecedente()
    {
        if (!PuoPaginaSegnalazioniPrecedente) return;
        PaginaSegnalazioni--;
        AggiornaPaginaSegnalazioni();
    }

    [RelayCommand]
    private void PaginaSegnalazioniSuccessiva()
    {
        if (!PuoPaginaSegnalazioniSuccessiva) return;
        PaginaSegnalazioni++;
        AggiornaPaginaSegnalazioni();
    }

    [RelayCommand]
    private void PrimaPaginaSegnalazioni()
    {
        if (PaginaSegnalazioni == 1) return;
        PaginaSegnalazioni = 1;
        AggiornaPaginaSegnalazioni();
    }

    public NonConformitaViewModel(AppState state, Action<string?> apriTestCase, Action<string?>? modificaTestCase = null,
        Action<string?>? apriChangeRequest = null, Action<string?>? apriSegnalazioneRete = null)
        : this(state, apriTestCase, modificaTestCase, apriChangeRequest, apriSegnalazioneRete, null)
    {
    }

    internal NonConformitaViewModel(AppState state, Action<string?> apriTestCase, Action<string?>? modificaTestCase,
        Action<string?>? apriChangeRequest, Action<string?>? apriSegnalazioneRete, Action? osservatoreRicarica)
    {
        _state = state;
        _apriTestCase = apriTestCase;
        _modificaTestCase = modificaTestCase ?? apriTestCase;
        _apriChangeRequest = apriChangeRequest ?? (_ => { });
        _apriSegnalazioneRete = apriSegnalazioneRete ?? (_ => { });
        _osservatoreRicarica = osservatoreRicarica;

        foreach (var f in new[] { FiltroGamma, FiltroVeicolo, FiltroEcu, FiltroPriorita, FiltroGravita, FiltroStatoAnomalia, FiltroAssegnatario, FiltroFunzionalita, FiltroCreatoreCr })
            f.Cambiato += () => { PaginaRighe = 1; PaginaCr = 1; FiltroManualeCambiato(); NotificaFiltriAttivi(); RicaricaSeNonInBatch(); };
        FiltroAssegnatario.Cambiato += () => OnPropertyChanged(nameof(FiltroAssegnatiAMeAttivo));
        FiltroCreatoreCr.Cambiato += () => OnPropertyChanged(nameof(FiltroCreatiDaMeCrAttivo));

        // Opzioni statiche (enum) dei nuovi filtri multi-selezione; le etichette seguono la lingua corrente.
        FiltroPriorita.Popola(Enum.GetValues<PrioritaOperativa>().Select(p => (p.ToString(), p.Label())));
        FiltroGravita.Popola(Enum.GetValues<SeveritaAnomalia>().Select(s => (s.ToString(), s.Label())));
        FiltroStatoAnomalia.Popola(Enum.GetValues<StatoAnomalia>().Select(s => (s.ToString(), s.Label())));

        // Il costruttore deve soltanto preparare le opzioni prima di ripristinare le selezioni persistite.
        // Aggiorna() eseguirebbe anche Ricarica(), causando una scansione completa prima del ripristino e una
        // seconda subito dopo. Il refresh completo viene eseguito una sola volta in fondo al bootstrap.
        RipopolaFiltri();

        // Ultimi filtri persistiti per questo PC/operatore (feedback utente 2026-09-18: "la stabilita' deve
        // rimanere anche in Test, CR/anomalie ecc, tutte le pagine"). Testo/Ticket ESCLUSI di proposito: sono
        // campi di ricerca libera digitati carattere per carattere, scriverli ad ogni tasto sarebbe I/O
        // sprecato per un valore che l'utente svuota e riusa in continuazione (stessa scelta di FiltroTesto
        // in TestCaseListViewModel). DOPO Aggiorna(): i tre FiltroMultiSelect devono avere le Opzioni reali
        // già costruite prima che LegaPersistenza possa incrociarle con la selezione salvata.
        filtroTipo = TryEnum<FiltroTipoAnomaliaOCr>(ViewStateService.Leggi(state, ChiaveVistaFiltroTipo), out var tipo) ? tipo : filtroTipo;
        filtroStato = TryEnum<StatoNonConformita>(ViewStateService.Leggi(state, ChiaveVistaFiltroStato), out var stato) ? stato : filtroStato;
        filtroStatoDettaglio = TryEnum<StatoDettaglioNc>(ViewStateService.Leggi(state, ChiaveVistaFiltroStatoDettaglio), out var dettaglio) ? dettaglio : null;
        filtroTicketEsta = TryEnum<FiltroPresenzaTicketEsta>(ViewStateService.Leggi(state, ChiaveVistaFiltroTicketEsta), out var ticketEsta) ? ticketEsta : FiltroPresenzaTicketEsta.Tutti;
        ordinamentoChiave = TryEnum<AnomalieOrdinamentoChiave>(ViewStateService.Leggi(state, ChiaveVistaOrdinamentoChiave), out var ordChiave) ? ordChiave : ordinamentoChiave;
        ordinamentoDecrescente = ViewStateService.Leggi(state, ChiaveVistaOrdinamentoDecrescente) is { } ordDir ? ordDir == "1" : ordinamentoDecrescente;
        ordinamentoCrChiave = TryEnum<ChangeRequestOrdinamentoChiave>(ViewStateService.Leggi(state, ChiaveVistaOrdinamentoCrChiave), out var ordCrChiave) ? ordCrChiave : ordinamentoCrChiave;
        ordinamentoCrDecrescente = ViewStateService.Leggi(state, ChiaveVistaOrdinamentoCrDecrescente) is { } ordCrDir ? ordCrDir == "1" : ordinamentoCrDecrescente;
        filtroDataAperturaDa = DataFiltroPersistita(ViewStateService.Leggi(state, ChiaveVistaDataAperturaDa));
        filtroDataAperturaA = DataFiltroPersistita(ViewStateService.Leggi(state, ChiaveVistaDataAperturaA));
        filtroDataChiusuraDa = DataFiltroPersistita(ViewStateService.Leggi(state, ChiaveVistaDataChiusuraDa));
        filtroDataChiusuraA = DataFiltroPersistita(ViewStateService.Leggi(state, ChiaveVistaDataChiusuraA));
        filtroDurataMinimaGiorni = ViewStateService.Leggi(state, "AnomalieCr.DurataMinimaGiorni") ?? "";
        filtroUnitaDurata = Enum.TryParse<UnitaDurataNc>(ViewStateService.Leggi(state, "AnomalieCr.UnitaDurata"), out var unitaDurata) && Enum.IsDefined(unitaDurata) ? unitaDurata : UnitaDurataNc.Giorni;
        _dateApplicate = new[] { DataAperturaDa, DataAperturaA, DataChiusuraDa, DataChiusuraA };
        filtroDestinatario = TryEnum<DestinatarioSegnalazione>(ViewStateService.Leggi(state, ChiaveVistaFiltroDestinatario), out var destinatario) ? destinatario : null;
        filtroStatoCr = TryEnum<StatoChangeRequest>(ViewStateService.Leggi(state, ChiaveVistaFiltroStatoCr), out var statoCr) ? statoCr : null;
        // _refreshInCorso qui: ciascuna delle 3 LegaPersistenza sotto, se trova una selezione salvata, scatena
        // il proprio Cambiato -> Ricarica() (vedi il guard sopra) — senza batching sarebbero fino a 3 passate
        // complete sull'archivio prima ancora di quella finale esplicita qui sotto.
        _refreshInCorso++;
        try
        {
            FiltroGamma.LegaPersistenza(state, ChiaveVistaFiltroGamma);
            FiltroVeicolo.LegaPersistenza(state, ChiaveVistaFiltroVeicolo);
            FiltroEcu.LegaPersistenza(state, ChiaveVistaFiltroEcu);
            FiltroPriorita.LegaPersistenza(state, ChiaveVistaFiltroPriorita);
            FiltroGravita.LegaPersistenza(state, ChiaveVistaFiltroGravita);
            FiltroStatoAnomalia.LegaPersistenza(state, ChiaveVistaFiltroStatoAnomalia);
            FiltroFunzionalita.LegaPersistenza(state, ChiaveVistaFiltroFunzionalita);
            // Assegnatario: le opzioni dipendono dai dati, quindi vanno popolate PRIMA di legare la persistenza
            // (LegaPersistenza riscrive la selezione effettiva: con opzioni vuote cancellerebbe quella salvata).
            // Il vecchio filtro Owner a valore singolo (<= 6.4) viene assorbito nella selezione multipla.
            PopolaAssegnatari(LeggiAssegnatariDaDati());
            FiltroAssegnatario.LegaPersistenza(state, ChiaveVistaFiltroAssegnatario);
            PopolaCreatoriCr();
            FiltroCreatoreCr.LegaPersistenza(state, ChiaveVistaFiltroCreatoreCr);
            if (ViewStateService.Leggi(state, ChiaveVistaFiltroOwnerLegacy) is { Length: > 0 } ownerLegacy)
            {
                if (FiltroAssegnatario.Opzioni.FirstOrDefault(o => o.Valore == ownerLegacy) is { } opzione) opzione.Selezionato = true;
                ViewStateService.Scrivi(state, ChiaveVistaFiltroOwnerLegacy, null);
            }
        }
        finally { _refreshInCorso--; }
        PaginaRighe = 1;
        PaginaCr = 1;
        Ricarica();
        CaricaVisteSalvate();
        _costruito = true;
    }

    private bool _costruito;

    private IReadOnlyList<string> LeggiAssegnatariDaDati()
    {
        if (!_state.CartellaValida) return Array.Empty<string>();
        // L'elenco deriva dall'intero archivio (Distinct su 100k stringhe ~30ms): si ricalcola solo se i dati
        // sono cambiati dall'ultima lettura (contatore di mutazione del repository).
        var generazione = _state.NonConformita.GenerazioneDati;
        var cache = _assegnatariCache;
        if (cache != null && cache.Generazione == generazione) return cache.Elenco;
        var ncOwners = _state.NonConformita.TryAssegnatariDistinti(out var daIndice)
            ? daIndice
            : _state.NonConformita.PeekAllReadOnly().Select(n => n.Owner).Where(o => !string.IsNullOrWhiteSpace(o));
        var elenco = ncOwners.Concat(_state.ChangeRequests.PeekAllReadOnly().Select(cr => cr.Responsabile))
            .Append(_state.Config.OperatoreAttivo).Where(o => !string.IsNullOrWhiteSpace(o))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        _assegnatariCache = new AssegnatariCache(generazione, elenco);
        return elenco;
    }

    private void PopolaCreatoriCr()
    {
        var creatori = _state.CartellaValida
            ? _state.ChangeRequests.PeekAllReadOnly().Select(cr => cr.CreatoDa).Append(_state.Config.OperatoreAttivo)
                .Where(o => !string.IsNullOrWhiteSpace(o)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(o => o, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();
        FiltroCreatoreCr.Ripopola(ConSelezionatiMancanti(FiltroCreatoreCr, creatori));
    }

    private sealed record AssegnatariCache(long Generazione, IReadOnlyList<string> Elenco);
    private volatile AssegnatariCache? _assegnatariCache;

    /// <summary>Come Enum.TryParse ma rifiuta valori numerici non definiti (es. "99") letti da una config obsoleta o manomessa.</summary>
    internal static bool TryEnum<T>(string? testo, out T valore) where T : struct, Enum =>
        Enum.TryParse(testo, out valore) && Enum.IsDefined(valore);

    private CancellationTokenSource? _debounceTesto;

    /// <summary>Ricerca libera/ticket: digitata carattere per carattere. La parte sincrona di Ricarica (Change
    /// Request, segnalazioni da classificare) gira sul thread UI, quindi in UI reale si attende una breve pausa
    /// di digitazione (250 ms) prima di ricalcolare; senza UI (test, costruttore) il ricalcolo e' immediato.</summary>
    private void RicaricaTestoDebounced()
    {
        if (!InBackground) { RicaricaSeNonInBatch(); return; }
        _debounceTesto?.Cancel();
        var cts = _debounceTesto = new CancellationTokenSource();
        _ = Task.Delay(250, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _ = SuThreadUi(() => { if (!cts.IsCancellationRequested) RicaricaSeNonInBatch(); });
        }, TaskScheduler.Default);
    }

    private void PopolaAssegnatari(IReadOnlyList<string> assegnatari)
    {
        _refreshInCorso++;
        try { FiltroAssegnatario.Ripopola(ConSelezionatiMancanti(FiltroAssegnatario, assegnatari)); }
        finally { _refreshInCorso--; }
    }

    private const string ChiaveVistaFiltroTipo = "AnomalieCr.FiltroTipo";
    private const string ChiaveVistaFiltroStato = "AnomalieCr.FiltroStato";
    private const string ChiaveVistaFiltroStatoDettaglio = "AnomalieCr.FiltroStatoDettaglio";
    private const string ChiaveVistaFiltroTicketEsta = "AnomalieCr.FiltroTicketEsta";
    private const string ChiaveVistaFiltroOwnerLegacy = "AnomalieCr.FiltroOwner";
    private const string ChiaveVistaFiltroAssegnatario = "AnomalieCr.FiltroAssegnatario";
    private const string ChiaveVistaFiltroPriorita = "AnomalieCr.FiltroPriorita";
    private const string ChiaveVistaFiltroGravita = "AnomalieCr.FiltroGravita";
    private const string ChiaveVistaFiltroStatoAnomalia = "AnomalieCr.FiltroStatoAnomalia";
    private const string ChiaveVistaFiltroFunzionalita = "AnomalieCr.FiltroFunzionalita";
    private const string ChiaveVistaDataAperturaDa = "AnomalieCr.DataAperturaDa";
    private const string ChiaveVistaDataAperturaA = "AnomalieCr.DataAperturaA";
    private const string ChiaveVistaDataChiusuraDa = "AnomalieCr.DataChiusuraDa";
    private const string ChiaveVistaDataChiusuraA = "AnomalieCr.DataChiusuraA";
    private const string ChiaveVistaOrdinamentoChiave = "AnomalieCr.OrdinamentoChiave";
    private const string ChiaveVistaOrdinamentoDecrescente = "AnomalieCr.OrdinamentoDecrescente";
    private const string ChiaveVistaOrdinamentoCrChiave = "AnomalieCr.OrdinamentoCrChiave";
    private const string ChiaveVistaOrdinamentoCrDecrescente = "AnomalieCr.OrdinamentoCrDecrescente";
    private const string ChiaveVistaFiltroDestinatario = "AnomalieCr.FiltroDestinatario";
    private const string ChiaveVistaFiltroStatoCr = "AnomalieCr.FiltroStatoCr";
    private const string ChiaveVistaFiltroGamma = "AnomalieCr.FiltroGamma";
    private const string ChiaveVistaFiltroVeicolo = "AnomalieCr.FiltroVeicolo";
    private const string ChiaveVistaFiltroEcu = "AnomalieCr.FiltroEcu";
    private const string ChiaveVistaFiltroCreatoreCr = "AnomalieCr.FiltroCreatoreCr";

    public NonConformitaViewModel(AppState state, Action<string?> apriTestCase, Action<string?>? modificaTestCase, Action creaChangeRequest)
        : this(state, apriTestCase, modificaTestCase, _ => creaChangeRequest()) { }

    /// <summary>Ripopola le opzioni dei 3 filtri (preservando la selezione, FiltroMultiSelect.Ripopola) e
    /// rilancia Ricarica() (Addendum: audit prestazioni 2026-08-25, IPaginaAggiornabile — questa pagina resta
    /// viva tra un cambio scheda e l'altro invece di essere ricreata).</summary>
    public void Aggiorna()
    {
        // Una sola lettura logica dei master per l'intera pagina, non due: RipopolaFiltri() e Ricarica() aprono
        // ciascuna il proprio MasterLetturaScope quando girano da sole (filtro cambiato, dopo un salvataggio),
        // ma chiamate qui in sequenza SENZA questo scope esterno restavano due letture separate — due scansioni
        // complete della cartella condivisa ad ogni cambio pagina invece di una sola (misurato >30s a freddo su
        // Anomalie/CR anche dopo il primo accorpamento, feedback utente 2026-09-24). Annidando qui, entrambe le
        // chiamate sotto vedono lo scope gia' aperto e riusano la stessa lettura.
        using var letturaMaster = MasterLetturaScope.Apri();
        _assegnatariCache = null;
        RipopolaFiltri();
        Ricarica();
    }

    private void RipopolaFiltri()
    {
        // Una sola lettura logica dei master per questo passo (stesso principio di HomeViewModel/ReportViewModel,
        // MasterLetturaScope): senza questo, ognuna delle tre Load() sotto rifaceva per conto suo l'intera
        // scansione/ricostruzione Phase D della cartella condivisa (feedback utente 2026-09-24: "il passaggio da
        // una pagina all'altra è molto lento", misurato ~49s a freddo sulla pagina Anomalie/CR con soli 575 NC
        // e 507 Test proprio perché mancava questo accorpamento, già presente invece in Home/Report).
        using var letturaMaster = MasterLetturaScope.Apri();
        // Batching (stesso motivo del costruttore): Ripopola su un filtro con una selezione già presente la
        // riapplica e scatena il proprio Cambiato -> Ricarica(), fino a 3 passate prima di quella finale qui
        // sotto se non fosse per il guard _refreshInCorso.
        // ConSelezionatiMancanti: i valori selezionati fuori catalogo (es. da una vista salvata) restano opzioni
        // selezionate; senza, Ripopola li scarterebbe e il filtro si allargherebbe in silenzio al refresh successivo.
        _refreshInCorso++;
        try
        {
            var cr = _state.CartellaValida ? _state.ChangeRequests.PeekAllReadOnly() : Array.Empty<ChangeRequest>();
            var veicoliCr = cr.SelectMany(c => c.VeicoliImpattati).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            var ecuCr = cr.SelectMany(c => c.EcuImpattate).Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
            var funzionalitaCr = cr.SelectMany(c => c.FunzionalitaImpattate).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            var veicoliMaster = _state.CartellaValida ? _state.Master.Veicoli.Load() : Enumerable.Empty<Veicolo>();
            var opzioniGamma = (_state.CartellaValida
                    ? _state.Master.Gamma.Load().Where(g => !g.Archiviata).Select(g => g.Nome)
                    : Enumerable.Empty<string>())
                .Concat(veicoliMaster.Where(v => veicoliCr.Contains(v.Nome, StringComparer.OrdinalIgnoreCase)).Select(v => v.GammaNome));
            var opzioniVeicolo = veicoliMaster.Where(v => !v.Archiviata).Select(v => v.Nome).Concat(veicoliCr);
            var opzioniEcu = (_state.CartellaValida
                    ? _state.Master.Componente.Load().Where(e => !e.Archiviata).Select(e => e.Nome)
                    : Enumerable.Empty<string>())
                .Concat(ecuCr);
            var opzioniFunzionalita = (_state.CartellaValida
                    ? _state.Master.Funzionalita.Load().Select(f => f.Nome)
                    : Enumerable.Empty<string>())
                .Concat(funzionalitaCr);
            static IEnumerable<string> UniciOrdinati(IEnumerable<string> valori) => valori
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase);

            // I cataloghi master coprono i valori correnti; i valori direttamente referenziati dalle CR
            // restano filtrabili anche se la voce è stata archiviata o non è più nel catalogo.
            FiltroGamma.Ripopola(ConSelezionatiMancanti(FiltroGamma, UniciOrdinati(opzioniGamma)));
            FiltroVeicolo.Ripopola(ConSelezionatiMancanti(FiltroVeicolo, UniciOrdinati(opzioniVeicolo)));
            FiltroEcu.Ripopola(ConSelezionatiMancanti(FiltroEcu, UniciOrdinati(opzioniEcu)));
            FiltroFunzionalita.Ripopola(ConSelezionatiMancanti(FiltroFunzionalita, UniciOrdinati(opzioniFunzionalita)));
            _assegnatariCache = null;
            PopolaCreatoriCr();
        }
        finally { _refreshInCorso--; }
    }

    partial void OnFiltroTestoChanged(string value) { PaginaRighe = 1; PaginaCr = 1; NotificaFiltriAttivi(); RicaricaTestoDebounced(); }
    partial void OnFiltroStatoChanged(StatoNonConformita? value) { ViewStateService.Scrivi(_state, ChiaveVistaFiltroStato, value?.ToString()); PaginaRighe = 1; RicaricaSeNonInBatch(); }
    partial void OnFiltroStatoDettaglioChanged(StatoDettaglioNc? value) { ViewStateService.Scrivi(_state, ChiaveVistaFiltroStatoDettaglio, value?.ToString()); PaginaRighe = 1; RicaricaSeNonInBatch(); }
    partial void OnFiltroDataAperturaDaChanged(DateTimeOffset? value) => DataCambiata(nameof(DataAperturaDa), ChiaveVistaDataAperturaDa, value);
    partial void OnFiltroDataAperturaAChanged(DateTimeOffset? value) => DataCambiata(nameof(DataAperturaA), ChiaveVistaDataAperturaA, value);
    partial void OnFiltroDataChiusuraDaChanged(DateTimeOffset? value) => DataCambiata(nameof(DataChiusuraDa), ChiaveVistaDataChiusuraDa, value);
    partial void OnFiltroDataChiusuraAChanged(DateTimeOffset? value) => DataCambiata(nameof(DataChiusuraA), ChiaveVistaDataChiusuraA, value);
    partial void OnFiltroDurataMinimaGiorniChanged(string value)
    {
        ViewStateService.Scrivi(_state, "AnomalieCr.DurataMinimaGiorni", value);
        OnPropertyChanged(nameof(DurataMinimaGiorni));
        OnPropertyChanged(nameof(DurataMinimaNonValida));
        NotificaFiltriAttivi();
        PaginaRighe = 1;
        RicaricaSeNonInBatch();
    }
    partial void OnFiltroUnitaDurataChanged(UnitaDurataNc value)
    {
        ViewStateService.Scrivi(_state, "AnomalieCr.UnitaDurata", value.ToString());
        PaginaRighe = 1;
        if (DurataMinimaGiorni.HasValue) RicaricaSeNonInBatch();
    }

    private DateTime?[] _dateApplicate = new DateTime?[4];

    /// <summary>Un cambio di data ricarica l'elenco solo se cambia la data effettivamente applicata (niente passate
    /// inutili). Una data valida rimuove la segnalazione del campo; il valore persistito e' sempre una data o vuoto.</summary>
    private void DataCambiata(string proprieta, string chiaveVista, DateTimeOffset? valore)
    {
        var indice = proprieta switch { nameof(DataAperturaDa) => 0, nameof(DataAperturaA) => 1, nameof(DataChiusuraDa) => 2, _ => 3 };
        var nuova = valore?.Date;
        if (nuova != null) AzzeraDataNonValida(proprieta);
        OnPropertyChanged(proprieta);
        NotificaFiltriAttivi();
        ViewStateService.Scrivi(_state, chiaveVista, nuova?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        if (_dateApplicate[indice] == nuova) return;
        _dateApplicate[indice] = nuova;
        PaginaRighe = 1; PaginaCr = 1;
        RicaricaSeNonInBatch();
    }
    partial void OnFiltroDestinatarioChanged(DestinatarioSegnalazione? value) { ViewStateService.Scrivi(_state, ChiaveVistaFiltroDestinatario, value?.ToString()); PaginaRighe = 1; RicaricaSeNonInBatch(); }
    partial void OnFiltroTicketChanged(string value) { PaginaRighe = 1; PaginaCr = 1; NotificaFiltriAttivi(); RicaricaTestoDebounced(); }
    partial void OnFiltroTicketEstaChanged(FiltroPresenzaTicketEsta value)
    {
        ViewStateService.Scrivi(_state, ChiaveVistaFiltroTicketEsta, value == FiltroPresenzaTicketEsta.Tutti ? null : value.ToString());
        PaginaRighe = 1;
        NotificaFiltriAttivi();
        RicaricaSeNonInBatch();
    }
    partial void OnFiltroStatoCrChanged(StatoChangeRequest? value) { ViewStateService.Scrivi(_state, ChiaveVistaFiltroStatoCr, value?.ToString()); PaginaCr = 1; NotificaFiltriAttivi(); RicaricaSeNonInBatch(); }

    [RelayCommand]
    private void ReimpostaFiltri()
    {
        _refreshInCorso++;
        try
        {
            FiltroTesto = "";
            FiltroStato = null;
            FiltroStatoDettaglio = null;
            FiltroDestinatario = null;
            FiltroTicket = "";
            FiltroTicketEsta = FiltroPresenzaTicketEsta.Tutti;
            FiltroStatoCr = null;
            FiltroGamma.DeselezionaTutto();
            FiltroVeicolo.DeselezionaTutto();
            FiltroEcu.DeselezionaTutto();
            FiltroPriorita.DeselezionaTutto();
            FiltroGravita.DeselezionaTutto();
            FiltroStatoAnomalia.DeselezionaTutto();
            FiltroAssegnatario.DeselezionaTutto();
            FiltroFunzionalita.DeselezionaTutto();
            FiltroCreatoreCr.DeselezionaTutto();
            FiltroDataAperturaDa = null;
            FiltroDataAperturaA = null;
            FiltroDataChiusuraDa = null;
            FiltroDataChiusuraA = null;
            FiltroDurataMinimaGiorni = "";
            FiltroUnitaDurata = UnitaDurataNc.Giorni;
            AzzeraTutteLeDataNonValide();
            VistaSelezionata = null;
        }
        finally { _refreshInCorso--; _debounceTesto?.Cancel(); }
        PaginaRighe = 1;
        PaginaCr = 1;
        Ricarica();
    }

    private void Ricarica()
    {
        // Vedi RipopolaFiltri(): stesso accorpamento, qui per le Load() eventualmente innescate dai filtri
        // Gamma/Veicolo attivi e da RicaricaChangeRequest() più sotto. Se RipopolaFiltri() ha già aperto uno
        // scope (chiamata dal costruttore/Aggiorna()), questo si annida sullo stesso id senza costo aggiuntivo;
        // se Ricarica() gira da sola (filtro cambiato, ReimpostaFiltri, dopo un salvataggio), apre comunque la
        // propria lettura unica invece di lasciarne aperta una per ciascuna Load() sottostante.
        using var letturaMaster = MasterLetturaScope.Apri();
        _osservatoreRicarica?.Invoke();
        NotificaFiltriAttivi();
        if (!_state.CartellaValida)
        {
            Righe.ReplaceWith(Array.Empty<NcRiga>());
            _generazioneSegnalazioni.Nuova(); // un calcolo ancora in volo non deve riapplicare dati di un'altra cartella
            ApplicaSegnalazioni(Array.Empty<DatiSegnalazione>());
            RicaricaChangeRequest();
            return;
        }

        // Il gamma dei veicoli e' letto una sola volta per giro e riusato da Anomalie e Change Request (la lettura
        // dei master resta su questo thread, sotto lo scope; il filtro e l'ordinamento girano in background).
        var gammaDiVeicolo = FiltroGamma.Selezionati.Count > 0 ? LeggiGammaDiVeicolo() : null;
        AvviaAggiornamentoSegnalazioni();
        AvviaAggiornamentoRighe(gammaDiVeicolo);
        RicaricaChangeRequest(gammaDiVeicolo);
    }

    // ------------------------------------------------------------------ elenco anomalie: filtro/ordinamento/pagina

    private CancellationTokenSource? _cts;
    private long _generazione;
    private Task _aggiornamentoCorrente = Task.CompletedTask;
    private Task _aggiornamentoSegnalazioni = Task.CompletedTask;
    private Task _aggiornamentoChangeRequest = Task.CompletedTask;
    private readonly GenerazioneCalcolo _generazioneSegnalazioni = new();
    private readonly GenerazioneCalcolo _generazioneChangeRequest = new();

    /// <summary>Solo test: eseguito all'inizio di ogni calcolo in background (puo' bloccare, per simulare un calcolo lento).</summary>
    internal Action? GateCalcoloPerTest { get; set; }

    private int _calcoliScartati;

    /// <summary>Solo test: risultati di calcoli in background superati da uno piu' recente, scartati senza essere applicati.</summary>
    internal int CalcoliScartati => Volatile.Read(ref _calcoliScartati);

    /// <summary>Solo test: letture del gamma dei veicoli per il filtro Gamma (una per giro di Ricarica).</summary>
    internal int GammaDiVeicoloLetti { get; private set; }

    /// <summary>Solo test: forza (true) o esclude (false) l'esecuzione su thread di lavoro. Di default il lavoro
    /// gira in background quando esiste un'applicazione Avalonia (UI reale), in modo sincrono altrimenti
    /// (costruttore, test unitari senza UI).</summary>
    internal bool? ForzaBackground;

    /// <summary>Completamento dei calcoli avviati (risultati gia' applicati alla UI, o scartati).</summary>
    internal Task AttendiAggiornamentoAsync() =>
        Task.WhenAll(_aggiornamentoCorrente, _aggiornamentoSegnalazioni, _aggiornamentoChangeRequest);

    private bool InBackground => _costruito && (ForzaBackground ?? Avalonia.Application.Current != null);

    private sealed record RisultatoRighe(AnomalieQueryRisultato Query, List<NcRiga> Righe, IReadOnlyList<string> Assegnatari);

    /// <summary>Contatore di generazione dei calcoli in background: un risultato superato da uno piu' recente non viene applicato.</summary>
    private sealed class GenerazioneCalcolo
    {
        private long _valore;
        public long Nuova() => Interlocked.Increment(ref _valore);
        public bool Attuale(long generazione) => Interlocked.Read(ref _valore) == generazione;
    }

    /// <summary>Segnalazione da classificare con i testi gia' risolti, calcolati fuori dal thread UI: il thread UI crea le
    /// righe solo per la pagina visibile.</summary>
    private sealed record DatiSegnalazione(SegnalazioneDaClassificare Segnalazione, string Reiterazione, string Risultato,
        string TitoloAnomalia, string Contesto, SeveritaAnomalia? Severita);

    /// <summary>Condizione di un Test trovata per UID: la prima esecuzione che la contiene, la condizione e il suo numero
    /// (1-based) in quella esecuzione, come nei testi della lista.</summary>
    private sealed record CondizioneTrovata(Esecuzione Esecuzione, Condizione Condizione, int NumeroCondizione);

    /// <summary>Filtri della Change Request catturati sul thread UI: il calcolo in background non legge lo stato della vista.</summary>
    internal sealed record FiltroChangeRequest(StatoChangeRequest? Stato, string? Testo, string? Ticket,
        IReadOnlyCollection<string>? Veicoli, IReadOnlyCollection<string>? Componente, IReadOnlyCollection<string>? Gamma,
        bool NessunaCr, IReadOnlyCollection<string>? Assegnatari, IReadOnlyCollection<string>? Funzionalita,
        DateTime? AperturaDa, DateTime? AperturaA, DateTime? ChiusuraDa, DateTime? ChiusuraA, long PaginaRichiesta,
        IReadOnlyCollection<string>? Creatori = null, ChangeRequestOrdinamento? Ordinamento = null);

    /// <summary>Esito del calcolo di una pagina di Change Request: totale filtrato, pagina effettiva e righe della pagina.</summary>
    internal sealed record RisultatoChangeRequest(long Totale, long Pagina, IReadOnlyList<ChangeRequest> Righe);

    /// <summary>Gamma per veicolo, letto dal catalogo dei Veicoli: una sola lettura per giro di Ricarica (Anomalie e Change Request
    /// usano lo stesso lookup). Chiamato sul thread UI sotto lo scope dei master.</summary>
    private IReadOnlyDictionary<string, string> LeggiGammaDiVeicolo()
    {
        GammaDiVeicoloLetti++;
        return _state.Master.Veicoli.Load().ToDictionary(v => v.Nome, v => v.GammaNome, StringComparer.OrdinalIgnoreCase);
    }

    private AnomalieQueryRichiesta CostruisciRichiesta(IReadOnlyDictionary<string, string>? gammaDiVeicolo)
    {
        static HashSet<string>? Insieme(List<string> valori) =>
            valori.Count > 0 ? new HashSet<string>(valori, StringComparer.OrdinalIgnoreCase) : null;

        var nc = new AnomalieFiltroNc
        {
            Stato = FiltroStato,
            StatoDettaglio = FiltroStatoDettaglio,
            Destinatario = FiltroDestinatario,
            StatiWorkflow = FiltroStatoAnomalia.Selezionati.Select(Enum.Parse<StatoAnomalia>).ToList(),
            Priorita = FiltroPriorita.Selezionati.Select(Enum.Parse<PrioritaOperativa>).ToList(),
            Gravita = FiltroGravita.Selezionati.Select(Enum.Parse<SeveritaAnomalia>).ToList(),
            Assegnatari = FiltroAssegnatario.Selezionati,
            AperturaDa = DataAperturaDa, AperturaA = DataAperturaA,
            ChiusuraDa = DataChiusuraDa, ChiusuraA = DataChiusuraA,
            DurataMinima = DurataMinimaGiorni, UnitaDurata = FiltroUnitaDurata,
            HaTicketEsta = FiltroTicketEsta switch
            {
                FiltroPresenzaTicketEsta.ConTicket => true,
                FiltroPresenzaTicketEsta.SenzaTicket => false,
                _ => null
            },
            Testo = string.IsNullOrWhiteSpace(FiltroTesto) ? null : FiltroTesto.Trim()
        };
        var gamma = Insieme(FiltroGamma.Selezionati);
        var ambito = new AnomalieFiltroAmbito
        {
            Veicoli = Insieme(FiltroVeicolo.Selezionati),
            Componente = Insieme(FiltroEcu.Selezionati),
            Gamma = gamma,
            Funzionalita = Insieme(FiltroFunzionalita.Selezionati),
            Ticket = string.IsNullOrWhiteSpace(FiltroTicket) ? null : FiltroTicket.Trim(),
            // Lookup gamma letto una volta per giro (vedi LeggiGammaDiVeicolo): mai rilettura del catalogo per richiesta.
            GammaDiVeicolo = gamma != null ? gammaDiVeicolo ?? LeggiGammaDiVeicolo() : null
        };
        return new AnomalieQueryRichiesta(nc, ambito, Ordinamento, Math.Max(1L, PaginaRighe), RighePageSize);
    }

    private RisultatoRighe EseguiRighe(AnomalieQueryRichiesta richiesta, CancellationToken ct)
    {
        var query = AnomalieQueryService.Esegui(_state.NonConformita, id => _state.TestCases.GetById(id), richiesta, ct: ct, trovaUid: _state.TestCases.GetByUid);
        var righe = query.Righe.Select(r => new NcRiga
        {
            Nc = r.Nc,
            TestCase = r.TestCase,
            TestCaseIdVisualizzato = r.TestCaseIdVisualizzato,
            TestCaseEliminato = r.TestCase?.Eliminato ?? true
        }).ToList();
        ct.ThrowIfCancellationRequested();
        return new RisultatoRighe(query, righe, LeggiAssegnatariDaDati());
    }

    private void ApplicaRighe(RisultatoRighe risultato)
    {
        TotaleRighe = risultato.Query.Totale;
        PaginaRighe = risultato.Query.PaginaEffettiva;
        Righe.ReplaceWith(risultato.Righe);
        PopolaAssegnatari(risultato.Assegnatari);
        NotificaPaginazioneRighe();
    }

    /// <summary>Avvia il ricalcolo dell'elenco. In background un nuovo avvio annulla il precedente e il
    /// risultato di un avvio superato non viene mai applicato (numero di generazione).</summary>
    private void AvviaAggiornamentoRighe(IReadOnlyDictionary<string, string>? gammaDiVeicolo = null)
    {
        var generazione = Interlocked.Increment(ref _generazione);
        _cts?.Cancel();

        if (!_state.CartellaValida)
        {
            Righe.ReplaceWith(Array.Empty<NcRiga>());
            TotaleRighe = 0;
            NotificaPaginazioneRighe();
            InCaricamento = false;
            return;
        }

        var richiesta = CostruisciRichiesta(gammaDiVeicolo);
        if (!InBackground)
        {
            ApplicaRighe(EseguiRighe(richiesta, CancellationToken.None));
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        InCaricamento = true;
        _aggiornamentoCorrente = Task.Run(async () =>
        {
            RisultatoRighe? risultato = null;
            Exception? errore = null;
            try { risultato = EseguiRighe(richiesta, cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { errore = ex; }

            await SuThreadUi(() =>
            {
                if (generazione != Interlocked.Read(ref _generazione)) return;
                if (errore != null)
                {
                    CrashLogService.Registra("Anomalie: aggiornamento elenco", errore);
                    MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("AnomalieAggiornamentoNonRiuscitoFmt"), errore.Message);
                }
                else ApplicaRighe(risultato!);
                InCaricamento = false;
            });
        });
    }

    private static Task SuThreadUi(Action azione)
    {
        if (Avalonia.Application.Current == null) { azione(); return Task.CompletedTask; }
        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(azione).GetTask();
    }


    /// <summary>Change Request = TestCase il cui template è marcato IsChangeRequest (feedback utente
    /// 2026-08-21, punto 11): stessa fonte dati dei Test veri, filtrata diversamente, non una repository a
    /// sé. Riusa FiltroTesto/FiltroTicket già presenti in questa pagina — un ticket TICKET condiviso tra
    /// un'Anomalia e una Change Request è un aggancio "a colpo d'occhio" cercando lo stesso codice in entrambe
    /// le liste (match implicito, nessun collegamento gestito esplicitamente per scelta).</summary>
    private void RicaricaChangeRequest(IReadOnlyDictionary<string, string>? gammaDiVeicolo = null)
    {
        var generazione = _generazioneChangeRequest.Nuova();
        if (!_state.CartellaValida) { ChangeRequests.ReplaceWith(Array.Empty<ChangeRequest>()); TotaleCr = 0; NotificaPaginazioneCr(); return; }

        // Filtri letti qui, sul thread UI: il calcolo (lettura delle CR, filtro, ordinamento, pagina) non legge piu' lo stato
        // della vista e gira fuori dal thread UI. Il gamma e' quello del giro (una sola lettura dei Veicoli).
        var gamma = gammaDiVeicolo ?? (FiltroGamma.Selezionati.Count > 0 ? LeggiGammaDiVeicolo() : null);
        var filtro = CatturaFiltroChangeRequest();
        // Solo lettura: le righe della pagina servono a mostrare l'elenco e ad aprire la CR per Id (l'editor rilegge con GetById),
        // quindi non serve la copia di GetAll per ogni CR.
        if (!InBackground)
        {
            ApplicaChangeRequest(CalcolaPaginaChangeRequest(_state.ChangeRequests.PeekAllReadOnly(), filtro, gamma));
            return;
        }
        _aggiornamentoChangeRequest = AvviaCalcoloInBackground(generazione, _generazioneChangeRequest,
            () => CalcolaPaginaChangeRequest(_state.ChangeRequests.PeekAllReadOnly(), filtro, gamma),
            ApplicaChangeRequest, "Anomalie: change request");
    }

    /// <summary>Filtri della Change Request letti sul thread UI, con le stesse regole di prima (testo e ticket ripuliti, filtro
    /// presente solo se attivo).</summary>
    private FiltroChangeRequest CatturaFiltroChangeRequest() => new(
        Stato: FiltroStatoCr,
        Testo: string.IsNullOrWhiteSpace(FiltroTesto) ? null : FiltroTesto.Trim(),
        Ticket: string.IsNullOrWhiteSpace(FiltroTicket) ? null : FiltroTicket.Trim(),
        Veicoli: FiltroVeicolo.Selezionati.Count > 0 ? FiltroVeicolo.Selezionati.ToList() : null,
        Componente: FiltroEcu.Selezionati.Count > 0 ? FiltroEcu.Selezionati.ToList() : null,
        Gamma: FiltroGamma.Selezionati.Count > 0 ? FiltroGamma.Selezionati.ToList() : null,
        NessunaCr: FiltroPriorita.HaSelezione || FiltroGravita.HaSelezione,
        Assegnatari: FiltroAssegnatario.HaSelezione ? FiltroAssegnatario.Selezionati.ToList() : null,
        Funzionalita: FiltroFunzionalita.HaSelezione ? FiltroFunzionalita.Selezionati.ToList() : null,
        AperturaDa: DataAperturaDa?.Date, AperturaA: DataAperturaA?.Date,
        ChiusuraDa: DataChiusuraDa?.Date, ChiusuraA: DataChiusuraA?.Date,
        PaginaRichiesta: PaginaCr,
        Creatori: FiltroCreatoreCr.HaSelezione ? FiltroCreatoreCr.Selezionati.ToList() : null,
        Ordinamento: new ChangeRequestOrdinamento(OrdinamentoCrChiave, OrdinamentoCrDecrescente));

    /// <summary>Filtro, ordinamento per data di creazione (decrescente) e pagina della Change Request. Stessa regola di prima:
    /// funzione pura su dati gia' letti, quindi eseguibile fuori dal thread UI.</summary>
    internal static RisultatoChangeRequest CalcolaPaginaChangeRequest(IReadOnlyList<ChangeRequest> tutte, FiltroChangeRequest f,
        IReadOnlyDictionary<string, string>? gammaDiVeicolo, CancellationToken ct = default)
    {
        var ordinate = CalcolaChangeRequestsFiltrate(tutte, f, gammaDiVeicolo, ct);
        var totale = ordinate.Count;
        var pagine = Math.Max(1L, checked((totale + RighePageSize - 1L) / RighePageSize));
        var pagina = Math.Min(Math.Max(1L, f.PaginaRichiesta), pagine);
        var offset = checked((int)((pagina - 1L) * RighePageSize));
        return new RisultatoChangeRequest(totale, pagina, ordinate.Skip(offset).Take(RighePageSize).ToList());
    }

    /// <summary>Applica filtri e ordinamento una sola volta. Il risultato ordinato può essere riutilizzato per
    /// tutte le pagine (in particolare durante l'export), evitando una scansione completa per ogni pagina.</summary>
    internal static IReadOnlyList<ChangeRequest> CalcolaChangeRequestsFiltrate(IReadOnlyList<ChangeRequest> tutte, FiltroChangeRequest f,
        IReadOnlyDictionary<string, string>? gammaDiVeicolo, CancellationToken ct = default)
    {
        IEnumerable<ChangeRequest> query = tutte.Where(_ => { ct.ThrowIfCancellationRequested(); return true; });
        if (f.Stato.HasValue) query = query.Where(cr => cr.Stato == f.Stato.Value);
        if (f.Testo != null)
        {
            var q = f.Testo;
            query = query.Where(tc => tc.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                || tc.Titolo.Contains(q, StringComparison.OrdinalIgnoreCase)
                || tc.Descrizione.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        if (f.Ticket != null)
        {
            var q = f.Ticket;
            query = query.Where(tc => tc.TicketCollegati.Any(t => t.Riferimento.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }
        // Veicolo/componente/Gamma vivono direttamente sulla CR (VeicoliImpattati/EcuImpattate), senza bisogno di
        // risolvere un TestCase collegato come per le Anomalie.
        if (f.Veicoli != null)
        {
            var hsVeicolo = new HashSet<string>(f.Veicoli, StringComparer.OrdinalIgnoreCase);
            query = query.Where(cr => cr.VeicoliImpattati.Any(hsVeicolo.Contains));
        }
        if (f.Componente != null)
        {
            var hsEcu = new HashSet<string>(f.Componente, StringComparer.OrdinalIgnoreCase);
            query = query.Where(cr => cr.EcuImpattate.Any(hsEcu.Contains));
        }
        if (f.Gamma != null)
        {
            var hsGamma = new HashSet<string>(f.Gamma, StringComparer.OrdinalIgnoreCase);
            var mappa = gammaDiVeicolo ?? throw new InvalidOperationException("Filtro gamma senza lookup dei veicoli.");
            query = query.Where(cr => cr.VeicoliImpattati.Any(v => mappa.TryGetValue(v, out var g) && g != null && hsGamma.Contains(g)));
        }
        // Filtri avanzati applicabili alle CR (AND con tutto il resto). Priorita'/Gravita' non esistono sulle CR:
        // con uno di questi filtri attivo nessuna CR puo' soddisfarlo. Stato anomalia/Stato NC non si applicano
        // (le CR hanno il proprio filtro Stato CR).
        if (f.NessunaCr) query = query.Where(_ => false);
        if (f.Assegnatari != null)
        {
            var assegnatari = new HashSet<string>(f.Assegnatari, StringComparer.OrdinalIgnoreCase);
            query = query.Where(cr => assegnatari.Contains(cr.Responsabile ?? ""));
        }
        if (f.Creatori != null)
        {
            var creatori = new HashSet<string>(f.Creatori, StringComparer.OrdinalIgnoreCase);
            query = query.Where(cr => creatori.Contains(cr.CreatoDa ?? ""));
        }
        if (f.Funzionalita != null)
        {
            var funz = new HashSet<string>(f.Funzionalita, StringComparer.OrdinalIgnoreCase);
            query = query.Where(cr => cr.FunzionalitaImpattate.Any(funz.Contains));
        }
        if (f.AperturaDa != null) query = query.Where(cr => cr.DataCreazione.Date >= f.AperturaDa);
        if (f.AperturaA != null) query = query.Where(cr => cr.DataCreazione.Date <= f.AperturaA);
        if (f.ChiusuraDa != null || f.ChiusuraA != null)
        {
            var chiusuraDa = f.ChiusuraDa;
            var chiusuraA = f.ChiusuraA;
            query = query.Where(cr => cr.ChiusaIl.HasValue
                && (chiusuraDa == null || cr.ChiusaIl.Value.Date >= chiusuraDa)
                && (chiusuraA == null || cr.ChiusaIl.Value.Date <= chiusuraA));
        }
        ct.ThrowIfCancellationRequested();
        var ordinate = query.ToList();
        ordinate.Sort(ChangeRequestOrdinamentoComparer.Crea(f.Ordinamento ?? ChangeRequestOrdinamento.Predefinito));
        ct.ThrowIfCancellationRequested();
        return ordinate;
    }

    private void ApplicaChangeRequest(RisultatoChangeRequest risultato)
    {
        TotaleCr = risultato.Totale;
        PaginaCr = risultato.Pagina;
        ChangeRequests.ReplaceWith(risultato.Righe);
        NotificaPaginazioneCr();
    }

    /// <summary>Esegue <paramref name="calcolo"/> fuori dal thread UI e applica il risultato sul thread UI solo se e' ancora
    /// l'ultimo avviato: un risultato superato viene scartato. Un errore viene registrato e segnalato, senza applicare nulla.</summary>
    private Task AvviaCalcoloInBackground<T>(long generazione, GenerazioneCalcolo generazioni, Func<T> calcolo, Action<T> applica, string contesto) =>
        Task.Run(async () =>
        {
            T? risultato = default;
            Exception? errore = null;
            GateCalcoloPerTest?.Invoke();
            try { risultato = calcolo(); }
            catch (Exception ex) { errore = ex; }
            await SuThreadUi(() =>
            {
                if (!generazioni.Attuale(generazione)) { Interlocked.Increment(ref _calcoliScartati); return; }
                if (errore != null)
                {
                    CrashLogService.Registra(contesto, errore);
                    MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("AnomalieAggiornamentoNonRiuscitoFmt"), errore.Message);
                    return;
                }
                applica(risultato!);
            });
        });

    // ------------------------------------------------------------------ segnalazioni da classificare: calcolo e pagina

    private IReadOnlyList<DatiSegnalazione> _datiSegnalazioni = Array.Empty<DatiSegnalazione>();
    private readonly Dictionary<Guid, SegnalazioneRiga> _righeSegnalazioni = new();

    /// <summary>Ricalcola l'elenco delle segnalazioni da classificare: in background (con generazione) in UI reale, in linea altrimenti.</summary>
    private void AvviaAggiornamentoSegnalazioni()
    {
        var generazione = _generazioneSegnalazioni.Nuova();
        if (!InBackground)
        {
            ApplicaSegnalazioni(CalcolaSegnalazioni());
            return;
        }
        _aggiornamentoSegnalazioni = AvviaCalcoloInBackground(generazione, _generazioneSegnalazioni,
            () => CalcolaSegnalazioni(), ApplicaSegnalazioni, "Anomalie: segnalazioni da classificare");
    }

    /// <summary>Elenco completo delle segnalazioni da classificare, con i testi gia' risolti. Ogni Test viene indicizzato per UID
    /// delle condizioni una sola volta, invece di cercare esecuzione e condizione per ogni segnalazione.</summary>
    private IReadOnlyList<DatiSegnalazione> CalcolaSegnalazioni()
    {
        var segnalazioni = _state.Segnalazioni.GetAll().Where(s => s.Stato == StatoSegnalazione.DaClassificare).ToList();
        var indiciPerTest = new Dictionary<string, Dictionary<Guid, CondizioneTrovata>>(StringComparer.Ordinal);
        var risultato = new List<DatiSegnalazione>(segnalazioni.Count);
        foreach (var segnalazione in segnalazioni)
        {
            var test = _state.TestCases.GetById(segnalazione.TestId);
            CondizioneTrovata? trovata = null;
            if (test != null)
            {
                if (!indiciPerTest.TryGetValue(test.Id, out var indice)) indiciPerTest[test.Id] = indice = IndicizzaCondizioni(test);
                indice.TryGetValue(segnalazione.CondizioneUid, out trovata);
            }
            var condizione = trovata?.Condizione;
            var numero = trovata?.NumeroCondizione ?? 0;
            risultato.Add(new DatiSegnalazione(
                segnalazione,
                Reiterazione: condizione == null ? LocalizationService.Instance.Traduci("ReiterazioneNonDisponibile")
                    : string.IsNullOrEmpty(condizione.Funzionalita) ? string.Format(LocalizationService.Instance.Traduci("ReiterazioneNumeroFmt"), numero) : condizione.Funzionalita,
                Risultato: condizione?.Nota ?? "",
                TitoloAnomalia: condizione == null ? "" : NonConformitaRulesService.TitoloDaOperazione(condizione.Funzionalita, condizione.Componente, condizione.DescrizioneProblema,
                    string.Format(LocalizationService.Instance.Traduci("ReiterazioneNumeroFmt"), numero)),
                Contesto: test == null || trovata == null ? "" : $"{_state.NomeTestTemplate(test.TestTemplateUid)} · {test.Componente} · matricola {trovata.Esecuzione.Matricola}",
                // Eredita la Severità già impostata sul test (feedback utente 2026-08-20, punto 18): resta
                // comunque modificabile prima di classificare, non è un vincolo.
                Severita: test?.Severita));
        }
        return risultato;
    }

    /// <summary>Prima occorrenza di ogni condizione del Test per UID: prima esecuzione che la contiene e prima condizione con
    /// quell'UID (stessa scelta di FirstOrDefault usata per le singole segnalazioni).</summary>
    private static Dictionary<Guid, CondizioneTrovata> IndicizzaCondizioni(TestCase test)
    {
        var indice = new Dictionary<Guid, CondizioneTrovata>();
        foreach (var esecuzione in test.Esecuzioni)
            foreach (var condizione in esecuzione.Condizioni)
                indice.TryAdd(condizione.Uid, new CondizioneTrovata(esecuzione, condizione, esecuzione.Condizioni.IndexOf(condizione) + 1));
        return indice;
    }

    /// <summary>Applica sul thread UI l'elenco completo: la pagina corrente (con il limite della nuova lunghezza) diventa <see cref="Segnalazioni"/>.</summary>
    private void ApplicaSegnalazioni(IReadOnlyList<DatiSegnalazione> dati)
    {
        _datiSegnalazioni = dati;
        _righeSegnalazioni.Clear();
        TotaleSegnalazioni = dati.Count;
        PaginaSegnalazioni = Math.Min(Math.Max(1L, PaginaSegnalazioni), PagineTotaliSegnalazioni);
        AggiornaPaginaSegnalazioni();
        OnPropertyChanged(nameof(HaSegnalazioni));
    }

    /// <summary>Crea le righe della sola pagina corrente. Una riga gia' creata resta lo stesso oggetto finche' l'elenco non viene
    /// ricalcolato, cosi' una modifica non salvata su una pagina sopravvive al cambio pagina.</summary>
    private void AggiornaPaginaSegnalazioni()
    {
        var offset = checked((int)((PaginaSegnalazioni - 1L) * SegnalazioniPerPagina));
        Segnalazioni.ReplaceWith(_datiSegnalazioni.Skip(offset).Take(SegnalazioniPerPagina).Select(MaterializzaRiga).ToList());
        NotificaPaginazioneSegnalazioni();
    }

    private SegnalazioneRiga MaterializzaRiga(DatiSegnalazione dati)
    {
        var uid = dati.Segnalazione.Uid;
        if (_righeSegnalazioni.TryGetValue(uid, out var riga)) return riga;
        riga = new SegnalazioneRiga
        {
            Segnalazione = dati.Segnalazione,
            Reiterazione = dati.Reiterazione,
            Risultato = dati.Risultato,
            TitoloAnomalia = dati.TitoloAnomalia,
            Contesto = dati.Contesto,
            Severita = dati.Severita,
        };
        _righeSegnalazioni[uid] = riga;
        return riga;
    }

    /// <summary>Dialogo di dettaglio della riga (sostituisce l'editor inline espandibile): lavora su una copia
    /// riletta dall'archivio e, a chiusura con modifiche, la pagina si riallinea con <see cref="RicaricaDopoDettaglio"/>.</summary>
    public AnomaliaDettaglioViewModel CreaDettaglio(NcRiga riga, bool solaLettura = false,
        bool abilitaModificaDaDettaglio = false)
    {
        var nc = riga.Nc ?? throw new InvalidOperationException("La riga NC non contiene un'anomalia caricata.");
        var ncId = nc.Id;
        // La pagina Anomalie può restare aperta mentre un altro PC aggiorna la NC. Ricarichiamo solo il suo JSON
        // prima di creare la copia editabile, evitando che un indice/cache locale obsoleto causi un falso conflitto.
        // Il controllo di revisione del repository resta l'ultima protezione se la modifica avviene dopo l'apertura.
        if (!WorkflowWorkingCopyScope.Attivo)
        {
            _state.NonConformita.RefreshPaths(new[] { _state.NonConformita.FilePathOf(nc) });
            nc = _state.NonConformita.GetById(ncId)
                ?? throw new InvalidOperationException($"L'anomalia {ncId} non è più disponibile.");
        }
        return new AnomaliaDettaglioViewModel(_state, nc,
            forzaSolaLettura: solaLettura && !abilitaModificaDaDettaglio,
            apriTest: id => _apriTestCase(id),
            inizialmenteSolaLettura: abilitaModificaDaDettaglio);
    }

    /// <summary>Snapshot della CR persistita, usato dalla finestra Dettagli che non espone campi modificabili.</summary>
    public ChangeRequest? LeggiDettaglioChangeRequest(ChangeRequest riga) =>
        _state.ChangeRequests.GetById(riga.Id);

    /// <summary>Preflight leggero delle transizioni CR, allineato alle proprietà PuoImplementare/PuoChiudere/PuoRiaprire
    /// dell'editor completo. Evita di costruire un editor e avviare i suoi caricamenti se non esiste alcuna azione.</summary>
    public bool PuoCambiareStatoChangeRequest(ChangeRequest? cr)
    {
        if (!_state.PuoScrivere || cr == null || string.IsNullOrWhiteSpace(cr.Id)) return false;
        // La lista può contenere una fotografia precedente o una CR archiviata: il preflight si basa sempre
        // sul documento attuale e non apre il workflow per record fuori dal ciclo operativo.
        var corrente = _state.ChangeRequests.GetById(cr.Id);
        if (corrente == null || corrente.Archiviata) return false;
        var puoModificare = corrente.Stato is not (StatoChangeRequest.Chiusa or StatoChangeRequest.Annullata);
        var puoImplementare = puoModificare &&
            !string.IsNullOrWhiteSpace(corrente.Responsabile) &&
            (_state.EMaster || corrente.Responsabile.Equals(_state.Config.OperatoreAttivo, StringComparison.OrdinalIgnoreCase));
        var puoChiudere = ChangeRequestChiusuraService.PuoChiudere(corrente, _state.PuoScrivere, _state.EMaster);
        var puoRiaprire = ChangeRequestChiusuraService.PuoRiaprire(corrente, _state.PuoScrivere, _state.EMaster);
        return puoImplementare || puoChiudere || puoRiaprire;
    }

    /// <summary>Preflight per aprire un editor CR dalla lista. La riga può essere una fotografia ormai stantia:
    /// permesso, stato e archiviazione vengono quindi verificati sul record persistito.</summary>
    public bool PuoModificareChangeRequest(ChangeRequest? riga)
    {
        if (!_state.PuoScrivere || riga == null || string.IsNullOrWhiteSpace(riga.Id)) return false;
        var corrente = _state.ChangeRequests.GetById(riga.Id);
        return corrente?.PuoModificaRapida == true || PuoCambiareStatoChangeRequest(corrente);
    }

    /// <summary>Preflight della modifica rapida CR, mantenuto come nome esplicito per il code-behind della quick action.</summary>
    public bool PuoModificareRapidamenteChangeRequest(ChangeRequest? riga)
    {
        if (!_state.PuoScrivere || riga == null || string.IsNullOrWhiteSpace(riga.Id)) return false;
        return _state.ChangeRequests.GetById(riga.Id)?.PuoModificaRapida == true;
    }

    /// <summary>Editor di workflow temporaneo: il dialogo rapido richiama i comandi di stato e i controlli permesso
    /// già usati dalla pagina CR completa, senza duplicare le transizioni persistenti.</summary>
    public ChangeRequestEditorViewModel? CreaEditorStatoChangeRequest(ChangeRequest riga)
    {
        var corrente = _state.ChangeRequests.GetById(riga.Id);
        // Anche una CR chiusa, annullata o archiviata si apre (in sola lettura, con le azioni che l'utente puo' ancora fare).
        if (corrente == null) return null;
        return new ChangeRequestEditorViewModel(_state, riga.Id, () => { }, _ => { });
    }

    /// <summary>Salva titolo e descrizione dalla modifica rapida con lo stesso optimistic lock e lo stesso registro
    /// revisioni della CR completa. Un errore conserva una bozza locale e non tocca la fotografia visualizzata.</summary>
    public bool SalvaModificaRapidaChangeRequest(ChangeRequest snapshotApertura, string titolo, string descrizione, out string? errore) =>
        SalvaModificaRapidaChangeRequest(snapshotApertura, titolo, descrizione, snapshotApertura.MotivazioneDuplicato, out errore);

    public bool SalvaModificaRapidaChangeRequest(ChangeRequest snapshotApertura, string titolo, string descrizione,
        string? motivazioneDuplicato, out string? errore)
    {
        errore = null;
        if (!_state.PuoScrivere)
        {
            errore = LocalizationService.Instance.Traduci("ArchivioAzioneNonAutorizzata");
            return false;
        }
        var corrente = _state.ChangeRequests.GetById(snapshotApertura.Id);
        if (corrente == null)
        {
            errore = LocalizationService.Instance.Scegli(
                "La Change Request non è più disponibile.",
                "The Change Request is no longer available.",
                "La demande de changement n’est plus disponible.");
            return false;
        }
        if (corrente.Stato is StatoChangeRequest.Chiusa or StatoChangeRequest.Annullata || corrente.Archiviata)
        {
            errore = LocalizationService.Instance.Traduci("ArchivioAzioneNonAutorizzata");
            return false;
        }
        if (corrente.RevisioneDati != snapshotApertura.RevisioneDati)
        {
            errore = LocalizationService.Instance.Scegli(
                "La Change Request è stata modificata da un altro utente. Riapri la modifica rapida per caricare i dati aggiornati.",
                "This Change Request was modified by another user. Reopen quick edit to load the latest data.",
                "Cette demande de changement a été modifiée par un autre utilisateur. Rouvrez la modification rapide pour charger les données à jour.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(titolo))
        {
            errore = LocalizationService.Instance.Traduci("CrEdValTitoloObbl");
            return false;
        }

        var baseSnapshot = EntityStateFingerprint.Clone(snapshotApertura);
        var candidato = EntityStateFingerprint.Clone(snapshotApertura);
        candidato.Titolo = titolo.Trim();
        candidato.Descrizione = descrizione?.Trim() ?? "";
        candidato.MotivazioneDuplicato = motivazioneDuplicato?.Trim() ?? "";
        // Non creare una revisione quando il testo normalizzato coincide con la fotografia corrente.
        // Confrontare il record intero evita di affidarsi a un flag UI e copre campi importati, ticket e link.
        if (ChangeRequestWorkflowService.StessoContenutoPersistibile(candidato, corrente)) return true;
        var duplicati = ChangeRequestDuplicatiService.TrovaPossibiliDuplicati(_state.ChangeRequests.PeekAllReadOnly(), candidato);
        if (duplicati.Count > 0 && string.IsNullOrWhiteSpace(candidato.MotivazioneDuplicato))
        {
            errore = string.Format(LocalizationService.Instance.Traduci("CrEdValDuplicatoFmt"), string.Join(", ", duplicati));
            return false;
        }
        candidato.UltimaModifica = DateTime.Now;
        candidato.Revisioni.Add(ChangeRequestWorkflowService.CreaRevisione(candidato, _state.Config.OperatoreAttivo));
        try
        {
            _state.ChangeRequests.Save(candidato, snapshotApertura.RevisioneDati);
        }
        catch (Exception ex)
        {
            try
            {
                FailedSaveDraftService.Save(_state.Paths.Root, "change-request", candidato.Uid,
                    snapshotApertura.RevisioneDati, _state.Config.OperatoreAttivo, candidato, ex.Message,
                    baseSnapshot: baseSnapshot);
            }
            catch (Exception draftEx)
            {
                CrashLogService.Registra("Bozza locale CR dopo modifica rapida non riuscita", draftEx);
            }
            errore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoFmt"), ex.Message);
            return false;
        }

        return true;
    }

    /// <summary>Riallinea le righe dopo un dialogo di dettaglio che ha scritto (non rilegge i master/filtri).</summary>
    public void RicaricaDopoDettaglio()
    {
        MessaggioErrore = null;
        Ricarica();
    }

    [RelayCommand]
    private void Seleziona(NcRiga? riga) => Selezionata = riga?.Nc;

    [RelayCommand]
    private void ApriTestCaseCollegato(NcRiga? riga)
    {
        if (riga != null && !riga.TestCaseEliminato) _apriTestCase(riga.TestCaseIdVisualizzato);
    }

    [RelayCommand]
    private void CreaChangeRequest() => _apriChangeRequest(null);

    /// <summary>Cosa la pagina Anomalie/CR puo' esportare e quali filtri si possono modificare dal dialogo (quelli a video).</summary>
    internal ExportDescrittore CreaDescrittoreExport()
    {
        var loc = LocalizationService.Instance;
        var sezioni = new List<ExportSezione>
        {
            new(AnomalieSezioniExport.VistaNc, loc.Traduci("ExpSezAnomalieVistaNc"), AnomalieCrExportVisteService.ColonneEsportabiliVistaNc()),
            new(AnomalieSezioniExport.VistaCr, loc.Traduci("ExpSezAnomalieVistaCr"), AnomalieCrExportVisteService.ColonneEsportabiliVistaCr()),
            new(AnomalieSezioniExport.DatiNc, loc.Traduci("ExpSezAnomalieDatiNc"),
                NonConformitaExportService.Intestazioni.Select(h => new ExportColonna(h, h)).ToList()),
            new(AnomalieSezioniExport.DatiCr, loc.Traduci("ExpSezAnomalieDatiCr"),
                ChangeRequestExportService.IntestazioniBase.Select(h => new ExportColonna(h, h)).ToList()),
            new(AnomalieSezioniExport.Info, loc.Traduci("ExpSezReportInfo")),
            new("pivot", "Pivot", PredefinitaAttiva: false),
        };
        ExportFiltroData Data(string id, string it, string en, string fr, Func<DateTimeOffset?> leggi, Action<DateTimeOffset?> imposta) =>
            new(id, loc.Scegli(it, en, fr), () => leggi()?.Date, d => imposta(d.HasValue ? new DateTimeOffset(d.Value) : null));
        ExportFiltroMultiplo Multi(string id, string it, string en, string fr, FiltroMultiSelect f) => new(id, loc.Scegli(it, en, fr), f);
        var statiCr = Enum.GetValues<StatoChangeRequest>()
            .ToDictionary(s => (string)WorkflowLabelConverter.Instance.Convert(s, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture)!, s => s);
        var statiNc = Enum.GetValues<StatoNonConformita>().ToDictionary(s => s.Label(), s => s);
        var filtri = new List<ExportFiltro>
        {
            new ExportFiltroTesto("testo", loc.Scegli("Testo", "Text", "Texte"), () => FiltroTesto, v => FiltroTesto = v ?? ""),
            new ExportFiltroTesto("ticket", loc.Scegli("Ticket", "Ticket", "Ticket"), () => FiltroTicket, v => FiltroTicket = v ?? ""),
            new ExportFiltroTesto("durataminima", loc.Scegli("Durata maggiore di", "Time to close greater than", "Durée avant clôture supérieure à"), () => FiltroDurataMinimaGiorni, v => FiltroDurataMinimaGiorni = v ?? ""),
            new ExportFiltroSingolo("unitadurata", loc.Scegli("Unità durata", "Duration unit", "Unité de durée"), Enum.GetNames<UnitaDurataNc>(), () => FiltroUnitaDurata.ToString(), v => FiltroUnitaDurata = Enum.TryParse<UnitaDurataNc>(v, out var u) && Enum.IsDefined(u) ? u : UnitaDurataNc.Giorni,
                v => Enum.TryParse<UnitaDurataNc>(v, out var u) && Enum.IsDefined(u) ? UnitaDurataEtichette.Etichetta(u) : v),
            new ExportFiltroSingolo("statonc", loc.Scegli("Stato NC", "NC status", "Statut NC"), statiNc.Keys.ToList(),
                () => FiltroStato.HasValue ? statiNc.FirstOrDefault(kv => kv.Value == FiltroStato.Value).Key : null,
                v => FiltroStato = v != null && statiNc.TryGetValue(v, out var s) ? s : null),
            new ExportFiltroSingolo("statocr", loc.Scegli("Stato CR", "CR status", "Statut CR"), statiCr.Keys.ToList(),
                () => FiltroStatoCr.HasValue ? statiCr.FirstOrDefault(kv => kv.Value == FiltroStatoCr.Value).Key : null,
                v => FiltroStatoCr = v != null && statiCr.TryGetValue(v, out var s) ? s : null),
            Multi("gamma", "Gamma", "Range", "Gamme", FiltroGamma),
            Multi("veicolo", "Veicolo", "Vehicle", "Véhicule", FiltroVeicolo),
            Multi("componente", "Componente", "Componente", "Componente", FiltroEcu),
            Multi("funzionalita", "Funzionalità", "Functionality", "Fonctionnalité", FiltroFunzionalita),
            Multi("statoanomalia", "Stato anomalia", "Anomaly status", "Statut anomalie", FiltroStatoAnomalia),
            Multi("priorita", "Priorità", "Priority", "Priorité", FiltroPriorita),
            Multi("gravita", "Gravità", "Severity", "Gravité", FiltroGravita),
            Multi("assegnatario", "Assegnatario", "Assignee", "Assigné", FiltroAssegnatario),
            Multi("creatorecr", "Creato da (CR)", "Created by (CR)", "Créé par (CR)", FiltroCreatoreCr),
            Data("aperturada", "Apertura da", "Opened from", "Ouverture du", () => FiltroDataAperturaDa, d => FiltroDataAperturaDa = d),
            Data("aperturaa", "Apertura a", "Opened to", "Ouverture au", () => FiltroDataAperturaA, d => FiltroDataAperturaA = d),
            Data("chiusurada", "Chiusura da", "Closed from", "Clôture du", () => FiltroDataChiusuraDa, d => FiltroDataChiusuraDa = d),
            Data("chiusuraa", "Chiusura a", "Closed to", "Clôture au", () => FiltroDataChiusuraA, d => FiltroDataChiusuraA = d),
        };
        return new ExportDescrittore("anomalie", loc.Traduci("ExpDlgTitoloAnomalie"), sezioni, RiepilogoFiltri, filtri, [
            new ExportFontePivot("nc-dati", loc.Traduci("ExpSezAnomalieDatiNc"), NonConformitaExportService.CampiPivot()),
            new ExportFontePivot("cr-dati", loc.Traduci("ExpSezAnomalieDatiCr"), ChangeRequestExportService.CampiPivot()),
            new ExportFontePivot("nc", loc.Traduci("ExpSezAnomalieVistaNc"), AnomalieCrExportVisteService.ColonneEsportabiliVistaNc()),
            new ExportFontePivot("nc-veicolo", loc.Scegli("NC per veicolo associato", "NC per associated vehicle", "NC par véhicule associé"), AnomalieCrExportVisteService.ColonneEsportabiliVistaNc()),
            new ExportFontePivot("cr", loc.Traduci("ExpSezAnomalieVistaCr"), AnomalieCrExportVisteService.ColonneEsportabiliVistaCr())]);
    }

    /// <summary>Stato applicativo per il dialogo di export (salvataggio delle configurazioni per operatore).</summary>
    internal AppState StatoPerExport => _state;

    /// <summary>Chiamato dal code-behind dopo il dialogo di salvataggio nativo, stesso schema di
    /// AuditViewModel.EsportaExcelVerso (feedback utente 2026-09-17: "devo poter esportare le non conformità
    /// con tutti i loro dati in excel"). Esporta tutte le NC corrispondenti ai filtri correnti, comprese quelle
    /// nelle pagine non visibili; l'elenco virtualizzato Righe contiene infatti solo la pagina corrente.</summary>
    public async Task EsportaExcelVerso(string percorso, ExportConfigurazione? scelta = null)
    {
        if (!_state.CartellaValida || EsportazioneInCorso) return;
        // Cattura i filtri sul thread UI, poi esegue una query completa sul thread di esportazione.
        // Righe contiene solo la pagina visibile e non può essere usata come origine del file.
        var gammaDiVeicolo = FiltroGamma.Selezionati.Count > 0 ? LeggiGammaDiVeicolo() : null;
        var richiesta = CostruisciRichiesta(gammaDiVeicolo) with { Pagina = 1, DimensionePagina = int.MaxValue };
        var filtroCr = CatturaFiltroChangeRequest() with { PaginaRichiesta = 1 };
        // Testi dei filtri e operatore letti qui, sul thread UI: il foglio Info li riporta senza leggere lo stato della vista dal worker.
        var riepilogoFiltriNc = RiepilogoFiltri;
        var riepilogoFiltriCr = RiepilogoFiltriCr;
        var operatoreExport = _state.Config.OperatoreAttivo;
        EsportazioneInCorso = true;
        Messaggio = LocalizationService.Instance.Traduci("GenerazioneReportInCorso");
        EsitoEsportazione esito;
        try
        {
            esito = await _esportazione.EseguiAsync(percorso, (temporaneo, ct) =>
            {
                var risultato = AnomalieQueryService.Esegui(_state.NonConformita,
                    id => _state.TestCases.GetById(id), richiesta, consentiIndice: false,
                    ct: ct, trovaUid: _state.TestCases.GetByUid);
                var changeRequests = _state.ChangeRequests.PeekAllReadOnly();
                var elencoCr = CalcolaChangeRequestsFiltrate(changeRequests, filtroCr, gammaDiVeicolo, ct);
                using var workbook = new ClosedXML.Excel.XLWorkbook();
                bool Ha(string id) => scelta == null || scelta.HaSezione(id);
                if (Ha(AnomalieSezioniExport.DatiNc))
                    NonConformitaExportService.ScriviFoglio(workbook, risultato.Righe.Select(r => r.Nc), _state, ct: ct,
                        colonne: scelta?.ColonneDi(AnomalieSezioniExport.DatiNc));
                var gammaExport = _state.Master.Veicoli.Load()
                    .ToDictionary(v => v.Nome, v => v.GammaNome, StringComparer.OrdinalIgnoreCase);
                if (Ha(AnomalieSezioniExport.DatiCr))
                    ChangeRequestExportService.ScriviFoglio(workbook, elencoCr, ct, gammaExport, scelta?.ColonneDi(AnomalieSezioniExport.DatiCr));
                // Viste "come visualizzate" in testa (stesso ordine e testi delle tabelle) e foglio Info: i fogli dati restano sotto.
                var righeVista = risultato.Righe.Select(r => new NcRiga
                {
                    Nc = r.Nc, TestCase = r.TestCase, TestCaseIdVisualizzato = r.TestCaseIdVisualizzato,
                    TestCaseEliminato = r.TestCase?.Eliminato ?? true
                }).ToList();
                AnomalieCrExportVisteService.Scrivi(workbook, righeVista, elencoCr, operatoreExport, DateTime.Now, riepilogoFiltriNc,
                    riepilogoFiltriCr, richiesta.Ordinamento, filtroCr.Ordinamento ?? ChangeRequestOrdinamento.Predefinito, ct,
                    new OpzioniVisteAnomalie(Ha(AnomalieSezioniExport.VistaNc), Ha(AnomalieSezioniExport.VistaCr),
                        Ha(AnomalieSezioniExport.Info) || workbook.Worksheets.Count == 0,
                        scelta?.ColonneDi(AnomalieSezioniExport.VistaNc), scelta?.ColonneDi(AnomalieSezioniExport.VistaCr)));
                if (scelta?.HaSezione("pivot") == true)
                {
                    var fonte = scelta.PivotFonte ?? "nc-dati";
                    var perVeicolo = fonte == "nc-veicolo";
                    var (campi, righe) = fonte switch
                    {
                        "cr-dati" => (ChangeRequestExportService.CampiPivot(), ChangeRequestExportService.RighePivot(elencoCr, gammaExport)),
                        "nc" => (AnomalieCrExportVisteService.ColonneEsportabiliVistaNc(), AnomalieCrExportVisteService.RighePivotNc(righeVista)),
                        "cr" => (AnomalieCrExportVisteService.ColonneEsportabiliVistaCr(), AnomalieCrExportVisteService.RighePivotCr(elencoCr)),
                        "nc-veicolo" => (AnomalieCrExportVisteService.ColonneEsportabiliVistaNc(), AnomalieCrExportVisteService.RighePivotNcPerVeicolo(righeVista, richiesta.Ambito.Veicoli)),
                        _ => (NonConformitaExportService.CampiPivot(), NonConformitaExportService.RighePivot(risultato.Righe.Select(r => r.Nc), _state, ct)),
                    };
                    ExportPivotService.Scrivi(workbook, campi, righe, scelta, ct,
                        perVeicolo ? LocalizationService.Instance.Scegli("Una riga per NC e veicolo associato: una NC può comparire più volte; i totali contano associazioni, non NC distinte.", "One row per NC and associated vehicle: an NC can occur more than once; totals count associations, not distinct NCs.", "Une ligne par NC et véhicule associé : une NC peut apparaître plusieurs fois ; les totaux comptent les associations, pas les NC distinctes.") : null);
                }
                using var stream = new FileStream(temporaneo, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                workbook.SaveAs(stream);
            });
        }
        finally
        {
            EsportazioneInCorso = false;
        }
        try
        {
            switch (esito.Stato)
            {
                case StatoEsportazione.Completata:
                    Messaggio = string.Format(LocalizationService.Instance.Traduci("NcExcelSalvatoFmt"), Path.GetFileName(percorso));
                    MessaggioErrore = null;
                    break;
                case StatoEsportazione.Annullata:
                    Messaggio = LocalizationService.Instance.Traduci("EsportazioneAnnullata");
                    MessaggioErrore = null;
                    break;
                default:
                    Messaggio = null;
                    MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("GenerazioneReportNonRiuscitaFmt"), esito.Errore?.Message);
                    break;
            }
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("GenerazioneReportNonRiuscitaFmt"), ex.Message);
        }
    }

    private readonly EsportazioneInBackground _esportazione = new();

    /// <summary>Vero mentre l'export Excel delle NC e in corso: la view disabilita il pulsante e mostra Annulla.</summary>
    [ObservableProperty] private bool esportazioneInCorso;

    [RelayCommand]
    private void AnnullaEsportazione() => _esportazione.Annulla();

    /// <summary>Punto di ingresso per una NC senza Test (feedback utente 2026-09-08): la rete assistenziale
    /// può segnalare un problema riscontrato su un veicolo prima ancora che qualcuno esegua un Test per
    /// riprodurlo — la NC porta allora i propri campi (componente/Veicolo/Funzionalità/Strumento) invece di ereditarli
    /// da un Test che non esiste.</summary>
    [RelayCommand]
    private void CreaSegnalazioneRete() => _apriSegnalazioneRete(null);

    [RelayCommand]
    private void ModificaSegnalazioneRete(NcRiga? riga)
    {
        if (riga != null) _apriSegnalazioneRete(riga.Nc.Id);
    }

    [RelayCommand]
    private void ApriChangeRequest(ChangeRequest? tc)
    {
        if (tc == null) return;
        if (ApriChangeRequestSolaLettura != null) ApriChangeRequestSolaLettura(tc.Id);
        else DettaglioChangeRequestRichiesto?.Invoke(tc);
    }

    [RelayCommand]
    private void ModificaChangeRequest(ChangeRequest? tc)
    {
        if (tc == null || !PuoModificareChangeRequest(tc)) return;
        _apriChangeRequest(tc.Id);
    }

    [RelayCommand]
    private void ModificaTestCaseCollegato(NcRiga? riga)
    {
        if (riga == null) return;
        var testPerId = new Dictionary<string, TestCase>(StringComparer.OrdinalIgnoreCase);
        if (riga.Nc.TestUid.HasValue || !string.IsNullOrWhiteSpace(riga.Nc.TestCaseId))
        {
            var tc = riga.Nc.TestUid.HasValue ? _state.TestCases.GetByUid(riga.Nc.TestUid.Value) : _state.TestCases.GetById(riga.Nc.TestCaseId);
            if (tc != null) testPerId[tc.Id] = tc;
        }
        foreach (var p in riga.Nc.ProveCollegate)
        {
            if (p.TestUid.HasValue || !string.IsNullOrWhiteSpace(p.TestId))
            {
                var tc = p.TestUid.HasValue ? _state.TestCases.GetByUid(p.TestUid.Value) : _state.TestCases.GetById(p.TestId);
                if (tc != null) testPerId[tc.Id] = tc;
            }
        }
        var corrente = AnomaliaWorkflowService.TestCorrente(riga.Nc, testPerId);
        if (corrente != null && !corrente.Eliminato) _modificaTestCase(corrente.Id);
    }

    /// <summary>
    /// Best-effort locale: non cambia il risultato del comando o la logica NC.
    /// La bozza viene salvata prima di eventuali rollback dei campi temporanei;
    /// l'errore del recupero non nasconde mai l'eccezione originale.
    /// </summary>
    private void SalvaBozzaNcFallita(NonConformita nc, Exception failure,
        NonConformita? baseSnapshot = null)
    {
        try
        {
            FailedSaveDraftService.Save(_state.Paths.Root, "nonconformita", nc.Uid,
                nc.RevisioneDati, _state.Config.OperatoreAttivo, nc, failure.Message,
                baseSnapshot: baseSnapshot);
        }
        catch (Exception e)
        {
            CrashLogService.Registra("Bozza locale NC dopo errore di salvataggio", e);
        }
    }

    private void SalvaBozzaSegnalazioneFallita(SegnalazioneDaClassificare item, Exception failure,
        SegnalazioneDaClassificare? baseSnapshot = null)
    {
        try
        {
            // La classificazione salva un'entità separata: non confondere
            // il payload di una segnalazione con quello della NC correlata.
            FailedSaveDraftService.Save(_state.Paths.Root, "segnalazione", item.Uid,
                item.RevisioneDati, _state.Config.OperatoreAttivo, item, failure.Message,
                baseSnapshot: baseSnapshot);
        }
        catch (Exception e)
        {
            CrashLogService.Registra("Bozza locale segnalazione dopo errore di salvataggio", e);
        }
    }

    [RelayCommand]
    private Task ChiudiManualmente(NonConformita? nc) =>
        ComandoAsincrono.EseguiAsync(() => ChiudiManualmenteCoreAsync(nc), "Chiusura manuale di una non conformità", m => MessaggioErrore = m);

    private async Task ChiudiManualmenteCoreAsync(NonConformita? nc)
    {
        if (nc == null) return;
        if (string.IsNullOrWhiteSpace(nc.MotivoChiusura))
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("ChiudereAnomaliaMotivo");
            return;
        }
        var loc = LocalizationService.Instance;
        if (!await ConfirmService.Chiedi(loc.Traduci("ChiudiAnomaliaConfermaTitolo"), loc.Traduci("ChiudiAnomaliaConfermaBody"),
                loc.Traduci("ChiudiAnomaliaBtn"), loc.Traduci("Annulla")))
            return;

        var baseSnapshot = EntityStateFingerprint.Clone(nc);
        NonConformitaRulesService.Chiudi(nc);
        if (!string.IsNullOrWhiteSpace(NuovaAzione)) nc.Azione = NuovaAzione;
        try { _state.NonConformita.Save(nc, nc.RevisioneDati); }
        catch (Exception ex) { SalvaBozzaNcFallita(nc, ex, baseSnapshot); MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ChiusuraNonRiuscitaFmt"), ex.Message); return; }
        MessaggioErrore = null;
        NuovaAzione = "";
        Ricarica();
    }

    /// <summary>Un solo salvataggio per riga: stato/severità/priorità/owner e un eventuale ticket TICKET digitato
    /// ma non ancora collegato si scrivono insieme in un'unica chiamata (feedback utente 2026-08-21, punto 8:
    /// prima "Salva modifiche" e "Collega" erano due bottoni distinti e solo uno salvava il ticket, quindi
    /// digitare il codice e cliccare quello sbagliato lo perdeva in silenzio).</summary>
    [RelayCommand]
    private void SalvaNc(NcRiga? riga)
    {
        if (riga == null) return;
        var nc = riga.Nc;
        if (!nc.Severita.HasValue) { MessaggioErrore = LocalizationService.Instance.Traduci("SeveritaObbl"); return; }
        var baseSnapshot = EntityStateFingerprint.Clone(nc);

        if (!AnomaliaAzioniService.TentaAggiungiTicket(nc, riga.NuovoTicketRiferimento, riga.NuovoTicketUrl, _state.Config.OperatoreAttivo, out var chiaveErrore, out var ticketAggiunto, _state.Paths))
        {
            MessaggioErrore = LocalizationService.Instance.Traduci(chiaveErrore!);
            return;
        }

        try { _state.NonConformita.Save(nc, nc.RevisioneDati); }
        catch (Exception ex)
        {
            SalvaBozzaNcFallita(nc, ex, baseSnapshot);
            if (ticketAggiunto) nc.CollegamentiPolarion.RemoveAt(nc.CollegamentiPolarion.Count - 1);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoFmt"), ex.Message);
            return;
        }
        MessaggioErrore = null;
        Ricarica();
    }

    public bool PuoEliminareAnomalie => _state.EMaster;

    /// <summary>Elimina definitivamente una NC — Master-only, coerente con l'eliminazione già disponibile
    /// dentro il Test (feedback utente 2026-08-21, punto 3). Qui la persistenza è sempre immediata, non c'è
    /// un salvataggio "del Test" a cui rimandarla come nell'editor.</summary>
    [RelayCommand]
    private Task EliminaAnomalia(NcRiga? riga) =>
        ComandoAsincrono.EseguiAsync(() => EliminaAnomaliaCoreAsync(riga), "Eliminazione di un'anomalia", m => MessaggioErrore = m);

    private async Task EliminaAnomaliaCoreAsync(NcRiga? riga)
    {
        if (riga == null || !_state.EMaster) return;
        var loc = LocalizationService.Instance;
        if (!await ConfirmService.Chiedi(loc.Traduci("EliminaAnomaliaConfermaTitolo"), loc.Traduci("EliminaAnomaliaConfermaBody"),
                loc.Traduci("EliminaBtn"), loc.Traduci("Annulla")))
            return;
        try { _state.NonConformita.Elimina(riga.Nc, _state.Config.OperatoreAttivo); }
        catch (Exception ex) { MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoFmt"), ex.Message); return; }
        MessaggioErrore = null;
        Ricarica();
    }

    /// <summary>Riporta un'anomalia chiusa per errore allo stato aperto — Master-only, coerente con
    /// EliminaAnomalia.</summary>
    [RelayCommand]
    private Task RiapriAnomalia(NcRiga? riga) =>
        ComandoAsincrono.EseguiAsync(() => RiapriAnomaliaCoreAsync(riga), "Riapertura di un'anomalia", m => MessaggioErrore = m);

    private async Task RiapriAnomaliaCoreAsync(NcRiga? riga)
    {
        if (riga == null || !_state.EMaster) return;
        var loc = LocalizationService.Instance;
        if (!await ConfirmService.Chiedi(loc.Traduci("RiapriAnomaliaConfermaTitolo"), loc.Traduci("RiapriAnomaliaConfermaBody"),
                loc.Traduci("RiapriAnomaliaBtn"), loc.Traduci("Annulla")))
            return;
        var baseSnapshot = EntityStateFingerprint.Clone(riga.Nc);
        NonConformitaRulesService.Riapri(riga.Nc);
        try { _state.NonConformita.Save(riga.Nc, riga.Nc.RevisioneDati); }
        catch (Exception ex) { SalvaBozzaNcFallita(riga.Nc, ex, baseSnapshot); MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoFmt"), ex.Message); return; }
        MessaggioErrore = null;
        Ricarica();
    }

    [RelayCommand]
    private void ClassificaSegnalazione(SegnalazioneRiga? riga)
    {
        if (riga == null) return;
        if (!riga.Severita.HasValue) { MessaggioErrore = LocalizationService.Instance.Traduci("ScegliSeveritaPrima"); return; }
        var seg = riga.Segnalazione;
        var baseSnapshot = EntityStateFingerprint.Clone(seg); // prima di qualunque mutazione: base della bozza di emergenza
        // Eredita anche la Priorità interna già impostata sul test, se c'è (feedback utente 2026-08-20, punto
        // 18) — resta comunque modificabile solo da un Master sulla NC, come sul test.
        var testOrigine = seg.TestUid.HasValue ? _state.TestCases.GetByUid(seg.TestUid.Value) : _state.TestCases.GetById(seg.TestId);
        if (testOrigine != null && !AnomaliaWorkflowService.SegnalazioneAppartieneAlTest(testOrigine, seg)) testOrigine = null;
        var testUid = seg.TestUid ?? testOrigine?.Uid;
        var nc = new NonConformita
        {
            Id = _state.NonConformita.NextId(), TestCaseId = seg.TestId, TestUid = testUid, DataApertura = DateTime.Now,
            Owner = _state.Config.OperatoreAttivo, Severita = riga.Severita, Priorita = testOrigine?.Priorita ?? PrioritaOperativa.Normal,
            StatoWorkflow = StatoAnomalia.Confermata, Descrizione = string.IsNullOrWhiteSpace(riga.TitoloAnomalia) ? riga.Reiterazione : riga.TitoloAnomalia,
            ProveCollegate = new List<RiferimentoProva> { new() { TestId = seg.TestId, TestUid = testUid, EsecuzioneIterazione = seg.EsecuzioneIterazione, CondizioneUid = seg.CondizioneUid } }
        };
        try
        {
            _state.NonConformita.Save(nc);
            seg.TestUid = testUid;
            seg.Stato = StatoSegnalazione.Classificata;
            seg.AnomaliaUid = nc.Uid;
            _state.Segnalazioni.Save(seg, seg.RevisioneDati);
        }
        catch (Exception ex)
        {
            SalvaBozzaSegnalazioneFallita(seg, ex, baseSnapshot);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ClassificazioneNonRiuscitaFmt"), ex.Message);
            return;
        }
        MessaggioErrore = null;
        _state.RicaricaTutto();
        Ricarica();
    }

    [RelayCommand]
    private void EscludiSegnalazione(SegnalazioneRiga? riga)
    {
        if (riga == null) return;
        if (string.IsNullOrWhiteSpace(riga.MotivazioneEsclusione)) { MessaggioErrore = LocalizationService.Instance.Traduci("MotivazioneEsclusioneObbl"); return; }
        var baseSnapshot = EntityStateFingerprint.Clone(riga.Segnalazione);
        riga.Segnalazione.Stato = StatoSegnalazione.Esclusa;
        riga.Segnalazione.MotivazioneEsclusione = riga.MotivazioneEsclusione.Trim();
        try { _state.Segnalazioni.Save(riga.Segnalazione, riga.Segnalazione.RevisioneDati); }
        catch (Exception ex) { SalvaBozzaSegnalazioneFallita(riga.Segnalazione, ex, baseSnapshot); MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("EsclusioneNonRiuscitaFmt"), ex.Message); return; }
        MessaggioErrore = null;
        Ricarica();
    }
}
