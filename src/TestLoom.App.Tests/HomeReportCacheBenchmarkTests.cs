using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TestLoom.App.Tests;

public sealed class HomeReportCacheBenchmarkTests : IDisposable
{
    private readonly string _dbPath;

    public HomeReportCacheBenchmarkTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ditm_test_homecache_{Guid.NewGuid():N}.db");
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE test_summary (
                uid TEXT PRIMARY KEY,
                eliminato INTEGER NOT NULL DEFAULT 0,
                data_creazione_ticks INTEGER NOT NULL,
                current_outcome INTEGER,
                first_conforme_revision INTEGER,
                revision_count INTEGER NOT NULL DEFAULT 1,
                origine_test INTEGER NOT NULL DEFAULT 0,
                executed_revision_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE test_vehicle (
                test_uid TEXT NOT NULL,
                value TEXT NOT NULL
            );

            CREATE TABLE test_revision (
                test_uid TEXT NOT NULL,
                revision_index INTEGER NOT NULL,
                data_ticks INTEGER NOT NULL,
                operatore TEXT NOT NULL,
                outcome INTEGER NOT NULL,
                raw_attempt_count INTEGER NOT NULL,
                origine INTEGER NOT NULL DEFAULT 0,
                unit_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE test_requirement (
                test_uid TEXT NOT NULL,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // Contratto dell'indice Test (P2.6): meta.revision incrementata dai trigger sulle tabelle lette dalle cache.
        using (var schemaMeta = connection.CreateCommand())
        {
            schemaMeta.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);";
            schemaMeta.ExecuteNonQuery();
        }
        using (var transazioneRevisione = connection.BeginTransaction())
        {
            LocalTestIndexStore.InstallaContatoreRevisione(connection, transazioneRevisione,
                new[] { "test_summary", "test_vehicle", "test_revision", "test_requirement" });
            transazioneRevisione.Commit();
        }

        using var tx = connection.BeginTransaction();
        for (int i = 0; i < 500; i++)
        {
            var uid = $"TEST-{i:D4}";
            using var insSum = connection.CreateCommand();
            insSum.Transaction = tx;
            insSum.CommandText = "INSERT INTO test_summary VALUES ($uid, 0, $ticks, $outcome, $firstConf, 1, 0, 1);";
            insSum.Parameters.AddWithValue("$uid", uid);
            insSum.Parameters.AddWithValue("$ticks", DateTime.UtcNow.Ticks);
            insSum.Parameters.AddWithValue("$outcome", (int)Esito.Conforme);
            insSum.Parameters.AddWithValue("$firstConf", 1);
            insSum.ExecuteNonQuery();

            using var insVeh = connection.CreateCommand();
            insVeh.Transaction = tx;
            insVeh.CommandText = "INSERT INTO test_vehicle VALUES ($uid, 'Daily');";
            insVeh.Parameters.AddWithValue("$uid", uid);
            insVeh.ExecuteNonQuery();

            using var insRev = connection.CreateCommand();
            insRev.Transaction = tx;
            insRev.CommandText = "INSERT INTO test_revision VALUES ($uid, 1, $ticks, 'Operatore1', $outcome, 1, 0, 1);";
            insRev.Parameters.AddWithValue("$uid", uid);
            insRev.Parameters.AddWithValue("$ticks", DateTime.UtcNow.Ticks);
            insRev.Parameters.AddWithValue("$outcome", (int)Esito.Conforme);
            insRev.ExecuteNonQuery();

            using var insReq = connection.CreateCommand();
            insReq.Transaction = tx;
            insReq.CommandText = "INSERT INTO test_requirement VALUES ($uid, 'REQ-001');";
            insReq.Parameters.AddWithValue("$uid", uid);
            insReq.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public void GetSnapshot_UsesGenerationTokenCache_OnSubsequentCalls()
    {
        var service = new LocalHomeDashboardQueryService(_dbPath, null);

        var sw1 = Stopwatch.StartNew();
        var snapshot1 = service.GetSnapshot(null, null);
        sw1.Stop();

        var sw2 = Stopwatch.StartNew();
        var snapshot2 = service.GetSnapshot(null, null);
        sw2.Stop();

        Assert.NotNull(snapshot1);
        Assert.NotNull(snapshot2);
        Assert.Equal(snapshot1.VehicleCounts["Daily"], snapshot2.VehicleCounts["Daily"]);
        Assert.Equal(snapshot1.OutcomeCounts[Esito.Conforme], snapshot2.OutcomeCounts[Esito.Conforme]);
        Assert.Equal(snapshot1.ExecutionCountsByOperator["Operatore1"], snapshot2.ExecutionCountsByOperator["Operatore1"]);

        // Cached call must be significantly faster or equal ticks
        Assert.True(sw2.ElapsedTicks <= sw1.ElapsedTicks);
    }

    [Fact]
    public void GetRequirementCoverage_UsesCache_ForIdenticalParameters()
    {
        var service = new LocalHomeDashboardQueryService(_dbPath, null);
        var requisiti = new List<Requisito> { new Requisito { Id = "REQ-001", Archiviata = false } };

        var sw1 = Stopwatch.StartNew();
        var bucket1 = service.GetRequirementCoverage(requisiti, null, null);
        sw1.Stop();

        var sw2 = Stopwatch.StartNew();
        var bucket2 = service.GetRequirementCoverage(requisiti, null, null);
        sw2.Stop();

        Assert.Equal(bucket1.Conformi, bucket2.Conformi);
        Assert.True(sw2.ElapsedTicks <= sw1.ElapsedTicks);
    }

    /// <summary>Il catalogo Requisiti e' JSON, non fa parte del PRAGMA data_version dell'indice SQLite Test:
    /// editare Tipo/Sottoveicoli Extra di un Requisito senza alcuna scrittura sull'archivio Test deve comunque
    /// invalidare la cache di GetRequirementCoverage (RequisitiCoverageCacheKey, condiviso con
    /// LocalTestArchiveQueryService.GetHomeAggiornaBatch) — altrimenti la dashboard mostrerebbe la copertura
    /// del tipo/ambito precedente. Il fixture ha 500 Test Conforme, tutti sul veicolo "Daily" e collegati a
    /// REQ-001: passando REQ-001 a Extra limitato a un sottoveicolo diverso, nessun Test lo copre più.</summary>
    [Fact]
    public void GetRequirementCoverage_RequisitoEditatoDaStandardAD4D_NonRiusaLaCacheDelPrecedenteTipo()
    {
        var service = new LocalHomeDashboardQueryService(_dbPath, null);
        var requisito = new Requisito { Id = "REQ-001", Archiviata = false, ApplicabileBanco = true, ApplicabileVeicolo = true };

        var standard = service.GetRequirementCoverage(new List<Requisito> { requisito }, null, null);
        Assert.Equal(1, standard.Conformi);
        Assert.Equal(0, standard.NonTestati);

        RequisitoApplicabilita.ImpostaTipo(requisito, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(requisito, ["Alpha"]);
        var extra = service.GetRequirementCoverage(new List<Requisito> { requisito }, null, null);

        Assert.Equal(0, extra.Conformi);
        Assert.Equal(1, extra.NonTestati);
    }

    /// <summary>Riproduce la causa del costo di ~1s misurato alla PRIMA rivisita della Home dopo l'avvio
    /// (piano 6.4.0, punto 3): AppState.EseguiManutenzioneStorage(force:true) parte in background alla
    /// costruzione di AppState, in corsa con la primissima Home già mostrata dal bootstrap. Diagnosi isolata
    /// sperimentalmente (2026-09-26, poi confermata dai due test sotto): PRAGMA wal_checkpoint(TRUNCATE) su un
    /// WAL vuoto NON fa avanzare PRAGMA data_version, ma PRAGMA optimize; SI (prima ANALYZE della sessione:
    /// sqlite_stat1 non esiste ancora) — un falso positivo per le cache di copertura Home, che usano data_version
    /// come chiave.
    /// Qui si dimostra che LocalSqliteMaintenanceService.OptimizeWorkspace(..., ottimizzaStatistiche: false),
    /// come ora chiamato SOLO dalla manutenzione di avvio, non fa avanzare data_version anche sul primo giro
    /// (nessuna ANALYZE precedente), mentre il default true (manutenzione periodica/esplicita, invariata) lo fa
    /// ancora — cosi' un'invalidazione legittima dovuta a scritture reali resta possibile altrove, non aggirata.</summary>
    [Fact]
    public void OptimizeWorkspace_ManutenzioneAvvio_NonFaAvanzareDataVersion_SenzaScrittureDiDominio()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ditm_test_ws_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var isolatoPath = Path.Combine(dir, "indice.db");
        SqliteConnection.ClearAllPools();
        File.Copy(_dbPath, isolatoPath);
        try
        {
            var prima = ReadDataVersion(isolatoPath);
            LocalSqliteMaintenanceService.OptimizeWorkspace(dir, vacuum: false, ottimizzaStatistiche: false);
            var dopo = ReadDataVersion(isolatoPath);
            Assert.Equal(prima, dopo);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>Controprova: con ottimizzaStatistiche:true (comportamento invariato per manutenzione periodica
    /// ed esplicita) PRAGMA optimize continua a girare e, sul primo ANALYZE della sessione, avanza davvero
    /// data_version — la correzione non ha semplicemente reso il controllo a vuoto per ogni caso.</summary>
    [Fact]
    public void OptimizeWorkspace_ManutenzionePeriodicaOEsplicita_ContinuaAdEseguireOptimize()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ditm_test_ws_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var isolatoPath = Path.Combine(dir, "indice.db");
        SqliteConnection.ClearAllPools();
        File.Copy(_dbPath, isolatoPath);
        try
        {
            var prima = ReadDataVersion(isolatoPath);
            LocalSqliteMaintenanceService.OptimizeWorkspace(dir, vacuum: false, ottimizzaStatistiche: true);
            var dopo = ReadDataVersion(isolatoPath);
            Assert.NotEqual(prima, dopo);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>Test end-to-end sul percorso reale che la Home usa: la cache di copertura popolata alla
    /// primissima visita (durante il bootstrap) deve restare valida alla prima rivisita anche se, nel
    /// frattempo, e' corsa la manutenzione di avvio — stesso scenario di AppState, senza istanziare AppState
    /// per intero (che richiederebbe un intero workspace condiviso).</summary>
    [Fact]
    public void GetRequirementCoverage_DopoManutenzioneAvvio_RiusaLaCacheDellaPrimaVisita()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ditm_test_ws_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var isolatoPath = Path.Combine(dir, "indice.db");
        SqliteConnection.ClearAllPools();
        File.Copy(_dbPath, isolatoPath);
        try
        {
            // Stessa istanza di servizio della Home (campo di HomeViewModel, vive fra una rivisita e l'altra):
            // qui simuliamo esattamente "prima visita mostrata dal bootstrap" -> "manutenzione di avvio in
            // background" -> "prima rivisita".
            var service = new LocalHomeDashboardQueryService(isolatoPath, null);
            var requisiti = new List<Requisito> { new Requisito { Id = "REQ-001", Archiviata = false } };
            var primaVisita = service.GetRequirementCoverage(requisiti, null, null);

            LocalSqliteMaintenanceService.OptimizeWorkspace(dir, vacuum: false, ottimizzaStatistiche: false);

            var sw = Stopwatch.StartNew();
            var primaRivisita = service.GetRequirementCoverage(requisiti, null, null);
            sw.Stop();

            Assert.Equal(primaVisita.Conformi, primaRivisita.Conformi);
            // Cache hit: la seconda chiamata non deve fare query SQL, quindi resta rapida come nel test
            // "UsesCache_ForIdenticalParameters" già presente in questo file (stessa soglia).
            Assert.True(sw.ElapsedTicks <= Stopwatch.Frequency / 20); // sotto i 50ms, larghissimo margine
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>Regressione (diagnosi 2026-09-26, task home-primo-ritorno): i due test sopra copiano SOLO
    /// `.db`, senza `-wal`/`-shm` — il fixture li produce con `SqliteConnection` in modalita' rollback-journal
    /// predefinita (nessun `PRAGMA journal_mode=WAL` nel costruttore di questa classe), quindi non avevano MAI
    /// esercitato `PRAGMA wal_checkpoint(TRUNCATE)` su un WAL realmente non vuoto. L'indice reale
    /// (LocalTestIndexStore) apre invece sempre in `journal_mode=WAL`: dopo il bootstrap/RefreshIndex il WAL
    /// puo' contenere frame non ancora svuotati quando la manutenzione di avvio (Task.Run in AppState,
    /// ottimizzaStatistiche:false) girava. PRIMA della correzione, con quel WAL non vuoto, il checkpoint
    /// (allora eseguito SEMPRE, indipendentemente dal flag) faceva avanzare PRAGMA data_version esattamente come
    /// PRAGMA optimize — falso positivo non coperto dai due test sopra, causa concreta del costo di ~1,5s
    /// misurato con FlaUI alla prima rivisita della Home. Ora checkpoint e optimize condividono lo stesso gate
    /// (ottimizzaStatistiche): con false, nessuno dei due deve avanzare data_version, nemmeno con un WAL
    /// realmente pieno.</summary>
    [Fact]
    public void OptimizeWorkspace_ManutenzioneAvvio_WalNonVuoto_NonFaAvanzareDataVersion()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ditm_test_ws_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var walPath = Path.Combine(dir, "indice.db");
        SqliteConnection.ClearAllPools();
        try
        {
            // Connessione "sentinella" che resta aperta per l'intero test: SQLite esegue un checkpoint
            // automatico quando l'ULTIMA connessione al file si chiude, il che nasconderebbe il fenomeno reale.
            // Nell'app vera, mentre la manutenzione di avvio gira, l'indice ha sempre almeno un'altra connessione
            // viva (pool di Microsoft.Data.Sqlite, reader concorrenti) — qui la riproduciamo esplicitamente.
            using var sentinella = new SqliteConnection($"Data Source={walPath}");
            sentinella.Open();
            using (var pragma = sentinella.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                pragma.ExecuteNonQuery();
            }
            using (var create = sentinella.CreateCommand())
            {
                create.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);";
                create.ExecuteNonQuery();
            }
            using (var setup = new SqliteConnection($"Data Source={walPath}"))
            {
                setup.Open();
                // Scrittura reale che resta nel WAL: nessun checkpoint esplicito prima di chiudere, come nel
                // caso reale in cui la connessione dell'indice resta aperta/riusata e il checkpoint arriva solo
                // dalla manutenzione, non dal chiamante che ha scritto.
                using (var insert = setup.CreateCommand())
                {
                    insert.CommandText = "INSERT INTO t (v) VALUES ('x');";
                    insert.ExecuteNonQuery();
                }
            }

            Assert.True(File.Exists(walPath + "-wal"), "Precondizione: deve esistere un file -wal con frame pendenti.");

            var prima = ReadDataVersion(walPath);
            LocalSqliteMaintenanceService.OptimizeWorkspace(dir, vacuum: false, ottimizzaStatistiche: false);
            var dopo = ReadDataVersion(walPath);

            Assert.Equal(prima, dopo);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static long ReadDataVersion(string path)
    {
        using var c = new SqliteConnection($"Data Source={path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA data_version;";
        return (long)cmd.ExecuteScalar()!;
    }
}
