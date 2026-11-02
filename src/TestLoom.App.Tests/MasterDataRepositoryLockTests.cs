using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Catalogo legacy condiviso (P1.7): il salvataggio rilegge il file, lo confronta con la fotografia attesa e scrive.
/// Prima della correzione quella finestra non aveva alcun lock condiviso: due PC che superavano il confronto nello
/// stesso istante si sovrascrivevano e le righe dell'altro andavano perse. Ora rilettura, confronto e scrittura
/// avvengono sotto il lock di commit del catalogo, come per CR e NC.
/// </summary>
public sealed class MasterDataRepositoryLockTests : IDisposable
{
    private const string NomeCatalogo = "componente.json";

    private readonly string _cartella = Path.Combine(Path.GetTempPath(), "TestLoom_MASTER_LOCK_" + Guid.NewGuid().ToString("N"));

    public MasterDataRepositoryLockTests()
    {
        // Una cartella assente vale come condivisa non disponibile: il salvataggio la rifiuterebbe prima del lock.
        Directory.CreateDirectory(_cartella);
    }

    [Fact]
    public async Task CommitAltruiInCorso_SalvataggioAttendeELeggeLaVersioneAggiornata()
    {
        var percorso = Path.Combine(_cartella, NomeCatalogo);
        new MasterDataRepository<Componente>(percorso).Save(new List<Componente> { NuovaEcu("Componente-BASE") });

        var repoB = new MasterDataRepository<Componente>(percorso);
        var listaB = repoB.Load();
        var attesaB = listaB.ToList();
        listaB.Add(NuovaEcu("Componente-B"));

        // Un altro PC tiene il lock di commit del catalogo mentre B prova a salvare.
        var lockAltroPc = DomainRecordCommitLock.Acquire("Anagrafica", NomeCatalogo, percorso);
        Task salvataggioB;
        try
        {
            salvataggioB = Task.Run(() => repoB.Save(listaB, attesaB, allowSilentRemoteMerge: false));

            // B non deve completare finche' il lock e' occupato: prima della correzione scriveva subito.
            var primoCompletato = await Task.WhenAny(salvataggioB, Task.Delay(TimeSpan.FromMilliseconds(400)));
            Assert.NotSame(salvataggioB, primoCompletato);

            // L'altro PC completa la sua scrittura mentre ha il lock: aggiunge componente-ALTRO.
            JsonIo.WriteAtomic(percorso, new List<Componente> { NuovaEcu("Componente-BASE"), NuovaEcu("Componente-ALTRO") }, overwrite: true);
        }
        finally
        {
            lockAltroPc.Dispose();
        }

        // Sbloccato, B rilegge il file sotto lock: la versione cambiata e' un conflitto e nessuna riga va persa.
        await Assert.ThrowsAsync<MasterDataConflictException>(() => salvataggioB);
        Assert.Equal(new[] { "Componente-BASE", "Componente-ALTRO" }, NomiNelFile(percorso));
    }

    [Fact]
    public async Task DueIstanzeSalvanoInContemporanea_UnaSoleRiesceLAltraRiceveConflittoSenzaRighePerse()
    {
        const int iterazioni = 10;
        for (var iterazione = 0; iterazione < iterazioni; iterazione++)
        {
            var percorso = Path.Combine(_cartella, $"ecu_{iterazione:D2}.json");
            new MasterDataRepository<Componente>(percorso).Save(new List<Componente> { NuovaEcu("Componente-BASE") });

            var repoA = new MasterDataRepository<Componente>(percorso);
            var repoB = new MasterDataRepository<Componente>(percorso);
            var listaA = repoA.Load();
            var attesaA = listaA.ToList();
            listaA.Add(NuovaEcu("Componente-A"));
            var listaB = repoB.Load();
            var attesaB = listaB.ToList();
            listaB.Add(NuovaEcu("Componente-B"));

            using var partenza = new Barrier(2);
            var esitoA = Task.Run(() => SalvaAllaPartenza(partenza, repoA, listaA, attesaA));
            var esitoB = Task.Run(() => SalvaAllaPartenza(partenza, repoB, listaB, attesaB));
            var esiti = await Task.WhenAll(esitoA, esitoB);

            // Esattamente un salvataggio riesce; l'altro riceve un conflitto, mai due successi che si sovrascrivono.
            Assert.Single(esiti, e => e == null);
            Assert.Single(esiti, e => e is MasterDataConflictException);
            var nomi = NomiNelFile(percorso);
            Assert.Equal(2, nomi.Length);
            Assert.Contains("Componente-BASE", nomi);
            Assert.True(nomi.Contains("Componente-A") ^ nomi.Contains("Componente-B"), $"Iterazione {iterazione}: {string.Join(", ", nomi)}");
        }
    }

    /// <summary>Esito del salvataggio: null se riesce, altrimenti il conflitto. Altre eccezioni non sono attese e
    /// risalgono al test.</summary>
    private static Exception? SalvaAllaPartenza(Barrier partenza, MasterDataRepository<Componente> repo, List<Componente> lista,
        IReadOnlyList<Componente> attesa)
    {
        partenza.SignalAndWait();
        try
        {
            repo.Save(lista, attesa, allowSilentRemoteMerge: false);
            return null;
        }
        catch (MasterDataConflictException ex)
        {
            return ex;
        }
    }

    private static Componente NuovaEcu(string nome) => new() { Nome = nome };

    private static string[] NomiNelFile(string percorso) =>
        new MasterDataRepository<Componente>(percorso).Load().Select(e => e.Nome).ToArray();

    public void Dispose()
    {
        try { if (Directory.Exists(_cartella)) Directory.Delete(_cartella, recursive: true); }
        catch { /* eventuale lock residuo: la cartella resta sotto TEMP */ }
    }
}
