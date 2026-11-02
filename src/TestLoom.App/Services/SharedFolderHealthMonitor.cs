using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Stato della cartella condivisa stimato SOLO da segnali locali e non mutanti. Non è una prova di sincronizzazione
/// cloud: OneDrive/SharePoint non espongono un segnale affidabile. Il monitor non blocca mai le scritture da solo:
/// il gate esistente (<see cref="WorkspaceWritePolicy"/>) resta l'unico punto che blocca, e <see cref="Incerta"/>
/// produce soltanto un avviso non bloccante.
/// </summary>
public enum SharedFolderHealthState
{
    /// <summary>Nessun segnale di incertezza nel campione esaminato.</summary>
    Disponibile,

    /// <summary>
    /// Radice assente, non configurata, solo offline o inaccessibile. Le scritture sono già bloccate dal gate
    /// esistente. Con una prova di scrittura esplicita, anche una scrittura fallita rientra qui.
    /// </summary>
    NonRaggiungibile,

    /// <summary>
    /// Raggiungibile ma non verificabile con certezza: file chiave mancanti o solo online, copie di conflitto recenti,
    /// JSON operativi solo online nel campione, radice lenta. Avviso non bloccante.
    /// </summary>
    Incerta
}

/// <summary>Esito della valutazione: stato, motivi leggibili in italiano e numero di JSON effettivamente esaminati.</summary>
public sealed record SharedFolderHealthReport(
    SharedFolderHealthState Stato,
    IReadOnlyList<string> Motivi,
    int FileEsaminati,
    DateTime VerificatoIl);

public sealed record SharedFolderHealthOptions
{
    /// <summary>Tetto ai JSON esaminati: il costo resta limitato, nessuna scansione completa della cartella.</summary>
    public int MaxFileCampionati { get; init; } = SharedFolderHealthMonitor.DefaultMaxFileCampionati;

    /// <summary>Una copia di conflitto conta come recente solo se modificata entro questa finestra.</summary>
    public TimeSpan FinestraCopieFresche { get; init; } = TimeSpan.FromHours(72);

    /// <summary>
    /// Prova di scrittura reale: crea e cancella <c>.testloom-write-probe</c>. Default false: il giro periodico non deve
    /// mutare la cartella (vedi CondivisioneHealthServiceTests). Usarla solo allo startup o al recupero da stato degradato.
    /// </summary>
    public bool EseguiProbeScrittura { get; init; }
}

/// <summary>
/// Valutazione non mutante della cartella condivisa per l'avviso di barra. Segnali: radice raggiungibile (con timeout);
/// file chiave presenti, non solo online e leggibili; copie di conflitto esplicite e recenti nel campione; JSON operativi
/// segnati solo online nel campione. Il campione è limitato in file e in directory visitate: nessuna scansione completa.
/// </summary>
public static class SharedFolderHealthMonitor
{
    public const int DefaultMaxFileCampionati = 200;

    private const int MaxDirectoriesVisitate = 64;

    public static SharedFolderHealthReport Valuta(
        DataPaths paths,
        SharedFolderHealthOptions? opzioni = null,
        DateTime? adesso = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var o = opzioni ?? new SharedFolderHealthOptions();
        var now = adesso ?? DateTime.Now;

        var radice = EsaminaRadiceConTimeout(paths.Root);
        if (radice.Esito == EsitoRadice.NonRaggiungibile)
            return new(SharedFolderHealthState.NonRaggiungibile, [radice.Motivo], 0, now);
        // Radice lenta: nessun'altra I/O sul percorso di rete e nessuna conclusione forte.
        if (radice.Esito == EsitoRadice.Lenta)
            return new(SharedFolderHealthState.Incerta, [radice.Motivo], 0, now);

        var motivi = new List<string>();
        foreach (var (etichetta, percorso) in FileChiave(paths))
        {
            var problema = VerificaFileChiave(etichetta, percorso);
            if (problema != null) motivi.Add(problema);
        }

        var campione = CampionaJson(paths, Math.Max(1, o.MaxFileCampionati));
        var offline = 0;
        var copieRecenti = new List<string>();
        foreach (var file in campione)
        {
            if ((file.Attributes & FileAttributes.Offline) != 0) offline++;
            if (EsCopiaDiConflittoRecente(file, now, o.FinestraCopieFresche)) copieRecenti.Add(file.Name);
        }
        if (copieRecenti.Count > 0)
            motivi.Add($"Copie di conflitto OneDrive/SharePoint recenti nel campione ({copieRecenti.Count}): {string.Join(", ", copieRecenti.Take(3))}.");
        if (offline > 0)
            motivi.Add($"{offline} file JSON operativi segnati solo online nel campione di {campione.Count} esaminati.");

        // La prova mutante gira solo se richiesta esplicitamente e se la modalità visualizzatore non è già attiva.
        if (o.EseguiProbeScrittura && !WorkspaceReadOnlyService.IsEnabled(paths.Root))
        {
            var prova = CondivisioneHealthService.Verifica(paths.Root);
            if (prova.Stato != StatoCondivisione.Operativa)
                return new(SharedFolderHealthState.NonRaggiungibile,
                    [.. motivi, $"Prova di scrittura non riuscita: {prova.Messaggio}"], campione.Count, now);
        }

        var stato = motivi.Count == 0 ? SharedFolderHealthState.Disponibile : SharedFolderHealthState.Incerta;
        return new(stato, motivi, campione.Count, now);
    }

    private static IEnumerable<(string Etichetta, string Percorso)> FileChiave(DataPaths paths)
    {
        yield return ("version-manifest.json", paths.VersionManifestFile);
        yield return ("workspace-id.json", WorkspaceIdentityService.SharedIdFile(paths));
        // Anagrafica operatori: dopo il cutover Phase D la fonte autorevole è il journal master-v2 con ready.json, e
        // operatori.json non viene più riscritto. Il monolite resta la chiave solo nei workspace legacy (senza manifest v2).
        if (File.Exists(PhaseDMasterJournalService.ManifestPath(paths.Root)))
            yield return ("ready.json master Phase D", PhaseDMasterBootstrapService.ReadyPath(paths.Root));
        else
            yield return ("operatori.json", paths.OperatoriFile);
    }

    private static string? VerificaFileChiave(string etichetta, string percorso)
    {
        try
        {
            if (!File.Exists(percorso)) return $"File chiave assente: {etichetta}.";
            // Attributi controllati PRIMA dell'apertura: aprire un file solo online ne forzerebbe il download.
            if ((File.GetAttributes(percorso) & FileAttributes.Offline) != 0)
                return $"File chiave segnato solo online: {etichetta}.";
            // Apertura in sola lettura senza leggere né scrivere il contenuto: verifica permessi e lock altrui.
            using var _ = new FileStream(percorso, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"File chiave non leggibile: {etichetta}.";
        }
    }

    private enum EsitoRadice { Ok, Lenta, NonRaggiungibile }

    private sealed record EsameRadice(EsitoRadice Esito, string Motivo);

    private static EsameRadice EsaminaRadiceConTimeout(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return new(EsitoRadice.NonRaggiungibile, "Cartella condivisa non configurata.");

        // Con timeout: una cartella di rete in stallo non deve trattenere il chiamante oltre il limite. Il limite dipende
        // dalla modalità (6.6.1): più ampio per la cartella di rete, così la latenza di una VPN non diventa "Incerta".
        var timeout = TimeoutCondivisione.Per(root);
        return IoTimeoutGuard.Esegui(
                   () => EsaminaRadice(root),
                   timeout,
                   new EsameRadice(EsitoRadice.Lenta, $"Cartella condivisa non risponde entro {timeout.TotalSeconds:0} secondi."))
               ?? new EsameRadice(EsitoRadice.Lenta, "Cartella condivisa non verificabile entro il tempo limite.");
    }

    private static EsameRadice EsaminaRadice(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return new(EsitoRadice.NonRaggiungibile, "Cartella condivisa non trovata.");
            if ((File.GetAttributes(root) & FileAttributes.Offline) != 0)
                return new(EsitoRadice.NonRaggiungibile, "Cartella condivisa disponibile solo offline: contenuto non locale.");
            return new(EsitoRadice.Ok, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(EsitoRadice.NonRaggiungibile, "Cartella condivisa non accessibile.");
        }
    }

    /// <summary>
    /// Campione BFS limitato: al massimo <paramref name="maxFile"/> JSON e <see cref="MaxDirectoriesVisitate"/> directory.
    /// La enumerazione riusa attributi e data già letti dal file system, quindi non genera una chiamata di I/O per file.
    /// Prima le cartelle dei record (dati operativi), poi la radice: i monoliti legacy alla radice non sono più il dato
    /// vivo dopo Phase D, e i file chiave della radice vengono comunque verificati a parte. I Test sono ricorsivi (per componente).
    /// </summary>
    private static List<FileInfo> CampionaJson(DataPaths paths, int maxFile)
    {
        var risultati = new List<FileInfo>();
        var coda = new Queue<(string Directory, bool Ricorsiva)>();
        coda.Enqueue((paths.TestCasesDir, true));
        coda.Enqueue((paths.NonConformitaDir, false));
        coda.Enqueue((paths.ChangeRequestDir, false));
        coda.Enqueue((paths.SegnalazioniDir, false));
        coda.Enqueue((paths.Root, false));

        var directoryViste = 0;
        while (coda.Count > 0 && directoryViste < MaxDirectoriesVisitate && risultati.Count < maxFile)
        {
            var (dir, ricorsiva) = coda.Dequeue();
            directoryViste++;
            try
            {
                var di = new DirectoryInfo(dir);
                if (!di.Exists) continue;
                foreach (var file in di.EnumerateFiles("*.json", SearchOption.TopDirectoryOnly))
                {
                    risultati.Add(file);
                    if (risultati.Count >= maxFile) break;
                }
                if (!ricorsiva || risultati.Count >= maxFile) continue;
                foreach (var sottoDir in di.EnumerateDirectories())
                {
                    if (directoryViste + coda.Count >= MaxDirectoriesVisitate) break;
                    coda.Enqueue((sottoDir.FullName, true));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Campione best-effort: una directory illeggibile non fa fallire la valutazione.
            }
        }
        return risultati;
    }

    /// <summary>
    /// Copia di conflitto esplicita (" (Laptop)", " -...", "-PC2", "_LAPTOP-...") il cui canonico <c>&lt;stem&gt;.json</c>
    /// esiste accanto, modificata entro la finestra. Senza canonico non si segnala nulla: nessun falso allarme.
    /// </summary>
    private static bool EsCopiaDiConflittoRecente(FileInfo file, DateTime now, TimeSpan finestra)
    {
        if (now - file.LastWriteTime > finestra) return false;
        var dir = file.DirectoryName;
        if (dir == null) return false;

        var nome = Path.GetFileNameWithoutExtension(file.Name);
        // Il suffisso esplicito inizia sempre con spazio, '-' o '_': gli altri punti di separazione non si testano.
        for (var i = 1; i < nome.Length; i++)
        {
            if (nome[i] is not (' ' or '-' or '_')) continue;
            if (!VisibleConflictWriteGuard.EsSuffissoConflittoEsplicito(nome[i..])) continue;
            if (File.Exists(Path.Combine(dir, nome[..i] + ".json"))) return true;
        }
        return false;
    }
}
