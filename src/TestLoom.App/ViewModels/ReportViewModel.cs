using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

public class RigaAnteprima
{
    public string Id { get; init; } = "";
    public string Componente { get; init; } = "";
    public string Veicolo { get; init; } = "";
    public string Template { get; init; } = "";
    public string Esito { get; init; } = "";
    public string Data { get; init; } = "";
}

public class ReportGenerato
{
    public string NomeFile { get; init; } = "";
    public string Percorso { get; init; } = "";
    public DateTime Data { get; init; }
}

public sealed partial class DimensioneMatriceOpzione : ObservableObject
{
    public string Id { get; init; } = "";
    /// <summary>Etichetta della dimensione: un cambio (per esempio la rinomina di un campo di sistema) notifica la combo degli assi.</summary>
    [ObservableProperty] private string _etichetta = "";
    public override string ToString() => Etichetta;
}

/// <summary>Segmento di un'intestazione raggruppata della matrice (6.6.1), con misure aggiornabili senza ricreare i
/// controlli visuali durante il trascinamento. ChiaveDimensione e' valorizzata solo sui segmenti foglia ridimensionabili.</summary>
public sealed partial class SegmentoIntestazione : ObservableObject
{
    public SegmentoIntestazione(string etichetta, double larghezza, double altezza, bool comprimibile, bool compressa,
        string? chiaveComando, string? chiaveDimensione, int span, string? testo = null)
    {
        Etichetta = etichetta;
        Testo = string.IsNullOrEmpty(testo) ? etichetta : testo;
        Larghezza = larghezza;
        Altezza = altezza;
        Comprimibile = comprimibile;
        Compressa = compressa;
        ChiaveComando = chiaveComando;
        ChiaveDimensione = chiaveDimensione;
        Span = span;
    }

    /// <summary>Nome completo (tooltip, nome di automazione).</summary>
    public string Etichetta { get; }
    /// <summary>Testo mostrato nell'intestazione: per i componenti la sola sigla, per ridurre la larghezza della matrice.</summary>
    public string Testo { get; }
    [ObservableProperty] private double larghezza;
    [ObservableProperty] private double altezza;
    public bool Comprimibile { get; }
    public bool Compressa { get; }
    public string? ChiaveComando { get; }
    public string? ChiaveDimensione { get; }
    public int Span { get; }
    public bool Ridimensionabile => !string.IsNullOrEmpty(ChiaveDimensione);
}

public sealed record TotaleColonnaMatrice(string Chiave, TotaleMatriceReport Totale);

public sealed class FiltroDimensioneMatrice
{
    public DimensioneMatriceOpzione Dimensione { get; init; } = null!;
    public FiltroMultiSelect Filtro { get; } = new();
    /// <summary>Vero dopo il collegamento alla persistenza della selezione (una sola volta per filtro, vedi ApplicaListe).</summary>
    internal bool PersistenzaCollegata { get; set; }
}

/// <summary>Un campo di un asse della matrice (6.6.1): riga dell'elenco ordinabile, al massimo tre per asse. La
/// dimensione si cambia dal selettore della riga; <see cref="Precedente"/> ripristina la scelta precedente se la nuova
/// dimensione e' gia' usata da un altro campo della matrice.</summary>
public sealed partial class CampoAsseMatrice : ObservableObject
{
    private DimensioneMatriceOpzione _precedente;

    public CampoAsseMatrice(DimensioneMatriceOpzione dimensione, bool righe)
    {
        _dimensione = dimensione;
        _precedente = dimensione;
        Righe = righe;
    }

    private DimensioneMatriceOpzione _dimensione = null!;

    /// <summary>Dimensione del campo. Una scelta vuota non e' un valore valido: il selettore della vista la scrive quando la
    /// pagina Report viene staccata dall'albero visuale (il ComboBox azzera la selezione attraverso il binding), e il valore
    /// precedente va conservato. Accettarla farebbe dereferenziare un campo senza dimensione al giro successivo (al ritorno
    /// sul Report, NullReferenceException e banner di navigazione).</summary>
    public DimensioneMatriceOpzione Dimensione
    {
        get => _dimensione;
        set
        {
            if (value == null) return;
            SetProperty(ref _dimensione, value);
        }
    }

    /// <summary>Dimensione prima dell'ultima modifica accettata.</summary>
    public DimensioneMatriceOpzione Precedente { get => _precedente; set => _precedente = value; }

    /// <summary>True per un campo dell'asse righe (Y), false per l'asse colonne (X).</summary>
    public bool Righe { get; }

    /// <summary>AutomationId del selettore: il primo campo di ciascun asse mantiene l'identificativo storico (FlaUI).</summary>
    public string IdAutomazione { get; set; } = "";
}

/// <summary>Report (§7.6): filtri -> anteprima tabellare in-app -> salva Excel/PDF con timestamp, mai
/// sovrascrive, cronologia consultabile con link diretto ai file già generati.</summary>
public partial class ReportViewModel : ViewModelBase, IPaginaAggiornabile, IDisposable
{
    public const string ChiaveColonnaSenzaCampo = "\0x";
    public const string ChiaveRigaSenzaCampo = "\0y";

    private readonly AppState _state;
    private PropertyChangedEventHandler? _linguaCambiata;
    private bool _disposed;
    private LocalTestArchiveQueryService? _archiveQuery;
    private readonly EsportazioneInBackground _esportazione = new();

    [ObservableProperty] private string? filtroEcu;
    [ObservableProperty] private string? filtroVeicolo;
    [ObservableProperty] private DateTimeOffset? dataDa;
    [ObservableProperty] private DateTimeOffset? dataA;
    [ObservableProperty] private bool mostraFiltriAvanzati;
    [ObservableProperty] private string dataDaTesto = "";
    [ObservableProperty] private string dataATesto = "";
    [ObservableProperty] private bool avvisoVolumeGrande;
    [ObservableProperty] private string? messaggio;
    public bool HaMessaggio => !string.IsNullOrEmpty(Messaggio);

    /// <summary>Genera un feedback visibile durante l'export (Addendum: audit UX 2026-08-26, punto 4): prima
    /// GeneraExcel/GeneraPdf giravano sincroni sul thread UI, quindi su un archivio grande l'interfaccia
    /// restava ferma senza alcun segnale per tutta la durata — sembrava un blocco, non un lavoro in corso.</summary>
    [ObservableProperty] private bool generazioneInCorso;

    /// <summary>Distinto da Messaggio (che la vista mostra sempre in verde, come conferma di successo): un
    /// errore di generazione/apertura file non deve mai apparire con lo stesso colore di un "salvato con
    /// successo", altrimenti sembra il contrario di quello che è.</summary>
    [ObservableProperty] private string? messaggioErrore;
    public bool HaMessaggioErrore => !string.IsNullOrEmpty(MessaggioErrore);

    /// <summary>Sostituisce i due checkbox fissi "Solo Banco"/"Solo Veicolo" (Addendum redesign 2026-08, punto
    /// 2: i tipi di test non sono più un enum fisso, quindi il filtro deve coprire un elenco aperto di
    /// template) — stesso controllo FiltroMultiSelect già usato dai filtri dashboard della Home.</summary>
    public FiltroMultiSelect FiltroTemplate { get; } = new();

    /// <summary>Un filtro per ciascun campo custom del catalogo (Ruolo=Nessuno), stesso meccanismo di
    /// HomeViewModel.FiltriCampiCustomDashboard (Addendum: grill-me 2026-08-18, seconda follow-up, punto 6).</summary>
    public ObservableCollection<FiltroCampoCustom> FiltriCampiCustomReport { get; } = new();
    public ObservableCollection<DimensioneMatriceOpzione> OpzioniDimensioniMatrice { get; } = new();
    public ObservableCollection<FiltroDimensioneMatrice> FiltriMatrice { get; } = new();

    /// <summary>Filtro per stato della matrice (multi-selezione, nessuna selezione = tutti gli stati visibili): nasconde le righe e le
    /// colonne in cui nessuna cella ha uno degli stati scelti. A differenza della maschera "Nascondi voci di stato" agisce sugli assi.</summary>
    public FiltroMultiSelect FiltroStatiMatrice { get; } = new();

    private const string ChiaveVistaFiltroStatiMatrice = "Report.FiltroStatiMatrice";

    private static readonly (StatoCellaMatrice Stato, string ChiaveEtichetta)[] StatiFiltrabiliMatrice =
    [
        (StatoCellaMatrice.Conforme, "MatriceLegendaConforme"),
        (StatoCellaMatrice.NonConforme, "MatriceLegendaNonConforme"),
        (StatoCellaMatrice.ConformeConDeviazioni, "MatriceLegendaDeviazioni"),
        (StatoCellaMatrice.DaEseguire, "MatriceLegendaDaEseguire"),
        (StatoCellaMatrice.NonTestato, "MatriceLegendaNonTestato"),
        (StatoCellaMatrice.DaDecidere, "MatriceLegendaDecidere"),
    ];

    /// <summary>Stati selezionati nel filtro (vuoto = nessun filtro).</summary>
    internal HashSet<StatoCellaMatrice> StatiFiltroMatriceSelezionati() =>
        FiltroStatiMatrice.Selezionati
            .Select(v => Enum.TryParse<StatoCellaMatrice>(v, out var stato) ? stato : (StatoCellaMatrice?)null)
            .OfType<StatoCellaMatrice>().ToHashSet();

    /// <summary>Cambio lingua: ritraduce le etichette del filtro stati mantenendo la selezione (Ripopola non fa nulla se invariate).</summary>
    private void RietichettaFiltroStatiMatrice() =>
        FiltroStatiMatrice.Ripopola(StatiFiltrabiliMatrice.Select(s => (s.Stato.ToString(), LocalizationService.Instance.Traduci(s.ChiaveEtichetta))).ToList());

    private void PopolaFiltroStatiMatrice() =>
        FiltroStatiMatrice.Popola(StatiFiltrabiliMatrice.Select(s => (s.Stato.ToString(), LocalizationService.Instance.Traduci(s.ChiaveEtichetta))).ToList());
    /// <summary>Campi dell'asse X (colonne) e dell'asse Y (righe), in ordine di annidamento (6.6.1: da 0 a 3 per asse; almeno
    /// un campo in tutta la matrice).</summary>
    public ObservableCollection<CampoAsseMatrice> CampiXMatrice { get; } = new();
    public ObservableCollection<CampoAsseMatrice> CampiYMatrice { get; } = new();

    /// <summary>Primo campo di ciascun asse: compatibilita' con chi legge o imposta un solo campo per asse.</summary>
    public DimensioneMatriceOpzione? AsseXMatrice
    {
        get => CampiXMatrice.FirstOrDefault()?.Dimensione;
        set => ImpostaPrimoCampo(righe: false, value);
    }
    public DimensioneMatriceOpzione? AsseYMatrice
    {
        get => CampiYMatrice.FirstOrDefault()?.Dimensione;
        set => ImpostaPrimoCampo(righe: true, value);
    }

    /// <summary>Contenuto delle celle (6.6.1): Percentuale (default) oppure Stato.</summary>
    [ObservableProperty] private ModalitaCellaMatrice modalitaMatrice = ModalitaCellaMatrice.Percentuale;

    /// <summary>TIME (6.6.1): base dei tempi, usata solo dalle modalita' Tempo totale e Tempo medio (preferenza locale della vista).</summary>
    [ObservableProperty] private BaseTempoMatrice baseTempo = BaseTempoMatrice.TutteLeEsecuzioni;
    [ObservableProperty] private bool nascondiOkMatrice;
    [ObservableProperty] private bool nascondiNokMatrice;
    [ObservableProperty] private bool nascondiDeviazioniMatrice;
    [ObservableProperty] private bool nascondiDaEseguireMatrice;
    [ObservableProperty] private bool nascondiNonTestatoMatrice;
    [ObservableProperty] private bool nascondiDaDecidereMatrice;

    private readonly HashSet<StatoCellaMatrice> _statiNascostiPresentazione = [];

    private void AggiornaStatiNascostiPresentazione()
    {
        _statiNascostiPresentazione.Clear();
        if (NascondiOkMatrice) _statiNascostiPresentazione.Add(StatoCellaMatrice.Conforme);
        if (NascondiNokMatrice) _statiNascostiPresentazione.Add(StatoCellaMatrice.NonConforme);
        if (NascondiDeviazioniMatrice) _statiNascostiPresentazione.Add(StatoCellaMatrice.ConformeConDeviazioni);
        if (NascondiDaEseguireMatrice) _statiNascostiPresentazione.Add(StatoCellaMatrice.DaEseguire);
        if (NascondiNonTestatoMatrice) _statiNascostiPresentazione.Add(StatoCellaMatrice.NonTestato);
        if (NascondiDaDecidereMatrice) _statiNascostiPresentazione.Add(StatoCellaMatrice.DaDecidere);
        ApplicaMatrice();
    }

    partial void OnNascondiOkMatriceChanged(bool value) => AggiornaStatiNascostiPresentazione();
    partial void OnNascondiNokMatriceChanged(bool value) => AggiornaStatiNascostiPresentazione();
    partial void OnNascondiDeviazioniMatriceChanged(bool value) => AggiornaStatiNascostiPresentazione();
    partial void OnNascondiDaEseguireMatriceChanged(bool value) => AggiornaStatiNascostiPresentazione();
    partial void OnNascondiNonTestatoMatriceChanged(bool value) => AggiornaStatiNascostiPresentazione();
    partial void OnNascondiDaDecidereMatriceChanged(bool value) => AggiornaStatiNascostiPresentazione();

    /// <summary>TIME (6.6.1): filtri numerici sulle durate. Testo vuoto = nessun filtro; operatore 0 = maggiore di, 1 = minore di.</summary>
    [ObservableProperty] private string testoFiltroTempoOperazione = "";
    [ObservableProperty] private int operatoreFiltroTempoOperazione;
    [ObservableProperty] private string testoFiltroTempoTotaleTest = "";
    [ObservableProperty] private int operatoreFiltroTempoTotaleTest;

    /// <summary>Filtro TIME attivo sulla durata delle operazioni (null se il testo non e' un intero non negativo).</summary>
    public FiltroDurataMinuti? FiltroTempoOperazioneAttivo => CostruisciFiltroTempo(TestoFiltroTempoOperazione, OperatoreFiltroTempoOperazione);

    /// <summary>Filtro TIME attivo sul totale minuti del Test (null se il testo non e' un intero non negativo).</summary>
    public FiltroDurataMinuti? FiltroTempoTotaleTestAttivo => CostruisciFiltroTempo(TestoFiltroTempoTotaleTest, OperatoreFiltroTempoTotaleTest);

    /// <summary>Errore di digitazione nei minuti (es. "1,5", "-3", "abc"): il filtro resta ignorato, la vista lo segnala in rosso.</summary>
    public bool FiltroTempoOperazioneHaErrore => !FiltroDurataMinuti.TestoValido(TestoFiltroTempoOperazione);
    public bool FiltroTempoTotaleTestHaErrore => !FiltroDurataMinuti.TestoValido(TestoFiltroTempoTotaleTest);

    private static FiltroDurataMinuti? CostruisciFiltroTempo(string? testo, int operatore) => FiltroDurataMinuti.DaTesto(testo, operatore);

    /// <summary>Vero quando la matrice mostra Tempo totale o Tempo medio: solo allora compaiono la base e il suo selettore.</summary>
    public bool ModalitaTempoMatrice => ReportMatriceService.ModalitaTempo(ModalitaMatrice);

    /// <summary>Indice del selettore di base (0 = Tutte le esecuzioni, 1 = Ultimo stato).</summary>
    public int IndiceBaseTempo
    {
        get => BaseTempo == BaseTempoMatrice.UltimoStato ? 1 : 0;
        set => BaseTempo = value == 1 ? BaseTempoMatrice.UltimoStato : BaseTempoMatrice.TutteLeEsecuzioni;
    }

    [ObservableProperty] private List<List<FasciaMatrice>> fasceColonneMatrice = new();
    [ObservableProperty] private List<List<FasciaMatrice>> fasceRigheMatrice = new();

    /// <summary>Gruppi compressi della vista (chiavi di ReportMatriceService.ChiaveCompressione): preferenza locale.</summary>
    public HashSet<string> GruppiCompressiMatrice { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>Misure predefinite per colonne e righe senza una preferenza specifica, in pixel. La finestra resta limitata a 50 righe x 12 colonne.</summary>
    public const double LarghezzaCellaMatricePredefinita = 145;
    public const double AltezzaRigaMatricePredefinita = 46;
    public const double AltezzaViewportMatricePredefinita = 560;
    public const double LarghezzaCellaMatriceMinima = 72;
    public const double LarghezzaCellaMatriceMassima = 360;
    public const double AltezzaRigaMatriceMinima = 30;
    public const double AltezzaRigaMatriceMassima = 180;
    public const double AltezzaViewportMatriceMinima = 260;
    public const double AltezzaViewportMatriceMassima = 1000;
    private const string ChiavePreferenzaDimensioniMatrice = "Report.DimensioniMatrice.v2";
    private const string ChiavePreferenzaDimensioniMatricePrecedente = "Report.DimensioniMatrice.v1";
    private double _larghezzaCellaMatrice = LarghezzaCellaMatricePredefinita;
    private double _altezzaRigaMatrice = AltezzaRigaMatricePredefinita;
    private double _altezzaViewportPreferitaMatrice = AltezzaViewportMatricePredefinita;
    private readonly Dictionary<string, double> _larghezzeColonneMatrice = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _altezzeRigheMatrice = new(StringComparer.Ordinal);
    [ObservableProperty] private int dimensioniMatriceVersione;
    public double LarghezzaCellaMatrice => _larghezzaCellaMatrice;
    public double AltezzaRigaMatrice => _altezzaRigaMatrice;
    public double AltezzaViewportMatrice => _altezzaViewportPreferitaMatrice;
    private const double LarghezzaLivelloRigaMatrice = 150;
    private const double AltezzaLivelloColonnaMatrice = 30;

    [ObservableProperty] private List<List<SegmentoIntestazione>> segmentiColonneMatrice = new();
    [ObservableProperty] private List<List<SegmentoIntestazione>> segmentiRigheMatrice = new();

    /// <summary>Larghezza dell'area delle intestazioni di riga (un livello per colonna; almeno una colonna per il totale).</summary>
    public double LarghezzaAreaRigheMatrice => Math.Max(1, FasceRigheMatrice.Count) * LarghezzaLivelloRigaMatrice;

    public bool ModalitaStatoMatrice => ModalitaMatrice == ModalitaCellaMatrice.Stato;
    public bool SenzaCampiXMatrice => CampiXMatrice.Count == 0;
    public bool SenzaCampiYMatrice => CampiYMatrice.Count == 0;

    partial void OnFasceColonneMatriceChanged(List<List<FasciaMatrice>> value) =>
        SegmentiColonneMatrice = CreaSegmenti(value, righe: false);

    partial void OnFasceRigheMatriceChanged(List<List<FasciaMatrice>> value)
    {
        SegmentiRigheMatrice = CreaSegmenti(value, righe: true);
        OnPropertyChanged(nameof(LarghezzaAreaRigheMatrice));
    }

    private List<List<SegmentoIntestazione>> CreaSegmenti(IEnumerable<List<FasciaMatrice>> livelli, bool righe)
    {
        var fasceLivelli = livelli.ToList();
        var risultato = new List<List<SegmentoIntestazione>>(fasceLivelli.Count);
        foreach (var livello in fasceLivelli)
        {
            var segmenti = new List<SegmentoIntestazione>(livello.Count);
            var indice = 0;
            foreach (var fascia in livello)
            {
                var span = Math.Max(1, fascia.Span);
                var dimensioneAsse = righe ? RigheMatrice.Count : ColonneMatrice.Count;
                var misura = 0d;
                for (var i = indice; i < Math.Min(dimensioneAsse, indice + span); i++)
                    misura += righe ? AltezzaPerRigaMatrice(RigheMatrice[i].Riga) : LarghezzaPerColonnaMatrice(ColonneMatrice[i]);
                if (misura <= 0) misura = span * (righe ? AltezzaRigaMatrice : LarghezzaCellaMatrice);

                var chiaveDimensione = !righe && fascia.Livello == fasceLivelli.Count && span == 1 && indice < ColonneMatrice.Count
                    ? ChiaveColonnaNormalizzata(ColonneMatrice[indice]) : null;
                var campiAsse = righe ? CampiYMatrice : CampiXMatrice;
                var campoLivello = fascia.Livello >= 1 && fascia.Livello <= campiAsse.Count ? campiAsse[fascia.Livello - 1] : null;
                var testoMostrato = campoLivello?.Dimensione?.Id == DimensioniMatrice.Componente ? PolarionTicketTitle.SiglaEcu(fascia.Etichetta) : null;
                segmenti.Add(new SegmentoIntestazione(fascia.Etichetta,
                    righe ? LarghezzaLivelloRigaMatrice : misura,
                    righe ? misura : AltezzaLivelloColonnaMatrice,
                    fascia.Comprimibile, fascia.Compressa,
                    fascia.Comprimibile ? ReportMatriceService.ChiaveCompressione(righe, fascia.Chiave) : null,
                    righe ? null : chiaveDimensione, span, testoMostrato));
                indice += span;
            }
            risultato.Add(segmenti);
        }
        return risultato;
    }
    [ObservableProperty] private List<string> colonneMatrice = new();
    [ObservableProperty] private List<RigaMatriceReport> righeMatrice = new();
    /// <summary>Totale per colonna, stesso ordine/indice di ColonneMatrice (addendum 6.4.0, totali di
    /// intestazione). Lista separata (non un tipo colonna combinato) per non toccare i molti punti che oggi
    /// leggono ColonneMatrice come List&lt;string&gt; (export Excel/PDF, ValoriDisponibili, vista).</summary>
    [ObservableProperty] private List<TotaleMatriceReport> totaliColonnaMatrice = new();
    [ObservableProperty] private List<TotaleColonnaMatrice> totaliColonnaConChiaveMatrice = new();
    [ObservableProperty] private TotaleMatriceReport totaleGeneraleMatrice = new();
    public bool MatriceVuota => RigheMatrice.Count == 0 || ColonneMatrice.Count == 0;
    private const int MassimoRighePerFinestraMatrice = 50;
    private const int MassimoColonnePerFinestraMatrice = 12;
    private int _righePerFinestraMatrice = 10;
    private int _colonnePerFinestraMatrice = 6;
    private double _larghezzaViewportMatrice;
    private double _altezzaViewportMatrice;
    private bool _aggiornamentoOffsetMatriceInterno;
    private ReportMatriceService.MatricePreparata? _matricePreparata;
    private int _totaleRigheMatrice;
    private int _totaleColonneMatrice;
    private int _righeUltimaFinestraMatrice = 10;
    private int _colonneUltimaFinestraMatrice = 6;
    private double _residuoScorrimentoRigheMatrice;
    private double _residuoScorrimentoColonneMatrice;
    [ObservableProperty] private double offsetRigheMatrice;
    [ObservableProperty] private double offsetColonneMatrice;
    public int RigheVisibiliMatrice => _righePerFinestraMatrice;
    public int ColonneVisibiliMatrice => _colonnePerFinestraMatrice;
    public int MassimoOffsetRigheMatrice => Math.Max(0, _totaleRigheMatrice - _righeUltimaFinestraMatrice);
    public int MassimoOffsetColonneMatrice => Math.Max(0, _totaleColonneMatrice - _colonneUltimaFinestraMatrice);

    partial void OnOffsetRigheMatriceChanged(double value) => ApplicaOffsetScorrimentoMatrice(righe: true, value);
    partial void OnOffsetColonneMatriceChanged(double value) => ApplicaOffsetScorrimentoMatrice(righe: false, value);

    /// <summary>Adatta la finestra materializzata allo spazio del controllo. La dimensione massima resta limitata
    /// a 50×12 celle; le barre rappresentano tutto l'asse e spostano la finestra di una riga/colonna alla volta.</summary>
    public void AggiornaDimensioniViewportMatrice(double larghezza, double altezza)
    {
        if (!double.IsFinite(larghezza) || !double.IsFinite(altezza) || larghezza <= 0 || altezza <= 0) return;
        _larghezzaViewportMatrice = larghezza;
        _altezzaViewportMatrice = altezza;
        if (AggiornaDimensioniFinestraMatrice(aggiornaCoda: true) && _matricePreparata != null)
            ApplicaMatrice(dimensioniAggiornate: true);
    }

    /// <summary>Applica le misure predefinite per gli assi e l'altezza del viewport. Le misure per singola colonna e riga
    /// sono gestite separatamente, così il conteggio della finestra resta limitato a 50×12.</summary>
    public void ImpostaDimensioniMatrice(double? larghezzaColonna = null, double? altezzaRiga = null, double? altezzaViewport = null)
    {
        var larghezza = Limita(larghezzaColonna, _larghezzaCellaMatrice, LarghezzaCellaMatriceMinima, LarghezzaCellaMatriceMassima);
        var altezza = Limita(altezzaRiga, _altezzaRigaMatrice, AltezzaRigaMatriceMinima, AltezzaRigaMatriceMassima);
        var viewport = Limita(altezzaViewport, _altezzaViewportPreferitaMatrice, AltezzaViewportMatriceMinima, AltezzaViewportMatriceMassima);
        var larghezzaCambiata = Math.Abs(larghezza - _larghezzaCellaMatrice) > 0.1;
        var altezzaCambiata = Math.Abs(altezza - _altezzaRigaMatrice) > 0.1;
        var viewportCambiato = Math.Abs(viewport - _altezzaViewportPreferitaMatrice) > 0.1;
        if (!larghezzaCambiata && !altezzaCambiata && !viewportCambiato) return;

        if (larghezzaCambiata)
        {
            _larghezzaCellaMatrice = larghezza;
            OnPropertyChanged(nameof(LarghezzaCellaMatrice));
            SegmentiColonneMatrice = CreaSegmenti(FasceColonneMatrice, righe: false);
        }
        if (altezzaCambiata)
        {
            _altezzaRigaMatrice = altezza;
            OnPropertyChanged(nameof(AltezzaRigaMatrice));
            SegmentiRigheMatrice = CreaSegmenti(FasceRigheMatrice, righe: true);
        }
        if (viewportCambiato)
        {
            _altezzaViewportPreferitaMatrice = viewport;
            OnPropertyChanged(nameof(AltezzaViewportMatrice));
        }

        if (AggiornaDimensioniFinestraMatrice(aggiornaCoda: true) && _matricePreparata != null)
            ApplicaMatrice(dimensioniAggiornate: true);
    }

    public double LarghezzaPerColonnaMatrice(string chiave) =>
        _larghezzeColonneMatrice.TryGetValue(ChiaveColonnaNormalizzata(chiave), out var larghezza)
            ? Math.Clamp(larghezza, LarghezzaCellaMatriceMinima, LarghezzaCellaMatriceMassima)
            : LarghezzaCellaMatrice;

    public double AltezzaPerRigaMatrice(string chiave) =>
        _altezzeRigheMatrice.TryGetValue(ChiaveRigaNormalizzata(chiave), out var altezza)
            ? Math.Clamp(altezza, AltezzaRigaMatriceMinima, AltezzaRigaMatriceMassima)
            : AltezzaRigaMatrice;

    public void ImpostaLarghezzaColonnaMatrice(string chiave, double larghezza)
    {
        if (!double.IsFinite(larghezza)) return;
        chiave = ChiaveColonnaNormalizzata(chiave);
        larghezza = Math.Clamp(larghezza, LarghezzaCellaMatriceMinima, LarghezzaCellaMatriceMassima);
        if (Math.Abs(larghezza - LarghezzaCellaMatrice) < 0.1) _larghezzeColonneMatrice.Remove(chiave);
        else _larghezzeColonneMatrice[chiave] = larghezza;
        AggiornaMisureSegmenti(righe: false);
        if (AggiornaDimensioniFinestraMatrice(aggiornaCoda: true) && _matricePreparata != null)
            ApplicaMatrice(dimensioniAggiornate: true);
    }

    public void ImpostaAltezzaRigaMatrice(string chiave, double altezza)
    {
        if (!double.IsFinite(altezza)) return;
        chiave = ChiaveRigaNormalizzata(chiave);
        altezza = Math.Clamp(altezza, AltezzaRigaMatriceMinima, AltezzaRigaMatriceMassima);
        if (Math.Abs(altezza - AltezzaRigaMatrice) < 0.1) _altezzeRigheMatrice.Remove(chiave);
        else _altezzeRigheMatrice[chiave] = altezza;
        AggiornaMisureSegmenti(righe: true);
        if (AggiornaDimensioniFinestraMatrice(aggiornaCoda: true) && _matricePreparata != null)
            ApplicaMatrice(dimensioniAggiornate: true);
    }

    private void AggiornaMisureSegmenti(bool righe)
    {
        var segmenti = righe ? SegmentiRigheMatrice : SegmentiColonneMatrice;
        var valori = righe ? RigheMatrice.Select(r => r.Riga).ToList() : ColonneMatrice;
        var fasce = righe ? FasceRigheMatrice : FasceColonneMatrice;
        for (var livello = 0; livello < Math.Min(segmenti.Count, fasce.Count); livello++)
        {
            var indice = 0;
            for (var i = 0; i < Math.Min(segmenti[livello].Count, fasce[livello].Count); i++)
            {
                var span = Math.Max(1, fasce[livello][i].Span);
                var misura = 0d;
                for (var j = indice; j < Math.Min(valori.Count, indice + span); j++)
                    misura += righe ? AltezzaPerRigaMatrice(valori[j]) : LarghezzaPerColonnaMatrice(valori[j]);
                if (misura > 0)
                {
                    if (righe) segmenti[livello][i].Altezza = misura;
                    else segmenti[livello][i].Larghezza = misura;
                }
                indice += span;
            }
        }
        DimensioniMatriceVersione++;
    }

    private static string ChiaveColonnaNormalizzata(string chiave) => string.IsNullOrEmpty(chiave) ? ChiaveColonnaSenzaCampo : chiave;
    private static string ChiaveRigaNormalizzata(string chiave) => string.IsNullOrEmpty(chiave) ? ChiaveRigaSenzaCampo : chiave;

    /// <summary>Persiste le misure in config locale, con chiave distinta per operatore.</summary>
    public void SalvaDimensioniMatrice()
    {
        var chiave = $"{ChiavePreferenzaDimensioniMatrice}|{_state.Config.OperatoreAttivo}";
        var preferenza = new PreferenzaDimensioniMatrice(2, _larghezzaCellaMatrice, _altezzaRigaMatrice,
            _altezzaViewportPreferitaMatrice, new(_larghezzeColonneMatrice, StringComparer.Ordinal),
            new(_altezzeRigheMatrice, StringComparer.Ordinal));
        try { ViewStateService.Scrivi(_state, chiave, JsonSerializer.Serialize(preferenza)); }
        catch (Exception ex) { CrashLogService.Registra("Salvataggio dimensioni matrice report", ex); }
    }

    private void CaricaPreferenzeDimensioniMatrice()
    {
        var chiave = $"{ChiavePreferenzaDimensioniMatrice}|{_state.Config.OperatoreAttivo}";
        var chiavePrecedente = $"{ChiavePreferenzaDimensioniMatricePrecedente}|{_state.Config.OperatoreAttivo}";
        var testo = ViewStateService.Leggi(_state, chiave) ?? ViewStateService.Leggi(_state, chiavePrecedente);
        if (string.IsNullOrWhiteSpace(testo)) return;
        try
        {
            var preferenza = JsonSerializer.Deserialize<PreferenzaDimensioniMatrice>(testo);
            if (preferenza is not { Versione: 1 or 2 }) return;
            _larghezzaCellaMatrice = Limita(preferenza.LarghezzaColonna, LarghezzaCellaMatricePredefinita,
                LarghezzaCellaMatriceMinima, LarghezzaCellaMatriceMassima);
            _altezzaRigaMatrice = Limita(preferenza.AltezzaRiga, AltezzaRigaMatricePredefinita,
                AltezzaRigaMatriceMinima, AltezzaRigaMatriceMassima);
            _altezzaViewportPreferitaMatrice = Limita(preferenza.AltezzaViewport, AltezzaViewportMatricePredefinita,
                AltezzaViewportMatriceMinima, AltezzaViewportMatriceMassima);
            _larghezzeColonneMatrice.Clear();
            foreach (var (chiaveColonna, larghezza) in preferenza.LarghezzeColonne ?? [])
                if (!string.IsNullOrEmpty(chiaveColonna) && double.IsFinite(larghezza))
                    _larghezzeColonneMatrice[chiaveColonna] = Math.Clamp(larghezza, LarghezzaCellaMatriceMinima, LarghezzaCellaMatriceMassima);
            _altezzeRigheMatrice.Clear();
            foreach (var (chiaveRiga, altezza) in preferenza.AltezzeRighe ?? [])
                if (!string.IsNullOrEmpty(chiaveRiga) && double.IsFinite(altezza))
                    _altezzeRigheMatrice[chiaveRiga] = Math.Clamp(altezza, AltezzaRigaMatriceMinima, AltezzaRigaMatriceMassima);
        }
        catch (JsonException ex) { CrashLogService.Registra("Lettura dimensioni matrice report", ex); }
    }

    private static double Limita(double? valore, double corrente, double minimo, double massimo) =>
        valore is { } v && double.IsFinite(v) ? Math.Clamp(v, minimo, massimo) : corrente;

    private sealed record PreferenzaDimensioniMatrice(int Versione, double LarghezzaColonna, double AltezzaRiga, double AltezzaViewport,
        Dictionary<string, double>? LarghezzeColonne = null, Dictionary<string, double>? AltezzeRighe = null);

    private bool AggiornaDimensioniFinestraMatrice(bool aggiornaCoda = false)
    {
        if (_larghezzaViewportMatrice <= 0 || _altezzaViewportMatrice <= 0) return false;

        const double spazioBarra = 18;
        var altezzaIntestazioni = Math.Max(1, CampiXMatrice.Count) * AltezzaLivelloColonnaMatrice + 48;
        var altezzaDati = Math.Max(_altezzaRigaMatrice,
            _altezzaViewportMatrice - spazioBarra - altezzaIntestazioni);
        var larghezzaIntestazioniRiga = Math.Max(1, CampiYMatrice.Count) * LarghezzaLivelloRigaMatrice;
        var larghezzaDati = Math.Max(_larghezzaCellaMatrice,
            _larghezzaViewportMatrice - spazioBarra - larghezzaIntestazioniRiga - _larghezzaCellaMatrice);

        var righe = Math.Clamp((int)Math.Floor(altezzaDati / _altezzaRigaMatrice), 1, MassimoRighePerFinestraMatrice);
        var colonne = Math.Clamp((int)Math.Floor(larghezzaDati / _larghezzaCellaMatrice), 1, MassimoColonnePerFinestraMatrice);
        var righeUltima = _righeUltimaFinestraMatrice;
        var colonneUltima = _colonneUltimaFinestraMatrice;
        var totaleRighe = _totaleRigheMatrice;
        var totaleColonne = _totaleColonneMatrice;
        var primaRiga = IndiceOffsetMatrice(OffsetRigheMatrice);
        var primaColonna = IndiceOffsetMatrice(OffsetColonneMatrice);

        if (_matricePreparata is { } matrice)
        {
            var gruppiCompressi = GruppiCompressiMatrice;
            var soloDaDecidere = SoloCelleConDaDecidere;
            var richiesta = matrice.AssiFinestra(primaRiga, MassimoRighePerFinestraMatrice,
                primaColonna, MassimoColonnePerFinestraMatrice, gruppiCompressi, soloDaDecidere);
            totaleRighe = richiesta.TotaleRighe;
            totaleColonne = richiesta.TotaleColonne;

            // Il limite dello scorrimento dipende dalle misure delle ultime righe/colonne. Ricalcolalo quando
            // cambiano le dimensioni o la matrice; durante lo scorrimento si riusa questa coda.
            if (aggiornaCoda || totaleRighe != _totaleRigheMatrice || totaleColonne != _totaleColonneMatrice)
            {
                var ultima = matrice.AssiFinestra(
                    Math.Max(0, totaleRighe - MassimoRighePerFinestraMatrice), MassimoRighePerFinestraMatrice,
                    Math.Max(0, totaleColonne - MassimoColonnePerFinestraMatrice), MassimoColonnePerFinestraMatrice,
                    gruppiCompressi, soloDaDecidere);
                righeUltima = ContaElementiCodaFinestra(ultima.Righe, altezzaDati, MassimoRighePerFinestraMatrice,
                    AltezzaPerRigaMatrice);
                colonneUltima = ContaElementiCodaFinestra(ultima.Colonne, larghezzaDati, MassimoColonnePerFinestraMatrice,
                    LarghezzaPerColonnaMatrice);
            }
            var massimoRighe = Math.Max(0, totaleRighe - righeUltima);
            var massimoColonne = Math.Max(0, totaleColonne - colonneUltima);
            primaRiga = Math.Clamp(primaRiga, 0, massimoRighe);
            primaColonna = Math.Clamp(primaColonna, 0, massimoColonne);

            var finestraCorrente = primaRiga == IndiceOffsetMatrice(OffsetRigheMatrice) &&
                primaColonna == IndiceOffsetMatrice(OffsetColonneMatrice)
                ? richiesta
                : matrice.AssiFinestra(primaRiga, MassimoRighePerFinestraMatrice,
                    primaColonna, MassimoColonnePerFinestraMatrice, gruppiCompressi, soloDaDecidere);
            righe = ContaElementiFinestra(finestraCorrente.Righe, altezzaDati, MassimoRighePerFinestraMatrice,
                AltezzaRigaMatrice, AltezzaPerRigaMatrice);
            colonne = ContaElementiFinestra(finestraCorrente.Colonne, larghezzaDati, MassimoColonnePerFinestraMatrice,
                LarghezzaCellaMatrice, LarghezzaPerColonnaMatrice);
        }

        var misureCambiate = righe != _righePerFinestraMatrice || colonne != _colonnePerFinestraMatrice ||
            righeUltima != _righeUltimaFinestraMatrice || colonneUltima != _colonneUltimaFinestraMatrice ||
            totaleRighe != _totaleRigheMatrice || totaleColonne != _totaleColonneMatrice;
        var offsetCambiato = primaRiga != IndiceOffsetMatrice(OffsetRigheMatrice) ||
            primaColonna != IndiceOffsetMatrice(OffsetColonneMatrice);
        if (!misureCambiate && !offsetCambiato) return false;

        _righePerFinestraMatrice = righe;
        _colonnePerFinestraMatrice = colonne;
        _righeUltimaFinestraMatrice = righeUltima;
        _colonneUltimaFinestraMatrice = colonneUltima;
        _totaleRigheMatrice = totaleRighe;
        _totaleColonneMatrice = totaleColonne;
        if (offsetCambiato) ImpostaOffsetMatriceInterno(primaRiga, primaColonna);
        NotificaFinestraMatrice();
        return true;
    }

    private static int ContaElementiFinestra(IReadOnlyList<string> elementi, double spazio, int massimo,
        double dimensionePredefinita, Func<string, double> dimensione)
    {
        var conteggio = 0;
        var occupato = 0d;
        while (conteggio < massimo)
        {
            // Alla fine dell'asse possono rimanere meno elementi della capienza. Le misure predefinite
            // mantengono la dimensione della finestra, mentre il limite di scorrimento usa le sole voci reali.
            var misura = conteggio < elementi.Count ? dimensione(elementi[conteggio]) : dimensionePredefinita;
            misura = Math.Max(1, misura);
            if (conteggio > 0 && occupato + misura > spazio) break;
            occupato += misura;
            conteggio++;
        }
        return Math.Max(1, conteggio);
    }

    private static int ContaElementiCodaFinestra(IReadOnlyList<string> elementi, double spazio, int massimo,
        Func<string, double> dimensione)
    {
        var conteggio = 0;
        var occupato = 0d;
        for (var i = elementi.Count - 1; i >= 0 && conteggio < massimo; i--)
        {
            var misura = Math.Max(1, dimensione(elementi[i]));
            if (conteggio > 0 && occupato + misura > spazio) break;
            occupato += misura;
            conteggio++;
        }
        return conteggio;
    }

    private void ApplicaOffsetScorrimentoMatrice(bool righe, double value)
    {
        if (_aggiornamentoOffsetMatriceInterno || _matricePreparata == null) return;
        if (righe) _residuoScorrimentoRigheMatrice = 0;
        else _residuoScorrimentoColonneMatrice = 0;
        var massimo = righe ? MassimoOffsetRigheMatrice : MassimoOffsetColonneMatrice;
        var intero = double.IsFinite(value) ? (int)Math.Clamp(Math.Floor(value), 0, massimo) : 0;
        ImpostaOffsetMatriceInterno(righe ? intero : OffsetRigheMatrice, righe ? OffsetColonneMatrice : intero);
        ApplicaMatrice(scorrimento: true);
    }

    /// <summary>Traduce rotella e trackpad in spostamenti della matrice. Gli offset frazionari tipici del
    /// trackpad vengono accumulati fino a una riga/colonna; gli eventi che possono ancora scorrere restano
    /// gestiti qui e non fanno muovere la pagina esterna.</summary>
    public bool ScorriMatriceDaRotella(double deltaX, double deltaY, bool spostaSoloColonne = false)
    {
        if (_matricePreparata == null) return false;

        var spostamentoRighe = spostaSoloColonne ? 0 : -deltaY * 3;
        var spostamentoColonne = Math.Abs(deltaX) > double.Epsilon
            ? -deltaX * 3
            : spostaSoloColonne ? -deltaY * 3 : 0;

        var righePossibili = PuòScorrere(OffsetRigheMatrice, MassimoOffsetRigheMatrice, spostamentoRighe);
        var colonnePossibili = PuòScorrere(OffsetColonneMatrice, MassimoOffsetColonneMatrice, spostamentoColonne);
        if (!righePossibili) _residuoScorrimentoRigheMatrice = 0;
        if (!colonnePossibili) _residuoScorrimentoColonneMatrice = 0;

        var passiRighe = righePossibili
            ? PassiScorrimento(spostamentoRighe, ref _residuoScorrimentoRigheMatrice)
            : 0;
        var passiColonne = colonnePossibili
            ? PassiScorrimento(spostamentoColonne, ref _residuoScorrimentoColonneMatrice)
            : 0;
        var righe = Math.Clamp((int)OffsetRigheMatrice + passiRighe, 0, MassimoOffsetRigheMatrice);
        var colonne = Math.Clamp((int)OffsetColonneMatrice + passiColonne, 0, MassimoOffsetColonneMatrice);

        if (righe != OffsetRigheMatrice || colonne != OffsetColonneMatrice)
        {
            ImpostaOffsetMatriceInterno(righe, colonne);
            ApplicaMatrice(scorrimento: true);
        }

        return righePossibili || colonnePossibili;
    }

    private static bool PuòScorrere(double offset, int massimo, double spostamento) =>
        (spostamento > 0 && offset < massimo) || (spostamento < 0 && offset > 0);

    private static int PassiScorrimento(double spostamento, ref double residuo)
    {
        var totale = residuo + spostamento;
        var passi = (int)Math.Truncate(totale);
        residuo = totale - passi;
        return passi;
    }

    private void ImpostaOffsetMatriceInterno(double righe, double colonne)
    {
        _aggiornamentoOffsetMatriceInterno = true;
        try
        {
            OffsetRigheMatrice = righe;
            OffsetColonneMatrice = colonne;
        }
        finally { _aggiornamentoOffsetMatriceInterno = false; }
    }

    private void ReimpostaPosizioneMatrice()
    {
        _residuoScorrimentoRigheMatrice = 0;
        _residuoScorrimentoColonneMatrice = 0;
        ImpostaOffsetMatriceInterno(0, 0);
    }

    private void ReimpostaPosizioneRigheMatrice()
    {
        _residuoScorrimentoRigheMatrice = 0;
        ImpostaOffsetMatriceInterno(0, OffsetColonneMatrice);
    }
    private bool _inizializzazioneMatrice;
    private bool _riallineamentoAssiMatrice;

    /// <summary>Batching dei ricalcoli di pagina (feedback utente 2026-09-18: "anche mettendo i filtri la cosa
    /// e' decisamente lenta... sembra bloccato"). Anteprima e matrice venivano ricalcolate una volta per OGNI
    /// mutazione di filtro: ReimpostaFiltri tocca 6+ filtri e Aggiorna() ne ripopola una dozzina, quindi un
    /// singolo gesto dell'utente produceva 6-12 filtraggi completi dell'archivio piu' altrettante generazioni
    /// di matrice. Qui le richieste vengono accumulate e soddisfatte UNA volta sola alla fine dell'operazione,
    /// condividendo anche l'unico filtraggio dei Test fra anteprima e matrice.</summary>
    private int _refreshInCorso;
    private bool _anteprimaDaRigenerare;
    private bool _matriceDaRigenerare;
    // Il primo giro (costruttore) applica liste, selezioni persistite, anteprima e matrice in un solo passaggio.
    private bool _primoGiroPendente;

    // Firma economica dell'ultimo popolamento completo (audit prestazioni 2026-09-25, banco 100k Test): a
    // quella scala generare le liste "disponibili" per i filtri (ValoriDisponibiliPerDimensioni, ValoriDistinti
    // per OGNI filtro campo custom) e ricostruire la matrice (ReportMatriceService.Prepara, CostruisciCombinazioni/
    // CreaCandidati) sono 3+ scansioni complete dell'archivio — e Aggiorna() viene richiamato ad OGNI rivisita
    // della scheda (IPaginaAggiornabile) anche quando nulla e' cambiato da allora. Stesso modello di
    // WuStatusViewModel.Ricarica: l'impronta usa la revisione Phase D dei tre cataloghi master effettivamente
    // letti qui (Veicoli/Funzionalita/TestTemplate — MAI data/dimensione file, vedi CatturaImpronteCataloghi)
    // piu' l'identita' per-riferimento di TestCases.GetAll() (memoizzata dal repository, invalidata SOLO da una
    // scrittura reale locale o da un refresh dopo scrittura remota, vedi TestCaseRepository). Quando tutte
    // coincidono con l'ultimo popolamento, una rivisita si limita a riapplicare filtri/selezioni sulle
    // proiezioni gia' pronte (un giro con matrice riusata, che usa la
    // matrice preparata invece di rifare CostruisciCombinazioni/CreaCandidati) invece di rileggere i cataloghi
    // e riscandire l'intero archivio. Le marcature N.A. per cella (Config locale) e la Cronologia NON entrano
    // nella firma: sono economiche e vengono comunque riapplicate/ricaricate ad ogni chiamata (ApplicaMatrice
    // legge sempre Config fresco, CaricaCronologia gira sempre). Il ruolo (EMaster) non e' oggi usato da nessun
    // calcolo qui sotto, ma e' incluso per difesa, come in WuStatusViewModel: se in futuro un filtro/dimensione
    // dipendesse dal ruolo, un cambio ruolo senza altre modifiche non deve mai riusare una proiezione stantia.
    // I campi di sistema (quali campi del catalogo CampiSistema* sono usabili come filtro o dimensione) sono nella firma:
    // l'impronta dei due cataloghi (ImprontaCampiSistema) e la composizione di filtri e dimensioni (FirmaCampiCustom) entrano
    // nell'impronta del giro (CatturaGiro). Un campo abilitato o disabilitato dal Master cambia la firma e le liste si
    // riconciliano e si rileggono alla rivisita successiva, senza ricreare la pagina.
    private string? _improntaCataloghiUltimoPopolamento;
    private IReadOnlyList<TestCase>? _testUltimoPopolamento;
    private bool _eMasterUltimoPopolamento;

    /// <summary>Combina le impronte Phase D (CatturaImpronteCatalogoCorrente, MAI data/dimensione file) dei tre
    /// cataloghi master che Aggiorna() legge per costruire le liste "disponibili"/la matrice. Null se ANCHE UNO
    /// SOLO dei cataloghi non ha un'impronta economica e sicura (workspace non ancora Phase D): in quel caso
    /// non si mette nulla in cache, si ricalcola sempre — mai una cache basata su un proxy inesatto.</summary>
    private string? CatturaImpronteCataloghi()
    {
        var veicoli = _state.Master.Veicoli.CatturaImpronteCatalogoCorrente();
        var funzionalita = _state.Master.Funzionalita.CatturaImpronteCatalogoCorrente();
        var template = _state.Master.TestTemplate.CatturaImpronteCatalogoCorrente();
        if (veicoli == null || funzionalita == null || template == null) return null;
        return string.Join("|", veicoli, funzionalita, template);
    }

    public bool HaFiltriAttivi => FiltroEcu != null || FiltroVeicolo != null || DataDa != null || DataA != null || FiltroTemplate.HaSelezione
        || FiltriCampiCustomReport.Any(f => f.Filtro.HaSelezione)
        || FiltriMatrice.Any(f => f.Filtro.HaSelezione) || FiltroStatiMatrice.HaSelezione
        || FiltroTempoOperazioneAttivo != null || FiltroTempoTotaleTestAttivo != null;

    /// <summary>Rende visibili le selezioni attive anche quando il pannello dei filtri avanzati è chiuso.</summary>
    public string RiepilogoFiltriAttivi
    {
        get
        {
            var attivi = new List<string>();
            if (FiltroEcu != null) attivi.Add($"{LocalizationService.Instance.Traduci("ColEcu")}: {FiltroEcu}");
            if (FiltroVeicolo != null) attivi.Add($"{LocalizationService.Instance.Traduci("ColVeicolo")}: {FiltroVeicolo}");
            if (DataDa != null) attivi.Add($"{LocalizationService.Instance.Traduci("DataDaLabel")}: {DataDa:dd/MM/yyyy}");
            if (DataA != null) attivi.Add($"{LocalizationService.Instance.Traduci("DataALabel")}: {DataA:dd/MM/yyyy}");
            if (FiltroTemplate.HaSelezione)
                attivi.Add($"{LocalizationService.Instance.Traduci("TipoTestLabel")}: {string.Join(", ", FiltroTemplate.Opzioni.Where(o => o.Selezionato).Select(o => o.Etichetta))}");
            foreach (var filtro in FiltriCampiCustomReport.Where(f => f.Filtro.HaSelezione))
                attivi.Add($"{filtro.Etichetta}: {string.Join(", ", filtro.Filtro.Selezionati)}");
            foreach (var filtro in FiltriMatrice.Where(f => f.Filtro.HaSelezione))
                attivi.Add($"{filtro.Dimensione.Etichetta}: {string.Join(", ", filtro.Filtro.Selezionati)}");
            if (FiltroStatiMatrice.HaSelezione)
                attivi.Add($"{LocalizationService.Instance.Traduci("MatriceFiltroStatiLabel")}: {string.Join(", ", FiltroStatiMatrice.Opzioni.Where(o => o.Selezionato).Select(o => o.Etichetta))}");
            if (FiltroTempoOperazioneAttivo is { } operazione)
                attivi.Add($"{LocalizationService.Instance.Traduci("FiltroTempoOperazioneLabel")}: {operazione.Etichetta}");
            if (FiltroTempoTotaleTestAttivo is { } totale)
                attivi.Add($"{LocalizationService.Instance.Traduci("FiltroTempoTotaleTestLabel")}: {totale.Etichetta}");
            return attivi.Count == 0
                ? ""
                : $"{LocalizationService.Instance.Traduci("FiltriAttiviReport")}: {string.Join(" · ", attivi)}";
        }
    }

    private void NotificaFiltriAttiviCambiati()
    {
        OnPropertyChanged(nameof(HaFiltriAttivi));
        OnPropertyChanged(nameof(RiepilogoFiltriAttivi));
    }

    [RelayCommand]
    private void ToggleFiltriAvanzati() => MostraFiltriAvanzati = !MostraFiltriAvanzati;

    public List<string> EcuDisponibili { get; private set; } = new();
    public List<string> VeicoliDisponibili { get; private set; } = new();

    // BulkObservableCollection (feedback utente 2026-09-17, volumi 100k): senza filtri attivi l'anteprima
    // conteneva di default TUTTI i Test — stesso problema già risolto per Test/NC/Home, Clear()+Add() ripetuto
    // costringeva la DataGrid a ricalcolare la tabella una volta per riga invece che una sola per l'intero refresh.
    // A scala 100k+ questo non bastava più (Addendum prestazioni 2026-09-20, feedback utente "navigazione
    // fluida, 700ms su qualunque pagina"): ordinare e materializzare 100k RigaAnteprima per una preview mai
    // davvero scorsa fino in fondo (il banner AvvisoVolumeGrande già invita a filtrare) dominava il tempo di
    // apertura pagina. Ora, quando AvvisoVolumeGrande è vero, Anteprima mostra solo le AnteprimaMaxRighe più
    // di recente modificate — TestoConteggioAnteprima resta il conteggio REALE (TotaleRisultatiAnteprima), non
    // quello troncato, così il testo mostrato all'operatore non cambia. L'export ("Genera()"/Cronologia) non è
    // toccato: lavora sempre sull'intero risultato filtrato.
    private const int AnteprimaMaxRighe = 200;
    public BulkObservableCollection<RigaAnteprima> Anteprima { get; } = new();
    public int TotaleRisultatiAnteprima { get; private set; }
    public string TestoConteggioAnteprima => string.Format(LocalizationService.Instance.Traduci("TestInclusiFmt"), TotaleRisultatiAnteprima);
    public string TestoAvvisoAnteprimaLimitata => string.Format(LocalizationService.Instance.Traduci("AnteprimaLimitataFmt"), AnteprimaMaxRighe, TotaleRisultatiAnteprima);
    public bool AnteprimaLimitata => Anteprima.Count < TotaleRisultatiAnteprima;
    public bool AnteprimaVuota => TotaleRisultatiAnteprima == 0;

    public ObservableCollection<ReportGenerato> Cronologia { get; } = new();

    partial void OnMessaggioChanged(string? value) => OnPropertyChanged(nameof(HaMessaggio));
    partial void OnMessaggioErroreChanged(string? value) => OnPropertyChanged(nameof(HaMessaggioErrore));

    private const string ChiaveVistaFiltroEcu = "Report.FiltroEcu";
    private const string ChiaveVistaFiltroVeicolo = "Report.FiltroVeicolo";
    private const string ChiaveVistaDataDa = "Report.DataDa";
    private const string ChiaveVistaDataA = "Report.DataA";
    private const string ChiaveVistaFiltroTemplate = "Report.FiltroTemplate";

    /// <summary>Con <paramref name="aggiornaInBackground"/> i ricalcoli girano su thread di pool (vedi ReportViewModel.Giro);
    /// il valore predefinito (test e percorsi esistenti) li esegue in linea con risultati identici.</summary>
    public ReportViewModel(AppState state, bool aggiornaInBackground = false)
    {
        _state = state;
        var paginaDebole = new WeakReference<ReportViewModel>(this);
        PropertyChangedEventHandler? linguaCambiata = null;
        linguaCambiata = (mittente, args) =>
        {
            if (!paginaDebole.TryGetTarget(out var pagina))
            {
                LocalizationService.Instance.PropertyChanged -= linguaCambiata;
                return;
            }
            if (args.PropertyName == nameof(LocalizationService.Lingua))
            {
                pagina.AggiornaEtichetteDimensioniMatrice();
                pagina.RietichettaFiltroStatiMatrice();
                pagina.OnPropertyChanged(nameof(RiepilogoFiltriAttivi));
            }
        };
        _linguaCambiata = linguaCambiata;
        LocalizationService.Instance.PropertyChanged += _linguaCambiata;
        CaricaPreferenzeDimensioniMatrice();
        // Una sola lettura logica dei master per tutta la costruzione sincrona (catalogo campi letto tre volte): un solo refresh
        // Phase D sul thread UI. Il giro in background apre la propria lettura sul pool.
        using var letturaMaster = MasterLetturaScope.Apri();
        FiltroTemplate.Cambiato += GeneraAnteprima;
        PopolaFiltroStatiMatrice();
        FiltroStatiMatrice.LegaPersistenza(_state, ChiaveVistaFiltroStatiMatrice);
        FiltroStatiMatrice.Cambiato += GeneraMatrice;

        CreaStrutturaMatrice();
        // Filtri e dimensioni dei campi di sistema: stesso allineamento di ogni giro (vedi ConciliaCampiCustom). Gli assi salvati
        // restano disponibili anche se il loro campo e' stato disabilitato, altrimenti la configurazione andrebbe persa alla riapertura.
        ConciliaCampiCustom(IdDimensioniAssiPersistiti());

        // Prima passata dati (Aggiorna) con la stessa soppressione già usata per il resto dell'inizializzazione
        // della matrice: gli assi/categorie di default vengono impostati SUBITO dopo, e ognuno farebbe scattare
        // un GeneraMatrice() reale se non fosse per questo guard — un solo calcolo alla fine, non uno per passo.
        _inizializzazioneMatrice = true;
        // Filtri scalari persistiti (feedback utente 2026-09-18: "tutte quante le viste devono mantenere
        // filtri... non solo alcune"), impostati PRIMA di Aggiorna(): assegnazione diretta ai campi, non alle
        // proprietà — i relativi On...Changed chiamano GeneraAnteprima(), che pero' esce subito perché
        // _inizializzazioneMatrice e' già true, quindi il valore resta impostato senza alcun calcolo prematuro
        // su liste (EcuDisponibili ecc.) che Aggiorna() non ha ancora popolato.
        filtroEcu = ViewStateService.Leggi(_state, ChiaveVistaFiltroEcu);
        filtroVeicolo = ViewStateService.Leggi(_state, ChiaveVistaFiltroVeicolo);
        dataDa = ViewStateService.LeggiData(_state, ChiaveVistaDataDa);
        dataA = ViewStateService.LeggiData(_state, ChiaveVistaDataA);
        if (dataDa.HasValue) dataDaTesto = dataDa.Value.ToString("dd/MM/yyyy");
        if (dataA.HasValue) dataATesto = dataA.Value.ToString("dd/MM/yyyy");
        // Liste dei filtri: assi e selezioni persistite le usano, quindi devono esistere prima. Con aggiornaInBackground il
        // primo popolamento parte in background (indicatore nel frattempo) e la costruzione prosegue al suo termine.
        CaricaCronologia();
        // Assi della matrice: non dipendono dai dati dell'archivio (le opzioni delle dimensioni esistono gia'), quindi si
        // impostano prima del primo giro, che calcola cosi' matrice e anteprima con le selezioni gia' effettive.
        ImpostaAssiIniziali();
        _listeDaRicaricare = true;
        _anteprimaDaRigenerare = true;
        _matriceDaRigenerare = true;
        _primoGiroPendente = true;
        _aggiornaInBackground = aggiornaInBackground;
        if (aggiornaInBackground)
        {
            PartiPrimoGiroInBackground();
            return;
        }
        EseguiGiroInLinea();
        CompletaCostruzione();
    }

    /// <summary>Assi della matrice (ultima scelta persistita per questo PC/operatore, altrimenti Componente x CategoriaFunzionalita).
    /// Sul thread UI, durante la costruzione: le notifiche degli assi sono inerti finche' <c>_inizializzazioneMatrice</c> e' vera.</summary>
    private void ImpostaAssiIniziali()
    {
        var x = LeggiCampiPersistiti(ChiaveVistaCampiX, ChiaveVistaAsseX) ?? new List<string> { DimensioniMatrice.Componente };
        var y = LeggiCampiPersistiti(ChiaveVistaCampiY, ChiaveVistaAsseY) ?? new List<string> { DimensioniMatrice.CategoriaFunzionalita };
        CampiXMatrice.Clear();
        CampiYMatrice.Clear();
        foreach (var id in x.Distinct(StringComparer.OrdinalIgnoreCase).Take(ConfigurazioneMatriceReport.MassimoCampiPerAsse))
            if (OpzioniDimensioniMatrice.FirstOrDefault(d => d.Id == id) is { } opzione) AggiungiCampoMatriceInterno(false, opzione);
        foreach (var id in y.Distinct(StringComparer.OrdinalIgnoreCase).Where(id => !CampiXMatrice.Any(c => c.Dimensione.Id == id))
                     .Take(ConfigurazioneMatriceReport.MassimoCampiPerAsse))
            if (OpzioniDimensioniMatrice.FirstOrDefault(d => d.Id == id) is { } opzione) AggiungiCampoMatriceInterno(true, opzione);
        if (CampiXMatrice.Count + CampiYMatrice.Count == 0)
            AggiungiCampoMatriceInterno(false, OpzioniDimensioniMatrice.First(d => d.Id == DimensioniMatrice.Componente));
        ModalitaMatrice = ViewStateService.Leggi(_state, ChiaveVistaModalita) switch
        {
            "stato" => ModalitaCellaMatrice.Stato,
            "test" => ModalitaCellaMatrice.TestPercentuale,
            "tempo-totale" => ModalitaCellaMatrice.TempoTotale,
            "tempo-medio" => ModalitaCellaMatrice.TempoMedio,
            _ => ModalitaCellaMatrice.Percentuale, // anche la preferenza legacy "percentuale": Stato %
        };
        BaseTempo = ViewStateService.Leggi(_state, ChiaveVistaBaseTempo) == "ultimo"
            ? BaseTempoMatrice.UltimoStato : BaseTempoMatrice.TutteLeEsecuzioni;
        TestoFiltroTempoOperazione = ViewStateService.Leggi(_state, ChiaveVistaTestoFiltroTempoOperazione) ?? "";
        OperatoreFiltroTempoOperazione = LeggiOperatoreTempo(ChiaveVistaOperatoreFiltroTempoOperazione);
        TestoFiltroTempoTotaleTest = ViewStateService.Leggi(_state, ChiaveVistaTestoFiltroTempoTotaleTest) ?? "";
        OperatoreFiltroTempoTotaleTest = LeggiOperatoreTempo(ChiaveVistaOperatoreFiltroTempoTotaleTest);
        GruppiCompressiMatrice = LeggiGruppiCompressi();
    }

    /// <summary>Chiusura della costruzione (sul thread UI), dopo il primo giro: le selezioni persistite, le categorie di default e
    /// la persistenza dei filtri sono gia' applicate da quel giro (<see cref="ApplicaSelezioniPrimoGiro"/>). Nessun giro aggiuntivo:
    /// <see cref="RichiediGiro"/> esce subito se non c'e' nulla in sospeso.</summary>
    private void CompletaCostruzione()
    {
        _inizializzazioneMatrice = false;
        RichiediGiro(immediato: true);
    }

    private static string ChiaveVistaFiltro(string dimensioneId) => "Report.FiltroMatrice." + dimensioneId;

    private static string? ChiaveTraduzioneDimensioneMatrice(string id) => id switch
    {
        DimensioniMatrice.Gamma => "MatriceDimGamma",
        DimensioniMatrice.Sottoveicolo => "MatriceDimSottoveicolo",
        DimensioniMatrice.Componente => "MatriceDimEcu",
        DimensioniMatrice.CategoriaFunzionalita => "MatriceDimCategoriaFunzionalita",
        DimensioniMatrice.Funzionalita => "MatriceDimFunzionalita",
        DimensioniMatrice.Template => "MatriceDimTemplate",
        DimensioniMatrice.Esito => "MatriceDimEsito",
        DimensioniMatrice.Assegnatario => "MatriceDimAssegnatario",
        DimensioniMatrice.Requisito => "MatriceDimRequisito",
        _ => null
    };

    private void AggiornaEtichetteDimensioniMatrice()
    {
        var loc = LocalizationService.Instance;
        foreach (var dimensione in OpzioniDimensioniMatrice)
            if (ChiaveTraduzioneDimensioneMatrice(dimensione.Id) is { } chiave)
                dimensione.Etichetta = loc.Traduci(chiave);
    }

    /// <summary>Dimensioni già coperte dal pannello globale: mantengono il ruolo di asse ma hanno un solo filtro modificabile.</summary>
    private static bool HaFiltroGlobale(string dimensioneId) =>
        dimensioneId.Equals(DimensioniMatrice.Componente, StringComparison.OrdinalIgnoreCase)
        || dimensioneId.Equals(DimensioniMatrice.Sottoveicolo, StringComparison.OrdinalIgnoreCase)
        || dimensioneId.Equals(DimensioniMatrice.Template, StringComparison.OrdinalIgnoreCase)
        || dimensioneId.StartsWith(DimensioniMatrice.CampoCustomPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Struttura una tantum della matrice con le dimensioni standard (un FiltroDimensioneMatrice per ciascuna, con la
    /// sottoscrizione a Cambiato): non dipende da NESSUN dato di archivio. Le dimensioni dei campi di sistema si aggiungono e si
    /// tolgono con <see cref="ConciliaCampiCustom"/>. Separata da Aggiorna() (Addendum: audit prestazioni 2026-08-25,
    /// IPaginaAggiornabile — questa pagina resta viva tra un cambio scheda e l'altro) perché rifarla ad ogni rivisita
    /// duplicherebbe le sottoscrizioni a Cambiato.</summary>
    private void CreaStrutturaMatrice()
    {
        var loc = LocalizationService.Instance;
        void Aggiungi(string id, string chiave) => OpzioniDimensioniMatrice.Add(new DimensioneMatriceOpzione { Id = id, Etichetta = loc.Traduci(chiave) });
        Aggiungi(DimensioniMatrice.Gamma, "MatriceDimGamma");
        Aggiungi(DimensioniMatrice.Sottoveicolo, "MatriceDimSottoveicolo");
        Aggiungi(DimensioniMatrice.Componente, "MatriceDimEcu");
        Aggiungi(DimensioniMatrice.CategoriaFunzionalita, "MatriceDimCategoriaFunzionalita");
        Aggiungi(DimensioniMatrice.Funzionalita, "MatriceDimFunzionalita");
        Aggiungi(DimensioniMatrice.Template, "MatriceDimTemplate");
        Aggiungi(DimensioniMatrice.Esito, "MatriceDimEsito");
        Aggiungi(DimensioniMatrice.Assegnatario, "MatriceDimAssegnatario");
        Aggiungi(DimensioniMatrice.Requisito, "MatriceDimRequisito");

        foreach (var dimensione in OpzioniDimensioniMatrice)
        {
            if (HaFiltroGlobale(dimensione.Id)) continue;
            var filtro = new FiltroDimensioneMatrice { Dimensione = dimensione };
            filtro.Filtro.Cambiato += GeneraMatrice;
            FiltriMatrice.Add(filtro);
        }
    }

    /// <summary>Allinea i filtri e le dimensioni di matrice dei campi di sistema al catalogo corrente, per Chiave (come la Home, vedi
    /// <see cref="FiltriCampiCustomSync"/>). Un campo abilitato compare come filtro e come dimensione di asse; uno disabilitato o archiviato
    /// esce dai filtri (nessun filtro invisibile) e dalle dimensioni offerte. Una dimensione ancora usata da un asse configurato resta
    /// nel menu di asse (la configurazione non si perde), e <paramref name="dimensioniDaConservare"/> copre gli assi salvati alla
    /// costruzione. Ritorna true se la composizione e' cambiata. Sul thread UI, senza generare giri: il chiamante li avvia.</summary>
    private bool ConciliaCampiCustom(IEnumerable<string>? dimensioniDaConservare = null)
    {
        var eleggibili = CampoSistemaCatalogoService.CampiUtilizzabiliComeFiltro(_state);
        var (aggiunti, rimossi) = FiltriCampiCustomSync.Allinea(FiltriCampiCustomReport, eleggibili);
        foreach (var filtro in aggiunti) filtro.Filtro.Cambiato += GeneraAnteprima;
        foreach (var filtro in rimossi)
        {
            filtro.Filtro.Cambiato -= GeneraAnteprima;
            if (!filtro.Filtro.HaSelezione) continue;
            // Il filtro rimosso filtrava anteprima e matrice: entrambe si ricalcolano nel giro che sta catturando la richiesta.
            _anteprimaDaRigenerare = true;
            _matriceDaRigenerare = true;
        }
        var cambiato = aggiunti.Count + rimossi.Count > 0;

        // Le selezioni precedentemente duplicate non restano filtri invisibili:
        // per questi criteri vale solo la selezione globale visibile sopra.
        foreach (var filtro in FiltriMatrice.Where(f => HaFiltroGlobale(f.Dimensione.Id)).ToList())
        {
            filtro.Filtro.Cambiato -= GeneraMatrice;
            FiltriMatrice.Remove(filtro);
            ViewStateService.Scrivi(_state, ChiaveVistaFiltro(filtro.Dimensione.Id), null);
            cambiato = true;
        }

        var prefisso = DimensioniMatrice.CampoCustomPrefix;
        bool IdCampoSistema(string id) => id.StartsWith(prefisso, StringComparison.OrdinalIgnoreCase);
        var ammesse = eleggibili.Select(c => prefisso + c.Chiave).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usate = CampiXMatrice.Concat(CampiYMatrice).Select(c => c.Dimensione.Id)
            .Concat(dimensioniDaConservare ?? Array.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Filtri di valore della matrice: restano solo per i campi ammessi (un filtro su un campo disabilitato sarebbe invisibile).
        foreach (var filtro in FiltriMatrice.Where(f => IdCampoSistema(f.Dimensione.Id) && !ammesse.Contains(f.Dimensione.Id)).ToList())
        {
            filtro.Filtro.Cambiato -= GeneraMatrice;
            FiltriMatrice.Remove(filtro);
            cambiato = true;
            if (!filtro.Filtro.HaSelezione) continue;
            _anteprimaDaRigenerare = true;
            _matriceDaRigenerare = true;
        }
        // Dimensioni offerte negli assi: le non ammesse escono solo se nessun asse (o asse salvato) le usa ancora.
        foreach (var opzione in OpzioniDimensioniMatrice.Where(o => IdCampoSistema(o.Id) && !ammesse.Contains(o.Id) && !usate.Contains(o.Id)).ToList())
        {
            OpzioniDimensioniMatrice.Remove(opzione);
            cambiato = true;
        }
        foreach (var campo in eleggibili)
        {
            var id = prefisso + campo.Chiave;
            var opzione = OpzioniDimensioniMatrice.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
            if (opzione == null)
            {
                opzione = new DimensioneMatriceOpzione { Id = id, Etichetta = campo.Etichetta };
                OpzioniDimensioniMatrice.Add(opzione);
                cambiato = true;
            }
            else if (opzione.Etichetta != campo.Etichetta) opzione.Etichetta = campo.Etichetta;

            if (HaFiltroGlobale(id) || FiltriMatrice.Any(f => string.Equals(f.Dimensione.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
            var filtro = new FiltroDimensioneMatrice { Dimensione = opzione };
            filtro.Filtro.Cambiato += GeneraMatrice;
            FiltriMatrice.Add(filtro);
            cambiato = true;
        }
        // Assi salvati (o gia' in uso) di campi non piu' ammessi: la dimensione resta offerta con la sua etichetta, senza filtro.
        var mancanti = dimensioniDaConservare?.Where(IdCampoSistema)
            .Where(id => !ammesse.Contains(id) && !OpzioniDimensioniMatrice.Any(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        if (mancanti.Count > 0)
        {
            // Tutti i campi del catalogo, archiviati compresi: un asse salvato su un campo archiviato mostra la sua ultima etichetta.
            var noti = _state.Master.CampiSistemaDettagli.Load().Concat(_state.Master.CampiSistemaPianificazione.Load()).ToList();
            foreach (var id in mancanti)
            {
                var etichetta = noti.FirstOrDefault(c => string.Equals(prefisso + c.Chiave, id, StringComparison.OrdinalIgnoreCase))?.Etichetta
                    ?? id[prefisso.Length..];
                OpzioniDimensioniMatrice.Add(new DimensioneMatriceOpzione { Id = id, Etichetta = etichetta });
                cambiato = true;
            }
        }
        return cambiato;
    }

    /// <summary>Dimensioni di campo di sistema degli assi salvati (ultima scelta per questo PC/operatore): alla costruzione restano
    /// offerte anche se il campo e' stato disabilitato, cosi' l'asse salvato non viene perso.</summary>
    private IEnumerable<string> IdDimensioniAssiPersistiti() =>
        (LeggiCampiPersistiti(ChiaveVistaCampiX, ChiaveVistaAsseX) ?? new List<string>())
        .Concat(LeggiCampiPersistiti(ChiaveVistaCampiY, ChiaveVistaAsseY) ?? new List<string>())
        .Where(id => id.StartsWith(DimensioniMatrice.CampoCustomPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Ripopola liste "disponibili", filtri (preservando la selezione, FiltroMultiSelect.Ripopola),
    /// cronologia e anteprima/matrice (Addendum: audit prestazioni 2026-08-25, IPaginaAggiornabile — questa
    /// pagina resta viva tra un cambio scheda e l'altro invece di essere ricreata). La STRUTTURA della matrice
    /// (CreaStrutturaMatrice) e gli assi/categorie di default restano cose fatte una volta sola nel
    /// costruttore, non qui.</summary>
    public void Aggiorna()
    {
        // Il calcolo (liste, anteprima, matrice) e' un giro: su thread di pool in modalita' background, in linea altrimenti.
        // Qui si chiede soltanto il giro, senza coalescenza: una rivisita non ha cambi da attendere.
        _refreshInCorso++;
        try
        {
            CaricaCronologia();
            // Liste e filtri: il giro confronta archivio e cataloghi con l'ultimo popolamento e ricalcola solo se serve.
            // La matrice e' esplicitamente fra i pendenti: se nulla e' cambiato, il giro riusa quella gia' preparata.
            _listeDaRicaricare = true;
            _anteprimaDaRigenerare = true;
            _matriceDaRigenerare = true;
        }
        finally { _refreshInCorso--; }
        RichiediGiro(immediato: true);
        // TICKET lifecycle analytics live in the NC/CR section, not in generic Report.
    }

    /// <summary>Ruolo corrente usato dalla firma di invarianza sopra: nessun calcolo di questa pagina ne
    /// dipende oggi, ma e' incluso per difesa (stesso principio di WuStatusViewModel.Ricarica) — un cambio
    /// ruolo senza alcuna altra modifica non deve mai riusare una proiezione calcolata sotto un ruolo diverso.</summary>
    private bool EMasterCorrente => _state.EMaster;

    private const string ChiaveVistaAsseX = "Report.AsseX";
    private const string ChiaveVistaAsseY = "Report.AsseY";
    private const string ChiaveVistaCampiX = "Report.AssiX";
    private const string ChiaveVistaCampiY = "Report.AssiY";
    private const string ChiaveVistaModalita = "Report.ModalitaMatrice";
    private const string ChiaveVistaGruppi = "Report.GruppiMatriceCompressi";

    private ObservableCollection<CampoAsseMatrice> CampiDi(bool righe) => righe ? CampiYMatrice : CampiXMatrice;

    /// <summary>Identificativi delle dimensioni di un asse, nell'ordine dei campi (chiavi di MatriceNonApplicabileService).</summary>
    private static List<string> IdCampi(IEnumerable<CampoAsseMatrice> campi) => campi.Select(c => c.Dimensione.Id).ToList();

    /// <summary>Indice del selettore (0 = Stato %, 1 = Stato, 2 = Test %).</summary>
    public int IndiceModalitaMatrice
    {
        get => ModalitaMatrice switch
        {
            ModalitaCellaMatrice.Stato => 1,
            ModalitaCellaMatrice.TestPercentuale => 2,
            ModalitaCellaMatrice.TempoTotale => 3,
            ModalitaCellaMatrice.TempoMedio => 4,
            _ => 0,
        };
        set => ModalitaMatrice = value switch
        {
            1 => ModalitaCellaMatrice.Stato,
            2 => ModalitaCellaMatrice.TestPercentuale,
            3 => ModalitaCellaMatrice.TempoTotale,
            4 => ModalitaCellaMatrice.TempoMedio,
            _ => ModalitaCellaMatrice.Percentuale,
        };
    }

    /// <summary>Intestazione della colonna unica (asse X senza campi): "Stato" o "% conforme". Vuota se c'e' almeno un campo.</summary>
    public string EtichettaColonnaMatrice => CampiXMatrice.Count > 0 ? "" : EtichettaAsseVuoto();

    /// <summary>Intestazione della riga unica (asse Y senza campi): "Stato" o "% conforme". Vuota se c'e' almeno un campo.</summary>
    public string EtichettaRigaMatrice => CampiYMatrice.Count > 0 ? "" : EtichettaAsseVuoto();

    private string EtichettaAsseVuoto() => LocalizationService.Instance.Traduci(ModalitaMatrice switch
    {
        ModalitaCellaMatrice.Stato => "MatriceColonnaStato",
        ModalitaCellaMatrice.TestPercentuale => "MatriceColonnaTestPercentuale",
        ModalitaCellaMatrice.TempoTotale => "MatriceColonnaTempoTotale",
        ModalitaCellaMatrice.TempoMedio => "MatriceColonnaTempoMedio",
        _ => "MatriceColonnaPercentuale",
    });

    private const string ChiaveVistaBaseTempo = "Report.BaseTempo";
    private const string ChiaveVistaTestoFiltroTempoOperazione = "Report.FiltroTempoOperazione.Minuti";
    private const string ChiaveVistaOperatoreFiltroTempoOperazione = "Report.FiltroTempoOperazione.Operatore";
    private const string ChiaveVistaTestoFiltroTempoTotaleTest = "Report.FiltroTempoTotaleTest.Minuti";
    private const string ChiaveVistaOperatoreFiltroTempoTotaleTest = "Report.FiltroTempoTotaleTest.Operatore";

    private int LeggiOperatoreTempo(string chiave) =>
        int.TryParse(ViewStateService.Leggi(_state, chiave), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var valore) && valore == 1 ? 1 : 0;

    partial void OnBaseTempoChanged(BaseTempoMatrice value)
    {
        if (!_inizializzazioneMatrice)
            ViewStateService.Scrivi(_state, ChiaveVistaBaseTempo, value == BaseTempoMatrice.UltimoStato ? "ultimo" : "tutte");
        OnPropertyChanged(nameof(IndiceBaseTempo));
        if (!_inizializzazioneMatrice) GeneraAnteprima();
    }

    partial void OnTestoFiltroTempoOperazioneChanged(string value)
    {
        OnPropertyChanged(nameof(FiltroTempoOperazioneHaErrore));
        if (!_inizializzazioneMatrice) ViewStateService.Scrivi(_state, ChiaveVistaTestoFiltroTempoOperazione, value);
        if (!_inizializzazioneMatrice) GeneraAnteprima();
    }

    partial void OnOperatoreFiltroTempoOperazioneChanged(int value)
    {
        if (!_inizializzazioneMatrice) ViewStateService.Scrivi(_state, ChiaveVistaOperatoreFiltroTempoOperazione, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!_inizializzazioneMatrice) GeneraAnteprima();
    }

    partial void OnTestoFiltroTempoTotaleTestChanged(string value)
    {
        OnPropertyChanged(nameof(FiltroTempoTotaleTestHaErrore));
        if (!_inizializzazioneMatrice) ViewStateService.Scrivi(_state, ChiaveVistaTestoFiltroTempoTotaleTest, value);
        if (!_inizializzazioneMatrice) GeneraAnteprima();
    }

    partial void OnOperatoreFiltroTempoTotaleTestChanged(int value)
    {
        if (!_inizializzazioneMatrice) ViewStateService.Scrivi(_state, ChiaveVistaOperatoreFiltroTempoTotaleTest, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!_inizializzazioneMatrice) GeneraAnteprima();
    }

    partial void OnModalitaMatriceChanged(ModalitaCellaMatrice value)
    {
        if (!_inizializzazioneMatrice)
        {
            ViewStateService.Scrivi(_state, ChiaveVistaModalita, value switch
            {
                ModalitaCellaMatrice.Stato => "stato",
                ModalitaCellaMatrice.TestPercentuale => "test",
                ModalitaCellaMatrice.TempoTotale => "tempo-totale",
                ModalitaCellaMatrice.TempoMedio => "tempo-medio",
                _ => "percentuale",
            });
            _listeDaRicaricare = true;
            _improntaCataloghiUltimoPopolamento = null;
        }
        OnPropertyChanged(nameof(IndiceModalitaMatrice));
        OnPropertyChanged(nameof(ModalitaTempoMatrice));
        OnPropertyChanged(nameof(ModalitaStatoMatrice));
        OnPropertyChanged(nameof(EtichettaColonnaMatrice));
        OnPropertyChanged(nameof(EtichettaRigaMatrice));
        GeneraMatrice();
    }

    private void ImpostaPrimoCampo(bool righe, DimensioneMatriceOpzione? valore)
    {
        var campi = CampiDi(righe);
        if (valore == null)
        {
            if (campi.Count > 0) RimuoviCampoMatrice(campi[0]);
            return;
        }
        if (campi.Count == 0)
        {
            AggiungiCampoMatriceInterno(righe, valore);
            SalvaCampiMatrice();
            GeneraMatrice();
            return;
        }
        campi[0].Dimensione = valore;
    }

    private void AggiungiCampoMatriceInterno(bool righe, DimensioneMatriceOpzione dimensione)
    {
        var campo = new CampoAsseMatrice(dimensione, righe);
        campo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CampoAsseMatrice.Dimensione)) CampoMatriceCambiato(campo);
        };
        CampiDi(righe).Add(campo);
        RinumeraCampiMatrice(righe);
    }

    /// <summary>Scelta di una dimensione su un campo: una dimensione gia' usata da un altro campo viene rifiutata (ripristinata).</summary>
    private void CampoMatriceCambiato(CampoAsseMatrice campo)
    {
        if (_riallineamentoAssiMatrice) return;
        var duplicato = CampiXMatrice.Concat(CampiYMatrice).Any(c => !ReferenceEquals(c, campo) && c.Dimensione.Id == campo.Dimensione.Id);
        if (duplicato)
        {
            _riallineamentoAssiMatrice = true;
            campo.Dimensione = campo.Precedente;
            _riallineamentoAssiMatrice = false;
            return;
        }
        campo.Precedente = campo.Dimensione;
        OnPropertyChanged(nameof(AsseXMatrice));
        OnPropertyChanged(nameof(AsseYMatrice));
        SalvaCampiMatrice();
        GeneraMatrice();
    }

    private void RinumeraCampiMatrice(bool righe)
    {
        var campi = CampiDi(righe);
        var prefisso = righe ? "Report_MatriceAsseY" : "Report_MatriceAsseX";
        for (var i = 0; i < campi.Count; i++)
            campi[i].IdAutomazione = i == 0 ? prefisso + "_ComboBox" : $"{prefisso}_Campo{i + 1}_ComboBox";
        OnPropertyChanged(nameof(SenzaCampiXMatrice));
        OnPropertyChanged(nameof(SenzaCampiYMatrice));
        OnPropertyChanged(nameof(EtichettaColonnaMatrice));
        OnPropertyChanged(nameof(EtichettaRigaMatrice));
    }

    /// <summary>Aggiunge un campo all'asse (fino a tre), con la prima dimensione non ancora usata nella matrice.</summary>
    public void AggiungiCampoMatrice(bool righe)
    {
        if (CampiDi(righe).Count >= ConfigurazioneMatriceReport.MassimoCampiPerAsse) return;
        var usati = new HashSet<string>(CampiXMatrice.Concat(CampiYMatrice).Select(c => c.Dimensione.Id), StringComparer.OrdinalIgnoreCase);
        var libera = OpzioniDimensioniMatrice.FirstOrDefault(d => !usati.Contains(d.Id));
        if (libera == null) return;
        AggiungiCampoMatriceInterno(righe, libera);
        SalvaCampiMatrice();
        GeneraMatrice();
    }

    /// <summary>Rimuove un campo; non si puo' rimuovere l'ultimo campo dell'intera matrice.</summary>
    public void RimuoviCampoMatrice(CampoAsseMatrice campo)
    {
        if (CampiXMatrice.Count + CampiYMatrice.Count <= 1) return;
        CampiDi(campo.Righe).Remove(campo);
        RinumeraCampiMatrice(campo.Righe);
        SalvaCampiMatrice();
        GeneraMatrice();
    }

    /// <summary>Sposta un campo di una posizione nel proprio asse (delta -1 = su, +1 = giu').</summary>
    public void SpostaCampoMatrice(CampoAsseMatrice campo, int delta)
    {
        var campi = CampiDi(campo.Righe);
        var indice = campi.IndexOf(campo);
        var destinazione = indice + delta;
        if (indice < 0 || destinazione < 0 || destinazione >= campi.Count) return;
        campi.Move(indice, destinazione);
        RinumeraCampiMatrice(campo.Righe);
        SalvaCampiMatrice();
        GeneraMatrice();
    }

    /// <summary>Espande o comprime un gruppo della matrice (vista locale: non cambia conteggi ne' totali).</summary>
    public void AlternaGruppoMatrice(string chiave)
    {
        if (!GruppiCompressiMatrice.Remove(chiave)) GruppiCompressiMatrice.Add(chiave);
        if (!_inizializzazioneMatrice)
            ViewStateService.Scrivi(_state, ChiaveVistaGruppi, GruppiCompressiMatrice.Count == 0
                ? null : JsonSerializer.Serialize(GruppiCompressiMatrice.OrderBy(v => v, StringComparer.Ordinal).ToList()));
        ReimpostaPosizioneMatrice();
        ApplicaMatrice();
    }

    /// <summary>Salva i campi degli assi. Oltre alle chiavi multi-campo scrive anche le chiavi a un campo per asse, che le
    /// versioni precedenti leggono (un asse senza campi cancella la chiave legacy).</summary>
    private void SalvaCampiMatrice()
    {
        if (_inizializzazioneMatrice) return;
        var x = IdCampi(CampiXMatrice);
        var y = IdCampi(CampiYMatrice);
        ViewStateService.Scrivi(_state, ChiaveVistaCampiX, JsonSerializer.Serialize(x));
        ViewStateService.Scrivi(_state, ChiaveVistaCampiY, JsonSerializer.Serialize(y));
        ViewStateService.Scrivi(_state, ChiaveVistaAsseX, x.Count > 0 ? x[0] : null);
        ViewStateService.Scrivi(_state, ChiaveVistaAsseY, y.Count > 0 ? y[0] : null);
    }

    private List<string>? LeggiCampiPersistiti(string chiaveLista, string chiaveLegacy)
    {
        var testo = ViewStateService.Leggi(_state, chiaveLista);
        if (testo != null)
        {
            try { return JsonSerializer.Deserialize<List<string>>(testo) ?? new List<string>(); }
            catch (JsonException) { return null; }
        }
        var legacy = ViewStateService.Leggi(_state, chiaveLegacy);
        return string.IsNullOrEmpty(legacy) ? null : new List<string> { legacy };
    }

    private HashSet<string> LeggiGruppiCompressi()
    {
        var testo = ViewStateService.Leggi(_state, ChiaveVistaGruppi);
        if (string.IsNullOrEmpty(testo)) return new HashSet<string>(StringComparer.Ordinal);
        try { return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(testo) ?? new List<string>(), StringComparer.Ordinal); }
        catch (JsonException) { return new HashSet<string>(StringComparer.Ordinal); }
    }

    private ConfigurazioneMatriceReport CostruisciConfigurazioneMatrice(bool applicaMascheraPresentazione = true)
    {
        var campiX = IdCampi(CampiXMatrice);
        var campiY = IdCampi(CampiYMatrice);
        var ordini = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dimensione in campiX.Concat(campiY))
        {
            var ordine = OrdineAssiReportService.Leggi(_state.Config, _state.RuoloAttivo, dimensione);
            if (ordine != null) ordini[dimensione] = ordine;
        }
        return new ConfigurazioneMatriceReport
        {
            StatiNascostiPresentazione = applicaMascheraPresentazione
                ? new HashSet<StatoCellaMatrice>(_statiNascostiPresentazione)
                : new HashSet<StatoCellaMatrice>(),
            CampiX = campiX,
            CampiY = campiY,
            ModalitaCella = ModalitaMatrice,
            BaseTempo = BaseTempo,
            FiltroTempoOperazione = FiltroTempoOperazioneAttivo,
            FiltroTempoTotaleTest = FiltroTempoTotaleTestAttivo,
            Filtri = FiltriMatrice.ToDictionary(f => f.Dimensione.Id, f => f.Filtro.Selezionati, StringComparer.OrdinalIgnoreCase),
            Ordini = ordini,
            GruppiCompressi = new HashSet<string>(GruppiCompressiMatrice, StringComparer.Ordinal),
            StatiFiltroRigheColonne = StatiFiltroMatriceSelezionati(),
            EcuRistrettePerRequisito = campiX.Concat(campiY).Contains(DimensioniMatrice.Requisito) && campiX.Concat(campiY).Contains(DimensioniMatrice.Componente)
                ? RequisitoApplicabilita.EcuRistrettePerRequisito(_state.Master.Requisiti.Load())
                : new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Valori (righe = asse Y, colonne = asse X) nell'ordine corrente, per il dialogo "Ordina".
    /// Con filtri attivi sono solo quelli presenti nella matrice filtrata.</summary>
    public IReadOnlyList<string> ValoriOrdinabili(bool righe) =>
        _matricePreparata == null ? Array.Empty<string>() : righe ? _matricePreparata.TutteLeRighe : _matricePreparata.TutteLeColonne;

    public string? DimensioneOrdinabile(bool righe) => righe ? AsseYMatrice?.Id : AsseXMatrice?.Id;
    public string? EtichettaDimensioneOrdinabile(bool righe) => righe ? AsseYMatrice?.Etichetta : AsseXMatrice?.Etichetta;

    /// <summary>Applica e salva (solo locale, per operatore|ruolo|dimensione) l'ordine scelto per un asse.
    /// <paramref name="ordine"/> null/vuoto = ripristino alfabetico. Non ricostruisce l'aggregato: ordina solo
    /// i valori degli assi. L'ordine vale per la dimensione, quindi anche se la stessa compare sull'altro asse.</summary>
    public void ImpostaOrdineAsse(bool righe, IReadOnlyList<string>? ordine)
    {
        var dimensione = DimensioneOrdinabile(righe);
        if (dimensione == null) return;
        var daSalvare = ordine == null || ordine.Count == 0
            ? null
            : OrdineAssiReportService.Unisci(ordine, OrdineAssiReportService.Leggi(_state.Config, _state.RuoloAttivo, dimensione));
        if (OrdineAssiReportService.Imposta(_state.Config, _state.RuoloAttivo, dimensione, daSalvare))
        {
            try { _state.ConfigService.Save(_state.Config); }
            catch (Exception ex) { CrashLogService.Registra("Salvataggio ordine assi report", ex); }
        }
        _matricePreparata?.Riordina(
            AsseXMatrice == null ? null : OrdineAssiReportService.Leggi(_state.Config, _state.RuoloAttivo, AsseXMatrice.Id),
            AsseYMatrice == null ? null : OrdineAssiReportService.Leggi(_state.Config, _state.RuoloAttivo, AsseYMatrice.Id));
        ReimpostaPosizioneMatrice();
        ApplicaMatrice();
    }

    private void GeneraMatrice()
    {
        NotificaFiltriAttiviCambiati();
        if (_inizializzazioneMatrice) return;
        _matricePreparata = null;
        // Cambio assi/filtri: la finestra precedente potrebbe non esistere piu' nel nuovo risultato.
        ReimpostaPosizioneMatrice();
        _matriceDaRigenerare = true;
        if (_refreshInCorso == 0) RichiediGiro(immediato: false);
    }

    /// <summary>Seleziona i Test piu' recenti in O(n log k) quando la preview e' limitata, mantenendo
    /// l'ordinamento stabile per data uguale usato da OrderByDescending. Con 100k righe evita di ordinare
    /// l'intero archivio per mostrare soltanto le prime 200.</summary>
    internal static IEnumerable<TestCase> SelezionaAnteprimaPiuRecenti(IReadOnlyList<TestCase> risultati, int massimo)
    {
        if (risultati.Count <= massimo) return risultati.OrderByDescending(tc => tc.UltimaModifica);

        // La coda usa l'ordine inverso per esporre in testa il peggiore tra i k candidati: data piu' vecchia,
        // poi indice originale piu' alto. L'indice rende deterministici e stabili i pareggi di data.
        var prioritaInvertita = Comparer<(long DataTicksInvertiti, int Indice)>.Create((a, b) => b.CompareTo(a));
        var recenti = new PriorityQueue<(TestCase Test, int Indice), (long DataTicksInvertiti, int Indice)>(prioritaInvertita);
        for (var indice = 0; indice < risultati.Count; indice++)
        {
            var test = risultati[indice];
            var priorita = (DateTime.MaxValue.Ticks - test.UltimaModifica.Ticks, indice);
            if (recenti.Count < massimo)
            {
                recenti.Enqueue((test, indice), priorita);
                continue;
            }

            var peggiore = recenti.Peek().Indice;
            var prioritaPeggiore = (DateTime.MaxValue.Ticks - risultati[peggiore].UltimaModifica.Ticks, peggiore);
            if (priorita.CompareTo(prioritaPeggiore) >= 0) continue;
            recenti.Dequeue();
            recenti.Enqueue((test, indice), priorita);
        }

        return recenti.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(item => item.Test.UltimaModifica)
            .ThenBy(item => item.Indice)
            .Select(item => item.Test)
            .ToList();
    }

    private void ApplicaMatrice(bool dimensioniAggiornate = false, bool scorrimento = false)
    {
        if (CampiXMatrice.Count + CampiYMatrice.Count == 0 || _matricePreparata == null) return;
        if (!dimensioniAggiornate) AggiornaDimensioniFinestraMatrice(aggiornaCoda: !scorrimento);
        _matricePreparata.ImpostaStatiNascostiPresentazione(_statiNascostiPresentazione);
        // La matrice preparata arriva dal giro (calcolo su pool): qui si proietta solo la finestra visibile.
        // L'export usa ancora Genera() e quindi l'intera matrice. La vista, fatta di ItemsControl annidati
        // non virtualizzati, crea al massimo 50x12 celle. Le barre scorrono l'intero asse spostando questa
        // finestra, senza conservare una seconda copia dei 100k Test o riespandere l'archivio.
        // Addendum 6.4.0 (totali di intestazione): i Totali dipendono dalla marcatura N.A., stato locale
        // riapplicato "ad ogni visita" — si legge sempre l'elenco corrente di marcature (mai nella cache di
        // _matricePreparata), cosi' un cambio di marcatura si riflette anche quando ApplicaMatrice() riusa la
        // stessa _matricePreparata (SegnaCellaNonApplicabile/RimuoviCellaNonApplicabile non forzano una nuova
        // Prepara()). MatricePreparata.Finestra confronta la FIRMA di questo elenco e ricalcola i Totali solo
        // se e' cambiata: lo scorrimento riusa il risultato gia' calcolato.
        var campiX = IdCampi(CampiXMatrice);
        var campiY = IdCampi(CampiYMatrice);
        var marcature = MatriceNonApplicabileService.Leggi(_state.Config, campiX, campiY);
        var primaRiga = IndiceOffsetMatrice(OffsetRigheMatrice);
        var primaColonna = IndiceOffsetMatrice(OffsetColonneMatrice);
        var finestra = _matricePreparata.Finestra(primaRiga, _righePerFinestraMatrice,
            primaColonna, _colonnePerFinestraMatrice, marcature, GruppiCompressiMatrice, SoloCelleConDaDecidere);
        var offsetRighe = Math.Clamp(primaRiga, 0, Math.Max(0, finestra.TotaleRighe - _righeUltimaFinestraMatrice));
        var offsetColonne = Math.Clamp(primaColonna, 0, Math.Max(0, finestra.TotaleColonne - _colonneUltimaFinestraMatrice));
        if (offsetRighe != primaRiga || offsetColonne != primaColonna)
        {
            ImpostaOffsetMatriceInterno(offsetRighe, offsetColonne);
            finestra = _matricePreparata.Finestra(offsetRighe, _righePerFinestraMatrice,
                offsetColonne, _colonnePerFinestraMatrice, marcature, GruppiCompressiMatrice, SoloCelleConDaDecidere);
        }
        // Overlay N.A. (decisione utente 6.4.0, punto 4) sulla sola finestra visibile: MatricePreparata.Finestra
        // costruisce oggetti CellaMatriceReport nuovi ad ogni chiamata, quindi non serve "ripulire" marcature
        // di una finestra precedente.
        MatriceNonApplicabileService.Applica(_state.Config, campiX, campiY, finestra.Risultato);
        _totaleRigheMatrice = finestra.TotaleRighe;
        _totaleColonneMatrice = finestra.TotaleColonne;
        ColonneMatrice = finestra.Risultato.Colonne;
        RigheMatrice = finestra.Risultato.Righe;
        TotaliColonnaMatrice = finestra.Risultato.TotaliColonna;
        TotaliColonnaConChiaveMatrice = finestra.Risultato.TotaliColonna
            .Select((totale, indice) => indice < finestra.Risultato.Colonne.Count
                ? new TotaleColonnaMatrice(finestra.Risultato.Colonne[indice], totale) : null)
            .OfType<TotaleColonnaMatrice>().ToList();
        TotaleGeneraleMatrice = finestra.Risultato.TotaleGenerale;
        FasceColonneMatrice = finestra.Risultato.FasceColonne;
        FasceRigheMatrice = finestra.Risultato.FasceRighe;
        OnPropertyChanged(nameof(MatriceVuota));
        NotificaFinestraMatrice();
    }

    /// <summary>Valori distinti di un campo (al suo livello) nella matrice corrente, per il dialogo "Ordina" del campo.</summary>
    public IReadOnlyList<string> ValoriOrdinabiliCampo(CampoAsseMatrice campo)
    {
        if (_matricePreparata == null) return Array.Empty<string>();
        var livello = CampiDi(campo.Righe).IndexOf(campo);
        if (livello < 0) return Array.Empty<string>();
        var foglie = campo.Righe ? _matricePreparata.TutteLeRighe : _matricePreparata.TutteLeColonne;
        return foglie.Select(ReportMatriceService.SpezzaPercorso)
            .Where(parti => parti.Length > livello).Select(parti => parti[livello])
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Ordine personale di una dimensione (per operatore e ruolo), valido a ogni livello in cui compare. Il primo campo
    /// di un asse si riapplica subito; un campo più in profondità ricostruisce la matrice (giro in background).</summary>
    public void ImpostaOrdineCampo(CampoAsseMatrice campo, IReadOnlyList<string>? ordine)
    {
        var dimensione = campo.Dimensione.Id;
        var daSalvare = ordine == null || ordine.Count == 0
            ? null
            : OrdineAssiReportService.Unisci(ordine, OrdineAssiReportService.Leggi(_state.Config, _state.RuoloAttivo, dimensione));
        if (OrdineAssiReportService.Imposta(_state.Config, _state.RuoloAttivo, dimensione, daSalvare))
        {
            try { _state.ConfigService.Save(_state.Config); }
            catch (Exception ex) { CrashLogService.Registra("Salvataggio ordine campo matrice report", ex); }
        }
        if (CampiDi(campo.Righe).IndexOf(campo) == 0)
        {
            ImpostaOrdineAsse(campo.Righe, ordine);
            return;
        }
        GeneraMatrice();
    }

    private void NotificaFinestraMatrice()
    {
        OnPropertyChanged(nameof(RigheVisibiliMatrice));
        OnPropertyChanged(nameof(ColonneVisibiliMatrice));
        OnPropertyChanged(nameof(MassimoOffsetRigheMatrice));
        OnPropertyChanged(nameof(MassimoOffsetColonneMatrice));
    }

    private static int IndiceOffsetMatrice(double offset) =>
        double.IsFinite(offset) ? (int)Math.Clamp(Math.Floor(offset), 0, int.MaxValue) : 0;

    [RelayCommand] private void AggiungiCampoX() => AggiungiCampoMatrice(righe: false);
    [RelayCommand] private void AggiungiCampoY() => AggiungiCampoMatrice(righe: true);
    [RelayCommand] private void RimuoviCampo(CampoAsseMatrice? campo) { if (campo != null) RimuoviCampoMatrice(campo); }
    [RelayCommand] private void SpostaCampoSu(CampoAsseMatrice? campo) { if (campo != null) SpostaCampoMatrice(campo, -1); }
    [RelayCommand] private void SpostaCampoGiu(CampoAsseMatrice? campo) { if (campo != null) SpostaCampoMatrice(campo, 1); }
    [RelayCommand] private void AlternaGruppo(string? chiave) { if (!string.IsNullOrEmpty(chiave)) AlternaGruppoMatrice(chiave); }

    /// <summary>Marca la cella come "N.A." (decisione utente 6.4.0, punto 4), chiedendo una nota opzionale con
    /// lo stesso dialogo già usato per note/descrizioni altrove (TextEditService). Stessi permessi di chi può
    /// generare report: nessun controllo aggiuntivo, la pagina Report è già dietro l'autorizzazione esistente.</summary>
    [RelayCommand]
    private async Task SegnaCellaNonApplicabile(CellaMatriceReport? cella)
    {
        if (cella == null || !cella.Marcabile) return;
        var esistente = MatriceNonApplicabileService.Leggi(_state.Config, IdCampi(CampiXMatrice), IdCampi(CampiYMatrice))
            .FirstOrDefault(m => string.Equals(m.ValoreAsseX, cella.Colonna, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.ValoreAsseY, cella.Riga, StringComparison.OrdinalIgnoreCase));
        var titolo = LocalizationService.Instance.Traduci("MatriceNotaNATitolo");
        var nota = await TextEditService.Modifica(titolo, esistente?.Nota ?? "");
        if (nota == null) return; // dialogo annullato: nessuna modifica
        var notaPulita = string.IsNullOrWhiteSpace(nota) ? null : nota.Trim();
        if (MatriceNonApplicabileService.Imposta(_state.Config, IdCampi(CampiXMatrice), IdCampi(CampiYMatrice), cella.Colonna, cella.Riga, notaPulita))
            _state.ConfigService.Save(_state.Config);
        ApplicaMatrice();
    }

    [RelayCommand]
    private void RimuoviCellaNonApplicabile(CellaMatriceReport? cella)
    {
        if (cella == null || !cella.Marcabile) return;
        if (MatriceNonApplicabileService.Rimuovi(_state.Config, IdCampi(CampiXMatrice), IdCampi(CampiYMatrice), cella.Colonna, cella.Riga))
            _state.ConfigService.Save(_state.Config);
        ApplicaMatrice();
    }

    private ReportFilter CostruisciFiltro()
    {
        var f = new ReportFilter
        {
            DataDa = DataDa?.DateTime,
            DataA = DataA?.DateTime,
            CampiCustom = FiltriCampiCustomReport.ToDictionary(fc => fc.Chiave, fc => fc.Filtro.Selezionati),
        };
        if (!string.IsNullOrEmpty(FiltroEcu)) f.Componente.Add(FiltroEcu);
        if (!string.IsNullOrEmpty(FiltroVeicolo)) f.Veicoli.Add(FiltroVeicolo);
        f.TemplateUid.AddRange(FiltroTemplate.Selezionati.Select(Guid.Parse));
        f.TempoOperazione = FiltroTempoOperazioneAttivo;
        f.TempoTotaleTest = FiltroTempoTotaleTestAttivo;
        return f;
    }

    /// <summary>Ultimo risultato di LeggiFiltratiDaIndice con il suo token (Generazione, revisione dell'indice) e la chiave del
    /// filtro: rivisitando la pagina con filtro invariato e indice invariato, non si rilegge l'archivio. Il risultato e' condiviso
    /// in sola lettura (anteprima e matrice non lo modificano).</summary>
    private (long Generazione, long Versione, string Chiave, List<TestCase> Risultato)? _filtratiIndicizzati;

    /// <summary>Chiave del filtro per la cache: valori ordinati (la query li tratta come insiemi), date in ticks.
    /// Una chiave diversa non produce mai una lettura errata: al peggio riesegue la query.</summary>
    private static string ChiaveFiltroReport(ReportFilter f) => string.Join('\u0001',
        string.Join('\u0002', f.Componente.OrderBy(v => v, StringComparer.Ordinal)),
        string.Join('\u0002', f.Veicoli.OrderBy(v => v, StringComparer.Ordinal)),
        string.Join('\u0002', f.TemplateUid.Select(g => g.ToString("N")).OrderBy(v => v, StringComparer.Ordinal)),
        f.DataDa?.Ticks.ToString() ?? "-",
        f.DataA?.Ticks.ToString() ?? "-",
        f.TempoOperazione?.Etichetta ?? "-",
        f.TempoTotaleTest?.Etichetta ?? "-",
        string.Join('\u0002', f.CampiCustom.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key + '\u0003' + string.Join('\u0004', x.Value.OrderBy(v => v, StringComparer.Ordinal)))));

    [RelayCommand]
    private void GeneraAnteprima()
    {
        NotificaFiltriAttiviCambiati();
        if (_inizializzazioneMatrice) return;
        // Un filtro cambiato rende superata la matrice preparata: nessun giro successivo deve riusarla (vedi CalcolaGiro).
        _matricePreparata = null;
        _anteprimaDaRigenerare = true;
        _matriceDaRigenerare = true;
        if (_refreshInCorso == 0) RichiediGiro(immediato: false);
    }

    [RelayCommand]
    private void ReimpostaFiltri()
    {
        _refreshInCorso++;
        try
        {
            FiltroEcu = null;
            FiltroVeicolo = null;
            DataDa = null;
            DataA = null;
            DataDaTesto = "";
            DataATesto = "";
            FiltroTemplate.DeselezionaTutto();
            foreach (var f in FiltriCampiCustomReport) f.Filtro.DeselezionaTutto();
            foreach (var f in FiltriMatrice) f.Filtro.DeselezionaTutto();
            FiltroStatiMatrice.DeselezionaTutto();
            TestoFiltroTempoOperazione = "";
            TestoFiltroTempoTotaleTest = "";
            OperatoreFiltroTempoOperazione = 0;
            OperatoreFiltroTempoTotaleTest = 0;
        }
        finally { _refreshInCorso--; }
        RichiediGiro(immediato: false);
    }

    partial void OnFiltroEcuChanged(string? value) { ViewStateService.Scrivi(_state, ChiaveVistaFiltroEcu, value); GeneraAnteprima(); }
    partial void OnFiltroVeicoloChanged(string? value) { ViewStateService.Scrivi(_state, ChiaveVistaFiltroVeicolo, value); GeneraAnteprima(); }
    partial void OnDataDaChanged(DateTimeOffset? value) { ViewStateService.ScriviData(_state, ChiaveVistaDataDa, value); GeneraAnteprima(); }
    partial void OnDataAChanged(DateTimeOffset? value) { ViewStateService.ScriviData(_state, ChiaveVistaDataA, value); GeneraAnteprima(); }
    partial void OnDataDaTestoChanged(string value)
    {
        DataDa = ParseData(value);
        var msg = LocalizationService.Instance.Traduci("DataInizialeNonValida");
        if (!string.IsNullOrWhiteSpace(value) && DataDa == null) MessaggioErrore = msg;
        else if (MessaggioErrore == msg) MessaggioErrore = null;
    }
    partial void OnDataATestoChanged(string value)
    {
        DataA = ParseData(value);
        var msg = LocalizationService.Instance.Traduci("DataFinaleNonValida");
        if (!string.IsNullOrWhiteSpace(value) && DataA == null) MessaggioErrore = msg;
        else if (MessaggioErrore == msg) MessaggioErrore = null;
    }

    private static DateTimeOffset? ParseData(string value) => DateTime.TryParseExact(value.Trim(), "dd/MM/yyyy",
        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var data)
        ? new DateTimeOffset(data) : null;

    /// <summary>Scelta fatta nel dialogo di export per l'export in corso (sezioni e colonne): null = tutto, come prima del dialogo.</summary>
    internal ExportConfigurazione? SceltaExportCorrente { get; set; }

    /// <summary>Stato applicativo per il dialogo di export (salvataggio delle configurazioni per operatore).</summary>
    internal AppState StatoPerExport => _state;

    /// <summary>Cosa il Report puo' esportare e quali filtri si possono modificare dal dialogo. I filtri coincidono con quelli a video.</summary>
    internal ExportDescrittore CreaDescrittoreExport()
    {
        var loc = LocalizationService.Instance;
        var sezioni = new List<ExportSezione>
        {
            new(ReportSezioniExport.Vista, loc.Traduci("ExpSezReportVista")),
            new(ReportSezioniExport.Origine, loc.Traduci("ExpSezReportOrigine")),
            new(ReportSezioniExport.Dati, loc.Traduci("ExpSezReportDati"), ReportExportArricchimentiService.ColonneEsportabili()),
            new(ReportSezioniExport.Riepilogo, loc.Traduci("ExpSezReportRiepilogo")),
            new(ReportSezioniExport.Grafici, loc.Traduci("ExpSezReportGrafici")),
            new(ReportSezioniExport.Nc, loc.Traduci("ExpSezReportNc")),
            new(ReportSezioniExport.Info, loc.Traduci("ExpSezReportInfo")),
            new(ReportSezioniExport.Pivot, loc.Scegli("Pivot personalizzata (oltre alle pivot standard)", "Custom pivot (in addition to standard pivots)", "Tableau croisé personnalisé (en plus des tableaux standard)"), PredefinitaAttiva: false),
        };
        DateTime? Leggi(string testo) => DateTime.TryParseExact((testo ?? "").Trim(), "dd/MM/yyyy",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        var filtri = new List<ExportFiltro>
        {
            new ExportFiltroSingolo("componente", loc.Traduci("ColEcu"), EcuDisponibili, () => FiltroEcu, v => FiltroEcu = v),
            new ExportFiltroSingolo("veicolo", loc.Traduci("ColVeicolo"), VeicoliDisponibili, () => FiltroVeicolo, v => FiltroVeicolo = v),
            new ExportFiltroData("da", loc.Traduci("DataDaLabel"), () => Leggi(DataDaTesto), d => DataDaTesto = d?.ToString("dd/MM/yyyy") ?? ""),
            new ExportFiltroData("a", loc.Traduci("DataALabel"), () => Leggi(DataATesto), d => DataATesto = d?.ToString("dd/MM/yyyy") ?? ""),
            new ExportFiltroMultiplo("template", loc.Traduci("TipoTestLabel"), FiltroTemplate),
        };
        filtri.AddRange(FiltriMatrice.Select(f => new ExportFiltroMultiplo("matrice-" + f.Dimensione.Id, f.Dimensione.Etichetta, f.Filtro)));
        filtri.Add(new ExportFiltroMultiplo("matrice-stati", loc.Traduci("MatriceFiltroStatiLabel"), FiltroStatiMatrice));
        filtri.AddRange(FiltriCampiCustomReport.Select(f => new ExportFiltroMultiplo("campo-" + f.Chiave, f.Etichetta, f.Filtro)));
        var campiPivot = ReportExportArricchimentiService.CampiPivotPersonalizzata();
        var fontiPivot = new[] {
            new ExportFontePivot("core", loc.Scegli("Combinazioni veicolo / componente / funzionalità", "Vehicle / componente / function combinations", "Combinaisons véhicule / componente / fonction"), ReportExportArricchimentiService.CampiPivotPersonalizzata(false)),
            new ExportFontePivot("categorie", loc.Scegli("Combinazioni per categoria", "Combinations by category", "Combinaisons par catégorie"), campiPivot),
        };
        return new ExportDescrittore("report", loc.Traduci("ExpDlgTitoloReport"), sezioni, RiepilogoFiltriAttivi, filtri, fontiPivot);
    }

    /// <summary>Chiamato dal code-behind dopo che l'utente ha scelto dove salvare col dialogo nativo
    /// (Addendum: grill-me 2026-08-18, follow-up — prima l'export salvava sempre nella cartella condivisa
    /// fissa senza chiedere). Genera l'Excel e scrive esattamente nel percorso scelto.</summary>
    /// <summary>Genera l'Excel su un thread di background e scrive solo al termine (Addendum: audit UX
    /// 2026-08-26, punto 4): ReportService.GeneraExcel legge TestCases/NonConformita/ecc. tramite gli stessi
    /// repository ora thread-safe (vedi TestCaseRepository._gate), quindi può girare in parallelo a un
    /// salvataggio o a un refresh periodico senza rischio. GenerazioneInCorso disabilita i pulsanti Esporta
    /// dalla view finché non finisce, per evitare due export sovrapposti sullo stesso ViewModel.</summary>
    internal async Task EsportaExcelVerso(string percorso, bool includiTutteLeRigheOrigine = false)
    {
        if (!_state.CartellaValida || GenerazioneInCorso) return;
        // Filtro e configurazione dai controlli visibili adesso, non dall'ultimo giro applicato: un giro in corso
        // non fa mai esportare dati di un filtro precedente.
        var (filtro, configurazioneMatrice, origine) = await PreparaOrigineExportAsync();
        var operatore = _state.Config.OperatoreAttivo;
        if (origine.TotaleCompleto > ReportOrigineExportService.LimiteRigheAggiuntive)
            throw new InvalidOperationException("La selezione supera il limite: scegliere esplicitamente se troncare, creare un archivio ZIP o annullare.");
        GenerazioneInCorso = true;
        try
        {
            await EseguiEsportazioneConGuardiaInterna(percorso, "ReportExcelSalvatoFmt", [Path.GetFileName(percorso)],
                (temporaneo, ct) => File.WriteAllBytes(temporaneo, ReportService.GeneraExcelVista(filtro, _state, operatore, configurazioneMatrice, origine, ct, SceltaExportCorrente)),
                () => VerificaSnapshotReport(origine));
        }
        finally { GenerazioneInCorso = false; }
    }

    public int CalcolaConteggioOriginePerExport()
    {
        if (!_state.CartellaValida || GenerazioneInCorso) return 0;
        var filtro = CostruisciFiltro();
        var config = CostruisciConfigurazioneMatrice(applicaMascheraPresentazione: false);
        return ReportOrigineExportService.CalcolaOrigineCompleta(
            _state.TestCases.GetAll().Where(filtro.CorrispondeMatrice).ToList(), _state, config).TotaleCompleto;
    }

    public async Task<(ReportFilter Filtro, ConfigurazioneMatriceReport Configurazione, ReportOrigineExportService.OriginePreparata Origine)> PreparaOrigineExportAsync(CancellationToken ct = default)
    {
        var filtro = CostruisciFiltro();
        var config = CostruisciConfigurazioneMatrice(applicaMascheraPresentazione: false);
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var letturaMaster = MasterLetturaScope.Apri();
            var masterInizio = CatturaFirmeMasterReportCompleto();
            var firmaConfigInizio = FirmaConfigurazioneReport();
            var testCompleti = _state.TestCases.GetAll();
            var firmeTest = ReportService.CatturaFirmeTestCases(testCompleti, ct);
            var testSnapshot = CloneSnapshot(testCompleti.Where(filtro.Corrisponde), ct);
            var ncSnapshot = CloneMaster(_state.NonConformita.GetAll(), ct);
            var requisiti = CloneMaster(_state.Master.Requisiti.Load(), ct);
            var veicoli = CloneMaster(_state.Master.Veicoli.Load(), ct);
            var funzionalita = CloneMaster(_state.Master.Funzionalita.Load(), ct);
            var templates = CloneMaster(_state.Master.TestTemplate.Load(), ct);
            var componente = CloneMaster(_state.Master.Componente.Load(), ct);
            var manuali = CloneMaster(_state.Master.StatiFunzionalitaEcuVeicolo.Load(), ct);
            var strumenti = CloneMaster(_state.Master.Strumenti.Load(), ct);
            var campiDettagli = CloneMaster(_state.Master.CampiSistemaDettagli.Load(), ct);
            var campiPianificazione = CloneMaster(_state.Master.CampiSistemaPianificazione.Load(), ct);
            var marcature = MatriceNonApplicabileService.Leggi(_state.Config, config.CampiX, config.CampiY);
            var testMatrice = CloneSnapshot(testCompleti.Where(filtro.CorrispondeMatrice), ct);
            var origine = ReportOrigineExportService.CalcolaOrigine(testMatrice, _state, config, ct,
                veicoli, funzionalita, templates, manuali, marcature);
            ct.ThrowIfCancellationRequested();
            if (!masterInizio.OrderBy(x => x.Key).SequenceEqual(CatturaFirmeMasterReportCompleto().OrderBy(x => x.Key))
                || !string.Equals(firmaConfigInizio, FirmaConfigurazioneReport(), StringComparison.Ordinal))
                throw new InvalidOperationException("I dati master o la configurazione sono cambiati durante la preparazione. Riprovare.");
            origine = origine with
            {
                TestCasesVista = testSnapshot.Where(filtro.Corrisponde).ToList(), TestCasesMatrice = testMatrice,
                FirmeTestCompleti = firmeTest, NonConformitaSnapshot = ncSnapshot,
                NonConformitaCompletaSnapshot = ncSnapshot, FirmaNc = ReportService.SnapshotNonConformitaFirma(ncSnapshot),
                FirmaConfig = firmaConfigInizio, FirmeMaster = masterInizio, FirmeTuttiMaster = masterInizio,
                RequisitiSnapshot = requisiti, VeicoliSnapshot = veicoli, FunzionalitaSnapshot = funzionalita,
                TemplatesSnapshot = templates, EcuSnapshot = componente, StatiManualiSnapshot = manuali,
                StrumentiSnapshot = strumenti, CampiDettagliSnapshot = campiDettagli,
                CampiPianificazioneSnapshot = campiPianificazione, MarcatureSnapshot = marcature
            };
            if (!ReportService.FirmeTestCasesUguali(firmeTest, _state.TestCases.GetAll(), ct)
                || !ReportService.SnapshotNonConformitaUguale(ncSnapshot, _state.NonConformita.GetAll()))
                throw new InvalidOperationException("Test o NC sono cambiati durante la preparazione. Riprovare.");
            return (filtro, config, origine);
        }, ct);
    }

    public List<TestCase> TestCasesPerFiltroMatrice(ReportFilter filtro) =>
        _state.TestCases.GetAll().Where(filtro.CorrispondeMatrice).ToList();

    public Task<int> CalcolaConteggioRigheDecisiveAsync(ReportOrigineExportService.OriginePreparata origine,
        ConfigurazioneMatriceReport config, CancellationToken ct = default) => Task.Run(() =>
    {
        using var letturaMaster = MasterLetturaScope.Apri();
        return ReportOrigineExportService.ContaRigheDecisive(origine.TestCasesMatrice!.ToList(), _state, config, ct,
            origine.VeicoliSnapshot, origine.FunzionalitaSnapshot, origine.TemplatesSnapshot,
            origine.StatiManualiSnapshot, origine.MarcatureSnapshot);
    }, ct);

    internal async Task EsportaExcelPreparatoVerso(string percorso, ReportFilter filtro,
        ConfigurazioneMatriceReport configurazioneMatrice, ReportOrigineExportService.OriginePreparata origine,
        bool truncamentoEsplicito = false)
    {
        if (!_state.CartellaValida || GenerazioneInCorso) return;
        var operatore = _state.Config.OperatoreAttivo;
        var limite = ReportOrigineExportService.LimiteRigheAggiuntive;
        var determinanti = origine.Righe.Count(r => r.DeterminaStato);
        var conteggiLimitati = ReportOrigineExportService.CalcolaConteggiLimitati(determinanti,
            Math.Max(0, origine.TotaleCompleto - determinanti), limite);
        if (conteggiLimitati.RichiedeScelta && !truncamentoEsplicito)
            throw new InvalidOperationException("L'origine supera il limite di righe: selezionare ZIP completo, troncamento esplicito o annullamento.");
        var preparata = conteggiLimitati.RichiedeScelta
            ? ReportOrigineExportService.CreaPreparataDaSnapshot(origine,
                ReportOrigineExportService.SelezionaRigheLimitate(origine.Righe, limite))
            : origine;
        var righeEscluse = Math.Max(0, origine.TotaleCompleto - preparata.Righe.Count);
        var origineLimitata = righeEscluse > 0;
        var successo = origineLimitata ? "ReportExcelTroncatoFmt" : "ReportExcelSalvatoFmt";
        await EseguiEsportazioneConGuardiaInterna(percorso, successo,
            origineLimitata ? [Path.GetFileName(percorso), preparata.Righe.Count, righeEscluse] : [Path.GetFileName(percorso)],
            (temporaneo, ct) => File.WriteAllBytes(temporaneo, ReportService.GeneraExcelVista(filtro, _state, operatore, configurazioneMatrice, preparata, ct, SceltaExportCorrente)),
            () => VerificaSnapshotReport(origine));
    }

    public async Task<int> CalcolaConteggioOriginePerExportAsync(CancellationToken ct = default)
    {
        if (!_state.CartellaValida || GenerazioneInCorso) return 0;
        var filtro = CostruisciFiltro();
        var config = CostruisciConfigurazioneMatrice(applicaMascheraPresentazione: false);
        return await Task.Run(() => ReportOrigineExportService.ContaOrigine(
            _state.TestCases.GetAll().Where(filtro.CorrispondeMatrice).ToList(), _state, config, ct), ct);
    }

    internal async Task EsportaExcelPartizionatoVerso(string percorso, ReportFilter? filtroPreparato = null,
        ConfigurazioneMatriceReport? configurazionePreparata = null, ReportOrigineExportService.OriginePreparata? originePreparata = null,
        bool preparaCompleto = false)
    {
        if (!_state.CartellaValida || GenerazioneInCorso) return;
        var (filtro, configurazioneMatrice, full) = originePreparata != null && filtroPreparato != null && configurazionePreparata != null
            ? (filtroPreparato, configurazionePreparata, originePreparata)
            : await PreparaOrigineExportAsync();
        var operatore = _state.Config.OperatoreAttivo;
        GenerazioneInCorso = true;
        try
        {
            if (preparaCompleto)
            {
                full = await Task.Run(() =>
                {
                    using var letturaMaster = MasterLetturaScope.Apri();
                    return ReportOrigineExportService.CalcolaOrigineCompleta(
                        full.TestCasesMatrice!.ToList(), _state, configurazioneMatrice,
                        veicoliSnapshot: full.VeicoliSnapshot, funzionalitaSnapshot: full.FunzionalitaSnapshot,
                        templatesSnapshot: full.TemplatesSnapshot, manualiSnapshot: full.StatiManualiSnapshot,
                        marcatureSnapshot: full.MarcatureSnapshot);
                });
                full = full with
                {
                    TestCasesVista = originePreparata!.TestCasesVista,
                    TestCasesMatrice = originePreparata.TestCasesMatrice,
                    TestCasesCompletiSnapshot = originePreparata.TestCasesCompletiSnapshot,
                    FirmeTestCompleti = originePreparata.FirmeTestCompleti,
                    NonConformitaSnapshot = originePreparata.NonConformitaSnapshot,
                    NonConformitaCompletaSnapshot = originePreparata.NonConformitaCompletaSnapshot,
                    FirmaConfig = originePreparata.FirmaConfig,
                    FirmeMaster = originePreparata.FirmeMaster,
                    FirmeTuttiMaster = originePreparata.FirmeTuttiMaster,
                    FirmaNc = originePreparata.FirmaNc,
                    RequisitiSnapshot = originePreparata.RequisitiSnapshot,
                    VeicoliSnapshot = originePreparata.VeicoliSnapshot,
                    FunzionalitaSnapshot = originePreparata.FunzionalitaSnapshot,
                    TemplatesSnapshot = originePreparata.TemplatesSnapshot,
                    EcuSnapshot = originePreparata.EcuSnapshot,
                    StatiManualiSnapshot = originePreparata.StatiManualiSnapshot,
                    StrumentiSnapshot = originePreparata.StrumentiSnapshot,
                    CampiDettagliSnapshot = originePreparata.CampiDettagliSnapshot,
                    CampiPianificazioneSnapshot = originePreparata.CampiPianificazioneSnapshot,
                    MarcatureSnapshot = originePreparata.MarcatureSnapshot
                };
            }
            if (full.TotaleCompleto <= ReportOrigineExportService.LimiteRigheAggiuntive)
            {
                await EseguiEsportazioneConGuardiaInterna(percorso, "ReportExcelSalvatoFmt", [Path.GetFileName(percorso)],
                    (temporaneo, ct) => File.WriteAllBytes(temporaneo, ReportService.GeneraExcelVista(filtro, _state, operatore, configurazioneMatrice, full, ct, SceltaExportCorrente)),
                    () => VerificaSnapshotReport(full));
                return;
            }
            var righePerParte = Math.Max(1, ReportOrigineExportService.LimiteRigheAggiuntive);
            await EseguiEsportazioneConGuardiaInterna(percorso, "ReportExcelPartiSalvateFmt", [Path.GetFileName(percorso), (full.Righe.Count + righePerParte - 1) / righePerParte],
                (temporaneo, ct) =>
                {
                    using var fs = File.Create(temporaneo);
                    ReportService.ScriviExcelVistaPartizionato(fs, filtro, _state, operatore, configurazioneMatrice,
                        full.Righe, full.TotaleCompleto, righePerParte, ct, full, SceltaExportCorrente);
                }, () => VerificaSnapshotReport(full));
        }
        finally { GenerazioneInCorso = false; }
    }

    public async Task EsportaPdfVerso(string percorso)
    {
        if (!_state.CartellaValida || GenerazioneInCorso) return;
        var filtro = CostruisciFiltro();
        var operatore = _state.Config.OperatoreAttivo;
        var configurazioneMatrice = CostruisciConfigurazioneMatrice(applicaMascheraPresentazione: false);
        await EseguiEsportazione(percorso, "ReportPdfSalvatoFmt",
            (temporaneo, ct) => File.WriteAllBytes(temporaneo, PdfReportService.GeneraPdf(filtro, _state, operatore, configurazioneMatrice, ct)));
    }

    /// <summary>Annulla l'export Excel/PDF in corso: nessun file viene creato e un eventuale file gia presente resta intatto.</summary>
    [RelayCommand]
    private void AnnullaEsportazione() => _esportazione.Annulla();

    /// <summary>Esegue l'export su thread di background (vedi EsportazioneInBackground) e riporta l'esito.
    /// GenerazioneInCorso disabilita i pulsanti Esporta e mostra Annulla finche l'operazione non e conclusa.</summary>
    private async Task EseguiEsportazione(string percorso, string chiaveSuccesso, Action<string, CancellationToken> scrivi)
        => await EseguiEsportazioneInterna(percorso, chiaveSuccesso, [Path.GetFileName(percorso)], scrivi);

    private async Task EseguiEsportazioneConGuardiaInterna(string percorso, string chiaveSuccesso, object[] argomenti,
        Action<string, CancellationToken> scrivi, Action primaDelCommit)
    {
        GenerazioneInCorso = true;
        EsitoEsportazione esito;
        try { esito = await _esportazione.EseguiAsync(percorso, scrivi, primaDelCommit); }
        finally { GenerazioneInCorso = false; }
        MostraEsitoEsportazione(percorso, chiaveSuccesso, argomenti, esito);
    }

    private async Task EseguiEsportazioneInterna(string percorso, string chiaveSuccesso, object[] argomenti, Action<string, CancellationToken> scrivi)
    {
        GenerazioneInCorso = true;
        EsitoEsportazione esito;
        try { esito = await _esportazione.EseguiAsync(percorso, scrivi); }
        finally { GenerazioneInCorso = false; }
        MostraEsitoEsportazione(percorso, chiaveSuccesso, argomenti, esito);
    }

    private void MostraEsitoEsportazione(string percorso, string chiaveSuccesso, object[] argomenti, EsitoEsportazione esito)
    {
        try
        {
            switch (esito.Stato)
            {
                case StatoEsportazione.Completata:
                    RegistraSalvataggio(percorso);
                    Messaggio = string.Format(LocalizationService.Instance.Traduci(chiaveSuccesso), argomenti);
                    MessaggioErrore = null;
                    break;
                case StatoEsportazione.Annullata:
                    Messaggio = LocalizationService.Instance.Traduci("EsportazioneAnnullata");
                    MessaggioErrore = null;
                    break;
                default:
                    MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("GenerazioneReportNonRiuscitaFmt"), esito.Errore?.Message);
                    break;
            }
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("GenerazioneReportNonRiuscitaFmt"), ex.Message);
        }
        CaricaCronologia();
    }

    private void VerificaSnapshotReport(ReportOrigineExportService.OriginePreparata snapshot)
    {
        if (snapshot.TestCasesVista is not { } testsVista || snapshot.TestCasesMatrice is not { } testsMatrice)
            throw new InvalidOperationException("Snapshot del report non disponibile. Riprovare.");
        var firmeTest = snapshot.FirmeTestCompleti;
        if (firmeTest == null || !ReportService.FirmeTestCasesUguali(firmeTest, _state.TestCases.GetAll()))
            throw new InvalidOperationException("I Test sono cambiati durante l'esportazione. Riprovare.");
        var ncSnapshot = snapshot.NonConformitaSnapshot;
        if (ncSnapshot == null || !ReportService.SnapshotNonConformitaUguale(ncSnapshot, _state.NonConformita.GetAll()))
            throw new InvalidOperationException("Le NC sono cambiate durante l'esportazione. Riprovare.");
        var firmeCorrenti = CatturaFirmeMasterReport();
        if (!ReportService.SnapshotExtraReportUguale(_state, snapshot,
                ReportService.SnapshotNonConformitaFirma(ncSnapshot)))
            throw new InvalidOperationException("Configurazione o dati master sono cambiati durante l'esportazione. Riprovare.");
    }

    private string FirmaConfigurazioneReport() => System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { _state.Config.MatriceNonApplicabili, _state.Config.OperatoreAttivo })));

    private static List<TestCase> CloneSnapshot(IEnumerable<TestCase> tests, CancellationToken ct = default)
    {
        var risultato = new List<TestCase>();
        foreach (var tc in tests)
        {
            ct.ThrowIfCancellationRequested();
            risultato.Add(System.Text.Json.JsonSerializer.Deserialize<TestCase>(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(tc, JsonIo.Options), JsonIo.Options)!);
        }
        return risultato;
    }

    private static List<T> CloneMaster<T>(IEnumerable<T> records, CancellationToken ct = default)
    {
        var risultato = new List<T>();
        foreach (var item in records)
        {
            ct.ThrowIfCancellationRequested();
            risultato.Add(System.Text.Json.JsonSerializer.Deserialize<T>(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(item, JsonIo.Options), JsonIo.Options)!);
        }
        return risultato;
    }

    private Dictionary<string, string> CatturaFirmeMasterReport() => new(StringComparer.Ordinal)
    {
        [nameof(_state.Master.Veicoli)] = _state.Master.Veicoli.CatturaImprontaLettura(),
        [nameof(_state.Master.Funzionalita)] = _state.Master.Funzionalita.CatturaImprontaLettura(),
        [nameof(_state.Master.TestTemplate)] = _state.Master.TestTemplate.CatturaImprontaLettura(),
        [nameof(_state.Master.Componente)] = _state.Master.Componente.CatturaImprontaLettura(),
        [nameof(_state.Master.Requisiti)] = _state.Master.Requisiti.CatturaImprontaLettura(),
        [nameof(_state.Master.CampiSistemaDettagli)] = _state.Master.CampiSistemaDettagli.CatturaImprontaLettura(),
        [nameof(_state.Master.CampiSistemaPianificazione)] = _state.Master.CampiSistemaPianificazione.CatturaImprontaLettura(),
        [nameof(_state.Master.StatiFunzionalitaEcuVeicolo)] = _state.Master.StatiFunzionalitaEcuVeicolo.CatturaImprontaLettura()
    };

    private Dictionary<string, string> CatturaFirmeMasterReportCompleto() => new(StringComparer.Ordinal)
    {
        [nameof(_state.Master.Veicoli)] = _state.Master.Veicoli.CatturaImprontaLettura(),
        [nameof(_state.Master.Funzionalita)] = _state.Master.Funzionalita.CatturaImprontaLettura(),
        [nameof(_state.Master.TestTemplate)] = _state.Master.TestTemplate.CatturaImprontaLettura(),
        [nameof(_state.Master.Componente)] = _state.Master.Componente.CatturaImprontaLettura(),
        [nameof(_state.Master.Requisiti)] = _state.Master.Requisiti.CatturaImprontaLettura(),
        [nameof(_state.Master.Gamma)] = _state.Master.Gamma.CatturaImprontaLettura(),
        [nameof(_state.Master.Strumenti)] = _state.Master.Strumenti.CatturaImprontaLettura(),
        [nameof(_state.Master.CampiSistemaDettagli)] = _state.Master.CampiSistemaDettagli.CatturaImprontaLettura(),
        [nameof(_state.Master.CampiSistemaPianificazione)] = _state.Master.CampiSistemaPianificazione.CatturaImprontaLettura(),
        [nameof(_state.Master.StatiFunzionalitaEcuVeicolo)] = _state.Master.StatiFunzionalitaEcuVeicolo.CatturaImprontaLettura()
    };

    private void RegistraSalvataggio(string percorso)
    {
        _state.Config.CronologiaReport = ReportCronologiaService.Aggiungi(_state.Config.CronologiaReport, percorso, DateTime.Now);
        _state.ConfigService.Save(_state.Config);
    }

    private void CaricaCronologia()
    {
        Cronologia.Clear();
        foreach (var voce in _state.Config.CronologiaReport)
            Cronologia.Add(new ReportGenerato { NomeFile = Path.GetFileName(voce.Percorso), Percorso = voce.Percorso, Data = voce.Data });
    }

    [RelayCommand]
    private void ApriFile(ReportGenerato? r)
    {
        if (r == null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Percorso) { UseShellExecute = true }); }
        catch { MessaggioErrore = LocalizationService.Instance.Traduci("ImpossibileAprireFile"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generazioneGiro++;
        AnnullaRitardo();
        if (_linguaCambiata is { } handler)
        {
            LocalizationService.Instance.PropertyChanged -= handler;
            _linguaCambiata = null;
        }
        FiltroTemplate.Cambiato -= GeneraAnteprima;
        FiltroStatiMatrice.Cambiato -= GeneraMatrice;
        foreach (var filtro in FiltriMatrice) filtro.Filtro.Cambiato -= GeneraMatrice;
        foreach (var filtro in FiltriCampiCustomReport) filtro.Filtro.Cambiato -= GeneraAnteprima;
    }
}
