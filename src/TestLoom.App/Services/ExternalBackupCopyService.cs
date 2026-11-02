using System.IO.Compression;
using System.Security.Cryptography;

namespace TestLoom.App.Services;

/// <summary>
/// Copia facoltativa e verificata di uno snapshot ZIP locale verso una destinazione esterna
/// scelta dall'utente. Non modifica MAI la copia locale o il dataset autorevole; non scrive
/// nella cartella operativa OneDrive, non sovrascrive backup gia esistenti.
/// Gli allegati non sono inclusi negli ZIP di BackupService: sono protetti da un piano separato.
/// </summary>
public static class ExternalBackupCopyService
{
    public static string CopyVerified(string localZip, string destinationDirectory, string sharedRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localZip) || string.IsNullOrWhiteSpace(destinationDirectory) ||
            string.IsNullOrWhiteSpace(sharedRoot))
            throw new ArgumentException("Percorsi di backup mancanti.");

        var source = Path.GetFullPath(localZip);
        var targetDirectory = Path.GetFullPath(destinationDirectory);
        var dataRoot = Path.GetFullPath(sharedRoot);
        var localDirectory = Path.GetDirectoryName(source)!;

        // Impedisce di confondere il secondo backup con la normale replica OneDrive oppure
        // di copiarlo sopra il proprio ZIP locale.
        if (Inside(targetDirectory, dataRoot) || Inside(targetDirectory, localDirectory) ||
            Inside(dataRoot, targetDirectory) || Inside(localDirectory, targetDirectory))
            throw new IOException("La destinazione aggiuntiva deve essere indipendente dalle cartelle dati e backup locali.");

        if (!File.Exists(source) || !Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Snapshot ZIP locale non disponibile.", source);

        // Verifica almeno che la central directory dello ZIP sia leggibile.
        using (var archive = ZipFile.OpenRead(source))
        {
            if (archive.Entries.Count == 0)
                throw new InvalidDataException("Lo snapshot ZIP è vuoto.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(targetDirectory);
        var hash = HashOf(source, cancellationToken);
        var name = Path.GetFileNameWithoutExtension(source) + "_" + hash[..20] + ".zip";
        var destination = Path.Combine(targetDirectory, name);

        if (File.Exists(destination))
        {
            if (HashOf(destination, cancellationToken).Equals(hash, StringComparison.Ordinal))
                return destination; // idempotente, non crea copie duplicate
            throw new IOException("Esiste già una copia esterna con lo stesso nome ma contenuto differente.");
        }

        var temporary = Path.Combine(targetDirectory, ".TestLoom-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output, 128 * 1024);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!HashOf(temporary, cancellationToken).Equals(hash, StringComparison.Ordinal))
                throw new IOException("Verifica SHA-256 della copia esterna fallita.");

            File.Move(temporary, destination, overwrite: false);
            return destination;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string HashOf(string file, CancellationToken ct)
    {
        using var stream = File.OpenRead(file);
        using var sha = SHA256.Create();
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, buffer, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private static bool Inside(string path, string candidateParent)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateParent));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return p.Equals(parent, comparison) ||
            p.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }
}
