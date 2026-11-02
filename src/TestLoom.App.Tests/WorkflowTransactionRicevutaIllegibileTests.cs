using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Esito di commit non verificabile (review P1.1): se la ricevuta nel ledger non si può leggere, l'esito è sconosciuto.
/// Non si ripristina nulla, il marker resta, le scritture restano bloccate con messaggio e il recupero riprova.
/// </summary>
public sealed class WorkflowTransactionRicevutaIllegibileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_TxRicevuta_" + Guid.NewGuid().ToString("N"));

    public WorkflowTransactionRicevutaIllegibileTests()
    {
        Directory.CreateDirectory(_root);
        new DataPaths(_root).EnsureStructure();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void CommitConLedgerIllegibile_NonRipristinaEBloccaLeScritture_FinoAllaVerificaRiuscita()
    {
        var paths = new DataPaths(_root);
        var path = paths.NonConformitaFile("NC-P11-001");
        var uid = Guid.NewGuid();
        JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P11-001", RevisioneDati = 1 });
        var prima = File.ReadAllBytes(path);

        // Un commit precedente rende il ledger del device leggibile e ne fissa il file: poi lo si blocca.
        WorkflowCommitLedgerService.Publish(paths, RicevutaConUnaVoce(paths));
        var ledgerFile = Directory.EnumerateFiles(Path.Combine(paths.Root, "_ditm", "transactions", "committed"),
            "current.ndjson", SearchOption.AllDirectories).Single();

        byte[] dopo;
        using (new FileStream(ledgerFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // La pubblicazione della ricevuta non può leggere il ledger: il commit fallisce e l'esito non è verificabile.
            var transactionId = Guid.Empty;
            Assert.Throws<WorkflowVerificaPendenteException>(() =>
            {
                using var tx = WorkflowTransactionService.Begin(paths, "ricevuta-illeggibile");
                transactionId = tx.Id;
                JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P11-001", RevisioneDati = 2 });
                tx.Commit();
            });

            // The failed Commit has exited its owner scope. This fixture now represents a crashed/stale owner,
            // so recovery should consult the still-unreadable ledger rather than block on a live lease.
            ScadiMarker(paths, transactionId);

            // Nessun ripristino: il contenuto nuovo resta com'è e non è stato annullato.
            dopo = File.ReadAllBytes(path);
            Assert.NotEqual(prima, dopo);

            // Finché il ledger resta illeggibile: né le scritture né il recupero passano.
            Assert.Throws<WorkflowVerificaPendenteException>(() => WorkflowTransactionService.Begin(paths, "bloccata"));
            Assert.Throws<WorkflowVerificaPendenteException>(() => WorkflowTransactionService.RecoverPending(paths));
            Assert.Equal(dopo, File.ReadAllBytes(path));
        }

        // Il ledger torna leggibile e non contiene la ricevuta: l'esito è certo, quindi il recupero ripristina il before-image.
        WorkflowTransactionService.RecoverPending(paths);
        Assert.Equal(prima, File.ReadAllBytes(path));

        // Recuperata la transazione, le scritture tornano disponibili.
        using (WorkflowTransactionService.Begin(paths, "dopo-recupero")) { }
    }

    private static WorkflowTransactionCommitReceipt RicevutaConUnaVoce(DataPaths paths)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"ricevuta\":\"precedente\"}");
        return new WorkflowTransactionCommitReceipt
        {
            Id = Guid.NewGuid(),
            StartedUtc = DateTime.UtcNow.AddSeconds(-1),
            CompletedUtc = DateTime.UtcNow,
            DeviceId = DeviceIdentityService.GetOrCreate(),
            Device = "test",
            Description = "ricevuta-illeggibile",
            Entries =
            [
                new WorkflowTransactionCommitEntry
                {
                    RelativePath = WorkflowTransactionVisibilityService.NormalizeRelativePath(paths.Root, paths.NonConformitaFile("NC-PRECEDENTE")),
                    FinalSha256 = WorkflowTransactionVisibilityService.Hash(bytes),
                    LogicallyDeleted = false
                }
            ]
        };
    }

    private static void ScadiMarker(DataPaths paths, Guid id)
    {
        var markerPath = Path.Combine(paths.Root, "_ditm", "transactions", $"{id:N}.pending.json");
        var marker = System.Text.Json.JsonSerializer.Deserialize<WorkflowTransactionMarker>(File.ReadAllBytes(markerPath), JsonIo.Options)!;
        marker.HeartbeatUtc = DateTime.UtcNow.AddMinutes(-10);
        File.WriteAllBytes(markerPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(marker, JsonIo.Options));
    }
}
