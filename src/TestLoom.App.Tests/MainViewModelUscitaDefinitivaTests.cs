using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;
using static TestLoom.App.Tests.NavigazioneTestHelper;

namespace TestLoom.App.Tests;

/// <summary>"Esci" dalla tray: la logica di uscita vive in MainViewModel.PreparaUscitaDefinitivaAsync, cosi' e'
/// testabile senza finestra. Conferma negata = nulla viene toccato; conferma accettata = bozza scartata e lock
/// dell'editor rilasciato.</summary>
public sealed class MainViewModelUscitaDefinitivaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_UscitaDefinitiva_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private AppState CreaStateMaster()
    {
        var dataRoot = Path.Combine(_root, "data");
        var configPath = Path.Combine(_root, "local", "config.json");
        Directory.CreateDirectory(dataRoot);
        var configService = new AppConfigService(configPath);
        configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Master Test" });

        var state = new AppState(configService, runBackgroundMaintenance: false);
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(TestOperatori.Sigilla(new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" }));
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Master Test", "1234");
        return state;
    }

    /// <summary>Apre un nuovo Test dalla lista (come l'operatore) e lo rende sporco: il caso in cui la tray chiede conferma.</summary>
    private static async Task<TestCaseEditorViewModel> ApriNuovoTestSporco(MainViewModel main)
    {
        var lista = Assert.IsType<TestCaseListViewModel>(await main.PreparaPaginaAsync("test"));
        await NavigaAsync(main, "test");
        Assert.Same(lista, main.CurrentPage);
        lista.NuovoCommand.Execute(null);
        var editor = Assert.IsType<TestCaseEditorViewModel>(main.CurrentPage);
        Assert.True(editor.IsNew);
        editor.ModificheNonSalvate = true;
        return editor;
    }

    [Fact]
    public async Task PreparaUscitaDefinitiva_ConModificheEConfermaNo_RestituisceFalsoENonEliminaNulla()
    {
        var state = CreaStateMaster();
        using var _dispose = state;
        using var main = new MainViewModel(state);
        var editor = await ApriNuovoTestSporco(main);
        var operatore = state.Config.OperatoreAttivo;
        state.BozzaAutosave.Salva(operatore, editor.TestCase);
        Assert.NotNull(state.BozzaAutosave.Carica(operatore));

        var uscita = await main.PreparaUscitaDefinitivaAsync(() => Task.FromResult(false));

        Assert.False(uscita);
        Assert.NotNull(state.BozzaAutosave.Carica(operatore));
        Assert.True(main.HaModificheNonSalvate());
    }

    [Fact]
    public async Task PreparaUscitaDefinitiva_ConModificheEConfermaSi_EliminaLaBozzaDellEditorAttivo()
    {
        var state = CreaStateMaster();
        using var _dispose = state;
        using var main = new MainViewModel(state);
        var editor = await ApriNuovoTestSporco(main);
        var operatore = state.Config.OperatoreAttivo;
        state.BozzaAutosave.Salva(operatore, editor.TestCase);
        var richieste = 0;

        var uscita = await main.PreparaUscitaDefinitivaAsync(() => { richieste++; return Task.FromResult(true); });

        Assert.True(uscita);
        Assert.Equal(1, richieste);
        Assert.Null(state.BozzaAutosave.Carica(operatore));
    }

    [Fact]
    public async Task PreparaUscitaDefinitiva_DuranteCaricamentoAllegatoAspettaPrimaDiChiedereConferma()
    {
        var state = CreaStateMaster();
        using var _dispose = state;
        using var main = new MainViewModel(state);
        var lista = Assert.IsType<NonConformitaViewModel>(await main.PreparaPaginaAsync("anomalie"));
        await NavigaAsync(main, "anomalie");
        lista.CreaSegnalazioneReteCommand.Execute(null);
        var editor = Assert.IsType<SegnalazioneReteEditorViewModel>(main.CurrentPage);
        var file = Path.Combine(_root, "allegato-upload.txt");
        File.WriteAllText(file, "allegato in caricamento");
        var avviato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continua = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.GateCaricamentoAllegatoPerTest = async () =>
        {
            avviato.TrySetResult();
            await continua.Task;
        };
        var caricamento = editor.AggiungiAllegatoCommand.ExecuteAsync(file);
        var richiesteConferma = 0;
        try
        {
            await avviato.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(main.HaModificheNonSalvate());

            var uscita = main.PreparaUscitaDefinitivaAsync(() =>
            {
                richiesteConferma++;
                return Task.FromResult(false);
            });

            Assert.False(uscita.IsCompleted);
            Assert.Equal(0, richiesteConferma);
            continua.TrySetResult();
            await caricamento.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(await uscita.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { continua.TrySetResult(); }

        Assert.Equal(1, richiesteConferma);
        Assert.Same(editor, main.CurrentPage);
        Assert.Single(editor.Nc.Allegati);
    }

    [Fact]
    public async Task PreparaUscitaDefinitiva_SenzaModifiche_NonChiedeConfermaERilasciaIlLockMasterInModifica()
    {
        var state = CreaStateMaster();
        using var _dispose = state;
        var main = new MainViewModel(state);
        await main.PreparaPaginaAsync("impostazioni");
        await NavigaAsync(main, "impostazioni");
        var impostazioni = Assert.IsType<ImpostazioniViewModel>(main.CurrentPage);
        impostazioni.SottoSezioneSelezionata = impostazioni.SottoSezioni.First(s => s.Id == "requisiti");
        var requisiti = Assert.IsType<RequisitiViewModel>(impostazioni.PaginaCorrente);
        requisiti.IniziaModificaCommand.AttendiModifica();
        var lockPath = LockService.LockPathFor(state.Paths.RequisitiFile);
        Assert.True(File.Exists(lockPath));
        Assert.False(main.HaModificheNonSalvate());
        var richieste = 0;

        var uscita = await main.PreparaUscitaDefinitivaAsync(() => { richieste++; return Task.FromResult(false); });

        Assert.True(uscita);
        Assert.Equal(0, richieste);
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task PreparaUscitaDefinitiva_SenzaModifiche_AttendeIlRetryDelLockTestPrimaDiConsentireLaChiusura()
    {
        var state = CreaStateMaster();
        using var _dispose = state;
        var testCase = new TestCase
        {
            Id = "TC-LOCK-CLOSE",
            Componente = new EcuSelection { "Componente-A" },
            DescrizioneBreve = "Test lock shutdown",
            StatoLavoro = StatoLavoroTest.Pianificato,
        };
        state.TestCases.Save(testCase, isNew: true);
        state.RicaricaTutto();

        using var main = new MainViewModel(state);
        await main.PreparaPaginaAsync("test");
        await NavigaAsync(main, "test");
        var lista = Assert.IsType<TestCaseListViewModel>(main.CurrentPage);
        lista.ApriCommand.Execute(testCase);
        var editor = Assert.IsType<TestCaseEditorViewModel>(main.CurrentPage);
        await editor.IniziaModificaCommand.ExecuteAsync(null);

        var lockPath = LockService.LockPathFor(state.TestCases.PercorsoPer(testCase));
        Assert.True(editor.InModifica);
        Assert.True(File.Exists(lockPath));

        var retryAvviato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tentativiRilascio = 0;
        editor.RilascioSessioneLockPerTest = (locks, path, sessione) =>
            Interlocked.Increment(ref tentativiRilascio) == 1
                ? EsitoIo.Errore
                : locks.ReleaseIfSessionTracked(path, sessione);
        editor.AttesaRetryLockPerTest = async _ =>
        {
            retryAvviato.TrySetResult();
            await continuaRetry.Task;
        };

        var uscita = main.PreparaUscitaDefinitivaAsync(() => Task.FromResult(true));
        try
        {
            await retryAvviato.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(uscita.IsCompleted);
            Assert.False(editor.InterazioneEditorAbilitata);
            Assert.True(File.Exists(lockPath));

            continuaRetry.TrySetResult();
            Assert.True(await uscita.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            continuaRetry.TrySetResult();
        }

        Assert.False(File.Exists(lockPath));
        Assert.True(tentativiRilascio >= 2);
    }

    [Fact]
    public async Task PreparaUscitaDefinitiva_SeIlLockTestRestaOccupato_NonConsenteLaChiusuraEPermetteRetry()
    {
        var state = CreaStateMaster();
        using var _dispose = state;
        var testCase = new TestCase
        {
            Id = "TC-LOCK-CLOSE-RETRY",
            Componente = new EcuSelection { "Componente-A" },
            DescrizioneBreve = "Test lock shutdown retry",
            StatoLavoro = StatoLavoroTest.Pianificato,
        };
        state.TestCases.Save(testCase, isNew: true);
        state.RicaricaTutto();

        using var main = new MainViewModel(state);
        await main.PreparaPaginaAsync("test");
        await NavigaAsync(main, "test");
        var lista = Assert.IsType<TestCaseListViewModel>(main.CurrentPage);
        lista.ApriCommand.Execute(testCase);
        var editor = Assert.IsType<TestCaseEditorViewModel>(main.CurrentPage);
        await editor.IniziaModificaCommand.ExecuteAsync(null);

        var lockPath = LockService.LockPathFor(state.TestCases.PercorsoPer(testCase));
        editor.RilascioSessioneLockPerTest = (_, _, _) => EsitoIo.Errore;
        editor.AttesaRetryLockPerTest = _ => Task.CompletedTask;

        Assert.False(await main.PreparaUscitaDefinitivaAsync(() => Task.FromResult(true)));
        Assert.True(editor.RilascioLockInAttesa);
        Assert.True(editor.InterazioneEditorAbilitata);
        Assert.True(File.Exists(lockPath));

        editor.RilascioSessioneLockPerTest = (locks, path, sessione) => locks.ReleaseIfSessionTracked(path, sessione);
        Assert.True(await main.RilasciaLockEditorAttivoAsync());
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task RilasciaLockEditorAttivo_DuranteAcquisizioneAttendeLaSessioneELaRilascia()
    {
        var state = CreaStateMaster();
        using var _dispose = state;
        var testCase = new TestCase
        {
            Id = "TC-LOCK-ACQUIRE-CLOSE",
            Componente = new EcuSelection { "Componente-A" },
            DescrizioneBreve = "Test attesa acquisizione lock",
            StatoLavoro = StatoLavoroTest.Pianificato,
        };
        state.TestCases.Save(testCase, isNew: true);
        state.RicaricaTutto();

        using var main = new MainViewModel(state);
        await main.PreparaPaginaAsync("test");
        await NavigaAsync(main, "test");
        var lista = Assert.IsType<TestCaseListViewModel>(main.CurrentPage);
        lista.ApriCommand.Execute(testCase);
        var editor = Assert.IsType<TestCaseEditorViewModel>(main.CurrentPage);

        var acquisizioneAvviata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaAcquisizione = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.AcquisisciLockPerTest = async (percorso, operatore) =>
        {
            acquisizioneAvviata.TrySetResult();
            await continuaAcquisizione.Task;
            return await Task.Run(() => state.Locks.TryAcquireNuovaSessione(percorso, operatore));
        };
        var inizioModifica = editor.IniziaModificaCommand.ExecuteAsync(null);

        try
        {
            await acquisizioneAvviata.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var lockPath = LockService.LockPathFor(state.TestCases.PercorsoPer(testCase));
            var uscita = main.RilasciaLockEditorAttivoAsync();

            Assert.False(uscita.IsCompleted);
            continuaAcquisizione.TrySetResult();
            await inizioModifica.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await uscita.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(File.Exists(lockPath));
        }
        finally
        {
            continuaAcquisizione.TrySetResult();
            await inizioModifica.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
