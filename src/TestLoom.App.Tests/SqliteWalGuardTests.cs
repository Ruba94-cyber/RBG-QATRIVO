using Microsoft.Data.Sqlite;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>SqliteWalGuard (feedback utente 2026-09-17: "nessuno perda dati... l'app dovrà essere perfetta ed
/// essere pronta al deploy") — dopo un'interruzione brutale del processo a metà di una grande transazione, il
/// WAL può restare enorme e rallentare le operazioni successive. Un checkpoint TRUNCATE proattivo all'apertura
/// lo ripulisce senza mai perdere dati già commessi.</summary>
public sealed class SqliteWalGuardTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "TestLoom_WalGuard_" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { }
    }

    private SqliteConnection Apri()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE IF NOT EXISTS t(v TEXT);";
        pragma.ExecuteNonQuery();
        return connection;
    }

    [Fact]
    public void CheckpointIfOversized_ConWalPiccolo_NonFaNulla()
    {
        using var connection = Apri();
        using (var insert = connection.CreateCommand()) { insert.CommandText = "INSERT INTO t VALUES ('riga');"; insert.ExecuteNonQuery(); }

        // Non deve lanciare né alterare il contenuto per un WAL sotto soglia.
        SqliteWalGuard.CheckpointIfOversized(connection, _dbPath);

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM t;";
        Assert.Equal(1L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public void CheckpointIfOversized_ConWalSopraSoglia_LoTronca_SenzaPerdereIDatiCommessi()
    {
        using (var writer = Apri())
        {
            using var insert = writer.CreateCommand();
            insert.CommandText = "INSERT INTO t VALUES ($v);";
            var p = insert.Parameters.Add("$v", SqliteType.Text);
            insert.Prepare();
            // Righe abbastanza grandi/numerose da far crescere il WAL oltre la soglia (16 MiB) senza mai
            // committare esplicitamente un checkpoint: journal_mode=WAL con synchronous di default continua
            // ad accumulare nel -wal finché non arriva un checkpoint (automatico o esplicito).
            var valoreGrande = new string('x', 64 * 1024);
            using var tx = writer.BeginTransaction();
            insert.Transaction = tx;
            for (var i = 0; i < 400; i++) { p.Value = valoreGrande; insert.ExecuteNonQuery(); }
            tx.Commit();
        }

        var walPath = _dbPath + "-wal";
        Assert.True(File.Exists(walPath) && new FileInfo(walPath).Length > 16L * 1024 * 1024,
            $"Precondizione del test non soddisfatta: WAL troppo piccolo ({(File.Exists(walPath) ? new FileInfo(walPath).Length : 0)} byte).");

        using var connection = Apri();
        SqliteWalGuard.CheckpointIfOversized(connection, _dbPath);

        Assert.True(new FileInfo(walPath).Length < 1024 * 1024, "Il WAL doveva essere troncato dal checkpoint.");
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM t;";
        Assert.Equal(400L, (long)count.ExecuteScalar()!);
    }
}
