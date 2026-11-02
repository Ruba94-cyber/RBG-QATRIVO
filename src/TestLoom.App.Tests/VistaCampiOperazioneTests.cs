using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public sealed class VistaCampiOperazioneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_VistaCampi_" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _opened = new();

    [Fact]
    public void OverrideBeatsTemplateDefault_FilledFieldsAreNeverHidden_AndDefaultMatchRemovesOverride()
    {
        var valori = new Dictionary<string, string>();
        var predefiniti = new[] { "UnitLogger::A", "UnitLogger::B" };
        var overrides = VistaCampiOperazione.LeggiOverride(valori);

        Assert.True(VistaCampiOperazione.Nascosto("UnitLogger::A", predefiniti, overrides, haValore: false));
        Assert.False(VistaCampiOperazione.Nascosto("UnitLogger::A", predefiniti, overrides, haValore: true)); // has data: stays visible
        Assert.False(VistaCampiOperazione.Nascosto("UnitLogger::C", predefiniti, overrides, haValore: false));

        VistaCampiOperazione.Imposta(valori, "UnitLogger::A", visibile: true, predefiniti);   // show what the type hides
        VistaCampiOperazione.Imposta(valori, "UnitLogger::C", visibile: false, predefiniti);  // hide what the type shows
        overrides = VistaCampiOperazione.LeggiOverride(valori);
        Assert.False(VistaCampiOperazione.Nascosto("UnitLogger::A", predefiniti, overrides, false));
        Assert.True(VistaCampiOperazione.Nascosto("UnitLogger::C", predefiniti, overrides, false));

        VistaCampiOperazione.Imposta(valori, "UnitLogger::C", visibile: true, predefiniti);   // back to default: override disappears
        Assert.False(VistaCampiOperazione.LeggiOverride(valori).ContainsKey("UnitLogger::C"));
        VistaCampiOperazione.Imposta(valori, "UnitLogger::A", visibile: false, predefiniti);
        Assert.False(valori.ContainsKey(VistaCampiOperazione.ChiaveValoriStrumentiTest));
    }

    [Fact]
    public void ReservedKeyIsNeverTreatedAsAToolValue()
    {
        Assert.True(EvidenzaOperazione.EChiaveRiservata(VistaCampiOperazione.ChiaveValoriStrumentiTest));
        var garbage = new Dictionary<string, string> { [VistaCampiOperazione.ChiaveValoriStrumentiTest] = "x\n+nodelimiter\n-\n-UnitLogger::Ok" };
        var letto = VistaCampiOperazione.LeggiOverride(garbage);
        Assert.Single(letto);
        Assert.False(letto["UnitLogger::Ok"]);
    }

    [Fact]
    public void LegacyTemplateJsonWithoutTheListLoadsEmpty()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new TestTemplate { Nome = "T" }, JsonIo.Options)
            .Replace("\"CampiOperazioneNascosti\":[],", "").Replace(",\"CampiOperazioneNascosti\":[]", "");
        var t = System.Text.Json.JsonSerializer.Deserialize<TestTemplate>(json, JsonIo.Options)!;
        Assert.Empty(t.CampiOperazioneNascosti);
    }

    private TestCaseEditorViewModel CreaVmConStrumentoUdt()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var operatore = state.Master.Operatori.Load().First(o => o.Ruolo == RuoloOperatore.Master);
        state.ImpostaOperatore(operatore.Nome, "1234");
        var vm = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        _opened.Add(state);
        _opened.Add(vm);
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.StrumentiOptions.First(o => o.Valore == "UnitLogger").Selezionato = true;
        return vm;
    }

    [Fact]
    public void HidingAFieldInTheTestRemovesItFromEveryOperationButKeepsTheData()
    {
        var vm = CreaVmConStrumentoUdt();
        var voci = vm.VociVistaDi("UnitLogger");
        Assert.NotEmpty(voci);
        var voce = voci.First(v => v.PuoCambiare);
        var chiave = voce.Chiave;
        var op = vm.RevisioneCorrente!.Condizioni[0];
        bool Presente() => (op.CampiVisual ?? new()).Concat((op.GruppiVisual ?? new()).SelectMany(g => g.Campi))
            .Any(c => $"{c.Strumento}::{c.Nome}" == chiave);
        Assert.True(Presente());
        op.ValoriStrumenti.TryGetValue(chiave, out var prima);

        voce.Visibile = false;

        Assert.False(Presente());
        Assert.True(vm.ModificheNonSalvate);
        Assert.Equal(prima, op.ValoriStrumenti.GetValueOrDefault(chiave)); // data untouched
        Assert.Contains("-" + chiave, vm.TestCase.ValoriStrumentiTest[VistaCampiOperazione.ChiaveValoriStrumentiTest]);

        vm.RipristinaVistaCampiCommand.Execute(null);
        Assert.True(Presente());
        Assert.False(vm.TestCase.ValoriStrumentiTest.ContainsKey(VistaCampiOperazione.ChiaveValoriStrumentiTest));
    }

    [Fact]
    public void ReadOnlyEditorCannotChangeTheView()
    {
        var vm = CreaVmConStrumentoUdt();
        var voce = vm.VociVistaDi("UnitLogger").First(v => v.PuoCambiare);
        vm.InModifica = false;
        voce.Visibile = false;
        Assert.False(vm.TestCase.ValoriStrumentiTest.ContainsKey(VistaCampiOperazione.ChiaveValoriStrumentiTest));
    }

    public void Dispose()
    {
        foreach (var item in _opened.AsEnumerable().Reverse()) item.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
