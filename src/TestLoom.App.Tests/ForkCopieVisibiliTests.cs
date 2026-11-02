using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Fork TestCase con copie di conflitto OneDrive VISIBILI dei rami (6.6.0 — guardia di scrittura sui fork):
/// (1) la risoluzione manuale del Master non deve bloccarsi sulla copia del proprio ramo; (2) dopo una risoluzione
/// (auto o manuale) la copia di un ramo incorporato non deve bloccare i salvataggi successivi; (3) quella copia viene
/// archiviata nel recovery-bin (mai cancellata), mentre una copia di contenuto sconosciuto continua a bloccare.
/// Stesse postazioni simulate di <see cref="TestCaseAutoForkMergeServiceTests"/>: due cartelle condivise distinte.
/// </summary>
public sealed class ForkCopieVisibiliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_ForkCopie_" + Guid.NewGuid().ToString("N"));
    private readonly bool _flagOriginale = TestCaseAutoForkMergeService.AbilitaMergeAutomaticoFork;

    public void Dispose()
    {
        TestCaseAutoForkMergeService.AbilitaMergeAutomaticoFork = _flagOriginale;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Postazione
    {
        public required DataPaths Paths;
        public required TestCaseRepository Repo;
        public required string DeviceId;
    }

    private Postazione CreaPostazione(string nome, string deviceId)
    {
        var sharedRoot = Path.Combine(_root, "shared_" + nome);
        var localDir = Path.Combine(_root, "local_" + nome);
        var paths = new DataPaths(sharedRoot);
        paths.EnsureStructure();
        var store = new LocalTestIndexStore(sharedRoot, localDir);
        var repo = new TestCaseRepository(paths, store, deviceIdOverride: deviceId);
        return new Postazione { Paths = paths, Repo = repo, DeviceId = deviceId };
    }

    private static void Sincronizza(Postazione da, Postazione a, string sottocartella)
    {
        var origine = Path.Combine(da.Paths.Root, sottocartella);
        var destinazione = Path.Combine(a.Paths.Root, sottocartella);
        if (!Directory.Exists(origine)) return;
        Directory.CreateDirectory(destinazione);
        foreach (var file in Directory.EnumerateFiles(origine, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destinazione, Path.GetRelativePath(origine, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void SincronizzaBidirezionale(Postazione x, Postazione y)
    {
        foreach (var sotto in new[] { "testcases", Path.Combine("_ditm", "journal"), Path.Combine("_ditm", "conflicts") })
        {
            Sincronizza(x, y, sotto);
            Sincronizza(y, x, sotto);
        }
    }

    private static TestCase Base(string id) => new()
    {
        Id = id,
        Componente = new EcuSelection { "BCM" },
        Descrizione = "descrizione base",
        DescrizioneBreve = "breve base",
        CreatoDa = "Seed",
        ModificatoDa = "Seed"
    };

    private static string CreaBaseCondivisa(Postazione a, Postazione b, string id)
    {
        var tc = Base(id);
        a.Repo.Save(tc, isNew: true);
        SincronizzaBidirezionale(a, b);
        a.Repo.RefreshIndex();
        b.Repo.RefreshIndex();
        return tc.Id;
    }

    /// <summary>Due rami divergenti dalla stessa base (campi diversi), sincronizzati e rilevati come fork su entrambe.</summary>
    private static void CreaForkDueRami(Postazione a, Postazione b, string id, Action<TestCase> mutaA, Action<TestCase> mutaB)
    {
        var tcA = a.Repo.LoadCurrent(a.Repo.GetById(id)!)!;
        mutaA(tcA);
        a.Repo.Save(tcA, isNew: false, revisioneAttesa: 1);

        var tcB = b.Repo.LoadCurrent(b.Repo.GetById(id)!)!;
        mutaB(tcB);
        b.Repo.Save(tcB, isNew: false, revisioneAttesa: 1);

        SincronizzaBidirezionale(a, b);
        a.Repo.RefreshIndex();
        b.Repo.RefreshIndex();
        a.Repo.RefreshJournalMetadata();
        b.Repo.RefreshJournalMetadata();
        SincronizzaBidirezionale(a, b);
        a.Repo.RefreshJournalMetadata();
        b.Repo.RefreshJournalMetadata();
    }

    /// <summary>Fork aperto visto da A, con la copia di recovery di ogni ramo caricata come farebbe il view model.</summary>
    private static (JournalForkConflict Fork, ForkRecoveryLoadResult Recovery, TestCase Current) CaricaFork(Postazione a)
    {
        a.Repo.RefreshJournalMetadata();
        var fork = Assert.Single(a.Repo.JournalForks);
        var copie = a.Repo.GetAll(includeEliminati: true).Where(t => t.Uid == fork.EntityUid).ToList();
        var cached = copie.Where(t => !t.Eliminato).OrderByDescending(t => t.RevisioneDati).First();
        var current = a.Repo.LoadCurrent(cached)!;
        var recovery = DelayedForkRecoveryService.LoadAllBranches(a.Paths, fork, current, copie);
        Assert.True(recovery.Complete, "Entrambi i rami devono essere recuperabili prima della risoluzione.");
        return (fork, recovery, current);
    }

    /// <summary>Il ramo il cui contenuto NON è quello del canonico (quale dei due PC abbia vinto il file dipende dall'ordine
    /// della sincronizzazione): solo la sua copia OneDrive è divergente dal canonico e quindi bloccherebbe il salvataggio.</summary>
    private static TestCase RamoNonCanonico(ForkRecoveryLoadResult recovery, TestCase current)
    {
        var hashCanonico = TestCaseFingerprint.Compute(current);
        return recovery.Available.Single(b => !TestCaseFingerprint.Compute(b.TestCase).Equals(hashCanonico, StringComparison.OrdinalIgnoreCase)).TestCase;
    }

    /// <summary>Copia di conflitto OneDrive accanto al canonico, con il contenuto indicato (come la scrive OneDrive).</summary>
    private static string ScriviCopiaOneDrive(Postazione a, TestCase canonico, string suffisso, TestCase contenuto)
    {
        var canonicalPath = a.Repo.FilePathOf(canonico);
        var copia = canonicalPath.Replace(".json", $" ({suffisso}).json", StringComparison.OrdinalIgnoreCase);
        JsonIo.WriteAtomic(copia, contenuto);
        return copia;
    }

    /// <summary>Il Master sceglie sempre la versione "mia" (corrente) per ogni campo in conflitto: il fork manuale
    /// senza base comune marca come conflitto ogni campo diverso fra i due rami.</summary>
    private static Task<IReadOnlyDictionary<string, SceltaMerge>?> SceltaMasterMia(IReadOnlyList<CampoMergeTestCase> conflitti) =>
        Task.FromResult<IReadOnlyDictionary<string, SceltaMerge>?>(conflitti.ToDictionary(c => c.Nome, _ => SceltaMerge.Mia));

    [Fact]
    public void LoadAllBranches_PayloadIlleggibile_RestaMissingERegistraErrore()
    {
        var uid = Guid.NewGuid();
        var eventId = Guid.NewGuid().ToString("N");
        var current = Base("TC-RECOVERY-CORROTTO");
        current.Uid = uid;
        var expected = Base("TC-RECOVERY-CORROTTO");
        expected.Uid = uid;
        expected.Descrizione = "ramo recuperato";
        var fork = new JournalForkConflict
        {
            EntityUid = uid,
            EntityId = current.Id,
            Branches =
            [
                new TestJournalEvent
                {
                    EventId = eventId,
                    EntityUid = uid,
                    NewHash = TestCaseFingerprint.Compute(expected)
                }
            ]
        };
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var recoveryDir = Path.Combine(paths.Root, "_ditm", "conflicts", uid.ToString("N"));
        Directory.CreateDirectory(recoveryDir);
        File.WriteAllBytes(Path.Combine(recoveryDir, eventId + ".json.gz"), [1, 2, 3]);

        var result = DelayedForkRecoveryService.LoadAllBranches(paths, fork, current);

        Assert.False(result.Complete);
        Assert.DoesNotContain(result.Available, branch => branch.Event.EventId == eventId);
        Assert.Same(fork.Branches[0], Assert.Single(result.Missing));
        var testoLog = File.ReadAllText(Path.Combine(CrashLogService.LogDirectory, "crash.log"));
        Assert.Contains("Recupero fork: payload non leggibile", testoLog);
    }

    [Fact]
    public async Task RisoluzioneManuale_CopiaVisibileDelPropriRamo_NonBloccaLaRisoluzione()
    {
        var a = CreaPostazione("A", "device-aaaa");
        var b = CreaPostazione("B", "device-bbbb");
        var id = CreaBaseCondivisa(a, b, "TC-FORK-MAN-01");
        CreaForkDueRami(a, b, id, mutaA: tc => tc.Descrizione = "modifica ramo A", mutaB: tc => tc.DescrizioneBreve = "modifica ramo B");
        var (fork, recovery, current) = CaricaFork(a);
        ScriviCopiaOneDrive(a, current, "device-aaaa", RamoNonCanonico(recovery, current));

        var risultato = await TestCaseManualForkResolution.RisolviAsync(
            a.Repo, fork, current, recovery, "Master", SceltaMasterMia);

        Assert.NotNull(risultato);
        var salvato = a.Repo.LoadCurrent(a.Repo.GetById(id)!)!;
        Assert.Equal(current.RevisioneDati + 1, salvato.RevisioneDati);
        Assert.NotEmpty(TestCaseForkResolution.GetResolvedKeys(salvato));
        Assert.Contains(salvato.Changelog, c => c.Campo == "conflitto sincronizzazione OneDrive");
    }

    [Fact]
    public async Task RisoluzioneManuale_CopiaDiContenutoSconosciuto_BloccaETuttoResteInvariato()
    {
        var a = CreaPostazione("A", "device-aaaa");
        var b = CreaPostazione("B", "device-bbbb");
        var id = CreaBaseCondivisa(a, b, "TC-FORK-MAN-02");
        CreaForkDueRami(a, b, id, mutaA: tc => tc.Descrizione = "modifica ramo A", mutaB: tc => tc.DescrizioneBreve = "modifica ramo B");
        var (fork, recovery, current) = CaricaFork(a);
        var sconosciuta = ScriviCopiaOneDrive(a, current, "PC-ignoto", new TestCase
        {
            Uid = current.Uid, Id = id, Descrizione = "versione mai vista", CreatoDa = "Seed", ModificatoDa = "Altro"
        });
        var prima = File.ReadAllBytes(a.Repo.FilePathOf(current));
        var copiaPrima = File.ReadAllBytes(sconosciuta);

        await Assert.ThrowsAsync<IOException>(() => TestCaseManualForkResolution.RisolviAsync(
            a.Repo, fork, current, recovery, "Master", SceltaMasterMia));

        Assert.Equal(prima, File.ReadAllBytes(a.Repo.FilePathOf(current)));
        Assert.Equal(copiaPrima, File.ReadAllBytes(sconosciuta));
    }

    [Fact]
    public void MergeAutomatico_CopiaVisibileDiUnRamo_NonBloccaIlSalvataggioSuccessivo()
    {
        var a = CreaPostazione("A", "device-aaaa");
        var b = CreaPostazione("B", "device-bbbb");
        var id = CreaBaseCondivisa(a, b, "TC-FORK-AUT-01");
        CreaForkDueRami(a, b, id, mutaA: tc => tc.Descrizione = "modifica ramo A", mutaB: tc => tc.DescrizioneBreve = "modifica ramo B");
        var (_, recovery, current) = CaricaFork(a);
        ScriviCopiaOneDrive(a, current, "device-bbbb", RamoNonCanonico(recovery, current));

        Assert.Equal(1, TestCaseAutoForkMergeService.EseguiSuForkNoti(a.Paths, a.Repo).Applicati);

        var successiva = a.Repo.LoadCurrent(a.Repo.GetById(id)!)!;
        successiva.Descrizione = "modifica successiva alla risoluzione";
        a.Repo.Save(successiva, isNew: false, revisioneAttesa: successiva.RevisioneDati);

        Assert.Equal("modifica successiva alla risoluzione", a.Repo.LoadCurrent(a.Repo.GetById(id)!)!.Descrizione);
    }

    [Fact]
    public void MergeAutomatico_CopiaVisibileDiUnRamo_VieneArchiviataNelRecoveryBinSenzaCancellazioneDefinitiva()
    {
        var a = CreaPostazione("A", "device-aaaa");
        var b = CreaPostazione("B", "device-bbbb");
        var id = CreaBaseCondivisa(a, b, "TC-FORK-AUT-02");
        CreaForkDueRami(a, b, id, mutaA: tc => tc.Descrizione = "modifica ramo A", mutaB: tc => tc.DescrizioneBreve = "modifica ramo B");
        var (_, recovery, current) = CaricaFork(a);
        var copia = ScriviCopiaOneDrive(a, current, "device-bbbb", RamoNonCanonico(recovery, current));

        Assert.Equal(1, TestCaseAutoForkMergeService.EseguiSuForkNoti(a.Paths, a.Repo).Applicati);

        Assert.True(!File.Exists(copia) || WorkflowTransactionVisibilityService.IsCommittedTombstone(a.Paths, copia),
            "La copia del ramo incorporato deve uscire dalla cartella condivisa (resta solo il tombstone TestLoom).");
        Assert.Contains(DomainRecoveryService.GetAll(a.Paths),
            r => r.Category == DomainRecoveryCategory.TestCase && r.OriginalFileName == Path.GetFileName(copia));
    }

    [Fact]
    public void DopoRisoluzione_CopiaTardivaDiUnRamoChiuso_NonBloccaIlSalvataggio()
    {
        var a = CreaPostazione("A", "device-aaaa");
        var b = CreaPostazione("B", "device-bbbb");
        var id = CreaBaseCondivisa(a, b, "TC-FORK-AUT-03");
        CreaForkDueRami(a, b, id, mutaA: tc => tc.Descrizione = "modifica ramo A", mutaB: tc => tc.DescrizioneBreve = "modifica ramo B");
        var (_, recovery, current) = CaricaFork(a);
        Assert.Equal(1, TestCaseAutoForkMergeService.EseguiSuForkNoti(a.Paths, a.Repo).Applicati);

        // La copia del ramo B arriva DOPO la risoluzione (sincronizzazione OneDrive in ritardo).
        ScriviCopiaOneDrive(a, current, "device-bbbb", RamoNonCanonico(recovery, current));
        a.Repo.RefreshJournalMetadata();

        var successiva = a.Repo.LoadCurrent(a.Repo.GetById(id)!)!;
        successiva.Descrizione = "modifica dopo copia tardiva";
        a.Repo.Save(successiva, isNew: false, revisioneAttesa: successiva.RevisioneDati);

        Assert.Equal("modifica dopo copia tardiva", a.Repo.LoadCurrent(a.Repo.GetById(id)!)!.Descrizione);
    }
}
