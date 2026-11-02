using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// 6.7.1 (fix rete): (a) la lettura strict del journal Phase D usa la condivisione ReadLinesShared, così un handle aperto da
/// un altro processo di sincronizzazione non la fa fallire; (b) un'append interrotta (VPN caduta a metà riga) lascia una coda
/// incompleta in current.ndjson. Prima di questa correzione ogni Append successivo veniva rifiutato per sempre. Ora la coda
/// viene riparata SOLO se tutte le righe precedenti sono valide: la coda viene salvata in un file laterale e troncata.
/// </summary>
public sealed class PhaseDMasterJournalTailRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_TailRepair_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_TailRepair_Local_" + Guid.NewGuid().ToString("N"));

    public PhaseDMasterJournalTailRepairTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_local);
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDeleteDir(_root);
        TryDeleteDir(_local);
    }

    private PhaseDMasterRepositoryEngine<Funzionalita> Engine() =>
        new(_root, "funzionalita", localBaseDirectory: _local);

    private string Current() =>
        PhaseDMasterJournalService.CurrentPath(_root, DeviceIdentityService.GetOrCreate());

    private static string[] Laterali(string current) =>
        Directory.GetFiles(Path.GetDirectoryName(current)!, "current.ndjson.torn-*");

    [Fact]
    public void LetturaStrict_ConUnHandleDiScritturaAperto_NonFallisce()
    {
        // ReadLinesShared (FileShare.ReadWrite|Delete) deve leggere anche mentre un altro handle tiene il file in scrittura.
        var engine = Engine();
        engine.SavePatch(new List<Funzionalita> { new() { Uid = Guid.NewGuid(), Nome = "FUN-LETTURA" } }, new List<Funzionalita>());
        var current = Current();

        using var altroHandle = new FileStream(current, FileMode.Open, FileAccess.Write, FileShare.Read);

        Assert.True(PhaseDMasterJournalService.TryReadBatchesStrict(current, DeviceIdentityService.GetOrCreate(), out var batches));
        Assert.Single(batches);
    }

    [Fact]
    public void CodaTroncataDopoRigaValida_VieneSpostataENonRifiutaPiuLAppend()
    {
        var engine = Engine();
        engine.SavePatch(new List<Funzionalita> { new() { Uid = Guid.NewGuid(), Nome = "FUN-PRIMA" } }, new List<Funzionalita>());
        var current = Current();
        var rigaValida = File.ReadAllText(current);
        // Append interrotta: la coda non è terminata da newline e non è un batch completo.
        File.AppendAllText(current, "{\"SchemaVersion\":1,\"DeviceId\":\"abc");

        var ex = Record.Exception(() => engine.SavePatch(
            new List<Funzionalita> { new() { Uid = Guid.NewGuid(), Nome = "FUN-DOPO" } }, new List<Funzionalita>()));

        Assert.Null(ex);
        var laterale = Assert.Single(Laterali(current));
        Assert.Contains("{\"SchemaVersion\":1,\"DeviceId\":\"abc", File.ReadAllText(laterale));
        Assert.StartsWith(rigaValida, File.ReadAllText(current));
        Assert.Equal(2, engine.Load().Items.Count);
    }

    [Fact]
    public void RigaIntermediaCorrotta_NonVieneRiparataEIlJournalRestaInvariato()
    {
        // Riparare solo la coda: un errore dentro il file (riga intermedia) resta bloccante e il file non si tocca.
        var engine = Engine();
        engine.SavePatch(new List<Funzionalita> { new() { Uid = Guid.NewGuid(), Nome = "FUN-A" } }, new List<Funzionalita>());
        var current = Current();
        var rigaValida = File.ReadAllText(current);
        File.WriteAllText(current, "{riga-rotta}\n" + rigaValida + "{\"coda");
        var prima = File.ReadAllText(current);

        Assert.ThrowsAny<Exception>(() => engine.SavePatch(
            new List<Funzionalita> { new() { Uid = Guid.NewGuid(), Nome = "FUN-B" } }, new List<Funzionalita>()));

        Assert.Equal(prima, File.ReadAllText(current));
        Assert.Empty(Laterali(current));
    }

    [Fact]
    public void Append_MentreUnAltroProcessoScriveUnaRiga_NonTroncaLaRigaInScrittura()
    {
        // Regressione (review 6.7.1, HIGH): il lock in-process (Gate) non basta fra processi diversi. Un altro writer che tiene il
        // mutex nominato del journal e ha una riga ancora incompleta non deve vedere quella coda riparata/troncata dall'append.
        var engine = Engine();
        engine.SavePatch(new List<Funzionalita> { new() { Uid = Guid.NewGuid(), Nome = "FUN-UNO" } }, new List<Funzionalita>());
        engine.SavePatch(new List<Funzionalita> { new() { Uid = Guid.NewGuid(), Nome = "FUN-DUE" } }, new List<Funzionalita>());
        var current = Current();
        var nl = Environment.NewLine;
        var righe = File.ReadAllLines(current);
        Assert.Equal(2, righe.Length);
        var prefisso = righe[0] + nl;
        var rigaInScrittura = righe[1] + nl;
        var meta = rigaInScrittura.Length / 2;
        File.WriteAllText(current, prefisso);

        var pronto = new ManualResetEventSlim(false);
        var rilascia = new ManualResetEventSlim(false);
        var altroWriter = Task.Run(() =>
        {
            using var mutex = new Mutex(false, PhaseDMasterJournalService.NomeMutexJournal(current));
            Assert.True(mutex.WaitOne(TimeSpan.FromSeconds(10)));
            try
            {
                File.AppendAllText(current, rigaInScrittura[..meta]);
                pronto.Set();
                rilascia.Wait(TimeSpan.FromSeconds(20));
                File.AppendAllText(current, rigaInScrittura[meta..]);
            }
            finally { mutex.ReleaseMutex(); }
        });
        Assert.True(pronto.Wait(TimeSpan.FromSeconds(10)));

        var append = Task.Run(() => engine.SavePatch(
            new List<Funzionalita> { new() { Uid = Guid.NewGuid(), Nome = "FUN-TRE" } }, new List<Funzionalita>()));
        Assert.False(append.Wait(TimeSpan.FromMilliseconds(600)), "l'append deve attendere il mutex del journal");
        Assert.Equal(prefisso + rigaInScrittura[..meta], File.ReadAllText(current));

        rilascia.Set();
        altroWriter.Wait(TimeSpan.FromSeconds(20));
        append.Wait(TimeSpan.FromSeconds(20));

        Assert.Empty(Laterali(current));
        Assert.Equal(3, engine.Load().Items.Count);
    }

    [Fact]
    public void Riparazione_MantieniSoloIDieciLateraliPiuRecenti()
    {
        var engine = Engine();
        engine.SavePatch(new List<Funzionalita> { new() { Uid = Guid.NewGuid(), Nome = "FUN-PRIMA" } }, new List<Funzionalita>());
        var current = Current();
        // Undici laterali vecchi (timestamp fittizi a larghezza fissa, come quelli reali): 001 e 002 devono sparire.
        for (var i = 1; i <= 11; i++)
            File.WriteAllText(current + ".torn-20000101000000" + i.ToString("D3"), "vecchio");
        File.AppendAllText(current, "{\"coda");

        engine.SavePatch(new List<Funzionalita> { new() { Uid = Guid.NewGuid(), Nome = "FUN-DOPO" } }, new List<Funzionalita>());

        var rimasti = Laterali(current).Select(x => Path.GetFileName(x)).ToList();
        Assert.Equal(PhaseDMasterJournalService.TornConservati, rimasti.Count);
        Assert.DoesNotContain(rimasti, n => n!.EndsWith("20000101000000001", StringComparison.Ordinal));
        Assert.DoesNotContain(rimasti, n => n!.EndsWith("20000101000000002", StringComparison.Ordinal));
        Assert.Contains(rimasti, n => n!.EndsWith("20000101000000003", StringComparison.Ordinal));
        Assert.Equal(2, engine.Load().Items.Count);
    }

    private static void TryDeleteDir(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
