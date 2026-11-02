using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>6.5.1 fase 4, regole pure del blocco "Aggiungi guasto" del Test: guasto disponibili per componente/veicolo e validazione di UN guasto.</summary>
public sealed class DtcInjectionAggiuntaRegoleTests
{
    private static Requisito Extra(string id, params (string Codice, string Componente, string? Veicolo)[] guasto)
    {
        var r = new Requisito { Id = id, Testo = "t", Guasto = guasto.Select(d => new DtcRequisito { Codice = d.Codice, Componente = d.Componente, Veicolo = d.Veicolo }).ToList() };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        return r;
    }

    [Fact]
    public void DtcDisponibili_FiltraPerEcuEVeicolo_SenzaVeicoloVaBeneOvunque_EsclusiIGiaPresenti()
    {
        var d1 = Extra("D1", ("P1", "ECM", null), ("P2", "ECM", "A"), ("P3", "ECM", "B"), ("P4", "BCM", null));
        var d2 = Extra("D2", ("P1", "ECM", null));
        var tutti = DtcOperazioneRegole.DtcDisponibili(new[] { d1, d2 }, r => r.DtcVoci, new[] { "A" }, "ecm");
        Assert.Equal(new[] { "P1·D1", "P1·D2", "P2·D1" }, tutti.Select(o => o.Codice + "·" + o.RequisitoId).ToArray());   // P3 e di B, P4 e di BCM
        Assert.Empty(DtcOperazioneRegole.DtcDisponibili(new[] { d1 }, r => r.DtcVoci, new[] { "A" }, null));            // componente non scelta
        Assert.Equal(3, DtcOperazioneRegole.DtcDisponibili(new[] { d1 }, r => r.DtcVoci, Array.Empty<string>(), "ECM").Count);   // veicoli del test vuoti = nessun filtro
        var senzaGia = DtcOperazioneRegole.DtcDisponibili(new[] { d1, d2 }, r => r.DtcVoci, new[] { "A" }, "ECM", o => o.Codice == "P1" && o.RequisitoId == "D1");
        Assert.Equal(new[] { "P1·D2", "P2·D1" }, senzaGia.Select(o => o.Codice + "·" + o.RequisitoId).ToArray());
    }

    [Fact]
    public void DtcDisponibili_SoloRequisitiD4d()
    {
        var std = new Requisito { Id = "S1", Testo = "t", Guasto = new List<DtcRequisito> { new() { Codice = "P1", Componente = "ECM" } } };
        Assert.Empty(DtcOperazioneRegole.DtcDisponibili(new[] { std }, r => r.DtcVoci, Array.Empty<string>(), "ECM"));
    }

    [Theory]
    [InlineData("P0123", "ECM", null, false, null)]
    [InlineData("P 01", "ECM", null, false, "DtcCodiceNonValido")]
    [InlineData("P0123", "", null, false, "DtcEcuMancante")]
    [InlineData("P0123", "BCM", null, false, "DtcEcuNonInAnagrafica")]
    [InlineData("P0123", "ECM", "D1", false, null)]
    [InlineData("P0123", "ECM", "D9", false, "DtcRequisitoNonCollegato")]
    [InlineData("P0123", "ECM", "D1", true, "DtcGiaPresente")]
    public void ValidaAggiuntaDtc(string codice, string componente, string? req, bool presente, string? atteso) =>
        Assert.Equal(atteso, DtcOperazioneRegole.ValidaAggiuntaDtc(codice, componente, new[] { "ECM" }, req, new[] { "D1" }, presente));
}

/// <summary>6.5.1 fase 4, editor Test: "Aggiungi guasto" (un guasto alla volta, da elenco o manuale) con la coppia Validazione/Devalidazione.</summary>
[Collection("Localization")]
public sealed class DtcInjectionAggiuntaEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_dtcins_" + Guid.NewGuid().ToString("N"));
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

    private static Requisito Extra(string id, params (string Codice, string Componente)[] guasto)
    {
        var r = new Requisito { Id = id, Testo = "t", Guasto = guasto.Select(d => new DtcRequisito { Codice = d.Codice, Componente = d.Componente }).ToList() };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        return r;
    }

    private static TestCase Test(string[] req, params string[] componente)
    {
        var tc = new TestCase { Id = "T-1", Componente = new EcuSelection(componente), Veicolo = "V", Veicoli = { "V" } };
        foreach (var r in req) tc.RequisitoId.Add(r);
        tc.StrumentiUsati.Add("Guasto Injection");
        tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.DaEseguire, Matricola = "x" });
        return tc;
    }

    private static TestCaseEditorViewModel Apri(AppState state, TestCase tc)
    {
        state.TestCases.Save(tc, true);
        return new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
    }

    private static List<Condizione> Ops(TestCaseEditorViewModel vm) => vm.RevisioneCorrente!.Condizioni;

    [Fact]
    public void DaElenco_UsaIlRequisitoDiProvenienza_EGeneraLaCoppiaConPropriEsiti()
    {
        var vm = Apri(CreaStato(Extra("D1", ("P0001", "ECM"), ("P0002", "BCM"))), Test(new[] { "D1" }, "ECM"));
        Assert.Equal("ECM", vm.DtcInsEcu);                                  // unica componente del test: preselezionata
        var disp = Assert.Single(vm.DtcInsDisponibili);                     // P0002 e di BCM
        Assert.Equal(("P0001", "D1"), (disp.Codice, disp.RequisitoId));
        Assert.True(vm.DtcInsMostraCombo);
        vm.DtcInsScelto = disp;
        Assert.Equal("D1", vm.DtcInsRequisitoSel);
        Assert.False(vm.DtcInsRequisitoEditabile);                          // da elenco: requisito di provenienza

        vm.AggiungiDtcCommand.Execute(null);

        Assert.Null(vm.DtcInsErrore);
        Assert.Equal(new[] { PassoDtc.Validazione, PassoDtc.Devalidazione }, Ops(vm).Select(o => o.DtcPasso!.Value).ToArray());
        Assert.All(Ops(vm), o =>
        {
            Assert.Equal("Guasto Injection", o.Strumento);
            Assert.Equal(("P0001", "D1", Esito.DaEseguire), (o.Guasto, o.DtcRequisitoId, o.Esito));
            Assert.Equal(new[] { "ECM" }, o.Componente.ToArray());
            Assert.Equal("P0001", o.ValoriStrumenti[DtcOperazioneRegole.ChiaveAtteso]);   // precompilazione, non secondo dato
        });
        Assert.NotEqual(Ops(vm)[0].Uid, Ops(vm)[1].Uid);
        var riga = Assert.Single(vm.DtcInseriti);
        Assert.Equal(2, riga.Operazioni.Count);
        Assert.Empty(vm.DtcInsDisponibili);                                 // gia aggiunto: non piu proponibile
        Assert.Null(vm.DtcInsScelto);
        Assert.True(vm.ModificheNonSalvate);
        Assert.True(Ops(vm).All(o => o.DtcDaStrumento) && Ops(vm).All(o => !o.DtcLegacy));
        Assert.Equal(DtcOperazioneRegole.ChiaveDtc("D1", "V", "ECM", "P0001"), riga.Chiave);
    }

    [Fact]
    public void Manuale_RequisitoVuoto_PoiAssegnabileSoloAUnD4dCollegato_SenzaToccareLAnagraficaDelRequisito()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")), Extra("D9", ("P0009", "ECM")));
        var vm = Apri(state, Test(new[] { "D1" }, "ECM"));
        vm.DtcInsCodiceManuale = " P0999 ";
        Assert.Equal("(nessuno)", vm.DtcInsRequisitoSel);
        Assert.True(vm.DtcInsRequisitoEditabile);
        vm.AggiungiDtcCommand.Execute(null);
        Assert.Null(vm.DtcInsErrore);
        Assert.Equal("", vm.DtcInsCodiceManuale);
        Assert.All(Ops(vm), o => { Assert.Equal("P0999", o.Guasto); Assert.Null(o.DtcRequisitoId); });
        var riga = Assert.Single(vm.DtcInseriti);
        Assert.True(riga.Libero);

        Assert.Equal("DtcRequisitoNonCollegato", vm.ImpostaRequisitoDtcInserito(riga, "D9"));   // D9 non e collegato al test
        Assert.Null(vm.ImpostaRequisitoDtcInserito(riga, "D1"));
        Assert.All(Ops(vm), o => Assert.Equal("D1", o.DtcRequisitoId));
        Assert.Equal("D1", Assert.Single(vm.DtcInseriti).RequisitoId);
        Assert.Null(vm.ImpostaRequisitoDtcInserito(vm.DtcInseriti[0], vm.DtcInsRequisitiOpzioni[0]));  // "(nessuno)" lo toglie
        Assert.All(Ops(vm), o => Assert.Null(o.DtcRequisitoId));

        var req = state.Master.Requisiti.Load().Single(r => r.Id == "D1");
        Assert.DoesNotContain(state.Master.RequisitiDtc.Leggi(req).Voci, v => v.Codice == "P0999");   // anagrafica guasto del requisito intatta
    }

    [Fact]
    public void Manuale_DaRequisitoAssegnato_NelloStessoGesto()
    {
        var vm = Apri(CreaStato(Extra("D1", ("P0001", "ECM"))), Test(new[] { "D1" }, "ECM"));
        vm.DtcInsCodiceManuale = "P0999";
        vm.DtcInsRequisitoSel = "D1";
        vm.AggiungiDtcCommand.Execute(null);
        Assert.All(Ops(vm), o => Assert.Equal("D1", o.DtcRequisitoId));
    }

    [Fact]
    public void Errori_NullaVieneCreato()
    {
        var vm = Apri(CreaStato(Extra("D1", ("P0001", "ECM"))), Test(new[] { "D1" }, "ECM"));
        Assert.Equal("DtcCodiceNonValido", vm.AggiungiDtcInjection("ECM", "P 01;", null));
        Assert.Equal("DtcEcuMancante", vm.AggiungiDtcInjection("", "P0100", null));
        Assert.Equal("DtcEcuNonInAnagrafica", vm.AggiungiDtcInjection("BCM", "P0100", null));
        Assert.Equal("DtcRequisitoNonCollegato", vm.AggiungiDtcInjection("ECM", "P0100", "D7"));
        Assert.Empty(Ops(vm));
        Assert.Null(vm.AggiungiDtcInjection("ecm", "p0100", null));        // componente risolta sul nome canonico
        Assert.Equal("ECM", Ops(vm)[0].Componente.Single());
        Assert.Equal("DtcGiaPresente", vm.AggiungiDtcInjection("ECM", "P0100", null));
        Assert.Equal(2, Ops(vm).Count);
        vm.AggiungiDtcCommand.Execute(null);                                // nessun input: codice non valido, errore mostrato
        Assert.True(vm.HaDtcInsErrore);
        Assert.Equal(2, Ops(vm).Count);
    }

    [Fact]
    public void ConPiuEcu_PreselezionaLaPrimaSenzaDtc_ECambioEcuECodiceAdeguaLeDueOperazioni()
    {
        var vm = Apri(CreaStato(Extra("D1", ("P0001", "ECM"), ("P0002", "BCM"))), Test(new[] { "D1" }, "ECM", "BCM"));
        Assert.Equal("ECM", vm.DtcInsEcu);                                  // 6.5.2: prima coppia Veicolo/componente ancora senza guasto
        Assert.Equal("P0001", Assert.Single(vm.DtcInsDisponibili).Codice);
        vm.DtcInsEcu = "BCM";
        Assert.Equal("P0002", Assert.Single(vm.DtcInsDisponibili).Codice);
        Assert.Null(vm.AggiungiDtcInjection("BCM", "P0002", "D1"));
        var riga = vm.DtcInseriti.Single();

        Assert.Null(vm.CambiaEcuDtcInserito(riga, "ECM"));
        Assert.All(Ops(vm), o => Assert.Equal(new[] { "ECM" }, o.Componente.ToArray()));
        riga = vm.DtcInseriti.Single();
        Assert.Equal("ECM", riga.Componente);

        Assert.Equal("DtcCodiceNonValido", vm.CambiaCodiceDtcInserito(riga, "x y"));
        Assert.Null(vm.CambiaCodiceDtcInserito(riga, "P0777"));
        Assert.All(Ops(vm), o => { Assert.Equal("P0777", o.Guasto); Assert.Equal("P0777", o.ValoriStrumenti[DtcOperazioneRegole.ChiaveAtteso]); });
        Assert.Null(vm.CambiaEcuDtcInserito(vm.DtcInseriti.Single(), "BCM"));
        Assert.Equal("DtcEcuNonInAnagrafica", vm.CambiaEcuDtcInserito(vm.DtcInseriti.Single(), "XYZ"));

        // un secondo guasto uguale al primo dopo un cambio: duplicato rifiutato
        Assert.Null(vm.AggiungiDtcInjection("BCM", "P0100", "D1"));
        var seconda = vm.DtcInseriti.Single(r => r.Codice == "P0100");
        Assert.Equal("DtcGiaPresente", vm.CambiaCodiceDtcInserito(seconda, "P0777"));
        Assert.Equal(4, Ops(vm).Count);
    }

    [Fact]
    public void Rimozione_EliminaLeDueOperazioni_MaEBloccataSeUnaHaEsito_ComeInFase3()
    {
        var vm = Apri(CreaStato(Extra("D1", ("P0001", "ECM"))), Test(new[] { "D1" }, "ECM"));
        Assert.Null(vm.AggiungiDtcInjection("ECM", "P0001", "D1"));
        Assert.Null(vm.AggiungiDtcInjection("ECM", "P0555", null));
        Assert.Equal(4, Ops(vm).Count);

        var eseguita = vm.DtcInseriti.Single(r => r.Codice == "P0001");
        eseguita.Operazioni[0].Esito = Esito.Conforme;                      // un esito registrato
        Assert.Equal("DtcInsBloccatoEsiti", vm.RimuoviDtcInserito(eseguita));
        Assert.Equal("DtcInsBloccatoEsiti", vm.CambiaEcuDtcInserito(eseguita, "ECM"));
        Assert.Equal("DtcInsBloccatoEsiti", vm.CambiaCodiceDtcInserito(eseguita, "P0002"));
        Assert.Equal("DtcInsBloccatoEsiti", vm.ImpostaRequisitoDtcInserito(eseguita, null));
        Assert.True(vm.HaDtcInsErrore);
        Assert.Equal(4, Ops(vm).Count);
        Assert.Equal("D1", eseguita.Operazioni[0].DtcRequisitoId);

        var libera = vm.DtcInseriti.Single(r => r.Codice == "P0555");
        Assert.Null(vm.RimuoviDtcInserito(libera));
        Assert.Equal(2, Ops(vm).Count);
        Assert.All(Ops(vm), o => Assert.Equal("P0001", o.Guasto));
        var nuovo = Assert.Single(vm.DtcInseriti);
        Assert.Contains(nuovo.Operazioni, o => o.Esito == Esito.Conforme);
    }

    [Fact]
    public void DtcLiberi_SalvatiNelTest_MaFuoriDaAudit()
    {
        var d1 = Extra("D1", ("P0001", "ECM"));
        var vm = Apri(CreaStato(d1), Test(new[] { "D1" }, "ECM"));
        Assert.Null(vm.AggiungiDtcInjection("ECM", "P0999", null));
        Assert.All(Ops(vm), o => o.Esito = Esito.Conforme);
        var r = DtcAvanzamentoService.Calcola(new[] { d1 }, new[] { vm.TestCase });
        var cella = Assert.Single(r.PerDtc);
        Assert.Equal("P0001", cella.Guasto);
        Assert.Equal(StatoDtc.DaFare, cella.Stato);                          // il guasto libero non completa nulla del requisito
        Assert.Equal(1, r.CelleSenzaAnagrafica);                             // contato solo come "non in anagrafica", mai un errore
    }

    [Fact]
    public async Task Persistenza_RoundTrip_SalvataggioERiaperturaRicostruisconoLeRighe()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        var vm = Apri(state, Test(new[] { "D1" }, "ECM"));
        vm.IniziaModificaCommand.AttendiModifica();
        typeof(TestCaseEditorViewModel).GetField("_sopprimiRitornoAllaLista", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(vm, true);
        Assert.True(vm.MostraBloccoAggiungiDtc);
        Assert.Null(vm.AggiungiDtcInjection("ECM", "P0001", "D1"));
        Assert.Null(vm.AggiungiDtcInjection("ECM", "P0999", null));
        await vm.SalvaSicuroCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErroriValidazione);
        vm.Dispose();

        var riletto = state.TestCases.GetById("T-1")!.Esecuzioni[^1];
        Assert.Equal(4, riletto.Condizioni.Count);
        Assert.All(riletto.Condizioni, c => Assert.Equal("Guasto Injection", c.Strumento));
        Assert.Equal(new string?[] { "D1", "D1", null, null }, riletto.Condizioni.Select(c => c.DtcRequisitoId).ToArray());
        Assert.Equal(new[] { PassoDtc.Validazione, PassoDtc.Devalidazione, PassoDtc.Validazione, PassoDtc.Devalidazione }, riletto.Condizioni.Select(c => c.DtcPasso!.Value).ToArray());

        var vm2 = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        Assert.Equal(new[] { "P0001", "P0999" }, vm2.DtcInseriti.Select(r => r.Codice).ToArray());
        Assert.Equal(new[] { "D1", "" }, vm2.DtcInseriti.Select(r => r.RequisitoId).ToArray());
        Assert.All(vm2.DtcInseriti, r => Assert.Equal(2, r.Operazioni.Count));
        vm2.Dispose();
    }

    private static void Notifica(TestCaseEditorViewModel vm) =>
        typeof(TestCaseEditorViewModel).GetMethod("NotificaDtcOpzioni", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, null);

    private static void CreaBozzaCrash(AppState state, string matricola)
    {
        var baseSalvata = state.TestCases.GetById("T-1")!;
        var bozzaCrash = state.TestCases.CopiaPerModifica(baseSalvata);
        bozzaCrash.Esecuzioni[^1].Matricola = matricola;
        state.BozzaAutosave.SalvaModifica(state.Config.OperatoreAttivo, baseSalvata, bozzaCrash);
    }

    private sealed class EditorAcquisizioneRitardata : TestCaseEditorViewModel
    {
        private readonly AppState _state;
        public readonly TaskCompletionSource<LockResult> LockAcquisito =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> ConsentiRitornoAcquisizione =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EditorAcquisizioneRitardata(AppState state, string id)
            : base(state, id, () => { }, _ => { }) => _state = state;

        internal override Task<LockResult> AcquisisciLockAsync(string percorso, string operatore) =>
            Task.Run(async () =>
            {
                var risultato = _state.Locks.TryAcquire(percorso, operatore);
                LockAcquisito.TrySetResult(risultato);
                await ConsentiRitornoAcquisizione.Task;
                return risultato;
            });
    }

    [Fact]
    public async Task Nessuno_ScelaDallUtente_NonViene_RiabbinatoDaSolo_NemmenoDopoSalvataggioERiapertura()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")), Extra("D2", ("P0002", "ECM")));
        var vm = Apri(state, Test(new[] { "D1" }, "ECM"));
        vm.IniziaModificaCommand.AttendiModifica();
        typeof(TestCaseEditorViewModel).GetField("_sopprimiRitornoAllaLista", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(vm, true);
        Assert.Null(vm.AggiungiDtcInjection("ECM", "P0001", null));             // il guasto esiste in D1 ma l'utente lascia "(nessuno)"
        Assert.Null(vm.AggiungiDtcInjection("ECM", "P0002", "D1"));
        Assert.Null(vm.ImpostaRequisitoDtcInserito(vm.DtcInseriti.Single(r => r.Codice == "P0002"), vm.DtcInsRequisitiOpzioni[0]));   // scelta (nessuno)

        vm.TestCase.RequisitoId.Add("D2");                                       // cambio requisiti
        Notifica(vm);
        vm.TestCase.Componente = new EcuSelection(new[] { "ECM", "BCM" });              // cambio componente
        Notifica(vm);
        vm.TestCase.Componente = new EcuSelection(new[] { "ECM" });
        Notifica(vm);
        Assert.All(Ops(vm), o => Assert.Null(o.DtcRequisitoId));

        await vm.SalvaSicuroCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErroriValidazione);
        vm.Dispose();
        var vm2 = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        Notifica(vm2);
        Assert.All(Ops(vm2), o => Assert.Null(o.DtcRequisitoId));
        Assert.All(vm2.DtcInseriti, r => Assert.Equal("", r.RequisitoId));
        vm2.Dispose();
    }

    [Fact]
    public async Task RecuperoBozzaModifica_ComandoPrimaDelCallbackRecuperaUnaVoltaESalvaInSicurezza()
    {
        var handlerPrecedente = ConfirmService.Handler;
        var richiesteConferma = 0;
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        var test = Test(new[] { "D1" }, "ECM");
        state.TestCases.Save(test, true);
        CreaBozzaCrash(state, "Matricola dalla sessione interrotta");

        // Tiene in coda la callback Background: il comando utente è il primo chiamante del recupero.
        using var dispatcherOccupato = new ManualResetEventSlim();
        using var liberaDispatcher = new ManualResetEventSlim();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            dispatcherOccupato.Set();
            liberaDispatcher.Wait(TimeSpan.FromSeconds(10));
        }, Avalonia.Threading.DispatcherPriority.Send);
        Assert.True(dispatcherOccupato.Wait(TimeSpan.FromSeconds(5)), "The dispatcher barrier did not start.");

        TestCaseEditorViewModel? vm = null;
        try
        {
            ConfirmService.Handler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref richiesteConferma);
                return Task.FromResult(true);
            };
            vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
            typeof(TestCaseEditorViewModel).GetField("_sopprimiRitornoAllaLista", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(vm, true);

            await Task.Run(async () => await vm.IniziaModificaCommand.ExecuteAsync(null)).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("Matricola dalla sessione interrotta", vm.TestCase.Esecuzioni[^1].Matricola);
            Assert.True(vm.InModifica);
            Assert.True(vm.ModificheNonSalvate);
            Assert.Equal(1, Volatile.Read(ref richiesteConferma));

            liberaDispatcher.Set();
            var callbackDrenato = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => callbackDrenato.TrySetResult(true), Avalonia.Threading.DispatcherPriority.Background);
            await callbackDrenato.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref richiesteConferma)); // la callback ritardata riusa il task del comando

            Assert.Null(vm.AggiungiDtcInjection("ECM", "P0001", "D1"));
            await Task.Run(async () => await vm.SalvaSicuroCommand.ExecuteAsync(null)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(vm.ErroriValidazione);
            Assert.Null(state.BozzaAutosave.CaricaModifica(state.Config.OperatoreAttivo, vm.TestCase.Uid));
            Assert.Equal("Matricola dalla sessione interrotta", state.TestCases.GetById("T-1")!.Esecuzioni[^1].Matricola);
            Assert.False(vm.InModifica);

            await Task.Run(async () => await vm.IniziaModificaCommand.ExecuteAsync(null)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(vm.InModifica); // il risultato Ripristinata già completato non deve bloccare una modifica successiva
        }
        finally
        {
            liberaDispatcher.Set();
            vm?.Dispose();
            ConfirmService.Handler = handlerPrecedente;
        }
    }

    [Fact]
    public async Task RecuperoBozzaModifica_CallbackDopoEccezionePulisceIlTaskERendePossibileUnNuovoTentativo()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        using var dispatcherOccupato = new ManualResetEventSlim();
        using var liberaDispatcher = new ManualResetEventSlim();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            dispatcherOccupato.Set();
            liberaDispatcher.Wait(TimeSpan.FromSeconds(10));
        }, Avalonia.Threading.DispatcherPriority.Send);
        Assert.True(dispatcherOccupato.Wait(TimeSpan.FromSeconds(5)), "The dispatcher barrier did not start.");

        TestCaseEditorViewModel? vm = null;
        try
        {
            vm = Apri(state, Test(new[] { "D1" }, "ECM"));
            var taskField = typeof(TestCaseEditorViewModel).GetField("_existingDraftRecoveryTask",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var outcomeType = taskField.FieldType.GetGenericArguments()[0];
            var fromException = typeof(Task).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Single(m => m.Name == nameof(Task.FromException) && m.IsGenericMethodDefinition &&
                    m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Exception));
            var failedTask = (Task)fromException.MakeGenericMethod(outcomeType)
                .Invoke(null, new object[] { new InvalidOperationException("simulated recovery failure") })!;
            taskField.SetValue(vm, failedTask);

            var eseguiCallback = typeof(TestCaseEditorViewModel).GetMethod("EseguiRecuperoBozzaModificaSchedulatoAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await ((Task)eseguiCallback.Invoke(vm, null)!).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(taskField.GetValue(vm));
            var assicuraRecupero = typeof(TestCaseEditorViewModel).GetMethod("AssicuraRecuperoBozzaModificaAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var retryTask = (Task)assicuraRecupero.Invoke(vm, null)!;
            Assert.NotSame(failedTask, retryTask);
            await retryTask.WaitAsync(TimeSpan.FromSeconds(5));

            liberaDispatcher.Set();
            var callbackDrenato = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => callbackDrenato.TrySetResult(true), Avalonia.Threading.DispatcherPriority.Background);
            await callbackDrenato.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(retryTask, taskField.GetValue(vm)); // la callback originale usa il tentativo già condiviso
        }
        finally
        {
            liberaDispatcher.Set();
            vm?.Dispose();
        }
    }

    [Fact]
    public async Task RecuperoBozzaMergeAnnullatoRilasciaIlLockESalvaLaBozza()
    {
        var handlerConfermaPrecedente = ConfirmService.Handler;
        var handlerMergePrecedente = MergeConflittoDialogService.Handler;
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        state.TestCases.Save(Test(new[] { "D1" }, "ECM"), true);
        CreaBozzaCrash(state, "Matricola della bozza crash");
        var corrente = state.TestCases.GetById("T-1")!;
        corrente.Esecuzioni[^1].Matricola = "Matricola aggiornato da un'altra sessione";
        state.TestCases.Save(corrente, isNew: false, revisioneAttesa: corrente.RevisioneDati);

        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        MergeConflittoDialogService.Handler = _ => Task.FromResult<IReadOnlyDictionary<string, SceltaMerge>?>(null);
        TestCaseEditorViewModel? vm = null;
        try
        {
            vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
            await Task.Run(async () => await vm.IniziaModificaCommand.ExecuteAsync(null)).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Contains(vm.ErroriValidazione, e => e.Contains("Merge di recupero annullato", StringComparison.Ordinal));
            Assert.False(vm.InModifica);
            Assert.Equal("Matricola della bozza crash",
                state.BozzaAutosave.CaricaModifica(state.Config.OperatoreAttivo, vm.TestCase.Uid)!.TestCase.Esecuzioni[^1].Matricola);
            Assert.Null(state.Locks.GetActiveLock(state.TestCases.PercorsoPer(vm.TestCase)));
        }
        finally
        {
            vm?.Dispose();
            ConfirmService.Handler = handlerConfermaPrecedente;
            MergeConflittoDialogService.Handler = handlerMergePrecedente;
        }
    }

    [Fact]
    public async Task RecuperoBozzaModifica_CallbackDopoDisposeNonChiedeConfermaENonScartaLaBozza()
    {
        var richiesteConferma = 0;
        var handlerPrecedente = ConfirmService.Handler;
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        state.TestCases.Save(Test(new[] { "D1" }, "ECM"), true);
        CreaBozzaCrash(state, "Matricola conservato dopo Dispose");

        using var dispatcherOccupato = new ManualResetEventSlim();
        using var liberaDispatcher = new ManualResetEventSlim();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            dispatcherOccupato.Set();
            liberaDispatcher.Wait(TimeSpan.FromSeconds(10));
        }, Avalonia.Threading.DispatcherPriority.Send);
        Assert.True(dispatcherOccupato.Wait(TimeSpan.FromSeconds(5)), "The dispatcher barrier did not start.");

        TestCaseEditorViewModel? vm = null;
        try
        {
            ConfirmService.Handler = (_, _, _, _) =>
            {
                Interlocked.Increment(ref richiesteConferma);
                return Task.FromResult(true);
            };
            vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
            vm.Dispose();
            liberaDispatcher.Set();

            var callbackDrenato = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => callbackDrenato.TrySetResult(true), Avalonia.Threading.DispatcherPriority.Background);
            await callbackDrenato.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, Volatile.Read(ref richiesteConferma));
            Assert.Equal("Matricola conservato dopo Dispose",
                state.BozzaAutosave.CaricaModifica(state.Config.OperatoreAttivo, vm.TestCase.Uid)!.TestCase.Esecuzioni[^1].Matricola);
        }
        finally
        {
            liberaDispatcher.Set();
            vm?.Dispose();
            ConfirmService.Handler = handlerPrecedente;
        }
    }

    [Fact]
    public async Task RecuperoBozzaModifica_DisposeDuranteConfermaConservaLaBozzaENonAcquisisceLock()
    {
        var handlerPrecedente = ConfirmService.Handler;
        var confermaAvviata = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completaConferma = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        state.TestCases.Save(Test(new[] { "D1" }, "ECM"), true);
        CreaBozzaCrash(state, "Matricola conservato dopo conferma tardiva");
        ConfirmService.Handler = (_, _, _, _) =>
        {
            confermaAvviata.TrySetResult(true);
            return completaConferma.Task;
        };

        TestCaseEditorViewModel? vm = null;
        try
        {
            vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
            var comando = Task.Run(async () => await vm.IniziaModificaCommand.ExecuteAsync(null));
            await confermaAvviata.Task.WaitAsync(TimeSpan.FromSeconds(5));

            vm.Dispose();
            completaConferma.TrySetResult(false);
            await comando.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("Matricola conservato dopo conferma tardiva",
                state.BozzaAutosave.CaricaModifica(state.Config.OperatoreAttivo, vm.TestCase.Uid)!.TestCase.Esecuzioni[^1].Matricola);
            Assert.Null(state.Locks.GetActiveLock(state.TestCases.PercorsoPer(vm.TestCase)));
            Assert.False(vm.InModifica);
        }
        finally
        {
            completaConferma.TrySetResult(false);
            vm?.Dispose();
            ConfirmService.Handler = handlerPrecedente;
        }
    }

    [Fact]
    public async Task RecuperoBozzaModifica_DisposeDuranteMergeBloccaUnaNuovaAcquisizioneFinoAlRilascioEsatto()
    {
        var handlerConfermaPrecedente = ConfirmService.Handler;
        var handlerMergePrecedente = MergeConflittoDialogService.Handler;
        var mergeAvviato = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completaMerge = new TaskCompletionSource<IReadOnlyDictionary<string, SceltaMerge>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryInAttesa = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaRetry = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        state.TestCases.Save(Test(new[] { "D1" }, "ECM"), true);
        CreaBozzaCrash(state, "Matricola della bozza locale");
        var corrente = state.TestCases.GetById("T-1")!;
        corrente.Esecuzioni[^1].Matricola = "Matricola aggiornato da un'altra sessione";
        state.TestCases.Save(corrente, isNew: false, revisioneAttesa: corrente.RevisioneDati);
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        MergeConflittoDialogService.Handler = _ =>
        {
            mergeAvviato.TrySetResult(true);
            return completaMerge.Task;
        };

        TestCaseEditorViewModel? vm = null;
        Task? comando = null;
        Task<EsitoIo>? rilascio = null;
        Task<LockResult>? acquisizioneSuccessiva = null;
        LockResult? lockSuccessivo = null;
        try
        {
            vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
            comando = Task.Run(async () => await vm.IniziaModificaCommand.ExecuteAsync(null));
            await mergeAvviato.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var percorso = state.TestCases.PercorsoPer(vm.TestCase);
            Assert.False(vm.InModifica); // il lock è pronto, ma la bozza va ancora applicata
            var sessione = Assert.IsType<string>(state.Locks.GetActiveLock(percorso)?.SessionId);

            // Simula un rilascio che deve ritentare. Il coordinatore mantiene il gate anche mentre attende.
            var tentativiRilascio = 0;
            rilascio = LockGate.RilasciaSessioneEsattaConRetryAsync(
                state.Locks,
                percorso,
                sessione,
                (locks, path, exactSession) => Interlocked.Increment(ref tentativiRilascio) == 1
                    ? EsitoIo.Errore
                    : locks.ReleaseIfSession(path, exactSession),
                async _ =>
                {
                    retryInAttesa.TrySetResult(true);
                    await continuaRetry.Task;
                });
            await retryInAttesa.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(sessione, state.Locks.GetActiveLock(percorso)?.SessionId);

            vm.Dispose();
            completaMerge.TrySetResult(new Dictionary<string, SceltaMerge>());
            // Stesso servizio e operatore: la nuova acquisizione deve attendere il cleanup exact-session.
            acquisizioneSuccessiva = LockGate.EseguiAsync(percorso,
                () => Task.Run(() => state.Locks.TryAcquire(percorso, state.Config.OperatoreAttivo)));
            Assert.False(acquisizioneSuccessiva.IsCompleted);

            continuaRetry.TrySetResult(true);
            Assert.Equal(EsitoIo.Ok, await rilascio.WaitAsync(TimeSpan.FromSeconds(5)));
            lockSuccessivo = await acquisizioneSuccessiva.WaitAsync(TimeSpan.FromSeconds(5));
            await comando.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("Matricola della bozza locale",
                state.BozzaAutosave.CaricaModifica(state.Config.OperatoreAttivo, vm.TestCase.Uid)!.TestCase.Esecuzioni[^1].Matricola);
            Assert.True(lockSuccessivo.Acquisito);
            Assert.NotEqual(sessione, lockSuccessivo.DetenutoDa?.SessionId);
            Assert.Equal(lockSuccessivo.DetenutoDa?.SessionId, state.Locks.GetActiveLock(percorso)?.SessionId);
            Assert.False(vm.InModifica);
        }
        finally
        {
            continuaRetry.TrySetResult(true);
            completaMerge.TrySetResult(new Dictionary<string, SceltaMerge>());
            if (rilascio != null) try { await rilascio.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (acquisizioneSuccessiva != null)
            {
                try { lockSuccessivo ??= await acquisizioneSuccessiva.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
            if (comando != null) try { await comando.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            if (lockSuccessivo?.DetenutoDa?.SessionId is { } sessioneNuova)
                state.Locks.ReleaseIfSession(state.TestCases.PercorsoPer(state.TestCases.GetById("T-1")!), sessioneNuova);
            vm?.Dispose();
            ConfirmService.Handler = handlerConfermaPrecedente;
            MergeConflittoDialogService.Handler = handlerMergePrecedente;
        }
    }

    [Fact]
    public async Task DisposeDuranteAcquisizioneRiusaIlCoordinatoreInCodaSenzaDeadlockTraDueEditor()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        state.TestCases.Save(Test(new[] { "D1" }, "ECM"), true);
        var editorCheChiude = new EditorAcquisizioneRitardata(state, "T-1");
        var comandoChiusura = Task.Run(async () => await editorCheChiude.IniziaModificaCommand.ExecuteAsync(null));
        EditorAcquisizioneRitardata? editorNuovo = null;
        Task? comandoNuovo = null;
        Task<EsitoIo>? coordinatoreGiaAccodato = null;
        string? path = null;
        string? sessionePrecedente = null;
        string? sessioneNuova = null;
        var rilasciDelCoordinatoreAccodato = 0;

        try
        {
            var lockPrecedente = await editorCheChiude.LockAcquisito.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(lockPrecedente.Acquisito);
            path = state.TestCases.PercorsoPer(editorCheChiude.TestCase);
            sessionePrecedente = Assert.IsType<string>(lockPrecedente.DetenutoDa?.SessionId);

            // È chiamato mentre l'editor detiene il gate durante AcquisisciLockAsync: il coordinator viene
            // registrato per la stessa lease, ma rimane in attesa del gate posseduto dall'editor.
            coordinatoreGiaAccodato = LockGate.RilasciaSessioneEsattaConRetryAsync(
                state.Locks,
                path,
                sessionePrecedente,
                (_, _, _) =>
                {
                    Interlocked.Increment(ref rilasciDelCoordinatoreAccodato);
                    return EsitoIo.Errore;
                },
                _ => Task.CompletedTask);
            Assert.False(coordinatoreGiaAccodato.IsCompleted);

            editorCheChiude.Dispose();
            editorNuovo = new EditorAcquisizioneRitardata(state, "T-1");
            comandoNuovo = Task.Run(async () => await editorNuovo.IniziaModificaCommand.ExecuteAsync(null));

            // La vecchia acquisition completa sotto gate e tenta il cleanup. Deve eseguire il rilascio
            // bounded direttamente sotto gate e completare il task già registrato, senza attenderlo.
            editorCheChiude.ConsentiRitornoAcquisizione.TrySetResult(true);
            await comandoChiusura.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(EsitoIo.Ok, await coordinatoreGiaAccodato.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, Volatile.Read(ref rilasciDelCoordinatoreAccodato));

            // L'editor nuovo può aver letto la vecchia lease prima che il coordinator la rilasciasse e aver
            // concluso il primo tentativo senza arrivare ad AcquisisciLockAsync. In alternativa può acquisire
            // subito la nuova lease dopo il rilascio. Entrambi gli esiti sono validi: ciò che non deve accadere
            // è cancellare o riusare la sessione precedente.
            Assert.NotEqual(sessionePrecedente, state.Locks.GetActiveLock(path)?.SessionId);
            var primoEsitoNuovo = await Task.WhenAny(editorNuovo.LockAcquisito.Task, comandoNuovo)
                .WaitAsync(TimeSpan.FromSeconds(10));
            if (ReferenceEquals(primoEsitoNuovo, comandoNuovo) && !editorNuovo.LockAcquisito.Task.IsCompleted)
            {
                await comandoNuovo.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(editorNuovo.InModifica);
                Assert.NotEqual(sessionePrecedente, state.Locks.GetActiveLock(path)?.SessionId);

                // Il primo tentativo può aver osservato ancora la vecchia lease prima di entrare nel gate.
                // Dopo il completamento del coordinator, un retry deve raggiungere l'acquisizione reale.
                comandoNuovo = Task.Run(async () => await editorNuovo.IniziaModificaCommand.ExecuteAsync(null));
            }

            var lockNuovo = await editorNuovo.LockAcquisito.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(lockNuovo.Acquisito);
            sessioneNuova = Assert.IsType<string>(lockNuovo.DetenutoDa?.SessionId);
            Assert.NotEqual(sessionePrecedente, sessioneNuova);
            editorNuovo.ConsentiRitornoAcquisizione.TrySetResult(true);
            await comandoNuovo.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(sessioneNuova, state.Locks.GetActiveLock(path)?.SessionId);
            Assert.True(editorNuovo.InModifica);
        }
        finally
        {
            editorCheChiude.ConsentiRitornoAcquisizione.TrySetResult(true);
            editorNuovo?.ConsentiRitornoAcquisizione.TrySetResult(true);
            editorCheChiude.Dispose();
            editorNuovo?.Dispose();
            try { await comandoChiusura.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            if (comandoNuovo != null) try { await comandoNuovo.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            if (coordinatoreGiaAccodato != null)
            {
                try { await coordinatoreGiaAccodato.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            }
            if (path != null && sessioneNuova != null)
            {
                try
                {
                    await LockGate.RilasciaSessioneEsattaConRetryAsync(state.Locks, path, sessioneNuova)
                        .WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch { }
            }
        }
    }

    [Fact]
    public async Task DisposeDuranteAcquisizioneRilasciaSottoIlGatePrimaDellaNuovaAcquisizione()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        state.TestCases.Save(Test(new[] { "D1" }, "ECM"), true);
        var vm = new EditorAcquisizioneRitardata(state, "T-1");
        var comando = Task.Run(async () => await vm.IniziaModificaCommand.ExecuteAsync(null));
        Task<LockResult>? acquisizioneSuccessiva = null;
        LockResult? lockSuccessivo = null;
        string? path = null;
        try
        {
            var acquisizione = await vm.LockAcquisito.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(acquisizione.Acquisito);
            path = state.TestCases.PercorsoPer(vm.TestCase);
            var sessione = Assert.IsType<string>(acquisizione.DetenutoDa?.SessionId);
            Assert.Equal(sessione, state.Locks.GetActiveLock(path)?.SessionId);

            // La chiamata AcquisisciLockAsync ha creato il lock, ma non ha ancora consegnato il risultato
            // al core. Dispose cade quindi tra l'acquisizione effettiva e il suo continuamento.
            vm.Dispose();
            acquisizioneSuccessiva = LockGate.EseguiAsync(path,
                () => Task.Run(() => state.Locks.TryAcquire(path, state.Config.OperatoreAttivo)));
            Assert.False(acquisizioneSuccessiva.IsCompleted);

            vm.ConsentiRitornoAcquisizione.TrySetResult(true);
            await comando.WaitAsync(TimeSpan.FromSeconds(10));
            lockSuccessivo = await acquisizioneSuccessiva.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(lockSuccessivo.Acquisito);
            Assert.NotEqual(sessione, lockSuccessivo.DetenutoDa?.SessionId);
            Assert.Equal(lockSuccessivo.DetenutoDa?.SessionId, state.Locks.GetActiveLock(path)?.SessionId);
            Assert.False(vm.InModifica);
        }
        finally
        {
            vm.ConsentiRitornoAcquisizione.TrySetResult(true);
            vm.Dispose();
            try { await comando.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            if (acquisizioneSuccessiva != null)
            {
                try { lockSuccessivo ??= await acquisizioneSuccessiva.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            }
            if (path is not null && lockSuccessivo?.DetenutoDa?.SessionId is { } sessioneNuova)
                state.Locks.ReleaseIfSession(path, sessioneNuova);
        }
    }

    [Fact]
    public async Task DisposeDuranteAcquisizioneConRetryEsauritiNonRilasciaLaSessioneDellaNuovaView()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        state.TestCases.Save(Test(new[] { "D1" }, "ECM"), true);
        var tentativiRilascio = 0;
        var vm = new EditorAcquisizioneRitardata(state, "T-1")
        {
            RilascioSessioneLockPerTest = (_, _, _) =>
            {
                Interlocked.Increment(ref tentativiRilascio);
                return EsitoIo.Errore;
            },
            AttesaRetryLockPerTest = _ => Task.CompletedTask
        };
        var comando = Task.Run(async () => await vm.IniziaModificaCommand.ExecuteAsync(null));
        Task<LockResult>? acquisizioneSuccessiva = null;
        LockResult? lockSuccessivo = null;
        string? path = null;
        string? sessione = null;
        try
        {
            var acquisizione = await vm.LockAcquisito.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(acquisizione.Acquisito);
            path = state.TestCases.PercorsoPer(vm.TestCase);
            sessione = Assert.IsType<string>(acquisizione.DetenutoDa?.SessionId);

            vm.Dispose();
            acquisizioneSuccessiva = LockGate.EseguiAsync(path,
                () => Task.Run(() => state.Locks.TryAcquire(path, state.Config.OperatoreAttivo)));
            Assert.False(acquisizioneSuccessiva.IsCompleted);

            vm.ConsentiRitornoAcquisizione.TrySetResult(true);
            await comando.WaitAsync(TimeSpan.FromSeconds(10));
            lockSuccessivo = await acquisizioneSuccessiva.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(6, Volatile.Read(ref tentativiRilascio));
            Assert.True(lockSuccessivo.Acquisito); // il LockService continua la stessa sessione owned
            Assert.Equal(sessione, lockSuccessivo.DetenutoDa?.SessionId);
            Assert.Equal(sessione, state.Locks.GetActiveLock(path)?.SessionId);
        }
        finally
        {
            vm.ConsentiRitornoAcquisizione.TrySetResult(true);
            vm.Dispose();
            try { await comando.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            if (acquisizioneSuccessiva != null)
            {
                try { lockSuccessivo ??= await acquisizioneSuccessiva.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            }
            if (path != null && sessione != null)
                state.Locks.ReleaseIfSession(path, sessione);
        }
    }

    [Fact]
    public async Task LockGate_RetryEsattoDeduplicatoDopoErroriMultipli()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        state.TestCases.Save(Test(new[] { "D1" }, "ECM"), true);
        var path = state.TestCases.PercorsoPer(state.TestCases.GetById("T-1")!);
        var acquisizione = state.Locks.TryAcquire(path, state.Config.OperatoreAttivo);
        Assert.True(acquisizione.Acquisito);
        var sessione = Assert.IsType<string>(acquisizione.DetenutoDa?.SessionId);
        var tentativi = 0;
        var rilascioDuplicato = 0;
        var attesaAvviata = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaRetry = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = LockGate.RilasciaSessioneEsattaConRetryAsync(
            state.Locks,
            path,
            sessione,
            (locks, targetPath, targetSession) =>
            {
                var numero = Interlocked.Increment(ref tentativi);
                return numero <= 3
                    ? EsitoIo.Errore
                    : locks.ReleaseIfSession(targetPath, targetSession);
            },
            async _ =>
            {
                attesaAvviata.TrySetResult(true);
                await continuaRetry.Task;
            });

        try
        {
            await attesaAvviata.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var duplicato = LockGate.RilasciaSessioneEsattaConRetryAsync(
                state.Locks,
                path,
                sessione,
                (_, _, _) =>
                {
                    Interlocked.Increment(ref rilascioDuplicato);
                    return EsitoIo.Errore;
                },
                _ => Task.CompletedTask);
            Assert.Same(task, duplicato);

            continuaRetry.TrySetResult(true);
            Assert.Equal(EsitoIo.Ok, await task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(4, Volatile.Read(ref tentativi));
            Assert.Equal(0, Volatile.Read(ref rilascioDuplicato));
            Assert.Null(state.Locks.GetActiveLock(path));
        }
        finally
        {
            continuaRetry.TrySetResult(true);
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    [Fact]
    public async Task LockGate_RetryEsattoRegistraIlFallimentoFinaleEConservaLaLease()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        state.TestCases.Save(Test(new[] { "D1" }, "ECM"), true);
        var path = state.TestCases.PercorsoPer(state.TestCases.GetById("T-1")!);
        var acquisizione = state.Locks.TryAcquire(path, state.Config.OperatoreAttivo);
        Assert.True(acquisizione.Acquisito);
        var sessione = Assert.IsType<string>(acquisizione.DetenutoDa?.SessionId);
        var tentativi = 0;

        try
        {
            var risultato = await LockGate.RilasciaSessioneEsattaConRetryAsync(
                state.Locks,
                path,
                sessione,
                (_, _, _) =>
                {
                    Interlocked.Increment(ref tentativi);
                    return EsitoIo.Errore;
                },
                _ => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(EsitoIo.Errore, risultato);
            Assert.Equal(6, Volatile.Read(ref tentativi));
            var fallimento = Assert.Single(LockGate.RilasciSessioneFalliti.Where(r =>
                StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(r.Percorso), Path.GetFullPath(path)) &&
                StringComparer.Ordinal.Equals(r.Sessione, sessione)));
            Assert.Equal(6, fallimento.Tentativi);
            Assert.Equal(EsitoIo.Errore, fallimento.UltimoEsito);
            Assert.Equal(sessione, state.Locks.GetActiveLock(path)?.SessionId);
        }
        finally
        {
            state.Locks.ReleaseIfSession(path, sessione);
        }
    }

    [Fact]
    public async Task LockGate_RetryDiSessioneVecchiaNonRimuoveUnLockPiuRecente()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        state.TestCases.Save(Test(new[] { "D1" }, "ECM"), true);
        var path = state.TestCases.PercorsoPer(state.TestCases.GetById("T-1")!);
        var acquisizioneVecchia = state.Locks.TryAcquire(path, state.Config.OperatoreAttivo);
        Assert.True(acquisizioneVecchia.Acquisito);
        var sessioneVecchia = Assert.IsType<string>(acquisizioneVecchia.DetenutoDa?.SessionId);
        var tentativi = 0;
        var retryInAttesa = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuaRetry = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var servizioNuovo = new LockService("PC-nuovo");
        LockResult? acquisizioneNuova = null;
        Task<EsitoIo>? taskRilascio = null;

        try
        {
            taskRilascio = LockGate.RilasciaSessioneEsattaConRetryAsync(
                state.Locks,
                path,
                sessioneVecchia,
                (locks, targetPath, targetSession) =>
                {
                    if (Interlocked.Increment(ref tentativi) == 1) return EsitoIo.Errore;
                    return locks.ReleaseIfSession(targetPath, targetSession);
                },
                async _ =>
                {
                    retryInAttesa.TrySetResult(true);
                    await continuaRetry.Task;
                });

            await retryInAttesa.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(EsitoIo.Ok, state.Locks.ReleaseIfSession(path, sessioneVecchia));
            acquisizioneNuova = servizioNuovo.TryAcquire(path, "Altro operatore");
            Assert.True(acquisizioneNuova.Acquisito);
            var sessioneNuova = Assert.IsType<string>(acquisizioneNuova.DetenutoDa?.SessionId);
            Assert.NotEqual(sessioneVecchia, sessioneNuova);

            continuaRetry.TrySetResult(true);
            Assert.Equal(EsitoIo.Ok, await taskRilascio.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, Volatile.Read(ref tentativi));
            Assert.Equal(sessioneNuova, state.Locks.GetActiveLock(path)?.SessionId);
        }
        finally
        {
            continuaRetry.TrySetResult(true);
            if (taskRilascio != null)
            {
                try { await taskRilascio.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
            if (acquisizioneNuova?.DetenutoDa?.SessionId is { } sessioneNuova)
                servizioNuovo.ReleaseIfSession(path, sessioneNuova);
        }
    }

    [Fact]
    public async Task DisposeFermaAutosaveEsistenteEUnTickGiaInCodaNonRicreaLaBozza()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        var vm = Apri(state, Test(new[] { "D1" }, "ECM"));
        try
        {
            await Task.Run(async () => await vm.IniziaModificaCommand.ExecuteAsync(null)).WaitAsync(TimeSpan.FromSeconds(10));
            var timerField = typeof(TestCaseEditorViewModel).GetField("_existingEditAutosaveTimer",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.NotNull(timerField.GetValue(vm));

            vm.TestCase.Esecuzioni[^1].Matricola = "Matricola modificato prima di Dispose";
            vm.ModificheNonSalvate = true;
            Assert.NotNull(state.BozzaAutosave.CaricaModifica(state.Config.OperatoreAttivo, vm.TestCase.Uid));

            vm.Dispose();
            Assert.Null(timerField.GetValue(vm));

            // Simula senza attese una callback Elapsed che era già stata accodata sul Dispatcher.
            state.BozzaAutosave.EliminaModifica(state.Config.OperatoreAttivo, vm.TestCase.Uid);
            var tick = typeof(TestCaseEditorViewModel).GetMethod("EseguiTickAutosaveModificaEsistente",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            tick.Invoke(vm, null);
            Assert.Null(state.BozzaAutosave.CaricaModifica(state.Config.OperatoreAttivo, vm.TestCase.Uid));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task RecuperoBozzaModifica_RifiutataAvviaModificaEAutosaveRestaAttivo()
    {
        var handlerPrecedente = ConfirmService.Handler;
        var richiesteConferma = 0;
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        state.TestCases.Save(Test(new[] { "D1" }, "ECM"), true);
        CreaBozzaCrash(state, "Matricola dalla sessione interrotta");
        ConfirmService.Handler = (_, _, _, _) =>
        {
            Interlocked.Increment(ref richiesteConferma);
            return Task.FromResult(false);
        };
        TestCaseEditorViewModel? vm = null;
        try
        {
            vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
            await Task.Run(async () => await vm.IniziaModificaCommand.ExecuteAsync(null)).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(vm.InModifica);
            Assert.Equal(1, Volatile.Read(ref richiesteConferma));
            Assert.Null(state.BozzaAutosave.CaricaModifica(state.Config.OperatoreAttivo, vm.TestCase.Uid));

            vm.TestCase.Esecuzioni[^1].Matricola = "Matricola della sessione corrente";
            vm.ModificheNonSalvate = true;
            Assert.Equal("Matricola della sessione corrente",
                state.BozzaAutosave.CaricaModifica(state.Config.OperatoreAttivo, vm.TestCase.Uid)!.TestCase.Esecuzioni[^1].Matricola);
        }
        finally
        {
            vm?.Dispose();
            ConfirmService.Handler = handlerPrecedente;
        }
    }

    [Fact]
    public async Task IniziaModifica_SenzaBozzaNonChiedeConfermaEAutosalvaLeModifiche()
    {
        var handlerPrecedente = ConfirmService.Handler;
        var richiesteConferma = 0;
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        var vm = Apri(state, Test(new[] { "D1" }, "ECM"));
        ConfirmService.Handler = (_, _, _, _) =>
        {
            Interlocked.Increment(ref richiesteConferma);
            return Task.FromResult(true);
        };
        try
        {
            await Task.Run(async () => await vm.IniziaModificaCommand.ExecuteAsync(null)).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(vm.InModifica);
            Assert.Equal(0, Volatile.Read(ref richiesteConferma));
            vm.TestCase.Esecuzioni[^1].Matricola = "Matricola nuovo";
            vm.ModificheNonSalvate = true;
            Assert.Equal("Matricola nuovo",
                state.BozzaAutosave.CaricaModifica(state.Config.OperatoreAttivo, vm.TestCase.Uid)!.TestCase.Esecuzioni[^1].Matricola);
        }
        finally
        {
            vm.Dispose();
            ConfirmService.Handler = handlerPrecedente;
        }
    }

    [Fact]
    public void FlagRequisitoVuotoScelto_Additivo_NullNonScritto_ESopravvive_AllaRipetizione()
    {
        var c = new Condizione { Guasto = "P1", Esito = Esito.DaEseguire };
        Assert.DoesNotContain("DtcRequisitoVuotoScelto", System.Text.Json.JsonSerializer.Serialize(c, JsonIo.Options));
        c.DtcRequisitoVuotoScelto = true;
        var json = System.Text.Json.JsonSerializer.Serialize(c, JsonIo.Options);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<Condizione>(json, JsonIo.Options)!.DtcRequisitoVuotoScelto);
        var vecchio = System.Text.Json.JsonSerializer.Deserialize<Condizione>("{\"Guasto\":\"P1\"}", JsonIo.Options)!;
        Assert.Null(vecchio.DtcRequisitoVuotoScelto);                             // dati 6.5.0: nessun flag = comportamento invariato

        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        var vm = Apri(state, Test(new[] { "D1" }, "ECM"));
        Assert.Null(vm.AggiungiDtcInjection("ECM", "P0001", null));
        var origine = Ops(vm)[0];
        Assert.True(origine.DtcRequisitoVuotoScelto);
        vm.RipetiCondizioneCommand.Execute(origine);
        Assert.True(Ops(vm)[1].DtcRequisitoVuotoScelto);
    }

    [Fact]
    public void SenzaEcuSulTest_ProponeLEcuDellAnagraficaDelVeicolo()
    {
        var state = CreaStato(Extra("D1", ("P0001", "ECM")));
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "ECM", VeicoliAssociati = { "V" } }, new() { Nome = "BCM", VeicoliAssociati = { "Altro" } },
        });
        var vm = Apri(state, Test(new[] { "D1" }));
        Assert.Equal(new[] { "ECM" }, vm.DtcInsEcuOpzioni.ToArray());
        Assert.Equal("ECM", vm.DtcInsEcu);
    }

    [Fact]
    public void BloccoVisibile_SoloConDtcInjectionInModificaSullaRevCorrente()
    {
        var vm = Apri(CreaStato(Extra("D1", ("P0001", "ECM"))), Test(new[] { "D1" }, "ECM"));
        Assert.False(vm.MostraBloccoAggiungiDtc);                           // lettura
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.MostraBloccoAggiungiDtc);
        vm.Dispose();
    }

    [Fact]
    public void SenzaDtcInjection_BloccoNascosto_EAggiuntaRifiutata()
    {
        var senza = Test(new[] { "D1" }, "ECM"); senza.StrumentiUsati.Clear();
        var vm = Apri(CreaStato(Extra("D1", ("P0001", "ECM"))), senza);
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.False(vm.MostraBloccoAggiungiDtc);
        Assert.Equal("DtcInsNonDisponibile", vm.AggiungiDtcInjection("ECM", "P0100", null));
        vm.Dispose();
    }

    [Fact]
    public void Cascata_VeicoloEcuDtc_DefaultVeicoloDelTest_CambioVeicoloRiazzeraEcuEDtc()
    {
        var state = CreaStato(new Requisito
        {
            Id = "D1", Testo = "t", Guasto = new List<DtcRequisito>
            {
                new() { Codice = "PA1", Componente = "ECM", Veicolo = "A" }, new() { Codice = "PB1", Componente = "BCM", Veicolo = "B" },
                new() { Codice = "PX1", Componente = "ECM" },
            }
        });
        var d1 = state.Master.Requisiti.Load().Single();
        RequisitoApplicabilita.ImpostaTipo(d1, TipoRequisito.Extra);
        state.Master.Requisiti.Save(new List<Requisito> { d1 });
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "ECM", VeicoliAssociati = { "A" } }, new() { Nome = "BCM", VeicoliAssociati = { "B" } },
        });
        var tc = Test(new[] { "D1" });
        tc.Veicolo = "A"; tc.Veicoli.Clear(); tc.Veicoli.AddRange(new[] { "A", "B" });
        var vm = Apri(state, tc);

        Assert.Equal(new[] { "A", "B" }, vm.DtcInsVeicoliOpzioni.ToArray());
        Assert.Equal(("A", "ECM"), (vm.DtcInsVeicolo, vm.DtcInsEcu));        // 6.5.2: preselezionata la prima coppia Veicolo/componente senza guasto
        Assert.Equal("DtcVeicoloMancante", vm.AggiungiDtcInjection("ECM", "P1", null));   // senza veicolo indicato (piu veicoli) resta richiesto

        vm.DtcInsVeicolo = "A";
        Assert.Equal(new[] { "ECM" }, vm.DtcInsEcuOpzioni.ToArray());       // solo i componenti di quel veicolo
        Assert.Equal("ECM", vm.DtcInsEcu);
        Assert.Equal(new[] { "PA1", "PX1" }, vm.DtcInsDisponibili.Select(o => o.Codice).ToArray());   // PB1 e del veicolo B
        vm.DtcInsScelto = vm.DtcInsDisponibili[0];

        vm.DtcInsVeicolo = "B";                                              // cambio veicolo: componente e guasto non piu validi si riazzerano
        Assert.Null(vm.DtcInsScelto);
        Assert.Equal("BCM", vm.DtcInsEcu);
        Assert.Equal(new[] { "PB1" }, vm.DtcInsDisponibili.Select(o => o.Codice).ToArray());

        Assert.Equal("DtcEcuNonInAnagrafica", vm.AggiungiDtcInjection("ECM", "P1", null, "B"));
        Assert.Equal("DtcVeicoloNonDelTest", vm.AggiungiDtcInjection("ECM", "P1", null, "Z"));
        vm.DtcInsScelto = vm.DtcInsDisponibili[0];
        vm.AggiungiDtcCommand.Execute(null);
        Assert.All(Ops(vm), o => Assert.Equal(("PB1", "B", "D1"), (o.Guasto, o.DtcVeicolo, o.DtcRequisitoId)));
        Assert.Equal(new[] { "BCM" }, Ops(vm)[0].Componente.ToArray());
        Assert.Equal(("A", "ECM"), (vm.DtcInsVeicolo, vm.DtcInsEcu));        // avanza alla prima coppia ancora senza guasto
        vm.DtcInsVeicolo = "B";                                            // l'utente puo tornare alla coppia gia coperta
        vm.DtcInsCodiceManuale = "PM9";                                      // manuale: stessa coppia Veicolo/componente scelta
        vm.AggiungiDtcCommand.Execute(null);
        Assert.Equal(("PM9", "B"), (Ops(vm)[2].Guasto, Ops(vm)[2].DtcVeicolo));
        Assert.Equal(new[] { "BCM" }, Ops(vm)[2].Componente.ToArray());
    }

}
