using System.IO.Compression;
using System.Text.Json;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class CommitLedgerCompactionSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_ReceiptCompact_" + Guid.NewGuid().ToString("N"));

    public CommitLedgerCompactionSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public void SegmentoReceiptMalformato_NonVieneCompattatoNeCancellato()
    {
        var paths = CreatePaths();
        const string device = "receipt-device-malformed";
        var dir = LedgerDir(device);
        Directory.CreateDirectory(dir);

        var malformed = Path.Combine(dir, "segment-0001.ndjson");
        const string broken = "{\"Id\":\"troncato";
        File.WriteAllText(malformed, broken);
        WriteReceiptSegment(Path.Combine(dir, "segment-0002.ndjson"), Receipt(device, "two"));
        WriteReceiptSegment(Path.Combine(dir, "segment-0003.ndjson"), Receipt(device, "three"));

        WorkflowCommitLedgerService.Compact(paths, device);

        Assert.True(File.Exists(malformed));
        Assert.Equal(broken, File.ReadAllText(malformed));
        Assert.False(File.Exists(Path.Combine(dir, "checkpoint.json.gz")));
        Assert.Equal(3, Directory.GetFiles(dir, "segment-*.ndjson").Length);
    }

    [Fact]
    public void CheckpointReceiptSchemaFuturo_NonVieneSovrascrittoENonCancellaSegmenti()
    {
        var paths = CreatePaths();
        const string device = "receipt-device-future";
        var dir = LedgerDir(device);
        Directory.CreateDirectory(dir);
        WriteReceiptSegment(Path.Combine(dir, "segment-0001.ndjson"), Receipt(device, "one"));
        WriteReceiptSegment(Path.Combine(dir, "segment-0002.ndjson"), Receipt(device, "two"));
        WriteReceiptSegment(Path.Combine(dir, "segment-0003.ndjson"), Receipt(device, "three"));

        var checkpointPath = Path.Combine(dir, "checkpoint.json.gz");
        using (var file = new FileStream(checkpointPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        {
            JsonSerializer.Serialize(gzip, new WorkflowCommitLedgerCheckpoint
            {
                SchemaVersion = 999,
                DeviceId = device,
                UpdatedUtc = DateTime.UtcNow
            }, JsonIo.CompactOptions);
        }
        var openingHash = WorkflowTransactionVisibilityService.Hash(File.ReadAllBytes(checkpointPath));

        WorkflowCommitLedgerService.Compact(paths, device);

        Assert.Equal(openingHash, WorkflowTransactionVisibilityService.Hash(File.ReadAllBytes(checkpointPath)));
        Assert.Equal(3, Directory.GetFiles(dir, "segment-*.ndjson").Length);
    }

    private DataPaths CreatePaths()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        return paths;
    }

    private string LedgerDir(string device) => Path.Combine(_root, "_ditm", "transactions", "committed", device);

    private static WorkflowTransactionCommitReceipt Receipt(string device, string suffix) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = device,
        StartedUtc = DateTime.UtcNow.AddSeconds(-1),
        CompletedUtc = DateTime.UtcNow,
        Description = suffix,
        Entries = new List<WorkflowTransactionCommitEntry>
        {
            new()
            {
                RelativePath = Path.Combine("testcases", $"testcase_{suffix}.json"),
                FinalSha256 = new string('A', 64),
                LogicallyDeleted = false
            }
        }
    };

    private static void WriteReceiptSegment(string path, WorkflowTransactionCommitReceipt receipt) =>
        File.WriteAllText(path, JsonSerializer.Serialize(receipt, JsonIo.CompactOptions) + Environment.NewLine);
}
