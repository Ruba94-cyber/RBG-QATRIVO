using TestLoom.App;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionGridLifecycleConverterTests
{
    [Fact]
    public void ShowsRealPolarionCreationButExplicitlyObservedTestingClosure()
    {
        var row = new PolarionReportService.Row
        {
            TicketId = "TICKET-123", Status = "closed",
            CreatedAt = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero),
            FirstTestingObservedAt = new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero),
            FirstClosedObservedAt = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero)
        };
        Assert.Equal("closed", PolarionGridLifecycleConverter.FormatRows([row], "status"));
        Assert.DoesNotContain("Rilevato", PolarionGridLifecycleConverter.FormatRows([row], "created"));
        Assert.Contains("Rilevato", PolarionGridLifecycleConverter.FormatRows([row], "testing"));
        Assert.Contains("Rilevato", PolarionGridLifecycleConverter.FormatRows([row], "closed"));
    }

    [Fact]
    public void StatoSconosciutoInCache_NonEConfusoConTicketMaiSincronizzato()
    {
        var cached = new PolarionReportService.Row
        {
            TicketId = "TICKET-3", HasCache = true, Status = ""
        };
        var missing = new PolarionReportService.Row
        {
            TicketId = "TICKET-4", HasCache = false, Status = ""
        };
        Assert.Contains(new[] { "Stato sconosciuto", "Unknown status", "Statut inconnu" },
            x => PolarionGridLifecycleConverter.FormatRows([cached], "status").Contains(x));
        Assert.Contains(new[] { "Non sincronizzato", "Not synchronized", "Non synchronisé" },
            x => PolarionGridLifecycleConverter.FormatRows([missing], "status").Contains(x));
    }

    [Fact]
    public void MultipleLinksAreNotFalselyCombinedIntoSingleClosureDate()
    {
        var a = new PolarionReportService.Row { TicketId = "TICKET-1", Status = "assigned" };
        var b = new PolarionReportService.Row { TicketId = "TICKET-2", Status = "closed" };
        var rows = new[] { a, b };
        Assert.Contains("2 TICKET", PolarionGridLifecycleConverter.FormatRows(rows, "status"));
        Assert.Contains("(+1)", PolarionGridLifecycleConverter.FormatRows(rows, "closed"));
        Assert.Contains("TICKET-1", PolarionGridLifecycleConverter.FormatRows(rows, "tooltip"));
        Assert.Contains("TICKET-2", PolarionGridLifecycleConverter.FormatRows(rows, "tooltip"));
    }

    [Fact]
    public void BaselineClosedNeverMasqueradesAsHistoricClosureDate()
    {
        var row = new PolarionReportService.Row { TicketId = "TICKET-7",
            Status = "closed", AlreadyClosedAtBaseline = true };
        Assert.Contains("Già chiuso", PolarionGridLifecycleConverter.FormatRows([row], "closed"));
    }
}
