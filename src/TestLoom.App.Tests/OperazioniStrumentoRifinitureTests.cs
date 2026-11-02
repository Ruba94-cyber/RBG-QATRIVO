using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>6.5.1 fase 4, rifiniture della fase 3: l'operazione vuota iniziale viene adottata (non duplicata) e aggiungere/rimuovere una
/// funzionalita dopo aver selezionato uno strumento Da testare crea/adegua le sue operazioni (rimozione solo senza esiti).</summary>
[Collection("Localization")]
public sealed class OperazioniStrumentoRifinitureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_opstr2_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        var req = new Requisito { Id = "D1", Testo = "t", Guasto = new List<DtcRequisito> { new() { Codice = "P0001", Componente = "ECM" } } };
        RequisitoApplicabilita.ImpostaTipo(req, TipoRequisito.Extra);
        state.Master.Requisiti.Save(new List<Requisito> { req });
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico> { new() { Nome = "UnitLogger" }, new() { Nome = "Guasto Injection" } });
        return state;
    }

    private static TestCase Test(string[] funz, params Condizione[] ops)
    {
        var tc = new TestCase { Id = "T-1", Componente = new EcuSelection(new[] { "ECM" }), Veicolo = "V", Veicoli = { "V" }, Funzionalita = funz.ToList() };
        tc.RequisitoId.Add("D1");
        var rev = new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.DaEseguire, Matricola = "x" };
        rev.Condizioni.AddRange(ops);
        tc.Esecuzioni.Add(rev);
        return tc;
    }

    private static Condizione Vuota() => new() { Esito = Esito.DaEseguire, Componente = new EcuSelection(new[] { "ECM" }) };

    private static TestCaseEditorViewModel Apri(AppState state, TestCase tc)
    {
        state.TestCases.Save(tc, true);
        return new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
    }

    private static StrumentoOption Opt(TestCaseEditorViewModel vm, string nome) => vm.StrumentiOptions.Single(o => o.Valore == nome);
    private static List<Condizione> Ops(TestCaseEditorViewModel vm) => vm.RevisioneCorrente!.Condizioni;
    private static List<Condizione> Proprie(TestCaseEditorViewModel vm, string nome) => Ops(vm).Where(c => c.Strumento == nome).ToList();

    [Fact]
    public void SelezioneStrumento_AdottaLOperazioneVuotaIniziale_SenzaLasciareUnaRigaVuota()
    {
        var vuota = Vuota();
        var vm = Apri(CreaStato(), Test(new[] { "F1", "F2" }, vuota));
        Opt(vm, "UnitLogger").Selezionato = true;
        Assert.Equal(2, Ops(vm).Count);                                      // niente riga vuota in piu
        Assert.Equal(vuota.Uid, Ops(vm)[0].Uid);                             // stessa operazione, adottata
        Assert.Equal(("UnitLogger", "F1"), (Ops(vm)[0].Strumento, Ops(vm)[0].Funzionalita));
        Assert.Equal(new[] { "F1", "F2" }, Proprie(vm, "UnitLogger").Select(c => c.Funzionalita).ToArray());
        Assert.Equal(2, vm.CondizioniVisual.Count);
    }

    [Fact]
    public void OperazioneToccata_NonSiAdotta()
    {
        var vm = Apri(CreaStato(), Test(new[] { "F1" }, new Condizione { Esito = Esito.DaEseguire, Nota = "scritta a mano", Componente = new EcuSelection(new[] { "ECM" }) }));
        Opt(vm, "UnitLogger").Selezionato = true;
        Assert.Equal(2, Ops(vm).Count);
        Assert.Null(Ops(vm)[0].Strumento);
        Assert.Equal("scritta a mano", Ops(vm)[0].Nota);

        var conEsito = Apri2(new Condizione { Esito = Esito.Conforme, Componente = new EcuSelection(new[] { "ECM" }) });
        Opt(conEsito, "UnitLogger").Selezionato = true;
        Assert.Equal(2, Ops(conEsito).Count);
        Assert.Null(Ops(conEsito)[0].Strumento);

        var conValori = Apri2(new Condizione { Esito = Esito.DaEseguire, ValoriStrumenti = { ["UnitLogger::X"] = "1" } });
        Opt(conValori, "UnitLogger").Selezionato = true;
        Assert.Equal(2, Ops(conValori).Count);
    }

    private TestCaseEditorViewModel Apri2(Condizione c)
    {
        var dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico> { new() { Nome = "UnitLogger" } });
        return Apri(state, Test(new[] { "F1" }, c));
    }

    [Fact]
    public void SoloDtc_RimuoveLOperazioneVuota_EAggiuntaCreaLaCoppia()
    {
        var vuota = Vuota();
        var vm = Apri(CreaStato(), Test(new[] { "F1" }, vuota));
        Opt(vm, "Guasto Injection").Selezionato = true;
        Assert.Empty(Ops(vm));
        Assert.Null(vm.AggiungiDtcInjection("ECM", "P0001", "D1"));
        Assert.Equal(2, Ops(vm).Count);
        Assert.DoesNotContain(Ops(vm), c => c.Uid == vuota.Uid);
        Assert.Equal(new[] { PassoDtc.Validazione, PassoDtc.Devalidazione }, Ops(vm).Select(c => c.DtcPasso!.Value).ToArray());
        Assert.All(Ops(vm), c => Assert.Equal("Guasto Injection", c.Strumento));
        Assert.Equal("P0001", Ops(vm)[0].ValoriStrumenti[DtcOperazioneRegole.ChiaveAtteso]);
        Assert.Equal(2, Assert.Single(vm.DtcInseriti).Operazioni.Count);
    }

    [Fact]
    public void FunzionalitaAggiuntaDopoLoStrumento_CreaLaOperazioneMancante_Idempotente()
    {
        var vm = Apri(CreaStato(), Test(new[] { "F1" }));
        Opt(vm, "UnitLogger").Selezionato = true;
        Assert.Single(Proprie(vm, "UnitLogger"));
        vm.NuovaFunzionalita = "F2";
        vm.AggiungiFunzionalitaCommand.Execute(null);
        Assert.Equal(new[] { "F1", "F2" }, Proprie(vm, "UnitLogger").Select(c => c.Funzionalita).ToArray());
        Assert.All(Proprie(vm, "UnitLogger"), c => { Assert.Equal(Esito.DaEseguire, c.Esito); Assert.Equal(new[] { "ECM" }, c.Componente.ToArray()); });
        Assert.Equal(2, vm.CondizioniVisual.Count);
        Assert.True(vm.ModificheNonSalvate);
        vm.NuovaFunzionalita = "F2";
        vm.AggiungiFunzionalitaCommand.Execute(null);                        // gia presente: nulla
        Assert.Equal(2, Proprie(vm, "UnitLogger").Count);
    }

    [Fact]
    public void FunzionalitaRimossa_EliminaLeOperazioniSenzaEsiti_LegacyIntatte()
    {
        var legacy = new Condizione { Esito = Esito.DaEseguire, Nota = "legacy", Funzionalita = "F2", Componente = new EcuSelection(new[] { "ECM" }) };
        var vm = Apri(CreaStato(), Test(new[] { "F1", "F2" }, legacy));
        Opt(vm, "UnitLogger").Selezionato = true;
        Assert.Equal(new[] { "F1", "F2" }, Proprie(vm, "UnitLogger").Select(c => c.Funzionalita).ToArray());
        vm.RimuoviFunzionalitaCommand.Execute("F2");
        Assert.Equal(new[] { "F1" }, Proprie(vm, "UnitLogger").Select(c => c.Funzionalita).ToArray());
        Assert.False(vm.HaAvvisoStrumentiOperazioni);
        Assert.Contains(Ops(vm), c => c.Nota == "legacy" && c.Strumento == null);                                    // la legacy resta (funzionalita riallineata come prima)
        Assert.Equal(2, vm.CondizioniVisual.Count);
    }

    [Fact]
    public void FunzionalitaRimossa_ConEsito_LeOperazioniRestanoESiSegnala()
    {
        var vm = Apri(CreaStato(), Test(new[] { "F1", "F2" }));
        Opt(vm, "UnitLogger").Selezionato = true;
        var f2 = Proprie(vm, "UnitLogger").Single(c => c.Funzionalita == "F2");
        f2.Esito = Esito.NonConforme;
        vm.RimuoviFunzionalitaCommand.Execute("F2");
        Assert.Equal(2, Proprie(vm, "UnitLogger").Count);
        Assert.Equal("F2", f2.Funzionalita);                                 // conserva la funzionalita (storico)
        Assert.True(vm.HaAvvisoStrumentiOperazioni);
        Assert.Contains("UnitLogger", vm.AvvisoStrumentiOperazioni);
        Assert.Contains("F2", vm.AvvisoStrumentiOperazioni);
    }
}
