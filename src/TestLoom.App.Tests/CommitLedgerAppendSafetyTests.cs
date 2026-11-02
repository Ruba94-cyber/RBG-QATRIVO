using System.Text.Json;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class CommitLedgerAppendSafetyTests : IDisposable
{
    private const string Device = "receipt-device-partial";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_ReceiptAppend_" + Guid.NewGuid().ToString("N"));

    public CommitLedgerAppendSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private string DeviceDir => Path.Combine(_root, "_ditm", "transactions", "committed", Device);
    private string Current => Path.Combine(DeviceDir, "current.ndjson");

    [Fact]
    public void Publish_CodaParzialeRiparataSenzaPerdereLeRicevuteValide()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        Directory.CreateDirectory(DeviceDir);
        var precedente = NuovaRicevuta();
        const string partial = "{\"Id\":\"troncato";
        File.WriteAllText(Current, Riga(precedente) + "\n" + partial);

        var nuova = NuovaRicevuta();
        WorkflowCommitLedgerService.Publish(paths, nuova);

        // La coda parziale (crash a metà riga) finisce nel file laterale e il ledger torna accodabile:
        // né la ricevuta precedente né quella appena pubblicata vengono perse.
        var laterale = Assert.Single(Directory.GetFiles(DeviceDir, "current.ndjson.torn-*"));
        Assert.Equal(partial, File.ReadAllText(laterale));
        Assert.DoesNotContain(partial, File.ReadAllText(Current));
        Assert.True(WorkflowCommitLedgerService.TryGetReceipt(paths, Device, precedente.Id, out _, out var errorePrecedente), errorePrecedente);
        Assert.True(WorkflowCommitLedgerService.TryGetReceipt(paths, Device, nuova.Id, out _, out var erroreNuova), erroreNuova);
    }

    [Fact]
    public void Publish_RigaIntermediaNonValida_NonRiparaENonModificaIlLedger()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        Directory.CreateDirectory(DeviceDir);
        // Corruzione a metà file (non all'ultima riga): non è una coda da riparare e l'append resterebbe ambiguo.
        File.WriteAllText(Current, Riga(NuovaRicevuta()) + "\n{\"Id\":\"intermedia-corrotta\n" + Riga(NuovaRicevuta()) + "\n");
        var originali = File.ReadAllBytes(Current);

        Assert.Throws<InvalidDataException>(() => WorkflowCommitLedgerService.Publish(paths, NuovaRicevuta()));
        Assert.Equal(originali, File.ReadAllBytes(Current));
        Assert.Empty(Directory.GetFiles(DeviceDir, "current.ndjson.torn-*"));
    }

    [Fact]
    public void Publish_UltimaRicevutaValidaSenzaNewline_ChiudeLaRigaSenzaScartarla()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        Directory.CreateDirectory(DeviceDir);
        // Riga completa e valida ma senza newline finale: non è una coda parziale e non deve essere scartata.
        var precedente = NuovaRicevuta();
        File.WriteAllText(Current, Riga(precedente));

        var nuova = NuovaRicevuta();
        WorkflowCommitLedgerService.Publish(paths, nuova);

        Assert.Empty(Directory.GetFiles(DeviceDir, "current.ndjson.torn-*"));
        var righe = File.ReadAllLines(Current).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        Assert.Equal(2, righe.Count);
        Assert.True(WorkflowCommitLedgerService.TryGetReceipt(paths, Device, precedente.Id, out _, out _));
        Assert.True(WorkflowCommitLedgerService.TryGetReceipt(paths, Device, nuova.Id, out _, out _));
    }

    private static WorkflowTransactionCommitReceipt NuovaRicevuta() => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = Device,
        StartedUtc = DateTime.UtcNow.AddSeconds(-1),
        CompletedUtc = DateTime.UtcNow,
        Description = "append-safety",
        Entries = new List<WorkflowTransactionCommitEntry>
        {
            new()
            {
                RelativePath = Path.Combine("testcases", "ECM", "testcase_TC-RECEIPT.json"),
                FinalSha256 = new string('A', 64),
                LogicallyDeleted = false
            }
        }
    };

    private static string Riga(WorkflowTransactionCommitReceipt receipt) =>
        JsonSerializer.Serialize(receipt, JsonIo.CompactOptions);
}
