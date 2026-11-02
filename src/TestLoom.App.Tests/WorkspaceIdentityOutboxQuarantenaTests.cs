using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Cambio di identità della cartella condivisa (P1.2): gli indici locali si eliminano, ma l'outbox del record-journal
/// e i payload non ancora pubblicati (NC/CR/Segnalazioni) non devono mai essere cancellati: finiscono in una
/// cartella di quarantena datata dentro la cartella locale del workspace.
/// </summary>
public sealed class WorkspaceIdentityOutboxQuarantenaTests : IDisposable
{
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_IdentitaOutbox_" + Guid.NewGuid().ToString("N"));

    public WorkspaceIdentityOutboxQuarantenaTests()
    {
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_local)) Directory.Delete(_local, recursive: true); } catch { }
    }

    [Fact]
    public void CambioIdentita_ConOutboxPresente_OutboxEPayloadRestanoRecuperabili()
    {
        var outbox = Path.Combine(_local, "record-journal", "outbox.ndjson");
        var payload = Path.Combine(_local, "record-journal", "payloads", "NC", "evento-1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
        File.WriteAllText(outbox, "{\"EventId\":\"evento-1\"}\n");
        File.WriteAllText(payload, "{\"Uid\":\"nc-1\"}");
        File.WriteAllText(Path.Combine(_local, "index.db"), "indice");
        File.WriteAllText(Path.Combine(_local, "index.db-wal"), "wal");
        File.WriteAllText(Path.Combine(_local, "journal.db-shm"), "shm");
        var backup = Path.Combine(_local, WorkspaceLocalPaths.BackupDirectoryName, "TestLoom_snapshot.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, "backup");

        WorkspaceIdentityService.SvuotaIndiciLocali(_local);

        // Gli indici SQLite appartengono alla cartella precedente: vengono eliminati.
        Assert.False(File.Exists(Path.Combine(_local, "index.db")));
        Assert.False(File.Exists(Path.Combine(_local, "index.db-wal")));
        Assert.False(File.Exists(Path.Combine(_local, "journal.db-shm")));

        // L'outbox non e' piu' nel percorso attivo, ma resta intatto in una cartella di quarantena datata.
        Assert.False(File.Exists(outbox));
        var copieOutbox = Directory.GetFiles(_local, "outbox.ndjson", SearchOption.AllDirectories);
        Assert.Single(copieOutbox);
        Assert.Contains("quarantena-identita-", copieOutbox[0]);
        Assert.Equal("{\"EventId\":\"evento-1\"}\n", File.ReadAllText(copieOutbox[0]));
        var payloadSpostato = Path.Combine(Path.GetDirectoryName(copieOutbox[0])!, "payloads", "NC", "evento-1.json");
        Assert.Equal("{\"Uid\":\"nc-1\"}", File.ReadAllText(payloadSpostato));

        // I backup locali non vanno toccati.
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public void CambioIdentitaRipetuto_NonRiquarantenaLaQuarantenaPrecedente()
    {
        var outbox = Path.Combine(_local, "record-journal", "outbox.ndjson");
        Directory.CreateDirectory(Path.GetDirectoryName(outbox)!);
        File.WriteAllText(outbox, "{\"EventId\":\"evento-2\"}\n");

        WorkspaceIdentityService.SvuotaIndiciLocali(_local);
        WorkspaceIdentityService.SvuotaIndiciLocali(_local);

        var copie = Directory.GetFiles(_local, "outbox.ndjson", SearchOption.AllDirectories);
        Assert.Single(copie);
        Assert.Equal("{\"EventId\":\"evento-2\"}\n", File.ReadAllText(copie[0]));
    }
}
