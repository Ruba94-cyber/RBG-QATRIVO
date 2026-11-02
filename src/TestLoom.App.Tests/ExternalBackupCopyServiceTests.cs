using System.IO.Compression;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class ExternalBackupCopyServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_ExternalBackup_" + Guid.NewGuid().ToString("N"));

    private (string Data, string Snapshot, string External) Arrange()
    {
        var data = Path.Combine(_root, "shared");
        var local = Path.Combine(_root, "local", "backup");
        var external = Path.Combine(_root, "external");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(local);
        var snapshot = Path.Combine(local, "TestLoom_snapshot_20261009.zip");
        using (var zip = ZipFile.Open(snapshot, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("operatori.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("[]");
        }
        return (data, snapshot, external);
    }

    [Fact]
    public void CopiaVerificata_NonToccaBackupOriginale_EDiventaIdempotente()
    {
        var (data, source, external) = Arrange();
        var original = File.ReadAllBytes(source);

        var first = ExternalBackupCopyService.CopyVerified(source, external, data);
        var second = ExternalBackupCopyService.CopyVerified(source, external, data);

        Assert.Equal(first, second);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(original, File.ReadAllBytes(first));
        Assert.Single(Directory.GetFiles(external, "*.zip"));
        Assert.Empty(Directory.GetFiles(external, "*.partial"));
    }

    [Fact]
    public void NonPermetteDiUsareComeDestinazioneLaCartellaOperativa()
    {
        var (data, source, _) = Arrange();
        Assert.Throws<IOException>(() => ExternalBackupCopyService.CopyVerified(source, Path.Combine(data, "backup"), data));
        Assert.False(Directory.Exists(Path.Combine(data, "backup")));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void NonPermetteDiCopiareSopraIlBackupLocale()
    {
        var (data, source, _) = Arrange();
        Assert.Throws<IOException>(() => ExternalBackupCopyService.CopyVerified(source, Path.GetDirectoryName(source)!, data));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void BackupCorrottoNonVienePubblicatoSulPercorsoEsterno()
    {
        var (data, source, external) = Arrange();
        File.WriteAllText(source, "not a zip");
        Assert.Throws<InvalidDataException>(() => ExternalBackupCopyService.CopyVerified(source, external, data));
        Assert.False(Directory.Exists(external));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
