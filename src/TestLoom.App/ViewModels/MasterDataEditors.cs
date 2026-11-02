using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

public class RequisitiViewModel : MasterDataEditorViewModelBase<Requisito>
{
    protected override string CampoChiaveDaRinominare => "Id";

    /// <summary>Scheda di appartenenza (6.5.0): Standard (default, comportamento storico) oppure Extra. Le due schede
    /// mostrano righe distinte (stesso catalogo/indice, filtro per Tipo lato SQL in consultazione paginata e in
    /// memoria in modifica), creano righe del proprio Tipo e non mescolano mai le liste.</summary>
    public TipoRequisito Scheda { get; }
    public bool ESchedaD4d => Scheda == TipoRequisito.Extra;
    public string TitoloScheda => ESchedaD4d ? "Requisiti Extra" : LocalizationService.Instance.Traduci("RequisitiTitolo");
    public bool PuoImportareDtcMassivo => ESchedaD4d && InModifica && !ImportDtcInCorso;

    public RequisitiViewModel(AppState state) : this(state, TipoRequisito.Standard) { }

    public RequisitiViewModel(AppState state, TipoRequisito scheda)
        : base(state, state.Master.Requisiti, state.Paths.RequisitiFile, r => r.Id, CreaSorgente(state, scheda))
    {
        Scheda = scheda;
        AggiungiDtcCommand = new RelayCommand(AggiungiDtc);
        RimuoviDtcCommand = new RelayCommand<DtcRequisito?>(RimuoviDtc);
        ToggleNodoDtcCommand = new RelayCommand<DtcAlberoNodo?>(ToggleNodoDtc);
        AggiungiNodoVeicoloCommand = new RelayCommand(AggiungiNodoVeicolo);
        AggiungiNodoEcuCommand = new RelayCommand(AggiungiNodoEcu);
        AggiungiDtcDalRegistroCommand = new AsyncRelayCommand(AggiungiDtcDalRegistro);
        AssociaDtcAlRequisitoCommand = new AsyncRelayCommand(AssociaDtcAlRequisito);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ElementoSelezionato) or nameof(InModifica))
            {
                if (e.PropertyName == nameof(InModifica))
                {
                    // Le copie di lavoro dei guasti valgono per UNA sessione (consultazione o modifica): al cambio si rileggono.
                    if (!InModifica) { UltimoReportDtc = null; _scartateConfermate = false; _importDtcCts?.Cancel(); }
                    lock (_dtcGate) _dtcLavoro.Clear();
                    _nodiVuoti.Clear(); _nodiAlterati.Clear(); _ecuAnagrafica = null; _veicoliAnagrafica = null;
                }
                if (e.PropertyName == nameof(ElementoSelezionato)) { _nodiVuoti.Clear(); _nodiAlterati.Clear(); NotificaListeSottoveicoli(); }
                RicaricaDtcVisibili(); OnPropertyChanged(nameof(PuoImportareDtcMassivo));
                OnPropertyChanged(nameof(PuoAggiungereDtcDalRegistro)); OnPropertyChanged(nameof(PuoAssociareDtcAlRequisito));
            }
        };
    }

    private static MasterDataPagedReadSource<Requisito> CreaSorgente(AppState state, TipoRequisito scheda)
    {
        var s = MasterDataPagedReadSources.Requisiti(state.Paths);
        s.FiltroTipoRequisito = scheda;
        return s;
    }

    protected override bool IncludiNellaVista(Requisito item) => RequisitoApplicabilita.TipoDi(item) == Scheda;

    protected override void InizializzaNuovo(Requisito nuovo)
    {
        if (Scheda == TipoRequisito.Extra) RequisitoApplicabilita.ImpostaTipo(nuovo, TipoRequisito.Extra);
        MasterEntityIdentityService.GetOrCreate(nuovo); // Uid tecnico subito: chiave dell'archivio guasto anche se l'Id cambia prima del Salva
    }

    // === guasto del requisito (6.5.0, punto 7) =========================================================
    // i guasti NON vivono nel Requisito: stanno nell'archivio separato (RequisitoDtcArchivio, un documento per requisito).
    // Qui si tiene una copia di LAVORO per i soli requisiti toccati/selezionati, letta lazy (sincrona se piccola, in
    // background se grande). Salva scrive SOLO gli archivi dei requisiti con guasto modificati (PrimaDiPersistere).

    private sealed class DtcLavoro
    {
        public List<DtcRequisito> Voci { get; init; } = new();
        public Requisito? Requisito { get; init; }
        /// <summary>Voci ORIGINALI complete all'apertura (per Annulla dopo un Salva il cui archivio e gia stato scritto).</summary>
        public List<DtcRequisito> VociBase { get; init; } = new();
        /// <summary>L'archivio esisteva all'apertura (false: legacy embedded o nessun guasto => l'originale e "nessun archivio").</summary>
        public bool ArchivioEsisteva { get; init; }
        /// <summary>Chiavi Codice|componente all'apertura della sessione (base del riepilogo "+N/-M guasto" del Changelog).</summary>
        public HashSet<string> ChiaviBase { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public DtcArchivioRequisito? Documento { get; set; }
        public DtcArchivioStato Stato { get; init; }
        public string? Avviso { get; init; }
        public bool Embedded { get; init; }
        public bool Modificato { get; set; }
        /// <summary>Le voci di lavoro sono GIA nell'archivio (scritto da un Salva il cui commit dei requisiti non e riuscito):
        /// stato persistente tra un tentativo e l'altro. Un retry non riscrive l'archivio ma riapplica comunque
        /// DtcConteggio/Guasto=null alla copia staged; si azzera (insieme a Modificato) SOLO dopo l'esito OK del commit.
        /// Qualsiasi nuova modifica locale lo rimette a false (l'archivio va riscritto).</summary>
        public bool ArchivioScritto { get; set; }
        public void Tocca() { Modificato = true; ArchivioScritto = false; }
    }

    private readonly Dictionary<Guid, DtcLavoro> _dtcLavoro = new();
    private readonly object _dtcGate = new();
    private CancellationTokenSource? _dtcSelezioneCts;
    /// <summary>Soglia sotto la quale il caricamento dell'archivio del requisito selezionato e sincrono (pochi ms).</summary>
    public const int SogliaDtcCaricamentoSincrono = 500;

    private static string ChiaveDtc(DtcRequisito v) => DtcRequisito.Chiave(v);

    private DtcLavoro CaricaLavoro(Requisito r)
    {
        var uid = MasterEntityIdentityService.GetOrCreate(r);
        lock (_dtcGate) if (_dtcLavoro.TryGetValue(uid, out var gia)) return gia;
        var letto = StatoApp.Master.RequisitiDtc.Leggi(r);
        var lav = new DtcLavoro
        {
            Voci = letto.Voci, Requisito = r, ArchivioEsisteva = letto.Documento != null,
            VociBase = letto.Voci.Select(v => v.Clona()).ToList(), Documento = letto.Documento, Stato = letto.Stato, Avviso = letto.Avviso, Embedded = letto.Embedded,
            ChiaviBase = letto.Voci.Select(ChiaveDtc).ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
        lock (_dtcGate) return _dtcLavoro.TryAdd(uid, lav) ? lav : _dtcLavoro[uid];
    }

    private DtcLavoro? LavoroEsistente(Requisito? r)
    {
        if (r == null || !MasterEntityIdentityService.TryGet(r, out var uid)) return null;
        lock (_dtcGate) return _dtcLavoro.TryGetValue(uid, out var l) ? l : null;
    }

    /// <summary>Voci guasto (copia di lavoro) del requisito: legge l'archivio se non ancora caricato. Puo girare in background.</summary>
    public IReadOnlyList<DtcRequisito> DtcDi(Requisito r) => CaricaLavoro(r).Voci;
    private IReadOnlyList<DtcRequisito> VociDi(Requisito r) => DtcDi(r);

    /// <summary>Voci guasto visibili del requisito selezionato. Lista VIRTUALIZZATA nella view (ListBox): regge 100k+ voci perche
    /// solo i contenitori a schermo vengono realizzati; una sola notifica Reset per ricaricamento. Per archivi grandi
    /// il caricamento e in background con segnaposto (<see cref="DtcCaricamento"/>).</summary>
    public BulkObservableCollection<DtcRequisito> DtcVisibili { get; } = new();
    /// <summary>i guasti si gestiscono solo sui requisiti Extra (RequisitoApplicabilita.ED4D, Tipo gia' esistente).</summary>
    public bool MostraPannelloDtc => ElementoSelezionato != null && RequisitoApplicabilita.ED4D(ElementoSelezionato);
    public string TitoloDtc => ElementoSelezionato == null ? "" : $"Guasto · {ElementoSelezionato.Id} ({DtcVisibili.Count})";
    public bool DtcModificabile => InModifica && MostraPannelloDtc && !ImportDtcInCorso && !DtcInCaricamento;

    private bool _dtcInCaricamento;
    public bool DtcInCaricamento { get => _dtcInCaricamento; private set { if (SetProperty(ref _dtcInCaricamento, value)) OnPropertyChanged(nameof(DtcModificabile)); } }
    /// <summary>Completa quando i guasti del requisito selezionato sono visibili (gia completato se il caricamento e sincrono).</summary>
    public Task DtcCaricamento { get; private set; } = Task.CompletedTask;

    private string _nuovoDtcCodice = "";
    /// <summary>Testo dei codici guasto da aggiungere AL NODO scelto (Veicolo + componente): uno o piu codici separati da a capo, virgola, punto e virgola o spazio.</summary>
    public string NuovoDtcCodici { get => _nuovoDtcCodice; set => SetProperty(ref _nuovoDtcCodice, value ?? ""); }
    /// <summary>Alias di <see cref="NuovoDtcCodici"/> (un solo codice = caso particolare dell'aggiunta in blocco).</summary>
    public string NuovoDtcCodice { get => NuovoDtcCodici; set => NuovoDtcCodici = value; }
    private string _nuovoDtcEcu = "";
    /// <summary>componente della nuova voce: scelta dal menu a tendina dell'anagrafica Componente (<see cref="EcuDisponibili"/>), non digitata.</summary>
    public string NuovoDtcEcu { get => _nuovoDtcEcu; set => SetProperty(ref _nuovoDtcEcu, value ?? ""); }
    private string _nuovoDtcVeicolo = "";
    /// <summary>Sottoveicolo della nuova voce, "" = senza sottoveicolo. Deve essere tra i <see cref="VeicoliDichiarati"/> del requisito.</summary>
    public string NuovoDtcVeicolo
    {
        get => _nuovoDtcVeicolo;
        set { if (SetProperty(ref _nuovoDtcVeicolo, (value ?? "").Trim() == DtcRequisito.SenzaVeicolo ? "" : (value ?? "").Trim())) OnPropertyChanged(nameof(NuovoDtcVeicoloScelto)); }
    }
    /// <summary>Valore del menu a tendina Sottoveicolo (mai null: "(senza veicolo)" per la voce senza sottoveicolo).</summary>
    public string NuovoDtcVeicoloScelto
    {
        get => NuovoDtcVeicolo.Length == 0 ? DtcRequisito.SenzaVeicolo : NuovoDtcVeicolo;
        set => NuovoDtcVeicolo = value ?? "";
    }

    public IAsyncRelayCommand AggiungiDtcDalRegistroCommand { get; }
    public IAsyncRelayCommand AssociaDtcAlRequisitoCommand { get; }

    /// <summary>In modifica di un requisito Extra: si possono aggiungere al suo elenco guasto quelli del registro non ancora associati.</summary>
    public bool PuoAggiungereDtcDalRegistro => ESchedaD4d && InModifica && ElementoSelezionato != null && !DtcInCaricamento && !ImportDtcInCorso;

    /// <summary>Su un requisito Extra salvato (non in modifica): si associano ai Test le righe guasto del registro, aggiungendole all'elenco del requisito.</summary>
    public bool PuoAssociareDtcAlRequisito => ESchedaD4d && !InModifica && ElementoSelezionato != null && StatoApp.PuoScrivere;

    /// <summary>Elenca i guasti del registro che nessun requisito ha ancora (raggruppati per veicolo, componente e codice), ne fa scegliere più d'uno e li
    /// aggiunge alla copia di lavoro del requisito con le stesse regole dell'inserimento manuale. L'associazione ai Test si fa dopo il salvataggio.</summary>
    private async Task AggiungiDtcDalRegistro()
    {
        var req = ElementoSelezionato;
        if (req == null || !PuoAggiungereDtcDalRegistro) return;
        var loc = LocalizationService.Instance;
        try
        {
            var flusso = new DtcAssociazioneFlusso(StatoApp);
            var righe = await Task.Run(() => flusso.RigheAssociabili());
            var lav = CaricaLavoro(req);
            var chiavi = lav.Voci.Select(ChiaveDtc).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var gruppi = righe.GroupBy(r => DtcRequisito.Chiave(r.Veicolo, r.Componente, r.Guasto), StringComparer.OrdinalIgnoreCase)
                .Where(g => !chiavi.Contains(g.Key)).Select(g => (Prima: g.First(), Righe: g.Count()))
                .OrderBy(g => g.Prima.Guasto, StringComparer.OrdinalIgnoreCase).ToList();
            if (gruppi.Count == 0)
            {
                Messaggio = loc.Scegli("Nessun guasto del registro da aggiungere: sono già nell'elenco o associati a un requisito.",
                    "No registry fault to add: they are already in the list or assigned to a requirement.",
                    "Aucun guasto du registre à ajouter : ils sont déjà dans la liste ou associés à une exigence.");
                return;
            }
            var voci = gruppi.Select(g => new VoceSelezionabile
            {
                Valore = g.Prima,
                TestoPrincipale = $"{g.Prima.Guasto} · {g.Prima.EcuVisualizzata} · {g.Prima.VeicoloVisualizzato}",
                TestoSecondario = string.Format(loc.Scegli("{0} righe nel registro · es. test {1}", "{0} registry rows · e.g. test {1}", "{0} lignes du registre · p. ex. test {1}"), g.Righe, g.Prima.TestId),
                TestoRicerca = $"{g.Prima.Guasto} {g.Prima.Componente} {g.Prima.Veicolo} {g.Prima.TestId}"
            }).ToList();
            var scelte = await SelezionaDialogService.ScegliMultipli(
                string.Format(loc.Scegli("Aggiungi guasto dal registro a {0}", "Add registry Guasti to {0}", "Ajouter des guasto du registre à {0}"), req.Id), voci);
            var daAggiungere = scelte.Select(v => v.Valore).OfType<RigaDtcRegistro>().ToList();
            if (daAggiungere.Count == 0) return;

            var rifiutate = new List<string>();
            var aggiunte = 0;
            foreach (var r in daAggiungere)
            {
                var errore = DtcAlberoRegole.ValidaNuovaVoce(r.Veicolo, r.Componente, r.Guasto, VeicoliDichiarati, EcuDisponibili, chiavi, VeicoliAnagrafica);
                if (errore != null) { rifiutate.Add($"{r.Guasto} / {r.Componente}: {loc.Traduci(errore)}"); continue; }
                var canonico = DtcAlberoRegole.Canonico(r.Veicolo, DtcAlberoRegole.Ammessi(VeicoliDichiarati, VeicoliAnagrafica));
                var voce = new DtcRequisito { Codice = r.Guasto.Trim(), Componente = r.Componente.Trim(), Veicolo = string.IsNullOrEmpty(canonico) ? null : canonico };
                lav.Voci.Add(voce);
                chiavi.Add(DtcRequisito.Chiave(voce));
                aggiunte++;
            }
            if (aggiunte > 0) { lav.Tocca(); RicaricaDtcVisibili(); }
            Messaggio = string.Format(loc.Scegli(
                "Aggiunti {0} guasto dal registro. Salva il requisito, poi associa i Test dal Registro guasto (Da requisito Extra) o da qui.",
                "Added {0} Faults from the registry. Save the requirement, then assign the Tests from the fault Registry (From Extra requirement) or from here.",
                "{0} guasto ajoutés depuis le registre. Enregistrez l’exigence, puis associez les Tests depuis le Registre guasto (Depuis une exigence Extra) ou d’ici."), aggiunte);
            MessaggioErrore = rifiutate.Count == 0 ? null : string.Join(" · ", rifiutate.Take(5)) + (rifiutate.Count > 5 ? $" … (+{rifiutate.Count - 5})" : "");
        }
        catch (Exception ex)
        {
            MessaggioErrore = ex.Message;
            CrashLogService.Registra("Aggiunta guasto dal registro al requisito", ex);
        }
    }

    /// <summary>A requisito salvato: sceglie le righe del registro da associare a questo requisito (aggiungendo i guasti mancanti al suo elenco).</summary>
    private async Task AssociaDtcAlRequisito()
    {
        var req = ElementoSelezionato;
        if (req == null || !PuoAssociareDtcAlRequisito) return;
        try
        {
            var messaggio = await new DtcAssociazioneFlusso(StatoApp).DaRequisitoAsync(req);
            // L'archivio guasto del requisito può essere cambiato: la copia di consultazione si rilegge.
            lock (_dtcGate) _dtcLavoro.Clear();
            RicaricaDtcVisibili();
            if (messaggio != null) Messaggio = messaggio;
        }
        catch (Exception ex)
        {
            MessaggioErrore = ex.Message;
            CrashLogService.Registra("Associazione guasto al requisito dal master", ex);
        }
    }

    public IRelayCommand AggiungiDtcCommand { get; }
    public IRelayCommand<DtcRequisito?> RimuoviDtcCommand { get; }
    public IRelayCommand<DtcAlberoNodo?> ToggleNodoDtcCommand { get; }
    public IRelayCommand AggiungiNodoVeicoloCommand { get; }
    public IRelayCommand AggiungiNodoEcuCommand { get; }

    // === Albero Sottoveicolo -> componente -> guasto (6.5.1) ==================================================
    // Solo una VISTA raggruppata della lista piatta delle voci (nessun cambio di storage). Righe appiattite e virtualizzate
    // (AlberoDtc): regge i 100k guasto. Nodi vuoti (sottoveicolo/componente senza ancora guasto) esistono solo nella sessione di modifica.

    /// <summary>Soglia sotto la quale l'albero si apre espanso; oltre, gruppi chiusi (si espandono a richiesta).</summary>
    public const int SogliaAlberoDtcEspanso = 500;
    private readonly HashSet<(string Sv, string Componente)> _nodiVuoti = new();
    private readonly HashSet<string> _nodiAlterati = new(StringComparer.OrdinalIgnoreCase); // chiavi il cui stato differisce dal default
    private bool _alberoDefaultEspanso = true;
    private List<string>? _ecuAnagrafica;
    private List<string>? _veicoliAnagrafica;

    public BulkObservableCollection<DtcAlberoNodo> AlberoDtc { get; } = new();

    /// <summary>Sottoveicoli DICHIARATI dal requisito Extra selezionato (RequisitoApplicabilita): gli unici ammessi per le voci guasto.</summary>
    public IReadOnlyList<string> VeicoliDichiarati => ElementoSelezionato == null ? Array.Empty<string>() : RequisitoApplicabilita.SottoveicoliDi(ElementoSelezionato);
    /// <summary>Opzioni del menu a tendina Sottoveicolo: "(senza veicolo)" + i dichiarati.</summary>
    public IReadOnlyList<string> VeicoliScegliibili => new[] { DtcRequisito.SenzaVeicolo }.Concat(VeicoliAmmessi).ToList();
    /// <summary>Veicoli dell'anagrafica (non archiviati): ammessi per le voci guasto quando il requisito Extra non dichiara veicoli (lista vuota = tutti,
    /// come <see cref="RequisitoApplicabilita.SiApplicaAlSottoveicolo"/>). Riletta a ogni ingresso in modifica.</summary>
    public IReadOnlyList<string> VeicoliAnagrafica => _veicoliAnagrafica ??= StatoApp.Master.Veicoli.Load().Where(v => !v.Archiviata && !string.IsNullOrWhiteSpace(v.Nome))
        .Select(v => v.Nome.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, NaturalStringComparer.Instance).ToList();
    /// <summary>Veicoli scegliibili per le voci guasto: i dichiarati dal requisito oppure, se non ne dichiara, tutti quelli dell'anagrafica.</summary>
    public IReadOnlyList<string> VeicoliAmmessi => VeicoliDichiarati.Count > 0 ? VeicoliDichiarati : VeicoliAnagrafica;
    /// <summary>componente dell'anagrafica (non archiviate), per il menu a tendina componente. Riletta a ogni ingresso in modifica.</summary>
    public IReadOnlyList<string> EcuDisponibili => _ecuAnagrafica ??= StatoApp.Master.Componente.Load().Where(e => !e.Archiviata && !string.IsNullOrWhiteSpace(e.Nome))
        .Select(e => e.Nome.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, NaturalStringComparer.Instance).ToList();

    /// <summary>Per il selettore Extra: solo componente attive esplicitamente montate sui rispettivi veicoli.</summary>
    internal IReadOnlyDictionary<string, List<string>> EcuMontatePerVeicolo()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var componente in StatoApp.Master.Componente.Load().Where(e => !e.Archiviata && !string.IsNullOrWhiteSpace(e.Nome)))
        {
            foreach (var vehicle in componente.VeicoliAssociati.Where(v => !string.IsNullOrWhiteSpace(v)))
            {
                var name = vehicle.Trim();
                if (!result.TryGetValue(name, out var list)) result[name] = list = new List<string>();
                list.Add(componente.Nome.Trim());
            }
        }
        return result;
    }

    private DtcAlberoNodo? _nodoDtcSelezionato;
    /// <summary>Nodo selezionato nell'albero: sottoveicolo/componente del nodo precompilano i menu a tendina della nuova voce.</summary>
    public DtcAlberoNodo? NodoDtcSelezionato
    {
        get => _nodoDtcSelezionato;
        set
        {
            if (!SetProperty(ref _nodoDtcSelezionato, value) || value == null || !InModifica) return;
            NuovoDtcVeicolo = value.Veicolo;
            if (value.Componente.Length > 0) NuovoDtcEcu = value.Componente;
        }
    }

    private void NotificaListeSottoveicoli()
    {
        OnPropertyChanged(nameof(VeicoliDichiarati));
        OnPropertyChanged(nameof(VeicoliAmmessi));
        OnPropertyChanged(nameof(VeicoliScegliibili));
        OnPropertyChanged(nameof(NuovoDtcVeicoloScelto));
    }

    /// <summary>I sottoveicoli dichiarati del requisito sono cambiati (o il tipo): aggiorna i menu e ricalcola i segnali "non allineata" dell'albero.</summary>
    public void NotificaSottoveicoliCambiati()
    {
        NotificaListeSottoveicoli();
        if (MostraPannelloDtc && !DtcInCaricamento) RicostruisciAlbero(LavoroEsistente(ElementoSelezionato)?.Voci ?? (IReadOnlyList<DtcRequisito>)DtcVisibili);
    }

    private bool NodoEspanso(string chiave) => _nodiAlterati.Contains(chiave) ? !_alberoDefaultEspanso : _alberoDefaultEspanso;

    private void RicostruisciAlbero(IReadOnlyList<DtcRequisito> voci)
    {
        _alberoDefaultEspanso = voci.Count <= SogliaAlberoDtcEspanso;
        var dichiarati = VeicoliDichiarati;
        var nodiVuoti = _nodiVuoti.Select(n => (n.Sv, n.Componente)).ToList();
        AlberoDtc.ReplaceWith(DtcAlberoRegole.Costruisci(voci, dichiarati, nodiVuoti, NodoEspanso, VeicoliAnagrafica));
    }

    private void ToggleNodoDtc(DtcAlberoNodo? nodo)
    {
        if (nodo == null || !nodo.EIntestazione) return;
        if (!_nodiAlterati.Add(nodo.ChiaveNodo)) _nodiAlterati.Remove(nodo.ChiaveNodo);
        var req = ElementoSelezionato;
        if (req != null && LavoroEsistente(req) is { } lav) RicostruisciAlbero(lav.Voci);
    }

    private void AssicuraEspanso(string sv, string componente)
    {
        foreach (var k in componente.Length == 0 ? new[] { sv } : new[] { sv, sv + "|" + componente })
            if (!NodoEspanso(k) && !_nodiAlterati.Add(k)) _nodiAlterati.Remove(k);
    }

    private void AggiungiNodoVeicolo()
    {
        var req = ElementoSelezionato;
        if (!InModifica || req == null || DtcInCaricamento || !MostraPannelloDtc) return;
        var sv = NuovoDtcVeicolo.Trim();
        if (sv.Length > 0 && DtcAlberoRegole.Canonico(sv, VeicoliAmmessi) == null)
        { MessaggioErrore = LocalizationService.Instance.Traduci("DtcVeicoloNonDichiarato"); return; }
        MessaggioErrore = null;
        _nodiVuoti.Add((sv, ""));
        AssicuraEspanso(sv, "");
        RicaricaDtcVisibili();
    }

    private void AggiungiNodoEcu()
    {
        var req = ElementoSelezionato;
        if (!InModifica || req == null || DtcInCaricamento || !MostraPannelloDtc) return;
        var loc = LocalizationService.Instance;
        var sv = NuovoDtcVeicolo.Trim(); var componente = NuovoDtcEcu.Trim();
        if (componente.Length == 0) { MessaggioErrore = loc.Traduci("DtcEcuMancante"); return; }
        if (EcuDisponibili.Count > 0 && !EcuDisponibili.Contains(componente, StringComparer.OrdinalIgnoreCase)) { MessaggioErrore = loc.Traduci("DtcEcuNonInAnagrafica"); return; }
        if (sv.Length > 0 && DtcAlberoRegole.Canonico(sv, VeicoliAmmessi) == null) { MessaggioErrore = loc.Traduci("DtcVeicoloNonDichiarato"); return; }
        MessaggioErrore = null;
        _nodiVuoti.Add((sv, componente));
        AssicuraEspanso(sv, componente);
        RicaricaDtcVisibili();
    }

    private void RicaricaDtcVisibili()
    {
        _dtcSelezioneCts?.Cancel();
        var req = ElementoSelezionato;
        if (req == null || !RequisitoApplicabilita.ED4D(req))
        {
            DtcInCaricamento = false; DtcCaricamento = Task.CompletedTask;
            DtcVisibili.ReplaceWith(Array.Empty<DtcRequisito>());
            AlberoDtc.ReplaceWith(Array.Empty<DtcAlberoNodo>());
            AggiornaDopoDtc();
            return;
        }
        StatoApp.Master.RequisitiDtc.Risolvi(new[] { req }); // conteggio null/0 = ignoto: dimensione reale dall'archivio
        if (LavoroEsistente(req) != null || req.DtcTotale <= SogliaDtcCaricamentoSincrono)
        {
            DtcInCaricamento = false; DtcCaricamento = Task.CompletedTask;
            var voci = CaricaLavoro(req).Voci;
            DtcVisibili.ReplaceWith(voci);
            RicostruisciAlbero(voci);
            AggiornaDopoDtc();
            return;
        }
        // Archivio grande: lista vuota + segnaposto subito, lettura in background, poi popolamento.
        var cts = _dtcSelezioneCts = new CancellationTokenSource();
        DtcVisibili.ReplaceWith(Array.Empty<DtcRequisito>());
        AlberoDtc.ReplaceWith(Array.Empty<DtcAlberoNodo>());
        DtcInCaricamento = true;
        AggiornaDopoDtc();
        DtcCaricamento = CaricaDtcSelezionatoAsync(req, cts.Token);
    }

    private async Task CaricaDtcSelezionatoAsync(Requisito req, CancellationToken ct)
    {
        try
        {
            var dichiarati = VeicoliDichiarati;
            var anagrafica = VeicoliAnagrafica;
            var alterati = new HashSet<string>(_nodiAlterati, StringComparer.OrdinalIgnoreCase);
            // Raggruppamento di decine di migliaia di voci fuori dal thread UI; oltre la soglia i gruppi partono chiusi.
            var (lav, righe) = await Task.Run(() =>
            {
                var l = CaricaLavoro(req);
                var def = l.Voci.Count <= SogliaAlberoDtcEspanso;
                return (l, DtcAlberoRegole.Costruisci(l.Voci, dichiarati, null, k => alterati.Contains(k) ? !def : def, anagrafica));
            }, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(ElementoSelezionato, req)) return;
            DtcVisibili.ReplaceWith(lav.Voci);
            _alberoDefaultEspanso = lav.Voci.Count <= SogliaAlberoDtcEspanso;
            AlberoDtc.ReplaceWith(righe);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { CrashLogService.Registra("Caricamento guasto requisito", ex); }
        if (ct.IsCancellationRequested) return;
        DtcInCaricamento = false;
        AggiornaDopoDtc();
    }

    private void AggiornaDopoDtc()
    {
        OnPropertyChanged(nameof(MostraPannelloDtc));
        OnPropertyChanged(nameof(TitoloDtc));
        OnPropertyChanged(nameof(DtcModificabile));
        AggiornaAvvisoVolume();
    }

    private void AggiungiDtc()
    {
        var req = ElementoSelezionato;
        if (!InModifica || req == null || DtcInCaricamento) return;
        var loc = LocalizationService.Instance;
        var lav = CaricaLavoro(req);
        // Aggiunta A NODO (6.5.1): Veicolo e componente si scelgono una volta, poi si incollano tutti i guasti del nodo. Validazione in scrittura: componente
        // dell'anagrafica, veicolo tra i dichiarati dal requisito, CodiceValido per codice, dedup sulla chiave (Veicolo, componente, Codice).
        // Una voce GIA salvata con un veicolo poi rimosso dal requisito non blocca nulla: e segnalata "non allineata" (albero e Audit).
        var chiavi = lav.Voci.Select(ChiaveDtc).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var r = DtcAlberoRegole.ValidaBlocco(NuovoDtcVeicolo, NuovoDtcEcu, NuovoDtcCodici, VeicoliDichiarati, EcuDisponibili, chiavi, VeicoliAnagrafica);
        if (r.ErroreNodo != null) { MessaggioErrore = loc.Traduci(r.ErroreNodo); return; }
        if (r.Letti == 1 && r.Nuove.Count == 0)   // codice singolo: messaggio puntuale (invariato)
        { MessaggioErrore = loc.Traduci(r.NonValidi.Count > 0 ? "DtcCodiceNonValido" : "DtcDuplicato"); return; }
        var problemi = new List<string>();
        if (r.Duplicati.Count > 0) problemi.Add(string.Format(loc.Traduci("DtcBloccoDuplicatiFmt"), r.Duplicati.Count, Anteprima(r.Duplicati)));
        if (r.NonValidi.Count > 0) problemi.Add(string.Format(loc.Traduci("DtcBloccoScartatiFmt"), r.NonValidi.Count, Anteprima(r.NonValidi)));
        MessaggioErrore = problemi.Count == 0 ? null : string.Join(" ", problemi);
        if (r.Nuove.Count == 0) return;           // nulla di valido: il testo resta per la correzione
        lav.Voci.AddRange(r.Nuove);
        lav.Tocca();
        var sv = r.Nuove[0].VeicoloNorm; var componente = r.Nuove[0].Componente;
        AssicuraEspanso(sv, componente);
        // Restano nella casella solo i codici non validi (da correggere); duplicati e aggiunti escono.
        NuovoDtcCodici = string.Join(Environment.NewLine, r.NonValidi);
        RicaricaDtcVisibili();
        Messaggio = string.Format(loc.Traduci("DtcBloccoEsitoFmt"), r.Nuove.Count, (sv.Length == 0 ? DtcRequisito.SenzaVeicolo : sv) + " / " + componente);
    }

    private static string Anteprima(IReadOnlyList<string> codici) =>
        string.Join(", ", codici.Take(10)) + (codici.Count > 10 ? $" … (+{codici.Count - 10})" : "");

    private void RimuoviDtc(DtcRequisito? voce)
    {
        var req = ElementoSelezionato;
        if (!InModifica || req == null || voce == null || DtcInCaricamento) return;
        var lav = CaricaLavoro(req);
        if (!lav.Voci.Remove(voce)) return;
        lav.Tocca();
        RicaricaDtcVisibili();
    }

    protected override bool OperazioneInCorso => ImportDtcInCorso || DtcInCaricamento;

    private bool _scartateConfermate;

    /// <summary>Salva bloccato durante l'import; con righe scartate dall'ultimo import richiede una conferma esplicita
    /// (secondo click su Salva dopo il messaggio "N righe scartate: salvare comunque?").</summary>
    protected override bool PuoProcedereAlSalvataggio()
    {
        var loc = LocalizationService.Instance;
        if (ImportDtcInCorso) { MessaggioErrore = loc.Traduci("DtcImportInCorsoNonSalvare"); return false; }
        var n = UltimoReportDtc?.Scartate.Count ?? 0;
        if (n > 0 && !_scartateConfermate)
        {
            _scartateConfermate = true;
            MessaggioErrore = string.Format(loc.Traduci("DtcImportScartateConfermaFmt"), n);
            return false;
        }
        _scartateConfermate = false;
        UltimoReportDtc = null;
        return true;
    }

    protected override bool HaModifiche()
    {
        if (base.HaModifiche()) return true;
        lock (_dtcGate) return _dtcLavoro.Values.Any(l => l.Modificato);
    }

    /// <summary>Riepilogo "+N/-M guasto" per il Changelog (confronto per chiave Codice|componente con la base della sessione).</summary>
    protected override string? DifferenzeExtra(Requisito prev, Requisito item)
    {
        var lav = LavoroEsistente(item);
        if (lav is not { Modificato: true }) return null;
        var b = lav.Voci.Select(ChiaveDtc).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plus = b.Count(x => !lav.ChiaviBase.Contains(x)); var minus = lav.ChiaviBase.Count(x => !b.Contains(x));
        return plus == 0 && minus == 0 ? null : $"+{plus}/-{minus} guasto";
    }

    /// <summary>Scrive gli archivi guasto dei SOLI requisiti con guasto modificati (batch), poi aggiorna sulle copie staged il
    /// conteggio e azzera l'eventuale guasto embedded del formato precedente. Un requisito senza modifiche guasto non riscrive
    /// alcun archivio. Va PRIMA del commit dei requisiti: se questo fallisce nulla e stato salvato. Se invece fallisse il
    /// commit dei requisiti (conflitto/IO) l'archivio e gia scritto: il requisito resta marcato "archivio scritto, requisito
    /// da allineare" (<see cref="DtcLavoro.ArchivioScritto"/>) e il retry riapplica conteggio e Guasto=null SENZA riscrivere
    /// l'archivio; il marcatore si azzera solo in <see cref="DopoPersistenza"/>, cioe dopo l'esito OK del commit.</summary>
    protected override void PrimaDiPersistere(List<Requisito> daSalvare)
    {
        List<(Guid Uid, DtcLavoro Lav)> modificati;
        lock (_dtcGate) modificati = _dtcLavoro.Where(kv => kv.Value.Modificato).Select(kv => (kv.Key, kv.Value)).ToList();
        if (modificati.Count == 0) return;
        var staged = new Dictionary<Guid, Requisito>();
        foreach (var c in daSalvare) if (MasterEntityIdentityService.TryGet(c, out var u)) staged[u] = c;
        var scritture = new List<DtcScrittura>();
        var perUid = new Dictionary<Guid, DtcLavoro>();
        foreach (var (uid, lav) in modificati)
        {
            if (!staged.TryGetValue(uid, out var clone)) continue; // riga rimossa nella sessione
            if (!lav.ArchivioScritto) scritture.Add(new DtcScrittura(clone, lav.Voci, lav.Documento));
            perUid[uid] = lav;
        }
        var archivio = StatoApp.Master.RequisitiDtc;
        if (!archivio.Disponibile)
        {
            // Workspace non Phase D: formato embedded storico.
            foreach (var (uid, lav) in perUid) staged[uid].Guasto = lav.Voci.Count == 0 ? null : lav.Voci.Select(v => v.Clona()).ToList();
            return;
        }
        Dictionary<Guid, DtcArchivioRequisito> scritti;
        try { scritti = archivio.Scrivi(scritture, StatoApp.Config.OperatoreAttivo); }
        catch (Exception ex)
        {
            // 6.6.0: scrittura tutto-o-niente (una sola riga journal): nessun archivio e' cambiato, il Salvataggio si puo' ripetere.
            throw new InvalidOperationException(string.Format(LocalizationService.Instance.Traduci("DtcSalvataggioAtomicoFallitoFmt"), ex.Message), ex);
        }
        foreach (var (uid, lav) in perUid)
        {
            if (scritti.TryGetValue(uid, out var doc))
            {
                lav.Documento = doc;
                // Merge a 3 vie con un altro PC: la copia di lavoro adotta il risultato fuso (nessuna perdita remota).
                if (!lav.Voci.Select(ChiaveDtc).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        .SetEquals(doc.Voci.Select(ChiaveDtc)))
                {
                    lav.Voci.Clear();
                    lav.Voci.AddRange(doc.Voci.Select(v => v.Clona()));
                }
            }
            lav.ArchivioScritto = true;
            var clone = staged[uid];
            clone.Guasto = null;
            clone.DtcConteggio = lav.Voci.Count == 0 ? null : lav.Voci.Count;
        }
    }

    /// <summary>Annulla dopo un Salva fallito: gli archivi gia scritti (ArchivioScritto) tornano alle voci originali (merge a 3 vie
    /// con l'eventuale remoto, mai tombstone: i guasti aggiunti da altri PC nel frattempo restano), cosi non restano guasto annullati dall'utente ne
    /// resuscitabili da un merge successivo. Se la riscrittura fallisce l'editing resta aperto con avviso.</summary>
    protected override bool PrimaDiAnnullare()
    {
        List<DtcLavoro> scritti;
        lock (_dtcGate) scritti = _dtcLavoro.Values.Where(l => l.ArchivioScritto && l.Requisito != null).ToList();
        if (scritti.Count == 0) return true;
        var archivio = StatoApp.Master.RequisitiDtc;
        var op = StatoApp.Config.OperatoreAttivo;
        // Sempre merge a 3 vie (mai tombstone): sottrae solo le voci scritte da questo PC e preserva quelle aggiunte nel
        // frattempo da altri PC. Se l'archivio non esisteva all'apertura VociBase e' vuoto: puo' restare un documento
        // vuoto, innocuo (conteggio 0, ignorato in lettura per i legacy), mai la cancellazione di dati altrui.
        // 6.6.0: il ripristino e' UNA sola scrittura atomica (come il Salvataggio): o tornano tutti alla baseline o nessuno.
        try
        {
            var uids = scritti.Select(lav => MasterEntityIdentityService.GetOrCreate(lav.Requisito!)).ToList();
            var esito = archivio.Scrivi(scritti.Select(lav => new DtcScrittura(lav.Requisito!, lav.VociBase, lav.Documento)).ToList(), op);
            for (var i = 0; i < scritti.Count; i++)
            {
                if (esito.TryGetValue(uids[i], out var doc)) scritti[i].Documento = doc;
                scritti[i].ArchivioScritto = false;
            }
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Ripristino archivio guasto all'annullamento", ex);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("DtcAnnullaRiscritturaAtomicaFallitaFmt"), scritti.Count, ex.Message);
            return false;
        }
        return true;
    }

    /// <summary>Commit dei requisiti riuscito: ora (e solo ora) lo stato "archivio scritto, requisito da allineare" si chiude.
    /// Requisiti rimossi nella sessione: l'archivio orfano viene tombstonato (best-effort, mai blocca il salvataggio).</summary>
    protected override void DopoPersistenza(List<Requisito> daSalvare, IReadOnlyList<Requisito> snapshotPrecedente)
    {
        lock (_dtcGate) foreach (var l in _dtcLavoro.Values) { l.Modificato = false; l.ArchivioScritto = false; }
        PulisciArchiviOrfani(daSalvare, snapshotPrecedente);
    }

    private void PulisciArchiviOrfani(IEnumerable<Requisito> rimasti, IEnumerable<Requisito> prima)
    {
        try
        {
            var archivio = StatoApp.Master.RequisitiDtc;
            if (!archivio.Disponibile) return;
            var restano = rimasti.Select(r => MasterEntityIdentityService.TryGet(r, out var u) ? u : Guid.Empty).ToHashSet();
            foreach (var r in prima)
                if (RequisitoDtcArchivio.TryUid(r, out var uid) && !restano.Contains(uid))
                    archivio.Rimuovi(r, StatoApp.Config.OperatoreAttivo);
        }
        catch (Exception ex) { CrashLogService.Registra("Pulizia archivio guasto requisiti rimossi", ex); }
    }

    private string _avvisoDtcVolume = "";
    /// <summary>Avviso morbido (nessun limite) quando il requisito selezionato supera la soglia di guasto; segnala anche
    /// l'archivio mancante/illeggibile e il caricamento in corso.</summary>
    public string AvvisoDtcVolume { get => _avvisoDtcVolume; private set { if (SetProperty(ref _avvisoDtcVolume, value)) OnPropertyChanged(nameof(HaAvvisoDtcVolume)); } }
    public bool HaAvvisoDtcVolume => AvvisoDtcVolume.Length > 0;
    private void AggiornaAvvisoVolume()
    {
        var req = ElementoSelezionato;
        var parti = new List<string>();
        if (DtcInCaricamento) parti.Add(LocalizationService.Instance.Traduci("DtcCaricamentoInCorsoTesto"));
        var lav = LavoroEsistente(req);
        if (lav?.Avviso is { Length: > 0 } a) parti.Add(a);
        var n = lav?.Voci.Count ?? req?.DtcTotale ?? 0;
        if (req != null && n > DtcImportRequisitoService.SogliaAvvisoDtcPerRequisito) parti.Add(DtcImportRequisitoService.AvvisoVolume(req.Id, n));
        AvvisoDtcVolume = string.Join(" ", parti);
    }

    /// <summary>Risolve i requisiti Extra citati dall'import sull'INTERO catalogo (non solo la finestra paginata Items):
    /// prima Items, poi lettura per Id dall'indice (CaricaRigaEsistenteFuoriFinestra), che aggiunge la riga a Items e alla
    /// baseline cosi il Salva la includa. Solo Extra non archiviati; gli altri restano assenti (righe scartate).
    /// Va chiamato sul thread UI.</summary>
    private Dictionary<string, Requisito> RisolviRequisitiImport(IEnumerable<string> ids, bool massivo, Requisito? selezionato)
    {
        var risultato = new Dictionary<string, Requisito>(StringComparer.OrdinalIgnoreCase);
        if (!massivo)
        {
            if (selezionato != null && RequisitoApplicabilita.ED4D(selezionato) && !selezionato.Archiviata) risultato[selezionato.Id] = selezionato;
            return risultato;
        }
        var perId = new Dictionary<string, Requisito>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Items) perId.TryAdd(r.Id, r);
        foreach (var id in ids)
        {
            if (!perId.TryGetValue(id, out var rq)) rq = CaricaRigaEsistenteFuoriFinestra(id);
            if (rq != null && RequisitoApplicabilita.ED4D(rq) && !rq.Archiviata) risultato[id] = rq;
        }
        return risultato;
    }

    private void ApplicaReportDtc(DtcImportReport report, Dictionary<string, Requisito> perId)
    {
        var loc = LocalizationService.Instance;
        foreach (var g in report.Importate.GroupBy(v => v.RequisitoId, StringComparer.OrdinalIgnoreCase))
        {
            var rq = perId[g.Key];
            var lav = CaricaLavoro(rq);
            lav.Voci.AddRange(g.Select(v => v.Voce));
            lav.Tocca();
            // Anche per requisiti diversi da quello selezionato: ModificatoDa/UltimaModifica e Changelog sono aggiornati
            // dal confronto Guasto in RegistraModifiche al Salva.
        }
        RicaricaDtcVisibili();
        AggiornaAvvisoVolume();
        _scartateConfermate = false;
        MessaggioErrore = report.Rifiutato
            ? string.Format(loc.Traduci("DtcImportRifiutatoFmt"), string.Join("; ", report.Scartate))
            : report.Scartate.Count > 0 ? string.Format(loc.Traduci("DtcImportScartateFmt"), report.Scartate.Count, string.Join("; ", report.Scartate.Take(8))) : null;
        if (!report.Rifiutato)
            Messaggio = string.Format(loc.Traduci("DtcImportEsitoFmt"), report.Importate.Count, report.Duplicate)
                + (report.EcuSconosciute.Count > 0 ? " " + string.Format(loc.Traduci("DtcImportEcuSconosciuteFmt"), string.Join(", ", report.EcuSconosciute.Take(10))) : "")
                + (report.Avvisi is { Count: > 0 } ? " " + string.Join(" ", report.Avvisi) : "");
        UltimoReportDtc = report;
        ImpostaReportImport(string.Format(loc.Traduci("ImportReportDtcFmt"), report.Importate.Count, report.Duplicate, report.Scartate.Count, report.EcuSconosciute.Count));
    }

    /// <summary>Import guasto da righe gia in memoria (Excel): stesse regole dell'import CSV (catalogo intero, report). UX-4: il
    /// report si calcola senza scritture; i guasti si aggiungono solo dopo la conferma dell'anteprima (annullando nulla cambia).</summary>
    public async Task ImportaDtcDaRighe(IReadOnlyList<List<string>> righe, bool massivo = false)
    {
        var loc = LocalizationService.Instance;
        var selezionato = ElementoSelezionato;
        if (!InModifica || ImportDtcInCorso) { MessaggioErrore = loc.Traduci("DtcImportaSelezionaRequisito"); return; }
        if (!massivo && (selezionato == null || !RequisitoApplicabilita.ED4D(selezionato))) { MessaggioErrore = loc.Traduci("DtcImportaSelezionaRequisito"); return; }
        var (ids, _) = DtcImportRequisitoService.RaccogliRequisiti(righe, selezionato?.Id);
        var perId = RisolviRequisitiImport(ids, massivo, selezionato);
        var ecuNote = StatoApp.Master.Componente.Load().Where(e => !e.Archiviata).Select(e => e.Nome).ToList();
        var report = DtcImportRequisitoService.Interpreta(righe, id => perId.TryGetValue(id, out var rq) ? VociDi(rq) : null, selezionato?.Id, ecuNote,
            veicoliDichiarati: id => perId.TryGetValue(id, out var rq) ? RequisitoApplicabilita.SottoveicoliDi(rq) : null);
        // Finche la conferma e' aperta Salva e gli altri import restano bloccati (come per l'import CSV).
        bool confermato;
        ImportDtcInCorso = true;
        try { confermato = await ConfermaAnteprimaDtcAsync(report); }
        finally { ImportDtcInCorso = false; }
        if (!InModifica || (!massivo && !ReferenceEquals(ElementoSelezionato, selezionato))) return;
        if (!confermato) { Messaggio = loc.Traduci("DtcImportAnnullato"); return; }
        ApplicaReportDtc(report, perId);
    }

    /// <summary>Anteprima dell'import guasto (UX-4): conteggi e primi errori del report gia calcolato, senza scritture.
    /// Non chiede nulla se l'import e' rifiutato per struttura o non aggiunge guasto: il report finale basta.</summary>
    private static async Task<bool> ConfermaAnteprimaDtcAsync(DtcImportReport report)
    {
        if (report.Rifiutato || report.Importate.Count == 0) return true;
        var a = AnteprimaDtcImport.Da(report);
        var righe = new List<string>
        {
            string.Format(LocalizationService.Instance.Traduci("DtcAnteprimaRequisitiFmt"), a.RequisitiToccati),
            string.Format(LocalizationService.Instance.Traduci("DtcAnteprimaNuoviFmt"), a.DtcNuovi),
            string.Format(LocalizationService.Instance.Traduci("DtcAnteprimaGiaPresentiFmt"), a.GiaPresenti),
            string.Format(LocalizationService.Instance.Traduci("DtcAnteprimaRipetutiFmt"), a.DuplicatiNelFile),
            string.Format(LocalizationService.Instance.Traduci("DtcAnteprimaErroriFmt"), a.RigheConErrore),
        };
        righe.AddRange(a.PrimiErrori.Select(e => "  - " + e));
        if (a.RigheConErrore > a.PrimiErrori.Count) righe.Add(string.Format(LocalizationService.Instance.Traduci("DtcAnteprimaAltreFmt"), a.RigheConErrore - a.PrimiErrori.Count));
        if (report.EcuSconosciute.Count > 0) righe.Add(LocalizationService.Instance.Traduci("DtcAnteprimaEcuSconosciuteTesto") + string.Join(", ", report.EcuSconosciute.Take(10)));
        if (report.Avvisi is { Count: > 0 } avvisi) righe.AddRange(avvisi);
        righe.Add("");
        righe.Add(LocalizationService.Instance.Traduci("DtcAnteprimaNoteTesto"));
        return await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("DtcAnteprimaTitolo"), string.Join("\n", righe), LocalizationService.Instance.Traduci("ImportaBtnSemplice"), LocalizationService.Instance.Traduci("Annulla"));
    }

    private CancellationTokenSource? _importDtcCts;
    private bool _importDtcInCorso;
    public bool ImportDtcInCorso { get => _importDtcInCorso; private set { if (SetProperty(ref _importDtcInCorso, value)) { OnPropertyChanged(nameof(DtcModificabile)); OnPropertyChanged(nameof(PuoImportareDtcMassivo)); } } }
    private string _importDtcProgresso = "";
    public string ImportDtcProgresso { get => _importDtcProgresso; private set => SetProperty(ref _importDtcProgresso, value); }
    public void AnnullaImportDtc() => _importDtcCts?.Cancel();

    /// <summary>Import guasto da CSV in STREAMING (due passate: requisiti citati, poi interpretazione a righe), fuori dal thread
    /// UI, con progresso e annullabile (nulla applicato se annullato). Salva/Annulla sono bloccati finche dura; dopo
    /// l'await si ricontrolla che la modifica sia ancora aperta e la selezione invariata. Opera sull'intero catalogo
    /// (vedi <see cref="RisolviRequisitiImport"/>). Il report finale e sempre mostrato.</summary>
    public async Task ImportaDtcDaCsvAsync(string percorso, bool massivo)
    {
        var loc = LocalizationService.Instance;
        if (!InModifica || ImportDtcInCorso) return;
        var selezionato = ElementoSelezionato;
        if (!massivo && (selezionato == null || !RequisitoApplicabilita.ED4D(selezionato))) { MessaggioErrore = loc.Traduci("DtcImportaSelezionaRequisito"); return; }
        var ecuNote = StatoApp.Master.Componente.Load().Where(e => !e.Archiviata).Select(e => e.Nome).ToList();
        var cts = _importDtcCts = new CancellationTokenSource();
        ImportDtcInCorso = true; ImportDtcProgresso = "0";
        ImpostaRiprovaImport(() => ImportaDtcDaCsvAsync(percorso, massivo)); // 6.6.0: stesso file per "Riprova import"
        try
        {
            // Passata 1 (background): requisiti citati; poi risoluzione sul thread UI.
            var (ids, errore) = await Task.Run(() =>
            {
                using var r1 = new StreamReader(percorso);
                return DtcImportRequisitoService.RaccogliRequisiti(DtcImportService.LeggiCsvStreaming(r1), selezionato?.Id, cts.Token);
            }, cts.Token);
            if (!InModifica) return;
            var perId = RisolviRequisitiImport(ids, massivo, selezionato);
            // Passata 2 (background): interpretazione.
            var progresso = new Progress<int>(n => ImportDtcProgresso = n.ToString());
            var report = await Task.Run(() =>
            {
                using var reader = new StreamReader(percorso);
                return DtcImportRequisitoService.Interpreta(DtcImportService.LeggiCsvStreaming(reader),
                    id => perId.TryGetValue(id, out var rq) ? VociDi(rq) : null,
                    selezionato?.Id, ecuNote, progresso, cts.Token,
                    id => perId.TryGetValue(id, out var rq) ? RequisitoApplicabilita.SottoveicoliDi(rq) : null);
            }, cts.Token);
            // Dopo l'await: la modifica potrebbe essere stata chiusa o la selezione cambiata.
            if (!InModifica || cts.IsCancellationRequested) return;
            // UX-4: anteprima con conteggi prima di applicare (ImportDtcInCorso resta attivo durante la conferma).
            if (!await ConfermaAnteprimaDtcAsync(report)) { Messaggio = loc.Traduci("DtcImportAnnullato"); return; }
            if (!InModifica) return;
            ApplicaReportDtc(report, perId);
        }
        catch (OperationCanceledException) { Messaggio = loc.Traduci("DtcImportAnnullato"); }
        catch (IOException ex)
        {
            MessaggioErrore = string.Format(loc.Traduci("ImportExcelErroreFmt"), ex.Message);
            SegnaImportFallito(string.Format(loc.Traduci("ImportReportDtcFallitoFmt"), ex.Message));
        }
        finally { ImportDtcInCorso = false; }
    }

    /// <summary>Export Excel (stesso formato dell'import) dei requisiti salvati della scheda corrente: legge il catalogo completo, non la
    /// sola pagina visibile, e non include le modifiche non ancora salvate.</summary>
    public async Task EsportaExcelVerso(string percorso)
    {
        var loc = LocalizationService.Instance;
        var scheda = Scheda;
        var requisiti = await Task.Run(() => StatoApp.Master.Requisiti.Load()
            .Where(r => RequisitoApplicabilita.TipoDi(r) == scheda).ToList());
        await Task.Run(() => RequisitiExcelExportService.Esporta(requisiti, percorso));
        MessaggioErrore = null;
        Messaggio = loc.Scegli($"Esportati {requisiti.Count(r => !r.Archiviata)} requisiti in Excel.",
            $"Exported {requisiti.Count(r => !r.Archiviata)} requirements to Excel.",
            $"{requisiti.Count(r => !r.Archiviata)} exigences exportées vers Excel.");
    }

    /// <summary>Ultimo report di import guasto (importate/duplicate/scartate con motivo), per UI e test.</summary>
    public DtcImportReport? UltimoReportDtc { get; private set; }

    /// <summary>Ordine posizionale import Excel (feedback utente 2026-08-20, punto 8): stesso ordine delle
    /// colonne editabili a schermo in RequisitiView (Id, Testo, ApplicabileBanco, ApplicabileVeicolo).</summary>
    /// <summary>Contratto Excel: A Id, B Testo, C Banco, D Veicolo (invariate) + E OPZIONALE "Veicolo-componente Extra" (coppie
    /// "Veicolo|componente; Veicolo|componente", solo scheda Extra). Un file a 4 colonne (o con la E vuota) non tocca mai le coppie esistenti;
    /// con la E valorizzata le coppie si AGGIUNGONO a quelle esistenti (mai rimosse/sovrascritte).</summary>
    protected override IReadOnlyList<string> ColonneImportOrdine => new[] { nameof(Requisito.Id), nameof(Requisito.Testo), nameof(Requisito.ApplicabileBanco), nameof(Requisito.ApplicabileVeicolo), nameof(Requisito.VeicoliEcuD4DTesto) };

    protected override IReadOnlyList<string> ValidaRigaImport(Requisito item)
    {
        var loc = LocalizationService.Instance;
        const string col = "Veicolo-componente Extra";
        var errori = new List<string>();
        if (item.VeicoliEcuD4DNonValide is { Count: > 0 } nonValide)
            errori.Add(loc.Scegli(
                $"Colonna {col}: formato non valido ({string.Join("; ", nonValide)}); atteso \"Veicolo|componente; Veicolo|componente\".",
                $"Column {col}: invalid format ({string.Join("; ", nonValide)}); expected \"Vehicle|componente; Vehicle|componente\".",
                $"Colonne {col} : format non valide ({string.Join("; ", nonValide)}) ; attendu « Véhicule|componente; Véhicule|componente »."));
        if (item.VeicoliEcuD4D is { Count: > 0 } coppie)
        {
            if (!ESchedaD4d)
                errori.Add(loc.Scegli($"Colonna {col} valorizzata: le coppie si importano solo nella scheda Extra.",
                    $"Column {col} is filled in: pairs can only be imported in the Extra tab.",
                    $"Colonne {col} renseignée : les paires ne s'importent que dans l'onglet Extra."));
            else
            {
                var veicoli = VeicoliAnagrafica; var componente = EcuDisponibili;
                var ignoti = coppie.SelectMany(c => new[]
                    {
                        veicoli.Contains(c.Veicolo, StringComparer.OrdinalIgnoreCase) ? null : loc.Scegli("veicolo ", "vehicle ", "véhicule ") + c.Veicolo,
                        componente.Contains(c.Componente, StringComparer.OrdinalIgnoreCase) ? null : "Componente " + c.Componente
                    })
                    .Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (ignoti.Count > 0)
                    errori.Add(loc.Scegli($"Colonna {col}: non presenti nei master ({string.Join(", ", ignoti)}).",
                        $"Column {col}: not present in the masters ({string.Join(", ", ignoti)}).",
                        $"Colonne {col} : absents des référentiels ({string.Join(", ", ignoti)})."));
                // Stessa invariante della UI (selezione sottoveicoli): se il requisito esistente dichiara dei sottoveicoli, le coppie
                // possono riguardare solo quei veicoli. Un requisito nuovo non dichiara sottoveicoli (non importabili): nessun vincolo.
                var esistente = Items.FirstOrDefault(r => string.Equals(r.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                    ?? StatoApp.Master.Requisiti.Load().FirstOrDefault(r => string.Equals(r.Id, item.Id, StringComparison.OrdinalIgnoreCase));
                if (esistente != null && RequisitoApplicabilita.ED4D(esistente) && RequisitoApplicabilita.SottoveicoliDi(esistente) is { Count: > 0 } ammessi)
                {
                    var fuori = coppie.Select(c => c.Veicolo).Where(v => !ammessi.Contains(v, StringComparer.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (fuori.Count > 0)
                        errori.Add(loc.Scegli(
                            $"Colonna {col}: veicoli non ammessi dai sottoveicoli del requisito {item.Id} ({string.Join(", ", fuori)}); ammessi: {string.Join(", ", ammessi)}.",
                            $"Column {col}: vehicles not allowed by the sub-vehicles of requirement {item.Id} ({string.Join(", ", fuori)}); allowed: {string.Join(", ", ammessi)}.",
                            $"Colonne {col} : véhicules non admis par les sous-véhicules de l'exigence {item.Id} ({string.Join(", ", fuori)}) ; admis : {string.Join(", ", ammessi)}."));
                }
            }
        }
        return errori;
    }

    /// <summary>Unione additiva delle coppie Extra su un requisito Extra gia' esistente: aggiunge le coppie mancanti (i nomi come
    /// scritti nel file, gia' validati contro i master), non rimuove mai nulla. Righe senza coppie nel file, o requisiti esistenti non Extra: invariati (saltati).</summary>
    protected override bool ProvaUnisciRigaImport(Requisito destinazione, Requisito sorgente)
    {
        if (sorgente.VeicoliEcuD4D is not { Count: > 0 } nuove || !RequisitoApplicabilita.ED4D(destinazione)) return false;
        var esistenti = destinazione.VeicoliEcuD4D ?? new List<RequisitoVeicoloEcu>();
        var risultato = esistenti.Select(c => new RequisitoVeicoloEcu { Veicolo = c.Veicolo, Componente = c.Componente }).ToList();
        var aggiunte = 0;
        foreach (var c in nuove)
        {
            if (risultato.Any(x => string.Equals(x.Veicolo, c.Veicolo, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Componente, c.Componente, StringComparison.OrdinalIgnoreCase))) continue;
            risultato.Add(new RequisitoVeicoloEcu { Veicolo = c.Veicolo, Componente = c.Componente });
            aggiunte++;
        }
        if (aggiunte == 0) return false;
        destinazione.VeicoliEcuD4D = risultato;
        destinazione.UltimaModifica = DateTime.Now;
        destinazione.ModificatoDa = StatoApp.Config.OperatoreAttivo;
        destinazione.Changelog.Add(new ChangelogEntry
        {
            Data = DateTime.Now, Operatore = StatoApp.Config.OperatoreAttivo,
            Campo = nameof(Requisito.VeicoliEcuD4D), ValoreNuovo = destinazione.VeicoliEcuD4DTesto
        });
        return true;
    }

    protected override bool HaRiferimenti(Requisito item, IReadOnlyList<TestCase> testCases) =>
        testCases.Any(tc => tc.RequisitoId.Contains(item.Id));
}

public class CategoriaViewModel : MasterDataEditorViewModelBase<Categoria>
{
    /// <summary>Titolo del pannello annidato, segue l'Etichetta rinominata dal Master nel catalogo campi di
    /// sistema (Addendum: grill-me 2026-08-18, follow-up) — fallback "Categoria" se non rinominata. Calcolato
    /// anche da FunzionalitaViewModel.TitoloCategoria (perf: profilo rivisita 2026-09-25) quando questo editor
    /// annidato non è ancora stato costruito, per mostrare l'intestazione dell'Expander senza pagare il costo
    /// pieno del ctor — le due letture restano identiche perché leggono lo stesso catalogo.</summary>
    public string TitoloCategoria { get; }

    public CategoriaViewModel(AppState state) : base(state, state.Master.Categorie, state.Paths.CategorieFile, c => c.Nome)
    {
        TitoloCategoria = RuoloCampoSistemaLookup.EtichettaDelRuolo(state.CatalogoCampiSistemaCompleto(), RuoloCampoSistema.Categoria, "Categoria");
    }

    /// <summary>Non è un campo di TestCase (vive su Funzionalita.Categorie), quindi il riferimento va cercato
    /// lì invece che nei TestCase passati.</summary>
    protected override bool HaRiferimenti(Categoria item, IReadOnlyList<TestCase> testCases) =>
        StatoApp.Master.Funzionalita.Load().Any(f => f.Categorie.Contains(item.Nome));
}

public class FunzionalitaViewModel : MasterDataEditorViewModelBase<Funzionalita>
{
    /// <summary>Categoria non è più testo libero (Addendum §10, feedback QA): gestita qui dentro Funzionalità,
    /// stesso schema di Gamma dentro Veicoli — un elenco proprio, annidato invece che a parte.
    /// Costruito PIGRAMENTE (perf: profilo rivisita Funzionalità 2026-09-25, ~52ms di questo ctor annidato su
    /// ogni apertura della pagina): l'Expander che lo mostra parte collassato, quindi la stragrande maggioranza
    /// delle visite non lo apre mai — costruirlo comunque a ogni ctor di FunzionalitaViewModel pagava un intero
    /// giro di Ricarica() + il calcolo di TitoloCategoria per un pannello che l'utente non guarda. Creato al
    /// primo accesso reale: la prima apertura dell'Expander (FunzionalitaView.axaml.cs) o qualunque altro
    /// accesso a questa proprietà.</summary>
    private CategoriaViewModel? _categorie;
    public CategoriaViewModel Categorie => _categorie ??= CreaCategorie();

    private CategoriaViewModel CreaCategorie()
    {
        var categorie = new CategoriaViewModel(StatoApp);
        categorie.Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CategorieDisponibili));
        return categorie;
    }

    /// <summary>Non richiede più di costruire l'intero CategoriaViewModel annidato se non è già stato aperto
    /// (perf): una lettura leggera dello stesso catalogo, sufficiente per popolare il picker "Categorie" di
    /// ogni riga della griglia principale. Se il nested è già stato creato (Expander aperto almeno una volta)
    /// si continua a leggere da lì, cosi' le modifiche non ancora salvate restano visibili nel picker come
    /// prima.
    /// Lettura leggera calcolata una sola volta e invalidata in OnDatiRicaricati (vedi GammeDisponibili di
    /// VeicoliViewModel: un Load() a ogni lettura costava per ogni riga della griglia).</summary>
    private List<string>? _categorieDisponibiliLeggere;
    public List<string> CategorieDisponibili => _categorie != null
        ? _categorie.Items.Where(c => !c.Archiviata).Select(c => c.Nome).ToList()
        : _categorieDisponibiliLeggere ??= StatoApp.Master.Categorie.Load().Where(c => !c.Archiviata).Select(c => c.Nome).ToList();

    /// <summary>Titolo dell'Expander "Categorie" (vedi CategoriaViewModel.TitoloCategoria): calcolato qui senza
    /// costruire il nested per intero, cosi' l'intestazione resta visibile a Expander ancora collassato senza
    /// pagare il costo pieno del ctor annidato. Una volta creato il nested, la sua stessa TitoloCategoria resta
    /// l'unica fonte di verità (nessuna divergenza possibile: entrambe leggono lo stesso catalogo).</summary>
    private string? _titoloCategoriaCache;
    public string TitoloCategoria => _categorie?.TitoloCategoria
        ?? (_titoloCategoriaCache ??= RuoloCampoSistemaLookup.EtichettaDelRuolo(StatoApp.CatalogoCampiSistemaCompleto(), RuoloCampoSistema.Categoria, "Categoria"));

    /// <summary>componente selezionabili nel picker "Componenti associati" di questa pagina (feedback utente 2026-09-23): solo il catalogo attivo,
    /// riletto a ogni DatiRicaricati (vedi RicaricaCataloghiRiferimento) così un componente aggiunta da un'altra pagina compare subito.</summary>
    public List<string> EcuDisponibili { get; private set; } = new();

    /// <summary>Veicoli ammessi nelle restrizioni per componente (colonna D dell'import): solo il catalogo attivo, riletto come EcuDisponibili.</summary>
    public List<string> VeicoliDisponibili { get; private set; } = new();

    /// <summary>componente attive per nome, dal catalogo corrente: il picker componente legge da qui il montaggio dei veicoli di ciascuna componente.</summary>
    private Dictionary<string, Componente> _ecuAttivePerNome = new(StringComparer.OrdinalIgnoreCase);

    public FunzionalitaViewModel(AppState state) : base(state, state.Master.Funzionalita, state.Paths.FunzionalitaFile, f => f.Nome)
    {
        RicaricaCataloghiRiferimento();
    }

    /// <summary>Legge i cataloghi di riferimento (componente attive, veicoli attivi). Chiamata alla creazione e a ogni DatiRicaricati:
    /// senza, un componente o un veicolo cambiato da un'altra pagina o postazione (per esempio la finestra del veicolo) resterebbe
    /// fuori dal picker, e il montaggio mostrato per ogni componente sarebbe quello della pagina aperta.</summary>
    private void RicaricaCataloghiRiferimento()
    {
        var componente = StatoApp.Master.Componente.Load().Where(e => !e.Archiviata).ToList();
        var perNome = new Dictionary<string, Componente>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in componente)
            if (!string.IsNullOrEmpty(e.Nome)) perNome.TryAdd(e.Nome, e);
        _ecuAttivePerNome = perNome;
        EcuDisponibili = componente.Select(e => e.Nome).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        VeicoliDisponibili = StatoApp.Master.Veicoli.Load().Where(v => !v.Archiviata).Select(v => v.Nome).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>componente attiva per nome (catalogo corrente), o null se non è censita o è archiviata.</summary>
    public Componente? EcuAttivaPerNome(string nome) => _ecuAttivePerNome.GetValueOrDefault(nome);

    /// <summary>Riepilogo dei veicoli di un componente nel picker della pagina: "tutti i veicoli", "nessun veicolo" o "N di M veicoli",
    /// sui soli veicoli montati (vedi AssociazioniVeicoloService.RiepilogoVeicoli). Vuoto se il componente non è attiva o non ha veicoli
    /// montati: in quel caso il picker non mostra il riepilogo.</summary>
    public string TestoVeicoliEcu(Funzionalita funzionalita, string componente)
    {
        if (EcuAttivaPerNome(componente) is not { } ecuAttiva) return "";
        var riepilogo = AssociazioniVeicoloService.RiepilogoVeicoli(funzionalita, ecuAttiva);
        if (riepilogo.Montati == 0) return "";
        if (riepilogo.TuttiVeicoli) return LocalizationService.Instance.Traduci("FunzEcuVeicoliTutti");
        if (riepilogo.Ammessi == 0) return LocalizationService.Instance.Traduci("FunzEcuVeicoliNessuno");
        return string.Format(LocalizationService.Instance.Traduci("FunzEcuVeicoliDiFmt"), riepilogo.Ammessi, riepilogo.Montati);
    }

    /// <summary>Scrive i veicoli su cui la funzionalità vale sul componente, dalla checklist del picker componente: stessa regola di
    /// AssociazioniVeicoloService.ImpostaVeicoliFunzione. Solo in modifica; la voce segue il normale salvataggio della pagina.</summary>
    public void ImpostaVeicoliEcu(Funzionalita funzionalita, string componente, IEnumerable<string> veicoliScelti)
    {
        ArgumentNullException.ThrowIfNull(funzionalita);
        if (!InModifica || EcuAttivaPerNome(componente) is not { } ecuAttiva) return;
        AssociazioniVeicoloService.ImpostaVeicoliFunzione(funzionalita, ecuAttiva, veicoliScelti);
    }

    /// <summary>FIX review 2026-09-25 (finding Major): finché Categorie non è mai stata aperta,
    /// CategorieDisponibili/TitoloCategoria leggono il catalogo Categorie direttamente (vedi sopra) invece che
    /// dal nested — che prima era l'unico a notificare un refresh (il suo CollectionChanged girava a ogni
    /// DatiRicaricati tramite OnDatiRicaricati della base). Senza questo override, una categoria aggiunta/
    /// rinominata da un'altra postazione (o un refresh periodico/forzato) non aggiornava più il picker
    /// "Categorie" della griglia Funzionalità finché l'utente non apriva l'Expander almeno una volta.
    /// _titoloCategoriaCache va invalidata insieme, stesso motivo di VeicoliViewModel.OnDatiRicaricati per
    /// Gamma/_titoloGammaCache.</summary>
    protected override void OnDatiRicaricati()
    {
        base.OnDatiRicaricati();
        RicaricaCataloghiRiferimento();
        if (_categorie == null)
        {
            _titoloCategoriaCache = null;
            _categorieDisponibiliLeggere = null;
            OnPropertyChanged(nameof(CategorieDisponibili));
            OnPropertyChanged(nameof(TitoloCategoria));
        }
    }

    public override bool HaModificheNonSalvate() => base.HaModificheNonSalvate() || (_categorie?.HaModificheNonSalvate() ?? false);

    /// <summary>Eccezione mirata alla regola uniforme "tutti i master data solo Master" (feedback utente
    /// 2026-08-20, punto 11): uno Standard può aggiungere nuove Funzionalità, ma modificarne/eliminarne di
    /// esistenti resta riservato a un Master (PuoModificare, invariato) — la vera barriera è comunque
    /// SoloRigheNuoveModificate() al salvataggio, questo controlla solo se il pulsante "Modifica" è cliccabile.</summary>
    public override bool PuoCreare => PuoModificare || (StatoApp.RuoloAttivo == RuoloOperatore.Standard && StatoApp.PuoScrivere);

    /// <summary>Ordine posizionale import Excel (feedback utente 2026-08-20, punto 8; esteso 2026-09-23 con
    /// EcuAssociateTesto): stesso ordine delle colonne editabili a schermo in FunzionalitaView (Nome, Categorie,
    /// Componenti associati). CategorieTesto/EcuAssociateTesto invece delle liste dirette: l'import imposta le
    /// proprietà semplici via reflection con una stringa grezza dalla cella (MasterDataEditorViewModelBase.
    /// ImportaDaExcel), una List&lt;string&gt; non la accetterebbe — stesso schema già in uso per
    /// Componente.VeicoliAssociatiTesto. Più valori nella stessa cella separati da virgola.</summary>
    // Colonna D (facoltativa, assente nei file 6.7.1): veicoli per componente nel formato "Componente => V1, V2; componente2 => V3" (VeicoliPerEcuTesto).
    protected override IReadOnlyList<string> ColonneImportOrdine => new[] { nameof(Funzionalita.Nome), nameof(Funzionalita.CategorieTesto), nameof(Funzionalita.EcuAssociateTesto), nameof(Funzionalita.VeicoliPerEcuTesto) };

    /// <summary>Stessa validazione di EcuViewModel.ValidaRigaImport ma sul verso opposto della relazione: un
    /// nome componente importato che non esiste nel master data verrebbe altrimenti salvato come associazione "silente"
    /// verso un componente inesistente — bloccare l'intero import (atomico, come già per ValidaRigaImport) evidenzia
    /// subito il problema invece di lasciare un riferimento rotto scoperto solo più tardi nel filtro del Test.</summary>
    protected override IReadOnlyList<string> ValidaRigaImport(Funzionalita item)
    {
        var errori = new List<string>();
        var sconosciute = item.EcuAssociate
            .Where(e => !EcuDisponibili.Contains(e, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sconosciute.Count > 0)
            errori.Add(string.Format(LocalizationService.Instance.Traduci("MasterDataEcuNonInMasterFmt"), string.Join(", ", sconosciute)));

        // Veicoli per componente (colonna D): sintassi, Componenti presenti e associate alla funzionalità, veicoli presenti nel master.
        foreach (var segmento in item.VeicoliPerEcuTestoNonValidi)
            errori.Add(string.Format(LocalizationService.Instance.Traduci("MasterDataVeicoliPerEcuSintassiFmt"), segmento));
        var ecuRestrizioni = item.VeicoliPerEcu.Select(v => v.Componente).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ecuRestrizioneSconosciute = ecuRestrizioni.Where(e => !EcuDisponibili.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
        if (ecuRestrizioneSconosciute.Count > 0)
            errori.Add(string.Format(LocalizationService.Instance.Traduci("MasterDataEcuNonInMasterFmt"), string.Join(", ", ecuRestrizioneSconosciute)));
        foreach (var componente in ecuRestrizioni.Where(e => EcuDisponibili.Contains(e, StringComparer.OrdinalIgnoreCase) && !item.ValeSuEcu(e)))
            errori.Add(string.Format(LocalizationService.Instance.Traduci("MasterDataVeicoliPerEcuEcuNonAssociataFmt"), componente));
        var veicoliSconosciuti = item.VeicoliPerEcu.SelectMany(v => v.Veicoli)
            .Where(v => !VeicoliDisponibili.Contains(v, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (veicoliSconosciuti.Count > 0)
            errori.Add(string.Format(LocalizationService.Instance.Traduci("MasterDataVeicoliPerEcuVeicoloNonInMasterFmt"), string.Join(", ", veicoliSconosciuti)));
        return errori;
    }

    /// <summary>Merge additivo dei componenti associate su un duplicato di import, stesso schema di
    /// EcuViewModel.ProvaUnisciRigaImport per VeicoliAssociati: una funzionalità già censita che ricompare in un
    /// nuovo file Excel con altre componente aggiunte non deve perdere le associazioni già presenti. TutteLeEcu/
    /// EcuAssociate sono mutuamente esclusivi (feedback utente 2026-09-23): se la riga sorgente importata è
    /// "Tutti i componenti" esplicito, quello assorbe qualunque elenco già presente sulla destinazione (unione
    /// insiemistica: "tutte" include già ogni componente specifica); se è la destinazione ad essere già "Tutti i componenti",
    /// un elenco specifico dalla sorgente non restringerebbe comunque nulla, quindi non c'è nulla da unire.</summary>
    protected override bool ProvaUnisciRigaImport(Funzionalita destinazione, Funzionalita sorgente)
    {
        // Restrizioni per componente: unite PRIMA di toccare TutteLeEcu/EcuAssociate, che la regola legge nello stato precedente. Una riga senza
        // colonna D (o con la cella vuota) non dice nulla sulle restrizioni: non le tocca, altrimenti un file vecchio le allargherebbe.
        var veicoliPrima = destinazione.VeicoliPerEcuTesto;
        if (sorgente.VeicoliPerEcu.Count > 0) destinazione.UnisciVeicoliPerEcuDa(sorgente);
        var restrizioniCambiate = destinazione.VeicoliPerEcuTesto != veicoliPrima;

        var applicabilitaCambiata = false;
        if (sorgente.TutteLeEcu)
        {
            if (!destinazione.TutteLeEcu)
            {
                destinazione.TutteLeEcu = true;
                destinazione.EcuAssociate = new();
                applicabilitaCambiata = true;
            }
        }
        else if (!destinazione.TutteLeEcu)
        {
            var prima = destinazione.EcuAssociate.Count;
            destinazione.EcuAssociate = destinazione.EcuAssociate
                .Concat(sorgente.EcuAssociate)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            applicabilitaCambiata = destinazione.EcuAssociate.Count != prima;
        }
        if (!applicabilitaCambiata && !restrizioniCambiate) return false;

        destinazione.UltimaModifica = DateTime.Now;
        destinazione.ModificatoDa = StatoApp.Config.OperatoreAttivo;
        if (applicabilitaCambiata)
            destinazione.Changelog.Add(new ChangelogEntry
            {
                Data = DateTime.Now,
                Operatore = StatoApp.Config.OperatoreAttivo,
                Campo = nameof(Funzionalita.EcuAssociate),
                ValoreNuovo = destinazione.EcuAssociateRiepilogo
            });
        if (restrizioniCambiate)
            destinazione.Changelog.Add(new ChangelogEntry
            {
                Data = DateTime.Now,
                Operatore = StatoApp.Config.OperatoreAttivo,
                Campo = nameof(Funzionalita.VeicoliPerEcu),
                ValoreNuovo = destinazione.VeicoliPerEcuTesto
            });
        return true;
    }

    /// <summary>Invariante delle restrizioni per componente prima di ogni salvataggio: voci di componente non più associate scartate, duplicati
    /// fusi (vedi Funzionalita.NormalizzaVeicoliPerEcu). Agisce sulla copia da salvare.</summary>
    protected override void PrimaDiPersistere(List<Funzionalita> daSalvare)
    {
        foreach (var funzionalita in daSalvare) funzionalita.NormalizzaVeicoliPerEcu();
    }

    /// <summary>Categorie è un ViewModel annidato indipendente (propria sottoscrizione a DatiRicaricati):
    /// disporre solo questo senza anche lui lascerebbe quella sottoscrizione agganciata per sempre. Null-safe:
    /// se l'Expander non è mai stato aperto, il nested non esiste e non ha nulla da disporre.</summary>
    public override void Dispose() { _categorie?.Dispose(); base.Dispose(); }

    protected override bool HaRiferimenti(Funzionalita item, IReadOnlyList<TestCase> testCases) =>
        testCases.Any(tc => tc.Funzionalita.Contains(item.Nome));
}

public class EcuViewModel : MasterDataEditorViewModelBase<Componente>
{
    private List<string>? _veicoliDisponibili;

    /// <summary>Veicoli attivi per la validazione delle righe importate da Excel (ValidaRigaImport). Letti alla prima
    /// necessita', non all'apertura dell'editor: la vista non li usa, e il Load del catalogo Veicoli (con il refresh
    /// Phase D che porta con se') pesava ogni volta sull'apertura del componente.</summary>
    public List<string> VeicoliDisponibili => _veicoliDisponibili ??=
        StatoApp.Master.Veicoli.Load().Where(v => !v.Archiviata).Select(v => v.Nome).ToList();

    public EcuViewModel(AppState state) : base(state, state.Master.Componente, state.Paths.EcuFile, e => e.Nome)
    {
    }

    /// <summary>Il catalogo Veicoli può cambiare da un'altra pagina o postazione (la finestra del veicolo, un altro PC): la lettura
    /// usata dalla validazione dell'import Excel si rilegge a ogni DatiRicaricati, invece di restare quella della prima apertura.</summary>
    protected override void OnDatiRicaricati()
    {
        base.OnDatiRicaricati();
        _veicoliDisponibili = null;
    }

    /// <summary>Stessa eccezione mirata di FunzionalitaViewModel (feedback utente 2026-08-20, punto 11):
    /// uno Standard può aggiungere nuove componente, non modificare/eliminare quelle esistenti.</summary>
    public override bool PuoCreare => PuoModificare || (StatoApp.RuoloAttivo == RuoloOperatore.Standard && StatoApp.PuoScrivere);

    /// <summary>Ordine posizionale import Excel (feedback utente 2026-08-20, punto 8): Nome, poi Veicoli
    /// associati (una cella con più nomi separati da virgola, stesso schema di Operatore.GammeAssegnateTesto).</summary>
    protected override IReadOnlyList<string> ColonneImportOrdine => new[] { nameof(Componente.Nome), nameof(Componente.VeicoliAssociatiTesto) };

    protected override IReadOnlyList<string> ValidaRigaImport(Componente item)
    {
        var sconosciuti = item.VeicoliAssociati
            .Where(v => !VeicoliDisponibili.Contains(v, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return sconosciuti.Count == 0
            ? Array.Empty<string>()
            : new[] { string.Format(LocalizationService.Instance.Traduci("MasterDataVeicoliNonInMasterFmt"), string.Join(", ", sconosciuti)) };
    }

    protected override bool ProvaUnisciRigaImport(Componente destinazione, Componente sorgente)
    {
        var prima = destinazione.VeicoliAssociati.Count;
        destinazione.VeicoliAssociati = destinazione.VeicoliAssociati
            .Concat(sorgente.VeicoliAssociati)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (destinazione.VeicoliAssociati.Count == prima) return false;
        destinazione.UltimaModifica = DateTime.Now;
        destinazione.ModificatoDa = StatoApp.Config.OperatoreAttivo;
        destinazione.Changelog.Add(new ChangelogEntry
        {
            Data = DateTime.Now,
            Operatore = StatoApp.Config.OperatoreAttivo,
            Campo = nameof(Componente.VeicoliAssociati),
            ValoreNuovo = string.Join(", ", destinazione.VeicoliAssociati)
        });
        return true;
    }

    /// <summary>Riferimenti che bloccano l'eliminazione definitiva del componente, per tipo: Test attivi, Test eliminati
    /// (nella scheda Archivio) e Funzionalita che la citano (EcuAssociate o VeicoliPerEcu). Una Funzionalita non e' un Test:
    /// va nominata come tale, cosi' l'operatore sa dove rimuovere il riferimento.</summary>
    private sealed record RiferimentiEcu(List<string> TestAttivi, List<string> TestEliminati, List<string> Funzionalita, List<string> Requisiti)
    {
        public bool Trovati => TestAttivi.Count + TestEliminati.Count + Funzionalita.Count + Requisiti.Count > 0;
    }

    /// <summary>Cerca i riferimenti al componente su tutti i Test, eliminati compresi (un Test nell'Archivio che la cita si
    /// ritroverebbe con un componente inesistente al ripristino), e sulle Funzionalita. Sola lettura.</summary>
    private RiferimentiEcu CercaRiferimentiEcu(Componente item)
    {
        var testAttivi = new List<string>();
        var testEliminati = new List<string>();
        if (StatoApp.CartellaValida)
            foreach (var tc in StatoApp.TestCases.GetAll(includeEliminati: true))
            {
                if (!tc.Componente.Contains(item.Nome, StringComparer.OrdinalIgnoreCase)) continue;
                (tc.Eliminato ? testEliminati : testAttivi).Add(tc.Id);
            }
        var funzionalita = StatoApp.Master.Funzionalita.Load()
            .Where(f => f.EcuAssociate.Contains(item.Nome, StringComparer.OrdinalIgnoreCase)
                || f.VeicoliPerEcu.Any(v => string.Equals(v.Componente, item.Nome, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.Nome)
            .ToList();
        var requisiti = StatoApp.Master.Requisiti.Load().Where(r => RequisitoApplicabilita.RiferisceEcu(r, item.Nome)).Select(r => r.Id).ToList();
        return new RiferimentiEcu(testAttivi, testEliminati, funzionalita, requisiti);
    }

    protected override bool HaRiferimenti(Componente item, IReadOnlyList<TestCase> testCases) => CercaRiferimentiEcu(item).Trovati;

    protected override string? DescriviRiferimenti(Componente item, IReadOnlyList<TestCase> testCases)
    {
        var riferimenti = CercaRiferimentiEcu(item);
        if (!riferimenti.Trovati) return null;
        var loc = LocalizationService.Instance;
        var parti = new List<string>();
        if (riferimenti.TestAttivi.Count > 0)
            parti.Add(string.Format(loc.Traduci("EcuEliminaRifTestFmt"), ElencoRiferimenti(riferimenti.TestAttivi)));
        if (riferimenti.TestEliminati.Count > 0)
            parti.Add(string.Format(loc.Traduci("EcuEliminaRifTestEliminatiFmt"), ElencoRiferimenti(riferimenti.TestEliminati)));
        if (riferimenti.Funzionalita.Count > 0)
            parti.Add(string.Format(loc.Traduci("EcuEliminaRifFunzionalitaFmt"), ElencoRiferimenti(riferimenti.Funzionalita)));
        if (riferimenti.Requisiti.Count > 0)
            parti.Add(loc.Scegli("Requisiti: ", "Requirements: ", "Exigences : ") + ElencoRiferimenti(riferimenti.Requisiti));
        return string.Format(loc.Traduci("EcuEliminaRiferimentiFmt"), item.Nome, string.Join("; ", parti));
    }

    private static string ElencoRiferimenti(IReadOnlyList<string> voci) =>
        string.Join(", ", voci.Take(10)) + (voci.Count > 10 ? $" … (+{voci.Count - 10})" : "");
}

public class GammaViewModel : MasterDataEditorViewModelBase<Gamma>
{
    /// <summary>Titolo del pannello annidato, segue l'Etichetta rinominata dal Master nel catalogo campi di
    /// sistema (Addendum: grill-me 2026-08-18, follow-up) — fallback "Gamma" se non rinominata. Calcolato anche
    /// da VeicoliViewModel.TitoloGamma (perf: profilo rivisita 2026-09-25) quando questo editor annidato non è
    /// ancora stato costruito, per mostrare l'intestazione dell'Expander senza pagare il costo pieno del ctor —
    /// le due letture restano identiche perché leggono lo stesso catalogo.</summary>
    public string TitoloGamma { get; }

    public GammaViewModel(AppState state) : base(state, state.Master.Gamma, state.Paths.GammaFile, g => g.Nome)
    {
        TitoloGamma = RuoloCampoSistemaLookup.EtichettaDelRuolo(state.CatalogoCampiSistemaCompleto(), RuoloCampoSistema.Gamma, "Gamma");
    }

    /// <summary>Non è un campo di TestCase (vive su Veicolo.GammaNome), quindi il riferimento va cercato lì
    /// invece che nei TestCase passati.</summary>
    protected override bool HaRiferimenti(Gamma item, IReadOnlyList<TestCase> testCases) =>
        StatoApp.Master.Veicoli.Load().Any(v => v.GammaNome == item.Nome);
}

public class VeicoliViewModel : MasterDataEditorViewModelBase<Veicolo>
{
    /// <summary>Gamma veicolari non è più una sezione separata (Addendum redesign 2026-08, revisione feedback
    /// QA): gestita qui dentro Veicoli, dove viene comunque scelta per ogni sottoveicolo — stesso editor
    /// riusato pari pari, solo annidato invece che dietro una voce a parte nel menu Impostazioni.
    /// Costruito PIGRAMENTE (perf: profilo rivisita Veicoli 2026-09-25, ~140ms di questo ctor annidato su
    /// ogni apertura della pagina — quasi il 60% dei ~236ms di ImpostazioniViewModel.SostituisciPaginaCorrente):
    /// l'Expander che lo mostra (VeicoliView.axaml) parte collassato, quindi la stragrande maggioranza delle
    /// visite non lo apre mai. Creato al primo accesso reale: la prima apertura dell'Expander
    /// (VeicoliView.axaml.cs) o qualunque altro accesso a questa proprietà.</summary>
    private GammaViewModel? _gamme;
    public GammaViewModel Gamme => _gamme ??= CreaGamme();

    private GammaViewModel CreaGamme()
    {
        var gamme = new GammaViewModel(StatoApp);
        gamme.Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(GammeDisponibili));
        return gamme;
    }

    /// <summary>Non richiede più di costruire l'intero GammaViewModel annidato se non è già stato aperto
    /// (perf): una lettura leggera dello stesso catalogo, sufficiente per popolare la colonna/il picker
    /// "Gamma" di ogni riga della griglia principale dei Veicoli. Se il nested è già stato creato (Expander
    /// aperto almeno una volta) si continua a leggere da lì, cosi' le modifiche non ancora salvate restano
    /// visibili nel picker come prima.
    /// La lettura leggera è calcolata una sola volta e invalidata in OnDatiRicaricati (stesso momento in cui il
    /// nested, se esistesse, ricaricherebbe i propri Items): la colonna "Gamma" interroga questa proprietà più
    /// volte per OGNI riga, e un Load() del catalogo a ogni lettura portava Master.Veicoli da ~460 a ~1490 ms
    /// (misura headless 2026-09-25).</summary>
    private List<string>? _gammeDisponibiliLeggere;
    public List<string> GammeDisponibili => _gamme != null
        ? _gamme.Items.Where(g => !g.Archiviata).Select(g => g.Nome).ToList()
        : _gammeDisponibiliLeggere ??= StatoApp.Master.Gamma.Load().Where(g => !g.Archiviata).Select(g => g.Nome).ToList();

    /// <summary>Titolo dell'Expander "Gamma" (vedi GammaViewModel.TitoloGamma): calcolato qui senza costruire
    /// il nested per intero, cosi' l'intestazione resta visibile a Expander ancora collassato senza pagare il
    /// costo pieno del ctor annidato. Una volta creato il nested, la sua stessa TitoloGamma resta l'unica fonte
    /// di verità (nessuna divergenza possibile: entrambe leggono lo stesso catalogo).</summary>
    private string? _titoloGammaCache;
    public string TitoloGamma => _gamme?.TitoloGamma
        ?? (_titoloGammaCache ??= RuoloCampoSistemaLookup.EtichettaDelRuolo(StatoApp.CatalogoCampiSistemaCompleto(), RuoloCampoSistema.Gamma, "Gamma"));

    public VeicoliViewModel(AppState state) : base(state, state.Master.Veicoli, state.Paths.VeicoliFile, v => v.Nome)
    {
    }

    /// <summary>FIX review 2026-09-25 (finding Major): finché Gamme non è mai stata aperta, GammeDisponibili/
    /// TitoloGamma leggono il catalogo Gamma direttamente (vedi sopra) invece che dal nested — che prima era
    /// l'unico a notificare un refresh (il suo CollectionChanged girava a ogni DatiRicaricati tramite
    /// OnDatiRicaricati della base). Senza questo override, una gamma aggiunta/rinominata da un'altra
    /// postazione (o un refresh periodico/forzato) non aggiornava più la colonna "Gamma" della griglia
    /// Veicoli finché l'utente non apriva l'Expander almeno una volta. _titoloGammaCache va invalidata insieme,
    /// altrimenti resterebbe l'etichetta letta alla prima apertura pagina anche dopo che un Master rinomina il
    /// ruolo di sistema "Gamma" da un'altra postazione.</summary>
    protected override void OnDatiRicaricati()
    {
        base.OnDatiRicaricati();
        // La lista dei Veicoli non è paginata: la base non la rilegge. Qui si riallinea dopo un salvataggio dalla finestra
        // "Componente e funzionalità" (o da un'altra postazione), senza toccare una modifica in corso.
        RiallineaListaNonPaginata();
        if (_gamme == null)
        {
            _titoloGammaCache = null;
            _gammeDisponibiliLeggere = null;
            OnPropertyChanged(nameof(GammeDisponibili));
            OnPropertyChanged(nameof(TitoloGamma));
        }
    }

    /// <summary>Stessa eccezione mirata di FunzionalitaViewModel/EcuViewModel (feedback utente 2026-09-18:
    /// "possibilità di aggiungere i campi di sistema tipo componente/veicoli ecc anche direttamente dal test... la
    /// possibilità viene data sia ad utente master che standard"): uno Standard può aggiungere nuovi Veicoli,
    /// non modificare quelli esistenti — puo' anche archiviare/eliminare quelli che ha creato lui stesso
    /// (PuoEliminareRiga nella base, non un rilassamento qui).</summary>
    public override bool PuoCreare => PuoModificare || (StatoApp.RuoloAttivo == RuoloOperatore.Standard && StatoApp.PuoScrivere);

    /// <summary>Ordine posizionale import Excel (feedback utente 2026-08-20, punto 8 — l'esempio esplicito
    /// dell'utente: colonna A dell'Excel = Nome, B = Gamma, esattamente come mostrato a schermo). Prima
    /// richiedeva un'intestazione "GammaNome" (nome della proprietà C#), non "Gamma" come mostrato a schermo:
    /// causa più probabile del "non funziona" segnalato.</summary>
    protected override IReadOnlyList<string> ColonneImportOrdine => new[] { nameof(Veicolo.Nome), nameof(Veicolo.GammaNome) };

    /// <summary>Le Gamme sono annidate qui dentro (vedi Gamme sopra): le loro modifiche non salvate contano
    /// come modifiche non salvate di questa pagina, altrimenti passare via da Veicoli le scarterebbe senza
    /// nessun avviso (Addendum redesign 2026-08, revisione feedback QA).</summary>
    public override bool HaModificheNonSalvate() => base.HaModificheNonSalvate() || (_gamme?.HaModificheNonSalvate() ?? false);

    /// <summary>Gamme è un ViewModel annidato indipendente (propria sottoscrizione a DatiRicaricati):
    /// disporre solo questo senza anche lui lascerebbe quella sottoscrizione agganciata per sempre. Null-safe:
    /// se l'Expander non è mai stato aperto, il nested non esiste e non ha nulla da disporre.</summary>
    public override void Dispose() { _gamme?.Dispose(); base.Dispose(); }

    // Una restrizione per componente che cita il veicolo blocca l'eliminazione anche se il veicolo non è più montato sul componente: la voce
    // residua è inerte per le letture (AssociazioniVeicoloService.ValeSuVeicoloMontato) ma resta un riferimento nei dati.
    protected override bool HaRiferimenti(Veicolo item, IReadOnlyList<TestCase> testCases) =>
        testCases.Any(tc => tc.VeicoliEffettivi.Contains(item.Nome, StringComparer.OrdinalIgnoreCase))
        || StatoApp.Master.Componente.Load().Any(e => e.VeicoliAssociati.Contains(item.Nome, StringComparer.OrdinalIgnoreCase))
        || StatoApp.Master.Funzionalita.Load().Any(f => f.VeicoliPerEcu.Any(v => v.Veicoli.Contains(item.Nome, StringComparer.OrdinalIgnoreCase)))
        || StatoApp.Master.Requisiti.Load().Any(r => RequisitoApplicabilita.RiferisceVeicolo(r, item.Nome));

    /// <summary>Finestra "Componente e funzionalità" del veicolo: vista dal veicolo sulle associazioni componente e Funzionalità, con lo stato
    /// dell'applicazione condiviso con questa pagina (ruolo, cartella, lock).</summary>
    public AssociazioniVeicoloViewModel CreaAssociazioniVeicolo(string veicolo) => new(StatoApp, veicolo);
}

public partial class StrumentiViewModel : MasterDataEditorViewModelBase<StrumentoDiagnostico>
{
    public StrumentiViewModel(AppState state) : base(state, state.Master.Strumenti, state.Paths.StrumentiFile, s => s.Nome) { }

    /// <summary>Stessa eccezione mirata di FunzionalitaViewModel/EcuViewModel/VeicoliViewModel (feedback
    /// utente 2026-09-18): uno Standard può aggiungere nuovi Strumenti, non modificare quelli esistenti — può
    /// anche archiviare/eliminare quelli che ha creato lui stesso. La configurazione tecnica dei Campi di uno
    /// strumento resta comunque riservata a un Master (TestCaseEditorViewModel.PuoGestireStrumenti, invariato):
    /// qui si parla solo del nome dello strumento nel catalogo, non dei suoi campi.</summary>
    public override bool PuoCreare => PuoModificare || (StatoApp.RuoloAttivo == RuoloOperatore.Standard && StatoApp.PuoScrivere);

    /// <summary>Ordine posizionale import Excel (feedback utente 2026-08-20, punto 8): solo Nome è una colonna
    /// "semplice" a schermo — Campi è una struttura annidata (gestita da "Gestisci campi..."), non importabile
    /// da una singola cella Excel.</summary>
    protected override IReadOnlyList<string> ColonneImportOrdine => new[] { nameof(StrumentoDiagnostico.Nome) };

    protected override bool HaRiferimenti(StrumentoDiagnostico item, IReadOnlyList<TestCase> testCases) =>
        testCases.Any(tc => tc.StrumentiUsati.Contains(item.Nome));

    /// <summary>Ordine mostrato in questa tabella E nel modulo Test (spec riordino campi 2026-09-23 §2,
    /// seguito): sopra l'ordine alfabetico naturale già calcolato dalla base (che resta la base stabile per
    /// gli strumenti con Ordine ancora a zero, mai riordinati esplicitamente) applica un ordinamento stabile
    /// per StrumentoDiagnostico.Ordine, stesso principio di CampoOrdinamentoService.OrdinePerVisualizzazione
    /// già usato per i campi.</summary>
    protected override IEnumerable<StrumentoDiagnostico> OrdinaPerVisualizzazione(IEnumerable<StrumentoDiagnostico> ordinatoPerChiave) =>
        CampoOrdinamentoService.OrdinePerVisualizzazione(ordinatoPerChiave);

    /// <summary>Sposta uno strumento su/giù nell'ordine mostrato in questa tabella e nel modulo Test (elenco
    /// "Strumenti usati" e sezioni dei suoi campi) — stesso principio già usato per i campi dei Test Template/
    /// Strumenti (CampoOrdinamentoService), qui applicato all'elenco degli strumenti stessi. Opera su
    /// ItemsAttivi (la vista già ordinata mostrata in tabella, non Items grezzo): gli elementi condivisi per
    /// riferimento con Items ricevono comunque l'Ordine aggiornato, persistito al prossimo Salva; un elemento
    /// archiviato o fuori dal filtro di ricerca corrente non è toccato (mantiene il proprio Ordine relativo).</summary>
    [RelayCommand]
    private void SpostaSuStrumento(StrumentoDiagnostico? item)
    {
        if (!InModifica || item == null) return;
        if (CampoOrdinamentoService.SpostaSu(ItemsAttivi, item)) RicalcolaItemsAttivi();
    }

    [RelayCommand]
    private void SpostaGiuStrumento(StrumentoDiagnostico? item)
    {
        if (!InModifica || item == null) return;
        if (CampoOrdinamentoService.SpostaGiu(ItemsAttivi, item)) RicalcolaItemsAttivi();
    }
}

public class TestTemplateViewModel : MasterDataEditorViewModelBase<TestTemplate>
{
    /// <summary>Oggetti completi (non solo i nomi, Addendum: collaudo live 2026-08-17): l'editor del contenuto
    /// del template ne legge anche i Campi, per offrire un menu a tendina Strumento→Campo invece di testo
    /// libero "Strumento::Campo" nelle colonne del report.</summary>
    public List<StrumentoDiagnostico> StrumentiDisponibili { get; }

    /// <summary>Esposto pubblicamente (Addendum: grill-me 2026-08-18, follow-up — "sposta tutto dentro Tipi
    /// di test"): TestTemplateEditorWindow.Apri ne ha bisogno per caricare/creare voci del catalogo campi di
    /// sistema (Pianificazione/Dettagli) direttamente dal "Gestisci campi", senza più passare da liste
    /// precaricate qui che si sarebbero disallineate a ogni creazione.</summary>
    public AppState Stato => StatoApp;

    public TestTemplateViewModel(AppState state) : base(state, state.Master.TestTemplate, state.Paths.TestTemplateFile, t => t.Uid.ToString())
    {
        StrumentiDisponibili = state.Master.Strumenti.Load().Where(s => !s.Archiviata).ToList();
    }

    /// <summary>Genera automaticamente un foglio report di default per ogni template che ne è ancora privo
    /// (feedback utente 2026-08-20, punto 23): supera la scelta del 17/08 "sempre configurazione manuale" —
    /// un Master può ancora personalizzare/riordinare le colonne dopo con l'editor esistente, ma non parte più
    /// da un foglio vuoto. Non tocca template che hanno già almeno un foglio (manuale o generato prima).</summary>
    protected override bool ValidaPrimaDiSalvare()
    {
        foreach (var template in Items) FoglioReportAutoGenService.GeneraSeAssente(template);
        return true;
    }

    protected override bool HaRiferimenti(TestTemplate item, IReadOnlyList<TestCase> testCases) =>
        testCases.Any(tc => tc.TestTemplateUid == item.Uid);
}

/// <summary>Editor del catalogo campi di sistema (Addendum: grill-me 2026-08-18, punto 1) — una sezione per
/// Pianificazione e una per Dettagli (due file separati, vedi DataPaths.CampiSistemaPianificazioneFile/
/// CampiSistemaDettagliFile), stesso schema di ogni altro editor master data. Righe con Ruolo diverso da Nessuno
/// sono campi "di ruolo" già usati da Coverage/dashboard/import: la Chiave non è mai editabile da tabella
/// (è la colonna sola-lettura "Chiave") e un cambio di Tipo su un campo con dati già salvati viene bloccato
/// al salvataggio da CampoSistemaTipoChangeGuard, non silenziosamente accettato.</summary>
public class CampoSistemaDefinizioneViewModel : MasterDataEditorViewModelBase<CampoSistemaDefinizione>
{
    public Array Tipi => Enum.GetValues(typeof(TipoCampoStrumento));
    public string Titolo { get; }

    public CampoSistemaDefinizioneViewModel(AppState state, GruppoCampoSistema gruppo)
        : base(state, gruppo == GruppoCampoSistema.Pianificazione ? state.Master.CampiSistemaPianificazione : state.Master.CampiSistemaDettagli,
               gruppo == GruppoCampoSistema.Pianificazione ? state.Paths.CampiSistemaPianificazioneFile : state.Paths.CampiSistemaDettagliFile,
               c => c.Uid.ToString())
    {
        Titolo = LocalizationService.Instance.Traduci(gruppo == GruppoCampoSistema.Pianificazione ? "TplPianificazioneBreve" : "DettagliTitolo");
        RigaCreata += voce =>
        {
            // Chiave assegnata più avanti, in ValidaPrimaDiSalvare (Addendum: grill-me 2026-08-18, terza
            // follow-up, punto 6): a questo punto l'Etichetta è ancora il placeholder sotto, il Master la
            // sovrascrive modificando la riga in tabella — generare la Chiave da un'Etichetta non ancora
            // reale produrrebbe una chiave insensata.
            voce.Gruppo = gruppo;
            voce.Etichetta = LocalizationService.Instance.Traduci("CampoSistemaNuovaEtichettaDefault");
        };
    }

    /// <summary>Creazione rapida dal dialogo "+ Crea nuovo campo..." dell'editor Tipi di test: stesso percorso
    /// Modifica→Nuovo→Salva dell'editor del catalogo (lock e changelog), con la spunta "Usabile come filtro" che ha lo
    /// stesso significato della colonna Filtro (default off). Restituisce la voce salvata e l'eventuale errore
    /// (lock di un altro operatore o errore di salvataggio).</summary>
    public async Task<(CampoSistemaDefinizione? Voce, string? Errore)> CreaNuovoRapidoAsync(string etichetta, TipoCampoStrumento tipo, bool usabileComeFiltro)
    {
        using (AttivaContestoCreazioneRapidaLockTest())
            await IniziaModificaCommand.ExecuteAsync(null);
        if (!InModifica)
        {
            return (null, LockInfoTesto);
        }

        CampoSistemaDefinizione? nuovaVoce = null;
        RigaCreata += v => nuovaVoce = v;
        NuovoCommand.Execute(null);
        if (nuovaVoce == null) return (null, null);

        nuovaVoce.Etichetta = etichetta;
        nuovaVoce.Tipo = tipo;
        nuovaVoce.UsabileComeFiltro = usabileComeFiltro;
        await SalvaCommand.ExecuteAsync(null);
        if (RilascioSessioneLockInCorso is { } rilascio)
            await rilascio;
        if (HaMessaggioErrore || RilascioLockInAttesa)
        {
            return (null, MessaggioErrore ?? LocalizationService.Instance.Traduci("SalvatoSbloccoLockNonRiuscito"));
        }
        return (nuovaVoce, null);
    }

    /// <summary>Applica le opzioni di un campo di tipo Lista scelte nell'editor "Modifica lista" (solo in modifica).
    /// La modifica entra nella riga come le altre: una voce di changelog e Ultima modifica/Da aggiornate subito, e il
    /// confronto di Salva (JSON completo, OpzioniLista incluso) la rileva come da salvare. Restituisce false, senza
    /// toccare la riga, se la lista non cambia (stesse voci nello stesso ordine).</summary>
    public bool ImpostaOpzioniLista(CampoSistemaDefinizione campo, IReadOnlyList<string> nuove)
    {
        if (!InModifica || campo.OpzioniLista.SequenceEqual(nuove)) return false;

        var precedenti = campo.OpzioniLista;
        campo.OpzioniLista = nuove.ToList();
        var operatore = StatoApp.Config.OperatoreAttivo;
        campo.Changelog.Add(new ChangelogEntry
        {
            Data = DateTime.Now,
            Operatore = operatore,
            Campo = nameof(CampoSistemaDefinizione.OpzioniLista),
            ValorePrecedente = string.Join("; ", precedenti),
            ValoreNuovo = string.Join("; ", nuove),
        });
        campo.UltimaModifica = DateTime.Now;
        campo.ModificatoDa = operatore;
        return true;
    }

    /// <summary>Blocca il salvataggio se il Tipo di un campo con Ruolo diverso da Nessuno è stato cambiato
    /// (per loro il Tipo non ha comunque effetto visibile, vedi Addendum — non dovrebbe essere possibile
    /// modificarlo dalla UI, ma questo è il guard di sicurezza se succede comunque), o se il Tipo di un campo
    /// libero con dati già salvati sui TestCase è stato cambiato. Assegna anche la Chiave dei campi nuovi
    /// (Addendum: grill-me 2026-08-18, terza follow-up, punto 6): derivata dall'Etichetta finale digitata dal
    /// Master, univoca su ENTRAMBI i gruppi (condividono lo stesso TestCase.CampiCustom).</summary>
    protected override bool ValidaPrimaDiSalvare()
    {
        var testCases = StatoApp.CartellaValida ? StatoApp.TestCases.GetAll() : new List<TestCase>();
        foreach (var voce in Items)
        {
            var prima = Snapshot.FirstOrDefault(s => ChiaveDi(s) == ChiaveDi(voce));
            if (prima == null || prima.Tipo == voce.Tipo) continue;

            if (voce.Ruolo != RuoloCampoSistema.Nessuno)
            {
                MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("CampoSistemaTipoRuoloBloccatoFmt"), voce.Etichetta);
                return false;
            }
            if (CampoSistemaTipoChangeGuard.HaDatiEsistenti(prima, testCases))
            {
                MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("CampoSistemaTipoDatiEsistentiFmt"), voce.Etichetta);
                return false;
            }
        }

        var chiaviEsistenti = new HashSet<string>(
            StatoApp.Master.CampiSistemaPianificazione.Load().Select(c => c.Chiave)
                .Concat(StatoApp.Master.CampiSistemaDettagli.Load().Select(c => c.Chiave)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var voce in Items)
        {
            // Solo le righe senza Chiave ancora assegnata (appena create da Nuovo(), mai toccate): una riga
            // che arriva già con una Chiave (importata da Excel, o assegnata a mano in un test) non va
            // rigenerata, altrimenti smetterebbe di corrispondere ai dati TestCase.CampiCustom già salvati
            // sotto quella chiave.
            if (!string.IsNullOrWhiteSpace(voce.Chiave)) continue;

            if (string.IsNullOrWhiteSpace(voce.Etichetta))
            {
                MessaggioErrore = LocalizationService.Instance.Traduci("CampoSistemaEtichettaObbligatoria");
                return false;
            }
            var chiave = CampoSistemaCatalogoService.GeneraChiaveDaEtichetta(voce.Etichetta, chiaviEsistenti);
            voce.Chiave = chiave;
            chiaviEsistenti.Add(chiave);
        }
        return true;
    }

    protected override bool HaRiferimenti(CampoSistemaDefinizione item, IReadOnlyList<TestCase> testCases) =>
        CampoSistemaTipoChangeGuard.HaDatiEsistenti(item, testCases);

    /// <summary>Dopo un salvataggio riuscito del catalogo le schede Test già aperte rileggono opzioni e tipi dei campi:
    /// il catalogo che usano è una copia presa alla loro apertura, e questo editor è il punto da cui cambia. Un errore
    /// di notifica viene solo registrato: il salvataggio è già avvenuto.</summary>
    protected override void DopoPersistenza(List<CampoSistemaDefinizione> salvate, IReadOnlyList<CampoSistemaDefinizione> snapshotPrecedente)
    {
        try { StatoApp.NotificaDatiRicaricati(); }
        catch (Exception ex) { CrashLogService.Registra("Notifica dati dopo salvataggio catalogo campi di sistema", ex); }
    }
}

public partial class OperatoriViewModel : MasterDataEditorViewModelBase<Operatore>
{
    public List<string> GammeDisponibili { get; }
    public Array Ruoli => Enum.GetValues(typeof(RuoloOperatore));

    /// <summary>PIN e chiave di recupero in chiaro (PinChiaro/RecoveryCodeChiaro) sono dati sensibili di
    /// TUTTI gli operatori: solo un Master deve poterli leggere, non Standard/Viewer che possono comunque
    /// aprire questa pagina in sola lettura per vedere ruolo/gamme altrui (bug scoperto in collaudo
    /// 2026-08-24 — la colonna PIN/chiave era visibile a qualunque ruolo, non solo a chi può modificarla).</summary>
    public bool EMaster => StatoApp.EMaster;

    public OperatoriViewModel(AppState state) : base(state, state.Master.Operatori, state.Paths.OperatoriFile, o => o.Nome)
    {
        GammeDisponibili = state.Master.Gamma.Load().Where(g => !g.Archiviata).Select(g => g.Nome).ToList();
    }

    /// <summary>Rivaluta anche EMaster al giro di ricarica dati, non solo PuoModificare/PuoCreare (base):
    /// senza, un Master declassato a Standard mentre questa pagina resta aperta continuerebbe a vedere
    /// PIN/chiave di recupero altrui in chiaro finché non ri-naviga altrove e torna indietro.</summary>
    protected override void OnDatiRicaricati()
    {
        base.OnDatiRicaricati();
        OnPropertyChanged(nameof(EMaster));
        OnPropertyChanged(nameof(OperatoriConRuoloNonSigillato));
        OnPropertyChanged(nameof(HaOperatoriConRuoloNonSigillato));
        OnPropertyChanged(nameof(OperatoriConRuoloNonSigillatoTesto));
    }

    /// <summary>Avviso per i Master (piano 6.4.2, §4b, punto 2: "sigillo assente o non valido... i Master
    /// vedono un avviso"): elenco degli operatori il cui Ruolo persistito non è (più) protetto da un sigillo
    /// valido — valgono già come Viewer (Operatore.RuoloEffettivo, punto unico), questo è solo il segnale
    /// visibile che qualcosa è stato modificato fuori dall'app.</summary>
    public List<string> OperatoriConRuoloNonSigillato => Items.Where(o => !o.SigilloRuoloValido).Select(o => o.Nome).ToList();
    public bool HaOperatoriConRuoloNonSigillato => OperatoriConRuoloNonSigillato.Count > 0;
    public string OperatoriConRuoloNonSigillatoTesto => string.Format(
        LocalizationService.Instance.Traduci("OperatoriRuoloManomessoElencoFmt"), string.Join(", ", OperatoriConRuoloNonSigillato));

    /// <summary>Imposta il PIN di un operatore (feedback utente 2026-08-20, punto 2): se il Master ha
    /// digitato un valore in NuovoPinTemp lo usa così com'è (può scegliere lui il PIN, sia alla creazione sia
    /// nei reset successivi), altrimenti ne genera uno casuale per comodità. Il PIN resta sempre visibile in
    /// chiaro (PinChiaro) oltre che come hash. La chiave di recupero viene generata solo se l'operatore non ne
    /// ha ancora una fissa — un reset PIN non la tocca più (si rigenera solo con RigeneraChiaveRecupero).</summary>
    [RelayCommand]
    private void ImpostaPin(Operatore? operatore)
    {
        if (operatore == null) return;
        var pin = string.IsNullOrWhiteSpace(operatore.NuovoPinTemp) ? PinService.GeneraPin() : operatore.NuovoPinTemp.Trim();
        operatore.PinHash = PinService.Hash(pin);
        operatore.PinChiaro = pin;
        operatore.NuovoPinTemp = "";
        if (string.IsNullOrEmpty(operatore.RecoveryCodeChiaro))
        {
            var codice = PinService.GeneraCodiceRecupero();
            operatore.RecoveryCodeHash = PinService.Hash(codice);
            operatore.RecoveryCodeChiaro = codice;
        }
    }

    /// <summary>Rigenera solo la chiave di recupero, azione esplicita e separata dal reset PIN (feedback
    /// utente 2026-08-20, punto 2 — la chiave è fissa per operatore finché non la si rigenera di proposito).</summary>
    [RelayCommand]
    private void RigeneraChiaveRecupero(Operatore? operatore)
    {
        if (operatore == null) return;
        var codice = PinService.GeneraCodiceRecupero();
        operatore.RecoveryCodeHash = PinService.Hash(codice);
        operatore.RecoveryCodeChiaro = codice;
    }

    /// <summary>Ricalcola il sigillo del ruolo di OGNI riga ad ogni Salva (piano 6.4.2, §4b, punto 2: "Solo un
    /// Master dall'app cambia un ruolo: il salvataggio ricalcola il sigillo"). Risigillare anche le righe con
    /// ruolo invariato è idempotente e voluto: riporta a "sigillo valido" un operatore che era stato segnalato
    /// come manomesso (OperatoriConRuoloNonSigillato), visto che questo salvataggio passa comunque dall'app ed
    /// è quindi autorevole. Un evento audit viene scritto solo per le righe il cui Ruolo è realmente cambiato
    /// rispetto a quanto persistito, confrontando con la fotografia su disco (non con lo snapshot locale, che
    /// non distingue "riga toccata ma ruolo invariato" da "ruolo cambiato").</summary>
    /// <summary>Unicità del Nome (case-insensitive, su TUTTI gli operatori inclusi i Disattivo — revisione
    /// piano 6.4.2, §4b, punto 2): il Nome è la chiave con cui AppState.ImpostaOperatore/
    /// RiallineaOperatoreCorrente identificano l'operatore attivo e con cui TestoRuolo lega il sigillo
    /// all'identità; due operatori con lo stesso Nome (differenza solo di maiuscole/minuscole) sarebbero
    /// indistinguibili per quella logica. Inclusi i Disattivo apposta: un Nome "liberato" disattivando la riga
    /// e poi riusato da un nuovo operatore attivo creerebbe la stessa ambiguità, e riattivare un Disattivo
    /// resta un'operazione più rara di crearne uno nuovo — l'unicità totale è la scelta più prudente.</summary>
    private bool NomiDuplicati()
    {
        var duplicato = Items
            .GroupBy(o => o.Nome.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1 && !string.IsNullOrWhiteSpace(g.Key));
        if (duplicato == null) return false;
        MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("OperatoreNomeDuplicatoFmt"), duplicato.Key);
        return true;
    }

    protected override bool ValidaPrimaDiSalvare()
    {
        if (NomiDuplicati()) return false;
        var persistiti = StatoApp.CartellaValida
            ? StatoApp.Master.Operatori.Load().ToDictionary(o => o.Nome, o => o.Ruolo, StringComparer.Ordinal)
            : new Dictionary<string, RuoloOperatore>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            RuoloSigilloService.Sigilla(item);
            if (persistiti.TryGetValue(item.Nome, out var ruoloPrecedente) && ruoloPrecedente != item.Ruolo)
                StatoApp.RegistraAudit("CambioRuoloOperatore", item.Nome, $"{ruoloPrecedente} -> {item.Ruolo}");
        }
        return true;
    }

    protected override bool HaRiferimenti(Operatore item, IReadOnlyList<TestCase> testCases) =>
        testCases.Any(tc => tc.Assegnatario == item.Nome || tc.CreatoDa == item.Nome || tc.Esecuzioni.Any(e => e.Operatore == item.Nome));
}
