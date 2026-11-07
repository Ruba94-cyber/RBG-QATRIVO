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
        // Il parametro "statusBrush" alimenta una proprieta' IBrush: mai restituire stringhe (un valore di tipo errato rompe il binding della riga).
        var perPennello = parameter?.ToString() == "statusBrush";
        if (values.Count < 2 || values[1] is not DataPaths paths) return perPennello ? NeutralBrush : "—";
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
                return perPennello ? NeutralBrush : "—";
        }

        if (associations.Count == 0) return perPennello ? NeutralBrush : "—";
        var ids = associations.Select(x => x.TicketId).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var key = paths.Root + "|" + string.Join("|", ids);
        var now = DateTimeOffset.UtcNow;
        if (Cache.TryGetValue(key, out var ready) && ready.Until > now)
            return parameter?.ToString() == "statusBrush"
                ? StatusBrush(ready.Rows)
                : FormatRows(ready.Rows, parameter?.ToString());

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
        return parameter?.ToString() == "statusBrush" ? NeutralBrush : "Caricamento TICKET…";
    }

    private static readonly Avalonia.Media.IBrush NeutralBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8A8F98"));
    private static readonly Avalonia.Media.IBrush OkBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2E9E5B"));
    private static readonly Avalonia.Media.IBrush WorkBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3B82F6"));
    private static readonly Avalonia.Media.IBrush OpenBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D97706"));
    private static readonly Avalonia.Media.IBrush StaleBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#DC2626"));

    /// <summary>Colore di evidenza dello stato TICKET: verde = chiuso/risolto/verificato, blu = in lavorazione/test,
    /// ambra = aperto/nuovo, grigio = non sincronizzato o sconosciuto, rosso = dati più vecchi di 24 h.
    /// Con più ticket collegati prevale lo stato meno avanzato (un solo ticket aperto rende la riga ambra).</summary>
    public static Avalonia.Media.IBrush StatusBrush(IReadOnlyList<PolarionReportService.Row> rows, DateTimeOffset? referenceUtc = null)
    {
        if (rows.Count == 0) return NeutralBrush;
        var now = referenceUtc ?? DateTimeOffset.UtcNow;
        if (rows.Any(r => r.HasCache && r.LastFetchedAt != default && now - r.LastFetchedAt > TimeSpan.FromHours(24)))
            return StaleBrush;
        static int Rank(PolarionReportService.Row r)
        {
            var t = (r.Status ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) return 0;
            if (t.Contains("clos") || t.Contains("chius") || t.Contains("resol") || t.Contains("verif") || t.Contains("done") || t.Contains("rejected") || t.Contains("rifiut")) return 3;
            if (t.Contains("test") || t.Contains("progress") || t.Contains("lavoraz") || t.Contains("review") || t.Contains("analy") || t.Contains("implement")) return 2;
            return 1;
        }
        return rows.Min(Rank) switch { 3 => OkBrush, 2 => WorkBrush, 1 => OpenBrush, _ => NeutralBrush };
    }

    /// <summary>
    /// Never invent a combined transition timestamp for several independently linked TICKET tickets.
    /// Concise cells show the first ticket and the number of additional links; tooltip lists ALL.
    /// </summary>
    public static string FormatRows(IReadOnlyList<PolarionReportService.Row> rows, string? field,
        DateTimeOffset? referenceUtc = null)
    {
        if (rows.Count == 0) return field == "commentCompact" ? "" : "—";
        var now = referenceUtc ?? DateTimeOffset.UtcNow;
        var stale = rows.Any(r => r.HasCache && r.LastFetchedAt != default
            && now - r.LastFetchedAt > TimeSpan.FromHours(24));
        if (field == "commentCompact")
        {
            var linked = rows.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.LatestCommentPreview));
            if (linked is null) return rows.Sum(r => r.CommentsCount) > 0
                ? LocalizationService.Instance.Scegli("💬 Commenti disponibili", "💬 Comments available", "💬 Commentaires disponibles")
                : "";
            var preview = linked.LatestCommentPreview;
            if (preview.Length > 90) preview = preview[..87] + "…";
            return (rows.Count > 1 ? linked.TicketId + " · " : "") + "💬 " + preview;
        }
        if (field == "tooltip")
            return string.Join("\n\n", rows.Select(r =>
            {
                var status = !string.IsNullOrWhiteSpace(r.Status) ? r.Status :
                    r.HasCache ? LocalizationService.Instance.Scegli("Stato sconosciuto", "Unknown status", "Statut inconnu") :
                        LocalizationService.Instance.Scegli("Non sincronizzato", "Not synchronized", "Non synchronisé");
                var resolution = string.IsNullOrWhiteSpace(r.Resolution)
                    ? LocalizationService.Instance.Scegli("Non indicata", "Not specified", "Non précisée") : r.Resolution;
                var commenti = LocalizationService.Instance.Scegli("Commenti", "Comments", "Commentaires");
                var ultimaNota = LocalizationService.Instance.Scegli("Ultimo commento", "Latest comment", "Dernier commentaire");
                var titolo = string.IsNullOrWhiteSpace(r.Title) ? "" : "\n" + r.Title;
                var nota = string.IsNullOrWhiteSpace(r.LatestCommentPreview) ? ""
                    : $"\n{ultimaNota}" +
                        (string.IsNullOrWhiteSpace(r.LatestCommentAuthor) ? "" : $" ({r.LatestCommentAuthor})") +
                        $": {r.LatestCommentPreview}";
                return $"{r.TicketId} — {status}{titolo}\n" +
                    $"{LocalizationService.Instance.Scegli("Risoluzione", "Resolution", "Résolution")}: {resolution}\n" +
                    $"{commenti}: {r.CommentsCount}{nota}\n" +
                    $"{LocalizationService.Instance.Scegli("Apertura", "Created", "Ouvert")}: {r.CreatedText}\n" +
                    $"Testing: {r.TestingText}\nClosed: {r.ClosedText}\n{r.DataQuality}";
            }));
        if (field == "status")
        {
            // Una cache presente con stato mancante NON equivale a un ticket
            // mai sincronizzato: distinguerli è essenziale durante il triage TICKET.
            var statuses = rows.Select(r => !string.IsNullOrWhiteSpace(r.Status) ? r.Status
                : r.HasCache
                    ? LocalizationService.Instance.Scegli("Stato sconosciuto", "Unknown status", "Statut inconnu")
                    : LocalizationService.Instance.Scegli("Non sincronizzato", "Not synchronized", "Non synchronisé"))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var statusLabel = (rows.Count == 1 ? statuses.First() : $"{rows.Count} TICKET · {string.Join(", ", statuses)}");
            if (rows.Count == 1 && !string.IsNullOrWhiteSpace(rows[0].Resolution)
                && !string.Equals(rows[0].Resolution, rows[0].Status, StringComparison.OrdinalIgnoreCase))
                statusLabel += $" / {rows[0].Resolution}";
            var commenti = rows.Sum(r => r.CommentsCount);
            if (commenti > 0) statusLabel += $" · 💬 {commenti}";
            // La colonna e' stretta e tronca i testi: il warning di freschezza deve precedere
            // lo stato, non restare invisibile in fondo a un label con risoluzione/commenti.
            if (stale) statusLabel = "⚠ >24h · " + statusLabel;
            return statusLabel;
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
