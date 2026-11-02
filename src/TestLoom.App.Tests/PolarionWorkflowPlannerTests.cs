using TestLoom.App.Services;
using Action = TestLoom.App.Services.PolarionWorkflowPlanner.Action;
using static TestLoom.App.Services.PolarionWorkflowPlanner;

namespace TestLoom.App.Tests;

public sealed class PolarionWorkflowPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 13, 0, 0, TimeSpan.Zero);
    private static Link Linked(string id, string status = "closed", string resolution = "done",
        bool blocking = true, string type = "bug", string? duplicateOf = null, int ageMinutes = 0) =>
        new(id, type, status, resolution, Now.AddMinutes(-ageMinutes), blocking, duplicateOf);

    private static Result Plan(EntityKind kind, params Link[] links) =>
        Evaluate(kind, links, Now, baselineApproved: true, automationPaused: false,
            crImplemented: true, maxSnapshotAge: TimeSpan.FromMinutes(30));

    [Fact]
    public void NcAdvancesOnlyWhenEveryBlockingTicketDone()
    {
        Assert.Equal(Action.ReadyForRetest, Plan(EntityKind.Nc, Linked("TICKET-1"), Linked("TICKET-2")).SuggestedAction);
        Assert.Equal(Action.None, Plan(EntityKind.Nc, Linked("TICKET-1"), Linked("TICKET-2", "assigned", "")).SuggestedAction);
    }

    [Fact]
    public void MasterExceptionMayExcludeOneTicketFromAutomatedBlockers()
    {
        Assert.Equal(Action.ReadyForRetest, Plan(EntityKind.Nc,
            Linked("TICKET-1"), Linked("TICKET-2", "assigned", "", blocking: false)).SuggestedAction);
    }

    [Fact]
    public void DuplicateFollowsOnlyExplicitVerifiedOriginal()
    {
        Assert.Equal(Action.None, Plan(EntityKind.Nc,
            Linked("TICKET-1", resolution: "duplicate", duplicateOf: "TICKET-2"),
            Linked("TICKET-2", "assigned", "")).SuggestedAction);
        Assert.Equal(Action.RequiresMasterReview, Plan(EntityKind.Nc,
            Linked("TICKET-1", resolution: "duplicate")).SuggestedAction);
    }

    [Fact]
    public void TwoDifferentDuplicateChainsMayShareOneOriginal()
    {
        var result = Plan(EntityKind.Nc,
            Linked("TICKET-1", resolution: "duplicate", duplicateOf: "TICKET-2"),
            Linked("TICKET-2", resolution: "duplicate", duplicateOf: "TICKET-3"),
            Linked("TICKET-3"),
            Linked("TICKET-4", resolution: "duplicate", duplicateOf: "TICKET-2"));
        Assert.Equal(Action.ReadyForRetest, result.SuggestedAction);
    }

    [Fact]
    public void InvalidRequiresMasterAndSuspendedMayPutNcOnHold()
    {
        Assert.Equal(Action.RequiresMasterReview, Plan(EntityKind.Nc,
            Linked("TICKET-1", resolution: "invalid")).SuggestedAction);
        Assert.Equal(Action.PutNcOnHold, Plan(EntityKind.Nc,
            Linked("TICKET-1", resolution: "suspended")).SuggestedAction);
        Assert.Equal(Action.PutNcOnHold, Plan(EntityKind.Nc,
            Linked("TICKET-1", resolution: "suspended"), Linked("TICKET-2")).SuggestedAction);
        Assert.Equal(Action.None, Plan(EntityKind.Cr,
            Linked("TICKET-1", resolution: "suspended", type: "new_request")).SuggestedAction);
    }

    [Fact]
    public void ReclassifiedNcDoesNotAdvanceAutomatically()
    {
        Assert.Equal(Action.RequiresMasterReview, Plan(EntityKind.Nc,
            Linked("TICKET-1", type: "new_request")).SuggestedAction);
    }

    [Fact]
    public void CrRequiresImplementationToAdvance()
    {
        var link = Linked("TICKET-1", type: "new_request");
        Assert.Equal(Action.ReadyForValidation, Plan(EntityKind.Cr, link).SuggestedAction);
        Assert.Equal(Action.None, Evaluate(EntityKind.Cr, new[] { link }, Now, true, false, false,
            TimeSpan.FromMinutes(30)).SuggestedAction);
    }

    [Fact]
    public void DuplicateAssociationDoesNotThrowButContradictionsRequireReview()
    {
        Assert.Equal(Action.ReadyForRetest, Plan(EntityKind.Nc,
            Linked("TICKET-1"), Linked("TICKET-1")).SuggestedAction);
        Assert.Equal(Action.RequiresMasterReview, Plan(EntityKind.Nc,
            Linked("TICKET-1"), Linked("TICKET-1", "assigned", "")).SuggestedAction);
    }

    [Fact]
    public void StaleDuplicateCannotTriggerRetestEvenIfOriginalIsFresh()
    {
        var result = Plan(EntityKind.Nc,
            Linked("TICKET-1", resolution: "duplicate", duplicateOf: "TICKET-2", ageMinutes: 60),
            Linked("TICKET-2"));
        Assert.Equal(Action.None, result.SuggestedAction);
    }

    [Fact]
    public void StaleDataAndInitialBaselineNeverCauseAutomaticTransition()
    {
        Assert.Equal(Action.None, Plan(EntityKind.Nc, Linked("TICKET-1", ageMinutes: 120)).SuggestedAction);
        Assert.Equal(Action.None, Evaluate(EntityKind.Nc, new[] { Linked("TICKET-1") }, Now,
            baselineApproved: false, automationPaused: false, crImplemented: false,
            maxSnapshotAge: TimeSpan.FromMinutes(30)).SuggestedAction);
        Assert.Equal(Action.None, Evaluate(EntityKind.Nc, new[] { Linked("TICKET-1") }, Now,
            baselineApproved: true, automationPaused: true, crImplemented: false,
            maxSnapshotAge: TimeSpan.FromMinutes(30)).SuggestedAction);
    }
}
