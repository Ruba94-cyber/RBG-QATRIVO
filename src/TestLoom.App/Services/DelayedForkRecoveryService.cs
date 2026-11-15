using System.IO.Compression;
using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Accesso ai payload completi pubblicati in _ditm/conflicts soltanto quando il journal rileva un vero fork.
/// I recovery non sono fonte di verità: servono esclusivamente a riconciliare i rami, poi possono essere
/// eliminati dopo che il Test autorevole contiene lo stamp di risoluzione.
/// </summary>
public static class DelayedForkRecoveryService
{
    /// <summary>
    /// Un recovery può sincronizzarsi prima del journal che ne descrive il fork. Se lo stesso Test ha già
    /// risolto un conflitto storico, cancellare immediatamente qualunque recovery non associato a un fork
    /// attualmente visibile rischierebbe di eliminare il ramo di un NUOVO conflitto prima che il relativo
    /// evento journal arrivi. I recovery non riconducibili a un fork aperto vengono quindi considerati orfani
    /// soltanto dopo una finestra ampia. I recovery di un fork appena risolto continuano invece a essere
    /// eliminati subito da CleanupResolvedRecovery, che conosce esattamente gli EventId coinvolti.
    /// </summary>
    public static readonly TimeSpan LateRecoveryGracePeriod = TimeSpan.FromDays(7);

    public static ForkRecoveryLoadResult LoadAllBranches(
        DataPaths paths,
        JournalForkConflict fork,
        TestCase current,
        IEnumerable<TestCase>? repositoryCopies = null)
    {
        var expected = fork.Branches
            .Where(b => !string.IsNullOrWhiteSpace(b.NewHash))
            .GroupBy(b => b.NewHash!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        var currentHash = TestCaseFingerprint.Compute(current);
        var byHash = new Dictionary<string, TestCase>(StringComparer.OrdinalIgnoreCase) { [currentHash] = Clone(current) };

        foreach (var copy in repositoryCopies ?? Array.Empty<TestCase>())
        {
            if (copy.Uid != fork.EntityUid) continue;
            try
            {
                var hash = TestCaseFingerprint.Compute(copy);
                if (expected.Any(e => e.NewHash!.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                    byHash.TryAdd(hash, Clone(copy));
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Recupero fork: copia del Test non elaborabile", ex);
            }
        }

        var dir = Path.Combine(paths.Root, "_ditm", "conflicts", fork.EntityUid.ToString("N"));
        foreach (var branch in expected)
        {
            if (byHash.ContainsKey(branch.NewHash!)) continue;
            var file = Path.Combine(dir, branch.EventId + ".json.gz");
            var recovered = TryReadRecovery(file);
            if (recovered == null || recovered.Uid != fork.EntityUid) continue;
            try
            {
                var hash = TestCaseFingerprint.Compute(recovered);
                if (!hash.Equals(branch.NewHash, StringComparison.OrdinalIgnoreCase)) continue;
                byHash[hash] = recovered;
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Recupero fork: payload di recupero non valido", ex);
            }
        }

        var missing = expected.Where(b => !byHash.ContainsKey(b.NewHash!)).ToList();
        var ordered = expected.Where(b => byHash.ContainsKey(b.NewHash!))
            .Select(b => new ForkRecoveryBranch(b, byHash[b.NewHash!])).ToList();
        return new ForkRecoveryLoadResult(ordered, missing);
    }

    /// <summary>
    /// Una riga per ramo mancante (evento, data, postazione) con il motivo: file non ancora arrivato oppure presente ma non valido.
    /// Serve all'operatore per sapere quale postazione deve sincronizzare, invece di un conteggio generico.
    /// </summary>
    public static string DescriviMancanti(DataPaths paths, JournalForkConflict fork, IEnumerable<TestJournalEvent> missing)
    {
        var dir = Path.Combine(paths.Root, "_ditm", "conflicts", fork.EntityUid.ToString("N"));
        static string Breve(string s) => string.IsNullOrEmpty(s) ? "?" : s.Length <= 8 ? s : s[..8];
        var righe = missing.Select(e =>
        {
            var chiave = File.Exists(Path.Combine(dir, e.EventId + ".json.gz")) ? "ForkRamoMancanteNonValidoFmt" : "ForkRamoMancanteAssenteFmt";
            return string.Format(LocalizationService.Instance.Traduci(chiave), Breve(e.EventId),
                e.TimestampUtc == default ? "?" : e.TimestampUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), Breve(e.DeviceId));
        });
        return string.Join(Environment.NewLine, righe);
    }

    /// <summary>Attesa minima prima di poter risolvere un conflitto con i soli rami disponibili.</summary>
    public static readonly TimeSpan PartialResolutionMinimumWait = TimeSpan.FromHours(24);

    /// <summary>
    /// Tempo ancora da attendere prima che la risoluzione parziale sia consentita (null = consentita). Si conta dall'evento
    /// mancante più recente: un ramo pubblicato da poco ha ancora tempo di arrivare da OneDrive.
    /// </summary>
    public static TimeSpan? AttesaResiduaRisoluzioneParziale(IEnumerable<TestJournalEvent> missing, DateTime nowUtc)
    {
        var ultimo = missing.Select(e => e.TimestampUtc).DefaultIfEmpty(default).Max();
        if (ultimo == default) return null;
        var residuo = ultimo.ToUniversalTime() + PartialResolutionMinimumWait - nowUtc.ToUniversalTime();
        return residuo > TimeSpan.Zero ? residuo : null;
    }

    /// <summary>
    /// Scrive nel recovery-bin il record dei rami che mancano (eventi completi, hash dei rami disponibili, operatore) PRIMA di
    /// una risoluzione parziale: se il payload arriva dopo, resta identificabile e recuperabile. Create-only; se fallisce la
    /// risoluzione non deve proseguire (l'eccezione risale al chiamante).
    /// </summary>
    public static string ArchiviaRamiMancanti(DataPaths paths, JournalForkConflict fork, ForkRecoveryLoadResult recovery,
        string operatore, DateTime nowUtc)
    {
        var dir = Path.Combine(paths.RecoveryBinDir, "fork-mancanti", fork.EntityUid.ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, nowUtc.ToUniversalTime().ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        var record = new
        {
            Tipo = "RisoluzioneParzialeForkTest",
            RegistratoIlUtc = nowUtc.ToUniversalTime(),
            Operatore = operatore,
            fork.EntityUid,
            fork.BaseRevision,
            fork.BaseHash,
            RamiMancanti = recovery.Missing,
            HashRamiDisponibili = recovery.Available.Select(b => b.Event.NewHash).ToList(),
        };
        JsonIo.WriteAtomic(file, record, overwrite: false);
        return file;
    }

    public static int CleanupResolvedRecovery(DataPaths paths, JournalForkConflict fork)
    {
        // Anche la rimozione di un recovery è una mutazione condivisa. Un refresh o una risoluzione arrivati
        // mentre il tenant è offline/incompatibile o mentre un'altra replica ha una transaction pending non
        // devono cancellare nulla. L'eventuale transaction corrente è ignorata dal gate tramite ActiveId.
        if (!WorkspaceWritePolicy.CanWrite(paths)) return 0;

        var dir = Path.Combine(paths.Root, "_ditm", "conflicts", fork.EntityUid.ToString("N"));
        if (!Directory.Exists(dir)) return 0;
        var removed = 0;
        foreach (var eventId in fork.Branches.Select(b => b.EventId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.Combine(dir, eventId + ".json.gz");
            try
            {
                if (!File.Exists(file)) continue;
                File.Delete(file);
                removed++;
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Recupero fork: file di recupero non eliminabile", ex);
            }
        }
        TryDeleteEmptyDirectory(dir);
        return removed;
    }

    /// <summary>
    /// Rete di sicurezza per propagazioni OneDrive fuori ordine. Il caso normale viene pulito immediatamente
    /// da CleanupResolvedRecovery usando gli EventId del fork appena risolto. Qui gestiamo soltanto recovery
    /// arrivati *dopo* la risoluzione o rimasti senza journal visibile.
    ///
    /// Per evitare perdita dati con l'ordine inverso (recovery di un NUOVO fork che arriva prima del suo
    /// journal), un file viene eliminato soltanto se:
    /// - per quell'UID non esiste alcun fork aperto;
    /// - il Test autorevole contiene almeno uno stamp di fork risolto;
    /// - il recovery è più vecchio della grace period.
    ///
    /// Un recovery fresco è quindi sempre preservato finché OneDrive ha tempo di consegnare journal/JSON.
    /// </summary>
    public static int CleanupResolvedDirectories(
        DataPaths paths,
        IEnumerable<TestCase> authoritativeTests,
        IEnumerable<JournalForkConflict> unresolvedForks,
        DateTime? nowUtcOverride = null)
    {
        // Questa funzione viene chiamata anche dal percorso di refresh. In sola lettura un refresh deve essere
        // realmente non mutante: nessuna cleanup tecnica è abbastanza urgente da giustificare File.Delete
        // senza avere prima dimostrato che il workspace è scrivibile e privo di transaction straniere.
        if (!WorkspaceWritePolicy.CanWrite(paths)) return 0;

        var root = Path.Combine(paths.Root, "_ditm", "conflicts");
        if (!Directory.Exists(root)) return 0;
        var nowUtc = (nowUtcOverride ?? DateTime.UtcNow).ToUniversalTime();
        var cutoffUtc = nowUtc - LateRecoveryGracePeriod;
        var openUids = unresolvedForks.Select(f => f.EntityUid).ToHashSet();
        var resolvedTests = authoritativeTests
            .Where(t => t.Uid != Guid.Empty && TestCaseForkResolution.GetResolvedKeys(t).Count > 0)
            .GroupBy(t => t.Uid)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.RevisioneDati).First());
        var removed = 0;

        foreach (var dir in SafeDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out var uid)) continue;
            if (openUids.Contains(uid) || !resolvedTests.ContainsKey(uid)) continue;
            foreach (var file in SafeFiles(dir, "*.json.gz"))
            {
                try
                {
                    var lastWriteUtc = File.GetLastWriteTimeUtc(file);
                    if (lastWriteUtc == default || lastWriteUtc > cutoffUtc) continue;
                    ArchiviaRecoveryScaduto(paths, uid, file);
                    removed++;
                }
                catch (Exception ex)
                {
                    CrashLogService.Registra("Recupero fork: file di recupero scaduto non eliminabile", ex);
                }
            }
            // Anche temp vecchi vengono gestiti dalla manutenzione storage; qui rimuoviamo la directory solo
            // se davvero vuota, senza toccare file sconosciuti.
            TryDeleteEmptyDirectory(dir);
        }
        return removed;
    }

    /// <summary>
    /// Un payload di ramo arrivato in ritardo e ormai scaduto non si cancella: contiene lavoro di un operatore. Si sposta nel
    /// recovery-bin (stesso volume, quindi atomico), dove resta nei backup e senza retention, ripristinabile a mano.
    /// </summary>
    private static void ArchiviaRecoveryScaduto(DataPaths paths, Guid uid, string file)
    {
        var dir = Path.Combine(paths.RecoveryBinDir, "fork-tardivi", uid.ToString("N"));
        Directory.CreateDirectory(dir);
        var destinazione = Path.Combine(dir, Path.GetFileName(file));
        if (File.Exists(destinazione))
            destinazione = Path.Combine(dir, Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file)) +
                                             "." + Guid.NewGuid().ToString("N")[..8] + ".json.gz");
        File.Move(file, destinazione, overwrite: false);
    }

    private static TestCase? TryReadRecovery(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var gzip = new GZipStream(fs, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<TestCase>(gzip, JsonIo.Options);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Recupero fork: payload non leggibile", ex);
            return null;
        }
    }

    private static void TryDeleteEmptyDirectory(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir, false); }
        catch (Exception ex) { CrashLogService.Registra("Recupero fork: cartella vuota non rimossa", ex); }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).ToList() : Array.Empty<string>(); }
        catch (Exception ex)
        {
            CrashLogService.Registra("Recupero fork: elenco file non leggibile", ex);
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToList() : Array.Empty<string>(); }
        catch (Exception ex)
        {
            CrashLogService.Registra("Recupero fork: elenco cartelle non leggibile", ex);
            return Array.Empty<string>();
        }
    }

    private static TestCase Clone(TestCase tc) =>
        JsonSerializer.Deserialize<TestCase>(JsonSerializer.Serialize(tc, JsonIo.Options), JsonIo.Options)!;
}

public sealed record ForkRecoveryBranch(TestJournalEvent Event, TestCase TestCase);
public sealed record ForkRecoveryLoadResult(IReadOnlyList<ForkRecoveryBranch> Available, IReadOnlyList<TestJournalEvent> Missing)
{
    public bool Complete => Missing.Count == 0;
}
