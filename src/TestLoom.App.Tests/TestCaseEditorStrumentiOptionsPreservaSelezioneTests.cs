using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Bug segnalato dalla review del commit 9b757f2: CaricaStrumenti() ricostruiva sempre StrumentiOptions
/// leggendo Selezionato da TestCase.StrumentiUsati, che si aggiorna solo al salvataggio del Test. Ogni
/// richiamo di CaricaStrumenti prima di un Salva (AggiungiStrumentoRapido dopo aver creato lo strumento,
/// SalvaCampiStrumento dopo aver salvato i campi di uno strumento esistente) perdeva quindi le spunte fatte
/// dall'operatore ma non ancora salvate. Fix: se StrumentiOptions esiste già, la selezione di partenza per
/// ogni voce è quella corrente in memoria, non quella persistita.</summary>
[Collection("Localization")]
public sealed class TestCaseEditorStrumentiOptionsPreservaSelezioneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_StrumentiOptionsPreserva_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private AppState CreaStatoMaster()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Operatori.Save(new List<Operatore> { TestOperatori.Sigilla(new() { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") }) });
        Assert.True(state.ImpostaOperatore("Master Prova", "1234"));
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = "A", Tipo = TipoStrumento.DaTestare },
            new() { Nome = "B", Tipo = TipoStrumento.DaTestare },
        });
        return state;
    }

    [Fact]
    public void AggiungiStrumentoRapido_PreservaLeSpuntePrecedentiNonAncoraSalvate()
    {
        var state = CreaStatoMaster();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        Assert.True(vm.PuoGestireStrumenti);

        vm.StrumentiOptions.Single(o => o.Valore == "A").Selezionato = true;
        vm.StrumentiOptions.Single(o => o.Valore == "B").Selezionato = true;

        vm.NuovoStrumentoNome = "C";
        vm.AggiungiStrumentoRapidoCommand.Execute(null);

        var selezionati = vm.StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Valore).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "A", "B", "C" }, selezionati);
    }

    [Fact]
    public void SalvaCampiStrumento_PreservaLeSpuntePrecedentiNonAncoraSalvate()
    {
        var state = CreaStatoMaster();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        vm.StrumentiOptions.Single(o => o.Valore == "A").Selezionato = true;
        vm.StrumentiOptions.Single(o => o.Valore == "B").Selezionato = true;

        // Simula la modifica di un campo dello strumento dalla tabella master (Addendum §8): non tocca
        // affatto la selezione, ma richiama CaricaStrumenti al pari di AggiungiStrumentoRapido.
        vm.SalvaCampiStrumento();

        var selezionati = vm.StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Valore).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "A", "B" }, selezionati);
    }

    [Fact]
    public async Task SelezioneStrumento_ModificaIlFlagNonSalvatoSoloQuandoIlCambioRestaAccettato()
    {
        var state = CreaStatoMaster();
        var strumentiSupporto = state.Master.Strumenti.Load();
        foreach (var strumento in strumentiSupporto) strumento.Tipo = TipoStrumento.Supporto;
        state.Master.Strumenti.Save(strumentiSupporto);
        var tc = new TestCase
        {
            Id = "TC-STR-DIRTY",
            StrumentiUsati = new() { "B" },
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
            Esecuzioni = new() { new Esecuzione { Iterazione = 1, Data = DateTime.Now } }
        };
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { }) { InModifica = true };

        vm.StrumentiOptions.Single(o => o.Valore == "A").Selezionato = true;
        Assert.True(vm.ModificheNonSalvate);

        vm.ModificheNonSalvate = false;
        var strumentoSalvato = vm.StrumentiOptions.Single(o => o.Valore == "B");
        strumentoSalvato.Selezionato = false;
        await vm.DeselezioneDtcInCorso;
        Assert.True(vm.ModificheNonSalvate);
    }

    /// <summary>Il comportamento persistito resta invariato: la selezione preservata in memoria diventa
    /// autorevole solo al Salva del Test, esattamente come prima di questa modifica.</summary>
    [Fact]
    public async Task SelezionePreservataInMemoria_DiventaAutorevoleSoloAlSalvataggioDelTest()
    {
        var state = CreaStatoMaster();
        var condizione = new Condizione { Esito = Esito.Conforme, Nota = "OK", Componente = "BCM" };
        var tc = new TestCase
        {
            Id = "TC-STR-1", Componente = "BCM", Assegnatario = "Tester", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now,
            Esecuzioni = new() { new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.Conforme, Condizioni = new() { condizione } } }
        };
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });

        vm.StrumentiOptions.Single(o => o.Valore == "A").Selezionato = true;
        vm.NuovoStrumentoNome = "C";
        vm.AggiungiStrumentoRapidoCommand.Execute(null);
        Assert.DoesNotContain("A", vm.TestCase.StrumentiUsati);

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Empty(vm.ErroriValidazione);
        Assert.Contains("A", vm.TestCase.StrumentiUsati);
        Assert.Contains("C", vm.TestCase.StrumentiUsati);
    }
}
