using System.Net;
using System.Net.Http;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionRefreshResultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_SyntheticRefresh_" + Guid.NewGuid().ToString("N"));
    private readonly AppState _state;
    public PolarionRefreshResultTests()
    {
        Directory.CreateDirectory(_root);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_root);
        Assert.True(_state.InizializzazioneWorkspaceCompletata);
        var master = TestOperatori.Sigilla(new Operatore
        {
            Nome = "Synthetic Master", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234")
        });
        _state.Master.Operatori.Save([master]);
        Assert.True(_state.ImpostaOperatore(master.Nome, "1234"));
        var settings = PolarionApiSettingsService.Load(_state.Paths);
        settings.ApiRoot = "https://example.invalid/ticketing/rest/v1";
        settings.LetturaAbilitata = true;
        settings.ImpostaToken("synthetic-token");
        PolarionApiSettingsService.Save(_state.Paths, settings, true);
    }

    private void AddLinks(params string[] ids)
    {
        _state.NonConformita.Save(new NonConformita
        {
            Id = "NC-1", DataApertura = DateTime.Now,
            CollegamentiPolarion = ids.Select(id => new CollegamentoPolarion { Codice = id }).ToList()
        });
    }

    [Fact]
    public async Task NoTicketsIsNotReportedAsSuccessfulNetworkRefresh()
    {
        var calls = 0;
        using var sync = new PolarionBackgroundSyncService(_state, httpFactory: () =>
            new HttpClient(new Handler((_, _) =>
            {
                calls++;
                return Task.FromResult(Json("""{"data":[]}"""));
            })));
        var result = await sync.RefreshOnceAsync(force: true);
        Assert.Equal(PolarionSyncStatus.NoTickets, result.Status);
        Assert.Equal(0, result.Total);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PartialFailureAndProgressReflectActualTicketOutcomes()
    {
        AddLinks("TICKET-123", "TICKET-124");
        var reports = new List<PolarionSyncProgress>();
        var calls = 0;
        using var sync = new PolarionBackgroundSyncService(_state, httpFactory: () =>
            new HttpClient(new Handler((request, _) =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                calls++;
                if (request.RequestUri!.AbsolutePath.EndsWith("/TICKET-123", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal)
                    ? Json("""{"data":[]}""")
                    : Json("""{"data":{"id":"TICKET_Tickets/TICKET-124","attributes":{"status":"testing"}}}"""));
            })));
        var result = await sync.RefreshOnceAsync(force: true, progress: new ImmediateProgress<PolarionSyncProgress>(reports.Add));
        Assert.Equal(PolarionSyncStatus.PartialFailure, result.Status);
        Assert.Equal(2, result.Total);
        Assert.Equal(1, result.Refreshed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(HttpStatusCode.NotFound, result.HttpStatus);
        Assert.Equal(3, calls);
        Assert.NotEmpty(reports);
        Assert.Equal(2, reports[^1].Total);
        Assert.Equal(2, reports[^1].Completed);
        Assert.Equal("TICKET-124", reports[^1].TicketId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpFailureIsReportedAndDoesNotClaimCompletedRefresh(HttpStatusCode status)
    {
        AddLinks("TICKET-123", "TICKET-124");
        var calls = 0;
        using var sync = new PolarionBackgroundSyncService(_state, httpFactory: () =>
            new HttpClient(new Handler((_, _) =>
            {
                calls++;
                return Task.FromResult(new HttpResponseMessage(status));
            })));
        var result = await sync.RefreshOnceAsync(force: true);
        Assert.Equal(PolarionSyncStatus.Failed, result.Status);
        Assert.Equal(0, result.Refreshed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(status, result.HttpStatus);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationStopsRequestAndPreservesExistingCache()
    {
        AddLinks("TICKET-123");
        var cache = new PolarionSnapshotStore(_state.Paths);
        cache.SaveIfNewer(new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = "assigned",
            RetrievedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30)
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sync = new PolarionBackgroundSyncService(_state, httpFactory: () =>
            new HttpClient(new Handler(async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Json("""{"data":[]}""");
            })));
        using var cancel = new CancellationTokenSource();
        var task = sync.RefreshOnceAsync(force: true, token: cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal("assigned", cache.Load("TICKET_Tickets", "TICKET-123")!.Status);
    }

    [Fact]
    public async Task ForceRefreshInReadOnlyWorkspaceCannotReportSuccessfulPersistence()
    {
        AddLinks("TICKET-123");
        var cache = new PolarionSnapshotStore(_state.Paths);
        cache.SaveIfNewer(new PolarionTicketSnapshot
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-123", Status = "assigned",
            RetrievedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30)
        });
        var calls = 0;
        using var sync = new PolarionBackgroundSyncService(_state, httpFactory: () =>
            new HttpClient(new Handler((_, _) =>
            {
                calls++;
                return Task.FromResult(Json("""{"data":[]}"""));
            })));
        WorkspaceReadOnlyService.SetEnabled(_state.Paths.Root, true);
        try
        {
            var result = await sync.RefreshOnceAsync(force: true);
            Assert.NotEqual(PolarionSyncStatus.Success, result.Status);
            Assert.Equal(0, result.Refreshed);
            Assert.Equal(0, calls);
            Assert.Equal("assigned", cache.Load("TICKET_Tickets", "TICKET-123")!.Status);
        }
        finally { WorkspaceReadOnlyService.SetEnabled(_state.Paths.Root, false); }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token); }

    public void Dispose()
    {
        _state.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
