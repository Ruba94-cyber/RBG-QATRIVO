using System.Security.Cryptography;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class WorkspaceDiagnosticSnapshotServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_Integrity_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_IntegrityBackups_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CartellaAssente_AvvisoSegueLaLinguaCorrente()
    {
        var loc = LocalizationService.Instance;
        var originale = loc.Lingua;
        try
        {
            loc.Lingua = "it";
            var it = WorkspaceDiagnosticSnapshotService.Collect(_root, localBackupBaseDirectory: _local);
            Assert.Contains("Cartella dati non raggiungibile. Nessun dato modificato.", it.Warnings);

            loc.Lingua = "fr";
            var fr = WorkspaceDiagnosticSnapshotService.Collect(_root, localBackupBaseDirectory: _local);
            Assert.Contains("Dossier de données inaccessible. Aucune donnée modifiée.", fr.Warnings);
            Assert.DoesNotContain(fr.Warnings, w => w.Contains("Cartella dati"));
        }
        finally { loc.Lingua = originale; }
    }

    [Fact]
    public void CartellaAssente_NonCreaNessunaStruttura()
    {
        var result = WorkspaceDiagnosticSnapshotService.Collect(_root, localBackupBaseDirectory: _local);
        Assert.False(result.Available);
        Assert.Equal(0, result.OperationalFiles);
        Assert.False(Directory.Exists(_root));
        Assert.False(Directory.Exists(_local));
        Assert.Contains("NON certificano", result.Format());
    }

    [Fact]
    public void Inventario_IncludeOperativiEJournal_MaNonAllegatiComeDati()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        File.WriteAllText(paths.RequisitiFile, "[]");
        File.WriteAllText(paths.NonConformitaFile("1"), "{}");
        File.WriteAllText(paths.ChangeRequestFile("1"), "{}");
        var test = paths.TestCaseFile("Componente_A", "2");
        Directory.CreateDirectory(Path.GetDirectoryName(test)!);
        File.WriteAllText(test, "{}");
        var current = Path.Combine(_root, "_ditm", "master-v2", "journal", "dev", "current.ndjson");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllText(current, "{}");
        var allegato = Path.Combine(paths.AllegatiRootDir, "test", "prova.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(allegato)!);
        File.WriteAllText(allegato, "binario");
        var legacy = Path.Combine(paths.TestCaseDir("Componente_A"), "allegati", "testcase_private.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "binario");
        File.WriteAllText(Path.Combine(_root, "estraneo.json"), "{}");

        var before = File.ReadAllBytes(paths.RequisitiFile);
        var result = WorkspaceDiagnosticSnapshotService.Collect(_root, localBackupBaseDirectory: _local);

        Assert.True(result.Available);
        Assert.Equal(5, result.OperationalFiles);
        Assert.Equal(1, result.Attachments); // cartella allegati dedicata, senza hydratare
        Assert.Contains("OneDrive", result.Format());
        Assert.Equal(before, File.ReadAllBytes(paths.RequisitiFile));
        Assert.Equal("binario", File.ReadAllText(legacy));
        Assert.False(Directory.Exists(_local));
    }

    [Fact]
    public void ContaBackupEBozzeSoloDellaPostazione()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var zipDir = WorkspaceLocalPaths.GetBackupDirectory(_root, _local);
        Directory.CreateDirectory(zipDir);
        File.WriteAllText(Path.Combine(zipDir, "TestLoom_snapshot_20261010.zip"), "dummy");
        File.WriteAllText(Path.Combine(zipDir, "estraneo.zip"), "dummy");
        var draft = FailedSaveDraftService.Save(_root, "change-request", Guid.NewGuid(), 0,
            "QA", new { Id = "CR-QA" }, "offline");
        try
        {
            var report = WorkspaceDiagnosticSnapshotService.Collect(_root,
                localBackupBaseDirectory: _local);

            Assert.Equal(1, report.LocalDrafts);
            Assert.Equal(1, report.LocalSnapshots);
            Assert.NotNull(report.LastSnapshotUtc);
            Assert.True(File.Exists(draft));
        }
        finally { try { File.Delete(draft); } catch { } }
    }

    [Fact]
    public void ScalaMilleDocumentiNC_NessunLimiteArtificialeeUnSoloPatternPinning()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        for (var i = 0; i < 1024; i++)
            File.WriteAllText(paths.NonConformitaFile(i.ToString("D5")), "{}");

        var report = WorkspaceDiagnosticSnapshotService.Collect(_root,
            localBackupBaseDirectory: _local);
        var pinPlan = LocalDataPinService.BuildPlan(paths);

        Assert.Equal(1024, report.OperationalFiles);
        Assert.Single(pinPlan.Where(t =>
            t.Path == Path.Combine(paths.NonConformitaDir, "nc_*.json")));
        Assert.All(pinPlan, t => Assert.False(t.Recursive));
    }

    [Fact]
    public void DiagnosticaCancellata_NonEliminaFileOAllegati()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        File.WriteAllText(paths.RequisitiFile, "[]");
        var original = File.ReadAllBytes(paths.RequisitiFile);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            WorkspaceDiagnosticSnapshotService.Collect(_root,
                localBackupBaseDirectory: _local, cancellationToken: cancellation.Token));
        Assert.Equal(original, File.ReadAllBytes(paths.RequisitiFile));
    }

    [Fact]
    public void ManifestPhaseDNonReady_ProduceAvvisoSenzaAttivareBootstrap()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var manifest = Path.Combine(_root, "_ditm", "master-v2", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "{}");

        var report = WorkspaceDiagnosticSnapshotService.Collect(_root, localBackupBaseDirectory: _local);

        Assert.True(report.PhaseDManifestPresent);
        Assert.False(report.PhaseDReadyPresent);
        Assert.Contains(report.Warnings, s => s.Contains("non pronto"));
        Assert.False(File.Exists(Path.Combine(_root, "_ditm", "master-v2", "ready.json")));
    }

    [Fact]
    public void Report_RispettaBloccoLocaleEBackupEsternoOpzionale()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var external = Path.Combine(_local, "esterno");
        Directory.CreateDirectory(external);
        WorkspaceReadOnlyService.SetEnabled(_root, true);
        try
        {
            var result = WorkspaceDiagnosticSnapshotService.Collect(_root,
                externalBackupDirectory: external, localBackupBaseDirectory: _local);
            Assert.True(result.ViewerMode);
            Assert.True(result.ExternalDestinationPresent);
            Assert.Contains("attiva", result.Format());
            Assert.Contains("contenuto non verificato", result.Format());
        }
        finally { WorkspaceReadOnlyService.SetEnabled(_root, false); }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        try { if (Directory.Exists(_local)) Directory.Delete(_local, true); } catch { }
        try
        {
            var draftDir = FailedSaveDraftService.LocalDirectory(_root);
            if (Directory.Exists(draftDir)) Directory.Delete(draftDir, true);
        }
        catch { }
    }
}
