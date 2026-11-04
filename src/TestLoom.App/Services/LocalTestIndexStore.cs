using TestLoom.App.Models;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Read-model/cache SQLite LOCALE dei TestCase. Non è mai source of truth e non deve mai essere collocato
/// nella cartella OneDrive/SharePoint: i JSON condivisi restano l'unica fonte autorevole. Il DB può essere
/// cancellato/corrotto in qualunque momento e viene ricostruito dai JSON senza perdita dati.
///
/// Schema v5 (include campi custom, strumenti e data raw indicizzata): oltre al payload JSON conserva una proiezione normalizzata di Test, revisioni e operazioni.
/// Le statistiche storiche NON dipendono dal journal condiviso: revisioni/tentativi restano nel TestCase JSON
/// e questa proiezione è sempre rigenerabile anche per un test modificato dopo anni.
///
/// VINCOLO (review 6.4.0, vedi anche il commento su Models.Esito): le colonne outcome/current_outcome/
/// calculated_outcome qui sotto persistono (int)Esito SOLO perché questa è una proiezione ricostruibile a
/// piacere — ogni bump di CurrentSchemaVersion può tranquillamente far ripartire il calcolo da zero dal JSON
/// autorevole (vedi RecomputeOperationCounts per l'esempio v5→v6). È VIETATO persistere (int)Esito in
/// qualunque storage NON rigenerabile allo stesso modo: una rinumerazione futura dell'enum romperebbe
/// silenziosamente quel dato.
/// </summary>
public sealed class LocalTestIndexStore
{
    /// <summary>v7 (decisione utente 6.4.0, "Test importati: solo stato finale"): nuova colonna origine_test
    /// (proiezione di TestCase.Origine) su test_summary, usata dalle query di conteggio Home/statistiche
    /// operative per escludere i Test con OrigineTest.ImportStatoUdt. Solo la proiezione locale ricostruibile
    /// cambia formato, non il JSON autorevole: la migrazione aggiunge la colonna e la valorizza dal JSON già
    /// presente in test_case_cache (vedi EnsureOrigineTestColumn/BackfillOrigineTest), nessuna riscrittura su
    /// disco del TestCase.
    ///
    /// v6 (decisione utente 6.4.0, punto 1): operation_attempt_count/effective_operation_count e i
    /// loro equivalenti per revisione ora escludono le operazioni non eseguite (Da eseguire/N.A.). Solo la
    /// proiezione locale ricostruibile cambia formato, non il JSON autorevole: la migrazione ricalcola i
    /// conteggi dalla cache già presente (vedi RecomputeOperationCounts), nessuna riscrittura su disco.</summary>
    ///
    /// v9 (6.5.0, punto 1): test_revision.unit_count (unita operative per revisione) per il conteggio del lavoro in Home.
    ///
    /// v10 (6.5.0, punto 7): tabella test_dtc_operation (proiezione delle operazioni guasto: requisito, guasto, passo,
    /// esito, data) per calcolare l'avanzamento guasto da SQL senza scandire i Test in memoria. Solo proiezione
    /// locale ricostruibile: la migrazione la riempie dalla cache JSON (BackfillDtcOperations), nessuna
    /// riscrittura della fonte autorevole.
    ///
    /// v11 (6.5.1, schema dati 9): colonna test_dtc_operation.veicolo (veicolo dell'operazione guasto: Condizione.DtcVeicolo, o
    /// l'unico veicolo del test). Un indice v10 la riceve con ALTER TABLE e la proiezione guasto viene ricostruita dalla cache JSON nella
    /// stessa transazione (rollback completo se fallisce, versione non avanzata). Un client piu vecchio che trova v11 lancia
    /// LocalIndexSchemaIncompatibleException, gestita da InitializeWithRecovery: l'indice e SOLO una cache locale ricostruibile, quindi viene
    /// messo in quarantena e ricostruito dal JSON condiviso (nessun errore all'utente, nessun dato autorevole toccato).
    /// <summary>v12 (6.6.1, filtri Lista Test senza indice in memoria): test_summary.ecu_grezzi (numero di componente del Test,
    /// grezzo come TestCase.Componente.Count, anche con voci vuote o duplicate, a differenza di test_ecu) e
    /// test_summary.ticket_collegati (TestCase.TicketCollegati.Count). Solo proiezione locale: la migrazione le
    /// valorizza dal JSON gia' in test_case_cache (BackfillConteggiFiltroLista), nessuna riscrittura della fonte.</summary>
    /// <summary>v13 (6.6.1, ricerca testuale della Home): test_revision.note e test_revision.deviazioni (testo di
    /// Esecuzione.Note e Esecuzione.Deviazioni per revisione), per cercare nelle esecuzioni con la stessa regola della
    /// Home in memoria (HomeViewModel.Corrisponde). Solo proiezione locale: la migrazione le valorizza dal JSON in cache
    /// (BackfillNoteEsecuzioni), nessuna riscrittura della fonte condivisa.
    /// v14 (6.6.1, Home al primo ingresso a 100k): test_vehicle.eliminato, origine_test, data_creazione_ticks ed esito_home
    /// (copie di test_summary, con COALESCE(current_outcome, NonEseguitoNA)) per la query Veicoli × esito senza JOIN, con
    /// l'indice ix_test_vehicle_home; test_summary.senza_ecu (nessuna riga in test_ecu, stessa regola di InsertValues) per il
    /// conteggio Test senza componente senza sottoquery. Solo proiezione locale: la migrazione le valorizza dalle tabelle gia'
    /// presenti (BackfillProiezioniHome), nessuna lettura del JSON e nessuna riscrittura della fonte condivisa.</summary>
    internal const int CurrentSchemaVersion = 16;

    /// <summary>Versione scritta dal passo v2..v4 -> v5, mai quella corrente: i passi successivi (v8..v15) la completano in una
    /// transazione propria. Un crash tra i due lascia questa versione e il riavvio riprende dai passi successivi.</summary>
    private const string VersioneIntermediaV5 = "5";
    private const int BusyTimeoutMs = 750;
    private const int LoadBatchSize = 256;
    private const string FullScanCompletedKey = "full_scan_completed";
    private const string LastFullScanUtcKey = "last_full_scan_utc";
    private const string FtsEnabledKey = "fts_enabled";
    private const string ContentHashAlgoKey = "content_hash_algo";
    private readonly string _connectionString;

    public string DatabasePath { get; }
    public string WorkspaceKey { get; }
    public string WorkspaceDirectory { get; }

    /// <summary>
    /// Crea lo store per una cartella condivisa. localBaseDirectory esiste solo per test/diagnostica;
    /// in produzione il DB vive sotto %LOCALAPPDATA%\TestLoom\workspaces\&lt;hash-percorso&gt;.
    /// </summary>
    public LocalTestIndexStore(string sharedRoot, string? localBaseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(sharedRoot))
            throw new ArgumentException("La cartella condivisa non può essere vuota.", nameof(sharedRoot));

        // Estratto in WorkspaceLocalPaths (condiviso con BackupService per la cartella backup locale): stessa
        // normalizzazione e stesso hashing di prima, la chiave di un workspace già esistente non cambia.
        WorkspaceKey = WorkspaceLocalPaths.ComputeWorkspaceKey(sharedRoot);
        WorkspaceDirectory = WorkspaceLocalPaths.GetWorkspaceDirectory(sharedRoot, localBaseDirectory);
        Directory.CreateDirectory(WorkspaceDirectory);
        DatabasePath = Path.Combine(WorkspaceDirectory, "index.db");

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        _connectionString = builder.ToString();

        InitializeWithRecovery();
    }

    public bool HasCompletedFullScan => string.Equals(ReadMeta(FullScanCompletedKey), "1", StringComparison.Ordinal);

    /// <summary>Vero se gli hash memorizzati nelle righe sono stati prodotti dall'algoritmo attuale
    /// (<see cref="TestCaseFingerprint.VersioneAlgoritmo"/>). Il marcatore lo scrive SOLO la riconciliazione completa
    /// (stessa transazione che riscrive tutte le righe con hash attuali): un indice senza marcatore, o con un algoritmo
    /// diverso, fa ricalcolare i fingerprint al warm start come prima. Le scritture di singoli Test producono sempre hash attuali.</summary>
    public bool HashIndicizzatiRiusabili =>
        string.Equals(ReadMeta(ContentHashAlgoKey), TestCaseFingerprint.VersioneAlgoritmo, StringComparison.Ordinal);

    /// <summary>Righe scritte (upsert) da questa istanza: una riconciliazione profonda su dati invariati non dovrebbe scriverne nessuna.
    /// Misura per banchi e test di equivalenza, per istanza (nessuno stato statico condiviso).</summary>
    public long RigheScritteTotali { get; private set; }

    public bool FtsEnabled => string.Equals(ReadMeta(FtsEnabledKey), "1", StringComparison.Ordinal);

    public DateTime? LastFullScanUtc
    {
        get
        {
            var raw = ReadMeta(LastFullScanUtcKey);
            return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var value)
                ? value
                : null;
        }
    }

    /// <summary>
    /// Carica la fotografia locale senza toccare OneDrive. La deserializzazione avviene a batch in parallelo:
    /// mantiene limitato il picco di memoria ma sfrutta più core durante un warm-start con archivi molto grandi.
    /// Record illeggibili vengono ignorati e il successivo full reconciliation li rigenera dal JSON autorevole.
    /// </summary>
    public IReadOnlyList<LocalIndexedTest> LoadAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path, mtime_utc_ticks, file_length, content_hash, json
            FROM test_case_cache
            ORDER BY path;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<LocalIndexedTest>();
        var batch = new List<RawIndexedRow>(LoadBatchSize);

        while (reader.Read())
        {
            batch.Add(new RawIndexedRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4)));

            if (batch.Count >= LoadBatchSize)
            {
                ParseBatch(batch, result);
                batch.Clear();
            }
        }
        if (batch.Count > 0) ParseBatch(batch, result);
        return result;
    }

    private static void ParseBatch(List<RawIndexedRow> batch, List<LocalIndexedTest> destination)
    {
        var parsed = new LocalIndexedTest?[batch.Count];
        Parallel.For(0, batch.Count, i =>
        {
            var row = batch[i];
            var tc = JsonIo.TryParse<TestCase>(row.Json, out _);
            if (tc == null) return;
            tc.NormalizzaVeicoli();
            parsed[i] = new LocalIndexedTest(
                row.Path,
                new DateTime(row.MtimeUtcTicks, DateTimeKind.Utc),
                row.FileLength,
                row.ContentHash,
                tc);
        });
        foreach (var item in parsed)
            if (item != null) destination.Add(item);
    }

    /// <summary>Aggiorna una sola entità dopo che il JSON autorevole è stato scritto con successo.</summary>
    public void Upsert(string path, DateTime mtimeUtc, long fileLength, TestCase testCase)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var oldUid = ReadUidForPath(connection, transaction, path);
        using var command = CreateUpsertCommand(connection, transaction);
        BindUpsert(command, path, mtimeUtc, fileLength, testCase);
        command.ExecuteNonQuery();
        RebuildProjection(connection, transaction, path, testCase);

        if (!string.IsNullOrEmpty(oldUid) && !oldUid.Equals(testCase.Uid.ToString("N"), StringComparison.OrdinalIgnoreCase))
            RemoveProjectionIfOrphan(connection, transaction, oldUid);

        AllineaSorgentiHome(connection, transaction);
        transaction.Commit();
    }

    public void Remove(string path)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var uid = ReadUidForPath(connection, transaction, path);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM test_case_cache WHERE path = $path;";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }

        if (!string.IsNullOrEmpty(uid)) RemoveProjectionIfOrphan(connection, transaction, uid);
        AllineaSorgentiHome(connection, transaction);
        transaction.Commit();
    }

    /// <summary>
    /// Applica in una sola transazione tutte le mutazioni generate da un giro watcher. In questo modo un burst
    /// OneDrive di 10/100 file non apre una connessione e una transazione per ogni path. Gli orphan delle
    /// proiezioni vengono eliminati soltanto a fine batch: un move rappresentato come remove+upsert dello stesso
    /// UID non può quindi cancellare temporaneamente statistiche/FTS mentre la nuova destinazione è nello stesso
    /// commit locale. Il DB resta una cache: un eventuale errore fa rollback dell'intero batch e il successivo
    /// refresh/full reconciliation lo rigenera dai JSON autorevoli.
    /// </summary>
    public void ApplyBatch(IEnumerable<LocalIndexMutation> mutations)
    {
        var list = mutations
            .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Path))
            .GroupBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        if (list.Count == 0) return;

        using var connection = Open();
        if (list.Count < SogliaFileCacheBatch)
        {
            ApplyBatchInTransazione(connection, list);
            return;
        }
        // Cache piu' ampia per i batch con piu' file: a ogni file la cancellazione per Uid scandisce test_search (uid UNINDEXED)
        // e con la cache predefinita le stesse pagine vengono rilette. Misurato su RefreshPaths a 20.000 Test: con 64 MB il
        // tempo cala del 21 % a 100 file e del 19 % a 1.000 file; a un solo file la cache non si ammortizza e non si usa.
        var cacheOriginale = LeggiCacheSize(connection);
        ImpostaCacheSize(connection, -CacheScritturaKiB);
        try
        {
            ApplyBatchInTransazione(connection, list);
        }
        finally
        {
            ImpostaCacheSize(connection, cacheOriginale);
        }
    }

    /// <summary>Numero di file da cui il batch incrementale riusa i comandi della proiezione e usa la cache di scrittura.</summary>
    private const int SogliaFileCacheBatch = 10;

    private void ApplyBatchInTransazione(SqliteConnection connection, List<LocalIndexMutation> list)
    {
        using var transaction = connection.BeginTransaction();
        // Comandi della proiezione: un contenitore per file; riusati solo nei batch grandi (per un file la preparazione non si ammortizza).
        var riusaComandi = list.Count >= SogliaFileCacheBatch;
        var ftsEnabled = string.Equals(ReadMeta(connection, FtsEnabledKey), "1", StringComparison.Ordinal);
        var orphanCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var upsert = CreateUpsertCommand(connection, transaction);
        upsert.Prepare();

        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM test_case_cache WHERE path = $path;";
        var deletePath = delete.Parameters.Add("$path", SqliteType.Text);
        delete.Prepare();
        foreach (var mutation in list)
        {
            var oldUid = ReadUidForPath(connection, transaction, mutation.Path);
            if (mutation.Kind == LocalIndexMutationKind.Remove)
            {
                deletePath.Value = mutation.Path;
                delete.ExecuteNonQuery();
                if (!string.IsNullOrWhiteSpace(oldUid)) orphanCandidates.Add(oldUid);
                continue;
            }

            var tc = mutation.TestCase
                ?? throw new InvalidDataException($"Mutazione indice Upsert senza TestCase: {mutation.Path}");
            tc.NormalizzaVeicoli();
            var oldHash = ReadHashForPath(connection, transaction, mutation.Path);
            var hash = mutation.ContentHash ?? TestCaseFingerprint.GetStored(tc) ?? TestCaseFingerprint.Compute(tc);
            RigheScritteTotali++;
            BindUpsert(upsert, mutation.Path, mutation.MtimeUtc, mutation.FileLength, tc, hash);
            upsert.ExecuteNonQuery();

            if (!string.Equals(oldHash, hash, StringComparison.OrdinalIgnoreCase) ||
                !ProjectionExists(connection, transaction, tc.Uid))
            {
                RebuildProjection(connection, transaction, mutation.Path, tc,
                    deleteExisting: true, ftsEnabledOverride: ftsEnabled, riusaComandi: riusaComandi);
            }

            var newUid = tc.Uid.ToString("N");
            if (!string.IsNullOrWhiteSpace(oldUid) &&
                !oldUid.Equals(newUid, StringComparison.OrdinalIgnoreCase))
                orphanCandidates.Add(oldUid);
        }

        foreach (var uid in orphanCandidates)
            RemoveProjectionIfOrphan(connection, transaction, uid);

        AllineaSorgentiHome(connection, transaction);
        transaction.Commit();
    }

    /// <summary>
    /// Allinea atomicamente il DB locale alla fotografia completa verificata dei JSON. I payload sono sempre
    /// riconciliati; la proiezione storica viene rigenerata solo se il fingerprint del Test è cambiato, così
    /// una deep reconciliation di 50k/100k file invariati non riscrive milioni di righe operazione.
    /// </summary>
    public void ReconcileFull(IEnumerable<LocalIndexWriteEntry> entries, DateTime completedUtc) =>
        ReconcileFull(entries, completedUtc, markFullScanCompleted: true);

    /// <summary>
    /// Variante che consente di NON marcare la fotografia come completa. Serve quando la sorgente condivisa
    /// e' verificabile solo in parte (consegna OneDrive parziale, JSON illeggibili senza last-known-good):
    /// i payload letti restano utili come cache, ma un indice parziale non deve mai risultare "pronto",
    /// altrimenti al riavvio verrebbe usato come fotografia autorevole di un archivio incompleto.
    /// </summary>
    public void ReconcileFull(IEnumerable<LocalIndexWriteEntry> entries, DateTime completedUtc, bool markFullScanCompleted)
    {
        var entryList = entries as IReadOnlyList<LocalIndexWriteEntry> ?? entries.ToList();
        using var connection = Open();
        // Cache piu' ampia solo durante la riconciliazione completa: con la cache predefinita (circa 2 MB) la transazione
        // di un cold build riscrive piu' volte le stesse pagine (20.000 Test: cold build da 8,2 s a 6,3 s con 64 MB).
        // Il valore precedente torna in ogni caso a fine metodo: la connessione resta nel pool e ne e' condivisa.
        var cacheOriginale = LeggiCacheSize(connection);
        ImpostaCacheSize(connection, -CacheScritturaKiB);
        try
        {
            ReconcileFullInTransazione(connection, entryList, completedUtc, markFullScanCompleted);
        }
        finally
        {
            ImpostaCacheSize(connection, cacheOriginale);
        }
    }

    private void ReconcileFullInTransazione(SqliteConnection connection, IReadOnlyList<LocalIndexWriteEntry> entryList,
        DateTime completedUtc, bool markFullScanCompleted)
    {
        using var transaction = connection.BeginTransaction();
        // Righe e proiezioni lette una sola volta: le righe invariate non vengono riscritte e le proiezioni non
        // vengono cercate riga per riga (prima: un upsert con serializzazione JSON e una SELECT per ogni Test).
        var esistenti = LoadPathStamps(connection, transaction);
        var uidProiettati = LoadProjectedUids(connection, transaction);
        var freshProjectionStore = esistenti.Count == 0 &&
            entryList.Select(e => e.TestCase.Uid).Distinct().Count() == entryList.Count;
        // Caricamento completo su indice vuoto: gli indici secondari si sospendono e si ricreano nella stessa transazione,
        // prima del ciclo di pulizia (il DDL e' transazionale: un errore li ripristina col rollback). Ogni inserimento
        // aggiornava tutti gli indici delle tabelle di proiezione.
        var indiciSospesi = freshProjectionStore ? SospendiIndiciSecondari(connection, transaction) : null;
        var ftsEnabled = string.Equals(ReadMeta(connection, FtsEnabledKey), "1", StringComparison.Ordinal);

        using (var createSeen = connection.CreateCommand())
        {
            createSeen.Transaction = transaction;
            createSeen.CommandText = "CREATE TEMP TABLE IF NOT EXISTS seen_paths(path TEXT PRIMARY KEY); DELETE FROM seen_paths;";
            createSeen.ExecuteNonQuery();
        }

        using var seen = connection.CreateCommand();
        seen.Transaction = transaction;
        seen.CommandText = "INSERT OR IGNORE INTO seen_paths(path) VALUES($path);";
        var seenPath = seen.Parameters.Add("$path", SqliteType.Text);
        seen.Prepare();

        using var upsert = CreateUpsertCommand(connection, transaction);
        upsert.Prepare();
        // Comandi di proiezione riusati per tutta la riconciliazione: si preparano dalla seconda esecuzione (vedi ComandiProiezione).
        using var comandi = new ComandiProiezione(connection, transaction);

        foreach (var entry in entryList)
        {
            seenPath.Value = entry.Path;
            seen.ExecuteNonQuery();

            var hash = entry.ContentHash ?? TestCaseFingerprint.Compute(entry.TestCase);
            var uid = entry.TestCase.Uid.ToString("N");
            var hashInvariato = esistenti.TryGetValue(entry.Path, out var riga) &&
                riga.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase);
            // Riga identica (stesso path, mtime, lunghezza, hash e Uid): nessuna riscrittura. Lo hash determina il contenuto.
            if (!hashInvariato || riga.Mtime != entry.MtimeUtc.ToUniversalTime().Ticks ||
                riga.Length != entry.FileLength || riga.Uid != uid)
            {
                RigheScritteTotali++;
                BindUpsert(upsert, entry.Path, entry.MtimeUtc, entry.FileLength, entry.TestCase, hash);
                upsert.ExecuteNonQuery();
            }

            if (!hashInvariato || !uidProiettati.Contains(uid))
            {
                RebuildProjection(connection, transaction, entry.Path, entry.TestCase,
                    deleteExisting: !freshProjectionStore, ftsEnabledOverride: ftsEnabled, uidProiettati: uidProiettati, comandi: comandi);
            }
        }

        if (indiciSospesi != null) RipristinaIndiciSecondari(connection, transaction, indiciSospesi);

        using (var removeStale = connection.CreateCommand())
        {
            removeStale.Transaction = transaction;
            removeStale.CommandText = """
                DELETE FROM test_case_cache
                WHERE NOT EXISTS (SELECT 1 FROM seen_paths s WHERE s.path = test_case_cache.path);
                """;
            removeStale.ExecuteNonQuery();
        }

        RemoveAllOrphanProjections(connection, transaction);
        AllineaSorgentiHome(connection, transaction);
        if (markFullScanCompleted)
        {
            WriteMeta(connection, transaction, FullScanCompletedKey, "1");
            WriteMeta(connection, transaction, LastFullScanUtcKey, completedUtc.ToUniversalTime().ToString("O"));
            WriteMeta(connection, transaction, ContentHashAlgoKey, TestCaseFingerprint.VersioneAlgoritmo);
        }
        transaction.Commit();
    }

    /// <summary>Cache durante le scritture della proiezione (riconciliazione completa e batch incrementale), in KiB: 64 MB.</summary>
    private const long CacheScritturaKiB = 65536;

    private static long LeggiCacheSize(SqliteConnection connection)
    {
        using var comando = connection.CreateCommand();
        comando.CommandText = "PRAGMA cache_size;";
        return Convert.ToInt64(comando.ExecuteScalar());
    }

    /// <summary>Valore del PRAGMA cache_size: negativo = KiB (es. -2000), positivo = numero di pagine.</summary>
    private static void ImpostaCacheSize(SqliteConnection connection, long valorePragma)
    {
        using var comando = connection.CreateCommand();
        comando.CommandText = $"PRAGMA cache_size={valorePragma};";
        comando.ExecuteNonQuery();
    }

    /// <summary>Statistiche aggregate locali, calcolate senza materializzare i TestCase completi. Decisione
    /// utente 6.4.0 ("Test importati: solo stato finale"): i Test con OrigineTest.ImportStatoUdt sono
    /// fotografie di uno stato noto, non esecuzioni fatte in TestLoom — restano fuori da questi conteggi
    /// operativi (origine_test = 0, cioè TestLoom).</summary>
    public LocalArchiveStatistics GetArchiveStatistics(bool includeDeleted = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                COUNT(*),
                COALESCE(SUM(executed_revision_count), 0),
                COALESCE(SUM(operation_attempt_count), 0),
                COALESCE(SUM(effective_operation_count), 0),
                COALESCE(SUM(CASE WHEN current_outcome IS NOT NULL AND executed_revision_count > 0 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN current_outcome = $conforme AND executed_revision_count > 0 THEN 1 ELSE 0 END), 0)
            FROM test_summary
            WHERE origine_test = 0 {(includeDeleted ? "" : "AND eliminato = 0")};
            """;
        command.Parameters.AddWithValue("$conforme", (int)Esito.Conforme);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new();
        return new LocalArchiveStatistics
        {
            TestCount = reader.GetInt32(0),
            RevisionCount = reader.GetInt32(1),
            OperationAttemptCount = reader.GetInt32(2),
            EffectiveOperationCount = reader.GetInt32(3),
            TestedCount = reader.GetInt32(4),
            ConformingCount = reader.GetInt32(5)
        };
    }

    /// <summary>
    /// Lavoro eseguito per operatore. Ogni tentativo storico resta contato; EffectiveOperationCount conta
    /// invece solo l'ultimo tentativo di ciascuna operazione logica dentro la revisione, stessa semantica di
    /// Esecuzione.CondizioniEffettive usata oggi dall'esito del Test.
    /// </summary>
    public IReadOnlyList<LocalOperatorOperationStatistics> GetOperatorOperationStatistics(bool includeDeleted = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                r.operatore,
                COUNT(DISTINCT r.test_uid),
                COUNT(*),
                COALESCE(SUM(r.raw_attempt_count), 0),
                COALESCE(SUM(r.effective_operation_count), 0),
                COALESCE(SUM(CASE WHEN r.outcome = $conforme THEN 1 ELSE 0 END), 0)
            FROM test_revision r
            JOIN test_summary t ON t.uid = r.test_uid
            WHERE t.origine_test = 0 AND r.raw_attempt_count > 0 AND r.origine <> 1 {(includeDeleted ? "" : "AND t.eliminato = 0")}
            GROUP BY r.operatore
            ORDER BY r.operatore COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$conforme", (int)Esito.Conforme);
        using var reader = command.ExecuteReader();
        var result = new List<LocalOperatorOperationStatistics>();
        while (reader.Read())
        {
            result.Add(new LocalOperatorOperationStatistics
            {
                Operator = reader.IsDBNull(0) ? "" : reader.GetString(0),
                TestCount = reader.GetInt32(1),
                RevisionCount = reader.GetInt32(2),
                OperationAttemptCount = reader.GetInt32(3),
                EffectiveOperationCount = reader.GetInt32(4),
                ConformingRevisionCount = reader.GetInt32(5)
            });
        }
        return result;
    }

    /// <summary>
    /// Statistica per Funzionalità: conserva tutti i tentativi in ordine cronologico e misura quanti tentativi
    /// sono serviti, per coppia Test+Funzionalità, fino al primo Esito.Conforme. Le coppie mai arrivate a
    /// Conforme restano esplicitamente contate e non contaminano la media, coerente con KpiService.
    /// </summary>
    public IReadOnlyList<LocalFunctionalityOperationStatistics> GetFunctionalityOperationStatistics(bool includeDeleted = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // Decisione utente 6.4.0, punto 1: "a.outcome IN (1,2,3)" = solo le operazioni realmente eseguite
        // (Conforme/NonConforme/ConformeConDeviazioni — Esito.EEseguito() lato modello). Un tentativo mai
        // eseguito non è un tentativo: non deve contare né nel totale né nel calcolo di AttemptsToFirstConforme.
        command.CommandText = $"""
            SELECT a.test_uid, a.funzionalita, a.outcome, a.is_effective
            FROM test_operation_attempt a
            JOIN test_summary t ON t.uid = a.test_uid
            WHERE a.funzionalita <> '' AND a.outcome IN (1, 2, 3) AND t.origine_test = 0 {(includeDeleted ? "" : "AND t.eliminato = 0")}
            ORDER BY a.test_uid, a.funzionalita COLLATE NOCASE, a.revision_index, a.attempt_index;
            """;
        using var reader = command.ExecuteReader();

        var perPair = new Dictionary<(string TestUid, string Functionality), PairAccumulator>();
        while (reader.Read())
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!perPair.TryGetValue(key, out var acc))
                perPair[key] = acc = new PairAccumulator();
            acc.TotalAttempts++;
            if (reader.GetInt32(3) != 0) acc.EffectiveOperations++;
            if (!acc.AttemptsToFirstConforme.HasValue && reader.GetInt32(2) == (int)Esito.Conforme)
                acc.AttemptsToFirstConforme = acc.TotalAttempts;
        }

        return perPair
            .GroupBy(kv => kv.Key.Functionality, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var reached = g.Where(x => x.Value.AttemptsToFirstConforme.HasValue).ToList();
                return new LocalFunctionalityOperationStatistics
                {
                    Functionality = g.Key,
                    TestFunctionalityPairs = g.Count(),
                    SuccessfulPairs = reached.Count,
                    NeverSuccessfulPairs = g.Count() - reached.Count,
                    TotalOperationAttempts = g.Sum(x => x.Value.TotalAttempts),
                    TotalEffectiveOperations = g.Sum(x => x.Value.EffectiveOperations),
                    AverageAttemptsToFirstConforme = reached.Count == 0
                        ? 0
                        : reached.Average(x => x.Value.AttemptsToFirstConforme!.Value)
                };
            })
            .OrderBy(x => x.Functionality, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>TIME (6.6.1), regola di copia della durata per una nuova operazione. Stessa chiave del Report: funzionalita' (per le
    /// operazioni non guasto) e componente. Una sola componente: l'operazione che la contiene. Piu' componente: l'insieme esatto dei componenti. Operazioni guasto:
    /// stesso passo (<paramref name="passoDtc"/>) e stessa componente. Preferisce le operazioni sui veicoli del Test, poi le altre; a parita'
    /// l'ordine del Report (data, Id del Test in ordine ordinale, revisione, tentativo). Solo operazioni eseguite con durata nota;
    /// Test eliminati e snapshot UnitLogger esclusi. Solo l'ULTIMA revisione di ogni operazione (stesso TestUid + Condizione.Uid) puo'
    /// proporre un valore: una revisione superata (per esempio 30 min, poi durata cancellata) non torna mai in copia.
    /// Null = nessun precedente: il valore resta vuoto.</summary>
    public int? TrovaDurataCopia(IReadOnlyCollection<string> componente, string? funzionalita, IReadOnlyCollection<string> veicoli, int? passoDtc)
    {
        var ecuNormalizzate = componente.Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ecuNormalizzate.Count == 0) return null;
        var veicoliNormalizzati = veicoli.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (passoDtc is null && string.IsNullOrWhiteSpace(funzionalita)) return null;

        var parametri = new List<(string Nome, object Valore)>();
        var condizioni = new List<string>
        {
            "a.outcome IN (1, 2, 3)",
            "a.duration_minutes IS NOT NULL",
            "s.eliminato = 0",
            "s.origine_test = 0",
            // Solo l'ultima revisione in cui l'operazione compare (il valore dell'identita del Report): una revisione successiva senza
            // durata, o con un esito non eseguito, rende superato il valore precedente. Il confronto e' sulla riga piu' recente, non
            // sulle sole righe eseguite.
            "NOT EXISTS (SELECT 1 FROM test_operation_attempt n WHERE n.test_uid = a.test_uid AND n.condition_uid = a.condition_uid "
                + "AND n.revision_index > a.revision_index)",
        };
        if (passoDtc is { } passo)
        {
            condizioni.Add("EXISTS (SELECT 1 FROM test_dtc_operation d WHERE d.test_uid = a.test_uid AND d.revision_index = a.revision_index "
                + "AND d.attempt_index = a.attempt_index AND d.passo = $passo)");
            parametri.Add(("$passo", passo));
        }
        else
        {
            condizioni.Add("a.funzionalita = $funzionalita COLLATE NOCASE");
            parametri.Add(("$funzionalita", funzionalita!.Trim()));
            condizioni.Add("NOT EXISTS (SELECT 1 FROM test_dtc_operation d WHERE d.test_uid = a.test_uid AND d.revision_index = a.revision_index "
                + "AND d.attempt_index = a.attempt_index)");
        }

        const string chiaveEcu = "e.test_uid = a.test_uid AND e.revision_index = a.revision_index AND e.attempt_index = a.attempt_index";
        for (var i = 0; i < ecuNormalizzate.Count; i++) parametri.Add(($"$componente{i}", ecuNormalizzate[i]));
        if (ecuNormalizzate.Count == 1)
        {
            condizioni.Add($"EXISTS (SELECT 1 FROM test_operation_ecu e WHERE {chiaveEcu} AND e.componente = $componente0)");
        }
        else
        {
            var elenco = string.Join(", ", ecuNormalizzate.Select((_, i) => $"$componente{i}"));
            parametri.Add(("$numeroEcu", ecuNormalizzate.Count));
            condizioni.Add($"(SELECT COUNT(*) FROM test_operation_ecu e WHERE {chiaveEcu}) = $numeroEcu");
            condizioni.Add($"(SELECT COUNT(*) FROM test_operation_ecu e WHERE {chiaveEcu} AND e.componente IN ({elenco})) = $numeroEcu");
        }

        // Senza veicoli nel Test non c'e' preferenza: il termine non entra nell'ORDER BY (un "0" sarebbe letto come indice di colonna, errore SQL).
        var preferenzaVeicolo = "";
        if (veicoliNormalizzati.Count > 0)
        {
            for (var i = 0; i < veicoliNormalizzati.Count; i++) parametri.Add(($"$veicolo{i}", veicoliNormalizzati[i]));
            var elencoVeicoli = string.Join(", ", veicoliNormalizzati.Select((_, i) => $"$veicolo{i}"));
            preferenzaVeicolo = $"EXISTS (SELECT 1 FROM test_vehicle v WHERE v.test_uid = a.test_uid AND v.value IN ({elencoVeicoli})) DESC, ";
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT a.duration_minutes
            FROM test_operation_attempt a
            JOIN test_summary s ON s.uid = a.test_uid
            JOIN test_revision r ON r.test_uid = a.test_uid AND r.revision_index = a.revision_index
            WHERE {string.Join("\n              AND ", condizioni)}
            ORDER BY {preferenzaVeicolo}r.data_ticks DESC, s.id DESC, a.revision_index DESC, a.attempt_index DESC
            LIMIT 1;
            """;
        foreach (var (nome, valore) in parametri) command.Parameters.AddWithValue(nome, valore);
        return command.ExecuteScalar() is long minuti ? (int)minuti : null;
    }

    /// <summary>Ricerca FTS locale. Ritorna UID, non TestCase completi, per mantenere il caricamento lazy.</summary>
    public IReadOnlyList<Guid> SearchTestUids(string query, int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(query) || !FtsEnabled) return Array.Empty<Guid>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT uid FROM test_search
            WHERE test_search MATCH $query
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", BuildFtsQuery(query));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader();
        var result = new List<Guid>();
        while (reader.Read())
            if (Guid.TryParseExact(reader.GetString(0), "N", out var uid)) result.Add(uid);
        return result;
    }

    /// <summary>
    /// Recupero in fase di inizializzazione. Prima intercettava QUALUNQUE SqliteException: un BUSY per un
    /// altro processo TestLoom aperto, una ACL negata o un disco pieno mandavano in quarantena un database
    /// perfettamente sano, buttando via la fotografia calda senza alcun beneficio. Ora solo corruzione
    /// (SQLITE_CORRUPT=11 / SQLITE_NOTADB=26) mette il file in quarantena; una versione non interpretabile viene
    /// solo eliminata, senza copia diagnostica (non e' corruzione). Ogni altro guasto viene rilanciato al chiamante,
    /// che scegliera' il percorso lento senza distruggere nulla.
    /// </summary>
    /// <summary>Soglia oltre la quale l'allineamento delle fonti Home ricostruisce tutto invece di ricalcolare Test per Test.</summary>
    internal const int SogliaRicostruzioneSorgentiHomeMinima = 64;

    /// <summary>Soglia di ricostruzione globale proporzionale all'archivio: 1/50 dei Test, con minimo <see cref="SogliaRicostruzioneSorgentiHomeMinima"/>.
    /// Il ricalcolo incrementale costa per Test modificato; la ricostruzione globale costa una passata sull'intero archivio, quindi con
    /// archivi grandi conviene solo oltre una frazione del totale. Entrambi i percorsi producono le stesse fonti (test di equivalenza).</summary>
    internal static int SogliaRicostruzioneSorgentiHome(long totaleTest) =>
        (int)Math.Min(int.MaxValue, Math.Max(SogliaRicostruzioneSorgentiHomeMinima, totaleTest / 50));

    /// <summary>Punto di interruzione della migrazione v16 (solo test): un'eccezione lanciata qui simula un'interruzione prima del commit.</summary>
    [ThreadStatic] internal static Action<string>? PuntoInterruzioneMigrazione;

    /// <summary>Candidate delle fonti Home: stesse regole di GetHomeFunctionalitySources, prima della scelta del vincitore. Il
    /// segnaposto FILTRO_UID limita la query a un solo Test (ricalcolo incrementale).</summary>
    private const string SqlCandidatiHome = """
        WITH attivi AS (
            SELECT uid, id FROM test_summary WHERE eliminato = 0/*FILTRO_UID*/
        ),
        dichiarate AS (
            SELECT s.uid, s.id,
                   COALESCE(f.value, '') AS funzionalita,
                   COALESCE(e.value, '') AS componente,
                   COALESCE(v.value, '') AS veicolo
            FROM attivi s
            LEFT JOIN test_functionality f ON f.test_uid = s.uid
            LEFT JOIN test_ecu e ON e.test_uid = s.uid
            LEFT JOIN test_vehicle v ON v.test_uid = s.uid
        ),
        tentativi AS (
            SELECT a.test_uid, a.revision_index, a.attempt_index, a.funzionalita, a.outcome,
                   a.data_ticks, s.id, r.data_ticks AS data_revisione
            FROM test_operation_attempt a
            JOIN attivi s ON s.uid = a.test_uid
            JOIN test_revision r ON r.test_uid = a.test_uid AND r.revision_index = a.revision_index
            WHERE a.is_effective = 1
              AND NOT EXISTS (
                  SELECT 1 FROM test_dtc_operation d
                  WHERE d.test_uid = a.test_uid AND d.revision_index = a.revision_index
                    AND d.attempt_index = a.attempt_index
              )
        ),
        tentativo_ecu AS (
            SELECT t.test_uid, t.revision_index, t.attempt_index, e.componente
            FROM tentativi t
            JOIN test_operation_ecu e ON e.test_uid = t.test_uid
              AND e.revision_index = t.revision_index AND e.attempt_index = t.attempt_index
            UNION ALL
            SELECT t.test_uid, t.revision_index, t.attempt_index,
                   CASE WHEN COUNT(e.value) = 1 THEN MIN(e.value) ELSE '' END AS componente
            FROM tentativi t
            LEFT JOIN test_ecu e ON e.test_uid = t.test_uid
            WHERE NOT EXISTS (
                SELECT 1 FROM test_operation_ecu oe WHERE oe.test_uid = t.test_uid
                  AND oe.revision_index = t.revision_index AND oe.attempt_index = t.attempt_index
            )
            GROUP BY t.test_uid, t.revision_index, t.attempt_index
        ),
        tentativo_funzionalita AS (
            SELECT test_uid, revision_index, attempt_index, funzionalita
            FROM tentativi WHERE TRIM(funzionalita) <> ''
            UNION ALL
            SELECT t.test_uid, t.revision_index, t.attempt_index, COALESCE(f.value, '')
            FROM tentativi t
            LEFT JOIN test_functionality f ON f.test_uid = t.test_uid
            WHERE TRIM(t.funzionalita) = ''
        ),
        tentativo_candidati AS (
            SELECT t.test_uid, t.id,
                   f.funzionalita,
                   e.componente, COALESCE(v.value, '') AS veicolo,
                   t.outcome AS outcome,
                   t.data_revisione AS data_ticks, t.revision_index, t.attempt_index
            FROM tentativi t
            JOIN tentativo_ecu e ON e.test_uid = t.test_uid AND e.revision_index = t.revision_index
                AND e.attempt_index = t.attempt_index
            JOIN tentativo_funzionalita f ON f.test_uid = t.test_uid AND f.revision_index = t.revision_index
                AND f.attempt_index = t.attempt_index
            LEFT JOIN test_vehicle v ON v.test_uid = t.test_uid
        ),
        revision_fallback AS (
            SELECT d.uid AS test_uid, d.id, d.funzionalita, d.componente, d.veicolo,
                   CASE WHEN EXISTS (
                       SELECT 1 FROM test_operation_attempt a
                       WHERE a.test_uid = r.test_uid AND a.revision_index = r.revision_index AND a.is_effective = 1
                   ) THEN NULL ELSE r.outcome END AS outcome,
                   r.data_ticks, r.revision_index, 0 AS attempt_index
            FROM test_revision r JOIN dichiarate d ON d.uid = r.test_uid
            WHERE NOT EXISTS (
                SELECT 1 FROM test_operation_attempt a
                WHERE a.test_uid = r.test_uid AND a.revision_index = r.revision_index AND a.is_effective = 1
                  AND NOT EXISTS (
                      SELECT 1 FROM test_dtc_operation guasto WHERE guasto.test_uid = a.test_uid
                        AND guasto.revision_index = a.revision_index AND guasto.attempt_index = a.attempt_index
                  )
            )
        ),
        candidati AS (
            SELECT test_uid, id, funzionalita, componente, veicolo, outcome, data_ticks, revision_index, attempt_index FROM tentativo_candidati
            UNION ALL
            SELECT test_uid, id, funzionalita, componente, veicolo, outcome, data_ticks, revision_index, attempt_index FROM revision_fallback
            UNION ALL
            SELECT d.uid AS test_uid, d.id, d.funzionalita, d.componente, d.veicolo, NULL AS outcome, 0 AS data_ticks,
                   -1 AS revision_index, 0 AS attempt_index
            FROM dichiarate d
            WHERE NOT EXISTS (SELECT 1 FROM test_revision r WHERE r.test_uid = d.uid)
        )
        SELECT test_uid, id, funzionalita, componente, veicolo, outcome, data_ticks, revision_index, attempt_index,
               CASE WHEN outcome IN ($conforme, $nonConforme, $deviazioni) THEN 1 ELSE 0 END AS eseguito,
               CASE WHEN outcome IS NULL OR outcome = $daEseguire THEN 1 ELSE 0 END AS previsto
        FROM candidati
        """;

    private const string SqlVincitoriDaCandidatiHome = """
        INSERT INTO home_source_winner(funzionalita, componente, veicolo, outcome, data_ticks, id, revision_index, attempt_index)
        SELECT funzionalita, componente, veicolo, outcome, data_ticks, id, revision_index, attempt_index
        FROM (
            SELECT c.funzionalita, c.componente, c.veicolo, c.outcome, c.data_ticks, c.id, c.revision_index, c.attempt_index,
                   ROW_NUMBER() OVER (
                       PARTITION BY c.funzionalita, c.componente, c.veicolo
                       ORDER BY c.eseguito DESC, c.previsto DESC, c.data_ticks DESC, c.id COLLATE BINARY DESC,
                                c.revision_index DESC, c.attempt_index DESC
                   ) AS posizione
            FROM home_source_candidate c
        )
        WHERE posizione = 1;
        """;

    internal const string SqlVincitoreDiPartizioneHome = """
        INSERT INTO home_source_winner(funzionalita, componente, veicolo, outcome, data_ticks, id, revision_index, attempt_index)
        SELECT funzionalita, componente, veicolo, outcome, data_ticks, id, revision_index, attempt_index
        FROM home_source_candidate
        WHERE funzionalita = $f AND componente = $e AND veicolo = $v
        ORDER BY eseguito DESC, previsto DESC, data_ticks DESC, id COLLATE BINARY DESC, revision_index DESC, attempt_index DESC
        LIMIT 1;
        """;

    /// <summary>Marca il Test come da riallineare nelle fonti Home, nella stessa transazione della sua proiezione.</summary>
    private static void MarcaSorgentiHomeDirty(SqliteConnection connection, SqliteTransaction transaction, string uid)
    {
        using var command = ComandoSorgentiHome(connection, transaction, "INSERT OR IGNORE INTO home_source_dirty(test_uid) VALUES ($uid);");
        command.Parameters.AddWithValue("$uid", uid);
        command.ExecuteNonQuery();
    }

    /// <summary>Allinea le fonti Home alle proiezioni modificate nella transazione corrente. Poche modifiche: ricalcolo per Test e
    /// per partizione (funzionalita' x componente x veicolo); molte: ricostruzione completa. Chiamata prima di ogni commit che scrive
    /// proiezioni, cosi' fonti e proiezioni restano coerenti anche dopo un'interruzione (rollback della stessa transazione).</summary>
    private static void AllineaSorgentiHome(SqliteConnection connection, SqliteTransaction transaction)
    {
        var daRiallineare = new List<string>();
        using (var lettura = ComandoSorgentiHome(connection, transaction, "SELECT test_uid FROM home_source_dirty;"))
        using (var reader = lettura.ExecuteReader())
            while (reader.Read()) daRiallineare.Add(reader.GetString(0));
        if (daRiallineare.Count == 0) return;
        // Il conteggio dell'archivio serve solo quando le modifiche superano il minimo (caso raro): nel caso comune nessuna query in piu'.
        if (daRiallineare.Count >= SogliaRicostruzioneSorgentiHomeMinima && daRiallineare.Count >= SogliaRicostruzioneSorgentiHome(ContaTestArchivio(connection, transaction)))
        {
            RicostruisciSorgentiHomeGlobale(connection, transaction);
            return;
        }

        var partizioni = new Dictionary<string, (string Funzionalita, string Componente, string Veicolo)>(StringComparer.Ordinal);
        foreach (var uid in daRiallineare)
        {
            foreach (var p in PartizioniSorgentiHome(connection, transaction, uid)) partizioni[ChiaveSorgentiHome(p)] = p;
            using (var delete = ComandoSorgentiHome(connection, transaction, "DELETE FROM home_source_candidate WHERE test_uid = $uid;"))
            {
                delete.Parameters.AddWithValue("$uid", uid);
                delete.ExecuteNonQuery();
            }
            using (var insert = ComandoSorgentiHome(connection, transaction, SqlInserisciCandidatiHome(perUid: true)))
            {
                insert.Parameters.AddWithValue("$uid", uid);
                AggiungiParametriEsitoSorgentiHome(insert);
                insert.ExecuteNonQuery();
            }
            foreach (var p in PartizioniSorgentiHome(connection, transaction, uid)) partizioni[ChiaveSorgentiHome(p)] = p;
        }
        foreach (var p in partizioni.Values) RicalcolaVincitoreSorgentiHome(connection, transaction, p);
        EseguiSorgentiHome(connection, transaction, "DELETE FROM home_source_dirty;");
    }

    private static long ContaTestArchivio(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = ComandoSorgentiHome(connection, transaction, "SELECT COUNT(*) FROM test_summary;");
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>Ricostruisce da zero candidate e vincitori (migrazione v16, bootstrap, riallineamento massivo).</summary>
    private static void RicostruisciSorgentiHomeGlobale(SqliteConnection connection, SqliteTransaction transaction)
    {
        EseguiSorgentiHome(connection, transaction, "DELETE FROM home_source_candidate;");
        EseguiSorgentiHome(connection, transaction, "DELETE FROM home_source_winner;");
        EseguiSorgentiHome(connection, transaction, "DELETE FROM home_source_dirty;");
        using (var insert = ComandoSorgentiHome(connection, transaction, SqlInserisciCandidatiHome(perUid: false)))
        {
            AggiungiParametriEsitoSorgentiHome(insert);
            insert.ExecuteNonQuery();
        }
        EseguiSorgentiHome(connection, transaction, SqlVincitoriDaCandidatiHome);
    }

    private static string SqlInserisciCandidatiHome(bool perUid) =>
        "INSERT INTO home_source_candidate(test_uid, id, funzionalita, componente, veicolo, outcome, data_ticks, revision_index, attempt_index, eseguito, previsto)\n"
        + SqlCandidatiHome.Replace("/*FILTRO_UID*/", perUid ? " AND uid = $uid" : "") + ";";

    private static void AggiungiParametriEsitoSorgentiHome(SqliteCommand command)
    {
        command.Parameters.AddWithValue("$conforme", (int)Esito.Conforme);
        command.Parameters.AddWithValue("$nonConforme", (int)Esito.NonConforme);
        command.Parameters.AddWithValue("$deviazioni", (int)Esito.ConformeConDeviazioni);
        command.Parameters.AddWithValue("$daEseguire", (int)Esito.DaEseguire);
    }

    private static SqliteCommand ComandoSorgentiHome(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void EseguiSorgentiHome(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = ComandoSorgentiHome(connection, transaction, sql);
        command.ExecuteNonQuery();
    }

    private static string ChiaveSorgentiHome((string Funzionalita, string Componente, string Veicolo) p) =>
        string.Concat(p.Funzionalita, "\u0001", p.Componente, "\u0001", p.Veicolo);

    private static List<(string Funzionalita, string Componente, string Veicolo)> PartizioniSorgentiHome(
        SqliteConnection connection, SqliteTransaction transaction, string uid)
    {
        var risultato = new List<(string Funzionalita, string Componente, string Veicolo)>();
        using var command = ComandoSorgentiHome(connection, transaction,
            "SELECT DISTINCT funzionalita, componente, veicolo FROM home_source_candidate WHERE test_uid = $uid;");
        command.Parameters.AddWithValue("$uid", uid);
        using var reader = command.ExecuteReader();
        while (reader.Read()) risultato.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return risultato;
    }

    private static void RicalcolaVincitoreSorgentiHome(SqliteConnection connection, SqliteTransaction transaction,
        (string Funzionalita, string Componente, string Veicolo) p)
    {
        using (var delete = ComandoSorgentiHome(connection, transaction,
            "DELETE FROM home_source_winner WHERE funzionalita = $f AND componente = $e AND veicolo = $v;"))
        {
            AggiungiPartizioneSorgentiHome(delete, p);
            delete.ExecuteNonQuery();
        }
        using var insert = ComandoSorgentiHome(connection, transaction, SqlVincitoreDiPartizioneHome);
        AggiungiPartizioneSorgentiHome(insert, p);
        insert.ExecuteNonQuery();
    }

    private static void AggiungiPartizioneSorgentiHome(SqliteCommand command,
        (string Funzionalita, string Componente, string Veicolo) p)
    {
        command.Parameters.AddWithValue("$f", p.Funzionalita);
        command.Parameters.AddWithValue("$e", p.Componente);
        command.Parameters.AddWithValue("$v", p.Veicolo);
    }

    private void InitializeWithRecovery()
    {
        var guasto = LocalIndexFaultKind.None;
        try
        {
            InitializeCore();
            return;
        }
        catch (Exception ex) when (LocalIndexFaultClassifier.Classify(ex) is
            LocalIndexFaultKind.Corruption or LocalIndexFaultKind.SchemaIncompatible)
        {
            guasto = LocalIndexFaultClassifier.Classify(ex);
            CrashLogService.Registra(
                $"Indice SQLite locale TestCase: reset all'avvio ({LocalIndexFaultClassifier.Descrivi(guasto)})", ex);
        }

        // Fuori dal filtro: il recupero puo' fallire e in quel caso l'errore deve arrivare al chiamante,
        // non essere confuso con l'eccezione originale. Una versione non compatibile viene eliminata, la corruzione
        // resta in quarantena (vedi LocalIndexQuarantine.TryRecuperaAllAvvio).
        if (!LocalIndexQuarantine.TryRecuperaAllAvvio(DatabasePath, guasto, out var motivo))
            throw new IOException($"Indice locale non recuperabile all'avvio. {motivo}");

        InitializeCore();
    }

    /// <summary>
    /// Riapre lo schema dopo che il coordinatore ha messo in quarantena i file. Serve perche' dopo la
    /// quarantena il file non esiste piu': senza questa reinizializzazione la ricostruzione scriverebbe su un
    /// database vuoto senza tabelle.
    /// </summary>
    public void ReinizializzaDopoQuarantena()
    {
        SqliteConnection.ClearAllPools();
        InitializeWithRecovery();
    }

    private void InitializeCore()
    {
        using var connection = Open();
        SqliteWalGuard.CheckpointIfOversized(connection, DatabasePath);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA busy_timeout={BusyTimeoutMs};
                PRAGMA temp_store=MEMORY;
                PRAGMA foreign_keys=ON;

                CREATE TABLE IF NOT EXISTS meta(
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS test_case_cache(
                    path TEXT PRIMARY KEY,
                    uid TEXT NOT NULL,
                    id TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    eliminato INTEGER NOT NULL,
                    mtime_utc_ticks INTEGER NOT NULL,
                    file_length INTEGER NOT NULL,
                    content_hash TEXT NOT NULL,
                    json TEXT NOT NULL,
                    indexed_utc_ticks INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_test_case_cache_uid ON test_case_cache(uid);
                CREATE INDEX IF NOT EXISTS ix_test_case_cache_id ON test_case_cache(id);
                CREATE INDEX IF NOT EXISTS ix_test_case_cache_eliminato ON test_case_cache(eliminato);
                CREATE INDEX IF NOT EXISTS ix_test_case_cache_revision ON test_case_cache(revision);
                """;
            command.ExecuteNonQuery();
        }

        var schemaRaw = ReadMeta(connection, "schema_version");
        if (schemaRaw != null && (!int.TryParse(schemaRaw, out var schema) || schema is < 1 or > CurrentSchemaVersion))
        {
            // Tipo dedicato: il file e' un database valido scritto da un'altra versione di TestLoom, non e'
            // corruzione. Usare SqliteException(…, 1) lo rendeva indistinguibile da un SQLITE_ERROR qualsiasi.
            throw new LocalIndexSchemaIncompatibleException($"Schema indice locale non supportato: {schemaRaw}.");
        }

        CreateProjectionSchema(connection);
        EnsureFtsSchema(connection);
        if (schemaRaw is "2" or "3" or "4") MigrateProjectionToV5(connection, schemaRaw);

        if (schemaRaw == null || schemaRaw == "1")
        {
            // v15 (TIME): la colonna della durata deve esistere prima della proiezione. Idempotente: su un indice nuovo
            // la tabella l'ha gia' creata CreateProjectionSchema.
            using (var colonnaDurata = connection.BeginTransaction())
            {
                EnsureColumn(connection, colonnaDurata, "test_operation_attempt", "duration_minutes", "INTEGER");
                colonnaDurata.Commit();
            }
            BackfillProjectionFromCache(connection);
            using var transaction = connection.BeginTransaction();
            EnsureRawCreationIndex(connection, transaction);
            RicostruisciSorgentiHomeGlobale(connection, transaction);
            PuntoInterruzioneMigrazione?.Invoke("v16:sorgenti-home");
            WriteMeta(connection, transaction, "schema_version", CurrentSchemaVersion.ToString());
            transaction.Commit();
        }
        else if (schemaRaw == "14")
        {
            // v15 (TIME, 6.6.1): colonna della durata per operazione e backfill dal JSON gia' in test_case_cache. Un indice
            // v14 e' gia' completo: solo questo passo (niente ricalcolo dei conteggi ne' della proiezione guasto). Transazione
            // unica, versione scritta per ultima: un'interruzione rolla back e il passo si ripete al successivo avvio.
            using var transaction = connection.BeginTransaction();
            EnsureColumn(connection, transaction, "test_operation_attempt", "duration_minutes", "INTEGER");
            BackfillDurataOperazioni(connection, transaction);
            RicostruisciSorgentiHomeGlobale(connection, transaction);
            PuntoInterruzioneMigrazione?.Invoke("v16:sorgenti-home");
            WriteMeta(connection, transaction, "schema_version", CurrentSchemaVersion.ToString());
            transaction.Commit();
        }
        else if (schemaRaw == "15")
        {
            // v16 (fonti Home): le proiezioni dei Test ci sono gia'; si costruiscono solo le fonti. Transazione unica, versione
            // scritta per ultima: un'interruzione rolla back e il passo si ripete al successivo avvio.
            using var transaction = connection.BeginTransaction();
            RicostruisciSorgentiHomeGlobale(connection, transaction);
            PuntoInterruzioneMigrazione?.Invoke("v16:sorgenti-home");
            WriteMeta(connection, transaction, "schema_version", CurrentSchemaVersion.ToString());
            transaction.Commit();
        }
        else if (schemaRaw != CurrentSchemaVersion.ToString())
        {
            // v6 (decisione utente 6.4.0, punto 1): un indice v2..v5 già popolato ha operation_attempt_count/
            // effective_operation_count calcolati con la vecchia regola (ogni Condizione conta, eseguita o
            // no). Ricalcolo diretto dal JSON già in test_case_cache, stesso principio di BackfillProjectionFromCache
            // — nessuna scansione della cartella condivisa, nessuna riscrittura della fonte autorevole.
            using var transaction = connection.BeginTransaction();
            // Un indice v10 ha gia completato (in una transazione unica, versione scritta per ultima) tutti i passi v2..v10: per v10 -> v11
            // basta la colonna veicolo + ricostruzione della sola proiezione guasto (nessun ricalcolo dei conteggi su 100k Test).
            if (schemaRaw != "10")
            {
            // v8 (decisione utente 6.4.8, punto 5): colonne executed_revision_count (test_summary) e origine
            // (test_revision) PRIMA del ricalcolo, che le valorizza insieme ai conteggi operativi.
            EnsureColumn(connection, transaction, "test_summary", "executed_revision_count", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, transaction, "test_revision", "origine", "INTEGER NOT NULL DEFAULT 0");
            // v9 (feedback utente 2026-10, punto 1): unit_count = unita operative (operazione eseguita x componente, vedi
            // OperationalSemantics.CountWorkUnits) per revisione; la Home somma queste invece di contare le REV.
            EnsureColumn(connection, transaction, "test_revision", "unit_count", "INTEGER NOT NULL DEFAULT 0");
            RecomputeOperationCounts(connection, transaction);
            // v7: aggiunge/valorizza origine_test per un indice v2..v6 già popolato, stesso principio di
            // EnsureRawCreationColumn/BackfillRawCreationTicks (v5) sopra: ALTER TABLE ADD COLUMN seguito da un
            // backfill dal JSON già in cache, nessuna riscrittura della fonte autorevole.
            EnsureOrigineTestColumn(connection, transaction);
            BackfillOrigineTest(connection, transaction);
            }
            // v10: proiezione operazioni guasto (idempotente: svuota e ricostruisce dalla cache JSON).
            // v11: colonna veicolo PRIMA della ricostruzione (un indice v10 ha la tabella senza la colonna).
            EnsureColumn(connection, transaction, "test_dtc_operation", "veicolo", "TEXT NOT NULL DEFAULT '' COLLATE NOCASE");
            BackfillDtcOperations(connection, transaction);
            // v12: colonne grezze dei filtri Lista Test "senza componente" e "riferimento Ticketing", valorizzate dal JSON in cache.
            EnsureColumn(connection, transaction, "test_summary", "ecu_grezzi", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, transaction, "test_summary", "ticket_collegati", "INTEGER NOT NULL DEFAULT 0");
            BackfillConteggiFiltroLista(connection, transaction);
            // v13: testo delle esecuzioni (note e deviazioni) per la ricerca testuale della Home, dal JSON gia' in cache.
            EnsureColumn(connection, transaction, "test_revision", "note", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, transaction, "test_revision", "deviazioni", "TEXT NOT NULL DEFAULT ''");
            BackfillNoteEsecuzioni(connection, transaction);
            // v14: proiezioni della Home a scala (Veicoli × esito senza JOIN, Test senza componente), dalle tabelle gia' presenti.
            EnsureColumn(connection, transaction, "test_vehicle", "eliminato", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, transaction, "test_vehicle", "origine_test", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, transaction, "test_vehicle", "data_creazione_ticks", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, transaction, "test_vehicle", "esito_home", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, transaction, "test_summary", "senza_ecu", "INTEGER NOT NULL DEFAULT 0");
            BackfillProiezioniHome(connection, transaction);
            // v15 (TIME): durata per operazione, dal JSON gia' in cache (vedi BackfillDurataOperazioni).
            EnsureColumn(connection, transaction, "test_operation_attempt", "duration_minutes", "INTEGER");
            BackfillDurataOperazioni(connection, transaction);
            RicostruisciSorgentiHomeGlobale(connection, transaction);
            PuntoInterruzioneMigrazione?.Invoke("v16:sorgenti-home");
            WriteMeta(connection, transaction, "schema_version", CurrentSchemaVersion.ToString());
            transaction.Commit();
        }

        EnsureHomeIndex(connection);

        // Contatore di revisione (P2.6): seme nel meta e trigger sulle tabelle di contenuto, nella stessa transazione.
        // Idempotente: ogni apertura in scrittura riallinea anche un indice creato prima del contatore.
        using var transazioneRevisione = connection.BeginTransaction();
        InstallaContatoreRevisione(connection, transazioneRevisione);
        transazioneRevisione.Commit();
    }

    /// <summary>Indice di copertura della query KPI Home: v8 sostituisce revision_count con executed_revision_count.
    /// Creato qui (non in CreateProjectionSchema) perche' su un indice v2..v7 la colonna esiste solo dopo la migrazione.
    /// v14: ix_test_vehicle_home copre la query Veicoli × esito con il solo periodo della Home (vedi GetSnapshot).
    /// ix_test_summary_origine (origine_test, v7) sta qui per la stessa ragione: CreateProjectionSchema gira prima dei passi di migrazione,
    /// e su un indice v2..v6 (o interrotto alla versione 5) la colonna non c'e' ancora.
    /// ix_operation_uid_revision accelera la query TIME ultima-occorrrenza per TestUid/CondizioneUid,
    /// senza cambiare schema_version né dati aziendali (è una cache SQLite locale).</summary>
    private static void EnsureHomeIndex(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_test_summary_origine ON test_summary(origine_test);
            DROP INDEX IF EXISTS ix_test_summary_home;
            CREATE INDEX IF NOT EXISTS ix_test_summary_home_v8 ON test_summary(
                eliminato, data_creazione_ticks, uid, current_outcome, executed_revision_count, first_conforme_revision);
            CREATE INDEX IF NOT EXISTS ix_test_vehicle_home ON test_vehicle(
                eliminato, origine_test, value COLLATE NOCASE, esito_home, data_creazione_ticks, test_uid);
            -- TIME Home: anti-join sull'ultima revisione di (Test UID, Condizione UID).
            -- Anche gli indici v16 esistenti lo ottengono qui senza riscrivere dati o proiezioni.
            CREATE INDEX IF NOT EXISTS ix_operation_uid_revision
                ON test_operation_attempt(test_uid, condition_uid, revision_index DESC);
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>v14: valorizza le colonne di proiezione Home dalle tabelle locali gia' presenti (test_summary e test_ecu), nella
    /// stessa transazione della migrazione. Nessuna lettura del JSON: test_vehicle copia da test_summary, come fa
    /// InsertVeicoli per i Test scritti da questo codice; senza_ecu e' la stessa condizione di NOT EXISTS su test_ecu.</summary>
    private static void BackfillProiezioniHome(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE test_summary SET senza_ecu = CASE WHEN EXISTS (SELECT 1 FROM test_ecu e WHERE e.test_uid = test_summary.uid) THEN 0 ELSE 1 END;
            UPDATE test_vehicle SET
                eliminato = (SELECT s.eliminato FROM test_summary s WHERE s.uid = test_vehicle.test_uid),
                origine_test = (SELECT s.origine_test FROM test_summary s WHERE s.uid = test_vehicle.test_uid),
                data_creazione_ticks = (SELECT s.data_creazione_ticks FROM test_summary s WHERE s.uid = test_vehicle.test_uid),
                esito_home = (SELECT COALESCE(s.current_outcome, {(int)Esito.NonEseguitoNA}) FROM test_summary s WHERE s.uid = test_vehicle.test_uid);
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string ddl)
    {
        using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = $"PRAGMA table_info({table});";
            using var reader = info.ExecuteReader();
            while (reader.Read())
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }
        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {ddl};";
        alter.ExecuteNonQuery();
    }

    /// <summary>v6 (decisione utente 6.4.0, punto 1): ricalcola SOLO i conteggi operativi già proiettati
    /// (operation_attempt_count/effective_operation_count su test_summary, raw_attempt_count/
    /// effective_operation_count su test_revision) applicando la nuova regola "solo operazioni eseguite".
    /// test_operation_attempt/test_operation_ecu (dati grezzi per-tentativo) restano invariati: non serve
    /// filtrarli qui, i consumer che li leggono direttamente (es. GetFunctionalityOperationStatistics) filtrano
    /// già sull'outcome alla lettura.</summary>
    private static void RecomputeOperationCounts(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<(string Uid, string Json)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT s.uid, c.json
                FROM test_summary s
                JOIN test_case_cache c ON c.path = s.path;
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        using var updateSummary = connection.CreateCommand();
        updateSummary.Transaction = transaction;
        updateSummary.CommandText = """
            UPDATE test_summary SET operation_attempt_count = $attempt, effective_operation_count = $effective,
                executed_revision_count = $executedRevisions, first_conforme_revision = $firstConforme
            WHERE uid = $uid;
            """;
        var summaryExecutedRevisions = updateSummary.Parameters.Add("$executedRevisions", SqliteType.Integer);
        var summaryFirstConforme = updateSummary.Parameters.Add("$firstConforme", SqliteType.Integer);
        var summaryAttempt = updateSummary.Parameters.Add("$attempt", SqliteType.Integer);
        var summaryEffective = updateSummary.Parameters.Add("$effective", SqliteType.Integer);
        var summaryUid = updateSummary.Parameters.Add("$uid", SqliteType.Text);
        updateSummary.Prepare();

        using var updateRevision = connection.CreateCommand();
        updateRevision.Transaction = transaction;
        updateRevision.CommandText = """
            UPDATE test_revision SET raw_attempt_count = $raw, effective_operation_count = $effective, origine = $origine, unit_count = $units
            WHERE test_uid = $uid AND revision_index = $index;
            """;
        var revisionOrigine = updateRevision.Parameters.Add("$origine", SqliteType.Integer);
        var revisionUnits = updateRevision.Parameters.Add("$units", SqliteType.Integer);
        var revisionRaw = updateRevision.Parameters.Add("$raw", SqliteType.Integer);
        var revisionEffective = updateRevision.Parameters.Add("$effective", SqliteType.Integer);
        var revisionUid = updateRevision.Parameters.Add("$uid", SqliteType.Text);
        var revisionIndex = updateRevision.Parameters.Add("$index", SqliteType.Integer);
        updateRevision.Prepare();

        foreach (var row in rows)
        {
            var tc = JsonIo.TryParse<TestCase>(row.Json, out _);
            if (tc == null) continue;
            // Punto unico condiviso con TestCaseRepository (review 6.4.0, punto 1): stessa interpretazione
            // del JSON legacy in entrambi i consumer, editor e proiezione SQLite non possono più divergere.
            TestCaseLegacyMigrations.MigraInLetturaDaTesto(tc, row.Json);

            var totalAttempts = 0;
            var totalEffective = 0;
            for (var revisionIdx = 0; revisionIdx < tc.Esecuzioni.Count; revisionIdx++)
            {
                var revision = tc.Esecuzioni[revisionIdx];
                var raw = revision.Condizioni.Count(c => c.Esito.EEseguito());
                var effective = revision.CondizioniEffettive.Count(c => c.Esito.EEseguito());
                totalAttempts += raw;
                totalEffective += effective;

                revisionRaw.Value = raw;
                revisionUnits.Value = OperationalSemantics.CountWorkUnits(tc, revision);
                revisionEffective.Value = effective;
                revisionOrigine.Value = (int)revision.Origine;
                revisionUid.Value = row.Uid;
                revisionIndex.Value = revisionIdx;
                updateRevision.ExecuteNonQuery();
            }

            summaryAttempt.Value = totalAttempts;
            summaryEffective.Value = totalEffective;
            summaryExecutedRevisions.Value = OperationalSemantics.RevisioniEseguite(tc);
            var firstConformeExecuted = OperationalSemantics.IterazioniEseguitePerConforme(tc);
            summaryFirstConforme.Value = firstConformeExecuted.HasValue ? firstConformeExecuted.Value : DBNull.Value;
            summaryUid.Value = row.Uid;
            updateSummary.ExecuteNonQuery();
        }
    }

    /// <summary>v7 (decisione utente 6.4.0, "Test importati: solo stato finale"): aggiunge la colonna
    /// origine_test a un test_summary v2..v6 già esistente. Idempotente: se la colonna è già presente (indice
    /// creato da zero, dove CreateProjectionSchema la include già) non fa nulla.</summary>
    private static void EnsureOrigineTestColumn(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var info = connection.CreateCommand();
        info.Transaction = transaction;
        info.CommandText = "PRAGMA table_info(test_summary);";
        using var reader = info.ExecuteReader();
        var exists = false;
        while (reader.Read())
            if (string.Equals(reader.GetString(1), "origine_test", StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        if (exists) return;

        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = "ALTER TABLE test_summary ADD COLUMN origine_test INTEGER NOT NULL DEFAULT 0;";
        alter.ExecuteNonQuery();
    }

    /// <summary>Valorizza origine_test dal JSON già presente in test_case_cache (nessuna scansione della
    /// cartella condivisa). Punto unico condiviso con RebuildProjection: entrambi leggono TestCase.Origine,
    /// mai un'inferenza separata dal Changelog (quella resta responsabilità esclusiva dell'harness esterno,
    /// vedi Models.TestCaseOrigine).</summary>
    private static void BackfillOrigineTest(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<(string Uid, string Json)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT s.uid, c.json
                FROM test_summary s
                JOIN test_case_cache c ON c.path = s.path;
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE test_summary SET origine_test = $origine WHERE uid = $uid;";
        var origineParam = update.Parameters.Add("$origine", SqliteType.Integer);
        var uidParam = update.Parameters.Add("$uid", SqliteType.Text);
        update.Prepare();

        foreach (var row in rows)
        {
            var tc = JsonIo.TryParse<TestCase>(row.Json, out _);
            origineParam.Value = tc != null ? (int)tc.Origine : 0;
            uidParam.Value = row.Uid;
            update.ExecuteNonQuery();
        }
    }

    /// <summary>Valorizza note e deviazioni delle esecuzioni dal JSON gia' in test_case_cache, per (test, indice della
    /// revisione): stessa numerazione della scrittura (posizione nella lista Esecuzioni). Nessuna scansione della cartella condivisa.</summary>
    private static void BackfillNoteEsecuzioni(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<(string Uid, string Json)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT s.uid, c.json
                FROM test_summary s
                JOIN test_case_cache c ON c.path = s.path;
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE test_revision SET note = $note, deviazioni = $deviazioni WHERE test_uid = $uid AND revision_index = $index;";
        var noteParam = update.Parameters.Add("$note", SqliteType.Text);
        var deviazioniParam = update.Parameters.Add("$deviazioni", SqliteType.Text);
        var uidParam = update.Parameters.Add("$uid", SqliteType.Text);
        var indexParam = update.Parameters.Add("$index", SqliteType.Integer);
        update.Prepare();

        foreach (var row in rows)
        {
            var tc = JsonIo.TryParse<TestCase>(row.Json, out _);
            if (tc == null) continue;
            uidParam.Value = row.Uid;
            for (var i = 0; i < tc.Esecuzioni.Count; i++)
            {
                noteParam.Value = tc.Esecuzioni[i].Note ?? "";
                deviazioniParam.Value = tc.Esecuzioni[i].Deviazioni ?? "";
                indexParam.Value = i;
                update.ExecuteNonQuery();
            }
        }
    }

    /// <summary>Valorizza ecu_grezzi e ticket_collegati dal JSON gia' presente in test_case_cache (nessuna scansione
    /// della cartella condivisa). Stessa regola del writer di test_summary: i conteggi sono quelli grezzi del TestCase.</summary>
    private static void BackfillConteggiFiltroLista(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<(string Uid, string Json)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT s.uid, c.json
                FROM test_summary s
                JOIN test_case_cache c ON c.path = s.path;
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE test_summary SET ecu_grezzi = $componente, ticket_collegati = $ticket WHERE uid = $uid;";
        var ecuParam = update.Parameters.Add("$componente", SqliteType.Integer);
        var ticketParam = update.Parameters.Add("$ticket", SqliteType.Integer);
        var uidParam = update.Parameters.Add("$uid", SqliteType.Text);
        update.Prepare();

        foreach (var row in rows)
        {
            var tc = JsonIo.TryParse<TestCase>(row.Json, out _);
            ecuParam.Value = tc?.Componente.Count ?? 0;
            ticketParam.Value = tc?.TicketCollegati.Count ?? 0;
            uidParam.Value = row.Uid;
            update.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Porta una cache v2/v3/v4 al passo v5 come un'unica unita' atomica. Campi custom, strumenti e data raw
    /// devono essere tutti completi prima di scrivere la versione intermedia 5 (non la corrente): un crash lascia
    /// la versione precedente e il riavvio ripete questo passo, poi quelli successivi.
    /// </summary>
    private static void MigrateProjectionToV5(SqliteConnection connection, string schemaRaw)
    {
        using var transaction = connection.BeginTransaction();
        if (schemaRaw == "2") BackfillCustomFields(connection, transaction);
        if (schemaRaw is "2" or "3") BackfillInstruments(connection, transaction);
        EnsureRawCreationColumn(connection, transaction);
        BackfillRawCreationTicks(connection, transaction);
        EnsureRawCreationIndex(connection, transaction);
        WriteMeta(connection, transaction, "schema_version", VersioneIntermediaV5);
        transaction.Commit();
    }

    private static void EnsureRawCreationColumn(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var info = connection.CreateCommand();
        info.Transaction = transaction;
        info.CommandText = "PRAGMA table_info(test_summary);";
        using var reader = info.ExecuteReader();
        var exists = false;
        while (reader.Read())
            if (string.Equals(reader.GetString(1), "data_creazione_raw_ticks", StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        if (exists) return;

        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = "ALTER TABLE test_summary ADD COLUMN data_creazione_raw_ticks INTEGER NOT NULL DEFAULT 0;";
        alter.ExecuteNonQuery();
    }

    private static void BackfillRawCreationTicks(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<(string Uid, long RawTicks)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT s.uid, c.json
                FROM test_summary s
                JOIN test_case_cache c ON c.path = s.path;
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(reader.GetString(1));
                    rows.Add((reader.GetString(0), document.RootElement
                        .GetProperty(nameof(TestCase.DataCreazione)).GetDateTime().Ticks));
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                {
                    throw new LocalIndexSchemaIncompatibleException(
                        $"Payload cache locale non compatibile con lo schema v5 (uid {reader.GetString(0)}): {ex.Message}");
                }
            }
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE test_summary SET data_creazione_raw_ticks = $ticks WHERE uid = $uid;";
        var ticks = update.Parameters.Add("$ticks", SqliteType.Integer);
        var uid = update.Parameters.Add("$uid", SqliteType.Text);
        update.Prepare();
        foreach (var row in rows)
        {
            ticks.Value = row.RawTicks;
            uid.Value = row.Uid;
            update.ExecuteNonQuery();
        }
    }

    private static void EnsureRawCreationIndex(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_test_summary_created_raw
            ON test_summary(eliminato, data_creazione_raw_ticks, uid);
            """;
        command.ExecuteNonQuery();
    }

    private static void BackfillCustomFields(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM test_custom_field;
            INSERT INTO test_custom_field(test_uid, field_key, value)
            SELECT s.uid, j.key, CAST(j.value AS TEXT)
            FROM test_summary s
            JOIN test_case_cache c ON c.path = s.path
            JOIN json_each(c.json, '$.CampiCustom') j
            WHERE j.type = 'text';
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Lo schema v4 aggiunge la proiezione degli strumenti. Come per i campi custom, il database e' una
    /// cache rigenerabile: per un indice v3 gia' popolato ricaviamo il dato dal JSON gia' presente, senza
    /// toccare la fonte autorevole ne' attendere una scansione completa della cartella condivisa.
    /// </summary>
    private static void BackfillInstruments(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM test_instrument;
            INSERT OR IGNORE INTO test_instrument(test_uid, value)
            SELECT s.uid, CAST(j.value AS TEXT)
            FROM test_summary s
            JOIN test_case_cache c ON c.path = s.path
            JOIN json_each(c.json, '$.StrumentiUsati') j
            WHERE j.type = 'text' AND TRIM(CAST(j.value AS TEXT)) <> '';
            """;
        command.ExecuteNonQuery();
    }
    private static void CreateProjectionSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS test_summary(
                uid TEXT PRIMARY KEY,
                path TEXT NOT NULL UNIQUE,
                id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                eliminato INTEGER NOT NULL,
                data_creazione_ticks INTEGER NOT NULL,
                data_creazione_raw_ticks INTEGER NOT NULL,
                ultima_modifica_ticks INTEGER NOT NULL,
                modificato_da TEXT NOT NULL,
                stato_lavoro INTEGER NOT NULL,
                test_template_uid TEXT NOT NULL,
                creato_da TEXT NOT NULL,
                assegnatario TEXT,
                descrizione_breve TEXT NOT NULL,
                descrizione TEXT NOT NULL,
                descrizione_problema TEXT,
                severita INTEGER,
                priorita INTEGER NOT NULL,
                current_outcome INTEGER,
                revision_count INTEGER NOT NULL,
                operation_attempt_count INTEGER NOT NULL,
                effective_operation_count INTEGER NOT NULL,
                first_conforme_revision INTEGER,
                origine_test INTEGER NOT NULL DEFAULT 0,
                executed_revision_count INTEGER NOT NULL DEFAULT 0,
                ecu_grezzi INTEGER NOT NULL DEFAULT 0,
                ticket_collegati INTEGER NOT NULL DEFAULT 0,
                senza_ecu INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_test_summary_id ON test_summary(id);
            CREATE INDEX IF NOT EXISTS ix_test_summary_eliminato ON test_summary(eliminato);
            CREATE INDEX IF NOT EXISTS ix_test_summary_stato ON test_summary(stato_lavoro);
            CREATE INDEX IF NOT EXISTS ix_test_summary_modificato ON test_summary(ultima_modifica_ticks DESC);
            CREATE INDEX IF NOT EXISTS ix_test_summary_assegnatario ON test_summary(assegnatario);
            CREATE INDEX IF NOT EXISTS ix_test_summary_template ON test_summary(test_template_uid);
            CREATE INDEX IF NOT EXISTS ix_test_summary_outcome ON test_summary(current_outcome);
            CREATE INDEX IF NOT EXISTS ix_test_summary_work_page ON test_summary(
                eliminato, stato_lavoro, ultima_modifica_ticks DESC, id COLLATE NOCASE, uid);
            CREATE INDEX IF NOT EXISTS ix_test_summary_ordine_archivio ON test_summary(
                eliminato, ultima_modifica_ticks DESC, id COLLATE NOCASE, uid);

            CREATE TABLE IF NOT EXISTS test_ecu(
                test_uid TEXT NOT NULL,
                value TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY(test_uid, value),
                FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_test_ecu_value ON test_ecu(value COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS test_vehicle(
                test_uid TEXT NOT NULL,
                value TEXT NOT NULL COLLATE NOCASE,
                eliminato INTEGER NOT NULL DEFAULT 0,
                origine_test INTEGER NOT NULL DEFAULT 0,
                data_creazione_ticks INTEGER NOT NULL DEFAULT 0,
                esito_home INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(test_uid, value),
                FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_test_vehicle_value ON test_vehicle(value COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS test_requirement(
                test_uid TEXT NOT NULL,
                value TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY(test_uid, value),
                FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_test_requirement_value ON test_requirement(value COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS test_functionality(
                test_uid TEXT NOT NULL,
                value TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY(test_uid, value),
                FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_test_functionality_value ON test_functionality(value COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS test_instrument(
                test_uid TEXT NOT NULL,
                value TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY(test_uid, value),
                FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_test_instrument_value ON test_instrument(value COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS test_custom_field(
                test_uid TEXT NOT NULL,
                field_key TEXT NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY(test_uid, field_key),
                FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_test_custom_field_values
                ON test_custom_field(field_key, value, test_uid);
            CREATE TABLE IF NOT EXISTS test_revision(
                test_uid TEXT NOT NULL,
                revision_index INTEGER NOT NULL,
                iteration_number INTEGER NOT NULL,
                data_ticks INTEGER NOT NULL,
                operatore TEXT NOT NULL,
                outcome INTEGER NOT NULL,
                calculated_outcome INTEGER NOT NULL,
                raw_attempt_count INTEGER NOT NULL,
                effective_operation_count INTEGER NOT NULL,
                origine INTEGER NOT NULL DEFAULT 0,
                unit_count INTEGER NOT NULL DEFAULT 0,
                note TEXT NOT NULL DEFAULT '',
                deviazioni TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(test_uid, revision_index),
                FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_test_revision_operator ON test_revision(operatore COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_test_revision_home_operator ON test_revision(test_uid, operatore);
            CREATE INDEX IF NOT EXISTS ix_test_revision_date ON test_revision(data_ticks);
            CREATE INDEX IF NOT EXISTS ix_test_revision_outcome ON test_revision(outcome);

            CREATE TABLE IF NOT EXISTS test_operation_attempt(
                test_uid TEXT NOT NULL,
                revision_index INTEGER NOT NULL,
                attempt_index INTEGER NOT NULL,
                global_attempt_index INTEGER NOT NULL,
                condition_uid TEXT NOT NULL,
                operation_root_uid TEXT NOT NULL,
                is_effective INTEGER NOT NULL,
                funzionalita TEXT NOT NULL COLLATE NOCASE,
                outcome INTEGER NOT NULL,
                operatore TEXT NOT NULL,
                data_ticks INTEGER NOT NULL,
                duration_minutes INTEGER,
                PRIMARY KEY(test_uid, revision_index, attempt_index),
                FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_operation_root ON test_operation_attempt(operation_root_uid);
            CREATE INDEX IF NOT EXISTS ix_operation_functionality ON test_operation_attempt(funzionalita COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_operation_operator ON test_operation_attempt(operatore COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_operation_outcome ON test_operation_attempt(outcome);
            CREATE INDEX IF NOT EXISTS ix_operation_date ON test_operation_attempt(data_ticks);

            CREATE TABLE IF NOT EXISTS test_dtc_operation(
                test_uid TEXT NOT NULL,
                revision_index INTEGER NOT NULL,
                attempt_index INTEGER NOT NULL,
                requisito TEXT NOT NULL COLLATE NOCASE,
                guasto TEXT NOT NULL COLLATE NOCASE,
                passo INTEGER NOT NULL,
                is_effective INTEGER NOT NULL,
                outcome INTEGER NOT NULL,
                data_ticks INTEGER NOT NULL,
                veicolo TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                PRIMARY KEY(test_uid, revision_index, attempt_index),
                FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_dtc_operation_key ON test_dtc_operation(requisito, guasto);

            CREATE TABLE IF NOT EXISTS test_operation_ecu(
                test_uid TEXT NOT NULL,
                revision_index INTEGER NOT NULL,
                attempt_index INTEGER NOT NULL,
                componente TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY(test_uid, revision_index, attempt_index, componente),
                FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_operation_ecu_value ON test_operation_ecu(componente COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS home_source_candidate(
                test_uid TEXT NOT NULL,
                funzionalita TEXT NOT NULL COLLATE NOCASE,
                componente TEXT NOT NULL COLLATE NOCASE,
                veicolo TEXT NOT NULL COLLATE NOCASE,
                id TEXT NOT NULL,
                outcome INTEGER,
                data_ticks INTEGER NOT NULL,
                revision_index INTEGER NOT NULL,
                attempt_index INTEGER NOT NULL,
                eseguito INTEGER NOT NULL,
                previsto INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_home_source_candidate_core ON home_source_candidate(funzionalita, componente, veicolo);
            CREATE INDEX IF NOT EXISTS ix_home_source_candidate_uid ON home_source_candidate(test_uid);
            -- Copertura del vincitore di partizione (SqlVincitoreDiPartizioneHome): partizione + ordine di SqlVincitoreDiPartizioneHome,
            -- poi le colonne lette. Senza, ogni ricalcolo ordina i candidati della partizione (USE TEMP B-TREE). Creato a ogni apertura
            -- (IF NOT EXISTS): copre DB nuovi, migrazione 15->16 e DB v16 scritti dalla build precedente senza questo indice.
            CREATE INDEX IF NOT EXISTS ix_home_source_candidate_winner ON home_source_candidate(
                funzionalita, componente, veicolo, eseguito DESC, previsto DESC, data_ticks DESC, id COLLATE BINARY DESC,
                revision_index DESC, attempt_index DESC, outcome);
            CREATE TABLE IF NOT EXISTS home_source_winner(
                funzionalita TEXT NOT NULL COLLATE NOCASE,
                componente TEXT NOT NULL COLLATE NOCASE,
                veicolo TEXT NOT NULL COLLATE NOCASE,
                outcome INTEGER,
                data_ticks INTEGER NOT NULL,
                id TEXT NOT NULL,
                revision_index INTEGER NOT NULL,
                attempt_index INTEGER NOT NULL,
                PRIMARY KEY(funzionalita, componente, veicolo)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS home_source_dirty(
                test_uid TEXT PRIMARY KEY
            ) WITHOUT ROWID;
            """;
        command.ExecuteNonQuery();
    }

    private void EnsureFtsSchema(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS test_search USING fts5(
                    uid UNINDEXED,
                    id,
                    descrizione_breve,
                    descrizione,
                    assegnatario,
                    creato_da,
                    requisiti,
                    funzionalita,
                    componente,
                    veicoli,
                    tokenize='unicode61 remove_diacritics 2'
                );
                """;
            command.ExecuteNonQuery();
            using var tx = connection.BeginTransaction();
            WriteMeta(connection, tx, FtsEnabledKey, "1");
            tx.Commit();
        }
        catch (SqliteException ex) when (!LocalIndexFaultClassifier.IsCorruption(ex))
        {
            // FTS5 assente nella build SQLite: si degrada la sola ricerca. Una CORRUZIONE qui non va invece
            // mascherata come "FTS non disponibile", altrimenti si prosegue su un database illeggibile.
            using var tx = connection.BeginTransaction();
            WriteMeta(connection, tx, FtsEnabledKey, "0");
            tx.Commit();
        }
    }

    private void BackfillProjectionFromCache(SqliteConnection connection)
    {
        var rows = new List<(string Path, string Json)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT path, json FROM test_case_cache ORDER BY path;";
            using var reader = read.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        }
        if (rows.Count == 0) return;

        using var transaction = connection.BeginTransaction();
        foreach (var row in rows)
        {
            var tc = JsonIo.TryParse<TestCase>(row.Json, out _);
            if (tc == null) continue;
            tc.NormalizzaVeicoli();
            RebuildProjection(connection, transaction, row.Path, tc);
        }
        AllineaSorgentiHome(connection, transaction);
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static SqliteCommand CreateUpsertCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO test_case_cache(
                path, uid, id, revision, eliminato, mtime_utc_ticks, file_length, content_hash, json, indexed_utc_ticks)
            VALUES(
                $path, $uid, $id, $revision, $eliminato, $mtime, $length, $hash, $json, $indexed)
            ON CONFLICT(path) DO UPDATE SET
                uid = excluded.uid,
                id = excluded.id,
                revision = excluded.revision,
                eliminato = excluded.eliminato,
                mtime_utc_ticks = excluded.mtime_utc_ticks,
                file_length = excluded.file_length,
                content_hash = excluded.content_hash,
                json = excluded.json,
                indexed_utc_ticks = excluded.indexed_utc_ticks;
            """;
        command.Parameters.Add("$path", SqliteType.Text);
        command.Parameters.Add("$uid", SqliteType.Text);
        command.Parameters.Add("$id", SqliteType.Text);
        command.Parameters.Add("$revision", SqliteType.Integer);
        command.Parameters.Add("$eliminato", SqliteType.Integer);
        command.Parameters.Add("$mtime", SqliteType.Integer);
        command.Parameters.Add("$length", SqliteType.Integer);
        command.Parameters.Add("$hash", SqliteType.Text);
        command.Parameters.Add("$json", SqliteType.Text);
        command.Parameters.Add("$indexed", SqliteType.Integer);
        return command;
    }

    private static string BindUpsert(
        SqliteCommand command,
        string path,
        DateTime mtimeUtc,
        long fileLength,
        TestCase testCase,
        string? precomputedHash = null)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(testCase, JsonIo.CompactOptions);
        var hash = precomputedHash ?? TestCaseFingerprint.Compute(testCase);
        command.Parameters["$path"].Value = path;
        command.Parameters["$uid"].Value = testCase.Uid.ToString("N");
        command.Parameters["$id"].Value = testCase.Id ?? "";
        command.Parameters["$revision"].Value = testCase.RevisioneDati;
        command.Parameters["$eliminato"].Value = testCase.Eliminato ? 1 : 0;
        command.Parameters["$mtime"].Value = mtimeUtc.ToUniversalTime().Ticks;
        command.Parameters["$length"].Value = fileLength;
        command.Parameters["$hash"].Value = hash;
        command.Parameters["$json"].Value = json;
        command.Parameters["$indexed"].Value = DateTime.UtcNow.Ticks;
        return hash;
    }

    private void RebuildProjection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string path,
        TestCase tc,
        bool deleteExisting = true,
        bool? ftsEnabledOverride = null,
        HashSet<string>? uidProiettati = null,
        ComandiProiezione? comandi = null,
        bool riusaComandi = true)
    {
        // Senza comandi condivisi (batch incrementali, migrazioni) la proiezione di questo Test usa comandi propri.
        using var comandiLocali = comandi == null ? new ComandiProiezione(connection, transaction, riusaComandi) : null;
        comandi ??= comandiLocali!;

        // Migrazione in lettura 6.4.0 punto 1 (vedi TestCaseLegacyMigrations, punto unico condiviso con
        // TestCaseRepository): clona PRIMA di applicarla, per non mutare l'oggetto del chiamante — che può
        // essere lo stesso TestCase vivo nella cache di TestCaseRepository — la correzione resta confinata a
        // questa sola proiezione locale ricostruibile, mai al JSON autorevole. Path-based (rilegge il file
        // originale solo se davvero serve, cioè SchemaVersion legacy) per poter rilevare anche il gap teorico
        // "Esito assente dal JSON", impossibile ricostruire dal solo tc già deserializzato. Guardia su
        // StatoLavoro per non pagare una clonazione extra su ogni singolo Test in una scansione completa:
        // solo i Pianificati (piccola minoranza, dataset di riferimento 7 su 507) entrano nel ramo lento.
        if (tc.StatoLavoro == StatoLavoroTest.Pianificato)
        {
            var clone = JsonIo.TryParse<TestCase>(System.Text.Json.JsonSerializer.Serialize(tc, JsonIo.CompactOptions), out _);
            if (clone != null)
            {
                TestCaseLegacyMigrations.MigraInLettura(clone, path);
                tc = clone;
            }
        }

        var uid = tc.Uid.ToString("N");
        var ftsEnabled = ftsEnabledOverride ?? string.Equals(ReadMeta(connection, FtsEnabledKey), "1", StringComparison.Ordinal);
        if (deleteExisting)
        {
            DeleteProjection(connection, transaction, uid, ftsEnabled);
            uidProiettati?.Remove(uid);
        }
        // Un path puo' cambiare Uid (file riparato o sostituito): la proiezione del vecchio Uid va rimossa prima
        // dell'inserimento, altrimenti UNIQUE(test_summary.path) fa fallire l'intera riconciliazione. Le tabelle
        // figlie cadono in cascata su test_summary (ON DELETE CASCADE).
        RimuoviProiezioneDiPathConAltroUid(connection, transaction, path, uid, ftsEnabled, uidProiettati, comandi);

        // Decisione utente 6.4.0, punto 1: solo le operazioni realmente eseguite (Esito.EEseguito()) contano
        // come unità operative — "Da eseguire"/N.A. restano fuori da questi due conteggi (e dai loro
        // equivalenti per-revisione su test_revision, più sotto), qui e in LocalOperationalStatisticsService.
        var rawAttempts = tc.Esecuzioni.Sum(e => e.Condizioni.Count(c => c.Esito.EEseguito()));
        var effectiveOperations = tc.Esecuzioni.Sum(e => e.CondizioniEffettive.Count(c => c.Esito.EEseguito()));
        // Decisione utente 6.4.8, punto 5: solo le REV realmente eseguite sono iterazioni/revisioni; la
        // pianificazione non conta. first_conforme_revision e' la posizione tra le sole REV eseguite.
        var executedRevisions = OperationalSemantics.RevisioniEseguite(tc);
        var firstConformeExecuted = OperationalSemantics.IterazioniEseguitePerConforme(tc);

        {
            var summary = comandi.Riepilogo;
            summary.Parameters["$uid"].Value = uid;
            summary.Parameters["$path"].Value = path;
            summary.Parameters["$id"].Value = tc.Id ?? "";
            summary.Parameters["$revision"].Value = tc.RevisioneDati;
            summary.Parameters["$eliminato"].Value = tc.Eliminato ? 1 : 0;
            summary.Parameters["$creato"].Value = tc.DataCreazione.ToUniversalTime().Ticks;
            summary.Parameters["$creatoRaw"].Value = tc.DataCreazione.Ticks;
            summary.Parameters["$modificato"].Value = tc.UltimaModifica.ToUniversalTime().Ticks;
            summary.Parameters["$modificatoDa"].Value = tc.ModificatoDa ?? "";
            summary.Parameters["$stato"].Value = (int)tc.StatoLavoro;
            summary.Parameters["$template"].Value = tc.TestTemplateUid.ToString("N");
            summary.Parameters["$creatoDa"].Value = tc.CreatoDa ?? "";
            summary.Parameters["$assegnatario"].Value = (object?)tc.Assegnatario ?? DBNull.Value;
            summary.Parameters["$breve"].Value = tc.DescrizioneBreve ?? "";
            summary.Parameters["$descrizione"].Value = tc.Descrizione ?? "";
            summary.Parameters["$problema"].Value = (object?)tc.DescrizioneProblema ?? DBNull.Value;
            summary.Parameters["$severita"].Value = tc.Severita.HasValue ? (int)tc.Severita.Value : DBNull.Value;
            summary.Parameters["$priorita"].Value = (int)tc.Priorita;
            summary.Parameters["$outcome"].Value = tc.StatoCorrente.HasValue ? (int)tc.StatoCorrente.Value : DBNull.Value;
            summary.Parameters["$revisionCount"].Value = tc.Esecuzioni.Count;
            summary.Parameters["$attemptCount"].Value = rawAttempts;
            summary.Parameters["$effectiveCount"].Value = effectiveOperations;
            summary.Parameters["$firstConforme"].Value = firstConformeExecuted.HasValue ? firstConformeExecuted.Value : DBNull.Value;
            summary.Parameters["$executedRevisions"].Value = executedRevisions;
            summary.Parameters["$ecuGrezzi"].Value = tc.Componente.Count;
            summary.Parameters["$ticketCollegati"].Value = tc.TicketCollegati.Count;
            summary.Parameters["$senzaEcu"].Value = ValoriDistinti(tc.Componente).Count == 0 ? 1 : 0;
            summary.Parameters["$origine"].Value = (int)tc.Origine;
            summary.ExecuteNonQuery();
            uidProiettati?.Add(uid);
        }

        InsertValues(comandi, "test_ecu", uid, tc.Componente);
        InsertVeicoli(comandi, uid, tc.VeicoliEffettivi);
        InsertValues(comandi, "test_requirement", uid, tc.RequisitoId);
        InsertValues(comandi, "test_functionality", uid, tc.Funzionalita);
        InsertValues(comandi, "test_instrument", uid, tc.StrumentiUsati);
        {
            var campiValorizzati = tc.CampiCustom.Count(field => field.Value != null);
            if (campiValorizzati > 0)
            {
                var custom = comandi.CampoCustom(campiValorizzati);
                custom.Parameters["$uid"].Value = uid;
                foreach (var field in tc.CampiCustom)
                {
                    if (field.Value == null) continue;
                    custom.Parameters["$key"].Value = field.Key;
                    custom.Parameters["$value"].Value = field.Value;
                    custom.ExecuteNonQuery();
                }
            }
        }

        var globalAttempt = 0;
        for (var revisionIndex = 0; revisionIndex < tc.Esecuzioni.Count; revisionIndex++)
        {
            var revision = tc.Esecuzioni[revisionIndex];
            var effectiveUids = revision.CondizioniEffettive.Select(c => c.Uid).ToHashSet();

            {
                var revisionCommand = comandi.Revisione;
                revisionCommand.Parameters["$uid"].Value = uid;
                revisionCommand.Parameters["$index"].Value = revisionIndex;
                revisionCommand.Parameters["$iteration"].Value = revision.Iterazione;
                revisionCommand.Parameters["$data"].Value = revision.Data.ToUniversalTime().Ticks;
                revisionCommand.Parameters["$operator"].Value = revision.Operatore ?? "";
                revisionCommand.Parameters["$outcome"].Value = (int)revision.Esito;
                revisionCommand.Parameters["$calculated"].Value = (int)revision.EsitoCalcolato;
                revisionCommand.Parameters["$raw"].Value = revision.Condizioni.Count(c => c.Esito.EEseguito());
                revisionCommand.Parameters["$effective"].Value = revision.CondizioniEffettive.Count(c => c.Esito.EEseguito());
                revisionCommand.Parameters["$origine"].Value = (int)revision.Origine;
                revisionCommand.Parameters["$units"].Value = OperationalSemantics.CountWorkUnits(tc, revision);
                revisionCommand.Parameters["$note"].Value = revision.Note ?? "";
                revisionCommand.Parameters["$deviazioni"].Value = revision.Deviazioni ?? "";
                revisionCommand.ExecuteNonQuery();
            }

            for (var attemptIndex = 0; attemptIndex < revision.Condizioni.Count; attemptIndex++)
            {
                var condition = revision.Condizioni[attemptIndex];
                globalAttempt++;
                {
                    var attempt = comandi.Tentativo;
                    attempt.Parameters["$uid"].Value = uid;
                    attempt.Parameters["$revision"].Value = revisionIndex;
                    attempt.Parameters["$attempt"].Value = attemptIndex;
                    attempt.Parameters["$global"].Value = globalAttempt;
                    attempt.Parameters["$conditionUid"].Value = condition.Uid.ToString("N");
                    attempt.Parameters["$rootUid"].Value = (condition.OperazioneOrigineUid ?? condition.Uid).ToString("N");
                    attempt.Parameters["$effective"].Value = effectiveUids.Contains(condition.Uid) ? 1 : 0;
                    attempt.Parameters["$functionality"].Value = condition.Funzionalita ?? "";
                    attempt.Parameters["$outcome"].Value = (int)condition.Esito;
                    attempt.Parameters["$operator"].Value = revision.Operatore ?? "";
                    attempt.Parameters["$data"].Value = revision.Data.ToUniversalTime().Ticks;
                    // v15 (TIME): durata manuale dell'operazione; null = non nota, mai inventata.
                    attempt.Parameters["$durata"].Value = (object?)condition.DurataMinuti ?? DBNull.Value;
                    attempt.ExecuteNonQuery();
                }

                foreach (var componente in condition.Componente.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var operationEcu = comandi.OperazioneEcu;
                    operationEcu.Parameters["$uid"].Value = uid;
                    operationEcu.Parameters["$revision"].Value = revisionIndex;
                    operationEcu.Parameters["$attempt"].Value = attemptIndex;
                    operationEcu.Parameters["$componente"].Value = componente;
                    operationEcu.ExecuteNonQuery();
                }
            }
        }

        InsertDtcOperations(connection, transaction, uid, tc);
        UpsertFts(comandi, tc, ftsEnabled);
        MarcaSorgentiHomeDirty(connection, transaction, uid);
    }

    /// <summary>Proietta le operazioni con Guasto valorizzato (6.5.0, punto 7) in test_dtc_operation. Nessun costo
    /// per i Test senza operazioni guasto (un solo controllo per Condizione, nessuna query).</summary>
    private static void InsertDtcOperations(SqliteConnection connection, SqliteTransaction transaction, string uid, TestCase tc)
    {
        SqliteCommand? command = null;
        try
        {
            for (var revisionIndex = 0; revisionIndex < tc.Esecuzioni.Count; revisionIndex++)
            {
                var revision = tc.Esecuzioni[revisionIndex];
                HashSet<Guid>? effectiveUids = null;
                for (var attemptIndex = 0; attemptIndex < revision.Condizioni.Count; attemptIndex++)
                {
                    var c = revision.Condizioni[attemptIndex];
                    if (!c.RiguardaDtc) continue;
                    effectiveUids ??= revision.CondizioniEffettive.Select(x => x.Uid).ToHashSet();
                    if (command == null)
                    {
                        command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = """
                            INSERT OR REPLACE INTO test_dtc_operation(
                                test_uid, revision_index, attempt_index, requisito, guasto, passo, is_effective, outcome, data_ticks, veicolo)
                            VALUES($uid, $revision, $attempt, $req, $guasto, $passo, $eff, $outcome, $data, $sv);
                            """;
                        command.Parameters.Add("$uid", SqliteType.Text);
                        command.Parameters.Add("$revision", SqliteType.Integer);
                        command.Parameters.Add("$attempt", SqliteType.Integer);
                        command.Parameters.Add("$req", SqliteType.Text);
                        command.Parameters.Add("$guasto", SqliteType.Text);
                        command.Parameters.Add("$passo", SqliteType.Integer);
                        command.Parameters.Add("$eff", SqliteType.Integer);
                        command.Parameters.Add("$outcome", SqliteType.Integer);
                        command.Parameters.Add("$data", SqliteType.Integer);
                        command.Parameters.Add("$sv", SqliteType.Text);
                    }
                    command.Parameters["$uid"].Value = uid;
                    command.Parameters["$revision"].Value = revisionIndex;
                    command.Parameters["$attempt"].Value = attemptIndex;
                    command.Parameters["$req"].Value = (c.DtcRequisitoId ?? "").Trim();
                    command.Parameters["$guasto"].Value = c.Guasto!.Trim();
                    command.Parameters["$passo"].Value = (int)(c.DtcPasso ?? PassoDtc.Validazione);
                    command.Parameters["$eff"].Value = effectiveUids.Contains(c.Uid) ? 1 : 0;
                    command.Parameters["$outcome"].Value = (int)c.Esito;
                    command.Parameters["$data"].Value = revision.Data.ToUniversalTime().Ticks;
                    command.Parameters["$sv"].Value = DtcAvanzamentoService.VeicoloOperazione(tc, c);
                    command.ExecuteNonQuery();
                }
            }
        }
        finally { command?.Dispose(); }
    }

    /// <summary>Migrazione v10/v11: ricostruisce test_dtc_operation (con veicolo) dalla cache JSON. Filtro LIKE '"Guasto"' (la proprieta
    /// e scritta solo quando valorizzata): i Test senza guasto non vengono nemmeno deserializzati.</summary>
    /// <summary>v15 (TIME): copia la durata manuale delle operazioni dal JSON gia' in test_case_cache alle righe
    /// test_operation_attempt. La chiave e' quella di RebuildProjection (Uid del Test, indice di revisione, indice di
    /// tentativo su tutte le Condizioni della revisione). Legge a lotti (keyset su path) solo i Test che contengono la
    /// chiave riservata: un indice senza durate non viene rielaborato, e nessun lettore resta aperto mentre si scrive.
    /// Nessuna rilettura della cartella condivisa.</summary>
    private static void BackfillDurataOperazioni(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE test_operation_attempt SET duration_minutes = $durata
            WHERE test_uid = $uid AND revision_index = $revisione AND attempt_index = $tentativo;
            """;
        var durata = update.Parameters.Add("$durata", SqliteType.Integer);
        var uid = update.Parameters.Add("$uid", SqliteType.Text);
        var revisione = update.Parameters.Add("$revisione", SqliteType.Integer);
        var tentativo = update.Parameters.Add("$tentativo", SqliteType.Integer);

        var ultimoPath = "";
        while (true)
        {
            var lotto = new List<string>();
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = """
                    SELECT path, json FROM test_case_cache
                    WHERE path > $ultimo AND instr(json, $chiave) > 0
                    ORDER BY path LIMIT $dimensione;
                    """;
                read.Parameters.AddWithValue("$ultimo", ultimoPath);
                read.Parameters.AddWithValue("$chiave", DurataOperazione.ChiaveValoriStrumenti);
                read.Parameters.AddWithValue("$dimensione", LoadBatchSize);
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    ultimoPath = reader.GetString(0);
                    lotto.Add(reader.GetString(1));
                }
            }
            if (lotto.Count == 0) return;

            foreach (var json in lotto)
            {
                var tc = JsonIo.TryParse<TestCase>(json, out _);
                if (tc == null) continue;
                var testUid = tc.Uid.ToString("N");
                for (var r = 0; r < tc.Esecuzioni.Count; r++)
                {
                    var condizioni = tc.Esecuzioni[r].Condizioni;
                    for (var t = 0; t < condizioni.Count; t++)
                    {
                        if (condizioni[t].DurataMinuti is not { } minuti) continue;
                        durata.Value = minuti;
                        uid.Value = testUid;
                        revisione.Value = r;
                        tentativo.Value = t;
                        update.ExecuteNonQuery();
                    }
                }
            }
        }
    }

    private static void BackfillDtcOperations(SqliteConnection connection, SqliteTransaction transaction)
    {
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM test_dtc_operation;";
            clear.ExecuteNonQuery();
        }
        var rows = new List<(string Uid, string Json)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT s.uid, c.json
                FROM test_summary s
                JOIN test_case_cache c ON c.path = s.path
                WHERE c.json LIKE '%"Guasto"%';
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var row in rows)
        {
            var tc = JsonIo.TryParse<TestCase>(row.Json, out _);
            if (tc != null) InsertDtcOperations(connection, transaction, row.Uid, tc);
        }
    }

    /// <summary>Valori da proiettare: senza vuoti e spazi, senza duplicati (la chiave NOCASE della tabella li unirebbe comunque).
    /// Punto unico per InsertValues, InsertVeicoli e senza_ecu: il conteggio Test senza componente dipende da questa stessa regola.</summary>
    private static List<string> ValoriDistinti(IEnumerable<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void InsertValues(ComandiProiezione comandi, string table, string uid, IEnumerable<string> values)
    {
        var distinct = ValoriDistinti(values);
        if (distinct.Count == 0) return;
        var command = comandi.Valori(table, distinct.Count);
        command.Parameters["$uid"].Value = uid;
        var valueParameter = command.Parameters["$value"];
        foreach (var value in distinct)
        {
            valueParameter.Value = value;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Veicoli del Test: come InsertValues, ma ogni riga porta le copie di test_summary della proiezione Home (v14),
    /// lette dalla riga appena scritta nella stessa transazione (ComandiProiezione.Veicolo).</summary>
    private static void InsertVeicoli(ComandiProiezione comandi, string uid, IEnumerable<string> veicoli)
    {
        var distinct = ValoriDistinti(veicoli);
        if (distinct.Count == 0) return;
        var command = comandi.Veicolo(distinct.Count);
        command.Parameters["$uid"].Value = uid;
        var valueParameter = command.Parameters["$value"];
        foreach (var value in distinct)
        {
            valueParameter.Value = value;
            command.ExecuteNonQuery();
        }
    }

    private static void UpsertFts(ComandiProiezione comandi, TestCase tc, bool ftsEnabled)
    {
        if (!ftsEnabled) return;
        var uid = tc.Uid.ToString("N");
        try
        {
            var insert = comandi.Fts;
            insert.Parameters["$uid"].Value = uid;
            insert.Parameters["$id"].Value = tc.Id ?? "";
            insert.Parameters["$breve"].Value = tc.DescrizioneBreve ?? "";
            insert.Parameters["$descrizione"].Value = tc.Descrizione ?? "";
            insert.Parameters["$assegnatario"].Value = tc.Assegnatario ?? "";
            insert.Parameters["$creatoDa"].Value = tc.CreatoDa ?? "";
            insert.Parameters["$requisiti"].Value = string.Join(' ', tc.RequisitoId);
            insert.Parameters["$funzionalita"].Value = string.Join(' ', tc.Funzionalita);
            insert.Parameters["$componente"].Value = string.Join(" ", (IEnumerable<string>)tc.Componente);
            insert.Parameters["$veicoli"].Value = string.Join(' ', tc.VeicoliEffettivi);
            insert.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            CrashLogService.Registra("Indice SQLite TestCase: aggiornamento FTS", ex);
        }
    }

    /// <summary>Rimuove la proiezione dei Uid DIVERSI da quello attuale che occupano ancora la stessa path (file riparato o
    /// sostituito con un altro Uid): il vincolo UNIQUE su test_summary.path lo richiede prima dell'inserimento.</summary>
    /// <summary>Rimuove gli indici secondari delle tabelle di cache e di proiezione e restituisce il loro SQL originale,
    /// per ricrearli identici a fine caricamento completo (stessa transazione).</summary>
    private static List<string> SospendiIndiciSecondari(SqliteConnection connection, SqliteTransaction transaction)
    {
        var indici = new List<(string Nome, string Definizione)>();
        using (var elenco = connection.CreateCommand())
        {
            elenco.Transaction = transaction;
            elenco.CommandText = """
                SELECT name, sql FROM sqlite_master
                WHERE type = 'index' AND sql IS NOT NULL
                  AND tbl_name IN ('test_case_cache', 'test_summary', 'test_ecu', 'test_vehicle', 'test_requirement',
                                   'test_functionality', 'test_instrument', 'test_custom_field', 'test_revision',
                                   'test_operation_attempt', 'test_operation_ecu', 'test_dtc_operation');
                """;
            using var lettore = elenco.ExecuteReader();
            while (lettore.Read()) indici.Add((lettore.GetString(0), lettore.GetString(1)));
        }
        foreach (var (nome, _) in indici)
        {
            using var drop = connection.CreateCommand();
            drop.Transaction = transaction;
            drop.CommandText = $"DROP INDEX \"{nome}\";";
            drop.ExecuteNonQuery();
        }
        return indici.Select(i => i.Definizione).ToList();
    }

    private static void RipristinaIndiciSecondari(SqliteConnection connection, SqliteTransaction transaction, List<string> definizioni)
    {
        foreach (var definizione in definizioni)
        {
            using var create = connection.CreateCommand();
            create.Transaction = transaction;
            create.CommandText = definizione;
            create.ExecuteNonQuery();
        }
    }

    private void RimuoviProiezioneDiPathConAltroUid(SqliteConnection connection, SqliteTransaction transaction,
        string path, string uid, bool ftsEnabled, HashSet<string>? uidProiettati, ComandiProiezione comandi)
    {
        var vecchi = new List<string>();
        {
            var cerca = comandi.CercaAltroUid;
            cerca.Parameters["$path"].Value = path;
            cerca.Parameters["$uid"].Value = uid;
            using var lettore = cerca.ExecuteReader();
            while (lettore.Read()) vecchi.Add(lettore.GetString(0));
        }
        foreach (var vecchio in vecchi)
        {
            DeleteProjection(connection, transaction, vecchio, ftsEnabled);
            uidProiettati?.Remove(vecchio);
        }
    }

    private void DeleteProjection(SqliteConnection connection, SqliteTransaction transaction, string uid, bool? ftsEnabledOverride = null)
    {
        var ftsEnabled = ftsEnabledOverride ?? string.Equals(ReadMeta(connection, FtsEnabledKey), "1", StringComparison.Ordinal);
        if (ftsEnabled)
        {
            try
            {
                using var fts = connection.CreateCommand();
                fts.Transaction = transaction;
                fts.CommandText = "DELETE FROM test_search WHERE uid = $uid;";
                fts.Parameters.AddWithValue("$uid", uid);
                fts.ExecuteNonQuery();
            }
            catch (SqliteException) { }
        }

        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM test_summary WHERE uid = $uid;";
        delete.Parameters.AddWithValue("$uid", uid);
        delete.ExecuteNonQuery();
        MarcaSorgentiHomeDirty(connection, transaction, uid);
    }

    private void RemoveProjectionIfOrphan(SqliteConnection connection, SqliteTransaction transaction, string uid)
    {
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM test_case_cache WHERE uid = $uid;";
        count.Parameters.AddWithValue("$uid", uid);
        if (Convert.ToInt32(count.ExecuteScalar()) == 0) DeleteProjection(connection, transaction, uid);
    }

    private void RemoveAllOrphanProjections(SqliteConnection connection, SqliteTransaction transaction)
    {
        var orphanUids = new List<string>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT s.uid FROM test_summary s
                WHERE NOT EXISTS (SELECT 1 FROM test_case_cache c WHERE c.uid = s.uid);
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read()) orphanUids.Add(reader.GetString(0));
        }
        foreach (var uid in orphanUids) DeleteProjection(connection, transaction, uid);
    }

    private static string? ReadUidForPath(SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT uid FROM test_case_cache WHERE path = $path LIMIT 1;";
        command.Parameters.AddWithValue("$path", path);
        return command.ExecuteScalar() as string;
    }

    private static string? ReadHashForPath(SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT content_hash FROM test_case_cache WHERE path = $path LIMIT 1;";
        command.Parameters.AddWithValue("$path", path);
        return command.ExecuteScalar() as string;
    }

    private static bool ProjectionExists(SqliteConnection connection, SqliteTransaction transaction, Guid uid)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM test_summary WHERE uid = $uid LIMIT 1;";
        command.Parameters.AddWithValue("$uid", uid.ToString("N"));
        return command.ExecuteScalar() != null;
    }

    /// <summary>Stato di una riga di test_case_cache per la riconciliazione per differenza.</summary>
    private readonly record struct PathStamp(string Hash, long Mtime, long Length, string Uid);

    private static Dictionary<string, PathStamp> LoadPathStamps(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT path, content_hash, mtime_utc_ticks, file_length, uid FROM test_case_cache;";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, PathStamp>(StringComparer.Ordinal);
        while (reader.Read())
            result[reader.GetString(0)] = new PathStamp(reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4));
        return result;
    }

    private static HashSet<string> LoadProjectedUids(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT uid FROM test_summary;";
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private string? ReadMeta(string key)
    {
        using var connection = Open();
        return ReadMeta(connection, key);
    }

    /// <summary>Tabelle di contenuto lette dalle cache di lettura (P2.6): ogni scrittura su una di esse fa avanzare la revisione.</summary>
    private static readonly string[] TabelleContenutoRevisione =
    [
        "test_case_cache", "test_summary", "test_ecu", "test_vehicle", "test_requirement", "test_functionality",
        "test_instrument", "test_custom_field", "test_revision", "test_operation_attempt", "test_dtc_operation",
        "test_operation_ecu", "home_source_candidate", "home_source_winner", "home_source_dirty"
    ];

    /// <summary>
    /// Contatore di revisione dell'indice (P2.6). Il seme nel meta usa i ticks di creazione, cosi' due indici diversi non
    /// partono mai dallo stesso valore. I trigger AFTER INSERT/UPDATE/DELETE incrementano il contatore dentro la stessa
    /// transazione della scrittura che li attiva, anche per le scritture in autocommit: nessun punto di chiamata da
    /// ricordare, nessuna finestra in cui il dato cambia senza che la revisione avanzi. Idempotente.
    /// </summary>
    internal static void InstallaContatoreRevisione(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<string>? tabelle = null)
    {
        using (var seme = connection.CreateCommand())
        {
            seme.Transaction = transaction;
            seme.CommandText = $"INSERT OR IGNORE INTO meta(key, value) VALUES('{LocalIndexSnapshot.ChiaveRevisione}', $seme);";
            seme.Parameters.AddWithValue("$seme", DateTime.UtcNow.Ticks);
            seme.ExecuteNonQuery();
        }

        var trigger = new List<string>();
        foreach (var tabella in tabelle ?? TabelleContenutoRevisione)
        {
            trigger.Add($"CREATE TRIGGER IF NOT EXISTS trg_revisione_{tabella}_ai AFTER INSERT ON {tabella} BEGIN UPDATE meta SET value = CAST(value AS INTEGER) + 1 WHERE key = '{LocalIndexSnapshot.ChiaveRevisione}'; END;");
            trigger.Add($"CREATE TRIGGER IF NOT EXISTS trg_revisione_{tabella}_au AFTER UPDATE ON {tabella} BEGIN UPDATE meta SET value = CAST(value AS INTEGER) + 1 WHERE key = '{LocalIndexSnapshot.ChiaveRevisione}'; END;");
            trigger.Add($"CREATE TRIGGER IF NOT EXISTS trg_revisione_{tabella}_ad AFTER DELETE ON {tabella} BEGIN UPDATE meta SET value = CAST(value AS INTEGER) + 1 WHERE key = '{LocalIndexSnapshot.ChiaveRevisione}'; END;");
        }
        using var comando = connection.CreateCommand();
        comando.Transaction = transaction;
        comando.CommandText = string.Join('\n', trigger);
        comando.ExecuteNonQuery();
    }

    private static string? ReadMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void WriteMeta(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO meta(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string BuildFtsQuery(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Replace("\"", "\"\""))
            .Where(t => t.Length > 0)
            .Select(t => $"\"{t}\"*");
        return string.Join(" AND ", terms);
    }

    /// <summary>Comandi della proiezione SQLite usati per ogni Test di una riconciliazione completa o di un batch incrementale.
    /// Ogni comando si crea alla prima esecuzione e si prepara dalla seconda: un comando eseguito una sola volta si compila
    /// come prima (un file modificato spesso genera una sola esecuzione per tabella), uno riusato si compila una volta sola.
    /// Legati alla transazione del chiamante. La FTS si crea al primo uso: una FTS assente o non valida non blocca le altre
    /// tabelle (vedi UpsertFts).</summary>
    private sealed class ComandiProiezione : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly ComandoRiusabile _cercaAltroUid = new("SELECT uid FROM test_summary WHERE path = $path AND uid <> $uid;",
            ("$path", SqliteType.Text), ("$uid", SqliteType.Text));
        private readonly ComandoRiusabile _riepilogo = new(
            """
            INSERT INTO test_summary(
                uid, path, id, revision, eliminato, data_creazione_ticks, data_creazione_raw_ticks, ultima_modifica_ticks,
                modificato_da, stato_lavoro, test_template_uid, creato_da, assegnatario,
                descrizione_breve, descrizione, descrizione_problema, severita, priorita, current_outcome,
                revision_count, operation_attempt_count, effective_operation_count, first_conforme_revision,
                origine_test, executed_revision_count, ecu_grezzi, ticket_collegati, senza_ecu)
            VALUES(
                $uid, $path, $id, $revision, $eliminato, $creato, $creatoRaw, $modificato,
                $modificatoDa, $stato, $template, $creatoDa, $assegnatario,
                $breve, $descrizione, $problema, $severita, $priorita, $outcome,
                $revisionCount, $attemptCount, $effectiveCount, $firstConforme, $origine, $executedRevisions, $ecuGrezzi, $ticketCollegati, $senzaEcu);
            """,
            ("$uid", SqliteType.Text), ("$path", SqliteType.Text), ("$id", SqliteType.Text), ("$revision", SqliteType.Integer),
            ("$eliminato", SqliteType.Integer), ("$creato", SqliteType.Integer), ("$creatoRaw", SqliteType.Integer),
            ("$modificato", SqliteType.Integer), ("$modificatoDa", SqliteType.Text), ("$stato", SqliteType.Integer),
            ("$template", SqliteType.Text), ("$creatoDa", SqliteType.Text), ("$assegnatario", SqliteType.Text),
            ("$breve", SqliteType.Text), ("$descrizione", SqliteType.Text), ("$problema", SqliteType.Text),
            ("$severita", SqliteType.Integer), ("$priorita", SqliteType.Integer), ("$outcome", SqliteType.Integer),
            ("$revisionCount", SqliteType.Integer), ("$attemptCount", SqliteType.Integer), ("$effectiveCount", SqliteType.Integer),
            ("$firstConforme", SqliteType.Integer), ("$executedRevisions", SqliteType.Integer), ("$origine", SqliteType.Integer),
            ("$ecuGrezzi", SqliteType.Integer), ("$ticketCollegati", SqliteType.Integer), ("$senzaEcu", SqliteType.Integer));
        // Veicolo con le copie di test_summary della proiezione Home (v14): la riga e' letta dalla stessa transazione,
        // quindi copia esattamente i valori scritti da _riepilogo, senza ripetere qui la loro derivazione.
        private readonly ComandoRiusabile _veicolo = new(
            $"""
            INSERT OR IGNORE INTO test_vehicle(test_uid, value, eliminato, origine_test, data_creazione_ticks, esito_home)
            SELECT s.uid, $value, s.eliminato, s.origine_test, s.data_creazione_ticks, COALESCE(s.current_outcome, {(int)Esito.NonEseguitoNA})
            FROM test_summary s
            WHERE s.uid = $uid;
            """,
            ("$uid", SqliteType.Text), ("$value", SqliteType.Text));
        private readonly ComandoRiusabile _campoCustom = new(
            "INSERT INTO test_custom_field(test_uid, field_key, value) VALUES($uid, $key, $value);",
            ("$uid", SqliteType.Text), ("$key", SqliteType.Text), ("$value", SqliteType.Text));
        private readonly ComandoRiusabile _revisione = new(
            """
            INSERT INTO test_revision(
                test_uid, revision_index, iteration_number, data_ticks, operatore, outcome,
                calculated_outcome, raw_attempt_count, effective_operation_count, origine, unit_count, note, deviazioni)
            VALUES($uid, $index, $iteration, $data, $operator, $outcome, $calculated, $raw, $effective, $origine, $units, $note, $deviazioni);
            """,
            ("$uid", SqliteType.Text), ("$index", SqliteType.Integer), ("$iteration", SqliteType.Integer),
            ("$data", SqliteType.Integer), ("$operator", SqliteType.Text), ("$outcome", SqliteType.Integer),
            ("$calculated", SqliteType.Integer), ("$raw", SqliteType.Integer), ("$effective", SqliteType.Integer),
            ("$origine", SqliteType.Integer), ("$units", SqliteType.Integer), ("$note", SqliteType.Text),
            ("$deviazioni", SqliteType.Text));
        private readonly ComandoRiusabile _tentativo = new(
            """
            INSERT INTO test_operation_attempt(
                test_uid, revision_index, attempt_index, global_attempt_index, condition_uid,
                operation_root_uid, is_effective, funzionalita, outcome, operatore, data_ticks, duration_minutes)
            VALUES($uid, $revision, $attempt, $global, $conditionUid, $rootUid, $effective,
                   $functionality, $outcome, $operator, $data, $durata);
            """,
            ("$uid", SqliteType.Text), ("$revision", SqliteType.Integer), ("$attempt", SqliteType.Integer),
            ("$global", SqliteType.Integer), ("$conditionUid", SqliteType.Text), ("$rootUid", SqliteType.Text),
            ("$effective", SqliteType.Integer), ("$functionality", SqliteType.Text), ("$outcome", SqliteType.Integer),
            ("$operator", SqliteType.Text), ("$data", SqliteType.Integer), ("$durata", SqliteType.Integer));
        private readonly ComandoRiusabile _operazioneEcu = new(
            """
            INSERT OR IGNORE INTO test_operation_ecu(test_uid, revision_index, attempt_index, componente)
            VALUES($uid, $revision, $attempt, $componente);
            """,
            ("$uid", SqliteType.Text), ("$revision", SqliteType.Integer), ("$attempt", SqliteType.Integer),
            ("$componente", SqliteType.Text));
        private readonly ComandoRiusabile _fts = new(
            """
            INSERT INTO test_search(uid, id, descrizione_breve, descrizione, assegnatario, creato_da,
                                    requisiti, funzionalita, componente, veicoli)
            VALUES($uid, $id, $breve, $descrizione, $assegnatario, $creatoDa,
                   $requisiti, $funzionalita, $componente, $veicoli);
            """,
            ("$uid", SqliteType.Text), ("$id", SqliteType.Text), ("$breve", SqliteType.Text), ("$descrizione", SqliteType.Text),
            ("$assegnatario", SqliteType.Text), ("$creatoDa", SqliteType.Text), ("$requisiti", SqliteType.Text),
            ("$funzionalita", SqliteType.Text), ("$componente", SqliteType.Text), ("$veicoli", SqliteType.Text));
        private readonly Dictionary<string, ComandoRiusabile> _valori = new();
        private readonly bool _riusaComandi;

        /// <summary>'riusaComandi' falso: ogni inserimento usa un comando nuovo, come prima del contenitore (un file solo).</summary>
        public ComandiProiezione(SqliteConnection connection, SqliteTransaction transaction, bool riusaComandi = true)
        {
            _connection = connection;
            _transaction = transaction;
            _riusaComandi = riusaComandi;
            foreach (var comando in new[] { _cercaAltroUid, _riepilogo, _veicolo, _campoCustom, _revisione, _tentativo, _operazioneEcu, _fts })
                comando.Riusa = riusaComandi;
        }

        public SqliteCommand CercaAltroUid => _cercaAltroUid.Ottieni(_connection, _transaction);
        public SqliteCommand Riepilogo => _riepilogo.Ottieni(_connection, _transaction);
        public SqliteCommand Veicolo(int esecuzioni) => _veicolo.Ottieni(_connection, _transaction, esecuzioni);
        public SqliteCommand CampoCustom(int esecuzioni) => _campoCustom.Ottieni(_connection, _transaction, esecuzioni);
        public SqliteCommand Revisione => _revisione.Ottieni(_connection, _transaction);
        public SqliteCommand Tentativo => _tentativo.Ottieni(_connection, _transaction);
        public SqliteCommand OperazioneEcu => _operazioneEcu.Ottieni(_connection, _transaction);
        public SqliteCommand Fts => _fts.Ottieni(_connection, _transaction);

        /// <summary>Inserimento dei valori di una tabella di valori (test_ecu, test_vehicle, ...), un comando per tabella.
        /// 'esecuzioni' e' il numero di inserimenti del ciclo: con piu' di uno il comando si prepara subito.</summary>
        public SqliteCommand Valori(string tabella, int esecuzioni)
        {
            if (!_valori.TryGetValue(tabella, out var comando))
            {
                comando = new ComandoRiusabile($"INSERT OR IGNORE INTO {tabella}(test_uid, value) VALUES($uid, $value);",
                    ("$uid", SqliteType.Text), ("$value", SqliteType.Text)) { Riusa = _riusaComandi };
                _valori[tabella] = comando;
            }
            return comando.Ottieni(_connection, _transaction, esecuzioni);
        }

        public void Dispose()
        {
            _cercaAltroUid.Dispose();
            _riepilogo.Dispose();
            _campoCustom.Dispose();
            _revisione.Dispose();
            _tentativo.Dispose();
            _operazioneEcu.Dispose();
            _fts.Dispose();
            _veicolo.Dispose();
            foreach (var comando in _valori.Values) comando.Dispose();
        }
    }

    /// <summary>Un comando della proiezione: creato alla prima esecuzione, preparato dalla seconda (vedi ComandiProiezione).</summary>
    private sealed class ComandoRiusabile : IDisposable
    {
        private readonly string _sql;
        private readonly (string Nome, SqliteType Tipo)[] _parametri;
        private SqliteCommand? _comando;
        private int _accessi;
        private bool _preparato;
        private SqliteCommand? _ultimoNonRiusato;

        /// <summary>Falso: il comando non si riusa, ogni richiesta ne crea uno nuovo (come il codice precedente al contenitore).</summary>
        public bool Riusa { get; set; } = true;

        public ComandoRiusabile(string sql, params (string Nome, SqliteType Tipo)[] parametri)
        {
            _sql = sql;
            _parametri = parametri;
        }

        /// <summary>Comando da usare per il prossimo inserimento (o per un ciclo di 'esecuzioni' inserimenti). Si prepara se il
        /// ciclo ne prevede piu' di uno, altrimenti alla seconda richiesta.</summary>
        public SqliteCommand Ottieni(SqliteConnection connection, SqliteTransaction transaction, int esecuzioni = 1)
        {
            if (!Riusa)
            {
                // Il comando precedente ha gia' eseguito: si chiude qui, come il 'using' del codice precedente.
                _ultimoNonRiusato?.Dispose();
                _ultimoNonRiusato = CreaComando(connection, transaction);
                if (esecuzioni > 1) _ultimoNonRiusato.Prepare();
                return _ultimoNonRiusato;
            }
            _comando ??= CreaComando(connection, transaction);
            _accessi++;
            if (!_preparato && (esecuzioni > 1 || _accessi > 1))
            {
                _comando.Prepare();
                _preparato = true;
            }
            return _comando;
        }

        private SqliteCommand CreaComando(SqliteConnection connection, SqliteTransaction transaction)
        {
            var comando = connection.CreateCommand();
            comando.Transaction = transaction;
            comando.CommandText = _sql;
            foreach (var (nome, tipo) in _parametri) comando.Parameters.Add(nome, tipo);
            return comando;
        }

        public void Dispose()
        {
            _comando?.Dispose();
            _ultimoNonRiusato?.Dispose();
        }
    }

    private sealed record RawIndexedRow(
        string Path,
        long MtimeUtcTicks,
        long FileLength,
        string ContentHash,
        string Json);

    private sealed class PairAccumulator
    {
        public int TotalAttempts { get; set; }
        public int EffectiveOperations { get; set; }
        public int? AttemptsToFirstConforme { get; set; }
    }
}

public sealed record LocalIndexedTest(
    string Path,
    DateTime MtimeUtc,
    long FileLength,
    string ContentHash,
    TestCase TestCase);

public sealed record LocalIndexWriteEntry(
    string Path,
    DateTime MtimeUtc,
    long FileLength,
    TestCase TestCase,
    string? ContentHash = null);

public enum LocalIndexMutationKind
{
    Upsert,
    Remove
}

public sealed record LocalIndexMutation(
    LocalIndexMutationKind Kind,
    string Path,
    DateTime MtimeUtc,
    long FileLength,
    TestCase? TestCase,
    string? ContentHash = null)
{
    public static LocalIndexMutation Upsert(
        string path,
        DateTime mtimeUtc,
        long fileLength,
        TestCase testCase,
        string? contentHash = null) =>
        new(LocalIndexMutationKind.Upsert, path, mtimeUtc, fileLength, testCase, contentHash);

    public static LocalIndexMutation Remove(string path) =>
        new(LocalIndexMutationKind.Remove, path, default, 0, null);
}

public sealed class LocalArchiveStatistics
{
    public int TestCount { get; init; }
    public int RevisionCount { get; init; }
    public int OperationAttemptCount { get; init; }
    public int EffectiveOperationCount { get; init; }
    public int TestedCount { get; init; }
    public int ConformingCount { get; init; }
    public double ConformityRatePercent => TestedCount == 0 ? 0 : 100.0 * ConformingCount / TestedCount;
}

public sealed class LocalOperatorOperationStatistics
{
    public string Operator { get; init; } = "";
    public int TestCount { get; init; }
    public int RevisionCount { get; init; }
    public int OperationAttemptCount { get; init; }
    public int EffectiveOperationCount { get; init; }
    public int ConformingRevisionCount { get; init; }
}

public sealed class LocalFunctionalityOperationStatistics
{
    public string Functionality { get; init; } = "";
    public int TestFunctionalityPairs { get; init; }
    public int SuccessfulPairs { get; init; }
    public int NeverSuccessfulPairs { get; init; }
    public int TotalOperationAttempts { get; init; }
    public int TotalEffectiveOperations { get; init; }
    public double AverageAttemptsToFirstConforme { get; init; }
    public double NeverSuccessfulPercent => TestFunctionalityPairs == 0 ? 0 : 100.0 * NeverSuccessfulPairs / TestFunctionalityPairs;
}



