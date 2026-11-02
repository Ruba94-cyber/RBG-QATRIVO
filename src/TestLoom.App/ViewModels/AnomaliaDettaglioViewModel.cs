using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Una condizione (operazione) del test collegato nella sezione "Aggiorna test collegato" del dialogo
/// di dettaglio anomalia: mostra Funzionalita/componente e lascia scegliere il nuovo esito.</summary>
public sealed partial class CondizioneEsitoRiga : ObservableObject
{
    private string _durataTesto = "";

    public CondizioneEsitoRiga(Condizione condizione)
    {
        Uid = condizione.Uid;
        Origine = condizione;
        EsitoAttuale = condizione.Esito;
        Etichetta = string.Join(" · ", new[] { condizione.Funzionalita, condizione.Componente.ToString() }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (Etichetta.Length == 0) Etichetta = "—";
        var esiti = AnomaliaDettaglioViewModel.EsitiSelezionabili.ToList();
        if (!esiti.Contains(condizione.Esito)) esiti.Insert(0, condizione.Esito);
        Esiti = esiti;
        esitoNuovo = condizione.Esito;
    }

    public Guid Uid { get; }
    public string Etichetta { get; }
    public Esito EsitoAttuale { get; }
    public IReadOnlyList<Esito> Esiti { get; }
    public string AutomationIdEsito => $"AnomaliaDettaglio_Condizione_{Uid}_Esito_Combo";
    public string AutomationIdDurata => $"AnomaliaDettaglio_Condizione_{Uid}_Durata_Campo";
    public bool Cambiato => EsitoNuovo != EsitoAttuale;

    /// <summary>Operazione di origine del test collegato: componente, Funzionalita o guasto sono la chiave della regola di copia della durata.</summary>
    internal Condizione Origine { get; }

    /// <summary>Durata della nuova prova in minuti, come digitata (vuoto = non nota). Finche l'operatore non la tocca, la regola di copia
    /// puo proporre il valore (vedi <see cref="ImpostaPropostaDurata"/>).</summary>
    public string DurataTesto
    {
        get => _durataTesto;
        set
        {
            if (SetProperty(ref _durataTesto, value ?? "")) DurataModificataDallOperatore = true;
        }
    }

    /// <summary>True una volta che l'operatore ha scritto nel campo durata: la proposta della copia non lo sovrascrive piu.</summary>
    public bool DurataModificataDallOperatore { get; private set; }

    /// <summary>Vero solo per le nuove prove eseguite: la durata si chiede solo dove la politica e le regole di copia la riguardano.</summary>
    public bool MostraDurata => Cambiato && EsitoNuovo.EEseguito();

    /// <summary>Proposta della regola di copia (mai una misura): non sovrascrive un valore digitato dall'operatore.</summary>
    internal void ImpostaPropostaDurata(int? minuti)
    {
        if (DurataModificataDallOperatore || minuti is not { } valore) return;
        SetProperty(ref _durataTesto, valore.ToString(System.Globalization.CultureInfo.InvariantCulture), nameof(DurataTesto));
    }

    [ObservableProperty] private Esito esitoNuovo;
    partial void OnEsitoNuovoChanged(Esito value)
    {
        OnPropertyChanged(nameof(Cambiato));
        OnPropertyChanged(nameof(MostraDurata));
    }
}

/// <summary>Voce della ComboBox Origine (Rete / Rieseguito da noi).</summary>
public sealed record OpzioneOrigine(OrigineRevisione Valore, string Etichetta);

/// <summary>
/// Dialogo UNICO di dettaglio anomalia, usato dalla pagina Anomalie e dalla pagina Test. Lavora su una COPIA
/// della NC (nessuna mutazione dell'istanza in cache) e salva con optimistic lock sulla RevisioneDati vista
/// all'apertura. Alla chiusura, se la NC ha un test collegato, permette di cambiare gli esiti delle
/// condizioni e lo StatoLavoro del test: i nuovi esiti nascono in una NUOVA REV con Origine esplicita
/// (<see cref="AnomaliaChiusuraService.ChiudiESalva"/>); senza modifiche la chiusura e semplice.
/// </summary>
public partial class AnomaliaDettaglioViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly bool _forzaSolaLettura;
    private readonly bool _inizialmenteSolaLettura;
    private readonly Action<string?>? _apriTest;
    private string _fingerprintIniziale = "";
    private NonConformita? _snapshotIniziale;
    private long? _revisioneTestAttesa;
    private bool _severitaPresenteInApertura;

    /// <summary>Esiti proponibili per le nuove prove (stessi di TestCaseEditorViewModel.EsitiTecnici).</summary>
    public static readonly IReadOnlyList<Esito> EsitiSelezionabili =
        new[] { Esito.DaEseguire, Esito.Conforme, Esito.NonConforme, Esito.ConformeConDeviazioni, Esito.NonApplicabile };

    /// <param name="sorgente">NC da aprire; per una NC gia salvata viene riletta dall'archivio (copia).</param>
    /// <param name="persistita">False per una NC creata nel Test e non ancora scritta su disco: si aprono solo i
    /// campi, senza sezione REV, senza Riapri/Elimina e senza salvataggio (vedi <see cref="NcModificata"/>).</param>
    public AnomaliaDettaglioViewModel(AppState state, NonConformita sorgente, bool persistita = true,
        bool forzaSolaLettura = false, Action<string?>? apriTest = null, bool inizialmenteSolaLettura = false)
    {
        ArgumentNullException.ThrowIfNull(sorgente);
        _state = state;
        _forzaSolaLettura = forzaSolaLettura;
        _inizialmenteSolaLettura = inizialmenteSolaLettura;
        inModifica = !inizialmenteSolaLettura;
        _apriTest = apriTest;
        Persistita = persistita;
        nc = persistita ? (state.NonConformita.GetById(sorgente.Id)?.Clona() ?? sorgente.Clona()) : sorgente.Clona();
        CaricaStatoDerivato();
    }

    public bool Persistita { get; }
    [ObservableProperty] private NonConformita nc;

    /// <summary>Vero se il dialogo ha scritto qualcosa (Salva, Chiudi, Riapri, Elimina): il chiamante deve
    /// ricaricare la propria vista.</summary>
    public bool Modificato { get; private set; }

    /// <summary>La pagina apre il dettaglio in consultazione; l'utente può entrare esplicitamente in modifica completa.</summary>
    [ObservableProperty] private bool inModifica;

    /// <summary>Per una NC non persistita: copia validata dei campi modificati, da riapplicare con
    /// <see cref="ApplicaA"/> alla NC in memoria del Test.</summary>
    public NonConformita? NcModificata { get; private set; }

    /// <summary>Chiamato quando il dialogo deve chiudersi (il code-behind chiude la finestra).</summary>
    public event Action? ChiusuraRichiesta;

    [ObservableProperty] private string? errore;
    public bool HaErrore => !string.IsNullOrEmpty(Errore);
    partial void OnErroreChanged(string? value) => OnPropertyChanged(nameof(HaErrore));

    [ObservableProperty] private string nuovoTicketRiferimento = "";
    [ObservableProperty] private string nuovoTicketUrl = "";
    [ObservableProperty] private string testIdDaCollegare = "";
    [ObservableProperty] private string motivoChiusura = "";
    /// <summary>Motivo scritto obbligatorio per riaprire una NC chiusa (resta nello storico della NC).</summary>
    [ObservableProperty] private string motivoRiapertura = "";

    // Ogni input che entra in HaModificheNonSalvate notifica il proprio cambio: l'indicatore "Modifiche non salvate"
    // della view si aggiorna subito, senza attendere il salvataggio o la chiusura del dialogo.
    partial void OnNuovoTicketRiferimentoChanged(string value) => SegnalaModificaCampo();
    partial void OnNuovoTicketUrlChanged(string value) => SegnalaModificaCampo();
    partial void OnTestIdDaCollegareChanged(string value) => SegnalaModificaCampo();
    partial void OnMotivoChiusuraChanged(string value) => SegnalaModificaCampo();
    partial void OnNcChanged(NonConformita value)
    {
        OnPropertyChanged(nameof(CausaProbabileSelezionata));
        OnPropertyChanged(nameof(MostraCausaAltro));
        SegnalaModificaCampo();
    }

    // ---- permessi / visibilita ----
    public bool PuoModificare => !_forzaSolaLettura && _state.PuoScrivere && InModifica;
    public bool PuoEntrareInModifica => _inizialmenteSolaLettura && !_forzaSolaLettura && !InModifica && _state.PuoScrivere;
    public bool SolaLettura => !PuoModificare;
    public bool EMaster => _state.EMaster;
    public bool Aperta => Nc.StatoWorkflow != StatoAnomalia.Conclusa;
    public bool Chiusa => !Aperta;
    public DateTimeOffset? DataChiusuraModificabile
    {
        get => Nc.DataChiusura is { } data ? new DateTimeOffset(data) : null;
        set
        {
            if (!PuoModificare || !Chiusa || !value.HasValue) return;
            var nuova = value.Value.Date + (Nc.DataChiusura?.TimeOfDay ?? TimeSpan.Zero);
            if (Nc.DataChiusura == nuova) return;
            Nc.DataChiusura = nuova;
            OnPropertyChanged();
            SegnalaModificaCampo();
        }
    }
    public bool PuoChiudere => PuoModificare && Persistita && Aperta;
    // Riapri: come prima dalla pagina Test, per chiunque possa scrivere (Master e Standard). Elimina resta solo Master.
    public bool PuoRiaprire => PuoModificare && Persistita && Chiusa;
    public bool PuoEliminare => PuoModificare && Persistita && EMaster;
    public bool PuoRecuperareBozza => PuoModificare && Persistita && _snapshotIniziale != null;
    public IReadOnlyDictionary<string, string> CampiBozzaRecuperabili =>
        FailedSaveDraftRecoveryPreparationService.EditableAnomaliaDetailFields
            .Where(pair => (pair.Key != nameof(NonConformita.Priorita) || EMaster) &&
                ((pair.Key != nameof(NonConformita.MotivoChiusura) && pair.Key != nameof(NonConformita.DataChiusura)) || Chiusa))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    public string WorkspaceRoot => _state.Paths.Root;
    public bool SenzaTestCollegato => Persistita && string.IsNullOrWhiteSpace(Nc.TestCaseId) && Nc.ProveCollegate.Count == 0;
    public bool NonPersistita => !Persistita;

    public void EntraInModifica()
    {
        if (!PuoEntrareInModifica) return;
        InModifica = true;
        NotificaTutto();
    }

    // ---- opzioni delle combo ----
    public Array StatiWorkflow => Aperta
        ? Enum.GetValues(typeof(StatoAnomalia)).Cast<StatoAnomalia>().Where(s => s != StatoAnomalia.Conclusa).ToArray()
        : Enum.GetValues(typeof(StatoAnomalia));
    public Array SeveritaDisponibili => Enum.GetValues(typeof(SeveritaAnomalia));
    public Array PrioritaDisponibili => Enum.GetValues(typeof(PrioritaOperativa));
    public Array Destinatari => Enum.GetValues(typeof(DestinatarioSegnalazione));
    public Array CauseProbabili => Enum.GetValues(typeof(CausaProbabileUdt));

    /// <summary>Causa probabile della NC, legata alla ComboBox. <see cref="Nc"/> non notifica le proprie proprieta: la
    /// scrittura passa da qui e notifica <see cref="MostraCausaAltro"/> e l'indicatore di modifiche non salvate.</summary>
    public CausaProbabileUdt? CausaProbabileSelezionata
    {
        get => Nc.CausaProbabile;
        set
        {
            if (Nc.CausaProbabile == value) return;
            Nc.CausaProbabile = value;
            OnPropertyChanged(nameof(CausaProbabileSelezionata));
            OnPropertyChanged(nameof(MostraCausaAltro));
            SegnalaModificaCampo();
        }
    }

    /// <summary>Vero se la causa probabile e 'Altro': mostra il campo libero <see cref="NonConformita.CausaProbabileAltro"/>.</summary>
    public bool MostraCausaAltro => Nc.CausaProbabile == CausaProbabileUdt.Altro;
    public Array StatiDettaglio => Enum.GetValues(typeof(StatoDettaglioNc));
    public Array StatiLavoro => Enum.GetValues(typeof(StatoLavoroTest));
    public IReadOnlyList<OpzioneOrigine> OpzioniOrigine { get; } = new[]
    {
        new OpzioneOrigine(OrigineRevisione.Rete, LocalizationService.Instance.Traduci("OrigineReteLabel")),
        new OpzioneOrigine(OrigineRevisione.RieseguitoDaNoi, LocalizationService.Instance.Traduci("OrigineRieseguitoLabel")),
    };

    // ---- sezione "Aggiorna test collegato" ----
    public TestCase? TestCollegato { get; private set; }
    public bool HaTestCollegato => TestCollegato != null;
    public bool MostraSezioneTest => HaTestCollegato && Aperta && PuoModificare;
    public string TestCollegatoId => TestCollegato?.Id ?? "";
    public bool PuoAprireTest => _apriTest != null && TestPerApertura() is { Eliminato: false };
    public string VeicoliTest => TestCollegato?.VeicoliTesto ?? "";
    public ObservableCollection<CondizioneEsitoRiga> Condizioni { get; } = new();

    [ObservableProperty] private StatoLavoroTest statoLavoroNuovo;
    [ObservableProperty] private OpzioneOrigine? origineSelezionata;

    partial void OnStatoLavoroNuovoChanged(StatoLavoroTest value)
    {
        OnPropertyChanged(nameof(ModificaTestRichiesta));
        SegnalaModificaCampo();
    }

    /// <summary>Vero se almeno un esito o lo StatoLavoro del test e diverso da quello corrente.</summary>
    public bool ModificaTestRichiesta =>
        TestCollegato != null && (Condizioni.Any(c => c.Cambiato) || StatoLavoroNuovo != TestCollegato.StatoLavoro);

    /// <summary>Tiene aggiornato <see cref="ModificaTestRichiesta"/> quando cambia un esito nelle righe.</summary>
    private void OnRigaCambiata(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CondizioneEsitoRiga.EsitoNuovo)) return;
        // TIME (6.6.1): una nuova prova eseguita propone la durata dalla regola di copia (stessa chiave dell'editor); resta modificabile.
        if (sender is CondizioneEsitoRiga riga && riga.MostraDurata) ProponiDurataCopia(riga);
        OnPropertyChanged(nameof(ModificaTestRichiesta));
        SegnalaModificaCampo();
    }

    /// <summary>Risultato della regola di copia per le copie in volo (test: <see cref="AttendiCopieDurataInCorso"/>). Il risultato
    /// arriva sul thread UI tramite questo hook.</summary>
    internal Action<Action> PubblicaRisultatoCopia { get; set; } = azione => Avalonia.Threading.Dispatcher.UIThread.Post(azione);

    private readonly List<Task> _copieDurataInCorso = new();

    /// <summary>Attende le proposte di durata avviate finora (test).</summary>
    internal Task AttendiCopieDurataInCorso() => Task.WhenAll(_copieDurataInCorso.ToArray());

    /// <summary>Chiede la durata della nuova prova alla regola di copia (indice locale, fuori dal thread UI). Il valore si applica solo
    /// se la riga e' ancora una nuova prova eseguita e l'operatore non ha scritto nel campo.</summary>
    private void ProponiDurataCopia(CondizioneEsitoRiga riga)
    {
        var test = TestCollegato;
        if (test == null) return;
        var origine = riga.Origine;
        var componente = origine.Componente.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var funzionalita = origine.RiguardaDtc ? null : origine.Funzionalita?.Trim();
        int? passoDtc = origine.RiguardaDtc ? (int)(origine.DtcPasso ?? PassoDtc.Validazione) : null;
        if (componente.Count == 0 || (passoDtc is null && string.IsNullOrWhiteSpace(funzionalita))) return;
        var veicoli = test.VeicoliEffettivi.ToList();
        var repository = _state.TestCases;
        var attivita = Task.Run(() => repository.TrovaDurataCopia(componente, funzionalita, veicoli, passoDtc))
            .ContinueWith(t =>
            {
                // Un errore della query non deve sparire: la durata non viene proposta, ma il motivo resta nel log.
                if (t.IsFaulted) CrashLogService.Registra("Durata copia (dettaglio anomalia)", t.Exception);
                if (!t.IsCompletedSuccessfully) return;
                PubblicaRisultatoCopia(() =>
                {
                    if (riga.MostraDurata) riga.ImpostaPropostaDurata(t.Result);
                });
            }, TaskScheduler.Default);
        _copieDurataInCorso.RemoveAll(c => c.IsCompleted);
        _copieDurataInCorso.Add(attivita);
    }

    /// <summary>TIME (6.6.1): durate delle nuove prove (operazioni eseguite cambiate). Un testo non valido blocca sempre; un vuoto
    /// blocca con la politica Master "durata obbligatoria" (non per i Test importati da UnitLogger, come nell'editor). Imposta <see cref="Errore"/>.</summary>
    private bool ValidaDurateNuoveProve(Dictionary<Guid, int?> durate)
    {
        var richiedeDurata = PoliticaDurataService.RichiedeDurata(_state.PoliticaDurata) && TestCollegato?.Origine != OrigineTest.ImportStatoUdt;
        foreach (var riga in Condizioni.Where(c => c.Cambiato && c.EsitoNuovo.EEseguito()))
        {
            if (!DurataOperazione.TryParse(riga.DurataTesto, out var minuti))
            {
                Errore = Tr("DurataNonValidaMsg");
                return false;
            }
            if (minuti is null && richiedeDurata)
            {
                Errore = Tr("ValDurataOperazioneObbl");
                return false;
            }
            durate[riga.Uid] = minuti;
        }
        return true;
    }

    private bool HaModificheCampi =>
        EntityStateFingerprint.Compute(Nc) != _fingerprintIniziale
        || !string.IsNullOrWhiteSpace(NuovoTicketRiferimento)
        || (SenzaTestCollegato && !string.IsNullOrWhiteSpace(TestIdDaCollegare));

    public bool HaModificheNonSalvate => HaModificheCampi || ModificaTestRichiesta ||
        !string.Equals(MotivoChiusura ?? "", Nc.MotivoChiusura ?? "", StringComparison.Ordinal);

    /// <summary>Rinotifica <see cref="HaModificheNonSalvate"/>. Va chiamato quando cambia un input che lo determina e che
    /// non e una proprieta del ViewModel: i campi del modello <see cref="Nc"/> sono legati direttamente ai controlli
    /// (TextBox/ComboBox) e il binding non notifica il ViewModel. Il valore e ricalcolato dallo stato, quindi un ritorno
    /// ai valori salvati spegne l'indicatore.</summary>
    public void SegnalaModificaCampo() => OnPropertyChanged(nameof(HaModificheNonSalvate));

    /// <summary>La severita e obbligatoria per le NC nuove o gia classificate; una NC legacy persistita senza
    /// severita puo essere salvata/chiusa senza essere costretta a classificarla (comportamento precedente).</summary>
    private bool SeveritaRichiesta => !Persistita || _severitaPresenteInApertura || Nc.Severita.HasValue;

    private void CaricaStatoDerivato()
    {
        _severitaPresenteInApertura = Nc.Severita.HasValue;
        _snapshotIniziale = EntityStateFingerprint.Clone(Nc);
        _fingerprintIniziale = EntityStateFingerprint.Compute(Nc);
        MotivoChiusura = Nc.MotivoChiusura ?? "";
        NuovoTicketRiferimento = "";
        NuovoTicketUrl = "";
        TestIdDaCollegare = "";
        foreach (var r in Condizioni) r.PropertyChanged -= OnRigaCambiata;
        Condizioni.Clear();
        TestCollegato = null;
        OrigineSelezionata = null;
        _revisioneTestAttesa = null;

        if (Persistita)
        {
            var test = RisolviTestCollegato();
            if (test != null && !test.Eliminato && test.Esecuzioni.Count > 0)
            {
                TestCollegato = test;
                _revisioneTestAttesa = test.RevisioneDati;
                StatoLavoroNuovo = test.StatoLavoro;
                foreach (var c in test.Esecuzioni[^1].CondizioniEffettive)
                {
                    var riga = new CondizioneEsitoRiga(c);
                    riga.PropertyChanged += OnRigaCambiata;
                    Condizioni.Add(riga);
                }
            }
        }
        NotificaTutto();
    }

    /// <summary>Test collegato riletto da disco (Esecuzioni complete), come fa ChiudiESalva.</summary>
    private TestCase? RisolviTestCollegato()
    {
        var perId = new Dictionary<string, TestCase>(StringComparer.OrdinalIgnoreCase);
        void Aggiungi(string? id, Guid? uid)
        {
            if (string.IsNullOrWhiteSpace(id) && !uid.HasValue) return;
            var t = uid.HasValue ? _state.TestCases.GetByUid(uid.Value) : _state.TestCases.GetById(id!);
            if (t != null) perId[t.Id] = t;
        }
        Aggiungi(Nc.TestCaseId, Nc.TestUid);
        foreach (var p in Nc.ProveCollegate) Aggiungi(p.TestId, p.TestUid);
        var corrente = AnomaliaWorkflowService.TestCorrente(Nc, perId);
        if (corrente == null) return null;
        try { return _state.TestCases.LoadCurrent(corrente) ?? corrente; }
        catch (Exception) { return corrente; }
    }

    private TestCase? TestPerApertura() => RisolviTestCollegato();

    [RelayCommand]
    private void ApriTestCollegato()
    {
        var test = TestPerApertura();
        if (test is { Eliminato: false }) _apriTest?.Invoke(test.Id);
    }

    private void NotificaTutto()
    {
        foreach (var n in new[]
        {
            nameof(PuoModificare), nameof(PuoEntrareInModifica), nameof(InModifica), nameof(SolaLettura), nameof(EMaster), nameof(Aperta), nameof(Chiusa), nameof(PuoChiudere),
            nameof(PuoRiaprire), nameof(PuoEliminare), nameof(SenzaTestCollegato), nameof(StatiWorkflow), nameof(TestCollegato),
            nameof(PuoRecuperareBozza), nameof(HaTestCollegato), nameof(MostraSezioneTest), nameof(TestCollegatoId), nameof(PuoAprireTest),
            nameof(VeicoliTest), nameof(DataChiusuraModificabile), nameof(ModificaTestRichiesta), nameof(HaModificheNonSalvate)
        })
            OnPropertyChanged(n);
    }

    public NonConformita CaricaBozzaRecuperabile(FailedSaveDraftInspector.DraftRow row)
    {
        if (!PuoRecuperareBozza || HaModificheNonSalvate || _snapshotIniziale == null)
            throw new InvalidOperationException(Tr("AnomaliaRecuperoSalvareAnnullarePrimaMsg"));
        return FailedSaveDraftRecoveryPreparationService.LoadNetworkNcForEditor(
            _state.Paths.Root, row, _snapshotIniziale, _snapshotIniziale.RevisioneDati);
    }

    public void ApplicaBozzaRecuperata(NonConformita draft, IReadOnlyCollection<string> selectedFields)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(selectedFields);
        var baseline = _snapshotIniziale;
        var selected = selectedFields.Distinct(StringComparer.Ordinal).ToArray();
        if (!PuoRecuperareBozza || HaModificheNonSalvate || baseline == null ||
            draft.Uid != baseline.Uid || draft.Id != baseline.Id ||
            draft.RevisioneDati > baseline.RevisioneDati || selected.Length == 0 ||
            selected.Any(field => !CampiBozzaRecuperabili.ContainsKey(field)))
            throw new InvalidOperationException(Tr("NcRevisioneCambiataImportaMsg"));

        var source = EntityStateFingerprint.Clone(draft);
        var staged = EntityStateFingerprint.Clone(baseline);
        foreach (var field in selected)
        {
            switch (field)
            {
                case nameof(NonConformita.Descrizione): staged.Descrizione = source.Descrizione; break;
                case nameof(NonConformita.DescrizioneBreveSegnalata): staged.DescrizioneBreveSegnalata = source.DescrizioneBreveSegnalata; break;
                case nameof(NonConformita.ConcessionariaOfficina): staged.ConcessionariaOfficina = source.ConcessionariaOfficina; break;
                case nameof(NonConformita.Owner): staged.Owner = source.Owner; break;
                case nameof(NonConformita.Severita): staged.Severita = source.Severita; break;
                case nameof(NonConformita.Priorita): staged.Priorita = source.Priorita; break;
                case nameof(NonConformita.Destinatario): staged.Destinatario = source.Destinatario; break;
                case nameof(NonConformita.CausaProbabile): staged.CausaProbabile = source.CausaProbabile; break;
                case nameof(NonConformita.CausaProbabileAltro): staged.CausaProbabileAltro = source.CausaProbabileAltro; break;
                case nameof(NonConformita.StatoDettaglio): staged.StatoDettaglio = source.StatoDettaglio; break;
                case nameof(NonConformita.MotivoChiusura): staged.MotivoChiusura = source.MotivoChiusura; break;
                case nameof(NonConformita.DataChiusura): staged.DataChiusura = source.DataChiusura; break;
                case nameof(NonConformita.EcuSegnalate): staged.EcuSegnalate = source.EcuSegnalate; break;
                case nameof(NonConformita.VeicoliSegnalati): staged.VeicoliSegnalati = source.VeicoliSegnalati; break;
                case nameof(NonConformita.FunzionalitaSegnalate): staged.FunzionalitaSegnalate = source.FunzionalitaSegnalate; break;
                case nameof(NonConformita.StrumentoSegnalato): staged.StrumentoSegnalato = source.StrumentoSegnalato; break;
                case nameof(NonConformita.ValoriStrumentoSegnalato): staged.ValoriStrumentoSegnalato = source.ValoriStrumentoSegnalato; break;
                case nameof(NonConformita.OperazioniSegnalate): staged.OperazioniSegnalate = source.OperazioniSegnalate; break;
                case nameof(NonConformita.NumeriPraticaRete): staged.NumeriPraticaRete = source.NumeriPraticaRete; break;
                case nameof(NonConformita.VinCliente): staged.VinCliente = source.VinCliente; break;
                case nameof(NonConformita.Licenze): staged.Licenze = source.Licenze; break;
                case nameof(NonConformita.Allegati): staged.Allegati = source.Allegati; break;
                case nameof(NonConformita.CollegamentiPolarion): staged.CollegamentiPolarion = source.CollegamentiPolarion; break;
            }
        }

        var severitaRichiestaAllApertura = _severitaPresenteInApertura;
        Nc = staged;
        CaricaStatoDerivato();
        _severitaPresenteInApertura = severitaRichiestaAllApertura;
        _snapshotIniziale = baseline;
        _fingerprintIniziale = EntityStateFingerprint.Compute(baseline);
        Errore = Tr("AnomaliaCampiImportatiRevisioneMsg");
        NotificaTutto();
    }

    /// <summary>True dopo un conflitto di revisione: l'input è ancora nel dialogo ma la revisione è superata.</summary>
    [ObservableProperty] private bool conflittoInAttesa;

    /// <summary>Carica la versione corrente dopo un conflitto. L'input era già stato conservato in bozza
    /// e si può riportare con "Recupera bozza locale…" (solo campi non sovrapposti).</summary>
    [RelayCommand]
    private void RicaricaDopoConflitto()
    {
        if (!ConflittoInAttesa) return;
        Ricarica();
        ConflittoInAttesa = false;
        Errore = Tr("AnomaliaConflittoRicaricata") +
            Tr("AnomaliaModifichePrecedentiBozzaMsg");
    }
    /// <summary>Rilegge NC e test dall'archivio scartando le modifiche non salvate (dopo un conflitto).</summary>
    public void Ricarica()
    {
        if (Persistita)
        {
            // Il conflitto può arrivare da un altro processo: GetById legge la cache dell'istanza corrente,
            // quindi riallinea prima il singolo record dal disco per mostrare davvero la versione canonica.
            _state.NonConformita.RefreshPaths(new[] { _state.NonConformita.FilePathOf(Nc) });
            var fresca = _state.NonConformita.GetById(Nc.Id)?.Clona();
            if (fresca == null) { Errore = LocalizationService.Instance.Traduci("AnomaliaNonTrovata"); return; }
            Nc = fresca;
        }
        CaricaStatoDerivato();
    }

    private string Tr(string chiave) => LocalizationService.Instance.Traduci(chiave);

    /// <summary>Applica a <paramref name="candidato"/> (copia di <see cref="Nc"/>) validazioni e commit dei campi
    /// "in sospeso": severita obbligatoria, ticket TICKET digitato, collegamento a un test.</summary>
    private bool PreparaCandidato(NonConformita candidato)
    {
        if (SeveritaRichiesta && !candidato.Severita.HasValue) { Errore = Tr("SeveritaObbl"); return false; }

        if (!AnomaliaAzioniService.TentaAggiungiTicket(candidato, NuovoTicketRiferimento, NuovoTicketUrl,
                _state.Config.OperatoreAttivo, out var chiaveErrore, out _, _state.Paths))
        {
            Errore = Tr(chiaveErrore!);
            return false;
        }

        if (SenzaTestCollegato && !string.IsNullOrWhiteSpace(TestIdDaCollegare))
        {
            var testId = TestIdDaCollegare.Trim();
            var test = _state.TestCases.GetById(testId);
            if (test == null || test.Eliminato)
            {
                Errore = string.Format(Tr("CollegaATestNonTrovatoFmt"), testId);
                return false;
            }
            candidato.TestCaseId = test.Id;
            candidato.TestUid = test.Uid;
            candidato.Changelog.Add(new ChangelogEntry
            {
                Data = DateTime.Now, Operatore = _state.Config.OperatoreAttivo, Campo = "collegamento a test", ValoreNuovo = test.Id
            });
        }
        return true;
    }

    /// <summary>Salvataggio dei soli campi, senza chiudere il dialogo. Ritorna true se riuscito.</summary>
    private bool SalvaCampi()
    {
        var candidato = Nc.Clona();
        if (!PreparaCandidato(candidato)) return false;
        if (Chiusa && !AnomaliaAzioniService.RegistraCorrezioneData(candidato, _snapshotIniziale, _state.Config.OperatoreAttivo))
        { Errore = Tr("AnomaliaDataChiusuraNonValida"); return false; }
        // Il motivo di chiusura di una NC gia chiusa resta modificabile (come nella vecchia pagina Test).
        if (Persistita && Chiusa) candidato.MotivoChiusura = (MotivoChiusura ?? "").Trim();
        if (!Persistita)
        {
            NcModificata = candidato;
            return true;
        }
        try { _state.NonConformita.Save(candidato, Nc.RevisioneDati); }
        catch (ConflittoRevisioneException ex)
        {
            // L'input resta nel dialogo e viene anche conservato in una bozza locale; la versione
            // dell'altro operatore si carica solo con RicaricaDopoConflitto, scelta esplicita dell'utente.
            var bozza = ConservaBozzaSuConflitto(candidato, ex);
            ConflittoInAttesa = true;
            Errore = Tr("AnomaliaConflittoInputPreservato") + (bozza ??
                Tr("AnomaliaImpossibileConservareBozzaMsg"));
            return false;
        }
        catch (Exception ex)
        {
            var recupero = ConservaBozzaFallita(candidato, ex);
            Errore = string.Format(Tr("SalvataggioNonRiuscitoFmt"), ex.Message) + recupero;
            return false;
        }
        Modificato = true;
        // Nessun CaricaStatoDerivato qui: perderebbe gli esiti/origine scelti per la chiusura con nuova REV.
        Nc = candidato;
        _snapshotIniziale = EntityStateFingerprint.Clone(candidato);
        _severitaPresenteInApertura = Nc.Severita.HasValue;
        _fingerprintIniziale = EntityStateFingerprint.Compute(Nc);
        NuovoTicketRiferimento = "";
        NuovoTicketUrl = "";
        TestIdDaCollegare = "";
        NotificaTutto();
        return true;
    }

    private string ConservaBozzaFallita(NonConformita candidato, Exception failure) =>
        ConservaBozzaSuConflitto(candidato, failure) ??
        (Persistita && _snapshotIniziale != null
            ? Tr("AnomaliaImpossibileConservareDialogoMsg")
            : "");

    /// <summary>Salva l'input in una bozza locale con fotografia di base. Null se la bozza non è stata scritta.</summary>
    private string? ConservaBozzaSuConflitto(NonConformita candidato, Exception failure)
    {
        if (!Persistita || _snapshotIniziale == null) return null;
        try
        {
            var path = FailedSaveDraftService.Save(_state.Paths.Root, "nonconformita",
                _snapshotIniziale.Uid, _snapshotIniziale.RevisioneDati,
                _state.Config.OperatoreAttivo, candidato, failure.Message,
                baseSnapshot: _snapshotIniziale);
            return Tr("BozzaLocaleConservataPrefisso") + path + Tr("AnomaliaBozzaNonApplicataSuffisso");
        }
        catch (Exception draftFailure)
        {
            CrashLogService.Registra("Bozza locale NC dopo errore nel dettaglio", draftFailure);
            return null;
        }
    }

    private string ConservaModificheDialogoFallite(Exception failure)
    {
        var motivoModificato = Persistita && Chiusa &&
            !string.Equals(MotivoChiusura ?? "", Nc.MotivoChiusura ?? "", StringComparison.Ordinal);
        if (!HaModificheCampi && !motivoModificato) return "";
        var candidato = Nc.Clona();
        if (!PreparaCandidato(candidato))
            return " I dati non validi restano nel dialogo e non sono stati inclusi nella bozza locale.";
        if (Persistita && Chiusa) candidato.MotivoChiusura = (MotivoChiusura ?? "").Trim();
        return ConservaBozzaFallita(candidato, failure);
    }

    [RelayCommand]
    private void Salva()
    {
        if (!PuoModificare) { Errore = Tr("AnomaliaSolaLettura"); return; }
        if (!SalvaCampi()) return;
        Errore = null;
        ChiusuraRichiesta?.Invoke();
    }

    [RelayCommand]
    private void Annulla() => ChiusuraRichiesta?.Invoke();

    [RelayCommand]
    private async Task Chiudi()
    {
        if (!PuoModificare) { Errore = Tr("AnomaliaSolaLettura"); return; }
        if (!Persistita || !Aperta) return;
        var motivo = (MotivoChiusura ?? "").Trim();
        if (motivo.Length == 0) { Errore = Tr("ChiudereAnomaliaMotivo"); return; }
        if (SeveritaRichiesta && !Nc.Severita.HasValue) { Errore = Tr("SeveritaObbl"); return; }

        var conREv = ModificaTestRichiesta;
        if (conREv && OrigineSelezionata == null) { Errore = Tr("AnomaliaOrigineObbl"); return; }
        var durate = new Dictionary<Guid, int?>();
        if (conREv && !ValidaDurateNuoveProve(durate)) return;

        var corpo = conREv
            ? string.Format(Tr("AnomaliaChiusuraRevConfermaBody"), OrigineSelezionata!.Etichetta)
            : Tr("ChiudiAnomaliaConfermaBody");
        if (!await ConfirmService.Chiedi(Tr("ChiudiAnomaliaConfermaTitolo"), corpo, Tr("ChiudiAnomaliaBtn"), Tr("Annulla")))
            return;
        if (!PuoModificare) { Errore = Tr("AnomaliaSolaLettura"); return; }

        var campiGiaSalvati = false;
        NonConformita? candidatoChiusura = null;
        try
        {
            if (!conREv)
            {
                candidatoChiusura = Nc.Clona();
                if (!PreparaCandidato(candidatoChiusura)) return;
                AnomaliaAzioniService.ApplicaChiusura(candidatoChiusura, _state.Config.OperatoreAttivo, motivo);
                _state.NonConformita.Save(candidatoChiusura, Nc.RevisioneDati);
            }
            else
            {
                // I campi modificati nel dialogo non fanno parte della chiusura atomica Test+NC (che rilegge la
                // NC dall'archivio): si salvano prima, poi la chiusura usa la revisione appena prodotta.
                if (HaModificheCampi) { if (!SalvaCampi()) return; campiGiaSalvati = true; }
                var nuovi = Condizioni.Where(c => c.Cambiato).ToDictionary(c => c.Uid, c => c.EsitoNuovo);
                AnomaliaChiusuraService.ChiudiESalva(
                    _state, TestCollegato!.Id, Nc.Id, nuovi,
                    StatoLavoroNuovo != TestCollegato.StatoLavoro ? StatoLavoroNuovo : null,
                    OrigineSelezionata!.Valore, _state.Config.OperatoreAttivo, DateTime.Now, motivo,
                    _revisioneTestAttesa, Nc.RevisioneDati, durate);
            }
        }
        catch (ConflittoRevisioneException ex)
        {
            var recupero = candidatoChiusura == null ? "" : ConservaBozzaFallita(candidatoChiusura, ex);
            ConflittoInAttesa = true;
            Errore = Tr("AnomaliaConflittoInputPreservato") + recupero;
            return;
        }
        catch (Exception ex)
        {
            var recupero = candidatoChiusura == null ? "" : ConservaBozzaFallita(candidatoChiusura, ex);
            Errore = string.Format(Tr(campiGiaSalvati ? "ChiusuraNonRiuscitaCampiSalvatiFmt" : "ChiusuraNonRiuscitaFmt"), ex.Message) + recupero;
            return;
        }

        Modificato = true;
        Errore = null;
        _state.RicaricaTutto();
        ChiusuraRichiesta?.Invoke();
    }

    [RelayCommand]
    private async Task Riapri()
    {
        if (!PuoRiaprire) return;
        var motivoRiaperturaPulito = (MotivoRiapertura ?? "").Trim();
        if (motivoRiaperturaPulito.Length == 0) { Errore = Tr("RiaperturaMotivoObbl"); return; }
        if (!await ConfirmService.Chiedi(Tr("RiapriAnomaliaConfermaTitolo"), Tr("RiapriAnomaliaConfermaBody"), Tr("RiapriAnomaliaBtn"), Tr("Annulla")))
            return;
        if (!PuoRiaprire) return;
        try { AnomaliaAzioniService.Riapri(_state, Nc, motivoRiaperturaPulito); }
        catch (ConflittoRevisioneException ex)
        {
            var recupero = ConservaModificheDialogoFallite(ex);
            ConflittoInAttesa = true;
            Errore = Tr("AnomaliaConflittoInputPreservato") + recupero;
            return;
        }
        catch (Exception ex)
        {
            var recupero = ConservaModificheDialogoFallite(ex);
            Errore = string.Format(Tr("SalvataggioNonRiuscitoFmt"), ex.Message) + recupero;
            return;
        }
        Modificato = true;
        Errore = null;
        ChiusuraRichiesta?.Invoke();
    }

    [RelayCommand]
    private async Task Elimina()
    {
        if (!PuoEliminare || !_state.EMaster) return;
        if (!await ConfirmService.Chiedi(Tr("EliminaAnomaliaConfermaTitolo"), Tr("EliminaAnomaliaConfermaBody"), Tr("EliminaBtn"), Tr("Annulla")))
            return;
        if (!PuoEliminare || !_state.EMaster) { Errore = Tr("ArchivioAzioneNonAutorizzata"); return; }
        try { AnomaliaAzioniService.Elimina(_state, Nc); }
        catch (ConflittoRevisioneException ex)
        {
            var recupero = ConservaModificheDialogoFallite(ex);
            ConflittoInAttesa = true;
            Errore = Tr("AnomaliaConflittoInputPreservato") + recupero;
            return;
        }
        catch (Exception ex)
        {
            var recupero = ConservaModificheDialogoFallite(ex);
            Errore = string.Format(Tr("SalvataggioNonRiuscitoFmt"), ex.Message) + recupero;
            return;
        }
        Modificato = true;
        Errore = null;
        _state.RicaricaTutto();
        ChiusuraRichiesta?.Invoke();
    }

    /// <summary>Riporta su una NC non persistita (in memoria nel Test) i campi modificabili dal dialogo.</summary>
    public void ApplicaA(NonConformita destinazione)
    {
        var src = NcModificata ?? Nc;
        destinazione.StatoWorkflow = src.StatoWorkflow;
        destinazione.Severita = src.Severita;
        destinazione.Priorita = src.Priorita;
        destinazione.Owner = src.Owner;
        destinazione.Destinatario = src.Destinatario;
        destinazione.CausaProbabile = src.CausaProbabile;
        destinazione.CausaProbabileAltro = src.CausaProbabileAltro;
        destinazione.StatoDettaglio = src.StatoDettaglio;
        destinazione.CollegamentiPolarion = src.CollegamentiPolarion.Select(c => new CollegamentoPolarion
        {
            Uid = c.Uid, Codice = c.Codice, Url = c.Url, CollegatoIl = c.CollegatoIl, CollegatoDa = c.CollegatoDa, Note = c.Note
        }).ToList();
    }
}
