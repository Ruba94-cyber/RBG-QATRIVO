using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Tests del piano, senza eseguire attrib.exe e senza mai contattare OneDrive.
/// Il pinning viene effettuato solo su richiesta esplicita dell'utente.
/// </summary>
public sealed class LocalDataPinServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_DataPin_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Plan_IncludeTuttiIDominiMaEscludeAllegatiEDatiEstranei()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var test = paths.TestCaseFile("Componente-A", "TC-1");
        Directory.CreateDirectory(Path.GetDirectoryName(test)!);
        File.WriteAllText(test, "{}");

        var nc = paths.NonConformitaFile("1");
        var cr = paths.ChangeRequestFile("1");
        var segnalazione = paths.SegnalazioneFile(Guid.NewGuid());
        File.WriteAllText(nc, "{}");
        File.WriteAllText(cr, "{}");
        File.WriteAllText(segnalazione, "{}");
        File.WriteAllText(paths.RequisitiFile, "[]");
        File.WriteAllText(paths.AccessoConfigFile, "{}");

        var foreign = Path.Combine(_root, "third-party.json");
        File.WriteAllText(foreign, "{}");
        var attachment = Path.Combine(paths.AllegatiRootDir, "test", "evidence.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(attachment)!);
        File.WriteAllText(attachment, "binary");
        var legacyAttachment = Path.Combine(paths.TestCaseDir("Componente-A"), "allegati", "TC-1", "testcase_not_data.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyAttachment)!);
        File.WriteAllText(legacyAttachment, "{}");

        var plan = LocalDataPinService.BuildPlan(paths);
        var pathsInPlan = plan.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // I documenti Test vengono pinnati tramite wildcard per la propria directory,
        // senza invocare un processo attrib per ogni singolo file.
        Assert.Contains(Path.Combine(Path.GetDirectoryName(test)!, "testcase_*.json"), pathsInPlan);
        Assert.Contains(Path.Combine(paths.NonConformitaDir, "nc_*.json"), pathsInPlan);
        Assert.Contains(Path.Combine(paths.ChangeRequestDir, "cr_*.json"), pathsInPlan);
        Assert.Contains(Path.Combine(paths.SegnalazioniDir, "segnalazione_*.json"), pathsInPlan);
        Assert.Contains(paths.RequisitiFile, pathsInPlan);
        Assert.Contains(paths.AccessoConfigFile, pathsInPlan);
        Assert.DoesNotContain(foreign, pathsInPlan);
        Assert.DoesNotContain(attachment, pathsInPlan);
        Assert.DoesNotContain(legacyAttachment, pathsInPlan);
        Assert.DoesNotContain(paths.AllegatiRootDir, pathsInPlan);
    }

    [Fact]
    public void Plan_IncludesPhaseDTechnicalDataWithoutPinningEntireRoot()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var phaseD = Path.Combine(paths.Root, "_ditm", "master-v2", "journal", "device", "current.ndjson");
        Directory.CreateDirectory(Path.GetDirectoryName(phaseD)!);
        File.WriteAllText(phaseD, "");

        var plan = LocalDataPinService.BuildPlan(paths);

        Assert.Contains(plan, p => p.Path == Path.Combine(Path.GetDirectoryName(phaseD)!, "*.ndjson") &&
            !p.Recursive);
        Assert.DoesNotContain(plan, p => p.Path == paths.Root);
        Assert.DoesNotContain(plan, p => p.Path == paths.AllegatiRootDir);
        Assert.DoesNotContain(plan, p => p.Path == paths.BackupDir);
    }

    [Fact]
    public void Plan_NonPinnaCartelleRicorsiveConAllegatiLegacy()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var nc = paths.NonConformitaFile("2");
        File.WriteAllText(nc, "{}");
        var cr = paths.ChangeRequestFile("2");
        File.WriteAllText(cr, "{}");
        var test = paths.TestCaseFile("Componente-A", "TC-2");
        Directory.CreateDirectory(Path.GetDirectoryName(test)!);
        File.WriteAllText(test, "{}");

        foreach (var baseDir in new[] {
            paths.TestCaseDir("Componente-A"), paths.NonConformitaDir,
            paths.ChangeRequestDir, paths.CestinoDir, Path.Combine(_root, "_ditm")
        })
        {
            var attachmentDir = Path.Combine(baseDir, "allegati", "vecchi");
            Directory.CreateDirectory(attachmentDir);
            File.WriteAllText(Path.Combine(attachmentDir, "testcase_private.json"), "binary");
            File.WriteAllText(Path.Combine(attachmentDir, "nc_private.json"), "binary");
            File.WriteAllText(Path.Combine(attachmentDir, "cr_private.json"), "binary");
            File.WriteAllText(Path.Combine(attachmentDir, "segment-archive.ndjson"), "binary");
        }
        var plan = LocalDataPinService.BuildPlan(paths);

        Assert.NotEmpty(plan);
        Assert.All(plan, p => Assert.False(p.Recursive));
        Assert.DoesNotContain(plan, p => p.Path.Contains("allegati", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan, p => p.Path == _root || p.Path == paths.TestCasesDir);
        Assert.Contains(plan, p => p.Path == Path.Combine(paths.NonConformitaDir, "nc_*.json"));
        Assert.Contains(plan, p => p.Path == Path.Combine(paths.ChangeRequestDir, "cr_*.json"));
        Assert.Contains(plan, p => p.Path == Path.Combine(Path.GetDirectoryName(test)!, "testcase_*.json"));
    }

    [Fact]
    public void Plan_NonIncludeJsonEstraneiONeRootONelSottodominio()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        File.WriteAllText(Path.Combine(paths.NonConformitaDir, "personale.json"), "{}");
        File.WriteAllText(Path.Combine(paths.ChangeRequestDir, "note.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "private.json"), "{}");
        var plan = LocalDataPinService.BuildPlan(paths);
        Assert.DoesNotContain(plan, p => p.Path.Contains("private", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan, p => p.Path.Contains("personale", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan, p => p.Path.Contains("note.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan, p => p.Recursive);
    }

    [Fact]
    public void Plan_NonModificaFileOAttributi()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        File.WriteAllText(paths.RequisitiFile, "[]");
        var before = File.ReadAllBytes(paths.RequisitiFile);
        var when = File.GetLastWriteTimeUtc(paths.RequisitiFile);

        _ = LocalDataPinService.BuildPlan(paths);

        Assert.Equal(before, File.ReadAllBytes(paths.RequisitiFile));
        Assert.Equal(when, File.GetLastWriteTimeUtc(paths.RequisitiFile));
    }

    [Fact]
    public void Plan_RequiresExistingWorkspace()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            LocalDataPinService.BuildPlan(new DataPaths(Path.Combine(_root, "missing"))));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
