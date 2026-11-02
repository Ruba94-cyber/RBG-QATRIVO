namespace TestLoom.App.Services;

/// <summary>Stato esplicito dell'indice locale. Un booleano non basta: "leggibile" e "aggiornato" sono cose diverse.</summary>
public enum LocalIndexHealth
{
    /// <summary>L'indice e' allineato ai JSON condivisi verificati: il percorso indicizzato e' autorizzato.</summary>
    Healthy,

    /// <summary>Ricostruzione in corso. Nessun nuovo lettore parte, i chiamanti usano il percorso lento.</summary>
    Recovering,

    /// <summary>
    /// Degradato: l'indice puo' anche aprirsi, ma non e' piu' una fotografia attendibile (errore dopo un
    /// Save, ReconcileFull fallito, ricostruzione fallita). Nessuna pagina deve trattarlo come valido.
    /// </summary>
    Unavailable
}

public sealed record LocalIndexStatus(
    LocalIndexHealth Salute,
    long Generazione,
    string? Motivo,
    int RicostruzioniEseguite,
    bool UltimaRicostruzioneFallita,
    DateTime AggiornatoUtc);

/// <summary>
/// Esito di una ricostruzione dai dati condivisi. Completa=false significa che la sorgente NON era
/// integralmente verificabile (consegna OneDrive parziale, cartella irraggiungibile, JSON illeggibili):
/// in quel caso l'indice non viene mai marcato pronto.
/// </summary>
public sealed record LocalIndexRebuildOutcome(bool Completa, string? Motivo, int Indicizzati)
{
    public static LocalIndexRebuildOutcome Riuscita(int indicizzati) => new(true, null, indicizzati);
    public static LocalIndexRebuildOutcome Incompleta(string motivo) => new(false, motivo, 0);
}

/// <summary>Cosa deve fare il chiamante dopo un guasto in lettura.</summary>
public enum LocalIndexReadFaultAction
{
    /// <summary>Percorso lento per questo giro; l'indice tornera' da solo quando la generazione avanza.</summary>
    Fallback,

    /// <summary>Una generazione sana e' gia' disponibile: si puo' ritentare UNA volta, senza ricorsione.</summary>
    RetryOnNewGeneration
}

/// <summary>
/// Coordinatore per workspace/database dell'indice SQLite locale: unico punto in cui si decide se l'indice e'
/// utilizzabile, chi lo sta usando, quando va ricostruito e da cosa. Reader, repository, refresh e manutenzione
/// condividono questa istanza.
///
/// Prima esisteva solo un recupero in fase di inizializzazione (LocalTestIndexStore.InitializeWithRecovery) che
/// per giunta intercettava qualunque SqliteException. A runtime, invece, ogni pagina faceva catch e fallback al
/// percorso lento: dopo un errore l'app restava degradata per sempre, in silenzio, fino al riavvio — e senza
/// nemmeno distinguere "indice corrotto" da "indice leggibile ma OBSOLETO".
///
/// Regole non negoziabili incapsulate qui:
/// - solo SQLITE_CORRUPT (11) e SQLITE_NOTADB (26) avviano la quarantena; tutto il resto ha politiche diverse;
/// - N richieste concorrenti producono UNA sola ricostruzione (single flight);
/// - prima della quarantena si smette di usare la generazione guasta e si attendono reader e writer in volo;
/// - i reader tornano abilitati solo a ricostruzione COMPLETA, su una nuova generazione;
/// - l'indice non e' mai la fonte dati: fallire qui non puo' mai toccare i JSON condivisi.
/// </summary>
public sealed class LocalIndexRecoveryCoordinator
{
    private static readonly TimeSpan AttesaOperazioniInVolo = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Func<LocalIndexRebuildOutcome> _ricostruisci;
    private readonly string _nome;

    /// <summary>
    /// Attesa massima che una lettura in errore concede alla ricostruzione prima di rinunciare. Limitata di
    /// proposito: su archivi piccoli la ricostruzione finisce dentro questa finestra e il chiamante rientra
    /// subito sul percorso indicizzato; su archivi grandi si cade sul percorso lento dopo un ritardo comunque
    /// molto inferiore al costo del percorso lento stesso. Non diventa mai un'attesa illimitata sulla UI.
    /// </summary>
    public TimeSpan AttesaRecuperoInLettura { get; set; } = TimeSpan.FromSeconds(2);

    private LocalIndexHealth _salute = LocalIndexHealth.Healthy;
    private long _generazione = 1;
    private int _operazioniInVolo;
    private string? _motivo;
    private int _ricostruzioniEseguite;
    private int _richiesteDiRecupero;
    private bool _ultimaRicostruzioneFallita;
    private Task<bool>? _recuperoInCorso;

    /// <param name="databasePath">DB locale governato; puo' essere vuoto solo nei test del solo coordinatore.</param>
    /// <param name="ricostruisci">
    /// Ricostruzione dai dati condivisi VERIFICATI. Viene invocata al massimo una volta per recupero, gia'
    /// fuori dal gate dei lettori, e non deve passare da TryRead/TryWrite (sarebbe rifiutata: lo stato e'
    /// Recovering proprio per impedire ogni altro accesso).
    /// </param>
    public LocalIndexRecoveryCoordinator(string databasePath, Func<LocalIndexRebuildOutcome> ricostruisci, string? nome = null)
    {
        DatabasePath = databasePath ?? "";
        _ricostruisci = ricostruisci ?? throw new ArgumentNullException(nameof(ricostruisci));
        _nome = nome ?? (string.IsNullOrWhiteSpace(databasePath) ? "indice locale" : Path.GetFileName(databasePath));
    }

    public string DatabasePath { get; }

    public LocalIndexHealth Salute { get { lock (_gate) return _salute; } }
    public long Generazione { get { lock (_gate) return _generazione; } }
    public bool IsUsable { get { lock (_gate) return _salute == LocalIndexHealth.Healthy; } }
    public string? Motivo { get { lock (_gate) return _motivo; } }

    /// <summary>Ricostruzioni realmente eseguite: e' la misura che dimostra il raggruppamento delle richieste.</summary>
    public int RicostruzioniEseguite { get { lock (_gate) return _ricostruzioniEseguite; } }

    /// <summary>Richieste ricevute, incluse quelle assorbite da un recupero gia' in corso.</summary>
    public int RichiesteDiRecupero { get { lock (_gate) return _richiesteDiRecupero; } }

    public LocalIndexStatus Stato
    {
        get
        {
            lock (_gate)
                return new LocalIndexStatus(_salute, _generazione, _motivo, _ricostruzioniEseguite,
                    _ultimaRicostruzioneFallita, DateTime.UtcNow);
        }
    }

    /// <summary>Notifica non modale: ricostruzione in corso / completata / fallita. Non deve mai far fallire il coordinatore.</summary>
    public event Action<LocalIndexStatus>? StatoCambiato;

    // ---------------------------------------------------------------- letture

    public bool TryRead(string operazione, Action lettura) =>
        TryRead<object?>(operazione, () => { lettura(); return null; }, out _);

    /// <summary>
    /// Esegue una lettura sul percorso indicizzato. Ritorna false quando il chiamante deve usare il percorso
    /// lento: indice non sano, oppure guasto durante la lettura. Il retry (al massimo UNO, sulla nuova
    /// generazione) e' centralizzato qui proprio per non riaverlo sparso e ricorsivo nei ViewModel.
    /// </summary>
    public bool TryRead<T>(string operazione, Func<T> lettura, out T risultato)
    {
        risultato = default!;
        if (!TryBegin(out var generazioneLettura)) return false;

        Exception? guasto;
        try
        {
            risultato = lettura();
            return true;
        }
        catch (Exception ex)
        {
            guasto = ex;
        }
        finally
        {
            End();
        }

        // Fuori dal conteggio delle operazioni in volo: altrimenti il recupero attenderebbe se stesso.
        var azione = SegnalaGuastoLettura(operazione, guasto, generazioneLettura);

        // Solo dopo una corruzione ha senso attendere: e' l'unico guasto che avvia una ricostruzione.
        if (azione == LocalIndexReadFaultAction.Fallback &&
            LocalIndexFaultClassifier.IsCorruption(guasto) &&
            AttesaRecuperoInLettura > TimeSpan.Zero &&
            AttendiRecupero(AttesaRecuperoInLettura))
        {
            azione = LocalIndexReadFaultAction.RetryOnNewGeneration;
        }

        if (azione != LocalIndexReadFaultAction.RetryOnNewGeneration) return false;

        if (!TryBegin(out _)) return false;
        try
        {
            risultato = lettura();
            return true;
        }
        catch (Exception ex)
        {
            // Un secondo guasto sulla generazione nuova non riavvia il ciclo: si degrada e basta.
            CrashLogService.Registra($"Indice locale {_nome}: {operazione} fallita anche dopo la ricostruzione", ex);
            Degrada($"{operazione}: {LocalIndexFaultClassifier.Descrivi(LocalIndexFaultClassifier.Classify(ex))}");
            return false;
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// Scrittura sull'indice a valle di un JSON gia' committato. Ritorna false se l'indice non e' stato
    /// aggiornato: il salvataggio resta valido e autorevole, ma il read model va considerato OBSOLETO.
    /// Non deve MAI causare la ripetizione del Save.
    /// </summary>
    public bool TryWrite(string operazione, Action scrittura)
    {
        if (!TryBegin(out _))
        {
            // L'indice era gia' fuori servizio: la mutazione verra' recuperata dalla riconciliazione completa.
            return false;
        }

        try
        {
            scrittura();
            return true;
        }
        catch (Exception ex)
        {
            var tipo = LocalIndexFaultClassifier.Classify(ex);
            CrashLogService.Registra(LocalIndexFaultClassifier.IsErroreSqlProgramma(ex)
                ? $"Indice locale {_nome}: BUG SQL di programma in {operazione} (read model obsoleto)"
                : $"Indice locale {_nome}: {operazione}", ex);
            Degrada($"{operazione}: {LocalIndexFaultClassifier.Descrivi(tipo)}");
            if (tipo == LocalIndexFaultKind.Corruption) _ = RichiediRecupero($"{operazione}: {LocalIndexFaultClassifier.Descrivi(tipo)}");
            return false;
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// Classifica un guasto in lettura e decide la politica. Solo la corruzione avvia la ricostruzione: un
    /// BUSY, un disco pieno o una ACL negata NON devono mettere in quarantena un database sano.
    /// </summary>
    public LocalIndexReadFaultAction SegnalaGuastoLettura(string operazione, Exception? guasto, long generazioneLettura)
    {
        if (guasto != null && LocalIndexFaultClassifier.IsErroreSqlProgramma(guasto))
        {
            // Bug del programma (SQL non valido), non un guasto dell'ambiente: il database resta sano e la lettura
            // ripiega sul percorso lento. Niente degrado: l'indice resta servibile per le altre letture.
            CrashLogService.Registra($"Indice locale {_nome}: BUG SQL di programma in {operazione} (indice non degradato)", guasto);
            return LocalIndexReadFaultAction.Fallback;
        }
        var tipo = LocalIndexFaultClassifier.Classify(guasto);
        if (guasto != null) CrashLogService.Registra($"Indice locale {_nome}: {operazione}", guasto);

        switch (tipo)
        {
            case LocalIndexFaultKind.Corruption:
                lock (_gate)
                {
                    // La generazione e' gia' avanzata: qualcun altro ha ricostruito mentre leggevamo.
                    // Una sola ripetizione della lettura, sulla generazione nuova.
                    if (_salute == LocalIndexHealth.Healthy && _generazione != generazioneLettura)
                        return LocalIndexReadFaultAction.RetryOnNewGeneration;
                }
                _ = RichiediRecupero($"{operazione}: {LocalIndexFaultClassifier.Descrivi(tipo)}");
                return LocalIndexReadFaultAction.Fallback;

            case LocalIndexFaultKind.Busy:
                // Contesa transitoria: nessuna quarantena, nessun cambio di stato. Il giro dopo riprova.
                return LocalIndexReadFaultAction.Fallback;

            case LocalIndexFaultKind.AccessDenied:
            case LocalIndexFaultKind.DiskFull:
            case LocalIndexFaultKind.Io:
            case LocalIndexFaultKind.SchemaIncompatible:
            case LocalIndexFaultKind.Other:
            case LocalIndexFaultKind.None:
            default:
                // Il database puo' essere perfettamente sano: si degrada il percorso indicizzato senza
                // toccare i file. Tornera' utilizzabile dopo una riconciliazione riuscita.
                Degrada($"{operazione}: {LocalIndexFaultClassifier.Descrivi(tipo)}");
                return LocalIndexReadFaultAction.Fallback;
        }
    }

    // ------------------------------------------------------------ transizioni

    /// <summary>
    /// L'indice e' leggibile ma non piu' allineato (ReconcileFull/ApplyBatch/Upsert falliti). Nessuna
    /// quarantena: il contenuto e' valido, e' la freschezza a non esserlo.
    /// </summary>
    public void InvalidaComeObsoleto(string motivo)
    {
        Degrada(motivo);
    }

    /// <summary>Ricostruzione/riconciliazione completata con successo: nuova generazione, reader riabilitati.</summary>
    public void MarcaSano(string? motivo = null)
    {
        LocalIndexStatus stato;
        lock (_gate)
        {
            // Durante una ricostruzione la transizione appartiene al coordinatore: una riconciliazione
            // ordinaria che finisse nel frattempo non deve riammettere i lettori su un file che sta per
            // essere messo in quarantena.
            if (_salute == LocalIndexHealth.Recovering) return;
            if (_salute != LocalIndexHealth.Healthy) _generazione++;
            _salute = LocalIndexHealth.Healthy;
            _motivo = motivo;
            _ultimaRicostruzioneFallita = false;
            stato = SnapshotSenzaLock();
        }
        Notifica(stato);
    }

    private void Degrada(string motivo)
    {
        LocalIndexStatus stato;
        lock (_gate)
        {
            if (_salute == LocalIndexHealth.Recovering) return; // un recupero in corso ha la precedenza
            if (_salute == LocalIndexHealth.Unavailable && string.Equals(_motivo, motivo, StringComparison.Ordinal)) return;
            _salute = LocalIndexHealth.Unavailable;
            _motivo = motivo;
            stato = SnapshotSenzaLock();
        }
        Notifica(stato);
    }

    // -------------------------------------------------------------- recuperi

    /// <summary>
    /// Single flight: dieci query concorrenti che incontrano la stessa corruzione condividono lo stesso Task
    /// e producono UNA sola ricostruzione.
    /// </summary>
    public Task<bool> RichiediRecupero(string motivo)
    {
        LocalIndexStatus? daNotificare = null;
        Task<bool> task;
        lock (_gate)
        {
            _richiesteDiRecupero++;
            if (_recuperoInCorso != null) return _recuperoInCorso;

            _salute = LocalIndexHealth.Recovering;
            _motivo = motivo;
            daNotificare = SnapshotSenzaLock();
            task = Task.Run(() => EseguiRecupero(motivo));
            _recuperoInCorso = task;
        }
        if (daNotificare != null) Notifica(daNotificare);
        return task;
    }

    /// <summary>Recupero sincrono: comando "Riprova" dell'utente e test. Condivide il single flight.</summary>
    public bool RecuperaAdesso(string motivo) => RichiediRecupero(motivo).GetAwaiter().GetResult();

    /// <summary>Attende il recupero eventualmente in corso. Ritorna false su timeout o recupero fallito.</summary>
    public bool AttendiRecupero(TimeSpan timeout)
    {
        Task<bool>? task;
        lock (_gate) task = _recuperoInCorso;
        if (task == null) return IsUsable;
        return task.Wait(timeout) && task.Result;
    }

    private bool EseguiRecupero(string motivo)
    {
        try
        {
            // 1. Nessun nuovo lettore parte (stato gia' Recovering) e si attendono quelli in volo. Spostare
            //    i file con handle ancora aperti fallirebbe comunque su Windows: meglio attendere.
            if (!AttendiOperazioniInVolo(AttesaOperazioniInVolo))
            {
                ChiudiRecupero(false, "Ricostruzione rimandata: letture o scritture sull'indice ancora in corso.");
                return false;
            }

            // 2. Quarantena DB+WAL+SHM come insieme unico. Se fallisce non si dichiara riuscita e non si
            //    ricostruisce sopra un file ancora corrotto.
            if (!string.IsNullOrWhiteSpace(DatabasePath) && !LocalIndexQuarantine.TrySequester(DatabasePath, out var errore))
            {
                ChiudiRecupero(false, errore);
                return false;
            }

            // 3. Ricostruzione dai JSON condivisi verificati. Una sorgente incompleta non produce mai un
            //    indice parziale marcato pronto.
            var esito = _ricostruisci();
            if (!esito.Completa)
            {
                ChiudiRecupero(false, esito.Motivo ?? "Sorgente condivisa incompleta: indice non marcato pronto.");
                return false;
            }

            ChiudiRecupero(true, null);
            return true;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra($"Indice locale {_nome}: ricostruzione ({motivo})", ex);
            ChiudiRecupero(false, $"Ricostruzione non riuscita: {ex.Message}");
            return false;
        }
    }

    private void ChiudiRecupero(bool riuscito, string? motivo)
    {
        LocalIndexStatus stato;
        lock (_gate)
        {
            _recuperoInCorso = null;
            _ultimaRicostruzioneFallita = !riuscito;
            if (riuscito)
            {
                _ricostruzioniEseguite++;
                _generazione++;
                _salute = LocalIndexHealth.Healthy;
                _motivo = null;
            }
            else
            {
                _salute = LocalIndexHealth.Unavailable;
                _motivo = motivo;
            }
            stato = SnapshotSenzaLock();
        }
        Notifica(stato);
    }

    // ------------------------------------------------- operazioni in volo

    private bool TryBegin(out long generazione)
    {
        lock (_gate)
        {
            generazione = _generazione;
            if (_salute != LocalIndexHealth.Healthy) return false;
            _operazioniInVolo++;
            return true;
        }
    }

    private void End()
    {
        lock (_gate)
        {
            if (_operazioniInVolo > 0) _operazioniInVolo--;
            if (_operazioniInVolo == 0) Monitor.PulseAll(_gate);
        }
    }

    private bool AttendiOperazioniInVolo(TimeSpan timeout)
    {
        var scadenza = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            while (_operazioniInVolo > 0)
            {
                var rimasto = scadenza - DateTime.UtcNow;
                if (rimasto <= TimeSpan.Zero) return false;
                Monitor.Wait(_gate, rimasto);
            }
            return true;
        }
    }

    private LocalIndexStatus SnapshotSenzaLock() =>
        new(_salute, _generazione, _motivo, _ricostruzioniEseguite, _ultimaRicostruzioneFallita, DateTime.UtcNow);

    private void Notifica(LocalIndexStatus stato)
    {
        try { StatoCambiato?.Invoke(stato); }
        catch (Exception ex) { CrashLogService.Registra($"Indice locale {_nome}: notifica stato", ex); }
    }
}
