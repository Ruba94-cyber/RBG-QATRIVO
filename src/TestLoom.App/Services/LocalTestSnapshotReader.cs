using System.Text.Json;
using TestLoom.App.Models;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Lettore leggero della fotografia SQLite dei Test. Il warm-start usa gli header per costruire lookup ID/UID
/// senza deserializzare il payload completo di ogni Test; il JSON viene idratato soltanto quando un chiamante
/// apre davvero il record o usa un percorso legacy che richiede l'intero archivio.
/// </summary>
public sealed class LocalTestSnapshotReader
{
    private const int BusyTimeoutMs = 500;
    private readonly string _connectionString;

    public LocalTestSnapshotReader(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public IReadOnlyList<LocalTestSnapshotHeader> LoadHeaders()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path, uid, id, revision, eliminato, mtime_utc_ticks, file_length, content_hash
            FROM test_case_cache
            ORDER BY path;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<LocalTestSnapshotHeader>();
        while (reader.Read())
        {
            if (!Guid.TryParseExact(reader.GetString(1), "N", out var uid)) continue;
            result.Add(new LocalTestSnapshotHeader(
                reader.GetString(0), uid, reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4) != 0,
                new DateTime(reader.GetInt64(5), DateTimeKind.Utc), reader.GetInt64(6), reader.GetString(7)));
        }
        return result;
    }

    public LocalIndexedTest? LoadOne(string path)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mtime_utc_ticks, file_length, content_hash, json,
                   (SELECT value FROM meta WHERE key = 'content_hash_algo') AS marcatore_algoritmo
            FROM test_case_cache
            WHERE path = $path
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var tc = JsonSerializer.Deserialize<TestCase>(reader.GetString(3), JsonIo.Options);
        if (tc == null) return null;
        tc.NormalizzaVeicoli();
        TestCaseFingerprint.StampDaIndice(tc, reader.GetString(2), reader.IsDBNull(4) ? null : reader.GetString(4));
        return new LocalIndexedTest(
            path,
            new DateTime(reader.GetInt64(0), DateTimeKind.Utc),
            reader.GetInt64(1),
            reader.GetString(2),
            tc);
    }

    public IReadOnlyList<LocalIndexedTest> LoadMany(IEnumerable<string> paths)
    {
        var requested = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (requested.Count == 0) return Array.Empty<LocalIndexedTest>();
        var result = new List<LocalIndexedTest>(requested.Count);
        foreach (var batch in requested.Chunk(500))
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            var names = new List<string>();
            for (var i = 0; i < batch.Length; i++)
            {
                var key = "$p" + i;
                names.Add(key);
                command.Parameters.AddWithValue(key, batch[i]);
            }
            command.CommandText = $"""
                SELECT path, mtime_utc_ticks, file_length, content_hash, json,
                       (SELECT value FROM meta WHERE key = 'content_hash_algo') AS marcatore_algoritmo
                FROM test_case_cache
                WHERE path IN ({string.Join(',', names)});
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var tc = JsonSerializer.Deserialize<TestCase>(reader.GetString(4), JsonIo.Options);
                if (tc == null) continue;
                tc.NormalizzaVeicoli();
                TestCaseFingerprint.StampDaIndice(tc, reader.GetString(3), reader.IsDBNull(5) ? null : reader.GetString(5));
                result.Add(new LocalIndexedTest(
                    reader.GetString(0),
                    new DateTime(reader.GetInt64(1), DateTimeKind.Utc),
                    reader.GetInt64(2),
                    reader.GetString(3), tc));
            }
        }
        return result;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        // Vedi lo stesso commento in LocalHomeDashboardQueryService.Open() (feedback utente 2026-09-17, volumi 100k).
        command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA query_only=ON; PRAGMA cache_size=-131072; PRAGMA mmap_size=536870912;";
        command.ExecuteNonQuery();
        return connection;
    }
}

public sealed record LocalTestSnapshotHeader(
    string Path,
    Guid Uid,
    string Id,
    long Revision,
    bool Eliminato,
    DateTime MtimeUtc,
    long FileLength,
    string ContentHash);
