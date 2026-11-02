using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata al punto 15 (feedback utente 2026-08-20): campi obbligatori sempre evidenziati
/// con "*", e al salvataggio bloccato per campi mancanti un popup dedicato deve elencarli e permettere di
/// andare direttamente su ciascuno — non solo un banner di testo generico.</summary>
public sealed class CampiMancantiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_CampiMancanti_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Mario Rossi", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Mario Rossi", "1234");
        return state;
    }

    [Fact]
    public void CampoObbligatorio_CampoObbligatorioNelTemplate_RitornaVero()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);

        // Componente è seminato come obbligatorio di default in tutti i template automotive standard.
        Assert.True(vm.EcuObbligatorio);
        Assert.True(vm.CampoObbligatorio(CampiSistema.Componente));
    }

    [Fact]
    public void CampoObbligatorio_SenzaTemplateScelto_RitornaFalso()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        Assert.False(vm.EcuObbligatorio);
    }

    [Fact]
    public async Task Salva_ConCampiMancanti_PopolaCampiObbligatoriMancanti_ConChiaveCorretta()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        // Componente non impostata di proposito.

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.CampiObbligatoriMancanti);
        Assert.Contains(vm.CampiObbligatoriMancanti, c => c.Chiave == CampiSistema.Componente);
    }

    [Fact]
    public async Task Salva_ConCampiMancanti_SollevaRichiestaMostraCampiMancanti()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);

        var sollevato = false;
        vm.RichiestaMostraCampiMancanti += () => sollevato = true;
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(sollevato);
    }

    [Fact]
    public async Task Salva_SenzaCampiMancanti_NonSollevaRichiestaMostraCampiMancanti()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.VeicoloSelezionato = "Daily 24";
        vm.TestCase.Descrizione = "Descrizione completa";
        vm.AssegnatarioSelezionato = "Mario Rossi";
        vm.StatoLavoroSelezionato = StatoLavoroTest.Pianificato; // matricola non obbligatorio finché pianificato
        if (vm.RevisioneCorrente != null) vm.RevisioneCorrente.Matricola = "";

        var sollevato = false;
        vm.RichiestaMostraCampiMancanti += () => sollevato = true;
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(sollevato);
        Assert.Empty(vm.CampiObbligatoriMancanti);
    }

    [Fact]
    public async Task Salva_BloccoPerAltroMotivo_NonPopolaCampiObbligatoriMancanti()
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];
        var tc = new TestCase
        {
            Id = "TC-BLOCCO-01", Componente = "BCM", Veicolo = "Daily 24", TestTemplateUid = template.Uid,
            Descrizione = "Descrizione completa", Funzionalita = new List<string> { "Diagnosi" }, Assegnatario = "Mario Rossi",
            // Componente valorizzata come farebbe EcuAutoSuNuovaOperazione in UI: la Condizione costruita a mano qui
            // deve restare altrimenti valida, cosi' il test isola solo il blocco per EsitoSovrascritto.
            Esecuzioni = new List<Esecuzione> { new() { Iterazione = 1, Condizioni = new List<Condizione> { new() { Esito = Esito.Conforme, Componente = new EcuSelection(new[] { "BCM" }) } } } }
        };
        state.TestCases.Save(tc, isNew: true);

        var vm = new TestCaseEditorViewModel(state, testCaseId: tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.RevisioneCorrente!.EsitoSovrascritto = true; // richiede motivazione, non un campo mancante

        var sollevato = false;
        vm.RichiestaMostraCampiMancanti += () => sollevato = true;
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.ErroriValidazione);
        Assert.Empty(vm.CampiObbligatoriMancanti);
        Assert.False(sollevato);
    }

    [Fact]
    public void CampoObbligatorio_RispettaStatiObbligatorio_DipendenteDaStatoLavoro()
    {
        var state = CreateState();
        var templates = state.Master.TestTemplate.Load();
        var template = templates[0];
        var campoVin = template.Sezioni.SelectMany(s => s.Campi).FirstOrDefault(c => c.CampoSistemaChiave == CampiSistema.Matricola);
        Assert.NotNull(campoVin);
        campoVin!.StatiObbligatorio = new List<StatoLavoroTest> { StatoLavoroTest.Concluso };
        state.Master.TestTemplate.Save(templates);

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili.First(t => t.Uid == template.Uid));

        vm.StatoLavoroSelezionato = StatoLavoroTest.Pianificato;
        Assert.False(vm.VinObbligatorio);

        vm.StatoLavoroSelezionato = StatoLavoroTest.Concluso;
        Assert.True(vm.VinObbligatorio);
    }
}
