using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>TIME (6.6.1), revisione 2 del review indipendente: identita dell'operazione per ultima revisione salvata (controllo di
/// salvataggio), copia solo dall'ultima revisione, copia anche all'inserimento di operazioni strumento/guasto, guardia del valore
/// digitato dall'operatore, distacco del testo della durata.</summary>
[Collection("Localization")]
public sealed class DurataRevisioniEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_DurataRev_" + Guid.NewGuid().ToString("N"));
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

    private static Condizione Op(Guid uid, string nota, int? minuti) => new()
    {
        Uid = uid,
        Funzionalita = "F1",
        Esito = Esito.Conforme,
        Componente = new EcuSelection(["BCM"]),
        DurataMinuti = minuti,
        Nota = nota,
    };

    private static Esecuzione Rev(int iterazione, DateTime data, params Condizione[] condizioni) => new()
    {
        Iterazione = iterazione,
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

    private LocalTestIndexStore NuovoIndice() => new(Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"));

    // ----- Finding 1: identita per ultima revisione salvata (controllo "operazione nuova o modificata") -----

    [Fact]
    public void OperazioneSvuotata_DopoRevisioneConDurata_EModificataEQuindiBloccata()
    {
        var state = CreaStato();
        var uid = Guid.NewGuid();
        // Revisione 1 senza durata (salvata prima della politica), revisione 2 con 20: l'ultimo valore salvato e 20.
        var tc = Test("T-SVUOTATA", "Daily A", Rev(1, Giorno, Op(uid, "", null)), Rev(2, Giorno.AddDays(1), Op(uid, "", 20)));
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var operazione = vm.RevisioneCorrente!.Condizioni.Single();
        Assert.Equal(20, operazione.DurataMinuti);

        operazione.DurataMinuti = null;   // l'operatore cancella la durata
        Assert.True(vm.OperazioneNuovaOModificata(operazione));   // confrontata con 20 (ultima revisione), non con il vuoto della 1
    }

    [Fact]
    public void OperazioneInvariataConNotaCambiataSoloNellaUltimaRevisione_NonEModificata()
    {
        var state = CreaStato();
        var uid = Guid.NewGuid();
        // Revisione 1 e 2 differiscono solo per la nota (durata vuota in entrambe): l'operazione non cambia rispetto all'ultima salvata.
        var tc = Test("T-NOTA", "Daily A", Rev(1, Giorno, Op(uid, "", null)), Rev(2, Giorno.AddDays(1), Op(uid, "nota della revisione 2", null)));
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var operazione = vm.RevisioneCorrente!.Condizioni.Single();
        Assert.Equal("nota della revisione 2", operazione.Nota);

        Assert.False(vm.OperazioneNuovaOModificata(operazione));   // nessun obbligo sulla durata per un'operazione salvata e invariata
    }

    [Fact]
    public void OperazioneNuova_SenzaAlcunaRevisioneSalvataCorrispondente_EModificata()
    {
        var state = CreaStato();
        var tc = Test("T-NUOVA", "Daily A", Rev(1, Giorno, Op(Guid.NewGuid(), "", null)));
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        var nuova = Op(Guid.NewGuid(), "", null);
        vm.RevisioneCorrente!.Condizioni.Add(nuova);

        Assert.True(vm.OperazioneNuovaOModificata(nuova));
    }

    // ----- Finding 4: la copia propone solo dall'ultima revisione di ogni operazione -----

    [Fact]
    public void TrovaDurataCopia_RevisioneSuccessivaSenzaDurata_NonRiproponeIlValorePrecedente()
    {
        var store = NuovoIndice();
        var uid = Guid.NewGuid();
        // Stessa operazione (stesso Uid): 30 nella revisione 1, senza durata nella 2. Il 30 e' superato.
        Indicizza(store, Test("T-SUPERATA", "Daily A", Rev(1, Giorno, Op(uid, "", 30)), Rev(2, Giorno.AddDays(5), Op(uid, "", null))));
        Assert.Null(store.TrovaDurataCopia(["BCM"], "F1", ["Daily A"], null));

        // Un'altra operazione con durata nota, piu' vecchia, torna a essere la proposta.
        Indicizza(store, Test("T-ALTRA", "Daily A", Rev(1, Giorno.AddDays(-1), Op(Guid.NewGuid(), "", 7))));
        Assert.Equal(7, store.TrovaDurataCopia(["BCM"], "F1", ["Daily A"], null));
    }

    [Fact]
    public void TrovaDurataCopia_RevisioneSuccessivaConDurata_VinceLaPrecedente()
    {
        var store = NuovoIndice();
        var uid = Guid.NewGuid();
        Indicizza(store, Test("T-AGGIORNATA", "Daily A", Rev(1, Giorno, Op(uid, "", 30)), Rev(2, Giorno.AddDays(5), Op(uid, "", 20))));

        Assert.Equal(20, store.TrovaDurataCopia(["BCM"], "F1", ["Daily A"], null));   // il 30 della revisione 1 non e' piu' il valore dell'operazione
    }

    // ----- Finding 5: inserimento di operazioni strumento/guasto: la regola di copia parte anche li' -----

    [Fact]
    public async Task InserimentoDtc_ProponeLaDurataDallaRegolaDiCopia_SenzaSovrascriverLaCoppia()
    {
        var state = CreaStato();
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        var guasto = new Requisito { Id = "D1", Testo = "t", Guasto = [new DtcRequisito { Codice = "P0001", Componente = "ECM" }] };
        RequisitoApplicabilita.ImpostaTipo(guasto, TipoRequisito.Extra);
        state.Master.Requisiti.Save(new List<Requisito> { guasto });

        // Un Test precedente con la sola fase Validazione del guasto P0001 su ECM: 45 minuti.
        var precedente = new Condizione
        {
            Esito = Esito.Conforme, Strumento = DtcOperazioneRegole.StrumentoDtcInjection, Guasto = "P0001", DtcRequisitoId = "D1",
            DtcPasso = PassoDtc.Validazione, Componente = new EcuSelection(["ECM"]), DurataMinuti = 45,
        };
        var storico = new TestCase { Id = "T-guasto-STORICO", Componente = new EcuSelection(["ECM"]), Veicolo = "V", Veicoli = { "V" }, DataCreazione = Giorno, UltimaModifica = Giorno };
        storico.RequisitoId.Add("D1");
        storico.Esecuzioni.Add(Rev(1, Giorno, precedente));
        state.TestCases.Save(storico, isNew: true);

        var tc = new TestCase { Id = "T-guasto-NUOVO", Componente = new EcuSelection(["ECM"]), Veicolo = "V", Veicoli = { "V" } };
        tc.RequisitoId.Add("D1");
        tc.StrumentiUsati.Add("Guasto Injection");
        tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = Giorno, Esito = Esito.DaEseguire, Matricola = "x" });
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.PubblicaRisultatoCopia = azione => azione();

        Assert.Equal("ECM", vm.DtcInsEcu);                       // unica componente del test: preselezionata
        vm.DtcInsScelto = Assert.Single(vm.DtcInsDisponibili);   // P0001 del requisito D1
        vm.AggiungiDtcCommand.Execute(null);
        Assert.Null(vm.DtcInsErrore);
        await vm.AttendiCopieDurataInCorso();

        var operazioni = vm.RevisioneCorrente!.Condizioni.Where(c => c.RiguardaDtc).ToList();
        Assert.Equal(2, operazioni.Count);
        Assert.Equal(45, operazioni[0].DurataMinuti);             // Validazione: copia dal precedente
        Assert.True(operazioni[0].DurataDaCopiaAutomatica);
        Assert.Null(operazioni[1].DurataMinuti);                  // Devalidazione: nessun precedente, resta vuota
    }

    // ----- Finding 9: guardia del valore digitato dall'operatore, distacco del testo della durata -----

    [Fact]
    public void ImpostaDurataDaCopia_NonSovrascriveUnVuotoDigitatoDallOperatore()
    {
        var operazione = new Condizione { Esito = Esito.DaEseguire };
        operazione.ImpostaDurataDaCopia(10);
        Assert.Equal(10, operazione.DurataMinuti);

        operazione.DurataTesto.Testo = "";           // l'operatore cancella il valore copiato
        operazione.ImpostaDurataDaCopia(15);         // una copia arrivata dopo non lo riempie

        Assert.Null(operazione.DurataMinuti);
        Assert.True(operazione.DurataModificataDallOperatore);
    }

    [Fact]
    public void ApplicaDurataCopiata_VuotoDigitatoDuranteLaCopiaInVolo_NonSiApplica()
    {
        var state = CreaStato();
        var tc = Test("T-VUOTO-IN-VOLO", "Daily A", Rev(1, Giorno, Op(Guid.NewGuid(), "", null)));
        state.TestCases.Save(tc, isNew: true);
        var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        vm.PubblicaRisultatoCopia = _ => { };
        var operazione = vm.RevisioneCorrente!.Condizioni[0];

        var richiesta = vm.PrenotaCopiaDurata(operazione)!;
        operazione.DurataTesto.Testo = "";           // l'operatore lascia vuoto il campo mentre la copia e' in volo

        Assert.False(vm.ApplicaDurataCopiata(operazione, richiesta.Generazione, richiesta.Chiave, 15));
        Assert.Null(operazione.DurataMinuti);
    }

    [Fact]
    public void DurataTesto_DopoDispose_NonSeguePiuIlModello()
    {
        var operazione = new Condizione { Esito = Esito.DaEseguire };
        var testo = operazione.DurataTesto;

        testo.Dispose();
        operazione.DurataMinuti = 7;

        Assert.Equal("", testo.Testo);                // sottoscrizione staccata: il testo non si aggiorna dal modello
        testo.Dispose();                              // idempotente
    }
}
