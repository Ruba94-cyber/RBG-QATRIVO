using System.Globalization;
using System.Text;

namespace TestLoom.Core;

/// <summary>
/// Exports general-purpose workspace data without depending on UI, office
/// automation, private integrations or external libraries.
/// </summary>
public static class WorkspaceCsvReport
{
    public static string ExportTestCases(WorkspaceDocument workspace, bool includeArchived = false)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        WorkspaceStore.Validate(workspace);

        var output = new StringBuilder();
        AppendRow(output, "Test ID", "Title", "Template", "Description", "Archived",
            "Latest outcome", "Executions", "Requirement IDs", "Created UTC");

        var requirementKeys = workspace.Requirements.ToDictionary(r => r.Id, r => r.Key);
        foreach (var test in workspace.TestCases.Where(t => includeArchived || !t.Archived))
        {
            AppendRow(output, test.Id.ToString(), test.Title, test.TemplateName,
                test.Description, test.Archived ? "Yes" : "No",
                test.LatestResult,
                test.Executions.Count.ToString(CultureInfo.InvariantCulture),
                string.Join("; ", test.RequirementIds.Select(id => requirementKeys[id])),
                test.CreatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }
        return output.ToString();
    }

    /// <summary>Export ordered action/expected-result pairs using the same CSV escaping safeguards.</summary>
    public static string ExportTestSteps(WorkspaceDocument workspace, bool includeArchived = false)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        WorkspaceStore.Validate(workspace);

        var output = new StringBuilder();
        AppendRow(output, "Test ID", "Test title", "Step ID", "Step number", "Action", "Expected result");
        foreach (var test in workspace.TestCases.Where(t => includeArchived || !t.Archived))
        {
            for (var index = 0; index < test.Steps.Count; index++)
            {
                var step = test.Steps[index];
                AppendRow(output, test.Id.ToString(), test.Title, step.Id.ToString(),
                    (index + 1).ToString(CultureInfo.InvariantCulture),
                    step.Action, step.ExpectedResult);
            }
        }
        return output.ToString();
    }

    public static string ExportRequirements(WorkspaceDocument workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var coverage = RequirementCoverage.Calculate(workspace);

        var output = new StringBuilder();
        AppendRow(output, "Requirement ID", "Title", "Linked tests",
            "Passing tests", "Failing tests", "Blocked tests");

        foreach (var requirement in coverage.Items)
        {
            AppendRow(output, requirement.Key, requirement.Title,
                requirement.LinkedTests.ToString(CultureInfo.InvariantCulture),
                requirement.PassingTests.ToString(CultureInfo.InvariantCulture),
                requirement.FailingTests.ToString(CultureInfo.InvariantCulture),
                requirement.BlockedTests.ToString(CultureInfo.InvariantCulture));
        }
        return output.ToString();
    }

    public static string ExportDefects(WorkspaceDocument workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        WorkspaceStore.Validate(workspace);

        var titleByTestId = workspace.TestCases.ToDictionary(t => t.Id, t => t.Title);
        var output = new StringBuilder();
        AppendRow(output, "Defect ID", "Title", "Description", "Severity",
            "Status", "Linked test ID", "Linked test title", "Created UTC");
        foreach (var defect in workspace.Defects)
        {
            AppendRow(output, defect.Key, defect.Title, defect.Description,
                defect.Severity.ToString(), defect.Status.ToString(),
                defect.TestCaseId?.ToString() ?? "",
                defect.TestCaseId is { } linkedId ? titleByTestId[linkedId] : "",
                defect.CreatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }
        return output.ToString();
    }

    private static void AppendRow(StringBuilder builder, params string?[] values)
    {
        builder.AppendJoin(",", values.Select(EscapeCell));
        builder.Append("\r\n");
    }

    private static string EscapeCell(string? value)
    {
        value ??= "";
        // Quoting alone does not stop spreadsheet programs evaluating formulas.
        // A leading apostrophe forces suspicious user data to be treated as text.
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
