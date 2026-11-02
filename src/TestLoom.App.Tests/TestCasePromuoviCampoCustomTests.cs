using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using System.Reflection;

namespace TestLoom.App.Tests;

/// <summary>
/// P4: "Promuovi campo" dall'editor Test scrive il catalogo dei campi di dettaglio. Deve prendere lo stesso lock di
/// modifica dell'editor del catalogo (stesso file), altrimenti sovrascrive un'altra postazione in modifica. Il ramo di
/// errore (messaggio localizzato) non ha un test: nello spazio di lavoro il catalogo e' su Phase D, e un errore di
/// lettura non si puo' iniettare senza un seam di produzione.
/// </summary>
[Collection("Localization")]
public sealed class TestCasePromuoviCampoCustomTests : IDisposable
{
    private const string Campo = "Campo promosso";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_PromuoviCampo_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _handlerOriginale = ConfirmService.Handler;

    public void Dispose()
    {
        ConfirmService.Handler = _handlerOriginale;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStatoComeMaster()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Promozione", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    private static TestCaseEditorViewModel CreaEditor(AppState state, Action? tornaAllaLista = null)
    {
        state.TestCases.Save(new TestCase
        {
            Id = "TC-PROMO-1", Componente = "BCM", Assegnatario = "Tester",
            DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now, StatoLavoro = StatoLavoroTest.InCorso,
        }, isNew: true);
        return new TestCaseEditorViewModel(state, "TC-PROMO-1", tornaAllaLista ?? (() => { }), _ => { });
    }

    private static async Task InvocaHeartbeatAsync(TestCaseEditorViewModel vm)
    {
        var method = typeof(TestCaseEditorViewModel).GetMethod("RinnovaOInRiacquisisciLock", BindingFlags.Instance | BindingFlags.NonPublic);
        var task = Assert.IsAssignableFrom<Task>(method?.Invoke(vm, null));
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CatalogoInModificaAltrove_NonScriveEMostraIlTitolareDelLock()
    {
        var state = CreaStatoComeMaster();
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        var percorso = state.Paths.CampiSistemaDettagliFile;
        var altroPc = state.Locks.TryAcquire(percorso, "Altro Operatore");
        Assert.True(altroPc.Acquisito);
        var vm = CreaEditor(state);
        var prima = state.Master.CampiSistemaDettagli.Load().Count;

        await vm.PromuoviCampoCustomCommand.ExecuteAsync(Campo);

        Assert.Equal(prima, state.Master.CampiSistemaDettagli.Load().Count);
        Assert.DoesNotContain(state.Master.CampiSistemaDettagli.Load(), c => c.Etichetta == Campo);
        Assert.Contains(vm.ErroriValidazione, e => e.Contains("Altro Operatore", StringComparison.Ordinal));
        state.Locks.Release(percorso);
    }

    [Fact]
    public async Task Promozione_NonCondivideNeRilasciaIlLockDelCatalogoGiaAperto()
    {
        var state = CreaStatoComeMaster();
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        var percorso = state.Paths.CampiSistemaDettagliFile;
        var lockEditor = state.Locks.TryAcquire(percorso, state.Config.OperatoreAttivo);
        Assert.True(lockEditor.Acquisito);
        var vm = CreaEditor(state);
        var prima = state.Master.CampiSistemaDettagli.Load().Count;

        await vm.PromuoviCampoCustomCommand.ExecuteAsync(Campo);

        Assert.Equal(prima, state.Master.CampiSistemaDettagli.Load().Count);
        Assert.DoesNotContain(state.Master.CampiSistemaDettagli.Load(), c => c.Etichetta == Campo);
        Assert.Contains(vm.ErroriValidazione, e => e.Contains("Master Promozione", StringComparison.Ordinal));
        Assert.Equal(lockEditor.DetenutoDa!.SessionId, state.Locks.GetActiveLock(percorso)!.SessionId);
        state.Locks.Release(percorso);
    }

    [Fact]
    public async Task PromozioneRiuscita_AggiungeIlCampoEdEsceDalLock()
    {
        var state = CreaStatoComeMaster();
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        var vm = CreaEditor(state);
        var percorso = state.Paths.CampiSistemaDettagliFile;

        await vm.PromuoviCampoCustomCommand.ExecuteAsync(Campo);

        var campo = Assert.Single(state.Master.CampiSistemaDettagli.Load(), c => c.Etichetta == Campo);
        Assert.True(campo.UsabileComeFiltro);
        Assert.Empty(vm.ErroriValidazione);
        var dopo = state.Locks.TryAcquire(percorso, state.Config.OperatoreAttivo);
        Assert.True(dopo.Acquisito);
        state.Locks.Release(percorso);
    }

    [Fact]
    public async Task PromozioneInCoda_DopoDisposeNonScriveNeAcquisisceIlCatalogo()
    {
        using var state = CreaStatoComeMaster();
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        using var vm = CreaEditor(state);
        var percorso = state.Paths.CampiSistemaDettagliFile;
        var conteggioPrima = state.Master.CampiSistemaDettagli.Load().Count;
        var gateEntrato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sbloccaGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = LockGate.EseguiAsync(percorso, async () =>
        {
            gateEntrato.TrySetResult();
            await sbloccaGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });

        Task? promozione = null;
        try
        {
            await gateEntrato.Task.WaitAsync(TimeSpan.FromSeconds(5));
            promozione = vm.PromuoviCampoCustomCommand.ExecuteAsync(Campo);
            Assert.False(promozione.IsCompleted);

            vm.Dispose();
            sbloccaGate.TrySetResult();
            await gate.WaitAsync(TimeSpan.FromSeconds(5));
            await promozione.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(conteggioPrima, state.Master.CampiSistemaDettagli.Load().Count);
            Assert.DoesNotContain(state.Master.CampiSistemaDettagli.Load(), c => c.Etichetta == Campo);
            Assert.Null(state.Locks.GetActiveLock(percorso));
        }
        finally
        {
            sbloccaGate.TrySetResult();
            try { await gate.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (promozione != null) try { await promozione.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    [Fact]
    public async Task PromozioneConRilascioCatalogoFallito_MantieneLaSessioneTestEPermetteIlRetryEsatto()
    {
        using var state = CreaStatoComeMaster();
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        using var vm = CreaEditor(state);
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica);

        var catalogoPath = state.Paths.CampiSistemaDettagliFile;
        var testPath = state.TestCases.PercorsoPer(vm.TestCase);
        var sessioneTest = Assert.IsType<string>(state.Locks.GetActiveLock(testPath)?.SessionId);
        var tentativiCatalogo = 0;
        vm.RilascioSessioneLockPerTest = (locks, percorso, sessione) =>
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(percorso), Path.GetFullPath(catalogoPath)) &&
                Interlocked.Increment(ref tentativiCatalogo) <= 6)
                return EsitoIo.Errore;
            return locks.ReleaseIfSessionTracked(percorso, sessione);
        };
        vm.AttesaRetryLockPerTest = _ => Task.CompletedTask;

        await vm.PromuoviCampoCustomCommand.ExecuteAsync(Campo);

        Assert.Equal(6, tentativiCatalogo);
        Assert.True(vm.RilascioLockCatalogoInAttesa);
        Assert.False(vm.RilascioLockInAttesa);
        Assert.True(vm.InModifica);
        Assert.Equal(sessioneTest, state.Locks.GetActiveLock(testPath)?.SessionId);

        await InvocaHeartbeatAsync(vm);
        Assert.True(vm.InModifica);
        Assert.Equal(sessioneTest, state.Locks.GetActiveLock(testPath)?.SessionId);

        await vm.RiprovaRilascioLockCatalogoCommand.ExecuteAsync(null);

        Assert.False(vm.RilascioLockCatalogoInAttesa);
        Assert.Null(vm.MessaggioRilascioLockCatalogo);
        Assert.Null(state.Locks.GetActiveLock(catalogoPath));
        Assert.True(vm.InModifica);
        Assert.Equal(sessioneTest, state.Locks.GetActiveLock(testPath)?.SessionId);

        vm.Dispose();
        await Assert.IsAssignableFrom<Task>(vm.RilascioLockDopoDisposePerTest).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(state.Locks.GetActiveLock(testPath));
    }

    [Fact]
    public async Task ChiusuraDurantePromozione_AttendeERilasciaIlLockCatalogoInRetry()
    {
        using var state = CreaStatoComeMaster();
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        using var vm = CreaEditor(state);
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica);

        var catalogoPath = state.Paths.CampiSistemaDettagliFile;
        var testPath = state.TestCases.PercorsoPer(vm.TestCase);
        var retryIniziato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tentativiCatalogo = 0;
        var attese = 0;
        vm.RilascioSessioneLockPerTest = (locks, percorso, sessione) =>
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(percorso), Path.GetFullPath(catalogoPath)) &&
                Interlocked.Increment(ref tentativiCatalogo) <= 6)
                return EsitoIo.Errore;
            return locks.ReleaseIfSessionTracked(percorso, sessione);
        };
        vm.AttesaRetryLockPerTest = async _ =>
        {
            if (Interlocked.Increment(ref attese) != 1) return;
            retryIniziato.TrySetResult();
            await continuaRetry.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        var promozione = vm.PromuoviCampoCustomCommand.ExecuteAsync(Campo);
        Task<bool>? chiusura = null;
        try
        {
            await retryIniziato.Task.WaitAsync(TimeSpan.FromSeconds(5));
            chiusura = vm.RilasciaLockPerUscitaAsync();
            Assert.False(chiusura.IsCompleted);
            Assert.False(vm.InterazioneEditorAbilitata);

            continuaRetry.TrySetResult();
            await promozione.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.RilascioLockCatalogoInAttesa);
            Assert.True(await chiusura.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            continuaRetry.TrySetResult();
            await promozione.WaitAsync(TimeSpan.FromSeconds(5));
            if (chiusura != null) await chiusura.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(7, tentativiCatalogo);
        Assert.False(vm.RilascioLockCatalogoInAttesa);
        Assert.Null(state.Locks.GetActiveLock(catalogoPath));
        Assert.Null(state.Locks.GetActiveLock(testPath));
    }

    [Fact]
    public async Task ChiusuraDuranteAnnulla_AttendeIlComandoESiOccupaDelLockCatalogoPendente()
    {
        using var state = CreaStatoComeMaster();
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        var navigazioni = 0;
        using var vm = CreaEditor(state, () => navigazioni++);
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica);

        var catalogoPath = state.Paths.CampiSistemaDettagliFile;
        var testPath = state.TestCases.PercorsoPer(vm.TestCase);
        var tentativiCatalogo = 0;
        vm.RilascioSessioneLockPerTest = (locks, percorso, sessione) =>
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(percorso), Path.GetFullPath(catalogoPath)) &&
                Interlocked.Increment(ref tentativiCatalogo) <= 6)
                return EsitoIo.Errore;
            return locks.ReleaseIfSessionTracked(percorso, sessione);
        };
        vm.AttesaRetryLockPerTest = _ => Task.CompletedTask;
        await vm.PromuoviCampoCustomCommand.ExecuteAsync(Campo);
        Assert.True(vm.RilascioLockCatalogoInAttesa);
        Assert.NotNull(state.Locks.GetActiveLock(catalogoPath));

        var confermaAnnullaAvviata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rispostaAnnulla = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfirmService.Handler = (_, _, _, _) =>
        {
            confermaAnnullaAvviata.TrySetResult();
            return rispostaAnnulla.Task;
        };
        vm.ModificheNonSalvate = true;

        var annullamento = vm.AnnullaCommand.ExecuteAsync(null);
        Task<bool>? chiusura = null;
        try
        {
            await confermaAnnullaAvviata.Task.WaitAsync(TimeSpan.FromSeconds(5));
            chiusura = vm.RilasciaLockPerUscitaAsync();
            Assert.False(chiusura.IsCompleted);
            Assert.False(vm.InterazioneEditorAbilitata);

            rispostaAnnulla.TrySetResult(true);
            await annullamento.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, navigazioni);
            Assert.True(await chiusura.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            rispostaAnnulla.TrySetResult(true);
            await annullamento.WaitAsync(TimeSpan.FromSeconds(5));
            if (chiusura != null) await chiusura.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(7, tentativiCatalogo);
        Assert.Null(state.Locks.GetActiveLock(catalogoPath));
        Assert.Null(state.Locks.GetActiveLock(testPath));
    }

    [Fact]
    public async Task ChiusuraDuranteRetryAnnulla_AttendeEntrambiIRilasciEsattiSenzaNavigare()
    {
        using var state = CreaStatoComeMaster();
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        var navigazioni = 0;
        using var vm = CreaEditor(state, () => navigazioni++);
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica);

        var catalogoPath = state.Paths.CampiSistemaDettagliFile;
        var testPath = state.TestCases.PercorsoPer(vm.TestCase);
        var sessioneTest = Assert.IsType<string>(state.Locks.GetActiveLock(testPath)?.SessionId);
        var tentativiTest = 0;
        var tentativiCatalogo = 0;
        var rilasciRiusciti = new List<(string Percorso, string Sessione)>();
        var rilasciGate = new object();

        bool StessoPercorso(string sinistro, string destro) =>
            StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(sinistro), Path.GetFullPath(destro));

        vm.RilascioSessioneLockPerTest = (locks, percorso, sessione) =>
        {
            if (StessoPercorso(percorso, catalogoPath) && Interlocked.Increment(ref tentativiCatalogo) <= 6)
                return EsitoIo.Errore;
            if (StessoPercorso(percorso, testPath) && Interlocked.Increment(ref tentativiTest) <= 7)
                return EsitoIo.Errore;

            var esito = locks.ReleaseIfSessionTracked(percorso, sessione);
            if (esito == EsitoIo.Ok)
                lock (rilasciGate) rilasciRiusciti.Add((Path.GetFullPath(percorso), sessione));
            return esito;
        };
        vm.AttesaRetryLockPerTest = _ => Task.CompletedTask;

        await vm.PromuoviCampoCustomCommand.ExecuteAsync(Campo);
        Assert.True(vm.RilascioLockCatalogoInAttesa);
        var sessioneCatalogo = Assert.IsType<string>(state.Locks.GetActiveLock(catalogoPath)?.SessionId);

        // Il primo Annulla esaurisce i tentativi del lease Test e lascia entrambi i lock pendenti.
        await vm.AnnullaCommand.ExecuteAsync(null);
        Assert.True(vm.RilascioLockInAttesa);
        Assert.True(vm.RilascioLockCatalogoInAttesa);
        Assert.Equal(sessioneTest, state.Locks.GetActiveLock(testPath)?.SessionId);
        Assert.Equal(sessioneCatalogo, state.Locks.GetActiveLock(catalogoPath)?.SessionId);

        var retryIniziato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attese = 0;
        vm.AttesaRetryLockPerTest = async _ =>
        {
            if (Interlocked.Increment(ref attese) != 1) return;
            retryIniziato.TrySetResult();
            await continuaRetry.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        var annullamento = vm.AnnullaCommand.ExecuteAsync(null);
        Task<bool>? chiusura = null;
        try
        {
            await retryIniziato.Task.WaitAsync(TimeSpan.FromSeconds(5));
            chiusura = vm.RilasciaLockPerUscitaAsync();
            Assert.False(chiusura.IsCompleted);
            Assert.False(vm.InterazioneEditorAbilitata);

            continuaRetry.TrySetResult();
            await annullamento.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, navigazioni);
            Assert.True(await chiusura.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            continuaRetry.TrySetResult();
            await annullamento.WaitAsync(TimeSpan.FromSeconds(5));
            if (chiusura != null) await chiusura.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(8, tentativiTest);
        Assert.Equal(7, tentativiCatalogo);
        Assert.Null(state.Locks.GetActiveLock(testPath));
        Assert.Null(state.Locks.GetActiveLock(catalogoPath));
        lock (rilasciGate)
        {
            Assert.Contains(rilasciRiusciti, rilascio =>
                StessoPercorso(rilascio.Percorso, testPath) && rilascio.Sessione == sessioneTest);
            Assert.Contains(rilasciRiusciti, rilascio =>
                StessoPercorso(rilascio.Percorso, catalogoPath) && rilascio.Sessione == sessioneCatalogo);
        }
    }
}
