using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Copertura per le correzioni C4/C5 di diagnosi_sync/diagnosi.md (feedback utente: "una modifica fatta da un
/// altro operatore compare con moltissimo ritardo" su cartella condivisa multi-PC). Prima di questa correzione:
/// - C4: TestCaseChangeTracker (FileSystemWatcher) accumulava soltanto path "sporchi", consumati al giro
///   successivo del timer periodico da 60s (MainViewModel.cs) — il watcher non innescava mai nulla da solo.
/// - C5: un QUALSIASI editor con modifiche non salvate (MainViewModel.ModificaInCorso) faceva SALTARE
///   l'intero giro di aggiornamento (MainViewModel.cs:188 pre-fix), non solo il record in modifica — ritardo
///   illimitato anche per i record che l'utente non stava toccando.
/// </summary>
public sealed class MainViewModelWatcherRefreshTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_MainVmWatcherRefresh_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private static void AttendiCondizione(Func<bool> condizione, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condizione()) return;
            Thread.Sleep(25);
        }
        Assert.Fail($"La condizione attesa non si è verificata in tempo (evento FileSystemWatcher/debounce asincrono). Attesi {sw.ElapsedMilliseconds}ms.");
    }

    /// <summary>Come AttendiCondizione, ma ripete un'azione ASINCRONA (qui: un refresh esplicito) ad ogni
    /// iterazione prima di valutare la condizione — serve perché la scrittura di "un altro PC" arriva al
    /// FileSystemWatcher di questo processo in modo asincrono: il primo refresh dopo la scrittura può correre
    /// prima che l'evento sia stato recapitato e drenato in tracker.Drain(), esattamente come nella app reale
    /// un singolo giro può non vedere ancora l'ultimo evento.</summary>
    private static async Task AttendiCondizioneAsync(Func<Task> azione, Func<bool> condizione, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await azione();
            if (condizione()) return;
            await Task.Delay(25);
        }
        Assert.Fail($"La condizione attesa non si è verificata in tempo dopo refresh ripetuti. Attesi {sw.ElapsedMilliseconds}ms.");
    }

    private static AppState CreaStatoIsolato()
    {
        var configDir = Path.Combine(Path.GetTempPath(), "TestLoom_TestConfig", Guid.NewGuid().ToString("N"));
        return new AppState(new AppConfigService(Path.Combine(configDir, "config.json")), runBackgroundMaintenance: false);
    }

    private AppState CreaStatoMasterInizializzato()
    {
        Directory.CreateDirectory(_dir);
        var state = CreaStatoIsolato();
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Master Test", "1234");
        return state;
    }

    /// <summary>C4: una raffica di eventi ravvicinati del watcher (come una sincronizzazione OneDrive che
    /// consegna in sequenza i file di uno o più salvataggi remoti) deve produrre UN SOLO refresh effettivo,
    /// non uno per evento — e quel refresh deve arrivare in un tempo dell'ordine del debounce (qui accorciato
    /// per il test), ben prima del timer periodico (minuti) che resta solo una rete di sicurezza.</summary>
    [Fact]
    public void RaffigaDiEventiWatcherRavvicinati_ProduceUnSoloRefreshEntroIlDebounce_SenzaAttendereIlTimerPeriodico()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_MainVmDebounce_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var state = CreaStatoIsolato();
        state.InizializzaCartella(dir);
        Assert.True(state.CartellaValida);

        var debounceOriginale = MainViewModel.DebounceRefreshWatcher;
        MainViewModel.DebounceRefreshWatcher = TimeSpan.FromMilliseconds(150);
        MainViewModel? main = null;
        try
        {
            main = new MainViewModel(state);
            var refreshCompletati = 0;
            state.DatiRicaricati += () => Interlocked.Increment(ref refreshCompletati);

            // Raffica di 6 scritture ravvicinate nella cartella Test sorvegliata dal watcher: ognuna, da sola,
            // rilancerebbe il debounce (C4) — deve arrivarne UN SOLO refresh quando la raffica si placa.
            for (var i = 0; i < 6; i++)
            {
                File.WriteAllText(Path.Combine(state.Paths!.TestCasesDir, $"testcase_BURST-{i}.json"), "{}");
                Thread.Sleep(20);
            }

            // Ben entro pochi secondi, non entro i minuti del timer periodico: prova che è l'EVENTO a guidare
            // il refresh, non il timer.
            AttendiCondizione(() => Volatile.Read(ref refreshCompletati) >= 1, timeoutMs: 3000);

            // Attesa oltre il debounce senza nuovi eventi: la raffica già drenata non deve produrre un secondo
            // refresh "in ritardo".
            Thread.Sleep(600);
            Assert.Equal(1, Volatile.Read(ref refreshCompletati));
        }
        finally
        {
            main?.Dispose();
            state.Dispose();
            MainViewModel.DebounceRefreshWatcher = debounceOriginale;
            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    /// <summary>C4: un singolo evento del watcher innesca comunque un refresh (non serve una raffica), sempre
    /// entro il debounce e senza aspettare il timer periodico.</summary>
    [Fact]
    public void SingoloEventoWatcher_InneScaComunqueUnRefresh_SenzaAttendereIlTimerPeriodico()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_MainVmSingoloEvento_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var state = CreaStatoIsolato();
        state.InizializzaCartella(dir);

        var debounceOriginale = MainViewModel.DebounceRefreshWatcher;
        MainViewModel.DebounceRefreshWatcher = TimeSpan.FromMilliseconds(150);
        MainViewModel? main = null;
        try
        {
            main = new MainViewModel(state);
            var refreshCompletati = 0;
            state.DatiRicaricati += () => Interlocked.Increment(ref refreshCompletati);

            File.WriteAllText(Path.Combine(state.Paths!.TestCasesDir, "testcase_SINGOLO.json"), "{}");

            AttendiCondizione(() => Volatile.Read(ref refreshCompletati) >= 1, timeoutMs: 3000);
        }
        finally
        {
            main?.Dispose();
            state.Dispose();
            MainViewModel.DebounceRefreshWatcher = debounceOriginale;
            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    /// <summary>C5: con un TestCaseEditorViewModel aperto e InModifica=true, un refresh in background (qui
    /// forzato tramite AggiornaOraCommand per un test deterministico, stessa esclusione del refresh periodico/
    /// guidato dagli eventi) deve aggiornare gli ALTRI record modificati da un altro PC, ma NON deve toccare
    /// nella cache del repository il record attualmente in modifica — e il lavoro non salvato nel clone
    /// dell'editor resta comunque intatto.</summary>
    [Fact]
    public async Task EditorTestApertoInModifica_AggiornaGliAltriRecordDiUnAltroPc_MaNonQuelloInModifica()
    {
        var state = CreaStatoMasterInizializzato();

        var testA = new TestCase { Id = "TC-PROTETTO", Componente = "Componente-A", Veicolo = "vecchio-A", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now, ModificatoDa = "Master Test" };
        var testB = new TestCase { Id = "TC-ALTRO", Componente = "Componente-B", Veicolo = "vecchio-B", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now, ModificatoDa = "Master Test" };
        state.TestCases.Save(testA, isNew: true);
        state.TestCases.Save(testB, isNew: true);

        var main = new MainViewModel(state);
        var editorA = new TestCaseEditorViewModel(state, testA.Id, () => { }, _ => { });
        await editorA.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(editorA.InModifica);
        main.CurrentPage = editorA;

        // "Un altro PC" (istanza AppState indipendente, stessa cartella condivisa) modifica ENTRAMBI i
        // record scrivendo davvero su disco: solo così il processo di questo test (state/main sopra) li vede
        // arrivare da una rilettura da disco, invece di trovarli già aggiornati in cache dalla propria stessa
        // Save() (che aggiorna la cache in-process immediatamente).
        var statoAltroPc = CreaStatoIsolato();
        statoAltroPc.InizializzaCartella(_dir);
        // ModificatoDa (non Veicolo: quest'ultimo è ricalcolato da NormalizzaVeicoli() ad ogni Save() a partire
        // dalla lista Veicoli, quindi assegnarlo direttamente qui verrebbe silenziosamente sovrascritto) —
        // un campo semplice, non normalizzato, che basta a dimostrare se questo processo ha riletto da disco.
        var aSuAltroPc = statoAltroPc.TestCases.GetAll().First(t => t.Id == testA.Id);
        aSuAltroPc.ModificatoDa = "Altro PC";
        statoAltroPc.TestCases.Save(aSuAltroPc, isNew: false);
        var bSuAltroPc = statoAltroPc.TestCases.GetAll().First(t => t.Id == testB.Id);
        bSuAltroPc.ModificatoDa = "Altro PC";
        statoAltroPc.TestCases.Save(bSuAltroPc, isNew: false);
        statoAltroPc.Dispose();

        // Refresh esplicito (stessa logica di esclusione del refresh periodico/guidato dagli eventi, vedi
        // MainViewModel.CalcolaEsclusioniRefresh); ripetuto perché la scrittura "dell'altro PC" arriva al
        // FileSystemWatcher di questo processo in modo asincrono (vedi AttendiCondizioneAsync).
        await AttendiCondizioneAsync(
            () => main.AggiornaOraCommand.ExecuteAsync(null),
            () => state.TestCases.GetAll().First(t => t.Id == testB.Id).ModificatoDa == "Altro PC");

        var bDopo = state.TestCases.GetAll().First(t => t.Id == testB.Id);
        Assert.Equal("Altro PC", bDopo.ModificatoDa); // NON in modifica: si aggiorna normalmente

        var aDopo = state.TestCases.GetAll().First(t => t.Id == testA.Id);
        Assert.Equal("Master Test", aDopo.ModificatoDa); // in modifica: la cache del repository NON viene toccata

        // Vincolo assoluto: il lavoro non salvato nel clone dell'editor resta comunque intatto.
        Assert.Equal("vecchio-A", editorA.TestCase.Veicolo);
        Assert.True(editorA.InModifica);

        main.Dispose();
    }

    /// <summary>C5, caso di controllo: SENZA nessun editor aperto lo stesso refresh aggiorna anche il record
    /// che nel test precedente restava protetto, a conferma che l'esclusione è mirata al solo record in
    /// modifica e non un effetto collaterale generico.</summary>
    [Fact]
    public async Task SenzaEditorAperto_LoStessoRefreshAggiornaAncheIlRecordCheAltrimentiSarebbeProtetto()
    {
        var state = CreaStatoMasterInizializzato();

        var testA = new TestCase { Id = "TC-NONPROTETTO", Componente = "Componente-A", Veicolo = "vecchio-A", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now, ModificatoDa = "Master Test" };
        state.TestCases.Save(testA, isNew: true);

        var main = new MainViewModel(state);
        // Nessun editor aperto: CurrentPage resta quello di default (Home) dopo la costruzione.
        Assert.IsNotType<TestCaseEditorViewModel>(main.CurrentPage);

        var statoAltroPc = CreaStatoIsolato();
        statoAltroPc.InizializzaCartella(_dir);
        var aSuAltroPc = statoAltroPc.TestCases.GetAll().First(t => t.Id == testA.Id);
        aSuAltroPc.ModificatoDa = "Altro PC";
        statoAltroPc.TestCases.Save(aSuAltroPc, isNew: false);
        statoAltroPc.Dispose();

        await AttendiCondizioneAsync(
            () => main.AggiornaOraCommand.ExecuteAsync(null),
            () => state.TestCases.GetAll().First(t => t.Id == testA.Id).ModificatoDa == "Altro PC");

        var aDopo = state.TestCases.GetAll().First(t => t.Id == testA.Id);
        Assert.Equal("Altro PC", aDopo.ModificatoDa);

        main.Dispose();
    }
}
