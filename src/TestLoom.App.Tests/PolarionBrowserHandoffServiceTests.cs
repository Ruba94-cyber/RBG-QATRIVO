using TestLoom.App.Services;
using TestLoom.App.Models;

namespace TestLoom.App.Tests;

public sealed class PolarionBrowserHandoffServiceTests
{
    [Fact]
    public void BrowserCreationDoesNotRequireRestReadToken()
    {
        using var fixture = new ReconcileFixture(false, false);
        fixture.Receipts.Clear(fixture.Receipt);
        var settings = PolarionApiSettingsService.Load(fixture.State.Paths);
        settings.LetturaAbilitata = false;
        settings.TokenOffuscato = "";
        PolarionApiSettingsService.Save(fixture.State.Paths, settings, true);
        var nc = fixture.State.NonConformita.GetById(fixture.SourceId)!;
        var prepared = PolarionBrowserHandoffService.Prepare(fixture.State, false,
            fixture.SourceId, nc.Uid, "Synthetic test", "Synthetic description", null, null, null);
        Assert.Equal("https", prepared.BrowserUrl.Scheme);
        Assert.Contains("form_mode=create", prepared.BrowserUrl.AbsoluteUri);
        Assert.False(PolarionApiSettingsService.Load(fixture.State.Paths).LetturaAbilitata);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReconcileSyntheticBrowserTicketLinksSourceAndOptionalTest(bool isCr, bool withTest)
    {
        using var fixture = new ReconcileFixture(isCr, withTest);
        var calls = new List<string>();
        using var http = fixture.Client(req =>
        {
            Assert.Equal(System.Net.Http.HttpMethod.Get, req.Method);
            calls.Add(req.RequestUri!.AbsolutePath);
            return req.RequestUri.AbsolutePath.EndsWith("/workitems", StringComparison.Ordinal)
                ? "{\"data\":[{\"id\":\"TICKET_Tickets/TICKET-123\"}]}"
                : fixture.TicketJson();
        });
        var result = await PolarionBrowserHandoffService.TryReconcileAsync(fixture.State, fixture.Receipt,
            clientFactory: () => new PolarionReadClient(http, fixture.ApiRoot, "synthetic-token"));
        Assert.True(result.Linked);
        Assert.False(result.Pending);
        Assert.Equal("TICKET-123", result.TicketId);
        Assert.Equal(2, calls.Count);
        fixture.AssertLinked();
        Assert.Empty(fixture.Receipts.PendingBrowserReceipts());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileNoOrMultipleCandidatesLeavesSourceAndReceiptUntouched(bool multiple)
    {
        using var fixture = new ReconcileFixture(false, false);
        var calls = 0;
        using var http = fixture.Client(req =>
        {
            Assert.Equal(System.Net.Http.HttpMethod.Get, req.Method);
            calls++;
            return multiple
                ? "{\"data\":[{\"id\":\"TICKET-123\"},{\"id\":\"TICKET-124\"}]}"
                : "{\"data\":[]}";
        });
        var result = await PolarionBrowserHandoffService.TryReconcileAsync(fixture.State, fixture.Receipt,
            clientFactory: () => new PolarionReadClient(http, fixture.ApiRoot, "synthetic-token"));
        Assert.False(result.Linked);
        Assert.True(result.Pending);
        Assert.Equal(1, calls);
        Assert.Empty(fixture.State.NonConformita.GetById(fixture.SourceId)!.CollegamentiPolarion);
        Assert.Null(Assert.Single(fixture.Receipts.PendingBrowserReceipts()).CreatedTicketId);
    }

    private sealed class ReconcileFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom-Reconcile-" + Guid.NewGuid().ToString("N"));
        private readonly bool _isCr;
        private readonly Guid? _testUid;
        public Uri ApiRoot { get; } = new("https://example.invalid/ticketing/rest/v1");
        public AppState State { get; }
        public string SourceId { get; }
        public PolarionCreationReceiptStore Receipts { get; }
        public PolarionCreationReceiptStore.Receipt Receipt { get; }

        public ReconcileFixture(bool isCr, bool withTest)
        {
            _isCr = isCr;
            Directory.CreateDirectory(_root);
            State = TestStateFactory.Create();
            State.InizializzaCartella(_root);
            Assert.True(State.InizializzazioneWorkspaceCompletata);
            var op = TestOperatori.Sigilla(new Operatore { Nome = "Synthetic", Ruolo = RuoloOperatore.Master,
                PinHash = PinService.Hash("1234") });
            State.Master.Operatori.Save(new List<Operatore> { op });
            Assert.True(State.ImpostaOperatore(op.Nome, "1234"));
            var settings = PolarionApiSettingsService.Load(State.Paths);
            settings.ApiRoot = ApiRoot.AbsoluteUri;
            settings.LetturaAbilitata = true;
            settings.ImpostaToken("synthetic-token");
            PolarionApiSettingsService.Save(State.Paths, settings, true);
            SourceId = isCr ? "CR-1" : "NC-1";
            var uid = Guid.NewGuid();
            if (isCr) State.ChangeRequests.Save(new ChangeRequest { Uid = uid, Id = SourceId });
            else State.NonConformita.Save(new NonConformita { Uid = uid, Id = SourceId, DataApertura = DateTime.Now });
            if (withTest)
            {
                _testUid = Guid.NewGuid();
                State.TestCases.Save(new TestCase { Uid = _testUid.Value, Id = "TC-1", Componente = "BCM",
                    DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now }, isNew: true);
            }
            Receipts = new PolarionCreationReceiptStore(State.Paths);
            Receipt = Receipts.BeginBrowser(isCr, SourceId, uid, "TestLoomREF" + new string('A', 32),
                isCr ? "new_request" : "bug", null, op.Nome, _testUid, withTest ? "TC-1" : null);
        }
        public string TicketJson() => System.Text.Json.JsonSerializer.Serialize(new
        {
            data = new { id = "TICKET_Tickets/TICKET-123", type = "workitems", attributes = new
            {
                type = "TICKET_Ticket", TICKET_tck_type = _isCr ? "new_request" : "bug",
                description = new { value = Receipt.BrowserCorrelationMarker }, created = DateTimeOffset.UtcNow
            } }
        });
        public System.Net.Http.HttpClient Client(Func<System.Net.Http.HttpRequestMessage, string> respond) =>
            new(new FakeHandler(respond));
        public void AssertLinked()
        {
            if (_isCr) Assert.Equal("TICKET-123", Assert.Single(State.ChangeRequests.GetById(SourceId)!.TicketCollegati).Riferimento);
            else Assert.Equal("TICKET-123", Assert.Single(State.NonConformita.GetById(SourceId)!.CollegamentiPolarion).Codice);
            if (_testUid is { } uid)
                Assert.Equal("TICKET-123", Assert.Single(State.TestCases.GetByUid(uid)!.TicketCollegati).Riferimento);
        }
        public void Dispose()
        {
            // A test may have replaced the original receipt (e.g. Prepare after Clear): clear whatever is stored now.
            if (Receipts.Read(Receipt.IsCr, Receipt.SourceId, Receipt.SourceUid) is { } stored)
                Receipts.Clear(stored);
            State.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
    private sealed class FakeHandler(Func<System.Net.Http.HttpRequestMessage, string> respond) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent(respond(request), System.Text.Encoding.UTF8, "application/json") });
    }

    [Fact]
    public void PartialSourceAndTestLinkRetainsVerifiedIdentityAndRecoversWithoutDuplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom-BrowserRecovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var state = TestStateFactory.Create();
        state.InizializzaCartella(root);
        Assert.True(state.InizializzazioneWorkspaceCompletata);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Synthetic", Ruolo = RuoloOperatore.Master,
            PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        var nc = new NonConformita { Id = "NC-0001", DataApertura = DateTime.Now };
        state.NonConformita.Save(nc);
        var testUid = Guid.NewGuid();
        var receipts = new PolarionCreationReceiptStore(state.Paths);
        var receipt = receipts.BeginBrowser(false, nc.Id, nc.Uid, "TestLoomREF" + new string('A', 32),
            "bug", null, op.Nome, testUid, "TC-1");
        var created = new PolarionWriteService.Created("TICKET-123", "TICKET_Tickets/TICKET-123",
            "https://example.invalid/TICKET-123");

        // Test is missing: NC saves but the operation must remain recoverable.
        var partial = PolarionBrowserHandoffService.ApplyVerifiedLinks(state, receipt, created);
        Assert.True(partial.Source.Linked);
        Assert.NotNull(partial.Test);
        Assert.False(partial.Test!.Linked);
        Assert.Single(state.NonConformita.GetById(nc.Id)!.CollegamentiPolarion);
        var pending = Assert.Single(new PolarionCreationReceiptStore(state.Paths).PendingBrowserReceipts());
        Assert.Equal("TICKET-123", pending.CreatedTicketId);

        state.TestCases.Save(new TestCase { Uid = testUid, Id = "TC-1", Componente = "BCM",
            DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now }, isNew: true);
        var recovered = PolarionBrowserHandoffService.ApplyVerifiedLinks(state, pending, created);
        Assert.True(recovered.Source.Linked);
        Assert.True(recovered.Test!.Linked);
        Assert.Single(state.NonConformita.GetById(nc.Id)!.CollegamentiPolarion);
        Assert.Equal("TICKET-123", Assert.Single(state.TestCases.GetByUid(testUid)!.TicketCollegati).Riferimento);
        Assert.Empty(receipts.PendingBrowserReceipts());
    }

    [Fact]
    public void BrowserHandoffMarkerHasUniqueSearchableFormat()
    {
        var marker = "TestLoomREF" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        Assert.Matches("^TestLoomREF[A-F0-9]{32}$", marker);
        Assert.DoesNotContain("-", marker);
    }

    [Fact]
    public void CreateFormDeepLinkPrefillsTypeAndEscapedTitleWithoutSubmitting()
    {
        var url = PolarionBrowserHandoffService.BuildCreateFormUrl(
            new Uri("https://ticketing.example.invalid/ticketing/rest/v1"),
            "TICKET_Tickets", "Brake & CAN #1").AbsoluteUri;

        Assert.StartsWith("https://ticketing.example.invalid/ticketing/#/project/TICKET_Tickets/workitem?", url);
        Assert.Contains("form_mode=create", url);
        Assert.Contains("form_field_type=TICKET_Ticket", url);
        Assert.Contains("form_field_title=Brake%20%26%20CAN%20%231", url);
        Assert.DoesNotContain("form_field_TICKET_tck_type", url); // Custom enum requires manual verification.
        Assert.DoesNotContain("/ticketing/rest/", url);
    }

    [Fact]
    public void CreateFormDoesNotGuessCustomFieldsOrEmbedDescriptionOrCredentials()
    {
        var title = "TICKET NC / test";
        var url = PolarionBrowserHandoffService.BuildCreateFormUrl(
            new Uri("https://ticketing.example.invalid/ticketing/rest/v1"),
            "TICKET_Tickets", title).AbsoluteUri;

        Assert.DoesNotContain("TestLoomREF", url);
        Assert.DoesNotContain("description", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("priority", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("severity", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assignee", url, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://ticketing.example.invalid/ticketing/rest/v1", "TICKET_Tickets", "Title")]
    [InlineData("https://ticketing.example.invalid/ticketing/rest/v1", "../OTHER", "Title")]
    [InlineData("https://ticketing.example.invalid/ticketing/rest/v1", "TICKET_Tickets", "")]
    public void CreateFormRejectsUntrustedHostOrInvalidProjectOrTitle(
        string root, string project, string title)
    {
        Assert.Throws<ArgumentException>(() =>
            PolarionBrowserHandoffService.BuildCreateFormUrl(new Uri(root), project, title));
    }

    [Theory]
    [InlineData("bug", "P1", "50.0")]
    [InlineData("new_request", "P3", "25.0")]
    [InlineData("bug", "P5", "16.0")]
    public void ExperimentalFullPrefillCarriesAllAvailableEstaFields(
        string subtype, string priority, string expectedPriority)
    {
        var marker = "TestLoomREF" + new string('A', 32);
        var description = "Source: NC-1\\nDetailed test evidence & diagnostics\\n" + marker;
        var url = PolarionBrowserHandoffService.BuildExperimentalCreateFormUrl(
            new Uri("https://ticketing.example.invalid/ticketing/rest/v1"),
            "TICKET_Tickets", "CAN & safety", description,
            subtype, priority, "Major", "external.user").AbsoluteUri;
        var decoded = Uri.UnescapeDataString(url);

        Assert.Contains("form_mode=create", decoded);
        Assert.Contains("form_field_type=TICKET_Ticket", decoded);
        Assert.Contains("form_field_title=CAN & safety", decoded);
        Assert.Contains("form_field_description=Source: NC-1", decoded);
        Assert.Contains(marker, decoded);
        Assert.Contains("form_field_TICKET_tck_type=" + subtype, decoded);
        Assert.Contains("form_field_priority=" + expectedPriority, decoded);
        Assert.Contains("form_field_severity=major", decoded);
        Assert.Contains("form_field_TICKET_tkc_assign_to=external.user", decoded);
        Assert.DoesNotContain("/ticketing/rest/", url);
        Assert.DoesNotContain("Bearer", url);
    }

    [Fact]
    public void ExperimentalFullPrefillRejectsOversizedDescriptionInsteadOfTruncatingMarker()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PolarionBrowserHandoffService.BuildExperimentalCreateFormUrl(
                new Uri("https://ticketing.example.invalid/ticketing/rest/v1"),
                "TICKET_Tickets", "Test", new string('X', 20000) + "TestLoomREF" + new string('B', 32),
                "bug", "P2", "Normal", "external.user"));
    }

    [Fact]
    public void ExperimentalFullPrefillDoesNotAllowUnknownTicketSubtype()
    {
        Assert.Throws<ArgumentException>(() =>
            PolarionBrowserHandoffService.BuildExperimentalCreateFormUrl(
                new Uri("https://ticketing.example.invalid/ticketing/rest/v1"),
                "TICKET_Tickets", "Test", "Text", "new_request_execution", "P2", "Normal", ""));
    }

    [Fact]
    public void VerifiedBrowserTicketRequiresExactMarkerSubtypeCreationTimeAndAssignee()
    {
        var started = DateTimeOffset.Parse("2026-10-30T10:00:00Z");
        var marker = "TestLoomREF" + new string('C', 32);
        var receipt = new PolarionCreationReceiptStore.Receipt
        {
            SourceId = "NC-41", SourceUid = Guid.NewGuid(),
            StartedUtc = started, ExpectedTicketType = "bug",
            BrowserCorrelationMarker = marker, RequestedAssignee = "assigned.user"
        };
        PolarionTicketSnapshot Ticket(string type = "bug", string? description = null,
            DateTimeOffset? created = null, string? assignee = "assigned.user") => new()
        {
            ProjectId = "TICKET_Tickets", Id = "TICKET-9", TicketType = type,
            Description = description ?? "NC evidence + " + marker + " / TestLoom source NC-41",
            Created = created ?? started.AddMinutes(1),
            CustomAssignees = assignee is null ? new() : new() { assignee }
        };
        var now = started.AddMinutes(2);
        Assert.Equal(PolarionBrowserHandoffService.CandidateVerification.Match,
            PolarionBrowserHandoffService.VerifyCandidate(receipt, Ticket(), now));
        Assert.Equal(PolarionBrowserHandoffService.CandidateVerification.TypeMarkerOrDateMismatch,
            PolarionBrowserHandoffService.VerifyCandidate(receipt, Ticket(type: "new_request"), now));
        Assert.Equal(PolarionBrowserHandoffService.CandidateVerification.TypeMarkerOrDateMismatch,
            PolarionBrowserHandoffService.VerifyCandidate(receipt, Ticket(description: "no marker"), now));
        Assert.Equal(PolarionBrowserHandoffService.CandidateVerification.TypeMarkerOrDateMismatch,
            PolarionBrowserHandoffService.VerifyCandidate(receipt,
                Ticket(created: started.AddMinutes(-10)), now));
        Assert.Equal(PolarionBrowserHandoffService.CandidateVerification.TypeMarkerOrDateMismatch,
            PolarionBrowserHandoffService.VerifyCandidate(receipt,
                Ticket(created: now.AddMinutes(10)), now));
        Assert.Equal(PolarionBrowserHandoffService.CandidateVerification.AssigneeMismatch,
            PolarionBrowserHandoffService.VerifyCandidate(receipt,
                Ticket(assignee: "other.user"), now));
    }

    [Fact]
    public void ExternalPolarionAssigneesAreNotInternalDitmOperators()
    {
        var settings = new PolarionApiSettings
        {
            AssegnatariEsta = ["ticketing.user.one", "ticketing.user.two"]
        };
        Assert.Equal(2, settings.AssegnatariEsta.Count);
        Assert.False(settings.ScritturaAbilitata);
    }
}
