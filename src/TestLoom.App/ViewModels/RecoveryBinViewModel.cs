using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Vista amministrativa del recovery-bin condiviso. Non espone mai PayloadBase64: la UI lavora soltanto con
/// RecoveryId e metadati, mentre DomainRecoveryService rilegge/verifica l'envelope dal disco al momento del
/// ripristino. Nessuna funzione di purge è intenzionalmente esposta.
/// </summary>
public partial class RecoveryBinViewModel : ViewModelBase, IDisposable
{
    private readonly AppState _state;
    private bool _disposed;

    /// <summary>Lettura del cestino: ogni <see cref="Aggiorna"/> avvia una nuova generazione. Una lettura superata da una
    /// generazione piu' recente (per esempio da un ripristino o una purga nel frattempo) non viene applicata.</summary>
    private int _generazioneLettura;
    private int _letturaScartate;
    private Task _lettura = Task.CompletedTask;

    /// <summary>Solo test: forza (true) o esclude (false) la lettura in background. Di default la lettura gira in background
    /// quando esiste un'applicazione Avalonia (UI reale), in linea altrimenti (test senza UI).</summary>
    internal bool? ForzaBackground;

    /// <summary>Solo test: eseguito dopo la lettura del cestino e prima della sua applicazione (puo' bloccare).</summary>
    internal Action? GateLetturaPerTest { get; set; }

    /// <summary>Solo test: letture superate da una generazione piu' recente, scartate senza essere applicate.</summary>
    internal int LettureScartate => Volatile.Read(ref _letturaScartate);

    /// <summary>Completamento dell'ultima lettura avviata (applicata o scartata).</summary>
    internal Task AttendiAggiornamentoAsync() => _lettura;

    private bool InBackground => ForzaBackground ?? Avalonia.Application.Current != null;

    /// <summary>Vero mentre l'ultima lettura non e' ancora stata applicata: la vista mostra un indicatore discreto.</summary>
    [ObservableProperty] private bool caricamentoInCorso;

    /// <summary>Elenco dei record del cestino (sola pagina di lettura, nessun gruppo parziale): aggiornato in blocco.</summary>
    public BulkObservableCollection<RecoveryBinEntryViewModel> Elementi { get; } = new();

    [ObservableProperty] private bool ripristinoInCorso;
    [ObservableProperty] private bool mostraGiaRipristinati = true;
    [ObservableProperty] private string? messaggio;
    [ObservableProperty] private string? messaggioErrore;

    public bool EMaster => _state.EMaster;
    public bool PuoScrivere => _state.PuoScrivere;
    public bool HaElementi => Elementi.Count > 0;
    /// <summary>Messaggio "nessun elemento" solo a lettura conclusa: durante la lettura in background l'elenco e' ancora vuoto.</summary>
    public bool MostraNessunElemento => !HaElementi && !CaricamentoInCorso;
    public bool HaMessaggio => !string.IsNullOrWhiteSpace(Messaggio);
    public bool HaMessaggioErrore => !string.IsNullOrWhiteSpace(MessaggioErrore);
    public int Totale => Elementi.Count;
    public int Disponibili => Elementi.Count(x => !x.Ripristinato);
    public int Ripristinati => Elementi.Count(x => x.Ripristinato);

    public string Titolo => T("Recovery cancellazioni", "Deletion recovery", "Récupération des suppressions");
    public string Sottotitolo => T(
        "Ogni cancellazione distruttiva di TestLoom conserva una copia condivisa verificata. Un ripristino non sovrascrive mai un record diverso già presente.",
        "Every destructive TestLoom deletion keeps a verified shared recovery copy. A restore never overwrites a different record already present.",
        "Chaque suppression destructive de TestLoom conserve une copie de récupération partagée et vérifiée. Une restauration n'écrase jamais un enregistrement différent déjà présent.");
    public string NotaRetention => T(
        "I recovery non vengono mai eliminati automaticamente e sono inclusi anche nei backup JSON di TestLoom. Usa \"Elimina definitivamente\" su una riga per togliere per sempre quella copia — non è reversibile.",
        "Recovery records are never purged automatically, and are also included in TestLoom JSON backups. Use \"Delete permanently\" on a row to remove that one copy for good — it cannot be undone.",
        "Les copies de récupération ne sont jamais purgées automatiquement et sont aussi incluses dans les sauvegardes JSON de TestLoom. Utilisez « Supprimer définitivement » sur une ligne pour supprimer cette seule copie : l'action est irréversible.");
    public string AggiornaTesto => T("Aggiorna", "Refresh", "Actualiser");
    public string MostraRipristinatiTesto => T("Mostra anche gli elementi già ripristinati", "Show already restored entries", "Afficher aussi les éléments déjà restaurés");
    public string NessunElementoTesto => T("Non sono presenti elementi nel recovery-bin.", "No recovery entries are present.", "Aucun élément de récupération n'est présent.");
    public string SoloMasterTesto => T("Questa sezione è disponibile solo agli utenti Master.", "This section is available to Master users only.", "Cette section est réservée aux utilisateurs Master.");
    public string SolaLetturaTesto => T(
        "Il workspace è attualmente in sola lettura: i recovery sono consultabili ma non ripristinabili.",
        "The workspace is currently read-only: recovery entries can be inspected but not restored.",
        "L'espace de travail est actuellement en lecture seule : les éléments de récupération peuvent être consultés mais pas restaurés.");
    public string RiepilogoTesto => T(
        $"{Disponibili} disponibili · {Ripristinati} già ripristinati",
        $"{Disponibili} available · {Ripristinati} already restored",
        $"{Disponibili} disponibles · {Ripristinati} déjà restaurés");

    private static string T(string it, string en, string fr) => LocalizationService.Instance.Scegli(it, en, fr);

    public RecoveryBinViewModel(AppState state) : this(state, forzaBackground: null)
    {
    }

    /// <summary>Costruttore con l'opzione per i test: <paramref name="forzaBackground"/> forza o esclude la lettura in background
    /// gia' alla costruzione (come in UI reale, dove il costruttore torna subito e l'elenco arriva dopo).</summary>
    internal RecoveryBinViewModel(AppState state, bool? forzaBackground)
    {
        _state = state;
        ForzaBackground = forzaBackground;
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
        Aggiorna();
    }

    partial void OnCaricamentoInCorsoChanged(bool value) => OnPropertyChanged(nameof(MostraNessunElemento));
    partial void OnMessaggioChanged(string? value) => OnPropertyChanged(nameof(HaMessaggio));
    partial void OnMessaggioErroreChanged(string? value) => OnPropertyChanged(nameof(HaMessaggioErrore));

    partial void OnMostraGiaRipristinatiChanged(bool value) => Aggiorna();

    partial void OnRipristinoInCorsoChanged(bool value)
    {
        OnPropertyChanged(nameof(PuoScrivere));
        foreach (var row in Elementi) row.NotificaPuoRipristinare(_state.PuoScrivere && !value);
    }

    /// <summary>Rilegge il cestino. In UI reale la lettura (file e verifica dei payload) gira fuori dal thread UI; l'elenco viene
    /// poi sostituito in blocco. Una nuova chiamata supera le letture in corso: un risultato superato non viene applicato, cosi'
    /// un ripristino o una purga durante la lettura non fa riapparire un record gia' ripristinato, ne' fa perdere gli altri.</summary>
    [RelayCommand]
    private void Aggiorna()
    {
        if (_disposed) return;
        var generazione = Interlocked.Increment(ref _generazioneLettura);
        var paths = _state.CartellaValida && _state.EMaster ? _state.Paths : null;
        var mostraRipristinati = MostraGiaRipristinati;
        if (!InBackground)
        {
            ApplicaElementi(LeggiRecord(paths, mostraRipristinati));
            CaricamentoInCorso = false;
            return;
        }

        CaricamentoInCorso = true;
        _lettura = Task.Run(async () =>
        {
            IReadOnlyList<DomainRecoveryRecord>? records = null;
            Exception? errore = null;
            try { records = LeggiRecord(paths, mostraRipristinati); }
            catch (Exception ex) { errore = ex; }
            GateLetturaPerTest?.Invoke();
            await SuThreadUi(() =>
            {
                if (_disposed || generazione != Volatile.Read(ref _generazioneLettura))
                {
                    Interlocked.Increment(ref _letturaScartate);
                    return;
                }
                if (errore != null)
                {
                    CrashLogService.Registra("Recovery-bin: lettura", errore);
                    MessaggioErrore = T($"Elenco recovery non caricato: {errore.Message}", $"Recovery list not loaded: {errore.Message}", $"Liste de récupération non chargée : {errore.Message}");
                }
                else ApplicaElementi(records!);
                CaricamentoInCorso = false;
            });
        });
    }

    /// <summary>Record da mostrare, senza oggetti UI: rilettura del cestino, filtro dei ripristinati, ordine per data di archiviazione.</summary>
    private static IReadOnlyList<DomainRecoveryRecord> LeggiRecord(DataPaths? paths, bool mostraRipristinati)
    {
        IReadOnlyList<DomainRecoveryRecord> records = paths == null ? Array.Empty<DomainRecoveryRecord>() : DomainRecoveryService.GetAll(paths);
        if (!mostraRipristinati) records = records.Where(x => !x.RestoredUtc.HasValue).ToList();
        return records.OrderByDescending(x => x.ArchivedUtc).ToList();
    }

    /// <summary>Sostituisce l'elenco in un'unica notifica (righe nuove con i permessi del momento).</summary>
    private void ApplicaElementi(IReadOnlyList<DomainRecoveryRecord> records)
    {
        Elementi.ReplaceWith(records.Select(record => new RecoveryBinEntryViewModel(record, _state.PuoScrivere && !RipristinoInCorso)));
        NotificaRiepilogo();
    }

    private static Task SuThreadUi(Action azione)
    {
        if (Avalonia.Application.Current == null) { azione(); return Task.CompletedTask; }
        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(azione).GetTask();
    }

    [RelayCommand]
    private Task Ripristina(RecoveryBinEntryViewModel? row) =>
        ComandoAsincrono.EseguiAsync(() => RipristinaCoreAsync(row), "Ripristino dal cestino", m => MessaggioErrore = m);

    private async Task RipristinaCoreAsync(RecoveryBinEntryViewModel? row)
    {
        if (row == null || row.Ripristinato || !_state.EMaster || !_state.PuoScrivere || RipristinoInCorso) return;

        var confermato = await ConfirmService.Chiedi(
            T("Ripristina elemento cancellato", "Restore deleted item", "Restaurer l'élément supprimé"),
            T($"Ripristinare {row.Categoria} nel percorso '{row.PercorsoOriginale}'?\n\nDITM non sovrascriverà mai un record diverso già presente. Se viene rilevata una collisione, questa copia di recovery resterà intatta.",
              $"Restore {row.Categoria} to '{row.PercorsoOriginale}'?\n\nDITM will never overwrite a different record already present. If a collision is found, this recovery copy will remain untouched.",
              $"Restaurer {row.Categoria} vers '{row.PercorsoOriginale}' ?\n\nDITM n'écrasera jamais un enregistrement différent déjà présent. En cas de collision, cette copie de récupération restera intacte."),
            T("Ripristina", "Restore", "Restaurer"),
            T("Annulla", "Cancel", "Annuler"));
        if (!confermato) return;

        RipristinoInCorso = true;
        Messaggio = null;
        MessaggioErrore = null;
        DomainRecoveryRestoreResult? restored = null;
        try
        {
            using (var tx = WorkflowTransactionService.Begin(_state.Paths, $"restore-domain-recovery:{row.RecoveryId:N}"))
            {
                restored = DomainRecoveryService.Restore(_state.Paths, row.RecoveryId, _state.Config.OperatoreAttivo);
                tx.Commit();
            }
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Ripristino recovery-bin", ex);
            MessaggioErrore = T($"Ripristino non eseguito: {ex.Message}", $"Restore not performed: {ex.Message}", $"Restauration non effectuée : {ex.Message}");
            Aggiorna();
            return;
        }
        finally
        {
            RipristinoInCorso = false;
        }

        try
        {
            // Journal per-device, best effort: stesso comportamento del ripristino dalle Impostazioni.
            DomainRecoveryService.TryRecordRestoreInJournal(_state.RecordJournal, restored!, _state.Config.OperatoreAttivo);
            _state.RicaricaTutto();
            _state.RegistraAudit(
                "RestoreDomainRecovery",
                restored.Record.OriginalFileName,
                $"recovery={restored.Record.RecoveryId:N}; category={restored.Record.Category}; path={restored.Record.OriginalRelativePath}; alreadyPresent={restored.AlreadyPresent}");
            _state.NotificaDatiRicaricati();
            Messaggio = restored.AlreadyPresent
                ? T("Lo stesso record era già presente: recovery verificato senza riscriverlo.", "The exact same record was already present. Recovery was verified without rewriting it.", "Le même enregistrement était déjà présent : la récupération a été vérifiée sans le réécrire.")
                : T("Elemento ripristinato e verificato correttamente.", "Item restored and verified successfully.", "Élément restauré et vérifié avec succès.");
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Refresh dopo ripristino recovery-bin", ex);
            MessaggioErrore = T(
                $"L'elemento è stato ripristinato, ma la schermata non ha potuto riallinearsi: {ex.Message}",
                $"The item was restored, but the screen could not be refreshed: {ex.Message}",
                $"L'élément a été restauré, mais l'écran n'a pas pu être actualisé : {ex.Message}");
        }
        Aggiorna();
    }

    /// <summary>Rimuove per sempre la copia di recovery, senza toccare l'eventuale record già ripristinato al
    /// suo percorso originale (feedback utente 2026-09-18: "non capisco perché elimina definitivamente mi
    /// manda la roba in recovery... ho praticamente due archiviazioni e nulla si elimina mai definitivamente"
    /// — questa è l'azione Master esplicita e separata che la classe di servizio richiede per completare il
    /// ciclo, mai automatica di proposito). A differenza di Ripristina, non c'è nulla da verificare dopo: la
    /// copia semplicemente non esiste più.</summary>
    [RelayCommand]
    private Task Purga(RecoveryBinEntryViewModel? row) =>
        ComandoAsincrono.EseguiAsync(() => PurgaCoreAsync(row), "Eliminazione definitiva dal cestino", m => MessaggioErrore = m);

    private async Task PurgaCoreAsync(RecoveryBinEntryViewModel? row)
    {
        if (row == null || !_state.EMaster || !_state.PuoScrivere || RipristinoInCorso) return;

        var confermato = await ConfirmService.Chiedi(
            T("Elimina definitivamente la copia di recovery", "Permanently delete recovery copy", "Supprimer définitivement la copie de récupération"),
            T($"Eliminare per sempre la copia di recovery di {row.Categoria} ('{row.PercorsoOriginale}')?\n\nL'operazione NON è reversibile: dopo, l'elemento cancellato non potrà più essere ripristinato da qui.",
              $"Permanently delete the recovery copy of {row.Categoria} ('{row.PercorsoOriginale}')?\n\nThis CANNOT be undone: after this, the deleted item can no longer be restored from here.",
              $"Supprimer définitivement la copie de récupération de {row.Categoria} ('{row.PercorsoOriginale}') ?\n\nCette opération est IRRÉVERSIBLE : ensuite, l'élément supprimé ne pourra plus être restauré depuis cet écran."),
            T("Elimina definitivamente", "Delete permanently", "Supprimer définitivement"),
            T("Annulla", "Cancel", "Annuler"));
        if (!confermato) return;

        Messaggio = null;
        MessaggioErrore = null;
        try
        {
            DomainRecoveryService.Purge(_state.Paths, row.RecoveryId, _state.Config.OperatoreAttivo);
            _state.RegistraAudit(
                "PurgeDomainRecovery",
                row.PercorsoOriginale,
                $"recovery={row.RecoveryId:N}; category={row.Categoria}");
            Messaggio = T("Copia di recovery eliminata definitivamente.", "Recovery copy permanently deleted.", "Copie de récupération supprimée définitivement.");
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Eliminazione definitiva recovery-bin", ex);
            MessaggioErrore = T($"Eliminazione non eseguita: {ex.Message}", $"Deletion not performed: {ex.Message}", $"Suppression non effectuée : {ex.Message}");
        }
        Aggiorna();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LocalizationService.Lingua) or nameof(LocalizationService.EInglese))) return;
        OnPropertyChanged(nameof(Titolo));
        OnPropertyChanged(nameof(Sottotitolo));
        OnPropertyChanged(nameof(NotaRetention));
        OnPropertyChanged(nameof(AggiornaTesto));
        OnPropertyChanged(nameof(MostraRipristinatiTesto));
        OnPropertyChanged(nameof(NessunElementoTesto));
        OnPropertyChanged(nameof(SoloMasterTesto));
        OnPropertyChanged(nameof(SolaLetturaTesto));
        Aggiorna();
    }

    private void NotificaRiepilogo()
    {
        OnPropertyChanged(nameof(HaElementi));
        OnPropertyChanged(nameof(MostraNessunElemento));
        OnPropertyChanged(nameof(Totale));
        OnPropertyChanged(nameof(Disponibili));
        OnPropertyChanged(nameof(Ripristinati));
        OnPropertyChanged(nameof(RiepilogoTesto));
        OnPropertyChanged(nameof(PuoScrivere));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
    }
}

public partial class RecoveryBinEntryViewModel : ViewModelBase
{
    public DomainRecoveryRecord Record { get; }
    private bool _workspaceWritable;

    public Guid RecoveryId => Record.RecoveryId;
    public bool Ripristinato => Record.RestoredUtc.HasValue;
    public string PercorsoOriginale => Record.OriginalRelativePath;
    public string EliminatoIl => Record.ArchivedUtc == default ? "—" : Record.ArchivedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    public string EliminatoDa => string.IsNullOrWhiteSpace(Record.ArchivedBy) ? "—" : Record.ArchivedBy;
    public string Dispositivo => string.IsNullOrWhiteSpace(Record.Device) ? "—" : Record.Device;
    public string Categoria => Record.Category switch
    {
        DomainRecoveryCategory.TestCase => "Test",
        DomainRecoveryCategory.NonConformita => LocalizationService.Instance.Scegli("Non Conformità", "Non-conformity", "Non-conformité"),
        DomainRecoveryCategory.ChangeRequest => "Change Request",
        DomainRecoveryCategory.Segnalazione => LocalizationService.Instance.Scegli("Segnalazione", "Report to classify", "Signalement à classer"),
        _ => Record.Category.ToString()
    };
    public string Stato => Ripristinato
        ? LocalizationService.Instance.Scegli(
            $"Ripristinato {Record.RestoredUtc!.Value.ToLocalTime():dd/MM/yyyy HH:mm} da {(string.IsNullOrWhiteSpace(Record.RestoredBy) ? "—" : Record.RestoredBy)}",
            $"Restored {Record.RestoredUtc!.Value.ToLocalTime():dd/MM/yyyy HH:mm} by {(string.IsNullOrWhiteSpace(Record.RestoredBy) ? "—" : Record.RestoredBy)}",
            $"Restauré le {Record.RestoredUtc!.Value.ToLocalTime():dd/MM/yyyy HH:mm} par {(string.IsNullOrWhiteSpace(Record.RestoredBy) ? "—" : Record.RestoredBy)}")
        : LocalizationService.Instance.Scegli("Disponibile", "Available", "Disponible");
    public string RipristinaTesto => LocalizationService.Instance.Scegli("Ripristina", "Restore", "Restaurer");
    public bool PuoRipristinare => !Ripristinato && _workspaceWritable;

    /// <summary>Consentita anche su una copia già ripristinata (a differenza di PuoRipristinare): a quel
    /// punto la copia di recovery è solo un backup ormai ridondante col record già tornato al suo posto, e
    /// deve poter comunque essere eliminata per sempre.</summary>
    public string PurgaTesto => LocalizationService.Instance.Scegli("Elimina definitivamente", "Delete permanently", "Supprimer définitivement");
    public bool PuoPurgare => _workspaceWritable;

    public RecoveryBinEntryViewModel(DomainRecoveryRecord record, bool workspaceWritable)
    {
        Record = record;
        _workspaceWritable = workspaceWritable;
    }

    public void NotificaPuoRipristinare(bool workspaceWritable)
    {
        _workspaceWritable = workspaceWritable;
        OnPropertyChanged(nameof(PuoRipristinare));
        OnPropertyChanged(nameof(PuoPurgare));
    }
}
