namespace TestLoom.App.Services;

/// <summary>
/// Riconoscimento (solo per NOME file, nessuna lettura del contenuto) delle copie di conflitto OneDrive/SharePoint
/// ESPLICITE di un record: " (Laptop)", " - Copia", "-PC2", "_LAPTOP-...". Stessa regola di
/// <see cref="VisibleConflictWriteGuard"/>; in piu' richiede che il file canonico <c>&lt;stem&gt;.json</c> esista accanto,
/// cosi' un record distinto con un nome simile non viene mai scambiato per una copia.
/// Serve a tenere le copie FUORI dagli inventari operativi della cartella condivisa (vedi
/// <see cref="WorkspaceOperationalFileEnumerator"/>) e a individuarle per la quarantena
/// (<see cref="ConflictCopyQuarantineService"/>): i conflitti si gestiscono solo nella sezione Conflitti.
/// </summary>
public static class ConflictCopyClassifier
{
    /// <summary>True se <paramref name="copyPath"/> e' una copia di conflitto esplicita il cui canonico esiste nella stessa cartella.</summary>
    public static bool TryGetCanonicalPath(string copyPath, out string canonicalPath)
    {
        canonicalPath = "";
        try
        {
            var dir = Path.GetDirectoryName(copyPath);
            if (string.IsNullOrEmpty(dir)) return false;
            var stem = Path.GetFileNameWithoutExtension(copyPath);
            if (!copyPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;

            // Il suffisso esplicito inizia sempre con spazio, '-' o '_'; gli altri punti non si testano.
            for (var i = 1; i < stem.Length; i++)
            {
                if (stem[i] is not (' ' or '-' or '_')) continue;
                if (!VisibleConflictWriteGuard.EsSuffissoConflittoEsplicito(stem[i..])) continue;
                var candidate = Path.Combine(dir, stem[..i] + ".json");
                if (File.Exists(candidate))
                {
                    canonicalPath = candidate;
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static bool IsConflictCopy(string copyPath) => TryGetCanonicalPath(copyPath, out _);
}
