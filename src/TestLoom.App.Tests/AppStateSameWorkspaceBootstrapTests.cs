using TestLoom.App.Models;
using TestLoom.App.Services;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>Il login non deve ripetere il bootstrap gia' concluso dal costruttore di AppState.
/// Tutti i JSON e il config di questa prova vivono in directory temporanee dedicate.</summary>
public sealed class AppStateSameWorkspaceBootstrapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_SameWorkspace_" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public AppStateSameWorkspaceBootstrapTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ImpostaCartella_GiaInizializzata_NonRipeteLaRiconciliazione()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        for (var i = 0; i < 200; i++)
        {
            var test = new TestCase { Id = $"TC-{i:D5}", Componente = "BCM" };
            JsonIo.WriteAtomic(paths.TestCaseFile("BCM", test.Id), test);
        }

        using var state = TestStateFactory.Create();
        state.ImpostaCartella(_root);
        Assert.True(state.InizializzazioneWorkspaceCompletata);
        Assert.Equal(200, state.TestCases.GetAll().Count);
        var repository = state.TestCases;
        var eventiPrima = PerformanceDiagnosticsService.ReadRecent(repository.LocalWorkspaceDirectory)
            .Count(x => x.Operation == "workspace.bootstrap.totale");

        state.ImpostaCartella(_root + Path.DirectorySeparatorChar);

        Assert.Same(repository, state.TestCases);
        Assert.Equal(eventiPrima, PerformanceDiagnosticsService.ReadRecent(repository.LocalWorkspaceDirectory)
            .Count(x => x.Operation == "workspace.bootstrap.totale"));
        Assert.Equal(200, state.TestCases.GetAll().Count);
    }

    [Fact]
    public void ImpostaCartella_RitentaDopoUnBootstrapNonCompletato()
    {
        using var state = TestStateFactory.Create();
        state.ImpostaCartella(_root);
        Assert.False(state.InizializzazioneWorkspaceCompletata);

        new DataPaths(_root).EnsureStructure();
        state.ImpostaCartella(_root);

        Assert.True(state.InizializzazioneWorkspaceCompletata);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void BootstrapRipetuto_DuecentoTest_ConfrontaPrimaEDopo()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        for (var i = 0; i < 200; i++)
            JsonIo.WriteAtomic(paths.TestCaseFile("BCM", $"TC-{i:D5}"),
                new TestCase { Id = $"TC-{i:D5}", Componente = "BCM" });

        using var state = TestStateFactory.Create();
        state.ImpostaCartella(_root);
        var prima = System.Diagnostics.Stopwatch.StartNew();
        state.InizializzaCartella(_root); // stesso lavoro che ImpostaCartella faceva prima del guard
        prima.Stop();
        var dopo = System.Diagnostics.Stopwatch.StartNew();
        state.ImpostaCartella(_root);
        dopo.Stop();

        _output.WriteLine($"LOGIN_BOOTSTRAP|test=200|prima_ms={prima.Elapsed.TotalMilliseconds:F1}|dopo_ms={dopo.Elapsed.TotalMilliseconds:F1}");
        Assert.True(state.InizializzazioneWorkspaceCompletata);
        Assert.Equal(200, state.TestCases.GetAll().Count);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
