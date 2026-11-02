using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Invio nella casella di ricerca della Lista Test (KeyBinding Enter, come nella Home): il testo digitato si applica
/// subito, senza attendere il ritardo di digitazione.</summary>
public sealed class ListaTestInvioRicercaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ListaTestInvio_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    [Fact]
    public void Invio_ApplicaSubitoIlTestoDigitato()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.RicaricaTutto();
        var vm = new TestCaseListViewModel(state, _ => { });

        vm.FiltroTestoDigitato = "banco";
        Assert.Equal("", vm.FiltroTesto); // il ritardo di digitazione e' ancora in attesa

        vm.ConfermaRicercaTestoImmediata();

        Assert.Equal("banco", vm.FiltroTesto);
    }
}
