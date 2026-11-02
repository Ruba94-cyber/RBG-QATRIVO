using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class TestCaseIncrementalIndexTests : IDisposable
{
    private readonly string _shared = Path.Combine(Path.GetTempPath(), "TestLoom_Incremental_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_Incremental_Local_" + Guid.NewGuid().ToString("N"));

    public TestCaseIncrementalIndexTests()
    {
        Directory.CreateDirectory(_shared);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        FileProblemToleranceTracker.NowUtcOverrideForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDelete(_shared);
        TryDelete(_local);
    }

    /// <summary>Vedi RepositoryLastKnownGoodTests.OltrepassaLaTolleranza: un errore di lettura transitorio
    /// entra in FileConProblemi solo dopo >= 2 tentativi consecutivi e >= 2 minuti dal primo.</summary>
    private static void OltrepassaLaTolleranza(Action refresh)
    {
        var now = DateTime.UtcNow;
        FileProblemToleranceTracker.NowUtcOverrideForTests = () => now;
        refresh();
        now = now.AddMinutes(3);
        FileProblemToleranceTracker.NowUtcOverrideForTests = () => now;
        refresh();
    }

    [Fact]
    public void RefreshPaths_AggiornaSoloIlJsonSegnalato()
    {
        var paths = new DataPaths(_shared);
        paths.EnsureStructure();
        var repo = new TestCaseRepository(paths, new LocalTestIndexStore(_shared, _local));
        var a = NuovoTest("TC-0001", "prima");
        var b = NuovoTest("TC-0002", "immutato");
        repo.Save(a, isNew: true);
        repo.Save(b, isNew: true);
        repo.RefreshIndex();

        var pathA = repo.PercorsoPer(a);
        var esterno = JsonIo.TryRead<TestCase>(pathA, out _)!;
        esterno.DescrizioneBreve = "modifica esterna";
        // Simula anche il caso peggiore di editor/strumento esterno che non incrementa RevisioneDati.
        JsonIo.WriteAtomic(pathA, esterno);

        repo.RefreshPaths([pathA]);

        Assert.Equal("modifica esterna", repo.GetById("TC-0001")!.DescrizioneBreve);
        Assert.Equal("immutato", repo.GetById("TC-0002")!.DescrizioneBreve);
    }

    [Fact]
    public void RefreshIndex_DeepReconciliationRilevaSostituzioneConStessaLunghezzaEStessoTimestamp()
    {
        var paths = new DataPaths(_shared);
        paths.EnsureStructure();
        var repo = new TestCaseRepository(paths, new LocalTestIndexStore(_shared, _local));
        var tc = NuovoTest("TC-0003", "versione-A");
        repo.Save(tc, isNew: true);
        repo.RefreshIndex();

        var path = repo.PercorsoPer(tc);
        var openingInfo = new FileInfo(path);
        var openingLength = openingInfo.Length;
        var openingMtime = openingInfo.LastWriteTimeUtc;

        var esterno = JsonIo.TryRead<TestCase>(path, out _)!;
        esterno.DescrizioneBreve = "versione-B"; // stessa lunghezza della versione iniziale
        JsonIo.WriteAtomic(path, esterno);
        Assert.Equal(openingLength, new FileInfo(path).Length);
        File.SetLastWriteTimeUtc(path, openingMtime);
        Assert.Equal(openingMtime, File.GetLastWriteTimeUtc(path));

        // Nessun RefreshPaths: simula un evento watcher/change-feed perso. La full reconciliation deve
        // comunque rileggere il contenuto e non considerare mtime+length come prova di identità.
        repo.RefreshIndex();

        Assert.Equal("versione-B", repo.GetById("TC-0003")!.DescrizioneBreve);
    }

    [Fact]
    public void RefreshPaths_DeleteRimuoveCacheEModelloPersistente()
    {
        var paths = new DataPaths(_shared);
        paths.EnsureStructure();
        var store = new LocalTestIndexStore(_shared, _local);
        var repo = new TestCaseRepository(paths, store);
        var tc = NuovoTest("TC-0100", "da cancellare");
        repo.Save(tc, isNew: true);
        repo.RefreshIndex();
        var path = repo.PercorsoPer(tc);

        File.Delete(path);
        repo.RefreshPaths([path]);

        Assert.Null(repo.GetById("TC-0100"));
        Assert.Empty(repo.GetAll(includeEliminati: true));
        Assert.Empty(new LocalTestIndexStore(_shared, _local).LoadAll());
    }

    [Fact]
    public void RefreshPaths_JsonCorrottoMantieneUltimaCopiaValidaMaBloccaIlSave()
    {
        var paths = new DataPaths(_shared);
        paths.EnsureStructure();
        var repo = new TestCaseRepository(paths, new LocalTestIndexStore(_shared, _local));
        var tc = NuovoTest("TC-0200", "valido");
        repo.Save(tc, isNew: true);
        repo.RefreshIndex();
        var path = repo.PercorsoPer(tc);

        File.WriteAllText(path, "{ json troncato");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        OltrepassaLaTolleranza(() => repo.RefreshPaths([path]));

        var cached = repo.GetById("TC-0200");
        Assert.NotNull(cached);
        Assert.Equal("valido", cached!.DescrizioneBreve);
        Assert.Contains(repo.FileConProblemi, p => p.Percorso.Equals(path, StringComparison.OrdinalIgnoreCase));
        Assert.Throws<InvalidDataException>(() => repo.Save(cached, isNew: false, revisioneAttesa: cached.RevisioneDati));

        // Un refresh transitorio non deve essere persistito come delete nel warm snapshot locale.
        var reopened = new TestCaseRepository(paths, new LocalTestIndexStore(_shared, _local));
        Assert.Equal("TC-0200", Assert.Single(reopened.GetAll()).Id);
    }

    /// <summary>
    /// FASE 2 (correzione orchestratore): un file troncato dall'evento watcher che poi si sistema DA SOLO,
    /// senza che arrivi un secondo evento watcher per quel path, deve comunque tornare leggibile al giro
    /// periodico successivo — perché AppState.RileggiTestCases ripianifica un tentativo per OGNI path ancora
    /// in tolleranza (repo.PercorsiConProblemiTracciati), non solo per quelli già visibili in FileConProblemi.
    /// Qui si simula esattamente quella composizione, senza passare da AppState.
    /// </summary>
    [Fact]
    public void RefreshPaths_FileSiSistemaSenzaNuoviEventiWatcher_TornaLeggibileAlGiroPeriodicoSuccessivo()
    {
        var paths = new DataPaths(_shared);
        paths.EnsureStructure();
        var repo = new TestCaseRepository(paths, new LocalTestIndexStore(_shared, _local));
        var tc = NuovoTest("TC-0300", "iniziale");
        repo.Save(tc, isNew: true);
        repo.RefreshIndex();
        var path = repo.PercorsoPer(tc);

        try
        {
            var now = DateTime.UtcNow;
            FileProblemToleranceTracker.NowUtcOverrideForTests = () => now;

            // L'evento watcher coglie il file a metà scrittura OneDrive.
            File.WriteAllText(path, "{ json troncato");
            File.SetLastWriteTimeUtc(path, now.AddSeconds(2));
            repo.RefreshPaths([path]);
            Assert.Empty(repo.FileConProblemi); // ancora tollerato: primo tentativo
            Assert.Contains(path, repo.PercorsiConProblemiTracciati);

            // Il file si sistema DA SOLO (scrittura completata) e NESSUN nuovo evento watcher arriva per
            // questo path: solo il giro periodico (System.Timers.Timer da 5 minuti in MainViewModel) lo
            // ripropone, componendo "changed" da PercorsiConProblemiTracciati come fa AppState.RileggiTestCases.
            var aggiornato = NuovoTest("TC-0300", "riparato da solo");
            aggiornato.Uid = tc.Uid;
            aggiornato.RevisioneDati = tc.RevisioneDati;
            JsonIo.WriteAtomic(path, aggiornato);
            now = now.AddMinutes(3);
            FileProblemToleranceTracker.NowUtcOverrideForTests = () => now;

            repo.RefreshPaths(repo.PercorsiConProblemiTracciati); // nessun path "nuovo" dal watcher: solo il retry pianificato

            Assert.Empty(repo.FileConProblemi);
            var cached = repo.GetById("TC-0300");
            Assert.NotNull(cached);
            Assert.Equal("riparato da solo", cached!.DescrizioneBreve);
        }
        finally
        {
            FileProblemToleranceTracker.NowUtcOverrideForTests = null;
        }
    }

    [Fact]
    public void RefreshPaths_IgnoraJsonCheNonSonoTestCase()
    {
        var paths = new DataPaths(_shared);
        paths.EnsureStructure();
        var repo = new TestCaseRepository(paths, new LocalTestIndexStore(_shared, _local));
        var unrelated = Path.Combine(paths.TestCasesDir, "altro.json");
        File.WriteAllText(unrelated, "{}");

        repo.RefreshPaths([unrelated]);

        Assert.Empty(repo.GetAll(includeEliminati: true));
        Assert.Empty(repo.FileConProblemi);
    }

    private static TestCase NuovoTest(string id, string descrizione) => new()
    {
        Id = id,
        Componente = new EcuSelection { "Componente-A" },
        Veicolo = "Vehicle-A",
        Veicoli = ["Vehicle-A"],
        DescrizioneBreve = descrizione,
        StatoLavoro = StatoLavoroTest.Pianificato,
        RevisioneDati = 1
    };

    private static void TryDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
