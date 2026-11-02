using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Proiezioni della Home a scala (schema v14): Veicoli × esito dalla proiezione test_vehicle (solo periodo Home) e Test
/// senza componente dalla colonna test_summary.senza_ecu. Il risultato nuovo e' confrontato con le query originali, copiate qui
/// nello stesso testo in uso prima della v14. Dati misti: grafie diverse dello stesso veicolo (NOCASE), esiti e date dentro e
/// fuori dal periodo, Test eliminati e importati, Componenti mancanti, vuote o solo spazi, riscritture dello stesso Test.
/// </summary>
public sealed class LocalHomeProiezioniTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_HomeProiezioni_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Giorno = new(2026, 8, 1, 8, 0, 0);
    private static readonly DateTime DaHome = new(2026, 1, 1);
    private static readonly DateTime AHome = new(2026, 12, 31, 23, 59, 59);
    private static readonly string[] Veicoli = { "Golf", "golf", "GOLF", "Veicolo-0001", "veicolo-0001", "VEICOLO-0001", "Daily", "daily", "Ú-x", "ú-X" };

    // Query Veicoli × esito come in GetSnapshot prima della v14: periodo Home, nessun filtro dimensionale.
    private const string SqlVeicoliOriginale = """
        SELECT v.value, COALESCE(s.current_outcome, $notExecuted), COUNT(*)
        FROM test_vehicle v
        JOIN test_summary s ON s.uid = v.test_uid AND s.eliminato = 0 AND s.origine_test = 0 AND s.data_creazione_ticks >= $homeDateFrom AND s.data_creazione_ticks < $homeDateTo
        GROUP BY v.value COLLATE NOCASE, COALESCE(s.current_outcome, $notExecuted)
        ORDER BY v.value COLLATE NOCASE, COALESCE(s.current_outcome, $notExecuted);
        """;

    // Stessa query senza periodo e con il solo estremo inferiore: non passano dalla proiezione.
    private const string SqlVeicoliOriginaleSenzaPeriodo = """
        SELECT v.value, COALESCE(s.current_outcome, $notExecuted), COUNT(*)
        FROM test_vehicle v
        JOIN test_summary s ON s.uid = v.test_uid AND s.eliminato = 0 AND s.origine_test = 0
        GROUP BY v.value COLLATE NOCASE, COALESCE(s.current_outcome, $notExecuted)
        ORDER BY v.value COLLATE NOCASE, COALESCE(s.current_outcome, $notExecuted);
        """;

    private const string SqlVeicoliOriginaleDaSolo = """
        SELECT v.value, COALESCE(s.current_outcome, $notExecuted), COUNT(*)
        FROM test_vehicle v
        JOIN test_summary s ON s.uid = v.test_uid AND s.eliminato = 0 AND s.origine_test = 0 AND s.data_creazione_ticks >= $homeDateFrom
        GROUP BY v.value COLLATE NOCASE, COALESCE(s.current_outcome, $notExecuted)
        ORDER BY v.value COLLATE NOCASE, COALESCE(s.current_outcome, $notExecuted);
        """;

    // Stesso periodo con filtro Veicolo (dimensionale): resta sulla query originale.
    private const string SqlVeicoliOriginaleConVeicolo = """
        SELECT v.value, COALESCE(s.current_outcome, $notExecuted), COUNT(*)
        FROM test_vehicle v
        JOIN test_summary s ON s.uid = v.test_uid AND s.eliminato = 0 AND s.origine_test = 0
            AND EXISTS (SELECT 1 FROM test_vehicle x WHERE x.test_uid = s.uid AND x.value COLLATE NOCASE IN ($homeVehicle0))
            AND s.data_creazione_ticks >= $homeDateFrom AND s.data_creazione_ticks < $homeDateTo
        GROUP BY v.value COLLATE NOCASE, COALESCE(s.current_outcome, $notExecuted)
        ORDER BY v.value COLLATE NOCASE, COALESCE(s.current_outcome, $notExecuted);
        """;

    // Test senza componente come in GetHomeSummaryCore prima della v14 (NOT EXISTS su test_ecu, includeDeleted = false).
    private const string SqlSenzaEcuOriginale = """
        SELECT COALESCE(SUM(CASE WHEN NOT EXISTS (SELECT 1 FROM test_ecu e WHERE e.test_uid = s.uid) THEN 1 ELSE 0 END), 0)
        FROM test_summary s
        WHERE origine_test = 0 AND eliminato = 0;
        """;

    [Fact]
    public void VeicoliEsito_PeriodoHome_UgualeAllaQueryOriginale_ConGrafieMiste_EDopoRiscritture()
    {
        var store = NuovoStore("misto");
        Popola(store, 600, seme: 3);
        Riscrivi(store, 150, seme: 4);
        var db = store.DatabasePath;
        var filtro = new DashboardFilter { DataDa = DaHome, DataA = AHome };

        var servizio = new LocalHomeDashboardQueryService(db);
        var snapshot = servizio.GetSnapshot(filtro);
        var attesi = VeicoliOriginali(db, SqlVeicoliOriginale, Ticks(DaHome), TicksA(AHome));

        Assert.True(attesi.Count > 10, "Il dataset deve produrre piu' gruppi di veicolo ed esito.");
        Assert.Equal(attesi, snapshot.VehicleOutcomeRows);
        Assert.Equal(ContaPerVeicolo(attesi).ToList(), snapshot.VehicleCounts.Select(kv => (kv.Key, kv.Value)).ToList());
        Assert.Equal(1, servizio.VeicoliDaProiezione);

        // Output con indici e statistiche come dopo PRAGMA optimize (ANALYZE): la grafia non cambia col piano.
        Sql(db, "ANALYZE;");
        var dopoAnalyze = new LocalHomeDashboardQueryService(db).GetSnapshot(filtro);
        Assert.Equal(attesi, dopoAnalyze.VehicleOutcomeRows);
    }

    [Fact]
    public void VeicoliEsito_DataAConGiornoScelto_IncludeITestCreatiNelGiorno()
    {
        // "Data a" dal selettore arriva a mezzanotte: un Test creato alle 15:30 del giorno scelto deve restare nel periodo.
        var store = NuovoStore("data-a-giorno");
        var tc = new TestCase
        {
            Id = "TC-GIORNO",
            Uid = Guid.Parse("00000001-0000-4000-8000-00000000d002"),
            RevisioneDati = 1,
            DataCreazione = new DateTime(2026, 8, 1, 15, 30, 0),
            UltimaModifica = Giorno,
            ModificatoDa = "Mario",
            CreatoDa = "Mario",
            StatoLavoro = StatoLavoroTest.Concluso,
            DescrizioneBreve = "Test del giorno",
            Descrizione = "Test del giorno",
            Origine = OrigineTest.TestLoom,
            Veicoli = ["Golf"],
        };
        Salva(store, tc);

        var snapshot = new LocalHomeDashboardQueryService(store.DatabasePath)
            .GetSnapshot(new DashboardFilter { DataDa = new DateTime(2026, 8, 1), DataA = new DateTime(2026, 8, 1) });

        var riga = Assert.Single(snapshot.VehicleOutcomeRows);
        Assert.Equal("Golf", riga.Item1);
        Assert.Equal(1L, riga.Item3);
    }

    [Fact]
    public void VeicoliEsito_SenzaPeriodoOConUnSoloEstremo_RestaSullaQueryOriginale()
    {
        var store = NuovoStore("senzaPeriodo");
        Popola(store, 300, seme: 5);
        var db = store.DatabasePath;

        var senzaPeriodo = new LocalHomeDashboardQueryService(db);
        Assert.Equal(VeicoliOriginali(db, SqlVeicoliOriginaleSenzaPeriodo, null, null), senzaPeriodo.GetSnapshot(new DashboardFilter()).VehicleOutcomeRows);
        Assert.Equal(0, senzaPeriodo.VeicoliDaProiezione);

        var soloDa = new LocalHomeDashboardQueryService(db);
        var filtroSoloDa = new DashboardFilter { DataDa = DaHome };
        Assert.Equal(VeicoliOriginali(db, SqlVeicoliOriginaleDaSolo, Ticks(DaHome), null), soloDa.GetSnapshot(filtroSoloDa).VehicleOutcomeRows);
        Assert.Equal(0, soloDa.VeicoliDaProiezione);
    }

    [Fact]
    public void VeicoliEsito_FiltroDimensionale_RestaSullaQueryOriginale()
    {
        var store = NuovoStore("dimensionale");
        Popola(store, 400, seme: 6);
        var db = store.DatabasePath;
        var filtro = new DashboardFilter { DataDa = DaHome, DataA = AHome, Veicolo = ["golf"] };

        var servizio = new LocalHomeDashboardQueryService(db);
        var ottenuti = servizio.GetSnapshot(filtro).VehicleOutcomeRows;
        Assert.Equal(VeicoliOriginali(db, SqlVeicoliOriginaleConVeicolo, Ticks(DaHome), TicksA(AHome), veicolo: "golf"), ottenuti);
        Assert.Equal(0, servizio.VeicoliDaProiezione);
        Assert.NotEmpty(ottenuti);
    }

    [Fact]
    public void ProiezioneVeicoli_RestaAllineataAiTestDopoRiscritture()
    {
        var store = NuovoStore("allineamento");
        Popola(store, 400, seme: 7);
        Riscrivi(store, 200, seme: 8);
        var db = store.DatabasePath;

        // Ogni riga di test_vehicle copia esattamente i campi di test_summary del suo Test (eliminato, origine, data, esito).
        Assert.Equal(0L, Convert.ToInt64(Scalare(db, """
            SELECT COUNT(*) FROM test_vehicle v JOIN test_summary s ON s.uid = v.test_uid
            WHERE v.eliminato <> s.eliminato OR v.origine_test <> s.origine_test
               OR v.data_creazione_ticks <> s.data_creazione_ticks OR v.esito_home <> COALESCE(s.current_outcome, 5);
            """)));
        Assert.True(Convert.ToInt64(Scalare(db, "SELECT COUNT(*) FROM test_vehicle;")) > 400);
    }

    [Fact]
    public void Sommario_SenzaEcu_UgualeAllaQueryOriginale_ESenzaEcuDiTestSummaryCoerente()
    {
        var store = NuovoStore("senzaEcu");
        Popola(store, 500, seme: 9);
        Riscrivi(store, 180, seme: 10);
        var db = store.DatabasePath;

        var attesi = Convert.ToInt64(Scalare(db, SqlSenzaEcuOriginale));
        Assert.True(attesi > 0 && attesi < 500, "Il dataset deve contenere Test con e senza componente.");
        Assert.Equal(attesi, new LocalTestArchiveQueryService(db).GetHomeSummary(null).WithoutEcu);

        // La colonna senza_ecu coincide con la sottoquery per ogni Test, eliminati e importati compresi.
        Assert.Equal(0L, Convert.ToInt64(Scalare(db, """
            SELECT COUNT(*) FROM test_summary s
            WHERE s.senza_ecu <> (CASE WHEN NOT EXISTS (SELECT 1 FROM test_ecu e WHERE e.test_uid = s.uid) THEN 1 ELSE 0 END);
            """)));
    }

    [Fact]
    public void Indice_MigrazioneV13ToV14_RicostruisceLeProiezioniDaiDatiLocali()
    {
        var store = NuovoStore("migrazione");
        Popola(store, 300, seme: 11);
        Riscrivi(store, 80, seme: 12);
        var db = store.DatabasePath;
        var attesiVeicoli = VeicoliOriginali(db, SqlVeicoliOriginale, Ticks(DaHome), TicksA(AHome));
        var attesiSenzaEcu = Convert.ToInt64(Scalare(db, SqlSenzaEcuOriginale));

        // Indice v13 reale: niente indice ne' colonne della v14, versione 13.
        Sql(db, """
            DROP INDEX IF EXISTS ix_test_vehicle_home;
            ALTER TABLE test_vehicle DROP COLUMN eliminato;
            ALTER TABLE test_vehicle DROP COLUMN origine_test;
            ALTER TABLE test_vehicle DROP COLUMN data_creazione_ticks;
            ALTER TABLE test_vehicle DROP COLUMN esito_home;
            ALTER TABLE test_summary DROP COLUMN senza_ecu;
            UPDATE meta SET value = '13' WHERE key = 'schema_version';
            """);

        var riaperto = NuovoStore("migrazione");
        Assert.Equal(LocalTestIndexStore.CurrentSchemaVersion.ToString(), Scalare(db, "SELECT value FROM meta WHERE key='schema_version';"));
        Assert.Equal(1L, Convert.ToInt64(Scalare(db, "SELECT COUNT(*) FROM pragma_index_list('test_vehicle') WHERE name = 'ix_test_vehicle_home';")));
        Assert.Equal(4L, Convert.ToInt64(Scalare(db, "SELECT COUNT(*) FROM pragma_table_info('test_vehicle') WHERE name IN ('eliminato','origine_test','data_creazione_ticks','esito_home');")));

        var servizio = new LocalHomeDashboardQueryService(riaperto.DatabasePath);
        Assert.Equal(attesiVeicoli, servizio.GetSnapshot(new DashboardFilter { DataDa = DaHome, DataA = AHome }).VehicleOutcomeRows);
        Assert.Equal(1, servizio.VeicoliDaProiezione);
        Assert.Equal(attesiSenzaEcu, new LocalTestArchiveQueryService(db).GetHomeSummary(null).WithoutEcu);

        // Idempotenza: forzare di nuovo la migrazione non cambia le proiezioni.
        Sql(db, "UPDATE meta SET value = '13' WHERE key = 'schema_version';");
        _ = NuovoStore("migrazione");
        Assert.Equal(attesiVeicoli, new LocalHomeDashboardQueryService(db).GetSnapshot(new DashboardFilter { DataDa = DaHome, DataA = AHome }).VehicleOutcomeRows);
        Assert.Equal(attesiSenzaEcu, new LocalTestArchiveQueryService(db).GetHomeSummary(null).WithoutEcu);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    private LocalTestIndexStore NuovoStore(string nome) =>
        new(Path.Combine(_root, nome, "data"), Path.Combine(_root, nome, "cache"));

    private static void Popola(LocalTestIndexStore store, int n, int seme)
    {
        var rnd = new Random(seme);
        for (var i = 0; i < n; i++) Salva(store, Crea(i, rnd));
    }

    /// <summary>Riscrive alcuni Test con lo stesso Uid e lo stesso percorso: nuovi veicoli, esiti, componente ed eliminazioni.</summary>
    private static void Riscrivi(LocalTestIndexStore store, int quanti, int seme)
    {
        var rnd = new Random(seme);
        for (var k = 0; k < quanti; k++)
        {
            var i = rnd.Next(quanti * 2);
            Salva(store, Crea(i, rnd));
        }
    }

    private static void Salva(LocalTestIndexStore store, TestCase tc) =>
        store.Upsert(Path.Combine(Path.GetTempPath(), "TestLoom_HomeProiezioni_json", tc.Id + ".json"), Giorno, 100, tc);

    private static TestCase Crea(int i, Random rnd)
    {
        var tc = new TestCase
        {
            Id = $"TC-{i:D4}",
            Uid = Guid.Parse($"{i + 1:x8}-0000-4000-8000-00000000d001"),
            RevisioneDati = 1,
            DataCreazione = DataVariabile(rnd.Next(4)),
            UltimaModifica = Giorno,
            ModificatoDa = "Mario",
            CreatoDa = "Mario",
            StatoLavoro = StatoLavoroTest.Concluso,
            DescrizioneBreve = $"Test {i}",
            Descrizione = $"Test {i}",
            Eliminato = rnd.Next(10) == 0,
            Origine = rnd.Next(10) == 0 ? OrigineTest.ImportStatoUdt : OrigineTest.TestLoom,
        };
        tc.Componente = EcuVariabile(rnd.Next(4));
        tc.Veicoli = Enumerable.Range(0, 1 + rnd.Next(3)).Select(_ => Veicoli[rnd.Next(Veicoli.Length)]).ToList();
        var esiti = new[] { Esito.Conforme, Esito.NonConforme, Esito.ConformeConDeviazioni, Esito.NonApplicabile };
        var revisioni = rnd.Next(3);
        for (var k = 0; k < revisioni; k++)
        {
            var esito = esiti[rnd.Next(esiti.Length)];
            tc.Esecuzioni.Add(new Esecuzione
            {
                Iterazione = k + 1,
                Data = Giorno.AddDays(k),
                Operatore = "Mario",
                Esito = esito,
                Condizioni = [new Condizione { Esito = esito }],
            });
        }
        return tc;
    }

    private static DateTime DataVariabile(int k) => k switch
    {
        0 => Giorno,
        1 => new DateTime(2026, 3, 5, 9, 0, 0),
        2 => new DateTime(2025, 12, 31, 23, 0, 0),
        _ => new DateTime(2027, 1, 1, 0, 0, 0),
    };

    private static EcuSelection EcuVariabile(int k)
    {
        var componente = new EcuSelection();
        switch (k)
        {
            case 1: componente.Add("   "); break;
            case 2: componente.Add("Componente-01"); break;
            case 3: componente.Add("Componente-01"); componente.Add("componente-01"); componente.Add(" componente-02 "); break;
        }
        return componente;
    }

    private static long Ticks(DateTime d) => d.ToUniversalTime().Ticks;

    // "Data a" inclusiva come in produzione: il limite superiore e' l'inizio del giorno successivo, in UTC.
    private static long TicksA(DateTime dataA) => Ticks(DataAInclusiva.FineEsclusa(dataA));

    private static IEnumerable<(string Nome, long Valore)> ContaPerVeicolo(IEnumerable<(string Nome, Esito Esito, long Valore)> righe)
    {
        // Stessa regola di GetSnapshot: la chiave del conteggio e' la prima grafia incontrata (dizionario OrdinalIgnoreCase).
        var conteggi = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (nome, _, valore) in righe) conteggi[nome] = conteggi.GetValueOrDefault(nome) + valore;
        return conteggi.Select(kv => (kv.Key, kv.Value));
    }

    private static List<(string Nome, Esito Esito, long Valore)> VeicoliOriginali(string db, string sql, long? da, long? a, string? veicolo = null)
    {
        using var connection = Apri(db);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$notExecuted", (int)Esito.NonEseguitoNA);
        if (da.HasValue) command.Parameters.AddWithValue("$homeDateFrom", da.Value);
        if (a.HasValue) command.Parameters.AddWithValue("$homeDateTo", a.Value);
        if (veicolo != null) command.Parameters.AddWithValue("$homeVehicle0", veicolo);
        var righe = new List<(string Nome, Esito Esito, long Valore)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) righe.Add((reader.GetString(0), (Esito)reader.GetInt32(1), reader.GetInt64(2)));
        return righe;
    }

    private static SqliteConnection Apri(string db)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Scalare(string db, string sql)
    {
        using var connection = Apri(db);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Sql(string db, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
