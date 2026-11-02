using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public sealed class ChangeRequestNavigationSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_CRNav_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _originalHandler = ConfirmService.Handler;

    public void Dispose()
    {
        ConfirmService.Handler = _originalHandler;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private (AppState State, TestTemplate TemplateCr) CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var template = new TestTemplate { Nome = "Change Request", IsChangeRequest = true };
        state.Master.TestTemplate.Save(new List<TestTemplate> { template });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily", GammaNome = "Daily" } });
        state.Master.Operatori.Save(new List<Operatore> { TestOperatori.Crea("Mario Rossi", RuoloOperatore.Standard) });
        Assert.True(state.ImpostaOperatore("Mario Rossi", "1234"));
        return (state, template);
    }

    private ChangeRequestEditorViewModel CreaCrSalvata(AppState state, TestTemplate template, Action torna)
    {
        var vm = new ChangeRequestEditorViewModel(state, null, torna, _ => { })
        {
            TemplateSelezionato = template,
            Titolo = "CR navigation safety"
        };
        vm.AggiungiVeicoli(new[] { "Daily" });
        vm.SalvaCommand.Execute(null);
        Assert.False(vm.IsNew);
        Assert.False(vm.ModificheNonSalvate);
        vm.Titolo = "CR modificata localmente";
        Assert.True(vm.ModificheNonSalvate);
        return vm;
    }

    [Fact]
    public async Task TornaSicuro_ModifichePresentiEConfermaNegata_NonNaviga()
    {
        var (state, template) = CreaStato();
        using (state)
        {
            var navigazioni = 0;
            var vm = CreaCrSalvata(state, template, () => navigazioni++);
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(false);

            await vm.TornaAllaListaSicuroCommand.ExecuteAsync(null);

            Assert.Equal(0, navigazioni);
            Assert.True(vm.ModificheNonSalvate);
        }
    }

    [Fact]
    public async Task TornaSicuro_ModifichePresentiEConfermaAccettata_NavigaUnaSolaVolta()
    {
        var (state, template) = CreaStato();
        using (state)
        {
            var navigazioni = 0;
            var vm = CreaCrSalvata(state, template, () => navigazioni++);
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);

            await vm.TornaAllaListaSicuroCommand.ExecuteAsync(null);

            Assert.Equal(1, navigazioni);
        }
    }

    [Fact]
    public async Task TornaSicuro_SenzaModifiche_NonMostraDialogoENaviga()
    {
        var (state, template) = CreaStato();
        using (state)
        {
            var navigazioni = 0;
            var dialoghi = 0;
            var vm = new ChangeRequestEditorViewModel(state, null, () => navigazioni++, _ => { })
            {
                TemplateSelezionato = template,
                Titolo = "CR pulita"
            };
            vm.AggiungiVeicoli(new[] { "Daily" });
            vm.SalvaCommand.Execute(null);
            ConfirmService.Handler = (_, _, _, _) => { dialoghi++; return Task.FromResult(false); };

            await vm.TornaAllaListaSicuroCommand.ExecuteAsync(null);

            Assert.Equal(0, dialoghi);
            Assert.Equal(1, navigazioni);
        }
    }
}
