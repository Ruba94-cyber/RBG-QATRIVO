using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Applica una tolleranza agli errori di lettura JSON transitori (scrittura OneDrive a metà, file bloccato
/// momentaneamente da un altro processo, copia ancora in corso, cancellazione ritardata) prima che compaiano
/// nell'avviso "File con problemi" mostrato all'utente in MainViewModel/ImpostazioniViewModel.
///
/// Multi-PC (6.4.2): con due postazioni che scrivono sulla stessa cartella OneDrive/SharePoint, un refresh
/// periodico o un evento watcher possono osservare un JSON a metà scrittura. Prima di questa tolleranza
/// bastava UN solo errore di lettura per far comparire l'avviso, anche se il file si sistemava da solo un
/// istante dopo: rumore per l'utente e nessun valore diagnostico reale.
///
/// Un file entra nell'elenco VISIBILE solo se resta illeggibile per almeno <see cref="MinConsecutiveFailures"/>
/// tentativi consecutivi E almeno <see cref="MinElapsed"/> dal primo errore osservato: un file davvero
/// corrotto (non una scrittura in corso) supera comunque questa soglia e viene segnalato. Ogni occorrenza,
/// tollerata o no, resta comunque registrata in CrashLogService per diagnostica.
///
/// Il tracker è la fonte di verità per l'elenco visibile: i repository gli passano solo i path effettivamente
/// riverificati in questo giro (tutti, per una riconciliazione completa; solo quelli toccati, per un refresh
/// incrementale guidato dal watcher) e lui ricostruisce l'elenco corrente applicando la soglia.
/// </summary>
public sealed class FileProblemToleranceTracker
{
    /// <summary>Tentativi di lettura consecutivi minimi prima che il file compaia nell'avviso utente.</summary>
    public const int MinConsecutiveFailures = 2;

    /// <summary>Tempo minimo trascorso dal primo errore osservato prima che il file compaia nell'avviso.</summary>
    public static readonly TimeSpan MinElapsed = TimeSpan.FromMinutes(2);

    /// <summary>Solo per i test: sovrascrive l'orologio usato per valutare <see cref="MinElapsed"/>, così un
    /// test non deve attendere minuti reali per verificare il comportamento oltre soglia. Stesso pattern di
    /// WorkspaceLocalPaths.LocalApplicationDataOverrideForTests. Null in produzione (usa DateTime.UtcNow).</summary>
    public static Func<DateTime>? NowUtcOverrideForTests;

    private static DateTime NowUtc() => NowUtcOverrideForTests?.Invoke() ?? DateTime.UtcNow;

    private readonly object _gate = new();
    private readonly Dictionary<string, Stato> _stato = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Stato
    {
        public DateTime FirstSeenUtc;
        public int ConsecutiveFailures;
        public string Errore = "sconosciuto";
        public bool GiaSegnalatoComeVisibile;
    }

    /// <summary>
    /// Riconciliazione completa: <paramref name="grezziIlleggibiliOra"/> è l'elenco ESATTO di tutti i path
    /// attualmente illeggibili in questo giro (ogni file condiviso è stato riletto). Un path tracciato in
    /// precedenza ma assente da questo elenco è tornato leggibile (o è stato eliminato) ed esce dal tracking.
    /// </summary>
    public List<FileConProblemi> AggiornaCompleto(IReadOnlyCollection<FileConProblemi> grezziIlleggibiliOra)
    {
        lock (_gate)
        {
            var visti = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var problema in grezziIlleggibiliOra)
            {
                visti.Add(problema.Percorso);
                Aggiorna(problema.Percorso, problema.Errore);
            }
            foreach (var path in _stato.Keys.Where(p => !visti.Contains(p)).ToList())
                _stato.Remove(path);
            return Visibili();
        }
    }

    /// <summary>
    /// Refresh incrementale (watcher): solo i path in <paramref name="percorsiRiverificati"/> sono stati
    /// effettivamente riletti in questo giro. <paramref name="percorsiConfermatiOk"/> sono quelli tornati
    /// leggibili o eliminati (escono dal tracking); <paramref name="nuoviGrezziIlleggibili"/> quelli ancora
    /// illeggibili (aggiornano il tracking). I path NON in <paramref name="percorsiRiverificati"/> non sono
    /// stati toccati da questo giro e il loro stato precedente resta invariato.
    /// </summary>
    public List<FileConProblemi> AggiornaIncrementale(
        IEnumerable<string> percorsiConfermatiOk,
        IReadOnlyCollection<FileConProblemi> nuoviGrezziIlleggibili)
    {
        lock (_gate)
        {
            foreach (var path in percorsiConfermatiOk)
                _stato.Remove(path);
            foreach (var problema in nuoviGrezziIlleggibili)
                Aggiorna(problema.Percorso, problema.Errore);
            return Visibili();
        }
    }

    private void Aggiorna(string percorso, string? errore)
    {
        var nowUtc = NowUtc();
        if (!_stato.TryGetValue(percorso, out var stato))
        {
            stato = new Stato { FirstSeenUtc = nowUtc };
            _stato[percorso] = stato;
            CrashLogService.Registra(
                $"File illeggibile, primo tentativo (tollerato salvo persistenza): {percorso} — {errore}", null);
        }
        stato.ConsecutiveFailures++;
        stato.Errore = errore ?? "sconosciuto";

        var visibileOra = stato.ConsecutiveFailures >= MinConsecutiveFailures &&
                           nowUtc - stato.FirstSeenUtc >= MinElapsed;
        if (visibileOra && !stato.GiaSegnalatoComeVisibile)
        {
            stato.GiaSegnalatoComeVisibile = true;
            CrashLogService.Registra(
                $"File illeggibile oltre la tolleranza, mostrato all'utente: {percorso} — {errore}", null);
        }
    }

    /// <summary>Path attualmente tracciati (visibili o ancora in tolleranza). Usato dai repository che
    /// enumerano l'intera cartella a ogni giro ma rileggono il contenuto solo dei file cambiati, per capire
    /// quali path tracciati sono scomparsi dal disco e vanno quindi confermati risolti.</summary>
    public IReadOnlyCollection<string> PercorsiTracciati()
    {
        lock (_gate) return _stato.Keys.ToList();
    }

    private List<FileConProblemi> Visibili()
    {
        var nowUtc = NowUtc();
        return _stato
            .Where(kv => kv.Value.ConsecutiveFailures >= MinConsecutiveFailures &&
                         nowUtc - kv.Value.FirstSeenUtc >= MinElapsed)
            .Select(kv => new FileConProblemi
            {
                Percorso = kv.Key,
                Errore = kv.Value.Errore,
                RilevatoIl = kv.Value.FirstSeenUtc.ToLocalTime()
            })
            .ToList();
    }
}
