using System.Globalization;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Pure effective-priority/severity calculation for linked TICKET tickets.
/// Does not mutate NC/CR, TestCase, history or Ticketing.
/// Closed/Done continues to contribute until TestLoom closes its own record.
/// </summary>
public static class PolarionCriticalityService
{
    public sealed record Result(string Priority, string Severity, IReadOnlyList<string> ContributingIds);
    private static readonly string[] Severities = ["enhancement", "trivial", "minor", "normal", "major", "critical"];

    public static Result? Evaluate(IEnumerable<PolarionTicketSnapshot> snapshots, bool isChangeRequest, bool testloomEntityClosed)
    {
        if (testloomEntityClosed) return null;
        var contributing = new List<string>();
        int highestPriority = -1, highestSeverity = -1;
        foreach (var ticket in snapshots)
        {
            var matches = isChangeRequest
                ? PolarionTicketClassification.MatchesCr(ticket.TicketType)
                : PolarionTicketClassification.MatchesNc(ticket.TicketType);
            if (!matches || IsExcluded(ticket)) continue;
            var priority = ParsePriority(ticket.Priority);
            var severity = ParseSeverity(ticket.Severity);
            if (priority is null && severity is null) continue;
            if (priority is { } p && p > highestPriority) highestPriority = p;
            if (severity is { } s && s > highestSeverity) highestSeverity = s;
            contributing.Add(ticket.Id);
        }
        if (contributing.Count == 0) return null;
        return new Result(highestPriority > 0 ? "P" + (6 - highestPriority) : "",
            highestSeverity >= 0 ? ToLabel(Severities[highestSeverity]) : "",
            contributing.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Ticketing enum IDs: 50=P1, 33=P2, 25=P3, 20=P4, 16=P5.</summary>
    public static int? ParsePriority(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 2 && (trimmed[0] is 'P' or 'p') && trimmed[1] >= '1' && trimmed[1] <= '5')
            return 6 - (trimmed[1] - '0');
        if (!decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            return null;
        return number switch
        {
            50m => 5, 33m => 4, 25m => 3, 20m => 2, 16m => 1, _ => null
        };
    }

    public static int? ParseSeverity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var index = Array.FindIndex(Severities, x => x.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : index;
    }

    private static string ToLabel(string severity) => char.ToUpperInvariant(severity[0]) + severity[1..];

    private static bool IsExcluded(PolarionTicketSnapshot ticket) =>
        string.Equals(ticket.Status, "closed", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(ticket.Resolution, "duplicate", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(ticket.Resolution, "invalid", StringComparison.OrdinalIgnoreCase));
}
