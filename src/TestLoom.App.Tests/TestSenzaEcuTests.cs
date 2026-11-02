using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Piano 6.4.0 punto 5(3): l'assenza di componente su un Test non deve più produrre solo righe/colonne
/// vuote o etichettate "—" incomprensibili (feedback utente: "non si sa a cosa serva e cosa sia"), ma un
/// conteggio esplicito e cliccabile sulla Home più un filtro dedicato nella pagina Test. Nota: per decisione
/// esplicita dell'utente il componente resta facoltativa (nessuna validazione bloccante viene introdotta qui, né in
/// TestCaseEditorViewModel né negli import).</summary>
/// <remarks>[Collection("Localization")]: l'ultimo test muta TextEditService.Handler, statico e condiviso da
/// tutto il processo — va serializzato con ogni altro test che tocca lo stesso Handler (stesso motivo di
/// VisteSalvateTests/TestCaseListBulkActionsTests).</remarks>
[Collection("Localization")]
public sealed class TestSenzaEcuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_senza_ecu_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, Task<string?>>? _textEditOriginale = TextEditService.Handler;

    private AppState CreaStatoComeMaster()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        return state;
    }

    public void Dispose()
    {
        TextEditService.Handler = _textEditOriginale;
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void HomeViewModel_ContaEMostraAvvisoSoloSeCiSonoTestSenzaEcu()
    {
        var state = CreaStatoComeMaster();
        state.TestCases.Save(new TestCase { Id = "TC-1", Componente = new EcuSelection(new[] { "BCM" }) }, isNew: true);
        state.TestCases.Save(new TestCase { Id = "TC-2", Componente = new EcuSelection() }, isNew: true);
        state.TestCases.Save(new TestCase { Id = "TC-3", Componente = new EcuSelection() }, isNew: true);

        var vm = new HomeViewModel(state, _ => { });

        Assert.Equal(2, vm.TestSenzaEcu);
        Assert.True(vm.MostraAvvisoTestSenzaEcu);
        Assert.Contains("2", vm.TestSenzaEcuTesto);
    }

    [Fact]
    public void HomeViewModel_NessunAvviso_QuandoTuttiITestHannoEcu()
    {
        var state = CreaStatoComeMaster();
        state.TestCases.Save(new TestCase { Id = "TC-1", Componente = new EcuSelection(new[] { "BCM" }) }, isNew: true);

        var vm = new HomeViewModel(state, _ => { });

        Assert.Equal(0, vm.TestSenzaEcu);
        Assert.False(vm.MostraAvvisoTestSenzaEcu);
    }

    [Fact]
    public void HomeViewModel_VaiATestSenzaEcuCommand_InvocaIlCallbackDiNavigazione()
    {
        var state = CreaStatoComeMaster();
        var chiamato = 0;
        var vm = new HomeViewModel(state, _ => { }, () => chiamato++);

        vm.VaiATestSenzaEcuCommand.Execute(null);

        Assert.Equal(1, chiamato);
    }

    [Fact]
    public void TestCaseListViewModel_FiltroSoloSenzaEcu_MostraSoloITestSenzaEcu()
    {
        var state = CreaStatoComeMaster();
        state.TestCases.Save(new TestCase { Id = "TC-1", Componente = new EcuSelection(new[] { "BCM" }) }, isNew: true);
        state.TestCases.Save(new TestCase { Id = "TC-2", Componente = new EcuSelection() }, isNew: true);
        var vm = new TestCaseListViewModel(state, _ => { });

        vm.FiltroSoloSenzaEcu = true;

        Assert.Single(vm.Righe);
        Assert.Equal("TC-2", vm.Righe[0].Id);
        Assert.True(vm.HaFiltriAttivi);
    }

    [Fact]
    public void TestCaseListViewModel_RimuoviFiltri_AzzeraAncheFiltroSoloSenzaEcu()
    {
        var state = CreaStatoComeMaster();
        state.TestCases.Save(new TestCase { Id = "TC-1", Componente = new EcuSelection(new[] { "BCM" }) }, isNew: true);
        state.TestCases.Save(new TestCase { Id = "TC-2", Componente = new EcuSelection() }, isNew: true);
        var vm = new TestCaseListViewModel(state, _ => { }) { FiltroSoloSenzaEcu = true };

        vm.ReimpostaFiltriCommand.Execute(null);

        Assert.False(vm.FiltroSoloSenzaEcu);
        Assert.False(vm.HaFiltriAttivi);
        Assert.Equal(2, vm.Righe.Count);
    }

    [Fact]
    public async Task TestCaseListViewModel_FiltroSoloSenzaEcu_SopravviveAUnaVistaSalvata()
    {
        var state = CreaStatoComeMaster();
        state.TestCases.Save(new TestCase { Id = "TC-1", Componente = new EcuSelection(new[] { "BCM" }) }, isNew: true);
        state.TestCases.Save(new TestCase { Id = "TC-2", Componente = new EcuSelection() }, isNew: true);
        var vm = new TestCaseListViewModel(state, _ => { }) { FiltroSoloSenzaEcu = true };
        TextEditService.Handler = (_, _) => Task.FromResult<string?>("Vista senza componente");

        await vm.SalvaVistaCorrenteCommand.ExecuteAsync(null);

        // Una seconda istanza (stessa AppState, come dopo una riapertura della pagina Test) parte con
        // VistaSelezionata=null: assegnarla forza davvero ApplicaVista, a differenza di riassegnare sulla
        // stessa istanza lo stesso riferimento appena salvato (nessun cambiamento, nessun evento).
        var vm2 = new TestCaseListViewModel(state, _ => { });
        var vista = Assert.Single(vm2.VisteSalvate);
        vm2.VistaSelezionata = vista;

        Assert.True(vm2.FiltroSoloSenzaEcu);
        Assert.Single(vm2.Righe);
        Assert.Equal("TC-2", vm2.Righe[0].Id);
    }
}
