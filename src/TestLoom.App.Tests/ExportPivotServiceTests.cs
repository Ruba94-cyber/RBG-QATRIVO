using ClosedXML.Excel;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class ExportPivotServiceTests
{
    [Theory]
    [InlineData("nessuna", 1)]
    [InlineData("riga", 2)]
    [InlineData("colonna", 2)]
    [InlineData("totale", 2)]
    public void Pivot_HaSorgenteDedicataEValoriConfigurati(string percentuale, int valori)
    {
        using var wb = new XLWorkbook();
        var campi = new[] { new ExportColonna("veicolo", "Veicolo"), new ExportColonna("stato", "Stato") };
        ExportPivotService.Scrivi(wb, campi, new IReadOnlyList<object?>[] { new object?[] { "V1", "Aperto" }, new object?[] { "V2", "Chiuso" } }, "veicolo", "stato", percentuale);
        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;
        using var riaperto = new XLWorkbook(stream);
        var pivot = riaperto.Worksheets.Single(w => w.PivotTables.Any()).PivotTables.Single();
        Assert.Equal(valori, pivot.Values.Count());
        var origine = riaperto.Worksheets.Single(w => w.Name != "Pivot");
        Assert.Equal("V1", origine.Cell(2, 1).GetString());
        Assert.Equal("Chiuso", origine.Cell(3, 2).GetString());
        Assert.Equal(1, origine.Cell(2, 3).GetDouble());
    }

    [Fact]
    public void PivotSenzaDati_MostraMessaggioSenzaInventareRighe()
    {
        using var wb = new XLWorkbook();
        ExportPivotService.Scrivi(wb, new[] { new ExportColonna("a", "A"), new ExportColonna("b", "B") }, Array.Empty<IReadOnlyList<object?>>(), null, null, null);
        Assert.Empty(wb.Worksheets.SelectMany(w => w.PivotTables));
        Assert.Equal(1, wb.Worksheets.First().LastRowUsed()!.RowNumber());
    }

    [Fact]
    public void StessoCampoInRigheEColonne_VieneRifiutato()
    {
        using var wb = new XLWorkbook();
        Assert.Throws<ArgumentException>(() => ExportPivotService.Scrivi(wb, new[] { new ExportColonna("a", "A"), new ExportColonna("b", "B") }, Array.Empty<IReadOnlyList<object?>>(), "a", "a", "riga"));
    }
    [Fact]
    public void PivotNcPerVeicolo_EspandeAssociazioniSenzaDuplicatiEMantieneNcSenzaVeicolo()
    {
        var righe = new[]
        {
            new TestLoom.App.ViewModels.NcRiga { Nc = new TestLoom.App.Models.NonConformita { Id = "NC1", VeicoliSegnalati = ["A", "B", "a"] } },
            new TestLoom.App.ViewModels.NcRiga { Nc = new TestLoom.App.Models.NonConformita { Id = "NC2" } },
        };
        var colonne = AnomalieCrExportVisteService.ColonneEsportabiliVistaNc();
        var indiceVeicolo = colonne.ToList().FindIndex(c => c.Id == "veicoli");
        var espanse = AnomalieCrExportVisteService.RighePivotNcPerVeicolo(righe).ToList();
        Assert.Equal(3, espanse.Count);
        Assert.Equal(new[] { "A", "B", "" }, espanse.Select(r => r[indiceVeicolo]));
        Assert.Equal(new[] { "NC1", "NC1", "NC2" }, espanse.Select(r => r[0]));
    }
    [Fact]
    public void PivotNcPerVeicolo_RispettaFiltroSulleAssociazioni()
    {
        var nc = new TestLoom.App.Models.NonConformita { Id = "NC1", VeicoliSegnalati = ["A", "B"] };
        var righe = new[] { new TestLoom.App.ViewModels.NcRiga { Nc = nc } };
        var riga = Assert.Single(AnomalieCrExportVisteService.RighePivotNcPerVeicolo(righe, ["b"]));
        var indice = AnomalieCrExportVisteService.ColonneEsportabiliVistaNc().ToList().FindIndex(c => c.Id == "veicoli");
        Assert.Equal("B", riga[indice]);
    }

    [Fact]
    public void PivotNcECr_ConservanoDateEVeicoliCompleti()
    {
        var apertura = new DateTime(2026, 4, 5);
        var chiusura = new DateTime(2026, 5, 7);
        var nc = new TestLoom.App.ViewModels.NcRiga { Nc = new TestLoom.App.Models.NonConformita { DataApertura = apertura, DataChiusura = chiusura } };
        var cr = new TestLoom.App.Models.ChangeRequest { DataCreazione = apertura, ChiusaIl = chiusura, VeicoliImpattati = ["A", "B", "C"] };
        var ncFields = AnomalieCrExportVisteService.ColonneEsportabiliVistaNc().ToList();
        var crFields = AnomalieCrExportVisteService.ColonneEsportabiliVistaCr().ToList();
        var ncRow = Assert.Single(AnomalieCrExportVisteService.RighePivotNc([nc]));
        var crRow = Assert.Single(AnomalieCrExportVisteService.RighePivotCr([cr]));
        Assert.Equal(apertura, ncRow[ncFields.FindIndex(c => c.Id == "apertura")]);
        Assert.Equal(chiusura, ncRow[ncFields.FindIndex(c => c.Id == "chiusura")]);
        Assert.Equal(apertura, crRow[crFields.FindIndex(c => c.Id == "apertura")]);
        Assert.Equal(chiusura, crRow[crFields.FindIndex(c => c.Id == "chiusura")]);
        Assert.Equal("A, B, C", crRow[crFields.FindIndex(c => c.Id == "veicoli")]);
    }
}
