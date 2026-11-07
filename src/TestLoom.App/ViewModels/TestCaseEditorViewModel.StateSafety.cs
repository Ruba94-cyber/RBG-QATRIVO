using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Guardrail di stato separati dal grande editor principale. Essendo una partial class condivide i membri
/// privati del ViewModel ma mantiene isolate le protezioni che devono rimanere semplici da verificare:
/// - una bozza nuova non diventa autorevole solo perché si allega/esporta un file;
/// - le mutazioni della collezione Allegati marcano sempre il Test come sporco;
/// - la visualizzazione di una REV storica non materializza default moderni nei suoi dizionari;
/// - i salvataggi multi-entità passano da un Unit of Work durevole con rollback/recovery;
/// - anche le modifiche a Test esistenti hanno una bozza locale recuperabile dopo crash.
/// </summary>
public partial class TestCaseEditorViewModel
{
    private bool _stateSafetyHooksInstalled;
    private bool _attachmentTrackingReady;
    private bool _existingDraftRecoveryScheduled;
    private readonly object _existingDraftRecoveryTaskGate = new();
    private Task<EsitoRecuperoBozzaModifica>? _existingDraftRecoveryTask;
    private System.Timers.Timer? _existingEditAutosaveTimer;
    private readonly HashSet<Guid> _allegatiAggiuntiNellaSessione = new();
    // Binari staccati dal Test: restano nel percorso attivo finche' il Salva non persiste il Test senza di essi.
    private readonly AttachmentRemovalQueue _rimozioniAllegati = new();

    private sealed record WorkflowEditorSnapshot(
        List<NonConformita> WorkingNonConformita,
        List<SegnalazioneDaClassificare> WorkingSegnalazioni,
        List<NonConformita> AnomalieNuove,
        List<SegnalazioneDaClassificare> SegnalazioniNuove,
        Dictionary<Guid, long> AnomalieDaAggiornare,
        Dictionary<Guid, long> SegnalazioniDaAggiornare,
        HashSet<Guid> AnomalieDaEliminare,
        Dictionary<Guid, NonConformita> AnomalieCollegate);

    private enum EsitoRecuperoBozzaModifica { Nessuna, Scartata, Ripristinata, DaRitentare }

    /// <summary>Hook generato da CommunityToolkit.Mvvm per [ObservableProperty] TestCase. Viene invocato anche
    /// durante la costruzione dell'editor; qui installiamo gli handler una sola volta. Il tracking allegati e il
    /// recupero crash vengono accodati da CompletaCostruzioneStateSafety, dopo il popolamento iniziale.</summary>
    partial void OnTestCaseChanged(TestCase value)
    {
        if (_stateSafetyHooksInstalled) return;
        _stateSafetyHooksInstalled = true;
        PropertyChanged += OnStateSafetyPropertyChanged;
        AllegatiVisual.CollectionChanged += OnAllegatiVisualCollectionChanged;
        ConfirmService.RegisterConfirmedAbandonmentCleanup(
            this,
            static owner => ((TestCaseEditorViewModel)owner).ScheduleConfirmedAbandonmentCleanup());
    }

    /// <summary>Va chiamato alla fine del costruttore: il partial OnTestCaseChanged si attiva quando il Test
    /// è assegnato, prima che il form sia completamente popolato.</summary>
    private void CompletaCostruzioneStateSafety()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _attachmentTrackingReady = true,
            Avalonia.Threading.DispatcherPriority.Background);

        if (!IsNew && _richiediModificaEsterna == null)
        {
            lock (_existingDraftRecoveryTaskGate)
            {
                if (_existingDraftRecoveryScheduled) return;
                _existingDraftRecoveryScheduled = true;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(
                async () => await EseguiRecuperoBozzaModificaSchedulatoAsync(),
                Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private async Task EseguiRecuperoBozzaModificaSchedulatoAsync()
    {
        try
        {
            if (!_disposto) await AssicuraRecuperoBozzaModificaAsync();
        }
        catch (Exception ex) { CrashLogService.Registra("Recupero bozza modifica Test", ex); }
        finally { CompletaRecuperoBozzaModificaSchedulato(); }
    }

    private void OnAllegatiVisualCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_attachmentTrackingReady) return;

        if (e.NewItems != null)
            foreach (var item in e.NewItems.OfType<Allegato>())
                _allegatiAggiuntiNellaSessione.Add(item.Uid);

        ModificheNonSalvate = true;
    }

    private void OnStateSafetyPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RevisioneVisualizzata) && VisualizzaStorico)
            RimuoviDefaultMaterializzatiDallaRevisioneStorica();

        if (e.PropertyName == nameof(InModifica) && !IsNew)
        {
            if (InModifica) AvviaAutosaveModificaEsistente();
            else
            {
                FermaAutosaveModificaEsistente();
                if (!ModificheNonSalvate && !_existingDraftRecoveryTaskInCorso && TestCase is { } current && current.Uid != Guid.Empty)
                    _state.BozzaAutosave.EliminaModifica(_state.Config.OperatoreAttivo, current.Uid);
            }
        }

        // Prima fotografia subito al passaggio clean->dirty; le modifiche successive, mentre il flag resta
        // true, vengono catturate dal timer. Riduce il tempo massimo di perdita senza scrivere ad ogni tasto.
        if (e.PropertyName == nameof(ModificheNonSalvate) && !IsNew && InModifica && ModificheNonSalvate)
            SalvaBozzaModificaEsistente();

        if (e.PropertyName == nameof(InModifica) && !InModifica && !ModificheNonSalvate)
            _allegatiAggiuntiNellaSessione.Clear();
    }

    /// <summary>
    /// La bozza di recovery serve per crash/failure, non per resuscitare modifiche che l'utente ha scelto
    /// esplicitamente di scartare. La conferma avviene prima che Annulla/navigazione rilascino il lock: catturiamo
    /// quindi il SessionId attuale e al giro UI successivo eliminiamo la bozza SOLO se quella specifica sessione
    /// non possiede più il lock. Altri editor sporchi restano intatti e un crash, non passando dalla conferma,
    /// conserva la recovery.
    /// </summary>
    private void ScheduleConfirmedAbandonmentCleanup()
    {
        if (IsNew || _existingDraftRecoveryTaskInCorso || !InModifica || !ModificheNonSalvate || TestCase.Uid == Guid.Empty ||
            string.IsNullOrWhiteSpace(PercorsoFile) || string.IsNullOrWhiteSpace(_state.Config.OperatoreAttivo)) return;

        var lockBefore = _state.Locks.GetActiveLock(PercorsoFile);
        if (lockBefore == null || string.IsNullOrWhiteSpace(lockBefore.SessionId)) return;

        var path = PercorsoFile;
        var sessionId = lockBefore.SessionId;
        var operatore = _state.Config.OperatoreAttivo;
        var uid = TestCase.Uid;
        var locks = _state.Locks;
        var drafts = _state.BozzaAutosave;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var current = locks.GetActiveLock(path);
            if (current != null && string.Equals(current.SessionId, sessionId, StringComparison.Ordinal)) return;
            drafts.EliminaModifica(operatore, uid);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void AvviaAutosaveModificaEsistente()
    {
        if (IsNew || _existingEditAutosaveTimer != null) return;
        var weak = new WeakReference<TestCaseEditorViewModel>(this);
        var timer = new System.Timers.Timer(TimeSpan.FromSeconds(30).TotalMilliseconds) { AutoReset = true };
        timer.Elapsed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!weak.TryGetTarget(out var vm))
            {
                timer.Stop();
                timer.Dispose();
                return;
            }
            vm.EseguiTickAutosaveModificaEsistente();
        });
        _existingEditAutosaveTimer = timer;
        timer.Start();
    }

    /// <summary>Separa il tick dalla callback del timer per rendere verificabile il caso in cui il tick sia già
    /// in coda sul Dispatcher quando Dispose ferma il timer.</summary>
    private void EseguiTickAutosaveModificaEsistente()
    {
        if (_disposto) return;
        if (InModifica && ModificheNonSalvate) SalvaBozzaModificaEsistente();
    }

    private void FermaAutosaveModificaEsistente()
    {
        var timer = _existingEditAutosaveTimer;
        _existingEditAutosaveTimer = null;
        if (timer == null) return;
        try { timer.Stop(); timer.Dispose(); } catch { }
    }

    private void SalvaBozzaModificaEsistente()
    {
        if (_disposto || IsNew || !InModifica || !ModificheNonSalvate || _snapshot == null || TestCase.Uid == Guid.Empty) return;
        _state.BozzaAutosave.SalvaModifica(_state.Config.OperatoreAttivo, _snapshot, TestCase);
    }

    private bool _existingDraftRecoveryTaskInCorso
    {
        get
        {
            lock (_existingDraftRecoveryTaskGate)
                return _existingDraftRecoveryTask is { IsCompleted: false };
        }
    }

    private Task<EsitoRecuperoBozzaModifica> AssicuraRecuperoBozzaModificaAsync()
    {
        if (_disposto) return Task.FromResult(EsitoRecuperoBozzaModifica.DaRitentare);
        if (IsNew || _richiediModificaEsterna != null)
            return Task.FromResult(EsitoRecuperoBozzaModifica.Nessuna);

        lock (_existingDraftRecoveryTaskGate)
        {
            if (_existingDraftRecoveryTask is { IsCompletedSuccessfully: true } task &&
                task.Result == EsitoRecuperoBozzaModifica.DaRitentare && !_existingDraftRecoveryScheduled)
                _existingDraftRecoveryTask = null;

            return _existingDraftRecoveryTask ??= RecuperaBozzaModificaEsistenteAsync();
        }
    }

    private void CompletaRecuperoBozzaModificaSchedulato()
    {
        lock (_existingDraftRecoveryTaskGate)
        {
            _existingDraftRecoveryScheduled = false;
            if (_existingDraftRecoveryTask is { IsFaulted: true } or { IsCanceled: true })
            {
                // Il callback ha già registrato l'eccezione/cancellazione. Dopo il suo completamento il
                // comando può avviare un nuovo tentativo, senza sovrapporlo al recupero fallito.
                _existingDraftRecoveryTask = null;
                return;
            }

            if (_existingDraftRecoveryTask is { IsCompletedSuccessfully: true } task &&
                task.Result == EsitoRecuperoBozzaModifica.DaRitentare)
                _existingDraftRecoveryTask = null;
        }
    }

    /// <summary>
    /// Recupera una sessione di modifica interrotta. Se la versione autorevole è ancora la base della bozza,
    /// la ripristina direttamente. Se nel frattempo un altro PC ha modificato il Test, un Master passa dal
    /// three-way merge già usato per i conflitti di salvataggio; gli altri ruoli mantengono la bozza intatta.
    /// </summary>
    private async Task<EsitoRecuperoBozzaModifica> RecuperaBozzaModificaEsistenteAsync()
    {
        LockResult? lockRecovery = null;
        var bozzaRipristinata = false;
        try
        {
            if (_disposto || IsNew || _snapshot == null ||
                TestCase.Uid == Guid.Empty || string.IsNullOrWhiteSpace(_state.Config.OperatoreAttivo))
                return EsitoRecuperoBozzaModifica.Nessuna;

            var bozza = _state.BozzaAutosave.CaricaModifica(_state.Config.OperatoreAttivo, TestCase.Uid);
            if (bozza == null) return EsitoRecuperoBozzaModifica.Nessuna;

            // Una bozza identica al JSON attuale è residuo tecnico di un salvataggio già concluso: pulizia silente.
            if (TestCaseFingerprint.Compute(bozza.TestCase).Equals(TestCaseFingerprint.Compute(TestCase), StringComparison.OrdinalIgnoreCase))
            {
                if (_disposto) return EsitoRecuperoBozzaModifica.DaRitentare;
                _state.BozzaAutosave.EliminaModifica(_state.Config.OperatoreAttivo, TestCase.Uid);
                return EsitoRecuperoBozzaModifica.Nessuna;
            }

            var loc = LocalizationService.Instance;
            if (_disposto) return EsitoRecuperoBozzaModifica.DaRitentare;
            var ripristina = await ConfirmService.Chiedi(
                loc.Scegli("Recupera modifiche Test non salvate", "Recover unsaved Test changes", "Récupérer les modifications non enregistrées du Test"),
                loc.Scegli(
                    $"TestLoom ha trovato una copia locale di recupero per {TestCase.Id}, salvata il {bozza.SalvataIl:g}. Ripristinarla? Scegliendo No viene scartata solo la bozza locale; il Test condiviso non viene modificato.",
                    $"TestLoom found a local recovery copy for {TestCase.Id} saved on {bozza.SalvataIl:g}. Recover it? Choosing No discards only the local draft; the shared Test is not changed.",
                    $"TestLoom a trouvé une copie locale de récupération pour {TestCase.Id}, enregistrée le {bozza.SalvataIl:g}. La restaurer ? En choisissant Non, seul le brouillon local est abandonné ; le Test partagé n'est pas modifié."),
                loc.Scegli("Ripristina", "Recover", "Restaurer"),
                loc.Scegli("Scarta bozza", "Discard draft", "Abandonner le brouillon"));
            if (_disposto) return EsitoRecuperoBozzaModifica.DaRitentare;
            if (!ripristina)
            {
                _state.BozzaAutosave.EliminaModifica(_state.Config.OperatoreAttivo, TestCase.Uid);
                return EsitoRecuperoBozzaModifica.Scartata;
            }

            // Il comando Modifica attende questo task condiviso: il recupero passa dal core lock per non
            // invocare ricorsivamente il comando che è già in attesa. In questa fase non abilita ancora
            // l'editing: prima completa la lettura/avviso del lock e poi applica la bozza.
            var avvio = await IniziaModificaCoreAsync(abilitaModifica: false);
            lockRecovery = avvio.Lock;
            if (_disposto || !avvio.Ready) return EsitoRecuperoBozzaModifica.DaRitentare;

            var corrente = await Task.Run(() => _state.TestCases.LoadCurrent(TestCase) ?? Clona(TestCase));
            if (_disposto) return EsitoRecuperoBozzaModifica.DaRitentare;
            var hashCorrente = TestCaseFingerprint.Compute(corrente);
            var baseAncoraCorrente = corrente.RevisioneDati == bozza.RevisioneBase &&
                hashCorrente.Equals(bozza.HashBase, StringComparison.OrdinalIgnoreCase);

            if (baseAncoraCorrente)
            {
                if (_disposto) return EsitoRecuperoBozzaModifica.DaRitentare;
                ApplicaMergeNelForm(EntityStateFingerprint.Clone(bozza.TestCase), corrente);
                ErroriValidazione = new List<string>
                {
                    loc.Scegli("Bozza locale recuperata. Verificala e premi Salva per renderla autorevole.", "Local recovery draft restored. Review it and save to commit it.", "Brouillon local restauré. Vérifiez-le puis enregistrez-le pour le rendre faisant foi.")
                };
                if (_disposto) return EsitoRecuperoBozzaModifica.DaRitentare;
                InModifica = true;
                bozzaRipristinata = true;
                return EsitoRecuperoBozzaModifica.Ripristinata;
            }

            if (!_state.EMaster)
            {
                if (_disposto) return EsitoRecuperoBozzaModifica.DaRitentare;
                ErroriValidazione = new List<string>
                {
                    loc.Scegli(
                        "Il Test condiviso è cambiato dopo la creazione di questa bozza locale. La bozza è stata conservata; serve un Master per riconciliarla prima del commit.",
                        "The shared Test changed after this local draft was created. The draft was preserved; a Master must reconcile it before it can be committed.",
                        "Le Test partagé a changé après la création de ce brouillon local. Le brouillon a été conservé ; un Master doit le réconcilier avant tout enregistrement.")
                };
                return EsitoRecuperoBozzaModifica.DaRitentare;
            }

            var merge = TestCaseThreeWayMerge.Prepara(bozza.BaseTestCase, bozza.TestCase, corrente);
            var scelte = merge.Conflitti.Count == 0
                ? new Dictionary<string, SceltaMerge>()
                : await MergeConflittoDialogService.Risolvi(merge.Conflitti);
            if (_disposto) return EsitoRecuperoBozzaModifica.DaRitentare;
            if (scelte == null)
            {
                ErroriValidazione = new List<string>
                {
                    loc.Scegli("Merge di recupero annullato. La bozza locale è stata conservata.", "Recovery merge cancelled. The local draft was preserved.", "Fusion de récupération annulée. Le brouillon local a été conservé.")
                };
                return EsitoRecuperoBozzaModifica.DaRitentare;
            }

            if (_disposto) return EsitoRecuperoBozzaModifica.DaRitentare;
            ApplicaMergeNelForm(merge.CreaVersioneRisolta(scelte, _state.Config.OperatoreAttivo), corrente);
            ErroriValidazione = new List<string>
            {
                loc.Scegli("Merge di recupero preparato. Verifica il risultato e premi Salva per renderlo autorevole.", "Recovery merge prepared. Review the result and save to commit it.", "Fusion de récupération préparée. Vérifiez le résultat puis enregistrez-le pour le rendre faisant foi.")
            };
            if (_disposto) return EsitoRecuperoBozzaModifica.DaRitentare;
            InModifica = true;
            bozzaRipristinata = true;
            return EsitoRecuperoBozzaModifica.Ripristinata;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Recupero bozza modifica Test", ex);
            try
            {
                if (!_disposto)
                    ErroriValidazione = new List<string>
                    {
                        string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message)
                    };
            }
            catch (Exception uiEx) { CrashLogService.Registra("Visualizzazione errore recupero bozza Test", uiEx); }
            return EsitoRecuperoBozzaModifica.DaRitentare;
        }
        finally
        {
            if (lockRecovery != null && (!bozzaRipristinata || _disposto))
                await ChiudiModificaDopoRecuperoAsync(lockRecovery);
        }
    }

    private async Task ChiudiModificaDopoRecuperoAsync(LockResult? lockRecovery)
    {
        FermaHeartbeat();
        if (InModifica) InModifica = false; // il task condiviso è ancora attivo: l'handler conserva la bozza preesistente.
        if (lockRecovery != null && !string.IsNullOrWhiteSpace(PercorsoFile))
        {
            var percorso = PercorsoFile;
            var esito = await RilasciaLockDelTentativoAsync(lockRecovery, percorso);
            if (esito != EsitoIo.Ok)
                SegnalaRilascioLockInAttesa(lockRecovery, percorso, tornaAllaLista: false);
        }
    }

    /// <summary>
    /// Salvataggio sicuro del Test e di tutti i side-effect prodotti dal normale SalvaCommand.
    /// </summary>
    [RelayCommand]
    private async Task SalvaSicuro()
    {
        if (!_state.CartellaValida || !InModifica)
        {
            await SalvaCommand.ExecuteAsync(null);
            return;
        }

        try { WorkspaceWritePolicy.EnsureCanWrite(_state.Paths, LocalizationService.Instance.Traduci("TcSalvataggioTestOperazioneTesto")); }
        catch (Exception ex)
        {
            ErroriValidazione = new List<string> { ex.Message };
            return;
        }

        // La collezione Esecuzioni e' la fonte di verita' dell'editor (nuova iterazione, wizard di un Test nuovo e
        // operazioni vivono li'); TestCase.Esecuzioni viene allineata solo dentro Salva, DOPO la validazione.
        // Clonare il form prima di questo allineamento faceva ripristinare, dopo una validazione fallita, un form
        // senza le revisioni/operazioni appena create: "le operazioni spariscono al Salva".
        TestCase.Esecuzioni = Esecuzioni.ToList();
        var formPrima = Clona(TestCase);
        var snapshotPrima = _snapshot != null ? Clona(_snapshot) : null;
        var salvatoPrima = _salvatoSuDisco;
        var inModificaPrima = InModifica;
        var dirtyPrima = ModificheNonSalvate;
        var erroriPrecedenti = ErroriValidazione.ToList();
        var workflowPrima = CatturaWorkflowEditor();

        using var workingCopies = WorkflowWorkingCopyScope.Begin(
            workflowPrima.WorkingNonConformita,
            workflowPrima.WorkingSegnalazioni);

        WorkflowTransactionService.WorkflowTransactionScope tx;
        try
        {
            tx = WorkflowTransactionService.Begin(_state.Paths,
                $"save-test:{(string.IsNullOrWhiteSpace(TestCase.Id) ? TestCase.Uid.ToString("N") : TestCase.Id)}");
        }
        catch (Exception ex)
        {
            // Race sicura: un altro PC può pubblicare il proprio marker tra il preflight UI e Begin(). Il
            // salvataggio non è iniziato e quindi non c'è nulla da rollbackare; il form e la bozza restano
            // intatti e l'utente vede l'errore invece di demandarlo al solo crash-log globale.
            ErroriValidazione = erroriPrecedenti.Concat(new[] { ex.Message })
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            try
            {
                if (!IsNew) SalvaBozzaModificaEsistente();
                else _state.BozzaAutosave.Salva(_state.Config.OperatoreAttivo, TestCase);
            }
            catch (Exception bozzaEx)
            {
                // Un disco locale pieno/non scrivibile non deve cancellare il
                // messaggio del conflitto iniziale o trasformare il preflight
                // in un'eccezione non gestita dal comando Avalonia.
                CrashLogService.Registra("Salvataggio bozza locale dopo Begin workflow fallito", bozzaEx);
                ErroriValidazione = ErroriValidazione.Concat(new[]
                {
                    LocalizationService.Instance.Scegli(
                        "Non è stato possibile salvare anche la bozza locale di sicurezza.",
                        "The local safety draft could not be saved either.",
                        "Le brouillon local de sécurité n'a pas pu être enregistré.")
                }).ToList();
            }
            return;
        }

        // Il risultato del SalvaCommand non e' ancora definitivo: il commit della
        // transazione puo' fallire. Non navigare via dall'editor prima del commit.
        bool navigaDopoCommit = false;
        using (tx)
        {
            try
            {
                // Anche il preflight di protezione file puo' fallire (share non
                // disponibile, ACL, marker concorrente): DEVE entrare nello stesso
                // catch e rollback del save, non uscire dall'async command senza
                // messaggio o lasciando il form senza recupero.
                if (salvatoPrima)
                    WorkflowTransactionService.ProtectFile(_state.TestCases.PercorsoPer(TestCase));
                foreach (var uid in _anomalieDaEliminare)
                {
                    // Solo l'Id serve a costruire il percorso da proteggere: PeekAllReadOnly() evita di clonare
                    // l'intero archivio per un solo campo letto e mai mutato (stesso principio di TutteAnomalie()
                    // in CaricaPannelloAnomalie).
                    var nc = _state.NonConformita.PeekAllReadOnly().FirstOrDefault(n => n.Uid == uid);
                    if (nc != null) WorkflowTransactionService.ProtectFile(_state.Paths.NonConformitaFile(nc.Id));
                }

                var soppressionePrimaDelSalva = _sopprimiRitornoAllaLista;
                _sopprimiRitornoAllaLista = true;
                try { await SalvaCommand.ExecuteAsync(null); }
                finally { _sopprimiRitornoAllaLista = soppressionePrimaDelSalva; }

                if (!InModifica && !ModificheNonSalvate)
                {
                    tx.Commit();
                    DiagnosticTraceService.Registra("Test.Workflow",
                        $"save transaction committed: status={TestCase.StatoLavoro}");
                    if (!IsNew && !_existingDraftRecoveryTaskInCorso)
                    {
                        try { _state.BozzaAutosave.EliminaModifica(_state.Config.OperatoreAttivo, formPrima.Uid); }
                        catch (Exception cleanupEx)
                        {
                            // La pulizia della bozza non deve trasformare un commit riuscito in rollback.
                            CrashLogService.Registra("Cleanup bozza post-commit Test", cleanupEx);
                        }
                    }
                    if (!soppressionePrimaDelSalva)
                    {
                        // Se il lock non si e' rilasciato, il coordinatore fa tornare
                        // alla lista SOLO dopo lo sblocco, come nel flusso preesistente.
                        if (RilascioLockInAttesa) _tornaAllaListaDopoRilascio = true;
                        else navigaDopoCommit = true;
                    }
                }
                else
                {
                    tx.Rollback();
                    DiagnosticTraceService.Registra("Test.Workflow",
                        "save transaction rolled back: validation or save did not complete");
                    RipristinaFormDopoRollback(formPrima, snapshotPrima, salvatoPrima, inModificaPrima, dirtyPrima, workflowPrima);
                }
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Test.Workflow: transaction save failed", ex);
                try
                {
                    tx.Rollback();
                    DiagnosticTraceService.Registra("Test.Workflow", "save transaction rolled back: exception");
                }
                catch (Exception rollbackEx)
                {
                    CrashLogService.Registra("Rollback Unit of Work salvataggio Test", rollbackEx);
                    ErroriValidazione = new List<string>
                    {
                        LocalizationService.Instance.Traduci("SafRipristinoIncompletoMsg"),
                        rollbackEx.Message
                    };
                    throw;
                }

                RipristinaFormDopoRollback(formPrima, snapshotPrima, salvatoPrima, inModificaPrima, dirtyPrima, workflowPrima);
                ErroriValidazione = erroriPrecedenti.Concat(new[] { ex.Message }).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
                if (!IsNew) SalvaBozzaModificaEsistente();
            }
        }

        // La pagina viene chiusa solo dopo Dispose della transazione e dello
        // scope di working-copy: la lista non deve leggere overlay temporanei
        // invece dello stato autorevole appena pubblicato. Dispose e' idempotente.
        if (navigaDopoCommit)
        {
            workingCopies.Dispose();
            _tornaAllaLista();
        }
    }

    private WorkflowEditorSnapshot CatturaWorkflowEditor()
    {
        var workingNc = AnomalieTestAperte.Concat(AnomalieTestChiuse)
            .Select(r => r.Nc)
            .Concat(_anomalieNuove)
            .Where(n => n.Uid != Guid.Empty)
            .DistinctBy(n => n.Uid)
            .Select(EntityStateFingerprint.Clone)
            .ToList();
        var workingSeg = SegnalazioniTest
            .Select(r => r.Segnalazione)
            .Concat(_segnalazioniNuove)
            .Where(s => s.Uid != Guid.Empty)
            .DistinctBy(s => s.Uid)
            .Select(EntityStateFingerprint.Clone)
            .ToList();

        return new WorkflowEditorSnapshot(
            workingNc,
            workingSeg,
            _anomalieNuove.Select(EntityStateFingerprint.Clone).ToList(),
            _segnalazioniNuove.Select(EntityStateFingerprint.Clone).ToList(),
            new Dictionary<Guid, long>(_anomalieDaAggiornare),
            new Dictionary<Guid, long>(_segnalazioniDaAggiornare),
            new HashSet<Guid>(_anomalieDaEliminare),
            _anomalieCollegate.ToDictionary(kv => kv.Key, kv => EntityStateFingerprint.Clone(kv.Value)));
    }

    private void RipristinaWorkflowTracking(WorkflowEditorSnapshot state)
    {
        _anomalieNuove.Clear();
        _anomalieNuove.AddRange(state.AnomalieNuove.Select(EntityStateFingerprint.Clone));
        _segnalazioniNuove.Clear();
        _segnalazioniNuove.AddRange(state.SegnalazioniNuove.Select(EntityStateFingerprint.Clone));

        _anomalieDaAggiornare.Clear();
        foreach (var kv in state.AnomalieDaAggiornare) _anomalieDaAggiornare[kv.Key] = kv.Value;
        _segnalazioniDaAggiornare.Clear();
        foreach (var kv in state.SegnalazioniDaAggiornare) _segnalazioniDaAggiornare[kv.Key] = kv.Value;
        _anomalieDaEliminare.Clear();
        foreach (var uid in state.AnomalieDaEliminare) _anomalieDaEliminare.Add(uid);
        _anomalieCollegate.Clear();
        foreach (var kv in state.AnomalieCollegate) _anomalieCollegate[kv.Key] = EntityStateFingerprint.Clone(kv.Value);
    }

    private void RipristinaFormDopoRollback(
        TestCase formPrima,
        TestCase? snapshotPrima,
        bool salvatoPrima,
        bool inModificaPrima,
        bool dirtyPrima,
        WorkflowEditorSnapshot workflowPrima)
    {
        _salvatoSuDisco = salvatoPrima;
        _snapshot = snapshotPrima;
        _salvatiInQuestoSalva.Clear(); // il rollback ha annullato anche le NC/segnalazioni nuove già scritte
        OnPropertyChanged(nameof(MostraNotaPianificazione));
        TestCase = formPrima;
        RipristinaWorkflowTracking(workflowPrima);

        Esecuzioni.Clear();
        foreach (var esecuzione in TestCase.Esecuzioni) Esecuzioni.Add(esecuzione);
        RevisioneVisualizzata = RevisioneCorrente;
        SincronizzaCondizioniVisual();

        AllegatiVisual.Clear();
        foreach (var allegato in TestCase.Allegati) AllegatiVisual.Add(allegato);
        AggiornaEvidenzeOperazioni();

        try { _state.RicaricaTutto(); }
        catch (Exception ex) { CrashLogService.Registra("Ricarica repository dopo rollback Test", ex); }

        CaricaStrumenti();
        CaricaPannelloAnomalie();
        OnPropertyChanged(nameof(TitoloPagina));
        OnPropertyChanged(nameof(NumeroRevisione));
        OnPropertyChanged(nameof(StoricoRevisioni));
        OnPropertyChanged(nameof(ChangelogVuoto));
        OnPropertyChanged(nameof(TestRichiedeEcu));
        OnPropertyChanged(nameof(PreparaPolarionTooltip));
        InModifica = inModificaPrima;
        ModificheNonSalvate = dirtyPrima || inModificaPrima;

        if (IsNew)
            _state.BozzaAutosave.Salva(_state.Config.OperatoreAttivo, TestCase);
    }

    private void RimuoviDefaultMaterializzatiDallaRevisioneStorica()
    {
        if (_snapshot == null || RevisioneVisualizzata == null) return;
        var indice = Esecuzioni.IndexOf(RevisioneVisualizzata);
        if (indice < 0 || indice >= _snapshot.Esecuzioni.Count) return;
        var originale = _snapshot.Esecuzioni[indice];

        RipristinaDizionario(RevisioneVisualizzata.ValoriStrumenti, originale.ValoriStrumenti);

        foreach (var corrente in RevisioneVisualizzata.Condizioni)
        {
            var origine = originale.Condizioni.FirstOrDefault(c => c.Uid == corrente.Uid);
            if (origine != null) RipristinaDizionario(corrente.ValoriStrumenti, origine.ValoriStrumenti);
        }

        foreach (var campo in CampiRevisioneCostanti.Concat(GruppiRevisioneCostanti.SelectMany(g => g.Campi)))
        {
            var chiave = string.IsNullOrEmpty(campo.Strumento) ? campo.Nome : $"{campo.Strumento}::{campo.Nome}";
            if (!originale.ValoriStrumenti.ContainsKey(chiave) && campo.Valore == campo.ValoreDefault)
                campo.Valore = null;
        }
        foreach (var corrente in RevisioneVisualizzata.Condizioni)
        {
            var origine = originale.Condizioni.FirstOrDefault(c => c.Uid == corrente.Uid);
            if (origine == null) continue;
            foreach (var campo in (corrente.CampiVisual ?? new()).Concat((corrente.GruppiVisual ?? new()).SelectMany(g => g.Campi)))
            {
                var chiave = string.IsNullOrEmpty(campo.Strumento) ? campo.Nome : $"{campo.Strumento}::{campo.Nome}";
                if (!origine.ValoriStrumenti.ContainsKey(chiave) && campo.Valore == campo.ValoreDefault)
                    campo.Valore = null;
            }
        }
    }

    private static void RipristinaDizionario(Dictionary<string, string> corrente, Dictionary<string, string> originale)
    {
        foreach (var key in corrente.Keys.Where(k => !originale.ContainsKey(k)).ToList()) corrente.Remove(key);
        foreach (var kv in originale) corrente[kv.Key] = kv.Value;
    }

    private bool AssicuraIdentitaBozzaSenzaPersistenza()
    {
        if (_salvatoSuDisco) return true;
        if (TestCase.Componente.Count == 0)
        {
            ErroriValidazione = new List<string> { LocalizationService.Instance.Traduci("EcuRichiestaPrima") };
            return false;
        }

        if (string.IsNullOrEmpty(TestCase.Id))
        {
            var iniziali = IdGenerator.IniziliDa(_state.Config.OperatoreAttivo);
            TestCase.Id = IdGenerator.Genera(_state.TestCases.GetAll(true).Select(t => t.Id), iniziali);
            OnPropertyChanged(nameof(TitoloPagina));
            OnPropertyChanged(nameof(TestRichiedeEcu));
            OnPropertyChanged(nameof(PreparaPolarionTooltip));
        }
        return true;
    }

    public async Task AggiungiAllegatoSicuro(string? percorsoFile) => await AggiungiAllegatoSicuroCore(percorsoFile);

    /// <summary>Percorso unico di salvataggio allegati del Test, con o senza collegamento a una operazione.
    /// Ritorna l'allegato aggiunto, null se nulla e' stato aggiunto (l'errore e' gia' in ErroriValidazione).</summary>
    private async Task<Allegato?> AggiungiAllegatoSicuroCore(string? percorsoFile)
    {
        if (string.IsNullOrEmpty(percorsoFile) || !_state.CartellaValida) return null;
        if (!AssicuraIdentitaBozzaSenzaPersistenza()) return null;
        try
        {
            var allegato = await AttachmentService.SalvaAsync(_state.Paths, TestCase, percorsoFile, _state.Config.OperatoreAttivo);
            TestCase.Allegati.Add(allegato);
            AllegatiVisual.Add(allegato);
            _allegatiAggiuntiNellaSessione.Add(allegato.Uid);
            ModificheNonSalvate = true;
            if (IsNew) _state.BozzaAutosave.Salva(_state.Config.OperatoreAttivo, TestCase);
            if (allegato.DimensioneByte > AttachmentService.SogliaAvvisoByte)
                Avvisi = Avvisi.Append(string.Format(LocalizationService.Instance.Traduci("AllegatoGrandeFmt"), allegato.NomeFile, allegato.DimensioneByte / 1024 / 1024)).ToList();
            return allegato;
        }
        catch (Exception ex)
        {
            ErroriValidazione = new List<string> { ex.Message };
            return null;
        }
    }

    public async Task CollegaFileCampoStrumentoSicuro(CampoStrumentoValore? campo, string? percorsoFile)
    {
        if (campo == null || string.IsNullOrEmpty(percorsoFile) || !_state.CartellaValida) return;
        if (!AssicuraIdentitaBozzaSenzaPersistenza()) return;
        try
        {
            var allegato = await AttachmentService.SalvaAsync(_state.Paths, TestCase, percorsoFile, _state.Config.OperatoreAttivo);
            TestCase.Allegati.Add(allegato);
            AllegatiVisual.Add(allegato);
            _allegatiAggiuntiNellaSessione.Add(allegato.Uid);
            campo.Valore = allegato.NomeFile;
            ModificheNonSalvate = true;
            if (IsNew) _state.BozzaAutosave.Salva(_state.Config.OperatoreAttivo, TestCase);
        }
        catch (Exception ex)
        {
            ErroriValidazione = new List<string> { ex.Message };
        }
    }

    /// <summary>Vero mentre il pacchetto Ticketing del Test viene generato su un thread di background: il pulsante resta disabilitato.</summary>
    [ObservableProperty] private bool pacchettoInCorso;

    public async Task CreaTicketSicuro(RichiestaPacchettoPolarion? richiesta)
    {
        if (!_state.CartellaValida || richiesta == null || string.IsNullOrWhiteSpace(richiesta.Cartella) || PacchettoInCorso) return;
        if (!AssicuraIdentitaBozzaSenzaPersistenza()) return;
        // La fotografia va acquisita sul thread UI prima di Task.Run: le collezioni dell'editor non sono thread-safe
        // e includono modifiche ancora non salvate che il pacchetto deve comunque rappresentare.
        var snapshot = CreaSnapshotExportPolarion();
        PacchettoInCorso = true;
        try
        {
            var percorso = await Task.Run(() => PolarionPackageService.Crea(_state.Paths, snapshot, _state, richiesta.Cartella, richiesta.Sezioni));
            MessaggioTicket = string.Format(LocalizationService.Instance.Traduci("PacchettoZipProntoFmt"), Path.GetFileName(percorso));
        }
        catch (Exception ex)
        {
            ErroriValidazione = new List<string> { string.Format(LocalizationService.Instance.Traduci("EsportazioneTicketErroreFmt"), ex.Message) };
        }
        finally
        {
            PacchettoInCorso = false;
        }
    }

    public void CreaTestCollegatiSicuro(IReadOnlyList<TestTemplate> templatiDestinazione, IReadOnlyCollection<string> campiDaCopiare)
    {
        // Con modifiche pendenti il salvataggio passa SEMPRE dal normale SalvaSicuro (vedi
        // AssicuraTestSalvatoPerTestCollegato): CreaTestCollegati salva il TestCase senza validazioni.
        if (ModificheNonSalvate)
        {
            ErroriValidazione = new List<string> { LocalizationService.Instance.Traduci("SalvaPrimaDiCollegatoBody") };
            return;
        }

        if (!_salvatoSuDisco)
        {
            ErroriValidazione = new List<string>
            {
                LocalizationService.Instance.Traduci("SafSalvaPrimaTestMsg")
            };
            return;
        }

        try { WorkspaceWritePolicy.EnsureCanWrite(_state.Paths, "Creazione Test collegati"); }
        catch (Exception ex)
        {
            ErroriValidazione = new List<string> { ex.Message };
            return;
        }

        using var tx = WorkflowTransactionService.Begin(_state.Paths, $"create-linked-tests:{TestCase.Id}");
        WorkflowTransactionService.ProtectFile(_state.TestCases.PercorsoPer(TestCase));
        try
        {
            CreaTestCollegati(templatiDestinazione, campiDaCopiare);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            try { _state.RicaricaTutto(); } catch { }
            throw;
        }
    }
}
