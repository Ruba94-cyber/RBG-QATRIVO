using TestLoom.App.Models;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Read-side locale e ricostruibile per l'Archivio dei master ad alta crescita. Legge la stessa master-index.db
/// usata dalla consultazione paginata, ma seleziona ESCLUSIVAMENTE archived=1. Non e' mai source of truth: il
/// caller deve prima riconciliare l'indice con la fonte autorevole e qualsiasi errore fa fallback al repository.
/// Conteggi/offset sono a 64 bit: il limite 1000 riguarda soltanto la singola finestra restituita, non il catalogo.
/// </summary>
public sealed class LocalMasterArchiveQueryService
{
    private const int BusyTimeoutMs = 750;
    private readonly string _connectionString;

    public LocalMasterArchiveQueryService(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Percorso database master locale non valido.", nameof(databasePath));

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public long CountArchived(string catalog)
    {
        ValidateCatalog(catalog);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM master_item WHERE catalog=$catalog AND archived=1;";
        command.Parameters.AddWithValue("$catalog", catalog);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public IReadOnlyList<T> QueryArchived<T>(string catalog, long offset, int limit) where T : MasterEntity
    {
        ValidateCatalog(catalog);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_key, json
            FROM master_item
            WHERE catalog=$catalog AND archived=1
            ORDER BY modified_ticks DESC, key_sort COLLATE NOCASE
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$catalog", catalog);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var result = new List<T>(limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var item = JsonIo.TryParse<T>(reader.GetString(1), out var error);
            if (item == null)
                throw new InvalidDataException($"Indice locale archivio master {catalog}/{reader.GetString(0)} illeggibile: {error}");
            result.Add(item);
        }
        return result;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        // cache_size/mmap_size: una connessione per chiamata su un file condiviso da più tabelle non deve
        // ripartire ogni volta con la minuscola cache pagine di default (feedback utente 2026-09-17, volumi
        // 100k: stesso problema misurato e risolto in LocalHomeDashboardQueryService/LocalTestArchiveQueryService).
        pragma.CommandText = $"PRAGMA query_only=ON; PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA cache_size=-131072; PRAGMA mmap_size=536870912;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void ValidateCatalog(string catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog))
            throw new ArgumentException("Catalogo master non valido.", nameof(catalog));
    }
}
