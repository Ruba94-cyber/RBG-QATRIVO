using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

public class TestUidIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "testloom-testuid-" + Guid.NewGuid().ToString("N"));
    private DataPaths Paths => new(_root);
    public TestUidIdentityTests()
    {
        Directory.CreateDirectory(_root);
        JsonIo.WriteAtomic(Paths.VersionManifestFile, new VersionManifest { SchemaDatiMassimo = 10 });
    }

    private static (TestCase Test, RiferimentoProva Proof) Fixture(string code = "T-RP-0001")
    {
        var c = new Condizione { Esito = Esito.Conforme };
        var t = new TestCase { Id = code, Esecuzioni = new() { new Esecuzione { Iterazione = 1, Condizioni = new() { c } } } };
        return (t, new RiferimentoProva { TestId = code, CondizioneUid = c.Uid, EsecuzioneIterazione = 1 });
    }

    [Fact]
    public void UidMismatch_NeverFallsBackToSameCodeOrCondition()
    {
        var (t, p) = Fixture();
        p.TestUid = Guid.NewGuid();
        var nc = new NonConformita { TestCaseId = t.Id, TestUid = p.TestUid, ProveCollegate = new() { p } };
        Assert.False(AnomaliaWorkflowService.AppartieneAlTest(nc, t.Id, t));
        Assert.Null(AnomaliaWorkflowService.RisolviProva(t, p));
        Assert.Null(AnomaliaWorkflowService.TestCorrente(nc, new[] { t }));
        Assert.False(AnomaliaWorkflowService.PuoChiudersiPerConformita(nc, new[] { t }));
        p.TestUid = null; // La prova legacy eredita l'identità primaria nota, mai il codice riusato.
        Assert.False(AnomaliaWorkflowService.AppartieneAlTest(nc, t.Id, t));
        Assert.Null(AnomaliaWorkflowService.TestCorrente(nc, new[] { t }));
        Assert.False(AnomaliaWorkflowService.PuoChiudersiPerConformita(nc, new[] { t }));
    }

    [Fact]
    public void SameUid_WithChangedCodeStillMatchesAndCloses()
    {
        var (t, p) = Fixture();
        p.TestUid = t.Uid;
        t.Id = "NEW-CODE";
        var nc = new NonConformita { TestCaseId = "OLD-CODE", TestUid = t.Uid, ProveCollegate = new() { p } };
        Assert.True(AnomaliaWorkflowService.AppartieneAlTest(nc, t.Id, t));
        Assert.Equal(t.Uid, AnomaliaWorkflowService.TestCorrente(nc, new[] { t })?.Uid);
        Assert.True(AnomaliaWorkflowService.PuoChiudersiPerConformita(nc, new[] { t }));
        Assert.Single(AnomaliaWorkflowService.PerTest(t.Id, new[] { nc }, _ => t));
        Assert.Single(AnomaliaWorkflowService.IndicizzaPerTest(new[] { nc }, _ => null, _ => t)[t.Id]);
    }

    [Fact]
    public void LegacyRequiresActualNonemptyResolvableCondition_NoDateHeuristic()
    {
        var (t, p) = Fixture();
        var nc = new NonConformita { TestCaseId = t.Id, ProveCollegate = new() { p }, DataApertura = DateTime.MaxValue };
        Assert.True(AnomaliaWorkflowService.AppartieneAlTest(nc, t.Id, t));
        p.CondizioneUid = Guid.NewGuid();
        Assert.False(AnomaliaWorkflowService.AppartieneAlTest(nc, t.Id, t));
        Assert.False(AnomaliaWorkflowService.PuoChiudersiPerConformita(nc, new[] { t }));
        nc.ProveCollegate.Clear();
        Assert.Null(AnomaliaWorkflowService.TestCorrente(nc, new[] { t }));
    }

    [Fact]
    public void RoundtripClone_PreservesUidUnknownFieldsAndOmitsNull()
    {
        var nc = JsonSerializer.Deserialize<NonConformita>("""
            {"Id":"NC-1","future":{"x":2},"ProveCollegate":[{"TestId":"T","nestedFuture":[1]}]}
            """, JsonIo.Options)!;
        Assert.DoesNotContain("TestUid", JsonSerializer.Serialize(nc, JsonIo.Options));
        nc.TestUid = Guid.NewGuid();
        nc.ProveCollegate[0].TestUid = nc.TestUid;
        var clone = nc.Clona();
        Assert.Equal(nc.TestUid, clone.TestUid);
        Assert.Equal(nc.TestUid, clone.ProveCollegate[0].TestUid);
        clone.ProveCollegate[0].ExtensionData!.Clear();
        Assert.Single(nc.ProveCollegate[0].ExtensionData!);
        var roundtrip = EntityStateFingerprint.Clone(nc);
        Assert.Equal(2, roundtrip.ExtensionData!["future"].GetProperty("x").GetInt32());
        Assert.Single(roundtrip.ProveCollegate[0].ExtensionData!);
        var signal = JsonSerializer.Deserialize<SegnalazioneDaClassificare>("{\"future\":7}", JsonIo.Options)!;
        Assert.DoesNotContain("TestUid", JsonSerializer.Serialize(signal, JsonIo.Options));
        signal.TestUid = nc.TestUid;
        Assert.Equal(7, EntityStateFingerprint.Clone(signal).ExtensionData!["future"].GetInt32());
    }

    private NonConformitaRepository Repo() => new(Paths, localIndex: new LocalNonConformitaIndexStore(_root, Path.Combine(_root, "local")));

    [Fact]
    public void RepositorySqlite_ChangedCodeAndOverlayMatchUid_RejectReusedCode()
    {
        var (a, _) = Fixture("A");
        var (b, _) = Fixture("B");
        var repo = Repo();
        repo.TestResolver = code => code == "A" ? a : code == "B" ? b : null;
        var nc = new NonConformita { Id = "NC-0001", TestCaseId = "OLD", TestUid = a.Uid };
        repo.Save(nc);
        repo.RefreshIndex();
        Assert.Single(repo.GetByTestIds(new[] { "A" }));
        Assert.Empty(repo.GetByTestIds(new[] { "B" }));
        var overlay = nc.Clona();
        overlay.TestCaseId = "A"; overlay.TestUid = b.Uid;
        using (WorkflowWorkingCopyScope.Begin(new[] { overlay }))
        {
            Assert.Empty(repo.GetByTestIds(new[] { "A" }));
            Assert.Single(repo.GetByTestIds(new[] { "B" }));
        }
        Assert.Single(repo.GetByTestIds(new[] { "A" }));
    }

    [Fact]
    public void SqliteSchemaUpgrade_RebuildsLinksFromCachedJsonAtomically()
    {
        var store = new LocalNonConformitaIndexStore(_root, Path.Combine(_root, "local"));
        var nc = new NonConformita { Id = "NC-1", TestCaseId = "T", TestUid = Guid.NewGuid() };
        store.Upsert("path", DateTime.UtcNow, 1, nc);
        using (var conn = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            conn.Open(); using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE nc_test_link; UPDATE meta SET value='2' WHERE key='schema_version';";
            cmd.ExecuteNonQuery();
        }
        var upgraded = new LocalNonConformitaIndexStore(_root, Path.Combine(_root, "local"));
        Assert.Equal("path", Assert.Single(upgraded.QueryTestLinkPaths(Array.Empty<string>(), new[] { nc.TestUid!.Value })));
    }

    [Fact]
    public void UidWritesFailClosedUntilManifestSchemaConfirmed()
    {
        var repo = Repo();
        JsonIo.WriteAtomic(Paths.VersionManifestFile, new VersionManifest { SchemaDatiMassimo = 9 });
        Assert.Throws<IOException>(() => repo.Save(new NonConformita { Id = "NC-0001", TestUid = Guid.NewGuid() }));
        Assert.False(File.Exists(Paths.NonConformitaFile("NC-0001")));
    }

    private (TestCase Test, NonConformita Nc) MigrationFixture()
    {
        var (test, proof) = Fixture();
        Directory.CreateDirectory(Paths.TestCaseDir("Componente"));
        JsonIo.WriteAtomic(Paths.TestCaseFile("Componente", test.Id), test);
        var nc = new NonConformita { Id = "NC-0001", TestCaseId = test.Id, ProveCollegate = new() { proof },
            Descrizione = "Problema da conservare", StatoWorkflow = StatoAnomalia.Conclusa, MotivoChiusura = "manuale" };
        Repo().Save(nc);
        return (test, nc);
    }

    [Fact]
    public void MigrationDryRunPure_ApplyPreservesMeaningBacksUpAndIsIdempotent()
    {
        var (test, nc) = MigrationFixture();
        var bytes = File.ReadAllBytes(Paths.NonConformitaFile(nc.Id));
        var plan = TestUidMigrationService.Analyze(Paths);
        Assert.Equal(bytes, File.ReadAllBytes(Paths.NonConformitaFile(nc.Id)));
        var change = Assert.Single(plan.Changes);
        Assert.Equal(test.Uid, change.TestUid);
        var backup = TestUidMigrationService.Apply(Paths, plan, Path.Combine(_root, "migration-index"));
        Assert.NotNull(backup);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(backup!, "nonconformita", Path.GetFileName(Paths.NonConformitaFile(nc.Id)))));
        var persisted = JsonIo.TryRead<NonConformita>(Paths.NonConformitaFile(nc.Id), out _)!;
        Assert.Equal(test.Uid, persisted.TestUid);
        Assert.Equal(test.Uid, persisted.ProveCollegate[0].TestUid);
        Assert.Equal(nc.Descrizione, persisted.Descrizione);
        Assert.Equal(nc.MotivoChiusura, persisted.MotivoChiusura);
        Assert.Equal(nc.StatoWorkflow, persisted.StatoWorkflow);
        persisted.TestUid = null; persisted.ProveCollegate[0].TestUid = null;
        persisted.SchemaVersion = nc.SchemaVersion; persisted.RevisioneDati = nc.RevisioneDati;
        Assert.Equal(EntityStateFingerprint.Compute(nc), EntityStateFingerprint.Compute(persisted));
        Assert.Empty(TestUidMigrationService.Analyze(Paths).Changes);
        var appliedBytes = File.ReadAllBytes(Paths.NonConformitaFile(nc.Id));
        Assert.Null(TestUidMigrationService.Apply(Paths, plan, Path.Combine(_root, "migration-index")));
        Assert.Equal(appliedBytes, File.ReadAllBytes(Paths.NonConformitaFile(nc.Id)));
    }

    [Fact]
    public void MigrationStalePlanFailsWithoutChangingNc()
    {
        var (_, nc) = MigrationFixture();
        var plan = TestUidMigrationService.Analyze(Paths);
        var repo = Repo(); repo.RefreshIndex();
        var concurrent = Assert.Single(repo.GetAll()); concurrent.Descrizione = "modifica concorrente";
        repo.Save(concurrent, concurrent.RevisioneDati);
        var bytes = File.ReadAllBytes(Paths.NonConformitaFile(nc.Id));
        Assert.Throws<IOException>(() => TestUidMigrationService.Apply(Paths, plan, Path.Combine(_root, "migration-index")));
        Assert.Equal(bytes, File.ReadAllBytes(Paths.NonConformitaFile(nc.Id)));
    }

    [Fact]
    public void MigrationFailureAfterWrite_RollsBackExactBytes()
    {
        var (_, nc) = MigrationFixture();
        var bytes = File.ReadAllBytes(Paths.NonConformitaFile(nc.Id));
        var plan = TestUidMigrationService.Analyze(Paths);
        TestUidMigrationService.AfterWriteForTests = _ => throw new IOException("injected failure");
        try { Assert.Throws<IOException>(() => TestUidMigrationService.Apply(Paths, plan, Path.Combine(_root, "migration-index"))); }
        finally { TestUidMigrationService.AfterWriteForTests = null; }
        Assert.Equal(bytes, File.ReadAllBytes(Paths.NonConformitaFile(nc.Id)));
        Assert.Null(JsonIo.TryRead<NonConformita>(Paths.NonConformitaFile(nc.Id), out _)!.TestUid);
    }

    [Fact]
    public void MigrationAmbiguousOrphanStandalone_LeavesAllUncertainLinksUnchanged()
    {
        var (test, nc) = MigrationFixture();
        nc.ProveCollegate[0].CondizioneUid = Guid.NewGuid();
        JsonIo.WriteAtomic(Paths.NonConformitaFile(nc.Id), nc);
        var repo = Repo(); repo.RefreshIndex();
        repo.Save(new NonConformita { Id = "NC-0002", TestCaseId = "missing" });
        repo.Save(new NonConformita { Id = "NC-0003", DaSegnalazioneRete = true });
        var plan = TestUidMigrationService.Analyze(Paths);
        Assert.Empty(plan.Changes);
        Assert.Single(plan.Diagnostics, d => d.State == "ambiguous");
        Assert.Single(plan.Diagnostics, d => d.State == "orphan");
        Assert.Equal(3, plan.NonConformitaCount);
    }

    [Fact]
    public void MigrationSkipsTombstonesAndNestedBackups()
    {
        MigrationFixture();
        File.WriteAllText(Paths.NonConformitaFile("deleted"), "{\"_ditmDeleted\":true}");
        Directory.CreateDirectory(Paths.SegnalazioniDir);
        File.WriteAllText(Path.Combine(Paths.SegnalazioniDir, "segnalazione_deleted.json"), "{\"_ditmDeleted\":true}");
        Directory.CreateDirectory(Path.Combine(Paths.NonConformitaDir, "backup"));
        File.WriteAllText(Path.Combine(Paths.NonConformitaDir, "backup", "nc_bad.json"), "broken json");
        var plan = TestUidMigrationService.Analyze(Paths);
        Assert.Equal(1, plan.NonConformitaCount);
        Assert.Equal(0, plan.SegnalazioniCount);
        Assert.Single(plan.Changes);
    }

    [Fact]
    public void MigrationRejectsNoncanonicalRecordWithoutCreatingAnotherNc()
    {
        var (_, nc) = MigrationFixture();
        var source = Paths.NonConformitaFile(nc.Id);
        var renamed = Path.Combine(Paths.NonConformitaDir, "nc_manually-renamed.json");
        File.Move(source, renamed);
        var bytes = File.ReadAllBytes(renamed);
        Assert.Throws<InvalidDataException>(() => TestUidMigrationService.Analyze(Paths));
        Assert.False(File.Exists(source));
        Assert.Equal(bytes, File.ReadAllBytes(renamed));
    }

    [Fact]
    public void MigrationConcurrentNewSameCodeTest_RollsBackNcAndPreservesForeignTest()
    {
        var (test, nc) = MigrationFixture();
        var bytes = File.ReadAllBytes(Paths.NonConformitaFile(nc.Id));
        var plan = TestUidMigrationService.Analyze(Paths);
        var foreignPath = Paths.TestCaseFile("OTHER", test.Id);
        TestUidMigrationService.AfterWriteForTests = _ =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(foreignPath)!);
            File.WriteAllBytes(foreignPath, JsonSerializer.SerializeToUtf8Bytes(new TestCase { Id = test.Id }, JsonIo.Options));
        };
        try { Assert.Throws<IOException>(() => TestUidMigrationService.Apply(Paths, plan, Path.Combine(_root, "migration-index"))); }
        finally { TestUidMigrationService.AfterWriteForTests = null; }
        Assert.Equal(bytes, File.ReadAllBytes(Paths.NonConformitaFile(nc.Id)));
        Assert.True(File.Exists(foreignPath));
    }

    [Fact]
    public void MigrationConcurrentTestContentChange_RollsBackNcPreservesForeignContent()
    {
        var (test, nc) = MigrationFixture();
        var bytes = File.ReadAllBytes(Paths.NonConformitaFile(nc.Id));
        var plan = TestUidMigrationService.Analyze(Paths);
        var testPath = Paths.TestCaseFile("Componente", test.Id);
        test.DescrizioneBreve = "modifica di un altro PC";
        var foreign = JsonSerializer.SerializeToUtf8Bytes(test, JsonIo.Options);
        TestUidMigrationService.AfterWriteForTests = _ => File.WriteAllBytes(testPath, foreign);
        try { Assert.Throws<IOException>(() => TestUidMigrationService.Apply(Paths, plan, Path.Combine(_root, "migration-index"))); }
        finally { TestUidMigrationService.AfterWriteForTests = null; }
        Assert.Equal(bytes, File.ReadAllBytes(Paths.NonConformitaFile(nc.Id)));
        Assert.Equal(foreign, File.ReadAllBytes(testPath));
    }

    [Fact]
    public void SignalPersistenceAndMigration_UseUidAndPreserveFutureData()
    {
        var (test, _) = MigrationFixture();
        var p = test.Esecuzioni[0].Condizioni[0];
        var signal = new SegnalazioneDaClassificare { TestId = test.Id, CondizioneUid = p.Uid, EsecuzioneIterazione = 1,
            ExtensionData = new() { ["future"] = JsonSerializer.SerializeToElement("retained") } };
        var repo = new SegnalazioneRepository(Paths); repo.Save(signal);
        var plan = TestUidMigrationService.Analyze(Paths);
        Assert.Equal(2, plan.Changes.Count);
        TestUidMigrationService.Apply(Paths, plan, Path.Combine(_root, "migration-index"));
        repo.RefreshIndex();
        var persisted = repo.Get(signal.Uid)!;
        Assert.Equal(test.Uid, persisted.TestUid);
        Assert.Equal("retained", persisted.ExtensionData!["future"].GetString());
        test.Id = "changed-code";
        Assert.Single(repo.GetByTestIdOrSenzaTest(test.Id, test));
        test.Uid = Guid.NewGuid();
        Assert.Empty(repo.GetByTestIdOrSenzaTest(signal.TestId, test));
    }

    [Fact]
    public void DiagnosticsResolveEachReferenceIdentity_ExposeOrphanAndKeepRenamedUidHealthy()
    {
        var (test, p) = Fixture(); p.TestUid = Guid.NewGuid();
        var nc = new NonConformita { Id = "NC-1", TestCaseId = test.Id, TestUid = test.Uid, ProveCollegate = new() { p } };
        var errors = NcAgganciTestService.ElencaAgganciIncoerenti(new[] { nc }, _ => test, uid => uid == test.Uid ? test : null);
        Assert.Equal(MotivoAgganciaIncoerente.TestNonDisponibile, Assert.Single(errors).Motivo);
        nc.TestCaseId = "old-code"; nc.ProveCollegate.Clear();
        Assert.Empty(NcAgganciTestService.ElencaAgganciIncoerenti(new[] { nc }, _ => null, uid => uid == test.Uid ? test : null));
    }

    [Fact]
    public void PermanentDeleteWarning_UsesUidForRenamedTestAndRejectsOtherIdentity()
    {
        var test = new TestCase { Id = "NEW" };
        var nc = new NonConformita { Id = "NC-1", TestCaseId = "OLD", TestUid = test.Uid };
        var viaProof = new NonConformita { Id = "NC-2", ProveCollegate = new() { new() { TestId = "OLD", TestUid = test.Uid } } };
        var other = new NonConformita { Id = "NC-3", TestCaseId = "NEW", TestUid = Guid.NewGuid() };
        var signal = new SegnalazioneDaClassificare { TestId = "OLD", TestUid = test.Uid };
        Assert.Equal(new[] { "NC-1", "NC-2" }, NcAgganciTestService.NcCheCitanoIlTest(test.Id, new[] { nc, viaProof, other }, test.Uid).Select(n => n.NcId));
        Assert.NotNull(NcAgganciTestService.AvvisoEliminazioneDefinitiva(test.Id, new[] { nc, viaProof }, new[] { signal }, test.Uid));
        Assert.Null(NcAgganciTestService.AvvisoEliminazioneDefinitiva(test.Id, new[] { other }, Array.Empty<SegnalazioneDaClassificare>(), test.Uid));
        Assert.NotNull(NcAgganciTestService.AvvisoEliminazioneDefinitiva(test.Id, Array.Empty<NonConformita>(), new[] { signal }, test.Uid));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
