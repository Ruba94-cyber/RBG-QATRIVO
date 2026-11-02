using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Rapida = solo i campi obbligatori (con interruttore "Mostra tutti i campi"); Completa invariata.
/// Solo presentazione: nessun dato e nessuna regola di validazione cambia.</summary>
[Collection("Localization")]
public sealed class QuickEssentialFieldsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_QuickEssential_" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _opened = new();

    private TestCaseEditorViewModel CreaVm()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var vm = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        _opened.Add(state);
        _opened.Add(vm);
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        return vm;
    }

    private static Dictionary<string, bool> Visibili(TestCaseEditorViewModel vm) => new()
    {
        ["Componente"] = vm.MostraCampoEcu, ["Veicolo"] = vm.MostraCampoVeicolo, ["Funzionalita"] = vm.MostraCampoFunzionalita,
        ["StrumentiUsati"] = vm.MostraCampoStrumentiUsati, ["DescrizioneBreve"] = vm.MostraCampoDescrizioneBreve,
        ["Descrizione"] = vm.MostraCampoDescrizione, ["Matricola"] = vm.MostraCampoVin, ["BaselineSw"] = vm.MostraCampoBaselineSw,
        ["BaselineHw"] = vm.MostraCampoBaselineHw,
    };

    [Fact]
    public void QuickShowsEveryRequiredFieldAndNoOptionalOne_FullIsUnchanged()
    {
        var vm = CreaVm();
        Assert.True(vm.ModoEditorRapido);
        var quick = Visibili(vm);

        var obbligatori = new Dictionary<string, bool>
        {
            ["Componente"] = vm.EcuObbligatorio, ["Veicolo"] = vm.VeicoloObbligatorio, ["Funzionalita"] = vm.FunzionalitaObbligatoria,
            ["DescrizioneBreve"] = vm.DescrizioneBreveObbligatoria, ["Descrizione"] = vm.DescrizioneObbligatoria,
            ["Matricola"] = vm.VinObbligatorio, ["BaselineSw"] = vm.BaselineSwObbligatorio, ["BaselineHw"] = vm.BaselineHwObbligatorio,
        };

        vm.AttivaModalitaCompletaCommand.Execute(null);
        var full = Visibili(vm);
        vm.AttivaModalitaRapidaCommand.Execute(null);

        foreach (var (chiave, visibileInFull) in full)
        {
            if (quick[chiave]) Assert.True(visibileInFull, $"{chiave}: Quick must never show what Full hides");
            if (obbligatori.TryGetValue(chiave, out var req) && req && visibileInFull)
                Assert.True(quick[chiave], $"{chiave}: a required field must always be visible in Quick");
            if (!(obbligatori.TryGetValue(chiave, out var r) && r))
                Assert.False(quick[chiave], $"{chiave}: optional fields are hidden in Quick by default");
        }
        Assert.False(vm.MostraCampiStrumentoDettagli);
    }

    [Fact]
    public void ShowAllFieldsToggleRestoresTheFullFieldSetWithoutTouchingData()
    {
        var vm = CreaVm();
        vm.AttivaModalitaCompletaCommand.Execute(null);
        var full = Visibili(vm);
        vm.AttivaModalitaRapidaCommand.Execute(null);
        var datiPrima = System.Text.Json.JsonSerializer.Serialize(vm.TestCase, JsonIo.Options);
        var nascosti = vm.CampiOpzionaliNascostiRapido;

        vm.MostraTuttiICampiRapido = true;

        Assert.Equal(full, Visibili(vm));
        Assert.True(vm.MostraCampiStrumentoDettagli);
        Assert.Equal(0, vm.CampiOpzionaliNascostiRapido);
        Assert.True(vm.HaCampiOpzionaliRapido || nascosti == 0);
        Assert.Equal(datiPrima, System.Text.Json.JsonSerializer.Serialize(vm.TestCase, JsonIo.Options)); // presentation only

        vm.MostraTuttiICampiRapido = false;
        Assert.Equal(nascosti, vm.CampiOpzionaliNascostiRapido);
    }

    [Fact]
    public void RequiredValidationStillAppliesWhileOptionalFieldsAreHidden()
    {
        var vm = CreaVm();
        // New Test without componente/vehicle: the required ones are visible AND still validated, whatever the toggle.
        Assert.True(vm.MostraCampoVeicolo || !vm.VeicoloObbligatorio);
        var mancanti = vm.CampiObbligatoriMancanti;
        vm.MostraTuttiICampiRapido = false;
        vm.SalvaCommand.Execute(null);
        Assert.NotNull(vm.CampiObbligatoriMancanti);
        Assert.True(vm.CampiObbligatoriMancanti.Count > 0 || vm.ErroriValidazione.Count > 0);
        Assert.NotNull(mancanti);
    }

    public void Dispose()
    {
        foreach (var item in _opened.AsEnumerable().Reverse()) item.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
