using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class LocalTestIndexBatchTests : IDisposable
{
    private readonly string _shared = Path.Combine(Path.GetTempPath(), "TestLoom_LocalBatch_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_LocalBatch_Local_" + Guid.NewGuid().ToString("N"));

    public LocalTestIndexBatchTests()
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
    public void ApplyBatch_PiuUpsert_SonoVisibiliComeUnUnicoAggiornamentoLocale()
    {
        var store = new LocalTestIndexStore(_shared, _local);
        var now = DateTime.UtcNow;
        var a = NuovoTest("TC-B001", "alpha");
        var b = NuovoTest("TC-B002", "beta");
        var pathA = Path.Combine(_shared, "testcase_TC-B001.json");
        var pathB = Path.Combine(_shared, "testcase_TC-B002.json");

        store.ApplyBatch([
            LocalIndexMutation.Upsert(pathA, now, 101, a),
            LocalIndexMutation.Upsert(pathB, now.AddMilliseconds(1), 202, b)
        ]);

        var rows = store.LoadAll();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, x => x.Path == pathA && x.TestCase.Id == "TC-B001" && x.FileLength == 101);
        Assert.Contains(rows, x => x.Path == pathB && x.TestCase.Id == "TC-B002" && x.FileLength == 202);
        Assert.Equal(2, store.GetArchiveStatistics().TestCount);
    }

    [Fact]
    public void ApplyBatch_MoveRemovePiuUpsertStessoUid_NonPerdeLaProiezione()
    {
        var store = new LocalTestIndexStore(_shared, _local);
        var tc = NuovoTest("TC-MOVE", "move");
        var oldPath = Path.Combine(_shared, "old", "testcase_TC-MOVE.json");
        var newPath = Path.Combine(_shared, "new", "testcase_TC-MOVE.json");
        var now = DateTime.UtcNow;
        store.Upsert(oldPath, now, 123, tc);
        Assert.Equal(1, store.GetArchiveStatistics().TestCount);

        store.ApplyBatch([
            LocalIndexMutation.Remove(oldPath),
            LocalIndexMutation.Upsert(newPath, now.AddSeconds(1), 124, tc)
        ]);

        var row = Assert.Single(store.LoadAll());
        Assert.Equal(newPath, row.Path);
        Assert.Equal(tc.Uid, row.TestCase.Uid);
        Assert.Equal(1, store.GetArchiveStatistics().TestCount);
        if (store.FtsEnabled)
            Assert.Equal(tc.Uid, Assert.Single(store.SearchTestUids("move")));
    }

    [Fact]
    public void ApplyBatch_StessoPathUltimaMutazioneVince()
    {
        var store = new LocalTestIndexStore(_shared, _local);
        var path = Path.Combine(_shared, "testcase_TC-LAST.json");
        var first = NuovoTest("TC-LAST", "prima");
        var second = NuovoTest("TC-LAST", "seconda");
        second.Uid = first.Uid;
        var now = DateTime.UtcNow;

        store.ApplyBatch([
            LocalIndexMutation.Upsert(path, now, 10, first),
            LocalIndexMutation.Remove(path),
            LocalIndexMutation.Upsert(path, now.AddSeconds(2), 30, second)
        ]);

        var row = Assert.Single(store.LoadAll());
        Assert.Equal("seconda", row.TestCase.DescrizioneBreve);
        Assert.Equal(30, row.FileLength);
        Assert.Equal(1, store.GetArchiveStatistics().TestCount);
    }

    [Fact]
    public void ApplyBatch_ErroreInUnaMutazione_FaRollbackDellInteroBatch()
    {
        var store = new LocalTestIndexStore(_shared, _local);
        var stable = NuovoTest("TC-STABLE", "stabile");
        var stablePath = Path.Combine(_shared, "testcase_TC-STABLE.json");
        var candidate = NuovoTest("TC-ROLLBACK", "non deve restare");
        var candidatePath = Path.Combine(_shared, "testcase_TC-ROLLBACK.json");
        var badPath = Path.Combine(_shared, "testcase_BAD.json");
        var now = DateTime.UtcNow;
        store.Upsert(stablePath, now, 10, stable);

        var invalid = new LocalIndexMutation(
            LocalIndexMutationKind.Upsert,
            badPath,
            now.AddSeconds(2),
            99,
            null);

        Assert.Throws<InvalidDataException>(() => store.ApplyBatch([
            LocalIndexMutation.Upsert(candidatePath, now.AddSeconds(1), 20, candidate),
            invalid
        ]));

        var row = Assert.Single(store.LoadAll());
        Assert.Equal("TC-STABLE", row.TestCase.Id);
        Assert.Equal(stablePath, row.Path);
        Assert.Equal(1, store.GetArchiveStatistics().TestCount);
    }

    [Fact]
    public void ApplyBatch_RemoveVero_RimuoveAncheLaProiezioneOrfana()
    {
        var store = new LocalTestIndexStore(_shared, _local);
        var tc = NuovoTest("TC-DELETE", "delete target");
        var path = Path.Combine(_shared, "testcase_TC-DELETE.json");
        store.Upsert(path, DateTime.UtcNow, 10, tc);
        Assert.Equal(1, store.GetArchiveStatistics().TestCount);

        store.ApplyBatch([LocalIndexMutation.Remove(path)]);

        Assert.Empty(store.LoadAll());
        Assert.Equal(0, store.GetArchiveStatistics().TestCount);
        if (store.FtsEnabled)
            Assert.DoesNotContain(tc.Uid, store.SearchTestUids("delete"));
    }

    private static TestCase NuovoTest(string id, string descrizione) => new()
    {
        Uid = Guid.NewGuid(),
        Id = id,
        Componente = new EcuSelection { "Componente-A" },
        Veicolo = "Vehicle-A",
        Veicoli = ["Vehicle-A"],
        DescrizioneBreve = descrizione,
        Descrizione = descrizione,
        StatoLavoro = StatoLavoroTest.Pianificato,
        RevisioneDati = 1,
        DataCreazione = DateTime.Now,
        UltimaModifica = DateTime.Now
    };

    private static void TryDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
