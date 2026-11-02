using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public sealed class TestCaseEditorSalvataggioBusyStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_SalvataggioBusy_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private AppState CreateState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_root);
        state.InizializzaCartella(_root);
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(TestOperatori.Sigilla(new Operatore
        {
            Nome = "Mario Rossi",
            Ruolo = RuoloOperatore.Master,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Test"
        }));
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Mario Rossi", "1234");
        return state;
    }

    private static async Task<string> CreaTestValido(AppState state)
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
        Assert.Empty(vm.CampiObbligatoriMancanti);
        var id = vm.TestCase.Id;
        vm.Dispose();
        return id;
    }

    private static async Task<(TaskCompletionSource Continua, Task Gate)> BloccaControlloLockAsync(string percorso)
    {
        var entrato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continua = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = LockGate.EseguiAsync(percorso, async () =>
        {
            entrato.TrySetResult();
            await continua.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });
        await entrato.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return (continua, gate);
    }

    private static async Task AttendiBusyAsync(TestCaseEditorViewModel vm)
    {
        if (vm.SalvataggioInCorso) return;
        var entrato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(vm.SalvataggioInCorso) && vm.SalvataggioInCorso)
                entrato.TrySetResult();
        }

        vm.PropertyChanged += OnPropertyChanged;
        try
        {
            if (!vm.SalvataggioInCorso) await entrato.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            vm.PropertyChanged -= OnPropertyChanged;
        }
    }

    [Fact]
    public async Task AnnullaDaVistaSolaLettura_SenzaLease_TornaAllaListaSenzaErroreDiRilascio()
    {
        using var state = CreateState();
        var id = await CreaTestValido(state);
        var ritorniAllaLista = 0;
        using var vm = new TestCaseEditorViewModel(state, id,
            tornaAllaLista: () => ritorniAllaLista++, navigaATest: _ => { });

        var percorso = state.TestCases.PercorsoPer(vm.TestCase);
        Assert.False(vm.InModifica);
        Assert.Null(state.Locks.GetActiveLock(percorso));

        await vm.AnnullaCommand.ExecuteAsync(null);

        Assert.Equal(1, ritorniAllaLista);
        Assert.False(vm.RilascioLockInAttesa);
        Assert.Null(vm.MessaggioRilascioLock);
        Assert.Null(state.Locks.GetActiveLock(percorso));
    }

    [Fact]
    public async Task SalvataggioInAttesaDelControlloLock_DisabilitaEditorEFaRitornareLoStatoAlTermine()
    {
        using var state = CreateState();
        var id = await CreaTestValido(state);
        using var vm = new TestCaseEditorViewModel(state, id, tornaAllaLista: () => { }, navigaATest: _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);

        var percorso = state.TestCases.PercorsoPer(vm.TestCase);
        var (continuaControllo, gate) = await BloccaControlloLockAsync(percorso);

        var salvataggio = vm.SalvaCommand.ExecuteAsync(null);
        try
        {
            await AttendiBusyAsync(vm);
            Assert.True(vm.SalvataggioInCorso);
            Assert.False(vm.InterazioneEditorAbilitata);
        }
        finally
        {
            continuaControllo.TrySetResult();
            await gate.WaitAsync(TimeSpan.FromSeconds(5));
            await salvataggio.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(vm.SalvataggioInCorso);
        Assert.True(vm.InterazioneEditorAbilitata);
        Assert.Empty(vm.ErroriValidazione);
    }

    [Fact]
    public async Task EscapeDuranteSalvataggioNonAnnullaLaSessioneInAttesaDelLock()
    {
        using var state = CreateState();
        var id = await CreaTestValido(state);
        var ritorniAllaLista = 0;
        using var vm = new TestCaseEditorViewModel(state, id, tornaAllaLista: () => ritorniAllaLista++, navigaATest: _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        vm.TestCase.Descrizione = "Descrizione salvata durante l'attesa";

        var percorso = state.TestCases.PercorsoPer(vm.TestCase);
        var (continuaControllo, gate) = await BloccaControlloLockAsync(percorso);

        var salvataggio = vm.SalvaCommand.ExecuteAsync(null);
        try
        {
            await AttendiBusyAsync(vm);
            Assert.True(vm.SalvataggioInCorso);

            Assert.True(((IScorciatoiePagina)vm).AnnullaDaTastiera());

            Assert.True(vm.InModifica);
            Assert.Equal(0, ritorniAllaLista);
            Assert.True(vm.SalvataggioInCorso);
        }
        finally
        {
            continuaControllo.TrySetResult();
            await gate.WaitAsync(TimeSpan.FromSeconds(5));
            await salvataggio.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(vm.SalvataggioInCorso);
        Assert.Equal("Descrizione salvata durante l'attesa", state.TestCases.GetById(id)!.Descrizione);
        Assert.Equal(1, ritorniAllaLista);
    }

    [Fact]
    public async Task SalvataggioDuranteConfermaAnnullaNonParte()
    {
        using var state = CreateState();
        var id = await CreaTestValido(state);
        using var vm = new TestCaseEditorViewModel(state, id, tornaAllaLista: () => { }, navigaATest: _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        vm.TestCase.Descrizione = "Modifica da conservare";
        vm.ModificheNonSalvate = true;

        var confermaEntrata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rispostaConferma = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerOriginale = ConfirmService.Handler;
        ConfirmService.Handler = (_, _, _, _) =>
        {
            confermaEntrata.TrySetResult();
            return rispostaConferma.Task;
        };

        var annullamento = vm.AnnullaCommand.ExecuteAsync(null);
        try
        {
            await confermaEntrata.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await vm.SalvaCommand.ExecuteAsync(null);

            Assert.False(vm.SalvataggioInCorso);
            Assert.NotEqual("Modifica da conservare", state.TestCases.GetById(id)!.Descrizione);

            rispostaConferma.TrySetResult(false);
            await annullamento.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            rispostaConferma.TrySetResult(false);
            ConfirmService.Handler = handlerOriginale;
        }

        Assert.True(vm.InModifica);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Equal("Modifica da conservare", state.TestCases.GetById(id)!.Descrizione);
    }

    [Fact]
    public async Task ChiusuraDuranteSalvataggio_AttendeIlCommitPrimaDiRilasciareIlLock()
    {
        using var state = CreateState();
        var id = await CreaTestValido(state);
        using var vm = new TestCaseEditorViewModel(state, id, tornaAllaLista: () => { }, navigaATest: _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        vm.TestCase.Descrizione = "Commit completato prima della chiusura";

        var percorso = state.TestCases.PercorsoPer(vm.TestCase);
        var (continuaControllo, gate) = await BloccaControlloLockAsync(percorso);
        var salvataggio = vm.SalvaCommand.ExecuteAsync(null);
        Task<bool>? chiusura = null;
        try
        {
            await AttendiBusyAsync(vm);
            Assert.True(vm.InModifica);

            chiusura = vm.RilasciaLockPerUscitaAsync();
            Assert.False(chiusura.IsCompleted);
            Assert.False(vm.InterazioneEditorAbilitata);

            continuaControllo.TrySetResult();
            await gate.WaitAsync(TimeSpan.FromSeconds(5));
            await salvataggio.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await chiusura.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            continuaControllo.TrySetResult();
            await gate.WaitAsync(TimeSpan.FromSeconds(5));
            await salvataggio.WaitAsync(TimeSpan.FromSeconds(5));
            if (chiusura != null) await chiusura.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal("Commit completato prima della chiusura", state.TestCases.GetById(id)!.Descrizione);
        Assert.Null(state.Locks.GetActiveLock(percorso));
    }
}
