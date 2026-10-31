using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class WorkspaceDefectTests
{
    [Fact]
    public void DefectsAndStableLinkedTestIdRoundTripWithoutBreakingExecutionHistory()
    {
        WithStore(store =>
        {
            var document = WorkspaceStore.Create("Generic test management", "software-qa");
            var test = new WorkspaceTestCase { Title = "App login" };
            test.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Failed });
            document.TestCases.Add(test);
            document.Defects.Add(new WorkspaceDefect
            {
                Key = "BUG-001",
                Title = "Unexpected dialog",
                Description = "Found during generic test",
                Severity = WorkspaceDefectSeverity.High,
                TestCaseId = test.Id
            });

            store.Save(document);
            var restored = store.Load()!;
            Assert.Single(restored.Defects);
            Assert.Equal(test.Id, restored.Defects[0].TestCaseId);
            Assert.Equal(ExecutionResult.Failed, restored.TestCases[0].Executions.Single().Result);
            Assert.Equal(WorkspaceDefectSeverity.High, restored.Defects[0].Severity);

            restored.Defects[0].Status = WorkspaceDefectStatus.Resolved;
            store.Save(restored);
            Assert.Equal(WorkspaceDefectStatus.Resolved, store.Load()!.Defects.Single().Status);
            Assert.Contains("\"Resolved\"", WorkspaceCsvReport.ExportDefects(store.Load()!));
        });
    }

    [Fact]
    public void DefectDetailsRemainEditableWithoutChangingStableTestLinkOrStatus()
    {
        WithStore(store =>
        {
            var workspace = WorkspaceStore.Create("Example", "blank");
            var test = new WorkspaceTestCase { Title = "Synthetic" };
            workspace.TestCases.Add(test);
            workspace.Defects.Add(new WorkspaceDefect
            {
                Key = "ISSUE-1",
                Title = "Initial title",
                Description = "Initial description",
                TestCaseId = test.Id
            });
            store.Save(workspace);

            var loaded = store.Load()!;
            var issue = loaded.Defects.Single();
            var stableId = issue.Id;
            issue.Title = "Corrected title";
            issue.Description = "Reproduced on a clean installation";
            issue.Severity = WorkspaceDefectSeverity.Critical;
            store.Save(loaded);

            var final = new WorkspaceStore(Path.GetDirectoryName(store.FilePath)!).Load()!;
            var saved = final.Defects.Single();
            Assert.Equal(stableId, saved.Id);
            Assert.Equal(test.Id, saved.TestCaseId);
            Assert.Equal("Corrected title", saved.Title);
            Assert.Equal("Reproduced on a clean installation", saved.Description);
            Assert.Equal(WorkspaceDefectSeverity.Critical, saved.Severity);
            Assert.Equal(WorkspaceDefectStatus.Open, saved.Status);
            Assert.Contains("Corrected title", WorkspaceCsvReport.ExportDefects(final));
        });
    }

    [Fact]
    public void DefectsMustHaveValidLinksUniqueKeysAndKnownStatuses()
    {
        var document = WorkspaceStore.Create("Generic", "blank");
        var defect = new WorkspaceDefect
        {
            Key = "BUG-001", Title = "Broken", TestCaseId = Guid.NewGuid()
        };
        document.Defects.Add(defect);
        Assert.Throws<InvalidDataException>(() => WorkspaceStore.Validate(document));

        var test = new WorkspaceTestCase { Title = "Linked" };
        document.TestCases.Add(test);
        defect.TestCaseId = test.Id;
        WorkspaceStore.Validate(document);

        document.Defects.Add(new WorkspaceDefect { Key = "bug-001", Title = "Duplicate" });
        Assert.Throws<InvalidDataException>(() => WorkspaceStore.Validate(document));
        document.Defects.RemoveAt(1);
        defect.Status = (WorkspaceDefectStatus)999;
        Assert.Throws<InvalidDataException>(() => WorkspaceStore.Validate(document));
    }

    [Fact]
    public void LegacySchemaOneWithoutDefectsStillLoadsAndCanBeSaved()
    {
        WithStore(store =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath,
                """{"SchemaVersion":1,"Name":"Legacy","PresetId":"blank","TestCases":[],"Requirements":[]}""");
            var document = store.Load()!;
            Assert.Empty(document.Defects);
            store.Save(document);
            Assert.Empty(store.Load()!.Defects);
        });
    }

    [Fact]
    public void DefectCsvEscapesFormulaLikeText()
    {
        var document = WorkspaceStore.Create("Generic", "blank");
        document.Defects.Add(new WorkspaceDefect
        {
            Key = "BUG-002",
            Title = "=CMD()",
            Description = "+SUM(1,2)"
        });
        var csv = WorkspaceCsvReport.ExportDefects(document);
        Assert.Contains("\"'=CMD()\"", csv);
        Assert.Contains("\"'+SUM(1,2)\"", csv);
    }

    private static void WithStore(Action<WorkspaceStore> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TestLoom-Defects-" + Guid.NewGuid().ToString("N"));
        try { test(new WorkspaceStore(directory)); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
