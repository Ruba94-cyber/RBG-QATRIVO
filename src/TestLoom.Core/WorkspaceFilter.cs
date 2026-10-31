namespace TestLoom.Core;

public enum OutcomeFilter { Any, NotRun, Passed, Failed, Blocked }

/// <summary>Search and filter over test cases; pure and non-mutating so views can reapply it on every keystroke.</summary>
public sealed record TestCaseFilter(
    string? Text = null,
    OutcomeFilter Outcome = OutcomeFilter.Any,
    bool IncludeArchived = false,
    Guid? RequirementId = null,
    string? TemplateName = null)
{
    public static IReadOnlyList<WorkspaceTestCase> Apply(WorkspaceDocument workspace, TestCaseFilter filter)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(filter);
        var terms = (filter.Text ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var requirementKeys = workspace.Requirements.ToDictionary(r => r.Id, r => r.Key + " " + r.Title);

        return workspace.TestCases.Where(test =>
        {
            if (test.Archived && !filter.IncludeArchived) return false;
            if (!string.IsNullOrWhiteSpace(filter.TemplateName) &&
                !string.Equals(test.TemplateName, filter.TemplateName, StringComparison.OrdinalIgnoreCase)) return false;
            if (filter.RequirementId is { } rid && !test.RequirementIds.Contains(rid)) return false;
            if (!MatchesOutcome(test, filter.Outcome)) return false;
            if (terms.Length == 0) return true;

            var haystack = string.Join(' ',
                test.Title, test.Description, test.TemplateName,
                string.Join(' ', test.Steps.Select(s => s.Action + " " + s.ExpectedResult)),
                string.Join(' ', test.RequirementIds.Select(id => requirementKeys.GetValueOrDefault(id, ""))));
            return terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
        }).ToArray();
    }

    private static bool MatchesOutcome(WorkspaceTestCase test, OutcomeFilter outcome)
    {
        var last = test.Executions.LastOrDefault()?.Result;
        return outcome switch
        {
            OutcomeFilter.Any => true,
            OutcomeFilter.NotRun => last is null,
            OutcomeFilter.Passed => last == ExecutionResult.Passed,
            OutcomeFilter.Failed => last == ExecutionResult.Failed,
            OutcomeFilter.Blocked => last == ExecutionResult.Blocked,
            _ => true
        };
    }
}
