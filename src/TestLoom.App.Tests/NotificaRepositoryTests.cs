using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class NotificaRepositoryTests
{
    [Fact]
    [Trait("Category", "Scale")]
    public void ManyNotifications_KeepSharedInfrastructureBounded()
    {
        var root = Temp("TestLoom_NotificationsBounded");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var repo = new NotificaRepository(paths);
            var payload = new string('x', 4096);

            for (var i = 0; i < 5_000; i++)
                repo.Add(new NotificaApp
                {
                    Destinatario = "Mario",
                    Titolo = $"N-{i}",
                    Messaggio = payload,
                    ElementoId = $"T-{i}"
                });

            var notificationRoot = Path.Combine(root, "_ditm", "notifications");
            var deviceDirs = Directory.GetDirectories(notificationRoot, "*", SearchOption.TopDirectoryOnly);
            var device = Assert.Single(deviceDirs);
            var files = Directory.GetFiles(device, "*", SearchOption.TopDirectoryOnly);

            Assert.True(files.Count(f => Path.GetFileName(f).StartsWith("segment-", StringComparison.OrdinalIgnoreCase)) <= 2);
            Assert.True(files.Length <= 4,
                $"Il ledger notifiche ha creato troppi file: {string.Join(", ", files.Select(Path.GetFileName))}");
            Assert.True(File.Exists(Path.Combine(device, "checkpoint.json.gz")));
            var tutte = repo.GetAll("Mario");
            Assert.Equal(5_000, tutte.Count);
            Assert.Equal(50, tutte.Take(50).Count());

            repo.MarkAllRead("Mario");
            Assert.All(repo.GetAll("Mario"), n => Assert.True(n.Letta));

            // Il vecchio pattern file-per-notifica non deve essere usato per i nuovi eventi.
            Assert.Empty(Directory.GetFiles(paths.NotificheDir, "notifica_*.json", SearchOption.TopDirectoryOnly));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void ReadThenDelete_DoesNotResurrectNotification()
    {
        var root = Temp("TestLoom_NotificationsDelete");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var repo = new NotificaRepository(paths);
            var original = new NotificaApp { Destinatario = "Anna", Titolo = "Test", Messaggio = "Messaggio" };

            repo.Add(original);
            var loaded = Assert.Single(repo.GetAll("Anna"));
            Assert.False(loaded.Letta);

            repo.MarkRead(loaded);
            var read = Assert.Single(repo.GetAll("Anna"));
            Assert.True(read.Letta);

            repo.Delete(read);
            Assert.Empty(repo.GetAll("Anna"));

            // Una nuova istanza deve vedere il tombstone condiviso, non solo lo stato in memoria.
            Assert.Empty(new NotificaRepository(paths).GetAll("Anna"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void DeleteTombstone_DominaCreateVecchiaArrivataDaUnAltraReplica()
    {
        var root = Temp("TestLoom_NotificationsReplicaDelete");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var repo = new NotificaRepository(paths);
            var uid = Guid.NewGuid();
            var createdAt = DateTime.Now.AddMinutes(-10);
            repo.AddDeterministic(new NotificaApp
            {
                Uid = uid,
                Data = createdAt,
                Destinatario = "Anna",
                Titolo = "Da eliminare",
                Messaggio = "Originale"
            });

            var live = Assert.Single(repo.GetAll("Anna"));
            repo.Delete(live);
            Assert.Empty(repo.GetAll("Anna"));

            // Simula un PC rimasto offline: sincronizza DOPO la cancellazione un Create più vecchio dello stesso
            // UID. Delete deve dominare semanticamente Create, non dipendere dall'ordine con cui OneDrive rende
            // visibili i file delle due repliche.
            WriteReplicaEvent(root, "replica-stale", new NotificationLedgerEvent
            {
                EventId = Guid.NewGuid(),
                DeviceId = "replica-stale",
                TimestampUtc = createdAt.ToUniversalTime(),
                NotificationUid = uid,
                Operation = NotificationLedgerOperation.Create,
                Notification = new NotificaApp
                {
                    Uid = uid,
                    Data = createdAt,
                    Destinatario = "Anna",
                    Titolo = "Da eliminare",
                    Messaggio = "Copia offline"
                }
            });

            Assert.Empty(new NotificaRepository(paths).GetAll("Anna"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void DeleteTombstone_RestaEfficaceDopoCompattazione()
    {
        var root = Temp("TestLoom_NotificationsDeleteCompact");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var repo = new NotificaRepository(paths);
            var uid = Guid.NewGuid();
            var createdAt = DateTime.Now.AddMinutes(-20);
            repo.AddDeterministic(new NotificaApp
            {
                Uid = uid,
                Data = createdAt,
                Destinatario = "Marta",
                Titolo = "Compatta",
                Messaggio = "Originale"
            });
            repo.Delete(Assert.Single(repo.GetAll("Marta")));

            repo.RunMaintenance(force: true);

            // Dopo la compattazione il current/segment può essere sparito: è il checkpoint a dover conservare
            // il tombstone. Una replica vecchia che compare in seguito non deve quindi resuscitare la notifica.
            WriteReplicaEvent(root, "replica-after-compact", new NotificationLedgerEvent
            {
                EventId = Guid.NewGuid(),
                DeviceId = "replica-after-compact",
                TimestampUtc = createdAt.ToUniversalTime(),
                NotificationUid = uid,
                Operation = NotificationLedgerOperation.Create,
                Notification = new NotificaApp
                {
                    Uid = uid,
                    Data = createdAt,
                    Destinatario = "Marta",
                    Titolo = "Compatta",
                    Messaggio = "Replica vecchia"
                }
            });

            Assert.Empty(new NotificaRepository(paths).GetAll("Marta"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void LegacyJson_IsReadableAndRemovedWhenUserDeletesIt()
    {
        var root = Temp("TestLoom_NotificationsLegacy");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var legacy = new NotificaApp
            {
                Destinatario = "Luca",
                Titolo = "Legacy",
                Messaggio = "Vecchia notifica"
            };
            var legacyPath = Path.Combine(paths.NotificheDir, $"notifica_{legacy.Uid:N}.json");
            JsonIo.WriteAtomic(legacyPath, legacy);

            var repo = new NotificaRepository(paths);
            var loaded = Assert.Single(repo.GetAll("Luca"));
            Assert.Equal(legacy.Uid, loaded.Uid);

            repo.Delete(loaded);
            Assert.False(File.Exists(legacyPath));
            Assert.Empty(repo.GetAll("Luca"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Maintenance_RimuoveJsonLegacyGiaRappresentatoNelLedgerV2()
    {
        var root = Temp("TestLoom_NotificationsLegacyMigration");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var uid = Guid.NewGuid();
            var data = DateTime.Now.AddDays(-2);
            var legacy = new NotificaApp
            {
                Uid = uid,
                Data = data,
                Destinatario = "Luca",
                Titolo = "Legacy duplicata",
                Messaggio = "Payload storico"
            };
            var legacyPath = Path.Combine(paths.NotificheDir, $"notifica_{uid:N}.json");
            JsonIo.WriteAtomic(legacyPath, legacy);

            // La stessa identità è già stata migrata/rappresentata nel ledger bounded. La manutenzione deve
            // eliminare il piccolo JSON legacy senza richiedere un click manuale dell'utente.
            var repo = new NotificaRepository(paths);
            WriteReplicaEvent(root, "migration-ledger", new NotificationLedgerEvent
            {
                EventId = Guid.NewGuid(),
                DeviceId = "migration-ledger",
                TimestampUtc = data.ToUniversalTime(),
                NotificationUid = uid,
                Operation = NotificationLedgerOperation.Create,
                Notification = legacy
            });

            var result = repo.RunMaintenance(force: true);

            Assert.False(File.Exists(legacyPath));
            Assert.True(result.LegacyJsonEliminati >= 1);
            Assert.Single(repo.GetAll("Luca"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void DeleteRead_RemovesOnlyReadNotifications()
    {
        var root = Temp("TestLoom_NotificationsDeleteRead");
        try
        {
            var paths = new DataPaths(root);
            paths.EnsureStructure();
            var repo = new NotificaRepository(paths);
            repo.Add(new NotificaApp { Destinatario = "Sara", Titolo = "A" });
            repo.Add(new NotificaApp { Destinatario = "Sara", Titolo = "B" });

            var all = repo.GetAll("Sara");
            repo.MarkRead(all.Single(n => n.Titolo == "A"));
            Assert.Equal(1, repo.DeleteRead("Sara"));

            var remaining = Assert.Single(repo.GetAll("Sara"));
            Assert.Equal("B", remaining.Titolo);
            Assert.False(remaining.Letta);
        }
        finally { Delete(root); }
    }

    private static void WriteReplicaEvent(string root, string deviceId, NotificationLedgerEvent evt)
    {
        var dir = Path.Combine(root, "_ditm", "notifications", deviceId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "current.ndjson");
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(JsonSerializer.Serialize(evt, JsonIo.CompactOptions));
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
