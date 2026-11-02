using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Device-local, per-operator read tracking keyed by TICKET ticket (not NC/CR link).
/// The first observation is a baseline; it never reports older comments as new.
/// No settings or read flags are written into the shared SharePoint workspace.
/// </summary>
public sealed class PolarionLocalReadTracker
{
    private static readonly Regex TicketIdPattern = new("^[A-Za-z][A-Za-z0-9_]*-[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory;

    public PolarionLocalReadTracker(DataPaths paths, string operatorId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(operatorId)) throw new ArgumentException("Operator ID required.", nameof(operatorId));
        var operatorHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operatorId.Trim().ToUpperInvariant())))[..24];
        _directory = Path.Combine(WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", "workspaces",
            WorkspaceLocalPaths.ComputeWorkspaceKey(paths.Root), "ticketing-read", operatorHash);
    }

    public sealed class Cursor
    {
        public string TicketId { get; set; } = "";
        // Never interpret an older OneDrive snapshot as a fresh reversal of state.
        public DateTimeOffset? LastObservedAtUtc { get; set; }
        /// <summary>Latest verified Ticketing work-item update; guards against stale cluster responses.</summary>
        public DateTimeOffset? LastPolarionUpdatedUtc { get; set; }
        public string? Status { get; set; }
        public string? Resolution { get; set; }
        public string? Type { get; set; }
        public string? Priority { get; set; }
        public string? Severity { get; set; }
        public List<string> KnownCommentIds { get; set; } = new();
        public List<string> UnreadCommentIds { get; set; } = new();
        public bool UnreadFieldChange { get; set; }
    }

    public sealed record Unread(int Comments, bool Fields);

    /// <summary>Only a successful complete snapshot may update the local event baseline.</summary>
    public Unread Observe(PolarionTicketSnapshot snapshot, PolarionNotificationScopeService.Mode mode = PolarionNotificationScopeService.Mode.Full)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var path = FilePath(snapshot.Id);
        lock (Gate(path))
        {
            var cursor = Load(path);
            if (cursor is not null)
            {
                // Retrieval time is not a Ticketing revision: a late GET to an older
                // server replica must never create a false reopened/status notification.
                if (cursor.LastPolarionUpdatedUtc.HasValue &&
                    (snapshot.Updated is null || snapshot.Updated < cursor.LastPolarionUpdatedUtc.Value))
                    return new Unread(cursor.UnreadCommentIds.Count, cursor.UnreadFieldChange);
                if (cursor.LastObservedAtUtc.HasValue && snapshot.RetrievedAtUtc != default &&
                    snapshot.RetrievedAtUtc <= cursor.LastObservedAtUtc.Value)
                    return new Unread(cursor.UnreadCommentIds.Count, cursor.UnreadFieldChange);
                if (snapshot.Updated.HasValue && cursor.LastPolarionUpdatedUtc == snapshot.Updated &&
                    (Changed(cursor.Status, snapshot.Status) || Changed(cursor.Resolution, snapshot.Resolution)))
                    throw new InvalidDataException("Same TICKET revision returned conflicting states; local notifications are paused for this sample.");
            }
            if (cursor is null)
            {
                // Baseline: historical comments are stored as known, not unread.
                cursor = new Cursor { TicketId = snapshot.Id };
                UpdateFields(cursor, snapshot);
                cursor.KnownCommentIds = snapshot.Comments.Select(x => x.Id)
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
            }
            else
            {
                var reopened = string.Equals(cursor.Status, "closed", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(snapshot.Status, "closed", StringComparison.OrdinalIgnoreCase);
                var relevantFieldChanged = Changed(cursor.Status, snapshot.Status) ||
                    Changed(cursor.Resolution, snapshot.Resolution) ||
                    Changed(cursor.Type, snapshot.TicketType) ||
                    Changed(cursor.Priority, snapshot.Priority) ||
                    Changed(cursor.Severity, snapshot.Severity);
                if (mode == PolarionNotificationScopeService.Mode.Full && relevantFieldChanged ||
                    mode == PolarionNotificationScopeService.Mode.ReopenOnly && reopened)
                    cursor.UnreadFieldChange = true;

                var known = cursor.KnownCommentIds.ToHashSet(StringComparer.Ordinal);
                var unread = cursor.UnreadCommentIds.ToHashSet(StringComparer.Ordinal);
                foreach (var comment in snapshot.Comments)
                {
                    if (string.IsNullOrWhiteSpace(comment.Id) || !known.Add(comment.Id)) continue;
                    if (mode == PolarionNotificationScopeService.Mode.Full)
                        unread.Add(comment.Id);
                }
                if (mode == PolarionNotificationScopeService.Mode.Silent)
                {
                    unread.Clear();
                    cursor.UnreadFieldChange = false;
                }
                else if (mode == PolarionNotificationScopeService.Mode.ReopenOnly)
                {
                    unread.Clear();
                    if (!reopened) cursor.UnreadFieldChange = false;
                }
                cursor.KnownCommentIds = known.OrderBy(x => x, StringComparer.Ordinal).ToList();
                cursor.UnreadCommentIds = unread.OrderBy(x => x, StringComparer.Ordinal).ToList();
                UpdateFields(cursor, snapshot);
            }
            if (snapshot.RetrievedAtUtc != default)
                cursor.LastObservedAtUtc = snapshot.RetrievedAtUtc;
            if (snapshot.Updated is not null &&
                (cursor.LastPolarionUpdatedUtc is null || snapshot.Updated > cursor.LastPolarionUpdatedUtc))
                cursor.LastPolarionUpdatedUtc = snapshot.Updated;
            Write(path, cursor);
            return new Unread(cursor.UnreadCommentIds.Count, cursor.UnreadFieldChange);
        }
    }

    public Unread GetUnread(string ticketId)
    {
        var path = FilePath(ticketId);
        lock (Gate(path))
        {
            var cursor = Load(path);
            return new Unread(cursor?.UnreadCommentIds.Count ?? 0, cursor?.UnreadFieldChange ?? false);
        }
    }

    public void MarkRead(string ticketId, bool comments, bool fields)
    {
        var path = FilePath(ticketId);
        lock (Gate(path))
        {
            var cursor = Load(path);
            if (cursor is null) return;
            if (comments) cursor.UnreadCommentIds.Clear();
            if (fields) cursor.UnreadFieldChange = false;
            Write(path, cursor);
        }
    }

    private static bool Changed(string? previous, string? next) =>
        !string.Equals(previous ?? "", next ?? "", StringComparison.OrdinalIgnoreCase);

    private static void UpdateFields(Cursor cursor, PolarionTicketSnapshot snapshot)
    {
        cursor.Status = snapshot.Status;
        cursor.Resolution = snapshot.Resolution;
        cursor.Type = snapshot.TicketType;
        cursor.Priority = snapshot.Priority;
        cursor.Severity = snapshot.Severity;
    }

    private static object Gate(string path) => Gates.GetOrAdd(path, _ => new object());

    private static Cursor? Load(string path)
    {
        if (!File.Exists(path)) return null;
        return JsonIo.TryRead<Cursor>(path, out var error) ??
            throw new InvalidDataException("Local TICKET read state is unreadable; cannot overwrite.", error is null ? null : new InvalidDataException(error));
    }

    private static void Write(string path, Cursor cursor)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonIo.WriteAtomic(path, cursor);
    }

    private string FilePath(string ticketId)
    {
        if (string.IsNullOrWhiteSpace(ticketId) || !TicketIdPattern.IsMatch(ticketId))
            throw new ArgumentException("Invalid TICKET ID for local read tracking.", nameof(ticketId));
        return Path.Combine(_directory, ticketId.ToUpperInvariant() + ".json");
    }
}
