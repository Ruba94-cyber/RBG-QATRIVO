using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using System.Diagnostics;

namespace TestLoom.App.Tests;

/// <summary>
/// Decisione utente 6.4.1 "Avviso + merge automatico", punto A: avviso non bloccante quando è visibile — arrivato
/// via sync OneDrive/SharePoint — un lock/heartbeat ATTIVO di un'altra postazione/operatore sullo stesso Test.
/// Verificato qui tramite <see cref="TestCaseEditorViewModel.AvvisoAltraPostazioneVisibile"/> e
/// <see cref="TestCaseEditorViewModel.AvvisoAltraPostazioneTesto"/>, e tramite <see cref="ConfirmService"/> per
/// il "Salva comunque"/"Annulla" richiesto subito prima del Salva.
///
/// Il lock di un'altra postazione è simulato scrivendo direttamente il file .lock accanto al Test (stesso
/// approccio già usato da <c>LockServiceTests</c>/<c>LockSessionAndSystemAuditTests</c>): rappresenta il file
/// arrivato via sync da un altro PC, che <see cref="LockService"/> tratterebbe comunque come lock attivo reale.
/// </summary>
[Collection("Localization")]
public sealed class TestCaseEditorAltraPostazioneAvvisoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_AvvisoAltraPostazione_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;

    private sealed class TestMonotonicClock
    {
        private long _timestamp;

        public LockLeaseObservationTracker CreateTracker() => new(() => Interlocked.Read(ref _timestamp));

        public void Advance(TimeSpan elapsed)
        {
            var delta = checked((long)((decimal)elapsed.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond));
            Interlocked.Add(ref _timestamp, delta);
        }
    }

    public void Dispose()
    {
        ConfirmService.Handler = _confirmOriginale;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Mario Rossi", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Mario Rossi", "1234");
        return state;
    }

    /// <summary>Crea e salva un Test valido (nessun campo obbligatorio mancante), cosi' il resto dei test può
    /// riaprirlo come test ESISTENTE (PercorsoFile risolvibile, lock reale sul path condiviso).</summary>
    private static async Task<TestCase> CreaTestValidoSuDisco(AppState state)
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
        return state.TestCases.GetById(id)!;
    }

    private static void ScriviLockAltraPostazione(AppState state, TestCase tc, string operatore, string deviceId, DateTime acquisitoIl)
    {
        var path = state.TestCases.PercorsoPer(tc);
        var lockPath = LockService.LockPathFor(path);
        var info = new LockInfo
        {
            Operatore = operatore,
            AcquisitoIl = acquisitoIl,
            UltimoHeartbeat = acquisitoIl,
            DeviceId = deviceId,
            SessionId = Guid.NewGuid().ToString("N")
        };
        File.WriteAllText(lockPath, System.Text.Json.JsonSerializer.Serialize(info, JsonIo.Options));
        // Keep remote timestamps old to exercise clock-skew handling; staleness comes from local observation.
        File.SetLastWriteTimeUtc(lockPath, acquisitoIl.ToUniversalTime());
    }

    [Fact]
    public async Task AllApertura_NessunLockAltrui_NessunAvviso()
    {
        var state = CreateState();
        var tc = await CreaTestValidoSuDisco(state);
        var vm = new TestCaseEditorViewModel(state, tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });

        await vm.IniziaModificaCommand.ExecuteAsync(null);

        Assert.True(vm.InModifica);
        Assert.False(vm.AvvisoAltraPostazioneVisibile);
        Assert.Null(vm.AvvisoAltraPostazioneTesto);
        vm.Dispose();
    }

    /// <summary>Prima del Salva viene ririchiesto lo stato del lock: un lock di un'altra postazione arrivato via
    /// sync DOPO l'apertura (mentre si stava già modificando) deve comunque far comparire l'avviso e la conferma
    /// "Salva comunque"/"Annulla", anche se all'apertura non c'era nulla di visibile.</summary>
    [Fact]
    public async Task PrimaDelSalva_LockAltraPostazioneArrivatoViaSyncDopoLApertura_ChiedeConfermaEsplicita()
    {
        var state = CreateState();
        var tc = await CreaTestValidoSuDisco(state);
        var vm = new TestCaseEditorViewModel(state, tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.False(vm.AvvisoAltraPostazioneVisibile); // ancora nulla all'apertura

        // Simula l'arrivo via sync, qualche minuto dopo l'apertura, del lock di un'altra postazione/operatore
        // sullo stesso Test — sovrascrive il file .lock esattamente come farebbe OneDrive.
        ScriviLockAltraPostazione(state, vm.TestCase, "Luca Bianchi", "PC-LINEA-2", DateTime.Now.AddMinutes(-3));

        var titoloVisto = "";
        var messaggioVisto = "";
        ConfirmService.Handler = (titolo, messaggio, _, _) => { titoloVisto = titolo; messaggioVisto = messaggio; return Task.FromResult(true); };

        vm.TestCase.Descrizione = "Descrizione completa - modifica 1";
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.AvvisoAltraPostazioneVisibile);
        Assert.Contains("Luca Bianchi", vm.AvvisoAltraPostazioneTesto);
        Assert.NotEqual("", titoloVisto);
        Assert.Contains("Luca Bianchi", messaggioVisto);

        // "Salva comunque" (handler ha risposto true): il salvataggio deve essere avvenuto per davvero.
        var rilettoDopoSalvaComunque = state.TestCases.LoadCurrent(state.TestCases.GetById(tc.Id)!);
        Assert.Equal("Descrizione completa - modifica 1", rilettoDopoSalvaComunque!.Descrizione);
        vm.Dispose();
    }

    [Fact]
    public async Task PrimaDelSalva_AnnullaSullAvviso_LaBozzaRestaIntattaSuDisco()
    {
        var state = CreateState();
        var tc = await CreaTestValidoSuDisco(state);
        var vm = new TestCaseEditorViewModel(state, tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);

        ScriviLockAltraPostazione(state, vm.TestCase, "Luca Bianchi", "PC-LINEA-2", DateTime.Now.AddMinutes(-1));
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(false); // "Annulla"

        vm.TestCase.Descrizione = "Descrizione che NON deve essere salvata";
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.AvvisoAltraPostazioneVisibile);
        var rilettaSuDisco = state.TestCases.LoadCurrent(state.TestCases.GetById(tc.Id)!);
        Assert.Equal("Descrizione completa", rilettaSuDisco!.Descrizione); // valore originale, non toccato
        vm.Dispose();
    }

    /// <summary>Non deve scattare per il proprio lock: l'identità è la sessione di LockService (SessionId).</summary>
    [Fact]
    public async Task PropriaPostazione_StessoOperatoreEStessoDevice_NessunAvviso()
    {
        var state = CreateState();
        var tc = await CreaTestValidoSuDisco(state);
        var vm = new TestCaseEditorViewModel(state, tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);

        // Heartbeat periodico del PROPRIO processo (stessa sessione): mai un avviso per sé stessi.
        state.Locks.Heartbeat(state.TestCases.PercorsoPer(vm.TestCase), "Mario Rossi");

        var chiestaConferma = false;
        ConfirmService.Handler = (_, _, _, _) => { chiestaConferma = true; return Task.FromResult(true); };
        vm.TestCase.Descrizione = "Descrizione completa - modifica propria postazione";
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.AvvisoAltraPostazioneVisibile);
        Assert.False(chiestaConferma);
        vm.Dispose();
    }

    /// <summary>Non deve scattare per un lock scaduto (regole di scadenza esistenti, LockService.Timeout).</summary>
    [Fact]
    public async Task LockAltraPostazioneScaduto_NessunAvviso()
    {
        var state = CreateState();
        var clock = new TestMonotonicClock();
        state.Locks = new LockService("PC-Lettore", clock.CreateTracker());
        var tc = await CreaTestValidoSuDisco(state);
        var vm = new TestCaseEditorViewModel(state, tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);

        ScriviLockAltraPostazione(state, vm.TestCase, "Luca Bianchi", "PC-LINEA-2",
            DateTime.Now - LockService.Timeout - TimeSpan.FromMinutes(5));
        var path = state.TestCases.PercorsoPer(vm.TestCase);
        Assert.Equal(StatoLockAltrui.Occupato, state.Locks.GetForeignActiveLock(path, "Mario Rossi").Stato);
        clock.Advance(LockService.Timeout + TimeSpan.FromSeconds(1));
        Assert.Equal(StatoLockAltrui.Libero, state.Locks.GetForeignActiveLock(path, "Mario Rossi").Stato);

        var chiestaConferma = false;
        ConfirmService.Handler = (_, _, _, _) => { chiestaConferma = true; return Task.FromResult(true); };
        vm.TestCase.Descrizione = "Descrizione completa - lock scaduto altrui";
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.AvvisoAltraPostazioneVisibile);
        Assert.False(chiestaConferma);
        var riletta = state.TestCases.LoadCurrent(state.TestCases.GetById(tc.Id)!);
        Assert.Equal("Descrizione completa - lock scaduto altrui", riletta!.Descrizione);
        vm.Dispose();
    }

    [Fact]
    public async Task LockRemotoConTimestampVecchioAlPrimoAvvistamento_MostraAvviso()
    {
        var state = CreateState();
        var clock = new TestMonotonicClock();
        state.Locks = new LockService("PC-Lettore", clock.CreateTracker());
        var tc = await CreaTestValidoSuDisco(state);
        var vm = new TestCaseEditorViewModel(state, tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);

        ScriviLockAltraPostazione(state, vm.TestCase, "Luca Bianchi", "PC-LINEA-2",
            DateTime.Now - LockService.Timeout - TimeSpan.FromMinutes(5));

        var chiestaConferma = false;
        ConfirmService.Handler = (_, _, _, _) => { chiestaConferma = true; return Task.FromResult(false); };
        vm.TestCase.Descrizione = "Modifica bloccata da prima osservazione";
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.AvvisoAltraPostazioneVisibile);
        Assert.True(chiestaConferma);
        Assert.Equal("Descrizione completa", state.TestCases.LoadCurrent(state.TestCases.GetById(tc.Id)!)!.Descrizione);
        vm.Dispose();
    }

    /// <summary>Bug utente: editor aperto a lungo, heartbeat saltati, il proprio lock risulta scaduto ma nessun
    /// altro lo detiene: niente avviso, e il lock viene riacquisito in modo trasparente.</summary>
    [Fact]
    public async Task ProprioLockScadutoSenzaAltriDetentori_NessunAvvisoERiacquisito()
    {
        var state = CreateState();
        var tc = await CreaTestValidoSuDisco(state);
        var vm = new TestCaseEditorViewModel(state, tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);

        var path = state.TestCases.PercorsoPer(vm.TestCase);
        var lockPath = LockService.LockPathFor(path);
        var info = System.Text.Json.JsonSerializer.Deserialize<LockInfo>(File.ReadAllText(lockPath), JsonIo.Options)!;
        info.UltimoHeartbeat = DateTime.Now - LockService.Timeout - TimeSpan.FromMinutes(10);
        File.WriteAllText(lockPath, System.Text.Json.JsonSerializer.Serialize(info, JsonIo.Options));
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - LockService.Timeout - TimeSpan.FromMinutes(10));

        var chiestaConferma = false;
        ConfirmService.Handler = (_, _, _, _) => { chiestaConferma = true; return Task.FromResult(true); };
        vm.TestCase.Descrizione = "Descrizione completa - lock proprio scaduto";
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.AvvisoAltraPostazioneVisibile);
        Assert.False(chiestaConferma);
        vm.Dispose();
    }
}
