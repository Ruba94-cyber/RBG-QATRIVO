using System.Reflection;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Finestra "Componente e funzionalità" del veicolo: elenco e filtri, modifica con lock, salvataggio atomico con changelog,
/// lock di un'altra postazione, sola lettura per il ruolo Viewer, conflitto remoto e rilascio dei lock alla chiusura.</summary>
[Collection("Localization")]
public sealed class AssociazioniVeicoloViewModelTests : IDisposable
{
    private const string V1 = "VEI-1";
    private const string V2 = "VEI-2";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_AssociazioniVm_" + Guid.NewGuid().ToString("N"));

    private static async Task AttendiRilascioLockAsync(MasterDataEditorViewModelBase<Funzionalita> vm)
    {
        if (!vm.RilascioLockInAttesa) return;

        var completato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(vm.RilascioLockInAttesa) && !vm.RilascioLockInAttesa)
                completato.TrySetResult();
        };
        vm.PropertyChanged += handler;
        try
        {
            if (vm.RilascioLockInAttesa)
                await completato.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            vm.PropertyChanged -= handler;
        }

        Assert.False(vm.RilascioLockInAttesa);
    }

    private static int RigheJournal(AppState state)
    {
        var percorso = PhaseDMasterJournalService.CurrentPath(state.Paths.Root, DeviceIdentityService.GetOrCreate());
        return File.Exists(percorso) ? File.ReadLines(percorso).Count(r => r.Trim().Length > 0) : 0;
    }

    [Fact]
    public async Task DopoIlSalvataggio_LePagineEcuEFunzionaliteLeggonoLoStatoAggiornato()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        using (var vm = new AssociazioniVeicoloViewModel(state, V1))
        {
            vm.IniziaModificaCommand.AttendiModifica();
            vm.RigheVisibili.Single(r => r.Nome == "Componente-C").Montata = true;
            vm.RigheVisibili.Single(r => r.Nome == "Componente-B").Espansa = true;
            vm.RigheVisibili.Single(r => r.Nome == "Componente-B").Funzioni.Single(f => f.Nome == "Diagnosi").Applicata = false;
            await vm.SalvaCommand.ExecuteAsync(null);
        }

        // Le pagine Componente e Funzionalità, aperte dopo il salvataggio, leggono il catalogo appena scritto.
        using var pagineEcu = new EcuViewModel(state);
        using var pagineFunzionalita = new FunzionalitaViewModel(state);
        Assert.Contains(V1, pagineEcu.Items.Single(e => e.Nome == "Componente-C").VeicoliAssociati);
        var diagnosi = pagineFunzionalita.Items.Single(f => f.Nome == "Diagnosi");
        Assert.Equal(new[] { V2 }, diagnosi.VeicoliRistretti("Componente-B"));
    }

    [Fact]
    public async Task ModificaConEffettoNetto_Nullo_NonScriveNessunBatch()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        using var vm = new AssociazioniVeicoloViewModel(state, V1);
        vm.IniziaModificaCommand.AttendiModifica();
        var righePrima = RigheJournal(state);

        // Disattivare e riattivare la stessa funzionalità sullo stesso veicolo non cambia nulla: niente batch, niente changelog.
        vm.RigheVisibili.Single(r => r.Nome == "Componente-B").Espansa = true;
        vm.RigheVisibili.Single(r => r.Nome == "Componente-B").Funzioni.Single(f => f.Nome == "Diagnosi").Applicata = false;
        vm.RigheVisibili.Single(r => r.Nome == "Componente-B").Funzioni.Single(f => f.Nome == "Diagnosi").Applicata = true;
        Assert.False(vm.HaModificheDaSalvare);

        var rinnovoEntrato = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaRinnovo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.RinnovaLockHeartbeatPerTest = async (locks, percorso, operatore) =>
        {
            rinnovoEntrato.TrySetResult();
            await continuaRinnovo.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return await locks.EnsureOwnedTrackedAsync(percorso, operatore);
        };
        var salvataggio = vm.SalvaCommand.ExecuteAsync(null);
        await rinnovoEntrato.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.SalvataggioInCorso);
        Assert.False(vm.InterazioneEditorAbilitata);
        continuaRinnovo.TrySetResult();
        await salvataggio.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.InModifica);
        Assert.False(vm.SalvataggioInCorso);
        Assert.True(vm.InterazioneEditorAbilitata);
        Assert.Equal(righePrima, RigheJournal(state));
        Assert.Equal(LocalizationService.Instance.Traduci("AssociazioniVeicoloNessunaModifica"), vm.Messaggio);
    }

    [Fact]
    public async Task SalvataggioConLockIlleggibile_RetryRilasciaSenzaRiscrivereIlBatch()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        using var vm = new AssociazioniVeicoloViewModel(state, V1);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.RigheVisibili.Single(r => r.Nome == "Componente-C").Montata = true;

        var lockPath = LockService.LockPathFor(state.Paths.EcuFile);
        var lockOriginale = File.ReadAllText(lockPath);
        File.WriteAllText(lockPath, "{corrotto");
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.InModifica);
        Assert.True(vm.RilascioLockInAttesa);
        Assert.Equal(LocalizationService.Instance.Traduci("SalvatoSbloccoLockNonRiuscito"), vm.MessaggioErrore);
        Assert.Contains(V1, state.Master.Componente.Load().Single(e => e.Nome == "Componente-C").VeicoliAssociati);
        var journalDopoSalva = RigheJournal(state);
        var changelogDopoSalva = state.Master.Componente.Load().Single(e => e.Nome == "Componente-C").Changelog.Count;
        Assert.True(journalDopoSalva > 0);
        Assert.True(File.Exists(lockPath));

        File.WriteAllText(lockPath, lockOriginale);
        await vm.RiprovaRilascioLockCommand.ExecuteAsync(null);

        Assert.False(vm.RilascioLockInAttesa);
        Assert.Null(vm.MessaggioErrore);
        Assert.Equal(journalDopoSalva, RigheJournal(state));
        Assert.Equal(changelogDopoSalva, state.Master.Componente.Load().Single(e => e.Nome == "Componente-C").Changelog.Count);
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task SalvataggioDallaFinestra_RiallineaLePagineGiaAperteEcuFunzionaleVeicoli()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = V1 }, new() { Nome = V2 } });
        // Pagine aperte PRIMA del salvataggio: devono riallinearsi da sole (DatiRicaricati), senza essere ricreate.
        using var pagineEcu = new EcuViewModel(state);
        using var pagineFunzionalita = new FunzionalitaViewModel(state);
        using var pagineVeicoli = new VeicoliViewModel(state);
        pagineVeicoli.ElementoSelezionato = pagineVeicoli.Items.Single(v => v.Nome == V1);
        Assert.DoesNotContain(V1, pagineEcu.Items.Single(e => e.Nome == "Componente-C").VeicoliAssociati);
        Assert.Null(pagineFunzionalita.Items.Single(f => f.Nome == "Diagnosi").VeicoliRistretti("Componente-B"));

        using (var vm = new AssociazioniVeicoloViewModel(state, V1))
        {
            vm.IniziaModificaCommand.AttendiModifica();
            vm.RigheVisibili.Single(r => r.Nome == "Componente-C").Montata = true;
            vm.RigheVisibili.Single(r => r.Nome == "Componente-B").Espansa = true;
            vm.RigheVisibili.Single(r => r.Nome == "Componente-B").Funzioni.Single(f => f.Nome == "Diagnosi").Applicata = false;
            await vm.SalvaCommand.ExecuteAsync(null);
        }

        Assert.Contains(V1, pagineEcu.Items.Single(e => e.Nome == "Componente-C").VeicoliAssociati);
        Assert.Equal(new[] { V2 }, pagineFunzionalita.Items.Single(f => f.Nome == "Diagnosi").VeicoliRistretti("Componente-B"));
        // La selezione resta sulla stessa voce (ricollegata per chiave alla lista riletta).
        Assert.Same(pagineVeicoli.Items.Single(v => v.Nome == V1), pagineVeicoli.ElementoSelezionato);
    }

    [Fact]
    public void SalvataggioAtomico_RiallineaICataloghiDiRiferimentoDellePagineAperte()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        using var pagineEcu = new EcuViewModel(state);
        using var pagineFunzionalita = new FunzionalitaViewModel(state);
        Assert.DoesNotContain("Componente-NUOVA", pagineFunzionalita.EcuDisponibili);
        Assert.DoesNotContain("VEI-NUOVO", pagineFunzionalita.VeicoliDisponibili);
        Assert.DoesNotContain("VEI-NUOVO", pagineEcu.VeicoliDisponibili); // popola la cache letta dalla validazione Excel

        // Stesso salvataggio atomico della finestra: un componente nuova, poi un veicolo nuovo nel suo catalogo.
        var impronteEcu = state.Master.Componente.CatturaImpronteCatalogoCorrente();
        var ecuPrima = state.Master.Componente.Load();
        var impronteFunzionalita = state.Master.Funzionalita.CatturaImpronteCatalogoCorrente();
        var funzionalitaPrima = state.Master.Funzionalita.Load();
        MasterDataSalvataggioAtomico.Salva(state.Paths, state.Master,
            new(ecuPrima.Append(new Componente { Nome = "Componente-NUOVA" }).ToList(), Copia(ecuPrima), impronteEcu),
            new(funzionalitaPrima, Copia(funzionalitaPrima), impronteFunzionalita),
            allowSilentRemoteMerge: false);
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = V1 }, new() { Nome = V2 }, new() { Nome = "VEI-NUOVO" } });
        state.NotificaDatiRicaricati();

        Assert.Contains("Componente-NUOVA", pagineFunzionalita.EcuDisponibili);
        Assert.NotNull(pagineFunzionalita.EcuAttivaPerNome("Componente-NUOVA"));
        Assert.Contains("Componente-NUOVA", pagineEcu.Items.Select(e => e.Nome));
        Assert.Contains("VEI-NUOVO", pagineFunzionalita.VeicoliDisponibili);
        Assert.Contains("VEI-NUOVO", pagineEcu.VeicoliDisponibili);
    }

    [Fact]
    public async Task FunzionalitaViewModel_ImpostaVeicoliEcu_ScriveLaRestrizioneSoloInModificaEConfermaConIlSalvataggio()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        using var vm = new FunzionalitaViewModel(state);

        // Fuori dalla modifica il picker non scrive nulla.
        vm.ImpostaVeicoliEcu(vm.Items.Single(f => f.Nome == "Diagnosi"), "Componente-B", new[] { V2 });
        Assert.Null(vm.Items.Single(f => f.Nome == "Diagnosi").VeicoliRistretti("Componente-B"));

        vm.IniziaModificaCommand.AttendiModifica();
        var diagnosi = vm.Items.Single(f => f.Nome == "Diagnosi");
        vm.ImpostaVeicoliEcu(diagnosi, "Componente-B", new[] { V2 });
        Assert.Equal(new[] { V2 }, diagnosi.VeicoliRistretti("Componente-B"));
        Assert.True(vm.HaModificheNonSalvate());

        // Il salvataggio normale della pagina scrive la restrizione.
        await vm.SalvaCommand.ExecuteAsync(null);
        await AttendiRilascioLockAsync(vm);
        Assert.Equal(new[] { V2 }, state.Master.Funzionalita.Load().Single(f => f.Nome == "Diagnosi").VeicoliRistretti("Componente-B"));

        // Tutti i veicoli montati: nessuna restrizione (la voce viene tolta).
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImpostaVeicoliEcu(vm.Items.Single(f => f.Nome == "Diagnosi"), "Componente-B", new[] { V1, V2 });
        Assert.Empty(vm.Items.Single(f => f.Nome == "Diagnosi").VeicoliPerEcu);
    }

    [Fact]
    public void FunzionalitaViewModel_TestoVeicoliEcu_RiassumeISoliVeicoliMontati()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        using var vm = new FunzionalitaViewModel(state);
        var diagnosi = vm.Items.Single(f => f.Nome == "Diagnosi");

        // componente-B è montata su V1 e V2: senza restrizione la funzionalità vale su tutti.
        Assert.Equal(LocalizationService.Instance.Traduci("FunzEcuVeicoliTutti"), vm.TestoVeicoliEcu(diagnosi, "Componente-B"));

        // Restrizione su V2 più un veicolo non più montato (VEI-9): il riepilogo conta solo i montati.
        diagnosi.VeicoliPerEcu = new List<VeicoliFunzionalitaEcu> { new() { Componente = "Componente-B", Veicoli = new List<string> { V2, "VEI-9" } } };
        Assert.Equal(string.Format(LocalizationService.Instance.Traduci("FunzEcuVeicoliDiFmt"), 1, 2), vm.TestoVeicoliEcu(diagnosi, "Componente-B"));

        // Restrizione vuota: nessun veicolo.
        diagnosi.VeicoliPerEcu = new List<VeicoliFunzionalitaEcu> { new() { Componente = "Componente-B", Veicoli = new List<string>() } };
        Assert.Equal(LocalizationService.Instance.Traduci("FunzEcuVeicoliNessuno"), vm.TestoVeicoliEcu(diagnosi, "Componente-B"));

        // componente archiviata o non censita: nessun riepilogo da mostrare.
        Assert.Equal("", vm.TestoVeicoliEcu(diagnosi, "Componente-ARCH"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStato(bool master = true)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master Test",
            Ruolo = master ? RuoloOperatore.Master : RuoloOperatore.Viewer,
            PinHash = PinService.Hash("1234"),
            ModificatoDa = "Test",
        });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        Assert.True(state.ImpostaOperatore("Master Test", "1234"));
        return state;
    }

    /// <summary>componente: B e A montate su V1 (B anche su V2), C solo su V2, una archiviata su V1. Funzionalità: "Diagnosi" su tutte
    /// i componenti e "Solo componente-B" valida solo su componente-B.</summary>
    private static void SeminaEcuEFunzionalita(AppState state)
    {
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "Componente-B", VeicoliAssociati = new List<string> { V1, V2 } },
            new() { Nome = "Componente-A", VeicoliAssociati = new List<string> { V1 } },
            new() { Nome = "Componente-C", VeicoliAssociati = new List<string> { V2 } },
            new() { Nome = "Componente-ARCH", Archiviata = true, VeicoliAssociati = new List<string> { V1 } },
        });
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Diagnosi", TutteLeEcu = true },
            new() { Nome = "Solo componente-B", TutteLeEcu = false, EcuAssociate = new List<string> { "Componente-B" } },
        });
    }

    private static T Copia<T>(T valore) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(valore, JsonIo.Options), JsonIo.Options)!;

    [Fact]
    public void Apertura_ListaLeEcuNonArchiviateInOrdineEMarcaLeMontateSulVeicolo()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);

        using var vm = new AssociazioniVeicoloViewModel(state, V1);

        Assert.Equal(new[] { "Componente-A", "Componente-B", "Componente-C" }, vm.RigheVisibili.Select(r => r.Nome));
        Assert.Equal(new[] { "Componente-A", "Componente-B" }, vm.RigheVisibili.Where(r => r.Montata).Select(r => r.Nome));
        Assert.False(vm.InModifica);
        Assert.True(vm.PuoModificare);
    }

    [Fact]
    public void Filtri_SoloMontateERicercaRestringonoLElenco()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        using var vm = new AssociazioniVeicoloViewModel(state, V1);

        vm.SoloMontate = true;
        Assert.Equal(new[] { "Componente-A", "Componente-B" }, vm.RigheVisibili.Select(r => r.Nome));

        vm.Ricerca = "b";
        Assert.Equal(new[] { "Componente-B" }, vm.RigheVisibili.Select(r => r.Nome));

        vm.SoloMontate = false;
        vm.Ricerca = "";
        Assert.Equal(3, vm.RigheVisibili.Count);
    }

    [Fact]
    public void Funzioni_DiUnEcuMontataSonoLeApplicabiliEConLaRestrizioneDelVeicolo()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        using var vm = new AssociazioniVeicoloViewModel(state, V1);

        var b = vm.RigheVisibili.Single(r => r.Nome == "Componente-B");
        b.Espansa = true;

        Assert.Equal(new[] { "Diagnosi", "Solo componente-B" }, b.Funzioni.Select(f => f.Nome));
        Assert.All(b.Funzioni, f => Assert.True(f.Applicata));
        Assert.Empty(vm.RigheVisibili.Single(r => r.Nome == "Componente-C").Funzioni); // non montata: nessun dettaglio
    }

    [Fact]
    public async Task ModificaESalva_ScriveLeAssociazioniConIlRegistroEAggiornaLeRighe()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        using var vm = new AssociazioniVeicoloViewModel(state, V1);

        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);

        vm.RigheVisibili.Single(r => r.Nome == "Componente-C").Montata = true;
        var b = vm.RigheVisibili.Single(r => r.Nome == "Componente-B");
        b.Espansa = true;
        b.Funzioni.Single(f => f.Nome == "Diagnosi").Applicata = false;
        Assert.True(vm.HaModificheDaSalvare);

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.InModifica);
        Assert.False(vm.HaMessaggioErrore);
        Assert.True(vm.HaMessaggio);
        Assert.Contains(V1, state.Master.Componente.Load().Single(e => e.Nome == "Componente-C").VeicoliAssociati);
        var diagnosi = state.Master.Funzionalita.Load().Single(f => f.Nome == "Diagnosi");
        Assert.False(diagnosi.ValeSu("Componente-B", V1));   // restrizione per questo veicolo
        Assert.True(diagnosi.ValeSu("Componente-B", V2));    // l'altro veicolo del componente non cambia
        Assert.True(diagnosi.ValeSu("Componente-C", V1));
        Assert.Contains(diagnosi.Changelog, c => c.Campo == "VeicoliPerEcuTesto");
        Assert.Contains(state.Master.Componente.Load().Single(e => e.Nome == "Componente-C").Changelog, c => c.Campo == "VeicoliAssociatiTesto");
        Assert.True(vm.RigheVisibili.Single(r => r.Nome == "Componente-C").Montata); // le righe riflettono il salvato
    }

    [Fact]
    public void Modifica_ConLockDiAltraPostazione_RestaInLettura()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        var altraPostazione = new LockService("PC-ALTRO");
        Assert.True(altraPostazione.TryAcquire(state.Paths.EcuFile, "Altro Operatore").Acquisito);
        try
        {
            using var vm = new AssociazioniVeicoloViewModel(state, V1);
            Assert.True(vm.LockDetenutoDaAltri);

            vm.IniziaModificaCommand.AttendiModifica();

            Assert.False(vm.InModifica);
        }
        finally
        {
            altraPostazione.Release(state.Paths.EcuFile);
        }
    }

    [Fact]
    public void RuoloViewer_NonPuoModificare_EResteInLettura()
    {
        var state = CreaStato(master: false);
        SeminaEcuEFunzionalita(state);
        using var vm = new AssociazioniVeicoloViewModel(state, V1);

        Assert.False(vm.PuoModificare);
        Assert.True(vm.SoloLettura);
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.False(vm.InModifica);
    }

    [Fact]
    public async Task Salvataggio_ConModificaRemotaDopoLApertura_NonScriveEMantieneLaModifica()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        using var vm = new AssociazioniVeicoloViewModel(state, V1);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.RigheVisibili.Single(r => r.Nome == "Componente-C").Montata = true;

        // Un'altra postazione cambia le funzionalità dopo l'apertura: il salvataggio deve fermarsi senza scrivere nulla.
        var remoto = state.Master.Funzionalita.Load();
        var baseRemota = Copia(remoto);
        remoto.Single(f => f.Nome == "Diagnosi").Categorie = new List<string> { "modificata altrove" };
        state.Master.Funzionalita.Save(remoto, baseRemota);

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.InModifica);
        Assert.Equal(LocalizationService.Instance.Traduci("AssociazioniVeicoloConflitto"), vm.MessaggioErrore);
        Assert.DoesNotContain(V1, state.Master.Componente.Load().Single(e => e.Nome == "Componente-C").VeicoliAssociati);
    }

    [Fact]
    public async Task Chiusura_RilasciaILockDellaSessioneDiModifica()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        var vm = new AssociazioniVeicoloViewModel(state, V1);
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);
        var sessioneEcu = Assert.IsType<string>(state.Locks.GetActiveLock(state.Paths.EcuFile)?.SessionId);
        var sessioneFunzionalita = Assert.IsType<string>(state.Locks.GetActiveLock(state.Paths.FunzionalitaFile)?.SessionId);

        vm.Dispose();
        Assert.All(await Task.WhenAll(
            LockGate.RilasciaSessioneEsattaConRetryAsync(state.Locks, state.Paths.EcuFile, sessioneEcu),
            LockGate.RilasciaSessioneEsattaConRetryAsync(state.Locks, state.Paths.FunzionalitaFile, sessioneFunzionalita)),
            esito => Assert.Equal(EsitoIo.Ok, esito));

        var altraPostazione = new LockService("PC-ALTRO");
        var esito = altraPostazione.TryAcquire(state.Paths.EcuFile, "Altro Operatore");
        Assert.True(esito.Acquisito);
        altraPostazione.Release(state.Paths.EcuFile);
    }

    [Fact]
    public async Task HeartbeatRiacquisito_AggiornaIlTokenUsatoDallaChiusura()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        using var vm = new AssociazioniVeicoloViewModel(state, V1);
        await vm.IniziaModificaCommand.ExecuteAsync(null);

        var lockPath = LockService.LockPathFor(state.Paths.EcuFile);
        var sessioneOriginale = Assert.IsType<string>(state.Locks.GetActiveLock(state.Paths.EcuFile)?.SessionId);
        File.Delete(lockPath); // simula un lease sparito che il rinnovo deve riacquisire con un token nuovo

        await InvocaCallbackHeartbeat(vm, state);

        var sessioneRiacquisita = Assert.IsType<string>(state.Locks.GetActiveLock(state.Paths.EcuFile)?.SessionId);
        Assert.NotEqual(sessioneOriginale, sessioneRiacquisita);
        Assert.True(await vm.ConfermaChiusuraAsync());
        Assert.False(File.Exists(lockPath));
        Assert.False(File.Exists(LockService.LockPathFor(state.Paths.FunzionalitaFile)));
    }

    [Fact]
    public async Task SalvaConLockScaduto_RiacquisisceIlTokenTracciatoPrimaDiChiudere()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        using var vm = new AssociazioniVeicoloViewModel(state, V1);
        await vm.IniziaModificaCommand.ExecuteAsync(null);

        var lockEcu = LockService.LockPathFor(state.Paths.EcuFile);
        var sessioneEcuOriginale = Assert.IsType<string>(state.Locks.GetActiveLock(state.Paths.EcuFile)?.SessionId);
        File.Delete(lockEcu); // il rinnovo immediatamente precedente al salvataggio deve ottenere un token nuovo
        var esitiPerPercorso = new Dictionary<string, LockEnsureResult>(StringComparer.OrdinalIgnoreCase);
        vm.RinnovaLockHeartbeatPerTest = async (locks, percorso, operatore) =>
        {
            var esito = await locks.EnsureOwnedTrackedAsync(percorso, operatore);
            lock (esitiPerPercorso) esitiPerPercorso[percorso] = esito;
            return esito;
        };
        vm.RigheVisibili.Single(r => r.Nome == "Componente-C").Montata = true;

        await vm.SalvaCommand.ExecuteAsync(null);

        LockEnsureResult? rinnovoEcu;
        lock (esitiPerPercorso) esitiPerPercorso.TryGetValue(state.Paths.EcuFile, out rinnovoEcu);
        Assert.NotNull(rinnovoEcu);
        Assert.True(rinnovoEcu!.Posseduto);
        Assert.NotEqual(sessioneEcuOriginale, rinnovoEcu.SessionId);
        lock (esitiPerPercorso)
        {
            Assert.Contains(state.Paths.EcuFile, esitiPerPercorso.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(state.Paths.FunzionalitaFile, esitiPerPercorso.Keys, StringComparer.OrdinalIgnoreCase);
        }
        Assert.False(vm.InModifica);
        Assert.False(File.Exists(lockEcu));
        Assert.False(File.Exists(LockService.LockPathFor(state.Paths.FunzionalitaFile)));
    }

    [Fact]
    public async Task CallbackHeartbeatInVolo_DopoDisposeNonToccaLaNuovaSessione()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        var vecchiaVm = new AssociazioniVeicoloViewModel(state, V1);
        await vecchiaVm.IniziaModificaCommand.ExecuteAsync(null);

        var lockEcu = LockService.LockPathFor(state.Paths.EcuFile);
        var lockFunzionalita = LockService.LockPathFor(state.Paths.FunzionalitaFile);
        var sessioneEcuVecchia = Assert.IsType<string>(state.Locks.GetActiveLock(state.Paths.EcuFile)?.SessionId);
        var sessioneFunzionalitaVecchia = Assert.IsType<string>(state.Locks.GetActiveLock(state.Paths.FunzionalitaFile)?.SessionId);
        var callbackEntrata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vecchiaVm.RinnovaLockHeartbeatPerTest = async (locks, percorso, operatore) =>
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(percorso, state.Paths.EcuFile))
            {
                callbackEntrata.TrySetResult();
                await continuaCallback.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            return await locks.EnsureOwnedTrackedAsync(percorso, operatore);
        };

        var callback = InvocaCallbackHeartbeat(vecchiaVm, state);
        await callbackEntrata.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vecchiaVm.Dispose();
        var rilascio = Task.WhenAll(
            LockGate.RilasciaSessioneEsattaConRetryAsync(state.Locks, state.Paths.EcuFile, sessioneEcuVecchia),
            LockGate.RilasciaSessioneEsattaConRetryAsync(state.Locks, state.Paths.FunzionalitaFile, sessioneFunzionalitaVecchia));
        Assert.False(rilascio.IsCompleted);
        Assert.True(File.Exists(lockEcu)); // la cleanup attende l'I/O del rinnovo sotto il gate del path

        continuaCallback.TrySetResult();
        await callback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(await rilascio.WaitAsync(TimeSpan.FromSeconds(5)), esito => Assert.Equal(EsitoIo.Ok, esito));
        Assert.False(File.Exists(lockEcu));
        Assert.False(File.Exists(lockFunzionalita));

        var nuovaVm = new AssociazioniVeicoloViewModel(state, V1);
        await nuovaVm.IniziaModificaCommand.ExecuteAsync(null);
        var sessioneEcuNuova = Assert.IsType<string>(state.Locks.GetActiveLock(state.Paths.EcuFile)?.SessionId);
        var sessioneFunzionalitaNuova = Assert.IsType<string>(state.Locks.GetActiveLock(state.Paths.FunzionalitaFile)?.SessionId);
        await InvocaCallbackHeartbeat(vecchiaVm, state); // una callback post-Dispose non deve agire sulla nuova modifica
        Assert.Equal(sessioneEcuNuova, state.Locks.GetActiveLock(state.Paths.EcuFile)?.SessionId);
        Assert.Equal(sessioneFunzionalitaNuova, state.Locks.GetActiveLock(state.Paths.FunzionalitaFile)?.SessionId);

        nuovaVm.Dispose();
        Assert.All(await Task.WhenAll(
            LockGate.RilasciaSessioneEsattaConRetryAsync(state.Locks, state.Paths.EcuFile, sessioneEcuNuova),
            LockGate.RilasciaSessioneEsattaConRetryAsync(state.Locks, state.Paths.FunzionalitaFile, sessioneFunzionalitaNuova)),
            esito => Assert.Equal(EsitoIo.Ok, esito));
    }

    [Fact]
    public async Task CallbackHeartbeatRiacquisitaDopoDispose_RilasciaLaNuovaLeaseConRetrySottoGate()
    {
        using var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        var vm = new AssociazioniVeicoloViewModel(state, V1);
        await vm.IniziaModificaCommand.ExecuteAsync(null);

        var percorsoEcu = state.Paths.EcuFile;
        var lockEcu = LockService.LockPathFor(percorsoEcu);
        var sessioneEcuOriginale = Assert.IsType<string>(state.Locks.GetActiveLock(percorsoEcu)?.SessionId);
        var sessioneFunzOriginale = Assert.IsType<string>(state.Locks.GetActiveLock(state.Paths.FunzionalitaFile)?.SessionId);
        var callbackEntrata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? sessioneRiacquisita = null;
        var tentativiRilascioNuovo = 0;
        vm.RinnovaLockHeartbeatPerTest = async (locks, percorso, operatore) =>
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(percorso, percorsoEcu))
                return await locks.EnsureOwnedTrackedAsync(percorso, operatore);

            callbackEntrata.TrySetResult();
            await continuaCallback.Task.WaitAsync(TimeSpan.FromSeconds(5));
            File.Delete(lockEcu); // la vecchia lease sparisce mentre Dispose è accodato dietro il gate
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

        var callback = InvocaCallbackHeartbeat(vm, state);
        try
        {
            await callbackEntrata.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.Dispose();
            continuaCallback.TrySetResult();
            await callback.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(EsitoIo.Ok, await LockGate.RilasciaSessioneEsattaConRetryAsync(
                state.Locks, percorsoEcu, sessioneEcuOriginale).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(EsitoIo.Ok, await LockGate.RilasciaSessioneEsattaConRetryAsync(
                state.Locks, state.Paths.FunzionalitaFile, sessioneFunzOriginale).WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.NotNull(sessioneRiacquisita);
            Assert.NotEqual(sessioneEcuOriginale, sessioneRiacquisita);
            Assert.Equal(2, Volatile.Read(ref tentativiRilascioNuovo));
            Assert.Null(state.Locks.GetActiveLock(percorsoEcu));
            Assert.Null(state.Locks.GetActiveLock(state.Paths.FunzionalitaFile));
        }
        finally
        {
            continuaCallback.TrySetResult();
            vm.Dispose();
            try { await callback.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            await Task.WhenAll(
                LockGate.RilasciaSessioneEsattaConRetryAsync(state.Locks, percorsoEcu, sessioneEcuOriginale),
                LockGate.RilasciaSessioneEsattaConRetryAsync(state.Locks, state.Paths.FunzionalitaFile, sessioneFunzOriginale));
        }
    }

    private static Task InvocaCallbackHeartbeat(AssociazioniVeicoloViewModel vm, AppState state)
    {
        var method = typeof(AssociazioniVeicoloViewModel).GetMethod("RinnovaLock", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method!.Invoke(vm, new object[] { state.Config.OperatoreAttivo }));
    }

    [Fact]
    public void Annulla_SenzaModifiche_TornaInLetturaSenzaChiedere()
    {
        var state = CreaStato();
        SeminaEcuEFunzionalita(state);
        using var vm = new AssociazioniVeicoloViewModel(state, V1);
        vm.IniziaModificaCommand.AttendiModifica();

        vm.AnnullaCommand.AttendiModifica();

        Assert.False(vm.InModifica);
        Assert.Equal(new[] { "Componente-A", "Componente-B" }, vm.RigheVisibili.Where(r => r.Montata).Select(r => r.Nome));
    }

    // P2.3: la finestra può chiudersi mentre l'acquisizione dei lock è in corso. I lock già presi non devono restare in
    // mano a una finestra chiusa, e la modifica non deve partire.

    [Fact]
    public async Task ChiusuraDuranteAcquisizione_RilasciaIlLockGiaPreso()
    {
        var state = CreaStato();
        var lockEcu = LockService.LockPathFor(state.Paths.EcuFile);
        var lockFunzionalita = LockService.LockPathFor(state.Paths.FunzionalitaFile);
        // Il gate del secondo file resta chiuso: l'acquisizione si ferma dopo il primo lock, come con uno share lento.
        var sbloccoFunzionalita = new TaskCompletionSource();
        var tenutoFunzionalita = LockGate.EseguiAsync(state.Paths.FunzionalitaFile, () => sbloccoFunzionalita.Task);

        var vm = new AssociazioniVeicoloViewModel(state, V1);
        var inizio = vm.IniziaModificaCommand.ExecuteAsync(null);
        await AttendiFile(lockEcu);

        vm.Dispose();                     // la finestra si chiude mentre l'acquisizione è in corso
        sbloccoFunzionalita.SetResult();  // il secondo lock può ora essere preso, ma la finestra non c'è più
        await inizio;
        await tenutoFunzionalita;

        Assert.False(vm.InModifica);
        Assert.False(File.Exists(lockEcu), "Il lock già preso prima della chiusura va rilasciato.");
        Assert.False(File.Exists(lockFunzionalita), "Nessun lock deve restare in mano a una finestra chiusa.");
    }

    [Fact]
    public async Task ChiusuraPrimaDelPrimoLock_NonAcquisisceNulla()
    {
        var state = CreaStato();
        var lockEcu = LockService.LockPathFor(state.Paths.EcuFile);
        var lockFunzionalita = LockService.LockPathFor(state.Paths.FunzionalitaFile);
        var sbloccoEcu = new TaskCompletionSource();
        var tenutoEcu = LockGate.EseguiAsync(state.Paths.EcuFile, () => sbloccoEcu.Task);

        var vm = new AssociazioniVeicoloViewModel(state, V1);
        var inizio = vm.IniziaModificaCommand.ExecuteAsync(null);
        await Task.Delay(200); // l'acquisizione è ferma sul gate del primo file

        vm.Dispose();
        sbloccoEcu.SetResult();
        await inizio;
        await tenutoEcu;

        Assert.False(vm.InModifica);
        Assert.False(File.Exists(lockEcu));
        Assert.False(File.Exists(lockFunzionalita));
    }

    private static async Task AttendiFile(string percorso)
    {
        var scadenza = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(percorso))
        {
            if (DateTime.UtcNow > scadenza) throw new TimeoutException($"Il lock {percorso} non è stato acquisito.");
            await Task.Delay(10);
        }
    }

    private static async Task AttendiFileApribileInEsclusiva(string percorso)
    {
        var scadenza = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow <= scadenza)
        {
            if (File.Exists(percorso))
            {
                try
                {
                    using var stream = File.Open(percorso, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    return;
                }
                catch (IOException)
                {
                    // L'acquisizione sta ancora completando la scrittura del file lock.
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Il lock {percorso} non è diventato disponibile in esclusiva.");
    }

    [Fact]
    public async Task AcquisizioneParziale_QuandoRilascioFallisce_ConservaPercorsiESegnaRetry()
    {
        var state = CreaStato();
        var lockEcu = LockService.LockPathFor(state.Paths.EcuFile);
        var lockFunzionalita = LockService.LockPathFor(state.Paths.FunzionalitaFile);

        // Blocchiamo il gate di Funzionalità: componente verrà acquisita, Funzionalità rimarrà in attesa.
        var sbloccoFunzionalita = new TaskCompletionSource();
        var tenutoFunzionalita = LockGate.EseguiAsync(state.Paths.FunzionalitaFile, () => sbloccoFunzionalita.Task);

        var vm = new AssociazioniVeicoloViewModel(state, V1);
        var inizio = vm.IniziaModificaCommand.ExecuteAsync(null);
        await AttendiFile(lockEcu);

        var contenutoValidoEcu = File.ReadAllText(lockEcu);

        // Creiamo un lock altrui su Funzionalità per far fallire la seconda acquisizione
        File.WriteAllText(lockFunzionalita, JsonSerializer.Serialize(new LockInfo
        {
            Operatore = "Altro",
            AcquisitoIl = DateTime.UtcNow,
            UltimoHeartbeat = DateTime.UtcNow,
            DeviceId = "PC-Altro",
            SessionId = "altra-sessione"
        }, JsonIo.Options));

        // Corrompiamo il file di lock componente in modo che il rilascio parziale fallisca
        File.WriteAllText(lockEcu, "{corrotto");

        sbloccoFunzionalita.SetResult();
        await inizio;
        await tenutoFunzionalita;

        // Modifica non deve essere partita
        Assert.False(vm.InModifica);
        // Il rilascio è fallito: la finestra viva DEVE mantenere RilascioLockInAttesa e il percorso fallito
        Assert.True(vm.RilascioLockInAttesa, "La finestra deve segnalare che il rilascio del lock è in attesa.");
        Assert.True(vm.HaMessaggioErrore);

        // Ora ripristiniamo il lock componente valido e invochiamo RiprovaRilascioLock
        File.WriteAllText(lockEcu, contenutoValidoEcu);
        await vm.RiprovaRilascioLockCommand.ExecuteAsync(null);

        Assert.False(vm.RilascioLockInAttesa);
        Assert.False(File.Exists(lockEcu), "Il lock componente deve essere stato rilasciato con successo dopo il retry.");
    }

    [Fact]
    public async Task Dispose_DuranteAcquisizioneParziale_QuandoRilascioFallisce_TracciaErrore()
    {
        var traceLog = Path.Combine(Path.GetTempPath(), "trace_" + Guid.NewGuid().ToString("N") + ".log");
        DiagnosticTraceService.PercorsoPerTest = traceLog;
        try
        {
            var state = CreaStato();
            var lockEcu = LockService.LockPathFor(state.Paths.EcuFile);
            var lockFunzionalita = LockService.LockPathFor(state.Paths.FunzionalitaFile);

            var sbloccoFunzionalita = new TaskCompletionSource();
            var tenutoFunzionalita = LockGate.EseguiAsync(state.Paths.FunzionalitaFile, () => sbloccoFunzionalita.Task);

            var vm = new AssociazioniVeicoloViewModel(state, V1);
            var inizio = vm.IniziaModificaCommand.ExecuteAsync(null);
            await AttendiFileApribileInEsclusiva(lockEcu);

            // Corrompiamo il file lock componente in modo che il rilascio in Dispose fallisca
            File.WriteAllText(lockEcu, "{corrotto");

            // Dispose chiude la finestra mentre l'acquisizione è in corso
            vm.Dispose();

            sbloccoFunzionalita.SetResult();
            await inizio;
            await tenutoFunzionalita;

            Assert.False(vm.InModifica);
            Assert.True(File.Exists(traceLog), "Il file di traccia deve esistere.");
            var contenutoTraccia = File.ReadAllText(traceLog);
            Assert.True(contenutoTraccia.Contains("Associazioni.Dispose") || contenutoTraccia.Contains("Associazioni.AcquisizioneParziale"),
                "Il rilascio fallito alla chiusura forzata deve essere tracciato.");
        }
        finally
        {
            DiagnosticTraceService.PercorsoPerTest = null;
            try { if (File.Exists(traceLog)) File.Delete(traceLog); } catch { }
        }
    }
}
