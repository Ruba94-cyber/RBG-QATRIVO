using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Dialogo unico di dettaglio anomalia (pagina Anomalie e pagina Test): validazioni dei campi, permessi,
/// Riapri/Elimina solo Master, optimistic lock con due istanze, chiusura con e senza modifica degli esiti del
/// test collegato (nuova REV con Origine).</summary>
public sealed class AnomaliaDettaglioViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_AnomDett_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var path in new[] { _dir, FailedSaveDraftService.LocalDirectory(_dir) })
            if (Directory.Exists(path)) { try { Directory.Delete(path, true); } catch { } }
    }

    private AppState CreaStato(RuoloOperatore ruolo = RuoloOperatore.Master)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Prova", Ruolo = ruolo, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    private static (Guid C1, Guid C2) CreaTestENc(AppState state, string ncId = "NC-0001", bool conTest = true)
    {
        var c1 = new Condizione { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Diagnosi", Esito = Esito.NonConforme };
        var c2 = new Condizione { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Lettura", Esito = Esito.Conforme };
        if (conTest)
        {
            var rev = new Esecuzione { Iterazione = 1, Data = new DateTime(2026, 9, 1), Operatore = "Mario", Matricola = "Matricola1", Condizioni = { c1, c2 } };
            rev.SincronizzaEsito();
            state.TestCases.Save(new TestCase
            {
                Id = "T-1", Componente = { "BCM" }, Veicolo = "V1", Veicoli = { "V1" }, Assegnatario = "Luigi",
                StatoLavoro = StatoLavoroTest.InCorso, Esecuzioni = new List<Esecuzione> { rev }
            }, isNew: true);
        }
        state.NonConformita.Save(new NonConformita
        {
            Id = ncId, TestCaseId = conTest ? "T-1" : "", EsecuzioneIterazione = 1, Owner = "Mario", Descrizione = "KO",
            Severita = SeveritaAnomalia.Medium, StatoWorkflow = StatoAnomalia.Confermata, DataApertura = DateTime.Now,
            ProveCollegate = conTest ? new() { new RiferimentoProva { TestId = "T-1", EsecuzioneIterazione = 1, CondizioneUid = c1.Uid } } : new()
        });
        return (c1.Uid, c2.Uid);
    }

    private static AnomaliaDettaglioViewModel Apri(AppState state, string ncId = "NC-0001") =>
        new(state, state.NonConformita.GetById(ncId)!);

    // ---------------------------------------------------------------- campi

    [Fact]
    public void Salva_ModificaOwnerESeverita_PersisteEChiede()
    {
        var state = CreaStato();
        CreaTestENc(state);
        var vm = Apri(state);
        var chiusure = 0;
        vm.ChiusuraRichiesta += () => chiusure++;

        vm.Nc.Owner = "Giulia";
        vm.Nc.Priorita = PrioritaOperativa.Major;
        vm.SalvaCommand.Execute(null);

        Assert.Null(vm.Errore);
        Assert.True(vm.Modificato);
        Assert.Equal(1, chiusure);
        var salvata = state.NonConformita.GetById("NC-0001")!;
        Assert.Equal("Giulia", salvata.Owner);
        Assert.Equal(PrioritaOperativa.Major, salvata.Priorita);
    }

    [Fact]
    public void Salva_LavoraSuUnaCopia_LaCacheNonCambiaSeIlSalvataggioFallisce()
    {
        var state = CreaStato();
        CreaTestENc(state);
        var vm = Apri(state);
        vm.Nc.Owner = "Cambiato";
        vm.Nc.Severita = null;

        vm.SalvaCommand.Execute(null);

        Assert.Equal(LocalizationService.Instance.Traduci("SeveritaObbl"), vm.Errore);
        Assert.False(vm.Modificato);
        Assert.Equal("Mario", state.NonConformita.GetById("NC-0001")!.Owner);
    }

    [Theory]
    [InlineData("XYZ-12", "", "FormatoPolarionNonValido")]
    [InlineData("TICKET-123", "non un url", "LinkPolarionNonValido")]
    public void Salva_TicketNonValido_BloccaSenzaScrivere(string codice, string url, string chiave)
    {
        var state = CreaStato();
        CreaTestENc(state);
        var vm = Apri(state);
        vm.NuovoTicketRiferimento = codice;
        vm.NuovoTicketUrl = url;

        vm.SalvaCommand.Execute(null);

        Assert.Equal(LocalizationService.Instance.Traduci(chiave), vm.Errore);
        Assert.Empty(state.NonConformita.GetById("NC-0001")!.CollegamentiPolarion);
    }

    [Fact]
    public void Salva_TicketValido_VieneCollegatoInMaiuscolo()
    {
        var state = CreaStato();
        CreaTestENc(state);
        var vm = Apri(state);
        vm.NuovoTicketRiferimento = "ticket-777";
        vm.NuovoTicketUrl = "https://ticketing.example/TICKET-777";

        vm.SalvaCommand.Execute(null);

        Assert.Null(vm.Errore);
        var t = Assert.Single(state.NonConformita.GetById("NC-0001")!.CollegamentiPolarion);
        Assert.Equal("TICKET-777", t.Codice);
        Assert.Equal("Op Prova", t.CollegatoDa);
    }

    [Fact]
    public void Salva_CollegaATestEsistente_ImpostaTestCaseId_EDaTestNonTrovato_Errore()
    {
        var state = CreaStato();
        CreaTestENc(state, "NC-0001", conTest: true);
        CreaTestENc(state, "NC-0002", conTest: false); // seconda NC senza test (il test T-1 esiste gia')
        var vm = Apri(state, "NC-0002");
        Assert.True(vm.SenzaTestCollegato);

        vm.TestIdDaCollegare = "T-INESISTENTE";
        vm.SalvaCommand.Execute(null);
        Assert.Contains("T-INESISTENTE", vm.Errore);

        vm.TestIdDaCollegare = "T-1";
        vm.SalvaCommand.Execute(null);
        Assert.Null(vm.Errore);
        Assert.Equal("T-1", state.NonConformita.GetById("NC-0002")!.TestCaseId);
    }

    // ---------------------------------------------------------------- permessi

    [Fact]
    public void DettaglioSolaLettura_ConsenteIngressoEsplicitoInModificaSoloQuandoAutorizzato()
    {
        var state = CreaStato();
        CreaTestENc(state);
        var consultazione = new AnomaliaDettaglioViewModel(
            state, state.NonConformita.GetById("NC-0001")!, inizialmenteSolaLettura: true);

        Assert.True(consultazione.SolaLettura);
        Assert.True(consultazione.PuoEntrareInModifica);
        consultazione.EntraInModifica();
        Assert.True(consultazione.PuoModificare);
        Assert.False(consultazione.PuoEntrareInModifica);

        var viewerOp = TestOperatori.Sigilla(new Operatore { Nome = "Viewer Prova", Ruolo = RuoloOperatore.Viewer, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(state.Master.Operatori.Load().Append(viewerOp).ToList());
        Assert.True(state.ImpostaOperatore(viewerOp.Nome, "1234"));
        var viewer = new AnomaliaDettaglioViewModel(
            state, state.NonConformita.GetById("NC-0001")!, inizialmenteSolaLettura: true);
        viewer.EntraInModifica();
        Assert.True(viewer.SolaLettura);
        Assert.False(viewer.PuoEntrareInModifica);
    }

    [Fact]
    public async Task SenzaPuoScrivere_NienteSalvaNeChiudiNeSezioneTest()
    {
        var state = CreaStato(RuoloOperatore.Viewer);
        CreaTestENc(state);
        Assert.False(state.PuoScrivere);
        var vm = Apri(state);
        var chiusure = 0;
        vm.ChiusuraRichiesta += () => chiusure++;

        Assert.True(vm.SolaLettura);
        Assert.False(vm.PuoChiudere);
        Assert.False(vm.MostraSezioneTest);

        vm.Nc.Owner = "Intruso";
        vm.SalvaCommand.Execute(null);
        vm.MotivoChiusura = "x";
        await vm.ChiudiCommand.ExecuteAsync(null);

        Assert.Equal(0, chiusure);
        Assert.False(vm.Modificato);
        var nc = state.NonConformita.GetById("NC-0001")!;
        Assert.Equal("Mario", nc.Owner);
        Assert.NotEqual(StatoAnomalia.Conclusa, nc.StatoWorkflow);
    }

    [Fact]
    public async Task Elimina_SoloMaster()
    {
        var stateStd = CreaStato(RuoloOperatore.Standard);
        CreaTestENc(stateStd);
        var nc = stateStd.NonConformita.GetById("NC-0001")!.Clona();
        NonConformitaRulesService.Chiudi(nc);
        nc.MotivoChiusura = "ok";
        stateStd.NonConformita.Save(nc, nc.RevisioneDati);

        var vmStd = Apri(stateStd);
        Assert.False(vmStd.PuoEliminare); // Riapri e' consentito anche a Standard (vedi Standard_PuoRiaprire...), Elimina solo Master
        await vmStd.EliminaCommand.ExecuteAsync(null);
        Assert.NotNull(stateStd.NonConformita.GetById("NC-0001"));
        Assert.False(vmStd.Modificato);
    }

    [Fact]
    public async Task Master_RiapriPoiElimina()
    {
        var state = CreaStato();
        CreaTestENc(state);
        var nc = state.NonConformita.GetById("NC-0001")!.Clona();
        NonConformitaRulesService.Chiudi(nc);
        nc.MotivoChiusura = "ok";
        state.NonConformita.Save(nc, nc.RevisioneDati);

        var vm = Apri(state);
        Assert.True(vm.PuoRiaprire);
        vm.MotivoRiapertura = "Riapertura di test";
        await vm.RiapriCommand.ExecuteAsync(null);
        Assert.True(vm.Modificato);
        var riaperta = state.NonConformita.GetById("NC-0001")!;
        Assert.NotEqual(StatoAnomalia.Conclusa, riaperta.StatoWorkflow);
        Assert.Equal("", riaperta.MotivoChiusura);

        var vm2 = Apri(state);
        Assert.True(vm2.PuoEliminare);
        await vm2.EliminaCommand.ExecuteAsync(null);
        Assert.True(vm2.Modificato);
        Assert.Null(state.NonConformita.GetById("NC-0001"));
    }

    [Fact]
    public async Task Riapri_SenzaMotivo_NonRiapreELasciaLaNcChiusa()
    {
        var state = CreaStato();
        CreaTestENc(state);
        var nc = state.NonConformita.GetById("NC-0001")!.Clona();
        NonConformitaRulesService.Chiudi(nc);
        nc.MotivoChiusura = "ok";
        state.NonConformita.Save(nc, nc.RevisioneDati);

        var vm = Apri(state);
        vm.MotivoRiapertura = "   ";
        await vm.RiapriCommand.ExecuteAsync(null);

        Assert.False(vm.Modificato);
        Assert.False(string.IsNullOrEmpty(vm.Errore));
        Assert.Equal(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
    }

    [Fact]
    public async Task Riapri_ConMotivo_LoScriveNelloStoricoDellaNc()
    {
        var state = CreaStato();
        CreaTestENc(state);
        var nc = state.NonConformita.GetById("NC-0001")!.Clona();
        NonConformitaRulesService.Chiudi(nc);
        nc.MotivoChiusura = "ok";
        state.NonConformita.Save(nc, nc.RevisioneDati);

        var vm = Apri(state);
        vm.MotivoRiapertura = "Il difetto si ripresenta";
        await vm.RiapriCommand.ExecuteAsync(null);

        var voce = state.NonConformita.GetById("NC-0001")!.Changelog.Last(c => c.Campo == "Riapertura");
        Assert.Contains("Il difetto si ripresenta", voce.ValoreNuovo);
    }

    // ---------------------------------------------------------------- conflitto

    [Fact]
    public void ConDueIstanze_ilSecondoSalvataggioConflitta_ConservaLaBozzaLocaleEIlDialogoResta()
    {
        var state = CreaStato();
        CreaTestENc(state);
        var a = Apri(state);
        var b = Apri(state);
        var chiusureB = 0;
        b.ChiusuraRichiesta += () => chiusureB++;

        a.Nc.Owner = "Da A";
        a.SalvaCommand.Execute(null);
        Assert.True(a.Modificato);

        b.Nc.Owner = "Da B";
        b.SalvaCommand.Execute(null);

        Assert.StartsWith(LocalizationService.Instance.Traduci("AnomaliaConflittoInputPreservato"), b.Errore);
        Assert.True(b.ConflittoInAttesa);
        Assert.Equal(0, chiusureB);          // il dialogo resta aperto
        Assert.False(b.Modificato);
        Assert.Equal("Da B", b.Nc.Owner);    // nessuna perdita dell'input locale
        Assert.Equal("Da A", state.NonConformita.GetById("NC-0001")!.Owner);
        var draftRow = Assert.Single(FailedSaveDraftInspector.ListPage(_dir, 0, 10));
        var savedDraft = JsonIo.TryParse<FailedSaveDraftService.DraftEnvelope>(
            File.ReadAllBytes(draftRow.FilePath), out var parseError)!;
        Assert.Null(parseError);
        Assert.Contains("Da B", savedDraft.PayloadJson);
        Assert.Contains("Mario", savedDraft.BasePayloadJson);
    }

    [Fact]
    public void RecuperaBozzaNcConBaseSuperata_ImportaSoloCampoSceltoNellaVersioneCorrente()
    {
        var state = CreaStato();
        CreaTestENc(state);
        var original = state.NonConformita.GetById("NC-0001")!.Clona();
        var remote = original.Clona();
        remote.Owner = "Da A";
        state.NonConformita.Save(remote, original.RevisioneDati);

        var proposed = original.Clona();
        proposed.Owner = "Da B";
        proposed.Descrizione = "Descrizione non scelta";
        var draftPath = FailedSaveDraftService.Save(_dir, "nonconformita", original.Uid,
            original.RevisioneDati, "Op Prova", proposed, "Conflitto", baseSnapshot: original);
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_dir, 0, 10));
        var vm = Apri(state);
        var candidate = vm.CaricaBozzaRecuperabile(row);

        vm.ApplicaBozzaRecuperata(candidate, new[] { nameof(NonConformita.Owner) });

        Assert.Equal("Da B", vm.Nc.Owner);
        Assert.Equal("KO", vm.Nc.Descrizione);
        Assert.Equal("Da A", state.NonConformita.GetById("NC-0001")!.Owner);
        Assert.True(File.Exists(draftPath));

        vm.SalvaCommand.Execute(null);

        Assert.Null(vm.Errore);
        Assert.Equal("Da B", state.NonConformita.GetById("NC-0001")!.Owner);
        Assert.Equal("KO", state.NonConformita.GetById("NC-0001")!.Descrizione);

    }

    // ---------------------------------------------------------------- chiusura

    [Fact]
    public async Task Chiudi_SenzaMotivo_Errore_NessunaScrittura()
    {
        var state = CreaStato();
        CreaTestENc(state);
        var vm = Apri(state);

        await vm.ChiudiCommand.ExecuteAsync(null);

        Assert.Equal(LocalizationService.Instance.Traduci("ChiudereAnomaliaMotivo"), vm.Errore);
        Assert.NotEqual(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
    }

    [Fact]
    public async Task Chiudi_SenzaModificheAlTest_ChiusuraSempliceNessunaRev()
    {
        var state = CreaStato();
        CreaTestENc(state);
        var revTest = state.TestCases.GetById("T-1")!.RevisioneDati;
        var vm = Apri(state);
        Assert.True(vm.MostraSezioneTest);
        Assert.Equal(2, vm.Condizioni.Count);
        Assert.False(vm.ModificaTestRichiesta);

        vm.MotivoChiusura = "Risolta, nessuna modifica test";
        await vm.ChiudiCommand.ExecuteAsync(null);

        Assert.Null(vm.Errore);
        Assert.True(vm.Modificato);
        var nc = state.NonConformita.GetById("NC-0001")!;
        Assert.Equal(StatoAnomalia.Conclusa, nc.StatoWorkflow);
        Assert.Equal("Risolta, nessuna modifica test", nc.MotivoChiusura);
        var tc = state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!;
        Assert.Single(tc.Esecuzioni);
        Assert.Equal(revTest, tc.RevisioneDati);
    }

    [Fact]
    public async Task Chiudi_ConEsitoCambiato_SenzaOrigine_Errore()
    {
        var state = CreaStato();
        var (c1, _) = CreaTestENc(state);
        var vm = Apri(state);
        vm.Condizioni.Single(c => c.Uid == c1).EsitoNuovo = Esito.Conforme;
        vm.MotivoChiusura = "Risolta";

        await vm.ChiudiCommand.ExecuteAsync(null);

        Assert.True(vm.ModificaTestRichiesta);
        Assert.Equal(LocalizationService.Instance.Traduci("AnomaliaOrigineObbl"), vm.Errore);
        Assert.NotEqual(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
        Assert.Single(state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!.Esecuzioni);
    }

    [Theory]
    [InlineData(OrigineRevisione.Rete)]
    [InlineData(OrigineRevisione.RieseguitoDaNoi)]
    public async Task Chiudi_ConEsitoCambiato_CreaRevConOrigine(OrigineRevisione origine)
    {
        var state = CreaStato();
        var (c1, _) = CreaTestENc(state);
        var vm = Apri(state);
        vm.Condizioni.Single(c => c.Uid == c1).EsitoNuovo = Esito.Conforme;
        vm.StatoLavoroNuovo = StatoLavoroTest.Concluso;
        vm.OrigineSelezionata = vm.OpzioniOrigine.Single(o => o.Valore == origine);
        vm.MotivoChiusura = "Risolta in rete";
        var chiusure = 0;
        vm.ChiusuraRichiesta += () => chiusure++;

        await vm.ChiudiCommand.ExecuteAsync(null);

        Assert.Null(vm.Errore);
        Assert.Equal(1, chiusure);
        Assert.True(vm.Modificato);
        var tc = state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!;
        Assert.Equal(2, tc.Esecuzioni.Count);
        Assert.Equal(origine, tc.Esecuzioni[1].Origine);
        Assert.Equal("NC-0001", tc.Esecuzioni[1].NonConformitaOrigineId);
        Assert.Equal(Esito.NonConforme, tc.Esecuzioni[0].Esito);
        Assert.Equal(StatoLavoroTest.Concluso, tc.StatoLavoro);
        Assert.Equal("Luigi", tc.Assegnatario);
        Assert.Equal(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
    }

    [Fact]
    public async Task Chiudi_ConSoloStatoLavoroCambiato_ServeOrigine_NonCreaRev()
    {
        var state = CreaStato();
        CreaTestENc(state);
        var vm = Apri(state);
        vm.StatoLavoroNuovo = StatoLavoroTest.DaRitestare;
        vm.MotivoChiusura = "Da ritestare";

        await vm.ChiudiCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationService.Instance.Traduci("AnomaliaOrigineObbl"), vm.Errore);

        vm.OrigineSelezionata = vm.OpzioniOrigine.First(o => o.Valore == OrigineRevisione.RieseguitoDaNoi);
        await vm.ChiudiCommand.ExecuteAsync(null);

        Assert.Null(vm.Errore);
        var tc = state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!;
        Assert.Single(tc.Esecuzioni);
        Assert.Equal(StatoLavoroTest.DaRitestare, tc.StatoLavoro);
    }

    [Fact]
    public async Task Chiudi_ConEsitiCambiati_ConflittoSulTest_PreservaLInputERestaAperto()
    {
        var state = CreaStato();
        var (c1, _) = CreaTestENc(state);
        var vm = Apri(state);
        vm.Condizioni.Single(c => c.Uid == c1).EsitoNuovo = Esito.Conforme;
        vm.OrigineSelezionata = vm.OpzioniOrigine[0];
        vm.StatoLavoroNuovo = StatoLavoroTest.Concluso;
        vm.MotivoChiusura = "Risolta";
        var statoInserito = vm.StatoLavoroNuovo;
        var origineInserita = vm.OrigineSelezionata;
        var chiusure = 0;
        vm.ChiusuraRichiesta += () => chiusure++;

        // Un'altra postazione modifica il test dopo l'apertura del dialogo.
        var tc = state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!;
        tc.StatoLavoro = StatoLavoroTest.InAttesa;
        state.TestCases.Save(tc, false, tc.RevisioneDati);

        await vm.ChiudiCommand.ExecuteAsync(null);

        Assert.Contains(LocalizationService.Instance.Traduci("AnomaliaConflittoInputPreservato"), vm.Errore);
        Assert.Equal(0, chiusure);
        Assert.False(vm.Modificato);
        Assert.NotEqual(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
        Assert.Single(state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!.Esecuzioni);
        Assert.Equal(Esito.Conforme, vm.Condizioni.Single(c => c.Uid == c1).EsitoNuovo);
        Assert.Equal(statoInserito, vm.StatoLavoroNuovo);
        Assert.Equal(origineInserita, vm.OrigineSelezionata);
        Assert.Equal("Risolta", vm.MotivoChiusura);
        Assert.True(vm.ModificaTestRichiesta);
    }

    [Fact]
    public async Task Chiudi_ConflittoSullaNc_ConservaBozzaERestaAperto()
    {
        var state = CreaStato();
        CreaTestENc(state);
        var a = Apri(state);
        var b = Apri(state);

        a.Nc.Owner = "Modifica concorrente";
        a.SalvaCommand.Execute(null);
        b.Nc.Owner = "Modifica locale";
        b.MotivoChiusura = "Risolta";
        var chiusure = 0;
        b.ChiusuraRichiesta += () => chiusure++;

        await b.ChiudiCommand.ExecuteAsync(null);

        Assert.Contains(LocalizationService.Instance.Traduci("AnomaliaConflittoInputPreservato"), b.Errore);
        Assert.Contains("Bozza locale conservata", b.Errore);
        Assert.Equal(0, chiusure);
        Assert.Equal("Modifica locale", b.Nc.Owner);
        Assert.Equal("Risolta", b.MotivoChiusura);
        Assert.NotEqual(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
        var draftRow = Assert.Single(FailedSaveDraftInspector.ListPage(_dir, 0, 10));
        var envelope = JsonIo.TryParse<FailedSaveDraftService.DraftEnvelope>(
            File.ReadAllBytes(draftRow.FilePath), out var parseError)!;
        Assert.Null(parseError);
        Assert.Contains("Modifica locale", envelope.PayloadJson);
        Assert.Contains("Mario", envelope.BasePayloadJson);
    }

    [Fact]
    public async Task Chiudi_ConCampiModificatiEREV_SalvaPrimaICampi()
    {
        var state = CreaStato();
        var (c1, _) = CreaTestENc(state);
        var vm = Apri(state);
        vm.Nc.Owner = "Nuovo Owner";
        vm.Condizioni.Single(c => c.Uid == c1).EsitoNuovo = Esito.Conforme;
        vm.OrigineSelezionata = vm.OpzioniOrigine[1];
        vm.MotivoChiusura = "Risolta";

        await vm.ChiudiCommand.ExecuteAsync(null);

        Assert.Null(vm.Errore);
        var nc = state.NonConformita.GetById("NC-0001")!;
        Assert.Equal("Nuovo Owner", nc.Owner);
        Assert.Equal(StatoAnomalia.Conclusa, nc.StatoWorkflow);
        Assert.Equal(2, state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!.Esecuzioni.Count);
    }

    [Fact]
    public void NcSenzaTest_NienteSezioneTest()
    {
        var state = CreaStato();
        CreaTestENc(state, "NC-0009", conTest: false);
        var vm = Apri(state, "NC-0009");
        Assert.False(vm.HaTestCollegato);
        Assert.False(vm.MostraSezioneTest);
        Assert.True(vm.PuoChiudere);
    }

    // ---------------------------------------------------------------- NC non persistita (solo campi)

    [Fact]
    public void NcNonPersistita_SoloCampi_NienteSezioneREV_NienteScrittura_ApplicaARiportaICampi()
    {
        var state = CreaStato();
        var live = new NonConformita { Id = "NC-0777", TestCaseId = "T-X", Owner = "Mario", Severita = SeveritaAnomalia.Low, DataApertura = DateTime.Now };
        var vm = new AnomaliaDettaglioViewModel(state, live, persistita: false);
        Assert.True(vm.NonPersistita);
        Assert.False(vm.HaTestCollegato);
        Assert.False(vm.PuoChiudere);
        Assert.False(vm.PuoRiaprire);
        Assert.False(vm.PuoEliminare);

        vm.Nc.Owner = "Giulia";
        vm.NuovoTicketRiferimento = "TICKET-42";
        vm.SalvaCommand.Execute(null);

        Assert.Null(vm.Errore);
        Assert.Null(state.NonConformita.GetById("NC-0777"));
        Assert.False(vm.Modificato);
        Assert.Equal("Mario", live.Owner);          // l'istanza originale resta intatta finche non si applica
        vm.ApplicaA(live);
        Assert.Equal("Giulia", live.Owner);
        Assert.Contains(live.CollegamentiPolarion, t => t.Codice == "TICKET-42");
    }

    [Fact]
    public void Esecuzione_OrigineEtichetta_VuotaPerOperatore_AltrimentiLabel()
    {
        Assert.Equal("", new Esecuzione { Origine = OrigineRevisione.Operatore }.OrigineEtichetta);
        Assert.Contains(LocalizationService.Instance.Traduci("OrigineRevRete"), new Esecuzione { Origine = OrigineRevisione.Rete }.OrigineEtichetta);
        Assert.Contains(LocalizationService.Instance.Traduci("OrigineRevRieseguito"), new Esecuzione { Origine = OrigineRevisione.RieseguitoDaNoi }.OrigineEtichetta);
    }

    // ---------------------------------------------------------------- parita' con la vecchia pagina Test (NC legacy)

    private static void ChiudiNcSuDisco(AppState state, string motivo = "ok")
    {
        var nc = state.NonConformita.GetById("NC-0001")!.Clona();
        NonConformitaRulesService.Chiudi(nc);
        nc.MotivoChiusura = motivo;
        state.NonConformita.Save(nc, nc.RevisioneDati);
    }

    [Fact]
    public async Task NcLegacySenzaSeverita_ChiudiNonRichiedeClassificazione_ESalvaAltriCampi()
    {
        var state = CreaStato();
        CreaTestENc(state, conTest: false);
        var legacy = state.NonConformita.GetById("NC-0001")!.Clona();
        legacy.Severita = null;
        state.NonConformita.Save(legacy, legacy.RevisioneDati);

        var vmSalva = Apri(state);
        vmSalva.Nc.Owner = "Giulia";
        vmSalva.SalvaCommand.Execute(null);
        Assert.Null(vmSalva.Errore);
        Assert.Equal("Giulia", state.NonConformita.GetById("NC-0001")!.Owner);
        Assert.Null(state.NonConformita.GetById("NC-0001")!.Severita);

        var vm = Apri(state);
        vm.MotivoChiusura = "chiusa legacy";
        await vm.ChiudiCommand.ExecuteAsync(null);
        Assert.Null(vm.Errore);
        Assert.Equal(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
    }

    [Fact]
    public void NcNonPersistitaSenzaSeverita_ResteObbligatoria()
    {
        var state = CreaStato();
        var live = new NonConformita { Id = "NC-0778", TestCaseId = "T-X", Owner = "Mario", DataApertura = DateTime.Now };
        var vm = new AnomaliaDettaglioViewModel(state, live, persistita: false);
        vm.SalvaCommand.Execute(null);
        Assert.Equal(LocalizationService.Instance.Traduci("SeveritaObbl"), vm.Errore);
    }

    [Fact]
    public void DataChiusura_ConflittoRicaricaRecuperoEPersistenza()
    {
        var state = CreaStato();
        CreaTestENc(state, conTest: false);
        var iniziale = state.NonConformita.GetById("NC-0001")!.Clona();
        iniziale.StatoWorkflow = StatoAnomalia.Conclusa;
        iniziale.DataApertura = new DateTime(2026, 1, 1, 10, 0, 0);
        iniziale.DataChiusura = new DateTime(2026, 2, 1, 12, 0, 0);
        state.NonConformita.Save(iniziale, iniziale.RevisioneDati);
        var a = Apri(state);
        var b = Apri(state);
        a.DataChiusuraModificabile = new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero);
        a.SalvaCommand.Execute(null);
        b.DataChiusuraModificabile = new DateTimeOffset(2026, 2, 3, 0, 0, 0, TimeSpan.Zero);
        b.SalvaCommand.Execute(null);
        Assert.True(b.ConflittoInAttesa);
        Assert.Equal(new DateTime(2026, 2, 2, 12, 0, 0), state.NonConformita.GetById("NC-0001")!.DataChiusura);
        var notifiche = 0;
        b.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(b.DataChiusuraModificabile)) notifiche++; };
        b.Ricarica();
        Assert.Equal(new DateTime(2026, 2, 2), b.DataChiusuraModificabile!.Value.Date);
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_dir, 0, 10));
        var draft = b.CaricaBozzaRecuperabile(row);
        b.ApplicaBozzaRecuperata(draft, [nameof(NonConformita.DataChiusura)]);
        Assert.Equal(new DateTime(2026, 2, 3), b.DataChiusuraModificabile!.Value.Date);
        Assert.True(notifiche >= 2);
        b.SalvaCommand.Execute(null);
        Assert.Null(b.Errore);
        var salvata = state.NonConformita.GetById("NC-0001")!;
        Assert.Equal(new DateTime(2026, 2, 3, 12, 0, 0), salvata.DataChiusura);
        Assert.Equal(2, salvata.Changelog.Count(c => c.Campo == "Data chiusura"));
    }

    [Fact]
    public void NcChiusa_MotivoChiusuraModificabileESalvato()
    {
        var state = CreaStato();
        CreaTestENc(state);
        ChiudiNcSuDisco(state, "vecchio motivo");

        var vm = Apri(state);
        Assert.True(vm.Chiusa);
        Assert.True(vm.PuoModificare);
        vm.MotivoChiusura = "  motivo corretto ";
        vm.SalvaCommand.Execute(null);

        Assert.Null(vm.Errore);
        var salvata = state.NonConformita.GetById("NC-0001")!;
        Assert.Equal("motivo corretto", salvata.MotivoChiusura);
        Assert.Equal(StatoAnomalia.Conclusa, salvata.StatoWorkflow);
    }

    [Fact]
    public async Task Standard_PuoRiaprire_ConChangelog_MaNonEliminare()
    {
        var state = CreaStato(RuoloOperatore.Standard);
        CreaTestENc(state);
        ChiudiNcSuDisco(state, "ok");

        var vm = Apri(state);
        Assert.True(vm.PuoRiaprire);
        Assert.False(vm.PuoEliminare);
        vm.MotivoRiapertura = "Riapertura di test";
        await vm.RiapriCommand.ExecuteAsync(null);

        var riaperta = state.NonConformita.GetById("NC-0001")!;
        Assert.NotEqual(StatoAnomalia.Conclusa, riaperta.StatoWorkflow);
        Assert.Null(riaperta.DataChiusura);
        Assert.Equal("", riaperta.MotivoChiusura);
        Assert.Contains(riaperta.Changelog, c => c.Campo == "Riapertura");
    }

    [Fact]
    public async Task TestCollegatoAssente_ChiudiSempliceFunziona()
    {
        var state = CreaStato();
        CreaTestENc(state, conTest: false);
        var nc = state.NonConformita.GetById("NC-0001")!.Clona();
        nc.TestCaseId = "T-ELIMINATO";
        nc.ProveCollegate = new() { new RiferimentoProva { TestId = "T-ELIMINATO", EsecuzioneIterazione = 1, CondizioneUid = Guid.NewGuid() } };
        state.NonConformita.Save(nc, nc.RevisioneDati);

        var vm = Apri(state);
        Assert.False(vm.HaTestCollegato);
        vm.MotivoChiusura = "test non piu' presente";
        await vm.ChiudiCommand.ExecuteAsync(null);
        Assert.Null(vm.Errore);
        Assert.Equal(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
    }

    [Fact]
    public async Task NcDaSegnalazioneRete_SenzaTest_SalvaEChiudeConImpostazioniLegacy()
    {
        var state = CreaStato(RuoloOperatore.Standard);
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-0009", TestCaseId = "", Owner = "Rete", Descrizione = "dalla rete", DaSegnalazioneRete = true,
            StatoWorkflow = StatoAnomalia.Confermata, DataApertura = DateTime.Now, Severita = null
        });
        var vm = Apri(state, "NC-0009");
        Assert.True(vm.SenzaTestCollegato);
        vm.Nc.Owner = "Giulia";
        vm.SalvaCommand.Execute(null);
        Assert.Null(vm.Errore);
        var vm2 = Apri(state, "NC-0009");
        vm2.MotivoChiusura = "risolta in rete";
        await vm2.ChiudiCommand.ExecuteAsync(null);
        Assert.Null(vm2.Errore);
        var salvata = state.NonConformita.GetById("NC-0009")!;
        Assert.Equal(StatoAnomalia.Conclusa, salvata.StatoWorkflow);
        Assert.True(salvata.DaSegnalazioneRete);
    }

    [Fact]
    public void JsonLegacySenzaCampiNuovi_SiApreESiSalva()
    {
        var state = CreaStato();
        var json = "{\"Id\":\"NC-0050\",\"Uid\":\"" + Guid.NewGuid() + "\",\"Owner\":\"Mario\",\"Descrizione\":\"vecchia\",\"StatoWorkflow\":0}";
        Directory.CreateDirectory(state.Paths.NonConformitaDir);
        File.WriteAllText(state.Paths.NonConformitaFile("NC-0050"), json);
        state.NonConformita.RefreshIndex();

        var vm = Apri(state, "NC-0050");
        vm.Nc.Owner = "Luigi";
        vm.SalvaCommand.Execute(null);
        Assert.Null(vm.Errore);
        Assert.Equal("Luigi", state.NonConformita.GetById("NC-0050")!.Owner);
    }

    [Fact]
    public async Task ChiusuraConRevFallitaDopoSalvaCampi_MessaggioDiceCampiSalvatiENcAperta()
    {
        var state = CreaStato();
        var (c1, _) = CreaTestENc(state);
        var vm = Apri(state);
        vm.Nc.Owner = "Giulia"; // campi modificati: vengono salvati prima della chiusura
        vm.MotivoChiusura = "ok";
        vm.OrigineSelezionata = vm.OpzioniOrigine[0];
        vm.Condizioni.Single(c => c.Uid == c1).EsitoNuovo = Esito.Conforme;

        // Un altro utente avanza il Test: ChiudiESalva fallisce con il test atteso diverso (non conflitto sul campi NC).
        var t = state.TestCases.GetById("T-1")!;
        t.ModificatoDa = "altro";
        state.TestCases.Save(t, false, t.RevisioneDati);

        await vm.ChiudiCommand.ExecuteAsync(null);

        Assert.NotEqual(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
        Assert.Equal("Giulia", state.NonConformita.GetById("NC-0001")!.Owner); // campi salvati
        Assert.NotNull(vm.Errore);
    }
}
