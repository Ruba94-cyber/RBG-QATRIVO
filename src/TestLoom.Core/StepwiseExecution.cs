namespace TestLoom.Core;

/// <summary>Combines per-step outcomes into the outcome of a whole execution.</summary>
public static class StepwiseExecution
{
    /// <summary>
    /// Any failed step fails the run; otherwise any blocked step blocks it; the run passes only when EVERY step of
    /// the test has a passing result. Returns null while steps are still unrecorded.
    /// </summary>
    public static ExecutionResult? Overall(WorkspaceTestCase test, IReadOnlyCollection<WorkspaceStepResult> results)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(results);
        var known = test.Steps.Select(s => s.Id).ToHashSet();
        var relevant = results.Where(r => known.Contains(r.StepId)).ToArray();
        if (relevant.Any(r => r.Result == ExecutionResult.Failed)) return ExecutionResult.Failed;
        if (relevant.Any(r => r.Result == ExecutionResult.Blocked)) return ExecutionResult.Blocked;
        return test.Steps.Count > 0 && known.All(id => relevant.Any(r => r.StepId == id && r.Result == ExecutionResult.Passed))
            ? ExecutionResult.Passed : null;
    }

    /// <summary>Creates a complete execution from step outcomes; fails if the outcome is not decided yet.</summary>
    public static WorkspaceExecution Build(WorkspaceTestCase test, IEnumerable<WorkspaceStepResult> results,
        string tester = "", string environment = "", string buildVersion = "", int durationSeconds = 0, string notes = "")
    {
        var list = results.GroupBy(r => r.StepId).Select(g => g.Last()).ToList();
        var overall = Overall(test, list) ?? throw new InvalidOperationException("Record a result for every step first.");
        return new WorkspaceExecution
        {
            Result = overall, StepResults = list, Tester = tester, Environment = environment,
            BuildVersion = buildVersion, DurationSeconds = durationSeconds, Notes = notes
        };
    }
}
