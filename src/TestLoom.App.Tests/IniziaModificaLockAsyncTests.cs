using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// IniziaModifica legge e acquisisce il lock fuori dal thread UI (share SMB lento). Verifica: (a) gli esiti restano
/// quelli di prima (acquisito / tenuto da un'altra sessione), (b) una acquisizione lenta non blocca il chiamante,
/// (c) chiudere l'editor mentre il lock è in acquisizione lo rilascia senza lasciare lock orfani, (d) chiudere e
/// riaprire durante l'acquisizione non rilascia il lock del nuovo editor, (e) errori di lettura/apertura arrivano
/// all'utente senza lasciare lock acquisiti, (f) stato e property change restano sul thread UI.
/// La lentezza è simulata da sottoclassi che sovrascrivono i seam interni con un gate controllato dal test; il lock
/// reale viene comunque preso da <see cref="LockService"/>.
/// </summary>
[Collection("Localization")]
public sealed class IniziaModificaLockAsyncTests : IDisposable
{
    private static readonly TimeSpan Attesa = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_IniziaModificaAsync_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private AppState CreaState()
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

    private static TestCase SalvaTest(AppState state, string id)
    {
        state.TestCases.Save(new TestCase
        {
            Id = id, Componente = "BCM", Assegnatario = "Mario Rossi", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now,
            StatoLavoro = StatoLavoroTest.Pianificato
        }, isNew: true);
        return state.TestCases.GetById(id)!;
    }

    // ----- sottoclassi Master data: seam di acquisizione e lettura controllabili -----

    /// <summary>Acquisizione lenta: il gate tiene ferma la chiamata prima del TryAcquire reale.</summary>
    private sealed class RequisitiConAcquisizioneControllata : RequisitiViewModel
    {
        private readonly AppState _stato;
        public readonly ManualResetEventSlim InAttesa = new(false);
        public readonly ManualResetEventSlim Via = new(false);

        public RequisitiConAcquisizioneControllata(AppState stato) : base(stato) => _stato = stato;

        internal override Task<LockResult> AcquisisciLockAsync(string percorso, string operatore) => Task.Run(() =>
        {
            InAttesa.Set();
            Via.Wait(Attesa);
            return _stato.Locks.TryAcquire(percorso, operatore);
        });
    }

    /// <summary>Acquisizione reale ma consegnata tardi: il lock esiste già su disco quando il gate si apre.</summary>
    private sealed class RequisitiAcquisizioneTardiva : RequisitiViewModel
    {
        private readonly AppState _stato;
        public readonly ManualResetEventSlim InAttesa = new(false);
        public readonly ManualResetEventSlim Via = new(false);

        public RequisitiAcquisizioneTardiva(AppState stato) : base(stato) => _stato = stato;

        internal override Task<LockResult> AcquisisciLockAsync(string percorso, string operatore) => Task.Run(() =>
        {
            var risultato = _stato.Locks.TryAcquire(percorso, operatore);
            InAttesa.Set();
            Via.Wait(Attesa);
            return risultato;
        });
    }

    /// <summary>Lettura della fotografia controllabile: gate prima della lettura reale, oppure errore.</summary>
    private sealed class RequisitiConLettura : RequisitiViewModel
    {
        public readonly ManualResetEventSlim InAttesa = new(false);
        public readonly ManualResetEventSlim Via = new(false);
        public bool Gate;
        public bool Fallisci;

        public RequisitiConLettura(AppState stato) : base(stato) { }

        internal override Task<FotografiaModifica> LeggiFotografiaAsync(bool bounded, string testo, long pagina, int dimensione) => Task.Run(() =>
        {
            if (Fallisci) throw new InvalidOperationException("lettura fallita (test)");
            if (Gate)
            {
                InAttesa.Set();
                Via.Wait(Attesa);
            }
            return LeggiFotografiaModifica(bounded, testo, pagina, dimensione);
        });
    }

    /// <summary>Traccia il thread su cui avvengono acquisizione e lettura, senza ritardi.</summary>
    private sealed class RequisitiTracciante : RequisitiViewModel
    {
        public int ThreadAcquisizione = -1;
        public int ThreadLettura = -1;

        public RequisitiTracciante(AppState stato) : base(stato) { }

        internal override Task<LockResult> AcquisisciLockAsync(string percorso, string operatore) => Task.Run(() =>
        {
            ThreadAcquisizione = Environment.CurrentManagedThreadId;
            return base.AcquisisciLockAsync(percorso, operatore).GetAwaiter().GetResult();
        });

        internal override Task<FotografiaModifica> LeggiFotografiaAsync(bool bounded, string testo, long pagina, int dimensione) => Task.Run(() =>
        {
            ThreadLettura = Environment.CurrentManagedThreadId;
            return LeggiFotografiaModifica(bounded, testo, pagina, dimensione);
        });
    }

    // ----- sottoclassi Test: seam di acquisizione, banner e tracciamento thread -----

    /// <summary>Editor Test con acquisizione controllata: stesso schema di <see cref="RequisitiConAcquisizioneControllata"/>.</summary>
    private sealed class TestCaseConAcquisizioneControllata : TestCaseEditorViewModel
    {
        private readonly AppState _stato;
        public readonly ManualResetEventSlim InAttesa = new(false);
        public readonly ManualResetEventSlim Via = new(false);

        public TestCaseConAcquisizioneControllata(AppState stato, string id) : base(stato, id, () => { }, _ => { }) => _stato = stato;

        internal override Task<LockResult> AcquisisciLockAsync(string percorso, string operatore) => Task.Run(() =>
        {
            InAttesa.Set();
            Via.Wait(Attesa);
            return _stato.Locks.TryAcquire(percorso, operatore);
        });
    }

    /// <summary>Editor Test: acquisizione reale consegnata tardi (vedi <see cref="RequisitiAcquisizioneTardiva"/>).</summary>
    private sealed class TestCaseAcquisizioneTardiva : TestCaseEditorViewModel
    {
        private readonly AppState _stato;
        public readonly ManualResetEventSlim InAttesa = new(false);
        public readonly ManualResetEventSlim Via = new(false);

        public TestCaseAcquisizioneTardiva(AppState stato, string id) : base(stato, id, () => { }, _ => { }) => _stato = stato;

        internal override Task<LockResult> AcquisisciLockAsync(string percorso, string operatore) => Task.Run(() =>
        {
            var risultato = _stato.Locks.TryAcquire(percorso, operatore);
            InAttesa.Set();
            Via.Wait(Attesa);
            return risultato;
        });
    }

    /// <summary>Editor Test con il controllo banner che fallisce dopo l'apertura della modifica.</summary>
    private sealed class TestCaseBannerInErrore : TestCaseEditorViewModel
    {
        public TestCaseBannerInErrore(AppState stato, string id) : base(stato, id, () => { }, _ => { }) { }

        internal override Task<LockInfo?> RilevaLockAltraPostazioneAsync() =>
            Task.FromException<LockInfo?>(new InvalidOperationException("banner fallito (test)"));
    }

    /// <summary>Editor Test che traccia il thread di acquisizione.</summary>
    private sealed class TestCaseTracciante : TestCaseEditorViewModel
    {
        private readonly AppState _stato;
        public int ThreadAcquisizione = -1;

        public TestCaseTracciante(AppState stato, string id) : base(stato, id, () => { }, _ => { }) => _stato = stato;

        internal override Task<LockResult> AcquisisciLockAsync(string percorso, string operatore) => Task.Run(() =>
        {
            ThreadAcquisizione = Environment.CurrentManagedThreadId;
            return _stato.Locks.TryAcquire(percorso, operatore);
        });
    }

    // ----- (a) esiti invariati -----

    [Fact]
    public async Task Master_LockLibero_AcquisisceEEntraInModifica()
    {
        var state = CreaState();
        using var vm = new RequisitiViewModel(state);

        await vm.IniziaModificaCommand.ExecuteAsync(null);

        Assert.True(vm.InModifica);
        Assert.True(File.Exists(LockService.LockPathFor(state.Paths.RequisitiFile)));
    }

    [Fact]
    public async Task Master_LockTenutoDaAltraSessione_NonEntraInModificaEMostraIlBanner()
    {
        var state = CreaState();
        using var vm = new RequisitiViewModel(state);
        Assert.True(new LockService("PC-Altro").TryAcquire(state.Paths.RequisitiFile, "Luca Bianchi").Acquisito);

        await vm.IniziaModificaCommand.ExecuteAsync(null);

        Assert.False(vm.InModifica);
        Assert.True(vm.LockDetenutoDaAltri);
        Assert.Equal("Luca Bianchi", LockService.TryReadLockInfo(state.Paths.RequisitiFile)?.Operatore);
    }

    [Fact]
    public async Task TestCase_LockTenutoDaAltraSessione_NonEntraInModificaEMostraIlBanner()
    {
        var state = CreaState();
        var tc = SalvaTest(state, "TC-LOCK-1");
        using var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        Assert.True(new LockService("PC-Altro").TryAcquire(state.TestCases.PercorsoPer(tc), "Luca Bianchi").Acquisito);

        await vm.IniziaModificaCommand.ExecuteAsync(null);

        Assert.False(vm.InModifica);
        Assert.True(vm.LockDetenutoDaAltri);
    }

    [Fact]
    public async Task TestCase_LockLibero_AcquisisceEEntraInModifica()
    {
        var state = CreaState();
        var tc = SalvaTest(state, "TC-LOCK-2");
        using var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });

        await vm.IniziaModificaCommand.ExecuteAsync(null);

        Assert.True(vm.InModifica);
        Assert.True(File.Exists(LockService.LockPathFor(state.TestCases.PercorsoPer(tc))));
    }

    // ----- (b) acquisizione lenta: il chiamante non resta bloccato -----

    [Fact]
    public async Task Master_AcquisizioneLenta_NonBloccaIlChiamanteEIlComandoResteOccupato()
    {
        var state = CreaState();
        using var vm = new RequisitiConAcquisizioneControllata(state);

        var sw = Stopwatch.StartNew();
        var pendente = vm.IniziaModificaCommand.ExecuteAsync(null);
        var tempoChiamata = sw.Elapsed;
        Assert.True(vm.IniziaModificaCommand.IsRunning);
        Assert.False(vm.IniziaModificaCommand.CanExecute(null)); // niente secondo click durante l'attesa
        Assert.True(vm.InAttesa.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(pendente.IsCompleted);
        Assert.True(tempoChiamata < TimeSpan.FromSeconds(2), $"la chiamata ha bloccato il chiamante per {tempoChiamata}");
        Assert.False(vm.InModifica);

        vm.Via.Set();
        await pendente;
        Assert.True(vm.InModifica);
        Assert.True(vm.IniziaModificaCommand.CanExecute(null));
    }

    [Fact]
    public async Task TestCase_AcquisizioneLenta_NonBloccaIlChiamanteEIlComandoResteOccupato()
    {
        var state = CreaState();
        var tc = SalvaTest(state, "TC-LOCK-3");
        using var vm = new TestCaseConAcquisizioneControllata(state, tc.Id);

        var sw = Stopwatch.StartNew();
        var pendente = vm.IniziaModificaCommand.ExecuteAsync(null);
        var tempoChiamata = sw.Elapsed;
        Assert.True(vm.IniziaModificaCommand.IsRunning);
        Assert.False(vm.IniziaModificaCommand.CanExecute(null));
        Assert.True(vm.InAttesa.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(pendente.IsCompleted);
        Assert.True(tempoChiamata < TimeSpan.FromSeconds(2), $"la chiamata ha bloccato il chiamante per {tempoChiamata}");
        Assert.False(vm.InModifica);

        vm.Via.Set();
        await pendente;
        Assert.True(vm.InModifica);
    }

    // ----- (c) chiusura durante l'acquisizione: nessun lock orfano -----

    [Fact]
    public async Task Master_ChiusuraDuranteAcquisizione_RilasciaIlLockAcquisitoTardi()
    {
        var state = CreaState();
        var vm = new RequisitiConAcquisizioneControllata(state);
        var lockPath = LockService.LockPathFor(state.Paths.RequisitiFile);

        var pendente = vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vm.InAttesa.Wait(TimeSpan.FromSeconds(5)));
        vm.Dispose(); // editor chiuso mentre il lock è ancora in acquisizione

        vm.Via.Set();
        await pendente;

        Assert.False(vm.InModifica);
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task TestCase_ChiusuraDuranteAcquisizione_RilasciaIlLockAcquisitoTardi()
    {
        var state = CreaState();
        var tc = SalvaTest(state, "TC-LOCK-4");
        var lockPath = LockService.LockPathFor(state.TestCases.PercorsoPer(tc));
        var vm = new TestCaseConAcquisizioneControllata(state, tc.Id);

        var pendente = vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vm.InAttesa.Wait(TimeSpan.FromSeconds(5)));
        vm.Dispose();

        vm.Via.Set();
        await pendente;

        Assert.False(vm.InModifica);
        Assert.False(File.Exists(lockPath));
    }

    // ----- (d) chiudi e riapri durante l'acquisizione: il lock del nuovo editor resta -----

    [Fact]
    public async Task Master_ChiusuraERiaperturaDuranteAcquisizione_NonRilasciaIlLockDelNuovoEditor()
    {
        var state = CreaState();
        var vecchio = new RequisitiAcquisizioneTardiva(state);
        var pendente = vecchio.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vecchio.InAttesa.Wait(TimeSpan.FromSeconds(5)));
        vecchio.Dispose();

        using var nuovo = new RequisitiViewModel(state);
        // Il nuovo editor attende il rilascio tardivo del vecchio: stesso file, stessa sessione di processo.
        var pendenteNuovo = nuovo.IniziaModificaCommand.ExecuteAsync(null);
        Assert.False(pendenteNuovo.IsCompleted);

        vecchio.Via.Set();
        await pendente;
        await pendenteNuovo;

        Assert.True(nuovo.InModifica);
        Assert.True(state.Locks.IsOwnedByThisSession(state.Paths.RequisitiFile));
        Assert.True(File.Exists(LockService.LockPathFor(state.Paths.RequisitiFile)));
    }

    [Fact]
    public async Task TestCase_ChiusuraERiaperturaDuranteAcquisizione_NonRilasciaIlLockDelNuovoEditor()
    {
        var state = CreaState();
        var tc = SalvaTest(state, "TC-LOCK-5");
        var vecchio = new TestCaseAcquisizioneTardiva(state, tc.Id);
        var pendente = vecchio.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vecchio.InAttesa.Wait(TimeSpan.FromSeconds(5)));
        vecchio.Dispose();

        using var nuovo = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var lockPath = state.TestCases.PercorsoPer(tc);
        var pendenteNuovo = nuovo.IniziaModificaCommand.ExecuteAsync(null);
        Assert.False(pendenteNuovo.IsCompleted);

        vecchio.Via.Set();
        await pendente;
        await pendenteNuovo;

        Assert.True(nuovo.InModifica);
        Assert.True(state.Locks.IsOwnedByThisSession(lockPath));
        Assert.True(File.Exists(LockService.LockPathFor(lockPath)));
    }

    // ----- (e) errori: arrivano all'utente, nessun lock acquisito resta -----

    [Fact]
    public async Task Master_ErroreDiLettura_RilasciaIlLockEMostraIlMessaggio()
    {
        var state = CreaState();
        using var vm = new RequisitiConLettura(state) { Fallisci = true };
        var lockPath = LockService.LockPathFor(state.Paths.RequisitiFile);

        await vm.IniziaModificaCommand.ExecuteAsync(null);

        Assert.False(vm.InModifica);
        Assert.False(File.Exists(lockPath));
        Assert.True(vm.HaMessaggioErrore);
        Assert.Contains("lettura fallita (test)", vm.MessaggioErrore);
    }

    [Fact]
    public async Task TestCase_ErroreDopoApertura_MostraIlMessaggioEMantieniLaModifica()
    {
        var state = CreaState();
        var tc = SalvaTest(state, "TC-LOCK-6");
        using var vm = new TestCaseBannerInErrore(state, tc.Id);

        await vm.IniziaModificaCommand.ExecuteAsync(null);

        Assert.True(vm.InModifica); // il lock è dell'editor che sta modificando: un banner che fallisce non rompe la modifica
        Assert.Contains(vm.ErroriValidazione, e => e.Contains("banner fallito (test)"));
    }

    // ----- chiusura durante la lettura: il lock viene rilasciato -----

    [Fact]
    public async Task Master_ChiusuraDuranteLettura_RilasciaIlLock()
    {
        var state = CreaState();
        var vm = new RequisitiConLettura(state) { Gate = true };
        var lockPath = LockService.LockPathFor(state.Paths.RequisitiFile);

        var pendente = vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vm.InAttesa.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(File.Exists(lockPath)); // il lock è già acquisito quando parte la lettura
        vm.Dispose();

        vm.Via.Set();
        await pendente;

        Assert.False(vm.InModifica);
        Assert.False(File.Exists(lockPath));
    }

    // ----- (f) thread UI: stato sul thread UI, I/O fuori -----

    [Fact]
    public void Master_StatoSulThreadUi_LetturaEAcquisizioneFuoriDalThreadUi()
    {
        var state = CreaState();
        using var vm = new RequisitiTracciante(state);
        var ui = new UiThreadTestContext();
        var threadCambi = new List<int>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RequisitiViewModel.InModifica)) threadCambi.Add(Environment.CurrentManagedThreadId); };

        ui.Esegui(() => vm.IniziaModificaCommand.ExecuteAsync(null), Attesa);

        Assert.True(vm.InModifica);
        Assert.NotEmpty(threadCambi);
        Assert.All(threadCambi, id => Assert.Equal(ui.ThreadUiId, id));
        Assert.NotEqual(ui.ThreadUiId, vm.ThreadAcquisizione);
        Assert.NotEqual(ui.ThreadUiId, vm.ThreadLettura);
    }

    [Fact]
    public void TestCase_StatoSulThreadUi_AcquisizioneFuoriDalThreadUi()
    {
        var state = CreaState();
        var tc = SalvaTest(state, "TC-LOCK-7");
        using var vm = new TestCaseTracciante(state, tc.Id);
        var ui = new UiThreadTestContext();
        var threadCambi = new List<int>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TestCaseEditorViewModel.InModifica)) threadCambi.Add(Environment.CurrentManagedThreadId); };

        ui.Esegui(() => vm.IniziaModificaCommand.ExecuteAsync(null), Attesa);

        Assert.True(vm.InModifica);
        Assert.NotEmpty(threadCambi);
        Assert.All(threadCambi, id => Assert.Equal(ui.ThreadUiId, id));
        Assert.NotEqual(ui.ThreadUiId, vm.ThreadAcquisizione);
    }

    [Fact]
    public void Master_SulThreadUi_ErroreDiLetturaArrivaAlloStatoSenzaBloccare()
    {
        var state = CreaState();
        using var vm = new RequisitiConLettura(state) { Fallisci = true };
        var ui = new UiThreadTestContext();

        ui.Esegui(() => vm.IniziaModificaCommand.ExecuteAsync(null), Attesa);

        Assert.False(vm.InModifica);
        Assert.Contains("lettura fallita (test)", vm.MessaggioErrore);
    }
}
