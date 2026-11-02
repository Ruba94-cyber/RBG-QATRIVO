using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Fork NC fra due postazioni con copie di conflitto OneDrive VISIBILI (6.6.0): (1) la scelta del Master non deve
/// bloccarsi sulla copia del ramo scelto; (2) il ramo scelto esce dalla cartella condivisa e finisce nel recovery-bin;
/// (3) dopo la risoluzione una copia tardiva di un ramo chiuso non blocca i salvataggi; (4) una copia di contenuto
/// sconosciuto continua a bloccare e nulla viene scritto.
/// </summary>
public sealed class RecordForkCopieVisibiliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_RecordForkCopie_" + Guid.NewGuid().ToString("N"));
    private readonly string _localB = Path.Combine(Path.GetTempPath(), "TestLoom_RecordForkCopieB_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var path in new[] { _dir, _localB, FailedSaveDraftService.LocalDirectory(_dir) })
            if (Directory.Exists(path)) { try { Directory.Delete(path, true); } catch { } }
    }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Master", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    /// <summary>Stessa costruzione di RecordForkResolutionTests: A salva Owner="A", poi B salva Owner="B" dalla stessa base.</summary>
    private (NonConformita Nc, RecordFork Fork, string Path) CreaFork(AppState state)
    {
        Assert.NotNull(state.RecordJournal);
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-COPIE-1", Owner = "Base", Descrizione = "KO", Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.Confermata, DataApertura = DateTime.Now
        });
        var path = state.NonConformita.FilePathOf(state.NonConformita.GetById("NC-COPIE-1")!);
        var baseBytes = File.ReadAllBytes(path);

        var mia = state.NonConformita.GetById("NC-COPIE-1")!.Clona();
        mia.Owner = "A";
        state.NonConformita.Save(mia, mia.RevisioneDati);

        File.WriteAllBytes(path, baseBytes);
        var journalB = new DomainRecordJournalService(state.Paths, _localB, "PC-B-TEST");
        var repoB = new NonConformitaRepository(state.Paths) { RecordJournal = journalB };
        repoB.RefreshPaths(new[] { path });
        var loro = repoB.GetById("NC-COPIE-1")!.Clona();
        loro.Owner = "B";
        repoB.Save(loro, loro.RevisioneDati);
        state.NonConformita.RefreshPaths(new[] { path });

        var fork = Assert.Single(state.RecordJournal!.Refresh());
        Assert.Equal(2, fork.Branches.Count);
        return (state.NonConformita.GetById("NC-COPIE-1")!, fork, path);
    }

    /// <summary>Copia di conflitto OneDrive accanto al canonico, con il payload del ramo indicato.</summary>
    private static string ScriviCopiaDelRamo(AppState state, RecordFork fork, RecordForkBranch ramo, string canonicalPath, string suffisso)
    {
        var json = state.RecordJournal!.TryReadBranchPayload(fork, ramo);
        Assert.NotNull(json);
        var copia = canonicalPath.Replace(".json", $" ({suffisso}).json", StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(copia, json);
        return copia;
    }

    [Fact]
    public void ApplicaRamo_CopiaVisibileDelRamoScelto_NonBloccaLaSceltaDelMaster()
    {
        using var state = CreaStato();
        var (_, fork, path) = CreaFork(state);
        var ramoA = Assert.Single(fork.Branches, b => b.DeviceId == state.RecordJournal!.DeviceId);
        ScriviCopiaDelRamo(state, fork, ramoA, path, "PC-A");

        RecordForkResolutionService.ApplyBranch(state, fork, ramoA, "Op Master");

        Assert.Equal("A", state.NonConformita.GetById("NC-COPIE-1")!.Owner);
        Assert.Empty(state.RecordJournal!.OpenForks);
    }

    [Fact]
    public void ApplicaRamo_CopiaVisibileDelRamoScelto_VieneArchiviataNelRecoveryBin()
    {
        using var state = CreaStato();
        var (_, fork, path) = CreaFork(state);
        var ramoA = Assert.Single(fork.Branches, b => b.DeviceId == state.RecordJournal!.DeviceId);
        var copia = ScriviCopiaDelRamo(state, fork, ramoA, path, "PC-A");

        RecordForkResolutionService.ApplyBranch(state, fork, ramoA, "Op Master");

        Assert.True(!File.Exists(copia) || WorkflowTransactionVisibilityService.IsCommittedTombstone(state.Paths, copia),
            "La copia del ramo scelto deve uscire dalla cartella condivisa (resta solo il tombstone TestLoom).");
        Assert.Contains(DomainRecoveryService.GetAll(state.Paths),
            r => r.Category == DomainRecoveryCategory.NonConformita && r.OriginalFileName == Path.GetFileName(copia));
    }

    [Fact]
    public void DopoRisoluzione_CopiaTardivaDelRamoChiuso_NonBloccaIlSalvataggio()
    {
        using var state = CreaStato();
        var (_, fork, path) = CreaFork(state);
        var ramoA = Assert.Single(fork.Branches, b => b.DeviceId == state.RecordJournal!.DeviceId);
        RecordForkResolutionService.ApplyBranch(state, fork, ramoA, "Op Master");

        // La copia del ramo chiuso arriva dopo la risoluzione (OneDrive in ritardo su questo PC).
        ScriviCopiaDelRamo(state, fork, ramoA, path, "PC-A");

        var dopo = state.NonConformita.GetById("NC-COPIE-1")!.Clona();
        dopo.Owner = "Normale dopo risoluzione";
        state.NonConformita.Save(dopo, dopo.RevisioneDati);

        Assert.Equal("Normale dopo risoluzione", state.NonConformita.GetById("NC-COPIE-1")!.Owner);
    }

    [Fact]
    public void CopiaDiContenutoSconosciuto_BloccaLaRisoluzioneSenzaScrivere()
    {
        using var state = CreaStato();
        var (nc, fork, path) = CreaFork(state);
        var ramoA = Assert.Single(fork.Branches, b => b.DeviceId == state.RecordJournal!.DeviceId);
        var sconosciuta = path.Replace(".json", " (PC-ignoto).json", StringComparison.OrdinalIgnoreCase);
        var altra = EntityStateFingerprint.Clone(nc);
        altra.Owner = "Versione mai vista";
        JsonIo.WriteAtomic(sconosciuta, altra);
        var canonicoPrima = File.ReadAllBytes(path);
        var sconosciutaPrima = File.ReadAllBytes(sconosciuta);

        Assert.Throws<IOException>(() => RecordForkResolutionService.ApplyBranch(state, fork, ramoA, "Op Master"));

        Assert.Equal(canonicoPrima, File.ReadAllBytes(path));
        Assert.Equal(sconosciutaPrima, File.ReadAllBytes(sconosciuta));
    }
    /// <summary>Change Request: stessa forma del fork NC (base, A salva, B salva dalla base vecchia).</summary>
    private (RecordFork Fork, string Path) CreaForkCr(AppState state, string id)
    {
        state.ChangeRequests.Save(new ChangeRequest { Id = id, Titolo = "Base" });
        var path = state.Paths.ChangeRequestFile(id);
        var baseBytes = File.ReadAllBytes(path);

        var mia = EntityStateFingerprint.Clone(state.ChangeRequests.GetAll(includeArchiviate: true).Single(c => c.Id == id));
        mia.Titolo = "Titolo di A";
        state.ChangeRequests.Save(mia, mia.RevisioneDati);

        File.WriteAllBytes(path, baseBytes);
        var journalB = new DomainRecordJournalService(state.Paths, _localB, "PC-B-TEST");
        var repoB = new ChangeRequestRepository(state.Paths) { RecordJournal = journalB };
        repoB.RefreshIndex();
        var loro = EntityStateFingerprint.Clone(repoB.GetAll(includeArchiviate: true).Single(c => c.Id == id));
        loro.Titolo = "Titolo di B";
        repoB.Save(loro, loro.RevisioneDati);
        state.ChangeRequests.RefreshIndex();

        var fork = Assert.Single(state.RecordJournal!.Refresh());
        Assert.Equal(DomainRecordJournalService.ChangeRequestDomain, fork.Domain);
        Assert.Equal(2, fork.Branches.Count);
        return (fork, path);
    }

    [Fact]
    public void ChangeRequest_CopiaVisibileDelRamoScelto_NonBloccaLaSceltaEVieneArchiviata()
    {
        using var state = CreaStato();
        var (fork, path) = CreaForkCr(state, "CR-COPIE-1");
        var ramoA = Assert.Single(fork.Branches, b => b.DeviceId == state.RecordJournal!.DeviceId);
        var copia = ScriviCopiaDelRamo(state, fork, ramoA, path, "PC-A");

        RecordForkResolutionService.ApplyBranch(state, fork, ramoA, "Op Master");

        Assert.Equal("Titolo di A", state.ChangeRequests.GetAll(includeArchiviate: true).Single(c => c.Id == "CR-COPIE-1").Titolo);
        Assert.True(!File.Exists(copia) || WorkflowTransactionVisibilityService.IsCommittedTombstone(state.Paths, copia));
        Assert.Contains(DomainRecoveryService.GetAll(state.Paths),
            r => r.Category == DomainRecoveryCategory.ChangeRequest && r.OriginalFileName == Path.GetFileName(copia));
    }

    [Fact]
    public void Segnalazione_CopiaVisibileDelRamoScelto_NonBloccaLaSceltaEVieneArchiviata()
    {
        using var state = CreaStato();
        var seg = new SegnalazioneDaClassificare { MotivazioneEsclusione = "Origine" };
        state.Segnalazioni.Save(seg);
        var uid = seg.Uid;
        var path = state.Paths.SegnalazioneFile(uid);
        var baseBytes = File.ReadAllBytes(path);

        var mia = EntityStateFingerprint.Clone(state.Segnalazioni.Get(uid)!);
        mia.MotivazioneEsclusione = "Motivo di A";
        state.Segnalazioni.Save(mia, mia.RevisioneDati);

        File.WriteAllBytes(path, baseBytes);
        var journalB = new DomainRecordJournalService(state.Paths, _localB, "PC-B-TEST");
        var repoB = new SegnalazioneRepository(state.Paths) { RecordJournal = journalB };
        repoB.RefreshIndex();
        var loro = EntityStateFingerprint.Clone(repoB.Get(uid)!);
        loro.MotivazioneEsclusione = "Motivo di B";
        repoB.Save(loro, loro.RevisioneDati);
        state.Segnalazioni.RefreshIndex();

        var fork = Assert.Single(state.RecordJournal!.Refresh());
        Assert.Equal(DomainRecordJournalService.SegnalazioneDomain, fork.Domain);
        var ramoA = Assert.Single(fork.Branches, b => b.DeviceId == state.RecordJournal!.DeviceId);
        var copia = ScriviCopiaDelRamo(state, fork, ramoA, path, "PC-A");

        RecordForkResolutionService.ApplyBranch(state, fork, ramoA, "Op Master");

        Assert.Equal("Motivo di A", state.Segnalazioni.Get(uid)!.MotivazioneEsclusione);
        Assert.True(!File.Exists(copia) || WorkflowTransactionVisibilityService.IsCommittedTombstone(state.Paths, copia));
        Assert.Contains(DomainRecoveryService.GetAll(state.Paths),
            r => r.Category == DomainRecoveryCategory.Segnalazione && r.OriginalFileName == Path.GetFileName(copia));
    }
}
