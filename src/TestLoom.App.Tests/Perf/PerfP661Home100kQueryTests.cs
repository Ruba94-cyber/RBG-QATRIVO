using System.Diagnostics;
using System.Globalization;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace TestLoom.App.Tests.Perf;

/// <summary>
/// Banco ciclo prestazioni 6.6.1, primo ingresso della Home a scala (20k e 100k Test): query "Veicoli × esito" del
/// dashboard con il periodo predefinito della Home (anno corrente) e sommario batch (Test senza componente). Il dataset passa
/// dal writer reale (<see cref="LocalTestIndexStore.Upsert"/>), senza file JSON. Le SQL originali sono copiate qui
/// nello stesso testo di LocalHomeDashboardQueryService / LocalTestArchiveQueryService e misurate nello stesso processo
/// rispetto al percorso di produzione: campioni alternati, GC stabilizzato prima di ogni misura. Parita': il risultato
/// della SQL originale deve coincidere con quello del percorso di produzione.
///
/// Attivo solo con TestLoom_PERF_P661_HOME100K=1. TestLoom_PERF_MEDIA_SCALE limita la scala, TestLoom_PERF_P661_LABEL etichetta l'output.
/// Output: .ai/artifacts/perf/p661-home100k-{etichetta}-{N}k.tsv nella radice del repository (non versionata).
/// </summary>
[Collection("Scale performance")]
[Trait("Category", "Benchmark")]
public sealed class PerfP661Home100kQueryTests
{
    private const int Riscaldamenti = 2;
    private const int CampioniMisurati = 9;

    // Testo della query Veicoli × esito come in LocalHomeDashboardQueryService.GetSnapshot (periodo Home, nessun altro filtro).
    private const string SqlVeicoliEsitoOriginale = """
        SELECT v.value, COALESCE(s.current_outcome, $notExecuted), COUNT(*)
        FROM test_vehicle v
        JOIN test_summary s ON s.uid = v.test_uid AND s.eliminato = 0 AND s.origine_test = 0 AND s.data_creazione_ticks >= $homeDateFrom AND s.data_creazione_ticks < $homeDateTo
        GROUP BY v.value COLLATE NOCASE, COALESCE(s.current_outcome, $notExecuted)
        ORDER BY v.value COLLATE NOCASE, COALESCE(s.current_outcome, $notExecuted);
        """;

    // Testo della query Veicoli × esito come in LocalHomeDashboardQueryService (proiezione test_vehicle, solo periodo Home).
    private const string SqlVeicoliNuova = """
        SELECT v.value, v.esito_home, COUNT(*)
        FROM test_vehicle v INDEXED BY ix_test_vehicle_home
        WHERE v.eliminato = 0 AND v.origine_test = 0
            AND v.data_creazione_ticks >= $homeDateFrom AND v.data_creazione_ticks < $homeDateTo
        GROUP BY v.value COLLATE NOCASE, v.esito_home
        ORDER BY v.value COLLATE NOCASE, v.esito_home;
        """;

    // Testo del sommario come in LocalTestArchiveQueryService.GetHomeSummaryCore (includeDeleted = false).
    private const string SqlSommarioOriginale = """
        SELECT
            COUNT(*),
            COALESCE(SUM(CASE WHEN stato_lavoro = $daRitestare THEN 1 ELSE 0 END), 0),
            COALESCE(SUM(CASE WHEN assegnatario = $operatore AND stato_lavoro IN ($pianificato,$inCorso,$daRivedere,$inAttesa,$daRitestare) THEN 1 ELSE 0 END), 0),
            COALESCE(SUM(CASE WHEN current_outcome IS NOT NULL AND executed_revision_count > 0 THEN 1 ELSE 0 END), 0),
            COALESCE(SUM(CASE WHEN current_outcome = $conforme AND executed_revision_count > 0 THEN 1 ELSE 0 END), 0),
            COALESCE(SUM(CASE WHEN NOT EXISTS (SELECT 1 FROM test_ecu e WHERE e.test_uid = s.uid) THEN 1 ELSE 0 END), 0)
        FROM test_summary s
        WHERE origine_test = 0 AND eliminato = 0;
        """;

    private readonly ITestOutputHelper _output;
    public PerfP661Home100kQueryTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(20_000)]
    [InlineData(100_000)]
    public void MisuraVeicoliEsitoESommarioAScala(int scala)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_P661_HOME100K"), "1", StringComparison.Ordinal)) return;
        var scalaSolo = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_SCALE");
        if (!string.IsNullOrWhiteSpace(scalaSolo) && scalaSolo != scala.ToString(CultureInfo.InvariantCulture)) return;
        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_P661_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";

        var radice = TrovaRadiceRepo() ?? Path.GetTempPath();
        var root = Path.Combine(radice, ".ai", "tmp", "perf-p661-home100k", $"{scala}_{Guid.NewGuid():N}");
        try
        {
            var store = new LocalTestIndexStore(Path.Combine(root, "data"), Path.Combine(root, "cache"));
            var swDataset = Stopwatch.StartNew();
            GeneraTest(store, Path.Combine(root, "tc"), scala);
            var durataDataset = swDataset.Elapsed.TotalMilliseconds;
            var dbPath = store.DatabasePath;
            SqliteConnection.ClearAllPools();
            _output.WriteLine($"DATASET|scala={scala}|durata_ms={durataDataset:F0}");

            var filtro = new DashboardFilter { DataDa = new DateTime(2026, 1, 1), DataA = new DateTime(2026, 12, 31, 23, 59, 59) };
            var ticksDa = filtro.DataDa!.Value.ToUniversalTime().Ticks;
            var ticksA = DataAInclusiva.FineEsclusa(filtro.DataA!.Value).ToUniversalTime().Ticks;

            // Parita' prima della misura: la SQL originale copiata deve dare lo stesso risultato del percorso di produzione.
            var copiaVeicoli = EseguiVeicoliOriginale(dbPath, ticksDa, ticksA);
            var prodVeicoli = new LocalHomeDashboardQueryService(dbPath).GetSnapshot(filtro).VehicleOutcomeRows.ToList();
            var paritaVeicoli = copiaVeicoli.SequenceEqual(prodVeicoli);
            _output.WriteLine($"PARITY|veicoli_esito|scala={scala}|righe={copiaVeicoli.Count}|uguali={paritaVeicoli}");
            var copiaNuova = EseguiVeicoliNuova(dbPath, ticksDa, ticksA);
            var paritaNuova = copiaNuova.SequenceEqual(prodVeicoli);
            _output.WriteLine($"PARITY|veicoli_esito_nuova|scala={scala}|righe={copiaNuova.Count}|uguali={paritaNuova}");

            const string operatore = "Operatore-Bench";
            var copiaSommario = EseguiSommarioOriginale(dbPath, operatore);
            var prodSommario = new LocalTestArchiveQueryService(dbPath).GetHomeSummary(operatore);
            var paritaSommario = copiaSommario.Equals((prodSommario.TotalTests, prodSommario.ToRetest, prodSommario.MyActive,
                prodSommario.Tested, prodSommario.Conforming, prodSommario.WithoutEcu));
            _output.WriteLine($"PARITY|sommario|scala={scala}|totale={prodSommario.TotalTests}|senza_ecu={prodSommario.WithoutEcu}|uguali={paritaSommario}");

            var misure = new Dictionary<string, List<double>>
            {
                ["veicoli_sql_originale"] = new(),
                ["veicoli_sql_nuova"] = new(),
                ["snapshot_produzione"] = new(),
                ["sommario_sql_originale"] = new(),
                ["sommario_produzione"] = new(),
            };
            for (var giro = 0; giro < Riscaldamenti + CampioniMisurati; giro++)
            {
                Misura(misure["veicoli_sql_originale"], giro, () => EseguiVeicoliOriginale(dbPath, ticksDa, ticksA));
                Misura(misure["veicoli_sql_nuova"], giro, () => EseguiVeicoliNuova(dbPath, ticksDa, ticksA));
                Misura(misure["snapshot_produzione"], giro, () => new LocalHomeDashboardQueryService(dbPath).GetSnapshot(filtro));
                Misura(misure["sommario_sql_originale"], giro, () => EseguiSommarioOriginale(dbPath, operatore));
                Misura(misure["sommario_produzione"], giro, () => new LocalTestArchiveQueryService(dbPath).GetHomeSummary(operatore));
            }

            var cartella = Path.Combine(radice, ".ai", "artifacts", "perf");
            Directory.CreateDirectory(cartella);
            var tsv = new List<string> { "percorso\tscala\tmediana_ms\tmin_ms\tmax_ms\tcampioni_ms" };
            foreach (var (percorso, campioni) in misure)
            {
                var ordinati = campioni.OrderBy(x => x).ToList();
                var mediana = ordinati[ordinati.Count / 2];
                var riga = $"BENCH|scala={scala}|percorso={percorso}|mediana_ms={Fmt(mediana)}|min_ms={Fmt(ordinati[0])}|max_ms={Fmt(ordinati[^1])}|campioni={string.Join(",", campioni.Select(Fmt))}";
                _output.WriteLine(riga);
                tsv.Add($"{percorso}\t{scala}\t{Fmt(mediana)}\t{Fmt(ordinati[0])}\t{Fmt(ordinati[^1])}\t{string.Join(";", campioni.Select(Fmt))}");
            }

            // Upgrade one-shot (solo con TestLoom_PERF_P661_HOME100K_MIGRAZIONE=1): il workspace torna allo schema v13 (senza colonne
            // e indice della v14) e la riapertura esegue la migrazione reale. Misura il costo del primo avvio dopo l'aggiornamento.
            if (string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_P661_HOME100K_MIGRAZIONE"), "1", StringComparison.Ordinal))
            {
                EseguiSql(dbPath, """
                    DROP INDEX IF EXISTS ix_test_vehicle_home;
                    ALTER TABLE test_vehicle DROP COLUMN eliminato;
                    ALTER TABLE test_vehicle DROP COLUMN origine_test;
                    ALTER TABLE test_vehicle DROP COLUMN data_creazione_ticks;
                    ALTER TABLE test_vehicle DROP COLUMN esito_home;
                    ALTER TABLE test_summary DROP COLUMN senza_ecu;
                    UPDATE meta SET value = '13' WHERE key = 'schema_version';
                    """);
                SqliteConnection.ClearAllPools();
                var swMigrazione = Stopwatch.StartNew();
                _ = new LocalTestIndexStore(Path.Combine(root, "data"), Path.Combine(root, "cache"));
                var durataMigrazione = swMigrazione.Elapsed.TotalMilliseconds;
                _output.WriteLine($"BENCH|scala={scala}|percorso=migrazione_v13_v14|durata_ms={Fmt(durataMigrazione)}");
                tsv.Add($"migrazione_v13_v14\t{scala}\t{Fmt(durataMigrazione)}\t{Fmt(durataMigrazione)}\t{Fmt(durataMigrazione)}\t{Fmt(durataMigrazione)}");
            }
            File.WriteAllLines(Path.Combine(cartella, $"p661-home100k-{etichetta}-{scala / 1000}k.tsv"), tsv);

            Assert.True(paritaVeicoli, "Veicoli × esito: la SQL originale copiata non coincide con il percorso di produzione.");
            Assert.True(paritaNuova, "Veicoli × esito: la SQL nuova copiata non coincide con il percorso di produzione.");
            Assert.True(paritaSommario, "Sommario: la SQL originale copiata non coincide con il percorso di produzione.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Misura(List<double> campioni, int giro, Action operazione)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var sw = Stopwatch.StartNew();
        operazione();
        sw.Stop();
        if (giro >= Riscaldamenti) campioni.Add(sw.Elapsed.TotalMilliseconds);
    }

    private static List<(string Nome, Esito Esito, long Valore)> EseguiVeicoliOriginale(string dbPath, long ticksDa, long ticksA)
    {
        using var connection = Apri(dbPath);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SqlVeicoliEsitoOriginale;
        command.Parameters.AddWithValue("$notExecuted", (int)Esito.NonEseguitoNA);
        command.Parameters.AddWithValue("$homeDateFrom", ticksDa);
        command.Parameters.AddWithValue("$homeDateTo", ticksA);
        var righe = new List<(string Nome, Esito Esito, long Valore)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) righe.Add((reader.GetString(0), (Esito)reader.GetInt32(1), reader.GetInt64(2)));
        return righe;
    }

    private static List<(string Nome, Esito Esito, long Valore)> EseguiVeicoliNuova(string dbPath, long ticksDa, long ticksA)
    {
        using var connection = Apri(dbPath);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SqlVeicoliNuova;
        command.Parameters.AddWithValue("$homeDateFrom", ticksDa);
        command.Parameters.AddWithValue("$homeDateTo", ticksA);
        var righe = new List<(string Nome, Esito Esito, long Valore)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) righe.Add((reader.GetString(0), (Esito)reader.GetInt32(1), reader.GetInt64(2)));
        return righe;
    }

    private static void EseguiSql(string dbPath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Fmt(double valore) => valore.ToString("F1", CultureInfo.InvariantCulture);

    private static (long TotalTests, long ToRetest, long MyActive, long Tested, long Conforming, long WithoutEcu) EseguiSommarioOriginale(string dbPath, string operatore)
    {
        using var connection = Apri(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = SqlSommarioOriginale;
        command.Parameters.AddWithValue("$operatore", operatore);
        command.Parameters.AddWithValue("$pianificato", (int)StatoLavoroTest.Pianificato);
        command.Parameters.AddWithValue("$inCorso", (int)StatoLavoroTest.InCorso);
        command.Parameters.AddWithValue("$daRivedere", (int)StatoLavoroTest.DaRivedere);
        command.Parameters.AddWithValue("$inAttesa", (int)StatoLavoroTest.InAttesa);
        command.Parameters.AddWithValue("$daRitestare", (int)StatoLavoroTest.DaRitestare);
        command.Parameters.AddWithValue("$conforme", (int)Esito.Conforme);
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
    }

    /// <summary>Stessa apertura del percorso di produzione (sola lettura, cache privata, pragma di LocalHomeDashboardQueryService.Open).</summary>
    private static SqliteConnection Apri(string dbPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = true
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=500; PRAGMA query_only=ON; PRAGMA cache_size=-131072; PRAGMA mmap_size=536870912;";
        command.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Dataset deterministico: Test con data identica (come il gate) e sparse nel 2026, veicolo con due grafie
    /// (NOCASE), componente mancante o solo spazi, Test eliminati e importati (esclusi dai conteggi della Home).</summary>
    private static void GeneraTest(LocalTestIndexStore store, string cartella, int n)
    {
        var giornoFisso = new DateTime(2026, 8, 1, 8, 0, 0);
        for (var i = 0; i < n; i++)
        {
            var veicolo = $"Veicolo-{i % 1000:D4}";
            if (i % 20 == 7) veicolo = veicolo.ToLowerInvariant();
            var componente = new EcuSelection();
            if (i % 10 != 3) componente.Add(i % 17 == 5 ? "   " : $"Componente-{i % 1000:D4}");
            var id = $"TC-{i:D6}";
            var tc = new TestCase
            {
                Id = id,
                Uid = Guid.Parse($"{i + 1:x8}-0000-4000-8000-00000000c001"),
                RevisioneDati = 1,
                DataCreazione = i % 2 == 0 ? giornoFisso : new DateTime(2026, 1, 1).AddDays(i % 300).AddHours(8),
                UltimaModifica = giornoFisso.AddHours(4),
                ModificatoDa = "Operatore-Bench",
                CreatoDa = "Operatore-Bench",
                Assegnatario = i % 5 == 0 ? "Operatore-Bench" : null,
                Componente = componente,
                StatoLavoro = i % 3 == 0 ? StatoLavoroTest.DaRitestare : StatoLavoroTest.Concluso,
                DescrizioneBreve = $"Scenario {i % 137}",
                Descrizione = $"Verifica scenario {i % 137}",
                Eliminato = i % 100 == 7,
                Origine = i % 100 == 9 ? OrigineTest.ImportStatoUdt : OrigineTest.TestLoom,
                Veicolo = i % 3 == 0 ? veicolo : "",
            };
            if (i % 3 != 0) tc.Veicoli = new List<string> { veicolo };
            store.Upsert(Path.Combine(cartella, $"testcase_{id}.json"), giornoFisso, 100, tc);
        }
    }

    private static string? TrovaRadiceRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TestLoom.slnx"))) return dir.FullName;
        return null;
    }
}
