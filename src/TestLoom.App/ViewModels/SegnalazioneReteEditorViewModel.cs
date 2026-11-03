using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Un campo tecnico dello strumento segnalato, stesso principio di CampoCrRiga (ChangeRequestEditorViewModel)
/// ma con chiave diretta sul Nome del campo: qui esiste un solo strumento per segnalazione, non serve comporre
/// una chiave "NomeStrumento::NomeCampo" come su TestCase.ValoriStrumentiTest.</summary>
public sealed class CampoSegnalazioneReteRiga : ViewModelBase
{
    private readonly Dictionary<string, string> _valori;
    private readonly Action _onCambiato;
    public DefinizioneCampoStrumento Campo { get; }
    public string Nome => Campo.Nome;
    public bool ECheckbox => Campo.Tipo == TipoCampoStrumento.Checkbox;
    public bool ELista => Campo.Tipo == TipoCampoStrumento.Lista;
    public bool ETesto => !ECheckbox && !ELista;
    public List<string> Opzioni => Campo.OpzioniLista;
    public string UnitaMisura => Campo.UnitaMisura ?? "";

    public string Valore
    {
        get => _valori.GetValueOrDefault(Campo.Nome) ?? "";
        set
        {
            if (value == Valore) return;
            if (string.IsNullOrWhiteSpace(value)) _valori.Remove(Campo.Nome);
            else _valori[Campo.Nome] = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValoreCheckbox));
            _onCambiato();
        }
    }

    public bool ValoreCheckbox { get => Valore == "true"; set => Valore = value ? "true" : "false"; }

    public CampoSegnalazioneReteRiga(DefinizioneCampoStrumento campo, Dictionary<string, string> valori, Action onCambiato)
    {
        Campo = campo;
        _valori = valori;
        _onCambiato = onCambiato;
    }
}

/// <summary>
/// Editor per una Non Conformità nata SENZA un Test, tipicamente una segnalazione arrivata direttamente dalla
/// rete assistenziale (feedback utente 2026-09-08/2026-09-15). Porta i propri componente/Veicolo/Funzionalità/Strumento
/// invece di ereditarli da un TestCase inesistente — NON il Requisito, esclusivo dei Test. Nessuna meccanica di
/// esecuzione/condizione: la NC/CR serve qui a tracciare bug e richieste di miglioramento sia da Test sia da
/// segnalazioni di rete, non a rieseguire nulla.
/// </summary>
public partial class SegnalazioneReteEditorViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly Action _torna;
    private readonly long? _revisioneAttesa;
    private readonly NonConformita? _snapshotIniziale;

    [ObservableProperty] private NonConformita nc;
    [ObservableProperty] private string strumentoSelezionato = "";
    [ObservableProperty] private string? messaggioErrore;
    [ObservableProperty] private string? messaggio;
    [ObservableProperty] private bool modificheNonSalvate;
    [ObservableProperty] private bool caricamentoAllegatoInCorso;
    [ObservableProperty] private string? nomeAllegatoInCaricamento;

    private TaskCompletionSource? _completamentoCaricamentoAllegato;

    internal Func<Task>? GateCaricamentoAllegatoPerTest { get; set; }
    public bool PuoAggiungereAllegati => PuoModificare && !CaricamentoAllegatoInCorso;
    public bool PuoSalvare => PuoModificare && !CaricamentoAllegatoInCorso;
    public bool PuoAnnullare => !CaricamentoAllegatoInCorso;

    partial void OnCaricamentoAllegatoInCorsoChanged(bool value)
    {
        OnPropertyChanged(nameof(PuoAggiungereAllegati));
        OnPropertyChanged(nameof(PuoSalvare));
        OnPropertyChanged(nameof(PuoAnnullare));
    }

    /// <summary>La navigazione/chiusura attende la copia avviata prima di applicare la conferma di abbandono.</summary>
    public async Task AttendiCaricamentoAllegatoAsync()
    {
        while (_completamentoCaricamentoAllegato is { } completamento)
            await completamento.Task;
    }

    public bool HaMessaggioErrore => !string.IsNullOrEmpty(MessaggioErrore);
    public bool HaMessaggio => !string.IsNullOrEmpty(Messaggio);
    partial void OnMessaggioErroreChanged(string? value) => OnPropertyChanged(nameof(HaMessaggioErrore));
    partial void OnMessaggioChanged(string? value)
    {
        OnPropertyChanged(nameof(HaMessaggio));
        // Messaggio puramente informativo: scade da solo (solo se nel frattempo non e' stato sostituito).
        if (!string.IsNullOrEmpty(value))
            AutoScadenzaMessaggio.Programma(() => { if (Messaggio == value) Messaggio = null; });
    }

    [RelayCommand] private void ChiudiMessaggio() => Messaggio = null;
    [RelayCommand] private void ChiudiMessaggioErrore() => MessaggioErrore = null;

    /// <summary>Vero se e' stato scelto uno strumento: proprieta' bool esplicita per IsVisible dei pulsanti
    /// (un binding diretto string->bool non e' una conversione affidabile).</summary>
    public bool HaStrumentoSelezionato => !string.IsNullOrWhiteSpace(StrumentoSelezionato);

    /// <summary>Campi obbligatori mancanti dall'ultima validazione (feedback utente 2026-09-15: "deve comparire
    /// anche qua il pop-up per ciò che manca ed è obbligatorio"), stesso CampoMancante/CampiMancantiDialog già
    /// usati da Test e Change Request.</summary>
    public List<CampoMancante> CampiMancanti { get; private set; } = new();
    public event Action? RichiestaMostraCampiMancanti;

    [ObservableProperty] private string nuovoNumeroPratica = "";
    [ObservableProperty] private string nuovoVin = "";
    [ObservableProperty] private string nuovaLicenza = "";

    public ObservableCollection<string> NumeriPratica { get; } = new();
    public ObservableCollection<string> VinCliente { get; } = new();

    /// <summary>Feedback utente 2026-09-16: "deve esserci anche licenza e possono esserci più licenze" — stesso
    /// principio di NumeriPratica/VinCliente sopra, obbligatorio (almeno una) e a valori multipli, invece del
    /// campo libero "Licence"/"Licenza" definito dentro lo strumento (che resta nascosto dalla stessa logica
    /// di ECampoOrigineSegnalazione già in uso per "Asist").</summary>
    public ObservableCollection<string> Licenze { get; } = new();
    public ObservableCollection<string> Componente { get; } = new();
    public ObservableCollection<string> Veicoli { get; } = new();
    public ObservableCollection<string> Funzionalita { get; } = new();
    public ObservableCollection<CampoSegnalazioneReteRiga> CampiStrumento { get; } = new();

    /// <summary>Operazioni della segnalazione (feedback utente 2026-09-18): una riga per coppia Componente×
    /// Funzionalità, ciascuna con i propri campi Ambito=PerOperazione — vedi OperazioneSegnalazione. Wrapper
    /// ObservableCollection su Nc.OperazioniSegnalate (un semplice List&lt;T&gt;), stesso schema di
    /// TestCaseEditorViewModel.CondizioniVisual/SincronizzaCondizioniVisual.</summary>
    public ObservableCollection<OperazioneSegnalazione> Operazioni { get; } = new();
    public bool HaOperazioni => Operazioni.Count > 0;
    public ObservableCollection<Allegato> AllegatiVisual { get; } = new();

    /// <summary>Ticket TICKET/Ticketing (feedback utente 2026-09-16: "dovrebbe poi essere possibile anche
    /// collegare un Ticketing") — CollegamentiPolarion esisteva già sul modello NonConformita (usato solo
    /// dall'export), ma questo editor non aveva alcuna UI per popolarlo: stesso schema add/apri/rimuovi già
    /// in uso in ChangeRequestEditorViewModel.AggiungiTicket/ApriTicket/RimuoviTicket.</summary>
    [ObservableProperty] private string nuovoTicketRiferimento = "";
    [ObservableProperty] private string nuovoTicketUrl = "";
    public ObservableCollection<CollegamentoPolarion> TicketCollegatiVisual { get; } = new();

    public List<string> EcuDisponibili { get; }
    public List<string> VeicoliDisponibili { get; }

    private readonly List<Funzionalita> _tutteLeFunzionalita;

    /// <summary>componente attive con il loro montaggio: serve al filtro Funzionalità per coppie componente-veicolo (vedi FunzionalitaDisponibili).</summary>
    private readonly List<Componente> _tutteLeEcu;

    /// <summary>Interruttore "mostra tutte" (feedback utente 2026-09-23), stesso principio di
    /// TestCaseEditorViewModel.MostraTutteLeFunzionalita: il filtro per componente scelta non deve mai bloccare
    /// l'operatore quando Funzionalita.EcuAssociate è incompleto o sbagliato.</summary>
    [ObservableProperty] private bool mostraTutteLeFunzionalita;
    partial void OnMostraTutteLeFunzionalitaChanged(bool value) => OnPropertyChanged(nameof(FunzionalitaDisponibili));

    /// <summary>Filtrata per i componenti già scelte sulla segnalazione (feedback utente 2026-09-23), stesso
    /// principio/stessa logica di TestCaseEditorViewModel.FunzionalitaDisponibili: con almeno un componente scelta
    /// mostra solo le funzionalità associate a QUALUNQUE di quelle componente più quelle generiche (EcuAssociate
    /// vuoto), le Funzionalità già scelte restano comunque sempre visibili.</summary>
    public List<string> FunzionalitaDisponibili
    {
        get
        {
            var attive = _tutteLeFunzionalita.Where(f => !f.Archiviata);
            if (MostraTutteLeFunzionalita || Componente.Count == 0)
                return attive.Select(f => f.Nome).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            var ecuScelte = Componente.ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Stessa regola di TestCaseEditorViewModel.FunzionalitaDisponibili: con veicoli scelti vale su almeno una coppia
            // (componente scelta, veicolo scelto), restrizioni per componente comprese (Funzionalita.VeicoliPerEcu) intersecate con il montaggio.
            var veicoliScelti = Veicoli.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            var ecuCatalogo = _tutteLeEcu.GroupBy(e => e.Nome, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            bool ValeSuCoppia(Funzionalita f, string ecuNome, string veicolo) =>
                ecuCatalogo.TryGetValue(ecuNome, out var catalogo)
                    ? AssociazioniVeicoloService.ValeSuVeicoloMontato(f, catalogo, veicolo)
                    : f.ValeSu(ecuNome, veicolo);
            var filtrate = attive
                .Where(f => ecuScelte.Any(componente => veicoliScelti.Count == 0
                    ? f.ValeSuEcu(componente)
                    : veicoliScelti.Any(veicolo => ValeSuCoppia(f, componente, veicolo))))
                .Select(f => f.Nome)
                .ToList();
            foreach (var nome in Funzionalita)
                if (!filtrate.Contains(nome, StringComparer.OrdinalIgnoreCase)) filtrate.Add(nome);
            return filtrate.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public List<string> StrumentiDisponibili { get; }
    public List<string> OperatoriDisponibili { get; }
    public Array SeveritaDisponibili => Enum.GetValues(typeof(SeveritaAnomalia));
    public Array PrioritaDisponibili => Enum.GetValues(typeof(PrioritaOperativa));
    public Array StatiDisponibili => Nc.StatoWorkflow == StatoAnomalia.Conclusa
        ? Enum.GetValues(typeof(StatoAnomalia))
        : Enum.GetValues(typeof(StatoAnomalia)).Cast<StatoAnomalia>().Where(s => s != StatoAnomalia.Conclusa).ToArray();

    public bool IsNew => string.IsNullOrWhiteSpace(Nc.Id);
    public bool PuoRecuperareBozza => !IsNew && PuoModificare && _snapshotIniziale != null &&
        _revisioneAttesa.HasValue && !ModificheNonSalvate;

    /// <summary>Editor di una segnalazione di rete mai salvata (ID vuoto, nessuna fotografia iniziale): puo importare una bozza locale senza base.</summary>
    public bool PuoRecuperareBozzaNuova => IsNew && PuoModificare && _snapshotIniziale == null && !ModificheNonSalvate;

    /// <summary>Pulsante "Recupera bozza locale…": vale sia per una segnalazione esistente (con base) sia per una nuova.</summary>
    public bool PuoRecuperareBozzaLocale => PuoRecuperareBozza || PuoRecuperareBozzaNuova;
    public string WorkspaceRoot => _state.Paths.Root;
    private bool _inModifica = true;
    private Action? _entraInModifica;
    /// <summary>False quando l'editor e' aperto da "Apri": tutto in sola lettura finche' non si preme "Modifica"
    /// (che riapre un editor nuovo, riletto dall'archivio, in modifica).</summary>
    public bool InModifica => _inModifica;
    public bool PuoEntrareInModifica => !_inModifica && _state.PuoScrivere;
    public bool MostraSolaLetturaHint => !_inModifica;

    public void ImpostaSolaLetturaIniziale(Action? entraInModifica)
    {
        _entraInModifica = entraInModifica;
        _inModifica = false;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void EntraInModifica()
    {
        if (!PuoEntrareInModifica) return;
        if (_entraInModifica != null) { _entraInModifica(); return; }
        _inModifica = true;
        OnPropertyChanged(string.Empty);
    }

    public bool PuoModificare => _inModifica && _state.PuoScrivere;

    /// <summary>Conclusa non e' selezionabile dalla combo: si chiude solo col pulsante Chiudi (motivo obbligatorio).</summary>
    public bool PuoCambiareStato => PuoModificare && Nc.StatoWorkflow != StatoAnomalia.Conclusa;
    public bool PuoChiudere => PuoModificare && !IsNew && Nc.StatoWorkflow != StatoAnomalia.Conclusa && !ModificheNonSalvate;
    public bool PuoRiaprire => PuoModificare && !IsNew && Nc.StatoWorkflow == StatoAnomalia.Conclusa && !ModificheNonSalvate;
    public bool MostraDataChiusura => !IsNew && Nc.StatoWorkflow == StatoAnomalia.Conclusa;
    public DateTimeOffset? DataChiusuraModificabile
    {
        get => Nc.DataChiusura is { } data ? new DateTimeOffset(data) : null;
        set
        {
            if (!PuoModificare || !MostraDataChiusura || !value.HasValue) return;
            var nuova = value.Value.Date + (Nc.DataChiusura?.TimeOfDay ?? TimeSpan.Zero);
            if (Nc.DataChiusura == nuova) return;
            Nc.DataChiusura = nuova;
            OnPropertyChanged();
            ModificheNonSalvate = true;
        }
    }
    public bool MostraAzioniChiusura => PuoChiudere || PuoRiaprire;
    [ObservableProperty] private string motivoChiusura = "";
    [ObservableProperty] private string motivoRiapertura = "";
    public string TitoloPagina => IsNew
        ? LocalizationService.Instance.Traduci("NuovaSegnalazioneRete")
        : Nc.Id;

    partial void OnModificheNonSalvateChanged(bool value)
    {
        OnPropertyChanged(nameof(PuoRecuperareBozza));
        OnPropertyChanged(nameof(PuoRecuperareBozzaNuova));
        OnPropertyChanged(nameof(PuoRecuperareBozzaLocale));
        OnPropertyChanged(nameof(PuoChiudere));
        OnPropertyChanged(nameof(PuoRiaprire));
        OnPropertyChanged(nameof(MostraAzioniChiusura));
    }

    /// <summary>Altre NC nate dalla stessa segnalazione originale, quando questa fa parte di uno split multi-
    /// funzionalità (feedback utente 2026-09-16: "riferimento incrociato... Segnalazione collegata: NC-0041,
    /// NC-0042"). Calcolata una volta all'apertura: puramente informativa, non richiede di restare aggiornata
    /// in tempo reale mentre l'editor è aperto.</summary>
    public List<string> SegnalazioniSorelle { get; }
    public bool HaSegnalazioniSorelle => SegnalazioniSorelle.Count > 0;

    /// <summary>Descrizione dettagliata (obbligatoria): riusa il campo Descrizione già condiviso da tutte le
    /// NC (liste, report, export) invece di un terzo campo descrittivo separato.</summary>
    public string Descrizione { get => Nc.Descrizione; set { if (value == Nc.Descrizione) return; Nc.Descrizione = value; OnPropertyChanged(); ModificheNonSalvate = true; } }
    public string DescrizioneBreve { get => Nc.DescrizioneBreveSegnalata; set { if (value == Nc.DescrizioneBreveSegnalata) return; Nc.DescrizioneBreveSegnalata = value; OnPropertyChanged(); ModificheNonSalvate = true; } }
    public string ConcessionariaOfficina { get => Nc.ConcessionariaOfficina; set { if (value == Nc.ConcessionariaOfficina) return; Nc.ConcessionariaOfficina = value; OnPropertyChanged(); ModificheNonSalvate = true; } }
    public string Owner { get => Nc.Owner; set { if (value == Nc.Owner) return; Nc.Owner = value; OnPropertyChanged(); ModificheNonSalvate = true; } }
    public SeveritaAnomalia? Severita { get => Nc.Severita; set { if (value == Nc.Severita) return; Nc.Severita = value; OnPropertyChanged(); ModificheNonSalvate = true; } }

    /// <summary>Nullable qui a differenza di NonConformita.Priorita (da cui parte già valorizzata a Normal):
    /// la Priorità è obbligatoria per una segnalazione di rete (feedback utente 2026-09-15), quindi una NUOVA
    /// segnalazione parte deliberatamente senza selezione invece di ereditare in silenzio un default mai scelto
    /// dall'operatore. Una volta scelta si scrive normalmente su Nc.Priorita (mai nullo lì).</summary>
    [ObservableProperty] private PrioritaOperativa? prioritaScelta;
    partial void OnPrioritaSceltaChanged(PrioritaOperativa? value)
    {
        if (value.HasValue) Nc.Priorita = value.Value;
        ModificheNonSalvate = true;
    }

    /// <summary>Nullable per lo stesso motivo di prioritaScelta (feedback utente 2026-09-16: "rendilo una
    /// scelta esplicita"): prima ogni nuova segnalazione partiva silenziosamente su StatoAnomalia.Confermata
    /// senza che l'operatore lo scegliesse. Tutti gli 11 stati del workflow restano selezionabili anche alla
    /// creazione (nessuna restrizione sui valori iniziali ammessi).</summary>
    [ObservableProperty] private StatoAnomalia? statoScelta;
    partial void OnStatoSceltaChanged(StatoAnomalia? value)
    {
        if (value.HasValue) Nc.StatoWorkflow = value.Value;
        ModificheNonSalvate = true;
    }

    /// <summary>Callback opzionale per aprire un'altra segnalazione per Id (feedback utente 2026-09-16: il
    /// richiamo "Segnalazione collegata" verso le NC sorelle nate dallo stesso split era solo un'etichetta,
    /// non cliccabile — non esisteva un canale di navigazione diretta NC→NC). Passato da MainViewModel come lo
    /// stesso NavigaASegnalazioneRete già usato per aprire QUESTA pagina: nullo nei test, dove non serve.</summary>
    private readonly Action<string>? _apriSegnalazione;
    private readonly Action<string?>? _apriTest;

    public SegnalazioneReteEditorViewModel(AppState state, string? id, Action torna, Action<string>? apriSegnalazione = null,
        Action<string?>? apriTest = null)
    {
        _state = state;
        _torna = torna;
        _apriSegnalazione = apriSegnalazione;
        _apriTest = apriTest;

        var existing = string.IsNullOrWhiteSpace(id) ? null : state.NonConformita.GetById(id);
        Nc = existing == null
            ? new NonConformita { DaSegnalazioneRete = true, Owner = state.Config.OperatoreAttivo, DataApertura = DateTime.Now }
            : EntityStateFingerprint.Clone(existing);
        _revisioneAttesa = existing?.RevisioneDati;
        _snapshotIniziale = existing == null ? null : EntityStateFingerprint.Clone(existing);
        prioritaScelta = existing?.Priorita;
        // Nullo per una nuova segnalazione (scelta obbligatoria, feedback utente 2026-09-16), valorizzato con
        // lo stato già presente quando si modifica una segnalazione esistente.
        statoScelta = existing?.StatoWorkflow;

        _tutteLeEcu = state.CartellaValida ? state.Master.Componente.Load().Where(e => !e.Archiviata).ToList() : new();
        EcuDisponibili = _tutteLeEcu.Select(e => e.Nome).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        VeicoliDisponibili = state.CartellaValida ? state.Master.Veicoli.Load().Where(v => !v.Archiviata).Select(v => v.Nome).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : new();
        _tutteLeFunzionalita = state.CartellaValida ? state.Master.Funzionalita.Load() : new();
        StrumentiDisponibili = state.CartellaValida ? state.Master.Strumenti.Load().Where(s => !s.Archiviata).Select(s => s.Nome).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : new();
        OperatoriDisponibili = state.CartellaValida ? state.Master.Operatori.Load().Where(o => !o.Disattivo).Select(o => o.Nome).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : new();

        foreach (var x in Nc.NumeriPraticaRete) NumeriPratica.Add(x);
        foreach (var x in Nc.VinCliente) VinCliente.Add(x);
        foreach (var x in Nc.Licenze) Licenze.Add(x);
        foreach (var x in Nc.EcuSegnalate) Componente.Add(x);
        foreach (var x in Nc.VeicoliSegnalati) Veicoli.Add(x);
        foreach (var x in Nc.FunzionalitaSegnalate) Funzionalita.Add(x);
        foreach (var x in Nc.Allegati) AllegatiVisual.Add(x);
        foreach (var x in Nc.CollegamentiPolarion) TicketCollegatiVisual.Add(x);
        strumentoSelezionato = Nc.StrumentoSegnalato;
        RicostruisciCampiStrumento();

        SegnalazioniSorelle = Nc.GruppoSegnalazioneUid.HasValue && state.CartellaValida
            ? state.NonConformita.GetAll()
                .Where(n => n.GruppoSegnalazioneUid == Nc.GruppoSegnalazioneUid && n.Id != Nc.Id)
                .Select(n => n.Id).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
    }

    public NonConformita CaricaBozzaRecuperabile(FailedSaveDraftInspector.DraftRow row)
    {
        if (!PuoRecuperareBozza || ModificheNonSalvate || _snapshotIniziale == null || !_revisioneAttesa.HasValue)
            throw new InvalidOperationException(T("NcRecuperoSalvareAnnullarePrimaMsg"));
        return FailedSaveDraftRecoveryPreparationService.LoadNetworkNcForEditor(
            _state.Paths.Root, row, _snapshotIniziale, _revisioneAttesa.Value);
    }

    public void ApplicaBozzaRecuperata(NonConformita draft, IReadOnlyCollection<string> selectedFields)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(selectedFields);
        var selected = selectedFields.Distinct(StringComparer.Ordinal).ToArray();
        if (!PuoRecuperareBozza || ModificheNonSalvate || _snapshotIniziale == null ||
            draft.Uid != _snapshotIniziale.Uid || draft.Id != _snapshotIniziale.Id ||
            _revisioneAttesa.HasValue && draft.RevisioneDati > _revisioneAttesa.Value || selected.Length == 0 ||
            selected.Any(field => !FailedSaveDraftRecoveryPreparationService.EditableNetworkNcFields.ContainsKey(field)))
            throw new InvalidOperationException(T("NcRevisioneCambiataImportaMsg"));

        MostraNcStaged(StagingCampiNc(EntityStateFingerprint.Clone(_snapshotIniziale), EntityStateFingerprint.Clone(draft), selected));
        Messaggio = T("SegCampiSelezionatiCaricatiMsg");
        MessaggioErrore = null;
    }

    /// <summary>Copia sulla NC di base i soli campi di segnalazione indicati, dalla bozza o dal candidato.</summary>
    private static NonConformita StagingCampiNc(NonConformita copia, NonConformita sorgente, IEnumerable<string> campi)
    {
        foreach (var field in campi)
        {
            switch (field)
            {
                case nameof(NonConformita.Descrizione): copia.Descrizione = sorgente.Descrizione; break;
                case nameof(NonConformita.DescrizioneBreveSegnalata): copia.DescrizioneBreveSegnalata = sorgente.DescrizioneBreveSegnalata; break;
                case nameof(NonConformita.ConcessionariaOfficina): copia.ConcessionariaOfficina = sorgente.ConcessionariaOfficina; break;
                case nameof(NonConformita.Owner): copia.Owner = sorgente.Owner; break;
                case nameof(NonConformita.Severita): copia.Severita = sorgente.Severita; break;
                case nameof(NonConformita.Priorita): copia.Priorita = sorgente.Priorita; break;
                case nameof(NonConformita.StatoWorkflow): copia.StatoWorkflow = sorgente.StatoWorkflow; break;
                case nameof(NonConformita.DataChiusura): copia.DataChiusura = sorgente.DataChiusura; break;
                case nameof(NonConformita.EcuSegnalate): copia.EcuSegnalate = sorgente.EcuSegnalate; break;
                case nameof(NonConformita.VeicoliSegnalati): copia.VeicoliSegnalati = sorgente.VeicoliSegnalati; break;
                case nameof(NonConformita.FunzionalitaSegnalate): copia.FunzionalitaSegnalate = sorgente.FunzionalitaSegnalate; break;
                case nameof(NonConformita.StrumentoSegnalato): copia.StrumentoSegnalato = sorgente.StrumentoSegnalato; break;
                case nameof(NonConformita.ValoriStrumentoSegnalato): copia.ValoriStrumentoSegnalato = sorgente.ValoriStrumentoSegnalato; break;
                case nameof(NonConformita.OperazioniSegnalate): copia.OperazioniSegnalate = sorgente.OperazioniSegnalate; break;
                case nameof(NonConformita.NumeriPraticaRete): copia.NumeriPraticaRete = sorgente.NumeriPraticaRete; break;
                case nameof(NonConformita.VinCliente): copia.VinCliente = sorgente.VinCliente; break;
                case nameof(NonConformita.Licenze): copia.Licenze = sorgente.Licenze; break;
                case nameof(NonConformita.Allegati): copia.Allegati = sorgente.Allegati; break;
                case nameof(NonConformita.CollegamentiPolarion): copia.CollegamentiPolarion = sorgente.CollegamentiPolarion; break;
            }
        }
        return copia;
    }

    /// <summary>Mostra nell'editor una NC costruita da bozza e rinfresca liste, scelte e stato dei campi.</summary>
    private void MostraNcStaged(NonConformita staged)
    {
        Nc = staged;
        OnPropertyChanged(nameof(DataChiusuraModificabile));
        NumeriPratica.Clear(); foreach (var x in Nc.NumeriPraticaRete) NumeriPratica.Add(x);
        VinCliente.Clear(); foreach (var x in Nc.VinCliente) VinCliente.Add(x);
        Licenze.Clear(); foreach (var x in Nc.Licenze) Licenze.Add(x);
        Componente.Clear(); foreach (var x in Nc.EcuSegnalate) Componente.Add(x);
        Veicoli.Clear(); foreach (var x in Nc.VeicoliSegnalati) Veicoli.Add(x);
        Funzionalita.Clear(); foreach (var x in Nc.FunzionalitaSegnalate) Funzionalita.Add(x);
        AllegatiVisual.Clear(); foreach (var x in Nc.Allegati) AllegatiVisual.Add(x);
        TicketCollegatiVisual.Clear(); foreach (var x in Nc.CollegamentiPolarion) TicketCollegatiVisual.Add(x);
        Operazioni.Clear(); foreach (var x in Nc.OperazioniSegnalate) Operazioni.Add(x);
        PrioritaScelta = Nc.Priorita;
        StatoScelta = Nc.StatoWorkflow;
        // Assegnazione diretta al campo di backing, voluta: la proprieta' generata riattiverebbe
        // OnStrumentoSelezionatoChanged, che ricostruisce campi strumento e operazioni. Quella ricostruzione e' gia'
        // eseguita esplicitamente alla riga successiva, quindi passare dalla proprieta' la duplicherebbe.
#pragma warning disable MVVMTK0034
        strumentoSelezionato = Nc.StrumentoSegnalato;
#pragma warning restore MVVMTK0034
        RicostruisciCampiStrumento();
        foreach (var property in new[]
        {
            nameof(Descrizione), nameof(DescrizioneBreve), nameof(ConcessionariaOfficina), nameof(Owner),
            nameof(Severita), nameof(PrioritaScelta), nameof(StatoScelta), nameof(StrumentoSelezionato),
            nameof(HaStrumentoSelezionato), nameof(HaOperazioni), nameof(TitoloPagina), nameof(IsNew),
            nameof(PuoRecuperareBozza), nameof(PuoEsportarePacchetto), nameof(HaTestCollegatoLink)
        }) OnPropertyChanged(property);
        ModificheNonSalvate = true;
    }

    /// <summary>UID di tutte le NC nel repository, archiviate incluse: scarta dall'elenco le bozze di record gia esistenti.</summary>
    public IReadOnlySet<Guid> UidNcPresenti() => _state.NonConformita.PeekAllReadOnly().Select(n => n.Uid).ToHashSet();

    /// <summary>Legge una bozza di segnalazione di rete mai salvata senza scrivere nulla; il candidato si applica con <see cref="ApplicaBozzaNuovaRecuperata"/>.</summary>
    public NonConformita CaricaBozzaNuovaRecuperabile(FailedSaveDraftInspector.DraftRow row)
    {
        if (!PuoRecuperareBozzaNuova)
            throw new InvalidOperationException(T("SegRecuperoNuovaRichiedeEditorMsg"));
        var presenti = UidNcPresenti();
        return FailedSaveDraftRecoveryPreparationService.LoadNewNetworkNcForEditor(
            _state.Paths.Root, row, presenti.Contains);
    }

    /// <summary>Importa nell'editor nuovo tutti i campi di segnalazione del candidato: nessun ID assegnato, nessun salvataggio automatico.</summary>
    public void ApplicaBozzaNuovaRecuperata(NonConformita candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!PuoRecuperareBozzaNuova || !string.IsNullOrEmpty(candidate.Id))
            throw new InvalidOperationException(T("SegBozzaNonImportabileMsg"));
        var campi = FailedSaveDraftRecoveryPreparationService.EditableNetworkNcFields;
        var staged = StagingCampiNc(EntityStateFingerprint.Clone(Nc), EntityStateFingerprint.Clone(candidate), campi.Keys);
        staged.Uid = candidate.Uid;
        MostraNcStaged(staged);
        MessaggioErrore = null;
        Messaggio = T("SegCampiImportatiNuovoRecordPrefisso") + string.Join(", ", campi.Values) +
            T("SegNessunFileAssegnaIdSuffisso");
    }

    partial void OnStrumentoSelezionatoChanged(string value)
    {
        OnPropertyChanged(nameof(HaStrumentoSelezionato));
        Nc.StrumentoSegnalato = value;
        RicostruisciCampiStrumento();
        ModificheNonSalvate = true;
    }

    /// <summary>Filtra i campi già raccolti in "Origine segnalazione" (Numero ticket, matricola) quando anche lo
    /// strumento selezionato ne definisce uno equivalente (feedback utente 2026-09-15: "i campi già presenti
    /// in origine segnalazione... se anche presenti nello strumento non dovranno essere riproposti più volte").
    /// Confronto per PAROLA (non nome esatto): il caso reale che ha rotto il primo confronto a 4 stringhe
    /// esatte era lo strumento UnitLogger, il cui campo si chiama "Asist" — non "numero ticket"/"ticket"/"matricola"/
    /// "licenza" (feedback utente 2026-09-16) — e restava quindi duplicato. I campi tecnici sono definiti
    /// liberamente in Impostazioni → Strumenti senza alcun collegamento esplicito ai campi di NC, quindi un
    /// confronto per parola/prefisso è l'unico modo di tollerare grafie reali diverse (Asist, N. Ticket,
    /// Licence, Licenza...) senza dover elencare ogni variante possibile. "matricola" resta un confronto per PAROLA
    /// INTERA (non prefisso) per non intercettare per errore un campo come "Vincolo".</summary>
    private static readonly string[] PrefissiOrigineSegnalazione = { "ticket", "asist", "assist", "licen" };
    private static readonly string[] ParoleIntereOrigineSegnalazione = { "matricola" };

    private static IEnumerable<string> ParoleNormalizzate(string testo)
    {
        var minuscolo = testo.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(minuscolo.Length);
        foreach (var c in minuscolo)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Split(new[] { ' ', '-', '_', '.', '/', '\\', ':' }, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Internal (invece di private) solo per essere testabile direttamente da TestLoom.App.Tests
    /// (InternalsVisibleTo già configurato in AssemblyInfo.cs) senza dover passare da un StrumentoDiagnostico
    /// intero: la logica di confronto è pura e non dipende da AppState.</summary>
    internal static bool ECampoOrigineSegnalazione(string nomeCampo)
    {
        var parole = ParoleNormalizzate(nomeCampo);
        return parole.Any(p => ParoleIntereOrigineSegnalazione.Contains(p) || PrefissiOrigineSegnalazione.Any(pref => p.StartsWith(pref, StringComparison.Ordinal)));
    }

    private void RicostruisciCampiStrumento()
    {
        CampiStrumento.Clear();
        if (_state.CartellaValida && !string.IsNullOrWhiteSpace(StrumentoSelezionato))
        {
            var strumento = _state.Master.Strumenti.Load().FirstOrDefault(s => s.Nome.Equals(StrumentoSelezionato, StringComparison.OrdinalIgnoreCase));
            if (strumento != null)
                foreach (var campo in strumento.Campi)
                {
                    if (ECampoOrigineSegnalazione(campo.Nome)) continue;
                    // Ambito=PerOperazione va nell'operazione (Componente×Funzionalità) che lo riguarda, non qui:
                    // qui restano solo i campi stabili per l'intera segnalazione (feedback utente 2026-09-18).
                    if (campo.Ambito == TipoAmbitoCampo.PerOperazione) continue;
                    CampiStrumento.Add(new CampoSegnalazioneReteRiga(campo, Nc.ValoriStrumentoSegnalato, () => ModificheNonSalvate = true));
                }
        }
        AggiornaTutteLeOperazioni();
    }

    /// <summary>Ricalcola i campi PerOperazione visibili su OGNI operazione esistente: chiamato quando cambia
    /// lo strumento selezionato (i campi disponibili cambiano con lui), non quando cambia una singola
    /// operazione — stesso principio di AggiornaCampiStrumenti in TestCaseEditorViewModel.</summary>
    private void AggiornaTutteLeOperazioni()
    {
        foreach (var operazione in Nc.OperazioniSegnalate) AggiornaCampiVisualOperazione(operazione);
        SincronizzaOperazioniVisual();
    }

    private void AggiornaCampiVisualOperazione(OperazioneSegnalazione operazione)
    {
        var strumento = _state.CartellaValida && !string.IsNullOrWhiteSpace(StrumentoSelezionato)
            ? _state.Master.Strumenti.Load().FirstOrDefault(s => s.Nome.Equals(StrumentoSelezionato, StringComparison.OrdinalIgnoreCase))
            : null;
        var strumenti = strumento == null ? Array.Empty<StrumentoDiagnostico>() : new[] { strumento };
        var (diretti, gruppi) = CampoStrumentoValoreLayout.SpaccaPerGruppo(
            strumenti, operazione.ValoriStrumenti, TipoAmbitoCampo.PerOperazione, _state.OperatoreCorrente?.ValoriDefaultCampi);
        operazione.CampiVisual = diretti;
        operazione.GruppiVisual = gruppi;
    }

    private void SincronizzaOperazioniVisual()
    {
        Operazioni.Clear();
        foreach (var o in Nc.OperazioniSegnalate) Operazioni.Add(o);
        OnPropertyChanged(nameof(HaOperazioni));
    }

    /// <summary>Nuova riga: eredita Componente/Funzionalità quando la segnalazione ne ha uno solo scelto, esattamente
    /// come TestCaseEditorViewModel.AggiungiCondizione fa con EcuAutoSuNuovaOperazione/
    /// FunzionalitaAutoSeSingola — nessuna ambiguità possibile con una sola scelta, l'utente restringe solo
    /// dove serve con più di una.</summary>
    [RelayCommand]
    private void AggiungiOperazioneSegnalazione()
    {
        var operazione = new OperazioneSegnalazione
        {
            Componente = Componente.Count == 1 ? Componente[0] : "",
            Funzionalita = Funzionalita.Count == 1 ? Funzionalita[0] : "",
        };
        Nc.OperazioniSegnalate.Add(operazione);
        AggiornaCampiVisualOperazione(operazione);
        SincronizzaOperazioniVisual();
        ModificheNonSalvate = true;
    }

    [RelayCommand]
    private void RimuoviOperazioneSegnalazione(OperazioneSegnalazione? operazione)
    {
        if (operazione == null) return;
        Nc.OperazioniSegnalate.Remove(operazione);
        SincronizzaOperazioniVisual();
        ModificheNonSalvate = true;
    }

    /// <summary>Stesso schema di ChangeRequestEditorViewModel.AggiungiTicket/ApriTicket/RimuoviTicket, sul
    /// modello CollegamentoPolarion (non TicketRif: è quello già usato da NonConformita).</summary>
    [RelayCommand]
    private void AggiungiTicket()
    {
        if (string.IsNullOrWhiteSpace(NuovoTicketRiferimento)) return;
        var codice = NuovoTicketRiferimento.Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(codice, "^TICKET-[A-Z0-9_-]+$"))
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("FormatoPolarionNonValido");
            return;
        }
        if (!string.IsNullOrWhiteSpace(NuovoTicketUrl) && !Uri.TryCreate(NuovoTicketUrl.Trim(), UriKind.Absolute, out _))
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("LinkPolarionNonValido");
            return;
        }
        var ticket = new CollegamentoPolarion { Codice = codice, Url = string.IsNullOrWhiteSpace(NuovoTicketUrl) ? PolarionTicketLinkService.DerivaUrl(_state.Paths, codice) : NuovoTicketUrl.Trim(), CollegatoIl = DateTime.Now, CollegatoDa = _state.Config.OperatoreAttivo };
        Nc.CollegamentiPolarion.Add(ticket);
        TicketCollegatiVisual.Add(ticket);
        NuovoTicketRiferimento = "";
        NuovoTicketUrl = "";
        MessaggioErrore = null;
        ModificheNonSalvate = true;
    }

    [RelayCommand]
    private void ApriTicket(CollegamentoPolarion? ticket)
    {
        if (ticket == null || string.IsNullOrWhiteSpace(ticket.Url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ticket.Url) { UseShellExecute = true }); }
        catch { MessaggioErrore = LocalizationService.Instance.Traduci("ApriPolarionErrore"); }
    }

    [RelayCommand]
    private void RimuoviTicket(CollegamentoPolarion? ticket)
    {
        if (ticket == null) return;
        Nc.CollegamentiPolarion.Remove(ticket);
        TicketCollegatiVisual.Remove(ticket);
        ModificheNonSalvate = true;
    }

    [RelayCommand]
    private void AggiungiNumeroPratica()
    {
        // Un codice TICKET non e' un numero pratica/Asist della rete: va nei ticket TICKET collegati.
        if (System.Text.RegularExpressions.Regex.IsMatch((NuovoNumeroPratica ?? "").Trim(), @"^TICKET-\d+$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            MessaggioErrore = LocalizationService.Instance.Scegli(
                "Questo è un codice TICKET, non un numero pratica/Asist: inseriscilo nei ticket TICKET collegati.",
                "This is an TICKET code, not a service case/Asist number: add it to the linked TICKET tickets.",
                "Ceci est un code TICKET, pas un numéro de dossier/Asist : ajoutez-le aux tickets TICKET associés.");
            return;
        }
        Aggiungi(NumeriPratica, NuovoNumeroPratica);
        NuovoNumeroPratica = "";
    }
    [RelayCommand] private void RimuoviNumeroPratica(string? valore) => Rimuovi(NumeriPratica, valore);

    [RelayCommand]
    private void AggiungiVin()
    {
        Aggiungi(VinCliente, NuovoVin);
        NuovoVin = "";
    }
    [RelayCommand] private void RimuoviVin(string? valore) => Rimuovi(VinCliente, valore);

    [RelayCommand]
    private void AggiungiLicenza()
    {
        Aggiungi(Licenze, NuovaLicenza);
        NuovaLicenza = "";
    }
    [RelayCommand] private void RimuoviLicenza(string? valore) => Rimuovi(Licenze, valore);

    [RelayCommand] private void AggiungiEcu(string? valore) => Aggiungi(Componente, valore);
    [RelayCommand] private void RimuoviEcu(string? valore) => Rimuovi(Componente, valore);
    [RelayCommand] private void AggiungiVeicolo(string? valore) => Aggiungi(Veicoli, valore);
    [RelayCommand] private void RimuoviVeicolo(string? valore) => Rimuovi(Veicoli, valore);
    [RelayCommand] private void AggiungiFunzionalita(string? valore) => Aggiungi(Funzionalita, valore);
    [RelayCommand] private void RimuoviFunzionalita(string? valore) => Rimuovi(Funzionalita, valore);

    /// <summary>Aggiunta multipla, stessa firma di ChangeRequestEditorViewModel.AggiungiEcu/Veicoli/Strumenti:
    /// permette alla View di riusare lo stesso picker a soglia combobox/flyout (MultiSelectPickerFlyout) già
    /// collaudato lì, invece di reinventarlo per questa pagina.</summary>
    public void AggiungiEcu(IReadOnlyList<string> valori) { foreach (var v in valori) Aggiungi(Componente, v); }
    public void AggiungiVeicoli(IReadOnlyList<string> valori) { foreach (var v in valori) Aggiungi(Veicoli, v); }
    public void AggiungiFunzionalita(IReadOnlyList<string> valori) { foreach (var v in valori) Aggiungi(Funzionalita, v); }

    private void Aggiungi(ObservableCollection<string> collezione, string? valore)
    {
        valore = valore?.Trim();
        if (string.IsNullOrWhiteSpace(valore) || collezione.Contains(valore, StringComparer.OrdinalIgnoreCase)) return;
        collezione.Add(valore);
        ModificheNonSalvate = true;
    }

    private void Rimuovi(ObservableCollection<string> collezione, string? valore)
    {
        if (valore == null) return;
        var esistente = collezione.FirstOrDefault(v => v.Equals(valore, StringComparison.OrdinalIgnoreCase));
        if (esistente == null) return;
        collezione.Remove(esistente);
        ModificheNonSalvate = true;
    }

    /// <summary>Test da cui nasce la segnalazione (Nc.TestCaseId): il link del banner apre quel Test con la stessa
    /// navigazione e conferma di abbandono degli altri link (MainViewModel.NavigaATestCase). Assente per una NC nuova.</summary>
    public bool HaTestCollegatoLink => _apriTest != null && !string.IsNullOrWhiteSpace(Nc.TestCaseId);

    public string TestCollegatoLink => Nc.TestCaseId;

    [RelayCommand]
    private void ApriTestCollegato()
    {
        if (HaTestCollegatoLink) _apriTest!(Nc.TestCaseId);
    }

    private static string T(string chiave) => LocalizationService.Instance.Traduci(chiave);
    private static string TF(string chiave, params object[] args) => string.Format(LocalizationService.Instance.Traduci(chiave), args);

    private bool Valida()
    {
        // Valori digitati ma non ancora "aggiunti" (niente Invio/Aggiungi): vanno considerati inseriti, altrimenti
        // l'utente che ha scritto ticket/matricola/licenza si vede rifiutare il salvataggio per un campo apparentemente pieno.
        if (!string.IsNullOrWhiteSpace(NuovoNumeroPratica)) AggiungiNumeroPratica();
        if (!string.IsNullOrWhiteSpace(NuovoVin)) AggiungiVin();
        if (!string.IsNullOrWhiteSpace(NuovaLicenza)) AggiungiLicenza();
        var mancanti = new List<CampoMancante>();
        if (VinCliente.Count == 0) mancanti.Add(new(T("SegEdValVin"), "OrigineSegnalazione"));
        // Obbligatoria almeno una licenza (feedback utente 2026-09-16), stesso principio di ticket/matricola sopra.
        if (Licenze.Count == 0) mancanti.Add(new(T("SegEdValLicenza"), "OrigineSegnalazione"));
        if (string.IsNullOrWhiteSpace(DescrizioneBreve)) mancanti.Add(new(T("SegEdValSintesi"), "DescrizioneSegnalata"));
        if (string.IsNullOrWhiteSpace(Descrizione)) mancanti.Add(new(T("SegEdValDescrizione"), "DescrizioneSegnalata"));
        if (!Severita.HasValue) mancanti.Add(new(LocalizationService.Instance.Traduci("SeveritaObbl"), "DescrizioneSegnalata"));
        if (!PrioritaScelta.HasValue) mancanti.Add(new(T("SegEdValPriorita"), "DescrizioneSegnalata"));
        if (!StatoScelta.HasValue) mancanti.Add(new(T("SegEdValStato"), "DescrizioneSegnalata"));
        // Almeno uno tra componente/Veicolo/Funzionalità/Strumento (feedback utente 2026-09-16): un "ambito" del
        // tutto vuoto non permetterebbe nemmeno il controllo duplicati sotto, oltre a non dire nulla su cosa
        // sia effettivamente coinvolto.
        if (Componente.Count == 0 && Veicoli.Count == 0 && Funzionalita.Count == 0 && string.IsNullOrWhiteSpace(StrumentoSelezionato))
            mancanti.Add(new(T("SegEdValAmbito"), "AmbitoSegnalato"));
        CampiMancanti = mancanti;
        MessaggioErrore = mancanti.Count > 0 ? string.Join(Environment.NewLine, mancanti.Select(m => m.Messaggio)) : null;
        if (mancanti.Count > 0) RichiestaMostraCampiMancanti?.Invoke();
        return mancanti.Count == 0;
    }

    /// <summary>Ambito (componente/Funzionalità) di una NC qualunque sia la sua origine: per una segnalazione di rete
    /// sono i suoi stessi campi, per una NC nata da un Test vanno letti dal Test collegato (EcuSegnalate/
    /// FunzionalitaSegnalate restano vuoti su una NC che non è DaSegnalazioneRete). Usato solo dal controllo
    /// duplicati sotto.</summary>
    private (IReadOnlyList<string> Componente, IReadOnlyList<string> Funzionalita) AmbitoDi(NonConformita candidato)
    {
        if (candidato.DaSegnalazioneRete) return (candidato.EcuSegnalate, candidato.FunzionalitaSegnalate);
        var test = candidato.TestUid.HasValue ? _state.TestCases.GetByUid(candidato.TestUid.Value) :
            string.IsNullOrWhiteSpace(candidato.TestCaseId) ? null : _state.TestCases.GetById(candidato.TestCaseId);
        if (test != null && !AnomaliaWorkflowService.AppartieneAlTest(candidato, test.Id, test)) test = null;
        return test != null ? (test.Componente, test.Funzionalita) : (Array.Empty<string>(), Array.Empty<string>());
    }

    private static string RiepilogoNc(NonConformita nc) =>
        string.IsNullOrWhiteSpace(nc.DescrizioneBreveSegnalata) ? nc.Descrizione : nc.DescrizioneBreveSegnalata;

    /// <summary>Controllo duplicati (feedback utente 2026-09-16: "non devo creare una non conformità per
    /// errore se già presente in uno dei nostri test, piuttosto aggiungo il caso/segnalazione a quella non
    /// conformità"): stessa componente + stessa funzionalità su una NC ancora APERTA (una già chiusa non è più un
    /// caso da collegare — se il problema si ripresenta ha senso una NC nuova). Richiede almeno un componente
    /// indicata: senza non c'è un aggancio sensato per il confronto.</summary>
    private NonConformita? TrovaCandidatoDuplicato(string funzionalita)
    {
        if (!_state.CartellaValida || Componente.Count == 0) return null;
        var ecuSet = Componente.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidato in _state.NonConformita.GetAll())
        {
            if (!IsNew && candidato.Id == Nc.Id) continue;
            if (candidato.Stato != StatoNonConformita.Aperta) continue;
            var (ecuCandidato, funzCandidato) = AmbitoDi(candidato);
            if (ecuCandidato.Any(ecuSet.Contains) && funzCandidato.Contains(funzionalita, StringComparer.OrdinalIgnoreCase))
                return candidato;
        }
        return null;
    }

    /// <summary>Unisce ticket/matricola/licenze di QUESTA segnalazione in una NC esistente invece di crearne una
    /// nuova (scelta dell'operatore dopo il controllo duplicati), aggiungendo la descrizione come blocco
    /// riconoscibile invece di scartarla: l'unica informazione persa altrimenti sarebbe proprio il motivo per
    /// cui l'operatore ha aperto questa segnalazione. Restituisce false (con MessaggioErrore già impostato) se
    /// il salvataggio fallisce, così i chiamanti non proseguono come se fosse andato a buon fine.</summary>
    private async Task<bool> CollegaSegnalazioneEsistente(NonConformita esistente)
    {
        void UnisciCaseInsensitive(List<string> destinazione, IEnumerable<string> nuovi)
        {
            foreach (var v in nuovi)
                if (!destinazione.Contains(v, StringComparer.OrdinalIgnoreCase)) destinazione.Add(v);
        }
        UnisciCaseInsensitive(esistente.NumeriPraticaRete, NumeriPratica);
        UnisciCaseInsensitive(esistente.VinCliente, VinCliente);
        UnisciCaseInsensitive(esistente.Licenze, Licenze);

        var operatore = _state.Config.OperatoreAttivo;
        esistente.Descrizione += $"\n\n--- Ulteriore segnalazione collegata il {DateTime.Now:dd/MM/yyyy HH:mm} da {operatore} ---\n{DescrizioneBreve}\n{Descrizione}";
        esistente.Changelog.Add(new ChangelogEntry
        {
            Data = DateTime.Now,
            Operatore = operatore,
            Campo = "segnalazione rete",
            ValoreNuovo = $"Segnalazione aggiuntiva collegata invece di una nuova NC (ticket: {string.Join(", ", NumeriPratica)})"
        });

        var allegatiAggiunti = new List<Allegato>();
        try
        {
            foreach (var allegato in Nc.Allegati)
            {
                if (!string.IsNullOrWhiteSpace(allegato.Sha256) && esistente.Allegati.Any(a => !a.Archiviata &&
                        a.Sha256.Equals(allegato.Sha256, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var sorgente = AttachmentService.PercorsoCompleto(_state.Paths, Nc, allegato);
                var copiato = await AttachmentService.SalvaAsync(_state.Paths, esistente, sorgente, _state.Config.OperatoreAttivo);
                esistente.Allegati.Add(copiato);
                allegatiAggiunti.Add(copiato);
            }
            _state.NonConformita.Save(esistente, esistente.RevisioneDati);
            return true;
        }
        catch (Exception ex)
        {
            if (allegatiAggiunti.Count > 0)
                PulisciAllegatiNonPersistiti(esistente);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoFmt"), ex.Message);
            return false;
        }
    }

    [RelayCommand]
    private Task Salva() =>
        ComandoAsincrono.EseguiAsync(SalvaCoreAsync, "Salvataggio della segnalazione di rete", m => MessaggioErrore = m);

    [RelayCommand]
    private Task Chiudi() =>
        ComandoAsincrono.EseguiAsync(() => EseguiChiusuraORiaperturaAsync(chiudi: true), "Chiusura della segnalazione di rete", m => MessaggioErrore = m);

    [RelayCommand]
    private Task Riapri() =>
        ComandoAsincrono.EseguiAsync(() => EseguiChiusuraORiaperturaAsync(chiudi: false), "Riapertura della segnalazione di rete", m => MessaggioErrore = m);

    /// <summary>Chiusura/riapertura con motivo obbligatorio tramite lo stesso servizio del dettaglio (permessi e
    /// controllo di revisione inclusi). Al successo si torna alla lista, che si ricarica.</summary>
    private Task EseguiChiusuraORiaperturaAsync(bool chiudi)
    {
        if (chiudi ? !PuoChiudere : !PuoRiaprire) return Task.CompletedTask;
        var motivo = ((chiudi ? MotivoChiusura : MotivoRiapertura) ?? "").Trim();
        if (motivo.Length == 0)
        {
            MessaggioErrore = T(chiudi ? "ChiudereAnomaliaMotivo" : "RiaperturaMotivoObbl");
            return Task.CompletedTask;
        }
        try
        {
            if (chiudi) AnomaliaAzioniService.Chiudi(_state, Nc, motivo, severitaRichiesta: !Nc.Severita.HasValue);
            else AnomaliaAzioniService.Riapri(_state, Nc, motivo);
        }
        catch (ConflittoRevisioneException)
        {
            MessaggioErrore = T("AnomaliaConflittoInputPreservato");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            MessaggioErrore = TF("SalvataggioNonRiuscitoFmt", ex.Message);
            return Task.CompletedTask;
        }
        MessaggioErrore = null;
        _torna();
        return Task.CompletedTask;
    }

    private async Task SalvaCoreAsync()
    {
        if (!PuoSalvare) return;
        if (!Valida()) return;

        Nc.NumeriPraticaRete = NumeriPratica.ToList();
        Nc.VinCliente = VinCliente.ToList();
        Nc.Licenze = Licenze.ToList();
        Nc.EcuSegnalate = new EcuSelection(Componente);
        Nc.VeicoliSegnalati = Veicoli.ToList();
        Nc.FunzionalitaSegnalate = Funzionalita.ToList();
        Nc.StrumentoSegnalato = StrumentoSelezionato;
        // Solo le segnalazioni NUOVE nascono "da rete": salvare una NC esistente non deve cambiarne l'origine.
        if (IsNew) Nc.DaSegnalazioneRete = true;

        // Split in più NC indipendenti, una per funzionalità selezionata (feedback utente 2026-09-16), solo
        // per una segnalazione NUOVA: modificare le funzionalità di una NC già salvata resta un aggiornamento
        // della stessa riga, non genera retroattivamente NC "sorelle" che l'operatore non si aspetta.
        if (IsNew && Funzionalita.Count > 1)
        {
            await SalvaComeSegnalazioniMultiple();
            return;
        }

        if (IsNew && Funzionalita.Count == 1)
        {
            var candidato = TrovaCandidatoDuplicato(Funzionalita[0]);
            if (candidato != null)
            {
                var collega = await ConfirmService.Chiedi(
                    T("SegEdDuplicatoTitolo"),
                    TF("SegEdDuplicatoBodyFmt", Funzionalita[0], candidato.Id, RiepilogoNc(candidato)),
                    TF("SegEdDuplicatoCollegaFmt", candidato.Id), T("SegEdDuplicatoCreaComunque"));
                if (collega)
                {
                    if (await CollegaSegnalazioneEsistente(candidato))
                    {
                        PulisciAllegatiBozzaSorgente();
                        MessaggioErrore = null;
                        Messaggio = TF("SegEdCollegataFmt", candidato.Id);
                        ModificheNonSalvate = false;
                        _torna();
                    }
                    return;
                }
            }
        }

        SalvaSingola();
    }

    /// <summary>Best effort: il fallimento del rescue non maschera l'errore originale.</summary>
    private void SalvaBozzaEmergenza(Exception motivo, NonConformita? tentativo = null)
    {
        // Per una NC nuova la bozza porta l'ID tentato (tentativo); altrimenti la NC aperta con la sua base.
        var payload = tentativo ?? Nc;
        try
        {
            FailedSaveDraftService.Save(_state.Paths.Root, "nonconformita", payload.Uid,
                _revisioneAttesa ?? 0, _state.Config.OperatoreAttivo, payload, motivo.Message,
                baseSnapshot: _snapshotIniziale);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Recupero locale segnalazione/NC dopo salvataggio fallito", ex);
        }
    }

    private void SalvaSingola()
    {
        Nc.StatoWorkflow = StatoScelta!.Value;
        var candidato = EntityStateFingerprint.Clone(Nc);
        if (!AnomaliaAzioniService.RegistraCorrezioneData(candidato, _snapshotIniziale, _state.Config.OperatoreAttivo))
        { MessaggioErrore = T("AnomaliaDataChiusuraNonValida"); return; }
        NonConformita? tentativo = null;
        try
        {
            if (IsNew)
            {
                Nc.Id = _state.NonConformita.NextId();
                tentativo = EntityStateFingerprint.Clone(Nc); // copia per la bozza di emergenza: porta l'ID tentato
                _state.NonConformita.Save(Nc);
            }
            else
            {
                candidato.Changelog.Add(new ChangelogEntry
                {
                    Data = DateTime.Now,
                    Operatore = _state.Config.OperatoreAttivo,
                    Campo = "segnalazione rete",
                    ValoreNuovo = "modificata"
                });
                _state.NonConformita.Save(candidato, _revisioneAttesa);
            }
        }
        catch (Exception ex)
        {
            // Una NC nuova che non si salva torna "nuova" (ID vuoto): l'ID tentato resta solo nella bozza e
            // non viene riutilizzato da una NC aperta, così il prossimo Salva assegna un ID con le normali verifiche.
            if (tentativo != null) Nc.Id = "";
            SalvaBozzaEmergenza(ex, tentativo);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoFmt"), ex.Message)
                + LocalizationService.Instance.Traduci("SegVerificaBozzeImpostazioniMsg");
            return;
        }

        MessaggioErrore = null;
        ModificheNonSalvate = false;
        // Salvataggio riuscito: solo ora i binari staccati che il record non referenzia piu' possono essere rimossi.
        _rimozioniAllegati.ApplicaDopoPersistenza(Nc.Allegati, a => AttachmentService.Elimina(_state.Paths, Nc, a));
        _torna();
    }

    /// <summary>Split in NC separate, una per funzionalità (feedback utente 2026-09-16: "o più di una non
    /// conformità per funzionalità... dovrà poi essere definita come si fa con tutte le non conformità che
    /// hanno ticket ticket e stato"). Conferma esplicita prima di procedere (evita che scatti per errore da un
    /// semplice multi-select), poi un solo controllo duplicati consolidato per TUTTE le funzionalità coinvolte
    /// (l'operatore vede tutti i casi ambigui insieme, non un popup alla volta). I dati comuni (ticket/matricola/
    /// licenze/componente/Veicolo/Strumento/campi strumento/descrizione/severità/priorità/stato/ticket Ticketing già
    /// aggiunti in questo form) sono copiati UNA VOLTA in ciascuna NC generata: da quel momento ogni NC vive
    /// la propria vita indipendente (stato/TICKET/Ticketing/allegati suoi), esattamente come una NC creata a
    /// mano — nessuna sincronizzazione successiva fra "sorelle".</summary>
    private async Task SalvaComeSegnalazioniMultiple()
    {
        var conferma = await ConfirmService.Chiedi(
            T("SegEdSplitTitolo"),
            TF("SegEdSplitBodyFmt", Funzionalita.Count),
            T("SegEdSplitBtn"), T("Annulla"));
        if (!conferma) return;

        var candidatiPerFunzionalita = new Dictionary<string, NonConformita>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Funzionalita)
        {
            var candidato = TrovaCandidatoDuplicato(f);
            if (candidato != null) candidatiPerFunzionalita[f] = candidato;
        }

        // Una scelta collega/crea per CIASCUN candidato trovato (feedback utente 2026-09-17: prima era una
        // sola scelta valida per tutte le funzionalità insieme — "collega tutte quelle con un candidato" o
        // "crea comunque tutte nuove", mai una via di mezzo). Un popup in sequenza per candidato, stesso
        // testo/pulsanti già usati sul percorso a una sola funzionalità: nessuna finestra nuova da costruire,
        // e resta corretto anche con un solo candidato trovato durante lo split.
        var collegaPerFunzionalita = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (f, candidato) in candidatiPerFunzionalita)
        {
            collegaPerFunzionalita[f] = await ConfirmService.Chiedi(
                T("SegEdDuplicatoTitolo"),
                TF("SegEdDuplicatoBodyFmt", f, candidato.Id, RiepilogoNc(candidato)),
                TF("SegEdDuplicatoCollegaFmt", candidato.Id), T("SegEdDuplicatoCreaComunque"));
        }

        var gruppo = Guid.NewGuid();
        var idGiaCollegati = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var creati = new List<string>();
        var collegati = new List<string>();
        var completate = new List<string>();
        // Copia: in caso di errore parziale l'elenco Funzionalita viene ridotto alle sole ancora da salvare.
        foreach (var f in Funzionalita.ToList())
        {
            if (collegaPerFunzionalita.GetValueOrDefault(f) && candidatiPerFunzionalita.TryGetValue(f, out var esistente))
            {
                // Una stessa NC candidata per due funzionalità diverse di questa segnalazione (raro, ma
                // possibile): unita una sola volta, non due — la seconda userebbe una revisione ormai
                // superata dalla prima e fallirebbe con un conflitto di revisione invece che essere idempotente.
                if (idGiaCollegati.Add(esistente.Id) && !await CollegaSegnalazioneEsistente(esistente))
                {
                    RimuoviFunzionalitaGiaSalvate(completate, creati, collegati);
                    return;
                }
                collegati.Add($"{f} → {esistente.Id}");
                completate.Add(f);
                continue;
            }

            var nuova = EntityStateFingerprint.Clone(Nc);
            nuova.Uid = Guid.NewGuid();
            nuova.Id = "";
            nuova.RevisioneDati = 0;
            nuova.FunzionalitaSegnalate = new List<string> { f };
            // Un'operazione appartiene a UNA funzionalità (feedback utente 2026-09-18): lo split per
            // funzionalità deve restringere le operazioni allo stesso modo di FunzionalitaSegnalate sopra,
            // altrimenti ogni NC "sorella" erediterebbe anche le operazioni delle altre funzionalità, mai
            // rilevanti per quella riga. Un'operazione senza Funzionalità scelta (riga appena aggiunta, non
            // ancora compilata) resta con la prima NC generata, cosi' non sparisce silenziosamente.
            nuova.OperazioniSegnalate = Nc.OperazioniSegnalate
                .Where(o => o.Funzionalita.Equals(f, StringComparison.OrdinalIgnoreCase)
                    || (string.IsNullOrWhiteSpace(o.Funzionalita) && f.Equals(Funzionalita[0], StringComparison.OrdinalIgnoreCase)))
                .Select(o => new OperazioneSegnalazione { Componente = o.Componente, Funzionalita = o.Funzionalita, ValoriStrumenti = new Dictionary<string, string>(o.ValoriStrumenti) })
                .ToList();
            nuova.GruppoSegnalazioneUid = gruppo;
            nuova.StatoWorkflow = StatoScelta!.Value;
            nuova.Allegati = new List<Allegato>();
            nuova.CollegamentiPolarion = Nc.CollegamentiPolarion
                .Select(t => new CollegamentoPolarion { Codice = t.Codice, Url = t.Url, CollegatoIl = t.CollegatoIl, CollegatoDa = t.CollegatoDa, Note = t.Note })
                .ToList();
            nuova.Changelog = new List<ChangelogEntry>();
            try
            {
                await CopiaAllegatiBozzaSu(nuova);
                nuova.Id = _state.NonConformita.NextId();
                _state.NonConformita.Save(nuova);
                creati.Add(nuova.Id);
                completate.Add(f);
            }
            catch (Exception ex)
            {
                PulisciAllegatiNonPersistiti(nuova);
                SalvaBozzaEmergenza(ex);
                MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoFmt"), ex.Message)
                    + LocalizationService.Instance.Traduci("SegVerificaBozzeImpostazioniMsg");
                RimuoviFunzionalitaGiaSalvate(completate, creati, collegati);
                return;
            }
        }

        MessaggioErrore = null;
        var riepilogo = new List<string>();
        if (creati.Count > 0) riepilogo.Add(TF("SegEdRiepilogoCreateFmt", string.Join(", ", creati)));
        if (collegati.Count > 0) riepilogo.Add(TF("SegEdRiepilogoCollegateFmt", string.Join(", ", collegati)));
        Messaggio = string.Join(" ", riepilogo);
        PulisciAllegatiBozzaSorgente();
        ModificheNonSalvate = false;
        _torna();
    }

    /// <summary>Errore a metà split: le funzionalità già trasformate in NC (o collegate) escono dall'editor insieme
    /// alle loro operazioni, così un nuovo Salva non le duplica. Prima il retry ricreava anche le NC già scritte.</summary>
    private void RimuoviFunzionalitaGiaSalvate(List<string> completate, List<string> creati, List<string> collegati)
    {
        if (completate.Count == 0) return;
        var fatte = new HashSet<string>(completate, StringComparer.OrdinalIgnoreCase);
        foreach (var f in Funzionalita.Where(fatte.Contains).ToList()) Funzionalita.Remove(f);
        // Le operazioni senza funzionalità sono andate con la prima NC generata (la prima elaborata = completate[0]).
        foreach (var o in Nc.OperazioniSegnalate.Where(o => fatte.Contains(o.Funzionalita) ||
                     string.IsNullOrWhiteSpace(o.Funzionalita)).ToList())
        {
            Nc.OperazioniSegnalate.Remove(o);
            Operazioni.Remove(o);
        }
        Nc.FunzionalitaSegnalate = Funzionalita.ToList();
        var giaFatte = creati.Select(id => $"creata {id}").Concat(collegati.Select(c => $"collegata {c}"));
        MessaggioErrore += string.Format(T("SegGiaSalvateFmt"), string.Join(", ", giaFatte)) +
            T("SegPremiDiNuovoSalvaMsg");
        ModificheNonSalvate = true;
    }

    // ----- Allegati: i file vengono salvati subito nel bucket temporaneo identificato dall'UID stabile della
    // NC (anche se non ha ancora un Id). Il primo salvataggio li persiste insieme al record; split/collegamento
    // duplicato copiano i binari nei bucket autorevoli prima di ripulire il bucket temporaneo.

    [RelayCommand]
    private Task AggiungiAllegato(string? percorsoFile) =>
        ComandoAsincrono.EseguiAsync(() => AggiungiAllegatoCoreAsync(percorsoFile), "Aggiunta di un allegato alla segnalazione", m => MessaggioErrore = m);

    private async Task AggiungiAllegatoCoreAsync(string? percorsoFile)
    {
        if (string.IsNullOrEmpty(percorsoFile) || !_state.CartellaValida || CaricamentoAllegatoInCorso) return;
        var completamento = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _completamentoCaricamentoAllegato = completamento;
        NomeAllegatoInCaricamento = Path.GetFileName(percorsoFile);
        CaricamentoAllegatoInCorso = true;
        try
        {
            if (GateCaricamentoAllegatoPerTest is { } gate) await gate();
            var allegato = await AttachmentService.SalvaAsync(_state.Paths, Nc, percorsoFile, _state.Config.OperatoreAttivo);
            Nc.Allegati.Add(allegato);
            AllegatiVisual.Add(allegato);
            ModificheNonSalvate = true;
        }
        catch (Exception ex) { MessaggioErrore = ex.Message; }
        finally
        {
            _completamentoCaricamentoAllegato = null;
            CaricamentoAllegatoInCorso = false;
            NomeAllegatoInCaricamento = null;
            completamento.TrySetResult();
        }
    }

    private async Task CopiaAllegatiBozzaSu(NonConformita destinazione)
    {
        foreach (var allegato in Nc.Allegati)
        {
            var sorgente = AttachmentService.PercorsoCompleto(_state.Paths, Nc, allegato);
            var copiato = await AttachmentService.SalvaAsync(_state.Paths, destinazione, sorgente, _state.Config.OperatoreAttivo);
            destinazione.Allegati.Add(copiato);
        }
    }

    private void PulisciAllegatiNonPersistiti(NonConformita working)
    {
        try
        {
            _state.NonConformita.RefreshIndex();
            var autorevole = _state.NonConformita.GetByUid(working.Uid);
            if (autorevole != null && !string.Equals(autorevole.Id, working.Id, StringComparison.OrdinalIgnoreCase))
            {
                CrashLogService.Registra("Cleanup allegati non persistiti", new IOException($"La NC UID {working.Uid:N} è associata a un Id diverso; cleanup annullata."));
                return;
            }
            DraftAttachmentCleanupService.ReconcileAfterExplicitDiscard(_state.Paths, working, autorevole);
        }
        catch (Exception ex) { CrashLogService.Registra($"Cleanup allegati NC non persistiti {working.Uid:N}", ex); }
    }

    private void PulisciAllegatiBozzaSorgente()
    {
        if (HaBozzaLocale()) return;
        var staccati = _rimozioniAllegati.Abbandona();
        if (Nc.Allegati.Count == 0 && staccati.Count == 0) return;
        var working = EntityStateFingerprint.Clone(Nc);
        AttachmentRemovalQueue.UnisciAllaCopiaDiLavoro(working.Allegati, staccati);
        PulisciAllegatiNonPersistiti(working);
    }

    // Il binario non si tocca qui: resta nel percorso attivo finche' il Salva non persiste la segnalazione senza
    // questo allegato (vedi SalvaSingola). Annulla, crash o un Salva fallito lasciano quindi file e JSON coerenti.
    private readonly AttachmentRemovalQueue _rimozioniAllegati = new();

    [RelayCommand]
    private void RimuoviAllegato(Allegato? allegato)
    {
        if (allegato == null) return;
        _rimozioniAllegati.Accoda(allegato);
        Nc.Allegati.Remove(allegato);
        AllegatiVisual.Remove(allegato);
        ModificheNonSalvate = true;
    }

    [RelayCommand]
    private void ApriAllegato(Allegato? allegato)
    {
        if (allegato == null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AttachmentService.PercorsoCompleto(_state.Paths, Nc, allegato)) { UseShellExecute = true }); }
        catch { /* file non apribile: nessuna azione, non blocca il resto dell'app */ }
    }

    /// <summary>Stato applicativo per aprire l'anteprima di creazione TICKET dal code-behind.</summary>
    internal AppState StatoApp => _state;

    /// <summary>Vero mentre il salvataggio che precede la preparazione del ticket TICKET è in corso.</summary>
    [ObservableProperty] private bool pacchettoInCorso;

    /// <summary>"Prepara / Crea ticket TICKET" (feedback utente 2026-10-30): disponibile subito, anche su una segnalazione
    /// nuova, che viene salvata prima. Il pulsante resta disabilitato solo in sola lettura o durante l'operazione.</summary>
    public bool PuoEsportarePacchetto => PuoModificare && !PacchettoInCorso;

    partial void OnPacchettoInCorsoChanged(bool value) => OnPropertyChanged(nameof(PuoEsportarePacchetto));

    /// <summary>Salva se nuova o modificata e ritorna la NC persistita da cui preparare il ticket TICKET; null se il
    /// salvataggio non è riuscito, è stato annullato o ha generato più NC/un collegamento (nessuna NC singola da usare).
    /// Il salvataggio riuscito riporta alla lista: l'oggetto Nc resta valido per l'anteprima.</summary>
    public async Task<NonConformita?> SalvaPerPreparareEstaAsync()
    {
        if (PacchettoInCorso) return null;
        PacchettoInCorso = true;
        try
        {
            if (IsNew || ModificheNonSalvate)
                await ComandoAsincrono.EseguiAsync(SalvaCoreAsync, "Salvataggio della segnalazione di rete", m => MessaggioErrore = m);
            return IsNew || ModificheNonSalvate ? null : Nc;
        }
        finally { PacchettoInCorso = false; }
    }

    [RelayCommand]
    private void Annulla()
    {
        if (PuoAnnullare) _torna();
    }

    /// <summary>Apre un'altra segnalazione per Id (feedback utente 2026-09-16/17): il richiamo "Segnalazione
    /// collegata" verso le NC sorelle nate dallo stesso split era solo informativo. Nullo se questo editor è
    /// stato creato senza il canale di navigazione (es. nei test) o se l'id è vuoto — nessuna eccezione, solo
    /// nessuna navigazione.</summary>
    [RelayCommand]
    private void ApriSorella(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        _apriSegnalazione?.Invoke(id);
    }

    /// <summary>Picker con ricerca per Owner quando gli operatori superano la soglia combobox (stesso schema
    /// di ChangeRequestEditorViewModel.ScegliResponsabile).</summary>
    [RelayCommand]
    private async Task ScegliOwner()
    {
        var voce = await SelezionaDialogService.Scegli("Scegli owner",
            OperatoriDisponibili.Select(o => new VoceSelezionabile { Valore = o, TestoPrincipale = o, TestoRicerca = o }).ToList());
        if (voce != null) Owner = (string)voce.Valore;
    }
}
