using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class WorkspaceCsvReportTests
{
    [Fact]
    public void ExportIncludesRealExecutionAndRequirementLinks()
    {
        var workspace = WorkspaceStore.Create("Demo", "software-qa");
        var requirement = new WorkspaceRequirement { Key = "REQ-1", Title = "Working login" };
        workspace.Requirements.Add(requirement);
        var test = new WorkspaceTestCase { Title = "Login flow", TemplateName = "Smoke test" };
        test.RequirementIds.Add(requirement.Id);
        test.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Passed });
        workspace.TestCases.Add(test);

        var cases = WorkspaceCsvReport.ExportTestCases(workspace);
        var requirements = WorkspaceCsvReport.ExportRequirements(workspace);
        Assert.Contains("\"Login flow\"", cases);
        Assert.Contains("\"Passed\"", cases);
        Assert.Contains("\"REQ-1\"", cases);
        Assert.Contains("\"REQ-1\",\"Working login\",\"1\",\"1\"", requirements);
    }

    [Fact]
    public void CsvEscapesQuotesNewlinesAndSpreadsheetFormulaPrefixes()
    {
        var workspace = WorkspaceStore.Create("Synthetic", "blank");
        workspace.TestCases.Add(new WorkspaceTestCase
        {
            Title = "=HYPERLINK(\"https://example.invalid\",\"open\")",
            Description = "first,\"quoted\"\nsecond"
        });
        var csv = WorkspaceCsvReport.ExportTestCases(workspace);
        Assert.Contains("\"'=HYPERLINK(\"\"", csv);
        Assert.Contains("first,\"\"quoted\"\"\nsecond", csv);
        Assert.EndsWith("\r\n", csv);
    }

    [Fact]
    public void ArchivedTestsAreExcludedByDefaultButCanBeIncludedExplicitly()
    {
        var workspace = WorkspaceStore.Create("Synthetic", "blank");
        workspace.TestCases.Add(new WorkspaceTestCase { Title = "Active test" });
        workspace.TestCases.Add(new WorkspaceTestCase { Title = "Archived test", Archived = true });
        Assert.DoesNotContain("Archived test", WorkspaceCsvReport.ExportTestCases(workspace));
        Assert.Contains("Archived test", WorkspaceCsvReport.ExportTestCases(workspace, includeArchived: true));
    }
}
