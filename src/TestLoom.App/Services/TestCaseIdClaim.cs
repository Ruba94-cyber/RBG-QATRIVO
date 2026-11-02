using System.Text.Json;

namespace TestLoom.App.Services;

/// <summary>
/// Prenotazione esclusiva dell'ID di un NUOVO Test sulla cartella condivisa (P2.8). Due postazioni con indice locale non
/// aggiornato possono generare lo stesso progressivo (es. T-AB-0001) e scrivere due Test diversi con lo stesso codice: il
/// file di claim, creato con <see cref="FileMode.CreateNew"/> prima della prima scrittura, è esclusivo. Chi trova il claim
/// già presente riceve null e rigenera l'ID.
///
/// Layout a blocchi: <c>_ditm/id-claims/testcase/&lt;blocco&gt;/&lt;ID&gt;.claim</c>, dove il blocco è il progressivo diviso
/// per <see cref="DimensioneShard"/>: nessuna cartella supera quel numero di file anche con 100k Test. Il blocco dipende
/// solo dall'ID, così due postazioni calcolano sempre lo stesso percorso per lo stesso ID.
///
/// Ciclo di vita: il claim resta se il Test è stato scritto (<see cref="Conferma"/>); altrimenti (errore prima della
/// scrittura, lock di commit scaduto, conflitto) viene rimosso in <see cref="Dispose"/>. Un claim senza Test (salvataggio
/// interrotto) non viene mai rimosso dalla pulizia: blocca solo quell'ID. La pulizia (<see cref="PulisciScaduti"/>) rimuove
/// un claim solo se è più vecchio di <see cref="EtaMinimaPulizia"/> E il Test con quell'ID esiste già nei dati condivisi:
/// a quel punto ogni PC è riallineato e vede il Test. Assunzione: ogni PC si riallinea ai dati condivisi almeno ogni pochi
/// giorni (refresh all'avvio e riconciliazione periodica); un PC fermo più a lungo va riallineato prima di creare Test.
///
/// LIMITE: su OneDrive/SharePoint un claim creato da un PC diventa visibile agli altri solo dopo la sincronizzazione; la
/// garanzia piena vale su una cartella di rete (SMB) o sulla stessa macchina.
/// </summary>
public sealed class TestCaseIdClaim : IDisposable
{
    /// <summary>Progressivi (quindi claim) per cartella di blocco: al massimo 500 file per cartella.</summary>
    public const int DimensioneShard = 500;

    /// <summary>Età minima di un claim prima che la pulizia possa rimuoverlo, purché il Test con quell'ID esista.</summary>
    public static readonly TimeSpan EtaMinimaPulizia = TimeSpan.FromDays(7);

    /// <summary>Intervallo minimo tra due pulizie opportunistiche dello stesso PC.</summary>
    public static readonly TimeSpan IntervalloPulizia = TimeSpan.FromDays(1);

    private const string BloccoAltri = "_altri";
    private const string EstensioneClaim = ".claim";
    private const string NomeMarcatorePulizia = "pulizia-claim-id.marcatore";

    private readonly string _percorso;
    private bool _confermato;
    private bool _rilasciato;

    private TestCaseIdClaim(string percorso) => _percorso = percorso;

    /// <summary>Cartella radice dei claim degli ID Test (area tecnica di TestLoom nella cartella condivisa).</summary>
    public static string CartellaClaimTest(DataPaths paths) => Path.Combine(paths.Root, "_ditm", "id-claims", "testcase");

    /// <summary>Blocco di un ID: il progressivo diviso per <see cref="DimensioneShard"/> su 4 cifre, oppure "_altri" per un ID
    /// che non ha il formato T-XX-NNNN.</summary>
    public static string BloccoPer(string id) =>
        IdGenerator.TryProgressivo(id, out var progressivo) ? (progressivo / DimensioneShard).ToString("D4") : BloccoAltri;

    /// <summary>Percorso del claim di un ID, blocco incluso.</summary>
    public static string PercorsoClaim(DataPaths paths, string id) =>
        Path.Combine(CartellaClaimTest(paths), BloccoPer(id), NomeFile(id));

    /// <summary>Prenota <paramref name="id"/> in modo esclusivo. Restituisce null se l'ID è già prenotato, da questa o da
    /// un'altra postazione. Un errore diverso dalla collisione viene propagato.</summary>
    public static TestCaseIdClaim? TryPrenota(DataPaths paths, string id, Guid uid)
    {
        var percorso = PercorsoClaim(paths, id);
        Directory.CreateDirectory(Path.GetDirectoryName(percorso)!);

        FileStream flusso;
        try
        {
            flusso = new FileStream(percorso, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(percorso))
        {
            return null;
        }

        try
        {
            using (flusso)
                JsonSerializer.Serialize(flusso, new { id, uid, creatoIl = DateTime.UtcNow }, JsonIo.CompactOptions);
        }
        catch
        {
            try { File.Delete(percorso); } catch { }
            throw;
        }
        return new TestCaseIdClaim(percorso);
    }

    /// <summary>Il Test è stato scritto: l'ID resta prenotato in modo permanente.</summary>
    public void Conferma() => _confermato = true;

    /// <summary>Se il Test non è stato scritto, rilascia l'ID rimuovendo il claim.</summary>
    public void Dispose()
    {
        if (_confermato || _rilasciato) return;
        _rilasciato = true;
        try { if (File.Exists(_percorso)) File.Delete(_percorso); }
        catch (Exception ex) { CrashLogService.Registra("Rilascio prenotazione ID Test", ex); }
    }

    /// <summary>Rimuove i claim idonei alla pulizia: più vecchi di <see cref="EtaMinimaPulizia"/> (data dell'ultima scrittura,
    /// che non cambia dopo la creazione) e con un Test già esistente per quell'ID. Restituisce il numero di claim rimossi.
    /// Best-effort per file: un errore su un file non ferma la pulizia, e il file viene riprovato alla pulizia successiva.</summary>
    public static int PulisciScaduti(DataPaths paths, Func<string, bool> testEsiste, DateTime adessoUtc)
    {
        var radice = new DirectoryInfo(CartellaClaimTest(paths));
        if (!radice.Exists) return 0;

        var soglia = adessoUtc - EtaMinimaPulizia;
        var rimossi = 0;
        foreach (var blocco in radice.EnumerateDirectories())
        {
            foreach (var claim in EnumeraClaim(blocco))
            {
                try
                {
                    if (claim.LastWriteTimeUtc > soglia) continue;
                    if (!testEsiste(Path.GetFileNameWithoutExtension(claim.Name))) continue;
                    claim.Delete();
                    rimossi++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return rimossi;
    }

    private static List<FileInfo> EnumeraClaim(DirectoryInfo blocco)
    {
        try { return blocco.EnumerateFiles("*" + EstensioneClaim).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new List<FileInfo>(); }
    }

    /// <summary>Vero se questo PC non ha ancora eseguito una pulizia nelle ultime <see cref="IntervalloPulizia"/>.</summary>
    public static bool PuliziaDovuta(DateTime? ultimaPuliziaUtc, DateTime adessoUtc) =>
        ultimaPuliziaUtc is null || adessoUtc - ultimaPuliziaUtc.Value >= IntervalloPulizia;

    /// <summary>Marcatore dell'ultima pulizia di questo PC: vive nella cartella locale del workspace, mai in quella condivisa.</summary>
    public static string? PercorsoMarcatorePulizia(string? cartellaLocale) =>
        string.IsNullOrWhiteSpace(cartellaLocale) ? null : Path.Combine(cartellaLocale, NomeMarcatorePulizia);

    /// <summary>Data dell'ultima pulizia di questo PC, o null se non è mai avvenuta (o il marcatore è illeggibile).</summary>
    public static DateTime? LeggiUltimaPulizia(string? cartellaLocale)
    {
        var marcatore = PercorsoMarcatorePulizia(cartellaLocale);
        try
        {
            return marcatore != null && File.Exists(marcatore) ? File.GetLastWriteTimeUtc(marcatore) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Registra l'ultima pulizia di questo PC (la data è l'ora del marcatore). Best-effort.</summary>
    public static void RegistraUltimaPulizia(string? cartellaLocale, DateTime adessoUtc)
    {
        var marcatore = PercorsoMarcatorePulizia(cartellaLocale);
        if (marcatore == null) return;
        try
        {
            Directory.CreateDirectory(cartellaLocale!);
            File.WriteAllText(marcatore, adessoUtc.ToString("O"));
            File.SetLastWriteTimeUtc(marcatore, adessoUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Nome file sicuro per l'ID: i caratteri non alfanumerici diventano "_" (gli ID sono "T-AB-0001").</summary>
    private static string NomeFile(string id) =>
        new string(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()) + EstensioneClaim;
}
