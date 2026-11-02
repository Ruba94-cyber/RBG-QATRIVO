namespace TestLoom.App.Services;

/// <summary>
/// Retired REST write API. TestLoom creates and modifies TICKET work items through the
/// operator's authenticated Ticketing browser only. No HTTP POST/PATCH code is present.
/// This compatibility type exists for callers and legacy records, and always fails closed.
/// </summary>
public sealed class PolarionWriteService
{
    public PolarionWriteService(DataPaths paths, HttpClient http) { }
    public sealed record Draft(string SourceId, string Title, string Description, string TicketType,
        string? Priority = null, string? Severity = null);
    public sealed record Created(string TicketId, string ResourceId, string? PortalUrl = null);
    public sealed class OutcomeUnknownException(string message, Exception? inner = null) : IOException(message, inner);

    private static NotSupportedException Disabled() =>
        new("TestLoom Ticketing REST writes were removed. Open the authenticated Ticketing browser form instead.");

    public void ValidateCreate(Draft draft, bool operatorAuthorized, bool confirmed)
    {
        if (!operatorAuthorized || !confirmed)
            throw new UnauthorizedAccessException("An authorized operator and explicit confirmation are required.");
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Title) || string.IsNullOrWhiteSpace(draft.Description))
            throw new ArgumentException("A title and description are required.");
        throw Disabled();
    }

    public Task<Created> CreateAsync(Draft draft, bool operatorAuthorized, bool confirmed,
        CancellationToken token = default)
    {
        try { ValidateCreate(draft, operatorAuthorized, confirmed); }
        catch (Exception error) { return Task.FromException<Created>(error); }
        return Task.FromException<Created>(Disabled());
    }

    public Task AddCommentAsync(string itemId, string plainText, bool operatorAuthorized, bool confirmed,
        CancellationToken token = default) => Task.FromException(Disabled());

    public Task UpdateTextAsync(string itemId, string? newTitle, string? newDescription,
        bool operatorAuthorized, bool confirmed, CancellationToken token = default) =>
        Task.FromException(Disabled());
}
