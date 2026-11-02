using System.Text.Json;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class FileRepairStateSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_RepairSafety_" + Guid.NewGuid().ToString("N"));

    public FileRepairStateSafetyTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "_ditm"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ManagedCorruptJson_IsRepairedAtomicallyAndKeepsDurableBackup()
    {
        var path = Path.Combine(_root, "operatori.json");
        File.WriteAllText(path, "{\"nome\":\"Mario\",}");
        var before = File.ReadAllBytes(path);

        var result = FileRepairService.ProvaRipara(path);

        Assert.True(result.Riuscita, result.Messaggio);
        Assert.True(File.Exists(result.Messaggio));
        Assert.Equal(before, File.ReadAllBytes(result.Messaggio));
        using var repaired = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal("Mario", repaired.RootElement.GetProperty("nome").GetString());
    }

    [Fact]
    public void ForeignWorkflowMarker_BlocksRepairAndPreservesOriginalBytes()
    {
        var path = Path.Combine(_root, "operatori.json");
        File.WriteAllText(path, "{\"nome\":\"Mario\",}");
        var before = File.ReadAllBytes(path);
        var paths = new DataPaths(_root);
        var txDir = Path.Combine(_root, "_ditm", "transactions");
        Directory.CreateDirectory(txDir);
        var marker = new WorkflowTransactionMarker
        {
            Id = Guid.NewGuid(),
            StartedUtc = DateTime.UtcNow,
            Device = "PC-B",
            Description = "remote-save"
        };
        JsonIo.WriteAtomic(Path.Combine(txDir, $"{marker.Id:N}.pending.json"), marker);

        var result = FileRepairService.ProvaRipara(path);

        Assert.False(result.Riuscita);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(_root, "operatori.json.corrotto-*.bak", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void UnmanagedJson_IsNeverTouchedEvenIfRepairable()
    {
        var external = Path.Combine(_root, "third-party.json");
        File.WriteAllText(external, "{\"x\":1,}");
        var before = File.ReadAllBytes(external);

        var result = FileRepairService.ProvaRipara(external);

        Assert.False(result.Riuscita);
        Assert.Equal(before, File.ReadAllBytes(external));
        Assert.Empty(Directory.EnumerateFiles(_root, "third-party.json.corrotto-*.bak", SearchOption.TopDirectoryOnly));
    }
}
