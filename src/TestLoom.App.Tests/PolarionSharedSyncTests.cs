using System.Net;
using System.Net.Http;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionSharedSyncTests
{
    [Fact]
    public void SamePolarionRevisionWithContradictoryStatus_DoesNotOverwriteSharedCache()
    {
        using var fixture = new SyncFixture();
        var cache = new PolarionSnapshotStore(fixture.First.Paths);
        var retrieved = DateTimeOffset.UtcNow.AddMinutes(-5);
        var updated = retrieved.AddSeconds(-30);
        var initial = new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = "testing",
            Updated = updated, RetrievedAtUtc = retrieved
        };
        Assert.True(cache.SaveIfNewer(initial));
        var contradictory = new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = "closed",
            Resolution = "done", Updated = updated, RetrievedAtUtc = retrieved.AddMinutes(1)
        };
        Assert.Throws<InvalidDataException>(() => cache.SaveIfNewer(contradictory));
        Assert.Equal("testing", cache.Load("TICKET_Tickets", "TICKET-123")!.Status);
        Assert.Equal(updated, cache.Load("TICKET_Tickets", "TICKET-123")!.Updated);
    }

    [Fact]
    public void ReportEsta_IsolaCacheJsonConListaCommentiNull_SenzaBloccareGliAltriTicket()
    {
        using var fixture = new SyncFixture();
        var dir = Path.Combine(fixture.First.Paths.PolarionSnapshotDir, "TICKET_Tickets");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "TICKET-123.json"),
            """{"ProjectId":"TICKET_Tickets","Id":"TICKET-123","Comments":null}""");
        var store = new PolarionSnapshotStore(fixture.First.Paths);
        Assert.Throws<InvalidDataException>(() => store.Load("TICKET_Tickets", "TICKET-123"));
        var rows = PolarionReportService.Build(
            [new PolarionReportService.Association("TICKET-123", "NC", "NC-1", [])],
            id => store.Load("TICKET_Tickets", id), _ => null,
            new Dictionary<string, string>());
        var row = Assert.Single(rows);
        Assert.False(row.HasCache);
        Assert.Equal("TICKET-123", row.TicketId);
    }

    [Fact]
    public async Task ReadOnlyWorkspaceDoesNotPollPolarionAutomatically()
    {
        using var fixture = new SyncFixture();
        var calls = 0;
        using var sync = new PolarionBackgroundSyncService(fixture.First, httpFactory: () =>
            new HttpClient(new Handler((_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Json("""{"data":[]}"""));
            })));
        WorkspaceReadOnlyService.SetEnabled(fixture.First.Paths.Root, true);
        try { await sync.RefreshOnceAsync(); }
        finally { WorkspaceReadOnlyService.SetEnabled(fixture.First.Paths.Root, false); }
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExplicitManualRefreshRemainsAvailableDuringScheduledCooldown()
    {
        using var fixture = new SyncFixture();
        var calls = 0;
        using var sync = new PolarionBackgroundSyncService(fixture.First, httpFactory: () =>
            new HttpClient(new Handler((request, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal)
                    ? Json("""{"data":[]}""")
                    : Json("""{"data":{"id":"TICKET_Tickets/TICKET-123","attributes":{"status":"testing"}}}"""));
            })));
        await sync.RefreshOnceAsync();
        await sync.RefreshOnceAsync(force: true);
        Assert.Equal(4, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task FailedAttemptIsSharedAndDoesNotTriggerImmediateRetryOnAnotherPc(HttpStatusCode status)
    {
        using var fixture = new SyncFixture();
        var calls = 0;
        using var first = new PolarionBackgroundSyncService(fixture.First, httpFactory: () =>
            new HttpClient(new Handler((request, _) =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Interlocked.Increment(ref calls);
                return Task.FromResult(new HttpResponseMessage(status));
            })));
        using var second = new PolarionBackgroundSyncService(fixture.Second, httpFactory: () =>
            new HttpClient(new Handler((_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Json("""{"data":[]}"""));
            })));
        await first.RefreshOnceAsync();
        await second.RefreshOnceAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConcurrentPeersMakeOneHttpPassAndFollowerObservesSharedCache()
    {
        using var fixture = new SyncFixture();
        var followerTracker = new PolarionLocalReadTracker(fixture.Second.Paths, "SyntheticB");
        followerTracker.Observe(new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = "assigned",
            Updated = DateTimeOffset.UtcNow.AddMinutes(-2), RetrievedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        new PolarionNotificationPreferencesService(fixture.Second.Paths, "SyntheticB")
            .Save(new PolarionNotificationPreferences { IncludiNonAssociati = true });

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var leader = new PolarionBackgroundSyncService(fixture.First, httpFactory: () =>
            new HttpClient(new Handler(async (request, token) =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Interlocked.Increment(ref calls);
                if (!request.RequestUri!.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal))
                {
                    entered.TrySetResult();
                    await unblock.Task.WaitAsync(token);
                    return Json("""{"data":{"id":"TICKET_Tickets/TICKET-123","attributes":{"status":"testing","updated":"2090-01-01T00:00:00Z"}}}""");
                }
                return Json("""{"data":[]}""");
            })));
        var followerCalls = 0;
        using var follower = new PolarionBackgroundSyncService(fixture.Second, httpFactory: () =>
            new HttpClient(new Handler((_, _) =>
            {
                Interlocked.Increment(ref followerCalls);
                return Task.FromResult(Json("""{"data":[]}"""));
            })));

        var first = leader.RefreshOnceAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await follower.RefreshOnceAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, Volatile.Read(ref followerCalls));
        }
        finally { unblock.TrySetResult(); }
        await first.WaitAsync(TimeSpan.FromSeconds(20));
        await follower.RefreshOnceAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(2, calls); // Work item + comments, both GET.
        Assert.Equal(0, followerCalls);
        Assert.True(followerTracker.GetUnread("TICKET-123").Fields);
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            respond(request, token);
    }

    private sealed class SyncFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom-SyntheticSharedSync-" + Guid.NewGuid().ToString("N"));
        public AppState First { get; }
        public AppState Second { get; }

        public SyncFixture()
        {
            Directory.CreateDirectory(_root);
            First = TestStateFactory.Create();
            First.InizializzaCartella(_root);
            Assert.True(First.InizializzazioneWorkspaceCompletata);
            var a = TestOperatori.Sigilla(new Operatore { Nome = "SyntheticA", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
            var b = TestOperatori.Sigilla(new Operatore { Nome = "SyntheticB", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
            First.Master.Operatori.Save([a, b]);
            Assert.True(First.ImpostaOperatore(a.Nome, "1234"));
            var settings = PolarionApiSettingsService.Load(First.Paths);
            settings.ApiRoot = "https://example.invalid/ticketing/rest/v1";
            settings.LetturaAbilitata = true;
            settings.ImpostaToken("synthetic-token");
            PolarionApiSettingsService.Save(First.Paths, settings, true);
            First.NonConformita.Save(new NonConformita
            {
                Id = "NC-1", DataApertura = DateTime.Now,
                CollegamentiPolarion = [new CollegamentoPolarion { Codice = "TICKET-123" }]
            });
            Second = TestStateFactory.Create();
            Second.InizializzaCartella(_root);
            Assert.True(Second.InizializzazioneWorkspaceCompletata);
            Assert.True(Second.ImpostaOperatore(b.Nome, "1234"));
        }

        public void Dispose()
        {
            Second.Dispose();
            First.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
