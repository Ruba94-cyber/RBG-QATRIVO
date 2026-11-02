using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Regressioni di integrita' per il paging locale dei master data. SQLite e' una vista di consultazione:
/// qualunque operazione che possa scrivere deve tornare alla fotografia JSON completa dopo il lock, e
/// l'Archivio deve risolvere le righe per chiave sulla versione autorevole corrente.
/// </summary>
public sealed class MasterDataPagingSafetyTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }
    }

    private AppState CreateMasterState()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterPaging_" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        Directory.CreateDirectory(dir);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(dir);
        var operatori = state.Master.Operatori.Load();
        operatori.Add(TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master Paging",
            Ruolo = RuoloOperatore.Master,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Test"
        }));
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore("Master Paging", "1234"));
        return state;
    }

    [Fact]
    public async Task Salvataggio_RileggeLoStatoDelLockDopoIlRilascio()
    {
        using var state = CreateMasterState();
        using var vm = new RequisitiViewModel(state);
        using var continua = new ManualResetEventSlim();
        var avviato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.RilascioSessioneLockPerTest = (locks, percorso, sessione) =>
        {
            avviato.TrySetResult();
            if (!continua.Wait(TimeSpan.FromSeconds(10))) return EsitoIo.Errore;
            return locks.ReleaseIfSessionTracked(percorso, sessione);
        };

        await vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica);
        await vm.SalvaCommand.ExecuteAsync(null);
        await avviato.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Simula una lettura precedente del banner mentre il rilascio è ancora in corso.
        vm.LockDetenutoDaAltri = true;
        continua.Set();
        await vm.RilascioSessioneLockInCorso!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(vm.RilascioLockInAttesa);
        Assert.False(vm.LockDetenutoDaAltri);
        Assert.True(vm.MostraModifica);
    }

    [Fact]
    public async Task Salvataggio_MostraUnLockAltruiAcquisitoDopoIlRilascio()
    {
        using var state = CreateMasterState();
        using var vm = new RequisitiViewModel(state);
        var altroClient = new LockService();
        vm.RilascioSessioneLockPerTest = (locks, percorso, sessione) =>
        {
            var esito = locks.ReleaseIfSessionTracked(percorso, sessione);
            if (esito == EsitoIo.Ok && !altroClient.TryAcquire(percorso, "Altro PC").Acquisito)
                return EsitoIo.Errore;
            return esito;
        };

        await vm.IniziaModificaCommand.ExecuteAsync(null);
        await vm.SalvaCommand.ExecuteAsync(null);
        await vm.RilascioSessioneLockInCorso!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(vm.RilascioLockInAttesa);
        Assert.True(vm.LockDetenutoDaAltri);
        Assert.Contains("Altro PC", vm.LockInfoTesto);
    }

    [Fact]
    public void Requisiti_PagingNavigaSenzaMaterializzareIlCatalogoCompleto()
    {
        using var state = CreateMasterState();
        state.Master.Requisiti.Save(Enumerable.Range(0, 450)
            .Select(i => new Requisito
            {
                Id = $"REQ-{i:D3}",
                Testo = $"Requisito {i:D3}",
                Archiviata = i == 17,
                ModificatoDa = "Seed"
            })
            .ToList());

        using var vm = new RequisitiViewModel(state);
        Assert.True(vm.UsaPaginazione);
        Assert.Equal(200, vm.Items.Count);
        Assert.DoesNotContain(vm.Items, r => r.Id == "REQ-017");
        Assert.True(vm.PaginaSuccessivaDisponibile);
        Assert.False(vm.PaginaPrecedenteDisponibile);

        vm.PaginaSuccessivaCommand.Execute(null);
        Assert.Equal(2, vm.PaginaCorrente);
        Assert.Equal(200, vm.Items.Count);
        Assert.True(vm.PaginaPrecedenteDisponibile);
        Assert.True(vm.PaginaSuccessivaDisponibile);

        vm.PaginaSuccessivaCommand.Execute(null);
        Assert.Equal(3, vm.PaginaCorrente);
        Assert.Equal(49, vm.Items.Count); // 450 totali - 1 archiviato - 400 nelle prime due pagine.
        Assert.False(vm.PaginaSuccessivaDisponibile);
    }

    [Fact]
    public void Requisiti_FiltroTrovaUnaRigaFuoriDallaPrimaPaginaEResettaPagina()
    {
        using var state = CreateMasterState();
        state.Master.Requisiti.Save(Enumerable.Range(0, 450)
            .Select(i => new Requisito
            {
                Id = $"REQ-{i:D3}",
                Testo = i == 432 ? "bersaglio molto lontano" : $"Requisito {i:D3}",
                ModificatoDa = "Seed"
            })
            .ToList());

        using var vm = new RequisitiViewModel(state);
        vm.PaginaSuccessivaCommand.Execute(null);
        Assert.Equal(2, vm.PaginaCorrente);

        vm.FiltroTesto = "bersaglio molto lontano";
        vm.ConfermaRicercaImmediata(); // Invio: la ricerca paginata si applica subito, senza attendere il debounce

        Assert.Equal(1, vm.PaginaCorrente);
        var hit = Assert.Single(vm.ItemsAttivi);
        Assert.Equal("REQ-432", hit.Id);
        Assert.False(vm.PaginaSuccessivaDisponibile);
    }

    [Fact]
    public void EditorPaginato_IniziaModificaRileggeIlJsonCompletoDopoIlLock()
    {
        using var state = CreateMasterState();
        state.Master.Componente.Save(Enumerable.Range(0, 450)
            .Select(i => new Componente { Nome = $"Componente-{i:D3}", ModificatoDa = "Seed" })
            .ToList());

        using var vm = new EcuViewModel(state);
        Assert.True(vm.UsaPaginazione);
        Assert.Equal(200, vm.Items.Count);

        // Simula una replica che arriva dopo l'apertura della pagina ma PRIMA dell'inizio modifica. Il nome
        // ordina prima di "Componente-000" cosi' ricade comunque nella prima pagina: dal refactor "Edit Phase D master
        // pages without full-catalog promotion" (91fc21f) l'editing resta O(page-size) in RAM, non promuove
        // piu' al catalogo completo — la garanzia che questo test verifica e' che il refresh riconcili la
        // finestra caricata, non che OGNI riga remota diventi visibile a prescindere da dove ordina.
        var remoto = state.Master.Componente.Load();
        remoto.Add(new Componente { Nome = "0000-componente-REMOTA", ModificatoDa = "PC-B" });
        state.Master.Componente.Save(remoto);

        vm.IniziaModificaCommand.AttendiModifica();

        Assert.True(vm.InModifica);
        Assert.True(vm.UsaPaginazione);
        Assert.True(vm.MostraControlliPaginazione);
        Assert.True(vm.MostraSalvaPagina);
        Assert.Equal(vm.DimensionePagina, vm.Items.Count);
        Assert.Contains(vm.Items, e => e.Nome == "0000-componente-REMOTA");
    }

    [Fact]
    public async Task Funzionalita_PagineInModificaConsentonoCommitEsplicitoSenzaPerdereLock()
    {
        using var state = CreateMasterState();
        state.Master.Funzionalita.Save(Enumerable.Range(0, 450)
            .Select(i => new Funzionalita { Nome = $"FUNZ-{i:D3}", ModificatoDa = "Seed" })
            .ToList());

        using var vm = new FunzionalitaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();

        Assert.True(vm.InModifica);
        Assert.True(vm.UsaPaginazione);
        Assert.True(vm.MostraControlliPaginazione);
        Assert.True(vm.MostraSalvaPagina);
        Assert.Equal(200, vm.Items.Count);

        vm.Items.Single(f => f.Nome == "FUNZ-000").Categorie.Add("Pagina uno");
        vm.PaginaSuccessivaCommand.Execute(null);
        Assert.Equal(1, vm.PaginaCorrente);
        Assert.Equal(LocalizationService.Instance.Traduci("MasterDataSalvaPaginaPrimaDiNavigare"), vm.MessaggioErrore);

        await vm.SalvaPaginaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica);
        Assert.True(state.Locks.IsOwnedByThisSession(state.Paths.FunzionalitaFile));
        Assert.Equal(LocalizationService.Instance.Traduci("MasterDataPaginaSalvata"), vm.Messaggio);
        vm.PaginaSuccessivaCommand.Execute(null);
        Assert.Equal(2, vm.PaginaCorrente);
        Assert.Contains(vm.Items, f => f.Nome == "FUNZ-250");

        vm.Items.Single(f => f.Nome == "FUNZ-250").Categorie.Add("Pagina due");
        await vm.SalvaPaginaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica);
        vm.PaginaSuccessivaCommand.Execute(null);
        Assert.Equal(3, vm.PaginaCorrente);
        Assert.Contains(vm.Items, f => f.Nome == "FUNZ-432");

        vm.Items.Single(f => f.Nome == "FUNZ-432").Categorie.Add("Pagina tre");
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.InModifica);
        var salvate = state.Master.Funzionalita.Load();
        Assert.Contains("Pagina uno", salvate.Single(f => f.Nome == "FUNZ-000").Categorie);
        Assert.Contains("Pagina due", salvate.Single(f => f.Nome == "FUNZ-250").Categorie);
        Assert.Contains("Pagina tre", salvate.Single(f => f.Nome == "FUNZ-432").Categorie);
    }

    [Fact]
    public async Task Funzionalita_SalvaPaginaInConflittoConservaModificheLockENavigazioneBloccata()
    {
        using var state = CreateMasterState();
        state.Master.Funzionalita.Save(Enumerable.Range(0, 450)
            .Select(i => new Funzionalita { Nome = $"FUNZ-{i:D3}", ModificatoDa = "Seed" })
            .ToList());

        using var vm = new FunzionalitaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.Items.Single(f => f.Nome == "FUNZ-000").Categorie.Add("Modifica locale");

        var remoto = state.Master.Funzionalita.Load();
        remoto.Single(f => f.Nome == "FUNZ-000").Categorie.Add("Modifica remota");
        state.Master.Funzionalita.Save(remoto);

        await vm.SalvaPaginaCommand.ExecuteAsync(null);

        Assert.True(vm.InModifica);
        Assert.True(state.Locks.IsOwnedByThisSession(state.Paths.FunzionalitaFile));
        Assert.Contains("Modifica locale", vm.Items.Single(f => f.Nome == "FUNZ-000").Categorie);
        Assert.Contains("Modifica remota", state.Master.Funzionalita.Load().Single(f => f.Nome == "FUNZ-000").Categorie);
        Assert.DoesNotContain("Modifica locale", state.Master.Funzionalita.Load().Single(f => f.Nome == "FUNZ-000").Categorie);

        vm.PaginaSuccessivaCommand.Execute(null);
        Assert.Equal(1, vm.PaginaCorrente);
        Assert.Equal(LocalizationService.Instance.Traduci("MasterDataSalvaPaginaPrimaDiNavigare"), vm.MessaggioErrore);
    }

    [Fact]
    public async Task Requisiti_ModificaPaginataConservaCommitPaginaELock()
    {
        using var state = CreateMasterState();
        state.Master.Requisiti.Save(Enumerable.Range(0, 450)
            .Select(i => new Requisito { Id = $"REQ-{i:D3}", Testo = $"Requisito {i:D3}", ModificatoDa = "Seed" })
            .ToList());

        using var vm = new RequisitiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();

        Assert.True(vm.UsaPaginazione);
        Assert.True(vm.MostraControlliPaginazione);
        Assert.True(vm.MostraSalvaPagina);
        Assert.Equal(200, vm.Items.Count);

        vm.Items.Single(r => r.Id == "REQ-000").Testo = "Requisito aggiornato";
        await vm.SalvaPaginaCommand.ExecuteAsync(null);

        Assert.True(vm.InModifica);
        Assert.True(state.Locks.IsOwnedByThisSession(state.Paths.RequisitiFile));
        Assert.Equal("Requisito aggiornato", state.Master.Requisiti.Load().Single(r => r.Id == "REQ-000").Testo);

        vm.PaginaSuccessivaCommand.Execute(null);
        Assert.Equal(2, vm.PaginaCorrente);
        Assert.Contains(vm.Items, r => r.Id == "REQ-250");
        vm.AnnullaCommand.Execute(null);
        Assert.False(vm.InModifica);
        Assert.Equal("Requisito aggiornato", state.Master.Requisiti.Load().Single(r => r.Id == "REQ-000").Testo);
    }

    [Fact]
    public async Task ArchivioPaginato_RipristinaPerChiaveSullaFotografiaAutorevoleCorrente()
    {
        using var state = CreateMasterState();
        state.Master.Requisiti.Save(Enumerable.Range(0, 450)
            .Select(i => new Requisito
            {
                Id = $"REQ-{i:D3}",
                Testo = $"Requisito {i:D3}",
                Archiviata = i == 432,
                ModificatoDa = "Seed"
            })
            .ToList());

        using var vm = new RequisitiViewModel(state);
        Assert.True(vm.UsaPaginazione);
        Assert.DoesNotContain(vm.Items, r => r.Id == "REQ-432");

        var archiviato = Assert.Single(vm.CaricaElementiArchivio());
        Assert.Equal("REQ-432", archiviato.Id);
        var errore = await vm.RipristinaDaArchivio(vm.ChiaveArchivio(archiviato));

        Assert.Null(errore);
        Assert.False(state.Master.Requisiti.Load().Single(r => r.Id == "REQ-432").Archiviata);
        Assert.True(vm.UsaPaginazione);
        Assert.True(vm.Items.Count <= vm.DimensionePagina);
    }

    [Fact]
    public async Task ArchivioPaginato_VoceAssenteIncludeAncheIlFallimentoDelRilascioLock()
    {
        using var state = CreateMasterState();
        state.Master.Requisiti.Save(Enumerable.Range(0, 450)
            .Select(i => new Requisito { Id = $"REQ-{i:D3}", ModificatoDa = "Seed" })
            .ToList());
        using var vm = new RequisitiViewModel(state);
        vm.RilascioSessioneLockPerTest = (_, _, _) => EsitoIo.Errore;
        vm.AttesaRetryLockPerTest = _ => Task.CompletedTask;

        var errore = await vm.RipristinaDaArchivio("REQ-MANCANTE");

        Assert.NotNull(errore);
        Assert.Contains(LocalizationService.Instance.Traduci("MasterDataVoceNonPiuPresente"), errore);
        Assert.Contains(LocalizationService.Instance.Traduci("LockSbloccoNonRiuscito"), errore);
        Assert.True(vm.RilascioLockInAttesa);

        vm.RilascioSessioneLockPerTest = (locks, percorso, sessione) => locks.ReleaseIfSessionTracked(percorso, sessione);
        await vm.RiprovaRilascioLockCommand.ExecuteAsync(null);
        Assert.False(vm.RilascioLockInAttesa);
        Assert.Null(state.Locks.GetActiveLock(state.Paths.RequisitiFile));
    }

    [Fact]
    public async Task ArchivioPaginato_RipristinoNonBloccaIlDispatcherDuranteAcquisizioneERilascioLock()
    {
        using var state = CreateMasterState();
        state.Master.Requisiti.Save(new List<Requisito>
        {
            new() { Id = "REQ-ARCHIVE", Archiviata = true, ModificatoDa = "Seed" }
        });
        using var vm = new RequisitiViewModel(state);
        var archiviato = Assert.Single(vm.CaricaElementiArchivio());
        var acquisizioneInAttesa = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaAcquisizione = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rilascioAvviato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaRilascio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?>? azione = null;
        var timeout = TimeSpan.FromSeconds(10);
        var gate = LockGate.EseguiAsync(state.Paths.RequisitiFile, async () =>
        {
            acquisizioneInAttesa.TrySetResult();
            await continuaAcquisizione.Task;
        });
        vm.RilascioSessioneLockPerTest = (locks, percorso, sessione) =>
        {
            rilascioAvviato.TrySetResult();
            continuaRilascio.Task.GetAwaiter().GetResult();
            return locks.ReleaseIfSessionTracked(percorso, sessione);
        };

        try
        {
            await Avalonia.Threading.Dispatcher.UIThread
                .InvokeAsync((Action)(() => azione = vm.RipristinaDaArchivio(vm.ChiaveArchivio(archiviato))))
                .GetTask()
                .WaitAsync(timeout);
            await acquisizioneInAttesa.Task.WaitAsync(timeout);

            var dispatcherResponsiveDuranteAcquisizione = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await Avalonia.Threading.Dispatcher.UIThread
                .InvokeAsync((Action)(() => dispatcherResponsiveDuranteAcquisizione.TrySetResult()))
                .GetTask()
                .WaitAsync(timeout);
            Assert.False(rilascioAvviato.Task.IsCompleted);
            Assert.NotNull(azione);
            Assert.False(azione!.IsCompleted);

            continuaAcquisizione.TrySetResult();
            await rilascioAvviato.Task.WaitAsync(timeout);

            var dispatcherResponsiveDuranteRilascio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await Avalonia.Threading.Dispatcher.UIThread
                .InvokeAsync((Action)(() => dispatcherResponsiveDuranteRilascio.TrySetResult()))
                .GetTask()
                .WaitAsync(timeout);

            Assert.True(dispatcherResponsiveDuranteRilascio.Task.IsCompleted);
            Assert.False(azione!.IsCompleted);
        }
        finally
        {
            continuaAcquisizione.TrySetResult();
            continuaRilascio.TrySetResult();
            await gate.WaitAsync(timeout);
            if (azione is not null) await azione.WaitAsync(timeout);
        }

        Assert.False(state.Master.Requisiti.Load().Single().Archiviata);
    }

    [Fact]
    public async Task ArchivioPaginato_EliminazioneDefinitivaNonSalvaMaiUnaPaginaParziale()
    {
        using var state = CreateMasterState();
        state.Master.Componente.Save(Enumerable.Range(0, 450)
            .Select(i => new Componente
            {
                Nome = $"Componente-{i:D3}",
                Archiviata = i == 432,
                ModificatoDa = "Seed"
            })
            .ToList());

        using var vm = new EcuViewModel(state);
        Assert.Equal(200, vm.Items.Count);
        var archiviato = Assert.Single(vm.CaricaElementiArchivio());
        Assert.Equal("Componente-432", archiviato.Nome);

        var errore = await vm.EliminaDefinitivamenteDaArchivio(vm.ChiaveArchivio(archiviato));

        Assert.Null(errore);
        var autorevole = state.Master.Componente.Load();
        Assert.Equal(449, autorevole.Count);
        Assert.DoesNotContain(autorevole, e => e.Nome == "Componente-432");
        Assert.Contains(autorevole, e => e.Nome == "Componente-000");
        Assert.Contains(autorevole, e => e.Nome == "Componente-449");
    }

    [Fact]
    public async Task EditorPaginato_SalvaIgnoratoFuoriDaModalitaModifica()
    {
        using var state = CreateMasterState();
        state.Master.Funzionalita.Save(Enumerable.Range(0, 450)
            .Select(i => new Funzionalita { Nome = $"FUN-{i:D3}", ModificatoDa = "Seed" })
            .ToList());

        using var vm = new FunzionalitaViewModel(state);
        Assert.True(vm.UsaPaginazione);
        Assert.Equal(200, vm.Items.Count);

        // Anche se un caller tentasse programmaticamente il comando sulla pagina locale, Salva() deve uscire
        // immediatamente perché non esiste una sessione di modifica con snapshot autorevole completo.
        vm.Items[0].Nome = "NON-DEVE-ESSERE-SALVATO";
        await vm.SalvaCommand.ExecuteAsync(null);

        var autorevole = state.Master.Funzionalita.Load();
        Assert.Equal(450, autorevole.Count);
        Assert.DoesNotContain(autorevole, f => f.Nome == "NON-DEVE-ESSERE-SALVATO");
    }
}
