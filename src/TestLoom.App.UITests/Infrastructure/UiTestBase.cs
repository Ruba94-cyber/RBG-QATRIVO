namespace TestLoom.App.UITests.Infrastructure;

/// <summary>
/// Base comune per un test UI FlaUI: crea un <see cref="TestWorkspace"/> isolato, avvia TestLoom.exe puntato su
/// di esso e garantisce che entrambi vengano smaltiti anche se il test fallisce a metà. Le sottoclassi
/// scelgono se partire già loggate (<see cref="TestWorkspace.CreateLoggedIn"/>, il caso comune) o da schermata
/// di login (<see cref="TestWorkspace.CreateLoggedOut"/>, solo per i test che esercitano il login stesso) —
/// vedi <see cref="Tests.LoginTests"/>.
/// </summary>
public abstract class UiTestBase : IDisposable
{
    protected readonly TestWorkspace Workspace;
    protected readonly TestLoomApplication App;

    protected UiTestBase(TestWorkspace workspace)
    {
        Workspace = workspace;
        App = TestLoomApplication.Launch(Workspace);
    }

    /// <summary>Chiamare dal blocco catch di ogni [Fact]: cattura screenshot/albero UI/eccezione (Fase 10)
    /// sotto TestResults/UIFailures/&lt;test&gt;/&lt;timestamp&gt;/ e ristampa il percorso nell'eccezione
    /// rilanciata, così compare nell'output del test runner senza dover aprire l'artifact a parte.</summary>
    protected void CaptureFailureAndRethrow(
        string testName, Exception ex, string? step = null,
        string? searchedAutomationId = null, string? searchedName = null, string? searchedControlType = null)
    {
        var dir = FailureArtifacts.Capture(App, testName, ex, step, searchedAutomationId, searchedName, searchedControlType);
        throw new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
    }

    public virtual void Dispose()
    {
        App.Dispose();
        Workspace.Dispose();
    }
}
