namespace TestLoom.App.Services;

/// <summary>
/// Collega <see cref="SyncHandshakeService"/> all'app: avvio dopo la verifica OneDrive, risposta alle altre postazioni ogni
/// 15 secondi, impulso periodico ogni 2 minuti, e impulso con attesa breve all'uscita. Non scrive mai quando il workspace è
/// in sola lettura (viewer locale o blocco OneDrive) e non lancia mai eccezioni verso il chiamante.
/// </summary>
public static class SyncHandshake
{
    private static readonly object Gate = new();
    private static SyncHandshakeService? _servizio;
    private static Timer? _timer;
    private static Func<bool> _puoScrivere = () => false;
    private static int _tick;

    public static HandshakeReport? UltimoReport { get; private set; }
    public static event Action? ReportAggiornato;

    public static void Avvia(string radice, string etichettaPostazione, Func<bool> puoScrivere)
    {
        try
        {
            lock (Gate)
            {
                _timer?.Dispose();
                _servizio = new SyncHandshakeService(radice, DeviceIdentityService.GetOrCreate(), etichettaPostazione);
                _puoScrivere = puoScrivere;
                _tick = 0;
                _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
            }
        }
        catch (Exception ex) { CrashLogService.Registra("Handshake sincronizzazione: avvio", ex); }
    }

    public static void Ferma()
    {
        lock (Gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private static void Tick()
    {
        try
        {
            SyncHandshakeService? servizio;
            bool scrive;
            int tick;
            lock (Gate) { servizio = _servizio; scrive = _puoScrivere(); tick = _tick++; }
            if (servizio == null) return;
            if (scrive)
            {
                if (tick % 8 == 0) servizio.Impulso();      // ogni ~2 minuti (e subito all'avvio)
                else servizio.RispondiSeNecessario();
            }
            UltimoReport = servizio.Valuta();
            ReportAggiornato?.Invoke();
        }
        catch (Exception ex) { CrashLogService.Registra("Handshake sincronizzazione: ciclo", ex); }
    }

    /// <summary>
    /// Uscita: scrive un impulso e attende (max <paramref name="timeout"/>) che le postazioni attive lo confermino. Restituisce
    /// null se l'handshake non è attivo o il workspace è in sola lettura. Non lancia mai eccezioni.
    /// </summary>
    public static async Task<HandshakeReport?> UscitaAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        SyncHandshakeService? servizio;
        lock (Gate) { servizio = _servizio; if (!_puoScrivere()) return null; }
        if (servizio == null) return null;
        try
        {
            var report = await Task.Run(() => servizio.ImpulsoEAttendiAsync(timeout, TimeSpan.FromSeconds(2), ct: ct), CancellationToken.None)
                .ConfigureAwait(false);
            UltimoReport = report;
            return report;
        }
        catch (OperationCanceledException) { return servizio.Valuta(); }
        catch (Exception ex)
        {
            CrashLogService.Registra("Handshake sincronizzazione: uscita", ex);
            return null;
        }
    }

    /// <summary>Testo per lo stato sincronizzazione; null se non ancora disponibile (si mostra lo stato locale standard).</summary>
    public static string? TestoStato(HandshakeReport? report)
    {
        if (report == null) return null;
        var t = LocalizationService.Instance;
        return report.Stato switch
        {
            StatoHandshake.Confermata => string.Format(t.Traduci("HandshakeConfermataFmt"), report.Confermate),
            StatoHandshake.InAttesa => string.Format(t.Traduci("HandshakeInAttesaFmt"), report.InAttesa, report.Attive),
            _ => t.Traduci("HandshakeNessunaPostazione"),
        };
    }
}
