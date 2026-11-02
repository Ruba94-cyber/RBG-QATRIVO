using System.Net;
using System.Net.Http;
using System.Text;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionConnectionProbeTests
{
    private const string Project = "TICKET_Tickets";
    private static readonly Uri Root = new("https://example.invalid/ticketing/rest/v1");

    [Fact]
    public async Task ProbeUsesOnlyAuthenticatedGetAndDoesNotNeedAnExistingTicket()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/ticketing/rest/v1/projects/TICKET_Tickets/workitems", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json("""{"data":[]}"""));
        }));
        await new PolarionReadClient(http, Root, "synthetic-token").ProbeProjectAsync(Project);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ProbeReportsHttpFailureWithoutRevealingToken(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new PolarionReadClient(http, Root, "synthetic-secret").ProbeProjectAsync(Project));
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    [Fact]
    public async Task ProbeRefusesSuccessfulHttpWithWrongResponseShape()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json("""{"data":{}}"""))));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PolarionReadClient(http, Root, "synthetic-token").ProbeProjectAsync(Project));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("<html>not an API response</html>")]
    [InlineData("{\"data\":[],\"meta\":{\"errors\":[{\"title\":\"partial\"}]}}")]
    public async Task ProbeRefusesMalformedOrPartialSuccessfulResponse(string body)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(body))));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PolarionReadClient(http, Root, "synthetic-token").ProbeProjectAsync(Project));
    }

    [Fact]
    public async Task ProbeRejectsInvalidProjectWithoutSendingRequest()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Json("""{"data":[]}"""));
        }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new PolarionReadClient(http, Root, "synthetic-token").ProbeProjectAsync("../admin"));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ProbePropagatesTimeoutAndCancellation()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new TaskCanceledException("synthetic timeout")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PolarionReadClient(http, Root, "synthetic-token").ProbeProjectAsync(Project));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var cancelHttp = new HttpClient(new Handler((_, token) =>
            Task.FromCanceled<HttpResponseMessage>(token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PolarionReadClient(cancelHttp, Root, "synthetic-token").ProbeProjectAsync(Project, cts.Token));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
}
