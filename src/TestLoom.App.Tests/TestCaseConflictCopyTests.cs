using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// FASE 3 (multi-PC 6.4.2, correzione orchestratore): quando due postazioni scrivono lo stesso file quasi in
/// contemporanea, OneDrive/SharePoint risolve il conflitto creando una COPIA con un nome diverso (es.
/// "testcase_T-0001-NOMEPC.json", "testcase_T-0001 (1).json") invece di sovrascrivere l'originale. La
/// classificazione è content-driven (confronta il nome file con quello che DataPaths scriverebbe per l'Id
/// letto dal CONTENUTO, non con un pattern/formato generico) cosi' funziona con qualunque Id, generato da
/// IdGenerator o storico/importato.
/// </summary>
public sealed class TestCaseConflictCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_ConflictCopy_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void SaveTestCase_SpostamentoECU_ControllaIlForkRimastoNelPercorsoOrigine()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new TestCaseRepository(paths);
        var tc = new TestCase
        {
            Id = "TC-MOVE-SOURCE-FORK", Componente = "ECM", Veicolo = "Daily",
            Descrizione = "prima"
        };
        repo.Save(tc, isNew: true);
        var oldPath = repo.PercorsoPer(tc);
        var oldBytes = File.ReadAllBytes(oldPath);
        var fork = oldPath.Replace(".json", " (Secondo-PC).json",
            StringComparison.OrdinalIgnoreCase);
        JsonIo.WriteAtomic(fork, new TestCase
        {
            Id = tc.Id, Uid = tc.Uid, Componente = tc.Componente, Veicolo = tc.Veicolo,
            RevisioneDati = tc.RevisioneDati, Descrizione = "modifica concorrente"
        });
        var forkBytes = File.ReadAllBytes(fork);

        tc.Componente = "TCM"; // NuovoPathFor != PathFor; il fork resta nel vecchio folder.
        Assert.Throws<IOException>(() => repo.Save(tc, isNew: false, revisioneAttesa: tc.RevisioneDati));

        Assert.Equal(oldBytes, File.ReadAllBytes(oldPath));
        Assert.Equal(forkBytes, File.ReadAllBytes(fork));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(paths.TestCasesDir, $"testcase_{tc.Id}.json",
                SearchOption.AllDirectories),
            file => !file.Equals(oldPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SoftDeleteTest_CopiaForkVisibleBloccaCestinoSenzaPerdereNessunFile()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new TestCaseRepository(paths);
        var tc = new TestCase { Id = "T-SOFT-FORK", Componente = "ECM", Veicolo = "Daily" };
        repo.Save(tc, isNew: true);
        var original = repo.PercorsoPer(tc);
        var fork = original.Replace(".json", " (Secondo-PC).json",
            StringComparison.OrdinalIgnoreCase);
        var divergent = new TestCase
        {
            Id = tc.Id, Uid = tc.Uid, Componente = tc.Componente, Veicolo = tc.Veicolo,
            RevisioneDati = tc.RevisioneDati, Descrizione = "altro computer"
        };
        JsonIo.WriteAtomic(fork, divergent);
        var oldBytes = File.ReadAllBytes(original);
        var forkBytes = File.ReadAllBytes(fork);

        Assert.Throws<IOException>(() => repo.SoftDelete(tc, "QA"));

        Assert.Equal(oldBytes, File.ReadAllBytes(original));
        Assert.Equal(forkBytes, File.ReadAllBytes(fork));
        Assert.False(File.Exists(paths.CestinoFile(tc.Id)));
        Assert.False(tc.Eliminato);
    }

    [Fact]
    public void TestNelCestino_CopiaForkBloccaRipristinoEDefinitivaEliminazione()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new TestCaseRepository(paths);
        var tc = new TestCase { Id = "T-TRASH-FORK", Componente = "ECM", Veicolo = "Daily" };
        repo.Save(tc, isNew: true);
        repo.SoftDelete(tc, "QA");
        var trash = paths.CestinoFile(tc.Id);
        var fork = trash.Replace(".json", " (Secondo-PC).json",
            StringComparison.OrdinalIgnoreCase);
        JsonIo.WriteAtomic(fork, new TestCase
        {
            Id = tc.Id, Uid = tc.Uid, Componente = tc.Componente, Veicolo = tc.Veicolo,
            RevisioneDati = tc.RevisioneDati, Eliminato = true,
            Descrizione = "modifica concorrente nel cestino"
        });
        var original = File.ReadAllBytes(trash);
        var alternate = File.ReadAllBytes(fork);

        Assert.Throws<IOException>(() => repo.Restore(tc, "QA"));
        Assert.Throws<IOException>(() => repo.EliminaDefinitivamente(tc));

        Assert.Equal(original, File.ReadAllBytes(trash));
        Assert.Equal(alternate, File.ReadAllBytes(fork));
        Assert.True(tc.Eliminato);
    }

    [Fact]
    public void CopiaDiConflittoContenutoIdentico_NonDiventaUnSecondoTestEnonUnIdDuplicatoAmbiguo()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new TestCaseRepository(paths);

        var originale = new TestCase { Id = "T-0001", Componente = "ECM", Veicolo = "Daily", Descrizione = "autentico" };
        repo.Save(originale, isNew: true);
        var pathOriginale = repo.PercorsoPer(originale);

        var pathCopiaConflitto = pathOriginale.Replace(".json", "-ALTROPC.json");
        File.Copy(pathOriginale, pathCopiaConflitto); // stesso contenuto/Id, nome non canonico

        repo.RefreshIndex();

        var tutti = repo.GetAll();
        Assert.Single(tutti); // niente secondo Test indirizzabile
        Assert.Equal("autentico", tutti.Single().Descrizione);
        Assert.Empty(repo.DuplicatiId); // non è l'elenco "ID duplicato" ambiguo

        var copia = Assert.Single(repo.CopieDiConflitto);
        Assert.Equal(pathCopiaConflitto, copia.Percorso, ignoreCase: true);
        Assert.Contains("identico", copia.Errore, StringComparison.OrdinalIgnoreCase);

        // Recuperabile: il file fisico non è mai stato toccato.
        Assert.True(File.Exists(pathCopiaConflitto));
        Assert.True(File.Exists(pathOriginale));
    }

    [Fact]
    public void CopiaDiConflittoContenutoDiverso_VieneSegnalataComeDaVerificareENonPersaMai()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new TestCaseRepository(paths);

        var originale = new TestCase { Id = "T-0002", Componente = "ECM", Veicolo = "Daily", Descrizione = "versione A" };
        repo.Save(originale, isNew: true);
        var pathOriginale = repo.PercorsoPer(originale);

        var modificaLocale = new TestCase
        {
            Id = "T-0002", // stesso Id, contenuto diverso: l'altra postazione ha modificato la sua copia
            Uid = originale.Uid,
            Componente = "ECM",
            Veicolo = "Daily",
            Descrizione = "versione B, modificata sull'altro PC"
        };
        var pathCopiaConflitto = pathOriginale.Replace(".json", " (1).json");
        JsonIo.WriteAtomic(pathCopiaConflitto, modificaLocale);

        repo.RefreshIndex();

        Assert.Single(repo.GetAll());
        Assert.Equal("versione A", repo.GetAll().Single().Descrizione); // il canonico resta l'autorevole
        Assert.Empty(repo.DuplicatiId);

        var copia = Assert.Single(repo.CopieDiConflitto);
        Assert.Equal(pathCopiaConflitto, copia.Percorso, ignoreCase: true);
        Assert.Contains("DIVERSO", copia.Errore);

        // Nessuna perdita di dati: il contenuto "versione B" resta leggibile direttamente dal file.
        Assert.True(File.Exists(pathCopiaConflitto));
        var recuperata = JsonIo.TryRead<TestCase>(pathCopiaConflitto, out _);
        Assert.Equal("versione B, modificata sull'altro PC", recuperata?.Descrizione);
    }

    [Fact]
    public void DueFileConNomeNonCanonicoEStessoId_RestaUnIdDuplicatoAmbiguoComePrima()
    {
        // Nessuno dei due file ha il nome canonico per l'Id (es. l'originale è stato rinominato/spostato):
        // non c'è modo sicuro di sapere qual è "l'originale", quindi resta l'elenco ambiguo esistente.
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new TestCaseRepository(paths);

        var tc = new TestCase { Id = "T-0003", Componente = "ECM", Veicolo = "Daily", Descrizione = "ambiguo" };
        var dir = paths.TestCaseDir("ECM");
        Directory.CreateDirectory(dir);
        var pathA = Path.Combine(dir, "testcase_T-0003-PC1.json");
        var pathB = Path.Combine(dir, "testcase_T-0003-PC2.json");
        JsonIo.WriteAtomic(pathA, tc);
        JsonIo.WriteAtomic(pathB, tc);

        repo.RefreshIndex();

        Assert.Empty(repo.CopieDiConflitto);
        var duplicato = Assert.Single(repo.DuplicatiId);
        Assert.Equal("T-0003", duplicato.Id);
        Assert.Equal(2, duplicato.Percorsi.Count);
    }

    [Fact]
    public void RefreshPaths_NuovaCopiaDiConflittoCreataDalWatcher_VieneRilevataSubito()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new TestCaseRepository(paths, new LocalTestIndexStore(_root, Path.Combine(_root, "_local")));

        var originale = new TestCase { Id = "T-0004", Componente = "ECM", Veicolo = "Daily", Descrizione = "autentico" };
        repo.Save(originale, isNew: true);
        repo.RefreshIndex();
        var pathOriginale = repo.PercorsoPer(originale);

        var pathCopiaConflitto = pathOriginale.Replace(".json", "-NOMEPC.json");
        File.Copy(pathOriginale, pathCopiaConflitto);

        repo.RefreshPaths([pathCopiaConflitto]); // solo l'evento watcher sulla nuova copia, nessun RefreshIndex completo

        Assert.Single(repo.GetAll());
        Assert.Contains(repo.CopieDiConflitto, c => c.Percorso.Equals(pathCopiaConflitto, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SpostamentoTestInCorsoFraCartelle_StessoUidEDueCanonici_NonDiventaUnIdDuplicatoAmbiguo()
    {
        // Revisione release/6.4.2: durante la sincronizzazione di uno spostamento (cambio Gamma/componente -> nuova
        // cartella), un'altra postazione può vedere per un po' sia il file nuovo (cartella destinazione) sia
        // il vecchio non ancora tombstone (cartella origine): entrambi hanno il nome CANONICO
        // "testcase_<Id>.json" (solo la cartella cambia). Prima della correzione questo finiva erroneamente in
        // DuplicatiId ("ID duplicato" falso allarme); ora deve restare un Test unico indirizzabile, con la
        // copia residua segnalata come "spostamento in corso", mai come ID duplicato.
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new TestCaseRepository(paths);

        var tc = new TestCase { Id = "T-0006", Componente = "ECM", Veicolo = "Daily", Descrizione = "post-spostamento" };
        repo.Save(tc, isNew: true);
        var pathNuovo = repo.PercorsoPer(tc); // cartella ECM (destinazione)

        // Simula il vecchio path pre-spostamento (altra componente/cartella), stesso Uid, stesso nome file canonico,
        // contenuto leggermente più vecchio (mtime precedente = la scrittura di origine, mai aggiornata dopo
        // lo spostamento).
        var dirVecchia = paths.TestCaseDir("TCM");
        Directory.CreateDirectory(dirVecchia);
        var pathVecchio = Path.Combine(dirVecchia, Path.GetFileName(pathNuovo));
        var contenutoVecchio = new TestCase { Id = "T-0006", Uid = tc.Uid, Componente = "TCM", Veicolo = "Daily", Descrizione = "pre-spostamento" };
        JsonIo.WriteAtomic(pathVecchio, contenutoVecchio);
        File.SetLastWriteTimeUtc(pathVecchio, DateTime.UtcNow.AddMinutes(-10));

        repo.RefreshIndex();

        Assert.Empty(repo.DuplicatiId); // niente più falso allarme "ID duplicato"
        Assert.Single(repo.GetAll());
        Assert.Equal("post-spostamento", repo.GetAll().Single().Descrizione); // vince il più recente

        var copia = Assert.Single(repo.CopieDiConflitto);
        Assert.Equal(pathVecchio, copia.Percorso, ignoreCase: true);
        Assert.Contains("spostamento", copia.Errore, StringComparison.OrdinalIgnoreCase);

        // Recuperabile: nessuna scrittura/cancellazione sul file vecchio finché la sincronizzazione non lo rimuove.
        Assert.True(File.Exists(pathVecchio));
    }

    [Fact]
    public void RefreshPaths_CopiaDiConflittoRisoltaDalMasterCancellandola_SparisceDallElenco()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new TestCaseRepository(paths);

        var originale = new TestCase { Id = "T-0005", Componente = "ECM", Veicolo = "Daily", Descrizione = "autentico" };
        repo.Save(originale, isNew: true);
        var pathOriginale = repo.PercorsoPer(originale);
        var pathCopiaConflitto = pathOriginale.Replace(".json", "-NOMEPC.json");
        File.Copy(pathOriginale, pathCopiaConflitto);
        repo.RefreshIndex();
        Assert.Single(repo.CopieDiConflitto);

        File.Delete(pathCopiaConflitto); // il Master risolve manualmente il conflitto
        repo.RefreshPaths([pathCopiaConflitto]);

        Assert.Empty(repo.CopieDiConflitto);
        Assert.Single(repo.GetAll());
    }
}
