using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class LateRecoveryArchiveTests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "testloom-late-archive-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Recovery_Scaduto_Viene_Archiviato_Nel_RecoveryBin_E_Non_Cancellato()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        paths.EnsureStructure();
        var uid = Guid.NewGuid();
        var test = new TestCase { Uid = uid, Id = "T-ARCH", RevisioneDati = 10, DataCreazione = DateTime.Now.AddDays(-30), UltimaModifica = DateTime.Now };
        TestCaseForkResolution.SetResolvedKeys(test, ["OLD-RESOLVED-FORK"]);

        var dir = Path.Combine(temp.Path, "_ditm", "conflicts", uid.ToString("N"));
        Directory.CreateDirectory(dir);
        var recovery = Path.Combine(dir, "evt-1.json.gz");
        byte[] payload = [9, 8, 7, 6];
        File.WriteAllBytes(recovery, payload);
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(recovery, now - DelayedForkRecoveryService.LateRecoveryGracePeriod - TimeSpan.FromDays(1));

        var removed = DelayedForkRecoveryService.CleanupResolvedDirectories(paths, [test], [], now);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(recovery));
        var archiviato = Path.Combine(paths.RecoveryBinDir, "fork-tardivi", uid.ToString("N"), "evt-1.json.gz");
        Assert.True(File.Exists(archiviato));
        Assert.Equal(payload, File.ReadAllBytes(archiviato));
    }

    [Fact]
    public void DescriviMancanti_DistingueFileAssenteDaFilePresenteNonValido()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        paths.EnsureStructure();
        var uid = Guid.NewGuid();
        var dir = Path.Combine(temp.Path, "_ditm", "conflicts", uid.ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "evt-presente.json.gz"), [1, 2, 3]);
        var assente = new TestJournalEvent { EventId = "evt-assente-0001", DeviceId = "pc-aaaa-bbbb", TimestampUtc = DateTime.UtcNow };
        var presente = new TestJournalEvent { EventId = "evt-presente", DeviceId = "pc-cccc", TimestampUtc = DateTime.UtcNow };
        var fork = new JournalForkConflict { EntityUid = uid };

        var testo = DelayedForkRecoveryService.DescriviMancanti(paths, fork, [assente, presente]);

        var righe = testo.Split(Environment.NewLine);
        Assert.Equal(2, righe.Length);
        Assert.Contains("evt-asse", righe[0]);
        Assert.Contains("pc-aaaa-", righe[0]);
        Assert.Contains("evt-pres", righe[1]);
        Assert.NotEqual(righe[0].Split(':').Last(), righe[1].Split(':').Last());
    }

    [Fact]
    public void RisoluzioneParziale_ConsentitaSoloDopo24OreDall_UltimoEventoMancante()
    {
        var ora = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var vecchio = new TestJournalEvent { TimestampUtc = ora.AddHours(-30) };
        var recente = new TestJournalEvent { TimestampUtc = ora.AddHours(-2) };

        Assert.Null(DelayedForkRecoveryService.AttesaResiduaRisoluzioneParziale([vecchio], ora));
        var attesa = DelayedForkRecoveryService.AttesaResiduaRisoluzioneParziale([vecchio, recente], ora);
        Assert.NotNull(attesa);
        Assert.Equal(TimeSpan.FromHours(22), attesa!.Value);
    }

    [Fact]
    public void ArchiviaRamiMancanti_ScriveRecordInRecoveryBinECreateOnly()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        paths.EnsureStructure();
        var fork = new JournalForkConflict { EntityUid = Guid.NewGuid(), BaseRevision = 3, BaseHash = "abc" };
        var mancante = new TestJournalEvent { EventId = "evt-miss", DeviceId = "pc-x", NewHash = "h2", TimestampUtc = DateTime.UtcNow.AddDays(-2) };
        var recovery = new ForkRecoveryLoadResult(Array.Empty<ForkRecoveryBranch>(), [mancante]);

        var file = DelayedForkRecoveryService.ArchiviaRamiMancanti(paths, fork, recovery, "op1", DateTime.UtcNow);

        Assert.True(File.Exists(file));
        Assert.StartsWith(Path.Combine(paths.RecoveryBinDir, "fork-mancanti"), file);
        var json = File.ReadAllText(file);
        Assert.Contains("evt-miss", json);
        Assert.Contains("op1", json);
        Assert.Throws<IOException>(() => JsonIo.WriteAtomic(file, new { X = 1 }, overwrite: false));
    }

    [Fact]
    public void Archiviazione_Con_Nome_Gia_Presente_Non_Sovrascrive_L_Archivio()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        paths.EnsureStructure();
        var uid = Guid.NewGuid();
        var test = new TestCase { Uid = uid, Id = "T-ARCH2", RevisioneDati = 10, DataCreazione = DateTime.Now.AddDays(-30), UltimaModifica = DateTime.Now };
        TestCaseForkResolution.SetResolvedKeys(test, ["OLD-RESOLVED-FORK"]);

        var archivio = Path.Combine(paths.RecoveryBinDir, "fork-tardivi", uid.ToString("N"));
        Directory.CreateDirectory(archivio);
        File.WriteAllBytes(Path.Combine(archivio, "evt-1.json.gz"), [1]);
        var dir = Path.Combine(temp.Path, "_ditm", "conflicts", uid.ToString("N"));
        Directory.CreateDirectory(dir);
        var recovery = Path.Combine(dir, "evt-1.json.gz");
        File.WriteAllBytes(recovery, [2]);
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(recovery, now - DelayedForkRecoveryService.LateRecoveryGracePeriod - TimeSpan.FromDays(1));

        DelayedForkRecoveryService.CleanupResolvedDirectories(paths, [test], [], now);

        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(archivio, "evt-1.json.gz")));
        Assert.Equal(2, Directory.GetFiles(archivio).Length);
    }
}
