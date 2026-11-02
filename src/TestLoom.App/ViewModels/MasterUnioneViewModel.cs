using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Coppia di voci di un catalogo master probabilmente nate da un errore di battitura: stesso testo a
/// meno di maiuscole/spazi/punteggiatura (stesso criterio di WuCoppiaSospetta/UdtLogImportService.NormalizzaPerConfronto,
/// mai una somiglianza fuzzy — spec 2026-09-23, punto 5).</summary>
public sealed record CoppiaSospettaMaster(string A, string B);

/// <summary>
/// ViewModel generico di Unisci/Rinomina/Dividi per un catalogo master (Componente, Veicolo, Funzionalita, Gamma,
/// Categoria, StrumentoDiagnostico, Operatore, Requisito...), stesso modello UX/anteprima/conferma di
/// WuStatusViewModel.Unisci ma riusabile per QUALUNQUE T registrato in MasterRiferimentiService.Registro invece
/// di essere riscritto per ciascun catalogo. Orchestrazione transazionale in MasterUnioneService, ricerca/
/// sostituzione dei riferimenti in MasterRiferimentiService — questo file resta solo binding/UX (comandi,
/// conferme, messaggi localizzati).
///
/// Composta dentro MasterDataEditorViewModelBase&lt;T&gt; (vedi UnioneRiferimenti/proprietà correlate lì): non
/// serve creare una sottoclasse per ogni editor, l'istanza viene creata una volta con il repo/keyOf già noti
/// alla base class.
/// </summary>
public partial class MasterUnioneViewModel<T> : ViewModelBase where T : MasterEntity, new()
{
    private readonly AppState _state;
    private readonly MasterDataRepository<T> _repo;
    private readonly Func<T, string> _keyOf;
    private readonly string _catalogo;
    private readonly Func<bool>? _inModifica;

    /// <summary>Falso se T non ha alcun campo registrato come riferimento in nessun altro record (es. un
    /// catalogo futuro non ancora inventariato in MasterRiferimentiService.Registro): la UI nasconde i pulsanti
    /// Unisci/Rinomina/Dividi in quel caso invece di offrire un'azione che non propagherebbe nulla.</summary>
    public bool Disponibile => MasterRiferimentiService.Registro.Any(r => r.TipoNodo == typeof(T) || r.Catalogo == _catalogo);

    public bool EMaster => _state.EMaster;

    [ObservableProperty] private string? erroreOperazione;
    public bool HaErroreOperazione => !string.IsNullOrWhiteSpace(ErroreOperazione);
    partial void OnErroreOperazioneChanged(string? value) => OnPropertyChanged(nameof(HaErroreOperazione));

    [ObservableProperty] private string? messaggioEsito;
    public bool HaMessaggioEsito => !string.IsNullOrWhiteSpace(MessaggioEsito);
    partial void OnMessaggioEsitoChanged(string? value) => OnPropertyChanged(nameof(HaMessaggioEsito));

    /// <summary>Vero mentre Unisci/Rinomina/Dividi lavora in background (review 2026-09-23, MEDIUM: a scala
    /// l'operazione può durare minuti — prima girava sul thread chiamante, bloccando la UI senza alcun
    /// avanzamento visibile). Guardia anche contro l'esecuzione doppia: un secondo tentativo mentre IsBusy è
    /// vero viene rifiutato subito, prima ancora di mostrare l'anteprima di conferma.</summary>
    [ObservableProperty] private bool isBusy;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(PannelloAbilitato));

    /// <summary>Vero se l'editor proprietario e' in modifica: Unisci/Rinomina/Dividi al termine ricaricano la
    /// tabella (OperazioneCompletata -> Ricarica) e scarterebbero in silenzio le modifiche locali non salvate.
    /// Per questo il pannello resta disabilitato finche' si e' in modifica e ogni operazione rifiuta comunque
    /// (guardia anche per chiamate dirette, es. coppie sospette).</summary>
    public bool InModifica => _inModifica?.Invoke() == true;

    /// <summary>Abilitazione del pannello azioni: ne' occupato ne' in modifica.</summary>
    public bool PannelloAbilitato => !IsBusy && !InModifica;

    /// <summary>Visibilita' del pannello Unisci/Rinomina/Dividi (feedback utente 2026-10-01): in modifica le
    /// azioni non sono eseguibili, quindi il pannello sparisce del tutto invece di restare visibile ma
    /// disabilitato, e la griglia recupera lo spazio.</summary>
    public bool MostraPannello => EMaster && Disponibile && !InModifica;

    /// <summary>Banner coppie sospette: stesso criterio di MostraPannello (l'"Unisci rapido" e' rifiutato in
    /// modifica, vedi BloccatoDaModifica).</summary>
    public bool MostraCoppieSospette => HaCoppieSospette && !InModifica;

    /// <summary>Intestazione dell'Expander del pannello: titolo + voce selezionata, cosi' a pannello chiuso
    /// si vede comunque su quale voce si agirebbe.</summary>
    public string TitoloPannello
    {
        get
        {
            var titolo = LocalizationService.Instance.Traduci("MasterUnioneSezioneTitolo");
            return HaSelezione ? titolo + " — " + ChiaveSelezionata : titolo;
        }
    }

    /// <summary>Da richiamare dall'editor proprietario quando InModifica cambia.</summary>
    public void NotificaInModificaCambiata()
    {
        OnPropertyChanged(nameof(InModifica));
        OnPropertyChanged(nameof(PannelloAbilitato));
        OnPropertyChanged(nameof(MostraPannello));
        OnPropertyChanged(nameof(MostraCoppieSospette));
    }

    private bool BloccatoDaModifica()
    {
        if (!InModifica) return false;
        ErroreOperazione = LocalizationService.Instance.Traduci("MasterUnioneSalvaPrimaHint");
        return true;
    }

    /// <summary>Avanzamento "record fatti / record stimati", aggiornato dal thread di lavoro mostrando
    /// N conteggiati sull'anteprima già calcolata per la conferma (nessuna doppia scansione più pesante).</summary>
    [ObservableProperty] private string? progressoTesto;

    public List<CoppiaSospettaMaster> CoppieSospette { get; private set; } = new();
    public bool HaCoppieSospette => CoppieSospette.Count > 0;

    /// <summary>Notificato dal chiamante dopo Unisci/Rinomina/Dividi riuscita: la base class ricarica la
    /// tabella (stesso schema di WuStatusViewModel.Ricarica dopo Unisci).</summary>
    public event Action? OperazioneCompletata;

    // --- Pannello azioni inline nella pagina Master (stesso pattern di WuStatusView: riga selezionata in
    // griglia + azione dal pannello sotto, niente finestra modale separata) ---------------------------------

    // Voci del catalogo per il picker "Unisci in": null = da leggere dal fornitore alla prima necessita' (lazy), cosi'
    // i cataloghi paginati non rileggono tutte le chiavi a ogni apertura dell'editor, ma solo quando si sceglie una riga.
    private List<string>? _vociCatalogoAttuali;
    private Func<IEnumerable<string>>? _fornitoreVoci;

    /// <summary>Chiave (Nome/Id) della riga selezionata nella griglia — valorizzata dalla base class quando
    /// cambia DataGrid.SelectedItem (vedi MasterDataEditorViewModelBase.ElementoSelezionato).</summary>
    [ObservableProperty] private string? chiaveSelezionata;

    public bool HaSelezione => !string.IsNullOrWhiteSpace(ChiaveSelezionata);
    partial void OnChiaveSelezionataChanged(string? value)
    {
        OnPropertyChanged(nameof(HaSelezione));
        _altreVociMemo = null;
        OnPropertyChanged(nameof(AltreVociPerUnione));
        OnPropertyChanged(nameof(TitoloPannello));
        VocePerUnioneSelezionata = null;
        NuovoNomeRinomina = value ?? "";
        VociDivisioneTesto = "";
    }

    /// <summary>Tutte le voci del catalogo tranne quella selezionata (stesso principio di
    /// WuStatusViewModel.AltreVociPerUnione): scelta libera del Master, non limitata alle coppie sospette.</summary>
    // Memorizzato: il picker la legge ad ogni notifica di binding; cambia solo con la selezione o con le voci del catalogo
    // (invalidate in OnChiaveSelezionataChanged, AggiornaVociDisponibili e AggiornaVociDisponibiliDaFornitore).
    private List<string>? _altreVociMemo;
    public List<string> AltreVociPerUnione => _altreVociMemo ??= string.IsNullOrWhiteSpace(ChiaveSelezionata)
        ? new()
        : VociCatalogoAttuali().Where(v => !v.Equals(ChiaveSelezionata, StringComparison.OrdinalIgnoreCase)).ToList();

    private List<string> VociCatalogoAttuali() =>
        _vociCatalogoAttuali ??= (_fornitoreVoci?.Invoke() ?? Array.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v)).ToList();

    [ObservableProperty] private string? vocePerUnioneSelezionata;

    /// <summary>Testo del pulsante-picker "Unisci in" (usato oltre ListaOltreSogliaConverter.Soglia voci, dove la
    /// ComboBox pura non offre ricerca): voce scelta oppure il placeholder localizzato.</summary>
    public string VocePerUnioneTesto => string.IsNullOrWhiteSpace(VocePerUnioneSelezionata)
        ? LocalizationService.Instance.Traduci("MasterUnisciInPlaceholder")
        : VocePerUnioneSelezionata!;
    partial void OnVocePerUnioneSelezionataChanged(string? value) => OnPropertyChanged(nameof(VocePerUnioneTesto));

    /// <summary>Picker con ricerca per "Unisci in" quando le altre voci superano la soglia (regola app: ogni
    /// lista oltre 10 elementi deve offrire ricerca).</summary>
    [RelayCommand]
    private async Task ScegliVocePerUnione()
    {
        var voci = AltreVociPerUnione.Select(v => new VoceSelezionabile { Valore = v, TestoPrincipale = v, TestoRicerca = v }).ToList();
        var scelta = await SelezionaDialogService.Scegli(LocalizationService.Instance.Traduci("MasterUnisciInPlaceholder"), voci);
        if (scelta?.Valore is string s) VocePerUnioneSelezionata = s;
    }
    [ObservableProperty] private string nuovoNomeRinomina = "";
    [ObservableProperty] private string vociDivisioneTesto = "";

    /// <summary>Da richiamare insieme a RicalcolaCoppieSospette ogni volta che il catalogo viene ricaricato:
    /// aggiorna l'elenco per il picker "Unisci in..." senza un secondo repo.Load() (le voci sono già in memoria
    /// nella pagina chiamante).</summary>
    public void AggiornaVociDisponibili(IEnumerable<string> vociCatalogoAttuali)
    {
        _fornitoreVoci = null;
        _vociCatalogoAttuali = vociCatalogoAttuali.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        _altreVociMemo = null;
        OnPropertyChanged(nameof(AltreVociPerUnione));
    }

    /// <summary>Variante lazy di AggiornaVociDisponibili per i cataloghi paginati: il fornitore (lettura delle sole chiavi
    /// dal read-model locale) viene interrogato alla prima necessita' del picker, cioe' con una riga selezionata. Ogni
    /// ricaricamento invalida l'elenco gia' letto, quindi resta allineato all'ultimo Ricarica()/DatiRicaricati.</summary>
    public void AggiornaVociDisponibiliDaFornitore(Func<IEnumerable<string>> fornitore)
    {
        _fornitoreVoci = fornitore;
        _vociCatalogoAttuali = null;
        _altreVociMemo = null;
        OnPropertyChanged(nameof(AltreVociPerUnione));
    }

    [RelayCommand]
    private async Task UnisciSelezionata() => await Unisci(ChiaveSelezionata, VocePerUnioneSelezionata);

    [RelayCommand]
    private async Task RinominaSelezionata() => await Rinomina(ChiaveSelezionata, NuovoNomeRinomina);

    [RelayCommand]
    private async Task DividiSelezionata() => await Dividi(ChiaveSelezionata,
        VociDivisioneTesto.Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    public MasterUnioneViewModel(AppState state, MasterDataRepository<T> repo, Func<T, string> keyOf, string? catalogo = null, Func<bool>? inModifica = null)
    {
        _inModifica = inModifica;
        _state = state;
        _repo = repo;
        _keyOf = keyOf;
        _catalogo = catalogo ?? typeof(T).Name;
    }

    /// <summary>Da richiamare dalla base class ogni volta che il catalogo viene ricaricato — stesso schema di
    /// WuStatusViewModel.Ricarica (CoppieSospette calcolate solo per un Master, mai fuzzy).</summary>
    public void RicalcolaCoppieSospette(IEnumerable<T> catalogoAttuale)
    {
        if (!EMaster) { CoppieSospette = new(); OnPropertyChanged(nameof(CoppieSospette)); OnPropertyChanged(nameof(HaCoppieSospette)); OnPropertyChanged(nameof(MostraCoppieSospette)); return; }
        var voci = catalogoAttuale.Select(_keyOf).Where(k => !string.IsNullOrWhiteSpace(k));
        CoppieSospette = UdtLogImportService.CoppieDoppioni(voci, UdtLogImportService.NormalizzaPerConfronto,
            (a, b) => new CoppiaSospettaMaster(a, b));
        OnPropertyChanged(nameof(CoppieSospette));
        OnPropertyChanged(nameof(HaCoppieSospette)); OnPropertyChanged(nameof(MostraCoppieSospette));
    }

    /// <summary>Conteggio "a secco" per l'anteprima obbligatoria (spec punto 4), nessuna mutazione.</summary>
    public MasterRiferimentiService.RisultatoImpatto Anteprima(string valore) =>
        MasterRiferimentiService.ContaImpatto(_catalogo, valore, _state.TestCases.GetAll(), _state.NonConformita.GetAll(), _state.ChangeRequests.PeekAllReadOnly());

    /// <summary>Aggiunge al messaggio di conferma la riga sulle collisioni della matrice Audit (Piano 6.3.1
    /// §1, review 2026-09-24: "l'anteprima deve mostrare quante collisioni verranno consolidate"), solo se
    /// ce n'è almeno una — nessun rumore per l'unione/rinomina/divisione più comune che non tocca affatto
    /// ValutazioneRequisitoEcuVeicolo. Nessuna mutazione: stesso principio "a secco" di Anteprima sopra.</summary>
    private string AggiungiRigaCollisioniValutazioni(string messaggio, string vecchio, string nuovo)
    {
        var collisioni = MasterRiferimentiService.ContaCollisioniValutazioni(_state.Master, _catalogo, vecchio, nuovo);
        if (collisioni <= 0) return messaggio;
        return messaggio + " " + string.Format(LocalizationService.Instance.Traduci("MasterUnioneCollisioniValutazioniFmt"), collisioni);
    }

    public async Task<bool> Unisci(string? scartata, string? corretta)
    {
        if (IsBusy) return false; // nessuna esecuzione doppia concorrente (review 2026-09-23, MEDIUM).
        if (BloccatoDaModifica()) return false;
        if (!EMaster || string.IsNullOrWhiteSpace(scartata) || string.IsNullOrWhiteSpace(corretta)) return false;
        if (scartata.Equals(corretta, StringComparison.OrdinalIgnoreCase)) return false;

        var loc = LocalizationService.Instance;
        if (_catalogo == "Requisito")
        {
            var elenco = _repo.Load();
            var rs = elenco.OfType<Requisito>().FirstOrDefault(r => r.Id.Equals(scartata, StringComparison.OrdinalIgnoreCase));
            var rc = elenco.OfType<Requisito>().FirstOrDefault(r => r.Id.Equals(corretta, StringComparison.OrdinalIgnoreCase));
            if (rs != null && rc != null && MasterUnioneService.RifiutoUnioneTipiDiversi(_state, rs, rc) is { } rifiuto)
            {
                ErroreOperazione = rifiuto;
                return false;
            }
        }
        var impatto = Anteprima(scartata);
        var messaggio = string.Format(loc.Traduci("MasterUnioneConfermaBodyFmt"),
            scartata, corretta, impatto.TestAggiornati, impatto.NonConformitaAggiornate, impatto.ChangeRequestAggiornate);
        messaggio = AggiungiRigaCollisioniValutazioni(messaggio, scartata, corretta);
        if (!await ConfirmService.Chiedi(loc.Traduci("MasterUnioneConfermaTitolo"), messaggio, loc.Traduci("MasterUnisciBtn"), loc.Traduci("Annulla")))
            return false;

        return await EseguiOperazioneAsync(progresso =>
        {
            var avvisi = new List<string>();
            MasterUnioneService.Unisci(_state, _repo, _catalogo, scartata, corretta, _keyOf, avvisi, progresso);
            return avvisi;
        });
    }

    public async Task<bool> Rinomina(string? vecchio, string? nuovo)
    {
        if (IsBusy) return false;
        if (BloccatoDaModifica()) return false;
        if (!EMaster || string.IsNullOrWhiteSpace(vecchio) || string.IsNullOrWhiteSpace(nuovo)) return false;
        nuovo = nuovo.Trim(); // niente spazi iniziali/finali nella chiave salvata ("Componente1 " != "Componente1").
        // Solo un nome identico (maiuscole incluse) e' un no-op: "componente1" -> "Componente1" cambia la grafia ed e' lecito.
        if (vecchio.Equals(nuovo, StringComparison.Ordinal)) return false;

        var loc = LocalizationService.Instance;
        // Il duplicato esclude la voce stessa (stessa chiave a meno delle maiuscole = rinomina solo di grafia).
        if (!vecchio.Equals(nuovo, StringComparison.OrdinalIgnoreCase)
            && _repo.Load().Any(e => _keyOf(e).Equals(nuovo, StringComparison.OrdinalIgnoreCase)))
        {
            ErroreOperazione = loc.Traduci("MasterRinominaEsistenteProponiUnisci");
            return false;
        }

        var impatto = Anteprima(vecchio);
        var messaggio = string.Format(loc.Traduci("MasterRinominaConfermaBodyFmt"),
            vecchio, nuovo, impatto.TestAggiornati, impatto.NonConformitaAggiornate, impatto.ChangeRequestAggiornate);
        messaggio = AggiungiRigaCollisioniValutazioni(messaggio, vecchio, nuovo);
        if (!await ConfirmService.Chiedi(loc.Traduci("MasterRinominaConfermaTitolo"), messaggio, loc.Traduci("MasterRinominaBtn"), loc.Traduci("Annulla")))
            return false;

        return await EseguiOperazioneAsync(progresso =>
        {
            var avvisi = new List<string>();
            MasterUnioneService.Rinomina(_state, _repo, _catalogo, vecchio, nuovo, _keyOf, avvisi, progresso);
            return avvisi;
        });
    }

    public async Task<bool> Dividi(string? originale, IReadOnlyList<string>? nuovi)
    {
        if (IsBusy) return false;
        if (BloccatoDaModifica()) return false;
        if (!EMaster || string.IsNullOrWhiteSpace(originale) || nuovi == null) return false;
        var loc = LocalizationService.Instance;
        var distinte = nuovi.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
            .Where(n => !n.Equals(originale, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Dividi non rimuove mai l'originale (decisione 2026-09-30): serve almeno UNA nuova voce, diversa da lui.
        if (distinte.Count < 1) { ErroreOperazione = loc.Traduci("MasterDividiServeAlmenoUna"); return false; }

        var impatto = Anteprima(originale);
        var messaggio = string.Format(loc.Traduci("MasterDividiConfermaBodyFmt"),
            originale, string.Join(", ", distinte), impatto.TestAggiornati, impatto.NonConformitaAggiornate, impatto.ChangeRequestAggiornate);
        // Nessuna riga sulle collisioni delle valutazioni: i campi singoli (Componente/Veicolo/RequisitoId) restano
        // sull'originale, quindi la divisione non può far collassare due celle.
        if (!await ConfirmService.Chiedi(loc.Traduci("MasterDividiConfermaTitolo"), messaggio, loc.Traduci("MasterDividiBtn"), loc.Traduci("Annulla")))
            return false;

        return await EseguiOperazioneAsync(progresso =>
        {
            var avvisi = new List<string>();
            MasterUnioneService.Dividi(_state, _repo, _catalogo, originale, distinte, _keyOf, avvisi, progresso);
            return avvisi;
        });
    }

    [RelayCommand]
    public async Task UnisciCoppiaSospetta(CoppiaSospettaMaster? coppia)
    {
        if (coppia != null) await Unisci(coppia.A, coppia.B);
    }

    /// <summary>Esegue il motore transazionale su un thread separato (Task.Run): a scala l'operazione può
    /// durare minuti (misurato: ~150-160s su 100k Test/~1000 coinvolti) e prima girava sincrona sul thread
    /// chiamante, bloccando la UI senza alcun feedback (review 2026-09-23, MEDIUM). IsBusy=true per l'intera
    /// durata blocca anche i tentativi di riavvio concorrenti (controllato a monte in Unisci/Rinomina/Dividi,
    /// ma anche qui per difesa in profondità sulle chiamate dirette). <see cref="Progress{T}"/> marshalla
    /// l'aggiornamento di ProgressoTesto sul thread che ha catturato il SynchronizationContext (la UI, dato che
    /// l'oggetto viene creato qui prima del Task.Run) — nessun accesso a proprietà bindate da un thread diverso
    /// da quello UI.</summary>
    private async Task<bool> EseguiOperazioneAsync(Func<IProgress<(int Fatti, int Totale)>, List<string>> azione)
    {
        if (IsBusy) return false;
        IsBusy = true;
        ProgressoTesto = null;
        var progresso = new Progress<(int Fatti, int Totale)>(p =>
            ProgressoTesto = string.Format(LocalizationService.Instance.Traduci("MasterUnioneAvanzamentoFmt"), p.Fatti, p.Totale));
        try
        {
            ErroreOperazione = null;
            var avvisi = await Task.Run(() => azione(progresso));
            MessaggioEsito = avvisi.Count > 0 ? string.Join(" ", avvisi) : null;
            OperazioneCompletata?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            ErroreOperazione = string.Format(LocalizationService.Instance.Traduci("MasterUnioneErroreFmt"), ex.Message);
            _state.TestCases.RefreshIndex();
            return false;
        }
        finally
        {
            IsBusy = false;
            ProgressoTesto = null;
        }
    }
}
