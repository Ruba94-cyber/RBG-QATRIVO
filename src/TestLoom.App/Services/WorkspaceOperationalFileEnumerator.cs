namespace TestLoom.App.Services;

/// <summary>
/// Lazy, read-only inventory of managed operational JSON. Only known TestLoom
/// domain roots are visited: arbitrary third-party trees below a SharePoint
/// workspace and legacy/on-demand attachments are never traversed.
/// Files themselves are not opened/hydrated. Same ownership predicate as the
/// legacy backup enumeration, with no change to snapshot contents.
/// </summary>
public static class WorkspaceOperationalFileEnumerator
{
    private static readonly string[] DomainRoots =
    [
        "testcases", "nonconformita", "change-requests", "segnalazioni",
        "cestino", "notifiche", "recovery-bin"
    ];

    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "allegati", "attachments", "backup", "_ditm"
    };

    public static IEnumerable<string> EnumerateManagedJson(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) yield break;
        var fullRoot = Path.GetFullPath(root);
        ct.ThrowIfCancellationRequested();

        // The root contains only a small, fixed catalogue of authoritative
        // JSON files; third-party folders are NOT searched recursively.
        foreach (var file in Directory.EnumerateFiles(fullRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            if (TestLoomStorageOwnership.IsManagedJsonPath(fullRoot, file))
                yield return file;
        }

        var queue = new Stack<string>();
        foreach (var name in DomainRoots)
        {
            var dir = Path.Combine(fullRoot, name);
            if (!Directory.Exists(dir)) continue;
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            queue.Push(dir);
        }

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = queue.Pop();
            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (TestLoomStorageOwnership.IsManagedJsonPath(fullRoot, file))
                    yield return file;
            }

            foreach (var folder in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (Excluded.Contains(Path.GetFileName(folder))) continue;
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                queue.Push(folder);
            }
        }
    }
}
