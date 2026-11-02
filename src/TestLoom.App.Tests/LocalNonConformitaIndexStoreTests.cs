using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>LocalNonConformitaIndexStore (feedback utente 2026-09-17: "per test era stato inserito un db
/// sqlite... dovrebbe essere gestito tutto nel modo più veloce e perfetto possibile" — stesso principio di
/// LocalTestIndexStore applicato alle Non Conformità, per non dover ririleggere ogni JSON a ogni avvio a
/// volumi (100k) grandi.</summary>
public sealed class LocalNonConformitaIndexStoreTests : IDisposable
{
    private readonly string _shared = Path.Combine(Path.GetTempPath(), "TestLoom_NcLocalIndex_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_NcLocalIndex_Local_" + Guid.NewGuid().ToString("N"));

    public LocalNonConformitaIndexStoreTests()
    {
        Directory.CreateDirectory(_shared);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDelete(_shared);
        TryDelete(_local);
    }

    [Fact]
    public void DatabaseRestaFuoriDallaCartellaCondivisa()
    {
        var store = new LocalNonConformitaIndexStore(_shared, _local);

        Assert.StartsWith(Path.GetFullPath(_local), Path.GetFullPath(store.DatabasePath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(_shared), Path.GetFullPath(store.DatabasePath), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(store.DatabasePath));
    }

    [Fact]
    public void FullReconcileVuoto_EUnWarmSnapshotValido()
    {
        var store = new LocalNonConformitaIndexStore(_shared, _local);
        Assert.False(store.HasCompletedFullScan);

        store.ReconcileFull(Array.Empty<LocalIndexWriteEntryNc>(), DateTime.UtcNow);

        var reopened = new LocalNonConformitaIndexStore(_shared, _local);
        Assert.True(reopened.HasCompletedFullScan);
        Assert.Empty(reopened.LoadAll());
    }

    [Fact]
    public void Reopen_RipristinaLaNonConformitaSenzaLeggereIlJsonCondiviso()
    {
        var store = new LocalNonConformitaIndexStore(_shared, _local);
        var nc = NuovaNc("NC-0100");
        var path = Path.Combine(_shared, "nc_NC-0100.json");
        var when = DateTime.UtcNow.AddMinutes(-1);

        store.ReconcileFull([new LocalIndexWriteEntryNc(path, when, 123, nc)], DateTime.UtcNow);

        var reopened = new LocalNonConformitaIndexStore(_shared, _local);
        var rows = reopened.LoadAll();
        var loaded = Assert.Single(rows);
        Assert.Equal(path, loaded.Path);
        Assert.Equal(nc.Uid, loaded.Nc.Uid);
        Assert.Equal("NC-0100", loaded.Nc.Id);
        Assert.Equal(123, loaded.FileLength);
        Assert.Equal(when.Ticks, loaded.MtimeUtc.Ticks);
    }

    [Fact]
    public void ReconcileSuccessivo_RimuoveRecordNonPiuPresenti()
    {
        var store = new LocalNonConformitaIndexStore(_shared, _local);
        var a = NuovaNc("NC-0001");
        var b = NuovaNc("NC-0002");
        var pathA = Path.Combine(_shared, "nc_NC-0001.json");
        var pathB = Path.Combine(_shared, "nc_NC-0002.json");
        var now = DateTime.UtcNow;

        store.ReconcileFull(
        [
            new LocalIndexWriteEntryNc(pathA, now, 10, a),
            new LocalIndexWriteEntryNc(pathB, now, 20, b)
        ], now);

        store.ReconcileFull([new LocalIndexWriteEntryNc(pathB, now.AddSeconds(1), 21, b)], now.AddSeconds(1));

        var rows = store.LoadAll();
        var only = Assert.Single(rows);
        Assert.Equal("NC-0002", only.Nc.Id);
        Assert.Equal(21, only.FileLength);
    }

    [Fact]
    public void Repository_WarmStartERiconciliazioneRestanoAutoritativiSuiJson()
    {
        var paths = new DataPaths(_shared);
        paths.EnsureStructure();
        var localStore = new LocalNonConformitaIndexStore(_shared, _local);
        var repo = new NonConformitaRepository(paths, TimeSpan.Zero, localStore);
        var nc = NuovaNc("NC-0200");

        repo.Save(nc);
        repo.RefreshIndex();

        var reopened = new NonConformitaRepository(paths, TimeSpan.Zero, new LocalNonConformitaIndexStore(_shared, _local));
        Assert.Equal("NC-0200", Assert.Single(reopened.GetAll()).Id);

        File.Delete(paths.NonConformitaFile("NC-0200"));
        reopened.RefreshIndex();
        Assert.Empty(reopened.GetAll());

        var afterReconcile = new NonConformitaRepository(paths, TimeSpan.Zero, new LocalNonConformitaIndexStore(_shared, _local));
        Assert.Empty(afterReconcile.GetAll());
    }

    [Fact]
    public void Repository_EliminaRimuoveAncheDallIndiceLocale()
    {
        var paths = new DataPaths(_shared);
        paths.EnsureStructure();
        var localStore = new LocalNonConformitaIndexStore(_shared, _local);
        var repo = new NonConformitaRepository(paths, TimeSpan.Zero, localStore);
        var nc = NuovaNc("NC-0300");
        repo.Save(nc);

        repo.Elimina(nc);

        var reopened = new NonConformitaRepository(paths, TimeSpan.Zero, new LocalNonConformitaIndexStore(_shared, _local));
        Assert.Empty(reopened.GetAll());
    }

    [Fact]
    public void Repository_CorruzioneDuranteReconcile_RicostruisceDalJsonSenzaPerdereIlSave()
    {
        var paths = new DataPaths(_shared);
        paths.EnsureStructure();
        var store = new LocalNonConformitaIndexStore(_shared, _local);
        var repo = new NonConformitaRepository(paths, TimeSpan.Zero, store);
        var nc = NuovaNc("NC-0400");
        repo.Save(nc);
        var json = File.ReadAllBytes(paths.NonConformitaFile(nc.Id));

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using (var file = new FileStream(store.DatabasePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            file.Write(new byte[100]);

        repo.RefreshIndex();
        Assert.True(repo.IndexRecovery!.AttendiRecupero(TimeSpan.FromSeconds(15)));
        Assert.Equal(LocalIndexHealth.Healthy, repo.IndexRecovery.Salute);
        Assert.Equal(json, File.ReadAllBytes(paths.NonConformitaFile(nc.Id)));
        Assert.Equal(nc.Id, Assert.Single(store.LoadAll()).Nc.Id);
        Assert.NotEmpty(Directory.GetFiles(store.WorkspaceDirectory, "nc-index.db.corrupt-*"));
    }

    private static NonConformita NuovaNc(string id) => new()
    {
        Id = id,
        Owner = "Operatore Prova",
        Descrizione = "Descrizione di prova",
    };

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
