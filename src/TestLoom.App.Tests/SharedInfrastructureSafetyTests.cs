using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Regressioni sui writer condivisi che non sono repository di dominio. Anche configurazioni, audit e backup
/// devono rispettare gli stessi gate del resto di TestLoom: un errore di lettura o un marker concorrente non può
/// essere trasformato in default/sovrascrittura o in un nuovo artefatto condiviso.
/// </summary>
[Collection("BackupWriteGate")] // BackupService.WriteGate è statico: niente esecuzione parallela fra queste classi
public sealed class SharedInfrastructureSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_SharedInfra_" + Guid.NewGuid().ToString("N"));

    public SharedInfrastructureSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void PolarionConfig_ExistingCorruptFile_LoadsRuntimeFallbackButSaveCannotOverwriteIt()
    {
        var paths = new DataPaths(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PolarionConfigFile)!);
        File.WriteAllText(paths.PolarionConfigFile, "{ non-json");
        var before = File.ReadAllBytes(paths.PolarionConfigFile);

        var config = PolarionConfigService.Load(paths);

        Assert.NotNull(config);
        Assert.False(string.IsNullOrWhiteSpace(config.ErroreCaricamento));
        Assert.NotEmpty(config.SezioniDiDefault);
        Assert.Throws<InvalidDataException>(() => PolarionConfigService.Save(paths, new PolarionExportConfig()));
        Assert.Equal(before, File.ReadAllBytes(paths.PolarionConfigFile));
    }

    [Fact]
    public void PolarionConfig_MissingFile_StillReturnsSafeDefaultsWithoutWriting()
    {
        var paths = new DataPaths(_root);

        var config = PolarionConfigService.Load(paths);

        Assert.NotNull(config);
        Assert.Null(config.ErroreCaricamento);
        Assert.NotEmpty(config.SezioniDiDefault);
        Assert.False(File.Exists(paths.PolarionConfigFile));
    }

    [Fact]
    public void PolarionConfig_ForeignTransaction_BlocksSaveBeforeCreatingFile()
    {
        var paths = new DataPaths(_root);
        WriteForeignMarker(paths, "PC-B");

        Assert.Throws<IOException>(() =>
            PolarionConfigService.Save(paths, new PolarionExportConfig()));
        Assert.False(File.Exists(paths.PolarionConfigFile));
    }

    [Fact]
    public void SystemAudit_ForeignTransaction_BlocksSharedLedgerCreation()
    {
        var paths = new DataPaths(_root);
        WriteForeignMarker(paths, "PC-B");
        var audit = new SystemAuditService(paths);

        Assert.Throws<IOException>(() => audit.Add(new SystemAuditEvent
        {
            Operatore = "Tester",
            Azione = "SafetyTest",
            Target = "target"
        }));

        Assert.False(Directory.Exists(Path.Combine(_root, "_ditm", "audit")));
    }

    [Fact]
    public void Backup_ForeignTransaction_DoesNotPublishSnapshot()
    {
        var paths = new DataPaths(_root);
        WriteForeignMarker(paths, "PC-B");

        var localBase = Path.Combine(_root, "_local");
        Assert.Throws<IOException>(() => BackupService.CreaOra(paths, DateTime.Now, localBase));
        Assert.False(Directory.Exists(WorkspaceLocalPaths.GetBackupDirectory(paths.Root, localBase)));
        Assert.False(Directory.Exists(paths.BackupDir));
    }

    [Fact]
    public void WorkflowBegin_WithForeignMarker_DoesNotCreateSecondSharedMarker()
    {
        var paths = new DataPaths(_root);
        var foreign = WriteForeignMarker(paths, "PC-B");
        var txDir = Path.Combine(_root, "_ditm", "transactions");

        Assert.Throws<IOException>(() =>
        {
            using var tx = WorkflowTransactionService.Begin(paths, "local-attempt");
        });

        var markers = Directory.EnumerateFiles(txDir, "*.pending.json", SearchOption.TopDirectoryOnly).ToList();
        Assert.Single(markers);
        Assert.Equal(Path.GetFullPath(foreign), Path.GetFullPath(markers[0]));
        Assert.Null(WorkflowTransactionService.ActiveId);
    }

    [Fact]
    public void StorageMaintenance_ForeignTransaction_BlocksCleanupBeforeAnySharedMutation()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var technicalDir = Path.Combine(_root, "_ditm", "journal", "device-a");
        Directory.CreateDirectory(technicalDir);
        var staleTemp = Path.Combine(technicalDir, "orphan.tmp");
        File.WriteAllText(staleTemp, "must-survive");
        File.SetLastWriteTimeUtc(staleTemp, DateTime.UtcNow.AddDays(-3));
        WriteForeignMarker(paths, "PC-B");

        Assert.Throws<IOException>(() => StorageMaintenanceService.Run(
            paths,
            new LockService(),
            new NotificaRepository(paths),
            DateTime.Now,
            Path.Combine(_root, "local-cache"),
            vacuumLocalSqlite: false));

        Assert.True(File.Exists(staleTemp));
        Assert.Equal("must-survive", File.ReadAllText(staleTemp));
    }

    private static string WriteForeignMarker(DataPaths paths, string device)
    {
        var txDir = Path.Combine(paths.Root, "_ditm", "transactions");
        Directory.CreateDirectory(txDir);
        var marker = new WorkflowTransactionMarker
        {
            Id = Guid.NewGuid(),
            StartedUtc = DateTime.UtcNow,
            Device = device,
            Description = "test-foreign-transaction"
        };
        var path = Path.Combine(txDir, $"{marker.Id:N}.pending.json");
        JsonIo.WriteAtomic(path, marker);
        return path;
    }
}
