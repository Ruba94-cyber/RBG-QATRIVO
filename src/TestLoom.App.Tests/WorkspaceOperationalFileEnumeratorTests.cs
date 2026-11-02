using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class WorkspaceOperationalFileEnumeratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "TestLoom_OperationalFiles_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void IncludeSoloFileGestiti_EscludeAllegatiLegacy()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var nc = paths.NonConformitaFile("1");
        var cr = paths.ChangeRequestFile("1");
        File.WriteAllText(nc, "{}");
        File.WriteAllText(cr, "{}");
        File.WriteAllText(paths.RequisitiFile, "[]");
        File.WriteAllText(Path.Combine(_root, "private-unrelated.json"), "{}");
        var legacyDir = Path.Combine(paths.NonConformitaDir, "allegati", "legacy");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "nc_2.json"), "{ sensitive }");
        var otherDir = Path.Combine(paths.AllegatiRootDir, "test", "original");
        Directory.CreateDirectory(otherDir);
        File.WriteAllText(Path.Combine(otherDir, "testcase_1.json"), "{ online only }");

        var selected = WorkspaceOperationalFileEnumerator.EnumerateManagedJson(_root).ToArray();

        Assert.Contains(nc, selected);
        Assert.Contains(cr, selected);
        Assert.Contains(paths.RequisitiFile, selected);
        Assert.DoesNotContain(selected, f => f.Contains("allegati", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(selected, f => f.Contains("private-unrelated"));
        Assert.Equal(3, selected.Length);
    }

    [Fact]
    public void IgnoraInteroAlberoDiTerziEMantieneLayoutGerarchiciTest()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var thirdParty = Path.Combine(_root, "AltraApplicazione", "dati", "testcases");
        Directory.CreateDirectory(thirdParty);
        for (var i = 0; i < 120; i++)
            File.WriteAllText(Path.Combine(thirdParty, $"testcase_{i}.json"), "{}");

        var testDir = Path.Combine(paths.TestCasesDir,
            "Gamma", "Veicolo", "Componente");
        Directory.CreateDirectory(testDir);
        var test = Path.Combine(testDir, "testcase_TC-LEGACY.json");
        File.WriteAllText(test, "{}");
        var recovery = Path.Combine(paths.RecoveryBinDir, "recovery_test.json");
        File.WriteAllText(recovery, "{}");
        var thirdPartyFile = Path.Combine(thirdParty, "testcase_10.json");
        var snapshot = WorkspaceOperationalFileEnumerator.EnumerateManagedJson(_root).ToArray();

        Assert.Contains(test, snapshot);
        Assert.Contains(recovery, snapshot);
        Assert.DoesNotContain(thirdPartyFile, snapshot);
        Assert.DoesNotContain(snapshot, item => item.Contains("AltraApplicazione"));
        Assert.Equal(2, snapshot.Length);
        Assert.True(File.Exists(thirdPartyFile));
    }

    [Fact]
    public void NessunaDirectoryNuovaEdEnumerazioneCancellabile()
    {
        Assert.Empty(WorkspaceOperationalFileEnumerator.EnumerateManagedJson(_root));
        Assert.False(Directory.Exists(_root));
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        File.WriteAllText(paths.RequisitiFile, "[]");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            WorkspaceOperationalFileEnumerator.EnumerateManagedJson(_root, cts.Token).ToArray());
        Assert.Equal("[]", File.ReadAllText(paths.RequisitiFile));
    }

    [Fact]
    public void Inventario5000NC_RestituisceTuttiSenzaCaricarePayload()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        for (int i = 0; i < 5000; i++)
            File.WriteAllText(paths.NonConformitaFile(i.ToString("D6")), "{}");

        var selected = WorkspaceOperationalFileEnumerator.EnumerateManagedJson(_root)
            .Where(x => x.Contains("nc_", StringComparison.OrdinalIgnoreCase)).Count();

        Assert.Equal(5000, selected);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
