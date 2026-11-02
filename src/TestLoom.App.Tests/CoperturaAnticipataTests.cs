using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Punto 8 ridotto dell'audit UX 2026-08-26: la copertura componente/Funzionalità mancante deve comparire
/// mentre si compila (EcuNonCoperte/FunzionalitaNonCoperte/HaCoperturaIncompleta), non solo come errore al
/// momento di Salvare — stessa identica regola di Valida() (ValEcuSenzaOperazioneFmt/ValFunzionalitaSenzaOperazioneFmt),
/// solo esposta prima. Ogni test verifica che le due viste — l'anticipo qui e il blocco reale in Salva() —
/// restino d'accordo, non solo che la proprietà anticipata esista.</summary>
[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class CoperturaAnticipataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_CoperturaAnticipata_" + Guid.NewGuid().ToString("N"));

    public CoperturaAnticipataTests() => SeedDemoDataService.Genera(_dir);

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
    public void FunzionalitaNonCoperta_CompareSubito_PrimaDelSalvataggio()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        // Template senza strumenti DaTestare preselezionati. Con il template "UnitLogger" (strumento UnitLogger = DaTestare) l'editor crea
        // da solo un'operazione per ogni Funzionalita (allineamento documentato in OperazioniStrumentoRifinitureTests): la
        // copertura risulterebbe completa gia' dopo la seconda Funzionalita e l'asserzione sotto non varrebbe piu'.
        // Non si usa TemplatesDisponibili[0]: l'ordine dei template non e' stabile (UID casuali nel journal Phase D).
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili.Single(t => t.Nome == "Validazione Banco"));
        vm.VeicoloSelezionato = vm.VeicoliDisponibili[0];
        vm.AssegnatarioSelezionato = state.Config.OperatoreAttivo;

        vm.NuovaFunzionalita = vm.FunzionalitaDisponibili[0];
        vm.AggiungiFunzionalitaCommand.Execute(null);
        // Con una sola Funzionalità collegata, FunzionalitaAutoSeSingola riallinea subito l'unica operazione
        // del wizard a quel valore (RiallineaFunzionalitaOperazioniDopoCambioElenco) — comportamento esistente,
        // non introdotto da questo intervento: la prima Funzionalità è quindi già coperta a questo punto.
        var funz1 = vm.TestCase.Funzionalita[0];
        Assert.Equal(funz1, vm.RevisioneCorrente!.Condizioni[0].Funzionalita);
        Assert.False(vm.HaCoperturaIncompleta);

        vm.NuovaFunzionalita = vm.FunzionalitaDisponibili[1];
        vm.AggiungiFunzionalitaCommand.Execute(null);
        var funz2 = vm.TestCase.Funzionalita[1];

        // Con la seconda Funzionalità collegata, il riallineamento automatico non si applica più (non è più
        // "singola"): la nuova Funzionalità non è coperta da nessuna operazione, e questo deve comparire QUI,
        // PRIMA di premere Salva — è esattamente il caso che il punto 8 dell'audit voleva anticipare.
        Assert.True(vm.HaCoperturaIncompleta);
        Assert.Equal(new[] { funz2 }, vm.FunzionalitaNonCoperte);

        // Una singola operazione copre una sola Funzionalità alla volta: riassegnarla da funz1 a funz2
        // copre la seconda ma scopre la prima — la lista anticipata deve riflettere il cambio subito, non
        // restare ferma alla situazione precedente.
        vm.RevisioneCorrente.Condizioni[0].Funzionalita = funz2;
        Assert.True(vm.HaCoperturaIncompleta);
        Assert.Equal(new[] { funz1 }, vm.FunzionalitaNonCoperte);

        // Aggiungo una seconda operazione per coprire anche funz1: ora la lista si svuota davvero.
        vm.AggiungiCondizioneCommand.Execute(null);
        vm.RevisioneCorrente.Condizioni[1].Funzionalita = funz1;
        Assert.False(vm.HaCoperturaIncompleta);
        Assert.Empty(vm.FunzionalitaNonCoperte);
    }

    [Fact]
    public async Task FunzionalitaNonCoperta_SiSvuotaAppenaTutteCoperte_EIlSalvataggioNonEPiuBloccato()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.VeicoloSelezionato = vm.VeicoliDisponibili[0];
        vm.AssegnatarioSelezionato = state.Config.OperatoreAttivo;
        vm.TestCase.Descrizione = "copertura completa";

        vm.NuovaFunzionalita = vm.FunzionalitaDisponibili[0];
        vm.AggiungiFunzionalitaCommand.Execute(null);
        vm.NuovaFunzionalita = vm.FunzionalitaDisponibili[1];
        vm.AggiungiFunzionalitaCommand.Execute(null);

        vm.RevisioneCorrente!.Condizioni[0].Funzionalita = vm.TestCase.Funzionalita[0];
        vm.AggiungiCondizioneCommand.Execute(null);
        vm.RevisioneCorrente.Condizioni[1].Funzionalita = vm.TestCase.Funzionalita[1];

        Assert.False(vm.HaCoperturaIncompleta);
        Assert.Empty(vm.FunzionalitaNonCoperte);

        await AssertSalvataggioNonBloccatoDaCopertura(vm);
    }

    private static async Task AssertSalvataggioNonBloccatoDaCopertura(TestCaseEditorViewModel vm)
    {
        // La lista anticipata concorda con Salva(): con copertura completa, ErroriValidazione non deve
        // contenere il motivo "senza operazione" — non testiamo l'intero salvataggio (richiede allegati/campi
        // obbligatori del template demo), solo che questa specifica regola non blocchi più.
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.ErroriValidazione, e => e.Contains("Funzionalità collegata", StringComparison.OrdinalIgnoreCase) || e.Contains("Functionality linked", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EcuNonCoperta_CompareSubitoEConcordaConValida()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("Componente-TEST-BCM");
        vm.AggiungiEcuCommand.Execute("Componente-TEST-SGW");
        vm.VeicoloSelezionato = vm.VeicoliDisponibili[0];
        vm.AssegnatarioSelezionato = state.Config.OperatoreAttivo;
        vm.TestCase.Descrizione = "copertura componente";

        // Restringo la prima (e unica) operazione a una sola delle due componente del test: l'altra resta scoperta.
        vm.RevisioneCorrente!.Condizioni[0].Componente.Clear();
        vm.RevisioneCorrente.Condizioni[0].Componente.Add("Componente-TEST-BCM");

        Assert.True(vm.HaCoperturaIncompleta);
        Assert.Equal(new[] { "Componente-TEST-SGW" }, vm.EcuNonCoperte);

        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Contains(vm.ErroriValidazione, e => e.Contains("Componente-TEST-SGW"));
    }

    [Fact]
    public void NessunaEcuOFunzionalitaCollegata_NonSegnalaNienteDiFalso()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);

        // Nessuna componente/Funzionalità ancora collegata al test: le due liste non devono lamentarsi di un
        // insieme vuoto (TestCase.Componente.Count==0 e FunzionalitaOperazioneObbligatoria==false con 0/1 collegate).
        Assert.False(vm.HaCoperturaIncompleta);
        Assert.Empty(vm.EcuNonCoperte);
        Assert.Empty(vm.FunzionalitaNonCoperte);
    }
}
