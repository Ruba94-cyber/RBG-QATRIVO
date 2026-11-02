using TestLoom.App.Models;
using System.Threading.Tasks;

namespace TestLoom.App.Services;

/// <summary>
/// Repository autorevole file-per-CR con read-model SQLite locale. I JSON condivisi restano la source of truth;
/// l'indice locale e' eliminabile e serve a evitare una cache RAM proporzionale al numero totale di CR.
/// In memoria restano soltanto gli hash di baseline delle CR effettivamente aperte tramite GetById/query legacy,
/// necessari per conservare il controllo anti-overwrite gia' esistente.
/// </summary>
public sealed class ChangeRequestRepository
{
    public const string LocalEntityKind = "change-request";
    // File per lotto nella scansione: letture e deserializzazioni del lotto in parallelo (vedi Scan in RefreshIndexCore).
    private const int RigheLottoScansione = 256;
    private static readonly TimeSpan DefaultDeepContentValidationInterval = TimeSpan.FromHours(6);

    private readonly DataPaths _paths;
    private readonly object _gate = new();
    private readonly object _refreshGate = new();
    private readonly TimeSpan _deepContentValidationInterval;
    private readonly LocalEntityIndexStore _localIndex;
    private readonly LocalIndexRecoveryCoordinator _indexRecovery;
    private readonly Dictionary<string, string> _hashByPath = new(StringComparer.OrdinalIgnoreCase);
    private List<FileConProblemi> _fileConProblemi = new();
    private List<FileConProblemi> _copieDiConflitto = new();
    private readonly FileProblemToleranceTracker _tolleranzaProblemi = new();
    private DateTime _lastDeepContentValidationUtc = DateTime.MinValue;
    private bool _hasCompletedScan;
    // Vero finche' la fotografia del tipo non e' completa: indice appena creato o ricreato (versione diversa, quarantena,
    // primo avvio) o ricostruzione in corso. In quel caso i lettori usano i JSON autorevoli, mai un indice vuoto o parziale.
    private volatile bool _indicePendente;

    public string LocalIndexDatabasePath => _localIndex.DatabasePath;
    public LocalIndexRecoveryCoordinator IndexRecovery => _indexRecovery;
    public bool HasPersistentSnapshot => _hasCompletedScan && _indexRecovery.IsUsable && !_indicePendente;

    public ChangeRequestRepository(DataPaths paths, TimeSpan? deepContentValidationInterval = null, string? localBaseDirectory = null)
    {
        _paths = paths;
        _deepContentValidationInterval = deepContentValidationInterval ?? DefaultDeepContentValidationInterval;
        _localIndex = new LocalEntityIndexStore(paths.Root, localBaseDirectory);
        _indexRecovery = new LocalIndexRecoveryCoordinator(_localIndex.DatabasePath,
            RicostruisciIndiceDaJson, "entity-index.db (Change Request)");
        // Nessuna eccezione dal costruttore: con il file bloccato (indice indisponibile) o senza fotografia completa le
        // letture vanno ai JSON autorevoli, e la sessione continua.
        _indicePendente = true;
        if (_localIndex.IndisponibileMotivo is { } motivo)
        {
            _indexRecovery.InvalidaComeObsoleto(motivo);
            return;
        }
        try
        {
            _indicePendente = !_localIndex.SnapshotCompleto(LocalEntityKind);
        }
        catch (Exception ex) when (LocalIndexFaultClassifier.IndisponibileAmbiente(ex))
        {
            CrashLogService.Registra("Change Request: fotografia dell'indice non verificabile (letture dai JSON)", ex);
            _indexRecovery.InvalidaComeObsoleto(ex.Message);
        }
    }

    public IReadOnlyList<FileConProblemi> FileConProblemi { get { lock (_gate) return _fileConProblemi.ToList(); } }

    /// <summary>Copie di conflitto OneDrive/SharePoint per CR (block C, revisione multi-PC 6.4.2): stessa
    /// logica content-driven di TestCaseRepository/NonConformitaRepository, adattata all'indice SQLite
    /// (LocalEntityIndexStore.GetByPath, niente materializzazione dell'intero archivio in RAM).</summary>
    public IReadOnlyList<FileConProblemi> CopieDiConflitto { get { lock (_gate) return _copieDiConflitto.ToList(); } }

    public void RefreshIndex()
    {
        lock (_refreshGate)
        {
            // Indice indisponibile per un file bloccato: si ritenta l'apertura (senza quarantena). Se ancora bloccato resta degradato.
            if (_localIndex.IndisponibileMotivo != null && !RiapriIndiceDopoIndisponibilita()) return;
            try
            {
                RefreshIndexCore(ricostruzione: false);
            }
            catch (Exception ex) when (LocalIndexFaultClassifier.IndisponibileAmbiente(ex))
            {
                // File bloccato: il riallineamento non avviene adesso. Le letture restano sui JSON e la prossima riconciliazione
                // riuscita, o il "Riprova" dell'utente, riabilita l'indice. Nessuna eccezione verso il chiamante.
                CrashLogService.Registra("Change Request: riallineamento dell'indice non riuscito (letture dai JSON)", ex);
                _indexRecovery.InvalidaComeObsoleto(ex.Message);
            }
        }
    }

    /// <summary>Ritenta l'apertura di uno store reso indisponibile da un file bloccato. Mai quarantena qui: un file sano torna in
    /// uso; un guasto persistente lascia l'indice degradato e non lancia verso il chiamante.</summary>
    private bool RiapriIndiceDopoIndisponibilita()
    {
        try
        {
            _localIndex.ReinizializzaDopoQuarantena();
            return true;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Change Request: riapertura dell'indice non riuscita (letture dai JSON)", ex);
            _indexRecovery.InvalidaComeObsoleto(ex.Message);
            return false;
        }
    }

    /// <summary>Lettura dall'indice per UI, ricerca, KPI e riferimenti. Con fotografia pendente l'indice non viene letto:
    /// il chiamante ricade sul JSON autorevole (come con indice non sano), cosi' nessuna pagina mostra zero o liste parziali.
    /// Le letture interne del refresh usano direttamente il coordinatore: sono quelle che costruiscono l'indice.</summary>
    private bool LeggiDaIndice<T>(string operazione, Func<T> lettura, out T risultato)
    {
        if (_indicePendente)
        {
            risultato = default!;
            return false;
        }
        return _indexRecovery.TryRead(operazione, lettura, out risultato);
    }

    private LocalIndexRebuildOutcome RicostruisciIndiceDaJson()
    {
        _indicePendente = true; // il database torna vuoto: finche' la riconciliazione non e' completa si legge dai JSON
        _localIndex.ReinizializzaDopoQuarantena();
        lock (_refreshGate) return RefreshIndexCore(ricostruzione: true);
    }

    private LocalIndexRebuildOutcome RefreshIndexCore(bool ricostruzione)
    {
        var nowUtc = DateTime.UtcNow;
        if (!Directory.Exists(_paths.Root))
        {
            if (ricostruzione) return LocalIndexRebuildOutcome.Incompleta("Cartella condivisa CR non raggiungibile.");
            throw new IOException("Cartella condivisa CR non raggiungibile: fotografia locale conservata.");
        }
        var deepValidate = ricostruzione || nowUtc - _lastDeepContentValidationUtc >= _deepContentValidationInterval;
        var problemi = new List<FileConProblemi>();
        // Tolleranza (multi-PC 6.4.2): un file la cui lettura fallisce va confermato "scomparso/risolto" solo
        // quando davvero non compare più nell'enumerazione di questo giro, non solo quando non viene riletto.
        var presentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var confermatiOk = new List<string>();

        IEnumerable<LocalEntityScanItem<ChangeRequest>> Scan()
        {
            if (!Directory.Exists(_paths.ChangeRequestDir)) yield break;
            // A lotti: la lettura e la deserializzazione dei file di un lotto gira in parallelo (solo I/O e CPU, nessuno stato
            // condiviso). Stamp, problemi, tombstone e yield restano sul thread di scansione, nell'ordine di enumerazione.
            foreach (var lotto in Lotti(new DirectoryInfo(_paths.ChangeRequestDir).EnumerateFiles("cr_*.json", SearchOption.TopDirectoryOnly), RigheLottoScansione))
            {
                var daLeggere = new List<int>(lotto.Count);
                var stampoInvariato = new bool[lotto.Count];
                for (var i = 0; i < lotto.Count; i++)
                {
                    var file = lotto[i];
                    presentPaths.Add(file.FullName);
                    if (!deepValidate && _localIndex.TryGetStamp(LocalEntityKind, file.FullName, out var indexedMtime, out var indexedLength) &&
                        indexedMtime == file.LastWriteTimeUtc.Ticks && indexedLength == file.Length)
                        stampoInvariato[i] = true; // stamp invariato dall'ultima lettura riuscita: non era e non e' un problema
                    else
                        daLeggere.Add(i);
                }

                var letti = new (ChangeRequest? Cr, string? Errore)[lotto.Count];
                var eccezioni = new Exception?[lotto.Count];
                Parallel.ForEach(daLeggere, i =>
                {
                    try
                    {
                        letti[i].Cr = JsonIo.TryRead<ChangeRequest>(lotto[i].FullName, out var errore);
                        letti[i].Errore = errore;
                    }
                    catch (Exception ex) { eccezioni[i] = ex; }
                });

                for (var i = 0; i < lotto.Count; i++)
                {
                    var file = lotto[i];
                    var path = file.FullName;
                    var mtime = file.LastWriteTimeUtc;
                    var length = file.Length;
                    if (stampoInvariato[i])
                    {
                        confermatiOk.Add(path);
                        yield return new LocalEntityScanItem<ChangeRequest>(path, mtime, length, null, PreserveExistingWhenEntityMissing: true);
                        continue;
                    }

                    // Eccezione inattesa di lettura: rilanciata qui, nell'ordine di enumerazione, come nel percorso sequenziale.
                    if (eccezioni[i] != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(eccezioni[i]!).Throw();
                    var cr = letti[i].Cr;
                    if (cr == null)
                    {
                        var committedDelete = WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, path);
                        if (!committedDelete)
                            problemi.Add(new FileConProblemi { Percorso = path, Errore = letti[i].Errore ?? "sconosciuto", RilevatoIl = DateTime.Now });
                        else
                            confermatiOk.Add(path);
                        yield return new LocalEntityScanItem<ChangeRequest>(
                            path, mtime, length, null, PreserveExistingWhenEntityMissing: !committedDelete);
                        continue;
                    }
                    confermatiOk.Add(path);
                    yield return new LocalEntityScanItem<ChangeRequest>(path, mtime, length, cr);
                }
            }
        }

        void Riconcilia() => _localIndex.Reconcile(
            LocalEntityKind, Scan(), x => x.Id, x => x.Uid,
            archivedOf: x => x.Archiviata, modifiedOf: x => x.UltimaModifica, ricercaOf: RicercaTesto);
        if (ricostruzione) Riconcilia();
        else if (_indexRecovery.Salute == LocalIndexHealth.Unavailable)
        {
            // Indice degradato (file bloccato, poi riaperto): la riconciliazione completa e' il rimedio, come per NC e
            // TestCaseRepository. Se riesce l'indice torna sano; altrimenti resta degradato senza eccezioni.
            try
            {
                Riconcilia();
                _indexRecovery.MarcaSano();
            }
            catch (Exception ex) when (LocalIndexFaultClassifier.IndisponibileAmbiente(ex))
            {
                return LocalIndexRebuildOutcome.Incompleta("Indice CR indisponibile: riconciliazione non pubblicata.");
            }
        }
        else if (!_indexRecovery.TryWrite("full reconciliation CR", Riconcilia))
            return LocalIndexRebuildOutcome.Incompleta("Indice CR indisponibile: riconciliazione non pubblicata.");

        var duplicati = ricostruzione
            ? _localIndex.GetDuplicateUids(LocalEntityKind)
            : _indexRecovery.TryRead("duplicati UID CR", () => _localIndex.GetDuplicateUids(LocalEntityKind), out var letti)
                ? letti : Array.Empty<LocalEntityDuplicateUid>();
        // Gli UID duplicati sono un fatto strutturale/deterministico, non un errore di lettura transitorio:
        // niente tolleranza, visibili subito come prima. Block C (revisione multi-PC 6.4.2): una copia di
        // conflitto OneDrive/SharePoint riconoscibile (nome file non canonico, stesso Uid del canonico) non
        // diventa più un "UID duplicato" allarmante — esclusa dall'indice locale, segnalata a parte in
        // CopieDiConflitto con l'esito del confronto di contenuto, mai persa (il file fisico non è mai toccato).
        var duplicatiProblemi = new List<FileConProblemi>();
        var copieDiConflittoOra = new List<FileConProblemi>();
        var pathDaEscludereDaIndice = new List<string>();
        foreach (var duplicate in duplicati)
        {
            var record = duplicate.Paths
                .Select(p => (Path: p, Record: _indexRecovery.TryRead("CR per path (classificazione conflitto)",
                    () => _localIndex.GetByPath<ChangeRequest>(LocalEntityKind, p), out var r) ? r : null))
                .Where(x => x.Record != null)
                .ToList();

            var canonici = record.Where(x => x.Path.Equals(_paths.ChangeRequestFile(x.Record!.Entity.Id), StringComparison.OrdinalIgnoreCase)).ToList();
            if (canonici.Count != 1)
            {
                duplicatiProblemi.AddRange(duplicate.Paths.Select(path => new FileConProblemi
                {
                    Percorso = path,
                    Errore = $"UID Change Request duplicato ({duplicate.Uid:N}): possibile copia di conflitto OneDrive. Nessuna versione deve essere sovrascritta finche il conflitto non e risolto.",
                    RilevatoIl = DateTime.Now
                }));
                continue;
            }

            var canonico = canonici[0];
            foreach (var copia in record.Where(x => !x.Path.Equals(canonico.Path, StringComparison.OrdinalIgnoreCase)))
            {
                var identico = copia.Record!.ContentHash.Equals(canonico.Record!.ContentHash, StringComparison.OrdinalIgnoreCase);
                copieDiConflittoOra.Add(new FileConProblemi
                {
                    Percorso = copia.Path,
                    Errore = identico
                        ? $"Copia di conflitto OneDrive/SharePoint della stessa Change Request ({canonico.Record.Entity.Id}), contenuto identico all'originale ({Path.GetFileName(canonico.Path)}). Ignorabile: il file non è stato toccato."
                        : $"Copia di conflitto OneDrive/SharePoint della stessa Change Request ({canonico.Record.Entity.Id}), CONTENUTO DIVERSO dall'originale ({Path.GetFileName(canonico.Path)}). Verifica manualmente prima di ignorarla: nessun dato è stato perso, il file resta sul disco.",
                    RilevatoIl = DateTime.Now,
                    RichiedeAttenzione = !identico
                });
                pathDaEscludereDaIndice.Add(copia.Path);
            }
        }
        // Fuori dal loop sopra: rimuove le copie di conflitto dall'indice SOLO dopo aver letto tutti i gruppi
        // (GetDuplicateUids/GetByPath sopra devono ancora vederle mentre le si classifica).
        foreach (var path in pathDaEscludereDaIndice)
            _indexRecovery.TryWrite("escludi copia di conflitto CR", () => _localIndex.Remove(LocalEntityKind, path));

        // Path tracciati in tolleranza che in questo giro non sono più stati enumerati (file rimosso/spostato
        // fuori dalla cartella condivisa): confermati risolti, altrimenti resterebbero segnalati per sempre.
        confermatiOk.AddRange(_tolleranzaProblemi.PercorsiTracciati().Where(p => !presentPaths.Contains(p)));

        lock (_gate)
        {
            _fileConProblemi = _tolleranzaProblemi.AggiornaIncrementale(confermatiOk, problemi).Concat(duplicatiProblemi).ToList();
            _copieDiConflitto = copieDiConflittoOra;
            _hasCompletedScan = true;
            if (deepValidate) _lastDeepContentValidationUtc = nowUtc;
        }
        // Riconciliazione di tutti i file enumerati: la fotografia del tipo e' completa e i lettori tornano sull'indice.
        // Il marcatore nel database va scritto prima di spegnere la pendenza, cosi' un riavvio non lo perde.
        if (_indicePendente)
        {
            try
            {
                _localIndex.MarcaSnapshotCompleto(LocalEntityKind);
                _indicePendente = false;
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Change Request: marcatore di fotografia completa non scritto, letture ancora dai JSON", ex);
            }
        }
        // La riconciliazione profonda ha riscritto il testo di ricerca di tutti i file: la proiezione e' completa.
        if (deepValidate)
        {
            try
            {
                _localIndex.MarcaRicercaCompleta(LocalEntityKind);
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Change Request: marcatore della ricerca per testo non scritto, ricerca sul JSON", ex);
            }
        }
        return problemi.Count == 0
            ? LocalIndexRebuildOutcome.Riuscita(0)
            : LocalIndexRebuildOutcome.Incompleta($"{problemi.Count} file CR non verificabili.");
    }

    /// <summary>
    /// API legacy: materializza solo su richiesta del caller. Il repository non conserva piu' questa lista in
    /// RAM dopo il ritorno. I percorsi UI ad alta crescita devono preferire QuerySlice/QueryArchivedSlice.
    /// </summary>
    public IReadOnlyList<ChangeRequest> GetAll(bool includeArchiviate = false)
    {
        var result = new List<ChangeRequest>();
        if (!LeggiDaIndice("elenco CR", () => _localIndex.Enumerate<ChangeRequest>(
                LocalEntityKind, archived: includeArchiviate ? null : false).ToList(), out var records))
            return LeggiJsonConPercorso()
                .Where(x => includeArchiviate || !x.Entity.Archiviata)
                .OrderByDescending(x => x.Entity.UltimaModifica).ThenBy(x => x.Entity.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Path, StringComparer.Ordinal)
                .Select(x => { RememberBaseline(x.Path, x.Entity); return EntityStateFingerprint.Clone(x.Entity); })
                .ToList();
        foreach (var record in records)
        {
            RememberBaseline(record);
            result.Add(EntityStateFingerprint.Clone(record.Entity));
        }
        return result;
    }

    /// <summary>
    /// Lettura di SOLA LETTURA delle CR per filtri, paginazione, conteggi e riferimenti che non modificano e non salvano
    /// nulla. Rispetto a <see cref="GetAll"/>: un solo passaggio di deserializzazione dall'indice locale, senza la copia
    /// di ogni record (serializzazione + deserializzazione) e senza registrare la fotografia di sicurezza anti-overwrite.
    /// Il repository non conserva questa lista in RAM: ogni chiamata costruisce oggetti nuovi, quindi una mutazione per
    /// errore non puo' alterare lo stato del repository. Gli oggetti NON vanno salvati: un Save richiede la fotografia
    /// registrata da GetById/GetAll/QueryActiveSlice, che questa lettura non crea.
    /// </summary>
    public IReadOnlyList<ChangeRequest> PeekAllReadOnly(bool includeArchiviate = false)
    {
        if (!LeggiDaIndice("elenco CR (sola lettura)", () => _localIndex.EnumerateOrdinatoPerLettura<ChangeRequest>(
                LocalEntityKind, archived: includeArchiviate ? null : false).Select(r => r.Entity).ToList(), out var entita))
            return LeggiJsonConPercorso()
                .Where(x => includeArchiviate || !x.Entity.Archiviata)
                .OrderByDescending(x => x.Entity.UltimaModifica).ThenBy(x => x.Entity.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Path, StringComparer.Ordinal)
                .Select(x => x.Entity)
                .ToList();
        return entita;
    }

    public ChangeRequest? GetById(string id)
    {
        if (!LeggiDaIndice("CR per ID", () => _localIndex.GetByKey<ChangeRequest>(LocalEntityKind, id), out var record))
        {
            if (!Directory.Exists(_paths.Root))
                throw new IOException("Cartella condivisa CR non raggiungibile: impossibile cercare per ID.");
            var path = _paths.ChangeRequestFile(id);
            if (!File.Exists(path) || WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, path)) return null;
            var cr = JsonIo.TryRead<ChangeRequest>(path, out var errore)
                ?? throw new InvalidDataException($"CR autorevole non leggibile ({path}): {errore}");
            RememberBaseline(path, cr);
            return EntityStateFingerprint.Clone(cr);
        }
        if (record == null) return null;
        RememberBaseline(record);
        return EntityStateFingerprint.Clone(record.Entity);
    }

    /// <summary>Trova le CR attive che referenziano un determinato TestId senza caricare l'intero archivio in memoria.</summary>
    public IReadOnlyList<ChangeRequest> GetByTestValidazioneId(string testId)
    {
        if (string.IsNullOrWhiteSpace(testId)) return Array.Empty<ChangeRequest>();

        if (LeggiDaIndice("CR per TestId", () => _localIndex.EnumerateMatching<ChangeRequest>(
                LocalEntityKind, testId, archived: false).ToList(), out var records))
        {
            var result = new List<ChangeRequest>();
            foreach (var record in records)
            {
                if (record.Entity.TestValidazione.Any(l => string.Equals(l.TestId, testId, StringComparison.OrdinalIgnoreCase)))
                {
                    RememberBaseline(record);
                    result.Add(EntityStateFingerprint.Clone(record.Entity));
                }
            }
            return result;
        }

        return GetAll().Where(c => c.TestValidazione.Any(l => string.Equals(l.TestId, testId, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public ChangeRequestArchiveSlice QueryArchivedSlice(long offset, int limit)
    {
        if (!LeggiDaIndice("pagina CR archiviate", () => _localIndex.QuerySlice<ChangeRequest>(
                LocalEntityKind, offset, limit, archived: true), out var slice))
            return PaginaDaJson(offset, limit, archived: true);
        var result = new List<ChangeRequest>(slice.Items.Count);
        foreach (var record in slice.Items)
        {
            RememberBaseline(record);
            result.Add(EntityStateFingerprint.Clone(record.Entity));
        }
        return new ChangeRequestArchiveSlice(result, slice.TotalCount);
    }

    public ChangeRequestArchiveSlice QueryActiveSlice(long offset, int limit)
    {
        if (!LeggiDaIndice("pagina CR attive", () => _localIndex.QuerySlice<ChangeRequest>(
                LocalEntityKind, offset, limit, archived: false), out var slice))
            return PaginaDaJson(offset, limit, archived: false);
        var result = new List<ChangeRequest>(slice.Items.Count);
        foreach (var record in slice.Items)
        {
            RememberBaseline(record);
            result.Add(EntityStateFingerprint.Clone(record.Entity));
        }
        return new ChangeRequestArchiveSlice(result, slice.TotalCount);
    }

    /// <summary>Vero se un'altra CR (attiva o archiviata) cita <paramref name="crId"/> in CrCorrelate: stessa risposta del
    /// percorso completo. Percorso veloce: candidati dall'indice locale, ciascuno confermato sul proprio JSON; si usa solo
    /// con indice allineato ai file e Id ricercabile. In ogni altro caso ricade sul percorso completo.</summary>
    public bool ReferenziataDaAltraCr(string crId)
    {
        if (TentaReferenziataDaIndice(crId, out var referenziata)) return referenziata;
        return ReferenziataDaAltraCrPercorsoCompleto(crId);
    }

    /// <summary>Percorso completo: scorre tutte le pagine di CR attive e archiviate (dall'indice, o dal JSON se l'indice
    /// non e' disponibile).</summary>
    internal bool ReferenziataDaAltraCrPercorsoCompleto(string crId)
    {
        const int passoPagina = 500;
        foreach (var archived in new[] { false, true })
        {
            long offset = 0;
            while (true)
            {
                var slice = archived
                    ? QueryArchivedSlice(offset, passoPagina)
                    : QueryActiveSlice(offset, passoPagina);
                if (slice.Items.Any(c => c.Id != crId && c.CrCorrelate.Contains(crId, StringComparer.OrdinalIgnoreCase)))
                    return true;
                if (slice.Items.Count == 0 || checked(offset + slice.Items.Count) >= slice.TotalCount) break;
                offset = checked(offset + slice.Items.Count);
            }
        }
        return false;
    }

    /// <summary>Percorso veloce. Restituisce false (non applicabile) se l'indice non e' allineato ai file, l'Id non e'
    /// ricercabile nell'indice o una lettura fallisce: il chiamante ricade sul percorso completo. Con indice allineato,
    /// i candidati dell'indice (JSON che contiene l'Id) vengono letti dal file, che resta l'unica fonte per la conferma.</summary>
    internal bool TentaReferenziataDaIndice(string crId, out bool referenziata)
    {
        referenziata = false;
        if (!IdRicercabileNellIndice(crId) || !IndiceAllineatoAiFile()) return false;
        if (!LeggiDaIndice("candidati CR per riferimento",
                () => _localIndex.PercorsiContenenti(LocalEntityKind, crId, archived: null), out var percorsi)) return false;
        foreach (var percorso in percorsi)
        {
            var candidata = JsonIo.TryRead<ChangeRequest>(percorso, out _);
            if (candidata == null) return false;
            if (candidata.Id != crId && candidata.CrCorrelate.Contains(crId, StringComparer.OrdinalIgnoreCase))
            {
                referenziata = true;
                return true;
            }
        }
        return true;
    }

    /// <summary>Id con soli caratteri ASCII che il JSON non escapa, senza 'i' e 's': per questi l'uguaglianza
    /// OrdinalIgnoreCase coincide con quella ASCII (solo 'ı' e 'ſ' maiuscolizzano in I e S), quindi la ricerca
    /// sull'indice non puo' perdere candidati.</summary>
    internal static bool IdRicercabileNellIndice(string id) =>
        id.Length > 0
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
        && id.IndexOfAny(new[] { 'i', 'I', 's', 'S' }) < 0;

    /// <summary>Vero se l'indice corrisponde ai file su disco: stessi file (le copie di conflitto sono escluse dall'indice
    /// per costruzione), stesso mtime e stessa lunghezza, nessun record in piu'. Non legge i JSON.</summary>
    internal bool IndiceAllineatoAiFile()
    {
        if (!_hasCompletedScan || !Directory.Exists(_paths.ChangeRequestDir)) return false;
        if (!LeggiDaIndice("stamp CR", () => _localIndex.CaricaStamp(LocalEntityKind), out var stamp)) return false;
        var copie = CopieDiConflitto.Select(c => c.Percorso).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visti = 0;
        foreach (var file in new DirectoryInfo(_paths.ChangeRequestDir).EnumerateFiles("cr_*.json", SearchOption.TopDirectoryOnly))
        {
            if (copie.Contains(file.FullName)) continue;
            if (!stamp.TryGetValue(file.FullName, out var atteso)
                || atteso.MtimeTicks != file.LastWriteTimeUtc.Ticks || atteso.Length != file.Length)
                return false;
            visti++;
        }
        return visti == stamp.Count;
    }

    public long Count(bool includeArchived = false)
    {
        if (LeggiDaIndice("conteggio CR", () => includeArchived
                ? _localIndex.Count(LocalEntityKind) : _localIndex.Count(LocalEntityKind, archived: false), out var count))
            return count;
        return LeggiJsonConPercorso().LongCount(x => includeArchived || !x.Entity.Archiviata);
    }

    /// <summary>
    /// CR non archiviate il cui Stato NON e' chiuso (Chiusa o Annullata): alimenta il KPI "Change request aperte"
    /// della Home. Conteggio SQLite sull'indice, senza deserializzare i CR; senza un indice sano ricade sulla lettura
    /// JSON, come <see cref="Count"/>.
    /// </summary>
    public long CountAperte()
    {
        var chiuse = new[] { nameof(StatoChangeRequest.Chiusa), nameof(StatoChangeRequest.Annullata) };
        if (LeggiDaIndice("conteggio CR aperte", () => _localIndex.CountJsonTextNotIn(
                LocalEntityKind, nameof(ChangeRequest.Stato), chiuse, archived: false), out var conteggio))
            return conteggio;
        return LeggiJsonConPercorso().LongCount(x => !x.Entity.Archiviata &&
            x.Entity.Stato is not (StatoChangeRequest.Chiusa or StatoChangeRequest.Annullata));
    }

    /// <summary>Divide un'enumerazione in lotti di al massimo <paramref name="dimensione"/> elementi, nello stesso ordine.</summary>
    private static IEnumerable<List<T>> Lotti<T>(IEnumerable<T> elementi, int dimensione)
    {
        var lotto = new List<T>(dimensione);
        foreach (var e in elementi)
        {
            lotto.Add(e);
            if (lotto.Count < dimensione) continue;
            yield return lotto;
            lotto = new List<T>(dimensione);
        }
        if (lotto.Count > 0) yield return lotto;
    }

    /// <summary>
    /// Prime <paramref name="limite"/> CR non archiviate il cui testo di ricerca (identificativo, titolo, descrizione,
    /// responsabile, veicoli ed componente impattati) contiene <paramref name="q"/> senza distinguere maiuscole, nell'ordine di
    /// <see cref="PeekAllReadOnly"/>. Con la proiezione di ricerca completa legge soltanto l'indice e deserializza le sole
    /// righe restituite; altrimenti ricade sul percorso di sola lettura, con lo stesso risultato.
    /// </summary>
    public IReadOnlyList<ChangeRequest> CercaTesto(string q, int limite)
    {
        if (limite <= 0) return Array.Empty<ChangeRequest>();
        if (ProiezioneRicercaUtilizzabile(q)
            && LeggiDaIndice("ricerca CR per testo", () => CercaIndice(q, limite), out var trovate) && trovate != null)
            return trovate;
        return PeekAllReadOnly().Where(c => CorrispondeTesto(c, q)).Take(limite).ToList();
    }

    /// <summary>La proiezione risponde come il percorso JSON solo per query senza a capo: il testo di ricerca tiene ogni campo
    /// separato da un a capo, quindi una query che lo attraversa non puo' trovare nulla in nessuno dei due percorsi.</summary>
    private bool ProiezioneRicercaUtilizzabile(string q) =>
        !_indicePendente && !string.IsNullOrEmpty(q) && q.IndexOfAny(new[] { '\n', '\r' }) < 0
        && _localIndex.RicercaCompleta(LocalEntityKind);

    /// <summary>Null quando la proiezione non basta per questa lettura (riga senza testo o senza record): il chiamante ricade sul JSON.</summary>
    private List<ChangeRequest>? CercaIndice(string q, int limite)
    {
        var risultati = new List<ChangeRequest>();
        foreach (var (path, testo) in _localIndex.EnumerateRicerca(LocalEntityKind, archived: false))
        {
            if (testo == null) return null;
            if (!testo.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            var record = _localIndex.GetByPath<ChangeRequest>(LocalEntityKind, path);
            if (record == null) return null;
            risultati.Add(record.Entity);
            if (risultati.Count >= limite) break;
        }
        return risultati;
    }

    /// <summary>Testo di ricerca della CR: i campi che la ricerca globale confronta, separati da a capo.</summary>
    internal static string RicercaTesto(ChangeRequest cr) =>
        string.Join('\n', new string?[] { cr.Id, cr.Titolo, cr.DescrizioneBreve, cr.Descrizione, cr.Responsabile }
            .Concat(cr.VeicoliImpattati ?? new List<string>())
            .Concat(cr.EcuImpattate ?? new List<string>())
            .Select(v => v ?? string.Empty));

    /// <summary>Stesso criterio della ricerca globale: un campo contiene la query senza distinguere maiuscole.</summary>
    private static bool CorrispondeTesto(ChangeRequest cr, string q) =>
        new string?[] { cr.Id, cr.Titolo, cr.DescrizioneBreve, cr.Descrizione, cr.Responsabile }.Any(v => v?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
        || cr.VeicoliImpattati.Any(v => v?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
        || cr.EcuImpattate.Any(v => v?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);

    private ChangeRequestArchiveSlice PaginaDaJson(long offset, int limit, bool archived)
    {
        var ordinate = LeggiJsonConPercorso().Where(x => x.Entity.Archiviata == archived)
            .OrderByDescending(x => x.Entity.UltimaModifica).ThenBy(x => x.Entity.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Path, StringComparer.Ordinal).ToList();
        var items = ordinate.Skip(checked((int)Math.Min(offset, int.MaxValue))).Take(limit).Select(x =>
        {
            RememberBaseline(x.Path, x.Entity);
            return EntityStateFingerprint.Clone(x.Entity);
        }).ToList();
        return new ChangeRequestArchiveSlice(items, ordinate.Count);
    }

    private IEnumerable<(string Path, ChangeRequest Entity)> LeggiJsonConPercorso()
    {
        if (!Directory.Exists(_paths.Root))
            throw new IOException("Cartella condivisa CR non raggiungibile: impossibile usare il fallback autorevole.");
        if (!Directory.Exists(_paths.ChangeRequestDir)) yield break;
        // A lotti come lo scan dell'indice: ogni file si legge una volta sola e i file di un lotto si leggono in parallelo.
        // Ordine di restituzione, tombstone ed eccezioni restano quelli del percorso precedente (vedi LeggiAutorevole).
        foreach (var lotto in Lotti(Directory.EnumerateFiles(_paths.ChangeRequestDir, "cr_*.json", SearchOption.TopDirectoryOnly), RigheLottoScansione))
        {
            var letti = new (ChangeRequest? Cr, bool Tombstone, string? Errore)[lotto.Count];
            var eccezioni = new Exception?[lotto.Count];
            Parallel.For(0, lotto.Count, i =>
            {
                try { letti[i] = LeggiAutorevole(lotto[i]); }
                catch (Exception ex) { eccezioni[i] = ex; }
            });
            for (var i = 0; i < lotto.Count; i++)
            {
                if (eccezioni[i] != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(eccezioni[i]!).Throw();
                if (letti[i].Tombstone) continue;
                var cr = letti[i].Cr
                    ?? throw new InvalidDataException($"CR autorevole non leggibile ({lotto[i]}): {letti[i].Errore}");
                yield return (lotto[i], cr);
            }
        }
    }

    /// <summary>
    /// Lettura autorevole di un file CR con un solo caricamento dei byte: il tombstone (<c>_ditmDeleted</c>) e la
    /// deserializzazione vengono dagli stessi byte. Se il caricamento o il parse non riescono, si ricade sul percorso di
    /// prima (controllo tombstone su file e <see cref="JsonIo.TryRead{T}"/> con i suoi tentativi), cosi' lo stesso file in
    /// scrittura o danneggiato da' lo stesso esito di prima.
    /// </summary>
    private (ChangeRequest? Cr, bool Tombstone, string? Errore) LeggiAutorevole(string path)
    {
        byte[]? bytes;
        try { bytes = JsonIo.ReadAllBytesShared(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { bytes = null; }
        if (bytes != null)
        {
            var cr = JsonIo.TryParse<ChangeRequest>(bytes, out var errore);
            if (cr != null) return (cr, false, null);
            if (errore != null && errore.StartsWith("Tombstone TestLoom", StringComparison.Ordinal)) return (null, true, null);
        }
        if (WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, path)) return (null, true, null);
        var letto = JsonIo.TryRead<ChangeRequest>(path, out var erroreLettura);
        return (letto, false, erroreLettura);
    }

    private void RememberBaseline(LocalEntityRecord<ChangeRequest> record)
    {
        lock (_gate) _hashByPath[record.Path] = record.ContentHash;
    }

    private void RememberBaseline(string path, ChangeRequest cr)
    {
        lock (_gate) _hashByPath[path] = EntityStateFingerprint.Compute(cr);
    }

    public string NextId(DateTime? now = null)
    {
        var year = (now ?? DateTime.Now).Year;
        var prefix = $"CR-{year}-";
        // I tombstone fisici restano enumerabili e impediscono il riuso di ID cancellati. Il progressivo e'
        // a 64 bit: D4 e' soltanto la larghezza minima di visualizzazione, non un limite a 9999.
        var maxDisk = Directory.Exists(_paths.ChangeRequestDir)
            ? Directory.EnumerateFiles(_paths.ChangeRequestDir, "cr_*.json", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Select(name => name.StartsWith("cr_", StringComparison.OrdinalIgnoreCase) ? name[3..] : name)
                .Select(id => ParseProgressivo(id, prefix))
                .DefaultIfEmpty(0L).Max()
            : 0L;
        return $"{prefix}{checked(maxDisk + 1L):D4}";
    }

    private static long ParseProgressivo(string? id, string prefix)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
        return long.TryParse(id[prefix.Length..], out var n) ? n : 0;
    }

    /// <summary>Journal per-device dei commit (R9). Null nei test/strumenti che non lo configurano.</summary>
    public DomainRecordJournalService? RecordJournal { get; set; }

    public void Save(ChangeRequest cr, long? revisioneAttesa = null)
    {
        WorkspaceWritePolicy.EnsureCanWrite(_paths, "Salvataggio Change Request");
        lock (_gate)
        {
            var path = _paths.ChangeRequestFile(cr.Id);
            // Lock cross-processo: confronti e WriteAtomic dentro la stessa finestra.
            using var commitLock = DomainRecordCommitLock.Acquire("Change Request", cr.Id, path);
            VisibleConflictWriteGuard.EnsureNoVisibleFork(path, cr.Uid, null,
                RecordJournal?.CopiaChiusaPer(DomainRecordJournalService.ChangeRequestDomain, cr.Uid));
            RecordJournal?.EnsureNotBlocked(DomainRecordJournalService.ChangeRequestDomain, cr.Uid, cr.Id);
            ChangeRequest? current = null;
            if (File.Exists(path))
            {
                if (WorkflowTransactionVisibilityService.IsCommittedTombstone(_paths, path))
                    throw new IOException($"L'ID {cr.Id} è già stato usato da una Change Request eliminata. Genera un nuovo ID; il tombstone non viene sovrascritto.");

                current = JsonIo.TryRead<ChangeRequest>(path, out var error)
                    ?? throw new InvalidDataException($"Il file della CR {cr.Id} non e leggibile e non verra sovrascritto: {error}");
                if (!revisioneAttesa.HasValue) throw new IOException($"Esiste gia una CR con ID {cr.Id}.");
                if (current.Uid != cr.Uid)
                    throw new IOException($"L'ID {cr.Id} appartiene a una diversa Change Request. Nessun file e stato sovrascritto.");
                if (current.RevisioneDati != revisioneAttesa.Value)
                    throw new ConflittoRevisioneException(cr.Id, revisioneAttesa.Value, current.RevisioneDati);
                if (!_hashByPath.TryGetValue(path, out var expectedHash))
                    throw new IOException($"Fotografia di sicurezza della CR {cr.Id} non disponibile. Ricarica i dati prima di salvare.");
                var currentHash = EntityStateFingerprint.Compute(current);
                if (!currentHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new ConflittoRevisioneException(cr.Id, revisioneAttesa.Value, current.RevisioneDati, true);
            }
            else if (revisioneAttesa.HasValue)
            {
                // La rimozione remota dopo l'apertura non autorizza a ricreare una fotografia vecchia.
                throw new ConflittoRevisioneException(cr.Id, revisioneAttesa.Value, -1);
            }

            var old = cr.RevisioneDati;
            cr.RevisioneDati = Math.Max(old, revisioneAttesa ?? 0) + 1;
            DomainRecordJournalService.PendingCommit? journalPending = null;
            try
            {
                journalPending = RecordJournal?.Prepare(DomainRecordJournalService.ChangeRequestDomain, cr.Uid, cr.Id,
                    current == null ? RecordJournalOperation.Create : RecordJournalOperation.Save,
                    current, current?.RevisioneDati, cr, cr.RevisioneDati, cr.Changelog.LastOrDefault()?.Operatore);
                JsonIo.WriteAtomic(path, cr, overwrite: revisioneAttesa.HasValue);
            }
            catch
            {
                cr.RevisioneDati = old;
                RecordJournal?.Cancel(journalPending);
                throw;
            }
            if (journalPending != null) RecordJournal!.Publish(journalPending);

            var info = new FileInfo(path);
            var snapshot = EntityStateFingerprint.Clone(cr);
            _indexRecovery.TryWrite("upsert CR", () => _localIndex.Upsert(
                LocalEntityKind, path, info.LastWriteTimeUtc, info.Length, snapshot,
                x => x.Id, x => x.Uid, archived: snapshot.Archiviata, modified: snapshot.UltimaModifica, ricerca: RicercaTesto(snapshot)));
            _hashByPath[path] = EntityStateFingerprint.Compute(snapshot);
            DiagnosticTraceService.Registra("ChangeRequest.Save", $"{cr.Id} revisione={cr.RevisioneDati}");
        }
    }

    /// <summary>Loggato con la propria categoria (feedback utente 2026-09-18: "il logging... è importante
    /// perché non si vede quando archivio ecc") oltre al "ChangeRequest.Save" che Save() già emette per la
    /// scrittura sottostante: dalla sola traccia di Save non si distingue un salvataggio ordinario da
    /// un'archiviazione, che è esattamente l'operazione che l'utente non riusciva a vedere.</summary>
    public void Archivia(ChangeRequest cr, string autore)
    {
        var candidato = EntityStateFingerprint.Clone(cr);
        var rev = candidato.RevisioneDati;
        candidato.Archiviata = true;
        candidato.DataArchiviazione = DateTime.Now;
        candidato.UltimaModifica = DateTime.Now;
        candidato.Changelog.Add(new ChangelogEntry { Data = DateTime.Now, Operatore = autore, Campo = "archiviazione", ValorePrecedente = "attiva", ValoreNuovo = "archiviata" });
        Save(candidato, rev);
        ApplicaRisultatoArchivio(cr, candidato);
        DiagnosticTraceService.Registra("ChangeRequest.Archivia", $"{cr.Id} autore={autore}");
    }

    public void Ripristina(ChangeRequest cr, string autore)
    {
        var candidato = EntityStateFingerprint.Clone(cr);
        var rev = candidato.RevisioneDati;
        candidato.Archiviata = false;
        candidato.DataArchiviazione = null;
        candidato.UltimaModifica = DateTime.Now;
        candidato.Changelog.Add(new ChangelogEntry { Data = DateTime.Now, Operatore = autore, Campo = "archiviazione", ValorePrecedente = "archiviata", ValoreNuovo = "attiva" });
        Save(candidato, rev);
        ApplicaRisultatoArchivio(cr, candidato);
        DiagnosticTraceService.Registra("ChangeRequest.Ripristina", $"{cr.Id} autore={autore}");
    }

    private static void ApplicaRisultatoArchivio(ChangeRequest target, ChangeRequest source)
    {
        target.Archiviata = source.Archiviata;
        target.DataArchiviazione = source.DataArchiviazione;
        target.UltimaModifica = source.UltimaModifica;
        target.RevisioneDati = source.RevisioneDati;
        target.Changelog = source.Changelog.Select(EntityStateFingerprint.Clone).ToList();
    }

    public void EliminaDefinitivamente(ChangeRequest cr, string? operatore = null)
    {
        WorkspaceWritePolicy.EnsureCanWrite(_paths, "Eliminazione Change Request");
        lock (_gate)
        {
            var path = _paths.ChangeRequestFile(cr.Id);
            // Lock cross-processo: confronti e WriteAtomic dentro la stessa finestra.
            using var commitLock = DomainRecordCommitLock.Acquire("Change Request", cr.Id, path);
            VisibleConflictWriteGuard.EnsureNoVisibleFork(path, cr.Uid, null,
                RecordJournal?.CopiaChiusaPer(DomainRecordJournalService.ChangeRequestDomain, cr.Uid));
            if (File.Exists(path))
            {
                var current = JsonIo.TryRead<ChangeRequest>(path, out var error)
                    ?? throw new InvalidDataException($"Il file della CR {cr.Id} non e leggibile e non verra eliminato: {error}");
                if (current.Uid != cr.Uid)
                    throw new IOException($"Il file {cr.Id} appartiene a una diversa Change Request. Eliminazione bloccata.");
                if (current.RevisioneDati != cr.RevisioneDati)
                    throw new ConflittoRevisioneException(cr.Id, cr.RevisioneDati, current.RevisioneDati);
                if (!_hashByPath.TryGetValue(path, out var expectedHash))
                    throw new IOException($"Fotografia di sicurezza della CR {cr.Id} non disponibile. Ricarica i dati prima di eliminarla.");
                var currentHash = EntityStateFingerprint.Compute(current);
                if (!currentHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new ConflittoRevisioneException(cr.Id, cr.RevisioneDati, current.RevisioneDati, true);
                RecordJournal?.EnsureNotBlocked(DomainRecordJournalService.ChangeRequestDomain, cr.Uid, cr.Id);

                DomainRecordJournalService.PendingCommit? journalPending = null;
                try
                {
                    journalPending = RecordJournal?.Prepare(DomainRecordJournalService.ChangeRequestDomain, current.Uid, current.Id,
                        RecordJournalOperation.Delete, current, current.RevisioneDati, null, null, operatore);
                    DomainRecoveryService.ArchiveAndDelete(
                        _paths, path, DomainRecoveryCategory.ChangeRequest, operatore);
                }
                catch
                {
                    RecordJournal?.Cancel(journalPending);
                    throw;
                }
                if (journalPending != null) RecordJournal!.Publish(journalPending);
            }
            _indexRecovery.TryWrite("remove CR", () => _localIndex.Remove(LocalEntityKind, path));
            _hashByPath.Remove(path);
            DiagnosticTraceService.Registra("ChangeRequest.EliminaDefinitivamente", cr.Id);
        }
    }
}

public sealed record ChangeRequestArchiveSlice(IReadOnlyList<ChangeRequest> Items, long TotalCount);
