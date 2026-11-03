using System.Text.Json;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionSyncCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom-SyntheticSyncLease-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task ConcurrentLeaseConfirmationsDoNotLoseOwnership()
    {
        Directory.CreateDirectory(_root);
        using var lease = new PolarionSyncCoordinator(new DataPaths(_root)).TryBegin(Interval, DateTimeOffset.UtcNow);
        Assert.NotNull(lease);
        // Dedicated threads avoid starving LockService's bounded I/O worker while
        // still exercising concurrent callers against the same lease.
        var checks = Enumerable.Range(0, 20).Select(_ => Task.Factory.StartNew(
            () => lease!.ConfirmOwnership(), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default));
        Assert.All(await Task.WhenAll(checks), Assert.True);
        Assert.False(lease!.LostToken.IsCancellationRequested);
    }

    [Fact]
    public void IncompletePassBringsTheNextAttemptForward()
    {
        Directory.CreateDirectory(_root);
        var paths = new DataPaths(_root);
        var now = DateTimeOffset.UtcNow;
        var peer = new PolarionSyncCoordinator(paths);
        using (var lease = new PolarionSyncCoordinator(paths).TryBegin(Interval, now))
        {
            Assert.NotNull(lease);
            lease!.ScheduleRetry(Interval, TimeSpan.FromMinutes(5));
        }
        Assert.Null(peer.TryBegin(Interval, DateTimeOffset.UtcNow.AddMinutes(1)));
        using var retry = peer.TryBegin(Interval, DateTimeOffset.UtcNow.AddMinutes(6));
        Assert.NotNull(retry);
    }

    [Fact]
    public async Task TwoWorkerConfirmationsPreserveOwnership()
    {
        Directory.CreateDirectory(_root);
        using var lease = new PolarionSyncCoordinator(new DataPaths(_root)).TryBegin(Interval, DateTimeOffset.UtcNow);
        Assert.NotNull(lease);
        using var start = new ManualResetEventSlim(false);
        var checks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return lease!.ConfirmOwnership();
        })).ToArray();
        start.Set();
        Assert.All(await Task.WhenAll(checks), Assert.True);
    }

    [Fact]
    public async Task ConcurrentPeersHaveOneOwnerAndShareCooldownAfterRelease()
    {
        Directory.CreateDirectory(_root);
        var paths = new DataPaths(_root);
        var a = new PolarionSyncCoordinator(paths);
        var b = new PolarionSyncCoordinator(paths);
        var now = DateTimeOffset.UtcNow;
        using var gate = new ManualResetEventSlim(false);
        var tasks = new[] { a, b }.Select(c => Task.Run(() => { gate.Wait(); return c.TryBegin(Interval, now); })).ToArray();
        gate.Set();
        var leases = await Task.WhenAll(tasks);
        var owner = Assert.Single(leases, l => l is not null)!;
        try { Assert.True(owner.ConfirmOwnership()); }
        finally { owner.Dispose(); }
        Assert.Null(b.TryBegin(Interval, now.AddMinutes(1)));
        using var next = b.TryBegin(Interval, now.Add(Interval).AddSeconds(1));
        Assert.NotNull(next);
        Assert.True(next!.ConfirmOwnership());
    }

    [Fact]
    public void MalformedOrFutureScheduleFailsClosedAndIsPreserved()
    {
        var coordinator = new PolarionSyncCoordinator(new DataPaths(_root));
        Directory.CreateDirectory(Path.GetDirectoryName(coordinator.StatePath)!);
        File.WriteAllText(coordinator.StatePath, "{broken");
        Assert.Null(coordinator.TryBegin(Interval, DateTimeOffset.UtcNow));
        Assert.Equal("{broken", File.ReadAllText(coordinator.StatePath));
        var now = DateTimeOffset.UtcNow;
        var future = JsonSerializer.Serialize(new { LastAttemptUtc = now.AddHours(1) });
        File.WriteAllText(coordinator.StatePath, future);
        Assert.Null(coordinator.TryBegin(Interval, now));
        Assert.Equal(future, File.ReadAllText(coordinator.StatePath));
    }

    [Fact]
    public void AbandonedPeerCanBeReplacedOnlyAfterConservativeLeaseObservation()
    {
        var clock = new TestMonotonicClock();
        var coordinator = new PolarionSyncCoordinator(new DataPaths(_root),
            new LockService("synthetic-recovering-pc", clock.CreateTracker()));
        var lockPath = LockService.LockPathFor(coordinator.StatePath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var stale = new LockInfo
        {
            Operatore = "synthetic-old-owner", DeviceId = "synthetic-old-pc",
            SessionId = Guid.NewGuid().ToString("N"),
            AcquisitoIl = DateTime.Now - LockService.Timeout - TimeSpan.FromMinutes(1),
            UltimoHeartbeat = DateTime.Now - LockService.Timeout - TimeSpan.FromMinutes(1)
        };
        File.WriteAllText(lockPath, JsonSerializer.Serialize(stale, JsonIo.Options));
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - LockService.Timeout - TimeSpan.FromMinutes(1));
        var now = DateTimeOffset.UtcNow;
        Assert.Null(coordinator.TryBegin(Interval, now));
        clock.Advance(LockService.Timeout + TimeSpan.FromSeconds(1));
        using var recovered = coordinator.TryBegin(Interval, now.AddSeconds(1));
        Assert.NotNull(recovered);
        Assert.True(recovered!.ConfirmOwnership());
    }

    [Fact]
    public void DisconnectedOneDriveReplicasCanBothElectAnOwner()
    {
        var a = Path.Combine(_root, "replica-a");
        var b = Path.Combine(_root, "replica-b");
        _ = new OneDriveFolderSyncSimulator(a, b, seed: 1);
        var now = DateTimeOffset.UtcNow;
        using var first = new PolarionSyncCoordinator(new DataPaths(a)).TryBegin(Interval, now);
        using var second = new PolarionSyncCoordinator(new DataPaths(b)).TryBegin(Interval, now);
        Assert.NotNull(first);
        Assert.NotNull(second); // No shared atomic primitive across unsynchronized replicas.
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
