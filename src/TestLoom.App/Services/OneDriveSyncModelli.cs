namespace TestLoom.App.Services;

/// <summary>Stato di un file di una cartella sincronizzata da OneDrive (Cloud Files / Files On-Demand).</summary>
public enum StatoFileCloud
{
    /// <summary>Contenuto presente sul disco e allineato (o file non gestito da Files On-Demand).</summary>
    Locale,
    /// <summary>Segnaposto "solo online": il contenuto va ancora scaricato (RECALL_ON_DATA_ACCESS / RECALL_ON_OPEN / OFFLINE).</summary>
    SoloOnline,
    /// <summary>Modifica locale non ancora caricata sul cloud (segnaposto non in sync o file nuovo non ancora preso in carico).</summary>
    InAttesaCaricamento,
}

/// <summary>Un file la cui situazione richiede attenzione. <see cref="Percorso"/> è assoluto.</summary>
public sealed record FileCloud(string Percorso, StatoFileCloud Stato, long Dimensione);

/// <summary>Risultato di una scansione: solo i file NON allineati sono elencati, il totale serve alla barra di avanzamento.</summary>
public sealed record ScansioneCloud(int TotaleFile, IReadOnlyList<FileCloud> DaAllineare);

/// <summary>
/// Accesso al file system e al client OneDrive, astratto per poter testare tutta la logica di sincronizzazione senza OneDrive
/// reale (vedi OneDriveFolderSyncSimulator nei test). L'implementazione di produzione è <see cref="OneDriveAccessoWindows"/>.
/// Tutti i metodi sono sincroni e chiamati dal servizio fuori dal thread UI.
/// </summary>
public interface IOneDriveAccesso
{
    /// <summary>Vero se il percorso sta dentro una cartella gestita da OneDrive/SharePoint sincronizzato su questo PC.</summary>
    bool EDentroOneDrive(string percorso);

    /// <summary>Vero se il processo del client OneDrive è in esecuzione per l'utente corrente.</summary>
    bool ClientInEsecuzione();

    /// <summary>Tenta di avviare il client OneDrive dell'utente (nessun permesso di amministratore). Non attende. Best-effort.</summary>
    bool AvviaClient();

    /// <summary>
    /// Elenca i file di <paramref name="radice"/> non allineati. <paramref name="percorsiSospetti"/> sono file (relativi alla
    /// radice) già noti come "da caricare" dalla chiusura precedente: se risultano ancora non sincronizzati contano come in attesa
    /// a prescindere dall'età.
    /// </summary>
    ScansioneCloud Scansiona(string radice, IReadOnlyCollection<string> percorsiSospetti, CancellationToken ct);

    /// <summary>Forza il download del contenuto di un file solo online (lettura che attiva il richiamo di Cloud Files).</summary>
    void Scarica(string percorso, CancellationToken ct);
}

/// <summary>Persistenza LOCALE (fuori dalla cartella condivisa) dei file rimasti da caricare alla chiusura.</summary>
public interface IOneDrivePendenteStore
{
    OneDrivePendente? Leggi(string radice);
    void Salva(string radice, OneDrivePendente pendente);
    void Cancella(string radice);
}

/// <summary>File che alla chiusura non erano ancora stati caricati su OneDrive; riprovati al prossimo avvio.</summary>
public sealed class OneDrivePendente
{
    public DateTime RegistratoIlUtc { get; set; }
    public int NumeroFile { get; set; }
    /// <summary>Percorsi relativi alla radice (al massimo <see cref="OneDriveSyncOptions.MassimoPercorsiPersistiti"/>).</summary>
    public List<string> Percorsi { get; set; } = new();
}

public enum EsitoSyncOneDrive
{
    /// <summary>La cartella non è gestita da OneDrive (rete/locale): nessuna azione.</summary>
    NonApplicabile,
    /// <summary>Tutto allineato.</summary>
    Completata,
    /// <summary>Tempo scaduto con file ancora da scaricare e/o da caricare.</summary>
    Timeout,
    /// <summary>Il client OneDrive non è in esecuzione e non è stato possibile avviarlo.</summary>
    ClientNonAttivo,
    /// <summary>Operazione annullata dall'utente (es. "Chiudi senza attendere").</summary>
    Annullata,
    /// <summary>Errore imprevisto durante la verifica: non blocca mai l'app, ma l'avvio resta prudente.</summary>
    Errore,
}

public sealed record RisultatoSyncOneDrive(EsitoSyncOneDrive Esito, int DaScaricare, int DaCaricare, string? Dettaglio = null)
{
    /// <summary>Vero se è sicuro scrivere sui dati: allineati oppure non gestiti da OneDrive.</summary>
    public bool Sicuro => Esito is EsitoSyncOneDrive.Completata or EsitoSyncOneDrive.NonApplicabile;
}

public enum FaseSyncOneDrive { Verifica, AvvioClient, Scaricamento, AttesaCaricamento }

/// <summary>Avanzamento pubblicato al thread UI tramite <see cref="IProgress{T}"/>. Totale 0 = non ancora noto (barra indeterminata).</summary>
public sealed record AvanzamentoSyncOneDrive(FaseSyncOneDrive Fase, int Elaborati, int Totale, int DaScaricare, int DaCaricare, TimeSpan Trascorso);

/// <summary>Parametri del servizio; modificabili solo dai test.</summary>
public sealed class OneDriveSyncOptions
{
    public TimeSpan TimeoutAvvio { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan TimeoutChiusura { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan IntervalloVerifica { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Attesa massima, all'avvio, perché il client appena lanciato risulti in esecuzione.</summary>
    public TimeSpan AttesaAvvioClient { get; init; } = TimeSpan.FromSeconds(10);
    public int ParallelismoDownload { get; init; } = 4;
    public int MassimoPercorsiPersistiti { get; init; } = 200;
}
