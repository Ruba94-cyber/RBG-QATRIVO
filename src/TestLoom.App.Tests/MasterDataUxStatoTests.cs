using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Stato "azione non disponibile = nascosta" e stato vuoto delle pagine master data (audit UX 2026-10).</summary>
public sealed class MasterDataUxStatoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterUx_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStato(RuoloOperatore ruolo)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Utente Ux", Ruolo = ruolo, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    [Fact]
    public void Modifica_VisibileSoloAlMasterEFuoriDallaModifica()
    {
        var state = CreaStato(RuoloOperatore.Master);
        using var vm = new GammaViewModel(state);
        Assert.True(vm.MostraModifica);
        Assert.True(vm.MostraModificaPerCreazione);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);
        Assert.False(vm.MostraModifica);
        Assert.False(vm.MostraModificaPerCreazione);
        Assert.Contains(nameof(vm.MostraModifica), raised);
    }

    [Fact]
    public void Modifica_NascostaAChiNonEMaster()
    {
        var state = CreaStato(RuoloOperatore.Standard);
        using var vm = new GammaViewModel(state);
        Assert.False(vm.MostraModifica);
        Assert.False(vm.PuoModificare);
    }

    [Fact]
    public void ElencoVuoto_TitoloSuggerimentoEFiltro()
    {
        var state = CreaStato(RuoloOperatore.Master);
        state.Master.Gamma.Save(new List<Gamma>());
        using var vm = new GammaViewModel(state);

        Assert.True(vm.ElencoVuoto);
        Assert.True(vm.MostraElencoVuoto);
        Assert.False(vm.MostraElencoVuotoFiltrato);
        Assert.True(vm.MostraSuggerimentoElencoVuoto); // Master fuori modifica: puo' aggiungere

        vm.FiltroTesto = "zzz";
        Assert.False(vm.MostraElencoVuoto);
        Assert.True(vm.MostraElencoVuotoFiltrato);
        Assert.False(vm.MostraSuggerimentoElencoVuoto);

        vm.FiltroTesto = "";
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.MostraElencoVuoto);
        Assert.False(vm.MostraSuggerimentoElencoVuoto); // gia' in modifica: niente suggerimento

        vm.NuovoCommand.Execute(null);
        Assert.False(vm.ElencoVuoto);
        Assert.False(vm.MostraElencoVuoto);
    }

    [Fact]
    public void CancellaMessaggio_AzzeraEsito()
    {
        var state = CreaStato(RuoloOperatore.Master);
        using var vm = new GammaViewModel(state);
        vm.Messaggio = "Salvato";
        Assert.True(vm.HaMessaggio);
        vm.CancellaMessaggioCommand.Execute(null);
        Assert.Null(vm.Messaggio);
        Assert.False(vm.HaMessaggio);
    }

    [Theory]
    [InlineData(RuoloOperatore.Master)]
    [InlineData(RuoloOperatore.Standard)]
    [InlineData(RuoloOperatore.Viewer)]
    public void Componente_VisibilitaAzioniPerRuolo_ETransizioneInModifica(RuoloOperatore ruolo)
    {
        var state = CreaStato(ruolo);
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" } });
        using var vm = new EcuViewModel(state);
        var master = ruolo == RuoloOperatore.Master;

        Assert.Equal(master, vm.MostraModifica);
        Assert.Equal(vm.PuoCreare, vm.MostraModificaPerCreazione);
        Assert.Equal(master, vm.Unione.MostraPannello);
        if (ruolo == RuoloOperatore.Viewer) Assert.False(vm.MostraModificaPerCreazione);
        if (master) Assert.True(vm.MostraModificaPerCreazione);

        if (!vm.PuoCreare) return; // niente transizione per chi non puo' entrare in modifica

        var raised = new HashSet<string?>();
        var raisedUnione = new HashSet<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.Unione.PropertyChanged += (_, e) => raisedUnione.Add(e.PropertyName);
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);
        Assert.False(vm.MostraModifica);
        Assert.False(vm.MostraModificaPerCreazione);
        Assert.False(vm.Unione.MostraPannello);
        Assert.Contains(nameof(vm.MostraModifica), raised);
        Assert.Contains(nameof(vm.MostraModificaPerCreazione), raised);
        Assert.Contains(nameof(vm.Unione.MostraPannello), raisedUnione);
    }
}
