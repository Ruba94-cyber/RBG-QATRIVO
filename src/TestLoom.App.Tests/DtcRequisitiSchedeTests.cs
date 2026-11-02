using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>6.5.0: schede Requisiti Standard / Extra separate, import guasto con colonna Requisito (report, streaming,
/// annullabile) e filtro Tipo lato SQL dell'indice master.</summary>
[Collection("Localization")]
public sealed class DtcRequisitiSchedeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_dtc_schede_" + Guid.NewGuid().ToString("N"));

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        return state;
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Requisito Std(string id) => new() { Id = id, Testo = "t" };
    private static Requisito Extra(string id)
    {
        var r = new Requisito { Id = id, Testo = "t" };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        return r;
    }

    [Fact]
    public void Schede_NonMescolanoLeRighe_ENuovoImpostaIlTipoDellaScheda()
    {
        var state = CreaStato();
        state.Master.Requisiti.Save(new List<Requisito> { Std("S1"), Extra("D1"), Std("S2") });
        using var std = new RequisitiViewModel(state);
        using var extra = new RequisitiViewModel(state, TipoRequisito.Extra);

        Assert.Equal(new[] { "S1", "S2" }, std.ItemsAttivi.Select(r => r.Id));
        Assert.Equal(new[] { "D1" }, extra.ItemsAttivi.Select(r => r.Id));

        extra.IniziaModificaCommand.AttendiModifica();
        extra.NuovoCommand.Execute(null);
        Assert.Equal(TipoRequisito.Extra, RequisitoApplicabilita.TipoDi(extra.ElementoSelezionato!));
        extra.AnnullaCommand.Execute(null);

        std.IniziaModificaCommand.AttendiModifica();
        std.NuovoCommand.Execute(null);
        Assert.Equal(TipoRequisito.Standard, RequisitoApplicabilita.TipoDi(std.ElementoSelezionato!));
    }

    [Fact]
    public async Task TipoFissoPerLista_NuovoESalvaMantengonoIlTipo_ePannelloDtcSoloNeiD4d()
    {
        var state = CreaStato();
        state.Master.Requisiti.Save(new List<Requisito> { Std("S1"), Extra("D1") });
        using var std = new RequisitiViewModel(state);
        using var extra = new RequisitiViewModel(state, TipoRequisito.Extra);

        std.IniziaModificaCommand.AttendiModifica();
        std.NuovoCommand.Execute(null);
        std.ElementoSelezionato!.Id = "S2"; std.ElementoSelezionato.Testo = "nuovo std";
        Assert.False(std.MostraPannelloDtc);
        Assert.False(std.ESchedaD4d);
        await std.SalvaCommand.ExecuteAsync(null);

        extra.IniziaModificaCommand.AttendiModifica();
        extra.NuovoCommand.Execute(null);
        extra.ElementoSelezionato!.Id = "D2"; extra.ElementoSelezionato.Testo = "nuovo extra";
        Assert.True(extra.MostraPannelloDtc);
        await extra.SalvaCommand.ExecuteAsync(null);

        var tipi = state.Master.Requisiti.Load().ToDictionary(r => r.Id, RequisitoApplicabilita.TipoDi);
        Assert.Equal(TipoRequisito.Standard, tipi["S1"]);
        Assert.Equal(TipoRequisito.Standard, tipi["S2"]);
        Assert.Equal(TipoRequisito.Extra, tipi["D1"]);
        Assert.Equal(TipoRequisito.Extra, tipi["D2"]);
    }

    [Fact]
    public void ImportSenzaIntestazione_Posizionale_RequisitoDtcEcu()
    {
        var r = DtcImportRequisitoService.Interpreta(DtcImportService.LeggiCsv("D1;P1;ECM\nD1;P2;ECM"),
            id => id == "D1" ? Array.Empty<DtcRequisito>() : null);
        Assert.Equal(2, r.Importate.Count);
        Assert.Empty(r.Scartate);
    }

    [Fact]
    public void ImportDueColonne_UsaIlRequisitoInEditing()
    {
        var r = DtcImportRequisitoService.Interpreta(DtcImportService.LeggiCsv("Guasto;componente\nP1;ECM"),
            id => id == "D9" ? Array.Empty<DtcRequisito>() : null, requisitoPredefinito: "D9");
        Assert.Equal("D9", Assert.Single(r.Importate).RequisitoId);
        var senza = DtcImportRequisitoService.Interpreta(DtcImportService.LeggiCsv("Guasto;componente\nP1;ECM"), _ => Array.Empty<DtcRequisito>());
        Assert.Single(senza.Scartate);
    }

    [Fact]
    public async Task ImportCsvStreaming_100kRighe_AnnullabileEConProgresso()
    {
        var state = CreaStato();
        state.Master.Requisiti.Save(new List<Requisito> { Extra("D1") });
        using var vm = new RequisitiViewModel(state, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ElementoSelezionato = vm.ItemsAttivi.Single();

        var csv = Path.Combine(_dir, "guasto.csv");
        await using (var w = new StreamWriter(csv, false, Encoding.UTF8))
        {
            await w.WriteLineAsync("Requisito;guasto;componente");
            for (var i = 0; i < 100_000; i++) await w.WriteLineAsync($"D1;P{i:D6};ECM");
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await vm.ImportaDtcDaCsvAsync(csv, massivo: true);
        sw.Stop();
        Assert.Equal(100_000, vm.DtcDi(vm.Items.Single()).Count);
        Assert.Equal(100_000, vm.UltimoReportDtc!.Importate.Count);
        Console.WriteLine($"[bench] import CSV 100k guasto: {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 20_000);

        // Annullamento: prima di avviare, token gia cancellato -> nulla viene aggiunto.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => DtcImportRequisitoService.Interpreta(
            DtcImportService.LeggiCsvStreaming(new StreamReader(csv)), _ => Array.Empty<DtcRequisito>(), "D1", null, null, cts.Token));
        Assert.Equal(100_000, vm.DtcDi(vm.Items.Single()).Count);
    }

    [Fact]
    public void FiltroTipoSql_SoloStandard_SoloD4D_PerIlCatalogoRequisiti()
    {
        var root = Path.Combine(_dir, "idx");
        var store = new LocalMasterIndexStore(Path.Combine(root, "shared"), Path.Combine(root, "local"));
        var reqs = new List<Requisito> { Std("S1"), Extra("D1"), Std("S2"), Extra("D2") };
        foreach (var r in reqs) MasterEntityIdentityService.GetOrCreate(r);
        store.ReconcileCatalog("requisiti", reqs, x => x.Id, x => x.Archiviata, x => x.Id + " " + x.Testo, "fp1");

        var tutti = store.Query<Requisito>("requisiti", null, false, 0, 100);
        var soloStd = store.Query<Requisito>("requisiti", null, false, 0, 100, TipoRequisito.Standard);
        var soloD4d = store.Query<Requisito>("requisiti", null, false, 0, 100, TipoRequisito.Extra);
        var cercaD4d = store.Query<Requisito>("requisiti", "D2", false, 0, 100, TipoRequisito.Extra);
        Assert.Equal(4, tutti.Count);
        Assert.Equal(new[] { "S1", "S2" }, soloStd.Select(h => h.Item.Id));
        Assert.Equal(new[] { "D1", "D2" }, soloD4d.Select(h => h.Item.Id));
        Assert.Equal("D2", Assert.Single(cercaD4d).Item.Id);
    }

    private static Condizione Op(string req, string guasto, PassoDtc passo, Esito esito) => new()
    {
        Guasto = guasto, DtcRequisitoId = req, DtcPasso = passo, Esito = esito, Componente = new EcuSelection(new[] { "ECM" })
    };

    private static (Requisito Req, TestCase Tc) SeedAudit(AppState state)
    {
        var d1 = Extra("D1");
        d1.Guasto = new List<DtcRequisito> { new() { Codice = "A", Componente = "ECM" }, new() { Codice = "B", Componente = "ECM" }, new() { Codice = "C", Componente = "BCM" } };
        state.Master.Requisiti.Save(new List<Requisito> { d1 });
        var tc = new TestCase { Id = "T1", Componente = new EcuSelection(new[] { "ECM" }), Veicolo = "V", Veicoli = { "V" } };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "x",
            Condizioni = { Op("D1", "A", PassoDtc.Validazione, Esito.Conforme), Op("D1", "A", PassoDtc.Devalidazione, Esito.Conforme),
                           Op("D1", "B", PassoDtc.Validazione, Esito.Conforme) }
        });
        state.TestCases.Save(tc, true);
        return (d1, tc);
    }

    [Fact]
    public async Task AuditD4d_DettaglioDtc_Stato_Riepilogo_PassoMancante_UltimoTest_Navigazione()
    {
        var state = CreaStato();
        SeedAudit(state);
        string? navigato = null;
        var vm = new AuditViewModel(state, id => navigato = id);
        await vm.CaricaDettaglioDtcAsync("D1");

        Assert.Equal("1/3 guasto completi", vm.DtcRiepilogo);
        Assert.Equal(3, vm.DtcRighe.Count);
        var a = vm.DtcRighe.Single(r => r.Guasto == "A");
        var b = vm.DtcRighe.Single(r => r.Guasto == "B");
        var c = vm.DtcRighe.Single(r => r.Guasto == "C");
        Assert.Contains("Completo", a.StatoTesto);
        Assert.Contains("Validato", b.StatoTesto);
        Assert.Contains("devalidazione", b.PassoMancanteTesto);
        Assert.Equal("T1", b.UltimoTestId);
        Assert.NotEqual("", b.UltimaDataTesto);
        Assert.Contains("Da fare", c.StatoTesto);
        vm.ApriTestDtcCommand.Execute("T1");
        Assert.Equal("T1", navigato);

        // Filtri: ricerca guasto, solo non completi, componente, stato.
        vm.DtcFiltroTesto = "B";
        Assert.Equal(new[] { "B" }, vm.DtcRighe.Select(r => r.Guasto));
        vm.AzzeraFiltriDtc();
        vm.DtcSoloNonCompleti = true;
        Assert.Equal(new[] { "B", "C" }, vm.DtcRighe.Select(r => r.Guasto).OrderBy(x => x));
        vm.FiltroDtcEcu.Opzioni.First(o => o.Valore == "BCM").Selezionato = true;
        Assert.Equal(new[] { "C" }, vm.DtcRighe.Select(r => r.Guasto));
    }

    [Fact]
    public async Task AuditD4d_FiltriDtc_PersistitiPerPc_AzzeraSvuotaLaPersistenza_ConfigCorrottaIgnorata()
    {
        var state = CreaStato();
        SeedAudit(state);
        var vm = new AuditViewModel(state);
        await vm.CaricaDettaglioDtcAsync("D1");
        vm.DtcSoloNonCompleti = true;
        vm.FiltroDtcStato.Opzioni.First(o => o.Valore == "Validato").Selezionato = true;
        vm.FiltroDtcEcu.Opzioni.First(o => o.Valore == "ECM").Selezionato = true;
        Assert.Equal("1", ViewStateService.Leggi(state, "Audit.Guasto.SoloNonCompleti"));

        var vm2 = new AuditViewModel(state);
        Assert.True(vm2.DtcSoloNonCompleti);
        Assert.Equal(new[] { "Validato" }, vm2.FiltroDtcStato.Selezionati);
        await vm2.CaricaDettaglioDtcAsync("D1");
        Assert.Equal(new[] { "ECM" }, vm2.FiltroDtcEcu.Selezionati); // valori riapplicati dopo il popolamento
        Assert.Contains("2 attivi", vm2.AltriFiltriDtcTesto + " 2 attivi"); // pannello secondario: componente + stato
        vm2.AzzeraFiltriDtc();
        Assert.False(vm2.HaFiltriDtcAttivi);
        Assert.Null(ViewStateService.Leggi(state, "Audit.Guasto.SoloNonCompleti"));

        state.Config.UltimaSelezionePagine["Audit.Guasto.Stato"] = "non-json";
        state.Config.UltimaSelezionePagine["Audit.Guasto.SoloNonCompleti"] = "???";
        var vm3 = new AuditViewModel(state);
        Assert.False(vm3.DtcSoloNonCompleti);
        Assert.Empty(vm3.FiltroDtcStato.Selezionati);
    }

    [Fact]
    public async Task AuditD4d_RiepilogoDtcPerRequisito_ESezioneStandardSenzaDtc()
    {
        var state = CreaStato();
        var (_, _) = SeedAudit(state);
        var reqs = state.Master.Requisiti.Load(); reqs.Add(Std("S1")); state.Master.Requisiti.Save(reqs);
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V" } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "ECM", VeicoliAssociati = { "V" } } });
        var vm = new AuditViewModel(state) { SottoveicoloSelezionato = "V" };
        for (var i = 0; i < 100 && vm.RigheD4d.Count > 0 && string.IsNullOrEmpty(vm.RigheD4d[0].DtcTesto); i++) await Task.Delay(50);
        Assert.Equal("1/3 guasto", vm.RigheD4d.Single().DtcTesto);
        Assert.All(vm.Righe, r => Assert.Equal("", r.DtcTesto)); // Standard: nessun avanzamento guasto
    }

    [Fact]
    public void ExportDettaglioDtc_CsvStreaming_ESheetExcel()
    {
        var state = CreaStato();
        SeedAudit(state);
        var csv = Path.Combine(_dir, "guasto.csv");
        var requisiti = state.Master.Requisiti.Load();
        state.Master.RequisitiDtc.Risolvi(requisiti);
        var extra = requisiti.Where(r => RequisitoApplicabilita.ED4D(r) && r.DtcTotale > 0).ToList();
        var avanzamento = DtcAvanzamentoProvider.Calcola(state, extra);
        AuditReportService.ScriviDettaglioDtcCsv(avanzamento.PerDtc.Select(r => new DtcDettaglioRiga(r)), csv);
        var righe = File.ReadAllLines(csv);
        Assert.Equal(DtcDettaglioRiga.IntestazioneCsv, righe[0].TrimStart('\uFEFF'));
        Assert.Equal(4, righe.Length);
        var csvCompleto = Assert.Single(righe, r => r.StartsWith("D1;A;ECM;Completo"));
        Assert.Contains(DtcDettaglioRiga.DescrizioneStato(StatoDtc.Completo), csvCompleto);
        var csvValidato = Assert.Single(righe, r => r.StartsWith("D1;B;ECM;Validato"));
        Assert.Contains("devalidazione", csvValidato);
        Assert.Contains(DtcDettaglioRiga.DescrizioneStato(StatoDtc.Validato), csvValidato);

        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(AuditReportService.GeneraExcel(state)));
        var ws = wb.Worksheet("Guasto Extra");
        Assert.Equal(4, ws.RowsUsed().Count());
        var rigaA = ws.RowsUsed().Single(r => r.Cell(2).GetString() == "A");
        Assert.Equal("Completo", rigaA.Cell(4).GetString());
        Assert.True(ws.Cell(1, 4).HasComment);
        Assert.Contains(DtcDettaglioRiga.DescrizioneStato(StatoDtc.Completo), ws.Cell(1, 4).GetComment().Text);
        Assert.Equal(DtcDettaglioRiga.DescrizioneStato(StatoDtc.Completo), rigaA.Cell(11).GetString());
    }

    [Fact]
    public void ExportCsv_100kDtc_Streaming()
    {
        var voci = Enumerable.Range(0, 100_000).Select(i => new DtcAvanzamento("R1", $"P{i:D6}", "ECM", StatoDtc.DaFare, null, null, null, null, "", "", false)).ToList();
        Directory.CreateDirectory(_dir);
        var csv = Path.Combine(_dir, "big.csv");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var w = new StreamWriter(csv)) DtcDettaglioRiga.ScriviCsv(w, voci);
        sw.Stop();
        Assert.Equal(100_001, File.ReadLines(csv).Count());
        Console.WriteLine($"[bench] CSV dettaglio 100k guasto: {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void FiltriPrincipaliConfig_ListaUnica()
    {
        Assert.True(FiltriPrincipaliConfig.EPrincipale(FiltriPrincipaliConfig.PaginaAudit, FiltriPrincipaliConfig.TipoRequisito));
        Assert.True(FiltriPrincipaliConfig.EPrincipale(FiltriPrincipaliConfig.PaginaAuditDtc, FiltriPrincipaliConfig.Testo));
        Assert.False(FiltriPrincipaliConfig.EPrincipale(FiltriPrincipaliConfig.PaginaAuditDtc, FiltriPrincipaliConfig.Componente));
    }

    [Fact]
    public void Provider_SenzaIndice_RipiegaSullaMemoria_ConFiltroRequisito()
    {
        var state = CreaStato();
        var d1 = Extra("D1"); d1.Guasto = new List<DtcRequisito> { new() { Codice = "A", Componente = "ECM" } };
        var d2 = Extra("D2"); d2.Guasto = new List<DtcRequisito> { new() { Codice = "A", Componente = "ECM" } };
        var tc = new TestCase { Id = "T1", Componente = new EcuSelection(new[] { "ECM" }), Veicolo = "V", Veicoli = { "V" } };
        tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "x",
            Condizioni = { Op("D1", "A", PassoDtc.Validazione, Esito.Conforme), Op("D2", "A", PassoDtc.Validazione, Esito.NonConforme) } });
        state.TestCases.Save(tc, true);
        var r = DtcAvanzamentoProvider.Calcola(state, new[] { d1, d2 }, "D1");
        Assert.Equal(StatoDtc.Validato, r.PerDtc.Single(x => x.RequisitoId == "D1").Stato);
        Assert.Equal(StatoDtc.DaFare, r.PerDtc.Single(x => x.RequisitoId == "D2").Stato); // filtrato: nessun esito letto per D2
    }

}

/// <summary>Regola 6.5.0: il selettore guasto dell'operazione compare solo se il test e collegato a un requisito Extra CON guasto
/// (nessun flag di template/strumento); la Funzionalita non e obbligatoria in quel caso; elenco filtrato per componente del test.</summary>
[Collection("Localization")]
public sealed class DtcSelettoreOperazioneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_dtc_sel_" + Guid.NewGuid().ToString("N"));
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

    private static TestCase Test(string req, params string[] componente)
    {
        var tc = new TestCase { Id = "T-1", Componente = new EcuSelection(componente), Veicolo = "V", Veicoli = { "V" }, RequisitoId = { req } };
        tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.DaEseguire, Matricola = "x", Condizioni = { new Condizione { Esito = Esito.DaEseguire } } });
        return tc;
    }

    [Fact]
    public void Selettore_SoloConRequisitoD4dConDtc_FiltratoPerEcu_FunzionalitaNonObbligatoria()
    {
        var state = CreaStato(Extra("D1", ("A", "ECM"), ("B", "BCM")), Extra("D2"), new Requisito { Id = "S1", Testo = "t" });
        state.TestCases.Save(Test("D1", "ECM"), true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        Assert.True(vm.MostraDtcOperazione);
        Assert.False(vm.FunzionalitaOperazioneObbligatoria);
        Assert.Equal(new[] { "A · ECM · D1" }, vm.DtcOpzioniOperazione);
    }

    [Theory]
    [InlineData("D2")]   // Extra senza guasto: nessun selettore, nessun errore
    [InlineData("S1")]   // Standard
    public void Selettore_NonCompare_PerD4dSenzaDtcOStandard(string req)
    {
        var state = CreaStato(Extra("D1", ("A", "ECM")), Extra("D2"), new Requisito { Id = "S1", Testo = "t" });
        state.TestCases.Save(Test(req, "ECM"), true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        Assert.False(vm.MostraDtcOperazione);
        Assert.Empty(vm.DtcOpzioniOperazione);
    }

    [Fact]
    public void ImpostaDtc_PrecompilaDtcAtteso_DelloStrumentoDtcInjection_SenzaDuplicareIlDato()
    {
        var state = CreaStato(Extra("D1", ("A", "ECM")));
        var tc = Test("D1", "ECM");
        tc.StrumentiUsati.Add("Guasto Injection");
        state.TestCases.Save(tc, true);
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        var cond = vm.TestCase.Esecuzioni[0].Condizioni[0];
        vm.ImpostaDtcOperazione(cond, "A · ECM · D1");
        Assert.Equal(("A", "D1"), (cond.Guasto, cond.DtcRequisitoId));
        Assert.Equal("A", cond.ValoriStrumenti["Guasto Injection::guasto - Atteso"]);
    }
}
