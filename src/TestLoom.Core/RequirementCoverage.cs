namespace TestLoom.Core;

/// <summary>
/// Domain-neutral traceability summary based on the latest execution of each
/// test. Results are derived and are never written as duplicate persisted data.
/// </summary>
public static class RequirementCoverage
{
    public static RequirementCoverageReport Calculate(WorkspaceDocument workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        WorkspaceStore.Validate(workspace);

        var details = workspace.Requirements.Select(requirement =>
        {
            var linked = workspace.TestCases
                .Where(test => !test.Archived && test.RequirementIds.Contains(requirement.Id))
                .ToArray();
            return new RequirementCoverageItem(
                requirement.Id,
                requirement.Key,
                requirement.Title,
                linked.Length,
                linked.Count(test => test.Executions.LastOrDefault()?.Result == ExecutionResult.Passed),
                linked.Count(test => test.Executions.LastOrDefault()?.Result == ExecutionResult.Failed),
                linked.Count(test => test.Executions.LastOrDefault()?.Result == ExecutionResult.Blocked));
        }).ToArray();

        return new RequirementCoverageReport(
            details.Length,
            details.Count(item => item.LinkedTests > 0),
            details.Count(item => item.PassingTests > 0),
            details);
    }
}

public sealed record RequirementCoverageItem(
    Guid RequirementId,
    string Key,
    string Title,
    int LinkedTests,
    int PassingTests,
    int FailingTests,
    int BlockedTests);

public sealed record RequirementCoverageReport(
    int TotalRequirements,
    int LinkedRequirements,
    int RequirementsWithPassingTest,
    IReadOnlyList<RequirementCoverageItem> Items);
