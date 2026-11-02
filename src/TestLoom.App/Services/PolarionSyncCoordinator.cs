namespace TestLoom.App.Services;

/// <summary>
/// Best-effort coordination of scheduled Ticketing reads in one shared workspace.
/// File locks and timestamps cannot guarantee exclusion across unsynchronized OneDrive
/// replicas; callers must not treat this as an authoritative distributed lock.
/// </summary>
internal sealed class PolarionSyncCoordinator
{
    private const string Owner = "Ticketing scheduled sync";
    private readonly DataPaths _paths;
    private readonly LockService _locks;
    internal string StatePath => Path.Combine(_paths.PolarionSnapshotDir, "sync-schedule.json");

    internal PolarionSyncCoordinator(DataPaths paths, LockService? locks = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        // A distinct identity per coordinator prevents LockService treating another
        // live sync instance on the same PC as this instance's abandoned session.
        _locks = locks ?? new LockService(Guid.NewGuid().ToString("N"));
    }

    private sealed class Schedule { public DateTimeOffset LastAttemptUtc { get; set; } }

    internal Lease? TryBegin(TimeSpan interval, DateTimeOffset nowUtc)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        try
        {
            WorkspaceWritePolicy.EnsureCanWrite(_paths, "Ticketing scheduled sync");
            Directory.CreateDirectory(_paths.PolarionSnapshotDir);
            var acquired = _locks.TryAcquire(StatePath, Owner);
            if (!acquired.Acquisito || acquired.DetenutoDa?.SessionId is not { Length: > 0 } session)
                return null;
            var lease = new Lease(_locks, StatePath, session);
            try
            {
                if (!lease.ConfirmOwnership()) { lease.Dispose(); return null; }
                if (File.Exists(StatePath))
                {
                    var schedule = JsonIo.TryRead<Schedule>(StatePath, out _);
                    if (schedule is null || schedule.LastAttemptUtc == default ||
                        schedule.LastAttemptUtc > nowUtc || nowUtc - schedule.LastAttemptUtc < interval)
                    {
                        lease.Dispose();
                        return null;
                    }
                }
                // Record the attempt BEFORE network access, including attempts that
                // later fail, are cancelled, or terminate with the process.
                JsonIo.WriteAtomic(StatePath, new Schedule { LastAttemptUtc = nowUtc });
                if (!lease.ConfirmOwnership()) { lease.Dispose(); return null; }
                lease.StartHeartbeat();
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            DiagnosticTraceService.Registra("Ticketing.Sync", "coordination-unavailable kind=" + ex.GetType().Name);
            return null;
        }
    }

    internal sealed class Lease : IDisposable
    {
        private readonly LockService _locks;
        private readonly string _path;
        private readonly string _session;
        private readonly CancellationTokenSource _lost = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly object _heartbeatGate = new();
        private int _disposed;
        internal CancellationToken LostToken => _lost.Token;

        internal Lease(LockService locks, string path, string session)
        { _locks = locks; _path = path; _session = session; }

        internal bool ConfirmOwnership()
        {
            lock (_heartbeatGate)
            {
                if (Volatile.Read(ref _disposed) != 0 || _lost.IsCancellationRequested) return false;
                if (_locks.Heartbeat(_path, Owner) == EsitoIo.Ok) return true;
                _lost.Cancel();
                return false;
            }
        }

        internal void StartHeartbeat() => _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(LockService.IntervalloHeartbeat);
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                    if (!ConfirmOwnership()) break;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception) { _lost.Cancel(); }
        });

        public void Dispose()
        {
            lock (_heartbeatGate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _stop.Cancel();
                _lost.Cancel();
                _locks.ReleaseIfSession(_path, _session);
            }
        }
    }
}
