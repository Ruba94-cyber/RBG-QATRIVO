using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

public class WidgetRiga
{
    public string Etichetta { get; init; } = "";
    public long Valore { get; init; }
    public string Sottotitolo { get; init; } = "";
}

/// <summary>Voce del pannello "scegli grafici" (Addendum §18): permette di avere più grafici visibili
/// simultaneamente invece di uno statico fisso, spuntando/togliendo dalla lista.</summary>
public class ChartOption : ViewModelBase
{
    public string Id { get; init; } = "";
    public string Titolo { get; init; } = "";
    private bool _selezionato;
    public bool Selezionato { get => _selezionato; set => SetProperty(ref _selezionato, value); }

    /// <summary>Tipi di rappresentazione compatibili col dato di questo grafico (selettore Home, piano 6.5.0
    /// punto 5); con un solo tipo il selettore non viene mostrato.</summary>
    public IReadOnlyList<TipoGraficoVoce> TipiDisponibili { get; init; } = Array.Empty<TipoGraficoVoce>();
    private TipoGraficoVoce? _tipoSelezionato;
    public TipoGraficoVoce? TipoSelezionato { get => _tipoSelezionato; set => SetProperty(ref _tipoSelezionato, value); }
    public TipoGrafico Tipo => TipoSelezionato?.Tipo ?? HomeChartCatalog.Predefinito(Id);
    public bool HaSelettoreTipo => TipiDisponibili.Count > 1;
    public string AutomationIdCheckBox => $"Home_Grafico_{Id}_CheckBox";
    public string AutomationIdSelettoreTipo => $"Home_GraficoTipo_{Id}_ComboBox";
}

/// <summary>Voce del selettore tipo grafico; ToString = etichetta, cosi' la ComboBox espone un nome UIA stabile.</summary>
public sealed class TipoGraficoVoce
{
    public TipoGraficoVoce(TipoGrafico tipo, string etichetta) { Tipo = tipo; Etichetta = etichetta; }
    public TipoGrafico Tipo { get; }
    public string Etichetta { get; }
    public override string ToString() => Etichetta;
}

/// <summary>Singola voce spuntabile di un filtro Home a selezione multipla (Addendum §9, feedback QA: prima
/// ogni filtro accettava un solo valore, e una volta scelto non c'era modo di tornare a "tutti" se non col
/// reset globale).</summary>
public class FiltroOpzione : ViewModelBase
{
    public string Valore { get; init; } = "";
    private string _etichetta = "";
    public string Etichetta
    {
        get => _etichetta;
        set => SetProperty(ref _etichetta, value);
    }
    private bool _selezionato;
    public bool Selezionato { get => _selezionato; set => SetProperty(ref _selezionato, value); }
}

/// <summary>Un filtro Home a selezione multipla: nessuna voce spuntata = "tutti" (stesso significato del
/// vecchio valore null), coerente col comportamento precedente. "Seleziona tutto"/"Deseleziona tutto" evitano
/// di dover spuntare una per una decine di voci solo per tornare allo stato di partenza.</summary>
public class FiltroMultiSelect : ViewModelBase
{
    public BulkObservableCollection<FiltroOpzione> Opzioni { get; } = new();
    public List<string> Selezionati => Opzioni.Where(o => o.Selezionato).Select(o => o.Valore).ToList();
    public bool HaSelezione => Opzioni.Any(o => o.Selezionato);
    public bool TutteSelezionate => Opzioni.Count > 0 && Opzioni.All(o => o.Selezionato);
    public string TestoRiepilogo
    {
        get
        {
            var n = Selezionati.Count;
            if (n == 0) return LocalizationService.Instance.Traduci("FiltroTutteLeVoci");
            if (n == 1) return Opzioni.First(o => o.Selezionato).Etichetta;
            return string.Format(LocalizationService.Instance.Traduci("FiltroNVociSelezionateFmt"), n);
        }
    }

    public event Action? Cambiato;

    private bool _sospendiNotifiche;
    private AppState? _statoPersistenza;
    private string? _chiavePersistenza;

    /// <summary>Persistenza automatica della selezione fra un riavvio dell'app e l'altro (feedback utente
    /// 2026-09-18: "tutte quante le viste devono mantenere filtri... non solo alcune"). Chiamare UNA sola
    /// volta in costruzione, DOPO che Ripopola/Popola ha gia' riempito Opzioni con l'elenco reale — riapplica
    /// qui la selezione persistita esattamente come Ripopola riapplica una selezione gia' in memoria, e da
    /// questo momento ogni cambiamento (click, Seleziona/Deseleziona tutto, un Ripopola successivo) viene
    /// scritto in ViewStateService. NON richiamare ad ogni Ripopola: sovrascriverebbe con il valore vecchio
    /// una selezione che l'utente ha appena cambiato in questa sessione.</summary>
    public void LegaPersistenza(AppState state, string chiave)
    {
        _statoPersistenza = state;
        _chiavePersistenza = chiave;
        var salvati = ViewStateService.LeggiLista(state, chiave);
        if (salvati.Count == 0) return;
        _sospendiNotifiche = true;
        try { foreach (var o in Opzioni.Where(o => salvati.Contains(o.Valore))) o.Selezionato = true; }
        finally { _sospendiNotifiche = false; }
        NotificaCambiati();
    }

    /// <summary>Primo giro del Report: imposta la selezione gia' calcolata dal giro (persistita intersecata con le opzioni, o i
    /// default) e collega la persistenza. Con una selezione salvata scrive lo stato e notifica come <see cref="LegaPersistenza"/>;
    /// senza, imposta le voci senza scrivere, come prima.</summary>
    public void CollegaSelezioneIniziale(AppState state, string chiave, IReadOnlyList<string> salvati, IReadOnlyCollection<string> selezionati)
    {
        if (salvati.Count == 0)
        {
            foreach (var o in Opzioni)
            {
                var voluto = selezionati.Contains(o.Valore);
                if (o.Selezionato != voluto) o.Selezionato = voluto;
            }
            _statoPersistenza = state;
            _chiavePersistenza = chiave;
            return;
        }
        _statoPersistenza = state;
        _chiavePersistenza = chiave;
        _sospendiNotifiche = true;
        try
        {
            foreach (var o in Opzioni)
            {
                var voluto = selezionati.Contains(o.Valore);
                if (o.Selezionato != voluto) o.Selezionato = voluto;
            }
        }
        finally { _sospendiNotifiche = false; }
        NotificaCambiati();
    }

    public void Popola(IEnumerable<(string Valore, string Etichetta)> valori)
    {
        foreach (var o in Opzioni) o.PropertyChanged -= OnOpzioneCambiata;
        // Una sola notifica (Reset) invece di Clear + una Add per opzione: con mille voci ogni Add notificava la lista.
        var nuove = new List<FiltroOpzione>();
        foreach (var (valore, etichetta) in valori)
        {
            var opt = new FiltroOpzione { Valore = valore, Etichetta = etichetta };
            opt.PropertyChanged += OnOpzioneCambiata;
            nuove.Add(opt);
        }
        Opzioni.ReplaceWith(nuove);
    }

    public void Ripopola(IEnumerable<(string Valore, string Etichetta)> valori)
    {
        // A 100k+ Test, sette filtri Home con ~1000 opzioni ciascuno rifacevano Clear+Add (e via gli handler
        // PropertyChanged di ogni FiltroOpzione) a OGNI rivisita, anche quando i valori letti erano identici a
        // quelli gia' mostrati (Addendum prestazioni 2026-09-25). Confronto sul CONTENUTO delle liste appena
        // lette (mai su data/dimensione file, che qui non esistono nemmeno): se identico, nessuna mutazione
        // delle Opzioni, quindi selezione e ricerca sulle voci esistenti restano intatte senza bisogno di
        // riapplicarle.
        var lista = valori as IReadOnlyList<(string Valore, string Etichetta)> ?? valori.ToList();
        if (ContenutoInvariato(lista)) return;

        var selezionatiPrima = new HashSet<string>(Selezionati, StringComparer.OrdinalIgnoreCase);
        Popola(lista);
        if (selezionatiPrima.Count == 0) return;
        _sospendiNotifiche = true;
        try { foreach (var o in Opzioni.Where(o => selezionatiPrima.Contains(o.Valore))) o.Selezionato = true; }
        finally { _sospendiNotifiche = false; }
        NotificaCambiati();
    }

    private bool ContenutoInvariato(IReadOnlyList<(string Valore, string Etichetta)> nuovi)
    {
        if (nuovi.Count != Opzioni.Count) return false;
        for (var i = 0; i < nuovi.Count; i++)
        {
            if (!string.Equals(Opzioni[i].Valore, nuovi[i].Valore, StringComparison.Ordinal)) return false;
            if (!string.Equals(Opzioni[i].Etichetta, nuovi[i].Etichetta, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private void OnOpzioneCambiata(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FiltroOpzione.Selezionato) || _sospendiNotifiche) return;
        NotificaCambiati();
    }

    private void NotificaCambiati()
    {
        OnPropertyChanged(nameof(Selezionati));
        OnPropertyChanged(nameof(HaSelezione));
        OnPropertyChanged(nameof(TutteSelezionate));
        OnPropertyChanged(nameof(TestoRiepilogo));
        if (_statoPersistenza != null && _chiavePersistenza != null)
            ViewStateService.ScriviLista(_statoPersistenza, _chiavePersistenza, Selezionati);
        Cambiato?.Invoke();
    }

    public void SelezionaTutto()
    {
        _sospendiNotifiche = true;
        try { foreach (var o in Opzioni) o.Selezionato = true; }
        finally { _sospendiNotifiche = false; }
        NotificaCambiati();
    }

    public void DeselezionaTutto()
    {
        _sospendiNotifiche = true;
        try { foreach (var o in Opzioni) o.Selezionato = false; }
        finally { _sospendiNotifiche = false; }
        NotificaCambiati();
    }
}

/// <summary>Filtro di un campo di sistema con UsabileComeFiltro (Home e Report). La Chiave e' stabile; l'Etichetta segue il catalogo.
/// <see cref="PersistenzaCollegata"/> dice se la selezione salvata e' gia' stata collegata a questa istanza (una sola volta).</summary>
public class FiltroCampoCustom : ViewModelBase
{
    public string Chiave { get; init; } = "";
    private string _etichetta = "";
    public string Etichetta { get => _etichetta; set => SetProperty(ref _etichetta, value); }
    /// <summary>Tipo e opzioni del campo nel catalogo, allineati da <see cref="FiltriCampiCustomSync"/> a ogni giro: un campo Lista offre
    /// le sue opzioni anche quando nessun Test le usa ancora (vedi <see cref="CampoSistemaCatalogoService.OpzioniFiltro"/>).</summary>
    public TipoCampoStrumento Tipo { get; set; } = TipoCampoStrumento.Testo;
    public List<string> OpzioniLista { get; set; } = new();
    public FiltroMultiSelect Filtro { get; } = new();
    internal bool PersistenzaCollegata { get; set; }
}

public partial class HomeViewModel : ViewModelBase, IPaginaAggiornabile, IScorciatoiePagina
{
    private readonly AppState _state;
    private readonly Action<string?> _apriTestCase;
    private readonly DateTimeOffset _defaultDataDa;
    private readonly DateTimeOffset _defaultDataA;
    private Dictionary<string, List<string>> _categoriaPerFunzionalita = new();

    [ObservableProperty] private bool soloMiei;
    [ObservableProperty] private string testoRicerca = "";

    public long TotaleTestCase { get; private set; }
    public long TotaleNcAperte { get; private set; }

    /// <summary>Change request non archiviate e non chiuse (Stato diverso da Chiusa/Annullata): KPI della Home,
    /// cliccabile verso la pagina Anomalie / CR.</summary>
    public long ChangeRequestAperte { get; private set; }
    public long DaClassificare { get; private set; }
    public long DaRitestare { get; private set; }
    public long MieiAttivi { get; private set; }
    public double CoperturaRequisitiPercentuale { get; private set; }
    public double TassoConformitaPercentuale { get; private set; }
    [ObservableProperty] private int modalitaPercentualeConformita;
    private StatiMatrice _statiConformitaHome;
    public string TestoTassoConformita => _statiConformitaHome.Totale == 0
        ? "—"
        : $"{_statiConformitaHome.TestoPercentuali} {string.Format(LocalizationService.Instance.Traduci(
            ModalitaPercentualeConformita == 1 ? "HomePercentualiTestCountFmt" : "HomePercentualiStatoCountFmt"), _statiConformitaHome.Totale)}";

    partial void OnModalitaPercentualeConformitaChanged(int value)
    {
        ViewStateService.Scrivi(_state, "Home.ModalitaPercentualeConformita", value == 1 ? "test" : "stato");
        if (_aggiornaInBackground)
        {
            _testataDaCalcolare = true;
            _versioneTestata++;
            _ = AvviaGiro(recenti: false);
            return;
        }
        AggiornaKpiTestata(testata: true, copertura: false);
    }

    private void ImpostaDistribuzioneConformita(StatiMatrice stati)
    {
        _statiConformitaHome = stati;
        TassoConformitaPercentuale = stati.Totale == 0 ? 0 : 100d * stati.Ok / stati.Totale;
        OnPropertyChanged(nameof(TassoConformitaPercentuale));
        OnPropertyChanged(nameof(TestoTassoConformita));
    }

    /// <summary>Piano 6.4.0 punto 5(3): quanti Test non hanno nessuna componente associata. Prima di questo
    /// cambiamento l'assenza di componente su un Test produceva solo righe/colonne vuote o etichettate "—" altrove
    /// (es. matrice Report), senza spiegazione (feedback utente: "non si sa a cosa serva e cosa sia"). Qui
    /// invece è un conteggio esplicito e cliccabile verso l'elenco filtrato dei Test da correggere.</summary>
    public long TestSenzaEcu { get; private set; }
    public bool MostraAvvisoTestSenzaEcu => TestSenzaEcu > 0;
    public string TestSenzaEcuTesto => string.Format(LocalizationService.Instance.Traduci("TestSenzaEcuAvvisoFmt"), TestSenzaEcu);

    private long ContaChangeRequestAperte() => _state.CartellaValida ? _state.ChangeRequests.CountAperte() : 0;

    /// <summary>KPI della testata: <paramref name="testata"/> = conteggi (Riepilogo, Avvisi, KPI), <paramref name="copertura"/> =
    /// copertura requisiti (solo KPI). Le parti non richieste restano invariate: sono da calcolare alla loro visualizzazione.</summary>
    private void AggiornaKpiTestata(bool testata, bool copertura)
    {
        if (!_state.CartellaValida)
        {
            TotaleTestCase = TotaleNcAperte = DaClassificare = DaRitestare = MieiAttivi = TestSenzaEcu = 0;
            ChangeRequestAperte = 0;
            CoperturaRequisitiPercentuale = TassoConformitaPercentuale = 0;
            _statiConformitaHome = default;
        }
        else if (TryAggiornaKpiTestataScalabile(testata, copertura))
        {
            return;
        }
        else
        {
            var tutti = _state.TestCases.GetAll();
            if (testata)
            {
                var operatoreAttivo = _state.Config.OperatoreAttivo;
                // Decisione utente 6.4.0 ("Test importati: solo stato finale"): i Test con
                // OrigineTest.ImportStatoUdt sono fotografie di uno stato noto, non esecuzioni fatte in TestLoom.
                // Esclusi dai conteggi operativi della testata (numero test, non eseguiti/da ritestare, carico
                // "miei attivi", Test senza componente e tasso "Test %"). Restano nello stato delle funzionalità (tasso
                // "Stato %", distribuzione sotto, su `tutti`) e nella copertura: lo stato noto è usabile per le funzionalità.
                var contati = tutti.Where(tc => tc.Origine != OrigineTest.ImportStatoUdt).ToList();
                long testati = 0, conformi = 0, senzaEcu = 0;
                TotaleTestCase = contati.LongCount();
                DaRitestare = 0;
                MieiAttivi = 0;
                foreach (var tc in contati)
                {
                    if (tc.StatoLavoro == StatoLavoroTest.DaRitestare) DaRitestare++;
                    if (tc.Assegnatario == operatoreAttivo && tc.DaFare) MieiAttivi++;
                    if (tc.Componente.Count == 0) senzaEcu++;
                    // Decisione 6.4.8 punto 5: "testato" = almeno una REV realmente eseguita; la sola pianificazione no.
                    if (OperationalSemantics.TestTestato(tc) && tc.StatoCorrente.HasValue)
                    {
                        testati++;
                        if (tc.StatoCorrente == Esito.Conforme) conformi++;
                    }
                }
                TassoConformitaPercentuale = testati == 0 ? 0 : 100.0 * conformi / testati;
                TotaleNcAperte = _state.NonConformita.CountByState(StatoNonConformita.Aperta);
                ChangeRequestAperte = ContaChangeRequestAperte();
                DaClassificare = _state.Segnalazioni.CountByState(StatoSegnalazione.DaClassificare);
                TestSenzaEcu = senzaEcu;
                ImpostaDistribuzioneConformita(ModalitaPercentualeConformita == 1
                    ? HomeConformitySummaryService.CalcolaTestPercentuale(contati)
                    : HomeConformitySummaryService.CalcolaStatoCorrenteFiltrato(tutti, FiltroStatoConformita(),
                        _state.Master.Veicoli.Load(), _state.Master.Funzionalita.Load(), _state.Master.Componente.Load(),
                        _state.Master.StatiFunzionalitaEcuVeicolo.Load()));
            }
            if (copertura)
                CoperturaRequisitiPercentuale = CoverageService.PerRequisito(_state.Master.Requisiti.Load(), tutti, soloBanco: false, soloVeicolo: false).PercentualeCompletamento;
        }
        OnPropertyChanged(nameof(TotaleTestCase));
        OnPropertyChanged(nameof(TotaleNcAperte));
        OnPropertyChanged(nameof(DaClassificare));
        OnPropertyChanged(nameof(DaRitestare));
        OnPropertyChanged(nameof(MieiAttivi));
        OnPropertyChanged(nameof(CoperturaRequisitiPercentuale));
        OnPropertyChanged(nameof(TassoConformitaPercentuale));
        OnPropertyChanged(nameof(TestoTassoConformita));
        OnPropertyChanged(nameof(RiepilogoTesto));
        OnPropertyChanged(nameof(TestSenzaEcu));
        OnPropertyChanged(nameof(MostraAvvisoTestSenzaEcu));
        OnPropertyChanged(nameof(TestSenzaEcuTesto));
        OnPropertyChanged(nameof(MostraSezioneAvvisi));
        OnPropertyChanged(nameof(ChangeRequestAperte));
    }

    public bool PuoCreare => _state.PuoScrivere;
    public string DashboardDataDaTesto
    {
        get => DashboardDataDa?.ToString("dd/MM/yyyy") ?? "";
        set { if (DateTime.TryParseExact(value, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) DashboardDataDa = new DateTimeOffset(d); }
    }
    public string DashboardDataATesto
    {
        get => DashboardDataA?.ToString("dd/MM/yyyy") ?? "";
        set { if (DateTime.TryParseExact(value, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) DashboardDataA = new DateTimeOffset(d); }
    }

    public string RiepilogoTesto => string.Format(LocalizationService.Instance.Traduci("HomeRiepilogoFmt"),
        TotaleTestCase, LocalizationService.Instance.Traduci(TotaleTestCase == 1 ? "HomeTestTotaleSingolare" : "HomeTestTotalePlurale"),
        TotaleNcAperte, LocalizationService.Instance.Traduci(TotaleNcAperte == 1 ? "HomeNcApertaSingolare" : "HomeNcApertaPlurale"));

    public BulkObservableCollection<TestCase> DaFare { get; } = new();
    public BulkObservableCollection<TestCase> Fatti { get; } = new();
    public bool DaFareVuoto => DaFare.Count == 0;
    public bool FattiVuoto => Fatti.Count == 0;

    [ObservableProperty] private bool mostraAltriFiltriDashboard;
    public ObservableCollection<ChartOption> ChartOptions { get; } = new();
    public ObservableCollection<ChartRenderizzato> ChartsAttivi { get; } = new();
    /// <summary>Due colonne di card solo in layout Ampio/Standard: in Compatto e Minimo ogni grafico occupa tutta la
    /// larghezza, cosi' la legenda resta leggibile a 12 DIP (a 1280x672 la card misura circa 1000 DIP).</summary>
    public int ChartColumns => ChartsAttivi.Count >= 2 && LayoutAdattivo.ModoCorrente >= ModoLayout.Standard ? 2 : 1;

    /// <summary>Altezza visibile della Home: le card dei grafici restano entro il 45% di questa altezza.</summary>
    public void ImpostaAltezzaVisibile(double altezza)
    {
        foreach (var card in ChartsAttivi) card.ImpostaAltezzaVisibile(altezza);
        OnPropertyChanged(nameof(ChartColumns));
    }

    public FiltroMultiSelect FiltroGammaDashboard { get; } = new();
    public FiltroMultiSelect FiltroVeicoloDashboard { get; } = new();
    public FiltroMultiSelect FiltroEcuDashboard { get; } = new();
    public FiltroMultiSelect FiltroFunzionalitaDashboard { get; } = new();
    public FiltroMultiSelect FiltroCategoriaFunzionalitaDashboard { get; } = new();
    public FiltroMultiSelect FiltroRequisitoDashboard { get; } = new();
    public FiltroMultiSelect FiltroStrumentoDashboard { get; } = new();
    public FiltroMultiSelect FiltroOperatoreDashboard { get; } = new();
    public FiltroMultiSelect FiltroAssegnatarioDashboard { get; } = new();
    public FiltroMultiSelect FiltroTipoTestDashboard { get; } = new();
    public FiltroMultiSelect FiltroEsitoDashboard { get; } = new();
    public ObservableCollection<FiltroCampoCustom> FiltriCampiCustomDashboard { get; } = new();

    [ObservableProperty] private DateTimeOffset? dashboardDataDa;
    [ObservableProperty] private DateTimeOffset? dashboardDataA;

    // TIME (6.6.1): filtri Home sulla durata delle operazioni e sul totale minuti del Test. Stessa semantica del Report (a livello di
    // Test); testo vuoto o non valido = nessun filtro. Persistiti per PC come gli altri filtri della Home.
    [ObservableProperty] private string testoFiltroTempoOperazione = "";
    [ObservableProperty] private int operatoreFiltroTempoOperazione;
    [ObservableProperty] private string testoFiltroTempoTotaleTest = "";
    [ObservableProperty] private int operatoreFiltroTempoTotaleTest;
    private bool _caricandoFiltriTempo;
    public FiltroDurataMinuti? FiltroTempoOperazioneAttivo => FiltroDurataMinuti.DaTesto(TestoFiltroTempoOperazione, OperatoreFiltroTempoOperazione);
    public FiltroDurataMinuti? FiltroTempoTotaleTestAttivo => FiltroDurataMinuti.DaTesto(TestoFiltroTempoTotaleTest, OperatoreFiltroTempoTotaleTest);
    /// <summary>Errore di digitazione nei minuti (es. "1,5", "-3", "abc"): il filtro resta ignorato, la vista lo segnala in rosso.</summary>
    public bool FiltroTempoOperazioneHaErrore => !FiltroDurataMinuti.TestoValido(TestoFiltroTempoOperazione);
    public bool FiltroTempoTotaleTestHaErrore => !FiltroDurataMinuti.TestoValido(TestoFiltroTempoTotaleTest);
    private const string ChiaveVistaTestoFiltroTempoOperazione = "Home.FiltroTempoOperazione.Minuti";
    private const string ChiaveVistaOperatoreFiltroTempoOperazione = "Home.FiltroTempoOperazione.Operatore";
    private const string ChiaveVistaTestoFiltroTempoTotaleTest = "Home.FiltroTempoTotaleTest.Minuti";
    private const string ChiaveVistaOperatoreFiltroTempoTotaleTest = "Home.FiltroTempoTotaleTest.Operatore";

    partial void OnTestoFiltroTempoOperazioneChanged(string value)
    {
        FiltroTempoCambiato(ChiaveVistaTestoFiltroTempoOperazione, value);
        OnPropertyChanged(nameof(FiltroTempoOperazioneHaErrore));
    }

    partial void OnOperatoreFiltroTempoOperazioneChanged(int value) =>
        FiltroTempoCambiato(ChiaveVistaOperatoreFiltroTempoOperazione, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    partial void OnTestoFiltroTempoTotaleTestChanged(string value)
    {
        FiltroTempoCambiato(ChiaveVistaTestoFiltroTempoTotaleTest, value);
        OnPropertyChanged(nameof(FiltroTempoTotaleTestHaErrore));
    }

    partial void OnOperatoreFiltroTempoTotaleTestChanged(int value) =>
        FiltroTempoCambiato(ChiaveVistaOperatoreFiltroTempoTotaleTest, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Un filtro TIME cambia: si persiste (salvo durante il caricamento iniziale) e la dashboard si aggiorna.</summary>
    private void FiltroTempoCambiato(string chiave, string valore)
    {
        if (_caricandoFiltriTempo) return;
        ViewStateService.Scrivi(_state, chiave, valore);
        OnPropertyChanged(nameof(HaFiltriDashboardAttivi));
        AggiornaDashboard();
    }

    /// <summary>Ripristina i filtri TIME salvati per questo PC (senza scriverli di nuovo e senza un refresh per ciascun valore).</summary>
    private void CaricaFiltriTempoSalvati(AppState state)
    {
        _caricandoFiltriTempo = true;
        try
        {
            TestoFiltroTempoOperazione = ViewStateService.Leggi(state, ChiaveVistaTestoFiltroTempoOperazione) ?? "";
            OperatoreFiltroTempoOperazione = LeggiOperatoreTempoSalvato(state, ChiaveVistaOperatoreFiltroTempoOperazione);
            TestoFiltroTempoTotaleTest = ViewStateService.Leggi(state, ChiaveVistaTestoFiltroTempoTotaleTest) ?? "";
            OperatoreFiltroTempoTotaleTest = LeggiOperatoreTempoSalvato(state, ChiaveVistaOperatoreFiltroTempoTotaleTest);
        }
        finally
        {
            _caricandoFiltriTempo = false;
        }
    }

    private static int LeggiOperatoreTempoSalvato(AppState state, string chiave) =>
        ViewStateService.Leggi(state, chiave) == "1" ? 1 : 0;

    public List<WidgetRiga> PerOperatore { get; private set; } = new();
    public List<WidgetRiga> PerGamma { get; private set; } = new();
    public string KpiIterazioniTesto { get; private set; } = "";
    public long TestCaseNelFiltro { get; private set; }
    public string TestoFiltroAttivo => string.Format(LocalizationService.Instance.Traduci(
        TestCaseNelFiltro == 1 ? "FiltroAttivoSingolareFmt" : "FiltroAttivoPluraleFmt"), TestCaseNelFiltro);
    public bool HaFiltriDashboardAttivi =>
        FiltroGammaDashboard.HaSelezione || FiltroVeicoloDashboard.HaSelezione || FiltroEcuDashboard.HaSelezione ||
        FiltroFunzionalitaDashboard.HaSelezione || FiltroCategoriaFunzionalitaDashboard.HaSelezione ||
        FiltroRequisitoDashboard.HaSelezione || FiltroStrumentoDashboard.HaSelezione ||
        FiltroOperatoreDashboard.HaSelezione || FiltroAssegnatarioDashboard.HaSelezione ||
        FiltroTipoTestDashboard.HaSelezione || FiltroEsitoDashboard.HaSelezione ||
        FiltriCampiCustomDashboard.Any(f => f.Filtro.HaSelezione) ||
        !string.IsNullOrWhiteSpace(TestoFiltroTempoOperazione) || !string.IsNullOrWhiteSpace(TestoFiltroTempoTotaleTest) ||
        DashboardDataDa != _defaultDataDa || DashboardDataA != _defaultDataA;

    private readonly Action _vaiATestSenzaEcu;

    public HomeViewModel(AppState state, Action<string?> apriTestCase, Action? vaiATestSenzaEcu = null,
        Action<RecentItemsService.ElementoRecente>? apriRecente = null, Action? vaiAAnomalie = null,
        bool aggiornaInBackground = false)
    {
        _state = state;
        _apriTestCase = apriTestCase;
        _vaiATestSenzaEcu = vaiATestSenzaEcu ?? (() => { });
        _apriRecente = apriRecente;
        _vaiAAnomalie = vaiAAnomalie ?? (() => { });
        _aggiornaInBackground = aggiornaInBackground;
        // Ordine e visibilita' delle sezioni: letti PRIMA di Aggiorna(), cosi' una sezione nascosta non fa ricalcolare
        // la dashboard al primo caricamento.
        CaricaLayout(state);
        // Campo diretto: la proprieta' richiamerebbe OnSoloMieiChanged (lista Da fare/Fatti) prima del primo giro.
        soloMiei = state.Config.SoloMieiElementiDaSeguire;
        modalitaPercentualeConformita = ViewStateService.Leggi(state, "Home.ModalitaPercentualeConformita") == "test" ? 1 : 0;

        var oggi = DateTime.Now;
        _defaultDataDa = new DateTimeOffset(new DateTime(oggi.Year, 1, 1));
        _defaultDataA = new DateTimeOffset(new DateTime(oggi.Year, 12, 31, 23, 59, 59));
        // Ultimo intervallo persistito per questo PC/operatore (feedback utente 2026-09-18: "tutte le viste
        // dovrebbero rimanere esattamente come quando l'app e' stata chiusa... questo migliorerebbe anche
        // eventuali possibili ricalcoli"): riapplicare sempre l'intervallo di default a ogni riapertura
        // dell'app forza un ricalcolo dell'intera dashboard su un range diverso da quello che l'utente aveva
        // davvero scelto. Assegnazione diretta al campo, non alla proprieta': durante la costruzione i filtri
        // e i repository non sono ancora pronti, AggiornaDashboard() gira una sola volta alla fine.
        dashboardDataDa = ViewStateService.LeggiData(state, ChiaveVistaDataDa) ?? _defaultDataDa;
        dashboardDataA = ViewStateService.LeggiData(state, ChiaveVistaDataA) ?? _defaultDataA;
        CaricaFiltriTempoSalvati(state);

        DaFare.CollectionChanged += (_, _) => OnPropertyChanged(nameof(DaFareVuoto));
        Fatti.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FattiVuoto));

        FiltroEsitoDashboard.Popola(Enum.GetValues(typeof(Esito)).Cast<Models.Esito>().Select(e => (e.ToString(), e.Label())));
        // Chiave di persistenza per ciascun filtro fisso (feedback utente 2026-09-18: "tutte quante le viste
        // devono mantenere filtri... non solo alcune"): stesso array gia' usato per agganciare Cambiato,
        // estenso con la chiave ViewStateService cosi' resta un solo punto da aggiornare se un filtro cambia
        // nome o se ne aggiunge uno nuovo.
        _filtriFissi = new[]
        {
            (Filtro: FiltroGammaDashboard, Chiave: "Home.FiltroGamma"),
            (Filtro: FiltroVeicoloDashboard, Chiave: "Home.FiltroVeicolo"),
            (Filtro: FiltroEcuDashboard, Chiave: "Home.FiltroEcu"),
            (Filtro: FiltroFunzionalitaDashboard, Chiave: "Home.FiltroFunzionalita"),
            (Filtro: FiltroCategoriaFunzionalitaDashboard, Chiave: "Home.FiltroCategoriaFunzionalita"),
            (Filtro: FiltroRequisitoDashboard, Chiave: "Home.FiltroRequisito"),
            (Filtro: FiltroStrumentoDashboard, Chiave: "Home.FiltroStrumento"),
            (Filtro: FiltroOperatoreDashboard, Chiave: "Home.FiltroOperatore"),
            (Filtro: FiltroAssegnatarioDashboard, Chiave: "Home.FiltroAssegnatario"),
            (Filtro: FiltroTipoTestDashboard, Chiave: "Home.FiltroTipoTest"),
            (Filtro: FiltroEsitoDashboard, Chiave: "Home.FiltroEsito"),
        };
        foreach (var (f, _) in _filtriFissi) f.Cambiato += AggiornaDashboard;

        // I filtri dei campi custom si creano nel primo giro con i grafici visibili (ApplicaCampiCustom): la costruzione
        // non legge i master, quindi non apre un refresh Phase D fuori dalla lettura del giro.

        // Preferenza locale (config.json del PC, MAI nel file condiviso) del tipo di rappresentazione per grafico.
        var tipiSalvati = ViewStateService.LeggiDizionario(state, ChiaveVistaTipiGrafico);
        ChartOption NuovaOpzione(string id, string chiaveTitolo, bool selezionato) => new()
        {
            Id = id,
            Titolo = LocalizationService.Instance.Traduci(chiaveTitolo),
            Selezionato = selezionato,
            TipiDisponibili = HomeChartCatalog.Compatibili(id)
                .Select(t => new TipoGraficoVoce(t, LocalizationService.Instance.Traduci("TipoGrafico_" + t))).ToList(),
        };
        ChartOptions.Add(NuovaOpzione(HomeChartCatalog.IdCopertura, "GraficoCoperturaRequisiti", false));
        ChartOptions.Add(NuovaOpzione(HomeChartCatalog.IdAndamento, "GraficoAndamentoAnomalie", false));
        ChartOptions.Add(NuovaOpzione(HomeChartCatalog.IdDonut, "GraficoEsitiSottoveicolo", true));
        foreach (var opt in ChartOptions)
        {
            var tipo = HomeChartCatalog.Normalizza(opt.Id, tipiSalvati.GetValueOrDefault(opt.Id));
            opt.TipoSelezionato = opt.TipiDisponibili.FirstOrDefault(t => t.Tipo == tipo);
        }
        // Selezione grafici persistita (stessa chiave ViewStateService dei filtri): riapplicata PRIMA di
        // agganciare la sottoscrizione di salvataggio, altrimenti la riapplicazione stessa provocherebbe
        // scritture ridondanti (comunque no-op grazie al confronto in ScriviLista, ma inutili).
        var graficiSalvati = ViewStateService.LeggiLista(state, ChiaveVistaGrafici);
        if (graficiSalvati.Count > 0)
            foreach (var opt in ChartOptions) opt.Selezionato = graficiSalvati.Contains(opt.Id);
        foreach (var opt in ChartOptions)
            opt.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == nameof(ChartOption.TipoSelezionato))
                {
                    CambiaTipoGrafico((ChartOption)sender!);
                    return;
                }
                if (e.PropertyName != nameof(ChartOption.Selezionato)) return;
                ViewStateService.ScriviLista(_state, ChiaveVistaGrafici, ChartOptions.Where(o => o.Selezionato).Select(o => o.Id).ToList());
                AggiornaDashboard();
            };

        // Aggiorna() popola i filtri del grafici (solo se visibile) e poi riapplica la selezione salvata (LegaFiltriPersistenti),
        // dentro lo stesso guard di batching: vedi FiltroMultiSelect.LegaPersistenza sul perche' una sola volta per ViewModel.
        Aggiorna();
    }

    private const string ChiaveVistaGrafici = "Home.Grafici";
    private const string ChiaveVistaTipiGrafico = "Home.TipiGrafico";

    /// <summary>Batching dei ricalcoli della dashboard (feedback utente 2026-09-18: "anche mettendo i filtri
    /// la cosa e' decisamente lenta... sembra bloccato"). Ogni Ripopola di un filtro che aveva gia' una
    /// selezione notifica Cambiato, e Cambiato e' agganciato ad AggiornaDashboard: un solo Aggiorna() poteva
    /// quindi ricalcolare l'intera dashboard (query, aggregati, PNG dei grafici) fino a una dozzina di volte,
    /// e ReimpostaFiltriDashboard tredici. Le richieste vengono accumulate e soddisfatte una volta sola.</summary>
    private int _refreshInCorso;
    private bool _dashboardDaAggiornare;

    // Dati delle sezioni "da calcolare" (vedi HomeViewModel.Giro): true = il dato non e' ancora stato letto dall'ultimo
    // refresh. Una sezione nascosta resta da calcolare e si calcola una sola volta alla prima visualizzazione.
    private bool _testataDaCalcolare = true;
    /// <summary>Incrementata ogni volta che la testata (Stato %) torna "da calcolare". Un giro azzera il flag solo se, alla sua
    /// applicazione, la versione e' ancora quella con cui e' stato catturato: un cambio di filtro arrivato nel frattempo non va perso.</summary>
    private int _versioneTestata;
    private bool _coperturaDaCalcolare = true;
    private bool _listeDaCalcolare = true;
    private bool _filtriDaCalcolare = true;
    private bool _filtriLegati;
    private (FiltroMultiSelect Filtro, string Chiave)[] _filtriFissi = Array.Empty<(FiltroMultiSelect Filtro, string Chiave)>();

    private void EseguiAggiornamentoDashboardPendente()
    {
        if (_refreshInCorso > 0 || !_dashboardDaAggiornare) return;
        // Sezione "grafici" nascosta: la richiesta resta pendente (query + PNG dei grafici saltati) e viene
        // soddisfatta quando la sezione torna visibile, vedi NotificaLayout().
        if (!SezioneGraficiVisibile) return;
        AvviaGiro(recenti: false);
    }

    /// <summary>Aggiornamento delle sezioni visibili. In modalita' in background ritorna subito (il calcolo prosegue, vedi
    /// AggiornaAsync); altrimenti e' sincrono e rilancia le eccezioni come prima. Test e percorsi esistenti: sincrono.</summary>
    public void Aggiorna()
    {
        if (_aggiornaInBackground)
        {
            _ = AggiornaAsync();
            return;
        }
        AggiornaAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Dopo un cambio di visibilita' o di ordine delle sezioni (o un layout del team applicato): avvia un giro per le sezioni
    /// appena rese visibili e per la dashboard se era in attesa. Senza nulla da calcolare non fa nulla. Vedi HomeViewModel.Giro.
    /// </summary>
    private void EseguiAggiornamentoPendente()
    {
        if (_refreshInCorso != 0) return;
        if (HaSezioniDaCalcolare() || (_dashboardDaAggiornare && SezioneGraficiVisibile)) AvviaGiro(recenti: false);
    }

    private bool HaSezioniDaCalcolare() =>
        (_testataDaCalcolare && SezioneTestataVisibile) || (_coperturaDaCalcolare && SezioneKpiVisibile) ||
        (_listeDaCalcolare && SezioneListeVisibile) || (_filtriDaCalcolare && SezioneGraficiVisibile);

    /// <summary>Elenco componente e campi custom del grafici sul percorso non indicizzato (stessi valori del batch indicizzato).</summary>
    private void AggiornaFiltriInMemoria()
    {
        FiltroEcuDashboard.Ripopola((_state.CartellaValida ? _state.TestCases.GetAll().SelectMany(tc => tc.Componente).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x) : Enumerable.Empty<string>()).Select(x => (x, x)));

        var tuttiITestCase = _state.CartellaValida ? _state.TestCases.GetAll() : new List<TestCase>();
        foreach (var filtroCustom in FiltriCampiCustomDashboard)
            filtroCustom.Filtro.Ripopola(CampoSistemaCatalogoService.OpzioniFiltro(filtroCustom.Tipo, filtroCustom.OpzioniLista,
                CampoSistemaCatalogoService.ValoriDistinti(tuttiITestCase, filtroCustom.Chiave)).Select(v => (v, v)));
    }

    /// <summary>
    /// Riapplica la selezione salvata dei filtri del grafici. Va chiamata solo DOPO il popolamento dei cataloghi (altrimenti non
    /// ci sono opzioni a cui applicarla). Ogni filtro si collega una sola volta (vedi FiltroMultiSelect.LegaPersistenza): i filtri
    /// dei campi di sistema aggiunti dopo il primo giro si collegano quando hanno le loro opzioni.
    /// </summary>
    private void LegaFiltriPersistenti()
    {
        if (!_filtriLegati)
        {
            _filtriLegati = true;
            foreach (var (f, chiave) in _filtriFissi) f.LegaPersistenza(_state, chiave);
        }
        foreach (var custom in FiltriCampiCustomDashboard.Where(c => !c.PersistenzaCollegata && c.Filtro.Opzioni.Count > 0))
        {
            custom.PersistenzaCollegata = true;
            custom.Filtro.LegaPersistenza(_state, "Home.FiltroCustom." + custom.Chiave);
        }
    }
    private const string ChiaveVistaDataDa = "Home.DataDa";
    private const string ChiaveVistaDataA = "Home.DataA";

    partial void OnDashboardDataDaChanged(DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(DashboardDataDaTesto));
        ViewStateService.ScriviData(_state, ChiaveVistaDataDa, value);
        AggiornaDashboard();
    }
    partial void OnDashboardDataAChanged(DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(DashboardDataATesto));
        ViewStateService.ScriviData(_state, ChiaveVistaDataA, value);
        AggiornaDashboard();
    }

    [RelayCommand]
    private void ToggleAltriFiltriDashboard() => MostraAltriFiltriDashboard = !MostraAltriFiltriDashboard;

    [RelayCommand]
    private void FiltraDashboardPerGamma(string? gamma)
    {
        _refreshInCorso++;
        try
        {
            FiltroGammaDashboard.DeselezionaTutto();
            if (gamma == null) return;
            var opt = FiltroGammaDashboard.Opzioni.FirstOrDefault(o => o.Valore == gamma);
            if (opt != null) opt.Selezionato = true;
        }
        finally { _refreshInCorso--; }
        EseguiAggiornamentoDashboardPendente();
    }

    [RelayCommand]
    private void FiltraDashboardPerOperatore(string? operatore)
    {
        _refreshInCorso++;
        try
        {
            FiltroOperatoreDashboard.DeselezionaTutto();
            if (operatore == null) return;
            var opt = FiltroOperatoreDashboard.Opzioni.FirstOrDefault(o => o.Valore == operatore);
            if (opt != null) opt.Selezionato = true;
        }
        finally { _refreshInCorso--; }
        EseguiAggiornamentoDashboardPendente();
    }

    [RelayCommand]
    private void ReimpostaFiltriDashboard()
    {
        _refreshInCorso++;
        try
        {
            foreach (var f in new[] { FiltroGammaDashboard, FiltroVeicoloDashboard, FiltroEcuDashboard, FiltroFunzionalitaDashboard, FiltroCategoriaFunzionalitaDashboard,
                         FiltroRequisitoDashboard, FiltroStrumentoDashboard, FiltroOperatoreDashboard, FiltroAssegnatarioDashboard, FiltroTipoTestDashboard, FiltroEsitoDashboard })
                f.DeselezionaTutto();
            foreach (var f in FiltriCampiCustomDashboard) f.Filtro.DeselezionaTutto();
            TestoFiltroTempoOperazione = "";
            OperatoreFiltroTempoOperazione = 0;
            TestoFiltroTempoTotaleTest = "";
            OperatoreFiltroTempoTotaleTest = 0;
            DashboardDataDa = _defaultDataDa;
            DashboardDataA = _defaultDataA;
        }
        finally { _refreshInCorso--; }
        EseguiAggiornamentoDashboardPendente();
    }

    private void AggiornaDashboard()
    {
        _dashboardDaAggiornare = true;
        // 6.7.1: lo Stato % della testata segue i filtri della dashboard, quindi si ricalcola anche con la testata.
        _testataDaCalcolare = true;
        _versioneTestata++;
        if (_refreshInCorso > 0 || !SezioneGraficiVisibile) return;
        AvviaGiro(recenti: false);
    }

    /// <summary>Regola utente (2026-10, punto 1): conta il lavoro reale, cioe' ogni operazione realmente eseguita
    /// (Esito.EEseguito; per componente coinvolta) in ogni REV, non 1 per REV; N.A./Da eseguire/Da rivedere no. Le celle
    /// ereditate in una nuova REV partono Da eseguire, quindi contano solo se rieseguite. Le REV con Origine Rete (chiusura da rete) non sono
    /// attribuite a nessun operatore. Equivalente a raw_attempt_count &gt; 0 AND origine &lt;&gt; Rete nel read-model SQL.</summary>
    internal static Dictionary<string, long> ContaEsecuzioniPerOperatore(IEnumerable<TestCase> testCases)
    {
        var conteggi = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var tc in testCases)
            foreach (var e in tc.Esecuzioni)
                if (!string.IsNullOrEmpty(e.Operatore) && OperationalSemantics.EsecuzioneAttribuibileAOperatore(e))
                    conteggi[e.Operatore] = conteggi.GetValueOrDefault(e.Operatore) + OperationalSemantics.CountWorkUnits(tc, e);
        return conteggi;
    }

    /// <summary>Dashboard sul percorso non indicizzato, sul thread UI, con la sola lettura logica dei master: stessi valori del
    /// percorso indicizzato. La chiama ApplicaDashboard (Giro) se l'indice non e' disponibile o la lettura e' fallita.</summary>
    private void AggiornaDashboardInMemoria(bool coperturaSelezionata, bool andamentoSelezionato, bool donutSelezionato)
    {
        // Una sola lettura logica dei master per l'intero refresh: senza questo, ogni catalogo
        // interrogato rifa' per conto suo la scansione della cartella condivisa (vedi MasterLetturaScope).
        using var letturaMaster = MasterLetturaScope.Apri();

        var veicoli = _state.Master.Veicoli.Load();
        var gammaPerVeicolo = veicoli.ToDictionary(v => v.Nome, v => v.GammaNome, StringComparer.OrdinalIgnoreCase);
        string? GammaDi(string veicolo) => gammaPerVeicolo.GetValueOrDefault(veicolo);
        IEnumerable<string> CategorieDiFunzionalita(string nome) => _categoriaPerFunzionalita.GetValueOrDefault(nome) ?? Enumerable.Empty<string>();

        var filtro = CreaFiltroDashboard();
        var testCases = filtro.Applica(_state.TestCases.GetAll(), GammaDi, CategorieDiFunzionalita);
        // Decisione utente 6.4.0 ("Test importati: solo stato finale"): i Test con OrigineTest.ImportStatoUdt
        // restano dentro `testCases` (usato sotto per copertura e andamento NC, entrambi stato finale
        // invariato) ma escono dai conteggi operativi veri e propri — numero test nel filtro, grafico
        // esiti/sottoveicolo, KPI iterazioni, carico per operatore/gamma — che usano invece questo insieme.
        var testCasesContati = testCases.Where(tc => tc.Origine != OrigineTest.ImportStatoUdt).ToList();
        TestCaseNelFiltro = testCasesContati.LongCount();
        OnPropertyChanged(nameof(TestCaseNelFiltro));
        OnPropertyChanged(nameof(TestoFiltroAttivo));

        var requisiti = _state.Master.Requisiti.Load();

        if (coperturaSelezionata)
        {
            var bucket = CoverageService.PerRequisito(requisiti, testCases, soloBanco: false, soloVeicolo: false);
            var titolo = LocalizationService.Instance.Traduci("GraficoCoperturaRequisiti");
            ChartsAttivi.Add(OttieniGraficoCopertura(titolo, bucket));
        }
        if (andamentoSelezionato)
        {
            IReadOnlyList<NonConformita> nonConformita;
            if (testCases.Count == _state.TestCases.Count())
            {
                nonConformita = _state.NonConformita.GetAll();
            }
            else
            {
                var testIds = testCases.Select(tc => tc.Id).ToList();
                nonConformita = _state.NonConformita.GetByTestIds(testIds);
            }
            var titolo = LocalizationService.Instance.Traduci("GraficoAndamentoAnomalie");
            ChartsAttivi.Add(OttieniGraficoAndamento(titolo, ReportService.AndamentoNc(nonConformita)));
        }
        if (donutSelezionato)
        {
            var perSottoveicolo = testCasesContati.SelectMany(tc => tc.VeicoliEffettivi.DefaultIfEmpty("—")).GroupBy(v => v)
                .Select(g => (Nome: g.Key, Valore: g.LongCount()))
                .OrderByDescending(x => x.Valore).ToList();
            var perEsito = testCasesContati.GroupBy(tc => tc.StatoCorrente ?? Esito.NonEseguitoNA)
                .Select(g => (Esito: g.Key, Valore: g.LongCount())).ToList();
            // stessa sorgente (testCasesContati) del donut: dettaglio esito per veicolo, nessuna lettura aggiuntiva
            // i Test senza veicolo restano fuori (come nel percorso SQL, che parte da test_vehicle)
            var perVeicoloEsito = testCasesContati
                .SelectMany(tc => tc.VeicoliEffettivi.Select(v => (Nome: v, Esito: tc.StatoCorrente ?? Esito.NonEseguitoNA)))
                .GroupBy(x => x).Select(g => (g.Key.Nome, g.Key.Esito, Valore: g.LongCount()))
                .OrderBy(x => x.Nome, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Esito).ToList();
            var titolo = LocalizationService.Instance.Traduci("GraficoEsitiSottoveicolo");
            ChartsAttivi.Add(OttieniGraficoDonut(titolo, perSottoveicolo, perEsito, perVeicoloEsito));
        }
        OnPropertyChanged(nameof(ChartColumns));

        var kpi = KpiService.CalcolaAggregato(testCasesContati);
        var casoConcluso = LocalizationService.Instance.Traduci(kpi.CasiRaggiuntiConforme == 1 ? "KpiCasoConclusoSingolare" : "KpiCasoConclusoPlurale");
        var maiArrivato = LocalizationService.Instance.Traduci(kpi.CasiMaiRaggiuntiConforme == 1 ? "KpiMaiArrivatoSingolare" : "KpiMaiArrivatoPlurale");
        KpiIterazioniTesto = string.Format(LocalizationService.Instance.Traduci("KpiIterazioniFmt"),
            kpi.MediaIterazioni, kpi.CasiRaggiuntiConforme, casoConcluso, kpi.CasiMaiRaggiuntiConforme, maiArrivato, kpi.PercentualeMaiRaggiunti);
        OnPropertyChanged(nameof(KpiIterazioniTesto));

        var countPerOperatore = ContaEsecuzioniPerOperatore(testCasesContati);
        var countPerGamma = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var tc in testCasesContati)
        {
            foreach (var v in tc.VeicoliEffettivi)
            {
                var g = GammaDi(v);
                if (!string.IsNullOrEmpty(g))
                    countPerGamma[g] = countPerGamma.GetValueOrDefault(g) + 1L;
            }
        }

        var etichettaSing = LocalizationService.Instance.Traduci("EsecuzioneSingolare");
        var etichettaPlur = LocalizationService.Instance.Traduci("EsecuzionePlurale");
        PerOperatore = countPerOperatore
            .Select(kv => new WidgetRiga { Etichetta = kv.Key, Valore = kv.Value, Sottotitolo = kv.Value == 1 ? etichettaSing : etichettaPlur })
            .OrderByDescending(w => w.Valore)
            .ToList();
        OnPropertyChanged(nameof(PerOperatore));

        PerGamma = countPerGamma
            .Select(kv => new WidgetRiga { Etichetta = kv.Key, Valore = kv.Value, Sottotitolo = "test" })
            .OrderByDescending(w => w.Valore)
            .ToList();
        OnPropertyChanged(nameof(PerGamma));
    }

    /// <summary>Unica traduzione dello stato UI in <see cref="DashboardFilter"/> per tenere identici il
    /// fallback autorevole e il percorso scalabile SQLite.</summary>
    private DashboardFilter CreaFiltroDashboard() => new()
    {
        Gamma = FiltroGammaDashboard.Selezionati,
        Veicolo = FiltroVeicoloDashboard.Selezionati,
        Componente = FiltroEcuDashboard.Selezionati,
        Funzionalita = FiltroFunzionalitaDashboard.Selezionati,
        CategoriaFunzionalita = FiltroCategoriaFunzionalitaDashboard.Selezionati,
        Requisito = FiltroRequisitoDashboard.Selezionati,
        Strumento = FiltroStrumentoDashboard.Selezionati,
        Operatore = FiltroOperatoreDashboard.Selezionati,
        Assegnatario = FiltroAssegnatarioDashboard.Selezionati,
        TestTemplateUid = FiltroTipoTestDashboard.Selezionati.Select(Guid.Parse).ToList(),
        Esito = FiltroEsitoDashboard.Selezionati.Select(Enum.Parse<Models.Esito>).ToList(),
        CampiCustom = FiltriCampiCustomDashboard.ToDictionary(f => f.Chiave, f => f.Filtro.Selezionati),
        DataDa = DashboardDataDa?.DateTime,
        DataA = DashboardDataA?.DateTime,
        TempoOperazione = FiltroTempoOperazioneAttivo,
        TempoTotaleTest = FiltroTempoTotaleTestAttivo,
    };

    /// <summary>Filtri della dashboard per lo Stato % (6.7.1). Le date di default (anno corrente) non sono un filtro: stessa regola
    /// dell'indicatore "filtri attivi" della Home. Senza altri criteri lo Stato % resta su tutti i Test, universo "Non testato" incluso.</summary>
    private DashboardFilter FiltroStatoConformita()
    {
        var filtro = CreaFiltroDashboard();
        if (DashboardDataDa == _defaultDataDa) filtro.DataDa = null;
        if (DashboardDataA == _defaultDataA) filtro.DataA = null;
        return filtro;
    }

    private void SvuotaGrafici()
    {
        // Clear() rimuove i controlli: ogni card rilascia la bitmap che possiede (render alla dimensione della card);
        // le bitmap condivise con la cache le rilascia la cache in InvalidaGrafico*.
        var daChiudere = ChartsAttivi.ToList();
        ChartsAttivi.Clear();
        foreach (var card in daChiudere) card.Dispose();
        InvalidaGraficoCopertura();
        InvalidaGraficoAndamento();
        InvalidaGraficoDonut();
    }

    private static Bitmap? PngABitmap(byte[] png)
    {
        try { using var ms = new MemoryStream(png); return new Bitmap(ms); }
        catch { return null; }
    }

    // ------------------------------------------------ cache PNG dei grafici (perf: rivisita Home)
    // I PNG SkiaSharp sono costosi da rigenerare e la Home vi torna spesso senza che i dati del
    // grafico siano cambiati (stessa gamma di filtri, stesso indice). Il confronto e' SEMPRE sui
    // dati di input effettivi (bucket/aggregati numerici, liste ordinate), mai su timestamp o file:
    // un titolo diverso (cambio lingua) o un valore diverso invalida la cache come qualsiasi altro dato.
    // La validita' della cache e' un bool dedicato, non "Bitmap non nullo": PngABitmap puo' restituire null
    // (decodifica PNG fallita) anche quando l'input e' correttissimo, ad es. in un host senza platform Skia/
    // Avalonia inizializzata (unit test puri). Legare la cache alla riuscita della decodifica farebbe
    // ripetere il render a ogni chiamata in quel caso, invece di "fallito una volta, resta fallito finche'
    // l'input non cambia" — la stessa politica del resto della cache.
    //
    // Piano 6.5.0 punto 5: la chiave di cache e' (titolo, TIPO, dati). Gli aggregati dell'ultimo refresh restano
    // nella cache anche quando cambia solo il tipo: il cambio tipo ridisegna da li' (PNG + decodifica fuori dal
    // thread UI, vedi RiRenderizzaGraficoAsync) senza rifare alcuna query.
    private abstract class CacheGrafico
    {
        public bool Valida;
        public string? Titolo;
        public TipoGrafico Tipo;
        public Bitmap? Bitmap;
        /// <summary>Incrementata a ogni render sincrono/invalidazione: scarta i risultati asincroni superati.</summary>
        public int Versione;
        public abstract bool HaDati { get; }
        /// <summary>Snapshot immutabile degli aggregati correnti: eseguibile su qualunque thread.</summary>
        public abstract Func<byte[]> CreaRender(TipoGrafico tipo);
        protected abstract void AzzeraDati();

        public void Invalida()
        {
            Bitmap?.Dispose();
            Bitmap = null;
            Titolo = null;
            Valida = false;
            Versione++;
            AzzeraDati();
        }
    }

    private sealed class CacheGrafico<TDati> : CacheGrafico where TDati : class
    {
        private readonly Func<TDati, TDati, bool> _uguali;
        private readonly Func<TipoGrafico, string, TDati, byte[]> _render;
        private TDati? _dati;

        public CacheGrafico(Func<TDati, TDati, bool> uguali, Func<TipoGrafico, string, TDati, byte[]> render)
        { _uguali = uguali; _render = render; }

        public override bool HaDati => _dati != null && Titolo != null;
        public bool Corrisponde(string titolo, TipoGrafico tipo, TDati dati) =>
            Valida && Titolo == titolo && Tipo == tipo && _dati != null && _uguali(_dati, dati);
        public void ImpostaDati(string titolo, TDati dati) { Titolo = titolo; _dati = dati; }
        public byte[] Render(TipoGrafico tipo, string titolo, TDati dati) => _render(tipo, titolo, dati);
        protected override void AzzeraDati() => _dati = null;

        public override Func<byte[]> CreaRender(TipoGrafico tipo)
        {
            var titolo = Titolo!; var dati = _dati!;
            return () => _render(tipo, titolo, dati);
        }
    }

    // Copertura: 4 contatori (Tuple = tipo riferimento, richiesto dal vincolo class della cache generica).
    private readonly CacheGrafico<Tuple<long, long, long, long>> _cacheCopertura = new(
        (a, b) => a.Equals(b),
        (tipo, titolo, d) => ChartRenderer.CoperturaPerTipo(tipo, titolo,
            new CoverageBucket { Conformi = d.Item1, ConformiConDeviazioni = d.Item2, NonConformi = d.Item3, NonTestati = d.Item4 }, 900, 380));

    private readonly CacheGrafico<IReadOnlyList<(DateTime Data, int Aperte, int Chiuse)>> _cacheAndamento = new(
        (a, b) => a.SequenceEqual(b),
        (tipo, titolo, d) => ChartRenderer.AndamentoNonConformita(titolo, d, 900, 380, area: tipo == TipoGrafico.Area));

    private readonly CacheGrafico<DatiDonut> _cacheDonut = new(
        (a, b) => a.PerSottoveicolo.SequenceEqual(b.PerSottoveicolo) && a.PerEsito.SequenceEqual(b.PerEsito)
            && a.PerVeicoloEsito.SequenceEqual(b.PerVeicoloEsito),
        (tipo, titolo, d) => tipo == TipoGrafico.Ciambella
            ? ChartRenderer.DonutEsitiPerSottoveicolo(titolo, d.PerSottoveicolo, d.PerEsito, 900, 480)
            : ChartRenderer.SottoveicoloEsitiPerTipo(tipo, titolo, d.PerVeicoloEsito, 900, 480));

    private sealed record DatiDonut(List<(string Nome, long Valore)> PerSottoveicolo, List<(Esito Esito, long Valore)> PerEsito,
        List<(string Nome, Esito Esito, long Valore)> PerVeicoloEsito);

    private CacheGrafico? CachePerId(string id) => id switch
    {
        HomeChartCatalog.IdCopertura => _cacheCopertura,
        HomeChartCatalog.IdAndamento => _cacheAndamento,
        HomeChartCatalog.IdDonut => _cacheDonut,
        _ => null,
    };

    private ChartRenderizzato OttieniGrafico<TDati>(CacheGrafico<TDati> cache, string id, string titolo, TDati dati, DatiGrafico datiGrafico) where TDati : class
    {
        var tipo = ChartOptions.First(o => o.Id == id).Tipo;
        if (cache.Corrisponde(titolo, tipo, dati))
        {
            PerfContatori.IncrementaGraficoPngRiusato();
            return CreaCard(id, tipo, titolo, datiGrafico, cache.Bitmap);
        }

        cache.Bitmap?.Dispose();
        cache.Versione++;
        PerfContatori.IncrementaGraficoPngGenerato();
        cache.Bitmap = PngABitmap(cache.Render(tipo, titolo, dati));
        cache.ImpostaDati(titolo, dati);
        cache.Tipo = tipo;
        cache.Valida = true;
        return CreaCard(id, tipo, titolo, datiGrafico, cache.Bitmap);
    }

    /// <summary>Card con snapshot dei dati e riepilogo dei filtri nel momento in cui viene creata.</summary>
    private ChartRenderizzato CreaCard(string id, TipoGrafico tipo, string titolo, DatiGrafico? dati, Bitmap? immagine) => new()
    {
        Id = id, Tipo = tipo, Titolo = titolo, Dati = dati, Filtri = DescrizioneFiltriAttivi(), Immagine = immagine,
        Registra = RegistraEsportazioneGrafico,
    };

    /// <summary>Riepilogo dei filtri Home attivi: titolo della finestra grafico ed export PNG (stessa selezione dei dati).</summary>
    internal string DescrizioneFiltriAttivi()
    {
        var loc = LocalizationService.Instance;
        var parti = new List<string>();
        void Aggiungi(FiltroMultiSelect filtro, string chiaveEtichetta)
        {
            if (!filtro.HaSelezione) return;
            var voci = string.Join(", ", filtro.Opzioni.Where(o => o.Selezionato).Select(o => o.Etichetta));
            parti.Add($"{loc.Traduci(chiaveEtichetta)}: {voci}");
        }
        Aggiungi(FiltroGammaDashboard, "GammaVeicolareLabel");
        Aggiungi(FiltroVeicoloDashboard, "SottoveicoloLabel");
        Aggiungi(FiltroEcuDashboard, "ColEcu");
        Aggiungi(FiltroFunzionalitaDashboard, "FunzionalitaLabel");
        Aggiungi(FiltroCategoriaFunzionalitaDashboard, "FiltroCategoriaFunzionalitaLabel");
        Aggiungi(FiltroRequisitoDashboard, "RequisitoLabel");
        Aggiungi(FiltroStrumentoDashboard, "StrumentoLabel");
        Aggiungi(FiltroTipoTestDashboard, "TipoTestLabel");
        Aggiungi(FiltroOperatoreDashboard, "OperatoreEsecuzioniLabel");
        Aggiungi(FiltroAssegnatarioDashboard, "AssegnatoALabel");
        Aggiungi(FiltroEsitoDashboard, "ColEsito");
        if (DashboardDataDa != _defaultDataDa || DashboardDataA != _defaultDataA)
            parti.Add($"{loc.Traduci("DataDaLabel")} {DashboardDataDa?.ToString("dd/MM/yyyy")} – {loc.Traduci("DataALabel")} {DashboardDataA?.ToString("dd/MM/yyyy")}");
        if (FiltroTempoOperazioneAttivo is { } tempoOperazione) parti.Add($"{loc.Traduci("FiltroTempoOperazioneLabel")} {tempoOperazione.Etichetta}");
        if (FiltroTempoTotaleTestAttivo is { } tempoTotale) parti.Add($"{loc.Traduci("FiltroTempoTotaleTestLabel")} {tempoTotale.Etichetta}");
        return parti.Count == 0 ? loc.Traduci("GraficoNessunFiltro") : string.Join(" · ", parti);
    }

    private ChartRenderizzato OttieniGraficoCopertura(string titolo, CoverageBucket bucket) =>
        OttieniGrafico(_cacheCopertura, HomeChartCatalog.IdCopertura, titolo,
            Tuple.Create(bucket.Conformi, bucket.ConformiConDeviazioni, bucket.NonConformi, bucket.NonTestati), DatiCopertura.Da(bucket));

    private ChartRenderizzato OttieniGraficoAndamento(string titolo, IReadOnlyList<(DateTime Data, int Aperte, int Chiuse)> punti) =>
        OttieniGrafico(_cacheAndamento, HomeChartCatalog.IdAndamento, titolo, punti, new DatiAndamento(punti));

    private ChartRenderizzato OttieniGraficoDonut(string titolo, List<(string Nome, long Valore)> perSottoveicolo, List<(Esito Esito, long Valore)> perEsito,
        List<(string Nome, Esito Esito, long Valore)> perVeicoloEsito) =>
        OttieniGrafico(_cacheDonut, HomeChartCatalog.IdDonut, titolo, new DatiDonut(perSottoveicolo, perEsito, perVeicoloEsito),
            new DatiEsiti(perSottoveicolo, perEsito, perVeicoloEsito));

    private void InvalidaGraficoCopertura() => _cacheCopertura.Invalida();
    private void InvalidaGraficoAndamento() => _cacheAndamento.Invalida();
    private void InvalidaGraficoDonut() => _cacheDonut.Invalida();

    /// <summary>Ultimo cambio tipo in corso (per i test; nell'app e' fire-and-forget).</summary>
    internal Task UltimoCambioTipoTask { get; private set; } = Task.CompletedTask;

    /// <summary>Solo test: attende dopo il render di un tipo e prima dell'applicazione del risultato,
    /// per rendere deterministico l'ordine di completamento di richieste sovrapposte.</summary>
    internal Func<TipoGrafico, Task>? GateRenderPerTest { get; set; }

    /// <summary>Cambio del tipo di rappresentazione: persiste la preferenza LOCALE e, se il grafico e'
    /// visibile e ha gia' i suoi aggregati, lo ridisegna da quelli — nessuna query, nessun ricalcolo filtri.</summary>
    private void CambiaTipoGrafico(ChartOption opt)
    {
        var daSalvare = ChartOptions
            .Where(o => o.Tipo != HomeChartCatalog.Predefinito(o.Id))
            .ToDictionary(o => o.Id, o => (string?)o.Tipo.ToString());
        ViewStateService.ScriviDizionario(_state, ChiaveVistaTipiGrafico, daSalvare);
        if (!opt.Selezionato) return;
        UltimoCambioTipoTask = RiRenderizzaGraficoAsync(opt.Id, opt.Tipo);
    }

    private async Task RiRenderizzaGraficoAsync(string id, TipoGrafico tipo)
    {
        var cache = CachePerId(id);
        if (cache == null) return;
        // Versione incrementata SEMPRE e prima di ogni uscita anticipata: ogni cambio tipo invalida le richieste
        // in volo (A->B->A: il render di B ancora in corso non deve sovrascrivere A).
        var versione = ++cache.Versione;
        if (!cache.HaDati) return; // nessun aggregato ancora: il tipo vale dal prossimo refresh
        if (cache.Valida && cache.Tipo == tipo) return;
        var titolo = cache.Titolo!;
        try
        {
            var render = cache.CreaRender(tipo);
            var bitmap = await Task.Run(async () =>
            {
                var b = PngABitmap(render());
                if (GateRenderPerTest != null) await GateRenderPerTest(tipo);
                return b;
            });
            // Di nuovo sul thread UI (nell'app): un refresh o una nuova scelta nel frattempo rendono superato
            // questo risultato.
            if (versione != cache.Versione || !cache.HaDati)
            {
                bitmap?.Dispose();
                return;
            }
            PerfContatori.IncrementaGraficoPngGenerato();
            var vecchio = cache.Bitmap;
            cache.Bitmap = bitmap;
            cache.Tipo = tipo;
            cache.Valida = true;
            for (var i = 0; i < ChartsAttivi.Count; i++)
            {
                if (ChartsAttivi[i].Id != id) continue;
                var vecchiaCard = ChartsAttivi[i];
                ChartsAttivi[i] = CreaCard(id, tipo, titolo, vecchiaCard.Dati, bitmap);
                vecchiaCard.Dispose();
                break;
            }
            vecchio?.Dispose();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Cambio tipo grafico Home", ex);
        }
    }

    partial void OnSoloMieiChanged(bool value)
    {
        _state.Config.SoloMieiElementiDaSeguire = value;
        _state.ConfigService.Save(_state.Config);
        ResetPagineHome();
        RichiediAggiornamentoListe();
    }

    /// <summary>Ritardo della ricerca testuale della Home, come quello della Lista Test: ogni tasto riavvia il conto alla
    /// rovescia e le liste si ricalcolano a fine digitazione. Campo svuotato = applicazione immediata.</summary>
    internal static TimeSpan DebounceTestoRicerca = TimeSpan.FromMilliseconds(250);
    private string _testoRicercaApplicato = "";
    private CancellationTokenSource? _debounceTestoRicercaCts;

    /// <summary>Testo applicato alle liste dopo il ritardo di digitazione, senza spazi ai bordi.</summary>
    internal string TestoRicercaApplicato => _testoRicercaApplicato;

    /// <summary>Percorso indicizzato di Da fare/Fatti. false = percorso in memoria: serve al test di parita' e alle misure A/B.</summary>
    internal bool PercorsoIndicizzatoAttivo { get; set; } = true;

    partial void OnTestoRicercaChanged(string value)
    {
        _debounceTestoRicercaCts?.Cancel();
        if (string.IsNullOrWhiteSpace(value))
        {
            ApplicaTestoRicerca(value);
            return;
        }
        var cts = _debounceTestoRicercaCts = new CancellationTokenSource();
        _ = ApplicaTestoRicercaDopoDebounce(value, cts.Token);
    }

    private async Task ApplicaTestoRicercaDopoDebounce(string value, CancellationToken token)
    {
        try { await Task.Delay(DebounceTestoRicerca, token); }
        catch (OperationCanceledException) { return; }
        if (!token.IsCancellationRequested) ApplicaTestoRicerca(value);
    }

    private void ApplicaTestoRicerca(string value)
    {
        var applicato = value.Trim();
        if (applicato == _testoRicercaApplicato) return;
        _testoRicercaApplicato = applicato;
        ResetPagineHome();
        RichiediAggiornamentoListe();
    }

    /// <summary>Invio nella casella di ricerca: applica subito il testo corrente, senza attendere il ritardo di digitazione.</summary>
    [RelayCommand]
    private void ConfermaRicercaTesto() => ConfermaRicercaTestoImmediata();

    internal void ConfermaRicercaTestoImmediata()
    {
        _debounceTestoRicercaCts?.Cancel();
        ApplicaTestoRicerca(TestoRicerca);
    }

    /// <summary>Ricalcola subito Da fare/Fatti con il testo applicato, senza il giro in background (test e misure).</summary>
    internal void AggiornaListeSincrono()
    {
        _listeDaCalcolare = false;
        AggiornaDaFareEFatti();
    }

    private bool NelloScopeGamma(TestCase tc, Func<string, string?> gammaDi)
    {
        var op = _state.OperatoreCorrente;
        // RuoloEffettivo (piano 6.4.2, §4b): mai il Ruolo grezzo per decidere lo scoping per Gamma.
        if (op == null || op.RuoloEffettivo != RuoloOperatore.Standard || op.GammeAssegnate.Count == 0) return true;
        return tc.VeicoliEffettivi.Any(v => gammaDi(v) is { } gamma && op.GammeAssegnate.Contains(gamma));
    }

    private void AggiornaDaFareEFatti()
    {
        // Sezione "liste" nascosta: nessuna lettura. La lista resta da calcolare e si ricalcola alla prima visualizzazione.
        if (!SezioneListeVisibile) { _listeDaCalcolare = true; return; }
        _listeDaCalcolare = false;
        if (!_state.CartellaValida)
        {
            DaFare.ReplaceWith(Array.Empty<TestCase>());
            Fatti.ReplaceWith(Array.Empty<TestCase>());
            return;
        }
        if (TryAggiornaDaFareEFattiScalabile()) return;

        var veicoli = _state.Master.Veicoli.Load();
        string? GammaDi(string veicolo) => veicoli.FirstOrDefault(v => v.Nome == veicolo)?.GammaNome;

        var visibili = _state.TestCases.GetAll().Where(tc => NelloScopeGamma(tc, GammaDi));
        if (SoloMiei) visibili = visibili.Where(tc => tc.Assegnatario == _state.Config.OperatoreAttivo);
        if (!string.IsNullOrWhiteSpace(_testoRicercaApplicato)) visibili = visibili.Where(tc => Corrisponde(tc, _testoRicercaApplicato));
        var lista = visibili.ToList();

        DaFare.ReplaceWith(lista.Where(tc => tc.DaFare).OrderByDescending(tc => tc.UltimaModifica));
        Fatti.ReplaceWith(lista.Where(tc => tc.Fatto).OrderByDescending(tc => tc.UltimaModifica));
    }

    private bool Corrisponde(TestCase tc, string q) =>
        tc.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        tc.Componente.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        tc.VeicoliEffettivi.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
        tc.Descrizione.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        tc.RequisitoId.Any(r => r.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
        tc.Funzionalita.Any(f => f.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
        _state.NomeTestTemplate(tc.TestTemplateUid).Contains(q, StringComparison.OrdinalIgnoreCase) ||
        tc.Esecuzioni.Any(e => e.Note.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Deviazioni.Contains(q, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private void Apri(TestCase? tc)
    {
        if (tc != null) _apriTestCase(tc.Id);
    }

    [RelayCommand]
    private void NuovoTestCase() => _apriTestCase(null);

    /// <summary>Piano 6.4.0 punto 5(3): dall'avviso "Test senza componente" apre la pagina Test già filtrata sui
    /// record da correggere, invece di lasciare l'utente a cercarli manualmente.</summary>
    [RelayCommand]
    private void VaiATestSenzaEcu() => _vaiATestSenzaEcu();

    bool IScorciatoiePagina.NuovoDaTastiera()
    {
        if (!_state.PuoScrivere) return false;
        NuovoTestCaseCommand.Execute(null);
        return true;
    }
}
