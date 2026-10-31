using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class RequirementCoverageTests
{
    [Fact]
    public void MissingRequirementsAndOutcomesAreTrackedWithoutInventingPasses()
    {
        var workspace = WorkspaceStore.Create("Quality", "hardware");
        var linked = new WorkspaceRequirement { Key = "REQ-001", Title = "Device starts" };
        var untouched = new WorkspaceRequirement { Key = "REQ-002", Title = "Battery endurance" };
        workspace.Requirements.AddRange(new[] { linked, untouched });

        var test = new WorkspaceTestCase { Title = "Power-on verification" };
        test.RequirementIds.Add(linked.Id);
        test.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Failed });
        test.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Passed });
        workspace.TestCases.Add(test);

        var result = RequirementCoverage.Calculate(workspace);
        Assert.Equal(2, result.TotalRequirements);
        Assert.Equal(1, result.LinkedRequirements);
        Assert.Equal(1, result.RequirementsWithPassingTest);
        Assert.Equal(1, result.Items[0].LinkedTests);
        Assert.Equal(1, result.Items[0].PassingTests);
        Assert.Equal(0, result.Items[0].FailingTests);
        Assert.Equal(0, result.Items[1].LinkedTests);
        Assert.Equal(0, result.Items[1].PassingTests);
    }

    [Fact]
    public void ArchivedCasesDoNotCountTowardActiveRequirementCoverage()
    {
        var workspace = WorkspaceStore.Create("Hardware validation", "hardware");
        var requirement = new WorkspaceRequirement { Key = "REQ-ARCH", Title = "Standby draw" };
        workspace.Requirements.Add(requirement);
        var test = new WorkspaceTestCase { Title = "Power idle", Archived = true };
        test.RequirementIds.Add(requirement.Id);
        test.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Passed });
        workspace.TestCases.Add(test);

        var report = RequirementCoverage.Calculate(workspace);
        Assert.Equal(0, report.LinkedRequirements);
        Assert.Equal(0, report.RequirementsWithPassingTest);
        test.Archived = false;
        Assert.Equal(1, RequirementCoverage.Calculate(workspace).RequirementsWithPassingTest);
    }

    [Fact]
    public void RequirementKeysMustBeUniqueIgnoringLetterCase()
    {
        var workspace = WorkspaceStore.Create("Quality", "blank");
        workspace.Requirements.Add(new WorkspaceRequirement { Key = "REQ-001", Title = "One" });
        workspace.Requirements.Add(new WorkspaceRequirement { Key = "req-001", Title = "Two" });
        Assert.Throws<InvalidDataException>(() => WorkspaceStore.Validate(workspace));
    }

    [Fact]
    public void MissingOrDuplicateRequirementLinksAreRejected()
    {
        var workspace = WorkspaceStore.Create("Quality", "blank");
        var test = new WorkspaceTestCase { Title = "Check" };
        test.RequirementIds.Add(Guid.NewGuid());
        workspace.TestCases.Add(test);
        Assert.Throws<InvalidDataException>(() => WorkspaceStore.Validate(workspace));

        var requirement = new WorkspaceRequirement { Key = "REQ-1", Title = "Real" };
        workspace.Requirements.Add(requirement);
        test.RequirementIds.Clear();
        test.RequirementIds.AddRange(new[] { requirement.Id, requirement.Id });
        Assert.Throws<InvalidDataException>(() => WorkspaceStore.Validate(workspace));
    }

    [Fact]
    public void RenamingRequirementPreservesGuidLinksAndCoverageAfterRestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "TestLoom-RequirementEdit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore(folder);
            var workspace = WorkspaceStore.Create("Editing sample", "software-qa");
            var requirement = new WorkspaceRequirement { Key = "REQ-BEFORE", Title = "Before" };
            var test = new WorkspaceTestCase { Title = "Linked test" };
            test.RequirementIds.Add(requirement.Id);
            test.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Passed });
            workspace.Requirements.Add(requirement);
            workspace.TestCases.Add(test);
            store.Save(workspace);

            var loaded = store.Load()!;
            loaded.Requirements.Single().Key = "REQ-AFTER";
            loaded.Requirements.Single().Title = "After";
            store.Save(loaded);

            var restarted = new WorkspaceStore(folder).Load()!;
            Assert.Equal(requirement.Id, restarted.Requirements.Single().Id);
            Assert.Equal("REQ-AFTER", restarted.Requirements.Single().Key);
            Assert.Equal("After", restarted.Requirements.Single().Title);
            Assert.Equal(requirement.Id, restarted.TestCases.Single().RequirementIds.Single());
            Assert.Equal(1, RequirementCoverage.Calculate(restarted).RequirementsWithPassingTest);
            Assert.Contains("REQ-AFTER", WorkspaceCsvReport.ExportRequirements(restarted));
            Assert.DoesNotContain("REQ-BEFORE", WorkspaceCsvReport.ExportRequirements(restarted));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void RequirementsAndLinksSurviveLocalSaveReload()
    {
        var folder = Path.Combine(Path.GetTempPath(), "TestLoom-Coverage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore(folder);
            var workspace = WorkspaceStore.Create("Laboratory", "laboratory");
            var requirement = new WorkspaceRequirement { Key = "LAB-1", Title = "Repeatability" };
            var test = new WorkspaceTestCase { Title = "Measure again" };
            test.RequirementIds.Add(requirement.Id);
            workspace.Requirements.Add(requirement);
            workspace.TestCases.Add(test);
            store.Save(workspace);
            var loaded = new WorkspaceStore(folder).Load()!;
            Assert.Single(loaded.Requirements);
            Assert.Equal(requirement.Id, loaded.TestCases[0].RequirementIds.Single());
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }
}
