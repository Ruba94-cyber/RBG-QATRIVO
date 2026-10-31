using System.Text.Json;
using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class WorkspaceRecoveryTests
{
    [Fact]
    public void DamagedPrimaryIsRestoredOnlyFromValidatedPreviousVersion()
    {
        WithFolder(folder =>
        {
            var store = new WorkspaceStore(folder);
            var previous = WorkspaceStore.Create("Safe previous version", "blank");
            store.Save(previous);
            var latest = store.Load()!;
            latest.Name = "Most recent version";
            store.Save(latest);

            // A broken primary is never silently deleted by Load.
            var invalidContent = "{this is not valid JSON";
            File.WriteAllText(store.FilePath, invalidContent);
            Assert.ThrowsAny<JsonException>(() => new WorkspaceStore(folder).Load());

            var recoverySession = new WorkspaceStore(folder);
            var recovered = recoverySession.RecoverCorruptedFromBackup();

            Assert.Equal("Safe previous version", recovered.Name);
            Assert.Equal("Safe previous version", new WorkspaceStore(folder).Load()!.Name);
            Assert.Equal("Safe previous version", recoverySession.Load()!.Name);
            Assert.True(File.Exists(store.FilePath + ".bak"));
            var damaged = Directory.GetFiles(folder, "workspace.json.corrupt-*");
            Assert.Single(damaged);
            Assert.Equal(invalidContent, File.ReadAllText(damaged[0]));
        });
    }

    [Fact]
    public void ValidPrimaryCannotBeOverwrittenWithOldBackup()
    {
        WithFolder(folder =>
        {
            var store = new WorkspaceStore(folder);
            store.Save(WorkspaceStore.Create("Valid primary", "blank"));
            var update = store.Load()!;
            update.Name = "New primary";
            store.Save(update);
            var unchanged = File.ReadAllBytes(store.FilePath);

            var recoverySession = new WorkspaceStore(folder);
            Assert.Throws<InvalidOperationException>(() =>
                recoverySession.RecoverCorruptedFromBackup());

            Assert.Equal(unchanged, File.ReadAllBytes(store.FilePath));
            Assert.Empty(Directory.GetFiles(folder, "workspace.json.corrupt-*"));
        });
    }

    [Fact]
    public void InvalidBackupDoesNotReplaceBrokenPrimary()
    {
        WithFolder(folder =>
        {
            var store = new WorkspaceStore(folder);
            store.Save(WorkspaceStore.Create("Initial", "blank"));
            var next = store.Load()!;
            next.Name = "Latest";
            store.Save(next);

            var corruptedPrimary = """{"SchemaVersion": 1, "Name": "", "PresetId": "blank", "TestCases": []}""";
            File.WriteAllText(store.FilePath, corruptedPrimary);
            File.WriteAllText(store.FilePath + ".bak", "{invalid backup");
            var recoverySession = new WorkspaceStore(folder);

            Assert.ThrowsAny<JsonException>(() =>
                recoverySession.RecoverCorruptedFromBackup());
            Assert.Equal(corruptedPrimary, File.ReadAllText(store.FilePath));
            Assert.Empty(Directory.GetFiles(folder, "workspace.json.corrupt-*"));
        });
    }

    [Theory]
    [InlineData("""{"Name":"Legacy unknown","PresetId":"blank","TestCases":[]}""")]
    [InlineData("""{"SchemaVersion":2,"Name":"Newer","PresetId":"blank","TestCases":[]}""")]
    public void UnsupportedBackupIsNeverRestoredOverDamagedPrimary(string backup)
    {
        WithFolder(folder =>
        {
            Directory.CreateDirectory(folder);
            var store = new WorkspaceStore(folder);
            const string damagedPrimary = "{damaged";
            File.WriteAllText(store.FilePath, damagedPrimary);
            File.WriteAllText(store.FilePath + ".bak", backup);

            Assert.Throws<NotSupportedException>(() =>
                store.RecoverCorruptedFromBackup());
            Assert.Equal(damagedPrimary, File.ReadAllText(store.FilePath));
            Assert.Equal(backup, File.ReadAllText(store.FilePath + ".bak"));
            Assert.Empty(Directory.GetFiles(folder, "workspace.json.corrupt-*"));
        });
    }

    [Fact]
    public void MissingBackupLeavesCorruptedPrimaryUntouched()
    {
        WithFolder(folder =>
        {
            Directory.CreateDirectory(folder);
            var store = new WorkspaceStore(folder);
            const string invalid = "not JSON";
            File.WriteAllText(store.FilePath, invalid);
            Assert.Throws<FileNotFoundException>(() =>
                store.RecoverCorruptedFromBackup());
            Assert.Equal(invalid, File.ReadAllText(store.FilePath));
        });
    }

    [Fact]
    public void NewerSchemaIsNotMistakenForCorruption()
    {
        WithFolder(folder =>
        {
            var store = new WorkspaceStore(folder);
            store.Save(WorkspaceStore.Create("Original", "blank"));
            var second = store.Load()!;
            second.Name = "Before upgrade";
            store.Save(second);

            // The next app version can write a newer schema into this folder.
            const string upgraded = """{"SchemaVersion":2,"Name":"Future version","PresetId":"blank","TestCases":[],"Requirements":[]}""";
            File.WriteAllText(store.FilePath, upgraded);
            var recovery = new WorkspaceStore(folder);

            Assert.Throws<NotSupportedException>(() => recovery.RecoverCorruptedFromBackup());
            Assert.Equal(upgraded, File.ReadAllText(store.FilePath));
            Assert.Empty(Directory.GetFiles(folder, "workspace.json.corrupt-*"));
        });
    }

    [Fact]
    public void FailedReloadCanRecoverInSameSessionWithoutRestart()
    {
        WithFolder(folder =>
        {
            var session = new WorkspaceStore(folder);
            session.Save(WorkspaceStore.Create("Previous", "blank"));
            var updated = session.Load()!;
            updated.Name = "Current";
            session.Save(updated);

            // Simulate external corruption while the same UI session is open.
            File.WriteAllText(session.FilePath, "{invalid external bytes");
            Assert.ThrowsAny<JsonException>(() => session.Load());

            var restored = session.RecoverCorruptedFromBackup();
            Assert.Equal("Previous", restored.Name);
            Assert.Equal("Previous", session.Load()!.Name);
            Assert.Single(Directory.GetFiles(folder, "workspace.json.corrupt-*"));
        });
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("123")]
    public void ValidJsonWithoutVersionedObjectMustNotTriggerBackupRollback(string json)
    {
        WithFolder(folder =>
        {
            var store = new WorkspaceStore(folder);
            store.Save(WorkspaceStore.Create("Before", "blank"));
            var newer = store.Load()!;
            newer.Name = "Latest";
            store.Save(newer);
            File.WriteAllText(store.FilePath, json);

            var recovery = new WorkspaceStore(folder);
            Assert.Throws<NotSupportedException>(() => recovery.Load());
            Assert.Throws<NotSupportedException>(() =>
                recovery.RecoverCorruptedFromBackup());
            Assert.Equal(json, File.ReadAllText(store.FilePath));
            Assert.Empty(Directory.GetFiles(folder, "workspace.json.corrupt-*"));
        });
    }

    [Fact]
    public void FutureSchemaWithChangedFieldTypesMustNeverBeRolledBack()
    {
        WithFolder(folder =>
        {
            var store = new WorkspaceStore(folder);
            store.Save(WorkspaceStore.Create("Original", "blank"));
            var second = store.Load()!;
            second.Name = "Prior version";
            store.Save(second);

            // This JSON is well-formed, but old typed deserialization would
            // throw on TestCases and accidentally classify it as corruption.
            const string newer = """{"SchemaVersion":2,"Name":"Future","PresetId":"blank","TestCases":{"newFormat":true}}""";
            File.WriteAllText(store.FilePath, newer);
            var recovery = new WorkspaceStore(folder);

            Assert.Throws<NotSupportedException>(() =>
                recovery.RecoverCorruptedFromBackup());
            Assert.Equal(newer, File.ReadAllText(store.FilePath));
            Assert.Empty(Directory.GetFiles(folder, "workspace.json.corrupt-*"));
        });
    }

    [Fact]
    public void ConcurrentWriterLockPreventsSaveAndRecovery()
    {
        WithFolder(folder =>
        {
            var store = new WorkspaceStore(folder);
            store.Save(WorkspaceStore.Create("Original", "blank"));
            var current = store.Load()!;
            var originalJson = File.ReadAllText(store.FilePath);
            using (var holdLock = new FileStream(Path.Combine(folder, ".workspace.lock"),
                       FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                current.Name = "Blocked save";
                Assert.Throws<IOException>(() => store.Save(current));
                Assert.Equal(originalJson, File.ReadAllText(store.FilePath));
                File.WriteAllText(store.FilePath, "{invalid primary");
                var recovery = new WorkspaceStore(folder);
                Assert.Throws<IOException>(() => recovery.RecoverCorruptedFromBackup());
                Assert.Equal("{invalid primary", File.ReadAllText(store.FilePath));
            }
        });
    }

    private static void WithFolder(Action<string> action)
    {
        var folder = Path.Combine(Path.GetTempPath(), "TestLoom-Recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            action(folder);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
