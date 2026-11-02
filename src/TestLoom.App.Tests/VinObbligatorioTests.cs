using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Regressione matricola obbligatorio (feedback utente 6.4.x: "pur avendo messo la matricola mi dice di metterlo e non
/// mi fa salvare"). Il campo mostrato/editato e il valore validato devono essere la stessa REV (corrente) e una REV
/// storica sfogliata non deve mai essere scrivibile.</summary>
public sealed class VinObbligatorioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_VinObbl_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } } }

    private TestCaseEditorViewModel CreaVmConVinObbligatorio()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Mario Rossi", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList(); ops.Add(op); state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Mario Rossi", "1234");
        var templates = state.Master.TestTemplate.Load();
        var campoVin = templates[0].Sezioni.SelectMany(s => s.Campi).First(c => c.CampoSistemaChiave == CampiSistema.Matricola);
        campoVin.Obbligatorio = true; campoVin.StatiObbligatorio = new List<StatoLavoroTest>();
        state.Master.TestTemplate.Save(templates);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili.First(t => t.Uid == templates[0].Uid));
        return vm;
    }

    [Theory]
    [InlineData("ZFA25000002123456")]
    [InlineData("  zfa25000002123456  ")]
    [InlineData("BANCO 3")]
    public async Task VinDigitatoNelCampo_NonRisultaMancanteAlSalva(string digitato)
    {
        var vm = CreaVmConVinObbligatorio();
        Assert.True(vm.PuoModificareContenuto);
        vm.VinRevisione = digitato;
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.CampiObbligatoriMancanti, c => c.Chiave == CampiSistema.Matricola);
        Assert.Equal(digitato, vm.RevisioneCorrente!.Matricola);
    }

    [Fact]
    public async Task VinVuoto_RisultaMancante()
    {
        var vm = CreaVmConVinObbligatorio();
        vm.VinRevisione = "   ";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Contains(vm.CampiObbligatoriMancanti, c => c.Chiave == CampiSistema.Matricola);
    }

    [Fact]
    public async Task RevStoricaSfogliata_VinNonScrivibile_EValidatoQuelloMostrato()
    {
        var vm = CreaVmConVinObbligatorio();
        vm.Esecuzioni[0].Matricola = "Matricola-VECCHIO";
        vm.Esecuzioni.Add(new Esecuzione { Iterazione = 2, Matricola = "" });
        vm.RevisioneVisualizzata = vm.Esecuzioni[0];
        Assert.True(vm.VisualizzaStorico);
        Assert.Equal("Matricola-VECCHIO", vm.VinRevisione);

        vm.VinRevisione = "TENTATIVO"; // storica: sola lettura
        Assert.Equal("Matricola-VECCHIO", vm.Esecuzioni[0].Matricola);
        Assert.Equal("", vm.RevisioneCorrente!.Matricola);

        await vm.SalvaCommand.ExecuteAsync(null);
        // La validazione riallinea alla REV corrente: il campo mostrato e quello validato coincidono (vuoto, mancante).
        Assert.False(vm.VisualizzaStorico);
        Assert.Equal("", vm.VinRevisione);
        Assert.Contains(vm.CampiObbligatoriMancanti, c => c.Chiave == CampiSistema.Matricola);

        vm.VinRevisione = "ZFA25000002123456";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.CampiObbligatoriMancanti, c => c.Chiave == CampiSistema.Matricola);
    }

    [Fact]
    public async Task DopoRicostruzioneEsecuzioni_VinScrittoVaSullaRevCorrente()
    {
        var vm = CreaVmConVinObbligatorio();
        var nuove = vm.Esecuzioni.ToList();
        vm.Esecuzioni.Clear();
        foreach (var e in nuove) vm.Esecuzioni.Add(e);
        vm.VinRevisione = "ZFA25000002123456";
        Assert.Equal("ZFA25000002123456", vm.RevisioneCorrente!.Matricola);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.CampiObbligatoriMancanti, c => c.Chiave == CampiSistema.Matricola);
    }

    [Fact]
    public async Task TestSenzaRev_CampoVinNonCompilabile_NonObbligatorioNeBloccaIlSalva()
    {
        var vm = CreaVmConVinObbligatorio();
        vm.Esecuzioni.Clear(); // test pianificato: "Avvia prima esecuzione" non ancora premuto
        Assert.False(vm.HaRevisioneCorrente);
        Assert.False(vm.PuoModificareCampiRevisione);
        Assert.False(vm.VinObbligatorio); // niente asterisco su un campo che non si puo' compilare
        vm.VinRevisione = "ZFA25000002123456"; // mai scartato in silenzio ne' creatore di REV: la UI lo disabilita
        Assert.Empty(vm.Esecuzioni);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.CampiObbligatoriMancanti, c => c.Chiave == CampiSistema.Matricola);
    }

    [Fact]
    public void AvviataLaPrimaEsecuzione_CampoVinCompilabileEObbligatorio()
    {
        var vm = CreaVmConVinObbligatorio();
        vm.Esecuzioni.Clear();
        vm.AggiungiIterazioneCommand.Execute(null);
        Assert.True(vm.PuoModificareCampiRevisione);
        Assert.True(vm.VinObbligatorio);
        vm.VinRevisione = "ZFA25000002123456";
        Assert.Equal("ZFA25000002123456", vm.RevisioneCorrente!.Matricola);
    }

    private (AppState State, Guid TemplateUid) CreaStatoConVinObbligatorioPerStati(params StatoLavoroTest[] stati)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Mario Rossi", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList(); ops.Add(op); state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Mario Rossi", "1234");
        var templates = state.Master.TestTemplate.Load();
        var campoVin = templates[0].Sezioni.SelectMany(s => s.Campi).First(c => c.CampoSistemaChiave == CampiSistema.Matricola);
        campoVin.Obbligatorio = true;
        campoVin.StatiObbligatorio = stati.ToList();
        state.Master.TestTemplate.Save(templates);
        return (state, templates[0].Uid);
    }

    /// <summary>Segnalazione utente 2026-10-06: da un test NUOVO portato subito a Concluso / Da ritestare, con la matricola
    /// digitato nello stesso editing, il Salva diceva 'matricola mancante'.</summary>
    [Theory]
    [InlineData(StatoLavoroTest.Concluso)]
    [InlineData(StatoLavoroTest.DaRitestare)]
    public async Task TestNuovo_StatoImmediato_VinDigitato_NonRisultaMancante(StatoLavoroTest stato)
    {
        var (state, uid) = CreaStatoConVinObbligatorioPerStati(StatoLavoroTest.Concluso, StatoLavoroTest.DaRitestare);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili.First(t => t.Uid == uid));

        vm.StatoLavoroSelezionato = stato;
        vm.VinRevisione = "ZFA25000002123456";
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.DoesNotContain(vm.CampiObbligatoriMancanti, c => c.Chiave == CampiSistema.Matricola);

        // Ordine inverso: prima la matricola, poi lo stato.
        var vm2 = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm2.SceglieTemplateCommand.Execute(vm2.TemplatesDisponibili.First(t => t.Uid == uid));
        vm2.VinRevisione = "ZFA25000002123456";
        vm2.StatoLavoroSelezionato = stato;
        await vm2.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm2.CampiObbligatoriMancanti, c => c.Chiave == CampiSistema.Matricola);
    }

    /// <summary>Stessa radice della matricola (segnalazione utente 2026-10-06): un test senza REV portato subito a Concluso /
    /// Da ritestare (anche in creazione) non ha dove tenere matricola e operazioni; ora la prima REV nasce al cambio stato e
    /// le operazioni inserite sopravvivono sia a un Salva fallito per un campo mancante sia al Salva riuscito.</summary>
    [Fact]
    public async Task NuovoTest_SenzaRev_ConclusoSubito_RevCreata_OperazioniNonSpariscono()
    {
        var (state, uid) = CreaStatoConVinObbligatorioPerStati(StatoLavoroTest.Concluso);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili.First(t => t.Uid == uid));
        vm.Esecuzioni.Clear();
        Assert.False(vm.HaRevisioneCorrente);

        vm.StatoLavoroSelezionato = StatoLavoroTest.Concluso;
        Assert.True(vm.HaRevisioneCorrente);
        Assert.Single(vm.Esecuzioni);

        vm.RevisioneCorrente!.Condizioni.Add(new Condizione { Esito = Esito.Conforme, Funzionalita = "F1" });
        vm.RevisioneCorrente.Condizioni.Add(new Condizione { Esito = Esito.Conforme, Funzionalita = "F2" });

        await vm.SalvaCommand.ExecuteAsync(null); // matricola mancante: Salva fallisce, le operazioni devono restare
        Assert.Contains(vm.CampiObbligatoriMancanti, c => c.Chiave == CampiSistema.Matricola);
        Assert.Single(vm.Esecuzioni);
        Assert.Equal(2, vm.RevisioneCorrente!.Condizioni.Count);

        vm.VinRevisione = "ZFA25000002123456";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.CampiObbligatoriMancanti, c => c.Chiave == CampiSistema.Matricola);
        Assert.Equal(2, vm.RevisioneCorrente!.Condizioni.Count);
    }

    /// <summary>Regressione 6.4.8: aprendo un test ESISTENTE e premendo Modifica, il campo matricola restava disabilitato
    /// (PuoModificareCampiRevisione non notificata da InModifica) fino al primo Salva.</summary>
    [Fact]
    public async Task TestEsistente_IniziaModifica_NotificaCampiRevisioneEVinCompilabile()
    {
        var (state, uid) = CreaStatoConVinObbligatorioPerStati(StatoLavoroTest.Concluso, StatoLavoroTest.DaRitestare);
        var id = "T-matricola-1";
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" } });
        state.TestCases.Save(new TestCase { Id = id, TestTemplateUid = uid, Componente = { "BCM" }, Veicolo = "V1", Veicoli = { "V1" }, Descrizione = "Prova matricola", StatoLavoro = StatoLavoroTest.Pianificato }, isNew: true);

        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        Assert.False(vm.InModifica);
        var notifiche = new List<string?>();
        vm.PropertyChanged += (_, e) => notifiche.Add(e.PropertyName);

        vm.IniziaModificaCommand.AttendiModifica();

        Assert.True(vm.InModifica);
        // Test pianificato: nessuna REV, matricola non ancora compilabile.
        Assert.False(vm.HaRevisioneCorrente);
        Assert.False(vm.PuoModificareCampiRevisione);
        Assert.Contains(nameof(TestCaseEditorViewModel.PuoModificareCampiRevisione), notifiche);

        // Passando subito a Concluso nasce la prima REV (come 'Avvia prima esecuzione'): la matricola diventa compilabile.
        vm.StatoLavoroSelezionato = StatoLavoroTest.Concluso;
        Assert.True(vm.HaRevisioneCorrente);
        Assert.True(vm.PuoModificareCampiRevisione);
        Assert.NotNull(vm.TemplateCorrente);
        Assert.Equal(StatoLavoroTest.Concluso, vm.TestCase.StatoLavoro);
        Assert.True(vm.VinObbligatorio);
        vm.VinRevisione = "ZFA25000002123456";
        Assert.Equal("ZFA25000002123456", vm.RevisioneCorrente!.Matricola);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.CampiObbligatoriMancanti, c => c.Chiave == CampiSistema.Matricola);
    }
}
