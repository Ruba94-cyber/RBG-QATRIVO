using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

public sealed class UiUxExperienceAndResponsivenessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_UiUxTests_" + Guid.NewGuid().ToString("N"));

    public UiUxExperienceAndResponsivenessTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, true); } catch { }
        }
    }

    private AppState CreateStateWithTestCases(int count)
    {
        var state = TestStateFactory.Create();
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Tester UX", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Tester UX", "1234");

        for (int i = 1; i <= count; i++)
        {
            state.TestCases.Save(new TestCase
            {
                Id = $"TC-{i:D4}",
                Componente = i % 2 == 0 ? "BCM" : "EDC",
                Veicolo = "Daily MY24",
                Descrizione = $"Scenario di collaudo UX {i}",
                CreatoDa = "Tester UX"
            }, isNew: true);
        }

        return state;
    }

    [Fact]
    public void Paging_NextAndPreviousPage_NavigatesSmoothlyWithoutDataLoss()
    {
        // 260 elementi: Pagina 1 (250 items - PageSize di default), Pagina 2 (10 items)
        var state = CreateStateWithTestCases(260);
        var vm = new TestCaseListViewModel(state, _ => { });

        Assert.Equal(1, vm.PaginaCorrente);
        Assert.Equal(250, vm.Righe.Count);
        Assert.True(vm.PuoPaginaSuccessiva);
        Assert.False(vm.PuoPaginaPrecedente);

        // Pagina successiva
        vm.PaginaSuccessivaCommand.Execute(null);
        Assert.Equal(2, vm.PaginaCorrente);
        Assert.Equal(10, vm.Righe.Count);
        Assert.False(vm.PuoPaginaSuccessiva);
        Assert.True(vm.PuoPaginaPrecedente);

        // Tentativo di oltrepassare l'ultima pagina (guardrail UX)
        vm.PaginaSuccessivaCommand.Execute(null);
        Assert.Equal(2, vm.PaginaCorrente);
        Assert.Equal(10, vm.Righe.Count);

        // Torna alla prima pagina
        vm.PaginaPrecedenteCommand.Execute(null);
        Assert.Equal(1, vm.PaginaCorrente);
        Assert.Equal(250, vm.Righe.Count);
        Assert.True(vm.PuoPaginaSuccessiva);
        Assert.False(vm.PuoPaginaPrecedente);
    }

    [Fact]
    public void SavedViews_SaveAndRestoreFilterConfiguration_RestoresAccurateState()
    {
        var state = CreateStateWithTestCases(30);

        // Prepariamo una vista salvata in configurazione
        state.Config.VisteSalvate["test-list"] = new List<VistaSalvataFiltri>
        {
            new()
            {
                Nome = "Vista Solo BCM",
                Valori = new Dictionary<string, string?>
                {
                    ["Componente"] = "BCM",
                    ["Testo"] = "Scenario"
                }
            }
        };
        state.ConfigService.Save(state.Config);

        var vm = new TestCaseListViewModel(state, _ => { });
        Assert.NotEmpty(vm.VisteSalvate);
        var targetView = vm.VisteSalvate.First(v => v.Nome == "Vista Solo BCM");

        // Applica vista salvata
        vm.VistaSelezionata = targetView;
        Assert.Equal(new[] { "BCM" }, vm.FiltroEcu.Selezionati);
        Assert.Equal("Scenario", vm.FiltroTesto);
        Assert.True(vm.Righe.Count > 0);
        Assert.All(vm.Righe, r => Assert.Equal("BCM", r.Componente));

        // Resetta filtri e riapplica
        vm.ReimpostaFiltriCommand.Execute(null);
        Assert.Null(vm.VistaSelezionata);
        Assert.False(vm.FiltroEcu.HaSelezione);
        Assert.Equal("", vm.FiltroTesto);
        Assert.Equal(30, vm.Righe.Count);

        // Riapplica la vista salvata
        vm.VistaSelezionata = targetView;
        Assert.Equal(new[] { "BCM" }, vm.FiltroEcu.Selezionati);
        Assert.Equal("Scenario", vm.FiltroTesto);
        Assert.All(vm.Righe, r => Assert.Equal("BCM", r.Componente));
    }

    [Fact]
    public void NonConformita_FastPathPagination_LoadsSubPageWithoutFullHydration()
    {
        var state = CreateStateWithTestCases(10);
        for (int i = 1; i <= 260; i++)
        {
            state.NonConformita.Save(new NonConformita
            {
                Id = $"NC-{i:D4}",
                DataApertura = DateTime.Now,
                Severita = SeveritaAnomalia.High,
                TestCaseId = "TC-0001",
                Descrizione = $"Anomalia di collaudo {i}"
            });
        }

        var vm = new NonConformitaViewModel(state, _ => { });

        // Prima pagina deve contenere esattamente la dimensione pagina (250)
        Assert.Equal(1, vm.PaginaRighe);
        Assert.Equal(250, vm.Righe.Count);
        Assert.True(vm.PuoPaginaRigheSuccessiva);
        Assert.False(vm.PuoPaginaRighePrecedente);

        // Pagina successiva: i restanti 10
        vm.PaginaRigheSuccessivaCommand.Execute(null);
        Assert.Equal(2, vm.PaginaRighe);
        Assert.Equal(10, vm.Righe.Count);
        Assert.False(vm.PuoPaginaRigheSuccessiva);
        Assert.True(vm.PuoPaginaRighePrecedente);

        // Torna alla prima pagina
        vm.PaginaRighePrecedenteCommand.Execute(null);
        Assert.Equal(1, vm.PaginaRighe);
        Assert.Equal(250, vm.Righe.Count);
    }

    [Fact]
    public void QuickFilters_AssigneeAndMine_ReflectsActiveOperatorFilter()
    {
        var state = CreateStateWithTestCases(20);
        var vm = new TestCaseListViewModel(state, _ => { });

        Assert.False(vm.FiltroCreatiDaMeAttivo);

        // Attiva filtro "Creati da me"
        vm.FiltraCreatiDaMeCommand.Execute(null);
        Assert.True(vm.FiltroCreatiDaMeAttivo);
        Assert.Equal(new[] { "Tester UX" }, vm.FiltroCreatoDa.Selezionati);
        Assert.Equal(20, vm.Righe.Count);

        // Tasto toggle: cliccare nuovamente rimuove il filtro
        vm.FiltraCreatiDaMeCommand.Execute(null);
        Assert.False(vm.FiltroCreatiDaMeAttivo);
        Assert.False(vm.FiltroCreatoDa.HaSelezione);
    }
}
