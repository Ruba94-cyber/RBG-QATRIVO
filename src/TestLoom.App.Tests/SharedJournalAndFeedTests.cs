using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class SharedJournalAndFeedTests
{
    [Fact]
    [Trait("Category", "Scale")]
    public void ManySaves_KeepSharedFileCountBounded()
    {
        var root = Temp("TestLoom_JournalShared");
        var local = Temp("TestLoom_JournalLocal");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var journal = new SharedTestJournalService(paths, local, "device-a", rotateAfterBytesOverride: 512, completedSegmentsToKeepOverride: 2);
            var path = Path.Combine(paths.TestCasesDir, "testcase_T-1.json");
            TestCase? previous = null;

            for (var i = 1; i <= 5_000; i++)
            {
                var current = NewTest("T-1", previous?.Uid ?? Guid.NewGuid(), i, $"rev-{i}");
                JsonIo.WriteAtomic(path, current);
                var pending = journal.Prepare(previous == null ? JournalOperation.Create : JournalOperation.Save,
                    previous, current, path, path);
                Assert.True(journal.TryPublish(pending));
                previous = Clone(current);
            }

            var dir = Path.Combine(root, "_ditm", "journal", "device-a");
            var files = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly);
            Assert.True(files.Count(f => Path.GetFileName(f).StartsWith("segment-", StringComparison.OrdinalIgnoreCase)) <= 2);
            Assert.True(files.Length <= 4, $"File infrastrutturali inattesi: {string.Join(", ", files.Select(Path.GetFileName))}");
            Assert.True(File.Exists(Path.Combine(dir, "checkpoint.json.gz")));
        }
        finally { Delete(root); Delete(local); }
    }

    [Fact]
    public void ChangeFeed_AfterBaselineReturnsOnlyNewChangedTestPaths()
    {
        var root = Temp("TestLoom_FeedShared");
        var local = Temp("TestLoom_FeedLocal");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var index = new LocalTestIndexStore(root, local);
            var journal = new SharedTestJournalService(paths, index.WorkspaceDirectory, "device-a", 4096, 2);
            var testPath = Path.Combine(paths.TestCasesDir, "testcase_T-2.json");

            var v1 = NewTest("T-2", Guid.NewGuid(), 1, "v1");
            JsonIo.WriteAtomic(testPath, v1);
            var info = new FileInfo(testPath);
            index.Upsert(testPath, info.LastWriteTimeUtc, info.Length, v1);
            index.ReconcileFull(new[] { new LocalIndexWriteEntry(testPath, info.LastWriteTimeUtc, info.Length, v1) }, DateTime.UtcNow);
            Assert.True(journal.TryPublish(journal.Prepare(JournalOperation.Create, null, v1, null, testPath)));

            var feed = new JournalChangeFeedService(paths, index.WorkspaceDirectory);
            Assert.True(feed.EstablishBaseline());
            Assert.True(feed.HasBaseline);

            var v2 = NewTest("T-2", v1.Uid, 2, "v2");
            JsonIo.WriteAtomic(testPath, v2);
            Assert.True(journal.TryPublish(journal.Prepare(JournalOperation.Save, v1, v2, testPath, testPath)));

            var batch = feed.Poll();
            Assert.False(batch.RequiresFullReconciliation);
            Assert.Contains(batch.Paths, p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(testPath), StringComparison.OrdinalIgnoreCase));
            Assert.Single(batch.Events);

            feed.Ack(batch);
            var second = feed.Poll();
            Assert.Empty(second.Events);
            Assert.Empty(second.Paths);
        }
        finally { Delete(root); Delete(local); }
    }

    [Fact]
    public void TwoDevices_FromSameBase_DetectForkAndPublishRecoveryOnlyForConflict()
    {
        var root = Temp("TestLoom_ForkShared");
        var localA = Temp("TestLoom_ForkA");
        var localB = Temp("TestLoom_ForkB");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var uid = Guid.NewGuid();
            var baseVersion = NewTest("T-3", uid, 1, "base");
            var branchA = NewTest("T-3", uid, 2, "A");
            var branchB = NewTest("T-3", uid, 2, "B");
            var testPath = Path.Combine(paths.TestCasesDir, "testcase_T-3.json");

            var a = new SharedTestJournalService(paths, localA, "device-a", 4096, 2);
            var b = new SharedTestJournalService(paths, localB, "device-b", 4096, 2);
            Assert.True(a.TryPublish(a.Prepare(JournalOperation.Save, baseVersion, branchA, testPath, testPath)));
            Assert.True(b.TryPublish(b.Prepare(JournalOperation.Save, baseVersion, branchB, testPath, testPath)));

            var forksA = a.RefreshKnownForks();
            var forksB = b.RefreshKnownForks();
            var fork = Assert.Single(forksA);
            Assert.Equal(uid, fork.EntityUid);
            Assert.Equal(2, fork.Branches.Count);
            Assert.Single(forksB);

            var conflictDir = Path.Combine(root, "_ditm", "conflicts", uid.ToString("N"));
            Assert.True(Directory.Exists(conflictDir));
            Assert.Equal(2, Directory.GetFiles(conflictDir, "*.json.gz", SearchOption.TopDirectoryOnly).Length);

            // Normal journal infrastructure stays bounded; full snapshots exist only because a real fork exists.
            Assert.False(Directory.Exists(Path.Combine(root, "_ditm", "conflicts", Guid.NewGuid().ToString("N"))));
        }
        finally { Delete(root); Delete(localA); Delete(localB); }
    }

    [Fact]
    public void TryPublish_ForeignPendingTransaction_LeavesEventOnlyInLocalOutbox()
    {
        var root = Temp("TestLoom_JournalReadOnly");
        var local = Temp("TestLoom_JournalReadOnlyLocal");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var journal = new SharedTestJournalService(paths, local, "device-readonly", 4096, 2);
            var uid = Guid.NewGuid();
            var test = NewTest("T-RO", uid, 1, "pending locale");
            var testPath = Path.Combine(paths.TestCasesDir, "testcase_T-RO.json");
            var pending = journal.Prepare(JournalOperation.Save, null, test, testPath, testPath);

            var transactions = Path.Combine(root, "_ditm", "transactions");
            Directory.CreateDirectory(transactions);
            File.WriteAllText(Path.Combine(transactions, Guid.NewGuid().ToString("N") + ".pending.json"), "{broken");

            Assert.False(journal.TryPublish(pending));
            Assert.False(Directory.Exists(Path.Combine(root, "_ditm", "journal", "device-readonly")));

            // La barriera rimossa simula il ritorno operativo: lo stesso pending deve essere ancora
            // pubblicabile, prova che il fallimento read-only non ha perso la outbox locale.
            Directory.Delete(transactions, recursive: true);
            Assert.True(journal.TryPublish(pending));
            Assert.True(File.Exists(Path.Combine(root, "_ditm", "journal", "device-readonly", "current.ndjson")));
        }
        finally { Delete(root); Delete(local); }
    }

    [Fact]
    public void SeparateReplicas_DelayedJournalDelivery_PreservesBothBranchesAfterRestart()
    {
        var replicaA = Temp("TestLoom_ReplicaA");
        var replicaB = Temp("TestLoom_ReplicaB");
        var localA = Temp("TestLoom_ReplicaLocalA");
        var localB = Temp("TestLoom_ReplicaLocalB");
        try
        {
            var pathsA = new DataPaths(replicaA);
            var pathsB = new DataPaths(replicaB);
            pathsA.EnsureStructure();
            pathsB.EnsureStructure();
            var uid = Guid.NewGuid();
            var original = NewTest("T-DELAY", uid, 1, "base");
            var branchA = NewTest("T-DELAY", uid, 2, "modifica PC A");
            var branchB = NewTest("T-DELAY", uid, 2, "modifica PC B");
            var pathA = Path.Combine(pathsA.TestCasesDir, "testcase_T-DELAY.json");
            var pathB = Path.Combine(pathsB.TestCasesDir, "testcase_T-DELAY.json");
            var a = new SharedTestJournalService(pathsA, localA, "device-a", 4096, 2);
            var b = new SharedTestJournalService(pathsB, localB, "device-b", 4096, 2);
            Assert.True(a.TryPublish(a.Prepare(JournalOperation.Save, original, branchA, pathA, pathA)));
            Assert.True(b.TryPublish(b.Prepare(JournalOperation.Save, original, branchB, pathB, pathB)));
            Assert.Empty(a.RefreshKnownForks());
            Assert.Empty(b.RefreshKnownForks());

            // Each PC has committed independently before the other device's journal arrives.
            DeliverDeviceJournal(replicaA, replicaB, "device-a");
            DeliverDeviceJournal(replicaB, replicaA, "device-b");
            a = new SharedTestJournalService(pathsA, localA, "device-a", 4096, 2);
            b = new SharedTestJournalService(pathsB, localB, "device-b", 4096, 2);
            Assert.Equal(2, Assert.Single(a.RefreshKnownForks()).Branches.Count);
            Assert.Equal(2, Assert.Single(b.RefreshKnownForks()).Branches.Count);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(replicaA, "_ditm", "conflicts", uid.ToString("N")), "*.json.gz"));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(replicaB, "_ditm", "conflicts", uid.ToString("N")), "*.json.gz"));
            // Duplicate delivery must not manufacture a third branch.
            DeliverDeviceJournal(replicaA, replicaB, "device-a");
            Assert.Equal(2, Assert.Single(b.RefreshKnownForks()).Branches.Count);
        }
        finally { Delete(replicaA); Delete(replicaB); Delete(localA); Delete(localB); }
    }

    private static void DeliverDeviceJournal(string source, string destination, string device)
    {
        var sourceDirectory = Path.Combine(source, "_ditm", "journal", device);
        var destinationDirectory = Path.Combine(destination, "_ditm", "journal", device);
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.GetFiles(sourceDirectory))
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: true);
    }
    private static TestCase NewTest(string id, Guid uid, long revision, string description) => new()
    {
        Id = id,
        Uid = uid,
        RevisioneDati = revision,
        DataCreazione = DateTime.Now.AddMinutes(-revision),
        UltimaModifica = DateTime.Now,
        CreatoDa = "test",
        ModificatoDa = "test",
        Descrizione = description
    };

    private static TestCase Clone(TestCase tc) =>
        JsonIo.TryParse<TestCase>(System.Text.Json.JsonSerializer.Serialize(tc, JsonIo.Options), out _)!;

    private static string Temp(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}

