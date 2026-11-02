using TestLoom.App.Models;

namespace TestLoom.App.Services;

public class NonConformitaRepository
{
    private static readonly TimeSpan DefaultDeepContentValidationInterval = TimeSpan.FromMinutes(5);

    private readonly DataPaths _paths;
    private readonly LocalNonConformitaIndexStore? _localIndex;
    private readonly LocalIndexRecoveryCoordinator? _indexRecovery;
    private readonly object _gate = new();
    private readonly object _refreshGate = new();
    // Copie complete dell'archivio servite da GetAll(): spia dei test sui percorsi di sola lettura (contatore per istanza).
    private long _chiamateGetAll;
    internal long ChiamateGetAll => Interlocked.Read(ref _chiamateGetAll);
    private readonly TimeSpan _deepContentValidationInterval;
    private Dictionary<string, (DateTime Mtime, NonConformita Nc)> _cache = new();
    private Dictionary<string, string> _hashByPath = new(StringComparer.OrdinalIgnoreCase);
    private List<FileConProblemi> _fileConProblemi = new();
    private List<FileConProblemi> _copieDiConflitto = new();
    private readonly FileProblemToleranceTracker _tolleranzaProblemi = new();
    private DateTime _lastDeepContentValidationUtc = DateTime.MinValue;
    private bool _snapshotCaricato;

    // Allineamento fra cache in memoria e indice SQLite per le interrogazioni filtrate (pagina Anomalie).
    // _gen cresce a ogni mutazione della cache (sotto _gate); _indexGen e' la generazione che l'indice riflette
    // di sicuro. L'indice e' interrogabile SOLO quando coincidono: una scrittura incrementale riuscita
    // (Save/Elimina/RefreshPaths) mantiene l'allineamento, un refresh completo lo ripristina solo se nessun'altra
    // mutazione e' intervenuta mentre il ReconcileFull girava fuori dal lock. In ogni altro caso le query
    // ricadono sul percorso in memoria, sempre corretto.
    private long _gen;
    private long _indexGen = -1;

    public LocalIndexRecoveryCoordinator? IndexRecovery => _indexRecovery;

    /// <summary>Contatore di mutazione della cache: cambia a ogni Save/Elimina/refresh che modifica i dati.
    /// Permette ai chiamanti di riusare risultati derivati (es. elenco assegnatari) finche' non cambia.</summary>
    public long GenerazioneDati { get { lock (_gate) return _gen; } }

    /// <summary>Vero se l'indice SQLite riflette esattamente la cache corrente ed e' sano. Usato dalla pagina
    /// Anomalie per decidere fra interrogazione indicizzata e ripiego in memoria.</summary>
    public bool IndiceFiltriUtilizzabile
    {
        get
        {
            lock (_gate)
                return _localIndex != null && _indexGen == _gen && (_indexRecovery?.IsUsable ?? false)
                    && !WorkflowWorkingCopyScope.Attivo;
        }
    }

    /// <summary>Interroga l'indice per filtro+ordinamento+pagina e restituisce le istanze IN CACHE (sola
    /// lettura: il chiamante deve clonare cio' che espone o puo' modificare). Ritorna false, senza risultato,
    /// quando l'indice non e' utilizzabile, la query fallisce, la cache cambia durante la query o l'indice cita
    /// un percorso assente dalla cache: il chiamante usa allora il percorso in memoria. limit &lt; 0 = tutti i
    /// risultati (totale = numero restituito).</summary>
    public bool TryQueryIndicizzata(AnomalieFiltroNc filtro, AnomalieOrdinamento ordinamento, long offset, int limit,
        out long totale, out IReadOnlyList<NonConformita> risultati)
    {
        totale = 0;
        risultati = Array.Empty<NonConformita>();
        // Le chiavi che dipendono dal Test collegato non hanno colonna nell'indice: la query resta in memoria.
        if (AnomalieOrdinamentoChiaveEstensioni.RichiedeTest(ordinamento.Chiave)) return false;
        long generazione;
        lock (_gate)
        {
            if (_localIndex == null || _indexGen != _gen || WorkflowWorkingCopyScope.Attivo) return false;
            generazione = _gen;
        }
        if (_indexRecovery == null) return false;
        if (!_indexRecovery.TryRead("query NC filtrata", () =>
            {
                var (tot, paths) = _localIndex.QueryPagina(filtro, ordinamento, offset, limit);
                return (Totale: tot, Percorsi: paths);
            }, out var esito))
            return false;

        var elenco = new List<NonConformita>(esito.Percorsi.Count);
        lock (_gate)
        {
            if (_gen != generazione || _indexGen != _gen) return false;
            foreach (var path in esito.Percorsi)
            {
                if (!_cache.TryGetValue(path, out var voce)) return false;
                elenco.Add(voce.Nc);
            }
        }
        totale = esito.Totale < 0 ? elenco.Count : esito.Totale;
        risultati = elenco;
        return true;
    }

    /// <summary>Assegnatari distinti (non vuoti, ordinati) letti dall'indice; false se non utilizzabile.</summary>
    public bool TryAssegnatariDistinti(out IReadOnlyList<string> assegnatari)
    {
        assegnatari = Array.Empty<string>();
        long generazione;
        lock (_gate)
        {
            if (_localIndex == null || _indexGen != _gen || WorkflowWorkingCopyScope.Attivo) return false;
            generazione = _gen;
        }
        if (_indexRecovery == null || !_indexRecovery.TryRead("assegnatari NC", () => _localIndex.AssegnatariDistinti(), out var lista))
            return false;
        lock (_gate)
        {
            if (_gen != generazione || _indexGen != _gen) return false;
        }
        assegnatari = lista;
        return true;
    }

    /// <summary>Scrittura incrementale dell'indice a valle di una mutazione della cache, sotto _gate: mantiene
    /// l'allineamento se c'era e la scrittura riesce, altrimenti lo perde fino al prossimo refresh completo.</summary>
    private void ScriviIndiceIncrementale(string operazione, Action scrittura)
    {
        var allineato = _indexGen == _gen;
        _gen++;
        var ok = _indexRecovery?.TryWrite(operazione, scrittura) ?? false;
        if (allineato && ok) _indexGen = _gen;
    }

    private static bool CacheCambiata(
        Dictionary<string, (DateTime Mtime, NonConformita Nc)> prima,
        Dictionary<string, (DateTime Mtime, NonConformita Nc)> dopo)
    {
        if (prima.Count != dopo.Count) return true;
        foreach (var kv in prima)
            if (!dopo.TryGetValue(kv.Key, out var v) || v.Mtime != kv.Value.Mtime || !ReferenceEquals(v.Nc, kv.Value.Nc)) return true;
        return false;
    }

    public bool HasPersistentSnapshot => _snapshotCaricato && (_localIndex?.HasCompletedFullScan ?? false) && (_indexRecovery?.IsUsable ?? false);

    public int CountAll { get { lock (_gate) return _cache.Count; } }

    /// <summary>Cache/read-model SQLite locale, stesso principio di TestCaseRepository/LocalTestIndexStore
    /// (feedback utente 2026-09-17, volumi 100k): warm-start senza dover ririleggere ogni JSON dalla cartella
    /// condivisa a ogni avvio. localIndex=null (default in produzione via AppState) costruisce automaticamente
    /// il proprio store; i test possono iniettarne uno con localBaseDirectory dedicata.</summary>
    public NonConformitaRepository(DataPaths paths, TimeSpan? deepContentValidationInterval = null, LocalNonConformitaIndexStore? localIndex = null)
    {
        _paths = paths;
        _deepContentValidationInterval = deepContentValidationInterval ?? DefaultDeepContentValidationInterval;
        _localIndex = localIndex ?? new LocalNonConformitaIndexStore(paths.Root);
        _indexRecovery = new LocalIndexRecoveryCoordinator(_localIndex.DatabasePath,
            RicostruisciIndiceDaJson, "nc-index.db (Non Conformita)");
        // File bloccato anche per la quarantena: l'indice parte degradato, senza eccezioni dal costruttore.
        if (_localIndex.IndisponibileMotivo is { } motivoIndisponibile)
            _indexRecovery.InvalidaComeObsoleto(motivoIndisponibile);
        if (_indexRecovery.TryRead("bootstrap NC", () => _localIndex.HasCompletedFullScan, out var completo) && completo &&
            _indexRecovery.TryRead("caricamento NC", () => _localIndex.LoadAll(), out var elementi))
        {
            foreach (var item in elementi)
            {
                _cache[item.Path] = (item.MtimeUtc, item.Nc);
                _hashByPath[item.Path] = item.ContentHash;
            }
            _snapshotCaricato = true;
            // La cache e' stata appena caricata dall'indice stesso: coincidono per costruzione.
            _indexGen = _gen;
        }
        else if (_localIndex.IndisponibileMotivo != null)
        {
            // Indice indisponibile: la cache si riempie dai JSON autorevoli, come nel riallineamento ordinario. Una cartella
            // condivisa non raggiungibile resta un errore del riallineamento, non del costruttore.
            try { RefreshIndex(); }
            catch (IOException ex) { CrashLogService.Registra("Non Conformita: cache non caricata dai JSON (cartella condivisa non raggiungibile)", ex); }
        }
    }

    public IReadOnlyList<FileConProblemi> FileConProblemi { get { lock (_gate) return _fileConProblemi.ToList(); } }

    /// <summary>Copie di conflitto OneDrive/SharePoint per NC (block C, revisione multi-PC 6.4.2): stessa
    /// logica content-driven di TestCaseRepository.CopieDiConflitto, adattata al modello NC (cartella piatta,
    /// niente sotto-cartelle per componente/Gamma, quindi nessuno scenario "spostamento in corso" da gestire qui —
    /// solo la classica copia di conflitto OneDrive/SharePoint con nome non canonico).</summary>
    public IReadOnlyList<FileConProblemi> CopieDiConflitto { get { lock (_gate) return _copieDiConflitto.ToList(); } }

    /// <param name="onProgress">Callback opzionale (elaborati, totale) invocato durante la rilettura parallela
    /// dei JSON: stesso contratto di TestCaseRepository.RefreshIndex, pura osservazione senza effetto sull'esito.</param>
    public void RefreshIndex(Action<int, int>? onProgress = null)
    {
        lock (_refreshGate)
        {
            // Indice indisponibile per un file bloccato: si ritenta l'apertura (senza quarantena). In ogni caso la cache viene
            // riletta dai JSON autorevoli: le letture NC dipendono dalla cache, non dall'indice.
            if (_localIndex.IndisponibileMotivo != null) RiapriIndiceDopoIndisponibilita();
            try
            {
                RefreshIndexCore(ricostruzione: false, onProgress);
            }
            catch (Exception ex) when (LocalIndexFaultClassifier.IndisponibileAmbiente(ex))
            {
                // File bloccato: il riallineamento non avviene adesso. Le letture restano sui JSON; la prossima riconciliazione
                // riuscita, o il "Riprova" dell'utente, riabilita l'indice. Nessuna eccezione verso il chiamante.
                CrashLogService.Registra("Non Conformita: riallineamento dell'indice non riuscito (letture dai JSON)", ex);
                _indexRecovery?.InvalidaComeObsoleto(ex.Message);
            }
        }
    }

    /// <summary>Ritenta l'apertura di uno store reso indisponibile da un file bloccato. Mai quarantena qui; un guasto
    /// persistente lascia l'indice degradato e non lancia verso il chiamante.</summary>
    private bool RiapriIndiceDopoIndisponibilita()
    {
        try
        {
            _localIndex.ReinizializzaDopoQuarantena();
            return true;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Non Conformita: riapertura dell'indice non riuscita (letture dai JSON)", ex);
            _indexRecovery?.InvalidaComeObsoleto(ex.Message);
            return false;
        }
    }

    private LocalIndexRebuildOutcome RicostruisciIndiceDaJson()
    {
        if (_localIndex == null) return LocalIndexRebuildOutcome.Incompleta("Indice locale NC assente.");
        _localIndex.ReinizializzaDopoQuarantena();
        lock (_refreshGate) return RefreshIndexCore(ricostruzione: true);
    }

    private LocalIndexRebuildOutcome RefreshIndexCore(bool ricostruzione, Action<int, int>? onProgress = null)
    {
        if (!Directory.Exists(_paths.Root))
        {
            if (ricostruzione) return LocalIndexRebuildOutcome.Incompleta("Cartella condivisa NC non raggiungibile.");
            throw new IOException("Cartella condivisa NC non raggiungibile: fotografia locale conservata.");
        }
        var nowUtc = DateTime.UtcNow;
        var deepValidate = ricostruzione || nowUtc - _lastDeepContentValidationUtc >= _deepContentValidationInterval;
        if (!Directory.Exists(_paths.NonConformitaDir))
        {
            lock (_gate)
            {
                _cache = new();
                _gen++;
                _hashByPath = new(StringComparer.OrdinalIgnoreCase);
                _fileConProblemi = _tolleranzaProblemi.AggiornaCompleto(Array.Empty<FileConProblemi>());
                if (deepValidate) _lastDeepContentValidationUtc = nowUtc;
            }
            return LocalIndexRebuildOutcome.Riuscita(0);
        }

        Dictionary<string, (DateTime Mtime, NonConformita Nc)> fotografia;
        Dictionary<string, string> fotografiaHash;
        lock (_gate)
        {
            fotografia = new(_cache);
            fotografiaHash = _cache.ToDictionary(
                kv => kv.Key,
                kv => _hashByPath.TryGetValue(kv.Key, out var hash) ? hash : EntityStateFingerprint.Compute(kv.Value.Nc),
                StringComparer.OrdinalIgnoreCase);
        }

        var nuovaCache = new Dictionary<string, (DateTime Mtime, NonConformita Nc)>(fotografia);
        var problemi = new List<FileConProblemi>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lunghezze = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var daRileggere = new List<(string Path, DateTime Mtime)>();
        foreach (var file in new DirectoryInfo(_paths.NonConformitaDir).EnumerateFiles("nc_*.json"))
        {
            var path = file.FullName;
            seen.Add(path);
            var mtime = file.LastWriteTimeUtc;
            lunghezze[path] = file.Length;
            if (!deepValidate && fotografia.TryGetValue(path, out var cached) && cached.Mtime == mtime) continue;
            // mtime e lunghezza non costituiscono identità del contenuto su OneDrive. Il refresh ordinario
            // resta economico, ma almeno ogni pochi minuti una validazione profonda rilegge tutti i JSON.
            daRileggere.Add((path, mtime));
        }

        // Stesso schema di TestCaseRepository.RefreshIndex: la lettura/deserializzazione di ogni file è
        // indipendente dalle altre, quindi si parallelizza per non pagare in sequenza una latenza per-file
        // (OneDrive, antivirus, disco lento) che con NC numerose (feedback utente 2026-09-17, volumi 100k)
        // altrimenti scala linearmente con il numero di file invece che con il tempo del file più lento.
        if (daRileggere.Count > 0)
        {
            onProgress?.Invoke(0, daRileggere.Count);

            var risultati = new (NonConformita? Nc, string? Errore)[daRileggere.Count];
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) };
            var elaboratiCount = 0;
            var totaleDaRileggere = daRileggere.Count;
            var reportOgni = Math.Max(1, totaleDaRileggere / 200);
            Parallel.For(0, daRileggere.Count, parallelOptions, i =>
            {
                risultati[i] = (JsonIo.TryRead<NonConformita>(daRileggere[i].Path, out var errore), errore);
                if (onProgress == null) return;
                var fatti = Interlocked.Increment(ref elaboratiCount);
                if (fatti % reportOgni == 0 || fatti == totaleDaRileggere) onProgress(fatti, totaleDaRileggere);
            });

            for (var i = 0; i < daRileggere.Count; i++)
            {
                var (path, mtime) = daRileggere[i];
                var (nc, errore) = risultati[i];
                if (nc == null)
                {
                    if (WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, path))
                    {
                        // Delete committed: il tombstone resta fisicamente per rendere deterministica la sync,
                        // ma il record non deve più comparire nel read-model.
                        nuovaCache.Remove(path);
                        continue;
                    }

                    // Pending/invalid/corrotto non equivale mai a delete: conserva la last-known-good se presente.
                    if (!fotografia.ContainsKey(path)) nuovaCache.Remove(path);
                    problemi.Add(new FileConProblemi { Percorso = path, Errore = errore ?? "sconosciuto", RilevatoIl = DateTime.Now });
                    continue;
                }
                nuovaCache[path] = (mtime, nc);
            }
        }
        foreach (var stale in nuovaCache.Keys.Where(p => !seen.Contains(p)).ToList()) nuovaCache.Remove(stale);

        List<LocalIndexWriteEntryNc>? snapshotPersistente = null;
        long generazioneSnapshot;
        lock (_gate)
        {
            foreach (var kv in _cache)
            {
                var currentHash = _hashByPath.TryGetValue(kv.Key, out var hash)
                    ? hash
                    : EntityStateFingerprint.Compute(kv.Value.Nc);
                if (!fotografia.TryGetValue(kv.Key, out var prima) ||
                    prima.Mtime != kv.Value.Mtime ||
                    !fotografiaHash.TryGetValue(kv.Key, out var openingHash) ||
                    !openingHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
                    nuovaCache[kv.Key] = kv.Value;
            }
            foreach (var eliminato in fotografia.Keys.Where(k => !_cache.ContainsKey(k))) nuovaCache.Remove(eliminato);

            // Gli UID duplicati sono un fatto strutturale/deterministico (due file validi con lo stesso UID),
            // non un errore di lettura transitorio: niente tolleranza, visibili subito. A differenza di prima
            // (block C, revisione multi-PC 6.4.2), una copia di conflitto OneDrive/SharePoint riconoscibile
            // (nome non canonico, stesso Uid del canonico) NON diventa più un "UID duplicato" allarmante per
            // ogni refresh: esclusa dalla cache come per TestCaseRepository, resta segnalata in CopieDiConflitto
            // con l'esito del confronto di contenuto, recuperabile, mai persa.
            ClassificaDuplicatiUidEEscludiCopieDiConflitto(nuovaCache, _paths, out var duplicati, out var copieDiConflittoOra);

            var cambiata = CacheCambiata(_cache, nuovaCache);
            // Indice gia' allineato e cache invariata dal refresh precedente: l'indice contiene gia' esattamente
            // questi dati (garantito dagli upsert incrementali sotto lock), quindi il ReconcileFull periodico
            // (secondi a 100k) si salta: nessuna scrittura, nessuna finestra in cui l'indice e' escluso, nessuna
            // race con Save concorrenti. La ricostruzione (dopo quarantena) riconcilia sempre.
            var saltaReconcile = !ricostruzione && !cambiata && _localIndex != null && _indexGen == _gen
                && (_indexRecovery?.IsUsable ?? false);
            if (cambiata) _gen++;
            generazioneSnapshot = _gen;
            _cache = nuovaCache;
            _hashByPath = CalcolaHashParallelo(nuovaCache);
            _copieDiConflitto = copieDiConflittoOra;
            _fileConProblemi = _tolleranzaProblemi.AggiornaCompleto(problemi).Concat(duplicati).ToList();
            if (deepValidate) _lastDeepContentValidationUtc = nowUtc;

            // Da qui fino al termine del ReconcileFull l'indice puo' essere riscritto con lo snapshot appena
            // catturato: un Save/Elimina concorrente porterebbe cache e indice a divergere (il reconcile
            // riscrive valori vecchi o ripristina una riga eliminata). L'indice quindi NON e' interrogabile
            // finche' AllineaIndice non conferma che nessuna mutazione e' intervenuta nel frattempo.
            if (_localIndex != null && !saltaReconcile) _indexGen = -1;
            if (_localIndex != null && !saltaReconcile)
                snapshotPersistente = _cache.Select(kv => new LocalIndexWriteEntryNc(
                    kv.Key, kv.Value.Mtime, lunghezze.GetValueOrDefault(kv.Key), kv.Value.Nc,
                    _hashByPath.GetValueOrDefault(kv.Key))).ToList();
        }

        // Fuori dal lock _gate (I/O SQLite non deve bloccare Save/GetAll concorrenti): stessa cache locale
        // ricostruibile di TestCaseRepository, mai source of truth, rende il prossimo avvio indipendente dal
        // numero di file JSON invece di dover ririleggere tutto da zero (feedback utente 2026-09-17, 100k NC).
        if (snapshotPersistente != null)
        {
            PrimaDelReconcileTest?.Invoke();
            if (ricostruzione)
            {
                if (problemi.Count > 0)
                    return LocalIndexRebuildOutcome.Incompleta($"{problemi.Count} file NC non verificabili.");
                _localIndex!.ReconcileFull(snapshotPersistente, DateTime.UtcNow);
                AllineaIndice(generazioneSnapshot);
            }
            else if (_indexRecovery != null)
            {
                bool riuscita;
                if (_indexRecovery.Salute == LocalIndexHealth.Unavailable)
                {
                    // Indice degradato (es. un upsert incrementale e' fallito/scaduto): TryWrite rifiuterebbe ogni
                    // scrittura e l'indice resterebbe fuori servizio fino al riavvio. La riconciliazione completa e'
                    // proprio il rimedio previsto: se riesce l'indice torna sano (come TestCaseRepository).
                    try
                    {
                        _localIndex!.ReconcileFull(snapshotPersistente, DateTime.UtcNow);
                        _indexRecovery.MarcaSano();
                        riuscita = true;
                    }
                    catch (Exception ex)
                    {
                        CrashLogService.Registra("Indice locale NC: riconciliazione di ripristino fallita", ex);
                        if (LocalIndexFaultClassifier.Classify(ex) == LocalIndexFaultKind.Corruption)
                            _ = _indexRecovery.RichiediRecupero("riconciliazione di ripristino: corruzione");
                        riuscita = false;
                    }
                }
                else
                    riuscita = _indexRecovery.TryWrite("full reconciliation NC",
                        () => _localIndex!.ReconcileFull(snapshotPersistente, DateTime.UtcNow));
                if (riuscita) AllineaIndice(generazioneSnapshot);
            }
        }
        return LocalIndexRebuildOutcome.Riuscita(snapshotPersistente?.Count ?? 0);
    }

    /// <summary>Dopo un ReconcileFull riuscito: l'indice riflette lo snapshot di <paramref name="generazione"/>.
    /// Se nel frattempo la cache e' cambiata (Save concorrente, altro refresh) l'allineamento NON si afferma.</summary>
    internal Action? PrimaDelReconcileTest;

    private void AllineaIndice(long generazione)
    {
        lock (_gate)
        {
            if (_gen == generazione) _indexGen = generazione;
        }
    }

    /// <summary>Conteggio scalare sullo stesso snapshot/overlay di GetAll, senza clonare l'archivio.</summary>
    public long CountByState(StatoNonConformita stato)
    {
        lock (_gate)
            return WorkflowWorkingCopyScope.CountNonConformitaByState(_cache.Values.Select(v => v.Nc), stato);
    }
    public IReadOnlyList<NonConformita> GetAll()
    {
        Interlocked.Increment(ref _chiamateGetAll);
        List<NonConformita> authoritative;
        lock (_gate) authoritative = _cache.Values.Select(v => v.Nc).ToList();
        return WorkflowWorkingCopyScope.ApplyNonConformita(authoritative);
    }

    /// <summary>Come GetAll(), ma senza clonare ogni record: a 100k+ Non Conformità clonare l'intero archivio
    /// solo per ordinare/filtrare/contare (senza mai esporre le righe al di fuori della pagina visibile)
    /// dominava il tempo di apertura della pagina Anomalie/CR (Addendum prestazioni 2026-09-20, feedback utente
    /// "navigazione fluida, 700ms su qualunque pagina a piena scala"). Gli oggetti ritornati sono le istanze
    /// effettive in cache: il chiamante non deve MAI mutarle né trattenerle oltre l'uso sincrono immediato — se
    /// anche solo una riga deve poter essere modificata o salvata, va clonata esplicitamente con Clona() prima
    /// di uscire dal metodo chiamante. GetAll() resta la scelta di default; usare questo solo nei punti caldi
    /// verificati.</summary>
    public IReadOnlyList<NonConformita> PeekAllReadOnly()
    {
        List<NonConformita> authoritative;
        lock (_gate) authoritative = _cache.Values.Select(v => v.Nc).ToList();
        return WorkflowWorkingCopyScope.PeekNonConformita(authoritative);
    }

    // Indice in memoria dei candidati di associazione (solo dati NC): codice (TestCaseId, ProveCollegate.TestId) e Uid
    // (TestUid, ProveCollegate.TestUid) -> percorsi. Invalidato da _gen (ogni mutazione della cache lo incrementa). Non
    // contiene oggetti NC ma percorsi, cosi' ogni lettura prende l'istanza corrente in cache.
    private long _candidatiGen = -1;
    private Dictionary<string, List<string>> _candidatiPerCodice = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<Guid, List<string>> _candidatiPerUid = new();

    /// <summary>Sovrainsieme esatto delle NC che possono risultare associate ai Test dati (codice o Uid), per
    /// <see cref="AnomaliaWorkflowService.IndicizzaPerTest"/>: l'associazione resta validata sui Test correnti dal
    /// chiamante. Istanze in cache, sola lettura come PeekAllReadOnly. Con copie di lavoro attive ripiega sull'archivio intero.</summary>
    public IReadOnlyList<NonConformita> CandidatePerTest(IEnumerable<TestCase> tests)
    {
        if (WorkflowWorkingCopyScope.Attivo) return PeekAllReadOnly();
        lock (_gate)
        {
            if (_candidatiGen != _gen) RicostruisciCandidati();
            var percorsi = new HashSet<string>(StringComparer.Ordinal);
            foreach (var test in tests)
            {
                if (_candidatiPerCodice.TryGetValue(test.Id, out var perCodice)) percorsi.UnionWith(perCodice);
                if (_candidatiPerUid.TryGetValue(test.Uid, out var perUid)) percorsi.UnionWith(perUid);
            }
            var risultato = new List<NonConformita>(percorsi.Count);
            foreach (var percorso in percorsi)
                if (_cache.TryGetValue(percorso, out var voce)) risultato.Add(voce.Nc);
            return risultato;
        }
    }

    /// <summary>Ricostruisce l'indice dei candidati dalla cache corrente. Chiamare solo sotto _gate.</summary>
    private void RicostruisciCandidati()
    {
        _candidatiPerCodice = new(StringComparer.OrdinalIgnoreCase);
        _candidatiPerUid = new();
        foreach (var (percorso, voce) in _cache)
        {
            var nc = voce.Nc;
            if (!string.IsNullOrWhiteSpace(nc.TestCaseId)) AggiungiCandidato(_candidatiPerCodice, nc.TestCaseId, percorso);
            if (nc.TestUid is { } uid) AggiungiCandidato(_candidatiPerUid, uid, percorso);
            foreach (var prova in nc.ProveCollegate)
            {
                if (!string.IsNullOrWhiteSpace(prova.TestId)) AggiungiCandidato(_candidatiPerCodice, prova.TestId, percorso);
                if (prova.TestUid is { } provaUid) AggiungiCandidato(_candidatiPerUid, provaUid, percorso);
            }
        }
        _candidatiGen = _gen;
    }

    private static void AggiungiCandidato<TChiave>(Dictionary<TChiave, List<string>> indice, TChiave chiave, string percorso)
        where TChiave : notnull
    {
        if (!indice.TryGetValue(chiave, out var percorsi)) indice[chiave] = percorsi = new List<string>(1);
        percorsi.Add(percorso);
    }

    /// <summary>Solo le NC che referenziano uno dei Test passati (TestCaseId o ProveCollegate), filtrate
    /// PRIMA della copia isolata di ogni record — a differenza di GetAll().Where(...), non clona l'intero
    /// archivio per poi scartarne la quasi totalità (feedback utente 2026-09-17, volumi 100k: usato dalle
    /// pagine che devono solo annotare le poche righe visibili a schermo, non l'intero elenco NC).</summary>
    public IReadOnlyList<NonConformita> GetByTestIds(IReadOnlyCollection<string> testIds)
    {
        if (testIds.Count == 0) return Array.Empty<NonConformita>();
        var richiesti = new HashSet<string>(testIds, StringComparer.OrdinalIgnoreCase);
        var resolver = TestResolver;
        var test = resolver == null ? new List<TestCase>() : richiesti.Select(resolver).OfType<TestCase>().ToList();
        var perUid = test.ToDictionary(t => t.Uid);
        var perCodice = test.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        List<NonConformita>? candidate = null;
        long generazione;
        lock (_gate) generazione = _gen;
        if (IndiceFiltriUtilizzabile && _indexRecovery != null &&
            _indexRecovery.TryRead("collegamenti NC Test", () => _localIndex!.QueryTestLinkPaths(richiesti, perUid.Keys.ToList()), out var percorsi))
        {
            lock (_gate)
                if (_gen == generazione && _indexGen == _gen && percorsi.All(_cache.ContainsKey))
                    candidate = percorsi.Select(p => _cache[p].Nc).ToList();
        }
        if (candidate == null)
            lock (_gate) candidate = _cache.Values.Select(v => v.Nc).ToList();
        // Overlay prima del filtro; il percorso SQLite e disabilitato durante lo scope.
        if (WorkflowWorkingCopyScope.Attivo) candidate = WorkflowWorkingCopyScope.ApplyNonConformita(candidate).ToList();
        bool Matches(NonConformita n)
        {
            if (resolver == null) return richiesti.Contains(n.TestCaseId) || n.ProveCollegate.Any(p => richiesti.Contains(p.TestId));
            if (n.TestUid.HasValue && perUid.ContainsKey(n.TestUid.Value)) return true;
            foreach (var proof in n.ProveCollegate)
            {
                var effective = AnomaliaWorkflowService.ProvaConIdentita(n, proof);
                var current = effective.TestUid.HasValue ? perUid.GetValueOrDefault(effective.TestUid.Value) : perCodice.GetValueOrDefault(effective.TestId);
                if (current != null && AnomaliaWorkflowService.ProvaAppartieneAlTest(current, effective)) return true;
            }
            return false;
        }
        var corrispondenti = candidate.Where(Matches).ToList();
        return WorkflowWorkingCopyScope.ApplyNonConformita(corrispondenti);
    }

    /// <summary>Risolve un codice test nel Test persistito (iniettato da AppState). Usato solo per validare
    /// l'aggancio NC -> Test per codice; assente (null) = comportamento storico per sola stringa.</summary>
    public Func<string, TestCase?>? TestResolver { get; set; }

    /// <summary>Tutti i codici test referenziati da una NC (TestCaseId e ProveCollegate), anche di NC chiuse.
    /// Serve a non riassegnare mai un codice ancora referenziato (IdGenerator).</summary>
    public IReadOnlyCollection<string> GetAllReferencedTestIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            foreach (var n in _cache.Values.Select(v => v.Nc))
            {
                if (!string.IsNullOrWhiteSpace(n.TestCaseId)) ids.Add(n.TestCaseId);
                foreach (var p in n.ProveCollegate) if (!string.IsNullOrWhiteSpace(p.TestId)) ids.Add(p.TestId);
            }
        return ids;
    }

    public NonConformita? GetById(string id)
    {
        NonConformita? found;
        lock (_gate) found = _cache.Values.Select(v => v.Nc).FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
        return WorkflowWorkingCopyScope.ApplyNonConformita(found);
    }

    public NonConformita? GetByUid(Guid uid)
    {
        NonConformita? found;
        lock (_gate) found = _cache.Values.Select(v => v.Nc).FirstOrDefault(n => n.Uid == uid);
        return WorkflowWorkingCopyScope.ApplyNonConformita(found);
    }

    public string NextId()
    {
        lock (_gate)
        {
            // Considera anche i tombstone: un ID già assegnato non viene riciclato solo perché il record è
            // stato cancellato. Oltre a evitare ambiguità di recovery mantiene monotona la numerazione.
            var maxCache = _cache.Values.Select(v => ParseId(v.Nc.Id)).DefaultIfEmpty(0).Max();
            var maxDisk = Directory.Exists(_paths.NonConformitaDir)
                ? Directory.EnumerateFiles(_paths.NonConformitaDir, "nc_*.json", SearchOption.TopDirectoryOnly)
                    .Select(p => ParseId(Path.GetFileNameWithoutExtension(p).Replace("nc_", "", StringComparison.OrdinalIgnoreCase)))
                    .DefaultIfEmpty(0).Max()
                : 0;
            return $"NC-{(Math.Max(maxCache, maxDisk) + 1):D4}";
        }
    }

    private static int ParseId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return 0;
        var value = id.StartsWith("NC-", StringComparison.OrdinalIgnoreCase) ? id[3..] : id;
        return int.TryParse(value, out var n) ? n : 0;
    }

    /// <summary>Journal per-device dei commit (R9). Null nei test/strumenti che non lo configurano: nessun cambio di comportamento.</summary>
    public DomainRecordJournalService? RecordJournal { get; set; }

    public void Save(NonConformita nc, long? revisioneAttesa = null)
    {
        WorkspaceWritePolicy.EnsureCanWrite(_paths, "Salvataggio Non Conformita");
        if (nc.TestUid.HasValue || nc.ProveCollegate.Any(p => p.TestUid.HasValue))
        {
            CompatibilitaVersioneService.EnsureTestUidSchema(_paths);
            nc.SchemaVersion = Math.Max(nc.SchemaVersion, 3);
        }
        lock (_gate)
        {
            var path = _paths.NonConformitaFile(nc.Id);
            using var commitLock = DomainRecordCommitLock.Acquire("Non Conformità", nc.Id, path);
            VisibleConflictWriteGuard.EnsureNoVisibleFork(path, nc.Uid, null,
                RecordJournal?.CopiaChiusaPer(DomainRecordJournalService.NonConformitaDomain, nc.Uid));
            RecordJournal?.EnsureNotBlocked(DomainRecordJournalService.NonConformitaDomain, nc.Uid, nc.Id);
            NonConformita? corrente = null;
            if (File.Exists(path))
            {
                if (WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, path))
                    throw new IOException($"L'ID {nc.Id} è già stato usato da una Non Conformita eliminata. Genera un nuovo ID; il tombstone non viene sovrascritto.");

                corrente = JsonIo.TryRead<NonConformita>(path, out var errore);
                if (corrente == null)
                    throw new InvalidDataException($"Il file dell'anomalia {nc.Id} non e leggibile e non verra sovrascritto: {errore}");
                if (!revisioneAttesa.HasValue)
                    throw new IOException($"Esiste gia un'anomalia con ID {nc.Id}; ricarica i dati e riprova.");
                if (corrente.Uid != nc.Uid)
                    throw new IOException($"L'ID {nc.Id} appartiene a una diversa Non Conformita. Nessun file e stato sovrascritto.");
                if (corrente.RevisioneDati != revisioneAttesa.Value)
                    throw new ConflittoRevisioneException(nc.Id, revisioneAttesa.Value, corrente.RevisioneDati);
                if (!_hashByPath.TryGetValue(path, out var expectedHash))
                    throw new IOException($"Fotografia di sicurezza dell'anomalia {nc.Id} non disponibile. Ricarica i dati prima di salvare.");
                var currentHash = EntityStateFingerprint.Compute(corrente);
                if (!currentHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new ConflittoRevisioneException(nc.Id, revisioneAttesa.Value, corrente.RevisioneDati, true);
            }

            else if (revisioneAttesa.HasValue)
            {
                // Un file sparito nel frattempo non autorizza a ricreare una versione obsoleta.
                throw new ConflittoRevisioneException(nc.Id, revisioneAttesa.Value, -1);
            }

            var revisionePrecedente = nc.RevisioneDati;
            nc.RevisioneDati = Math.Max(nc.RevisioneDati, revisioneAttesa ?? 0) + 1;
            DomainRecordJournalService.PendingCommit? journalPending = null;
            try
            {
                journalPending = RecordJournal?.Prepare(DomainRecordJournalService.NonConformitaDomain, nc.Uid, nc.Id,
                    corrente == null ? RecordJournalOperation.Create : RecordJournalOperation.Save,
                    corrente, corrente?.RevisioneDati, nc, nc.RevisioneDati, nc.Changelog.LastOrDefault()?.Operatore);
                JsonIo.WriteAtomic(path, nc, overwrite: revisioneAttesa.HasValue);
            }
            catch
            {
                nc.RevisioneDati = revisionePrecedente;
                RecordJournal?.Cancel(journalPending);
                throw;
            }
            if (journalPending != null) RecordJournal!.Publish(journalPending);

            var snapshot = nc.Clona();
            var mtime = File.GetLastWriteTimeUtc(path);
            var savedHash = EntityStateFingerprint.Compute(snapshot);
            _cache[path] = (mtime, snapshot);
            _hashByPath[path] = savedHash;
            ScriviIndiceIncrementale("upsert NC",
                () => _localIndex?.Upsert(path, mtime, new FileInfo(path).Length, snapshot, savedHash));
            DiagnosticTraceService.Registra("NonConformita.Save", $"{nc.Id} revisione={nc.RevisioneDati}");
        }
    }

    public string FilePathOf(NonConformita nc) => _paths.NonConformitaFile(nc.Id);

    /// <summary>Riallinea la cache ai soli file indicati dopo che il file su disco e stato modificato fuori dal
    /// repository (tipico: rollback di una Unit of Work che ripristina il file ma non la cache aggiornata dal
    /// Save). File assente o tombstone: la voce esce dalla cache; illeggibile: la last-known-good resta (mai una
    /// cancellazione implicita). Piu economico e deterministico di un RefreshIndex completo.</summary>
    public void RefreshPaths(IEnumerable<string> changedPaths)
    {
        lock (_gate)
        {
            var toccati = changedPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var path in toccati)
            {
                if (!File.Exists(path) || WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, path))
                {
                    _cache.Remove(path); _hashByPath.Remove(path);
                    ScriviIndiceIncrementale("remove NC (refresh)", () => _localIndex?.Remove(path));
                    continue;
                }
                var nc = JsonIo.TryRead<NonConformita>(path, out _);
                if (nc == null) continue;
                var mtime = File.GetLastWriteTimeUtc(path);
                var hash = EntityStateFingerprint.Compute(nc);
                _cache[path] = (mtime, nc);
                _hashByPath[path] = hash;
                ScriviIndiceIncrementale("refresh NC",
                    () => _localIndex?.Upsert(path, mtime, new FileInfo(path).Length, nc, hash));
            }
            // Le copie di conflitto (stesso Uid, nome non canonico) escono dalla cache come nella riconciliazione
            // completa: non devono restare ne' in memoria ne' nell'indice, anche se la Upsert sopra le ha gia' scritte.
            ClassificaDuplicatiUidEEscludiCopieDiConflitto(_cache, _paths, out _, out var copieOra);
            _copieDiConflitto = copieOra;
            foreach (var path in toccati.Where(p => !_cache.ContainsKey(p)))
            {
                _hashByPath.Remove(path);
                ScriviIndiceIncrementale("rimuovi copia di conflitto NC (refresh)", () => _localIndex?.Remove(path));
            }
        }
    }

    public void Elimina(NonConformita nc, string? operatore = null)
    {
        WorkspaceWritePolicy.EnsureCanWrite(_paths, "Eliminazione Non Conformita");
        lock (_gate)
        {
            var path = _paths.NonConformitaFile(nc.Id);
            using var commitLock = DomainRecordCommitLock.Acquire("Non Conformità", nc.Id, path);
            VisibleConflictWriteGuard.EnsureNoVisibleFork(path, nc.Uid, null,
                RecordJournal?.CopiaChiusaPer(DomainRecordJournalService.NonConformitaDomain, nc.Uid));
            if (File.Exists(path))
            {
                var corrente = JsonIo.TryRead<NonConformita>(path, out var errore)
                    ?? throw new InvalidDataException($"Il file dell'anomalia {nc.Id} non e leggibile e non verra eliminato: {errore}");
                if (corrente.Uid != nc.Uid)
                    throw new IOException($"Il file {nc.Id} appartiene a una diversa Non Conformita. Eliminazione bloccata.");
                if (corrente.RevisioneDati != nc.RevisioneDati)
                    throw new ConflittoRevisioneException(nc.Id, nc.RevisioneDati, corrente.RevisioneDati);
                if (!_hashByPath.TryGetValue(path, out var expectedHash))
                    throw new IOException($"Fotografia di sicurezza dell'anomalia {nc.Id} non disponibile. Ricarica i dati prima di eliminarla.");
                var currentHash = EntityStateFingerprint.Compute(corrente);
                if (!currentHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new ConflittoRevisioneException(nc.Id, nc.RevisioneDati, corrente.RevisioneDati, true);
                RecordJournal?.EnsureNotBlocked(DomainRecordJournalService.NonConformitaDomain, nc.Uid, nc.Id);

                DomainRecordJournalService.PendingCommit? journalPending = null;
                try
                {
                    journalPending = RecordJournal?.Prepare(DomainRecordJournalService.NonConformitaDomain, corrente.Uid, corrente.Id,
                        RecordJournalOperation.Delete, corrente, corrente.RevisioneDati, null, null, operatore);
                    DomainRecoveryService.ArchiveAndDelete(
                        _paths, path, DomainRecoveryCategory.NonConformita, operatore);
                }
                catch
                {
                    RecordJournal?.Cancel(journalPending);
                    throw;
                }
                if (journalPending != null) RecordJournal!.Publish(journalPending);
            }
            _cache.Remove(path);
            _hashByPath.Remove(path);
            ScriviIndiceIncrementale("remove NC", () => _localIndex?.Remove(path));
            DiagnosticTraceService.Registra("NonConformita.Elimina", nc.Id);
        }
    }

    /// <summary>Block C (revisione multi-PC 6.4.2): stessa idea content-driven di
    /// TestCaseRepository.ClassificaDuplicatiEEscludiCopieDiConflitto, adattata al modello NC — qui il
    /// raggruppamento è per Uid (identità interna, mai riciclato per due NC realmente distinte, a differenza
    /// dell'Id "NC-0001" testuale di TestCase che può collidere fra postazioni offline) e la cartella NC è
    /// piatta (nessuna sotto-cartella componente/Gamma, quindi nessuno scenario "spostamento in corso").
    ///
    /// In un gruppo che condivide lo stesso Uid, se ESATTAMENTE UNO dei file ha il nome canonico
    /// ("nc_&lt;Id&gt;.json" per l'Id letto dal proprio contenuto), gli altri sono copie di conflitto OneDrive/
    /// SharePoint: escluse dalla cache (non diventano una seconda NC indirizzabile) e segnalate una volta in
    /// <see cref="CopieDiConflitto"/> con l'esito del confronto di contenuto. Un gruppo senza un canonico
    /// univoco (0 o due file "canonici" — improbabile con Id testuali diversi per lo stesso Uid, ma possibile
    /// con dati corrotti/importati) resta un vero UID duplicato ambiguo, comportamento preesistente invariato.</summary>
    /// <summary>Fingerprint di tutte le NC in parallelo: calcolo puro su oggetti indipendenti, stesso risultato della
    /// versione sequenziale (che a ogni giro di refresh serializzava l'intero archivio).</summary>
    private static Dictionary<string, string> CalcolaHashParallelo(Dictionary<string, (DateTime Mtime, NonConformita Nc)> cache)
    {
        var chiavi = cache.Keys.ToArray();
        var hash = new string[chiavi.Length];
        var opzioni = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) };
        Parallel.For(0, chiavi.Length, opzioni, i => hash[i] = EntityStateFingerprint.Compute(cache[chiavi[i]].Nc));
        var risultato = new Dictionary<string, string>(chiavi.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < chiavi.Length; i++) risultato[chiavi[i]] = hash[i];
        return risultato;
    }

    private static void ClassificaDuplicatiUidEEscludiCopieDiConflitto(
        Dictionary<string, (DateTime Mtime, NonConformita Nc)> cache,
        DataPaths paths,
        out List<FileConProblemi> duplicatiVeri,
        out List<FileConProblemi> copieDiConflitto)
    {
        duplicatiVeri = new List<FileConProblemi>();
        copieDiConflitto = new List<FileConProblemi>();

        foreach (var gruppo in cache.GroupBy(kv => kv.Value.Nc.Uid).Where(g => g.Key != Guid.Empty && g.Count() > 1).ToList())
        {
            var percorsi = gruppo.Select(kv => kv.Key).ToList();
            var canonici = percorsi
                .Where(p => !string.IsNullOrWhiteSpace(cache[p].Nc.Id) &&
                            p.Equals(paths.NonConformitaFile(cache[p].Nc.Id), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (canonici.Count != 1)
            {
                duplicatiVeri.AddRange(percorsi.Select(p => new FileConProblemi
                {
                    Percorso = p,
                    Errore = $"UID Non Conformita duplicato ({gruppo.Key:N}): possibile copia di conflitto OneDrive. Nessuna versione deve essere sovrascritta finche il conflitto non e risolto.",
                    RilevatoIl = DateTime.Now
                }));
                continue;
            }

            var pathCanonico = canonici[0];
            var canonico = cache[pathCanonico].Nc;
            var hashCanonico = EntityStateFingerprint.Compute(canonico);
            foreach (var pathCopia in percorsi.Where(p => !p.Equals(pathCanonico, StringComparison.OrdinalIgnoreCase)))
            {
                var identico = EntityStateFingerprint.Compute(cache[pathCopia].Nc).Equals(hashCanonico, StringComparison.OrdinalIgnoreCase);
                copieDiConflitto.Add(new FileConProblemi
                {
                    Percorso = pathCopia,
                    Errore = identico
                        ? $"Copia di conflitto OneDrive/SharePoint della stessa Non Conformita ({canonico.Id}), contenuto identico all'originale ({Path.GetFileName(pathCanonico)}). Ignorabile: il file non è stato toccato."
                        : $"Copia di conflitto OneDrive/SharePoint della stessa Non Conformita ({canonico.Id}), CONTENUTO DIVERSO dall'originale ({Path.GetFileName(pathCanonico)}). Verifica manualmente prima di ignorarla: nessun dato è stato perso, il file resta sul disco.",
                    RilevatoIl = DateTime.Now,
                    RichiedeAttenzione = !identico
                });
                cache.Remove(pathCopia);
            }
        }
    }
}

