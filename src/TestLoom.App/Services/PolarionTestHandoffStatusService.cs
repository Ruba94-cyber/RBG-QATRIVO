using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Read-only status snapshot for the Test editor. Only browser handoffs explicitly
/// launched from this Test are included: an NC/CR receipt without OriginTestUid
/// must never be attributed to the Test by guessing a shared source ID.
/// Local receipts indicate an attempt, never proof of an TICKET created in Ticketing.
/// </summary>
public static class PolarionTestHandoffStatusService
{
    public sealed record Pending(string SourceId, bool IsCr, string? VerifiedTicketId);
    public sealed record Snapshot(IReadOnlyList<Pending> PendingHandoffs,
        IReadOnlyList<string> LinkedTicketIds)
    {
        public int PendingCount => PendingHandoffs.Count;
        public int LinkedCount => LinkedTicketIds.Count;
    }

    public static Snapshot Summarize(TestCase test,
        IEnumerable<PolarionCreationReceiptStore.Receipt> receipts,
        IEnumerable<TicketRif> linkedTickets, string? currentOperator = null)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(receipts);
        ArgumentNullException.ThrowIfNull(linkedTickets);

        var pending = receipts
            .Where(r => r.BrowserCorrelationMarker is not null &&
                r.OriginTestUid == test.Uid &&
                string.Equals(r.OriginTestId, test.Id, StringComparison.OrdinalIgnoreCase) &&
                (currentOperator is null ||
                 string.Equals(r.OperatorAtHandoff, currentOperator, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(r => r.OperationId)
            .Select(group => group.First())
            .OrderBy(r => r.StartedUtc)
            .Select(r => new Pending(r.SourceId, r.IsCr, r.CreatedTicketId))
            .ToArray();

        // A stored link is a TestLoom reference, not proof of a fresh online GET.
        var linked = linkedTickets
            .Select(t => t.Riferimento?.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new Snapshot(pending, linked);
    }
}
