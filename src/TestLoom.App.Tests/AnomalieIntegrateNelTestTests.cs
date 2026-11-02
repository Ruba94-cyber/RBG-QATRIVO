using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public sealed class AnomalieIntegrateNelTestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_AnomalieTest_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    [Fact]
    public async Task Conclusione_BloccataFincheLaSegnalazioneEffettivaNonEGestita()
    {
        var (state, tc) = CreaTestNegativo();
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.StatoLavoroSelezionato = StatoLavoroTest.Concluso;

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.HaSegnalazioniTest);
        Assert.Contains(vm.ErroriValidazione, e => e.Contains("classifica", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClassificazioneNelTest_CreaUnaSolaAnomaliaCollegataAllaProva()
    {
        var (state, tc) = CreaTestNegativo();
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var segnalazione = Assert.Single(vm.SegnalazioniTest);
        segnalazione.Severita = SeveritaAnomalia.High;

        vm.CreaAnomaliaDaTestCommand.Execute(segnalazione);

        var anomalia = Assert.Single(vm.AnomalieTestAperte).Nc;
        Assert.Equal(tc.Id, anomalia.TestCaseId);
        Assert.Equal(segnalazione.Condizione.Uid, Assert.Single(anomalia.ProveCollegate).CondizioneUid);
        Assert.False(vm.HaSegnalazioniTest);
    }

    [Fact]
    public void Ripeti_CopiaICampiERegistraLaRadice_SenzaSostituireNuovaOperazione()
    {
        var (state, tc) = CreaTestNegativo();
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var origine = vm.CondizioniVisual[0];
        origine.ValoriStrumenti["ValidationTool::Comando"] = "22 F1 90";

        vm.RipetiCondizioneCommand.Execute(origine);
        var ripetizione = vm.CondizioniVisual[1];
        vm.AggiungiCondizioneCommand.Execute(null);
        var nuova = vm.CondizioniVisual[2];

        Assert.Equal(origine.Uid, ripetizione.OperazioneOrigineUid);
        Assert.Equal("22 F1 90", ripetizione.ValoriStrumenti["ValidationTool::Comando"]);
        Assert.Null(nuova.OperazioneOrigineUid);
        Assert.Empty(nuova.ValoriStrumenti);
    }

    [Fact]
    public async Task ClassificazioneESalvataggio_PersistonoTestSegnalazioneEAnomaliaInsieme()
    {
        var (state, tc) = CreaTestNegativo();
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var riga = Assert.Single(vm.SegnalazioniTest);
        riga.Severita = SeveritaAnomalia.High;
        vm.CreaAnomaliaDaTestCommand.Execute(riga);

        // Non porta lo StatoLavoro a Concluso qui: da questa modifica un Test con un'anomalia ancora aperta
        // (StatoWorkflow appena creato = Confermata, non Conclusa) non può più concludersi — comportamento
        // testato a parte in ConclusioneBloccata_FincheAnomaliaCollegataResta. Questo test verifica solo che
        // segnalazione classificata + anomalia si salvino insieme.
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Empty(vm.ErroriValidazione);
        Assert.Single(state.NonConformita.GetAll());
        Assert.Single(state.Segnalazioni.GetAll(), s => s.Stato == StatoSegnalazione.Classificata);
    }

    [Fact]
    public async Task RipetizioneConforme_ChiudeAnomaliaConMotivazioneObbligatoria()
    {
        var (state, tc) = CreaTestNegativo();
        var origine = tc.Esecuzioni[0].Condizioni[0];
        var seg = new SegnalazioneDaClassificare
        {
            TestId = tc.Id, CondizioneUid = origine.Uid, EsecuzioneIterazione = 1,
            Stato = StatoSegnalazione.Classificata, AnomaliaUid = Guid.NewGuid()
        };
        origine.SegnalazioneUid = seg.Uid;
        state.TestCases.Save(tc, isNew: false, revisioneAttesa: tc.RevisioneDati);
        var nc = new NonConformita
        {
            Uid = seg.AnomaliaUid!.Value, Id = "NC-0001", TestCaseId = tc.Id, DataApertura = DateTime.Now,
            Severita = SeveritaAnomalia.High,
            ProveCollegate = new() { new RiferimentoProva { TestId = tc.Id, EsecuzioneIterazione = 1, CondizioneUid = origine.Uid } }
        };
        state.Segnalazioni.Save(seg);
        state.NonConformita.Save(nc);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.RipetiCondizioneCommand.Execute(vm.CondizioniVisual[0]);
        vm.CondizioniVisual[1].Esito = Esito.Conforme;
        vm.SincronizzaEsitoCondizioni();
        Assert.Single(vm.AnomalieTestAperte).Nc.MotivoChiusura = "Fix verificato sulla nuova revisione";

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Empty(vm.ErroriValidazione);
        var salvata = state.NonConformita.GetById(nc.Id)!;
        Assert.Equal(StatoNonConformita.Chiusa, salvata.Stato);
        Assert.Equal(StatoAnomalia.Conclusa, salvata.StatoWorkflow);
        Assert.Contains("Fix verificato", salvata.MotivoChiusura);
    }

    [Fact]
    public async Task ConclusioneBloccata_FincheAnomaliaCollegataResta()
    {
        var (state, tc) = CreaTestNegativo();
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var riga = Assert.Single(vm.SegnalazioniTest);
        riga.Severita = SeveritaAnomalia.High;
        vm.CreaAnomaliaDaTestCommand.Execute(riga);
        vm.StatoLavoroSelezionato = StatoLavoroTest.Concluso;

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Contains(vm.ErroriValidazione, e => e.Contains("anomalie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TestGiaConcluso_ConOperazioneNokEAnomaliaCreata_SiSalvaEPassaADaRitestare()
    {
        // Regressione 6.5.1: test Concluso su disco, in modifica un'operazione diventa NOK e si crea l'anomalia;
        // il Salva restava bloccato da "anomalie collegate ancora aperte" anche con l'anomalia correttamente creata.
        var (state, tc) = CreaTestNegativo();
        tc.StatoLavoro = StatoLavoroTest.Concluso;
        tc.Esecuzioni[0].Condizioni[0].Esito = Esito.Conforme;
        tc.Esecuzioni[0].Esito = Esito.Conforme;
        state.TestCases.Save(tc, isNew: false, revisioneAttesa: tc.RevisioneDati);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.CondizioniVisual[0].Esito = Esito.NonConforme;
        vm.CondizioniVisual[0].DescrizioneProblema = "Risposta errata";
        vm.SincronizzaEsitoCondizioni();
        var riga = Assert.Single(vm.SegnalazioniTest);
        riga.Severita = SeveritaAnomalia.High;
        vm.CreaAnomaliaDaTestCommand.Execute(riga);
        Assert.Equal(StatoLavoroTest.Concluso, vm.StatoLavoroSelezionato);

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Empty(vm.ErroriValidazione);
        Assert.Single(state.NonConformita.GetAll());
        Assert.Equal(StatoLavoroTest.DaRitestare, state.TestCases.GetById(tc.Id)!.StatoLavoro);
    }

    /// <summary>Test con storia REV1 NOK (segnalazione + anomalia poi chiusa) e REV2 Conforme: le condizioni di REV2
    /// sono cloni di REV1 (stesso Uid, stesso SegnalazioneUid). Restituisce (stato, test, Uid anomalia vecchia).</summary>
    private (AppState State, TestCase Test, Guid VecchiaNc) CreaTestNokPoiConforme(StatoLavoroTest statoLavoro)
    {
        var (state, tc) = CreaTestNegativo(salva: false);
        var origine = tc.Esecuzioni[0].Condizioni[0];
        var seg = new SegnalazioneDaClassificare
        {
            TestId = tc.Id, CondizioneUid = origine.Uid, EsecuzioneIterazione = 1,
            Stato = StatoSegnalazione.Classificata, AnomaliaUid = Guid.NewGuid()
        };
        origine.SegnalazioneUid = seg.Uid;
        var rev2 = new Esecuzione
        {
            Iterazione = 2, Data = DateTime.Now.AddMinutes(1), Esito = Esito.Conforme,
            Condizioni = new() { new Condizione { Uid = origine.Uid, SegnalazioneUid = seg.Uid, Esito = Esito.Conforme, Componente = "BCM" } }
        };
        tc.Esecuzioni.Add(rev2);
        tc.StatoLavoro = statoLavoro;
        state.TestCases.Save(tc, isNew: true);
        var vecchia = new NonConformita
        {
            Uid = seg.AnomaliaUid!.Value, Id = "NC-0001", TestCaseId = tc.Id, DataApertura = DateTime.Now.AddMinutes(-5),
            Severita = SeveritaAnomalia.High, StatoWorkflow = StatoAnomalia.Conclusa, DataChiusura = DateTime.Now,
            MotivoChiusura = "Risolto in REV2",
            ProveCollegate = new() { new RiferimentoProva { TestId = tc.Id, EsecuzioneIterazione = 1, CondizioneUid = origine.Uid } }
        };
        state.Segnalazioni.Save(seg);
        state.NonConformita.Save(vecchia);
        return (state, tc, vecchia.Uid);
    }

    [Fact]
    public async Task NuovoPassaggioANok_DopoRevConformeEAnomaliaChiusa_CreaNuovaSegnalazioneEAnomalia()
    {
        // Regressione 6.5.1: REV1 NOK (segnalazione + anomalia poi chiusa) -> REV2 Conforme -> di nuovo NOK. La condizione
        // clonata conserva il SegnalazioneUid della vecchia segnalazione: prima non veniva creata alcuna nuova segnalazione
        // e il test si salvava con l'operazione NOK senza anomalia aperta.
        var (state, tc, vecchiaUid) = CreaTestNokPoiConforme(StatoLavoroTest.InCorso);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        Assert.Empty(vm.SegnalazioniTest);
        vm.CondizioniVisual[0].Esito = Esito.NonConforme;
        vm.CondizioniVisual[0].DescrizioneProblema = "Di nuovo errata";
        vm.SincronizzaEsitoCondizioni();

        var riga = Assert.Single(vm.SegnalazioniTest);
        Assert.NotEqual(Assert.Single(state.Segnalazioni.GetAll()).Uid, riga.Segnalazione.Uid);
        riga.Severita = SeveritaAnomalia.High;
        vm.CreaAnomaliaDaTestCommand.Execute(riga);
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Empty(vm.ErroriValidazione);
        var tutte = state.NonConformita.GetAll();
        Assert.Equal(2, tutte.Count);
        var nuova = Assert.Single(tutte, n => n.Uid != vecchiaUid);
        Assert.Equal(StatoNonConformita.Aperta, nuova.Stato);
        Assert.Equal(StatoNonConformita.Chiusa, tutte.Single(n => n.Uid == vecchiaUid).Stato);
        Assert.Equal(2, state.Segnalazioni.GetAll().Count);
    }

    [Fact]
    public void NuovoPassaggioANok_SenzaSalvareUnaVoltaNokPersistita_NonDuplicaLaSegnalazione()
    {
        // Controllo di non-regressione: se NOK e' gia' quello persistito (nessun nuovo passaggio) e l'anomalia e' stata
        // chiusa, ricaricare e risalvare non deve generare altre segnalazioni.
        var (state, tc) = CreaTestNegativo();
        var origine = tc.Esecuzioni[0].Condizioni[0];
        var seg = new SegnalazioneDaClassificare
        {
            TestId = tc.Id, CondizioneUid = origine.Uid, EsecuzioneIterazione = 1,
            Stato = StatoSegnalazione.Classificata, AnomaliaUid = Guid.NewGuid()
        };
        origine.SegnalazioneUid = seg.Uid;
        state.TestCases.Save(tc, isNew: false, revisioneAttesa: tc.RevisioneDati);
        state.Segnalazioni.Save(seg);
        state.NonConformita.Save(new NonConformita
        {
            Uid = seg.AnomaliaUid!.Value, Id = "NC-0001", TestCaseId = tc.Id, DataApertura = DateTime.Now, Severita = SeveritaAnomalia.High,
            StatoWorkflow = StatoAnomalia.Conclusa, DataChiusura = DateTime.Now, MotivoChiusura = "x",
            ProveCollegate = new() { new RiferimentoProva { TestId = tc.Id, EsecuzioneIterazione = 1, CondizioneUid = origine.Uid } }
        });

        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.SincronizzaEsitoCondizioni();

        Assert.Empty(vm.SegnalazioniTest);
        Assert.Single(state.Segnalazioni.GetAll());
    }

    [Fact]
    public void NuovoPassaggioANok_ConAnomaliaPrecedenteAncoraAperta_RestaDeduplicato()
    {
        var (state, tc, vecchiaUid) = CreaTestNokPoiConforme(StatoLavoroTest.InCorso);
        var vecchia = state.NonConformita.GetByUid(vecchiaUid)!;
        NonConformitaRulesService.Riapri(vecchia);
        state.NonConformita.Save(vecchia, vecchia.RevisioneDati);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.CondizioniVisual[0].Esito = Esito.NonConforme;
        vm.CondizioniVisual[0].DescrizioneProblema = "Di nuovo errata";
        vm.SincronizzaEsitoCondizioni();

        Assert.Empty(vm.SegnalazioniTest);
    }

    [Fact]
    public async Task TestConclusoDopoRevConforme_NuovoNokConAnomaliaInBozza_SiSalvaEPassaADaRitestare()
    {
        // Percorso reale del pannello anomalie: Concluso su disco, operazione riportata a NOK, anomalia creata come bozza
        // (_anomalieNuove, "non ancora salvata"), poi Salva: nessun popup "anomalie ancora aperte".
        var (state, tc, _) = CreaTestNokPoiConforme(StatoLavoroTest.Concluso);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.CondizioniVisual[0].Esito = Esito.NonConforme;
        vm.CondizioniVisual[0].DescrizioneProblema = "Di nuovo errata";
        vm.SincronizzaEsitoCondizioni();
        var riga = Assert.Single(vm.SegnalazioniTest);
        riga.Severita = SeveritaAnomalia.High;
        vm.CreaAnomaliaDaTestCommand.Execute(riga);

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Empty(vm.ErroriValidazione);
        Assert.Equal(2, state.NonConformita.GetAll().Count);
        Assert.Equal(StatoLavoroTest.DaRitestare, state.TestCases.GetById(tc.Id)!.StatoLavoro);
    }

    [Fact]
    public void ChiudereUltimaAnomaliaAperta_FaAvanzareDaSoloLoStatoLavoroAConcluso()
    {
        var (state, tc) = CreaTestNegativo();
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var riga = Assert.Single(vm.SegnalazioniTest);
        riga.Severita = SeveritaAnomalia.High;
        vm.CreaAnomaliaDaTestCommand.Execute(riga);
        Assert.Equal(StatoLavoroTest.InCorso, vm.StatoLavoroSelezionato); // valore ereditato da CreaTestNegativo, mai toccato qui

        var anomalia = Assert.Single(vm.AnomalieTestAperte);
        anomalia.Nc.MotivoChiusura = "Bug risolto e verificato";
        vm.ChiudiAnomaliaDalTestCommand.Execute(anomalia);

        Assert.Equal(StatoLavoroTest.Concluso, vm.StatoLavoroSelezionato);
        Assert.Empty(vm.AnomalieTestAperte);
    }

    [Fact]
    public void CreaAnomalia_NonCreaNienteSeLaConfermaVieneRifiutata()
    {
        var originalHandler = ConfirmService.Handler;
        try
        {
            var (state, tc) = CreaTestNegativo();
            var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
            var riga = Assert.Single(vm.SegnalazioniTest);
            riga.Severita = SeveritaAnomalia.High;
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(false);

            vm.CreaAnomaliaDaTestCommand.Execute(riga);

            Assert.Empty(vm.AnomalieTestAperte);
            Assert.True(vm.HaSegnalazioniTest);
        }
        finally { ConfirmService.Handler = originalHandler; }
    }

    /// <summary>Copre il bug feedback utente 2026-08-21 punto 8: prima "Salva anomalia" e "Collega TICKET" erano
    /// due bottoni distinti e solo il salvataggio del Test ora committa entrambi insieme, senza bottoni
    /// dedicati per riga.</summary>
    [Fact]
    public async Task TicketEstaDigitato_SiSalvaConIlSalvataggioDelTest_SenzaBottoneDedicato()
    {
        var (state, tc) = CreaTestNegativo();
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var riga = Assert.Single(vm.SegnalazioniTest);
        riga.Severita = SeveritaAnomalia.High;
        vm.CreaAnomaliaDaTestCommand.Execute(riga);
        var anomalia = Assert.Single(vm.AnomalieTestAperte);
        anomalia.NuovoTicketRiferimento = "TICKET-9999";

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Empty(vm.ErroriValidazione);
        var salvata = Assert.Single(state.NonConformita.GetAll());
        Assert.Contains(salvata.CollegamentiPolarion, t => t.Codice == "TICKET-9999");
    }

    [Fact]
    public async Task EliminaAnomaliaDalTest_RimuoveUnAnomaliaAppenaCreataSenzaScrivereSuDisco()
    {
        var (state, tc) = CreaTestNegativo();
        ImpersonaMaster(state);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var riga = Assert.Single(vm.SegnalazioniTest);
        riga.Severita = SeveritaAnomalia.High;
        vm.CreaAnomaliaDaTestCommand.Execute(riga);
        var anomalia = Assert.Single(vm.AnomalieTestAperte);

        vm.EliminaAnomaliaDalTestCommand.Execute(anomalia);

        Assert.Empty(vm.AnomalieTestAperte);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(state.NonConformita.GetAll());
    }

    [Fact]
    public async Task EliminaAnomaliaDalTest_RimuoveUnAnomaliaGiaSalvataSoloDopoIlSalvataggioDelTest()
    {
        var (state, tc) = CreaTestNegativo();
        ImpersonaMaster(state);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var riga = Assert.Single(vm.SegnalazioniTest);
        riga.Severita = SeveritaAnomalia.High;
        vm.CreaAnomaliaDaTestCommand.Execute(riga);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Single(state.NonConformita.GetAll());

        var vm2 = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var anomalia = Assert.Single(vm2.AnomalieTestAperte);

        vm2.EliminaAnomaliaDalTestCommand.Execute(anomalia);
        Assert.Empty(vm2.AnomalieTestAperte);
        Assert.Single(state.NonConformita.GetAll()); // non ancora scritto su disco

        await vm2.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(state.NonConformita.GetAll());
    }

    [Fact]
    public void RiapriAnomaliaDalTest_RiportaUnaAnomaliaChiusaAdAperta()
    {
        var (state, tc) = CreaTestNegativo();
        ImpersonaMaster(state);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var riga = Assert.Single(vm.SegnalazioniTest);
        riga.Severita = SeveritaAnomalia.High;
        vm.CreaAnomaliaDaTestCommand.Execute(riga);
        var anomalia = Assert.Single(vm.AnomalieTestAperte);
        anomalia.Nc.MotivoChiusura = "Bug risolto e verificato";
        vm.ChiudiAnomaliaDalTestCommand.Execute(anomalia);
        var chiusa = Assert.Single(vm.AnomalieTestChiuse);

        vm.RiapriAnomaliaDalTestCommand.Execute(chiusa);

        Assert.Empty(vm.AnomalieTestChiuse);
        var riaperta = Assert.Single(vm.AnomalieTestAperte);
        Assert.Equal(StatoAnomalia.Confermata, riaperta.Nc.StatoWorkflow);
        Assert.Equal("", riaperta.Nc.MotivoChiusura);
    }

    /// <summary>Copre il bug feedback utente 2026-08-21 punto 8/2: due anomalie con la stessa DataApertura
    /// (creabile in rapida successione) devono mantenere un ordine deterministico attraverso ricostruzioni
    /// ripetute del pannello, non dipendere dall'ordine di enumerazione non garantito di un Dictionary.</summary>
    [Fact]
    public void PannelloAnomalie_OrdineStabilePerAnomalieConStessaDataAperturaAttraversoRicostruzioni()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_OrdineAnomalie_" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = TestStateFactory.Create();
            Directory.CreateDirectory(dir);
            state.InizializzaCartella(dir);
            var c1 = new Condizione { Esito = Esito.NonConforme, Nota = "Prima" };
            var c2 = new Condizione { Esito = Esito.NonConforme, Nota = "Seconda" };
            var tc = new TestCase
            {
                Id = "TC-NEG-2", Componente = "BCM", Assegnatario = "Tester",
                DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now, StatoLavoro = StatoLavoroTest.InCorso,
                Esecuzioni = new() { new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.NonConforme, Condizioni = new() { c1, c2 } } }
            };
            state.TestCases.Save(tc, isNew: true);
            var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
            var righe = vm.SegnalazioniTest.ToList();
            Assert.Equal(2, righe.Count);
            righe[0].Severita = SeveritaAnomalia.High;
            righe[1].Severita = SeveritaAnomalia.High;
            vm.CreaAnomaliaDaTestCommand.Execute(righe[0]);
            vm.CreaAnomaliaDaTestCommand.Execute(righe[1]);
            Assert.Equal(2, vm.AnomalieTestAperte.Count);

            var stessoIstante = DateTime.Now;
            foreach (var a in vm.AnomalieTestAperte) a.Nc.DataApertura = stessoIstante;

            var primoOrdine = vm.AnomalieTestAperte.Select(r => r.Nc.Id).ToList();
            vm.SincronizzaEsitoCondizioni(); // ricostruisce il pannello, come farebbe qualunque altro cambio di Esito
            var secondoOrdine = vm.AnomalieTestAperte.Select(r => r.Nc.Id).ToList();
            vm.SincronizzaEsitoCondizioni();
            var terzoOrdine = vm.AnomalieTestAperte.Select(r => r.Nc.Id).ToList();

            Assert.Equal(primoOrdine, secondoOrdine);
            Assert.Equal(primoOrdine, terzoOrdine);
        }
        finally { if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } } }
    }

    private static void ImpersonaMaster(AppState state)
    {
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
    }

    [Fact]
    public async Task EsitoNonConformeCorrettoInConformePrimaDelSalva_NonLasciaSegnalazioni()
    {
        // Segnalazione utente 2026-10-10: in modifica l'operatore mette per errore Non Conforme e poi
        // corregge in Conforme; la segnalazione creata nel frattempo restava e veniva salvata come "risolta".
        var (state, tc) = CreaTestNegativo(salva: false);
        tc.Esecuzioni[0].Condizioni[0].Esito = Esito.Conforme;
        tc.Esecuzioni[0].Esito = Esito.Conforme;
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        var condizione = vm.CondizioniVisual[0];

        condizione.Esito = Esito.NonConforme;
        vm.SincronizzaEsitoCondizioni();
        Assert.Single(vm.SegnalazioniTest);

        condizione.Esito = Esito.Conforme;
        vm.SincronizzaEsitoCondizioni();

        Assert.False(vm.HaSegnalazioniTest);
        Assert.Null(condizione.SegnalazioneUid);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErroriValidazione);
        Assert.Empty(state.Segnalazioni.GetAll());
    }

    [Fact]
    public async Task EscludiInSessione_PoiSalva_NonCreaUnaSecondaSegnalazioneSullaStessaOperazione()
    {
        // Issue 1 (UI TestSegnalazioniCoverageUiTests.Escludi_ConMotivazione): la segnalazione nasce e viene esclusa nella
        // stessa sessione, su un passaggio NOK che il Test non ha ancora salvato. Il Salva non deve trattarla come un
        // passaggio precedente concluso e crearne una seconda "Da classificare" (che bloccherebbe il Salva).
        var (state, tc) = CreaTestNegativo(salva: false);
        tc.Esecuzioni[0].Condizioni[0].Esito = Esito.Conforme;
        tc.Esecuzioni[0].Esito = Esito.Conforme;
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        var condizione = vm.CondizioniVisual[0];
        condizione.Esito = Esito.NonConforme;
        condizione.DescrizioneProblema = "Problema osservato - test";
        vm.SincronizzaEsitoCondizioni();
        var riga = Assert.Single(vm.SegnalazioniTest);

        riga.MotivazioneEsclusione = "Segnalazione non pertinente - test";
        vm.EscludiSegnalazioneDaTestCommand.Execute(riga);
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Empty(vm.ErroriValidazione);
        var seg = Assert.Single(state.Segnalazioni.GetAll());
        Assert.Equal(StatoSegnalazione.Esclusa, seg.Stato);
        Assert.Equal("Segnalazione non pertinente - test", seg.MotivazioneEsclusione);
        Assert.Null(seg.AnomaliaUid);
        Assert.Empty(state.NonConformita.GetAll());
    }

    [Fact]
    public async Task NonConformeCorrettaInConforme_IlPannelloSiSvuotaMentreIlDialogoEAperto()
    {
        // Issue 3: il cambio a Conforme apre il dialogo modale "Cambio esito" (il problema scritto c'e'). Il pannello deve
        // svuotarsi subito, senza aspettare la risposta; "Rimuovi" poi conferma l'esito senza segnalazioni sul disco.
        var (state, tc) = CreaTestNegativo(salva: false);
        tc.Esecuzioni[0].Condizioni[0].Esito = Esito.Conforme;
        tc.Esecuzioni[0].Esito = Esito.Conforme;
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        var condizione = vm.CondizioniVisual[0];
        condizione.Esito = Esito.NonConforme;
        condizione.DescrizioneProblema = "Problema osservato - test";
        vm.SincronizzaEsitoCondizioni();
        Assert.Single(vm.SegnalazioniTest);

        var risposta = new TaskCompletionSource<bool>();
        var originale = ConfirmService.Handler;
        ConfirmService.Handler = (_, _, _, _) => risposta.Task;
        try
        {
            condizione.Esito = Esito.Conforme;
            var cambio = vm.GestisciCambioEsitoCondizione(condizione);

            Assert.Empty(vm.SegnalazioniTest);
            Assert.Null(condizione.SegnalazioneUid);
            risposta.SetResult(true);
            await cambio;
        }
        finally { ConfirmService.Handler = originale; }

        Assert.Empty(vm.SegnalazioniTest);
        Assert.True(string.IsNullOrEmpty(condizione.DescrizioneProblema));
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErroriValidazione);
        Assert.Empty(state.Segnalazioni.GetAll());
    }

    [Fact]
    public async Task NonConformeCorrettaInConforme_SeSiMantieneLEsitoPrecedente_LaSegnalazioneTorna()
    {
        // Issue 3, ramo "Mantieni esito precedente": l'esito torna Non Conforme e la segnalazione del passaggio si ricrea.
        var (state, tc) = CreaTestNegativo(salva: false);
        tc.Esecuzioni[0].Condizioni[0].Esito = Esito.Conforme;
        tc.Esecuzioni[0].Esito = Esito.Conforme;
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        var condizione = vm.CondizioniVisual[0];
        condizione.Esito = Esito.NonConforme;
        condizione.DescrizioneProblema = "Problema osservato - test";
        vm.SincronizzaEsitoCondizioni();
        Assert.Single(vm.SegnalazioniTest);

        var risposta = new TaskCompletionSource<bool>();
        var originale = ConfirmService.Handler;
        ConfirmService.Handler = (_, _, _, _) => risposta.Task;
        try
        {
            condizione.Esito = Esito.Conforme;
            var cambio = vm.GestisciCambioEsitoCondizione(condizione);
            Assert.Empty(vm.SegnalazioniTest);
            risposta.SetResult(false);
            await cambio;
        }
        finally { ConfirmService.Handler = originale; }

        Assert.Equal(Esito.NonConforme, condizione.Esito);
        Assert.Equal("Problema osservato - test", condizione.DescrizioneProblema);
        Assert.Single(vm.SegnalazioniTest);
        Assert.NotNull(condizione.SegnalazioneUid);
    }

    [Fact]
    public void NuovaIterazioneInSessione_NonConformeCorrettaInConforme_LaSegnalazioneEsceSubito()
    {
        // Issue 3, seconda causa: una nuova iterazione creata in sessione vive solo nella collezione dell'editor fino al
        // Salva (TestCase.Esecuzioni non e' ancora aggiornata). La segnalazione nata sulla sua operazione va riconosciuta
        // alla revisione corrente, altrimenti il ritorno a Conforme non la scarta e resta nel pannello fino al Salva.
        var (state, tc) = CreaTestNegativo(salva: false);
        tc.Esecuzioni[0].Condizioni[0].Esito = Esito.Conforme;
        tc.Esecuzioni[0].Esito = Esito.Conforme;
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        vm.AggiungiIterazioneCommand.Execute(null);
        var condizione = vm.CondizioniVisual[0];
        condizione.Esito = Esito.NonConforme;
        condizione.DescrizioneProblema = "Problema osservato - test";
        vm.SincronizzaEsitoCondizioni();
        Assert.Single(vm.SegnalazioniTest);

        condizione.DescrizioneProblema = "";
        condizione.Esito = Esito.Conforme;
        vm.SincronizzaEsitoCondizioni();

        Assert.Empty(vm.SegnalazioniTest);
        Assert.Null(condizione.SegnalazioneUid);
    }

    private (AppState State, TestCase Test) CreaTestNegativo(bool salva = true)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Crea("Master Test", RuoloOperatore.Master);
        var operatori = state.Master.Operatori.Load().ToList();
        operatori.Add(master);
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        var condizione = new Condizione { Esito = Esito.NonConforme, Nota = "Risposta errata", Componente = "BCM" };
        var tc = new TestCase
        {
            Id = "TC-NEG-1",
            Componente = "BCM",
            Assegnatario = "Tester",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
            StatoLavoro = StatoLavoroTest.InCorso,
            Esecuzioni = new()
            {
                new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.NonConforme, Condizioni = new() { condizione } }
            }
        };
        if (salva) state.TestCases.Save(tc, isNew: true);
        return (state, tc);
    }
}
