using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Ricerca globale (ciclo prestazioni 6.6.1): la riconciliazione dell'indice master avviene una volta per apertura
/// dell'overlay (<see cref="GlobalSearchViewModel.PreparaApertura"/>) o per ricaricamento dei dati
/// (<see cref="AppState.NotificaDatiRicaricati"/>), non a ogni query. Il conteggio dei refresh avviati e' il criterio.
/// </summary>
public sealed class GlobalSearchRiconciliazioneIndiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_GlobalSearchRiconc_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState Stato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Componente.Save([new Componente { Nome = "Componente-RICONC", ModificatoDa = "Seed" }]);
        state.Master.Requisiti.Save([new Requisito { Id = "REQ-RICONC", Testo = "Requisito di prova", ModificatoDa = "Seed" }]);
        state.Master.Funzionalita.Save([new Funzionalita { Nome = "FUNZ-RICONC", ModificatoDa = "Seed" }]);
        state.Master.Operatori.Save([TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master Riconc", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Seed"
        })]);
        state.RicaricaTutto();
        Assert.True(state.ImpostaOperatore("Master Riconc", "1234"));
        return state;
    }

    [Fact]
    public async Task QuerySuccessive_NonRiconciliaLIndiceASpeseDelleDigitazioni()
    {
        using var state = Stato();
        var vm = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);
        await vm.AttendiRefreshInCorsoAsync();
        var prima = vm.RefreshIndiceAvviati;

        for (var i = 0; i < 10; i++) vm.Query = i % 2 == 0 ? "RICONC" : "FUNZ";
        await vm.AttendiRefreshInCorsoAsync();

        Assert.Equal(prima, vm.RefreshIndiceAvviati);
    }

    [Fact]
    public async Task AperturaOverlay_RiconciliaUnaSolaVoltaAllaPrimaQuery()
    {
        using var state = Stato();
        var vm = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);
        await vm.AttendiRefreshInCorsoAsync();
        var prima = vm.RefreshIndiceAvviati;

        // La chiusura (Reset) da sola non riconcilia: il segnale e' l'apertura.
        vm.Reset();
        vm.Query = "RICONC";
        Assert.Equal(prima, vm.RefreshIndiceAvviati);

        vm.PreparaApertura();
        vm.Query = "RICONC";
        vm.Query = "FUNZ";
        vm.Query = "REQ";
        await vm.AttendiRefreshInCorsoAsync();

        Assert.Equal(prima + 1, vm.RefreshIndiceAvviati);
    }

    [Fact]
    public async Task RicaricamentoDati_RiconciliaUnaVoltaEIlNuovoMasterSiTrova()
    {
        using var state = Stato();
        var vm = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);
        await vm.AttendiRefreshInCorsoAsync();
        var prima = vm.RefreshIndiceAvviati;

        state.Master.Componente.Save([new Componente { Nome = "Componente-NUOVA-RICONC", ModificatoDa = "Seed" }]);
        state.RicaricaTutto();
        vm.Query = "NUOVA-RICONC";
        vm.Query = "NUOVA-RICO";
        await vm.AttendiRefreshInCorsoAsync();
        Assert.Equal(prima + 1, vm.RefreshIndiceAvviati);

        // Dopo la riconciliazione la ricerca trova il nuovo componente, senza altri refresh.
        vm.Query = "NUOVA-RICONC";
        Assert.Contains(vm.Risultati, x => x.Tipo == "Componente" && x.Titolo == "Componente-NUOVA-RICONC");
        Assert.Equal(prima + 1, vm.RefreshIndiceAvviati);
    }
}
