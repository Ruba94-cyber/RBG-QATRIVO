using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PhaseDBootstrapBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseD_Boundary_" + Guid.NewGuid().ToString("N"));

    public PhaseDBootstrapBoundaryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ManifestSenzaReady_BloccaQualunqueScritturaNormale()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterJournalService.EnsureInitialized(paths);
        Assert.True(File.Exists(PhaseDMasterJournalService.ManifestPath(_root)));
        Assert.False(PhaseDMasterBootstrapService.IsReady(_root));

        Assert.False(WorkspaceWritePolicy.CanWrite(paths));
        var error = Assert.Throws<IOException>(() =>
            WorkspaceWritePolicy.EnsureCanWrite(paths, "Salvataggio master legacy"));
        Assert.Contains("Phase D", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManifestSenzaReady_PuoEssereRecuperatoSoloDaEnsureReady()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterJournalService.EnsureInitialized(paths);
        Assert.False(PhaseDMasterBootstrapService.IsReady(_root));

        PhaseDMasterBootstrapService.EnsureReady(paths);

        Assert.True(PhaseDMasterBootstrapService.IsReady(_root));
        Assert.True(WorkspaceWritePolicy.CanWrite(paths));
    }

    [Fact]
    public void ManifestSenzaReady_WriterPhaseDInvocatoFuoriBootstrapRestaBloccato()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterJournalService.EnsureInitialized(paths);

        var error = Assert.Throws<IOException>(() =>
            PhaseDMasterJournalService.EnsureInitialized(paths));

        Assert.Contains("migrazione master Phase D incompleta", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(PhaseDMasterBootstrapService.IsReady(_root));
    }
}
