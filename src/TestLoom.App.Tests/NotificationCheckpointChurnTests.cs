using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class NotificationCheckpointChurnTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_NotificationCheckpoint_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MaintenanceSuCheckpointGiaCompatto_NonRiscriveIlFile()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new NotificaRepository(paths);
        repo.Add(new NotificaApp
        {
            Destinatario = "Paolo",
            Titolo = "stabile",
            Messaggio = "nessun churn",
            Data = DateTime.Now
        });

        var primaCompattazione = repo.RunMaintenance(force: true);
        Assert.True(primaCompattazione.FileLedgerCompattati >= 1);

        var device = DeviceIdentityService.GetOrCreate();
        var checkpoint = Path.Combine(_root, "_ditm", "notifications", device, "checkpoint.json.gz");
        Assert.True(File.Exists(checkpoint));
        var bytesPrima = File.ReadAllBytes(checkpoint);
        var sentinel = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(checkpoint, sentinel);

        var secondaCompattazione = repo.RunMaintenance(force: true);

        Assert.Equal(0, secondaCompattazione.FileLedgerCompattati);
        Assert.True(File.Exists(checkpoint));
        Assert.Equal(bytesPrima, File.ReadAllBytes(checkpoint));
        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(checkpoint));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
