using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

public sealed class CampoCrRiga : ViewModelBase
{
    private readonly Dictionary<string, string> _values;
    private readonly Action _onChanged;
    public CampoTemplate Campo { get; }
    public string Nome => Campo.Nome;
    public bool ECheckbox => Campo.TipoLibero == TipoCampoStrumento.Checkbox;
    public bool ELista => Campo.TipoLibero == TipoCampoStrumento.Lista;
    public bool ETesto => !ECheckbox && !ELista;
    public List<string> Opzioni => Campo.OpzioniListaLibero;
    public bool Obbligatorio { get; init; }
    public string Valore { get => _values.GetValueOrDefault(Campo.Uid.ToString()) ?? ""; set { if (value == Valore) return; if (string.IsNullOrWhiteSpace(value)) _values.Remove(Campo.Uid.ToString()); else _values[Campo.Uid.ToString()] = value; OnPropertyChanged(); OnPropertyChanged(nameof(ValoreCheckbox)); _onChanged(); } }
    public bool ValoreCheckbox { get => Valore == "true"; set => Valore = value ? "true" : "false"; }
    public CampoCrRiga(CampoTemplate campo, Dictionary<string, string> values, bool obbligatorio, Action onChanged) { Campo = campo; _values = values; Obbligatorio = obbligatorio; _onChanged = onChanged; }
}

/// <summary>Gruppo di veicoli per gamma nella visualizzazione dell'Ambito impattato (punto 4, feedback utente
/// 2026-08-21) — vedi ChangeRequestEditorViewModel.VeicoliRaggruppati per come viene calcolato.</summary>
public sealed class GruppoVeicoli
{
    public string Etichetta { get; }
    public IReadOnlyList<string> Veicoli { get; }
    public bool Espanso { get; }
    public string EtichettaConteggio => $"{Etichetta} ({Veicoli.Count})";
    public GruppoVeicoli(string etichetta, IReadOnlyList<string> veicoli, bool espanso) { Etichetta = etichetta; Veicoli = veicoli; Espanso = espanso; }
}

public partial class ChangeRequestEditorViewModel : ViewModelBase, IScorciatoiePagina
{
    private readonly AppState _state;
    private readonly Action _torna;
    private readonly Action<string?> _apriTest;
    private readonly ChangeRequest? _snapshot;
    private ChangeRequest? _snapshotPersistitoCorrente;
    private long? _revisioneAttesa;
    private bool _inizializzazione = true;
    private StatoChangeRequest _statoSelezionato = StatoChangeRequest.Aperta;
    private bool _statoSelezioneManuale;
    private string? _messaggioDopoRicalcolo;

    [ObservableProperty] private ChangeRequest changeRequest;
    [ObservableProperty] private TestTemplate? templateSelezionato;
    [ObservableProperty] private TestCase? testDaCollegare;
    [ObservableProperty] private TestTemplate? templateTestDaCreare;
    [ObservableProperty] private bool testCreatoObbligatorio = true;
    [ObservableProperty] private string messaggio = "";
    [ObservableProperty] private string errore = "";
    [ObservableProperty] private bool modificheNonSalvate;
    [ObservableProperty] private string nuovoTicketRiferimento = "";
    [ObservableProperty] private string nuovoTicketUrl = "";

    private readonly HashSet<string> _gammeEspanse = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool? _forzaBackground;
    private readonly Action? _gateCaricamentoTest;
    private Task _caricamentoTestDisponibili = Task.CompletedTask;
    private bool InBackground => _forzaBackground ?? Avalonia.Application.Current != null;

    // Giro di caricamento (cataloghi e copertura), come HomeViewModel.Giro: cattura sul thread UI, letture su pool dentro
    // MasterLetturaScope, applicazione sul thread UI solo se il giro e' ancora attuale. Il salvataggio non usa mai una
    // copertura superata: se un giro e' ancora in attesa, lo completa in linea sul valore attuale prima di salvare.
    private int _generazioneGiro;
    private bool _giroInAttesa;
    private bool _giroIniziale;
    private bool _cataloghiCaricati;
    private Task _giro = Task.CompletedTask;

    /// <summary>Vero mentre cataloghi o copertura sono in calcolo: l'editor resta utilizzabile, la vista lo segnala.</summary>
    [ObservableProperty] private bool caricamentoInCorso;

    /// <summary>Risultati di un giro superato da uno piu' recente o dal salvataggio (contatore per i test).</summary>
    internal int GiriScartati { get; private set; }

    /// <summary>Ultima eccezione del giro in background (registrata anche nel log di crash).</summary>
    internal Exception? UltimaEccezioneCaricamento { get; private set; }

    /// <summary>Completamento dell'ultimo giro avviato (cataloghi e copertura applicati o errore registrato).</summary>
    internal Task AttendiGiroAsync() => _giro;

    /// <summary>Completamento del caricamento dei Test candidati (elenco e sorgente del selettore applicati, o errore registrato).</summary>
    internal Task AttendiCaricamentoTestDisponibiliAsync() => _caricamentoTestDisponibili;

    /// <summary>Avvia il calcolo dei Test candidati: in background (con la sorgente del selettore) in UI reale, in linea altrimenti.</summary>
    private void AvviaCaricamentoTestDisponibili()
    {
        if (!InBackground)
        {
            var elenco = ElencoTestCandidati();
            ApplicaTestDisponibili(elenco, SorgenteSelettoreTest(elenco));
            return;
        }
        _caricamentoTestDisponibili = Task.Run(async () =>
        {
            List<TestCase>? elenco = null;
            PickerMotore<TestCase>? picker = null;
            Exception? errore = null;
            try
            {
                elenco = ElencoTestCandidati();
                picker = SorgenteSelettoreTest(elenco);
                _gateCaricamentoTest?.Invoke();
                await picker.PreparaAsync();
            }
            catch (Exception ex) { errore = ex; }
            await SuThreadUi(() =>
            {
                if (errore != null)
                {
                    CrashLogService.Registra("Change request: test candidati", errore);
                    return;
                }
                ApplicaTestDisponibili(elenco!, picker!);
            });
        });
    }

    /// <summary>Test candidati: non eliminati, dal piu' recente (stesso ordine di prima).</summary>
    private List<TestCase> ElencoTestCandidati() =>
        _state.TestCases.GetAll().Where(t => !t.Eliminato).OrderByDescending(t => t.DataCreazione).ToList();

    private static PickerMotore<TestCase> SorgenteSelettoreTest(List<TestCase> elenco) => new(elenco, ChiaveRicercaTest);

    /// <summary>Testo su cui il selettore cerca un Test: stessi campi della scelta testuale di <see cref="ScegliTestValidazione"/>.</summary>
    private static string ChiaveRicercaTest(TestCase t) => $"{t.Id} {t.DescrizioneBreve} {t.Componente}";

    private void ApplicaTestDisponibili(List<TestCase> elenco, PickerMotore<TestCase> picker)
    {
        TestDisponibili = elenco;
        PickerTestDisponibili = picker;
        OnPropertyChanged(nameof(TestDisponibili));
        OnPropertyChanged(nameof(PickerTestDisponibili));
    }

    private static Task SuThreadUi(Action azione)
    {
        if (Avalonia.Application.Current == null) { azione(); return Task.CompletedTask; }
        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(azione).GetTask();
    }

    public ObservableCollection<string> Veicoli { get; } = new();
    public ObservableCollection<string> Componente { get; } = new();
    public ObservableCollection<string> Strumenti { get; } = new();
    public ObservableCollection<CollegamentoValidazioneCr> TestValidazione { get; } = new();
    public ObservableCollection<CampoCrRiga> CampiCr { get; } = new();
    public ObservableCollection<Allegato> AllegatiVisual { get; } = new();
    public ObservableCollection<TicketRif> TicketCollegatiVisual { get; } = new();

    /// <summary>Campi obbligatori/regole mancanti dall'ultima validazione (punto 8, feedback utente
    /// 2026-08-21): stesso record CampoMancante già usato per il Test, così CampiMancantiDialog è riusabile
    /// senza modifiche. La Chiave individua la card su cui saltare (vedi ChangeRequestEditorView.VaiAlCampo),
    /// non un singolo controllo pixel-preciso — scelta esplicita dell'utente per le regole trasversali (Ambito
    /// mancante, motivazione duplicato) che non hanno un solo campo su cui puntare.</summary>
    public List<CampoMancante> CampiMancanti { get; private set; } = new();
    public bool MotivazioneDuplicatoRichiesta { get; private set; }
    public event Action? RichiestaMostraCampiMancanti;
    // Cataloghi: vuoti finche' il giro di caricamento non li applica (vedi AvviaGiro).
    public List<TestTemplate> TemplateCr { get; private set; } = new();
    public List<TestTemplate> TemplateTest { get; private set; } = new();
    public List<string> VeicoliDisponibili { get; private set; } = new();

    private List<Componente> _tutteLeEcu = new();

    /// <summary>Ricalcolata ad ogni lettura invece che una volta sola in costruzione (feedback utente
    /// 2026-08-24): "Seleziona tutto" nel picker componente deve proporre solo i componenti montate sui veicoli già
    /// scelti per la CR, non l'intero catalogo — stesso filtro già usato da AuditMatriceService.EcuDelSottoveicolo
    /// per l'audit, qui esteso ai (possibili) più veicoli impattati dalla CR. Nessun veicolo ancora scelto:
    /// non c'è nulla su cui filtrare, si ripiega sul catalogo intero (comportamento precedente).</summary>
    public List<string> EcuDisponibili => Veicoli.Count == 0
        ? _tutteLeEcu.Where(e => !e.Archiviata).Select(e => e.Nome).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
        : Veicoli.SelectMany(v => AuditMatriceService.EcuDelSottoveicolo(_tutteLeEcu, v))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    public List<string> StrumentiDisponibili { get; private set; } = new();
    public List<string> GammeDisponibili { get; private set; } = new();
    /// <summary>Test candidati alla CR (non eliminati, dal piu' recente): calcolati in background in UI reale e pubblicati sul
    /// thread UI quando sono pronti, cosi' l'editor si apre subito anche con molti Test. Vuoto finche' il calcolo non e' applicato.</summary>
    public List<TestCase> TestDisponibili { get; private set; } = new();

    /// <summary>Sorgente con ricerca per il selettore dei Test (PickerMotore): stessi elementi di <see cref="TestDisponibili"/>, con le
    /// chiavi di ricerca preparate fuori dal thread UI. Null finche' l'elenco non e' pronto.</summary>
    public PickerMotore<TestCase>? PickerTestDisponibili { get; private set; }
    public List<string> OperatoriDisponibili { get; private set; } = new();
    public bool IsNew => string.IsNullOrWhiteSpace(ChangeRequest.Id);
    public bool PuoRecuperareBozza => !IsNew && PuoModificare && _snapshot != null &&
        _revisioneAttesa.HasValue && !ModificheNonSalvate;

    /// <summary>Editor di una CR mai salvata (ID vuoto, nessuna fotografia iniziale): puo importare una bozza locale senza base.</summary>
    public bool PuoRecuperareBozzaNuova => IsNew && PuoModificare && _snapshot == null && !ModificheNonSalvate;

    /// <summary>Pulsante "Recupera bozza locale…": vale sia per una CR esistente (con base) sia per una CR nuova.</summary>
    public bool PuoRecuperareBozzaLocale => PuoRecuperareBozza || PuoRecuperareBozzaNuova;
    public string WorkspaceRoot => _state.Paths.Root;
    public bool EMaster => _state.EMaster;
    private bool _inModifica = true;
    private Action? _entraInModifica;
    /// <summary>False quando l'editor e' aperto da "Apri": tutto in sola lettura finche' non si preme "Modifica"
    /// (che riapre un editor nuovo, riletto dall'archivio, in modifica). Le altre aperture partono in modifica.</summary>
    public bool InModifica
    {
        get => _inModifica;
        private set { if (_inModifica == value) return; _inModifica = value; OnPropertyChanged(); OnPropertyChanged(nameof(PuoEntrareInModifica)); OnPropertyChanged(nameof(MostraSolaLetturaHint)); }
    }
    public bool PuoEntrareInModifica => !InModifica && _state.PuoScrivere && !ChangeRequest.Archiviata
        && ChangeRequest.Stato != StatoChangeRequest.Annullata
        && (ChangeRequest.Stato != StatoChangeRequest.Chiusa || _state.EMaster);
    public bool MostraSolaLetturaHint => !InModifica;

    /// <summary>Apre l'editor in sola lettura; <paramref name="entraInModifica"/> naviga a un editor nuovo in modifica
    /// (senza callback il passaggio avviene sul posto).</summary>
    public void ImpostaSolaLetturaIniziale(Action? entraInModifica)
    {
        _entraInModifica = entraInModifica;
        InModifica = false;
        NotificaStato();
    }

    [RelayCommand]
    private void EntraInModifica()
    {
        if (!PuoEntrareInModifica) return;
        if (_entraInModifica != null) { _entraInModifica(); return; }
        InModifica = true;
        NotificaStato();
    }

    public bool PuoModificare => InModifica && _state.PuoScrivere && !ChangeRequest.Archiviata
        && ChangeRequest.Stato is not (StatoChangeRequest.Chiusa or StatoChangeRequest.Annullata);
    public bool PuoImplementare => PuoModificare && !string.IsNullOrWhiteSpace(ChangeRequest.Responsabile)
        && (_state.EMaster || ChangeRequest.Responsabile.Equals(_state.Config.OperatoreAttivo, StringComparison.OrdinalIgnoreCase));
    public bool PuoChiudere => InModifica && ChangeRequestChiusuraService.PuoChiudere(ChangeRequest, _state.PuoScrivere, _state.EMaster);
    public bool PuoRiaprire => InModifica && ChangeRequestChiusuraService.PuoRiaprire(ChangeRequest, _state.PuoScrivere, _state.EMaster);
    public bool PuoSalvare => PuoModificare || PuoChiudere || PuoRiaprire;
    public bool PuoSelezionareStato => PuoSalvare && StatiWorkflow.Count > 1;
    public IReadOnlyList<StatoChangeRequest> StatiWorkflow
    {
        get
        {
            var stati = new List<StatoChangeRequest> { ChangeRequest.Stato };
            if (PuoImplementare && !stati.Contains(StatoChangeRequest.Implementata)) stati.Add(StatoChangeRequest.Implementata);
            if (PuoChiudere && !stati.Contains(StatoChangeRequest.Chiusa)) stati.Add(StatoChangeRequest.Chiusa);
            if (PuoRiaprire && !stati.Contains(StatoChangeRequest.Aperta)) stati.Add(StatoChangeRequest.Aperta);
            return stati;
        }
    }
    public StatoChangeRequest StatoSelezionato
    {
        get => _statoSelezionato;
        set
        {
            if (_statoSelezionato == value) return;
            _statoSelezionato = value;
            _statoSelezioneManuale = value != ChangeRequest.Stato;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RichiedeMotivoTransizione));
            if (_statoSelezioneManuale)
            {
                Errore = "";
                Messaggio = "";
                ModificheNonSalvate = true;
            }
        }
    }
    private string _motivoTransizione = "";
    /// <summary>Motivo scritto richiesto per chiudere (sempre, anche da Validata) o per riaprire una CR.</summary>
    public string MotivoTransizione
    {
        get => _motivoTransizione;
        set { if (_motivoTransizione == value) return; _motivoTransizione = value; OnPropertyChanged(); }
    }
    private bool _conflittoRevisione;
    /// <summary>True se l'ultimo salvataggio e' fallito per conflitto di revisione (un altro PC ha modificato la CR):
    /// la Modifica rapida offre allora "Ricarica" invece di sovrascrivere.</summary>
    public bool ConflittoRevisione
    {
        get => _conflittoRevisione;
        private set { if (_conflittoRevisione == value) return; _conflittoRevisione = value; OnPropertyChanged(); }
    }
    public bool HaAzioniDiChiusura => PuoChiudere || PuoRiaprire;
    public bool RichiedeMotivoTransizione =>
        (StatoSelezionato == StatoChangeRequest.Chiusa && ChangeRequest.Stato != StatoChangeRequest.Chiusa)
        || (StatoSelezionato == StatoChangeRequest.Aperta && ChangeRequest.Stato == StatoChangeRequest.Chiusa);
    public bool PuoArchiviare => InModifica && _state.PuoScrivere && !IsNew && !ChangeRequest.Archiviata;
    private static string T(string chiave) => LocalizationService.Instance.Traduci(chiave);
    private static string TF(string chiave, params object[] args) => string.Format(LocalizationService.Instance.Traduci(chiave), args);

    public string TitoloPagina => IsNew ? T("CrEdNuova") : ChangeRequest.Id;
    public string StatoTesto => ChangeRequest.Stato switch
    {
        StatoChangeRequest.DaValidare => T("CrEdDaValidare"), _ => ChangeRequest.Stato.ToString()
    };
    /// <summary>Sola lettura: impostato una sola volta alla creazione (vedi ChangeRequest.CreatoDa), non è mai
    /// modificabile dall'editor come invece lo è Responsabile.</summary>
    public string CreatoDa => ChangeRequest.CreatoDa;
    public string Titolo { get => ChangeRequest.Titolo; set { if (value == ChangeRequest.Titolo) return; ChangeRequest.Titolo = value; OnPropertyChanged(); ModificheNonSalvate = true; } }
    public string DescrizioneBreve { get => ChangeRequest.DescrizioneBreve; set { if (value == ChangeRequest.DescrizioneBreve) return; ChangeRequest.DescrizioneBreve = value; OnPropertyChanged(); ModificheNonSalvate = true; } }
    public string Descrizione { get => ChangeRequest.Descrizione; set { if (value == ChangeRequest.Descrizione) return; ChangeRequest.Descrizione = value; OnPropertyChanged(); ModificheNonSalvate = true; } }
    public string Responsabile { get => ChangeRequest.Responsabile; set { if (value == ChangeRequest.Responsabile) return; ChangeRequest.Responsabile = value; OnPropertyChanged(); ModificheNonSalvate = true; NotificaStato(); } }
    public string MotivazioneDuplicato { get => ChangeRequest.MotivazioneDuplicato; set { if (value == ChangeRequest.MotivazioneDuplicato) return; ChangeRequest.MotivazioneDuplicato = value; OnPropertyChanged(); ModificheNonSalvate = true; } }

    public ChangeRequestEditorViewModel(AppState state, string? id, Action torna, Action<string?> apriTest)
        : this(state, id, torna, apriTest, forzaBackground: null, gateCaricamentoTest: null)
    {
    }

    /// <summary>Costruttore con le opzioni per i test: <paramref name="forzaBackground"/> forza o esclude il calcolo dei Test
    /// candidati in background; <paramref name="gateCaricamentoTest"/> (solo test) trattiene quel calcolo prima della pubblicazione.</summary>
    internal ChangeRequestEditorViewModel(AppState state, string? id, Action torna, Action<string?> apriTest,
        bool? forzaBackground, Action? gateCaricamentoTest)
    {
        _state = state;
        _torna = torna;
        _apriTest = apriTest;
        _forzaBackground = forzaBackground;
        _gateCaricamentoTest = gateCaricamentoTest;
        var existing = id == null ? null : state.ChangeRequests.GetById(id);
        ChangeRequest = existing == null ? new ChangeRequest { CreatoDa = state.Config.OperatoreAttivo } : Clona(existing);
        _statoSelezionato = ChangeRequest.Stato;
        _snapshot = existing == null ? null : Clona(existing);
        _snapshotPersistitoCorrente = existing == null ? null : Clona(existing);
        _revisioneAttesa = existing?.RevisioneDati;
        // Cataloghi (sette letture dei master in MasterLetturaScope, una per catalogo) e copertura non si calcolano piu' qui:
        // li calcola il giro di caricamento (AvviaGiro), in linea nei test e in background nella UI. I Test candidati non sono qui.
        AvviaCaricamentoTestDisponibili();
        foreach (var x in ChangeRequest.VeicoliImpattati) Veicoli.Add(x);
        foreach (var x in ChangeRequest.EcuImpattate) Componente.Add(x);
        foreach (var x in ChangeRequest.StrumentiImpattati) Strumenti.Add(x);
        foreach (var x in ChangeRequest.TestValidazione) TestValidazione.Add(x);
        foreach (var x in ChangeRequest.Allegati) AllegatiVisual.Add(x);
        foreach (var x in ChangeRequest.TicketCollegati) TicketCollegatiVisual.Add(x);
        // Ricalcola il raggruppamento per gamma (punto 4, feedback utente 2026-08-21) ad ogni aggiunta/rimozione
        // veicolo, senza dover toccare Aggiungi/Rimuovi generici usati anche da componente e Strumenti.
        Veicoli.CollectionChanged += (_, _) => OnPropertyChanged(nameof(VeicoliRaggruppati));
        AvviaGiro(iniziale: true);
        CaricaCampiCr();
        _inizializzazione = false;
        ModificheNonSalvate = false;
    }

    /// <summary>Testo del pulsante-picker sopra soglia (punto 1, feedback utente 2026-08-21): bindato diretto
    /// a una proprietà piatta invece di un percorso annidato tipo TemplateSelezionato.Nome, per lo stesso
    /// motivo già documentato su ScegliPlaceholderConverter (TargetNullValue non affidabile sui percorsi
    /// annidati quando l'oggetto intermedio è null).</summary>
    public string TemplateSelezionatoTesto => TemplateSelezionato?.Nome ?? T("CrEdScegliTipoPlaceholder");
    public string TemplateTestDaCreareTesto => TemplateTestDaCreare?.Nome ?? T("CrEdScegliTipoTestPlaceholder");

    partial void OnTemplateSelezionatoChanged(TestTemplate? value) { if (value != null) ChangeRequest.TemplateUid = value.Uid; if (!_inizializzazione) ModificheNonSalvate = true; CaricaCampiCr(); OnPropertyChanged(nameof(TemplateSelezionatoTesto)); }
    partial void OnTemplateTestDaCreareChanged(TestTemplate? value) => OnPropertyChanged(nameof(TemplateTestDaCreareTesto));
    partial void OnModificheNonSalvateChanged(bool value)
    {
        OnPropertyChanged(nameof(PuoRecuperareBozza));
        OnPropertyChanged(nameof(PuoRecuperareBozzaNuova));
        OnPropertyChanged(nameof(PuoRecuperareBozzaLocale));
    }

    private void CaricaCampiCr()
    {
        CampiCr.Clear();
        foreach (var campo in TemplateSelezionato?.CampiPianificazione.Concat(TemplateSelezionato.Sezioni.SelectMany(s => s.Campi)).Where(c => c.Tipo == TipoCampoTemplate.CampoLibero).OrderBy(c => c.Ordine) ?? Enumerable.Empty<CampoTemplate>())
        {
            if (campo.CondizioneEsito.Count > 0) continue;
            if (campo.StatiCrVisibile.Count > 0 && !campo.StatiCrVisibile.Contains(ChangeRequest.Stato)) continue;
            var obbl = campo.StatiCrObbligatorio.Count > 0 ? campo.StatiCrObbligatorio.Contains(ChangeRequest.Stato) : campo.Obbligatorio;
            CampiCr.Add(new CampoCrRiga(campo, ChangeRequest.CampiCustom, obbl, () => ModificheNonSalvate = true));
        }
    }

    /// <summary>Rilegge e valida una bozza CR fuori dal thread UI. Non modifica lo stato dell'editor.</summary>
    public ChangeRequest CaricaBozzaRecuperabile(FailedSaveDraftInspector.DraftRow row)
    {
        if (!PuoRecuperareBozza || ModificheNonSalvate || _snapshot == null || !_revisioneAttesa.HasValue)
            throw new InvalidOperationException(T("CrRecuperoSalvareAnnullarePrimaMsg"));
        return FailedSaveDraftRecoveryPreparationService.LoadChangeRequestForEditor(
            _state.Paths.Root, row, _snapshot, _revisioneAttesa.Value);
    }

    /// <summary>
    /// Stages a user-confirmed draft in this editor. The immutable opening snapshot and expected revision
    /// remain unchanged, so the next Save still passes through the repository's ordinary conflict guards.
    /// </summary>
    public void ApplicaBozzaRecuperata(ChangeRequest draft, IReadOnlyCollection<string> selectedFields)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(selectedFields);
        var selected = selectedFields.Distinct(StringComparer.Ordinal).ToArray();
        if (!PuoRecuperareBozza || ModificheNonSalvate || _snapshot == null ||
            draft.Uid != _snapshot.Uid || draft.Id != _snapshot.Id ||
            _revisioneAttesa.HasValue && draft.RevisioneDati > _revisioneAttesa.Value || selected.Length == 0 ||
            draft.VeicoliImpattati == null || draft.EcuImpattate == null ||
            draft.StrumentiImpattati == null || draft.CampiCustom == null ||
            draft.TicketCollegati == null || draft.Allegati == null ||
            draft.CrCorrelate == null || draft.TestValidazione == null ||
            selected.Any(field => !FailedSaveDraftRecoveryPreparationService.EditableChangeRequestFields.ContainsKey(field)))
            throw new InvalidOperationException(T("CrRevisioneCambiataImportaMsg"));

        MostraChangeRequestStaged(StagingCampiCr(Clona(_snapshot), draft, selected));
        var labels = selected.Select(field => FailedSaveDraftRecoveryPreparationService.EditableChangeRequestFields[field]);
        Messaggio = T("CrCampiImportatiEditorPrefisso") + string.Join(", ", labels) +
            T("CrRivedereModificheSuffisso") +
            T("CrSalvataggioVerificheRevisioneMsg");
        Errore = "";
        OnPropertyChanged(nameof(TitoloPagina));
        OnPropertyChanged(nameof(IsNew));
        NotificaStato();
    }

    /// <summary>Copia sulla CR di base i soli campi modificabili indicati, dalla bozza o dal candidato.</summary>
    private ChangeRequest StagingCampiCr(ChangeRequest copia, ChangeRequest sorgente, IEnumerable<string> campi)
    {
        foreach (var field in campi)
        {
            switch (field)
            {
                case nameof(ChangeRequest.TemplateUid): copia.TemplateUid = sorgente.TemplateUid; break;
                case nameof(ChangeRequest.Titolo): copia.Titolo = sorgente.Titolo; break;
                case nameof(ChangeRequest.DescrizioneBreve): copia.DescrizioneBreve = sorgente.DescrizioneBreve; break;
                case nameof(ChangeRequest.Descrizione): copia.Descrizione = sorgente.Descrizione; break;
                case nameof(ChangeRequest.MotivazioneDuplicato): copia.MotivazioneDuplicato = sorgente.MotivazioneDuplicato; break;
                case nameof(ChangeRequest.Responsabile): copia.Responsabile = sorgente.Responsabile; break;
                case nameof(ChangeRequest.VeicoliImpattati): copia.VeicoliImpattati = [.. sorgente.VeicoliImpattati]; break;
                case nameof(ChangeRequest.EcuImpattate): copia.EcuImpattate = [.. sorgente.EcuImpattate]; break;
                case nameof(ChangeRequest.StrumentiImpattati): copia.StrumentiImpattati = [.. sorgente.StrumentiImpattati]; break;
                case nameof(ChangeRequest.CampiCustom): copia.CampiCustom = new(sorgente.CampiCustom); break;
                case nameof(ChangeRequest.TicketCollegati): copia.TicketCollegati = ClonaLista(sorgente.TicketCollegati); break;
                case nameof(ChangeRequest.Allegati): copia.Allegati = ClonaLista(sorgente.Allegati); break;
                case nameof(ChangeRequest.CrCorrelate): copia.CrCorrelate = [.. sorgente.CrCorrelate]; break;
                case nameof(ChangeRequest.TestValidazione): copia.TestValidazione = ClonaLista(sorgente.TestValidazione); break;
            }
        }
        return copia;
    }

    /// <summary>Mostra nell'editor una CR costruita da una bozza e segna le modifiche come non salvate.</summary>
    private void MostraChangeRequestStaged(ChangeRequest staged)
    {
        AssicuraGiroCompletato(); // i cataloghi (template) devono essere caricati prima di scegliere il template della bozza
        _inizializzazione = true;
        try
        {
            ChangeRequest = staged;
            TemplateSelezionato = TemplateCr.FirstOrDefault(t => t.Uid == ChangeRequest.TemplateUid);
            Veicoli.Clear(); foreach (var x in ChangeRequest.VeicoliImpattati) Veicoli.Add(x);
            Componente.Clear(); foreach (var x in ChangeRequest.EcuImpattate) Componente.Add(x);
            Strumenti.Clear(); foreach (var x in ChangeRequest.StrumentiImpattati) Strumenti.Add(x);
            TestValidazione.Clear(); foreach (var x in ChangeRequest.TestValidazione) TestValidazione.Add(x);
            AllegatiVisual.Clear(); foreach (var x in ChangeRequest.Allegati) AllegatiVisual.Add(x);
            TicketCollegatiVisual.Clear(); foreach (var x in ChangeRequest.TicketCollegati) TicketCollegatiVisual.Add(x);
            CaricaCampiCr();
        }
        finally { _inizializzazione = false; }
        AggiornaValutazione();
        ModificheNonSalvate = true;
    }

    /// <summary>UID di tutte le CR nel repository, archiviate incluse: scarta dall'elenco le bozze di record gia esistenti.</summary>
    public IReadOnlySet<Guid> UidCrPresenti() =>
        _state.ChangeRequests.PeekAllReadOnly(includeArchiviate: true).Select(c => c.Uid).ToHashSet();

    /// <summary>Legge una bozza di CR mai salvata senza scrivere nulla; il candidato si applica con <see cref="ApplicaBozzaNuovaRecuperata"/>.</summary>
    public ChangeRequest CaricaBozzaNuovaRecuperabile(FailedSaveDraftInspector.DraftRow row)
    {
        if (!PuoRecuperareBozzaNuova)
            throw new InvalidOperationException(T("CrRecuperoNuovaRichiedeEditorMsg"));
        var presenti = UidCrPresenti();
        return FailedSaveDraftRecoveryPreparationService.LoadNewChangeRequestForEditor(
            _state.Paths.Root, row, presenti.Contains);
    }

    /// <summary>Importa nell'editor nuovo tutti i campi modificabili del candidato: nessun ID assegnato, nessun salvataggio automatico.</summary>
    public void ApplicaBozzaNuovaRecuperata(ChangeRequest candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!PuoRecuperareBozzaNuova || !string.IsNullOrEmpty(candidate.Id))
            throw new InvalidOperationException(T("CrBozzaNonImportabileMsg"));
        var campi = FailedSaveDraftRecoveryPreparationService.EditableChangeRequestFields;
        var staged = StagingCampiCr(Clona(ChangeRequest), candidate, campi.Keys);
        staged.Uid = candidate.Uid;
        MostraChangeRequestStaged(staged);
        Messaggio = T("CrCampiImportatiNuovoRecordPrefisso") + string.Join(", ", campi.Values) +
            T("CrNessunFileAssegnaIdSuffisso") +
            T("CrCloudNonVerificatoMsg");
        Errore = "";
        OnPropertyChanged(nameof(TitoloPagina));
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(PuoEsportarePacchetto));
        NotificaStato();
    }

    [RelayCommand] private void RimuoviVeicolo(string? x) { Rimuovi(x, ChangeRequest.VeicoliImpattati, Veicoli); }
    [RelayCommand] private void RimuoviEcu(string? x) { Rimuovi(x, ChangeRequest.EcuImpattate, Componente); }
    [RelayCommand] private void RimuoviStrumento(string? x) { Rimuovi(x, ChangeRequest.StrumentiImpattati, Strumenti); }

    /// <summary>Collegamento ticket TICKET/Ticketing (feedback utente 2026-09-15: "nelle CR dovrebbe poi anche
    /// essere possibile collegare il ticket TICKET come avviene per le non conformità") — il campo
    /// ChangeRequest.TicketCollegati esisteva già (usato solo dall'export Ticketing/report), ma l'editor CR non
    /// aveva alcuna UI per popolarlo: stesso schema già in uso per TestCase (TestCaseEditorViewModel.AggiungiTicket).</summary>
    [RelayCommand]
    private void AggiungiTicket()
    {
        if (string.IsNullOrWhiteSpace(NuovoTicketRiferimento)) return;
        var riferimento = NuovoTicketRiferimento.Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(riferimento, "^TICKET-[A-Z0-9_-]+$"))
        {
            Errore = LocalizationService.Instance.Traduci("FormatoPolarionNonValido");
            return;
        }
        if (!string.IsNullOrWhiteSpace(NuovoTicketUrl) && !Uri.TryCreate(NuovoTicketUrl.Trim(), UriKind.Absolute, out _))
        {
            Errore = LocalizationService.Instance.Traduci("LinkPolarionNonValido");
            return;
        }
        var ticket = new TicketRif
        {
            Riferimento = riferimento,
            Url = string.IsNullOrWhiteSpace(NuovoTicketUrl) ? PolarionTicketLinkService.DerivaUrl(_state.Paths, riferimento) : NuovoTicketUrl.Trim(),
            CollegatoIl = DateTime.Now,
            CollegatoDa = _state.Config.OperatoreAttivo
        };
        ChangeRequest.TicketCollegati.Add(ticket);
        TicketCollegatiVisual.Add(ticket);
        NuovoTicketRiferimento = "";
        NuovoTicketUrl = "";
        Errore = "";
        ModificheNonSalvate = true;
    }

    [RelayCommand]
    private void ApriTicket(TicketRif? ticket)
    {
        if (ticket == null || string.IsNullOrWhiteSpace(ticket.Url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ticket.Url) { UseShellExecute = true }); }
        catch { Errore = LocalizationService.Instance.Traduci("ApriPolarionErrore"); }
    }

    [RelayCommand]
    private void RimuoviTicket(TicketRif? ticket)
    {
        if (ticket == null) return;
        ChangeRequest.TicketCollegati.Remove(ticket);
        TicketCollegatiVisual.Remove(ticket);
        ModificheNonSalvate = true;
    }

    /// <summary>Aggiunta multipla in un solo passaggio (feedback utente 2026-08-21: "dovrebbe esserci la
    /// combobox con multiselezione essendo possibile più di un campo sia in veicoli che in componente che in
    /// strumenti") — Veicolo/componente/Strumento sono campi intrinsecamente multi-valore (si aggiunge più di un
    /// elemento), quindi seguono lo stesso schema già in uso per Requisiti/Funzionalità/Test correlati nel Test
    /// (checkbox flyout sotto soglia, dialogo con ricerca sopra soglia) invece di "scegli uno, Aggiungi, ripeti"
    /// — vedi ChangeRequestEditorView.OnScegliVeicoli/OnScegliEcu/OnScegliStrumenti per il code-behind che
    /// decide flyout o dialogo in base alla quantità.</summary>
    public void AggiungiVeicoli(IReadOnlyList<string> valori) { foreach (var v in valori) Aggiungi(v, ChangeRequest.VeicoliImpattati, Veicoli); }
    public void AggiungiEcu(IReadOnlyList<string> valori) { foreach (var v in valori) Aggiungi(v, ChangeRequest.EcuImpattate, Componente); }
    public void AggiungiStrumenti(IReadOnlyList<string> valori) { foreach (var v in valori) Aggiungi(v, ChangeRequest.StrumentiImpattati, Strumenti); }

    private static VoceSelezionabile VoceStringa(string s) => new() { Valore = s, TestoPrincipale = s, TestoRicerca = s };

    // ----- Picker soglia combobox/pulsante (punto 1/2/3, feedback utente 2026-08-21): stesso schema già in
    // uso nel Test (ListaEntroSogliaConverter/ListaOltreSogliaConverter + SelezionaDialogService), esteso a
    // tutta la pagina CR invece di restare confinato a Veicolo/componente/Strumento.

    [RelayCommand]
    private async Task ScegliTemplateCr()
    {
        var voci = TemplateCr.Select(t => new VoceSelezionabile { Valore = t, TestoPrincipale = t.Nome, TestoRicerca = t.Nome }).ToList();
        var voce = await SelezionaDialogService.Scegli(T("CrEdScegliTipo"), voci);
        if (voce != null) TemplateSelezionato = (TestTemplate)voce.Valore;
    }

    [RelayCommand]
    private async Task ScegliResponsabile()
    {
        var voce = await SelezionaDialogService.Scegli(T("CrEdScegliResponsabile"), OperatoriDisponibili.Select(VoceStringa).ToList());
        if (voce != null) Responsabile = (string)voce.Valore;
    }

    [RelayCommand]
    private async Task ScegliTemplateTest()
    {
        var voci = TemplateTest.Select(t => new VoceSelezionabile { Valore = t, TestoPrincipale = t.Nome, TestoRicerca = t.Nome }).ToList();
        var voce = await SelezionaDialogService.Scegli(T("CrEdScegliTipoTest"), voci);
        if (voce != null) TemplateTestDaCreare = (TestTemplate)voce.Valore;
    }

    /// <summary>Arricchisce la scelta con la descrizione breve del test (punto 2, feedback utente 2026-08-21:
    /// "no cronologia/suggerimenti automatici, una CR porta un cambiamento nuovo — basta vedere i dettagli del
    /// test nella lista"), scegliendo e collegando in un solo passaggio come obbligatorio.</summary>
    [RelayCommand]
    private async Task ScegliTestValidazione()
    {
        var voci = TestDisponibili.Select(t => new VoceSelezionabile
        {
            Valore = t, TestoPrincipale = t.Id,
            TestoSecondario = string.IsNullOrWhiteSpace(t.DescrizioneBreve) ? null : t.DescrizioneBreve,
            TestoRicerca = $"{t.Id} {t.DescrizioneBreve} {t.Componente}"
        }).ToList();
        var voce = await SelezionaDialogService.Scegli(T("CrEdScegliTestCollegare"), voci);
        if (voce == null) return;
        TestDaCollegare = (TestCase)voce.Valore;
        CollegaTest();
    }

    /// <summary>Aggiunge tutti i veicoli di una o più gamme in un solo passaggio (punto 3/4, feedback utente
    /// 2026-08-21: multi-gamma con un pulsante "Seleziona tutto", non più una gamma alla volta). Chiamato dal
    /// code-behind dopo il flyout/dialogo di scelta multipla (stesso schema soglia di ScegliVeicolo ecc.).</summary>
    public void AggiungiVeicoliDaGamme(IReadOnlyList<string> gamme)
    {
        if (gamme.Count == 0) return;
        var insieme = new HashSet<string>(gamme, StringComparer.OrdinalIgnoreCase);
        foreach (var v in _state.Master.Veicoli.Load().Where(v => !v.Archiviata && insieme.Contains(v.GammaNome)))
            Aggiungi(v.Nome, ChangeRequest.VeicoliImpattati, Veicoli);
    }

    /// <summary>Raggruppa i veicoli selezionati per gamma (punto 4, feedback utente 2026-08-21: "non voglio la
    /// lista di tutti i veicoli" quando si sceglie una gamma intera) invece di un'unica esplosione piatta di
    /// badge — il dato sottostante (VeicoliImpattati) resta comunque una lista piatta di nomi veicolo, non
    /// tocca il motore di copertura CollegamentoValidazioneCr: il raggruppamento è solo di visualizzazione,
    /// risolto al volo sulla gamma attuale del veicolo in anagrafica (non congelato alla selezione).</summary>
    public IReadOnlyList<GruppoVeicoli> VeicoliRaggruppati
    {
        get
        {
            var mappaGamma = _state.Master.Veicoli.Load().Where(v => !v.Archiviata)
                .ToDictionary(v => v.Nome, v => v.GammaNome, StringComparer.OrdinalIgnoreCase);
            return Veicoli
                .GroupBy(v => mappaGamma.TryGetValue(v, out var g) && !string.IsNullOrWhiteSpace(g) ? g : "")
                .OrderBy(gr => string.IsNullOrEmpty(gr.Key) ? 1 : 0).ThenBy(gr => gr.Key)
                .Select(gr =>
                {
                    // Rinominato da "Altri veicoli" (feedback utente 2026-09-15: letto come una seconda lista
                    // separata/ridondante rispetto a "Aggiungi veicoli...", quando è solo il bucket di
                    // visualizzazione per i veicoli scelti la cui Gamma non è nota in anagrafica — nessun
                    // secondo campo di input esiste davvero, i dati sono sempre gli stessi VeicoliImpattati.
                    var etichetta = string.IsNullOrEmpty(gr.Key) ? T("CrEdVeicoliSenzaGamma") : gr.Key;
                    var veicoli = gr.ToList();
                    return new GruppoVeicoli(etichetta, veicoli, _gammeEspanse.Contains(etichetta) || veicoli.Count <= 4);
                })
                .ToList();
        }
    }

    [RelayCommand]
    private void ToggleGruppoVeicoli(string? etichetta)
    {
        if (string.IsNullOrEmpty(etichetta)) return;
        if (!_gammeEspanse.Remove(etichetta)) _gammeEspanse.Add(etichetta);
        OnPropertyChanged(nameof(VeicoliRaggruppati));
    }

    // ----- Allegati (punto 6, feedback utente 2026-08-21): stessa infrastruttura del Test (AttachmentService,
    // stessa cartella condivisa su disco), solo con un bucket dedicato invece del componente. Richiede la CR già
    // salvata (stesso vincolo già esistente per "Esporta pacchetto CR"), non un salvataggio silenzioso
    // anticipato come nel Test: la CR non ha un singolo campo "Componente" la cui compilazione segnali che è pronta.

    [RelayCommand]
    private async Task AggiungiAllegato(string? percorsoFile)
    {
        if (string.IsNullOrEmpty(percorsoFile) || !_state.CartellaValida) return;
        if (IsNew) { Errore = T("CrEdSalvaPrimaAllegati"); return; }
        try
        {
            var allegato = await AttachmentService.SalvaAsync(_state.Paths, ChangeRequest, percorsoFile, _state.Config.OperatoreAttivo);
            ChangeRequest.Allegati.Add(allegato);
            AllegatiVisual.Add(allegato);
            ModificheNonSalvate = true;
        }
        catch (Exception ex) { Errore = ex.Message; }
    }

    // Il binario non si tocca qui: resta nel percorso attivo finche' il Salva non persiste la CR senza questo
    // allegato (vedi SalvaInternoOrThrow). Annulla, crash o un Salva fallito lasciano quindi file e JSON coerenti.
    private readonly AttachmentRemovalQueue _rimozioniAllegati = new();

    [RelayCommand]
    private void RimuoviAllegato(Allegato? allegato)
    {
        if (allegato == null) return;
        _rimozioniAllegati.Accoda(allegato);
        ChangeRequest.Allegati.Remove(allegato);
        AllegatiVisual.Remove(allegato);
        ModificheNonSalvate = true;
    }

    [RelayCommand]
    private void ApriAllegato(Allegato? allegato)
    {
        if (allegato == null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AttachmentService.PercorsoCompleto(_state.Paths, ChangeRequest, allegato)) { UseShellExecute = true }); }
        catch { /* file non apribile: nessuna azione, non blocca il resto dell'app */ }
    }

    private void Aggiungi(string? value, List<string> model, ObservableCollection<string> visual)
    {
        if (string.IsNullOrWhiteSpace(value) || model.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        model.Add(value); visual.Add(value); InvalidaPerModificaTecnica();
    }
    private void Rimuovi(string? value, List<string> model, ObservableCollection<string> visual)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        model.RemoveAll(x => x.Equals(value, StringComparison.OrdinalIgnoreCase)); visual.Remove(value); InvalidaPerModificaTecnica();
    }
    private void InvalidaPerModificaTecnica()
    {
        // Cambiare l'ambito invalida implementazione/validazione gia' fatte: e' voluto (la copertura dei Test
        // era calcolata sul vecchio ambito), ma non deve avvenire in silenzio.
        var tornataAperta = ChangeRequest.Stato is not (StatoChangeRequest.Aperta or StatoChangeRequest.Annullata);
        if (tornataAperta)
        {
            if (ChangeRequest.Stato == StatoChangeRequest.Chiusa)
            {
                // I dati di chiusura (motivo, marcatura) non devono sopravvivere a una CR tornata Aperta: restano nel Changelog.
                ChangeRequestChiusuraService.AzzeraDatiChiusura(ChangeRequest);
                ChangeRequest.Changelog.Add(new ChangelogEntry
                {
                    Data = DateTime.Now, Operatore = _state.Config.OperatoreAttivo, Campo = "stato",
                    ValorePrecedente = "Chiusa", ValoreNuovo = "Aperta: modifica tecnica dell'ambito"
                });
            }
            ChangeRequest.Stato = StatoChangeRequest.Aperta;
        }
        ModificheNonSalvate = true; NotificaStato();
        if (tornataAperta) Messaggio = T("CrEdStatoRiportatoAperta");
    }

    [RelayCommand]
    private void CollegaTest()
    {
        if (TestDaCollegare == null || ChangeRequest.TestValidazione.Any(l => l.TestId == TestDaCollegare.Id)) return;
        var link = CreaLink(TestDaCollegare, obbligatorio: true);
        ChangeRequest.TestValidazione.Add(link); TestValidazione.Add(link); TestDaCollegare = null; ModificheNonSalvate = true; AggiornaValutazione();
    }

    [RelayCommand]
    private void CreaTestValidazione()
    {
        if (TemplateTestDaCreare == null) return;
        // Stesse regole del Salva: senza titolo/tipo/ambito la CR non deve essere persistita di striscio
        // solo perche' l'operatore ha chiesto un Test di validazione (e il Test non deve nascere orfano).
        if (!Valida(false)) return;
        var crPrima = Clona(ChangeRequest);
        var revisioneAttesaPrima = _revisioneAttesa;
        TestCase? test = null;
        try
        {
            using var tx = WorkflowTransactionService.Begin(_state.Paths, $"create-validation-test:{ChangeRequest.Id}");

            // Un Test di validazione e il link nella CR sono una sola operazione logica. Prima il Test veniva
            // persistito da solo e un conflitto sulla CR lasciava un orfano. Ora qualunque errore successivo
            // fa rollbackare il file Test insieme alla CR.
            test = new TestCase
            {
                Id = IdGenerator.Genera(_state.TestCases.GetAllIds(true), IdGenerator.IniziliDa(_state.Config.OperatoreAttivo)),
                TestTemplateUid = TemplateTestDaCreare.Uid,
                DataCreazione = DateTime.Now,
                UltimaModifica = DateTime.Now,
                CreatoDa = _state.Config.OperatoreAttivo,
                ModificatoDa = _state.Config.OperatoreAttivo,
                Assegnatario = _state.Config.OperatoreAttivo,
                Componente = new EcuSelection(ChangeRequest.EcuImpattate),
                Veicoli = new(ChangeRequest.VeicoliImpattati),
                StrumentiUsati = new(ChangeRequest.StrumentiImpattati),
                DescrizioneBreve = string.IsNullOrWhiteSpace(ChangeRequest.DescrizioneBreve)
                    ? $"Validazione {ChangeRequest.Id}: {ChangeRequest.Titolo}"
                    : ChangeRequest.DescrizioneBreve,
                Descrizione = ChangeRequest.Descrizione
            };
            test.NormalizzaVeicoli();
            test.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = DateTime.Now, Operatore = _state.Config.OperatoreAttivo, Esito = Esito.NonEseguitoNA, Condizioni = new() { new Condizione { Esito = Esito.NonEseguitoNA, Componente = new EcuSelection(test.Componente) } } });
            _state.TestCases.Save(test, true);
            var link = CreaLink(test, TestCreatoObbligatorio);
            ChangeRequest.TestValidazione.Add(link);
            TestValidazione.Add(link);
            SalvaInternoOrThrow();
            tx.Commit();
        }
        catch (Exception ex)
        {
            // Il rollback ripristina i file, ma i repository/VM possono avere già aggiornato le cache o gli
            // oggetti live. Riallineiamo entrambi senza lasciare il link/test fantasma nella schermata.
            ChangeRequest = crPrima;
            _revisioneAttesa = revisioneAttesaPrima;
            TestValidazione.Clear();
            foreach (var link in ChangeRequest.TestValidazione) TestValidazione.Add(link);
            _state.TestCases.RefreshIndex();
            ModificheNonSalvate = true;
            Errore = TF("SalvataggioNonRiuscitoFmt", ex.Message);
            CaricaCampiCr();
            return;
        }

        AggiornaValutazione();
        // Finestra dedicata, non navigazione (feedback utente 2026-08-21: "creare un test di validazione mi
        // chiude la CR senza motivo"): _apriTest(test.Id) sostituiva l'intera pagina corrente con l'editor del
        // test appena creato, portando via la CR dalla vista prima ancora di poterla salvare con calma. Il test
        // è già collegato e la CR è già salvata sopra; la finestra separata (vedi
        // ChangeRequestEditorView.RichiestaAperturaTestInFinestra) lascia la pagina CR intatta sullo sfondo.
        RichiestaAperturaTestInFinestra?.Invoke(test!.Id);
    }

    /// <summary>Nuova istanza pienamente editabile (non la variante "visualizzatore correlato" di sola lettura
    /// usata altrove) per la finestra dedicata al Test di validazione appena creato — vedi CreaTestValidazione
    /// e TestCaseEditWindow.</summary>
    public TestCaseEditorViewModel CreaEditorTestValidazione(string testCaseId) =>
        new(_state, testCaseId, () => { }, _ => { });

    public event Action<string>? RichiestaAperturaTestInFinestra;

    private CollegamentoValidazioneCr CreaLink(TestCase test, bool obbligatorio)
    {
        var testEcu = test.Componente.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var link = new CollegamentoValidazioneCr
        {
            TestId = test.Id,
            Obbligatorio = obbligatorio,
            VeicoliCoperti = ChangeRequest.VeicoliImpattati.Intersect(test.VeicoliEffettivi, StringComparer.OrdinalIgnoreCase).ToList(),
            EcuCoperte = ChangeRequest.EcuImpattate.Where(testEcu.Contains).ToList(),
            StrumentiCoperti = ChangeRequest.StrumentiImpattati.Intersect(test.StrumentiUsati, StringComparer.OrdinalIgnoreCase).ToList(),
            CollegatoDa = _state.Config.OperatoreAttivo
        };
        var mismatch = link.VeicoliCoperti.Count != ChangeRequest.VeicoliImpattati.Count || link.EcuCoperte.Count != ChangeRequest.EcuImpattate.Count || link.StrumentiCoperti.Count != ChangeRequest.StrumentiImpattati.Count;
        link.EccezioneCopertura = mismatch;
        return link;
    }

    [RelayCommand]
    private void ApprovaEccezione(CollegamentoValidazioneCr? link)
    {
        if (!_state.EMaster || link == null || string.IsNullOrWhiteSpace(link.MotivazioneEccezione)) return;
        link.ApprovataDa = _state.Config.OperatoreAttivo; link.ApprovataIl = DateTime.Now; ModificheNonSalvate = true; AggiornaValutazione();
    }

    [RelayCommand]
    private void ApprovaDeviazione(CollegamentoValidazioneCr? link)
    {
        if (!_state.EMaster || link == null || string.IsNullOrWhiteSpace(link.MotivazioneDeviazione)) return;
        link.DeviazioneApprovata = true; link.DeviazioneApprovataDa = _state.Config.OperatoreAttivo; ModificheNonSalvate = true; AggiornaValutazione();
    }

    [RelayCommand] private void ApriTest(string? id) { if (!string.IsNullOrWhiteSpace(id)) _apriTest(id); }

    [RelayCommand]
    private void Implementa()
    {
        if (!PuoImplementare)
        {
            Errore = T("CrEdTransizioneNonConsentita");
            return;
        }
        // Il dialogo Cambio stato può essere aperto prima che cataloghi e copertura finiscano il giro in background.
        // Completare lo stesso giro usato da Salva evita che il clic immediato fallisca per TemplateSelezionato nullo.
        if (!AssicuraGiroCompletato() || !Valida(perImplementazione: true)) return;
        // Salva una sola fotografia coerente. Prima veniva eseguito SalvaInterno() anche PRIMA della transizione
        // di stato: un errore al secondo Save lasciava su disco la prima revisione ma non l'implementazione.
        // La transizione e' applicata a una COPIA: se il Save fallisce (conflitto di revisione, I/O) la CR
        // mostrata resta quella persistita e un nuovo tentativo non duplica revisioni ne' stati mai scritti.
        if (!SalvaInterno(cr =>
        {
            cr.Stato = cr.TestValidazione.Any(l => l.Obbligatorio) ? StatoChangeRequest.DaValidare : StatoChangeRequest.Implementata;
            cr.ImplementataIl = DateTime.Now;
            cr.ImplementataDa = _state.Config.OperatoreAttivo;
            cr.RevisioneImplementata = cr.Revisioni.Count + 1; // SalvaInterno aggiunge questa revisione.
        })) return;
        var feedback = T("CrEdNotImplementata");
        AggiornaValutazione(feedback);
        NotificaInteressati(feedback); NotificaStato();
    }

    [RelayCommand]
    private void Chiudi()
    {
        AggiornaValutazione();
        // AggiornaValutazione può completarsi in background: la chiusura deve usare l'esito appena ricalcolato,
        // non il vecchio Stato=Validata visibile prima del completamento del giro (da questo stato dipende la
        // marcatura "chiusa senza validazione").
        if (!AssicuraGiroCompletato()) return;
        if (!PuoChiudere)
        {
            Errore = T("CrEdTransizioneNonConsentita");
            return;
        }
        if (string.IsNullOrWhiteSpace(MotivoTransizione))
        {
            Errore = T("CrEdMotivoChiusuraMancante");
            return;
        }
        var motivo = MotivoTransizione;
        var senzaValidazione = false;
        if (!SalvaInterno(cr =>
        {
            var esito = ChangeRequestChiusuraService.Chiudi(cr, _state.Config.OperatoreAttivo, motivo,
                _state.PuoScrivere, _state.EMaster, DateTime.Now);
            if (esito != EsitoTransizioneCr.Ok) throw new InvalidOperationException(T("CrEdTransizioneNonConsentita"));
            senzaValidazione = cr.ChiusaSenzaValidazione;
        })) return;
        MotivoTransizione = "";
        var msg = T(senzaValidazione ? "CrEdNotChiusaSenzaValidazione" : "CrEdNotChiusa");
        Messaggio = msg; NotificaInteressati(msg); NotificaStato();
    }

    [RelayCommand]
    private void Riapri()
    {
        if (!PuoRiaprire)
        {
            Errore = T("CrEdTransizioneNonConsentita");
            return;
        }
        if (string.IsNullOrWhiteSpace(MotivoTransizione))
        {
            Errore = T("CrEdMotivoRiaperturaMancante");
            return;
        }
        var motivo = MotivoTransizione;
        if (!SalvaInterno(cr =>
        {
            var esito = ChangeRequestChiusuraService.Riapri(cr, _state.Config.OperatoreAttivo, motivo,
                _state.PuoScrivere, _state.EMaster, DateTime.Now);
            if (esito != EsitoTransizioneCr.Ok) throw new InvalidOperationException(T("CrEdTransizioneNonConsentita"));
        })) return;
        MotivoTransizione = "";
        Messaggio = T("CrEdNotRiaperta"); NotificaInteressati(T("CrEdNotRiaperta")); NotificaStato();
    }

    [RelayCommand]
    private void Salva()
    {
        Errore = "";
        Messaggio = "";
        if (StatoSelezionato != ChangeRequest.Stato)
        {
            if (!StatiWorkflow.Contains(StatoSelezionato))
            {
                Errore = T("CrEdTransizioneNonConsentita");
                return;
            }

            switch (StatoSelezionato)
            {
                case StatoChangeRequest.Implementata when PuoImplementare:
                    Implementa();
                    return;
                case StatoChangeRequest.Chiusa when PuoChiudere:
                    Chiudi();
                    return;
                case StatoChangeRequest.Aperta when PuoRiaprire:
                    Riapri();
                    return;
                default:
                    Errore = T("CrEdTransizioneNonConsentita");
                    return;
            }
        }

        // PuoModificare include PuoScrivere, che puo' diventare false anche dopo l'inizio delle modifiche.
        // Lasciare arrivare il tentativo a SalvaInterno consente al repository di rifiutare la scrittura condivisa
        // e al relativo handler di conservare una bozza locale. Stati terminali e CR archiviate restano bloccati.
        if (ChangeRequest.Archiviata || ChangeRequest.Stato is (StatoChangeRequest.Chiusa or StatoChangeRequest.Annullata))
        {
            Errore = T("CrEdTransizioneNonConsentita");
            return;
        }
        if (!AssicuraGiroCompletato()) return;
        if (!IsNew && !_rimozioniAllegati.HaPendenti && StessoContenutoCorrente())
        {
            CompletaSalvataggioSenzaModifiche();
            return;
        }
        if (Valida(false)) SalvaInterno();
    }

    /// <summary>Salvataggio della Modifica rapida (responsabile e ticket). A differenza di <see cref="Salva"/> non esegue la
    /// validazione completa (template, ambito, campi custom): quella resta di Implementa e dell'editor completo, cosi'
    /// una CR incompleta si puo' comunque riassegnare. Passa dallo stesso salvataggio con controllo di revisione.</summary>
    [RelayCommand]
    private void SalvaRapida()
    {
        Errore = "";
        Messaggio = "";
        if (!PuoModificare)
        {
            Errore = T("CrEdTransizioneNonConsentita");
            return;
        }
        if (!AssicuraGiroCompletato()) return;
        if (!IsNew && !_rimozioniAllegati.HaPendenti && StessoContenutoCorrente())
        {
            CompletaSalvataggioSenzaModifiche();
            return;
        }
        SalvaInterno();
    }

    /// <summary>Sezioni disponibili nel dialogo di esportazione del pacchetto CR (punto 7, feedback utente
    /// 2026-08-21: "esporta pacchetto CR" deve essere identico a "genera pacchetto Ticketing" nei Test) — a
    /// differenza del Test, la CR non ha un meccanismo di restrizione per template, quindi sono sempre tutte
    /// disponibili.</summary>
    public List<string> SezioniPacchettoDisponibili() => CrSezioni.Tutte.ToList();

    public List<string> SezioniPacchettoDiDefault() => CrSezioni.Tutte.ToList();

    public string EtichettaSezionePacchetto(string chiave) => chiave switch
    {
        CrSezioni.DatiGenerali => T("CrEdSezDatiGenerali"),
        CrSezioni.Ambito => T("CrEdSezAmbito"),
        CrSezioni.CampiCr => T("CrEdSezCampiCr"),
        CrSezioni.TestValidazione => T("CrEdSezTestValidazione"),
        CrSezioni.TicketCollegati => T("CrEdSezTicket"),
        CrSezioni.Allegati => T("CrEdSezAllegati"),
        _ => chiave,
    };

    public string GeneraAnteprimaPacchetto(HashSet<string> sezioni) => TicketExportService.GeneraTesto(ChangeRequest, _state, sezioni);

    /// <summary>Stesso pacchetto zip (txt+pdf+manifest+evidenze) di "genera pacchetto Ticketing" nei Test, non
    /// più un semplice .txt: sezioni e cartella di destinazione scelte dall'utente nei dialoghi di code-behind
    /// (ChangeRequestEditorView.OnEsportaPacchetto), null qui significa "annullato".</summary>
    /// <summary>Vero mentre il pacchetto Ticketing viene generato su un thread di background: il pulsante resta disabilitato.</summary>
    [ObservableProperty] private bool pacchettoInCorso;

    /// <summary>Pulsante "Esporta pacchetto": CR gia salvata e nessuna generazione in corso.</summary>
    public bool PuoEsportarePacchetto => !IsNew && !PacchettoInCorso;

    internal AppState StatoApp => _state;

    partial void OnPacchettoInCorsoChanged(bool value) => OnPropertyChanged(nameof(PuoEsportarePacchetto));

    [RelayCommand]
    private async Task EsportaPacchetto(RichiestaPacchettoPolarion? richiesta)
    {
        if (IsNew) { Errore = T("CrEdSalvaPrimaEsportare"); return; }
        if (!AssicuraGiroCompletato()) return; // il pacchetto include stato e copertura: non si esporta un valore superato
        if (richiesta == null || string.IsNullOrWhiteSpace(richiesta.Cartella) || PacchettoInCorso) return;
        string percorso;
        PacchettoInCorso = true;
        Messaggio = T("GenerazioneReportInCorso");
        try { percorso = await Task.Run(() => PolarionPackageService.Crea(_state.Paths, ChangeRequest, _state, richiesta.Cartella, richiesta.Sezioni)); }
        catch (Exception ex) { Messaggio = ""; Errore = ex.Message; return; }
        finally { PacchettoInCorso = false; }
        Errore = "";
        Messaggio = TF("CrEdPacchettoEsportatoFmt", percorso);
    }

    [RelayCommand]
    private Task Archivia() =>
        ComandoAsincrono.EseguiAsync(ArchiviaCoreAsync, "Archiviazione della change request", m => Errore = m);

    private async Task ArchiviaCoreAsync()
    {
        if (!PuoArchiviare) return;
        if (!await ConfirmService.Chiedi(T("CrEdArchiviaTitolo"), TF("CrEdArchiviaBodyFmt", ChangeRequest.Id), T("CrEdArchiviaBtn"), T("Annulla"))) return;
        if (!AssicuraGiroCompletato()) return;
        _state.ChangeRequests.Archivia(ChangeRequest, _state.Config.OperatoreAttivo);
        _torna();
    }
    /// <summary>Salvataggio fail-closed: fallimento (conflitto di revisione, I/O) = messaggio localizzato in Errore
    /// e NESSUN cambiamento alla CR mostrata (vedi SalvaInternoOrThrow). Ritorna false se non e' stato persistito.</summary>
    private bool SalvaInterno(Action<ChangeRequest>? muta = null)
    {
        try { SalvaInternoOrThrow(muta); }
        catch (Exception ex)
        {
            ConflittoRevisione = ex is ConflittoRevisioneException;
            var recupero = "";
            try
            {
                var path = FailedSaveDraftService.Save(_state.Paths.Root, "change-request",
                    ChangeRequest.Uid, _revisioneAttesa ?? 0, _state.Config.OperatoreAttivo,
                    ChangeRequest, ex.Message, baseSnapshot: _snapshot);
                recupero = T("BozzaLocaleConservataPrefisso") + path +
                    T("CrBozzaNonPubblicataSuffisso");
            }
            catch (Exception draftEx)
            {
                CrashLogService.Registra("Recupero locale CR dopo salvataggio fallito", draftEx);
                recupero = T("CrAttenzioneBozzaImpossibileMsg");
            }
            Errore = TF("SalvataggioNonRiuscitoFmt", ex.Message) + recupero;
            return false;
        }
        return true;
    }

    /// <summary>Variante che propaga l'eccezione (usata dentro la WorkflowTransaction di CreaTestValidazione, che
    /// deve poter fare rollback). Id, revisione e mutazioni di stato sono applicati a una COPIA: la CR live viene
    /// sostituita solo dopo il Save riuscito, cosi' un errore non lascia Id/revisioni/stati mai persistiti e un
    /// retry non duplica la revisione.</summary>
    private void SalvaInternoOrThrow(Action<ChangeRequest>? muta = null)
    {
        // Cataloghi e copertura devono essere quelli della CR che si sta salvando: un giro in attesa si completa qui, e un errore
        // arriva al chiamante (SalvaInterno registra il salvataggio non riuscito, senza scrivere nulla).
        if (_giroInAttesa) ApplicaGiroSincrono(_giroIniziale);
        var candidato = Clona(ChangeRequest);
        muta?.Invoke(candidato);
        if (string.IsNullOrWhiteSpace(candidato.Id)) candidato.Id = _state.ChangeRequests.NextId();
        candidato.TemplateUid = TemplateSelezionato?.Uid ?? candidato.TemplateUid;
        if (!IsNew && !_rimozioniAllegati.HaPendenti && _snapshotPersistitoCorrente != null &&
            ChangeRequestWorkflowService.StessoContenutoPersistibile(_snapshotPersistitoCorrente, candidato))
        {
            CompletaSalvataggioSenzaModifiche();
            return;
        }
        candidato.UltimaModifica = DateTime.Now;
        candidato.Revisioni.Add(ChangeRequestWorkflowService.CreaRevisione(candidato, _state.Config.OperatoreAttivo));
        _state.ChangeRequests.Save(candidato, _revisioneAttesa);
        var paths = _state.Paths;
        _rimozioniAllegati.ApplicaDopoPersistenza(candidato.Allegati, a => AttachmentService.Elimina(paths, candidato, a));

        // Da qui in poi il Save e' riuscito: adotta la copia persistita come CR corrente.
        _revisioneAttesa = candidato.RevisioneDati;
        _snapshotPersistitoCorrente = Clona(candidato);
        ChangeRequest = candidato;
        SincronizzaStatoSelezionato();
        // Le righe dei campi custom (CampoCrRiga) tengono un riferimento al dizionario CampiCustom dell'istanza
        // precedente: vanno ri-legate a quella nuova, altrimenti una modifica successiva finirebbe sul dizionario
        // vecchio e il Salva seguente la perderebbe in silenzio. (Veicoli/Componente/Strumenti sono liste di stringhe
        // con contenuto identico e le mutazioni passano sempre da ChangeRequest.* al momento della chiamata;
        // TestValidazione/Allegati/Ticket sono risincronizzati subito sotto.)
        CaricaCampiCr();
        TestValidazione.Clear(); foreach (var l in candidato.TestValidazione) TestValidazione.Add(l);
        AllegatiVisual.Clear(); foreach (var a in candidato.Allegati) AllegatiVisual.Add(a);
        TicketCollegatiVisual.Clear(); foreach (var t in candidato.TicketCollegati) TicketCollegatiVisual.Add(t);
        ModificheNonSalvate = false; Messaggio = T("CrEdSalvata"); Errore = ""; ConflittoRevisione = false;
        OnPropertyChanged(nameof(TitoloPagina)); OnPropertyChanged(nameof(IsNew)); OnPropertyChanged(nameof(PuoEsportarePacchetto));
        NotificaStato();
    }

    private bool StessoContenutoCorrente()
    {
        if (_snapshotPersistitoCorrente == null) return false;
        var candidato = Clona(ChangeRequest);
        candidato.TemplateUid = TemplateSelezionato?.Uid ?? candidato.TemplateUid;
        return ChangeRequestWorkflowService.StessoContenutoPersistibile(_snapshotPersistitoCorrente, candidato);
    }

    private void CompletaSalvataggioSenzaModifiche()
    {
        ModificheNonSalvate = false;
        Errore = "";
        Messaggio = T("CrEdNessunaModifica");
    }

    /// <summary>Blocco salvataggio come popup posizionato sulla card interessata (punto 8, feedback utente
    /// 2026-08-21), non più solo il banner di testo in cima: CampiMancanti guida CampiMancantiDialog (stesso
    /// componente già usato per il Test) verso "Dati Change Request" o "Ambito impattato" a seconda della
    /// Chiave. Le regole trasversali (Ambito mancante, motivazione duplicato) non hanno un singolo campo su cui
    /// puntare — per queste la scelta esplicita dell'utente è ancorarle comunque alla card "Ambito impattato"
    /// invece di ripiegare su un alert generico separato (a differenza del Test, che per questi casi apre un
    /// ConfirmService.Chiedi senza card).</summary>
    private bool Valida(bool perImplementazione)
    {
        var mancanti = new List<CampoMancante>();
        if (TemplateSelezionato == null) mancanti.Add(new(T("CrEdValSelezionaTipo"), "DatiCr"));
        if (string.IsNullOrWhiteSpace(ChangeRequest.Titolo)) mancanti.Add(new(T("CrEdValTitoloObbl"), "DatiCr"));
        if (!ChangeRequest.HaAmbito) mancanti.Add(new(T("CrEdValAmbitoObbl"), "AmbitoImpattato"));
        var duplicati = TrovaPossibiliDuplicati();
        if (duplicati.Count > 0 && string.IsNullOrWhiteSpace(ChangeRequest.MotivazioneDuplicato))
            mancanti.Add(new(TF("CrEdValDuplicatoFmt", string.Join(", ", duplicati)), "MotivazioneDuplicato"));
        if (perImplementazione && string.IsNullOrWhiteSpace(ChangeRequest.Responsabile)) mancanti.Add(new(T("CrEdValResponsabileObbl"), "DatiCr"));
        if (perImplementazione && string.IsNullOrWhiteSpace(ChangeRequest.Descrizione)) mancanti.Add(new(T("CrEdValDescrizioneObbl"), "DatiCr"));
        foreach (var campo in CampiCr.Where(c => c.Obbligatorio && string.IsNullOrWhiteSpace(c.Valore))) mancanti.Add(new(TF("CrEdValCampoObblFmt", campo.Nome), "DatiCr"));
        CampiMancanti = mancanti;
        var motivazioneDuplicatoRichiesta = duplicati.Count > 0 && string.IsNullOrWhiteSpace(ChangeRequest.MotivazioneDuplicato);
        if (MotivazioneDuplicatoRichiesta != motivazioneDuplicatoRichiesta)
        {
            MotivazioneDuplicatoRichiesta = motivazioneDuplicatoRichiesta;
            OnPropertyChanged(nameof(MotivazioneDuplicatoRichiesta));
        }
        Errore = string.Join(Environment.NewLine, mancanti.Select(m => m.Messaggio));
        if (mancanti.Count > 0) RichiestaMostraCampiMancanti?.Invoke();
        return mancanti.Count == 0;
    }

    private List<string> TrovaPossibiliDuplicati() =>
        ChangeRequestDuplicatiService.TrovaPossibiliDuplicati(_state.ChangeRequests.PeekAllReadOnly(), ChangeRequest);

    /// <summary>Richiede la copertura dopo una modifica che la riguarda. Il risultato precedente resta valido finche' il nuovo giro
    /// non e' applicato; prima di salvare il giro in attesa si completa in linea (vedi AssicuraGiroCompletato).</summary>
    private void AggiornaValutazione(string? feedback = null)
    {
        _messaggioDopoRicalcolo = feedback;
        if (feedback != null) Messaggio = feedback;
        AvviaGiro(iniziale: false);
    }

    /// <summary>Avvia un giro: cataloghi (se non ancora letti) e copertura, calcolati su una copia della CR. Nei test (nessuna UI) il
    /// giro gira sul thread chiamante e il risultato e' gia' applicato al ritorno; nella UI gira su un thread di pool.</summary>
    private void AvviaGiro(bool iniziale)
    {
        var richiesta = CatturaGiro(iniziale, asincrono: InBackground);
        _giroInAttesa = true;
        _giroIniziale = iniziale;
        CaricamentoInCorso = true;
        if (!richiesta.Asincrono)
        {
            ApplicaGiro(richiesta, LeggiGiro(richiesta));
            return;
        }
        _giro = EseguiGiroInBackground(richiesta);
    }

    /// <summary>Cattura sul thread UI: copia della CR per la copertura, firma dei dati da cui dipende, generazione nuova (supera i giri in corso).</summary>
    private RichiestaGiroCr CatturaGiro(bool iniziale, bool asincrono) => new()
    {
        Generazione = ++_generazioneGiro,
        Asincrono = asincrono,
        Iniziale = iniziale,
        CaricaCataloghi = !_cataloghiCaricati,
        Copia = Clona(ChangeRequest),
        Firma = FirmaValutazione(ChangeRequest),
    };

    /// <summary>Letture e calcolo del giro: lavora sulla copia della CR e sui dati del repository, senza toccare lo stato della vista
    /// (puo' girare sul pool). I cataloghi si leggono dentro una sola lettura logica dei master (MasterLetturaScope).</summary>
    private RisultatoGiroCr LeggiGiro(RichiestaGiroCr r)
    {
        Cataloghi? cataloghi = null;
        if (r.CaricaCataloghi)
        {
            using var letturaMaster = MasterLetturaScope.Apri();
            cataloghi = LeggiCataloghi();
        }
        var test = _state.TestCases.GetAll();
        var copia = r.Copia;
        // Stessa sequenza di prima: prima l'esito per il messaggio (senza scrivere), poi stato e snapshot dei link.
        var esito = ChangeRequestWorkflowService.Valuta(copia, test, aggiornaSnapshot: false);
        var statoCambiato = ChangeRequestWorkflowService.RicalcolaStato(copia, test);
        return new RisultatoGiroCr(cataloghi, esito, copia.Stato, statoCambiato, copia.TestValidazione);
    }

    private Cataloghi LeggiCataloghi()
    {
        // Ogni catalogo si legge una sola volta anche quando serve a due liste (template e veicoli).
        var template = _state.Master.TestTemplate.Load();
        var veicoli = _state.Master.Veicoli.Load();
        return new Cataloghi(
            TemplateCr: template.Where(t => !t.Archiviata && t.IsChangeRequest).OrderBy(t => t.Nome).ToList(),
            TemplateTest: template.Where(t => !t.Archiviata && !t.IsChangeRequest).OrderBy(t => t.Nome).ToList(),
            VeicoliNomi: veicoli.Where(v => !v.Archiviata).Select(v => v.Nome).OrderBy(x => x).ToList(),
            EcuMaster: _state.Master.Componente.Load(),
            StrumentiNomi: _state.Master.Strumenti.Load().Where(v => !v.Archiviata).Select(v => v.Nome).OrderBy(x => x).ToList(),
            GammeNomi: veicoli.Where(v => !v.Archiviata).Select(v => v.GammaNome).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x).ToList(),
            OperatoriNomi: _state.Master.Operatori.Load().Where(o => !o.Disattivo).Select(o => o.Nome).OrderBy(x => x).ToList());
    }

    private async Task EseguiGiroInBackground(RichiestaGiroCr r)
    {
        RisultatoGiroCr? risultato = null;
        Exception? errore = null;
        try { risultato = await Task.Run(() => { _gateCaricamentoTest?.Invoke(); return LeggiGiro(r); }); }
        catch (Exception ex) { errore = ex; }
        await SuThreadUi(() =>
        {
            if (errore != null || risultato == null)
            {
                CrashLogService.Registra("Change request: caricamento editor", errore ?? new InvalidOperationException("Giro senza risultato."));
                if (r.Generazione != _generazioneGiro) return;
                _messaggioDopoRicalcolo = null;
                UltimaEccezioneCaricamento = errore;
                CaricamentoInCorso = false;
                return;
            }
            // Superato da un giro piu' recente o dal salvataggio: il risultato non si applica (lo ha gia' sostituito chi l'ha superato).
            if (r.Generazione != _generazioneGiro)
            {
                GiriScartati++;
                return;
            }
            try { ApplicaGiro(r, risultato); }
            catch (Exception ex)
            {
                _messaggioDopoRicalcolo = null;
                UltimaEccezioneCaricamento = ex;
                CaricamentoInCorso = false;
                CrashLogService.Registra("Change request: applicazione caricamento editor", ex);
            }
        });
    }

    /// <summary>Applicazione sul thread UI: prima i cataloghi, poi la copertura. La copertura si applica solo se la CR non e' cambiata
    /// dopo la cattura; altrimenti si ricalcola sul valore attuale, cosi' nessuna copertura superata finisce nella CR.</summary>
    private void ApplicaGiro(RichiestaGiroCr r, RisultatoGiroCr risultato)
    {
        if (risultato.Cataloghi != null) ApplicaCataloghi(risultato.Cataloghi);
        if (FirmaValutazione(ChangeRequest) != r.Firma)
        {
            ApplicaGiroSincrono(r.Iniziale);
            return;
        }
        ApplicaValutazione(risultato, segnaModifica: !r.Iniziale);
        _giroInAttesa = false;
        CaricamentoInCorso = false;
    }

    /// <summary>Completa in linea, sul valore attuale della CR, un giro: usato prima di salvare o esportare e quando la CR e'
    /// cambiata dopo la cattura. Il giro in background superato viene scartato (la generazione cambia).</summary>
    private void ApplicaGiroSincrono(bool iniziale)
    {
        var richiesta = CatturaGiro(iniziale, asincrono: false);
        ApplicaGiro(richiesta, LeggiGiro(richiesta));
    }

    /// <summary>Prima di salvare o esportare: se cataloghi o copertura sono ancora in attesa, si completano qui. Il salvataggio
    /// non usa mai una copertura superata.</summary>
    private bool AssicuraGiroCompletato()
    {
        if (!_giroInAttesa) return true;
        try
        {
            ApplicaGiroSincrono(_giroIniziale);
            return true;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Change request: completamento caricamento prima di salvare", ex);
            Errore = TF("SalvataggioNonRiuscitoFmt", ex.Message);
            return false;
        }
    }

    private void ApplicaCataloghi(Cataloghi c)
    {
        TemplateCr = c.TemplateCr;
        TemplateTest = c.TemplateTest;
        VeicoliDisponibili = c.VeicoliNomi;
        _tutteLeEcu = c.EcuMaster;
        StrumentiDisponibili = c.StrumentiNomi;
        GammeDisponibili = c.GammeNomi;
        OperatoriDisponibili = c.OperatoriNomi;
        OnPropertyChanged(nameof(TemplateCr)); OnPropertyChanged(nameof(TemplateTest)); OnPropertyChanged(nameof(VeicoliDisponibili));
        OnPropertyChanged(nameof(EcuDisponibili)); OnPropertyChanged(nameof(StrumentiDisponibili)); OnPropertyChanged(nameof(GammeDisponibili));
        OnPropertyChanged(nameof(OperatoriDisponibili));
        if (_cataloghiCaricati) return;
        _cataloghiCaricati = true;
        // Primo caricamento: template e tipo di Test predefiniti come prima del giro, senza contare come modifica non salvata.
        // Un valore gia' scelto (es. dal chiamante appena dopo la costruzione) non viene sostituito dal predefinito.
        var eraInizializzazione = _inizializzazione;
        _inizializzazione = true;
        try { TemplateSelezionato ??= TemplateCr.FirstOrDefault(t => t.Uid == ChangeRequest.TemplateUid) ?? TemplateCr.FirstOrDefault(); }
        finally { _inizializzazione = eraInizializzazione; }
        TemplateTestDaCreare ??= TemplateTest.FirstOrDefault();
    }

    /// <summary>Scrive sulla CR la copertura calcolata sulla sua copia: messaggio, stato e snapshot dei link. La lista dei link ha lo
    /// stesso ordine della copia perche' la firma ne garantisce l'uguaglianza.</summary>
    private void ApplicaValutazione(RisultatoGiroCr risultato, bool segnaModifica)
    {
        Messaggio = _messaggioDopoRicalcolo
            ?? (risultato.Esito.Completa ? T("CrEdCoperturaCompleta") : string.Join(Environment.NewLine, risultato.Esito.Mancanze));
        _messaggioDopoRicalcolo = null;
        ChangeRequest.Stato = risultato.Stato;
        for (var i = 0; i < risultato.Link.Count && i < ChangeRequest.TestValidazione.Count; i++)
        {
            ChangeRequest.TestValidazione[i].EsecuzioneValidante = risultato.Link[i].EsecuzioneValidante;
            ChangeRequest.TestValidazione[i].SnapshotValidazione = risultato.Link[i].SnapshotValidazione;
        }
        if (segnaModifica && risultato.StatoCambiato) ModificheNonSalvate = true;
        NotificaStato();
    }

    /// <summary>Dati da cui dipende la copertura: se cambiano dopo la cattura di un giro, il suo risultato non e' piu' applicabile.</summary>
    private static string FirmaValutazione(ChangeRequest cr) => EntityStateFingerprint.Compute(new
    {
        cr.Stato, cr.ImplementataIl, cr.VeicoliImpattati, cr.EcuImpattate, cr.StrumentiImpattati, cr.TestValidazione,
    });

    private sealed class RichiestaGiroCr
    {
        public required int Generazione { get; init; }
        public required bool Asincrono { get; init; }
        public required bool Iniziale { get; init; }
        public required bool CaricaCataloghi { get; init; }
        public required ChangeRequest Copia { get; init; }
        public required string Firma { get; init; }
    }

    private sealed record RisultatoGiroCr(Cataloghi? Cataloghi, EsitoValidazioneCr Esito, StatoChangeRequest Stato, bool StatoCambiato,
        IReadOnlyList<CollegamentoValidazioneCr> Link);

    private sealed record Cataloghi(List<TestTemplate> TemplateCr, List<TestTemplate> TemplateTest, List<string> VeicoliNomi,
        List<Componente> EcuMaster, List<string> StrumentiNomi, List<string> GammeNomi, List<string> OperatoriNomi);

    private void NotificaInteressati(string messaggio)
    {
        var destinatari = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ChangeRequest.Responsabile)) destinatari.Add(ChangeRequest.Responsabile);
        foreach (var link in ChangeRequest.TestValidazione)
        {
            var test = _state.TestCases.GetById(link.TestId);
            if (!string.IsNullOrWhiteSpace(test?.Assegnatario)) destinatari.Add(test.Assegnatario);
        }
        foreach (var master in _state.Master.Operatori.Load().Where(o => !o.Disattivo && o.Ruolo == RuoloOperatore.Master)) destinatari.Add(master.Nome);
        foreach (var destinatario in destinatari)
            _state.Notifiche.Add(new NotificaApp { Destinatario = destinatario, Tipo = "ChangeRequest", Titolo = $"{ChangeRequest.Id}: {StatoTesto}", Messaggio = messaggio, ElementoId = ChangeRequest.Id });
    }
    private void SincronizzaStatoSelezionato()
    {
        _statoSelezionato = ChangeRequest.Stato;
        _statoSelezioneManuale = false;
        OnPropertyChanged(nameof(StatoSelezionato));
    }

    private void NotificaStato()
    {
        if (!_statoSelezioneManuale)
        {
            if (_statoSelezionato != ChangeRequest.Stato)
            {
                _statoSelezionato = ChangeRequest.Stato;
                OnPropertyChanged(nameof(StatoSelezionato));
            }
        }
        else if (_statoSelezionato == ChangeRequest.Stato)
        {
            _statoSelezioneManuale = false;
        }

        OnPropertyChanged(nameof(StatoTesto));
        OnPropertyChanged(nameof(RichiedeMotivoTransizione));
        OnPropertyChanged(nameof(HaAzioniDiChiusura));
        OnPropertyChanged(nameof(PuoEntrareInModifica));
        OnPropertyChanged(nameof(StatiWorkflow));
        OnPropertyChanged(nameof(PuoModificare));
        OnPropertyChanged(nameof(PuoImplementare));
        OnPropertyChanged(nameof(PuoChiudere));
        OnPropertyChanged(nameof(PuoRiaprire));
        OnPropertyChanged(nameof(PuoSalvare));
        OnPropertyChanged(nameof(PuoSelezionareStato));
        OnPropertyChanged(nameof(PuoArchiviare));
        OnPropertyChanged(nameof(PuoRecuperareBozza));
        OnPropertyChanged(nameof(PuoRecuperareBozzaNuova));
        OnPropertyChanged(nameof(PuoRecuperareBozzaLocale));
        CaricaCampiCr();
    }
    [RelayCommand] private void TornaAllaLista() => _torna();

    /// <summary>Ctrl+S (Addendum: audit UX 2026-08-26, punto 6). Passa da SalvaCommand, quindi mantiene la
    /// validazione. Esc NON e' invece agganciato qui: a differenza dell'editor Test, TornaAllaLista non chiede
    /// conferma prima di uscire, e legare un tasto cosi' facile da premere per sbaglio a un'uscita silenziosa
    /// farebbe perdere lavoro. Da rivalutare se/quando anche questo editor avra' la conferma di abbandono.</summary>
    bool IScorciatoiePagina.SalvaDaTastiera()
    {
        if (!PuoModificare) return false;
        SalvaCommand.Execute(null);
        return true;
    }

    private static ChangeRequest Clona(ChangeRequest cr) => System.Text.Json.JsonSerializer.Deserialize<ChangeRequest>(System.Text.Json.JsonSerializer.Serialize(cr, JsonIo.Options), JsonIo.Options)!;
    private static List<T> ClonaLista<T>(IEnumerable<T> items) => System.Text.Json.JsonSerializer.Deserialize<List<T>>(System.Text.Json.JsonSerializer.Serialize(items, JsonIo.Options), JsonIo.Options)!;
}
