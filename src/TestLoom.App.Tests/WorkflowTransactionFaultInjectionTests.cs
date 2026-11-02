using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

public sealed class WorkflowTransactionFaultInjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_TxFault_" + Guid.NewGuid().ToString("N"));

    public WorkflowTransactionFaultInjectionTests()
    {
        Directory.CreateDirectory(_root);
        new DataPaths(_root).EnsureStructure();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // StampedRecord_WithoutReceipt_IsPendingAndNeverExposed, ReceiptArrivesBeforeAllMembers_..., e
    // StaleMemberAfterReceipt_... sono stati rimossi con la 6.4.2 (docs/PIANO_6.4.2.md §1): asserivano
    // esplicitamente il vecchio comportamento "Pending finché la receipt cross-file non si assesta" che è
    // stato eliminato — proprio quel comportamento produceva il difetto "file non leggibili" a PC singolo.

    [Fact]
    public void CommittedDelete_IsATombstoneAndCannotBeExposedAsDomainObject()
    {
        var paths = new DataPaths(_root);
        var path = paths.NonConformitaFile("NC-TX-004");
        var nc = new NonConformita { Uid = Guid.NewGuid(), Id = "NC-TX-004", RevisioneDati = 1 };
        JsonIo.WriteAtomic(path, nc);

        using (var tx = WorkflowTransactionService.Begin(paths, "fault-test-delete"))
        {
            WorkflowTransactionService.DeleteFile(path);
            tx.Commit();
        }

        Assert.True(File.Exists(path));
        Assert.True(WorkflowTransactionVisibilityService.IsCommittedTombstone(paths, path));
        Assert.Null(JsonIo.TryRead<NonConformita>(path, out var errore));
        Assert.Contains("Tombstone", errore);
        Assert.True(WorkflowTransactionVisibilityService.TryReadTombstoneIdentity(path, out var uid, out var id));
        Assert.Equal(nc.Uid, uid);
        Assert.Equal(nc.Id, id);
    }

    /// <summary>
    /// La Unit of Work locale (before-image/rollback) resta invariata dalla 6.4.2: solo lo stampaggio _ditmTx
    /// sui JSON di dominio ordinari è stato rimosso (WorkflowTransactionService.PrepareJsonWrite ora scrive i
    /// byte as-is), quindi una scrittura poi annullata torna byte-per-byte identica all'originale, senza più
    /// bisogno di ignorare metadati tecnici per il confronto.
    /// </summary>
    [Fact]
    public void DisposedUncommittedTransaction_RestoresExactBeforeImageAndPublishesNoReceipt()
    {
        var paths = new DataPaths(_root);
        var path = paths.NonConformitaFile("NC-TX-005");
        var original = new NonConformita { Uid = Guid.NewGuid(), Id = "NC-TX-005", RevisioneDati = 1 };
        JsonIo.WriteAtomic(path, original);
        var originalBytes = File.ReadAllBytes(path);
        Guid txId;

        using (var tx = WorkflowTransactionService.Begin(paths, "fault-test-rollback"))
        {
            txId = tx.Id;
            var changed = new NonConformita
            {
                Uid = original.Uid,
                Id = original.Id,
                RevisioneDati = 2,
                Descrizione = "non deve sopravvivere al rollback"
            };
            JsonIo.WriteAtomic(path, changed);
            Assert.NotEqual(originalBytes, File.ReadAllBytes(path));
            // Nessun Commit(): Dispose deve eseguire rollback.
        }

        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        Assert.NotNull(JsonIo.TryRead<NonConformita>(path, out _));
        Assert.False(WorkflowCommitLedgerService.TryGetReceipt(paths, DeviceIdentityService.GetOrCreate(), txId, out _, out _));
        var pendingDir = Path.Combine(paths.Root, "_ditm", "transactions");
        Assert.Empty(Directory.Exists(pendingDir)
            ? Directory.EnumerateFiles(pendingDir, $"{txId:N}.pending.json", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>());
    }

    [Fact]
    public void JournalPrepare_InsideTransaction_IsCanceledFromLocalOutboxOnRollback()
    {
        var paths = new DataPaths(_root);
        var local = Path.Combine(Path.GetTempPath(), "TestLoom_TxFaultJournal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        try
        {
            var journal = new SharedTestJournalService(paths, local, deviceIdOverride: "pc-journal");
            var uid = Guid.NewGuid();
            var before = new TestCase { Uid = uid, Id = "TC-JOURNAL-001", RevisioneDati = 4 };
            var after = new TestCase { Uid = uid, Id = "TC-JOURNAL-001", RevisioneDati = 5 };

            using (WorkflowTransactionService.Begin(paths, "fault-test-journal-rollback"))
            {
                _ = journal.Prepare(JournalOperation.Save, before, after, null, null);
                Assert.Equal(1, CountPendingJournalRows(journal.LocalDatabasePath));
                // Dispose senza Commit -> rollback -> callback Cancel(pending).
            }

            Assert.Equal(0, CountPendingJournalRows(journal.LocalDatabasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(local, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Decisione 6.4.2 (docs/PIANO_6.4.2.md §1): PrepareJsonWrite non stampa più _ditmTx sui JSON di dominio
    /// ordinari, quindi una riscrittura con payload equivalente dentro una WorkflowTransaction produce byte
    /// identici all'originale. DomainRecoveryService.Restore deve comunque riconoscere lo stato come già
    /// presente (EquivalentDomainPayload) senza bisogno di alcuna verifica di visibilità/receipt.
    /// </summary>
    [Fact]
    public void RecoveryEquivalence_CommittedEquivalentState_IsAlreadyRestored()
    {
        var paths = new DataPaths(_root);
        var path = paths.NonConformitaFile("NC-TX-006");
        var original = JsonSerializer.SerializeToUtf8Bytes(
            new NonConformita { Uid = Guid.NewGuid(), Id = "NC-TX-006", RevisioneDati = 5, Descrizione = "evidenza" },
            JsonIo.Options);
        File.WriteAllBytes(path, original);
        var recovery = DomainRecoveryService.ArchiveOnly(paths, path, DomainRecoveryCategory.NonConformita);

        WorkflowTransactionService.Execute(paths, "commit-equivalent-recovery-payload", () =>
            JsonIo.WriteBytesAtomic(path, original, overwrite: true));
        var committedEquivalent = File.ReadAllBytes(path);
        Assert.Equal(original, committedEquivalent);

        var result = DomainRecoveryService.Restore(paths, recovery.RecoveryId, "Master");

        Assert.True(result.AlreadyPresent);
        Assert.Equal(committedEquivalent, File.ReadAllBytes(path));
        var envelope = DomainRecoveryService.GetAll(paths).Single(x => x.RecoveryId == recovery.RecoveryId);
        Assert.NotNull(envelope.RestoredUtc);
    }

    // RecoveryEquivalence_PendingEquivalentState_IsNotAcceptedAsAlreadyRestored è stato rimosso con la 6.4.2:
    // asseriva che un payload equivalente ma "pending" (tx non ancora committed/assestata) venisse rifiutato dal
    // ripristino. Quella nozione di "pending" non esiste più: un JSON di dominio sintatticamente valido è
    // sempre leggibile e DomainRecoveryService.Restore decide solo in base a EquivalentDomainPayload.

    [Fact]
    public void TruncatedLedgerTail_DoesNotInvalidateAnEarlierCompleteReceipt()
    {
        var paths = new DataPaths(_root);
        var txId = Guid.NewGuid();
        const string deviceId = "pc-tail";
        var path = paths.NonConformitaFile("NC-TX-007");
        var bytes = WorkflowTransactionVisibilityService.StampJson(
            JsonSerializer.SerializeToUtf8Bytes(
                new NonConformita { Uid = Guid.NewGuid(), Id = "NC-TX-007", RevisioneDati = 2 },
                JsonIo.Options), txId, deviceId);
        File.WriteAllBytes(path, bytes);
        WorkflowCommitLedgerService.Publish(paths, Receipt(paths, txId, deviceId, (path, bytes, false)));

        var current = Path.Combine(paths.Root, "_ditm", "transactions", "committed", deviceId, "current.ndjson");
        File.AppendAllText(current, "{\"Id\":\"truncated", Encoding.UTF8);

        Assert.True(WorkflowCommitLedgerService.TryGetReceipt(paths, deviceId, txId, out var receipt, out var error));
        Assert.Null(error);
        Assert.Equal(txId, receipt!.Id);
    }

    [Fact]
    public void LedgerCompaction_PreservesOldReceiptsWithoutPerTransactionFiles()
    {
        var paths = new DataPaths(_root);
        const string deviceId = "pc-compact";
        Guid firstId = Guid.Empty;

        for (var i = 0; i < 6; i++)
        {
            var txId = Guid.NewGuid();
            if (i == 0) firstId = txId;
            var path = paths.NonConformitaFile($"NC-COMPACT-{i:D2}");
            var bytes = WorkflowTransactionVisibilityService.StampJson(
                JsonSerializer.SerializeToUtf8Bytes(
                    new NonConformita { Uid = Guid.NewGuid(), Id = $"NC-COMPACT-{i:D2}", RevisioneDati = i + 1 },
                    JsonIo.Options), txId, deviceId);
            File.WriteAllBytes(path, bytes);
            WorkflowCommitLedgerService.Publish(paths, Receipt(paths, txId, deviceId, (path, bytes, false)));
            WorkflowCommitLedgerService.Compact(paths, deviceId);
        }

        Assert.True(WorkflowCommitLedgerService.TryGetReceipt(paths, deviceId, firstId, out var first, out var error));
        Assert.Null(error);
        Assert.Equal(firstId, first!.Id);
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(paths.Root, "_ditm", "transactions", "committed"),
            "*.committed.json", SearchOption.TopDirectoryOnly));
    }

    private static long CountPendingJournalRows(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM journal_entry WHERE state = 0;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static WorkflowTransactionCommitReceipt Receipt(
        DataPaths paths,
        Guid id,
        string deviceId,
        params (string Path, byte[] Bytes, bool Deleted)[] entries)
    {
        return new WorkflowTransactionCommitReceipt
        {
            Id = id,
            StartedUtc = DateTime.UtcNow.AddSeconds(-1),
            CompletedUtc = DateTime.UtcNow,
            DeviceId = deviceId,
            Device = "test",
            Description = "fault-injection",
            Entries = entries.Select(e => new WorkflowTransactionCommitEntry
            {
                RelativePath = WorkflowTransactionVisibilityService.NormalizeRelativePath(paths.Root, e.Path),
                FinalSha256 = WorkflowTransactionVisibilityService.Hash(e.Bytes),
                LogicallyDeleted = e.Deleted
            }).ToList()
        };
    }
}