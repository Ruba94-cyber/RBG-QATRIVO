using System.Linq;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>EliminaDefinitivamente/HaRiferimenti vivono in MasterDataEditorViewModelBase (Addendum: grill-me
/// 2026-08-18, terza follow-up, punto 4 — "dovrebbe essere possibile cancellarle definitivamente
/// dall'archivio"), generici per tutti i master data. Testati qui su due sottoclassi rappresentative: una che
/// referenzia via TestCase direttamente (Requisito) e una che referenzia via un altro master data (Gamma, via
/// Veicolo.GammaNome) invece che via TestCase.</summary>
public sealed class MasterDataEliminaDefinitivamenteTests : IDisposable
{
    private readonly List<string> _dirs = new();

    private AppState CreaStatoComeMaster()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_eliminadef_" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        Directory.CreateDirectory(dir);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        return state;
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }
    }

    [Fact]
    public void EliminaDefinitivamente_RigaNonArchiviata_Bloccata()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = false } });
        var vm = new RequisitiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(r => r.Id == "R1");

        vm.EliminaDefinitivamenteCommand.Execute(riga);

        Assert.Contains(riga, vm.Items);
        Assert.True(vm.HaMessaggioErrore);
    }

    [Fact]
    public void EliminaDefinitivamente_ArchiviataMaReferenziata_Bloccata()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = true } });
        var tc = new TestCase { Id = "TC-1", RequisitoId = new List<string> { "R1" } };
        state.TestCases.Save(tc, true);
        var vm = new RequisitiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(r => r.Id == "R1");

        vm.EliminaDefinitivamenteCommand.Execute(riga);

        Assert.Contains(riga, vm.Items);
        Assert.True(vm.HaMessaggioErrore);
    }

    [Fact]
    public void EliminaDefinitivamente_ArchiviataENonReferenziata_RimuoveLaRiga()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = true } });
        var vm = new RequisitiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(r => r.Id == "R1");

        vm.EliminaDefinitivamenteCommand.Execute(riga);

        Assert.DoesNotContain(riga, vm.Items);
        Assert.False(vm.HaMessaggioErrore);
    }

    [Fact]
    public void EliminaDefinitivamente_RimozioneNonPersisteSenzaSalva()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = true } });
        var vm = new RequisitiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(r => r.Id == "R1");
        vm.EliminaDefinitivamenteCommand.Execute(riga);

        Assert.Contains(state.Master.Requisiti.Load(), r => r.Id == "R1"); // resta su disco finché non si salva
    }

    [Fact]
    public async Task EliminaDefinitivamente_DopoSalva_LaRigaSparisceDalDisco()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = true } });
        var vm = new RequisitiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(r => r.Id == "R1");
        vm.EliminaDefinitivamenteCommand.Execute(riga);

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.DoesNotContain(state.Master.Requisiti.Load(), r => r.Id == "R1");
    }

    [Fact]
    public void EliminaDefinitivamente_GammaReferenziataDaUnVeicolo_Bloccata()
    {
        var state = CreaStatoComeMaster();
        state.Master.Gamma.Save(new List<Gamma> { new() { Nome = "LCV Diesel", Archiviata = true } });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily MY24", GammaNome = "LCV Diesel" } });
        var vm = new GammaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(g => g.Nome == "LCV Diesel");

        vm.EliminaDefinitivamenteCommand.Execute(riga);

        Assert.Contains(riga, vm.Items);
        Assert.True(vm.HaMessaggioErrore);
    }

    [Fact]
    public void EliminaDefinitivamente_GammaArchiviataENonReferenziataDaAlcunVeicolo_RimuoveLaRiga()
    {
        var state = CreaStatoComeMaster();
        state.Master.Gamma.Save(new List<Gamma> { new() { Nome = "LCV Diesel", Archiviata = true } });
        var vm = new GammaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(g => g.Nome == "LCV Diesel");

        vm.EliminaDefinitivamenteCommand.Execute(riga);

        Assert.DoesNotContain(riga, vm.Items);
    }
}
