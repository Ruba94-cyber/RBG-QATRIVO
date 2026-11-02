using System.Text.Json;
using TestLoom.App.Models;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Read-side Phase D senza materializzazione globale. Usa il DB locale ricostruibile del materializer e offre
/// conteggi a 64 bit, enumerazione streaming e lookup mirati per UID/chiave business. Nessuna API qui impone una
/// cardinalita' massima al catalogo: le sole strutture in memoria sono la finestra/delta richiesti dal caller.
/// </summary>
public sealed class PhaseDMasterScalableReader
{
    private readonly string _sharedRoot;
    private readonly PhaseDMasterMaterializer _materializer;
    private readonly string _connectionString;

    public PhaseDMasterScalableReader(string sharedRoot, string? localBaseDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        _sharedRoot = sharedRoot;
        _materializer = new PhaseDMasterMaterializer(sharedRoot, localBaseDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _materializer.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public PhaseDScalableCatalogState GetCatalogState(string catalog)
    {
        ValidateCatalog(catalog);
        _materializer.Refresh(_sharedRoot);
        using var connection = Open();
        EnsureCatalogQueryable(connection, catalog);
        var revision = ReadCatalogRevision(connection, catalog);
        var count = CountActive(connection, catalog);
        return new PhaseDScalableCatalogState(
            $"PD2:{catalog}:{revision}:{count}",
            count,
            revision);
    }

    /// <summary>
    /// Enumerazione lazy delle entita attive. La connessione/reader restano aperti soltanto durante
    /// l'enumerazione e ogni payload viene deserializzato uno alla volta; non viene creata una List globale.
    /// </summary>
    public IEnumerable<T> EnumerateCatalog<T>(string catalog) where T : MasterEntity
    {
        ValidateCatalog(catalog);
        _materializer.Refresh(_sharedRoot);
        return EnumerateCurrent<T>(catalog);
    }

    /// <summary>
    /// Fotografia mirata per un patch editoriale. Il costo RAM e' O(numero UID/chiavi toccati), non O(catalogo).
    /// Le teste in conflitto NON falliscono qui: tornano con <see cref="PhaseDMaterializedHead.HasConflict"/> valorizzato e
    /// sta al chiamante di scrittura decidere se la propria operazione le tocca (vedi PhaseDMasterRepositoryEngine).
    /// Collisioni di chiave business fra teste attive restano fail-closed; una chiave posseduta solo da una testa in
    /// conflitto risulta posseduta da quella testa (cosi' nessun'altra voce puo' prenderla).
    /// </summary>
    public PhaseDMasterPatchReadState LoadPatchState(
        string catalog,
        IEnumerable<Guid> entityUids,
        IEnumerable<string> businessKeys)
    {
        ValidateCatalog(catalog);
        ArgumentNullException.ThrowIfNull(entityUids);
        ArgumentNullException.ThrowIfNull(businessKeys);
        _materializer.Refresh(_sharedRoot);

        using var connection = Open();
        var heads = new Dictionary<Guid, PhaseDMaterializedHead>();
        using (var byUid = connection.CreateCommand())
        {
            byUid.CommandText = """
                SELECT entity_uid,operation,business_key,payload_json,payload_sha256,
                       vector_json,conflict,concurrent_heads_json
                FROM phase_d_head
                WHERE catalog=$catalog AND entity_uid=$uid
                LIMIT 1;
                """;
            var pCatalog = byUid.Parameters.Add("$catalog", SqliteType.Text);
            var pUid = byUid.Parameters.Add("$uid", SqliteType.Text);
            pCatalog.Value = catalog;
            byUid.Prepare();

            foreach (var uid in entityUids.Where(x => x != Guid.Empty).Distinct())
            {
                pUid.Value = uid.ToString("N");
                using var reader = byUid.ExecuteReader();
                if (!reader.Read()) continue;
                heads[uid] = ReadHead(reader, catalog);
            }
        }

        var owners = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        using (var byKey = connection.CreateCommand())
        {
            byKey.CommandText = """
                SELECT entity_uid,conflict
                FROM phase_d_head
                WHERE catalog=$catalog
                  AND operation=$upsert
                  AND business_key=$key COLLATE NOCASE;
                """;
            var pCatalog = byKey.Parameters.Add("$catalog", SqliteType.Text);
            var pUpsert = byKey.Parameters.Add("$upsert", SqliteType.Integer);
            var pKey = byKey.Parameters.Add("$key", SqliteType.Text);
            pCatalog.Value = catalog;
            pUpsert.Value = (int)PhaseDMasterMutationOperation.Upsert;
            byKey.Prepare();

            foreach (var rawKey in businessKeys)
            {
                var key = rawKey?.Trim() ?? string.Empty;
                if (key.Length == 0 || owners.ContainsKey(key)) continue;
                pKey.Value = key;
                Guid? owner = null;             // testa attiva (non in conflitto)
                Guid? ownerInConflitto = null;  // usata solo se nessuna testa attiva possiede la chiave
                using var reader = byKey.ExecuteReader();
                while (reader.Read())
                {
                    var uid = Guid.ParseExact(reader.GetString(0), "N");
                    if (reader.GetInt32(1) != 0)
                    {
                        ownerInConflitto ??= uid;
                        continue;
                    }
                    if (owner.HasValue && owner.Value != uid)
                    {
                        // Vedi PhaseDMasterKeyCollisionTolerance. Per il catalogo tollerato non blocchiamo la
                        // lettura dello stato per il patch: 'owner' qui e' usata solo da
                        // PhaseDMasterRepositoryEngine.EnsureBusinessKeyAvailable, che per lo stesso catalogo e'
                        // a sua volta un no-op (vedi commento li'). Il valore che finisce in 'owners' per questa
                        // chiave e' quindi irrilevante ai fini della correttezza per questo catalogo.
                        if (!PhaseDMasterKeyCollisionTolerance.IsTolerant(catalog))
                            throw new PhaseDMasterBusinessKeyConflictException(catalog, key, owner.Value, uid);
                        continue;
                    }
                    owner = uid;
                }
                if (owner.HasValue) owners[key] = owner.Value;
                else if (ownerInConflitto.HasValue) owners[key] = ownerInConflitto.Value;
            }
        }

        return new PhaseDMasterPatchReadState(heads, owners);
    }

    /// <summary>Numero di voci (array "Voci" del payload) per ogni entita attiva, calcolato da SQLite senza materializzare i
    /// payload in .NET. Pensato per i conteggi leggeri dell'archivio guasto dei requisiti.</summary>
    public Dictionary<Guid, int> ContaVociPerEntita(string catalog)
    {
        ValidateCatalog(catalog);
        _materializer.Refresh(_sharedRoot);
        var risultato = new Dictionary<Guid, int>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT entity_uid,
                   CASE WHEN json_valid(payload_json) THEN COALESCE(json_array_length(payload_json,'$.Voci'),0) ELSE 0 END
            FROM phase_d_head
            WHERE catalog=$catalog AND operation=$upsert;
            """;
        command.Parameters.AddWithValue("$catalog", catalog);
        command.Parameters.AddWithValue("$upsert", (int)PhaseDMasterMutationOperation.Upsert);
        using var reader = command.ExecuteReader();
        while (reader.Read()) risultato[Guid.ParseExact(reader.GetString(0), "N")] = reader.GetInt32(1);
        return risultato;
    }

    /// <summary>
    /// Lettura di UNA voce. Resta fail-closed sulla singola voce: se ha teste concorrenti non risolte lancia
    /// <see cref="PhaseDMasterConflictException"/> (i chiamanti guasto lo gestiscono gia' come "testa in conflitto").
    /// Non influisce sulla lettura del catalogo intero.
    /// </summary>
    public PhaseDActiveEntity<T>? LoadActiveEntity<T>(string catalog, Guid uid) where T : MasterEntity
    {
        var state = LoadPatchState(catalog, new[] { uid }, Array.Empty<string>());
        if (!state.Heads.TryGetValue(uid, out var head)) return null;
        if (head.HasConflict) throw new PhaseDMasterConflictException(catalog, new[] { uid });
        if (head.Operation != PhaseDMasterMutationOperation.Upsert)
            return null;
        var item = DeserializePayload<T>(catalog, head);
        return new PhaseDActiveEntity<T>(item, head);
    }

    private IEnumerable<T> EnumerateCurrent<T>(string catalog) where T : MasterEntity
    {
        using var connection = Open();
        EnsureCatalogQueryable(connection, catalog);
        using var command = connection.CreateCommand();
        // Stessa regola di PhaseDMasterMaterializer.LoadCatalog: le teste in conflitto compaiono con la testa deterministica,
        // tranne quelle la cui chiave business e' condivisa con un'altra testa (catalogo non tollerante).
        command.CommandText = """
            SELECT h.entity_uid,h.operation,h.business_key,h.payload_json,h.payload_sha256,
                   h.vector_json,h.conflict,h.concurrent_heads_json
            FROM phase_d_head h
            WHERE h.catalog=$catalog AND h.operation=$upsert
              AND (h.conflict=0 OR $tollerante=1 OR NOT EXISTS (
                    SELECT 1 FROM phase_d_head o
                    WHERE o.catalog=h.catalog AND o.operation=$upsert
                      AND o.entity_uid<>h.entity_uid AND o.business_key=h.business_key COLLATE NOCASE))
            ORDER BY h.business_key COLLATE NOCASE, h.entity_uid;
            """;
        command.Parameters.AddWithValue("$catalog", catalog);
        command.Parameters.AddWithValue("$upsert", (int)PhaseDMasterMutationOperation.Upsert);
        command.Parameters.AddWithValue("$tollerante", PhaseDMasterKeyCollisionTolerance.IsTolerant(catalog) ? 1 : 0);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var head = ReadHead(reader, catalog);
            yield return DeserializePayload<T>(catalog, head);
        }
    }

    private static T DeserializePayload<T>(string catalog, PhaseDMaterializedHead head) where T : MasterEntity
    {
        var item = JsonSerializer.Deserialize<T>(head.PayloadJson, JsonIo.Options)
            ?? throw new InvalidDataException($"Payload Phase D non deserializzabile per {catalog}/{head.EntityUid:N}.");
        var payloadUid = MasterEntityIdentityService.GetRequired(item);
        if (payloadUid != head.EntityUid)
            throw new InvalidDataException($"Uid payload Phase D diverso dall'envelope per {catalog}/{head.EntityUid:N}.");
        var businessKey = PhaseDMasterBusinessKeyService.Get(item);
        if (!businessKey.Equals(head.BusinessKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Chiave business payload Phase D diversa dall'envelope per {catalog}/{head.EntityUid:N}.");
        return item;
    }

    private static PhaseDMaterializedHead ReadHead(SqliteDataReader reader, string catalog)
    {
        var uid = Guid.ParseExact(reader.GetString(0), "N");
        var vector = JsonSerializer.Deserialize<Dictionary<string, long>>(reader.GetString(5), JsonIo.Options)
            ?? throw new InvalidDataException("Version vector Phase D locale non leggibile.");
        var concurrent = JsonSerializer.Deserialize<List<PhaseDMasterMutation>>(reader.GetString(7), JsonIo.Options)
            ?? new List<PhaseDMasterMutation>();
        return new PhaseDMaterializedHead(
            catalog,
            uid,
            (PhaseDMasterMutationOperation)reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            vector,
            reader.GetInt32(6) != 0,
            concurrent);
    }

    /// <summary>Gate del catalogo per l'enumerazione: NON fallisce per le teste in conflitto (vedi EnumerateCurrent), ma
    /// resta fail-closed su una chiave business duplicata fra teste NON in conflitto.</summary>
    /// <summary>Prima chiave business duplicata (senza distinzione di maiuscole) tra le teste attive di un catalogo. Ha la
    /// stessa forma dell'indice locale idx_phase_d_head_dup_nocase (vedi PhaseDMasterMaterializer): WHERE su
    /// catalog/conflict/operation, GROUP BY sulla chiave NOCASE, cosi' SQLite raggruppa scorrendo l'indice senza un
    /// ordinamento temporaneo. Il testo e' condiviso con i test: non va modificato senza aggiornare l'indice.</summary>
    internal const string SqlDuplicatiAttivi = """
        SELECT business_key,MIN(entity_uid),MAX(entity_uid)
        FROM phase_d_head
        WHERE catalog=$catalog AND conflict=0 AND operation=$upsert AND business_key<>''
        GROUP BY business_key COLLATE NOCASE
        HAVING COUNT(*)>1
        LIMIT 1;
        """;

    private static void EnsureCatalogQueryable(SqliteConnection connection, string catalog)
    {
        // Business key duplicata tra UID attive: il GROUP BY viene risolto da SQLite, non da un dizionario RAM.
        using var duplicate = connection.CreateCommand();
        duplicate.CommandText = SqlDuplicatiAttivi;
        duplicate.Parameters.AddWithValue("$catalog", catalog);
        duplicate.Parameters.AddWithValue("$upsert", (int)PhaseDMasterMutationOperation.Upsert);
        using (var reader = duplicate.ExecuteReader())
        {
            // Vedi PhaseDMasterKeyCollisionTolerance: nessun catalogo tollerato usa oggi questo percorso
            // (EnumerateCatalog/paged reader), ma il gate resta corretto anche se in futuro ne usasse uno.
            if (reader.Read() && !PhaseDMasterKeyCollisionTolerance.IsTolerant(catalog))
                throw new PhaseDMasterBusinessKeyConflictException(
                    catalog,
                    reader.GetString(0),
                    Guid.ParseExact(reader.GetString(1), "N"),
                    Guid.ParseExact(reader.GetString(2), "N"));
        }
    }

    private static long CountActive(SqliteConnection connection, string catalog)
    {
        using var command = connection.CreateCommand();
        // Stesso conteggio dell'impronta di PhaseDMasterMaterializer (teste Upsert incluse quelle in conflitto).
        command.CommandText = "SELECT COUNT(*) FROM phase_d_head WHERE catalog=$catalog AND operation=$upsert;";
        command.Parameters.AddWithValue("$catalog", catalog);
        command.Parameters.AddWithValue("$upsert", (int)PhaseDMasterMutationOperation.Upsert);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long ReadCatalogRevision(SqliteConnection connection, string catalog)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision FROM phase_d_catalog_state WHERE catalog=$catalog;";
        command.Parameters.AddWithValue("$catalog", catalog);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=1500; PRAGMA cache_size=-131072; PRAGMA mmap_size=536870912;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void ValidateCatalog(string catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog);
        if (catalog.Length > 128) throw new ArgumentOutOfRangeException(nameof(catalog));
    }
}

public sealed record PhaseDScalableCatalogState(string SourceFingerprint, long ActiveCount, long Revision);

public sealed record PhaseDMasterPatchReadState(
    IReadOnlyDictionary<Guid, PhaseDMaterializedHead> Heads,
    IReadOnlyDictionary<string, Guid> BusinessKeyOwners);

public sealed record PhaseDActiveEntity<T>(T Item, PhaseDMaterializedHead Head) where T : MasterEntity;
