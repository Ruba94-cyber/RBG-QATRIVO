using System.Reflection;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public sealed class MasterDataEditorLockLifecycleTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_MasterLockLifecycle_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private AppState CreateState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_root);
        state.InizializzaCartella(_root);
        var operators = state.Master.Operatori.Load().ToList();
        operators.Add(TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master Lock Lifecycle",
            Ruolo = RuoloOperatore.Master,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Test"
        }));
        state.Master.Operatori.Save(operators);
        Assert.True(state.ImpostaOperatore("Master Lock Lifecycle", "1234"));
        return state;
    }

    private static async Task InvokeHeartbeatAsync(object vm)
    {
        var method = FindPrivateMethod(vm, "RinnovaOInRiacquisisciLock");
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(vm, null));
        await task.WaitAsync(Timeout);
    }

    private static async Task<LockEnsureResult> InvokePreSaveRenewalAsync(RequisitiViewModel vm)
    {
        var method = FindPrivateMethod(vm, "RinnovaLockModificaPrimaDelSalvataggioAsync");
        var task = Assert.IsAssignableFrom<Task<LockEnsureResult?>>(method.Invoke(vm, null));
        return Assert.IsType<LockEnsureResult>(await task.WaitAsync(Timeout));
    }

    private static MethodInfo FindPrivateMethod(object instance, string name)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (method != null) return method;
        }

        throw new InvalidOperationException($"Metodo privato {name} non trovato.");
    }

    [Fact]
    public async Task HeartbeatInVoloAllaChiusura_RiacquisisceEStaccaSoloLaPropriaSessionePrimaDiUnaNuovaModifica()
    {
        using var state = CreateState();
        using var vm = new RequisitiViewModel(state);
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica);

        var targetPath = state.Paths.RequisitiFile;
        var lockPath = LockService.LockPathFor(targetPath);
        var sessioneOriginale = Assert.IsType<string>(state.Locks.GetActiveLock(targetPath)?.SessionId);
        var heartbeatEntrato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaHeartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessioneRilasciataDalCallback = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        vm.RinnovaLockHeartbeatPerTest = async (locks, percorso, operatore) =>
        {
            heartbeatEntrato.TrySetResult();
            await continuaHeartbeat.Task.WaitAsync(Timeout);
            return await locks.EnsureOwnedTrackedAsync(percorso, operatore);
        };
        vm.RilascioSessioneLockPerTest = (locks, percorso, sessione) =>
        {
            var esito = locks.ReleaseIfSessionTracked(percorso, sessione);
            if (!StringComparer.Ordinal.Equals(sessione, sessioneOriginale))
                sessioneRilasciataDalCallback.TrySetResult(sessione);
            return esito;
        };

        var callback = InvokeHeartbeatAsync(vm);
        await heartbeatEntrato.Task.WaitAsync(Timeout);

        var chiusura = vm.RilasciaLockSeAttivoAsync();
        Assert.False(vm.InModifica);
        File.Delete(lockPath); // simulazione deterministica di un lease scomparso mentre il rinnovo è sospeso
        continuaHeartbeat.TrySetResult();

        await callback.WaitAsync(Timeout);
        await chiusura.WaitAsync(Timeout);
        var sessioneRiacquisita = await sessioneRilasciataDalCallback.Task.WaitAsync(Timeout);
        Assert.NotEqual(sessioneOriginale, sessioneRiacquisita);
        Assert.Null(state.Locks.GetActiveLock(targetPath));

        // Il tentativo di chiusura della vecchia sessione è concluso prima dell'acquisizione successiva.
        Assert.Equal(EsitoIo.Ok, await LockGate.RilasciaSessioneEsattaConRetryAsync(state.Locks, targetPath, sessioneOriginale)
            .WaitAsync(Timeout));

        using var nuovaVm = new RequisitiViewModel(state);
        await nuovaVm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(nuovaVm.InModifica);
        var sessioneNuova = Assert.IsType<string>(state.Locks.GetActiveLock(targetPath)?.SessionId);
        Assert.NotEqual(sessioneRiacquisita, sessioneNuova);

        await InvokeHeartbeatAsync(vm); // callback tardiva della vecchia VM
        Assert.Equal(sessioneNuova, state.Locks.GetActiveLock(targetPath)?.SessionId);

        await nuovaVm.RilasciaLockSeAttivoAsync();
        Assert.Equal(EsitoIo.Ok, await LockGate.RilasciaSessioneEsattaConRetryAsync(state.Locks, targetPath, sessioneNuova)
            .WaitAsync(Timeout));
        Assert.Null(state.Locks.GetActiveLock(targetPath));
    }

    [Fact]
    public async Task ControlloPreSalvataggio_RiacquisisceEConservaIlTokenAggiornatoPerLaChiusura()
    {
        using var state = CreateState();
        using var vm = new RequisitiViewModel(state);
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica);

        var targetPath = state.Paths.RequisitiFile;
        var lockPath = LockService.LockPathFor(targetPath);
        var sessioneOriginale = Assert.IsType<string>(state.Locks.GetActiveLock(targetPath)?.SessionId);

        File.Delete(lockPath);
        var esito = await InvokePreSaveRenewalAsync(vm);

        Assert.True(esito.Posseduto);
        Assert.True(esito.Riacquisito);
        Assert.NotEqual(sessioneOriginale, esito.SessionId);
        Assert.Equal(esito.SessionId, state.Locks.GetActiveLock(targetPath)?.SessionId);

        var sessioneAttiva = typeof(MasterDataEditorViewModelBase<Requisito>)
            .GetField("_sessioneLockModifica", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(vm);
        Assert.Equal(esito.SessionId, sessioneAttiva);

        await vm.RilasciaLockSeAttivoAsync();
        Assert.Equal(EsitoIo.Ok, await LockGate.RilasciaSessioneEsattaConRetryAsync(
            state.Locks, targetPath, Assert.IsType<string>(esito.SessionId)).WaitAsync(Timeout));
        Assert.Null(state.Locks.GetActiveLock(targetPath));
    }

    [Fact]
    public async Task Salva_DisabilitaEditorFincheIlRinnovoLockNonCompleta()
    {
        using var state = CreateState();
        using var vm = new RequisitiViewModel(state);
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica);

        var rinnovoEntrato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaRinnovo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.RinnovaLockHeartbeatPerTest = async (locks, percorso, operatore) =>
        {
            rinnovoEntrato.TrySetResult();
            await continuaRinnovo.Task.WaitAsync(Timeout);
            return await locks.EnsureOwnedTrackedAsync(percorso, operatore);
        };

        var salvataggio = vm.SalvaCommand.ExecuteAsync(null);
        await rinnovoEntrato.Task.WaitAsync(Timeout);

        Assert.True(vm.SalvataggioInCorso);
        Assert.False(vm.InterazioneEditorAbilitata);

        continuaRinnovo.TrySetResult();
        await salvataggio.WaitAsync(Timeout);

        Assert.False(vm.SalvataggioInCorso);
        Assert.True(vm.InterazioneEditorAbilitata);
    }
}
