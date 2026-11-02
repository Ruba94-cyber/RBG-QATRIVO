using System.Text;
using System.Text.Json;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Lease del marker condiviso (review P2.4): un marker scaduto di un altro PC si sblocca solo se il ledger del proprietario
/// conferma il commit; con ricevuta assente o ledger non verificabile resta bloccante. I marker senza lease restano bloccanti.
/// </summary>
public sealed class WorkflowTransactionLeaseTests : IDisposable
{
    private const string PcAltro = "pc-altro";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_TxLease_" + Guid.NewGuid().ToString("N"));

    public WorkflowTransactionLeaseTests()
    {
        Directory.CreateDirectory(_root);
        new DataPaths(_root).EnsureStructure();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void MarkerScadutoConRicevutaPubblicata_SbloccaLeScritture()
    {
        var paths = new DataPaths(_root);
        var id = Guid.NewGuid();
        WorkflowCommitLedgerService.Publish(paths, RicevutaDi(paths, PcAltro, id));
        var marker = ScriviMarker(paths, id, PcAltro, DateTime.UtcNow.AddMinutes(-10), 180);

        SharedWorkflowTransactionGuard.EnsureNoBlockingTransaction(paths, "Test lease");

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void MarkerScadutoSenzaRicevuta_RestaBloccante()
    {
        var paths = new DataPaths(_root);
        var id = Guid.NewGuid();
        // Il ledger del PC proprietario è leggibile e contiene solo un'altra transazione: la ricevuta di questa è assente.
        WorkflowCommitLedgerService.Publish(paths, RicevutaDi(paths, PcAltro, Guid.NewGuid()));
        var marker = ScriviMarker(paths, id, PcAltro, DateTime.UtcNow.AddMinutes(-10), 180);

        Assert.Throws<IOException>(() => SharedWorkflowTransactionGuard.EnsureNoBlockingTransaction(paths, "Test lease"));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void MarkerScadutoConLedgerNonVerificabile_RestaBloccante()
    {
        var paths = new DataPaths(_root);
        var id = Guid.NewGuid();
        // Nessun ledger visibile per il PC proprietario: l'esito non è verificabile, quindi nessuno sblocco.
        var marker = ScriviMarker(paths, id, PcAltro, DateTime.UtcNow.AddMinutes(-10), 180);

        Assert.Throws<IOException>(() => SharedWorkflowTransactionGuard.EnsureNoBlockingTransaction(paths, "Test lease"));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void MarkerNonScadutoConRicevutaPubblicata_RestaBloccante()
    {
        var paths = new DataPaths(_root);
        var id = Guid.NewGuid();
        WorkflowCommitLedgerService.Publish(paths, RicevutaDi(paths, PcAltro, id));
        var marker = ScriviMarker(paths, id, PcAltro, DateTime.UtcNow, 180);

        Assert.Throws<IOException>(() => SharedWorkflowTransactionGuard.EnsureNoBlockingTransaction(paths, "Test lease"));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void MarkerSenzaLease_RestaBloccanteAncheConRicevuta()
    {
        var paths = new DataPaths(_root);
        var id = Guid.NewGuid();
        WorkflowCommitLedgerService.Publish(paths, RicevutaDi(paths, PcAltro, id));
        var marker = ScriviMarker(paths, id, PcAltro, null, null);

        Assert.Throws<IOException>(() => SharedWorkflowTransactionGuard.EnsureNoBlockingTransaction(paths, "Test lease"));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void PreparazioneConMarkerFresco_BloccaLeLettureSenzaToccareIFile()
    {
        var paths = new DataPaths(_root);
        var id = Guid.NewGuid();
        var localDeviceId = DeviceIdentityService.GetOrCreate();
        var marker = ScriviMarker(paths, id, localDeviceId, DateTime.UtcNow, 180);
        var preparation = ScriviPreparazione(paths, id, localDeviceId);
        var manifest = File.ReadAllBytes(Path.Combine(preparation, "manifest.json"));

        Assert.False(SharedWorkflowTransactionGuard.CanReadConsistently(paths, out _));
        Assert.True(File.Exists(marker));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(preparation, "manifest.json")));
        Assert.True(Directory.Exists(preparation));

        // This synthetic marker has no owner scope to remove it, so clean the fixture directly after
        // verifying that the live-marker barrier preserves both shared and local preparation bytes.
        File.Delete(marker);
        Directory.Delete(preparation, recursive: true);
    }

    [Fact]
    public void PreparazioneVuotaScadutaSenzaRicevuta_CleanupSicuro()
    {
        var paths = new DataPaths(_root);
        var localDeviceId = DeviceIdentityService.GetOrCreate();
        WorkflowCommitLedgerService.Publish(paths, RicevutaDi(paths, localDeviceId, Guid.NewGuid()));
        var id = Guid.NewGuid();
        var marker = ScriviMarker(paths, id, localDeviceId, DateTime.UtcNow.AddMinutes(-10), 180);
        var preparation = ScriviPreparazione(paths, id, localDeviceId);

        SharedWorkflowTransactionGuard.EnsureNoBlockingTransaction(paths, "Test prepare cleanup");

        Assert.False(File.Exists(marker));
        Assert.False(Directory.Exists(preparation));
    }

    [Fact]
    public void PreparazioneScadutaConEntryNonVuota_RestaBloccante()
    {
        var paths = new DataPaths(_root);
        var localDeviceId = DeviceIdentityService.GetOrCreate();
        WorkflowCommitLedgerService.Publish(paths, RicevutaDi(paths, localDeviceId, Guid.NewGuid()));
        var id = Guid.NewGuid();
        var marker = ScriviMarker(paths, id, localDeviceId, DateTime.UtcNow.AddMinutes(-10), 180);
        var preparation = ScriviPreparazione(paths, id, localDeviceId, conEntry: true);
        var manifest = File.ReadAllBytes(Path.Combine(preparation, "manifest.json"));

        Assert.Throws<IOException>(() => SharedWorkflowTransactionGuard.EnsureNoBlockingTransaction(paths, "Test prepare cleanup"));

        Assert.True(File.Exists(marker));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(preparation, "manifest.json")));
        Assert.True(Directory.Exists(preparation));
    }

    [Fact]
    public void LetturaConsistente_NonRimuoveIlMarkerScadutoCommesso()
    {
        var paths = new DataPaths(_root);
        var id = Guid.NewGuid();
        WorkflowCommitLedgerService.Publish(paths, RicevutaDi(paths, PcAltro, id));
        var marker = ScriviMarker(paths, id, PcAltro, DateTime.UtcNow.AddMinutes(-10), 180);

        Assert.True(SharedWorkflowTransactionGuard.CanReadConsistently(paths, out _));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void Commit_ScriveIlBattitoEIlLeaseNelMarkerCondiviso()
    {
        var paths = new DataPaths(_root);
        var scope = WorkflowTransactionService.Begin(paths, "lease-battito");
        try
        {
            var marker = JsonSerializer.Deserialize<WorkflowTransactionMarker>(
                File.ReadAllBytes(Path.Combine(paths.Root, "_ditm", "transactions", $"{scope.Id:N}.pending.json")), JsonIo.Options)!;
            Assert.Equal((int)WorkflowTransactionService.MarkerLease.TotalSeconds, marker.LeaseSeconds);
            Assert.NotNull(marker.HeartbeatUtc);
            Assert.True(DateTime.UtcNow - marker.HeartbeatUtc!.Value < TimeSpan.FromMinutes(2));
        }
        finally
        {
            scope.Dispose();
        }
    }

    private static WorkflowTransactionCommitReceipt RicevutaDi(DataPaths paths, string deviceId, Guid id)
    {
        var bytes = Encoding.UTF8.GetBytes($"{{\"id\":\"{id:N}\"}}");
        return new WorkflowTransactionCommitReceipt
        {
            Id = id,
            StartedUtc = DateTime.UtcNow.AddSeconds(-1),
            CompletedUtc = DateTime.UtcNow,
            DeviceId = deviceId,
            Device = "test",
            Description = "lease",
            Entries =
            [
                new WorkflowTransactionCommitEntry
                {
                    RelativePath = WorkflowTransactionVisibilityService.NormalizeRelativePath(paths.Root, paths.NonConformitaFile($"NC-{id:N}")),
                    FinalSha256 = WorkflowTransactionVisibilityService.Hash(bytes),
                    LogicallyDeleted = false
                }
            ]
        };
    }

    private static string ScriviMarker(DataPaths paths, Guid id, string deviceId, DateTime? battito, int? lease)
    {
        var dir = Path.Combine(paths.Root, "_ditm", "transactions");
        Directory.CreateDirectory(dir);
        var marker = new WorkflowTransactionMarker
        {
            Id = id,
            DeviceId = deviceId,
            StartedUtc = DateTime.UtcNow.AddMinutes(-10),
            Device = deviceId,
            Description = "lease-test",
            HeartbeatUtc = battito,
            LeaseSeconds = lease
        };
        var path = Path.Combine(dir, $"{id:N}.pending.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(marker, JsonIo.Options));
        return path;
    }

    private static string ScriviPreparazione(DataPaths paths, Guid id, string deviceId, bool conEntry = false)
    {
        var preparation = Path.Combine(WorkflowTransactionService.WorkspaceDirectory(paths.Root), $"prepare-{id:N}");
        Directory.CreateDirectory(preparation);
        var manifest = new WorkflowTransactionManifest
        {
            Id = id,
            DeviceId = deviceId,
            WorkspaceRoot = Path.GetFullPath(paths.Root),
            Description = "lease-test-prepare",
            StartedUtc = DateTime.UtcNow,
            State = WorkflowTransactionState.Prepared,
            Entries = conEntry ? [new WorkflowTransactionEntry { Path = paths.NonConformitaFile("NC-PREPARE") }] : []
        };
        File.WriteAllBytes(Path.Combine(preparation, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, JsonIo.Options));
        return preparation;
    }

    private static void AggiornaHeartbeat(string markerPath, DateTime heartbeatUtc)
    {
        var marker = JsonSerializer.Deserialize<WorkflowTransactionMarker>(File.ReadAllBytes(markerPath), JsonIo.Options)!;
        marker.HeartbeatUtc = heartbeatUtc;
        File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker, JsonIo.Options));
    }
}
