using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class WorkflowTransactionPoisonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_TxPoison_" + Guid.NewGuid().ToString("N"));

    public WorkflowTransactionPoisonTests()
    {
        Directory.CreateDirectory(_root);
        new DataPaths(_root).EnsureStructure();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void CaughtMandatoryDeleteFailure_CannotBeTurnedIntoCommittedTransaction_OnWindows()
    {
        // Windows è la piattaforma reale di TestLoom e consente di forzare deterministicamente il failure tramite
        // FileShare.None. Su Unix la rimozione di un file aperto è consentita, quindi il caso non è riproducibile.
        if (!OperatingSystem.IsWindows()) return;

        var paths = new DataPaths(_root);
        var path = paths.NonConformitaFile("NC-TX-POISON-001");
        var original = new NonConformita { Uid = Guid.NewGuid(), Id = "NC-TX-POISON-001", RevisioneDati = 1 };
        JsonIo.WriteAtomic(path, original);
        var exact = File.ReadAllBytes(path);

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var tx = WorkflowTransactionService.Begin(paths, "poison-delete-test");

            Assert.ThrowsAny<IOException>(() => WorkflowTransactionService.DeleteFile(path));

            // Simula il vecchio repository che intercetta/logga la delete fallita e prova comunque a chiudere il save.
            Assert.Throws<IOException>(() => tx.Commit());
            Assert.True(File.Exists(path));
        }

        // Dopo Dispose la transazione ha eseguito rollback; il lock è ora chiuso e la before-state è leggibile.
        Assert.Equal(exact, File.ReadAllBytes(path));
    }
}