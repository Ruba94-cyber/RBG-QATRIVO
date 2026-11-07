using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Filtro "riferimento Ticketing" (feedback utente 2026-08-20, punto 19): tre stati espliciti invece
/// di un bool? nudo, più comodo da legare a un ComboBox in XAML.</summary>
public enum FiltroPolarioneOpzione { Tutti, ConPolarion, SenzaPolarion }

/// <summary>Un filtro attivo mostrato come chip nel riepilogo a pannello filtri chiuso (6.6.1). Il comando annulla solo quel filtro.</summary>
public sealed record ChipFiltroLista(string Etichetta, ICommand RimuoviCommand);

public sealed record RilascioLockMassaInAttesa(string TestId, string Percorso, string SessionId);

/// <summary>Tabella completa Test Case (§7.3): ordinabile/filtrabile a fondo su tutte le colonne,
/// a differenza del riepilogo rapido della Home.</summary>
public partial class TestCaseListViewModel : ViewModelBase, IPaginaAggiornabile, IScorciatoiePagina
{
    private readonly AppState _state;
    /// <summary>Stato proprietario per il dialogo di configurazione export.</summary>
    public AppState StatoPerExport => _state;
    private readonly Action<string?> _apriTestCase;
    private LocalTestArchiveQueryService? _archiveQuery;
    private bool _sospendiApplicaFiltri;
    private const int PageSize = LocalTestArchiveQueryService.DefaultPageSize;

    [ObservableProperty] private string filtroTesto = "";

    /// <summary>Testo legato al TextBox della ricerca: ad ogni carattere la query SQLite non parte subito, ma
    /// dopo una breve pausa di digitazione (debounce), poi propaga a FiltroTesto. Impostazioni programmatiche
    /// di FiltroTesto (reset, viste) restano immediate e vengono riflesse qui.</summary>
    [ObservableProperty] private string filtroTestoDigitato = "";
    internal static TimeSpan DebounceTesto = TimeSpan.FromMilliseconds(250);
    /// <summary>Percorso indicizzato della pagina (query SQLite). false = sempre il percorso in memoria: serve al test
    /// di parita' e alle misure A/B nello stesso processo. In produzione resta true.</summary>
    internal bool PercorsoIndicizzatoAttivo { get; set; } = true;
    private CancellationTokenSource? _debounceTestoCts;

    // Filtri lista a selezione multipla (richiesta utente 6.4.2: pagina Test allineata alle altre, pulsante +
    // multi-selezione con ricerca oltre 10 voci). Nessuna voce spuntata = nessun filtro (stesso significato del
    // vecchio null). Valore = chiave stabile (nome/enum/Uid), Etichetta = testo mostrato.
    public FiltroMultiSelect FiltroEcu { get; } = new();
    public FiltroMultiSelect FiltroVeicolo { get; } = new();
    public FiltroMultiSelect FiltroTemplate { get; } = new();
    public FiltroMultiSelect FiltroEsito { get; } = new();

    /// <summary>Filtri aggiuntivi opzionali (feedback utente 2026-08-20, punto 19: "mancano i filtri per
    /// assegnatario e gli altri filtri che dovrebbero essere opzionali").</summary>
    public FiltroMultiSelect FiltroAssegnatario { get; } = new();
    public FiltroMultiSelect FiltroCreatoDa { get; } = new();
    [ObservableProperty] private FiltroPolarioneOpzione filtroHaPolarion = FiltroPolarioneOpzione.Tutti;
    [ObservableProperty] private bool mostraFiltriAvanzati;
    public FiltroMultiSelect FiltroStatoLavoro { get; } = new();
    public FiltroMultiSelect FiltroRequisito { get; } = new();
    public FiltroMultiSelect FiltroFunzionalita { get; } = new();
    public FiltroMultiSelect FiltroSeverita { get; } = new();
    public FiltroMultiSelect FiltroPriorita { get; } = new();
    public FiltroMultiSelect FiltroStatoAnomalia { get; } = new();

    /// <summary>Filtri dei campi di sistema usabili come filtro (UsabileComeFiltro), come in Home e Report. La lista mostra solo i campi
    /// pertinenti ai Test del tipo scelto (o tutti, senza tipo); <see cref="_filtriCampiCustom"/> tiene un filtro per ogni campo eleggibile,
    /// con la sua selezione anche quando non e' pertinente.</summary>
    public ObservableCollection<FiltroCampoCustom> FiltriCampiCustomLista { get; } = new();
    private readonly List<FiltroCampoCustom> _filtriCampiCustom = new();
    /// <summary>Selezioni (formato delle viste salvate) dei campi che non hanno un filtro: campo tolto dal catalogo filtri, oppure voce di una
    /// vista o dello snapshot non ancora conciliata. Tornano quando il campo e' di nuovo eleggibile.</summary>
    private readonly Dictionary<string, string> _selezioniCampiCustomInAttesa = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Tipi di test con cui e' stata conciliata la pertinenza dei campi (vedi <see cref="ConciliaFiltriCampiCustom"/>).</summary>
    private string _firmaTipiTestCampiCustom = "";
    [ObservableProperty] private DateTimeOffset? filtroDataDa;
    [ObservableProperty] private DateTimeOffset? filtroDataA;

    /// <summary>Segnalazione "data digitata non valida" (CalendarDatePicker): il vincolo del campo viene azzerato e il
    /// messaggio sotto i filtri resta visibile finche' l'utente non corregge il valore o sceglie una data dal calendario.</summary>
    [ObservableProperty] private bool filtroDataDaNonValida;
    [ObservableProperty] private bool filtroDataANonValida;
    public bool HaDataNonValida => FiltroDataDaNonValida || FiltroDataANonValida;

    partial void OnFiltroDataDaNonValidaChanged(bool value) => OnPropertyChanged(nameof(HaDataNonValida));
    partial void OnFiltroDataANonValidaChanged(bool value) => OnPropertyChanged(nameof(HaDataNonValida));

    /// <summary>Chiamato quando il testo digitato in un campo data non e' una data valida: il filtro di quel campo non
    /// viene applicato (mai una data diversa da quella mostrata) e la segnalazione resta visibile.</summary>
    public void SegnalaDataNonValida(string campo)
    {
        switch (campo)
        {
            case nameof(FiltroDataDa): FiltroDataDa = null; FiltroDataDaNonValida = true; break;
            case nameof(FiltroDataA): FiltroDataA = null; FiltroDataANonValida = true; break;
            default: throw new ArgumentOutOfRangeException(nameof(campo), campo, "Campo data sconosciuto.");
        }
    }

    /// <summary>Rimuove la segnalazione di un campo: il tasto premuto o l'uscita dal campo avviano un nuovo tentativo.</summary>
    public void AzzeraDataNonValida(string campo)
    {
        switch (campo)
        {
            case nameof(FiltroDataDa): FiltroDataDaNonValida = false; break;
            case nameof(FiltroDataA): FiltroDataANonValida = false; break;
            default: throw new ArgumentOutOfRangeException(nameof(campo), campo, "Campo data sconosciuto.");
        }
    }

    /// <summary>Piano 6.4.0 punto 5(3): filtro dedicato per isolare i Test senza alcuna componente associata (elenco
    /// "da correggere" raggiunto dall'avviso della Home). Forza il percorso legacy in Applica() come già
    /// avviene per FiltroHaPolarion/FiltroStatoAnomalia, invece di estendere la query SQLite scalabile — il
    /// caso d'uso è occasionale (backlog di correzione), non la navigazione quotidiana ad alto volume.</summary>
    [ObservableProperty] private bool filtroSoloSenzaEcu;

    /// <summary>Ingresso dall'avviso Home "Test senza componente": azzera prima ogni altro filtro (anche quelli
    /// persistiti) e poi isola i Test senza componente, altrimenti l'AND con filtri gia' attivi darebbe lista vuota.</summary>
    public void ImpostaSoloSenzaEcu()
    {
        ReimpostaFiltri();
        FiltroSoloSenzaEcu = true;
    }

    /// <summary>Filtro dedicato pagina Test (decisione utente 6.4.0, "Test importati: solo stato finale"):
    /// isola i Test con OrigineTest.ImportStatoUdt, etichettati "Importato da UnitLogger" nella lista/editor. Persistito
    /// nelle Viste salvate come gli altri filtri (CatturaFiltriCorrenti/ApplicaVista).</summary>
    [ObservableProperty] private bool filtroSoloImportatoUdt;
    [ObservableProperty] private long paginaCorrente = 1;
    [ObservableProperty] private long totaleRisultati;

    public Array OpzioniFiltroPolarione => Enum.GetValues(typeof(FiltroPolarioneOpzione));

    private IEnumerable<FiltroMultiSelect> FiltriMultiSelect => new[]
    {
        FiltroEcu, FiltroVeicolo, FiltroTemplate, FiltroEsito, FiltroAssegnatario, FiltroCreatoDa,
        FiltroStatoLavoro, FiltroRequisito, FiltroFunzionalita, FiltroSeverita, FiltroPriorita, FiltroStatoAnomalia
    };

    public bool HaFiltriAttivi => !string.IsNullOrWhiteSpace(FiltroTesto) || FiltriMultiSelect.Any(f => f.HaSelezione) ||
        FiltriCampiCustomLista.Any(f => f.Filtro.HaSelezione) || FiltroHaPolarion != FiltroPolarioneOpzione.Tutti ||
        FiltroDataDa != null || FiltroDataA != null || FiltroSoloSenzaEcu || FiltroSoloImportatoUdt;

    public BulkObservableCollection<TestCase> Righe { get; } = new();
    public bool RigheVuote => Righe.Count == 0;
    public List<string> EcuDisponibili { get; private set; } = new();
    public List<string> VeicoliDisponibili { get; private set; } = new();
    public List<TestTemplate> TemplatesDisponibili { get; private set; } = new();
    public List<string> AssegnatariDisponibili { get; private set; } = new();
    public List<string> CreatoriDisponibili { get; private set; } = new();
    public List<string> RequisitiDisponibili { get; private set; } = new();
    public List<string> FunzionalitaDisponibili { get; private set; } = new();
    public Array Esiti => Enum.GetValues(typeof(Esito));
    public Array StatiLavoro => Enum.GetValues(typeof(StatoLavoroTest));
    public Array SeveritaDisponibili => Enum.GetValues(typeof(SeveritaAnomalia));
    public Array PrioritaDisponibili => Enum.GetValues(typeof(PrioritaOperativa));
    public Array StatiAnomaliaDisponibili => Enum.GetValues(typeof(StatoAnomalia));
    public bool PuoCreare => _state.PuoScrivere;

    public long PagineTotali => Math.Max(1L, checked((TotaleRisultati + PageSize - 1L) / PageSize));
    public bool PuoPaginaPrecedente => PaginaCorrente > 1;
    public bool PuoPaginaSuccessiva => PaginaCorrente < PagineTotali;
    public string TestoPaginazione => LocalizationService.Instance.Scegli(
        $"Pagina {PaginaCorrente} / {PagineTotali} · {TotaleRisultati:N0} test",
        $"Page {PaginaCorrente} / {PagineTotali} · {TotaleRisultati:N0} tests",
        $"Page {PaginaCorrente} / {PagineTotali} · {TotaleRisultati:N0} tests");

    /// <summary>Vero se il quick-filter "Assegnati a me" è attivo (feedback utente 2026-08-20, punto 19):
    /// serve alla UI per evidenziare il pulsante come "attivo" invece di un semplice comando senza stato.</summary>
    // TwoWay dalla View: il ToggleButton imposta IsChecked da solo al click e con un binding OneWay lo stato
    // visivo poteva divergere dal filtro reale. Il setter applica il toggle; il getter riflette sempre il filtro.
    public bool FiltroAssegnatiAMeAttivo
    {
        get => SoloSelezionato(FiltroAssegnatario, _state.Config.OperatoreAttivo);
        set { if (value != FiltroAssegnatiAMeAttivo) ImpostaQuickFilter(FiltroAssegnatario, FiltroAssegnatiAMeAttivo); }
    }
    public bool FiltroCreatiDaMeAttivo
    {
        get => SoloSelezionato(FiltroCreatoDa, _state.Config.OperatoreAttivo);
        set { if (value != FiltroCreatiDaMeAttivo) ImpostaQuickFilter(FiltroCreatoDa, FiltroCreatiDaMeAttivo); }
    }

    private static bool SoloSelezionato(FiltroMultiSelect filtro, string? valore)
    {
        if (string.IsNullOrEmpty(valore)) return false;
        var sel = filtro.Selezionati;
        return sel.Count == 1 && sel[0] == valore;
    }

    public TestCaseListViewModel(AppState state, Action<string?> apriTestCase)
    {
        _state = state;
        _apriTestCase = apriTestCase;
        foreach (var f in FiltriMultiSelect) f.Cambiato += OnFiltroMultiSelectCambiato;
        CaricaVisteSalvate();
        Aggiorna();
        // Ultimi filtri persistiti per questo PC/operatore (feedback utente 2026-09-18: "la stabilita' deve
        // rimanere anche in Test, CR/anomalie ecc, tutte le pagine non solo in Home e Report"). DOPO
        // Aggiorna(): serve TemplatesDisponibili gia' popolato per risolvere FiltroTemplate dall'Uid
        // persistito, esattamente come quando l'utente applica manualmente una Vista Salvata (stesso
        // ApplicaVista, qui alimentato dallo snapshot automatico invece che da un salvataggio con nome).
        // Pannello filtri (6.6.1): aperto di default; se l'operatore l'ha chiuso, la scelta resta nello stesso store locale.
        filtriVisibili = ViewStateService.Leggi(state, ChiaveFiltriVisibili) != "0";
        var persistiti = ViewStateService.LeggiDizionario(state, ChiaveVistaPersistente);
        if (persistiti.Count > 0) ApplicaVista(new VistaSalvataFiltri { Valori = new Dictionary<string, string?>(persistiti) });
    }

    // === Viste salvate (Addendum: audit UX 2026-08-26, punto 3) =============================================
    private const string ChiaveVista = "test-list";
    // Distinta da ChiaveVista sopra (usata per il TableId di TableLayoutPreferences E per le Viste Salvate con
    // nome): questa e' la chiave ViewStateService per lo snapshot AUTOMATICO, non richiesto dall'utente.
    private const string ChiaveVistaPersistente = "TestList.Filtri";
    /// <summary>Prefisso delle selezioni dei campi di sistema dentro Valori (viste salvate e snapshot): "CampoSistema." + Chiave del campo.</summary>
    private const string PrefissoCampoSistemaVista = "CampoSistema.";
    public ObservableCollection<VistaSalvataFiltri> VisteSalvate { get; } = new();
    [ObservableProperty] private VistaSalvataFiltri? vistaSelezionata;

    private void CaricaVisteSalvate()
    {
        VisteSalvate.Clear();
        foreach (var v in _state.Config.VisteSalvate.GetValueOrDefault(ChiaveVista) ?? new List<VistaSalvataFiltri>())
            VisteSalvate.Add(v);
    }

    partial void OnVistaSelezionataChanged(VistaSalvataFiltri? value)
    {
        if (value != null) ApplicaVista(value);
        EliminaVistaSelezionataCommand.NotifyCanExecuteChanged();
    }

    private Dictionary<string, string?> CatturaFiltriCorrenti()
    {
        var filtri = new Dictionary<string, string?>
        {
            ["Testo"] = string.IsNullOrWhiteSpace(FiltroTesto) ? null : FiltroTesto,
            ["Componente"] = Serializza(FiltroEcu),
            ["Veicolo"] = Serializza(FiltroVeicolo),
            ["TemplateUid"] = Serializza(FiltroTemplate),
            ["Esito"] = Serializza(FiltroEsito),
            ["Assegnatario"] = Serializza(FiltroAssegnatario),
            ["CreatoDa"] = Serializza(FiltroCreatoDa),
            ["HaPolarion"] = FiltroHaPolarion == FiltroPolarioneOpzione.Tutti ? null : FiltroHaPolarion.ToString(),
            ["StatoLavoro"] = Serializza(FiltroStatoLavoro),
            ["Requisito"] = Serializza(FiltroRequisito),
            ["Funzionalita"] = Serializza(FiltroFunzionalita),
            ["Severita"] = Serializza(FiltroSeverita),
            ["Priorita"] = Serializza(FiltroPriorita),
            ["StatoAnomalia"] = Serializza(FiltroStatoAnomalia),
            ["DataDa"] = FiltroDataDa?.ToString("O"),
            ["DataA"] = FiltroDataA?.ToString("O"),
            ["SoloSenzaEcu"] = FiltroSoloSenzaEcu ? "1" : null,
            ["SoloImportatoUdt"] = FiltroSoloImportatoUdt ? "1" : null,
        };
        // Anche le selezioni dei campi di sistema non pertinenti al tipo scelto: tornano quando il tipo torna pertinente.
        foreach (var (chiave, grezzo) in SelezioniCampiCustomDaSalvare())
            filtri[PrefissoCampoSistemaVista + chiave] = grezzo;
        return filtri;
    }

    // Serializzazione delle selezioni multiple per le viste salvate: condivisa con WU Status (FiltroMultiSelectSalvataggio).
    private static string? Serializza(FiltroMultiSelect filtro) => FiltroMultiSelectSalvataggio.Serializza(filtro);

    private static void ApplicaSelezione(FiltroMultiSelect filtro, string? grezzo, bool conservaMancanti = false) =>
        FiltroMultiSelectSalvataggio.ApplicaSelezione(filtro, grezzo, conservaMancanti);

    private void ApplicaVista(VistaSalvataFiltri vista)
    {
        var v = vista.Valori;
        _sospendiApplicaFiltri = true;
        try
        {
            ImpostaTestoImmediato(v.GetValueOrDefault("Testo") ?? "");
            ApplicaSelezione(FiltroEcu, v.GetValueOrDefault("Componente"), conservaMancanti: true);
            ApplicaSelezione(FiltroVeicolo, v.GetValueOrDefault("Veicolo"), conservaMancanti: true);
            ApplicaSelezione(FiltroTemplate, v.GetValueOrDefault("TemplateUid"));
            ApplicaSelezione(FiltroEsito, v.GetValueOrDefault("Esito"));
            ApplicaSelezione(FiltroAssegnatario, v.GetValueOrDefault("Assegnatario"), conservaMancanti: true);
            ApplicaSelezione(FiltroCreatoDa, v.GetValueOrDefault("CreatoDa"), conservaMancanti: true);
            FiltroHaPolarion = Enum.TryParse<FiltroPolarioneOpzione>(v.GetValueOrDefault("HaPolarion"), out var hp) ? hp : FiltroPolarioneOpzione.Tutti;
            ApplicaSelezione(FiltroStatoLavoro, v.GetValueOrDefault("StatoLavoro"));
            ApplicaSelezione(FiltroRequisito, v.GetValueOrDefault("Requisito"), conservaMancanti: true);
            ApplicaSelezione(FiltroFunzionalita, v.GetValueOrDefault("Funzionalita"), conservaMancanti: true);
            ApplicaSelezione(FiltroSeverita, v.GetValueOrDefault("Severita"));
            ApplicaSelezione(FiltroPriorita, v.GetValueOrDefault("Priorita"));
            ApplicaSelezione(FiltroStatoAnomalia, v.GetValueOrDefault("StatoAnomalia"));
            FiltroDataDa = DateTimeOffset.TryParse(v.GetValueOrDefault("DataDa"), out var dd) ? dd : null;
            FiltroDataA = DateTimeOffset.TryParse(v.GetValueOrDefault("DataA"), out var da) ? da : null;
            FiltroDataDaNonValida = false;
            FiltroDataANonValida = false;
            FiltroSoloSenzaEcu = v.GetValueOrDefault("SoloSenzaEcu") == "1";
            FiltroSoloImportatoUdt = v.GetValueOrDefault("SoloImportatoUdt") == "1";
            ApplicaCampiCustomVista(v);
        }
        finally { _sospendiApplicaFiltri = false; }
        PersistiFiltriCorrenti();
        PaginaCorrente = 1;
        Applica();
    }

    // Errore di un'azione sulla lista (vista salvata, azioni di massa): resta nel banner della pagina finché non cambia (P4 2).
    /// <summary>Dialogo condiviso Export (filtri correnti, sezioni e colonne) con
    /// selezione aggiuntiva dei singoli campi dei tool definiti nel Master.</summary>
    public ExportDescrittore CreaDescrittoreExport()
    {
        // Una ricerca appena digitata e' gia' visibile nel TextBox anche prima
        // del debounce: il dialogo deve ripartire ESATTAMENTE da quel testo.
        ConfermaRicercaTestoImmediata();
        var filtri = new List<ExportFiltro>
        {
            new ExportFiltroTesto("testo", Traduci("TestListChipTesto"),
                () => FiltroTestoDigitato, ImpostaTestoImmediato),
        };
        filtri.AddRange(CampiChipMultiSelect.Select(x =>
            (ExportFiltro)new ExportFiltroMultiplo(x.Chiave, Traduci(x.Chiave), x.Filtro)));
        filtri.Add(new ExportFiltroSingolo("ticketing", "Ticketing",
            ["ConPolarion", "SenzaPolarion"],
            () => FiltroHaPolarion == FiltroPolarioneOpzione.Tutti ? null : FiltroHaPolarion.ToString(),
            v => FiltroHaPolarion = Enum.TryParse<FiltroPolarioneOpzione>(v, out var scelta)
                ? scelta : FiltroPolarioneOpzione.Tutti));
        filtri.Add(new ExportFiltroData("dataDa",
            LocalizationService.Instance.Scegli("Creazione dal", "Created from", "Créé à partir du"),
            () => FiltroDataDa?.Date, d => FiltroDataDa = d.HasValue ? new DateTimeOffset(d.Value) : null));
        filtri.Add(new ExportFiltroData("dataA",
            LocalizationService.Instance.Scegli("Creazione al", "Created to", "Créé jusqu'au"),
            () => FiltroDataA?.Date, d => FiltroDataA = d.HasValue ? new DateTimeOffset(d.Value) : null));
        filtri.Add(new ExportFiltroBooleano("senzaEcu",
            LocalizationService.Instance.Scegli("Solo Test senza componente", "Tests without componente only", "Tests sans componente uniquement"),
            () => FiltroSoloSenzaEcu, v => FiltroSoloSenzaEcu = v));
        filtri.Add(new ExportFiltroBooleano("importatoUdt",
            LocalizationService.Instance.Scegli("Solo importati UnitLogger", "UnitLogger imports only", "Imports UnitLogger uniquement"),
            () => FiltroSoloImportatoUdt, v => FiltroSoloImportatoUdt = v));
        foreach (var campo in FiltriCampiCustomLista)
            filtri.Add(new ExportFiltroMultiplo("custom-" + campo.Chiave, campo.Etichetta, campo.Filtro));

        var riepilogo = HaFiltriAttivi
            ? string.Join(" · ", ChipFiltriAttivi.Select(x => x.Etichetta))
            : LocalizationService.Instance.Traduci("ExpDlgNessunFiltro");
        return TestCaseExportSelezioneService.CreaDescrittore(
            _state.Master.Strumenti.Load(), _state.Master.TestTemplate.Load(), riepilogo,
            filtri, ApplicaFiltriExportInBlocco);
    }

    private void ApplicaFiltriExportInBlocco(Action applica)
    {
        var sospesoPrima = _sospendiApplicaFiltri;
        _sospendiApplicaFiltri = true;
        try { applica(); }
        finally { _sospendiApplicaFiltri = sospesoPrima; }
        if (!sospesoPrima)
        {
            PersistiFiltriCorrenti();
            PaginaCorrente = 1;
            Applica(); // Una sola query, anche quando nel dialogo cambiano molti filtri.
        }
    }

    /// <summary>Congela i valori dei filtri dopo la conferma del dialogo, cosi'
    /// la lettura dei Test e le operazioni Excel non dipendono dalla UI.</summary>
    private TestCaseExportFiltro CatturaFiltriExport()
    {
        var multipli = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        var filtri = new (string Chiave, FiltroMultiSelect Valori)[]
        {
            ("Componente", FiltroEcu), ("Veicolo", FiltroVeicolo),
            ("TemplateUid", FiltroTemplate), ("Esito", FiltroEsito),
            ("Assegnatario", FiltroAssegnatario), ("CreatoDa", FiltroCreatoDa),
            ("StatoLavoro", FiltroStatoLavoro), ("Requisito", FiltroRequisito),
            ("Funzionalita", FiltroFunzionalita), ("Severita", FiltroSeverita),
            ("Priorita", FiltroPriorita), ("StatoAnomalia", FiltroStatoAnomalia)
        };
        foreach (var (chiave, filtro) in filtri)
            multipli[chiave] = filtro.Selezionati.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Lo stesso comportamento della Lista Test: selezioni stale che non si
        // possono piu' convertire in enum/Guid non diventano filtri che escludono
        // silenziosamente ogni risultato dall'Excel.
        multipli["TemplateUid"] = (ValoriGuid(FiltroTemplate) ?? [])
            .Select(v => v.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        multipli["Esito"] = (ValoriEnum<Esito>(FiltroEsito) ?? [])
            .Select(v => v.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        multipli["StatoLavoro"] = (ValoriEnum<StatoLavoroTest>(FiltroStatoLavoro) ?? [])
            .Select(v => v.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        multipli["Severita"] = (ValoriEnum<SeveritaAnomalia>(FiltroSeverita) ?? [])
            .Select(v => v.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        multipli["Priorita"] = (ValoriEnum<PrioritaOperativa>(FiltroPriorita) ?? [])
            .Select(v => v.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var statiSelezionati = ValoriEnum<StatoAnomalia>(FiltroStatoAnomalia);
        multipli["StatoAnomalia"] = (statiSelezionati ?? [])
            .Select(v => v.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ncStato = statiSelezionati is { Count: > 0 }
            ? UidTestConNcCheSoddisfa(nc => nc.Stato == StatoNonConformita.Aperta
                && statiSelezionati.Contains(nc.StatoWorkflow))
                .ToHashSet()
            : new HashSet<Guid>();
        var ncPolarion = FiltroHaPolarion != FiltroPolarioneOpzione.Tutti
            ? UidTestConNcCheSoddisfa(nc => nc.CollegamentiPolarion.Count > 0).ToHashSet()
            : new HashSet<Guid>();
        var custom = SelezioniCampiCustomAttive();
        return new TestCaseExportFiltro
        {
            Ricerca = FiltroTesto,
            Dal = FiltroDataDa?.Date, Al = FiltroDataA?.Date,
            SoloSenzaEcu = FiltroSoloSenzaEcu,
            SoloImportatoUdt = FiltroSoloImportatoUdt,
            Ticketing = FiltroHaPolarion,
            UidTestConNcStato = ncStato,
            UidTestConNcPolarion = ncPolarion,
            Multi = multipli,
            CampiCustom = custom?.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyCollection<string>)kv.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private readonly EsportazioneInBackground _esportazioneCompleta = new();
    [ObservableProperty] private string? messaggioExportCompleto;
    public bool HaMessaggioExportCompleto => !string.IsNullOrWhiteSpace(MessaggioExportCompleto);
    partial void OnMessaggioExportCompletoChanged(string? value) => OnPropertyChanged(nameof(HaMessaggioExportCompleto));
    [ObservableProperty] private bool esportazioneCompletaInCorso;

    /// <summary>
    /// Esporta i Test attivi autorizzati che corrispondono ai filtri confermati
    /// (non soltanto quelli della pagina corrente), o tutti i Test accessibili
    /// quando l'operatore sceglie esplicitamente IgnoraFiltri.
    /// Si riutilizza il perimetro operatore della Lista Test e si escludono i template CR.
    /// Generazione e salvataggio avvengono fuori dal thread UI; annullamento e scrittura atomica
    /// impediscono file incompleti, anche su archivi numerosi.
    /// </summary>
    public async Task EsportaTuttiTestVerso(string percorso,
        ExportConfigurazione? opzioni = null, ExportDescrittore? descrittore = null)
    {
        if (IsBusy || !_state.CartellaValida) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(percorso);
        // null = compatibilita' con export generale precedente, nessun filtro.
        // L'opzione IgnoraFiltri e' esplicita, mai inferita dalla pagina.
        var filtro = opzioni == null || opzioni.IgnoraFiltri ? null : CatturaFiltriExport();
        var colonneTool = opzioni != null && descrittore != null
            ? TestCaseExportSelezioneService.ChiaviStrumentoSelezionate(opzioni, descrittore) : null;
        var colonneCustom = opzioni != null && descrittore != null
            ? TestCaseExportSelezioneService.ChiaviCustomSelezionate(opzioni, descrittore) : null;
        var riepilogoConfermato = filtro == null
            ? "Nessun filtro utente (tutti i Test accessibili)"
            : HaFiltriAttivi
                ? string.Join(" · ", ChipFiltriAttivi.Select(c => c.Etichetta))
                : "Nessun filtro attivo";
        IsBusy = true;
        MessaggioErrore = null;
        MessaggioExportCompleto = null;
        EsportazioneCompletaInCorso = true;
        var totaleEsportati = 0;
        try
        {
            var risultato = await _esportazioneCompleta.EseguiAsync(percorso, (temporaneo, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var catalogoTipiTest = _state.Master.TestTemplate.Load();
                var templatesCr = catalogoTipiTest
                    .Where(t => t.IsChangeRequest).Select(t => t.Uid).ToHashSet();
                IEnumerable<TestCase> candidati = _state.FiltraTestCaseVisibiliPerOperatore(
                    _state.TestCases.GetAll()).Where(t => !templatesCr.Contains(t.TestTemplateUid));
                if (filtro != null) candidati = filtro.Applica(candidati, ct);
                var test = candidati.OrderBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(t => t.Uid).ToList();
                totaleEsportati = test.Count;
                // Catalogo strumenti letto nello stesso scope snapshot dell'export:
                // comprende anche definizioni di campi opzionali ancora non valorizzati.
                var catalogoStrumenti = _state.Master.Strumenti.Load();
                using var workbook = TestCaseCompleteExportService.CreaWorkbook(
                    test, ct, catalogoStrumenti, catalogoTipiTest, colonneTool, colonneCustom, opzioni);
                TestCaseExportSelezioneService.ScriviContesto(workbook, opzioni, riepilogoConfermato, totaleEsportati);
                ct.ThrowIfCancellationRequested();
                if (opzioni != null) TestCaseExportSelezioneService.Applica(workbook, opzioni);
                ct.ThrowIfCancellationRequested();
                // Il temporaneo non ha estensione Excel: ClosedXML accetta SaveAs(percorso) solo con .xlsx/.xlsm, quindi si scrive su stream.
                using var flusso = new FileStream(temporaneo, FileMode.Create, FileAccess.Write, FileShare.None);
                workbook.SaveAs(flusso);
            });
            switch (risultato.Stato)
            {
                case StatoEsportazione.Completata:
                    MessaggioExportCompleto = string.Format(
                        LocalizationService.Instance.Traduci("TestListExportCompletoSuccessoFmt"), totaleEsportati);
                    break;
                case StatoEsportazione.Annullata:
                    MessaggioExportCompleto = LocalizationService.Instance.Traduci("EsportazioneAnnullata");
                    break;
                default:
                    MessaggioExportCompleto = null;
                    MessaggioErrore = risultato.Errore?.Message ?? LocalizationService.Instance.Traduci("TestListExportCompletoErrore");
                    break;
            }
        }
        finally
        {
            IsBusy = false;
            EsportazioneCompletaInCorso = false;
        }
    }

    [RelayCommand]
    private void AnnullaEsportazioneCompleta() => _esportazioneCompleta.Annulla();

    [ObservableProperty] private string? messaggioErrore;
    public bool HaMessaggioErrore => !string.IsNullOrEmpty(MessaggioErrore);
    partial void OnMessaggioErroreChanged(string? value) => OnPropertyChanged(nameof(HaMessaggioErrore));

    private Task EseguiComandoConErroreAsync(Func<Task> azione, string origine)
    {
        MessaggioErrore = null;
        return ComandoAsincrono.EseguiAsync(azione, origine, m => MessaggioErrore = m);
    }

    [RelayCommand]
    private Task SalvaVistaCorrente() =>
        EseguiComandoConErroreAsync(SalvaVistaCorrenteCoreAsync, "Salvataggio della vista della lista Test");

    private async Task SalvaVistaCorrenteCoreAsync()
    {
        var nome = await TextEditService.Modifica(LocalizationService.Instance.Traduci("SalvaVistaTitolo"), "");
        if (string.IsNullOrWhiteSpace(nome)) return;
        nome = nome.Trim();

        if (!_state.Config.VisteSalvate.TryGetValue(ChiaveVista, out var lista))
        {
            lista = new List<VistaSalvataFiltri>();
            _state.Config.VisteSalvate[ChiaveVista] = lista;
        }
        lista.RemoveAll(v => v.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase));
        lista.Add(new VistaSalvataFiltri { Nome = nome, Valori = CatturaFiltriCorrenti() });
        _state.ConfigService.Save(_state.Config);
        CaricaVisteSalvate();
        VistaSelezionata = VisteSalvate.First(v => v.Nome == nome);
    }

    private bool PuoEliminareVistaSelezionata() => VistaSelezionata != null;

    [RelayCommand(CanExecute = nameof(PuoEliminareVistaSelezionata))]
    private Task EliminaVistaSelezionata() =>
        EseguiComandoConErroreAsync(EliminaVistaSelezionataCoreAsync, "Eliminazione della vista della lista Test");

    private async Task EliminaVistaSelezionataCoreAsync()
    {
        if (VistaSelezionata == null) return;
        if (!await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("EliminaVistaTitoloConferma"),
            string.Format(LocalizationService.Instance.Traduci("EliminaVistaBodyFmt"), VistaSelezionata.Nome),
            LocalizationService.Instance.Traduci("Elimina"), LocalizationService.Instance.Traduci("Annulla"))) return;

        if (_state.Config.VisteSalvate.TryGetValue(ChiaveVista, out var lista))
            lista.RemoveAll(v => v.Nome == VistaSelezionata.Nome);
        _state.ConfigService.Save(_state.Config);
        VistaSelezionata = null;
        CaricaVisteSalvate();
    }

    public void Aggiorna()
    {
        // Le Ripopola dei filtri multi-selezione, se una selezione esisteva gia', scatenano Cambiato: qui
        // sospese, perche' Applica() in fondo e' l'unica query necessaria (mai due passate per la stessa visita).
        var sospesoPrima = _sospendiApplicaFiltri;
        _sospendiApplicaFiltri = true;
        try { RipopolaOpzioniFiltri(); }
        finally { _sospendiApplicaFiltri = sospesoPrima; }
        Applica();
    }

    /// <summary>Ripopola le opzioni di tutti i filtri multi-selezione (catalogo valori + enum + Tipi test),
    /// preservando la selezione gia' fatta (FiltroMultiSelect.Ripopola: a contenuto identico non muta nulla).</summary>
    private void RipopolaOpzioniFiltri()
    {
        // Enum: sempre disponibili (anche senza cartella); etichette localizzate, quindi un cambio lingua
        // ricostruisce le opzioni alla visita successiva (Ripopola confronta anche le etichette).
        FiltroEsito.Ripopola(Enum.GetValues<Esito>().Select(e => (e.ToString(), e.Label())));
        FiltroStatoLavoro.Ripopola(Enum.GetValues<StatoLavoroTest>().Select(e => (e.ToString(), e.Label())));
        FiltroSeverita.Ripopola(Enum.GetValues<SeveritaAnomalia>().Select(e => (e.ToString(), e.Label())));
        FiltroPriorita.Ripopola(Enum.GetValues<PrioritaOperativa>().Select(e => (e.ToString(), e.Label())));
        FiltroStatoAnomalia.Ripopola(Enum.GetValues<StatoAnomalia>().Select(e => (e.ToString(), e.Label())));

        if (_state.CartellaValida)
        {
            var scalable = TryGetArchiveQuery();
            var coordinatore = _state.TestCases.IndexRecovery;
            if (scalable != null && coordinatore != null &&
                coordinatore.TryRead("Catalogo filtri Lista Test", () => scalable.GetFilterCatalog(), out LocalTestFilterCatalog catalogoScalabile))
            {
                ImpostaValoriFiltri(catalogoScalabile.Componente, catalogoScalabile.Veicoli, catalogoScalabile.Assegnatari,
                    catalogoScalabile.Creatori, catalogoScalabile.Requisiti, catalogoScalabile.Funzionalita);
            }
            else
            {
                var catalogo = TestCaseFilterCatalog.Build(_state.TestCases.GetAll());
                ImpostaValoriFiltri(catalogo.Componente, catalogo.Veicoli, catalogo.Assegnatari,
                    catalogo.Creatori, catalogo.Requisiti, catalogo.Funzionalita);
            }
            TemplatesDisponibili = _state.Master.TestTemplate.Load().Where(t => !t.Archiviata && !t.IsChangeRequest).ToList();
            OnPropertyChanged(nameof(TemplatesDisponibili));

            FiltroEcu.Ripopola(ConSelezionatiMancanti(FiltroEcu, EcuDisponibili));
            FiltroVeicolo.Ripopola(ConSelezionatiMancanti(FiltroVeicolo, VeicoliDisponibili));
            FiltroAssegnatario.Ripopola(ConSelezionatiMancanti(FiltroAssegnatario, AssegnatariDisponibili));
            FiltroCreatoDa.Ripopola(ConSelezionatiMancanti(FiltroCreatoDa, CreatoriDisponibili));
            FiltroRequisito.Ripopola(ConSelezionatiMancanti(FiltroRequisito, RequisitiDisponibili));
            FiltroFunzionalita.Ripopola(ConSelezionatiMancanti(FiltroFunzionalita, FunzionalitaDisponibili));
            FiltroTemplate.Ripopola(TemplatesDisponibili.Select(t => (t.Uid.ToString(), t.Nome)));
            ConciliaFiltriCampiCustom();
        }
    }

    /// <summary>Opzioni (valore = etichetta) del catalogo piu' i valori GIA' selezionati che il catalogo non
    /// contiene (es. l'operatore aggiunto dal quick-filter "Assegnati/Creati da me" senza Test): senza, il
    /// Ripopola successivo scarterebbe l'opzione e la selezione andrebbe persa (lista da vuota a tutti i Test).</summary>
    private static IEnumerable<(string, string)> ConSelezionatiMancanti(FiltroMultiSelect filtro, IEnumerable<string> catalogo)
    {
        var elenco = catalogo.ToList();
        var noti = new HashSet<string>(elenco, StringComparer.Ordinal);
        foreach (var sel in filtro.Selezionati)
            if (noti.Add(sel)) elenco.Add(sel);
        return elenco.Select(x => (x, x));
    }

    private void OnFiltroMultiSelectCambiato()
    {
        // Il pulsante "Assegnati/Creati da me" evidenzia lo stato dal contenuto della selezione.
        OnPropertyChanged(nameof(FiltroAssegnatiAMeAttivo));
        OnPropertyChanged(nameof(FiltroCreatiDaMeAttivo));
        // Un cambio del tipo di test sposta i campi di sistema pertinenti: si concilia prima della query, come in Aggiorna().
        if (_state.CartellaValida && FirmaTipiTestCampiCustom() != _firmaTipiTestCampiCustom) ConciliaFiltriCampiCustom();
        FiltroCambiato();
    }

    private static List<string>? Valori(FiltroMultiSelect f)
    {
        var sel = f.Selezionati;
        return sel.Count == 0 ? null : sel;
    }

    private static List<T>? ValoriEnum<T>(FiltroMultiSelect f) where T : struct, Enum
    {
        var elenco = new List<T>();
        foreach (var v in f.Selezionati)
            if (Enum.TryParse<T>(v, out var e)) elenco.Add(e);
        return elenco.Count == 0 ? null : elenco;
    }

    private static List<Guid>? ValoriGuid(FiltroMultiSelect f)
    {
        var elenco = new List<Guid>();
        foreach (var v in f.Selezionati)
            if (Guid.TryParse(v, out var g)) elenco.Add(g);
        return elenco.Count == 0 ? null : elenco;
    }

    /// <summary>Seleziona SOLO il valore indicato (usato dai quick-filter "Assegnati/Creati da me"). Se il valore
    /// non e' fra le opzioni del catalogo (es. un operatore che non ha ancora Test) lo aggiunge, cosi' il filtro
    /// resta applicabile invece di non fare nulla.</summary>
    private static void SelezionaSolo(FiltroMultiSelect filtro, string? valore)
    {
        if (string.IsNullOrEmpty(valore)) return;
        if (!filtro.Opzioni.Any(o => o.Valore == valore))
            filtro.Popola(filtro.Opzioni.Select(o => (o.Valore, o.Etichetta)).Append((valore, valore)));
        var altri = filtro.Opzioni.Where(o => o.Selezionato && o.Valore != valore).ToList();
        foreach (var o in altri) o.Selezionato = false;
        var voluto = filtro.Opzioni.First(o => o.Valore == valore);
        if (!voluto.Selezionato) voluto.Selezionato = true;
    }

    /// <summary>Correzione prestazioni 6.4.0 (pagina Test a freddo, 100k): sostituisce una lista di valori
    /// dei filtri e notifica la View SOLO se il contenuto è cambiato. Prima ogni visita della pagina assegnava
    /// sei liste nuove (fino a ~1000 voci ciascuna a 100k Test) anche a contenuto identico, e ComboBox e
    /// AutoCompleteBox legati a ciascuna ricaricavano l'ItemsSource e rifacevano misura/layout. A contenuto
    /// uguale (confronto ordinale, stesso ordine) resta la lista già visualizzata; a contenuto diverso il
    /// comportamento è quello di prima (nuova lista + PropertyChanged).</summary>
    private void ImpostaValoriFiltri(
        IReadOnlyList<string> componente, IReadOnlyList<string> veicoli, IReadOnlyList<string> assegnatari,
        IReadOnlyList<string> creatori, IReadOnlyList<string> requisiti, IReadOnlyList<string> funzionalita)
    {
        if (!StessiValori(EcuDisponibili, componente)) { EcuDisponibili = componente.ToList(); OnPropertyChanged(nameof(EcuDisponibili)); }
        if (!StessiValori(VeicoliDisponibili, veicoli)) { VeicoliDisponibili = veicoli.ToList(); OnPropertyChanged(nameof(VeicoliDisponibili)); }
        if (!StessiValori(AssegnatariDisponibili, assegnatari)) { AssegnatariDisponibili = assegnatari.ToList(); OnPropertyChanged(nameof(AssegnatariDisponibili)); }
        if (!StessiValori(CreatoriDisponibili, creatori)) { CreatoriDisponibili = creatori.ToList(); OnPropertyChanged(nameof(CreatoriDisponibili)); }
        if (!StessiValori(RequisitiDisponibili, requisiti)) { RequisitiDisponibili = requisiti.ToList(); OnPropertyChanged(nameof(RequisitiDisponibili)); }
        if (!StessiValori(FunzionalitaDisponibili, funzionalita)) { FunzionalitaDisponibili = funzionalita.ToList(); OnPropertyChanged(nameof(FunzionalitaDisponibili)); }
    }

    private static bool StessiValori(List<string> attuali, IReadOnlyList<string> nuovi) =>
        attuali.Count == nuovi.Count && attuali.SequenceEqual(nuovi, StringComparer.Ordinal);

    // === Filtri dei campi di sistema usabili come filtro (come Home e Report) ===================================================
    /// <summary>Concilia i filtri dei campi di sistema con il catalogo e con i tipi di test scelti. Ogni campo eleggibile ha un filtro con
    /// la sua selezione. Un campo non pertinente ai tipi scelti non compare e non filtra, ma la sua selezione resta e torna quando torna
    /// pertinente; un campo tolto dal catalogo mette la selezione in attesa. Le opzioni dei campi visibili si ricaricano qui.</summary>
    private void ConciliaFiltriCampiCustom()
    {
        if (!_state.CartellaValida) return;
        var sospeso = _sospendiApplicaFiltri;
        _sospendiApplicaFiltri = true;
        try
        {
            _firmaTipiTestCampiCustom = FirmaTipiTestCampiCustom();
            var eleggibili = CampoSistemaCatalogoService.CampiUtilizzabiliComeFiltro(_state);
            var (aggiunti, rimossi) = FiltriCampiCustomSync.Allinea(_filtriCampiCustom, eleggibili);
            foreach (var campo in rimossi)
            {
                campo.Filtro.Cambiato -= OnFiltroMultiSelectCambiato;
                if (FiltroMultiSelectSalvataggio.Serializza(campo.Filtro) is { } grezzo) _selezioniCampiCustomInAttesa[campo.Chiave] = grezzo;
            }
            foreach (var campo in aggiunti)
            {
                campo.Filtro.Cambiato += OnFiltroMultiSelectCambiato;
                if (_selezioniCampiCustomInAttesa.Remove(campo.Chiave, out var grezzo))
                    ApplicaSelezione(campo.Filtro, grezzo, conservaMancanti: true);
            }

            var pertinenti = ChiaviCampiPertinenti();
            var visibili = _filtriCampiCustom.Where(c => pertinenti == null || pertinenti.Contains(c.Chiave)).ToList();
            if (!visibili.SequenceEqual(FiltriCampiCustomLista))
            {
                FiltriCampiCustomLista.Clear();
                foreach (var campo in visibili) FiltriCampiCustomLista.Add(campo);
            }

            var scalable = TryGetArchiveQuery();
            foreach (var campo in visibili)
                campo.Filtro.Ripopola(ConSelezionatiMancanti(campo.Filtro, CampoSistemaCatalogoService.OpzioniFiltro(
                    campo.Tipo, campo.OpzioniLista, ValoriCampoCustomPresenti(campo.Chiave, scalable))));
        }
        finally { _sospendiApplicaFiltri = sospeso; }
    }

    /// <summary>Campi di sistema pertinenti ai Test del tipo scelto: quelli usati dai template selezionati. Senza tipo scelto ritorna null,
    /// e compaiono tutti i campi filtrabili, come in Home e Report.</summary>
    private IReadOnlySet<string>? ChiaviCampiPertinenti()
    {
        if (!FiltroTemplate.HaSelezione) return null;
        var scelti = new HashSet<string>(FiltroTemplate.Selezionati, StringComparer.OrdinalIgnoreCase);
        return CampoSistemaCatalogoService.ChiaviCampiSistemaDeiTemplate(_state.Master.TestTemplate.Load().Where(t => scelti.Contains(t.Uid.ToString())));
    }

    private string FirmaTipiTestCampiCustom() => string.Join(',', FiltroTemplate.Selezionati.OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>Valori presenti nei Test per un campo di sistema: dall'indice (le stesse letture della Home, in cache finche' l'indice non
    /// cambia), altrimenti dall'archivio in memoria. Le voci sono poi composte con <see cref="CampoSistemaCatalogoService.OpzioniFiltro"/>.</summary>
    private IReadOnlyList<string> ValoriCampoCustomPresenti(string chiave, LocalTestArchiveQueryService? scalable)
    {
        var coordinatore = _state.TestCases.IndexRecovery;
        if (scalable != null && coordinatore != null &&
            coordinatore.TryRead("Valori campo di sistema Lista Test", () => scalable.GetCustomFieldValues(chiave), out IReadOnlyList<string> valori))
            return valori;
        return CampoSistemaCatalogoService.ValoriDistinti(_state.TestCases.GetAll(), chiave);
    }

    /// <summary>Selezioni dei filtri dei campi di sistema visibili (pertinenti) con almeno un valore: il predicato della query. Null se nessuna.</summary>
    private Dictionary<string, IReadOnlyCollection<string>>? SelezioniCampiCustomAttive()
    {
        Dictionary<string, IReadOnlyCollection<string>>? selezioni = null;
        foreach (var campo in FiltriCampiCustomLista)
            if (campo.Filtro.HaSelezione)
                (selezioni ??= new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal))[campo.Chiave] = campo.Filtro.Selezionati;
        return selezioni;
    }

    /// <summary>Selezioni dei campi di sistema da salvare in una vista o nello snapshot: quelle dei filtri (pertinenti o no) e quelle in attesa.</summary>
    private IEnumerable<KeyValuePair<string, string>> SelezioniCampiCustomDaSalvare()
    {
        foreach (var (chiave, grezzo) in _selezioniCampiCustomInAttesa)
            yield return new KeyValuePair<string, string>(chiave, grezzo);
        foreach (var campo in _filtriCampiCustom)
            if (FiltroMultiSelectSalvataggio.Serializza(campo.Filtro) is { } grezzo)
                yield return new KeyValuePair<string, string>(campo.Chiave, grezzo);
    }

    /// <summary>Riapplica le selezioni dei campi di sistema di una vista o dello snapshot: ogni filtro prende la sua voce (vuota se assente);
    /// le voci di campi che non hanno ancora un filtro restano in attesa finche' il campo non torna eleggibile.</summary>
    private void ApplicaCampiCustomVista(IReadOnlyDictionary<string, string?> valori)
    {
        _selezioniCampiCustomInAttesa.Clear();
        foreach (var campo in _filtriCampiCustom)
            ApplicaSelezione(campo.Filtro, valori.GetValueOrDefault(PrefissoCampoSistemaVista + campo.Chiave), conservaMancanti: true);
        foreach (var (chiave, grezzo) in valori)
        {
            if (grezzo == null || !chiave.StartsWith(PrefissoCampoSistemaVista, StringComparison.Ordinal)) continue;
            var campo = chiave[PrefissoCampoSistemaVista.Length..];
            if (!_filtriCampiCustom.Any(c => string.Equals(c.Chiave, campo, StringComparison.OrdinalIgnoreCase)))
                _selezioniCampiCustomInAttesa[campo] = grezzo;
        }
    }

    private void FiltroCambiato()
    {
        if (_sospendiApplicaFiltri) return;
        // Un cambio manuale dei filtri non e' piu' "la vista selezionata": svuota il combo, cosi' riscegliere la
        // stessa vista la riapplica (SelectedItem cambia davvero).
        if (VistaSelezionata != null) VistaSelezionata = null;
        PersistiFiltriCorrenti();
        PaginaCorrente = 1;
        Applica();
    }

    /// <summary>Testo libero ESCLUSO di proposito (a differenza di CatturaFiltriCorrenti, usata invariata per
    /// le Viste Salvate con nome): scrivere su disco ad ogni carattere digitato durante una ricerca sarebbe
    /// puro spreco di I/O per un campo che l'utente svuota e riusa in continuazione, non un filtro stabile.</summary>
    private void PersistiFiltriCorrenti()
    {
        var filtri = CatturaFiltriCorrenti();
        filtri.Remove("Testo");
        // Filtro transitorio (ingresso dall'avviso Home): resta nelle Viste salvate esplicite ma NON nello snapshot
        // automatico, altrimenti riapparirebbe dopo un riavvio come filtro non voluto.
        filtri.Remove("SoloSenzaEcu");
        ViewStateService.ScriviDizionario(_state, ChiaveVistaPersistente, filtri);
    }

    /// <summary>Impostazione programmatica del testo (reset, viste, ingressi dalla Home): annulla l'eventuale
    /// debounce ancora pendente e allinea sia FiltroTesto sia il TextBox. Senza questo, un testo digitato meno di
    /// 250 ms prima del reset verrebbe riapplicato dopo il reset stesso (FiltroTesto="" non cambia se il
    /// debounce non era ancora scattato, quindi OnFiltroTestoChanged non annullerebbe nulla).</summary>
    private void ImpostaTestoImmediato(string testo)
    {
        _debounceTestoCts?.Cancel();
        FiltroTesto = testo;
        if (FiltroTestoDigitato != testo) FiltroTestoDigitato = testo;
        _debounceTestoCts?.Cancel(); // il setter di FiltroTestoDigitato non deve aver lasciato attese pendenti
    }

    /// <summary>Invio nella casella di ricerca: applica subito il testo digitato, senza attendere il ritardo di digitazione.</summary>
    [RelayCommand]
    private void ConfermaRicercaTesto() => ConfermaRicercaTestoImmediata();

    internal void ConfermaRicercaTestoImmediata() => ImpostaTestoImmediato(FiltroTestoDigitato);

    partial void OnFiltroTestoChanged(string value)
    {
        // Cambio immediato (o appena promosso dal debounce): annulla l'attesa pendente e allinea il TextBox.
        _debounceTestoCts?.Cancel();
        if (FiltroTestoDigitato != value) FiltroTestoDigitato = value;
        FiltroCambiato();
    }

    partial void OnFiltroTestoDigitatoChanged(string value)
    {
        if (value == FiltroTesto) return;
        _debounceTestoCts?.Cancel();
        var cts = _debounceTestoCts = new CancellationTokenSource();
        _ = PropagaTestoDopoDebounce(value, cts.Token);
    }

    private async Task PropagaTestoDopoDebounce(string value, CancellationToken token)
    {
        try { await Task.Delay(DebounceTesto, token); }
        catch (OperationCanceledException) { return; }
        if (!token.IsCancellationRequested) FiltroTesto = value;
    }
    partial void OnFiltroHaPolarionChanged(FiltroPolarioneOpzione value) => FiltroCambiato();
    partial void OnFiltroDataDaChanged(DateTimeOffset? value)
    {
        if (value != null) FiltroDataDaNonValida = false;
        FiltroCambiato();
    }
    partial void OnFiltroDataAChanged(DateTimeOffset? value)
    {
        if (value != null) FiltroDataANonValida = false;
        FiltroCambiato();
    }
    partial void OnFiltroSoloSenzaEcuChanged(bool value) => FiltroCambiato();
    partial void OnFiltroSoloImportatoUdtChanged(bool value) => FiltroCambiato();

    [RelayCommand]
    private void ToggleFiltriAvanzati() => MostraFiltriAvanzati = !MostraFiltriAvanzati;

    // === Pannello filtri comprimibile e riepilogo a chip (6.6.1) ==========================================
    /// <summary>Chiave ViewStateService dello stato del pannello filtri: per operatore, locale al PC come le altre viste.</summary>
    private string ChiaveFiltriVisibili => $"TestList.PannelloFiltri|{_state.Config.OperatoreAttivo}";

    /// <summary>Pannello filtri aperto (predefinito). Chiuderlo lascia alla tabella piu' righe e mostra i filtri attivi come chip;
    /// la scelta dell'utente viene ricordata per operatore.</summary>
    [ObservableProperty] private bool filtriVisibili;

    partial void OnFiltriVisibiliChanged(bool value)
    {
        ViewStateService.Scrivi(_state, ChiaveFiltriVisibili, value ? "1" : "0");
        NotificaRiepilogoFiltri();
    }

    [RelayCommand]
    private void ToggleFiltri() => FiltriVisibili = !FiltriVisibili;

    /// <summary>I chip compaiono solo a pannello chiuso e solo se c'e' almeno un filtro attivo.</summary>
    public bool MostraChipFiltri => !FiltriVisibili && HaFiltriAttivi;

    /// <summary>Testo del pulsante "Filtri": numero di filtri attivi e freccia dello stato del pannello.</summary>
    public string TestoToggleFiltri
    {
        get
        {
            var titolo = Traduci("TestListFiltriBtn");
            var freccia = FiltriVisibili ? "▴" : "▾";
            var attivi = ChipFiltriAttivi.Count;
            return attivi > 0 ? $"{titolo} ({attivi}) {freccia}" : $"{titolo} {freccia}";
        }
    }

    /// <summary>Filtri attivi come chip "Etichetta: valore". Il comando di ogni chip annulla solo quel filtro.</summary>
    public IReadOnlyList<ChipFiltroLista> ChipFiltriAttivi => CostruisciChipFiltri();

    private void NotificaRiepilogoFiltri()
    {
        OnPropertyChanged(nameof(HaFiltriAttivi));
        OnPropertyChanged(nameof(ChipFiltriAttivi));
        OnPropertyChanged(nameof(MostraChipFiltri));
        OnPropertyChanged(nameof(TestoToggleFiltri));
    }

    private static string Traduci(string chiave) => LocalizationService.Instance.Traduci(chiave);

    /// <summary>Filtri a selezione multipla con la chiave di etichetta da mostrare nel chip.</summary>
    private IEnumerable<(FiltroMultiSelect Filtro, string Chiave)> CampiChipMultiSelect => new[]
    {
        (FiltroEcu, "ColEcu"), (FiltroVeicolo, "ColVeicolo"), (FiltroTemplate, "ColTipoTest"), (FiltroEsito, "ColEsito"),
        (FiltroAssegnatario, "AssegnatoALabel"), (FiltroCreatoDa, "FiltroCreatoDa"), (FiltroStatoLavoro, "FiltroStatoLavoro"),
        (FiltroRequisito, "FiltroRequisito"), (FiltroFunzionalita, "FiltroFunzionalita"), (FiltroSeverita, "FiltroSeverita"),
        (FiltroPriorita, "FiltroPriorita"), (FiltroStatoAnomalia, "FiltroStatoAnomalia"),
    };

    private List<ChipFiltroLista> CostruisciChipFiltri()
    {
        var chip = new List<ChipFiltroLista>();
        if (!string.IsNullOrWhiteSpace(FiltroTesto))
            chip.Add(new ChipFiltroLista($"{Traduci("TestListChipTesto")}: {FiltroTesto}", new RelayCommand(() => ImpostaTestoImmediato(""))));
        foreach (var (filtro, chiave) in CampiChipMultiSelect)
            if (filtro.HaSelezione)
                chip.Add(new ChipFiltroLista($"{Traduci(chiave)}: {filtro.TestoRiepilogo}", new RelayCommand(() => filtro.DeselezionaTutto())));
        foreach (var campo in FiltriCampiCustomLista)
            if (campo.Filtro.HaSelezione)
                chip.Add(new ChipFiltroLista($"{campo.Etichetta}: {campo.Filtro.TestoRiepilogo}", new RelayCommand(() => campo.Filtro.DeselezionaTutto())));
        if (FiltroHaPolarion != FiltroPolarioneOpzione.Tutti)
            chip.Add(new ChipFiltroLista($"Ticketing: {WorkflowLabelConverter.Instance.Convert(FiltroHaPolarion, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture)}",
                new RelayCommand(() => FiltroHaPolarion = FiltroPolarioneOpzione.Tutti)));
        if (FiltroDataDa is { } da)
            chip.Add(new ChipFiltroLista($"{Traduci("FiltroDataDa")}: {da:dd/MM/yyyy}", new RelayCommand(() => FiltroDataDa = null)));
        if (FiltroDataA is { } a)
            chip.Add(new ChipFiltroLista($"{Traduci("FiltroDataA")}: {a:dd/MM/yyyy}", new RelayCommand(() => FiltroDataA = null)));
        if (FiltroSoloSenzaEcu)
            chip.Add(new ChipFiltroLista(Traduci("TestListSoloSenzaEcuChip"), new RelayCommand(() => FiltroSoloSenzaEcu = false)));
        if (FiltroSoloImportatoUdt)
            chip.Add(new ChipFiltroLista(Traduci("FiltroSoloImportatoUdtBtn"), new RelayCommand(() => FiltroSoloImportatoUdt = false)));
        return chip;
    }

    [RelayCommand]
    private void FiltraAssegnatiAMe() => ImpostaQuickFilter(FiltroAssegnatario, FiltroAssegnatiAMeAttivo);

    [RelayCommand]
    private void FiltraCreatiDaMe() => ImpostaQuickFilter(FiltroCreatoDa, FiltroCreatiDaMeAttivo);

    /// <summary>Toggle "Assegnati/Creati da me": se gia' attivo svuota la selezione, altrimenti seleziona solo
    /// l'operatore attivo. Una sola query alla fine (le modifiche intermedie sono sospese).</summary>
    private void ImpostaQuickFilter(FiltroMultiSelect filtro, bool attivo)
    {
        _sospendiApplicaFiltri = true;
        try
        {
            if (attivo) filtro.DeselezionaTutto();
            else SelezionaSolo(filtro, _state.Config.OperatoreAttivo);
        }
        finally { _sospendiApplicaFiltri = false; }
        OnFiltroMultiSelectCambiato();
    }

    [RelayCommand]
    private void ReimpostaFiltri()
    {
        _sospendiApplicaFiltri = true;
        try
        {
            ImpostaTestoImmediato("");
            foreach (var f in FiltriMultiSelect) if (f.HaSelezione) f.DeselezionaTutto();
            // Anche i campi di sistema non pertinenti al tipo scelto: un reset non lascia filtri nascosti da riattivare.
            foreach (var f in _filtriCampiCustom) if (f.Filtro.HaSelezione) f.Filtro.DeselezionaTutto();
            _selezioniCampiCustomInAttesa.Clear();
            FiltroHaPolarion = FiltroPolarioneOpzione.Tutti;
            FiltroDataDa = null;
            FiltroDataA = null;
            FiltroDataDaNonValida = false;
            FiltroDataANonValida = false;
            FiltroSoloSenzaEcu = false;
            FiltroSoloImportatoUdt = false;
            VistaSelezionata = null;
            EliminaVistaSelezionataCommand.NotifyCanExecuteChanged();
        }
        finally { _sospendiApplicaFiltri = false; }
        PersistiFiltriCorrenti();
        PaginaCorrente = 1;
        Applica();
    }

    [RelayCommand]
    private void PaginaPrecedente()
    {
        if (!PuoPaginaPrecedente) return;
        PaginaCorrente--;
        Applica();
    }

    [RelayCommand]
    private void PaginaSuccessiva()
    {
        if (!PuoPaginaSuccessiva) return;
        PaginaCorrente++;
        Applica();
    }

    [RelayCommand]
    private void PrimaPagina()
    {
        if (PaginaCorrente == 1) return;
        PaginaCorrente = 1;
        Applica();
    }

    private void Applica()
    {
        NotificaRiepilogoFiltri();
        if (!_state.CartellaValida)
        {
            TotaleRisultati = 0;
            PaginaCorrente = 1;
            Righe.ReplaceWith(Array.Empty<TestCase>());
            NotificaPaginazione();
            return;
        }

        var templatiCr = _state.Master.TestTemplate.Load().Where(t => t.IsChangeRequest).Select(t => t.Uid).ToHashSet();

        // Tutti i filtri lavorano su SQLite e idratano soltanto la pagina richiesta. Stato anomalia e Ticketing dipendono
        // dalle NC: l'insieme di Test che li soddisfa e' calcolato con la stessa associazione della pagina Anomalie e
        // passato alla query come elenco di Uid. Senza indice utilizzabile resta il percorso in memoria (legacy).
        var scalable = PercorsoIndicizzatoAttivo ? TryGetArchiveQuery() : null;
        var filtriAnomalie = scalable != null ? CalcolaFiltriAnomalieIndicizzati() : FiltriAnomalieIndicizzati.Nessuno;
        List<TestCase> visibili;
        IReadOnlyDictionary<string, IReadOnlyList<NonConformita>> anomaliePerTest;

        // Solo i filtri dei campi di sistema visibili (pertinenti al tipo scelto): quelli nascosti non filtrano.
        var selezioniCampiCustom = SelezioniCampiCustomAttive();
        var coordinatore = _state.TestCases.IndexRecovery;
        var richiesta = new LocalTestArchiveQuery
        {
            Text = FiltroTesto,
            // Liste (OR fra i valori scelti, AND fra filtri diversi). AllowedVehicles resta lo scoping per
            // Gamma dell'operatore, distinto dal filtro utente Vehicles: le due condizioni si intersecano.
            Componenti = Valori(FiltroEcu),
            Vehicles = Valori(FiltroVeicolo),
            TemplateUids = ValoriGuid(FiltroTemplate),
            Outcomes = ValoriEnum<Esito>(FiltroEsito),
            Assignees = Valori(FiltroAssegnatario),
            Creators = Valori(FiltroCreatoDa),
            WorkStates = ValoriEnum<StatoLavoroTest>(FiltroStatoLavoro),
            Requirements = Valori(FiltroRequisito),
            Functionalities = Valori(FiltroFunzionalita),
            Severities = ValoriEnum<SeveritaAnomalia>(FiltroSeverita),
            Priorities = ValoriEnum<PrioritaOperativa>(FiltroPriorita),
            CreatedFrom = FiltroDataDa,
            CreatedTo = FiltroDataA,
            AllowedVehicles = VeicoliConsentitiOperatore(),
            ExcludedTemplateUids = templatiCr,
            Origine = FiltroSoloImportatoUdt ? OrigineTest.ImportStatoUdt : null,
            CustomFieldSelections = selezioniCampiCustom,
            SoloSenzaEcu = FiltroSoloSenzaEcu,
            Ticketing = filtriAnomalie.Ticketing,
            UidConAnomaliaAperta = filtriAnomalie.UidStatoAnomalia,
            Page = PaginaCorrente,
            PageSize = PageSize
        };
        if (scalable != null && coordinatore != null &&
            coordinatore.TryRead("Pagina Lista Test", () => scalable.Query(richiesta), out LocalTestArchivePage page))
        {
            PaginaCorrente = page.Page;
            TotaleRisultati = page.TotalCount;
            visibili = page.Items.ToList();
            // Solo le NC delle righe davvero visibili (feedback utente 2026-09-17, volumi 100k): qui le
            // anomalie servono solo per annotare la pagina corrente, non per filtrare (i filtri NC/Ticketing
            // impongono il percorso legacy sopra), quindi non serve clonare l'intero archivio NC.
            anomaliePerTest = AnomaliaWorkflowService.IndicizzaPerTest(
                _state.NonConformita.CandidatePerTest(visibili), _state.TestCases.GetById, _state.TestCases.GetByUid);
        }
        else
        {
            anomaliePerTest = AnomaliaWorkflowService.IndicizzaPerTest(_state.NonConformita.GetAll(), _state.TestCases.GetById, _state.TestCases.GetByUid);
            var all = _state.FiltraTestCaseVisibiliPerOperatore(_state.TestCases.GetAll())
                .Where(tc => !templatiCr.Contains(tc.TestTemplateUid)).ToList();
            foreach (var tc in all) AggiornaProiezioneAnomalie(tc, anomaliePerTest);
            IEnumerable<TestCase> query = all;
            if (!string.IsNullOrWhiteSpace(FiltroTesto))
            {
                var q = FiltroTesto.Trim();
                query = query.Where(tc => tc.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || tc.DescrizioneBreve.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || tc.Descrizione.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || (tc.Assegnatario?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                    || tc.CreatoDa.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || tc.RequisitoId.Any(r => r.Contains(q, StringComparison.OrdinalIgnoreCase))
                    || tc.Funzionalita.Any(f => f.Contains(q, StringComparison.OrdinalIgnoreCase)));
            }
            var ecuSel = Valori(FiltroEcu);
            if (ecuSel != null) query = query.Where(tc => tc.Componente.Any(e => ecuSel.Contains(e, StringComparer.OrdinalIgnoreCase)));
            var veicoliSel = Valori(FiltroVeicolo);
            if (veicoliSel != null) query = query.Where(tc => tc.VeicoliEffettivi.Any(v => veicoliSel.Contains(v, StringComparer.OrdinalIgnoreCase)));
            var templateSel = ValoriGuid(FiltroTemplate);
            if (templateSel != null) query = query.Where(tc => templateSel.Contains(tc.TestTemplateUid));
            var esitiSel = ValoriEnum<Esito>(FiltroEsito);
            if (esitiSel != null) query = query.Where(tc => tc.StatoCorrente.HasValue && esitiSel.Contains(tc.StatoCorrente.Value));
            var assegnatariSel = Valori(FiltroAssegnatario);
            if (assegnatariSel != null) query = query.Where(tc => tc.Assegnatario != null && assegnatariSel.Contains(tc.Assegnatario));
            var creatoriSel = Valori(FiltroCreatoDa);
            if (creatoriSel != null) query = query.Where(tc => creatoriSel.Contains(tc.CreatoDa));
            if (FiltroHaPolarion != FiltroPolarioneOpzione.Tutti)
                query = query.Where(tc => tc.HaPolarionVisual == (FiltroHaPolarion == FiltroPolarioneOpzione.ConPolarion));
            var statiLavoroSel = ValoriEnum<StatoLavoroTest>(FiltroStatoLavoro);
            if (statiLavoroSel != null) query = query.Where(tc => statiLavoroSel.Contains(tc.StatoLavoro));
            var requisitiSel = Valori(FiltroRequisito);
            if (requisitiSel != null) query = query.Where(tc => tc.RequisitoId.Any(r => requisitiSel.Contains(r)));
            var funzionalitaSel = Valori(FiltroFunzionalita);
            if (funzionalitaSel != null) query = query.Where(tc => tc.Funzionalita.Any(f => funzionalitaSel.Contains(f)));
            var severitaSel = ValoriEnum<SeveritaAnomalia>(FiltroSeverita);
            if (severitaSel != null) query = query.Where(tc => tc.Severita.HasValue && severitaSel.Contains(tc.Severita.Value));
            var prioritaSel = ValoriEnum<PrioritaOperativa>(FiltroPriorita);
            if (prioritaSel != null) query = query.Where(tc => prioritaSel.Contains(tc.Priorita));
            var statiAnomaliaSel = ValoriEnum<StatoAnomalia>(FiltroStatoAnomalia);
            if (statiAnomaliaSel != null) query = query.Where(tc => tc.StatiAnomalieAperteVisual.Any(st => statiAnomaliaSel.Contains(st)));
            if (selezioniCampiCustom != null)
                query = query.Where(tc => CampoSistemaCatalogoService.CorrispondeCampiCustom(selezioniCampiCustom, tc.CampiCustom));
            if (FiltroSoloSenzaEcu) query = query.Where(tc => tc.Componente.Count == 0);
            if (FiltroSoloImportatoUdt) query = query.Where(tc => tc.Origine == OrigineTest.ImportStatoUdt);
            if (FiltroDataDa.HasValue) query = query.Where(tc => tc.DataCreazione.Date >= FiltroDataDa.Value.Date);
            if (FiltroDataA.HasValue) query = query.Where(tc => tc.DataCreazione.Date <= FiltroDataA.Value.Date);

            // Stesso ordine totale di SQLite (ultima_modifica DESC, id NOCASE, uid): pagine identiche nei due percorsi.
            var ordered = query.OrderByDescending(tc => tc.UltimaModifica)
                .ThenBy(tc => tc.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(tc => tc.Uid.ToString("D"), StringComparer.Ordinal).ToList();
            TotaleRisultati = ordered.LongCount();
            var pages = Math.Max(1L, checked((TotaleRisultati + PageSize - 1L) / PageSize));
            PaginaCorrente = Math.Min(Math.Max(1L, PaginaCorrente), pages);
            // Questo cast appartiene esclusivamente al fallback che ha già materializzato una List<T>, quindi
            // l'offset è per definizione <= ordered.Count e non introduce alcun limite nel percorso scalabile.
            var legacyOffset = checked((int)((PaginaCorrente - 1L) * PageSize));
            visibili = ordered.Skip(legacyOffset).Take(PageSize).ToList();
        }

        foreach (var tc in visibili) AggiornaProiezioneAnomalie(tc, anomaliePerTest);
        Righe.ReplaceWith(visibili);
        OnPropertyChanged(nameof(RigheVuote));
        NotificaPaginazione();
    }

    /// <summary>Filtri NC della Lista Test tradotti in insiemi di Uid per la query indicizzata. Null in un campo = filtro non attivo.</summary>
    private sealed record FiltriAnomalieIndicizzati(IReadOnlyCollection<Guid>? UidStatoAnomalia, LocalTestPolarionFiltro? Ticketing)
    {
        public static readonly FiltriAnomalieIndicizzati Nessuno = new(null, null);
    }

    /// <summary>Stato anomalia e Ticketing dipendono dalle NC, non da test_summary. Un Test soddisfa il filtro se almeno una
    /// sua NC, associata con la regola della pagina Anomalie (AnomaliaWorkflowService.IndicizzaPerTest), rispetta il
    /// predicato. Si leggono in sola lettura (PeekAllReadOnly) solo le NC che rispettano il predicato, senza copiare
    /// l'archivio. Il Ticketing dei ticket collegati direttamente al Test e' gia' in test_summary.ticket_collegati.</summary>
    private FiltriAnomalieIndicizzati CalcolaFiltriAnomalieIndicizzati()
    {
        IReadOnlyCollection<Guid>? uidStato = null;
        if (FiltroStatoAnomalia.HaSelezione)
        {
            var stati = ValoriEnum<StatoAnomalia>(FiltroStatoAnomalia) ?? new List<StatoAnomalia>();
            uidStato = UidTestConNcCheSoddisfa(nc => nc.Stato == StatoNonConformita.Aperta && stati.Contains(nc.StatoWorkflow));
        }
        LocalTestPolarionFiltro? ticketing = null;
        if (FiltroHaPolarion != FiltroPolarioneOpzione.Tutti)
            ticketing = new LocalTestPolarionFiltro(FiltroHaPolarion == FiltroPolarioneOpzione.ConPolarion,
                UidTestConNcCheSoddisfa(nc => nc.CollegamentiPolarion.Count > 0));
        return new FiltriAnomalieIndicizzati(uidStato, ticketing);
    }

    private IReadOnlyCollection<Guid> UidTestConNcCheSoddisfa(Func<NonConformita, bool> predicato)
    {
        var ncScelte = _state.NonConformita.PeekAllReadOnly().Where(predicato).ToList();
        if (ncScelte.Count == 0) return Array.Empty<Guid>();
        var perTest = AnomaliaWorkflowService.IndicizzaPerTest(ncScelte, _state.TestCases.GetById, _state.TestCases.GetByUid);
        var uids = new List<Guid>(perTest.Count);
        foreach (var testId in perTest.Keys)
        {
            var test = _state.TestCases.GetById(testId);
            if (test != null) uids.Add(test.Uid);
        }
        return uids;
    }

    private void AggiornaProiezioneAnomalie(TestCase tc, IReadOnlyDictionary<string, IReadOnlyList<NonConformita>> anomaliePerTest)
    {
        var collegate = anomaliePerTest.GetValueOrDefault(tc.Id) ?? Array.Empty<NonConformita>();
        tc.StatiAnomalieAperteVisual = collegate.Where(n => n.Stato == StatoNonConformita.Aperta).Select(n => n.StatoWorkflow).ToList();
        tc.AnomalieChiuseVisual = collegate.Count(n => n.Stato == StatoNonConformita.Chiusa);
        tc.HaPolarionVisual = tc.TicketCollegati.Count > 0 || collegate.Any(n => n.CollegamentiPolarion.Count > 0);
    }

    private LocalTestArchiveQueryService? TryGetArchiveQuery()
    {
        if (_archiveQuery != null && _archiveQuery.Generazione == _state.TestCases.LocalIndexGeneration &&
            _state.TestCases.HasPersistentSnapshot) return _archiveQuery;
        if (!_state.TestCases.HasPersistentSnapshot || string.IsNullOrWhiteSpace(_state.TestCases.LocalIndexDatabasePath)) return null;
        try
        {
            _archiveQuery = new LocalTestArchiveQueryService(_state.TestCases.LocalIndexDatabasePath!, _state.TestCases.IndexRecovery);
            return _archiveQuery;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Query scalabile Lista Test", ex);
            return null;
        }
    }

    private IReadOnlyCollection<string>? VeicoliConsentitiOperatore()
    {
        var op = _state.OperatoreCorrente;
        // RuoloEffettivo (piano 6.4.2, §4b): mai il Ruolo grezzo per decidere lo scoping per Gamma.
        if (op == null || op.RuoloEffettivo != RuoloOperatore.Standard || op.GammeAssegnate.Count == 0) return null;
        var gamme = op.GammeAssegnate.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _state.Master.Veicoli.Load().Where(v => gamme.Contains(v.GammaNome)).Select(v => v.Nome).ToList();
    }

    private void NotificaPaginazione()
    {
        OnPropertyChanged(nameof(PagineTotali));
        OnPropertyChanged(nameof(PuoPaginaPrecedente));
        OnPropertyChanged(nameof(PuoPaginaSuccessiva));
        OnPropertyChanged(nameof(TestoPaginazione));
    }

    [RelayCommand]
    private void Apri(TestCase? tc)
    {
        if (tc != null) _apriTestCase(tc.Id);
    }

    [RelayCommand]
    private void Nuovo() => _apriTestCase(null);

    bool IScorciatoiePagina.NuovoDaTastiera()
    {
        if (!PuoCreare) return false;
        NuovoCommand.Execute(null);
        return true;
    }

    public ObservableCollection<TestCase> Selezionati { get; } = new();
    public bool HaSelezione => Selezionati.Count > 0;
    public event Action? RichiestaAzzeramentoSelezioneGriglia;

    private void AzzeraSelezione()
    {
        Selezionati.Clear();
        RichiestaAzzeramentoSelezioneGriglia?.Invoke();
    }

    public bool PuoAssegnareInMassa => _state.EMaster;
    public string TestoSelezione => Selezionati.Count == 1
        ? string.Format(LocalizationService.Instance.Traduci("SelezioneUnTestFmt"), Selezionati.Count)
        : string.Format(LocalizationService.Instance.Traduci("SelezionePiuTestFmt"), Selezionati.Count);

    /// <summary>Con UN solo test selezionato che ha anomalie collegate (aperte o chiuse) compare "Verifica anomalie".</summary>
    public bool PuoVerificareAnomalie =>
        Selezionati.Count == 1 && (Selezionati[0].StatiAnomalieAperteVisual.Count > 0 || Selezionati[0].AnomalieChiuseVisual > 0);

    /// <summary>Elenco delle anomalie del test selezionato (null se la selezione non e un singolo test).</summary>
    public AnomalieTestElencoViewModel? PreparaVerificaAnomalie() =>
        Selezionati.Count == 1 ? new AnomalieTestElencoViewModel(_state, Selezionati[0].Id) : null;

    public void NotificaSelezioneCambiata()
    {
        OnPropertyChanged(nameof(PuoVerificareAnomalie));
        OnPropertyChanged(nameof(HaSelezione));
        OnPropertyChanged(nameof(TestoSelezione));
        AssegnaSelezionatiCommand.NotifyCanExecuteChanged();
        CambiaStatoSelezionatiCommand.NotifyCanExecuteChanged();
        ArchiviaSelezionatiCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void DeselezionaTutti() => AzzeraSelezione();

    public static IReadOnlyList<StatoLavoroTest> StatiSelezionabiliInMassa { get; } =
        new[] { StatoLavoroTest.Pianificato, StatoLavoroTest.InCorso, StatoLavoroTest.DaRivedere, StatoLavoroTest.InAttesa, StatoLavoroTest.DaRitestare };

    /// <summary>Vero mentre un'azione di massa lavora in background (P4): blocca un secondo avvio concorrente e disabilita
    /// i comandi di massa. Lock, salvataggi e spostamenti nel cestino non girano più sul thread della UI.</summary>
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private List<RilascioLockMassaInAttesa> rilasciLockMassaInAttesa = new();
    [ObservableProperty] private string? messaggioRilascioLockMassa;
    internal Func<LockService, string, string, EsitoIo>? RilascioLockPerTest { get; set; }
    internal Func<TimeSpan, Task>? AttesaRetryLockPerTest { get; set; }

    public bool MostraRilascioLockMassaInAttesa => RilasciLockMassaInAttesa.Count > 0;
    public bool PuoRiprovaRilascioLockMassa => MostraRilascioLockMassaInAttesa && !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        NotificaSelezioneCambiata();
        OnPropertyChanged(nameof(PuoRiprovaRilascioLockMassa));
    }

    partial void OnRilasciLockMassaInAttesaChanged(List<RilascioLockMassaInAttesa> value)
    {
        OnPropertyChanged(nameof(MostraRilascioLockMassaInAttesa));
        OnPropertyChanged(nameof(PuoRiprovaRilascioLockMassa));
    }

    private async Task RiprovaRilascioLockMassa()
    {
        if (!PuoRiprovaRilascioLockMassa) return;
        IsBusy = true;
        try
        {
            var inAttesa = RilasciLockMassaInAttesa;
            var rimanenti = await Task.Run(async () =>
            {
                var falliti = new List<RilascioLockMassaInAttesa>();
                foreach (var rilascio in inAttesa)
                {
                    var esito = await LockGate.RilasciaSessioneEsattaConRetryAsync(
                        _state.Locks, rilascio.Percorso, rilascio.SessionId,
                        RilascioLockPerTest, AttesaRetryLockPerTest).ConfigureAwait(false);
                    if (esito != EsitoIo.Ok) falliti.Add(rilascio);
                }
                return falliti;
            });
            RilasciLockMassaInAttesa = rimanenti;
            MessaggioRilascioLockMassa = rimanenti.Count == 0
                ? null
                : CreaMessaggioRilascioLockMassa(rimanenti);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task RiprovaRilasciLockMassa() => RiprovaRilascioLockMassa();

    private bool PuoEseguireAzioneMassa() => HaSelezione && _state.PuoScrivere && !IsBusy;

    [RelayCommand(CanExecute = nameof(PuoEseguireAzioneMassa))]
    private Task AssegnaSelezionati() =>
        EseguiComandoConErroreAsync(AssegnaSelezionatiCoreAsync, "Assegnazione delle voci selezionate");

    private async Task AssegnaSelezionatiCoreAsync()
    {
        if (!PuoAssegnareInMassa || Selezionati.Count == 0) return;
        var voci = _state.Master.Operatori.Load().Where(o => !o.Disattivo)
            .Select(o => new VoceSelezionabile { Valore = o.Nome, TestoPrincipale = o.Nome, TestoRicerca = o.Nome }).ToList();
        var scelto = await SelezionaDialogService.Scegli(LocalizationService.Instance.Traduci("AssegnaSelezionatiTitolo"), voci);
        if (scelto == null) return;
        var nuovoAssegnatario = (string)scelto.Valore;

        var nota = await TextEditService.Modifica(LocalizationService.Instance.Traduci("NotaPianificazioneLabel"), "");
        if (nota == null || string.IsNullOrWhiteSpace(nota))
        {
            await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("ImpossibileSalvareTitolo"),
                LocalizationService.Instance.Traduci("ValNotaPianificazioneObbl"), LocalizationService.Instance.Traduci("OkBtn"), "");
            return;
        }

        await EseguiAzioneMassaSuSelezione(tc =>
        {
            tc.Assegnatario = nuovoAssegnatario;
            tc.DataAssegnazione = DateTime.Now;
            tc.NotePianificazione.Add(new NotaPianificazione
            {
                Data = DateTime.Now, Operatore = _state.Config.OperatoreAttivo,
                CampiModificati = "assegnatario", Nota = nota.Trim()
            });
            return null;
        });
    }

    [RelayCommand(CanExecute = nameof(PuoEseguireAzioneMassa))]
    private Task CambiaStatoSelezionati(StatoLavoroTest nuovoStato) =>
        EseguiComandoConErroreAsync(() => CambiaStatoSelezionatiCoreAsync(nuovoStato), "Cambio di stato delle voci selezionate");

    private async Task CambiaStatoSelezionatiCoreAsync(StatoLavoroTest nuovoStato)
    {
        if (Selezionati.Count == 0) return;
        await EseguiAzioneMassaSuSelezione(tc =>
        {
            var segnalazionePendente = _state.Segnalazioni.GetAll().Any(s => AnomaliaWorkflowService.SegnalazioneAppartieneAlTest(tc, s) && s.Stato == StatoSegnalazione.DaClassificare);
            if (segnalazionePendente) return LocalizationService.Instance.Traduci("ValClassificaPrimaConclusione");
            tc.StatoLavoro = nuovoStato;
            return null;
        });
    }

    [RelayCommand(CanExecute = nameof(PuoEseguireAzioneMassa))]
    private Task ArchiviaSelezionati() =>
        EseguiComandoConErroreAsync(ArchiviaSelezionatiCoreAsync, "Archiviazione delle voci selezionate");

    private async Task ArchiviaSelezionatiCoreAsync()
    {
        if (Selezionati.Count == 0) return;
        if (!await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("ArchiviaSelezionatiTitoloConferma"),
            string.Format(LocalizationService.Instance.Traduci("ArchiviaSelezionatiBodyFmt"), Selezionati.Count),
            LocalizationService.Instance.Traduci("Archivia"), LocalizationService.Instance.Traduci("Annulla"))) return;

        await EseguiAzioneMassaSuSelezione(tc => { _state.TestCases.SoftDelete(tc, _state.Config.OperatoreAttivo); return "@@softdelete@@"; }, giaSalvato: true);
    }

    /// <summary>Esegue l'azione su tutti i Test selezionati: il lavoro (lock, modifica, salvataggio, cestino) gira in
    /// background mentre la UI resta reattiva e i comandi di massa sono disabilitati; poi UI aggiornata e riepilogo.</summary>
    private async Task EseguiAzioneMassaSuSelezione(Func<TestCase, string?> mutazione, bool giaSalvato = false)
    {
        if (IsBusy) return;
        // Snapshot e operatore letti sul thread UI: il lavoro in background non tocca proprietà bindate.
        var target = Selezionati.ToList();
        var operatore = _state.Config.OperatoreAttivo;

        (int Riusciti, List<(string Id, string Motivo)> Saltati, List<RilascioLockMassaInAttesa> RilasciFalliti) esito;
        IsBusy = true;
        try
        {
            esito = await Task.Run(() => EseguiAzioneMassaInBackgroundAsync(target, mutazione, giaSalvato, operatore));
        }
        finally
        {
            IsBusy = false;
        }

        _state.NotificaDatiRicaricati();
        AzzeraSelezione();
        Aggiorna();
        if (esito.RilasciFalliti.Count > 0)
        {
            RilasciLockMassaInAttesa = RilasciLockMassaInAttesa
                .Concat(esito.RilasciFalliti)
                .DistinctBy(r => (r.Percorso, r.SessionId))
                .ToList();
            MessaggioRilascioLockMassa = CreaMessaggioRilascioLockMassa(RilasciLockMassaInAttesa);
        }

        var riepilogo = esito.Saltati.Count == 0
            ? string.Format(LocalizationService.Instance.Traduci("AzioneMassaRiepilogoOkFmt"), esito.Riusciti)
            : string.Format(LocalizationService.Instance.Traduci("AzioneMassaRiepilogoParzialeFmt"), esito.Riusciti, esito.Saltati.Count,
                string.Join("\n", esito.Saltati.Select(s => $"{s.Id}: {s.Motivo}")));
        if (esito.RilasciFalliti.Count > 0)
            riepilogo += $"\n\n{LocalizationService.Instance.Traduci("SalvatoSbloccoLockNonRiuscito")} ({string.Join(", ", esito.RilasciFalliti.Select(r => r.TestId))})";
        await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("AzioneMassaRiepilogoTitolo"), riepilogo, LocalizationService.Instance.Traduci("OkBtn"), "");
    }

    private static string CreaMessaggioRilascioLockMassa(IReadOnlyCollection<RilascioLockMassaInAttesa> rilasci) =>
        $"{LocalizationService.Instance.Traduci("SalvatoSbloccoLockNonRiuscito")} ({string.Join(", ", rilasci.Select(r => r.TestId))})";

    /// <summary>Lavora un Test alla volta sulle COPIE di lavoro (CopiaPerModifica): la cache condivisa cambia solo quando il
    /// salvataggio riesce. Su conflitto o errore il Test viene riletto dal disco, così la cache riflette ciò che è salvato
    /// davvero e nessuna modifica non salvata resta visibile alle altre letture (P1.3).</summary>
    private async Task<(int Riusciti, List<(string Id, string Motivo)> Saltati, List<RilascioLockMassaInAttesa> RilasciFalliti)> EseguiAzioneMassaInBackgroundAsync(
        IReadOnlyList<TestCase> target, Func<TestCase, string?> mutazione, bool giaSalvato, string operatore)
    {
        var riusciti = 0;
        var saltati = new List<(string Id, string Motivo)>();
        var rilasciFalliti = new List<RilascioLockMassaInAttesa>();

        foreach (var originale in target)
        {
            var revisioneAttesa = originale.RevisioneDati;
            var path = _state.TestCases.PercorsoPer(originale);
            var lockResult = await LockGate.EseguiAsync(
                path,
                () => Task.Run(() => _state.Locks.TryAcquireNuovaSessione(path, operatore))).ConfigureAwait(false);
            if (!lockResult.Acquisito)
            {
                saltati.Add((originale.Id, string.Format(LocalizationService.Instance.Traduci("AzioneMassaSaltatoLockFmt"), lockResult.DetenutoDa?.Operatore ?? "?")));
                continue;
            }
            try
            {
                var copia = _state.TestCases.CopiaPerModifica(originale);
                var esito = mutazione(copia);
                if (esito == "@@softdelete@@") { riusciti++; continue; }
                if (esito != null) { saltati.Add((originale.Id, esito)); continue; }
                if (!giaSalvato) _state.TestCases.Save(copia, isNew: false, revisioneAttesa);
                riusciti++;
            }
            catch (ConflittoRevisioneException)
            {
                saltati.Add((originale.Id, LocalizationService.Instance.Traduci("AzioneMassaSaltatoConflittoFmt")));
                RileggiDaDisco(path);
            }
            catch (Exception ex)
            {
                saltati.Add((originale.Id, ex.Message));
                RileggiDaDisco(path);
            }
            finally
            {
                var sessione = lockResult.DetenutoDa?.SessionId;
                if (!string.IsNullOrWhiteSpace(sessione))
                {
                    var esitoRilascio = await LockGate.RilasciaSessioneEsattaConRetryAsync(
                        _state.Locks, path, sessione, RilascioLockPerTest, AttesaRetryLockPerTest)
                        .ConfigureAwait(false);
                    if (esitoRilascio != EsitoIo.Ok)
                    {
                        rilasciFalliti.Add(new RilascioLockMassaInAttesa(originale.Id, path, sessione));
                        CrashLogService.Registra(
                            "Rilascio lock dopo azione di massa sui Test fallito",
                            new IOException($"Rilascio sessione {sessione} fallito."));
                    }
                }
            }
        }

        return (riusciti, saltati, rilasciFalliti);
    }

    /// <summary>Riallinea la cache di un Test al contenuto del disco dopo un salvataggio o uno spostamento fallito.</summary>
    private void RileggiDaDisco(string path)
    {
        try { _state.TestCases.RefreshPaths(new[] { path }); }
        catch (Exception ex) { CrashLogService.Registra("Azione di massa: rilettura del Test dopo un errore", ex); }
    }
}
