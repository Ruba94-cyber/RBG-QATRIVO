using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Interscambio Excel delle coppie Extra Veicolo-componente dei requisiti: colonna E opzionale, compatibile all'indietro.</summary>
[Collection("Localization")]
public sealed class RequisitiExcelD4DTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReqD4D_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;
    private readonly List<AppState> _stati = new();

    public void Dispose()
    {
        ConfirmService.Handler = _confirmOriginale;
        foreach (var s in _stati) { try { s.Dispose(); } catch { } }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStato(params Requisito[] requisiti)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        _stati.Add(state);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Extra", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList(); ops.Add(op); state.Master.Operatori.Save(ops);
        Assert.True(state.ImpostaOperatore("Master Extra", "1234"));
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily 24" }, new() { Nome = "eDaily 24" } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ABS" } });
        if (requisiti.Length > 0) state.Master.Requisiti.Save(requisiti.ToList());
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        return state;
    }

    private static Requisito Extra(string id, params (string V, string E)[] coppie)
    {
        var r = new Requisito { Id = id, Testo = "Testo " + id, ApplicabileBanco = true, UltimaModifica = DateTime.Now, ModificatoDa = "Test" };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        if (coppie.Length > 0) r.VeicoliEcuD4D = coppie.Select(c => new RequisitoVeicoloEcu { Veicolo = c.V, Componente = c.E }).ToList();
        return r;
    }

    private string Excel(int colonne, params string[][] righe)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Requisiti");
        var intest = new[] { "Id", "Testo", "Banco", "Veicolo", "Veicolo-componente Extra" };
        for (var c = 0; c < colonne; c++) ws.Cell(1, c + 1).Value = intest[c];
        for (var r = 0; r < righe.Length; r++)
            for (var c = 0; c < righe[r].Length; c++) ws.Cell(r + 2, c + 1).Value = righe[r][c];
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xlsx");
        wb.SaveAs(p);
        return p;
    }

    private static RequisitiViewModel Apri(AppState state, string percorso)
    {
        var vm = new RequisitiViewModel(state, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));
        return vm;
    }

    private static string Coppie(Requisito r) => string.Join(";", (r.VeicoliEcuD4D ?? new()).Select(c => c.Veicolo + "|" + c.Componente));

    [Fact]
    public void FileVecchioASoleQuattroColonne_NonCancellaLeCoppieEsistenti()
    {
        var state = CreaStato(Extra("R1", ("Daily 24", "BCM")));
        var vm = Apri(state, Excel(4, new[] { "R1", "Testo nuovo", "si", "no" }, new[] { "R2", "Altro", "si", "si" }));
        Assert.False(vm.HaMessaggioErrore);
        Assert.Equal("Daily 24|BCM", Coppie(vm.Items.Single(x => x.Id == "R1")));
        var r2 = vm.Items.Single(x => x.Id == "R2");
        Assert.True(r2.ApplicabileVeicolo);
        Assert.Null(r2.VeicoliEcuD4D);
    }

    [Fact]
    public void ColonnaEVuota_NonCancellaLeCoppieEsistenti()
    {
        var state = CreaStato(Extra("R1", ("Daily 24", "BCM")));
        var vm = Apri(state, Excel(5, new[] { "R1", "x", "si", "no", "" }));
        Assert.False(vm.HaMessaggioErrore);
        Assert.Equal("Daily 24|BCM", Coppie(vm.Items.Single(x => x.Id == "R1")));
    }

    [Fact]
    public void ColonnaE_AggiungeCoppieSenzaPerdereQuelleEsistenti_ESuNuoviRequisiti()
    {
        var state = CreaStato(Extra("R1", ("Daily 24", "BCM")));
        var vm = Apri(state, Excel(5,
            new[] { "R1", "x", "si", "no", "Daily 24|BCM; eDaily 24|ABS" },
            new[] { "R9", "Nuovo", "si", "no", "eDaily 24|BCM" }));
        Assert.False(vm.HaMessaggioErrore);
        Assert.Equal("Daily 24|BCM;eDaily 24|ABS", Coppie(vm.Items.Single(x => x.Id == "R1")));
        var r9 = vm.Items.Single(x => x.Id == "R9");
        Assert.Equal("eDaily 24|BCM", Coppie(r9));
        Assert.True(RequisitoApplicabilita.ED4D(r9));
        // Undo ripristina le coppie originali di R1.
        vm.AnnullaUltimoImportCommand.Execute(null);
        Assert.Equal("Daily 24|BCM", Coppie(vm.Items.Single(x => x.Id == "R1")));
        Assert.DoesNotContain(vm.Items, x => x.Id == "R9");
    }

    [Fact]
    public void ColonnaE_FormatoNonValidoOMasterSconosciuto_BloccaTuttoSenzaModifiche()
    {
        var state = CreaStato(Extra("R1", ("Daily 24", "BCM")));
        var vm = Apri(state, Excel(5, new[] { "R1", "x", "si", "no", "Daily 24|ABS" }, new[] { "R2", "x", "si", "no", "SoloVeicolo" }));
        Assert.True(vm.HaMessaggioErrore);
        Assert.DoesNotContain(vm.Items, x => x.Id == "R2");
        Assert.Equal("Daily 24|BCM", Coppie(vm.Items.Single(x => x.Id == "R1")));
    }

    [Fact]
    public void ColonnaE_VeicoloOEcuNonNeiMaster_Bloccata()
    {
        var state = CreaStato(Extra("R1", ("Daily 24", "BCM")));
        var vm2 = Apri(state, Excel(5, new[] { "R3", "x", "si", "no", "Fantasma|BCM" }));
        Assert.True(vm2.HaMessaggioErrore);
        Assert.DoesNotContain(vm2.Items, x => x.Id == "R3");
    }

    [Fact]
    public void ColonnaE_VeicoloFuoriDaiSottoveicoliDelRequisito_BloccaTuttoSenzaModifiche()
    {
        var r1 = Extra("R1", ("Daily 24", "BCM"));
        RequisitoApplicabilita.ImpostaSottoveicoli(r1, new[] { "Daily 24" });
        var state = CreaStato(r1, Extra("R5"));
        var vm = Apri(state, Excel(5,
            new[] { "R5", "x", "si", "no", "eDaily 24|ABS" },
            new[] { "R1", "x", "si", "no", "eDaily 24|ABS" }));
        Assert.True(vm.HaMessaggioErrore);
        Assert.Contains("eDaily 24", vm.MessaggioErrore);
        Assert.Equal("Daily 24|BCM", Coppie(vm.Items.Single(x => x.Id == "R1")));
        Assert.Null(vm.Items.Single(x => x.Id == "R5").VeicoliEcuD4D);
    }

    [Fact]
    public void ColonnaE_VeicoloAmmessoDaiSottoveicoli_AggiungeLaCoppia()
    {
        var r1 = Extra("R1", ("Daily 24", "BCM"));
        RequisitoApplicabilita.ImpostaSottoveicoli(r1, new[] { "Daily 24", "eDaily 24" });
        var state = CreaStato(r1);
        var vm = Apri(state, Excel(5, new[] { "R1", "x", "si", "no", "eDaily 24|ABS" }));
        Assert.False(vm.HaMessaggioErrore);
        Assert.Equal("Daily 24|BCM;eDaily 24|ABS", Coppie(vm.Items.Single(x => x.Id == "R1")));
    }

    [Fact]
    public async Task EsportaExcelVerso_ScriveLeSoleRichiesteDellaSchedaCorrenteNelFormatoDiImport()
    {
        var standard = new Requisito { Id = "S1", Testo = "std", ApplicabileBanco = true, UltimaModifica = DateTime.Now, ModificatoDa = "Test" };
        var state = CreaStato(Extra("R1", ("Daily 24", "BCM")), standard);
        var vm = new RequisitiViewModel(state, TipoRequisito.Extra);
        var file = Path.Combine(_dir, "export-vm.xlsx");
        await vm.EsportaExcelVerso(file);
        using var wb = new XLWorkbook(file);
        var ws = wb.Worksheet(RequisitiExcelExportService.NomeFoglio);
        Assert.Equal("R1", ws.Cell(2, 1).GetString());
        Assert.Equal("Daily 24|BCM", ws.Cell(2, 5).GetString());
        Assert.True(ws.Cell(3, 1).IsEmpty());
        Assert.True(vm.HaMessaggio);
    }

    [Fact]
    public void RoundTrip_EsportaPoiImportaSuCatalogoVuotoRicostruisceLeCoppie()
    {
        var sorgente = new[] { Extra("R1", ("Daily 24", "BCM"), ("eDaily 24", "ABS")), Extra("R2") };
        var file = Path.Combine(_dir, "export.xlsx");
        Directory.CreateDirectory(_dir);
        RequisitiExcelExportService.Esporta(sorgente, file);

        using (var wb = new XLWorkbook(file))
        {
            var ws = wb.Worksheet(RequisitiExcelExportService.NomeFoglio);
            Assert.Equal("Veicolo-componente Extra", ws.Cell(1, 5).GetString());
            Assert.Equal("Daily 24|BCM; eDaily 24|ABS", ws.Cell(2, 5).GetString());
            Assert.Equal("", ws.Cell(3, 5).GetString());
        }

        var state = CreaStato();
        var vm = Apri(state, file);
        Assert.False(vm.HaMessaggioErrore);
        Assert.Equal("Daily 24|BCM;eDaily 24|ABS", Coppie(vm.Items.Single(x => x.Id == "R1")));
        Assert.Null(vm.Items.Single(x => x.Id == "R2").VeicoliEcuD4D);
        Assert.True(vm.Items.Single(x => x.Id == "R1").ApplicabileBanco);
    }

    [Fact]
    public void SchedaStandard_ConColonnaEValorizzata_VieneRifiutata()
    {
        var state = CreaStato();
        var vm = new RequisitiViewModel(state, TipoRequisito.Standard);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(Excel(5, new[] { "S1", "x", "si", "no", "Daily 24|BCM" }), null, 1));
        Assert.True(vm.HaMessaggioErrore);
        Assert.DoesNotContain(vm.Items, x => x.Id == "S1");
    }
}
