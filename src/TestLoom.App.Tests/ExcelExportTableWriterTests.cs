using ClosedXML.Excel;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class ExcelExportTableWriterTests
{
    [Fact]
    public void ValoriLunghiEConMolteInterruzioni_RestanoRicostruibiliDaiFogliDettagli()
    {
        using var workbook = new XLWorkbook();
        var testoLungo = string.Concat(Enumerable.Repeat("prova😀", 5_000));
        var testoConMolteRighe = string.Join("\n", Enumerable.Range(0, 260).Select(i => $"riga {i}"));
        var writer = new ExcelExportTableWriter(workbook, "NC", ["ID", "Descrizione", "Cronologia"]);

        writer.AggiungiRiga("NC-1", ["NC-1", testoLungo, testoConMolteRighe]);
        writer.Completa();

        var principale = workbook.Worksheet("NC");
        Assert.Contains("Testo esteso", principale.Cell(2, 2).GetString());
        Assert.Contains("Testo esteso", principale.Cell(2, 3).GetString());
        var dettagli = workbook.Worksheets.Where(w => w.Name.StartsWith("NC dettagli estesi", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(dettagli);
        var parti = dettagli.SelectMany(w => w.RowsUsed().Skip(1).Select(r =>
            (Riferimento: r.Cell(1).GetString(), Campo: r.Cell(2).GetString(), Parte: r.Cell(3).GetString(), Testo: r.Cell(4).GetString()))).ToList();
        Assert.Equal(testoLungo, string.Concat(parti.Where(p => p.Campo == "Descrizione")
            .OrderBy(p => NumeroParte(p.Parte)).Select(p => p.Testo)));
        Assert.Equal(testoConMolteRighe, string.Concat(parti.Where(p => p.Campo == "Cronologia")
            .OrderBy(p => NumeroParte(p.Parte)).Select(p => p.Testo)));
        Assert.All(parti, p =>
        {
            Assert.True(p.Testo.Length <= ExcelExportTableWriter.LimiteCaratteriCella);
            Assert.True(p.Testo.Count(c => c == '\n') <= 253);
        });
    }

    [Fact]
    public void ExcelExport_TestDaOperatoreConPrefissoFormulaRestaTesto_NonEsegueFormule()
    {
        using var workbook = new XLWorkbook();
        var extended = "=WEBSERVICE(1)" + new string('x', 40_000);
        var writer = new ExcelExportTableWriter(workbook, "NC", ["ID", "Nota", "Dettaglio"]);
        writer.AggiungiRiga("=RISCHIO", ["=HYPERLINK(1)", "  +CMD(1)", extended]);
        writer.Completa();

        var main = workbook.Worksheet("NC");
        Assert.Equal("=HYPERLINK(1)", main.Cell(2, 1).GetString());
        Assert.Equal("  +CMD(1)", main.Cell(2, 2).GetString());
        Assert.False(main.Cell(2, 1).HasFormula);
        Assert.False(main.Cell(2, 2).HasFormula);
        Assert.True(main.Cell(2, 1).Style.IncludeQuotePrefix);
        var dettaglio = workbook.Worksheets.Single(w => w.Name.StartsWith("NC dettagli", StringComparison.Ordinal));
        Assert.Equal("=RISCHIO", dettaglio.Cell(2, 1).GetString());
        Assert.False(dettaglio.Cell(2, 1).HasFormula);
        Assert.False(dettaglio.Cell(2, 4).HasFormula);
        Assert.Equal(extended, string.Concat(dettaglio.RowsUsed().Skip(1).Select(row => row.Cell(4).GetString())));
    }

    [Fact]
    public void SuperatoLimiteRighe_RipeteIntestazioniSuNuoviFogliPrincipaliEDettagli()
    {
        using var workbook = new XLWorkbook();
        var writer = new ExcelExportTableWriter(workbook, "CR", ["ID", "Descrizione"], limiteRighe: 3);
        writer.AggiungiRiga("CR-1", ["CR-1", new string('a', 32_768)]);
        writer.AggiungiRiga("CR-2", ["CR-2", "ok"]);
        writer.AggiungiRiga("CR-3", ["CR-3", "ok"]);
        writer.AggiungiRiga("CR-4", ["CR-4", new string('b', 60_001)]);
        writer.Completa();

        Assert.Equal(new[] { "CR", "CR dettagli estesi", "CR (2)", "CR dettagli estesi (2)", "CR dettagli estesi (3)" }, workbook.Worksheets.Select(w => w.Name));
        Assert.Equal("ID", workbook.Worksheet("CR").Cell(1, 1).GetString());
        Assert.Equal("ID", workbook.Worksheet("CR (2)").Cell(1, 1).GetString());
        Assert.Equal("CR-3", workbook.Worksheet("CR (2)").Cell(2, 1).GetString());
        var dettaglio = workbook.Worksheet("CR dettagli estesi");
        Assert.Equal("ID riferimento", dettaglio.Cell(1, 1).GetString());
        Assert.Equal("CR-1", dettaglio.Cell(2, 1).GetString());
        var dettaglioSuccessivo = workbook.Worksheet("CR dettagli estesi (2)");
        Assert.Equal("ID riferimento", dettaglioSuccessivo.Cell(1, 1).GetString());
        Assert.Contains(dettaglioSuccessivo.Rows(2, 3), row => row.Cell(1).GetString() == "CR-4");
        var ultimoDettaglio = workbook.Worksheet("CR dettagli estesi (3)");
        Assert.Equal("ID riferimento", ultimoDettaglio.Cell(1, 1).GetString());
        Assert.Equal("CR-4", ultimoDettaglio.Cell(2, 1).GetString());
    }

    [Fact]
    public void MarkerDettaglio_IndicaPrimoEUltimoFoglioConNomeLungoERollover()
    {
        using var workbook = new XLWorkbook();
        var writer = new ExcelExportTableWriter(workbook, "Tabella NC con nome estremamente lungo", ["ID", "Descrizione"], limiteRighe: 2);
        writer.AggiungiRiga("NC-1", ["NC-1", new string('x', 65_000)]);
        writer.Completa();
        var fogli = workbook.Worksheets.Skip(1).ToList();
        Assert.True(fogli.Count >= 3);
        var marker = workbook.Worksheets.First().Cell(2, 2).GetString();
        Assert.Contains(fogli.First().Name, marker);
        Assert.Contains(fogli.Last().Name, marker);
        Assert.Equal(new string('x', 65_000), string.Concat(fogli.Select(f => f.Cell(2, 4).GetString())));
    }

    [Fact]
    public void MarkerDettaglio_IndicaNomeRealeDopoCollisione()
    {
        using var workbook = new XLWorkbook();
        workbook.Worksheets.Add("NC dettagli estesi");
        var writer = new ExcelExportTableWriter(workbook, "NC", ["ID", "Descrizione"]);

        writer.AggiungiRiga("NC-1", ["NC-1", new string('x', 32_768)]);
        writer.Completa();

        var principale = workbook.Worksheet("NC");
        Assert.Contains("Testo esteso", principale.Cell(2, 2).GetString());
        var nomeDettagliReale = "NC dettagli estesi (2)";
        Assert.Contains(workbook.Worksheets, w => w.Name == nomeDettagliReale);
        Assert.Contains(nomeDettagliReale, principale.Cell(2, 2).GetString());
        Assert.True(nomeDettagliReale.Length <= 31);
    }

    [Fact]
    public void MarkerDettaglio_IndicaNomeTroncatoEffettivoDelFoglio()
    {
        using var workbook = new XLWorkbook();
        var nomeBase = "Tabella NC con nome estremamente lungo";
        var writer = new ExcelExportTableWriter(workbook, nomeBase, ["ID", "Descrizione"]);

        writer.AggiungiRiga("NC-1", ["NC-1", new string('x', 32_768)]);
        writer.Completa();

        var principale = workbook.Worksheet(workbook.Worksheets.First().Name);
        var marker = principale.Cell(2, 2).GetString();
        var foglioDettagli = workbook.Worksheets.Single(w => w.Name != principale.Name);
        Assert.Contains(foglioDettagli.Name, marker);
        Assert.True(foglioDettagli.Name.Length <= 31);
    }

    private static int NumeroParte(string parte) => int.Parse(parte.Split('/')[0], System.Globalization.CultureInfo.InvariantCulture);
}
