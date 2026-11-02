using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class LockSessionAndSystemAuditTests
{
    [Fact]
    public void SameOperatorDifferentSession_CannotStealOrReleaseLiveLock()
    {
        var root = Temp("TestLoom_LockSession");
        try
        {
            var target = Path.Combine(root, "master.json");
            File.WriteAllText(target, "[]");
            var first = new LockService("PC-A");
            var second = new LockService("PC-B");

            var acquired = first.TryAcquire(target, "Paolo");
            Assert.True(acquired.Acquisito);
            Assert.False(second.TryAcquire(target, "Paolo").Acquisito);

            second.Release(target);
            Assert.True(File.Exists(LockService.LockPathFor(target)));

            first.Heartbeat(target, "Paolo");
            Assert.NotNull(first.GetActiveLock(target));
            first.Release(target);
            Assert.False(File.Exists(LockService.LockPathFor(target)));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void LateReleaseFromOldSession_DoesNotDeleteReacquiredLock()
    {
        var root = Temp("TestLoom_LateLockRelease");
        try
        {
            var target = Path.Combine(root, "master.json");
            File.WriteAllText(target, "[]");
            var oldSession = new LockService();
            var newSession = new LockService();
            Assert.True(oldSession.TryAcquire(target, "Paolo").Acquisito);

            newSession.ForceUnlock(target);
            Assert.True(newSession.TryAcquire(target, "Paolo").Acquisito);
            oldSession.Release(target);

            Assert.NotNull(newSession.GetActiveLock(target));
            newSession.Release(target);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void ForceUnlock_RaisesAuditEventWithPreviousOwner()
    {
        var root = Temp("TestLoom_ForceUnlockAuditEvent");
        try
        {
            var target = Path.Combine(root, "master.json");
            File.WriteAllText(target, "[]");
            var owner = new LockService();
            var admin = new LockService();
            Assert.True(owner.TryAcquire(target, "UserA").Acquisito);

            string? seenPath = null;
            LockInfo? seenInfo = null;
            admin.ForceUnlocked += (path, info) => { seenPath = path; seenInfo = info; };
            admin.ForceUnlock(target);

            Assert.Equal(target, seenPath);
            Assert.Equal("UserA", seenInfo?.Operatore);
            Assert.False(File.Exists(LockService.LockPathFor(target)));
        }
        finally { Delete(root); }
    }

    [Fact]
    [Trait("Category", "Scale")]
    public void SystemAudit_KeepsAllEventsWhileFileCountStaysBounded()
    {
        var root = Temp("TestLoom_SystemAuditBounded");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var audit = new SystemAuditService(paths);
            for (var i = 0; i < 5_000; i++)
            {
                audit.Add(new SystemAuditEvent
                {
                    Operatore = "Master",
                    Azione = "TestAction",
                    Target = $"T-{i}",
                    Dettagli = new string('x', 180)
                });
            }

            Assert.Equal(5_000, audit.GetAll(paths).Count);
            var auditRoot = Path.Combine(root, "_ditm", "audit");
            var files = Directory.EnumerateFiles(auditRoot, "*", SearchOption.AllDirectories).ToList();
            Assert.True(files.Count <= 4, $"File audit non bounded: {files.Count}");
        }
        finally { Delete(root); }
    }

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
