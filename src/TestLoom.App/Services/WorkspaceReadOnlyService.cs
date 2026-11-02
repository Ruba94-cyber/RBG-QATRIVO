using System.Collections.Concurrent;

namespace TestLoom.App.Services;

/// <summary>
/// Blocco locale e persistente delle scritture condivise per una singola cartella TestLoom.
/// Il marker vive FUORI dalla cartella OneDrive e FUORI dagli indici SQLite ricostruibili,
/// cosi' il cleanup degli indici non lo cancella e non si propaga ad altri PC.
/// Non e' un rilevatore di sincronizzazione cloud: e' un interruttore di sicurezza locale.
/// Dalla 6.6.1 il marker è anche legato all'identità condivisa del workspace (workspace-id.json, decisione A): una seconda
/// ortografia della stessa cartella non può aggirarlo.
/// </summary>
public static class WorkspaceReadOnlyService
{
    /// <summary>Identità condivisa nota per ogni ortografia della cartella, registrata da AppState all'apertura.</summary>
    private static readonly ConcurrentDictionary<string, Guid> IdentitaPerRadice = new(StringComparer.OrdinalIgnoreCase);

    private static string BaseDirectory(string? localBaseDirectory) =>
        localBaseDirectory ?? Path.Combine(WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", "read-only");

    private static string Marker(string sharedRoot, string? localBaseDirectory = null) =>
        Path.Combine(BaseDirectory(localBaseDirectory), WorkspaceLocalPaths.ComputeWorkspaceKey(sharedRoot) + ".lock");

    private static string MarkerPerIdentita(Guid identita, string? localBaseDirectory = null) =>
        Path.Combine(BaseDirectory(localBaseDirectory), "id-" + identita.ToString("N") + ".lock");

    /// <summary>Registra l'identità condivisa del workspace aperto: da quel momento il marker vale anche per id.</summary>
    internal static void RegistraIdentita(string sharedRoot, Guid identita) =>
        IdentitaPerRadice[Path.GetFullPath(sharedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)] = identita;

    /// <summary>Una verifica rapida read-only; nessun file viene creato durante la lettura.</summary>
    public static bool IsEnabled(string sharedRoot, string? localBaseDirectory = null)
    {
        if (File.Exists(Marker(sharedRoot, localBaseDirectory))) return true;
        return IdentitaPerRadice.TryGetValue(Path.GetFullPath(sharedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), out var identita)
               && File.Exists(MarkerPerIdentita(identita, localBaseDirectory));
    }

    /// <summary>
    /// Cambia stato SOLO su questa postazione e workspace. In caso di errore durante
    /// l'abilitazione non altera i dati condivisi. Non rimuove bozze o backup.
    /// </summary>
    public static void SetEnabled(string sharedRoot, bool enabled, string? localBaseDirectory = null)
    {
        var percorsi = new List<string> { Marker(sharedRoot, localBaseDirectory) };
        var chiaveRadice = Path.GetFullPath(sharedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (IdentitaPerRadice.TryGetValue(chiaveRadice, out var identita))
            percorsi.Add(MarkerPerIdentita(identita, localBaseDirectory));

        foreach (var path in percorsi)
        {
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                JsonIo.WriteAtomic(path, new ReadOnlyMarker
                {
                    AttivatoIlUtc = DateTime.UtcNow,
                    Motivo = "Sola lettura locale richiesta dall'utente"
                });
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class ReadOnlyMarker
    {
        public DateTime AttivatoIlUtc { get; set; }
        public string Motivo { get; set; } = "";
    }
}
