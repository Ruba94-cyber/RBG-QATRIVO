using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Evento "in volo" del journal (P1.6): un salvataggio prepara l'evento, scrive il record e poi lo pubblica. Se un
/// Refresh in background gira fra i due passi, finche' il record non e' ancora stato rinominato il commit risulta
/// non avvenuto: prima della correzione FlushOutbox scartava l'evento dall'outbox e il journal perdeva la traccia del
/// salvataggio (nessun fork rilevabile dagli altri PC). I test coprono NC, CR e Segnalazioni.
/// </summary>
public sealed class DomainRecordJournalInVoloTests : IDisposable
{
    private const string Device = "PC-A";

    private readonly string _base = Path.Combine(Path.GetTempPath(), "TestLoom_JOURNAL_INVOLO_TEST", Guid.NewGuid().ToString("N"));

    private sealed record Replica(string Root, string LocalDir, DataPaths Paths, DomainRecordJournalService Journal);

    [Fact]
    public void NonConformita_RefreshTraPrepareEScrittura_EventoPubblicato()
    {
        var a = NuovoReplica("A");
        var nc = new NonConformita { Id = "NC-VOLO-001", Owner = "In volo", RevisioneDati = 1 };

        VerificaRefreshTraPrepareEScrittura(a, DomainRecordJournalService.NonConformitaDomain, nc.Uid, nc.Id, nc,
            a.Paths.NonConformitaFile(nc.Id));
    }

    [Fact]
    public void ChangeRequest_RefreshTraPrepareEScrittura_EventoPubblicato()
    {
        var a = NuovoReplica("A");
        var cr = new ChangeRequest { Id = "CR-VOLO-001", Titolo = "In volo", RevisioneDati = 1 };

        VerificaRefreshTraPrepareEScrittura(a, DomainRecordJournalService.ChangeRequestDomain, cr.Uid, cr.Id, cr,
            a.Paths.ChangeRequestFile(cr.Id));
    }

    [Fact]
    public void Segnalazione_RefreshTraPrepareEScrittura_EventoPubblicato()
    {
        var a = NuovoReplica("A");
        var seg = new SegnalazioneDaClassificare { MotivazioneEsclusione = "In volo", RevisioneDati = 1 };

        VerificaRefreshTraPrepareEScrittura(a, DomainRecordJournalService.SegnalazioneDomain, seg.Uid, seg.Uid.ToString("N"),
            seg, a.Paths.SegnalazioneFile(seg.Uid));
    }

    [Fact]
    public void RefreshTraPrepareECancel_EventoNonPubblicato()
    {
        var a = NuovoReplica("A");
        var nc = new NonConformita { Id = "NC-VOLO-002", RevisioneDati = 1 };
        var pending = a.Journal.Prepare(DomainRecordJournalService.NonConformitaDomain, nc.Uid, nc.Id,
            RecordJournalOperation.Create, null, null, nc, nc.RevisioneDati, "Test");

        a.Journal.Refresh();
        a.Journal.Cancel(pending);
        a.Journal.FlushOutbox();

        Assert.Empty(EventiPubblicati(a));
        Assert.Empty(RigheOutbox(a));
    }

    [Fact]
    public void Publish_SenzaRecordScritto_EventoScartatoENonRestaInVolo()
    {
        var a = NuovoReplica("A");
        var nc = new NonConformita { Id = "NC-VOLO-003", RevisioneDati = 1 };
        var pending = a.Journal.Prepare(DomainRecordJournalService.NonConformitaDomain, nc.Uid, nc.Id,
            RecordJournalOperation.Create, null, null, nc, nc.RevisioneDati, "Test");

        // Nessun record scritto: il commit non e' avvenuto. Publish non deve pubblicarlo e l'evento non deve restare
        // in volo, altrimenti resterebbe nell'outbox per tutta la sessione.
        a.Journal.Publish(pending);
        a.Journal.Refresh();

        Assert.Empty(EventiPubblicati(a));
        Assert.Empty(RigheOutbox(a));
    }

    private static void VerificaRefreshTraPrepareEScrittura<T>(Replica a, string domain, Guid uid, string id, T record,
        string percorsoRecord)
    {
        var pending = a.Journal.Prepare(domain, uid, id, RecordJournalOperation.Create, null, null, record, 1, "Test");

        // Refresh in background mentre il salvataggio e' ancora in corso: il record non e' ancora stato scritto.
        a.Journal.Refresh();
        Assert.Single(RigheOutbox(a));

        JsonIo.WriteAtomic(percorsoRecord, record, overwrite: false);
        a.Journal.Publish(pending);

        var evento = Assert.Single(EventiPubblicati(a));
        Assert.Equal(domain, evento.Domain);
        Assert.Equal(uid, evento.EntityUid);
        Assert.Empty(RigheOutbox(a));
    }

    private Replica NuovoReplica(string nome)
    {
        var root = Path.Combine(_base, nome, "shared");
        var local = Path.Combine(_base, nome, "local");
        Directory.CreateDirectory(local);
        var paths = new DataPaths(root);
        paths.EnsureStructure();
        Assert.True(WorkspaceWritePolicy.CanWrite(paths), $"Il workspace di test {nome} non e scrivibile.");
        return new Replica(root, local, paths, new DomainRecordJournalService(paths, local, Device));
    }

    private static string CurrentFile(Replica r) =>
        Path.Combine(r.Root, "_ditm", "record-journal", Device, "current.ndjson");

    private static string OutboxFile(Replica r) =>
        Path.Combine(r.LocalDir, "record-journal", "outbox.ndjson");

    private static string[] RigheOutbox(Replica r) => RigheNonVuote(OutboxFile(r));

    private static string[] RigheNonVuote(string file) =>
        File.Exists(file)
            ? File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray()
            : Array.Empty<string>();

    private static List<RecordJournalEvent> EventiPubblicati(Replica r) =>
        RigheNonVuote(CurrentFile(r))
            .Select(l => JsonSerializer.Deserialize<RecordJournalEvent>(l, JsonIo.CompactOptions)!)
            .ToList();

    public void Dispose()
    {
        try { if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true); }
        catch { /* eventuale lock residuo: la cartella resta sotto TEMP */ }
    }
}
