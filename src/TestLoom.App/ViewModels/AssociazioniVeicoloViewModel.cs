using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Finestra "Componente e funzionalità" di un veicolo, aperta dalla pagina Veicoli. Vista dal veicolo: quali componente sono montate su
/// di esso (Componente.VeicoliAssociati) e, per ciascuna, quali funzionalità valgono su quel veicolo (vedi
/// <see cref="AssociazioniVeicoloService"/>). Lettura libera; modifica solo per il ruolo Master e con il lock di Componente e
/// Funzionalità. Il salvataggio scrive i due cataloghi in un'unica operazione atomica (<see cref="MasterDataSalvataggioAtomico"/>).
/// </summary>
public sealed partial class AssociazioniVeicoloViewModel : ViewModelBase, IDisposable
{
    private static readonly StringComparer Confronto = StringComparer.OrdinalIgnoreCase;

    private readonly AppState _state;
    private readonly string _veicolo;
    private readonly List<AssociazioneEcuRiga> _righe = new();
    private List<Componente> _ecuAttese = new();
    private List<Componente> _ecuLavoro = new();
    private List<Funzionalita> _funzAttese = new();
    private List<Funzionalita> _funzLavoro = new();
    private string? _impronteEcu;
    private string? _impronteFunzionalita;
    private bool _seguitoDaForzatura;
    private List<string> _percorsiAcquisiti = new();
    // Lock presi dal tentativo di IniziaModifica in corso: se la finestra si chiude durante l'acquisizione, Dispose li rilascia (P2.3).
    private List<string>? _acquisizioneInCorso;
    private readonly object _acquisizioneSync = new();
    private readonly object _heartbeatSync = new();
    private readonly Dictionary<string, string> _sessioniLock = new(StringComparer.OrdinalIgnoreCase);
    private System.Timers.Timer? _heartbeat;
    private volatile bool _sessioneAttiva;
    private volatile bool _disposto;
    private long _generazioneHeartbeat;
    private string _ricerca = "";
    private bool _soloMontate;
    private bool _inModifica;
    private bool _rilascioLockInAttesa;
    private bool _lockDetenutoDaAltri;
    private bool _avvisoLockNonConfermato;
    private string? _avvisoLockNonConfermatoTesto;
    private readonly MonitorRinnovoLock _monitorRinnovo = new();
    private string? _lockInfoTesto;
    private string? _messaggioErrore;
    private string? _messaggio;
    private bool _rilascioLockDopoSalvataggio;
    private bool _salvataggioInCorso;

    internal Func<LockService, string, string, Task<LockEnsureResult>>? RinnovaLockHeartbeatPerTest { get; set; }
    internal Func<LockService, string, string, EsitoIo>? RilascioSessioneLockPerTest { get; set; }
    internal Func<TimeSpan, Task>? AttesaRetryLockPerTest { get; set; }

    public AssociazioniVeicoloViewModel(AppState state, string veicolo)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _veicolo = veicolo ?? throw new ArgumentNullException(nameof(veicolo));
        RigheVisibili = new ObservableCollection<AssociazioneEcuRiga>();
        Ricarica();
        AggiornaStatoLockAltrui();
    }

    public string Veicolo => _veicolo;

    public bool SalvataggioInCorso
    {
        get => _salvataggioInCorso;
        private set
        {
            if (SetProperty(ref _salvataggioInCorso, value))
                OnPropertyChanged(nameof(InterazioneEditorAbilitata));
        }
    }

    public bool InterazioneEditorAbilitata => !SalvataggioInCorso;

    public string Titolo => string.Format(Testo("AssociazioniVeicoloTitoloFmt"), _veicolo);

    /// <summary>componente visibili nel dialogo (filtrate per ricerca e per "solo montate"); l'elenco resta fisso fino al prossimo filtro.</summary>
    public ObservableCollection<AssociazioneEcuRiga> RigheVisibili { get; }

    public string Ricerca
    {
        get => _ricerca;
        set { if (SetProperty(ref _ricerca, value ?? "")) RiapplicaFiltro(); }
    }

    public bool SoloMontate
    {
        get => _soloMontate;
        set { if (SetProperty(ref _soloMontate, value)) RiapplicaFiltro(); }
    }

    public string RiepilogoMontate => string.Format(Testo("AssociazioniVeicoloMontateFmt"), _righe.Count(r => r.Montata));

    public bool NessunaEcuDaMostrare => RigheVisibili.Count == 0;

    /// <summary>Vero solo tra "Modifica" e "Salva"/"Annulla": i controlli di associazione sono attivi solo in questo stato.</summary>
    public bool InModifica
    {
        get => _inModifica;
        private set { if (SetProperty(ref _inModifica, value)) NotificaStato(); }
    }

    public bool PuoModificare => _state.EMaster && _state.PuoScrivere;

    public bool RilascioLockInAttesa
    {
        get => _rilascioLockInAttesa;
        private set { if (SetProperty(ref _rilascioLockInAttesa, value)) NotificaStato(); }
    }

    public bool MostraModifica => !InModifica && !RilascioLockInAttesa && PuoModificare;

    public bool SoloLettura => !PuoModificare;

    public bool LockDetenutoDaAltri
    {
        get => _lockDetenutoDaAltri;
        private set => SetProperty(ref _lockDetenutoDaAltri, value);
    }

    /// <summary>Avviso persistente: il lock non si conferma da più rinnovi di seguito (P2.1). Si salva o si chiude.</summary>
    public bool AvvisoLockNonConfermato
    {
        get => _avvisoLockNonConfermato;
        private set => SetProperty(ref _avvisoLockNonConfermato, value);
    }

    public string? AvvisoLockNonConfermatoTesto
    {
        get => _avvisoLockNonConfermatoTesto;
        private set => SetProperty(ref _avvisoLockNonConfermatoTesto, value);
    }

    public string? LockInfoTesto
    {
        get => _lockInfoTesto;
        private set => SetProperty(ref _lockInfoTesto, value);
    }

    public string? MessaggioErrore
    {
        get => _messaggioErrore;
        private set { SetProperty(ref _messaggioErrore, value); OnPropertyChanged(nameof(HaMessaggioErrore)); }
    }

    public bool HaMessaggioErrore => !string.IsNullOrEmpty(MessaggioErrore);

    [RelayCommand]
    private async Task RiprovaRilascioLock()
    {
        if (!RilascioLockInAttesa) return;
        var dopoSalvataggio = _rilascioLockDopoSalvataggio;
        var percorsi = _percorsiAcquisiti.ToList();
        var sessioni = CatturaSessioniLock(percorsi);
        _percorsiAcquisiti = await RilasciaLockAsync(percorsi, sessioni);
        if (_percorsiAcquisiti.Count > 0)
        {
            MessaggioErrore = Testo(_rilascioLockDopoSalvataggio
                ? "SalvatoSbloccoLockNonRiuscito"
                : "LockSbloccoNonRiuscito");
            return;
        }

        RimuoviSessioniLockNonInAttesa(Array.Empty<string>());
        RilascioLockInAttesa = false;
        _rilascioLockDopoSalvataggio = false;
        MessaggioErrore = null;
        if (dopoSalvataggio) Messaggio = Testo("AssociazioniVeicoloSalvate");
        NotificaStato();
    }

    public string? Messaggio
    {
        get => _messaggio;
        private set { SetProperty(ref _messaggio, value); OnPropertyChanged(nameof(HaMessaggio)); }
    }

    public bool HaMessaggio => !string.IsNullOrEmpty(Messaggio);

    /// <summary>Vero se nei dati di lavoro c'è almeno una modifica rispetto alla lettura di apertura.</summary>
    public bool HaModificheDaSalvare => InModifica && ContaModifiche() > 0;

    [RelayCommand]
    private async Task IniziaModifica()
    {
        if (!PuoModificare || InModifica || RilascioLockInAttesa || _disposto) return;
        MessaggioErrore = null;
        Messaggio = null;
        var operatore = _state.Config.OperatoreAttivo;
        var percorsi = new[] { _state.Paths.EcuFile, _state.Paths.FunzionalitaFile };
        var acquisiti = new List<string>();
        lock (_acquisizioneSync) _acquisizioneInCorso = acquisiti;
        lock (_heartbeatSync) _sessioniLock.Clear();
        _seguitoDaForzatura = false; // per sessione: un Forza sblocco di una sessione precedente non vale per questa
        try
        {
            var letture = await Task.Run(() => percorsi.Select(p => _state.Locks.GetForeignActiveLock(p, operatore)).ToList());
            if (_disposto) return; // la finestra può chiudersi durante ogni attesa: vedi finally
            var altro = letture.FirstOrDefault(l => l.Stato == StatoLockAltrui.Occupato)?.Info;
            if (altro != null)
            {
                MostraLockAltrui(altro);
                return;
            }
            if (letture.Any(l => l.Stato == StatoLockAltrui.Sconosciuto))
            {
                // Lettura del lock non riuscita: lo stato non è "libero", quindi la modifica non parte (P2.2).
                MostraLockSconosciuto();
                return;
            }

            foreach (var percorso in percorsi)
            {
                if (_disposto) return; // nessun nuovo lock per una finestra già chiusa
                var esito = await LockGate.EseguiAsync(percorso, () => Task.Run(() => _state.Locks.TryAcquireNuovaSessione(percorso, operatore)));
                if (!esito.Acquisito)
                {
                    if (esito.DetenutoDa != null) MostraLockAltrui(esito.DetenutoDa);
                    else MessaggioErrore = Testo("AssociazioniVeicoloLockNonAcquisito");
                    return;
                }
                var sessioneLock = esito.DetenutoDa?.SessionId;
                if (string.IsNullOrWhiteSpace(sessioneLock))
                    throw new InvalidOperationException("Il lock acquisito non contiene una SessionId esatta.");
                lock (_heartbeatSync) _sessioniLock[percorso] = sessioneLock;
                lock (_acquisizioneSync) acquisiti.Add(percorso);
                _seguitoDaForzatura |= esito.SeguitoDaForzatura;
                if (_disposto) return; // chiusa durante l'acquisizione di questo lock: il finally rilascia quelli presi
            }

            // Fotografia fresca letta a lock acquisiti: è la baseline della sessione di modifica.
            if (_disposto) return;
            Ricarica();
            _percorsiAcquisiti = acquisiti;
            AvviaHeartbeat(operatore);
            LockDetenutoDaAltri = false;
            LockInfoTesto = null;
            InModifica = true;
            RinfrescaRighe();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Associazioni veicolo (IniziaModifica)", ex);
            MessaggioErrore = string.Format(Testo("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
        }
        finally
        {
            lock (_acquisizioneSync) _acquisizioneInCorso = null;
            // Modifica non partita (lock altrui, errore, finestra chiusa): i lock presi da questo tentativo tornano liberi.
            if (!InModifica)
            {
                var percorsiDaRilasciare = acquisiti.ToList();
                var sessioniDaRilasciare = CatturaSessioniLock(percorsiDaRilasciare);
                var falliti = await RilasciaLockAsync(percorsiDaRilasciare, sessioniDaRilasciare);
                lock (_acquisizioneSync) acquisiti.Clear();
                RimuoviSessioniLockNonInAttesa(falliti);
                if (falliti.Count > 0)
                {
                    if (_disposto)
                    {
                        DiagnosticTraceService.Registra("Associazioni.AcquisizioneParziale",
                            $"rilascio post-dispose fallito per {falliti.Count} lock: {string.Join(", ", falliti)}");
                    }
                    else
                    {
                        _percorsiAcquisiti = falliti;
                        RilascioLockInAttesa = true;
                        _rilascioLockDopoSalvataggio = false;
                        MessaggioErrore = Testo("LockSbloccoNonRiuscito");
                    }
                }
            }
        }
    }

    [RelayCommand]
    private async Task Salva()
    {
        if (SalvataggioInCorso) return;
        SalvataggioInCorso = true;
        try { await SalvaCoreAsync(); }
        finally { SalvataggioInCorso = false; }
    }

    private async Task SalvaCoreAsync()
    {
        if (!InModifica) return;
        if (!PuoModificare)
        {
            MessaggioErrore = Testo("AssociazioniVeicoloSolaLettura");
            return;
        }
        MessaggioErrore = null;
        Messaggio = null;
        var operatore = _state.Config.OperatoreAttivo;

        // Come nelle pagine master: il lock viene riconfermato prima di scrivere; un lock vivo di un'altra postazione
        // viene segnalato, ma la protezione reale resta la verifica di revisione/impronta del salvataggio.
        // Il rinnovo condivide il gate col timer e la chiusura, così l'I/O resta tracciato fino al suo completamento.
        (long Generazione, List<LockEnsureResult> Esiti, bool SessioneAttiva) rinnovoSalvataggio;
        try
        {
            rinnovoSalvataggio = await RinnovaLockSessioneAsync(operatore);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Associazioni veicolo (rinnovo lock pre-salvataggio)", ex);
            MessaggioErrore = Testo("LockSbloccoNonRiuscito");
            return;
        }
        if (!rinnovoSalvataggio.SessioneAttiva || rinnovoSalvataggio.Esiti.Count != PercorsiLock().Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            if (InModifica) MessaggioErrore = Testo("LockSbloccoNonRiuscito");
            return;
        }
        if (_disposto || !InModifica || !PuoModificare ||
            !StringComparer.Ordinal.Equals(operatore, _state.Config.OperatoreAttivo))
        {
            if (InModifica) MessaggioErrore = Testo("AssociazioniVeicoloSolaLettura");
            return;
        }
        RegistraEsitoRinnovo(PeggioreEsito(rinnovoSalvataggio.Esiti));

        // Le modifiche di registro (changelog, data, autore) si preparano su copie: un fallimento non deve sporcare i dati di lavoro.
        var ecuDaSalvare = Clona(_ecuLavoro);
        var funzDaSalvare = Clona(_funzLavoro);
        var cambiamenti = PreparaRegistro(ecuDaSalvare, funzDaSalvare, operatore);
        if (cambiamenti == 0)
        {
            await ChiudiSessioneAsync();
            if (RilascioLockInAttesa) return;
            Messaggio = Testo("AssociazioniVeicoloNessunaModifica");
            return;
        }

        try
        {
            MasterDataSalvataggioAtomico.Salva(_state.Paths, _state.Master,
                new(ecuDaSalvare, _ecuAttese, _impronteEcu),
                new(funzDaSalvare, _funzAttese, _impronteFunzionalita),
                _seguitoDaForzatura);
        }
        catch (MasterDataConflictException ex)
        {
            CrashLogService.Registra("Associazioni veicolo (conflitto di salvataggio)", ex);
            MessaggioErrore = Testo("AssociazioniVeicoloConflitto");
            return; // resta in modifica: le modifiche in corso non vanno perse, l'utente può annullare e riaprire
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Associazioni veicolo (Salva)", ex);
            MessaggioErrore = string.Format(Testo("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
            return;
        }

        _state.NotificaDatiRicaricati();
        await ChiudiSessioneAsync(dopoSalvataggio: true);
        Ricarica();
        RiapplicaFiltro();
        if (RilascioLockInAttesa) return;
        Messaggio = Testo("AssociazioniVeicoloSalvate");
    }

    [RelayCommand]
    private async Task Annulla()
    {
        if (!InModifica) return;
        if (HaModificheDaSalvare && !await ConfirmService.ConfermaAbbandonoModifiche(null)) return;
        await ChiudiSessioneAsync();
        if (RilascioLockInAttesa)
        {
            Ricarica();
            RiapplicaFiltro();
            return;
        }
        MessaggioErrore = null;
        Ricarica();
        RiapplicaFiltro();
    }

    /// <summary>Vero se la finestra può chiudersi: senza modifiche sì; con modifiche in corso chiede conferma di abbandono.</summary>
    public async Task<bool> ConfermaChiusuraAsync()
    {
        if (InModifica && HaModificheDaSalvare && !await ConfirmService.ConfermaAbbandonoModifiche(null)) return false;
        if (InModifica || RilascioLockInAttesa) return await ChiudiSessioneAsync();
        return true;
    }

    /// <summary>Rilascio dei lock e dell'heartbeat: chiamato alla chiusura della finestra.</summary>
    public void Dispose()
    {
        if (_disposto) return;
        _disposto = true;
        if (InModifica || RilascioLockInAttesa)
        {
            FermaHeartbeat();
            var percorsi = _percorsiAcquisiti.ToList();
            var sessioni = CatturaSessioniLock(percorsi);
            InModifica = false;
            RilascioLockInAttesa = false;
            _ = RilasciaLockDopoDisposeAsync(percorsi, sessioni);
            return;
        }
        FermaHeartbeat();
        // Chiusa durante l'acquisizione (P2.3): i lock già presi da questo tentativo si rilasciano subito. Quelli che
        // arrivassero dopo li rilascia la continuazione di IniziaModifica, che controlla _disposto dopo ogni attesa.
        List<string>? presi;
        lock (_acquisizioneSync) presi = _acquisizioneInCorso?.ToList();
        if (presi is { Count: > 0 })
        {
            var sessioni = CatturaSessioniLock(presi);
            _ = RilasciaLockDopoDisposeAsync(presi, sessioni, "rilascio durante acquisizione");
        }
    }

    // ---------------------------------------------------------------- azioni sulle associazioni (chiamate dalle righe)

    internal void CambiaEcuMontata(AssociazioneEcuRiga riga, bool montata)
    {
        if (!InModifica)
        {
            riga.NotificaMontata();
            return;
        }
        AssociazioniVeicoloService.ImpostaEcuMontata(riga.Componente, _veicolo, montata, _funzLavoro);
        RinfrescaRiga(riga);
        OnPropertyChanged(nameof(RiepilogoMontate));
    }

    [RelayCommand]
    private void SelezionaEcuVisibili() => ImpostaEcuVisibili(true);

    [RelayCommand]
    private void DeselezionaEcuVisibili() => ImpostaEcuVisibili(false);

    private void ImpostaEcuVisibili(bool montata)
    {
        if (!InModifica) return;
        foreach (var riga in RigheVisibili.ToList())
        {
            AssociazioniVeicoloService.ImpostaEcuMontata(riga.Componente, _veicolo, montata, _funzLavoro);
            RinfrescaRiga(riga);
        }
        RiapplicaFiltro();
        OnPropertyChanged(nameof(RiepilogoMontate));
    }

    internal void CambiaFunzione(AssociazioneFunzioneRiga riga, bool applicata)
    {
        if (!InModifica)
        {
            riga.NotificaApplicata();
            return;
        }
        AssociazioniVeicoloService.ImpostaFunzioneSulVeicolo(riga.Funzionalita, riga.Componente.Componente, _veicolo, applicata);
        RinfrescaRiga(riga.Componente);
    }

    internal void ImpostaFunzioniVisibili(AssociazioneEcuRiga riga, bool applicata)
    {
        if (!InModifica) return;
        foreach (var funzione in riga.Funzioni.ToList())
            AssociazioniVeicoloService.ImpostaFunzioneSulVeicolo(funzione.Funzionalita, riga.Componente, _veicolo, applicata);
        RinfrescaRiga(riga);
    }

    internal void AggiungiFunzione(AssociazioneEcuRiga riga)
    {
        if (!InModifica || riga.FunzioneDaAggiungere is not { } funzione) return;
        var soloQuestoVeicolo = riga.SoloQuestoVeicolo && riga.AltriVeicoliSulluEcu;
        AssociazioniVeicoloService.AggiungiFunzioneAdEcu(funzione, riga.Componente, _veicolo, soloQuestoVeicolo);
        riga.FunzioneDaAggiungere = null;
        RinfrescaRiga(riga);
    }

    internal void RinfrescaEspansione(AssociazioneEcuRiga riga) => RinfrescaRiga(riga);

    internal async Task SceltaFunzioneDaAggiungere(AssociazioneEcuRiga riga)
    {
        if (!InModifica || riga.Aggiungibili.Count == 0) return;
        var voci = riga.Aggiungibili
            .Select(f => new VoceSelezionabile { Valore = f, TestoPrincipale = f.Nome, TestoRicerca = f.Nome })
            .ToList();
        var scelta = await SelezionaDialogService.Scegli(string.Format(Testo("AssociazioniVeicoloSceltaFunzioneFmt"), riga.Nome), voci);
        if (scelta?.Valore is Funzionalita funzione) riga.FunzioneDaAggiungere = funzione;
    }

    // ---------------------------------------------------------------- stato interno

    /// <summary>Rilegge i cataloghi e ricostruisce copie di lavoro e baseline. Le impronte sono catturate PRIMA della lettura:
    /// una modifica remota nel frattempo fa fallire il salvataggio invece di passare inosservata.</summary>
    private void Ricarica()
    {
        var impronteEcu = _state.Master.Componente.CatturaImpronteCatalogoCorrente();
        var componente = _state.Master.Componente.Load();
        var impronteFunzionalita = _state.Master.Funzionalita.CatturaImpronteCatalogoCorrente();
        var funzionalita = _state.Master.Funzionalita.Load();

        _impronteEcu = impronteEcu;
        _impronteFunzionalita = impronteFunzionalita;
        _ecuAttese = Clona(componente);
        _ecuLavoro = Clona(componente);
        _funzAttese = Clona(funzionalita);
        _funzLavoro = Clona(funzionalita);
        CostruisciRighe();
        RiapplicaFiltro();
    }

    private void CostruisciRighe()
    {
        _righe.Clear();
        foreach (var componente in AssociazioniVeicoloService.EcuSelezionabili(_ecuLavoro))
            _righe.Add(new AssociazioneEcuRiga(this, componente));
        RinfrescaRighe();
    }

    private void RinfrescaRighe()
    {
        foreach (var riga in _righe) RinfrescaRiga(riga);
        OnPropertyChanged(nameof(RiepilogoMontate));
    }

    /// <summary>Riallinea una riga allo stato del modello: montaggio, funzioni applicabili, aggiungibili (solo se espansa).</summary>
    private void RinfrescaRiga(AssociazioneEcuRiga riga)
    {
        var nome = riga.Nome;
        riga.Aggiorna(
            montata: AssociazioniVeicoloService.EcuMontataSulVeicolo(riga.Componente, _veicolo),
            altriVeicoli: riga.Componente.VeicoliAssociati.Any(v => !Confronto.Equals(v, _veicolo)));

        if (!riga.Montata)
        {
            riga.SostituisciFunzioni(Array.Empty<AssociazioneFunzioneRiga>());
            riga.SostituisciAggiungibili(Array.Empty<Funzionalita>());
            return;
        }

        var funzioni = AssociazioniVeicoloService.FunzionalitaDellEcu(_funzLavoro, nome)
            .Select(f => new AssociazioneFunzioneRiga(this, riga, f, applicata: f.ValeSu(nome, _veicolo), ristretta: f.VeicoliRistretti(nome) != null))
            .ToList();
        riga.SostituisciFunzioni(funzioni);
        riga.SostituisciAggiungibili(riga.Espansa && InModifica
            ? AssociazioniVeicoloService.FunzionalitaAggiungibili(_funzLavoro, nome)
            : Array.Empty<Funzionalita>());
    }

    private void RiapplicaFiltro()
    {
        var q = (Ricerca ?? "").Trim();
        RigheVisibili.Clear();
        foreach (var riga in _righe)
        {
            if (SoloMontate && !riga.Montata) continue;
            if (q.Length > 0 && !riga.Nome.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            RigheVisibili.Add(riga);
        }
        OnPropertyChanged(nameof(NessunaEcuDaMostrare));
    }

    private void NotificaStato()
    {
        OnPropertyChanged(nameof(MostraModifica));
        OnPropertyChanged(nameof(PuoModificare));
        OnPropertyChanged(nameof(SoloLettura));
        foreach (var riga in _righe) riga.NotificaStatoModifica();
    }

    /// <summary>Numero di componente e funzionalità con differenze semantiche rispetto alla lettura di apertura (stesso criterio di
    /// <see cref="PreparaRegistro"/>), senza scrivere nulla.</summary>
    private int ContaModifiche()
    {
        var elementi = 0;
        var attese = _ecuAttese.ToDictionary(e => MasterEntityIdentityService.GetRequired(e));
        foreach (var componente in _ecuLavoro)
        {
            if (attese.TryGetValue(MasterEntityIdentityService.GetRequired(componente), out var prima) && DifferenzeEcu(prima, componente).Count > 0)
                elementi++;
        }
        var attesePerFunzione = _funzAttese.ToDictionary(f => MasterEntityIdentityService.GetRequired(f));
        foreach (var funzione in _funzLavoro)
        {
            if (attesePerFunzione.TryGetValue(MasterEntityIdentityService.GetRequired(funzione), out var prima) && DifferenzeFunzionalita(prima, funzione).Count > 0)
                elementi++;
        }
        return elementi;
    }

    /// <summary>Registro delle modifiche (come RegistraModifiche degli editor), sulle sole copie da salvare: per ogni campo con
    /// differenza semantica una voce di changelog con data e autore. Un elemento senza differenze semantiche (solo ordine o
    /// duplicati) torna identico alla baseline e quindi non viene riscritto. Ritorna il numero di elementi cambiati.</summary>
    private int PreparaRegistro(List<Componente> componente, List<Funzionalita> funzionalita, string operatore)
    {
        var elementi = 0;
        var attese = _ecuAttese.ToDictionary(e => MasterEntityIdentityService.GetRequired(e));
        for (var i = 0; i < componente.Count; i++)
        {
            if (!attese.TryGetValue(MasterEntityIdentityService.GetRequired(componente[i]), out var prima)) continue;
            var differenze = DifferenzeEcu(prima, componente[i]);
            if (differenze.Count == 0)
            {
                componente[i] = ClonaSingolo(prima);
                continue;
            }
            foreach (var (campo, vecchio, nuovo) in differenze) AggiungiRegistro(componente[i], campo, vecchio, nuovo, operatore);
            elementi++;
        }
        var attesePerFunzione = _funzAttese.ToDictionary(f => MasterEntityIdentityService.GetRequired(f));
        for (var i = 0; i < funzionalita.Count; i++)
        {
            if (!attesePerFunzione.TryGetValue(MasterEntityIdentityService.GetRequired(funzionalita[i]), out var prima)) continue;
            var differenze = DifferenzeFunzionalita(prima, funzionalita[i]);
            if (differenze.Count == 0)
            {
                funzionalita[i] = ClonaSingolo(prima);
                continue;
            }
            foreach (var (campo, vecchio, nuovo) in differenze) AggiungiRegistro(funzionalita[i], campo, vecchio, nuovo, operatore);
            elementi++;
        }
        return elementi;
    }

    /// <summary>Differenze semantiche di un componente: l'insieme dei veicoli montati, senza contare ordine e duplicati.</summary>
    private static List<(string Campo, string Vecchio, string Nuovo)> DifferenzeEcu(Componente prima, Componente dopo)
    {
        var differenze = new List<(string Campo, string Vecchio, string Nuovo)>();
        var vecchio = ChiaveVeicoliEcu(prima);
        var nuovo = ChiaveVeicoliEcu(dopo);
        if (vecchio != nuovo) differenze.Add(("VeicoliAssociatiTesto", vecchio, nuovo));
        return differenze;
    }

    /// <summary>Differenze semantiche di una funzionalità sui tre campi che la definiscono per i componenti.</summary>
    private static List<(string Campo, string Vecchio, string Nuovo)> DifferenzeFunzionalita(Funzionalita prima, Funzionalita dopo)
    {
        var differenze = new List<(string Campo, string Vecchio, string Nuovo)>();
        foreach (var (campo, valore) in CampiFunzionalita)
        {
            var vecchio = valore(prima);
            var nuovo = valore(dopo);
            if (vecchio != nuovo) differenze.Add((campo, vecchio, nuovo));
        }
        return differenze;
    }

    private static void AggiungiRegistro(MasterEntity item, string campo, string valorePrecedente, string valoreNuovo, string operatore)
    {
        var ora = DateTime.Now;
        item.Changelog.Add(new ChangelogEntry
        {
            Data = ora,
            Operatore = operatore,
            Campo = campo,
            ValorePrecedente = valorePrecedente,
            ValoreNuovo = valoreNuovo,
        });
        item.UltimaModifica = ora;
        item.ModificatoDa = operatore;
    }

    /// <summary>Campi di Funzionalità che entrano nel changelog (stessi nomi usati dagli editor per le colonne testuali).</summary>
    private static readonly (string Campo, Func<Funzionalita, string> Valore)[] CampiFunzionalita =
    {
        ("EcuAssociateTesto", f => ChiaveEcuAssociate(f)),
        ("TutteLeEcu", f => f.TutteLeEcu.ToString()),
        ("VeicoliPerEcuTesto", f => f.VeicoliPerEcuTesto),
    };

    /// <summary>Forma canonica dei veicoli montati su un componente: ordinata, senza vuoti né duplicati.</summary>
    private static string ChiaveVeicoliEcu(Componente componente) =>
        string.Join(", ", componente.VeicoliAssociati.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(Confronto).OrderBy(v => v, Confronto));

    /// <summary>Forma canonica dei componenti associate a una funzionalità: ordinata, senza vuoti né duplicati.</summary>
    private static string ChiaveEcuAssociate(Funzionalita funzionalita) =>
        string.Join(", ", funzionalita.EcuAssociate.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(Confronto).OrderBy(v => v, Confronto));

    private static T ClonaSingolo<T>(T valore) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(valore, JsonIo.Options), JsonIo.Options)!;

    private IEnumerable<string> PercorsiLock() => new[] { _state.Paths.EcuFile, _state.Paths.FunzionalitaFile };

    private void AggiornaStatoLockAltrui()
    {
        var letture = PercorsiLock()
            .Select(p => _state.Locks.GetForeignActiveLock(p, _state.Config.OperatoreAttivo))
            .ToList();
        var altro = letture.FirstOrDefault(l => l.Stato == StatoLockAltrui.Occupato)?.Info;
        if (altro != null) MostraLockAltrui(altro);
        else if (letture.Any(l => l.Stato == StatoLockAltrui.Sconosciuto)) MostraLockSconosciuto();
    }

    private void MostraLockAltrui(LockInfo? info)
    {
        if (info == null) return;
        LockDetenutoDaAltri = true;
        LockInfoTesto = string.Format(Testo("LockInModificaDaFmt"), info.Operatore, info.AcquisitoIl);
    }

    /// <summary>Stato del lock non verificabile (lettura fallita o scaduta): non è libero, quindi blocca l'avvio (P2.2).</summary>
    private void MostraLockSconosciuto()
    {
        LockDetenutoDaAltri = true;
        LockInfoTesto = Testo("LockStatoSconosciuto");
    }

    /// <summary>Interpreta l'esito di un rinnovo sul thread UI (P2.1): un detentore altrui mostra il banner di conflitto; un
    /// rinnovo non confermato viene registrato e, dopo la soglia, diventa un avviso persistente finché non torna confermato.</summary>
    private void RegistraEsitoRinnovo(LockEnsureResult esito)
    {
        switch (_monitorRinnovo.Registra(esito))
        {
            case EsitoRinnovoLock.NonPosseduto:
                MostraLockAltrui(esito.Holder);
                break;
            case EsitoRinnovoLock.NonConfermato:
                CrashLogService.Registra("Associazioni veicolo (rinnovo lock non confermato)", null);
                break;
        }
        AggiornaAvvisoLockNonConfermato();
    }

    private void AggiornaAvvisoLockNonConfermato()
    {
        AvvisoLockNonConfermato = _monitorRinnovo.AvvisoAttivo;
        AvvisoLockNonConfermatoTesto = AvvisoLockNonConfermato ? Testo("LockNonConfermatoSalvaOChiudi") : null;
    }

    /// <summary>Esito più grave fra quelli dei percorsi di un rinnovo: prima un detentore altrui, poi un rinnovo non confermato.</summary>
    private static LockEnsureResult PeggioreEsito(IEnumerable<LockEnsureResult> esiti)
    {
        var lista = esiti.ToList();
        return lista.FirstOrDefault(e => e.Holder != null)
            ?? lista.FirstOrDefault(e => !(e.Posseduto && e.Confermato))
            ?? lista.FirstOrDefault()
            ?? new LockEnsureResult(true, null, false, Confermato: true);
    }

    /// <summary>Chiude la sessione di modifica: ferma il heartbeat, rilascia i lock acquisiti da questa finestra, torna in lettura.</summary>
    private async Task<bool> ChiudiSessioneAsync(bool dopoSalvataggio = false)
    {
        FermaHeartbeat();
        var percorsi = _percorsiAcquisiti.ToList();
        var sessioni = CatturaSessioniLock(percorsi);
        _percorsiAcquisiti = await RilasciaLockAsync(percorsi, sessioni);
        RimuoviSessioniLockNonInAttesa(_percorsiAcquisiti);
        InModifica = false;
        RilascioLockInAttesa = _percorsiAcquisiti.Count > 0;
        _rilascioLockDopoSalvataggio = RilascioLockInAttesa && dopoSalvataggio;
        if (RilascioLockInAttesa)
            MessaggioErrore = Testo(_rilascioLockDopoSalvataggio
                ? "SalvatoSbloccoLockNonRiuscito"
                : "LockSbloccoNonRiuscito");
        return !RilascioLockInAttesa;
    }

    private Dictionary<string, string> CatturaSessioniLock(IEnumerable<string> percorsi)
    {
        lock (_heartbeatSync)
        {
            return percorsi.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(_sessioniLock.ContainsKey)
                .ToDictionary(percorso => percorso, percorso => _sessioniLock[percorso], StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<List<string>> RilasciaLockAsync(
        IEnumerable<string> percorsi,
        IReadOnlyDictionary<string, string> sessioni)
    {
        var falliti = new List<string>();
        foreach (var percorso in percorsi.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!sessioni.TryGetValue(percorso, out var sessione) || string.IsNullOrWhiteSpace(sessione))
                continue; // Nessun token locale significa che questa finestra non può rilasciare il lock.

            try
            {
                var esito = await LockGate.RilasciaSessioneEsattaConRetryAsync(_state.Locks, percorso, sessione)
                    .ConfigureAwait(false);
                if (esito != EsitoIo.Ok)
                {
                    falliti.Add(percorso);
                    continue;
                }

                lock (_heartbeatSync)
                {
                    if (_sessioniLock.TryGetValue(percorso, out var attuale) &&
                        StringComparer.Ordinal.Equals(attuale, sessione))
                        _sessioniLock.Remove(percorso);
                }
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Associazioni veicolo (rilascio lock)", ex);
                falliti.Add(percorso);
            }
        }

        return falliti;
    }

    private void RimuoviSessioniLockNonInAttesa(IEnumerable<string> percorsiInAttesa)
    {
        var daMantenere = new HashSet<string>(percorsiInAttesa, StringComparer.OrdinalIgnoreCase);
        lock (_heartbeatSync)
        {
            foreach (var percorso in _sessioniLock.Keys.Where(p => !daMantenere.Contains(p)).ToList())
                _sessioniLock.Remove(percorso);
        }
    }

    private async Task RilasciaLockDopoDisposeAsync(
        IReadOnlyCollection<string> percorsi,
        IReadOnlyDictionary<string, string> sessioni,
        string contesto = "rilascio lock alla chiusura forzata")
    {
        try
        {
            var falliti = await RilasciaLockAsync(percorsi, sessioni).ConfigureAwait(false);
            if (falliti.Count > 0)
            {
                DiagnosticTraceService.Registra("Associazioni.Dispose",
                    $"{contesto} fallito per {falliti.Count} lock: {string.Join(", ", falliti)}");
            }
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Associazioni veicolo (rilascio lock dopo Dispose)", ex);
        }
    }

    private void AvviaHeartbeat(string operatore)
    {
        FermaHeartbeat();
        lock (_heartbeatSync)
        {
            _sessioneAttiva = true;
            _generazioneHeartbeat++;
        }
        _heartbeat = new System.Timers.Timer(LockService.IntervalloHeartbeat.TotalMilliseconds) { AutoReset = true };
        _heartbeat.Elapsed += async (_, _) => await RinnovaLock(operatore);
        _heartbeat.Start();
    }

    private async Task RinnovaLock(string operatore)
    {
        try
        {
            var rinnovo = await RinnovaLockSessioneAsync(operatore).ConfigureAwait(false);
            if (rinnovo.Esiti.Count > 0)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    lock (_heartbeatSync)
                    {
                        if (_sessioneAttiva && !_disposto && _generazioneHeartbeat == rinnovo.Generazione)
                            RegistraEsitoRinnovo(PeggioreEsito(rinnovo.Esiti));
                    }
                });
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Associazioni veicolo (heartbeat lock)", ex);
        }
    }

    private async Task<(long Generazione, List<LockEnsureResult> Esiti, bool SessioneAttiva)> RinnovaLockSessioneAsync(string operatore)
    {
        long generazione;
        lock (_heartbeatSync)
        {
            if (!_sessioneAttiva || _disposto)
                return (_generazioneHeartbeat, new List<LockEnsureResult>(), false);
            generazione = _generazioneHeartbeat;
        }

        var esiti = new List<LockEnsureResult>();
        foreach (var percorso in PercorsiLock().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            LockEnsureResult? esitoDaRegistrare = null;
            await LockGate.EseguiAsync(percorso, async () =>
            {
                string sessioneAttesa;
                lock (_heartbeatSync)
                {
                    if (!_sessioneAttiva || _disposto || _generazioneHeartbeat != generazione ||
                        !_sessioniLock.TryGetValue(percorso, out var sessioneCorrente)) return;
                    sessioneAttesa = sessioneCorrente;
                }

                var esito = RinnovaLockHeartbeatPerTest is { } rinnovaPerTest
                    ? await rinnovaPerTest(_state.Locks, percorso, operatore).ConfigureAwait(false)
                    : await _state.Locks.EnsureOwnedTrackedAsync(percorso, operatore).ConfigureAwait(false);

                bool sessioneAncoraAttiva;
                lock (_heartbeatSync)
                {
                    sessioneAncoraAttiva = _sessioneAttiva && !_disposto &&
                        _generazioneHeartbeat == generazione &&
                        _sessioniLock.TryGetValue(percorso, out var sessioneCorrente) &&
                        StringComparer.Ordinal.Equals(sessioneCorrente, sessioneAttesa);
                    if (sessioneAncoraAttiva && esito.Posseduto && !string.IsNullOrWhiteSpace(esito.SessionId))
                        _sessioniLock[percorso] = esito.SessionId;
                }

                if (!sessioneAncoraAttiva)
                {
                    // Se la callback ha ottenuto un lease nuovo dopo la chiusura o dopo un cambio di
                    // sessione, rimuove solo quel token esatto prima di cedere il gate del percorso.
                    if (!string.IsNullOrWhiteSpace(esito.SessionId) &&
                        (esito.Riacquisito || !StringComparer.Ordinal.Equals(esito.SessionId, sessioneAttesa)))
                    {
                        await LockGate.RilasciaSessioneEsattaConRetrySottoGateAsync(
                            _state.Locks, percorso, esito.SessionId,
                            RilascioSessioneLockPerTest, AttesaRetryLockPerTest).ConfigureAwait(false);
                    }
                    return;
                }

                esitoDaRegistrare = esito;
            }).ConfigureAwait(false);

            lock (_heartbeatSync)
            {
                if (!_sessioneAttiva || _disposto || _generazioneHeartbeat != generazione) break;
            }
            if (esitoDaRegistrare != null) esiti.Add(esitoDaRegistrare);
        }

        bool sessioneAttiva;
        lock (_heartbeatSync)
            sessioneAttiva = _sessioneAttiva && !_disposto && _generazioneHeartbeat == generazione;
        return (generazione, esiti, sessioneAttiva);
    }

    private void FermaHeartbeat()
    {
        System.Timers.Timer? heartbeat;
        lock (_heartbeatSync)
        {
            _sessioneAttiva = false;
            _generazioneHeartbeat++;
            heartbeat = _heartbeat;
            _heartbeat = null;
        }
        heartbeat?.Stop();
        heartbeat?.Dispose();
        _monitorRinnovo.Azzera();
        AggiornaAvvisoLockNonConfermato();
    }

    private static List<T> Clona<T>(IEnumerable<T> elementi) =>
        JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(elementi.ToList(), JsonIo.Options), JsonIo.Options) ?? new List<T>();

    private static string Testo(string chiave) => LocalizationService.Instance.Traduci(chiave);
}

/// <summary>Riga componente della finestra: casella di montaggio sul veicolo, funzionalità applicabili espandibili, aggiunta di una
/// funzionalità non ancora associata al componente.</summary>
public sealed class AssociazioneEcuRiga : ViewModelBase
{
    private readonly AssociazioniVeicoloViewModel _vm;
    private bool _montata;
    private bool _espansa;
    private bool _altriVeicoli;
    private bool _soloQuestoVeicolo = true;
    private Funzionalita? _funzioneDaAggiungere;

    internal AssociazioneEcuRiga(AssociazioniVeicoloViewModel vm, Componente componente)
    {
        _vm = vm;
        Componente = componente;
        AggiungiFunzioneCommand = new RelayCommand(() => _vm.AggiungiFunzione(this), () => _vm.InModifica && FunzioneDaAggiungere != null);
        SceltaFunzioneCommand = new AsyncRelayCommand(() => _vm.SceltaFunzioneDaAggiungere(this));
        AlternaEspansioneCommand = new RelayCommand(() => Espansa = !Espansa);
        SelezionaFunzioniVisibiliCommand = new RelayCommand(() => _vm.ImpostaFunzioniVisibili(this, true));
        DeselezionaFunzioniVisibiliCommand = new RelayCommand(() => _vm.ImpostaFunzioniVisibili(this, false));
    }

    public Componente Componente { get; }

    public string Nome => Componente.Nome;

    public AssociazioniVeicoloViewModel Vm => _vm;

    /// <summary>Dettaglio funzioni visibile solo per componente montata ed espansa.</summary>
    public bool MostraDettaglio => Montata && Espansa;

    public bool NessunaFunzione => Funzioni.Count == 0;

    public string TestoEspansione => (Espansa ? "▾ " : "▸ ") + TestoFunzioni;

    public bool Montata
    {
        get => _montata;
        set => _vm.CambiaEcuMontata(this, value);
    }

    public int NumeroVeicoli => Componente.VeicoliAssociati.Count;

    public bool AltriVeicoliSulluEcu => _altriVeicoli;

    /// <summary>Scelta "solo su questo veicolo" per la funzionalità da aggiungere: visibile solo se il componente ha altri veicoli.</summary>
    public bool SoloQuestoVeicolo
    {
        get => _soloQuestoVeicolo;
        set => SetProperty(ref _soloQuestoVeicolo, value);
    }

    public bool Espansa
    {
        get => _espansa;
        set
        {
            if (!SetProperty(ref _espansa, value)) return;
            OnPropertyChanged(nameof(TestoEspansione));
            OnPropertyChanged(nameof(MostraDettaglio));
            _vm.RinfrescaEspansione(this);
        }
    }

    public ObservableCollection<AssociazioneFunzioneRiga> Funzioni { get; } = new();

    public ObservableCollection<Funzionalita> Aggiungibili { get; } = new();

    public Funzionalita? FunzioneDaAggiungere
    {
        get => _funzioneDaAggiungere;
        set
        {
            if (!SetProperty(ref _funzioneDaAggiungere, value)) return;
            OnPropertyChanged(nameof(TestoSceltaFunzione));
            AggiungiFunzioneCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Testo del pulsante di scelta oltre soglia: la funzionalità scelta, altrimenti il segnaposto.</summary>
    public string TestoSceltaFunzione => FunzioneDaAggiungere?.Nome ?? LocalizationService.Instance.Traduci("AssociazioniVeicoloSceltaFunzioneBtn");

    public string TestoFunzioni => string.Format(LocalizationService.Instance.Traduci("AssociazioniVeicoloFunzioniFmt"), Funzioni.Count);

    public string TestoVeicoli => string.Format(LocalizationService.Instance.Traduci("AssociazioniVeicoloVeicoliFmt"), NumeroVeicoli);

    public IRelayCommand AggiungiFunzioneCommand { get; }

    public IAsyncRelayCommand SceltaFunzioneCommand { get; }

    public IRelayCommand AlternaEspansioneCommand { get; }

    public IRelayCommand SelezionaFunzioniVisibiliCommand { get; }

    public IRelayCommand DeselezionaFunzioniVisibiliCommand { get; }

    internal void Aggiorna(bool montata, bool altriVeicoli)
    {
        _altriVeicoli = altriVeicoli;
        _montata = montata;
        OnPropertyChanged(nameof(Montata));
        OnPropertyChanged(nameof(AltriVeicoliSulluEcu));
        OnPropertyChanged(nameof(NumeroVeicoli));
        OnPropertyChanged(nameof(TestoVeicoli));
        OnPropertyChanged(nameof(MostraDettaglio));
    }

    internal void NotificaMontata() => OnPropertyChanged(nameof(Montata));

    internal void NotificaStatoModifica() => AggiungiFunzioneCommand.NotifyCanExecuteChanged();

    /// <summary>Sostituisce la lista delle funzioni applicabili (chiamata dal ViewModel su ogni riallineamento).</summary>
    internal void SostituisciFunzioni(IEnumerable<AssociazioneFunzioneRiga> funzioni)
    {
        Funzioni.Clear();
        foreach (var funzione in funzioni) Funzioni.Add(funzione);
        OnPropertyChanged(nameof(TestoFunzioni));
        OnPropertyChanged(nameof(TestoEspansione));
        OnPropertyChanged(nameof(NessunaFunzione));
        OnPropertyChanged(nameof(MostraDettaglio));
    }

    internal void SostituisciAggiungibili(IEnumerable<Funzionalita> aggiungibili)
    {
        Aggiungibili.Clear();
        foreach (var funzione in aggiungibili) Aggiungibili.Add(funzione);
        if (FunzioneDaAggiungere != null && !Aggiungibili.Contains(FunzioneDaAggiungere)) FunzioneDaAggiungere = null;
    }

}

/// <summary>Riga funzione dentro un componente: applicabilità sul veicolo, con indicazione di restrizione per veicoli.</summary>
public sealed class AssociazioneFunzioneRiga : ViewModelBase
{
    private readonly AssociazioniVeicoloViewModel _vm;
    private bool _applicata;
    private bool _ristretta;

    internal AssociazioneFunzioneRiga(AssociazioniVeicoloViewModel vm, AssociazioneEcuRiga componente, Funzionalita funzionalita, bool applicata, bool ristretta)
    {
        _vm = vm;
        Componente = componente;
        Funzionalita = funzionalita;
        _applicata = applicata;
        _ristretta = ristretta;
    }

    public AssociazioneEcuRiga Componente { get; }

    public Funzionalita Funzionalita { get; }

    public string Nome => Funzionalita.Nome;

    /// <summary>Etichetta di scopo: "(tutti i componenti)" per le funzionalità valide su ogni componente, "(solo alcuni veicoli)" se questa componente ha una restrizione.</summary>
    public string Etichetta
    {
        get
        {
            var parti = new List<string>();
            if (Funzionalita.TutteLeEcu) parti.Add(LocalizationService.Instance.Traduci("AssociazioniVeicoloTutteLeEcu"));
            if (_ristretta) parti.Add(LocalizationService.Instance.Traduci("AssociazioniVeicoloSoloAlcuni"));
            return string.Join(" ", parti);
        }
    }

    public bool Applicata
    {
        get => _applicata;
        set => _vm.CambiaFunzione(this, value);
    }

    internal void NotificaApplicata() => OnPropertyChanged(nameof(Applicata));
}
