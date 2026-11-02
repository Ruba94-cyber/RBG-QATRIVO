using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionCriticalityServiceTests
{
    private static PolarionTicketSnapshot Ticket(string id, string priority, string severity,
        string status = "assigned", string? resolution = null, string type = "bug") => new()
        {
            ProjectId = "TICKET_Tickets", Id = id, TicketType = type,
            Status = status, Resolution = resolution, Priority = priority, Severity = severity
        };

    [Fact]
    public void MultipleRelevantTicketsUseHighestCriticalityEvenWhenDone()
    {
        var value = PolarionCriticalityService.Evaluate(new[]
        {
            Ticket("TICKET-1", "50.0", "critical", "closed", "done"),
            Ticket("TICKET-2", "25.0", "normal")
        }, false, false);
        Assert.NotNull(value);
        Assert.Equal("P1", value.Priority);
        Assert.Equal("Critical", value.Severity);
    }

    [Fact]
    public void DuplicateInvalidAndReclassifiedCannotInflateResult()
    {
        var value = PolarionCriticalityService.Evaluate(new[]
        {
            Ticket("TICKET-1", "50.0", "critical", "closed", "duplicate"),
            Ticket("TICKET-2", "50.0", "critical", "closed", "invalid"),
            Ticket("TICKET-3", "50.0", "critical", type: "new_request"),
            Ticket("TICKET-4", "20.0", "minor")
        }, false, false);
        Assert.NotNull(value);
        Assert.Equal("P4", value.Priority);
        Assert.Equal("Minor", value.Severity);
        Assert.Equal(new[] { "TICKET-4" }, value.ContributingIds);
    }

    [Fact]
    public void CrUsesNewRequestAndDoesNotRewriteClosedDitmHistory()
    {
        var tickets = new[] { Ticket("TICKET-1", "33.0", "major", type: "new_request") };
        Assert.Equal("P2", PolarionCriticalityService.Evaluate(tickets, true, false)!.Priority);
        Assert.Null(PolarionCriticalityService.Evaluate(tickets, true, true));
    }

    [Theory]
    [InlineData("16.0", 1)]
    [InlineData("P5", 1)]
    [InlineData("25.0", 3)]
    [InlineData("P1", 5)]
    [InlineData("33.0", 4)]
    public void PriorityOptionMapping(string value, int rank) =>
        Assert.Equal(rank, PolarionCriticalityService.ParsePriority(value));
}
