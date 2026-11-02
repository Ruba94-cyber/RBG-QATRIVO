using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App;

/// <summary>
/// Lightweight TICKET lifecycle projection for the EXISTING NC and CR table rows.
/// Data is read from shared snapshots, never from network on the UI thread.
/// Each record stays a native NcRiga/ChangeRequest (no changes to grid virtualization or SQL sorting).
/// </summary>
public sealed class PolarionGridLifecycleConverter : IMultiValueConverter
{
    public static readonly PolarionGridLifecycleConverter Instance = new();
    private sealed record Cached(IReadOnlyList<PolarionReportService.Row> Rows, DateTimeOffset Until);
    private static readonly ConcurrentDictionary<string, Cached> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Reads = new(4, 4);
    private const int MaxCacheEntries = 2048;

    /// <summary>Raised on the Avalonia UI thread after a background cache read finishes.</summary>
    public static event Action? CacheUpdated;
    private static int _notificationQueued;
    private static void NotifyCacheUpdated()
    {
        if (Interlocked.Exchange(ref _notificationQueued, 1) != 0) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _notificationQueued, 0);
            CacheUpdated?.Invoke();
        });
    }

    public static void Invalidate()
    {
        Cache.Clear();
        NotifyCacheUpdated();
    }

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[1] is not DataPaths paths) return "—";
        var associations = new List<PolarionReportService.Association>();
        switch (values[0])
        {
            case NcRiga nc:
                var links = nc.Nc.CollegamentiPolarion
                    .Select(x => (x.Codice, x.Url));
                if (nc.Nc.CollegamentiPolarion.Count == 0 && nc.TestCase is not null)
                    links = nc.TestCase.TicketCollegati.Select(x => (x.Riferimento, x.Url));
                foreach (var (id, url) in links)
                {
                    var ticket = PolarionQuickReportTicketId.Extract(id, url);
                    if (ticket is not null)
                        associations.Add(new(ticket, "NC", nc.Nc.Id, Array.Empty<string>()));
                }
                break;
            case ChangeRequest cr:
                foreach (var link in cr.TicketCollegati)
                {
                    var ticket = PolarionQuickReportTicketId.Extract(link.Riferimento, link.Url);
                    if (ticket is not null)
                        associations.Add(new(ticket, "CR", cr.Id, Array.Empty<string>()));
                }
                break;
            default:
                return "—";
        }

        if (associations.Count == 0) return "—";
        var ids = associations.Select(x => x.TicketId).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var key = paths.Root + "|" + string.Join("|", ids);
        var now = DateTimeOffset.UtcNow;
        if (Cache.TryGetValue(key, out var ready) && ready.Until > now)
            return FormatRows(ready.Rows, parameter?.ToString());

        // Never synchronously deserialize JSON from OneDrive during DataGrid rendering.
        // All columns for the same NC/CR share one coalesced background read.
        if (Pending.TryAdd(key, 0))
        {
            var captured = associations.ToArray();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Reads.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var cfg = PolarionApiSettingsService.Load(paths);
                        var project = string.IsNullOrWhiteSpace(cfg.ProjectId) ? "TICKET_Tickets" : cfg.ProjectId;
                        var snapshots = new PolarionSnapshotStore(paths);
                        var history = new PolarionStatusHistoryService(paths);
                        var rows = PolarionReportService.Build(captured,
                            id => snapshots.Load(project, id),
                            id => history.ReadMerged(project, id),
                            new Dictionary<string, string>());
                        if (Cache.Count >= MaxCacheEntries) Cache.Clear();
                        Cache[key] = new Cached(rows, DateTimeOffset.UtcNow.AddSeconds(20));
                    }
                    catch (Exception)
                    {
                        // Keep a short negative cache so a missing/offline OneDrive share
                        // cannot spawn infinite retry loops as table rows are recycled.
                        Cache[key] = new Cached(Array.Empty<PolarionReportService.Row>(),
                            DateTimeOffset.UtcNow.AddSeconds(10));
                    }
                    finally { Reads.Release(); }
                }
                finally
                {
                    Pending.TryRemove(key, out _);
                    NotifyCacheUpdated();
                }
            });
        }
        return "Caricamento TICKET…";
    }

    /// <summary>
    /// Never invent a combined transition timestamp for several independently linked TICKET tickets.
    /// Concise cells show the first ticket and the number of additional links; tooltip lists ALL.
    /// </summary>
    public static string FormatRows(IReadOnlyList<PolarionReportService.Row> rows, string? field)
    {
        if (rows.Count == 0) return "—";
        if (field == "tooltip")
            return string.Join("\n", rows.Select(r => $"{r.TicketId} | {r.Status} / {r.Resolution} | " +
                $"Aperto: {r.CreatedText} | Testing: {r.TestingText} | Closed: {r.ClosedText} | {r.DataQuality}"));
        if (field == "status")
        {
            var statuses = rows.Select(r => string.IsNullOrWhiteSpace(r.Status) ? "Non sincronizzato" : r.Status)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return rows.Count == 1 ? statuses.First() : $"{rows.Count} TICKET · {string.Join(", ", statuses)}";
        }
        string Pick(PolarionReportService.Row row) => field switch
        {
            "created" => row.CreatedText,
            "testing" => row.TestingText,
            "closed" => row.ClosedText,
            _ => "—"
        };
        var first = rows[0];
        var text = Pick(first);
        return rows.Count == 1 ? text : $"{first.TicketId}: {text} (+{rows.Count - 1})";
    }
}
