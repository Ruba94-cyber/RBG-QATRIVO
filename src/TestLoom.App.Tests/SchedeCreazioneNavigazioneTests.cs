using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Correzioni post-collaudo sull'editor a schede (Addendum: audit UX 2026-08-26): (1) la nota di
/// modifica pianificazione non ha senso su un test nuovo, dove non può mai diventare obbligatoria; (2)
/// Avanti/Indietro per muoversi tra le schede in sequenza; (3) Salva compare solo sull'ultima scheda con
/// contenuto reale (Riferimenti), non su ognuna — su un test esistente nessuno di questi tre comportamenti
/// deve cambiare.</summary>
[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class SchedeCreazioneNavigazioneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_SchedeNav_" + Guid.NewGuid().ToString("N"));

    public SchedeCreazioneNavigazioneTests() => SeedDemoDataService.Genera(_dir);

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
    public void TestNuovo_NotaPianificazione_NonECompilata()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { }) { ModoEditorRapido = false };
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);

        // PuoAssegnare (Master) è vero, ma su un test nuovo il campo non deve comunque comparire: senza
        // _snapshot, pianificazioneCambiata in Valida() è strutturalmente sempre falsa.
        Assert.True(vm.PuoAssegnare);
        Assert.False(vm.MostraNotaPianificazione);
    }

    [Fact]
    public async Task TestEsistente_ConUnMaster_MostraLaNotaPianificazione()
    {
        var state = CreateState();
        var creazione = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { }) { ModoEditorRapido = false };
        creazione.SceglieTemplateCommand.Execute(creazione.TemplatesDisponibili[0]);
        creazione.AggiungiEcuCommand.Execute("Componente-TEST-BCM");
        creazione.VeicoloSelezionato = creazione.VeicoliDisponibili[0];
        creazione.AssegnatarioSelezionato = state.Config.OperatoreAttivo;
        creazione.TestCase.Descrizione = "test per verifica nota pianificazione";
        // Il template preseleziona ValidationTool, classificato come strumento di supporto: il test deve comunque
        // dichiarare una Funzionalità generale nel riquadro Dettagli.
        creazione.NuovaFunzionalita = creazione.FunzionalitaDisponibili[0];
        creazione.AggiungiFunzionalitaCommand.Execute(null);
        await creazione.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(creazione.ErroriValidazione);

        var vm = new TestCaseEditorViewModel(state, creazione.TestCase.Id, tornaAllaLista: () => { }, navigaATest: _ => { }) { ModoEditorRapido = false };

        Assert.False(vm.IsNew);
        Assert.True(vm.MostraNotaPianificazione);
    }

    [Fact]
    public void TestNuovo_SalvaCompareSoloSullUltimaSchedaConContenutoReale()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { }) { ModoEditorRapido = false };
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);

        // Schede 0-3 (Pianificazione, Dettagli, Prove, Allegati): Salva non ancora visibile.
        for (var i = 0; i <= 3; i++)
        {
            vm.VaiASchedaCommand.Execute(i.ToString());
            Assert.False(vm.MostraAzioniModifica);
        }

        // Scheda 4 (Riferimenti, l'ultima con contenuto reale): Salva compare.
        vm.VaiASchedaCommand.Execute("4");
        Assert.True(vm.MostraAzioniModifica);
    }

    [Fact]
    public void TestNuovo_VaiAScheda_NonRaggiungeMaiRevisioni_AncheSeRichiestaEsplicitamente()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { }) { ModoEditorRapido = false };
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);

        // Anche chiedendo esplicitamente la scheda 5 (Revisioni, non selezionabile dalla striscia in
        // creazione), il ViewModel non deve mai arrivarci: resta bloccato sull'ultima scheda reale.
        vm.VaiASchedaCommand.Execute("5");

        Assert.Equal(4, vm.SchedaCorrente);
        Assert.True(vm.MostraAzioniModifica); // conseguenza corretta: siamo sull'ultima scheda vera
    }

    [Fact]
    public async Task TestEsistente_MostraAzioniModifica_IgnoraSchedaCorrente()
    {
        var state = CreateState();
        var creazione = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { }) { ModoEditorRapido = false };
        creazione.SceglieTemplateCommand.Execute(creazione.TemplatesDisponibili[0]);
        creazione.AggiungiEcuCommand.Execute("Componente-TEST-BCM");
        creazione.VeicoloSelezionato = creazione.VeicoliDisponibili[0];
        creazione.AssegnatarioSelezionato = state.Config.OperatoreAttivo;
        creazione.TestCase.Descrizione = "test per verifica salva su esistente";
        creazione.NuovaFunzionalita = creazione.FunzionalitaDisponibili[0];
        creazione.AggiungiFunzionalitaCommand.Execute(null);
        await creazione.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(creazione.ErroriValidazione);

        var vm = new TestCaseEditorViewModel(state, creazione.TestCase.Id, tornaAllaLista: () => { }, navigaATest: _ => { }) { ModoEditorRapido = false };
        vm.IniziaModificaCommand.AttendiModifica();

        // Su un test esistente Salva resta sempre visibile durante la modifica, qualunque sia SchedaCorrente
        // (che qui non pilota nemmeno la UI, vedi MostraBarraSaltaSezione/MostraSchedeCreazione).
        Assert.True(vm.MostraAzioniModifica);
        vm.SchedaCorrente = 0;
        Assert.True(vm.MostraAzioniModifica);
    }

    [Fact]
    public void AvantiIndietro_MuovonoSequenzialmenteERispettanoIConfini()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { }) { ModoEditorRapido = false };
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);

        Assert.Equal(0, vm.SchedaCorrente);
        Assert.False(vm.SchedaIndietroCommand.CanExecute(null));
        Assert.True(vm.SchedaAvantiCommand.CanExecute(null));

        vm.SchedaAvantiCommand.Execute(null);
        vm.SchedaAvantiCommand.Execute(null);
        Assert.Equal(2, vm.SchedaCorrente);

        vm.SchedaIndietroCommand.Execute(null);
        Assert.Equal(1, vm.SchedaCorrente);

        // Avanti fino in fondo (scheda 4): lì Avanti si disabilita, Indietro resta disponibile.
        vm.SchedaAvantiCommand.Execute(null);
        vm.SchedaAvantiCommand.Execute(null);
        vm.SchedaAvantiCommand.Execute(null);
        Assert.Equal(4, vm.SchedaCorrente);
        Assert.False(vm.SchedaAvantiCommand.CanExecute(null));
        Assert.True(vm.SchedaIndietroCommand.CanExecute(null));

        // Un ulteriore tentativo di andare avanti non supera il confine (nessuna eccezione, resta a 4).
        vm.SchedaAvantiCommand.Execute(null);
        Assert.Equal(4, vm.SchedaCorrente);
    }
}
