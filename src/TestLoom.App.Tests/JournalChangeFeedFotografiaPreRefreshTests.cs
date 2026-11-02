using System.Text.Json;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Baseline del journal condiviso (P2.7): la fotografia va presa PRIMA della ricostruzione autorevole dell'indice, così
/// un evento scritto da un altro PC durante la ricostruzione non viene mai marcato come già consumato. Copre anche
/// HasBaseline su un journal locale illeggibile (P4): deve rispondere false, senza eccezioni.
/// </summary>
public sealed class JournalChangeFeedFotografiaPreRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_FeedFotografia_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_FeedFotografia_Local_" + Guid.NewGuid().ToString("N"));
    private readonly JsonSerializerOptions _options = new(JsonIo.Options) { WriteIndented = false };

    public JournalChangeFeedFotografiaPreRefreshTests()
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
    public void EventoScrittoDuranteLaRicostruzione_NonVieneConsumatoDallaBaseline_MaArrivaAlPollSuccessivo()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var index = new LocalTestIndexStore(_root, _local);
        var feed = new JournalChangeFeedService(paths, index.WorkspaceDirectory);

        var primaDellaFotografia = AccodaEvento(paths, "device-a", "evento-prima");
        // Fotografia presa prima della ricostruzione autorevole (RefreshIndex).
        var fotografia = feed.CatturaFotografia();
        // Un altro PC scrive mentre la ricostruzione e' in corso: non e' nella fotografia.
        var durantelaRicostruzione = AccodaEvento(paths, "device-b", "evento-durante");

        Assert.True(feed.EstablishBaseline(fotografia));
        var batch = feed.Poll();

        Assert.False(batch.RequiresFullReconciliation);
        Assert.DoesNotContain(primaDellaFotografia, batch.Events.Select(e => e.EventId));
        Assert.Equal(new[] { durantelaRicostruzione }, batch.Events.Select(e => e.EventId).ToArray());
    }

    [Fact]
    public void BaselineSenzaFotografia_NonStabilisceLaBaseline()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var index = new LocalTestIndexStore(_root, _local);
        var feed = new JournalChangeFeedService(paths, index.WorkspaceDirectory);

        Assert.False(feed.EstablishBaseline(null));
        Assert.False(feed.HasBaseline);
    }

    [Fact]
    public void HasBaseline_JournalLocaleIllettibile_RispondeFalseSenzaEccezione()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var index = new LocalTestIndexStore(_root, _local);
        var feed = new JournalChangeFeedService(paths, index.WorkspaceDirectory);
        Assert.True(feed.EstablishBaseline());

        // Il file locale journal.db viene sostituito da un contenuto che non e' un database SQLite.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var db = Path.Combine(index.WorkspaceDirectory, "journal.db");
        TryDelete(db + "-wal");
        TryDelete(db + "-shm");
        File.WriteAllBytes(db, Enumerable.Repeat((byte)0x42, 8192).ToArray());

        var baseline = true;
        var eccezione = Record.Exception(() => baseline = feed.HasBaseline);

        Assert.Null(eccezione);
        Assert.False(baseline);
    }

    private string AccodaEvento(DataPaths paths, string deviceId, string eventId)
    {
        var testPath = Path.Combine(paths.TestCasesDir, $"testcase_{eventId}.json");
        var relativo = Path.GetRelativePath(_root, testPath);
        var evt = new TestJournalEvent
        {
            SchemaVersion = SharedTestJournalService.JournalSchemaVersion,
            EventId = eventId,
            DeviceId = deviceId,
            TimestampUtc = DateTime.UtcNow,
            Operation = JournalOperation.Save,
            EntityUid = Guid.NewGuid(),
            EntityId = eventId,
            BaseRevision = 1,
            BaseHash = "base",
            NewRevision = 2,
            NewHash = "new",
            OldRelativePath = relativo,
            NewRelativePath = relativo
        };
        var deviceDir = Path.Combine(_root, "_ditm", "journal", deviceId);
        Directory.CreateDirectory(deviceDir);
        File.AppendAllText(Path.Combine(deviceDir, "current.ndjson"), JsonSerializer.Serialize(evt, _options) + "\n");
        return eventId;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
