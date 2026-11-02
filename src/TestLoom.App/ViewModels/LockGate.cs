using System.Collections.Concurrent;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Serializza, dentro il processo, le operazioni di lock (acquisizione e rilascio tardivo) su uno stesso file.
/// LockService riusa lo stesso SessionId per tutte le acquisizioni di questo processo sullo stesso operatore: un
/// rilascio tardivo di un editor già chiuso staccherebbe quindi il lock appena acquisito da un editor nuovo. Con
/// il gate, l'acquisizione del nuovo editor attende che il rilascio dell'editor precedente sia terminato.
/// Asincrono: non blocca alcun thread mentre attende.
/// </summary>
internal static class LockGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan[] _atteseRetryRilascio =
    {
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(1200)
    };
    private static readonly ConcurrentDictionary<SessioneLockKey, TaskCompletionSource<EsitoIo>> _rilasciSessioneInCorso =
        new(new SessioneLockKeyComparer());
    private static readonly ConcurrentDictionary<SessioneLockKey, RilascioLockSessioneFallito> _rilasciSessioneFalliti =
        new(new SessioneLockKeyComparer());

    internal sealed record RilascioLockSessioneFallito(
        string Percorso,
        string Sessione,
        int Tentativi,
        EsitoIo UltimoEsito,
        DateTime FallitoIlUtc);

    private readonly record struct SessioneLockKey(string Percorso, string Sessione);

    private sealed class SessioneLockKeyComparer : IEqualityComparer<SessioneLockKey>
    {
        public bool Equals(SessioneLockKey x, SessioneLockKey y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Percorso, y.Percorso) &&
            StringComparer.Ordinal.Equals(x.Sessione, y.Sessione);

        public int GetHashCode(SessioneLockKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Percorso),
                StringComparer.Ordinal.GetHashCode(obj.Sessione));
    }

    /// <summary>Ultimi rilascî exact-session che hanno esaurito il retry. Il lock resta valido e scade secondo
    /// la lease normale; questa fotografia process-scoped conserva la coppia da diagnosticare.</summary>
    internal static IReadOnlyCollection<RilascioLockSessioneFallito> RilasciSessioneFalliti =>
        _rilasciSessioneFalliti.Values.ToArray();

    public static async Task<T> EseguiAsync<T>(string percorso, Func<Task<T>> operazione)
    {
        var gate = _gates.GetOrAdd(Path.GetFullPath(percorso), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try { return await operazione().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public static async Task EseguiAsync(string percorso, Func<Task> operazione)
    {
        var gate = _gates.GetOrAdd(Path.GetFullPath(percorso), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try { await operazione().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Rilascio exact-session di proprietà del processo, non della view che lo avvia. Le chiamate simultanee
    /// per la stessa coppia path/session condividono un solo task; il gate del path resta detenuto per tutti i
    /// tentativi e i backoff. ReleaseIfSession non può cancellare una sessione più recente.
    /// </summary>
    internal static Task<EsitoIo> RilasciaSessioneEsattaConRetryAsync(
        LockService locks,
        string percorso,
        string sessione,
        Func<LockService, string, string, EsitoIo>? rilascioPerTest = null,
        Func<TimeSpan, Task>? attesaPerTest = null) =>
        AvviaRilascioSessioneEsattaConRetry(
            locks, percorso, sessione, gateGiaAcquisito: false,
            rilascioPerTest: rilascioPerTest, attesaPerTest: attesaPerTest);

    /// <summary>Versione per cleanup eseguito dentro il gate path già detenuto dall'acquisizione.</summary>
    internal static Task<EsitoIo> RilasciaSessioneEsattaConRetrySottoGateAsync(
        LockService locks,
        string percorso,
        string sessione,
        Func<LockService, string, string, EsitoIo>? rilascioPerTest = null,
        Func<TimeSpan, Task>? attesaPerTest = null) =>
        AvviaRilascioSessioneEsattaConRetry(
            locks, percorso, sessione, gateGiaAcquisito: true,
            rilascioPerTest: rilascioPerTest, attesaPerTest: attesaPerTest);

    private static Task<EsitoIo> AvviaRilascioSessioneEsattaConRetry(
        LockService locks,
        string percorso,
        string sessione,
        bool gateGiaAcquisito,
        Func<LockService, string, string, EsitoIo>? rilascioPerTest,
        Func<TimeSpan, Task>? attesaPerTest)
    {
        if (string.IsNullOrWhiteSpace(sessione)) return Task.FromResult(EsitoIo.Ok);

        var percorsoChiave = Path.GetFullPath(percorso);
        var key = new SessioneLockKey(percorsoChiave, sessione);
        var nuovo = new TaskCompletionSource<EsitoIo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var condiviso = _rilasciSessioneInCorso.GetOrAdd(key, nuovo);
        if (!ReferenceEquals(condiviso, nuovo))
        {
            if (!gateGiaAcquisito || condiviso.Task.IsCompleted) return condiviso.Task;

            // Un coordinatore normale può essere registrato mentre il chiamante possiede già il gate e
            // attendere proprio quel gate. Attenderne qui il task creerebbe un deadlock: il chiamante
            // completa lo stesso task eseguendo il rilascio bounded sotto il gate; il coordinatore già
            // accodato, quando entrerà, vedrà il task completato e non ripeterà il rilascio.
            var rilascioEsistente = rilascioPerTest ?? (static (service, path, exactSession) => service.ReleaseIfSessionTracked(path, exactSession));
            var attesaEsistente = attesaPerTest ?? (static intervallo => Task.Delay(intervallo));
            _ = EseguiRilascioSessioneConRetryAsync(
                key, condiviso, locks, percorso, sessione, rilascioEsistente, attesaEsistente, gateGiaAcquisito: true);
            return condiviso.Task;
        }

        var rilascio = rilascioPerTest ?? (static (service, path, session) => service.ReleaseIfSessionTracked(path, session));
        var attesa = attesaPerTest ?? (static intervallo => Task.Delay(intervallo));
        _ = EseguiRilascioSessioneConRetryAsync(key, nuovo, locks, percorso, sessione, rilascio, attesa, gateGiaAcquisito);
        return nuovo.Task;
    }

    private static async Task EseguiRilascioSessioneConRetryAsync(
        SessioneLockKey key,
        TaskCompletionSource<EsitoIo> completamento,
        LockService locks,
        string percorso,
        string sessione,
        Func<LockService, string, string, EsitoIo> rilascio,
        Func<TimeSpan, Task> attesa,
        bool gateGiaAcquisito)
    {
        var ultimoEsito = EsitoIo.Errore;
        Exception? ultimoErrore = null;
        var tentativiMassimi = _atteseRetryRilascio.Length + 1;
        try
        {
            async Task EseguiTentativiAsync()
            {
                for (var tentativo = 1; tentativo <= tentativiMassimi; tentativo++)
                {
                    try
                    {
                        ultimoEsito = await Task.Run(() => rilascio(locks, percorso, sessione)).ConfigureAwait(false);
                        ultimoErrore = null;
                    }
                    catch (Exception ex)
                    {
                        ultimoErrore = ex;
                        ultimoEsito = EsitoIo.Errore;
                    }

                    if (ultimoEsito == EsitoIo.Ok) return;

                    if (tentativo <= _atteseRetryRilascio.Length)
                        await attesa(_atteseRetryRilascio[tentativo - 1]).ConfigureAwait(false);
                }
            }

            void CompletaEsito()
            {
                if (ultimoEsito == EsitoIo.Ok)
                {
                    _rilasciSessioneFalliti.TryRemove(key, out _);
                    completamento.TrySetResult(EsitoIo.Ok);
                    return;
                }

                var fallimento = new RilascioLockSessioneFallito(
                    percorso, sessione, tentativiMassimi, ultimoEsito, DateTime.UtcNow);
                _rilasciSessioneFalliti[key] = fallimento;
                var messaggioFinale =
                    $"Rilascio della sessione {sessione} per {percorso} fallito dopo {tentativiMassimi} tentativi ({ultimoEsito}).";
                Exception erroreFinale = ultimoErrore == null
                    ? new IOException(messaggioFinale)
                    : new InvalidOperationException(messaggioFinale, ultimoErrore);
                CrashLogService.Registra("Rilascio sessione lock test esauriti retry", erroreFinale);
                completamento.TrySetResult(ultimoEsito);
            }

            if (gateGiaAcquisito)
            {
                if (!completamento.Task.IsCompleted)
                {
                    await EseguiTentativiAsync().ConfigureAwait(false);
                    CompletaEsito();
                }
            }
            else
            {
                await EseguiAsync(percorso, async () =>
                {
                    // Una chiamata sotto-gate può aver completato il task mentre questo coordinatore
                    // attendeva il gate. Controllo e completamento avvengono sotto lo stesso gate, così
                    // nessuna nuova acquisizione può riusare la SessionId prima della pubblicazione.
                    if (completamento.Task.IsCompleted) return;
                    await EseguiTentativiAsync().ConfigureAwait(false);
                    CompletaEsito();
                }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            var fallimento = new RilascioLockSessioneFallito(
                percorso, sessione, tentativiMassimi, EsitoIo.Errore, DateTime.UtcNow);
            _rilasciSessioneFalliti[key] = fallimento;
            CrashLogService.Registra("Coordinatore rilascio sessione lock test", ex);
            completamento.TrySetResult(EsitoIo.Errore);
        }
        finally
        {
            _rilasciSessioneInCorso.TryRemove(new KeyValuePair<SessioneLockKey, TaskCompletionSource<EsitoIo>>(key, completamento));
        }
    }
}
