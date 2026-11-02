using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class LocalSqliteMaintenanceTests
{
    [Fact]
    public void CorruptQuarantines_AreBoundedByAgeAndCount()
    {
        using var temp = new TempDirectory();
        var now = DateTime.UtcNow;
        var created = new List<string>();

        // Otto copie recenti: devono restarne al massimo sei.
        for (var i = 0; i < 8; i++)
        {
            var path = Path.Combine(temp.Path, $"index.db.corrupt-20260901090{i:00}");
            File.WriteAllBytes(path, new byte[32 + i]);
            File.SetLastWriteTimeUtc(path, now.AddMinutes(-i));
            created.Add(path);
        }

        // Una copia molto vecchia deve sparire anche se rientrasse nel limite numerico.
        var old = Path.Combine(temp.Path, "journal.db.corrupt-20260101000000000");
        File.WriteAllBytes(old, new byte[64]);
        File.SetLastWriteTimeUtc(old, now - LocalSqliteMaintenanceService.CorruptQuarantineRetention - TimeSpan.FromDays(1));

        var freed = LocalSqliteMaintenanceService.CleanupCorruptQuarantines(temp.Path, now);
        var remaining = Directory.EnumerateFiles(temp.Path).Where(p => Path.GetFileName(p).Contains(".corrupt-", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.True(freed > 0);
        Assert.True(remaining.Count <= LocalSqliteMaintenanceService.MaxCorruptQuarantineFiles);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void QuarantineCleanup_DoesNotTouchUnrelatedLocalFiles()
    {
        using var temp = new TempDirectory();
        var unrelated = Path.Combine(temp.Path, "user-notes.txt");
        File.WriteAllText(unrelated, "keep");
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddYears(-1));

        LocalSqliteMaintenanceService.CleanupCorruptQuarantines(temp.Path, DateTime.UtcNow);

        Assert.True(File.Exists(unrelated));
        Assert.Equal("keep", File.ReadAllText(unrelated));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "testloom-sqlite-maint-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
