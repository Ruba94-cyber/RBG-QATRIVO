using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Crash tra la mutazione su disco e la registrazione nel manifest (review P1.9): l'intenzione con hash atteso, registrata prima
/// di File.Move e di File.Delete, permette alla recovery di distinguere una mutazione avvenuta da una non avvenuta.
/// </summary>
public sealed class WorkflowTransactionIntentoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_TxIntento_" + Guid.NewGuid().ToString("N"));

    public WorkflowTransactionIntentoTests()
    {
        Directory.CreateDirectory(_root);
        new DataPaths(_root).EnsureStructure();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void CrashTraSpostamentoERegistrazione_LaRecoveryRipristinaIlBeforeImage()
    {
        var paths = new DataPaths(_root);
        var path = paths.NonConformitaFile("NC-P19-001");
        var uid = Guid.NewGuid();
        JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P19-001", RevisioneDati = 1 });
        var prima = File.ReadAllBytes(path);

        var scope = WorkflowTransactionService.Begin(paths, "crash-spostamento");
        var txId = scope.Id;
        JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P19-001", RevisioneDati = 2 });
        var dopo = File.ReadAllBytes(path);
        var manifest = ManifestTransazione(paths, txId);

        // Stato lasciato dal crash: la mutazione è avvenuta, la registrazione finale no.
        ModificaEntry(manifest, path, e =>
        {
            e.IntendedSha256 = WorkflowTransactionVisibilityService.Hash(dopo);
            e.FinalSha256 = null;
            e.MutationCompleted = false;
        });
        InterrompiRollback(scope, paths, manifest, txId);

        // Riavvio: la recovery confronta il contenuto con l'intenzione e riporta il before-image.
        WorkflowTransactionService.RecoverPending(paths);
        Assert.Equal(prima, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(TransazioneDirectory(paths, txId)));
    }

    [Fact]
    public void IntenzioneSenzaSpostamento_LaRecoveryNonToccaIlFile()
    {
        var paths = new DataPaths(_root);
        var path = paths.NonConformitaFile("NC-P19-002");
        var uid = Guid.NewGuid();
        JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P19-002", RevisioneDati = 1 });
        var prima = File.ReadAllBytes(path);

        var scope = WorkflowTransactionService.Begin(paths, "intenzione-senza-spostamento");
        var txId = scope.Id;
        JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P19-002", RevisioneDati = 2 });
        var manifest = ManifestTransazione(paths, txId);

        // Crash dopo la registrazione dell'intenzione e prima di File.Move: il file è ancora l'originale.
        ModificaEntry(manifest, path, e =>
        {
            e.IntendedSha256 = WorkflowTransactionVisibilityService.Hash(File.ReadAllBytes(path));
            e.FinalSha256 = null;
            e.MutationCompleted = false;
        });
        File.WriteAllBytes(path, prima);
        InterrompiRollback(scope, paths, manifest, txId);

        WorkflowTransactionService.RecoverPending(paths);
        Assert.Equal(prima, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(TransazioneDirectory(paths, txId)));
    }

    [Fact]
    public void ContenutoEstraneoDopoLIntenzione_LaRecoveryBloccaSenzaSovrascrivere()
    {
        var paths = new DataPaths(_root);
        var path = paths.NonConformitaFile("NC-P19-003");
        var uid = Guid.NewGuid();
        JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P19-003", RevisioneDati = 1 });

        var scope = WorkflowTransactionService.Begin(paths, "contenuto-estraneo");
        var txId = scope.Id;
        JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P19-003", RevisioneDati = 2 });
        var dopo = File.ReadAllBytes(path);
        var manifest = ManifestTransazione(paths, txId);
        ModificaEntry(manifest, path, e =>
        {
            e.IntendedSha256 = WorkflowTransactionVisibilityService.Hash(dopo);
            e.FinalSha256 = null;
            e.MutationCompleted = false;
        });

        // Dopo l'intenzione un altro PC ha scritto un contenuto che non è né il nostro né l'originale.
        var estraneo = Encoding.UTF8.GetBytes("{\"altroPc\":true}");
        File.WriteAllBytes(path, estraneo);
        InterrompiRollback(scope, paths, manifest, txId);

        Assert.Throws<InvalidDataException>(() => WorkflowTransactionService.RecoverPending(paths));
        Assert.Equal(estraneo, File.ReadAllBytes(path));
        Assert.True(Directory.Exists(TransazioneDirectory(paths, txId)));
    }

    [Fact]
    public void RecoveryConMarkerDellaStessaTransazioneAncoraAttivo_NonRipristinaIlBeforeImage()
    {
        var paths = new DataPaths(_root);
        var path = paths.NonConformitaFile("NC-P19-004");
        var uid = Guid.NewGuid();
        JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P19-004", RevisioneDati = 1 });
        var prima = File.ReadAllBytes(path);

        var scope = WorkflowTransactionService.Begin(paths, "recovery-marker-live");
        var txId = scope.Id;
        JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P19-004", RevisioneDati = 2 });
        var dopo = File.ReadAllBytes(path);
        var txDir = TransazioneDirectory(paths, txId);
        var manifest = ManifestTransazione(paths, txId);
        var marker = Path.Combine(paths.Root, "_ditm", "transactions", $"{txId:N}.pending.json");
        var manifestLive = File.ReadAllBytes(manifest);
        var markerLive = File.ReadAllBytes(marker);
        var manifestDeviceId = JsonSerializer.Deserialize<WorkflowTransactionManifest>(manifestLive, JsonIo.Options)!.DeviceId;

        // Come un altro processo, si rimuove solo lo stato _attive locale lasciando marker e manifest vivi.
        InterrompiRollback(scope, paths, manifest, txId, scadereMarker: false);

        Assert.Throws<InvalidDataException>(() => WorkflowTransactionService.RecoverPending(paths));
        Assert.Equal(dopo, File.ReadAllBytes(path));
        Assert.Equal(markerLive, File.ReadAllBytes(marker));
        Assert.Equal(manifestLive, File.ReadAllBytes(manifest));
        Assert.True(Directory.Exists(txDir));

        // Anche un marker scaduto non autorizza il rollback se la sua identità non corrisponde al manifest.
        var markerState = JsonSerializer.Deserialize<WorkflowTransactionMarker>(markerLive, JsonIo.Options)!;
        var ownerDeviceId = markerState.DeviceId;
        markerState.DeviceId = "dispositivo-diverso";
        markerState.HeartbeatUtc = DateTime.UtcNow.AddMinutes(-10);
        File.WriteAllBytes(marker, JsonSerializer.SerializeToUtf8Bytes(markerState, JsonIo.Options));
        Assert.Throws<InvalidDataException>(() => WorkflowTransactionService.RecoverPending(paths));
        Assert.Equal(dopo, File.ReadAllBytes(path));

        // Una volta scaduto il lease, la recovery può applicare la decisione tri-state: ricevuta assente,
        // quindi ripristina il before-image e pulisce marker e directory della transazione.
        markerState.DeviceId = ownerDeviceId;
        Assert.Equal(manifestDeviceId, ownerDeviceId);
        File.WriteAllBytes(marker, JsonSerializer.SerializeToUtf8Bytes(markerState, JsonIo.Options));
        WorkflowTransactionService.RecoverPending(paths);

        Assert.Equal(prima, File.ReadAllBytes(path));
        Assert.False(File.Exists(marker));
        Assert.False(Directory.Exists(txDir));
    }

    [Fact]
    public void RecoveryConMarkerScadutoERicevutaPubblicata_PreservaIlCommitEPulisce()
    {
        var paths = new DataPaths(_root);
        var path = paths.NonConformitaFile("NC-P19-005");
        var uid = Guid.NewGuid();
        JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P19-005", RevisioneDati = 1 });

        var scope = WorkflowTransactionService.Begin(paths, "recovery-committed-marker-stale");
        var txId = scope.Id;
        JsonIo.WriteAtomic(path, new NonConformita { Uid = uid, Id = "NC-P19-005", RevisioneDati = 2 });
        var committedBytes = File.ReadAllBytes(path);
        var txDir = TransazioneDirectory(paths, txId);
        var manifestPath = ManifestTransazione(paths, txId);
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var markerPath = Path.Combine(paths.Root, "_ditm", "transactions", $"{txId:N}.pending.json");
        var markerState = JsonSerializer.Deserialize<WorkflowTransactionMarker>(File.ReadAllBytes(markerPath), JsonIo.Options)!;

        scope.Commit();
        scope.Dispose();

        // Simula l'arresto dopo la ricevuta di commit ma prima della pulizia locale: restano manifest e marker scaduto.
        Directory.CreateDirectory(txDir);
        File.WriteAllBytes(manifestPath, manifestBytes);
        markerState.HeartbeatUtc = DateTime.UtcNow.AddMinutes(-10);
        File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(markerState, JsonIo.Options));

        WorkflowTransactionService.RecoverPending(paths);

        Assert.Equal(committedBytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(markerPath));
        Assert.False(Directory.Exists(txDir));
    }

    [Fact]
    public void CrashDopoEliminazioneFisica_LaRecoveryRipristinaIlFile()
    {
        var paths = new DataPaths(_root);
        var aux = Path.Combine(_root, "appunti", "bozza-P19.json");
        Directory.CreateDirectory(Path.GetDirectoryName(aux)!);
        var prima = Encoding.UTF8.GetBytes("{\"bozza\":1}");
        File.WriteAllBytes(aux, prima);

        var scope = WorkflowTransactionService.Begin(paths, "crash-eliminazione");
        var txId = scope.Id;
        WorkflowTransactionService.DeleteFile(aux);
        Assert.False(File.Exists(aux));
        var manifest = ManifestTransazione(paths, txId);

        // Crash dopo File.Delete e prima della registrazione: il manifest conserva solo l'intenzione di eliminazione.
        ModificaEntry(manifest, aux, e =>
        {
            e.PhysicallyDeleted = false;
            e.MutationCompleted = false;
            e.IntendedDelete = true;
        });
        InterrompiRollback(scope, paths, manifest, txId);

        WorkflowTransactionService.RecoverPending(paths);
        Assert.Equal(prima, File.ReadAllBytes(aux));
    }

    /// <summary>Il rollback non riesce a riscrivere il manifest, come accade dopo un crash: la transazione esce dalla memoria senza altri effetti.</summary>
    private static void InterrompiRollback(
        WorkflowTransactionService.WorkflowTransactionScope scope,
        DataPaths paths,
        string manifestPath,
        Guid transactionId,
        bool scadereMarker = true)
    {
        using (new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<Exception>(() => scope.Rollback());
        }
        scope.Dispose();

        if (scadereMarker)
        {
            var markerPath = Path.Combine(paths.Root, "_ditm", "transactions", $"{transactionId:N}.pending.json");
            AggiornaHeartbeat(markerPath, DateTime.UtcNow.AddMinutes(-10));
        }
    }

    private static void AggiornaHeartbeat(string markerPath, DateTime heartbeatUtc)
    {
        var marker = JsonSerializer.Deserialize<WorkflowTransactionMarker>(File.ReadAllBytes(markerPath), JsonIo.Options)!;
        marker.HeartbeatUtc = heartbeatUtc;
        File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker, JsonIo.Options));
    }

    /// <summary>Modifica lo stato di una entry nel manifest su disco, per riprodurre il punto in cui il processo è caduto.</summary>
    private static void ModificaEntry(string manifestPath, string target, Action<WorkflowTransactionEntry> modifica)
    {
        var manifest = JsonSerializer.Deserialize<WorkflowTransactionManifest>(File.ReadAllBytes(manifestPath), JsonIo.Options)!;
        var entry = manifest.Entries.Single(e => string.Equals(e.Path, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase));
        modifica(entry);
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonIo.Options));
    }

    private static string ManifestTransazione(DataPaths paths, Guid txId) =>
        Path.Combine(TransazioneDirectory(paths, txId), "manifest.json");

    private static string TransazioneDirectory(DataPaths paths, Guid txId) =>
        Path.Combine(WorkflowTransactionService.WorkspaceDirectory(paths.Root), $"tx-{txId:N}");
}
