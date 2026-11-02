using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Read-model SQLite LOCALE dei cataloghi master ad alta crescita. Non e' mai source of truth: nei workspace
/// Phase D la fonte autorevole e' il mutation journal condiviso; nei workspace legacy restano i JSON fino al
/// cutover. Questo DB vive sotto LocalApplicationData (o sotto localBaseDirectory nei test), puo' essere
/// cancellato/corrotto senza perdita dati e viene ricostruito dalla fonte autorevole verificata.
///
/// Il reconcile e' streaming e disk-backed: la fotografia autorevole viene riversata in una TEMP table SQLite
/// e confrontata set-based con l'indice corrente. Nessun dizionario/lista proporzionale alla cardinalita' totale
/// viene mantenuto in RAM. La dimensione delle pagine e' un limite della singola finestra UI/query, NON del
/// catalogo: conteggi e offset sono a 64 bit e la cardinalita' totale e' limitata solo dalle risorse/storage.
/// </summary>
public sealed class LocalMasterIndexStore
{
    // Bump a 2: key_sort ora e' zero-pad natural-sort (vedi NaturalSortKey) invece della chiave grezza. Un
    // indice locale esistente con schema 1 ha key_sort vecchio stile per ogni riga gia' riconciliata e il
    // fingerprint del reconcile incrementale non lo riscriverebbe da solo (fonte autorevole invariata) — il
    // mismatch di versione forza InitializeWithRecovery a eliminare l'indice e ricostruire da zero.
    //
    // Bump a 3: master_item ha rid INTEGER PRIMARY KEY, allineato al rowid di master_search (FTS senza colonne
    // UNINDEXED, join per rid), ed entity_uid come spareggio dopo key_sort. Cosi' i pareggi di rilevanza non
    // dipendono dai rowid FTS, che cambiano a ogni riscrittura di riga. L'indice v2 e' solo una cache: il
    // mismatch di versione o di layout forza l'eliminazione e la ricostruzione automatica dalla fonte autorevole, senza migrazione.
    private const int CurrentSchemaVersion = 3;
    private const int BusyTimeoutMs = 750;
    private const string FtsEnabledKey = "fts_enabled";
    private readonly string _connectionString;

    public string DatabasePath { get; }
    public string WorkspaceKey { get; }
    public string WorkspaceDirectory { get; }

    /// <summary>Motivo per cui l'indice master non e' utilizzabile in questa sessione (file bloccato, nemmeno la quarantena
    /// riesce). Null quando lo store e' disponibile. Finche' e' valorizzato ogni operazione lancia LocalIndexUnavailableException
    /// e il coordinatore usa la fonte autorevole.</summary>
    public string? IndisponibileMotivo { get; private set; }
    public bool FtsEnabled => string.Equals(ReadMeta(FtsEnabledKey), "1", StringComparison.Ordinal);

    public LocalMasterIndexStore(string sharedRoot, string? localBaseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(sharedRoot))
            throw new ArgumentException("La cartella condivisa non puo essere vuota.", nameof(sharedRoot));

        // Stessa chiave di LocalTestIndexStore (pinned all'apertura o canonica): stessa cartella per-workspace.
        WorkspaceKey = WorkspaceLocalPaths.ComputeWorkspaceKey(sharedRoot);
        var baseDir = localBaseDirectory ?? Path.Combine(
            WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", "workspaces");
        WorkspaceDirectory = Path.Combine(baseDir, WorkspaceKey);
        Directory.CreateDirectory(WorkspaceDirectory);
        DatabasePath = Path.Combine(WorkspaceDirectory, "master-index.db");

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        _connectionString = builder.ToString();
        try
        {
            InitializeWithRecovery();
        }
        catch (Exception ex) when (LocalIndexFaultClassifier.IndisponibileAmbiente(ex))
        {
            // File bloccato anche per la quarantena: lo store resta "indisponibile" e il coordinatore degrada.
            IndisponibileMotivo = ex.Message;
            CrashLogService.Registra("Indice SQLite master: non disponibile in questa sessione (fonte autorevole)", ex);
        }
    }

    /// <summary>Vero solo se il chiamante aveva gia' indicizzato esattamente questa versione autorevole.</summary>
    public bool IsCatalogCurrent(string catalog, string sourceFingerprint)
    {
        ValidateCatalog(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFingerprint);
        return string.Equals(ReadMeta(SourceKey(catalog)), sourceFingerprint, StringComparison.Ordinal);
    }

    /// <summary>
    /// Riconcilia una sequenza autorevole senza materializzarla tutta in memoria. La TEMP table e' locale alla
    /// connessione e viene usata come staging disk-backed: nuove/modificate/stale vengono determinate da SQLite.
    /// Un duplicato di chiave nella sorgente fa fallire chiuso l'intero reconcile prima di toccare master_item.
    /// </summary>
    public LocalMasterReconcileResult ReconcileCatalog<T>(
        string catalog,
        IEnumerable<T> authoritativeItems,
        Func<T, string> keyOf,
        Func<T, bool> isArchived,
        Func<T, string> searchTextOf,
        string? sourceFingerprint = null)
        where T : MasterEntity
    {
        ValidateCatalog(catalog);
        ArgumentNullException.ThrowIfNull(authoritativeItems);
        ArgumentNullException.ThrowIfNull(keyOf);
        ArgumentNullException.ThrowIfNull(isArchived);
        ArgumentNullException.ThrowIfNull(searchTextOf);

        if (!string.IsNullOrWhiteSpace(sourceFingerprint) && IsCatalogCurrent(catalog, sourceFingerprint))
        {
            var currentCount = Count(catalog, includeArchived: true);
            return new LocalMasterReconcileResult(currentCount, 0, 0, 0, SkippedAsCurrent: true);
        }

        using var connection = Open();
        // Impedisce a SQLite di trasformare lo staging di cataloghi molto grandi in pressione RAM evitabile.
        using (var tempStore = connection.CreateCommand())
        {
            tempStore.CommandText = "PRAGMA temp_store=FILE;";
            tempStore.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        var ftsEnabled = string.Equals(ReadMeta(connection, FtsEnabledKey), "1", StringComparison.Ordinal);

        using (var createStage = connection.CreateCommand())
        {
            createStage.Transaction = transaction;
            createStage.CommandText = """
                DROP TABLE IF EXISTS temp.ditm_master_stage;
                DROP TABLE IF EXISTS temp.ditm_master_changed;
                DROP TABLE IF EXISTS temp.ditm_master_stale;
                CREATE TEMP TABLE ditm_master_stage(
                    item_key TEXT COLLATE NOCASE PRIMARY KEY,
                    key_sort TEXT NOT NULL,
                    entity_uid TEXT NOT NULL,
                    archived INTEGER NOT NULL,
                    modified_ticks INTEGER NOT NULL,
                    content_hash TEXT NOT NULL,
                    search_text TEXT NOT NULL,
                    json TEXT NOT NULL
                ) WITHOUT ROWID;
                """;
            createStage.ExecuteNonQuery();
        }

        long authoritativeCount = 0;
        using (var stage = connection.CreateCommand())
        {
            stage.Transaction = transaction;
            stage.CommandText = """
                INSERT INTO temp.ditm_master_stage(
                    item_key,key_sort,entity_uid,archived,modified_ticks,content_hash,search_text,json)
                VALUES($key,$sort,$uid,$archived,$modified,$hash,$search,$json);
                """;
            var pKey = stage.Parameters.Add("$key", SqliteType.Text);
            var pSort = stage.Parameters.Add("$sort", SqliteType.Text);
            var pUid = stage.Parameters.Add("$uid", SqliteType.Text);
            var pArchived = stage.Parameters.Add("$archived", SqliteType.Integer);
            var pModified = stage.Parameters.Add("$modified", SqliteType.Integer);
            var pHash = stage.Parameters.Add("$hash", SqliteType.Text);
            var pSearch = stage.Parameters.Add("$search", SqliteType.Text);
            var pJson = stage.Parameters.Add("$json", SqliteType.Text);
            stage.Prepare();

            foreach (var item in authoritativeItems)
            {
                var key = NormalizeItemKey(keyOf(item));
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(item, JsonIo.Options);
                var hash = Convert.ToHexString(SHA256.HashData(jsonBytes));
                var search = NormalizeSearchText(key + " " + (searchTextOf(item) ?? string.Empty));

                pKey.Value = key;
                pSort.Value = NaturalSortKey(key);
                // Uid tecnico in forma "N" (vuoto se l'entita non ne ha): e' lo spareggio dei pareggi di rilevanza.
                pUid.Value = MasterEntityIdentityService.TryGet(item, out var uid) ? uid.ToString("N") : string.Empty;
                pArchived.Value = isArchived(item) ? 1 : 0;
                pModified.Value = item.UltimaModifica.ToUniversalTime().Ticks;
                pHash.Value = hash;
                pSearch.Value = search;
                pJson.Value = Encoding.UTF8.GetString(jsonBytes);
                try { stage.ExecuteNonQuery(); }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    throw new InvalidDataException(
                        $"Chiave master duplicata '{key}' nel catalogo '{catalog}'; indice locale non aggiornato.", ex);
                }
                authoritativeCount = checked(authoritativeCount + 1);
            }
        }

        using (var classify = connection.CreateCommand())
        {
            classify.Transaction = transaction;
            classify.CommandText = """
                CREATE TEMP TABLE ditm_master_changed(item_key TEXT COLLATE NOCASE PRIMARY KEY) WITHOUT ROWID;
                INSERT INTO temp.ditm_master_changed(item_key)
                SELECT s.item_key
                FROM temp.ditm_master_stage s
                LEFT JOIN master_item m ON m.catalog=$catalog AND m.item_key=s.item_key
                WHERE m.item_key IS NULL OR m.content_hash<>s.content_hash OR m.entity_uid<>s.entity_uid;

                CREATE TEMP TABLE ditm_master_stale(item_key TEXT COLLATE NOCASE PRIMARY KEY) WITHOUT ROWID;
                INSERT INTO temp.ditm_master_stale(item_key)
                SELECT m.item_key
                FROM master_item m
                LEFT JOIN temp.ditm_master_stage s ON s.item_key=m.item_key
                WHERE m.catalog=$catalog AND s.item_key IS NULL;
                """;
            classify.Parameters.AddWithValue("$catalog", catalog);
            classify.ExecuteNonQuery();
        }

        var added = ScalarLong(connection, transaction, """
            SELECT COUNT(*)
            FROM temp.ditm_master_changed c
            LEFT JOIN master_item m ON m.catalog=$catalog AND m.item_key=c.item_key
            WHERE m.item_key IS NULL;
            """, catalog);
        var updated = ScalarLong(connection, transaction, """
            SELECT COUNT(*)
            FROM temp.ditm_master_changed c
            INNER JOIN master_item m ON m.catalog=$catalog AND m.item_key=c.item_key;
            """, catalog);
        var removed = ScalarLong(connection, transaction,
            "SELECT COUNT(*) FROM temp.ditm_master_stale;", catalog: null);

        if (ftsEnabled)
        {
            // Le righe FTS sono indicizzate dal rid di master_item: vanno rimosse PRIMA dell'upsert e della
            // cancellazione delle stale, quando i rid di righe cambiate e stale esistono ancora. Le righe nuove
            // non hanno ancora una FTS da togliere (il join non le trova).
            using var deleteFts = connection.CreateCommand();
            deleteFts.Transaction = transaction;
            deleteFts.CommandText = """
                DELETE FROM master_search
                WHERE rowid IN (
                    SELECT m.rid
                    FROM master_item m
                    WHERE m.catalog=$catalog
                      AND m.item_key IN (
                          SELECT item_key FROM temp.ditm_master_changed
                          UNION
                          SELECT item_key FROM temp.ditm_master_stale
                      )
                );
                """;
            deleteFts.Parameters.AddWithValue("$catalog", catalog);
            deleteFts.ExecuteNonQuery();
        }

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO master_item(catalog,item_key,key_sort,entity_uid,archived,modified_ticks,content_hash,search_text,json)
                SELECT $catalog,s.item_key,s.key_sort,s.entity_uid,s.archived,s.modified_ticks,s.content_hash,s.search_text,s.json
                FROM temp.ditm_master_stage s
                INNER JOIN temp.ditm_master_changed c ON c.item_key=s.item_key
                WHERE 1=1
                ON CONFLICT(catalog,item_key) DO UPDATE SET
                    key_sort=excluded.key_sort,
                    entity_uid=excluded.entity_uid,
                    archived=excluded.archived,
                    modified_ticks=excluded.modified_ticks,
                    content_hash=excluded.content_hash,
                    search_text=excluded.search_text,
                    json=excluded.json;
                """;
            upsert.Parameters.AddWithValue("$catalog", catalog);
            upsert.ExecuteNonQuery();
        }

        if (ftsEnabled)
        {
            // Il rid delle righe cambiate e' quello assegnato (o conservato) dall'upsert: si legge da master_item.
            using var insertFts = connection.CreateCommand();
            insertFts.Transaction = transaction;
            insertFts.CommandText = """
                INSERT INTO master_search(rowid,search_text)
                SELECT m.rid,s.search_text
                FROM temp.ditm_master_stage s
                INNER JOIN temp.ditm_master_changed c ON c.item_key=s.item_key
                INNER JOIN master_item m ON m.catalog=$catalog AND m.item_key=s.item_key;
                """;
            insertFts.Parameters.AddWithValue("$catalog", catalog);
            insertFts.ExecuteNonQuery();
        }

        using (var deleteStale = connection.CreateCommand())
        {
            deleteStale.Transaction = transaction;
            deleteStale.CommandText = """
                DELETE FROM master_item
                WHERE catalog=$catalog
                  AND item_key IN (SELECT item_key FROM temp.ditm_master_stale);
                """;
            deleteStale.Parameters.AddWithValue("$catalog", catalog);
            deleteStale.ExecuteNonQuery();
        }

        WriteMeta(connection, transaction, CountKey(catalog), authoritativeCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(sourceFingerprint))
            WriteMeta(connection, transaction, SourceKey(catalog), sourceFingerprint);
        else
            DeleteMeta(connection, transaction, SourceKey(catalog));
        transaction.Commit();

        return new LocalMasterReconcileResult(authoritativeCount, added, updated, removed, SkippedAsCurrent: false);
    }

    public long Count(string catalog, bool includeArchived = false)
    {
        ValidateCatalog(catalog);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = includeArchived
            ? "SELECT COUNT(*) FROM master_item WHERE catalog = $catalog;"
            : "SELECT COUNT(*) FROM master_item WHERE catalog = $catalog AND archived = 0;";
        command.Parameters.AddWithValue("$catalog", catalog);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>
    /// Query paginata. limit limita la singola finestra, non la cardinalita' totale del catalogo.
    /// Ordine, in tutte le ramificazioni: chiave naturale (key_sort, la stessa dell'editor a ricerca vuota), poi uid
    /// tecnico, poi chiave. Con la ricerca la rilevanza (bm25) precede. I pareggi sono quindi deterministici e
    /// indipendenti dai rowid di FTS, che cambiano a ogni riscrittura di riga.
    /// </summary>
    public IReadOnlyList<LocalMasterIndexHit<T>> Query<T>(
        string catalog,
        string? query,
        bool includeArchived,
        long offset,
        int limit,
        TipoRequisito? tipoRequisito = null)
        where T : MasterEntity
    {
        ValidateCatalog(catalog);
        // 6.5.0: filtro lato SQL per Tipo requisito (Standard/Extra) sulla colonna json esistente, SOLO per il
        // catalogo requisiti; nessun cambio di schema dell'indice (nessun bump, nessuna migrazione).
        var tipoSql = tipoRequisito == null || catalog != "requisiti" ? "" : TipoRequisitoPredicato("{0}", tipoRequisito.Value);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "La finestra deve contenere da 1 a 1000 righe.");

        var normalizedQuery = NormalizeSearchText(query ?? string.Empty);
        using var connection = Open();
        var ftsEnabled = string.Equals(ReadMeta(connection, FtsEnabledKey), "1", StringComparison.Ordinal);
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$catalog", catalog);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$limit", limit);

        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            command.CommandText = $"""
                SELECT item_key, archived, modified_ticks, json
                FROM master_item
                WHERE catalog = $catalog {(includeArchived ? "" : "AND archived = 0")} {string.Format(tipoSql, "json")}
                ORDER BY key_sort COLLATE NOCASE, entity_uid, item_key COLLATE NOCASE
                LIMIT $limit OFFSET $offset;
                """;
        }
        else if (ftsEnabled)
        {
            // Con FTS5 MATCH/bm25 devono riferirsi al nome reale della virtual table; aliasarla e poi usare
            // comunque master_search e' ambiguo tra versioni SQLite/provider. Evitiamo l'alias del lato FTS.
            // Il join e' per rid: master_search.rowid e' il rid della riga in master_item (vedi ReconcileCatalog).
            command.CommandText = $"""
                SELECT m.item_key, m.archived, m.modified_ticks, m.json
                FROM master_search
                JOIN master_item m ON m.rid = master_search.rowid
                WHERE master_search MATCH $fts AND m.catalog = $catalog {(includeArchived ? "" : "AND m.archived = 0")} {string.Format(tipoSql, "m.json")}
                ORDER BY bm25(master_search), m.key_sort COLLATE NOCASE, m.entity_uid, m.item_key COLLATE NOCASE
                LIMIT $limit OFFSET $offset;
                """;
            command.Parameters.AddWithValue("$fts", BuildFtsQuery(normalizedQuery));
        }
        else
        {
            command.CommandText = $"""
                SELECT item_key, archived, modified_ticks, json
                FROM master_item
                WHERE catalog = $catalog {(includeArchived ? "" : "AND archived = 0")} {string.Format(tipoSql, "json")}
                  AND search_text LIKE $like ESCAPE '\'
                ORDER BY key_sort COLLATE NOCASE, entity_uid, item_key COLLATE NOCASE
                LIMIT $limit OFFSET $offset;
                """;
            command.Parameters.AddWithValue("$like", "%" + EscapeLike(normalizedQuery) + "%");
        }

        using var reader = command.ExecuteReader();
        var result = new List<LocalMasterIndexHit<T>>(limit);
        while (reader.Read())
        {
            var json = reader.GetString(3);
            var item = JsonIo.TryParse<T>(json, out var error);
            if (item == null)
            {
                CrashLogService.Registra("Indice SQLite master: payload locale illeggibile",
                    new InvalidDataException($"{catalog}/{reader.GetString(0)}: {error}"));
                continue;
            }
            result.Add(new LocalMasterIndexHit<T>(
                reader.GetString(0),
                reader.GetInt32(1) != 0,
                new DateTime(reader.GetInt64(2), DateTimeKind.Utc),
                item));
        }
        return result;
    }

    private static string TipoRequisitoPredicato(string _, TipoRequisito tipo)
    {
        // "{0}" verra sostituito dal nome colonna json (json oppure m.json). Tipo Extra = stringa "Extra" o numero 1
        // (RequisitoApplicabilita.TipoDi accetta entrambi); tutto il resto, incluso assente, e Standard.
        const string eD4d = "(json_extract({0},'$.Tipo') = 'Extra' OR json_extract({0},'$.Tipo') = 1)";
        return tipo == TipoRequisito.Extra ? "AND " + eD4d : "AND NOT IFNULL(" + eD4d + ", 0)";
    }

    /// <summary>Solo le chiavi attive del catalogo, senza deserializzare il JSON di ciascuna riga: usata per
    /// popolare liste "tutte le voci del catalogo" (es. il picker "Unisci in..." dei cataloghi ad alta crescita)
    /// senza materializzare l'intero catalogo in RAM quando la vista è paginata. Costo O(n) su una singola
    /// colonna indicizzata, trascurabile anche su cataloghi da migliaia di voci.</summary>
    public IReadOnlyList<string> ReadActiveKeys(string catalog)
    {
        ValidateCatalog(catalog);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_key FROM master_item
            WHERE catalog = $catalog AND archived = 0
            ORDER BY key_sort COLLATE NOCASE, entity_uid, item_key COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$catalog", catalog);
        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Legge una singola riga per chiave esatta, archiviata o no. Serve al percorso archivio/ripristino
    /// dei cataloghi ad alta crescita: Query/ReadPage escludono sempre le righe archiviate (bounded per la
    /// consultazione normale), quindi ricaricare "la pagina corrente" prima di eliminare/ripristinare una voce
    /// d'archivio non la troverebbe mai (feedback utente: un componente archiviata risultava sempre "non più presente
    /// nel master data autorevole" anche se non era mai stata toccata da nessun'altra parte).</summary>
    public T? TryGetByKey<T>(string catalog, string itemKey) where T : MasterEntity
    {
        ValidateCatalog(catalog);
        var key = NormalizeItemKey(itemKey);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM master_item WHERE catalog = $catalog AND item_key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$catalog", catalog);
        command.Parameters.AddWithValue("$key", key);
        var json = command.ExecuteScalar() as string;
        if (json == null) return null;
        var item = JsonIo.TryParse<T>(json, out var error);
        if (item == null)
            CrashLogService.Registra("Indice SQLite master: payload locale illeggibile",
                new InvalidDataException($"{catalog}/{key}: {error}"));
        return item;
    }

    public void InvalidateCatalog(string catalog)
    {
        ValidateCatalog(catalog);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        DeleteMeta(connection, transaction, SourceKey(catalog));
        transaction.Commit();
    }

    public void RemoveCatalog(string catalog)
    {
        ValidateCatalog(catalog);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (string.Equals(ReadMeta(connection, FtsEnabledKey), "1", StringComparison.Ordinal))
        {
            // Prima le FTS (si risolvono tramite i rid), poi le righe: nessuna riga FTS orfana puo' restare.
            using var fts = connection.CreateCommand();
            fts.Transaction = transaction;
            fts.CommandText = "DELETE FROM master_search WHERE rowid IN (SELECT rid FROM master_item WHERE catalog = $catalog);";
            fts.Parameters.AddWithValue("$catalog", catalog);
            fts.ExecuteNonQuery();
        }
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM master_item WHERE catalog = $catalog;";
            delete.Parameters.AddWithValue("$catalog", catalog);
            delete.ExecuteNonQuery();
        }
        DeleteMeta(connection, transaction, SourceKey(catalog));
        DeleteMeta(connection, transaction, CountKey(catalog));
        transaction.Commit();
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
            LocalIndexFaultKind.Corruption or LocalIndexFaultKind.SchemaIncompatible || ex is InvalidDataException)
        {
            // InvalidDataException segnala metadati locali incoerenti, non SQLITE_CORRUPT: il reset e'
            // comunque esplicito ma la causa resta distinta nel log e non nasconde BUSY/ACL/disco pieno.
            // Senza una classificazione nota (guasto resta None) il recupero va in quarantena come per la corruzione.
            guasto = ex is InvalidDataException ? LocalIndexFaultKind.None : LocalIndexFaultClassifier.Classify(ex);
            var causa = ex is InvalidDataException ? "metadati locali incompatibili" : LocalIndexFaultClassifier.Descrivi(guasto);
            CrashLogService.Registra($"Indice SQLite master: reset all'avvio ({causa})", ex);
        }

        if (!LocalIndexQuarantine.TryRecuperaAllAvvio(DatabasePath, guasto, out var motivo))
            throw new LocalIndexUnavailableException($"master non recuperabile all'avvio. {motivo}");
        InitializeCore();
    }

    /// <summary>Ricrea lo schema locale esclusivamente dopo la quarantena della generazione guasta.</summary>
    public void ReinizializzaDopoQuarantena()
    {
        // Nuovo tentativo dopo una indisponibilita': se il file si e' liberato lo store torna disponibile.
        IndisponibileMotivo = null;
        try
        {
            InitializeCore();
        }
        catch (Exception ex) when (LocalIndexFaultClassifier.IndisponibileAmbiente(ex))
        {
            IndisponibileMotivo = ex.Message;
            throw;
        }
    }

    private void InitializeCore()
    {
        using var connection = OpenRaw();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout={BusyTimeoutMs};";
            pragma.ExecuteNonQuery();
        }

        using (var meta = connection.CreateCommand())
        {
            meta.CommandText = """
                CREATE TABLE IF NOT EXISTS meta(
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """;
            meta.ExecuteNonQuery();
        }

        // Versione e layout si verificano PRIMA di qualunque DDL sulle tabelle dati: un indice v2 (master_item senza
        // rid) non deve mai essere toccato dal DDL nuovo, che fallirebbe fuori dal recupero. Il mismatch lancia
        // LocalIndexSchemaIncompatibleException (non corruzione): InitializeWithRecovery elimina l'indice e lo ricrea.
        var schemaRaw = ReadMeta(connection, "schema_version");
        if (schemaRaw != null && (!int.TryParse(schemaRaw, out var schema) || schema != CurrentSchemaVersion))
            throw new LocalIndexSchemaIncompatibleException($"Schema indice master locale incompatibile: {schemaRaw}");
        if (TableExists(connection, "master_item") && !ColumnExists(connection, "master_item", "rid"))
            throw new LocalIndexSchemaIncompatibleException("Schema indice master locale incompatibile: layout master_item precedente.");

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS master_item(
                    rid INTEGER PRIMARY KEY,
                    catalog TEXT NOT NULL,
                    item_key TEXT COLLATE NOCASE NOT NULL,
                    key_sort TEXT NOT NULL,
                    entity_uid TEXT NOT NULL,
                    archived INTEGER NOT NULL,
                    modified_ticks INTEGER NOT NULL,
                    content_hash TEXT NOT NULL,
                    search_text TEXT NOT NULL,
                    json TEXT NOT NULL,
                    UNIQUE(catalog, item_key)
                );
                CREATE INDEX IF NOT EXISTS ix_master_catalog_archived_key
                    ON master_item(catalog, archived, key_sort COLLATE NOCASE, entity_uid, item_key COLLATE NOCASE);
                """;
            command.ExecuteNonQuery();
        }

        using (var transaction = connection.BeginTransaction())
        {
            WriteMeta(connection, transaction, "schema_version", CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            transaction.Commit();
        }

        var previousFtsEnabled = string.Equals(ReadMeta(connection, FtsEnabledKey), "1", StringComparison.Ordinal);
        var ftsEnabled = false;
        try
        {
            using var fts = connection.CreateCommand();
            fts.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS master_search USING fts5(
                    search_text,
                    tokenize='unicode61 remove_diacritics 2'
                );
                """;
            fts.ExecuteNonQuery();
            ftsEnabled = true;

            // Se un avvio precedente non disponeva di FTS5, master_item puo' essere gia popolata mentre la
            // virtual table e' vuota. Il primo avvio in cui FTS torna disponibile fa un solo backfill completo;
            // gli avvii successivi (meta=1) restano O(1) e i reconcile aggiornano poi solo le righe cambiate.
            if (!previousFtsEnabled)
            {
                using var transaction = connection.BeginTransaction();
                using var rebuild = connection.CreateCommand();
                rebuild.Transaction = transaction;
                rebuild.CommandText = """
                    DELETE FROM master_search;
                    INSERT INTO master_search(rowid, search_text)
                    SELECT rid, search_text FROM master_item;
                    """;
                rebuild.ExecuteNonQuery();
                transaction.Commit();
            }
        }
        catch (SqliteException ex)
        {
            CrashLogService.Registra("Indice SQLite master: FTS5 non disponibile, fallback LIKE", ex);
        }

        using (var transaction = connection.BeginTransaction())
        {
            WriteMeta(connection, transaction, FtsEnabledKey, ftsEnabled ? "1" : "0");
            transaction.Commit();
        }
    }

    private SqliteConnection Open()
    {
        var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs};";
        command.ExecuteNonQuery();
        return connection;
    }

    private SqliteConnection OpenRaw()
    {
        if (IndisponibileMotivo is { } motivo) throw new LocalIndexUnavailableException(motivo);
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static long ScalarLong(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string? catalog)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (catalog != null) command.Parameters.AddWithValue("$catalog", catalog);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private string? ReadMeta(string key)
    {
        using var connection = Open();
        return ReadMeta(connection, key);
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

    private static void DeleteMeta(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private static string SourceKey(string catalog) => "catalog:" + catalog + ":source";
    private static string CountKey(string catalog) => "catalog:" + catalog + ":count";

    private static void ValidateCatalog(string catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog);
        if (catalog.Length > 128) throw new ArgumentOutOfRangeException(nameof(catalog));
    }

    private static string NormalizeItemKey(string? key)
    {
        var value = key?.Trim() ?? string.Empty;
        if (value.Length == 0) throw new InvalidDataException("Chiave master vuota; indice locale non aggiornato.");
        return value;
    }

    /// <summary>Trasforma "ST100" in "ST0000000100": zero-pad ogni sequenza di cifre a larghezza fissa cosi'
    /// che l'ORDER BY testuale di SQLite (key_sort COLLATE NOCASE, nessuna collation naturale disponibile lato
    /// database) produca lo stesso ordine numerico intuitivo di NaturalStringComparer, usato altrove per i
    /// cataloghi non paginati. Senza questo, un catalogo ad alta crescita (Requisiti/Funzionalita/Componente, sempre
    /// letti in pagina dal DB) mostrava ST100/ST101 subito dopo ST10 e prima di ST11 (feedback utente:
    /// "ordinati in modo abbastanza sparso") perche' l'ordinamento restava puramente lessicografico.</summary>
    private static string NaturalSortKey(string key)
    {
        var builder = new StringBuilder(key.Length + 16);
        var i = 0;
        while (i < key.Length)
        {
            if (char.IsDigit(key[i]))
            {
                var start = i;
                while (i < key.Length && char.IsDigit(key[i])) i++;
                builder.Append(key[start..i].TrimStart('0').PadLeft(10, '0'));
            }
            else
            {
                builder.Append(key[i]);
                i++;
            }
        }
        return builder.ToString();
    }

    private static string NormalizeSearchText(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string BuildFtsQuery(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Replace("\"", "\"\""))
            .Where(t => t.Length > 0)
            .Select(t => $"\"{t}\"*");
        return string.Join(" AND ", terms);
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

}

public sealed record LocalMasterReconcileResult(
    long AuthoritativeCount,
    long Added,
    long Updated,
    long Removed,
    bool SkippedAsCurrent);

public sealed record LocalMasterIndexHit<T>(
    string Key,
    bool Archived,
    DateTime ModifiedUtc,
    T Item)
    where T : MasterEntity;
