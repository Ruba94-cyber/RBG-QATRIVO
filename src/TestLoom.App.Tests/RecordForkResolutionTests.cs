using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Risoluzione Master di un fork NC fra due postazioni (R9) attraverso AppState reale: il fork è prodotto da due
/// journal (device diversi) sulla stessa cartella, ripristinando la base fra i due salvataggi come farebbe OneDrive
/// consegnando in ritardo la versione dell'altro PC.
/// </summary>
public sealed class RecordForkResolutionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_RecordForkRes_" + Guid.NewGuid().ToString("N"));
    private readonly string _localB = Path.Combine(Path.GetTempPath(), "TestLoom_RecordForkResB_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var path in new[] { _dir, _localB, FailedSaveDraftService.LocalDirectory(_dir) })
            if (Directory.Exists(path)) { try { Directory.Delete(path, true); } catch { } }
    }

    private AppState CreaStato(RuoloOperatore ruolo = RuoloOperatore.Master)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Master", Ruolo = ruolo, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    /// <summary>Questo PC salva Owner="A"; poi la base torna sul disco e un secondo PC salva Owner="B" (vince sul file).</summary>
    private (NonConformita Nc, RecordFork Fork) CreaFork(AppState state)
    {
        Assert.NotNull(state.RecordJournal);
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-FORK-1", Owner = "Base", Descrizione = "KO", Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.Confermata, DataApertura = DateTime.Now
        });
        var path = state.NonConformita.FilePathOf(state.NonConformita.GetById("NC-FORK-1")!);
        var baseBytes = File.ReadAllBytes(path);

        var mia = state.NonConformita.GetById("NC-FORK-1")!.Clona();
        mia.Owner = "A";
        state.NonConformita.Save(mia, mia.RevisioneDati);

        File.WriteAllBytes(path, baseBytes);
        var journalB = new DomainRecordJournalService(state.Paths, _localB, "PC-B-TEST");
        var repoB = new NonConformitaRepository(state.Paths) { RecordJournal = journalB };
        repoB.RefreshPaths(new[] { path }); // l'indice locale condiviso conosce già la revisione 2
        var loro = repoB.GetById("NC-FORK-1")!.Clona();
        loro.Owner = "B";
        repoB.Save(loro, loro.RevisioneDati);
        state.NonConformita.RefreshPaths(new[] { path });

        var fork = Assert.Single(state.RecordJournal!.Refresh());
        Assert.Equal(2, fork.Branches.Count);
        Assert.True(state.RecordJournal.IsBlocked(DomainRecordJournalService.NonConformitaDomain, fork.EntityUid));
        return (state.NonConformita.GetById("NC-FORK-1")!, fork);
    }

    [Fact]
    public void RecordBloccato_ModificaNormaleRifiutataSenzaToccareIlFile()
    {
        using var state = CreaStato();
        var (nc, _) = CreaFork(state);
        var path = state.NonConformita.FilePathOf(nc);
        var prima = File.ReadAllBytes(path);
        var tentativo = nc.Clona();
        tentativo.Owner = "C";

        Assert.Throws<RecordForkOpenException>(() => state.NonConformita.Save(tentativo, tentativo.RevisioneDati));
        Assert.Equal(prima, File.ReadAllBytes(path));
    }

    [Fact]
    public void ApplicaRamoDiQuestoPc_RipristinaLaVersionePersaComeNuovaRevisione()
    {
        using var state = CreaStato();
        var (nc, fork) = CreaFork(state);
        var revisionePrima = nc.RevisioneDati;
        var ramoA = Assert.Single(fork.Branches, b => b.DeviceId == state.RecordJournal!.DeviceId);

        RecordForkResolutionService.ApplyBranch(state, fork, ramoA, "Op Master");

        var corrente = state.NonConformita.GetById("NC-FORK-1")!;
        Assert.Equal("A", corrente.Owner);
        Assert.Equal(revisionePrima + 1, corrente.RevisioneDati);
        Assert.Contains(corrente.Changelog, c => c.Campo == "risoluzione conflitto fra postazioni");
        Assert.Empty(state.RecordJournal!.OpenForks);
        Assert.False(state.RecordJournal.IsBlocked(DomainRecordJournalService.NonConformitaDomain, fork.EntityUid));

        var dopo = corrente.Clona();
        dopo.Owner = "Normale";
        state.NonConformita.Save(dopo, dopo.RevisioneDati); // il record è di nuovo modificabile
    }

    [Fact]
    public void MantieniCorrente_ChiudeIlForkSenzaModificareIlRecord()
    {
        using var state = CreaStato();
        var (nc, fork) = CreaFork(state);
        var path = state.NonConformita.FilePathOf(nc);
        var prima = File.ReadAllBytes(path);

        RecordForkResolutionService.KeepCurrent(state, fork, "Op Master");

        Assert.Equal(prima, File.ReadAllBytes(path));
        Assert.Equal("B", state.NonConformita.GetById("NC-FORK-1")!.Owner);
        Assert.Empty(state.RecordJournal!.Refresh());
    }

    [Fact]
    public void NonMaster_NonPuoRisolvere()
    {
        using var state = CreaStato(RuoloOperatore.Standard);
        var (_, fork) = CreaFork(state);

        Assert.Throws<UnauthorizedAccessException>(() => RecordForkResolutionService.KeepCurrent(state, fork, "Op Master"));
        Assert.Single(state.RecordJournal!.OpenForks);
    }
}
