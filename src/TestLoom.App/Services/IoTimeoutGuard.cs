namespace TestLoom.App.Services;

/// <summary>Esito di un'operazione I/O con tetto di tempo (<see cref="IoTimeoutGuard.EseguiConEsito{T}"/>).</summary>
public enum EsitoIo
{
    /// <summary>Completata entro il tetto: il valore è quello reale dell'operazione.</summary>
    Ok,
    /// <summary>Tetto superato: l'operazione può essere ancora in corso e il suo esito è ignoto.</summary>
    Timeout,
    /// <summary>Operazione terminata con eccezione: nessun valore affidabile.</summary>
    Errore
}

/// <summary>Risultato di <see cref="IoTimeoutGuard.EseguiConEsito{T}"/>. <see cref="Valore"/> è affidabile solo con
/// <see cref="EsitoIo.Ok"/>; <see cref="Eccezione"/> è valorizzata solo con <see cref="EsitoIo.Errore"/>.</summary>
public sealed record RisultatoIo<T>(EsitoIo Esito, T? Valore, Exception? Eccezione);

/// <summary>Esegue un'operazione potenzialmente bloccante (I/O su file) su un thread separato con un limite di
/// tempo: se non risponde entro il timeout, ritorna il fallback invece di attendere indefinitamente (feedback
/// utente 2026-08-20, punto 5 — l'app si bloccava cercando un file .lock su una cartella condivisa in stallo,
/// perché LockService/CondivisioneHealthService facevano I/O sincrono senza limiti, spesso direttamente sul
/// thread UI). Il thread eventualmente bloccato viene abbandonato: non è annullabile forzatamente, ma è
/// innocuo — se l'operazione completa più tardi il risultato viene semplicemente scartato. Bloccare il
/// chiamante per un tetto di pochi secondi invece che indefinitamente è la differenza tra "una breve attesa" e
/// "l'app che smette di rispondere".</summary>
public static class IoTimeoutGuard
{
    public static T? Esegui<T>(Func<T> operazione, TimeSpan timeout, T? fallback = default)
    {
        var risultato = EseguiConEsito(operazione, timeout);
        return risultato.Esito == EsitoIo.Ok ? risultato.Valore : fallback;
    }

    /// <summary>
    /// Come <see cref="Esegui{T}"/>, ma distingue completamento, scadenza ed errore: il chiamante che deve decidere
    /// (per esempio se un lock è libero o soltanto illeggibile) non può più scambiare un errore per un risultato vuoto.
    /// Ogni scadenza ed errore viene tracciato in <see cref="DiagnosticTraceService"/> con il contesto indicato.
    /// </summary>
    public static RisultatoIo<T> EseguiConEsito<T>(Func<T> operazione, TimeSpan timeout, string contesto = "I/O")
    {
        try
        {
            var task = Task.Run(operazione);
            if (!task.Wait(timeout))
            {
                DiagnosticTraceService.Registra("IoTimeoutGuard",
                    $"contesto={contesto}; esito=timeout; tetto={timeout.TotalMilliseconds:0}ms");
                return new RisultatoIo<T>(EsitoIo.Timeout, default, null);
            }
            return new RisultatoIo<T>(EsitoIo.Ok, task.Result, null);
        }
        catch (Exception ex)
        {
            var causa = ex.GetBaseException();
            DiagnosticTraceService.Registra("IoTimeoutGuard",
                $"contesto={contesto}; esito=errore; causa={causa.GetType().Name}: {causa.Message}");
            return new RisultatoIo<T>(EsitoIo.Errore, default, causa);
        }
    }

    /// <summary>
    /// Come <see cref="Esegui{T}"/>, ma propaga l'eccezione originale dell'operazione (per esempio il blocco del gate di
    /// scrittura): su scadenza restituisce <paramref name="suScadenza"/>. Per i gate che devono restare fail-closed.
    /// </summary>
    public static T EseguiPropagando<T>(Func<T> operazione, TimeSpan timeout, Func<T> suScadenza)
    {
        var task = Task.Run(operazione);
        try
        {
            if (!task.Wait(timeout)) return suScadenza();
        }
        catch (AggregateException)
        {
            // Operazione terminata con errore: l'eccezione originale viene rilanciata qui sotto, senza il contenitore.
        }
        return task.GetAwaiter().GetResult();
    }
}
