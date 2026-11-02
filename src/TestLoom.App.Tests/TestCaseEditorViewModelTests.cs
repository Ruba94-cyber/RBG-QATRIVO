using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Smoke test minimo (Addendum: collaudo live 2026-08-17 trovò un bug reale che il compilatore non
/// poteva vedere — AreeValidazione era rimasta "Enum.GetValues(typeof(AreaValidazione))" dopo che AreaValidazione
/// era diventata una classe master data invece di un enum: compilava, ma lanciava un'eccezione al primo
/// accesso. TestCaseEditorViewModel non aveva nessuna copertura di test prima d'ora — questo non è un test
/// esaustivo del ViewModel, solo una rete di sicurezza contro proprietà che esplodono all'apertura del form.</summary>
[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class TestCaseEditorViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_editorvm_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CostruzioneSuTestNuovo_NonLanciaEProprietaChiaveSonoPopolate()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        Assert.NotEmpty(vm.TemplatesDisponibili);
        Assert.NotEmpty(vm.StatiLavoro);
        Assert.True(vm.IsNew);
    }

    [Fact]
    public void StatoOperazioneNuova_MostraNonEseguitoComeValoreInizialeSelezionabile()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);

        Assert.Equal(Esito.DaEseguire, vm.RevisioneCorrente!.Condizioni[0].Esito);
        Assert.Equal(Esito.DaEseguire, vm.EsitiTecnici.GetValue(0));
        Assert.Contains(Esito.DaEseguire, vm.EsitiTecnici.Cast<Esito>());
        Assert.False(vm.RevisioneCorrente.Condizioni[0].Esito.EEseguito());
    }

    /// <summary>Filtra invece di solo riordinare (feedback utente 2026-08-24): prima l'intero catalogo restava
    /// sempre raggiungibile "sotto" i componenti del veicolo scelto, ora un componente di un altro veicolo sparisce del
    /// tutto — il modo per riportarla in lista è aggiungere anche quel veicolo al test (VeicoliEffettivi),
    /// non tenerla comunque visibile.</summary>
    [Fact]
    public void EcuDisponibili_SoloLeEcuDelVeicoloSelezionato_LeAltreSpariscono()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "BCM Alpha24", VeicoliAssociati = new List<string> { "Alpha 24" } },
            new() { Nome = "BCM Daily24-Series", VeicoliAssociati = new List<string> { "Daily 24", "eDaily 24" } },
        });
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        vm.VeicoloSelezionato = "eDaily 24";

        Assert.Contains("BCM Daily24-Series", vm.EcuDisponibili);
        Assert.DoesNotContain("BCM Alpha24", vm.EcuDisponibili);
    }

    [Fact]
    public void SelezionaVeicolo_NotificaLeEcuDopoAggiornamentoDelFiltro()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "Componente V1", VeicoliAssociati = new List<string> { "V1" } },
            new() { Nome = "Componente V2", VeicoliAssociati = new List<string> { "V2" } },
        });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" }, new() { Nome = "V2" } });
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new()
            {
                Nome = "Solo V2 su componente V1",
                TutteLeEcu = false,
                EcuAssociate = new List<string> { "Componente V1" },
                VeicoliPerEcu = new List<VeicoliFunzionalitaEcu>
                {
                    new() { Componente = "Componente V1", Veicoli = new List<string> { "V2" } },
                },
            },
        });
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.AggiungiEcuCommand.Execute("Componente V1");
        List<string>? ecuNotificate = null;
        List<string>? funzionalitaNotificate = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.EcuDisponibili)) ecuNotificate = vm.EcuDisponibili;
            if (e.PropertyName == nameof(vm.FunzionalitaDisponibili)) funzionalitaNotificate = vm.FunzionalitaDisponibili;
        };

        vm.VeicoloSelezionato = "V1";

        Assert.NotNull(ecuNotificate);
        Assert.Contains("Componente V1", ecuNotificate);
        Assert.DoesNotContain("Componente V2", ecuNotificate);
        Assert.NotNull(funzionalitaNotificate);
        Assert.DoesNotContain("Solo V2 su componente V1", funzionalitaNotificate);
    }

    [Fact]
    public void EcuDisponibili_NessunVeicoloScelto_TornaCatalogoIntero()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "BCM Alpha24", VeicoliAssociati = new List<string> { "Alpha 24" } },
            new() { Nome = "BCM Daily24-Series", VeicoliAssociati = new List<string> { "Daily 24" } },
        });
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        Assert.Contains("BCM Alpha24", vm.EcuDisponibili);
        Assert.Contains("BCM Daily24-Series", vm.EcuDisponibili);
    }

    /// <summary>Un test può coprire più Veicoli/gamme insieme (feedback utente 2026-08-24): il componente condivisa
    /// tra generazioni diverse torna raggiungibile aggiungendo anche l'altro Veicolo al test, non tenendo
    /// sempre visibile l'intero catalogo come prima.</summary>
    [Fact]
    public void EcuDisponibili_PiuVeicoliNelTest_UnioneDelleEcuDiCiascuno()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "BCM Alpha24", VeicoliAssociati = new List<string> { "Alpha 24" } },
            new() { Nome = "BCM Daily24-Series", VeicoliAssociati = new List<string> { "Daily 24" } },
        });
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        vm.TestCase.Veicoli = new List<string> { "Daily 24", "Alpha 24" };

        Assert.Contains("BCM Daily24-Series", vm.EcuDisponibili);
        Assert.Contains("BCM Alpha24", vm.EcuDisponibili);
    }

    [Fact]
    public void EcuDisponibili_VeicoloIntero_RestaSempreInCima()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM Alpha24", VeicoliAssociati = new List<string> { "Alpha 24" } } });
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        vm.VeicoloSelezionato = "Alpha 24";

        Assert.Equal(TestCaseEditorViewModel.EcuVeicoloIntero, vm.EcuDisponibili[0]);
    }

    [Fact]
    public void TestRichiedeEcu_VeroSuTestNuovoSenzaEcu()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        Assert.True(vm.TestRichiedeEcu);
        Assert.NotNull(vm.PreparaPolarionTooltip);

        vm.AggiungiEcuCommand.Execute("BCM");

        Assert.False(vm.TestRichiedeEcu);
        Assert.Null(vm.PreparaPolarionTooltip);
    }

    [Fact]
    public void AggiuntaSecondaFunzionalita_AbilitaSubitoIlSelettoreOperazioni()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var vm = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        var notificata = false;
        vm.PropertyChanged += (_, e) => notificata |= e.PropertyName == nameof(vm.FunzionalitaOperazioneObbligatoria);

        vm.NuovaFunzionalita = "Funzione A";
        vm.AggiungiFunzionalitaCommand.Execute(null);
        Assert.All(vm.RevisioneCorrente!.Condizioni, c => Assert.Equal("Funzione A", c.Funzionalita));
        vm.NuovaFunzionalita = "Funzione B";
        vm.AggiungiFunzionalitaCommand.Execute(null);

        Assert.True(vm.FunzionalitaOperazioneObbligatoria);
        Assert.True(notificata);
    }

    [Fact]
    public void SoloStrumentiDiSupporto_MostraERichiedeFunzionalitaGeneraleDelTest()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var vm = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        foreach (var strumento in vm.StrumentiOptions) strumento.Selezionato = false;

        Assert.False(vm.SoloStrumentiSupporto); // nessuno strumento non attiva la regola
        var operazioniPrimaDelSupporto = vm.RevisioneCorrente!.Condizioni.Count;
        vm.StrumentiOptions.First(o => o.Valore == "ValidationTool").Selezionato = true;

        Assert.True(vm.SoloStrumentiSupporto);
        Assert.True(vm.MostraCampoFunzionalita);
        Assert.True(vm.FunzionalitaObbligatoria);
        Assert.Equal(operazioniPrimaDelSupporto, vm.RevisioneCorrente.Condizioni.Count); // il supporto non crea operazioni

        vm.StrumentiOptions.First(o => o.Valore == "UnitLogger").Selezionato = true;
        Assert.False(vm.SoloStrumentiSupporto); // un test che ha anche uno strumento Da testare segue le regole UnitLogger
    }

    [Fact]
    public void DeselezioniTransitorieDelleCombo_NonCancellanoTipoEcuOVeicolo()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        var template = vm.TemplatesDisponibili[0];
        vm.SceglieTemplateCommand.Execute(template);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.VeicoloSelezionato = "Alpha 24";

        // Avalonia puo emettere questi valori durante la sostituzione periodica degli ItemsSource: non sono
        // una scelta esplicita dell'utente e non devono mai diventare dati del TestCase.
        vm.TemplateSelezionato = null;
        vm.VeicoloSelezionato = "";

        Assert.Equal(template.Uid, vm.TestCase.TestTemplateUid);
        Assert.Equal(template.Uid, vm.TemplateSelezionato?.Uid);
        Assert.Equal("BCM", vm.TestCase.Componente);
        Assert.Contains("BCM", vm.TestCase.Componente.AsEnumerable());
        Assert.Equal("Alpha 24", vm.TestCase.Veicolo);
        Assert.Equal("Alpha 24", vm.VeicoloSelezionato);
    }

    [Fact]
    public void RicaricaPeriodica_RiallineaLeSelezioniSenzaSporcareIlTest()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        var template = vm.TemplatesDisponibili[0];
        vm.SceglieTemplateCommand.Execute(template);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.VeicoloSelezionato = "Alpha 24";
        vm.ModificheNonSalvate = false;

        state.RicaricaTutto();

        Assert.Equal(template.Uid, vm.TestCase.TestTemplateUid);
        Assert.Same(vm.TemplatesDisponibili.Single(t => t.Uid == template.Uid), vm.TemplateSelezionato);
        Assert.Contains("BCM", vm.TestCase.Componente.AsEnumerable());
        Assert.Equal("Alpha 24", vm.VeicoloSelezionato);
        Assert.False(vm.ModificheNonSalvate);
    }

    [Fact]
    public void RicaricaPeriodica_ValoriGiaUsatiArchiviatiRestanoVisibili()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        var template = vm.TemplatesDisponibili[0];
        var veicolo = state.Master.Veicoli.Load().First();
        vm.SceglieTemplateCommand.Execute(template);
        vm.VeicoloSelezionato = veicolo.Nome;

        var templateSalvati = state.Master.TestTemplate.Load();
        templateSalvati.Single(t => t.Uid == template.Uid).Archiviata = true;
        state.Master.TestTemplate.Save(templateSalvati);
        var veicoliSalvati = state.Master.Veicoli.Load();
        veicoliSalvati.Single(v => v.Nome == veicolo.Nome).Archiviata = true;
        state.Master.Veicoli.Save(veicoliSalvati);

        state.RicaricaTutto();

        Assert.Contains(vm.TemplatesDisponibili, t => t.Uid == template.Uid);
        Assert.Equal(template.Uid, vm.TemplateSelezionato?.Uid);
        Assert.Contains(veicolo.Nome, vm.VeicoliDisponibili);
        Assert.Equal(veicolo.Nome, vm.VeicoloSelezionato);
    }

    [Fact]
    public void CreaTicket_SuTestNuovoConEcu_SalvaInAutomaticoSenzaSalvataggioManuale()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var destinazione = Path.Combine(_dir, "ticketing-export");
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("BCM");
        Assert.True(string.IsNullOrEmpty(vm.TestCase.Id));

        vm.CreaTicketCommand.Execute(new RichiestaPacchettoPolarion(destinazione, new HashSet<string> { PolarionSezioni.DatiGenerali }));

        Assert.False(string.IsNullOrEmpty(vm.TestCase.Id));
        Assert.NotNull(state.TestCases.GetById(vm.TestCase.Id));
        Assert.True(Directory.Exists(destinazione) && Directory.GetFiles(destinazione, "*.zip").Length > 0);
    }

    [Fact]
    public void AggiungiRequisito_CreaVocePunteggioAutomaticaPerLaRevisioneCorrente()
    {
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);
        var requisito = vm.RequisitiDisponibili[0];
        vm.RequisitoSelezionato = requisito;
        vm.AggiungiRequisitoCommand.Execute(null);

        Assert.Contains(vm.PunteggiRequisitiVisual, p => p.RequisitoId == requisito.Id && p.Valore == PunteggioConformita.Conforme);
    }

    [Fact]
    public void RimuoviRequisito_RimuoveAncheLaVocePunteggio()
    {
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);
        var requisito = vm.RequisitiDisponibili[0];
        vm.RequisitoSelezionato = requisito;
        vm.AggiungiRequisitoCommand.Execute(null);

        vm.RimuoviRequisitoCommand.Execute(requisito.Id);

        Assert.Empty(vm.PunteggiRequisitiVisual);
    }

    [Fact]
    public async Task Salva_BloccaSePunteggioSovrascrittoSenzaMotivazione()
    {
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);
        var requisito = vm.RequisitiDisponibili[0];
        vm.RequisitoSelezionato = requisito;
        vm.AggiungiRequisitoCommand.Execute(null);
        vm.PunteggiRequisitiVisual[0].Sovrascritto = true; // MotivazioneOverride lasciata vuota di proposito

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Contains(vm.ErroriValidazione, e => e.Contains(requisito.Id));
    }

    [Fact]
    public async Task Salva_BloccaConclusioneConOperazioneAncoraDaEseguire()
    {
        // Decisione utente 6.4.0, punto 1: un Test non può passare a Concluso mentre ha ancora un'operazione
        // "Da eseguire" nella revisione corrente.
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);
        vm.AggiungiCondizioneCommand.Execute(null); // nasce Esito.DaEseguire (default)
        vm.StatoLavoroSelezionato = StatoLavoroTest.Concluso;

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Contains(vm.ErroriValidazione, e => e.Contains(LocalizationService.Instance.Traduci("ValOperazioniNonEseguiteConclusione")));
    }

    [Fact]
    public async Task Salva_NonBloccaConclusionePerOperazioneNonApplicabile()
    {
        // N.A. resta un esito di chiusura legittimo: non deve impedire la Conclusione come invece fa "Da
        // eseguire" nel test sopra.
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);
        vm.StatoLavoroSelezionato = StatoLavoroTest.Concluso;

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.DoesNotContain(vm.ErroriValidazione, e => e.Contains(LocalizationService.Instance.Traduci("ValOperazioniNonEseguiteConclusione")));
    }

    [Fact]
    public void MostraRequisitiCollegati_FalseQuandoIlTemplateNonIncludePiuQuelCampo()
    {
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);
        Assert.True(vm.MostraRequisitiCollegati); // di default il seed include il campo

        vm.TemplateSelezionato!.CampiRiferimenti.RemoveAll(c => c.CampoSistemaChiave == CampiSistema.RequisitiCollegati);

        Assert.False(vm.MostraRequisitiCollegati);
        Assert.True(vm.MostraTestCorrelati); // gli altri due restano visibili, invariati
    }

    [Fact]
    public void CampiRevisioneCostanti_UsaLoStrumentarioStoricoDellaRevisioneSfogliata()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);

        // Revisione storica: "Componente Usato" selezionato, con un valore PerRevisione già compilato.
        var vecchia = new Esecuzione
        {
            Iterazione = 1, Data = DateTime.Now.AddDays(-2), Operatore = "Op",
            ValoriStrumenti = new Dictionary<string, string> { ["Componente Usato::Km percorsi"] = "12345" },
            Dettagli = new DettagliTestSnapshot { StrumentiUsati = new List<string> { "Componente Usato" } },
            Condizioni = new List<Condizione> { new() { Esito = Esito.Conforme } },
        };
        // Revisione corrente: lo strumento selezionato è cambiato in "UnitLogger" (nessun dato "Componente Usato").
        var corrente = new Esecuzione
        {
            Iterazione = 2, Data = DateTime.Now, Operatore = "Op",
            Condizioni = new List<Condizione> { new() { Esito = Esito.Conforme } },
        };
        var tc = new TestCase
        {
            Id = "TC-STORICO-1", Componente = "BCM", StrumentiUsati = new List<string> { "UnitLogger" },
            Esecuzioni = new List<Esecuzione> { vecchia, corrente },
        };
        state.TestCases.Save(tc, isNew: true);

        var vm = new TestCaseEditorViewModel(state, testCaseId: tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.RevisioneVisualizzata = vm.Esecuzioni[0]; // sfoglia la revisione storica

        Assert.Contains(vm.CampiRevisioneCostanti, c => c.Nome == "Km percorsi" && c.Valore == "12345");
    }

    [Fact]
    public void SezioniPolarionDisponibili_TutteSeIlTemplateNonLeRistringe()
    {
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);

        // Il catalogo fisso al completo più una voce dinamica per ciascuna sezione custom del template
        // (feedback utente 2026-08-21, punto 10) — il template seed ne ha esattamente una ("Dettagli").
        var attese = PolarionSezioni.Tutte.Length + vm.TemplateSelezionato!.Sezioni.Count;
        Assert.Equal(attese, vm.SezioniPolarionDisponibili().Count);
    }

    [Fact]
    public void SezioniPolarionDisponibili_SoloQuelleDelTemplateSeRistrette()
    {
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);
        vm.TemplateSelezionato!.SezioniPolarionDisponibili = new List<string> { PolarionSezioni.DatiGenerali, PolarionSezioni.Allegati };

        var disponibili = vm.SezioniPolarionDisponibili();

        // Il narrowing del Master si applica solo al catalogo fisso: le voci dinamiche per sezione custom
        // restano sempre disponibili (nessuna UI ancora per restringerle singolarmente, feedback utente
        // 2026-08-21, punto 10 — scelta di scope, non un oversight).
        var sezioniDinamicheAttese = vm.TemplateSelezionato!.Sezioni.Select(s => "Sezione:" + s.Uid);
        Assert.Equal(new[] { PolarionSezioni.DatiGenerali, PolarionSezioni.Allegati }.Concat(sezioniDinamicheAttese), disponibili);
    }

    [Fact]
    public void SezioniPolarionDiDefault_EscludeSezioniNonDisponibiliPerIlTemplate()
    {
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);
        vm.TemplateSelezionato!.SezioniPolarionDisponibili = new List<string> { PolarionSezioni.DatiGenerali };

        var defaultSezioni = vm.SezioniPolarionDiDefault();

        Assert.DoesNotContain(PolarionSezioni.RevisioneCorrente, defaultSezioni);
    }

    [Fact]
    public void EtichettaEcu_SeguelEtichettaRinominataDelRuoloEcu()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var catalogo = state.Master.CampiSistemaDettagli.Load();
        catalogo.Single(v => v.Ruolo == RuoloCampoSistema.Componente).Etichetta = "Centralina";
        state.Master.CampiSistemaDettagli.Save(catalogo);

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        Assert.Equal("Centralina", vm.EtichettaEcu);
    }

    [Fact]
    public void CampiCustomDettagliVisual_MostraUnCampoLiberoDelTemplate()
    {
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);
        var campoLibero = new CampoTemplate { Tipo = TipoCampoTemplate.CampoLibero, Nome = "Lotto produzione", TipoLibero = TipoCampoStrumento.Testo };
        vm.TemplateSelezionato!.Sezioni[0].Campi.Add(campoLibero);
        vm.SincronizzaEsitoCondizioni();

        var riga = Assert.Single(vm.CampiCustomDettagliVisual, c => c.Nome == "Lotto produzione");
        Assert.Equal("Lotto produzione", riga.Nome);

        riga.Valore = "L-42";
        Assert.Equal("L-42", vm.TestCase.CampiCustom[campoLibero.Uid.ToString()]);
    }

    [Fact]
    public void CampiCustomPianificazioneVisual_MostraUnCampoDiCatalogoRuoloNessuno()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var catalogoPianificazione = state.Master.CampiSistemaPianificazione.Load();
        catalogoPianificazione.Add(new CampoSistemaDefinizione { Chiave = "priorita-custom", Etichetta = "Priorità interna", Gruppo = GruppoCampoSistema.Pianificazione, Tipo = TipoCampoStrumento.Numero, Ruolo = RuoloCampoSistema.Nessuno });
        state.Master.CampiSistemaPianificazione.Save(catalogoPianificazione);

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.TemplateSelezionato!.CampiPianificazione.Add(new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = "priorita-custom" });
        vm.SincronizzaEsitoCondizioni();

        var riga = Assert.Single(vm.CampiCustomPianificazioneVisual);
        Assert.Equal("Priorità interna", riga.Nome);
        Assert.Equal(TipoCampoStrumento.Numero, riga.Tipo);
    }

    [Fact]
    public void CampiCustomDettagliVisual_NonMostraICampiDiSistemaNoti()
    {
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);

        Assert.DoesNotContain(vm.CampiCustomDettagliVisual, c => c.Nome is "Componente" or "Veicolo" or "Funzionalità" or "Descrizione");
        Assert.Contains(vm.CampiCustomDettagliVisual, c => c.Nome == "Progetto");
        Assert.Contains(vm.CampiCustomDettagliVisual, c => c.Nome == "Release obiettivo");
    }

    [Fact]
    public void CopiaDallaPrecedente_CreaOperazioneIndipendente_SenzaEsitiNeEvidenze()
    {
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);
        var originale = vm.RevisioneCorrente!.Condizioni.Last();
        var uidOriginale = originale.Uid;
        originale.Esito = Esito.NonConforme;
        originale.Nota = "Evidenza del test originale";
        originale.DescrizioneProblema = "Problema da non propagare";
        originale.ValoriStrumenti = new Dictionary<string, string>
        {
            ["UnitLogger::Versione"] = "9.1"
        };
        var numeroPrima = vm.RevisioneCorrente.Condizioni.Count;

        vm.CopiaDallaPrecedenteCommand.Execute(null);

        Assert.Equal(numeroPrima, vm.RevisioneCorrente.Condizioni.Count);
        Assert.True(vm.MostraAnteprimaCopiaPrecedente);
        Assert.Contains(vm.AnteprimaCopiaPrecedente, f => f.Campo == "Componente");
        Assert.Contains(vm.AnteprimaCopiaPrecedente, f => f.Campo == "Funzionalità");
        Assert.Contains(vm.AnteprimaCopiaPrecedente, f => f.Campo == "UnitLogger::Versione" && f.Valore == "9.1");
        Assert.DoesNotContain(vm.AnteprimaCopiaPrecedente, f => f.Campo.Contains("Esito") || f.Campo.Contains("Nota"));

        vm.ConfermaCopiaDallaPrecedenteCommand.Execute(null);

        Assert.Equal(numeroPrima + 1, vm.RevisioneCorrente.Condizioni.Count);
        var copia = vm.RevisioneCorrente.Condizioni[^1];
        Assert.NotEqual(uidOriginale, copia.Uid);
        Assert.Null(copia.OperazioneOrigineUid); // indipendente: NON un "Ripeti"
        Assert.Equal(Esito.DaEseguire, copia.Esito);
        Assert.Equal("", copia.Nota);
        Assert.Equal("", copia.DescrizioneProblema);
        Assert.Equal(originale.Componente.ToArray(), copia.Componente.ToArray());
        Assert.Equal(originale.Funzionalita, copia.Funzionalita);
        Assert.Equal("9.1", copia.ValoriStrumenti["UnitLogger::Versione"]);
        copia.ValoriStrumenti["UnitLogger::Versione"] = "9.2";
        Assert.Equal("9.1", originale.ValoriStrumenti["UnitLogger::Versione"]); // clone profondo dei valori
        Assert.True(vm.ModificheNonSalvate);
    }

    [Fact]
    public void AnnullaCopiaDallaPrecedente_RimuoveSoloSeOperazioneCopiaEInvariata()
    {
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);
        var numeroPrima = vm.RevisioneCorrente!.Condizioni.Count;
        var dirtyPrimaCopia = vm.ModificheNonSalvate;
        vm.CopiaDallaPrecedenteCommand.Execute(null);
        vm.ConfermaCopiaDallaPrecedenteCommand.Execute(null);

        Assert.True(vm.PuoAnnullareCopiaPrecedente);
        vm.AnnullaCopiaDallaPrecedenteCommand.Execute(null);

        Assert.Equal(numeroPrima, vm.RevisioneCorrente.Condizioni.Count);
        Assert.Equal(dirtyPrimaCopia, vm.ModificheNonSalvate);
        Assert.False(vm.PuoAnnullareCopiaPrecedente);
        Assert.Contains("rimossa", vm.MessaggioCopiaPrecedente ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnnullaCopiaDallaPrecedente_RipristinaEditorPulitoSeNessunAltroDatoCambia()
    {
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);
        vm.ModificheNonSalvate = false;
        var numeroPrima = vm.RevisioneCorrente!.Condizioni.Count;

        vm.CopiaDallaPrecedenteCommand.Execute(null);
        vm.ConfermaCopiaDallaPrecedenteCommand.Execute(null);
        Assert.True(vm.ModificheNonSalvate);

        vm.AnnullaCopiaDallaPrecedenteCommand.Execute(null);

        Assert.Equal(numeroPrima, vm.RevisioneCorrente.Condizioni.Count);
        Assert.False(vm.ModificheNonSalvate);
    }

    [Fact]
    public void AnnullaCopiaDallaPrecedente_NonRimuoveOperazioneCopiaModificata()
    {
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);
        var numeroPrima = vm.RevisioneCorrente!.Condizioni.Count;
        vm.CopiaDallaPrecedenteCommand.Execute(null);
        vm.ConfermaCopiaDallaPrecedenteCommand.Execute(null);
        var copia = vm.RevisioneCorrente.Condizioni[^1];
        copia.Funzionalita = "Modificata dopo la copia";

        vm.AnnullaCopiaDallaPrecedenteCommand.Execute(null);

        Assert.Equal(numeroPrima + 1, vm.RevisioneCorrente.Condizioni.Count);
        Assert.Equal("Modificata dopo la copia", copia.Funzionalita);
        Assert.False(vm.PuoAnnullareCopiaPrecedente);
        Assert.Contains("è cambiata", vm.MessaggioCopiaPrecedente ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopiaDallaPrecedente_RichiedeNuovaConfermaSeOrigineCambiaDopoAnteprima()
    {
        var vm = CreaViewModelConRevisioneEsito(Esito.Conforme);
        var origine = vm.RevisioneCorrente!.Condizioni.Last();
        var numeroPrima = vm.RevisioneCorrente.Condizioni.Count;

        vm.CopiaDallaPrecedenteCommand.Execute(null);
        origine.Funzionalita = "Aggiornata dopo l'anteprima";
        vm.ConfermaCopiaDallaPrecedenteCommand.Execute(null);

        Assert.Equal(numeroPrima, vm.RevisioneCorrente.Condizioni.Count);
        Assert.Contains(vm.AnteprimaCopiaPrecedente,
            f => f.Campo == "Funzionalità" && f.Valore == "Aggiornata dopo l'anteprima");
        Assert.Contains("conferma di nuovo", vm.MessaggioCopiaPrecedente ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private TestCaseEditorViewModel CreaViewModelConRevisioneEsito(Esito esito)
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.CondizioniVisual[0].Esito = esito;
        vm.SincronizzaEsitoCondizioni();
        return vm;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
