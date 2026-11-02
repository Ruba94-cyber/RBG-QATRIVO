using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class NotificationLedgerStateSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_NotificationLedgerSafety_" + Guid.NewGuid().ToString("N"));
    private DataPaths Paths => new(_root);

    public NotificationLedgerStateSafetyTests()
    {
        Directory.CreateDirectory(_root);
        Paths.EnsureStructure();
    }

    [Fact]
    public void CurrentTroncato_CompactionNonMutaLaSorgente_AppendRiparaLaCodaSenzaPerdereIlPrefisso()
    {
        var repo = new NotificaRepository(Paths);
        repo.Add(new NotificaApp
        {
            Destinatario = "Anna",
            Titolo = "Completa",
            Messaggio = "evento completo prima della coda parziale"
        });

        var deviceDir = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "_ditm", "notifications")));
        var current = Path.Combine(deviceDir, "current.ndjson");
        using (var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var partial = Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"EventId\":\"");
            stream.Write(partial);
        }
        var poisonedBytes = File.ReadAllBytes(current);

        var visible = repo.GetAll("Anna");
        Assert.Single(visible);
        Assert.Equal("Completa", visible[0].Titolo);

        // La compattazione resta strict: con una coda parziale non tocca né la sorgente né il checkpoint.
        Assert.Throws<InvalidDataException>(() => repo.RunMaintenance(force: true));
        Assert.Equal(poisonedBytes, File.ReadAllBytes(current));
        Assert.False(File.Exists(Path.Combine(deviceDir, "checkpoint.json.gz")));

        // L'append ripara la coda: la parte parziale finisce in current.ndjson.torn-* e il prefisso resta leggibile.
        repo.Add(new NotificaApp
        {
            Destinatario = "Anna",
            Titolo = "Non deve essere persa"
        });
        Assert.Single(Directory.GetFiles(deviceDir, "current.ndjson.torn-*"));
        Assert.Equal(2, repo.GetAll("Anna").Count);
    }

    [Fact]
    public void CurrentTroncato_LInterruzioneSiRegistraConFileERiga()
    {
        var repo = new NotificaRepository(Paths);
        repo.Add(new NotificaApp { Destinatario = "Anna", Titolo = "Completa", Messaggio = "evento completo" });

        var deviceDir = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "_ditm", "notifications")));
        var current = Path.Combine(deviceDir, "current.ndjson");
        using (var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            stream.Write(Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"EventId\":\""));
        }

        Assert.Single(repo.GetAll("Anna"));

        // Il prefisso resta visibile, ma la riga non valida non passa in silenzio: il registro indica file e riga.
        var testoLog = File.ReadAllText(Path.Combine(CrashLogService.LogDirectory, "crash.log"));
        Assert.Contains($"lettura interrotta alla riga 2 di {current}", testoLog);
    }

    [Fact]
    public void CheckpointSchemaFuturo_ManutenzionePreservaCheckpointECurrentBytePerByte()
    {
        var repo = new NotificaRepository(Paths);
        repo.Add(new NotificaApp { Destinatario = "Anna", Titolo = "Prima" });
        repo.RunMaintenance(force: true);

        var deviceDir = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "_ditm", "notifications")));
        var checkpoint = Path.Combine(deviceDir, "checkpoint.json.gz");
        WriteCheckpoint(checkpoint, new NotificationLedgerCheckpoint
        {
            SchemaVersion = 999,
            UpdatedUtc = DateTime.UtcNow,
            Heads = new Dictionary<Guid, NotificationLedgerEvent>()
        });
        var futureBytes = File.ReadAllBytes(checkpoint);

        repo.Add(new NotificaApp { Destinatario = "Anna", Titolo = "Nuova in current" });
        var current = Path.Combine(deviceDir, "current.ndjson");
        var currentBytes = File.ReadAllBytes(current);

        Assert.Throws<InvalidDataException>(() => repo.RunMaintenance(force: true));
        Assert.Equal(futureBytes, File.ReadAllBytes(checkpoint));
        Assert.Equal(currentBytes, File.ReadAllBytes(current));
    }

    private static void WriteCheckpoint(string path, NotificationLedgerCheckpoint checkpoint)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        JsonSerializer.Serialize(gzip, checkpoint, JsonIo.Options);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
