using TestLoom.App.Models;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Query statistiche operative sul read-model SQLite locale.
///
/// REGOLA DI DOMINIO: una Condizione/operazione che coinvolge N componente vale N operazioni effettivamente svolte.
/// Quindi una prova Funzionalita X su componente Y+Z vale 2 operazioni; una prova K su Y vale 1. Le revisioni non
/// duplicano il passato: aggiungono soltanto le operazioni/tentativi realmente presenti nelle loro Condizioni.
/// Una Condizione vuota senza componente non vale un'operazione. Per vecchi record con una sola componente sul Test ma componente
/// non ancora salvata sulla Condizione, quella singola componente viene inferita senza ambiguità.
///
/// Il servizio legge le tabelle normalizzate dell'indice locale e NON il journal OneDrive. Lo storico
/// autorevole resta nel TestCase JSON; il DB e queste statistiche sono ricostruibili anche dopo anni.
///
/// DECISIONE UTENTE 6.4.0, punto 1: un'operazione vale come unità solo quando è stata realmente eseguita
/// (Esito = Conforme/Non Conforme/Conforme con deviazioni — vedi Esito.EEseguito() lato modello). "Da
/// eseguire" e N.A. sono filtrati già dentro operation_units, quindi ogni query costruita su questa CTE
/// eredita automaticamente la regola senza doverla ripetere.
/// </summary>
public sealed class LocalOperationalStatisticsService
{
    private const int BusyTimeoutMs = 500;
    private readonly string _connectionString;

    // "1, 2, 3" = valori interi di Esito considerati "eseguiti" (Models/Enums.cs: Conforme=1, NonConforme=2,
    // ConformeConDeviazioni=3). Letterali perché la CTE è testo SQL statico condiviso da più comandi;
    // EsitoSemantica.EEseguito() resta comunque l'unica fonte di verità lato C#, questi valori devono solo
    // restare coerenti con quell'enum (blindato da LocalOperationalStatisticsServiceTests).
    private const string OperationUnitsCte = """
        WITH explicit_units AS (
            SELECT a.test_uid, a.revision_index, a.attempt_index, a.operatore, a.funzionalita,
                   a.outcome, a.is_effective, e.componente
            FROM test_operation_attempt a
            JOIN test_operation_ecu e
              ON e.test_uid = a.test_uid
             AND e.revision_index = a.revision_index
             AND e.attempt_index = a.attempt_index
        ),
        legacy_single_ecu AS (
            SELECT a.test_uid, a.revision_index, a.attempt_index, a.operatore, a.funzionalita,
                   a.outcome, a.is_effective, te.value AS componente
            FROM test_operation_attempt a
            JOIN test_ecu te ON te.test_uid = a.test_uid
            WHERE NOT EXISTS (
                SELECT 1 FROM test_operation_ecu oe
                WHERE oe.test_uid = a.test_uid
                  AND oe.revision_index = a.revision_index
                  AND oe.attempt_index = a.attempt_index
            )
              AND (SELECT COUNT(*) FROM test_ecu only_ecu WHERE only_ecu.test_uid = a.test_uid) = 1
        ),
        operation_units_raw AS (
            SELECT * FROM explicit_units
            UNION ALL
            SELECT * FROM legacy_single_ecu
        ),
        operation_units AS (
            SELECT * FROM operation_units_raw u WHERE u.outcome IN (1, 2, 3)
        )
        """;

    public LocalOperationalStatisticsService(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Percorso database locale non valido.", nameof(databasePath));

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    /// <summary>Riepilogo archivio. OperationCount è componente-expanded, non semplice numero di Condizioni.
    /// Decisione utente 6.4.0 ("Test importati: solo stato finale"): i Test con OrigineTest.ImportStatoUdt
    /// sono fotografie di uno stato noto, non esecuzioni fatte in TestLoom — esclusi da tutte le statistiche
    /// operative di questo servizio (origine_test = 0, cioè TestLoom).</summary>
    public OperationalArchiveStatistics GetArchiveStatistics(bool includeDeleted = false)
    {
        using var connection = Open();
        var result = new OperationalArchiveStatistics();

        using (var summary = connection.CreateCommand())
        {
            summary.CommandText = $"""
                SELECT
                    COUNT(*),
                    COALESCE(SUM(executed_revision_count), 0),
                    COALESCE(SUM(CASE WHEN current_outcome IS NOT NULL AND executed_revision_count > 0 THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN current_outcome = $conforme AND executed_revision_count > 0 THEN 1 ELSE 0 END), 0)
                FROM test_summary
                WHERE origine_test = 0 {(includeDeleted ? "" : "AND eliminato = 0")};
                """;
            summary.Parameters.AddWithValue("$conforme", (int)Esito.Conforme);
            using var reader = summary.ExecuteReader();
            if (reader.Read())
            {
                result.TestCount = reader.GetInt32(0);
                result.RevisionCount = reader.GetInt32(1);
                result.TestedCount = reader.GetInt32(2);
                result.ConformingCount = reader.GetInt32(3);
            }
        }

        using (var operations = connection.CreateCommand())
        {
            operations.CommandText = OperationUnitsCte + $"""
                SELECT
                    COUNT(*),
                    COALESCE(SUM(CASE WHEN u.is_effective = 1 THEN 1 ELSE 0 END), 0)
                FROM operation_units u
                JOIN test_summary t ON t.uid = u.test_uid
                WHERE t.origine_test = 0 {(includeDeleted ? "" : "AND t.eliminato = 0")};
                """;
            using var reader = operations.ExecuteReader();
            if (reader.Read())
            {
                result.OperationAttemptCount = reader.GetInt32(0);
                result.EffectiveOperationCount = reader.GetInt32(1);
            }
        }

        return result;
    }

    /// <summary>
    /// Quantità di lavoro per operatore (REV Rete escluse: nessun operatore esecutore; REV solo pianificate escluse). Le operazioni multi-componente sono espanse; il numero revisioni resta il
    /// numero di revisioni effettivamente eseguite dall'operatore, non viene moltiplicato per componente.
    /// </summary>
    public IReadOnlyList<OperationalOperatorStatistics> GetByOperator(bool includeDeleted = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = OperationUnitsCte + $"""
            , revisions AS (
                SELECT r.operatore, COUNT(*) revision_count,
                       SUM(CASE WHEN r.outcome = $conforme THEN 1 ELSE 0 END) conforming_revision_count
                FROM test_revision r
                JOIN test_summary t ON t.uid = r.test_uid
                WHERE t.origine_test = 0 AND r.raw_attempt_count > 0 AND r.origine <> 1 {(includeDeleted ? "" : "AND t.eliminato = 0")}
                GROUP BY r.operatore
            )
            SELECT
                u.operatore,
                COUNT(DISTINCT u.test_uid),
                COALESCE(MAX(r.revision_count), 0),
                COUNT(*),
                SUM(CASE WHEN u.is_effective = 1 THEN 1 ELSE 0 END),
                COALESCE(MAX(r.conforming_revision_count), 0)
            FROM operation_units u
            JOIN test_summary t ON t.uid = u.test_uid
            JOIN test_revision rv ON rv.test_uid = u.test_uid AND rv.revision_index = u.revision_index AND rv.origine <> 1
            LEFT JOIN revisions r ON r.operatore = u.operatore
            WHERE t.origine_test = 0 {(includeDeleted ? "" : "AND t.eliminato = 0")}
            GROUP BY u.operatore
            ORDER BY u.operatore COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$conforme", (int)Esito.Conforme);

        using var reader = command.ExecuteReader();
        var result = new List<OperationalOperatorStatistics>();
        while (reader.Read())
        {
            result.Add(new OperationalOperatorStatistics
            {
                Operator = reader.IsDBNull(0) ? "" : reader.GetString(0),
                TestCount = reader.GetInt32(1),
                RevisionCount = reader.GetInt32(2),
                OperationAttemptCount = reader.GetInt32(3),
                EffectiveOperationCount = reader.GetInt32(4),
                ConformingRevisionCount = reader.GetInt32(5)
            });
        }
        return result;
    }

    /// <summary>
    /// KPI per Funzionalità ed componente. La coppia statistica minima è Test+Funzionalità+componente, perché la stessa
    /// funzionalità provata su due componente rappresenta due operazioni distinte. AttemptsToFirstConforme conta le
    /// unità operative cronologiche fino al primo successo di quella specifica coppia Funzionalità/componente.
    /// </summary>
    public IReadOnlyList<OperationalFunctionalityStatistics> GetByFunctionality(bool includeDeleted = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = OperationUnitsCte + $"""
            SELECT
                u.test_uid,
                u.funzionalita,
                u.componente,
                u.outcome,
                u.is_effective
            FROM operation_units u
            JOIN test_summary t ON t.uid = u.test_uid
            WHERE u.funzionalita <> '' AND t.origine_test = 0 {(includeDeleted ? "" : "AND t.eliminato = 0")}
            ORDER BY u.test_uid, u.funzionalita COLLATE NOCASE, u.componente COLLATE NOCASE,
                     u.revision_index, u.attempt_index;
            """;

        using var reader = command.ExecuteReader();
        var perPair = new Dictionary<FunctionalityEcuPair, PairAccumulator>();
        while (reader.Read())
        {
            var key = new FunctionalityEcuPair(reader.GetString(0), reader.GetString(1), reader.GetString(2));
            if (!perPair.TryGetValue(key, out var acc)) perPair[key] = acc = new PairAccumulator();
            acc.TotalAttempts++;
            if (reader.GetInt32(4) != 0) acc.EffectiveOperations++;
            if (!acc.AttemptsToFirstConforme.HasValue && reader.GetInt32(3) == (int)Esito.Conforme)
                acc.AttemptsToFirstConforme = acc.TotalAttempts;
        }

        return perPair
            .GroupBy(x => x.Key.Functionality, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var successful = g.Where(x => x.Value.AttemptsToFirstConforme.HasValue).ToList();
                return new OperationalFunctionalityStatistics
                {
                    Functionality = g.Key,
                    TestFunctionalityEcuPairs = g.Count(),
                    SuccessfulPairs = successful.Count,
                    NeverSuccessfulPairs = g.Count() - successful.Count,
                    TotalOperationAttempts = g.Sum(x => x.Value.TotalAttempts),
                    TotalEffectiveOperations = g.Sum(x => x.Value.EffectiveOperations),
                    AverageAttemptsToFirstConforme = successful.Count == 0
                        ? 0
                        : successful.Average(x => x.Value.AttemptsToFirstConforme!.Value)
                };
            })
            .OrderBy(x => x.Functionality, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Dettaglio per componente, utile per carico di validazione e reporting tecnico.</summary>
    public IReadOnlyList<OperationalEcuStatistics> GetByEcu(bool includeDeleted = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = OperationUnitsCte + $"""
            SELECT
                u.componente,
                COUNT(*),
                SUM(CASE WHEN u.is_effective = 1 THEN 1 ELSE 0 END),
                SUM(CASE WHEN u.outcome = $conforme THEN 1 ELSE 0 END)
            FROM operation_units u
            JOIN test_summary t ON t.uid = u.test_uid
            WHERE t.origine_test = 0 {(includeDeleted ? "" : "AND t.eliminato = 0")}
            GROUP BY u.componente
            ORDER BY u.componente COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$conforme", (int)Esito.Conforme);
        using var reader = command.ExecuteReader();
        var result = new List<OperationalEcuStatistics>();
        while (reader.Read())
        {
            result.Add(new OperationalEcuStatistics
            {
                Componente = reader.GetString(0),
                OperationAttemptCount = reader.GetInt32(1),
                EffectiveOperationCount = reader.GetInt32(2),
                ConformingOperationCount = reader.GetInt32(3)
            });
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

    private readonly record struct FunctionalityEcuPair(string TestUid, string Functionality, string Componente);

    private sealed class PairAccumulator
    {
        public int TotalAttempts { get; set; }
        public int EffectiveOperations { get; set; }
        public int? AttemptsToFirstConforme { get; set; }
    }
}

public sealed class OperationalArchiveStatistics
{
    public int TestCount { get; set; }
    public int RevisionCount { get; set; }
    public int OperationAttemptCount { get; set; }
    public int EffectiveOperationCount { get; set; }
    public int TestedCount { get; set; }
    public int ConformingCount { get; set; }
    public double ConformityRatePercent => TestedCount == 0 ? 0 : 100.0 * ConformingCount / TestedCount;
}

public sealed class OperationalOperatorStatistics
{
    public string Operator { get; init; } = "";
    public int TestCount { get; init; }
    public int RevisionCount { get; init; }
    public int OperationAttemptCount { get; init; }
    public int EffectiveOperationCount { get; init; }
    public int ConformingRevisionCount { get; init; }
}

public sealed class OperationalFunctionalityStatistics
{
    public string Functionality { get; init; } = "";
    public int TestFunctionalityEcuPairs { get; init; }
    public int SuccessfulPairs { get; init; }
    public int NeverSuccessfulPairs { get; init; }
    public int TotalOperationAttempts { get; init; }
    public int TotalEffectiveOperations { get; init; }
    public double AverageAttemptsToFirstConforme { get; init; }
    public double NeverSuccessfulPercent => TestFunctionalityEcuPairs == 0 ? 0 : 100.0 * NeverSuccessfulPairs / TestFunctionalityEcuPairs;
}

public sealed class OperationalEcuStatistics
{
    public string Componente { get; init; } = "";
    public int OperationAttemptCount { get; init; }
    public int EffectiveOperationCount { get; init; }
    public int ConformingOperationCount { get; init; }
}