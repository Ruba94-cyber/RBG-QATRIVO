using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Riconcilia i binari allegati quando l'utente sceglie ESPRESSAMENTE di scartare una sessione di modifica.
/// Un crash non passa mai da qui: in quel caso file e recovery restano intatti per il recupero della bozza.
///
/// Regole fail-safe:
/// - un allegato presente nello snapshot autorevole non viene mai cancellato, anche se il tracking UI lo ha
///   erroneamente classificato come "aggiunto nella sessione" durante un refresh/merge della collection;
/// - un allegato nuovo, mai entrato nel JSON autorevole, viene eliminato dal bucket attivo/recovery SOLO se il
///   suo SHA dimostra che quei byte sono ancora quelli della sessione locale; un file concorrente resta intatto;
/// - un allegato autorevole rimosso nel form viene rimesso nel percorso attivo dalla recovery soltanto se la
///   recovery corrisponde al contenuto atteso. Se il restore fallisce o il path attivo è stato occupato da
///   altri byte, la recovery NON viene eliminata: spazio/staging in più è recuperabile, un'evidenza persa no;
/// - Test e Change Request condividono lo stesso algoritmo e differiscono soltanto per il bucket tecnico.
/// </summary>
public static class DraftAttachmentCleanupService
{
    private const string RecoveryDirectoryName = ".testloom-recovery";

    public static void ReconcileAfterExplicitDiscard(
        DataPaths paths,
        TestCase workingCopy,
        TestCase? authoritativeSnapshot,
        IEnumerable<Guid>? trackedAddedUids = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(workingCopy);

        ReconcileCore(
            paths,
            workingCopy.Allegati,
            authoritativeSnapshot?.Allegati,
            paths.AllegatiDir(workingCopy),
            authoritativeSnapshot == null ? null : paths.AllegatiDir(authoritativeSnapshot),
            trackedAddedUids);
    }

    /// <summary>
    /// Variante Change Request. A differenza del Test non serve un tracking esplicito degli UID aggiunti:
    /// gli upload CR sono ammessi solo su CR già persistite, quindi la differenza working-vs-autorevole è una
    /// baseline sufficiente. L'autorevole va riletto dal repository immediatamente prima di questa chiamata:
    /// una modifica remota arrivata durante la sessione viene così trattata come fonte di verità, mai rimossa.
    /// </summary>
    public static void ReconcileAfterExplicitDiscard(
        DataPaths paths,
        ChangeRequest workingCopy,
        ChangeRequest? authoritativeSnapshot)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(workingCopy);

        ReconcileCore(
            paths,
            workingCopy.Allegati,
            authoritativeSnapshot?.Allegati,
            paths.AllegatiDirChangeRequest(workingCopy),
            authoritativeSnapshot == null ? null : paths.AllegatiDirChangeRequest(authoritativeSnapshot),
            trackedAddedUids: null);
    }

    /// <summary>Variante Non Conformità: include anche gli allegati caricati prima del primo salvataggio;
    /// in quel caso non esiste ancora uno snapshot autorevole e il bucket UID temporaneo viene riconciliato
    /// come staging locale. Per una NC esistente resta valida la baseline autorevole appena riletta.</summary>
    public static void ReconcileAfterExplicitDiscard(
        DataPaths paths,
        NonConformita workingCopy,
        NonConformita? authoritativeSnapshot)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(workingCopy);

        ReconcileCore(
            paths,
            workingCopy.Allegati,
            authoritativeSnapshot?.Allegati,
            paths.AllegatiDirNonConformita(workingCopy),
            authoritativeSnapshot == null ? null : paths.AllegatiDirNonConformita(authoritativeSnapshot),
            trackedAddedUids: null);
    }

    private static void ReconcileCore(
        DataPaths paths,
        IReadOnlyCollection<Allegato> workingAttachments,
        IReadOnlyCollection<Allegato>? authoritativeAttachments,
        string workingBucket,
        string? authoritativeBucket,
        IEnumerable<Guid>? trackedAddedUids)
    {
        // Anche la cleanup è una mutazione condivisa. Se nel frattempo il workspace è diventato incompatibile,
        // offline o è comparsa una transazione straniera, non tocchiamo nulla: lasciare un file di staging in
        // più è recuperabile, cancellare/ripristinare mentre lo stato è congelato non lo è.
        if (!WorkspaceWritePolicy.CanWrite(paths)) return;

        var authoritative = authoritativeAttachments?
            .Where(a => a.Uid != Guid.Empty)
            .GroupBy(a => a.Uid)
            .ToDictionary(g => g.Key, g => g.First())
            ?? new Dictionary<Guid, Allegato>();
        var authoritativeUids = authoritative.Keys.ToHashSet();

        var workingByUid = workingAttachments
            .Where(a => a.Uid != Guid.Empty)
            .GroupBy(a => a.Uid)
            .ToDictionary(g => g.Key, g => g.First());

        var candidates = new HashSet<Guid>(trackedAddedUids ?? Array.Empty<Guid>());
        foreach (var uid in workingByUid.Keys.Where(uid => !authoritativeUids.Contains(uid))) candidates.Add(uid);
        candidates.ExceptWith(authoritativeUids); // guard finale: lo snapshot autorevole vince SEMPRE.

        var recoveryDir = Path.Combine(workingBucket, RecoveryDirectoryName);

        // Prima rimuoviamo i file mai autorevoli. È importante farlo prima del restore: un nuovo allegato può
        // avere riutilizzato lo stesso NomeFile di un autorevole rimosso nella stessa sessione.
        foreach (var uid in candidates)
        {
            try
            {
                if (workingByUid.TryGetValue(uid, out var item))
                {
                    TryDeleteAttachmentIfOwned(
                        Path.Combine(workingBucket, Path.GetFileName(item.NomeFile)),
                        workingBucket,
                        item);

                    if (Directory.Exists(recoveryDir))
                    {
                        foreach (var path in Directory.EnumerateFiles(recoveryDir, uid.ToString("N") + ".*", SearchOption.TopDirectoryOnly))
                            TryDeleteAttachmentIfOwned(path, recoveryDir, item);
                    }
                }
                else if (Directory.Exists(recoveryDir))
                {
                    // L'utente ha già rimosso l'allegato dalla collezione in sessione (es. Elimina + Remove()
                    // dalla UI prima di abbandonare): non abbiamo più NomeFile/SHA in memoria per verificarlo,
                    // ma il file di recovery è nominato per UID quindi non può collidere con un altro allegato.
                    // Essendo garantito "nuovo di questa sessione" (mai autorevole, tracciato tra gli UID
                    // aggiunti) è sicuro eliminarlo senza il confronto byte-per-byte riservato al bucket attivo.
                    foreach (var path in Directory.EnumerateFiles(recoveryDir, uid.ToString("N") + ".*", SearchOption.TopDirectoryOnly))
                    {
                        try { File.Delete(path); }
                        catch (Exception ex) { CrashLogService.Registra($"Cleanup recovery allegato rimosso in sessione {uid:N}", ex); }
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLogService.Registra($"Cleanup allegato bozza scartata {uid:N}", ex);
            }
        }

        if (authoritativeBucket != null)
        {
            foreach (var item in authoritative.Values)
            {
                try { RestoreAuthoritativeIfNeeded(authoritativeBucket, item); }
                catch (Exception ex) { CrashLogService.Registra($"Restore allegato autorevole {item.Uid:N}", ex); }
            }
        }

        TryDeleteDirectoryIfEmpty(recoveryDir);
        TryDeleteDirectoryIfEmpty(workingBucket);
    }

    private static void RestoreAuthoritativeIfNeeded(string bucket, Allegato item)
    {
        var active = Path.Combine(bucket, Path.GetFileName(item.NomeFile));
        if (File.Exists(active))
        {
            // Non basta il nome: una replica può aver prodotto altri byte sullo stesso path. Se non sono quelli
            // dell'allegato autorevole non li sostituiamo e lasciamo intatta l'eventuale recovery.
            if (!AttachmentMatches(active, item))
                CrashLogService.Registra("Restore allegato autorevole",
                    new IOException($"Il path attivo '{Path.GetFileName(active)}' contiene byte diversi dall'allegato autorevole {item.Uid:N}."));
            return;
        }

        var recovery = RecoveryPath(bucket, item);
        if (!File.Exists(recovery)) return;
        if (!AttachmentMatches(recovery, item))
        {
            CrashLogService.Registra("Restore allegato autorevole",
                new IOException($"La recovery dell'allegato {item.Uid:N} non corrisponde allo SHA autorevole."));
            return;
        }

        Directory.CreateDirectory(bucket);
        var temp = active + ".restore-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var input = new FileStream(recovery, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       1024 * 1024, FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                input.CopyTo(output, 1024 * 1024);
                output.Flush(flushToDisk: true);
            }

            // Verifica anche la copia temporanea: un errore I/O silenzioso non deve promuovere byte corrotti.
            if (!AttachmentMatches(temp, item))
                throw new IOException($"La copia di restore dell'allegato {item.Uid:N} non corrisponde allo SHA autorevole.");

            File.Move(temp, active, overwrite: false);
            try { File.Delete(recovery); }
            catch (Exception ex) { CrashLogService.Registra("Pulizia recovery allegato dopo restore", ex); }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static string RecoveryPath(string bucket, Allegato item) =>
        Path.Combine(bucket, RecoveryDirectoryName,
            item.Uid.ToString("N") + Path.GetExtension(item.NomeFile));

    private static void TryDeleteAttachmentIfOwned(string path, string expectedParent, Allegato item)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var parent = Path.GetFullPath(expectedParent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(parent, comparison) || !File.Exists(full)) return;

            // Gli upload nuovi hanno sempre SHA. Se un vecchio record ne fosse privo non esiste una prova
            // sufficiente che il file attuale appartenga alla sessione da scartare: fail-closed, non cancellare.
            if (string.IsNullOrWhiteSpace(item.Sha256) || !AttachmentMatches(full, item)) return;
            File.Delete(full);
        }
        catch (Exception ex) { CrashLogService.Registra($"Cleanup file allegato bozza: {path}", ex); }
    }

    private static bool AttachmentMatches(string path, Allegato item)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (item.DimensioneByte > 0 && new FileInfo(path).Length != item.DimensioneByte) return false;
            if (string.IsNullOrWhiteSpace(item.Sha256)) return true;
            return AttachmentService.CalcolaSha256(path).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        catch { }
    }
}
