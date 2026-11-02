using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

public sealed class MasterDataLockAuthorizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_MasterLockAuth_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Standard_NonPuoForzareLockMasterData_NemmenoInvocandoDirettamenteIlComando()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_root);
        state.InizializzaCartella(_root);
        state.Master.Operatori.Save([TestOperatori.Sigilla(new Operatore
        {
            Nome = "Standard A",
            Ruolo = RuoloOperatore.Standard,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Seed"
        })]);
        state.RicaricaTutto();
        Assert.True(state.ImpostaOperatore("Standard A", "1234"));

        var altroClient = new LockService();
        var acquisito = altroClient.TryAcquire(state.Paths.RequisitiFile, "Master remoto");
        Assert.True(acquisito.Acquisito);
        var lockPath = LockService.LockPathFor(state.Paths.RequisitiFile);
        var prima = File.ReadAllBytes(lockPath);

        using var vm = new RequisitiViewModel(state);
        Assert.True(vm.LockDetenutoDaAltri);
        Assert.False(vm.PuoModificare);
        Assert.False(vm.ForzaSbloccoCommand.CanExecute(null));

        // CanExecute disabilita tutti i pulsanti XAML che bindano questo comando. La barriera deve comunque
        // stare anche nel metodo: una chiamata diretta non deve arrivare al dialog e soprattutto non deve
        // toccare il lock del collega.
        await vm.ForzaSbloccoCommand.ExecuteAsync(null);

        Assert.True(File.Exists(lockPath));
        Assert.Equal(prima, File.ReadAllBytes(lockPath));
        Assert.Equal("Master remoto", state.Locks.GetActiveLock(state.Paths.RequisitiFile)?.Operatore);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
