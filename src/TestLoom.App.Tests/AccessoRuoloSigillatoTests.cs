using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata al piano 6.4.2, §4b (decisione utente 2026-09-28): modalità di accesso
/// con/senza PIN come impostazione unica sigillata della cartella dati, e ruolo operatore sigillato
/// (HMAC su Nome+Ruolo, stesso livello di protezione di PinService.Cifra). Ogni test isola la propria
/// cartella temporanea, mai la cartella dati reale.</summary>
public sealed class AccessoRuoloSigillatoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_AccessoRuolo_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        return state;
    }

    private static Operatore AggiungiOperatoreSigillato(AppState state, string nome, RuoloOperatore ruolo, string pin)
    {
        var op = new Operatore { Nome = nome, Ruolo = ruolo, PinHash = PinService.Hash(pin), PinChiaro = pin, ModificatoDa = "Test" };
        RuoloSigilloService.Sigilla(op);
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        return op;
    }

    // ============================================================================ Ruolo sigillato

    /// <summary>Regressione 6.4.2: una proprietà nativa Operatore.Uid aveva sostituito l'Uid tecnico Phase D
    /// (_ditmUid) e a ogni riapertura il catalogo operatori falliva con "Uid payload Phase D diverso
    /// dall'envelope". Sigillo e identità devono sopravvivere a una riapertura completa della cartella.</summary>
    [Fact]
    public void OperatoreSigillato_SopravviveAllaRiaperturaDellaCartella()
    {
        using (var state = CreateState())
        {
            AggiungiOperatoreSigillato(state, "Master Persistente", RuoloOperatore.Master, "1234");
        }

        using var riaperto = TestStateFactory.Create();
        riaperto.InizializzaCartella(_dir);
        var caricato = riaperto.Master.Operatori.Load().Single(o => o.Nome == "Master Persistente");
        Assert.True(caricato.SigilloRuoloValido);
        Assert.Equal(RuoloOperatore.Master, caricato.RuoloEffettivo);
    }

    [Fact]
    public void RuoloManomessoAManoNelJson_ValeComeViewerENonPassaLaVerifica()
    {
        var state = CreateState();

        // Simula una promozione fatta a mano nel JSON, fuori dall'app: un Viewer diventa Master cambiando
        // Ruolo SENZA passare da RuoloSigilloService.Sigilla (che un editor a testo non chiamerebbe mai).
        var viewer = new Operatore { Nome = "Viewer Manomesso", Ruolo = RuoloOperatore.Viewer, PinHash = PinService.Hash("0000"), ModificatoDa = "Test" };
        RuoloSigilloService.Sigilla(viewer);
        viewer.Ruolo = RuoloOperatore.Master; // manomissione: cambia il ruolo senza risigillare.
        var listaConManomesso = state.Master.Operatori.Load();
        listaConManomesso.Add(viewer);
        state.Master.Operatori.Save(listaConManomesso);

        var caricato = state.Master.Operatori.Load().Single(o => o.Nome == "Viewer Manomesso");
        Assert.False(caricato.SigilloRuoloValido);
        Assert.Equal(RuoloOperatore.Viewer, caricato.RuoloEffettivo);
        Assert.Equal(RuoloOperatore.Master, caricato.Ruolo); // il campo grezzo resta quello manomesso...
        Assert.NotEqual(caricato.Ruolo, caricato.RuoloEffettivo); // ...ma non è mai quello che conta.
    }

    [Fact]
    public void Login_ConRuoloManomesso_AppStateTrattaLOperatoreComeViewer()
    {
        var state = CreateState();
        var viewer = new Operatore { Nome = "Manomesso", Ruolo = RuoloOperatore.Viewer, PinHash = PinService.Hash("1111"), ModificatoDa = "Test" };
        RuoloSigilloService.Sigilla(viewer);
        viewer.Ruolo = RuoloOperatore.Master; // manomissione dopo il sigillo.
        var ops = state.Master.Operatori.Load();
        ops.Add(viewer);
        state.Master.Operatori.Save(ops);

        Assert.True(state.ImpostaOperatore("Manomesso", "1111"));
        Assert.Equal(RuoloOperatore.Viewer, state.RuoloAttivo);
        Assert.True(state.EViewer);
        Assert.False(state.EMaster);
    }

    [Fact]
    public void OperatoriViewModel_SegnalaOperatoriConRuoloNonSigillato()
    {
        var state = CreateState();
        AggiungiOperatoreSigillato(state, "Master Onesto", RuoloOperatore.Master, "1234");
        state.ImpostaOperatore("Master Onesto", "1234");

        var manomesso = new Operatore { Nome = "Standard Manomesso", Ruolo = RuoloOperatore.Viewer, PinHash = PinService.Hash("2222"), ModificatoDa = "Test" };
        RuoloSigilloService.Sigilla(manomesso);
        manomesso.Ruolo = RuoloOperatore.Standard;
        var ops = state.Master.Operatori.Load();
        ops.Add(manomesso);
        state.Master.Operatori.Save(ops);
        state.RicaricaTutto();

        var vm = new OperatoriViewModel(state);
        Assert.True(vm.HaOperatoriConRuoloNonSigillato);
        Assert.Contains("Standard Manomesso", vm.OperatoriConRuoloNonSigillato);
        Assert.DoesNotContain("Master Onesto", vm.OperatoriConRuoloNonSigillato);
    }

    [Fact]
    public async Task OperatoriViewModel_Salva_RicalcolaIlSigilloEScriveAudit_QuandoIlRuoloCambia()
    {
        var state = CreateState();
        AggiungiOperatoreSigillato(state, "Master Uno", RuoloOperatore.Master, "1234");
        AggiungiOperatoreSigillato(state, "Standard Due", RuoloOperatore.Standard, "5678");
        state.ImpostaOperatore("Master Uno", "1234");

        var vm = new OperatoriViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(o => o.Nome == "Standard Due");
        riga.Ruolo = RuoloOperatore.Master;
        await vm.SalvaCommand.ExecuteAsync(null);

        var autorevole = state.Master.Operatori.Load().Single(o => o.Nome == "Standard Due");
        Assert.Equal(RuoloOperatore.Master, autorevole.Ruolo);
        Assert.True(autorevole.SigilloRuoloValido);
        Assert.Equal(RuoloOperatore.Master, autorevole.RuoloEffettivo);

        var audit = new SystemAuditService(state.Paths).GetAll(state.Paths);
        Assert.Contains(audit, e => e.Azione == "CambioRuoloOperatore" && e.Target == "Standard Due" && e.Dettagli.Contains("Master"));
    }

    [Fact]
    public void FirstRun_AggiungiOperatore_CreaUnOperatoreSigillatoSubito()
    {
        var state = CreateState();
        var vm = new FirstRunViewModel(state, () => { });
        vm.Cartella = _dir;
        vm.NuovoOperatoreNome = "Primo Operatore";
        vm.NuovoOperatorePin = "1234";

        vm.AggiungiOperatoreCommand.Execute(null);

        var creato = state.Master.Operatori.Load().Single(o => o.Nome == "Primo Operatore");
        Assert.True(creato.SigilloRuoloValido);
        // Bootstrap: il primo operatore mai creato in una cartella diventa Master, ed è sigillato come tale
        // fin dalla creazione (piano 6.4.2, §4b, punto 2: "Il primo operatore creato al primo avvio nasce
        // sigillato").
        Assert.Equal(RuoloOperatore.Master, creato.Ruolo);
        Assert.Equal(RuoloOperatore.Master, creato.RuoloEffettivo);
    }

    [Fact]
    public void SigillaRuoliEsistenti_RisigillaOperatoriNonSigillatiSenzaCambiarneIlRuolo()
    {
        var state = CreateState();
        // Operatore "legacy": scritto direttamente via repository, come lo sarebbe stato prima della 6.4.2
        // (nessun sigillo — è esattamente lo scenario che l'harness tools/data-fix-6.4.2 deve correggere).
        var legacy = new Operatore { Nome = "Legacy Master", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Setup" };
        var ops = state.Master.Operatori.Load();
        ops.Add(legacy);
        state.Master.Operatori.Save(ops);

        var caricatoPrima = state.Master.Operatori.Load().Single(o => o.Nome == "Legacy Master");
        Assert.False(caricatoPrima.SigilloRuoloValido);
        Assert.Equal(RuoloOperatore.Viewer, caricatoPrima.RuoloEffettivo); // migrazione: fail-closed prima della correzione.

        var risigillati = RuoloSigilloService.SigillaRuoliEsistenti(state);

        Assert.Equal(1, risigillati);
        var caricatoDopo = state.Master.Operatori.Load().Single(o => o.Nome == "Legacy Master");
        Assert.True(caricatoDopo.SigilloRuoloValido);
        Assert.Equal(RuoloOperatore.Master, caricatoDopo.Ruolo); // ruolo INVARIATO: nessuna promozione/retrocessione.
        Assert.Equal(RuoloOperatore.Master, caricatoDopo.RuoloEffettivo);

        // Idempotente: una seconda chiamata su una cartella già sigillata non tocca più nulla.
        Assert.Equal(0, RuoloSigilloService.SigillaRuoliEsistenti(state));
    }

    [Fact]
    public void SigilloCopiatoSuAltroOperatore_NonEValido()
    {
        var state = CreateState();
        var master = AggiungiOperatoreSigillato(state, "Master Originale", RuoloOperatore.Master, "1234");

        // Stesso Nome e stesso Ruolo dell'operatore Master, sigillo copiato di peso dal Master — solo l'Uid
        // (generato indipendentemente da new Operatore()) differisce, esattamente lo scenario che la revisione
        // del piano 6.4.2 §4b voleva escludere: "non si può copiare il sigillo fra operatori diversi".
        var clone = new Operatore { Nome = master.Nome, Ruolo = master.Ruolo, SigilloRuolo = master.SigilloRuolo, PinHash = PinService.Hash("9999"), ModificatoDa = "Test" };

        Assert.False(RuoloSigilloService.SigilloRuoloValido(clone)); // senza Uid tecnico
        Assert.NotEqual(MasterEntityIdentityService.GetOrCreate(master), MasterEntityIdentityService.GetOrCreate(clone));
        Assert.False(RuoloSigilloService.SigilloRuoloValido(clone)); // con un Uid tecnico diverso
        Assert.Equal(RuoloOperatore.Viewer, RuoloSigilloService.RuoloEffettivo(clone));
    }

    [Fact]
    public async Task OperatoriViewModel_Salva_RifiutaNomiDuplicati()
    {
        var state = CreateState();
        AggiungiOperatoreSigillato(state, "Master Uno", RuoloOperatore.Master, "1234");
        state.ImpostaOperatore("Master Uno", "1234");

        var vm = new OperatoriViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.NuovoCommand.Execute(null);
        vm.NuovoCommand.Execute(null);
        var nuove = vm.Items.Where(o => string.IsNullOrEmpty(o.Nome)).ToList();
        Assert.Equal(2, nuove.Count);
        nuove[0].Nome = "Mario Rossi";
        nuove[1].Nome = "MARIO ROSSI"; // stesso nome, solo maiuscole diverse: deve comunque essere rifiutato.

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.HaMessaggioErrore);
        Assert.DoesNotContain(state.Master.Operatori.Load(), o => o.Nome == "Mario Rossi" || o.Nome == "MARIO ROSSI");
    }

    [Fact]
    public void FirstRun_AggiungiOperatore_NomeDuplicato_MostraErroreENonCreaUnSecondoOperatore()
    {
        var state = CreateState();
        AggiungiOperatoreSigillato(state, "Master Uno", RuoloOperatore.Master, "1234");

        var vm = new FirstRunViewModel(state, () => { });
        vm.Cartella = _dir;
        vm.NuovoOperatoreNome = "master uno"; // stesso nome esistente, solo maiuscole diverse.
        vm.NuovoOperatorePin = "5555";

        vm.AggiungiOperatoreCommand.Execute(null);

        Assert.True(!string.IsNullOrEmpty(vm.MessaggioErrore));
        Assert.Single(state.Master.Operatori.Load());
    }

    [Fact]
    public void FirstRun_OperatoriPresentiSenzaMasterSigillato_SegnalaLockout()
    {
        var state = CreateState();
        // Nessun Master con sigillo valido: uno Standard onesto e un "Master" manomesso a mano (ricade su
        // Viewer via RuoloEffettivo) — esattamente lo scenario che tools/data-fix-6.4.2 deve correggere.
        AggiungiOperatoreSigillato(state, "Standard Onesto", RuoloOperatore.Standard, "1234");
        var manomesso = new Operatore { Nome = "Master Manomesso", Ruolo = RuoloOperatore.Viewer, PinHash = PinService.Hash("2222"), ModificatoDa = "Test" };
        RuoloSigilloService.Sigilla(manomesso);
        manomesso.Ruolo = RuoloOperatore.Master; // manomissione dopo il sigillo: RuoloEffettivo resta Viewer.
        var ops = state.Master.Operatori.Load();
        ops.Add(manomesso);
        state.Master.Operatori.Save(ops);

        var vm = new FirstRunViewModel(state, () => { });
        vm.Cartella = _dir;
        vm.RicaricaDopoSceltaCommand.Execute(null);

        // 6.6.1 (correzione 1): gli operatori si leggono fuori dal thread UI: si attende, con tetto, l'esito di fondo.
        var scadenza = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!vm.LockoutRuoliNonSigillati && DateTime.UtcNow < scadenza) Thread.Sleep(25);
        Assert.True(vm.LockoutRuoliNonSigillati);
        Assert.True(vm.MostraLockoutRuoliNonSigillati);
    }

    [Fact]
    public void FirstRun_MasterConSigilloValido_NessunLockout()
    {
        var state = CreateState();
        AggiungiOperatoreSigillato(state, "Master Onesto", RuoloOperatore.Master, "1234");

        var vm = new FirstRunViewModel(state, () => { });
        vm.Cartella = _dir;
        vm.RicaricaDopoSceltaCommand.Execute(null);

        Assert.False(vm.LockoutRuoliNonSigillati);
    }

    // ============================================================================ Modalità di accesso

    [Fact]
    public void ImpostazioniViewModel_SalvaModalitaAccesso_SoloSeMaster()
    {
        var state = CreateState();
        AggiungiOperatoreSigillato(state, "Standard Uno", RuoloOperatore.Standard, "1234");
        state.ImpostaOperatore("Standard Uno", "1234");

        var vm = new ImpostazioniViewModel(state, () => { });
        vm.AccessoSenzaPinSelezionato = true;
        vm.SalvaModalitaAccessoCommand.Execute(null);

        // Nessuna scrittura: EMaster è falso, il comando esce subito (stesso schema di SalvaSezioniPolarion).
        Assert.False(File.Exists(state.Paths.AccessoConfigFile));
        Assert.False(state.AccessoSenzaPin);
    }

    [Fact]
    public void ImpostazioniViewModel_Master_SalvaModalitaAccesso_SenzaPin_ConSigilloValido()
    {
        var state = CreateState();
        AggiungiOperatoreSigillato(state, "Master Uno", RuoloOperatore.Master, "1234");
        state.ImpostaOperatore("Master Uno", "1234");

        var vm = new ImpostazioniViewModel(state, () => { });
        vm.AccessoSenzaPinSelezionato = true;
        vm.SalvaModalitaAccessoCommand.Execute(null);

        var config = AccessModeConfigService.Load(state.Paths);
        Assert.True(config.SenzaPin);
        Assert.True(RuoloSigilloService.SigilloModalitaAccessoValido(config));
        state.RicaricaTutto();
        Assert.True(state.AccessoSenzaPin);

        var audit = new SystemAuditService(state.Paths).GetAll(state.Paths);
        Assert.Contains(audit, e => e.Azione == "CambioModalitaAccesso");
    }

    [Fact]
    public void AccessModeConfigService_Save_RifiutaSeOperatoreNonEMasterEffettivo()
    {
        var state = CreateState();
        var config = new AccessModeConfig { SenzaPin = true };
        RuoloSigilloService.SigillaModalitaAccesso(config);

        // Difesa in profondità (revisione piano 6.4.2, §4b, punto 4): il servizio stesso rifiuta, non solo
        // il gate EMaster di ImpostazioniViewModel.SalvaModalitaAccesso che lo precede normalmente.
        Assert.Throws<UnauthorizedAccessException>(() => AccessModeConfigService.Save(state.Paths, config, false));
        Assert.False(File.Exists(state.Paths.AccessoConfigFile));
    }

    [Fact]
    public void ModalitaAccessoManomessa_TornaSempreConPin()
    {
        var state = CreateState();
        AggiungiOperatoreSigillato(state, "Master Uno", RuoloOperatore.Master, "1234");
        state.ImpostaOperatore("Master Uno", "1234");

        var vm = new ImpostazioniViewModel(state, () => { });
        vm.AccessoSenzaPinSelezionato = true;
        vm.SalvaModalitaAccessoCommand.Execute(null);
        Assert.True(state.AccessoSenzaPin);

        // Manomissione diretta del file: SenzaPin resta true ma il sigillo non corrisponde più (lo forziamo a
        // un valore qualunque, come farebbe una modifica a mano con un editor di testo).
        var config = AccessModeConfigService.Load(state.Paths);
        config.Sigillo = "manomesso";
        AccessModeConfigService.Save(state.Paths, config, true);

        state.RicaricaTutto();
        Assert.False(state.AccessoSenzaPin); // ricade su "con PIN", mai su "senza PIN" per un file manomesso.
    }

    [Fact]
    public async Task Login_SenzaPin_NonRichiedeIlPinENonBloccaSulCambioPinObbligatorio()
    {
        var state = CreateState();
        AggiungiOperatoreSigillato(state, "Master Demo", RuoloOperatore.Master, "1234");
        // Account "demo" che normalmente bloccherebbe l'accesso finché non cambia PIN.
        var ops = state.Master.Operatori.Load();
        ops.Single(o => o.Nome == "Master Demo").RichiedeCambioPin = true;
        state.Master.Operatori.Save(ops);

        var config = new AccessModeConfig { SenzaPin = true };
        RuoloSigilloService.SigillaModalitaAccesso(config);
        AccessModeConfigService.Save(state.Paths, config, true);
        state.RicaricaTutto();
        Assert.True(state.AccessoSenzaPin);

        var completamenti = 0;
        var vm = new FirstRunViewModel(state, () => completamenti++);
        vm.Cartella = _dir;
        vm.OperatoreSelezionato = "Master Demo";
        // PinInserito volutamente lasciato vuoto: la modalità senza PIN non deve mai richiederlo.

        await vm.ConfermaCommand.ExecuteAsync(null);

        Assert.Equal(1, completamenti); // login riuscito e completato subito, nessun blocco intermedio.
        Assert.Equal("Master Demo", state.OperatoreCorrente?.Nome);
        Assert.True(state.EMaster);
        Assert.False(vm.RichiedeCambioPin); // il flusso obbligatorio di cambio PIN non blocca l'accesso senza PIN.
    }

    [Fact]
    public void Login_SenzaPin_OperatoreInesistente_Fallisce()
    {
        var state = CreateState();
        AggiungiOperatoreSigillato(state, "Master Uno", RuoloOperatore.Master, "1234");
        Assert.False(state.ImpostaOperatoreSenzaPin("Fantasma"));
        Assert.Null(state.OperatoreCorrente);
    }

    [Fact]
    public void Login_ConPin_ContinuaAFunzionareQuandoLaModalitaERimastaConPin()
    {
        var state = CreateState();
        AggiungiOperatoreSigillato(state, "Master Uno", RuoloOperatore.Master, "1234");
        Assert.False(state.AccessoSenzaPin);
        Assert.True(state.ImpostaOperatore("Master Uno", "1234"));
        Assert.False(state.ImpostaOperatore("Master Uno", "0000"));
    }
}
