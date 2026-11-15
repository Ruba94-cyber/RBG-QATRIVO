namespace TestLoom.App.Services;

public enum EsitoMonitorOneDrive
{
    /// <summary>Client attivo e nessun caricamento fermo.</summary>
    Ok,
    /// <summary>Il client OneDrive non è in esecuzione da oltre la soglia.</summary>
    ClientNonAttivo,
    /// <summary>Uno o più file sono "in attesa di caricamento" da oltre la soglia: il client non sta sincronizzando (pausa, errore, rete).</summary>
    CaricamentoFermo,
}

public sealed class OneDriveMonitorOptions
{
    /// <summary>Quanto deve restare assente il client prima di bloccare (evita falsi allarmi per un riavvio del client).</summary>
    public TimeSpan SogliaClientAssente { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Quanto può restare un file in attesa di caricamento prima di considerare la sincronizzazione ferma.</summary>
    public TimeSpan SogliaCaricamentoFermo { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed record RisultatoMonitorOneDrive(EsitoMonitorOneDrive Esito, int FileFermi)
{
    public bool Sicuro => Esito == EsitoMonitorOneDrive.Ok;
}

/// <summary>
/// Verifica CONTINUA (durante la sessione) che OneDrive sia presente e stia sincronizzando. Senza, i file scritti localmente non
/// raggiungono gli altri PC e si rischia di lavorare su dati non allineati: il chiamante porta il workspace in sola lettura.
/// Tiene in memoria da quando ciascun file è in attesa di caricamento: un file che resta in attesa oltre la soglia indica un
/// client in pausa, scollegato o in errore, cosa che il solo controllo "processo in esecuzione" non vede.
/// Gli allegati solo online e i file temporanei non contano (stesso filtro della verifica di avvio/chiusura).
/// </summary>
public sealed class OneDriveMonitorContinuo
{
    private readonly IOneDriveAccesso _accesso;
    private readonly OneDriveMonitorOptions _opzioni;
    private readonly Func<DateTime> _oraUtc;
    private readonly Dictionary<string, DateTime> _inAttesaDal = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _clientAssenteDal;

    public OneDriveMonitorContinuo(IOneDriveAccesso accesso, OneDriveMonitorOptions? opzioni = null, Func<DateTime>? oraUtc = null)
    {
        _accesso = accesso;
        _opzioni = opzioni ?? new OneDriveMonitorOptions();
        _oraUtc = oraUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>Un giro di controllo. Può toccare il disco: chiamare fuori dal thread UI. Non lancia: un errore di lettura non blocca.</summary>
    public RisultatoMonitorOneDrive Controlla(string radice, CancellationToken ct = default)
    {
        var ora = _oraUtc();
        try
        {
            if (!_accesso.ClientInEsecuzione())
            {
                _clientAssenteDal ??= ora;
                if (ora - _clientAssenteDal.Value >= _opzioni.SogliaClientAssente)
                    return new RisultatoMonitorOneDrive(EsitoMonitorOneDrive.ClientNonAttivo, 0);
                return new RisultatoMonitorOneDrive(EsitoMonitorOneDrive.Ok, 0);
            }
            _clientAssenteDal = null;

            var scansione = _accesso.Scansiona(radice, Array.Empty<string>(), ct);
            var inAttesa = scansione.DaAllineare
                .Where(f => f.Stato == StatoFileCloud.InAttesaCaricamento && OneDriveSyncService.Rilevante(radice, f, avvio: false))
                .Select(f => f.Percorso)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var chiave in _inAttesaDal.Keys.Where(k => !inAttesa.Contains(k)).ToList()) _inAttesaDal.Remove(chiave);
            foreach (var percorso in inAttesa) _inAttesaDal.TryAdd(percorso, ora);

            var fermi = _inAttesaDal.Count(kv => ora - kv.Value >= _opzioni.SogliaCaricamentoFermo);
            return fermi > 0
                ? new RisultatoMonitorOneDrive(EsitoMonitorOneDrive.CaricamentoFermo, fermi)
                : new RisultatoMonitorOneDrive(EsitoMonitorOneDrive.Ok, 0);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Un errore del controllo stesso (disco momentaneamente non leggibile) non e' una prova che OneDrive sia fermo:
            // non cambia lo stato e viene solo registrato.
            DiagnosticTraceService.Registra("OneDrive.MonitorContinuo", $"errore={ex.GetType().Name}: {ex.Message}");
            return new RisultatoMonitorOneDrive(EsitoMonitorOneDrive.Ok, 0);
        }
    }
}
