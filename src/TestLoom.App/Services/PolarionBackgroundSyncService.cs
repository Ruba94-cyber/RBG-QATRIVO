using System.Net;
using System.Text.RegularExpressions;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

internal enum PolarionSyncStatus { NotConfigured, WorkspaceUnavailable, NoTickets, Deferred, Success, PartialFailure, Failed }

internal sealed record PolarionSyncResult(PolarionSyncStatus Status, int Total, int Refreshed,
    int Failed, int Skipped, HttpStatusCode? HttpStatus = null);

internal sealed record PolarionSyncProgress(int Completed, int Total, string TicketId);

/// <summary>
/// Background read-only TICKET sync. It is deliberately independent of TestLoom domain writes:
/// syncing can update the rebuildable snapshot cache only, never NC/CR workflow state.
/// No requests happen unless the Master has explicitly enabled Ticketing API reading.
/// </summary>
public sealed class PolarionBackgroundSyncService : IDisposable
{
    private static readonly Regex TicketId = new(@"(?<![A-Za-z0-9])TICKET-[0-9]+(?![0-9])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly AppState _state;
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private int _started;
    private readonly TimeSpan? _refreshIntervalOverride;
    private readonly Func<HttpClient> _httpFactory;

    public PolarionBackgroundSyncService(AppState state,
        TimeSpan? refreshInterval = null,
        Func<HttpClient>? httpFactory = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _refreshIntervalOverride = refreshInterval;
        if (refreshInterval is { } interval && interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        _httpFactory = httpFactory ?? PolarionReadClient.CreateWindowsProxyHttpClient;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        // Both shared-index enumeration and HTTP calls must remain off Avalonia's UI thread.
        _worker = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            await TryRefreshAsync(_stop.Token).ConfigureAwait(false);
            // Each PC can observe the shared cache locally. The shared schedule,
            // not this polling interval, determines when an HTTP pass is due.
            var poll = _refreshIntervalOverride is { } value && value < TimeSpan.FromMinutes(1)
                ? value : TimeSpan.FromMinutes(1);
            using var timer = new PeriodicTimer(poll);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                await TryRefreshAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Unexpected internal bugs must not prevent the rest of TestLoom from starting.
            // Never include the bearer token or HTTP authorization headers in diagnostics.
            CrashLogService.Registra("Ticketing background sync stopped unexpectedly", ex);
        }
    }

    private async Task TryRefreshAsync(CancellationToken token)
    {
        try { await RefreshOnceAsync(token: token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            DiagnosticTraceService.Registra("Ticketing.Sync", "pass-failed kind=" + ex.GetType().Name);
        }
    }

    /// <summary>One safe pass; exposed internally to enable bounded unit tests without timers.</summary>
    internal async Task<PolarionSyncResult> RefreshOnceAsync(bool force = false, CancellationToken token = default,
        IProgress<PolarionSyncProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        if (!_state.CartellaValida || _state.OperatoreCorrente is null)
            return new(PolarionSyncStatus.WorkspaceUnavailable, 0, 0, 0, 0);
        var paths = _state.Paths;
        if (paths is null) return new(PolarionSyncStatus.WorkspaceUnavailable, 0, 0, 0, 0);

        PolarionApiSettings settings;
        try { settings = PolarionApiSettingsService.Load(paths); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(PolarionSyncStatus.WorkspaceUnavailable, 0, 0, 0, 0); // Retain existing snapshots.
        }
        if (settings.ErroreCaricamento is not null || !settings.LetturaAbilitata || !settings.HaToken)
            return new(PolarionSyncStatus.NotConfigured, 0, 0, 0, 0);
        if (_refreshIntervalOverride is null && settings.IntervalloMinuti is < 1 or > 1440)
            return new(PolarionSyncStatus.NotConfigured, 0, 0, 0, 0);
        var refreshInterval = _refreshIntervalOverride ?? TimeSpan.FromMinutes(settings.IntervalloMinuti);

        // Enumerate and discard references before awaiting network calls.
        IReadOnlyList<string> ids;
        IReadOnlyDictionary<string, PolarionNotificationScopeService.Mode> scopes;
        try
        {
            var nc = _state.NonConformita.PeekAllReadOnly();
            var cr = _state.ChangeRequests.PeekAllReadOnly(); // archived CRs do not trigger polling
            ids = CollectTicketIds(nc, cr);
            // Work-item sync does not depend on subscriptions. Only the local unread
            // cursor is filtered, keeping records from other ranges silently updated.
            PolarionNotificationPreferences prefs;
            try
            {
                prefs = new PolarionNotificationPreferencesService(paths, _state.Config.OperatoreAttivo).Load();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                prefs = new PolarionNotificationPreferences(); // fail closed: no subscriptions
            }
            var gammaByVehicle = _state.Master.Veicoli.Load().Where(v => !v.Archiviata)
                .GroupBy(v => v.Nome, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().GammaNome, StringComparer.OrdinalIgnoreCase);
            scopes = PolarionNotificationScopeService.Build(nc, cr,
                testId => _state.TestCases.GetById(testId)?.VeicoliEffettivi?.ToList() ?? new List<string>(),
                prefs, gammaByVehicle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(PolarionSyncStatus.WorkspaceUnavailable, 0, 0, 0, 0);
        }
        if (ids.Count == 0) return new(PolarionSyncStatus.NoTickets, 0, 0, 0, 0);
        DiagnosticTraceService.Registra("Ticketing.Sync",
            "start linked-tickets=" + ids.Count + " force=" + force);
        var refreshed = 0;
        var skippedFresh = 0;
        var failed = 0;
        var processed = 0;
        HttpStatusCode? failureStatus = null;
        try
        {
            // Device-local read cursors never go to SharePoint or another operator's PC.
            var tracker = new PolarionLocalReadTracker(paths, _state.Config.OperatoreAttivo);
            var store = new PolarionSnapshotStore(paths, _state.PuoScrivere);
            // Every PC observes the shared snapshots for its own local unread cursor,
            // even if another PC owns the scheduled HTTP pass.
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var cached = store.Load(settings.ProjectId, id);
                    if (cached is not null)
                        tracker.Observe(cached, scopes.TryGetValue(id, out var mode)
                            ? mode : PolarionNotificationScopeService.Mode.Silent);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    DiagnosticTraceService.Registra("Ticketing.Sync", "cache-unavailable ticket=" + id + " kind=" + ex.GetType().Name);
                }
            }

            // A follower or read-only workspace must not contact Ticketing on a timer.
            // Explicit manual refresh remains independent of this best-effort schedule.
            if (!force && !_state.PuoScrivere) return new(PolarionSyncStatus.Deferred, ids.Count, 0, 0, 0);
            using var lease = force ? null : new PolarionSyncCoordinator(paths).TryBegin(refreshInterval, DateTimeOffset.UtcNow);
            if (!force && lease is null) return new(PolarionSyncStatus.Deferred, ids.Count, 0, 0, 0);
            using var linked = lease is null ? null : CancellationTokenSource.CreateLinkedTokenSource(token, lease.LostToken);
            var requestToken = linked?.Token ?? token;
            using var http = _httpFactory();
            var client = new PolarionReadClient(http, new Uri(settings.ApiRoot, UriKind.Absolute), settings.LeggiToken());
            foreach (var id in ids)
            {
                requestToken.ThrowIfCancellationRequested();
                progress?.Report(new(processed, ids.Count, id));
                try
                {
                    var notificationMode = scopes.TryGetValue(id, out var selected)
                        ? selected : PolarionNotificationScopeService.Mode.Silent;
                    // A recent snapshot from another PC avoids unnecessary calls from each replica.
                    // This is a bandwidth hint, NOT proof that a TestLoom workflow transition is safe.
                    try
                    {
                        var cached = store.Load(settings.ProjectId, id);
                        if (cached is not null)
                        {
                            if (!force && cached.RetrievedAtUtc <= DateTimeOffset.UtcNow &&
                                DateTimeOffset.UtcNow - cached.RetrievedAtUtc < refreshInterval)
                            {
                                skippedFresh++;
                                continue;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        // Damaged cache: do not replace it silently. Other tickets can still refresh.
                        failed++;
                        DiagnosticTraceService.Registra("Ticketing.Sync",
                            "cache-unavailable ticket=" + id + " kind=" + ex.GetType().Name);
                        continue;
                    }

                    try
                    {
                        if (lease is not null && !lease.ConfirmOwnership()) { failed++; break; }
                        var result = await new PolarionTicketRefreshService(client, store)
                            .RefreshAndStoreAsync(settings.ProjectId, id, requestToken).ConfigureAwait(false);
                        tracker.Observe(result.Snapshot, notificationMode);
                        if (result.CacheWritten) refreshed++;
                        else skippedFresh++;
                        // Shared refresh already records the same observation, including
                        // manual quick-edit refresh; never double-write history here.
                    }
                    catch (OperationCanceledException) when (lease is not null && lease.LostToken.IsCancellationRequested && !token.IsCancellationRequested)
                    {
                        failed++;
                        DiagnosticTraceService.Registra("Ticketing.Sync", "coordination-lost");
                        break;
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable)
                    {
                        failed++;
                        failureStatus = ex.StatusCode;
                        DiagnosticTraceService.Registra("Ticketing.Sync",
                            "paused ticket=" + id + " http=" + (int?)ex.StatusCode);
                        // Do not hammer the same account/proxy/instance for each remaining ticket.
                        break;
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    {
                        failed++;
                        failureStatus ??= ex.StatusCode;
                        DiagnosticTraceService.Registra("Ticketing.Sync",
                            "ticket-unavailable ticket=" + id + " http=" + (int?)ex.StatusCode);
                        // A single deleted or incorrect TICKET link must not starve other linked tickets.
                        continue;
                    }
                    catch (HttpRequestException ex)
                    {
                        failed++;
                        failureStatus ??= ex.StatusCode;
                        DiagnosticTraceService.Registra("Ticketing.Sync",
                            "transport-failed ticket=" + id + " http=" + (int?)ex.StatusCode);
                        // Transport failure: do not make more requests on a broken connection.
                        break;
                    }
                    catch (TaskCanceledException) when (!token.IsCancellationRequested)
                    {
                        failed++;
                        DiagnosticTraceService.Registra("Ticketing.Sync",
                            "timeout ticket=" + id);
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
                    {
                        failed++;
                        DiagnosticTraceService.Registra("Ticketing.Sync",
                            "ticket-failed ticket=" + id + " kind=" + ex.GetType().Name);
                        // A single bad response/cache must not stop refresh for unrelated tickets.
                    }
                }
                finally
                {
                    if (!token.IsCancellationRequested)
                    {
                        processed++;
                        progress?.Report(new(processed, ids.Count, id));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            failed++;
            DiagnosticTraceService.Registra("Ticketing.Sync", "pass-failed kind=" + ex.GetType().Name);
            // Invalid configuration / unavailable share. Existing TestLoom features remain usable.
        }
        finally
        {
            DiagnosticTraceService.Registra("Ticketing.Sync",
                "complete refreshed=" + refreshed + " fresh-cache=" + skippedFresh +
                " failed=" + failed + " total=" + ids.Count);
        }
        return new(failed == 0 && processed == ids.Count ? PolarionSyncStatus.Success
            : refreshed > 0 ? PolarionSyncStatus.PartialFailure : PolarionSyncStatus.Failed,
            ids.Count, refreshed, failed, skippedFresh, failureStatus);
    }

    internal static IReadOnlyList<string> CollectTicketIds(IEnumerable<NonConformita> nc, IEnumerable<ChangeRequest> cr)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in nc)
            foreach (var link in item.CollegamentiPolarion)
                Add(link.Codice, link.Url);
        foreach (var item in cr)
        {
            if (item.Archiviata) continue;
            foreach (var link in item.TicketCollegati)
                Add(link.Riferimento, link.Url);
        }
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        void Add(string? reference, string? url)
        {
            var match = TicketId.Match(reference ?? "");
            if (!match.Success) match = TicketId.Match(url ?? "");
            if (match.Success) result.Add(match.Value.ToUpperInvariant());
        }
    }

    public void Dispose()
    {
        // Cancellation is non-blocking. The worker owns its token for the remainder
        // of the asynchronous HTTP shutdown, so do not Dispose CTS here prematurely.
        _stop.Cancel();
        GC.SuppressFinalize(this);
    }
}
