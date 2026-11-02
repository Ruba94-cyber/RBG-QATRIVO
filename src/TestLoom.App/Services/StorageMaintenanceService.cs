namespace TestLoom.App.Services;

/// <summary>
/// Manutenzione conservativa dello storage TestLoom. Elimina esclusivamente artefatti tecnici/effimeri:
/// lock scaduti, notifiche oltre retention, snapshot di backup oltre retention, recovery di riparazione
/// ridondanti e file temporanei lasciati da scritture atomiche interrotte. NON elimina mai
/// Test/NC/CR/Segnalazioni/master data/audit/report/allegati.
///
/// I database SQLite locali sono ricostruibili e vivono fuori da OneDrive. La manutenzione standard esegue
/// WAL checkpoint + PRAGMA optimize; VACUUM è opt-in per la pulizia manuale/aggressiva, perché richiede un
/// lock SQLite più invasivo e non deve interferire con l'uso normale dell'app.
/// </summary>
public static class StorageMaintenanceService
{
    private static readonly TimeSpan TempFileRetention = TimeSpan.FromDays(1);
    internal static readonly TimeSpan RepairBackupRetention = TimeSpan.FromDays(180);
    internal const int MaxRepairBackupsPerTarget = 3;

    public static StorageMaintenanceResult Run(DataPaths paths, LockService locks, NotificaRepository notifiche, DateTime now,
        bool ottimizzaStatisticheSqlite = true, CancellationToken annullamento = default) =>
        Run(
            paths,
            locks,
            notifiche,
            now,
            LocalSqliteMaintenanceService.ResolveWorkspaceDirectory(paths.Root),
            vacuumLocalSqlite: false,
            ottimizzaStatisticheSqlite,
            annullamento);

    /// <param name="annullamento">Annullamento cooperativo (chiusura dell'app). Ogni fase controlla il token PRIMA di iniziare:
    /// la fase già in corso termina (le scritture sono atomiche per file), le fasi successive vengono saltate. Nessuna
    /// eccezione: il risultato riporta solo le fasi eseguite.</param>
    public static StorageMaintenanceResult Run(
        DataPaths paths,
        LockService locks,
        NotificaRepository notifiche,
        DateTime now,
        string? localWorkspaceDirectory,
        bool vacuumLocalSqlite,
        bool ottimizzaStatisticheSqlite = true,
        CancellationToken annullamento = default)
    {
        bool Prosegui() => !annullamento.IsCancellationRequested;
        // Questo servizio combina cleanup CONDIVISO e ottimizzazione SQLite locale. Non affidarsi al solo
        // caller AppState: se invocato direttamente mentre il workspace è sola lettura/frozen non deve poter
        // eliminare lock/temp/backup/checkpoint condivisi. La sola manutenzione locale resta disponibile tramite
        // LocalSqliteMaintenanceService, che non necessita di questo gate.
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Manutenzione storage condiviso");

        var lockResult = new LockCleanupResult(0, 0, 0);
        var notificationResult = new NotificationMaintenanceResult(0, 0, 0);
        var sqliteResult = new LocalSqliteMaintenanceResult(0, 0, 0, 0);
        var backupTemp = 0;
        var genericTemp = 0;
        var repairBackups = 0;
        var emptyTechnicalDirs = 0;
        var errors = 0;

        if (Prosegui()) try { lockResult = locks.CleanupStaleLocks(paths.Root); }
        catch (Exception ex) { errors++; CrashLogService.Registra("Manutenzione storage: lock", ex); }

        if (Prosegui()) try { notificationResult = notifiche.RunMaintenance(force: true); }
        catch (Exception ex) { errors++; CrashLogService.Registra("Manutenzione storage: notifiche", ex); }

        if (Prosegui()) try
        {
            // Modifica minima 6.4.2 (fuori dall'ownership stream B, segnalata nel report finale): dalla 6.4.2 gli
            // snapshot di backup sono SOLO locali (workspaceDirectory\backup, vedi BackupService/
            // WorkspaceLocalPaths), non più in paths.BackupDir (condiviso). Continuare ad applicare qui la
            // retention alla vecchia cartella condivisa avrebbe cancellato zip legacy che l'app non deve più
            // toccare; localWorkspaceDirectory è già la stessa cartella per-workspace usata per l'indice SQLite.
            var backupDirLocale = string.IsNullOrEmpty(localWorkspaceDirectory)
                ? null : Path.Combine(localWorkspaceDirectory, "backup");
            // ApplicaRetention pulisce già i temporary di backup e ne restituisce il conteggio. Richiamare una
            // seconda volta PulisciTemporanei produceva una scansione inutile e telemetria sempre a zero.
            backupTemp = backupDirLocale != null ? BackupService.ApplicaRetention(backupDirLocale, now) : 0;
        }
        catch (Exception ex) { errors++; CrashLogService.Registra("Manutenzione storage: backup", ex); }

        if (Prosegui()) try { genericTemp = CleanupStaleTechnicalTempFiles(paths.Root, now); }
        catch (Exception ex) { errors++; CrashLogService.Registra("Manutenzione storage: temporanei", ex); }

        if (Prosegui()) try { repairBackups = CleanupRepairBackups(paths.Root, now); }
        catch (Exception ex) { errors++; CrashLogService.Registra("Manutenzione storage: backup riparazione JSON", ex); }

        if (Prosegui()) try { emptyTechnicalDirs = CleanupEmptyTechnicalDirectories(paths.Root); }
        catch (Exception ex) { errors++; CrashLogService.Registra("Manutenzione storage: cartelle tecniche vuote", ex); }

        if (Prosegui()) try { sqliteResult = LocalSqliteMaintenanceService.OptimizeWorkspace(localWorkspaceDirectory, vacuumLocalSqlite, ottimizzaStatisticheSqlite); }
        catch (Exception ex) { errors++; CrashLogService.Registra("Manutenzione storage: SQLite locale", ex); }

        return new StorageMaintenanceResult(
            lockResult.TotaleRimossi,
            notificationResult.LegacyJsonEliminati,
            notificationResult.StatiScadutiEliminati,
            notificationResult.FileLedgerCompattati,
            backupTemp,
            genericTemp + repairBackups,
            emptyTechnicalDirs,
            errors + lockResult.Errori + sqliteResult.Errori,
            sqliteResult.DatabaseOttimizzati,
            sqliteResult.DatabaseVacuum,
            sqliteResult.ByteRecuperati);
    }

    /// <summary>
    /// Pulisce soltanto temporary che TestLoom può provare di possedere:
    /// - JsonIo.WriteAtomic: `&lt;TestLoom-json&gt;.tmpXXXXXXXX`, dove il target originale è riconosciuto da
    ///   TestLoomStorageOwnership;
    /// - temporary Phase D `&lt;target&gt;.tmp-XXXXXXXX`, soltanto se il target appartiene alla whitelist master-v2;
    /// - file `.tmp` sotto `_ditm`, namespace tecnico riservato a TestLoom.
    ///
    /// Un file di un altro software come `thirdparty.json.tmp1234ABCD` resta intatto anche se vecchio. Richiede
    /// inoltre almeno 24h di anzianità, così non interferisce con una scrittura/sync corrente.
    /// </summary>
    internal static int CleanupStaleTechnicalTempFiles(string root, DateTime now)
    {
        if (!Directory.Exists(root)) return 0;
        var removed = 0;
        var cutoffUtc = now.ToUniversalTime() - TempFileRetention;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            try
            {
                var name = Path.GetFileName(path);
                var isAtomicJsonTemp = TestLoomStorageOwnership.TryGetManagedAtomicTempTarget(root, path, out _);
                var isPhaseDTemp = TestLoomStorageOwnership.IsPhaseDTemporaryPath(root, path);
                var isDitmTemp = IsUnderDitm(path, root) && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                if (!isAtomicJsonTemp && !isPhaseDTemp && !isDitmTemp) continue;

                var lastWrite = File.GetLastWriteTimeUtc(path);
                if (lastWrite == default || lastWrite > cutoffUtc) continue;
                File.Delete(path);
                removed++;
            }
            catch { }
        }
        return removed;
    }

    /// <summary>
    /// `FileRepairService` conserva l'originale prima di una riparazione come
    /// `&lt;target.json&gt;.corrotto-yyyyMMdd-HHmmss.bak`. È recovery utile ma non deve crescere senza limite.
    /// Per ogni target TestLoom teniamo SEMPRE la copia più recente; delle precedenti ne teniamo al massimo altre
    /// due e solo per 180 giorni. Un `.bak` non riconducibile con certezza a un JSON TestLoom non viene mai toccato.
    /// </summary>
    internal static int CleanupRepairBackups(string root, DateTime now)
    {
        if (!Directory.Exists(root)) return 0;
        var groups = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        foreach (var path in Directory.EnumerateFiles(root, "*.bak", options))
        {
            try
            {
                var fileName = Path.GetFileName(path);
                var marker = fileName.IndexOf(".corrotto-", StringComparison.OrdinalIgnoreCase);
                if (marker <= 0 || !fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                var originalName = fileName[..marker];
                var target = Path.Combine(Path.GetDirectoryName(path) ?? root, originalName);
                if (!TestLoomStorageOwnership.IsManagedJsonPath(root, target)) continue;
                if (!groups.TryGetValue(target, out var list)) groups[target] = list = new List<FileInfo>();
                list.Add(new FileInfo(path));
            }
            catch { }
        }

        var cutoffUtc = now.ToUniversalTime() - RepairBackupRetention;
        var removed = 0;
        foreach (var group in groups.Values)
        {
            var ordered = group
                .OrderByDescending(f => SafeLastWriteUtc(f))
                .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                // La copia più recente è una rete di sicurezza e resta sempre disponibile.
                if (i == 0) continue;
                var old = SafeLastWriteUtc(ordered[i]) <= cutoffUtc;
                var overCount = i >= MaxRepairBackupsPerTarget;
                if (!old && !overCount) continue;
                try { ordered[i].Delete(); removed++; } catch { }
            }
        }
        return removed;
    }

    private static DateTime SafeLastWriteUtc(FileInfo file)
    {
        try { return file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    private static bool IsUnderDitm(string path, string root)
    {
        try
        {
            var testloom = Path.GetFullPath(Path.Combine(root, "_ditm"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(testloom, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>Rimuove soltanto directory vuote sotto _ditm/conflicts. Sono contenitori tecnici TestLoom.</summary>
    internal static int CleanupEmptyTechnicalDirectories(string root)
    {
        var conflicts = Path.Combine(root, "_ditm", "conflicts");
        if (!Directory.Exists(conflicts)) return 0;
        var removed = 0;
        foreach (var dir in SafeDirectoriesDeepestFirst(conflicts))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any()) continue;
                Directory.Delete(dir, false);
                removed++;
            }
            catch { }
        }
        return removed;
    }

    private static IEnumerable<string> SafeDirectoriesDeepestFirst(string root)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, ReturnSpecialDirectories = false };
            return Directory.EnumerateDirectories(root, "*", options)
                .OrderByDescending(p => p.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }
}

/// <summary>I primi otto parametri mantengono l'ordine storico; i campi SQLite sono estensioni in coda.</summary>
public sealed record StorageMaintenanceResult(
    int LockRimossi,
    int NotificheLegacyJsonEliminate,
    int StatiNotificaScadutiEliminati,
    int FileLedgerNotificheCompattati,
    int BackupTemporaneiEliminati,
    int TemporaneiTecniciEliminati,
    int CartelleTecnicheVuoteEliminate,
    int Errori,
    int DatabaseLocaliOttimizzati = 0,
    int DatabaseLocaliVacuum = 0,
    long ByteLocaliRecuperati = 0)
{
    public int TotaleArtefattiEliminati => LockRimossi + NotificheLegacyJsonEliminate + StatiNotificaScadutiEliminati +
                                           BackupTemporaneiEliminati + TemporaneiTecniciEliminati + CartelleTecnicheVuoteEliminate;
}
