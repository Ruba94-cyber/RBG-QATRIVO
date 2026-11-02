using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

public static class PhaseDMasterCatalog
{
    private static readonly Dictionary<string, string> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["requisiti.json"] = "requisiti",
        ["funzionalita.json"] = "funzionalita",
        ["componente.json"] = "componente",
        ["gamma.json"] = "gamma",
        ["categorie.json"] = "categorie",
        ["veicoli.json"] = "veicoli",
        ["strumenti.json"] = "strumenti",
        ["operatori.json"] = "operatori",
        ["test-template.json"] = "test-template",
        ["campi-sistema-pianificazione.json"] = "campi-sistema-pianificazione",
        ["campi-sistema-dettagli.json"] = "campi-sistema-dettagli",
        ["audit-risoluzioni-conflitto.json"] = "audit-risoluzioni-conflitto",
        ["revisioni-unitlogger.json"] = "revisioni-unitlogger",
        ["audit-valutazioni-requisito-componente-veicolo.json"] = "audit-valutazioni-requisito-componente-veicolo",
        ["report-stato-funzionalita-componente-veicolo.json"] = "report-stato-funzionalita-componente-veicolo",
    };

    public static bool TryResolve(string masterPath, out string root, out string catalog)
    {
        root = "";
        catalog = "";
        try
        {
            var full = Path.GetFullPath(masterPath);
            var fileName = Path.GetFileName(full);
            if (!ByFileName.TryGetValue(fileName, out catalog!)) return false;
            root = Path.GetDirectoryName(full) ?? "";
            return !string.IsNullOrWhiteSpace(root);
        }
        catch { return false; }
    }
}

public sealed class PhaseDMasterSnapshot<T> where T : MasterEntity
{
    public required List<T> Items { get; init; }
    public required IReadOnlyDictionary<Guid, PhaseDMaterializedHead> Heads { get; init; }
    public required string SourceFingerprint { get; init; }

    /// <summary>Uid con teste concorrenti non risolte. Le loro voci sono gia' presenti in <see cref="Items"/> (o escluse
    /// solo per collisione di chiave business, vedi <see cref="PhaseDMasterMaterializer.LoadCatalog{T}"/>) con la
    /// testa deterministica: la lettura non fallisce piu' per il conflitto.</summary>
    public IReadOnlyList<Guid> ConflictedUids { get; init; } = Array.Empty<Guid>();
}

public sealed record PhaseDMasterCatalogState(string SourceFingerprint, int ActiveCount, long Revision);

/// <summary>
/// Materializzazione SQLite LOCALE e ricostruibile di Phase D. Non riparsa l'intero journal ad ogni refresh:
/// segmenti e shard immutabili vengono acquisiti una sola volta, mentre i piccoli current.ndjson vengono
/// rifingerprintati. Ogni source mantiene il mapping verso le mutation osservate; soltanto le UID toccate da
/// source nuove/cambiate/rimosse vengono ricalcolate causalmente.
///
/// Le source sparite NON vengono scartate per semplice assenza: OneDrive puo consegnare la delete di un segmento
/// prima del checkpoint che lo incorpora. Una contribution locale viene rimossa soltanto quando una generazione
/// checkpoint integralmente leggibile dello stesso device dichiara CoveredThroughDeviceSequence >= alla
/// sequenza della contribution. Il DB locale puo quindi essere cancellato in ogni momento senza diventare fonte
/// autorevole, ma durante delivery fuori ordine conserva la conoscenza gia verificata invece di regredire.
/// </summary>
public sealed class PhaseDMasterMaterializer
{
    private const int SchemaVersion = 2;
    private static readonly ConcurrentDictionary<string, object> ProcessGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Ultimo Refresh gia' eseguito, per database e per lettura logica (vedi MasterLetturaScope).
    /// La chiave e' il percorso del database perche' ogni catalogo costruisce il PROPRIO materializer sullo
    /// stesso master-v2.db: senza uno stato condiviso fra le istanze, accorpare non servirebbe a nulla.</summary>
    private static readonly ConcurrentDictionary<string, (long Scope, string Root, string Revisione)> RefreshPerLettura = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string _connectionString;
    private readonly object _gate;
    public string DatabasePath { get; }

    public PhaseDMasterMaterializer(string sharedRoot, string? localBaseDirectory = null)
    {
        // Read-model SQLite locale e ricostruibile (vedi intestazione): sotto la chiave corrente, stessa di LocalTestIndexStore.
        var key = WorkspaceLocalPaths.ComputeWorkspaceKey(sharedRoot);
        var workspaceDir = localBaseDirectory == null
            ? Path.Combine(WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", "workspaces", key)
            : Path.Combine(localBaseDirectory, key);
        Directory.CreateDirectory(workspaceDir);
        DatabasePath = Path.Combine(workspaceDir, "master-v2.db");
        _gate = ProcessGates.GetOrAdd(DatabasePath, _ => new object());

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        _connectionString = builder.ToString();
        Initialize();
    }

    /// <summary>Riconcilia soltanto source cambiate e UID impattate. Il valore restituito e' una revisione
    /// locale del read-model, non un clock distribuito e non viene mai scritto nella cartella condivisa.</summary>
    public string Refresh(string sharedRoot)
    {
        // Dentro una lettura logica (Home/Report che interrogano piu' cataloghi per comporre UNA pagina) il
        // primo Refresh vale per tutti: enumerare la cartella condivisa e riscrivere le meta altre cinque
        // volte non porta informazione, costa I/O di rete e produce perfino una pagina meno coerente.
        // Fuori dallo scope il comportamento resta identico a prima.
        var normalizzata = NormalizeRoot(sharedRoot);
        if (MasterLetturaScope.IdCorrente is { } lettura
            && RefreshPerLettura.TryGetValue(DatabasePath, out var gia)
            && gia.Scope == lettura
            && PathComparer.Equals(gia.Root, normalizzata))
        {
            PerfContatori.IncrementaPhaseDRefreshAccorpato();
            return gia.Revisione;
        }

        var cronometroRefresh = System.Diagnostics.Stopwatch.StartNew();
        lock (_gate)
        {
            if (!PhaseDMasterJournalService.IsInitialized(sharedRoot))
                throw new InvalidDataException("Master Phase D non pronto: materializzazione bloccata.");

            var sources = PhaseDMasterJournalSourceReader.Enumerate(sharedRoot);
            var activePaths = new HashSet<string>(PathComparer);
            var checkpointCoverage = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                activePaths.Add(NormalizePath(source.Path));
                if (source.Kind == PhaseDMasterJournalSourceKind.CheckpointShard &&
                    source.CoveredThroughDeviceSequence > 0 &&
                    (!checkpointCoverage.TryGetValue(source.DeviceId, out var covered) || source.CoveredThroughDeviceSequence > covered))
                    checkpointCoverage[source.DeviceId] = source.CoveredThroughDeviceSequence;
            }

            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var known = LoadSources(connection, tx);
            var impacted = new HashSet<EntityKey>();

            var mappingsRemoved = 0L;
            foreach (var source in sources)
            {
                var path = NormalizePath(source.Path);
                known.TryGetValue(path, out var previous);
                if (CanSkipImmutableSource(source, previous)) continue;

                // Il current.ndjson va SEMPRE letto (mai saltato per mtime/dimensione: su OneDrive/SharePoint un
                // file cambiato puo' apparire invariato). Cio' che si puo' evitare quando lo SHA e la lunghezza
                // dei byte appena letti coincidono con l'ultima acquisizione registrata (stesso stato
                // completo/incompleto) e' il lavoro di ingest, che e' idempotente: nessuna mutation nuova puo'
                // comparire da contenuto bit-per-bit identico, quindi rifare upsert riga per riga e ricalcolare
                // le teste impattate non cambierebbe alcun risultato.
                var read = PhaseDMasterJournalSourceReader.Read(source);
                if (source.Kind != PhaseDMasterJournalSourceKind.Current && !read.Complete)
                    throw new InvalidDataException($"Sorgente Phase D immutabile incompleta: {Path.GetFileName(source.Path)}");
                if (previous != null && source.Kind != PhaseDMasterJournalSourceKind.Current &&
                    !previous.Sha256.Equals(read.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Sorgente Phase D immutabile cambiata dopo l'acquisizione: {Path.GetFileName(source.Path)}");

                if (source.Kind == PhaseDMasterJournalSourceKind.Current && previous != null &&
                    previous.Length == read.Length && previous.Complete == read.Complete &&
                    previous.Sha256.Equals(read.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                IngestSource(connection, tx, path, source, read, impacted);
                known[path] = new SourceState(
                    path, source.DeviceId, source.Kind, read.Sha256, read.Length, read.Complete,
                    Math.Max(previous?.MaxDeviceSequence ?? 0, read.MaxDeviceSequence),
                    source.CoveredThroughDeviceSequence,
                    SafeLastWriteTicks(source.Path));
            }

            // Una sorgente che sparisce viene dimenticata soltanto quando il checkpoint selezionato dello stesso
            // device ne prova la copertura. Prima di allora il read-model conserva i byte gia validati localmente.
            foreach (var stale in known.Values.Where(x => !activePaths.Contains(x.Path)).ToList())
            {
                if (!checkpointCoverage.TryGetValue(stale.DeviceId, out var covered) ||
                    stale.MaxDeviceSequence <= 0 || covered < stale.MaxDeviceSequence) continue;
                mappingsRemoved += RemoveSource(connection, tx, stale.Path, impacted);
                known.Remove(stale.Path);
            }

            // Anche il path current e' riutilizzato dopo una rotazione: le mapping storiche restano finche' un
            // checkpoint non le copre, poi vengono eliminate per mantenere bounded il DB locale.
            foreach (var pair in checkpointCoverage)
                mappingsRemoved += RemoveCoveredNonCheckpointMappings(connection, tx, pair.Key, pair.Value, impacted);

            // IngestSource fa solo upsert: nessuna mutation puo' diventare orfana (senza piu' un mapping in
            // phase_d_source_mutation) a meno che QUESTO refresh non abbia appena rimosso mappature sopra
            // (RemoveSource o la copertura checkpoint). Se non e' successo, il NOT IN su ~100k+ righe di
            // phase_d_mutation e' lavoro garantito a vuoto: lo si esegue solo quando puo' davvero servire.
            // Eventuali mutation orfane gia' presenti in un DB locale creato da una versione precedente (prima
            // di questa ottimizzazione) non sono un problema di correttezza: RecomputeHead le ignora comunque
            // tramite il JOIN con phase_d_source_mutation (nessuna influenza sulle teste), e vengono ripulite
            // non appena il PRIMO Refresh successivo rimuove almeno una mappatura vera.
            if (mappingsRemoved > 0)
                DeleteUnreferencedMutations(connection, tx);

            var changedCatalogs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in impacted)
                if (RecomputeHead(connection, tx, key)) changedCatalogs.Add(key.Catalog);

            foreach (var catalog in changedCatalogs.ToList())
                OscuraDuplicatiDiChiave(connection, tx, catalog);

            foreach (var catalog in changedCatalogs)
                BumpCatalogRevision(connection, tx, catalog);

            if (changedCatalogs.Count > 0)
            {
                var revision = ReadLongMeta(connection, tx, "state_revision") + 1;
                WriteMeta(connection, tx, "state_revision", revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            // schema_version e' gia' garantito da Initialize() per QUESTA istanza (unico chiamante di Refresh
            // su questo DatabasePath prima di qualunque Refresh) e non cambia mai a runtime: riscriverlo ad ogni
            // Refresh non ha alcun consumer (nessun altro codice lo rilegge dopo l'apertura) ed e' quindi lavoro
            // SQLite puro overhead. refreshed_utc resta invece scritto sempre: nessun consumer in codice lo
            // legge, ma e' l'unico timestamp diagnostico disponibile ispezionando il DB a mano, e il suo costo
            // e' trascurabile rispetto al resto del Refresh.
            WriteMeta(connection, tx, "refreshed_utc", DateTime.UtcNow.ToString("O"));
            tx.Commit();

            var stateRevision = ReadLongMeta(connection, null, "state_revision");
            var revisione = "PD2:" + stateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (MasterLetturaScope.IdCorrente is { } lettura2)
                RefreshPerLettura[DatabasePath] = (lettura2, normalizzata, revisione);
            PerfContatori.IncrementaPhaseDRefreshEseguito(cronometroRefresh.Elapsed.TotalMilliseconds);
            return revisione;
        }
    }

    public PhaseDMasterCatalogState GetCatalogState(string sharedRoot, string catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog);
        Refresh(sharedRoot);
        using var connection = Open();
        var revision = ReadCatalogRevision(connection, catalog);
        var count = CountActive(connection, catalog);
        return new PhaseDMasterCatalogState(CatalogFingerprint(catalog, revision, count), count, revision);
    }

    /// <summary>
    /// Lettura di un catalogo. Una voce in conflitto NON blocca piu' la lettura dell'intero catalogo: viene esposta con la
    /// TESTA DETERMINISTICA di <see cref="ResolveHeads"/> (versione con TimestampUtc massimo, a parita' MutationId massimo:
    /// stesso esito su ogni PC perche' dipende solo dal contenuto dei journal) e il suo Uid compare in
    /// <see cref="PhaseDMasterSnapshot{T}.ConflictedUids"/>. La testa provvisoria non e' una scelta applicativa: viene
    /// sostituita solo da una risoluzione Master (<see cref="PhaseDMasterConflictResolver"/>).
    /// Su cataloghi non tolleranti, una voce in conflitto la cui chiave business e' condivisa con un'altra testa non entra
    /// in Items (la chiave resterebbe ambigua): resta visibile solo nell'elenco dei conflitti.
    /// </summary>
    public PhaseDMasterSnapshot<T> LoadCatalog<T>(string sharedRoot, string catalog) where T : MasterEntity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog);
        PerfContatori.IncrementaLoadCatalog();
        Refresh(sharedRoot);
        using var connection = Open();
        var rows = ReadHeads(connection, catalog);
        var revision = ReadCatalogRevision(connection, catalog);
        var tolerant = PhaseDMasterKeyCollisionTolerance.IsTolerant(catalog);

        var upsertCount = 0;
        var keyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.Operation != PhaseDMasterMutationOperation.Upsert) continue;
            upsertCount++;
            if (string.IsNullOrWhiteSpace(row.BusinessKey)) continue;
            keyCounts[row.BusinessKey] = keyCounts.GetValueOrDefault(row.BusinessKey) + 1;
        }

        var items = new List<T>();
        var heads = new Dictionary<Guid, PhaseDMaterializedHead>(rows.Count);
        var conflicts = new List<Guid>();
        var businessKeys = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var head in rows)
        {
            heads.Add(head.EntityUid, head);
            if (head.HasConflict)
            {
                conflicts.Add(head.EntityUid);
                if (head.Operation == PhaseDMasterMutationOperation.Delete) continue;
                if (!tolerant && !string.IsNullOrWhiteSpace(head.BusinessKey) && keyCounts[head.BusinessKey] > 1) continue;
            }
            else
            {
                if (head.Operation == PhaseDMasterMutationOperation.Delete) continue;
                // Le collisioni fra teste NON in conflitto restano fail-closed (invariato). Le teste in conflitto non
                // entrano in questa mappa: un conflitto non deve mai rendere illeggibile il catalogo.
                if (!string.IsNullOrWhiteSpace(head.BusinessKey) &&
                    businessKeys.TryGetValue(head.BusinessKey, out var other) && other != head.EntityUid)
                {
                    // Vedi PhaseDMasterKeyCollisionTolerance: per il/i catalogo/i tollerati la riga entra comunque in
                    // 'items' cosi' il catalogo resta leggibile (il consumer decide quale riga vince al prossimo Save).
                    if (!tolerant)
                        throw new PhaseDMasterBusinessKeyConflictException(typeof(T).Name, head.BusinessKey, other, head.EntityUid);
                }
                else if (!string.IsNullOrWhiteSpace(head.BusinessKey))
                {
                    businessKeys[head.BusinessKey] = head.EntityUid;
                }
            }

            var item = JsonSerializer.Deserialize<T>(head.PayloadJson, JsonIo.Options)
                ?? throw new InvalidDataException($"Payload Phase D non deserializzabile per {catalog}/{head.EntityUid:N}.");
            var payloadUid = MasterEntityIdentityService.GetRequired(item);
            if (payloadUid != head.EntityUid)
                throw new InvalidDataException($"Uid payload Phase D diverso dall'envelope per {catalog}/{head.EntityUid:N}.");
            if (!PhaseDMasterBusinessKeyService.Get(item).Equals(head.BusinessKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Chiave business payload Phase D diversa dall'envelope per {catalog}/{head.EntityUid:N}.");
            items.Add(item);
        }

        return new PhaseDMasterSnapshot<T>
        {
            Items = items,
            Heads = heads,
            ConflictedUids = conflicts,
            SourceFingerprint = CatalogFingerprint(catalog, revision, upsertCount)
        };
    }

    /// <summary>Tutte le teste di un catalogo (incluse quelle in conflitto), una riga per Uid. Buffer unico: serve a
    /// contare le chiavi business prima di decidere quali teste in conflitto espone la lettura.</summary>
    /// <summary>Teste di un catalogo in ordine di UID (lettura di LoadCatalog). Il testo e' condiviso con i test del piano
    /// di esecuzione: la chiave primaria (catalog, entity_uid) deve continuare a servire l'ordinamento senza sort.</summary>
    internal const string SqlTesteCatalogo = """
        SELECT entity_uid, operation, business_key, payload_json, payload_sha256,
               vector_json, conflict, concurrent_heads_json
        FROM phase_d_head
        WHERE catalog = $catalog
        ORDER BY entity_uid;
        """;

    private static List<PhaseDMaterializedHead> ReadHeads(SqliteConnection connection, string catalog)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SqlTesteCatalogo;
        command.Parameters.AddWithValue("$catalog", catalog);

        var result = new List<PhaseDMaterializedHead>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var vector = JsonSerializer.Deserialize<Dictionary<string, long>>(reader.GetString(5), JsonIo.Options)
                ?? throw new InvalidDataException("Version vector Phase D locale non leggibile.");
            var concurrent = JsonSerializer.Deserialize<List<PhaseDMasterMutation>>(reader.GetString(7), JsonIo.Options)
                ?? new List<PhaseDMasterMutation>();
            result.Add(new PhaseDMaterializedHead(
                catalog,
                Guid.ParseExact(reader.GetString(0), "N"),
                (PhaseDMasterMutationOperation)reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                vector,
                reader.GetInt32(6) != 0,
                concurrent));
        }
        return result;
    }

    public IReadOnlyList<PhaseDMaterializedHead> LoadConflicts(string sharedRoot, string? catalog = null)
    {
        Refresh(sharedRoot);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(catalog)
            ? "SELECT catalog, entity_uid, operation, business_key, payload_json, payload_sha256, vector_json, concurrent_heads_json FROM phase_d_head WHERE conflict = 1 ORDER BY catalog, entity_uid;"
            : "SELECT catalog, entity_uid, operation, business_key, payload_json, payload_sha256, vector_json, concurrent_heads_json FROM phase_d_head WHERE conflict = 1 AND catalog = $catalog ORDER BY entity_uid;";
        if (!string.IsNullOrWhiteSpace(catalog)) command.Parameters.AddWithValue("$catalog", catalog);
        var result = new List<PhaseDMaterializedHead>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var vector = JsonSerializer.Deserialize<Dictionary<string, long>>(reader.GetString(6), JsonIo.Options) ?? new();
            var concurrent = JsonSerializer.Deserialize<List<PhaseDMasterMutation>>(reader.GetString(7), JsonIo.Options) ?? new();
            result.Add(new PhaseDMaterializedHead(
                reader.GetString(0),
                Guid.ParseExact(reader.GetString(1), "N"),
                (PhaseDMasterMutationOperation)reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), vector, true, concurrent));
        }
        return result;
    }

    public static IReadOnlyList<PhaseDMaterializedHead> ResolveHeads(IReadOnlyList<PhaseDMasterMutation> mutations)
    {
        var result = new List<PhaseDMaterializedHead>();
        foreach (var group in mutations.GroupBy(x => (x.Catalog, x.EntityUid)))
        {
            var all = group.ToList();
            foreach (var mutation in all) mutation.Validate();
            var maximal = all.Where(candidate =>
                    !all.Any(other => other.MutationId != candidate.MutationId &&
                                      PhaseDVersionVector.Dominates(other.VersionVector, candidate.VersionVector)))
                .ToList();

            maximal = maximal.GroupBy(x =>
                    PhaseDVersionVector.Fingerprint(x.VersionVector) + "|" + x.Operation + "|" + x.BusinessKey + "|" + x.PayloadSha256,
                    StringComparer.Ordinal)
                .Select(x => x.OrderBy(m => m.MutationId).First())
                .ToList();

            if (maximal.Count == 0) continue;
            var sameValue = maximal.All(x => x.Operation == maximal[0].Operation &&
                                             x.BusinessKey.Equals(maximal[0].BusinessKey, StringComparison.OrdinalIgnoreCase) &&
                                             x.PayloadSha256.Equals(maximal[0].PayloadSha256, StringComparison.OrdinalIgnoreCase));
            var joined = PhaseDVersionVector.Join(maximal.Select(x => (IReadOnlyDictionary<string, long>)x.VersionVector).ToArray());
            var chosen = maximal.OrderBy(x => x.TimestampUtc).ThenBy(x => x.MutationId).Last();
            result.Add(new PhaseDMaterializedHead(
                group.Key.Catalog,
                group.Key.EntityUid,
                chosen.Operation,
                chosen.BusinessKey,
                chosen.PayloadJson,
                chosen.PayloadSha256,
                joined,
                HasConflict: maximal.Count > 1 && !sameValue,
                ConcurrentHeads: maximal));
        }
        return result;
    }

    private void IngestSource(
        SqliteConnection connection,
        SqliteTransaction tx,
        string normalizedPath,
        PhaseDMasterJournalSource source,
        PhaseDMasterJournalSourceRead read,
        HashSet<EntityKey> impacted)
    {
        foreach (var mutation in read.Mutations)
        {
            mutation.Validate();
            var mutationId = mutation.MutationId.ToString("N");
            var mutationJson = JsonSerializer.Serialize(mutation, JsonIo.CompactOptions);
            var mutationSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mutationJson)));

            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = tx;
                existing.CommandText = "SELECT mutation_sha256 FROM phase_d_mutation WHERE mutation_id = $id;";
                existing.Parameters.AddWithValue("$id", mutationId);
                using var reader = existing.ExecuteReader();
                if (reader.Read())
                {
                    if (!reader.GetString(0).Equals(mutationSha, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"MutationId Phase D duplicato con contenuto divergente: {mutationId}");
                }
                else
                {
                    reader.Close();
                    using var insertMutation = connection.CreateCommand();
                    insertMutation.Transaction = tx;
                    insertMutation.CommandText = "INSERT INTO phase_d_mutation(mutation_id,catalog,entity_uid,mutation_json,mutation_sha256) VALUES($id,$catalog,$uid,$json,$sha);";
                    insertMutation.Parameters.AddWithValue("$id", mutationId);
                    insertMutation.Parameters.AddWithValue("$catalog", mutation.Catalog);
                    insertMutation.Parameters.AddWithValue("$uid", mutation.EntityUid.ToString("N"));
                    insertMutation.Parameters.AddWithValue("$json", mutationJson);
                    insertMutation.Parameters.AddWithValue("$sha", mutationSha);
                    insertMutation.ExecuteNonQuery();
                }
            }

            using var map = connection.CreateCommand();
            map.Transaction = tx;
            map.CommandText = """
                INSERT INTO phase_d_source_mutation(source_path, mutation_id, device_sequence)
                VALUES($path,$id,$sequence)
                ON CONFLICT(source_path, mutation_id) DO UPDATE SET
                    device_sequence = MAX(device_sequence, excluded.device_sequence);
                """;
            map.Parameters.AddWithValue("$path", normalizedPath);
            map.Parameters.AddWithValue("$id", mutationId);
            map.Parameters.AddWithValue("$sequence", read.DeviceSequenceByMutation.GetValueOrDefault(mutation.MutationId));
            map.ExecuteNonQuery();
            impacted.Add(new EntityKey(mutation.Catalog, mutation.EntityUid));
        }

        using var upsertSource = connection.CreateCommand();
        upsertSource.Transaction = tx;
        upsertSource.CommandText = """
            INSERT INTO phase_d_source(
                source_path, device_id, source_kind, sha256, length, complete,
                max_device_sequence, covered_through, last_write_ticks)
            VALUES($path,$device,$kind,$sha,$length,$complete,$max,$covered,$ticks)
            ON CONFLICT(source_path) DO UPDATE SET
                device_id=excluded.device_id,
                source_kind=excluded.source_kind,
                sha256=excluded.sha256,
                length=excluded.length,
                complete=excluded.complete,
                max_device_sequence=MAX(phase_d_source.max_device_sequence, excluded.max_device_sequence),
                covered_through=MAX(phase_d_source.covered_through, excluded.covered_through),
                last_write_ticks=excluded.last_write_ticks;
            """;
        upsertSource.Parameters.AddWithValue("$path", normalizedPath);
        upsertSource.Parameters.AddWithValue("$device", source.DeviceId);
        upsertSource.Parameters.AddWithValue("$kind", (int)source.Kind);
        upsertSource.Parameters.AddWithValue("$sha", read.Sha256);
        upsertSource.Parameters.AddWithValue("$length", read.Length);
        upsertSource.Parameters.AddWithValue("$complete", read.Complete ? 1 : 0);
        upsertSource.Parameters.AddWithValue("$max", read.MaxDeviceSequence);
        upsertSource.Parameters.AddWithValue("$covered", source.CoveredThroughDeviceSequence);
        upsertSource.Parameters.AddWithValue("$ticks", SafeLastWriteTicks(source.Path));
        upsertSource.ExecuteNonQuery();
    }

    /// <summary>
    /// Due postazioni scollegate che creano la STESSA voce (stessa chiave business, es. lo stesso nome di componente)
    /// producono due Upsert con Uid diversi: prima della 6.4.2 l'intero catalogo diventava illeggibile
    /// (PhaseDMasterBusinessKeyConflictException) su ogni PC, senza recupero dall'interfaccia. Qui, per i
    /// cataloghi non tolleranti, la collisione si risolve in modo deterministico nel SOLO read model locale:
    /// vince l'Uid ordinale minore (stessa scelta su ogni PC, dipende solo dagli Uid) e le altre teste diventano
    /// Delete locali, quindi spariscono da tutte le letture (che considerano solo gli Upsert). Il journal
    /// condiviso non viene toccato: la voce perdente resta registrata e nulla va perso; le voci hanno la stessa
    /// chiave, cioè lo stesso nome con cui Test e altri cataloghi le riferiscono.
    /// </summary>
    private static void OscuraDuplicatiDiChiave(SqliteConnection connection, SqliteTransaction tx, string catalog)
    {
        if (PhaseDMasterKeyCollisionTolerance.IsTolerant(catalog)) return;
        // Un solo passaggio raggruppato sul catalogo (niente sottoquery per riga: con cataloghi da 100k voci un
        // confronto riga per riga senza indice utilizzabile costava ore). Di norma non trova nulla.
        var chiaviDuplicate = new List<string>();
        using (var query = connection.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = """
                SELECT MIN(business_key)
                FROM phase_d_head
                WHERE catalog=$catalog AND operation=$upsert AND conflict=0 AND business_key<>''
                GROUP BY business_key COLLATE NOCASE
                HAVING COUNT(*)>1;
                """;
            query.Parameters.AddWithValue("$catalog", catalog);
            query.Parameters.AddWithValue("$upsert", (int)PhaseDMasterMutationOperation.Upsert);
            using var reader = query.ExecuteReader();
            while (reader.Read()) chiaviDuplicate.Add(reader.GetString(0));
        }
        if (chiaviDuplicate.Count == 0) return;

        var perdenti = new List<(string Uid, string Chiave)>();
        using (var perChiave = connection.CreateCommand())
        {
            perChiave.Transaction = tx;
            perChiave.CommandText = """
                SELECT entity_uid
                FROM phase_d_head
                WHERE catalog=$catalog AND operation=$upsert AND conflict=0
                  AND business_key=$chiave COLLATE NOCASE
                ORDER BY entity_uid;
                """;
            perChiave.Parameters.AddWithValue("$catalog", catalog);
            perChiave.Parameters.AddWithValue("$upsert", (int)PhaseDMasterMutationOperation.Upsert);
            var pChiave = perChiave.Parameters.Add("$chiave", SqliteType.Text);
            foreach (var chiave in chiaviDuplicate)
            {
                pChiave.Value = chiave;
                using var reader = perChiave.ExecuteReader();
                var primo = true;
                while (reader.Read())
                {
                    if (primo) { primo = false; continue; } // vince l'Uid ordinale minore
                    perdenti.Add((reader.GetString(0), chiave));
                }
            }
        }
        if (perdenti.Count == 0) return;

        using var oscura = connection.CreateCommand();
        oscura.Transaction = tx;
        oscura.CommandText = "UPDATE phase_d_head SET operation=$delete WHERE catalog=$catalog AND entity_uid=$uid;";
        var pUid = oscura.Parameters.Add("$uid", SqliteType.Text);
        oscura.Parameters.AddWithValue("$catalog", catalog);
        oscura.Parameters.AddWithValue("$delete", (int)PhaseDMasterMutationOperation.Delete);
        foreach (var (uid, chiave) in perdenti)
        {
            pUid.Value = uid;
            oscura.ExecuteNonQuery();
            CrashLogService.Registra("Catalogo condiviso: voce duplicata creata da due postazioni",
                new InvalidDataException($"Catalogo {catalog}, chiave '{chiave}': mostrata una sola voce, la copia {uid} resta solo nel journal."));
        }
    }

    private static bool RecomputeHead(
        SqliteConnection connection,
        SqliteTransaction tx,
        EntityKey key)
    {
        var mutations = new List<PhaseDMasterMutation>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = """
                SELECT DISTINCT m.mutation_json
                FROM phase_d_mutation m
                INNER JOIN phase_d_source_mutation sm ON sm.mutation_id = m.mutation_id
                WHERE m.catalog = $catalog AND m.entity_uid = $uid;
                """;
            command.Parameters.AddWithValue("$catalog", key.Catalog);
            command.Parameters.AddWithValue("$uid", key.Uid.ToString("N"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var mutation = JsonSerializer.Deserialize<PhaseDMasterMutation>(reader.GetString(0), JsonIo.Options)
                    ?? throw new InvalidDataException("Mutation Phase D locale non deserializzabile.");
                mutation.Validate();
                mutations.Add(mutation);
            }
        }

        var resolved = ResolveHeads(mutations);
        var newHead = resolved.SingleOrDefault();
        var newFingerprint = newHead == null ? "" : HeadFingerprint(newHead);
        var oldFingerprint = ReadHeadFingerprint(connection, tx, key);
        if (string.Equals(oldFingerprint, newFingerprint, StringComparison.Ordinal)) return false;

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM phase_d_head WHERE catalog=$catalog AND entity_uid=$uid;";
            delete.Parameters.AddWithValue("$catalog", key.Catalog);
            delete.Parameters.AddWithValue("$uid", key.Uid.ToString("N"));
            delete.ExecuteNonQuery();
        }
        if (newHead == null) return true;

        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO phase_d_head(
                catalog, entity_uid, operation, business_key, payload_json, payload_sha256,
                vector_json, conflict, concurrent_heads_json, head_fingerprint)
            VALUES($catalog,$uid,$operation,$business,$payload,$sha,$vector,$conflict,$heads,$fingerprint);
            """;
        insert.Parameters.AddWithValue("$catalog", newHead.Catalog);
        insert.Parameters.AddWithValue("$uid", newHead.EntityUid.ToString("N"));
        insert.Parameters.AddWithValue("$operation", (int)newHead.Operation);
        insert.Parameters.AddWithValue("$business", newHead.BusinessKey);
        insert.Parameters.AddWithValue("$payload", newHead.PayloadJson);
        insert.Parameters.AddWithValue("$sha", newHead.PayloadSha256);
        insert.Parameters.AddWithValue("$vector", JsonSerializer.Serialize(newHead.VersionVector, JsonIo.CompactOptions));
        insert.Parameters.AddWithValue("$conflict", newHead.HasConflict ? 1 : 0);
        insert.Parameters.AddWithValue("$heads", JsonSerializer.Serialize(newHead.ConcurrentHeads, JsonIo.CompactOptions));
        insert.Parameters.AddWithValue("$fingerprint", newFingerprint);
        insert.ExecuteNonQuery();
        return true;
    }

    /// <returns>Righe eliminate da phase_d_source_mutation (per decidere se DeleteUnreferencedMutations puo'
    /// davvero trovare qualcosa da fare).</returns>
    private static long RemoveSource(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sourcePath,
        HashSet<EntityKey> impacted)
    {
        AddMappedEntities(connection, tx, sourcePath, impacted, maxSequenceInclusive: null);
        long removed;
        using (var deleteMap = connection.CreateCommand())
        {
            deleteMap.Transaction = tx;
            deleteMap.CommandText = "DELETE FROM phase_d_source_mutation WHERE source_path=$path;";
            deleteMap.Parameters.AddWithValue("$path", sourcePath);
            removed = deleteMap.ExecuteNonQuery();
        }
        using var deleteSource = connection.CreateCommand();
        deleteSource.Transaction = tx;
        deleteSource.CommandText = "DELETE FROM phase_d_source WHERE source_path=$path;";
        deleteSource.Parameters.AddWithValue("$path", sourcePath);
        deleteSource.ExecuteNonQuery();
        return removed;
    }

    /// <returns>Righe eliminate da phase_d_source_mutation su tutte le source non-checkpoint del device.</returns>
    private static long RemoveCoveredNonCheckpointMappings(
        SqliteConnection connection,
        SqliteTransaction tx,
        string deviceId,
        long coveredThrough,
        HashSet<EntityKey> impacted)
    {
        var paths = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = "SELECT source_path FROM phase_d_source WHERE device_id=$device AND source_kind <> $checkpoint;";
            select.Parameters.AddWithValue("$device", deviceId);
            select.Parameters.AddWithValue("$checkpoint", (int)PhaseDMasterJournalSourceKind.CheckpointShard);
            using var reader = select.ExecuteReader();
            while (reader.Read()) paths.Add(reader.GetString(0));
        }

        var removedTotal = 0L;
        foreach (var path in paths)
        {
            AddMappedEntities(connection, tx, path, impacted, coveredThrough);
            long removed;
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM phase_d_source_mutation WHERE source_path=$path AND device_sequence <= $covered;";
                delete.Parameters.AddWithValue("$path", path);
                delete.Parameters.AddWithValue("$covered", coveredThrough);
                removed = delete.ExecuteNonQuery();
            }
            removedTotal += removed;

            // Se il checkpoint ha assorbito tutto cio che conoscevamo da questa source, dimentichiamo anche la
            // fingerprint locale: se il file e' ancora fisicamente presente e in futuro dovesse tornare utile
            // dopo un fallback di generazione, verra riletto invece di essere saltato come gia acquisito.
            using var remaining = connection.CreateCommand();
            remaining.Transaction = tx;
            remaining.CommandText = "SELECT COUNT(*) FROM phase_d_source_mutation WHERE source_path=$path;";
            remaining.Parameters.AddWithValue("$path", path);
            if (Convert.ToInt64(remaining.ExecuteScalar()) == 0)
            {
                using var deleteSource = connection.CreateCommand();
                deleteSource.Transaction = tx;
                deleteSource.CommandText = "DELETE FROM phase_d_source WHERE source_path=$path;";
                deleteSource.Parameters.AddWithValue("$path", path);
                deleteSource.ExecuteNonQuery();
            }
        }
        return removedTotal;
    }

    private static void AddMappedEntities(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sourcePath,
        HashSet<EntityKey> impacted,
        long? maxSequenceInclusive)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = maxSequenceInclusive.HasValue
            ? "SELECT DISTINCT m.catalog,m.entity_uid FROM phase_d_source_mutation sm JOIN phase_d_mutation m ON m.mutation_id=sm.mutation_id WHERE sm.source_path=$path AND sm.device_sequence <= $covered;"
            : "SELECT DISTINCT m.catalog,m.entity_uid FROM phase_d_source_mutation sm JOIN phase_d_mutation m ON m.mutation_id=sm.mutation_id WHERE sm.source_path=$path;";
        command.Parameters.AddWithValue("$path", sourcePath);
        if (maxSequenceInclusive.HasValue) command.Parameters.AddWithValue("$covered", maxSequenceInclusive.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            impacted.Add(new EntityKey(reader.GetString(0), Guid.ParseExact(reader.GetString(1), "N")));
    }

    private static void DeleteUnreferencedMutations(SqliteConnection connection, SqliteTransaction tx)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM phase_d_mutation WHERE mutation_id NOT IN (SELECT mutation_id FROM phase_d_source_mutation);";
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, SourceState> LoadSources(SqliteConnection connection, SqliteTransaction tx)
    {
        var result = new Dictionary<string, SourceState>(PathComparer);
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT source_path,device_id,source_kind,sha256,length,complete,max_device_sequence,covered_through,last_write_ticks FROM phase_d_source;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var state = new SourceState(
                reader.GetString(0), reader.GetString(1), (PhaseDMasterJournalSourceKind)reader.GetInt32(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetInt32(5) != 0,
                reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8));
            result[state.Path] = state;
        }
        return result;
    }

    private static bool CanSkipImmutableSource(PhaseDMasterJournalSource source, SourceState? previous)
    {
        if (previous == null || source.Kind == PhaseDMasterJournalSourceKind.Current) return false;
        if (previous.Kind != source.Kind || !previous.Complete) return false;
        try
        {
            var info = new FileInfo(source.Path);
            info.Refresh();
            if (!info.Exists || info.Length != previous.Length || info.LastWriteTimeUtc.Ticks != previous.LastWriteTicks)
                return false;
            if (!string.IsNullOrWhiteSpace(source.ExpectedSha256) &&
                !previous.Sha256.Equals(source.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) return false;
            if (source.ExpectedLength > 0 && previous.Length != source.ExpectedLength) return false;
            return true;
        }
        catch { return false; }
    }

    private static string? ReadHeadFingerprint(SqliteConnection connection, SqliteTransaction tx, EntityKey key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT head_fingerprint FROM phase_d_head WHERE catalog=$catalog AND entity_uid=$uid;";
        command.Parameters.AddWithValue("$catalog", key.Catalog);
        command.Parameters.AddWithValue("$uid", key.Uid.ToString("N"));
        return command.ExecuteScalar() as string;
    }

    private static string HeadFingerprint(PhaseDMaterializedHead head)
    {
        var concurrent = string.Join(",", head.ConcurrentHeads.OrderBy(x => x.MutationId).Select(x => x.MutationId.ToString("N")));
        var raw = head.Catalog + "|" + head.EntityUid.ToString("N") + "|" + head.Operation + "|" +
                  head.BusinessKey + "|" + head.PayloadSha256 + "|" + PhaseDVersionVector.Fingerprint(head.VersionVector) +
                  "|" + head.HasConflict + "|" + concurrent;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static void BumpCatalogRevision(SqliteConnection connection, SqliteTransaction tx, string catalog)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO phase_d_catalog_state(catalog,revision) VALUES($catalog,1)
            ON CONFLICT(catalog) DO UPDATE SET revision = revision + 1;
            """;
        command.Parameters.AddWithValue("$catalog", catalog);
        command.ExecuteNonQuery();
    }

    private static long ReadCatalogRevision(SqliteConnection connection, string catalog)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision FROM phase_d_catalog_state WHERE catalog=$catalog;";
        command.Parameters.AddWithValue("$catalog", catalog);
        return command.ExecuteScalar() is long value ? value : 0;
    }

    /// <summary>Teste Upsert del catalogo, incluse quelle in conflitto: deve coincidere con il conteggio usato da
    /// <see cref="LoadCatalog{T}"/> nell'impronta, altrimenti GetCatalogState e Load non risultano mai uguali.</summary>
    private static int CountActive(SqliteConnection connection, string catalog)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM phase_d_head WHERE catalog=$catalog AND operation=$upsert;";
        command.Parameters.AddWithValue("$catalog", catalog);
        command.Parameters.AddWithValue("$upsert", (int)PhaseDMasterMutationOperation.Upsert);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private void Initialize()
    {
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                SqliteWalGuard.CheckpointIfOversized(connection, DatabasePath);
                CreateSchema(connection);
                CreaIndiceDuplicatiNocase(connection);
                var schema = ReadMeta(connection, "schema_version");
                if (!string.IsNullOrWhiteSpace(schema) &&
                    schema != SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    throw new LocalIndexSchemaIncompatibleException($"Schema SQLite master-v2 locale incompatibile: {schema}.");
                using var tx = connection.BeginTransaction();
                WriteMeta(connection, tx, "schema_version", SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
                tx.Commit();
            }
            catch (LocalIndexSchemaIncompatibleException)
            {
                // Versione non interpretabile: cache ricostruibile, eliminata senza copia .corrupt-*. Se il file
                // non si puo' eliminare (bloccato) nulla e' stato cancellato e si ricade sulla quarantena.
                if (LocalIndexQuarantine.TryEliminaIncompatibile(DatabasePath, out _)) RecreateSchema();
                else QuarantineAndRecreate();
            }
            catch
            {
                QuarantineAndRecreate();
            }
        }
    }

    /// <summary>Nome dell'indice di copertura per la verifica di chiavi duplicate senza distinzione di maiuscole
    /// (<see cref="PhaseDMasterScalableReader.SqlDuplicatiAttivi"/> e OscuraDuplicatiDiChiave).</summary>
    internal const string IndiceDuplicatiNocase = "idx_phase_d_head_dup_nocase";

    /// <summary>Crea l'indice se manca. E' cache locale ricostruibile: la creazione e' idempotente, avviene anche sui DB
    /// creati da versioni precedenti (senza alzare SchemaVersion, quindi senza ricostruzione del DB) e, in caso di errore
    /// (per esempio DB occupato da un'altra istanza), NON fa quarantena: la verifica dei duplicati resta corretta anche
    /// senza indice, solo piu' lenta, e il tentativo si ripete all'inizializzazione successiva.</summary>
    private static void CreaIndiceDuplicatiNocase(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"CREATE INDEX IF NOT EXISTS {IndiceDuplicatiNocase} ON phase_d_head(catalog, conflict, operation, business_key COLLATE NOCASE, entity_uid);";
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            CrashLogService.Registra("Indice locale Phase D non creato: verifica duplicati piu' lenta, non bloccante", ex);
        }
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS phase_d_source(
                source_path TEXT PRIMARY KEY,
                device_id TEXT NOT NULL,
                source_kind INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                length INTEGER NOT NULL,
                complete INTEGER NOT NULL,
                max_device_sequence INTEGER NOT NULL,
                covered_through INTEGER NOT NULL,
                last_write_ticks INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_phase_d_source_device ON phase_d_source(device_id, source_kind);
            CREATE TABLE IF NOT EXISTS phase_d_mutation(
                mutation_id TEXT PRIMARY KEY,
                catalog TEXT NOT NULL,
                entity_uid TEXT NOT NULL,
                mutation_json TEXT NOT NULL,
                mutation_sha256 TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_phase_d_mutation_entity ON phase_d_mutation(catalog, entity_uid);
            CREATE TABLE IF NOT EXISTS phase_d_source_mutation(
                source_path TEXT NOT NULL,
                mutation_id TEXT NOT NULL,
                device_sequence INTEGER NOT NULL,
                PRIMARY KEY(source_path, mutation_id)
            );
            CREATE INDEX IF NOT EXISTS idx_phase_d_source_mutation_id ON phase_d_source_mutation(mutation_id);
            CREATE TABLE IF NOT EXISTS phase_d_head(
                catalog TEXT NOT NULL,
                entity_uid TEXT NOT NULL,
                operation INTEGER NOT NULL,
                business_key TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                vector_json TEXT NOT NULL,
                conflict INTEGER NOT NULL,
                concurrent_heads_json TEXT NOT NULL,
                head_fingerprint TEXT NOT NULL,
                PRIMARY KEY(catalog, entity_uid)
            );
            CREATE INDEX IF NOT EXISTS idx_phase_d_head_conflict ON phase_d_head(conflict, catalog);
            CREATE INDEX IF NOT EXISTS idx_phase_d_head_business ON phase_d_head(catalog, business_key, operation, conflict);
            CREATE TABLE IF NOT EXISTS phase_d_catalog_state(
                catalog TEXT PRIMARY KEY,
                revision INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS phase_d_meta(
                meta_key TEXT PRIMARY KEY,
                meta_value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=1500;";
        busy.ExecuteNonQuery();
        return connection;
    }

    private void QuarantineAndRecreate()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(DatabasePath))
                File.Move(DatabasePath, DatabasePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), overwrite: false);
            TryDelete(DatabasePath + "-wal");
            TryDelete(DatabasePath + "-shm");
        }
        catch { }
        RecreateSchema();
    }

    /// <summary>Crea lo schema nuovo su un file locale gia' eliminato o messo in quarantena.</summary>
    private void RecreateSchema()
    {
        using var connection = Open();
        CreateSchema(connection);
        using var tx = connection.BeginTransaction();
        WriteMeta(connection, tx, "schema_version", SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        tx.Commit();
    }

    private static string? ReadMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT meta_value FROM phase_d_meta WHERE meta_key=$key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static long ReadLongMeta(SqliteConnection connection, SqliteTransaction? transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT meta_value FROM phase_d_meta WHERE meta_key=$key;";
        command.Parameters.AddWithValue("$key", key);
        var text = command.ExecuteScalar() as string;
        return long.TryParse(text, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static void WriteMeta(SqliteConnection connection, SqliteTransaction tx, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO phase_d_meta(meta_key,meta_value) VALUES($key,$value)
            ON CONFLICT(meta_key) DO UPDATE SET meta_value=excluded.meta_value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string CatalogFingerprint(string catalog, long revision, int count) =>
        $"PD2:{catalog}:{revision}:{count}";

    private static long SafeLastWriteTicks(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; }
        catch { return 0; }
    }

    private static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private readonly record struct EntityKey(string Catalog, Guid Uid);
    private sealed record SourceState(
        string Path,
        string DeviceId,
        PhaseDMasterJournalSourceKind Kind,
        string Sha256,
        long Length,
        bool Complete,
        long MaxDeviceSequence,
        long CoveredThrough,
        long LastWriteTicks);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

/// <summary>
/// Lanciata SOLO quando un'operazione tocca una voce con teste concorrenti non risolte (salvataggio/eliminazione della
/// voce, lettura singola via <see cref="PhaseDMasterScalableReader.LoadActiveEntity{T}"/>). La lettura dell'intero catalogo
/// non la lancia piu'. Le altre voci dello stesso catalogo restano modificabili.
/// </summary>
public sealed class PhaseDMasterConflictException : IOException
{
    public string Catalog { get; }
    public IReadOnlyList<Guid> EntityUids { get; }

    public PhaseDMasterConflictException(string catalog, IReadOnlyList<Guid> entityUids)
        : base($"La modifica tocca {entityUids.Count} voce/i del catalogo Phase D '{catalog}' con teste concorrenti non risolte: " +
               "restano bloccate finche' un Master non sceglie la versione corretta (Impostazioni > Conflitti nei cataloghi master). " +
               "Nessun last-writer-wins e' stato applicato.")
    {
        Catalog = catalog;
        EntityUids = entityUids;
    }
}
