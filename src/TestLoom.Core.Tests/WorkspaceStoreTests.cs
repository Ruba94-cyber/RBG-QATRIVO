using System.Text.Json;
using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class WorkspaceStoreTests
{
    [Fact]
    public void CreateSaveReloadAndExecutionRoundTrip()
    {
        WithIsolatedStore(store =>
        {
            var document = WorkspaceStore.Create("Synthetic QA", "software-qa");
            var test = new WorkspaceTestCase { Title = "Display profile", TemplateName = "Smoke test" };
            test.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Passed, Notes = "Synthetic pass" });
            document.TestCases.Add(test);
            store.Save(document);

            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal("Synthetic QA", loaded.Name);
            Assert.Single(loaded.TestCases);
            Assert.Equal(test.Id, loaded.TestCases[0].Id);
            Assert.Equal(ExecutionResult.Passed, loaded.TestCases[0].Executions.Single().Result);

            // Preserve earlier content on subsequent updates.
            loaded.TestCases[0].Description = "Updated";
            loaded.TestCases[0].Archived = true;
            store.Save(loaded);
            Assert.True(File.Exists(store.FilePath + ".bak"));
            Assert.True(new WorkspaceStore(
                Path.GetDirectoryName(store.FilePath)!).Load()!.TestCases[0].Archived);
        });
    }

    [Fact]
    public void UnknownSchemaMustNotBeSilentlyMigratedOrOverwritten()
    {
        WithIsolatedStore(store =>
        {
            var folder = Path.GetDirectoryName(store.FilePath)!;
            Directory.CreateDirectory(folder);
            var invalid = """{"SchemaVersion":999,"Name":"Synthetic QA","PresetId":"blank","TestCases":[]}""";
            File.WriteAllText(store.FilePath, invalid);

            Assert.Throws<NotSupportedException>(() => store.Load());
            Assert.Equal(invalid, File.ReadAllText(store.FilePath));
        });
    }

    [Theory]
    [InlineData("""{"Name":"No schema","PresetId":"blank","TestCases":[]}""")]
    [InlineData("""{"SchemaVersion":"2","Name":"Bad type","PresetId":"blank","TestCases":[]}""")]
    [InlineData("""{"schemaVersion":2,"Name":"wrong casing","PresetId":"blank","TestCases":[]}""")]
    [InlineData("""{"SCHEMAVERSION":1,"Name":"wrong casing","PresetId":"blank","TestCases":[]}""")]
    [InlineData("""{"SchemaVersion":1,"SchemaVersion":2,"Name":"Ambiguous","PresetId":"blank","TestCases":[]}""")]
    [InlineData("""{"SchemaVersion":2,"Name":"Future","PresetId":"blank","TestCases":{"newShape":true}}""")]
    public void UnsupportedOrAmbiguousSchemaIsRejectedBeforeTypedParsing(string data)
    {
        WithIsolatedStore(store =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, data);
            Assert.Throws<NotSupportedException>(() => store.Load());
            Assert.Equal(data, File.ReadAllText(store.FilePath));
        });
    }

    [Fact]
    public void ExistingVersionOneWorkspaceLoadsWithoutNewOptionalCollections()
    {
        WithIsolatedStore(store =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            var id = Guid.NewGuid();
            var original = $$"""
            {
              "SchemaVersion": 1,
              "Name": "Earlier test workspace",
              "PresetId": "blank",
              "TestCases": [
                { "Id": "{{id}}", "Title": "Existing case", "Executions": [] }
              ]
            }
            """;
            File.WriteAllText(store.FilePath, original);
            var loaded = store.Load()!;
            Assert.Empty(loaded.Requirements);
            Assert.Single(loaded.TestCases);
            Assert.Empty(loaded.TestCases[0].RequirementIds);
            Assert.False(loaded.TestCases[0].Archived);

            loaded.TestCases[0].Description = "Retained after an update";
            store.Save(loaded);
            Assert.Equal(original, File.ReadAllText(store.FilePath + ".bak"));
            Assert.Equal(id, new WorkspaceStore(
                Path.GetDirectoryName(store.FilePath)!).Load()!.TestCases.Single().Id);
        });
    }

    [Fact]
    public void DuplicateCaseIdsRejectedWithoutWriting()
    {
        WithIsolatedStore(store =>
        {
            var document = WorkspaceStore.Create("Sample", "blank");
            var sharedId = Guid.NewGuid();
            document.TestCases.Add(new WorkspaceTestCase { Id = sharedId, Title = "A" });
            document.TestCases.Add(new WorkspaceTestCase { Id = sharedId, Title = "B" });
            Assert.Throws<InvalidDataException>(() => store.Save(document));
            Assert.False(File.Exists(store.FilePath));
        });
    }

    [Fact]
    public void ConcurrentStoreSessionsCannotSilentlyOverwriteEachOther()
    {
        WithIsolatedStore(first =>
        {
            var baseline = WorkspaceStore.Create("Original", "blank");
            first.Save(baseline);
            var second = new WorkspaceStore(Path.GetDirectoryName(first.FilePath)!);
            var secondCopy = second.Load()!;
            var firstCopy = first.Load()!;

            firstCopy.Name = "First writer";
            first.Save(firstCopy);
            secondCopy.Name = "Stale second writer";

            Assert.Throws<WorkspaceConflictException>(() => second.Save(secondCopy));
            Assert.Equal("First writer", new WorkspaceStore(
                Path.GetDirectoryName(first.FilePath)!).Load()!.Name);
        });
    }

    [Fact]
    public void ExistingWorkspaceCannotBeOverwrittenWithoutReadingIt()
    {
        WithIsolatedStore(first =>
        {
            first.Save(WorkspaceStore.Create("Existing", "blank"));
            var second = new WorkspaceStore(Path.GetDirectoryName(first.FilePath)!);

            Assert.Throws<WorkspaceConflictException>(() =>
                second.Save(WorkspaceStore.Create("Unrelated", "blank")));
            Assert.Equal("Existing", first.Load()!.Name);
        });
    }

    [Fact]
    public void DuplicateExecutionIdentifiersAreRejected()
    {
        WithIsolatedStore(store =>
        {
            var doc = WorkspaceStore.Create("Safe", "blank");
            var tc = new WorkspaceTestCase { Title = "Quality check" };
            var duplicate = Guid.NewGuid();
            tc.Executions.Add(new WorkspaceExecution { Id = duplicate, Result = ExecutionResult.Passed });
            tc.Executions.Add(new WorkspaceExecution { Id = duplicate, Result = ExecutionResult.Failed });
            doc.TestCases.Add(tc);
            Assert.Throws<InvalidDataException>(() => store.Save(doc));
            Assert.False(File.Exists(store.FilePath));
        });
    }

    [Fact]
    public void AnExternalFileChangeIsRejectedWithoutTouchingTheNewContent()
    {
        WithIsolatedStore(store =>
        {
            var document = WorkspaceStore.Create("Before", "blank");
            store.Save(document);
            var external = """{"SchemaVersion":1,"Name":"From another editor","PresetId":"blank","TestCases":[]}""";
            File.WriteAllText(store.FilePath, external);

            document.Name = "Unaware old process";
            Assert.Throws<WorkspaceConflictException>(() => store.Save(document));
            Assert.Equal(external, File.ReadAllText(store.FilePath));
        });
    }

    [Fact]
    public void UnknownPresetCannotCreateWorkspace()
    {
        Assert.Throws<ArgumentException>(() => WorkspaceStore.Create("Sample", "invalid"));
    }

    private static void WithIsolatedStore(Action<WorkspaceStore> action)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom-Tests-" + Guid.NewGuid().ToString("N"));
        try { action(new WorkspaceStore(dir)); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
