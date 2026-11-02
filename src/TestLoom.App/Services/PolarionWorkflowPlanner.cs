using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Pure, non-mutating decision engine. Callers must validate TestLoom permissions, current domain
/// revisions, test prerequisites and the initial baseline before applying any proposed change.
/// This service never persists NC/CR state and never sends Ticketing writes.
/// </summary>
public static class PolarionWorkflowPlanner
{
    public enum EntityKind { Nc, Cr }
    public enum Action { None, ReadyForRetest, ReadyForValidation, PutNcOnHold, RequiresMasterReview }

    public sealed record Link(
        string TicketId,
        string? Type,
        string? Status,
        string? Resolution,
        DateTimeOffset VerifiedAtUtc,
        bool Blocking = true,
        string? DuplicateOf = null);

    public sealed record Result(Action SuggestedAction, string Reason);

    public static Result Evaluate(
        EntityKind kind,
        IReadOnlyCollection<Link> links,
        DateTimeOffset nowUtc,
        bool baselineApproved,
        bool automationPaused,
        bool crImplemented,
        TimeSpan maxSnapshotAge)
    {
        if (automationPaused || !baselineApproved)
            return new Result(Action.None, "Automation is paused or initial baseline is pending.");
        if (maxSnapshotAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxSnapshotAge));
        if (links.Count == 0) return new Result(Action.None, "No linked TICKET tickets.");

        // Legacy TestLoom records may contain the same TICKET ID more than once. Duplicate
        // associations must not throw; contradictory data instead requires Master review.
        var unique = new List<Link>();
        foreach (var group in links.GroupBy(x => x.TicketId, StringComparer.OrdinalIgnoreCase))
        {
            var selected = group.OrderByDescending(x => x.VerifiedAtUtc).First();
            if (group.Any(x => x.Blocking != selected.Blocking ||
                !string.Equals(x.Type, selected.Type, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(x.Status, selected.Status, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(x.Resolution, selected.Resolution, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(x.DuplicateOf, selected.DuplicateOf, StringComparison.OrdinalIgnoreCase)))
                return new Result(Action.RequiresMasterReview, "Conflicting duplicate TICKET associations require review.");
            unique.Add(selected);
        }
        var byId = unique.ToDictionary(x => x.TicketId, StringComparer.OrdinalIgnoreCase);
        var blockers = new List<Link>();
        foreach (var link in unique.Where(l => l.Blocking))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = link;
            if (!Fresh(current, nowUtc, maxSnapshotAge))
                return new Result(Action.None, "TICKET data missing, stale or clock-skewed.");
            while (string.Equals(current.Status, "closed", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(current.Resolution, "duplicate", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(current.DuplicateOf) ||
                    !byId.TryGetValue(current.DuplicateOf, out var original) ||
                    !seen.Add(current.TicketId))
                    return new Result(Action.RequiresMasterReview, "Duplicate original not verified or cyclic relation.");
                current = original;
                if (!Fresh(current, nowUtc, maxSnapshotAge))
                    return new Result(Action.None, "Duplicate origin has not been freshly verified.");
            }
            blockers.Add(current);
        }
        if (blockers.Count == 0)
            return new Result(Action.None, "No blocking TICKET tickets.");

        foreach (var link in blockers)
        {
            // A slow/offline OneDrive replica is never a source of truth for new automatic transitions.
            if (!Fresh(link, nowUtc, maxSnapshotAge))
                return new Result(Action.None, "TICKET data missing, stale or clock-skewed.");
            if (kind == EntityKind.Nc && !PolarionTicketClassification.MatchesNc(link.Type) ||
                kind == EntityKind.Cr && !PolarionTicketClassification.MatchesCr(link.Type))
                return new Result(Action.RequiresMasterReview, "Ticket type differs from the TestLoom entity.");
            if (string.Equals(link.Status, "closed", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(link.Resolution, "invalid", StringComparison.OrdinalIgnoreCase))
                return new Result(Action.RequiresMasterReview, "TICKET Invalid requires manual review.");
        }
        if (blockers.All(IsDone))
        {
            if (kind == EntityKind.Nc)
                return new Result(Action.ReadyForRetest, "All blocking TICKET tickets are Closed/Done.");
            if (crImplemented)
                return new Result(Action.ReadyForValidation, "All blocking TICKET tickets are Done and CR is implemented.");
            return new Result(Action.None, "CR implementation prerequisite is missing.");
        }
        if (kind == EntityKind.Nc && blockers.Any(IsSuspended) && blockers.All(link => IsSuspended(link) || IsDone(link)))
            return new Result(Action.PutNcOnHold, "All unresolved blocking TICKET tickets are suspended.");
        return new Result(Action.None, "At least one blocking TICKET ticket still needs work.");
    }

    private static bool Fresh(Link link, DateTimeOffset nowUtc, TimeSpan maxAge) =>
        link.VerifiedAtUtc != default && link.VerifiedAtUtc <= nowUtc.AddMinutes(2) &&
        nowUtc - link.VerifiedAtUtc <= maxAge;

    private static bool IsDone(Link link) =>
        string.Equals(link.Status, "closed", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(link.Resolution, "done", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuspended(Link link) =>
        string.Equals(link.Status, "closed", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(link.Resolution, "suspended", StringComparison.OrdinalIgnoreCase);
}
