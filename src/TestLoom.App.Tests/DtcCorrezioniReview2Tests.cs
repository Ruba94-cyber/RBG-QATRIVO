using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Seconda tornata di correzioni 6.5.1 dopo review indipendente: dati compilati dall'utente su operazioni ancora "Da eseguire".</summary>
public sealed class DtcCorrezioniReview2RegoleTests
{
    [Fact]
    public void DatiUtente_NotaTestiValoriSegnalazione_SiMaNonLaPrecompilazioneAutomaticaDelDtc()
    {
        Assert.False(DtcOperazioneRegole.HaDatiUtente(new Condizione { Esito = Esito.DaEseguire }));
        Assert.True(DtcOperazioneRegole.HaDatiUtente(new Condizione { Nota = " x " }));
        Assert.True(DtcOperazioneRegole.HaDatiUtente(new Condizione { DescrizioneProblema = "y" }));
        Assert.True(DtcOperazioneRegole.HaDatiUtente(new Condizione { SegnalazioneUid = Guid.NewGuid() }));
        Assert.True(DtcOperazioneRegole.HaDatiUtente(new Condizione { ValoriStrumenti = { ["UnitLogger::Comando"] = "22 F1 90" } }));
        var auto = new Condizione { Guasto = "P0001", ValoriStrumenti = { [DtcOperazioneRegole.ChiaveAtteso] = "P0001" } };
        Assert.False(DtcOperazioneRegole.HaDatiUtente(auto));                                    // precompilazione dal guasto: non e un input
        auto.ValoriStrumenti[DtcOperazioneRegole.ChiaveAtteso] = "P0002";
        Assert.True(DtcOperazioneRegole.HaDatiUtente(auto));                                     // valore scelto a mano
        Assert.Equal(new[] { "Nota" }, DtcOperazioneRegole.DatiUtente(new Condizione { Nota = "n" }).ToArray());
    }
}

[Collection("Localization")]
public sealed partial class DtcCorrezioniReview2EditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_dtcrev2_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private AppState CreaStato(params Requisito[] reqs)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        state.Master.Requisiti.Save(reqs.ToList());
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico> { new() { Nome = "UnitLogger" }, new() { Nome = "Guasto Injection" } });
        return state;
    }

    private static Requisito Extra(string id, params (string Codice, string Componente, string? Veicolo)[] guasto)
    {
        var r = new Requisito { Id = id, Testo = "t", Guasto = guasto.Select(d => new DtcRequisito { Codice = d.Codice, Componente = d.Componente, Veicolo = d.Veicolo }).ToList() };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        return r;
    }

    private static TestCase Test(string[] req, string[] funz, string[]? strumenti = null, string[]? veicoli = null)
    {
        veicoli ??= new[] { "V" };
        var tc = new TestCase { Id = "T-1", Componente = new EcuSelection(new[] { "ECM" }), Veicolo = veicoli[0], Veicoli = veicoli.ToList(), Funzionalita = funz.ToList() };
        foreach (var r in req) tc.RequisitoId.Add(r);
        foreach (var s in strumenti ?? Array.Empty<string>()) tc.StrumentiUsati.Add(s);
        tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.DaEseguire, Matricola = "x" });
        return tc;
    }

    private static TestCaseEditorViewModel Apri(AppState state, TestCase tc)
    {
        state.TestCases.Save(tc, true);
        return new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
    }

    private static List<Condizione> Ops(TestCaseEditorViewModel vm) => vm.RevisioneCorrente!.Condizioni;
    private static StrumentoOption Opt(TestCaseEditorViewModel vm, string nome) => vm.StrumentiOptions.Single(o => o.Valore == nome);

    // ---- 3) perdita di input ------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeselezioneStrumento_ConOperazioniDaEseguireMaCompilate_ChiedeConfermaConElencoDeiDati_ENegataNulla_Rimosso()
    {
        var vm = Apri(CreaStato(), Test(Array.Empty<string>(), new[] { "F1", "F2" }));
        Opt(vm, "UnitLogger").Selezionato = true;
        var f2 = Ops(vm).Single(o => o.Strumento == "UnitLogger" && o.Funzionalita == "F2");
        f2.Nota = "misura importante"; f2.ValoriStrumenti["UnitLogger::Comando"] = "22 F1 90";
        IReadOnlyList<string>? elenco = null;
        vm.ConfermaPerditaDatiOperazioni = e => { elenco = e; return Task.FromResult(false); };
        vm.ConfermaRimozioneOperazioniStrumento = (_, _) => Task.FromResult(true);
        Opt(vm, "UnitLogger").Selezionato = false;
        await vm.DeselezioneDtcInCorso;
        Assert.NotNull(elenco);
        var riga = Assert.Single(elenco!);
        Assert.Contains("UnitLogger", riga); Assert.Contains("F2", riga);
        Assert.True(Opt(vm, "UnitLogger").Selezionato);
        Assert.Equal(2, Ops(vm).Count(o => o.Strumento == "UnitLogger"));
        Assert.Equal("misura importante", f2.Nota);

        vm.ConfermaPerditaDatiOperazioni = e => Task.FromResult(true);          // conferma esplicita: ora elimina
        Opt(vm, "UnitLogger").Selezionato = false;
        await vm.DeselezioneDtcInCorso;
        Assert.Empty(Ops(vm).Where(o => o.Strumento == "UnitLogger"));
    }

    [Fact]
    public async Task RimozioneFunzionalita_NonCancellaInSilenzio_LeOperazioniCompilate()
    {
        var vm = Apri(CreaStato(), Test(Array.Empty<string>(), new[] { "F1", "F2" }));
        Opt(vm, "UnitLogger").Selezionato = true;
        var f2 = Ops(vm).Single(o => o.Strumento == "UnitLogger" && o.Funzionalita == "F2");
        f2.DescrizioneDeviazione = "tempo lungo";
        var richieste = 0;
        vm.ConfermaPerditaDatiOperazioni = _ => { richieste++; return Task.FromResult(false); };
        await vm.RimuoviFunzionalitaCommand.ExecuteAsync("F2");
        Assert.Equal(1, richieste);
        Assert.Contains("F2", vm.TestCase.Funzionalita);                           // negata: la funzionalita resta
        Assert.Equal(2, Ops(vm).Count(o => o.Strumento == "UnitLogger"));

        vm.ConfermaPerditaDatiOperazioni = _ => Task.FromResult(true);
        await vm.RimuoviFunzionalitaCommand.ExecuteAsync("F2");
        Assert.DoesNotContain("F2", vm.TestCase.Funzionalita);
        Assert.Equal(new[] { "F1" }, Ops(vm).Where(o => o.Strumento == "UnitLogger").Select(o => o.Funzionalita).ToArray());
    }

    [Fact]
    public async Task RimozioneFunzionalita_ConOperazioniVuote_NessunaConferma()
    {
        var vm = Apri(CreaStato(), Test(Array.Empty<string>(), new[] { "F1", "F2" }));
        Opt(vm, "UnitLogger").Selezionato = true;
        vm.ConfermaPerditaDatiOperazioni = _ => throw new InvalidOperationException("non deve chiedere");
        await vm.RimuoviFunzionalitaCommand.ExecuteAsync("F2");
        Assert.Equal(new[] { "F1" }, Ops(vm).Where(o => o.Strumento == "UnitLogger").Select(o => o.Funzionalita).ToArray());
    }

    [Fact]
    public async Task RimozioneRigaDtc_ConDatiCompilati_RichiedeConferma_SenzaDatiNonChiede()
    {
        var vm = Apri(CreaStato(Extra("D1", ("P0001", "ECM", null))), Test(new[] { "D1" }, new[] { "F1" }, new[] { "Guasto Injection" }));
        Assert.Null(vm.AggiungiDtcInjection("ECM", "P0001", "D1"));
        Assert.Null(vm.AggiungiDtcInjection("ECM", "P0002", null));
        var conDati = vm.DtcInseriti.Single(r => r.Codice == "P0001");
        conDati.Operazioni[0].Nota = "osservazione";
        Assert.Equal("DtcInsConDati", vm.RimuoviDtcInserito(conDati));              // senza conferma: nulla rimosso
        Assert.Equal(4, Ops(vm).Count);

        vm.ConfermaPerditaDatiOperazioni = _ => Task.FromResult(false);
        Assert.False(await vm.RimuoviDtcInseritoConfermaAsync(conDati));
        Assert.Equal(4, Ops(vm).Count);
        vm.ConfermaPerditaDatiOperazioni = _ => Task.FromResult(true);
        Assert.True(await vm.RimuoviDtcInseritoConfermaAsync(conDati));
        Assert.Equal(2, Ops(vm).Count);

        vm.ConfermaPerditaDatiOperazioni = _ => throw new InvalidOperationException("non deve chiedere");
        Assert.True(await vm.RimuoviDtcInseritoConfermaAsync(vm.DtcInseriti.Single()));   // operazioni vuote: senza conferma
        Assert.Empty(Ops(vm));
    }
}
