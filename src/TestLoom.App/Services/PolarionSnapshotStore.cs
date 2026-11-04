using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Rebuildable shared TICKET snapshot cache. One file per ticket, not one copy per NC/CR.
/// Never writes to NC/CR domain records or stores credentials.
/// </summary>
public sealed class PolarionSnapshotStore(DataPaths paths, bool sharedWriteAllowed = true)
{
    internal bool SharedWriteAllowed => sharedWriteAllowed;
    private static readonly Regex KeyPattern = new("^[A-Za-z][A-Za-z0-9_]*(-[0-9]+)?$", RegexOptions.CultureInvariant);
    // Serializes read/compare/replace for this process only. OneDrive replication is not an
    // atomic multi-device compare-and-swap; later replicas must still reconcile remote forks.
    private static readonly ConcurrentDictionary<string, object> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public PolarionTicketSnapshot? Load(string projectId, string ticketId)
    {
        var path = FilePath(projectId, ticketId);
        if (!File.Exists(path)) return null;
        var snapshot = JsonIo.TryRead<PolarionTicketSnapshot>(path, out var error);
        if (snapshot is null)
            throw new InvalidDataException("Existing TICKET snapshot cannot be read; refusing to silently discard it.", error is null ? null : new InvalidDataException(error));
        if (!string.Equals(snapshot.Id, ticketId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cached TICKET snapshot identity mismatch.");
        // Un file JSON puo' essere valido ma contenere array esplicitamente null.
        // Report, timeline e pannelli di stato non devono lanciare NRE e
        // interrompere il caricamento di tutti gli altri ticket.
        if (snapshot.Comments is null || snapshot.Assignees is null ||
            snapshot.CustomAssignees is null || snapshot.LinkedWorkItems is null ||
            snapshot.Components is null || snapshot.Componenti is null)
            throw new InvalidDataException("Cached TICKET snapshot has invalid null collections.");
        return snapshot;
    }

    /// <summary>
    /// Best effort cache write. OneDrive replicas are not a distributed lock; cache conflicts can
    /// always be recovered by a fresh GET, while authoritative TestLoom workflow writes require their
    /// existing separate revision and journal transaction path.
    /// </summary>
    public bool SaveIfNewer(PolarionTicketSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!sharedWriteAllowed) return false; // A read-only operator may still fetch a live snapshot.
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Ticketing TICKET shared cache");
        var file = FilePath(snapshot.ProjectId, snapshot.Id);
        lock (FileLocks.GetOrAdd(file, _ => new object()))
        {
            var current = Load(snapshot.ProjectId, snapshot.Id);
            if (current is not null)
            {
                // Never overwrite a verified revision with an older revision, even if the
                // latter was fetched more recently from a lagging Ticketing cluster node.
                if (current.Updated is not null &&
                    (snapshot.Updated is null || snapshot.Updated < current.Updated))
                    return false;
                // Se due repliche Ticketing danno stati differenti alla medesima
                // revisione, non sovrascrivere la cache con uno stato ambiguo.
                // Anche lo storico rifiuta esplicitamente questa contraddizione.
                if (current.Updated is not null && current.Updated == snapshot.Updated &&
                    (!string.Equals(current.Status?.Trim(), snapshot.Status?.Trim(),
                        StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(current.Resolution?.Trim(), snapshot.Resolution?.Trim(),
                        StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException(
                        "Conflicting TICKET status or resolution at the same Ticketing revision; cache unchanged.");
                if (current.RetrievedAtUtc >= snapshot.RetrievedAtUtc)
                    return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            JsonIo.WriteAtomic(file, snapshot);
            return true;
        }
    }

    /// <summary>Records status observations for both periodic AND manual TICKET refreshes.</summary>
    public void RecordObservedStatus(PolarionTicketSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!sharedWriteAllowed) return;
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Ticketing status history");
        new PolarionStatusHistoryService(paths).Observe(snapshot);
    }

    private string FilePath(string projectId, string ticketId)
    {
        if (!KeyPattern.IsMatch(projectId) || !KeyPattern.IsMatch(ticketId) ||
            !ticketId.Contains('-'))
            throw new ArgumentException("Invalid Ticketing cache key.");
        return Path.Combine(paths.PolarionSnapshotDir, projectId, ticketId + ".json");
    }
}

/// <summary>Pulls a ticket once and shares its full comments and status across linked NC/CR.</summary>
public sealed class PolarionTicketRefreshService(PolarionReadClient client, PolarionSnapshotStore store)
{
    internal sealed record StoredRefresh(PolarionTicketSnapshot Snapshot, bool CacheWritten);

    public PolarionTicketSnapshot? ReadCached(string projectId, string ticketId) => store.Load(projectId, ticketId);

    /// <summary>Refresh for explicit or scheduled sync: report failure unless cache and history are usable.</summary>
    internal async Task<StoredRefresh> RefreshAndStoreAsync(string projectId, string ticketId, CancellationToken cancellationToken = default)
    {
        if (!store.SharedWriteAllowed)
            throw new IOException("Shared TICKET cache is read-only.");
        var fetched = await client.GetTicketAsync(projectId, ticketId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var written = store.SaveIfNewer(fetched);
        // A newer cache can win a concurrent or stale-server comparison. Use the
        // authoritative cached snapshot, not the older response, for observations.
        var current = written ? fetched : store.Load(projectId, ticketId)
            ?? throw new InvalidDataException("TICKET cache was not stored and no valid snapshot exists.");
        store.RecordObservedStatus(current);
        return new StoredRefresh(current, written);
    }

    public async Task<PolarionTicketSnapshot> RefreshAsync(string projectId, string ticketId, CancellationToken cancellationToken = default)
    {
        // Only commit complete successful work item + comment responses. In particular no partial
        // comment cache after HTTP 401/403/429, proxy failure, cancellation or pagination failure.
        var snapshot = await client.GetTicketAsync(projectId, ticketId, cancellationToken).ConfigureAwait(false);
        try
        {
            store.SaveIfNewer(snapshot);
            store.RecordObservedStatus(snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A read-only/offline shared workspace does not invalidate a successful GET.
            // A corrupt cache/history remains untouched for manual recovery.
        }
        return snapshot;
    }
}
