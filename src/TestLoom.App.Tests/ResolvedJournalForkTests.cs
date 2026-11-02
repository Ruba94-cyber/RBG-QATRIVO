using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class ResolvedJournalForkTests
{
    [Fact]
    public void ResolvedForkStamp_RemovesConflictFromSubsequentJournalRefreshes()
    {
        var root = Temp("TestLoom_ResolvedForkShared");
        var localA = Temp("TestLoom_ResolvedForkA");
        var localB = Temp("TestLoom_ResolvedForkB");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var uid = Guid.NewGuid();
            var testPath = Path.Combine(paths.TestCasesDir, "testcase_T-RES.json");
            var baseVersion = NewTest("T-RES", uid, 1, "base");
            var branchA = NewTest("T-RES", uid, 2, "branch-a");
            var branchB = NewTest("T-RES", uid, 2, "branch-b");

            var journalA = new SharedTestJournalService(paths, localA, "device-a", 4096, 2);
            var journalB = new SharedTestJournalService(paths, localB, "device-b", 4096, 2);
            Assert.True(journalA.TryPublish(journalA.Prepare(JournalOperation.Save, baseVersion, branchA, testPath, testPath)));
            Assert.True(journalB.TryPublish(journalB.Prepare(JournalOperation.Save, baseVersion, branchB, testPath, testPath)));

            // Il JSON autorevole deve esistere sul path che gli eventi del journal indicano come stato corrente.
            JsonIo.WriteAtomic(testPath, branchA);
            var fork = Assert.Single(journalA.RefreshKnownForks());
            Assert.Single(journalB.RefreshKnownForks());

            // Simula la risoluzione Master: lo stamp vive nel TestCase stesso, non in un sidecar separato.
            var resolved = Clone(branchA);
            resolved.RevisioneDati = 3;
            resolved.Descrizione = "resolved";
            TestCaseForkResolution.MarkResolved(resolved, new[] { fork });
            JsonIo.WriteAtomic(testPath, resolved);

            Assert.Empty(journalA.RefreshKnownForks());
            Assert.Empty(journalB.RefreshKnownForks());
            Assert.False(journalA.HasKnownFork(uid));
            Assert.False(journalB.HasKnownFork(uid));
        }
        finally
        {
            Delete(root);
            Delete(localA);
            Delete(localB);
        }
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
