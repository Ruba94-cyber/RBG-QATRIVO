using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class JournalCompactionSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_JournalCompact_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_JournalCompact_Local_" + Guid.NewGuid().ToString("N"));

    public JournalCompactionSafetyTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDelete(_root);
        TryDelete(_local);
    }

    [Fact]
    public void Compaction_SegmentoMalformatoNonVieneCheckpointatoNeCancellato()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        const string device = "device-compact";
        var deviceDir = Path.Combine(_root, "_ditm", "journal", device);
        Directory.CreateDirectory(deviceDir);

        // Nome volutamente precedente ai segmenti generati dalla rotazione, così diventa il primo candidato
        // alla compattazione quando completedSegmentsToKeep=1.
        var malformed = Path.Combine(deviceDir, "segment-00000000000000000-corrupt.ndjson");
        const string originalBytes = "{\"SchemaVersion\":1,\"EventId\":\"troncato";
        File.WriteAllText(malformed, originalBytes);

        var journal = new SharedTestJournalService(
            paths,
            _local,
            deviceIdOverride: device,
            rotateAfterBytesOverride: 256,
            completedSegmentsToKeepOverride: 1);

        var tc = new TestCase
        {
            Id = "TC-" + new string('X', 400), // garantisce che una singola riga superi la soglia di rotazione
            Uid = Guid.NewGuid(),
            RevisioneDati = 1,
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
            Descrizione = "payload"
        };
        var testPath = Path.Combine(paths.TestCasesDir, "testcase_compaction.json");
        JsonIo.WriteAtomic(testPath, tc);
        var pending = journal.Prepare(JournalOperation.Create, null, tc, null, testPath);

        Assert.True(journal.TryPublish(pending));

        Assert.True(File.Exists(malformed));
        Assert.Equal(originalBytes, File.ReadAllText(malformed));
        Assert.False(File.Exists(Path.Combine(deviceDir, "checkpoint.json.gz")));
        Assert.True(Directory.GetFiles(deviceDir, "segment-*.ndjson").Length >= 2);
    }

    private static void TryDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
