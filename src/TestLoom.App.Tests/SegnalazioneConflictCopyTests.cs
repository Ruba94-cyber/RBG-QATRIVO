using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Block C (revisione multi-PC 6.4.2): estende alle Segnalazioni la stessa classificazione content-driven delle
/// copie di conflitto OneDrive/SharePoint. A differenza di TestCase/NC/CR, il nome canonico di una Segnalazione
/// e' derivato direttamente dall'Uid (DataPaths.SegnalazioneFile), non da un Id testuale separato: prima di
/// questo fix, QUALUNQUE Uid duplicato (anche una copia OneDrive innocua a contenuto identico) rendeva l'intera
/// Segnalazione invisibile da GetAll() e bloccava Save()/Get() ("fork OneDrive aperto") finche' un Master non la
/// risolveva a mano — anche quando il "conflitto" non richiedeva alcuna decisione umana.
/// </summary>
public sealed class SegnalazioneConflictCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_SegConflictCopy_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void CopiaDiConflittoContenutoIdentico_NonBloccaPiuLaSegnalazioneCanonica()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new SegnalazioneRepository(paths, TimeSpan.Zero);

        var originale = new SegnalazioneDaClassificare { TestId = "T-0001" };
        var pathOriginale = paths.SegnalazioneFile(originale.Uid);
        JsonIo.WriteAtomic(pathOriginale, originale);

        var pathCopiaConflitto = pathOriginale.Replace(".json", "-ALTROPC.json");
        File.Copy(pathOriginale, pathCopiaConflitto); // stesso contenuto/Uid, nome non canonico

        repo.RefreshIndex();

        // Prima del fix: GetAll() vuoto e Get(uid) lanciava "fork OneDrive aperto" anche su una copia innocua.
        var visibile = Assert.Single(repo.GetAll());
        Assert.Equal("T-0001", visibile.TestId);
        Assert.NotNull(repo.Get(originale.Uid));
        Assert.Empty(repo.FileConProblemi);

        var copia = Assert.Single(repo.CopieDiConflitto);
        Assert.Equal(pathCopiaConflitto, copia.Percorso, ignoreCase: true);
        Assert.Contains("identico", copia.Errore, StringComparison.OrdinalIgnoreCase);

        Assert.True(File.Exists(pathCopiaConflitto)); // recuperabile, mai toccata
        Assert.True(File.Exists(pathOriginale));

        // Save() sul canonico deve tornare a funzionare (non più bloccato come "fork OneDrive aperto").
        repo.Save(visibile, visibile.RevisioneDati);
    }

    [Fact]
    public void CopiaDiConflittoContenutoDiverso_SegnalataDaVerificareENonPersaMai()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new SegnalazioneRepository(paths, TimeSpan.Zero);

        var originale = new SegnalazioneDaClassificare { TestId = "T-0002", MotivazioneEsclusione = "versione A" };
        var pathOriginale = paths.SegnalazioneFile(originale.Uid);
        JsonIo.WriteAtomic(pathOriginale, originale);

        var modificaAltraPostazione = new SegnalazioneDaClassificare { Uid = originale.Uid, TestId = "T-0002", MotivazioneEsclusione = "versione B, modificata sull'altro PC" };
        var pathCopiaConflitto = pathOriginale.Replace(".json", " (1).json");
        JsonIo.WriteAtomic(pathCopiaConflitto, modificaAltraPostazione);

        repo.RefreshIndex();

        var visibile = Assert.Single(repo.GetAll());
        Assert.Equal("versione A", visibile.MotivazioneEsclusione); // il canonico resta autorevole
        Assert.Empty(repo.FileConProblemi);

        var copia = Assert.Single(repo.CopieDiConflitto);
        Assert.Equal(pathCopiaConflitto, copia.Percorso, ignoreCase: true);
        Assert.Contains("DIVERSO", copia.Errore);

        Assert.True(File.Exists(pathCopiaConflitto));
        var recuperata = JsonIo.TryRead<SegnalazioneDaClassificare>(pathCopiaConflitto, out _);
        Assert.Equal("versione B, modificata sull'altro PC", recuperata?.MotivazioneEsclusione);
    }
}
