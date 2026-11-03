using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public sealed class QuickFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_QuickFlow_" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _opened = new();
    private AppState? _state;

    private TestCaseEditorViewModel CreaVm(int operazioni = 3)
    {
        SeedDemoDataService.Genera(_dir);
        _state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        _state.InizializzaCartella(_dir);
        var vm = new TestCaseEditorViewModel(_state, null, () => { }, _ => { }) { ModoEditorRapido = true };
        _opened.Add(_state);
        _opened.Add(vm);
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        while (vm.RevisioneCorrente!.Condizioni.Count < operazioni) vm.AggiungiCondizioneCommand.Execute(null);
        return vm;
    }

    [Fact]
    public async Task NextTodoSkipsExecutedWrapsAndReturnsNullWhenNothingIsLeft()
    {
        var vm = CreaVm();
        var op = vm.RevisioneCorrente!.Condizioni;
        Assert.Same(op[1], vm.TrovaProssimaDaEseguire(op[0]));
        await vm.EsitoRapidoConformeCommand.ExecuteAsync(op[1]);
        Assert.Same(op[2], vm.TrovaProssimaDaEseguire(op[0]));
        await vm.EsitoRapidoNonConformeCommand.ExecuteAsync(op[2]);
        Assert.Same(op[0], vm.TrovaProssimaDaEseguire(op[2])); // wraps around
        await vm.EsitoRapidoConformeCommand.ExecuteAsync(op[0]);
        Assert.Null(vm.TrovaProssimaDaEseguire(null));
    }

    [Fact]
    public async Task MarkRestOkOnlyTouchesOperationsStillToDoAndKeepsOtherOutcomes()
    {
        var vm = CreaVm();
        var op = vm.RevisioneCorrente!.Condizioni;
        await vm.EsitoRapidoNonConformeCommand.ExecuteAsync(op[0]);
        Assert.True(vm.PuoSegnareRestantiOk);
        Assert.Equal(2, vm.OperazioniRestantiRapide);

        await vm.SegnaRestantiOkCommand.ExecuteAsync(null);

        Assert.Equal(Esito.NonConforme, op[0].Esito); // an executed outcome is never overwritten
        Assert.All(op.Skip(1), c => Assert.Equal(Esito.Conforme, c.Esito));
        Assert.Equal(0, vm.OperazioniRestantiRapide);
        Assert.False(vm.PuoSegnareRestantiOk);
        Assert.True(vm.ModificheNonSalvate);
    }

    [Fact]
    public async Task MarkRestOkIsDeclinedWhenTheUserSaysNo()
    {
        var vm = CreaVm();
        var originale = ConfirmService.Handler;
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(false);
        try
        {
            await vm.SegnaRestantiOkCommand.ExecuteAsync(null);
            Assert.All(vm.RevisioneCorrente!.Condizioni, c => Assert.Equal(Esito.DaEseguire, c.Esito));
        }
        finally { ConfirmService.Handler = originale; }
    }

    [Fact]
    public async Task MarkRestOkLeavesOperationsWithEarlierReasonsAloneAndSaysSo()
    {
        var vm = CreaVm();
        var op = vm.RevisioneCorrente!.Condizioni;
        op[1].DescrizioneProblema = "motivazione precedente";
        await vm.SegnaRestantiOkCommand.ExecuteAsync(null);
        Assert.Equal(Esito.Conforme, op[0].Esito);
        Assert.Equal(Esito.DaEseguire, op[1].Esito); // would otherwise trigger its own confirmation: left for a one-by-one decision
        Assert.Equal(Esito.Conforme, op[2].Esito);
        Assert.Contains(vm.Avvisi, a => a.Contains("1"));
        Assert.Equal("motivazione precedente", op[1].DescrizioneProblema);
    }

    [Fact]
    public async Task NotApplicableAndResetQuickOutcomesWork()
    {
        var vm = CreaVm(1);
        var op = vm.RevisioneCorrente!.Condizioni[0];
        await vm.EsitoRapidoNonApplicabileCommand.ExecuteAsync(op);
        Assert.Equal(Esito.NonApplicabile, op.Esito);
        Assert.True(op.RichiedeMotivazioneNonApplicabile);
        await vm.EsitoRapidoDaEseguireCommand.ExecuteAsync(op);
        Assert.Equal(Esito.DaEseguire, op.Esito);
    }

    [Fact]
    public void ShowAllFieldsPreferenceIsRememberedPerOperatorAndNeverStoredInTheTest()
    {
        var vm = CreaVm(1);
        Assert.False(vm.MostraTuttiICampiRapido);
        vm.MostraTuttiICampiRapido = true;
        var datiTest = System.Text.Json.JsonSerializer.Serialize(vm.TestCase, JsonIo.Options);
        Assert.DoesNotContain("AllFields", datiTest);

        var successivo = new TestCaseEditorViewModel(_state!, null, () => { }, _ => { });
        _opened.Add(successivo);
        Assert.True(successivo.MostraTuttiICampiRapido);
        successivo.MostraTuttiICampiRapido = false;
        var ancora = new TestCaseEditorViewModel(_state!, null, () => { }, _ => { });
        _opened.Add(ancora);
        Assert.False(ancora.MostraTuttiICampiRapido);
    }

    [Fact]
    public void DetailsSummaryAndStatusAreOnlyForExistingTests()
    {
        var vm = CreaVm(1);
        Assert.True(vm.IsNew);
        Assert.False(vm.HaRiepilogoDettagliRapido);
        Assert.False(vm.HaStatoEstaRapido);
        Assert.True(vm.HaOperazioniRapide);
        vm.AttivaModalitaCompletaCommand.Execute(null);
        Assert.False(vm.HaOperazioniRapide);
    }

    public void Dispose()
    {
        foreach (var item in _opened.AsEnumerable().Reverse()) item.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
