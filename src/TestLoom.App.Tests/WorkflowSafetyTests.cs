using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class WorkflowSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_tests_" + Guid.NewGuid().ToString("N"));
    private DataPaths Paths => new(_root);

    [Fact]
    public void SalvataggioConcorrente_NonSovrascriveUnaRevisionePiuRecente()
    {
        Paths.EnsureStructure();
        var repo = new TestCaseRepository(Paths);
        var originale = new TestCase { Id = "TC-001", Componente = "ECM", Veicolo = "Daily", Descrizione = "base" };
        repo.Save(originale, true);

        var locale = Clone(originale);
        var remoto = Clone(originale);
        remoto.Descrizione = "modifica remota";
        repo.Save(remoto, false, remoto.RevisioneDati);

        locale.Descrizione = "modifica locale obsoleta";
        Assert.Throws<ConflittoRevisioneException>(() => repo.Save(locale, false, locale.RevisioneDati));
        Assert.Equal("modifica remota", JsonIo.TryRead<TestCase>(repo.PercorsoPer(remoto), out _)!.Descrizione);
    }

    [Fact]
    public void TestCaseDiventatoCorrotto_MantieneUltimaCopiaValidaESegnalaIlProblema()
    {
        Paths.EnsureStructure();
        var repo = new TestCaseRepository(Paths);
        var test = new TestCase { Id = "TC-CORROTTO", Componente = "ECM", Descrizione = "valido" };
        repo.Save(test, true);
        repo.RefreshIndex();
        var path = repo.PercorsoPer(test);

        File.WriteAllText(path, "{ json non valido");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        OltrepassaLaTolleranza(() => repo.RefreshIndex());

        var cached = repo.GetById(test.Id);
        Assert.NotNull(cached);
        Assert.Equal("valido", cached!.Descrizione);
        Assert.Contains(repo.FileConProblemi, f => f.Percorso == path);
        Assert.Throws<InvalidDataException>(() => repo.Save(cached, false, cached.RevisioneDati));
    }

    [Fact]
    public void TestCaseSuFileCorrotto_NonVieneMaiSovrascritto()
    {
        Paths.EnsureStructure();
        var repo = new TestCaseRepository(Paths);
        var test = new TestCase { Id = "TC-PROTETTO", Componente = "ECM" };
        repo.Save(test, true);
        var path = repo.PercorsoPer(test);
        const string corrotto = "{ contenuto da preservare";
        File.WriteAllText(path, corrotto);

        Assert.Throws<InvalidDataException>(() => repo.Save(test, false, test.RevisioneDati));
        Assert.Equal(corrotto, File.ReadAllText(path));
    }

    [Fact]
    public void NonConformitaDiventataCorrotta_MantieneUltimaCopiaValida()
    {
        Paths.EnsureStructure();
        var repo = new NonConformitaRepository(Paths);
        var nc = new NonConformita { Id = "NC-0001" };
        repo.Save(nc);
        repo.RefreshIndex();
        var path = Paths.NonConformitaFile(nc.Id);

        File.WriteAllText(path, "{ json non valido");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        OltrepassaLaTolleranza(() => repo.RefreshIndex());

        var cached = repo.GetById(nc.Id);
        Assert.NotNull(cached);
        Assert.Equal(nc.Uid, cached!.Uid);
        Assert.Contains(repo.FileConProblemi, f => f.Percorso == path);
        Assert.Throws<InvalidDataException>(() => repo.Save(cached, cached.RevisioneDati));
    }

    [Fact]
    public void CreazioneConIdNonConformitaGiaEsistente_NonSovrascriveIlFile()
    {
        Paths.EnsureStructure();
        var repo = new NonConformitaRepository(Paths);
        repo.Save(new NonConformita { Id = "NC-0001", Descrizione = "prima" });

        Assert.Throws<IOException>(() => repo.Save(new NonConformita { Id = "NC-0001", Descrizione = "seconda" }));
        Assert.Equal("prima", JsonIo.TryRead<NonConformita>(Paths.NonConformitaFile("NC-0001"), out _)!.Descrizione);
    }

    [Fact]
    public void CreazioneSegnalazioneGiaEsistente_NonSovrascriveIlFile()
    {
        Paths.EnsureStructure();
        var repo = new SegnalazioneRepository(Paths);
        var originale = new SegnalazioneDaClassificare { TestId = "TC-ORIGINALE" };
        repo.Save(originale);
        var duplicata = new SegnalazioneDaClassificare { Uid = originale.Uid, TestId = "TC-DIVERSO" };

        Assert.Throws<IOException>(() => repo.Save(duplicata));
        Assert.Equal("TC-ORIGINALE", JsonIo.TryRead<SegnalazioneDaClassificare>(Paths.SegnalazioneFile(originale.Uid), out _)!.TestId);
    }

    [Fact]
    public void ScritturaAtomicaFallita_NonLasciaFileTemporanei()
    {
        Paths.EnsureStructure();
        var path = Path.Combine(_root, "dato.json");
        JsonIo.WriteAtomic(path, new { Valore = 1 });

        Assert.Throws<IOException>(() => JsonIo.WriteAtomic(path, new { Valore = 2 }, overwrite: false));
        Assert.Empty(Directory.EnumerateFiles(_root, "dato.json.tmp*"));
    }

    [Fact]
    public void SalvataggioFisicoFallito_NonIncrementaLaRevisioneInMemoria()
    {
        Paths.EnsureStructure();
        var repo = new TestCaseRepository(Paths);
        var test = new TestCase { Id = "TC-BLOCCATO", Componente = "ECM", RevisioneDati = 0 };
        var destinazione = Paths.TestCaseFileGerarchico(test, Array.Empty<Veicolo>());
        Directory.CreateDirectory(destinazione); // forza il fallimento del rename file -> destinazione

        Assert.ThrowsAny<IOException>(() => repo.Save(test, true));
        Assert.Equal(0, test.RevisioneDati);
    }

    [Fact]
    public void SegnalazioneConcorrente_NonSovrascriveUnaClassificazionePiuRecente()
    {
        Paths.EnsureStructure();
        var repo = new SegnalazioneRepository(Paths);
        var originale = new SegnalazioneDaClassificare { TestId = "TC-004" };
        repo.Save(originale);

        var locale = Clone(originale);
        var remoto = Clone(originale);
        remoto.Stato = StatoSegnalazione.Classificata;
        repo.Save(remoto, remoto.RevisioneDati);

        locale.Stato = StatoSegnalazione.Esclusa;
        Assert.Throws<ConflittoRevisioneException>(() => repo.Save(locale, locale.RevisioneDati));
        Assert.Equal(StatoSegnalazione.Classificata, JsonIo.TryRead<SegnalazioneDaClassificare>(Paths.SegnalazioneFile(originale.Uid), out _)!.Stato);
    }

    [Fact]
    public void AllegatoConContenutoIdentico_VieneRiconosciutoAncheConNomeDiverso()
    {
        Paths.EnsureStructure();
        var tc = new TestCase { Id = "TC-002", Componente = "ECM" };
        var a = Path.Combine(_root, "a.log"); var b = Path.Combine(_root, "b.log");
        File.WriteAllText(a, "trace identica"); File.WriteAllText(b, "trace identica");
        tc.Allegati.Add(AttachmentService.Salva(Paths, tc, a, "Paolo"));
        Assert.Throws<InvalidOperationException>(() => AttachmentService.Salva(Paths, tc, b, "Paolo"));
    }

    [Fact]
    public void PerFunzionalita_UsaLEsitoDellUltimaEsecuzioneDelTestCollegato()
    {
        var funzione = new Funzionalita { Nome = "Fault memory", Categorie = new() { "Diagnostica" } };
        var test = new TestCase
        {
            Id = "TC-003", Funzionalita = new List<string> { funzione.Nome },
            Esecuzioni = new() { new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.Conforme, Condizioni = new() { new Condizione { Esito = Esito.Conforme } } } }
        };
        var bucket = CoverageService.PerFunzionalita(new[] { funzione }, new[] { test });
        Assert.Equal(1, bucket.Conformi);
        Assert.Equal(0, bucket.NonTestati);
    }

    [Fact]
    public void CommitLedger_TransactionStranieraBloccaPubblicazioneReceipt()
    {
        Paths.EnsureStructure();
        var transactions = Path.Combine(_root, "_ditm", "transactions");
        Directory.CreateDirectory(transactions);
        File.WriteAllText(Path.Combine(transactions, Guid.NewGuid().ToString("N") + ".pending.json"), "{broken");
        var receipt = new WorkflowTransactionCommitReceipt
        {
            Id = Guid.NewGuid(),
            DeviceId = "device-test",
            StartedUtc = DateTime.UtcNow.AddSeconds(-1),
            CompletedUtc = DateTime.UtcNow,
            Entries = new List<WorkflowTransactionCommitEntry>
            {
                new() { RelativePath = Path.Combine("testcases", "testcase_fake.json"), FinalSha256 = "ABC", LogicallyDeleted = false }
            }
        };

        Assert.Throws<IOException>(() => WorkflowCommitLedgerService.Publish(Paths, receipt));
        Assert.False(Directory.Exists(Path.Combine(transactions, "committed", "device-test")));
    }

    [Fact]
    public void CommitLedger_CacheMissConStessoStamp_RileggeLaReplicaPrimaDiDichiararePending()
    {
        Paths.EnsureStructure();
        const string device = "device-cache-stamp";
        var started = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
        var completed = started.AddSeconds(1);
        var first = new WorkflowTransactionCommitReceipt
        {
            Id = Guid.NewGuid(), DeviceId = device, StartedUtc = started, CompletedUtc = completed,
            Entries = new List<WorkflowTransactionCommitEntry>
            {
                new() { RelativePath = Path.Combine("testcases", "testcase_cache.json"), FinalSha256 = "ABCDEF", LogicallyDeleted = false }
            }
        };
        var target = new WorkflowTransactionCommitReceipt
        {
            Id = Guid.NewGuid(), DeviceId = device, StartedUtc = started, CompletedUtc = completed,
            Entries = new List<WorkflowTransactionCommitEntry>
            {
                new() { RelativePath = Path.Combine("testcases", "testcase_cache.json"), FinalSha256 = "ABCDEF", LogicallyDeleted = false }
            }
        };

        var dir = Path.Combine(_root, "_ditm", "transactions", "committed", device);
        Directory.CreateDirectory(dir);
        var current = Path.Combine(dir, "current.ndjson");
        var firstBytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(first, JsonIo.CompactOptions) + Environment.NewLine);
        var targetBytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(target, JsonIo.CompactOptions) + Environment.NewLine);
        Assert.Equal(firstBytes.Length, targetBytes.Length); // stessa size: il fast-path non può distinguerli.
        File.WriteAllBytes(current, firstBytes);
        var originalStamp = File.GetLastWriteTimeUtc(current);

        // Popola la cache con una mappa che NON contiene target.Id.
        Assert.False(WorkflowCommitLedgerService.TryGetReceipt(Paths, device, target.Id, out _, out _));

        // Simula una replica OneDrive sostituita preservando esattamente length + mtime.
        File.WriteAllBytes(current, targetBytes);
        File.SetLastWriteTimeUtc(current, originalStamp);
        Assert.Equal(firstBytes.Length, new FileInfo(current).Length);
        Assert.Equal(originalStamp, File.GetLastWriteTimeUtc(current));

        Assert.True(WorkflowCommitLedgerService.TryGetReceipt(Paths, device, target.Id, out var found, out var error), error);
        Assert.NotNull(found);
        Assert.Equal(target.Id, found!.Id);
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonIo.Options), JsonIo.Options)!;

    /// <summary>Vedi RepositoryLastKnownGoodTests.OltrepassaLaTolleranza: un errore di lettura transitorio
    /// entra in FileConProblemi solo dopo >= 2 tentativi consecutivi e >= 2 minuti dal primo.</summary>
    private static void OltrepassaLaTolleranza(Action refresh)
    {
        var now = DateTime.UtcNow;
        FileProblemToleranceTracker.NowUtcOverrideForTests = () => now;
        refresh();
        now = now.AddMinutes(3);
        FileProblemToleranceTracker.NowUtcOverrideForTests = () => now;
        refresh();
    }

    public void Dispose()
    {
        FileProblemToleranceTracker.NowUtcOverrideForTests = null;
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
