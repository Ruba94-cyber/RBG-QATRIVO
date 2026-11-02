using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionTestHandoffStatusServiceTests
{
    private static PolarionCreationReceiptStore.Receipt BrowserReceipt(
        TestCase test, string source, bool isCr, string? ticket = null) => new()
    {
        OperationId = Guid.NewGuid(),
        SourceId = source,
        SourceUid = Guid.NewGuid(),
        OriginTestUid = test.Uid,
        OriginTestId = test.Id,
        IsCr = isCr,
        StartedUtc = DateTimeOffset.UtcNow,
        BrowserCorrelationMarker = "TestLoomREF" + new string('A', 32),
        CreatedTicketId = ticket
    };

    [Fact]
    public void PendingBrowserHandoffIsNeverTreatedAsLinkedTicket()
    {
        var test = new TestCase { Id = "TEST-100" };
        var r = BrowserReceipt(test, "NC-100", false);
        var snapshot = PolarionTestHandoffStatusService.Summarize(
            test, new[] { r }, Array.Empty<TicketRif>());

        Assert.Equal(1, snapshot.PendingCount);
        Assert.Equal(0, snapshot.LinkedCount);
        Assert.Equal("NC-100", Assert.Single(snapshot.PendingHandoffs).SourceId);
        Assert.Null(snapshot.PendingHandoffs[0].VerifiedTicketId);
    }

    [Fact]
    public void VerifiedButUnlinkedReceiptRemainsPendingUntilPersistedTestHasLink()
    {
        var test = new TestCase { Id = "TEST-101" };
        var r = BrowserReceipt(test, "CR-1", true, ticket: "TICKET-17");
        var first = PolarionTestHandoffStatusService.Summarize(
            test, new[] { r }, Array.Empty<TicketRif>());

        Assert.Equal(1, first.PendingCount);
        Assert.Equal(0, first.LinkedCount);
        Assert.True(first.PendingHandoffs[0].IsCr);
        Assert.Equal("TICKET-17", first.PendingHandoffs[0].VerifiedTicketId);

        var completed = PolarionTestHandoffStatusService.Summarize(
            test, Array.Empty<PolarionCreationReceiptStore.Receipt>(),
            new[] { new TicketRif { Riferimento = "TICKET-17" } });
        Assert.Equal(0, completed.PendingCount);
        Assert.Equal(1, completed.LinkedCount);
    }

    [Fact]
    public void OtherTestsAndLegacySourceOnlyReceiptsNeverAppearInThisTestsPendingList()
    {
        var test = new TestCase { Id = "TEST-300" };
        var other = new TestCase { Id = "TEST-300" }; // same readable name, different UID
        var wrongUid = BrowserReceipt(other, "NC-1", false);
        var wrongId = BrowserReceipt(test, "NC-2", false);
        wrongId.OriginTestId = "TEST-301";
        var legacy = BrowserReceipt(test, "NC-3", false);
        legacy.OriginTestUid = null;
        legacy.OriginTestId = null;
        var notBrowser = BrowserReceipt(test, "NC-4", false);
        notBrowser.BrowserCorrelationMarker = null;
        var intended = BrowserReceipt(test, "CR-12", true);

        var snapshot = PolarionTestHandoffStatusService.Summarize(test,
            new[] { wrongUid, wrongId, legacy, notBrowser, intended }, Array.Empty<TicketRif>());
        Assert.Equal(1, snapshot.PendingCount);
        Assert.Equal("CR-12", Assert.Single(snapshot.PendingHandoffs).SourceId);
    }

    [Fact]
    public void OnlyActiveOperatorReceiptsAreShownWhenOperatorFilterIsProvided()
    {
        var test = new TestCase { Id = "TEST-4" };
        var mine = BrowserReceipt(test, "NC-4", false);
        mine.OperatorAtHandoff = "operatorA";
        var coworker = BrowserReceipt(test, "CR-9", true);
        coworker.OperatorAtHandoff = "operatorB";

        var snapshot = PolarionTestHandoffStatusService.Summarize(
            test, new[] { mine, coworker }, Array.Empty<TicketRif>(), "operatorA");
        Assert.Equal(1, snapshot.PendingCount);
        Assert.Equal("NC-4", Assert.Single(snapshot.PendingHandoffs).SourceId);
        Assert.Equal(2, PolarionTestHandoffStatusService.Summarize(
            test, new[] { mine, coworker }, Array.Empty<TicketRif>()).PendingCount);
    }

    [Fact]
    public void LinkedTestReferencesAreDeduplicatedAndDoNotDependOnReceiptCount()
    {
        var test = new TestCase { Id = "TEST-9" };
        var tickets = new[]
        {
            new TicketRif { Riferimento = " TICKET-3 " },
            new TicketRif { Riferimento = "ticket-3" },
            new TicketRif { Riferimento = "TICKET-8" },
            new TicketRif { Riferimento = " " }
        };
        var summary = PolarionTestHandoffStatusService.Summarize(test,
            Array.Empty<PolarionCreationReceiptStore.Receipt>(), tickets);
        Assert.Equal(0, summary.PendingCount);
        Assert.Equal(2, summary.LinkedCount);
        Assert.Equal(new[] { "TICKET-3", "TICKET-8" }, summary.LinkedTicketIds);
    }
}
