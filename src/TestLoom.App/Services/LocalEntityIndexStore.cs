using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Read-model SQLite LOCALE e ricostruibile per le entita' autorevoli salvate come file JSON separati
/// (Change Request, Non Conformita, Segnalazioni e future entita' analoghe). OneDrive/SharePoint trasporta i
/// file condivisi ma non viene trattato come database. Il DB locale contiene proiezioni/payload per query
/// bounded e puo' essere cancellato senza perdita dati.
///
/// Il reconcile usa TEMP table disk-backed: memoria proporzionale solo all'elemento corrente, non al numero
/// totale di file. Un file visto ma temporaneamente illeggibile puo' mantenere la last-known-good; un tombstone
/// committed viene invece esplicitamente rimosso dal read-model.
/// </summary>
public sealed class LocalEntityIndexStore
{
    // v5: colonna ricerca_testo (testo di ricerca della CR, scritto alla scrittura) con indice covering
    // (kind, archived, modified_ticks, item_key, ricerca_testo) per la ricerca testuale senza leggere i payload JSON;
    // indice (kind, archived, modified_ticks, item_key, path) che copre l'ordinamento dell'elenco CR (vedi
    // EnumerateOrdinatoPerLettura). Le versioni 1..4 si migrano in place (righe conservate, indici creati nella stessa
    // transazione); le versioni sconosciute si isolano e si ricostruiscono dai JSON.
    // v4: colonna stato_testo NORMALE e indice (kind, archived, stato_testo) per il conteggio delle CR aperte. Una colonna
    // GENERATA (v2/v3) non e' coperta dall'indice: SQLite rilegge ogni riga e il conteggio costa quanto json_extract.
    private const int CurrentSchemaVersion = 5;
    private const int BusyTimeoutMs = 750;
    // Righe per lotto nella riconciliazione: la preparazione CPU di un lotto gira in parallelo (vedi Reconcile).
    private const int RigheLottoRiconciliazione = 256;
    private readonly string _connectionString;

    public string DatabasePath { get; }
    public string WorkspaceDirectory { get; }

    /// <summary>
    /// Motivo per cui l'indice non e' utilizzabile in questa sessione (file bloccato, nemmeno la quarantena riesce). Null
    /// quando lo store e' disponibile. Finche' e' valorizzato ogni operazione lancia LocalIndexUnavailableException e chi
    /// usa lo store degrada sulla fonte autorevole; ReinizializzaDopoQuarantena ritenta l'apertura.
    /// </summary>
    public string? IndisponibileMotivo { get; private set; }

    public LocalEntityIndexStore(string sharedRoot, string? localBaseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(sharedRoot))
            throw new ArgumentException("La cartella condivisa non puo essere vuota.", nameof(sharedRoot));

        // Stessa chiave di LocalTestIndexStore (pinned all'apertura o canonica): stessa cartella per-workspace.
        var workspaceKey = WorkspaceLocalPaths.ComputeWorkspaceKey(sharedRoot);
        var baseDir = localBaseDirectory ?? Path.Combine(
            WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", "workspaces");
        WorkspaceDirectory = Path.Combine(baseDir, workspaceKey);
        Directory.CreateDirectory(WorkspaceDirectory);
        DatabasePath = Path.Combine(WorkspaceDirectory, "entity-index.db");

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
        try
        {
            InitializeWithRecovery();
        }
        catch (Exception ex) when (LocalIndexFaultClassifier.IndisponibileAmbiente(ex))
        {
            // Il file e' bloccato anche per la quarantena: lo store resta in vita come "indisponibile" e il chiamante
            // degrada sui JSON, senza un'eccezione dal costruttore.
            IndisponibileMotivo = ex.Message;
            CrashLogService.Registra("Indice SQLite entita': non disponibile in questa sessione (letture dai JSON)", ex);
        }
    }

    public bool TryGetStamp(string kind, string path, out long mtimeTicks, out long length)
    {
        ValidateKind(kind);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT mtime_ticks,file_length FROM entity_item WHERE kind=$kind AND path=$path LIMIT 1;";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            mtimeTicks = 0;
            length = 0;
            return false;
        }
        mtimeTicks = reader.GetInt64(0);
        length = reader.GetInt64(1);
        return true;
    }

    /// <summary>Righe preparate in parallelo da una riconciliazione: solo dati, nessuna risorsa SQLite. Un'eccezione o un errore
    /// di validazione viene riportato e rilanciato nell'ordine delle righe, come nel percorso sequenziale.</summary>
    private sealed record RigaPreparata(string Chiave, Guid Uid, bool Archiviata, bool Eliminata, long Modificata, string Json,
        string Hash, string? Ricerca, string? Errore, Exception? Eccezione);

    private static RigaPreparata Prepara<T>(T entity, string path, Func<T, string> keyOf, Func<T, Guid> uidOf,
        Func<T, bool>? archivedOf, Func<T, bool>? deletedOf, Func<T, DateTime>? modifiedOf, Func<T, string>? ricercaOf)
    {
        try
        {
            var key = keyOf(entity)?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(key)) return new RigaPreparata("", Guid.Empty, false, false, 0, "", "", null, $"chiave business vuota per '{path}'.", null);
            var uid = uidOf(entity);
            if (uid == Guid.Empty) return new RigaPreparata("", Guid.Empty, false, false, 0, "", "", null, $"UID vuoto per '{path}'.", null);
            var json = JsonSerializer.Serialize(entity, JsonIo.CompactOptions);
            var hash = EntityStateFingerprint.ComputeDaJson(entity, json);
            return new RigaPreparata(key, uid, archivedOf?.Invoke(entity) == true, deletedOf?.Invoke(entity) == true,
                (modifiedOf?.Invoke(entity) ?? DateTime.MinValue).ToUniversalTime().Ticks, json, hash, ricercaOf?.Invoke(entity), null, null);
        }
        catch (Exception ex)
        {
            return new RigaPreparata("", Guid.Empty, false, false, 0, "", "", null, null, ex);
        }
    }

    public LocalEntityReconcileResult Reconcile<T>(
        string kind,
        IEnumerable<LocalEntityScanItem<T>> scan,
        Func<T, string> keyOf,
        Func<T, Guid> uidOf,
        Func<T, bool>? archivedOf = null,
        Func<T, bool>? deletedOf = null,
        Func<T, DateTime>? modifiedOf = null,
        Func<T, string>? ricercaOf = null)
    {
        ValidateKind(kind);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(keyOf);
        ArgumentNullException.ThrowIfNull(uidOf);
        using var connection = Open();
        using (var tempStore = connection.CreateCommand())
        {
            tempStore.CommandText = "PRAGMA temp_store=FILE;";
            tempStore.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();

        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                DROP TABLE IF EXISTS temp.ditm_entity_seen;
                DROP TABLE IF EXISTS temp.ditm_entity_stage;
                CREATE TEMP TABLE ditm_entity_seen(
                    path TEXT PRIMARY KEY,
                    keep_existing INTEGER NOT NULL
                ) WITHOUT ROWID;
                CREATE TEMP TABLE ditm_entity_stage(
                    path TEXT PRIMARY KEY,
                    uid TEXT NOT NULL,
                    item_key TEXT NOT NULL COLLATE NOCASE,
                    archived INTEGER NOT NULL,
                    deleted INTEGER NOT NULL,
                    modified_ticks INTEGER NOT NULL,
                    mtime_ticks INTEGER NOT NULL,
                    file_length INTEGER NOT NULL,
                    content_hash TEXT NOT NULL,
                    json TEXT NOT NULL,
                    ricerca_testo TEXT
                ) WITHOUT ROWID;
                """;
            create.ExecuteNonQuery();
        }

        long seenCount = 0;
        long validCount = 0;
        using var insertSeen = connection.CreateCommand();
        insertSeen.Transaction = transaction;
        insertSeen.CommandText = "INSERT INTO temp.ditm_entity_seen(path,keep_existing) VALUES($path,$keep);";
        var seenPath = insertSeen.Parameters.Add("$path", SqliteType.Text);
        var seenKeep = insertSeen.Parameters.Add("$keep", SqliteType.Integer);

        using var insertStage = connection.CreateCommand();
        insertStage.Transaction = transaction;
        insertStage.CommandText = """
            INSERT INTO temp.ditm_entity_stage(
                path,uid,item_key,archived,deleted,modified_ticks,mtime_ticks,file_length,content_hash,json,ricerca_testo)
            VALUES($path,$uid,$key,$archived,$deleted,$modified,$mtime,$length,$hash,$json,$ricerca);
            """;
        var pPath = insertStage.Parameters.Add("$path", SqliteType.Text);
        var pUid = insertStage.Parameters.Add("$uid", SqliteType.Text);
        var pKey = insertStage.Parameters.Add("$key", SqliteType.Text);
        var pArchived = insertStage.Parameters.Add("$archived", SqliteType.Integer);
        var pDeleted = insertStage.Parameters.Add("$deleted", SqliteType.Integer);
        var pModified = insertStage.Parameters.Add("$modified", SqliteType.Integer);
        var pMtime = insertStage.Parameters.Add("$mtime", SqliteType.Integer);
        var pLength = insertStage.Parameters.Add("$length", SqliteType.Integer);
        var pHash = insertStage.Parameters.Add("$hash", SqliteType.Text);
        var pJson = insertStage.Parameters.Add("$json", SqliteType.Text);
        var pRicerca = insertStage.Parameters.Add("$ricerca", SqliteType.Text);

        // Righe a lotti: la preparazione (chiave, uid, JSON, hash, flag, testo di ricerca) e' CPU pura e gira in parallelo; le
        // scritture SQLite restano sequenziali e nello stesso ordine di prima, con gli stessi errori.
        void ElaboraLotto(List<LocalEntityScanItem<T>> righe)
        {
            var preparate = new RigaPreparata?[righe.Count];
            Parallel.For(0, righe.Count, i =>
            {
                if (righe[i].Entity != null)
                    preparate[i] = Prepara(righe[i].Entity!, righe[i].Path, keyOf, uidOf, archivedOf, deletedOf, modifiedOf, ricercaOf);
            });
            for (var i = 0; i < righe.Count; i++)
            {
                var row = righe[i];
                if (string.IsNullOrWhiteSpace(row.Path)) throw new InvalidDataException("Reconcile entity-index: path vuoto.");
                seenPath.Value = row.Path;
                seenKeep.Value = row.PreserveExistingWhenEntityMissing ? 1 : 0;
                try { insertSeen.ExecuteNonQuery(); }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    throw new InvalidDataException($"Reconcile entity-index {kind}: path duplicato '{row.Path}'.", ex);
                }
                seenCount++;

                var riga = preparate[i];
                if (riga == null) continue;
                if (riga.Eccezione != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(riga.Eccezione).Throw();
                if (riga.Errore != null) throw new InvalidDataException($"Reconcile entity-index {kind}: {riga.Errore}");

                pPath.Value = row.Path;
                pUid.Value = riga.Uid.ToString("N");
                pKey.Value = riga.Chiave;
                pArchived.Value = riga.Archiviata ? 1 : 0;
                pDeleted.Value = riga.Eliminata ? 1 : 0;
                pModified.Value = riga.Modificata;
                pMtime.Value = row.MtimeUtc.Ticks;
                pLength.Value = row.FileLength;
                pHash.Value = riga.Hash;
                pJson.Value = riga.Json;
                pRicerca.Value = (object?)riga.Ricerca ?? DBNull.Value;
                insertStage.ExecuteNonQuery();
                validCount++;
            }
        }

        var lotto = new List<LocalEntityScanItem<T>>(RigheLottoRiconciliazione);
        foreach (var row in scan)
        {
            lotto.Add(row);
            if (lotto.Count < RigheLottoRiconciliazione) continue;
            ElaboraLotto(lotto);
            lotto.Clear();
        }
        if (lotto.Count > 0) ElaboraLotto(lotto);

        long beforeCount;
        using (var before = connection.CreateCommand())
        {
            before.Transaction = transaction;
            before.CommandText = "SELECT COUNT(*) FROM entity_item WHERE kind=$kind;";
            before.Parameters.AddWithValue("$kind", kind);
            beforeCount = Convert.ToInt64(before.ExecuteScalar());
        }

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO entity_item(
                    kind,path,uid,item_key,archived,deleted,modified_ticks,mtime_ticks,file_length,content_hash,json,stato_testo,ricerca_testo)
                SELECT $kind,path,uid,item_key,archived,deleted,modified_ticks,mtime_ticks,file_length,content_hash,json,
                    json_extract(json, '$.Stato'),ricerca_testo
                FROM temp.ditm_entity_stage
                WHERE 1=1
                ON CONFLICT(kind,path) DO UPDATE SET
                    uid=excluded.uid,
                    item_key=excluded.item_key,
                    archived=excluded.archived,
                    deleted=excluded.deleted,
                    modified_ticks=excluded.modified_ticks,
                    mtime_ticks=excluded.mtime_ticks,
                    file_length=excluded.file_length,
                    content_hash=excluded.content_hash,
                    json=excluded.json,
                    stato_testo=excluded.stato_testo,
                    ricerca_testo=excluded.ricerca_testo;
                """;
            upsert.Parameters.AddWithValue("$kind", kind);
            upsert.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM entity_item
                WHERE kind=$kind AND (
                    path NOT IN (SELECT path FROM temp.ditm_entity_seen)
                    OR (
                        path IN (SELECT path FROM temp.ditm_entity_seen WHERE keep_existing=0)
                        AND path NOT IN (SELECT path FROM temp.ditm_entity_stage)
                    )
                );
                """;
            delete.Parameters.AddWithValue("$kind", kind);
            delete.ExecuteNonQuery();
        }

        long afterCount;
        using (var after = connection.CreateCommand())
        {
            after.Transaction = transaction;
            after.CommandText = "SELECT COUNT(*) FROM entity_item WHERE kind=$kind;";
            after.Parameters.AddWithValue("$kind", kind);
            afterCount = Convert.ToInt64(after.ExecuteScalar());
        }

        transaction.Commit();
        return new LocalEntityReconcileResult(seenCount, validCount, beforeCount, afterCount);
    }

    public long Count(string kind, bool? archived = null, bool? deleted = null)
    {
        ValidateKind(kind);
        using var connection = Open();
        using var command = connection.CreateCommand();
        var where = BuildWhere(command, kind, archived, deleted);
        command.CommandText = $"SELECT COUNT(*) FROM entity_item {where};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>
    /// Conteggio scalare delle righe di un tipo il cui campo JSON di primo livello (testo) NON e' in
    /// <paramref name="valoriEsclusi"/>. Calcolato da SQLite senza deserializzare il payload: per il campo Stato usa la
    /// colonna stato_testo (valorizzata a ogni scrittura da json_extract e coperta dall'indice, schema v4); per gli altri
    /// campi json_extract sul payload. Una riga senza il campo conta come non esclusa. Il nome del campo deve essere un
    /// identificatore fisso del chiamante (solo lettere, cifre e underscore), mai un dato utente.
    /// </summary>
    public long CountJsonTextNotIn(string kind, string campoJson, IReadOnlyList<string> valoriEsclusi, bool? archived = null, bool? deleted = null)
    {
        ValidateKind(kind);
        if (string.IsNullOrEmpty(campoJson) || !campoJson.All(c => char.IsLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("Campo JSON non valido.", nameof(campoJson));

        using var connection = Open();
        using var command = connection.CreateCommand();
        var where = BuildWhere(command, kind, archived, deleted);
        var clausolaEsclusi = "";
        if (valoriEsclusi.Count > 0)
        {
            var segnaposto = new List<string>(valoriEsclusi.Count);
            for (var i = 0; i < valoriEsclusi.Count; i++)
            {
                var nome = "$escluso" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                command.Parameters.AddWithValue(nome, valoriEsclusi[i]);
                segnaposto.Add(nome);
            }
            // Il campo Stato ha la colonna stato_testo indicizzata e coperta dall'indice: il conteggio non rilegge il JSON.
            var espressioneCampo = campoJson == "Stato" ? "stato_testo" : $"json_extract(json, '$.{campoJson}')";
            clausolaEsclusi = $" AND COALESCE({espressioneCampo}, '') NOT IN ({string.Join(",", segnaposto)})";
        }
        command.CommandText = $"SELECT COUNT(*) FROM entity_item {where}{clausolaEsclusi};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public LocalEntitySlice<T> QuerySlice<T>(string kind, long offset, int limit, bool? archived = null, bool? deleted = null)
    {
        ValidateKind(kind);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));

        using var connection = Open();
        long total;
        using (var count = connection.CreateCommand())
        {
            var where = BuildWhere(count, kind, archived, deleted);
            count.CommandText = $"SELECT COUNT(*) FROM entity_item {where};";
            total = Convert.ToInt64(count.ExecuteScalar());
        }
        if (offset >= total) return new LocalEntitySlice<T>(Array.Empty<LocalEntityRecord<T>>(), total);

        using var command = connection.CreateCommand();
        var queryWhere = BuildWhere(command, kind, archived, deleted);
        command.CommandText = $"""
            SELECT path,uid,item_key,mtime_ticks,file_length,content_hash,json
            FROM entity_item
            {queryWhere}
            ORDER BY modified_ticks DESC,item_key COLLATE NOCASE,path
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var result = new List<LocalEntityRecord<T>>(limit);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(ReadRecord<T>(reader));
        return new LocalEntitySlice<T>(result, total);
    }

    public LocalEntityRecord<T>? GetByKey<T>(string kind, string key)
    {
        ValidateKind(kind);
        if (string.IsNullOrWhiteSpace(key)) return null;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path,uid,item_key,mtime_ticks,file_length,content_hash,json
            FROM entity_item
            WHERE kind=$kind AND item_key=$key COLLATE NOCASE
            ORDER BY path
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", key.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var first = ReadRecord<T>(reader);
        if (reader.Read())
            throw new InvalidDataException($"Entity-index {kind}: chiave business duplicata '{key}'.");
        return first;
    }

    /// <summary>Fetch per path esatto, sempre univoco (PRIMARY KEY(kind,path)) — a differenza di GetByKey/
    /// GetByUid non lancia mai su una chiave business/uid duplicati: serve esattamente ai chiamanti che devono
    /// ispezionare i singoli file di un gruppo già noto per duplicato (block C, revisione multi-PC 6.4.2:
    /// classificazione content-driven delle copie di conflitto OneDrive), senza materializzare l'intero
    /// archivio in RAM solo per un piccolo numero di path sospetti.</summary>
    public LocalEntityRecord<T>? GetByPath<T>(string kind, string path)
    {
        ValidateKind(kind);
        if (string.IsNullOrWhiteSpace(path)) return null;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path,uid,item_key,mtime_ticks,file_length,content_hash,json
            FROM entity_item
            WHERE kind=$kind AND path=$path;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecord<T>(reader) : null;
    }

    public LocalEntityRecord<T>? GetByUid<T>(string kind, Guid uid)
    {
        ValidateKind(kind);
        if (uid == Guid.Empty) return null;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path,uid,item_key,mtime_ticks,file_length,content_hash,json
            FROM entity_item
            WHERE kind=$kind AND uid=$uid
            ORDER BY path
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$uid", uid.ToString("N"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var first = ReadRecord<T>(reader);
        if (reader.Read())
            throw new InvalidDataException($"Entity-index {kind}: UID duplicato {uid:N}.");
        return first;
    }

    public IEnumerable<LocalEntityRecord<T>> Enumerate<T>(string kind, bool? archived = null, bool? deleted = null)
    {
        ValidateKind(kind);
        using var connection = Open();
        using var command = connection.CreateCommand();
        var where = BuildWhere(command, kind, archived, deleted);
        command.CommandText = $"""
            SELECT path,uid,item_key,mtime_ticks,file_length,content_hash,json
            FROM entity_item
            {where}
            ORDER BY modified_ticks DESC,item_key COLLATE NOCASE,path;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return ReadRecord<T>(reader);
    }

    /// <summary>
    /// Come <see cref="Enumerate{T}"/>: stessi record e stesso ordine (modified_ticks DESC, item_key NOCASE, path), ma i payload
    /// non si leggono nell'ordine di modifica. Con Enumerate SQLite legge le righe in quell'ordine, cioe' con accessi casuali
    /// alla tabella: a 20.000 CR (111 MB di payload) costava circa 850 ms. Qui SQLite ordina solo i percorsi (query stretta,
    /// coperta dall'indice ix_entity_kind_archived_ordine), i payload si leggono in ordine di chiave primaria e il risultato
    /// segue l'ordine dei percorsi. Le due letture stanno nella stessa transazione e vedono lo stesso stato dell'indice.
    /// </summary>
    public List<LocalEntityRecord<T>> EnumerateOrdinatoPerLettura<T>(string kind, bool? archived = null, bool? deleted = null)
    {
        ValidateKind(kind);
        var ordine = new List<string>();
        var perPercorso = new Dictionary<string, LocalEntityRecord<T>>(StringComparer.Ordinal);
        using var connection = Open();
        using var transazione = connection.BeginTransaction();
        using (var comando = connection.CreateCommand())
        {
            comando.Transaction = transazione;
            var where = BuildWhere(comando, kind, archived, deleted);
            comando.CommandText = $"SELECT path FROM entity_item {where} ORDER BY modified_ticks DESC,item_key COLLATE NOCASE,path;";
            using var lettore = comando.ExecuteReader();
            while (lettore.Read()) ordine.Add(lettore.GetString(0));
        }
        using (var comando = connection.CreateCommand())
        {
            comando.Transaction = transazione;
            // Solo kind nella WHERE e ORDER BY path (la chiave primaria): la tabella si scorre in ordine di chiave. Il filtro
            // archived/deleted si applica sulle colonne prima di deserializzare, cosi' le righe escluse non vengono lette.
            comando.CommandText = "SELECT path,uid,item_key,mtime_ticks,file_length,content_hash,json,archived,deleted FROM entity_item WHERE kind=$kind ORDER BY path;";
            comando.Parameters.AddWithValue("$kind", kind);
            using var lettore = comando.ExecuteReader();
            while (lettore.Read())
            {
                if (archived.HasValue && (lettore.GetInt64(7) != 0) != archived.Value) continue;
                if (deleted.HasValue && (lettore.GetInt64(8) != 0) != deleted.Value) continue;
                var record = ReadRecord<T>(lettore);
                perPercorso[record.Path] = record;
            }
        }
        transazione.Commit();

        var risultato = new List<LocalEntityRecord<T>>(ordine.Count);
        foreach (var percorso in ordine)
            if (perPercorso.TryGetValue(percorso, out var record)) risultato.Add(record);
        return risultato;
    }

    /// <summary>Enumera solo i record il cui payload JSON contiene una specifica sottostringa via instr(), evitando la deserializzazione di massa.</summary>
    public IEnumerable<LocalEntityRecord<T>> EnumerateMatching<T>(string kind, string jsonSubstring, bool? archived = null, bool? deleted = null)
    {
        ValidateKind(kind);
        if (string.IsNullOrEmpty(jsonSubstring)) yield break;
        using var connection = Open();
        using var command = connection.CreateCommand();
        var where = BuildWhere(command, kind, archived, deleted);
        command.CommandText = $"""
            SELECT path,uid,item_key,mtime_ticks,file_length,content_hash,json
            FROM entity_item
            {where} AND instr(json, $jsonSubstring) > 0
            ORDER BY modified_ticks DESC,item_key COLLATE NOCASE,path;
            """;
        command.Parameters.AddWithValue("$jsonSubstring", jsonSubstring);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return ReadRecord<T>(reader);
    }

    /// <summary>Stamp (mtime e lunghezza) di tutti i record del tipo: serve a verificare che l'indice corrisponda ai file
    /// senza leggere i JSON.</summary>
    public Dictionary<string, (long MtimeTicks, long Length)> CaricaStamp(string kind)
    {
        ValidateKind(kind);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path,mtime_ticks,file_length FROM entity_item WHERE kind=$kind;";
        command.Parameters.AddWithValue("$kind", kind);
        var stamp = new Dictionary<string, (long MtimeTicks, long Length)>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read()) stamp[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
        return stamp;
    }

    /// <summary>Percorsi dei record il cui JSON contiene la sottostringa, senza distinguere le maiuscole ASCII (lower su
    /// json e sottostringa). Solo sottostringhe ASCII: il risultato e' un insieme di candidati da confermare sul file,
    /// non una risposta.</summary>
    public IReadOnlyList<string> PercorsiContenenti(string kind, string sottostringaAscii, bool? archived = null, bool? deleted = null)
    {
        ValidateKind(kind);
        using var connection = Open();
        using var command = connection.CreateCommand();
        var where = BuildWhere(command, kind, archived, deleted);
        command.CommandText = $"SELECT path FROM entity_item {where} AND instr(lower(json), $sottostringa) > 0;";
        command.Parameters.AddWithValue("$sottostringa", sottostringaAscii.ToLowerInvariant());
        var percorsi = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) percorsi.Add(reader.GetString(0));
        return percorsi;
    }

    public IReadOnlyList<LocalEntityDuplicateUid> GetDuplicateUids(string kind)
    {
        ValidateKind(kind);
        using var connection = Open();
        using var command = connection.CreateCommand();
        // Difetto trovato in revisione (block D, multi-PC 6.4.2): a differenza di C#/molti altri linguaggi, in
        // un literal stringa SQLite '\n' e' presa alla lettera (backslash seguito da 'n', DUE caratteri), non
        // un newline — non esistono escape C-style nei literal SQL. GROUP_CONCAT(path,'\n') produceva quindi
        // "pathA\npathB" con un backslash letterale al centro, mai uno 0x0A: lo Split('\n', ...) lato C# (che
        // usa invece il vero carattere newline) non trovava MAI corrispondenze e restituiva l'intera stringa
        // concatenata come UN SOLO path inesistente. Per un gruppo di duplicati con 2+ path questo azzerava
        // silenziosamente la classificazione content-driven delle copie di conflitto (canonici.Count restava
        // sempre 0, mai il vero path canonico) e, in produzione, rendeva "Apri file" inutilizzabile su quella
        // riga (percorso concatenato mai esistito su disco). char(10) e' il vero carattere newline: coerente
        // con lo Split('\n', ...) sotto.
        command.CommandText = """
            SELECT uid,GROUP_CONCAT(path,char(10))
            FROM entity_item
            WHERE kind=$kind AND uid<>''
            GROUP BY uid
            HAVING COUNT(*)>1;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        var result = new List<LocalEntityDuplicateUid>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Guid.TryParseExact(reader.GetString(0), "N", out var uid)) continue;
            result.Add(new LocalEntityDuplicateUid(uid, reader.GetString(1).Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        }
        return result;
    }

    public void Upsert<T>(string kind, string path, DateTime mtimeUtc, long fileLength, T entity,
        Func<T, string> keyOf, Func<T, Guid> uidOf, bool archived = false, bool deleted = false, DateTime? modified = null,
        string? ricerca = null)
    {
        ValidateKind(kind);
        ArgumentNullException.ThrowIfNull(entity);
        var key = keyOf(entity)?.Trim() ?? "";
        var uid = uidOf(entity);
        if (string.IsNullOrWhiteSpace(key) || uid == Guid.Empty)
            throw new InvalidDataException($"Entity-index {kind}: identita' non valida per '{path}'.");
        var json = JsonSerializer.Serialize(entity, JsonIo.CompactOptions);
        var hash = EntityStateFingerprint.ComputeDaJson(entity, json);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO entity_item(kind,path,uid,item_key,archived,deleted,modified_ticks,mtime_ticks,file_length,content_hash,json,stato_testo,ricerca_testo)
            VALUES($kind,$path,$uid,$key,$archived,$deleted,$modified,$mtime,$length,$hash,$json,json_extract($json, '$.Stato'),$ricerca)
            ON CONFLICT(kind,path) DO UPDATE SET
                uid=excluded.uid,item_key=excluded.item_key,archived=excluded.archived,deleted=excluded.deleted,
                modified_ticks=excluded.modified_ticks,mtime_ticks=excluded.mtime_ticks,file_length=excluded.file_length,
                content_hash=excluded.content_hash,json=excluded.json,stato_testo=excluded.stato_testo,ricerca_testo=excluded.ricerca_testo;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$uid", uid.ToString("N"));
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$archived", archived ? 1 : 0);
        command.Parameters.AddWithValue("$deleted", deleted ? 1 : 0);
        command.Parameters.AddWithValue("$modified", (modified ?? DateTime.MinValue).ToUniversalTime().Ticks);
        command.Parameters.AddWithValue("$mtime", mtimeUtc.Ticks);
        command.Parameters.AddWithValue("$length", fileLength);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$ricerca", (object?)ricerca ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void Remove(string kind, string path)
    {
        ValidateKind(kind);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM entity_item WHERE kind=$kind AND path=$path;";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    private static LocalEntityRecord<T> ReadRecord<T>(SqliteDataReader reader)
    {
        var entity = JsonSerializer.Deserialize<T>(reader.GetString(6), JsonIo.Options)
            ?? throw new InvalidDataException("Entity-index locale: payload JSON nullo/non deserializzabile.");
        return new LocalEntityRecord<T>(
            reader.GetString(0),
            Guid.TryParseExact(reader.GetString(1), "N", out var uid) ? uid : Guid.Empty,
            reader.GetString(2),
            new DateTime(reader.GetInt64(3), DateTimeKind.Utc),
            reader.GetInt64(4),
            reader.GetString(5),
            entity);
    }

    private static string BuildWhere(SqliteCommand command, string kind, bool? archived, bool? deleted)
    {
        var clauses = new List<string> { "kind=$kind" };
        command.Parameters.AddWithValue("$kind", kind);
        if (archived.HasValue)
        {
            clauses.Add("archived=$archived");
            command.Parameters.AddWithValue("$archived", archived.Value ? 1 : 0);
        }
        if (deleted.HasValue)
        {
            clauses.Add("deleted=$deleted");
            command.Parameters.AddWithValue("$deleted", deleted.Value ? 1 : 0);
        }
        return "WHERE " + string.Join(" AND ", clauses);
    }

    private SqliteConnection Open()
    {
        if (IndisponibileMotivo is { } motivo) throw new LocalIndexUnavailableException(motivo);
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={BusyTimeoutMs};";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void InitializeWithRecovery()
    {
        var guasto = LocalIndexFaultKind.None;
        try
        {
            Initialize();
            return;
        }
        catch (Exception ex) when (LocalIndexFaultClassifier.Classify(ex) is
            LocalIndexFaultKind.Corruption or LocalIndexFaultKind.SchemaIncompatible)
        {
            guasto = LocalIndexFaultClassifier.Classify(ex);
            CrashLogService.Registra($"Indice SQLite entita': reset all'avvio ({LocalIndexFaultClassifier.Descrivi(guasto)})", ex);
        }

        // Una versione non compatibile viene eliminata (cache ricostruibile); la corruzione va in quarantena.
        // Un reset incompleto ora fallisce senza distruggere la fotografia locale.
        if (!LocalIndexQuarantine.TryRecuperaAllAvvio(DatabasePath, guasto, out var motivo))
            throw new LocalIndexUnavailableException($"entita' non recuperabile all'avvio. {motivo}");
        Initialize();
    }

    /// <summary>Prepara il nuovo DB locale solo dopo che il coordinatore ha isolato la generazione guasta.</summary>
    public void ReinizializzaDopoQuarantena()
    {
        // Nuovo tentativo: se il file si e' liberato l'indice torna disponibile; altrimenti l'errore arriva al chiamante
        // (il coordinatore di recupero chiude il recupero come non riuscito).
        IndisponibileMotivo = null;
        try
        {
            Initialize();
        }
        catch (Exception ex) when (LocalIndexFaultClassifier.IndisponibileAmbiente(ex))
        {
            IndisponibileMotivo = ex.Message;
            throw;
        }
    }

    /// <summary>
    /// Vero se il tipo ha una fotografia completa del condiviso: una riconciliazione di tutti i file del tipo e' riuscita su
    /// questo database. Un database appena creato o ricreato dalla quarantena non ce l'ha: finche' manca, chi legge deve
    /// usare la fonte autorevole (i JSON) e non un indice vuoto o parziale.
    /// </summary>
    public bool SnapshotCompleto(string kind)
    {
        ValidateKind(kind);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $chiave;";
        command.Parameters.AddWithValue("$chiave", ChiaveSnapshotCompleto(kind));
        return (command.ExecuteScalar() as string) == "1";
    }

    /// <summary>Registra la fotografia completa del tipo dopo una riconciliazione di tutti i file enumerati.</summary>
    public void MarcaSnapshotCompleto(string kind)
    {
        ValidateKind(kind);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO meta(key,value) VALUES($chiave,'1') ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        command.Parameters.AddWithValue("$chiave", ChiaveSnapshotCompleto(kind));
        command.ExecuteNonQuery();
    }

    private static string ChiaveSnapshotCompleto(string kind) => "snapshot_completo:" + kind;

    /// <summary>
    /// Vero se la proiezione di ricerca del tipo e' completa: una riconciliazione profonda ha riscritto il testo di ricerca di
    /// tutte le righe. Finche' manca, la ricerca testuale resta sul percorso JSON, mai sulla proiezione parziale.
    /// </summary>
    public bool RicercaCompleta(string kind)
    {
        ValidateKind(kind);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $chiave;";
        command.Parameters.AddWithValue("$chiave", ChiaveRicercaCompleta(kind));
        return (command.ExecuteScalar() as string) == "1";
    }

    /// <summary>Registra la proiezione di ricerca completa dopo una riconciliazione profonda riuscita.</summary>
    public void MarcaRicercaCompleta(string kind)
    {
        ValidateKind(kind);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO meta(key,value) VALUES($chiave,'1') ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        command.Parameters.AddWithValue("$chiave", ChiaveRicercaCompleta(kind));
        command.ExecuteNonQuery();
    }

    private static string ChiaveRicercaCompleta(string kind) => "ricerca_completa:" + kind;

    /// <summary>
    /// Righe non archiviate di un tipo con il loro testo di ricerca, nell'ordine di <see cref="Enumerate{T}"/>. Legge soltanto
    /// l'indice: nessun payload JSON viene toccato. Il chiamante puo' fermarsi prima della fine: la connessione si chiude con l'enumeratore.
    /// </summary>
    public IEnumerable<(string Path, string? Testo)> EnumerateRicerca(string kind, bool archived)
    {
        ValidateKind(kind);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path, ricerca_testo FROM entity_item
            WHERE kind=$kind AND archived=$archived
            ORDER BY modified_ticks DESC,item_key COLLATE NOCASE,path;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$archived", archived ? 1 : 0);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>
    /// Allinea lo schema di un indice esistente. Le versioni 1..3 sono cache dello stesso dato: la tabella viene ricostruita
    /// in place (colonna stato_testo, righe copiate, indici ricreati) in una sola transazione. Le righe restano, quindi chi
    /// legge all'avvio vede l'ultima fotografia nota e non un indice vuoto; la riconciliazione del bootstrap la riallinea ai
    /// file. Una versione sconosciuta lancia LocalIndexSchemaIncompatibleException: InitializeWithRecovery isola il database
    /// e lo ricostruisce dai JSON.
    /// </summary>
    private static void AllineaSchema(SqliteConnection connection)
    {
        using var tabella = connection.CreateCommand();
        tabella.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta';";
        if (Convert.ToInt64(tabella.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0) return; // DB nuovo
        using var versione = connection.CreateCommand();
        versione.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        var letta = versione.ExecuteScalar() as string;
        if (letta == CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)) return;
        if (letta == "4")
        {
            MigraDaQuattro(connection);
            return;
        }
        if (letta is "1" or "2" or "3")
        {
            MigraInPlace(connection);
            return;
        }
        throw new LocalIndexSchemaIncompatibleException($"Schema indice entita' non supportato: {letta ?? "assente"}.");
    }

    /// <summary>Dalla versione 4 alla 5: aggiunge la colonna ricerca_testo (vuota finche' una riconciliazione completa non la
    /// riempie) e il suo indice, in una sola transazione. Le righe restano; la ricerca testuale resta sul percorso JSON
    /// finche' il marcatore della proiezione non viene scritto.</summary>
    private static void MigraDaQuattro(SqliteConnection connection)
    {
        try
        {
            using var comando = connection.CreateCommand();
            comando.CommandText = """
                BEGIN IMMEDIATE;
                ALTER TABLE entity_item ADD COLUMN ricerca_testo TEXT;
                CREATE INDEX IF NOT EXISTS ix_entity_kind_archived_ricerca ON entity_item(kind,archived,modified_ticks DESC,item_key COLLATE NOCASE,ricerca_testo);
                CREATE INDEX IF NOT EXISTS ix_entity_kind_archived_ordine ON entity_item(kind,archived,modified_ticks DESC,item_key COLLATE NOCASE,path);
                UPDATE meta SET value = '5' WHERE key = 'schema_version';
                COMMIT;
                """;
            comando.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            try
            {
                using var annulla = connection.CreateCommand();
                annulla.CommandText = "ROLLBACK;";
                annulla.ExecuteNonQuery();
            }
            catch
            {
                // Nessuna transazione aperta: il rollback non serve.
            }
            var tipo = LocalIndexFaultClassifier.Classify(ex);
            if (tipo is LocalIndexFaultKind.Busy or LocalIndexFaultKind.DiskFull or LocalIndexFaultKind.AccessDenied or LocalIndexFaultKind.Io)
                throw;
            throw new LocalIndexSchemaIncompatibleException($"Migrazione dell'indice (4 -> 5) non riuscita: {ex.Message}");
        }
    }

    /// <summary>Ricostruisce entity_item con la colonna stato_testo normale copiando le righe, in una sola transazione: un
    /// guasto a meta' lascia il database com'era. I guasti di contesa o di disco si propagano; gli altri diventano schema
    /// incompatibile, cioe' isolamento e ricostruzione dai JSON.</summary>
    private static void MigraInPlace(SqliteConnection connection)
    {
        try
        {
            using var comando = connection.CreateCommand();
            comando.CommandText = """
                BEGIN IMMEDIATE;
                CREATE TABLE entity_item_v4(
                    kind TEXT NOT NULL,
                    path TEXT NOT NULL,
                    uid TEXT NOT NULL,
                    item_key TEXT NOT NULL COLLATE NOCASE,
                    archived INTEGER NOT NULL DEFAULT 0,
                    deleted INTEGER NOT NULL DEFAULT 0,
                    modified_ticks INTEGER NOT NULL,
                    mtime_ticks INTEGER NOT NULL,
                    file_length INTEGER NOT NULL,
                    content_hash TEXT NOT NULL,
                    json TEXT NOT NULL,
                    stato_testo TEXT,
                    ricerca_testo TEXT,
                    PRIMARY KEY(kind,path)
                ) WITHOUT ROWID;
                INSERT INTO entity_item_v4(kind,path,uid,item_key,archived,deleted,modified_ticks,mtime_ticks,file_length,content_hash,json,stato_testo)
                    SELECT kind,path,uid,item_key,archived,deleted,modified_ticks,mtime_ticks,file_length,content_hash,json,json_extract(json, '$.Stato')
                    FROM entity_item;
                DROP TABLE entity_item;
                ALTER TABLE entity_item_v4 RENAME TO entity_item;
                CREATE INDEX ix_entity_kind_key ON entity_item(kind,item_key COLLATE NOCASE);
                CREATE INDEX ix_entity_kind_uid ON entity_item(kind,uid);
                CREATE INDEX ix_entity_kind_archived_modified ON entity_item(kind,archived,modified_ticks DESC);
                CREATE INDEX ix_entity_kind_deleted_modified ON entity_item(kind,deleted,modified_ticks DESC);
                CREATE INDEX ix_entity_kind_archived_stato ON entity_item(kind,archived,stato_testo);
                CREATE INDEX ix_entity_kind_archived_ricerca ON entity_item(kind,archived,modified_ticks DESC,item_key COLLATE NOCASE,ricerca_testo);
                CREATE INDEX ix_entity_kind_archived_ordine ON entity_item(kind,archived,modified_ticks DESC,item_key COLLATE NOCASE,path);
                INSERT OR REPLACE INTO meta(key,value)
                    SELECT 'snapshot_completo:change-request','1'
                    WHERE EXISTS (SELECT 1 FROM entity_item WHERE kind = 'change-request');
                UPDATE meta SET value = '5' WHERE key = 'schema_version';
                COMMIT;
                """;
            comando.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            try
            {
                using var annulla = connection.CreateCommand();
                annulla.CommandText = "ROLLBACK;";
                annulla.ExecuteNonQuery();
            }
            catch
            {
                // Nessuna transazione aperta: il rollback non serve.
            }
            var tipo = LocalIndexFaultClassifier.Classify(ex);
            if (tipo is LocalIndexFaultKind.Busy or LocalIndexFaultKind.DiskFull or LocalIndexFaultKind.AccessDenied or LocalIndexFaultKind.Io)
                throw;
            throw new LocalIndexSchemaIncompatibleException($"Migrazione dell'indice non riuscita: {ex.Message}");
        }
    }

    private void Initialize()
    {
        using var connection = Open();
        AllineaSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS entity_item(
                kind TEXT NOT NULL,
                path TEXT NOT NULL,
                uid TEXT NOT NULL,
                item_key TEXT NOT NULL COLLATE NOCASE,
                archived INTEGER NOT NULL DEFAULT 0,
                deleted INTEGER NOT NULL DEFAULT 0,
                modified_ticks INTEGER NOT NULL,
                mtime_ticks INTEGER NOT NULL,
                file_length INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                json TEXT NOT NULL,
                stato_testo TEXT,
                ricerca_testo TEXT,
                PRIMARY KEY(kind,path)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_entity_kind_key ON entity_item(kind,item_key COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_entity_kind_uid ON entity_item(kind,uid);
            CREATE INDEX IF NOT EXISTS ix_entity_kind_archived_modified ON entity_item(kind,archived,modified_ticks DESC);
            CREATE INDEX IF NOT EXISTS ix_entity_kind_archived_ordine ON entity_item(kind,archived,modified_ticks DESC,item_key COLLATE NOCASE,path);
            CREATE INDEX IF NOT EXISTS ix_entity_kind_deleted_modified ON entity_item(kind,deleted,modified_ticks DESC);
            CREATE INDEX IF NOT EXISTS ix_entity_kind_archived_stato ON entity_item(kind,archived,stato_testo);
            CREATE INDEX IF NOT EXISTS ix_entity_kind_archived_ricerca ON entity_item(kind,archived,modified_ticks DESC,item_key COLLATE NOCASE,ricerca_testo);
            INSERT INTO meta(key,value) VALUES('schema_version','{CurrentSchemaVersion}')
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """;
        command.ExecuteNonQuery();
    }

    private static void ValidateKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("Tipo entity-index non valido.", nameof(kind));
    }

}

public sealed record LocalEntityScanItem<T>(
    string Path,
    DateTime MtimeUtc,
    long FileLength,
    T? Entity,
    bool PreserveExistingWhenEntityMissing = true);

public sealed record LocalEntityReconcileResult(long Seen, long Valid, long Before, long After);

public sealed record LocalEntityRecord<T>(
    string Path,
    Guid Uid,
    string Key,
    DateTime MtimeUtc,
    long FileLength,
    string ContentHash,
    T Entity);

public sealed record LocalEntitySlice<T>(IReadOnlyList<LocalEntityRecord<T>> Items, long TotalCount);

public sealed record LocalEntityDuplicateUid(Guid Uid, IReadOnlyList<string> Paths);
