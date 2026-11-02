using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class RepairBackupRetentionTests
{
    [Fact]
    public void RepairBackups_KeepLatestAndBoundOlderCopies()
    {
        using var temp = new TempDirectory();
        var target = ManagedTestCaseTarget(temp.Path, "T1");
        File.WriteAllText(target, "{}");
        var now = DateTime.UtcNow;
        var backups = new List<string>();

        for (var i = 0; i < 5; i++)
        {
            var path = target + $".corrotto-20260901-09{i:00}00.bak";
            File.WriteAllText(path, $"backup-{i}");
            File.SetLastWriteTimeUtc(path, now.AddDays(-i * 10));
            backups.Add(path);
        }

        var removed = StorageMaintenanceService.CleanupRepairBackups(temp.Path, now);
        var remaining = backups.Where(File.Exists).ToList();

        Assert.Equal(2, removed);
        Assert.Equal(StorageMaintenanceService.MaxRepairBackupsPerTarget, remaining.Count);
        Assert.True(File.Exists(backups[0]));
    }

    [Fact]
    public void LatestRepairBackup_IsKeptEvenWhenVeryOld()
    {
        using var temp = new TempDirectory();
        var target = ManagedTestCaseTarget(temp.Path, "T2");
        File.WriteAllText(target, "{}");
        var backup = target + ".corrotto-20250101-000000.bak";
        File.WriteAllText(backup, "only-recovery");
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(backup, now - StorageMaintenanceService.RepairBackupRetention - TimeSpan.FromDays(30));

        var removed = StorageMaintenanceService.CleanupRepairBackups(temp.Path, now);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public void ThirdPartyBak_IsNeverDeleted()
    {
        using var temp = new TempDirectory();
        var thirdParty = Path.Combine(temp.Path, "thirdparty.json.corrotto-20200101-000000.bak");
        File.WriteAllText(thirdParty, "keep");
        File.SetLastWriteTimeUtc(thirdParty, DateTime.UtcNow.AddYears(-5));

        StorageMaintenanceService.CleanupRepairBackups(temp.Path, DateTime.UtcNow);

        Assert.True(File.Exists(thirdParty));
    }

    private static string ManagedTestCaseTarget(string root, string id)
    {
        var dir = Path.Combine(root, "testcases", "ECM");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"testcase_{id}.json");
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "testloom-repair-retention-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
