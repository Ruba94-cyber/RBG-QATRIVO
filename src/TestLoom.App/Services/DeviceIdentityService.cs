using System.Text;

namespace TestLoom.App.Services;

/// <summary>
/// Identità locale stabile del PC/utente TestLoom. Non usa hostname o username come chiave perché possono cambiare
/// o collidere. Il file vive solo in %LOCALAPPDATA% e non richiede privilegi amministratore.
///
/// L'identità è anche la chiave di ownership dei ledger bounded (journal/notifiche/audit/receipt): non può quindi
/// essere restituita in forma volatile. Se device-id.txt è corrotto, la riparazione avviene sotto lock file
/// inter-process e il GUID viene verificato dopo la sostituzione atomica prima di essere usato.
/// </summary>
public static class DeviceIdentityService
{
    private const int LockAttempts = 40;
    private const int LockRetryDelayMs = 25;
    private static readonly object Gate = new();
    private static string? _cached;

    public static string GetOrCreate()
    {
        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(_cached)) return _cached;

            var dir = Path.Combine(WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom");
            return _cached = GetOrCreateInDirectory(dir);
        }
    }

    /// <summary>
    /// Variante testabile che mantiene le stesse garanzie del percorso produzione senza dipendere da
    /// %LOCALAPPDATA%. Il file device-id.lock è locale e permanente: è soltanto il punto di sincronizzazione
    /// tra processi, non uno stato condiviso OneDrive e non richiede cleanup/stale-lock recovery.
    /// </summary>
    internal static string GetOrCreateInDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory identità device non valida.", nameof(directory));

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "device-id.txt");
        var lockPath = Path.Combine(directory, "device-id.lock");
        IOException? lastLockError = null;

        for (var attempt = 0; attempt < LockAttempts; attempt++)
        {
            FileStream? processLock = null;
            try
            {
                processLock = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException ex)
            {
                // Solo la contesa sull'apertura del lock è ritentabile. Un errore durante ReadOrRepair deve
                // invece emergere con la sua causa reale: non va mascherato come falsa contesa del lock.
                lastLockError = ex;
                if (attempt + 1 < LockAttempts)
                {
                    Thread.Sleep(LockRetryDelayMs);
                    continue;
                }
            }

            if (processLock == null) break;
            using (processLock)
                return ReadOrRepair(path);
        }

        throw new IOException(
            "Impossibile acquisire il lock locale dell'identità TestLoom. Identità device non modificata.",
            lastLockError);
    }

    private static string ReadOrRepair(string path)
    {
        if (TryReadValid(path, out var existing)) return existing;

        var candidate = Guid.NewGuid().ToString("N");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = Encoding.ASCII.GetBytes(candidate);
            using (var stream = new FileStream(
                       temp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 256,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            // Siamo sotto device-id.lock: altri processi TestLoom non possono scegliere un secondo candidato.
            // overwrite:true serve esclusivamente a sostituire un device-id.txt esistente ma illeggibile.
            File.Move(temp, path, overwrite: File.Exists(path));

            if (!TryReadValid(path, out var persisted))
                throw new IOException("La riparazione di device-id.txt non ha prodotto un GUID persistente valido.");
            return persisted;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static bool TryReadValid(string path, out string value)
    {
        value = "";
        try
        {
            if (!File.Exists(path)) return false;
            var raw = File.ReadAllText(path).Trim();
            if (!Guid.TryParse(raw, out var guid) || guid == Guid.Empty) return false;
            value = guid.ToString("N");
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}