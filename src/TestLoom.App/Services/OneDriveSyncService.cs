namespace TestLoom.App.Services;

/// <summary>
/// Verifica e forza la sincronizzazione OneDrive della cartella dati condivisa all'AVVIO e alla CHIUSURA dell'app.
///
/// Avvio: assicura che il client OneDrive sia in esecuzione (lo avvia se serve), scarica (hydrate) i file "solo online" necessari
/// (allegati esclusi: sono volutamente solo online, vedi <see cref="AllegatiSyncService"/>) e attende che non restino caricamenti
/// locali in sospeso, entro un timeout. Se non riesce l'app apre il workspace in sola lettura (vedi <see cref="OneDriveSyncGate"/>)
/// per non scrivere su dati non aggiornati.
///
/// Chiusura: attende (con timeout) che le modifiche locali siano caricate. Se il tempo scade, i file ancora da caricare vengono
/// persistiti in locale (<see cref="IOneDrivePendenteStore"/>) e ritentati al prossimo avvio.
///
/// Tutto asincrono e fuori dal thread UI; l'accesso al file system / client passa da <see cref="IOneDriveAccesso"/>, quindi la
/// logica è testabile con un finto OneDrive. Non lancia eccezioni verso il chiamante: un errore diventa <see cref="EsitoSyncOneDrive.Errore"/>.
/// </summary>
public sealed class OneDriveSyncService
{
    private readonly IOneDriveAccesso _accesso;
    private readonly IOneDrivePendenteStore _store;
    private readonly OneDriveSyncOptions _opzioni;
    private readonly Func<TimeSpan, CancellationToken, Task> _attesa;
    private readonly Func<DateTime> _oraUtc;

    public OneDriveSyncService(IOneDriveAccesso accesso, IOneDrivePendenteStore store, OneDriveSyncOptions? opzioni = null,
        Func<TimeSpan, CancellationToken, Task>? attesa = null, Func<DateTime>? oraUtc = null)
    {
        _accesso = accesso;
        _store = store;
        _opzioni = opzioni ?? new OneDriveSyncOptions();
        _attesa = attesa ?? Task.Delay;
        _oraUtc = oraUtc ?? (() => DateTime.UtcNow);
    }

    public OneDriveSyncOptions Opzioni => _opzioni;

    internal IOneDrivePendenteStore Store => _store;

    internal IOneDriveAccesso Accesso => _accesso;

    /// <summary>Eseguito prima di ogni verifica (avvio, chiusura): svuota il registro delle scritture recenti nello store.</summary>
    public Action? PrimaDellaVerifica { get; set; }

    /// <summary>Vero se la cartella è sotto OneDrive e quindi la verifica ha senso. Può toccare il disco: chiamare fuori dal thread UI.</summary>
    public bool Applicabile(string radice) => !string.IsNullOrWhiteSpace(radice) && SicuroDa(() => _accesso.EDentroOneDrive(radice), false);

    /// <summary>File rimasti da caricare alla chiusura precedente (null se nessuno).</summary>
    public OneDrivePendente? PendenteRegistrato(string radice) => SicuroDa(() => _store.Leggi(radice), null);

    /// <summary>Avvio: client attivo, file solo online scaricati, nessun caricamento locale in sospeso, entro <see cref="OneDriveSyncOptions.TimeoutAvvio"/>.</summary>
    public Task<RisultatoSyncOneDrive> VerificaEForzaAvvioAsync(string radice, IProgress<AvanzamentoSyncOneDrive>? progress = null,
        CancellationToken ct = default) =>
        EseguiAsync(radice, avvio: true, progress, ct);

    /// <summary>Chiusura: attende il caricamento delle modifiche locali entro <see cref="OneDriveSyncOptions.TimeoutChiusura"/>; a tempo scaduto persiste il pendente.</summary>
    public Task<RisultatoSyncOneDrive> AttendiCaricamentoChiusuraAsync(string radice, IProgress<AvanzamentoSyncOneDrive>? progress = null,
        CancellationToken ct = default) =>
        EseguiAsync(radice, avvio: false, progress, ct);

    private async Task<RisultatoSyncOneDrive> EseguiAsync(string radice, bool avvio, IProgress<AvanzamentoSyncOneDrive>? progress, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => CorpoAsync(radice, avvio, progress, ct), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new RisultatoSyncOneDrive(EsitoSyncOneDrive.Annullata, 0, 0);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra(avvio ? "Sincronizzazione OneDrive all'avvio" : "Sincronizzazione OneDrive alla chiusura", ex);
            return new RisultatoSyncOneDrive(EsitoSyncOneDrive.Errore, 0, 0, ex.Message);
        }
    }

    private async Task<RisultatoSyncOneDrive> CorpoAsync(string radice, bool avvio, IProgress<AvanzamentoSyncOneDrive>? progress, CancellationToken ct)
    {
        if (!Applicabile(radice)) return new RisultatoSyncOneDrive(EsitoSyncOneDrive.NonApplicabile, 0, 0);

        // Le scritture ancora in coda nel registro entrano tra i sospetti anche se non ancora arrivate nello store.
        try { PrimaDellaVerifica?.Invoke(); } catch (Exception ex) { CrashLogService.Registra("OneDrive: svuotamento registro scritture", ex); }

        var inizio = _oraUtc();
        var scadenza = inizio + (avvio ? _opzioni.TimeoutAvvio : _opzioni.TimeoutChiusura);
        TimeSpan Trascorso() => _oraUtc() - inizio;
        var sospetti = (PendenteRegistrato(radice)?.Percorsi ?? new List<string>()).ToArray();

        progress?.Report(new AvanzamentoSyncOneDrive(FaseSyncOneDrive.Verifica, 0, 0, 0, 0, Trascorso()));

        IReadOnlyList<FileCloud> ultimiDaCaricare = Array.Empty<FileCloud>();
        var ultimaDaScaricare = 0;
        var clientAttivo = false;
        try
        {
            // 1) Client OneDrive attivo. Senza client nessun file arriva o parte: all'avvio è un blocco, alla chiusura vale "non caricato".
            clientAttivo = await AssicuraClientAsync(progress, Trascorso, scadenza, ct).ConfigureAwait(false);

            // Senza Internet nulla puo' essere inviato o ricevuto: all'avvio e' un blocco esplicito, subito (non dopo 45 s).
            if (avvio && clientAttivo && !SicuroDa(_accesso.InternetOnline, true))
                return new RisultatoSyncOneDrive(EsitoSyncOneDrive.Offline, 0, 0);

            var totale = 0;
            var sollecitati = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var scansione = _accesso.Scansiona(radice, sospetti, ct);
                var rilevanti = scansione.DaAllineare.Where(f => Rilevante(radice, f, avvio)).ToList();
                var daScaricare = rilevanti.Where(f => f.Stato == StatoFileCloud.SoloOnline).ToList();
                var daCaricare = rilevanti.Where(f => f.Stato == StatoFileCloud.InAttesaCaricamento).ToList();
                ultimaDaScaricare = daScaricare.Count;
                ultimiDaCaricare = daCaricare;
                totale = Math.Max(totale, rilevanti.Count);

                progress?.Report(new AvanzamentoSyncOneDrive(
                    daScaricare.Count > 0 ? FaseSyncOneDrive.Scaricamento : FaseSyncOneDrive.AttesaCaricamento,
                    Math.Max(0, totale - rilevanti.Count), totale, daScaricare.Count, daCaricare.Count, Trascorso()));

                if (rilevanti.Count == 0)
                {
                    // Tutto allineato. Senza client attivo (nemmeno ora) non si può dire che i dati remoti siano arrivati.
                    if (!clientAttivo) clientAttivo = SicuroDa(_accesso.ClientInEsecuzione, false);
                    if (avvio && !clientAttivo) return new RisultatoSyncOneDrive(EsitoSyncOneDrive.ClientNonAttivo, 0, 0);
                    SicuroDa(() => { _store.Cancella(radice); return true; }, false);
                    return new RisultatoSyncOneDrive(EsitoSyncOneDrive.Completata, 0, 0);
                }

                // Sollecito una tantum per file: se OneDrive non ha preso in carico una modifica, la notifica lo spinge a farlo.
                foreach (var f in daCaricare)
                    if (sollecitati.Add(f.Percorso)) SicuroDa(() => { _accesso.Sollecita(f.Percorso); return true; }, false);

                // Avvio: i soli caricamenti in coda non vanno attesi (la coda di OneDrive puo' contenere file grandi di altri
                // e i dati letti sono comunque aggiornati): si prosegue subito, il pendente resta registrato per la chiusura.
                if (avvio && clientAttivo && daScaricare.Count == 0)
                {
                    PersistiPendente(radice, daCaricare);
                    DiagnosticTraceService.Registra("OneDrive.Pendenti",
                        string.Join(" | ", daCaricare.Take(10).Select(f => Path.GetRelativePath(radice, f.Percorso) + DettaglioPendente(f.Percorso))));
                    DiagnosticTraceService.Registra("OneDrive.Avvio", $"nessun file da scaricare; {daCaricare.Count} in coda di caricamento: si prosegue");
                    return new RisultatoSyncOneDrive(EsitoSyncOneDrive.CaricamentoInCorso, 0, daCaricare.Count);
                }

                if (_oraUtc() >= scadenza) break;

                // Avvio: richiama i file solo online (parallelismo limitato). Alla chiusura non si scarica nulla: si attende solo l'upload.
                if (avvio && daScaricare.Count > 0)
                    await ScaricaAsync(daScaricare, scadenza, ct).ConfigureAwait(false);

                var resto = scadenza - _oraUtc();
                if (resto <= TimeSpan.Zero) continue; // ultimo giro: rilegge lo stato e decide
                await _attesa(resto < _opzioni.IntervalloVerifica ? resto : _opzioni.IntervalloVerifica, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // "Chiudi senza attendere": i file ancora da caricare restano comunque registrati per il prossimo avvio.
            if (!avvio) PersistiPendente(radice, ultimiDaCaricare);
            throw;
        }

        // Tempo scaduto.
        if (!avvio) PersistiPendente(radice, ultimiDaCaricare);
        if (ultimiDaCaricare.Count > 0)
            DiagnosticTraceService.Registra("OneDrive.Pendenti",
                string.Join(" | ", ultimiDaCaricare.Take(10).Select(f => Path.GetRelativePath(radice, f.Percorso) + DettaglioPendente(f.Percorso))));
        // All'avvio i soli caricamenti in sospeso non rendono i dati obsoleti: niente blocco in sola lettura.
        if (avvio && clientAttivo && ultimaDaScaricare == 0 && ultimiDaCaricare.Count > 0)
        {
            PersistiPendente(radice, ultimiDaCaricare);
            DiagnosticTraceService.Registra("OneDrive.Avvio", $"caricamenti in sospeso senza blocco: {ultimiDaCaricare.Count} file");
            return new RisultatoSyncOneDrive(EsitoSyncOneDrive.CaricamentoInCorso, 0, ultimiDaCaricare.Count);
        }
        var esito = !clientAttivo ? EsitoSyncOneDrive.ClientNonAttivo
            : !SicuroDa(_accesso.InternetOnline, true) ? EsitoSyncOneDrive.Offline
            : EsitoSyncOneDrive.Timeout;
        return new RisultatoSyncOneDrive(esito, ultimaDaScaricare, ultimiDaCaricare.Count);
    }

    /// <summary>Verifica il client e, se manca, prova ad avviarlo attendendo un tempo breve. Restituisce lo stato finale.</summary>
    private async Task<bool> AssicuraClientAsync(IProgress<AvanzamentoSyncOneDrive>? progress, Func<TimeSpan> trascorso, DateTime scadenza, CancellationToken ct)
    {
        if (SicuroDa(_accesso.ClientInEsecuzione, false)) return true;
        progress?.Report(new AvanzamentoSyncOneDrive(FaseSyncOneDrive.AvvioClient, 0, 0, 0, 0, trascorso()));
        if (!SicuroDa(_accesso.AvviaClient, false)) return false;
        var limite = _oraUtc() + _opzioni.AttesaAvvioClient;
        if (limite > scadenza) limite = scadenza;
        var passo = TimeSpan.FromMilliseconds(Math.Min(500, Math.Max(1, _opzioni.IntervalloVerifica.TotalMilliseconds)));
        while (_oraUtc() < limite)
        {
            ct.ThrowIfCancellationRequested();
            if (SicuroDa(_accesso.ClientInEsecuzione, false)) return true;
            await _attesa(passo, ct).ConfigureAwait(false);
        }
        return SicuroDa(_accesso.ClientInEsecuzione, false);
    }

    private async Task ScaricaAsync(IReadOnlyList<FileCloud> file, DateTime scadenza, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lavoro = Parallel.ForEachAsync(file,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _opzioni.ParallelismoDownload), CancellationToken = cts.Token },
            (f, token) => new ValueTask(Task.Run(() =>
            {
                try { _accesso.Scarica(f.Percorso, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Un file che non si scarica non ferma gli altri: resta nella scansione successiva e conta nel timeout.
                    DiagnosticTraceService.Registra("OneDrive.Scarica", $"file={Path.GetFileName(f.Percorso)}; errore={ex.GetType().Name}: {ex.Message}");
                }
            }, CancellationToken.None)));

        var resto = scadenza - _oraUtc();
        if (resto < TimeSpan.Zero) resto = TimeSpan.Zero;
        // Tetto reale: un richiamo bloccato (rete in stallo) non deve trattenere oltre la scadenza. Il lavoro abbandonato
        // viene annullato e termina da solo in background.
        var scaduto = Task.Delay(resto, ct);
        var finito = await Task.WhenAny(lavoro, scaduto).ConfigureAwait(false);
        if (finito != lavoro)
        {
            cts.Cancel();
            _ = lavoro.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            ct.ThrowIfCancellationRequested();
            return;
        }
        try { await lavoro.ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private void PersistiPendente(string radice, IReadOnlyList<FileCloud> daCaricare)
    {
        if (daCaricare.Count == 0) return;
        var percorsi = daCaricare
            .Select(f => Path.GetRelativePath(radice, f.Percorso))
            .Take(_opzioni.MassimoPercorsiPersistiti)
            .ToList();
        SicuroDa(() =>
        {
            _store.Salva(radice, new OneDrivePendente { RegistratoIlUtc = _oraUtc(), NumeroFile = daCaricare.Count, Percorsi = percorsi });
            return true;
        }, false);
    }

    /// <summary>
    /// Filtra i file che non devono influenzare l'esito: gli allegati solo online sono voluti (Files On-Demand) e non si scaricano;
    /// file temporanei e di prova scrittura sono transitori e non vanno attesi. Alla chiusura interessa solo il caricamento.
    /// </summary>
    internal static bool Rilevante(string radice, FileCloud file, bool avvio)
    {
        var nome = Path.GetFileName(file.Percorso);
        if (nome.StartsWith("~$", StringComparison.Ordinal) || nome.StartsWith(".testloom-write-probe", StringComparison.OrdinalIgnoreCase) ||
            nome.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || EraTemporaneoDiScrittura(nome))
            return false;
        if (InHandshake(radice, file.Percorso)) return false; // riscritto di continuo; la consegna la prova l'handshake stesso
        if (file.Stato == StatoFileCloud.SoloOnline && InAllegati(radice, file.Percorso)) return false;
        return avvio || file.Stato == StatoFileCloud.InAttesaCaricamento;
    }

    /// <summary>Temporaneo di JsonIo.WriteBytesAtomic: "&lt;nome&gt;.tmp" + 8 cifre esadecimali.</summary>
    internal static bool EraTemporaneoDiScrittura(string nome)
    {
        var i = nome.LastIndexOf(".tmp", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || nome.Length - (i + 4) != 8) return false;
        for (var k = i + 4; k < nome.Length; k++)
            if (!Uri.IsHexDigit(nome[k])) return false;
        return true;
    }

    private static string DettaglioPendente(string percorso)
    {
        try
        {
            var i = new FileInfo(percorso);
            return $" [attr=0x{(uint)i.Attributes:X}; scritto={i.LastWriteTimeUtc:yyyy-MM-dd HH:mm}Z]";
        }
        catch (Exception) { return ""; }
    }

    private static bool InHandshake(string radice, string percorso)
    {
        var parti = Path.GetRelativePath(radice, percorso).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parti.Length >= 3 && parti[0].Equals("_ditm", StringComparison.OrdinalIgnoreCase) &&
               parti[1].Equals("sync-handshake", StringComparison.OrdinalIgnoreCase);
    }

    private static bool InAllegati(string radice, string percorso)
    {
        var relativo = Path.GetRelativePath(radice, percorso);
        var primo = relativo.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return primo.Equals("allegati", StringComparison.OrdinalIgnoreCase);
    }

    private static T SicuroDa<T>(Func<T> f, T predefinito)
    {
        try { return f(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticTraceService.Registra("OneDrive.Accesso", $"{ex.GetType().Name}: {ex.Message}");
            return predefinito;
        }
    }
}
