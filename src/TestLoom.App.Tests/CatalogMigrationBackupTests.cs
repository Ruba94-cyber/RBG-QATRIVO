using System.IO.Compression;
using System.Threading;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Il marcatore delle migrazioni del catalogo deve entrare nel backup giornaliero come gli altri file condivisi
/// di TestLoom (TestLoomStorageOwnership.RootJsonNames), altrimenti una ripristino perderebbe la memoria delle migrazioni già fatte.</summary>
[Collection("BackupWriteGate")] // BackupService.WriteGate è statico: niente esecuzione parallela con le altre classi di backup
public sealed class CatalogMigrationBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_CatMig_Backup_" + Guid.NewGuid().ToString("N"));
    private readonly string _localAppData = Path.Combine(Path.GetTempPath(), "TestLoom_CatMig_Backup_LocalAppData_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Backup_GiornalieroIncludeIlMarcatoreDelleMigrazioniCatalogo()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        File.WriteAllText(paths.CatalogMigrationsFile,
            "{\"RevisioneDati\":1,\"Applicate\":[\"" + CatalogMigrationService.IdRasUdtTemplateV1 + "\"]}");

        var zipPath = BackupService.CreaGiornalieroSeNecessario(paths, new DateTime(2026, 10, 12, 9, 0, 0), CancellationToken.None, _localAppData);

        Assert.NotNull(zipPath);
        using var zip = ZipFile.OpenRead(zipPath!);
        var entries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("catalog-migrations.json", entries);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (Directory.Exists(_localAppData)) Directory.Delete(_localAppData, true);
    }
}
