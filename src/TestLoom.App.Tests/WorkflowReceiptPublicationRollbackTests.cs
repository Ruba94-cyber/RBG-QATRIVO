using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class WorkflowReceiptPublicationRollbackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_ReceiptRollback_" + Guid.NewGuid().ToString("N"));

    public WorkflowReceiptPublicationRollbackTests()
    {
        Directory.CreateDirectory(_root);
        new DataPaths(_root).EnsureStructure();
    }

    [Fact]
    public void LedgerCurrentTroncato_CommitRiparaLaCodaEPubblicaLaRicevuta()
    {
        var paths = new DataPaths(_root);
        var path = paths.NonConformitaFile("NC-RECEIPT-ROLLBACK");
        var original = new NonConformita
        {
            Uid = Guid.NewGuid(),
            Id = "NC-RECEIPT-ROLLBACK",
            RevisioneDati = 1,
            Descrizione = "before"
        };
        JsonIo.WriteAtomic(path, original);
        var beforeBytes = File.ReadAllBytes(path);

        Guid txId;
        byte[] changedBytes;
        string deviceId;
        string ledgerDir;
        var tornLedger = Encoding.UTF8.GetBytes("{\"Id\":\"truncated");

        using (var tx = WorkflowTransactionService.Begin(paths, "receipt-publication-torn-tail"))
        {
            txId = tx.Id;
            var changed = new NonConformita
            {
                Uid = original.Uid,
                Id = original.Id,
                RevisioneDati = 2,
                Descrizione = "after da confermare"
            };
            JsonIo.WriteAtomic(path, changed, overwrite: true);
            changedBytes = File.ReadAllBytes(path);
            Assert.NotEqual(beforeBytes, changedBytes);

            // 6.4.2: i JSON di dominio ordinari non vengono più stampati con _ditmTx (PrepareJsonWrite scrive
            // i byte as-is), quindi il DeviceId non è più leggibile dal file: è lo stesso di questo processo.
            deviceId = DeviceIdentityService.GetOrCreate();
            Assert.False(string.IsNullOrWhiteSpace(deviceId));

            var safeDevice = string.Concat(deviceId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
            ledgerDir = Path.Combine(_root, "_ditm", "transactions", "committed", safeDevice);
            Directory.CreateDirectory(ledgerDir);
            File.WriteAllBytes(Path.Combine(ledgerDir, "current.ndjson"), tornLedger);

            // P2.5: la coda parziale lasciata da un crash precedente viene spostata in current.ndjson.torn-* e la
            // ricevuta viene pubblicata. Il commit riesce e la before-image NON viene ripristinata.
            tx.Commit();
            Assert.Equal(changedBytes, File.ReadAllBytes(path));
        }

        Assert.Equal(changedBytes, File.ReadAllBytes(path));
        Assert.True(WorkflowCommitLedgerService.TryGetReceipt(paths, deviceId, txId, out _, out var errore), errore);
        var laterale = Assert.Single(Directory.GetFiles(ledgerDir, "current.ndjson.torn-*"));
        Assert.Equal(tornLedger, File.ReadAllBytes(laterale));

        var pendingMarker = Path.Combine(_root, "_ditm", "transactions", $"{txId:N}.pending.json");
        Assert.False(File.Exists(pendingMarker));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
