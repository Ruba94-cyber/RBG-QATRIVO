using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestLoom.App.Services;

/// <summary>
/// Identità di istanza della cartella dati condivisa (piano 6.4.2/6.4.2-indice-stantio, difetto "indice
/// stantio"): un Guid stabile in <c>_ditm/workspace-id.json</c>, creato una sola volta e mai più rigenerato,
/// serve a distinguere "la stessa cartella dati non cambiata" da "una cartella dati DIVERSA che per caso vive
/// allo stesso percorso" — scenario reale riprodotto con lo strumento tools/data-fix-6.4.2: la stessa cartella
/// di output viene cancellata e ricreata con contenuto diverso, ma <see cref="WorkspaceLocalPaths"/> deriva la
/// cartella dell'indice locale SOLO dal percorso, quindi senza questo controllo l'app riapre l'indice/journal
/// locale del giro precedente credendolo ancora valido (0 Test invece di quelli realmente presenti su disco).
///
/// Il secondo pezzo è un marcatore LOCALE (<c>workspace-id.local.json</c>, accanto agli indici SQLite sotto
/// %LOCALAPPDATA%\TestLoom\workspaces\&lt;hash-percorso&gt;) che registra per quale id era stato costruito l'indice
/// locale corrente. Se l'id della cartella condivisa non coincide col marcatore locale (o uno dei due manca
/// mentre l'altro è presente) la cartella dati è cambiata sotto lo stesso percorso: gli indici locali (Test,
/// Non Conformità, Master, Entità/CR — vivono tutti nella stessa cartella per-workspace, vedi
/// <see cref="WorkspaceLocalPaths.GetWorkspaceDirectory"/>) vanno ricostruiti INTERAMENTE da disco, mai usati
/// così come sono.
/// </summary>
public static class WorkspaceIdentityService
{
    private const string SharedFileName = "workspace-id.json";
    private const string LocalMarkerFileName = "workspace-id.local.json";

    private sealed class SharedIdPayload
    {
        [JsonPropertyName("WorkspaceId")]
        public Guid WorkspaceId { get; set; }
    }

    /// <summary>
    /// Legge <c>_ditm/workspace-id.json</c> dalla cartella condivisa. Se assente e <paramref name="allowWrite"/>
    /// è vero (workspace in modalità Operativa, scrittura condivisa consentita — mai in Sola lettura), lo crea
    /// in modo atomico e race-safe fra più PC: scrive un file temporaneo univoco nella stessa cartella e lo
    /// rinomina sul nome finale. Se nel frattempo un altro PC ha già creato il file (rename fallito perché la
    /// destinazione esiste già), il Guid generato qui viene scartato e si rilegge quello scritto dall'altro PC
    /// — mai due id permanenti diversi per la stessa cartella dati.
    /// </summary>
    /// <summary>Legge l'id condiviso senza mai crearlo (null se assente o illeggibile).</summary>
    public static Guid? TryReadSharedId(DataPaths paths) => TryReadGuid(SharedIdFile(paths));

    public static Guid? EnsureOrReadSharedId(DataPaths paths, bool allowWrite)
    {
        var file = SharedIdFile(paths);
        var existing = TryReadGuid(file);
        if (existing.HasValue) return existing;
        if (!allowWrite) return null;

        try { Directory.CreateDirectory(Path.GetDirectoryName(file)!); }
        catch (Exception ex) { CrashLogService.Registra("Identita' workspace: creazione cartella _ditm", ex); return null; }

        var candidate = Guid.NewGuid();
        var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Scrittura con WriteThrough + flush: il temporaneo è completo su disco prima del rename. Il finally sotto lo elimina
            // in ogni caso (anche se la scrittura stessa fallisce a metà), così non restano residui nella cartella condivisa.
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(new System.Text.UTF8Encoding(false).GetBytes(Serialize(candidate)));
                stream.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(tmp, file);
                return candidate;
            }
            catch (IOException)
            {
                // Rename fallito perché nel frattempo un altro PC ha creato lo stesso file (race multi-PC su
                // cartella condivisa OneDrive/SharePoint): il NOSTRO Guid va scartato, si rilegge quello
                // vincitore. Nessun secondo tentativo di scrittura: mai due id permanenti per la stessa cartella.
                return TryReadGuid(file);
            }
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Identita' workspace: creazione id condiviso", ex);
            // Un altro PC potrebbe aver comunque vinto la race mentre questo tentativo falliva a metà.
            return TryReadGuid(file);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { /* file temporaneo, non bloccante */ }
        }
    }

    /// <summary>Legge il marcatore locale accanto agli indici SQLite di questo workspace (stessa cartella per
    /// Test/NC/Master/Entità, vedi <see cref="WorkspaceLocalPaths.GetWorkspaceDirectory"/>). Null se assente o
    /// illeggibile: un marcatore mancante NON è un errore, è lo stato normale al primo avvio con questa
    /// funzionalità o su un workspace mai aperto da questo PC.</summary>
    public static Guid? TryReadLocalMarker(string workspaceLocalDirectory) =>
        TryReadGuid(Path.Combine(workspaceLocalDirectory, LocalMarkerFileName));

    /// <summary>Scrive/aggiorna il marcatore locale con l'id condiviso corrente. Va richiamato SOLO dopo che
    /// gli indici locali sono stati ricostruiti/riconciliati per questo id (o, se l'id non è cambiato, ad ogni
    /// bootstrap — è una scrittura di pochi byte, nessun costo significativo). Scrittura diretta (non
    /// temp+rename): è uno stato locale di UN SOLO PC, mai conteso fra processi diversi come il file condiviso.</summary>
    public static void WriteLocalMarker(string workspaceLocalDirectory, Guid sharedId)
    {
        Directory.CreateDirectory(workspaceLocalDirectory);
        File.WriteAllText(Path.Combine(workspaceLocalDirectory, LocalMarkerFileName), Serialize(sharedId));
    }

    /// <summary>
    /// Vero quando l'indice locale (Test/NC/Master/Entità, tutti nella stessa cartella per-workspace) va
    /// ricostruito INTERAMENTE da disco perché la cartella dati condivisa non è più quella per cui l'indice
    /// locale era stato costruito. <paramref name="sharedId"/> null (impossibile determinarlo: Sola lettura su
    /// una cartella senza id ancora) NON forza mai una ricostruzione — non c'è modo di sapere se la cartella è
    /// cambiata, e forzare qui peggiorerebbe soltanto le prestazioni di un caso già degradato senza guadagno di
    /// correttezza (nessun nuovo id è stato pubblicato per nessuno). Ogni altra combinazione (id diversi, o uno
    /// dei due presente e l'altro assente) è un segnale di cambiamento e forza la ricostruzione.
    /// </summary>
    public static bool NeedsFullRebuild(Guid? sharedId, Guid? localMarkerId)
    {
        if (!sharedId.HasValue) return false;
        return localMarkerId != sharedId;
    }

    /// <summary>Prefisso delle cartelle di quarantena create quando la cartella condivisa viene sostituita.</summary>
    public const string QuarantenaIdentitaPrefix = "quarantena-identita-";

    /// <summary>
    /// Gestisce la cartella locale di un workspace la cui cartella condivisa è stata sostituita, PRIMA che qualunque
    /// store locale la apra. Gli indici SQLite (<c>*.db</c>, <c>*.db-wal</c>, <c>*.db-shm</c>: Test/NC/Master/Entità/journal)
    /// appartengono alla cartella precedente e vengono eliminati: sono tutti ricostruibili dai file autorevoli.
    /// Tutto il resto (in particolare <c>record-journal</c>, con outbox e payload delle modifiche NC/CR/Segnalazioni
    /// non ancora pubblicate) NON viene mai cancellato: viene spostato in una cartella di quarantena datata
    /// (<c>quarantena-identita-yyyyMMdd-HHmmss</c>) dentro la stessa cartella locale, e i nomi spostati finiscono nella
    /// diagnostica con il percorso di quarantena, così restano recuperabili. I backup locali (<c>backup</c>) restano intatti.
    /// Mai bloccante: un elemento che non si può né eliminare né spostare viene registrato e lasciato dov'è.
    /// Il marcatore locale viene riscritto dal chiamante solo dopo un bootstrap riuscito.
    /// </summary>
    public static void SvuotaIndiciLocali(string workspaceLocalDirectory)
    {
        if (!Directory.Exists(workspaceLocalDirectory)) return;
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        catch (Exception ex) { CrashLogService.Registra("Identita' workspace: chiusura connessioni locali", ex); }

        string? quarantena = null;
        var indiciEliminati = 0;
        var nomiIsolati = new List<string>();
        foreach (var voce in Directory.EnumerateFileSystemEntries(workspaceLocalDirectory).ToList())
        {
            var nome = Path.GetFileName(voce);
            if (nome.Equals(WorkspaceLocalPaths.BackupDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                nome.StartsWith(QuarantenaIdentitaPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (!Directory.Exists(voce) && EIndiceSqlite(nome))
                {
                    File.Delete(voce);
                    indiciEliminati++;
                    continue;
                }
                quarantena ??= CreaCartellaQuarantena(workspaceLocalDirectory);
                var destinazione = Path.Combine(quarantena, nome);
                if (Directory.Exists(voce)) Directory.Move(voce, destinazione);
                else File.Move(voce, destinazione);
                nomiIsolati.Add(nome);
            }
            catch (Exception ex)
            {
                // Mai cancellare contenuto che non sia un indice: se l'isolamento fallisce, l'elemento resta dov'è.
                CrashLogService.Registra($"Identita' workspace: isolamento di {nome} non riuscito", ex);
            }
        }

        if (nomiIsolati.Count == 0)
            DiagnosticTraceService.Registra("Workspace.CartellaSostituita",
                $"Indici locali eliminati: {indiciEliminati}. Ricostruzione completa da disco.");
        else
            DiagnosticTraceService.Registra("Workspace.CartellaSostituita",
                $"Indici locali eliminati: {indiciEliminati}. Contenuto non indice conservato in quarantena: " +
                $"{string.Join(", ", nomiIsolati)} in {quarantena}. Ricostruzione completa da disco.");
    }

    /// <summary>File SQLite locale (indice o sidecar WAL/SHM): appartiene alla cartella condivisa sostituita.</summary>
    private static bool EIndiceSqlite(string nome) =>
        nome.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
        nome.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) ||
        nome.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase);

    /// <summary>Crea una cartella di quarantena datata, senza sovrascrivere una precedente creata nello stesso secondo.</summary>
    private static string CreaCartellaQuarantena(string workspaceLocalDirectory)
    {
        var radice = QuarantenaIdentitaPrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var candidata = Path.Combine(workspaceLocalDirectory, radice);
        for (var i = 2; Directory.Exists(candidata); i++)
            candidata = Path.Combine(workspaceLocalDirectory, $"{radice}-{i}");
        Directory.CreateDirectory(candidata);
        return candidata;
    }

    internal static string SharedIdFile(DataPaths paths) => Path.Combine(paths.Root, "_ditm", SharedFileName);

    private static Guid? TryReadGuid(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var payload = JsonSerializer.Deserialize<SharedIdPayload>(File.ReadAllText(file), JsonIo.Options);
            return payload != null && payload.WorkspaceId != Guid.Empty ? payload.WorkspaceId : null;
        }
        catch { return null; }
    }

    private static string Serialize(Guid id) =>
        JsonSerializer.Serialize(new SharedIdPayload { WorkspaceId = id }, JsonIo.Options);
}
