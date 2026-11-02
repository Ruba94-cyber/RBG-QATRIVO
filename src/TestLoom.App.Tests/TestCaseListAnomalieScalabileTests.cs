using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Annotazione anomalie/Ticketing sulle righe visibili nel percorso SCALABILE (feedback utente
/// 2026-09-17, volumi 100k): NonConformitaRepository.GetByTestIds deve restituire, per le sole righe
/// effettivamente mostrate, lo stesso risultato che prima si otteneva clonando l'intero archivio NC.</summary>
public sealed class TestCaseListAnomalieScalabileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_TestListAnomalieScalabile_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateStateConIndiceScalabile()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);

        state.TestCases.Save(new TestCase { Id = "TC-A", Componente = "BCM" }, isNew: true);
        state.TestCases.Save(new TestCase { Id = "TC-B", Componente = "BCM" }, isNew: true);
        state.TestCases.Save(new TestCase { Id = "TC-C", Componente = "BCM" }, isNew: true);

        var ncAperta = new NonConformita { Id = "NC-1", TestCaseId = "TC-A", TestUid = state.TestCases.GetById("TC-A")!.Uid, StatoWorkflow = StatoAnomalia.Confermata };
        state.NonConformita.Save(ncAperta);
        var ncChiusa = new NonConformita { Id = "NC-2", TestCaseId = "TC-B", TestUid = state.TestCases.GetById("TC-B")!.Uid, StatoWorkflow = StatoAnomalia.Conclusa };
        state.NonConformita.Save(ncChiusa);
        var ncConPolarion = new NonConformita
        {
            Id = "NC-3", TestCaseId = "TC-C", TestUid = state.TestCases.GetById("TC-C")!.Uid, StatoWorkflow = StatoAnomalia.Confermata,
            CollegamentiPolarion = new List<CollegamentoPolarion> { new() { Codice = "TICKET-1" } }
        };
        state.NonConformita.Save(ncConPolarion);

        // Attiva il percorso scalabile: senza una full reconciliation, HasPersistentSnapshot resta false e
        // TestCaseListViewModel userebbe sempre il fallback legacy (mai il caso qui sotto test).
        state.TestCases.RefreshIndex();
        Assert.True(state.TestCases.HasPersistentSnapshot);
        return state;
    }

    [Fact]
    public void PercorsoScalabile_AnnotaAnomalieAperteEChiuseSolamenteSulleRigheVisibili()
    {
        var state = CreateStateConIndiceScalabile();
        var vm = new TestCaseListViewModel(state, _ => { });

        var righeOrdinate = vm.Righe.ToDictionary(tc => tc.Id);
        Assert.Equal(3, righeOrdinate.Count);

        Assert.Contains(StatoAnomalia.Confermata, righeOrdinate["TC-A"].StatiAnomalieAperteVisual);
        Assert.Equal(0, righeOrdinate["TC-A"].AnomalieChiuseVisual);
        Assert.False(righeOrdinate["TC-A"].HaPolarionVisual);

        Assert.Empty(righeOrdinate["TC-B"].StatiAnomalieAperteVisual);
        Assert.Equal(1, righeOrdinate["TC-B"].AnomalieChiuseVisual);

        Assert.True(righeOrdinate["TC-C"].HaPolarionVisual);
    }

    [Fact]
    public void GetByTestIds_TrovaAncheLeNcCollegateComeProvaESoloQuelle()
    {
        var state = CreateStateConIndiceScalabile();
        var provaCollegata = new NonConformita
        {
            Id = "NC-4", TestCaseId = "",
            ProveCollegate = new List<RiferimentoProva> { new() { TestId = "TC-B", TestUid = state.TestCases.GetById("TC-B")!.Uid } },
        };
        state.NonConformita.Save(provaCollegata);

        var risultato = state.NonConformita.GetByTestIds(new[] { "TC-B" });

        Assert.Contains(risultato, n => n.Id == "NC-2");
        Assert.Contains(risultato, n => n.Id == "NC-4");
        Assert.DoesNotContain(risultato, n => n.Id == "NC-1" || n.Id == "NC-3");
    }

    [Fact]
    public void GetByTestIds_ConElencoVuoto_NonRestituisceNulla()
    {
        var state = CreateStateConIndiceScalabile();
        Assert.Empty(state.NonConformita.GetByTestIds(Array.Empty<string>()));
    }
}
