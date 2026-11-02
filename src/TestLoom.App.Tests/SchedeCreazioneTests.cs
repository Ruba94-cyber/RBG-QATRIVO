using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Editor a schede solo in creazione (Addendum: audit UX 2026-08-26, punto 1) — vincolo esplicito
/// dell'utente: "sarà solo in compilazione per la creazione ma non per modifica o apertura in visualizzazione
/// perché lì serve la visualizzazione complessiva". Le sei proprietà MostraSchedaX sono l'unico meccanismo che
/// decide cosa si vede: se un test esistente le mostrasse tutte tranne una, il form apparirebbe rotto/incompleto
/// a chi lo apre per consultarlo — la garanzia più importante da testare è quindi che su un test NON nuovo
/// tutte e sei restino sempre vere, qualunque sia SchedaCorrente.</summary>
[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class SchedeCreazioneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_SchedeCreazione_" + Guid.NewGuid().ToString("N"));

    public SchedeCreazioneTests() => SeedDemoDataService.Genera(_dir);

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

    private static bool[] TutteLeSchede(TestCaseEditorViewModel vm) =>
    [
        vm.MostraSchedaPianificazione, vm.MostraSchedaDettagli, vm.MostraSchedaProve,
        vm.MostraSchedaAllegati, vm.MostraSchedaRiferimenti, vm.MostraSchedaRevisioni
    ];

    [Fact]
    public void TestNuovo_SoloLaSchedaCorrenteEVisibile()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { }) { ModoEditorRapido = false };
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);

        Assert.True(vm.IsNew);
        Assert.True(vm.MostraSchedeCreazione);

        // Di default parte dalla prima scheda (Pianificazione, indice 0).
        Assert.Equal(0, vm.SchedaCorrente);
        Assert.Equal(new[] { true, false, false, false, false, false }, TutteLeSchede(vm));

        vm.VaiASchedaCommand.Execute("2");
        Assert.Equal(2, vm.SchedaCorrente);
        Assert.Equal(new[] { false, false, true, false, false, false }, TutteLeSchede(vm));

        // Revisioni (5) non è raggiungibile in creazione (Addendum: audit UX 2026-08-26, correzione
        // post-collaudo — "salva solo nel penultimo form riferimenti"): è strutturalmente sempre vuota per
        // un test nuovo, quindi VaiAScheda tronca a 4 (Riferimenti), l'ultima scheda con contenuto reale.
        vm.VaiASchedaCommand.Execute("5");
        Assert.Equal(4, vm.SchedaCorrente);
        Assert.Equal(new[] { false, false, false, false, true, false }, TutteLeSchede(vm));
    }

    [Fact]
    public async Task TestEsistente_MostraSempreTutteLeSchedeInsieme_IndipendentementeDaSchedaCorrente()
    {
        var state = CreateState();
        // Crea e salva un test vero, poi lo riapre come farebbe l'utente dalla lista.
        var creazione = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { }) { ModoEditorRapido = false };
        creazione.SceglieTemplateCommand.Execute(creazione.TemplatesDisponibili[0]);
        creazione.VeicoloSelezionato = creazione.VeicoliDisponibili[0];
        creazione.AssegnatarioSelezionato = state.Config.OperatoreAttivo;
        creazione.TestCase.Descrizione = "test esistente per verifica schede";
        await creazione.SalvaCommand.ExecuteAsync(null);
        var id = creazione.TestCase.Id;

        var vm = new TestCaseEditorViewModel(state, testCaseId: id, tornaAllaLista: () => { }, navigaATest: _ => { }) { ModoEditorRapido = false };

        Assert.False(vm.IsNew);
        Assert.False(vm.MostraSchedeCreazione);
        Assert.True(vm.MostraBarraSaltaSezione);

        // Tutte vere all'apertura, prima di qualunque interazione.
        Assert.All(TutteLeSchede(vm), Assert.True);

        // E anche forzando SchedaCorrente a un valore qualunque: su un test esistente non deve cambiare nulla,
        // perché la UI non espone nemmeno la striscia schede in questo caso (MostraSchedeCreazione è falso),
        // ma la proprietà C# deve restare corretta comunque se qualcosa la invocasse.
        vm.SchedaCorrente = 3;
        Assert.All(TutteLeSchede(vm), Assert.True);
    }

    [Fact]
    public void VaiAScheda_ConIndiceNonNumerico_NonCambiaNienteENonLancia()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { }) { ModoEditorRapido = false };
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.VaiASchedaCommand.Execute("2");

        vm.VaiASchedaCommand.Execute("non-un-numero");

        Assert.Equal(2, vm.SchedaCorrente); // resta sulla scheda precedente, nessuna eccezione
    }
}
