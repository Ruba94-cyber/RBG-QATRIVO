using System.Buffers;
using System.Text.Json;
using TestLoom.App.Models;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Read-side scalabile dell'archivio Test. Legge esclusivamente l'indice SQLite locale, che resta una cache
/// ricostruibile: i JSON condivisi continuano ad essere l'unica source of truth. Lista/Home possono quindi
/// lavorare su proiezioni e pagine senza materializzare l'intero archivio in RAM.
/// </summary>
public sealed class LocalTestArchiveQueryService
{
    private const int BusyTimeoutMs = 500;
    public const int DefaultPageSize = 250;
    public const int MaxPageSize = 1000;
    private readonly string _connectionString;
    private readonly object _homeFunctionalityCacheLock = new();
    private (long Generazione, long Revisione, IReadOnlyList<LocalHomeFunctionalitySource> Sources)? _cachedHomeFunctionalitySources;
    internal Action? HomeFunctionalityReadObserver { get; set; }

    /// <summary>
    /// Generazione dell'indice a cui questa istanza e' legata. Dopo una ricostruzione il file sottostante e'
    /// un altro: il chiamante confronta questo valore con quello corrente del coordinatore e ricrea il
    /// servizio, invece di continuare a usare connessioni e cache di pagina della generazione guasta.
    /// </summary>
    public long Generazione { get; }

    public LocalTestArchiveQueryService(string databasePath) : this(databasePath, null) { }

    public LocalTestArchiveQueryService(string databasePath, LocalIndexRecoveryCoordinator? coordinatore)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Percorso database locale non valido.", nameof(databasePath));

        Generazione = coordinatore?.Generazione ?? 0;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            // Shared-cache introduce lock di tabella fra reader e writer nello stesso processo, annullando il
            // vantaggio WAL proprio durante refresh concorrenti. Ogni query usa quindi la propria page cache.
            Cache = SqliteCacheMode.Private,
            Pooling = true
        }.ToString();
    }

    /// <summary>La Home richiede soltanto i componenti: evita le scansioni degli altri cinque cataloghi.</summary>
    public IReadOnlyList<string> GetEcuFilterValues(bool includeDeleted = false)
    {
        using var connection = Open();
        return GetEcuFilterValuesCore(connection, includeDeleted);
    }

    private static IReadOnlyList<string> GetEcuFilterValuesCore(SqliteConnection connection, bool includeDeleted)
    {
        var deletedUids = includeDeleted ? Array.Empty<string>() : GetEliminatoUids(connection);
        return DistinctExcludingDeleted(connection, "test_ecu", ignoreCase: true, deletedUids);
    }
    private readonly object _filterCatalogCacheLock = new();
    private (long Generazione, long Revisione, bool IncludeDeleted, LocalTestFilterCatalog Catalog)? _cachedFilterCatalog;

    /// <summary>Hook interno per i test: invocato solo quando il catalogo filtri viene davvero riletto
    /// dall'indice (cache miss), stesso schema di EcuSummaryReadObserver.</summary>
    internal Action? FilterCatalogReadObserver { get; set; }

    /// <summary>
    /// Correzione prestazioni 6.4.0 (pagina Test a freddo, 100k): il catalogo dei filtri della Lista Test
    /// (sei scansioni DISTINCT più, se il cestino non è vuoto, le scansioni NOT INDEXED per escludere i valori
    /// solo eliminati: ~250-400 ms a 100k Test) veniva riletto a OGNI visita della pagina anche a indice
    /// invariato. Stessa politica di cache di GetEcuESommarioConCache/GetRequirementCoverageCore: valida per
    /// (Generazione, revisione dell'indice, includeDeleted), quindi invalidata esattamente quando lo è già la
    /// dashboard (qualunque commit sull'indice locale, compresi quelli che applicano le modifiche arrivate da
    /// altre postazioni); nessuna dipendenza da data/dimensione dei file. LocalTestFilterCatalog e le sue liste
    /// non vengono mai mutati dai chiamanti (li copiano con ToList), quindi l'istanza è condivisa senza clonarla.
    /// </summary>
    public LocalTestFilterCatalog GetFilterCatalog(bool includeDeleted = false)
    {
        using var connection = Open();
        using var snapshot = new LocalIndexSnapshot(connection);
        lock (_filterCatalogCacheLock)
        {
            if (snapshot.Cacheabile && _cachedFilterCatalog is { } c &&
                c.Generazione == Generazione && c.Revisione == snapshot.Revisione && c.IncludeDeleted == includeDeleted)
                return c.Catalog;
        }

        FilterCatalogReadObserver?.Invoke();
        var catalog = ReadFilterCatalogCore(connection, includeDeleted);
        lock (_filterCatalogCacheLock)
        {
            if (snapshot.Cacheabile)
                _cachedFilterCatalog = (Generazione, snapshot.Revisione, includeDeleted, catalog);
        }
        return catalog;
    }

    private static LocalTestFilterCatalog ReadFilterCatalogCore(SqliteConnection connection, bool includeDeleted)
    {
        // A 100k+ Test, un JOIN verso test_summary per il filtro eliminato=0 costringe SQLite a un lookup
        // indicizzato per OGNI riga della tabella figlia, anche quando il cestino e' una piccola minoranza
        // (verificato: ~1s per tabella con 170 eliminati su 104k, contro 19ms per la sola scansione DISTINCT
        // senza join). Si scansiona quindi la tabella figlia senza alcun filtro (rapido) e si esclude un
        // valore solo se e' referenziato ESCLUSIVAMENTE da test nel cestino — verificato con query mirate sul
        // solo, piccolo, insieme dei test eliminati, non sull'intero archivio (feedback utente 2026-09-17).
        var deletedUids = includeDeleted ? Array.Empty<string>() : GetEliminatoUids(connection);
        return new LocalTestFilterCatalog
        {
            Componente = DistinctExcludingDeleted(connection, "test_ecu", ignoreCase: true, deletedUids),
            Veicoli = DistinctExcludingDeleted(connection, "test_vehicle", ignoreCase: false, deletedUids),
            Requisiti = DistinctExcludingDeleted(connection, "test_requirement", ignoreCase: false, deletedUids),
            Funzionalita = DistinctExcludingDeleted(connection, "test_functionality", ignoreCase: false, deletedUids),
            Assegnatari = DistinctSummary(connection, "assegnatario", includeDeleted),
            Creatori = DistinctSummary(connection, "creato_da", includeDeleted)
        };
    }

    private static IReadOnlyList<string> GetEliminatoUids(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT uid FROM test_summary WHERE eliminato = 1;";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private static IReadOnlyList<string> DistinctExcludingDeleted(
        SqliteConnection connection, string table, bool ignoreCase, IReadOnlyList<string> deletedUids)
    {
        var all = RawDistinct(connection, table, ignoreCase);
        if (deletedUids.Count == 0) return all;

        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var candidati = ValuesForTestUids(connection, table, deletedUids, comparer);
        if (candidati.Count == 0) return all;

        // Una query sola su TUTTI i candidati insieme (non una per candidato: con centinaia di test nel
        // cestino il costo fisso per round-trip superava il JOIN originale, verificato). Il filtro
        // dei test eliminati viene materializzato una volta; i valori attivi sono letti in sequenza.
        var attiviTraICandidati = ActiveValuesAmong(connection, table, candidati, ignoreCase, comparer);
        var daEscludere = candidati.Where(v => !attiviTraICandidati.Contains(v)).ToHashSet(comparer);
        return daEscludere.Count == 0 ? all : all.Where(v => !daEscludere.Contains(v)).ToList();
    }

    /// <summary>Una scansione sequenziale evita i lookup casuali per ogni occorrenza dei candidati.
    /// Il sottoinsieme eliminato viene materializzato da SQLite una volta per query.</summary>
    private static HashSet<string> ActiveValuesAmong(
        SqliteConnection connection, string table, HashSet<string> candidati, bool ignoreCase, StringComparer comparer)
    {
        using var command = connection.CreateCommand();
        var collation = ignoreCase ? "NOCASE" : "BINARY";
        command.CommandText = $"""
            SELECT MIN(value) FROM {table} NOT INDEXED
            WHERE test_uid NOT IN (
                SELECT uid FROM test_summary WHERE eliminato = 1 AND uid IS NOT NULL)
            GROUP BY value COLLATE {collation};
            """;
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(comparer);
        while (reader.Read())
        {
            var value = reader.GetString(0);
            if (candidati.Contains(value)) result.Add(value);
        }
        return result;
    }
    private static IReadOnlyList<string> RawDistinct(SqliteConnection connection, string table, bool ignoreCase)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ignoreCase
            ? $"SELECT MIN(value) FROM {table} GROUP BY value COLLATE NOCASE;"
            : $"SELECT DISTINCT value COLLATE BINARY FROM {table};";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            var value = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
        }
        return result.OrderBy(x => x).ToList();
    }

    /// <summary>Valori referenziati dal (piccolo) insieme di test eliminati passato — mai l'intera tabella,
    /// a costo indipendente dal numero totale di Test.</summary>
    private static HashSet<string> ValuesForTestUids(
        SqliteConnection connection, string table, IReadOnlyList<string> testUids, StringComparer comparer)
    {
        var result = new HashSet<string>(comparer);
        const int batchSize = 400;
        for (var offset = 0; offset < testUids.Count; offset += batchSize)
        {
            var batch = testUids.Skip(offset).Take(batchSize).ToList();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT DISTINCT value FROM {table} WHERE test_uid IN ({string.Join(",", batch.Select((_, i) => $"$u{i}"))});";
            for (var i = 0; i < batch.Count; i++) command.Parameters.AddWithValue($"$u{i}", batch[i]);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var value = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
            }
        }
        return result;
    }

    public LocalTestArchivePage Query(LocalTestArchiveQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        return QueryCore(connection, transaction, query);
    }

    /// <summary>Funzioni SQL per la ricerca testuale della Home: stessa semantica di string.Contains e di EcuSelection.Contains
    /// con OrdinalIgnoreCase (anche sui caratteri non ASCII, dove COLLATE NOCASE di SQLite non e' esatto).</summary>
    private static void RegistraFunzioniTesto(SqliteConnection connection)
    {
        connection.CreateFunction<string?, string?, bool>("ci_contains",
            (testo, cerca) => testo is not null && cerca is not null && testo.Contains(cerca, StringComparison.OrdinalIgnoreCase),
            isDeterministic: true);
        connection.CreateFunction<string?, string?, bool>("ci_eq",
            (testo, cerca) => testo is not null && cerca is not null && string.Equals(testo, cerca, StringComparison.OrdinalIgnoreCase),
            isDeterministic: true);
    }

    /// <summary>Pagina della Lista Test letta nella transazione di lettura del chiamante: COUNT e pagina nello stesso snapshot,
    /// senza aprire una transazione annidata quando il chiamante ne ha gia' una (GetHomeAggiornaBatch).</summary>
    private static LocalTestArchivePage QueryCore(SqliteConnection connection, SqliteTransaction transaction, LocalTestArchiveQuery query)
    {
        var pageSize = Math.Clamp(query.PageSize <= 0 ? DefaultPageSize : query.PageSize, 1, MaxPageSize);
        var page = Math.Max(1L, query.Page);

        var where = BuildWhere(connection, query, out var parameters);
        var total = Count(connection, transaction, where, parameters);

        if (total == 0)
            return new LocalTestArchivePage(Array.Empty<TestCase>(), 0, 1, pageSize);

        var maxPage = Math.Max(1L, checked((total + pageSize - 1L) / pageSize));
        page = Math.Min(page, maxPage);
        var offset = checked((page - 1L) * pageSize);
        var items = ReadSlice(connection, transaction, where, parameters, offset, pageSize);
        return new LocalTestArchivePage(items, total, page, pageSize);
    }

    /// <summary>
    /// Slice per offset assoluto, usata dall'Archivio unificato e dai batch amministrativi. A differenza della
    /// paginazione UI non richiede che l'offset cada su un multiplo del page-size; il buffer resta comunque
    /// bounded a MaxPageSize e totale/offset sono a 64 bit.
    /// </summary>
    public LocalTestArchiveSlice QuerySlice(LocalTestArchiveQuery query, long offset, int limit)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > MaxPageSize) throw new ArgumentOutOfRangeException(nameof(limit));

        using var connection = Open();
        var where = BuildWhere(connection, query, out var parameters);
        using var transaction = connection.BeginTransaction(deferred: true);
        var total = Count(connection, transaction, where, parameters);
        if (offset >= total) return new LocalTestArchiveSlice(Array.Empty<TestCase>(), total);
        return new LocalTestArchiveSlice(ReadSlice(connection, transaction, where, parameters, offset, limit), total);
    }

    /// <summary>Materializza il risultato completo di una vista analitica (Audit e Report). Le pagine sono keyset
    /// (vedi LeggiPaginaKeyset) e non OFFSET: stesso ordine e stesso contenuto, ma il costo non cresce piu' con il
    /// quadrato del numero di Test. Il limite e' opzionale: senza <paramref name="maximumRows"/> il risultato e' sempre
    /// completo, perche' un tetto implicito faceva ripiegare Audit su GetAll() e scriveva un crash log a ogni refresh
    /// oltre soglia. Con un limite esplicito la lettura fallisce chiusa, prima di materializzare alcuna riga.</summary>
    public List<TestCase> QueryAll(LocalTestArchiveQuery query, long? maximumRows = null) =>
        QueryAllCore(query, maximumRows, MaterializzaRiga);

    /// <summary>
    /// Come <see cref="QueryAll"/> per Audit e Report: stessi Test, stesso ordine keyset e stesso snapshot, ma ogni JSON viene
    /// letto con la proiezione <see cref="TestCaseProiezioneAnalisi"/> (solo i campi usati dalle matrici e dall'anteprima).
    /// Il risultato non va usato per export, editor o salvataggi: quei percorsi restano sul Test completo.
    /// </summary>
    public List<TestCase> QueryAllAnalisi(LocalTestArchiveQuery query) =>
        QueryAllCore(query, null, MaterializzaRigaAnalisi);

    private List<TestCase> QueryAllCore(
        LocalTestArchiveQuery query,
        long? maximumRows,
        Func<SqliteDataReader, RigaJsonBuffer, TestCase?> materializza)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var connection = Open();
        var where = BuildWhere(connection, query, out var parameters);
        // COUNT e tutte le pagine devono appartenere allo stesso snapshot. Senza una transazione un writer
        // concorrente puo' inserire/rimuovere righe fra due slice, producendo duplicati, omissioni o un numero
        // di elementi diverso dal totale verificato. Deferred mantiene il reader non bloccante in WAL mode.
        using var transaction = connection.BeginTransaction(deferred: true);
        var total = Count(connection, transaction, where, parameters);
        if (maximumRows is { } limite && total > limite)
            throw new LocalTestArchiveResultLimitExceededException(total, limite);

        var results = new List<TestCase>((int)Math.Min(total, int.MaxValue));
        if (total == 0) return results;

        using var buffer = new RigaJsonBuffer();
        CursoreArchivio? dopo = null;
        long letteFinora = 0;
        while (true)
        {
            var (righeLette, ultimo) = LeggiPaginaKeyset(connection, transaction, where, parameters, dopo, MaxPageSize, buffer, results, materializza);
            QueryAllPageReadObserver?.Invoke(letteFinora);
            letteFinora += righeLette;
            // LIMIT conta le righe prodotte dal JOIN (dopo i filtri): una pagina piu' corta di MaxPageSize e' l'ultima.
            if (righeLette < MaxPageSize) break;
            dopo = ultimo;
        }
        return results;
    }

    /// <summary>Hook di sincronizzazione interno usato dai test per interporre deterministicamente un writer.</summary>
    internal Action<long>? QueryAllPageReadObserver { get; set; }

    private static long Count(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string where,
        IReadOnlyDictionary<string, object?> parameters)
    {
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = $"SELECT COUNT(*) FROM test_summary s {where};";
        AddParameters(count, parameters);
        return Convert.ToInt64(count.ExecuteScalar());
    }

    private static IReadOnlyList<TestCase> ReadSlice(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string where,
        IReadOnlyDictionary<string, object?> parameters,
        long offset,
        int limit)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Hash memorizzato e marcatore di algoritmo letti nella stessa istruzione della pagina: stesso snapshot.
        command.CommandText = $"""
            SELECT CAST(c.json AS BLOB), c.content_hash,
                   (SELECT value FROM meta WHERE key = 'content_hash_algo') AS marcatore_algoritmo
            FROM test_summary s
            JOIN test_case_cache c ON c.path = s.path
            {where}
            ORDER BY s.ultima_modifica_ticks DESC, s.id COLLATE NOCASE, s.uid
            LIMIT $limit OFFSET $offset;
            """;
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var result = new List<TestCase>(limit);
        using var buffer = new RigaJsonBuffer();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var tc = MaterializzaRiga(reader, buffer);
            if (tc != null) result.Add(tc);
        }
        return result;
    }

    /// <summary>
    /// Materializzazione unica per la lettura a offset e a keyset: contenuto e stamp sono gli stessi. Il JSON (colonna 0,
    /// BLOB con i byte UTF-8 del testo) va nel buffer riusato e si deserializza da span: prima ogni riga creava una
    /// stringa UTF-16 del payload e poi la transcodificava per il parser, cioe' due copie temporanee per Test.
    /// </summary>
    private static TestCase? MaterializzaRiga(SqliteDataReader reader, RigaJsonBuffer buffer)
    {
        var tc = JsonSerializer.Deserialize<TestCase>(buffer.Leggi(reader, 0), JsonIo.Options);
        if (tc == null) return null;
        tc.NormalizzaVeicoli();
        // Lo stamp si riusa solo se la riga e' stata prodotta dall'algoritmo attuale (guardrail unico in
        // TestCaseFingerprint.StampDaIndice); altrimenti si ricalcola come prima.
        TestCaseFingerprint.StampDaIndice(tc, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        return tc;
    }

    /// <summary>
    /// Lettura per Audit e Report (QueryAllAnalisi): lo stesso JSON viene deserializzato solo nei campi proiettati
    /// (<see cref="TestCaseProiezioneAnalisi"/>). Lo stamp di fingerprint NON viene applicato: il Test e' parziale e non
    /// deve far credere di avere il contenuto che l'indice ha calcolato per il file intero.
    /// </summary>
    private static TestCase? MaterializzaRigaAnalisi(SqliteDataReader reader, RigaJsonBuffer buffer)
    {
        var proiezione = JsonSerializer.Deserialize<TestCaseProiezioneAnalisi>(buffer.Leggi(reader, 0), JsonIo.Options);
        if (proiezione == null) return null;
        var tc = proiezione.ToTestCase();
        tc.NormalizzaVeicoli();
        return tc;
    }

    /// <summary>Ultima riga di una pagina keyset: chiave dell'ordine di QueryAll (UltimaModifica DESC, Id NOCASE, Uid).</summary>
    private readonly record struct CursoreArchivio(long UltimaModificaTicks, string Id, string Uid);

    /// <summary>Buffer UTF-8 riusato da una lettura di QueryAll o di una pagina: il JSON di ogni riga viene copiato in
    /// un array noleggiato dal pool, senza una stringa per riga. Va usato da un solo thread, per la durata della lettura.</summary>
    private sealed class RigaJsonBuffer : IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);

        public ReadOnlySpan<byte> Leggi(SqliteDataReader reader, int ordinale)
        {
            var lunghezza = checked((int)reader.GetBytes(ordinale, 0, null, 0, 0));
            if (lunghezza > _buffer.Length)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = ArrayPool<byte>.Shared.Rent(lunghezza);
            }
            var letti = checked((int)reader.GetBytes(ordinale, 0, _buffer, 0, lunghezza));
            return _buffer.AsSpan(0, letti);
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Una pagina di QueryAll a keyset: le sole righe DOPO il cursore, nello stesso ordine di ReadSlice e senza OFFSET.
    /// Uid e' la PRIMARY KEY di test_summary (esadecimale, valorizzata sempre), quindi la chiave e' un ordine totale.
    /// Il confronto "ticks &lt;= cursore" e' ridondante per la logica, ma permette a SQLite di usare l'indice come range.
    /// Prima ogni pagina rileggeva e scartava le righe precedenti (OFFSET), con costo superlineare nel numero di Test.
    /// Le righe materializzate vanno direttamente in <paramref name="destinazione"/>. Restituisce le righe lette dal SQL
    /// (il LIMIT le conta anche se una riga non produce un Test) e l'ultimo cursore.
    /// </summary>
    private static (int RigheLette, CursoreArchivio? Ultimo) LeggiPaginaKeyset(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string where,
        IReadOnlyDictionary<string, object?> parameters,
        CursoreArchivio? dopo,
        int limit,
        RigaJsonBuffer buffer,
        List<TestCase> destinazione,
        Func<SqliteDataReader, RigaJsonBuffer, TestCase?> materializza)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT CAST(c.json AS BLOB), c.content_hash,
                   (SELECT value FROM meta WHERE key = 'content_hash_algo') AS marcatore_algoritmo,
                   s.ultima_modifica_ticks, s.id, s.uid
            FROM test_summary s
            JOIN test_case_cache c ON c.path = s.path
            {(dopo == null ? where : AggiungiSeekKeyset(where))}
            ORDER BY s.ultima_modifica_ticks DESC, s.id COLLATE NOCASE, s.uid
            LIMIT $limit;
            """;
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("$limit", limit);
        if (dopo is { } cursore)
        {
            command.Parameters.AddWithValue("$cursoreTicks", cursore.UltimaModificaTicks);
            command.Parameters.AddWithValue("$cursoreId", cursore.Id);
            command.Parameters.AddWithValue("$cursoreUid", cursore.Uid);
        }

        CursoreArchivio? ultimo = null;
        var letteFinora = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            letteFinora++;
            ultimo = new CursoreArchivio(reader.GetInt64(3), reader.GetString(4), reader.GetString(5));
            var tc = materializza(reader, buffer);
            if (tc != null) destinazione.Add(tc);
        }
        return (letteFinora, ultimo);
    }

    /// <summary>Clausola "dopo il cursore" nell'ordine ultima modifica DESC, id NOCASE ASC, uid ASC (espansa a mano:
    /// SQLite non confronta tuple con direzioni miste).</summary>
    private static string AggiungiSeekKeyset(string where)
    {
        const string seek = """
            (s.ultima_modifica_ticks <= $cursoreTicks
             AND (s.ultima_modifica_ticks < $cursoreTicks
                  OR (s.ultima_modifica_ticks = $cursoreTicks
                      AND (s.id COLLATE NOCASE > $cursoreId COLLATE NOCASE
                           OR (s.id COLLATE NOCASE = $cursoreId COLLATE NOCASE AND s.uid > $cursoreUid)))))
            """;
        return where.Length == 0 ? "WHERE " + seek : where + " AND " + seek;
    }

    /// <summary>
    /// Verifica bounded se un Test e' ancora referenziato da un altro Test. Usa il read-model locale e JSON1,
    /// evitando GetAll() anche con cataloghi molto grandi. Il caller deve aver gia' riallineato l'indice alla
    /// fonte autorevole; in caso di errore deve fallire chiuso o usare il fallback autorevole esistente.
    /// </summary>
    public bool IsReferencedByAnotherTest(string testId)
    {
        if (string.IsNullOrWhiteSpace(testId)) return false;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM test_case_cache c
            JOIN test_summary s ON s.path = c.path
            WHERE s.id <> $id
              AND (
                    COALESCE(json_extract(c.json, '$.TestCollegatoRichiestoId'), '') = $id
                    OR EXISTS (
                        SELECT 1
                        FROM json_each(c.json, '$.TestCorrelati') j
                        WHERE CAST(j.value AS TEXT) = $id
                    )
                  )
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", testId.Trim());
        return command.ExecuteScalar() != null;
    }

    /// <summary>Decisione utente 6.4.0 ("Test importati: solo stato finale"): i Test con
    /// OrigineTest.ImportStatoUdt sono fotografie di uno stato noto, non esecuzioni fatte in TestLoom — esclusi
    /// da tutti i conteggi qui sotto (origine_test = 0, cioè TestLoom). La Home mostra questi Test comunque, solo
    /// non li somma nei propri KPI operativi.</summary>
    public LocalHomeArchiveSummary GetHomeSummary(string? activeOperator, bool includeDeleted = false)
    {
        using var connection = Open();
        return GetHomeSummaryCore(connection, activeOperator, includeDeleted);
    }

    private static LocalHomeArchiveSummary GetHomeSummaryCore(SqliteConnection connection, string? activeOperator, bool includeDeleted = false)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                COUNT(*),
                COALESCE(SUM(CASE WHEN stato_lavoro = $daRitestare THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN assegnatario = $operatore AND stato_lavoro IN ($pianificato,$inCorso,$daRivedere,$inAttesa,$daRitestare) THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN current_outcome IS NOT NULL AND executed_revision_count > 0 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN current_outcome = $conforme AND executed_revision_count > 0 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(s.senza_ecu), 0),
                COALESCE(SUM(CASE WHEN current_outcome = $conforme THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN current_outcome = $nonConforme THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN current_outcome = $deviazioni THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN current_outcome = $esitoDaRivedere THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN current_outcome IN ($nonApplicabile, $nonEseguitoNa) THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN current_outcome IS NULL OR current_outcome = $daEseguire THEN 1 ELSE 0 END), 0)
            FROM test_summary s
            WHERE origine_test = 0 {(includeDeleted ? "" : "AND eliminato = 0")};
            """;
        command.Parameters.AddWithValue("$operatore", activeOperator ?? "");
        command.Parameters.AddWithValue("$pianificato", (int)StatoLavoroTest.Pianificato);
        command.Parameters.AddWithValue("$inCorso", (int)StatoLavoroTest.InCorso);
        command.Parameters.AddWithValue("$daRivedere", (int)StatoLavoroTest.DaRivedere);
        command.Parameters.AddWithValue("$inAttesa", (int)StatoLavoroTest.InAttesa);
        command.Parameters.AddWithValue("$daRitestare", (int)StatoLavoroTest.DaRitestare);
        command.Parameters.AddWithValue("$conforme", (int)Esito.Conforme);
        command.Parameters.AddWithValue("$nonConforme", (int)Esito.NonConforme);
        command.Parameters.AddWithValue("$deviazioni", (int)Esito.ConformeConDeviazioni);
        command.Parameters.AddWithValue("$nonApplicabile", (int)Esito.NonApplicabile);
        command.Parameters.AddWithValue("$nonEseguitoNa", (int)Esito.NonEseguitoNA);
        command.Parameters.AddWithValue("$daEseguire", (int)Esito.DaEseguire);
        command.Parameters.AddWithValue("$esitoDaRivedere", (int)Esito.DaRivedere);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new LocalHomeArchiveSummary();
        return new LocalHomeArchiveSummary
        {
            TotalTests = reader.GetInt64(0),
            ToRetest = reader.GetInt64(1),
            MyActive = reader.GetInt64(2),
            Tested = reader.GetInt64(3),
            Conforming = reader.GetInt64(4),
            // Piano 6.4.0 punto 5(3): conteggio dei Test senza componente associata, usato dalla Home per
            // un'etichetta esplicita "Test senza componente (da correggere)" invece di lasciare che l'assenza di componente
            // produca solo una riga/colonna vuota e incomprensibile altrove (es. matrice Report). Dalla colonna
            // senza_ecu di test_summary (schema v14, stessa regola di test_ecu) invece di una sottoquery correlata
            // per Test, che a 100k Test era la parte dominante del costo del sommario.
            WithoutEcu = reader.GetInt64(5),
            TestStatusDistribution = new StatiMatrice(
                checked((int)reader.GetInt64(6)), checked((int)reader.GetInt64(7)), checked((int)reader.GetInt64(8)),
                checked((int)reader.GetInt64(11)), checked((int)reader.GetInt64(10)), checked((int)reader.GetInt64(9)))
        };
    }

    /// <summary>Legge un solo candidato sorgente per tripla funzionalita×componente×veicolo: la riduzione viene fatta
    /// nel read model SQLite senza materializzare lo storico Test. I valori manuali e il catalogo restano
    /// overlay autorevoli in memoria e vengono applicati dal reducer della matrice.</summary>
    public IReadOnlyList<LocalHomeFunctionalitySource> GetHomeFunctionalitySources()
    {
        using var connection = Open();
        using var snapshot = new LocalIndexSnapshot(connection);
        lock (_homeFunctionalityCacheLock)
        {
            if (snapshot.Cacheabile && _cachedHomeFunctionalitySources is { } cached &&
                cached.Generazione == Generazione && cached.Revisione == snapshot.Revisione)
                return cached.Sources;
        }
        HomeFunctionalityReadObserver?.Invoke();
        using var command = connection.CreateCommand();
        // v16: le fonti sono mantenute dallo store nella stessa transazione delle proiezioni dei Test (vedi LocalTestIndexStore,
        // home_source_winner): la lettura e' una scansione di una riga per partizione, senza ricalcolo sull'archivio.
        command.CommandText = """
            SELECT funzionalita, componente, veicolo, outcome, data_ticks, id, revision_index, attempt_index
            FROM home_source_winner
            ORDER BY funzionalita COLLATE NOCASE, componente COLLATE NOCASE, veicolo COLLATE NOCASE;
            """;
        var risultato = new List<LocalHomeFunctionalitySource>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            risultato.Add(new LocalHomeFunctionalitySource(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : (Esito)reader.GetInt32(3),
                reader.GetInt64(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7)));
        }
        IReadOnlyList<LocalHomeFunctionalitySource> sources = Array.AsReadOnly(risultato.ToArray());
        lock (_homeFunctionalityCacheLock)
            if (snapshot.Cacheabile)
                _cachedHomeFunctionalitySources = (Generazione, snapshot.Revisione, sources);
        return sources;
    }

    public LocalDefaultDashboardSnapshot GetDefaultDashboardSnapshot()
    {
        using var connection = Open();
        var snapshot = new LocalDefaultDashboardSnapshot();

        using (var vehicles = connection.CreateCommand())
        {
            vehicles.CommandText = """
                SELECT v.value, COUNT(*)
                FROM test_vehicle v
                JOIN test_summary s ON s.uid = v.test_uid
                WHERE s.eliminato = 0
                GROUP BY v.value COLLATE NOCASE
                ORDER BY COUNT(*) DESC, v.value COLLATE NOCASE;
                """;
            using var reader = vehicles.ExecuteReader();
            while (reader.Read()) snapshot.VehicleCounts[reader.GetString(0)] = reader.GetInt64(1);
        }

        using (var outcomes = connection.CreateCommand())
        {
            outcomes.CommandText = """
                SELECT COALESCE(current_outcome, $notExecuted), COUNT(*)
                FROM test_summary
                WHERE eliminato = 0
                GROUP BY COALESCE(current_outcome, $notExecuted);
                """;
            outcomes.Parameters.AddWithValue("$notExecuted", (int)Esito.NonEseguitoNA);
            using var reader = outcomes.ExecuteReader();
            while (reader.Read()) snapshot.OutcomeCounts[(Esito)reader.GetInt32(0)] = reader.GetInt64(1);
        }

        using (var operators = connection.CreateCommand())
        {
            operators.CommandText = """
                SELECT r.operatore, SUM(r.unit_count)
                FROM test_revision r
                JOIN test_summary s ON s.uid = r.test_uid
                WHERE s.eliminato = 0 AND TRIM(r.operatore) <> '' AND r.raw_attempt_count > 0 AND r.origine <> 1
                GROUP BY r.operatore COLLATE NOCASE
                ORDER BY SUM(r.unit_count) DESC, r.operatore COLLATE NOCASE;
                """;
            using var reader = operators.ExecuteReader();
            while (reader.Read()) snapshot.ExecutionCountsByOperator[reader.GetString(0)] = reader.GetInt64(1);
        }

        using (var kpi = connection.CreateCommand())
        {
            kpi.CommandText = """
                SELECT
                    COALESCE(AVG(CASE WHEN first_conforme_revision IS NOT NULL THEN first_conforme_revision END), 0),
                    COALESCE(SUM(CASE WHEN executed_revision_count > 0 AND first_conforme_revision IS NOT NULL THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN executed_revision_count > 0 AND first_conforme_revision IS NULL THEN 1 ELSE 0 END), 0)
                FROM test_summary
                WHERE eliminato = 0;
                """;
            using var reader = kpi.ExecuteReader();
            if (reader.Read())
            {
                snapshot.IterationKpi = new KpiIterazioniConforme
                {
                    MediaIterazioni = reader.GetDouble(0),
                    CasiRaggiuntiConforme = reader.GetInt64(1),
                    CasiMaiRaggiuntiConforme = reader.GetInt64(2)
                };
            }
        }

        return snapshot;
    }

    public CoverageBucket GetRequirementCoverage(
        IReadOnlyList<Requisito> requisiti,
        bool soloBanco = false,
        bool soloVeicolo = false)
    {
        ArgumentNullException.ThrowIfNull(requisiti);
        using var connection = Open();
        using var snapshot = new LocalIndexSnapshot(connection);
        return GetRequirementCoverageCore(connection, snapshot, requisiti, soloBanco, soloVeicolo);
    }

    private readonly object _coverageCacheLock = new();
    private (long Generazione, long Revisione, int RequisitiHash, bool SoloBanco, bool SoloVeicolo, CoverageBucket Bucket)? _cachedCoverage;

    private static CoverageBucket CloneCoverageBucket(CoverageBucket b) => new CoverageBucket
    {
        Conformi = b.Conformi,
        ConformiConDeviazioni = b.ConformiConDeviazioni,
        NonConformi = b.NonConformi,
        NonTestati = b.NonTestati
    };

    /// <summary>
    /// Addendum prestazioni 2026-09-26 (Home a 100k, rivisita): a differenza di LocalHomeDashboardQueryService,
    /// questo percorso (usato da GetHomeAggiornaBatch dentro HomeViewModel.Aggiorna) non aveva ALCUNA cache —
    /// la query di copertura (ranking per requisito più, se serve, il ranking Extra per requisito×veicolo) veniva
    /// ripetuta a ogni rivisita anche a dati identici (~390ms su 100k Test). L'istanza di questo servizio
    /// sopravvive fra le Aggiorna() successive fino a un cambio di Generazione (vedi HomeViewModel.Scalable.
    /// GetScalableArchiveQuery), quindi la cache per (Generazione, revisione dell'indice, requisiti, soloBanco,
    /// soloVeicolo) è invalidata esattamente quando lo è già GetSnapshot/GetRequirementCoverage della dashboard:
    /// stessa politica, nessuna dipendenza da data/dimensione file.
    /// </summary>
    private CoverageBucket GetRequirementCoverageCore(
        SqliteConnection connection,
        LocalIndexSnapshot snapshot,
        IReadOnlyList<Requisito> requisiti,
        bool soloBanco,
        bool soloVeicolo)
    {
        var requisitiHash = RequisitiCoverageCacheKey.ComputeHash(requisiti);
        lock (_coverageCacheLock)
        {
            if (snapshot.Cacheabile && _cachedCoverage.HasValue &&
                _cachedCoverage.Value.Generazione == Generazione &&
                _cachedCoverage.Value.Revisione == snapshot.Revisione &&
                _cachedCoverage.Value.RequisitiHash == requisitiHash &&
                _cachedCoverage.Value.SoloBanco == soloBanco &&
                _cachedCoverage.Value.SoloVeicolo == soloVeicolo)
            {
                return CloneCoverageBucket(_cachedCoverage.Value.Bucket);
            }
        }

        var bucket = ComputeRequirementCoverageCore(connection, snapshot.Transazione, requisiti, soloBanco, soloVeicolo);

        lock (_coverageCacheLock)
        {
            if (snapshot.Cacheabile)
                _cachedCoverage = (Generazione, snapshot.Revisione, requisitiHash, soloBanco, soloVeicolo, CloneCoverageBucket(bucket));
        }
        return bucket;
    }

    private CoverageBucket ComputeRequirementCoverageCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Requisito> requisiti,
        bool soloBanco,
        bool soloVeicolo)
    {
        // Le due mappe sono una sola fotografia logica (transazione dello snapshot del chiamante): un refresh fra la query
        // standard e quella Extra non deve combinare risultati appartenenti a commit diversi.
        var standard = new Dictionary<string, Esito>(StringComparer.OrdinalIgnoreCase);
        var extra = new Dictionary<(string Requisito, string Veicolo), Esito>(RequirementVehicleComparer.Instance);
        // Su un archivio appena importato/pianificato possono esserci molti Test collegati ai requisiti ma
        // nessuna REV ancora eseguita. In quel caso entrambi i ranking restituirebbero mappe vuote dopo aver
        // attraversato e ordinato tutte le associazioni Test×Requisito (100k righe nel benchmark di scala).
        // Il controllo resta nello stesso snapshot e permette di produrre direttamente il bucket NonTestati.
        using var esisteRevisione = connection.CreateCommand();
        esisteRevisione.Transaction = transaction;
        esisteRevisione.CommandText = "SELECT EXISTS(SELECT 1 FROM test_revision LIMIT 1);";
        var haRevisioni = Convert.ToInt64(esisteRevisione.ExecuteScalar()) != 0;

        if (haRevisioni) using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                WITH ranked AS (
                    SELECT rq.value requisito, r.outcome,
                           ROW_NUMBER() OVER (
                               PARTITION BY rq.value COLLATE NOCASE
                               ORDER BY r.data_ticks DESC, r.revision_index DESC, r.test_uid
                           ) rn
                    FROM test_requirement rq
                    CROSS JOIN test_summary s ON s.uid = rq.test_uid AND s.eliminato = 0
                    CROSS JOIN test_revision r ON r.test_uid = s.uid
                )
                SELECT requisito, outcome FROM ranked WHERE rn = 1;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read()) standard[reader.GetString(0)] = (Esito)reader.GetInt32(1);
        }

        if (haRevisioni) RequirementCoverageStandardReadObserver?.Invoke();

        // Il secondo ranking attraversa requirement x vehicle x revision ed e' il tratto piu' costoso
        // della prima apertura Home a 100k. I requisiti legacy/standard non leggono mai la mappa Extra:
        // evitarlo in quel caso non cambia il risultato e non indebolisce lo snapshot della query standard.
        // Se esiste almeno un Extra applicabile, il command resta nella stessa read transaction.
        var hasApplicableD4D = requisiti.Any(r => !r.Archiviata &&
            (!soloBanco || r.ApplicabileBanco) &&
            (!soloVeicolo || r.ApplicabileVeicolo) &&
            RequisitoApplicabilita.ED4D(r));
        if (haRevisioni && hasApplicableD4D)
        {
            RequirementCoverageD4DReadObserver?.Invoke();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                    WITH ranked AS (
                        SELECT rq.value requisito, v.value veicolo, r.outcome,
                               ROW_NUMBER() OVER (
                                    PARTITION BY rq.value COLLATE NOCASE, v.value COLLATE NOCASE
                                    ORDER BY r.data_ticks DESC, r.revision_index DESC, r.test_uid
                               ) rn
                        FROM test_requirement rq
                        CROSS JOIN test_summary s ON s.uid = rq.test_uid AND s.eliminato = 0
                        CROSS JOIN test_vehicle v ON v.test_uid = s.uid
                        CROSS JOIN test_revision r ON r.test_uid = s.uid
                    )
                    SELECT requisito, veicolo, outcome FROM ranked WHERE rn = 1;
                    """;
            using var reader = command.ExecuteReader();
            while (reader.Read()) extra[(reader.GetString(0), reader.GetString(1))] = (Esito)reader.GetInt32(2);
        }

        var bucket = new CoverageBucket();
        foreach (var requisito in requisiti.Where(r => !r.Archiviata))
        {
            if (soloBanco && !requisito.ApplicabileBanco) continue;
            if (soloVeicolo && !requisito.ApplicabileVeicolo) continue;

            if (!RequisitoApplicabilita.ED4D(requisito))
            {
                AccumulateCoverage(bucket,
                    standard.TryGetValue(requisito.Id, out var esitoStandard) ? esitoStandard : null);
                continue;
            }

            var targets = RequisitoApplicabilita.SottoveicoliDi(requisito);
            if (targets.Count == 0)
            {
                // 6.5.0: sottoveicoli facoltativi -> Extra applicabile a tutti, unita cross come uno Standard.
                AccumulateCoverage(bucket, standard.TryGetValue(requisito.Id, out var esitoCross) ? esitoCross : null);
                continue;
            }

            foreach (var target in targets)
                AccumulateCoverage(bucket, extra.TryGetValue((requisito.Id, target), out var esito) ? esito : null);
        }
        return bucket;
    }

    /// <summary>Hook interno per verificare che le due query coverage condividano lo stesso snapshot.</summary>
    internal Action? RequirementCoverageStandardReadObserver { get; set; }

    /// <summary>Hook interno per verificare che il ranking Extra sia eseguito soltanto quando necessario.</summary>
    internal Action? RequirementCoverageD4DReadObserver { get; set; }

    private readonly object _customFieldValuesCacheLock = new();
    private readonly Dictionary<string, (long Revisione, IReadOnlyList<string> Valori)> _cachedCustomFieldValues = new(StringComparer.Ordinal);

    /// <summary>
    /// Valori distinti di un campo custom (senza Test eliminati), per le voci dei filtri dei campi di sistema della Lista Test.
    /// Stessa politica di cache di <see cref="GetFilterCatalog"/>: valida per revisione dell'indice, quindi riletta solo dopo una
    /// scrittura sull'indice; a indice invariato una visita della pagina non ripete la scansione della tabella figlia.
    /// </summary>
    public IReadOnlyList<string> GetCustomFieldValues(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return Array.Empty<string>();
        using var connection = Open();
        using var snapshot = new LocalIndexSnapshot(connection);
        lock (_customFieldValuesCacheLock)
        {
            if (snapshot.Cacheabile && _cachedCustomFieldValues.TryGetValue(key, out var cached) && cached.Revisione == snapshot.Revisione)
                return cached.Valori;
        }
        var valori = GetCustomFieldValuesCore(connection, key);
        lock (_customFieldValuesCacheLock)
        {
            if (snapshot.Cacheabile)
                _cachedCustomFieldValues[key] = (snapshot.Revisione, valori);
        }
        return valori;
    }

    private static IReadOnlyList<string> GetCustomFieldValuesCore(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT value
            FROM test_custom_field
            WHERE field_key = $key AND TRIM(value) <> ''
              AND test_uid NOT IN (SELECT uid FROM test_summary WHERE eliminato = 1 AND uid IS NOT NULL)
            ORDER BY value;
            """;
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    /// <summary>
    /// Lettura minima dell'indice per la Home quando nessuna sezione visibile ne richiede altre: revisione dell'indice su
    /// una connessione reale. Serve a far scattare, a ogni refresh, il recupero/banner dell'indice anche con tutte le
    /// sezioni nascoste (una corruzione si rileva solo leggendo davvero il file).
    /// </summary>
    public long LeggiRevisione()
    {
        using var connection = Open();
        using var snapshot = new LocalIndexSnapshot(connection);
        return snapshot.Revisione;
    }

    /// <summary>
    /// Unica lettura indicizzata per l'intero refresh della Home (Addendum prestazioni 2026-09-25): prima ogni
    /// KPI/filtro apriva la propria connessione SQLite (ognuna con le proprie PRAGMA di apertura), moltiplicando
    /// per quattro-cinque il costo fisso di apertura per ogni rivisita a 100k Test, ognuno racchiuso nel proprio
    /// TryRead del coordinatore. Qui si apre UNA sola connessione e si eseguono in sequenza tutte le query che la
    /// Home richiede in un ciclo di Aggiorna(). Il chiamante avvolge l'intera chiamata in UN SOLO TryRead: se una
    /// qualunque sotto-query fallisce, l'intero batch fallisce e la Home cade sul percorso non indicizzato per
    /// TUTTI i KPI di questo giro (non solo per quello guasto) — stessa politica di recupero del coordinatore
    /// (nessun retry ricorsivo qui), a risultati identici al vecchio percorso a connessioni separate.
    /// </summary>
    public LocalHomeAggiornaBatchResult GetHomeAggiornaBatch(LocalHomeAggiornaBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var connection = Open();
        // Un solo snapshot per l'intero batch: revisione e dati (Componente, Summary, coverage, filtri) vengono dalla stessa
        // versione dell'indice. Serve anche alla cache di Componente/Summary sotto, stessa politica "nessuna dipendenza da
        // data/dimensione file" già usata da GetRequirementCoverageCore/LocalHomeDashboardQueryService.GetSnapshot.
        using var snapshot = new LocalIndexSnapshot(connection);
        using var revisionPresenceCommand = connection.CreateCommand();
        revisionPresenceCommand.CommandText = "SELECT EXISTS(SELECT 1 FROM test_revision LIMIT 1);";
        var hasAnyRevision = Convert.ToInt64(revisionPresenceCommand.ExecuteScalar()) != 0;

        // Addendum prestazioni 2026-09-26 (Home a 100k, rivisita — vedi anche GetRequirementCoverageCore sopra):
        // Componente e Summary sono le uniche due sotto-query di questo batch che restavano SENZA cache — a dati e
        // filtri invariati (stesso Generazione/Revisione/ActiveOperator) venivano rilette da zero a OGNI
        // rivisita della Home, anche quando la coverage sotto era già servita dalla cache. CustomFieldValues e
        // Todo/Done restano intenzionalmente non cache-ate qui: il primo dipende da chiavi arbitrarie decise a
        // runtime (poco valore a cache-are singole chiavi, il costo è già per-query non per-batch), i secondi
        // sono paginati e la pagina richiesta può cambiare fra due Aggiorna() consecutive senza che la revisione
        // cambi (SommaConteggi/paginazione andrebbe comunque riverificata) — cache-arli richiederebbe includere
        // Page/PageSize/AllowedVehicles/Assignee nella chiave, un rischio di staleness non giustificato dal
        // guadagno per un batch che a 100k Test resta comunque bounded (LIMIT/OFFSET su indice).
        var (componente, summary) = GetEcuESommarioConCache(connection, snapshot, request.ActiveOperator, request.IncludeEcu, request.IncludeSummary);
        var coverage = request.Requisiti != null
            ? GetRequirementCoverageCore(connection, snapshot, request.Requisiti, request.SoloBanco, request.SoloVeicolo)
            : null;

        var customFieldValues = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var key in request.CustomFieldKeys)
            customFieldValues[key] = GetCustomFieldValuesCore(connection, key);

        var todo = request.TodoQuery != null ? QueryCore(connection, snapshot.Transazione, request.TodoQuery) : null;
        var done = request.DoneQuery != null ? QueryCore(connection, snapshot.Transazione, request.DoneQuery) : null;

        return new LocalHomeAggiornaBatchResult
        {
            Revisione = snapshot.Revisione,
            Componente = componente,
            Summary = summary,
            HasAnyRevision = hasAnyRevision,
            Coverage = coverage,
            CustomFieldValues = customFieldValues,
            Todo = todo,
            Done = done
        };
    }

    private readonly object _ecuSummaryCacheLock = new();
    private (long Generazione, long Revisione, string? ActiveOperator, bool IncludeEcu, IReadOnlyList<string> Componente, LocalHomeArchiveSummary Summary)? _cachedEcuSummary;

    /// <summary>Hook interno per verificare nei test quante volte la lettura Componente/Summary viene davvero eseguita
    /// (invocato solo su cache miss) — stesso schema di RequirementCoverageStandardReadObserver/Extra sopra.</summary>
    internal Action? EcuSummaryReadObserver { get; set; }

    /// <summary>Componente e Summary condividono la stessa chiave di validità (Generazione/Revisione/ActiveOperator/
    /// IncludeEcu) e vengono quindi cache-ati insieme: LocalHomeArchiveSummary è immutabile (proprietà init-only,
    /// solo numeri) ed Componente non viene mai mutato dai chiamanti (solo enumerato), quindi condividere la stessa
    /// istanza fra rivisite successive è sicuro senza bisogno di alcuna clonazione defensiva (a differenza di
    /// CoverageBucket sopra, mutabile).</summary>
    private (IReadOnlyList<string> Componente, LocalHomeArchiveSummary Summary) GetEcuESommarioConCache(
        SqliteConnection connection, LocalIndexSnapshot snapshot, string? activeOperator, bool includeEcu, bool includeSummary)
    {
        if (!includeSummary)
        {
            // Sommario non richiesto: non lo si legge e la sua cache non viene toccata. Resta solo l'elenco componente, se
            // richiesto, letto per questo refresh (il chiamante non usa il Summary vuoto restituito qui).
            EcuSummaryReadObserver?.Invoke();
            var soloEcu = includeEcu ? GetEcuFilterValuesCore(connection, includeDeleted: false) : Array.Empty<string>();
            return (soloEcu, new LocalHomeArchiveSummary());
        }

        lock (_ecuSummaryCacheLock)
        {
            if (snapshot.Cacheabile && _cachedEcuSummary is { } c &&
                c.Generazione == Generazione && c.Revisione == snapshot.Revisione &&
                c.ActiveOperator == activeOperator && c.IncludeEcu == includeEcu)
                return (c.Componente, c.Summary);
        }

        EcuSummaryReadObserver?.Invoke();
        var componente = includeEcu ? GetEcuFilterValuesCore(connection, includeDeleted: false) : Array.Empty<string>();
        var summary = GetHomeSummaryCore(connection, activeOperator);

        lock (_ecuSummaryCacheLock)
        {
            if (snapshot.Cacheabile)
                _cachedEcuSummary = (Generazione, snapshot.Revisione, activeOperator, includeEcu, componente, summary);
        }
        return (componente, summary);
    }

    private static string BuildWhere(
        SqliteConnection connection,
        LocalTestArchiveQuery q,
        out Dictionary<string, object?> parameters)
    {
        parameters = new Dictionary<string, object?>();
        var paramMap = parameters;
        var clauses = new List<string>();
        switch (q.DeletionScope)
        {
            case LocalTestDeletionScope.ActiveOnly:
                clauses.Add("s.eliminato = 0");
                break;
            case LocalTestDeletionScope.DeletedOnly:
                clauses.Add("s.eliminato = 1");
                break;
            case LocalTestDeletionScope.All:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(q.DeletionScope));
        }

        void AddEq(string expression, string name, object? value)
        {
            if (value == null) return;
            clauses.Add($"{expression} = ${name}");
            paramMap[name] = value;
        }

        AddEq("s.test_template_uid", "template", q.TemplateUid?.ToString("N"));
        AddEq("s.current_outcome", "outcome", q.Outcome.HasValue ? (int)q.Outcome.Value : null);
        AddEq("s.assegnatario", "assignee", string.IsNullOrWhiteSpace(q.Assignee) ? null : q.Assignee);
        AddEq("s.creato_da", "creator", string.IsNullOrWhiteSpace(q.Creator) ? null : q.Creator);
        AddEq("s.stato_lavoro", "workstate", q.WorkState.HasValue ? (int)q.WorkState.Value : null);
        AddEq("s.severita", "severity", q.Severity.HasValue ? (int)q.Severity.Value : null);
        AddEq("s.priorita", "priority", q.Priority.HasValue ? (int)q.Priority.Value : null);
        AddEq("s.origine_test", "origine", q.Origine.HasValue ? (int)q.Origine.Value : null);

        if (q.TemplateUids is { Count: > 0 })
        {
            var names = new List<string>();
            var i = 0;
            foreach (var uid in q.TemplateUids.Distinct())
            {
                var key = "templateSet" + i++;
                names.Add("$" + key);
                parameters[key] = uid.ToString("N");
            }
            clauses.Add($"s.test_template_uid IN ({string.Join(',', names)})");
        }

        if (q.WorkStates is { Count: > 0 })
        {
            var names = new List<string>();
            var i = 0;
            foreach (var state in q.WorkStates.Distinct())
            {
                var key = "workStateSet" + i++;
                names.Add("$" + key);
                parameters[key] = (int)state;
            }
            if (names.Count > 0) clauses.Add($"s.stato_lavoro IN ({string.Join(',', names)})");
        }

        // Filtri a selezione multipla della pagina Test (richiesta utente 6.4.2: tutti i filtri lista con lo schema
        // pulsante + multi-selezione). Lista vuota/null = nessun filtro; piu' valori = OR fra loro; combinati in AND
        // con i filtri singoli e con gli scoping (AllowedEcus/AllowedVehicles), che restano un vincolo distinto.
        // Valori sempre parametrizzati (mai concatenati nell'SQL).
        void AddInList<T>(string expression, string prefix, IReadOnlyCollection<T>? values, Func<T, object?> convert)
        {
            if (values is not { Count: > 0 }) return;
            var names = new List<string>();
            var visti = new HashSet<object>();
            foreach (var v in values)
            {
                var converted = convert(v);
                if (converted == null || !visti.Add(converted)) continue;
                var key = prefix + names.Count;
                names.Add("$" + key);
                paramMap[key] = converted;
            }
            // Lista non vuota ma con soli valori non validi: nessun risultato (fail-closed), mai "tutti".
            if (names.Count == 0) { clauses.Add("0 = 1"); return; }
            clauses.Add($"{expression} IN ({string.Join(',', names)})");
        }
        static object? Testo(string v) => string.IsNullOrWhiteSpace(v) ? null : v;
        AddInList("s.current_outcome", "outcomeSet", q.Outcomes, o => (int)o);
        AddInList("s.assegnatario", "assigneeSet", q.Assignees, Testo);
        AddInList("s.creato_da", "creatorSet", q.Creators, Testo);
        AddInList("s.severita", "severitySet", q.Severities, o => (int)o);
        AddInList("s.priorita", "prioritySet", q.Priorities, o => (int)o);

        void AddExistsInList(string table, string alias, string prefix, IReadOnlyCollection<string>? values, string collate)
        {
            if (values is not { Count: > 0 }) return;
            var names = new List<string>();
            var visti = new HashSet<string>(collate == "NOCASE" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var v in values)
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                var valore = v.Trim();
                if (!visti.Add(valore)) continue;
                var key = prefix + names.Count;
                names.Add("$" + key);
                paramMap[key] = valore;
            }
            if (names.Count == 0) { clauses.Add("0 = 1"); return; }
            clauses.Add($"EXISTS (SELECT 1 FROM {table} {alias} WHERE {alias}.test_uid = s.uid AND {alias}.value COLLATE {collate} IN ({string.Join(',', names)}))");
        }
        AddExistsInList("test_ecu", "fe", "ecuSet", q.Componenti, "NOCASE");
        AddExistsInList("test_vehicle", "fv", "vehicleSet", q.Vehicles, "NOCASE");
        AddExistsInList("test_requirement", "fr", "requirementSet", q.Requirements, "BINARY");
        AddExistsInList("test_functionality", "ff", "functionalitySet", q.Functionalities, "BINARY");

        if (!string.IsNullOrWhiteSpace(q.Componente))
        {
            var collation = q.MatchStringsOrdinal ? "BINARY" : "NOCASE";
            clauses.Add($"EXISTS (SELECT 1 FROM test_ecu x WHERE x.test_uid = s.uid AND x.value COLLATE {collation} = $componente)");
            parameters["componente"] = q.MatchStringsOrdinal ? q.Componente : q.Componente.Trim();
        }
        if (q.AllowedEcus is { Count: > 0 })
        {
            var names = new List<string>();
            var i = 0;
            foreach (var componente in q.AllowedEcus.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = "allowedEcu" + i++;
                names.Add("$" + key);
                parameters[key] = componente.Trim();
            }
            if (names.Count > 0)
                // IN su sottoquery invece di EXISTS correlata: stessi Test, ma la sottoquery si valuta una volta sola.
                // Con EXISTS SQLite cercava in test_ecu per ogni riga dell'ordine archivio (279 ms per 517 Test su 50k).
                clauses.Add($"s.uid IN (SELECT ae.test_uid FROM test_ecu ae WHERE ae.value COLLATE NOCASE IN ({string.Join(',', names)}))");
        }
        if (!string.IsNullOrWhiteSpace(q.Vehicle))
        {
            var collation = q.MatchStringsOrdinal ? "BINARY" : "NOCASE";
            clauses.Add($"EXISTS (SELECT 1 FROM test_vehicle x WHERE x.test_uid = s.uid AND x.value COLLATE {collation} = $vehicle)");
            parameters["vehicle"] = q.MatchStringsOrdinal ? q.Vehicle : q.Vehicle.Trim();
        }
        if (!string.IsNullOrWhiteSpace(q.Requirement))
        {
            clauses.Add("EXISTS (SELECT 1 FROM test_requirement x WHERE x.test_uid = s.uid AND x.value COLLATE BINARY = $requirement)");
            parameters["requirement"] = q.Requirement.Trim();
        }
        if (!string.IsNullOrWhiteSpace(q.Functionality))
        {
            clauses.Add("EXISTS (SELECT 1 FROM test_functionality x WHERE x.test_uid = s.uid AND x.value COLLATE BINARY = $functionality)");
            parameters["functionality"] = q.Functionality.Trim();
        }

        if (q.AllowedVehicles is { Count: > 0 })
        {
            var names = new List<string>();
            var i = 0;
            foreach (var vehicle in q.AllowedVehicles.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = "allowedVehicle" + i++;
                names.Add("$" + key);
                parameters[key] = vehicle;
            }
            if (names.Count > 0)
                clauses.Add($"EXISTS (SELECT 1 FROM test_vehicle av WHERE av.test_uid = s.uid AND av.value COLLATE NOCASE IN ({string.Join(',', names)}))");
        }

        if (q.ExcludedTemplateUids is { Count: > 0 })
        {
            var names = new List<string>();
            var i = 0;
            foreach (var uid in q.ExcludedTemplateUids.Distinct())
            {
                var key = "excludedTemplate" + i++;
                names.Add("$" + key);
                parameters[key] = uid.ToString("N");
            }
            if (names.Count > 0) clauses.Add($"s.test_template_uid NOT IN ({string.Join(',', names)})");
        }

        if (q.CreatedFrom.HasValue)
        {
            clauses.Add("s.data_creazione_ticks >= $createdFrom");
            parameters["createdFrom"] = q.CreatedFrom.Value.Date.ToUniversalTime().Ticks;
        }
        if (q.CreatedTo.HasValue)
        {
            clauses.Add("s.data_creazione_ticks < $createdToExclusive");
            parameters["createdToExclusive"] = DataAInclusiva.FineEsclusa(q.CreatedTo.Value.DateTime).ToUniversalTime().Ticks;
        }
        if (q.CreatedAtOrAfter.HasValue)
        {
            clauses.Add("s.data_creazione_raw_ticks >= $createdAtOrAfter");
            parameters["createdAtOrAfter"] = q.CreatedAtOrAfter.Value.Ticks;
        }
        if (q.CreatedAtOrBefore.HasValue)
        {
            // Data a inclusiva come nel filtro Report: il giorno indicato vale per intero (vedi DataAInclusiva).
            clauses.Add("s.data_creazione_raw_ticks < $createdBeforeExclusive");
            parameters["createdBeforeExclusive"] = DataAInclusiva.FineEsclusa(q.CreatedAtOrBefore.Value).Ticks;
        }

        if (q.CustomFieldSelections != null)
        {
            var customIndex = 0;
            foreach (var custom in q.CustomFieldSelections.Where(x => x.Value.Count > 0).OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var key = "customFieldKey" + customIndex++;
                parameters[key] = custom.Key;
                var names = new List<string>();
                // Maiuscole/minuscole ignorate come in Home (COLLATE NOCASE) e nel filtro in memoria (ReportFilter.CorrispondeSenzaTempo).
                foreach (var value in custom.Value.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var valueKey = "customFieldValue" + customIndex++;
                    names.Add("$" + valueKey);
                    parameters[valueKey] = value;
                }
                if (names.Count == 0) clauses.Add("0");
                else clauses.Add($"EXISTS (SELECT 1 FROM test_custom_field cf WHERE cf.test_uid = s.uid AND cf.field_key = ${key} AND cf.value COLLATE NOCASE IN ({string.Join(',', names)}))");
            }
        }

        // Filtri Lista Test indicizzati (6.6.1): "senza componente" e "riferimento Ticketing" sono esatti su test_summary; gli
        // insiemi di Uid calcolati dalle NC arrivano come JSON e si leggono con json_each (nessun limite sui parametri).
        if (q.SoloSenzaEcu) clauses.Add("s.ecu_grezzi = 0");
        if (q.Ticketing is { } ticketing)
        {
            parameters["polarionUid"] = System.Text.Json.JsonSerializer.Serialize(ticketing.UidConNcPolarion.Select(u => u.ToString("N")));
            clauses.Add(ticketing.ConPolarion
                ? "(s.ticket_collegati > 0 OR s.uid IN (SELECT value FROM json_each($polarionUid)))"
                : "(s.ticket_collegati = 0 AND s.uid NOT IN (SELECT value FROM json_each($polarionUid)))");
        }
        if (q.UidConAnomaliaAperta is { } uidAnomalia)
        {
            parameters["anomaliaUid"] = System.Text.Json.JsonSerializer.Serialize(uidAnomalia.Select(u => u.ToString("N")));
            clauses.Add("s.uid IN (SELECT value FROM json_each($anomaliaUid))");
        }
        // Ricerca testuale della Home: le stesse condizioni di HomeViewModel.Corrisponde, come OR fra i campi del Test.
        if (!string.IsNullOrWhiteSpace(q.TestoHome))
        {
            parameters["testoHome"] = q.TestoHome.Trim();
            parameters["templateTestoHome"] = System.Text.Json.JsonSerializer.Serialize(
                (q.TemplateUidTestoHome ?? Array.Empty<Guid>()).Select(u => u.ToString("N")));
            // Ricerche ASCII: LIKE/= con COLLATE NOCASE (nativi, come la Lista Test). Con caratteri non ASCII NOCASE non
            // piega le lettere accentate: si usano le funzioni ci_* (OrdinalIgnoreCase, esatte). Le tabelle figlie sono
            // sottoquery non correlate (una sola lettura ciascuna).
            var ascii = q.TestoHome.Trim().All(c => c < 128);
            parameters["testoHome"] = q.TestoHome.Trim();
            if (ascii) parameters["testoHomeLike"] = "%" + EscapeLike(q.TestoHome.Trim()) + "%";
            string Contiene(string colonna) => ascii
                ? $"{colonna} COLLATE NOCASE LIKE $testoHomeLike ESCAPE '\\'"
                : $"ci_contains({colonna}, $testoHome)";
            string Uguale(string colonna) => ascii
                ? $"{colonna} COLLATE NOCASE = $testoHome"
                : $"ci_eq({colonna}, $testoHome)";
            clauses.Add("(" + string.Join(" OR ", new[]
            {
                Contiene("s.id"),
                Contiene("s.descrizione"),
                "s.test_template_uid IN (SELECT value FROM json_each($templateTestoHome))",
                $"s.uid IN (SELECT x.test_uid FROM test_ecu x WHERE {Uguale("x.value")})",
                $"s.uid IN (SELECT x.test_uid FROM test_vehicle x WHERE {Contiene("x.value")})",
                $"s.uid IN (SELECT x.test_uid FROM test_requirement x WHERE {Contiene("x.value")})",
                $"s.uid IN (SELECT x.test_uid FROM test_functionality x WHERE {Contiene("x.value")})",
                $"s.uid IN (SELECT x.test_uid FROM test_revision x WHERE {Contiene("x.note")} OR {Contiene("x.deviazioni")})",
            }) + ")");
        }

        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var text = q.Text.Trim();
            if (IsFtsEnabled(connection))
            {
                clauses.Add("s.uid IN (SELECT uid FROM test_search WHERE test_search MATCH $fts)");
                parameters["fts"] = BuildFtsQuery(text);
            }
            else
            {
                clauses.Add("(s.id COLLATE NOCASE LIKE $like ESCAPE '\\' OR s.descrizione_breve COLLATE NOCASE LIKE $like ESCAPE '\\' OR s.descrizione COLLATE NOCASE LIKE $like ESCAPE '\\' OR s.assegnatario COLLATE NOCASE LIKE $like ESCAPE '\\' OR s.creato_da COLLATE NOCASE LIKE $like ESCAPE '\\' OR EXISTS (SELECT 1 FROM test_requirement rq WHERE rq.test_uid=s.uid AND rq.value COLLATE NOCASE LIKE $like ESCAPE '\\') OR EXISTS (SELECT 1 FROM test_functionality fn WHERE fn.test_uid=s.uid AND fn.value COLLATE NOCASE LIKE $like ESCAPE '\\'))");
                parameters["like"] = "%" + EscapeLike(text) + "%";
            }
        }

        return clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
    }

    private static IReadOnlyList<string> DistinctSummary(SqliteConnection connection, string column, bool includeDeleted)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT DISTINCT {column} COLLATE BINARY
            FROM test_summary
            WHERE {column} IS NOT NULL AND TRIM({column}) <> '' {(includeDeleted ? "" : "AND eliminato = 0")};
            """;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result.OrderBy(x => x).ToList();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        RegistraFunzioniTesto(connection);
        using var command = connection.CreateCommand();
        // Vedi lo stesso commento in LocalHomeDashboardQueryService.Open(): una connessione per chiamata su
        // un file da centinaia di MB altrimenti rilegge dal disco pagine già toccate poco prima nella stessa
        // apertura pagina. Sola lettura, nessuna implicazione sulla durabilità dei dati condivisi.
        command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA query_only=ON; PRAGMA cache_size=-131072; PRAGMA mmap_size=536870912;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static bool IsFtsEnabled(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key='fts_enabled' LIMIT 1;";
        return string.Equals(command.ExecuteScalar() as string, "1", StringComparison.Ordinal);
    }

    private static void AddParameters(SqliteCommand command, IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue("$" + name, value ?? DBNull.Value);
    }

    private static void AccumulateCoverage(CoverageBucket bucket, Esito? esito)
    {
        switch (esito)
        {
            case Esito.Conforme: bucket.Conformi++; break;
            case Esito.ConformeConDeviazioni: bucket.ConformiConDeviazioni++; break;
            case Esito.NonConforme: bucket.NonConformi++; break;
            default: bucket.NonTestati++; break;
        }
    }

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string BuildFtsQuery(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Replace("\"", "\"\""))
            .Where(t => t.Length > 0)
            .Select(t => $"\"{t}\"*");
        return string.Join(" AND ", terms);
    }

    private sealed class RequirementVehicleComparer : IEqualityComparer<(string Requisito, string Veicolo)>
    {
        public static RequirementVehicleComparer Instance { get; } = new();
        public bool Equals((string Requisito, string Veicolo) x, (string Requisito, string Veicolo) y) =>
            string.Equals(x.Requisito, y.Requisito, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Veicolo, y.Veicolo, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Requisito, string Veicolo) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Requisito),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Veicolo));
    }
}

internal sealed class LocalTestArchiveResultLimitExceededException : InvalidOperationException
{
    public LocalTestArchiveResultLimitExceededException(long actualRows, long maximumRows)
        : base($"Il risultato richiesto ({actualRows}) supera il limite sicuro ({maximumRows}).")
    {
        ActualRows = actualRows;
        MaximumRows = maximumRows;
    }

    public long ActualRows { get; }
    public long MaximumRows { get; }
}

public enum LocalTestDeletionScope
{
    ActiveOnly,
    DeletedOnly,
    All
}

public sealed class LocalTestArchiveQuery
{
    public string? Text { get; init; }
    public string? Componente { get; init; }
    public IReadOnlyCollection<string>? AllowedEcus { get; init; }
    // Selezioni multiple dei filtri lista (OR fra i valori, AND con gli altri filtri). Null/vuoto = nessun filtro.
    public IReadOnlyCollection<string>? Componenti { get; init; }
    public IReadOnlyCollection<string>? Vehicles { get; init; }
    public IReadOnlyCollection<Esito>? Outcomes { get; init; }
    public IReadOnlyCollection<string>? Assignees { get; init; }
    public IReadOnlyCollection<string>? Creators { get; init; }
    public IReadOnlyCollection<string>? Requirements { get; init; }
    public IReadOnlyCollection<string>? Functionalities { get; init; }
    public IReadOnlyCollection<SeveritaAnomalia>? Severities { get; init; }
    public IReadOnlyCollection<PrioritaOperativa>? Priorities { get; init; }
    public string? Vehicle { get; init; }
    public Guid? TemplateUid { get; init; }
    public IReadOnlyCollection<Guid>? TemplateUids { get; init; }
    public Esito? Outcome { get; init; }
    public string? Assignee { get; init; }
    public string? Creator { get; init; }
    public StatoLavoroTest? WorkState { get; init; }
    public IReadOnlyCollection<StatoLavoroTest>? WorkStates { get; init; }
    public string? Requirement { get; init; }
    public string? Functionality { get; init; }
    public SeveritaAnomalia? Severity { get; init; }
    public PrioritaOperativa? Priority { get; init; }
    public DateTimeOffset? CreatedFrom { get; init; }
    public DateTimeOffset? CreatedTo { get; init; }
    public DateTime? CreatedAtOrAfter { get; init; }
    /// <summary>Data a inclusiva (filtro Report): il giorno indicato è incluso per intero, vedi <see cref="DataAInclusiva"/>.</summary>
    public DateTime? CreatedAtOrBefore { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>>? CustomFieldSelections { get; init; }
    /// <summary>Semantica ordinale del fallback ReportFilter; gli altri consumer mantengono NOCASE.</summary>
    internal bool MatchStringsOrdinal { get; init; }
    public IReadOnlyCollection<string>? AllowedVehicles { get; init; }
    public IReadOnlyCollection<Guid>? ExcludedTemplateUids { get; init; }
    /// <summary>Filtro dedicato pagina Test "Importato da UnitLogger" (decisione utente 6.4.0, "Test importati: solo
    /// stato finale"). Null = tutti, indipendentemente dall'origine.</summary>
    public OrigineTest? Origine { get; init; }
    /// <summary>Filtro Lista Test "senza componente" (equivale a TestCase.Componente.Count == 0): confronta il conteggio grezzo ecu_grezzi.</summary>
    public bool SoloSenzaEcu { get; init; }
    /// <summary>Filtro Lista Test "riferimento Ticketing" (vedi FiltroPolarioneOpzione). Null = nessun filtro.</summary>
    public LocalTestPolarionFiltro? Ticketing { get; init; }
    /// <summary>Uid dei Test con almeno una anomalia aperta nello stato di workflow scelto, calcolati dal chiamante
    /// sulle NC con la stessa regola della pagina Anomalie. Null = nessun filtro; insieme vuoto = nessun risultato.</summary>
    public IReadOnlyCollection<Guid>? UidConAnomaliaAperta { get; init; }
    /// <summary>Ricerca testuale della Home (Da fare / Fatti): stessa regola di HomeViewModel.Corrisponde su id, descrizione,
    /// componente (uguaglianza), veicoli, requisiti, funzionalita', nome del template e note/deviazioni delle esecuzioni.
    /// Null o vuoto = nessun filtro.</summary>
    public string? TestoHome { get; init; }
    /// <summary>Uid dei template il cui nome contiene il testo (calcolati dal chiamante con AppState.NomeTestTemplate).</summary>
    public IReadOnlyCollection<Guid>? TemplateUidTestoHome { get; init; }
    public LocalTestDeletionScope DeletionScope { get; init; } = LocalTestDeletionScope.ActiveOnly;
    public long Page { get; init; } = 1;
    public int PageSize { get; init; } = LocalTestArchiveQueryService.DefaultPageSize;
}

public sealed record LocalTestArchivePage(IReadOnlyList<TestCase> Items, long TotalCount, long Page, int PageSize)
{
    public long PageCount => Math.Max(1L, checked((TotalCount + Math.Max(1, PageSize) - 1L) / Math.Max(1, PageSize)));
}

public sealed record LocalTestArchiveSlice(IReadOnlyList<TestCase> Items, long TotalCount);

/// <summary>Filtro "riferimento Ticketing": ConPolarion = true per i Test con ticket o NC collegate a Ticketing.
/// UidConNcPolarion = Test con almeno una NC associata (AppartieneAlTest) con CollegamentiPolarion; i ticket
/// collegati direttamente al Test sono gia' in test_summary.ticket_collegati.</summary>
public sealed record LocalTestPolarionFiltro(bool ConPolarion, IReadOnlyCollection<Guid> UidConNcPolarion);

public sealed class LocalTestFilterCatalog
{
    public IReadOnlyList<string> Componente { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Veicoli { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Assegnatari { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Creatori { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Requisiti { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Funzionalita { get; init; } = Array.Empty<string>();
}

/// <summary>Richiesta del refresh Home unificato: ogni sezione è opzionale, così un chiamante che non ne ha
/// bisogno in questo giro (es. nessun filtro custom configurato) non paga la query corrispondente.</summary>
public sealed class LocalHomeAggiornaBatchRequest
{
    public bool IncludeEcu { get; init; } = true;
    /// <summary>Sommario della testata (conteggi, Test senza componente, base del grafico Donut della Home). False quando
    /// Riepilogo, Avvisi e KPI sono tutti nascosti: il sommario non viene letto e il risultato lo dichiara vuoto.</summary>
    public bool IncludeSummary { get; init; } = true;
    public string? ActiveOperator { get; init; }
    public IReadOnlyList<Requisito>? Requisiti { get; init; }
    public bool SoloBanco { get; init; }
    public bool SoloVeicolo { get; init; }
    public IReadOnlyList<string> CustomFieldKeys { get; init; } = Array.Empty<string>();
    public LocalTestArchiveQuery? TodoQuery { get; init; }
    public LocalTestArchiveQuery? DoneQuery { get; init; }
}

public sealed class LocalHomeAggiornaBatchResult
{
    /// <summary>Revisione dell'indice letta nello stesso snapshot del batch (P2.6): token di validita' per i chiamanti.</summary>
    public long Revisione { get; init; }
    public IReadOnlyList<string> Componente { get; init; } = Array.Empty<string>();
    public LocalHomeArchiveSummary Summary { get; init; } = new();
    public bool HasAnyRevision { get; init; }
    public CoverageBucket? Coverage { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CustomFieldValues { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();
    public LocalTestArchivePage? Todo { get; init; }
    public LocalTestArchivePage? Done { get; init; }
}

public sealed class LocalHomeArchiveSummary
{
    public long TotalTests { get; init; }
    public long ToRetest { get; init; }
    public long MyActive { get; init; }
    public long Tested { get; init; }
    public long Conforming { get; init; }
    public long WithoutEcu { get; init; }
    public StatiMatrice TestStatusDistribution { get; init; }
    public double ConformityRatePercent => Tested == 0 ? 0 : 100.0 * Conforming / Tested;
}

/// <summary>Un solo candidato vincente dell'indice per la chiave funzionalità×componente×veicolo.
/// Outcome null o non eseguito conserva la semantica di previsione/non testato del reducer Report.</summary>
public sealed record LocalHomeFunctionalitySource(string Funzionalita, string Componente, string Veicolo,
    Esito? Outcome, long DataTicks, string TestId, int Revisione, int Tentativo);

public sealed class LocalDefaultDashboardSnapshot
{
    public Dictionary<string, long> VehicleCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<Esito, long> OutcomeCounts { get; } = new();
    /// <summary>Test per (veicolo, esito): stessa sorgente e stesso filtro di VehicleCounts, per i grafici che mostrano l'esito per veicolo.</summary>
    public List<(string Nome, Esito Esito, long Valore)> VehicleOutcomeRows { get; } = new();
    public Dictionary<string, long> ExecutionCountsByOperator { get; } = new(StringComparer.OrdinalIgnoreCase);
    public KpiIterazioniConforme IterationKpi { get; set; } = new();
}





