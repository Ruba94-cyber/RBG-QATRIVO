using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

[Collection("Master index recovery")]
public sealed class MasterDataLocalIndexCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_MasterIndexCoordinator_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(_root)) return;
        try { Directory.Delete(_root, true); }
        catch { }
    }

    [Fact]
    public void Refresh_ReindicizzaSoloQuandoIlJsonAutorevoleCambia()
    {
        var shared = Path.Combine(_root, "shared");
        var local = Path.Combine(_root, "local");
        Directory.CreateDirectory(shared);
        var paths = new DataPaths(shared);
        JsonIo.WriteAtomic(paths.RequisitiFile, new List<Requisito>
        {
            new() { Id = "R-1", Testo = "Primo requisito", ModificatoDa = "Seed" },
            new() { Id = "R-2", Testo = "Secondo requisito", ModificatoDa = "Seed" }
        });

        var coordinator = new MasterDataLocalIndexCoordinator(paths, local);
        var first = coordinator.RefreshRequisiti();
        Assert.False(first.Reconcile.SkippedAsCurrent);
        Assert.Equal(2, first.Reconcile.Added);

        var second = coordinator.RefreshRequisiti();
        Assert.True(second.Reconcile.SkippedAsCurrent);
        Assert.Equal(first.SourceFingerprint, second.SourceFingerprint);

        JsonIo.WriteAtomic(paths.RequisitiFile, new List<Requisito>
        {
            new() { Id = "R-1", Testo = "Primo requisito modificato", ModificatoDa = "PC-B" },
            new() { Id = "R-3", Testo = "Terzo requisito", ModificatoDa = "PC-B" }
        });
        var third = coordinator.RefreshRequisiti();

        Assert.NotEqual(first.SourceFingerprint, third.SourceFingerprint);
        Assert.Equal(1, third.Reconcile.Added);
        Assert.Equal(1, third.Reconcile.Updated);
        Assert.Equal(1, third.Reconcile.Removed);
        Assert.Equal("R-1", Assert.Single(coordinator.Store.Query<Requisito>(
            MasterDataLocalIndexCoordinator.RequisitiCatalog, "modificato", false, 0, 20)).Key);
    }

    [Fact]
    public void JsonAutorevoleCorrotto_NonSvuotaNeSovrascriveLaFotografiaLocaleValida()
    {
        var shared = Path.Combine(_root, "shared-corrupt");
        var local = Path.Combine(_root, "local-corrupt");
        Directory.CreateDirectory(shared);
        var paths = new DataPaths(shared);
        JsonIo.WriteAtomic(paths.EcuFile, new List<Componente>
        {
            new() { Nome = "BCM", VeicoliAssociati = new() { "Daily" }, ModificatoDa = "Seed" }
        });

        var coordinator = new MasterDataLocalIndexCoordinator(paths, local);
        coordinator.RefreshEcu();
        Assert.Equal(1, coordinator.Store.Count(MasterDataLocalIndexCoordinator.EcuCatalog, true));

        File.WriteAllText(paths.EcuFile, "[{ json troncato");
        Assert.Throws<MasterDataReadException>(() => coordinator.RefreshEcu());

        // Il DB resta soltanto una cache della precedente fotografia valida. Non viene promosso ad autorita',
        // ma soprattutto una replica parziale non lo trasforma in un falso catalogo vuoto.
        Assert.Equal(1, coordinator.Store.Count(MasterDataLocalIndexCoordinator.EcuCatalog, true));
        Assert.Equal("BCM", Assert.Single(coordinator.Store.Query<Componente>(
            MasterDataLocalIndexCoordinator.EcuCatalog, "Daily", true, 0, 20)).Key);
    }

    [Fact]
    public void MasterAutorevoleMancante_FallisceChiusoESenzaUsareSQLiteComeFallbackAutoritativo()
    {
        var shared = Path.Combine(_root, "shared-missing");
        Directory.CreateDirectory(shared);
        var paths = new DataPaths(shared);
        var coordinator = new MasterDataLocalIndexCoordinator(paths, Path.Combine(_root, "local-missing"));

        Assert.Throws<MasterDataReadException>(() => coordinator.RefreshFunzionalita());
        Assert.Equal(0, coordinator.Store.Count(MasterDataLocalIndexCoordinator.FunzionalitaCatalog, true));
    }

    [Fact]
    public void QueryMasterCorrotta_RicostruisceTuttiICataloghiDaJson()
    {
        var shared = Path.Combine(_root, "shared-recovery");
        var local = Path.Combine(_root, "local-recovery");
        Directory.CreateDirectory(shared);
        var paths = new DataPaths(shared);
        JsonIo.WriteAtomic(paths.RequisitiFile, new List<Requisito> { new() { Id = "R-1", Testo = "Test" } });
        JsonIo.WriteAtomic(paths.FunzionalitaFile, new List<Funzionalita> { new() { Nome = "Funzione" } });
        JsonIo.WriteAtomic(paths.EcuFile, new List<Componente> { new() { Nome = "BCM" } });
        var coordinator = new MasterDataLocalIndexCoordinator(paths, local);
        coordinator.RefreshHighGrowthCatalogs();
        var originali = new[] { paths.RequisitiFile, paths.FunzionalitaFile, paths.EcuFile }
            .Select(File.ReadAllBytes).ToArray();

        SqliteConnection.ClearAllPools();
        using (var file = new FileStream(coordinator.Store.DatabasePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            file.Write(new byte[100]);

        Assert.True(coordinator.IndexRecovery.TryRead("query master corrotta",
            () => coordinator.Store.Count(MasterDataLocalIndexCoordinator.RequisitiCatalog, true), out var count));
        Assert.Equal(1, count);
        Assert.Equal(LocalIndexHealth.Healthy, coordinator.IndexRecovery.Salute);
        Assert.Equal(1, coordinator.Store.Count(MasterDataLocalIndexCoordinator.FunzionalitaCatalog, true));
        Assert.Equal(1, coordinator.Store.Count(MasterDataLocalIndexCoordinator.EcuCatalog, true));
        Assert.Equal(originali, new[] { paths.RequisitiFile, paths.FunzionalitaFile, paths.EcuFile }
            .Select(File.ReadAllBytes).ToArray(), new ByteArrayComparer());
        Assert.NotEmpty(Directory.GetFiles(coordinator.Store.WorkspaceDirectory, "master-index.db.corrupt-*"));
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x != null && y != null && x.SequenceEqual(y);
        public int GetHashCode(byte[] obj) => obj.Length;
    }
}
