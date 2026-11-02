using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestLoom.App.Services;

public class LockInfo
{
    public string Operatore { get; set; } = "";
    public DateTime AcquisitoIl { get; set; }
    public DateTime UltimoHeartbeat { get; set; }
    public string? BaseContentHash { get; set; }
    public string? DeviceId { get; set; }
    public string? SessionId { get; set; }

    /// <summary>Impronta dei byte letti dal file. Non viene serializzata.</summary>
    [JsonIgnore]
    internal string? ImprontaFile { get; set; }
}

public class LockResult
{
    public bool Acquisito { get; set; }
    public LockInfo? DetenutoDa { get; set; }
    /// <summary>Vero quando questa istanza di LockService possedeva già la sessione prima della chiamata.</summary>
    public bool SessioneGiaPosseduta { get; set; }

    /// <summary>Vero soltanto se questa acquisizione segue un ForceUnlock su questo stesso path, senza che nel
    /// frattempo un'altra sessione abbia già riacquisito e rilasciato normalmente il lock. Serve al chiamante
    /// (editor master data) per sapere se l'automerge per-riga di Phase D è ammesso per questa sessione di
    /// modifica: di norma un Master non deve mai vedere un merge silenzioso, ma dopo una forzatura esplicita
    /// dell'amministratore recuperare quanto possibile da entrambe le parti è il comportamento voluto.</summary>
    public bool SeguitoDaForzatura { get; set; }
}

/// <summary>Esito di <see cref="LockService.EnsureOwned"/>. <see cref="Posseduto"/>: al termine questa sessione
/// possiede il lock (già suo, oppure riacquisito in modo trasparente perché scaduto/sparito). <see cref="Holder"/>:
/// valorizzato SOLO per un conflitto reale, cioè un lock vivo di un'altra sessione.</summary>
/// <summary><see cref="Confermato"/>: il lock su disco è stato rinnovato o riacquisito in questa chiamata. Un rinnovo
/// fallito per I/O transitorio lascia <see cref="Posseduto"/> vero ma non confermato (P2.1).</summary>
public sealed record LockEnsureResult(bool Posseduto, LockInfo? Holder, bool Riacquisito, bool Confermato = false, string? SessionId = null);

/// <summary>Stato del lock di un'altra sessione come lo vede questa postazione. <see cref="Sconosciuto"/> non significa
/// libero: la lettura è scaduta o fallita (file bloccato, rete lenta, contenuto ancora in scrittura). La UI non deve
/// offrire la modifica come se il lock fosse libero (P2.2).</summary>
public enum StatoLockAltrui { Libero, Occupato, Sconosciuto }

/// <summary>Esito di <see cref="LockService.GetForeignActiveLock"/>. <see cref="LockInfo"/> è valorizzato solo con
/// <see cref="StatoLockAltrui.Occupato"/>.</summary>
public sealed record LetturaLockAltrui(StatoLockAltrui Stato, LockInfo? Info);

/// <summary>Esito del distacco di un file lock (<see cref="LockService.TryDetachLock"/>). <see cref="Errore"/> lascia
/// lo stato del lock incerto: chi possiede il lock deve conservare la propria proprietà in memoria.</summary>
internal enum EsitoDistacco { Rimosso, NonNecessario, Errore }

internal readonly record struct LockFileSnapshot(LockInfo? Info, string Impronta);

public sealed record LockCleanupResult(int RimossiScaduti, int RimossiIlleggibiliOrfani, int Errori)
{
    public int TotaleRimossi => RimossiScaduti + RimossiIlleggibiliOrfani;
}

/// <summary>
/// Lock applicativo file-based. È un guard UX/advisory: revision/hash/fork detection restano la garanzia di
/// correttezza cross-PC. Ogni acquisizione possiede però un SessionId reale: heartbeat e Release normali
/// possono agire soltanto sul lock creato dalla stessa istanza LockService. Un'altra finestra/postazione con
/// lo stesso nome operatore non può quindi rubare, rinnovare o cancellare accidentalmente la sessione altrui.
/// ForceUnlock resta intenzionalmente l'unica eccezione esplicita.
///
/// Le rimozioni condizionali non fanno più "read -> delete path" in due momenti separati: il file verificato
/// viene aperto impedendo read/write concorrenti, poi rinominato fuori dal path `.lock` mentre lo stesso handle
/// è ancora aperto. Il file spostato viene riverificato con lo stesso predicato prima di essere eliminato, e
/// ripristinato se non corrisponde (un altro PC può averlo sostituito nel frattempo, P2.9). Un lock nuovo può
/// quindi nascere soltanto sul path originale DOPO il detach e non può essere cancellato dal cleanup/release
/// della sessione precedente.
///
/// Le API di sola ispezione non fanno housekeeping: un Get non deve trasformarsi in una delete condivisa.
/// I lock scaduti/orfani vengono rimossi soltanto quando un writer prova realmente ad acquisire il target o
/// dalla manutenzione esplicita, che è a sua volta bloccata se il workspace non è scrivibile in sicurezza.
/// </summary>
public class LockService
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);
    private static readonly LockLeaseObservationTracker OsservazioniCondivise = new();
    /// <summary>Tetto delle operazioni di lock su un file: 3 s su OneDrive/locale (invariato), più ampio su cartella di
    /// rete in LAN o VPN (6.6.1, decisione 2). Vedi <see cref="TimeoutCondivisione"/>.</summary>
    private static TimeSpan IoTimeoutPer(string targetPath) => TimeoutCondivisione.Per(targetPath);

    /// <summary>Intervallo consigliato degli heartbeat degli editor: ben sotto <see cref="Timeout"/> cosi' che
    /// qualche tentativo saltato (file trattenuto dal client OneDrive) non porti il lock a scadere.</summary>
    public static readonly TimeSpan IntervalloHeartbeat = TimeSpan.FromMinutes(2);
    private const int TentativiHeartbeat = 3;
    private static readonly TimeSpan AttesaTraTentativi = TimeSpan.FromMilliseconds(200);

    internal static Action<string, string>? TestHookPrimaDiVerificaSpostato { get; set; }

    // DeviceId calcolato una sola volta per istanza: prima, un GetOrCreate() fallito sotto contesa faceva
    // oscillare il valore scritto nel lock fra l'id persistito e Environment.MachineName.
    private readonly Lazy<string?> _deviceId;
    private readonly LockLeaseObservationTracker _osservazioniLease;

    public LockService() : this(OsservazioniCondivise) { }

    private LockService(LockLeaseObservationTracker osservazioni)
    {
        _deviceId = new(CalcolaDeviceId, LazyThreadSafetyMode.ExecutionAndPublication);
        _osservazioniLease = osservazioni;
    }

    /// <summary>Hook per i test: DeviceId forzato, per simulare due PC (diverso) o lo stesso PC (uguale) con due
    /// istanze di servizio nello stesso processo.</summary>
    public LockService(string? deviceIdForzato) : this(deviceIdForzato, OsservazioniCondivise) { }

    internal LockService(string? deviceIdForzato, LockLeaseObservationTracker osservazioni)
    {
        _deviceId = new(() => deviceIdForzato);
        _osservazioniLease = osservazioni;
    }

    internal void ResetLeaseObservationsUnderRoot(string root) => _osservazioniLease.RimuoviRadice(root);

    /// <summary>TestLoom è a istanza singola per PC (SingleInstanceService, mutex per sessione di logon): un lock
    /// vivo di un'ALTRA sessione con stesso Operatore E stesso DeviceId di questa istanza non può appartenere a
    /// un'istanza viva di questo PC, quindi è il residuo di un'esecuzione precedente (crash/chiusura forzata) e
    /// può essere sostituito. Stesso operatore ma DeviceId diverso resta un altro PC.</summary>
    // Limiti noti (accettati): (1) il mutex di istanza singola non ha prefisso Global\, quindi vale per sessione
    // di logon: la stessa utenza Windows in due sessioni RDP sullo stesso PC può avere due istanze vive con lo
    // stesso DeviceId, e il "residuo" di una potrebbe essere sostituito dall'altra; (2) fra la lettura del lock e
    // lo spostamento in TryDetachLock resta una finestra in cui un altro PC può sostituire il file: il predicato
    // viene rivalutato sul file spostato e, se non combacia, il file torna al path originale (P2.9), ma non esiste
    // una transazione atomica sul file system condiviso. Revision/hash al Salva restano la garanzia di correttezza.
    private bool IsResiduoDiQuestaPostazione(string lockPath, LockInfo info, string? operatore)
    {
        var device = _deviceId.Value;
        return operatore != null && !Owns(lockPath, info) &&
               string.Equals(info.Operatore, operatore, StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(device) && !string.IsNullOrWhiteSpace(info.DeviceId) &&
               string.Equals(info.DeviceId, device, StringComparison.OrdinalIgnoreCase);
    }
    private readonly ConcurrentDictionary<string, string> _ownedSessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Marcatore one-shot: presente soltanto tra un ForceUnlock riuscito su questo path e la
    /// successiva acquisizione (di chiunque). Consumato alla prima TryAcquire che segue, cosi' una forzatura
    /// vecchia non continua a marcare sessioni future indefinitamente.</summary>
    private readonly ConcurrentDictionary<string, byte> _forzatiInAttesaDiRiacquisizione = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Notifica una forzatura reale, per audit centralizzato senza accoppiare il servizio ai ViewModel.</summary>
    public event Action<string, LockInfo?>? ForceUnlocked;

    public static string LockPathFor(string targetPath) => targetPath + ".lock";

    /// <summary>
    /// Tenta di acquisire il lock entro il tetto di tempo. Se il tetto scade, tentativo abbandonato può ancora riuscire a creare
    /// il lock: in tal caso il lock viene rimosso (6.6.1, correzione F), solo se è ancora quello di quel tentativo (stessa sessione),
    /// così non resta un lock orfano che blocca gli altri PC fino alla scadenza.
    /// </summary>
    public LockResult TryAcquire(string targetPath, string operatore)
    {
        // 6.7.1 (fix rete): la stessa pulizia dei tentativi abbandonati vale per rete e per OneDrive/locale. Su scadenza del
        // tetto o su errore del tentativo, il lock eventualmente creato dal tentativo (riconosciuto dalla SessionId) viene rimosso.
        var sessione = Guid.NewGuid().ToString("N");
        var lockPath = LockPathFor(targetPath);
        var lavoro = Task.Run(() => TryAcquireInterno(targetPath, operatore, sessione));
        try
        {
            if (lavoro.Wait(IoTimeoutPer(targetPath))) return lavoro.Result;
        }
        catch (Exception)
        {
            // Tentativo terminato con errore entro il tetto: un lock creato prima dell'errore non deve restare orfano.
        }
        AvviaPuliziaTentativoAbbandonato(lavoro, lockPath, sessione);
        return new LockResult { Acquisito = false };
    }

    /// <summary>Acquisisce un lock per una nuova finestra o un'azione one-shot senza condividere una sessione
    /// già posseduta da un altro editor della stessa istanza.</summary>
    public LockResult TryAcquireNuovaSessione(string targetPath, string operatore)
    {
        var risultato = TryAcquire(targetPath, operatore);
        if (!risultato.Acquisito || !risultato.SessioneGiaPosseduta) return risultato;
        return new LockResult
        {
            Acquisito = false,
            DetenutoDa = risultato.DetenutoDa,
            SessioneGiaPosseduta = true
        };
    }

    /// <summary>Quando il tentativo termina dopo la scadenza del tetto o con errore, rimuove il lock che eventualmente ha creato.
    /// Eseguito in background: il chiamante non attende. Best-effort, non lancia mai.</summary>
    private void AvviaPuliziaTentativoAbbandonato(Task<LockResult> lavoro, string lockPath, string sessione)
    {
        lavoro.ContinueWith(_ =>
        {
            try { RimuoviLockAbbandonato(lockPath, sessione); }
            catch (Exception) { /* pulizia best-effort di un tentativo già concluso: nessun effetto sul chiamante */ }
        }, TaskScheduler.Default);
    }

    private const int TentativiRimozioneAbbandonato = 3;
    private static readonly TimeSpan AttesaRimozioneAbbandonato = TimeSpan.FromMilliseconds(150);

    /// <summary>Rimuove il lock creato da un tentativo abbandonato, solo se il file porta ancora la sessione di quel tentativo.
    /// Se la cancellazione fallisce per un errore transitorio (SMB, client di sincronizzazione) ritenta con breve backoff; l'esito
    /// viene registrato, perché un lock non rimosso resta orfano fino alla scadenza.</summary>
    /// <remarks>Ogni tentativo rivaluta la SessionId sul file aperto (TryDetachLock: verifica sullo stesso handle, poi rinomina,
    /// poi elimina il solo file spostato). Nessuna delete alla cieca: un lock intanto sostituito da un'altra sessione non viene
    /// toccato. <paramref name="primaDiOgniTentativo"/> e' un hook di test, invocato prima di ogni tentativo.</remarks>
    internal void RimuoviLockAbbandonato(string lockPath, string sessione, Action<int>? primaDiOgniTentativo = null)
    {
        for (var tentativo = 1; tentativo <= TentativiRimozioneAbbandonato; tentativo++)
        {
            primaDiOgniTentativo?.Invoke(tentativo);
            if (TryDetachLock(lockPath, snapshot => snapshot.Info?.SessionId == sessione,
                    allowUnreadable: false, out _) == EsitoDistacco.Rimosso)
            {
                _ownedSessions.TryRemove(lockPath, out _);
                _osservazioniLease.Rimuovi(lockPath);
                if (tentativo > 1)
                    DiagnosticTraceService.Registra("Lock.RimozioneAbbandonata", $"path={lockPath}; tentativi={tentativo}; esito=rimosso");
                return;
            }
            // Nessuna rimozione: se il file non c'e' piu' o non e' piu' di questa sessione, non si ritenta e non si tocca nulla.
            var attuale = ReadLock(lockPath);
            if (!File.Exists(lockPath))
            {
                _osservazioniLease.Rimuovi(lockPath);
                return;
            }
            if (attuale != null && attuale.SessionId != sessione) return;
            if (tentativo < TentativiRimozioneAbbandonato) Thread.Sleep(AttesaRimozioneAbbandonato * tentativo);
        }
        DiagnosticTraceService.Registra("Lock.RimozioneAbbandonata",
            $"path={lockPath}; tentativi={TentativiRimozioneAbbandonato}; esito=non-rimosso (lock orfano fino alla scadenza)");
    }

    private LockResult TryAcquireInterno(string targetPath, string operatore, string? sessione = null)
    {
        var lockPath = LockPathFor(targetPath);
        var existing = ReadLock(lockPath);
        if (existing == null && !File.Exists(lockPath)) _osservazioniLease.Rimuovi(lockPath);

        if (existing == null && File.Exists(lockPath))
        {
            if (IsUnreadableOrphan(lockPath))
            {
                if (TryDetachLock(lockPath,
                        snapshot => snapshot.Info == null && _osservazioniLease.EScaduto(lockPath, snapshot.Impronta, Timeout),
                        allowUnreadable: true,
                        out _) == EsitoDistacco.Rimosso)
                {
                    _ownedSessions.TryRemove(lockPath, out _);
                    _osservazioniLease.Rimuovi(lockPath);
                }
                else
                    return new LockResult { Acquisito = false, DetenutoDa = ReadLock(lockPath) };
            }
            else
                return new LockResult { Acquisito = false };
        }

        existing = ReadLock(lockPath);
        var residuoNostro = existing != null && !IsStale(existing, lockPath) && IsResiduoDiQuestaPostazione(lockPath, existing, operatore);
        if (existing != null && !IsStale(existing, lockPath) && !residuoNostro)
        {
            // La riacquisizione idempotente è valida soltanto per la stessa sessione E lo stesso operatore.
            // AppState mantiene un LockService condiviso: dopo un cambio operatore non dobbiamo ereditare la
            // ownership logica di un editor aperto dalla sessione utente precedente.
            if (Owns(lockPath, existing) &&
                string.Equals(existing.Operatore, operatore, StringComparison.OrdinalIgnoreCase))
                return new LockResult { Acquisito = true, DetenutoDa = existing, SessioneGiaPosseduta = true };
            return new LockResult { Acquisito = false, DetenutoDa = existing };
        }

        if (existing != null)
        {
            if (TryDetachLock(lockPath,
                    snapshot => snapshot.Info is { } current &&
                               (IsStale(current, lockPath) || IsResiduoDiQuestaPostazione(lockPath, current, operatore)),
                    allowUnreadable: false,
                    out _) != EsitoDistacco.Rimosso)
            {
                // Il path può essere cambiato tra la prima lettura e il detach: non cancellare mai alla cieca.
                var winner = ReadLock(lockPath);
                if (winner != null && !IsStale(winner, lockPath) && !IsResiduoDiQuestaPostazione(lockPath, winner, operatore))
                    return new LockResult { Acquisito = false, DetenutoDa = winner };
                if (File.Exists(lockPath)) return new LockResult { Acquisito = false, DetenutoDa = winner };
            }
            _ownedSessions.TryRemove(lockPath, out _);
            _osservazioniLease.Rimuovi(lockPath);
        }

        var info = new LockInfo
        {
            Operatore = operatore,
            AcquisitoIl = DateTime.UtcNow,
            UltimoHeartbeat = DateTime.UtcNow,
            BaseContentHash = TryHashFile(targetPath),
            DeviceId = _deviceId.Value,
            SessionId = sessione ?? Guid.NewGuid().ToString("N")
        };
        if (!TryCreateExclusive(lockPath, info))
        {
            var winner = ReadLock(lockPath);
            return new LockResult { Acquisito = false, DetenutoDa = winner };
        }

        _ownedSessions[lockPath] = info.SessionId!;
        var seguitoDaForzatura = _forzatiInAttesaDiRiacquisizione.TryRemove(targetPath, out _);
        return new LockResult { Acquisito = true, DetenutoDa = info, SeguitoDaForzatura = seguitoDaForzatura };
    }

    private bool Owns(string lockPath, LockInfo info) =>
        !string.IsNullOrWhiteSpace(info.SessionId) &&
        _ownedSessions.TryGetValue(lockPath, out var owned) &&
        owned.Equals(info.SessionId, StringComparison.Ordinal);

    /// <summary>DeviceId stabile di questa istanza (lo stesso scritto in ogni lock che crea).</summary>
    public string? DeviceId => _deviceId.Value;

    /// <summary>Vero se il lock attualmente su disco è stato creato da QUESTA sessione (SessionId in
    /// _ownedSessions). È l'unica identità valida per "è mio": operatore e DeviceId possono coincidere fra
    /// postazioni diverse, o divergere per la stessa (fallback del DeviceId).</summary>
    public bool IsOwnedByThisSession(string targetPath)
    {
        var lockPath = LockPathFor(targetPath);
        var info = ReadLock(lockPath);
        return info != null && Owns(lockPath, info);
    }

    /// <summary>Stato del lock visto da questa postazione per un'altra sessione. <see cref="StatoLockAltrui.Libero"/>
    /// solo se il file è assente, scaduto, proprio o residuo di questa postazione. Timeout, errore di I/O o contenuto
    /// illeggibile e recente danno <see cref="StatoLockAltrui.Sconosciuto"/>: un errore non è mai un lock libero (P2.2).</summary>
    public LetturaLockAltrui GetForeignActiveLock(string targetPath, string? operatore = null)
    {
        var risultato = IoTimeoutGuard.EseguiConEsito(() => LeggiLockAltrui(targetPath, operatore), IoTimeoutPer(targetPath),
            "lettura lock altrui");
        return risultato.Esito == EsitoIo.Ok ? risultato.Valore! : new LetturaLockAltrui(StatoLockAltrui.Sconosciuto, null);
    }

    private LetturaLockAltrui LeggiLockAltrui(string targetPath, string? operatore)
    {
        var lockPath = LockPathFor(targetPath);
        var info = LeggiLock(lockPath, out var esito, out var impronta);
        switch (esito)
        {
            case EsitoLettura.Assente:
                _osservazioniLease.Rimuovi(lockPath);
                return new LetturaLockAltrui(StatoLockAltrui.Libero, null);
            case EsitoLettura.Illeggibile:
                return new LetturaLockAltrui(impronta != null && _osservazioniLease.EScaduto(lockPath, impronta, Timeout)
                    ? StatoLockAltrui.Libero
                    : StatoLockAltrui.Sconosciuto, null);
            case EsitoLettura.ErroreIo:
                return new LetturaLockAltrui(StatoLockAltrui.Sconosciuto, null);
        }
        if (IsStale(info!, lockPath) || Owns(lockPath, info!) || IsResiduoDiQuestaPostazione(lockPath, info!, operatore))
            return new LetturaLockAltrui(StatoLockAltrui.Libero, null);
        return new LetturaLockAltrui(StatoLockAltrui.Occupato, info);
    }

    /// <summary>
    /// Garantisce che questa sessione possieda il lock, senza mai sottrarlo a un detentore vivo. Se il lock è
    /// ancora nostro rinnova l'heartbeat (anche se nel frattempo risultava scaduto ma nessuno lo ha preso); se è
    /// sparito/scaduto lo riacquisisce (CreateNew atomico, come TryAcquire); se esiste un lock vivo di un'altra
    /// sessione NON lo tocca e ritorna il detentore in <see cref="LockEnsureResult.Holder"/>. Su timeout I/O
    /// ritorna Posseduto=false e Holder=null: nessun conflitto dimostrato, nessun avviso.
    /// </summary>
    public LockEnsureResult EnsureOwned(string targetPath, string operatore)
    {
        var risultato = IoTimeoutGuard.EseguiConEsito(() => EnsureOwnedInterno(targetPath, operatore), IoTimeoutPer(targetPath),
            "rinnovo lock");
        return risultato.Esito == EsitoIo.Ok ? risultato.Valore! : new LockEnsureResult(false, null, false);
    }

    /// <summary>Rinnova/riacquisisce il lock senza abbandonare il task al timeout. Va chiamato solo da un
    /// heartbeat in background mentre detiene <c>LockGate</c>: il gate deve restare occupato fino alla fine
    /// dell'I/O effettivo, così Dispose e una nuova acquisizione non possono superare una scrittura tardiva.</summary>
    internal Task<LockEnsureResult> EnsureOwnedTrackedAsync(string targetPath, string operatore) =>
        Task.Run(() => EnsureOwnedInterno(targetPath, operatore));

    private LockEnsureResult EnsureOwnedInterno(string targetPath, string operatore)
    {
        var lockPath = LockPathFor(targetPath);
        var existing = ReadLock(lockPath);
        // Un lock della NOSTRA sessione non è mai un conflitto, neppure dopo un cambio operatore (AppState
        // condivide il LockService): se l'operatore è cambiato l'operatore del lock viene aggiornato sotto lo
        // stesso handle esclusivo dell'heartbeat.
        if (existing != null && Owns(lockPath, existing))
        {
            // Ancora nostro (anche se scaduto): rinnovo. Un heartbeat non riuscito per I/O transitorio non toglie
            // ownership: il file su disco e' comunque quello con il nostro SessionId.
            var esito = HeartbeatCore(lockPath, operatore, adottaOperatore: true);
            if (esito != EsitoHeartbeat.NonPiuNostro)
                return new LockEnsureResult(true, null, false, Confermato: esito == EsitoHeartbeat.Rinnovato, SessionId: existing.SessionId);
        }

        var r = TryAcquireInterno(targetPath, operatore);
        if (r.Acquisito) return new LockEnsureResult(true, null, true, Confermato: true, SessionId: r.DetenutoDa?.SessionId);
        // Falliti senza holder vivo leggibile (file in scrittura/illeggibile): nessun conflitto dimostrato.
        return new LockEnsureResult(false, r.DetenutoDa is { } h && !IsStale(h, lockPath) ? h : null, false);
    }

    private static string? CalcolaDeviceId()
    {
        try { return DeviceIdentityService.GetOrCreate(); }
        catch { return Environment.MachineName; }
    }

    public static string? TryHashFile(string targetPath)
    {
        try
        {
            if (!File.Exists(targetPath)) return null;
            using var stream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch { return null; }
    }

    private static bool TryCreateExclusive(string lockPath, LockInfo info)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            using var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, info, JsonIo.Options);
            stream.Flush(flushToDisk: true);
            return true;
        }
        // 6.6.1 (correzione C): l'errore non viene propagato (il lock è semplicemente non acquisito), ma una radice persa va
        // comunque osservata dal monitor di sola lettura.
        catch (IOException ex) { SharedWriteLossMonitor.Osserva(lockPath, ex); return false; }
        catch (UnauthorizedAccessException ex) { SharedWriteLossMonitor.Osserva(lockPath, ex); return false; }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            File.Delete(path);
            return !File.Exists(path);
        }
        catch { return false; }
    }

    /// <summary>
    /// Rimuove il lock soltanto se il CONTENUTO aperto sotto lo stesso handle soddisfa il predicato. Invece di
    /// cancellare direttamente il path, lo sposta prima in un temp TestLoom (`target.json.tmpXXXXXXXX`): se una
    /// nuova sessione crea subito un nuovo `.lock`, la successiva Delete agisce sul temp vecchio e non sul
    /// nuovo lock. Se il processo cade tra Move e Delete, la normale housekeeping riconosce quel temp TestLoom e
    /// lo elimina dopo la retention di sicurezza.
    /// </summary>
    internal static EsitoDistacco TryDetachLock(
        string lockPath,
        Func<LockFileSnapshot, bool> shouldRemove,
        bool allowUnreadable,
        out LockInfo? removed)
    {
        removed = null;
        if (!File.Exists(lockPath)) return EsitoDistacco.NonNecessario;
        if (!lockPath.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return EsitoDistacco.NonNecessario;
        var targetPath = lockPath[..^5];
        var detachedPath = targetPath + ".tmp" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            using (var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Delete,
                       16 * 1024, FileOptions.SequentialScan))
            {
                var current = LeggiSnapshot(stream);
                if (current.Info == null && !allowUnreadable)
                {
                    DiagnosticTraceService.Registra("Lock.Distacco", $"path={lockPath}; errore=JSON illeggibile");
                    return EsitoDistacco.Errore;
                }
                if (!shouldRemove(current)) return EsitoDistacco.NonNecessario;

                // FILE_SHARE_DELETE consente il rename mentre questo handle resta aperto, ma impediamo letture e scritture
                // concorrenti. Il rename agisce però sul path: il file effettivamente spostato va riverificato qui sotto.
                File.Move(lockPath, detachedPath, overwrite: false);
            }
        }
        // Il file è sparito fra il controllo e l'apertura (un altro PC l'ha già rilasciato): nulla da fare.
        catch (FileNotFoundException) { return EsitoDistacco.NonNecessario; }
        catch (Exception ex)
        {
            // Errore di I/O (file bloccato, rete): lo stato del lock resta incerto. Il chiamante non cancella nulla.
            DiagnosticTraceService.Registra("Lock.Distacco", $"path={lockPath}; errore={ex.GetType().Name}: {ex.Message}");
            return EsitoDistacco.Errore;
        }

        // P2.9: da qui il lock è fuori dal suo path. Si cancella soltanto se il file spostato soddisfa ancora il predicato;
        // altrimenti un altro PC lo ha sostituito fra verifica e spostamento e va ripristinato al path originale.
        LockInfo? spostato = null;
        var combacia = false;
        var verificaRiuscita = false;
        try
        {
            TestHookPrimaDiVerificaSpostato?.Invoke(targetPath, detachedPath);
            using var copia = new FileStream(detachedPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
            var snapshotSpostato = LeggiSnapshot(copia);
            spostato = snapshotSpostato.Info;
            if (snapshotSpostato.Info == null && !allowUnreadable)
            {
                DiagnosticTraceService.Registra("Lock.Distacco", $"path={lockPath}; verifica-spostato=JSON illeggibile");
            }
            else
            {
                verificaRiuscita = true;
                combacia = shouldRemove(snapshotSpostato);
            }
        }
        catch (Exception ex)
        {
            DiagnosticTraceService.Registra("Lock.Distacco", $"path={lockPath}; verifica-spostato={ex.GetType().Name}: {ex.Message}");
        }

        if (!verificaRiuscita)
        {
            RipristinaLock(detachedPath, lockPath);
            return EsitoDistacco.Errore;
        }

        if (!combacia) return RipristinaLock(detachedPath, lockPath);

        // Il file spostato è quello verificato: si cancella solo lui. Il nome originale è ora libero per un lock nuovo.
        // Se la cancellazione fallisce il file ha il pattern atomico TestLoom e verrà raccolto dalla manutenzione.
        TryDelete(detachedPath);
        removed = spostato;
        return EsitoDistacco.Rimosso;
    }

    /// <summary>Riporta al path del lock il file spostato per errore. Se nel frattempo un'altra sessione ha creato un lock
    /// su quel path, il file resta nel temp TestLoom (la manutenzione lo raccoglie) e il caso viene tracciato.</summary>
    private static EsitoDistacco RipristinaLock(string detachedPath, string lockPath)
    {
        for (var tentativo = 1; tentativo <= 3; tentativo++)
        {
            try
            {
                File.Move(detachedPath, lockPath, overwrite: false);
                return EsitoDistacco.NonNecessario;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(lockPath) || tentativo == 3)
                {
                    DiagnosticTraceService.Registra("Lock.DistaccoNonRipristinato",
                        $"path={lockPath}; spostato={Path.GetFileName(detachedPath)}; errore={ex.GetType().Name}: {ex.Message}");
                    return EsitoDistacco.Errore;
                }
                Thread.Sleep(AttesaTraTentativi);
            }
        }
        return EsitoDistacco.Errore;
    }

    private static LockFileSnapshot LeggiSnapshot(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var impronta = Convert.ToHexString(SHA256.HashData(bytes));
        try
        {
            var info = JsonSerializer.Deserialize<LockInfo>(bytes, JsonIo.Options);
            if (info != null) info.ImprontaFile = impronta;
            return new LockFileSnapshot(info, impronta);
        }
        catch (JsonException) { return new LockFileSnapshot(null, impronta); }
    }

    private enum EsitoHeartbeat { Rinnovato, Occupato, NonPiuNostro }

    /// <summary>
    /// Heartbeat in-place sotto handle unico. Il file verificato e il file aggiornato sono lo STESSO handle,
    /// quindi una vecchia sessione non può sostituire il path dopo che un'altra lo ha riacquisito. L'handle
    /// consente letture concorrenti (FileShare.Read: i lettori usano ReadWrite|Delete) e su IOException
    /// transitoria (client OneDrive, antivirus) ritenta poche volte a breve distanza prima di rinunciare.
    /// Non lancia mai.
    /// </summary>
    public EsitoIo Heartbeat(string targetPath, string operatore)
    {
        var lockPath = LockPathFor(targetPath);
        var risultato = IoTimeoutGuard.EseguiConEsito(() => HeartbeatCore(lockPath, operatore, adottaOperatore: false),
            IoTimeoutPer(targetPath), "heartbeat lock");
        if (risultato.Esito != EsitoIo.Ok) return risultato.Esito;
        if (risultato.Valore == EsitoHeartbeat.Rinnovato) return EsitoIo.Ok;
        DiagnosticTraceService.Registra("Lock.Heartbeat", $"path={lockPath}; esito={risultato.Valore}");
        return EsitoIo.Errore;
    }

    private EsitoHeartbeat HeartbeatCore(string lockPath, string operatore, bool adottaOperatore)
    {
        for (var tentativo = 0; tentativo < TentativiHeartbeat; tentativo++)
        {
            try
            {
                if (!File.Exists(lockPath))
                {
                    _ownedSessions.TryRemove(lockPath, out _);
                    _osservazioniLease.Rimuovi(lockPath);
                    return EsitoHeartbeat.NonPiuNostro;
                }

                using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                    16 * 1024, FileOptions.WriteThrough);
                var existing = JsonSerializer.Deserialize<LockInfo>(stream, JsonIo.Options);
                if (existing == null)
                    return EsitoHeartbeat.Occupato;
                if (!Owns(lockPath, existing) ||
                    (!adottaOperatore && !string.Equals(existing.Operatore, operatore, StringComparison.OrdinalIgnoreCase)))
                {
                    _ownedSessions.TryRemove(lockPath, out _);
                    _osservazioniLease.Rimuovi(lockPath);
                    return EsitoHeartbeat.NonPiuNostro;
                }

                if (adottaOperatore) existing.Operatore = operatore;
                existing.UltimoHeartbeat = DateTime.UtcNow;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(existing, JsonIo.Options);
                stream.Position = 0;
                stream.Write(bytes, 0, bytes.Length);
                stream.SetLength(bytes.Length); // scrittura prima del troncamento: un lettore non vede mai un file vuoto
                stream.Flush(flushToDisk: true);
                return EsitoHeartbeat.Rinnovato;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
            catch { return EsitoHeartbeat.Occupato; }

            if (tentativo < TentativiHeartbeat - 1) Thread.Sleep(AttesaTraTentativi);
        }
        // File temporaneamente occupato/sincronizzato: un heartbeat saltato non distrugge ownership.
        return EsitoHeartbeat.Occupato;
    }

    /// <summary>Release normale = solo il proprio token, verificato e staccato dallo stesso file.</summary>
    public EsitoIo Release(string targetPath)
    {
        var lockPath = LockPathFor(targetPath);
        var risultato = IoTimeoutGuard.EseguiConEsito(() => TryDetachLock(lockPath,
                snapshot => snapshot.Info is { } current && Owns(lockPath, current),
                allowUnreadable: false,
                out _),
            IoTimeoutPer(targetPath), "rilascio lock");
        return EsitoRilascio(risultato, () =>
        {
            _ownedSessions.TryRemove(lockPath, out _);
            _osservazioniLease.Rimuovi(lockPath);
        });
    }

    /// <summary>Se il distacco è andato a buon fine (o il lock non c'era più) la proprietà in memoria si toglie. Su
    /// errore o timeout il lock può essere ancora su disco con il nostro SessionId: la proprietà resta, così heartbeat
    /// e un nuovo rilascio possono riprovare, invece di dare per fatto un rilascio mai avvenuto (P2.2).</summary>
    private static EsitoIo EsitoRilascio(RisultatoIo<EsitoDistacco> risultato, Action rimuoviProprieta)
    {
        if (risultato.Esito == EsitoIo.Ok && risultato.Valore != EsitoDistacco.Errore)
        {
            rimuoviProprieta();
            return EsitoIo.Ok;
        }
        return risultato.Esito == EsitoIo.Ok ? EsitoIo.Errore : risultato.Esito;
    }

    /// <summary>Rilascio legato a una specifica acquisizione: stacca il lock solo se il file porta ancora il
    /// SessionId indicato. Serve a un'acquisizione tardiva di un editor già chiuso, che non deve staccare un lock
    /// creato nel frattempo da un'altra apertura, anche con lo stesso operatore (dove Release basato su
    /// _ownedSessions accetterebbe il SessionId più recente).</summary>
    public EsitoIo ReleaseIfSession(string targetPath, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return EsitoIo.Ok;
        var lockPath = LockPathFor(targetPath);
        var risultato = IoTimeoutGuard.EseguiConEsito(() => TryDetachLock(lockPath,
                snapshot => snapshot.Info?.SessionId == sessionId,
                allowUnreadable: false,
                out _),
            IoTimeoutPer(targetPath), "rilascio di sessione");
        return EsitoRilascio(risultato, () =>
        {
            _ownedSessions.TryRemove(new KeyValuePair<string, string>(lockPath, sessionId));
            _osservazioniLease.Rimuovi(lockPath);
        });
    }

    /// <summary>Rilascia una sessione exact-token attendendo l'I/O effettivo, senza il timeout UI.
    /// Il coordinatore LockGate lo esegue in background e mantiene occupato il gate del percorso fino
    /// al completamento, evitando che una scrittura di rilascio tardiva superi una nuova acquisizione.</summary>
    internal EsitoIo ReleaseIfSessionTracked(string targetPath, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return EsitoIo.Ok;
        var lockPath = LockPathFor(targetPath);
        var distacco = TryDetachLock(lockPath,
            snapshot => snapshot.Info?.SessionId == sessionId,
            allowUnreadable: false,
            out _);
        if (distacco == EsitoDistacco.Errore)
        {
            // Il coordinatore conserva comunque il token esatto per i retry. Togliere la proprietà locale
            // permette a una nuova apertura dello stesso operatore su questo PC di sostituire il lease
            // rimasto sul disco con una nuova SessionId: un retry tardivo del vecchio editor non potrà
            // così riutilizzare la stessa identità e staccare il lock del nuovo editor.
            _ownedSessions.TryRemove(new KeyValuePair<string, string>(lockPath, sessionId));
            return EsitoIo.Errore;
        }

        _ownedSessions.TryRemove(new KeyValuePair<string, string>(lockPath, sessionId));
        _osservazioniLease.Rimuovi(lockPath);
        return EsitoIo.Ok;
    }

    /// <summary>
    /// Unica rimozione intenzionalmente non vincolata al token locale. Anche qui il detach cattura esattamente
    /// il file rimosso, così l'audit non può descrivere un holder e cancellarne accidentalmente uno successivo.
    /// I controlli revision/hash restano attivi al successivo Save.
    /// </summary>
    public EsitoIo ForceUnlock(string targetPath)
    {
        var lockPath = LockPathFor(targetPath);
        var risultato = IoTimeoutGuard.EseguiConEsito(() =>
            {
                var distacco = TryDetachLock(lockPath, _ => true, allowUnreadable: true, out var precedente);
                return (distacco, precedente);
            },
            IoTimeoutPer(targetPath), "sblocco forzato");
        if (risultato.Esito != EsitoIo.Ok) return risultato.Esito;
        var (esito, previous) = risultato.Valore;
        if (esito == EsitoDistacco.Errore) return EsitoIo.Errore;
        if (esito == EsitoDistacco.Rimosso)
        {
            _ownedSessions.TryRemove(lockPath, out _);
            _osservazioniLease.Rimuovi(lockPath);
            _forzatiInAttesaDiRiacquisizione[targetPath] = 0;
            try { ForceUnlocked?.Invoke(targetPath, previous); } catch { }
        }
        else if (esito == EsitoDistacco.NonNecessario && !File.Exists(lockPath))
            _osservazioniLease.Rimuovi(lockPath);
        return EsitoIo.Ok;
    }

    /// <summary>
    /// Lettura pura del lock attivo. Un lock scaduto/orfano viene trattato come non attivo ma non cancellato:
    /// la rimozione spetta a TryAcquire/CleanupStaleLocks, cioè a percorsi esplicitamente mutanti.
    /// </summary>
    public LockInfo? GetActiveLock(string targetPath) => IoTimeoutGuard.Esegui<LockInfo?>(() =>
    {
        var lockPath = LockPathFor(targetPath);
        var info = LeggiLock(lockPath, out var esito, out _);
        if (esito == EsitoLettura.Assente) _osservazioniLease.Rimuovi(lockPath);
        return info != null && !IsStale(info, lockPath) ? info : null;
    }, IoTimeoutPer(targetPath));

    /// <summary>Ispezione statica pura: non esegue cleanup come side-effect della lettura.</summary>
    public static LockInfo? TryReadLockInfo(string targetPath)
    {
        try
        {
            var path = LockPathFor(targetPath);
            if (!File.Exists(path))
            {
                OsservazioniCondivise.Rimuovi(path);
                return null;
            }
            var info = LeggiLock(path, out var esito, out _);
            if (esito == EsitoLettura.Assente) OsservazioniCondivise.Rimuovi(path);
            return info != null && !IsStale(info, path, OsservazioniCondivise) ? info : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Rimuove esclusivamente lock scaduti/orfani che corrispondono a target JSON riconosciuti come TestLoom.
    /// La root SharePoint può contenere file di altri applicativi: un generico `qualcosa.lock` non viene mai
    /// toccato, anche se vecchio o illeggibile. La manutenzione è mutante e quindi non parte se il workspace
    /// è sola lettura/incompatibile o presenta una transaction straniera.
    /// </summary>
    public LockCleanupResult CleanupStaleLocks(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return new(0, 0, 0);
        if (!WorkspaceWritePolicy.CanWrite(new DataPaths(root))) return new(0, 0, 0);
        var stale = 0;
        var unreadable = 0;
        var errors = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                MatchCasing = MatchCasing.CaseInsensitive
            };
            foreach (var path in Directory.EnumerateFiles(root, "*.lock", options))
            {
                if (!TestLoomStorageOwnership.IsManagedLockPath(root, path)) continue;
                try
                {
                    var info = ReadLock(path);
                    if (info == null && !File.Exists(path)) _osservazioniLease.Rimuovi(path);
                    if (info != null)
                    {
                        if (IsStale(info, path) && TryDetachLock(path,
                                snapshot => snapshot.Info != null && IsStale(snapshot.Info, path),
                                allowUnreadable: false,
                                out _) == EsitoDistacco.Rimosso)
                        {
                            _ownedSessions.TryRemove(path, out _);
                            _osservazioniLease.Rimuovi(path);
                            stale++;
                        }
                        continue;
                    }
                    if (IsUnreadableOrphan(path) && TryDetachLock(path,
                            snapshot => snapshot.Info == null && _osservazioniLease.EScaduto(path, snapshot.Impronta, Timeout),
                            allowUnreadable: true,
                            out _) == EsitoDistacco.Rimosso)
                    {
                        _ownedSessions.TryRemove(path, out _);
                        _osservazioniLease.Rimuovi(path);
                        unreadable++;
                    }
                }
                catch { errors++; }
            }
        }
        catch { errors++; }
        return new(stale, unreadable, errors);
    }

    /// <summary>La scadenza richiede che lo stesso contenuto sia stato osservato localmente per l'intero timeout.
    /// Né gli orologi client né mtime cloud/SMB sono fonti affidabili di età condivisa (P2.9).</summary>
    private bool IsStale(LockInfo info, string lockPath) => IsStale(info, lockPath, _osservazioniLease);

    private static bool IsStale(LockInfo info, string lockPath, LockLeaseObservationTracker observations) =>
        info.ImprontaFile != null && observations.EScaduto(lockPath, info.ImprontaFile, Timeout);

    /// <summary>Gli orari salvati in UTC tornano all'ora locale per chi li mostra (banner, messaggi).</summary>
    private static DateTime InLocale(DateTime d) => d.Kind == DateTimeKind.Utc && d != default ? d.ToLocalTime() : d;

    /// <summary>Un JSON corrotto diventa orfano solo dopo essere rimasto identico per l'intero timeout locale.</summary>
    private bool IsUnreadableOrphan(string lockPath)
    {
        try
        {
            _ = LeggiLock(lockPath, out var esito, out var impronta);
            if (esito == EsitoLettura.Assente)
            {
                _osservazioniLease.Rimuovi(lockPath);
                return false;
            }
            return esito == EsitoLettura.Illeggibile && impronta != null &&
                   _osservazioniLease.EScaduto(lockPath, impronta, Timeout);
        }
        catch { return false; }
    }

    /// <summary>Esito della lettura di un file lock: distingue il contenuto dall'errore di I/O.</summary>
    private enum EsitoLettura { Assente, Leggibile, Illeggibile, ErroreIo }

    /// <summary>Legge il lock distinguendo file assente, contenuto illeggibile ed errore di I/O (file bloccato, share non
    /// raggiungibile). Un errore non è un file assente: chi decide se il lock è libero deve poterlo sapere (P2.2).</summary>
    private static LockInfo? LeggiLock(string lockPath, out EsitoLettura esito, out string? impronta)
    {
        impronta = null;
        try
        {
            using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
            var snapshot = LeggiSnapshot(stream);
            impronta = snapshot.Impronta;
            var info = snapshot.Info;
            esito = info == null ? EsitoLettura.Illeggibile : EsitoLettura.Leggibile;
            if (info != null)
            {
                info.AcquisitoIl = InLocale(info.AcquisitoIl);
                info.UltimoHeartbeat = InLocale(info.UltimoHeartbeat);
            }
            return info;
        }
        catch (FileNotFoundException) { esito = EsitoLettura.Assente; return null; }
        catch (JsonException) { esito = EsitoLettura.Illeggibile; return null; }
        catch { esito = EsitoLettura.ErroreIo; return null; }
    }

    private static LockInfo? ReadLock(string lockPath) => LeggiLock(lockPath, out _, out _);

}
