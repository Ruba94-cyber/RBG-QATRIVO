using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Lightweight append-only (logical) record of distinct TICKET states observed by TestLoom.
/// It never claims the observation timestamp is the actual Ticketing transition timestamp.
/// One file per work item / device to avoid different PCs overwriting one another's history.
/// The first observation is a baseline, NOT a reconstructed historic workflow.
/// </summary>
public sealed class PolarionStatusHistoryService(DataPaths paths, string? deviceId = null)
{
    private static readonly Regex KeyPattern = new("^[A-Za-z][A-Za-z0-9_]*-[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly Regex ProjectPattern = new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _device = ValidateDevice(deviceId ?? DeviceIdentityService.GetOrCreate());

    public sealed class Observation
    {
        public string Status { get; set; } = "";
        public string Resolution { get; set; } = "";
        /// <summary>Time TestLoom first saw this state, not when Ticketing entered it.</summary>
        public DateTimeOffset FirstSeenUtc { get; set; }
        /// <summary>Ticketing work-item "updated"; this may refer to OTHER fields, not status.</summary>
        public DateTimeOffset? PolarionUpdatedUtc { get; set; }
    }

    public sealed class History
    {
        public string ProjectId { get; set; } = "";
        public string TicketId { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public DateTimeOffset? PolarionCreatedUtc { get; set; }
        public DateTimeOffset LastObservedUtc { get; set; }
        /// <summary>Most recent Ticketing update, even for edits unrelated to status.</summary>
        public DateTimeOffset? LastPolarionUpdatedUtc { get; set; }
        public List<Observation> Observations { get; set; } = new();
    }

    public void Observe(PolarionTicketSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.RetrievedAtUtc == default || snapshot.RetrievedAtUtc > DateTimeOffset.UtcNow.AddMinutes(2))
            throw new InvalidDataException("TICKET observation has no trustworthy retrieval time.");
        var file = DeviceFile(snapshot.ProjectId, snapshot.Id, _device);
        lock (Gates.GetOrAdd(file, _ => new object()))
        {
            var history = LoadFile(file) ?? new History
            {
                ProjectId = snapshot.ProjectId, TicketId = snapshot.Id, DeviceId = _device
            };
            if (!string.Equals(history.TicketId, snapshot.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(history.ProjectId, snapshot.ProjectId, StringComparison.OrdinalIgnoreCase) ||
                history.DeviceId != _device)
                throw new InvalidDataException("TICKET status history identity mismatch.");
            if (snapshot.RetrievedAtUtc <= history.LastObservedUtc) return;

            var last = history.Observations.LastOrDefault();
            // A title/comment update can advance Ticketing's revision without a status change.
            // Reject a later response from a lagging replica even in that situation.
            var latestRevision = history.LastPolarionUpdatedUtc ??
                history.Observations.Select(o => o.PolarionUpdatedUtc).Max();
            if (latestRevision is not null &&
                (snapshot.Updated is null || snapshot.Updated < latestRevision))
                return;
            // Same repository update timestamp with contradictory states is ambiguous.
            // It must not be counted as an actual Testing/Closed transition.
            if (last is not null && snapshot.Updated is not null &&
                latestRevision == snapshot.Updated &&
                (!string.Equals(last.Status, snapshot.Status ?? "", StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(last.Resolution, snapshot.Resolution ?? "", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Contradictory TICKET state at the same Ticketing revision.");
            var revisionAdvanced = snapshot.Updated is not null &&
                (latestRevision is null || snapshot.Updated > latestRevision);
            if (revisionAdvanced) history.LastPolarionUpdatedUtc = snapshot.Updated;
            if (last is null || !string.Equals(last.Status, snapshot.Status ?? "", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(last.Resolution, snapshot.Resolution ?? "", StringComparison.OrdinalIgnoreCase))
                history.Observations.Add(new Observation
                {
                    Status = snapshot.Status ?? "",
                    Resolution = snapshot.Resolution ?? "",
                    FirstSeenUtc = snapshot.RetrievedAtUtc,
                    PolarionUpdatedUtc = snapshot.Updated
                });

            // Avoid a OneDrive write on every 15-minute poll unless something observable changed.
            var createdChanged = history.PolarionCreatedUtc is null && snapshot.Created is not null;
            if (createdChanged) history.PolarionCreatedUtc = snapshot.Created;
            history.LastObservedUtc = snapshot.RetrievedAtUtc;
            if (last is null || !string.Equals(last.Status, snapshot.Status ?? "", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(last.Resolution, snapshot.Resolution ?? "", StringComparison.OrdinalIgnoreCase) ||
                createdChanged || revisionAdvanced)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                JsonIo.WriteAtomic(file, history);
            }
        }
    }

    /// <summary>Merge same-work-item observations from all devices for read-only reporting.</summary>
    public History? ReadMerged(string projectId, string ticketId)
    {
        var baseDir = Path.Combine(paths.PolarionHistoryDir, projectId);
        _ = DeviceFile(projectId, ticketId, _device); // Validate ticket and project IDs.
        if (!Directory.Exists(baseDir)) return null;
        var deviceDirs = Directory.EnumerateDirectories(baseDir).Take(257).ToArray();
        if (deviceDirs.Length > 256)
            throw new InvalidDataException("Too many TICKET device timelines to merge safely.");
        var files = deviceDirs.Select(dir => Path.Combine(dir, ticketId.ToUpperInvariant() + ".json"))
            .Where(File.Exists).ToArray();
        if (files.Length == 0) return null;
        var histories = files.Select(LoadFile).Where(h => h is not null).Cast<History>()
            .Where(h => h.TicketId.Equals(ticketId, StringComparison.OrdinalIgnoreCase) &&
                h.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (histories.Count == 0) return null;
        // If two devices report different states at the same repository revision, don't
        // silently choose one or fabricate a lifecycle event. Keep the raw files for review.
        var allObservations = histories.SelectMany(h => h.Observations).ToList();
        var contradictoryRevision = allObservations.Where(o => o.PolarionUpdatedUtc.HasValue)
            .GroupBy(o => o.PolarionUpdatedUtc)
            .Any(group => group.Select(o =>
                    (o.Status ?? "").Trim().ToLowerInvariant() + "|" +
                    (o.Resolution ?? "").Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal).Skip(1).Any());
        if (contradictoryRevision)
            throw new InvalidDataException("Conflicting TICKET states for the same Ticketing revision. Review required.");
        // Dedupe identical state observations from different PCs using Ticketing updated
        // revision as corroboration. A missing revision is not proof of equality.
        var observations = allObservations
            .Where(o => o.FirstSeenUtc != default)
            .GroupBy(o => new { o.Status, o.Resolution, o.PolarionUpdatedUtc,
                // When Ticketing omits its updated field, separate events cannot be proven
                // identical merely because their state labels happen to match.
                UnknownRevisionEvent = o.PolarionUpdatedUtc is null ? o.FirstSeenUtc : DateTimeOffset.MinValue })
            .Select(g => g.OrderBy(o => o.FirstSeenUtc).First())
            .OrderBy(o => o.FirstSeenUtc)
            .ToList();
        // A second device may observe an older Ticketing revision later than another
        // device observed a newer one. Such a stale sample is not a real reopening.
        DateTimeOffset? newestRevision = null;
        observations = observations.Where(observation =>
        {
            if (newestRevision.HasValue && (!observation.PolarionUpdatedUtc.HasValue ||
                observation.PolarionUpdatedUtc.Value < newestRevision.Value))
                return false;
            if (observation.PolarionUpdatedUtc.HasValue)
                newestRevision = observation.PolarionUpdatedUtc;
            return true;
        }).ToList();
        return new History
        {
            ProjectId = projectId, TicketId = ticketId, DeviceId = "merged",
            PolarionCreatedUtc = histories.Select(h => h.PolarionCreatedUtc).Where(x => x.HasValue)
                .OrderBy(x => x).FirstOrDefault(),
            LastObservedUtc = histories.Max(h => h.LastObservedUtc),
            LastPolarionUpdatedUtc = histories.Select(h => h.LastPolarionUpdatedUtc)
                .Concat(observations.Select(o => o.PolarionUpdatedUtc)).Max(),
            Observations = observations
        };
    }

    private static History? LoadFile(string path)
    {
        if (!File.Exists(path)) return null;
        return JsonIo.TryRead<History>(path, out var error) ??
            throw new InvalidDataException("TICKET history is corrupt; refusing to discard it.",
                error is null ? null : new InvalidDataException(error));
    }

    private string DeviceFile(string project, string ticket, string device)
    {
        if (string.IsNullOrWhiteSpace(project) || !ProjectPattern.IsMatch(project) ||
            string.IsNullOrWhiteSpace(ticket) || !KeyPattern.IsMatch(ticket))
            throw new ArgumentException("Invalid TICKET project or ticket ID.");
        return Path.Combine(paths.PolarionHistoryDir, project,
            device, ticket.ToUpperInvariant() + ".json");
    }

    private static string ValidateDevice(string device)
    {
        if (string.IsNullOrWhiteSpace(device) || device.Length > 100 ||
            device.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Invalid TestLoom device identity.");
        return device;
    }
}
