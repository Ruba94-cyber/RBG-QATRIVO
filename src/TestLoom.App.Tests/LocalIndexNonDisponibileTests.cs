using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TestLoom.App.Tests;

/// <summary>
/// Indice locale impossibile da aprire, o da mettere in quarantena, perche' il file e' bloccato da un altro processo.
/// I costruttori e RefreshIndex non lanciano un'eccezione non gestita: le letture vanno ai JSON autorevoli, le scritture
/// restano autorevoli, e quando il file si libera l'indice torna in uso. Nessun file viene cancellato.
/// </summary>
public sealed class LocalIndexNonDisponibileTests : IDisposable
{
    private readonly string _shared = Path.Combine(Path.GetTempPath(), "TestLoom_IdxNd_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_IdxNd_Local_" + Guid.NewGuid().ToString("N"));
    private readonly List<FileStream> _blocchi = new();

    public LocalIndexNonDisponibileTests()
    {
        Directory.CreateDirectory(_shared);
        Directory.CreateDirectory(_local);
        new DataPaths(_shared).EnsureStructure();
    }

    public void Dispose()
    {
        Libera();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_shared)) Directory.Delete(_shared, recursive: true); } catch { /* best-effort */ }
        try { if (Directory.Exists(_local)) Directory.Delete(_local, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ChangeRequest_FileBloccato_CostruttoreLettureScrittureERefreshNonLanciano()
    {
        var paths = new DataPaths(_shared);
        var seme = new ChangeRequestRepository(paths, localBaseDirectory: _local);
        seme.Save(Cr("CR-2026-0001", StatoChangeRequest.Aperta));
        seme.RefreshIndex();
        var db = seme.LocalIndexDatabasePath;
        Blocca(db);

        var repo = new ChangeRequestRepository(paths, localBaseDirectory: _local);

        Assert.NotEqual(LocalIndexHealth.Healthy, repo.IndexRecovery.Salute);
        Assert.Equal(new[] { "CR-2026-0001" }, repo.GetAll().Select(c => c.Id));   // letture dai JSON
        Assert.Equal(1L, repo.CountAperte());
        Assert.NotNull(repo.GetById("CR-2026-0001"));
        repo.Save(Cr("CR-2026-0002", StatoChangeRequest.Validata));                // scrittura: JSON autorevole
        Assert.True(File.Exists(paths.ChangeRequestFile("CR-2026-0002")));
        repo.RefreshIndex();                                                       // nessuna eccezione
        Assert.Equal(2, repo.GetAll().Count);
        Assert.True(File.Exists(db));                                              // il file bloccato non e' stato toccato
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(db)!, "entity-index.db.corrupt-*"));
    }

    [Fact]
    public void ChangeRequest_BloccatoAncheLaQuarantena_NonLanciaDalCostruttore()
    {
        // Header distrutto: SQLite lo legge e rileva la corruzione, ma il file non si puo' spostare (nessuna condivisione
        // di cancellazione), quindi la quarantena fallisce e l'indice resta indisponibile.
        var paths = new DataPaths(_shared);
        var seme = new ChangeRequestRepository(paths, localBaseDirectory: _local);
        seme.Save(Cr("CR-2026-0001", StatoChangeRequest.Aperta));
        seme.RefreshIndex();
        var db = seme.LocalIndexDatabasePath;
        SqliteConnection.ClearAllPools();
        Corrompi(db);
        _blocchi.Add(new FileStream(db, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite));

        var repo = new ChangeRequestRepository(paths, localBaseDirectory: _local);

        Assert.NotEqual(LocalIndexHealth.Healthy, repo.IndexRecovery.Salute);
        Assert.Equal(1, repo.GetAll().Count);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(db)!, "entity-index.db.corrupt-*"));
        Assert.True(File.Exists(db));
    }

    [Fact]
    public void ChangeRequest_QuandoIlFileSiLibera_LIndiceTornaInUsoAlRiallineamento()
    {
        var paths = new DataPaths(_shared);
        var seme = new ChangeRequestRepository(paths, localBaseDirectory: _local);
        seme.Save(Cr("CR-2026-0001", StatoChangeRequest.Aperta));
        seme.RefreshIndex();
        Blocca(seme.LocalIndexDatabasePath);
        var repo = new ChangeRequestRepository(paths, localBaseDirectory: _local);
        repo.Save(Cr("CR-2026-0002", StatoChangeRequest.Aperta));
        Assert.NotEqual(LocalIndexHealth.Healthy, repo.IndexRecovery.Salute);

        Libera();
        repo.RefreshIndex();

        Assert.Equal(LocalIndexHealth.Healthy, repo.IndexRecovery.Salute);
        Assert.Equal(2L, repo.CountAperte());
    }

    [Fact]
    public void NonConformita_FileBloccato_CostruttoreLettureScrittureERefreshNonLanciano()
    {
        var paths = new DataPaths(_shared);
        var storeSeme = new LocalNonConformitaIndexStore(_shared, _local);
        var seme = new NonConformitaRepository(paths, TimeSpan.Zero, storeSeme);
        seme.Save(NuovaNc("NC-0001"));
        seme.RefreshIndex();
        Blocca(storeSeme.DatabasePath);

        var repo = new NonConformitaRepository(paths, TimeSpan.Zero, new LocalNonConformitaIndexStore(_shared, _local));

        Assert.NotEqual(LocalIndexHealth.Healthy, repo.IndexRecovery!.Salute);
        Assert.Equal(new[] { "NC-0001" }, repo.GetAll().Select(n => n.Id));
        repo.Save(NuovaNc("NC-0002"));
        Assert.True(File.Exists(paths.NonConformitaFile("NC-0002")));
        repo.RefreshIndex();
        Assert.Equal(2, repo.GetAll().Count);
    }

    [Fact]
    public void Master_FileBloccato_RefreshNonLanciaEUsaLaFonteAutorevole()
    {
        var paths = new DataPaths(_shared);
        ScriviEcu(paths);
        var seme = new MasterDataLocalIndexCoordinator(paths, _local);
        seme.RefreshEcu();
        Blocca(seme.Store.DatabasePath);

        var coordinatore = new MasterDataLocalIndexCoordinator(paths, _local);

        Assert.NotNull(coordinatore.Store.IndisponibileMotivo);
        Assert.NotEqual(LocalIndexHealth.Healthy, coordinatore.IndexRecovery.Salute);
        var riallineamento = coordinatore.RefreshEcu();   // nessuna eccezione
        Assert.Equal(1L, riallineamento.AuthoritativeCount);
        Assert.False(riallineamento.Reconcile.SkippedAsCurrent);
    }

    [Fact]
    public void Master_QuandoIlFileSiLibera_LIndiceTornaSano()
    {
        var paths = new DataPaths(_shared);
        ScriviEcu(paths);
        var seme = new MasterDataLocalIndexCoordinator(paths, _local);
        seme.RefreshEcu();
        Blocca(seme.Store.DatabasePath);
        var coordinatore = new MasterDataLocalIndexCoordinator(paths, _local);
        Assert.NotNull(coordinatore.Store.IndisponibileMotivo);

        Libera();
        coordinatore.RefreshEcu();

        Assert.Null(coordinatore.Store.IndisponibileMotivo);
        Assert.Equal(LocalIndexHealth.Healthy, coordinatore.IndexRecovery.Salute);
        Assert.Equal(1L, coordinatore.Store.Count(MasterDataLocalIndexCoordinator.EcuCatalog, true));
    }

    private void Blocca(string database)
    {
        SqliteConnection.ClearAllPools();
        _blocchi.Add(new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.None));
    }

    private void Libera()
    {
        foreach (var blocco in _blocchi) blocco.Dispose();
        _blocchi.Clear();
    }

    private static void Corrompi(string database)
    {
        foreach (var companion in new[] { database + "-wal", database + "-shm" })
            if (File.Exists(companion)) File.Delete(companion);
        using var stream = new FileStream(database, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(System.Text.Encoding.ASCII.GetBytes("QUESTO NON E' UN DATABASE SQLITE - indice non disponibile"));
        stream.Flush(true);
    }

    private static void ScriviEcu(DataPaths paths) =>
        JsonIo.WriteAtomic(paths.EcuFile, new List<Componente>
        {
            new() { Nome = "BCM", VeicoliAssociati = new() { "Daily" }, ModificatoDa = "Seed" }
        });

    private static ChangeRequest Cr(string id, StatoChangeRequest stato) => new()
    {
        Id = id,
        Titolo = $"Change request {id}",
        Stato = stato,
        CreatoDa = "Operatore Prova A",
        DataCreazione = new DateTime(2026, 1, 1),
        UltimaModifica = new DateTime(2026, 2, 1),
    };

    private static NonConformita NuovaNc(string id) => new()
    {
        Id = id,
        Owner = "Operatore Prova",
        Descrizione = "Descrizione di prova",
    };
}
