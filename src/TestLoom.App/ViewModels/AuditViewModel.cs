using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Cella pronta per il rendering in-app: a differenza dell'esportazione (solo numeri puri o N/A),
/// qui restano visibili tutti gli stati, incluso Conflitto (Addendum: grill-me 2026-08-18, follow-up — "la
/// matrice all'interno dell'app deve far vedere chiaramente tutti gli stati possibili"; seconda follow-up —
/// "se i voti non corrispondono dovrebbe generarsi un alert").</summary>
public class CellaMatriceAuditVisual
{
    public string Componente { get; init; } = "";
    public string RequisitoId { get; init; } = "";
    public StatoCellaAudit Stato { get; init; }
    public PunteggioConformita? Punteggio { get; init; }
    public List<CandidatoConflitto> Candidati { get; init; } = new();
    public bool InConflitto => Stato == StatoCellaAudit.Conflitto;

    /// <summary>Vero quando Punteggio viene da un inserimento manuale di un Master, non da un'Esecuzione di
    /// Test (feedback utente 2026-08-26) — Testo/ToolTip lo segnalano esplicitamente perché il valore non è
    /// riproducibile ri-eseguendo i Test collegati, va tenuto a mente durante la revisione.</summary>
    public bool InserimentoManuale { get; init; }
    public string MotivazioneInserimentoManuale { get; init; } = "";

    /// <summary>Origine del valore quando Stato=Valutato ma NON è un inserimento manuale legacy
    /// (InserimentoManuale sopra, RisoluzioneConflittoAudit senza dimensione veicolo): Diretto segue l'ultima
    /// esecuzione del Test sul veicolo selezionato, Manuale/Propagata vengono da una riga salvata di
    /// ValutazioneRequisitoEcuVeicolo (Piano 6.3.1 §1) — vedi AuditMatriceService.CellaMatriceAudit.Origine.</summary>
    public OrigineCellaRequisitoEcuVeicolo? Origine { get; init; }

    /// <summary>Testo pronto del tooltip quando Origine è Manuale/Propagata (ValutazioneRequisitoEcuVeicoloService.TestoTooltip)
    /// oppure quando Stato=PropagabileNonConfermato (elenco sintetico dei veicoli sorgente).</summary>
    public string? TooltipOrigineVeicolo { get; init; }
    public string? DettaglioConflitto { get; init; }
    public string? Nota { get; init; }
    public string? BaselineSw { get; init; }

    /// <summary>Popolato solo quando Stato=PropagabileNonConfermato: veicoli della stessa componente da cui
    /// QUESTO veicolo (il sottoveicolo selezionato in matrice) potrebbe copiare un valore — vedi
    /// ValutazioneRequisitoEcuVeicoloService.CandidatiPropagazione.</summary>
    public IReadOnlyDictionary<string, CandidatoPropagazioneRequisito> CandidatiPropagazione { get; init; } =
        new Dictionary<string, CandidatoPropagazioneRequisito>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fix 6.4.0 (R4): vedi AuditMatriceService.CellaMatriceAudit.ConflittoCollisioneCella — una
    /// collisione di chiave business Phase D (due postazioni hanno scritto valori diversi quasi in
    /// contemporanea sulla stessa cella, catalogo audit-valutazioni-requisito-componente-veicolo) con valori
    /// discordanti mai ancora rivista da un operatore. Solo lettura di un campo già calcolato dal servizio:
    /// nessun lavoro pesante qui, coerente con le altre proprietà computate di questa cella.</summary>
    public bool ConflittoCollisioneCella { get; init; }

    public string Testo => Stato switch
    {
        StatoCellaAudit.DaTestare => "—",
        StatoCellaAudit.TestCreatoNonEseguito => "◷",
        StatoCellaAudit.Conflitto => "⚠",
        // Piano 6.3.1 §1: la cella "propagabile ma non confermata" mostra "!" invece del numero che la
        // vecchia logica propagava in silenzio — l'operatore deve decidere esplicitamente (vedi "!" click).
        StatoCellaAudit.PropagabileNonConfermato => "!",
        // Fix 6.4.0 (R4): prefisso "⚠" davanti al marcatore di origine — coerente con il simbolo già usato per
        // StatoCellaAudit.Conflitto — quando questo valore Valutato porta ancora una collisione di cella con
        // valori discordanti non chiusa (vedi ConflittoCollisioneCella): l'operatore deve accorgersi che non è
        // un valore ordinario, senza perdere l'informazione su Manuale/Propagata già presente.
        StatoCellaAudit.Valutato when ConflittoCollisioneCella && InserimentoManuale => "⚠✎" + (Punteggio == PunteggioConformita.NonApplicabile ? "N/A" : ((int)Punteggio!.Value).ToString()),
        StatoCellaAudit.Valutato when ConflittoCollisioneCella && Origine == OrigineCellaRequisitoEcuVeicolo.Propagata => "⚠↳" + (Punteggio == PunteggioConformita.NonApplicabile ? "N/A" : ((int)Punteggio!.Value).ToString()),
        StatoCellaAudit.Valutato when ConflittoCollisioneCella && Origine == OrigineCellaRequisitoEcuVeicolo.Manuale => "⚠✎" + (Punteggio == PunteggioConformita.NonApplicabile ? "N/A" : ((int)Punteggio!.Value).ToString()),
        StatoCellaAudit.Valutato when ConflittoCollisioneCella => "⚠" + (Punteggio == PunteggioConformita.NonApplicabile ? "N/A" : ((int)Punteggio!.Value).ToString()),
        StatoCellaAudit.Valutato when InserimentoManuale => "✎" + (Punteggio == PunteggioConformita.NonApplicabile ? "N/A" : ((int)Punteggio!.Value).ToString()),
        // Marcatura visiva discreta e distinta dall'inserimento manuale legacy (Piano 6.3.1 §1 UI, punto 4):
        // "↳" per un valore Propagato (copiato esplicitamente da un altro veicolo), "✎" per un Manuale
        // veicolo-specifico (stesso segno dell'inserimento manuale legacy, stesso concetto — "valore digitato
        // da un operatore", solo con dimensione veicolo aggiuntiva).
        StatoCellaAudit.Valutato when Origine == OrigineCellaRequisitoEcuVeicolo.Propagata => "↳" + (Punteggio == PunteggioConformita.NonApplicabile ? "N/A" : ((int)Punteggio!.Value).ToString()),
        StatoCellaAudit.Valutato when Origine == OrigineCellaRequisitoEcuVeicolo.Manuale => "✎" + (Punteggio == PunteggioConformita.NonApplicabile ? "N/A" : ((int)Punteggio!.Value).ToString()),
        StatoCellaAudit.Valutato when Punteggio == PunteggioConformita.NonApplicabile => "N/A",
        StatoCellaAudit.Valutato => ((int)Punteggio!.Value).ToString(),
        _ => "",
    };

    public string ToolTip => Stato switch
    {
        StatoCellaAudit.DaTestare => LocalizationService.Instance.Traduci("AuditDaTestareTip"),
        StatoCellaAudit.TestCreatoNonEseguito => LocalizationService.Instance.Traduci("AuditTestCreatoNonEseguitoTip"),
        StatoCellaAudit.PropagabileNonConfermato => TooltipOrigineVeicolo ?? LocalizationService.Instance.Traduci("AuditPropagabileTip"),
        // Fix 6.4.0 (R4): antepone l'avviso di collisione al tooltip ordinario della cella (che resta intatto,
        // incluso il dettaglio del consolidamento già confluito in Nota — vedi ConsolidaCollisioni).
        StatoCellaAudit.Valutato when ConflittoCollisioneCella =>
            LocalizationService.Instance.Traduci("AuditConflittoValoriTip") +
            (InserimentoManuale
                ? string.Format(LocalizationService.Instance.Traduci("AuditValoreAttualeManualeFmt"), Punteggio!.Value.Label(), MotivazioneInserimentoManuale)
                : string.Format(LocalizationService.Instance.Traduci("AuditValoreAttualeFmt"), TooltipOrigineVeicolo ?? Punteggio!.Value.Label())),
        StatoCellaAudit.Valutato when InserimentoManuale => string.Format(LocalizationService.Instance.Traduci("AuditInseritoManoFmt"), Punteggio!.Value.Label(), MotivazioneInserimentoManuale),
        StatoCellaAudit.Valutato when Origine is OrigineCellaRequisitoEcuVeicolo.Manuale or OrigineCellaRequisitoEcuVeicolo.Propagata => TooltipOrigineVeicolo ?? Punteggio!.Value.Label(),
        StatoCellaAudit.Valutato => Punteggio!.Value.Label(),
        StatoCellaAudit.Conflitto => DettaglioConflitto ?? ("Conflitto: " + string.Join(" / ", Candidati.Select(c => $"{c.TestCaseId} = {(c.Punteggio == PunteggioConformita.NonApplicabile ? "N/A" : (int)c.Punteggio)}"))),
        _ => "",
    };

    public string Colore => Stato switch
    {
        StatoCellaAudit.DaTestare => "#F1F5F9",
        StatoCellaAudit.TestCreatoNonEseguito => "#FEF3C7",
        StatoCellaAudit.Conflitto => "#FCA5A5",
        // Arancio distinto dal giallo "test creato" (Piano 6.3.1 §1 UI): stato azionabile, diverso da uno
        // stato di attesa passiva.
        StatoCellaAudit.PropagabileNonConfermato => "#FDBA74",
        // Fix 6.4.0 (R4): stesso rosso di StatoCellaAudit.Conflitto — una cella Valutato con una collisione di
        // cella con valori discordanti non chiusa è un conflitto da rivedere, non un valore ordinario, e deve
        // risaltare allo stesso modo indipendentemente dal punteggio vincente.
        StatoCellaAudit.Valutato when ConflittoCollisioneCella => "#FCA5A5",
        StatoCellaAudit.Valutato when Punteggio == PunteggioConformita.NonApplicabile => "#E2E8F0",
        StatoCellaAudit.Valutato when (int)Punteggio!.Value <= 1 => "#FECACA",
        StatoCellaAudit.Valutato when (int)Punteggio!.Value <= 3 => "#FDE68A",
        StatoCellaAudit.Valutato => "#BBF7D0",
        _ => "#FFFFFF",
    };
}

/// <summary>Un veicolo a cui il dialogo "!" (AuditView.ApriPropagaFlyout) propone di propagare la stessa
/// sorgente/valore scelti dall'operatore — vedi AuditViewModel.VeicoliPropagabiliPer.</summary>
public sealed record VeicoloPropagabileVoce(string Veicolo, CandidatoPropagazioneRequisito Candidato, string ImprontaAttesa);

/// <summary>Anteprima di un'applicazione manuale a riga/colonna: celle vuote e celle con un valore visibile
/// entrano nel batch; ogni valore presente richiede conferma esplicita di sovrascrittura.</summary>
public sealed record AnteprimaApplicazioneManualeAudit(
    IReadOnlyList<CellaApplicazioneManualeAuditAttesa> Celle,
    int CelleNonModificabili)
{
    public int CelleDaCompilare => Celle.Count(c => !c.Sovrascrive);
    public int CelleDaSovrascrivere => Celle.Count(c => c.Sovrascrive);
    public bool HaCelleModificabili => Celle.Count > 0;
}

public class RigaMatriceAudit
{
    public string RequisitoId { get; init; } = "";

    /// <summary>Testo del requisito (feedback utente 2026-08-26): mostrato nel tooltip dell'intestazione riga
    /// perché il solo Id non basta a capire di cosa tratti il requisito senza aprire Impostazioni.</summary>
    public string TestoRequisito { get; init; } = "";

    /// <summary>Avanzamento guasto "N/M guasto" del requisito Extra (6.5.0), calcolato in background; vuoto per Standard.</summary>
    public string DtcTesto { get; set; } = "";
    /// <summary>Numero di valutazioni Manuali/Propagate veicolo-specifiche che il Master può rimuovere in
    /// blocco per questa riga e il veicolo selezionato. Conta i record salvati anche quando un Test diretto
    /// più recente è la fonte attualmente mostrata dalla cella.</summary>
    public int NumeroValutazioniRimuovibili { get; init; }
    public Dictionary<string, CellaMatriceAuditVisual> Celle { get; } = new();
}

/// <summary>Pannello Audit dedicato (Addendum: grill-me 2026-08-18, follow-up — "come home, test, anomalie,
/// report dovremmo avere audit con i vari filtri e dashboard disponibili"): un selettore di Sottoveicolo
/// mostra la matrice completa Requisito×componente di quel sottoveicolo (righe = tutti i Requisiti attivi, colonne =
/// tutti i componenti associate), mentre le card KPI in alto riassumono lo stato su TUTTI i sottoveicoli insieme.
/// Distinto dall'anteprima sparsa dentro Report (solo coppie testate): qui compare sempre ogni coppia, con
/// uno dei quattro stati di CellaMatriceAuditVisual.</summary>
public partial class AuditViewModel : ViewModelBase, IPaginaAggiornabile
{
    private readonly AppState _state;
    private LocalTestArchiveQueryService? _archiveQuery;
    private Dictionary<string, int> _conteggiValutazioniRimuovibiliPerEcu = new(StringComparer.OrdinalIgnoreCase);

    public bool EMaster => _state.EMaster;

    public List<string> SottoveicoliDisponibili { get; private set; } = new();
    [ObservableProperty] private string? sottoveicoloSelezionato;

    public List<string> EcuColonne { get; private set; } = new();
    /// <summary>Righe dei requisiti STANDARD (6.5.0: i Extra sono in <see cref="RigheD4d"/>, lista separata).</summary>
    public ObservableCollection<RigaMatriceAudit> Righe { get; } = new();
    /// <summary>Righe dei requisiti Extra (6.5.0): sezione separata, con propri totali.</summary>
    public ObservableCollection<RigaMatriceAudit> RigheD4d { get; } = new();

    /// <summary>Filtro per tipo di requisito (Standard/Extra), multi-selezione: nessuna selezione = entrambi.</summary>
    public FiltroMultiSelect FiltroTipoRequisito { get; } = CreaFiltroTipo();
    private static FiltroMultiSelect CreaFiltroTipo()
    {
        var f = new FiltroMultiSelect();
        f.Popola(new[] { ("Standard", "Standard"), ("Extra", "Extra") });
        return f;
    }
    private bool TipoIncluso(string tipo) => !FiltroTipoRequisito.HaSelezione || FiltroTipoRequisito.Selezionati.Contains(tipo);
    public bool HaFiltroTipoAttivo => FiltroTipoRequisito.HaSelezione;
    public void AzzeraFiltroTipo() => FiltroTipoRequisito.DeselezionaTutto();
    public bool MostraSezioneStandard => TipoIncluso("Standard");
    public bool MostraSezioneD4d => TipoIncluso("Extra") && (RigheD4d.Count > 0 || D4dKpi != null);
    public string TitoloSezioneStandard => $"Standard · {Righe.Count}";
    public string TitoloSezioneD4d => $"Extra · {RigheD4d.Count}";
    public AuditKpiComplessivi? D4dKpi { get; private set; }
    public string RiepilogoDtcD4d { get; private set; } = "";
    public string RiepilogoD4d => D4dKpi == null ? "" :
        (RiepilogoDtcD4d.Length > 0 ? RiepilogoDtcD4d + " · " : "") + $"Extra: {D4dKpi.TotaleValutate} valutate · {D4dKpi.TotaleTestCreato} test creato · {D4dKpi.TotaleDaTestare} da testare · {D4dKpi.TotaleInConflitto} conflitti · {D4dKpi.PercentualeValutato:0}%"
        + (D4dKpi.PunteggioMedio.HasValue ? $" · media {D4dKpi.PunteggioMedio.Value:0.0}" : "");
    public bool MatriceVuota => EcuColonne.Count == 0 || (Righe.Count == 0 && RigheD4d.Count == 0);

    /// <summary>True se, nella matrice del sottoveicolo selezionato, esiste almeno una cella "!"
    /// (PropagabileNonConfermato) su QUALSIASI componente — condizione di visibilità del pulsante "Propaga tutti i
    /// '!' di tutti i componenti" (Piano 6.4.0, punto 1: visibile/abilitato solo per un Master e solo se esistono
    /// celle "!" nel perimetro). Ricalcolato ad ogni RicaricaMatrice, non richiede un giro separato sul
    /// catalogo: la matrice già in memoria (Righe) ha tutto quel che serve.</summary>
    public bool HaCellePropagabiliVeicoloSelezionato =>
        Righe.Concat(RigheD4d).Any(r => r.Celle.Values.Any(c => c.Stato == StatoCellaAudit.PropagabileNonConfermato));

    /// <summary>Condizione di visibilità/abilitazione del pulsante "Propaga tutti i '!' di tutti i componenti"
    /// (Piano 6.4.0, punto 1): solo un Master, e solo se il veicolo selezionato ha almeno una cella "!".</summary>
    public bool PuoPropagareVeicolo => EMaster && HaCellePropagabiliVeicoloSelezionato;

    /// <summary>Come sopra ma per UNA sola colonna componente — visibilità del pulsante "Propaga tutti i '!' di
    /// questa componente" sulla relativa intestazione di colonna (Piano 6.4.0, punto 1).</summary>
    public bool EcuHaCellePropagabili(string ecuNome) =>
        Righe.Concat(RigheD4d).Any(r => r.Celle.TryGetValue(ecuNome, out var c) && c.Stato == StatoCellaAudit.PropagabileNonConfermato);

    /// <summary>Numero di valutazioni veicolo-specifiche Manuali/Propagate attive nella colonna componente, contando
    /// solo requisiti attivi. Alimenta il comando di rimozione in intestazione senza una nuova lettura per cella.</summary>
    public int NumeroValutazioniRimuovibiliColonna(string ecuNome) =>
        _conteggiValutazioniRimuovibiliPerEcu.GetValueOrDefault(ecuNome);

    /// <summary>La View ricostruisce la griglia (colonne dinamiche = componente) in code-behind ogni volta che questo
    /// evento scatta, invece di affidarsi al binding automatico di un DataGrid a colonne fisse.</summary>
    public event Action? MatriceAggiornata;

    [ObservableProperty] private int totaleCoppieValutate;
    [ObservableProperty] private int totaleCoppieDaTestare;
    [ObservableProperty] private int totaleCoppieTestCreato;
    [ObservableProperty] private int totaleCoppieInConflitto;
    /// <summary>KPI aggregato (Piano 6.3.1 §1, seconda consegna, punto (a) — "vista aggregata": conta le
    /// coppie Componente×Requisito×Veicolo "!" su TUTTI i veicoli, indipendentemente dal sottoveicolo selezionato in
    /// matrice, esattamente come le altre card KPI sopra. Non ridefinisce/tocca il calcolo esistente di
    /// TotaleCoppieValutate/DaTestare/TestCreato/InConflitto (già sottratte separatamente in
    /// AuditMatriceService.CalcolaKpiComplessivi da prima di questa consegna): questo è un contatore
    /// AGGIUNTIVO e visibile, non una ridefinizione silenziosa dei numeri esistenti.</summary>
    [ObservableProperty] private int totaleCoppiePropagabili;
    [ObservableProperty] private double percentualeValutato;
    [ObservableProperty] private double? punteggioMedio;
    public bool HaPunteggioMedio => PunteggioMedio.HasValue;
    public bool HaConflitti => TotaleCoppieInConflitto > 0;
    public bool HaPropagabili => TotaleCoppiePropagabili > 0;
    partial void OnTotaleCoppieInConflittoChanged(int value) => OnPropertyChanged(nameof(HaConflitti));
    partial void OnTotaleCoppiePropagabiliChanged(int value) => OnPropertyChanged(nameof(HaPropagabili));

    public ObservableCollection<ReportGenerato> Cronologia { get; } = new();

    [ObservableProperty] private string? messaggio;
    public bool HaMessaggio => !string.IsNullOrEmpty(Messaggio);
    [ObservableProperty] private string? messaggioErrore;
    public bool HaMessaggioErrore => !string.IsNullOrEmpty(MessaggioErrore);
    partial void OnMessaggioChanged(string? value) => OnPropertyChanged(nameof(HaMessaggio));

    [RelayCommand]
    private void CancellaMessaggio() => Messaggio = null;
    partial void OnMessaggioErroreChanged(string? value) => OnPropertyChanged(nameof(HaMessaggioErrore));

    private const string ChiaveVista = "Audit.Sottoveicolo";

    public AuditViewModel(AppState state, Action<string?>? navigaATest = null)
    {
        _state = state;
        InizializzaDettaglioDtc(navigaATest);
        // Filtro Tipo requisito: PRINCIPALE (sempre visibile, FiltriPrincipaliConfig), valore mantenuto per PC
        // (ViewStateService, config locale); opzioni fisse quindi si lega subito, valori obsoleti ignorati.
        FiltroTipoRequisito.LegaPersistenza(state, "Audit.FiltroTipoRequisito");
        FiltroTipoRequisito.Cambiato += () =>
        {
            OnPropertyChanged(nameof(HaFiltroTipoAttivo));
            OnPropertyChanged(nameof(MostraSezioneStandard));
            OnPropertyChanged(nameof(MostraSezioneD4d));
            MatriceAggiornata?.Invoke();
        };
        Aggiorna();
    }

    /// <summary>Ripopola i sottoveicoli disponibili, KPI complessivi, cronologia e matrice (Addendum: audit
    /// prestazioni 2026-08-25, IPaginaAggiornabile — questa pagina resta viva tra un cambio scheda e l'altro).
    /// Se il sottoveicolo prima selezionato esiste ancora nell'elenco ricaricato resta quello. Alla PRIMA
    /// apertura (SottoveicoloSelezionato ancora null: un nuovo processo, non solo un cambio scheda) si ricade
    /// sull'ultima selezione persistita per questo PC/operatore (feedback utente 2026-09-18: "chiudendo e
    /// riaprendo l'app... mi torna al selettore veicolo iniziale") invece che sempre sul primo disponibile.</summary>
    public void Aggiorna()
    {
        // Una sola lettura logica dei master per l'intera pagina (stesso principio di Home/Report,
        // MasterLetturaScope): senza questo, Aggiorna() -> CalcolaKpiComplessivi() -> RicaricaMatrice() rifà
        // per conto proprio la scansione/ricostruzione Phase D di componente/Requisiti/Veicoli/Risoluzioni/Valutazioni
        // fino a due volte ciascuna (~9-10 Load() non accorpate), misurato ~3.8s a freddo su soli 575 NC/507
        // Test (feedback utente 2026-09-24: "il passaggio da una pagina all'altra è molto lento").
        using var letturaMaster = MasterLetturaScope.Apri();
        var precedente = SottoveicoloSelezionato ?? ViewStateService.Leggi(_state, ChiaveVista);
        SottoveicoliDisponibili = _state.CartellaValida
            ? _state.Master.Veicoli.Load().Where(v => !v.Archiviata).Select(v => v.Nome).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : new();
        OnPropertyChanged(nameof(SottoveicoliDisponibili));

        CalcolaKpiComplessivi();
        CaricaCronologia();

        var nuovaSelezione = precedente != null && SottoveicoliDisponibili.Contains(precedente) ? precedente : SottoveicoliDisponibili.FirstOrDefault();
        if (nuovaSelezione == SottoveicoloSelezionato) RicaricaMatrice(); // invariato: OnSottoveicoloSelezionatoChanged non scatterebbe da solo
        else SottoveicoloSelezionato = nuovaSelezione; // scatena OnSottoveicoloSelezionatoChanged -> RicaricaMatrice
    }

    partial void OnSottoveicoloSelezionatoChanged(string? value)
    {
        ViewStateService.Scrivi(_state, ChiaveVista, value);
        RicaricaMatrice();
    }

    private static VoceSelezionabile VoceStringa(string s) => new() { Valore = s, TestoPrincipale = s, TestoRicerca = s };

    /// <summary>Sopra la soglia di 10 elementi (feedback utente 2026-08-26), lo stesso dialog di ricerca già
    /// usato per il campo Veicolo dell'editor Test (TestCaseEditorViewModel.ScegliVeicolo) invece della
    /// ComboBox, scomoda da scorrere con molti sottoveicoli — vedi SelezionaDialogService.</summary>
    [RelayCommand]
    private async Task ScegliSottoveicolo()
    {
        var voce = await SelezionaDialogService.Scegli(LocalizationService.Instance.Traduci("ScegliVeicoloTitolo"), SottoveicoliDisponibili.Select(VoceStringa).ToList());
        if (voce != null) SottoveicoloSelezionato = (string)voce.Valore;
    }

    private void RicaricaMatrice()
    {
        // Vedi Aggiorna(): stesso accorpamento, per quando questo metodo gira da solo (cambio sottoveicolo).
        using var letturaMaster = MasterLetturaScope.Apri();
        Righe.Clear();
        RigheD4d.Clear();
        EcuColonne = new List<string>();
        _conteggiValutazioniRimuovibiliPerEcu = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (_state.CartellaValida && !string.IsNullOrEmpty(SottoveicoloSelezionato))
        {
            var tutteLeEcu = _state.Master.Componente.Load();
            var requisiti = _state.Master.Requisiti.Load();
            var risoluzioni = _state.Master.RisoluzioniConflittoAudit.Load();
            var valutazioni = _state.Master.ValutazioniRequisitoEcuVeicolo.Load();

            EcuColonne = AuditMatriceService.EcuDelSottoveicolo(tutteLeEcu, SottoveicoloSelezionato);
            var testCases = TryLeggiTestDelleEcuDaIndice(EcuColonne) ?? _state.TestCases.GetAll();
            var (reqStandard, reqD4d) = AuditMatriceService.SeparaPerTipo(requisiti);
            var indice = AuditMatriceService.IndicizzaTestPerEcuERequisito(testCases);
            var celle = AuditMatriceService.Matrice(SottoveicoloSelezionato, tutteLeEcu, reqStandard, testCases, risoluzioni, indice, valutazioni);
            var celleD4d = reqD4d.Count == 0 ? new List<CellaMatriceAudit>()
                : AuditMatriceService.Matrice(SottoveicoloSelezionato, tutteLeEcu, reqD4d, testCases, risoluzioni, indice, valutazioni);
            var testoPerRequisito = requisiti.ToDictionary(r => r.Id, r => r.Testo, StringComparer.OrdinalIgnoreCase);
            var ecusDelVeicolo = new HashSet<string>(EcuColonne, StringComparer.OrdinalIgnoreCase);
            var requisitiAttivi = new HashSet<string>(requisiti.Where(r => !r.Archiviata).Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
            _conteggiValutazioniRimuovibiliPerEcu = valutazioni
                .Where(v => !v.IsArchiviata &&
                    v.Veicolo.Equals(SottoveicoloSelezionato, StringComparison.OrdinalIgnoreCase) &&
                    ecusDelVeicolo.Contains(v.Componente) &&
                    requisitiAttivi.Contains(v.RequisitoId) &&
                    v.Origine is OrigineValutazioneRequisito.Manuale or OrigineValutazioneRequisito.Propagata)
                .GroupBy(v => v.Componente, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var conteggiValutazioniRimuovibili = valutazioni
                .Where(v => !v.IsArchiviata &&
                    v.Veicolo.Equals(SottoveicoloSelezionato, StringComparison.OrdinalIgnoreCase) &&
                    ecusDelVeicolo.Contains(v.Componente) &&
                    v.Origine is OrigineValutazioneRequisito.Manuale or OrigineValutazioneRequisito.Propagata)
                .GroupBy(v => v.RequisitoId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            int ConteggiaValutazioniRimuovibili(string requisitoId) =>
                conteggiValutazioniRimuovibili.GetValueOrDefault(requisitoId);
            // Ordinamento naturale (feedback utente 2026-08-26: "vedo solo fino alla riga ST99 nonostante
            // arrivino a 1700+"): con StringComparer.OrdinalIgnoreCase "ST-100" veniva prima di "ST-99"
            // (confronto cifra per cifra, '1' < '9'), mescolando le righe in un ordine che sembrava troncato
            // pur non mancando nessuna riga — vedi NaturalStringComparer.
            foreach (var gruppo in celle.GroupBy(c => c.RequisitoId).OrderBy(g => g.Key, NaturalStringComparer.Instance))
            {
                var riga = new RigaMatriceAudit
                {
                    RequisitoId = gruppo.Key,
                    TestoRequisito = testoPerRequisito.GetValueOrDefault(gruppo.Key, ""),
                    NumeroValutazioniRimuovibili = ConteggiaValutazioniRimuovibili(gruppo.Key),
                };
                foreach (var cella in gruppo)
                    riga.Celle[cella.Componente] = new CellaMatriceAuditVisual
                    {
                        Componente = cella.Componente, RequisitoId = cella.RequisitoId, Stato = cella.Stato, Punteggio = cella.Punteggio, Candidati = cella.Candidati,
                        InserimentoManuale = cella.InserimentoManuale, MotivazioneInserimentoManuale = cella.MotivazioneInserimentoManuale,
                        Origine = cella.Origine, TooltipOrigineVeicolo = cella.Tooltip, Nota = cella.Nota, BaselineSw = cella.BaselineSw,
                        DettaglioConflitto = cella.DettaglioConflitto,
                        CandidatiPropagazione = cella.CandidatiPropagazione, ConflittoCollisioneCella = cella.ConflittoCollisioneCella,
                    };
                Righe.Add(riga);
            }
            foreach (var gruppo in celleD4d.GroupBy(c => c.RequisitoId).OrderBy(g => g.Key, NaturalStringComparer.Instance))
            {
                var riga = new RigaMatriceAudit
                {
                    RequisitoId = gruppo.Key,
                    TestoRequisito = testoPerRequisito.GetValueOrDefault(gruppo.Key, ""),
                    NumeroValutazioniRimuovibili = ConteggiaValutazioniRimuovibili(gruppo.Key),
                };
                foreach (var cella in gruppo)
                    riga.Celle[cella.Componente] = new CellaMatriceAuditVisual
                    {
                        Componente = cella.Componente, RequisitoId = cella.RequisitoId, Stato = cella.Stato, Punteggio = cella.Punteggio, Candidati = cella.Candidati,
                        InserimentoManuale = cella.InserimentoManuale, MotivazioneInserimentoManuale = cella.MotivazioneInserimentoManuale,
                        Origine = cella.Origine, TooltipOrigineVeicolo = cella.Tooltip, Nota = cella.Nota, BaselineSw = cella.BaselineSw,
                        DettaglioConflitto = cella.DettaglioConflitto,
                        CandidatiPropagazione = cella.CandidatiPropagazione, ConflittoCollisioneCella = cella.ConflittoCollisioneCella,
                    };
                RigheD4d.Add(riga);
            }
        }
        OnPropertyChanged(nameof(MostraSezioneStandard));
        OnPropertyChanged(nameof(MostraSezioneD4d));
        OnPropertyChanged(nameof(TitoloSezioneStandard));
        OnPropertyChanged(nameof(TitoloSezioneD4d));
        _ = CaricaDtcD4dAsync();

        OnPropertyChanged(nameof(EcuColonne));
        OnPropertyChanged(nameof(MatriceVuota));
        OnPropertyChanged(nameof(HaCellePropagabiliVeicoloSelezionato));
        OnPropertyChanged(nameof(PuoPropagareVeicolo));
        MatriceAggiornata?.Invoke();
    }

    private CancellationTokenSource? _dtcCts;

    /// <summary>Avanzamento guasto dei requisiti Extra (6.5.0, nessuna pagina dedicata): totale "N/M guasto completi" nel
    /// riepilogo della sezione Extra e "k/n guasto" per ogni requisito Extra. Calcolo in background (DtcAvanzamentoProvider:
    /// SQLite in streaming), poi ricostruzione della sezione; mai sul thread UI.</summary>
    private async Task CaricaDtcD4dAsync()
    {
        _dtcCts?.Cancel();
        if (!_state.CartellaValida || RigheD4d.Count == 0) { RiepilogoDtcD4d = ""; return; }
        var cts = _dtcCts = new CancellationTokenSource();
        try
        {
            var risultato = await Task.Run(() =>
            {
                var tuttiReq = _state.Master.Requisiti.Load();
                _state.Master.RequisitiDtc.Risolvi(tuttiReq); // DtcConteggio null/0 = ignoto: verifica economica sugli archivi
                var reqs = tuttiReq.Where(r => !r.Archiviata && RequisitoApplicabilita.ED4D(r) && r.DtcTotale > 0).ToList();
                return reqs.Count == 0 ? null : DtcAvanzamentoProvider.CalcolaRiepilogo(_state, reqs, cts.Token);
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            RiepilogoDtcD4d = risultato == null ? "" : string.Format(LocalizationService.Instance.Traduci("DtcRiepilogoReqFmt"), risultato.Totale.Completi, risultato.Totale.Totale);
            var perReq = risultato?.PerRequisito.ToDictionary(r => r.RequisitoId, StringComparer.OrdinalIgnoreCase);
            foreach (var riga in RigheD4d)
                riga.DtcTesto = perReq != null && perReq.TryGetValue(riga.RequisitoId, out var r) ? $"{r.Completi}/{r.Totale} guasto" : "";
            OnPropertyChanged(nameof(RiepilogoD4d));
            MatriceAggiornata?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CrashLogService.Registra("Avanzamento guasto Audit", ex); }
    }

    private void CalcolaKpiComplessivi()
    {
        if (!_state.CartellaValida) return;

        // Vedi Aggiorna(): stesso accorpamento, per quando questo metodo gira da solo.
        using var letturaMaster = MasterLetturaScope.Apri();
        var tutteLeEcu = _state.Master.Componente.Load();
        var requisiti = _state.Master.Requisiti.Load();
        var testCases = _state.TestCases.GetAll();
        var veicoli = _state.Master.Veicoli.Load().Where(v => !v.Archiviata);
        var risoluzioni = _state.Master.RisoluzioniConflittoAudit.Load();
        var valutazioni = _state.Master.ValutazioniRequisitoEcuVeicolo.Load();

        // 6.5.0: KPI Standard e Extra SEPARATI - le card in alto sono solo Standard; il riepilogo Extra e a parte.
        var (reqStandard, reqD4d) = AuditMatriceService.SeparaPerTipo(requisiti);
        var veicoliLista = veicoli.ToList();
        var indice = AuditMatriceService.IndicizzaTestPerEcuERequisito(testCases);
        var kpi = AuditMatriceService.CalcolaKpiComplessivi(veicoliLista, tutteLeEcu, reqStandard, testCases, risoluzioni, indice, valutazioni);
        D4dKpi = reqD4d.Any(r => !r.Archiviata)
            ? AuditMatriceService.CalcolaKpiComplessivi(veicoliLista, tutteLeEcu, reqD4d, testCases, risoluzioni, indice, valutazioni)
            : null;
        OnPropertyChanged(nameof(D4dKpi));
        OnPropertyChanged(nameof(RiepilogoD4d));
        TotaleCoppieValutate = kpi.TotaleValutate;
        TotaleCoppieDaTestare = kpi.TotaleDaTestare;
        TotaleCoppieTestCreato = kpi.TotaleTestCreato;
        TotaleCoppieInConflitto = kpi.TotaleInConflitto;
        TotaleCoppiePropagabili = kpi.TotalePropagabiliNonConfermati;
        PercentualeValutato = kpi.PercentualeValutato;
        PunteggioMedio = kpi.PunteggioMedio;
    }

    /// <summary>La matrice selezionata deve leggere i Test dei componenti associate al sottoveicolo. Il legame
    /// autorevole passa dal catalogo componente: un Test puo' non ripetere il veicolo nei propri campi. I KPI di
    /// apertura restano globali per contratto; qui il filtro utente non riclonera' l'archivio completo.</summary>
    private List<TestCase>? TryLeggiTestDelleEcuDaIndice(IReadOnlyCollection<string> componente)
    {
        if (!_state.TestCases.HasPersistentSnapshot || string.IsNullOrWhiteSpace(_state.TestCases.LocalIndexDatabasePath)) return null;
        if (componente.Count == 0) return new List<TestCase>();
        try
        {
            _archiveQuery ??= new LocalTestArchiveQueryService(_state.TestCases.LocalIndexDatabasePath!, _state.TestCases.IndexRecovery);
            if (_archiveQuery.Generazione != _state.TestCases.IndexRecovery?.Generazione) _archiveQuery = new LocalTestArchiveQueryService(_state.TestCases.LocalIndexDatabasePath!, _state.TestCases.IndexRecovery);
            // Filtro selettivo: Test materializzati dall'indice. Filtro non selettivo ("tutti i componenti"): snapshot gia'
            // caricato, senza ricreare l'archivio da SQLite (vedi AuditSelezioneTestPerEcu). Il ripiego su GetAll()
            // resta solo per un errore reale di lettura dell'indice.
            return AuditSelezioneTestPerEcu.Leggi(_archiveQuery, _state.TestCases.GetAll(), componente);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Audit filtrato su indice locale", ex);
            return null;
        }
    }

    /// <summary>Applica una modifica al catalogo condiviso delle risoluzioni Audit con optimistic-concurrency
    /// sullo snapshot appena letto. Il lock UI non protegge da repliche OneDrive offline/delayed: se un altro
    /// PC pubblica tra Load e Save, la modifica locale deve fallire chiusa invece di sovrascrivere il file.
    /// La funzione di mutazione restituisce false per un no-op (per esempio rimozione di una voce inesistente).</summary>
    private bool ProvaAggiornaRisoluzioni(Func<List<RisoluzioneConflittoAudit>, bool> mutazione)
    {
        var repo = _state.Master.RisoluzioniConflittoAudit;
        var risoluzioni = repo.Load();
        var expectedSnapshot = EntityStateFingerprint.Clone(risoluzioni);
        if (!mutazione(risoluzioni)) return false;

        try
        {
            repo.Save(risoluzioni, expectedSnapshot);
            MessaggioErrore = null;
            return true;
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
            return false;
        }
    }

    private static void UnisciStoricoLegacy(RisoluzioneConflittoAudit destinazione, RisoluzioneConflittoAudit sorgente)
    {
        destinazione.StoricoPunteggiManuali ??= new List<SnapshotValutazioneAudit>();
        foreach (var evento in sorgente.StoricoPunteggiManuali ?? new List<SnapshotValutazioneAudit>())
        {
            if (destinazione.StoricoPunteggiManuali.Any(e => e.EventId == evento.EventId)) continue;
            destinazione.StoricoPunteggiManuali.Add(new SnapshotValutazioneAudit
            {
                EventId = evento.EventId,
                Origine = evento.Origine,
                Punteggio = evento.Punteggio,
                Data = evento.Data,
                Operatore = evento.Operatore,
                Nota = evento.Nota,
                BaselineSw = evento.BaselineSw,
                VeicoloSorgente = evento.VeicoloSorgente,
                TestCaseId = evento.TestCaseId,
                IterazioneTest = evento.IterazioneTest,
            });
        }
    }

    /// <summary>Un Master indica quale Test è autorevole per una coppia componente×Requisito in conflitto (Addendum:
    /// grill-me 2026-08-18, seconda follow-up — "un Master può marcare quale Test è quello valido"): non
    /// congela il punteggio, punta al Test — se quel Test viene ritestato la cella segue comunque la sua
    /// revisione più recente. Motivazione obbligatoria, stesso principio di EsitoSovrascritto/
    /// MotivazioneOverride già usato altrove per le correzioni manuali.</summary>
    public void RisolviConflitto(string componente, string requisitoId, string testCaseIdValido, string motivazione)
    {
        if (!_state.EMaster || string.IsNullOrWhiteSpace(motivazione)) return;

        if (!ProvaAggiornaRisoluzioni(risoluzioni =>
            {
                var corrispondenti = risoluzioni.Where(r =>
                    r.Componente.Equals(componente, StringComparison.OrdinalIgnoreCase) &&
                    r.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase)).ToList();
                var risoluzione = corrispondenti.FirstOrDefault();
                if (risoluzione == null)
                {
                    risoluzione = new RisoluzioneConflittoAudit { Componente = componente, RequisitoId = requisitoId };
                    risoluzioni.Add(risoluzione);
                }
                else
                {
                    ValutazioneRequisitoEcuVeicoloService.ArchiviaPunteggioManualeLegacy(risoluzione);
                    foreach (var duplicata in corrispondenti.Skip(1))
                    {
                        ValutazioneRequisitoEcuVeicoloService.ArchiviaPunteggioManualeLegacy(duplicata);
                        UnisciStoricoLegacy(risoluzione, duplicata);
                        risoluzioni.Remove(duplicata);
                    }
                }
                risoluzione.PunteggioManuale = null;
                risoluzione.EventoPunteggioManualeCorrenteId = null;
                risoluzione.TestCaseIdValido = testCaseIdValido;
                risoluzione.Motivazione = motivazione.Trim();
                risoluzione.UltimaModifica = DateTime.Now;
                risoluzione.ModificatoDa = _state.Config.OperatoreAttivo;
                return true;
            })) return;

        CalcolaKpiComplessivi();
        RicaricaMatrice();
    }

    /// <summary>API legacy per dati componente×Requisito senza veicolo, mantenuta per compatibilità e importazioni
    /// storiche. La view Audit usa SalvaValutazioneManualeVeicoloSelezionato per ogni nuovo inserimento, così
    /// le nuove valutazioni non diventano globali.</summary>
    public void SalvaValutazioneManuale(string componente, string requisitoId, PunteggioConformita valore, string motivazione)
    {
        if (!_state.EMaster || string.IsNullOrWhiteSpace(motivazione)) return;

        if (!ProvaAggiornaRisoluzioni(risoluzioni =>
            {
                var risoluzione = risoluzioni.FirstOrDefault(r =>
                    r.Componente.Equals(componente, StringComparison.OrdinalIgnoreCase) &&
                    r.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase));
                if (risoluzione == null)
                {
                    risoluzione = new RisoluzioneConflittoAudit { Componente = componente, RequisitoId = requisitoId };
                    risoluzioni.Add(risoluzione);
                }
                else
                    ValutazioneRequisitoEcuVeicoloService.ArchiviaPunteggioManualeLegacy(risoluzione);

                // Questa API è mantenuta per compatibilità; conserva la semantica legacy di sostituzione
                // della scelta Test quando si crea un nuovo punteggio globale.
                risoluzione.TestCaseIdValido = "";
                risoluzione.PunteggioManuale = valore;
                risoluzione.EventoPunteggioManualeCorrenteId = Guid.NewGuid();
                risoluzione.Motivazione = motivazione.Trim();
                risoluzione.UltimaModifica = DateTime.Now;
                risoluzione.ModificatoDa = _state.Config.OperatoreAttivo;
                return true;
            })) return;

        CalcolaKpiComplessivi();
        RicaricaMatrice();
    }

    /// <summary>Rimuove un inserimento manuale (feedback utente 2026-08-26): la cella torna a seguire l'esito
    /// calcolato dai Test collegati (o "Da testare"/"Conflitto" se non ce n'è ancora uno univoco) invece di
    /// restare bloccata sul valore digitato a mano. Non tocca una RisoluzioneConflittoAudit "scelta tra
    /// candidati" (TestCaseIdValido) eventualmente presente per la stessa coppia — è un caso diverso.</summary>
    public void RimuoviValutazioneManuale(string componente, string requisitoId)
    {
        if (!_state.EMaster) return;

        if (!ProvaAggiornaRisoluzioni(risoluzioni =>
            {
                var manuali = risoluzioni.Where(r =>
                    r.Componente.Equals(componente, StringComparison.OrdinalIgnoreCase) &&
                    r.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase) &&
                    r.PunteggioManuale.HasValue).ToList();
                foreach (var manuale in manuali)
                {
                    ValutazioneRequisitoEcuVeicoloService.ArchiviaPunteggioManualeLegacy(manuale);
                    manuale.PunteggioManuale = null;
                    manuale.EventoPunteggioManualeCorrenteId = null;
                    // Non rimuove la riga né modifica TestCaseIdValido: può contenere una scelta di Test
                    // indipendente che deve restare disponibile dopo l'archiviazione del punteggio.
                }
                return manuali.Count > 0;
            })) return;

        CalcolaKpiComplessivi();
        RicaricaMatrice();
    }

    // --- Piano 6.3.1 §1 UI: propagazione esplicita e inserimento manuale con dimensione veicolo ---
    // A differenza di RisolviConflitto/SalvaValutazioneManuale sopra (catalogo RisoluzioneConflittoAudit,
    // per coppia Componente×Requisito, invariato), questi metodi scrivono sul nuovo catalogo
    // ValutazioneRequisitoEcuVeicolo (per Componente×Requisito×Veicolo) tramite ValutazioneRequisitoEcuVeicoloService.
    // Stessi permessi (solo Master), stesso schema fail-closed su conflitto multi-PC (MessaggioErrore,
    // nessuna sovrascrittura silenziosa, la matrice si ricarica dallo snapshot su disco più recente).

    /// <summary>Veicoli associati alla stessa componente, usati dal flusso distinto di propagazione esplicita "!".
    /// I nuovi inserimenti manuali Audit restano limitati al sottoveicolo selezionato.</summary>
    public IReadOnlyList<string> VeicoliDellaEcu(string ecuNome)
    {
        if (!_state.CartellaValida) return Array.Empty<string>();
        var componente = _state.Master.Componente.Load().FirstOrDefault(e => e.Nome.Equals(ecuNome, StringComparison.OrdinalIgnoreCase));
        return componente?.VeicoliAssociati.Where(v => !string.IsNullOrWhiteSpace(v)).ToList() ?? new List<string>();
    }

    /// <summary>Altri veicoli della stessa componente che, per la stessa coppia Componente×Requisito, propagherebbero
    /// ESATTAMENTE la stessa sorgente/valore scelti dall'operatore nel dialogo "!" (Piano 6.3.1 §1 UI, punto
    /// 2: "elenco dei veicoli propagabili"): calcolato ricalcolando RisolviCella per ciascun veicolo
    /// associato al componente (costo limitato al numero di veicoli di un componente, non l'intera matrice — chiamato solo
    /// all'apertura del dialogo, mai nel percorso caldo RicaricaMatrice).</summary>
    public List<VeicoloPropagabileVoce> VeicoliPropagabiliPer(string ecuNome, string requisitoId, CandidatoPropagazioneRequisito sorgenteScelta)
    {
        var risultato = new List<VeicoloPropagabileVoce>();
        if (!_state.CartellaValida) return risultato;

        var componente = _state.Master.Componente.Load().FirstOrDefault(e => e.Nome.Equals(ecuNome, StringComparison.OrdinalIgnoreCase));
        if (componente == null) return risultato;

        var testCases = _state.TestCases.GetAll();
        var testDellaCoppia = testCases
            .Where(tc => tc.Componente.Contains(ecuNome, StringComparer.OrdinalIgnoreCase) && tc.RequisitoId.Contains(requisitoId, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var valutazioniDellaCoppia = _state.Master.ValutazioniRequisitoEcuVeicolo.Load()
            .Where(v => v.Componente.Equals(ecuNome, StringComparison.OrdinalIgnoreCase) && v.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var risoluzioniDellaCoppia = _state.Master.RisoluzioniConflittoAudit.Load()
            .Where(r => r.Componente.Equals(ecuNome, StringComparison.OrdinalIgnoreCase) && r.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var veicolo in componente.VeicoliAssociati)
        {
            if (string.IsNullOrWhiteSpace(veicolo)) continue;
            var risoluzione = risoluzioniDellaCoppia.FirstOrDefault();
            var cella = ValutazioneRequisitoEcuVeicoloService.RisolviCella(
                requisitoId, componente, veicolo, testDellaCoppia, valutazioniDellaCoppia, risoluzione);
            if (!cella.Propagabile) continue;
            if (!cella.CandidatiPropagazione.TryGetValue(sorgenteScelta.VeicoloSorgente, out var candidato)) continue;
            if (candidato != sorgenteScelta) continue; // stessa sorgente ma valore/data ormai diversi: non proponibile insieme
            var impronta = ValutazioneRequisitoEcuVeicoloService.ImprontaPropagazioneCella(
                requisitoId, componente, veicolo, testDellaCoppia, valutazioniDellaCoppia, risoluzioniDellaCoppia);
            risultato.Add(new VeicoloPropagabileVoce(veicolo, candidato, impronta));
        }
        return risultato;
    }

    /// <summary>Conferma la propagazione a uno o più veicoli scelti dall'operatore dal dialogo "!" (Piano
    /// 6.3.1 §1 UI, punto 2). Stesso schema fail-closed su conflitto multi-PC delle altre scritture Master di
    /// questa pagina.</summary>
    public void Propaga(string componente, string requisitoId, IReadOnlyList<VeicoloPropagabileVoce> voci, string nota)
    {
        if (!_state.EMaster || string.IsNullOrWhiteSpace(nota)) return;

        // È l'istante della conferma esplicita dell'operatore: la stessa data viene persistita nel record
        // Propagata, così una REV creata successivamente può superarlo per timestamp anche se salva prima.
        var dataConferma = DateTime.Now;

        var ecuMaster = _state.Master.Componente.Load().FirstOrDefault(e => e.Nome.Equals(componente, StringComparison.OrdinalIgnoreCase));
        if (ecuMaster == null || voci.Any(v => !ecuMaster.VeicoliAssociati.Contains(v.Veicolo, StringComparer.OrdinalIgnoreCase)))
        {
            MessaggioErrore = "Il componente o un veicolo selezionato non è più associato alla propagazione.";
            RicaricaMatrice();
            return;
        }

        var testCases = _state.TestCases.GetAll();
        var risoluzioni = _state.Master.RisoluzioniConflittoAudit.Load();
        var attese = voci.Select(v => new VocePropagazioneAuditAttesa(v.Veicolo, v.Candidato, v.ImprontaAttesa)).ToList();
        var ok = ValutazioneRequisitoEcuVeicoloService.PropagaVerificando(
            _state.Master.ValutazioniRequisitoEcuVeicolo, requisitoId, ecuMaster, attese,
            testCases, risoluzioni, nota, _state.Config.OperatoreAttivo, out var errore,
            dataConferma, RileggiTestAutorevoliPerPropagazione);
        MessaggioErrore = errore == null ? null : string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), errore);
        if (!ok)
        {
            // errore != null significa che repo.Save ha lanciato (tipicamente MasterDataConflictException:
            // un'altra postazione ha pubblicato tra Load e Save): mai una sovrascrittura silenziosa, la
            // matrice si ricarica dall'ultimo stato davvero su disco invece di restare bloccata su uno stantio.
            // errore == null è solo una validazione locale fallita (nota/veicoli mancanti): niente da ricaricare.
            if (errore != null) RicaricaMatrice();
            return;
        }

        CalcolaKpiComplessivi();
        RicaricaMatrice();
    }

    /// <summary>Anteprima (nessuna scrittura) della Propagazione massiva per il veicolo selezionato (Piano
    /// 6.4.0, punto 1): <paramref name="ecuNome"/> nullo/vuoto propaga su Tutti i componenti del sottoveicolo
    /// selezionato ("Propaga tutti i '!' di tutti i componenti"), altrimenti solo su quella componente ("Propaga tutti i
    /// '!' di questa componente"). Quando <paramref name="requisitoId"/> è specificato, restringe inoltre l'anteprima
    /// alla sola riga requisito, attraversando tutti i componenti associate al veicolo. Legge i master una sola volta (stesso principio delle altre letture di pagina) e
    /// delega interamente il calcolo a ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva —
    /// nessuna logica di propagazione duplicata qui.</summary>
    public AnteprimaPropagazioneMassiva AnteprimaPropagazioneMassiva(string? ecuNome, string? requisitoId = null)
    {
        if (!_state.CartellaValida || string.IsNullOrEmpty(SottoveicoloSelezionato))
            return new AnteprimaPropagazioneMassiva(Array.Empty<VocePropagazioneMassiva>(), Array.Empty<ConflittoPropagazioneMassiva>());

        using var letturaMaster = MasterLetturaScope.Apri();
        var tutteLeEcu = _state.Master.Componente.Load();
        var requisiti = _state.Master.Requisiti.Load();
        var risoluzioni = _state.Master.RisoluzioniConflittoAudit.Load();
        var valutazioni = _state.Master.ValutazioniRequisitoEcuVeicolo.Load();
        var testCases = _state.TestCases.GetAll();

        var ecuDaConsiderare = AuditMatriceService.EcuDelSottoveicolo(tutteLeEcu, SottoveicoloSelezionato)
            .Where(e => string.IsNullOrEmpty(ecuNome) || e.Equals(ecuNome, StringComparison.OrdinalIgnoreCase))
            .Select(nome => tutteLeEcu.First(e => e.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var requisitiAttivi = requisiti.Where(r => !r.Archiviata &&
            (string.IsNullOrWhiteSpace(requisitoId) || r.Id.Equals(requisitoId, StringComparison.OrdinalIgnoreCase))).ToList();

        return ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            SottoveicoloSelezionato, ecuDaConsiderare, requisitiAttivi, testCases, valutazioni, risoluzioni);
    }

    /// <summary>Conferma la Propagazione massiva (Piano 6.4.0, punti 4-6): <paramref name="celleAttese"/> è
    /// <see cref="AnteprimaPropagazioneMassiva"/>.DaPropagare (o un sottoinsieme) mostrato nell'anteprima e
    /// confermato dall'operatore. Rilegge da disco componente/Requisiti/Test/RisoluzioniConflittoAudit/Valutazioni
    /// TUTTI freschi in questo momento (non riusa nulla calcolato per l'anteprima, che può risalire a prima
    /// che l'operatore finisse di leggere il dialogo): il servizio ricalcola lo stato di ciascuna cella
    /// attesa su questi dati e scrive solo quelle che risolvono ancora a un'unica sorgente con lo stesso
    /// punteggio confermato — la garanzia stessa vive nel servizio (ValutazioneRequisitoEcuVeicoloService.
    /// PropagaMassiva), non qui: questo metodo si limita a procurargli dati davvero freschi. Una cella
    /// cambiata nel frattempo (da un altro Test, un'altra valutazione salvata o una risoluzione conflitto)
    /// viene saltata, mai sovrascritta. Aggiorna KPI e matrice al termine, con lo stesso messaggio
    /// riepilogativo propagate/saltate/conflitti richiesto dal piano. Fail-closed SENZA retry automatico: se
    /// il salvataggio fallisce per una scrittura concorrente, non è stato scritto nulla e l'operatore deve
    /// ripetere l'intera azione (anteprima + conferma) da capo — vedi il messaggio d'errore mostrato.</summary>
    public void ConfermaPropagazioneMassiva(string? ecuNome, IReadOnlyList<VocePropagazioneMassiva> celleAttese, string nota)
    {
        if (!_state.EMaster || string.IsNullOrEmpty(SottoveicoloSelezionato)) return;

        // Catturare la data sul confine della conferma, prima di rileggere le sorgenti e comporre il batch.
        var dataConferma = DateTime.Now;

        var tutteLeEcu = _state.Master.Componente.Load();
        var ecuDaConsiderare = AuditMatriceService.EcuDelSottoveicolo(tutteLeEcu, SottoveicoloSelezionato)
            .Where(e => string.IsNullOrEmpty(ecuNome) || e.Equals(ecuNome, StringComparison.OrdinalIgnoreCase))
            .Select(nome => tutteLeEcu.First(e => e.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var requisitiAttivi = _state.Master.Requisiti.Load().Where(r => !r.Archiviata).ToList();
        var testCases = _state.TestCases.GetAll();
        var risoluzioni = _state.Master.RisoluzioniConflittoAudit.Load();

        var risultato = ValutazioneRequisitoEcuVeicoloService.PropagaMassiva(
            _state.Master.ValutazioniRequisitoEcuVeicolo, SottoveicoloSelezionato, ecuDaConsiderare, requisitiAttivi,
            testCases, risoluzioni, celleAttese, nota, _state.Config.OperatoreAttivo, out var errore,
            dataConferma, RileggiTestAutorevoliPerPropagazione);

        if (errore != null)
        {
            // MasterDataConflictException dal Save: rara scrittura concorrente durante la finestra Load→Save
            // di QUESTA chiamata (distinta dal caso normale, molto più frequente, gestito dentro il servizio:
            // una cella cambiata fra anteprima e conferma finisce in risultato.SaltatePerchèCambiate senza
            // errore). Qui invece non è stato scritto NULLA — nessun retry automatico: il messaggio lo dice
            // esplicitamente, l'operatore deve ripetere l'intera Propagazione massiva da capo.
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("AuditPropagaMassivaErroreSalvataggioFmt"), errore);
            RicaricaMatrice();
            return;
        }

        MessaggioErrore = null;
        Messaggio = string.Format(LocalizationService.Instance.Traduci("AuditPropagaMassivaRiepilogoFmt"),
            risultato.Propagate.Count, risultato.SaltatePerchèCambiate.Count, risultato.ConflittiRimasti.Count);

        CalcolaKpiComplessivi();
        RicaricaMatrice();
    }

    /// <summary>Rilegge il catalogo Test al confine immediatamente precedente al commit Phase D. Il refresh è
    /// valido solo se la fotografia persistente è completa e senza file irrisolti, duplicati o fork: in caso
    /// contrario la propagazione viene annullata prima del Save. Non esiste un mutex distribuito condiviso
    /// tra archivio Test e Phase D (né fra PC OneDrive/SharePoint), quindi resta una piccola finestra fra questa
    /// lettura e il commit Phase D; non si acquisiscono lock Test esternamente per non invertire l'ordine
    /// _gate → lock per-Test del repository.</summary>
    private IReadOnlyList<TestCase> RileggiTestAutorevoliPerPropagazione()
    {
        _state.TestCases.RefreshIndex();
        if (!_state.TestCases.HasPersistentSnapshot ||
            _state.TestCases.FileConProblemi.Count > 0 ||
            _state.TestCases.PercorsiConProblemiTracciati.Count > 0 ||
            _state.TestCases.DuplicatiId.Count > 0 ||
            _state.TestCases.CopieDiConflitto.Count > 0)
            throw new InvalidDataException("La fotografia dei Test non è completa o contiene file/duplicati irrisolti.");
        return _state.TestCases.GetAll();
    }

    private (IReadOnlyList<TestCase> TestCases, IReadOnlyList<RisoluzioneConflittoAudit> Risoluzioni)
        RileggiFontiAutorevoliPrimaDelCommit() =>
        (RileggiTestAutorevoliPerPropagazione(), _state.Master.RisoluzioniConflittoAudit.Load());

    /// <summary>Inserimento/correzione manuale con dimensione veicolo (Piano 6.3.1 §1 UI, punto 3): una
    /// singola coppia componente×veicolo, oppure un sottoinsieme/tutti i veicoli di un componente, sempre con nota
    /// obbligatoria e BaselineSw opzionale — vedi ValutazioneRequisitoEcuVeicoloService.ImpostaManuale.</summary>
    public void ImpostaManualeVeicolo(string componente, string requisitoId, IReadOnlyList<string> veicoli, PunteggioConformita punteggio, string nota, string? baselineSw,
        AnteprimaApplicazioneManualeAudit? anteprima = null)
    {
        if (!_state.EMaster) return;
        var veicoliUnici = veicoli.Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (anteprima == null)
        {
            var testsAllaConferma = _state.TestCases.GetAll();
            var risoluzioniAllaConferma = _state.Master.RisoluzioniConflittoAudit.Load();
            var celle = veicoliUnici.Select(v =>
                new CellaApplicazioneManualeAudit(componente, requisitoId, v, Sovrascrive: true, HaValutazioneVeicoloSalvata: true)).ToList();
            var attese = ValutazioneRequisitoEcuVeicoloService.CatturaApplicazioneManualeAudit(
                _state.Master.ValutazioniRequisitoEcuVeicolo, celle, testsAllaConferma, risoluzioniAllaConferma);
            anteprima = new AnteprimaApplicazioneManualeAudit(attese, 0);
        }
        if (!AnteprimaCorrispondeAlleCelle(anteprima, componente, requisitoId, veicoliUnici))
        {
            MessaggioErrore = LocalizationService.Instance.Scegli(
                "L'anteprima non corrisponde alle celle manuali da salvare. Ricarica e ripeti l'operazione.",
                "The preview does not match the manual cells to save. Reload and try again.",
                "L'aperçu ne correspond pas aux cellules manuelles à enregistrer. Rechargez et réessayez.");
            return;
        }

        // Questo helper mantiene la firma storica che accetta uno o più veicoli espliciti. I comandi UI
        // riga/colonna passano invece dal metodo pubblico che limita il batch al veicolo selezionato.
        _ = ApplicaManualeMassivaAuditCore(anteprima, punteggio, nota, baselineSw,
            confermaSovrascritture: true, richiediVeicoloSelezionato: false);
    }

    /// <summary>Inserisce una nuova valutazione manuale Audit solo sul sottoveicolo attualmente selezionato.
    /// Le valutazioni legacy senza dimensione Veicolo restano leggibili e rimovibili, ma questo flusso non ne
    /// crea di nuove e non propaga implicitamente ad altri veicoli.</summary>
    public void SalvaValutazioneManualeVeicoloSelezionato(string componente, string requisitoId, PunteggioConformita valore, string motivazione, string? baselineSw,
        AnteprimaApplicazioneManualeAudit? anteprima = null)
    {
        if (!_state.EMaster || string.IsNullOrWhiteSpace(SottoveicoloSelezionato)) return;
        ImpostaManualeVeicolo(componente, requisitoId, new[] { SottoveicoloSelezionato }, valore, motivazione, baselineSw, anteprima);
    }

    /// <summary>Storico recuperabile della cella corrente: REV Test con snapshot componente/veicolo, eventi locali e
    /// legacy. Le REV senza Details non vengono associate usando i campi correnti del Test.</summary>
    public IReadOnlyList<SnapshotValutazioneAudit> LeggiStoricoValutazioneCella(string componente, string requisitoId, string veicolo)
    {
        try
        {
            using var letturaMaster = MasterLetturaScope.Apri();
            var valutazioni = _state.Master.ValutazioniRequisitoEcuVeicolo.Load();
            var legacy = _state.Master.RisoluzioniConflittoAudit.Load();
            var testCases = _state.TestCases.GetAll(includeEliminati: true);
            MessaggioErrore = null;
            return ValutazioneRequisitoEcuVeicoloService.CostruisciStoricoCella(
                requisitoId, componente, veicolo, testCases, valutazioni, legacy);
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
            return Array.Empty<SnapshotValutazioneAudit>();
        }
    }

    /// <summary>Ripristina uno snapshot come nuova valutazione Manuale solo sul veicolo selezionato. Il batch
    /// a singola cella riusa l'ottimistic concurrency del flusso Audit e richiede conferma quando la cella è
    /// attualmente valorizzata.</summary>
    public bool RipristinaValutazioneStorica(
        string componente,
        string requisitoId,
        string veicolo,
        Guid eventoId,
        string nota,
        bool sovrascriveValoreVisibile,
        bool confermaSovrascrittura,
        AnteprimaApplicazioneManualeAudit? anteprima = null)
    {
        if (!_state.EMaster || string.IsNullOrWhiteSpace(SottoveicoloSelezionato) ||
            !veicolo.Equals(SottoveicoloSelezionato, StringComparison.OrdinalIgnoreCase) ||
            eventoId == Guid.Empty || string.IsNullOrWhiteSpace(nota)) return false;

        var evento = LeggiStoricoValutazioneCella(componente, requisitoId, veicolo)
            .FirstOrDefault(e => e.EventId == eventoId);
        if (evento == null)
        {
            MessaggioErrore = LocalizationService.Instance.Scegli(
                "Il valore storico non è più disponibile. Ricarica la cella e riprova.",
                "This historical value is no longer available. Reload the cell and try again.",
                "Cette valeur historique n'est plus disponible. Rechargez la cellule et réessayez.");
            RicaricaMatrice();
            return false;
        }
        if (sovrascriveValoreVisibile && !confermaSovrascrittura)
        {
            MessaggioErrore = LocalizationService.Instance.Scegli(
                "Conferma la sovrascrittura del valore corrente.",
                "Confirm replacing the current value.",
                "Confirmez le remplacement de la valeur actuelle.");
            return false;
        }

        try
        {
            var repo = _state.Master.ValutazioniRequisitoEcuVeicolo;
            anteprima ??= PreparaApplicazioneManualeAudit(componente, requisitoId);
            if (!AnteprimaCorrispondeAlleCelle(anteprima, componente, requisitoId, new[] { veicolo }))
                throw new InvalidOperationException("L'anteprima non corrisponde alla cella da ripristinare.");
            var testsAllaConferma = _state.TestCases.GetAll();
            var risoluzioniAllaConferma = _state.Master.RisoluzioniConflittoAudit.Load();
            var dataConferma = DateTime.Now;
            var ok = ValutazioneRequisitoEcuVeicoloService.ImpostaManualeMassivaAudit(
                repo, anteprima.Celle, evento.Punteggio, nota.Trim(), _state.Config.OperatoreAttivo,
                evento.BaselineSw, confermaSovrascrittura,
                testsAllaConferma, risoluzioniAllaConferma, out _, out var errore,
                RileggiFontiAutorevoliPrimaDelCommit, dataConferma);
            MessaggioErrore = errore == null ? null : string.Format(
                LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), errore);
            if (!ok)
            {
                if (errore != null) RicaricaMatrice();
                return false;
            }

            CalcolaKpiComplessivi();
            RicaricaMatrice();
            Messaggio = LocalizationService.Instance.Scegli(
                "Valore storico ripristinato come nuova valutazione manuale per il veicolo selezionato.",
                "Historical value restored as a new manual assessment for the selected vehicle.",
                "Valeur historique rétablie comme nouvelle évaluation manuelle pour le véhicule sélectionné.");
            return true;
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
            RicaricaMatrice();
            return false;
        }
    }

    /// <summary>Prepara il perimetro effettivamente visibile (filtro Standard/Extra incluso) per un'applicazione
    /// manuale a riga (requisitoId) o colonna (ecuNome). Il veicolo è sempre quello selezionato. Celle dirette,
    /// manuali locali, propagate, legacy e conflitti richiedono conferma di sovrascrittura; gli stati senza
    /// punteggio sono compilazioni. Le celle non applicabili restano fuori da righe/colonne della matrice.</summary>
    public AnteprimaApplicazioneManualeAudit PreparaApplicazioneManualeAudit(string? ecuNome, string? requisitoId)
    {
        if (!_state.EMaster || string.IsNullOrWhiteSpace(SottoveicoloSelezionato))
            return new AnteprimaApplicazioneManualeAudit(Array.Empty<CellaApplicazioneManualeAuditAttesa>(), 0);

        var celle = new List<CellaApplicazioneManualeAudit>();
        var nonModificabili = 0;
        IEnumerable<RigaMatriceAudit> righeVisibili =
            (MostraSezioneStandard ? Righe : Enumerable.Empty<RigaMatriceAudit>())
            .Concat(MostraSezioneD4d ? RigheD4d : Enumerable.Empty<RigaMatriceAudit>());
        if (!string.IsNullOrWhiteSpace(requisitoId))
            righeVisibili = righeVisibili.Where(r => r.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase));

        foreach (var riga in righeVisibili)
        foreach (var componente in EcuColonne)
        {
            if (!string.IsNullOrWhiteSpace(ecuNome) && !componente.Equals(ecuNome, StringComparison.OrdinalIgnoreCase)) continue;
            if (!riga.Celle.TryGetValue(componente, out var cella)) continue; // componente/requisito non applicabili

            var sovrascrive = cella.Stato is StatoCellaAudit.Valutato or StatoCellaAudit.Conflitto;
            var haValutazioneLocale = cella.Stato == StatoCellaAudit.Valutato &&
                !cella.InserimentoManuale &&
                cella.Origine is OrigineCellaRequisitoEcuVeicolo.Manuale or OrigineCellaRequisitoEcuVeicolo.Propagata;
            var vuota = cella.Stato is StatoCellaAudit.DaTestare or StatoCellaAudit.TestCreatoNonEseguito or StatoCellaAudit.PropagabileNonConfermato;
            if (vuota || sovrascrive)
                celle.Add(new CellaApplicazioneManualeAudit(componente, riga.RequisitoId, SottoveicoloSelezionato, sovrascrive, haValutazioneLocale));
            else
                nonModificabili++;
        }

        var attese = ValutazioneRequisitoEcuVeicoloService.CatturaApplicazioneManualeAudit(
            _state.Master.ValutazioniRequisitoEcuVeicolo, celle,
            _state.TestCases.GetAll(), _state.Master.RisoluzioniConflittoAudit.Load());
        return new AnteprimaApplicazioneManualeAudit(attese, nonModificabili);
    }

    private static bool AnteprimaCorrispondeAlleCelle(
        AnteprimaApplicazioneManualeAudit anteprima,
        string componente,
        string requisitoId,
        IEnumerable<string> veicoli)
    {
        var attese = veicoli.Select(v => $"{componente}\u001f{requisitoId}\u001f{v}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var viste = anteprima.Celle.Select(c => $"{c.Componente}\u001f{c.RequisitoId}\u001f{c.Veicolo}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return attese.Count > 0 && attese.SetEquals(viste);
    }

    /// <summary>Conferma il batch manuale preparato dalla view. Il servizio valida l'intera fotografia prima di
    /// una sola Save; ogni fallimento ricarica la matrice così la vista non resta su uno stato obsoleto.</summary>
    public bool ApplicaManualeMassivaAudit(
        AnteprimaApplicazioneManualeAudit anteprima,
        PunteggioConformita punteggio,
        string nota,
        string? baselineSw,
        bool confermaSovrascritture)
        => ApplicaManualeMassivaAuditCore(anteprima, punteggio, nota, baselineSw,
            confermaSovrascritture, richiediVeicoloSelezionato: true);

    private bool ApplicaManualeMassivaAuditCore(
        AnteprimaApplicazioneManualeAudit anteprima,
        PunteggioConformita punteggio,
        string nota,
        string? baselineSw,
        bool confermaSovrascritture,
        bool richiediVeicoloSelezionato)
    {
        if (!_state.EMaster ||
            (richiediVeicoloSelezionato && (string.IsNullOrWhiteSpace(SottoveicoloSelezionato) ||
                anteprima.Celle.Any(c => !c.Veicolo.Equals(SottoveicoloSelezionato, StringComparison.OrdinalIgnoreCase)))) ||
            anteprima.Celle.Count == 0)
            return false;

        var testsAllaConferma = _state.TestCases.GetAll();
        var risoluzioniAllaConferma = _state.Master.RisoluzioniConflittoAudit.Load();
        var dataConferma = DateTime.Now;
        var ok = ValutazioneRequisitoEcuVeicoloService.ImpostaManualeMassivaAudit(
            _state.Master.ValutazioniRequisitoEcuVeicolo, anteprima.Celle, punteggio, nota,
            _state.Config.OperatoreAttivo, baselineSw, confermaSovrascritture,
            testsAllaConferma, risoluzioniAllaConferma, out var applicate, out var errore,
            RileggiFontiAutorevoliPrimaDelCommit, dataConferma);
        MessaggioErrore = errore == null ? null : string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), errore);
        if (!ok)
        {
            if (errore != null) RicaricaMatrice();
            return false;
        }

        CalcolaKpiComplessivi();
        RicaricaMatrice();
        Messaggio = LocalizationService.Instance.Scegli(
            $"Valutazione manuale applicata a {applicate} celle del veicolo selezionato.",
            $"Manual assessment applied to {applicate} cells on the selected vehicle.",
            $"Évaluation manuelle appliquée à {applicate} cellules du véhicule sélectionné.");
        return true;
    }

    /// <summary>Rimuove una valutazione salvata (Manuale o Propagata) per UNA cella veicolo-specifica (Piano
    /// 6.3.1 §1 UI, punto 4: "permetti di rimuovere/modificare una valutazione salvata") — la cella torna al
    /// calcolo automatico (Diretto se esiste un Test sul veicolo, "!" se ancora propagabile, altrimenti
    /// vuota).</summary>
    public void RimuoviValutazioneVeicolo(string componente, string requisitoId, string veicolo)
    {
        if (!_state.EMaster) return;

        var ok = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazione(
            _state.Master.ValutazioniRequisitoEcuVeicolo, requisitoId, componente, veicolo, out var errore);
        MessaggioErrore = errore == null ? null : string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), errore);
        if (!ok)
        {
            if (errore != null) RicaricaMatrice(); // conflitto multi-PC: mai sovrascrittura silenziosa, ricarica l'ultimo stato su disco
            return;
        }

        CalcolaKpiComplessivi();
        RicaricaMatrice();
    }

    /// <summary>Crea l'anteprima di rimozione per una riga Audit sul solo veicolo selezionato. Le valutazioni
    /// legacy senza veicolo e gli esiti dei Test non sono inclusi tra i target.</summary>
    public AnteprimaRimozioneValutazioniRigaAudit AnteprimaRimozioneValutazioniRiga(string requisitoId)
    {
        MessaggioErrore = null;
        var veicolo = SottoveicoloSelezionato;
        if (!_state.CartellaValida || !_state.EMaster || string.IsNullOrWhiteSpace(veicolo) || string.IsNullOrWhiteSpace(requisitoId))
            return new AnteprimaRimozioneValutazioniRigaAudit(requisitoId, veicolo ?? "", Array.Empty<string>(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Array.Empty<VoceRimozioneValutazioneRigaAudit>());

        try
        {
            var tutteLeEcu = _state.Master.Componente.Load();
            var testCases = RileggiTestAutorevoliPerPropagazione();
            var risoluzioni = _state.Master.RisoluzioniConflittoAudit.Load();
            var valutazioni = _state.Master.ValutazioniRequisitoEcuVeicolo.Load();
            return ValutazioneRequisitoEcuVeicoloService.CatturaRimozioneValutazioniRigaAudit(
                requisitoId, veicolo, tutteLeEcu, testCases, risoluzioni, valutazioni);
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
            return new AnteprimaRimozioneValutazioniRigaAudit(requisitoId, veicolo, Array.Empty<string>(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Array.Empty<VoceRimozioneValutazioneRigaAudit>());
        }
    }

    /// <summary>Conferma la rimozione a livello requisito con un solo commit atomico. Il servizio archivia
    /// soltanto le valutazioni Manuali/Propagate del veicolo e conserva ogni valore nella cronologia; il
    /// refresh di Test, risoluzioni e associazioni componente immediatamente prima del commit invalida le anteprime
    /// superate. I Test e le valutazioni legacy globali non vengono scritti.</summary>
    public bool RimuoviValutazioniRiga(AnteprimaRimozioneValutazioniRigaAudit anteprima)
    {
        if (!_state.EMaster || !anteprima.HaValutazioni) return false;
        if (!string.Equals(SottoveicoloSelezionato, anteprima.Veicolo, StringComparison.OrdinalIgnoreCase))
        {
            MessaggioErrore = LocalizationService.Instance.Scegli(
                "Il veicolo selezionato è cambiato. Ricarica la matrice e ripeti la rimozione.",
                "The selected vehicle changed. Reload the matrix and retry the removal.",
                "Le véhicule sélectionné a changé. Rechargez la matrice et réessayez la suppression.");
            RicaricaMatrice();
            return false;
        }

        bool RequisitoAttivo() => _state.Master.Requisiti.Load().Any(r =>
            !r.Archiviata && r.Id.Equals(anteprima.RequisitoId, StringComparison.OrdinalIgnoreCase));

        if (!RequisitoAttivo())
        {
            MessaggioErrore = LocalizationService.Instance.Scegli(
                "Il requisito non è più attivo. Ricarica la matrice e ripeti la rimozione.",
                "The requirement is no longer active. Reload the matrix and retry the removal.",
                "L'exigence n'est plus active. Rechargez la matrice et réessayez la suppression.");
            RicaricaMatrice();
            return false;
        }

        try
        {
            var tutteLeEcu = _state.Master.Componente.Load();
            var testCases = RileggiTestAutorevoliPerPropagazione();
            var risoluzioni = _state.Master.RisoluzioniConflittoAudit.Load();
            var salvata = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazioniRigaAudit(
                _state.Master.ValutazioniRequisitoEcuVeicolo,
                anteprima,
                tutteLeEcu,
                testCases,
                risoluzioni,
                _state.Config.OperatoreAttivo,
                out var rimosse,
                out var errore,
                () =>
                {
                    if (!RequisitoAttivo())
                        throw new InvalidOperationException("Il requisito è stato archiviato prima del commit.");
                    return new FontiRimozioneValutazioniRigaAudit(
                        _state.Master.Componente.Load(),
                        RileggiTestAutorevoliPerPropagazione(),
                        _state.Master.RisoluzioniConflittoAudit.Load());
                });

            MessaggioErrore = errore == null ? null : string.Format(
                LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), errore);
            if (!salvata)
            {
                if (errore != null) RicaricaMatrice();
                return false;
            }

            Messaggio = LocalizationService.Instance.Scegli(
                $"Rimosse {rimosse} valutazioni manuali o propagate dal requisito {anteprima.RequisitoId} su {anteprima.Veicolo}.",
                $"Removed {rimosse} manual or propagated assessments for requirement {anteprima.RequisitoId} on {anteprima.Veicolo}.",
                $"{rimosse} évaluations manuelles ou propagées supprimées pour l'exigence {anteprima.RequisitoId} sur {anteprima.Veicolo}.");
            CalcolaKpiComplessivi();
            RicaricaMatrice();
            return true;
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
            RicaricaMatrice();
            return false;
        }
    }

    /// <summary>Crea l'anteprima di rimozione per una colonna componente del solo veicolo selezionato. Il perimetro
    /// comprende tutti i requisiti attivi; le fonti Test/REV e legacy sono soltanto lette e rivalidate.</summary>
    public AnteprimaRimozioneValutazioniColonnaAudit AnteprimaRimozioneValutazioniColonna(string componente)
    {
        MessaggioErrore = null;
        var veicolo = SottoveicoloSelezionato;
        if (!_state.CartellaValida || !_state.EMaster || string.IsNullOrWhiteSpace(veicolo) || string.IsNullOrWhiteSpace(componente))
            return AnteprimaRimozioneColonnaVuota(componente, veicolo);

        try
        {
            return ValutazioneRequisitoEcuVeicoloService.CatturaRimozioneValutazioniColonnaAudit(
                componente,
                veicolo,
                _state.Master.Componente.Load(),
                _state.Master.Requisiti.Load(),
                RileggiTestAutorevoliPerPropagazione(),
                _state.Master.RisoluzioniConflittoAudit.Load(),
                _state.Master.ValutazioniRequisitoEcuVeicolo.Load());
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
            return AnteprimaRimozioneColonnaVuota(componente, veicolo);
        }
    }

    private static AnteprimaRimozioneValutazioniColonnaAudit AnteprimaRimozioneColonnaVuota(string componente, string? veicolo) =>
        new(componente, veicolo ?? "", false,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<VoceRimozioneValutazioneColonnaAudit>());

    /// <summary>Conferma con un solo commit atomico la rimozione delle valutazioni Manuali/Propagate di una
    /// componente per tutti i requisiti attivi del veicolo selezionato, preservando ogni snapshot nello storico.</summary>
    public bool RimuoviValutazioniColonna(AnteprimaRimozioneValutazioniColonnaAudit anteprima)
    {
        if (!_state.EMaster || !anteprima.HaValutazioni) return false;
        if (!string.Equals(SottoveicoloSelezionato, anteprima.Veicolo, StringComparison.OrdinalIgnoreCase))
        {
            MessaggioErrore = LocalizationService.Instance.Scegli(
                "Il veicolo selezionato è cambiato. Ricarica la matrice e ripeti la rimozione.",
                "The selected vehicle changed. Reload the matrix and retry the removal.",
                "Le véhicule sélectionné a changé. Rechargez la matrice et réessayez la suppression.");
            RicaricaMatrice();
            return false;
        }

        try
        {
            var salvata = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazioniColonnaAudit(
                _state.Master.ValutazioniRequisitoEcuVeicolo,
                anteprima,
                _state.Master.Componente.Load(),
                _state.Master.Requisiti.Load(),
                RileggiTestAutorevoliPerPropagazione(),
                _state.Master.RisoluzioniConflittoAudit.Load(),
                _state.Config.OperatoreAttivo,
                out var rimosse,
                out var errore,
                () => new FontiRimozioneValutazioniColonnaAudit(
                    _state.Master.Componente.Load(),
                    _state.Master.Requisiti.Load(),
                    RileggiTestAutorevoliPerPropagazione(),
                    _state.Master.RisoluzioniConflittoAudit.Load()));

            MessaggioErrore = errore == null ? null : string.Format(
                LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), errore);
            if (!salvata)
            {
                if (errore != null) RicaricaMatrice();
                return false;
            }

            Messaggio = LocalizationService.Instance.Scegli(
                $"Rimosse {rimosse} valutazioni manuali o propagate del componente {anteprima.Componente} su {anteprima.Veicolo}.",
                $"Removed {rimosse} manual or propagated assessments for componente {anteprima.Componente} on {anteprima.Veicolo}.",
                $"{rimosse} évaluations manuelles ou propagées supprimées pour il componente {anteprima.Componente} sur {anteprima.Veicolo}.");
            CalcolaKpiComplessivi();
            RicaricaMatrice();
            return true;
        }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
            RicaricaMatrice();
            return false;
        }
    }

    /// <summary>Cosa l'Audit puo' esportare e quali filtri si possono modificare dal dialogo (quelli a video).</summary>
    internal ExportDescrittore CreaDescrittoreExport()
    {
        var loc = LocalizationService.Instance;
        var sezioni = new List<ExportSezione>
        {
            new(AuditSezioniExport.Legenda, loc.Scegli("Legenda", "Legend", "Légende")),
            new(AuditSezioniExport.Matrice, loc.Scegli("Matrice del veicolo", "Vehicle matrix", "Matrice du véhicule")),
            new(AuditSezioniExport.Conflitti, loc.Scegli("Conflitti", "Conflicts", "Conflits")),
            new(AuditSezioniExport.DettaglioDtc, loc.Scegli("Dettaglio guasto (se presente)", "Guasto detail (if any)", "Détail guasto (le cas échéant)")),
            new("pivot", "Pivot", PredefinitaAttiva: false),
        };
        var filtri = new List<ExportFiltro>
        {
            new ExportFiltroSingolo("veicolo", loc.Scegli("Veicolo", "Vehicle", "Véhicule"), SottoveicoliDisponibili,
                () => SottoveicoloSelezionato, v => { if (!string.IsNullOrEmpty(v)) SottoveicoloSelezionato = v; }),
            new ExportFiltroMultiplo("tiporequisito", loc.Scegli("Tipo requisito", "Requirement type", "Type d'exigence"), FiltroTipoRequisito),
        };
        return new ExportDescrittore("audit", loc.Scegli("Esporta Audit: scegli cosa includere", "Export Audit: choose what to include", "Exporter l'Audit : choisir le contenu"), sezioni, "", filtri, [new ExportFontePivot("matrice", loc.Scegli("Celle matrice", "Matrix cells", "Cellules matrice"), AuditReportService.CampiPivot())]);
    }

    /// <summary>Stato applicativo per il dialog di export (salvataggio delle configurazioni per operatore).</summary>
    internal AppState StatoPerExport => _state;

    /// <summary>Chiamato dal code-behind dopo il dialogo di salvataggio nativo, stesso schema di
    /// ReportViewModel.EsportaExcelVerso (Addendum: grill-me 2026-08-18, follow-up). Generazione su thread di
    /// background con Annulla: il file compare solo a scrittura completa (vedi EsportazioneInBackground).</summary>
    public async Task EsportaExcelVerso(string percorso, ExportConfigurazione? scelta = null)
    {
        if (!_state.CartellaValida || EsportazioneInCorso) return;
        var veicolo = SottoveicoloSelezionato;
        if (string.IsNullOrWhiteSpace(veicolo))
        {
            MessaggioErrore = LocalizationService.Instance.Scegli(
                "Seleziona un veicolo prima di esportare l'Audit.",
                "Select a vehicle before exporting the Audit.",
                "Sélectionnez un véhicule avant d'exporter l'Audit.");
            return;
        }
        var tipiVisibili = FiltroTipoRequisito.HaSelezione ? FiltroTipoRequisito.Selezionati.ToArray() : null;
        await EseguiEsportazione(percorso, (temporaneo, ct) => File.WriteAllBytes(temporaneo,
                AuditReportService.GeneraExcel(_state, veicolo, tipiVisibili, ct, scelta == null ? null : new AuditExportOpzioni(
                    scelta.HaSezione(AuditSezioniExport.Legenda), scelta.HaSezione(AuditSezioniExport.Matrice),
                    scelta.HaSezione(AuditSezioniExport.Conflitti), scelta.HaSezione(AuditSezioniExport.DettaglioDtc), scelta))),
            registraCronologia: true);
    }

    private readonly EsportazioneInBackground _esportazione = new();

    /// <summary>Vero mentre un export dell'Audit e in corso: la view disabilita i pulsanti di esportazione e mostra Annulla.</summary>
    [ObservableProperty] private bool esportazioneInCorso;

    [RelayCommand]
    private void AnnullaEsportazione() => _esportazione.Annulla();

    private async Task EseguiEsportazione(string percorso, Action<string, CancellationToken> scrivi, bool registraCronologia)
    {
        EsportazioneInCorso = true;
        Messaggio = LocalizationService.Instance.Traduci("GenerazioneReportInCorso");
        EsitoEsportazione esito;
        try
        {
            esito = await _esportazione.EseguiAsync(percorso, scrivi);
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
                    if (registraCronologia) RegistraSalvataggio(percorso);
                    Messaggio = string.Format(LocalizationService.Instance.Traduci("AuditExcelSalvatoFmt"), Path.GetFileName(percorso));
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
        CaricaCronologia();
    }

    /// <summary>Stessa cronologia locale di Report (_state.Config.CronologiaReport): stesso concetto di "file
    /// generati da questo operatore su questo PC", non serve un elenco separato per l'Audit.</summary>
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
}
