using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

public sealed record GlobalSearchResult(string Icona, string Tipo, string Titolo, string Sottotitolo,
    string Destinazione, string? ElementoId = null, Guid? ElementoUid = null);

/// <summary>Ricerca globale con debounce. I Test usano FTS5 sul read-model SQLite locale quando disponibile;
/// anche Requisiti/Funzionalita/componente usano il read-model master locale dopo il primo warm-up. I JSON condivisi
/// restano autorevoli e alimentano l'indice in background: la digitazione non deve fare scansioni O(N) su
/// OneDrive a ogni carattere. Veicoli/Strumenti, cataloghi normalmente molto piu piccoli, restano in memoria.</summary>
public partial class GlobalSearchViewModel : ViewModelBase
{
    private readonly AppState _state;
    private readonly Action<GlobalSearchResult> _apri;
    private readonly int _debounceMs;
    private System.Timers.Timer? _debounceTimer;
    private readonly MasterDataLocalIndexCoordinator? _masterIndex;
    private readonly object _masterIndexRefreshGate = new();
    private Task? _masterIndexRefreshTask;
    private volatile bool _requisitiIndexReady;
    private volatile bool _funzionalitaIndexReady;
    private volatile bool _ecuIndexReady;

    /// <summary>Vero quando l'indice master va riconciliato alla prossima query: all'apertura dell'overlay
    /// (<see cref="PreparaApertura"/>) e dopo un ricaricamento dei dati (<see cref="AppState.DatiRicaricati"/>).
    /// Prima ogni query di ogni digitazione riavviava il refresh dei tre cataloghi, anche senza alcun cambio.</summary>
    private volatile bool _indiceDaRiconciliare;
    private int _refreshIndiceAvviati;

    /// <summary>Quanti refresh dell'indice master sono stati avviati (test e banco).</summary>
    internal int RefreshIndiceAvviati => Volatile.Read(ref _refreshIndiceAvviati);

    [ObservableProperty] private string query = "";
    public ObservableCollection<GlobalSearchResult> Risultati { get; } = new();
    public bool NessunRisultato => Query.Trim().Length >= 2 && Risultati.Count == 0;

    public GlobalSearchViewModel(AppState state, Action<GlobalSearchResult> apri, int debounceMs = 250)
    {
        _state = state;
        _apri = apri;
        _debounceMs = debounceMs;
        if (_state.CartellaValida)
        {
            try
            {
                _masterIndex = new MasterDataLocalIndexCoordinator(_state.Paths);
                AvviaRefreshMasterIndex();
            }
            catch (Exception ex)
            {
                // L'indice e' solo una cache: un problema locale non deve mai rendere indisponibile la ricerca.
                CrashLogService.Registra("Ricerca globale: inizializzazione indice master locale", ex);
            }
        }
        _state.DatiRicaricati += OnDatiRicaricati;
    }

    /// <summary>Apertura dell'overlay: la prossima query riconcilia l'indice master una sola volta.</summary>
    public void PreparaApertura() => _indiceDaRiconciliare = true;

    private void OnDatiRicaricati() => _indiceDaRiconciliare = true;

    partial void OnQueryChanged(string value)
    {
        if (_debounceMs <= 0) { Cerca(); return; }
        _debounceTimer?.Stop();
        _debounceTimer?.Dispose();
        _debounceTimer = new System.Timers.Timer(_debounceMs) { AutoReset = false };
        _debounceTimer.Elapsed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Cerca);
        _debounceTimer.Start();
    }

    public void Reset()
    {
        _debounceTimer?.Stop();
        Query = "";
        _debounceTimer?.Stop();
        Interlocked.Increment(ref _generazione); // scarta eventuali ricerche ancora in volo
        AnnullaRicercaInCorso();
        RicercaInCorso = false;
        Risultati.Clear();
        OnPropertyChanged(nameof(NessunRisultato));
    }

    /// <summary>
    /// Una sola riconciliazione per volta. Ogni catalogo mantiene readiness indipendente: se, per esempio, componente
    /// diventa illeggibile, l'indice componente viene invalidato senza disabilitare Requisiti/Funzionalita gia verificati.
    /// SQLite resta sempre una cache locale; un refresh fallito riporta esplicitamente a false la readiness del
    /// catalogo coinvolto, impedendo che una vecchia fotografia continui a essere trattata come corrente.
    /// </summary>
    /// <returns>Vero se il refresh e' stato avviato o non serve (indice assente); falso se ne e' gia' in corso uno.</returns>
    private bool AvviaRefreshMasterIndex()
    {
        if (_masterIndex == null) return true;
        lock (_masterIndexRefreshGate)
        {
            if (_masterIndexRefreshTask is { IsCompleted: false }) return false;
            Interlocked.Increment(ref _refreshIndiceAvviati);
            _masterIndexRefreshTask = Task.Run(() =>
            {
                AggiornaCatalogoIndice("Requisiti", _masterIndex.RefreshRequisiti,
                    ready => _requisitiIndexReady = ready);
                AggiornaCatalogoIndice("Funzionalita", _masterIndex.RefreshFunzionalita,
                    ready => _funzionalitaIndexReady = ready);
                AggiornaCatalogoIndice("Componente", _masterIndex.RefreshEcu,
                    ready => _ecuIndexReady = ready);
            });
            return true;
        }
    }

    private static void AggiornaCatalogoIndice(
        string nomeCatalogo,
        Func<MasterCatalogIndexRefresh> refresh,
        Action<bool> setReady)
    {
        try
        {
            refresh();
            setReady(true);
        }
        catch (Exception ex)
        {
            // Fail-closed per catalogo: una cache precedentemente valida non puo' restare marcata pronta se il
            // JSON autorevole non e' piu verificabile. Gli altri cataloghi vengono comunque riconciliati.
            setReady(false);
            CrashLogService.Registra($"Ricerca globale: refresh indice master locale {nomeCatalogo}", ex);
        }
    }

    /// <summary>Attende il refresh gia' in corso senza avviarne uno nuovo (test del numero di riconciliazioni).</summary>
    internal Task AttendiRefreshInCorsoAsync()
    {
        Task? task;
        lock (_masterIndexRefreshGate) task = _masterIndexRefreshTask;
        return task ?? Task.CompletedTask;
    }

    /// <summary>Usato anche dai test di integrita per attendere deterministicamente la riconciliazione asincrona.</summary>
    internal async Task AttendiRefreshIndiceMasterAsync()
    {
        AvviaRefreshMasterIndex();
        Task? task;
        lock (_masterIndexRefreshGate) task = _masterIndexRefreshTask;
        if (task != null) await task.ConfigureAwait(false);
    }

    /// <summary>Raccoglitore del calcolo in corso: ogni ricerca gira interamente su un solo thread.</summary>
    [ThreadStatic] private static List<GlobalSearchResult>? _raccolta;
    private static void Aggiungi(GlobalSearchResult risultato) => _raccolta!.Add(risultato);
    private int _generazione;

    /// <summary>Solo test e misure A/B: lettura storica (copia di NC e Segnalazioni, verifica per Test per ogni Segnalazione).
    /// Stessi risultati, costo diverso: il confronto prima/dopo avviene nello stesso processo e sullo stesso workspace.</summary>
    internal bool PercorsoStoricoPerConfronto { get; set; }

    /// <summary>Token della ricerca in background ancora in corso (solo thread UI): la digitazione successiva lo annulla.</summary>
    private CancellationTokenSource? _ricercaCts;

    [ObservableProperty] private bool ricercaInCorso;

    /// <summary>
    /// Il calcolo (FTS, repository, cataloghi) gira fuori dal thread UI; solo l'ultima ricerca digitata
    /// aggiorna i risultati, quelle superate vengono scartate. Con debounce 0 (test) resta sincrona.
    /// </summary>
    private void Cerca()
    {
        var q = Query.Trim();
        var generazione = Interlocked.Increment(ref _generazione);
        AnnullaRicercaInCorso();
        if (q.Length < 2 || !_state.CartellaValida)
        {
            RicercaInCorso = false;
            Applica(Array.Empty<GlobalSearchResult>());
            return;
        }
        if (_debounceMs <= 0) { Applica(CalcolaRisultati(q, CancellationToken.None)); return; }

        RicercaInCorso = true;
        var token = (_ricercaCts = new CancellationTokenSource()).Token;
        Task.Run(() => CalcolaRisultati(q, token)).ContinueWith(t => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (generazione != Volatile.Read(ref _generazione)) return; // superata da una digitazione successiva
            RicercaInCorso = false;
            if (t.IsCompletedSuccessfully) Applica(t.Result);
            else CrashLogService.Registra("Ricerca globale in background", t.Exception?.GetBaseException() ?? new InvalidOperationException("Ricerca annullata"));
        }), TaskScheduler.Default);
    }

    private void Applica(IReadOnlyList<GlobalSearchResult> risultati)
    {
        Risultati.Clear();
        foreach (var r in risultati) Risultati.Add(r);
        OnPropertyChanged(nameof(NessunRisultato));
    }

    /// <summary>Calcolo dei risultati di una query, per i test: con un token gia' annullato non legge nessun dominio.</summary>
    internal IReadOnlyList<GlobalSearchResult> CalcolaRisultatiPerTest(string q, CancellationToken token) => CalcolaRisultati(q, token);

    private List<GlobalSearchResult> CalcolaRisultati(string q, CancellationToken token)
    {
        var precedente = _raccolta;
        _raccolta = new List<GlobalSearchResult>();
        try
        {
            // Una sola lettura logica per query: Veicoli/Strumenti e i fallback componente/Requisiti/Funzionalita condividono
            // un solo refresh Phase D (prima 4-5 per query). MasterLetturaScope e' [ThreadStatic] e questo metodo e'
            // interamente sincrono: Apri() e Dispose() avvengono sullo stesso thread, sia nel percorso in background
            // (Task.Run) sia in quello sincrono dei test con debounce 0. Nessuna scrittura avviene qui dentro.
            using var letturaMaster = MasterLetturaScope.Apri();
            CalcolaDomini(q, token);
            return _raccolta;
        }
        finally { _raccolta = precedente; }
    }

    /// <summary>Annulla la ricerca in background ancora in corso: ferma il calcolo al dominio successivo. Il suo risultato
    /// sarebbe comunque scartato dalla generazione; l'annullamento evita il lavoro sprecato. Solo sul thread UI.</summary>
    private void AnnullaRicercaInCorso()
    {
        _ricercaCts?.Cancel();
        _ricercaCts = null;
    }

    /// <remarks>Il token e' cooperativo: quando e' annullato (ricerca superata) i domini successivi non si leggono piu' e il
    /// risultato parziale viene scartato dal chiamante. Nessuna eccezione: i domini non la registrerebbero come guasto.</remarks>
    private void CalcolaDomini(string q, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;

        // Indice caldo rispetto ai salvataggi master: riconciliato al piu' una volta per apertura dell'overlay o per
        // ricaricamento dei dati, non a ogni digitazione. Se un refresh e' ancora in corso la richiesta resta pendente
        // e la query successiva la riprova, cosi' un refresh gia' partito prima della modifica non lascia l'indice vecchio.
        if (_indiceDaRiconciliare)
        {
            _indiceDaRiconciliare = false;
            if (!AvviaRefreshMasterIndex()) _indiceDaRiconciliare = true;
        }

        // Ogni dominio e' isolato: un file Test/CR/NC corrotto o, per uno Standard, il master Veicoli necessario
        // alla visibilita non deve impedire di cercare negli altri domini sani. Per i Test l'errore di scope e'
        // fail-closed: se non possiamo dimostrare che una riga appartiene alle gamme assegnate, non la mostriamo.
        CercaTest(q);
        if (token.IsCancellationRequested) return;
        CercaChangeRequest(q);
        if (token.IsCancellationRequested) return;
        CercaNonConformita(q);
        if (token.IsCancellationRequested) return;
        CercaSegnalazioni(q);
        if (token.IsCancellationRequested) return;

        if (_state.EMaster)
        {
            CercaEcu(q);
            CercaRequisiti(q);
            CercaFunzionalita(q);
            if (token.IsCancellationRequested) return;

            // Cataloghi piu piccoli: anche qui ogni lettura e' isolata. Un singolo JSON guasto non deve
            // interrompere la ricerca globale e nascondere risultati sani gia raccolti negli altri domini.
            CercaFallbackMaster("Veicoli",
                () => _state.Master.Veicoli.Load().Where(x => !x.Archiviata && Match(q, x.Nome, x.GammaNome)).Take(5),
                x => new("🚛", LocalizationService.Instance.Traduci("VeicoliTitolo"), x.Nome, x.GammaNome, "impostazioni"));
            CercaFallbackMaster("Strumenti",
                () => _state.Master.Strumenti.Load().Where(x => !x.Archiviata && Match(q, x.Nome)).Take(5),
                x => new("🛠️", LocalizationService.Instance.Traduci("StrumentiTitolo"), x.Nome,
                    LocalizationService.Instance.Traduci("NavImpostazioni"), "impostazioni"));
        }
    }

    private void CercaTest(string q)
    {
        try
        {
            // FTS locale: evita O(N) sui Test ad ogni ricerca. Null = FTS/DB non disponibile, quindi fallback
            // conservativo all'algoritmo storico. Lista vuota = query valida senza Test corrispondenti.
            var indexedHits = LocalTestSearchService.Search(_state.TestCases.LocalIndexDatabasePath, q, 32);
            if (indexedHits != null)
            {
                IEnumerable<LocalTestSearchHit> hits = indexedHits;
                // Stesso perimetro di AppState.FiltraTestCaseVisibiliPerOperatore (RuoloEffettivo, non il Ruolo grezzo),
                // letto una volta per ricerca.
                var veicoliAmmessi = _state.VeicoliAmmessiOperatore(ancheRuoloGrezzo: true);
                if (veicoliAmmessi != null) hits = hits.Where(h => h.Veicoli.Any(veicoliAmmessi.Contains));

                foreach (var hit in hits.Take(12))
                {
                    var descrizione = !string.IsNullOrWhiteSpace(hit.DescrizioneBreve) ? hit.DescrizioneBreve : hit.Descrizione;
                    Aggiungi(new("🧪", LocalizationService.Instance.Traduci("NavTest"), hit.Id,
                        $"{hit.EcuTesto} · {hit.VeicoliTesto} — {descrizione}", "test", hit.Id));
                }
                return;
            }

            foreach (var tc in _state.FiltraTestCaseVisibiliPerOperatore(_state.TestCases.GetAll())
                         .Where(t => Match(q, t.Id, t.Descrizione, t.Componente) || t.VeicoliEffettivi.Any(v => Match(q, v))).Take(12))
                Aggiungi(new("🧪", LocalizationService.Instance.Traduci("NavTest"), tc.Id,
                    $"{tc.Componente} · {string.Join(", ", tc.VeicoliEffettivi)} — {tc.Descrizione}", "test", tc.Id));
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Ricerca globale: dominio Test non disponibile", ex);
        }
    }

    private void CercaChangeRequest(string q)
    {
        try
        {
            // Lettura senza copia dal repository (PeekAllReadOnly, come per le NC): GetAll clona ogni CR e resta solo per il confronto storico.
            // Ricerca per testo nel repository: con la proiezione di ricerca completa legge solo l'indice (altrimenti ricade sul
            // JSON, con lo stesso risultato). GetAll resta per il confronto storico.
            var risultati = PercorsoStoricoPerConfronto
                ? _state.ChangeRequests.GetAll().Where(c => Match(q, c.Id, c.Titolo, c.DescrizioneBreve, c.Descrizione, c.Responsabile)
                      || c.VeicoliImpattati.Any(v => Match(q, v)) || c.EcuImpattate.Any(e => Match(q, e))).Take(8).ToList()
                : _state.ChangeRequests.CercaTesto(q, 8).ToList();
            foreach (var cr in risultati)
                Aggiungi(new("📝", "Change Request", cr.Id, $"{cr.Stato} — {cr.Titolo}", "change-request", cr.Id));
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Ricerca globale: dominio Change Request non disponibile", ex);
        }
    }

    private void CercaNonConformita(string q)
    {
        try
        {
            // Lettura senza clone: la riga mostrata e' una stringa e nessuna NC viene esposta (GetAll clonerebbe l'intero archivio).
            var nonConformita = PercorsoStoricoPerConfronto ? _state.NonConformita.GetAll() : _state.NonConformita.PeekAllReadOnly();
            foreach (var nc in nonConformita.Where(n => Match(q, n.Id, n.TestCaseId, n.Descrizione, n.Owner)).Take(8))
                Aggiungi(new("⚠️", LocalizationService.Instance.Traduci("NavAnomalie"), nc.Id,
                    $"{nc.TestCaseId} — {nc.Descrizione}", "anomalie"));
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Ricerca globale: dominio Non Conformita non disponibile", ex);
        }
    }

    private void CercaSegnalazioni(string q)
    {
        try
        {
            // Lettura senza clone (la riga e' una stringa). Il perimetro dell'operatore si legge una volta per ricerca,
            // non una volta per segnalazione aperta (con Standard ogni verifica rileggeva il catalogo Veicoli).
            var veicoliAmmessi = _state.VeicoliAmmessiOperatore(ancheRuoloGrezzo: true);
            var segnalazioni = PercorsoStoricoPerConfronto ? _state.Segnalazioni.GetAll() : _state.Segnalazioni.PeekAllReadOnly();
            var aperti = segnalazioni.Where(s => s.Stato is StatoSegnalazione.DaClassificare or StatoSegnalazione.Esclusa);
            var aggiunti = 0;
            foreach (var segnalazione in aperti)
            {
                var test = segnalazione.TestUid is { } testUid
                    ? _state.TestCases.GetByUid(testUid)
                    : _state.TestCases.GetById(segnalazione.TestId);
                if (test == null || test.Eliminato) continue;
                var visibile = PercorsoStoricoPerConfronto
                    ? _state.TestCaseVisibilePerOperatore(test)
                    : AppState.TestCaseVisibileConVeicoli(test, veicoliAmmessi);
                if (!visibile) continue;

                var esecuzione = test.Esecuzioni.FirstOrDefault(e => e.Iterazione == segnalazione.EsecuzioneIterazione);
                var condizione = esecuzione?.CondizioniEffettive.FirstOrDefault(c => c.Uid == segnalazione.CondizioneUid);
                var componente = condizione?.Componente.ToString() ?? "";
                var matches = Match(q, test.Id, segnalazione.TestId, segnalazione.Uid.ToString("D"),
                    segnalazione.Uid.ToString("N"), segnalazione.Stato.ToString(),
                    segnalazione.MotivazioneEsclusione, condizione?.Funzionalita,
                    condizione?.DescrizioneProblema, condizione?.Nota, componente);
                if (!matches) continue;

                var dettaglio = condizione == null
                    ? "Segnalazione " + segnalazione.Stato
                    : string.Join(" · ", new[] { condizione.Funzionalita, componente, condizione.DescrizioneProblema, condizione.Nota }
                        .Where(v => !string.IsNullOrWhiteSpace(v)));
                Aggiungi(new("🏷️", "Segnalazione", test.Id,
                    $"{segnalazione.Stato} — {dettaglio}", "segnalazione", test.Id, segnalazione.Uid));
                if (++aggiunti >= 8) break;
            }
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Ricerca globale: dominio Segnalazioni non disponibile", ex);
        }
    }

    private void CercaEcu(string q)
    {
        if (CercaIndiceMaster<Componente>(MasterDataLocalIndexCoordinator.EcuCatalog, q, _ecuIndexReady,
                () => _ecuIndexReady = false,
                x => new("🔌", "Componente", x.Nome, LocalizationService.Instance.Traduci("NavImpostazioni"), "impostazioni")))
            return;

        CercaFallbackMaster("Componente",
            () => _state.Master.Componente.Load().Where(x => !x.Archiviata && Match(q, x.Nome)).Take(5),
            x => new("🔌", "Componente", x.Nome, LocalizationService.Instance.Traduci("NavImpostazioni"), "impostazioni"));
    }

    private void CercaRequisiti(string q)
    {
        if (CercaIndiceMaster<Requisito>(MasterDataLocalIndexCoordinator.RequisitiCatalog, q, _requisitiIndexReady,
                () => _requisitiIndexReady = false,
                x => new("📋", LocalizationService.Instance.Traduci("RequisitiTitolo"), x.Id, x.Testo, "impostazioni")))
            return;

        CercaFallbackMaster("Requisiti",
            () => _state.Master.Requisiti.Load().Where(x => !x.Archiviata && Match(q, x.Id, x.Testo)).Take(5),
            x => new("📋", LocalizationService.Instance.Traduci("RequisitiTitolo"), x.Id, x.Testo, "impostazioni"));
    }

    private void CercaFunzionalita(string q)
    {
        if (CercaIndiceMaster<Funzionalita>(MasterDataLocalIndexCoordinator.FunzionalitaCatalog, q, _funzionalitaIndexReady,
                () => _funzionalitaIndexReady = false,
                x => new("⚙️", LocalizationService.Instance.Traduci("FunzTitolo"), x.Nome, x.CategorieTesto, "impostazioni")))
            return;

        CercaFallbackMaster("Funzionalita",
            () => _state.Master.Funzionalita.Load().Where(x => !x.Archiviata && Match(q, x.Nome, x.CategorieTesto)).Take(5),
            x => new("⚙️", LocalizationService.Instance.Traduci("FunzTitolo"), x.Nome, x.CategorieTesto, "impostazioni"));
    }

    private bool CercaIndiceMaster<T>(
        string catalog,
        string q,
        bool ready,
        Action invalida,
        Func<T, GlobalSearchResult> proietta)
        where T : MasterEntity
    {
        if (!ready || _masterIndex == null) return false;
        try
        {
            if (!_masterIndex.IndexRecovery.TryRead($"ricerca globale master {catalog}",
                    () => _masterIndex.Store.Query<T>(catalog, q, false, 0, 5), out var hits))
            {
                invalida();
                return false;
            }
            foreach (var hit in hits)
                Aggiungi(proietta(hit.Item));
            return true;
        }
        catch (Exception ex)
        {
            // Anche una corruzione/busy del DB locale invalida solo questo catalogo. Il fallback autorevole
            // viene tentato subito e una query futura potra' riabilitare l'indice dopo un refresh riuscito.
            invalida();
            CrashLogService.Registra($"Ricerca globale: query indice master locale {catalog}", ex);
            return false;
        }
    }

    private void CercaFallbackMaster<T>(
        string nomeCatalogo,
        Func<IEnumerable<T>> carica,
        Func<T, GlobalSearchResult> proietta)
    {
        try
        {
            foreach (var item in carica()) Aggiungi(proietta(item));
        }
        catch (Exception ex)
        {
            // Il repository master e' fail-closed: un JSON corrotto non diventa una lista vuota. Qui isoliamo
            // l'errore perche' la ricerca globale deve continuare a mostrare gli altri domini sani.
            CrashLogService.Registra($"Ricerca globale: fallback master {nomeCatalogo}", ex);
        }
    }

    private static bool Match(string q, params string?[] valori) =>
        valori.Any(v => v?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);

    [RelayCommand]
    private void Apri(GlobalSearchResult? risultato) { if (risultato != null) _apri(risultato); }

    public void ApriPrimo() { if (Risultati.FirstOrDefault() is { } primo) _apri(primo); }
}
