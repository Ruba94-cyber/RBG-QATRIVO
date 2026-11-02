using System.IO.Compression;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

[Collection("BackupWriteGate")] // BackupService.WriteGate è statico: niente esecuzione parallela fra queste classi
public sealed class DomainRecoveryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_DomainRecovery_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_DomainRecoveryLocal_" + Guid.NewGuid().ToString("N"));

    public DomainRecoveryServiceTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_local);
        new DataPaths(_root).EnsureStructure();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_local, recursive: true); } catch { }
    }

    [Fact]
    public void ArchiveAndDelete_ThenRestore_PreservesExactRecoveryBytesAndDomainState()
    {
        var paths = new DataPaths(_root);
        var source = paths.NonConformitaFile("NC-7001");
        var original = "{\r\n  \"Uid\": \"" + Guid.NewGuid() + "\",\r\n  \"Id\": \"NC-7001\",\r\n  \"RevisioneDati\": 9\r\n}";
        File.WriteAllText(source, original);
        var originalBytes = File.ReadAllBytes(source);

        var record = DomainRecoveryService.ArchiveAndDelete(
            paths, source, DomainRecoveryCategory.NonConformita, "Master Uno");

        // La delete transazionale resta come tombstone fino a restore/purge: non deve sembrare una sparizione
        // ambigua a un secondo PC che riceve i file OneDrive in ordine diverso.
        Assert.True(File.Exists(source));
        Assert.True(WorkflowTransactionVisibilityService.IsCommittedTombstone(paths, source));
        Assert.Equal(originalBytes, Convert.FromBase64String(record.PayloadBase64));
        Assert.Equal("Master Uno", record.ArchivedBy);
        Assert.Single(DomainRecoveryService.GetAll(paths));

        var restored = DomainRecoveryService.Restore(paths, record.RecoveryId, "Master Due");

        Assert.False(restored.AlreadyPresent);
        Assert.True(File.Exists(source));
        Assert.False(WorkflowTransactionVisibilityService.IsCommittedTombstone(paths, source));
        var restoredNc = JsonIo.TryRead<NonConformita>(source, out var restoreError);
        Assert.NotNull(restoredNc);
        Assert.Null(restoreError);
        Assert.Equal("NC-7001", restoredNc!.Id);
        Assert.Equal(9, restoredNc.RevisioneDati);
        var after = Assert.Single(DomainRecoveryService.GetAll(paths));
        Assert.Equal(originalBytes, Convert.FromBase64String(after.PayloadBase64));
        Assert.NotNull(after.RestoredUtc);
        Assert.Equal("Master Due", after.RestoredBy);
    }

    /// <summary>Bug segnalato dall'utente (2026-09-18): "non capisco anche perché elimina definitivamente mi
    /// mandi la roba in recovery, ho praticamente due archiviazioni e nulla si elimina mai definitivamente" —
    /// prima di questa modifica non esisteva alcun modo di completare il ciclo: la classe stessa documentava
    /// "una purge deve essere un'azione Master esplicita e separata" ma quell'azione non era mai stata
    /// implementata da nessuna parte.</summary>
    [Fact]
    public void Purge_RimuoveLenvelopePerSempreSenzaToccareUnEventualeRipristinoGiaAvvenuto()
    {
        var paths = new DataPaths(_root);
        var source = paths.NonConformitaFile("NC-7002");
        File.WriteAllText(source, "{\r\n  \"Uid\": \"" + Guid.NewGuid() + "\",\r\n  \"Id\": \"NC-7002\",\r\n  \"RevisioneDati\": 1\r\n}");

        var record = DomainRecoveryService.ArchiveAndDelete(paths, source, DomainRecoveryCategory.NonConformita, "Master Uno");
        Assert.Single(DomainRecoveryService.GetAll(paths));

        // Ripristinato PRIMA della purge: la copia di recovery a quel punto è solo un backup ridondante.
        DomainRecoveryService.Restore(paths, record.RecoveryId, "Master Due");
        Assert.True(File.Exists(source), "il record ripristinato deve restare al suo posto dopo la purge della sola copia di recovery");

        DomainRecoveryService.Purge(paths, record.RecoveryId, "Master Tre");

        Assert.Empty(DomainRecoveryService.GetAll(paths));
        Assert.True(File.Exists(source));
        var ancoraLeggibile = JsonIo.TryRead<NonConformita>(source, out _);
        Assert.NotNull(ancoraLeggibile);
        Assert.Equal("NC-7002", ancoraLeggibile!.Id);
    }

    [Fact]
    public void Purge_SuUnaCopiaMaiRipristinata_LaRimuovePerSempre()
    {
        var paths = new DataPaths(_root);
        var source = paths.NonConformitaFile("NC-7003");
        File.WriteAllText(source, "{\r\n  \"Uid\": \"" + Guid.NewGuid() + "\",\r\n  \"Id\": \"NC-7003\",\r\n  \"RevisioneDati\": 1\r\n}");
        var record = DomainRecoveryService.ArchiveAndDelete(paths, source, DomainRecoveryCategory.NonConformita, "Master Uno");

        DomainRecoveryService.Purge(paths, record.RecoveryId, "Master Due");

        Assert.Empty(DomainRecoveryService.GetAll(paths));
        // Il tombstone della delete originaria resta intatto: la purge tocca solo l'envelope di recovery.
        Assert.True(WorkflowTransactionVisibilityService.IsCommittedTombstone(paths, source));
    }

    [Fact]
    public void Purge_SuUnRecoveryIdInesistente_NonLanciaEENonFaNulla()
    {
        var paths = new DataPaths(_root);
        var ex = Record.Exception(() => DomainRecoveryService.Purge(paths, Guid.NewGuid(), "Master"));
        Assert.Null(ex);
    }

    [Fact]
    public void TestRepository_DefinitiveDelete_FromTrash_LeavesSharedRecoveryAndCanRestore()
    {
        var paths = new DataPaths(_root);
        var store = new LocalTestIndexStore(_root, _local);
        var repo = new TestCaseRepository(paths, store);
        var tc = new TestCase
        {
            Uid = Guid.NewGuid(),
            Id = "TC-RECOVERY-7001",
            Componente = new EcuSelection { "Componente-A" },
            Veicoli = ["Daily"],
            Veicolo = "Daily",
            Descrizione = "payload da non perdere",
            ModificatoDa = "Master Test"
        };

        repo.Save(tc, isNew: true);
        repo.SoftDelete(tc, "Master Test");
        var trashPath = paths.CestinoFile(tc.Id);
        Assert.True(File.Exists(trashPath));
        var exactTrashBytes = File.ReadAllBytes(trashPath);

        repo.EliminaDefinitivamente(tc);

        Assert.True(File.Exists(trashPath));
        Assert.True(WorkflowTransactionVisibilityService.IsCommittedTombstone(paths, trashPath));
        var recovery = Assert.Single(DomainRecoveryService.GetAll(paths), x => x.Category == DomainRecoveryCategory.TestCase);
        Assert.Equal(exactTrashBytes, Convert.FromBase64String(recovery.PayloadBase64));
        Assert.Equal("Master Test", recovery.ArchivedBy);

        var restored = DomainRecoveryService.Restore(paths, recovery.RecoveryId, "Master Restore");
        Assert.False(restored.AlreadyPresent);
        Assert.Equal(trashPath, restored.TargetPath);
        Assert.False(WorkflowTransactionVisibilityService.IsCommittedTombstone(paths, trashPath));
        var restoredTc = JsonIo.TryRead<TestCase>(trashPath, out var error);
        Assert.NotNull(restoredTc);
        Assert.Null(error);
        Assert.Equal(tc.Uid, restoredTc!.Uid);
        Assert.True(restoredTc.Eliminato);
    }

    [Fact]
    public void Restore_WhenDifferentRecordAlreadyExists_NeverOverwritesIt()
    {
        var paths = new DataPaths(_root);
        var source = paths.ChangeRequestFile("CR-2026-7001");
        var original = new ChangeRequest { Uid = Guid.NewGuid(), Id = "CR-2026-7001", RevisioneDati = 4, Titolo = "originale" };
        JsonIo.WriteAtomic(source, original);
        var record = DomainRecoveryService.ArchiveAndDelete(paths, source, DomainRecoveryCategory.ChangeRequest);

        var other = new ChangeRequest { Uid = Guid.NewGuid(), Id = "CR-2026-7001", RevisioneDati = 1, Titolo = "nuovo diverso" };
        JsonIo.WriteAtomic(source, other);
        var before = File.ReadAllBytes(source);

        Assert.Throws<IOException>(() => DomainRecoveryService.Restore(paths, record.RecoveryId));
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Null(DomainRecoveryService.GetAll(paths).Single().RestoredUtc);
    }

    [Fact]
    public void Restore_WhenSamePayloadAlreadyExists_IsIdempotentAndDoesNotRewrite()
    {
        var paths = new DataPaths(_root);
        var source = paths.SegnalazioneFile(Guid.NewGuid());
        File.WriteAllText(source, "{\"Uid\":\"" + Guid.NewGuid() + "\",\"RevisioneDati\":2}");
        var bytes = File.ReadAllBytes(source);
        var record = DomainRecoveryService.ArchiveOnly(paths, source, DomainRecoveryCategory.Segnalazione);
        var mtime = File.GetLastWriteTimeUtc(source);

        var restored = DomainRecoveryService.Restore(paths, record.RecoveryId, "Master");

        Assert.True(restored.AlreadyPresent);
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(source));
    }

    [Fact]
    public void TamperedEnvelope_IsExcludedFromListAndCannotRestore()
    {
        var paths = new DataPaths(_root);
        var source = paths.NonConformitaFile("NC-7002");
        JsonIo.WriteAtomic(source, new NonConformita { Uid = Guid.NewGuid(), Id = "NC-7002", RevisioneDati = 1 });
        var record = DomainRecoveryService.ArchiveOnly(paths, source, DomainRecoveryCategory.NonConformita);
        var envelopePath = Directory.EnumerateFiles(paths.RecoveryBinDir, $"recovery_{record.RecoveryId:N}.json", SearchOption.AllDirectories).Single();
        var envelope = JsonIo.TryRead<DomainRecoveryRecord>(envelopePath, out _)!;
        envelope.PayloadBase64 = Convert.ToBase64String("{}"u8.ToArray());
        JsonIo.WriteAtomic(envelopePath, envelope);

        Assert.DoesNotContain(DomainRecoveryService.GetAll(paths), x => x.RecoveryId == record.RecoveryId);
        Assert.Throws<InvalidDataException>(() => DomainRecoveryService.Restore(paths, record.RecoveryId));
    }

    [Fact]
    public void RecoveryEnvelope_IsIncludedInNormalJsonBackup()
    {
        var paths = new DataPaths(_root);
        var source = paths.NonConformitaFile("NC-7003");
        JsonIo.WriteAtomic(source, new NonConformita { Uid = Guid.NewGuid(), Id = "NC-7003", RevisioneDati = 1 });
        var record = DomainRecoveryService.ArchiveAndDelete(paths, source, DomainRecoveryCategory.NonConformita);

        // localBaseDirectory esplicito: mai scrivere il backup nel vero %LOCALAPPDATA% durante i test (6.4.2,
        // stesso meccanismo di override gia' usato da LocalTestIndexStore/_local in questa classe).
        var backup = BackupService.CreaOra(paths, new DateTime(2026, 9, 4, 12, 0, 0), _local);
        using var zip = ZipFile.OpenRead(backup);

        Assert.Contains(zip.Entries, e =>
            e.FullName.Replace('\\', '/').Contains("recovery-bin/", StringComparison.OrdinalIgnoreCase) &&
            e.FullName.Contains(record.RecoveryId.ToString("N"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ArchiveOnly_RejectsMasterConfigAndPathsOutsideWorkspace()
    {
        var paths = new DataPaths(_root);
        JsonIo.WriteAtomic(paths.OperatoriFile, new List<Operatore>());
        Assert.Throws<InvalidOperationException>(() =>
            DomainRecoveryService.ArchiveOnly(paths, paths.OperatoriFile, DomainRecoveryCategory.NonConformita));

        var outside = Path.Combine(Path.GetTempPath(), "TestLoom_outside_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(outside, "{}");
            Assert.Throws<InvalidOperationException>(() =>
                DomainRecoveryService.ArchiveOnly(paths, outside, DomainRecoveryCategory.NonConformita));
        }
        finally { try { File.Delete(outside); } catch { } }
    }
}
