using System.Diagnostics;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

internal sealed class TestMonotonicClock
{
    private long _timestamp;

    internal LockLeaseObservationTracker CreateTracker(int capacity = 4096) =>
        new(() => Interlocked.Read(ref _timestamp), capacity);

    internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp,
        checked((long)Math.Ceiling(elapsed.TotalSeconds * Stopwatch.Frequency)));
}
