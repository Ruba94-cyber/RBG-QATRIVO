namespace TestLoom.App.Services;

/// <summary>Stato di una postazione nella cartella condivisa: un solo file per postazione, scritto solo da quella postazione.</summary>
public sealed class SyncHandshakeEntry
{
    public int SchemaVersion { get; set; } = 1;
    public string DeviceId { get; set; } = "";
    public string Operatore { get; set; } = "";
    /// <summary>Numero crescente: ogni impulso della postazione lo incrementa.</summary>
    public long Seq { get; set; }
    public DateTime WrittenUtc { get; set; }
    /// <summary>Ultima sequenza vista da questa postazione per ciascun'altra postazione (la conferma di ricezione).</summary>
    public Dictionary<string, long> Seen { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum StatoHandshake
{
    /// <summary>Nessun'altra postazione attiva: si può provare solo lo stato locale dei file.</summary>
    NessunaPostazione,
    /// <summary>Tutte le postazioni attive hanno confermato di aver ricevuto l'ultimo impulso di questa postazione.</summary>
    Confermata,
    /// <summary>Almeno una postazione attiva non ha ancora confermato.</summary>
    InAttesa,
}

public sealed record HandshakePostazione(string DeviceId, string Operatore, DateTime UltimaScritturaUtc, bool Attiva,
    bool HaConfermatoMiaSequenza, bool HoRicevutoSuaSequenza);

public sealed record HandshakeReport(StatoHandshake Stato, long MiaSequenza, IReadOnlyList<HandshakePostazione> Postazioni)
{
    public int Attive => Postazioni.Count(p => p.Attiva);
    public int Confermate => Postazioni.Count(p => p.Attiva && p.HaConfermatoMiaSequenza);
    public int InAttesa => Postazioni.Count(p => p.Attiva && !p.HaConfermatoMiaSequenza);
}

/// <summary>
/// Prova a due vie che la sincronizzazione della cartella condivisa funziona davvero, indipendente dagli attributi interni di
/// OneDrive: ogni postazione scrive SOLO il proprio file (mai conflitti tra postazioni) con una sequenza crescente e, per ogni
/// altra postazione, l'ultima sequenza che ha letto. Se B riporta di aver visto la sequenza N di A, la scrittura di A è arrivata
/// a B tramite il cloud, e viceversa. Non prova nulla sulle postazioni spente: in quel caso si dichiara solo ciò che è provato.
/// </summary>
public sealed class SyncHandshakeService
{
    public static readonly TimeSpan FinestraAttivaPredefinita = TimeSpan.FromMinutes(10);

    private readonly string _dir;
    private readonly string _deviceId;
    private readonly string _operatore;
    private readonly Func<DateTime> _oraUtc;
    private readonly TimeSpan _finestraAttiva;
    private readonly object _gate = new();
    private long _ultimaSeq;

    public SyncHandshakeService(string radice, string deviceId, string operatore, Func<DateTime>? oraUtc = null,
        TimeSpan? finestraAttiva = null)
    {
        _dir = Path.Combine(radice, "_ditm", "sync-handshake");
        _deviceId = deviceId;
        _operatore = operatore;
        _oraUtc = oraUtc ?? (() => DateTime.UtcNow);
        _finestraAttiva = finestraAttiva ?? FinestraAttivaPredefinita;
    }

    public long UltimaSequenza { get { lock (_gate) return _ultimaSeq; } }

    private string PercorsoMio => Path.Combine(_dir, Sicuro(_deviceId) + ".json");

    private static string Sicuro(string id) => string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    /// <summary>Scrive il proprio impulso: incrementa la sequenza e registra l'ultima sequenza letta da ogni altra postazione.
    /// La sequenza prova la propria scrittura; le conferme degli altri la provano ricevuta.</summary>
    public long Impulso() => Scrivi(incrementa: true);

    private long Scrivi(bool incrementa)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_dir);
            var esistente = LeggiMio();
            // Una risposta (solo conferme) NON incrementa la sequenza: altrimenti ogni conferma ne richiederebbe un'altra all'infinito.
            var seq = Math.Max(_ultimaSeq, esistente?.Seq ?? 0) + (incrementa ? 1 : 0);
            var entry = new SyncHandshakeEntry
            {
                DeviceId = _deviceId,
                Operatore = _operatore,
                Seq = seq,
                WrittenUtc = _oraUtc(),
            };
            foreach (var altro in LeggiAltri()) entry.Seen[altro.DeviceId] = altro.Seq;
            JsonIo.WriteAtomic(PercorsoMio, entry);
            _ultimaSeq = seq;
            return seq;
        }
    }

    /// <summary>
    /// Risponde a chi ha scritto dopo l'ultimo impulso: se un'altra postazione ha una sequenza che non abbiamo ancora confermato,
    /// scrive un nuovo impulso (con la conferma). Economico: legge pochi file piccoli. Restituisce true se ha scritto.
    /// </summary>
    public bool RispondiSeNecessario()
    {
        lock (_gate)
        {
            var mio = LeggiMio();
            var nuovo = LeggiAltri().Any(a => mio == null || !mio.Seen.TryGetValue(a.DeviceId, out var visto) || a.Seq > visto);
            if (!nuovo) return false;
        }
        Scrivi(incrementa: false);
        return true;
    }

    /// <summary>Valuta lo stato rispetto all'ultimo impulso di questa postazione.</summary>
    public HandshakeReport Valuta()
    {
        lock (_gate)
        {
            var mio = LeggiMio();
            var miaSeq = Math.Max(_ultimaSeq, mio?.Seq ?? 0);
            var ora = _oraUtc();
            var postazioni = LeggiAltri()
                .Select(a => new HandshakePostazione(
                    a.DeviceId, a.Operatore, a.WrittenUtc,
                    Attiva: ora - a.WrittenUtc <= _finestraAttiva,
                    HaConfermatoMiaSequenza: miaSeq > 0 && a.Seen.TryGetValue(_deviceId, out var visto) && visto >= miaSeq,
                    HoRicevutoSuaSequenza: mio != null && mio.Seen.TryGetValue(a.DeviceId, out var io) && io >= a.Seq))
                .OrderBy(p => p.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var attive = postazioni.Where(p => p.Attiva).ToList();
            var stato = attive.Count == 0 ? StatoHandshake.NessunaPostazione
                : attive.All(p => p.HaConfermatoMiaSequenza) ? StatoHandshake.Confermata
                : StatoHandshake.InAttesa;
            return new HandshakeReport(stato, miaSeq, postazioni);
        }
    }

    /// <summary>Impulso + attesa (con tetto) della conferma delle postazioni attive. Usato all'uscita.</summary>
    public async Task<HandshakeReport> ImpulsoEAttendiAsync(TimeSpan timeout, TimeSpan intervallo, Func<DateTime>? oraUtc = null,
        Func<TimeSpan, CancellationToken, Task>? attesa = null, CancellationToken ct = default)
    {
        Impulso();
        var ora = oraUtc ?? _oraUtc;
        var attendi = attesa ?? Task.Delay;
        var scadenza = ora() + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var report = Valuta();
            if (report.Stato != StatoHandshake.InAttesa || ora() >= scadenza) return report;
            await attendi(intervallo, ct).ConfigureAwait(false);
        }
    }

    private SyncHandshakeEntry? LeggiMio() =>
        File.Exists(PercorsoMio) ? JsonIo.TryRead<SyncHandshakeEntry>(PercorsoMio, out _) : null;

    private List<SyncHandshakeEntry> LeggiAltri()
    {
        var risultato = new List<SyncHandshakeEntry>();
        if (!Directory.Exists(_dir)) return risultato;
        var mio = Path.GetFileName(PercorsoMio);
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            if (string.Equals(Path.GetFileName(file), mio, StringComparison.OrdinalIgnoreCase)) continue;
            // Un file non ancora scaricato, troncato o di un'altra versione è semplicemente "non ancora visto": mai un errore.
            var entry = JsonIo.TryRead<SyncHandshakeEntry>(file, out _);
            if (entry != null && !string.IsNullOrWhiteSpace(entry.DeviceId) && !string.Equals(entry.DeviceId, _deviceId, StringComparison.OrdinalIgnoreCase))
                risultato.Add(entry);
        }
        // Una copia di conflitto OneDrive dello stesso file porta lo stesso DeviceId: vale la sequenza più alta.
        return risultato.GroupBy(e => e.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.Seq).First()).ToList();
    }
}
