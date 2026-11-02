using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>"Promuovi nel catalogo" per i campi custom liberi (Addendum: audit UX 2026-08-26, nota minore 3 —
/// §18.12 della guida operativa: "un valore inserito lì non comparirà mai come filtro Home/Report né nel
/// catalogo"). Verifica che l'azione crei davvero un campo libero filtrabile nel catalogo Dettagli, sia
/// riservata a un Master, sia idempotente (promuovere due volte la stessa chiave non duplica la voce), e non
/// tocchi mai il valore già scritto sul test (la promozione riguarda solo il catalogo).</summary>
[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class PromuoviCampoCustomTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_PromuoviCampo_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;

    public PromuoviCampoCustomTests() => SeedDemoDataService.Genera(_dir);

    public void Dispose()
    {
        ConfirmService.Handler = _confirmOriginale;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateState(RuoloOperatore ruolo)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var operatore = ruolo == RuoloOperatore.Master
            ? state.Master.Operatori.Load().First(o => o.Ruolo == RuoloOperatore.Master)
            : TestOperatori.Sigilla(new Operatore { Nome = "Operatore Standard", Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash("1234") });
        if (ruolo != RuoloOperatore.Master)
        {
            var operatori = state.Master.Operatori.Load();
            operatori.Add(operatore);
            state.Master.Operatori.Save(operatori);
        }
        Assert.True(state.ImpostaOperatore(operatore.Nome, "1234"));
        return state;
    }

    [Fact]
    public async Task PromuoviCampoCustom_CreaUnCampoLiberoFiltrabileNelCatalogoDettagli()
    {
        var state = CreateState(RuoloOperatore.Master);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.NuovoCampoCustomChiave = "Coppia di serraggio";
        vm.NuovoCampoCustomValore = "45 Nm";
        vm.AggiungiCampoCustomCommand.Execute(null);
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);

        await vm.PromuoviCampoCustomCommand.ExecuteAsync("Coppia di serraggio");

        var campo = state.Master.CampiSistemaDettagli.Load().FirstOrDefault(c => c.Etichetta == "Coppia di serraggio");
        Assert.NotNull(campo);
        Assert.Equal(RuoloCampoSistema.Nessuno, campo!.Ruolo);
        Assert.Equal(GruppoCampoSistema.Dettagli, campo.Gruppo);
        Assert.True(campo.UsabileComeFiltro);
        Assert.False(campo.Archiviata);
        Assert.False(string.IsNullOrWhiteSpace(campo.Chiave)); // chiave macchina generata, non il testo digitato

        // Il valore sul test non viene toccato/migrato dalla promozione: resta solo nel dizionario custom.
        Assert.Equal("45 Nm", vm.TestCase.CampiCustom["Coppia di serraggio"]);
    }

    [Fact]
    public async Task PromuoviCampoCustom_ChiamatoDueVolte_NonDuplicaLaVoceNelCatalogo()
    {
        var state = CreateState(RuoloOperatore.Master);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.NuovoCampoCustomChiave = "Nota banco";
        vm.NuovoCampoCustomValore = "x";
        vm.AggiungiCampoCustomCommand.Execute(null);
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);

        await vm.PromuoviCampoCustomCommand.ExecuteAsync("Nota banco");
        await vm.PromuoviCampoCustomCommand.ExecuteAsync("Nota banco");

        var voci = state.Master.CampiSistemaDettagli.Load().Where(c => c.Etichetta == "Nota banco").ToList();
        Assert.Single(voci);
    }

    [Fact]
    public async Task PromuoviCampoCustom_NonDisponibileAUnOperatoreStandard()
    {
        var state = CreateState(RuoloOperatore.Standard);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.NuovoCampoCustomChiave = "Campo riservato";
        vm.NuovoCampoCustomValore = "y";
        vm.AggiungiCampoCustomCommand.Execute(null);

        Assert.False(vm.PuoPromuovereCampiCustom);

        await vm.PromuoviCampoCustomCommand.ExecuteAsync("Campo riservato");

        Assert.DoesNotContain(state.Master.CampiSistemaDettagli.Load(), c => c.Etichetta == "Campo riservato");
    }
}
