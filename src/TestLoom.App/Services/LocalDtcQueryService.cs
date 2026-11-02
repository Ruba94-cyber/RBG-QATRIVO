using TestLoom.App.Models;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>Lettura in STREAMING degli esiti guasto dalla proiezione SQLite locale test_dtc_operation (indice test
/// v9, 6.5.0 punto 7): nessun Test viene caricato o scandito in memoria, la RAM resta limitata alle celle
/// dell'anagrafica (DtcAvanzamentoService.Calcola consuma l'enumerazione una volta sola). Sola lettura, l'indice
/// resta una cache ricostruibile dal JSON autorevole. Stessa semantica di DtcAvanzamentoService.RaccogliEsiti:
/// solo operazioni EFFETTIVE e realmente eseguite (Conforme/NonConforme/ConformeConDeviazioni = 1,2,3) di Test non
/// eliminati; componente = componente esplicite dell'operazione, altrimenti l'unica componente del Test (se esattamente una).</summary>
public sealed class LocalDtcQueryService
{
    private const int BusyTimeoutMs = 500;
    private readonly string _connectionString;

    public LocalDtcQueryService(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Percorso database locale non valido.", nameof(databasePath));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = true
        }.ToString();
    }

    private const string SqlBase = """
        SELECT d.requisito, d.guasto, oe.componente, d.passo, d.outcome, d.data_ticks, s.id, d.veicolo
        FROM test_dtc_operation d
        JOIN test_summary s ON s.uid = d.test_uid AND s.eliminato = 0
        JOIN test_operation_ecu oe ON oe.test_uid = d.test_uid AND oe.revision_index = d.revision_index AND oe.attempt_index = d.attempt_index
        WHERE d.is_effective = 1 AND d.outcome IN (1, 2, 3) {REQ}
        UNION ALL
        SELECT d.requisito, d.guasto, te.value, d.passo, d.outcome, d.data_ticks, s.id, d.veicolo
        FROM test_dtc_operation d
        JOIN test_summary s ON s.uid = d.test_uid AND s.eliminato = 0
        JOIN test_ecu te ON te.test_uid = d.test_uid
        WHERE d.is_effective = 1 AND d.outcome IN (1, 2, 3) {REQ}
          AND NOT EXISTS (SELECT 1 FROM test_operation_ecu x WHERE x.test_uid = d.test_uid AND x.revision_index = d.revision_index AND x.attempt_index = d.attempt_index)
          AND (SELECT COUNT(*) FROM test_ecu y WHERE y.test_uid = d.test_uid) = 1;
        """;

    /// <summary>Enumerazione pigra degli esiti (la connessione resta aperta fino a fine enumerazione o Dispose).
    /// Annullabile: il token e controllato a ogni riga.</summary>
    public IEnumerable<EsitoDtcRilevato> StreamEsiti(CancellationToken ct = default, string? requisitoId = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA query_only=ON; PRAGMA cache_size=-65536;";
            pragma.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        var filtro = string.IsNullOrWhiteSpace(requisitoId);
        command.CommandText = SqlBase.Replace("{REQ}", filtro ? "" : "AND d.requisito = $req");
        if (!filtro) command.Parameters.AddWithValue("$req", requisitoId!.Trim());
        using var reader = command.ExecuteReader();
        var ordine = 0;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            yield return new EsitoDtcRilevato(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                (PassoDtc)reader.GetInt32(3), (Esito)reader.GetInt32(4),
                new DateTime(reader.GetInt64(5), DateTimeKind.Utc).ToLocalTime(), reader.GetString(6), ordine++, reader.GetString(7));
        }
    }

    /// <summary>True se la tabella test_dtc_operation esiste CON la colonna veicolo (indice v11+). Un indice piu vecchio (anche v10, non
    /// ancora migrato) non e usabile per questa query: il chiamante ripiega sul calcolo in memoria.</summary>
    public bool Disponibile()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM pragma_table_info('test_dtc_operation') WHERE name='veicolo';";
            return command.ExecuteScalar() != null;
        }
        catch (SqliteException) { return false; }
    }
}
