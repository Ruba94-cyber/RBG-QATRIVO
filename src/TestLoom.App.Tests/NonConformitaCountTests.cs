using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

public sealed class NonConformitaCountTests
{
    [Fact]
    public void Count_SeesRemoteRefreshAndWorkingCopiesWithoutChangingStoredState()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_NcCount_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new DataPaths(Path.Combine(root, "data"));
            paths.EnsureStructure();
            var store = new LocalNonConformitaIndexStore(paths.Root, Path.Combine(root, "local"));
            var repository = new NonConformitaRepository(paths, TimeSpan.Zero, store);
            var nc = new NonConformita { Id = "NC-1", Uid = Guid.NewGuid(), StatoWorkflow = StatoAnomalia.InVerifica };
            var path = Path.Combine(paths.NonConformitaDir, "nc_1.json");
            JsonIo.WriteAtomic(path, nc);
            repository.RefreshIndex();
            Assert.Equal(1, repository.CountByState(StatoNonConformita.Aperta));
            var working = repository.GetAll().Single();
            working.StatoWorkflow = StatoAnomalia.Conclusa;
            using (WorkflowWorkingCopyScope.Begin(nonConformita: [working]))
            {
                Assert.Equal(0, repository.CountByState(StatoNonConformita.Aperta));
                Assert.Equal(repository.GetAll().LongCount(n => n.Stato == StatoNonConformita.Chiusa),
                    repository.CountByState(StatoNonConformita.Chiusa));
            }
            Assert.Equal(1, repository.CountByState(StatoNonConformita.Aperta));
            JsonIo.WriteAtomic(path, working);
            repository.RefreshIndex();
            Assert.Equal(0, repository.CountByState(StatoNonConformita.Aperta));
            Assert.Equal(1, repository.CountByState(StatoNonConformita.Chiusa));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
