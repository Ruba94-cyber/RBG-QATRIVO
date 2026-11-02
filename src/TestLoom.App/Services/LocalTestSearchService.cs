using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Ricerca Test sul read-model SQLite locale. Restituisce soltanto i campi necessari alla ricerca globale,
/// senza deserializzare i payload TestCase e senza toccare OneDrive. Null significa che l'indice/FTS non è
/// disponibile e il chiamante deve usare il fallback in memoria; una lista vuota significa ricerca valida
/// senza risultati.
/// </summary>
public static class LocalTestSearchService
{
    public static IReadOnlyList<LocalTestSearchHit>? Search(string? databasePath, string query, int limit = 24) =>
        Search(databasePath, query, limit, null);

    /// <summary>
    /// Con il coordinatore, una corruzione incontrata dalla ricerca globale non si limita a far tornare null
    /// (fallback silenzioso e permanente fino al riavvio): partecipa allo stesso recupero raggruppato degli
    /// altri lettori. BUSY, permessi, disco pieno e FTS assente restano semplici fallback, senza quarantena.
    /// </summary>
    public static IReadOnlyList<LocalTestSearchHit>? Search(
        string? databasePath, string query, int limit, LocalIndexRecoveryCoordinator? coordinatore)
    {
        if (coordinatore != null)
        {
            return coordinatore.TryRead("ricerca globale Test",
                () => SearchCore(databasePath, query, limit), out var risultato)
                ? risultato
                : null;
        }

        // Contratto storico dei chiamanti senza coordinatore: qualunque guasto diventa null (fallback in memoria).
        try { return SearchCore(databasePath, query, limit); }
        catch (SqliteException) { return null; }
    }

    private static IReadOnlyList<LocalTestSearchHit>? SearchCore(string? databasePath, string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath) || string.IsNullOrWhiteSpace(query))
            return null;

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                Pooling = true
            }.ToString());
            connection.Open();
            using (var setup = connection.CreateCommand())
            {
                setup.CommandText = "PRAGMA busy_timeout=500; PRAGMA query_only=ON; PRAGMA cache_size=-131072; PRAGMA mmap_size=536870912;";
                setup.ExecuteNonQuery();
            }

            // Se FTS5 non era disponibile alla creazione dell'indice, la tabella può non esistere: in quel
            // caso l'eccezione viene convertita in null e la UI torna al filtro in memoria.
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    s.uid,
                    s.id,
                    s.descrizione_breve,
                    s.descrizione,
                    COALESCE((SELECT group_concat(e.value, ' · ') FROM test_ecu e WHERE e.test_uid = s.uid), ''),
                    COALESCE((SELECT group_concat(v.value, ', ') FROM test_vehicle v WHERE v.test_uid = s.uid), '')
                FROM test_search
                JOIN test_summary s ON s.uid = test_search.uid
                WHERE test_search MATCH $query
                  AND s.eliminato = 0
                ORDER BY bm25(test_search), s.ultima_modifica_ticks DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$query", BuildFtsQuery(query));
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

            using var reader = command.ExecuteReader();
            var result = new List<LocalTestSearchHit>();
            while (reader.Read())
            {
                if (!Guid.TryParseExact(reader.GetString(0), "N", out var uid)) continue;
                result.Add(new LocalTestSearchHit
                {
                    Uid = uid,
                    Id = reader.GetString(1),
                    DescrizioneBreve = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Descrizione = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    EcuTesto = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    VeicoliTesto = reader.IsDBNull(5) ? "" : reader.GetString(5)
                });
            }
            return result;
        }
        catch (SqliteException ex) when (!LocalIndexFaultClassifier.IsCorruption(ex))
        {
            // FTS assente, BUSY, permessi: fallback in memoria per questo giro, nessuna azione distruttiva.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        // Una CORRUZIONE non viene piu' inghiottita qui: propaga al coordinatore, che la raggruppa con le
        // altre richieste e ricostruisce una volta sola. Senza coordinatore il chiamante storico la
        // riconverte comunque in null (vedi l'overload pubblico a due parametri).
    }

    private static string BuildFtsQuery(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Replace("\"", "\"\""))
            .Where(t => t.Length > 0)
            .Select(t => $"\"{t}\"*")
            .ToList();
        return terms.Count == 0 ? "\"\"" : string.Join(" AND ", terms);
    }
}

public sealed class LocalTestSearchHit
{
    public Guid Uid { get; init; }
    public string Id { get; init; } = "";
    public string DescrizioneBreve { get; init; } = "";
    public string Descrizione { get; init; } = "";
    public string EcuTesto { get; init; } = "";
    public string VeicoliTesto { get; init; } = "";

    public IReadOnlyList<string> Veicoli => VeicoliTesto
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
