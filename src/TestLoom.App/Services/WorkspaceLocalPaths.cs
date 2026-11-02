using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TestLoom.App.Services;

/// <summary>
/// Calcolo condiviso della cartella locale per-workspace sotto %LOCALAPPDATA%\TestLoom\workspaces\&lt;chiave&gt;.
/// Dalla 6.6.1 la chiave è FISSATA all'apertura per ogni cartella configurata (decisione A del coordinatore): salvata nel
/// config.json locale del PC come alias (lettera e UNC della stessa cartella puntano alla stessa chiave) e riusata per
/// tutta la sessione. Non segue lo stato delle unità: una VPN che cade non cambia mai la cartella locale dei dati. Nessun
/// dato locale viene più spostato tra chiavi.
/// </summary>
public static class WorkspaceLocalPaths
{
    /// <summary>
    /// Override SOLO per la suite di test automatici: impostato una volta sola all'avvio dell'assembly
    /// TestLoom.App.Tests (vedi TestLocalAppDataFixture, [ModuleInitializer]) verso una cartella temporanea
    /// per-esecuzione, cosi' nessun test scrive mai nel vero %LOCALAPPDATA%\TestLoom della macchina dello
    /// sviluppatore. In produzione questo campo resta sempre null.
    /// </summary>
    internal static string? LocalApplicationDataOverrideForTests;

    /// <summary>Chiavi fissate per questa sessione: ortografia della cartella (lettera o UNC) → chiave del workspace locale.</summary>
    private static readonly ConcurrentDictionary<string, string> ChiaviSessione = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Radice equivalente a <see cref="Environment.SpecialFolder.LocalApplicationData"/>, reindirizzabile dai test tramite
    /// <see cref="LocalApplicationDataOverrideForTests"/> e, per i collaudi automatici, dalla radice isolata
    /// <see cref="TestLoomDataRoot"/> (variabile TestLoom_DATA_ROOT). In produzione è sempre %LOCALAPPDATA% reale.
    /// </summary>
    internal static string LocalApplicationDataRoot =>
        LocalApplicationDataOverrideForTests
        ?? TestLoomDataRoot.LocalAppDataRoot;

    /// <summary>
    /// Chiave stabile di 32 caratteri esadecimali per la cartella condivisa <paramref name="sharedRoot"/>. Se la cartella è già
    /// fissata in questa sessione (vedi <see cref="FissaChiaveAllApertura"/>) restituisce sempre quella: la chiave non cambia
    /// più se l'unità mappata smette di rispondere. Per le altre cartelle: SHA-256 del percorso normalizzato.
    /// </summary>
    public static string ComputeWorkspaceKey(string sharedRoot)
    {
        if (string.IsNullOrWhiteSpace(sharedRoot))
            throw new ArgumentException("La cartella condivisa non può essere vuota.", nameof(sharedRoot));

        if (ChiaviSessione.TryGetValue(OrtografiaDiRiferimento(sharedRoot), out var fissata)) return fissata;
        return HashChiave(NormalizeRoot(sharedRoot));
    }

    /// <summary>
    /// Chiave con la formula che gli store locali usavano PRIMA della 6.7.1: SHA-256 del percorso grezzo (in maiuscolo su
    /// Windows, senza risolvere l'unità mappata in UNC). Coincide con <see cref="ComputeWorkspaceKey"/> per le cartelle non su
    /// unità mappata e per quelle fissate con la lettera; diversa solo per una cartella su unità mappata NON fissata.
    /// </summary>
    internal static string ChiaveGrezzaPrecedente(string sharedRoot)
    {
        var full = OrtografiaDiRiferimento(sharedRoot);
        return HashChiave(OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full);
    }

    /// <summary>
    /// Cartella legacy (chiave grezza, vedi <see cref="ChiaveGrezzaPrecedente"/>) sotto <paramref name="baseDir"/>, da LEGGERE per i
    /// dati locali NON ANCORA PUBBLICATI scritti prima della 6.7.1 (transazioni workflow, contatore journal Phase D). Null se la
    /// chiave coincide con quella corrente. Il contenuto NON viene spostato: resta dov'è e i chiamanti lo leggono in sola lettura
    /// (vedi WorkflowTransactionService.RecoverPending e PhaseDMasterJournalService.NextDeviceSequence). Gli indici SQLite locali,
    /// che sono cache ricostruibili, non usano questa cartella: si ricostruiscono sotto la chiave corrente.
    /// </summary>
    internal static string? CartellaLegacyDaLeggere(string baseDir, string sharedRoot)
    {
        var legacy = ChiaveGrezzaPrecedente(sharedRoot);
        return string.Equals(legacy, ComputeWorkspaceKey(sharedRoot), StringComparison.Ordinal)
            ? null
            : Path.Combine(baseDir, legacy);
    }

    /// <summary>
    /// Fissa la chiave del workspace locale per questa cartella configurata, all'apertura. Regole:
    /// 1) se la cartella è già fissata in questa sessione, si riusa quella chiave;
    /// 2) se la configurazione conosce già la cartella (lettera o UNC, alias), si riusa la chiave salvata;
    /// 3) altrimenti: un utente ESISTENTE con dati locali sotto la chiave con la lettera mantiene quella chiave per sempre;
    ///    una NUOVA configurazione usa la chiave canonica (lettera risolta in UNC). Se la lettera non è risolvibile in quel
    ///    momento, la chiave è quella con la lettera, e resta tale.
    /// 4) tutte le ortografie note (lettera e UNC, se risolvibile ora) vengono registrate come alias della stessa chiave.
    /// <paramref name="chiaviSalvate"/> è la tabella del config locale del PC: viene aggiornata e <paramref name="modificata"/>
    /// dice se va salvata. Nessun dato viene spostato.
    /// </summary>
    public static string FissaChiaveAllApertura(string sharedRoot, IDictionary<string, string> chiaviSalvate, out bool modificata)
    {
        modificata = false;
        var ortografia = OrtografiaDiRiferimento(sharedRoot);
        if (ChiaviSessione.TryGetValue(ortografia, out var gia))
        {
            // Già fissata in questa sessione: il config deve comunque contenerne l'alias (un salvataggio precedente può essere fallito).
            if (!chiaviSalvate.TryGetValue(ortografia, out var noto) || noto != gia)
            {
                chiaviSalvate[ortografia] = gia;
                modificata = true;
            }
            return gia;
        }

        var canonico = OperatingSystem.IsWindows()
            ? OrtografiaDiRiferimento(ModalitaCondivisioneService.PercorsoCanonico(ortografia))
            : ortografia;
        var diversa = !string.Equals(canonico, ortografia, StringComparison.OrdinalIgnoreCase);

        string? chiave = null;
        if (chiaviSalvate.TryGetValue(ortografia, out var salvata)) chiave = salvata;
        else if (diversa && chiaviSalvate.TryGetValue(canonico, out var salvataUnc)) chiave = salvataUnc;

        if (chiave == null)
        {
            var grezza = HashChiave(ortografia.ToUpperInvariant());
            var canonica = HashChiave(canonico.ToUpperInvariant());
            // 6.6.1 (correzione 1): la decisione presa in una sessione precedente (ricordo locale per lettera) vale anche se il
            // config ha perso l'alias; in assenza di ricordo vale la regola di apertura.
            // Il ricordo per lettera vale solo per l'UNC con cui è stato fatto: se la lettera ora punta a un altro UNC non se ne riusa
            // la chiave. Con lettera non risolvibile non c'è un UNC attuale da confrontare: il ricordo resta l'unica fonte (correzione 1).
            var ricordo = LeggiRicordoAlias(ortografia);
            var ricordoApplicabile = ricordo != null && (!diversa || string.Equals(ricordo.Unc, canonico, StringComparison.OrdinalIgnoreCase));
            chiave = (ricordoApplicabile ? ricordo!.Chiave : null) ?? (diversa && DatiLocaliEsistono(grezza) ? grezza : canonica);
            modificata = true;
        }
        if (diversa) ScriviRicordoAlias(ortografia, canonico, chiave);

        if (!chiaviSalvate.TryGetValue(ortografia, out var attuale) || attuale != chiave)
        {
            chiaviSalvate[ortografia] = chiave;
            modificata = true;
        }
        if (diversa && (!chiaviSalvate.TryGetValue(canonico, out var attualeUnc) || attualeUnc != chiave))
        {
            chiaviSalvate[canonico] = chiave;
            modificata = true;
        }

        ChiaviSessione[ortografia] = chiave;
        if (diversa) ChiaviSessione[canonico] = chiave;
        return chiave;
    }

    /// <summary>Ricordo locale della decisione di chiave per una lettera di unità (6.6.1, correzione 1): sopravvive alla perdita
    /// dell'alias nel config, quando la lettera non è più risolvibile e il percorso UNC non è noto nella sessione.</summary>
    public sealed class RicordoAliasCartella
    {
        public string Lettera { get; set; } = "";
        public string Unc { get; set; } = "";
        public string Chiave { get; set; } = "";
    }

    private static string PercorsoRicordoAlias(string ortografia) =>
        Path.Combine(LocalApplicationDataRoot, "TestLoom", "alias-cartelle", HashChiave(ortografia.ToUpperInvariant()) + ".json");

    internal static RicordoAliasCartella? LeggiRicordoAlias(string ortografia)
    {
        try { return JsonIo.TryRead<RicordoAliasCartella>(PercorsoRicordoAlias(ortografia), out _); }
        catch (Exception) { return null; }
    }

    /// <summary>Scrive il ricordo solo se cambia: best effort, senza ricordo resta valida la regola di apertura.</summary>
    private static void ScriviRicordoAlias(string ortografia, string canonico, string chiave)
    {
        try
        {
            var attuale = LeggiRicordoAlias(ortografia);
            if (attuale != null && attuale.Chiave == chiave && attuale.Unc == canonico) return;
            var percorso = PercorsoRicordoAlias(ortografia);
            Directory.CreateDirectory(Path.GetDirectoryName(percorso)!);
            JsonIo.WriteAtomic(percorso, new RicordoAliasCartella { Lettera = ortografia, Unc = canonico, Chiave = chiave });
        }
        catch (Exception)
        {
            // Best effort: nessun effetto sull'apertura.
        }
    }

    /// <summary>Solo test: dimentica le chiavi fissate, così ogni test riparte dalla regola di apertura.</summary>
    internal static void ReimpostaChiaviSessione() => ChiaviSessione.Clear();

    /// <summary>Vero se sotto la chiave esistono dati locali di questo PC: indici, journal, outbox, backup, bozze o marker.</summary>
    internal static bool DatiLocaliEsistono(string chiave)
    {
        var testloom = Path.Combine(LocalApplicationDataRoot, "TestLoom");
        var workspace = Path.Combine(testloom, "workspaces", chiave);
        if (Directory.Exists(workspace) && Directory.EnumerateFileSystemEntries(workspace).Any()) return true;
        var bozze = Path.Combine(testloom, "recovery-drafts", chiave);
        if (Directory.Exists(bozze) && Directory.EnumerateFileSystemEntries(bozze).Any()) return true;
        return File.Exists(Path.Combine(testloom, "read-only", chiave + ".lock"));
    }

    /// <summary>
    /// Cartella locale per-workspace: &lt;localBaseDirectory o %LOCALAPPDATA%\TestLoom\workspaces&gt;\&lt;chiave&gt;.
    /// </summary>
    public static string GetWorkspaceDirectory(string sharedRoot, string? localBaseDirectory = null)
    {
        var key = ComputeWorkspaceKey(sharedRoot);
        var baseDir = localBaseDirectory ?? Path.Combine(LocalApplicationDataRoot, "TestLoom", "workspaces");
        return Path.Combine(baseDir, key);
    }

    /// <summary>
    /// Cartella backup ZIP SOLO locale per-workspace (decisione utente 6.4.2).
    /// </summary>
    public static string GetBackupDirectory(string sharedRoot, string? localBaseDirectory = null) =>
        Path.Combine(GetWorkspaceDirectory(sharedRoot, localBaseDirectory), BackupDirectoryName);

    /// <summary>Nome della sottocartella dei backup locali dentro la cartella del workspace.</summary>
    public const string BackupDirectoryName = "backup";

    private static string HashChiave(string normalizzato) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizzato)))[..32];

    /// <summary>Ortografia di riferimento di una cartella: percorso assoluto senza separatori finali (non canonico).</summary>
    private static string OrtografiaDiRiferimento(string sharedRoot) =>
        Path.GetFullPath(sharedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string NormalizeRoot(string root)
    {
        var full = OrtografiaDiRiferimento(root);
        if (!OperatingSystem.IsWindows()) return full;
        // Cartelle non ancora fissate in questa sessione: unità di rete mappata → UNC, tutto il resto invariato.
        var canonico = ModalitaCondivisioneService.PercorsoCanonico(full)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return canonico.ToUpperInvariant();
    }
}
