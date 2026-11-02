using System.Linq;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Archivio unificato (feedback utente 2026-08-18, punto 6): ItemsAttivi nasconde le righe archiviate
/// dalla tabella di lavoro di ciascun master data, Archivia/Ripristina le spostano dentro/fuori, e
/// ArchivioViewModel le aggrega tutte (master data + TestCase eliminati) in un solo elenco ripristinabile o
/// eliminabile definitivamente.</summary>
[Collection("Localization")]
public sealed class ArchivioViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_archivio_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _handlerOriginale = ConfirmService.Handler;

    private AppState CreaStatoComeMaster()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        return state;
    }

    public void Dispose()
    {
        ConfirmService.Handler = _handlerOriginale;
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void ItemsAttivi_EscludeLeRigheArchiviate()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = false }, new() { Id = "R2", Archiviata = true } });

        var vm = new RequisitiViewModel(state);

        Assert.Contains(vm.ItemsAttivi, r => r.Id == "R1");
        Assert.DoesNotContain(vm.ItemsAttivi, r => r.Id == "R2");
        // Requisiti è un catalogo paginato: in sola consultazione (nessun IniziaModificaCommand) Items
        // riflette la pagina SQLite corrente, che esclude le archiviate come ItemsAttivi — "Items resta
        // la fonte piena" vale solo dopo l'ingresso in modifica (vedi CaricaFotografiaAutorevoleCompletaPerModifica).
        Assert.Single(vm.Items);
    }

    [Fact]
    public void Archivia_SpostaLaRigaFuoriDaItemsAttivi()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = false } });
        var vm = new RequisitiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(r => r.Id == "R1");

        vm.ArchiviaCommand.Execute(riga);

        Assert.True(riga.Archiviata);
        Assert.DoesNotContain(riga, vm.ItemsAttivi);
        Assert.Contains(riga, vm.Items);
    }

    [Fact]
    public void Ripristina_RimetteLaRigaInItemsAttivi()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = true } });
        var vm = new RequisitiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(r => r.Id == "R1");

        vm.RipristinaCommand.Execute(riga);

        Assert.False(riga.Archiviata);
        Assert.Contains(riga, vm.ItemsAttivi);
    }

    // Regressione punto 2 (feedback utente 2026-08-25): "Archivia" chiamava RicalcolaItemsAttivi() che fa
    // ItemsAttivi.Clear() + riaggiungi tutto, un evento CollectionChanged Reset che in una DataGrid virtualizzata
    // fa saltare lo scroll sempre in cima alla lista, qualunque fosse la posizione prima del click. Con più
    // righe presenti (non solo quella archiviata) verifichiamo che l'evento generato sia un Remove mirato, mai
    // un Reset — quella è la garanzia concreta che lo scroll resti dov'era.
    [Fact]
    public void Archivia_NonGeneraUnResetDellaCollezione_LoScrollDellaGridRestaFermo()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito>
        {
            new() { Id = "R1" }, new() { Id = "R2" }, new() { Id = "R3" }, new() { Id = "R4" }, new() { Id = "R5" }
        });
        var vm = new RequisitiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var daArchiviare = vm.Items.Single(r => r.Id == "R3");

        var azioni = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.ItemsAttivi.CollectionChanged += (_, e) => azioni.Add(e.Action);

        vm.ArchiviaCommand.Execute(daArchiviare);

        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, azioni);
        Assert.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Remove, azioni);
        Assert.Equal(4, vm.ItemsAttivi.Count);
        Assert.DoesNotContain(daArchiviare, vm.ItemsAttivi);
        // Le altre righe restano nella stessa collezione (nessun Clear+riaggiungi che le sostituirebbe con
        // nuove reference), garanzia indiretta che la DataGrid non le "veda" come elementi nuovi da rirenderizzare.
        Assert.Contains(vm.Items.Single(r => r.Id == "R1"), vm.ItemsAttivi);
        Assert.Contains(vm.Items.Single(r => r.Id == "R5"), vm.ItemsAttivi);
    }

    [Fact]
    public void ArchivioViewModel_AggregaRigheArchiviateDiPiuTipi()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = true } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", Archiviata = true } });

        var archivio = new ArchivioViewModel(state);

        Assert.Contains(archivio.Voci, v => v.Etichetta == "R1");
        Assert.Contains(archivio.Voci, v => v.Etichetta == "BCM");
    }

    [Fact]
    public async Task ArchivioViewModel_RipristinaVoce_LaRimuoveDallArchivioERimetteLaRigaAttiva()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = true } });
        var archivio = new ArchivioViewModel(state);
        var voce = archivio.Voci.Single(v => v.Etichetta == "R1");

        await archivio.RipristinaVoceCommand.ExecuteAsync(voce);

        Assert.DoesNotContain(archivio.Voci, v => v.Etichetta == "R1");
        Assert.False(state.Master.Requisiti.Load().Single(r => r.Id == "R1").Archiviata);
    }

    [Fact]
    public async Task ArchivioViewModel_AzioneSingolaImpedisceAltreAzioniFinoAlTermine()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = true } });
        using var archivio = new ArchivioViewModel(state);
        var avviata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continua = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sorgentiDispose = typeof(ArchivioViewModel).GetField(
            "_childrenDisposed",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var voce = new VoceArchivio
        {
            IdTecnico = "test:azione-asincrona",
            TipoEntita = "Test",
            Etichetta = "Azione asincrona",
            Ripristina = async () =>
            {
                avviata.TrySetResult();
                await continua.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return null;
            },
            EliminaDefinitivamente = () => Task.FromResult<string?>(null),
        };

        var azione = archivio.RipristinaVoceCommand.ExecuteAsync(voce);
        try
        {
            await avviata.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(archivio.IsBusy);
            Assert.False(archivio.MostraAvanzamentoSvuotamento);
            Assert.False(archivio.RipristinaVoceCommand.CanExecute(voce));
            Assert.False(archivio.EliminaVoceDefinitivamenteCommand.CanExecute(voce));
            Assert.False(archivio.SvuotaArchivioCommand.CanExecute(null));
            Assert.False((bool)sorgentiDispose.GetValue(archivio)!);
            archivio.Dispose();
            Assert.False((bool)sorgentiDispose.GetValue(archivio)!);
        }
        finally
        {
            continua.TrySetResult();
            await azione;
        }

        Assert.False(archivio.IsBusy);
        Assert.True((bool)sorgentiDispose.GetValue(archivio)!);
    }

    [Fact]
    public async Task ArchivioViewModel_EliminaVoceDefinitivamente_ReferenziataResta()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = true } });
        state.TestCases.Save(new TestCase { Id = "TC-1", RequisitoId = new List<string> { "R1" } }, true);
        var archivio = new ArchivioViewModel(state);
        var voce = archivio.Voci.Single(v => v.Etichetta == "R1");

        await archivio.EliminaVoceDefinitivamenteCommand.ExecuteAsync(voce);

        Assert.Contains(archivio.Voci, v => v.Etichetta == "R1");
        Assert.True(archivio.HaMessaggioErrore);
        Assert.Contains(state.Master.Requisiti.Load(), r => r.Id == "R1");
    }

    [Fact]
    public async Task ArchivioViewModel_EliminaVoceDefinitivamente_NonReferenziataSparisceEDalDisco()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = true } });
        var archivio = new ArchivioViewModel(state);
        var voce = archivio.Voci.Single(v => v.Etichetta == "R1");

        await archivio.EliminaVoceDefinitivamenteCommand.ExecuteAsync(voce);

        Assert.DoesNotContain(archivio.Voci, v => v.Etichetta == "R1");
        Assert.DoesNotContain(state.Master.Requisiti.Load(), r => r.Id == "R1");
    }

    [Fact]
    public void ArchivioViewModel_IncludeITestEliminati()
    {
        var state = CreaStatoComeMaster();
        var tc = new TestCase { Id = "TC-1", Componente = "BCM" };
        state.TestCases.Save(tc, true);
        state.TestCases.SoftDelete(tc, "Master Prova");

        var archivio = new ArchivioViewModel(state);

        Assert.Contains(archivio.Voci, v => v.Etichetta == "TC-1");
    }

    [Fact]
    public async Task ArchivioViewModel_RipristinaTest_LoRimetteAttivo()
    {
        var state = CreaStatoComeMaster();
        var tc = new TestCase { Id = "TC-1", Componente = "BCM" };
        state.TestCases.Save(tc, true);
        state.TestCases.SoftDelete(tc, "Master Prova");
        var archivio = new ArchivioViewModel(state);
        var voce = archivio.Voci.Single(v => v.Etichetta == "TC-1");

        await archivio.RipristinaVoceCommand.ExecuteAsync(voce);

        Assert.False(state.TestCases.GetById("TC-1")!.Eliminato);
    }

    [Fact]
    public async Task ArchivioViewModel_SvuotaArchivio_EliminaSoloLeVociNonReferenziate()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito>
        {
            new() { Id = "R1", Archiviata = true },
            new() { Id = "R2", Archiviata = true },
        });
        state.TestCases.Save(new TestCase { Id = "TC-1", RequisitoId = new List<string> { "R1" } }, true);
        var archivio = new ArchivioViewModel(state);

        await archivio.SvuotaArchivioCommand.ExecuteAsync(null);

        Assert.Contains(state.Master.Requisiti.Load(), r => r.Id == "R1"); // referenziata, resta
        Assert.DoesNotContain(state.Master.Requisiti.Load(), r => r.Id == "R2"); // non referenziata, eliminata
    }

    [Fact]
    public async Task ArchivioViewModel_SvuotaArchivio_MostraProgressoEBloccaAltreAzioniFinoAlTermine()
    {
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = true } });
        using var archivio = new ArchivioViewModel(state);

        var ingressoWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rilasciaAvvio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primoElementoCompletato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressoRicevuto = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rilasciaPrimoElemento = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressoSulDispatcher = 0;
        var progressoBannerNotificato = 0;
        archivio.GatePrimaDiSvuotarePerTest = () =>
        {
            ingressoWorker.TrySetResult();
            rilasciaAvvio.Task.GetAwaiter().GetResult();
        };
        archivio.GateDopoPrimoElementoSvuotatoPerTest = () =>
        {
            primoElementoCompletato.TrySetResult();
            rilasciaPrimoElemento.Task.GetAwaiter().GetResult();
        };
        archivio.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(archivio.MostraAvanzamentoSvuotamento) && archivio.SvuotamentoCompletati > 0)
                Interlocked.Exchange(ref progressoBannerNotificato, 1);
            if (args.PropertyName == nameof(archivio.SvuotamentoCompletati) && archivio.SvuotamentoCompletati > 0)
            {
                Interlocked.Exchange(ref progressoSulDispatcher,
                    Avalonia.Threading.Dispatcher.UIThread.CheckAccess() ? 1 : 0);
                progressoRicevuto.TrySetResult();
            }
        };

        var comando = archivio.SvuotaArchivioCommand.ExecuteAsync(null);
        await ingressoWorker.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(archivio.IsBusy);
        Assert.True(archivio.MostraAvanzamentoSvuotamento);
        Assert.True(archivio.SvuotamentoIndeterminato);
        Assert.False(archivio.SvuotaArchivioCommand.CanExecute(null));
        Assert.False(archivio.RipristinaVoceCommand.CanExecute(archivio.Voci.Single()));
        Assert.False(archivio.EliminaVoceDefinitivamenteCommand.CanExecute(archivio.Voci.Single()));

        rilasciaAvvio.TrySetResult();
        await Task.WhenAll(
            primoElementoCompletato.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            progressoRicevuto.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(archivio.IsBusy);
        Assert.False(archivio.SvuotamentoIndeterminato);
        Assert.Equal(1, archivio.SvuotamentoCompletati);
        Assert.Equal(1, archivio.SvuotamentoTotale);
        Assert.Equal(1, Volatile.Read(ref progressoSulDispatcher));
        Assert.Equal(1, Volatile.Read(ref progressoBannerNotificato));
        rilasciaPrimoElemento.TrySetResult();
        await comando;

        Assert.False(archivio.IsBusy);
        Assert.False(archivio.MostraAvanzamentoSvuotamento);
        Assert.Null(archivio.ProgressoSvuotamento);
        Assert.DoesNotContain(archivio.Voci, voce => voce.Etichetta == "R1");
    }

    [Fact]
    public async Task ArchivioViewModel_EliminazioneMasterVieneInoltrataAlDispatcherUi()
    {
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = true } });
        using var archivio = new ArchivioViewModel(state);
        var voceMaster = archivio.Voci.Single(v => v.Etichetta == "R1");
        Assert.True(voceMaster.EliminazioneRichiedeDispatcherUi);

        var threadEliminazioneCorretto = 0;
        var voceSonda = new VoceArchivio
        {
            IdTecnico = "master-test:dispatcher",
            TipoEntita = "Test dispatcher",
            Etichetta = "dispatcher",
            Ripristina = () => Task.FromResult<string?>(null),
            EliminazioneRichiedeDispatcherUi = voceMaster.EliminazioneRichiedeDispatcherUi,
            EliminaDefinitivamente = () =>
            {
                Interlocked.Exchange(ref threadEliminazioneCorretto,
                    Avalonia.Threading.Dispatcher.UIThread.CheckAccess() ? 1 : 0);
                return Task.FromResult<string?>(null);
            }
        };

        var errore = await Task.Run(() => ArchivioViewModel.EliminaVoceInBackgroundAsync(voceSonda));

        Assert.Null(errore);
        Assert.Equal(1, Volatile.Read(ref threadEliminazioneCorretto));
    }

    [Fact]
    public async Task ArchivioViewModel_SvuotaArchivio_AnnullamentoConfermaNonAvviaIlLavoro()
    {
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(false);
        var state = CreaStatoComeMaster();
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Archiviata = true } });
        using var archivio = new ArchivioViewModel(state);
        var workerAvviato = false;
        archivio.GatePrimaDiSvuotarePerTest = () => workerAvviato = true;

        await archivio.SvuotaArchivioCommand.ExecuteAsync(null);

        Assert.False(workerAvviato);
        Assert.False(archivio.IsBusy);
        Assert.Null(archivio.ProgressoSvuotamento);
        Assert.Contains(archivio.Voci, voce => voce.Etichetta == "R1");
    }
}
