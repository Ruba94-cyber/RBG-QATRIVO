using System.IO.Compression;
using System.Security.Cryptography;

namespace TestLoom.Core;

/// <summary>
/// Portable backup of a workspace: a ZIP holding the validated workspace JSON and the evidence files it references.
/// Restore refuses invalid archives, never overwrites an existing workspace and never extracts paths from the archive:
/// only <c>workspace.json</c> and <c>evidence/&lt;generated name&gt;</c> entries that match the document are accepted.
/// </summary>
public static class WorkspaceBackup
{
    private const string EntryName = "workspace.json";
    private const long MaxBytes = 64L * 1024 * 1024;
    private const long MaxTotalBytes = 1024L * 1024 * 1024;

    public static void Create(WorkspaceStore store, string zipPath)
    {
        ArgumentNullException.ThrowIfNull(store);
        var document = store.Load() ?? throw new InvalidDataException("There is no workspace to back up.");
        WorkspaceStore.Validate(document);
        var evidence = new EvidenceStore(Path.GetDirectoryName(store.FilePath)!);
        var temp = zipPath + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(store.FilePath, EntryName, CompressionLevel.Optimal);
                foreach (var item in document.TestCases.SelectMany(t => t.Executions).SelectMany(e => e.Evidence)
                             .DistinctBy(e => e.StoredName))
                {
                    var path = evidence.PathOf(item);
                    if (File.Exists(path))
                        archive.CreateEntryFromFile(path, EvidenceStore.FolderName + "/" + item.StoredName, CompressionLevel.Optimal);
                }
            }
            File.Move(temp, zipPath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
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
        if (entry.Length is <= 0 or > MaxBytes)
            throw new InvalidDataException("The archive is not a TestLoom backup.");

        // Every other entry must be an evidence file with a generated name; anything else rejects the archive.
        var extras = archive.Entries.Where(e => e != entry).ToArray();
        long total = entry.Length;
        foreach (var extra in extras)
        {
            var name = extra.FullName;
            if (!name.StartsWith(EvidenceStore.FolderName + "/", StringComparison.Ordinal) ||
                !EvidenceStore.IsStoredName(name[(EvidenceStore.FolderName.Length + 1)..]) ||
                extra.Length > EvidenceStore.MaxFileBytes ||
                extras.Count(x => x.FullName == name) != 1)
                throw new InvalidDataException("The archive is not a TestLoom backup.");
            total += extra.Length;
        }
        if (total > MaxTotalBytes) throw new InvalidDataException("The archive is too large.");

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

            WorkspaceStore.Validate(document);
            var declared = document.TestCases.SelectMany(t => t.Executions).SelectMany(e => e.Evidence)
                .DistinctBy(e => e.StoredName).ToDictionary(e => e.StoredName);
            var stagedEvidence = Path.Combine(staging, EvidenceStore.FolderName);
            foreach (var extra in extras)
            {
                var stored = extra.FullName[(EvidenceStore.FolderName.Length + 1)..];
                if (!declared.TryGetValue(stored, out var meta))
                    throw new InvalidDataException("The archive contains a file the workspace does not reference.");
                Directory.CreateDirectory(stagedEvidence);
                var staged = Path.Combine(stagedEvidence, stored);
                using (var input = extra.Open())
                using (var output = File.Create(staged))
                    input.CopyTo(output);
                using var check = File.OpenRead(staged);
                if (new FileInfo(staged).Length != meta.SizeBytes ||
                    !Convert.ToHexString(SHA256.HashData(check)).Equals(meta.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("An evidence file does not match its recorded checksum.");
            }

            Directory.CreateDirectory(targetDirectory);
            if (Directory.Exists(stagedEvidence))
            {
                var finalEvidence = Path.Combine(Path.GetFullPath(targetDirectory), EvidenceStore.FolderName);
                Directory.CreateDirectory(finalEvidence);
                foreach (var file in Directory.GetFiles(stagedEvidence))
                    File.Copy(file, Path.Combine(finalEvidence, Path.GetFileName(file)), overwrite: false);
            }
            target.Save(document);
            return document;
        }
        finally { try { Directory.Delete(staging, true); } catch (IOException) { } }
    }
}
