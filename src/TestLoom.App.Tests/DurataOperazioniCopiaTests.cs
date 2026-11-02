using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>TIME (6.6.1): regola di copia della durata (indice locale), comandi +/-5 dell'editor, risultato di copia superato
/// scartato, obbligo della politica solo per le operazioni eseguite della revisione corrente (mai per le revisioni storiche).</summary>
public sealed class DurataOperazioniCopiaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_DurataCopia_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Giorno = new(2026, 3, 1);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        return state;
    }

    private static Condizione Op(string funzionalita, string componente, Esito esito, int? minuti) => new()
    {
        Funzionalita = funzionalita,
        Esito = esito,
        Componente = new EcuSelection([componente]),
        DurataMinuti = minuti,
    };

    private static Esecuzione Rev(DateTime data, params Condizione[] condizioni) => new()
    {
        Iterazione = 1,
        Data = data,
        Operatore = "Tester",
        Esito = Esito.Conforme,
        Condizioni = [.. condizioni],
    };

    private static TestCase Test(string id, string veicolo, params Esecuzione[] revisioni)
    {
        var tc = new TestCase
        {
            Id = id,
            Componente = new EcuSelection(["BCM"]),
            Veicoli = [veicolo],
            Funzionalita = ["F1"],
            DataCreazione = Giorno,
            UltimaModifica = Giorno,
            Assegnatario = "Tester",
        };
        tc.Esecuzioni.AddRange(revisioni);
        return tc;
    }

    private static void Indicizza(LocalTestIndexStore store, TestCase tc) =>
        store.Upsert(Path.Combine(store.WorkspaceDirectory, tc.Id + ".json"), Giorno, 100, tc);

    private static Condizione OpMulti(string funzionalita, string[] componente, Esito esito, int? minuti) => new()
    {
        Funzionalita = funzionalita,
        Esito = esito,
        Componente = new EcuSelection(componente),
        DurataMinuti = minuti,
    };

    private LocalTestIndexStore NuovoIndice() => new(Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"));

    [Fact]
    public void TrovaDurataCopia_PreferisceLoStessoVeicolo_PoiLAltro_EscludeEliminatiESnapshotUdt()
    {
        var store = NuovoIndice();
        Indicizza(store, Test("T-A", "Daily A", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 20))));
        Indicizza(store, Test("T-B", "Daily B", Rev(Giorno.AddDays(5), Op("F1", "BCM", Esito.Conforme, 10))));
        Indicizza(store, Test("T-C", "Daily A", Rev(Giorno.AddDays(9), Op("F1", "BCM", Esito.Conforme, null))));
        var eliminato = Test("T-D", "Daily A", Rev(Giorno.AddDays(12), Op("F1", "BCM", Esito.Conforme, 99)));
        eliminato.Eliminato = true;
        Indicizza(store, eliminato);
        var importato = Test("T-E", "Daily A", Rev(Giorno.AddDays(13), Op("F1", "BCM", Esito.Conforme, 77)));
        importato.Origine = OrigineTest.ImportStatoUdt;
        Indicizza(store, importato);

        Assert.Equal(20, store.TrovaDurataCopia(["BCM"], "F1", ["Daily A"], null));   // stesso veicolo: la piu' recente con durata nota
        Assert.Equal(10, store.TrovaDurataCopia(["BCM"], "F1", ["Daily C"], null));   // altro veicolo: la piu' recente
        Assert.Null(store.TrovaDurataCopia(["EDC"], "F1", ["Daily A"], null));         // altra componente: nessun precedente, resta vuoto
        Assert.Null(store.TrovaDurataCopia([""], "F1", ["Daily A"], null));            // chiave incompleta: nessuna copia
    }

    [Fact]
    public void TrovaDurataCopia_SenzaVeicoliDelTest_RestituisceLaPiuRecenteSenzaErroreSql()
    {
        // Test senza veicoli: nessuna preferenza veicolo nell'ORDER BY (prima "ORDER BY 0", errore SQL, durata mai proposta).
        var store = NuovoIndice();
        Indicizza(store, Test("T-A", "Daily A", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 20))));
        Indicizza(store, Test("T-B", "Daily B", Rev(Giorno.AddDays(5), Op("F1", "BCM", Esito.Conforme, 10))));

        Assert.Equal(10, store.TrovaDurataCopia(["BCM"], "F1", [], null));
    }

    [Fact]
    public void TrovaDurataCopia_EcuMultiplaSoloInsiemeEsatto_EcuSingolaLaContiene()
    {
        var store = NuovoIndice();
        Indicizza(store, Test("T-MULTI", "Daily A", Rev(Giorno, OpMulti("F1", ["BCM", "ABS"], Esito.Conforme, 20))));
        Indicizza(store, Test("T-SINGOLA", "Daily A", Rev(Giorno.AddDays(5), OpMulti("F1", ["BCM"], Esito.Conforme, 10))));

        Assert.Equal(20, store.TrovaDurataCopia(["ABS", "BCM"], "F1", ["Daily A"], null));  // insieme esatto, ordine indifferente
        Assert.Equal(10, store.TrovaDurataCopia(["BCM"], "F1", ["Daily A"], null));         // componente singola: la piu' recente che la contiene
        Assert.Null(store.TrovaDurataCopia(["ABS", "EDC"], "F1", ["Daily A"], null));        // insieme diverso: nessuna copia
    }

    [Fact]
    public void TrovaDurataCopia_VeicoliDelTestPrimaDelleAltreAMenoDellaData()
    {
        var store = NuovoIndice();
        Indicizza(store, Test("T-NUOVO", "Daily B", Rev(Giorno.AddDays(9), Op("F1", "BCM", Esito.Conforme, 50))));
        Indicizza(store, Test("T-VECCHIO", "Daily A", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 15))));

        Assert.Equal(15, store.TrovaDurataCopia(["BCM"], "F1", ["Daily A"], null));
        Assert.Equal(15, store.TrovaDurataCopia(["BCM"], "F1", ["Daily C", "Daily A"], null)); // un veicolo del Test coincide
        Assert.Equal(50, store.TrovaDurataCopia(["BCM"], "F1", ["Daily C"], null));           // nessuno sul veicolo: la piu' recente
    }

    [Fact]
    public void TrovaDurataCopia_ParitaSecondoOrdineDelReport()
    {
        var store = NuovoIndice();
        Indicizza(store, Test("T-A", "Daily A", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 10))));
        Indicizza(store, Test("T-B", "Daily A", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 20))));

        // Stessa data: vince l'Id del Test in ordine ordinale (T-B dopo T-A), come nel Report.
        Assert.Equal(20, store.TrovaDurataCopia(["BCM"], "F1", ["Daily A"], null));
    }

    [Fact]
    public void TrovaDurataCopia_OperazioneDtcStessoPassoEStessaEcu_NonMescolaConLeNormali()
    {
        var store = NuovoIndice();
        var guasto = new Condizione { Funzionalita = "", Esito = Esito.Conforme, Componente = new EcuSelection(["BCM"]), DurataMinuti = 30, Guasto = "P0100", DtcPasso = PassoDtc.Validazione };
        Indicizza(store, Test("T-guasto", "Daily A", Rev(Giorno, guasto)));
        Indicizza(store, Test("T-NORMALE", "Daily A", Rev(Giorno.AddDays(3), Op("F1", "BCM", Esito.Conforme, 7))));

        Assert.Equal(30, store.TrovaDurataCopia(["BCM"], null, ["Daily A"], (int)PassoDtc.Validazione));
        Assert.Null(store.TrovaDurataCopia(["BCM"], null, ["Daily A"], (int)PassoDtc.Devalidazione));
        Assert.Equal(7, store.TrovaDurataCopia(["BCM"], "F1", ["Daily A"], null));           // una normale non prende la guasto
    }

    [Fact]
    public void ApplicaDurataCopiata_RichiestaCorrenteEUnaSolaVolta()
    {
        var state = CreaStato();
        var tc = Test("T-COPIA", "Daily A", Rev(Giorno, Op("F1", "BCM", Esito.DaEseguire, null)));
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.PubblicaRisultatoCopia = _ => { };
        var operazione = vm.RevisioneCorrente!.Condizioni[0];

        var richiesta = vm.PrenotaCopiaDurata(operazione)!;
        Assert.Equal("OP\u0001BCM\u0001F1", richiesta.Chiave);
        Assert.True(vm.ApplicaDurataCopiata(operazione, richiesta.Generazione, richiesta.Chiave, 15));
        Assert.Equal(15, operazione.DurataMinuti);
        Assert.True(operazione.DurataDaCopiaAutomatica);

        // Lo stesso risultato non si applica due volte.
        Assert.False(vm.ApplicaDurataCopiata(operazione, richiesta.Generazione, richiesta.Chiave, 40));
        Assert.Equal(15, operazione.DurataMinuti);
    }

    [Fact]
    public void ApplicaDurataCopiata_RichiestaSuperataOTestoNonValido_NonSiApplica()
    {
        var state = CreaStato();
        var tc = Test("T-SUPERATA", "Daily A", Rev(Giorno, Op("F1", "BCM", Esito.DaEseguire, null)));
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.PubblicaRisultatoCopia = _ => { };
        var operazione = vm.RevisioneCorrente!.Condizioni[0];

        var prima = vm.PrenotaCopiaDurata(operazione)!;
        var seconda = vm.PrenotaCopiaDurata(operazione)!;      // una nuova richiesta rende superata la prima
        Assert.False(vm.ApplicaDurataCopiata(operazione, prima.Generazione, prima.Chiave, 40));
        Assert.Null(operazione.DurataMinuti);

        // Un testo non valido in corso non viene sovrascritto dalla copia.
        operazione.DurataTesto.Testo = "abc";
        Assert.False(vm.ApplicaDurataCopiata(operazione, seconda.Generazione, seconda.Chiave, 15));
        Assert.Null(operazione.DurataMinuti);
        Assert.True(operazione.DurataTesto.HaErrore);
    }

    [Fact]
    public async Task CambioEcu_RilanciaLaCopiaSoloSeLaDurataEAutomatica_NonSovrascriveUnValoreDellOperatore()
    {
        var state = CreaStato();
        var storica = Test("T-STORIA-componente2", "Daily A", Rev(Giorno, Op("F1", "Componente2", Esito.Conforme, 9)));
        storica.Componente = new EcuSelection(["BCM", "Componente2"]);
        state.TestCases.Save(storica, isNew: true);
        var tc = Test("T-CAMBIO-componente", "Daily A", Rev(Giorno, Op("F1", "BCM", Esito.DaEseguire, null)));
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.PubblicaRisultatoCopia = azione => azione();
        var operazione = vm.RevisioneCorrente!.Condizioni[0];

        operazione.Componente = new EcuSelection(["Componente2"]);           // cambio componente: la copia viene rilanciata e applicata
        await vm.AttendiCopieDurataInCorso();
        Assert.Equal(9, operazione.DurataMinuti);
        Assert.True(operazione.DurataDaCopiaAutomatica);

        // Un valore digitato dall'operatore non viene toccato dal cambio di componente.
        operazione.DurataMinuti = 33;
        operazione.Componente = new EcuSelection(["BCM"]);
        await vm.AttendiCopieDurataInCorso();
        Assert.Equal(33, operazione.DurataMinuti);
        Assert.False(operazione.DurataDaCopiaAutomatica);
    }

    [Fact]
    public async Task CopiaDallaPrecedente_NonCopiaLaDurataPariPariMaSoloLaRegolaDiCopia()
    {
        var state = CreaStato();
        var tc = Test("T-PRECEDENTE", "Daily A", Rev(Giorno, Op("F1", "BCM", Esito.DaEseguire, null)));
        tc.Esecuzioni[0].Condizioni[0].ValoriStrumenti["ValidationTool::ADA"] = "NO";
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.PubblicaRisultatoCopia = azione => azione();
        var origine = vm.RevisioneCorrente!.Condizioni[0];
        origine.DurataMinuti = 40;                             // durata solo in memoria: l'indice non la conosce

        vm.InModifica = true;                                  // la copia dall'operazione precedente richiede la modifica dell'editor
        vm.CopiaDallaPrecedenteCommand.Execute(null);
        Assert.Contains(vm.AnteprimaCopiaPrecedente, c => c.Campo == "ValidationTool::ADA");
        Assert.DoesNotContain(vm.AnteprimaCopiaPrecedente, c => c.Campo == DurataOperazione.ChiaveValoriStrumenti);

        vm.ConfermaCopiaDallaPrecedenteCommand.Execute(null);
        await vm.AttendiCopieDurataInCorso();
        var copia = vm.RevisioneCorrente!.Condizioni[^1];
        Assert.NotSame(origine, copia);
        Assert.Equal("NO", copia.ValoriStrumenti["ValidationTool::ADA"]);
        Assert.Null(copia.DurataMinuti);                       // non 40: la durata non si copia pari pari
    }

    [Fact]
    public void ComandiPiuECinque_NonScendonoSottoZero_EDaVuotoIlMenoNonInventaUnoZero()
    {
        var state = CreaStato();
        var tc = Test("T-PASSO", "Daily A", Rev(Giorno, Op("F1", "BCM", Esito.DaEseguire, null)));
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var operazione = vm.RevisioneCorrente!.Condizioni[0];

        vm.DecrementaDurataOperazioneCommand.Execute(operazione);
        Assert.Null(operazione.DurataMinuti);

        vm.IncrementaDurataOperazioneCommand.Execute(operazione);
        Assert.Equal(5, operazione.DurataMinuti);
        vm.IncrementaDurataOperazioneCommand.Execute(operazione);
        Assert.Equal(10, operazione.DurataMinuti);

        vm.DecrementaDurataOperazioneCommand.Execute(operazione);
        vm.DecrementaDurataOperazioneCommand.Execute(operazione);
        vm.DecrementaDurataOperazioneCommand.Execute(operazione);
        Assert.Equal(0, operazione.DurataMinuti);   // zero noto, mai negativo
    }

    private void AttivaPoliticaObbligatoria(AppState state)
    {
        var politica = PoliticaDurataService.Load(state.Paths);
        politica.DurataObbligatoria = true;
        PoliticaDurataService.Save(state.Paths, politica, true);
        state.RicaricaTutto();
    }

    [Fact]
    public async Task ObbligoDurata_OperazioneSalvataInvariataNonSiBloccaMaLaModificataSi()
    {
        var state = CreaStato();
        var storica = Rev(Giorno, Op("F1", "BCM", Esito.Conforme, null));     // revisione storica senza durata: non viene riscritta
        var corrente = Rev(Giorno.AddDays(1), Op("F1", "BCM", Esito.Conforme, null), Op("F2", "BCM", Esito.DaEseguire, null));
        var tc = Test("T-OBBLIGO", "Daily A", storica, corrente);
        state.TestCases.Save(tc, isNew: true);
        AttivaPoliticaObbligatoria(state);
        var messaggio = LocalizationService.Instance.Traduci("ValDurataOperazioneObbl");

        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        // Operazione eseguita, salvata cosi' com'e': non si riscrive il passato, quindi non viene bloccata.
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.CampiObbligatoriMancanti, c => c.Messaggio == messaggio);

        // Modificata (una nota): ora e' soggetta all'obbligo.
        vm.RevisioneCorrente!.Condizioni[0].Nota = "verificato";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Single(vm.CampiObbligatoriMancanti, c => c.Messaggio == messaggio);

        vm.RevisioneCorrente!.Condizioni[0].DurataMinuti = 15;
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.CampiObbligatoriMancanti, c => c.Messaggio == messaggio);
    }

    [Fact]
    public async Task ObbligoDurata_OperazioneNuovaEDtcEseguiteSenzaDurataSiBloccano()
    {
        var state = CreaStato();
        var tc = Test("T-OBBLIGO-NUOVA", "Daily A", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 10)));
        state.TestCases.Save(tc, isNew: true);
        AttivaPoliticaObbligatoria(state);
        var messaggio = LocalizationService.Instance.Traduci("ValDurataOperazioneObbl");
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.PubblicaRisultatoCopia = _ => { };

        vm.AggiungiCondizioneCommand.Execute(null);
        var nuova = vm.RevisioneCorrente!.Condizioni[^1];
        nuova.Funzionalita = "F1";
        nuova.Esito = Esito.Conforme;
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Single(vm.CampiObbligatoriMancanti, c => c.Messaggio == messaggio);

        // Anche un'operazione guasto eseguita (la politica include le guasto, la matrice no).
        nuova.DurataMinuti = 5;
        vm.RevisioneCorrente!.Condizioni.Add(new Condizione { Esito = Esito.Conforme, Componente = new EcuSelection(["BCM"]), Guasto = "P0100", DtcPasso = PassoDtc.Validazione });
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Single(vm.CampiObbligatoriMancanti, c => c.Messaggio == messaggio);
    }

    [Fact]
    public async Task ObbligoDurata_NonSiApplicaAiTestImportatiDaUdt()
    {
        var state = CreaStato();
        var tc = Test("T-UnitLogger", "Daily A", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, null)));
        tc.Origine = OrigineTest.ImportStatoUdt;
        state.TestCases.Save(tc, isNew: true);
        AttivaPoliticaObbligatoria(state);
        var messaggio = LocalizationService.Instance.Traduci("ValDurataOperazioneObbl");

        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.CampiObbligatoriMancanti, c => c.Messaggio == messaggio);
    }

    [Fact]
    public async Task PoliticaIllegibile_MostraUnAvvisoMaNonBloccaIlSalvataggio()
    {
        var state = CreaStato();
        var tc = Test("T-POLITICA-ROTTA", "Daily A", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, null)));
        state.TestCases.Save(tc, isNew: true);
        File.WriteAllText(state.Paths.PoliticaDurataFile, "{ questo non e' json");
        state.RicaricaTutto();
        Assert.NotNull(state.PoliticaDurata.ErroreCaricamento);
        var messaggio = LocalizationService.Instance.Traduci("PoliticaDurataIllegibileAvviso");

        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        Assert.Contains(messaggio, vm.Avvisi);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.CampiObbligatoriMancanti, c => c.Messaggio == LocalizationService.Instance.Traduci("ValDurataOperazioneObbl"));
    }

    [Fact]
    public async Task AggiungiIterazione_NonCopiaLaDurataPariPariMaSoloLaRegolaDiCopia()
    {
        var state = CreaStato();
        var tc = Test("T-ITERA", "Daily A", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 15)));
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.PubblicaRisultatoCopia = azione => azione();
        vm.RevisioneCorrente!.Condizioni[0].DurataMinuti = 40;     // in memoria, mai salvata: l'indice conosce solo 15

        vm.AggiungiIterazioneCommand.Execute(null);
        await vm.AttendiCopieDurataInCorso();

        // La nuova iterazione non riceve il 40 in memoria: la regola di copia propone il valore salvato piu' recente (15), modificabile.
        var nuova = vm.RevisioneCorrente!.Condizioni;
        Assert.Single(nuova);
        Assert.Equal(15, nuova[0].DurataMinuti);
        Assert.True(nuova[0].DurataDaCopiaAutomatica);
        Assert.Equal("F1", nuova[0].Funzionalita);
    }

    [Fact]
    public async Task ObbligoDurata_SenzaPolitica_NonBloccaNulla()
    {
        var state = CreaStato();
        var tc = Test("T-FACOLTATIVA", "Daily A", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, null)));
        state.TestCases.Save(tc, isNew: true);
        var messaggio = LocalizationService.Instance.Traduci("ValDurataOperazioneObbl");

        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.DoesNotContain(vm.CampiObbligatoriMancanti, c => c.Messaggio == messaggio);
    }
}
