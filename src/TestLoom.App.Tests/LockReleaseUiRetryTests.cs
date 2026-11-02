using System.ComponentModel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public sealed class LockReleaseUiRetryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_LockReleaseRetry_" + Guid.NewGuid().ToString("N"));

    private static async Task AttendiMessaggioErroreAsync(RequisitiViewModel vm, string messaggio)
    {
        if (StringComparer.Ordinal.Equals(vm.MessaggioErrore, messaggio)) return;

        var completato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(vm.MessaggioErrore) &&
                StringComparer.Ordinal.Equals(vm.MessaggioErrore, messaggio))
                completato.TrySetResult();
        };
        vm.PropertyChanged += handler;
        try
        {
            if (!StringComparer.Ordinal.Equals(vm.MessaggioErrore, messaggio))
                await completato.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            vm.PropertyChanged -= handler;
        }

        Assert.Equal(messaggio, vm.MessaggioErrore);
    }

    [Fact]
    public async Task Master_SaveConJsonLockIlleggibile_DopoRetryRilasciaSenzaRipetereIlSalvataggio()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_root);
        state.InizializzaCartella(_root);
        var operatori = state.Master.Operatori.Load().ToList();
        operatori.Add(TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master retry",
            Ruolo = RuoloOperatore.Master,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Test"
        }));
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Master retry", "1234"));
        state.Master.Requisiti.Save(new List<Requisito>
        {
            new() { Id = "REQ-RETRY", Testo = "Prima", ApplicabileBanco = true, ModificatoDa = "Seed" }
        });
        using var vm = new RequisitiViewModel(state);

        await vm.IniziaModificaCommand.ExecuteAsync(null);
        var requisito = Assert.Single(vm.Items, r => r.Id == "REQ-RETRY");
        requisito.Testo = "Salvato una volta";

        var lockPath = LockService.LockPathFor(state.Paths.RequisitiFile);
        var lockOriginale = File.ReadAllText(lockPath);
        File.WriteAllText(lockPath, "{corrotto");
        await vm.SalvaCommand.ExecuteAsync(null);
        await AttendiMessaggioErroreAsync(vm, LocalizationService.Instance.Traduci("SalvatoSbloccoLockNonRiuscito"));

        Assert.False(vm.InModifica);
        Assert.True(vm.RilascioLockInAttesa);
        Assert.Equal(LocalizationService.Instance.Traduci("SalvatoSbloccoLockNonRiuscito"), vm.MessaggioErrore);
        Assert.True(File.Exists(lockPath));
        var salvato = Assert.Single(state.Master.Requisiti.Load());
        Assert.Equal("Salvato una volta", salvato.Testo);
        var changelogDopoSalva = salvato.Changelog.Count;
        Assert.Equal(1, changelogDopoSalva);

        File.WriteAllText(lockPath, lockOriginale);
        await vm.RiprovaRilascioLockCommand.ExecuteAsync(null);

        Assert.False(vm.RilascioLockInAttesa);
        Assert.Null(vm.MessaggioErrore);
        Assert.False(File.Exists(lockPath));
        var dopoRetry = Assert.Single(state.Master.Requisiti.Load());
        Assert.Equal("Salvato una volta", dopoRetry.Testo);
        Assert.Equal(changelogDopoSalva, dopoRetry.Changelog.Count);
    }

    [Fact]
    public async Task Master_AnnullaConJsonLockIlleggibile_MostraSoloErroreSblocco()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_root);
        state.InizializzaCartella(_root);
        var operatori = state.Master.Operatori.Load().ToList();
        operatori.Add(TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master retry",
            Ruolo = RuoloOperatore.Master,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Test"
        }));
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Master retry", "1234"));
        state.Master.Requisiti.Save(new List<Requisito>
        {
            new() { Id = "REQ-CANCEL", Testo = "Invariato", ApplicabileBanco = true, ModificatoDa = "Seed" }
        });
        using var vm = new RequisitiViewModel(state);

        await vm.IniziaModificaCommand.ExecuteAsync(null);
        var lockPath = LockService.LockPathFor(state.Paths.RequisitiFile);
        var lockOriginale = File.ReadAllText(lockPath);
        File.WriteAllText(lockPath, "{corrotto");
        await vm.AnnullaCommand.ExecuteAsync(null);
        await AttendiMessaggioErroreAsync(vm, LocalizationService.Instance.Traduci("LockSbloccoNonRiuscito"));

        Assert.False(vm.InModifica);
        Assert.True(vm.RilascioLockInAttesa);
        Assert.Equal(LocalizationService.Instance.Traduci("LockSbloccoNonRiuscito"), vm.MessaggioErrore);
        Assert.NotEqual(LocalizationService.Instance.Traduci("SalvatoSbloccoLockNonRiuscito"), vm.MessaggioErrore);
        Assert.Equal(0, Assert.Single(state.Master.Requisiti.Load()).Changelog.Count);
        Assert.True(File.Exists(lockPath));

        File.WriteAllText(lockPath, lockOriginale);
        await vm.RiprovaRilascioLockCommand.ExecuteAsync(null);
        Assert.False(vm.RilascioLockInAttesa);
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task TestCase_SaveCommititoConReleaseFallito_RetryNonRipeteSaveEAudit()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_root);
        state.InizializzaCartella(_root);
        var operatori = state.Master.Operatori.Load().ToList();
        operatori.Add(TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master retry",
            Ruolo = RuoloOperatore.Master,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Test"
        }));
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Master retry", "1234"));

        using (var nuovo = new TestCaseEditorViewModel(state, null, () => { }, _ => { }))
        {
            nuovo.SceglieTemplateCommand.Execute(nuovo.TemplatesDisponibili[0]);
            nuovo.AggiungiEcuCommand.Execute("BCM");
            nuovo.VeicoloSelezionato = "Daily 24";
            nuovo.TestCase.Descrizione = "Descrizione iniziale";
            nuovo.AssegnatarioSelezionato = "Master retry";
            nuovo.StatoLavoroSelezionato = StatoLavoroTest.Pianificato;
            if (nuovo.RevisioneCorrente != null) nuovo.RevisioneCorrente.Matricola = "";
            await nuovo.SalvaCommand.ExecuteAsync(null);
            Assert.Empty(nuovo.ErroriValidazione);
        }

        var id = state.TestCases.GetAll().Single().Id;
        var ritorni = 0;
        using var vm = new TestCaseEditorViewModel(state, id, () => ritorni++, _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        vm.TestCase.Descrizione = "Descrizione salvata";
        var percorso = state.TestCases.PercorsoPer(vm.TestCase);
        var lockPath = LockService.LockPathFor(percorso);
        var lockOriginale = File.ReadAllText(lockPath);
        File.WriteAllText(lockPath, "{corrotto");

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.RilascioLockInAttesa);
        Assert.False(vm.InModifica);
        Assert.Equal(LocalizationService.Instance.Traduci("SalvatoSbloccoLockNonRiuscito"), vm.MessaggioRilascioLock);
        Assert.Equal(0, ritorni);
        var salvato = state.TestCases.GetById(id)!;
        Assert.Equal("Descrizione salvata", salvato.Descrizione);
        var revisioneDopoSalva = salvato.RevisioneDati;
        var changelogDopoSalva = salvato.Changelog.Count;

        await vm.AnnullaCommand.ExecuteAsync(null);
        Assert.True(vm.RilascioLockInAttesa);
        Assert.Equal(0, ritorni);
        Assert.Equal(revisioneDopoSalva, state.TestCases.GetById(id)!.RevisioneDati);
        Assert.Equal(changelogDopoSalva, state.TestCases.GetById(id)!.Changelog.Count);

        File.WriteAllText(lockPath, lockOriginale);
        await vm.RiprovaRilascioLockCommand.ExecuteAsync(null);

        Assert.False(vm.RilascioLockInAttesa);
        Assert.Null(vm.MessaggioRilascioLock);
        Assert.Equal(1, ritorni);
        var dopoRetry = state.TestCases.GetById(id)!;
        Assert.Equal("Descrizione salvata", dopoRetry.Descrizione);
        Assert.Equal(revisioneDopoSalva, dopoRetry.RevisioneDati);
        Assert.Equal(changelogDopoSalva, dopoRetry.Changelog.Count);
        Assert.False(File.Exists(lockPath));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
