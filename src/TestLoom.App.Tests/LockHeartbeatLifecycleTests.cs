using System.Reflection;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Una callback dell'heartbeat gia' in volo quando la modifica si chiude non deve ricreare il lock
/// (lock orfano senza timer: gli altri PC vedrebbero "in modifica" per 20 minuti); un cambio operatore non deve
/// far vedere il proprio lock come "altra postazione".</summary>
public sealed class LockHeartbeatLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_LockLifecycle_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private AppState CreaStateMaster()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_root);
        state.InizializzaCartella(_root);
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(TestOperatori.Sigilla(new Operatore { Nome = "Mario Rossi", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" }));
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Mario Rossi", "1234");
        return state;
    }

    private static Task InvocaCallbackRinnovo(object vm, string nomeMetodo = "RinnovaOInRiacquisisciLock")
    {
        // Il metodo privato può stare in una classe base generica: risale la gerarchia.
        for (var t = vm.GetType(); t != null; t = t.BaseType)
        {
            var m = t.GetMethod(nomeMetodo, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (m != null) return m.Invoke(vm, null) as Task ?? Task.CompletedTask;
        }
        throw new InvalidOperationException("RinnovaOInRiacquisisciLock non trovato");
    }

    [Fact]
    public async Task Master_CallbackInVoloDopoIlRilascio_NonRicreaIlLock()
    {
        using var state = CreaStateMaster();
        using var vm = new RequisitiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);
        var lockPath = LockService.LockPathFor(state.Paths.RequisitiFile);
        Assert.True(File.Exists(lockPath));

        await vm.RilasciaLockSeAttivoAsync();
        await AttendiCondizione(() => !File.Exists(lockPath));

        await InvocaCallbackRinnovo(vm); // timer gia' scattato prima dello stop

        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task Master_DisposeInModifica_RilasciaIlLockESpegneIlHeartbeat()
    {
        using var state = CreaStateMaster();
        var vm = new RequisitiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var lockPath = LockService.LockPathFor(state.Paths.RequisitiFile);
        Assert.True(File.Exists(lockPath));

        vm.Dispose(); // cambio sotto-sezione / navigazione

        await AttendiCondizione(() => !File.Exists(lockPath));
        await InvocaCallbackRinnovo(vm);
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void Master_DisposeSenzaModifica_NonToccaIlLockDiAltri()
    {
        using var state = CreaStateMaster();
        var altro = new LockService("PC-B");
        Assert.True(altro.TryAcquire(state.Paths.RequisitiFile, "Luca").Acquisito);
        var vm = new RequisitiViewModel(state);

        vm.Dispose();

        Assert.True(altro.IsOwnedByThisSession(state.Paths.RequisitiFile));
    }

    [Fact]
    public async Task Test_CallbackInVoloDopoIlRilascio_NonRicreaIlLock()
    {
        using var state = CreaStateMaster();
        var tc = await CreaTestValido(state);
        var vm = new TestCaseEditorViewModel(state, tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        var targetPath = state.TestCases.PercorsoPer(vm.TestCase);
        var lockPath = LockService.LockPathFor(targetPath);
        Assert.True(File.Exists(lockPath));
        var sessioneVecchia = Assert.IsType<string>(state.Locks.GetActiveLock(targetPath)?.SessionId);

        var callbackEntrata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.RinnovaLockHeartbeatPerTest = async (locks, percorso, operatore) =>
        {
            callbackEntrata.TrySetResult();
            await continuaCallback.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return await locks.EnsureOwnedTrackedAsync(percorso, operatore);
        };
        var callback = InvocaCallbackRinnovo(vm);
        await callbackEntrata.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.Dispose(); // ferma il heartbeat e rilascia
        var rilascio = LockGate.RilasciaSessioneEsattaConRetryAsync(state.Locks, targetPath, sessioneVecchia);
        Assert.False(rilascio.IsCompleted); // Dispose non attende la callback né i retry
        Assert.True(File.Exists(lockPath)); // il rilascio aspetta la callback dentro il gate del path

        continuaCallback.TrySetResult();
        await callback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(EsitoIo.Ok, await rilascio.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(File.Exists(lockPath));
        await InvocaCallbackRinnovo(vm); // una callback arrivata dopo lo stop non riacquisisce
        Assert.False(File.Exists(lockPath));

        var nuovo = new TestCaseEditorViewModel(state, tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        await nuovo.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(nuovo.InModifica);
        Assert.True(File.Exists(lockPath));
        var sessioneNuova = Assert.IsType<string>(state.Locks.GetActiveLock(targetPath)?.SessionId);
        Assert.NotEqual(sessioneVecchia, sessioneNuova);
        await InvocaCallbackRinnovo(vm); // callback tardiva della vecchia VM non deve toccare il nuovo lease
        Assert.True(File.Exists(lockPath));
        Assert.True(state.Locks.IsOwnedByThisSession(targetPath));
        nuovo.Dispose();
        Assert.Equal(EsitoIo.Ok, await LockGate.RilasciaSessioneEsattaConRetryAsync(state.Locks, targetPath, sessioneNuova));
    }

    [Fact]
    public async Task Test_HeartbeatRiacquisito_AggiornaSessioneUsataDaDispose()
    {
        using var state = CreaStateMaster();
        var tc = await CreaTestValido(state);
        var vm = new TestCaseEditorViewModel(state, tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        var targetPath = state.TestCases.PercorsoPer(vm.TestCase);
        var lockPath = LockService.LockPathFor(targetPath);
        var sessioneOriginale = Assert.IsType<string>(state.Locks.GetActiveLock(targetPath)?.SessionId);

        File.Delete(lockPath); // simula un lock sparito/scaduto che il heartbeat deve riacquisire
        await InvocaCallbackRinnovo(vm);

        var sessioneRiacquisita = Assert.IsType<string>(state.Locks.GetActiveLock(targetPath)?.SessionId);
        Assert.NotEqual(sessioneOriginale, sessioneRiacquisita);
        var sessioneVm = typeof(TestCaseEditorViewModel)
            .GetField("_sessioneLockAcquisita", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(vm);
        Assert.Equal(sessioneRiacquisita, sessioneVm);

        vm.Dispose();
        Assert.Equal(EsitoIo.Ok, await LockGate.RilasciaSessioneEsattaConRetryAsync(state.Locks, targetPath, sessioneRiacquisita));
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task Test_HeartbeatDopoDispose_RilasciaLaLeaseRiacquisitaConRetrySottoGate()
    {
        using var state = CreaStateMaster();
        var tc = await CreaTestValido(state);
        var vm = new TestCaseEditorViewModel(state, tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        var targetPath = state.TestCases.PercorsoPer(vm.TestCase);
        var lockPath = LockService.LockPathFor(targetPath);
        var sessioneOriginale = Assert.IsType<string>(state.Locks.GetActiveLock(targetPath)?.SessionId);
        var callbackEntrata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? sessioneRiacquisita = null;
        var tentativiRilascioNuovo = 0;
        vm.RinnovaLockHeartbeatPerTest = async (locks, percorso, operatore) =>
        {
            callbackEntrata.TrySetResult();
            await continuaCallback.Task.WaitAsync(TimeSpan.FromSeconds(5));
            File.Delete(lockPath); // la lease originale sparisce mentre Dispose è accodato dietro il gate
            var esito = await locks.EnsureOwnedTrackedAsync(percorso, operatore);
            sessioneRiacquisita = esito.SessionId;
            return esito;
        };
        vm.RilascioSessioneLockPerTest = (locks, percorso, sessione) =>
        {
            if (StringComparer.Ordinal.Equals(sessione, sessioneRiacquisita) &&
                Interlocked.Increment(ref tentativiRilascioNuovo) == 1)
                return EsitoIo.Errore;
            return locks.ReleaseIfSessionTracked(percorso, sessione);
        };
        vm.AttesaRetryLockPerTest = _ => Task.CompletedTask;

        var callback = InvocaCallbackRinnovo(vm);
        try
        {
            await callbackEntrata.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.Dispose();
            continuaCallback.TrySetResult();
            await callback.WaitAsync(TimeSpan.FromSeconds(5));
            if (vm.RilascioLockDopoDisposePerTest is { } rilascio)
                await rilascio.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(sessioneRiacquisita);
            Assert.NotEqual(sessioneOriginale, sessioneRiacquisita);
            Assert.Equal(2, Volatile.Read(ref tentativiRilascioNuovo));
            Assert.Null(state.Locks.GetActiveLock(targetPath));
        }
        finally
        {
            continuaCallback.TrySetResult();
            vm.Dispose();
            try { await callback.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (vm.RilascioLockDopoDisposePerTest is { } rilascio)
                try { await rilascio.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    [Fact]
    public async Task Test_PreSalvataggioDopoDispose_RilasciaLaLeaseRiacquisitaConRetrySottoGate()
    {
        using var state = CreaStateMaster();
        var tc = await CreaTestValido(state);
        var vm = new TestCaseEditorViewModel(state, tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        var targetPath = state.TestCases.PercorsoPer(vm.TestCase);
        var lockPath = LockService.LockPathFor(targetPath);
        var sessioneOriginale = Assert.IsType<string>(state.Locks.GetActiveLock(targetPath)?.SessionId);
        var callbackEntrata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? sessioneRiacquisita = null;
        var tentativiRilascioNuovo = 0;
        vm.RinnovaLockPreSalvataggioPerTest = async (locks, percorso, operatore) =>
        {
            callbackEntrata.TrySetResult();
            await continuaCallback.Task.WaitAsync(TimeSpan.FromSeconds(5));
            File.Delete(lockPath); // la lease originale sparisce mentre Dispose è accodato dietro il gate
            var esito = await locks.EnsureOwnedTrackedAsync(percorso, operatore);
            sessioneRiacquisita = esito.SessionId;
            return esito;
        };
        vm.RilascioSessioneLockPerTest = (locks, percorso, sessione) =>
        {
            if (StringComparer.Ordinal.Equals(sessione, sessioneRiacquisita) &&
                Interlocked.Increment(ref tentativiRilascioNuovo) == 1)
                return EsitoIo.Errore;
            return locks.ReleaseIfSessionTracked(percorso, sessione);
        };
        vm.AttesaRetryLockPerTest = _ => Task.CompletedTask;

        var callback = InvocaCallbackRinnovo(vm, "AggiornaAvvisoAltraPostazioneAsync");
        try
        {
            await callbackEntrata.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.Dispose();
            continuaCallback.TrySetResult();
            await callback.WaitAsync(TimeSpan.FromSeconds(5));
            if (vm.RilascioLockDopoDisposePerTest is { } rilascio)
                await rilascio.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(sessioneRiacquisita);
            Assert.NotEqual(sessioneOriginale, sessioneRiacquisita);
            Assert.Equal(2, Volatile.Read(ref tentativiRilascioNuovo));
            Assert.Null(state.Locks.GetActiveLock(targetPath));
        }
        finally
        {
            continuaCallback.TrySetResult();
            vm.Dispose();
            try { await callback.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (vm.RilascioLockDopoDisposePerTest is { } rilascio)
                try { await rilascio.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    [Fact]
    public async Task Test_PreSalvataggioRiacquisisceLock_AggiornaSessioneUsataDaDispose()
    {
        using var state = CreaStateMaster();
        var tc = await CreaTestValido(state);
        var vm = new TestCaseEditorViewModel(state, tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        var targetPath = state.TestCases.PercorsoPer(vm.TestCase);
        var lockPath = LockService.LockPathFor(targetPath);
        var sessioneOriginale = Assert.IsType<string>(state.Locks.GetActiveLock(targetPath)?.SessionId);

        File.Delete(lockPath); // simula la scadenza prima del controllo effettuato immediatamente prima del Salva
        await InvocaCallbackRinnovo(vm, "AggiornaAvvisoAltraPostazioneAsync");

        var sessioneRiacquisita = Assert.IsType<string>(state.Locks.GetActiveLock(targetPath)?.SessionId);
        Assert.NotEqual(sessioneOriginale, sessioneRiacquisita);
        var sessioneVm = typeof(TestCaseEditorViewModel)
            .GetField("_sessioneLockAcquisita", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(vm);
        Assert.Equal(sessioneRiacquisita, sessioneVm);

        vm.Dispose();
        Assert.Equal(EsitoIo.Ok, await LockGate.RilasciaSessioneEsattaConRetryAsync(state.Locks, targetPath, sessioneRiacquisita));
        Assert.False(File.Exists(lockPath));
    }

    private static async Task<TestCase> CreaTestValido(AppState state)
    {
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.VeicoloSelezionato = "Daily 24";
        vm.TestCase.Descrizione = "Descrizione completa";
        vm.AssegnatarioSelezionato = "Mario Rossi";
        vm.StatoLavoroSelezionato = StatoLavoroTest.Pianificato;
        if (vm.RevisioneCorrente != null) vm.RevisioneCorrente.Matricola = "";
        await vm.SalvaCommand.ExecuteAsync(null);
        var id = vm.TestCase.Id;
        vm.Dispose();
        return state.TestCases.GetById(id)!;
    }

    [Fact]
    public void CambioOperatore_LockDellaNostraSessione_NonEMaiConflittoEVieneAggiornato()
    {
        var target = Path.Combine(_root, "componente.json");
        Directory.CreateDirectory(_root);
        var locks = new LockService("PC-A");
        Assert.True(locks.TryAcquire(target, "Mario").Acquisito);

        var esito = locks.EnsureOwned(target, "Luca"); // operatore cambiato nella stessa sessione

        Assert.True(esito.Posseduto);
        Assert.Null(esito.Holder);
        Assert.Equal(StatoLockAltrui.Libero, locks.GetForeignActiveLock(target, "Luca").Stato);
        Assert.True(locks.IsOwnedByThisSession(target));
        var info = JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(LockService.LockPathFor(target)), JsonIo.Options)!;
        Assert.Equal("Luca", info.Operatore);
    }

    // P2.1: rinnovi non confermati di seguito mostrano un avviso persistente; il lock non è perso né attribuito ad altri.
    [Fact]
    public async Task Master_RinnoviNonConfermatiDiSeguito_MostranoLAvvisoSenzaPerdereIlLock()
    {
        using var state = CreaStateMaster();
        using var vm = new RequisitiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);

        // Il file del lock è trattenuto da un altro processo (come un client di sincronizzazione): i rinnovi non si confermano.
        using (new FileStream(LockService.LockPathFor(state.Paths.RequisitiFile), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            for (var i = 0; i < MonitorRinnovoLock.SogliaAvviso; i++) await InvocaCallbackRinnovo(vm);
            await AttendiCondizione(() => vm.AvvisoLockNonConfermato);
        }

        Assert.False(string.IsNullOrWhiteSpace(vm.AvvisoLockNonConfermatoTesto));
        Assert.False(vm.LockDetenutoDaAltri);
        Assert.True(vm.InModifica);
    }

    private static async Task AttendiCondizione(Func<bool> condizione)
    {
        var scadenza = DateTime.UtcNow.AddSeconds(10);
        while (!condizione())
        {
            if (DateTime.UtcNow > scadenza) throw new TimeoutException("La condizione non si è verificata entro 10 secondi.");
            await Task.Delay(10);
        }
    }
}
