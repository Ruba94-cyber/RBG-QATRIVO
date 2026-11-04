using ClosedXML.Excel;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionExcelReportServiceTests
{
    [Fact]
    public void WorkbookMarksUnavailableHistoryAndDoesNotExportFabricatedZeroCounts()
    {
        var rows = new[] { new PolarionReportService.Row
        {
            TicketId = "TICKET-1", Status = "closed", HasCache = true, HistoryUnavailable = true
        } };
        using var stream = new MemoryStream(PolarionExcelReportService.Generate(rows, DateTimeOffset.UtcNow));
        using var workbook = new XLWorkbook(stream);
        var summary = workbook.Worksheet("Riepilogo TICKET");
        Assert.Equal("Cronologie non disponibili", summary.Cell("A17").GetString());
        Assert.Equal(1, summary.Cell("B17").GetValue<int>());
        var details = workbook.Worksheet("Ticket TICKET");
        Assert.True(details.Cell("P2").IsEmpty());
        Assert.True(details.Cell("Q2").IsEmpty());
        Assert.True(details.Cell("W2").IsEmpty());
        Assert.True(details.Cell("X2").IsEmpty());
        Assert.True(details.Cell("Y2").IsEmpty());
        Assert.Contains(new[] { "Cronologia non disponibile", "History unavailable", "Historique indisponible" }, x => details.Cell("T2").GetString().Contains(x, StringComparison.Ordinal));
    }

    [Fact]
    public void WorkbookPreservesExactVersusObservedDatesAndNeutralizesFormulas()
    {
        var rows = new[] { new PolarionReportService.Row
        {
            TicketId = "TICKET-123", Title = "=WEBSERVICE(1)", Status = "closed",
            Records = "NC NC-1", CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
            FirstClosedObservedAt = DateTimeOffset.UtcNow
        }};
        var bytes = PolarionExcelReportService.Generate(rows, DateTimeOffset.UtcNow);
        using var xlsx = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(3, xlsx.Worksheets.Count);
        Assert.NotNull(xlsx.Worksheet("Timeline TICKET"));
        Assert.Equal(1, xlsx.Worksheet("Riepilogo TICKET").Cell("B8").GetValue<int>());
        Assert.Contains("prima osservazione", xlsx.Worksheet("Riepilogo TICKET").Cell("B4").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("TICKET-123", xlsx.Worksheet("Ticket TICKET").Cell("A2").GetString());
        var cell = xlsx.Worksheet("Ticket TICKET").Cell("B2");
        Assert.Equal("=WEBSERVICE(1)", cell.GetString());
        Assert.Equal(XLDataType.Text, cell.DataType);
        Assert.False(cell.HasFormula);
        Assert.True(cell.Style.IncludeQuotePrefix);
    }

    [Fact]
    public void WorkbookExportsEveryObservedEventAndMarksBaselineAsNonHistorical()
    {
        var at = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
        var row = new PolarionReportService.Row
        {
            TicketId = "TICKET-44", Records = "CR CR-3",
            Timeline = [
                new PolarionReportService.ObservedTransition(at, "assigned", "", true),
                new PolarionReportService.ObservedTransition(at.AddMinutes(15), "testing", "", false),
                new PolarionReportService.ObservedTransition(at.AddMinutes(30), "closed", "done", false),
                new PolarionReportService.ObservedTransition(at.AddMinutes(45), "assigned", "", false)
            ]
        };
        using var xlsx = new XLWorkbook(new MemoryStream(
            PolarionExcelReportService.Generate([row], at)));
        var sheet = xlsx.Worksheet("Timeline TICKET");
        Assert.Equal("TICKET-44", sheet.Cell("A2").GetString());
        Assert.Equal("Baseline TestLoom", sheet.Cell("F2").GetString());
        Assert.Equal("testing", sheet.Cell("D3").GetString());
        Assert.Equal("closed", sheet.Cell("D4").GetString());
        Assert.Equal("assigned", sheet.Cell("D5").GetString());
        Assert.Contains("NON data storica", sheet.Cell("G4").GetString());
    }

    [Fact]
    public void WorkbookEsta_ConTitoloOltreLimiteCella_NonPerdeIlTestoEUsaFogliDettaglio()
    {
        var originalTitle = "=WEBSERVICE(1)" + new string('q', 40_000);
        var row = new PolarionReportService.Row { TicketId = "TICKET-88", Title = originalTitle };
        using var xlsx = new XLWorkbook(new MemoryStream(
            PolarionExcelReportService.Generate([row], DateTimeOffset.UtcNow)));

        Assert.Contains("Testo esteso", xlsx.Worksheet("Ticket TICKET").Cell("B2").GetString());
        var dettaglio = xlsx.Worksheets.Where(w =>
            w.Name.StartsWith("Ticket TICKET dettagli", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(dettaglio);
        var parts = dettaglio.SelectMany(w => w.RowsUsed().Skip(1)).Where(r =>
            r.Cell(2).GetString() == "Titolo")
            .Select(r => (Parte: int.Parse(r.Cell(3).GetString().Split('/')[0]),
                Testo: r.Cell(4).GetString()))
            .OrderBy(x => x.Parte)
            .Select(x => x.Testo);
        Assert.Equal(originalTitle, string.Concat(parts));
        Assert.False(dettaglio[0].Cell(2, 4).HasFormula);
    }

    [Fact]
    public void WorkbookEsta_CancellazionePrimaDelSalvataggio_NonCreaExcelIncompleto()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            PolarionExcelReportService.Generate([], DateTimeOffset.UtcNow, cts.Token));
    }

    [Fact]
    public void SaveToFile_ExportAtomico_NonSostituisceUnFileSeAnnullato()
    {
        var file = Path.Combine(Path.GetTempPath(), "TestLoom_TICKET_" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            File.WriteAllText(file, "contenuto precedente");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                PolarionExcelReportService.SaveToFile([], DateTimeOffset.UtcNow, file, canceled.Token));
            Assert.Equal("contenuto precedente", File.ReadAllText(file));

            PolarionExcelReportService.SaveToFile(
                [new PolarionReportService.Row { TicketId = "TICKET-42", Title = "Prova" }],
                DateTimeOffset.UtcNow, file);
            using var workbook = new XLWorkbook(file);
            Assert.Equal("TICKET-42", workbook.Worksheet("Ticket TICKET").Cell("A2").GetString());
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!,
                "." + Path.GetFileName(file) + ".*.tmp.xlsx"));
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void EmptyResultsStillExportAnExcelWorkbook()
    {
        using var xlsx = new XLWorkbook(new MemoryStream(
            PolarionExcelReportService.Generate([], DateTimeOffset.UtcNow)));
        Assert.Equal(0, xlsx.Worksheet("Riepilogo TICKET").Cell("B8").GetValue<int>());
    }
}
