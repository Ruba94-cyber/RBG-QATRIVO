namespace TestLoom.App.Services;

/// <summary>Prova a marcare gli allegati come "solo online" per liberare spazio locale su OneDrive/SharePoint
/// Files On-Demand (Addendum: grill-me 2026-08-18, quarta follow-up — "creando uno script base all'interno
/// dell'app per gestire la non sincronizzazione di quella cartella se possibile"): usa l'attributo Cloud
/// Files nativo di Windows tramite attrib.exe (+U = non pinnato/libera spazio, -P = non forzato sempre
/// presente), lo stesso meccanismo su cui si appoggia OneDrive — non un'API specifica di OneDrive, quindi
/// funziona a prescindere da quale client di sincronizzazione gestisce la cartella condivisa (OneDrive o
/// SharePoint sincronizzato con lo stesso motore).
///
/// Deliberatamente best-effort: se OneDrive/Files On-Demand non è attivo su un PC, se attrib.exe non è
/// disponibile, o se il tentativo fallisce per qualunque motivo, l'allegato resta comunque salvato
/// correttamente — questo servizio non deve MAI far fallire un salvataggio, solo provare a "liberare spazio"
/// quando possibile. Su una cartella di rete (6.6.1) non esistono Files On-Demand: il servizio non esegue nulla.
/// Il processo è un wrapper sottile: il tetto di tempo è verificato dal test <c>EseguiProcesso</c>.</summary>
public static class AllegatiSyncService
{
    private const int TimeoutFileMs = 3000;
    private const int TimeoutCartellaMs = 30000;

    /// <summary>Esito di un tentativo di esecuzione (usato dai test per verificare la terminazione al timeout).</summary>
    internal enum EsitoEsecuzione { NonAvviato, Completato, Terminato, Scaduto }

    /// <summary>Vero se gli attributi Cloud Files hanno senso per questo percorso. Nella cartella di rete (6.6.1, b) non
    /// esiste Files On-Demand: nessun attrib.exe viene avviato e i pulsanti corrispondenti sono nascosti.</summary>
    public static bool DeveApplicareAttributiCloudFiles(string? percorso) => !ModalitaCondivisioneService.ERete(percorso);

    public static void ProvaSegnaSoloOnline(string percorsoFile)
    {
        if (!DeveApplicareAttributiCloudFiles(percorsoFile)) return;
        Esegui($"+U -P \"{percorsoFile}\"", TimeoutFileMs, percorsoFile);
    }

    /// <summary>Applica ricorsivamente a un'intera cartella (/S = sottocartelle, /D = includi anche le
    /// cartelle stesse, non solo i file): usato dopo la migrazione, quando molti file arrivano dalla vecchia
    /// posizione senza essere passati per ProvaSegnaSoloOnline al momento del salvataggio.</summary>
    public static void ProvaSegnaCartellaSoloOnline(string cartella)
    {
        if (!Directory.Exists(cartella) || !DeveApplicareAttributiCloudFiles(cartella)) return;
        Esegui($"+U -P \"{cartella}\" /S /D", TimeoutCartellaMs, cartella);
    }

    /// <summary>
    /// Avvio di attrib.exe. Il processo viene terminato al timeout SOLO in modalità di rete (6.6.1, decisione B). Per OneDrive
    /// e cartelle locali resta il comportamento storico: si attende il tempo indicato e si prosegue senza terminarlo.
    /// </summary>
    private static void Esegui(string argomenti, int timeoutMs, string percorso) =>
        EseguiProcesso("attrib.exe", argomenti, timeoutMs, terminaAlTimeout: ModalitaCondivisioneService.ERete(percorso));

    /// <summary>
    /// Avvia un processo con un tetto di tempo. Se non termina entro <paramref name="timeoutMs"/> viene terminato insieme
    /// ai suoi figli: prima il processo restava in vita dopo il timeout (WaitForExit senza Kill), con attrib.exe bloccato
    /// su una cartella di rete o di OneDrive in stallo.
    /// </summary>
    internal static EsitoEsecuzione EseguiProcesso(string eseguibile, string argomenti, int timeoutMs, bool terminaAlTimeout = true)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(eseguibile, argomenti)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            using var processo = System.Diagnostics.Process.Start(psi);
            if (processo == null) return EsitoEsecuzione.NonAvviato;
            if (processo.WaitForExit(timeoutMs)) return EsitoEsecuzione.Completato;
            // Comportamento OneDrive invariato (decisione B): nessuna terminazione, il processo resta come prima.
            if (!terminaAlTimeout) return EsitoEsecuzione.Scaduto;
            try
            {
                processo.Kill(entireProcessTree: true);
                processo.WaitForExit(2000);
            }
            catch (Exception)
            {
                // Già terminato nel frattempo: nessun effetto da propagare.
            }
            return EsitoEsecuzione.Terminato;
        }
        catch
        {
            // Best-effort per design (vedi doc della classe): OneDrive potrebbe non essere installato su
            // questo PC, o attrib.exe potrebbe non supportare i flag Cloud Files su questa versione di
            // Windows — mai propagare l'errore, l'allegato è comunque salvato correttamente.
            return EsitoEsecuzione.NonAvviato;
        }
    }
}
