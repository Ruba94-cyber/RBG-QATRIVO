using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Copertura della correzione C1/C2: la catena di verifica transazionale deve sopravvivere alla contesa di I/O
/// tipica di una cartella OneDrive/SharePoint, dove il client di sincronizzazione tiene aperti in scrittura i
/// file che sta caricando o scaricando.
///
/// La 6.2.5 aveva reso condivisa la sola JsonIo.ReadAllBytesShared, ma tutto cio' che sta a valle
/// (VerifyReceiptSettled, Inspect, TryReadTombstoneIdentity, i ledger NDJSON e il guard dei marker) apriva
/// ancora con FileShare.Read. Un solo handle concorrente bastava quindi a far risultare un commit "non
/// assestato" (Test vecchio + segnalazione di file non leggibile) o, nel caso del marker, a congelare
/// l'intero workspace.
///
/// Gli handle di questi test usano FileAccess.Write + FileShare.ReadWrite | FileShare.Delete: e' lo stesso
/// handle gia' usato da LowEndPcAndMultiUserReadSafetyTests per simulare OneDrive ed e' incompatibile con
/// un'apertura FileShare.Read, quindi ognuno di questi test fallirebbe sul codice pre-correzione.
/// </summary>
public sealed class SharedReadContentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_SharedContention_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_SharedContentionLocal_" + Guid.NewGuid().ToString("N"));

    private const string DeviceA = "pc-a-device";

    public SharedReadContentionTests()
    {
        new DataPaths(_root).EnsureStructure();
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDelete(_root);
        TryDelete(_local);
    }

    /// <summary>Handle equivalente a quello che il client di sincronizzazione tiene su un file in transito.</summary>
    private static FileStream HandleDiSincronizzazione(string path) =>
        new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    // ---------------------------------------------------------------- C1: file di dominio della transazione

    // VerifyReceiptSettled_* e Inspect_ConFileApertoDalClientDiSync_RestituisceCommittedVisible sono stati
    // rimossi con la 6.4.2 (docs/PIANO_6.4.2.md §1): coprivano il gate cross-file all-or-nothing basato sulle
    // receipt (WorkflowTransactionVisibilityService.VerifyReceiptSettled/Inspect), eliminato perché un JSON di
    // dominio sintatticamente valido è ora sempre leggibile indipendentemente dalla receipt. La lettura
    // condivisa in presenza di contesa OneDrive resta coperta più sotto (JsonIo_TestStampatoConDominioELedgerAperti_RestaLeggibile
    // e TryReadTombstoneIdentity_ConFileApertoDalClientDiSync_RiconosceIlTombstone).

    [Fact]
    public void TryReadTombstoneIdentity_ConFileApertoDalClientDiSync_RiconosceIlTombstone()
    {
        var paths = new DataPaths(_root);
        var uid = Guid.NewGuid();
        var originale = JsonSerializer.SerializeToUtf8Bytes(
            new TestCase { Id = "TC-TOMB", Uid = uid, Descrizione = "da eliminare" }, JsonIo.Options);
        var tombstone = WorkflowTransactionVisibilityService.CreateTombstone(
            Guid.NewGuid(), DeviceA, "testcase_TC-TOMB.json", originale);

        var percorso = Path.Combine(paths.TestCasesDir, "testcase_TC-TOMB.json");
        Directory.CreateDirectory(Path.GetDirectoryName(percorso)!);
        File.WriteAllBytes(percorso, tombstone);

        using var handle = HandleDiSincronizzazione(percorso);

        Assert.True(WorkflowTransactionVisibilityService.TryReadTombstoneIdentity(percorso, out var letturaUid, out var letturaId));
        Assert.Equal(uid, letturaUid);
        Assert.Equal("TC-TOMB", letturaId);
    }

    // ---------------------------------------------------------------- C1: ledger NDJSON condivisi

    [Fact]
    public void TryGetReceipt_ConCurrentNdjsonApertoDalClientDiSync_TrovaComunqueLaReceipt()
    {
        var paths = new DataPaths(_root);
        var txId = Guid.NewGuid();
        var (percorso, bytes) = ScriviTestStampato(paths, "TC-LEDGER", txId);
        WorkflowCommitLedgerService.Publish(paths, Receipt(paths, txId, (percorso, bytes, false)));

        var ledger = Path.Combine(_root, "_ditm", "transactions", "committed", DeviceA, "current.ndjson");
        Assert.True(File.Exists(ledger));

        using (HandleDiSincronizzazione(ledger))
        {
            Assert.True(
                WorkflowCommitLedgerService.TryGetReceipt(paths, DeviceA, txId, out var receipt, out var errore),
                $"La receipt esiste gia' nel ledger e non deve risultare assente per contesa di I/O: {errore}");
            Assert.NotNull(receipt);
            Assert.Equal(txId, receipt!.Id);
        }
    }

    /// <summary>
    /// Percorso end-to-end del difetto segnalato: PC A ha gia' consegnato sia il JSON di dominio sia la
    /// receipt, ma il client di sincronizzazione tiene ancora aperti entrambi. Prima della correzione il Test
    /// risultava "non leggibile"; ora deve essere esposto normalmente.
    /// </summary>
    [Fact]
    public void JsonIo_TestStampatoConDominioELedgerAperti_RestaLeggibile()
    {
        var paths = new DataPaths(_root);
        var txId = Guid.NewGuid();
        var (percorso, bytes) = ScriviTestStampato(paths, "TC-E2E", txId);
        WorkflowCommitLedgerService.Publish(paths, Receipt(paths, txId, (percorso, bytes, false)));
        var ledger = Path.Combine(_root, "_ditm", "transactions", "committed", DeviceA, "current.ndjson");

        using (HandleDiSincronizzazione(percorso))
        using (HandleDiSincronizzazione(ledger))
        {
            var letto = JsonIo.TryRead<TestCase>(percorso, out var errore);
            Assert.NotNull(letto);
            Assert.Null(errore);
            Assert.Equal("TC-E2E", letto!.Id);
        }
    }

    [Fact]
    public void JournalChangeFeed_ConCurrentNdjsonApertoDalClientDiSync_NonPerdeIlDelta()
    {
        var paths = new DataPaths(_root);
        var index = new LocalTestIndexStore(_root, _local);
        var feed = new JournalChangeFeedService(paths, index.WorkspaceDirectory);
        Assert.True(feed.EstablishBaseline());

        var percorsoTest = Path.Combine(paths.TestCasesDir, "testcase_T-FEED.json");
        var evt = new TestJournalEvent
        {
            SchemaVersion = SharedTestJournalService.JournalSchemaVersion,
            EventId = Guid.NewGuid().ToString("N"),
            DeviceId = "device-feed",
            TimestampUtc = DateTime.UtcNow,
            Operation = JournalOperation.Save,
            EntityUid = Guid.NewGuid(),
            EntityId = "T-FEED",
            BaseRevision = 1,
            BaseHash = "base",
            NewRevision = 2,
            NewHash = "new",
            OldRelativePath = Path.GetRelativePath(_root, percorsoTest),
            NewRelativePath = Path.GetRelativePath(_root, percorsoTest)
        };
        var riga = JsonSerializer.Serialize(evt, new JsonSerializerOptions(JsonIo.Options) { WriteIndented = false });

        var deviceDir = Path.Combine(_root, "_ditm", "journal", evt.DeviceId);
        Directory.CreateDirectory(deviceDir);
        var current = Path.Combine(deviceDir, "current.ndjson");
        File.WriteAllText(current, riga + Environment.NewLine);

        using (HandleDiSincronizzazione(current))
        {
            var batch = feed.Poll();
            Assert.False(batch.RequiresFullReconciliation);
            Assert.Single(batch.Events);
            Assert.Equal(evt.EventId, batch.Events[0].EventId);
        }
    }

    // ---------------------------------------------------------------- C2: guard dei marker .pending.json

    [Fact]
    public void Guard_MarkerApertoDalClientDiSync_NonVieneDichiaratoIlleggibile()
    {
        var paths = new DataPaths(_root);
        var markerPath = ScriviMarker("PC-REMOTO", "save-test:TC-REMOTO");

        using var handle = HandleDiSincronizzazione(markerPath);

        // Il marker straniero blocca — quello e' il comportamento voluto — ma per il motivo GIUSTO: deve
        // essere letto e attribuito al device che lo ha creato, non classificato come illeggibile.
        Assert.False(SharedWorkflowTransactionGuard.CanReadConsistently(paths, out var messaggio));
        Assert.Contains("PC-REMOTO", messaggio, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("illeggibile", messaggio, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contesa", messaggio, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Nucleo di C2. Una IOException transitoria (handle esclusivo che si libera in poche decine di
    /// millisecondi) non deve piu' congelare il workspace: i retry con backoff breve la assorbono e il guard
    /// vede il marker per quello che e'. Senza retry questo caso produceva "marker illeggibile" e quindi
    /// SolaLettura fino al giro di refresh successivo.
    /// </summary>
    [Fact]
    public void Guard_ContesaTransitoriaSulMarker_VieneAssorbitaDaiRetry()
    {
        var paths = new DataPaths(_root);
        var markerPath = ScriviMarker("PC-TRANSITORIO", "save-test:TC-TRANSITORIO");

        // FileShare.None: qualunque apertura, anche condivisa, fallisce con IOException finche' l'handle vive.
        var esclusivo = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var rilascio = new Thread(() => { Thread.Sleep(120); esclusivo.Dispose(); }) { IsBackground = true };
        rilascio.Start();

        try
        {
            SharedWorkflowTransactionGuard.CanReadConsistently(paths, out var messaggio);
            // I backoff sono 75 ms + 225 ms: l'handle si libera dentro la finestra, quindi il marker viene
            // letto e attribuito al device invece di produrre un blocco per illeggibilita'.
            Assert.Contains("PC-TRANSITORIO", messaggio, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("illeggibile", messaggio, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            rilascio.Join();
        }
    }

    /// <summary>
    /// Fail-closed preservato. Se la contesa non si libera entro la finestra di retry il workspace si blocca
    /// comunque (mai "nel dubbio procedi"), ma il messaggio dichiara la contesa di I/O e dice esplicitamente
    /// che il marker non risulta danneggiato: l'utente non va a caccia di una corruzione inesistente.
    /// </summary>
    [Fact]
    public void Guard_ContesaPersistenteSulMarker_BloccaMaDistingueLaContesaDallaCorruzione()
    {
        var paths = new DataPaths(_root);
        var markerPath = ScriviMarker("PC-BLOCCATO", "save-test:TC-BLOCCATO");

        using var esclusivo = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(SharedWorkflowTransactionGuard.CanReadConsistently(paths, out var messaggio));
        Assert.Contains("contesa", messaggio, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non risulta danneggiato", messaggio, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recovery", messaggio, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Regressione: un marker davvero corrotto deve continuare a bloccare, come prima della C2.</summary>
    [Fact]
    public void Guard_MarkerRealmenteCorrotto_ContinuaABloccare()
    {
        var paths = new DataPaths(_root);
        var markerPath = Path.Combine(_root, "_ditm", "transactions", $"{Guid.NewGuid():N}.pending.json");
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, "{broken");

        Assert.False(SharedWorkflowTransactionGuard.CanReadConsistently(paths, out var messaggio));
        Assert.Contains("illeggibile", messaggio, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recovery", messaggio, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<IOException>(() =>
            SharedWorkflowTransactionGuard.EnsureNoBlockingTransaction(paths, "scrittura-di-prova"));
    }

    /// <summary>
    /// Il marker che sparisce tra l'enumerazione della directory e la lettura significa che il commit o la
    /// recovery proprietaria sono appena terminati. Nessun marker = nessuna barriera: non e' un errore e non
    /// deve bloccare. Prima veniva intercettato dal catch generico come "marker illeggibile".
    /// </summary>
    [Fact]
    public void Guard_MarkerRimossoDuranteLaLettura_NonBloccaIlWorkspace()
    {
        var paths = new DataPaths(_root);
        var markerPath = ScriviMarker("PC-USCENTE", "save-test:TC-USCENTE");

        // DeleteOnClose + FileShare.None: la lettura fallisce finche' l'handle vive, e alla chiusura il file
        // non esiste piu'. E' la corsa reale tra il refresh di questo PC e la rimozione del marker.
        var uscente = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.None, 4096,
            FileOptions.DeleteOnClose);
        var rilascio = new Thread(() => { Thread.Sleep(120); uscente.Dispose(); }) { IsBackground = true };
        rilascio.Start();

        try
        {
            Assert.True(SharedWorkflowTransactionGuard.CanReadConsistently(paths, out var messaggio), messaggio);
            Assert.Equal("", messaggio);
        }
        finally
        {
            rilascio.Join();
        }
    }

    // ---------------------------------------------------------------- helper

    private string ScriviMarker(string device, string descrizione)
    {
        var id = Guid.NewGuid();
        var markerPath = Path.Combine(_root, "_ditm", "transactions", $"{id:N}.pending.json");
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        JsonIo.WriteAtomic(markerPath, new WorkflowTransactionMarker
        {
            Id = id,
            StartedUtc = DateTime.UtcNow,
            Device = device,
            Description = descrizione
        });
        return markerPath;
    }

    private static (string Percorso, byte[] Bytes) ScriviTestStampato(DataPaths paths, string id, Guid txId)
    {
        var tc = new TestCase { Id = id, Descrizione = "non-conforme", DataCreazione = DateTime.Now };
        var percorso = paths.TestCaseFile(tc.Componente, tc.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(percorso)!);
        var bytes = WorkflowTransactionVisibilityService.StampJson(
            JsonSerializer.SerializeToUtf8Bytes(tc, JsonIo.Options), txId, DeviceA);
        File.WriteAllBytes(percorso, bytes);
        return (percorso, bytes);
    }

    private static WorkflowTransactionCommitReceipt Receipt(
        DataPaths paths,
        Guid txId,
        params (string Path, byte[] Bytes, bool Deleted)[] entries) => new()
    {
        Id = txId,
        DeviceId = DeviceA,
        Device = DeviceA,
        StartedUtc = DateTime.UtcNow,
        CompletedUtc = DateTime.UtcNow,
        Entries = entries.Select(e => new WorkflowTransactionCommitEntry
        {
            RelativePath = Path.GetRelativePath(paths.Root, e.Path).Replace('\\', '/'),
            FinalSha256 = WorkflowTransactionVisibilityService.Hash(e.Bytes),
            LogicallyDeleted = e.Deleted
        }).ToList()
    };

    private static void TryDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
