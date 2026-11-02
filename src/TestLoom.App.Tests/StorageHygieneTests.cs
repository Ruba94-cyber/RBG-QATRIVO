using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class StorageHygieneTests
{
    [Fact]
    public void IsPhaseDTemporaryPath_RiconosceEntrambiGliSuffissiMaSoloSuiPercorsiPhaseD()
    {
        // 6.7.1 (fix rete): JsonIo e LockService creano temporanei `.tmpXXXXXXXX` senza trattino; il riconoscitore accetta anche
        // la forma `.tmp-XXXXXXXX`. Non deve mai riconoscere dati reali o file estranei.
        var root = Temp("TestLoom_TmpRecognizer");
        try
        {
            var manifest = PhaseDMasterJournalService.ManifestPath(root);
            Assert.True(TestLoomStorageOwnership.IsPhaseDTemporaryPath(root, manifest + ".tmp1a2b3c4d"));
            Assert.True(TestLoomStorageOwnership.IsPhaseDTemporaryPath(root, manifest + ".tmp-1a2b3c4d"));

            Assert.False(TestLoomStorageOwnership.IsPhaseDTemporaryPath(root, manifest), "Il dato reale non è un temporaneo.");
            Assert.False(TestLoomStorageOwnership.IsPhaseDTemporaryPath(root, manifest + ".tmp1a2b3c4"), "Suffisso di lunghezza errata.");
            Assert.False(TestLoomStorageOwnership.IsPhaseDTemporaryPath(root, manifest + ".tmpzzzzzzzz"), "Suffisso non esadecimale.");
            Assert.False(TestLoomStorageOwnership.IsPhaseDTemporaryPath(root, manifest + ".tmp-1a2b3c4d.json"), "Un file reale con suffisso non è un temporaneo.");
            Assert.False(TestLoomStorageOwnership.IsPhaseDTemporaryPath(root, Path.Combine(root, "estraneo.json.tmp1a2b3c4d")),
                "Un file fuori dai percorsi Phase D non è gestito, anche con il suffisso temporaneo.");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void GetActiveLock_Scaduto_EReadOnlyENonRimuoveIlFile()
    {
        var root = Temp("TestLoom_LockExpiry");
        try
        {
            var target = Path.Combine(root, "operatori.json");
            File.WriteAllText(target, "[]");
            var clock = new TestMonotonicClock();
            var locks = new LockService("PC-Test", clock.CreateTracker());
            Assert.True(locks.TryAcquire(target, "Paolo").Acquisito);
            var lockPath = LockService.LockPathFor(target);
            var info = JsonIo.TryRead<LockInfo>(lockPath, out _)!;
            info.UltimoHeartbeat = DateTime.Now - LockService.Timeout - TimeSpan.FromMinutes(1);
            JsonIo.WriteAtomic(lockPath, info);
            File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - LockService.Timeout - TimeSpan.FromMinutes(1));

            // Il primo avvistamento resta conservativo anche con un mtime remoto vecchio.
            Assert.NotNull(locks.GetActiveLock(target));
            clock.Advance(LockService.Timeout + TimeSpan.FromSeconds(1));
            Assert.Null(locks.GetActiveLock(target));
            Assert.True(File.Exists(lockPath));

            var cleanup = locks.CleanupStaleLocks(root);
            Assert.Equal(1, cleanup.RimossiScaduti);
            Assert.False(File.Exists(lockPath));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void CleanupStaleLocks_RemovesOldDitmCorruptButKeepsFreshAndThirdPartyLocks()
    {
        var root = Temp("TestLoom_LockOrphan");
        try
        {
            var testDir = Path.Combine(root, "testcases", "Componente");
            Directory.CreateDirectory(testDir);
            var oldLock = Path.Combine(testDir, "testcase_OLD.json.lock");
            var freshLock = Path.Combine(testDir, "testcase_FRESH.json.lock");
            var thirdPartyLock = Path.Combine(root, "thirdparty-document.json.lock");
            File.WriteAllText(oldLock, "{broken");
            File.WriteAllText(freshLock, "{broken");
            File.WriteAllText(thirdPartyLock, "{broken");
            var old = DateTime.UtcNow - LockService.Timeout - TimeSpan.FromMinutes(2);
            File.SetLastWriteTimeUtc(oldLock, old);
            File.SetLastWriteTimeUtc(thirdPartyLock, old);

            var clock = new TestMonotonicClock();
            var locks = new LockService("PC-Test", clock.CreateTracker());
            var prima = locks.CleanupStaleLocks(root);
            Assert.Equal(0, prima.TotaleRimossi);
            clock.Advance(LockService.Timeout + TimeSpan.FromSeconds(1));
            File.WriteAllText(freshLock, "{still-broken}"); // cambia impronta e riavvia la soglia conservativa
            var result = locks.CleanupStaleLocks(root);

            Assert.False(File.Exists(oldLock));
            Assert.True(File.Exists(freshLock));
            Assert.True(File.Exists(thirdPartyLock));
            Assert.Equal(1, result.RimossiIlleggibiliOrfani);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void NotificationRetention_HidesAndCompactsExpiredReadAndUnreadNotifications()
    {
        var root = Temp("TestLoom_NotificationRetention");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var repo = new NotificaRepository(paths);

            repo.Add(new NotificaApp
            {
                Destinatario = "Paolo", Titolo = "old-read", Messaggio = "x", Letta = true,
                Data = DateTime.Now - NotificaRepository.RetentionLetta - TimeSpan.FromDays(2)
            });
            repo.Add(new NotificaApp
            {
                Destinatario = "Paolo", Titolo = "old-unread", Messaggio = "x", Letta = false,
                Data = DateTime.Now - NotificaRepository.RetentionNonLetta - TimeSpan.FromDays(2)
            });
            repo.Add(new NotificaApp
            {
                Destinatario = "Paolo", Titolo = "recent", Messaggio = "x", Letta = true,
                Data = DateTime.Now.AddDays(-2)
            });

            Assert.Equal(["recent"], repo.GetAll("Paolo").Select(n => n.Titolo).ToArray());
            var maintenance = repo.RunMaintenance(force: true);
            Assert.True(maintenance.StatiScadutiEliminati >= 2);
            Assert.Equal(["recent"], repo.GetAll("Paolo").Select(n => n.Titolo).ToArray());
        }
        finally { Delete(root); }
    }

    [Fact]
    public void NotificationMaintenance_DeletesExpiredLegacyJson()
    {
        var root = Temp("TestLoom_NotificationLegacyRetention");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var item = new NotificaApp
            {
                Uid = Guid.NewGuid(), Destinatario = "Paolo", Titolo = "legacy", Messaggio = "old",
                Letta = true, Data = DateTime.Now - NotificaRepository.RetentionLetta - TimeSpan.FromDays(5)
            };
            var file = Path.Combine(paths.NotificheDir, $"notifica_{item.Uid:N}.json");
            JsonIo.WriteAtomic(file, item);

            var result = new NotificaRepository(paths).RunMaintenance(force: true);

            Assert.False(File.Exists(file));
            Assert.Equal(1, result.LegacyJsonEliminati);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void MarkAllRead_UpdatesEveryNotificationInOneRepositoryOperation()
    {
        var root = Temp("TestLoom_NotificationBatchRead");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var repo = new NotificaRepository(paths);
            for (var i = 0; i < 75; i++)
                repo.Add(new NotificaApp { Destinatario = "Paolo", Titolo = $"N{i}", Messaggio = "x", Data = DateTime.Now });

            Assert.Equal(75, repo.MarkAllRead("Paolo"));
            var all = repo.GetAll("Paolo");
            Assert.Equal(75, all.Count);
            Assert.All(all, n => Assert.True(n.Letta));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void StorageMaintenance_RemovesOnlyOldDitmTempPatterns()
    {
        var root = Temp("TestLoom_TempCleanup");
        try
        {
            var testDir = Path.Combine(root, "testcases", "Componente");
            Directory.CreateDirectory(testDir);
            var oldAtomic = Path.Combine(testDir, "testcase_X.json.tmpABCDEF12");
            var unrelated = Path.Combine(root, "notes.tmp");
            var deceptiveThirdParty = Path.Combine(root, "thirdparty.json.tmp1234ABCD");
            var testloomDir = Path.Combine(root, "_ditm", "journal", "device");
            Directory.CreateDirectory(testloomDir);
            var oldDitm = Path.Combine(testloomDir, "checkpoint.json.gz.0123456789abcdef0123456789abcdef.tmp");
            File.WriteAllText(oldAtomic, "x");
            File.WriteAllText(unrelated, "x");
            File.WriteAllText(deceptiveThirdParty, "x");
            File.WriteAllText(oldDitm, "x");
            var old = DateTime.UtcNow.AddDays(-2);
            File.SetLastWriteTimeUtc(oldAtomic, old);
            File.SetLastWriteTimeUtc(unrelated, old);
            File.SetLastWriteTimeUtc(deceptiveThirdParty, old);
            File.SetLastWriteTimeUtc(oldDitm, old);

            var removed = StorageMaintenanceService.CleanupStaleTechnicalTempFiles(root, DateTime.Now);

            Assert.Equal(2, removed);
            Assert.False(File.Exists(oldAtomic));
            Assert.False(File.Exists(oldDitm));
            Assert.True(File.Exists(unrelated));
            Assert.True(File.Exists(deceptiveThirdParty));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void OwnershipClassifier_FailsClosedOutsideDitmPaths()
    {
        var root = Temp("TestLoom_Ownership");
        try
        {
            Assert.True(TestLoomStorageOwnership.IsManagedJsonPath(root, Path.Combine(root, "operatori.json")));
            Assert.True(TestLoomStorageOwnership.IsManagedJsonPath(root, Path.Combine(root, "testcases", "Componente", "testcase_T1.json")));
            Assert.False(TestLoomStorageOwnership.IsManagedJsonPath(root, Path.Combine(root, "unrelated.json")));
            Assert.False(TestLoomStorageOwnership.IsManagedJsonPath(root, Path.Combine(root, "documents", "testcase_fake.json")));
            Assert.False(TestLoomStorageOwnership.IsManagedJsonPath(root, Path.Combine(root, "..", "testcase_outside.json")));
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
