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

    [Fact]
    public void StatusShowsCommentCountAndTooltipExplainsTheLatestComment()
    {
        var row = new PolarionReportService.Row
        {
            TicketId = "TICKET-135",
            Title = "Guasto validation",
            Status = "in_progress",
            Resolution = "",
            HasCache = true,
            CommentsCount = 3,
            LatestCommentAuthor = "engineer",
            LatestCommentPreview = "Waiting for new vehicle validation"
        };

        var status = PolarionGridLifecycleConverter.FormatRows([row], "status");
        Assert.Contains("in_progress", status);
        Assert.Contains("3", status);
        var tooltip = PolarionGridLifecycleConverter.FormatRows([row], "tooltip");
        Assert.Contains("TICKET-135", tooltip);
        Assert.Contains("Guasto validation", tooltip);
        Assert.Contains("engineer", tooltip);
        Assert.Contains("Waiting for new vehicle validation", tooltip);
        Assert.Contains("3", tooltip);
    }

    [Fact]
    public void MultiticketTooltipKeepsEachStatusAndItsOwnCommentsSeparate()
    {
        var first = new PolarionReportService.Row
        {
            TicketId = "TICKET-1", Status = "open",
            CommentsCount = 2, LatestCommentPreview = "Investigating"
        };
        var second = new PolarionReportService.Row
        {
            TicketId = "TICKET-2", Status = "closed",
            CommentsCount = 1, LatestCommentPreview = "Resolved"
        };
        var tooltip = PolarionGridLifecycleConverter.FormatRows([first, second], "tooltip");
        Assert.Contains("TICKET-1", tooltip);
        Assert.Contains("TICKET-2", tooltip);
        Assert.Contains("Investigating", tooltip);
        Assert.Contains("Resolved", tooltip);
        Assert.Contains("\n\n", tooltip);
        Assert.Contains("3", PolarionGridLifecycleConverter.FormatRows([first, second], "status"));
    }

    [Fact]
    public void TicketSingoloMostraAncheLaRisoluzioneSenzaConfonderlaConLoStato()
    {
        var risolto = new PolarionReportService.Row
        {
            TicketId = "TICKET-14", Status = "closed", Resolution = "done",
            HasCache = true
        };
        var label = PolarionGridLifecycleConverter.FormatRows([risolto], "status");
        Assert.Contains("closed", label);
        Assert.Contains("done", label);
        var identico = new PolarionReportService.Row
        {
            TicketId = "TICKET-15", Status = "closed", Resolution = "closed"
        };
        Assert.Equal("closed", PolarionGridLifecycleConverter.FormatRows([identico], "status"));
    }

    [Fact]
    public void CacheESTAOltre24Ore_VieneIndicataSenzaFingereUnoStatoLive()
    {
        var now = new DateTimeOffset(2026, 11, 6, 16, 0, 0, TimeSpan.Zero);
        var stale = new PolarionReportService.Row
        {
            TicketId = "TICKET-11", Status = "assigned", HasCache = true,
            LastFetchedAt = now.AddHours(-30)
        };
        var recent = new PolarionReportService.Row
        {
            TicketId = "TICKET-12", Status = "assigned", HasCache = true,
            LastFetchedAt = now.AddMinutes(-10)
        };
        Assert.Contains("24h", PolarionGridLifecycleConverter.FormatRows([stale], "status", now));
        Assert.DoesNotContain("24h", PolarionGridLifecycleConverter.FormatRows([recent], "status", now));
    }

    [Fact]
    public void AnteprimaUltimoCommento_MantieneIlTicketOrigineSeMultiplo()
    {
        var rows = new[]
        {
            new PolarionReportService.Row { TicketId = "TICKET-11", Status = "assigned" },
            new PolarionReportService.Row {
                TicketId = "TICKET-12", Status = "testing", CommentsCount = 1,
                LatestCommentPreview = new string('x', 150)
            }
        };
        var preview = PolarionGridLifecycleConverter.FormatRows(rows, "commentCompact");
        Assert.Contains("TICKET-12", preview);
        Assert.Contains("💬", preview);
        Assert.True(preview.Length < 120);
        Assert.Equal("", PolarionGridLifecycleConverter.FormatRows(
            [new PolarionReportService.Row { TicketId = "TICKET-13" }], "commentCompact"));
    }

    [Theory]
    [InlineData("closed", "#FF2E9E5B")]
    [InlineData("testing", "#FF3B82F6")]
    [InlineData("open", "#FFD97706")]
    [InlineData("", "#FF8A8F98")]
    public void StatusBrushEvidenziaLoStatoEsta(string stato, string atteso)
    {
        var row = new PolarionReportService.Row { TicketId = "TICKET-1", Status = stato };
        var brush = Assert.IsType<Avalonia.Media.SolidColorBrush>(PolarionGridLifecycleConverter.StatusBrush([row]));
        Assert.Equal(atteso, brush.Color.ToString().ToUpperInvariant());
    }

    [Fact]
    public void StatusBrushConPiuTicketPrevaleIlMenoAvanzato()
    {
        var chiuso = new PolarionReportService.Row { TicketId = "TICKET-1", Status = "closed" };
        var aperto = new PolarionReportService.Row { TicketId = "TICKET-2", Status = "open" };
        var brush = Assert.IsType<Avalonia.Media.SolidColorBrush>(PolarionGridLifecycleConverter.StatusBrush([chiuso, aperto]));
        Assert.Equal("#FFD97706", brush.Color.ToString().ToUpperInvariant());
    }
}
