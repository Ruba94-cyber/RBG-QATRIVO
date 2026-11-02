using System.Text.Json;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class FailedSaveDraftServiceTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "TestLoom_DraftData_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_DraftLocal_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DueErroriDiSalvataggio_NonSovrascrivonoBozzeENonToccanoDataset()
    {
        Directory.CreateDirectory(_data);
        var original = Path.Combine(_data, "requisiti.json");
        File.WriteAllText(original, "[{\"Id\":\"6.5.3\"}]");
        var before = File.ReadAllBytes(original);
        var uid = Guid.NewGuid();

        var a = FailedSaveDraftService.Save(_data, "change-request", uid, 9, "Op1",
            new { Titolo = "Prima versione", Id = "CR-1" }, "Conflitto remoto", _local);
        var b = FailedSaveDraftService.Save(_data, "change-request", uid, 9, "Op1",
            new { Titolo = "Seconda versione", Id = "CR-1" }, "Disconnessione", _local);

        Assert.NotEqual(a, b);
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.Equal(before, File.ReadAllBytes(original));
        Assert.Empty(Directory.EnumerateFiles(_data, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(2, Directory.EnumerateFiles(FailedSaveDraftService.LocalDirectory(_data, _local), "*.json").Count());

        var envelope = JsonIo.TryRead<FailedSaveDraftService.DraftEnvelope>(a, out var error);
        Assert.Null(error);
        Assert.NotNull(envelope);
        Assert.Equal("change-request", envelope!.Domain);
        Assert.Equal(uid, envelope.EntityUid);
        Assert.Equal(9L, envelope.ExpectedRevision);
        using var payload = JsonDocument.Parse(envelope.PayloadJson);
        Assert.Equal("Prima versione", payload.RootElement.GetProperty("Titolo").GetString());
    }

    [Fact]
    public void BozzaConSnapshotOriginale_ConservaBaselineConSha256()
    {
        var uid = Guid.NewGuid();
        var original = new { Uid = uid, Id = "CR-BASE-01", Titolo = "Origine", RevisioneDati = 4L };
        var edited = new { Uid = uid, Id = "CR-BASE-01", Titolo = "Modificato", RevisioneDati = 4L };

        var file = FailedSaveDraftService.Save(_data, "change-request", uid, 4,
            "Op", edited, "Conflitto remoto", _local, baseSnapshot: original);
        var envelope = JsonIo.TryRead<FailedSaveDraftService.DraftEnvelope>(file, out var error)!;

        Assert.Null(error);
        Assert.NotNull(envelope.BasePayloadJson);
        Assert.NotNull(envelope.BaseSha256);
        Assert.Contains("Origine", envelope.BasePayloadJson);
        Assert.Contains("Modificato", envelope.PayloadJson);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(envelope.BasePayloadJson))), envelope.BaseSha256);
    }

    [Fact]
    public void BozzeLegacyPriveDiSnapshot_RestanoCompatibili()
    {
        var uid = Guid.NewGuid();
        var file = FailedSaveDraftService.Save(_data, "nonconformita", uid, 0,
            "Op", new { Uid = uid, Id = "NC-LEGACY" }, "offline", _local);
        var envelope = JsonIo.TryRead<FailedSaveDraftService.DraftEnvelope>(file, out var error)!;

        Assert.Null(error);
        Assert.Null(envelope.BasePayloadJson);
        Assert.Null(envelope.BaseSha256);
        Assert.Equal(1, envelope.Schema);
    }

    [Fact]
    public void BozzeSeparatePerCartella_AncheConLoStessoUid()
    {
        var uid = Guid.NewGuid();
        var first = FailedSaveDraftService.Save(_data, "nonconformita", uid, 1, "Op",
            new { Id = "NC-1" }, "offline", _local);
        var second = FailedSaveDraftService.Save(_data + "_altro", "nonconformita", uid, 1, "Op",
            new { Id = "NC-1" }, "offline", _local);
        Assert.NotEqual(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
    }

    [Fact]
    public void RifiutaDominioNonAutorizzato()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FailedSaveDraftService.Save(_data, "../allegati", Guid.NewGuid(), 0, "Op",
                new { Id = "x" }, "error", _local));
        Assert.False(Directory.Exists(_local));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_data)) Directory.Delete(_data, true); } catch { }
        try { if (Directory.Exists(_local)) Directory.Delete(_local, true); } catch { }
    }
}
