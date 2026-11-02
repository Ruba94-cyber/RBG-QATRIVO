namespace TestLoom.App.Services;

/// <summary>
/// Errore gestito quando il lock di commit del Test resta occupato oltre il timeout. Non è un conflitto di
/// revisione (il contenuto non è mai stato confrontato): un'altra postazione/processo sta scrivendo esattamente
/// lo stesso file in questo istante. L'utente deve solo riprovare; le modifiche locali NON vengono toccate
/// (l'eccezione viene lanciata prima di qualunque mutazione di <c>TestCase</c> o rilettura del file).
/// </summary>
public sealed class TestCaseSaveLockTimeoutException : IOException
{
    public string ElementoId { get; }

    public TestCaseSaveLockTimeoutException(string elementoId, Exception? innerException)
        : base($"Test {elementoId} in salvataggio da un'altra postazione, riprova.", innerException)
    {
        ElementoId = elementoId;
    }
}

/// <summary>
/// Lock di commit fra processi/postazioni per il salvataggio di un singolo Test. Copre l'INTERA finestra
/// "rilettura del file corrente -> confronto revisione/hash -> scrittura atomica" in
/// <see cref="TestCaseRepository"/>, cosa che il solo controllo optimistic-concurrency (rilettura + confronto
/// senza alcun lock condiviso) non garantiva: due processi potevano superare entrambi il confronto prima che
/// uno dei due scrivesse (vedi <see cref="TestCaseConcurrentSaveTwoInstancesTests"/> per l'evidenza).
///
/// Implementazione: un file sentinella <c>&lt;path&gt;.commitlock</c> accanto al file del Test, aperto con
/// <see cref="FileShare.None"/> (esclusivo). Su Windows/NTFS — anche per la cartella locale sincronizzata da
/// OneDrive/SharePoint, che è comunque un normale volume NTFS locale — questo è un vero lock di sistema
/// operativo: due thread/processi sulla STESSA macchina che tentano di acquisirlo sullo stesso path sono
/// serializzati per intero, incluso il caso di due "postazioni" (due <c>AppState</c>) che nello stesso processo
/// puntano alla stessa cartella condivisa, come nei test.
///
/// GARANZIE:
///  - Stessa macchina (più processi/thread, stessa cartella condivisa montata localmente): serializzazione
///    completa della finestra rilettura+confronto+scrittura. Il secondo arrivato NON supera più il confronto
///    finché il primo non ha commesso o fallito, e rilegge sempre lo stato fresco DENTRO il lock (mai una
///    fotografia presa prima di acquisirlo).
///  - Il file sentinella non lascia residui: viene creato con <see cref="FileOptions.DeleteOnClose"/> (il
///    sistema operativo lo elimina alla chiusura dell'handle, anche se il processo termina in modo anomalo,
///    perché Windows chiude comunque gli handle del processo terminato) E viene ripulito esplicitamente in
///    <see cref="Handle.Dispose"/> senza una seconda cancellazione non atomica. Non è un file gestito da
///    <see cref="TestLoomStorageOwnership"/> (estensione ".commitlock", non ".json"/".lock"): backup, indice e
///    manutenzione lo ignorano completamente.
///
/// LIMITI (dichiarati esplicitamente, non un dettaglio implementativo trascurabile):
///  - Fra macchine DIVERSE che condividono la cartella via OneDrive/SharePoint NON è un lock distribuito: ogni
///    PC ha una propria copia locale della cartella sincronizzata, quindi il file sentinella creato dal PC A
///    esiste inizialmente solo sul disco di A. OneDrive lo sincronizzerà verso B solo dopo il normale ritardo
///    di sincronizzazione (secondi, non nanosecondi): un secondo PC può quindi acquisire IL PROPRIO lock locale
///    (sul proprio file, ancora inesistente lato suo) mentre A sta già scrivendo, senza alcuna contesa visibile
///    a livello di file system.
///  - Per questo, DENTRO il lock la rilettura dal disco e il confronto con la revisione/hash attesi vengono
///    SEMPRE rifatti (mai riusati valori letti prima di acquisire il lock): questo non elimina la finestra
///    fra PC, ma la riduce al minimo possibile lato singolo PC e continua a garantire che una scrittura
///    "vecchia" non possa mai sovrascrivere una revisione più recente già arrivata su questo PC via sync.
///  - La correttezza fra PC diversi resta quindi affidata, come già prima di questa modifica, al rilevamento
///    dei fork/tombstone e al journal condiviso (<see cref="SharedTestJournalService"/>), non a questo lock.
///    Il file <see cref="File.Move"/> concorrente fra due PC che scrivono nello stesso istante sulle rispettive
///    copie locali resta possibile: quando si manifesta come IOException/UnauthorizedAccessException durante
///    la scrittura, <see cref="TestCaseRepository"/> lo traduce in <see cref="ConflittoRevisioneException"/>
///    invece di lasciarlo propagare come eccezione non gestita (punto 3 della correzione).
/// </summary>
public static class TestCaseCommitLock
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);
    private const int MinDelayMs = 15;
    private const int MaxDelayMs = 120;

    public static string LockPathFor(string targetPath) => targetPath + ".commitlock";

    /// <summary>
    /// Acquisisce, con retry e backoff limitato da un timeout complessivo, i lock su TUTTI i path indicati
    /// (tipicamente il path corrente del Test e, in caso di spostamento/rinomina, anche il path di destinazione).
    /// I path vengono ordinati in modo deterministico (ordinale) prima dell'acquisizione, cosi' due Save
    /// concorrenti che toccano la stessa COPPIA di path la richiedono sempre nello stesso ordine e non possono
    /// mai andare in deadlock reciproco.
    /// </summary>
    public static IDisposable AcquireAll(string elementoId, IReadOnlyCollection<string> targetPaths, TimeSpan? timeout = null)
    {
        var ordinati = targetPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var deadlineUtc = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var acquisiti = new List<Handle>(ordinati.Length);
        try
        {
            foreach (var target in ordinati)
                acquisiti.Add(AcquireOne(elementoId, target, deadlineUtc));

            var risultato = new CompositeHandle(acquisiti);
            acquisiti = null; // ownership passata al composite
            return risultato;
        }
        finally
        {
            if (acquisiti != null)
                foreach (var handle in acquisiti)
                    handle.Dispose();
        }
    }

    private static Handle AcquireOne(string elementoId, string targetPath, DateTime deadlineUtc)
    {
        var lockPath = LockPathFor(targetPath);
        var rng = Random.Shared;
        Exception? ultimoErrore = null;

        while (true)
        {
            try
            {
                var dir = Path.GetDirectoryName(lockPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.DeleteOnClose);
                return new Handle(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ultimoErrore = ex;
            }

            if (DateTime.UtcNow >= deadlineUtc)
                throw new TestCaseSaveLockTimeoutException(elementoId, ultimoErrore);

            var residuoMs = (deadlineUtc - DateTime.UtcNow).TotalMilliseconds;
            if (residuoMs <= 0) throw new TestCaseSaveLockTimeoutException(elementoId, ultimoErrore);
            var attesaMs = Math.Min(residuoMs, rng.Next(MinDelayMs, MaxDelayMs));
            if (attesaMs > 0) Thread.Sleep((int)attesaMs);
        }
    }

    private sealed class Handle : IDisposable
    {
        private readonly FileStream _stream;
        private bool _disposed;

        public Handle(FileStream stream) => _stream = stream;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _stream.Dispose(); } catch { }
            // NIENTE seconda File.Delete: dopo Dispose un altro processo può già avere
            // acquisito lo stesso path; rimuovergli il lock sarebbe una race TOCTOU.
            // DeleteOnClose resta il proprietario esclusivo della pulizia.
        }
    }

    private sealed class CompositeHandle : IDisposable
    {
        private readonly List<Handle> _handles;
        private bool _disposed;

        public CompositeHandle(List<Handle> handles) => _handles = handles;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Rilascio in ordine inverso rispetto all'acquisizione: non è strettamente necessario per la
            // correttezza (nessun altro lock dipende dall'ordine di rilascio), ma è la convenzione più leggibile.
            for (var i = _handles.Count - 1; i >= 0; i--)
                _handles[i].Dispose();
        }
    }
}
