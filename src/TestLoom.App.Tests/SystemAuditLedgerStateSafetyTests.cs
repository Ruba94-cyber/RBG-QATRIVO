using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class SystemAuditLedgerStateSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_AuditLedgerSafety_" + Guid.NewGuid().ToString("N"));
    private DataPaths Paths => new(_root);

    public SystemAuditLedgerStateSafetyTests()
    {
        Directory.CreateDirectory(_root);
        Paths.EnsureStructure();
    }

    [Fact]
    public void CurrentTroncato_PrefissoRestaLeggibileEAppendNonCorrompeLaCoda()
    {
        var audit = new SystemAuditService(Paths);
        audit.Add(NewEvent("prima"));

        var deviceDir = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "_ditm", "audit")));
        var current = Path.Combine(deviceDir, "current.ndjson");
        using (var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var partial = Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"EventId\":\"");
            stream.Write(partial);
        }
        var poisoned = File.ReadAllBytes(current);

        var visible = audit.GetAll(Paths);
        Assert.Single(visible);
        Assert.Equal("prima", visible[0].Target);

        Assert.Throws<InvalidDataException>(() => audit.Add(NewEvent("non-accodato")));
        Assert.Equal(poisoned, File.ReadAllBytes(current));
    }

    [Fact]
    public void SegmentoParziale_BloccaCompactionESopravviveAllaRotazione()
    {
        var audit = new SystemAuditService(Paths);
        audit.Add(NewEvent("base"));
        var deviceDir = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "_ditm", "audit")));

        var bad = Path.Combine(deviceDir, "segment-00000000000000000-bad.ndjson");
        File.WriteAllText(bad, "{\"SchemaVersion\":1,\"EventId\":\"", new UTF8Encoding(false));
        var badBytes = File.ReadAllBytes(bad);
        WriteSegment(Path.Combine(deviceDir, "segment-00000000000000001-ok.ndjson"), NewEvent("manual-1"));
        WriteSegment(Path.Combine(deviceDir, "segment-00000000000000002-ok.ndjson"), NewEvent("manual-2"));

        audit.Add(NewEvent("rotazione", new string('x', 300_000)));

        Assert.True(File.Exists(bad));
        Assert.Equal(badBytes, File.ReadAllBytes(bad));
        Assert.False(File.Exists(Path.Combine(deviceDir, "checkpoint.json.gz")));
        Assert.True(Directory.GetFiles(deviceDir, "segment-*.ndjson").Length >= 4);
    }

    [Fact]
    public void CheckpointSchemaFuturo_BloccaCompactionESopravviveBytePerByte()
    {
        var audit = new SystemAuditService(Paths);
        audit.Add(NewEvent("base"));
        var deviceDir = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "_ditm", "audit")));

        WriteSegment(Path.Combine(deviceDir, "segment-00000000000000000-ok.ndjson"), NewEvent("manual-0"));
        WriteSegment(Path.Combine(deviceDir, "segment-00000000000000001-ok.ndjson"), NewEvent("manual-1"));
        WriteSegment(Path.Combine(deviceDir, "segment-00000000000000002-ok.ndjson"), NewEvent("manual-2"));

        var checkpoint = Path.Combine(deviceDir, "checkpoint.json.gz");
        WriteCheckpoint(checkpoint, new SystemAuditCheckpoint
        {
            SchemaVersion = 999,
            UpdatedUtc = DateTime.UtcNow,
            Events = new List<SystemAuditEvent>()
        });
        var futureBytes = File.ReadAllBytes(checkpoint);

        audit.Add(NewEvent("rotazione", new string('y', 300_000)));

        Assert.Equal(futureBytes, File.ReadAllBytes(checkpoint));
        Assert.True(File.Exists(Path.Combine(deviceDir, "segment-00000000000000000-ok.ndjson")));
    }

    private static SystemAuditEvent NewEvent(string target, string details = "") => new()
    {
        EventId = Guid.NewGuid(),
        TimestampUtc = DateTime.UtcNow,
        DeviceId = "test",
        Operatore = "Master",
        Azione = "SafetyTest",
        Target = target,
        Dettagli = details
    };

    [Fact]
    public void CurrentTroncato_LInterruzioneSiRegistraConFileERiga()
    {
        var audit = new SystemAuditService(Paths);
        audit.Add(NewEvent("prima"));

        var deviceDir = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "_ditm", "audit")));
        var current = Path.Combine(deviceDir, "current.ndjson");
        using (var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            stream.Write(Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"EventId\":\""));
        }

        Assert.Single(audit.GetAll(Paths));

        // Il prefisso resta visibile, ma la riga non valida non passa in silenzio: il registro indica file e riga.
        var testoLog = File.ReadAllText(Path.Combine(CrashLogService.LogDirectory, "crash.log"));
        Assert.Contains($"lettura interrotta alla riga 2 di {current}", testoLog);
    }

    private static void WriteSegment(string path, SystemAuditEvent evt)
    {
        evt.SchemaVersion = 1;
        File.WriteAllText(path, JsonSerializer.Serialize(evt, JsonIo.CompactOptions) + Environment.NewLine, new UTF8Encoding(false));
    }

    private static void WriteCheckpoint(string path, SystemAuditCheckpoint checkpoint)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        JsonSerializer.Serialize(gzip, checkpoint, JsonIo.Options);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
