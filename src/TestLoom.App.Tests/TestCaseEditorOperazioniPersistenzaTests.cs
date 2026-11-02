using System.Reflection;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Segnalazione utente 2026-10-06: "le operazioni, quando provo a salvare, a volte spariscono". Ogni scenario
/// passa dagli stessi comandi della UI e rilegge il Test da disco: nessuna operazione deve andare persa.</summary>
[Collection("Localization")]
public sealed class TestCaseEditorOperazioniPersistenzaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_OpPersist_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;

    public void Dispose()
    {
        ConfirmService.Handler = _confirmOriginale;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(TestOperatori.Crea("Mario Rossi", RuoloOperatore.Master));
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Mario Rossi", "1234");
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        return state;
    }

    private static async Task<string> CreaTestConOperazioni(AppState state, int nOperazioni)
    {
        var vm = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.AggiungiEcuCommand.Execute("SGW");
        vm.VeicoloSelezionato = "Daily 24";
        vm.TestCase.Descrizione = "Descrizione completa";
        vm.AssegnatarioSelezionato = "Mario Rossi";
        vm.StatoLavoroSelezionato = StatoLavoroTest.Pianificato;
        if (vm.RevisioneCorrente != null) vm.RevisioneCorrente.Matricola = "";
        for (var i = 1; i < nOperazioni; i++) vm.AggiungiCondizioneCommand.Execute(null);
        for (var i = 0; i < nOperazioni; i++) vm.RevisioneCorrente!.Condizioni[i].Nota = "op" + i;
        await vm.SalvaSicuroCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErroriValidazione);
        var id = vm.TestCase.Id;
        vm.Dispose();
        return id;
    }

    private static void ImpostaSoppressione(TestCaseEditorViewModel vm, bool valore) =>
        typeof(TestCaseEditorViewModel).GetField("_sopprimiRitornoAllaLista", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, valore);

    private static List<string> Note(AppState state, string id) =>
        state.TestCases.GetById(id)!.Esecuzioni[^1].Condizioni.Select(c => c.Nota).ToList();

    [Fact]
    public async Task ModificaSoloEcuOperazione_PoiAggiungeOperazione_NessunaOperazionePersa()
    {
        var state = CreateState();
        var id = await CreaTestConOperazioni(state, 2);

        // Salvataggio 1: cambia SOLO il componente di un'operazione (DettagliCambiati non lo vede: lo gestisce TestRevisionSafety).
        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        ImpostaSoppressione(vm, true); // resta nell'editor, come "Salva e crea"
        vm.IniziaModificaCommand.AttendiModifica();
        vm.RevisioneCorrente!.Condizioni[0].Componente = new EcuSelection(new[] { "BCM" });
        await vm.SalvaSicuroCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErroriValidazione);
        Assert.Equal(new[] { "op0", "op1" }, Note(state, id));

        // Salvataggio 2 nella STESSA istanza: aggiunge un'operazione.
        vm.IniziaModificaCommand.AttendiModifica();
        vm.AggiungiCondizioneCommand.Execute(null);
        vm.RevisioneCorrente!.Condizioni[2].Nota = "op2";
        await vm.SalvaSicuroCommand.ExecuteAsync(null);
        var errori = string.Join(" | ", vm.ErroriValidazione);
        Assert.True(vm.ErroriValidazione.Count == 0, "Errori: " + errori);
        Assert.Equal(new[] { "op0", "op1", "op2" }, Note(state, id));
        vm.Dispose();
    }

    [Fact]
    public async Task AggiungeOperazioneConDettagliCambiati_NuovaRevContieneTutteLeOperazioni()
    {
        var state = CreateState();
        var id = await CreaTestConOperazioni(state, 2);
        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        vm.AggiungiCondizioneCommand.Execute(null);
        vm.RevisioneCorrente!.Condizioni[2].Nota = "op2";
        await vm.SalvaSicuroCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErroriValidazione);
        var tc = state.TestCases.GetById(id)!;
        Assert.Equal(2, tc.Esecuzioni.Count);
        Assert.Equal(new[] { "op0", "op1" }, tc.Esecuzioni[0].Condizioni.Select(c => c.Nota));
        Assert.Equal(new[] { "op0", "op1", "op2" }, tc.Esecuzioni[1].Condizioni.Select(c => c.Nota));
        vm.Dispose();
    }

    [Fact]
    public async Task SalvaAnnullatoPoiRipetuto_NessunaOperazionePersa()
    {
        var state = CreateState();
        var id = await CreaTestConOperazioni(state, 2);
        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        vm.AggiungiCondizioneCommand.Execute(null);
        vm.RevisioneCorrente!.Condizioni[2].Nota = "op2";
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(false);
        await vm.SalvaSicuroCommand.ExecuteAsync(null);
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        await vm.SalvaSicuroCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "op0", "op1", "op2" }, Note(state, id));
        vm.Dispose();
    }

    [Fact]
    public async Task RicaricaDatiInBackgroundDuranteModifica_PoiSalva_NessunaOperazionePersa()
    {
        var state = CreateState();
        var id = await CreaTestConOperazioni(state, 2);
        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        vm.AggiungiCondizioneCommand.Execute(null);
        vm.RevisioneCorrente!.Condizioni[2].Nota = "op2";
        state.RicaricaTutto();
        await vm.SalvaSicuroCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "op0", "op1", "op2" }, Note(state, id));
        vm.Dispose();
    }

    [Fact]
    public async Task ValidazioneFallita_TestNuovo_OperazioniIntatte()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.AggiungiEcuCommand.Execute("SGW");
        vm.VeicoloSelezionato = "Daily 24";
        vm.AssegnatarioSelezionato = "Mario Rossi";
        vm.StatoLavoroSelezionato = StatoLavoroTest.Pianificato;
        vm.AggiungiCondizioneCommand.Execute(null);
        vm.AggiungiCondizioneCommand.Execute(null);
        for (var i = 0; i < 3; i++) vm.RevisioneCorrente!.Condizioni[i].Nota = "op" + i;
        // componente SGW non coperta da nessuna operazione -> validazione fallita
        foreach (var c in vm.RevisioneCorrente!.Condizioni) c.Componente = new EcuSelection(new[] { "BCM" });

        await vm.SalvaSicuroCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.ErroriValidazione);
        Assert.Equal(new[] { "op0", "op1", "op2" }, vm.RevisioneCorrente!.Condizioni.Select(c => c.Nota));
        Assert.Equal(3, vm.CondizioniVisual.Count);
        Assert.Equal(3, vm.TestCase.Esecuzioni[^1].Condizioni.Count);
        vm.Dispose();
    }

    [Fact]
    public async Task ValidazioneFallita_NuovaIterazioneSuTestEsistente_OperazioniEIterazioneIntatte()
    {
        var state = CreateState();
        var id = await CreaTestConOperazioni(state, 2);
        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        vm.AggiungiIterazioneCommand.Execute(null);
        Assert.Equal(2, vm.Esecuzioni.Count);
        vm.AggiungiCondizioneCommand.Execute(null);
        foreach (var c in vm.RevisioneCorrente!.Condizioni) c.Componente = new EcuSelection(new[] { "BCM" }); // SGW scoperta

        await vm.SalvaSicuroCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.ErroriValidazione);
        Assert.Equal(2, vm.Esecuzioni.Count);
        Assert.Equal(3, vm.RevisioneCorrente!.Condizioni.Count);
        Assert.Equal(3, vm.CondizioniVisual.Count);
        vm.Dispose();
    }

    [Fact]
    public async Task ValidazioneFallita_ModificaOperazioniTestEsistente_OperazioniIntatte()
    {
        var state = CreateState();
        var id = await CreaTestConOperazioni(state, 2);
        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        vm.AggiungiCondizioneCommand.Execute(null);
        vm.RevisioneCorrente!.Condizioni[2].Nota = "op2";
        foreach (var c in vm.RevisioneCorrente!.Condizioni) c.Componente = new EcuSelection(new[] { "BCM" });

        await vm.SalvaSicuroCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.ErroriValidazione);
        Assert.Equal(new[] { "op0", "op1", "op2" }, vm.RevisioneCorrente!.Condizioni.Select(c => c.Nota));
        Assert.Equal(3, vm.CondizioniVisual.Count);
        vm.Dispose();
    }
}
