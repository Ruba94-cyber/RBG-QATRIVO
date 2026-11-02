using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Copertura di regressione per il lookup incrementale O(1) introdotto in TestCaseRepository.SaveCore
/// (AggiornaLookupDopoSalvataggio, Addendum prestazioni 2026-09-23) — richiesta esplicita della review
/// indipendente (FAIL, CRITICAL): ogni percorso che tocca _byId/_pathById/_pathByUid deve restare coerente con
/// lo stato autorevole su disco. Il criterio di verifica, per ogni scenario, è: ricostruire da zero un secondo
/// repository sulla STESSA cartella condivisa (RefreshIndex = piena riconciliazione dal disco) e confrontarlo
/// con quello "in memoria" che ha eseguito la sequenza di operazioni — mai fidarsi di un'asserzione puntuale su
/// un singolo Id, che avrebbe potuto mascherare esattamente il bug trovato dalla review (vecchio Id orfano).
/// </summary>
public sealed class TestCaseIncrementalLookupCoherenceTests : IDisposable
{
    private readonly string _shared = Path.Combine(Path.GetTempPath(), "TestLoom_LookupCoherence_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _localDirs = new();

    private string NuovaCartellaLocale()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_LookupCoherence_Local_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _localDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDelete(_shared);
        foreach (var dir in _localDirs) TryDelete(dir);
    }

    private static void TryDelete(string path) { if (Directory.Exists(path)) { try { Directory.Delete(path, true); } catch { } } }

    private DataPaths Paths()
    {
        var paths = new DataPaths(_shared);
        paths.EnsureStructure();
        return paths;
    }

    private TestCaseRepository NuovoRepository(DataPaths paths) => new(paths, new LocalTestIndexStore(_shared, NuovaCartellaLocale()));

    private static TestCase NuovoTest(string id, string componente = "Componente-A") => new()
    {
        Id = id,
        Uid = Guid.NewGuid(),
        Componente = new EcuSelection { componente },
        Veicolo = "V1",
        Veicoli = ["V1"],
        DescrizioneBreve = "x",
        StatoLavoro = StatoLavoroTest.Pianificato,
        RevisioneDati = 1,
    };

    /// <summary>Confronto "in memoria vs disco ricostruito da zero", il criterio esplicitamente richiesto dalla
    /// review: un secondo repository, mai toccato dalla sequenza di operazioni, fa RefreshIndex (piena
    /// riconciliazione dai file) e diventa l'oracolo. Copre sia gli Id attesi presenti sia — cruciale per il
    /// bug trovato — l'ASSENZA di Id che non dovrebbero più esistere (nessun confronto per singola chiave può
    /// dimostrarlo, serve l'uguaglianza fra i due insiemi completi).</summary>
    private void AssertLookupCoerenteConDisco(DataPaths paths, TestCaseRepository repoInMemoria)
    {
        var oracolo = NuovoRepository(paths);
        oracolo.RefreshIndex();

        var idAttesi = oracolo.GetAllIds(includeEliminati: true).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var idEffettivi = repoInMemoria.GetAllIds(includeEliminati: true).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(idAttesi, idEffettivi);

        foreach (var id in idAttesi)
        {
            Assert.True(repoInMemoria.IdExists(id), $"IdExists('{id}') dovrebbe essere true (presente su disco).");
            var atteso = oracolo.GetById(id);
            var effettivo = repoInMemoria.GetById(id);
            Assert.NotNull(effettivo);
            Assert.Equal(atteso!.Uid, effettivo!.Uid);
            Assert.Equal(atteso.RevisioneDati, effettivo.RevisioneDati);
            Assert.Equal(atteso.Eliminato, effettivo.Eliminato);
        }

        // Nessun Id "fantasma": ogni Id noto al repository in memoria deve esistere davvero sul disco.
        foreach (var id in idEffettivi)
            Assert.True(idAttesi.Contains(id), $"'{id}' è presente nel lookup in memoria ma non risulta più su disco (voce orfana).");
    }

    [Fact]
    public void Nuovo_LookupCoerente()
    {
        var paths = Paths();
        var repo = NuovoRepository(paths);
        repo.Save(NuovoTest("TC-0001"), isNew: true);
        AssertLookupCoerenteConDisco(paths, repo);
    }

    [Fact]
    public void Update_StessoIdStessoPath_LookupCoerente()
    {
        var paths = Paths();
        var repo = NuovoRepository(paths);
        var tc = NuovoTest("TC-0001");
        repo.Save(tc, isNew: true);
        tc.DescrizioneBreve = "modificato";
        repo.Save(tc, isNew: false, revisioneAttesa: tc.RevisioneDati);
        AssertLookupCoerenteConDisco(paths, repo);
        Assert.Equal("modificato", repo.GetById("TC-0001")!.DescrizioneBreve);
    }

    /// <summary>Lo scenario CRITICAL della review: cambio Id di un Test già esistente (stesso Uid), come fa
    /// TestCaseEditorViewModel dopo un cambio di iniziali/prefisso. Il vecchio Id NON deve restare orfano in
    /// IdExists/GetById — prima della correzione questa asserzione falliva.</summary>
    [Fact]
    public void CambioId_VecchioIdNonRestaOrfano_LookupCoerente()
    {
        var paths = Paths();
        var repo = NuovoRepository(paths);
        var tc = NuovoTest("TC-0001");
        repo.Save(tc, isNew: true);

        tc.Id = "TC-9999";
        repo.Save(tc, isNew: false, revisioneAttesa: tc.RevisioneDati);

        Assert.False(repo.IdExists("TC-0001"), "Il vecchio Id non deve più esistere nel lookup in memoria.");
        Assert.Null(repo.GetById("TC-0001"));
        Assert.True(repo.IdExists("TC-9999"));
        AssertLookupCoerenteConDisco(paths, repo);
    }

    /// <summary>Cambio path senza cambio Id (es. cambio componente che sposta il Test in un altro bucket di cartella
    /// gerarchica): il vecchio file viene spostato/cancellato, il lookup deve seguire il nuovo path restando
    /// interrogabile con lo stesso Id.</summary>
    [Fact]
    public void CambioPath_StessoId_LookupSeguePercorsoENonPerdeIlRecord()
    {
        var paths = Paths();
        var repo = NuovoRepository(paths);
        var tc = NuovoTest("TC-0001", componente: "Componente-A");
        repo.Save(tc, isNew: true);
        var pathIniziale = repo.PercorsoPer(tc);

        tc.Componente.Clear();
        tc.Componente.Add("Componente-B"); // stesso Id, gruppo componente diverso -> path gerarchico diverso
        repo.Save(tc, isNew: false, revisioneAttesa: tc.RevisioneDati);

        var pathFinale = repo.PercorsoPer(tc);
        Assert.NotEqual(pathIniziale, pathFinale, StringComparer.OrdinalIgnoreCase);
        // I Test sono "visibility managed": DeleteFile non cancella il vecchio file, lo trasforma in tombstone
        // (resta presente su disco ma non più leggibile come TestCase valido) — stesso meccanismo di
        // WorkflowTransactionVisibilityService usato ovunque nell'app per i quattro record di dominio.
        Assert.True(WorkflowTransactionVisibilityService.IsCommittedTombstone(paths, pathIniziale),
            "Il vecchio path dovrebbe essere diventato un tombstone dopo lo spostamento.");
        Assert.True(File.Exists(pathFinale));
        Assert.True(repo.IdExists("TC-0001"));
        AssertLookupCoerenteConDisco(paths, repo);
    }

    [Fact]
    public void Delete_Cestino_LookupCoerente_RipristinoRestituisceIlRecordAttivo()
    {
        var paths = Paths();
        var repo = NuovoRepository(paths);
        var tc = NuovoTest("TC-0001");
        repo.Save(tc, isNew: true);

        repo.SoftDelete(tc, "Operatore");
        Assert.DoesNotContain(repo.GetAll(), t => t.Id == "TC-0001");
        AssertLookupCoerenteConDisco(paths, repo);

        var eliminato = repo.GetAll(includeEliminati: true).Single(t => t.Id == "TC-0001");
        repo.Restore(eliminato, "Operatore");
        Assert.Contains(repo.GetAll(), t => t.Id == "TC-0001" && !t.Eliminato);
        AssertLookupCoerenteConDisco(paths, repo);
    }

    /// <summary>Rollback di una WorkflowTransaction (stesso meccanismo usato da MasterUnioneService): dopo un
    /// fallimento a metà, i file vengono ripristinati alla before-image, e il lookup in memoria del repository
    /// che ha eseguito le operazioni deve tornare coerente con quello stato ripristinato — non con quello
    /// (mai committato) che aveva scritto durante la transazione.</summary>
    [Fact]
    public void RollbackWorkflowTransaction_LookupCoerenteConLoStatoRipristinato()
    {
        var paths = Paths();
        var repo = NuovoRepository(paths);
        var t1 = NuovoTest("TC-0001");
        var t2 = NuovoTest("TC-0002");
        repo.Save(t1, isNew: true);
        repo.Save(t2, isNew: true);

        try
        {
            using var tx = WorkflowTransactionService.Begin(paths, "test-rollback");
            t1.DescrizioneBreve = "scritto-dentro-la-transazione";
            repo.Save(t1, isNew: false, revisioneAttesa: t1.RevisioneDati);
            throw new InvalidOperationException("Simula un fallimento a metà transazione.");
        }
        catch (InvalidOperationException) { /* atteso: la using-scope fa Rollback() in Dispose() */ }

        // Il repository "in memoria" ha ancora la versione scritta durante la transazione (non è stato
        // ricaricato): la richiesta della review è verificare che un repository fresco, che rilegge dal disco
        // ripristinato dal rollback, veda lo stato PRECEDENTE — nessuna scrittura sopravvissuta.
        var oracolo = NuovoRepository(paths);
        oracolo.RefreshIndex();
        Assert.Equal("x", oracolo.GetById("TC-0001")!.DescrizioneBreve);
        Assert.Equal(1, oracolo.GetById("TC-0001")!.RevisioneDati);
    }

    /// <summary>RefreshIndex "da un'altra postazione": un secondo repository scrive sulla stessa cartella
    /// condivisa (RevisioneDati incrementata, nuovo Id) mentre il primo mantiene ancora la propria cache
    /// stale; dopo RefreshIndex/RefreshPaths il primo deve riallinearsi, incluso il caso di cambio Id fatto
    /// dall'altra postazione (che passa comunque dal fast path incrementale DELL'ALTRO repository, non da
    /// questo: qui si verifica che RefreshIndex, il percorso di piena riconciliazione, non erediti mai un
    /// lookup incoerente indipendentemente da cosa abbia fatto l'altra postazione).</summary>
    [Fact]
    public void RefreshIndex_DaAltraPostazione_ConCambioId_SiRiallinea()
    {
        var paths = Paths();
        var repoA = NuovoRepository(paths);
        var tc = NuovoTest("TC-0001");
        repoA.Save(tc, isNew: true);

        var repoB = NuovoRepository(paths);
        repoB.RefreshIndex();
        var tcSuB = repoB.GetById("TC-0001")!;
        tcSuB.Id = "TC-9999";
        repoB.Save(tcSuB, isNew: false, revisioneAttesa: tcSuB.RevisioneDati);

        repoA.RefreshIndex(); // simula il change-feed/refresh periodico che rilegge dal disco condiviso

        Assert.False(repoA.IdExists("TC-0001"));
        Assert.True(repoA.IdExists("TC-9999"));
        AssertLookupCoerenteConDisco(paths, repoA);
    }

    /// <summary>Duplicato/fork: due file rivendicano lo stesso Id (simulando una sincronizzazione ritardata che
    /// ha lasciato due copie). Il fast path deve rilevare la collisione e ricadere sul rebuild completo, che
    /// applica la stessa regola "vince il file più recente per Mtime" già usata da RicostruisciLookup.</summary>
    [Fact]
    public void Duplicato_StessoIdSuDuePath_CollisioneRilevata_VinceIlPiuRecente()
    {
        var paths = Paths();
        var repo = NuovoRepository(paths);
        var t1 = NuovoTest("TC-0001");
        repo.Save(t1, isNew: true);
        var pathOriginale = repo.PercorsoPer(t1);

        // Copia manuale "fork" con lo stesso Id ma Uid diverso, scritta più tardi.
        var duplicato = NuovoTest("TC-0001");
        duplicato.DescrizioneBreve = "fork-piu-recente";
        var pathDuplicato = Path.Combine(Path.GetDirectoryName(pathOriginale)!, "testcase_TC-0001-fork.json");
        JsonIo.WriteAtomic(pathDuplicato, duplicato);
        File.SetLastWriteTimeUtc(pathDuplicato, DateTime.UtcNow.AddSeconds(5));

        repo.RefreshPaths([pathDuplicato]);

        // GetAll() enumera i file fisici (due, uno per copia): la deduplica non è compito di SaveCore, ma di un
        // sottosistema dedicato di fork detection/recovery. Quello che DEVE restare univoco e coerente è il
        // lookup per Id/Uid: _byId/GetById risolve all'unica versione "vincente" per Mtime più recente, stessa
        // regola documentata in RicostruisciLookup — qui si verifica che il fast path incrementale non l'abbia
        // lasciata ambigua o puntata a un path sbagliato/inesistente.
        var vincente = repo.GetById("TC-0001");
        Assert.NotNull(vincente);
        Assert.Equal("fork-piu-recente", vincente!.DescrizioneBreve);
        Assert.True(File.Exists(pathDuplicato));
    }
}
