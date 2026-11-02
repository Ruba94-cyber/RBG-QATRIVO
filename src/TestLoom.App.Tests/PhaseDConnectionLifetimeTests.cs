using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

public sealed class PhaseDConnectionLifetimeTests
{
    [Fact]
    public void ReadPoolSurvivesWriterPoolClear_NextRefreshRemainsWritable()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_ConnectionLifetime_" + Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        paths.EnsureStructure();
        // EnsureStructure() crea soltanto le cartelle: l'attivazione di Phase D passa esclusivamente dal gate
        // di compatibilita' versione (CompatibilitaVersioneService.Applica in InizializzaCartella). Qui serve
        // esplicitamente pronto prima di GetCatalogState, quindi lo bootstrappiamo direttamente.
        PhaseDMasterBootstrapService.EnsureReady(paths);
        try
        {
            var repo = new MasterDataRepository<Requisito>(paths.RequisitiFile);
            repo.Save([new Requisito { Id = "REQ-1", Testo = "before" }]);
            var materializer = new PhaseDMasterMaterializer(root);
            var reader = new PhaseDMasterScalableReader(root);
            reader.GetCatalogState("requisiti");

            // A read-only pooled connection can outlive the writer pool during maintenance.
            using (var writerPool = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = materializer.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared, Pooling = true
            }.ToString()))
                SqliteConnection.ClearPool(writerPool);

            var items = repo.Load();
            items[0].Testo = "after";
            repo.Save(items);
            Assert.Equal("after", Assert.Single(repo.Load()).Testo);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
