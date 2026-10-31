using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class RequirementLinksTests
{
    [Fact]
    public void ExistingTestCanLinkAndUnlinkRequirementsWithoutLosingHistory()
    {
        var folder = Path.Combine(Path.GetTempPath(),
            "TestLoom-LinkTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore(folder);
            var workspace = WorkspaceStore.Create("Traceability sample", "blank");
            var first = new WorkspaceRequirement { Key = "REQ-A", Title = "First" };
            var second = new WorkspaceRequirement { Key = "REQ-B", Title = "Second" };
            workspace.Requirements.AddRange(new[] { first, second });
            var test = new WorkspaceTestCase { Title = "Existing test" };
            test.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Passed });
            workspace.TestCases.Add(test);
            store.Save(workspace);

            var loaded = store.Load()!;
            Assert.True(RequirementLinks.SetLinked(loaded, test.Id, first.Id, linked: true));
            Assert.True(RequirementLinks.SetLinked(loaded, test.Id, second.Id, linked: true));
            Assert.False(RequirementLinks.SetLinked(loaded, test.Id, second.Id, linked: true));
            store.Save(loaded);

            var opened = store.Load()!;
            Assert.Equal(2, opened.TestCases.Single().RequirementIds.Count);
            Assert.Equal(2, RequirementCoverage.Calculate(opened).LinkedRequirements);
            Assert.True(RequirementLinks.SetLinked(opened, test.Id, first.Id, linked: false));
            Assert.False(RequirementLinks.SetLinked(opened, test.Id, first.Id, linked: false));
            store.Save(opened);

            var final = new WorkspaceStore(folder).Load()!;
            Assert.Equal(new[] { second.Id }, final.TestCases.Single().RequirementIds);
            Assert.Equal(1, RequirementCoverage.Calculate(final).LinkedRequirements);
            Assert.Equal(ExecutionResult.Passed,
                final.TestCases.Single().Executions.Single().Result);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void UnknownTargetsAndArchivedTestsCannotBeModified()
    {
        var workspace = WorkspaceStore.Create("Synthetic", "blank");
        var req = new WorkspaceRequirement { Key = "REQ-1", Title = "Known" };
        workspace.Requirements.Add(req);
        var tc = new WorkspaceTestCase { Title = "Known" };
        workspace.TestCases.Add(tc);

        Assert.Throws<ArgumentException>(() =>
            RequirementLinks.SetLinked(workspace, Guid.NewGuid(), req.Id, linked: true));
        Assert.Throws<ArgumentException>(() =>
            RequirementLinks.SetLinked(workspace, tc.Id, Guid.NewGuid(), linked: true));

        tc.Archived = true;
        Assert.Throws<InvalidOperationException>(() =>
            RequirementLinks.SetLinked(workspace, tc.Id, req.Id, linked: true));
        Assert.Empty(tc.RequirementIds);
    }

    [Fact]
    public void RequirementRenamePreservesLinkedGuids()
    {
        var workspace = WorkspaceStore.Create("Synthetic", "blank");
        var req = new WorkspaceRequirement { Key = "REQ-1", Title = "Before" };
        var tc = new WorkspaceTestCase { Title = "Check" };
        workspace.Requirements.Add(req);
        workspace.TestCases.Add(tc);

        Assert.True(RequirementLinks.SetLinked(workspace, tc.Id, req.Id, true));
        req.Key = "REQ-NEW";
        req.Title = "After";
        WorkspaceStore.Validate(workspace);
        Assert.Equal(req.Id, tc.RequirementIds.Single());
        Assert.Contains("REQ-NEW", WorkspaceCsvReport.ExportTestCases(workspace));
        Assert.DoesNotContain("REQ-1", WorkspaceCsvReport.ExportTestCases(workspace));
    }
}
