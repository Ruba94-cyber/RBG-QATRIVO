namespace TestLoom.App.Services;

/// <summary>
/// Punto di ingresso dell'app per la sincronizzazione OneDrive: servizio predefinito (sostituibile dai test), testi localizzati
/// e applicazione dell'esito dell'avvio al blocco di scrittura <see cref="OneDriveSyncGate"/>.
/// </summary>
public static class OneDriveSync
{
    private static OneDriveSyncService _servizio = new(new OneDriveAccessoWindows(), new OneDrivePendenteStore());

    public static OneDriveSyncService Servizio
    {
        get => _servizio;
        internal set => _servizio = value;
    }

    /// <summary>
    /// Verifica e forza la sincronizzazione (avvio o "Riprova") e ne applica l'esito: sola lettura se non riuscita, altrimenti
    /// sblocco. Un errore imprevisto del servizio (<see cref="EsitoSyncOneDrive.Errore"/>) NON blocca l'app: è un difetto di TestLoom, non
    /// una prova che OneDrive sia indietro, ed è già registrato nel crash log. Non lancia eccezioni.
    /// </summary>
    private static RegistroScrittureOneDrive? _registro;
    private static readonly object _gateRegistro = new();

    /// <summary>
    /// Attiva (una volta per radice) il registro delle scritture: ogni file salvato sotto <paramref name="radice"/> viene annotato
    /// nello store locale dei pendenti. Senza OneDrive sotto la radice non fa nulla. Può toccare il disco: fuori dal thread UI.
    /// </summary>
    public static void AttivaRegistroScritture(string radice)
    {
        try
        {
            if (!Servizio.Applicabile(radice)) return;
            lock (_gateRegistro)
            {
                var completa = Path.GetFullPath(radice);
                if (_registro != null && string.Equals(_registroRadice, completa, StringComparison.OrdinalIgnoreCase)) return;
                _registro?.Dispose();
                _registro = new RegistroScrittureOneDrive(completa, Servizio.Store, Servizio.Opzioni.MassimoPercorsiPersistiti);
                _registroRadice = completa;
                var registro = _registro;
                JsonIo.DopoScritturaRiuscita = registro.Registra;
                Servizio.PrimaDellaVerifica = registro.Svuota;
            }
        }
        catch (Exception ex) { CrashLogService.Registra("Attivazione registro scritture OneDrive", ex); }
    }

    private static string? _registroRadice;

    private static OneDriveMonitorContinuo? _monitor;
    private static string? _monitorRadice;
    private static bool _bloccatoDalMonitor;
    private static readonly object _gateMonitor = new();

    /// <summary>
    /// Controllo continuo durante la sessione: se OneDrive non e' in esecuzione o non sta caricando, il workspace passa in sola
    /// lettura (stesso blocco, banner e "Riprova" della verifica di avvio); quando torna a sincronizzare, il blocco messo da questo
    /// controllo viene sciolto. Un blocco dell'avvio non viene mai sciolto da qui. Vero se lo stato del blocco e' cambiato.
    /// Tocca il disco: fuori dal thread UI. Non lancia eccezioni.
    /// </summary>
    public static bool ControlloContinuo(string radice)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(radice) || !Servizio.Applicabile(radice)) return false;
            RisultatoMonitorOneDrive risultato;
            lock (_gateMonitor)
            {
                var completa = Path.GetFullPath(radice);
                if (_monitor == null || !string.Equals(_monitorRadice, completa, StringComparison.OrdinalIgnoreCase))
                {
                    _monitor = new OneDriveMonitorContinuo(Servizio.Accesso);
                    _monitorRadice = completa;
                    _bloccatoDalMonitor = false;
                }
                risultato = _monitor.Controlla(radice);
                var attivo = OneDriveSyncGate.Attivo(radice);
                if (!risultato.Sicuro)
                {
                    if (attivo) return false; // gia' bloccato (avvio o monitor): nulla da aggiungere
                    OneDriveSyncGate.Blocca(radice, MotivoMonitor(risultato));
                    _bloccatoDalMonitor = true;
                    return true;
                }
                if (_bloccatoDalMonitor && attivo)
                {
                    OneDriveSyncGate.Sblocca(radice);
                    _bloccatoDalMonitor = false;
                    return true;
                }
                if (!attivo) _bloccatoDalMonitor = false;
                return false;
            }
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Controllo continuo OneDrive", ex);
            return false;
        }
    }

    public static string MotivoMonitor(RisultatoMonitorOneDrive r) => r.Esito == EsitoMonitorOneDrive.ClientNonAttivo
        ? T("OneDriveSolaLetturaClientTesto")
        : string.Format(T("OneDriveSolaLetturaCaricamentoFermoFmt"), r.FileFermi);

    /// <summary>Solo test: sostituisce (o azzera) il monitor, per usare orologio e soglie controllati.</summary>
    internal static void ImpostaMonitorPerTest(OneDriveMonitorContinuo? monitor, string? radice)
    {
        lock (_gateMonitor)
        {
            _monitor = monitor;
            _monitorRadice = radice == null ? null : Path.GetFullPath(radice);
            _bloccatoDalMonitor = false;
        }
    }

    public static async Task<RisultatoSyncOneDrive> VerificaEApplicaAsync(string radice, IProgress<AvanzamentoSyncOneDrive>? progress, CancellationToken ct = default)
    {
        await Task.Run(() => AttivaRegistroScritture(radice), CancellationToken.None).ConfigureAwait(false);
        var pendente = await Task.Run(() => Servizio.PendenteRegistrato(radice), CancellationToken.None).ConfigureAwait(false);
        var risultato = await Servizio.VerificaEForzaAvvioAsync(radice, progress, ct).ConfigureAwait(false);
        switch (risultato.Esito)
        {
            case EsitoSyncOneDrive.Timeout:
            case EsitoSyncOneDrive.ClientNonAttivo:
                OneDriveSyncGate.Blocca(radice, MotivoSolaLettura(risultato));
                break;
            case EsitoSyncOneDrive.Completata:
            case EsitoSyncOneDrive.NonApplicabile:
                OneDriveSyncGate.Sblocca(radice);
                break;
            default:
                if (pendente != null) DiagnosticTraceService.Registra("OneDrive.Avvio", $"esito={risultato.Esito}; pendente precedente={pendente.NumeroFile}");
                break;
        }
        // Prova a due vie con le altre postazioni: solo se la cartella e' davvero sotto OneDrive e la verifica e' andata a buon fine.
        if (risultato.Esito == EsitoSyncOneDrive.Completata)
            SyncHandshake.Avvia(radice, Environment.MachineName, () => WorkspaceWritePolicy.CanWrite(new DataPaths(radice)));
        return risultato;
    }

    // ------------------------------------------------------------------ testi localizzati

    private static string T(string chiave) => LocalizationService.Instance.Traduci(chiave);

    public static string TestoAvanzamento(AvanzamentoSyncOneDrive a) => a.Fase switch
    {
        FaseSyncOneDrive.AvvioClient => T("OneDriveAvvioClientTesto"),
        FaseSyncOneDrive.Scaricamento => string.Format(T("OneDriveScaricamentoFmt"), a.DaScaricare, a.DaCaricare),
        FaseSyncOneDrive.AttesaCaricamento when a.DaCaricare > 0 => string.Format(T("OneDriveAttesaCaricamentoFmt"), a.DaCaricare),
        _ => T("OneDriveVerificaTesto"),
    };

    /// <summary>Testo del banner di sola lettura dopo un avvio non riuscito.</summary>
    public static string MotivoSolaLettura(RisultatoSyncOneDrive r) => r.Esito == EsitoSyncOneDrive.ClientNonAttivo
        ? T("OneDriveSolaLetturaClientTesto")
        : string.Format(T("OneDriveSolaLetturaTimeoutFmt"), r.DaScaricare, r.DaCaricare);

    /// <summary>Avviso finale di chiusura quando restano file da caricare.</summary>
    public static string AvvisoChiusura(RisultatoSyncOneDrive r) =>
        string.Format(T("OneDriveUscitaAvvisoFmt"), r.DaCaricare);
}
