namespace TestLoom.Core;

/// <summary>
/// Dashboard numbers derived from the workspace on demand (never persisted), so they cannot drift from the data.
/// Only active (non-archived) tests count, using the latest execution of each test.
/// </summary>
public static class WorkspaceStatistics
{
    public static WorkspaceStatisticsReport Calculate(WorkspaceDocument workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        WorkspaceStore.Validate(workspace);

        var active = workspace.TestCases.Where(t => !t.Archived).ToArray();
        var passed = active.Count(t => t.Executions.LastOrDefault()?.Result == ExecutionResult.Passed);
        var failed = active.Count(t => t.Executions.LastOrDefault()?.Result == ExecutionResult.Failed);
        var blocked = active.Count(t => t.Executions.LastOrDefault()?.Result == ExecutionResult.Blocked);
        var notRun = active.Length - passed - failed - blocked;
        var executed = passed + failed + blocked;

        var coverage = RequirementCoverage.Calculate(workspace);
        var openDefects = workspace.Defects
            .Where(d => d.Status is WorkspaceDefectStatus.Open or WorkspaceDefectStatus.InProgress)
            .ToArray();

        return new WorkspaceStatisticsReport(
            active.Length, workspace.TestCases.Count - active.Length,
            passed, failed, blocked, notRun,
            executed == 0 ? 0 : Math.Round(100.0 * passed / executed, 1),
            active.Length == 0 ? 0 : Math.Round(100.0 * executed / active.Length, 1),
            coverage.TotalRequirements,
            coverage.LinkedRequirements,
            coverage.TotalRequirements == 0 ? 0 : Math.Round(100.0 * coverage.LinkedRequirements / coverage.TotalRequirements, 1),
            workspace.Defects.Count,
            openDefects.Length,
            openDefects.Count(d => d.Severity == WorkspaceDefectSeverity.Critical),
            Enum.GetValues<WorkspaceDefectSeverity>().ToDictionary(s => s, s => openDefects.Count(d => d.Severity == s)));
    }
}

public sealed record WorkspaceStatisticsReport(
    int ActiveTests,
    int ArchivedTests,
    int Passed,
    int Failed,
    int Blocked,
    int NotRun,
    double PassRatePercent,
    double ExecutionProgressPercent,
    int Requirements,
    int RequirementsWithTests,
    double RequirementCoveragePercent,
    int Defects,
    int OpenDefects,
    int OpenCriticalDefects,
    IReadOnlyDictionary<WorkspaceDefectSeverity, int> OpenDefectsBySeverity)
{
    /// <summary>One-line summary for the status bar.</summary>
    public string Summary =>
        $"{ActiveTests} tests · {Passed} passed · {Failed} failed · {Blocked} blocked · {NotRun} not run · " +
        $"pass rate {PassRatePercent:0.#}% · coverage {RequirementCoveragePercent:0.#}% · open defects {OpenDefects}";
}
