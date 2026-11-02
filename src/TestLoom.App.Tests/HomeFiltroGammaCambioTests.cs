using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Segnalazione utente (2026-10): impostato un filtro gamma sulla dashboard, cambiarlo con un'altra gamma "non succede niente"
/// e la vista resta sui dati della prima. L'oracolo e' una Home nuova con la sola gamma di arrivo selezionata: dopo il cambio
/// la Home sotto esame deve mostrare gli stessi dati, sia con il calcolo sincrono sia con quello in background.
/// </summary>
[Trait("Category", "Lento")]
public sealed class HomeFiltroGammaCambioTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public HomeFiltroGammaCambioTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoom_HomeGammaCambio_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(_dir);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_dir);
        _state.TestCases.RefreshIndex();
    }

    public void Dispose()
    {
        _state.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static void ImpostaGamma(HomeViewModel home, string gamma)
    {
        foreach (var o in home.FiltroGammaDashboard.Opzioni) o.Selezionato = o.Valore == gamma;
    }

    private static void AssertStessiDatiDellaGamma(HomeViewModel atteso, HomeViewModel ottenuto)
    {
        Assert.Equal(atteso.FiltroGammaDashboard.Selezionati, ottenuto.FiltroGammaDashboard.Selezionati);
        Assert.Equal(atteso.TestCaseNelFiltro, ottenuto.TestCaseNelFiltro);
        Assert.Equal(atteso.PerGamma.Select(w => (w.Etichetta, w.Valore)), ottenuto.PerGamma.Select(w => (w.Etichetta, w.Valore)));
        Assert.Equal(atteso.PerOperatore.Select(w => (w.Etichetta, w.Valore)), ottenuto.PerOperatore.Select(w => (w.Etichetta, w.Valore)));
        Assert.Equal(atteso.TassoConformitaPercentuale, ottenuto.TassoConformitaPercentuale, 6);
    }

    private HomeViewModel Oracolo(string gamma)
    {
        var home = new HomeViewModel(_state, _ => { });
        ImpostaGamma(home, gamma);
        home.AttendiAggiornamentoAsync().GetAwaiter().GetResult();
        return home;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CambioDaUnaGammaAUnAltra_LaDashboardSegueLaNuovaGamma(bool inBackground, bool tramiteComando)
    {
        var home = new HomeViewModel(_state, _ => { }, aggiornaInBackground: inBackground);
        await home.AttendiAggiornamentoAsync();
        var gamme = home.FiltroGammaDashboard.Opzioni.Select(o => o.Valore).ToList();
        Assert.True(gamme.Count >= 2, "il seed deve avere almeno due gamme");
        var (prima, dopo) = (gamme[0], gamme[1]);

        ImpostaGamma(home, prima);
        await home.AttendiAggiornamentoAsync();
        AssertStessiDatiDellaGamma(Oracolo(prima), home);

        if (tramiteComando) home.FiltraDashboardPerGammaCommand.Execute(dopo);
        else ImpostaGamma(home, dopo);
        await home.AttendiAggiornamentoAsync();

        AssertStessiDatiDellaGamma(Oracolo(dopo), home);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CambiRapidiDiGamma_ApplicanoSempreL_UltimaScelta(bool inBackground)
    {
        var home = new HomeViewModel(_state, _ => { }, aggiornaInBackground: inBackground);
        await home.AttendiAggiornamentoAsync();
        var gamme = home.FiltroGammaDashboard.Opzioni.Select(o => o.Valore).ToList();

        foreach (var g in gamme) ImpostaGamma(home, g);
        ImpostaGamma(home, gamme[0]);
        await home.AttendiAggiornamentoAsync();

        AssertStessiDatiDellaGamma(Oracolo(gamme[0]), home);
    }
}
