using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Read-only reporting over already linked TICKET work items. One row per ticket, never per NC/CR.
/// Exact created timestamps come from Ticketing; testing/closure/reopen timestamps are FIRST
/// OBSERVATIONS by TestLoom, not authoritative historical transition timestamps.
/// </summary>
public static class PolarionReportService
{
    public sealed record Association(string TicketId, string TestLoomKind, string TestLoomId,
        IReadOnlyList<string> Vehicles, bool Archived = false);

    public sealed record ObservedTransition(DateTimeOffset AtUtc, string Status,
        string Resolution, bool IsBaseline);

    public sealed class Row
    {
        private static string L(string it, string en, string fr) => LocalizationService.Instance.Scegli(it, en, fr);
        public string TicketId { get; init; } = "";
        public string Title { get; init; } = "";
        public string Type { get; init; } = "";
        public string Status { get; init; } = "";
        public string Resolution { get; init; } = "";
        public string Priority { get; init; } = "";
        public string Severity { get; init; } = "";
        public string Records { get; init; } = "";
        public bool Archived { get; init; }
        public string ArchiveText => Archived ? L("Sì", "Yes", "Oui") : L("No", "No", "Non");
        public string Vehicles { get; init; } = "";
        public string Ranges { get; init; } = "";
        public DateTimeOffset? CreatedAt { get; init; }
        public DateTimeOffset? FirstTestingObservedAt { get; init; }
        public DateTimeOffset? FirstClosedObservedAt { get; init; }
        public DateTimeOffset? LastReopenObservedAt { get; init; }
        public bool AlreadyTestingAtBaseline { get; init; }
        public bool AlreadyClosedAtBaseline { get; init; }
        public DateTimeOffset LastFetchedAt { get; init; }
        public bool HasCache { get; init; }
        public bool HistoryUnavailable { get; init; }
        public int CommentsCount { get; init; }
        /// <summary>TestLoom-observed transitions after baseline; never official historical totals.</summary>
        public int TestingTransitionsObserved { get; init; }
        public int ClosureTransitionsObserved { get; init; }
        public int ReopenTransitionsObserved { get; init; }
        /// <summary>Source-backed observations, not reconstructed historical transition dates.</summary>
        public IReadOnlyList<ObservedTransition> Timeline { get; init; } =
            Array.Empty<ObservedTransition>();
        /// <summary>
        /// The elapsed time from Ticketing creation to TestLoom's first DETECTION of Testing/Closed.
        /// A conservative upper bound only, never the exact Ticketing transition duration.
        /// </summary>
        public double? TestingUpperBoundHours => UpperBound(CreatedAt, FirstTestingObservedAt);
        public double? ClosureUpperBoundHours => UpperBound(CreatedAt, FirstClosedObservedAt);
        public string TestingUpperBoundText => FormatUpperBound(TestingUpperBoundHours);
        public string ClosureUpperBoundText => FormatUpperBound(ClosureUpperBoundHours);
        public string ObservedTransitionsText => HistoryUnavailable
            ? L("Cronologia non disponibile", "History unavailable", "Historique indisponible")
            : "Testing " + TestingTransitionsObserved + " · " +
            L("Chiusure ", "Closed ", "Clôtures ") + ClosureTransitionsObserved + " · " +
            L("Riaperture ", "Reopened ", "Réouvertures ") + ReopenTransitionsObserved;

        private static double? UpperBound(DateTimeOffset? created, DateTimeOffset? observed)
            => !created.HasValue || !observed.HasValue || observed.Value < created.Value
                ? null : (observed.Value - created.Value).TotalHours;
        private static string FormatUpperBound(double? hours)
        {
            if (!hours.HasValue || !double.IsFinite(hours.Value))
                return L("Non calcolabile", "Unavailable", "Non calculable");
            var rounded = (long)Math.Ceiling(hours.Value);
            return "≤ " + (rounded >= 24 ? rounded / 24 + L("g ", "d ", "j ") : "") +
                rounded % 24 + L("h (limite superiore)", "h (upper bound)", "h (limite supérieure)");
        }

        public string CreatedText => Format(CreatedAt);
        public string TestingText => AlreadyTestingAtBaseline
            ? FirstTestingObservedAt.HasValue
                ? L("Testing al baseline; nuovo passaggio rilevato: ",
                    "Testing at baseline; next transition observed: ",
                    "En test au départ ; nouvelle transition observée : ") + Format(FirstTestingObservedAt)
                : L("Già in Testing alla prima lettura",
                    "Already in Testing at first read", "Déjà en test à la première lecture")
            : FirstTestingObservedAt.HasValue
                ? L("Rilevato: ", "Observed: ", "Observé : ") + Format(FirstTestingObservedAt)
                : L("Non noto", "Unknown", "Inconnu");
        public string ClosedText => AlreadyClosedAtBaseline && !FirstClosedObservedAt.HasValue
            ? L("Già chiuso alla prima lettura", "Already closed at first read",
                "Déjà clôturé à la première lecture")
            : FirstClosedObservedAt.HasValue
                ? L("Rilevato: ", "Observed: ", "Observé : ") + Format(FirstClosedObservedAt)
                : L("Non noto", "Unknown", "Inconnu");
        public string ReopenedText => LastReopenObservedAt.HasValue
            ? L("Rilevato: ", "Observed: ", "Observé : ") + Format(LastReopenObservedAt) : "—";
        public string DataQuality
        {
            get
            {
                var cache = !HasCache
                    ? L("Non sincronizzato", "Not synchronized", "Non synchronisé")
                    : LastFetchedAt == default
                        ? L("Cache senza data", "Undated cache", "Cache sans date")
                        : "Cache " + Format(LastFetchedAt);
                if (HasCache && string.IsNullOrWhiteSpace(Status))
                    cache += L(" · Stato sconosciuto", " · Unknown status", " · Statut inconnu");
                if (HistoryUnavailable)
                    cache += L(" · Cronologia non disponibile: verifica richiesta",
                        " · History unavailable: review required",
                        " · Historique indisponible : vérification nécessaire");
                return cache;
            }
        }
        private static string Format(DateTimeOffset? when) =>
            when.HasValue ? when.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
                : L("Non noto", "Unknown", "Inconnu");
    }

    public sealed record Filter(
        string? Query = null, string? Status = null, string? Type = null,
        string? Priority = null, string? Severity = null, string? Resolution = null,
        string? Range = null, string? Vehicle = null,
        DateTimeOffset? DateFrom = null, DateTimeOffset? DateTo = null,
        string DateDimension = "Apertura",
        bool OnlyObservedClosures = false, bool IncludeArchived = true);

    public sealed record Distribution(string Status, int Count);
    public sealed record Overview(int Total, int NotSynced, int Open, int Closed, int Testing,
        int AlreadyClosedAtBaseline, int ObservedClosures, int ObservedReopenings,
        int WithUnknownCreation, IReadOnlyList<Distribution> StatusDistribution)
    {
        public int UnknownStatus { get; init; }
        public int HistoriesUnavailable { get; init; }
    }

    public static IReadOnlyList<Row> Build(
        IEnumerable<Association> associations,
        Func<string, PolarionTicketSnapshot?> getSnapshot,
        Func<string, PolarionStatusHistoryService.History?> getHistory,
        IReadOnlyDictionary<string, string> rangeByVehicle)
    {
        var result = new List<Row>();
        foreach (var group in associations.Where(a => !string.IsNullOrWhiteSpace(a.TicketId))
            .GroupBy(a => a.TicketId, StringComparer.OrdinalIgnoreCase))
        {
            var id = group.Key;
            PolarionTicketSnapshot? snapshot = null;
            PolarionStatusHistoryService.History? history = null;
            try { snapshot = getSnapshot(id); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            var historyUnavailable = false;
            try { history = getHistory(id); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                historyUnavailable = true;
            }

            var events = (history?.Observations ?? new())
                .OrderBy(o => o.FirstSeenUtc).ToList();
            var first = events.FirstOrDefault();
            DateTimeOffset? testing = null, closed = null, reopened = null;
            var testingCount = 0;
            var closureCount = 0;
            var reopenCount = 0;
            for (var i = 1; i < events.Count; i++)
            {
                var prior = events[i - 1].Status;
                var current = events[i].Status;
                if (IsTesting(current) && !IsTesting(prior))
                {
                    testingCount++;
                    testing ??= events[i].FirstSeenUtc;
                }
                if (IsClosed(current) && !IsClosed(prior))
                {
                    closureCount++;
                    closed ??= events[i].FirstSeenUtc;
                }
                if (IsClosed(prior) && !IsClosed(current))
                {
                    reopenCount++;
                    reopened = events[i].FirstSeenUtc;
                }
            }
            var vehicles = group.SelectMany(a => a.Vehicles).Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray();
            var ranges = vehicles.Where(rangeByVehicle.ContainsKey).Select(v => rangeByVehicle[v])
                .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray();
            result.Add(new Row
            {
                TicketId = id.ToUpperInvariant(),
                Archived = group.All(a => a.Archived),
                Title = snapshot?.Title ?? "",
                Status = snapshot?.Status ?? "",
                Resolution = snapshot?.Resolution ?? "",
                Type = snapshot?.TicketType ?? "",
                Priority = DisplayPriority(snapshot?.Priority),
                Severity = DisplaySeverity(snapshot?.Severity),
                Records = string.Join(", ", group.Select(a => a.TestLoomKind + " " + a.TestLoomId)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                Vehicles = string.Join(", ", vehicles),
                Ranges = string.Join(", ", ranges),
                CreatedAt = snapshot?.Created ?? history?.PolarionCreatedUtc,
                FirstTestingObservedAt = testing,
                FirstClosedObservedAt = closed,
                LastReopenObservedAt = reopened,
                AlreadyTestingAtBaseline = first is not null && IsTesting(first.Status),
                AlreadyClosedAtBaseline = first is not null && IsClosed(first.Status),
                LastFetchedAt = snapshot?.RetrievedAtUtc ?? default,
                HasCache = snapshot is not null,
                HistoryUnavailable = historyUnavailable,
                CommentsCount = snapshot?.Comments.Count ?? 0,
                TestingTransitionsObserved = testingCount,
                ClosureTransitionsObserved = closureCount,
                ReopenTransitionsObserved = reopenCount,
                Timeline = events.Select((e, i) => new ObservedTransition(e.FirstSeenUtc,
                    e.Status, e.Resolution, IsBaseline: i == 0)).ToArray()
            });
        }
        return result.OrderByDescending(r => r.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(r => r.TicketId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<Row> Apply(IReadOnlyList<Row> rows, Filter filter)
    {
        var q = (filter.Query ?? "").Trim();
        return rows.Where(r =>
            (filter.IncludeArchived || !r.Archived) &&
            (q.Length == 0 || new[] { r.TicketId, r.Title, r.Records, r.Vehicles, r.Ranges }
                .Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase))) &&
            (string.IsNullOrWhiteSpace(filter.Status) || filter.Status == "Tutti" ||
                r.Status.Equals(filter.Status, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(filter.Type) || filter.Type == "Tutti" ||
                r.Type.Equals(filter.Type, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(filter.Priority) || filter.Priority == "Tutti" ||
                r.Priority.Equals(filter.Priority, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(filter.Severity) || filter.Severity == "Tutte" ||
                r.Severity.Equals(filter.Severity, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(filter.Resolution) || filter.Resolution == "Tutte" ||
                r.Resolution.Equals(filter.Resolution, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(filter.Range) || filter.Range == "Tutte" ||
                r.Ranges.Split(", ", StringSplitOptions.RemoveEmptyEntries).Contains(filter.Range, StringComparer.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(filter.Vehicle) || filter.Vehicle == "Tutti" ||
                r.Vehicles.Split(", ", StringSplitOptions.RemoveEmptyEntries).Contains(filter.Vehicle, StringComparer.OrdinalIgnoreCase)) &&
            (!filter.DateFrom.HasValue || DateFor(r, filter.DateDimension)?.ToLocalTime().Date >= filter.DateFrom.Value.ToLocalTime().Date) &&
            (!filter.DateTo.HasValue || DateFor(r, filter.DateDimension)?.ToLocalTime().Date <= filter.DateTo.Value.ToLocalTime().Date) &&
            (!filter.OnlyObservedClosures || r.FirstClosedObservedAt.HasValue))
            .ToList();
    }

    private static DateTimeOffset? DateFor(Row row, string dimension) => dimension switch
    {
        "Testing" => row.FirstTestingObservedAt,
        "Chiusura" => row.FirstClosedObservedAt,
        _ => row.CreatedAt
    };

    public static Overview Summarize(IReadOnlyList<Row> rows)
    {
        var distribution = rows.GroupBy(r => string.IsNullOrWhiteSpace(r.Status) ? "Sconosciuto" : r.Status,
            StringComparer.OrdinalIgnoreCase)
            .Select(group => new Distribution(group.Key, group.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Status).ToList();
        return new Overview(rows.Count, rows.Count(r => !r.HasCache),
            rows.Count(r => r.HasCache && !string.IsNullOrWhiteSpace(r.Status) && !IsClosed(r.Status)),
            rows.Count(r => IsClosed(r.Status)),
            rows.Count(r => IsTesting(r.Status)),
            rows.Count(r => r.AlreadyClosedAtBaseline),
            rows.Count(r => r.FirstClosedObservedAt.HasValue),
            rows.Count(r => r.LastReopenObservedAt.HasValue),
            rows.Count(r => !r.CreatedAt.HasValue), distribution)
        {
            UnknownStatus = rows.Count(r => string.IsNullOrWhiteSpace(r.Status)),
            HistoriesUnavailable = rows.Count(r => r.HistoryUnavailable)
        };
    }

    private static string DisplayPriority(string? priority)
    {
        var rank = PolarionCriticalityService.ParsePriority(priority);
        return rank.HasValue ? "P" + (6 - rank.Value) : priority ?? "";
    }

    private static string DisplaySeverity(string? severity)
    {
        if (string.IsNullOrWhiteSpace(severity)) return "";
        var value = severity.Trim();
        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static bool IsTesting(string? status) =>
        string.Equals(status, "testing", StringComparison.OrdinalIgnoreCase);
    private static bool IsClosed(string? status) =>
        string.Equals(status, "closed", StringComparison.OrdinalIgnoreCase);
}
