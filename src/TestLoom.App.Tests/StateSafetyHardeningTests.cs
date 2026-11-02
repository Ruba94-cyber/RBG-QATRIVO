using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Regressioni mirate agli invarianti di state-safety introdotti dopo la 6.0: una failure o un conflitto non
/// deve mai essere trasformato in lista vuota, last-wins silenzioso o mutazione della cache condivisa.
/// </summary>
public sealed class StateSafetyHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_StateSafety_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_StateSafetyLocal_" + Guid.NewGuid().ToString("N"));

    public StateSafetyHardeningTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        TryDelete(_root);
        TryDelete(_local);
    }

    [Fact]
    public void ExistingEditDraft_RoundTrip_IsIsolatedPerOperatorAndUid()
    {
        var service = new BozzaAutosaveService(_local);
        var uid = Guid.NewGuid();
        var baseTc = new TestCase { Uid = uid, Id = "TC-REC-0001", RevisioneDati = 7, Descrizione = "base" };
        TestCaseFingerprint.Stamp(baseTc);
        var edited = EntityStateFingerprint.Clone(baseTc);
        edited.Descrizione = "modifica non salvata";

        service.SalvaModifica("Mario Rossi", baseTc, edited);

        var restored = service.CaricaModifica("Mario Rossi", uid);
        Assert.NotNull(restored);
        Assert.Equal(7, restored!.RevisioneBase);
        Assert.Equal(TestCaseFingerprint.Compute(baseTc), restored.HashBase);
        Assert.Equal("modifica non salvata", restored.TestCase.Descrizione);
        Assert.Null(service.CaricaModifica("Altro Operatore", uid));
        Assert.Null(service.CaricaModifica("Mario Rossi", Guid.NewGuid()));

        // Chi legge la bozza riceve una deserializzazione separata: modificarla non cambia il file di recovery.
        restored.TestCase.Descrizione = "mutata in test";
        Assert.Equal("modifica non salvata", service.CaricaModifica("Mario Rossi", uid)!.TestCase.Descrizione);

        service.EliminaModifica("Mario Rossi", uid);
        Assert.Null(service.CaricaModifica("Mario Rossi", uid));
    }

    [Fact]
    public void MasterData_ExistingCorruptFile_FailsClosedInsteadOfReturningEmptyList()
    {
        var path = Path.Combine(_root, "operatori.json");
        File.WriteAllText(path, "{ non-json");
        var repo = new MasterDataRepository<Operatore>(path);

        var ex = Assert.Throws<MasterDataReadException>(() => repo.Load());
        Assert.Equal(path, ex.PathMaster);
        Assert.Contains("JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FutureSchema_WorkspaceWritePolicy_BlocksWritesWithoutMutatingManifest()
    {
        var paths = new DataPaths(_root);
        var manifest = new VersionManifest
        {
            VersioneCorrente = "99.0.0",
            VersioneMinima = CompatibilitaVersioneService.JournalSafetyMinimumVersion,
            SchemaDatiMassimo = TestCase.CurrentSchemaVersion + 1
        };
        File.WriteAllText(paths.VersionManifestFile, JsonSerializer.Serialize(manifest, JsonIo.Options));
        var before = File.ReadAllBytes(paths.VersionManifestFile);

        Assert.False(WorkspaceWritePolicy.CanWrite(paths));
        Assert.Throws<IOException>(() => WorkspaceWritePolicy.EnsureCanWrite(paths, "test"));
        Assert.Equal(before, File.ReadAllBytes(paths.VersionManifestFile));
    }

    [Fact]
    public void NotificationMarkRead_WhenWorkspaceIsReadOnly_DoesNotMutateCallerOrCreateLedger()
    {
        var paths = new DataPaths(_root);
        var manifest = new VersionManifest
        {
            VersioneCorrente = "99.0.0",
            VersioneMinima = CompatibilitaVersioneService.JournalSafetyMinimumVersion,
            SchemaDatiMassimo = TestCase.CurrentSchemaVersion + 1
        };
        File.WriteAllText(paths.VersionManifestFile, JsonSerializer.Serialize(manifest, JsonIo.Options));
        var repo = new NotificaRepository(paths);
        var item = new NotificaApp { Destinatario = "Mario Rossi", Titolo = "Safety", Letta = false };

        Assert.Throws<IOException>(() => repo.MarkRead(item));
        Assert.False(item.Letta);
        Assert.False(Directory.Exists(Path.Combine(_root, "_ditm", "notifications")));
    }

    [Fact]
    public void NotificationGetAll_OnReadOnlyWorkspace_RemainsPureReadSide()
    {
        var paths = new DataPaths(_root);
        var manifest = new VersionManifest
        {
            VersioneCorrente = "99.0.0",
            VersioneMinima = CompatibilitaVersioneService.JournalSafetyMinimumVersion,
            SchemaDatiMassimo = TestCase.CurrentSchemaVersion + 1
        };
        File.WriteAllText(paths.VersionManifestFile, JsonSerializer.Serialize(manifest, JsonIo.Options));
        var repo = new NotificaRepository(paths);

        Assert.Empty(repo.GetAll("Mario Rossi"));
        Assert.False(Directory.Exists(Path.Combine(_root, "_ditm", "notifications")));
    }

    [Fact]
    public void SharedWorkflowTransactionGuard_ForeignMarkerBlocksReadAndNewTransaction()
    {
        var paths = new DataPaths(_root);
        var txDir = Path.Combine(_root, "_ditm", "transactions");
        Directory.CreateDirectory(txDir);
        var marker = new WorkflowTransactionMarker
        {
            Id = Guid.NewGuid(),
            StartedUtc = DateTime.UtcNow.AddMinutes(-2),
            Device = "PC-B",
            Description = "save-test:TC-REMOTE"
        };
        JsonIo.WriteAtomic(Path.Combine(txDir, $"{marker.Id:N}.pending.json"), marker);

        Assert.False(SharedWorkflowTransactionGuard.CanReadConsistently(paths, out var message));
        Assert.Contains("PC-B", message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<IOException>(() =>
        {
            using var tx = WorkflowTransactionService.Begin(paths, "save-test:TC-LOCAL");
        });
    }

    [Fact]
    public void SharedWorkflowTransactionGuard_OwnActiveMarkerDoesNotBlockCurrentTransaction()
    {
        var paths = new DataPaths(_root);
        using var tx = WorkflowTransactionService.Begin(paths, "test-own-marker");

        Assert.NotNull(WorkflowTransactionService.ActiveId);
        Assert.Equal(tx.Id, WorkflowTransactionService.ActiveId);
        Assert.True(SharedWorkflowTransactionGuard.CanReadConsistently(paths, out var message), message);
    }

    [Fact]
    [Trait("Category", "Lento")]
    public void AppState_ForeignPendingTransactionFreezesRefreshUntilMarkerDisappears()
    {
        SeedDemoDataService.Genera(_root);
        var paths = new DataPaths(_root);
        var txDir = Path.Combine(_root, "_ditm", "transactions");
        Directory.CreateDirectory(txDir);
        var marker = new WorkflowTransactionMarker
        {
            Id = Guid.NewGuid(),
            StartedUtc = DateTime.UtcNow,
            Device = "PC-REMOTE",
            Description = "save-test:TC-REMOTE"
        };
        var markerPath = Path.Combine(txDir, $"{marker.Id:N}.pending.json");
        JsonIo.WriteAtomic(markerPath, marker);

        using var state = new AppState(new AppConfigService(Path.Combine(_local, "config.json")), runBackgroundMaintenance: false);
        state.InizializzaCartella(_root);

        Assert.Equal(StatoCondivisione.SolaLettura, state.StatoCondivisione.Stato);
        Assert.Contains("PC-REMOTE", state.StatoCondivisione.Messaggio, StringComparison.OrdinalIgnoreCase);

        File.Delete(markerPath);
        state.RicaricaTutto();
        Assert.Equal(StatoCondivisione.Operativa, state.StatoCondivisione.Stato);
    }

    [Fact]
    public void WorkflowWorkingCopyScope_OverlaysOnlyCurrentFlowAndNeverReturnsSharedObject()
    {
        var uid = Guid.NewGuid();
        var authoritative = new NonConformita { Uid = uid, Id = "NC-0001", RevisioneDati = 3 };
        var working = EntityStateFingerprint.Clone(authoritative);
        working.RevisioneDati = 4;

        var outside = WorkflowWorkingCopyScope.ApplyNonConformita([authoritative]);
        Assert.Equal(3, outside.Single().RevisioneDati);
        Assert.NotSame(authoritative, outside.Single());

        using (WorkflowWorkingCopyScope.Begin([working], null))
        {
            var overlaid = WorkflowWorkingCopyScope.ApplyNonConformita([authoritative]);
            Assert.Equal(4, overlaid.Single().RevisioneDati);
            overlaid.Single().RevisioneDati = 99;
            Assert.Equal(4, WorkflowWorkingCopyScope.ApplyNonConformita([authoritative]).Single().RevisioneDati);
        }

        Assert.Equal(3, WorkflowWorkingCopyScope.ApplyNonConformita([authoritative]).Single().RevisioneDati);
    }

    [Fact]
    public void SegnalazioneDuplicateUid_CanonicaRestaUtilizzabile_CopiaSegnalataInCopieDiConflitto()
    {
        // Comportamento aggiornato (block C, revisione multi-PC 6.4.2): prima QUALUNQUE Uid duplicato — anche
        // una copia di conflitto OneDrive/SharePoint riconoscibile per nome — rendeva l'intera Segnalazione
        // invisibile da GetAll() e bloccava Get()/Save() come "fork OneDrive aperto" (questo test asseriva
        // esattamente quel blocco totale). Ora, quando ESATTAMENTE UNO dei file ha il nome canonico
        // (segnalazione_<uid>.json, come qui "a"), il canonico resta normalmente utilizzabile e la copia non
        // canonica (qui "b", nome "(conflicted copy)") viene esclusa e segnalata in CopieDiConflitto — mai un
        // FileConProblemi indistinguibile da un errore di lettura, mai un blocco totale della Segnalazione.
        var paths = new DataPaths(_root);
        Directory.CreateDirectory(paths.SegnalazioniDir);
        var uid = Guid.NewGuid();
        var a = new SegnalazioneDaClassificare { Uid = uid, RevisioneDati = 1 };
        var b = new SegnalazioneDaClassificare { Uid = uid, RevisioneDati = 2 };

        JsonIo.WriteAtomic(Path.Combine(paths.SegnalazioniDir, $"segnalazione_{uid:N}.json"), a);
        JsonIo.WriteAtomic(Path.Combine(paths.SegnalazioniDir, $"segnalazione_{uid:N} (conflicted copy).json"), b);

        var repo = new SegnalazioneRepository(paths);
        repo.RefreshIndex();

        Assert.Contains(repo.GetAll(), x => x.Uid == uid);
        Assert.NotNull(repo.Get(uid));
        Assert.DoesNotContain(repo.FileConProblemi, x => x.Percorso.Contains(uid.ToString("N"), StringComparison.OrdinalIgnoreCase));
        var copia = Assert.Single(repo.CopieDiConflitto, x => x.Percorso.Contains(uid.ToString("N"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains("DIVERSO", copia.Errore);
    }

    /// <summary>Un gruppo SENZA un nome canonico univoco (0 o 2+ file "canonici" per lo stesso Uid) resta
    /// invece un vero UID duplicato ambiguo, bloccante come prima: qui nessuno dei due nomi corrisponde al
    /// pattern canonico "segnalazione_&lt;uid&gt;.json" (entrambi hanno un suffisso), quindi non c'è modo sicuro
    /// di sapere qual è l'originale.</summary>
    [Fact]
    public void SegnalazioneDuplicateUid_SenzaUnCanonicoUnivoco_RestaUnUidDuplicatoAmbiguoBloccante()
    {
        var paths = new DataPaths(_root);
        Directory.CreateDirectory(paths.SegnalazioniDir);
        var uid = Guid.NewGuid();
        var a = new SegnalazioneDaClassificare { Uid = uid, RevisioneDati = 1 };
        var b = new SegnalazioneDaClassificare { Uid = uid, RevisioneDati = 2 };

        JsonIo.WriteAtomic(Path.Combine(paths.SegnalazioniDir, $"segnalazione_{uid:N} (PC1).json"), a);
        JsonIo.WriteAtomic(Path.Combine(paths.SegnalazioniDir, $"segnalazione_{uid:N} (PC2).json"), b);

        var repo = new SegnalazioneRepository(paths);
        repo.RefreshIndex();

        Assert.DoesNotContain(repo.GetAll(), x => x.Uid == uid);
        Assert.True(repo.FileConProblemi.Count(x => x.Percorso.Contains(uid.ToString("N"), StringComparison.OrdinalIgnoreCase)) >= 2);
        Assert.Throws<InvalidDataException>(() => repo.Get(uid));
    }

    [Fact]
    public void ChangeRequestRepository_ReturnsSnapshots_NotMutableCacheReferences()
    {
        var paths = new DataPaths(_root);
        Directory.CreateDirectory(paths.ChangeRequestDir);
        var cr = new ChangeRequest { Uid = Guid.NewGuid(), Id = "CR-2026-0001", RevisioneDati = 5, Titolo = "autorevole" };
        JsonIo.WriteAtomic(paths.ChangeRequestFile(cr.Id), cr);

        var repo = new ChangeRequestRepository(paths);
        repo.RefreshIndex();

        var first = repo.GetById(cr.Id)!;
        first.Titolo = "mutato solo dal chiamante";
        first.RevisioneDati = 99;

        var second = repo.GetById(cr.Id)!;
        Assert.Equal("autorevole", second.Titolo);
        Assert.Equal(5, second.RevisioneDati);
        Assert.NotSame(first, second);
    }

    private static void TryDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
