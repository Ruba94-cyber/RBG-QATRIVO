using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Read-model/cache SQLite LOCALE delle Non Conformita, stesso principio di LocalTestIndexStore per i Test
/// (feedback utente 2026-09-17: "per test era stato inserito un db sqlite", volumi 100k). Non e' mai source of
/// truth e non va mai collocato nella cartella OneDrive/SharePoint: i JSON nc_*.json restano l'unica fonte
/// autorevole e il DB puo' essere cancellato/corrotto in qualunque momento senza perdita dati — il successivo
/// ReconcileFull lo ricostruisce dai JSON. A differenza di LocalTestIndexStore non porta alcuna proiezione
/// storica/statistica né FTS: le NC non hanno un equivalente delle statistiche per-operatore/funzionalita' dei
/// Test, quindi qui basta la sola cache path→payload che rende il warm-start indipendente dal numero di file.
/// </summary>
public sealed class LocalNonConformitaIndexStore
{
    /// <summary>v4: tabelle strette nc_filter/nc_search con le colonne di filtro e ordinamento della pagina
    /// Anomalie (stato, priorita', gravita', assegnatario, date, presenza ticket TICKET e testo cercabile), separate da nc_cache perche'
    /// la scansione non deve attraversare il payload json di ogni riga. nc_cache resta identica alla v1.
    /// Le migrazioni avvengono in una sola transazione con backfill dalla colonna json gia' presente: se falliscono
    /// a meta' restano atomiche e al prossimo avvio si ritentano; i JSON autorevoli condivisi non vengono mai letti ne'
    /// riscritti. Le due tabelle sono interamente derivabili: ReconcileFull le riallinea.</summary>
    private const int CurrentSchemaVersion = 4;
    private const int MigrationBatchSize = 2000;

    /// <summary>Solo test: invocato dopo ogni batch di backfill della migrazione dell'indice (parametro: righe
    /// elaborate finora). Un'eccezione lanciata qui simula un crash a meta' migrazione.</summary>
    internal static Action<int>? FaultInjectionMigrazione;
    private const int BusyTimeoutMs = 750;
    private const int LoadBatchSize = 256;
    private const string FullScanCompletedKey = "full_scan_completed";
    private const string LastFullScanUtcKey = "last_full_scan_utc";
    private readonly string _connectionString;

    public string DatabasePath { get; }
    public string WorkspaceDirectory { get; }

    /// <summary>Motivo per cui l'indice NC non e' utilizzabile in questa sessione (file bloccato, nemmeno la quarantena
    /// riesce). Null quando lo store e' disponibile. Finche' e' valorizzato ogni operazione lancia LocalIndexUnavailableException.</summary>
    public string? IndisponibileMotivo { get; private set; }

    /// <summary>localBaseDirectory esiste solo per test/diagnostica; in produzione il DB vive sotto
    /// %LOCALAPPDATA%\TestLoom\workspaces\&lt;hash-percorso&gt;, stessa cartella per-workspace di LocalTestIndexStore
    /// ma con un file proprio (nc-index.db) cosi' i due schemi non condividono mai una connessione/tabelle.</summary>
    public LocalNonConformitaIndexStore(string sharedRoot, string? localBaseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(sharedRoot))
            throw new ArgumentException("La cartella condivisa non può essere vuota.", nameof(sharedRoot));

        // Stessa chiave di LocalTestIndexStore (pinned all'apertura o canonica): stessa cartella per-workspace.
        var workspaceKey = WorkspaceLocalPaths.ComputeWorkspaceKey(sharedRoot);
        var baseDir = localBaseDirectory ?? Path.Combine(
            WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", "workspaces");
        WorkspaceDirectory = Path.Combine(baseDir, workspaceKey);
        Directory.CreateDirectory(WorkspaceDirectory);
        DatabasePath = Path.Combine(WorkspaceDirectory, "nc-index.db");

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
            // File bloccato anche per la quarantena: lo store resta "indisponibile" e il chiamante degrada sui JSON.
            IndisponibileMotivo = ex.Message;
            CrashLogService.Registra("Indice SQLite NC: non disponibile in questa sessione (letture dai JSON)", ex);
        }
    }

    /// <summary>Righe scritte (upsert) da questa istanza nella riconciliazione completa: misura per banchi e test di equivalenza.</summary>
    public long RigheScritteTotali { get; private set; }

    public bool HasCompletedFullScan => string.Equals(ReadMeta(FullScanCompletedKey), "1", StringComparison.Ordinal);

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

    /// <summary>Carica la fotografia locale senza toccare OneDrive: rende il warm-start indipendente dal
    /// numero di file JSON. Deserializzazione a batch in parallelo, stesso schema di LocalTestIndexStore.LoadAll.
    /// Record illeggibili vengono ignorati; il successivo ReconcileFull li rigenera dal JSON autorevole.</summary>
    /// <summary>Candidati soltanto: la conferma legacy resta sul Test corrente, mai persistita nella cache.</summary>
    public IReadOnlyList<string> QueryTestLinkPaths(IReadOnlyCollection<string> codes, IReadOnlyCollection<Guid> uids)
    {
        using var connection = Open();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in codes.Select(c => (Value: c, Uid: false)).Concat(uids.Select(u => (Value: u.ToString("N"), Uid: true))).Chunk(400))
        {
            using var command = connection.CreateCommand();
            var clauses = new List<string>();
            for (var i = 0; i < batch.Length; i++)
            {
                clauses.Add(batch[i].Uid ? $"test_uid = $p{i}" : $"test_id COLLATE NOCASE = $p{i}");
                command.Parameters.AddWithValue($"$p{i}", batch[i].Value);
            }
            command.CommandText = $"SELECT DISTINCT path FROM nc_test_link WHERE {string.Join(" OR ", clauses)};";
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetString(0));
        }
        return result.ToList();
    }

    public IReadOnlyList<LocalIndexedNc> LoadAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path, mtime_utc_ticks, file_length, content_hash, json
            FROM nc_cache
            ORDER BY path;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<LocalIndexedNc>();
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

    private static void ParseBatch(List<RawIndexedRow> batch, List<LocalIndexedNc> destination)
    {
        var parsed = new LocalIndexedNc?[batch.Count];
        Parallel.For(0, batch.Count, i =>
        {
            var row = batch[i];
            var nc = JsonIo.TryParse<NonConformita>(row.Json, out _);
            if (nc == null) return;
            parsed[i] = new LocalIndexedNc(row.Path, new DateTime(row.MtimeUtcTicks, DateTimeKind.Utc), row.FileLength, row.ContentHash, nc);
        });
        foreach (var item in parsed)
            if (item != null) destination.Add(item);
    }

    /// <summary>Aggiorna una sola entità dopo che il JSON autorevole è stato scritto con successo (Save).</summary>
    public void Upsert(string path, DateTime mtimeUtc, long fileLength, NonConformita nc, string? precomputedHash = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = CreateUpsertCommand(connection, transaction);
        BindUpsert(command, path, mtimeUtc, fileLength, nc, precomputedHash);
        command.ExecuteNonQuery();
        using var filtro = new FilterUpsert(connection, transaction);
        filtro.Esegui(path, nc);
        transaction.Commit();
    }

    /// <summary>Rimuove una entità dopo eliminazione/tombstone (Elimina).</summary>
    public void Remove(string path)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM nc_search WHERE fid IN (SELECT fid FROM nc_filter WHERE path = $path);
            DELETE FROM nc_test_link WHERE path = $path;
            DELETE FROM nc_filter WHERE path = $path;
            DELETE FROM nc_cache WHERE path = $path;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>
    /// Allinea atomicamente il DB locale alla fotografia completa verificata dei JSON (full reconciliation).
    /// Stesso schema di LocalTestIndexStore.ReconcileFull: una singola transazione, righe non piu' presenti
    /// rimosse in blocco.
    /// </summary>
    public void ReconcileFull(IEnumerable<LocalIndexWriteEntryNc> entries, DateTime completedUtc)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var createSeen = connection.CreateCommand())
        {
            createSeen.Transaction = transaction;
            createSeen.CommandText = "CREATE TEMP TABLE IF NOT EXISTS seen_nc_paths(path TEXT PRIMARY KEY); DELETE FROM seen_nc_paths;";
            createSeen.ExecuteNonQuery();
        }

        using var seen = connection.CreateCommand();
        seen.Transaction = transaction;
        seen.CommandText = "INSERT OR IGNORE INTO seen_nc_paths(path) VALUES($path);";
        var seenPath = seen.Parameters.Add("$path", SqliteType.Text);
        seen.Prepare();

        using var upsert = CreateUpsertCommand(connection, transaction);
        upsert.Prepare();
        using var filtro = new FilterUpsert(connection, transaction);

        // Righe gia' presenti lette una sola volta: una NC invariata (stessi path, mtime, lunghezza, hash e Uid) non viene
        // ne' riscritta ne' rifiltrata. Prima ogni riconciliazione completa riscriveva e rifiltrava l'intero archivio.
        var esistenti = LoadNcStamps(connection, transaction);
        foreach (var entry in entries)
        {
            seenPath.Value = entry.Path;
            seen.ExecuteNonQuery();
            var hash = entry.ContentHash ?? EntityStateFingerprint.Compute(entry.Nc);
            var uid = entry.Nc.Uid.ToString("N");
            if (esistenti.TryGetValue(entry.Path, out var riga) &&
                riga.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase) &&
                riga.Mtime == entry.MtimeUtc.ToUniversalTime().Ticks &&
                riga.Length == entry.FileLength && riga.Uid == uid)
                continue;

            RigheScritteTotali++;
            BindUpsert(upsert, entry.Path, entry.MtimeUtc, entry.FileLength, entry.Nc, hash);
            upsert.ExecuteNonQuery();
            filtro.Esegui(entry.Path, entry.Nc);
        }

        using (var removeStale = connection.CreateCommand())
        {
            removeStale.Transaction = transaction;
            removeStale.CommandText = """
                DELETE FROM nc_search WHERE fid IN (
                    SELECT fid FROM nc_filter
                    WHERE NOT EXISTS (SELECT 1 FROM seen_nc_paths s WHERE s.path = nc_filter.path));
                DELETE FROM nc_filter
                WHERE NOT EXISTS (SELECT 1 FROM seen_nc_paths s WHERE s.path = nc_filter.path);
                DELETE FROM nc_test_link WHERE NOT EXISTS (SELECT 1 FROM seen_nc_paths s WHERE s.path = nc_test_link.path);
                DELETE FROM nc_cache
                WHERE NOT EXISTS (SELECT 1 FROM seen_nc_paths s WHERE s.path = nc_cache.path);
                """;
            removeStale.ExecuteNonQuery();
        }

        WriteMeta(connection, transaction, FullScanCompletedKey, "1");
        WriteMeta(connection, transaction, LastFullScanUtcKey, completedUtc.ToUniversalTime().ToString("O"));
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
            LocalIndexFaultKind.Corruption or LocalIndexFaultKind.SchemaIncompatible)
        {
            guasto = LocalIndexFaultClassifier.Classify(ex);
            CrashLogService.Registra($"Indice SQLite locale NC: reset all'avvio ({LocalIndexFaultClassifier.Descrivi(guasto)})", ex);
        }

        // BUSY, disco pieno e ACL non dimostrano che il contenuto sia guasto: qui si isolano soltanto corruzione
        // (quarantena, con WAL e SHM per la diagnosi) o versione non interpretabile (eliminazione diretta, cache ricostruibile).
        if (!LocalIndexQuarantine.TryRecuperaAllAvvio(DatabasePath, guasto, out var motivo))
            throw new LocalIndexUnavailableException($"NC non recuperabile all'avvio. {motivo}");
        InitializeCore();
    }

    /// <summary>Solo dopo la quarantena coordinata: il nuovo file locale deve avere lo schema prima del reconcile.</summary>
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

    /// <summary>Due processi TestLoom sullo stesso PC condividono nc-index.db: la migrazione (che scrive) puo' trovare
    /// il database occupato oltre il busy_timeout. SQLITE_BUSY e' contesa transitoria, non corruzione: si ritenta
    /// con una breve attesa invece di far fallire l'avvio o di mettere in quarantena un indice sano.</summary>
    private void InitializeCore()
    {
        for (var tentativo = 0; ; tentativo++)
        {
            try { InitializeCoreOnce(); return; }
            catch (Exception ex) when (tentativo < 20 && LocalIndexFaultClassifier.Classify(ex) == LocalIndexFaultKind.Busy)
            {
                Thread.Sleep(250);
            }
        }
    }

    private void InitializeCoreOnce()
    {
        using var connection = Open();
        SqliteWalGuard.CheckpointIfOversized(connection, DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout={BusyTimeoutMs};
            PRAGMA temp_store=MEMORY;

            CREATE TABLE IF NOT EXISTS meta(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS nc_cache(
                path TEXT PRIMARY KEY,
                uid TEXT NOT NULL,
                id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                mtime_utc_ticks INTEGER NOT NULL,
                file_length INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                json TEXT NOT NULL,
                indexed_utc_ticks INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_nc_cache_uid ON nc_cache(uid);
            CREATE INDEX IF NOT EXISTS ix_nc_cache_id ON nc_cache(id);
            """;
        command.ExecuteNonQuery();

        var schemaRaw = ReadMeta(connection, "schema_version");
        if (schemaRaw != null && (!int.TryParse(schemaRaw, out var schema) || schema is < 1 or > CurrentSchemaVersion))
            throw new LocalIndexSchemaIncompatibleException($"Schema indice locale NC non supportato: {schemaRaw}.");

        using var transaction = connection.BeginTransaction();
        // Equivalente di BEGIN IMMEDIATE: acquisire SUBITO il lock di scrittura (statement di scrittura che non
        // modifica righe) evita che una transazione iniziata come lettura fallisca piu' tardi con
        // BUSY_SNAPSHOT quando un altro processo ha scritto nel frattempo.
        using (var lockScrittura = connection.CreateCommand())
        {
            lockScrittura.Transaction = transaction;
            lockScrittura.CommandText = "UPDATE meta SET value = value WHERE key = 'schema_version';";
            lockScrittura.ExecuteNonQuery();
        }
        using (var ddl = connection.CreateCommand())
        {
            ddl.Transaction = transaction;
            ddl.CommandText = """
                CREATE TABLE IF NOT EXISTS nc_test_link(path TEXT NOT NULL, test_uid TEXT, test_id TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_nc_test_link_path ON nc_test_link(path);
                CREATE INDEX IF NOT EXISTS ix_nc_test_link_uid ON nc_test_link(test_uid, path);
                CREATE INDEX IF NOT EXISTS ix_nc_test_link_id ON nc_test_link(test_id COLLATE NOCASE, path);
                """;
            ddl.ExecuteNonQuery();
        }
        EnsureFilterSchema(connection, transaction);
        if (schemaRaw != "4") BackfillFilterTables(connection, transaction);
        WriteMeta(connection, transaction, "schema_version", CurrentSchemaVersion.ToString());
        transaction.Commit();
    }

    /// <summary>Crea (se assenti) le tabelle di filtro dentro la transazione del chiamante. Se nc_filter non
    /// esisteva (DB v1, o DB nuovo) e nc_cache ha gia' righe, le popola dal json locale: atomico con l'avanzamento
    /// di schema_version, quindi un crash a meta' lascia il DB v1 intatto e la migrazione viene ritentata.
    /// Righe con json non interpretabile non producono voce di filtro: LoadAll le ignora gia' e il successivo
    /// ReconcileFull le rigenera dal JSON autorevole.</summary>
    private static void EnsureFilterSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        bool esisteva;
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='nc_filter';";
            esisteva = Convert.ToInt64(check.ExecuteScalar()) > 0;
        }

        if (esisteva)
        {
            // Versioni di sviluppo di nc_filter non avevano la chiave 'fid' esplicita: nc_search si appoggiava al
            // rowid implicito, che un VACUUM puo' rinumerare (testo associato alla NC sbagliata). Tabelle
            // derivate: si scartano e si ricostruiscono dal json locale.
            bool haFid;
            using (var info = connection.CreateCommand())
            {
                info.Transaction = transaction;
                info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('nc_filter') WHERE name='fid' AND pk=1;";
                haFid = Convert.ToInt64(info.ExecuteScalar()) > 0;
            }
            if (!haFid)
            {
                using var drop = connection.CreateCommand();
                drop.Transaction = transaction;
                drop.CommandText = "DROP TABLE IF EXISTS nc_search; DROP TABLE IF EXISTS nc_filter;";
                drop.ExecuteNonQuery();
                esisteva = false;
            }
        }

        using (var ddl = connection.CreateCommand())
        {
            ddl.Transaction = transaction;
            ddl.CommandText = """
                CREATE TABLE IF NOT EXISTS nc_filter(
                    fid INTEGER PRIMARY KEY,
                    path TEXT NOT NULL UNIQUE,
                    uid TEXT NOT NULL,
                    id TEXT NOT NULL,
                    sig INTEGER NOT NULL,
                    stato_workflow INTEGER NOT NULL,
                    stato INTEGER NOT NULL,
                    stato_dettaglio INTEGER,
                    destinatario INTEGER,
                    priorita INTEGER NOT NULL,
                    severita INTEGER,
                    owner TEXT NOT NULL,
                    apertura_ticks INTEGER NOT NULL,
                    chiusura_ticks INTEGER,
                    descrizione TEXT NOT NULL,
                    ha_ticket_esta INTEGER NOT NULL DEFAULT 0,
                    archiviata INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS nc_search(
                    fid INTEGER PRIMARY KEY,
                    search_text TEXT NOT NULL
                );
                """;
            ddl.ExecuteNonQuery();
        }

        bool haTicketEsta;
        using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('nc_filter') WHERE name='ha_ticket_esta';";
            haTicketEsta = Convert.ToInt64(info.ExecuteScalar()) > 0;
        }
        if (!haTicketEsta)
        {
            using var migra = connection.CreateCommand();
            migra.Transaction = transaction;
            migra.CommandText = "ALTER TABLE nc_filter ADD COLUMN ha_ticket_esta INTEGER NOT NULL DEFAULT 0;";
            migra.ExecuteNonQuery();
        }
        bool haArchiviata;
        using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('nc_filter') WHERE name='archiviata';";
            haArchiviata = Convert.ToInt64(info.ExecuteScalar()) > 0;
        }
        if (!haArchiviata)
        {
            // Le righe esistenti restano a 0 (attive): la firma include Archiviata, quindi il primo aggiornamento le riallinea.
            using var migra = connection.CreateCommand();
            migra.Transaction = transaction;
            migra.CommandText = "ALTER TABLE nc_filter ADD COLUMN archiviata INTEGER NOT NULL DEFAULT 0;";
            migra.ExecuteNonQuery();
        }
        using (var indiceTicket = connection.CreateCommand())
        {
            indiceTicket.Transaction = transaction;
            indiceTicket.CommandText = "CREATE INDEX IF NOT EXISTS ix_nc_filter_ticket_esta ON nc_filter(ha_ticket_esta);";
            indiceTicket.ExecuteNonQuery();
        }

        if (!esisteva) BackfillFilterTables(connection, transaction);
    }

    private static void BackfillFilterTables(SqliteConnection connection, SqliteTransaction transaction)
    {
        long ultimoRowid = 0;
        var elaborate = 0;
        while (true)
        {
            var batch = new List<(long Rowid, string Path, string Json)>(MigrationBatchSize);
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT rowid, path, json FROM nc_cache WHERE rowid > $last ORDER BY rowid LIMIT $n;";
                select.Parameters.AddWithValue("$last", ultimoRowid);
                select.Parameters.AddWithValue("$n", MigrationBatchSize);
                using var reader = select.ExecuteReader();
                while (reader.Read()) batch.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
            if (batch.Count == 0) break;

            var calcolate = new NonConformita?[batch.Count];
            Parallel.For(0, batch.Count, i => calcolate[i] = JsonIo.TryParse<NonConformita>(batch[i].Json, out _));

            using var filtro = new FilterUpsert(connection, transaction);
            for (var i = 0; i < batch.Count; i++)
                if (calcolate[i] is { } nc) filtro.Esegui(batch[i].Path, nc);

            ultimoRowid = batch[^1].Rowid;
            elaborate += batch.Count;
            FaultInjectionMigrazione?.Invoke(elaborate);
        }
    }

    /// <summary>Upsert della riga di filtro e del testo cercabile di una NC. Le scritture sono condizionate alla
    /// firma dei valori proiettati: a ogni refresh completo (ReconcileFull riscrive l'intero archivio) una NC
    /// invariata non produce alcuna scrittura sulle tabelle di filtro.</summary>
    private sealed class FilterUpsert : IDisposable
    {
        private readonly SqliteCommand _riga;
        private readonly SqliteCommand _links;
        private readonly SqliteCommand _testo;

        public FilterUpsert(SqliteConnection connection, SqliteTransaction transaction)
        {
            _links = connection.CreateCommand();
            _links.Transaction = transaction;
            _links.CommandText = """
                DELETE FROM nc_test_link WHERE path = $path;
                INSERT INTO nc_test_link(path, test_uid, test_id)
                SELECT $path, $uid, $id WHERE $uid IS NOT NULL OR $id <> '';
                INSERT INTO nc_test_link(path, test_uid, test_id)
                SELECT $path, replace(lower(json_extract(value, '$.TestUid')), '-', ''), COALESCE(json_extract(value, '$.TestId'), '')
                FROM json_each($proofs);
                """;
            _links.Parameters.Add("$path", SqliteType.Text);
            _links.Parameters.Add("$uid", SqliteType.Text);
            _links.Parameters.Add("$id", SqliteType.Text);
            _links.Parameters.Add("$proofs", SqliteType.Text);
            _riga = connection.CreateCommand();
            _riga.Transaction = transaction;
            _riga.CommandText = """
                INSERT INTO nc_filter(path, uid, id, sig, stato_workflow, stato, stato_dettaglio, destinatario, priorita, severita,
                    owner, apertura_ticks, chiusura_ticks, descrizione, ha_ticket_esta, archiviata)
                VALUES($path, $uid, $id, $sig, $sw, $st, $sd, $de, $pr, $se, $ow, $ap, $ch, $ds, $te, $ar)
                ON CONFLICT(path) DO UPDATE SET
                    uid = excluded.uid, id = excluded.id, sig = excluded.sig,
                    stato_workflow = excluded.stato_workflow, stato = excluded.stato, stato_dettaglio = excluded.stato_dettaglio,
                    destinatario = excluded.destinatario, priorita = excluded.priorita, severita = excluded.severita,
                    owner = excluded.owner, apertura_ticks = excluded.apertura_ticks, chiusura_ticks = excluded.chiusura_ticks,
                    descrizione = excluded.descrizione, ha_ticket_esta = excluded.ha_ticket_esta, archiviata = excluded.archiviata
                WHERE nc_filter.sig <> excluded.sig;
                """;
            foreach (var (nome, tipo) in new[]
            {
                ("$path", SqliteType.Text), ("$uid", SqliteType.Text), ("$id", SqliteType.Text), ("$sig", SqliteType.Integer),
                ("$sw", SqliteType.Integer), ("$st", SqliteType.Integer), ("$sd", SqliteType.Integer), ("$de", SqliteType.Integer),
                ("$pr", SqliteType.Integer), ("$se", SqliteType.Integer), ("$ow", SqliteType.Text), ("$ap", SqliteType.Integer),
                ("$ch", SqliteType.Integer), ("$ds", SqliteType.Text), ("$te", SqliteType.Integer), ("$ar", SqliteType.Integer)
            })
                _riga.Parameters.Add(nome, tipo);

            _testo = connection.CreateCommand();
            _testo.Transaction = transaction;
            _testo.CommandText = """
                INSERT INTO nc_search(fid, search_text)
                SELECT fid, $tx FROM nc_filter WHERE path = $path
                ON CONFLICT(fid) DO UPDATE SET search_text = excluded.search_text
                WHERE nc_search.search_text <> excluded.search_text;
                """;
            _testo.Parameters.Add("$path", SqliteType.Text);
            _testo.Parameters.Add("$tx", SqliteType.Text);
        }

        public void Esegui(string path, NonConformita nc)
        {
            _links.Parameters["$path"].Value = path;
            _links.Parameters["$uid"].Value = (object?)nc.TestUid?.ToString("N") ?? DBNull.Value;
            _links.Parameters["$id"].Value = nc.TestCaseId;
            _links.Parameters["$proofs"].Value = JsonSerializer.Serialize(nc.ProveCollegate, JsonIo.CompactOptions);
            _links.ExecuteNonQuery();
            var r = AnomalieIndiceRiga.Da(nc);
            var p = _riga.Parameters;
            p["$path"].Value = path;
            p["$uid"].Value = nc.Uid.ToString("N");
            p["$id"].Value = nc.Id ?? "";
            p["$sig"].Value = r.Firma();
            p["$sw"].Value = r.StatoWorkflow;
            p["$st"].Value = r.Stato;
            p["$sd"].Value = (object?)r.StatoDettaglio ?? DBNull.Value;
            p["$de"].Value = (object?)r.Destinatario ?? DBNull.Value;
            p["$pr"].Value = r.Priorita;
            p["$se"].Value = (object?)r.Severita ?? DBNull.Value;
            p["$ow"].Value = r.Owner;
            p["$ap"].Value = r.AperturaTicks;
            p["$ch"].Value = (object?)r.ChiusuraTicks ?? DBNull.Value;
            p["$ds"].Value = r.Descrizione;
            p["$te"].Value = r.HaTicketEsta ? 1 : 0;
            p["$ar"].Value = r.Archiviata ? 1 : 0;
            _riga.ExecuteNonQuery();

            _testo.Parameters["$path"].Value = path;
            _testo.Parameters["$tx"].Value = r.TestoCercabile;
            _testo.ExecuteNonQuery();
        }

        public void Dispose()
        {
            _links.Dispose();
            _riga.Dispose();
            _testo.Dispose();
        }
    }

    // ------------------------------------------------ interrogazione filtrata (pagina Anomalie)

    private sealed class LetturaScope(SqliteConnection connection) : IDisposable
    {
        public SqliteConnection Connection { get; } = connection;

        /// <summary>La connessione torna al pool: il pragma read_uncommitted va ripristinato, altrimenti un altro
        /// chiamante (scrittura/migrazione) la riceverebbe con letture sporche attive.</summary>
        public void Dispose()
        {
            try
            {
                using var command = Connection.CreateCommand();
                command.CommandText = "PRAGMA read_uncommitted=0;";
                command.ExecuteNonQuery();
            }
            catch { }
            Connection.Dispose();
        }
    }

    private LetturaScope OpenRead()
    {
        var connection = Open();
        // Cache condivisa: un lettore che incontra un writer sulla stessa tabella riceverebbe SQLITE_LOCKED
        // immediato (busy_timeout non si applica). L'indice e' solo derivato e ricostruibile: leggere anche
        // righe non ancora committate e' innocuo, un guasto vero ricade nel ripiego in memoria del chiamante.
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA read_uncommitted=1; PRAGMA mmap_size=536870912; PRAGMA cache_size=-65536;";
            command.ExecuteNonQuery();
            FaultInjectionOpenRead?.Invoke();
            return new LetturaScope(connection);
        }
        catch
        {
            // Un pragma fallito (o un guasto subito dopo) non deve restituire al pool una connessione con
            // letture sporche attive ne' lasciarla aperta: ripristino best-effort e dispose, poi si rilancia.
            new LetturaScope(connection).Dispose();
            throw;
        }
    }

    /// <summary>Solo test: eccezione simulata subito dopo l'impostazione dei pragma di lettura.</summary>
    internal static Action? FaultInjectionOpenRead;

    /// <summary>Totale e pagina sulla stessa connessione (un solo open). Sono DUE statement: non c'e' garanzia di
    /// istantanea unica; il repository scarta il risultato se la cache cambia durante la query.</summary>
    public (long Totale, IReadOnlyList<string> Percorsi) QueryPagina(AnomalieFiltroNc filtro, AnomalieOrdinamento ordinamento, long offset, int limit)
    {
        using var scope = OpenRead(); var connection = scope.Connection;
        long totale = -1;
        if (limit >= 0)
        {
            using var count = connection.CreateCommand();
            var whereCount = CostruisciWhere(filtro, count);
            count.CommandText = $"SELECT COUNT(*) FROM nc_filter{whereCount};";
            totale = Convert.ToInt64(count.ExecuteScalar());
        }
        using var command = connection.CreateCommand();
        var where = CostruisciWhere(filtro, command);
        command.CommandText = $"SELECT path FROM nc_filter{where} ORDER BY {Ordinamento(ordinamento)} LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", limit < 0 ? -1 : limit);
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        using var reader = command.ExecuteReader();
        var result = new List<string>(limit < 0 ? 1024 : Math.Min(limit, 4096));
        while (reader.Read()) result.Add(reader.GetString(0));
        return (totale < 0 ? result.Count : totale, result);
    }

    /// <summary>Percorsi (nc_cache.path) delle NC che soddisfano il filtro, nell'ordine richiesto. offset/limit
    /// paginano sul risultato filtrato; limit &lt; 0 restituisce tutto.</summary>
    public IReadOnlyList<string> QueryPercorsi(AnomalieFiltroNc filtro, AnomalieOrdinamento ordinamento, long offset, int limit)
    {
        using var scope = OpenRead(); var connection = scope.Connection;
        using var command = connection.CreateCommand();
        var where = CostruisciWhere(filtro, command);
        command.CommandText = $"SELECT path FROM nc_filter{where} ORDER BY {Ordinamento(ordinamento)} LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", limit < 0 ? -1 : limit);
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        using var reader = command.ExecuteReader();
        var result = new List<string>(limit < 0 ? 1024 : Math.Min(limit, 4096));
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public long Conta(AnomalieFiltroNc filtro)
    {
        using var scope = OpenRead(); var connection = scope.Connection;
        using var command = connection.CreateCommand();
        var where = CostruisciWhere(filtro, command);
        command.CommandText = $"SELECT COUNT(*) FROM nc_filter{where};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>Assegnatari distinti presenti nell'indice (per le opzioni del filtro), ordinati.</summary>
    public IReadOnlyList<string> AssegnatariDistinti()
    {
        using var scope = OpenRead(); var connection = scope.Connection;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT owner FROM nc_filter WHERE owner <> '' ORDER BY owner;";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private static string Ordinamento(AnomalieOrdinamento o)
    {
        var dir = o.Decrescente ? "DESC" : "ASC";
        var chiave = o.Chiave switch
        {
            AnomalieOrdinamentoChiave.Apertura => "apertura_ticks",
            AnomalieOrdinamentoChiave.Id => "id COLLATE NOCASE",
            AnomalieOrdinamentoChiave.Stato => "stato_workflow",
            AnomalieOrdinamentoChiave.Descrizione => "descrizione COLLATE NOCASE",
            AnomalieOrdinamentoChiave.Assegnatario => "owner COLLATE NOCASE",
            AnomalieOrdinamentoChiave.Priorita => "priorita",
            AnomalieOrdinamentoChiave.Gravita => "severita",
            AnomalieOrdinamentoChiave.Chiusura => "chiusura_ticks",
            _ => throw new ArgumentOutOfRangeException(nameof(o), o.Chiave, "Chiave di ordinamento non presente nell'indice.")
        };
        return $"{chiave} {dir}, id COLLATE NOCASE {dir}, uid {dir}";
    }

    /// <summary>Costruisce la clausola WHERE. Gli interi (enum) sono inlinati (valori numerici controllati, non
    /// input utente); testo e assegnatari passano sempre da parametri.</summary>
    private static string CostruisciWhere(AnomalieFiltroNc f, SqliteCommand command)
    {
        var parts = new List<string>();
        var n = 0;
        string Param(object value)
        {
            var name = "$f" + n++;
            command.Parameters.AddWithValue(name, value);
            return name;
        }

        if (f.Stato.HasValue) parts.Add($"stato = {(int)f.Stato.Value}");
        if (f.StatoDettaglio.HasValue) parts.Add($"stato_dettaglio = {(int)f.StatoDettaglio.Value}");
        if (f.Destinatario.HasValue) parts.Add($"destinatario = {(int)f.Destinatario.Value}");
        if (f.StatiWorkflow.Count > 0) parts.Add($"stato_workflow IN ({string.Join(",", f.StatiWorkflow.Select(x => (int)x))})");
        if (f.Priorita.Count > 0) parts.Add($"priorita IN ({string.Join(",", f.Priorita.Select(x => (int)x))})");
        if (f.Gravita.Count > 0) parts.Add($"severita IN ({string.Join(",", f.Gravita.Select(x => (int)x))})");
        if (f.Assegnatari.Count > 0) parts.Add($"owner IN ({string.Join(",", f.Assegnatari.Select(Param))})");
        if (f.AperturaDaTicks is { } ad) parts.Add($"apertura_ticks >= {ad}");
        if (f.AperturaAEsclusoTicks is { } aa) parts.Add($"apertura_ticks < {aa}");
        if (f.DurataMinima.HasValue) throw new NotSupportedException("Il filtro durata richiede il confronto calendario in memoria.");
        if (f.ChiusuraDa.HasValue || f.ChiusuraA.HasValue)
        {
            parts.Add("chiusura_ticks IS NOT NULL");
            if (f.ChiusuraDaTicks is { } cd) parts.Add($"chiusura_ticks >= {cd}");
            if (f.ChiusuraAEsclusoTicks is { } ca) parts.Add($"chiusura_ticks < {ca}");
        }
        if (!f.IncludiArchiviate) parts.Add("archiviata = 0");
        if (f.HaTicketEsta.HasValue) parts.Add($"ha_ticket_esta = {(f.HaTicketEsta.Value ? 1 : 0)}");
        if (f.HaTesto)
            parts.Add($"fid IN (SELECT fid FROM nc_search WHERE instr(search_text, {Param(AnomalieFiltroNc.TestoRicerca(f.Testo!))}) > 0)");
        return parts.Count == 0 ? "" : " WHERE " + string.Join(" AND ", parts);
    }

    private SqliteConnection Open()
    {
        if (IndisponibileMotivo is { } motivo) throw new LocalIndexUnavailableException(motivo);
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs};";
        command.ExecuteNonQuery();
        return connection;
    }

    private static SqliteCommand CreateUpsertCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO nc_cache(path, uid, id, revision, mtime_utc_ticks, file_length, content_hash, json, indexed_utc_ticks)
            VALUES($path, $uid, $id, $revision, $mtime, $length, $hash, $json, $indexed)
            ON CONFLICT(path) DO UPDATE SET
                uid = excluded.uid,
                id = excluded.id,
                revision = excluded.revision,
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
        command.Parameters.Add("$mtime", SqliteType.Integer);
        command.Parameters.Add("$length", SqliteType.Integer);
        command.Parameters.Add("$hash", SqliteType.Text);
        command.Parameters.Add("$json", SqliteType.Text);
        command.Parameters.Add("$indexed", SqliteType.Integer);
        return command;
    }

    /// <summary>Stato di una riga di nc_cache per la riconciliazione per differenza.</summary>
    private readonly record struct NcStamp(string Hash, long Mtime, long Length, string Uid);

    private static Dictionary<string, NcStamp> LoadNcStamps(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT path, content_hash, mtime_utc_ticks, file_length, uid FROM nc_cache;";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, NcStamp>(StringComparer.Ordinal);
        while (reader.Read())
            result[reader.GetString(0)] = new NcStamp(reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4));
        return result;
    }

    private static void BindUpsert(SqliteCommand command, string path, DateTime mtimeUtc, long fileLength, NonConformita nc, string? precomputedHash)
    {
        var json = JsonSerializer.Serialize(nc, JsonIo.CompactOptions);
        var hash = precomputedHash ?? EntityStateFingerprint.Compute(nc);
        command.Parameters["$path"].Value = path;
        command.Parameters["$uid"].Value = nc.Uid.ToString("N");
        command.Parameters["$id"].Value = nc.Id ?? "";
        command.Parameters["$revision"].Value = nc.RevisioneDati;
        command.Parameters["$mtime"].Value = mtimeUtc.ToUniversalTime().Ticks;
        command.Parameters["$length"].Value = fileLength;
        command.Parameters["$hash"].Value = hash;
        command.Parameters["$json"].Value = json;
        command.Parameters["$indexed"].Value = DateTime.UtcNow.Ticks;
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


    private sealed record RawIndexedRow(string Path, long MtimeUtcTicks, long FileLength, string ContentHash, string Json);
}

public sealed record LocalIndexedNc(string Path, DateTime MtimeUtc, long FileLength, string ContentHash, NonConformita Nc);

public sealed record LocalIndexWriteEntryNc(string Path, DateTime MtimeUtc, long FileLength, NonConformita Nc, string? ContentHash = null);
