namespace TestLoom.App.Services;

/// <summary>
/// Annota nello store locale (fuori da OneDrive) i file scritti da TestLoom sotto la cartella condivisa, subito dopo il salvataggio.
/// Senza questo registro i file non ancora caricati sono noti solo se la chiusura è pulita: dopo un crash o uno spegnimento
/// un file scritto ma mai caricato risulterebbe "allineato" per via dell'età. All'avvio successivo i percorsi registrati
/// vengono verificati come sospetti da <see cref="OneDriveSyncService"/>; la chiusura completata cancella lo store.
/// </summary>
public sealed class RegistroScrittureOneDrive : IDisposable
{
    private readonly string _radice;
    private readonly IOneDrivePendenteStore _store;
    private readonly int _massimo;
    private readonly TimeSpan _ritardo;
    private readonly Func<DateTime> _oraUtc;
    private readonly object _gate = new();
    private readonly List<string> _nuovi = new();
    private Timer? _timer;
    private bool _eliminato;
    private int _scritture;

    /// <summary>Scritture di dati rilevanti fatte da TestLoom in questa sessione (esclusi lock e file di pianificazione automatica).</summary>
    public int ScrittureRilevanti => Volatile.Read(ref _scritture);

    public RegistroScrittureOneDrive(string radice, IOneDrivePendenteStore store, int massimoPercorsi = 200,
        TimeSpan? ritardoScrittura = null, Func<DateTime>? oraUtc = null)
    {
        _radice = Path.GetFullPath(radice);
        _store = store;
        _massimo = Math.Max(1, massimoPercorsi);
        _ritardo = ritardoScrittura ?? TimeSpan.FromSeconds(3);
        _oraUtc = oraUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>Chiamato dopo ogni scrittura riuscita. Non lancia eccezioni e non blocca il chiamante.</summary>
    public void Registra(string percorso)
    {
        try
        {
            var relativo = Path.GetRelativePath(_radice, Path.GetFullPath(percorso));
            if (relativo.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativo)) return;
            var nome = Path.GetFileName(relativo);
            if (nome.StartsWith("~$", StringComparison.Ordinal) ||
                nome.StartsWith(".testloom-write-probe", StringComparison.OrdinalIgnoreCase) ||
                nome.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                OneDriveSyncService.EraTemporaneoDiScrittura(nome)) return;
            if (!nome.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) &&
                !nome.Equals("sync-schedule.json", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref _scritture);
            lock (_gate)
            {
                if (_eliminato) return;
                _nuovi.Add(relativo);
                _timer ??= new Timer(_ => Svuota(), null, _ritardo, Timeout.InfiniteTimeSpan);
                _timer.Change(_ritardo, Timeout.InfiniteTimeSpan);
            }
        }
        catch
        {
            // Il registro è un aiuto diagnostico: non deve mai far fallire un salvataggio.
        }
    }

    /// <summary>Scrive subito su disco i percorsi accumulati, unendoli a quelli già registrati.</summary>
    public void Svuota()
    {
        string[] daScrivere;
        lock (_gate)
        {
            if (_nuovi.Count == 0) return;
            daScrivere = _nuovi.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _nuovi.Clear();
        }
        try
        {
            var esistente = _store.Leggi(_radice);
            var tutti = (esistente?.Percorsi ?? new List<string>())
                .Concat(daScrivere)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var numero = tutti.Count;
            if (tutti.Count > _massimo) tutti = tutti.Skip(tutti.Count - _massimo).ToList();
            _store.Salva(_radice, new OneDrivePendente { RegistratoIlUtc = _oraUtc(), NumeroFile = Math.Max(numero, tutti.Count), Percorsi = tutti });
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Registro scritture OneDrive", ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _eliminato = true;
            _timer?.Dispose();
            _timer = null;
        }
        Svuota();
    }
}
