using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TestLoom.Core;

/// <summary>
/// Stores files attached to executions inside the workspace folder (<c>evidence/</c>). Files are copied, never
/// referenced in place; the stored name is generated (no user text reaches the file system) and the content is
/// identified by SHA-256 so a later change or loss is detectable.
/// </summary>
public sealed partial class EvidenceStore
{
    public const long MaxFileBytes = 25L * 1024 * 1024;
    public const string FolderName = "evidence";

    private readonly string _folder;

    public EvidenceStore(string workspaceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        _folder = Path.Combine(Path.GetFullPath(workspaceDirectory), FolderName);
    }

    public string FolderPath => _folder;

    [GeneratedRegex("^[0-9a-f]{32}[.][a-z0-9]{1,8}$")]
    private static partial Regex StoredNamePattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    public static bool IsStoredName(string? name) => name is not null && StoredNamePattern().IsMatch(name);

    public static bool IsValid(WorkspaceEvidence? e) =>
        e is not null && e.Id != Guid.Empty &&
        !string.IsNullOrWhiteSpace(e.FileName) && e.FileName.Length <= 255 &&
        e.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        IsStoredName(e.StoredName) && e.Sha256 is not null && HashPattern().IsMatch(e.Sha256) &&
        e.SizeBytes is >= 0 and <= MaxFileBytes;

    public WorkspaceEvidence Add(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var info = new FileInfo(sourcePath);
        if (!info.Exists) throw new FileNotFoundException("The file to attach does not exist.", sourcePath);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked files cannot be attached.");
        if (info.Length > MaxFileBytes)
            throw new InvalidDataException($"The file is larger than {MaxFileBytes / (1024 * 1024)} MB.");

        var extension = Path.GetExtension(info.Name).TrimStart('.').ToLowerInvariant();
        if (extension.Length is < 1 or > 8 || !extension.All(char.IsAsciiLetterOrDigit)) extension = "bin";
        var storedName = Guid.NewGuid().ToString("N") + "." + extension;

        EnsureFolder();
        var target = Path.Combine(_folder, storedName);
        var temporary = Path.Combine(_folder, "." + storedName + ".tmp");
        try
        {
            long copied = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var input = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    copied += read;
                    if (copied > MaxFileBytes)
                        throw new InvalidDataException($"The file is larger than {MaxFileBytes / (1024 * 1024)} MB.");
                    hash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }
            }
            File.Move(temporary, target);
            var name = info.Name.Length > 255 ? info.Name[^255..] : info.Name;
            return new WorkspaceEvidence
            {
                FileName = name, StoredName = storedName, SizeBytes = copied,
                Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()
            };
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string PathOf(WorkspaceEvidence evidence)
    {
        if (!IsValid(evidence)) throw new InvalidDataException("Invalid evidence attachment.");
        return Path.Combine(_folder, evidence.StoredName);
    }

    /// <summary>True when the stored file exists and still matches its recorded hash and size.</summary>
    public bool Verify(WorkspaceEvidence evidence)
    {
        if (!IsValid(evidence)) return false;
        var path = Path.Combine(_folder, evidence.StoredName);
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length != evidence.SizeBytes) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(evidence.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public void Remove(WorkspaceEvidence evidence)
    {
        var path = PathOf(evidence);
        if (File.Exists(path)) File.Delete(path);
    }

    private void EnsureFolder()
    {
        var folder = new DirectoryInfo(_folder);
        if (folder.Exists && (folder.LinkTarget is not null || (folder.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("A linked evidence folder is not supported.");
        Directory.CreateDirectory(_folder);
    }
}
