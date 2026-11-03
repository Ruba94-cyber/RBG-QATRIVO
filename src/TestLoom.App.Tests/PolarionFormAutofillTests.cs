using System.Text.Json;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class PolarionFormAutofillTests
{
    private sealed class FakeSession(Func<string, string> evaluate) : ICdpSession
    {
        public List<string> Methods { get; } = new();
        public int Evaluations { get; private set; }
        public Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct)
        {
            Methods.Add(method);
            if (method != "Runtime.evaluate") return Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
            Evaluations++;
            var expression = (string)parameters!.GetType().GetProperty("expression")!.GetValue(parameters)!;
            var json = JsonSerializer.Serialize(new { result = new { type = "string", value = evaluate(expression) } });
            return Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly Uri Form = new("https://ticketing.example.invalid/ticketing/#/project/TICKET_Tickets/workitem?form_mode=create&form_field_type=TICKET_Ticket");
    private const string Marker = "TestLoomREF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task WaitsUntilTheFormExistsThenReportsFilled()
    {
        var calls = 0;
        var session = new FakeSession(_ => ++calls < 3
            ? "{\"found\":false,\"title\":false,\"description\":false}"
            : "{\"found\":true,\"title\":true,\"description\":true}");
        var result = await PolarionFormAutofill.FillAsync(session, Form, "[Daily] titolo", "desc " + Marker,
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5), CancellationToken.None);
        Assert.Equal(AutofillOutcome.Filled, result.Outcome);
        Assert.Equal("Page.navigate", session.Methods[0]);
        Assert.Equal(3, session.Evaluations);
    }

    [Fact]
    public async Task RequestsTheLinkAgainWhenPolarionShowedThePreviousPage()
    {
        var calls = 0;
        var session = new FakeSession(_ => ++calls < 3
            ? "{\"found\":false,\"title\":false,\"description\":false,\"href\":\"https://ticketing.example.invalid/ticketing/#/project/TICKET_Tickets/workitems\"}"
            : "{\"found\":true,\"title\":true,\"description\":true,\"href\":\"x\"}");
        var result = await PolarionFormAutofill.FillAsync(session, Form, "t", "d " + Marker,
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5), CancellationToken.None,
            renavigateAfter: TimeSpan.Zero);
        Assert.Equal(AutofillOutcome.Filled, result.Outcome);
        Assert.True(session.Methods.Count(m => m == "Page.navigate") >= 2);
    }

    [Fact]
    public async Task NeverRenavigatesAwayFromALoginPageOnAnotherHost()
    {
        var session = new FakeSession(_ =>
            "{\"found\":false,\"title\":false,\"description\":false,\"href\":\"https://login.example.invalid/signin\"}");
        await PolarionFormAutofill.FillAsync(session, Form, "t", "d " + Marker,
            TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(5), CancellationToken.None,
            renavigateAfter: TimeSpan.Zero);
        Assert.Single(session.Methods, m => m == "Page.navigate");
    }

    [Fact]
    public async Task NeverRenavigatesAwayFromAPolarionLoginPageOnTheSameHost()
    {
        var session = new FakeSession(_ =>
            "{\"found\":false,\"title\":false,\"description\":false,\"href\":\"https://ticketing.example.invalid/ticketing/login/\"}");
        await PolarionFormAutofill.FillAsync(session, Form, "t", "d " + Marker,
            TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(5), CancellationToken.None,
            renavigateAfter: TimeSpan.Zero);
        Assert.Single(session.Methods, m => m == "Page.navigate");
    }

    [Fact]
    public async Task ReportsOpenedNotFilledWhenTheFormNeverAppearsWithinTimeout()
    {
        var session = new FakeSession(_ => "{\"found\":false,\"title\":false,\"description\":false}");
        var result = await PolarionFormAutofill.FillAsync(session, Form, "t", "d " + Marker,
            TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(5), CancellationToken.None);
        Assert.Equal(AutofillOutcome.OpenedNotFilled, result.Outcome);
        Assert.Equal("form-not-found", result.Detail);
    }

    [Fact]
    public async Task PartialFillIsNeverReportedAsFilled()
    {
        var session = new FakeSession(_ => "{\"found\":true,\"title\":true,\"description\":false}");
        var result = await PolarionFormAutofill.FillAsync(session, Form, "t", "d " + Marker,
            TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(5), CancellationToken.None);
        Assert.Equal(AutofillOutcome.OpenedNotFilled, result.Outcome);
        Assert.Equal("title=True description=False", result.Detail);
    }

    [Fact]
    public async Task GarbageScriptResultIsTreatedAsNotFilled()
    {
        var session = new FakeSession(_ => "not json");
        var result = await PolarionFormAutofill.FillAsync(session, Form, "t", "d",
            TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(5), CancellationToken.None);
        Assert.Equal(AutofillOutcome.OpenedNotFilled, result.Outcome);
    }

    [Fact]
    public async Task CancellationStopsWaitingForTheForm()
    {
        var session = new FakeSession(_ => "{\"found\":false,\"title\":false,\"description\":false}");
        using var cts = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PolarionFormAutofill.FillAsync(
            session, Form, "t", "d", TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(5), cts.Token));
    }

    [Fact]
    public void FillScriptCarriesValuesAsEscapedJsonLiteralsNotRawCode()
    {
        const string title = "a\"); alert(1);//";
        var description = "line1\nline2 " + Marker;
        var script = PolarionFormAutofill.BuildFillScript(title, description);
        Assert.EndsWith("(" + JsonSerializer.Serialize(title) + "," + JsonSerializer.Serialize(description) + ")", script);
        Assert.DoesNotContain(title, script);
        Assert.DoesNotContain("\n", script.Substring(script.LastIndexOf("})(", StringComparison.Ordinal)));
    }
}
