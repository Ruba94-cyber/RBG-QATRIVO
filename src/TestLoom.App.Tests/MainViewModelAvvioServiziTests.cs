using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// I worker optionali non devono rallentare il primo frame della GUI.
/// L'avvio differito non cambia la frequenza o la logica dei monitor.
/// </summary>
public sealed class MainViewModelAvvioServiziTests
{
    [Fact]
    public void MainViewModel_SenzaAvvioAutomatico_IWorkerPartonoSoloAlComandoEsplicito()
    {
        using var main = new MainViewModel(TestStateFactory.Create(), avviaServiziBackground: false);
        Assert.False(main.ServiziBackgroundAvviati);

        main.AvviaServiziBackground();
        Assert.True(main.ServiziBackgroundAvviati);

        // La callback ritardata e le altre vie di bootstrap potrebbero
        // entrambe tentare Start: il metodo deve essere idempotente.
        main.AvviaServiziBackground();
        Assert.True(main.ServiziBackgroundAvviati);
    }

    [Fact]
    public void MainViewModel_PercorsoLegacy_AvviaIWorkerComePrima()
    {
        using var main = new MainViewModel(TestStateFactory.Create());
        Assert.True(main.ServiziBackgroundAvviati);
    }

    [Fact]
    public void MainViewModel_ChiusaPrimaDelDelay_NonRiavviaWorkerDopoDispose()
    {
        var main = new MainViewModel(TestStateFactory.Create(), avviaServiziBackground: false);
        main.Dispose();
        main.AvviaServiziBackground();
        Assert.False(main.ServiziBackgroundAvviati);
    }
}
