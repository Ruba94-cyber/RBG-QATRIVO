using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Regressioni per il passaggio della rilettura periodica su un thread di background (Addendum:
/// audit UX 2026-08-26, punto 4). Prima di quell'intervento l'unico thread che toccava gli indici era quello
/// della UI, quindi i Dictionary interni non avevano bisogno di sincronizzazione; ora che RefreshIndex puo'
/// girare mentre l'utente salva, servono due garanzie esplicite:
///   1. nessuna eccezione/corruzione da accesso concorrente alle strutture in memoria;
///   2. nessun salvataggio perso — uno scambio cieco dell'indice a fine rilettura cancellerebbe dalla cache
///      un test appena salvato, che pur essendo corretto su disco sparirebbe dalle liste fino al giro dopo.
/// Il secondo punto e' quello che conta di piu' nell'uso reale descritto dall'utente: piu' operatori creano
/// test nuovi contemporaneamente sulla stessa cartella condivisa.</summary>
public sealed class RepositoryConcorrenzaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_Concorrenza_" + Guid.NewGuid().ToString("N"));

    public RepositoryConcorrenzaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private TestCaseRepository NuovoRepo()
    {
        var paths = new DataPaths(_dir);
        paths.EnsureStructure();
        return new TestCaseRepository(paths);
    }

    private static TestCase NuovoTest(string id) => new()
    {
        Id = id,
        Componente = new EcuSelection { "Componente-A" },
        DescrizioneBreve = "concorrenza",
        StatoLavoro = StatoLavoroTest.Pianificato,
    };

    [Fact]
    public async Task RefreshIndexConcorrenteASalvataggi_NonPerdeNessunTest()
    {
        var repo = NuovoRepo();

        // Un primo test gia' presente, cosi' la rilettura ha un indice di partenza non vuoto.
        repo.Save(NuovoTest("TC-0000"), isNew: true);

        var errori = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        const int quanti = 40;

        // Scrittore: simula l'operatore che continua a creare test mentre il refresh gira.
        var scrittore = Task.Run(() =>
        {
            for (var i = 1; i <= quanti; i++)
            {
                try { repo.Save(NuovoTest($"TC-{i:D4}"), isNew: true); }
                catch (Exception ex) { errori.Add(ex); }
            }
        });

        // Lettore/indicizzatore: il giro periodico che ora vive su un thread di background.
        var indicizzatore = Task.Run(() =>
        {
            for (var i = 0; i < quanti; i++)
            {
                try { repo.RefreshIndex(); _ = repo.GetAll().Count; }
                catch (Exception ex) { errori.Add(ex); }
            }
        });

        await Task.WhenAll(scrittore, indicizzatore);

        Assert.Empty(errori);

        // Nessun salvataggio perso nell'indice in memoria: e' la garanzia che lo scambio cieco romperebbe.
        var idInMemoria = repo.GetAll().Select(tc => tc.Id).ToHashSet();
        for (var i = 0; i <= quanti; i++)
            Assert.Contains($"TC-{i:D4}", idInMemoria);

        // E l'indice combacia con quello che c'e' davvero su disco.
        var suDisco = Directory.EnumerateFiles(_dir, "testcase_*.json", SearchOption.AllDirectories).Count();
        Assert.Equal(suDisco, repo.GetAll(includeEliminati: true).Count);
    }

    [Fact]
    public async Task SalvataggioDuranteRilettura_RestaNellIndiceDopoLoScambio()
    {
        var repo = NuovoRepo();
        repo.Save(NuovoTest("TC-0001"), isNew: true);
        repo.RefreshIndex();

        // Il caso stretto: il salvataggio avviene mentre una rilettura e' gia' partita (quindi la sua
        // "fotografia" non lo conosce). Il merge finale deve conservarlo comunque.
        var salvataggio = Task.Run(() => repo.Save(NuovoTest("TC-0002"), isNew: true));
        var rilettura = Task.Run(() => repo.RefreshIndex());
        await Task.WhenAll(salvataggio, rilettura);

        Assert.Contains("TC-0002", repo.GetAll().Select(tc => tc.Id));
    }

    [Fact]
    public void MasterDataRepositoryLetturaConcorrente_NonProduceListeVuoteSpurie()
    {
        var paths = new DataPaths(_dir);
        paths.EnsureStructure();
        var repo = new MasterDataRepository<Componente>(paths.EcuFile);
        repo.Save(new List<Componente> { new() { Nome = "Componente-A" }, new() { Nome = "Componente-B" } });

        var errori = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var conteggi = new System.Collections.Concurrent.ConcurrentBag<int>();

        Parallel.For(0, 200, _ =>
        {
            try { conteggi.Add(repo.Load().Count); }
            catch (Exception ex) { errori.Add(ex); }
        });

        Assert.Empty(errori);
        // Nessuna lettura deve osservare una cache "mezza scritta" e restituire una lista incompleta.
        Assert.All(conteggi, c => Assert.Equal(2, c));
    }
}
