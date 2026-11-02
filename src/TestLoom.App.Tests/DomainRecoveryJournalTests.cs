using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Ripristino dal recovery-bin → evento Create nel journal per-device. Percorso identico a quello dei ViewModel:
/// DomainRecoveryService.Restore e, dopo il commit della transazione, TryRecordRestoreInJournal. Tutto sotto
/// Path.GetTempPath(): nessun accesso al vero %LOCALAPPDATA%.
/// </summary>
[Collection("BackupWriteGate")] // BackupService.WriteGate è statico: niente esecuzione parallela fra queste classi
public sealed class DomainRecoveryJournalTests : IDisposable
{
    private const string Dispositivo = "PC-RIPRISTINO";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_RecoveryJournal_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_RecoveryJournalLocal_" + Guid.NewGuid().ToString("N"));

    public DomainRecoveryJournalTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_local);
        new DataPaths(_root).EnsureStructure();
    }

    private DataPaths Paths => new(_root);

    private DomainRecordJournalService NuovoJournal() => new(Paths, _local, Dispositivo);

    private string FileCorrente() => Path.Combine(_root, "_ditm", "record-journal", Dispositivo, "current.ndjson");

    /// <summary>Eventi pubblicati dal device nel log condiviso; le righe non interpretabili vengono saltate.</summary>
    private List<RecordJournalEvent> EventiPubblicati()
    {
        var eventi = new List<RecordJournalEvent>();
        if (!File.Exists(FileCorrente())) return eventi;
        foreach (var riga in File.ReadAllLines(FileCorrente()).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            try { eventi.Add(JsonSerializer.Deserialize<RecordJournalEvent>(riga, JsonIo.CompactOptions)!); }
            catch (JsonException) { }
        }
        return eventi;
    }

    [Fact]
    public void RipristinoNc_RegistraUnCreateNelJournalDelDevice()
    {
        var nc = new NonConformita { Uid = Guid.NewGuid(), Id = "NC-RJ-001", RevisioneDati = 3 };
        var fonte = Paths.NonConformitaFile(nc.Id);
        JsonIo.WriteAtomic(fonte, nc);
        var record = DomainRecoveryService.ArchiveAndDelete(Paths, fonte, DomainRecoveryCategory.NonConformita, "Master Uno");

        var restored = DomainRecoveryService.Restore(Paths, record.RecoveryId, "Master Due");
        DomainRecoveryService.TryRecordRestoreInJournal(NuovoJournal(), restored, "Master Due");

        var evento = Assert.Single(EventiPubblicati());
        Assert.Equal(RecordJournalOperation.Create, evento.Operation);
        Assert.Equal(DomainRecordJournalService.NonConformitaDomain, evento.Domain);
        Assert.Equal(nc.Uid, evento.EntityUid);
        Assert.Equal("NC-RJ-001", evento.EntityId);
        Assert.Equal(3L, evento.NewRevision);
        Assert.Equal("Master Due", evento.Operator);
        // Un solo device che ricrea il record: nessun fork da segnalare.
        Assert.Empty(NuovoJournal().Refresh());
    }

    [Fact]
    public void RipristinoChangeRequestESegnalazione_UsanoDominioEIdentificatoreCorretti()
    {
        var cr = new ChangeRequest { Uid = Guid.NewGuid(), Id = "CR-2026-RJ-1", RevisioneDati = 4 };
        var fonteCr = Paths.ChangeRequestFile(cr.Id);
        JsonIo.WriteAtomic(fonteCr, cr);
        var recordCr = DomainRecoveryService.ArchiveAndDelete(Paths, fonteCr, DomainRecoveryCategory.ChangeRequest, "Master Uno");

        var seg = new SegnalazioneDaClassificare { Uid = Guid.NewGuid(), RevisioneDati = 2 };
        var fonteSeg = Paths.SegnalazioneFile(seg.Uid);
        JsonIo.WriteAtomic(fonteSeg, seg);
        var recordSeg = DomainRecoveryService.ArchiveAndDelete(Paths, fonteSeg, DomainRecoveryCategory.Segnalazione, "Master Uno");

        var journal = NuovoJournal();
        DomainRecoveryService.TryRecordRestoreInJournal(journal, DomainRecoveryService.Restore(Paths, recordCr.RecoveryId, "Master Due"), "Master Due");
        DomainRecoveryService.TryRecordRestoreInJournal(journal, DomainRecoveryService.Restore(Paths, recordSeg.RecoveryId, "Master Due"), "Master Due");

        var eventi = EventiPubblicati();
        Assert.Equal(2, eventi.Count);
        Assert.Contains(eventi, e => e.Domain == DomainRecordJournalService.ChangeRequestDomain
                                     && e.EntityId == "CR-2026-RJ-1" && e.NewRevision == 4);
        // La segnalazione usa l'UID senza trattini come identificatore, come nel repository.
        Assert.Contains(eventi, e => e.Domain == DomainRecordJournalService.SegnalazioneDomain
                                     && e.EntityId == seg.Uid.ToString("N") && e.Operation == RecordJournalOperation.Create);
    }

    [Fact]
    public void RipristinoGiaPresenteConStessoContenuto_NonRegistraAlcunEvento()
    {
        var nc = new NonConformita { Uid = Guid.NewGuid(), Id = "NC-RJ-002", RevisioneDati = 1 };
        var fonte = Paths.NonConformitaFile(nc.Id);
        JsonIo.WriteAtomic(fonte, nc);
        var record = DomainRecoveryService.ArchiveOnly(Paths, fonte, DomainRecoveryCategory.NonConformita);

        var restored = DomainRecoveryService.Restore(Paths, record.RecoveryId, "Master Due");
        Assert.True(restored.AlreadyPresent);
        DomainRecoveryService.TryRecordRestoreInJournal(NuovoJournal(), restored, "Master Due");

        // Nessuno stato è cambiato: un Create qui sarebbe un evento falso.
        Assert.Empty(EventiPubblicati());
    }

    [Fact]
    public void CategoriaTest_NonScriveNelJournalDeiRecordENonFallisce()
    {
        var restored = new DomainRecoveryRestoreResult(
            new DomainRecoveryRecord
            {
                Category = DomainRecoveryCategory.TestCase,
                PayloadBase64 = Convert.ToBase64String("{}"u8.ToArray())
            },
            Path.Combine(_root, "testcases", "TC-X.json"),
            AlreadyPresent: false);

        var ex = Record.Exception(() => DomainRecoveryService.TryRecordRestoreInJournal(NuovoJournal(), restored, "Master"));

        Assert.Null(ex);
        Assert.Empty(EventiPubblicati());
    }

    [Fact]
    public void SenzaJournal_IlRipristinoRestaValidoENonFallisce()
    {
        var nc = new NonConformita { Uid = Guid.NewGuid(), Id = "NC-RJ-003", RevisioneDati = 2 };
        var fonte = Paths.NonConformitaFile(nc.Id);
        JsonIo.WriteAtomic(fonte, nc);
        var record = DomainRecoveryService.ArchiveAndDelete(Paths, fonte, DomainRecoveryCategory.NonConformita, "Master Uno");

        var restored = DomainRecoveryService.Restore(Paths, record.RecoveryId, "Master Due");
        var ex = Record.Exception(() => DomainRecoveryService.TryRecordRestoreInJournal(null, restored, "Master Due"));

        Assert.Null(ex);
        Assert.False(restored.AlreadyPresent);
        Assert.NotNull(JsonIo.TryRead<NonConformita>(fonte, out _));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_local, recursive: true); } catch { }
    }
}
