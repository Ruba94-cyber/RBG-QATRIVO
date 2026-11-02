namespace TestLoom.App.Services;

/// <summary>
/// Riconosce in modo conservativo i file che appartengono allo storage condiviso TestLoom. Serve alle routine
/// DISTRUTTIVE di housekeeping e al backup: la cartella SharePoint può contenere anche file di altri strumenti
/// e TestLoom non deve mai cancellare/copiare un artefatto solo perché ha un'estensione familiare.
/// </summary>
public static class TestLoomStorageOwnership
{
    private static readonly HashSet<string> RootJsonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "requisiti.json",
        "funzionalita.json",
        "componente.json",
        "gamma.json",
        "categorie.json",
        "veicoli.json",
        "strumenti.json",
        "operatori.json",
        "test-template.json",
        "campi-sistema-pianificazione.json",
        "campi-sistema-dettagli.json",
        "audit-risoluzioni-conflitto.json",
        "revisioni-unitlogger.json",
        "version-manifest.json",
        "ticketing-config.json",
        "catalog-migrations.json"
    };

    public static bool IsManagedJsonPath(string root, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(targetPath)) return false;
        try
        {
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullTarget = Path.GetFullPath(targetPath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!fullTarget.Equals(fullRoot, comparison) &&
                !fullTarget.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)) return false;

            if (!fullTarget.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
            var relative = Path.GetRelativePath(fullRoot, fullTarget).Replace('\\', '/');
            var fileName = Path.GetFileName(fullTarget);

            if (!relative.Contains('/') && RootJsonNames.Contains(fileName)) return true;
            if (IsVisibilityManagedDomainJsonPath(root, targetPath)) return true;

            // Gli envelope del recovery-bin sono JSON TestLoom gestiti: serve soprattutto per riconoscere i loro
            // temporary atomici durante housekeeping, non per considerarli record attivi di dominio.
            if (relative.StartsWith("recovery-bin/", StringComparison.OrdinalIgnoreCase) &&
                fileName.StartsWith("recovery_", StringComparison.OrdinalIgnoreCase)) return true;

            // Formato legacy notifiche: ancora letto/migrato, ma non confonderlo con file arbitrari.
            if (relative.StartsWith("notifiche/", StringComparison.OrdinalIgnoreCase) &&
                fileName.StartsWith("notifica_", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whitelist separata della source of truth master Phase D. `_ditm` contiene anche transazioni, receipt,
    /// recovery e altri artefatti tecnici che NON devono finire automaticamente in uno snapshot master: qui
    /// riconosciamo solo manifest/ready e journal/checkpoint bounded di `_ditm/master-v2`.
    /// </summary>
    public static bool IsPhaseDAuthoritativePath(string root, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(targetPath)) return false;
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullTarget = Path.GetFullPath(targetPath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!fullTarget.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)) return false;

            var relative = Path.GetRelativePath(fullRoot, fullTarget).Replace('\\', '/');
            if (relative.Equals("_ditm/master-v2/manifest.json", StringComparison.OrdinalIgnoreCase) ||
                relative.Equals("_ditm/master-v2/ready.json", StringComparison.OrdinalIgnoreCase)) return true;

            var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 ||
                !parts[0].Equals("_ditm", StringComparison.OrdinalIgnoreCase) ||
                !parts[1].Equals("master-v2", StringComparison.OrdinalIgnoreCase) ||
                !parts[2].Equals("journal", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(parts[3])) return false;

            if (parts.Length == 5)
            {
                if (parts[4].Equals("current.ndjson", StringComparison.OrdinalIgnoreCase)) return true;
                return IsSegmentName(parts[4]);
            }

            if (parts.Length == 6 && parts[4].Equals("checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                if (parts[5].Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) return true;
                return IsGenerationShardName(parts[5]);
            }

            return false;
        }
        catch { return false; }
    }

    /// <summary>Riconosce esclusivamente temporary tecnici Phase D con suffisso `.tmp-XXXXXXXX` oppure `.tmpXXXXXXXX` (come
    /// creati da JsonIo e dalla rimozione dei lock abbandonati), e solo se il file di destinazione è un percorso Phase D
    /// autoritativo o bootstrap. Serve alla retention post-crash: non autorizza mai la cancellazione di file estranei sotto `_ditm`.</summary>
    public static bool IsPhaseDTemporaryPath(string root, string tempPath)
    {
        try
        {
            var name = Path.GetFileName(tempPath);
            var marker = name.LastIndexOf(".tmp", StringComparison.OrdinalIgnoreCase);
            if (marker <= 0) return false;
            var suffix = name[(marker + 4)..];
            if (suffix.Length > 0 && suffix[0] == '-') suffix = suffix[1..];
            if (suffix.Length != 8 || !suffix.All(Uri.IsHexDigit)) return false;
            var originalName = name[..marker];
            var candidate = Path.Combine(Path.GetDirectoryName(tempPath) ?? root, originalName);
            return IsPhaseDAuthoritativePath(root, candidate) ||
                   IsPhaseDBootstrapTechnicalPath(root, candidate);
        }
        catch { return false; }
    }

    /// <summary>
    /// Le sole quattro famiglie che partecipano al protocollo cross-PC `_ditmTx` + commit receipt. Master data,
    /// recovery envelope, audit e notifiche possono essere protetti da before-image/rollback, ma non devono
    /// comparire nella receipt perché non sono uno stato di workflow che i repository debbano esporre insieme.
    /// </summary>
    public static bool IsVisibilityManagedDomainJsonPath(string root, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(targetPath)) return false;
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullTarget = Path.GetFullPath(targetPath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!fullTarget.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)) return false;
            if (!fullTarget.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;

            var relative = Path.GetRelativePath(fullRoot, fullTarget).Replace('\\', '/');
            var fileName = Path.GetFileName(fullTarget);
            if (fileName.StartsWith("testcase_", StringComparison.OrdinalIgnoreCase) &&
                (relative.StartsWith("testcases/", StringComparison.OrdinalIgnoreCase) ||
                 relative.StartsWith("cestino/", StringComparison.OrdinalIgnoreCase))) return true;
            if (relative.StartsWith("nonconformita/", StringComparison.OrdinalIgnoreCase) &&
                fileName.StartsWith("nc_", StringComparison.OrdinalIgnoreCase)) return true;
            if (relative.StartsWith("change-requests/", StringComparison.OrdinalIgnoreCase) &&
                fileName.StartsWith("cr_", StringComparison.OrdinalIgnoreCase)) return true;
            if (relative.StartsWith("segnalazioni/", StringComparison.OrdinalIgnoreCase) &&
                fileName.StartsWith("segnalazione_", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Whitelist più stretta per DomainRecoveryService. Master/config/notifiche non entrano mai nel cestino
    /// tecnico generico: soltanto le quattro famiglie di record di workflow possono essere archiviate/ripristinate.
    /// La categoria deve corrispondere anche alla directory/pattern fisico per impedire envelope manipolati.
    /// </summary>
    public static bool IsRecoverableDomainJsonPath(string root, string targetPath, DomainRecoveryCategory category)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(targetPath)) return false;
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullTarget = Path.GetFullPath(targetPath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!fullTarget.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)) return false;
            if (!fullTarget.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;

            var relative = Path.GetRelativePath(fullRoot, fullTarget).Replace('\\', '/');
            var fileName = Path.GetFileName(fullTarget);
            return category switch
            {
                DomainRecoveryCategory.TestCase =>
                    fileName.StartsWith("testcase_", StringComparison.OrdinalIgnoreCase) &&
                    (relative.StartsWith("testcases/", StringComparison.OrdinalIgnoreCase) ||
                     relative.StartsWith("cestino/", StringComparison.OrdinalIgnoreCase)),
                DomainRecoveryCategory.NonConformita =>
                    relative.StartsWith("nonconformita/", StringComparison.OrdinalIgnoreCase) &&
                    fileName.StartsWith("nc_", StringComparison.OrdinalIgnoreCase),
                DomainRecoveryCategory.ChangeRequest =>
                    relative.StartsWith("change-requests/", StringComparison.OrdinalIgnoreCase) &&
                    fileName.StartsWith("cr_", StringComparison.OrdinalIgnoreCase),
                DomainRecoveryCategory.Segnalazione =>
                    relative.StartsWith("segnalazioni/", StringComparison.OrdinalIgnoreCase) &&
                    fileName.StartsWith("segnalazione_", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }
        catch { return false; }
    }

    public static bool IsManagedLockPath(string root, string lockPath)
    {
        if (string.IsNullOrWhiteSpace(lockPath) || !lockPath.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            return false;
        return IsManagedJsonPath(root, lockPath[..^5]);
    }

    public static bool TryGetManagedAtomicTempTarget(string root, string tempPath, out string? targetPath)
    {
        targetPath = null;
        try
        {
            var name = Path.GetFileName(tempPath);
            var marker = name.LastIndexOf(".tmp", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return false;
            var suffix = name[(marker + 4)..];
            if (suffix.Length != 8 || !suffix.All(Uri.IsHexDigit)) return false;

            var originalName = name[..marker];
            var candidate = Path.Combine(Path.GetDirectoryName(tempPath) ?? root, originalName);
            if (!IsManagedJsonPath(root, candidate)) return false;
            targetPath = candidate;
            return true;
        }
        catch { return false; }
    }

    private static bool IsPhaseDBootstrapTechnicalPath(string root, string targetPath)
    {
        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(targetPath)).Replace('\\', '/');
            if (relative.Equals("_ditm/master-v2-bootstrap.lock.json", StringComparison.OrdinalIgnoreCase)) return true;
            return relative.StartsWith("_ditm/master-v2/bootstrap/", StringComparison.OrdinalIgnoreCase) &&
                   relative.EndsWith(".pending.json", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsSegmentName(string name)
    {
        if (!name.StartsWith("segment-", StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase)) return false;
        var core = name["segment-".Length..^".ndjson".Length];
        var parts = core.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && parts[0].Length == 20 && parts[1].Length == 20 && parts[2].Length == 32 &&
               parts[0].All(char.IsDigit) && parts[1].All(char.IsDigit) && parts[2].All(Uri.IsHexDigit);
    }

    private static bool IsGenerationShardName(string name)
    {
        if (!name.StartsWith("gen-", StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase)) return false;
        var core = name["gen-".Length..^".json.gz".Length];
        var marker = core.LastIndexOf("-shard-", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0) return false;
        var generation = core[..marker];
        var shard = core[(marker + "-shard-".Length)..];
        return generation.Length == 32 && generation.All(Uri.IsHexDigit) && shard.Length == 2 && shard.All(char.IsDigit);
    }
}
