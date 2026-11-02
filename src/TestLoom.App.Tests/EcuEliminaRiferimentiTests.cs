using System.Linq;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Difetto utente 6.7.3 (b): il componente "IPC12" non si eliminava e il messaggio parlava di "test collegati", ma nessun Test
/// della scheda Test la citava. Il blocco ora nomina ciascun riferimento per tipo e dove trovarlo: i Test attivi, i Test eliminati
/// (scheda Archivio) e le Funzionalita (che non sono test e non vanno chiamate tali).</summary>
public sealed class EcuEliminaRiferimentiTests : IDisposable
{
    private readonly List<string> _dirs = new();

    private AppState CreaStatoComeMaster()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ecuelimina_" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        Directory.CreateDirectory(dir);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "IPC12", Archiviata = true } });
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

    /// <summary>Tenta l'eliminazione definitiva del componente "IPC12" e restituisce la riga (per le asserzioni sul blocco).</summary>
    private static Componente EliminaTentando(EcuViewModel vm)
    {
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(e => e.Nome == "IPC12");
        vm.EliminaDefinitivamenteCommand.Execute(riga);
        return riga;
    }

    [Fact]
    public void EliminaEcu_CitataSoloDaUnaFunzionalita_NominaLaFunzionalitaESenzaParlareDiTest()
    {
        var state = CreaStatoComeMaster();
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Lettura guasto", TutteLeEcu = false, EcuAssociate = new() { "IPC12" } },
        });
        var vm = new EcuViewModel(state);

        var riga = EliminaTentando(vm);

        Assert.Contains(riga, vm.Items);
        Assert.True(vm.HaMessaggioErrore);
        Assert.Contains("Lettura guasto", vm.MessaggioErrore);
        Assert.DoesNotContain("TC-", vm.MessaggioErrore);
    }

    [Fact]
    public void EliminaEcu_CitataDaUnaFunzionalitaPerVeicolo_NominaLaFunzionalita()
    {
        var state = CreaStatoComeMaster();
        var funzionalita = new Funzionalita { Nome = "Calibrazione", TutteLeEcu = true };
        funzionalita.VeicoliPerEcu.Add(new VeicoliFunzionalitaEcu { Componente = "IPC12", Veicoli = new() { "Veicolo A" } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { funzionalita });
        var vm = new EcuViewModel(state);

        EliminaTentando(vm);

        Assert.True(vm.HaMessaggioErrore);
        Assert.Contains("Calibrazione", vm.MessaggioErrore);
    }

    [Fact]
    public void EliminaEcu_CitataDaUnTestAttivo_NominaIlTest()
    {
        var state = CreaStatoComeMaster();
        state.TestCases.Save(new TestCase { Id = "TC-100", Componente = "IPC12" }, true);
        var vm = new EcuViewModel(state);

        EliminaTentando(vm);

        Assert.True(vm.HaMessaggioErrore);
        Assert.Contains("TC-100", vm.MessaggioErrore);
    }

    [Fact]
    public void EliminaEcu_CitataDaUnTestNelCestino_NominaIlTestConIlSuoId()
    {
        var state = CreaStatoComeMaster();
        var eliminato = new TestCase { Id = "TC-200", Componente = "IPC12" };
        state.TestCases.Save(eliminato, true);
        state.TestCases.SoftDelete(eliminato, "Master Prova");
        var vm = new EcuViewModel(state);

        var riga = EliminaTentando(vm);

        Assert.Contains(riga, vm.Items);
        Assert.True(vm.HaMessaggioErrore);
        Assert.Contains("TC-200", vm.MessaggioErrore);
    }

    [Fact]
    public void EliminaEcu_SenzaRiferimenti_RimuoveLaRiga()
    {
        var state = CreaStatoComeMaster();
        var vm = new EcuViewModel(state);

        var riga = EliminaTentando(vm);

        Assert.DoesNotContain(riga, vm.Items);
        Assert.False(vm.HaMessaggioErrore);
    }
}
