using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class BackupMaintenanceTelemetryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_BackupMaintenance_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void StorageMaintenance_RiportaIlTemporaryBackupCheHaDavveroEliminato()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        // Dalla 6.4.2 il backup è SOLO locale, sotto <localWorkspaceDirectory>\backup (vedi BackupService/
        // WorkspaceLocalPaths): StorageMaintenanceService applica la retention lì, non più in paths.BackupDir
        // (condiviso, non più scritto).
        var localWorkspaceDirectory = Path.Combine(_root, "local-workspace");
        var backupDirLocale = Path.Combine(localWorkspaceDirectory, "backup");
        Directory.CreateDirectory(backupDirLocale);

        var now = DateTime.Now;
        var staleTemp = Path.Combine(backupDirLocale, "TestLoom_snapshot_20260101.tmp");
        File.WriteAllText(staleTemp, "partial-backup");
        File.SetLastWriteTime(staleTemp, now.AddDays(-2));

        var result = StorageMaintenanceService.Run(
            paths,
            new LockService(),
            new NotificaRepository(paths),
            now,
            localWorkspaceDirectory: localWorkspaceDirectory,
            vacuumLocalSqlite: false);

        Assert.False(File.Exists(staleTemp));
        Assert.Equal(1, result.BackupTemporaneiEliminati);
        Assert.True(result.TotaleArtefattiEliminati >= 1);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
