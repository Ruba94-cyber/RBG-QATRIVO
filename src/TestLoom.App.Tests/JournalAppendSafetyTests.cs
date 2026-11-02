using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class JournalAppendSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_JournalAppend_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_JournalAppend_Local_" + Guid.NewGuid().ToString("N"));

    public JournalAppendSafetyTests()
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
    public void TryPublish_CurrentConCodaParziale_RiparaLaCodaEAccodaLEvento()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        const string device = "device-partial-tail";
        var deviceDir = Path.Combine(_root, "_ditm", "journal", device);
        Directory.CreateDirectory(deviceDir);
        var current = Path.Combine(deviceDir, "current.ndjson");
        const string partial = "{\"SchemaVersion\":1,\"EventId\":\"parziale";
        File.WriteAllText(current, partial); // volutamente senza newline finale

        var journal = new SharedTestJournalService(paths, _local, deviceIdOverride: device);
        var tc = new TestCase
        {
            Id = "TC-TAIL",
            Uid = Guid.NewGuid(),
            RevisioneDati = 1,
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
            Descrizione = "payload"
        };
        var testPath = Path.Combine(paths.TestCasesDir, "testcase_TC-TAIL.json");
        JsonIo.WriteAtomic(testPath, tc);
        var pending = journal.Prepare(JournalOperation.Create, null, tc, null, testPath);

        // La coda parziale (crash a metà riga) viene spostata in current.ndjson.torn-* prima dell'append: il journal
        // non resta bloccato per sempre e la sola riga valida nel ledger è l'evento appena pubblicato.
        Assert.True(journal.TryPublish(pending));
        var laterale = Assert.Single(Directory.GetFiles(deviceDir, "current.ndjson.torn-*"));
        Assert.Equal(partial, File.ReadAllText(laterale));
        var righe = File.ReadAllLines(current).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        Assert.Single(righe);
        Assert.Contains("TC-TAIL", righe[0]);
    }

    private static void TryDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
