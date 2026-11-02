using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionLocalReadTrackerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PolarionRead_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_PolarionReadLocal_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FirstObservationIsBaselineAndLaterUpdatesAreUnreadPerOperator()
    {
        // Avoid changing WorkspaceLocalPaths overrides globally: tests on the same assembly
        // already isolate %LOCALAPPDATA% via TestLocalAppDataFixture.
        var paths = new DataPaths(_root);
        var alice = new PolarionLocalReadTracker(paths, "UnitAlice-" + Guid.NewGuid().ToString("N"));
        var bob = new PolarionLocalReadTracker(paths, "UnitBob-" + Guid.NewGuid().ToString("N"));
        var first = Snapshot("assigned", "first");
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, false), alice.Observe(first));
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, false), bob.Observe(first));
        var second = Snapshot("testing", "first", "second");
        Assert.Equal(new PolarionLocalReadTracker.Unread(1, true), alice.Observe(second));
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, false), bob.GetUnread("TICKET-123"));
        alice.MarkRead("TICKET-123", comments: true, fields: false);
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, true), alice.GetUnread("TICKET-123"));
        Assert.Equal(new PolarionLocalReadTracker.Unread(1, true), bob.Observe(second));
        alice.MarkRead("TICKET-123", comments: false, fields: true);
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, false), alice.GetUnread("TICKET-123"));
        Assert.Equal(new PolarionLocalReadTracker.Unread(1, true), bob.GetUnread("TICKET-123"));
    }

    [Fact]
    public void LateResponseFromStalePolarionReplicaCannotGenerateFakeReopen()
    {
        var tracker = new PolarionLocalReadTracker(new DataPaths(_root),
            "UnitRevision-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        var old = new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123",
            Status = "assigned", Updated = now.AddMinutes(-10),
            RetrievedAtUtc = now.AddMinutes(-8)
        };
        var closed = new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123",
            Status = "closed", Updated = now.AddMinutes(-5),
            RetrievedAtUtc = now.AddMinutes(-4)
        };
        var lagging = new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123",
            Status = "assigned", Updated = now.AddMinutes(-10),
            RetrievedAtUtc = now.AddMinutes(-1)
        };
        tracker.Observe(old);
        tracker.Observe(closed);
        tracker.MarkRead("TICKET-123", comments: true, fields: true);
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, false),
            tracker.Observe(lagging));
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, false),
            tracker.GetUnread("TICKET-123"));
    }

    [Fact]
    public void ConflictingStateAtSamePolarionRevisionDoesNotModifyReadCursor()
    {
        var tracker = new PolarionLocalReadTracker(new DataPaths(_root),
            "UnitSameRevision-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        tracker.Observe(new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123",
            Status = "assigned", Updated = now.AddMinutes(-3),
            RetrievedAtUtc = now.AddMinutes(-2)
        });
        Assert.Throws<InvalidDataException>(() => tracker.Observe(new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123",
            Status = "closed", Updated = now.AddMinutes(-3),
            RetrievedAtUtc = now.AddMinutes(-1)
        }));
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, false),
            tracker.GetUnread("TICKET-123"));
    }

    [Fact]
    public void OlderSharedSnapshotCannotGenerateFalseUnreadStateReversal()
    {
        var tracker = new PolarionLocalReadTracker(new DataPaths(_root),
            "UnitStale-" + Guid.NewGuid().ToString("N"));
        var first = new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = "assigned",
            RetrievedAtUtc = DateTimeOffset.Parse("2026-10-29T12:00:00Z")
        };
        var current = new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = "testing",
            RetrievedAtUtc = DateTimeOffset.Parse("2026-10-29T12:02:00Z")
        };
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, false), tracker.Observe(first));
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, true), tracker.Observe(current));
        tracker.MarkRead("TICKET-123", comments: true, fields: true);
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, false), tracker.Observe(first));
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, false), tracker.Observe(current));
    }

    [Fact]
    public void SilentScopesBaselineWithoutNotifyingAndReopenOnlyIgnoresComments()
    {
        var tracker = new PolarionLocalReadTracker(new DataPaths(_root),
            "UnitScope-" + Guid.NewGuid().ToString("N"));
        var first = Snapshot("closed", "one");
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, false),
            tracker.Observe(first, PolarionNotificationScopeService.Mode.Silent));
        var more = Snapshot("closed", "one", "two");
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, false),
            tracker.Observe(more, PolarionNotificationScopeService.Mode.ReopenOnly));
        var reopened = Snapshot("assigned", "one", "two", "three");
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, true),
            tracker.Observe(reopened, PolarionNotificationScopeService.Mode.ReopenOnly));
        tracker.MarkRead("TICKET-123", comments: true, fields: true);
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, false),
            tracker.Observe(Snapshot("assigned", "one", "two", "three"),
                PolarionNotificationScopeService.Mode.Passive));
    }

    [Fact]
    public void ClosingDitmSubscriptionClearsPriorOrdinaryFieldUnread()
    {
        var tracker = new PolarionLocalReadTracker(new DataPaths(_root),
            "UnitClosed-" + Guid.NewGuid().ToString("N"));
        tracker.Observe(Snapshot("assigned"));
        tracker.Observe(Snapshot("testing"));
        Assert.True(tracker.GetUnread("TICKET-123").Fields);
        Assert.Equal(new PolarionLocalReadTracker.Unread(0, false),
            tracker.Observe(Snapshot("closed"), PolarionNotificationScopeService.Mode.ReopenOnly));
    }

    [Fact]
    public void RepeatedSnapshotNeverCreatesDuplicateUnreadComments()
    {
        var tracker = new PolarionLocalReadTracker(new DataPaths(_root), "UnitRepeat-" + Guid.NewGuid().ToString("N"));
        tracker.Observe(Snapshot("assigned", "one"));
        tracker.Observe(Snapshot("testing", "one", "two"));
        Assert.Equal(new PolarionLocalReadTracker.Unread(1, true),
            tracker.Observe(Snapshot("testing", "one", "two")));
    }

    private static PolarionTicketSnapshot Snapshot(string status, params string[] comments) =>
        new()
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = status,
            Comments = comments.Select(id => new PolarionCommentSnapshot { Id = "TICKET_Tickets/TICKET-123/" + id }).ToList()
        };

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
