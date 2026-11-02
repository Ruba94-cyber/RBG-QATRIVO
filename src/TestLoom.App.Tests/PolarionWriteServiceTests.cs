using System.Net;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Rest writes must remain impossible even if an old configuration claims they are enabled.</summary>
public sealed class PolarionWriteServiceTests
{
    private static PolarionWriteService.Draft Draft() => new("NC-0001", "Synthetic title",
        "Synthetic description", "bug", "P2", "Major");

    private sealed class TrapHandler(Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onSend();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }
    }

    [Fact]
    public async Task CreateCommentAndPatchNeverSendRestWrites()
    {
        var sends = 0;
        using var http = new HttpClient(new TrapHandler(() => sends++));
        var writer = new PolarionWriteService(null!, http);
        Assert.Throws<NotSupportedException>(() => writer.ValidateCreate(Draft(), true, true));
        await Assert.ThrowsAsync<NotSupportedException>(() => writer.CreateAsync(Draft(), true, true));
        await Assert.ThrowsAsync<NotSupportedException>(() => writer.AddCommentAsync("TICKET-123", "note", true, true));
        await Assert.ThrowsAsync<NotSupportedException>(() => writer.UpdateTextAsync("TICKET-123", "new title", null, true, true));
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task InvalidOrUnauthorizedDraftsAlsoNeverContactPolarion()
    {
        var sends = 0;
        using var http = new HttpClient(new TrapHandler(() => sends++));
        var writer = new PolarionWriteService(null!, http);
        Assert.Throws<UnauthorizedAccessException>(() => writer.ValidateCreate(Draft(), false, true));
        Assert.Throws<ArgumentException>(() => writer.ValidateCreate(
            new PolarionWriteService.Draft("NC-0001", "", "description", "bug"), true, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => writer.CreateAsync(Draft(), false, true));
        Assert.Equal(0, sends);
    }
}
