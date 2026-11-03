namespace TestLoom.App.Models;

/// <summary>Read-only snapshot of the useful TICKET fields. Never contains credentials or attachments.</summary>
public sealed class PolarionTicketSnapshot
{
    public string ProjectId { get; init; } = "";
    public string Id { get; init; } = "";
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? TicketType { get; init; }
    public string? Status { get; init; }
    public string? Resolution { get; init; }
    public string? Priority { get; init; }
    public string? Severity { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Updated { get; init; }
    /// <summary>Real Ticketing resolution time (resolvedOn); null while the ticket is not resolved.</summary>
    public DateTimeOffset? ResolvedOn { get; init; }
    /// <summary>TICKET_ticket_end_date as returned by Ticketing (a date, not a timestamp).</summary>
    public string? TicketEndDate { get; init; }
    public string? WorkingUnit { get; init; }
    public string? Invoice { get; init; }
    /// <summary>TICKET_component / TICKET_ecu custom enum lists; empty when the field is not set.</summary>
    public List<string> Components { get; init; } = new();
    public List<string> Componenti { get; init; } = new();
    public DateTimeOffset RetrievedAtUtc { get; init; }
    public List<string> Assignees { get; init; } = new();
    /// <summary>Multi-assignee project custom field; not mapped to TestLoom's internal owner.</summary>
    public List<string> CustomAssignees { get; init; } = new();
    public List<string> LinkedWorkItems { get; init; } = new();
    public List<PolarionCommentSnapshot> Comments { get; init; } = new();
}

public sealed class PolarionCommentSnapshot
{
    public string Id { get; init; } = "";
    public string? AuthorId { get; init; }
    public DateTimeOffset? Created { get; init; }
    /// <summary>Original Ticketing rich-text value; UI must display it safely, not execute HTML.</summary>
    public string Text { get; init; } = "";
}

/// <summary>Only the confirmed TICKET classifications drive NC/CR integration.</summary>
public static class PolarionTicketClassification
{
    public const string NonConformita = "bug";
    public const string ChangeRequest = "new_request";
    public static bool MatchesNc(string? type) => string.Equals(type, NonConformita, StringComparison.OrdinalIgnoreCase);
    public static bool MatchesCr(string? type) => string.Equals(type, ChangeRequest, StringComparison.OrdinalIgnoreCase);
}
