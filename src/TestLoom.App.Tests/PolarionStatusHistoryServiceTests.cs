using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionStatusHistoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_EstaHistory_" + Guid.NewGuid().ToString("N"));
    private PolarionStatusHistoryService Store(string device) => new(new DataPaths(_root), device);
    private static PolarionTicketSnapshot Snap(string status, DateTimeOffset retrieved, string resolution = "") => new()
    {
        ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = status, Resolution = resolution,
        Created = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
        Updated = retrieved.AddSeconds(-10), RetrievedAtUtc = retrieved
    };

    [Fact]
    public void TracksOnlyDistinctObservedStatesAndDoesNotInventTestingHistory()
    {
        var service = Store("deviceA");
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        service.Observe(Snap("assigned", t0));
        service.Observe(Snap("assigned", t0.AddMinutes(1)));
        var before = service.ReadMerged("TICKET_Tickets", "TICKET-123")!;
        Assert.Single(before.Observations);
        Assert.Null(before.Observations.FirstOrDefault(o => o.Status == "testing"));
        service.Observe(Snap("testing", t0.AddMinutes(2)));
        service.Observe(Snap("closed", t0.AddMinutes(3), "done"));
        var report = service.ReadMerged("TICKET_Tickets", "TICKET-123")!;
        Assert.Equal(3, report.Observations.Count);
        Assert.Equal("testing", report.Observations[1].Status);
        Assert.Equal(t0.AddMinutes(2), report.Observations[1].FirstSeenUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), report.PolarionCreatedUtc);
    }

    [Fact]
    public void KeepsTheRealPolarionResolutionDateAndClearsItWhenTheTicketIsReopened()
    {
        var service = Store("deviceR");
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var resolved = new DateTimeOffset(2026, 2, 5, 9, 23, 47, TimeSpan.Zero);
        PolarionTicketSnapshot Closed(DateTimeOffset retrieved, DateTimeOffset? resolvedOn, string status) => new()
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = status, Resolution = resolvedOn is null ? "" : "done",
            Created = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero), ResolvedOn = resolvedOn,
            Updated = retrieved.AddSeconds(-10), RetrievedAtUtc = retrieved
        };
        service.Observe(Closed(t0, resolved, "closed"));
        Assert.Equal(resolved, service.ReadMerged("TICKET_Tickets", "TICKET-123")!.PolarionResolvedUtc);
        service.Observe(Closed(t0.AddMinutes(2), null, "assigned"));
        Assert.Null(service.ReadMerged("TICKET_Tickets", "TICKET-123")!.PolarionResolvedUtc);
    }

    [Fact]
    public void MergedResolutionDateComesFromTheDeviceThatObservedPolarionLast()
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var resolved = new DateTimeOffset(2026, 2, 5, 9, 23, 47, TimeSpan.Zero);
        PolarionTicketSnapshot S(DateTimeOffset retrieved, DateTimeOffset? resolvedOn, string status) => new()
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = status, Resolution = resolvedOn is null ? "" : "done",
            ResolvedOn = resolvedOn, Updated = retrieved.AddSeconds(-10), RetrievedAtUtc = retrieved
        };
        Store("deviceA").Observe(S(t0, resolved, "closed"));
        Store("deviceB").Observe(S(t0.AddMinutes(1), resolved, "closed"));
        Assert.Equal(resolved, Store("deviceA").ReadMerged("TICKET_Tickets", "TICKET-123")!.PolarionResolvedUtc);
    }

    [Fact]
    public void DifferentDevicesDoNotOverwriteEachOtherAndDuplicateObservationsMerge()
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        Store("deviceA").Observe(Snap("testing", t0));
        Store("deviceB").Observe(Snap("testing", t0.AddMinutes(1)));
        var merged = Store("deviceA").ReadMerged("TICKET_Tickets", "TICKET-123")!;
        Assert.Single(merged.Observations.Where(o => o.Status == "testing" && o.PolarionUpdatedUtc == t0.AddSeconds(-10)));
    }

    [Fact]
    public void ReportBatch_DirectorySnapshotRiutilizzata_ProduceLoStessoStoricoDelMergeStandard()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        Store("deviceA").Observe(Snap("assigned", now));
        Store("deviceB").Observe(Snap("testing", now.AddMinutes(2)));
        var service = Store("deviceA");
        var folders = service.SnapshotDeviceDirectories("TICKET_Tickets");
        Assert.Equal(2, folders.Count);
        var batch = service.ReadMerged("TICKET_Tickets", "TICKET-123", folders)!;
        var single = service.ReadMerged("TICKET_Tickets", "TICKET-123")!;
        Assert.Equal(single.Observations.Select(o => (o.Status, o.FirstSeenUtc)),
            batch.Observations.Select(o => (o.Status, o.FirstSeenUtc)));
        // Ogni nuovo refresh cattura nuovamente le cartelle; nessuna cache
        // persistente puo' occultare un device arrivato in seguito.
        Store("deviceC").Observe(Snap("closed", now.AddMinutes(4), "done"));
        Assert.Equal(3, service.SnapshotDeviceDirectories("TICKET_Tickets").Count);
        Assert.Equal(3, service.ReadMerged("TICKET_Tickets", "TICKET-123")!.Observations.Count);
    }

    [Fact]
    public void StoriaEstaJsonValidoMaListaOsservazioniNull_NonMascheraLaCorruzione()
    {
        var paths = new DataPaths(_root);
        var path = Path.Combine(paths.PolarionHistoryDir, "TICKET_Tickets", "corruptDevice", "TICKET-123.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {"ProjectId":"TICKET_Tickets","TicketId":"TICKET-123","DeviceId":"corruptDevice","Observations":null}
            """);
        Assert.Throws<InvalidDataException>(() => Store("deviceA").ReadMerged("TICKET_Tickets", "TICKET-123"));
    }

    [Fact]
    public void AStalePolarionRevisionMustNotCreateFalseReopening()
    {
        var store = Store("deviceC");
        var now = DateTimeOffset.UtcNow;
        store.Observe(Snap("closed", now.AddMinutes(-2)));
        // Fetched later, but source updated timestamp is older: stale cluster response.
        store.Observe(new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = "assigned",
            RetrievedAtUtc = now, Updated = now.AddMinutes(-10)
        });
        Assert.Single(store.ReadMerged("TICKET_Tickets", "TICKET-123")!.Observations);
    }

    [Fact]
    public void MergeRejectsStaleStateObservedOnAnotherDevice()
    {
        var now = DateTimeOffset.UtcNow;
        Store("deviceA").Observe(new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = "closed",
            Updated = now.AddMinutes(-2), RetrievedAtUtc = now.AddMinutes(-1)
        });
        Store("deviceB").Observe(new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = "assigned",
            Updated = now.AddMinutes(-10), RetrievedAtUtc = now
        });
        var merged = Store("deviceA").ReadMerged("TICKET_Tickets", "TICKET-123")!;
        Assert.Single(merged.Observations);
        Assert.Equal("closed", merged.Observations[0].Status);
    }

    [Fact]
    public void NonStatusEditsUpdateRevisionBarrierAndRejectStaleReopen()
    {
        var store = Store("revisionDevice");
        var now = DateTimeOffset.UtcNow;
        store.Observe(Snap("testing", now.AddMinutes(-10)));
        store.Observe(new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123",
            Status = "testing", RetrievedAtUtc = now.AddMinutes(-8),
            Updated = now.AddMinutes(-9)
        });
        Assert.Single(store.ReadMerged("TICKET_Tickets", "TICKET-123")!.Observations);
        Assert.Equal(now.AddMinutes(-9),
            store.ReadMerged("TICKET_Tickets", "TICKET-123")!.LastPolarionUpdatedUtc);
        store.Observe(new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123",
            Status = "assigned", RetrievedAtUtc = now.AddMinutes(-5),
            Updated = now.AddMinutes(-10)
        });
        Assert.Single(store.ReadMerged("TICKET_Tickets", "TICKET-123")!.Observations);
    }

    [Fact]
    public void ReadOnlyCacheCannotRecordManualStatusHistory()
    {
        var paths = new DataPaths(_root);
        new PolarionSnapshotStore(paths, sharedWriteAllowed: false)
            .RecordObservedStatus(Snap("testing", DateTimeOffset.UtcNow));
        Assert.Null(Store("readonlyDevice").ReadMerged("TICKET_Tickets", "TICKET-123"));
    }

    [Fact]
    public void ContradictoryStateAtSameRevisionIsNeverAddedToHistory()
    {
        var store = Store("contradictoryDevice");
        var now = DateTimeOffset.UtcNow.AddMinutes(-3);
        var first = Snap("assigned", now);
        store.Observe(first);
        Assert.Throws<InvalidDataException>(() => store.Observe(new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123",
            Status = "closed", Updated = first.Updated,
            RetrievedAtUtc = now.AddMinutes(1)
        }));
        Assert.Single(store.ReadMerged("TICKET_Tickets", "TICKET-123")!.Observations);
    }

    [Fact]
    public void ConflictingDevicesWithSameRevisionDoNotCreateFictitiousTransitions()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-3);
        Store("firstDevice").Observe(Snap("assigned", now));
        Store("secondDevice").Observe(new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = "closed",
            Updated = now.AddSeconds(-10), RetrievedAtUtc = now.AddMinutes(1)
        });
        Assert.Throws<InvalidDataException>(() =>
            Store("firstDevice").ReadMerged("TICKET_Tickets", "TICKET-123"));
    }

    [Fact]
    public void RejectsOlderSnapshotsAndPathTraversal()
    {
        var store = Store("deviceA");
        var now = DateTimeOffset.UtcNow;
        store.Observe(Snap("closed", now));
        store.Observe(Snap("new", now.AddMinutes(-3)));
        Assert.Single(store.ReadMerged("TICKET_Tickets", "TICKET-123")!.Observations);
        Assert.Throws<ArgumentException>(() => store.ReadMerged("TICKET_Tickets", "../TICKET-123"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
