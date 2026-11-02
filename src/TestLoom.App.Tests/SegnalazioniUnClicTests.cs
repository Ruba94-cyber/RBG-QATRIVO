using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Segnalazioni nel Test "a un clic" (feedback 2026-10-10): azione predefinita per riga (crea NC oppure collega
/// l'anomalia suggerita), "Gestisci tutte" con una sola conferma, e regressione del collegamento che non veniva salvato.</summary>
[Collection("Localization")]
public sealed class SegnalazioniUnClicTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_UnClic_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState NuovoStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Crea("Mario Rossi", RuoloOperatore.Master);
        var operatori = state.Master.Operatori.Load().ToList();
        operatori.Add(master);
        state.Master.Operatori.Save(operatori);
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        return state;
    }

    private static Condizione Nok(string funzionalita, string problema) => new()
    {
        Esito = Esito.NonConforme, Componente = "BCM", Funzionalita = funzionalita, DescrizioneProblema = problema, Nota = problema
    };

    private static TestCase NuovoTest(string id, params Condizione[] condizioni) => new()
    {
        Id = id, Componente = "BCM", Assegnatario = "Tester",
        DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now, StatoLavoro = StatoLavoroTest.InCorso,
        Esecuzioni = new() { new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.NonConforme, Condizioni = new(condizioni) } }
    };

    /// <summary>Anomalia aperta di un ALTRO Test: il suo titolo ha il formato delle NC create dalle segnalazioni.</summary>
    private static NonConformita NcAperta(string id, string funzionalita, string problema) => new()
    {
        Id = id, TestCaseId = "TC-ALTRO", DataApertura = DateTime.Now.AddDays(-3), StatoWorkflow = StatoAnomalia.Confermata,
        Severita = SeveritaAnomalia.Medium,
        Descrizione = NonConformitaRulesService.TitoloDaOperazione(funzionalita, "BCM", problema, "Operazione 1")
    };

    /// <summary>Sostituisce temporaneamente il gestore di conferma; il Dispose lo ripristina.</summary>
    private static IDisposable ConfermaCon(Func<bool> risposta)
    {
        var originale = ConfirmService.Handler;
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(risposta());
        return new Ripristino(() => ConfirmService.Handler = originale);
    }

    private sealed class Ripristino(Action azione) : IDisposable
    {
        public void Dispose() => azione();
    }

    [Fact]
    public void NokConNcSimileDiAltroTest_PreselezionaLaNcMaNonLaCollegaSenzaClic()
    {
        var state = NuovoStato();
        var tc = NuovoTest("TC-UC-1", Nok("Lettura guasto", "Risposta errata"));
        state.TestCases.Save(tc, isNew: true);
        state.NonConformita.Save(NcAperta("NC-0001", "Lettura guasto", "Risposta errata"));

        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });

        var riga = Assert.Single(vm.SegnalazioniTest);
        Assert.Equal("NC-0001", riga.AnomaliaSelezionata?.Id);
        Assert.True(riga.AzioneCollega);
        Assert.Equal(MotivoSuggerimentoAnomalia.StessaEcuFunzionalitaDescrizioneSimile, riga.Suggerimento?.Motivo);
        Assert.Contains("NC-0001", riga.EtichettaAzionePrimaria);
        Assert.Contains("NC-0001", riga.TestoSuggerimento);
        // La pre-selezione non scrive nulla: il collegamento avviene solo con il clic dell'utente.
        Assert.Empty(state.NonConformita.GetById("NC-0001")!.ProveCollegate);
        Assert.Equal(StatoSegnalazione.DaClassificare, riga.Segnalazione.Stato);
    }

    [Fact]
    public void NokSenzaNcSimili_AzionePredefinitaELaCreazioneNessunaNcPreselezionata()
    {
        var state = NuovoStato();
        var tc = NuovoTest("TC-UC-2", Nok("Lettura guasto", "Risposta errata"));
        state.TestCases.Save(tc, isNew: true);
        state.NonConformita.Save(NcAperta("NC-0001", "Scrittura guasto", "Timeout"));

        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });

        var riga = Assert.Single(vm.SegnalazioniTest);
        Assert.Null(riga.AnomaliaSelezionata);
        Assert.Null(riga.Suggerimento);
        Assert.True(riga.AzioneCrea);
        Assert.Equal(LocalizationService.Instance.Traduci("CreaNcBtn"), riga.EtichettaAzionePrimaria);
    }

    [Fact]
    public void PulsantePrimarioConSuggerimento_CollegaSenzaChiedereConferma()
    {
        var state = NuovoStato();
        var tc = NuovoTest("TC-UC-3", Nok("Lettura guasto", "Risposta errata"));
        state.TestCases.Save(tc, isNew: true);
        state.NonConformita.Save(NcAperta("NC-0001", "Lettura guasto", "Risposta errata"));
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var conferme = 0;
        using var ripristino = ConfermaCon(() => { conferme++; return true; });

        vm.AzionePrimariaSegnalazioneCommand.Execute(Assert.Single(vm.SegnalazioniTest));

        Assert.Equal(0, conferme);
        Assert.Empty(vm.SegnalazioniTest);
        Assert.Single(vm.AnomalieTestAperte, r => r.Nc.Id == "NC-0001");
    }

    [Fact]
    public void PulsantePrimarioSenzaSuggerimento_CreaLaNcSoloDopoConferma()
    {
        var state = NuovoStato();
        var tc = NuovoTest("TC-UC-4", Nok("Lettura guasto", "Risposta errata"));
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var riga = Assert.Single(vm.SegnalazioniTest);
        riga.Severita = SeveritaAnomalia.High;

        using (ConfermaCon(() => false)) vm.AzionePrimariaSegnalazioneCommand.Execute(riga);
        Assert.Single(vm.SegnalazioniTest);
        Assert.Empty(vm.AnomalieTestAperte);

        using (ConfermaCon(() => true)) vm.AzionePrimariaSegnalazioneCommand.Execute(riga);
        Assert.Empty(vm.SegnalazioniTest);
        Assert.Single(vm.AnomalieTestAperte);
    }

    [Fact]
    public void SceltaDellUtenteSullaRiga_SopravvieneAllaRicostruzioneDelPannello()
    {
        var state = NuovoStato();
        var tc = NuovoTest("TC-UC-5", Nok("Lettura guasto", "Risposta errata"));
        state.TestCases.Save(tc, isNew: true);
        state.NonConformita.Save(NcAperta("NC-0001", "Lettura guasto", "Risposta errata"));
        state.NonConformita.Save(NcAperta("NC-0002", "Altra funzione", "Altro problema"));
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var riga = Assert.Single(vm.SegnalazioniTest);
        Assert.Equal("NC-0001", riga.AnomaliaSelezionata?.Id);

        riga.AnomaliaSelezionata = riga.AnomalieCandidate.Single(n => n.Id == "NC-0002");
        vm.SincronizzaEsitoCondizioni(); // ricostruisce il pannello, come farebbe qualunque altro cambio di esito

        Assert.Equal("NC-0002", Assert.Single(vm.SegnalazioniTest).AnomaliaSelezionata?.Id);
    }

    [Fact]
    public async Task GestisciTutte_CollegaLaSuggeritaECreaLAltra_UnaSolaConferma_NessunDuplicato_IdempotenteAlSecondoClic()
    {
        var state = NuovoStato();
        var tc = NuovoTest("TC-UC-6", Nok("Lettura guasto", "Risposta errata"), Nok("Scrittura guasto", "Scrittura non riuscita"));
        state.TestCases.Save(tc, isNew: true);
        state.NonConformita.Save(NcAperta("NC-0001", "Lettura guasto", "Risposta errata"));
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var conferme = 0;
        using var ripristino = ConfermaCon(() => { conferme++; return true; });
        vm.SegnalazioniTest.Single(r => r.Condizione.Funzionalita == "Scrittura guasto").Severita = SeveritaAnomalia.High;
        Assert.Equal("NC-0001", vm.SegnalazioniTest.Single(r => r.Condizione.Funzionalita == "Lettura guasto").AnomaliaSelezionata?.Id);

        vm.GestisciTutteSegnalazioniCommand.Execute(null);
        vm.GestisciTutteSegnalazioniCommand.Execute(null); // secondo clic: le segnalazioni gia' gestite non ci sono piu'

        Assert.Equal(1, conferme);
        Assert.Empty(vm.SegnalazioniTest);
        Assert.Equal(2, vm.AnomalieTestAperte.Count);

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Empty(vm.ErroriValidazione);
        var tutte = state.NonConformita.GetAll();
        Assert.Equal(2, tutte.Count);
        Assert.Single(state.NonConformita.GetById("NC-0001")!.ProveCollegate, p => p.TestId == tc.Id);
        var nuova = Assert.Single(tutte, n => n.Id != "NC-0001");
        Assert.Equal(tc.Id, nuova.TestCaseId);
        Assert.Equal(SeveritaAnomalia.High, nuova.Severita);
        Assert.Equal(2, state.Segnalazioni.GetAll().Count(s => s.Stato == StatoSegnalazione.Classificata));
    }

    [Fact]
    public void GestisciTutte_SenzaSeveritaNonCreaNcEConservaLaSegnalazione()
    {
        var state = NuovoStato();
        var tc = NuovoTest("TC-UC-7", Nok("Lettura guasto", "Risposta errata"));
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var conferme = 0;
        using var ripristino = ConfermaCon(() => { conferme++; return true; });

        vm.GestisciTutteSegnalazioniCommand.Execute(null);

        Assert.Equal(0, conferme);
        Assert.Single(vm.SegnalazioniTest);
        Assert.Empty(vm.AnomalieTestAperte);
        Assert.Contains(vm.ErroriValidazione, e => !string.IsNullOrWhiteSpace(e));
    }

    [Fact]
    public void GestisciTutte_ConfermaRifiutata_NonCambiaNulla()
    {
        var state = NuovoStato();
        var tc = NuovoTest("TC-UC-8", Nok("Lettura guasto", "Risposta errata"));
        state.TestCases.Save(tc, isNew: true);
        state.NonConformita.Save(NcAperta("NC-0001", "Lettura guasto", "Risposta errata"));
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        using var ripristino = ConfermaCon(() => false);

        vm.GestisciTutteSegnalazioniCommand.Execute(null);

        Assert.Single(vm.SegnalazioniTest);
        Assert.Empty(vm.AnomalieTestAperte);
        Assert.Empty(state.NonConformita.GetById("NC-0001")!.ProveCollegate);
    }

    /// <summary>Regressione: il collegamento a una NC esistente non veniva mai salvato (la copia modificata restava
    /// orfana, la NC spariva dal pannello e non riceveva la prova del Test).</summary>
    [Fact]
    public async Task CollegamentoAUnaNcEsistente_SiSalvaSullaNc_E_LaNcRestaNelPannello()
    {
        var state = NuovoStato();
        var tc = NuovoTest("TC-UC-9", Nok("Lettura guasto", "Risposta errata"));
        state.TestCases.Save(tc, isNew: true);
        state.NonConformita.Save(NcAperta("NC-0001", "Lettura guasto", "Risposta errata"));
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var riga = Assert.Single(vm.SegnalazioniTest);
        riga.AnomaliaSelezionata = riga.AnomalieCandidate.Single(n => n.Id == "NC-0001");

        vm.CollegaSegnalazioneAdAnomaliaCommand.Execute(riga);
        Assert.Single(vm.AnomalieTestAperte, r => r.Nc.Id == "NC-0001");

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Empty(vm.ErroriValidazione);
        var nc = state.NonConformita.GetById("NC-0001")!;
        Assert.Single(nc.ProveCollegate, p => p.TestId == tc.Id && p.CondizioneUid == tc.Esecuzioni[0].Condizioni[0].Uid);
    }

    [Fact]
    public async Task DueSegnalazioniCollegateALaStessaNc_AccumulanoLeProveSenzaDuplicati()
    {
        var state = NuovoStato();
        var tc = NuovoTest("TC-UC-10", Nok("Lettura guasto", "Risposta errata"), Nok("Lettura guasto", "Risposta errata ripetuta"));
        state.TestCases.Save(tc, isNew: true);
        state.NonConformita.Save(NcAperta("NC-0001", "Lettura guasto", "Risposta errata"));
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var righe = vm.SegnalazioniTest.ToList();
        Assert.Equal(2, righe.Count);

        foreach (var riga in righe)
        {
            riga.AnomaliaSelezionata = riga.AnomalieCandidate.Single(n => n.Id == "NC-0001");
            vm.CollegaSegnalazioneAdAnomaliaCommand.Execute(riga);
        }
        Assert.Single(vm.AnomalieTestAperte);

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Empty(vm.ErroriValidazione);
        var nc = state.NonConformita.GetById("NC-0001")!;
        Assert.Equal(2, nc.ProveCollegate.Count(p => p.TestId == tc.Id));
        Assert.Equal(2, nc.ProveCollegate.Select(p => p.CondizioneUid).Distinct().Count());
    }

    /// <summary>Regressione (test UI 2026-10-10): la riga nasce quando l'esito diventa Non Conforme, cioe' prima che
    /// l'operatore scriva il problema. Scritto il problema il suggerimento deve comparire senza ricostruire la riga.</summary>
    [Fact]
    public void ProblemaScrittoDopoLEsito_AggiornaLaPreselezioneSenzaRicostruireLaRiga()
    {
        var state = NuovoStato();
        var tc = NuovoTest("TC-UC-11", Nok("Lettura guasto", ""));
        state.TestCases.Save(tc, isNew: true);
        state.NonConformita.Save(NcAperta("NC-0001", "Lettura guasto", "Risposta errata"));
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var riga = Assert.Single(vm.SegnalazioniTest);
        Assert.Null(riga.AnomaliaSelezionata); // senza problema non c'e' somiglianza da misurare

        riga.Condizione.DescrizioneProblema = "Risposta errata";

        Assert.Same(riga, Assert.Single(vm.SegnalazioniTest));
        Assert.Equal("NC-0001", riga.AnomaliaSelezionata?.Id);
        Assert.True(riga.AzioneCollega);
        Assert.Equal("NC-0001", riga.Suggerimento?.AnomaliaId);
    }

    /// <summary>Una scelta fatta a mano sulla riga non viene sovrascritta dal ricalcolo, anche se il suggerimento cambia.</summary>
    [Fact]
    public void SceltaManualeSullaRiga_NonVieneSovrascrittaQuandoCambiaIlProblema()
    {
        var state = NuovoStato();
        var tc = NuovoTest("TC-UC-12", Nok("Lettura guasto", ""));
        state.TestCases.Save(tc, isNew: true);
        state.NonConformita.Save(NcAperta("NC-0001", "Lettura guasto", "Risposta errata"));
        state.NonConformita.Save(NcAperta("NC-0002", "Altra funzione", "Altro problema"));
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var riga = Assert.Single(vm.SegnalazioniTest);
        riga.AnomaliaSelezionata = riga.AnomalieCandidate.Single(n => n.Id == "NC-0002");

        riga.Condizione.DescrizioneProblema = "Risposta errata";

        Assert.Equal("NC-0002", riga.AnomaliaSelezionata?.Id);
        Assert.Equal("NC-0001", riga.Suggerimento?.AnomaliaId);
    }

    /// <summary>Se il problema cambia e non assomiglia piu' all'anomalia, la pre-selezione automatica si ritira e la riga
    /// torna a "Crea NC".</summary>
    [Fact]
    public void ProblemaCambiatoInUnaltroSenso_RitiraLaPreselezioneAutomatica()
    {
        var state = NuovoStato();
        var tc = NuovoTest("TC-UC-13", Nok("Lettura guasto", "Risposta errata"));
        state.TestCases.Save(tc, isNew: true);
        state.NonConformita.Save(NcAperta("NC-0001", "Lettura guasto", "Risposta errata"));
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var riga = Assert.Single(vm.SegnalazioniTest);
        Assert.Equal("NC-0001", riga.AnomaliaSelezionata?.Id);

        riga.Condizione.DescrizioneProblema = "Timeout della comunicazione con il bus";

        Assert.Null(riga.AnomaliaSelezionata);
        Assert.Null(riga.Suggerimento);
        Assert.True(riga.AzioneCrea);
    }
}
