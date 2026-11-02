using System.IO.Compression;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

[Collection("BackupWriteGate")] // BackupService.WriteGate è statico: niente esecuzione parallela fra queste classi
public sealed class PhaseDBackupSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseD_Backup_" + Guid.NewGuid().ToString("N"));
    // Radice locale di test (mai il vero %LOCALAPPDATA%): stesso meccanismo di override già usato da
    // LocalTestIndexStore per i suoi test, passato qui direttamente a BackupService.CreaOra.
    private readonly string _localAppData = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseD_Backup_LocalAppData_" + Guid.NewGuid().ToString("N"));

    public PhaseDBackupSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { if (Directory.Exists(_localAppData)) Directory.Delete(_localAppData, recursive: true); } catch { }
    }

    [Fact]
    public void Backup_PostCutover_IncludeSourceOfTruthPhaseDEscludeArtefattiEstranei()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);
        var device = "11111111111111111111111111111111";
        var entity = new Funzionalita { Uid = Guid.NewGuid(), Nome = "Backup-PD" };
        var payload = JsonSerializer.Serialize(entity, JsonIo.CompactOptions);
        var mutation = new PhaseDMasterMutation
        {
            Catalog = "funzionalita",
            EntityUid = entity.Uid,
            Operation = PhaseDMasterMutationOperation.Upsert,
            VersionVector = new Dictionary<string, long> { [device] = 1 },
            PayloadJson = payload,
            PayloadSha256 = PhaseDMasterMutation.HashPayload(payload),
            BusinessKey = entity.Nome
        };
        var batch = new PhaseDMasterMutationBatch
        {
            DeviceId = device,
            DeviceSequence = 1,
            Mutations = new List<PhaseDMasterMutation> { mutation }
        };
        batch.Validate();
        var current = PhaseDMasterJournalService.CurrentPath(_root, device);
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllText(current, JsonSerializer.Serialize(batch, JsonIo.CompactOptions) + Environment.NewLine);

        var foreign = Path.Combine(PhaseDMasterJournalService.PhaseDRoot(_root), "foreign.bin");
        File.WriteAllText(foreign, "non-testloom");
        var unrelatedTechnical = Path.Combine(_root, "_ditm", "unrelated.json");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelatedTechnical)!);
        File.WriteAllText(unrelatedTechnical, "{}");

        var zipPath = BackupService.CreaOra(paths, new DateTime(2026, 9, 5, 14, 0, 0), _localAppData);

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("_ditm/master-v2/manifest.json", entries);
        Assert.Contains("_ditm/master-v2/ready.json", entries);
        Assert.Contains($"_ditm/master-v2/journal/{device}/current.ndjson", entries);
        Assert.DoesNotContain("_ditm/master-v2/foreign.bin", entries);
        Assert.DoesNotContain("_ditm/unrelated.json", entries);
        Assert.Contains("snapshot-manifest.txt", entries);
    }

    [Fact]
    public void Backup_PhaseDPresenteMaNonReady_FallisceSenzaPubblicareZip()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterJournalService.EnsureInitialized(paths);
        Assert.False(PhaseDMasterBootstrapService.IsReady(_root));

        Assert.Throws<IOException>(() => BackupService.CreaOra(paths, new DateTime(2026, 9, 5, 14, 1, 0), _localAppData));

        Assert.False(Directory.Exists(paths.BackupDir) &&
                     Directory.EnumerateFiles(paths.BackupDir, "TestLoom_snapshot_*.zip").Any());
    }
}
