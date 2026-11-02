using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Riproduzione mirata del punto segnalato dall'utente (feedback utente 2026-08-25, seconda ondata):
/// "aprendo un test in modifica o in visualizzazione non rimane poi salvata la funzionalità per operazione".
/// Crea un test con 2 Funzionalità, imposta la Funzionalità di ogni operazione, salva, poi ricarica il test da
/// zero (nuova istanza di TestCaseEditorViewModel come farebbe l'utente riaprendolo) per verificare se il dato
/// sopravvive davvero al giro completo salva/ricarica.</summary>
[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class FunzionalitaPersistenzaRepro : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_FunzRepro_" + Guid.NewGuid().ToString("N"));

    public FunzionalitaPersistenzaRepro() => SeedDemoDataService.Genera(_dir);

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
    public async Task FunzionalitaPerOperazione_SopravviveASalvataggioERicaricamento()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        // La riproduzione riguarda esclusivamente il salvataggio delle Funzionalità per operazione; il
        // template preseleziona ValidationTool (strumento di supporto), quindi lo deselezioniamo per non introdurre
        // il requisito separato della Funzionalità generale quando il test contiene solo strumenti di supporto.
        foreach (var strumento in vm.StrumentiOptions) strumento.Selezionato = false;
        vm.StatoLavoroSelezionato = StatoLavoroTest.Pianificato;
        vm.AggiungiEcuCommand.Execute("Componente-TEST-BCM");
        vm.AggiungiEcuCommand.Execute("Componente-TEST-SGW");
        vm.VeicoloSelezionato = vm.VeicoliDisponibili[0];
        vm.AssegnatarioSelezionato = state.Config.OperatoreAttivo;
        vm.TestCase.Descrizione = "Repro: funzionalità per operazione dopo salvataggio e ricaricamento (test Pianificato).";

        // Collega 2 Funzionalità al test (obbligatorio scegliere per ogni operazione con 2+).
        vm.NuovaFunzionalita = vm.FunzionalitaDisponibili[0];
        vm.AggiungiFunzionalitaCommand.Execute(null);
        vm.NuovaFunzionalita = vm.FunzionalitaDisponibili[1];
        vm.AggiungiFunzionalitaCommand.Execute(null);
        var funz1 = vm.TestCase.Funzionalita[0];
        var funz2 = vm.TestCase.Funzionalita[1];

        // Esito lasciato DaEseguire (default, 6.4.0): a Pianificazione l'operatore sceglie la Funzionalità ma
        // non ha ancora eseguito nulla, esattamente come nello screenshot dell'utente. Scenario esatto
        // confermato dall'utente: operazione1 = Funzionalita A + solo Componente A, operazione2 = Funzionalita B +
        // solo Componente B (copertura completa di entrambe i componenti e di entrambe le Funzionalità).
        var operazione1 = vm.RevisioneCorrente!.Condizioni[0];
        operazione1.Componente.Clear();
        operazione1.Componente.Add("Componente-TEST-BCM");
        operazione1.Funzionalita = funz1;

        vm.AggiungiCondizioneCommand.Execute(null);
        var operazione2 = vm.RevisioneCorrente.Condizioni[1];
        operazione2.Componente.Clear();
        operazione2.Componente.Add("Componente-TEST-SGW");
        operazione2.Funzionalita = funz2;

        Assert.Equal(2, vm.TestCase.Funzionalita.Count);
        Assert.False(vm.SoloStrumentiSupporto);
        Assert.All(vm.RevisioneCorrente.Condizioni, c => Assert.False(string.IsNullOrWhiteSpace(c.Funzionalita)));

        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErroriValidazione);
        var id = vm.TestCase.Id;

        // Riapertura come farebbe l'utente: nuova istanza del ViewModel che ricarica da AppState/disco.
        var vmRicaricato = new TestCaseEditorViewModel(state, testCaseId: id, tornaAllaLista: () => { }, navigaATest: _ => { });
        Assert.Equal(2, vmRicaricato.RevisioneCorrente!.Condizioni.Count);
        Assert.Equal(funz1, vmRicaricato.RevisioneCorrente.Condizioni[0].Funzionalita);
        Assert.Equal(funz2, vmRicaricato.RevisioneCorrente.Condizioni[1].Funzionalita);
        Assert.Contains(funz1, vmRicaricato.FunzionalitaOperazioniDisponibili);
        Assert.Contains(funz2, vmRicaricato.FunzionalitaOperazioniDisponibili);

        // Anche direttamente da AppState (bypassando il ViewModel), per escludere che sia solo un problema
        // di visualizzazione e non di salvataggio su disco.
        var daDisco = state.TestCases.GetById(id);
        Assert.Equal(funz1, daDisco!.Esecuzioni[0].Condizioni[0].Funzionalita);
        Assert.Equal(funz2, daDisco.Esecuzioni[0].Condizioni[1].Funzionalita);
    }

    [Fact]
    public void FunzionalitaStoricaRimossaDaiDettagli_RestaVisibileNellOperazione()
    {
        var state = CreateState();
        var funzionalitaStorica = state.Master.Funzionalita.Load().First().Nome;
        var test = new TestCase
        {
            Id = "TEST-STORICO-FUNZ",
            CreatoDa = state.Config.OperatoreAttivo,
            ModificatoDa = state.Config.OperatoreAttivo,
            // Simula i dettagli correnti dopo che la Funzionalita e stata scollegata: la fotografia
            // dell'operazione storica deve restare leggibile e selezionata nel ComboBox.
            Funzionalita = new List<string>(),
            Esecuzioni = new List<Esecuzione>
            {
                new()
                {
                    Iterazione = 1,
                    Condizioni = new List<Condizione>
                    {
                        new() { Funzionalita = funzionalitaStorica, Esito = Esito.Conforme }
                    }
                }
            }
        };
        state.TestCases.Save(test, true);

        var vm = new TestCaseEditorViewModel(state, test.Id, () => { }, _ => { });

        Assert.Equal(funzionalitaStorica, vm.RevisioneCorrente!.Condizioni.Single().Funzionalita);
        Assert.Contains(funzionalitaStorica, vm.FunzionalitaOperazioniDisponibili);
    }
}
