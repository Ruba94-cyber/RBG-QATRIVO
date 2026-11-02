using TestLoom.App.Models;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>Filtro della pagina guasto: i campi sono applicati anche all'export.</summary>
public sealed record FiltroDtcRegistro(
    string Testo = "",
    string? Veicolo = null,
    PassoDtc? Passo = null,
    bool SoloProblemi = false,
    bool SoloSenzaRequisito = false);

/// <summary>Una riga effettiva del registro guasto, collegata alla revisione e all'operazione nel Test autorevole.</summary>
public sealed record RigaDtcRegistro(
    string TestUid,
    string TestId,
    string Requisito,
    string Guasto,
    string Componente,
    PassoDtc Passo,
    Esito Esito,
    string Veicolo,
    DateTime Data,
    int RevisioneIndice,
    int NumeroRevisione,
    int TentativoIndice,
    string Operatore,
    string CondizioneUid)
{
    public string EsitoTesto => Esito.Label();
    /// <summary>Associazione a un requisito Extra: "Associato" se la riga ha un requisito, altrimenti "Da associare".</summary>
    public string StatoAssociazioneTesto => LocalizationService.Instance.Traduci(string.IsNullOrWhiteSpace(Requisito) ? "DtcRegistroDaAssociare" : "DtcRegistroAssociato");
    public string RequisitoVisualizzato => string.IsNullOrWhiteSpace(Requisito)
        ? LocalizationService.Instance.Traduci("DtcRegistroSenzaRequisito") : Requisito;
    public string VeicoloVisualizzato => string.IsNullOrWhiteSpace(Veicolo)
        ? LocalizationService.Instance.Traduci("DtcRegistroVeicoloNonSpecificato") : Veicolo;
    public string EcuVisualizzata => string.IsNullOrWhiteSpace(Componente)
        ? LocalizationService.Instance.Traduci("DtcRegistroEcuNonSpecificata") : Componente;
    public string PassoTesto => Passo == PassoDtc.Validazione
        ? LocalizationService.Instance.Traduci("DtcRegistroValidazione")
        : LocalizationService.Instance.Traduci("DtcRegistroDevalidazione");
    public string DataTesto => Data.ToString("dd/MM/yyyy HH:mm");
    public bool HaProblemi => Esito is Esito.NonConforme or Esito.ConformeConDeviazioni;
}

public sealed record PaginaDtcRegistro(IReadOnlyList<RigaDtcRegistro> Righe, long Totale);

/// <summary>
/// Read side paginato del registro guasto. SQLite resta una proiezione locale ricostruibile dai Test; le note,
/// deviazioni e i problemi si leggono dal Test autorevole solo quando si apre il dettaglio o si esporta.
/// Viene mostrata l'ultima condizione effettiva di ogni operazione logica in ogni revisione, anche quando
/// l'esito è ancora DaEseguire o N.A., così il registro espone anche il lavoro pianificato.
/// </summary>
public sealed class LocalDtcRegistroQueryService
{
    private const int BusyTimeoutMs = 500;
    private readonly string _connectionString;

    private const string SelectRows = """
        SELECT d.test_uid, s.id, d.requisito, d.guasto,
               COALESCE(oe.componente,
                   CASE WHEN (SELECT COUNT(*) FROM test_ecu te WHERE te.test_uid = d.test_uid) = 1
                        THEN (SELECT MIN(te.value) FROM test_ecu te WHERE te.test_uid = d.test_uid)
                        ELSE '' END) AS componente,
               d.passo, d.outcome, d.veicolo, d.data_ticks, d.revision_index,
               r.iteration_number, d.attempt_index, r.operatore, oa.condition_uid
        FROM test_dtc_operation d
        JOIN test_summary s ON s.uid = d.test_uid AND s.eliminato = 0
        JOIN test_revision r ON r.test_uid = d.test_uid AND r.revision_index = d.revision_index
        LEFT JOIN test_operation_ecu oe ON oe.test_uid = d.test_uid
            AND oe.revision_index = d.revision_index AND oe.attempt_index = d.attempt_index
        LEFT JOIN test_operation_attempt oa ON oa.test_uid = d.test_uid
            AND oa.revision_index = d.revision_index AND oa.attempt_index = d.attempt_index
        WHERE d.is_effective = 1 {FILTER}
        """;

    public LocalDtcRegistroQueryService(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = true
        }.ToString();
    }

    public IReadOnlyList<string> LeggiVeicoli(CancellationToken ct = default)
    {
        using var connection = Apri();
        using var snapshot = new LocalIndexSnapshot(connection);
        using var command = connection.CreateCommand();
        command.Transaction = snapshot.Transazione;
        command.CommandText = """
            SELECT DISTINCT d.veicolo COLLATE NOCASE
            FROM test_dtc_operation d
            JOIN test_summary s ON s.uid = d.test_uid AND s.eliminato = 0
            WHERE d.is_effective = 1 AND d.veicolo <> ''
            ORDER BY d.veicolo COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            result.Add(reader.GetString(0));
        }
        return result;
    }

    public PaginaDtcRegistro LeggiPagina(FiltroDtcRegistro filtro, int pagina, int dimensionePagina, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        pagina = Math.Max(1, pagina);
        dimensionePagina = Math.Clamp(dimensionePagina, 1, 1000);
        using var connection = Apri();
        using var snapshot = new LocalIndexSnapshot(connection);

        var where = CreaFiltro(filtro);
        using var count = connection.CreateCommand();
        count.Transaction = snapshot.Transazione;
        count.CommandText = $"SELECT COUNT(*) FROM ({SelectRows.Replace("{FILTER}", where.Sql)}) AS rows;";
        AggiungiParametri(count, where, filtro);
        var totale = Convert.ToInt64(count.ExecuteScalar());
        ct.ThrowIfCancellationRequested();

        using var command = connection.CreateCommand();
        command.Transaction = snapshot.Transazione;
        command.CommandText = SelectRows.Replace("{FILTER}", where.Sql)
            + " ORDER BY d.data_ticks DESC, s.id COLLATE NOCASE, d.revision_index DESC, d.attempt_index DESC, componente COLLATE NOCASE LIMIT $limit OFFSET $offset;";
        AggiungiParametri(command, where, filtro);
        command.Parameters.AddWithValue("$limit", dimensionePagina);
        command.Parameters.AddWithValue("$offset", checked((long)(pagina - 1) * dimensionePagina));
        return new PaginaDtcRegistro(EnumeraRighe(command, ct).ToList(), totale);
    }

    /// <summary>Stream completo per l'Excel, con lo stesso filtro della pagina e senza caricare l'archivio Test in RAM.</summary>
    public IEnumerable<RigaDtcRegistro> StreamRighe(FiltroDtcRegistro filtro, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        using var connection = Apri();
        using var snapshot = new LocalIndexSnapshot(connection);
        var where = CreaFiltro(filtro);
        using var command = connection.CreateCommand();
        command.Transaction = snapshot.Transazione;
        command.CommandText = SelectRows.Replace("{FILTER}", where.Sql)
            + " ORDER BY d.data_ticks DESC, s.id COLLATE NOCASE, d.revision_index DESC, d.attempt_index DESC, componente COLLATE NOCASE;";
        AggiungiParametri(command, where, filtro);
        foreach (var row in EnumeraRighe(command, ct)) yield return row;
    }

    private SqliteConnection Apri()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA query_only=ON; PRAGMA cache_size=-65536;";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static (string Sql, bool HasText, bool HasVehicle, bool HasStep, bool HasProblems, bool HasUnlinked) CreaFiltro(FiltroDtcRegistro f)
    {
        var conditions = new List<string>();
        var hasText = !string.IsNullOrWhiteSpace(f.Testo);
        var hasVehicle = !string.IsNullOrWhiteSpace(f.Veicolo);
        var hasStep = f.Passo.HasValue;
        if (hasText) conditions.Add("(instr(upper(d.guasto), upper($text)) > 0 OR instr(upper(d.requisito), upper($text)) > 0 OR instr(upper(COALESCE(oe.componente, '')), upper($text)) > 0 OR (NOT EXISTS (SELECT 1 FROM test_operation_ecu search_oe WHERE search_oe.test_uid = d.test_uid AND search_oe.revision_index = d.revision_index AND search_oe.attempt_index = d.attempt_index) AND (SELECT COUNT(*) FROM test_ecu search_count WHERE search_count.test_uid = d.test_uid) = 1 AND EXISTS (SELECT 1 FROM test_ecu search_te WHERE search_te.test_uid = d.test_uid AND instr(upper(search_te.value), upper($text)) > 0)) OR instr(upper(d.veicolo), upper($text)) > 0 OR instr(upper(s.id), upper($text)) > 0)");
        if (hasVehicle) conditions.Add("d.veicolo = $vehicle COLLATE NOCASE");
        if (hasStep) conditions.Add("d.passo = $step");
        if (f.SoloProblemi) conditions.Add("d.outcome IN (2, 3)");
        if (f.SoloSenzaRequisito) conditions.Add("d.requisito = ''");
        return (conditions.Count == 0 ? "" : "AND " + string.Join(" AND ", conditions), hasText, hasVehicle, hasStep, f.SoloProblemi, f.SoloSenzaRequisito);
    }

    private static void AggiungiParametri(SqliteCommand command,
        (string Sql, bool HasText, bool HasVehicle, bool HasStep, bool HasProblems, bool HasUnlinked) filtroSql,
        FiltroDtcRegistro filtro)
    {
        if (filtroSql.HasText) command.Parameters.AddWithValue("$text", filtro.Testo.Trim());
        if (filtroSql.HasVehicle) command.Parameters.AddWithValue("$vehicle", filtro.Veicolo!.Trim());
        if (filtroSql.HasStep) command.Parameters.AddWithValue("$step", (int)filtro.Passo!.Value);
    }

    private static IEnumerable<RigaDtcRegistro> EnumeraRighe(SqliteCommand command, CancellationToken ct)
    {
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            yield return new RigaDtcRegistro(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                (PassoDtc)reader.GetInt32(5), (Esito)reader.GetInt32(6), reader.GetString(7),
                new DateTime(reader.GetInt64(8), DateTimeKind.Utc).ToLocalTime(), reader.GetInt32(9),
                reader.GetInt32(10), reader.GetInt32(11), reader.GetString(12), reader.IsDBNull(13) ? "" : reader.GetString(13));
        }
    }
}
