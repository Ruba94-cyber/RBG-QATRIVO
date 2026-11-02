namespace TestLoom.App.Services;

/// <summary>Fase corrente del bootstrap di AppState, in parole comprensibili per la schermata di avvio.
/// Ogni valore corrisponde a un passo reale di <see cref="AppState.InizializzaCartella"/>, non a un'astrazione
/// inventata: se questo elenco cambia, è perché è cambiato il bootstrap stesso.</summary>
public enum FaseBootstrap
{
    Avvio,
    VerificaWorkspace,
    RipristinoTransazioni,
    CaricamentoTest,
    CaricamentoNonConformita,
    CaricamentoDatiCorrelati,
    Completato,

    /// <summary>Fase successiva a Completato (piano 6.4.0, "scaldamento all'avvio"): AppState è già pronto ma
    /// App.axaml.cs, prima di mostrare la MainWindow, costruisce già i ViewModel di WU Status/Report/Test
    /// (stesso percorso della prima navigazione, vedi MainViewModel.PreparaPaginaAsync) così la prima visita
    /// reale dell'utente trova la pagina già in cache invece di pagare 1-3s di calcolo a finestra visibile.
    /// Senza un messaggio dedicato l'utente vedrebbe la finestra di avvio restare ferma su "Completato" per
    /// diversi secondi in più, indistinguibile da un blocco (stesso motivo per cui esiste questa finestra).</summary>
    PreparazionePagine
}

/// <summary>
/// Fotografia dell'avanzamento del bootstrap, pubblicata via <see cref="IProgress{T}"/> verso la schermata di
/// avvio (feedback utente: su un workspace nuovo/100k+ elementi il bootstrap reale impiega 5-7 minuti senza
/// alcuna finestra visibile, e l'utente non sa se l'app è bloccata o sta ancora lavorando). <see cref="Totale"/>
/// è null finché il numero di elementi da elaborare non è realmente conosciuto (fine enumerazione file): la UI
/// non deve mai inventare una percentuale prima di allora.
/// </summary>
public sealed record BootstrapProgress
{
    public required FaseBootstrap Fase { get; init; }
    public required string Messaggio { get; init; }
    public int? Elaborati { get; init; }
    public int? Totale { get; init; }
    public required TimeSpan Trascorso { get; init; }
    public TimeSpan? StimaRimanente { get; init; }
}

/// <summary>
/// Incapsula il canale opzionale di avanzamento passato ad AppState: calcola trascorso/ETA e limita la
/// frequenza delle notifiche (la ricostruzione di un indice da 100k file chiamerebbe altrimenti il sink decine
/// di migliaia di volte). Quando nessun <see cref="IProgress{T}"/> è stato fornito (il caso di gran lunga più
/// comune: ogni test unitario e ogni refresh periodico dopo il primo avvio) ogni chiamata è no-op immediata,
/// senza lock né allocazioni.
/// </summary>
public sealed class BootstrapProgressReporter
{
    /// <summary>Istanza condivisa senza sink: usata da AppState dopo il primo bootstrap, così i refresh
    /// successivi (timer periodico, cambio cartella da Impostazioni) non continuano a riportare avanzamento
    /// verso una schermata di avvio ormai chiusa.</summary>
    public static readonly BootstrapProgressReporter Nessuno = new(null);

    private static readonly TimeSpan IntervalloMinimo = TimeSpan.FromMilliseconds(150);

    private readonly IProgress<BootstrapProgress>? _sink;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private readonly object _gate = new();
    private DateTime _ultimoReportUtc = DateTime.MinValue;

    public BootstrapProgressReporter(IProgress<BootstrapProgress>? sink) => _sink = sink;

    public bool Attivo => _sink != null;

    /// <summary>Cambio di fase: sempre inviato, anche se arriva prima dell'intervallo minimo, perché annuncia
    /// un passo nuovo (es. "ora sto leggendo le Non Conformità") che l'utente deve vedere subito.</summary>
    public void Fase(FaseBootstrap fase, string messaggio, int? elaborati = null, int? totale = null) =>
        Invia(fase, messaggio, elaborati, totale, forza: true);

    /// <summary>Avanzamento entro la stessa fase (es. durante la lettura parallela di migliaia di JSON):
    /// soggetto al throttling per non saturare il dispatcher UI.</summary>
    public void Avanzamento(FaseBootstrap fase, string messaggio, int elaborati, int? totale) =>
        Invia(fase, messaggio, elaborati, totale, forza: false);

    private void Invia(FaseBootstrap fase, string messaggio, int? elaborati, int? totale, bool forza)
    {
        if (_sink == null) return;
        // Un cambio di fase (forza=true) non deve consumare la finestra di throttling degli Avanzamento():
        // altrimenti la primissima notifica "totale ora conosciuto" che segue quasi sempre un Fase() (es.
        // "Ricostruzione dell'indice..." seguito subito da onProgress(0, totale)) verrebbe scartata perché
        // arrivata a pochi millisecondi dal Fase() precedente, lasciando l'utente senza numeri fino al primo
        // vero avanzamento utile. Solo gli Avanzamento() aggiornano/consultano il timestamp di throttling.
        if (!forza)
        {
            lock (_gate)
            {
                var adesso = DateTime.UtcNow;
                if (adesso - _ultimoReportUtc < IntervalloMinimo) return;
                _ultimoReportUtc = adesso;
            }
        }

        var trascorso = _sw.Elapsed;
        var stima = StimaRimanente(elaborati, totale, trascorso);
        _sink.Report(new BootstrapProgress
        {
            Fase = fase,
            Messaggio = messaggio,
            Elaborati = elaborati,
            Totale = totale,
            Trascorso = trascorso,
            StimaRimanente = stima
        });
    }

    /// <summary>
    /// Stima il tempo rimanente dal ritmo osservato finora (elementi/secondo). Funzione pura, testabile senza
    /// dipendere da un vero Stopwatch: niente stima finché non abbiamo sia un totale reale sia almeno un
    /// elemento elaborato in un tempo misurabile, per non mostrare mai un numero inventato.
    /// </summary>
    public static TimeSpan? StimaRimanente(int? elaborati, int? totale, TimeSpan trascorso)
    {
        if (!elaborati.HasValue || !totale.HasValue) return null;
        if (elaborati.Value <= 0 || totale.Value <= elaborati.Value) return null;
        if (trascorso <= TimeSpan.Zero) return null;

        var ritmo = elaborati.Value / trascorso.TotalSeconds; // elementi al secondo
        if (ritmo <= 0) return null;

        var rimanenti = totale.Value - elaborati.Value;
        return TimeSpan.FromSeconds(rimanenti / ritmo);
    }
}
