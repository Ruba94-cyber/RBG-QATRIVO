using System.Diagnostics;

namespace TestLoom.App.Services;

/// <summary>
/// Richiede a Windows Files On-Demand di conservare IN LOCALE i soli dati operativi TestLoom.
/// Non usa Graph, OneDrive API o servizi remoti; non modifica i payload, i journal o lo schema dati.
/// Gli allegati (anche nel layout legacy testcases/.../allegati/...) restano esclusi.
///
/// ATTENZIONE: attrib +P -U inoltra una richiesta di pinning, NON conferma che OneDrive abbia
/// completato il download o la sincronizzazione verso altri dispositivi.
/// </summary>
public static class LocalDataPinService
{
    public sealed record PinTarget(string Path, bool Recursive);
    public sealed record PinResult(int Requests, IReadOnlyList<string> Errors)
    {
        public bool Accepted => Errors.Count == 0;
    }

    /// <summary>
    /// Solo pattern NON RICORSIVI nelle cartelle che contengono realmente
    /// documenti operativi. Non vengono pinnate cartelle intere: gli allegati
    /// online-only possono comparire nei vecchi layout sotto testcases e cestino.
    /// Il piano non legge i payload e non altera attributi o contenuti.
    /// </summary>
    public static IReadOnlyList<PinTarget> BuildPlan(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Directory.Exists(paths.Root))
            throw new DirectoryNotFoundException("Cartella dati TestLoom non disponibile.");

        var root = Path.GetFullPath(paths.Root);
        var targets = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (TestLoomStorageOwnership.IsManagedJsonPath(root, path) ||
                name.Equals("accesso-config.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("audit-valutazioni-requisito-componente-veicolo.json", StringComparison.OrdinalIgnoreCase))
                targets.Add(path);
        }

        // I pattern di record sono definiti dal layout TestLoom, non dai file presenti
        // nella gerarchia degli allegati. Non pinnare file arbitrari o binari.
        var groups = new (string Directory, string Pattern)[]
        {
            ("testcases", "testcase_*.json"),
            ("nonconformita", "nc_*.json"),
            ("change-requests", "cr_*.json"),
            ("segnalazioni", "segnalazione_*.json"),
            ("cestino", "testcase_*.json"),
            ("notifiche", "notifica_*.json"),
            ("recovery-bin", "recovery_*.json"),
            ("_ditm", "*.json"),
            ("_ditm", "*.ndjson")
        };
        foreach (var (name, pattern) in groups)
        {
            var folder = Path.Combine(root, name);
            if (!Directory.Exists(folder)) continue;

            // Enumeriamo per identificare SOLO le directory effettivamente usate:
            // ciascuna produce una singola richiesta attrib wildcard, non un processo
            // per ogni file. La richiesta wildcard non usa mai /S o /D.
            foreach (var directory in EnumerateSafeDataDirectories(folder))
            {
                foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                {
                    // I layout non autorizzati non vengono inclusi. Per _ditm,
                    // il namespace e' riservato agli artefatti tecnici TestLoom.
                    if (name != "_ditm" &&
                        !TestLoomStorageOwnership.IsManagedJsonPath(root, file)) continue;

                    targets.Add(Path.Combine(directory, pattern));
                }
            }
        }

        return targets.OrderBy(p => p, StringComparer.Ordinal)
            .Select(path => new PinTarget(path, Recursive: false)).ToArray();
    }

    /// <summary>
    /// Visita soltanto sottocartelle operative, ignorando intere gerarchie
    /// Allegati/Attachments/Backup prima dell'enumerazione dei loro file.
    /// Ignora symlink/junction per impedire scansioni fuori dal workspace.
    /// </summary>
    private static IEnumerable<string> EnumerateSafeDataDirectories(string baseDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(baseDirectory);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            yield return dir;
            foreach (var child in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(child);
                if (name.Equals("allegati", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("attachments", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("backup", StringComparison.OrdinalIgnoreCase)) continue;
                if (new DirectoryInfo(child).LinkTarget != null) continue;
                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// Da richiamare solo dopo conferma esplicita dell'utente e fuori dal thread UI.
    /// Non si invoca alcun reset/riavvio forzato del client OneDrive.
    /// </summary>
    public static async Task<PinResult> RequestLocalAvailabilityAsync(
        DataPaths paths, CancellationToken cancellationToken = default)
    {
        var targets = await Task.Run(() => BuildPlan(paths), cancellationToken).ConfigureAwait(false);
        var errors = new List<string>();
        var requests = 0;
        if (!OperatingSystem.IsWindows())
            return new PinResult(0, new[] { "La disponibilita offline tramite attrib e supportata soltanto su Windows." });

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo("attrib.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = false,
                    RedirectStandardOutput = false
                };
                process.StartInfo.ArgumentList.Add("+P");
                process.StartInfo.ArgumentList.Add("-U");
                // Solo un file o una wildcard nella singola cartella.
                // NON applicare /S /D a gerarchie con allegati online-only.
                process.StartInfo.ArgumentList.Add(target.Recursive
                    ? Path.Combine(target.Path, "*")
                    : target.Path);
                if (target.Recursive)
                {
                    process.StartInfo.ArgumentList.Add("/S");
                    process.StartInfo.ArgumentList.Add("/D");
                }
                if (!process.Start()) throw new IOException("attrib.exe non avviato.");
                // Timeout per singola richiesta: il pinning ricorsivo puo richiedere tempo.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("attrib.exe non ha completato entro due minuti.");
                }
                if (process.ExitCode != 0)
                    throw new IOException($"attrib.exe ha restituito il codice {process.ExitCode}.");
                requests++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{Path.GetRelativePath(paths.Root, target.Path)}: {ex.Message}");
                // Continua sui file indipendenti, senza scrivere/sovrascrivere dati TestLoom.
            }
        }
        return new PinResult(requests, errors);
    }
}
