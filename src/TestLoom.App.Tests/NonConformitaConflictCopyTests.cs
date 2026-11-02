using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Block C (revisione multi-PC 6.4.2): estende alle Non Conformità la stessa classificazione content-driven
/// delle copie di conflitto OneDrive/SharePoint già usata da TestCaseRepository (fase 3). Prima di questo,
/// QUALUNQUE copia di conflitto (anche a contenuto identico, innocua) finiva sempre nell'elenco allarmante "UID
/// duplicato" a ogni refresh, indistinguibile da una vera collisione di identità.
/// </summary>
public sealed class NonConformitaConflictCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_NcConflictCopy_" + Guid.NewGuid().ToString("N"));

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
        var repo = new NonConformitaRepository(paths, TimeSpan.Zero);

        var originale = new NonConformita { Id = "NC-0001", Descrizione = "autentica" };
        var pathOriginale = paths.NonConformitaFile(originale.Id);
        JsonIo.WriteAtomic(pathOriginale, originale);

        var pathCopiaConflitto = pathOriginale.Replace(".json", "-ALTROPC.json");
        File.Copy(pathOriginale, pathCopiaConflitto); // stesso contenuto/Uid, nome non canonico

        repo.RefreshIndex();

        Assert.Single(repo.GetAll());
        Assert.Equal("autentica", repo.GetAll().Single().Descrizione);
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
        var repo = new NonConformitaRepository(paths, TimeSpan.Zero);

        var originale = new NonConformita { Id = "NC-0002", Descrizione = "versione A" };
        var pathOriginale = paths.NonConformitaFile(originale.Id);
        JsonIo.WriteAtomic(pathOriginale, originale);

        var modificaAltraPostazione = new NonConformita { Id = "NC-0002", Uid = originale.Uid, Descrizione = "versione B, modificata sull'altro PC" };
        var pathCopiaConflitto = pathOriginale.Replace(".json", " (1).json");
        JsonIo.WriteAtomic(pathCopiaConflitto, modificaAltraPostazione);

        repo.RefreshIndex();

        Assert.Single(repo.GetAll());
        Assert.Equal("versione A", repo.GetAll().Single().Descrizione); // il canonico resta autorevole
        Assert.Empty(repo.FileConProblemi);

        var copia = Assert.Single(repo.CopieDiConflitto);
        Assert.Equal(pathCopiaConflitto, copia.Percorso, ignoreCase: true);
        Assert.Contains("DIVERSO", copia.Errore);

        Assert.True(File.Exists(pathCopiaConflitto));
        var recuperata = JsonIo.TryRead<NonConformita>(pathCopiaConflitto, out _);
        Assert.Equal("versione B, modificata sull'altro PC", recuperata?.Descrizione);
    }

    [Fact]
    public void DueFileConNomeNonCanonicoEStessoUid_RestaUnUidDuplicatoAmbiguoComePrima()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new NonConformitaRepository(paths, TimeSpan.Zero);

        var nc = new NonConformita { Id = "NC-0003", Descrizione = "ambiguo" };
        var pathA = Path.Combine(paths.NonConformitaDir, "nc_NC-0003-PC1.json");
        var pathB = Path.Combine(paths.NonConformitaDir, "nc_NC-0003-PC2.json");
        JsonIo.WriteAtomic(pathA, nc);
        JsonIo.WriteAtomic(pathB, nc);

        repo.RefreshIndex();

        Assert.Empty(repo.CopieDiConflitto);
        Assert.Equal(2, repo.FileConProblemi.Count);
        Assert.All(repo.FileConProblemi, p => Assert.Contains("UID Non Conformita duplicato", p.Errore));
    }
}
