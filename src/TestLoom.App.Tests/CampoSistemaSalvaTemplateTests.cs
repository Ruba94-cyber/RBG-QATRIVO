using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Regressione (FlaUI CampiSistemaFiltro670UiTests, 2026-10-13): un campo di sistema creato in Gestisci campi,
/// aggiunto al template DOPO che il Test esisteva, deve poter essere valorizzato e salvato dall'editor del Test. Su un Test
/// Pianificato (nessuna revisione) il Salva lanciava "Index was out of range": il ramo di creazione della revisione
/// indicizzava Esecuzioni[^1] senza verificare che la lista non fosse vuota.</summary>
[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class CampoSistemaSalvaTemplateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_CampoSistemaSalva_" + Guid.NewGuid().ToString("N"));
    private const string Chiave = "area-prova";
    private const string Etichetta = "Area Prova";

    public void Dispose()
    {
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

    /// <summary>Salva il Test prima del campo, poi crea il campo nel catalogo e lo aggiunge al template nel gruppo indicato.</summary>
    private (AppState state, TestCase tc) Prepara(bool conRevisione, bool pianificazione, TipoCampoStrumento tipo = TipoCampoStrumento.Testo, List<string>? opzioni = null)
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];
        var tc = new TestCase
        {
            Id = "TC-CS-SALVA", Componente = "BCM", Veicolo = "Daily 24", TestTemplateUid = template.Uid,
            Descrizione = "Descrizione completa", Funzionalita = new List<string> { "Diagnosi" }, Assegnatario = "Mario Rossi",
        };
        if (conRevisione)
            tc.Esecuzioni = new List<Esecuzione> { new() { Iterazione = 1, Condizioni = new List<Condizione> { new() { Esito = Esito.Conforme, Componente = new EcuSelection(new[] { "BCM" }) } } } };
        state.TestCases.Save(tc, isNew: true);

        var gruppo = pianificazione ? GruppoCampoSistema.Pianificazione : GruppoCampoSistema.Dettagli;
        var repo = pianificazione ? state.Master.CampiSistemaPianificazione : state.Master.CampiSistemaDettagli;
        var catalogo = repo.Load().ToList();
        catalogo.Add(new CampoSistemaDefinizione { Chiave = Chiave, Etichetta = Etichetta, Gruppo = gruppo, Tipo = tipo, OpzioniLista = opzioni ?? new List<string>(), Ruolo = RuoloCampoSistema.Nessuno, UsabileComeFiltro = true });
        repo.Save(catalogo);

        // Il campo entra nel template dopo la creazione del Test: il Test non ha alcun valore per questa chiave.
        var templates = state.Master.TestTemplate.Load().ToList();
        var tpl = templates.First(t => t.Uid == template.Uid);
        var campo = new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = Chiave, Nome = Etichetta };
        if (pianificazione) tpl.CampiPianificazione.Add(campo);
        else tpl.Sezioni[0].Campi.Add(campo);
        state.Master.TestTemplate.Save(templates);
        return (state, tc);
    }

    private static async Task<TestCaseEditorViewModel> ValorizzaESalva(AppState state, TestCase tc, string valore, bool pianificazione)
    {
        var vm = new TestCaseEditorViewModel(state, testCaseId: tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.InModifica = true;
        var raccolta = pianificazione ? vm.CampiCustomPianificazioneVisual : vm.CampiCustomDettagliVisual;
        var campo = Assert.Single(raccolta, c => c.Nome == Etichetta);
        campo.Valore = valore;
        await vm.SalvaCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task TestPianificato_CampoDettagliAggiuntoAlTemplateDopo_SalvaEPersisteIlValore()
    {
        var (state, tc) = Prepara(conRevisione: false, pianificazione: false);

        var vm = await ValorizzaESalva(state, tc, "Alfa", pianificazione: false);

        Assert.Empty(vm.ErroriValidazione);
        Assert.Equal("Alfa", state.TestCases.GetById(tc.Id)!.CampiCustom[Chiave]);
    }

    [Fact]
    public async Task TestPianificato_CampoPianificazioneAggiuntoAlTemplateDopo_SalvaEPersisteIlValore()
    {
        var (state, tc) = Prepara(conRevisione: false, pianificazione: true);

        var vm = await ValorizzaESalva(state, tc, "Gamma", pianificazione: true);

        Assert.Empty(vm.ErroriValidazione);
        Assert.Equal("Gamma", state.TestCases.GetById(tc.Id)!.CampiCustom[Chiave]);
    }

    [Fact]
    public async Task TestConRevisione_CampoDettagliAggiuntoAlTemplateDopo_SalvaEPersisteIlValore()
    {
        var (state, tc) = Prepara(conRevisione: true, pianificazione: false);

        var vm = await ValorizzaESalva(state, tc, "Beta", pianificazione: false);

        Assert.Empty(vm.ErroriValidazione);
        Assert.Equal("Beta", state.TestCases.GetById(tc.Id)!.CampiCustom[Chiave]);
    }

    /// <summary>Il Master cambia le opzioni di un campo Lista nel catalogo mentre il Test è aperto: l'editor del Test
    /// deve mostrare le opzioni nuove senza essere riaperto, e conservare il valore già inserito.</summary>
    [Fact]
    public async Task TestAperto_OpzioniCampoListaAggiornateDopoSalvataggioDelCatalogo()
    {
        var (state, tc) = Prepara(conRevisione: false, pianificazione: false, tipo: TipoCampoStrumento.Lista, opzioni: new List<string> { "CAN", "LIN" });
        var vm = new TestCaseEditorViewModel(state, testCaseId: tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.InModifica = true;
        Assert.Equal(new[] { "CAN", "LIN" }, Assert.Single(vm.CampiCustomDettagliVisual, c => c.Nome == Etichetta).OpzioniLista);
        Assert.Single(vm.CampiCustomDettagliVisual, c => c.Nome == Etichetta).Valore = "CAN";

        var catalogo = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        catalogo.IniziaModificaCommand.AttendiModifica();
        Assert.True(catalogo.ImpostaOpzioniLista(catalogo.Items.Single(c => c.Chiave == Chiave), new List<string> { "CAN", "LIN", "Ethernet" }));
        await catalogo.SalvaCommand.ExecuteAsync(null);
        Assert.False(catalogo.HaMessaggioErrore);

        var campo = Assert.Single(vm.CampiCustomDettagliVisual, c => c.Nome == Etichetta);
        Assert.Equal(new[] { "CAN", "LIN", "Ethernet" }, campo.OpzioniLista);
        Assert.Equal("CAN", campo.Valore);
    }

    /// <summary>Il Master toglie un'opzione mentre un Test la usa: il valore già salvato resta nel menu del Test e non si perde, finché
    /// l'operatore non sceglie un'altra voce.</summary>
    [Fact]
    public async Task TestAperto_OpzioneRimossaDalCatalogo_ValoreSalvatoRestaLeggibile()
    {
        var (state, tc) = Prepara(conRevisione: false, pianificazione: false, tipo: TipoCampoStrumento.Lista, opzioni: new List<string> { "CAN", "LIN" });
        var vm = new TestCaseEditorViewModel(state, testCaseId: tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.InModifica = true;
        Assert.Single(vm.CampiCustomDettagliVisual, c => c.Nome == Etichetta).Valore = "CAN";

        var catalogo = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        catalogo.IniziaModificaCommand.AttendiModifica();
        Assert.True(catalogo.ImpostaOpzioniLista(catalogo.Items.Single(c => c.Chiave == Chiave), new List<string> { "LIN", "Ethernet" }));
        await catalogo.SalvaCommand.ExecuteAsync(null);

        var campo = Assert.Single(vm.CampiCustomDettagliVisual, c => c.Nome == Etichetta);
        Assert.Equal("CAN", campo.Valore);
        Assert.Equal(new[] { "LIN", "Ethernet", "CAN" }, campo.OpzioniVisualizzate);
    }

    /// <summary>Un campo Numero del catalogo mostra nel Test l'unità di misura configurata nel catalogo.</summary>
    [Fact]
    public void TestAperto_CampoNumeroMostraLUnitaDelCatalogo()
    {
        var (state, tc) = Prepara(conRevisione: false, pianificazione: false, tipo: TipoCampoStrumento.Numero);
        var catalogo = state.Master.CampiSistemaDettagli.Load().ToList();
        catalogo.Single(c => c.Chiave == Chiave).UnitaMisura = "km";
        state.Master.CampiSistemaDettagli.Save(catalogo);

        var vm = new TestCaseEditorViewModel(state, testCaseId: tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });

        Assert.Equal("km", Assert.Single(vm.CampiCustomDettagliVisual, c => c.Nome == Etichetta).UnitaMisura);
    }
}
