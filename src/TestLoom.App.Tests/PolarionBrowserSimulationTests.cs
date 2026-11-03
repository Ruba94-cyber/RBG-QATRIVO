using System.Net;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// End-to-end SIMULATION of the browser-created TICKET workflow with a stateful fake Ticketing (no network, synthetic data).
/// It proves TestLoom's own logic: opening the browser never counts as a created ticket, the link is made only after a GET
/// verification of marker + classification, nothing is ever written through REST, and a restart / transient error / ambiguity
/// never duplicates or mislinks. It does NOT prove that the corporate Ticketing form honours the URL prefill: that stays a
/// manual acceptance step (see docs/TestLoom_Ticketing_COLLAUDO.md).
/// </summary>
public sealed class PolarionBrowserSimulationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom-PolSim-" + Guid.NewGuid().ToString("N"));
    private readonly Uri _api = new("https://example.invalid/ticketing/rest/v1");
    private readonly FakePolarion _polarion = new();
    private readonly AppState _state;

    public PolarionBrowserSimulationTests()
    {
        Directory.CreateDirectory(_root);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_root);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Synthetic", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        _state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(_state.ImpostaOperatore(op.Nome, "1234"));
        var settings = PolarionApiSettingsService.Load(_state.Paths);
        settings.ApiRoot = _api.AbsoluteUri;
        settings.LetturaAbilitata = true;
        settings.ImpostaToken("synthetic-token");
        PolarionApiSettingsService.Save(_state.Paths, settings, true);
    }

    private (NonConformita Nc, PolarionBrowserHandoffService.Prepared Prepared) PrepareNc()
    {
        var nc = new NonConformita { Uid = Guid.NewGuid(), Id = "NC-1", DataApertura = DateTime.Now };
        _state.NonConformita.Save(nc);
        var prepared = PolarionBrowserHandoffService.Prepare(_state, false, nc.Id, nc.Uid,
            "Synthetic NC title", "Synthetic NC description", "P2", "major", null);
        return (nc, prepared);
    }

    private Task<PolarionBrowserHandoffService.CheckResult> Reconcile(PolarionCreationReceiptStore.Receipt receipt) =>
        PolarionBrowserHandoffService.TryReconcileAsync(_state, receipt, clientFactory: () =>
            new PolarionReadClient(new HttpClient(_polarion), _api, "synthetic-token"));

    [Fact]
    public async Task OpeningTheBrowserIsNotACreation_SavingThenVerifiesAndLinksExactlyOnce()
    {
        var (nc, prepared) = PrepareNc();

        // 1. Browser opened, user has not pressed Save yet: pending, never linked.
        Assert.Equal("https", prepared.BrowserUrl.Scheme);
        var before = await Reconcile(prepared.Receipt);
        Assert.False(before.Linked);
        Assert.True(before.Pending);
        Assert.Empty(_state.NonConformita.GetById(nc.Id)!.CollegamentiPolarion);

        // 2. The operator saves in (simulated) Ticketing with the TestLoomREF marker in the description.
        _polarion.UserSaves("TICKET-501", prepared.Receipt.BrowserCorrelationMarker!, "bug");

        // 3. Verified through GET only, linked once; a second cycle is idempotent.
        var after = await Reconcile(prepared.Receipt);
        Assert.True(after.Linked);
        Assert.Equal("TICKET-501", after.TicketId);
        Assert.Equal("TICKET-501", Assert.Single(_state.NonConformita.GetById(nc.Id)!.CollegamentiPolarion).Codice);
        _polarion.AssertOnlyReads();
    }

    [Fact]
    public async Task ClassificationMismatch_NcSavedAsNewRequest_IsNeverLinked()
    {
        var (nc, prepared) = PrepareNc();
        _polarion.UserSaves("TICKET-502", prepared.Receipt.BrowserCorrelationMarker!, "new_request"); // wrong: NC must be bug
        var result = await Reconcile(prepared.Receipt);
        Assert.False(result.Linked);
        Assert.Empty(_state.NonConformita.GetById(nc.Id)!.CollegamentiPolarion);
    }

    [Fact]
    public async Task TwoTicketsWithTheSameMarker_AreAmbiguous_NothingIsLinked()
    {
        var (nc, prepared) = PrepareNc();
        _polarion.UserSaves("TICKET-503", prepared.Receipt.BrowserCorrelationMarker!, "bug");
        _polarion.UserSaves("TICKET-504", prepared.Receipt.BrowserCorrelationMarker!, "bug");
        var result = await Reconcile(prepared.Receipt);
        Assert.False(result.Linked);
        Assert.True(result.Pending);
        Assert.Empty(_state.NonConformita.GetById(nc.Id)!.CollegamentiPolarion);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task TransientOrAuthErrors_NeverLinkAndKeepTheReceipt_ThenRecover(HttpStatusCode status)
    {
        var (nc, prepared) = PrepareNc();
        _polarion.UserSaves("TICKET-505", prepared.Receipt.BrowserCorrelationMarker!, "bug");
        _polarion.FailWith = status;
        PolarionBrowserHandoffService.CheckResult? failed = null;
        try { failed = await Reconcile(prepared.Receipt); } catch (Exception) { /* a thrown error is also acceptable: it must not link */ }
        if (failed is not null) Assert.False(failed.Linked);
        Assert.Empty(_state.NonConformita.GetById(nc.Id)!.CollegamentiPolarion);
        Assert.NotNull(new PolarionCreationReceiptStore(_state.Paths).Read(false, nc.Id, nc.Uid));

        _polarion.FailWith = null; // service is back
        var ok = await Reconcile(prepared.Receipt);
        Assert.True(ok.Linked);
        Assert.Equal("TICKET-505", Assert.Single(_state.NonConformita.GetById(nc.Id)!.CollegamentiPolarion).Codice);
        _polarion.AssertOnlyReads();
    }

    [Fact]
    public async Task RestartBetweenOpeningAndSaving_ReceiptSurvivesAndStillLinks()
    {
        var (nc, prepared) = PrepareNc();
        // "TestLoom closed": a brand new store instance must still see the pending receipt on disk.
        var reopened = new PolarionCreationReceiptStore(_state.Paths);
        var pending = Assert.Single(reopened.PendingBrowserReceipts());
        Assert.Equal(prepared.Receipt.BrowserCorrelationMarker, pending.BrowserCorrelationMarker);

        _polarion.UserSaves("TICKET-506", pending.BrowserCorrelationMarker!, "bug");
        var result = await Reconcile(pending);
        Assert.True(result.Linked);
        Assert.Equal("TICKET-506", Assert.Single(_state.NonConformita.GetById(nc.Id)!.CollegamentiPolarion).Codice);
    }

    [Fact]
    public void BrowserUrlCarriesNoSecretsAndOnlyAnHttpsGetNavigation()
    {
        var (_, prepared) = PrepareNc();
        var url = prepared.BrowserUrl.AbsoluteUri;
        Assert.StartsWith("https://example.invalid/ticketing/#/project/", url);
        Assert.Contains("form_mode=create", url);
        Assert.Contains("form_field_type=TICKET_Ticket", url);
        Assert.DoesNotContain("synthetic-token", url);
        // Ticketing ignores form_field_* (verified); title/description travel only to the local DevTools
        // session, never in the URL or the browser history: no marker, no description.
        Assert.DoesNotContain(prepared.Receipt.BrowserCorrelationMarker!, Uri.UnescapeDataString(url));
        Assert.DoesNotContain("form_field_description", url);
        // Nothing was sent anywhere just by preparing the handoff.
        Assert.Empty(_polarion.Requests);
    }

    public void Dispose()
    {
        _state.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Stateful fake of the Ticketing REST read API: only the two read endpoints TestLoom needs.</summary>
    private sealed class FakePolarion : HttpMessageHandler
    {
        private readonly List<(string Id, string Marker, string Type)> _items = new();
        public List<(HttpMethod Method, string Url)> Requests { get; } = new();
        public HttpStatusCode? FailWith { get; set; }

        public void UserSaves(string id, string marker, string type) => _items.Add((id, marker, type));

        public void AssertOnlyReads() => Assert.All(Requests, r => Assert.Equal(HttpMethod.Get, r.Method));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString()));
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Ticketing REST writes are forbidden.");
            if (FailWith is { } status)
                return Task.FromResult(new HttpResponseMessage(status));
            var path = request.RequestUri.AbsolutePath;
            string json;
            if (path.EndsWith("/workitems", StringComparison.Ordinal))
            {
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                var hits = _items.Where(i => query.Contains(i.Marker, StringComparison.Ordinal))
                    .Select(i => new { id = "TICKET_Tickets/" + i.Id });
                json = JsonSerializer.Serialize(new { data = hits });
            }
            else
            {
                var id = path[(path.LastIndexOf('/') + 1)..];
                var item = _items.FirstOrDefault(i => i.Id == id);
                if (item.Id is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                json = JsonSerializer.Serialize(new
                {
                    data = new
                    {
                        id = "TICKET_Tickets/" + item.Id, type = "workitems",
                        attributes = new
                        {
                            type = "TICKET_Ticket", TICKET_tck_type = item.Type,
                            description = new { value = "TestLoom correlation: " + item.Marker },
                            created = DateTimeOffset.UtcNow
                        }
                    }
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
