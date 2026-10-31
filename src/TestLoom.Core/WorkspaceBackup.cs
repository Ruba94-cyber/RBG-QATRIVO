using System.IO.Compression;

namespace TestLoom.Core;

/// <summary>
/// Portable backup of a workspace file: a ZIP holding the validated workspace JSON. Restore refuses invalid
/// archives, never overwrites an existing workspace and never extracts paths from the archive.
/// </summary>
public static class WorkspaceBackup
{
    private const string EntryName = "workspace.json";
    private const long MaxBytes = 64L * 1024 * 1024;

    public static void Create(WorkspaceStore store, string zipPath)
    {
        ArgumentNullException.ThrowIfNull(store);
        var document = store.Load() ?? throw new InvalidDataException("There is no workspace to back up.");
        WorkspaceStore.Validate(document);
        var temp = zipPath + ".tmp";
        using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            archive.CreateEntryFromFile(store.FilePath, EntryName, CompressionLevel.Optimal);
        File.Move(temp, zipPath, overwrite: true);
    }

    /// <summary>Restores into an EMPTY target folder and returns the restored document.</summary>
    public static WorkspaceDocument Restore(string zipPath, string targetDirectory)
    {
        var target = new WorkspaceStore(targetDirectory);
        if (File.Exists(target.FilePath))
            throw new IOException("The target folder already contains a workspace; choose an empty folder.");

        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.SingleOrDefault(e => e.FullName == EntryName)
            ?? throw new InvalidDataException("The archive is not a TestLoom backup.");
        if (archive.Entries.Count != 1 || entry.Length is <= 0 or > MaxBytes)
            throw new InvalidDataException("The archive is not a TestLoom backup.");

        // Load through the product's own store so the same parser, limits and validation apply as for a normal open.
        var staging = Path.Combine(Path.GetTempPath(), "TestLoom-Restore-" + Guid.NewGuid().ToString("N"));
        WorkspaceDocument document;
        try
        {
            Directory.CreateDirectory(staging);
            using (var input = entry.Open())
            using (var output = File.Create(Path.Combine(staging, "workspace.json")))
                input.CopyTo(output);
            try { document = new WorkspaceStore(staging).Load() ?? throw new InvalidDataException("Empty backup."); }
            catch (System.Text.Json.JsonException ex) { throw new InvalidDataException("The backup is damaged.", ex); }
        }
        finally { try { Directory.Delete(staging, true); } catch (IOException) { } }
        WorkspaceStore.Validate(document);

        Directory.CreateDirectory(targetDirectory);
        target.Save(document);
        return document;
    }
}
