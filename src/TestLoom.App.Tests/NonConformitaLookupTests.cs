using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Ricerca per chiave O(1) delle Non Conformita' (GetById/GetByUid su dizionari mantenuti a ogni mutazione della cache): stessi risultati
/// della scansione lineare storica dopo Save, sostituzione, eliminazione, refresh dei percorsi e refresh completo; nessun dato stantio.
/// </summary>
public sealed class NonConformitaLookupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_NcLookup_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_NcLookupIdx_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var d in new[] { _root, _local }) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
    }

    private (NonConformitaRepository Repo, DataPaths Paths) Crea()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var repo = new NonConformitaRepository(paths, TimeSpan.Zero, new LocalNonConformitaIndexStore(paths.Root, _local));
        repo.RefreshIndex();
        return (repo, paths);
    }

    private static void ConfrontaConScansioneLineare(NonConformitaRepository repo, IEnumerable<string> idsDaProvare, IEnumerable<Guid> uidDaProvare)
    {
        var tutte = repo.GetAll();
        foreach (var id in idsDaProvare)
        {
            var attesa = tutte.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
            var trovata = repo.GetById(id);
            Assert.Equal(attesa?.Uid, trovata?.Uid);
            Assert.Equal(attesa?.RevisioneDati, trovata?.RevisioneDati);
        }
        foreach (var uid in uidDaProvare)
        {
            var attesa = tutte.FirstOrDefault(n => n.Uid == uid);
            var trovata = repo.GetByUid(uid);
            Assert.Equal(attesa?.Id, trovata?.Id);
        }
    }

    [Fact]
    public void GetByIdEGetByUid_UgualiAllaScansioneLineareDopoOgniMutazione()
    {
        var (repo, paths) = Crea();
        var uids = new List<Guid>();
        for (var n = 1; n <= 60; n++)
        {
            var nc = new NonConformita { Id = $"NC-{n:D4}", Owner = "Mario", Priorita = PrioritaOperativa.Major, DataApertura = new DateTime(2026, 1, 1).AddDays(n) };
            repo.Save(nc);
            uids.Add(nc.Uid);
        }
        var ids = Enumerable.Range(1, 62).Select(n => $"NC-{n:D4}").Append("nc-0007").Append("").ToList();
        ConfrontaConScansioneLineare(repo, ids, uids.Append(Guid.NewGuid()));
        Assert.Equal("NC-0007", repo.GetById("nc-0007")!.Id); // senza distinguere le maiuscole, come prima

        // Save di una NC esistente: la voce viene sostituita, non duplicata
        var daModificare = repo.GetById("NC-0010")!;
        daModificare.Owner = "Luca";
        repo.Save(daModificare, daModificare.RevisioneDati);
        Assert.Equal("Luca", repo.GetById("NC-0010")!.Owner);
        Assert.Equal("Luca", repo.GetByUid(daModificare.Uid)!.Owner);
        ConfrontaConScansioneLineare(repo, ids, uids);

        // eliminazione: la chiave non risolve piu'
        var daEliminare = repo.GetById("NC-0020")!;
        repo.Elimina(daEliminare);
        Assert.Null(repo.GetById("NC-0020"));
        Assert.Null(repo.GetByUid(daEliminare.Uid));
        ConfrontaConScansioneLineare(repo, ids, uids);

        // un altro PC modifica un file e ne elimina un altro: RefreshPaths / RefreshIndex riallineano le chiavi
        var esterna = repo.GetById("NC-0030")!;
        esterna.Owner = "Altro PC"; esterna.RevisioneDati += 1;
        JsonIo.WriteAtomic(paths.NonConformitaFile(esterna.Id), esterna, overwrite: true);
        File.Delete(paths.NonConformitaFile("NC-0031"));
        repo.RefreshPaths(new[] { paths.NonConformitaFile("NC-0030"), paths.NonConformitaFile("NC-0031") });
        Assert.Equal("Altro PC", repo.GetById("NC-0030")!.Owner);
        Assert.Null(repo.GetById("NC-0031"));
        ConfrontaConScansioneLineare(repo, ids, uids);

        var nuovaEsterna = new NonConformita { Id = "NC-0099", Owner = "Sync" };
        JsonIo.WriteAtomic(paths.NonConformitaFile(nuovaEsterna.Id), nuovaEsterna, overwrite: false);
        File.Delete(paths.NonConformitaFile("NC-0040"));
        repo.RefreshIndex();
        Assert.Equal("Sync", repo.GetById("NC-0099")!.Owner);
        Assert.Null(repo.GetById("NC-0040"));
        ConfrontaConScansioneLineare(repo, ids.Append("NC-0099"), uids.Append(nuovaEsterna.Uid));
    }

    [Fact]
    public void GetById_DopoRiavvio_CaricaDallIndiceLocaleESaRisolvere()
    {
        var (repo, paths) = Crea();
        var nc = new NonConformita { Id = "NC-0001", Owner = "Mario" };
        repo.Save(nc);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var riavviato = new NonConformitaRepository(paths, TimeSpan.Zero, new LocalNonConformitaIndexStore(paths.Root, _local));
        Assert.Equal(nc.Uid, riavviato.GetById("nc-0001")?.Uid);
        Assert.Equal("NC-0001", riavviato.GetByUid(nc.Uid)?.Id);
    }

    [Fact]
    public async Task Precarica_CompletaIPassiEPortaIlProgressoAlTotale()
    {
        var (repo, _) = Crea();
        repo.Save(new NonConformita { Id = "NC-0001" });
        var passi = new List<(int, int)>();
        await repo.PrecaricaAsync(new Progress<(int fatti, int totale)>(p => { lock (passi) passi.Add(p); }), default);
        await Task.Delay(100);
        lock (passi) Assert.Contains((3, 3), passi);
    }
}
