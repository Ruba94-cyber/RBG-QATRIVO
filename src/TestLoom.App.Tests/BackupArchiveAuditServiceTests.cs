using System.IO.Compression;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class BackupArchiveAuditServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ZipAudit_" + Guid.NewGuid().ToString("N"));

    private string Create(string fileName, params (string Name, string Payload)[] entries)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, fileName);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, payload) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(payload);
            }
        return path;
    }

    [Fact]
    public void SnapshotValido_VieneLettoIntegralmenteENonEstratto()
    {
        var zip = Create("valid.zip",
            ("requisiti.json", "[{\"Id\":\"R-1\"}]"),
            ("nonconformita/nc_1.json", "{}"),
            ("snapshot-manifest.txt", "TestLoom snapshot"));
        var result = BackupArchiveAuditService.Verify(zip);
        Assert.True(result.Valid);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(64, result.ArchiveSha256.Length);
        Assert.Contains("non include gli allegati", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_dir, "nonconformita")));
    }

    [Theory]
    [InlineData("../segreto.json")]
    [InlineData("/root/evil.json")]
    [InlineData("C:/Windows/evil.json")]
    [InlineData("dir/../evil.json")]
    public void BloccaPathTraversalEDriveWindows(string entry)
    {
        var zip = Create("unsafe_" + Guid.NewGuid().ToString("N") + ".zip",
            ("snapshot-manifest.txt", "TestLoom snapshot"),
            (entry, "x"));
        var result = BackupArchiveAuditService.Verify(zip);
        Assert.False(result.Valid);
        Assert.Contains("insicuro", result.Summary);
    }

    [Fact]
    public void ArchivioPrivoDiManifestNonCertificabileComeBackupDitm()
    {
        var zip = Create("no-manifest.zip", ("dati.json", "{}"));
        var result = BackupArchiveAuditService.Verify(zip);
        Assert.False(result.Valid);
        Assert.Contains(result.Warnings, w => w.Contains("Manifest"));
    }

    [Fact]
    public void ArchivioConFileDuplicatiVieneRifiutato()
    {
        var zip = Create("duplicate.zip",
            ("snapshot-manifest.txt", "TestLoom"),
            ("nc_1.json", "versione1"),
            ("nc_1.json", "versione2"));
        var result = BackupArchiveAuditService.Verify(zip);
        Assert.False(result.Valid);
        Assert.Contains("duplicato", result.Summary);
    }

    [Fact]
    public void LimiteDecompressioneRidottoBloccaZipSenzaScrivere()
    {
        var zip = Create("large.zip", ("snapshot-manifest.txt", "TestLoom"),
            ("dati.json", new string('A', 300)));
        var sizeBefore = new FileInfo(zip).Length;
        var result = BackupArchiveAuditService.Verify(zip, maxUncompressedBytes: 30);
        Assert.False(result.Valid);
        Assert.Equal(sizeBefore, new FileInfo(zip).Length);
    }

    [Fact]
    public void ArchivioCorrottoNonLanciaErroreNonGestito()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "broken.zip");
        File.WriteAllBytes(path, new byte[] { 0, 1, 2, 3, 4 });
        var result = BackupArchiveAuditService.Verify(path);
        Assert.False(result.Valid);
    }

    [Fact]
    public void AnnullamentoInterrompeLaScansione()
    {
        var zip = Create("canceled.zip", ("snapshot-manifest.txt", "TestLoom"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => BackupArchiveAuditService.Verify(zip, cts.Token));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
