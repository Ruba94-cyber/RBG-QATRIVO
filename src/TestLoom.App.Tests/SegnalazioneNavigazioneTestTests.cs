using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Cross-navigazione Segnalazione -> Test di origine (Nc.TestCaseId): il link usa il callback di navigazione dei
/// Test gia esistente in MainViewModel (NavigaATestCase) e compare solo se c'e un Test collegato e un callback.</summary>
[Collection("Localization")]
public sealed class SegnalazioneNavigazioneTestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_SegNav_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        return state;
    }

    private static void SalvaSegnalazione(AppState state, string testCaseId)
    {
        state.NonConformita.Save(new NonConformita
        {
            Id = "SEG-0001", DaSegnalazioneRete = true, TestCaseId = testCaseId, Owner = "Mario",
            Descrizione = "Segnalazione di prova", DataApertura = DateTime.Now, StatoWorkflow = StatoAnomalia.Confermata
        });
    }

    [Fact]
    public void SegnalazioneConTestDiOrigine_MostraIlLink_ApriTestCollegatoInvocaIlCallbackConIlTest()
    {
        using var state = CreaStato();
        SalvaSegnalazione(state, "T-1");
        string? aperto = null;
        var vm = new SegnalazioneReteEditorViewModel(state, "SEG-0001", () => { }, null, id => aperto = id);

        Assert.True(vm.HaTestCollegatoLink);
        Assert.Equal("T-1", vm.TestCollegatoLink);
        vm.ApriTestCollegatoCommand.Execute(null);
        Assert.Equal("T-1", aperto);
    }

    [Fact]
    public void SegnalazioneSenzaTestDiOrigine_NessunLink_IlCommandoNonNavigaAlcuno()
    {
        using var state = CreaStato();
        SalvaSegnalazione(state, "");
        string? aperto = null;
        var vm = new SegnalazioneReteEditorViewModel(state, "SEG-0001", () => { }, null, id => aperto = id);

        Assert.False(vm.HaTestCollegatoLink);
        vm.ApriTestCollegatoCommand.Execute(null);
        Assert.Null(aperto);
    }

    [Fact]
    public void SegnalazioneSenzaCallbackDiNavigazione_NessunLink()
    {
        using var state = CreaStato();
        SalvaSegnalazione(state, "T-1");
        var vm = new SegnalazioneReteEditorViewModel(state, "SEG-0001", () => { });

        Assert.False(vm.HaTestCollegatoLink);
    }
}
