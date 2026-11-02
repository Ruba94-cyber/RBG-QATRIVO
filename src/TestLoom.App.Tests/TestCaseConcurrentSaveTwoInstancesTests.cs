using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Due postazioni (due <see cref="AppState"/> distinti, config/cartella locale separati, stessa cartella
/// condivisa) salvano lo STESSO Test quasi in contemporanea, partendo dalla STESSA revisione base — stesso
/// schema di race di <see cref="TestSaveBeginRaceTests"/>, ma qui la contesa è direttamente sulla scrittura
/// concorrente del file del Test tramite <see cref="TestCaseRepository.Save(TestCase, bool, long?)"/> (l'API
/// diretta del repository, la stessa che <c>SalvaSicuroCommand</c> chiama internamente — non passa per il lock
/// di editing/heartbeat di <c>TestCaseEditorViewModel</c>, che è un livello sopra e non è l'oggetto di questi
/// test).
///
/// STORIA: il controllo optimistic-concurrency in <c>TestCaseRepository.SaveCore</c> (confronto
/// <c>revisioneAttesa</c> vs <c>corrente.RevisioneDati</c> letto da disco) era una classica finestra
/// check-then-act SENZA alcun lock condiviso fra le due istanze — ognuna aveva il proprio <c>_gate</c> privato,
/// che serializza solo le chiamate sulla STESSA istanza di repository, non fra due AppState diversi (cioè non
/// fra due PC reali che condividono la cartella). Con la stessa <c>revisioneAttesa</c> di partenza, ENTRAMBE le
/// Save eseguite quasi simultaneamente potevano superare il controllo e scrivere, senza mai lanciare
/// <see cref="ConflittoRevisioneException"/>: la revisione finale avanzava di UNA sola unità (non due) e la
/// descrizione di una delle due postazioni veniva sovrascritta in modo silenzioso.
///
/// FIX: <see cref="TestCaseCommitLock"/> introduce un lock di commit fra processi (file sentinella
/// <c>.commitlock</c>, esclusivo via <see cref="FileShare.None"/>) che copre l'intera finestra rilettura ->
/// confronto -> scrittura. Sullo stesso PC (incluso questo caso di test, dove A e B condividono letteralmente
/// lo stesso filesystem locale) la serializzazione è completa e DETERMINISTICA: con la stessa revisione base,
/// una delle due Save vince sempre e l'altra riceve sempre <see cref="ConflittoRevisioneException"/>, mai più
/// un doppio successo silenzioso. I test sotto verificano questo comportamento corretto.
/// </summary>
public sealed class TestCaseConcurrentSaveTwoInstancesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_TestCaseConcurrentSave_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private AppState CreateState(string localSuffix)
    {
        var dataRoot = Path.Combine(_root, "data");
        var configPath = Path.Combine(_root, "local_" + localSuffix, "config.json");
        if (!Directory.Exists(dataRoot))
        {
            Directory.CreateDirectory(dataRoot);
            var configSeed = new AppConfigService(configPath);
            configSeed.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Master Test" });
            using var seed = new AppState(configSeed, runBackgroundMaintenance: false);
            var ops = seed.Master.Operatori.Load().ToList();
            ops.Add(TestOperatori.Sigilla(new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" }));
            seed.Master.Operatori.Save(ops);
            SqliteConnection.ClearAllPools();
        }

        var configService = new AppConfigService(configPath);
        configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Master Test" });
        var state = new AppState(configService, runBackgroundMaintenance: false);
        state.InizializzaCartella(dataRoot);
        state.ImpostaOperatore("Master Test", "1234");
        return state;
    }

    /// <summary>
    /// Due postazioni salvano lo stesso Test partendo dalla stessa revisione base, quasi in contemporanea.
    /// Con il lock di commit la finestra check-then-act non esiste più: il comportamento corretto è ora
    /// deterministico ad ogni iterazione, non solo "possibile a seconda dello scheduling" come prima del fix.
    /// </summary>
    [Fact]
    public async Task DuePostazioni_SalvanoLoStessoTestPartendoDallaStessaRevisione_UnaVinceLAltraRiceveConflittoRevisione()
    {
        using var statoA = CreateState("A");
        using var statoB = CreateState("B");

        const int iterazioni = 5;
        for (var iterazione = 0; iterazione < iterazioni; iterazione++)
        {
            var id = $"TC-RACE-SAVE-{iterazione:D4}";
            statoA.TestCases.Save(new TestCase
            {
                Id = id,
                Componente = "BCM",
                Descrizione = "autorevole",
                ModificatoDa = "Setup",
                DataCreazione = DateTime.Now,
                UltimaModifica = DateTime.Now,
            }, isNew: true);
            statoA.TestCases.RefreshIndex();
            statoB.TestCases.RefreshIndex();

            var baseA = statoA.TestCases.GetAll().Single(t => t.Id == id);
            var baseB = statoB.TestCases.GetAll().Single(t => t.Id == id);
            var revisioneBase = baseA.RevisioneDati;
            baseA.Descrizione = "modifica da postazione A";
            baseB.Descrizione = "modifica da postazione B";

            Exception? erroreA = null, erroreB = null;
            var barrier = new Barrier(2);
            await Task.WhenAll(
                Task.Run(() => { barrier.SignalAndWait(); try { statoA.TestCases.Save(baseA, isNew: false, revisioneBase); } catch (Exception ex) { erroreA = ex; } }),
                Task.Run(() => { barrier.SignalAndWait(); try { statoB.TestCases.Save(baseB, isNew: false, revisioneBase); } catch (Exception ex) { erroreB = ex; } }));

            // Esattamente una delle due vince, l'altra riceve un conflitto di revisione gestito. Mai più
            // "entrambe senza errore" (la perdita silenziosa) e mai un blocco/eccezione non gestita.
            var esiti = new[] { erroreA, erroreB };
            Assert.Single(esiti, e => e == null);
            Assert.Single(esiti, e => e is ConflittoRevisioneException);

            // La modifica della postazione che ha perso resta comunque nell'oggetto in memoria (il contratto
            // "le modifiche restano nell'editor" — qui verificato al livello del repository): SaveCore ripristina
            // solo RevisioneDati/fingerprint/storico di sicurezza, mai i campi utente come Descrizione.
            Assert.True(baseA.Descrizione == "modifica da postazione A");
            Assert.True(baseB.Descrizione == "modifica da postazione B");

            statoA.TestCases.RefreshIndex();
            statoB.TestCases.RefreshIndex();
            var autorevole = statoA.TestCases.GetAll().Single(t => t.Id == id);
            Assert.Equal(revisioneBase + 1, autorevole.RevisioneDati);
            Assert.True(autorevole.Descrizione is "modifica da postazione A" or "modifica da postazione B");
            var vistaDallAltraPostazione = statoB.TestCases.GetAll().Single(t => t.Id == id);
            Assert.Equal(autorevole.Descrizione, vistaDallAltraPostazione.Descrizione);

            // Il file fisico resta leggibile e non corrotto.
            var percorso = statoA.TestCases.PercorsoPer(autorevole);
            var suDisco = JsonIo.TryRead<TestCase>(percorso, out var errore);
            Assert.NotNull(suDisco);
            Assert.Null(errore);

            // Nessun residuo del file sentinella dopo un Save riuscito né dopo quello che ha ricevuto il
            // conflitto: DeleteOnClose + la rete di sicurezza in Handle.Dispose devono aver ripulito entrambi.
            Assert.False(File.Exists(TestCaseCommitLock.LockPathFor(percorso)));
        }
    }

    /// <summary>Verifica indipendente dal test sopra: quando una delle due Save viene rifiutata con un
    /// conflitto, il percorso di retry "ricarica e riprova" resta corretto e non perde né duplica nulla.</summary>
    [Fact]
    public async Task DopoLaPrimaFaseConcorrente_UnRetryEsplicitoSullaRevisioneFrescaConvergeSempreSenzaPerditaNeDuplicazione()
    {
        using var statoA = CreateState("RetryA");
        using var statoB = CreateState("RetryB");

        statoA.TestCases.Save(new TestCase
        {
            Id = "TC-RACE-RETRY-0001",
            Componente = "BCM",
            Descrizione = "base",
            ModificatoDa = "Setup",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        }, isNew: true);
        statoA.TestCases.RefreshIndex();
        statoB.TestCases.RefreshIndex();

        var baseA = statoA.TestCases.GetAll().Single(t => t.Id == "TC-RACE-RETRY-0001");
        var baseB = statoB.TestCases.GetAll().Single(t => t.Id == "TC-RACE-RETRY-0001");
        var revisioneBase = baseA.RevisioneDati;
        baseA.Descrizione = "A-primo-tentativo";
        baseB.Descrizione = "B-primo-tentativo";

        var barrier = new Barrier(2);
        Exception? erroreA = null, erroreB = null;
        await Task.WhenAll(
            Task.Run(() => { barrier.SignalAndWait(); try { statoA.TestCases.Save(baseA, isNew: false, revisioneBase); } catch (Exception ex) { erroreA = ex; } }),
            Task.Run(() => { barrier.SignalAndWait(); try { statoB.TestCases.Save(baseB, isNew: false, revisioneBase); } catch (Exception ex) { erroreB = ex; } }));

        // Con il fix l'esito è deterministico (uno null, uno ConflittoRevisioneException), ma il retry sotto
        // deve convergere comunque qualunque sia l'accoppiamento — è la garanzia indipendente che questo test
        // vuole dimostrare, non un dettaglio sull'esito della prima fase.
        statoA.TestCases.RefreshIndex();
        statoB.TestCases.RefreshIndex();

        void RitentaConRevisioneFresca(AppState stato, string descrizione)
        {
            var fresco = stato.TestCases.GetAll().Single(t => t.Id == "TC-RACE-RETRY-0001");
            fresco.Descrizione = descrizione;
            var ex = Record.Exception(() => stato.TestCases.Save(fresco, isNew: false, fresco.RevisioneDati));
            Assert.Null(ex); // sulla revisione fresca il retry deve sempre riuscire, nessun conflitto residuo.
        }

        RitentaConRevisioneFresca(statoA, "A-retry");
        // B ricarica DOPO il retry di A: deve vedere la revisione di A e ripetere sulla base corretta, non su
        // quella stantia già usata nel primo tentativo.
        statoB.TestCases.RefreshIndex();
        RitentaConRevisioneFresca(statoB, "B-retry");

        statoA.TestCases.RefreshIndex();
        statoB.TestCases.RefreshIndex();
        var finaleDaA = statoA.TestCases.GetAll().Single(t => t.Id == "TC-RACE-RETRY-0001");
        var finaleDaB = statoB.TestCases.GetAll().Single(t => t.Id == "TC-RACE-RETRY-0001");
        Assert.Equal(finaleDaA.RevisioneDati, finaleDaB.RevisioneDati);
        Assert.Equal("B-retry", finaleDaA.Descrizione); // l'ultimo retry sequenziale (mai in race) vince, senza perdite.
        Assert.Equal(finaleDaA.Descrizione, finaleDaB.Descrizione); // entrambe le postazioni convergono sullo stesso stato.
    }

    /// <summary>
    /// Il fork OneDrive compare quando il writer e' in attesa del lock locale.
    /// Dopo l'acquisizione va controllata l'identita' visibile: la sola
    /// validazione fatta PRIMA dell'attesa sarebbe una finestra check-then-act.
    /// </summary>
    [Fact]
    public async Task CopiaConflittoArrivataMentreLockOccupato_BloccaSalvataggioDopoAcquisizione()
    {
        using var state = CreateState("LateConflict");
        state.TestCases.Save(new TestCase
        {
            Id = "TC-LATE-VISIBLE-FORK", Componente = "BCM",
            Descrizione = "originale", ModificatoDa = "Setup",
            DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now
        }, isNew: true);
        state.TestCases.RefreshIndex();

        var testcase = state.TestCases.GetAll().Single(t => t.Id == "TC-LATE-VISIBLE-FORK");
        var canonical = state.TestCases.PercorsoPer(testcase);
        var forkPath = canonical.Replace(".json", " (Secondo-PC).json",
            StringComparison.OrdinalIgnoreCase);
        testcase.Descrizione = "modifica nel primo PC";
        var before = File.ReadAllBytes(canonical);
        using var lockHeld = new FileStream(TestCaseCommitLock.LockPathFor(canonical),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.DeleteOnClose);

        using var requested = new ManualResetEventSlim();
        var save = Task.Run(() =>
        {
            requested.Set();
            return Record.Exception(() => state.TestCases.Save(
                testcase, isNew: false, revisioneAttesa: testcase.RevisioneDati,
                lockTimeoutCommit: TimeSpan.FromSeconds(5)));
        });

        Assert.True(requested.Wait(TimeSpan.FromSeconds(2)),
            "Il writer TestCase deve partire mentre il lock e' occupato.");
        var conflicting = EntityStateFingerprint.Clone(testcase);
        conflicting.Descrizione = "versione su altro PC";
        JsonIo.WriteAtomic(forkPath, conflicting);
        var forkBefore = File.ReadAllBytes(forkPath);

        lockHeld.Dispose();
        var failure = await save.WaitAsync(TimeSpan.FromSeconds(7));

        Assert.NotNull(failure);
        Assert.IsAssignableFrom<IOException>(failure);
        Assert.Contains("divergenti", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("modifica nel primo PC", testcase.Descrizione);
        Assert.Equal(before, File.ReadAllBytes(canonical));
        Assert.Equal(forkBefore, File.ReadAllBytes(forkPath));
    }

    /// <summary>Il lock di commit non blocca indefinitamente: se il file sentinella resta occupato più del
    /// timeout, Save riceve un errore gestito e comprensibile (mai un hang, mai una scrittura senza lock), e le
    /// modifiche locali non vengono toccate.</summary>
    [Fact]
    public void SeIlLockDiCommitRestaOccupato_SaveTimeoutInTempiLimitatiConErroreGestito()
    {
        using var stato = CreateState("Timeout");
        stato.TestCases.Save(new TestCase
        {
            Id = "TC-LOCK-TIMEOUT-0001",
            Componente = "BCM",
            Descrizione = "base",
            ModificatoDa = "Setup",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        }, isNew: true);
        stato.TestCases.RefreshIndex();

        var tc = stato.TestCases.GetAll().Single(t => t.Id == "TC-LOCK-TIMEOUT-0001");
        var percorso = stato.TestCases.PercorsoPer(tc);
        var lockPath = TestCaseCommitLock.LockPathFor(percorso);

        tc.Descrizione = "modifica bloccata dal lock occupato";

        // DeleteOnClose mima esattamente la semantica del vero file sentinella (TestCaseCommitLock.Handle):
        // l'unico file sentinella presente durante il timeout è questo, non uno creato da Save (AcquireOne
        // fallisce e basta, senza mai riuscire ad aprirne una propria copia mentre questo handle è esclusivo).
        var occupante = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.DeleteOnClose);

        var timeout = TimeSpan.FromSeconds(2);
        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var ex = Record.Exception(() => stato.TestCases.Save(tc, isNew: false, tc.RevisioneDati, timeout));
        cronometro.Stop();

        Assert.IsType<TestCaseSaveLockTimeoutException>(ex);
        // Nessun blocco indefinito: il fallimento arriva vicino al timeout richiesto, con un margine ampio ma
        // limitato per non rendere il test fragile su macchine CI più lente.
        Assert.True(cronometro.Elapsed < timeout + TimeSpan.FromSeconds(5),
            $"Il timeout del lock ha impiegato troppo: {cronometro.Elapsed}");

        // Le modifiche locali non sono state toccate dal fallimento.
        Assert.Equal("modifica bloccata dal lock occupato", tc.Descrizione);

        // Il file su disco non è stato alterato: nessuna scrittura è mai avvenuta senza lock.
        var suDisco = JsonIo.TryRead<TestCase>(percorso, out var errore);
        Assert.NotNull(suDisco);
        Assert.Null(errore);
        Assert.Equal("base", suDisco!.Descrizione);

        // Dopo che anche l'occupante di test rilascia il proprio handle, non deve restare alcun residuo sul
        // disco: il tentativo di Save fallito per timeout non ne ha mai creato una copia propria.
        occupante.Dispose();
        Assert.False(File.Exists(lockPath));
    }

    /// <summary>Dopo che una Save fallisce (conflitto di revisione), il lock di commit deve essere stato
    /// rilasciato: una Save successiva sullo stesso path non deve mai restare bloccata da un lock residuo della
    /// chiamata fallita precedente.</summary>
    [Fact]
    public void DopoUnConflittoDiRevisione_IlLockVieneRilasciatoEUnaSaveSuccessivaNonRestaBloccata()
    {
        using var stato = CreateState("ReleaseAfterFailure");
        stato.TestCases.Save(new TestCase
        {
            Id = "TC-LOCK-RELEASE-0001",
            Componente = "BCM",
            Descrizione = "base",
            ModificatoDa = "Setup",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        }, isNew: true);
        stato.TestCases.RefreshIndex();

        var tc = stato.TestCases.GetAll().Single(t => t.Id == "TC-LOCK-RELEASE-0001");
        var revisioneAttesaStantia = tc.RevisioneDati - 1; // forza il conflitto senza serve una seconda postazione reale
        tc.Descrizione = "tentativo con revisione stantia";

        var conflitto = Record.Exception(() => stato.TestCases.Save(tc, isNew: false, revisioneAttesaStantia));
        Assert.IsType<ConflittoRevisioneException>(conflitto);

        // Nessun residuo del file sentinella dopo il conflitto di revisione.
        var percorso = stato.TestCases.PercorsoPer(tc);
        Assert.False(File.Exists(TestCaseCommitLock.LockPathFor(percorso)));

        // Se il lock fosse rimasto occupato dopo l'eccezione, questa seconda Save (sulla revisione corretta)
        // andrebbe in timeout invece di riuscire subito.
        tc.RevisioneDati = revisioneAttesaStantia + 1;
        tc.Descrizione = "salvataggio successivo dopo il rilascio del lock";
        var timeout = TimeSpan.FromSeconds(3);
        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var ex = Record.Exception(() => stato.TestCases.Save(tc, isNew: false, tc.RevisioneDati, timeout));
        cronometro.Stop();

        Assert.Null(ex);
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(1),
            $"La Save successiva ha impiegato troppo, il lock non sembra rilasciato subito: {cronometro.Elapsed}");
    }

    /// <summary>Caso (b) del bug storico: il File.Move concorrente della scrittura atomica lancia una
    /// IOException/UnauthorizedAccessException non gestita invece di un conflitto di revisione riconosciuto.
    /// Qui si simula la contesa mantenendo il file di destinazione apeto in scrittura esclusiva da un altro
    /// handle mentre Save tenta di sostituirlo: File.Move deve fallire, e SaveCore deve tradurre l'eccezione in
    /// ConflittoRevisioneException, lasciando il file su disco intatto.</summary>
    [Fact]
    public void SeLaScritturaAtomicaFallisceOccupataDaUnAltroHandle_DiventaConflittoRevisioneNonEccezioneGrezza()
    {
        using var stato = CreateState("WriteRace");
        stato.TestCases.Save(new TestCase
        {
            Id = "TC-LOCK-WRITERACE-0001",
            Componente = "BCM",
            Descrizione = "base",
            ModificatoDa = "Setup",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        }, isNew: true);
        stato.TestCases.RefreshIndex();

        var tc = stato.TestCases.GetAll().Single(t => t.Id == "TC-LOCK-WRITERACE-0001");
        var percorso = stato.TestCases.PercorsoPer(tc);
        tc.Descrizione = "modifica che dovrebbe scrivere ma trova il target occupato";

        // Un handle esclusivo sul file di destinazione impedisce a File.Move di sostituirlo: sullo stesso PC è
        // esattamente la meccanica del bug (b) osservato con due processi che scrivono lo stesso file.
        using var occupanteDestinazione = new FileStream(percorso, FileMode.Open, FileAccess.Read, FileShare.Read);

        var ex = Record.Exception(() => stato.TestCases.Save(tc, isNew: false, tc.RevisioneDati));

        Assert.IsType<ConflittoRevisioneException>(ex);

        // Le modifiche locali restano nell'oggetto anche dopo il fallimento (contratto editor).
        Assert.Equal("modifica che dovrebbe scrivere ma trova il target occupato", tc.Descrizione);

        occupanteDestinazione.Dispose();

        // Il file su disco è integro e leggibile: la vulnerabilità storica era sulla gestione dell'errore, non
        // sull'integrità del JSON (WriteAtomic scrive sempre su un file temporaneo prima del rename).
        var suDisco = JsonIo.TryRead<TestCase>(percorso, out var errore);
        Assert.NotNull(suDisco);
        Assert.Null(errore);
        Assert.Equal("base", suDisco!.Descrizione);

        // Nessun residuo del file sentinella: il lock viene rilasciato anche quando il fallimento arriva dalla
        // scrittura atomica stessa (File.Move), non solo dal confronto revisione/hash.
        Assert.False(File.Exists(TestCaseCommitLock.LockPathFor(percorso)));
    }
}
