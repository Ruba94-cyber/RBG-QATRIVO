using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copre il feedback utente 2026-08-21: la regola combobox-pura (&lt;=10)/ricerca (&gt;10) deve valere
/// per qualunque lista dell'app, non solo per i campi Lista degli strumenti. Qui la parte non coperta dai test
/// sui converter (ListaSogliaConverterTests): la ricerca inline sopra l'elenco Strumenti usati, che filtra le
/// checkbox visibili invece di aprire un dialogo (per non perdere i campi annidati per-strumento).</summary>
[Collection("Localization")]
public sealed class ListaSogliaViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ListaSogliaVm_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStatoConStrumenti(int numero)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var strumenti = Enumerable.Range(1, numero).Select(i => new StrumentoDiagnostico { Nome = $"Strumento{i:D2}", Tipo = TipoStrumento.DaTestare }).ToList();
        state.Master.Strumenti.Save(strumenti);
        return state;
    }

    [Fact]
    public void MostraRicercaStrumenti_FalsoFinoA10Strumenti()
    {
        var state = CreaStatoConStrumenti(10);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        Assert.False(vm.MostraRicercaStrumenti);
        Assert.Equal(10, vm.StrumentiOptions.Count);
    }

    [Fact]
    public void MostraRicercaStrumenti_VeroOltre10Strumenti()
    {
        var state = CreaStatoConStrumenti(11);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        Assert.True(vm.MostraRicercaStrumenti);
        Assert.Equal(11, vm.StrumentiOptions.Count);
    }

    /// <summary>Con ≤10 strumenti la view resta la WrapPanel inline invariata: la ricerca (nascosta di default,
    /// ma raggiungibile se il VM riceve comunque un filtro) continua a pilotare Visibile voce per voce, come
    /// prima della lista virtualizzata.</summary>
    [Fact]
    public void FiltroStrumenti_FinoA10_ContinuaAPilotareVisibile()
    {
        var state = CreaStatoConStrumenti(10);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        vm.FiltroStrumenti = "Strumento01";

        var visibili = vm.StrumentiOptions.Where(o => o.Visibile).ToList();
        Assert.Single(visibili);
        Assert.Equal("Strumento01", visibili[0].Label);

        vm.FiltroStrumenti = "";
        Assert.All(vm.StrumentiOptions, o => Assert.True(o.Visibile));
    }

    /// <summary>Oltre 10 strumenti (perf 2026-09-25 bis, lista virtualizzata): la ricerca filtra
    /// StrumentiOptionsFiltrate su TUTTE le voci del modello (mai troncata), non più Visibile — la ListBox lega
    /// direttamente questa collezione filtrata, quindi non serve più materializzare/nascondere ogni riga.</summary>
    [Fact]
    public void FiltroStrumenti_Oltre10_FiltraStrumentiOptionsFiltrateSuTutteLeVoci()
    {
        var state = CreaStatoConStrumenti(11);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        vm.FiltroStrumenti = "Strumento01";

        var filtrati = vm.StrumentiOptionsFiltrate;
        Assert.Single(filtrati);
        Assert.Equal("Strumento01", filtrati[0].Label);
        // Le altre 10 restano nel modello (mai troncate): tornano raggiungibili cancellando il filtro.
        Assert.Equal(11, vm.StrumentiOptions.Count);

        vm.FiltroStrumenti = "";
        Assert.Equal(11, vm.StrumentiOptionsFiltrate.Count);
    }

    /// <summary>Selezione preservata attraverso i filtri (oltre 10 strumenti): una voce spuntata resta
    /// selezionata anche quando il filtro la nasconde da StrumentiOptionsFiltrate, e ricompare spuntata appena
    /// il filtro viene cancellato o allargato.</summary>
    [Fact]
    public void Selezione_PreservataAttraversoIFiltri_Oltre10Strumenti()
    {
        var state = CreaStatoConStrumenti(11);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        var scelto = vm.StrumentiOptions.First(o => o.Label == "Strumento05");
        scelto.Selezionato = true;

        vm.FiltroStrumenti = "Strumento01"; // nasconde Strumento05 dalla lista filtrata
        Assert.DoesNotContain(vm.StrumentiOptionsFiltrate, o => o.Label == "Strumento05");
        Assert.True(scelto.Selezionato); // ma resta selezionato nel modello

        vm.FiltroStrumenti = "";
        Assert.Contains(vm.StrumentiOptionsFiltrate, o => o.Label == "Strumento05" && o.Selezionato);
    }

    /// <summary>Riepilogo sopra la lista sempre coerente coi selezionati, anche se una voce è nascosta dal
    /// filtro corrente o fuori dallo scroll — è l'unico modo in cui l'utente vede la selezione senza dover
    /// scorrere/cercare ogni voce.</summary>
    [Fact]
    public void StrumentiSelezionatiRiepilogo_MostraSempreISelezionatiAncheSeNascostiDalFiltro()
    {
        var state = CreaStatoConStrumenti(11);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        Assert.Equal("Nessuno strumento selezionato.", vm.StrumentiSelezionatiRiepilogo);

        vm.StrumentiOptions.First(o => o.Label == "Strumento03").Selezionato = true;
        Assert.Contains("Strumento03", vm.StrumentiSelezionatiRiepilogo);
        Assert.Contains("1", vm.StrumentiSelezionatiRiepilogo);

        vm.FiltroStrumenti = "Strumento09"; // nasconde Strumento03 dalla lista, non dal riepilogo
        Assert.Contains("Strumento03", vm.StrumentiSelezionatiRiepilogo);
    }

    /// <summary>Salvataggio invariato: TestCase.StrumentiUsati riflette la selezione nel modello anche quando è
    /// stata fatta con un filtro attivo (StrumentiOptionsFiltrate ristretta), perché la selezione vive sempre
    /// in StrumentiOptions, mai nella vista filtrata.</summary>
    [Fact]
    [Trait("Category", "Lento")]
    public async Task Salvataggio_StrumentiUsati_RiflettonoSelezioneAncheConFiltroAttivo()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(_dir);
        var operatore = state.Master.Operatori.Load().First(o => o.Ruolo == RuoloOperatore.Master);
        Assert.True(state.ImpostaOperatore(operatore.Nome, "1234"));
        var strumenti = Enumerable.Range(1, 11).Select(i => new StrumentoDiagnostico { Nome = $"Strumento{i:D2}", Tipo = TipoStrumento.DaTestare }).ToList();
        state.Master.Strumenti.Save(strumenti);

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.VeicoloSelezionato = vm.VeicoliDisponibili[0];
        vm.AssegnatarioSelezionato = state.Config.OperatoreAttivo;
        vm.TestCase.Descrizione = "prova salvataggio strumenti";
        if (vm.EcuDisponibili.Count > 0) vm.AggiungiEcuCommand.Execute(vm.EcuDisponibili[0]);

        vm.FiltroStrumenti = "Strumento07";
        vm.StrumentiOptionsFiltrate.Single(o => o.Label == "Strumento07").Selezionato = true;
        vm.FiltroStrumenti = "";

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Empty(vm.ErroriValidazione);
        Assert.Contains("Strumento07", vm.TestCase.StrumentiUsati);
    }
}
