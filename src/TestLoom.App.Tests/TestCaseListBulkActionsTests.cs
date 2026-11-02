using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Azioni di massa sulla lista Test (Addendum: audit UX 2026-08-26, punto 2). Verificano le tre
/// garanzie che l'utente ha chiesto esplicitamente durante la revisione: mai sovrascrivere un test in uso da
/// un collega (lock), mai perdere un salvataggio concorrente in silenzio (conflitto di revisione), e le stesse
/// regole di business del salvataggio singolo (nota obbligatoria per l'assegnatario, segnalazione da
/// classificare che blocca il salvataggio).</summary>
/// <remarks>[Collection("Localization")]: questi test mutano SelezionaDialogService.Handler/TextEditService.Handler,
/// proprietà statiche condivise da tutto il processo di test. Senza serializzarli nella stessa collection di
/// ogni altro test che tocca gli stessi Handler statici (stesso principio già applicato a LocalizationService),
/// due classi in esecuzione parallela possono sovrascriversi a vicenda l'Handler a metà di un test, facendo
/// fallire in modo intermittente e non riproducibile in isolamento.</remarks>
[Collection("Localization")]
public sealed class TestCaseListBulkActionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_BulkActions_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, IReadOnlyList<VoceSelezionabile>, bool, Task<IReadOnlyList<VoceSelezionabile>>>? _selezionaOriginale = SelezionaDialogService.Handler;
    private readonly Func<string, string, Task<string?>>? _textEditOriginale = TextEditService.Handler;
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;

    public void Dispose()
    {
        SelezionaDialogService.Handler = _selezionaOriginale;
        TextEditService.Handler = _textEditOriginale;
        ConfirmService.Handler = _confirmOriginale;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStatoComeMaster()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        var operatoreB = TestOperatori.Sigilla(new Operatore { Nome = "Operatore B", Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master, operatoreB });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        return state;
    }

    private static TestCase NuovoTest(string id) => new()
    {
        Id = id,
        Componente = new EcuSelection { "Componente-A" },
        DescrizioneBreve = "bulk",
        StatoLavoro = StatoLavoroTest.Pianificato,
    };

    private TestCaseListViewModel CreaListaConTest(AppState state, int quanti)
    {
        for (var i = 1; i <= quanti; i++) state.TestCases.Save(NuovoTest($"TC-{i:D4}"), isNew: true);
        state.RicaricaTutto();
        var vm = new TestCaseListViewModel(state, _ => { });
        foreach (var tc in state.TestCases.GetAll()) vm.Selezionati.Add(tc);
        return vm;
    }

    [Fact]
    public async Task CambiaStatoSelezionati_AggiornaTuttiIQuattroTest()
    {
        var state = CreaStatoComeMaster();
        var vm = CreaListaConTest(state, 4);

        await vm.CambiaStatoSelezionatiCommand.ExecuteAsync(StatoLavoroTest.InCorso);

        Assert.All(state.TestCases.GetAll(), tc => Assert.Equal(StatoLavoroTest.InCorso, tc.StatoLavoro));
        Assert.Empty(vm.Selezionati); // la selezione si svuota a fine azione, evitando un secondo click accidentale
    }

    [Fact]
    public async Task CambiaStatoSelezionati_RilascioFallitoMostraWarningEConsenteRetryDellaSessioneEsatta()
    {
        using var state = CreaStatoComeMaster();
        var vm = CreaListaConTest(state, 1);
        var test = Assert.Single(state.TestCases.GetAll());
        var percorso = state.TestCases.PercorsoPer(test);
        var fallisciRilascio = true;
        vm.RilascioLockPerTest = (locks, path, sessione) => fallisciRilascio
            ? EsitoIo.Errore
            : locks.ReleaseIfSessionTracked(path, sessione);
        vm.AttesaRetryLockPerTest = _ => Task.CompletedTask;
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);

        await vm.CambiaStatoSelezionatiCommand.ExecuteAsync(StatoLavoroTest.InCorso);

        Assert.Equal(StatoLavoroTest.InCorso, state.TestCases.GetAll().Single().StatoLavoro);
        Assert.True(vm.MostraRilascioLockMassaInAttesa);
        Assert.Contains(test.Id, vm.MessaggioRilascioLockMassa);
        var pending = Assert.Single(vm.RilasciLockMassaInAttesa);
        Assert.Equal(percorso, pending.Percorso);
        Assert.Equal(test.Id, pending.TestId);
        Assert.False(string.IsNullOrWhiteSpace(pending.SessionId));
        Assert.True(File.Exists(LockService.LockPathFor(percorso)));

        fallisciRilascio = false;
        await vm.RiprovaRilasciLockMassaCommand.ExecuteAsync(null);

        Assert.Empty(vm.RilasciLockMassaInAttesa);
        Assert.False(vm.MostraRilascioLockMassaInAttesa);
        Assert.False(File.Exists(LockService.LockPathFor(percorso)));
    }

    /// <summary>Segnalazione utente (post-collaudo): dopo un cambio di stato in massa, la tabella mostrava il
    /// valore vecchio finché non si usciva dalla pagina Test e ci si tornava. Causa: il motore chiamava
    /// _state.RicaricaTutto() (rilettura completa e sincrona di tutta la cartella condivisa) subito dopo aver
    /// già salvato correttamente ogni test — oltre a essere lento, su una cartella sincronizzata cloud poteva
    /// rileggere per un istante una versione del file non ancora sincronizzata, sovrascrivendo in memoria il
    /// valore appena salvato. Questo test verifica che Righe (la collezione bindata alla DataGrid) rifletta
    /// il nuovo stato SUBITO dopo il comando, senza bisogno di lasciare e rivisitare la pagina — cosa che il
    /// solo controllo su state.TestCases.GetAll() sopra non garantiva, perché passava comunque da Aggiorna().</summary>
    [Fact]
    public async Task CambiaStatoSelezionati_RigheRiflettonoSubitoIlNuovoStato_SenzaLasciareLaPagina()
    {
        var state = CreaStatoComeMaster();
        var vm = CreaListaConTest(state, 3);

        await vm.CambiaStatoSelezionatiCommand.ExecuteAsync(StatoLavoroTest.DaRivedere);

        // Nessuna chiamata a Aggiorna()/OttieniPagina di MainViewModel qui in mezzo: solo il comando appena
        // eseguito. Righe deve già mostrare il nuovo stato.
        Assert.NotEmpty(vm.Righe);
        Assert.All(vm.Righe, tc => Assert.Equal(StatoLavoroTest.DaRivedere, tc.StatoLavoro));
    }

    /// <summary>Segnalazione utente (post-collaudo): dopo un'azione di massa la DataGrid restava
    /// visivamente con le righe evidenziate come selezionate, anche se il ViewModel aveva già svuotato
    /// Selezionati — un'azione successiva risultava quindi "nessuna riga selezionata" pur sembrando il
    /// contrario. Causa: nulla nel ViewModel diceva mai alla View di azzerare la selezione REALE della
    /// DataGrid (Avalonia non espone SelectedItems bindabile a due vie). Il ponte
    /// RichiestaAzzeramentoSelezioneGriglia deve scattare ogni volta che Selezionati si svuota per
    /// un'azione, non solo quando l'utente clicca esplicitamente "Deseleziona".</summary>
    [Fact]
    public async Task DopoUnAzioneDiMassa_RichiedeAzzeramentoDellaSelezioneVisivaDellaGriglia()
    {
        var state = CreaStatoComeMaster();
        var vm = CreaListaConTest(state, 2);
        var azzeramentiRichiesti = 0;
        vm.RichiestaAzzeramentoSelezioneGriglia += () => azzeramentiRichiesti++;

        await vm.CambiaStatoSelezionatiCommand.ExecuteAsync(StatoLavoroTest.InCorso);

        Assert.Equal(1, azzeramentiRichiesti);
        Assert.Empty(vm.Selezionati);
    }

    [Fact]
    public void DeselezionaTutti_RichiedeAzzeramentoDellaSelezioneVisivaDellaGriglia()
    {
        var state = CreaStatoComeMaster();
        var vm = CreaListaConTest(state, 2);
        var azzeramentiRichiesti = 0;
        vm.RichiestaAzzeramentoSelezioneGriglia += () => azzeramentiRichiesti++;

        vm.DeselezionaTuttiCommand.Execute(null);

        Assert.Equal(1, azzeramentiRichiesti);
        Assert.Empty(vm.Selezionati);
    }

    [Fact]
    public async Task CambiaStatoSelezionati_SaltaUnTestBloccatoDaUnCollega_SenzaToccarlo()
    {
        var state = CreaStatoComeMaster();
        var vm = CreaListaConTest(state, 3);
        var bloccato = state.TestCases.GetAll().First();
        var percorso = state.TestCases.PercorsoPer(bloccato);

        // Simula un collega che ha già in modifica questo test (stesso lock applicativo file-based usato
        // dall'editor interattivo).
        var lockResult = state.Locks.TryAcquire(percorso, "Operatore B");
        Assert.True(lockResult.Acquisito);

        await vm.CambiaStatoSelezionatiCommand.ExecuteAsync(StatoLavoroTest.DaRivedere);

        var ricaricato = state.TestCases.GetAll().First(tc => tc.Id == bloccato.Id);
        Assert.Equal(StatoLavoroTest.Pianificato, ricaricato.StatoLavoro); // non toccato: mai scavalcare un lock altrui
        Assert.Equal(2, state.TestCases.GetAll().Count(tc => tc.StatoLavoro == StatoLavoroTest.DaRivedere)); // gli altri due sì
    }

    [Fact]
    public async Task CambiaStatoSelezionati_NonCondivideIlLockDiUnEditorApertoSullaStessaPostazione()
    {
        var state = CreaStatoComeMaster();
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        var vm = CreaListaConTest(state, 1);
        var test = state.TestCases.GetAll().Single();
        var percorso = state.TestCases.PercorsoPer(test);
        var lockEditor = state.Locks.TryAcquire(percorso, state.Config.OperatoreAttivo);
        Assert.True(lockEditor.Acquisito);

        await vm.CambiaStatoSelezionatiCommand.ExecuteAsync(StatoLavoroTest.DaRivedere);

        var aggiornato = state.TestCases.GetAll().Single(tc => tc.Id == test.Id);
        Assert.Equal(StatoLavoroTest.Pianificato, aggiornato.StatoLavoro);
        Assert.Equal(lockEditor.DetenutoDa!.SessionId, state.Locks.GetActiveLock(percorso)!.SessionId);
    }

    [Fact]
    public async Task CambiaStatoSelezionati_SaltaTestConSegnalazionePendente()
    {
        var state = CreaStatoComeMaster();
        var vm = CreaListaConTest(state, 2);
        var conSegnalazione = state.TestCases.GetAll().First();
        state.Segnalazioni.Save(new SegnalazioneDaClassificare { TestId = conSegnalazione.Id, TestUid = conSegnalazione.Uid, Stato = StatoSegnalazione.DaClassificare });

        await vm.CambiaStatoSelezionatiCommand.ExecuteAsync(StatoLavoroTest.InCorso);

        var ricaricato = state.TestCases.GetAll().First(tc => tc.Id == conSegnalazione.Id);
        Assert.Equal(StatoLavoroTest.Pianificato, ricaricato.StatoLavoro); // stessa regola del salvataggio singolo (Valida())
        Assert.Equal(StatoLavoroTest.InCorso, state.TestCases.GetAll().First(tc => tc.Id != conSegnalazione.Id).StatoLavoro);
    }

    [Fact]
    public async Task AssegnaSelezionati_ScriveAssegnatarioEUnaNotaCondivisaSuTuttiIRighi()
    {
        var state = CreaStatoComeMaster();
        var vm = CreaListaConTest(state, 3);
        SelezionaDialogService.Handler = (_, voci, _) => Task.FromResult<IReadOnlyList<VoceSelezionabile>>(new[] { voci.First(v => (string)v.Valore == "Operatore B") });
        TextEditService.Handler = (_, _) => Task.FromResult<string?>("Riassegnazione di massa per test.");

        await vm.AssegnaSelezionatiCommand.ExecuteAsync(null);

        Assert.All(state.TestCases.GetAll(), tc =>
        {
            Assert.Equal("Operatore B", tc.Assegnatario);
            Assert.Contains(tc.NotePianificazione, n => n.CampiModificati == "assegnatario" && n.Nota == "Riassegnazione di massa per test.");
        });
    }

    [Fact]
    public async Task AssegnaSelezionati_SenzaNota_NonSalvaNulla()
    {
        var state = CreaStatoComeMaster();
        var vm = CreaListaConTest(state, 2);
        SelezionaDialogService.Handler = (_, voci, _) => Task.FromResult<IReadOnlyList<VoceSelezionabile>>(new[] { voci.First(v => (string)v.Valore == "Operatore B") });
        TextEditService.Handler = (_, _) => Task.FromResult<string?>(""); // annullato/vuoto: nota obbligatoria mancante

        await vm.AssegnaSelezionatiCommand.ExecuteAsync(null);

        Assert.All(state.TestCases.GetAll(), tc => Assert.Null(tc.Assegnatario));
    }

    [Fact]
    public void AssegnaSelezionati_NonDisponibileAUnOperatoreStandard()
    {
        var state = CreaStatoComeMaster();
        state.Master.Operatori.Save(state.Master.Operatori.Load()); // no-op, mantiene entrambi gli operatori
        Assert.True(state.ImpostaOperatore("Operatore B", "1234"));
        var vm = CreaListaConTest(state, 2);

        Assert.False(vm.PuoAssegnareInMassa);
    }

    [Fact]
    public async Task ArchiviaSelezionati_SpostaTuttiInCestino()
    {
        var state = CreaStatoComeMaster();
        var vm = CreaListaConTest(state, 3);

        await vm.ArchiviaSelezionatiCommand.ExecuteAsync(null);

        Assert.Empty(state.TestCases.GetAll());
        Assert.Equal(3, state.TestCases.GetAll(includeEliminati: true).Count(tc => tc.Eliminato));
    }

    /// <summary>P1.3: un Test in conflitto con un'altra postazione non deve lasciare nella cache né nella lista
    /// l'assegnazione di massa che non è stata salvata; gli altri Test della selezione vengono assegnati comunque.</summary>
    [Fact]
    public async Task AssegnaSelezionati_TestInConflitto_NonSporcaNeLaCacheNeLaLista()
    {
        var state = CreaStatoComeMaster();
        var vm = CreaListaConTest(state, 3);
        var inConflitto = state.TestCases.GetById("TC-0002")!;

        // Un'altra postazione salva lo stesso Test dopo l'apertura della lista: la cache di questa postazione è stantia.
        var localeAltra = Path.Combine(Path.GetTempPath(), "TestLoom_BulkActions_Altra_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var altra = TestPostazioniCondivise.Apri(_dir, localeAltra);
            var copiaAltra = altra.TestCases.LoadCurrent(inConflitto)!;
            copiaAltra.StatoLavoro = StatoLavoroTest.DaRivedere;
            altra.TestCases.Save(copiaAltra, isNew: false, copiaAltra.RevisioneDati);
        }
        finally
        {
            try { if (Directory.Exists(localeAltra)) Directory.Delete(localeAltra, recursive: true); } catch { }
        }

        SelezionaDialogService.Handler = (_, voci, _) => Task.FromResult<IReadOnlyList<VoceSelezionabile>>(new[] { voci.First(v => (string)v.Valore == "Operatore B") });
        TextEditService.Handler = (_, _) => Task.FromResult<string?>("Riassegnazione di massa con conflitto.");

        await vm.AssegnaSelezionatiCommand.ExecuteAsync(null);

        Assert.Equal(2, state.TestCases.GetAll().Count(tc => tc.Assegnatario == "Operatore B"));
        var nellaCache = state.TestCases.GetById(inConflitto.Id)!;
        Assert.Null(nellaCache.Assegnatario);
        Assert.DoesNotContain(nellaCache.NotePianificazione, n => n.CampiModificati == "assegnatario");
        Assert.Null(vm.Righe.First(tc => tc.Id == inConflitto.Id).Assegnatario);
    }

    /// <summary>P4: l'azione di massa non esegue l'I/O sul thread chiamante. Con il primo Test bloccato dal lock di commit
    /// (un Save concorrente in corso), il comando torna subito, la lista resta in IsBusy e un secondo avvio è impedito
    /// finché il lavoro non termina.</summary>
    [Fact]
    public async Task AzioneDiMassa_NonBloccaIlChiamante_EIsBusyImpedisceUnSecondoAvvio()
    {
        var state = CreaStatoComeMaster();
        var vm = CreaListaConTest(state, 2);
        var primo = state.TestCases.GetById("TC-0001")!;
        using var lockTenuto = TestCaseCommitLock.AcquireAll(primo.Id, new[] { state.TestCases.PercorsoPer(primo) });

        SelezionaDialogService.Handler = (_, voci, _) => Task.FromResult<IReadOnlyList<VoceSelezionabile>>(new[] { voci.First(v => (string)v.Valore == "Operatore B") });
        TextEditService.Handler = (_, _) => Task.FromResult<string?>("Riassegnazione di massa in background.");

        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var esecuzione = vm.AssegnaSelezionatiCommand.ExecuteAsync(null);
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(4), "Il comando ha atteso il lavoro di massa invece di tornare subito.");
        Assert.True(vm.IsBusy);
        Assert.False(vm.AssegnaSelezionatiCommand.CanExecute(null));

        lockTenuto.Dispose();
        await esecuzione;
        Assert.False(vm.IsBusy);
        Assert.Equal(2, state.TestCases.GetAll().Count(tc => tc.Assegnatario == "Operatore B"));
    }
}
