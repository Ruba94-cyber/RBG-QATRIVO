using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Simulazione multi-PC del journal per-device dei commit (NC, CR, Segnalazioni). Ogni PC ha una replica separata
/// della cartella condivisa e una cartella locale. OneDrive e simulato dagli helper Sync*: i log dei device sono
/// append-only (vince la copia piu recente), i JSON dei record sono last-writer-wins (sovrascrittura incondizionata,
/// cioe il lost update che il journal deve rendere rilevabile). Tutto sotto Path.GetTempPath(), eliminato in Dispose.
/// </summary>
public sealed class DomainRecordJournalTests : IDisposable
{
    private const string PcA = "PC-A";
    private const string PcB = "PC-B";
    private const string PcC = "PC-C";
    private const string NcDomain = DomainRecordJournalService.NonConformitaDomain;

    private readonly string _base = Path.Combine(Path.GetTempPath(), "TestLoom_JOURNAL_TEST",
        Guid.NewGuid().ToString("N"));

    private sealed record Replica(string Nome, string Root, string LocalDir, DataPaths Paths,
        DomainRecordJournalService Journal, NonConformitaRepository Nc,
        ChangeRequestRepository Cr, SegnalazioneRepository Seg);

    // ------------------------------------------------------------------ 1. scrittura del journal

    [Fact]
    public void Salvataggio_ScriveEventoNelJournalDelDevice()
    {
        var a = NuovoReplica("A", PcA);
        var nc = new NonConformita { Id = "NC-JRN-001", Owner = "A" };

        a.Nc.Save(nc);

        Assert.Single(RigheNonVuote(CurrentFile(a, PcA)));
        var evento = Assert.Single(EventiValidi(CurrentFile(a, PcA)));
        Assert.Equal(nc.Uid, evento.EntityUid);
        Assert.Equal(RecordJournalOperation.Create, evento.Operation);
        Assert.Equal(0, FileNelSottoalbero(ConflittiDir(a)));
    }

    // ------------------------------------------------------------------ 2-3. fork e payload del ramo perdente

    [Fact]
    public void DuePC_StessaBase_ModificheDiverse_ForkRilevatoEBloccato()
    {
        var a = NuovoReplica("A", PcA);
        var b = NuovoReplica("B", PcB);
        const string id = "NC-FORK-001";
        var uid = CreaNcSuA(a, id, b);
        DueRamiDallaStessaBase(a, b, id);

        var forkA = Assert.Single(a.Journal.Refresh());
        var forkB = Assert.Single(b.Journal.Refresh());
        Assert.Equal(uid, forkA.EntityUid);
        Assert.Equal(forkA.Key, forkB.Key);
        Assert.Equal(2, forkA.Branches.Count);
        Assert.Single(forkA.Branches, r => r.DeviceId == PcA && r.Operation == RecordJournalOperation.Save);
        Assert.Single(forkA.Branches, r => r.DeviceId == PcB && r.Operation == RecordJournalOperation.Save);
        Assert.Single(a.Journal.OpenForks);
        Assert.Single(b.Journal.OpenForks);

        var perA = CopiaNc(a, id);
        Assert.Throws<RecordForkOpenException>(() => a.Nc.Save(perA, perA.RevisioneDati));
        var perB = CopiaNc(b, id);
        Assert.Throws<RecordForkOpenException>(() => b.Nc.Save(perB, perB.RevisioneDati));
    }

    [Fact]
    public void Fork_PayloadDelRamoPerdenteVienePubblicatoERecuperabile()
    {
        var a = NuovoReplica("A", PcA);
        var b = NuovoReplica("B", PcB);
        const string id = "NC-FORK-PAY";
        var uid = CreaNcSuA(a, id, b);
        DueRamiDallaStessaBase(a, b, id);

        var forkA = Assert.Single(a.Journal.Refresh());
        var ramoA = Assert.Single(forkA.Branches, r => r.DeviceId == PcA);
        var pubblicato = Path.Combine(a.Root, "_ditm", "conflicts", "records", "nonconformita",
            uid.ToString("N"), ramoA.EventId + ".json");
        Assert.True(File.Exists(pubblicato), $"Payload del ramo PC-A non pubblicato: {pubblicato}");
        Assert.Equal("A", JsonIo.TryRead<NonConformita>(pubblicato, out _)!.Owner);

        SyncConflitti(a.Root, b.Root);
        var forkB = Assert.Single(b.Journal.Refresh());
        var ramoAinB = Assert.Single(forkB.Branches, r => r.DeviceId == PcA);
        var payload = b.Journal.TryReadBranchPayload(forkB, ramoAinB);
        Assert.NotNull(payload);
        Assert.Equal("A", JsonSerializer.Deserialize<NonConformita>(payload!, JsonIo.Options)!.Owner);
    }

    // ------------------------------------------------------------------ 4-5. risoluzione Master e riapertura

    [Fact]
    public void Risoluzione_Master_SbloccaIlRecord()
    {
        var a = NuovoReplica("A", PcA);
        var b = NuovoReplica("B", PcB);
        const string id = "NC-FORK-RES";
        var uid = CreaNcSuA(a, id, b);
        DueRamiDallaStessaBase(a, b, id);
        var fork = Assert.Single(a.Journal.Refresh());

        RisolviComeMaster(a, fork, id, "Versione scelta dal Master");

        Assert.Empty(a.Journal.Refresh());
        Assert.False(a.Journal.IsBlocked(NcDomain, uid));
        var dopo = CopiaNc(a, id);
        dopo.Descrizione = "Modifica dopo la risoluzione";
        a.Nc.Save(dopo, dopo.RevisioneDati);

        // Sblocco propagato solo dai journal: il JSON su B resta quello di prima della risoluzione.
        SyncJournals(a.Root, b.Root);
        Assert.Empty(b.Journal.Refresh());
        Assert.False(b.Journal.IsBlocked(NcDomain, uid));
        var suB = CopiaNc(b, id);
        b.Nc.Save(suB, suB.RevisioneDati);
    }

    [Fact]
    public void NuovoRamoDopoRisoluzione_RiapreIlFork()
    {
        var a = NuovoReplica("A", PcA);
        var b = NuovoReplica("B", PcB);
        var c = NuovoReplica("C", PcC);
        const string id = "NC-REOPEN-001";
        var uid = CreaNcSuA(a, id, b, c); // PC C resta offline con la revisione 1
        DueRamiDallaStessaBase(a, b, id);
        var fork = Assert.Single(a.Journal.Refresh());
        RisolviComeMaster(a, fork, id, "Versione scelta dal Master");
        Assert.Empty(a.Journal.Refresh());

        var ncC = CopiaNc(c, id);
        ncC.Owner = "C";
        c.Nc.Save(ncC, ncC.RevisioneDati);
        SyncJournals(c.Root, a.Root);

        var riaperto = Assert.Single(a.Journal.Refresh());
        Assert.Equal(uid, riaperto.EntityUid);
        Assert.Equal(3, riaperto.Branches.Count);
        Assert.Contains(riaperto.Branches, r => r.DeviceId == PcC);
        Assert.NotEqual(fork.Key, riaperto.Key);
        var suA = CopiaNc(a, id);
        Assert.Throws<RecordForkOpenException>(() => a.Nc.Save(suA, suA.RevisioneDati));
    }

    // ------------------------------------------------------------------ 6. delete concorrente

    [Fact]
    public void EliminazioneConcorrenteConModifica_ForkConRamoDelete()
    {
        var a = NuovoReplica("A", PcA);
        var b = NuovoReplica("B", PcB);
        const string id = "NC-DEL-001";
        var uid = CreaNcSuA(a, id, b);

        var daEliminare = CopiaNc(a, id);
        a.Nc.Elimina(daEliminare, "Operatore A");
        // La cancellazione consegnata lascia un tombstone: il file resta ma non e piu un record vivo.
        Assert.True(WorkflowTransactionVisibilityService.IsCommittedTombstoneFor(
            a.Paths, a.Paths.NonConformitaFile(id), uid, id));

        var modificata = CopiaNc(b, id);
        modificata.Owner = "B";
        b.Nc.Save(modificata, modificata.RevisioneDati);

        SyncJournals(a.Root, b.Root);
        Sync(b.Root, a.Root); // il JSON modificato da B riappare su A (last-writer-wins)
        a.Nc.RefreshIndex();

        var fork = Assert.Single(a.Journal.Refresh());
        Assert.Equal(uid, fork.EntityUid);
        Assert.Equal(2, fork.Branches.Count);
        Assert.Single(fork.Branches, r => r.IsDelete && r.DeviceId == PcA);
        Assert.Single(fork.Branches, r => r.Operation == RecordJournalOperation.Save && r.DeviceId == PcB);
        Assert.Equal(fork.Key, Assert.Single(b.Journal.Refresh()).Key);
        Assert.Throws<RecordForkOpenException>(() => a.Nc.Save(modificata, modificata.RevisioneDati));
    }

    // ------------------------------------------------------------------ 7. scrittura autorevole fallita

    [Fact]
    public void ScritturaAutorevoleFallita_EventoNonPubblicato()
    {
        var a = NuovoReplica("A", PcA);
        const string id = "NC-FAIL-001";
        var uid = CreaNcSuA(a, id);
        var righeIniziali = RigheNonVuote(CurrentFile(a, PcA)).Length;

        // (1) revisione attesa obsoleta: il Save si ferma prima di preparare il commit.
        var obsoleta = CopiaNc(a, id);
        obsoleta.Owner = "Obsoleta";
        Assert.Throws<ConflittoRevisioneException>(() => a.Nc.Save(obsoleta, 99));
        Assert.Equal(righeIniziali, RigheNonVuote(CurrentFile(a, PcA)).Length);
        Assert.False(File.Exists(OutboxFile(a)));

        // (2) Prepare seguito da Cancel: l'evento esce dall'outbox e non viene mai pubblicato.
        var corrente = CopiaNc(a, id);
        var nuova = CopiaNc(a, id);
        nuova.Owner = "Annullata";
        nuova.RevisioneDati = 2;
        var pending = a.Journal.Prepare(NcDomain, uid, id, RecordJournalOperation.Save,
            corrente, 1, nuova, 2, "Test");
        a.Journal.Cancel(pending);
        a.Journal.FlushOutbox();
        Assert.Equal(righeIniziali, RigheNonVuote(CurrentFile(a, PcA)).Length);
        Assert.False(File.Exists(OutboxFile(a)));
    }

    [Fact]
    public void ScritturaAutorevoleFallitaDopoPrepare_EventoRimossoDallOutbox()
    {
        var a = NuovoReplica("A", PcA);
        const string id = "NC-FAIL-002";
        // Destinazione occupata da una cartella: Prepare riesce, la scrittura atomica del JSON fallisce.
        Directory.CreateDirectory(a.Paths.NonConformitaFile(id));

        var ex = Record.Exception(() => a.Nc.Save(new NonConformita { Id = id }));

        Assert.IsAssignableFrom<IOException>(ex);
        Assert.False(File.Exists(OutboxFile(a)));
        Assert.False(File.Exists(CurrentFile(a, PcA)));
    }

    // ------------------------------------------------------------------ 8. outbox e crash

    [Fact]
    public void OutboxSopravviveACrash_PubblicatoAlRiavvio()
    {
        var a = NuovoReplica("A", PcA);
        var nc = new NonConformita { Id = "NC-CRASH-001", Owner = "Prima del crash", RevisioneDati = 1 };
        // Prepare, scrittura autorevole riuscita, poi crash: niente Publish e niente Cancel.
        a.Journal.Prepare(NcDomain, nc.Uid, nc.Id, RecordJournalOperation.Create, null, null,
            nc, nc.RevisioneDati, null);
        JsonIo.WriteAtomic(a.Paths.NonConformitaFile(nc.Id), nc, overwrite: false);
        Assert.Single(RigheNonVuote(OutboxFile(a)));

        // Riavvio: nuova istanza sulla stessa cartella locale.
        var riavvio = new DomainRecordJournalService(a.Paths, a.LocalDir, PcA);
        riavvio.FlushOutbox();

        var evento = Assert.Single(EventiValidi(CurrentFile(a, PcA)));
        Assert.Equal(nc.Uid, evento.EntityUid);
        Assert.False(File.Exists(OutboxFile(a)));
    }

    [Fact]
    public void OutboxSenzaJsonScritto_EventoScartatoAlRiavvio()
    {
        var a = NuovoReplica("A", PcA);
        var nc = new NonConformita { Id = "NC-CRASH-002", RevisioneDati = 1 };
        // Prepare, poi crash prima della scrittura del JSON: il commit non e mai avvenuto.
        a.Journal.Prepare(NcDomain, nc.Uid, nc.Id, RecordJournalOperation.Create, null, null,
            nc, nc.RevisioneDati, null);

        var riavvio = new DomainRecordJournalService(a.Paths, a.LocalDir, PcA);
        riavvio.FlushOutbox();

        Assert.Empty(RigheNonVuote(CurrentFile(a, PcA)));
        Assert.False(File.Exists(OutboxFile(a)));
    }

    [Fact]
    public void OutboxConRigaTroncata_NonPerdeIlCommit()
    {
        var a = NuovoReplica("A", PcA);
        // Crash durante l'append in outbox di un evento precedente: riga troncata senza a capo finale.
        File.AppendAllText(OutboxFile(a), "{\"Schema\":1,\"EventId\":\"tronc");

        var nc = new NonConformita { Id = "NC-OUTBOX-TR-1", Owner = "Dopo il crash" };
        a.Nc.Save(nc); // il JSON viene scritto: il commit e avvenuto

        var stato = $"JSON record presente={File.Exists(a.Paths.NonConformitaFile(nc.Id))}, " +
                    $"current.ndjson presente={File.Exists(CurrentFile(a, PcA))}, " +
                    $"outbox residuo={File.Exists(OutboxFile(a))}";
        Assert.True(EventiValidi(CurrentFile(a, PcA)).Any(e => e.EntityUid == nc.Uid),
            "Commit assente dal journal dopo una riga troncata nell'outbox (" + stato + ").");
    }

    // ------------------------------------------------------------------ 9. salvataggi sequenziali

    [Fact]
    public void NessunFork_PerSalvataggiSequenziali()
    {
        var a = NuovoReplica("A", PcA);
        var b = NuovoReplica("B", PcB);
        const string id = "NC-SEQ-001";
        CreaNcSuA(a, id, b);

        var ncA = CopiaNc(a, id);
        ncA.Owner = "A rev2";
        a.Nc.Save(ncA, ncA.RevisioneDati);   // rev 1 -> 2
        Sync(a.Root, b.Root);
        b.Nc.RefreshIndex();

        var ncB = CopiaNc(b, id);
        ncB.Owner = "B rev3";
        b.Nc.Save(ncB, ncB.RevisioneDati);   // rev 2 -> 3, base gia sincronizzata
        Sync(b.Root, a.Root);
        a.Nc.RefreshIndex();

        Assert.Empty(a.Journal.Refresh());
        Assert.Empty(b.Journal.Refresh());
        Assert.False(a.Journal.IsBlocked(NcDomain, ncA.Uid));
    }

    [Fact]
    public void RollbackLocale_StessoDeviceStessaBase_NonEFork()
    {
        // Una transazione workflow che ripristina il file (rollback) e un nuovo salvataggio dalla stessa base
        // producono due commit dello STESSO device sulla stessa base: non è concorrenza fra PC.
        var a = NuovoReplica("A", PcA);
        const string id = "NC-ROLLBACK-001";
        CreaNcSuA(a, id);
        var path = a.Paths.NonConformitaFile(id);
        var originale = File.ReadAllBytes(path);

        var primo = CopiaNc(a, id);
        primo.Owner = "Poi annullato";
        a.Nc.Save(primo, primo.RevisioneDati);
        File.WriteAllBytes(path, originale);              // rollback della Unit of Work
        a.Nc.RefreshPaths(new[] { path });

        var secondo = CopiaNc(a, id);
        secondo.Owner = "Definitivo";
        a.Nc.Save(secondo, secondo.RevisioneDati);

        Assert.Empty(a.Journal.Refresh());
        Assert.False(a.Journal.IsBlocked(NcDomain, secondo.Uid));
    }

    // ------------------------------------------------------------------ 10. CR e Segnalazioni (solo Save)

    [Fact]
    public void ChangeRequest_DuePC_StessaBase_ForkRilevatoEBloccato()
    {
        var a = NuovoReplica("A", PcA);
        var b = NuovoReplica("B", PcB);
        const string id = "CR-JRN-001";
        var cr = new ChangeRequest { Id = id, Titolo = "Origine" };
        a.Cr.Save(cr);
        Sync(a.Root, b.Root);
        a.Cr.RefreshIndex();
        b.Cr.RefreshIndex();

        var crA = CopiaCr(a, id);
        crA.Titolo = "Titolo di A";
        a.Cr.Save(crA, crA.RevisioneDati);
        var crB = CopiaCr(b, id);
        crB.Titolo = "Titolo di B";
        b.Cr.Save(crB, crB.RevisioneDati);

        SyncJournals(a.Root, b.Root);
        Sync(b.Root, a.Root);
        a.Cr.RefreshIndex();

        var fork = Assert.Single(a.Journal.Refresh());
        Assert.Equal(DomainRecordJournalService.ChangeRequestDomain, fork.Domain);
        Assert.Equal(2, fork.Branches.Count);
        Assert.Contains(fork.Branches, r => r.DeviceId == PcA);
        Assert.Contains(fork.Branches, r => r.DeviceId == PcB);
        Assert.Equal(fork.Key, Assert.Single(b.Journal.Refresh()).Key);

        var perA = CopiaCr(a, id);
        Assert.Throws<RecordForkOpenException>(() => a.Cr.Save(perA, perA.RevisioneDati));
        var perB = CopiaCr(b, id);
        Assert.Throws<RecordForkOpenException>(() => b.Cr.Save(perB, perB.RevisioneDati));
    }

    [Fact]
    public void Segnalazione_DuePC_StessaBase_ForkRilevatoEBloccato()
    {
        var a = NuovoReplica("A", PcA);
        var b = NuovoReplica("B", PcB);
        var seg = new SegnalazioneDaClassificare { MotivazioneEsclusione = "Origine" };
        a.Seg.Save(seg);
        var uid = seg.Uid;
        Sync(a.Root, b.Root);
        a.Seg.RefreshIndex();
        b.Seg.RefreshIndex();

        var segA = CopiaSeg(a, uid);
        segA.MotivazioneEsclusione = "Motivo di A";
        a.Seg.Save(segA, segA.RevisioneDati);
        var segB = CopiaSeg(b, uid);
        segB.MotivazioneEsclusione = "Motivo di B";
        b.Seg.Save(segB, segB.RevisioneDati);

        SyncJournals(a.Root, b.Root);
        Sync(b.Root, a.Root);
        a.Seg.RefreshIndex();

        var fork = Assert.Single(a.Journal.Refresh());
        Assert.Equal(DomainRecordJournalService.SegnalazioneDomain, fork.Domain);
        Assert.Equal(2, fork.Branches.Count);
        Assert.Contains(fork.Branches, r => r.DeviceId == PcA);
        Assert.Contains(fork.Branches, r => r.DeviceId == PcB);

        var perA = CopiaSeg(a, uid);
        Assert.Throws<RecordForkOpenException>(() => a.Seg.Save(perA, perA.RevisioneDati));
        Assert.Equal(fork.Key, Assert.Single(b.Journal.Refresh()).Key);
        var perB = CopiaSeg(b, uid);
        Assert.Throws<RecordForkOpenException>(() => b.Seg.Save(perB, perB.RevisioneDati));
    }

    // ------------------------------------------------------------------ 11. righe troncate

    [Fact]
    public void RigaParziale_NonInterpretata()
    {
        var a = NuovoReplica("A", PcA);
        const string id = "NC-PART-001";
        CreaNcSuA(a, id);
        File.AppendAllText(CurrentFile(a, PcA), "{\"Schema\":1,\"EventId\":\"tronc\n");

        var eccezione = Record.Exception(() => a.Journal.Refresh());

        Assert.Null(eccezione);
        Assert.Empty(a.Journal.OpenForks);
        Assert.Single(EventiValidi(CurrentFile(a, PcA)));   // il Create precedente resta leggibile
    }

    [Fact]
    public void RigaTroncataSenzaACapo_NonPerdeIlProssimoEvento()
    {
        var a = NuovoReplica("A", PcA);
        var b = NuovoReplica("B", PcB);
        const string id = "NC-PART-002";
        var uid = CreaNcSuA(a, id, b);
        // Crash durante l'append di A: riga troncata SENZA a capo finale.
        File.AppendAllText(CurrentFile(a, PcA), "{\"Schema\":1,\"EventId\":\"tronc");

        // Dopo il riavvio A salva dalla revisione 1: il suo evento finisce appeso alla riga troncata.
        var ncA = CopiaNc(a, id);
        ncA.Owner = "A";
        a.Nc.Save(ncA, ncA.RevisioneDati);
        var ncB = CopiaNc(b, id);
        ncB.Owner = "B";
        b.Nc.Save(ncB, ncB.RevisioneDati);
        SyncJournals(a.Root, b.Root);
        Sync(b.Root, a.Root);
        a.Nc.RefreshIndex();

        // Il commit di A e avvenuto (JSON scritto): il suo evento deve risultare leggibile nel journal di A.
        Assert.True(EventiValidi(CurrentFile(a, PcA))
                .Any(e => e.Operation == RecordJournalOperation.Save && e.EntityUid == uid),
            "Evento Save di PC-A non leggibile nel journal. Righe non interpretate: " + RigheIllegibili(CurrentFile(a, PcA)));
        var fork = Assert.Single(a.Journal.Refresh());
        Assert.Equal(2, fork.Branches.Count);
    }

    // ------------------------------------------------------------------ 12. rotazione

    [Fact]
    public void Rotazione_MantieneAlMassimoDueSegmenti()
    {
        var a = NuovoReplica("A", PcA, rotazione: 300, segmenti: 2);
        var nc = new NonConformita { Id = "NC-ROT-001" };
        a.Nc.Save(nc);
        for (var i = 1; i < 10; i++)
        {
            nc.Descrizione = $"Modifica {i}";
            a.Nc.Save(nc, nc.RevisioneDati);
        }

        var cartella = Path.Combine(a.Root, "_ditm", "record-journal", PcA);
        var segmenti = Directory.GetFiles(cartella, "segment-*.ndjson");
        Assert.InRange(segmenti.Length, 1, 2);
        Assert.True(File.Exists(CurrentFile(a, PcA)));
        Assert.Equal(segmenti.Length + 1, Directory.GetFiles(cartella, "*.ndjson").Length);
        Assert.Empty(a.Journal.Refresh());
    }

    // ------------------------------------------------------------------ helper: scenari

    /// <summary>NC creata su A e copiata (JSON e journal) sulle altre repliche; tutte con revisione 1 e indice aggiornato.</summary>
    private static Guid CreaNcSuA(Replica origine, string id, params Replica[] destinazioni)
    {
        var nc = new NonConformita { Id = id, Owner = "Origine" };
        origine.Nc.Save(nc);
        foreach (var destinazione in destinazioni)
        {
            Sync(origine.Root, destinazione.Root);
            destinazione.Nc.RefreshIndex();
        }
        origine.Nc.RefreshIndex();
        return nc.Uid;
    }

    /// <summary>A e B partono entrambi dalla revisione 1 e salvano offline modifiche diverse. Poi il journal di B
    /// arriva ad A, il JSON di B (ultimo scrittore) sovrascrive quello di A e il journal di A arriva a B.</summary>
    private static void DueRamiDallaStessaBase(Replica a, Replica b, string id)
    {
        var ncA = CopiaNc(a, id);
        ncA.Owner = "A";
        a.Nc.Save(ncA, ncA.RevisioneDati);

        var ncB = CopiaNc(b, id);
        ncB.Owner = "B";
        b.Nc.Save(ncB, ncB.RevisioneDati);

        SyncJournals(a.Root, b.Root);
        Sync(b.Root, a.Root);
        a.Nc.RefreshIndex();
    }

    /// <summary>Il Master sceglie la versione presente sul disco di questo PC e la salva dentro la risoluzione.</summary>
    private static void RisolviComeMaster(Replica a, RecordFork fork, string id, string descrizione)
    {
        var scelta = CopiaNc(a, id);
        scelta.Descrizione = descrizione;
        using (a.Journal.BeginResolution(NcDomain, fork.EntityUid))
        {
            a.Nc.Save(scelta, scelta.RevisioneDati);
        }
        var hashScelto = EntityStateFingerprint.Compute(
            JsonIo.TryRead<NonConformita>(a.Paths.NonConformitaFile(id), out _)!);
        a.Journal.MarkResolved(fork, hashScelto, "Master");
    }

    // ------------------------------------------------------------------ helper: repliche e sincronizzazione

    private Replica NuovoReplica(string nome, string device, long? rotazione = null, int? segmenti = null)
    {
        var root = Path.Combine(_base, nome, "shared");
        var local = Path.Combine(_base, nome, "local");
        Directory.CreateDirectory(local);
        var paths = new DataPaths(root);
        paths.EnsureStructure();
        Assert.True(WorkspaceWritePolicy.CanWrite(paths), $"Il workspace di test {nome} non e scrivibile.");

        var journal = new DomainRecordJournalService(paths, local, device, rotazione, segmenti);
        var nc = new NonConformitaRepository(paths, null, new LocalNonConformitaIndexStore(root, local))
        {
            RecordJournal = journal
        };
        var cr = new ChangeRequestRepository(paths, null, local) { RecordJournal = journal };
        var seg = new SegnalazioneRepository(paths) { RecordJournal = journal };
        return new Replica(nome, root, local, paths, journal, nc, cr, seg);
    }

    /// <summary>Consegna OneDrive completa: journal, conflitti e JSON dei record.</summary>
    private static void Sync(string daRoot, string aRoot)
    {
        SyncJournals(daRoot, aRoot);
        SyncConflitti(daRoot, aRoot);
        var da = new DataPaths(daRoot);
        var a = new DataPaths(aRoot);
        CopiaAlbero(da.NonConformitaDir, a.NonConformitaDir, "*.json", piuRecente: false);
        CopiaAlbero(da.ChangeRequestDir, a.ChangeRequestDir, "*.json", piuRecente: false);
        CopiaAlbero(da.SegnalazioniDir, a.SegnalazioniDir, "*.json", piuRecente: false);
    }

    private static void SyncJournals(string daRoot, string aRoot) =>
        CopiaAlbero(Path.Combine(daRoot, "_ditm", "record-journal"),
            Path.Combine(aRoot, "_ditm", "record-journal"), "*", piuRecente: true);

    private static void SyncConflitti(string daRoot, string aRoot) =>
        CopiaAlbero(Path.Combine(daRoot, "_ditm", "conflicts"),
            Path.Combine(aRoot, "_ditm", "conflicts"), "*", piuRecente: true);

    /// <summary>Copia ricorsiva. piuRecente=true: i log e i conflitti non vengono mai regrediti a una copia piu vecchia.
    /// piuRecente=false: sovrascrittura incondizionata (last-writer-wins dei JSON record).</summary>
    private static void CopiaAlbero(string origine, string destinazione, string pattern, bool piuRecente)
    {
        if (!Directory.Exists(origine)) return;
        foreach (var file in Directory.EnumerateFiles(origine, pattern, SearchOption.AllDirectories))
        {
            var target = Path.Combine(destinazione, Path.GetRelativePath(origine, file));
            if (piuRecente && File.Exists(target) &&
                File.GetLastWriteTimeUtc(target) > File.GetLastWriteTimeUtc(file)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }
    }

    // ------------------------------------------------------------------ helper: accesso ai file

    private static string CurrentFile(Replica r, string device) =>
        Path.Combine(r.Root, "_ditm", "record-journal", device, "current.ndjson");

    private static string OutboxFile(Replica r) =>
        Path.Combine(r.LocalDir, "record-journal", "outbox.ndjson");

    private static string ConflittiDir(Replica r) =>
        Path.Combine(r.Root, "_ditm", "conflicts");

    private static string[] RigheNonVuote(string file) =>
        File.Exists(file)
            ? File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray()
            : Array.Empty<string>();

    /// <summary>Eventi leggibili; le righe non interpretabili vengono saltate come fa il servizio.</summary>
    private static List<RecordJournalEvent> EventiValidi(string file)
    {
        var result = new List<RecordJournalEvent>();
        foreach (var line in RigheNonVuote(file))
        {
            try { result.Add(JsonSerializer.Deserialize<RecordJournalEvent>(line, JsonIo.CompactOptions)!); }
            catch (JsonException) { }
        }
        return result;
    }

    /// <summary>Righe del log che non si interpretano come evento: diagnostica per i messaggi di errore.</summary>
    private static string RigheIllegibili(string file)
    {
        var illegibili = new List<string>();
        foreach (var line in RigheNonVuote(file))
        {
            try { JsonSerializer.Deserialize<RecordJournalEvent>(line, JsonIo.CompactOptions); }
            catch (JsonException) { illegibili.Add(line.Length > 90 ? line[..90] + "..." : line); }
        }
        return illegibili.Count == 0 ? "nessuna" : string.Join(" | ", illegibili);
    }

    private static int FileNelSottoalbero(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count() : 0;

    private static NonConformita CopiaNc(Replica r, string id) =>
        EntityStateFingerprint.Clone(r.Nc.GetById(id) ?? throw new InvalidOperationException($"NC {id} assente su {r.Nome}"));

    private static ChangeRequest CopiaCr(Replica r, string id) =>
        EntityStateFingerprint.Clone(r.Cr.GetById(id) ?? throw new InvalidOperationException($"CR {id} assente su {r.Nome}"));

    private static SegnalazioneDaClassificare CopiaSeg(Replica r, Guid uid) =>
        EntityStateFingerprint.Clone(r.Seg.Get(uid) ?? throw new InvalidOperationException($"Segnalazione {uid:N} assente su {r.Nome}"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true); }
        catch { /* eventuale lock SQLite residuo: la cartella resta sotto TEMP */ }
    }
}
