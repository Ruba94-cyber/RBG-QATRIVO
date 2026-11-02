using CommunityToolkit.Mvvm.ComponentModel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Aggiornamento della Home a giri. Un giro cattura sul thread UI tutto cio' che serve (sezioni pendenti e visibili,
/// operatore, pagine, filtri, indice), legge su un thread di pool dentro UNA lettura logica dei master
/// (<see cref="MasterLetturaScope"/>) e applica i risultati sul thread UI. Un giro piu' recente supera quelli in corso:
/// le loro applicazioni vengono scartate (contatore di generazione). Senza modalita' in background (default: test e
/// percorsi esistenti) le stesse funzioni girano in linea sul thread chiamante, con risultati identici.
/// Restano sul thread UI il percorso non indicizzato (degradato), i cataloghi di <c>AggiornaFiltriInMemoria</c> e la
/// paginazione Da fare/Fatti.
/// </summary>
public partial class HomeViewModel
{
    private bool _aggiornaInBackground;
    private int _generazioneGiro;
    private string? _operatoreDati;
    private readonly List<Task> _giri = new();

    /// <summary>Vero mentre un giro in background non ha ancora applicato i suoi dati: la vista mostra un indicatore discreto.</summary>
    [ObservableProperty] private bool caricamentoInCorso;

    /// <summary>Giri le cui letture sono state scartate perche' superate da un giro piu' recente.</summary>
    internal int GiriScartati { get; private set; }

    /// <summary>Ultima eccezione di un giro in background (registrata anche nel log dei crash): per i test.</summary>
    internal Exception? UltimaEccezioneAggiornamento { get; private set; }

    /// <summary>Solo test: eseguito all'inizio di ogni lettura in background (puo' bloccare, per simulare un giro lento).</summary>
    internal Action? GateLetturaPerTest { get; set; }

    /// <summary>Solo test: eseguito all'inizio di ogni lettura in background (puo' lanciare, per simulare un guasto).</summary>
    internal Action? GuastoLetturaPerTest { get; set; }
    internal Action<int, int>? DistribuzioneConformitaPreparataPerTest { get; set; }

    internal void ImpostaAggiornamentoInBackgroundPerTest(bool valore) => _aggiornaInBackground = valore;

    /// <summary>Aggiorna le sezioni visibili (Riepilogo, Avvisi, KPI, Da fare/Fatti, grafici) e i Recenti. Il Task si completa
    /// quando i dati sono applicati o il giro e' stato superato da uno piu' recente.</summary>
    public Task AggiornaAsync()
    {
        _testataDaCalcolare = _coperturaDaCalcolare = _listeDaCalcolare = _filtriDaCalcolare = true;
        _dashboardDaAggiornare = true;
        _versioneTestata++;
        return AvviaGiro(recenti: true);
    }

    /// <summary>Attende i giri avviati: dati applicati, oppure giri superati e conclusi. Con il calcolo sincrono e' gia' completo.</summary>
    public async Task AttendiAggiornamentoAsync()
    {
        // Un giro puo' avviarne uno di completamento mentre si chiude: si riattende finche' non ne resta nessuno in corso.
        while (true)
        {
            Task[] attesi;
            lock (_giri) attesi = _giri.Where(t => !t.IsCompleted).ToArray();
            if (attesi.Length == 0) return;
            await Task.WhenAll(attesi);
        }
    }

    /// <summary>Entrata del ciclo di aggiornamento usata dai flussi che hanno gia' impostato i propri flag "da calcolare".</summary>
    private void RichiediAggiornamentoListe()
    {
        _listeDaCalcolare = true;
        if (_refreshInCorso == 0) AvviaGiro(recenti: false);
    }

    private Task AvviaGiro(bool recenti)
    {
        var richiesta = CatturaGiro(recenti);
        if (richiesta == null) return Task.CompletedTask;
        CaricamentoInCorso = true;
        var giro = EseguiGiroAsync(richiesta);
        lock (_giri)
        {
            _giri.RemoveAll(t => t.IsCompleted);
            _giri.Add(giro);
        }
        return giro;
    }

    /// <summary>Cattura sul thread UI tutto cio' che il giro legge. Null quando non c'e' nulla da calcolare.</summary>
    private RichiestaGiro? CatturaGiro(bool recenti)
    {
        var testata = _testataDaCalcolare && SezioneTestataVisibile;
        var copertura = _coperturaDaCalcolare && SezioneKpiVisibile;
        var liste = _listeDaCalcolare && SezioneListeVisibile;
        var filtri = _filtriDaCalcolare && SezioneGraficiVisibile;
        var dashboard = _dashboardDaAggiornare && SezioneGraficiVisibile;
        if (!recenti && !testata && !copertura && !liste && !filtri && !dashboard) return null;

        // Dati di un altro operatore: si tolgono dalla vista prima di qualunque lettura, cosi' il primo render non li mostra.
        var operatore = _state.Config.OperatoreAttivo;
        if (_operatoreDati != null && _operatoreDati != operatore) AzzeraDatiOperatore();
        _operatoreDati = operatore;

        var op = _state.OperatoreCorrente;
        // Snapshot della dashboard in parallelo al passo 1 (solo in background): lo snapshot legge l'indice e non dipende dai
        // cataloghi se non ci sono filtri gamma o categoria e il grafico di copertura e' spento. Altrimenti resta sequenziale.
        var dashboardAnticipata = dashboard && _aggiornaInBackground ? CatturaDashboard(null, anticipata: true) : null;
        if (dashboardAnticipata is not { IndiceDisponibile: true, Copertura: false }
            || dashboardAnticipata.Filtro.Gamma.Count > 0 || dashboardAnticipata.Filtro.CategoriaFunzionalita.Count > 0)
            dashboardAnticipata = null;
        return new RichiestaGiro
        {
            Generazione = ++_generazioneGiro,
            VersioneTestata = _versioneTestata,
            Asincrono = _aggiornaInBackground,
            Testata = testata,
            ModalitaPercentualeConformita = ModalitaPercentualeConformita,
            FiltroStato = testata ? FiltroStatoConformita() : new DashboardFilter(),
            Copertura = copertura,
            Liste = liste,
            ListeNelBatch = liste && string.IsNullOrWhiteSpace(_testoRicercaApplicato),
            Filtri = filtri,
            Dashboard = dashboard,
            Recenti = recenti,
            CampiDaCaricare = filtri,
            Indice = GetScalableArchiveQuery(),
            Coordinatore = _state.CartellaValida ? _state.TestCases.IndexRecovery : null,
            Operatore = operatore,
            SoloMiei = SoloMiei,
            PaginaDaFare = PaginaDaFare,
            PaginaFatti = PaginaFatti,
            GammeConsentite = op == null || op.RuoloEffettivo != RuoloOperatore.Standard || op.GammeAssegnate.Count == 0
                ? null
                : op.GammeAssegnate.ToHashSet(StringComparer.OrdinalIgnoreCase),
            ElementiRecenti = recenti && _state.CartellaValida
                ? RecentItemsService.Leggi(_state).ToList()
                : new List<RecentItemsService.ElementoRecente>(),
            DashboardAnticipata = dashboardAnticipata,
        };
    }

    /// <summary>Un giro: letture su pool (Passo 1: campi, cataloghi, indice, Recenti), poi applicazione sul thread UI, poi la
    /// dashboard (Passo 2, letture sull'indice con i cataloghi gia' applicati). Senza modalita' in background tutto e' in linea.</summary>
    private async Task EseguiGiroAsync(RichiestaGiro r)
    {
        RisultatoPassoUno? uno = null;
        var riuscito = false;
        try
        {
            if (r.Testata || r.Copertura || r.Liste || r.Filtri || r.Recenti)
            {
                // Lo snapshot della dashboard (solo indice) parte insieme al passo 1, se la richiesta anticipata lo consente.
                var anticipata = r.DashboardAnticipata;
                var snapshot = anticipata != null ? Task.Run(() => LeggiSnapshotAnticipato(anticipata)) : null;
                var passo = await LeggiInBackground(r, () => LeggiPassoUno(r));
                uno = passo;
                // Stesso passaggio sul thread UI: applicazione del Passo 1 e cattura della dashboard (un solo ritorno al thread UI).
                var (attuale, richiesta) = await SulThreadUI(r, () =>
                {
                    ApplicaPassoUno(r, passo);
                    return ChiudiOProseguiDashboard(uno);
                });
                if (attuale && richiesta != null) await EseguiDashboardAsync(r, richiesta, snapshot);
            }
            else
            {
                var (attuale, richiesta) = await SulThreadUI(r, () => ChiudiOProseguiDashboard(uno));
                if (attuale && richiesta != null) await EseguiDashboardAsync(r, richiesta, null);
            }
            riuscito = true;
        }
        catch (Exception ex) when (r.Asincrono)
        {
            await EseguiSulUI(r.Asincrono, () =>
            {
                UltimaEccezioneAggiornamento = ex;
                if (r.Generazione == _generazioneGiro) CaricamentoInCorso = false;
            });
            CrashLogService.Registra("Aggiornamento Home in background", ex);
        }
        finally
        {
            // Sincrono: nessun passaggio sul thread UI, quindi si chiude qui. In background l'indicatore si chiude nell'ultima applicazione.
            if (!r.Asincrono && r.Generazione == _generazioneGiro) CaricamentoInCorso = false;
        }

        // Le selezioni salvate dei filtri si ripristinano nel Passo 1, dopo la cattura della richiesta: la testata (Stato %) di questo giro
        // e' stata calcolata senza di esse. Il ripristino ha rimesso la testata "da calcolare" ma, essendo dentro l'applicazione, non
        // ha avviato nessun giro: lo si avvia qui, solo se questo e' ancora l'ultimo e la testata e' rimasta indietro.
        // Dopo un errore non si riavvia: la testata resterebbe indietro e il giro si ripeterebbe all'infinito.
        if (riuscito && r.Testata)
            await EseguiSulUI(r.Asincrono, () =>
            {
                if (r.Generazione == _generazioneGiro && _refreshInCorso == 0 && _testataDaCalcolare && SezioneTestataVisibile)
                    AvviaGiro(recenti: false);
            });
    }

    /// <summary>Sul thread UI, dopo il Passo 1 (o da solo, per la sola dashboard): cattura la richiesta dei grafici. Se non c'e'
    /// nulla da calcolare chiude l'indicatore del giro.</summary>
    private RichiestaDashboard? ChiudiOProseguiDashboard(RisultatoPassoUno? uno)
    {
        var richiesta = CatturaDashboard(uno);
        if (richiesta == null) CaricamentoInCorso = false;
        return richiesta;
    }

    /// <summary>Dashboard: letture sull'indice fuori dal thread UI, applicazione sul thread UI, che chiude l'indicatore.</summary>
    private async Task EseguiDashboardAsync(RichiestaGiro r, RichiestaDashboard richiesta,
        Task<(bool Riuscito, LocalDefaultDashboardSnapshot? Snapshot)>? snapshot)
    {
        DatiDashboardIndice? dati;
        if (snapshot != null && r.DashboardAnticipata is { } anticipata && richiesta.IndiceDisponibile && richiesta.SenzaRevisioni == null
            && !richiesta.Copertura && StessoFiltroDashboard(anticipata.Filtro, richiesta.Filtro))
        {
            // Lo snapshot e' gia' letto in parallelo al passo 1 con lo stesso filtro: qui si completa solo la mappatura sui veicoli.
            var (riuscito, valori) = await snapshot;
            dati = riuscito
                ? await LeggiInBackground(r, () =>
                {
                    using var letturaMaster = MasterLetturaScope.Apri();
                    return CostruisciDatiDashboard(richiesta, valori!, null, richiesta.Veicoli ?? _state.Master.Veicoli.Load());
                })
                : new DatiDashboardIndice { Riuscito = false };
        }
        else
        {
            dati = richiesta.IndiceDisponibile ? await LeggiInBackground(r, () => LeggiDashboard(richiesta)) : null;
        }
        await SulThreadUI(r, () =>
        {
            ApplicaDashboard(richiesta, dati);
            CaricamentoInCorso = false;
            return true;
        });
    }

    private static Task<T> LeggiInBackground<T>(RichiestaGiro r, Func<T> lettura) =>
        r.Asincrono ? Task.Run(lettura) : Task.FromResult(lettura());

    /// <summary>Applica sul thread UI, solo se il giro e' ancora l'ultimo: altrimenti il risultato e' superato e si scarta.</summary>
    private Task<(bool Attuale, T? Valore)> SulThreadUI<T>(RichiestaGiro r, Func<T> azione)
    {
        if (!r.Asincrono || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            return Task.FromResult(AzioneSeAttuale(r, azione));
        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => AzioneSeAttuale(r, azione)).GetTask();
    }

    private (bool Attuale, T? Valore) AzioneSeAttuale<T>(RichiestaGiro r, Func<T> azione)
    {
        if (r.Generazione != _generazioneGiro)
        {
            GiriScartati++;
            return (false, default);
        }
        return (true, azione());
    }

    /// <summary>Azione sul thread UI senza controllo di generazione (chiusura del giro, eccezione).</summary>
    private static Task EseguiSulUI(bool asincrono, Action azione)
    {
        if (!asincrono || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            azione();
            return Task.CompletedTask;
        }
        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(azione).GetTask();
    }

    /// <summary>Lettura sul percorso indicizzato, sicura fuori dal thread UI: nessuna scrittura su stato osservabile.
    /// Senza coordinatore (indice non gestito) un guasto viene registrato e la lettura risulta fallita.</summary>
    private static bool TentaIndice(LocalIndexRecoveryCoordinator? coordinatore, string operazione, Action lettura)
    {
        if (coordinatore != null) return coordinatore.TryRead(operazione, lettura);
        try
        {
            lettura();
            return true;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra($"{operazione} (Home scalabile)", ex);
            return false;
        }
    }

    // ------------------------------------------------ Passo 1: letture su pool

    /// <summary>Letture del Passo 1 in UNA lettura logica dei master: campi custom (solo al primo giro con i grafici),
    /// cataloghi dei filtri, batch indicizzato (o sonda), Recenti. Non tocca nessuna proprieta' osservabile.</summary>
    private RisultatoPassoUno LeggiPassoUno(RichiestaGiro r)
    {
        GateLetturaPerTest?.Invoke();
        GuastoLetturaPerTest?.Invoke();
        using var letturaMaster = MasterLetturaScope.Apri();
        var uno = new RisultatoPassoUno();
        // Veicoli e Requisiti si caricano una sola volta per giro: servono a cataloghi, batch e dashboard.
        var veicoli = new Lazy<List<Veicolo>>(() => _state.CartellaValida ? _state.Master.Veicoli.Load() : new List<Veicolo>());
        var requisiti = new Lazy<List<Requisito>>(() => _state.CartellaValida ? _state.Master.Requisiti.Load() : new List<Requisito>());

        // Campi di sistema filtrabili: letti ad ogni giro che calcola i filtri (non solo al primo), cosi' un campo abilitato o
        // disabilitato dal catalogo entra o esce dai filtri anche su una Home gia' in cache (vedi ApplicaCampiCustom).
        if (r.CampiDaCaricare) uno.CampiCustom = CampoSistemaCatalogoService.CampiUtilizzabiliComeFiltro(_state);
        var chiaviCampi = uno.CampiCustom?.Select(c => c.Chiave).ToList() ?? new List<string>();

        if (r.Filtri) uno.Cataloghi = LeggiCataloghiFiltri(veicoli, requisiti);

        if (r.Indice == null) uno.IndiceOk = false;
        else if (!r.Testata && !r.Copertura && !r.ListeNelBatch && !r.Filtri)
        {
            PerfContatori.IncrementaHomeSondaIndice();
            uno.IndiceOk = TentaIndice(r.Coordinatore, "Sonda indice Home", () => r.Indice!.LeggiRevisione());
            uno.FallimentoIndice = !uno.IndiceOk && r.Coordinatore != null;
        }
        else LeggiBatchIndicizzato(r, uno, veicoli, requisiti, chiaviCampi);

        if (r.Recenti) uno.Recenti = LeggiRecenti(r.ElementiRecenti);
        if (veicoli.IsValueCreated) uno.Veicoli = veicoli.Value;
        if (requisiti.IsValueCreated) uno.Requisiti = requisiti.Value;
        return uno;
    }

    private CataloghiFiltri LeggiCataloghiFiltri(Lazy<List<Veicolo>> veicoli, Lazy<List<Requisito>> requisiti)
    {
        var funzionalita = _state.CartellaValida ? _state.Master.Funzionalita.Load().Where(f => !f.Archiviata).ToList() : new();
        var operatori = (_state.CartellaValida ? _state.Master.Operatori.Load().Where(o => !o.Disattivo).Select(o => o.Nome).OrderBy(x => x) : Enumerable.Empty<string>()).ToList();
        return new CataloghiFiltri
        {
            CategoriaPerFunzionalita = funzionalita.Where(f => f.Categorie.Count > 0).ToDictionary(f => f.Nome, f => f.Categorie),
            Gamma = (_state.CartellaValida ? _state.Master.Gamma.Load().Where(g => !g.Archiviata).Select(g => g.Nome).OrderBy(x => x) : Enumerable.Empty<string>()).ToList(),
            Veicoli = (_state.CartellaValida ? veicoli.Value.Where(v => !v.Archiviata).Select(v => v.Nome).OrderBy(x => x) : Enumerable.Empty<string>()).ToList(),
            Funzionalita = funzionalita.Select(f => f.Nome).OrderBy(x => x).ToList(),
            CategorieFunzionalita = funzionalita.SelectMany(f => f.Categorie).Distinct().OrderBy(x => x).ToList(),
            Requisiti = (_state.CartellaValida ? requisiti.Value.Where(r => !r.Archiviata).Select(r => r.Id).OrderBy(x => x) : Enumerable.Empty<string>()).ToList(),
            Strumenti = (_state.CartellaValida ? _state.Master.Strumenti.Load().Where(s => !s.Archiviata).Select(s => s.Nome).OrderBy(x => x) : Enumerable.Empty<string>()).ToList(),
            Operatori = operatori,
            TipiTest = (_state.CartellaValida ? _state.Master.TestTemplate.Load().Where(t => !t.Archiviata).OrderBy(t => t.Nome) : Enumerable.Empty<TestTemplate>())
                .Select(t => (t.Uid.ToString(), t.Nome)).ToList(),
        };
    }

    /// <summary>Batch unificato dell'indice per le sezioni richieste. In caso di guasto il risultato resta fallito e il
    /// Passo 1 applica il percorso non indicizzato per TUTTE le sezioni richieste (mai un ibrido).</summary>
    private void LeggiBatchIndicizzato(RichiestaGiro r, RisultatoPassoUno uno, Lazy<List<Veicolo>> veicoli,
        Lazy<List<Requisito>> requisiti, IReadOnlyList<string> chiaviCampi)
    {
        var indice = r.Indice!;
        LocalHomeAggiornaBatchResult? batch = null;
        StatiMatrice? distribuzioneConformita = null;
        long nonConformitaAperte = 0, changeRequestAperte = 0, daClassificare = 0;
        var ok = TentaIndice(r.Coordinatore, "Aggiorna Home (batch unificato)", () =>
        {
            if (r.Testata) PerfContatori.IncrementaHomeLetturaSommario();
            if (r.Copertura) PerfContatori.IncrementaHomeLetturaCopertura();
            if (r.ListeNelBatch) PerfContatori.IncrementaHomeLetturaListe();
            if (r.Filtri) PerfContatori.IncrementaHomeLetturaFiltri();
            var allowedVehicles = r.ListeNelBatch && r.GammeConsentite != null
                ? VeicoliConsentiti(r.GammeConsentite, veicoli.Value)
                : null;
            var assignee = r.SoloMiei ? r.Operatore : null;
            batch = indice.GetHomeAggiornaBatch(new LocalHomeAggiornaBatchRequest
            {
                IncludeEcu = r.Filtri,
                IncludeSummary = r.Testata,
                ActiveOperator = r.Operatore,
                Requisiti = r.Copertura ? requisiti.Value : null,
                SoloBanco = false,
                SoloVeicolo = false,
                CustomFieldKeys = r.Filtri ? chiaviCampi : Array.Empty<string>(),
                TodoQuery = !r.ListeNelBatch ? null : new LocalTestArchiveQuery
                {
                    WorkStates = new[]
                    {
                        StatoLavoroTest.Pianificato,
                        StatoLavoroTest.InCorso,
                        StatoLavoroTest.DaRivedere,
                        StatoLavoroTest.InAttesa,
                        StatoLavoroTest.DaRitestare
                    },
                    Assignee = assignee,
                    AllowedVehicles = allowedVehicles,
                    Page = r.PaginaDaFare,
                    PageSize = HomeListPageSize
                },
                DoneQuery = !r.ListeNelBatch ? null : new LocalTestArchiveQuery
                {
                    WorkState = StatoLavoroTest.Concluso,
                    Assignee = assignee,
                    AllowedVehicles = allowedVehicles,
                    Page = r.PaginaFatti,
                    PageSize = HomeListPageSize
                }
            });
            if (r.Testata)
            {
                distribuzioneConformita = r.ModalitaPercentualeConformita == 1
                    ? batch.Summary.TestStatusDistribution
                    // 6.7.1: con filtri (operatore, TIME, ...) le fonti dell'indice non bastano: stesso calcolo del Report sui Test.
                    : r.FiltroStato.HaFiltriAttivi
                        ? HomeConformitySummaryService.CalcolaStatoCorrenteFiltrato(_state.TestCases.GetAll(), r.FiltroStato,
                            veicoli.Value, _state.Master.Funzionalita.Load(), _state.Master.Componente.Load(),
                            _state.Master.StatiFunzionalitaEcuVeicolo.Load())
                        : HomeConformitySummaryService.CalcolaStatoCorrente(
                            indice.GetHomeFunctionalitySources(), veicoli.Value,
                            _state.Master.Funzionalita.Load(), _state.Master.Componente.Load(),
                            _state.Master.StatiFunzionalitaEcuVeicolo.Load());
                DistribuzioneConformitaPreparataPerTest?.Invoke(
                    r.ModalitaPercentualeConformita, Environment.CurrentManagedThreadId);
                nonConformitaAperte = _state.NonConformita.CountByState(StatoNonConformita.Aperta);
                changeRequestAperte = ContaChangeRequestAperte();
                daClassificare = _state.Segnalazioni.CountByState(StatoSegnalazione.DaClassificare);
            }
        });

        uno.IndiceOk = ok && batch != null;
        uno.FallimentoIndice = !uno.IndiceOk && r.Coordinatore != null;
        if (!uno.IndiceOk) return;
        uno.Batch = batch;
        uno.DistribuzioneConformita = distribuzioneConformita;
        // Scorciatoia "nessuna revisione" della dashboard: vale solo se il sommario e' stato letto in questo giro.
        uno.SenzaRevisioni = r.Testata && !batch!.HasAnyRevision
            ? (batch.Summary.TotalTests, batch.Revisione)
            : (ValueTuple<long, long>?)null;
        uno.NonConformitaAperte = nonConformitaAperte;
        uno.ChangeRequestAperte = changeRequestAperte;
        uno.DaClassificare = daClassificare;
    }

    private static IReadOnlyCollection<string>? VeicoliConsentiti(HashSet<string> gamme, List<Veicolo> veicoli) =>
        veicoli.Where(v => gamme.Contains(v.GammaNome)).Select(v => v.Nome).ToList();

    private List<VoceRecenteHome> LeggiRecenti(IReadOnlyList<RecentItemsService.ElementoRecente> elementi)
    {
        var voci = new List<VoceRecenteHome>();
        foreach (var elemento in elementi)
        {
            var titoloAttuale = TitoloElementoRecente(elemento);
            if (titoloAttuale == null) continue;
            voci.Add(new VoceRecenteHome(elemento, string.IsNullOrWhiteSpace(titoloAttuale) ? elemento.Titolo : titoloAttuale));
        }
        return voci;
    }

    // ------------------------------------------------ applicazione del Passo 1 (thread UI)

    private void ApplicaPassoUno(RichiestaGiro r, RisultatoPassoUno uno)
    {
        _refreshInCorso++;
        try
        {
            if (uno.CampiCustom != null) ApplicaCampiCustom(uno.CampiCustom);
            if (uno.Cataloghi != null) ApplicaCataloghiFiltri(uno.Cataloghi);
            if (uno.FallimentoIndice) AggiornaAvvisoIndice();

            if (uno.IndiceOk && uno.Batch != null)
            {
                ApplicaBatch(r, uno, uno.Batch);
                _dashboardSenzaRevisioni = uno.SenzaRevisioni;
            }
            else if (r.Indice == null || !uno.IndiceOk)
            {
                _dashboardSenzaRevisioni = null;
            }

            // Percorso non indicizzato: solo per le sezioni richieste, mai un ibrido con l'indice.
            if (!uno.IndiceOk)
            {
                if (r.Filtri) AggiornaFiltriInMemoria();
                if (r.Testata || r.Copertura) AggiornaKpiTestata(r.Testata, r.Copertura);
            }
            // Ricerca testuale attiva: la lista resta sul percorso storico (vedi TryAggiornaDaFareEFattiScalabile).
            if (r.Liste && (!r.ListeNelBatch || !uno.IndiceOk)) AggiornaDaFareEFatti();

            // Con un filtro cambiato dopo la cattura (versione diversa) il flag resta acceso: la testata di questo giro e' gia' vecchia.
            if (r.Testata && r.VersioneTestata == _versioneTestata) _testataDaCalcolare = false;
            if (r.Copertura) _coperturaDaCalcolare = false;
            if (r.Liste) _listeDaCalcolare = false;
            if (r.Filtri)
            {
                _filtriDaCalcolare = false;
                LegaFiltriPersistenti();
            }
        }
        finally { _refreshInCorso--; }

        if (uno.Recenti != null) ApplicaRecenti(uno.Recenti);
    }

    private void ApplicaBatch(RichiestaGiro r, RisultatoPassoUno uno, LocalHomeAggiornaBatchResult batch)
    {
        if (r.Testata)
        {
            TotaleTestCase = batch.Summary.TotalTests;
            DaRitestare = batch.Summary.ToRetest;
            MieiAttivi = batch.Summary.MyActive;
            ImpostaDistribuzioneConformita(uno.DistribuzioneConformita ?? default);
            TotaleNcAperte = uno.NonConformitaAperte;
            ChangeRequestAperte = uno.ChangeRequestAperte;
            DaClassificare = uno.DaClassificare;
            TestSenzaEcu = batch.Summary.WithoutEcu;
        }
        if (r.Copertura) CoperturaRequisitiPercentuale = (batch.Coverage ?? new CoverageBucket()).PercentualeCompletamento;
        if (r.Testata || r.Copertura) NotificaKpiTestata();

        if (r.Filtri)
        {
            FiltroEcuDashboard.Ripopola(batch.Componente.Select(x => (x, x)));
            foreach (var filtroCustom in FiltriCampiCustomDashboard)
                if (batch.CustomFieldValues.TryGetValue(filtroCustom.Chiave, out var valori))
                    filtroCustom.Filtro.Ripopola(CampoSistemaCatalogoService.OpzioniFiltro(filtroCustom.Tipo, filtroCustom.OpzioniLista, valori)
                        .Select(v => (v, v)));
        }

        if (r.ListeNelBatch && batch.Todo != null && batch.Done != null)
        {
            PaginaDaFare = batch.Todo.Page;
            PaginaFatti = batch.Done.Page;
            TotaleDaFare = batch.Todo.TotalCount;
            TotaleFatti = batch.Done.TotalCount;
            DaFare.ReplaceWith(batch.Todo.Items);
            Fatti.ReplaceWith(batch.Done.Items);
            NotificaPagineHome();
        }
    }

    /// <summary>Allinea i filtri dei campi di sistema al catalogo, ad ogni giro dei filtri: i nuovi si aggiungono, quelli non piu'
    /// eleggibili escono dalla collezione (e quindi dal predicato della dashboard), gli altri restano con la loro selezione.</summary>
    private void ApplicaCampiCustom(IReadOnlyList<CampoSistemaDefinizione> campi)
    {
        var (aggiunti, rimossi) = FiltriCampiCustomSync.Allinea(FiltriCampiCustomDashboard, campi);
        foreach (var filtro in aggiunti) filtro.Filtro.Cambiato += AggiornaDashboard;
        foreach (var filtro in rimossi)
        {
            filtro.Filtro.Cambiato -= AggiornaDashboard;
            // Un filtro rimosso con una selezione filtrava la dashboard: il ricalcolo di questo giro non deve piu' tenerne conto.
            if (filtro.Filtro.HaSelezione) _dashboardDaAggiornare = true;
        }
        if (aggiunti.Count > 0 || rimossi.Count > 0) OnPropertyChanged(nameof(HaFiltriDashboardAttivi));
    }

    private void ApplicaCataloghiFiltri(CataloghiFiltri c)
    {
        _categoriaPerFunzionalita = c.CategoriaPerFunzionalita;
        FiltroGammaDashboard.Ripopola(c.Gamma.Select(x => (x, x)));
        FiltroVeicoloDashboard.Ripopola(c.Veicoli.Select(x => (x, x)));
        FiltroFunzionalitaDashboard.Ripopola(c.Funzionalita.Select(x => (x, x)));
        FiltroCategoriaFunzionalitaDashboard.Ripopola(c.CategorieFunzionalita.Select(x => (x, x)));
        FiltroRequisitoDashboard.Ripopola(c.Requisiti.Select(x => (x, x)));
        FiltroStrumentoDashboard.Ripopola(c.Strumenti.Select(x => (x, x)));
        FiltroOperatoreDashboard.Ripopola(c.Operatori.Select(x => (x, x)));
        FiltroAssegnatarioDashboard.Ripopola(c.Operatori.Select(x => (x, x)));
        FiltroTipoTestDashboard.Ripopola(c.TipiTest);
    }

    private void ApplicaRecenti(List<VoceRecenteHome> voci)
    {
        Recenti.ReplaceWith(voci);
        OnPropertyChanged(nameof(RecentiVuoti));
    }

    /// <summary>Operatore cambiato: i dati operativi del precedente (Da fare/Fatti, miei attivi, totali di pagina) escono dalla vista.</summary>
    private void AzzeraDatiOperatore()
    {
        DaFare.ReplaceWith(Array.Empty<TestCase>());
        Fatti.ReplaceWith(Array.Empty<TestCase>());
        TotaleDaFare = TotaleFatti = 0;
        NotificaPagineHome();
        MieiAttivi = 0;
        OnPropertyChanged(nameof(MieiAttivi));
    }

    // ------------------------------------------------ Passo 2: dashboard

    /// <param name="anticipata">Cattura prima del passo 1: senza revisioni note, veicoli e requisiti (non ancora letti).</param>
    private RichiestaDashboard? CatturaDashboard(RisultatoPassoUno? uno, bool anticipata = false)
    {
        if (!_dashboardDaAggiornare || !SezioneGraficiVisibile) return null;
        var query = GetScalableArchiveQuery() == null ? null : _homeDashboardQuery;
        var andamentoSelezionato = ChartOptions.Count > 1 && ChartOptions[1].Selezionato;
        return new RichiestaDashboard
        {
            Filtro = CreaFiltroDashboard(),
            IndiceDisponibile = _state.CartellaValida && query != null && !andamentoSelezionato,
            Query = query,
            Coordinatore = _state.CartellaValida ? _state.TestCases.IndexRecovery : null,
            Copertura = ChartOptions.Count > 0 && ChartOptions[0].Selezionato,
            Donut = ChartOptions.Count > 2 && ChartOptions[2].Selezionato,
            SenzaRevisioni = anticipata ? null : _dashboardSenzaRevisioni,
            CategoriaPerFunzionalita = _categoriaPerFunzionalita,
            Veicoli = anticipata ? null : uno?.Veicoli,
            Requisiti = anticipata ? null : uno?.Requisiti,
            CartellaValida = _state.CartellaValida,
        };
    }

    /// <summary>Letture della dashboard sull'indice: snapshot, copertura e donut. Veicoli e Requisiti arrivano dal Passo 1
    /// quando ci sono; altrimenti si caricano qui, in una lettura logica propria.</summary>
    private DatiDashboardIndice LeggiDashboard(RichiestaDashboard d)
    {
        using var letturaMaster = MasterLetturaScope.Apri();
        var veicoli = d.Veicoli ?? _state.Master.Veicoli.Load();
        var requisiti = d.Copertura ? d.Requisiti ?? _state.Master.Requisiti.Load() : null;
        DatiDashboardIndice? dati = null;
        var riuscito = TentaIndice(d.Coordinatore, "Dashboard scalabile Home", () =>
        {
            PerfContatori.IncrementaHomeLetturaGrafici();
            var filtro = d.Filtro;
            var gammaSelezionate = new HashSet<string>(filtro.Gamma, StringComparer.OrdinalIgnoreCase);
            var veicoliDelleGamma = gammaSelezionate.Count == 0
                ? null
                : veicoli.Where(v => gammaSelezionate.Contains(v.GammaNome)).Select(v => v.Nome).ToList();
            var categorieSelezionate = new HashSet<string>(filtro.CategoriaFunzionalita, StringComparer.OrdinalIgnoreCase);
            var funzionalitaDelleCategorie = categorieSelezionate.Count == 0
                ? null
                : d.CategoriaPerFunzionalita.Where(x => x.Value.Any(categorieSelezionate.Contains)).Select(x => x.Key).ToList();
            var snapshot = d.Query!.GetSnapshot(filtro, veicoliDelleGamma, funzionalitaDelleCategorie,
                d.SenzaRevisioni?.Conteggio, d.SenzaRevisioni?.Revisione);
            var coverage = d.Copertura
                ? d.Query.GetRequirementCoverage(requisiti!, filtro, veicoliDelleGamma, funzionalitaDelleCategorie)
                : null;

            dati = CostruisciDatiDashboard(d, snapshot, coverage, veicoli);
        });
        return riuscito && dati != null ? dati : new DatiDashboardIndice { Riuscito = false };
    }

    /// <summary>Snapshot della dashboard letto solo sull'indice, senza cataloghi: nel percorso in parallelo al passo 1 vale
    /// senza filtri gamma e categoria, cioe' con le stesse liste nulle del percorso in linea.</summary>
    private static (bool Riuscito, LocalDefaultDashboardSnapshot? Snapshot) LeggiSnapshotAnticipato(RichiestaDashboard d)
    {
        LocalDefaultDashboardSnapshot? snapshot = null;
        var riuscito = TentaIndice(d.Coordinatore, "Dashboard scalabile Home", () =>
        {
            PerfContatori.IncrementaHomeLetturaGrafici();
            snapshot = d.Query!.GetSnapshot(d.Filtro, null, null, null, null);
        });
        return (riuscito && snapshot != null, snapshot);
    }

    /// <summary>Valori e donut della dashboard dai dati letti sull'indice: stessa mappatura per il percorso in linea e per quello
    /// con lo snapshot anticipato.</summary>
    private static DatiDashboardIndice CostruisciDatiDashboard(RichiestaDashboard d, LocalDefaultDashboardSnapshot snapshot,
        CoverageBucket? coverage, IReadOnlyList<Veicolo> veicoli)
    {
        var gammaByVehicle = veicoli
            .Where(v => !string.IsNullOrWhiteSpace(v.Nome) && !string.IsNullOrWhiteSpace(v.GammaNome))
            .ToDictionary(v => v.Nome, v => v.GammaNome, StringComparer.OrdinalIgnoreCase);
        return new DatiDashboardIndice
        {
            Riuscito = true,
            TotaleNelFiltro = snapshot.OutcomeCounts.Values.Sum(),
            Copertura = coverage,
            DonutSottoveicolo = d.Donut
                ? snapshot.VehicleCounts.Select(x => (Nome: x.Key, Valore: x.Value)).OrderByDescending(x => x.Valore).ToList()
                : null,
            DonutEsito = d.Donut ? snapshot.OutcomeCounts.Select(x => (Esito: x.Key, Valore: x.Value)).ToList() : null,
            DonutVeicoloEsito = d.Donut ? snapshot.VehicleOutcomeRows.ToList() : null,
            Kpi = snapshot.IterationKpi,
            EsecuzioniPerOperatore = snapshot.ExecutionCountsByOperator.Select(x => (Chiave: x.Key, Valore: x.Value)).ToList(),
            PerGamma = snapshot.VehicleCounts
                .Where(x => gammaByVehicle.ContainsKey(x.Key))
                .GroupBy(x => gammaByVehicle[x.Key], StringComparer.OrdinalIgnoreCase)
                .Select(g => (Gamma: g.Key, Valore: g.Sum(x => x.Value)))
                .OrderByDescending(x => x.Valore).ToList(),
        };
    }

    /// <summary>Stessi valori di filtro, con le stesse liste nello stesso ordine: lo snapshot anticipato vale solo se il filtro
    /// applicato e' quello con cui e' stato letto.</summary>
    private static bool StessoFiltroDashboard(DashboardFilter a, DashboardFilter b) =>
        a.Gamma.SequenceEqual(b.Gamma) && a.Veicolo.SequenceEqual(b.Veicolo) && a.Componente.SequenceEqual(b.Componente)
        && a.Funzionalita.SequenceEqual(b.Funzionalita) && a.CategoriaFunzionalita.SequenceEqual(b.CategoriaFunzionalita)
        && a.Requisito.SequenceEqual(b.Requisito) && a.Strumento.SequenceEqual(b.Strumento)
        && a.Operatore.SequenceEqual(b.Operatore) && a.Assegnatario.SequenceEqual(b.Assegnatario)
        && a.TestTemplateUid.SequenceEqual(b.TestTemplateUid) && a.Esito.SequenceEqual(b.Esito)
        && a.DataDa == b.DataDa && a.DataA == b.DataA
        && a.TempoOperazione == b.TempoOperazione && a.TempoTotaleTest == b.TempoTotaleTest   // TIME: un valore diverso non riusa lo snapshot
        && a.CampiCustom.Count == b.CampiCustom.Count
        && a.CampiCustom.All(kv => b.CampiCustom.TryGetValue(kv.Key, out var altro) && kv.Value.SequenceEqual(altro));

    /// <summary>Applica la dashboard sul thread UI: indice quando la lettura e' riuscita, altrimenti percorso non indicizzato.</summary>
    private void ApplicaDashboard(RichiestaDashboard d, DatiDashboardIndice? dati)
    {
        _dashboardDaAggiornare = false;
        OnPropertyChanged(nameof(HaFiltriDashboardAttivi));
        // Clear() rimuove i controlli: ogni card rilascia la bitmap che possiede (render alla dimensione della card), mentre le
        // bitmap della cache restano alla cache. Quando i dati di input di un grafico non sono cambiati, OttieniGraficoX riusa il
        // Bitmap gia' decodificato invece di rigenerare il PNG (render SkiaSharp, costoso). Un grafico deselezionato invalida la
        // propria cache, cosi' non trattiene Bitmap.
        var precedenti = ChartsAttivi.ToList();
        ChartsAttivi.Clear();
        foreach (var card in precedenti) card.Dispose();
        var coperturaSelezionata = ChartOptions.Count > 0 && ChartOptions[0].Selezionato;
        var andamentoSelezionato = ChartOptions.Count > 1 && ChartOptions[1].Selezionato;
        var donutSelezionato = ChartOptions.Count > 2 && ChartOptions[2].Selezionato;
        if (!coperturaSelezionata) InvalidaGraficoCopertura();
        if (!andamentoSelezionato) InvalidaGraficoAndamento();
        if (!donutSelezionato) InvalidaGraficoDonut();
        if (!_state.CartellaValida) return;

        if (dati is { Riuscito: true })
        {
            ApplicaDashboardIndice(dati);
            return;
        }
        if (dati != null && d.Coordinatore != null) AggiornaAvvisoIndice();
        AggiornaDashboardInMemoria(coperturaSelezionata, andamentoSelezionato, donutSelezionato);
    }

    private void ApplicaDashboardIndice(DatiDashboardIndice dati)
    {
        TestCaseNelFiltro = dati.TotaleNelFiltro;
        OnPropertyChanged(nameof(TestCaseNelFiltro));
        OnPropertyChanged(nameof(TestoFiltroAttivo));

        if (dati.Copertura != null)
            ChartsAttivi.Add(OttieniGraficoCopertura(LocalizationService.Instance.Traduci("GraficoCoperturaRequisiti"), dati.Copertura));
        if (dati.DonutSottoveicolo != null)
            ChartsAttivi.Add(OttieniGraficoDonut(LocalizationService.Instance.Traduci("GraficoEsitiSottoveicolo"),
                dati.DonutSottoveicolo, dati.DonutEsito!, dati.DonutVeicoloEsito!));
        OnPropertyChanged(nameof(ChartColumns));

        var kpi = dati.Kpi;
        var casoConcluso = LocalizationService.Instance.Traduci(kpi.CasiRaggiuntiConforme == 1 ? "KpiCasoConclusoSingolare" : "KpiCasoConclusoPlurale");
        var maiArrivato = LocalizationService.Instance.Traduci(kpi.CasiMaiRaggiuntiConforme == 1 ? "KpiMaiArrivatoSingolare" : "KpiMaiArrivatoPlurale");
        KpiIterazioniTesto = string.Format(LocalizationService.Instance.Traduci("KpiIterazioniFmt"),
            kpi.MediaIterazioni, kpi.CasiRaggiuntiConforme, casoConcluso,
            kpi.CasiMaiRaggiuntiConforme, maiArrivato, kpi.PercentualeMaiRaggiunti);
        OnPropertyChanged(nameof(KpiIterazioniTesto));

        PerOperatore = dati.EsecuzioniPerOperatore
            .Select(x => new WidgetRiga
            {
                Etichetta = x.Chiave,
                Valore = x.Valore,
                Sottotitolo = LocalizationService.Instance.Traduci(x.Valore == 1 ? "EsecuzioneSingolare" : "EsecuzionePlurale")
            }).OrderByDescending(x => x.Valore).ToList();
        OnPropertyChanged(nameof(PerOperatore));

        PerGamma = dati.PerGamma
            .Select(x => new WidgetRiga { Etichetta = x.Gamma, Valore = x.Valore, Sottotitolo = "test" })
            .OrderByDescending(x => x.Valore).ToList();
        OnPropertyChanged(nameof(PerGamma));
    }

    // ------------------------------------------------ strutture del giro

    private sealed class RichiestaGiro
    {
        public required int Generazione { get; init; }
        public int VersioneTestata { get; init; }
        public required bool Asincrono { get; init; }
        public required bool Testata { get; init; }
        public required int ModalitaPercentualeConformita { get; init; }
        /// <summary>Filtri della dashboard per lo Stato % (6.7.1), catturati sul thread UI; vuoto se la testata non e' richiesta.</summary>
        public required DashboardFilter FiltroStato { get; init; }
        public required bool Copertura { get; init; }
        public required bool Liste { get; init; }
        public required bool ListeNelBatch { get; init; }
        public required bool Filtri { get; init; }
        public required bool Dashboard { get; init; }
        public required bool Recenti { get; init; }
        public required bool CampiDaCaricare { get; init; }
        public required LocalTestArchiveQueryService? Indice { get; init; }
        public required LocalIndexRecoveryCoordinator? Coordinatore { get; init; }
        public required string? Operatore { get; init; }
        public required bool SoloMiei { get; init; }
        public required long PaginaDaFare { get; init; }
        public required long PaginaFatti { get; init; }
        public required HashSet<string>? GammeConsentite { get; init; }
        public required IReadOnlyList<RecentItemsService.ElementoRecente> ElementiRecenti { get; init; }
        /// <summary>Dashboard catturata prima del passo 1 per lo snapshot in parallelo (null se non applicabile).</summary>
        public RichiestaDashboard? DashboardAnticipata { get; init; }
    }

    private sealed class RisultatoPassoUno
    {
        public IReadOnlyList<CampoSistemaDefinizione>? CampiCustom { get; set; }
        public CataloghiFiltri? Cataloghi { get; set; }
        public bool IndiceOk { get; set; }
        public bool FallimentoIndice { get; set; }
        public LocalHomeAggiornaBatchResult? Batch { get; set; }
        public StatiMatrice? DistribuzioneConformita { get; set; }
        public (long Conteggio, long Revisione)? SenzaRevisioni { get; set; }
        public long NonConformitaAperte { get; set; }
        public long ChangeRequestAperte { get; set; }
        public long DaClassificare { get; set; }
        public List<VoceRecenteHome>? Recenti { get; set; }
        public List<Veicolo>? Veicoli { get; set; }
        public List<Requisito>? Requisiti { get; set; }
    }

    private sealed class CataloghiFiltri
    {
        public required Dictionary<string, List<string>> CategoriaPerFunzionalita { get; init; }
        public required List<string> Gamma { get; init; }
        public required List<string> Veicoli { get; init; }
        public required List<string> Funzionalita { get; init; }
        public required List<string> CategorieFunzionalita { get; init; }
        public required List<string> Requisiti { get; init; }
        public required List<string> Strumenti { get; init; }
        public required List<string> Operatori { get; init; }
        public required List<(string Valore, string Etichetta)> TipiTest { get; init; }
    }

    private sealed class RichiestaDashboard
    {
        public required DashboardFilter Filtro { get; init; }
        public required bool IndiceDisponibile { get; init; }
        public required LocalHomeDashboardQueryService? Query { get; init; }
        public required LocalIndexRecoveryCoordinator? Coordinatore { get; init; }
        public required bool Copertura { get; init; }
        public required bool Donut { get; init; }
        public required (long Conteggio, long Revisione)? SenzaRevisioni { get; init; }
        public required Dictionary<string, List<string>> CategoriaPerFunzionalita { get; init; }
        public required List<Veicolo>? Veicoli { get; init; }
        public required List<Requisito>? Requisiti { get; init; }
        public required bool CartellaValida { get; init; }
    }

    private sealed class DatiDashboardIndice
    {
        public bool Riuscito { get; init; }
        public long TotaleNelFiltro { get; init; }
        public CoverageBucket? Copertura { get; init; }
        public List<(string Nome, long Valore)>? DonutSottoveicolo { get; init; }
        public List<(Esito Esito, long Valore)>? DonutEsito { get; init; }
        public List<(string Nome, Esito Esito, long Valore)>? DonutVeicoloEsito { get; init; }
        public KpiIterazioniConforme Kpi { get; init; } = new();
        public List<(string Chiave, long Valore)> EsecuzioniPerOperatore { get; init; } = new();
        public List<(string Gamma, long Valore)> PerGamma { get; init; } = new();
    }
}
