using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Indaga il feedback utente 2026-08-21 (post-implementazione punto 8): "clicco salva modifiche
/// dopo aver inserito l'ticket mi sparisce il testo... rimane solo la severity salvata" — verifica se il
/// ticket viene davvero perso dai dati o solo mai mostrato dopo il salvataggio (gap di UI).</summary>
public sealed class NonConformitaViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_NcVm_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStatoMaster()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var operatore = TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master NC QA", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Tests"
        });
        state.Master.Operatori.Save(new List<Operatore> { operatore });
        Assert.True(state.ImpostaOperatore(operatore.Nome, "1234"));
        return state;
    }

    [Fact]
    public void SalvaNc_DallaPaginaAnomalie_PersisteIlTicketEstaDigitato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var nc = new NonConformita { Id = "NC-0001", TestCaseId = "TC-1", DataApertura = DateTime.Now, Severita = SeveritaAnomalia.High };
        state.NonConformita.Save(nc);

        var vm = new NonConformitaViewModel(state, _ => { });
        var riga = Assert.Single(vm.Righe);
        riga.NuovoTicketRiferimento = "TICKET-5555";

        vm.SalvaNcCommand.Execute(riga);

        Assert.Null(vm.MessaggioErrore);
        var salvata = state.NonConformita.GetById("NC-0001")!;
        Assert.Contains(salvata.CollegamentiPolarion, t => t.Codice == "TICKET-5555");
    }

    /// <summary>Copre il percorso toccato dalla correzione di performance del punto 9 (feedback utente
    /// 2026-08-21): Ricarica() ora costruisce un solo Dictionary&lt;id,TestCase&gt; per l'intera lista invece di
    /// uno per riga (AnomaliaWorkflowService.TestCorrente) — deve continuare a risolvere il test collegato
    /// esattamente come prima, non solo essere più veloce a farlo.</summary>
    [Fact]
    public void Righe_RisolveIlTestCaseCollegatoTramiteLaProvaPiuRecente()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var condizione = new Condizione { Uid = Guid.NewGuid(), Esito = Esito.NonConforme };
        var esecuzione = new Esecuzione { Iterazione = 1, Data = DateTime.Now, Condizioni = new() { condizione }, Esito = Esito.NonConforme };
        var test = new TestCase { Id = "TC-9", Componente = "BCM", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now, Esecuzioni = new() { esecuzione } };
        state.TestCases.Save(test, isNew: true);
        var nc = new NonConformita
        {
            Id = "NC-0002", TestCaseId = "TC-9", DataApertura = DateTime.Now, Severita = SeveritaAnomalia.Low,
            ProveCollegate = new() { new RiferimentoProva { TestId = "TC-9", EsecuzioneIterazione = 1, CondizioneUid = condizione.Uid } }
        };
        state.NonConformita.Save(nc);

        var vm = new NonConformitaViewModel(state, _ => { });

        var riga = Assert.Single(vm.Righe);
        Assert.Equal("TC-9", riga.TestCaseIdVisualizzato);
        Assert.False(riga.TestCaseEliminato);
        Assert.NotNull(riga.TestCase);
    }

    [Fact]
    public void Righe_MostraIlTicketEstaGiaCollegato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var nc = new NonConformita
        {
            Id = "NC-0001", TestCaseId = "TC-1", DataApertura = DateTime.Now, Severita = SeveritaAnomalia.High,
            CollegamentiPolarion = new() { new CollegamentoPolarion { Codice = "TICKET-9999" } }
        };
        state.NonConformita.Save(nc);

        var vm = new NonConformitaViewModel(state, _ => { });
        var riga = Assert.Single(vm.Righe);

        Assert.Contains("TICKET-9999", riga.TicketTesto);
    }

    /// <summary>Copertura dei filtri veicolo/gamma/componente (feedback utente 2026-08-24): applicati sul TestCase
    /// già risolto per la riga, sia sulle Anomalie sia sulle Change Request.</summary>
    [Fact]
    public void FiltroVeicolo_MostraSoloLeAnomalieDelTestCaseConQuelVeicolo()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var tc1 = new TestCase { Id = "TC-1", Componente = "BCM", Veicoli = new() { "Daily 24" } };
        var tc2 = new TestCase { Id = "TC-2", Componente = "BCM", Veicoli = new() { "Alpha 24" } };
        state.TestCases.Save(tc1, isNew: true);
        state.TestCases.Save(tc2, isNew: true);
        state.NonConformita.Save(new NonConformita { Id = "NC-0001", TestCaseId = "TC-1", TestUid = tc1.Uid, DataApertura = DateTime.Now, Severita = SeveritaAnomalia.High });
        state.NonConformita.Save(new NonConformita { Id = "NC-0002", TestCaseId = "TC-2", TestUid = tc2.Uid, DataApertura = DateTime.Now, Severita = SeveritaAnomalia.High });
        var vm = new NonConformitaViewModel(state, _ => { });

        vm.FiltroVeicolo.Popola(new[] { ("Daily 24", "Daily 24"), ("Alpha 24", "Alpha 24") });
        vm.FiltroVeicolo.Opzioni.Single(o => o.Valore == "Daily 24").Selezionato = true;

        var riga = Assert.Single(vm.Righe);
        Assert.Equal("NC-0001", riga.Nc.Id);
    }

    [Fact]
    public void FiltroGamma_RisolveLaGammaDalVeicoloDelTestCaseCollegato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily 24", GammaNome = "LCV Diesel" } });
        var tc1 = new TestCase { Id = "TC-1", Componente = "BCM", Veicoli = new() { "Daily 24" } };
        state.TestCases.Save(tc1, isNew: true);
        state.NonConformita.Save(new NonConformita { Id = "NC-0001", TestCaseId = "TC-1", TestUid = tc1.Uid, DataApertura = DateTime.Now, Severita = SeveritaAnomalia.High });
        var vm = new NonConformitaViewModel(state, _ => { });

        vm.FiltroGamma.Popola(new[] { ("LCV Diesel", "LCV Diesel"), ("HCV Diesel", "HCV Diesel") });
        vm.FiltroGamma.Opzioni.Single(o => o.Valore == "HCV Diesel").Selezionato = true;
        Assert.Empty(vm.Righe);

        vm.FiltroGamma.Opzioni.Single(o => o.Valore == "HCV Diesel").Selezionato = false;
        vm.FiltroGamma.Opzioni.Single(o => o.Valore == "LCV Diesel").Selezionato = true;
        Assert.Single(vm.Righe);
    }

    [Fact]
    public void FiltroEcu_ApplicatoAncheAllaChangeRequest_UsaVeicoliImpattatiPropri()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var templateCr = new TestTemplate { Nome = "Change Request", IsChangeRequest = true };
        state.Master.TestTemplate.Save(new List<TestTemplate> { templateCr });
        state.ChangeRequests.Save(new ChangeRequest { Id = "CR-0001", TemplateUid = templateCr.Uid, DataCreazione = DateTime.Now, EcuImpattate = new() { "BCM" } });
        state.ChangeRequests.Save(new ChangeRequest { Id = "CR-0002", TemplateUid = templateCr.Uid, DataCreazione = DateTime.Now, EcuImpattate = new() { "EDC" } });
        var vm = new NonConformitaViewModel(state, _ => { });

        vm.FiltroEcu.Popola(new[] { ("BCM", "BCM"), ("EDC", "EDC") });
        vm.FiltroEcu.Opzioni.Single(o => o.Valore == "BCM").Selezionato = true;

        var cr = Assert.Single(vm.ChangeRequests);
        Assert.Equal("CR-0001", cr.Id);
    }

    [Fact]
    public void ApriChangeRequest_EsponeDettaglioSenzaInvocareLEditor()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var template = new TestTemplate { Nome = "CR", IsChangeRequest = true };
        var templates = state.Master.TestTemplate.Load();
        templates.Add(template);
        state.Master.TestTemplate.Save(templates);
        var cr = new ChangeRequest { Id = "CR-ROUTE-1", Titolo = "CR prova", TemplateUid = template.Uid };
        state.ChangeRequests.Save(cr);
        string? idModifica = null;
        var vm = new NonConformitaViewModel(state, _ => { }, apriChangeRequest: id => idModifica = id);
        ChangeRequest? dettaglio = null;
        vm.DettaglioChangeRequestRichiesto += richiesta => dettaglio = richiesta;

        vm.ApriChangeRequestCommand.Execute(cr);

        Assert.Same(cr, dettaglio);
        Assert.Equal(cr.Id, dettaglio?.Id);
        Assert.Null(idModifica);
        Assert.Equal(cr.Id, vm.LeggiDettaglioChangeRequest(cr)?.Id);
    }

    [Fact]
    public void ApriChangeRequest_ConNavigazioneDiretta_VaAllEditorInSolaLetturaSenzaDialogo()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var template = new TestTemplate { Nome = "CR", IsChangeRequest = true };
        var templates = state.Master.TestTemplate.Load();
        templates.Add(template);
        state.Master.TestTemplate.Save(templates);
        var cr = new ChangeRequest { Id = "CR-ROUTE-2", Titolo = "CR prova", TemplateUid = template.Uid };
        state.ChangeRequests.Save(cr);
        string? idModifica = null;
        string? idSolaLettura = null;
        var vm = new NonConformitaViewModel(state, _ => { }, apriChangeRequest: id => idModifica = id)
        {
            ApriChangeRequestSolaLettura = id => idSolaLettura = id
        };
        ChangeRequest? dettaglio = null;
        vm.DettaglioChangeRequestRichiesto += richiesta => dettaglio = richiesta;

        vm.ApriChangeRequestCommand.Execute(cr);

        Assert.Equal(cr.Id, idSolaLettura);
        Assert.Null(dettaglio);
        Assert.Null(idModifica);
    }

    [Fact]
    public void ModificaChangeRequest_InvocaLEditorPerUnaCrModificabile()
    {
        var state = CreaStatoMaster();
        var template = new TestTemplate { Nome = "CR", IsChangeRequest = true };
        var templates = state.Master.TestTemplate.Load();
        templates.Add(template);
        state.Master.TestTemplate.Save(templates);
        var cr = new ChangeRequest { Id = "CR-ROUTE-1", Titolo = "CR prova", TemplateUid = template.Uid };
        state.ChangeRequests.Save(cr);
        string? idModifica = null;
        var vm = new NonConformitaViewModel(state, _ => { }, apriChangeRequest: id => idModifica = id);

        vm.ModificaChangeRequestCommand.Execute(cr);

        Assert.Equal(cr.Id, idModifica);
    }

    [Fact]
    public void SalvaModificaRapidaChangeRequest_PersisteCampiEUnaRevisioneSenzaPerdereAmbito()
    {
        var state = CreaStatoMaster();
        var template = new TestTemplate { Nome = "CR", IsChangeRequest = true };
        var templates = state.Master.TestTemplate.Load();
        templates.Add(template);
        state.Master.TestTemplate.Save(templates);
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-QUICK-1", Titolo = "Titolo prima", Descrizione = "Descrizione prima",
            TemplateUid = template.Uid, Stato = StatoChangeRequest.Aperta,
            VeicoliImpattati = new() { "Daily 24" }, EcuImpattate = new() { "BCM" },
        });
        var prima = state.ChangeRequests.GetById("CR-QUICK-1")!;
        var revisioniPrima = prima.Revisioni.Count;
        var vm = new NonConformitaViewModel(state, _ => { });

        var salvata = vm.SalvaModificaRapidaChangeRequest(prima, "Titolo aggiornato", "Descrizione aggiornata", out var errore);

        Assert.True(salvata, errore);
        Assert.Null(errore);
        var dopo = state.ChangeRequests.GetById(prima.Id)!;
        Assert.Equal("Titolo aggiornato", dopo.Titolo);
        Assert.Equal("Descrizione aggiornata", dopo.Descrizione);
        Assert.Equal(new[] { "Daily 24" }, dopo.VeicoliImpattati);
        Assert.Equal(new[] { "BCM" }, dopo.EcuImpattate);
        Assert.Equal(revisioniPrima + 1, dopo.Revisioni.Count);
    }

    [Fact]
    public void SalvaModificaRapidaChangeRequest_SenzaModificheNonAggiungeRevisione()
    {
        var state = CreaStatoMaster();
        var template = new TestTemplate { Nome = "CR", IsChangeRequest = true };
        var templates = state.Master.TestTemplate.Load();
        templates.Add(template);
        state.Master.TestTemplate.Save(templates);
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-QUICK-NOOP", Titolo = "Gateway ADAS", Descrizione = "Verifica compatibilità gateway ADAS",
            TemplateUid = template.Uid, Stato = StatoChangeRequest.Aperta,
            VeicoliImpattati = new() { "Daily 24" }, EcuImpattate = new() { "BCM" },
        });
        var prima = state.ChangeRequests.GetById("CR-QUICK-NOOP")!;
        var revisioni = prima.Revisioni.Count;
        var revisioneDati = prima.RevisioneDati;
        var ultimaModifica = prima.UltimaModifica;
        var vm = new NonConformitaViewModel(state, _ => { });

        var salvata = vm.SalvaModificaRapidaChangeRequest(prima, prima.Titolo, prima.Descrizione, out var errore);

        Assert.True(salvata, errore);
        Assert.Null(errore);
        var dopo = state.ChangeRequests.GetById(prima.Id)!;
        Assert.Equal(revisioni, dopo.Revisioni.Count);
        Assert.Equal(revisioneDati, dopo.RevisioneDati);
        Assert.Equal(ultimaModifica, dopo.UltimaModifica);
    }

    [Fact]
    public void SalvaModificaRapidaChangeRequest_RifiutaTitoloVuotoSenzaScrivere()
    {
        var state = CreaStatoMaster();
        var template = new TestTemplate { Nome = "CR", IsChangeRequest = true };
        var templates = state.Master.TestTemplate.Load();
        templates.Add(template);
        state.Master.TestTemplate.Save(templates);
        state.ChangeRequests.Save(new ChangeRequest { Id = "CR-QUICK-2", Titolo = "Titolo valido", TemplateUid = template.Uid });
        var revisioniPrima = state.ChangeRequests.GetById("CR-QUICK-2")!.Revisioni.Count;
        var vm = new NonConformitaViewModel(state, _ => { });

        var salvata = vm.SalvaModificaRapidaChangeRequest(state.ChangeRequests.GetById("CR-QUICK-2")!, "  ", "Testo", out var errore);

        Assert.False(salvata);
        Assert.False(string.IsNullOrWhiteSpace(errore));
        Assert.Equal("Titolo valido", state.ChangeRequests.GetById("CR-QUICK-2")!.Titolo);
        Assert.Equal(revisioniPrima, state.ChangeRequests.GetById("CR-QUICK-2")!.Revisioni.Count);
    }

    [Fact]
    public void SalvaModificaRapidaChangeRequest_RifiutaDuplicatoSenzaMotivazioneComeEditorCompleto()
    {
        var state = CreaStatoMaster();
        var template = new TestTemplate { Nome = "CR", IsChangeRequest = true };
        var templates = state.Master.TestTemplate.Load();
        templates.Add(template);
        state.Master.TestTemplate.Save(templates);
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-QUICK-DUP-1", Titolo = "Integrazione gateway ADAS", TemplateUid = template.Uid,
            VeicoliImpattati = new() { "Daily 24" }, EcuImpattate = new() { "BCM" },
        });
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-QUICK-DUP-2", Titolo = "Richiesta iniziale", TemplateUid = template.Uid,
            VeicoliImpattati = new() { "Daily 24" }, EcuImpattate = new() { "BCM" },
        });
        var snapshot = state.ChangeRequests.GetById("CR-QUICK-DUP-2")!;
        var revisioniPrima = snapshot.Revisioni.Count;
        var vm = new NonConformitaViewModel(state, _ => { });

        var salvata = vm.SalvaModificaRapidaChangeRequest(snapshot, "Integrazione gateway ADAS urgente", "", out var errore);

        Assert.False(salvata);
        Assert.Contains("CR-QUICK-DUP-1", errore);
        var dopo = state.ChangeRequests.GetById(snapshot.Id)!;
        Assert.Equal("Richiesta iniziale", dopo.Titolo);
        Assert.Equal(revisioniPrima, dopo.Revisioni.Count);
    }

    [Fact]
    public void SalvaModificaRapidaChangeRequest_RifiutaSnapshotStantioSenzaPerdereLaModificaConcorrente()
    {
        var state = CreaStatoMaster();
        var template = new TestTemplate { Nome = "CR", IsChangeRequest = true };
        var templates = state.Master.TestTemplate.Load();
        templates.Add(template);
        state.Master.TestTemplate.Save(templates);
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-QUICK-CONCURRENT", Titolo = "Titolo iniziale", Descrizione = "Descrizione iniziale",
            TemplateUid = template.Uid, Stato = StatoChangeRequest.Aperta,
        });
        var snapshotApertura = state.ChangeRequests.GetById("CR-QUICK-CONCURRENT")!;
        var concorrente = EntityStateFingerprint.Clone(snapshotApertura);
        concorrente.Titolo = "Titolo aggiornato da altro utente";
        state.ChangeRequests.Save(concorrente, snapshotApertura.RevisioneDati);
        var revisioneConcorrente = state.ChangeRequests.GetById(snapshotApertura.Id)!.RevisioneDati;
        var vm = new NonConformitaViewModel(state, _ => { });

        var salvata = vm.SalvaModificaRapidaChangeRequest(snapshotApertura, "Titolo rapido stantio", "Descrizione rapida", out var errore);

        Assert.False(salvata);
        Assert.Contains("modificat", errore, StringComparison.OrdinalIgnoreCase);
        var dopo = state.ChangeRequests.GetById(snapshotApertura.Id)!;
        Assert.Equal("Titolo aggiornato da altro utente", dopo.Titolo);
        Assert.Equal(revisioneConcorrente, dopo.RevisioneDati);
    }

    [Fact]
    public void PuoCambiareStatoChangeRequest_UsaLeTransizioniConcesseDallEditor()
    {
        var state = CreaStatoMaster();
        var template = new TestTemplate { Nome = "CR", IsChangeRequest = true };
        var templates = state.Master.TestTemplate.Load();
        templates.Add(template);
        state.Master.TestTemplate.Save(templates);
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-NO-TRANSITION", Titolo = "Senza responsabile", TemplateUid = template.Uid,
            Stato = StatoChangeRequest.Aperta,
        });
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-REOPEN", Titolo = "Da riaprire", TemplateUid = template.Uid,
            Stato = StatoChangeRequest.Chiusa, ChiusaIl = DateTime.Now, ChiusaDa = "Master NC QA",
        });
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-ARCHIVED", Titolo = "Archiviata", TemplateUid = template.Uid,
            Responsabile = "Master NC QA", Stato = StatoChangeRequest.Validata, Archiviata = true,
        });
        var vm = new NonConformitaViewModel(state, _ => { });
        var senzaTransizione = vm.CreaEditorStatoChangeRequest(state.ChangeRequests.GetById("CR-NO-TRANSITION")!);
        var riapribile = vm.CreaEditorStatoChangeRequest(state.ChangeRequests.GetById("CR-REOPEN")!);

        Assert.NotNull(senzaTransizione);
        // Senza responsabile non si implementa, ma il Master puo' sempre chiudere (con motivo): la chiusura non
        // dipende piu' dalla validazione.
        Assert.False(senzaTransizione!.PuoImplementare || senzaTransizione.PuoRiaprire);
        Assert.True(senzaTransizione.PuoChiudere);
        Assert.True(vm.PuoCambiareStatoChangeRequest(state.ChangeRequests.GetById("CR-NO-TRANSITION")));
        Assert.True(vm.PuoModificareRapidamenteChangeRequest(state.ChangeRequests.GetById("CR-NO-TRANSITION")));
        Assert.NotNull(riapribile);
        Assert.True(riapribile!.PuoRiaprire);
        Assert.True(vm.PuoCambiareStatoChangeRequest(state.ChangeRequests.GetById("CR-REOPEN")));
        Assert.True(vm.PuoModificareChangeRequest(state.ChangeRequests.GetById("CR-REOPEN")));
        Assert.False(vm.PuoModificareRapidamenteChangeRequest(state.ChangeRequests.GetById("CR-REOPEN")));

        var archiviata = state.ChangeRequests.GetById("CR-ARCHIVED")!;
        Assert.False(vm.PuoCambiareStatoChangeRequest(archiviata));
        Assert.False(vm.PuoModificareRapidamenteChangeRequest(archiviata));
        // La Modifica rapida si apre comunque (in sola lettura): nessuna azione di scrittura per una CR archiviata.
        var rapidaArchiviata = vm.CreaEditorStatoChangeRequest(archiviata);
        Assert.NotNull(rapidaArchiviata);
        Assert.False(rapidaArchiviata!.PuoModificare || rapidaArchiviata.PuoChiudere || rapidaArchiviata.PuoRiaprire || rapidaArchiviata.PuoImplementare);
        var editorArchiviata = new ChangeRequestEditorViewModel(state, archiviata.Id, () => { }, _ => { });
        Assert.False(editorArchiviata.PuoModificare);
        Assert.False(editorArchiviata.PuoImplementare);
        Assert.False(editorArchiviata.PuoChiudere);
        Assert.False(editorArchiviata.PuoRiaprire);
        Assert.False(editorArchiviata.PuoArchiviare);
    }

    [Fact]
    public void ChangeRequestAnnullata_NonAccettaModificaRapidaNeTransizioniDiRiattivazione()
    {
        var state = CreaStatoMaster();
        var template = new TestTemplate { Nome = "CR terminale", IsChangeRequest = true };
        var templates = state.Master.TestTemplate.Load();
        templates.Add(template);
        state.Master.TestTemplate.Save(templates);
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-CANCELED-QUICK", Titolo = "CR annullata", TemplateUid = template.Uid,
            Responsabile = "Master NC QA", Stato = StatoChangeRequest.Annullata,
        });

        var snapshot = state.ChangeRequests.GetById("CR-CANCELED-QUICK")!;
        var revisioniPrima = snapshot.Revisioni.Count;
        var vm = new NonConformitaViewModel(state, _ => { });

        Assert.False(vm.PuoCambiareStatoChangeRequest(snapshot));
        Assert.False(vm.PuoModificareRapidamenteChangeRequest(snapshot));
        // La rapida si apre comunque in sola lettura: nessuna azione di scrittura su una CR annullata.
        var rapida = vm.CreaEditorStatoChangeRequest(snapshot);
        Assert.NotNull(rapida);
        Assert.False(rapida!.PuoModificare || rapida.PuoChiudere || rapida.PuoRiaprire || rapida.PuoImplementare);
        Assert.False(vm.SalvaModificaRapidaChangeRequest(snapshot, "Titolo da bloccare", "Descrizione", out var errore));
        Assert.False(string.IsNullOrWhiteSpace(errore));

        var editor = new ChangeRequestEditorViewModel(state, snapshot.Id, () => { }, _ => { });
        Assert.False(editor.PuoModificare);
        Assert.False(editor.PuoImplementare);
        editor.ImplementaCommand.Execute(null);

        var dopo = state.ChangeRequests.GetById(snapshot.Id)!;
        Assert.Equal(StatoChangeRequest.Annullata, dopo.Stato);
        Assert.Equal("CR annullata", dopo.Titolo);
        Assert.Equal(revisioniPrima, dopo.Revisioni.Count);
    }

    [Fact]
    public void TransizioniCrSonoDisabilitateQuandoWorkspaceEInSolaLettura()
    {
        var state = CreaStatoMaster();
        var template = new TestTemplate { Nome = "CR sola lettura", IsChangeRequest = true };
        var templates = state.Master.TestTemplate.Load();
        templates.Add(template);
        state.Master.TestTemplate.Save(templates);
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-READONLY-CLOSE", Titolo = "Da chiudere", TemplateUid = template.Uid,
            Responsabile = "Master NC QA", Stato = StatoChangeRequest.Validata,
        });
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-READONLY-REOPEN", Titolo = "Da riaprire", TemplateUid = template.Uid,
            Stato = StatoChangeRequest.Chiusa,
        });

        var root = state.Paths!.Root;
        WorkspaceReadOnlyService.SetEnabled(root, true);
        try
        {
            Assert.False(state.PuoScrivere);
            var daChiudere = new ChangeRequestEditorViewModel(state, "CR-READONLY-CLOSE", () => { }, _ => { });
            var daRiaprire = new ChangeRequestEditorViewModel(state, "CR-READONLY-REOPEN", () => { }, _ => { });

            Assert.False(daChiudere.PuoChiudere);
            Assert.False(daChiudere.PuoSalvare);
            Assert.False(daRiaprire.PuoRiaprire);
            Assert.False(daRiaprire.PuoSalvare);
        }
        finally
        {
            WorkspaceReadOnlyService.SetEnabled(root, false);
        }
    }
}
