using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class RepositoryConcorrenzaTests_ReplicaRaceMatrix
{
    [Fact]
    public void ExistingMaster_RemoteSameStampReplacement_BloccaLaFotografiaLocaleVecchia()
    {
        var rootA = NewReplicaRoot("A");
        var rootB = NewReplicaRoot("B");
        try
        {
            Directory.CreateDirectory(rootA);
            Directory.CreateDirectory(rootB);
            var pathA = Path.Combine(rootA, "funzionalita.json");
            var pathB = Path.Combine(rootB, "funzionalita.json");
            var uid = Guid.NewGuid();
            var baseline = new List<Funzionalita>
            {
                new() { Uid = uid, Nome = "AAAA", UltimaModifica = new DateTime(2026, 9, 5, 5, 0, 0, DateTimeKind.Utc) }
            };
            JsonIo.WriteAtomic(pathA, baseline);
            File.Copy(pathA, pathB, overwrite: true);

            var repoA = new MasterDataRepository<Funzionalita>(pathA);
            var repoB = new MasterDataRepository<Funzionalita>(pathB);
            var localA = repoA.Load();
            var localB = repoB.Load();
            var originalStamp = File.GetLastWriteTimeUtc(pathA);
            var originalLength = new FileInfo(pathA).Length;

            localA[0].Archiviata = true; // modifica locale basata sulla fotografia AAAA.
            localB[0].Nome = "BBBB";     // modifica remota della stessa lunghezza.
            repoB.Save(localB);

            File.Copy(pathB, pathA, overwrite: true);
            File.SetLastWriteTimeUtc(pathA, originalStamp); // simula replica same-stamp.
            Assert.Equal(originalLength, new FileInfo(pathA).Length);
            var remoteBytes = File.ReadAllBytes(pathA);

            Assert.Throws<MasterDataConflictException>(() => repoA.Save(localA));
            Assert.Equal(remoteBytes, File.ReadAllBytes(pathA));

            var current = new MasterDataRepository<Funzionalita>(pathA).Load();
            var row = Assert.Single(current);
            Assert.Equal("BBBB", row.Nome);
            Assert.False(row.Archiviata);
        }
        finally
        {
            TryDelete(rootA);
            TryDelete(rootB);
        }
    }

    [Fact]
    public void MissingMaster_ConcurrentCreate_DivergenteNonVieneSovrascritta()
    {
        var rootA = NewReplicaRoot("A-create");
        var rootB = NewReplicaRoot("B-create");
        try
        {
            Directory.CreateDirectory(rootA);
            Directory.CreateDirectory(rootB);
            var pathA = Path.Combine(rootA, "componente.json");
            var pathB = Path.Combine(rootB, "componente.json");

            // Il repository A nasce mentre il file non esiste: il suo primo Save costruito ex-novo deve restare
            // create-only anche se la replica B arriva prima della rename locale.
            var repoA = new MasterDataRepository<Componente>(pathA);
            var intendedA = new List<Componente> { new() { Nome = "Componente-A" } };
            var remoteB = new List<Componente> { new() { Nome = "Componente-B" } };
            JsonIo.WriteAtomic(pathB, remoteB);
            File.Copy(pathB, pathA, overwrite: true);
            var bytesB = File.ReadAllBytes(pathA);

            Assert.Throws<MasterDataConflictException>(() => repoA.Save(intendedA));
            Assert.Equal(bytesB, File.ReadAllBytes(pathA));
            Assert.Equal("Componente-B", Assert.Single(new MasterDataRepository<Componente>(pathA).Load()).Nome);
        }
        finally
        {
            TryDelete(rootA);
            TryDelete(rootB);
        }
    }

    [Fact]
    public void MissingMaster_ConcurrentCreateIdentica_EIdempotente()
    {
        var rootA = NewReplicaRoot("A-identical");
        var rootB = NewReplicaRoot("B-identical");
        try
        {
            Directory.CreateDirectory(rootA);
            Directory.CreateDirectory(rootB);
            var pathA = Path.Combine(rootA, "componente.json");
            var pathB = Path.Combine(rootB, "componente.json");
            var same = new List<Componente> { new() { Nome = "Componente-SAME" } };
            var repoA = new MasterDataRepository<Componente>(pathA);

            JsonIo.WriteAtomic(pathB, same);
            File.Copy(pathB, pathA, overwrite: true);
            var before = File.ReadAllBytes(pathA);
            var stamp = File.GetLastWriteTimeUtc(pathA);

            repoA.Save(new List<Componente> { new() { Nome = "Componente-SAME" } });

            Assert.Equal(before, File.ReadAllBytes(pathA));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(pathA));
        }
        finally
        {
            TryDelete(rootA);
            TryDelete(rootB);
        }
    }

    private static string NewReplicaRoot(string suffix) =>
        Path.Combine(Path.GetTempPath(), "TestLoom_6_TEST", "replica-race-" + suffix + "-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
