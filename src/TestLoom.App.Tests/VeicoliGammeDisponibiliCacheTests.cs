using System.Linq;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Perf (profilo dotnet-trace su Impostazioni -> Veicoli, banco 1000 veicoli/1000 gamme):
/// VeicoliViewModel.GammeDisponibili ricalcolava Where+Select+ToList sull'intero catalogo Gamma a ogni
/// lettura, letta 4 volte a riga dalla colonna Gamma della griglia principale (colonna ora Button+Flyout,
/// VeicoliView.axaml/.cs). La cache/invalidazione lato ViewModel è già coperta da dd7f1ec
/// (MasterDataNestedLazyTests, che include già il caso "seconda postazione" via due AppState sulla stessa
/// cartella condivisa: non lo si ripete qui). Questa suite aggiunge solo la copertura mancante: l'invalidazione
/// per mutazioni LOCALI fatte tramite i comandi del GammaViewModel annidato (Archivia/Nuovo) una volta che
/// l'Expander Gamma è già stato aperto (ramo "_gamme != null" di GammeDisponibili) — MasterDataNestedLazyTests
/// verifica quel ramo solo per HaModificheNonSalvate/Dispose, non per il contenuto di GammeDisponibili.</summary>
public sealed class VeicoliGammeDisponibiliCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_gammecache_" + Guid.NewGuid().ToString("N"));

    public VeicoliGammeDisponibiliCacheTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private AppState CreaStatoComeMaster()
    {
        var state = TestStateFactory.Create();
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        return state;
    }

    [Fact]
    public void GammeDisponibili_DopoArchiviazioneLocale_NonMostraPiuLaGammaArchiviata()
    {
        var state = CreaStatoComeMaster();
        state.Master.Gamma.Save(new List<Gamma> { new() { Nome = "GammaA" }, new() { Nome = "GammaB" } });

        var vm = new VeicoliViewModel(state);
        Assert.Equal(new[] { "GammaA", "GammaB" }, vm.GammeDisponibili.OrderBy(x => x));

        // Legge la cache prima di mutare (come farebbe la griglia, che la legge più volte per riga) per
        // assicurarsi che l'invalidazione non dipenda dal non averla ancora "riscaldata".
        _ = vm.GammeDisponibili;

        vm.Gamme.IniziaModificaCommand.AttendiModifica();
        var gammaA = vm.Gamme.Items.Single(g => g.Nome == "GammaA");
        vm.Gamme.ArchiviaCommand.Execute(gammaA);

        Assert.DoesNotContain("GammaA", vm.GammeDisponibili);
        Assert.Contains("GammaB", vm.GammeDisponibili);
    }

    [Fact]
    public void GammeDisponibili_DopoNuovaGammaLocale_LaIncludeSenzaRicaricareLaPagina()
    {
        var state = CreaStatoComeMaster();
        state.Master.Gamma.Save(new List<Gamma> { new() { Nome = "GammaA" } });

        var vm = new VeicoliViewModel(state);
        Assert.Equal(new[] { "GammaA" }, vm.GammeDisponibili);
        _ = vm.GammeDisponibili; // scalda la cache prima della mutazione

        vm.Gamme.IniziaModificaCommand.AttendiModifica();
        vm.Gamme.NuovoCommand.Execute(null);
        var nuova = vm.Gamme.Items.Single(g => g.Nome != "GammaA");
        nuova.Nome = "GammaC";

        Assert.Contains("GammaC", vm.GammeDisponibili);
    }
}
