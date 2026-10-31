using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class WorkspaceStepTests
{
    [Fact]
    public void OrderedStepsSurviveSaveReloadAndExportInOrder()
    {
        var folder = Path.Combine(Path.GetTempPath(),
            "TestLoom-Steps-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore(folder);
            var document = WorkspaceStore.Create("Generic QA", "blank");
            var test = new WorkspaceTestCase { Title = "Two step test" };
            var first = new WorkspaceTestStep { Action = "Open application", ExpectedResult = "Home screen" };
            var second = new WorkspaceTestStep { Action = "Select item", ExpectedResult = "Details visible" };
            test.Steps.AddRange(new[] { first, second });
            document.TestCases.Add(test);
            store.Save(document);

            var restored = new WorkspaceStore(folder).Load()!;
            Assert.Equal(2, restored.TestCases[0].Steps.Count);
            Assert.Equal(first.Id, restored.TestCases[0].Steps[0].Id);
            Assert.Equal(second.Id, restored.TestCases[0].Steps[1].Id);

            var csv = WorkspaceCsvReport.ExportTestSteps(restored);
            Assert.Contains("\"Open application\",\"Home screen\"", csv);
            Assert.True(csv.IndexOf("Open application", StringComparison.Ordinal)
                < csv.IndexOf("Select item", StringComparison.Ordinal));
            Assert.Contains("\"1\"", csv);
            Assert.Contains("\"2\"", csv);

            // Reordering changes presentation but not persistent step IDs.
            var loadedTest = restored.TestCases.Single();
            (loadedTest.Steps[0], loadedTest.Steps[1]) = (loadedTest.Steps[1], loadedTest.Steps[0]);
            var opened = new WorkspaceStore(folder);
            // A store must have loaded the current version before a write.
            Assert.Throws<WorkspaceConflictException>(() => opened.Save(restored));
            store.Load();
            store.Save(restored);
            var reordered = new WorkspaceStore(folder).Load()!;
            Assert.Equal(second.Id, reordered.TestCases[0].Steps[0].Id);
            Assert.Equal(first.Id, reordered.TestCases[0].Steps[1].Id);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void InvalidStepDataCannotBeSaved()
    {
        var document = WorkspaceStore.Create("Sample", "blank");
        var test = new WorkspaceTestCase { Title = "Invalid step" };
        document.TestCases.Add(test);
        var id = Guid.NewGuid();
        test.Steps.Add(new WorkspaceTestStep { Id = id, Action = "First", ExpectedResult = "Good" });
        test.Steps.Add(new WorkspaceTestStep { Id = id, Action = "Second", ExpectedResult = "Good" });
        Assert.Throws<InvalidDataException>(() => WorkspaceStore.Validate(document));

        test.Steps.RemoveAt(1);
        test.Steps[0].ExpectedResult = "";
        Assert.Throws<InvalidDataException>(() => WorkspaceStore.Validate(document));
        test.Steps[0].ExpectedResult = "Expected";
        WorkspaceStore.Validate(document);
    }

    [Fact]
    public void OldVersionOneDocumentWithNoStepsRemainsReadable()
    {
        var folder = Path.Combine(Path.GetTempPath(),
            "TestLoom-LegacySteps-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var testId = Guid.NewGuid();
            File.WriteAllText(Path.Combine(folder, "workspace.json"),
                $"{{\"SchemaVersion\":1,\"Name\":\"Legacy\",\"PresetId\":\"blank\",\"TestCases\":[{{\"Id\":\"{testId}\",\"Title\":\"Legacy test\",\"Executions\":[]}}]}}");
            var restored = new WorkspaceStore(folder).Load()!;
            Assert.Empty(restored.TestCases.Single().Steps);
            Assert.Single(restored.TestCases);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void StepCsvDefusesFormulaInputsAndArchivedCasesAreExcludedByDefault()
    {
        var document = WorkspaceStore.Create("Security test", "blank");
        var test = new WorkspaceTestCase { Title = "Example" };
        test.Steps.Add(new WorkspaceTestStep
        {
            Action = "=HYPERLINK(\"https://example.invalid\",\"click\")",
            ExpectedResult = "+SUM(1,2)"
        });
        document.TestCases.Add(test);
        var csv = WorkspaceCsvReport.ExportTestSteps(document);
        Assert.Contains("\"'=HYPERLINK(", csv);
        Assert.Contains("\"'+SUM(1,2)\"", csv);

        test.Archived = true;
        Assert.DoesNotContain("HYPERLINK", WorkspaceCsvReport.ExportTestSteps(document));
        Assert.Contains("HYPERLINK", WorkspaceCsvReport.ExportTestSteps(document, includeArchived: true));
    }
}
