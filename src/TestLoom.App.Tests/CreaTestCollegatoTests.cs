using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata al redesign "Crea test collegato" (feedback utente 2026-08-20, punti 17/21):
/// menu interattivo runtime — quali campi tenere, quale/i tipo/i di test collegato creare (anche lo stesso
/// tipo del test di partenza, anche più di uno insieme) — al posto della vecchia AzioneCollegataTemplate
/// preconfigurata dal Master nel template, ora rimossa del tutto.</summary>
public sealed class CreaTestCollegatoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_CreaTestCollegato_" + Guid.NewGuid().ToString("N"));

    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;

    public void Dispose()
    {
        ConfirmService.Handler = _confirmOriginale;
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
    public void HaAzioniCollegate_SempreVero_NonPiuCondizionatoDaConfigurazioneTemplate()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        Assert.True(vm.HaAzioniCollegate);
    }

    [Fact]
    public void CreaTestCollegati_UnSoloTemplate_CreaUnTestENavigaLi()
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];

        string? navigatoA = null;
        var vm2 = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: id => navigatoA = id);
        vm2.SceglieTemplateCommand.Execute(template);
        vm2.AggiungiEcuCommand.Execute("BCM");

        vm2.CreaTestCollegati(new List<TestTemplate> { template }, new HashSet<string> { CampiSistema.Componente });

        Assert.Single(vm2.TestCase.TestCollegatiCreati);
        Assert.NotNull(navigatoA);
        Assert.Equal(vm2.TestCase.TestCollegatiCreati[0].TestCollegatoId, navigatoA);
    }

    // Regressione FlaUI CreaTestCollegato_ConUnSoloTemplate...: dopo la creazione il Test e' gia' su disco,
    // quindi la navigazione al test collegato non deve chiedere "modifiche non salvate" e le creazioni
    // successive dallo stesso editor non devono andare in conflitto con la scrittura precedente.
    [Fact]
    public void CreaTestCollegati_DopoIlSalvataggio_NonRestaSporco_ESecondaCreazioneNonVaInConflitto()
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(template);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.CreaTestCollegati(new List<TestTemplate> { template }, new HashSet<string>());
        Assert.False(vm.ModificheNonSalvate);

        // Con modifiche pendenti il percorso "senza validazioni" non e' raggiungibile: il wrapper sicuro rifiuta.
        vm.ModificheNonSalvate = true;
        vm.CreaTestCollegatiSicuro(new List<TestTemplate> { template }, new HashSet<string>());
        Assert.Single(vm.TestCase.TestCollegatiCreati);
        Assert.NotEmpty(vm.ErroriValidazione);
    }

    private (AppState State, string Id, TestTemplate Template) CreaTestPersistito()
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(template);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.VeicoloSelezionato = "Daily 24";
        vm.CreaTestCollegati(new List<TestTemplate> { template }, new HashSet<string>());
        Assert.False(vm.ModificheNonSalvate);
        return (state, vm.TestCase.Id, template);
    }

    private static string Rev(Esecuzione e) => System.Text.Json.JsonSerializer.Serialize(e, JsonIo.CompactOptions);

    [Fact]
    public async Task SalvaECrea_ConModificheEConferma_SalvaConChangelog_PoiCreaIlCollegato()
    {
        var (state, id, template) = CreaTestPersistito();
        var rev1Prima = Rev(state.TestCases.GetById(id)!.Esecuzioni[0]);
        int tornaAllaLista = 0; string? navigato = null; var domande = new List<string>();
        var vm = new TestCaseEditorViewModel(state, id, () => tornaAllaLista++, nuovoId => navigato = nuovoId);
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        vm.TestCase.Descrizione = "descrizione modificata prima del collegato";
        vm.ModificheNonSalvate = true;
        ConfirmService.Handler = (_, corpo, si, _) => { domande.Add(si); return Task.FromResult(true); };

        Assert.True(await vm.AssicuraTestSalvatoPerTestCollegato());

        Assert.Equal("Salva e crea", Assert.Single(domande));
        Assert.False(vm.ModificheNonSalvate);
        Assert.Equal(0, tornaAllaLista); // il Salva normale non deve riportare alla lista
        var salvato = state.TestCases.GetById(id)!;
        Assert.Equal("descrizione modificata prima del collegato", salvato.Descrizione);
        Assert.Equal(rev1Prima, Rev(salvato.Esecuzioni[0]));
        Assert.Equal(Enumerable.Range(1, salvato.Esecuzioni.Count), salvato.Esecuzioni.Select(e => e.Iterazione));
        Assert.Equal(2, salvato.Esecuzioni.Count);
        Assert.Single(salvato.Changelog, c => c.Campo == "descrizione");
        Assert.Single(salvato.TestCollegatiCreati);

        vm.CreaTestCollegatiSicuro(new List<TestTemplate> { template }, new HashSet<string>());

        var dopo = state.TestCases.GetById(id)!;
        Assert.Equal(2, dopo.TestCollegatiCreati.Count);
        Assert.Equal(2, dopo.Esecuzioni.Count);
        Assert.Single(dopo.Changelog, c => c.Campo == "descrizione");
        Assert.Equal(dopo.TestCollegatiCreati[^1].TestCollegatoId, navigato);
    }

    [Fact]
    public async Task SalvaECrea_ConConfermaMaValidazioneFallita_NonCreaNulla_MostraGliErrori()
    {
        var (state, id, template) = CreaTestPersistito();
        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        vm.TestCase.Veicolo = "";
        vm.TestCase.Veicoli = new List<string>();
        vm.TestCase.ContestoPredefinito.Veicolo = "";
        vm.ModificheNonSalvate = true;
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);

        Assert.False(await vm.AssicuraTestSalvatoPerTestCollegato());

        Assert.NotEmpty(vm.ErroriValidazione);
        Assert.True(vm.ModificheNonSalvate);
        vm.CreaTestCollegatiSicuro(new List<TestTemplate> { template }, new HashSet<string>());
        Assert.Single(state.TestCases.GetById(id)!.TestCollegatiCreati);
    }

    [Fact]
    public async Task SalvaECrea_Annulla_NonFaNulla_LeModificheRestanoPendenti()
    {
        var (state, id, template) = CreaTestPersistito();
        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        vm.TestCase.Descrizione = "non salvare";
        vm.ModificheNonSalvate = true;
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(false);

        Assert.False(await vm.AssicuraTestSalvatoPerTestCollegato());

        Assert.True(vm.ModificheNonSalvate);
        var disco = state.TestCases.GetById(id)!;
        Assert.NotEqual("non salvare", disco.Descrizione);
        Assert.Single(disco.TestCollegatiCreati);
        Assert.DoesNotContain(disco.Changelog, c => c.Campo == "descrizione");
    }

    [Fact]
    public async Task SalvaECrea_ConEditorPulito_NonChiedeNulla()
    {
        var (state, id, _) = CreaTestPersistito();
        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        ConfirmService.Handler = (_, _, _, _) => throw new InvalidOperationException("nessuna domanda attesa");

        Assert.True(await vm.AssicuraTestSalvatoPerTestCollegato());
    }

    // Il ricaricamento periodico dei master data fa riscrivere alla ComboBox "Operatore" un valore
    // transitorio: non deve sporcare il Test (nemmeno in sola lettura) ne' alterare l'Operatore.
    [Fact]
    public void RevisioneCorrenteOperatore_DuranteRiallineamentoListeMaster_IgnoraIlWritebackTransitorio()
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(template);
        Assert.NotNull(vm.RevisioneCorrente);
        vm.RevisioneCorrenteOperatore = "Mario Rossi";
        vm.ModificheNonSalvate = false;

        var campo = typeof(TestCaseEditorViewModel).GetField("_riallineamentoListeMaster", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        campo.SetValue(vm, true);
        vm.RevisioneCorrenteOperatore = "";
        campo.SetValue(vm, false);

        Assert.Equal("Mario Rossi", vm.RevisioneCorrenteOperatore);
        Assert.False(vm.ModificheNonSalvate);
    }

    [Fact]
    public void CreaTestCollegati_StessoTemplateDelTestDiPartenza_Consentito()
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(template);
        vm.AggiungiEcuCommand.Execute("BCM");

        vm.CreaTestCollegati(new List<TestTemplate> { template }, new HashSet<string>());

        var creato = Assert.Single(vm.TestCase.TestCollegatiCreati);
        var collegato = state.TestCases.GetById(creato.TestCollegatoId);
        Assert.NotNull(collegato);
        Assert.Equal(template.Uid, collegato!.TestTemplateUid);
    }

    [Fact]
    public void CreaTestCollegati_PiuTemplateInsieme_CreaUnTestPerCiascuno_NonNaviga()
    {
        var state = CreateState();
        var templates = state.Master.TestTemplate.Load();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(templates[0]);
        vm.AggiungiEcuCommand.Execute("BCM");

        vm.CreaTestCollegati(templates.Take(3).ToList(), new HashSet<string> { CampiSistema.Componente });

        Assert.Equal(3, vm.TestCase.TestCollegatiCreati.Count);
        foreach (var creato in vm.TestCase.TestCollegatiCreati)
            Assert.NotNull(state.TestCases.GetById(creato.TestCollegatoId));
    }

    [Fact]
    public void CreaTestCollegati_SoloCampiScelti_VengonoCopiati_AltriRestanoVuoti()
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(template);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.TestCase.DescrizioneBreve = "Descrizione originale";

        // Solo Componente scelto, non DescrizioneBreve.
        vm.CreaTestCollegati(new List<TestTemplate> { template }, new HashSet<string> { CampiSistema.Componente });

        var collegato = state.TestCases.GetById(vm.TestCase.TestCollegatiCreati[0].TestCollegatoId);
        Assert.Equal("BCM", collegato!.Componente);
        Assert.NotEqual("Descrizione originale", collegato.DescrizioneBreve);
    }

    [Fact]
    public void CreaTestCollegati_TemplateDestinazioneNome_CatturatoAllaCreazione()
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(template);
        vm.AggiungiEcuCommand.Execute("BCM");

        vm.CreaTestCollegati(new List<TestTemplate> { template }, new HashSet<string>());

        Assert.Equal(template.Nome, vm.TestCase.TestCollegatiCreati[0].TemplateDestinazioneNome);
    }

    // Punto 21: la ricerca dei test da collegare funziona anche per componente, non solo Id/descrizione breve.
    [Fact]
    public void AutoCompleteFilters_TestCorrelato_TrovaAncheColECU()
    {
        var tc = new TestCase { Id = "TC-XYZ-99", Componente = "BCM", DescrizioneBreve = "Verifica accensione" };
        Assert.True(AutoCompleteFilters.TestCorrelato("BCM", tc));
        Assert.True(AutoCompleteFilters.TestCorrelato("accensione", tc));
        Assert.True(AutoCompleteFilters.TestCorrelato("TC-XYZ", tc));
        Assert.False(AutoCompleteFilters.TestCorrelato("ECM", tc));
    }
}
