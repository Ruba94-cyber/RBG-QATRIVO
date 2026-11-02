using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Regole pure del selettore guasto dell'operazione (Extra con guasto oppure strumento guasto Injection) e abbinamento.</summary>
public sealed class DtcOperazioneRegoleTests
{
    private static Requisito Extra(string id, params (string, string)[] guasto)
    {
        var r = new Requisito { Id = id, Testo = "t", Guasto = guasto.Select(d => new DtcRequisito { Codice = d.Item1, Componente = d.Item2 }).ToList() };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        return r;
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Visibilita_D4dConDtc_Oppure_DtcInjection(bool extra, bool injection, bool atteso) =>
        Assert.Equal(atteso, DtcOperazioneRegole.MostraSelettore(extra, injection));

    [Fact]
    public void Funzionalita_NonObbligatoria_QuandoIlSelettoreEAttivo()
    {
        Assert.True(DtcOperazioneRegole.FunzionalitaObbligatoria(false, 2));
        Assert.False(DtcOperazioneRegole.FunzionalitaObbligatoria(true, 2));
        Assert.False(DtcOperazioneRegole.FunzionalitaObbligatoria(false, 1));
    }

    [Fact]
    public void RiconoscimentoStrumento_PerNome_CaseInsensitive()
    {
        Assert.True(DtcOperazioneRegole.UsaDtcInjection(new[] { "UnitLogger", "guasto injection" }));
        Assert.False(DtcOperazioneRegole.UsaDtcInjection(new[] { "UnitLogger" }));
    }

    [Theory]
    [InlineData("P0123", true)]
    [InlineData("U0100-00", true)]
    [InlineData("0x123456", true)]
    [InlineData("B1234:AB", true)]
    [InlineData("", false)]
    [InlineData("P", false)]
    [InlineData("P 01", false)]
    [InlineData("P0123;", false)]
    [InlineData("é1234", false)]
    public void FormatoCodice(string codice, bool valido) => Assert.Equal(valido, DtcOperazioneRegole.CodiceValido(codice));

    [Fact]
    public void Abbina_PerCodiceEEcu_SoloRequisitiCollegati()
    {
        var d1 = Extra("D1", ("P0001", "ECM"), ("P0002", "BCM"));
        var d2 = Extra("D2", ("P0001", "BCM"));
        Assert.Equal("D1", DtcOperazioneRegole.Abbina(new[] { d2, d1 }, "p0001", new[] { "ECM" }));
        Assert.Equal("D2", DtcOperazioneRegole.Abbina(new[] { d2, d1 }, "P0001", new[] { "BCM" }));
        Assert.Null(DtcOperazioneRegole.Abbina(new[] { d1 }, "P0001", new[] { "BCM" }));   // componente diversa
        Assert.Null(DtcOperazioneRegole.Abbina(new[] { d1 }, "P9999", new[] { "ECM" }));   // codice assente
        Assert.Equal("D1", DtcOperazioneRegole.Abbina(new[] { d1 }, "P0001", Array.Empty<string>())); // componente vuote = qualunque
    }

    [Fact]
    public void Abbina_EcuVuoteConCandidatiMultipli_Ambiguo_NonAbbina()
    {
        var d1 = Extra("D1", ("P0001", "ECM"));
        var d2 = Extra("D2", ("P0001", "BCM"));
        var r = DtcOperazioneRegole.AbbinaConEsito(new[] { d2, d1 }, "P0001", Array.Empty<string>());
        Assert.Null(r.RequisitoId);
        Assert.True(r.Ambiguo);
        Assert.Null(DtcOperazioneRegole.Abbina(new[] { d2, d1 }, "P0001", Array.Empty<string>()));
        Assert.Equal(("D2", false), DtcOperazioneRegole.AbbinaConEsito(new[] { d2, d1 }, "P0001", new[] { "BCM" }));
    }

    [Fact]
    public void Abbina_MultiEcu_CodiceSoloSuUnaEcu_Abbina()
    {
        var d1 = Extra("D1", ("P0001", "BCM"));
        Assert.Equal(("D1", false), DtcOperazioneRegole.AbbinaConEsito(new[] { d1 }, "P0001", new[] { "ECM", "BCM" }));
        Assert.Equal(((string?)null, false), DtcOperazioneRegole.AbbinaConEsito(new[] { d1 }, "P0001", new[] { "ECM" }));
    }
}

[Collection("Localization")]
public sealed class DtcInjectionEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_dtc_inj_" + Guid.NewGuid().ToString("N"));
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
        return state;
    }

    private static Requisito Extra(string id, params (string, string)[] guasto)
    {
        var r = new Requisito { Id = id, Testo = "t", Guasto = guasto.Length == 0 ? null : guasto.Select(d => new DtcRequisito { Codice = d.Item1, Componente = d.Item2 }).ToList() };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        return r;
    }

    private static TestCase Test(string[] req, string[] componente, bool injection, params string[] funz)
    {
        var tc = new TestCase { Id = "T-1", Componente = new EcuSelection(componente), Veicolo = "V", Veicoli = { "V" }, Funzionalita = funz.ToList() };
        foreach (var r in req) tc.RequisitoId.Add(r);
        if (injection) tc.StrumentiUsati.Add("Guasto Injection");
        tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.DaEseguire, Matricola = "x", Condizioni = { new Condizione { Esito = Esito.DaEseguire } } });
        return tc;
    }

    [Fact]
    public void SoloDtcInjection_SenzaRequisitoD4d_SelettoreVisibile_FunzionalitaNonObbligatoria_CodiceLibero()
    {
        var state = CreaStato(new Requisito { Id = "S1", Testo = "t" });
        state.TestCases.Save(Test(new[] { "S1" }, new[] { "ECM" }, injection: true, "F1", "F2"), true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        Assert.True(vm.MostraDtcOperazione);
        Assert.True(vm.MostraDtcCodiceLibero);
        Assert.False(vm.HaRequisitoD4dConDtc);
        Assert.False(vm.DtcMostraCombo);
        Assert.False(vm.FunzionalitaOperazioneObbligatoria);   // con 2 funzionalita sarebbe obbligatoria senza selettore

        var cond = vm.TestCase.Esecuzioni[0].Condizioni[0];
        Assert.False(vm.ImpostaDtcCodiceLibero(cond, "P 01;"));   // formato non valido: nulla modificato
        Assert.Null(cond.Guasto);
        Assert.True(vm.ImpostaDtcCodiceLibero(cond, " P0123 "));
        Assert.Equal("P0123", cond.Guasto);
        Assert.Null(cond.DtcRequisitoId);                          // nessun Extra: nessun avanzamento Audit
        Assert.Equal(PassoDtc.Validazione, cond.DtcPasso);
        Assert.Equal("P0123", cond.ValoriStrumenti[DtcOperazioneRegole.ChiaveAtteso]); // precompilazione, non secondo dato
    }

    [Fact]
    public void SenzaInjectionNeD4dConDtc_NessunSelettore()
    {
        var state = CreaStato(Extra("D2"));
        state.TestCases.Save(Test(new[] { "D2" }, new[] { "ECM" }, injection: false), true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        Assert.False(vm.MostraDtcOperazione);
        Assert.False(vm.MostraDtcCodiceLibero);
    }

    [Fact]
    public void InjectionPiuD4d_CodiceDigitato_SiAbbinaPerCodiceEEcu_ENonTrovatoRestaSenzaRequisito()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM"), ("P0002", "BCM")));
        state.TestCases.Save(Test(new[] { "D1" }, new[] { "ECM", "BCM" }, injection: true), true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        Assert.True(vm.HaRequisitoD4dConDtc);
        Assert.True(vm.DtcMostraCombo);
        Assert.True(vm.MostraDtcCodiceLibero);

        var cond = vm.TestCase.Esecuzioni[0].Condizioni[0];
        cond.Componente = new EcuSelection(new[] { "ECM" });
        Assert.True(vm.ImpostaDtcCodiceLibero(cond, "p0001"));
        Assert.Equal("D1", cond.DtcRequisitoId);

        var altra = new Condizione { Esito = Esito.DaEseguire, Componente = new EcuSelection(new[] { "ECM" }) };
        vm.TestCase.Esecuzioni[0].Condizioni.Add(altra);
        Assert.True(vm.ImpostaDtcCodiceLibero(altra, "P0002"));   // P0002 esiste solo su BCM, l'operazione e su ECM
        Assert.Null(altra.DtcRequisitoId);
    }

    [Fact]
    public void Audit_DtcDellOperazioneSenzaCorrispondenza_ContatoComeNonInAnagrafica_SenzaErrore_ECorrispondenteConta()
    {
        var d1 = Extra("D1", ("P0001", "ECM"));
        var tc = Test(new[] { "D1" }, new[] { "ECM" }, injection: true);
        var c0 = tc.Esecuzioni[0].Condizioni[0];
        c0.Guasto = "P0001"; c0.DtcRequisitoId = "D1"; c0.DtcPasso = PassoDtc.Validazione; c0.Esito = Esito.Conforme;
        var c1 = new Condizione { Guasto = "P7777", Esito = Esito.Conforme, Componente = new EcuSelection(new[] { "ECM" }) };
        tc.Esecuzioni[0].Condizioni.Add(c1);
        var r = DtcAvanzamentoService.Calcola(new[] { d1 }, new[] { tc });
        Assert.Equal(1, r.CelleSenzaAnagrafica);
        Assert.Equal(StatoDtc.Validato, Assert.Single(r.PerDtc).Stato);
    }

    [Fact]
    public void Stato_ConTutteLeEcuDellOperazione_EAvvisoRequisitoScollegato()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM"), ("P0001", "BCM")));
        state.TestCases.Save(Test(new[] { "D1" }, new[] { "ECM", "BCM" }, injection: false), true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        var cond = vm.TestCase.Esecuzioni[0].Condizioni[0];
        cond.Guasto = "P0001"; cond.DtcRequisitoId = "D1"; cond.Componente = new EcuSelection(new[] { "ECM", "BCM" });
        vm.TestCase.RequisitoId.Clear();   // requisito scollegato
        vm.GetType().GetMethod("NotificaDtcOpzioni", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, null);
        Assert.False(string.IsNullOrEmpty(cond.DtcAvviso));
        Assert.Contains("D1", cond.DtcAvviso);
        // Requisito scollegato ma operazione con requisito: lo stato NON deve essere il fuorviante "non in anagrafica Extra".
        Assert.DoesNotContain("anagrafica Extra", cond.DtcStatoTesto ?? "");
    }

    private static void Notifica(TestCaseEditorViewModel vm) =>
        vm.GetType().GetMethod("NotificaDtcOpzioni", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, null);

    private static StrumentoOption Injection(TestCaseEditorViewModel vm) =>
        vm.StrumentiOptions.Single(o => o.Valore == "Guasto Injection");

    [Fact]
    public void Riabbina_SoloOperazioniNonEseguiteDellaRevCorrente_MaiLoStorico()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        var tc = Test(new[] { "S9" }, new[] { "ECM" }, injection: true);
        var storica = new Esecuzione { Iterazione = 1, Data = DateTime.Now.AddDays(-2), Esito = Esito.Conforme, Matricola = "x",
            Condizioni = { new Condizione { Guasto = "P0001", Esito = Esito.Conforme, Componente = new EcuSelection(new[] { "ECM" }) } } };
        var corrente = tc.Esecuzioni[0];
        corrente.Iterazione = 2;
        corrente.Condizioni.Add(new Condizione { Guasto = "P0001", Esito = Esito.DaEseguire, Componente = new EcuSelection(new[] { "ECM" }) });
        corrente.Condizioni.Add(new Condizione { Guasto = "P0001", Esito = Esito.Conforme, Componente = new EcuSelection(new[] { "ECM" }) });
        tc.Esecuzioni.Insert(0, storica);
        state.TestCases.Save(tc, true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        vm.TestCase.RequisitoId.Add("D1");
        Notifica(vm);
        var cur = vm.TestCase.Esecuzioni[^1];
        Assert.Equal("D1", cur.Condizioni.Single(c => c.Esito == Esito.DaEseguire && c.Guasto == "P0001").DtcRequisitoId);
        Assert.Null(cur.Condizioni.Single(c => c.Esito == Esito.Conforme).DtcRequisitoId);   // gia eseguita: invariata
        Assert.Null(vm.TestCase.Esecuzioni[0].Condizioni[0].DtcRequisitoId);                   // storico: invariato
    }

    [Fact]
    public async Task DeselezioneInjection_ConDtc_Rimuovi_SoloNonEseguite_StoricoMantenuto()
    {
        var state = CreaStato(new Requisito { Id = "S1", Testo = "t" });
        var tc = Test(new[] { "S1" }, new[] { "ECM" }, injection: true, "F1", "F2");
        var rev = tc.Esecuzioni[0];
        rev.Condizioni[0].Guasto = "P0001"; rev.Condizioni[0].DtcPasso = PassoDtc.Validazione;
        rev.Condizioni.Add(new Condizione { Guasto = "P0002", DtcPasso = PassoDtc.Validazione, Esito = Esito.Conforme });
        state.TestCases.Save(tc, true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        var chiesto = 0;
        vm.ConfermaRimozioneDtc = n => { chiesto = n; return Task.FromResult(true); };
        Injection(vm).Selezionato = false;
        await vm.DeselezioneDtcInCorso;
        Assert.Equal(2, chiesto);
        Assert.False(vm.MostraDtcOperazione);
        Assert.Null(vm.TestCase.Esecuzioni[0].Condizioni[0].Guasto);                // non eseguita: azzerata
        Assert.Equal("P0002", vm.TestCase.Esecuzioni[0].Condizioni[1].Guasto);      // eseguita: storico mantenuto
        Assert.True(vm.FunzionalitaOperazioneObbligatoria);                       // 2 funzionalita, selettore spento
        Assert.Empty(vm.FunzionalitaNonCoperte);                                  // ma l'operazione con Guasto non blocca
    }

    [Fact]
    public async Task DeselezioneInjection_ConDtc_Mantieni_RiselezionaLoStrumento_NienteRimosso()
    {
        var state = CreaStato(new Requisito { Id = "S1", Testo = "t" });
        var tc = Test(new[] { "S1" }, new[] { "ECM" }, injection: true, "F1", "F2");
        tc.Esecuzioni[0].Condizioni[0].Guasto = "P0001";
        state.TestCases.Save(tc, true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        vm.ConfermaRimozioneDtc = _ => Task.FromResult(false);
        Injection(vm).Selezionato = false;
        await vm.DeselezioneDtcInCorso;
        Assert.True(Injection(vm).Selezionato);
        Assert.True(vm.MostraDtcOperazione);
        Assert.Equal("P0001", vm.TestCase.Esecuzioni[0].Condizioni[0].Guasto);
    }

    [Fact]
    public async Task DeselezioneInjection_SenzaOperazioniDtc_NessunaConferma()
    {
        var state = CreaStato(new Requisito { Id = "S1", Testo = "t" });
        state.TestCases.Save(Test(new[] { "S1" }, new[] { "ECM" }, injection: true), true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        vm.ConfermaRimozioneDtc = _ => throw new InvalidOperationException("non deve chiedere");
        Injection(vm).Selezionato = false;
        await vm.DeselezioneDtcInCorso;
        Assert.False(vm.MostraDtcOperazione);
    }

    [Fact]
    public void CambioRequisito_DopoDigitazione_RiabbinaLOperazioneNonEseguita()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        state.TestCases.Save(Test(new[] { "S9" }, new[] { "ECM" }, injection: true), true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        var cond = vm.TestCase.Esecuzioni[0].Condizioni[0];
        cond.Componente = new EcuSelection(new[] { "ECM" });
        Assert.True(vm.ImpostaDtcCodiceLibero(cond, "P0001"));
        Assert.Null(cond.DtcRequisitoId);                    // D1 non ancora collegato
        vm.TestCase.RequisitoId.Add("D1");
        Notifica(vm);
        Assert.Equal("D1", cond.DtcRequisitoId);
    }

    [Fact]
    public void PrecompilazioneDivergente_NonSovrascritta_ValeIlDtcDellOperazione()
    {
        var state = CreaStato(new Requisito { Id = "S1", Testo = "t" });
        state.TestCases.Save(Test(new[] { "S1" }, new[] { "ECM" }, injection: true), true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        var cond = vm.TestCase.Esecuzioni[0].Condizioni[0];
        cond.Componente = new EcuSelection(new[] { "ECM" });
        cond.ValoriStrumenti[DtcOperazioneRegole.ChiaveAtteso] = "P9999";   // scelto a mano
        Assert.True(vm.ImpostaDtcCodiceLibero(cond, "P0001"));
        Assert.Equal("P9999", cond.ValoriStrumenti[DtcOperazioneRegole.ChiaveAtteso]);
        Assert.Equal("P0001", cond.Guasto);
    }

    [Fact]
    public void FunzionalitaEDtcInsieme_AvvisoNonContaNellaCopertura()
    {
        var state = CreaStato(new Requisito { Id = "S1", Testo = "t" });
        state.TestCases.Save(Test(new[] { "S1" }, new[] { "ECM" }, injection: true, "F1"), true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        var cond = vm.TestCase.Esecuzioni[0].Condizioni[0];
        cond.Componente = new EcuSelection(new[] { "ECM" });
        cond.Funzionalita = "F1";
        Assert.True(vm.ImpostaDtcCodiceLibero(cond, "P0001"));
        Notifica(vm);
        Assert.Equal(LocalizationService.Instance.Traduci("DtcAvvisoFunzionalitaIgnorata"), cond.DtcAvviso);
    }

    [Fact]
    public void EcuVuotaConCandidatiMultipli_AvvisoAbbinamentoAmbiguo()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")), Extra("D2", ("P0001", "BCM")));
        state.TestCases.Save(Test(new[] { "D1", "D2" }, Array.Empty<string>(), injection: true), true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        var cond = vm.TestCase.Esecuzioni[0].Condizioni[0];
        cond.Componente = new EcuSelection(Array.Empty<string>());
        Assert.True(vm.ImpostaDtcCodiceLibero(cond, "P0001"));
        Notifica(vm);
        Assert.Null(cond.DtcRequisitoId);
        Assert.Contains("ambiguo", cond.DtcAvviso, StringComparison.OrdinalIgnoreCase);
    }
}

[Collection("Localization")]
public sealed class DtcImportCorrezioniTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_dtc_imp_" + Guid.NewGuid().ToString("N"));
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
        return state;
    }

    private static Requisito Extra(string id, params (string, string)[] guasto)
    {
        var r = new Requisito { Id = id, Testo = "t", Guasto = guasto.Length == 0 ? null : guasto.Select(d => new DtcRequisito { Codice = d.Item1, Componente = d.Item2 }).ToList() };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        return r;
    }

    // F6 ----------------------------------------------------------------------------------------------------------
    [Fact]
    public void F6_NumeroRigaReale_ConRigheVuote()
    {
        var csv = "Requisito;guasto;componente\n\nD1;P1;ECM\n\n\nD1;;ECM\n";
        var r = DtcImportRequisitoService.Interpreta(DtcImportService.LeggiCsvStreaming(new StringReader(csv)), _ => Array.Empty<DtcRequisito>());
        Assert.Single(r.Importate);
        Assert.Contains("riga 6", Assert.Single(r.Scartate));
    }

    [Fact]
    public void F6_DelimitatoreRilevatoIgnorandoLeVirgolette()
    {
        var righe = DtcImportService.LeggiCsv("\"Requisito, con virgola, e altre, virgole\";guasto;componente\nD1;P1;ECM");
        Assert.Equal(3, righe[1].Count);
        Assert.Equal("P1", righe[1][1]);
    }

    // F5 ----------------------------------------------------------------------------------------------------------
    [Fact]
    public void F5_AvvisoMorbidoOltre5000_NessunLimiteRigido()
    {
        var righe = Enumerable.Range(0, DtcImportRequisitoService.SogliaAvvisoDtcPerRequisito + 10)
            .Select(i => new List<string> { "D1", "P" + i, "ECM" }).ToList();
        var r = DtcImportRequisitoService.Interpreta(righe, _ => Array.Empty<DtcRequisito>());
        Assert.Equal(DtcImportRequisitoService.SogliaAvvisoDtcPerRequisito + 10, r.Importate.Count);
        Assert.Contains("D1", Assert.Single(r.Avvisi!));
        var pochi = DtcImportRequisitoService.Interpreta(righe.Take(10).ToList(), _ => Array.Empty<DtcRequisito>());
        Assert.True(pochi.Avvisi == null || pochi.Avvisi.Count == 0);
    }

    // F1 + F3 -----------------------------------------------------------------------------------------------------
    [Fact]
    public async Task F1_F3_ImportMassivo_TracciaChangelogDtc_ERichiedeConfermaSeCiSonoRigheScartate()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")), Extra("D2"));
        using var vm = new RequisitiViewModel(state, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        var righe = DtcImportService.LeggiCsv("Requisito;guasto;componente\nD1;P0002;ECM\nD2;P0003;BCM\nXX;P9;ECM\n");
        await vm.ImportaDtcDaRighe(righe, massivo: true);
        Assert.Single(vm.UltimoReportDtc!.Scartate);

        await vm.SalvaCommand.ExecuteAsync(null);                                  // prima pressione: conferma richiesta, nulla salvato
        Assert.True(vm.InModifica);
        Assert.Contains("1", vm.MessaggioErrore ?? "");
        Assert.Single(state.Master.RequisitiDtc.Leggi(state.Master.Requisiti.Load().Single(r => r.Id == "D1")).Voci);

        await vm.SalvaCommand.ExecuteAsync(null);                                  // seconda: salva
        var salvati = state.Master.Requisiti.Load();
        var d1 = salvati.Single(r => r.Id == "D1");
        Assert.Null(d1.Guasto);                                            // i guasti non stanno piu nel JSON del requisito
        Assert.Equal(2, d1.DtcConteggio);
        Assert.Equal(2, state.Master.RequisitiDtc.Leggi(d1).Voci.Count);
        Assert.Contains(d1.Changelog, c => c.Campo == "Guasto" && c.ValoreNuovo == "+1/-0 guasto");
        Assert.Contains(salvati.Single(r => r.Id == "D2").Changelog, c => c.Campo == "Guasto" && c.ValoreNuovo == "+1/-0 guasto");
        Assert.NotNull(d1.UltimaModifica);
    }

    [Fact]
    public async Task F3_ImportCsv_RifiutatoPerColonnaMancante_NienteApplicato()
    {
        var state = CreaStato(Extra("D1"));
        using var vm = new RequisitiViewModel(state, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        var csv = Path.Combine(_dir, "x.csv");
        File.WriteAllText(csv, "Requisito;guasto\nD1;P1\n");
        await vm.ImportaDtcDaCsvAsync(csv, massivo: true);
        Assert.False(vm.ImportDtcInCorso);
        Assert.Empty(state.Master.Requisiti.Load().Single().DtcVoci);
        Assert.Empty(vm.Items.Single().DtcVoci);
    }
}

public sealed class DtcOperazioniCoperturaMatriceTests
{
    [Fact]
    public void Coverage_RevMista_NonUsaLEsitoComprensivoDelleDtc()
    {
        var tc = new TestCase { Id = "T", Funzionalita = { "F1" } };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = DateTime.Now, Matricola = "x", Esito = Esito.NonConforme,   // esito complessivo peggiorato dalla guasto
            Condizioni =
            {
                new Condizione { Esito = Esito.Conforme },
                new Condizione { Guasto = "P1", Esito = Esito.NonConforme }
            }
        });
        var bucket = CoverageService.PerFunzionalita(new List<Funzionalita> { new() { Nome = "F1" } }, new List<TestCase> { tc });
        Assert.Equal(1, bucket.Conformi);
    }

    [Fact]
    public void Matrice_RevDiSoleOperazioniDtc_CombinazioneDichiarataNonValutata_NonSparisce()
    {
        var tc = new TestCase { Id = "T", Componente = new EcuSelection(new[] { "BCM" }), Veicoli = { "V" }, Funzionalita = { "F1" } };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = DateTime.Now, Matricola = "x", Esito = Esito.Conforme,
            Condizioni = { new Condizione { Guasto = "P1", Esito = Esito.Conforme, Componente = new EcuSelection(new[] { "BCM" }) } }
        });
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Funzionalita };
        var risultato = ReportMatriceService.Genera([tc], config, [], [], []);
        var cella = Assert.Single(risultato.Righe.SelectMany(r => r.Celle));
        Assert.Equal(1, cella.Totali);
        Assert.Equal(0, cella.Valutati);
        Assert.Equal(0, cella.Conformi);
    }
}
