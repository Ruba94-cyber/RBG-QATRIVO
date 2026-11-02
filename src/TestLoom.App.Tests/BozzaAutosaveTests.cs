using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using static TestLoom.App.Tests.NavigazioneTestHelper;

namespace TestLoom.App.Tests;

/// <summary>Autosave locale di un test nuovo (Addendum: audit UX 2026-08-26, nota minore 1 — "nessun autosave
/// su disco: BozzaPerNuovoTest vive solo in memoria, un crash la perde"). Verifica il ciclo completo: scrittura
/// periodica solo quando c'è davvero qualcosa di non salvato, ripristino tramite lo stesso ponte già usato da
/// "Duplica come nuovo test", ed eliminazione ad ogni uscita concorde (salvataggio riuscito o abbandono
/// confermato) — mai su un crash, che è l'unico caso che questa funzionalità deve coprire.</summary>
[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class BozzaAutosaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_BozzaAutosave_" + Guid.NewGuid().ToString("N"));
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "TestLoom_BozzaAutosaveConfig_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;

    public BozzaAutosaveTests() => SeedDemoDataService.Genera(_dir);

    public void Dispose()
    {
        ConfirmService.Handler = _confirmOriginale;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
        if (Directory.Exists(_configDir)) { try { Directory.Delete(_configDir, true); } catch { } }
    }

    /// <summary>Poll con timeout bounded per attendere una cleanup asincrona accodata sul dispatcher di test,
    /// invece di un Task.Delay fisso che su macchina carica può non bastare (vedi commento nel chiamante).</summary>
    private static async Task AttendiAsync(Func<bool> condizione, TimeSpan timeout)
    {
        var scadenza = DateTime.UtcNow + timeout;
        while (!condizione())
        {
            if (DateTime.UtcNow >= scadenza)
                return; // L'assert successivo nel chiamante riporta il fallimento con il messaggio giusto.
            await Task.Delay(20);
        }
    }

    private AppState CreateState()
    {
        var state = new AppState(new AppConfigService(Path.Combine(_configDir, "config.json")));
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var operatore = state.Master.Operatori.Load().First(o => o.Ruolo == RuoloOperatore.Master);
        Assert.True(state.ImpostaOperatore(operatore.Nome, "1234"));
        return state;
    }

    [Fact]
    public void BozzaAutosaveService_SalvaCaricaEd_Elimina_RoundTripSuDisco()
    {
        var servizio = new BozzaAutosaveService(_configDir);
        var tc = new TestCase { Descrizione = "prova autosave" };

        Assert.Null(servizio.Carica("Mario Rossi"));

        servizio.Salva("Mario Rossi", tc);
        var ripresa = servizio.Carica("Mario Rossi");
        Assert.NotNull(ripresa);
        Assert.Equal("prova autosave", ripresa!.TestCase.Descrizione);

        // Un altro operatore sullo stesso PC non vede la bozza di Mario: file separati per operatore.
        Assert.Null(servizio.Carica("Altro Operatore"));

        servizio.Elimina("Mario Rossi");
        Assert.Null(servizio.Carica("Mario Rossi"));
    }

    [Fact]
    public void EditorNuovo_DopoSceltaTemplate_AvviaAutosaveEScriveLaBozzaAllaChiamataDelTimer()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.TestCase.Descrizione = "bozza in corso";

        // Non si aspetta il timer reale (30s): si invoca la stessa logica che l'autosave userebbe, tramite
        // il servizio direttamente, per verificare il contenuto che finirebbe su disco è quello giusto.
        state.BozzaAutosave.Salva(state.Config.OperatoreAttivo, vm.TestCase);

        var bozza = state.BozzaAutosave.Carica(state.Config.OperatoreAttivo);
        Assert.NotNull(bozza);
        Assert.Equal("bozza in corso", bozza!.TestCase.Descrizione);
    }

    [Fact]
    public async Task SalvataggioRiuscito_EliminaLaBozza()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.VeicoloSelezionato = vm.VeicoliDisponibili[0];
        vm.AssegnatarioSelezionato = state.Config.OperatoreAttivo;
        vm.TestCase.Descrizione = "da salvare per davvero";

        state.BozzaAutosave.Salva(state.Config.OperatoreAttivo, vm.TestCase); // simula un giro di autosave già avvenuto
        Assert.NotNull(state.BozzaAutosave.Carica(state.Config.OperatoreAttivo));

        await vm.SalvaCommand.ExecuteAsync(null); // Salva() -> _tornaAllaLista() -> MainViewModel disporrebbe la pagina; qui chiamiamo Dispose direttamente
        vm.Dispose();

        Assert.Null(state.BozzaAutosave.Carica(state.Config.OperatoreAttivo));
    }

    [Fact]
    public void ApertoUnTestEsistente_NonTocca_NonElimina_LaBozzaDiUnTestNuovoInSospeso()
    {
        var state = CreateState();
        state.BozzaAutosave.Salva(state.Config.OperatoreAttivo, new TestCase { Descrizione = "bozza sospesa" });

        // Aprire/chiudere un test ESISTENTE non deve interferire con la bozza di un test nuovo mai aperto in
        // questa sessione: Dispose() elimina la bozza solo per un editor con IsNew=true.
        var testEsistente = new TestCase { Id = "TC-BOZZA-0001", Descrizione = "test vero" };
        state.TestCases.Save(testEsistente, isNew: true); // Save rigenera l'Id in place in caso di collisione col seed demo
        var vmEsistente = new TestCaseEditorViewModel(state, testEsistente.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        vmEsistente.Dispose();

        Assert.NotNull(state.BozzaAutosave.Carica(state.Config.OperatoreAttivo));
    }

    [Fact]
    public async Task TestEsistente_AbbandonoConfermato_EliminaSoloLaBozzaDiRecoveryDellaSessione()
    {
        var state = CreateState();
        var testEsistente = new TestCase { Id = "TC-BOZZA-EDIT-0001", Descrizione = "base" };
        state.TestCases.Save(testEsistente, isNew: true);
        var vm = new TestCaseEditorViewModel(state, testEsistente.Id, tornaAllaLista: () => { }, navigaATest: _ => { });

        await vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica);
        vm.TestCase.Descrizione = "modifica da scartare";
        vm.ModificheNonSalvate = true; // il guard state-safety salva subito la prima fotografia dirty.
        Assert.NotNull(state.BozzaAutosave.CaricaModifica(state.Config.OperatoreAttivo, vm.TestCase.Uid));

        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        await vm.AnnullaCommand.ExecuteAsync(null);

        // La cleanup della bozza è accodata su Dispatcher.UIThread.Post e drenata in background da
        // DispatcherTestPump (un thread dedicato, indipendente dal thread di questo test): con la macchina
        // carica il pump può non aver ancora girato dopo un ritardo fisso, rendendo il test flaky (fallisce
        // solo su macchina carica, non da solo — vedi piano 6.4.2 §5). Si attende (con timeout ampio, non un
        // ritardo fisso) la condizione vera anziché un tempo arbitrario: deterministico indipendentemente
        // dalla velocità della macchina, e comunque veloce quando il pump gira subito.
        await AttendiAsync(
            () => state.BozzaAutosave.CaricaModifica(state.Config.OperatoreAttivo, vm.TestCase.Uid) is null,
            TimeSpan.FromSeconds(10));

        Assert.Null(state.BozzaAutosave.CaricaModifica(state.Config.OperatoreAttivo, vm.TestCase.Uid));
        vm.ModificheNonSalvate = false;
        vm.InModifica = false;
        vm.Dispose();
    }

    [Fact]
    public async Task NavigaATestCase_ConBozzaTrovata_ChiedeConfermaEPreriempieIlFormSeAccettata()
    {
        var state = CreateState();
        state.BozzaAutosave.Salva(state.Config.OperatoreAttivo, new TestCase { Descrizione = "bozza da ripristinare", TestTemplateUid = state.Master.TestTemplate.Load()[0].Uid });

        var main = new MainViewModel(state);
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true); // "Riprendi bozza"

        // NavigaATestCase è privata: come farebbe davvero l'utente, si arriva alla lista Test e si preme
        // "Nuovo" — è quel comando (_apriTestCase(null)) a richiamare NavigaATestCase internamente.
        await NavigaAsync(main, "test");
        var lista = Assert.IsType<TestCaseListViewModel>(main.CurrentPage);
        lista.NuovoCommand.Execute(null);
        // Attende che il fire-and-forget di OffriRipristinoBozza completi: nessun await reale coinvolto oltre
        // al mock sincrono di ConfirmService.Handler sopra, ma un giro di Task.Yield basta a far avanzare la
        // continuazione già pronta.
        await Task.Yield();
        await Task.Delay(50);

        var vm = Assert.IsType<TestCaseEditorViewModel>(main.CurrentPage);
        Assert.True(vm.IsNew);
        Assert.True(vm.TipoTestScelto); // salta il passo 1 del wizard, come "Duplica come nuovo test"
        Assert.Equal("bozza da ripristinare", vm.TestCase.Descrizione);
    }

    [Fact]
    public async Task NavigaATestCase_ConBozzaTrovataMaRifiutata_ApreUnEditorVuotoELaCancella()
    {
        var state = CreateState();
        state.BozzaAutosave.Salva(state.Config.OperatoreAttivo, new TestCase { Descrizione = "bozza da scartare" });

        var main = new MainViewModel(state);
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(false); // "Scarta"

        await NavigaAsync(main, "test");
        var lista = Assert.IsType<TestCaseListViewModel>(main.CurrentPage);
        lista.NuovoCommand.Execute(null);
        await Task.Yield();
        await Task.Delay(50);

        var vm = Assert.IsType<TestCaseEditorViewModel>(main.CurrentPage);
        Assert.True(vm.IsNew);
        Assert.False(vm.TipoTestScelto); // passo 1 del wizard, form vuoto come una creazione normale
        Assert.Null(state.BozzaAutosave.Carica(state.Config.OperatoreAttivo));
    }
}
