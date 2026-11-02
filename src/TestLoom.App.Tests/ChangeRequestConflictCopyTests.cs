using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Block C (revisione multi-PC 6.4.2): estende alle Change Request la stessa classificazione content-driven
/// delle copie di conflitto OneDrive/SharePoint già usata da TestCaseRepository (fase 3) e NonConformitaRepository
/// (block C). ChangeRequestRepository usa un indice SQLite generico (LocalEntityIndexStore): la classificazione
/// usa il nuovo LocalEntityIndexStore.GetByPath per ispezionare solo i pochi path di un gruppo duplicato,
/// senza materializzare l'intero archivio in RAM (obiettivo di scala dichiarato nei commenti del repository).
/// </summary>
public sealed class ChangeRequestConflictCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_CrConflictCopy_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void CopiaDiConflittoContenutoIdentico_NonDiventaUnUidDuplicatoAllarmante()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new ChangeRequestRepository(paths);

        var originale = new ChangeRequest { Id = "CR-2026-0001", Titolo = "autentica" };
        var pathOriginale = paths.ChangeRequestFile(originale.Id);
        JsonIo.WriteAtomic(pathOriginale, originale);

        var pathCopiaConflitto = pathOriginale.Replace(".json", "-ALTROPC.json");
        File.Copy(pathOriginale, pathCopiaConflitto); // stesso contenuto/Uid, nome non canonico

        repo.RefreshIndex();

        Assert.Single(repo.GetAll());
        Assert.Equal("autentica", repo.GetAll().Single().Titolo);
        Assert.Empty(repo.FileConProblemi); // niente più "UID duplicato" allarmante

        var copia = Assert.Single(repo.CopieDiConflitto);
        Assert.Equal(pathCopiaConflitto, copia.Percorso, ignoreCase: true);
        Assert.Contains("identico", copia.Errore, StringComparison.OrdinalIgnoreCase);

        Assert.True(File.Exists(pathCopiaConflitto)); // recuperabile, mai toccata
        Assert.True(File.Exists(pathOriginale));
    }

    [Fact]
    public void CopiaDiConflittoContenutoDiverso_SegnalataDaVerificareENonPersaMai()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new ChangeRequestRepository(paths);

        var originale = new ChangeRequest { Id = "CR-2026-0002", Titolo = "versione A" };
        var pathOriginale = paths.ChangeRequestFile(originale.Id);
        JsonIo.WriteAtomic(pathOriginale, originale);

        var modificaAltraPostazione = new ChangeRequest { Id = "CR-2026-0002", Uid = originale.Uid, Titolo = "versione B, modificata sull'altro PC" };
        var pathCopiaConflitto = pathOriginale.Replace(".json", " (1).json");
        JsonIo.WriteAtomic(pathCopiaConflitto, modificaAltraPostazione);

        repo.RefreshIndex();

        Assert.Single(repo.GetAll());
        Assert.Equal("versione A", repo.GetAll().Single().Titolo); // il canonico resta autorevole
        Assert.Empty(repo.FileConProblemi);

        var copia = Assert.Single(repo.CopieDiConflitto);
        Assert.Equal(pathCopiaConflitto, copia.Percorso, ignoreCase: true);
        Assert.Contains("DIVERSO", copia.Errore);

        Assert.True(File.Exists(pathCopiaConflitto));
        var recuperata = JsonIo.TryRead<ChangeRequest>(pathCopiaConflitto, out _);
        Assert.Equal("versione B, modificata sull'altro PC", recuperata?.Titolo);
    }

    [Fact]
    public void DueFileConNomeNonCanonicoEStessoUid_RestaUnUidDuplicatoAmbiguoComePrima()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new ChangeRequestRepository(paths);

        var cr = new ChangeRequest { Id = "CR-2026-0003", Titolo = "ambiguo" };
        var pathA = Path.Combine(paths.ChangeRequestDir, "cr_CR-2026-0003-PC1.json");
        var pathB = Path.Combine(paths.ChangeRequestDir, "cr_CR-2026-0003-PC2.json");
        JsonIo.WriteAtomic(pathA, cr);
        JsonIo.WriteAtomic(pathB, cr);

        repo.RefreshIndex();

        Assert.Empty(repo.CopieDiConflitto);
        Assert.Equal(2, repo.FileConProblemi.Count);
        Assert.All(repo.FileConProblemi, p => Assert.Contains("UID Change Request duplicato", p.Errore));
    }
}
