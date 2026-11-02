using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Copertura della cache del catalogo Veicoli usata da TestCaseRepository per decidere il bucket di cartella
/// Gamma di un Test (Addendum prestazioni 2026-09-23): invalidazione su mtime del file veicoli.json e nessuna
/// condivisione di stato fra istanze/cartelle diverse — richiesta esplicita della review indipendente.
/// </summary>
public sealed class TestCaseVeicoliBucketingCacheTests : IDisposable
{
    private readonly string _shared = Path.Combine(Path.GetTempPath(), "TestLoom_VeicoliCache_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _localDirs = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_shared)) { try { Directory.Delete(_shared, true); } catch { } }
        foreach (var dir in _localDirs) { if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } } }
    }

    private string NuovaCartellaLocale()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_VeicoliCache_Local_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _localDirs.Add(dir);
        return dir;
    }

    private static TestCase NuovoTest(string id) => new()
    {
        Id = id,
        Uid = Guid.NewGuid(),
        Componente = new EcuSelection { "Componente-A" },
        Veicolo = "V1",
        Veicoli = ["V1"],
        RevisioneDati = 1,
    };

    [Fact]
    public void ModificaEsternaDiVeicoliJson_InvalidaLaCache_NuovoTestUsaLaGammaAggiornata()
    {
        var paths = new DataPaths(_shared);
        paths.EnsureStructure();
        var repo = new TestCaseRepository(paths, new LocalTestIndexStore(_shared, NuovaCartellaLocale()));

        // Nessun veicoli.json ancora: il primo Test finisce nel bucket "senza gamma".
        var t1 = NuovoTest("TC-0001");
        repo.Save(t1, isNew: true);
        var path1 = repo.PercorsoPer(t1);
        Assert.Contains("_SENZA_GAMMA", path1);

        // Modifica ESTERNA del catalogo Veicoli (bypassa il repository: simula un altro Master che salva da
        // Impostazioni → Veicoli, o una sincronizzazione OneDrive) — deve invalidare la cache basata su mtime.
        new MasterDataRepository<Veicolo>(paths.VeicoliFile).Save(new List<Veicolo> { new() { Nome = "V1", GammaNome = "LCV" } });

        var t2 = NuovoTest("TC-0002");
        repo.Save(t2, isNew: true);
        var path2 = repo.PercorsoPer(t2);
        Assert.Contains(Path.Combine("testcases", "LCV"), path2);
    }

    [Fact]
    public void DueRepositorySuCartelleDiverse_NonCondividonoLaCacheVeicoli()
    {
        var pathsA = new DataPaths(_shared);
        pathsA.EnsureStructure();
        new MasterDataRepository<Veicolo>(pathsA.VeicoliFile).Save(new List<Veicolo> { new() { Nome = "V1", GammaNome = "LCV" } });
        var repoA = new TestCaseRepository(pathsA, new LocalTestIndexStore(_shared, NuovaCartellaLocale()));
        var tA = NuovoTest("TC-0001");
        repoA.Save(tA, isNew: true);
        Assert.Contains(Path.Combine("testcases", "LCV"), repoA.PercorsoPer(tA));

        // Un secondo workspace (altra cartella condivisa), stesso nome Veicolo ma NESSUNA Gamma configurata:
        // una nuova istanza di TestCaseRepository non deve mai ereditare la cache Veicoli di un'altra cartella.
        var sharedB = Path.Combine(Path.GetTempPath(), "TestLoom_VeicoliCache_SharedB_" + Guid.NewGuid().ToString("N"));
        try
        {
            var pathsB = new DataPaths(sharedB);
            pathsB.EnsureStructure();
            var repoB = new TestCaseRepository(pathsB, new LocalTestIndexStore(sharedB, NuovaCartellaLocale()));
            var tB = NuovoTest("TC-0001");
            repoB.Save(tB, isNew: true);
            Assert.Contains("_SENZA_GAMMA", repoB.PercorsoPer(tB));
        }
        finally
        {
            if (Directory.Exists(sharedB)) { try { Directory.Delete(sharedB, true); } catch { } }
        }
    }
}
