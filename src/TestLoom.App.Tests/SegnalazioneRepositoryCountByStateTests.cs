using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Addendum prestazioni 2026-09-25 (Home a 100k): DaClassificare usava <c>GetAll().LongCount(...)</c>, che clona
/// OGNI Segnalazione (EntityStateFingerprint.Clone) solo per contarle. <see cref="SegnalazioneRepository.CountByState"/>
/// conta sullo stesso snapshot/overlay senza clonare, e deve restare equivalente a GetAll().Count(...) in ogni
/// scenario: stato multiplo, working-copy dello scope workflow attiva, repository vuoto.
/// </summary>
public sealed class SegnalazioneRepositoryCountByStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_SegnalazioneCount_" + Guid.NewGuid().ToString("N"));

    public SegnalazioneRepositoryCountByStateTests() => new DataPaths(_root).EnsureStructure();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private SegnalazioneDaClassificare Salva(SegnalazioneRepository repo, StatoSegnalazione stato)
    {
        var item = new SegnalazioneDaClassificare { Uid = Guid.NewGuid(), Stato = stato, TestId = "TC-1" };
        repo.Save(item);
        return item;
    }

    [Fact]
    public void CountByState_UgualeAGetAllFiltrato_ConStatiMisti()
    {
        var paths = new DataPaths(_root);
        var repo = new SegnalazioneRepository(paths);
        Salva(repo, StatoSegnalazione.DaClassificare);
        Salva(repo, StatoSegnalazione.DaClassificare);
        Salva(repo, StatoSegnalazione.Classificata);
        repo.RefreshIndex();

        var attesi = repo.GetAll().LongCount(s => s.Stato == StatoSegnalazione.DaClassificare);
        Assert.Equal(2, attesi);
        Assert.Equal(attesi, repo.CountByState(StatoSegnalazione.DaClassificare));
        Assert.Equal(1, repo.CountByState(StatoSegnalazione.Classificata));
    }

    [Fact]
    public void CountByState_RepositoryVuoto_RitornaZero()
    {
        var repo = new SegnalazioneRepository(new DataPaths(_root));
        repo.RefreshIndex();

        Assert.Equal(0, repo.CountByState(StatoSegnalazione.DaClassificare));
    }

    /// <summary>La working-copy dello scope workflow deve valere anche per il conteggio, non solo per GetAll:
    /// altrimenti un salvataggio in corso nello stesso flusso produrrebbe un conteggio stantio.</summary>
    [Fact]
    public void CountByState_RiflettteLaWorkingCopyDelloScopeWorkflow()
    {
        var paths = new DataPaths(_root);
        var repo = new SegnalazioneRepository(paths);
        var item = Salva(repo, StatoSegnalazione.DaClassificare);
        repo.RefreshIndex();

        var working = EntityStateFingerprint.Clone(item);
        working.Stato = StatoSegnalazione.Classificata;
        using (WorkflowWorkingCopyScope.Begin(segnalazioni: new[] { working }))
        {
            Assert.Equal(0, repo.CountByState(StatoSegnalazione.DaClassificare));
            Assert.Equal(1, repo.CountByState(StatoSegnalazione.Classificata));
        }

        // Fuori dallo scope torna al dato autoritativo su disco.
        Assert.Equal(1, repo.CountByState(StatoSegnalazione.DaClassificare));
    }
}
