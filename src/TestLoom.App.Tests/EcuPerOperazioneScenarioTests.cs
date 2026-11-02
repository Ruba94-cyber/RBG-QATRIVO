using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura end-to-end del punto 9 (feedback utente 2026-08-25) su dati generati realisticamente
/// da SeedDemoDataService, passando dagli stessi comandi che la UI invoca al click (AggiungiEcuCommand,
/// AggiungiCondizioneCommand, SalvaCommand) invece di costruire i TestCase a mano — così un regressione nel
/// collegamento comandi/validazione emerge qui come emergerebbe usando davvero l'app.</summary>
[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class EcuPerOperazioneScenarioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_EcuOperazione_" + Guid.NewGuid().ToString("N"));

    public EcuPerOperazioneScenarioTests() => SeedDemoDataService.Genera(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var operatore = state.Master.Operatori.Load().First(o => o.Ruolo == RuoloOperatore.Master);
        state.ImpostaOperatore(operatore.Nome, "1234");
        return state;
    }

    [Fact]
    public void NuovoTestMultiEcu_OperazioneNasceConTutteLeEcuPreselezionate()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("Componente-TEST-BCM");
        vm.AggiungiEcuCommand.Execute("Componente-TEST-SGW");

        // La prima operazione esiste già dal wizard, creata prima che i componenti fossero scelte: deve essersi
        // riallineata a entrambe non appena le due componente sono state aggiunte (RiallineaEcuOperazioniDopoCambioElenco).
        var operazioneIniziale = Assert.Single(vm.RevisioneCorrente!.Condizioni);
        Assert.Equal(new[] { "Componente-TEST-BCM", "Componente-TEST-SGW" }, operazioneIniziale.Componente);

        vm.AggiungiCondizioneCommand.Execute(null);
        var nuovaOperazione = vm.RevisioneCorrente.Condizioni[1];
        Assert.Equal(new[] { "Componente-TEST-BCM", "Componente-TEST-SGW" }, nuovaOperazione.Componente);
    }

    [Fact]
    public async Task NuovoTestMultiEcu_SalvaBloccatoSeUnaEcuNonECopertaDaNessunaOperazione()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("Componente-TEST-BCM");
        vm.AggiungiEcuCommand.Execute("Componente-TEST-SGW");
        vm.VeicoloSelezionato = vm.VeicoliDisponibili[0];
        vm.AssegnatarioSelezionato = state.Config.OperatoreAttivo;
        vm.TestCase.Descrizione = "Scenario reale: due componente, una sola operazione ristretta a una sola componente.";

        // L'operatore restringe manualmente l'unica operazione alla sola BCM (come farebbe dal flyout componente
        // nella riga dell'operazione): SGW resta senza nessuna operazione che la copra.
        var operazione = vm.RevisioneCorrente!.Condizioni[0];
        operazione.Componente.Clear();
        operazione.Componente.Add("Componente-TEST-BCM");
        operazione.Esito = Esito.Conforme;

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Contains(vm.ErroriValidazione, e => e.Contains("Componente-TEST-SGW"));
    }

    [Fact]
    public async Task NuovoTestMultiEcu_SalvaRiesceQuandoOgniEcuECopertaDaAlmenoUnOperazione_ECoverageServiceLeggeSoloLOperazionePertinente()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("Componente-TEST-BCM");
        vm.AggiungiEcuCommand.Execute("Componente-TEST-SGW");
        vm.VeicoloSelezionato = vm.VeicoliDisponibili[0];
        vm.AssegnatarioSelezionato = state.Config.OperatoreAttivo;
        vm.TestCase.Descrizione = "Scenario reale: due componente, ciascuna con la sua operazione ed esito diverso.";
        // Il template preseleziona ValidationTool come strumento di supporto; esplicita la Funzionalità generale.
        vm.NuovaFunzionalita = vm.FunzionalitaDisponibili[0];
        vm.AggiungiFunzionalitaCommand.Execute(null);

        // Operazione 1 -> solo BCM, Non Conforme. Operazione 2 -> solo SGW, Conforme (comportamenti
        // effettivamente diversi lato UnitLogger tra le due componente, punto 8 della grigliata).
        var operazione1 = vm.RevisioneCorrente!.Condizioni[0];
        operazione1.Componente.Clear();
        operazione1.Componente.Add("Componente-TEST-BCM");
        operazione1.Esito = Esito.NonConforme;
        operazione1.DescrizioneProblema = "Risposta diagnostica fuori tolleranza (scenario reale di test).";

        vm.AggiungiCondizioneCommand.Execute(null);
        var operazione2 = vm.RevisioneCorrente.Condizioni[1];
        operazione2.Componente.Clear();
        operazione2.Componente.Add("Componente-TEST-SGW");
        operazione2.Esito = Esito.Conforme;

        // Un esito Non Conforme genera una segnalazione da classificare (comportamento reale dell'app, non
        // legato al punto 9): il primo tentativo di salvataggio la crea e blocca finché non è classificata,
        // stesso schema di AnomalieIntegrateNelTestTests.
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.True(vm.HaSegnalazioniTest);
        var segnalazione = Assert.Single(vm.SegnalazioniTest);
        segnalazione.Severita = SeveritaAnomalia.High;
        vm.CreaAnomaliaDaTestCommand.Execute(segnalazione);

        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErroriValidazione);

        var salvato = state.TestCases.GetById(vm.TestCase.Id);
        Assert.NotNull(salvato);

        // CoverageService.PerEcu deve leggere l'esito SOLO dalle operazioni taggate con quella componente: BCM
        // risulta non conforme, SGW conforme, anche se condividono lo stesso test/esecuzione.
        var bucketBcm = CoverageService.PerEcu(new[] { salvato! }, "Componente-TEST-BCM");
        Assert.Equal(1, bucketBcm.NonConformi);
        Assert.Equal(0, bucketBcm.Conformi);

        var bucketSgw = CoverageService.PerEcu(new[] { salvato! }, "Componente-TEST-SGW");
        Assert.Equal(1, bucketSgw.Conformi);
        Assert.Equal(0, bucketSgw.NonConformi);
    }

    [Fact]
    public void RimuoviEcuDalTest_TogliePureLaScelaPendenteDalleOperazioni()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("Componente-TEST-BCM");
        vm.AggiungiEcuCommand.Execute("Componente-TEST-SGW");
        Assert.Equal(new[] { "Componente-TEST-BCM", "Componente-TEST-SGW" }, vm.RevisioneCorrente!.Condizioni[0].Componente);

        vm.RimuoviEcuCommand.Execute("Componente-TEST-SGW");

        Assert.Equal(new[] { "Componente-TEST-BCM" }, vm.RevisioneCorrente.Condizioni[0].Componente);
    }
}
