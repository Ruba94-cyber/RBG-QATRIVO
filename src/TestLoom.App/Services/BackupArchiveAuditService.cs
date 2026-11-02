using System.IO.Compression;
using System.Security.Cryptography;

namespace TestLoom.App.Services;

/// <summary>
/// Offline backup ZIP verification, completely read-only. Checks archive structure,
/// entry boundaries, duplicate names and full decompression, plus SHA-256 of the ZIP as a whole.
/// It NEVER extracts or changes TestLoom's authoritative shared workspace.
/// </summary>
public static class BackupArchiveAuditService
{
    public sealed record AuditResult(bool Valid, int FileCount, long TotalUncompressedBytes,
        string ArchiveSha256, IReadOnlyList<string> Warnings)
    {
        public string Summary => Valid
            ? $"ZIP verificato: {FileCount} file, {TotalUncompressedBytes} byte decompressi. " +
              "Questo NON include gli allegati online-only e non certifica OneDrive."
            : "ZIP non affidabile: " + string.Join("; ", Warnings.Take(6));
    }

    /// <summary>
    /// Protect low-power machines against zip bombs or highly fragmented archives.
    /// Fixed safety limits are for audit only, not limits on TestLoom record counts.
    /// </summary>
    public static AuditResult Verify(string archivePath, CancellationToken ct = default,
        int maxEntries = 250_000, long maxUncompressedBytes = 16L * 1024 * 1024 * 1024)
    {
        if (maxEntries <= 0 || maxUncompressedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return new(false, 0, 0, "", new[] { "Archivio ZIP mancante." });

        var warnings = new List<string>();
        var entriesRead = 0;
        long totalBytes = 0;
        string archiveHash = "";
        try
        {
            using (var sha = SHA256.Create())
            using (var raw = File.OpenRead(archivePath))
                archiveHash = Convert.ToHexString(sha.ComputeHash(raw));

            using var zip = ZipFile.OpenRead(archivePath);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var buffer = new byte[128 * 1024];
            var hasManifest = false;

            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (name.Length == 0 || name.EndsWith("/", StringComparison.Ordinal))
                    continue;
                if (name == "snapshot-manifest.txt") hasManifest = true;
                if (++entriesRead > maxEntries)
                    throw new InvalidDataException("Troppi file per un audit bounded; usare un piano di verifica dedicato.");
                if (!SafeRelativeName(name) || !paths.Add(name))
                    throw new InvalidDataException("Percorso di file ZIP insicuro o duplicato.");
                if (entry.Length < 0 || entry.Length > maxUncompressedBytes - totalBytes)
                    throw new InvalidDataException("Dimensione decompressa fuori dal limite di sicurezza.");
                // Disallow symlinks encoded as Unix file types; no extraction is attempted.
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Collegamento simbolico nello ZIP non consentito.");

                long actualSize = 0;
                using (var stream = entry.Open())
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var bytes = stream.Read(buffer, 0, buffer.Length);
                        if (bytes == 0) break;
                        actualSize += bytes;
                        if (actualSize > entry.Length || actualSize > maxUncompressedBytes - totalBytes)
                            throw new InvalidDataException("Flusso ZIP decomprime oltre la dimensione dichiarata.");
                    }
                }
                if (actualSize != entry.Length)
                    throw new InvalidDataException("File ZIP troncato durante la lettura.");
                totalBytes += actualSize;
            }
            if (entriesRead == 0) throw new InvalidDataException("Lo ZIP non contiene file.");
            if (!hasManifest)
                warnings.Add("Manifest dello snapshot TestLoom non presente; verificare origine dell'archivio.");
            // Missing manifest is an indication of non-native/archive invalid; never silently
            // certify it as a TestLoom restore source.
            return new(hasManifest && warnings.Count == 0, entriesRead, totalBytes, archiveHash, warnings);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            warnings.Add(ex.Message);
            return new(false, entriesRead, totalBytes, archiveHash, warnings);
        }
    }

    private static bool SafeRelativeName(string name)
    {
        if (name.Length > 1024 || name.StartsWith("/", StringComparison.Ordinal) ||
            name.Contains(':') || name.Contains('\0')) return false;
        var parts = name.Split('/');
        return parts.Length > 0 && parts.All(s =>
            s.Length > 0 && s != "." && s != ".." && !s.EndsWith(" ", StringComparison.Ordinal));
    }
}
