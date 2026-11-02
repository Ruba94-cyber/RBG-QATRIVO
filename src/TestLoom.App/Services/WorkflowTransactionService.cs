using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TestLoom.App.Services;

/// <summary>
/// Unit of Work durevole per le operazioni applicative che modificano più JSON autorevoli.
///
/// TestLoom usa file JSON su OneDrive/SharePoint come source of truth: non esiste una transazione filesystem
/// distribuita che possa rendere atomici Test + Segnalazioni + NC + CR. Questo servizio aggiunge quindi una
/// transazione applicativa con before-image locale e marker condiviso. Prima di ogni mutazione il contenuto
/// precedente viene copiato e flushato su disco locale; se una fase successiva fallisce, tutti i file già
/// toccati vengono ripristinati in ordine inverso. Un crash lascia il manifest locale e viene recuperato al
/// successivo avvio dello stesso workspace.
///
/// Il marker pending blocca i client che lo vedono durante il commit; NON è però sufficiente da solo perché
/// OneDrive può consegnare i file in ordine diverso. Le quattro famiglie di record workflow portano quindi
/// TransactionId + DeviceId e il commit viene pubblicato nel ledger bounded del device con gli hash finali.
/// Master data, recovery envelope, audit e altri side-effect possono essere before-image protected, ma non
/// entrano nella receipt di visibilità perché non sono parte del read-model atomico Test/NC/CR/Segnalazioni.
///
/// Dal formato manifest v1 ogni entry distingue inoltre la semplice Capture da una mutazione realmente
/// completata. Il rollback può così ripristinare/cancellare SOLO byte che appartengono ancora alla propria
/// transazione: se un altro processo/PC ha cambiato il path dopo la nostra write/delete, la recovery resta
/// fail-closed in RecoveryRequired senza sovrascrivere o cancellare il contenuto concorrente.
/// </summary>
public static class WorkflowTransactionService
{
    private const int CurrentMutationTrackingVersion = 1;

    /// <summary>Durata del lease del marker condiviso (review P2.4): un marker il cui ultimo battito è più vecchio è scaduto.</summary>
    internal static readonly TimeSpan MarkerLease = TimeSpan.FromSeconds(180);

    /// <summary>Intervallo minimo tra due battiti del proprietario sul marker condiviso (review P2.4).</summary>
    internal static readonly TimeSpan MarkerHeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly AsyncLocal<WorkflowTransaction?> Current = new();
    private static readonly AsyncLocal<int> InternalIoDepth = new();

    // Contatore di processo (non AsyncLocal): Current/IsActive valgono solo per il flusso async/thread che ha
    // aperto la transazione, quindi non bastano a un osservatore su un thread DIVERSO (es. il backup giornaliero
    // in background, che gira sul proprio Thread dedicato) per sapere se una Unit of Work è in corso altrove nel
    // processo, tipicamente sul thread UI durante un Salva. IsActiveAnywhere risponde a questa domanda diversa:
    // non sostituisce IsActive negli usi esistenti (nested-transaction guard, prompt prepare, ecc.), è pensato
    // solo per un preflight cross-thread "posso partire ora o conviene rinviare".
    private static int _globalActiveCount;

    // Transazioni ancora in corso in questo processo (da Begin fino al completamento di commit/rollback). La recovery le salta:
    // una riverifica concorrente (es. Begin di un altro thread) non deve annullare il lavoro di una transazione viva.
    private static readonly ConcurrentDictionary<Guid, byte> _attive = new();

    public static bool IsActive => Current.Value != null;
    public static Guid? ActiveId => Current.Value?.Manifest.Id;
    public static bool IsActiveAnywhere => Volatile.Read(ref _globalActiveCount) > 0;

    public static WorkflowTransactionScope Begin(DataPaths paths, string description)
    {
        if (Current.Value != null)
            throw new InvalidOperationException("Esiste già una transazione workflow attiva nello stesso flusso.");

        // Riprova il recupero delle transazioni locali rimaste pendenti: finché la ricevuta di una di esse non è verificabile
        // le scritture restano bloccate e il motivo esatto viene riportato a chi salva.
        RecoverPending(paths);
        SharedWorkflowTransactionGuard.EnsureNoBlockingTransaction(paths, "Avvio salvataggio workflow");

        var tx = WorkflowTransaction.Create(paths, description);
        Current.Value = tx;
        _attive.TryAdd(tx.Manifest.Id, 0);
        Interlocked.Increment(ref _globalActiveCount);
        return new WorkflowTransactionScope(tx);
    }

    public static void Execute(DataPaths paths, string description, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsActive)
        {
            action();
            return;
        }

        using var tx = Begin(paths, description);
        action();
        tx.Commit();
    }

    public static T Execute<T>(DataPaths paths, string description, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsActive) return action();

        using var tx = Begin(paths, description);
        var result = action();
        tx.Commit();
        return result;
    }

    internal static void BeforeWrite(string path)
    {
        if (InternalIoDepth.Value > 0) return;
        var tx = Current.Value;
        if (tx == null || !tx.Manages(path)) return;
        tx.Capture(path);
    }

    /// <summary>
    /// Decisione 6.4.2 (docs/PIANO_6.4.2.md §1): i JSON di dominio ordinari non vengono più stampati con
    /// _ditmTx/_ditmTxDevice. Stampare quei marker li rendeva leggibili solo dopo che la receipt cross-file si
    /// assestava (WorkflowTransactionVisibilityService.VerifyReceiptSettled, rimosso) — bastava che un ALTRO
    /// file della stessa transazione venisse legittimamente riscritto da un salvataggio successivo perché la
    /// receipt non si assestasse più "per sempre", con il file bloccato "in attesa di sincronizzazione" anche a
    /// PC singolo. Un JSON di dominio sintatticamente valido è ora sempre leggibile: i byte vengono scritti
    /// as-is. La Unit of Work locale (before-image/rollback, RecordFinal/BuildCommitReceipt più sotto) e il
    /// ledger dei commit restano solo per la crash-recovery (VerificaRicevutaCommit/RecoverPending), non più
    /// per un gate di lettura cross-file. Le delete transazionali restano tombstone (vedi DeleteFile/CreateTombstone
    /// sotto): un tombstone è un'eliminazione logica senza bisogno di receipt.
    /// </summary>
    internal static byte[] PrepareJsonWrite(string path, ReadOnlySpan<byte> bytes) => bytes.ToArray();

    /// <summary>
    /// Registra la write completata per QUALUNQUE path gestito dalla transazione, non soltanto per i quattro
    /// record visibility-managed. L'hash finale è necessario anche ai master/recovery/side-effect per sapere
    /// se il rollback possiede ancora i byte che sta per ripristinare. BuildCommitReceipt filtra comunque i
    /// soli path visibility-managed, quindi il protocollo condiviso delle receipt non cambia.
    /// </summary>
    internal static void AfterWrite(string path, ReadOnlySpan<byte> bytes)
    {
        if (InternalIoDepth.Value > 0) return;
        var tx = Current.Value;
        if (tx == null || !tx.Manages(path)) return;
        try { tx.RecordFinal(path, WorkflowTransactionVisibilityService.Hash(bytes), logicallyDeleted: false); }
        catch (Exception ex)
        {
            // La scrittura è già avvenuta sul disco: un errore di registrazione non la annulla e non deve farla risultare fallita
            // al chiamante. La transazione diventa non committabile: Commit rifiuta e il rollback ripristina il before-image.
            tx.MarkFault(ex);
            CrashLogService.Registra($"Registrazione della mutazione transazionale: {path}", ex);
        }
    }

    /// <summary>
    /// Intenzione registrata prima dello spostamento atomico (review P1.9): l'hash atteso permette alla recovery di distinguere,
    /// dopo un crash tra File.Move e la registrazione finale, una mutazione avvenuta da una non avvenuta. Un errore qui precede
    /// la mutazione: il file è intatto e la scrittura fallisce normalmente.
    /// </summary>
    internal static void BeforeMove(string path, ReadOnlySpan<byte> bytes)
    {
        if (InternalIoDepth.Value > 0) return;
        var tx = Current.Value;
        if (tx == null || !tx.Manages(path)) return;
        tx.RecordWriteIntent(path, WorkflowTransactionVisibilityService.Hash(bytes));
    }

    public static void ProtectFile(string path)
    {
        if (InternalIoDepth.Value > 0) return;
        var tx = Current.Value;
        if (tx == null || !tx.Manages(path)) return;
        tx.Capture(path);
    }

    /// <summary>
    /// Le delete dei quattro record di dominio diventano tombstone visibility-managed. Altri JSON eventualmente
    /// cancellati dentro la Unit of Work restano semplici delete fisiche, ma la before-image ne consente il rollback.
    /// Una delete obbligatoria fallita rende inoltre la transazione non committabile: alcuni repository storici
    /// intercettano errori di cleanup per poterli mostrare in diagnostica, ma dentro una Unit of Work quel catch
    /// non deve mai trasformare una rimozione fallita in un commit parziale con vecchio+nuovo path entrambi visibili.
    /// </summary>
    public static void DeleteFile(string path)
    {
        var tx = Current.Value;
        try
        {
            if (InternalIoDepth.Value == 0 && tx != null && tx.Manages(path) && tx.RequiresVisibility(path))
            {
                if (!File.Exists(path)) return;
                var original = File.ReadAllBytes(path);
                tx.Capture(path);
                var tombstone = WorkflowTransactionVisibilityService.CreateTombstone(
                    tx.Manifest.Id,
                    tx.Manifest.DeviceId,
                    Path.GetFileName(path),
                    original);
                // Intenzione registrata prima dello spostamento del tombstone (review P1.9).
                tx.RecordWriteIntent(path, WorkflowTransactionVisibilityService.Hash(tombstone));
                InternalIoDepth.Value++;
                try { WriteBytesDurable(path, tombstone); }
                finally { InternalIoDepth.Value--; }
                tx.RecordFinal(path, WorkflowTransactionVisibilityService.Hash(tombstone), logicallyDeleted: true);
                return;
            }

            ProtectFile(path);
            if (File.Exists(path))
            {
                // Intenzione registrata prima della delete fisica (review P1.9): dopo un crash la recovery ripristina il file.
                if (InternalIoDepth.Value == 0 && tx != null && tx.Manages(path)) tx.RecordDeleteIntent(path);
                File.Delete(path);
                // Una delete fisica è considerata completata soltanto se il path è realmente sparito. Se un
                // watcher/replica lo ricrea immediatamente non possiamo rivendicarne l'ownership nel rollback.
                if (File.Exists(path))
                    throw new IOException($"Il file '{Path.GetFileName(path)}' è ricomparso durante l'eliminazione transazionale.");
                if (InternalIoDepth.Value == 0 && tx != null && tx.Manages(path))
                    tx.RecordPhysicalDelete(path);
            }
        }
        catch (Exception ex)
        {
            if (InternalIoDepth.Value == 0 && tx != null && tx.Manages(path))
                tx.MarkFault(ex);
            throw;
        }
    }

    public static bool DeferAfterCommit(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var tx = Current.Value;
        if (tx == null || !tx.CanDefer) return false;
        tx.DeferCommit(action);
        return true;
    }

    public static bool DeferAfterRollback(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var tx = Current.Value;
        if (tx == null || !tx.CanDefer) return false;
        tx.DeferRollback(action);
        return true;
    }

    public static void RecoverPending(DataPaths paths)
    {
        foreach (var txDir in TransactionDirectoriesForRecovery(paths.Root))
        {
            try
            {
                var manifestPath = Path.Combine(txDir, "manifest.json");
                var manifest = ReadManifest(manifestPath)
                    ?? throw new InvalidDataException($"Manifest di recovery illeggibile: {manifestPath}");
                if (!SameRoot(manifest.WorkspaceRoot, paths.Root)
                    && !string.Equals(WorkspaceLocalPaths.ComputeWorkspaceKey(manifest.WorkspaceRoot),
                        WorkspaceLocalPaths.ComputeWorkspaceKey(paths.Root), StringComparison.Ordinal))
                    throw new InvalidDataException("La recovery area non appartiene al workspace atteso.");

                // Transazione ancora in corso in questo processo (un altro thread): non è una recovery.
                if (_attive.ContainsKey(manifest.Id)) continue;

                // Il chiamante può essere un altro processo: in quel caso _attive è vuoto. Non ripristinare
                // il before-image mentre il proprietario conserva un marker con lease ancora valido.
                SharedWorkflowTransactionGuard.EnsureRecoveryMarkerNotLive(paths, manifest.Id, manifest.DeviceId);
                SharedWorkflowTransactionGuard.EnsureNoBlockingTransaction(
                    paths, "Recovery transazione workflow", manifest.Id);

                var tx = WorkflowTransaction.FromExisting(paths, txDir, manifest);
                // Il manifest Committed è scritto solo dopo la pubblicazione della ricevuta. Altrimenti decide Rollback,
                // che interroga il ledger: mai ripristino su esito incerto.
                if (manifest.State == WorkflowTransactionState.Committed)
                    tx.CleanupCommitted();
                else
                    tx.Rollback();
            }
            catch (WorkflowVerificaPendenteException)
            {
                // Esito indeterminato: nessun file è stato toccato, il messaggio è già localizzato.
                throw;
            }
            catch (Exception ex)
            {
                CrashLogService.Registra($"Recovery transazione workflow: {txDir}", ex);
                throw new InvalidDataException("È presente un salvataggio TestLoom interrotto che non è stato possibile ripristinare automaticamente. Scrittura bloccata per evitare perdita di stato.", ex);
            }
        }
    }

    public sealed class WorkflowTransactionScope : IDisposable
    {
        private WorkflowTransaction? _tx;
        private bool _completed;
        private bool _globalActiveReleased;
        private readonly Guid _id;

        internal WorkflowTransactionScope(WorkflowTransaction tx)
        {
            _tx = tx;
            _id = tx.Manifest.Id;
        }

        public Guid Id => _tx?.Manifest.Id ?? Guid.Empty;

        private void ReleaseGlobalActive()
        {
            if (_globalActiveReleased) return;
            _globalActiveReleased = true;
            _attive.TryRemove(_id, out _);
            Interlocked.Decrement(ref _globalActiveCount);
        }

        public void Commit()
        {
            if (_completed || _tx == null) return;
            // Un errore durante il commit non viene interpretato qui: se la pubblicazione è avvenuta, la ricevuta nel ledger
            // lo dirà a Rollback (vedi WorkflowTransaction.Rollback), che non ripristina mai su esito incerto. Il Dispose che
            // segue può sostituire l'eccezione originale, quindi la causa viene registrata prima.
            try
            {
                _tx.Commit();
                _completed = true;
            }
            catch (Exception ex)
            {
                CrashLogService.Registra($"Commit transazione workflow {_id:N}", ex);
                throw;
            }
            finally
            {
                if (_completed)
                {
                    if (ReferenceEquals(Current.Value, _tx)) Current.Value = null;
                    _tx = null;
                    ReleaseGlobalActive();
                }
            }
        }

        public void Rollback()
        {
            if (_completed || _tx == null) return;
            try { _tx.Rollback(); }
            finally
            {
                _completed = true;
                if (ReferenceEquals(Current.Value, _tx)) Current.Value = null;
                _tx = null;
                ReleaseGlobalActive();
            }
        }

        public void Dispose()
        {
            if (!_completed) Rollback();
        }
    }

    internal sealed class WorkflowTransaction
    {
        private readonly DataPaths _paths;
        private readonly string _transactionDirectory;
        private readonly string _manifestPath;
        private readonly string _sharedMarkerPath;
        private readonly HashSet<string> _captured;
        private readonly List<Action> _afterCommit = new();
        private readonly List<Action> _afterRollback = new();
        private Exception? _fault;
        private bool _finished;
        private bool _committing;

        // true se la pubblicazione della ricevuta è avvenuta o potrebbe essere avvenuta. Senza tentativo la ricevuta non può
        // esistere e l'assenza è certa senza leggere il ledger; con tentativo l'esito è quello della ricevuta nel ledger.
        private bool _pubblicazioneTentata;

        // Ultimo battito scritto nel marker condiviso. Resta null finché il marker non è scritto da questa istanza (es. recovery).
        private DateTime? _ultimoBattitoUtc;

        public WorkflowTransactionManifest Manifest { get; }
        public bool CanDefer => !_finished && !_committing;

        private WorkflowTransaction(DataPaths paths, string transactionDirectory, WorkflowTransactionManifest manifest, bool pubblicazioneTentata)
        {
            _paths = paths;
            _transactionDirectory = transactionDirectory;
            _manifestPath = Path.Combine(transactionDirectory, "manifest.json");
            _sharedMarkerPath = SharedMarkerPath(paths.Root, manifest.Id);
            Manifest = manifest;
            _captured = manifest.Entries.Select(e => NormalizePath(e.Path)).ToHashSet(PathComparer);
            _pubblicazioneTentata = pubblicazioneTentata;
        }

        public static WorkflowTransaction Create(DataPaths paths, string description)
        {
            var id = Guid.NewGuid();
            var txDir = Path.Combine(WorkspaceDirectory(paths.Root), $"tx-{id:N}");
            var preparationDir = Path.Combine(WorkspaceDirectory(paths.Root), $"prepare-{id:N}");
            Directory.CreateDirectory(preparationDir);
            var manifest = new WorkflowTransactionManifest
            {
                Id = id,
                DeviceId = DeviceIdentityService.GetOrCreate(),
                WorkspaceRoot = Path.GetFullPath(paths.Root),
                Description = description,
                StartedUtc = DateTime.UtcNow,
                State = WorkflowTransactionState.Prepared,
                MutationTrackingVersion = CurrentMutationTrackingVersion
            };
            var preparation = new WorkflowTransaction(paths, preparationDir, manifest, pubblicazioneTentata: false);

            try
            {
                preparation.PersistManifest();
                preparation.WriteSharedMarker();
                Directory.Move(preparationDir, txDir);

                var tx = new WorkflowTransaction(paths, txDir, manifest, pubblicazioneTentata: false)
                {
                    _ultimoBattitoUtc = preparation._ultimoBattitoUtc
                };
                SharedWorkflowTransactionGuard.EnsureNoBlockingTransaction(paths, "Avvio salvataggio workflow", id);
                return tx;
            }
            catch
            {
                if (Directory.Exists(txDir))
                    new WorkflowTransaction(paths, txDir, manifest, pubblicazioneTentata: false).AbortPrepared();
                else
                    preparation.AbortPrepared();
                throw;
            }
        }

        public static WorkflowTransaction FromExisting(DataPaths paths, string txDir, WorkflowTransactionManifest manifest)
        {
            if (string.IsNullOrWhiteSpace(manifest.DeviceId))
                manifest.DeviceId = DeviceIdentityService.GetOrCreate();
            // Dopo un crash non si sa se la pubblicazione è avvenuta. Può essere tentata solo se esistono entry pubblicabili
            // (BuildCommitReceipt include solo quelle): in quel caso la ricevuta va verificata nel ledger.
            var pubblicabili = manifest.Entries.Any(e =>
                !string.IsNullOrWhiteSpace(e.FinalSha256) &&
                TestLoomStorageOwnership.IsVisibilityManagedDomainJsonPath(paths.Root, e.Path));
            return new WorkflowTransaction(paths, txDir, manifest, pubblicazioneTentata: pubblicabili);
        }

        public bool Manages(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var root = Path.GetFullPath(_paths.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return full.StartsWith(root, PathComparison) && !IsTransactionTechnicalPath(full);
            }
            catch { return false; }
        }

        public bool RequiresVisibility(string path) =>
            TestLoomStorageOwnership.IsVisibilityManagedDomainJsonPath(_paths.Root, path);

        public void Capture(string path)
        {
            if (_finished || _committing) throw new InvalidOperationException("Transazione workflow non più modificabile.");
            var full = Path.GetFullPath(path);
            var normalized = NormalizePath(full);
            if (!_captured.Add(normalized)) return;

            var entry = new WorkflowTransactionEntry
            {
                Path = full,
                Existed = File.Exists(full),
                BackupFile = $"before-{Manifest.Entries.Count:D4}.bin"
            };

            if (entry.Existed)
            {
                var backup = Path.Combine(_transactionDirectory, entry.BackupFile);
                CopyDurable(full, backup);
                entry.BeforeSha256 = WorkflowTransactionVisibilityService.Hash(File.ReadAllBytes(backup));
            }

            Manifest.Entries.Add(entry);
            Manifest.State = WorkflowTransactionState.Applying;
            PersistManifest();
        }

        public void RecordFinal(string path, string sha256, bool logicallyDeleted)
        {
            if (_finished || _committing) throw new InvalidOperationException("Transazione workflow non più modificabile.");
            var full = Path.GetFullPath(path);
            var entry = FindOrCapture(full);
            entry.FinalSha256 = sha256;
            entry.LogicallyDeleted = logicallyDeleted;
            entry.PhysicallyDeleted = false;
            entry.MutationCompleted = true;
            entry.IntendedSha256 = null;
            entry.IntendedDelete = false;
            PersistManifest();
        }

        public void RecordPhysicalDelete(string path)
        {
            if (_finished || _committing) throw new InvalidOperationException("Transazione workflow non più modificabile.");
            var full = Path.GetFullPath(path);
            var entry = FindOrCapture(full);
            entry.FinalSha256 = null;
            entry.LogicallyDeleted = false;
            entry.PhysicallyDeleted = true;
            entry.MutationCompleted = true;
            entry.IntendedSha256 = null;
            entry.IntendedDelete = false;
            PersistManifest();
        }

        /// <summary>
        /// Intenzione di scrittura registrata PRIMA di File.Move (review P1.9). Il rollback la usa se la conferma finale manca.
        /// </summary>
        public void RecordWriteIntent(string path, string sha256Atteso)
        {
            if (_finished || _committing) throw new InvalidOperationException("Transazione workflow non più modificabile.");
            var full = Path.GetFullPath(path);
            var entry = FindOrCapture(full);
            entry.IntendedSha256 = sha256Atteso;
            entry.IntendedDelete = false;
            PersistManifest();
        }

        /// <summary>
        /// Intenzione di eliminazione fisica registrata PRIMA di File.Delete (review P1.9).
        /// </summary>
        public void RecordDeleteIntent(string path)
        {
            if (_finished || _committing) throw new InvalidOperationException("Transazione workflow non più modificabile.");
            var full = Path.GetFullPath(path);
            var entry = FindOrCapture(full);
            entry.IntendedSha256 = null;
            entry.IntendedDelete = true;
            PersistManifest();
        }

        private WorkflowTransactionEntry FindOrCapture(string full)
        {
            var normalized = NormalizePath(full);
            var entry = Manifest.Entries.LastOrDefault(e => NormalizePath(e.Path).Equals(normalized, PathComparison));
            if (entry != null) return entry;
            Capture(full);
            return Manifest.Entries.Last(e => NormalizePath(e.Path).Equals(normalized, PathComparison));
        }

        public void MarkFault(Exception ex)
        {
            ArgumentNullException.ThrowIfNull(ex);
            Interlocked.CompareExchange(ref _fault, ex, null);
        }

        public void DeferCommit(Action action)
        {
            if (!CanDefer) throw new InvalidOperationException("Il commit della transazione è già iniziato.");
            _afterCommit.Add(action);
        }

        public void DeferRollback(Action action)
        {
            if (!CanDefer) throw new InvalidOperationException("Il commit/rollback della transazione è già iniziato.");
            _afterRollback.Add(action);
        }

        public void Commit()
        {
            if (_finished) return;
            _committing = true;
            WorkflowTransactionCommitReceipt receipt;
            try
            {
                if (_fault != null)
                    throw new IOException(
                        "La transazione TestLoom contiene una mutazione obbligatoria fallita. Commit bloccato: verrà eseguito il rollback per preservare lo stato precedente.",
                        _fault);

                receipt = BuildCommitReceipt();
                if (receipt.Entries.Count > 0)
                {
                    // Punto di commit: la ricevuta pubblicata nel ledger è l'unica prova del commit. Un errore qui non dimostra che la
                    // ricevuta manchi: l'esito lo decide la verifica nel ledger (vedi Rollback), non questo metodo.
                    _pubblicazioneTentata = true;
                    WorkflowCommitLedgerService.Publish(_paths, receipt);
                }
                else
                {
                    // Senza record visibility-managed non esiste ricevuta: il commit è il manifest Committed.
                    Manifest.State = WorkflowTransactionState.Committed;
                    Manifest.CompletedUtc = receipt.CompletedUtc;
                    PersistManifest();
                }
            }
            catch
            {
                _committing = false;
                throw;
            }

            CompletaCommit(receipt.CompletedUtc);
        }

        /// <summary>
        /// Passi successivi al commit avvenuto (ricevuta pubblicata oppure manifest committed). Non riportano errori come commit
        /// fallito: la ricevuta nel ledger è già l'esito autorevole e i passi residui vengono completati dalla recovery.
        /// Le azioni differite partono una sola volta.
        /// </summary>
        private void CompletaCommit(DateTime completedUtc)
        {
            if (Manifest.State != WorkflowTransactionState.Committed)
            {
                Manifest.State = WorkflowTransactionState.Committed;
                Manifest.CompletedUtc = completedUtc;
                try { PersistManifest(); }
                catch (Exception ex) { CrashLogService.Registra($"Manifest committed transazione {Manifest.Id:N}", ex); }
            }

            // La receipt rende il dominio verificabile senza più bisogno del marker pending.
            var markerRimosso = true;
            try { DeleteTechnicalFile(_sharedMarkerPath); }
            catch (Exception ex)
            {
                markerRimosso = false;
                CrashLogService.Registra($"Pulizia marker transazione committed {Manifest.Id:N}", ex);
            }

            if (ReferenceEquals(Current.Value, this)) Current.Value = null;
            foreach (var action in _afterCommit)
            {
                try { action(); }
                catch (Exception ex) { CrashLogService.Registra($"Effetto post-commit transazione {Manifest.Id:N}", ex); }
            }
            _afterCommit.Clear();

            // Con il marker ancora presente la directory resta: la recovery riprova la pulizia senza ripristinare nulla.
            if (markerRimosso) DeleteDirectoryBestEffort(_transactionDirectory);
            _finished = true;
        }

        private WorkflowTransactionCommitReceipt BuildCommitReceipt()
        {
            var completedUtc = DateTime.UtcNow;
            var entries = Manifest.Entries
                .Where(e => !string.IsNullOrWhiteSpace(e.FinalSha256) && RequiresVisibility(e.Path))
                .Select(e => new WorkflowTransactionCommitEntry
                {
                    RelativePath = WorkflowTransactionVisibilityService.NormalizeRelativePath(_paths.Root, e.Path),
                    FinalSha256 = e.FinalSha256!,
                    LogicallyDeleted = e.LogicallyDeleted
                })
                .OrderBy(e => e.RelativePath, PathComparer)
                .ToList();

            return new WorkflowTransactionCommitReceipt
            {
                Id = Manifest.Id,
                StartedUtc = Manifest.StartedUtc,
                CompletedUtc = completedUtc,
                DeviceId = Manifest.DeviceId,
                Device = Environment.MachineName,
                Description = Manifest.Description,
                Entries = entries
            };
        }

        /// <summary>
        /// Esito autorevole del punto di commit: unica base per il ripristino. Un errore di lettura del ledger produce
        /// Sconosciuto, mai "ricevuta assente".
        /// </summary>
        public EsitoRicevutaCommit VerificaRicevutaCommit(out string? dettaglio)
        {
            if (!_pubblicazioneTentata)
            {
                dettaglio = null;
                return EsitoRicevutaCommit.AssenteCertificata;
            }
            return WorkflowCommitLedgerService.VerificaRicevuta(_paths, Manifest.DeviceId, Manifest.Id, out dettaglio);
        }

        public void CleanupCommitted()
        {
            DeleteTechnicalFile(_sharedMarkerPath);
            DeleteDirectoryBestEffort(_transactionDirectory);
            _finished = true;
        }

        public void Rollback()
        {
            if (_finished) return;

            var esito = VerificaRicevutaCommit(out var dettaglio);
            if (esito == EsitoRicevutaCommit.Committed)
            {
                // Ricevuta nel ledger: il commit è avvenuto. Nessuna before-image va ripristinata; i passi post-commit si completano.
                CompletaCommit(DateTime.UtcNow);
                return;
            }
            if (esito == EsitoRicevutaCommit.Sconosciuto)
            {
                // Esito indeterminato: la ricevuta potrebbe esistere in un ledger non leggibile e annullare le scritture
                // perderebbe un commit riuscito. Nessun file, manifest o marker viene toccato: il prossimo tentativo riverifica.
                throw new WorkflowVerificaPendenteException(dettaglio);
            }

            _committing = false;
            Manifest.State = WorkflowTransactionState.RollingBack;
            PersistManifest();

            Exception? firstError = null;
            InternalIoDepth.Value++;
            try
            {
                foreach (var entry in Manifest.Entries.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (Manifest.MutationTrackingVersion >= CurrentMutationTrackingVersion)
                            RollbackTrackedEntry(entry);
                        else
                            RollbackLegacyEntryConservatively(entry);
                    }
                    catch (Exception ex)
                    {
                        firstError ??= ex;
                        CrashLogService.Registra($"Rollback transazione {Manifest.Id:N}: {entry.Path}", ex);
                    }
                }
            }
            finally { InternalIoDepth.Value--; }

            if (firstError != null)
            {
                Manifest.State = WorkflowTransactionState.RecoveryRequired;
                PersistManifest();
                throw new IOException("Rollback del salvataggio TestLoom incompleto. La recovery area è stata conservata e la scrittura deve restare bloccata fino al ripristino.", firstError);
            }

            foreach (var action in _afterRollback)
            {
                try { action(); }
                catch (Exception ex) { CrashLogService.Registra($"Effetto post-rollback transazione {Manifest.Id:N}", ex); }
            }

            Manifest.State = WorkflowTransactionState.RolledBack;
            Manifest.CompletedUtc = DateTime.UtcNow;
            PersistManifest();
            DeleteTechnicalFile(_sharedMarkerPath);
            DeleteDirectoryBestEffort(_transactionDirectory);
            _finished = true;
        }

        private void RollbackTrackedEntry(WorkflowTransactionEntry entry)
        {
            // Intenzione registrata prima dello spostamento o della delete senza conferma finale (review P1.9): decide il contenuto reale.
            if (entry.IntendedSha256 != null || entry.IntendedDelete)
            {
                RollbackIntentoPendente(entry);
                return;
            }

            // Capture da sola non prova che TestLoom abbia toccato il target. È il caso cruciale di una WriteAtomic
            // create-only fallita perché un altro PC ha creato il file tra Capture e File.Move: non dobbiamo
            // cancellare quel file nel tentativo di "annullare" una mutazione che non è mai avvenuta.
            if (!entry.MutationCompleted) return;

            if (entry.PhysicallyDeleted)
            {
                // La nostra delete è ancora l'ultimo stato solo se il path è rimasto assente. Una ricreazione
                // concorrente appartiene a qualcun altro e non può essere sovrascritta dalla before-image.
                if (File.Exists(entry.Path))
                    throw new IOException($"Rollback bloccato: '{Path.GetFileName(entry.Path)}' è stato ricreato dopo la delete della transazione.");
                if (entry.Existed) RestoreBeforeImage(entry);
                return;
            }

            if (string.IsNullOrWhiteSpace(entry.FinalSha256))
                throw new InvalidDataException($"Manifest transazionale incompleto per '{Path.GetFileName(entry.Path)}': hash finale assente.");
            if (!File.Exists(entry.Path))
                throw new IOException($"Rollback bloccato: '{Path.GetFileName(entry.Path)}' non è più presente dopo la write della transazione.");

            var currentHash = HashCurrent(entry.Path);
            if (!string.Equals(currentHash, entry.FinalSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Rollback bloccato: '{Path.GetFileName(entry.Path)}' è stato modificato da un'altra postazione dopo la write TestLoom.");

            if (entry.Existed) RestoreBeforeImage(entry);
            else File.Delete(entry.Path); // hash provato: stiamo eliminando esattamente il file creato da noi.
        }

        /// <summary>
        /// Intenzione registrata prima dello spostamento o della delete senza conferma finale (review P1.9): il processo può essere
        /// caduto dopo l'operazione sul file. Si decide sul contenuto reale. Il contenuto prodotto da noi (intenzione o ultima mutazione
        /// confermata) viene annullato; il before-image, o l'assenza originale, significa che la mutazione non è avvenuta; qualunque
        /// altro contenuto blocca la recovery senza toccare il file.
        /// </summary>
        private void RollbackIntentoPendente(WorkflowTransactionEntry entry)
        {
            var nome = Path.GetFileName(entry.Path);
            var presente = File.Exists(entry.Path);
            var hash = presente ? HashCurrent(entry.Path) : null;
            var nostro = hash != null &&
                (string.Equals(hash, entry.IntendedSha256, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(hash, entry.FinalSha256, StringComparison.OrdinalIgnoreCase));

            if (nostro)
            {
                if (entry.Existed) RestoreBeforeImage(entry);
                else File.Delete(entry.Path); // hash provato: stiamo eliminando esattamente il file prodotto da noi.
                return;
            }

            if (presente && entry.Existed && string.Equals(hash, entry.BeforeSha256, StringComparison.OrdinalIgnoreCase))
                return; // mutazione non avvenuta: il contenuto è già l'originale.

            if (!presente)
            {
                if (!entry.Existed) return; // non esisteva prima e non esiste ora.
                if (entry.IntendedDelete)
                {
                    RestoreBeforeImage(entry); // eliminazione avvenuta: si riporta il before-image.
                    return;
                }
                throw new IOException($"Rollback bloccato: '{nome}' non è più presente dopo l'intenzione registrata dalla transazione.");
            }

            // Creato da un altro PC dopo la nostra Capture (create-only fallito): non è nostro e non va toccato.
            if (!entry.Existed && entry.FinalSha256 == null) return;
            throw new IOException($"Rollback bloccato: '{nome}' contiene un contenuto non riconducibile alla transazione.");
        }

        private void RollbackLegacyEntryConservatively(WorkflowTransactionEntry entry)
        {
            // Manifest v0: per i record visibility-managed FinalSha256 era già scritto e consente ownership
            // forte. Per master/side-effect storici non esiste invece un marker post-write: in caso ambiguo
            // preferiamo RecoveryRequired a qualunque cancellazione/ripristino potenzialmente distruttivo.
            if (!string.IsNullOrWhiteSpace(entry.FinalSha256))
            {
                if (!File.Exists(entry.Path))
                    throw new IOException($"Recovery legacy ambigua: '{Path.GetFileName(entry.Path)}' è assente.");
                if (!string.Equals(HashCurrent(entry.Path), entry.FinalSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Recovery legacy bloccata: '{Path.GetFileName(entry.Path)}' non contiene più l'output della transazione.");
                if (entry.Existed) RestoreBeforeImage(entry);
                else File.Delete(entry.Path);
                return;
            }

            if (entry.Existed)
            {
                if (!File.Exists(entry.Path))
                    throw new IOException($"Recovery legacy ambigua: '{Path.GetFileName(entry.Path)}' è stato eliminato dopo Capture.");
                if (!string.IsNullOrWhiteSpace(entry.BeforeSha256) &&
                    string.Equals(HashCurrent(entry.Path), entry.BeforeSha256, StringComparison.OrdinalIgnoreCase))
                    return; // nessuna mutazione visibile: before-image è già al suo posto.
                throw new IOException($"Recovery legacy ambigua per '{Path.GetFileName(entry.Path)}': nessun marker di ownership post-write.");
            }

            if (File.Exists(entry.Path))
                throw new IOException($"Recovery legacy ambigua: '{Path.GetFileName(entry.Path)}' è comparso dopo una Capture che lo vedeva assente.");
        }

        private void RestoreBeforeImage(WorkflowTransactionEntry entry)
        {
            var backup = Path.Combine(_transactionDirectory, entry.BackupFile);
            if (!File.Exists(backup))
                throw new FileNotFoundException("Before-image della transazione non disponibile.", backup);
            if (!string.IsNullOrWhiteSpace(entry.BeforeSha256))
            {
                var backupHash = WorkflowTransactionVisibilityService.Hash(File.ReadAllBytes(backup));
                if (!string.Equals(backupHash, entry.BeforeSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Before-image corrotta per '{Path.GetFileName(entry.Path)}'.");
            }
            RestoreDurable(backup, entry.Path);
        }

        private static string HashCurrent(string path) =>
            WorkflowTransactionVisibilityService.Hash(File.ReadAllBytes(path));

        private void AbortPrepared()
        {
            if (_finished) return;
            if (Manifest.Entries.Count != 0 || Manifest.State != WorkflowTransactionState.Prepared) return;

            try { DeleteTechnicalFile(_sharedMarkerPath); }
            catch (Exception ex) { CrashLogService.Registra($"Ritiro marker transazione preparata {Manifest.Id:N}", ex); }
            DeleteDirectoryBestEffort(_transactionDirectory);
            _finished = true;
        }

        private bool IsTransactionTechnicalPath(string fullPath)
        {
            var shared = Path.Combine(_paths.Root, "_ditm", "transactions");
            var normalizedShared = Path.GetFullPath(shared).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(normalizedShared, PathComparison);
        }

        private void PersistManifest()
        {
            InternalIoDepth.Value++;
            try { WriteObjectDurable(_manifestPath, Manifest); }
            finally { InternalIoDepth.Value--; }
            RinnovaMarkerSeNecessario();
        }

        private void WriteSharedMarker()
        {
            var marker = new WorkflowTransactionMarker
            {
                Id = Manifest.Id,
                DeviceId = Manifest.DeviceId,
                StartedUtc = Manifest.StartedUtc,
                Device = Environment.MachineName,
                Description = Manifest.Description,
                HeartbeatUtc = DateTime.UtcNow,
                LeaseSeconds = (int)MarkerLease.TotalSeconds
            };
            InternalIoDepth.Value++;
            try { WriteObjectDurable(_sharedMarkerPath, marker); }
            finally { InternalIoDepth.Value--; }
            _ultimoBattitoUtc = marker.HeartbeatUtc;
        }

        /// <summary>
        /// Battito del lease (review P2.4): il marker condiviso riporta l'ultimo segno di vita del proprietario, così un altro PC
        /// distingue una transazione viva da una abbandonata. Al massimo un battito ogni <see cref="MarkerHeartbeatInterval"/>.
        /// Best-effort: un battito mancato non altera i dati e, al peggio, rende il marker scaduto: il lettore verifica il ledger.
        /// </summary>
        private void RinnovaMarkerSeNecessario()
        {
            if (_finished || _ultimoBattitoUtc is not DateTime ultimo) return;
            if (DateTime.UtcNow - ultimo < MarkerHeartbeatInterval) return;
            try { WriteSharedMarker(); }
            catch (Exception ex) { CrashLogService.Registra($"Battito marker transazione {Manifest.Id:N}", ex); }
        }
    }

    private static WorkflowTransactionManifest? ReadManifest(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return JsonSerializer.Deserialize<WorkflowTransactionManifest>(bytes, JsonIo.Options);
        }
        catch { return null; }
    }

    private static void WriteObjectDurable<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonIo.Options);
        WriteBytesDurable(path, bytes);
    }

    private static void WriteBytesDurable(string path, ReadOnlySpan<byte> bytes)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                       FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static void CopyDurable(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.WriteThrough | FileOptions.SequentialScan);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void RestoreDurable(string backup, string target)
    {
        var bytes = File.ReadAllBytes(backup);
        WriteBytesDurable(target, bytes);
    }

    private static void DeleteTechnicalFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) { CrashLogService.Registra($"Pulizia directory transazione {path}", ex); }
    }

    private static string TransactionsBaseDirectory() =>
        Path.Combine(WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", "workflow-transactions");

    /// <summary>Cartella delle transazioni locali di questo workspace, sotto la chiave corrente (stessa di LocalTestIndexStore).</summary>
    internal static string WorkspaceDirectory(string root) =>
        Path.Combine(TransactionsBaseDirectory(), WorkspaceLocalPaths.ComputeWorkspaceKey(root));

    internal static bool HasPublishedTransaction(string root, Guid id) =>
        TransactionDirectoriesForRecovery(root).Any(dir =>
            Path.GetFileName(dir).Equals($"tx-{id:N}", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Verifies and optionally removes an unpublished prepare directory. No workflow write can start until
    /// its empty Prepared manifest has been atomically moved into the tx-* recovery namespace.
    /// </summary>
    internal static bool CleanupUnpublishedPreparation(DataPaths paths, Guid id, string deviceId, bool delete)
    {
        if (HasPublishedTransaction(paths.Root, id))
            throw new InvalidDataException("La transazione ha già una directory di recovery pubblicata.");

        var preparationDir = Path.Combine(WorkspaceDirectory(paths.Root), $"prepare-{id:N}");
        if (!Directory.Exists(preparationDir)) return false;

        var entries = Directory.EnumerateFileSystemEntries(preparationDir).ToList();
        if (entries.Count != 1 || Directory.Exists(entries[0]) ||
            !Path.GetFileName(entries[0]).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("La directory di preparazione contiene dati inattesi; recovery bloccata.");

        var manifest = ReadManifest(entries[0]);
        if (manifest == null || manifest.Id != id || manifest.State != WorkflowTransactionState.Prepared ||
            manifest.Entries.Count != 0 || !SameRoot(manifest.WorkspaceRoot, paths.Root) ||
            !manifest.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("La directory di preparazione non contiene un manifest Prepared vuoto e coerente.");

        if (delete) Directory.Delete(preparationDir, recursive: true);
        return true;
    }

    /// <summary>
    /// Directory tx-* da recuperare per questo workspace: quelle sotto la chiave corrente e, se esiste, la cartella della chiave
    /// grezza precedente alla 6.7.1 (vedi <see cref="WorkspaceLocalPaths.CartellaLegacyDaLeggere"/>). Una transazione interrotta
    /// scritta sotto la vecchia chiave NON viene ignorata: viene letta e recuperata nel posto in cui si trova (nessuno spostamento).
    /// </summary>
    internal static IReadOnlyList<string> TransactionDirectoriesForRecovery(string root)
    {
        var result = new List<string>();
        var current = WorkspaceDirectory(root);
        if (Directory.Exists(current))
            result.AddRange(Directory.EnumerateDirectories(current, "tx-*", SearchOption.TopDirectoryOnly));
        var legacy = WorkspaceLocalPaths.CartellaLegacyDaLeggere(TransactionsBaseDirectory(), root);
        if (legacy != null && Directory.Exists(legacy))
            result.AddRange(Directory.EnumerateDirectories(legacy, "tx-*", SearchOption.TopDirectoryOnly));
        return result;
    }

    private static string SharedMarkerPath(string root, Guid id) =>
        Path.Combine(root, "_ditm", "transactions", $"{id:N}.pending.json");

    private static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    private static bool SameRoot(string a, string b) => string.Equals(NormalizeRoot(a), NormalizeRoot(b), PathComparison);
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

/// <summary>
/// Transazione workflow il cui esito di commit non è ancora verificabile (ricevuta nel ledger non leggibile). Non è un
/// fallimento definitivo: la transazione resta pendente, nessun file viene ripristinato e le scritture restano bloccate
/// finché un nuovo tentativo di recupero non la risolve.
/// </summary>
public sealed class WorkflowVerificaPendenteException : IOException
{
    public WorkflowVerificaPendenteException(string? dettaglio)
        : base(string.Format(LocalizationService.Instance.Traduci("TxVerificaPendenteFmt"), dettaglio ?? LocalizationService.Instance.Traduci("TxLedgerIllegibile")))
    {
    }
}

public enum WorkflowTransactionState
{
    Prepared,
    Applying,
    RollingBack,
    RecoveryRequired,
    RolledBack,
    Committed
}

public sealed class WorkflowTransactionManifest
{
    public Guid Id { get; set; }
    public string DeviceId { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public WorkflowTransactionState State { get; set; }
    public int MutationTrackingVersion { get; set; }
    public List<WorkflowTransactionEntry> Entries { get; set; } = new();
}

public sealed class WorkflowTransactionEntry
{
    public string Path { get; set; } = "";
    public bool Existed { get; set; }
    public string BackupFile { get; set; } = "";
    public string? BeforeSha256 { get; set; }
    public string? FinalSha256 { get; set; }
    public bool LogicallyDeleted { get; set; }
    public bool MutationCompleted { get; set; }
    public bool PhysicallyDeleted { get; set; }

    /// <summary>Hash del contenuto che lo spostamento in corso deve produrre, registrato prima di File.Move (review P1.9). Null senza intenzione.</summary>
    public string? IntendedSha256 { get; set; }

    /// <summary>Intenzione di eliminazione fisica registrata prima di File.Delete (review P1.9).</summary>
    public bool IntendedDelete { get; set; }
}

public sealed class WorkflowTransactionMarker
{
    public Guid Id { get; set; }
    public string DeviceId { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public string Device { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Ultimo segno di vita del proprietario (review P2.4). Assente nei marker delle versioni precedenti, che restano bloccanti.</summary>
    public DateTime? HeartbeatUtc { get; set; }

    /// <summary>Durata del lease a partire da <see cref="HeartbeatUtc"/>, in secondi. Assente nei marker delle versioni precedenti.</summary>
    public int? LeaseSeconds { get; set; }
}
