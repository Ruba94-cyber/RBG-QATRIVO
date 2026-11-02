using System.IO.Compression;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class JournalCheckpointCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_JournalFuture_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_JournalFuture_Local_" + Guid.NewGuid().ToString("N"));

    public JournalCheckpointCompatibilityTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDelete(_root);
        TryDelete(_local);
    }

    [Fact]
    public void CheckpointSchemaFuturo_NonVieneSovrascrittoNeUsatoPerCancellareSegmenti()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        const string device = "device-future-checkpoint";
        var deviceDir = Path.Combine(_root, "_ditm", "journal", device);
        Directory.CreateDirectory(deviceDir);
        var options = new JsonSerializerOptions(JsonIo.Options) { WriteIndented = false };

        var oldEvent = new TestJournalEvent
        {
            SchemaVersion = SharedTestJournalService.JournalSchemaVersion,
            EventId = Guid.NewGuid().ToString("N"),
            DeviceId = device,
            TimestampUtc = DateTime.UtcNow.AddMinutes(-1),
            Operation = JournalOperation.Save,
            EntityUid = Guid.NewGuid(),
            EntityId = "OLD",
            NewRevision = 1,
            NewHash = "OLD-HASH",
            NewRelativePath = Path.Combine("testcases", "old", "testcase_old.json")
        };
        var oldSegment = Path.Combine(deviceDir, "segment-00000000000000000-old.ndjson");
        File.WriteAllText(oldSegment, JsonSerializer.Serialize(oldEvent, options) + Environment.NewLine);

        var checkpointPath = Path.Combine(deviceDir, "checkpoint.json.gz");
        using (var file = new FileStream(checkpointPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        {
            JsonSerializer.Serialize(gzip, new JournalCheckpoint
            {
                SchemaVersion = 999,
                DeviceId = device,
                GeneratedUtc = DateTime.UtcNow,
                Heads = new Dictionary<string, TestJournalEvent>()
            }, options);
        }
        var openingHash = WorkflowTransactionVisibilityService.Hash(File.ReadAllBytes(checkpointPath));

        var journal = new SharedTestJournalService(
            paths,
            _local,
            deviceIdOverride: device,
            rotateAfterBytesOverride: 256,
            completedSegmentsToKeepOverride: 1);
        var tc = new TestCase
        {
            Id = "TC-" + new string('Y', 400),
            Uid = Guid.NewGuid(),
            RevisioneDati = 1,
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now
        };
        var testPath = Path.Combine(paths.TestCasesDir, "testcase_future_checkpoint.json");
        JsonIo.WriteAtomic(testPath, tc);
        Assert.True(journal.TryPublish(journal.Prepare(JournalOperation.Create, null, tc, null, testPath)));

        Assert.True(File.Exists(oldSegment));
        Assert.Equal(openingHash, WorkflowTransactionVisibilityService.Hash(File.ReadAllBytes(checkpointPath)));
        Assert.True(Directory.GetFiles(deviceDir, "segment-*.ndjson").Length >= 2);

        // Anche il refresh fork non deve trasformare il checkpoint futuro in uno schema corrente vuoto.
        _ = journal.RefreshKnownForks();
        Assert.Equal(openingHash, WorkflowTransactionVisibilityService.Hash(File.ReadAllBytes(checkpointPath)));
    }

    private static void TryDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
