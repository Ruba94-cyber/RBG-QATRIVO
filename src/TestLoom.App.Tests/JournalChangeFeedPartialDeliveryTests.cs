using System.Text.Json;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class JournalChangeFeedPartialDeliveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_FeedPartial_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_FeedPartial_Local_" + Guid.NewGuid().ToString("N"));

    public JournalChangeFeedPartialDeliveryTests()
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
    public void Poll_RigaParzialeNonAvanzaFileState_ECompletamentoVieneRiletto()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var index = new LocalTestIndexStore(_root, _local);
        var feed = new JournalChangeFeedService(paths, index.WorkspaceDirectory);
        Assert.True(feed.EstablishBaseline());

        var testPath = Path.Combine(paths.TestCasesDir, "testcase_T-PARTIAL.json");
        var evt = new TestJournalEvent
        {
            SchemaVersion = SharedTestJournalService.JournalSchemaVersion,
            EventId = Guid.NewGuid().ToString("N"),
            DeviceId = "device-partial",
            TimestampUtc = DateTime.UtcNow,
            Operation = JournalOperation.Save,
            EntityUid = Guid.NewGuid(),
            EntityId = "T-PARTIAL",
            BaseRevision = 1,
            BaseHash = "base",
            NewRevision = 2,
            NewHash = "new",
            OldRelativePath = Path.GetRelativePath(_root, testPath),
            NewRelativePath = Path.GetRelativePath(_root, testPath)
        };
        var options = new JsonSerializerOptions(JsonIo.Options) { WriteIndented = false };
        var json = JsonSerializer.Serialize(evt, options);

        var deviceDir = Path.Combine(_root, "_ditm", "journal", evt.DeviceId);
        Directory.CreateDirectory(deviceDir);
        var current = Path.Combine(deviceDir, "current.ndjson");
        File.WriteAllText(current, json[..(json.Length / 2)]);

        var partial = feed.Poll();
        Assert.False(partial.RequiresFullReconciliation);
        Assert.Empty(partial.Events);
        Assert.Empty(partial.Files);
        feed.Ack(partial);

        // Completa la stessa riga dopo che il primo Poll l'ha osservata troncata. Poiché il file parziale non
        // è stato ackato, il feed deve rileggere il file intero e consegnare l'evento invece di perderlo.
        File.WriteAllText(current, json + Environment.NewLine);

        var completed = feed.Poll();
        Assert.Single(completed.Events);
        Assert.Equal(evt.EventId, completed.Events[0].EventId);
        Assert.Contains(completed.Paths, p =>
            Path.GetFullPath(p).Equals(Path.GetFullPath(testPath), StringComparison.OrdinalIgnoreCase));

        feed.Ack(completed);
        Assert.Empty(feed.Poll().Events);
    }

    private static void TryDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
