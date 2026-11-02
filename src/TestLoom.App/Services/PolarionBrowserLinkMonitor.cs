using System.Net;
using Avalonia.Threading;

namespace TestLoom.App.Services;

/// <summary>
/// Polls only locally pending browser handoffs, never generates Ticketing work items.
/// A newly saved TICKET is linked ONLY after unique marker/type/date/assignee verification.
/// Runs on the Windows TestLoom PC without services, jobs or GitHub Actions.
/// </summary>
public sealed class PolarionBrowserLinkMonitor : IDisposable
{
    private readonly AppState _state;
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _interval;
    private Task? _worker;
    private int _started;
    private int _nextReceiptIndex;

    public PolarionBrowserLinkMonitor(AppState state, TimeSpan? interval = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _interval = interval ?? TimeSpan.FromMinutes(1);
        if (_interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _worker = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    await CheckOnceAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // One malformed receipt, temporary SharePoint failure or invalid
                    // settings must not stop reconciliation for the rest of the
                    // application lifetime. Do not log exception messages (they
                    // could contain endpoint URLs, raw responses or user data).
                    DiagnosticTraceService.Registra("Ticketing.BrowserMonitor",
                        "scan-failed kind=" + ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DiagnosticTraceService.Registra("Ticketing.BrowserMonitor",
                "worker-stopped kind=" + ex.GetType().Name);
            // Optional monitor must not interfere with other TestLoom functions.
        }
    }

    internal async Task CheckOnceAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!_state.CartellaValida || !_state.PuoScrivere || _state.OperatoreCorrente is null) return;
        var settings = PolarionApiSettingsService.Load(_state.Paths);
        if (settings.ErroreCaricamento is not null || !settings.LetturaAbilitata || !settings.HaToken) return;
        var pending = new PolarionCreationReceiptStore(_state.Paths).PendingBrowserReceipts()
            .Where(r => string.Equals(r.OperatorAtHandoff, _state.Config.OperatoreAttivo,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        // Rotating bounded scan: never starve a newly saved TICKET when more than
        // eight old browser drafts are pending in the same workspace.
        var receipts = SelectBatch(pending, ref _nextReceiptIndex);
        if (receipts.Count > 0)
            DiagnosticTraceService.Registra("Ticketing.BrowserMonitor",
                "scan batch=" + receipts.Count + " pending-total=" + pending.Count);
        foreach (var receipt in receipts)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var result = await PolarionBrowserHandoffService.TryReconcileAsync(_state, receipt, token)
                    .ConfigureAwait(false);
                if (result.Linked)
                {
                    DiagnosticTraceService.Registra("Ticketing.BrowserMonitor",
                        "verified-linked source=" + receipt.SourceId + " ticket=" + result.TicketId);
                    await Dispatcher.UIThread.InvokeAsync(_state.NotificaDatiRicaricati);
                }
                else if (!result.Pending)
                    DiagnosticTraceService.Registra("Ticketing.BrowserMonitor",
                        "completed-without-link source=" + receipt.SourceId);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.TooManyRequests or
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway)
            {
                DiagnosticTraceService.Registra("Ticketing.BrowserMonitor",
                    "paused source=" + receipt.SourceId + " http=" + (int?)ex.StatusCode);
                break; // Never hammer the instance on errors/limits.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or ArgumentException or HttpRequestException)
            {
                DiagnosticTraceService.Registra("Ticketing.BrowserMonitor",
                    "check-failed source=" + receipt.SourceId + " kind=" + ex.GetType().Name +
                    (ex is HttpRequestException httpError ? " http=" + (int?)httpError.StatusCode : ""));
                // Preserve receipt and the raw source. A later successful read can retry.
            }
        }
    }

    /// <summary>Fair, bounded round-robin selection independent of Ticketing availability.</summary>
    internal static IReadOnlyList<PolarionCreationReceiptStore.Receipt> SelectBatch(
        IReadOnlyList<PolarionCreationReceiptStore.Receipt> ordered,
        ref int nextIndex, int limit = 8)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        if (ordered.Count == 0)
        {
            nextIndex = 0;
            return Array.Empty<PolarionCreationReceiptStore.Receipt>();
        }
        var start = Math.Abs((long)nextIndex) % ordered.Count;
        var count = Math.Min(limit, ordered.Count);
        var selected = new List<PolarionCreationReceiptStore.Receipt>(count);
        for (var i = 0; i < count; i++)
            selected.Add(ordered[(int)((start + i) % ordered.Count)]);
        nextIndex = (int)((start + count) % ordered.Count);
        return selected;
    }

    public void Dispose()
    {
        _stop.Cancel();
        GC.SuppressFinalize(this);
    }
}
