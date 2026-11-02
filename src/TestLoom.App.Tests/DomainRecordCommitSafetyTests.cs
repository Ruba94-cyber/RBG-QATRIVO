using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Prove su workspace disposable: lock di commit locale e rifiuto di
/// ricreare una fotografia obsoleta dopo cancellazione consegnata.
/// NON simulano un ACK OneDrive tra filesystem indipendenti.
/// </summary>
public sealed class DomainRecordCommitSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_6_TEST",
        "domain-record-commit-" + Guid.NewGuid().ToString("N"));

    private DataPaths NewPaths()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        return paths;
    }

    [Fact]
    public void DueIstanze_NonPossonoAcquisireContemporaneamenteIlLockDelloStessoRecord()
    {
        var paths = NewPaths();
        var path = paths.ChangeRequestFile("CR-LOCK-1");
        using var holder = DomainRecordCommitLock.Acquire("Change Request", "CR-LOCK-1", path);
        var error = Assert.Throws<IOException>(() =>
            DomainRecordCommitLock.Acquire("Change Request", "CR-LOCK-1", path,
                TimeSpan.FromMilliseconds(80)));
        Assert.Contains("Change Request", error.Message);
        Assert.False(File.Exists(path)); // non crea il documento aziendale
    }

    [Fact]
    public async Task ChangeRequest_DueRepositoryStessaRevisione_UnoSoloPuoScrivere()
    {
        var paths = NewPaths();
        var a = new ChangeRequestRepository(paths);
        var b = new ChangeRequestRepository(paths);
        var original = new ChangeRequest { Id = "CR-COMMIT-RACE", Titolo = "base" };
        a.Save(original);
        a.RefreshIndex();
        b.RefreshIndex();
        var left = a.GetById(original.Id)!;
        var right = b.GetById(original.Id)!;
        var revision = left.RevisioneDati;
        Assert.Equal(revision, right.RevisioneDati);
        left.Titolo = "A";
        right.Titolo = "B";

        using var gate = new Barrier(2);
        Exception? exA = null, exB = null;
        await Task.WhenAll(
            Task.Run(() => { gate.SignalAndWait(); try { a.Save(left, revision); } catch (Exception ex) { exA = ex; } }),
            Task.Run(() => { gate.SignalAndWait(); try { b.Save(right, revision); } catch (Exception ex) { exB = ex; } }));

        Assert.Single(new[] { exA, exB }, e => e is null);
        Assert.Single(new[] { exA, exB }, e => e is ConflittoRevisioneException);
        var persisted = JsonIo.TryRead<ChangeRequest>(paths.ChangeRequestFile(original.Id), out var error);
        Assert.Null(error);
        Assert.NotNull(persisted);
        Assert.Equal(revision + 1, persisted.RevisioneDati);
        Assert.Contains(persisted.Titolo, new[] { "A", "B" });
        Assert.False(File.Exists(TestCaseCommitLock.LockPathFor(paths.ChangeRequestFile(original.Id))));
    }

    [Fact]
    public void NonConformita_FileRimossoDopoLettura_UpdateStantioNonLoRicrea()
    {
        var paths = NewPaths();
        var repo = new NonConformitaRepository(paths);
        var nc = new NonConformita { Id = "NC-GONE-SAFE" };
        repo.Save(nc);
        var originalPath = paths.NonConformitaFile(nc.Id);
        var revision = nc.RevisioneDati;
        File.Delete(originalPath);

        Assert.Throws<ConflittoRevisioneException>(() => repo.Save(nc, revision));
        Assert.False(File.Exists(originalPath));
        Assert.Equal(revision, nc.RevisioneDati);
    }

    [Fact]
    public void Segnalazione_FileRimossoDopoLettura_UpdateStantioNonLoRicrea()
    {
        var paths = NewPaths();
        var repo = new SegnalazioneRepository(paths);
        var seg = new SegnalazioneDaClassificare();
        repo.Save(seg);
        var originalPath = paths.SegnalazioneFile(seg.Uid);
        var revision = seg.RevisioneDati;
        File.Delete(originalPath);

        Assert.Throws<ConflittoRevisioneException>(() => repo.Save(seg, revision));
        Assert.False(File.Exists(originalPath));
        Assert.Equal(revision, seg.RevisioneDati);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
