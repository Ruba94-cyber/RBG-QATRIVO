using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Bug segnalato dall'utente (2026-09-18): "ho creato un test e su un altro pc risulta come non leggibile il
/// file nonostante venga anche visualizzato correttamente. e il tasto risolvi sembra inutile". Causa
/// originale: la receipt di un commit (WorkflowCommitLedgerService, in
/// _ditm/transactions/committed/&lt;device&gt;/current.ndjson e checkpoint.json.gz) poteva arrivare da un
/// altro PC SENZA che nessun file in testcases/ cambiasse, e un Test rimasto "Pending" (receipt non ancora
/// vista al momento dello scan) restava segnalato in FileConProblemi finché non scattava la riconciliazione
/// profonda ogni 6 ore o un riavvio, anche se i dati erano già sincronizzati.
///
/// Piano 6.4.2 §5 (pulizia residui §1): il gate cross-file basato sulle receipt è stato rimosso a monte (un
/// JSON di dominio sintatticamente valido è sempre leggibile), quindi il caso "Pending in attesa di receipt"
/// che il watcher dedicato al ledger (TestCaseChangeTracker.AddLedgerWatcher/ConsumeLedgerChanged) serviva a
/// far guarire prima della riconciliazione periodica non esiste più: è stato rimosso insieme ai due test che
/// lo coprivano specificamente. Resta però la copertura, sotto, che un Test scritto da un altro PC sia già
/// leggibile SUBITO (senza gate né watcher dedicato) e che la pubblicazione successiva della receipt non
/// cambi nulla.
/// </summary>
public sealed class TestCaseChangeTrackerLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_LedgerWatch_" + Guid.NewGuid().ToString("N"));

    public TestCaseChangeTrackerLedgerTests() => new DataPaths(_root).EnsureStructure();

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Aggiornato per la decisione del piano 6.4.2, §1 (docs/PIANO_6.4.2.md): il gate cross-file
    /// basato sulle receipt e' stato rimosso — un JSON di dominio sintatticamente valido e' SEMPRE leggibile,
    /// anche se la sua transazione non ha ancora una receipt visibile localmente (esattamente il caso di un
    /// Test scritto da un altro PC prima che OneDrive consegni anche la receipt). Il test originale (bug
    /// segnalato dall'utente il 2026-09-18) verificava che il Test restasse "non leggibile" finche' la receipt
    /// non arrivava: quel comportamento e' proprio quello rimosso dal piano, quindi qui si verifica il
    /// contrario, cioe' che il Test sia gia' leggibile e MAI in FileConProblemi anche senza receipt — la
    /// pubblicazione successiva della receipt (per un altro PC) non deve cambiare nulla. Copertura equivalente,
    /// piu' ampia, in LetturaSenzaGateTransazionaleTests.</summary>
    [Fact]
    public void UnTestPendingSenzaReceipt_TornaLeggibileDopoCheLaReceiptArrivaSenzaAttendereLaRiconciliazioneProfonda()
    {
        var state = new AppState(new AppConfigService(Path.Combine(_root, "config", "config.json")), runBackgroundMaintenance: false);
        state.InizializzaCartella(_root);
        var paths = state.Paths!;

        // Un Test "sano" già presente, cosi' la fotografia iniziale non e' vuota.
        var testSano = new TestCase { Id = "TC-SANO", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now };
        state.TestCases.Save(testSano, true);

        // Simula l'arrivo da un altro PC: file scritto con lo stamp transazionale, nessuna receipt ancora
        // visibile localmente — con il gate rimosso questo NON rende il file "Pending": resta un JSON di
        // dominio sintatticamente valido, quindi leggibile da subito.
        var txId = Guid.NewGuid();
        const string deviceId = "pc-remoto";
        var pending = new TestCase { Id = "TC-PENDING", Uid = Guid.NewGuid(), DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now };
        var payload = JsonSerializer.SerializeToUtf8Bytes(pending, JsonIo.Options);
        var stamped = WorkflowTransactionVisibilityService.StampJson(payload, txId, deviceId);
        var percorsoPending = paths.TestCaseFile(pending.Componente, pending.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(percorsoPending)!);
        File.WriteAllBytes(percorsoPending, stamped);

        state.RileggiDaDisco();
        Assert.DoesNotContain(state.TuttiIFileConProblemi(), p => p.Percorso.Equals(percorsoPending, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(state.TestCases.GetAll(), t => t.Id == "TC-PENDING");

        // La receipt arriva più tardi (pubblicata per davvero tramite il ledger): non deve cambiare nulla, dato
        // che non esiste più alcun gate da far scattare.
        WorkflowCommitLedgerService.Publish(paths, new WorkflowTransactionCommitReceipt
        {
            Id = txId,
            DeviceId = deviceId,
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
            Entries = new List<WorkflowTransactionCommitEntry>
            {
                new() { RelativePath = Path.GetRelativePath(paths.Root, percorsoPending).Replace('\\', '/'), FinalSha256 = WorkflowTransactionVisibilityService.Hash(stamped), LogicallyDeleted = false }
            }
        });
        state.RileggiDaDisco();
        Assert.DoesNotContain(state.TuttiIFileConProblemi(), p => p.Percorso.Equals(percorsoPending, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(state.TestCases.GetAll(), t => t.Id == "TC-PENDING");
    }
}
