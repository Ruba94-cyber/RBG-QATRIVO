using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class WorkspaceLinkSafetyTests
{
    [Fact]
    public void LinkedWorkspaceDirectoryCannotRedirectWritesToAnotherProfile()
    {
        WithFolder(root =>
        {
            var otherProfile = Path.Combine(root, "other-profile");
            Directory.CreateDirectory(otherProfile);
            var untouched = Path.Combine(otherProfile, "keep.txt");
            File.WriteAllText(untouched, "keep original data");

            var link = Path.Combine(root, "linked-testloom");
            if (!TryCreateDirectoryLink(link, otherProfile)) return;

            var store = new WorkspaceStore(link);
            Assert.Throws<InvalidDataException>(() =>
                store.Save(WorkspaceStore.Create("Synthetic", "blank")));
            Assert.Throws<InvalidDataException>(() => store.Load());
            Assert.Equal("keep original data", File.ReadAllText(untouched));
            Assert.False(File.Exists(Path.Combine(otherProfile, "workspace.json")));
        });
    }

    [Fact]
    public void LinkedBackupCannotRedirectAtomicWorkspaceReplacement()
    {
        WithFolder(root =>
        {
            var folder = Path.Combine(root, "real-profile");
            var store = new WorkspaceStore(folder);
            store.Save(WorkspaceStore.Create("Original", "blank"));
            var originalPrimary = File.ReadAllBytes(store.FilePath);

            var unrelated = Path.Combine(root, "outside.txt");
            File.WriteAllText(unrelated, "unrelated bytes");
            if (!TryCreateFileLink(store.FilePath + ".bak", unrelated)) return;

            var updated = WorkspaceStore.Create("Updated", "blank");
            Assert.Throws<InvalidDataException>(() => store.Save(updated));
            Assert.Equal(originalPrimary, File.ReadAllBytes(store.FilePath));
            Assert.Equal("unrelated bytes", File.ReadAllText(unrelated));
            Assert.Throws<InvalidDataException>(() => store.Load());
        });
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or PlatformNotSupportedException or NotSupportedException)
        {
            // Windows developer mode or permissions may prohibit symlinks.
            return false;
        }
    }

    private static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static void WithFolder(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(),
            "TestLoom-LinkSafety-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            action(root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
