using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class RepositoryLastKnownGoodTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_LastKnownGood_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_LastKnownGoodLocal_" + Guid.NewGuid().ToString("N"));

    public RepositoryLastKnownGoodTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_local);
        new DataPaths(_root).EnsureStructure();
    }

    public void Dispose()
    {
        FileProblemToleranceTracker.NowUtcOverrideForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_local, recursive: true); } catch { }
    }

    /// <summary>
    /// Un errore di lettura transitorio (multi-PC 6.4.2, FileProblemToleranceTracker) entra nell'avviso
    /// "File con problemi" solo dopo almeno due tentativi consecutivi e almeno due minuti dal primo: questo
    /// helper avanza un orologio finto oltre la soglia e invoca <paramref name="refresh"/> una seconda volta,
    /// cosi' i test non devono attendere minuti reali ne' indebolire l'asserzione di visibilita'.
    /// </summary>
    private static void OltrepassaLaTolleranza(Action refresh)
    {
        var now = DateTime.UtcNow;
        FileProblemToleranceTracker.NowUtcOverrideForTests = () => now;
        refresh(); // primo tentativo fallito: ancora tollerato, non visibile
        now = now.AddMinutes(3);
        FileProblemToleranceTracker.NowUtcOverrideForTests = () => now;
        refresh(); // secondo tentativo consecutivo oltre MinElapsed: ora visibile
    }

    [Fact]
    public void TestCase_FileAncoraPresenteMaIlleggibile_MantieneRamESqliteESaveRestaBloccato()
    {
        var paths = new DataPaths(_root);
        var path = Path.Combine(paths.TestCasesDir, "testcase_TC-9001.json");
        var item = new TestCase
        {
            Uid = Guid.NewGuid(),
            Id = "TC-9001",
            RevisioneDati = 5,
            Componente = new EcuSelection { "Componente-A" },
            Veicoli = ["Daily"],
            Veicolo = "Daily",
            Descrizione = "ultima copia valida"
        };
        JsonIo.WriteAtomic(path, item);

        var store = new LocalTestIndexStore(_root, _local);
        var repo = new TestCaseRepository(paths, store);
        repo.RefreshIndex();
        Assert.Equal("ultima copia valida", Assert.Single(repo.GetAll()).Descrizione);
        Assert.True(repo.HasPersistentSnapshot);

        CorrompiMantenendoIlPath(path);
        OltrepassaLaTolleranza(() => repo.RefreshPaths([path]));

        var cached = Assert.Single(repo.GetAll());
        Assert.Equal(item.Uid, cached.Uid);
        Assert.Equal(5, cached.RevisioneDati);
        Assert.Equal("ultima copia valida", cached.Descrizione);
        Assert.Contains(repo.FileConProblemi, p => p.Percorso.Equals(path, StringComparison.OrdinalIgnoreCase));
        Assert.Throws<InvalidDataException>(() => repo.Save(cached, isNew: false, revisioneAttesa: 5));

        // Il refresh fallito non deve aver trasformato la cache SQLite persistente in una delete.
        var reopened = new TestCaseRepository(paths, new LocalTestIndexStore(_root, _local));
        Assert.True(reopened.HasPersistentSnapshot);
        Assert.Equal(item.Uid, Assert.Single(reopened.GetAll()).Uid);
    }

    [Fact]
    public void NonConformita_FileAncoraPresenteMaIlleggibile_MantieneUltimaCopiaValidaESaveRestaBloccato()
    {
        var paths = new DataPaths(_root);
        var item = new NonConformita { Uid = Guid.NewGuid(), Id = "NC-9001", RevisioneDati = 4 };
        var path = paths.NonConformitaFile(item.Id);
        JsonIo.WriteAtomic(path, item);

        var repo = new NonConformitaRepository(paths);
        repo.RefreshIndex();
        Assert.Equal(4, Assert.Single(repo.GetAll()).RevisioneDati);

        CorrompiMantenendoIlPath(path);
        OltrepassaLaTolleranza(() => repo.RefreshIndex());

        var cached = Assert.Single(repo.GetAll());
        Assert.Equal(item.Uid, cached.Uid);
        Assert.Equal(4, cached.RevisioneDati);
        Assert.Contains(repo.FileConProblemi, p => p.Percorso.Equals(path, StringComparison.OrdinalIgnoreCase));
        Assert.Throws<InvalidDataException>(() => repo.Save(cached, revisioneAttesa: 4));
    }

    [Fact]
    public void ChangeRequest_FileAncoraPresenteMaIlleggibile_MantieneUltimaCopiaValidaESaveRestaBloccato()
    {
        var paths = new DataPaths(_root);
        var item = new ChangeRequest { Uid = Guid.NewGuid(), Id = "CR-2026-9001", RevisioneDati = 7, Titolo = "valido" };
        var path = paths.ChangeRequestFile(item.Id);
        JsonIo.WriteAtomic(path, item);

        var repo = new ChangeRequestRepository(paths);
        repo.RefreshIndex();
        Assert.Equal("valido", Assert.Single(repo.GetAll()).Titolo);

        CorrompiMantenendoIlPath(path);
        OltrepassaLaTolleranza(() => repo.RefreshIndex());

        var cached = Assert.Single(repo.GetAll());
        Assert.Equal(item.Uid, cached.Uid);
        Assert.Equal(7, cached.RevisioneDati);
        Assert.Contains(repo.FileConProblemi, p => p.Percorso.Equals(path, StringComparison.OrdinalIgnoreCase));
        Assert.Throws<InvalidDataException>(() => repo.Save(cached, revisioneAttesa: 7));
    }

    [Fact]
    public void Segnalazione_FileAncoraPresenteMaIlleggibile_MantieneUltimaCopiaValidaESaveRestaBloccato()
    {
        var paths = new DataPaths(_root);
        var item = new SegnalazioneDaClassificare { Uid = Guid.NewGuid(), RevisioneDati = 3, TestId = "TC-9001" };
        var path = paths.SegnalazioneFile(item.Uid);
        JsonIo.WriteAtomic(path, item);

        var repo = new SegnalazioneRepository(paths);
        repo.RefreshIndex();
        Assert.Equal(item.Uid, Assert.Single(repo.GetAll()).Uid);

        CorrompiMantenendoIlPath(path);
        OltrepassaLaTolleranza(() => repo.RefreshIndex());

        var cached = Assert.Single(repo.GetAll());
        Assert.Equal(item.Uid, cached.Uid);
        Assert.Equal(3, cached.RevisioneDati);
        Assert.Contains(repo.FileConProblemi, p => p.Percorso.Equals(path, StringComparison.OrdinalIgnoreCase));
        Assert.Throws<InvalidDataException>(() => repo.Save(cached, revisioneAttesa: 3));
    }

    [Fact]
    public void FileCorrottoMaiLetto_NonVieneInventatoComeRecordValido()
    {
        var paths = new DataPaths(_root);
        var path = paths.NonConformitaFile("NC-9999");
        CorrompiMantenendoIlPath(path);

        var repo = new NonConformitaRepository(paths);
        OltrepassaLaTolleranza(() => repo.RefreshIndex());

        Assert.Empty(repo.GetAll());
        Assert.Contains(repo.FileConProblemi, p => p.Percorso.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    private static void CorrompiMantenendoIlPath(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ json-incompleto");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
    }
}
