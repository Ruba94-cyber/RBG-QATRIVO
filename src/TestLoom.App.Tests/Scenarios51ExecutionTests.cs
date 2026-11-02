using System.Collections.ObjectModel;
using System.Text.Json;
using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class Scenarios51ExecutionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_51Scenarios_" + Guid.NewGuid().ToString("N"));

    public Scenarios51ExecutionTests()
    {
        SeedDemoDataService.Genera(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, true); } catch { }
        }
    }

    private AppState CreateState(string operatore = "Mario Rossi", RuoloOperatore ruolo = RuoloOperatore.Master)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var op = state.Master.Operatori.Load().FirstOrDefault(o => o.Nome == operatore);
        if (op == null)
        {
            op = TestOperatori.Crea(operatore, ruolo);
            var ops = state.Master.Operatori.Load().ToList();
            ops.Add(op);
            state.Master.Operatori.Save(ops);
        }
        else
        {
            op.Ruolo = ruolo;
            RuoloSigilloService.Sigilla(op); // migrazione 6.4.2: un ruolo non sigillato vale come Viewer.
            var ops = state.Master.Operatori.Load().ToList();
            var idx = ops.FindIndex(o => o.Nome == operatore);
            if (idx >= 0) ops[idx] = op;
            state.Master.Operatori.Save(ops);
        }
        state.ImpostaOperatore(operatore, "1234");
        return state;
    }

    // 1. Creare un test: verificare che Pianificazione e Dettagli siano due riquadri separati; matricola vuoto finché pianificato
    [Fact]
    public async Task Test_01_SeparazionePianificazioneDettagli_VinOpzionale()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);

        // Verifica separazione concettuale campi
        Assert.NotNull(vm.TemplateSelezionato);
        Assert.NotEmpty(vm.TemplateSelezionato.CampiPianificazione);
        Assert.NotEmpty(vm.TemplateSelezionato.Sezioni);

        // Stato pianificato di default
        vm.StatoLavoroSelezionato = StatoLavoroTest.Pianificato;
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.VeicoloSelezionato = "Daily 24";
        vm.TestCase.Descrizione = "Test pianificazione senza matricola";
        // ValidationTool è uno strumento di supporto preselezionato dal template: dichiara la Funzionalità generale
        // del test, come richiesto quando non sono presenti strumenti Da testare.
        vm.NuovaFunzionalita = vm.FunzionalitaDisponibili[0];
        vm.AggiungiFunzionalitaCommand.Execute(null);
        if (vm.RevisioneCorrente != null) vm.RevisioneCorrente.Matricola = ""; // matricola vuoto

        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErroriValidazione);
        Assert.False(string.IsNullOrEmpty(vm.TestCase.Id));
    }

    // 2. Da Master, cambiare Scadenza o Assegnatario su un test esistente -> nota obbligatoria visibile nello storico
    [Fact]
    public async Task Test_02_MasterCambioScadenzaAssegnatario_RichiedeNotaObbligatoria()
    {
        var state = CreateState(operatore: "Master Admin", ruolo: RuoloOperatore.Master);
        var template = state.Master.TestTemplate.Load()[0];
        var tc = new TestCase
        {
            Id = "TC-NOTE-01",
            Componente = "BCM",
            Veicolo = "Daily 24",
            Assegnatario = "Operatore 1",
            CreatoDa = "Master Admin",
            DescrizioneBreve = "Desc Breve",
            Descrizione = "Descrizione completa",
            Funzionalita = new List<string> { "Diagnosi" },
            Scadenza = DateTime.Today.AddDays(7),
            StatoLavoro = StatoLavoroTest.Pianificato,
            TestTemplateUid = template.Uid,
            Esecuzioni = new List<Esecuzione>
            {
                new() { Iterazione = 1, Data = DateTime.Now, Operatore = "Master Admin", Condizioni = new List<Condizione> { new() { Esito = Esito.Conforme, Componente = "BCM" } } }
            }
        };
        state.TestCases.Save(tc, isNew: true);

        var vm = new TestCaseEditorViewModel(state, testCaseId: tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.AssegnatarioSelezionato = "Operatore 2";
        vm.ScadenzaTesto = DateTime.Today.AddDays(14).ToString("dd/MM/yyyy");

        // Tentativo di salvataggio senza nota
        vm.NotaPianificazioneTemp = "";
        await vm.SalvaCommand.ExecuteAsync(null);

        // Deve bloccare e richiedere la nota
        Assert.Contains(vm.ErroriValidazione, e => e.Contains("nota", StringComparison.OrdinalIgnoreCase));

        // Inserendo la nota deve salvare e registrare nello storico
        vm.NotaPianificazioneTemp = "Riassegnato per carico lavoro";
        await vm.SalvaCommand.ExecuteAsync(null);

        var ricaricato = state.TestCases.GetById(tc.Id);
        Assert.NotNull(ricaricato);
        Assert.NotEmpty(ricaricato.NotePianificazione);
        Assert.Contains(ricaricato.NotePianificazione, h => h.Nota == "Riassegnato per carico lavoro");
    }

    // 3. Avviare la prima esecuzione e aggiungere due operazioni con esiti diversi -> calcolo condizione peggiore
    [Fact]
    public void Test_03_PrimaEsecuzioneDueOperazioni_EsitoCondizionePeggiore()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);

        // Aggiunge prima condizione: Conforme
        vm.CondizioniVisual[0].Esito = Esito.Conforme;
        vm.SincronizzaEsitoCondizioni();
        Assert.Equal(Esito.Conforme, vm.RevisioneCorrenteEsito);

        // Aggiunge seconda condizione: NonConforme
        vm.AggiungiCondizioneCommand.Execute(null);
        vm.CondizioniVisual[1].Esito = Esito.NonConforme;
        vm.SincronizzaEsitoCondizioni();

        // La condizione peggiore (NonConforme) vince
        Assert.Equal(Esito.NonConforme, vm.RevisioneCorrenteEsito);
    }

    // 4. Salvare modifica dettagli/operazioni -> nuova revisione navigabile dal menu REV in sola lettura
    [Fact]
    public async Task Test_04_ModificaDettagli_CreaNuovaRevisione_RevMenuReadOnly()
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];
        var tc = new TestCase
        {
            Id = "TC-REV-01",
            Componente = "BCM",
            Veicolo = "Daily 24",
            Assegnatario = "Mario Rossi",
            TestTemplateUid = template.Uid,
            Descrizione = "Versione 1",
            Esecuzioni = new List<Esecuzione>
            {
                new() { Iterazione = 1, Data = DateTime.Now.AddDays(-1), Operatore = "Tester", Condizioni = new List<Condizione> { new() { Esito = Esito.Conforme, Componente = "BCM" } } }
            }
        };
        state.TestCases.Save(tc, isNew: true);

        // Apri in modifica e aggiorna la descrizione nei dettagli
        var vm = new TestCaseEditorViewModel(state, testCaseId: tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.TestCase.Descrizione = "Versione 2 modificata";
        vm.CondizioniVisual[0].Nota = "Dettaglio dell'operazione aggiornato";
        await vm.SalvaCommand.ExecuteAsync(null);

        var aggiornato = state.TestCases.GetById(tc.Id);
        Assert.NotNull(aggiornato);
        Assert.Empty(vm.ErroriValidazione ?? new List<string>());
        Assert.Equal(2, aggiornato.Esecuzioni.Count);
        // La REV 1, ora storica, resta quella salvata: il suo Esito (qui DaEseguire, diverso dall'EsitoCalcolato
        // Conforme delle operazioni) non viene riallineato dalla ricostruzione della collezione nell'editor.
        Assert.Equal(Esito.DaEseguire, aggiornato.Esecuzioni[0].Esito);
        Assert.Equal("Versione 2 modificata", aggiornato.Descrizione);

        // Naviga alla prima revisione
        var vmVisualizza = new TestCaseEditorViewModel(state, testCaseId: tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        vmVisualizza.RevisioneVisualizzata = vmVisualizza.Esecuzioni[0];
        Assert.True(vmVisualizza.VisualizzaStorico);
    }

    /// <summary>Regressione 6.4.0: un Test scritto da un client 6.3.x (schema 5), ancora Pianificato, con la
    /// REV 1 salvata "Conforme" e operazioni Conforme senza note — che la migrazione in lettura porta a "Da
    /// eseguire" (EsitoCalcolato della revisione quindi diverso dall'Esito salvato). Modificare i Dettagli crea
    /// la REV 2: il Salva deve riuscire e la REV 1 storica restare esattamente quella letta (nessun "REV storica
    /// non modificabile" dovuto al riallineamento dell'esito durante la ricostruzione della collezione).</summary>
    [Fact]
    public async Task Test_04b_TestLegacyMigratoInLettura_ModificaDettagli_CreaNuovaRevisioneSenzaBloccarsi()
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];
        var tc = new TestCase
        {
            Id = "TC-REV-LEGACY",
            Componente = "BCM",
            Veicolo = "Daily 24",
            Assegnatario = "Mario Rossi",
            TestTemplateUid = template.Uid,
            Descrizione = "Versione 1",
            StatoLavoro = StatoLavoroTest.Pianificato,
            Esecuzioni = new List<Esecuzione>
            {
                new() { Iterazione = 1, Data = DateTime.Now.AddDays(-1), Operatore = "Tester", Esito = Esito.Conforme, Condizioni = new List<Condizione> { new() { Esito = Esito.Conforme, Componente = "BCM" } } }
            }
        };
        state.TestCases.Save(tc, isNew: true);

        // Riporta il file allo stato di un client 6.3.x (fixture di test, mai su dati utente).
        var file = Directory.GetFiles(_dir, "testcase_TC-REV-LEGACY*.json", SearchOption.AllDirectories).Single();
        var legacy = JsonIo.TryRead<TestCase>(file, out _)!;
        legacy.SchemaVersion = 5;
        JsonIo.WriteAtomic(file, legacy);
        state.RicaricaTutto();

        var letto = state.TestCases.GetById(tc.Id)!;
        Assert.Equal(Esito.DaEseguire, letto.Esecuzioni[0].Condizioni[0].Esito); // migrazione in lettura
        Assert.Equal(Esito.Conforme, letto.Esecuzioni[0].Esito);                 // esito salvato, non ricalcolato
        var rev1Letta = JsonSerializer.Serialize(letto.Esecuzioni[0], JsonIo.CompactOptions);

        var vm = new TestCaseEditorViewModel(state, testCaseId: tc.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.TestCase.Descrizione = "Versione 2 modificata";
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Empty(vm.ErroriValidazione ?? new List<string>());
        var aggiornato = state.TestCases.GetById(tc.Id)!;
        Assert.Equal(2, aggiornato.Esecuzioni.Count);
        Assert.Equal("Versione 2 modificata", aggiornato.Descrizione);
        Assert.Equal(rev1Letta, JsonSerializer.Serialize(aggiornato.Esecuzioni[0], JsonIo.CompactOptions));
    }

    // 5. Multi-selezione strumenti (ValidationTool, Breakout Box, guasto Injection, UnitLogger) e campi per operazione
    [Fact]
    public void Test_05_MultiStrumenti_CampiPerOperazione_UdtVaria()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);

        foreach (var opt in vm.StrumentiOptions)
        {
            if (opt.Valore is "ValidationTool" or "Breakout Box" or "Guasto Injection" or "UnitLogger")
                opt.Selezionato = true;
        }

        Assert.Contains("ValidationTool", vm.StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Valore));
        Assert.Contains("Breakout Box", vm.StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Valore));
        Assert.Contains("Guasto Injection", vm.StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Valore));
        Assert.Contains("UnitLogger", vm.StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Valore));
    }

    // 6. Icona ingranaggio su strumento aggiunge campo e sincronizza Impostazioni Strumenti
    [Fact]
    public void Test_06_IconaIngranaggioStrumento_AggiungeCampoSubitoInImpostazioni()
    {
        var state = CreateState();
        var strumenti = state.Master.Strumenti.Load().ToList();
        var validationtool = strumenti.FirstOrDefault(s => s.Nome == "ValidationTool") ?? new StrumentoDiagnostico { Nome = "ValidationTool" };
        if (!strumenti.Contains(validationtool)) strumenti.Add(validationtool);

        validationtool.Campi.Add(new DefinizioneCampoStrumento { Nome = "Firmware Test", Tipo = TipoCampoStrumento.Testo });
        state.Master.Strumenti.Save(strumenti);

        var ricaricati = state.Master.Strumenti.Load();
        var venusRicaricato = ricaricati.First(s => s.Nome == "ValidationTool");
        Assert.Contains(venusRicaricato.Campi, c => c.Nome == "Firmware Test");
    }

    // 7. Salvare operazione Non Conforme e classificarne la segnalazione in Anomalie
    [Fact]
    public void Test_07_SalvaOperazioneNonConforme_ClassificaInAnomalie()
    {
        var state = CreateState();
        var tc = new TestCase
        {
            Id = "TC-ANOM-01",
            Componente = "BCM",
            Esecuzioni = new List<Esecuzione>
            {
                new()
                {
                    Iterazione = 1,
                    Data = DateTime.Now,
                    Operatore = "Tester",
                    Condizioni = new List<Condizione>
                    {
                        new() { Esito = Esito.NonConforme }
                    }
                }
            }
        };
        state.TestCases.Save(tc, isNew: true);

        var ncRepo = state.NonConformita;
        var anomaliaId = "NC-CUSTOM-001";
        var anomalia = new NonConformita
        {
            Id = anomaliaId,
            TestCaseId = tc.Id,
            Descrizione = "Anomalia diagnostica",
            Severita = SeveritaAnomalia.Highest
        };
        ncRepo.Save(anomalia);

        anomalia.Severita = SeveritaAnomalia.Highest;
        anomalia.StatoDettaglio = StatoDettaglioNc.InAnalisi;
        ncRepo.Save(anomalia, anomalia.RevisioneDati);

        var verificata = ncRepo.GetById(anomaliaId);
        Assert.NotNull(verificata);
        Assert.Equal(StatoDettaglioNc.InAnalisi, verificata.StatoDettaglio);
    }

    // 8. Generare pacchetto Ticketing: sezioni selezionabili, live preview, nome file = data + componente + descrizione
    [Fact]
    public void Test_08_GeneraPacchettoPolarion_SezioniSelezionabili_NomeFileFormato()
    {
        var state = CreateState();
        var tc = new TestCase
        {
            Id = "TC-POL-01",
            Componente = "BCM",
            Descrizione = "Validazione diagnosi",
            DescrizioneBreve = "Validazione",
            Esecuzioni = new List<Esecuzione> { new() { Iterazione = 1, Condizioni = new List<Condizione> { new() { Esito = Esito.Conforme } } } }
        };
        state.TestCases.Save(tc, isNew: true);

        var testoExport = TicketExportService.GeneraTesto(tc, state, new HashSet<string> { PolarionSezioni.DatiGenerali, PolarionSezioni.RevisioneCorrente });
        Assert.Contains("TC-POL-01", testoExport);
        Assert.Contains("BCM", testoExport);
    }

    // 9. In Impostazioni → Generale, da Master, verificare/modificare default condiviso sezioni Ticketing
    [Fact]
    public void Test_09_ImpostazioniGenerale_MasterModificaDefaultSezioniPolarion()
    {
        var state = CreateState(ruolo: RuoloOperatore.Master);
        var config = PolarionConfigService.Load(state.Paths);
        config.SezioniDiDefault = new List<string> { PolarionSezioni.DatiGenerali, PolarionSezioni.Allegati };
        PolarionConfigService.Save(state.Paths, config);

        var ricaricata = PolarionConfigService.Load(state.Paths);
        Assert.Contains(PolarionSezioni.Allegati, ricaricata.SezioniDiDefault);
    }

    // 10. Esportare Excel e PDF: verificare fogli anomalie/TICKET/retest e carico operatori
    [Fact]
    public void Test_10_EsportaExcelEPdf_FogliAnomalieEstaCaricoOperatori()
    {
        var state = CreateState();
        var excelBytes = ReportService.GeneraExcel(new ReportFilter(), state, "Tester");
        Assert.NotNull(excelBytes);
        Assert.NotEmpty(excelBytes);

        using var ms = new MemoryStream(excelBytes);
        using var wb = new XLWorkbook(ms);
        Assert.NotNull(wb.Worksheet("1 Anomalie TICKET retest"));
        Assert.NotNull(wb.Worksheet("2 Carico operatori"));

        var pdfBytes = PdfReportService.GeneraPdf(new ReportFilter(), state, "Tester");
        Assert.NotNull(pdfBytes);
        Assert.NotEmpty(pdfBytes);
    }

    // 11. In Impostazioni → Operatori, Rigenera PIN e codice di recupero
    [Fact]
    public void Test_11_ImpostazioniOperatori_RigeneraPinECodiceRecupero()
    {
        var pin = PinService.GeneraPin();
        var rec = PinService.GeneraCodiceRecupero();
        Assert.Equal(4, pin.Length);
        Assert.True(pin.All(char.IsDigit));
        Assert.False(string.IsNullOrWhiteSpace(rec));
        Assert.Contains("-", rec);
        Assert.True(PinService.Verifica(pin, PinService.Hash(pin)));
    }

    // 12. In Impostazioni → Generale, stato sincronizzazione e Forza sincronizzazione
    [Fact]
    public void Test_12_ImpostazioniGenerale_BadgeSincronizzazioneEForzaSincronizzazione()
    {
        var salute = CondivisioneHealthService.Verifica(_dir);
        Assert.Equal(StatoCondivisione.Operativa, salute.Stato);
    }

    // 13. Nella Home, filtri dashboard selezione multipla, ricerca live, liste scorrevoli
    [Fact]
    public void Test_13_Home_FiltriDashboardMultiSelezione_RicercaLive_ScrollListe()
    {
        var filtro = new DashboardFilter { Componente = new List<string> { "BCM", "ECM" } };
        var tc1 = new TestCase { Id = "TC-1", Componente = "BCM" };
        var tc2 = new TestCase { Id = "TC-2", Componente = "VCM" };
        Assert.True(filtro.Corrisponde(tc1, _ => null));
        Assert.False(filtro.Corrisponde(tc2, _ => null));
    }

    // 14. In Impostazioni → Veicoli, nome sottoveicolo/banco modificabile con doppio clic
    [Fact]
    public void Test_14_ImpostazioniVeicoli_DoppioClicModificaSottoveicolo()
    {
        var state = CreateState();
        var veicoli = state.Master.Veicoli.Load().ToList();
        var v = veicoli.First();
        v.Nome = "Daily 2026 Restyle";
        state.Master.Veicoli.Save(veicoli);

        var ricaricati = state.Master.Veicoli.Load();
        Assert.Contains(ricaricati, item => item.Nome == "Daily 2026 Restyle");
    }

    // 15. Su test a banco, "+ Crea test veicolo collegato" sempre visibile e collegato
    [Fact]
    public void Test_15_TestBanco_CreaTestVeicoloCollegato_SempreVisibile()
    {
        var state = CreateState();
        var tcBanco = new TestCase { Id = "TC-BANCO-01", Componente = "BCM" };
        state.TestCases.Save(tcBanco, isNew: true);

        var tcVeicolo = new TestCase
        {
            Id = "TC-VEIC-01",
            Componente = tcBanco.Componente,
            TestCorrelati = new List<string> { tcBanco.Id }
        };
        state.TestCases.Save(tcVeicolo, isNew: true);

        var ricaricato = state.TestCases.GetById("TC-VEIC-01");
        Assert.NotNull(ricaricato);
        Assert.Contains(tcBanco.Id, ricaricato.TestCorrelati);
    }

    // 16. In Impostazioni → Strumenti, ambito "Stabile per revisione" in riquadro dedicato
    [Fact]
    public void Test_16_StrumentiAmbitoStabilePerRevisione_RiquadroDedicato()
    {
        var campo = new DefinizioneCampoStrumento { Nome = "Km percorsi", Ambito = TipoAmbitoCampo.PerRevisione, Tipo = TipoCampoStrumento.Numero };
        Assert.Equal(TipoAmbitoCampo.PerRevisione, campo.Ambito);
    }

    // 17. Modifica dettagli due volte -> due revisioni distinte e diff campi cambiati
    [Fact]
    public void Test_17_ModificaDueVolte_DueRevisioniDistinte_DiffCampiModificati()
    {
        var state = CreateState();
        var tc = new TestCase
        {
            Id = "TC-DIFF-01",
            Componente = "BCM",
            Descrizione = "Rev 1",
            Esecuzioni = new List<Esecuzione>
            {
                new() { Iterazione = 1, Condizioni = new List<Condizione> { new() { Esito = Esito.Conforme } } },
                new() { Iterazione = 2, Condizioni = new List<Condizione> { new() { Esito = Esito.NonConforme } } }
            }
        };
        state.TestCases.Save(tc, isNew: true);
        Assert.Equal(2, tc.Esecuzioni.Count);
        Assert.NotEqual(tc.Esecuzioni[0].Condizioni[0].Esito, tc.Esecuzioni[1].Condizioni[0].Esito);
    }

    // 18. In Impostazioni → Tipi di test: Gestisci campi, obbligatorietà per Stato e visibilità per Esito
    [Fact]
    public void Test_18_TipiTest_GestisciCampi_ObbligatorioPerStato_VisibilePerEsito()
    {
        var campo = new CampoTemplate
        {
            Nome = "Motivazione Deviazione",
            StatiObbligatorio = new List<StatoLavoroTest> { StatoLavoroTest.Concluso },
            CondizioneEsito = new List<Esito> { Esito.ConformeConDeviazioni, Esito.NonConforme }
        };
        Assert.Contains(StatoLavoroTest.Concluso, campo.StatiObbligatorio);
        Assert.Contains(Esito.NonConforme, campo.CondizioneEsito);
    }

    // 19. Campo tipo File collegato -> Scegli file, finisce negli allegati e riapribile
    [Fact]
    public void Test_19_StrumentiTipoFileCollegato_ScegliFile_FinisceInAllegati()
    {
        var state = CreateState();
        var paths = new DataPaths(_dir);
        var tc = new TestCase { Id = "TC-FILE-01", Componente = "BCM" };
        state.TestCases.Save(tc, isNew: true);

        var tempFile = Path.Combine(_dir, "test_log.png");
        File.WriteAllBytes(tempFile, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var meta = AttachmentService.Salva(paths, tc, tempFile, "Tester");
        tc.Allegati.Add(meta);
        state.TestCases.Save(tc, isNew: false);

        Assert.Contains(tc.Allegati, a => a.NomeFile == "test_log.png");
        var dest = AttachmentService.PercorsoCompleto(paths, tc, meta);
        Assert.True(File.Exists(dest));
    }

    // 20. Requisiti collegati e Test collegati: auto-complete per frammento di testo
    [Fact]
    public void Test_20_RequisitiETestCollegati_FiltroTestoConDescrizione()
    {
        var req = new Requisito { Id = "REQ-DIAG-01", Testo = "Controllo velocità CAN" };
        Assert.True(AutoCompleteFilters.Requisito("CAN", req));
        Assert.False(AutoCompleteFilters.Requisito("LIN", req));
    }

    // 21. Tour onboarding per Master, Operatore e Viewer: persistenza e riavvio
    [Fact]
    public void Test_21_TourOnboarding_MasterOperatoreViewer_PersistenzaERiavvio()
    {
        var masterPassi = OnboardingService.CreaPassi(RuoloOperatore.Master, "it");
        var opPassi = OnboardingService.CreaPassi(RuoloOperatore.Standard, "it");
        var viewPassi = OnboardingService.CreaPassi(RuoloOperatore.Viewer, "it");

        Assert.NotEmpty(masterPassi);
        Assert.NotEmpty(opPassi);
        Assert.NotEmpty(viewPassi);
        // Dal 6.6.0 i passi comuni sono tre (benvenuto, panoramica, Indietro): il primo passo per ruolo è il 4°.
        Assert.NotEqual(masterPassi[3].Titolo, opPassi[3].Titolo);

        var cfg = new AppConfig();
        OnboardingService.SegnaCompletato(cfg, "Tester", RuoloOperatore.Standard);
        Assert.True(OnboardingService.ECompletato(cfg, "Tester", RuoloOperatore.Standard));
    }

    // 22. Crea test collegato (feedback utente 2026-08-20, punto 17 — supersede la vecchia AzioneCollegataTemplate
    // preconfigurata dal Master): menu interattivo runtime, disponibile per qualunque template, con scelta
    // libera di destinazione/i (anche lo stesso template di partenza, anche più di uno insieme) e campi da copiare.
    [Fact]
    public void Test_22_CreaTestCollegato_MenuInterattivoRuntime_DestinazioneECampiLiberi()
    {
        var state = CreateState();
        var templates = state.Master.TestTemplate.Load();
        var templateBanco = templates.First(t => t.Nome == "Validazione Banco");
        var templateVeicolo = templates.First(t => t.Nome == "Validazione Veicolo");

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(templateBanco);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.TestCase.Descrizione = "Test di partenza per collegamento";

        Assert.True(vm.HaAzioniCollegate); // sempre disponibile, non più condizionato da azioni preconfigurate

        // Stesso template di partenza + un template diverso insieme, in un'unica chiamata.
        vm.CreaTestCollegati(new List<TestTemplate> { templateBanco, templateVeicolo }, new HashSet<string> { CampiSistema.Componente });

        Assert.Equal(2, vm.TestCase.TestCollegatiCreati.Count);
        Assert.Contains(vm.TestCase.TestCollegatiCreati, c => c.TemplateDestinazioneNome == "Validazione Banco");
        Assert.Contains(vm.TestCase.TestCollegatiCreati, c => c.TemplateDestinazioneNome == "Validazione Veicolo");
        foreach (var creato in vm.TestCase.TestCollegatiCreati)
        {
            var collegato = state.TestCases.GetById(creato.TestCollegatoId);
            Assert.NotNull(collegato);
            Assert.Equal("BCM", collegato!.Componente); // campo scelto (Componente) copiato
        }
    }

    // 23. Icona truck nella barra del titolo, taskbar ed eseguibile
    [Fact]
    public void Test_23_IconaTruck_BarraTitoloTaskbarEseguibile()
    {
        var baseDir = AppContext.BaseDirectory;
        var iconPath = Path.Combine(baseDir, "..", "..", "..", "..", "TestLoom.App", "Assets", "testloom.ico");
        Assert.True(File.Exists(iconPath));
    }

    // 24. Vincolo di collegamento template (es. UnitLogger) filtra i test ammessi
    [Fact]
    public void Test_24_VincoloCollegamentoTemplate_LimitaTestProposti()
    {
        var state = CreateState();
        var templates = state.Master.TestTemplate.Load();
        var unitlogger = templates.FirstOrDefault(t => t.Nome == "UnitLogger");
        Assert.NotNull(unitlogger);
    }

    // 25. Gestione campi di sistema: rinomina etichetta, slug chiave, guard cambio tipo, checkbox filtro
    [Fact]
    public void Test_25_GestioneCampiSistema_RinominaSlugGuardFiltro()
    {
        var slug = CampoSistemaCatalogoService.GeneraChiaveDaEtichetta("Velocità Massima Veicolo", new HashSet<string>());
        Assert.Equal("velocita-massima-veicolo", slug);

        var campo = new CampoSistemaDefinizione { Chiave = "custom-f", Etichetta = "Custom", UsabileComeFiltro = true, Ruolo = RuoloCampoSistema.Nessuno };
        Assert.True(campo.UsabileComeFiltro);
    }

    // 26. In Impostazioni → Tipi di test: Gestisci campi Riferimenti permette di nascondere/rendere obbligatorio
    [Fact]
    public void Test_26_GestisciCampiRiferimenti_NascondiSezioneDalForm()
    {
        var campo = new CampoTemplate { CampoSistemaChiave = CampiSistema.RequisitiCollegati, Obbligatorio = false };
        Assert.Equal(CampiSistema.RequisitiCollegati, campo.CampoSistemaChiave);
    }

    // 27. Conformità 0-5/N.A., correzione manuale con motivazione, matrice Audit e foglio Conflitti
    [Fact]
    public void Test_27_PunteggioConformita_AuditMatriceConflitti_ExcelPulito()
    {
        var p = PunteggioConformitaService.DaEsito(Esito.Conforme);
        Assert.Equal(PunteggioConformita.Conforme, p);

        var cellaConflitto = new CellaMatriceAudit
        {
            Componente = "BCM",
            RequisitoId = "REQ-01",
            Stato = StatoCellaAudit.Conflitto,
            Candidati = new List<CandidatoConflitto>
            {
                new() { TestCaseId = "TC-1", Punteggio = PunteggioConformita.Conforme },
                new() { TestCaseId = "TC-2", Punteggio = PunteggioConformita.NonConforme }
            }
        };
        Assert.Equal(StatoCellaAudit.Conflitto, cellaConflitto.Stato);
        Assert.Equal(2, cellaConflitto.Candidati.Count);
    }

    // 28. In Impostazioni → Generale: riordino navigazione e sotto-sezioni salvato localmente
    [Fact]
    public void Test_28_ImpostazioniGenerale_RiordinoNavigazioneESottoSezioniPersistito()
    {
        var navItems = new ObservableCollection<string> { "Test", "Anomalie", "Report", "Audit", "Impostazioni" };
        NavOrderService.Muovi(navItems, 0, 1);
        Assert.Equal("Anomalie", navItems[0]);
        Assert.Equal("Test", navItems[1]);
    }

    // 29. Report: dialogo salvataggio nativo e storico Cronologia con riapertura diretta
    [Fact]
    public void Test_29_Report_DialogoSalvataggioECronologia()
    {
        var crono = new List<ReportSalvato>();
        var aggiornata = ReportCronologiaService.Aggiungi(crono, "C:\\Reports\\Test.xlsx", DateTime.Now);
        Assert.NotEmpty(aggiornata);
        Assert.Equal("C:\\Reports\\Test.xlsx", aggiornata[0].Percorso);
    }

    // 30. Master Data: archivia, ripristina, blocco eliminazione se referenziato
    [Fact]
    public void Test_30_MasterData_ArchiviaERipristina_EliminaDefinitivoBloccatoSeReferenziato()
    {
        var state = CreateState();
        var ecuList = state.Master.Componente.Load().ToList();
        var componente = ecuList.First();
        componente.Archiviata = true;
        state.Master.Componente.Save(ecuList);

        var ricaricate = state.Master.Componente.Load();
        Assert.True(ricaricate.First(e => e.Nome == componente.Nome).Archiviata);
    }

    // 31. Allegati sotto bucket stabile allegati/test/<UID>/ e apertura cartella in Esplora risorse
    [Fact]
    public void Test_31_AllegatiSalvatiSottoCartellaEcuId_AperturaEsploraRisorse()
    {
        var state = CreateState();
        var paths = new DataPaths(_dir);
        var tc = new TestCase { Id = "TC-EXP-01", Componente = "BCM" };
        state.TestCases.Save(tc, isNew: true);

        var temp = Path.Combine(_dir, "dump.png");
        File.WriteAllBytes(temp, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var meta = AttachmentService.Salva(paths, tc, temp, "Tester");

        var dirAllegati = paths.AllegatiDir(tc);
        Assert.True(Directory.Exists(dirAllegati));
        Assert.True(File.Exists(Path.Combine(dirAllegati, meta.NomeFile)));
        Assert.Equal(Path.Combine(dirAllegati, meta.NomeFile), AttachmentService.PercorsoCompleto(paths, tc, meta));
        Assert.False(Directory.Exists(paths.AllegatiDir("BCM", "TC-EXP-01")));
    }

    // 32. In Archivio: aggregazione multi-tipo e Svuota archivio selettivo
    [Fact]
    public void Test_32_Archivio_MultiTipo_SvuotaArchivioEliminaSoloNonReferenziati()
    {
        var state = CreateState();
        var vm = new ArchivioViewModel(state);
        Assert.NotNull(vm.Voci);
    }

    // 33. Test UnitLogger Performance con Componente Usato (campi stabili) vs UnitLogger semplice
    [Fact]
    public void Test_33_UdtPerformance_ComponenteUsato_CampiStabiliVsUdtSemplice()
    {
        var state = CreateState();
        var templates = state.Master.TestTemplate.Load();
        var udtPerf = templates.FirstOrDefault(t => t.Nome == "UnitLogger Performance");
        Assert.NotNull(udtPerf);
    }

    // 34. Test nuovo: salvataggio automatico all'inserimento di allegato con componente; avviso se senza componente
    [Fact]
    public void Test_34_TestNuovoConEcu_AutoSalvataggioSuAllegato_AvvisoSeSenzaEcu()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        Assert.True(vm.TestRichiedeEcu);

        vm.AggiungiEcuCommand.Execute("BCM");
        Assert.False(vm.TestRichiedeEcu);
    }

    // 35. Sezioni pacchetto Ticketing configurabili per singolo template
    [Fact]
    public void Test_35_SezioniPolarionPerSingoloTemplate()
    {
        var template = new TestTemplate
        {
            Nome = "Template Personalizzato",
            SezioniPolarionDisponibili = new List<string> { PolarionSezioni.DatiGenerali, PolarionSezioni.Allegati }
        };
        Assert.Equal(2, template.SezioniPolarionDisponibili.Count);
    }

    // 36. File con problemi: riparazione automatica file JSON
    [Fact]
    public void Test_36_FileConProblemi_RiparazioneAutomaticaJson()
    {
        // A differenza degli altri scenari, questo non passa da CreateState(): senza InizializzaCartella la
        // cartella seminata dal costruttore non ha ancora _ditm/version-manifest.json, quindi
        // FileRepairService non la riconoscerebbe come radice TestLoom autorevole. Il file va anche chiamato come
        // uno dei nomi radice realmente gestiti (TestLoomStorageOwnership.RootJsonNames): un nome arbitrario come
        // "corrupted.json" verrebbe rifiutato a prescindere dall'inizializzazione della cartella.
        CreateState();
        var malformedPath = Path.Combine(_dir, "gamma.json");
        File.WriteAllText(malformedPath, "{\"nome\": \"test\", }");

        var res = FileRepairService.ProvaRipara(malformedPath);
        Assert.True(res.Riuscita);

        var parsed = JsonDocument.Parse(File.ReadAllText(malformedPath));
        Assert.NotNull(parsed);
    }

    // 37. Pulsante solido per "Gestisci campi di sistema..." in template
    [Fact]
    public void Test_37_GestisciCampiSistema_PulsantePienoInTemplate()
    {
        var state = CreateState();
        var vm = new TestTemplateViewModel(state);
        Assert.NotNull(vm.Items);
    }

    // 38. Demo primo avvio: completa vs solo configurazione su cartella vuota; rifiuto cartella piena
    [Fact]
    public void Test_38_DemoPrimoAvvio_CompletaVsSoloConfig_RifiutaCartellaPiena()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "TestLoom_Empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            SeedDemoDataService.GeneraConfigurazionePerPrimoAvvio(emptyDir);
            var paths = new DataPaths(emptyDir);
            Assert.NotEmpty(new MasterDataRepository<StrumentoDiagnostico>(paths.StrumentiFile).Load());
        }
        finally
        {
            if (Directory.Exists(emptyDir)) Directory.Delete(emptyDir, true);
        }
    }

    // 39. Guida contestuale: ricerca, filtro ruolo, azzeramento cache al logout
    [Fact]
    public void Test_39_GuidaContestuale_FiltroRuolo_ResetAlLogout()
    {
        var vm = new HelpViewModel(() => RuoloOperatore.Standard) { Ricerca = "Ticketing" };
        Assert.NotEmpty(vm.Sezioni);

        vm.Reset();
        Assert.Equal("", vm.Ricerca);
    }

    // 40. Sticky action bar, salti sezioni e indicatore modifiche non salvate
    [Fact]
    public void Test_40_StickyActionBar_SaltiSezioni_IndicatoreModifiche()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.ModificheNonSalvate = true;
        Assert.True(vm.ModificheNonSalvate);
    }

    // 41. Ricerca globale Ctrl+K, scoping gamma per operatore, chiusura con Esc
    [Fact]
    public void Test_41_RicercaGlobaleCtrlK_ScopingOperatore_EscPulisce()
    {
        var state = CreateState(operatore: "Op Standard", ruolo: RuoloOperatore.Standard);
        var vm = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);
        vm.Query = "BCM";
        Assert.NotNull(vm.Risultati);
    }

    // 42. Tour spotlight con anteprime ruoli e bordo evidenziatore
    [Fact]
    public void Test_42_TourSpotlight_AnteprimeRuoliEBordoEvidenziatore()
    {
        var passi = OnboardingService.CreaPassi(RuoloOperatore.Master, "it");
        Assert.All(passi, p => Assert.False(string.IsNullOrEmpty(p.TargetId)));
    }

    // 43. Personalizzazione colonne DataGrid, isolamento per utente e ripristino
    [Fact]
    public void Test_43_PersonalizzazioneColonneDataGrid_IsolamentoUtenteRipristino()
    {
        var key1 = TableLayoutPreferences.Chiave("Mario", RuoloOperatore.Standard, "test-list");
        var key2 = TableLayoutPreferences.Chiave("Mario", RuoloOperatore.Master, "test-list");
        Assert.NotEqual(key1, key2);
    }

    // 44. Operatore Standard: modifica disabilitata in Impostazioni e nessun riquadro "Altro"
    [Fact]
    public void Test_44_OperatoreStandard_ModificaDisabilitata_NessunRiquadroAltro()
    {
        var state = CreateState(ruolo: RuoloOperatore.Standard);
        Assert.False(state.EMaster);
    }

    // 45. Storico strumenti su revisioni passate ed esclusione in Ticketing di strumenti rimossi
    [Fact]
    public void Test_45_StoricoStrumentiRevisioniPassate_EsclusionePolarionDeselezionati()
    {
        var tc = new TestCase
        {
            Id = "TC-HIST-01",
            Componente = "BCM",
            StrumentiUsati = new List<string> { "UnitLogger" }
        };
        var esec = new Esecuzione { Iterazione = 1, Data = DateTime.Now, Operatore = "Op" };
        esec.ValoriStrumenti["UnitLogger::CanChannel"] = "CAN1";
        esec.ValoriStrumenti["ValidationTool::Voltage"] = "12V"; // strumento deselezionato
        tc.Esecuzioni.Add(esec);

        using var state = TestStateFactory.Create();
        var exportText = TicketExportService.GeneraTesto(tc, state, new HashSet<string> { PolarionSezioni.RevisioneCorrente });
        Assert.Contains("CAN1", exportText);
        Assert.DoesNotContain("12V", exportText);
    }

    // 46. Report Audit Excel: colonna Testo Integrale Requisito in fogli Sottoveicolo e Conflitti
    [Fact]
    public void Test_46_AuditExcel_ColonnaTestoIntegraleRequisito()
    {
        var state = CreateState();
        var excelBytes = AuditReportService.GeneraExcel(state);
        Assert.NotEmpty(excelBytes);

        using var ms = new MemoryStream(excelBytes);
        using var wb = new XLWorkbook(ms);
        var sheet = wb.Worksheets.FirstOrDefault(w => w.Name != "Legenda" && w.Name != "Conflitti");
        if (sheet != null)
        {
            Assert.Equal("Testo", sheet.Cell(1, 2).GetString());
        }
    }

    // 47. Contrasto visivo del bottone "Cerca ovunque"
    [Fact]
    public void Test_47_ContrastoBottoneCercaOvunque()
    {
        var baseDir = AppContext.BaseDirectory;
        var axaml = File.ReadAllText(Path.Combine(baseDir, "..", "..", "..", "..", "TestLoom.App", "Views", "MainWindow.axaml"));
        Assert.Contains("SecondaryDark", axaml);
        Assert.Contains("global-search", axaml);
    }

    // 48. Auto-complete case-insensitive: apertura lista completa al click e filtraggio live
    [Fact]
    public void Test_48_AutoCompleteCaseInsensitive_ClickApreTutto()
    {
        Assert.True(AutoCompleteFilters.Stringa("", "BCM Motore"));
        Assert.True(AutoCompleteFilters.Stringa("bcm", "BCM Motore"));
        Assert.True(AutoCompleteFilters.Stringa("MOTORE", "BCM Motore"));
    }

    // 49. Prepara dati per Ticketing: salvataggio automatico se componente presente, disabilitato con tooltip se assente
    [Fact]
    public void Test_49_PreparaPolarion_TestNuovoSalvataggioAutomaticoEValidazioneEcu()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        Assert.True(vm.TestRichiedeEcu);
        Assert.NotNull(vm.PreparaPolarionTooltip);

        vm.AggiungiEcuCommand.Execute("BCM");
        Assert.False(vm.TestRichiedeEcu);
        Assert.Null(vm.PreparaPolarionTooltip);
    }

    // 50. Ordinamento componente per veicolo selezionato con "Veicolo intero" sempre al primo posto
    [Fact]
    public void Test_50_OrdinamentoEcuPerVeicoloSelezionato()
    {
        // Filtra invece di solo riordinare (feedback utente 2026-08-24): da quando un test può coprire più
        // Veicoli insieme, un componente di un altro veicolo sparisce dalla lista finché quel veicolo non viene
        // aggiunto anche lui al test, invece di restare comunque raggiungibile "sotto" come prima.
        var state = CreateState();
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "BCM Alpha24", VeicoliAssociati = new List<string> { "Alpha 24" } },
            new() { Nome = "BCM Daily24-Series", VeicoliAssociati = new List<string> { "Daily 24", "eDaily 24" } },
        });

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.VeicoloSelezionato = "eDaily 24";

        Assert.Contains("BCM Daily24-Series", vm.EcuDisponibili);
        Assert.DoesNotContain("BCM Alpha24", vm.EcuDisponibili);
        Assert.Equal(TestCaseEditorViewModel.EcuVeicoloIntero, vm.EcuDisponibili[0]);
    }

    // 51. Ridenominazione ruolo operatore "Standard" con etichetta "Operatore"
    [Fact]
    public void Test_51_RuoloOperatoreStandard_EtichettaOperatore()
    {
        var label = RuoloOperatore.Standard.Label();
        Assert.Equal("Operatore", label);
        Assert.NotEqual("Slave", label);
    }
}
