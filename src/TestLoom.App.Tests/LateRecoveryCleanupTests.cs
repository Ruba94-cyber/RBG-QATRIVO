using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class LateRecoveryCleanupTests
{
    [Fact]
    public void Fresh_Recovery_Is_Not_Deleted_Only_Because_Test_Has_Old_Resolved_Fork()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        paths.EnsureStructure();
        var uid = Guid.NewGuid();
        var test = new TestCase
        {
            Uid = uid,
            Id = "T-LATE-RECOVERY",
            RevisioneDati = 10,
            DataCreazione = DateTime.Now.AddDays(-10),
            UltimaModifica = DateTime.Now
        };
        TestCaseForkResolution.SetResolvedKeys(test, ["OLD-RESOLVED-FORK"]);

        var dir = Path.Combine(temp.Path, "_ditm", "conflicts", uid.ToString("N"));
        Directory.CreateDirectory(dir);
        var recovery = Path.Combine(dir, "new-event.json.gz");
        File.WriteAllBytes(recovery, [1, 2, 3]);
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(recovery, now);

        var removed = DelayedForkRecoveryService.CleanupResolvedDirectories(paths, [test], [], now);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(recovery));
    }

    [Fact]
    public void Old_Recovery_Without_Open_Fork_Is_Eventually_Cleaned()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        paths.EnsureStructure();
        var uid = Guid.NewGuid();
        var test = new TestCase
        {
            Uid = uid,
            Id = "T-OLD-RECOVERY",
            RevisioneDati = 11,
            DataCreazione = DateTime.Now.AddDays(-20),
            UltimaModifica = DateTime.Now
        };
        TestCaseForkResolution.SetResolvedKeys(test, ["OLD-RESOLVED-FORK"]);

        var dir = Path.Combine(temp.Path, "_ditm", "conflicts", uid.ToString("N"));
        Directory.CreateDirectory(dir);
        var recovery = Path.Combine(dir, "late-old-event.json.gz");
        File.WriteAllBytes(recovery, [1, 2, 3]);
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(recovery, now - DelayedForkRecoveryService.LateRecoveryGracePeriod - TimeSpan.FromHours(1));

        var removed = DelayedForkRecoveryService.CleanupResolvedDirectories(paths, [test], [], now);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(recovery));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Old_Recovery_Is_Preserved_While_Any_Fork_For_Uid_Is_Open()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        paths.EnsureStructure();
        var uid = Guid.NewGuid();
        var test = new TestCase
        {
            Uid = uid,
            Id = "T-OPEN-RECOVERY",
            RevisioneDati = 12,
            DataCreazione = DateTime.Now.AddDays(-20),
            UltimaModifica = DateTime.Now
        };
        TestCaseForkResolution.SetResolvedKeys(test, ["OLD-RESOLVED-FORK"]);

        var dir = Path.Combine(temp.Path, "_ditm", "conflicts", uid.ToString("N"));
        Directory.CreateDirectory(dir);
        var recovery = Path.Combine(dir, "open-event.json.gz");
        File.WriteAllBytes(recovery, [1, 2, 3]);
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(recovery, now - DelayedForkRecoveryService.LateRecoveryGracePeriod - TimeSpan.FromHours(1));

        var openFork = new JournalForkConflict { EntityUid = uid, EntityId = test.Id };
        var removed = DelayedForkRecoveryService.CleanupResolvedDirectories(paths, [test], [openFork], now);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(recovery));
    }

    [Fact]
    public void Old_Recovery_Is_Preserved_When_A_Foreign_Transaction_Blocks_The_Workspace()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        paths.EnsureStructure();
        var uid = Guid.NewGuid();
        var test = new TestCase
        {
            Uid = uid,
            Id = "T-BLOCKED-RECOVERY",
            RevisioneDati = 13,
            DataCreazione = DateTime.Now.AddDays(-20),
            UltimaModifica = DateTime.Now
        };
        TestCaseForkResolution.SetResolvedKeys(test, ["OLD-RESOLVED-FORK"]);

        var dir = Path.Combine(temp.Path, "_ditm", "conflicts", uid.ToString("N"));
        Directory.CreateDirectory(dir);
        var recovery = Path.Combine(dir, "blocked-event.json.gz");
        File.WriteAllBytes(recovery, [1, 2, 3]);
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(recovery, now - DelayedForkRecoveryService.LateRecoveryGracePeriod - TimeSpan.FromHours(1));

        // Un marker straniero/illeggibile deve congelare qualsiasi mutazione condivisa, inclusa la semplice
        // cleanup di recovery tecnici eseguita da un refresh.
        var transactions = Path.Combine(temp.Path, "_ditm", "transactions");
        Directory.CreateDirectory(transactions);
        File.WriteAllText(Path.Combine(transactions, Guid.NewGuid().ToString("N") + ".pending.json"), "{broken");

        var removed = DelayedForkRecoveryService.CleanupResolvedDirectories(paths, [test], [], now);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(recovery));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "testloom-late-recovery-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
