using TestLoom.App.Models;

namespace TestLoom.App.Services;

public sealed class SegnalazioneRepository
{
    private static readonly TimeSpan DefaultDeepContentValidationInterval = TimeSpan.FromMinutes(5);

    private readonly DataPaths _paths;
    private readonly object _refreshGate = new();
    private readonly TimeSpan _deepContentValidationInterval;
    private Dictionary<string, (DateTime Mtime, SegnalazioneDaClassificare Item)> _cache = new();
    private Dictionary<string, string> _hashByPath = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<Guid, string> _uniquePathByUid = new();
    private HashSet<Guid> _duplicateUids = new();
    private List<FileConProblemi> _fileConProblemi = new();
    private List<FileConProblemi> _copieDiConflitto = new();
    private readonly FileProblemToleranceTracker _tolleranzaProblemi = new();
    private readonly object _gate = new();
    private DateTime _lastDeepContentValidationUtc = DateTime.MinValue;

    public SegnalazioneRepository(DataPaths paths, TimeSpan? deepContentValidationInterval = null)
    {
        _paths = paths;
        _deepContentValidationInterval = deepContentValidationInterval ?? DefaultDeepContentValidationInterval;
    }

    public IReadOnlyList<FileConProblemi> FileConProblemi { get { lock (_gate) return _fileConProblemi.ToList(); } }

    /// <summary>Copie di conflitto OneDrive/SharePoint per Segnalazioni (block C, revisione multi-PC 6.4.2):
    /// stessa logica content-driven delle altre entità. Qui il nome canonico è derivato direttamente dall'Uid
    /// (DataPaths.SegnalazioneFile), non da un Id testuale separato: un vero UID duplicato ambiguo (0 o 2+
    /// "canonici") è quindi ancora più improbabile che per TestCase/NC/CR — praticamente ogni gruppo con lo
    /// stesso Uid è una copia OneDrive/SharePoint dello stesso record.</summary>
    public IReadOnlyList<FileConProblemi> CopieDiConflitto { get { lock (_gate) return _copieDiConflitto.ToList(); } }

    public void RefreshIndex()
    {
        lock (_refreshGate) RefreshIndexCore();
    }

    private void RefreshIndexCore()
    {
        var nowUtc = DateTime.UtcNow;
        var deepValidate = nowUtc - _lastDeepContentValidationUtc >= _deepContentValidationInterval;
        if (!Directory.Exists(_paths.SegnalazioniDir))
        {
            lock (_gate)
            {
                _fileConProblemi = _tolleranzaProblemi.AggiornaCompleto(Array.Empty<FileConProblemi>());
                _cache = new();
                _hashByPath = new(StringComparer.OrdinalIgnoreCase);
                _uniquePathByUid = new();
                _duplicateUids = new();
                if (deepValidate) _lastDeepContentValidationUtc = nowUtc;
            }
            return;
        }

        Dictionary<string, (DateTime Mtime, SegnalazioneDaClassificare Item)> fotografia;
        Dictionary<string, string> fotografiaHash;
        lock (_gate)
        {
            fotografia = new(_cache);
            fotografiaHash = _cache.ToDictionary(
                kv => kv.Key,
                kv => _hashByPath.TryGetValue(kv.Key, out var hash) ? hash : EntityStateFingerprint.Compute(kv.Value.Item),
                StringComparer.OrdinalIgnoreCase);
        }

        var nuovaCache = new Dictionary<string, (DateTime Mtime, SegnalazioneDaClassificare Item)>(fotografia);
        var problemi = new List<FileConProblemi>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new DirectoryInfo(_paths.SegnalazioniDir).EnumerateFiles("segnalazione_*.json"))
        {
            var path = file.FullName;
            seen.Add(path);
            var mtime = file.LastWriteTimeUtc;
            if (!deepValidate && fotografia.TryGetValue(path, out var cached) && cached.Mtime == mtime) continue;

            var item = JsonIo.TryRead<SegnalazioneDaClassificare>(path, out var errore);
            if (item == null)
            {
                if (WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, path))
                {
                    nuovaCache.Remove(path);
                    continue;
                }

                // Un file ancora presente non diventa logicamente "cancellato" soltanto perché OneDrive lo
                // rende temporaneamente illeggibile o perché la receipt della sua transazione non è ancora
                // assestata. Manteniamo la last-known-good se esiste.
                if (!fotografia.ContainsKey(path)) nuovaCache.Remove(path);
                problemi.Add(new FileConProblemi { Percorso = path, Errore = errore ?? "sconosciuto", RilevatoIl = DateTime.Now });
                continue;
            }
            nuovaCache[path] = (mtime, item);
        }
        foreach (var stale in nuovaCache.Keys.Where(p => !seen.Contains(p)).ToList())
            nuovaCache.Remove(stale);

        lock (_gate)
        {
            foreach (var kv in _cache)
            {
                var currentHash = _hashByPath.TryGetValue(kv.Key, out var hash)
                    ? hash
                    : EntityStateFingerprint.Compute(kv.Value.Item);
                if (!fotografia.TryGetValue(kv.Key, out var prima) ||
                    prima.Mtime != kv.Value.Mtime ||
                    !fotografiaHash.TryGetValue(kv.Key, out var openingHash) ||
                    !openingHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
                    nuovaCache[kv.Key] = kv.Value;
            }
            foreach (var eliminato in fotografia.Keys.Where(k => !_cache.ContainsKey(k)))
                nuovaCache.Remove(eliminato);

            var groups = nuovaCache.GroupBy(kv => kv.Value.Item.Uid).ToList();

            // Block C (revisione multi-PC 6.4.2): il nome canonico di una Segnalazione e' derivato direttamente
            // dall'Uid (DataPaths.SegnalazioneFile), quindi un gruppo con lo stesso Uid e ESATTAMENTE UN path
            // canonico e' sempre una copia di conflitto OneDrive/SharePoint dello stesso record (non esiste,
            // per come e' costruito l'Id, un modo per due Segnalazioni realmente distinte di generare lo stesso
            // Uid). Prima di questo fix QUALUNQUE duplicato — anche una copia innocua a contenuto identico —
            // rendeva l'intera Segnalazione invisibile da GetAll() e bloccava Save()/Get() ("fork OneDrive
            // aperto") finche' un Master non la risolveva a mano: ora la copia viene esclusa dalla cache e
            // segnalata in CopieDiConflitto, mentre il canonico resta normalmente utilizzabile.
            var duplicati = new List<FileConProblemi>();
            var copieDiConflittoOra = new List<FileConProblemi>();
            foreach (var gruppo in groups.Where(g => g.Key != Guid.Empty && g.Count() > 1))
            {
                var pathCanonico = _paths.SegnalazioneFile(gruppo.Key);
                var canonici = gruppo.Where(kv => kv.Key.Equals(pathCanonico, StringComparison.OrdinalIgnoreCase)).ToList();
                if (canonici.Count != 1)
                {
                    duplicati.AddRange(gruppo.Select(kv => new FileConProblemi
                    {
                        Percorso = kv.Key,
                        Errore = $"UID Segnalazione duplicato ({gruppo.Key:N}): possibile fork OneDrive. Il record e bloccato finche il conflitto non viene risolto.",
                        RilevatoIl = DateTime.Now
                    }));
                    continue;
                }

                var canonico = canonici[0];
                var hashCanonico = EntityStateFingerprint.Compute(canonico.Value.Item);
                foreach (var copia in gruppo.Where(kv => !kv.Key.Equals(pathCanonico, StringComparison.OrdinalIgnoreCase)))
                {
                    var identico = EntityStateFingerprint.Compute(copia.Value.Item).Equals(hashCanonico, StringComparison.OrdinalIgnoreCase);
                    copieDiConflittoOra.Add(new FileConProblemi
                    {
                        Percorso = copia.Key,
                        Errore = identico
                            ? $"Copia di conflitto OneDrive/SharePoint della stessa Segnalazione ({gruppo.Key:N}), contenuto identico all'originale ({Path.GetFileName(pathCanonico)}). Ignorabile: il file non è stato toccato."
                            : $"Copia di conflitto OneDrive/SharePoint della stessa Segnalazione ({gruppo.Key:N}), CONTENUTO DIVERSO dall'originale ({Path.GetFileName(pathCanonico)}). Verifica manualmente prima di ignorarla: nessun dato è stato perso, il file resta sul disco.",
                        RilevatoIl = DateTime.Now,
                        RichiedeAttenzione = !identico
                    });
                    nuovaCache.Remove(copia.Key);
                }
            }

            _cache = nuovaCache;
            _hashByPath = nuovaCache.ToDictionary(kv => kv.Key, kv => EntityStateFingerprint.Compute(kv.Value.Item), StringComparer.OrdinalIgnoreCase);
            var groupsFinali = nuovaCache.GroupBy(kv => kv.Value.Item.Uid).ToList();
            _duplicateUids = groupsFinali.Where(g => g.Key != Guid.Empty && g.Count() > 1).Select(g => g.Key).ToHashSet();
            _uniquePathByUid = groupsFinali.Where(g => g.Key != Guid.Empty && g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Key);

            _copieDiConflitto = copieDiConflittoOra;
            // Gli UID duplicati residui (nessun canonico univoco) sono un fatto strutturale/deterministico, non
            // un errore di lettura transitorio: niente tolleranza, visibili subito come prima.
            _fileConProblemi = _tolleranzaProblemi.AggiornaCompleto(problemi).Concat(duplicati).ToList();
            if (deepValidate) _lastDeepContentValidationUtc = nowUtc;
        }
    }

    /// <summary>Codici test referenziati da qualunque Segnalazione (anche gia classificate/escluse), senza
    /// clonare: servono a non riassegnare mai un codice ancora referenziato.</summary>
    public IReadOnlyCollection<string> GetAllReferencedTestIds()
    {
        lock (_gate)
            return _uniquePathByUid.Values.Select(path => _cache[path].Item.TestId)
                .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<SegnalazioneDaClassificare> GetAll()
    {
        List<SegnalazioneDaClassificare> authoritative;
        lock (_gate)
        {
            authoritative = _uniquePathByUid.Values.Select(path => _cache[path].Item).OrderByDescending(x => x.CreataIl).ToList();
        }
        return WorkflowWorkingCopyScope.ApplySegnalazioni(authoritative);
    }

    /// <summary>Come GetAll(), stesso ordine, ma SENZA clonare ogni Segnalazione (clone JSON per elemento): gli elementi
    /// sono quelli in cache o la working-copy attiva. Solo lettura immediata, nel chiamante: non mutare e non trattenere.
    /// Usata dalla ricerca globale, che proietta stringhe; GetAll() resta la scelta di default.</summary>
    public IReadOnlyList<SegnalazioneDaClassificare> PeekAllReadOnly()
    {
        List<SegnalazioneDaClassificare> authoritative;
        lock (_gate)
        {
            authoritative = _uniquePathByUid.Values.Select(path => _cache[path].Item).OrderByDescending(x => x.CreataIl).ToList();
        }
        return WorkflowWorkingCopyScope.PeekSegnalazioni(authoritative);
    }

    /// <summary>Conteggio scalare sullo stesso snapshot/overlay di GetAll, senza clonare l'archivio (Addendum
    /// prestazioni 2026-09-25, Home a 100k: DaClassificare non deve pagare EntityStateFingerprint.Clone su ogni
    /// Segnalazione solo per contare quelle in un dato stato).</summary>
    public long CountByState(StatoSegnalazione stato)
    {
        IEnumerable<SegnalazioneDaClassificare> authoritative;
        lock (_gate)
            authoritative = _uniquePathByUid.Values.Select(path => _cache[path].Item).ToList();
        return WorkflowWorkingCopyScope.CountSegnalazioniByState(authoritative, stato);
    }

    /// <summary>Come GetAll(), ma filtrata PRIMA della copia isolata di ogni record: solo le Segnalazioni di
    /// <paramref name="testId"/> più quelle senza Test associato (stesso identico predicato che
    /// TestCaseEditorViewModel.CaricaPannelloAnomalie applicava dopo GetAll()). Analoga a
    /// NonConformitaRepository.GetByTestIds (Addendum prestazioni 2026-09-27, editor Test a 100k: aprire un
    /// singolo Test clonava l'intero archivio Segnalazioni con GetAll() solo per scartarne quasi tutte un
    /// istante dopo — costo che scalava con quante Segnalazioni esistono in totale, non con quante riguardano
    /// il Test che si sta aprendo).</summary>
    public IReadOnlyList<SegnalazioneDaClassificare> GetByTestIdOrSenzaTest(string testId, TestCase? test = null)
    {
        bool Corrisponde(SegnalazioneDaClassificare s, string id) =>
            test != null ? AnomaliaWorkflowService.SegnalazioneAppartieneAlTest(test, s) :
            s.TestId == id || string.IsNullOrEmpty(s.TestId);

        // Con una Unit of Work attiva la copia di lavoro può aggiungere Segnalazioni o cambiarne il TestId:
        // filtrare PRIMA dell'overlay darebbe un risultato diverso da GetAll().Where(...). In quel caso si usa
        // il percorso esatto di prima (stesso schema del fallback di PeekAllReadOnly/GetAll per le NC candidate).
        if (WorkflowWorkingCopyScope.Attivo)
            return GetAll().Where(s => Corrisponde(s, testId)).ToList();

        List<SegnalazioneDaClassificare> corrispondenti;
        lock (_gate)
            corrispondenti = _uniquePathByUid.Values.Select(path => _cache[path].Item)
                .Where(s => Corrisponde(s, testId))
                .OrderByDescending(x => x.CreataIl)
                .ToList();
        return WorkflowWorkingCopyScope.ApplySegnalazioni(corrispondenti);
    }

    public SegnalazioneDaClassificare? Get(Guid uid)
    {
        SegnalazioneDaClassificare? authoritative;
        lock (_gate)
        {
            if (_duplicateUids.Contains(uid))
                throw new InvalidDataException($"La segnalazione {uid:N} ha piu versioni fisiche in conflitto e non puo essere modificata automaticamente.");
            if (!_uniquePathByUid.TryGetValue(uid, out var path)) return null;
            authoritative = _cache[path].Item;
        }
        return WorkflowWorkingCopyScope.ApplySegnalazione(authoritative);
    }

    public void Save(SegnalazioneDaClassificare item) => Save(item, null);

    /// <summary>Journal per-device dei commit (R9). Null nei test/strumenti che non lo configurano.</summary>
    public DomainRecordJournalService? RecordJournal { get; set; }

    public void Save(SegnalazioneDaClassificare item, long? revisioneAttesa)
    {
        WorkspaceWritePolicy.EnsureCanWrite(_paths, "Salvataggio Segnalazione");
        if (item.TestUid.HasValue)
        {
            CompatibilitaVersioneService.EnsureTestUidSchema(_paths);
            item.SchemaVersion = Math.Max(item.SchemaVersion, 2);
        }
        lock (_gate)
        {
            if (_duplicateUids.Contains(item.Uid))
                throw new InvalidDataException($"La segnalazione {item.Uid:N} ha un fork OneDrive aperto. Salvataggio bloccato.");

            Directory.CreateDirectory(_paths.SegnalazioniDir);
            var path = _paths.SegnalazioneFile(item.Uid);
            using var commitLock = DomainRecordCommitLock.Acquire("Segnalazione", item.Uid.ToString("N"), path);
            VisibleConflictWriteGuard.EnsureNoVisibleFork(path, item.Uid, null,
                RecordJournal?.CopiaChiusaPer(DomainRecordJournalService.SegnalazioneDomain, item.Uid));
            RecordJournal?.EnsureNotBlocked(DomainRecordJournalService.SegnalazioneDomain, item.Uid, item.Uid.ToString("N"));
            SegnalazioneDaClassificare? corrente = null;
            if (File.Exists(path))
            {
                if (WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, path))
                    throw new IOException($"La segnalazione {item.Uid:N} è già stata eliminata. Il tombstone non viene sovrascritto; usa la Recovery se deve essere ripristinata.");

                corrente = JsonIo.TryRead<SegnalazioneDaClassificare>(path, out var errore);
                if (corrente == null)
                    throw new InvalidDataException($"Il file della segnalazione {item.Uid} non e leggibile e non verra sovrascritto: {errore}");
                if (!revisioneAttesa.HasValue)
                    throw new IOException("La segnalazione esiste gia; ricarica i dati e riprova.");
                if (corrente.Uid != item.Uid)
                    throw new IOException("Il file della segnalazione appartiene a un UID differente. Nessun dato e stato sovrascritto.");
                if (corrente.RevisioneDati != revisioneAttesa.Value)
                    throw new ConflittoRevisioneException(item.Uid.ToString(), revisioneAttesa.Value, corrente.RevisioneDati);
                if (!_hashByPath.TryGetValue(path, out var expectedHash))
                    throw new IOException("Fotografia di sicurezza della segnalazione non disponibile. Ricarica i dati prima di salvare.");
                var currentHash = EntityStateFingerprint.Compute(corrente);
                if (!currentHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new ConflittoRevisioneException(item.Uid.ToString(), revisioneAttesa.Value, corrente.RevisioneDati, true);
            }

            else if (revisioneAttesa.HasValue)
            {
                // I tombstone e le cancellazioni consegnate non devono resuscitare da un editor obsoleto.
                throw new ConflittoRevisioneException(item.Uid.ToString(), revisioneAttesa.Value, -1);
            }

            var revisionePrecedente = item.RevisioneDati;
            item.RevisioneDati = Math.Max(item.RevisioneDati, revisioneAttesa ?? 0) + 1;
            DomainRecordJournalService.PendingCommit? journalPending = null;
            try
            {
                journalPending = RecordJournal?.Prepare(DomainRecordJournalService.SegnalazioneDomain, item.Uid, item.Uid.ToString("N"),
                    corrente == null ? RecordJournalOperation.Create : RecordJournalOperation.Save,
                    corrente, corrente?.RevisioneDati, item, item.RevisioneDati, null);
                JsonIo.WriteAtomic(path, item, overwrite: revisioneAttesa.HasValue);
            }
            catch
            {
                item.RevisioneDati = revisionePrecedente;
                RecordJournal?.Cancel(journalPending);
                throw;
            }
            if (journalPending != null) RecordJournal!.Publish(journalPending);

            var snapshot = EntityStateFingerprint.Clone(item);
            _cache[path] = (File.GetLastWriteTimeUtc(path), snapshot);
            _hashByPath[path] = EntityStateFingerprint.Compute(snapshot);
            _uniquePathByUid[item.Uid] = path;
            DiagnosticTraceService.Registra("Segnalazione.Save", $"{item.Uid:N} revisione={item.RevisioneDati}");
        }
    }
}
