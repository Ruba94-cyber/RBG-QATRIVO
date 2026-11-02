using System.Net;
using System.Net.Http;
using System.Text;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionReadClientTests
{
    private const string ApiRoot = "https://example.invalid/ticketing/rest/v1";
    private static PolarionReadClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)), new Uri(ApiRoot), "fake-test-token");

    [Fact]
    public async Task ReadsEstaTicketAndComments_UsingGetOnly()
    {
        var calls = new List<string>();
        var client = Client(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal("fake-test-token", req.Headers.Authorization?.Parameter);
            calls.Add(req.RequestUri!.AbsolutePath);
            var json = req.RequestUri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal)
                ? """{"data":[{"id":"TICKET_Tickets/TICKET-123/1","attributes":{"text":{"type":"text/html","value":"First comment"},"created":"2026-10-29T10:30:00Z"},"relationships":{"author":{"data":{"type":"users","id":"writer1"}}}}]}"""
                : """{"data":{"id":"TICKET_Tickets/TICKET-123","type":"workitems","attributes":{"title":"Sample","description":{"type":"text/html","value":"<p>Details</p>"},"TICKET_tck_type":"bug","status":"testing","resolution":"done","priority":"33.0","severity":"major","created":"2026-10-28T10:30:00Z","updated":"2026-10-29T10:30:00Z"},"relationships":{"assignee":{"data":[{"type":"users","id":"user1"}]},"linkedWorkItems":{"data":[{"type":"workitems","id":"TICKET-456"}]}}}}""";
            return Json(json);
        });
        var snapshot = await client.GetTicketAsync("TICKET_Tickets", "TICKET-123");
        Assert.Equal("TICKET-123", snapshot.Id);
        Assert.Equal("Sample", snapshot.Title);
        Assert.Equal("bug", snapshot.TicketType);
        Assert.Equal("testing", snapshot.Status);
        Assert.Equal("33.0", snapshot.Priority);
        Assert.Equal("major", snapshot.Severity);
        Assert.Equal("<p>Details</p>", snapshot.Description);
        Assert.Single(snapshot.Assignees);
        Assert.Equal("TICKET-456", Assert.Single(snapshot.LinkedWorkItems));
        Assert.Equal("writer1", Assert.Single(snapshot.Comments).AuthorId);
        Assert.Equal("First comment", snapshot.Comments[0].Text);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task BrowserVerificationUsesOnlyOneGetAndNeverRequiresComments()
    {
        var calls = new List<string>();
        var client = Client(req =>
        {
            calls.Add(req.RequestUri!.AbsolutePath);
            if (req.RequestUri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            return Json("""
                {"data":{"id":"TICKET_Tickets/TICKET-321","type":"workitems",
                  "attributes":{"type":"TICKET_Ticket","title":"Synthetic ticket","TICKET_tck_type":"bug",
                    "description":{"type":"text/html","value":"<p>TestLoomREFAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA</p>"},
                    "created":"2026-10-30T01:00:00Z"},
                  "relationships":{"assignee":{"data":[{"type":"users","id":"owner1"}]}}}}
                """);
        });

        var verified = await client.GetTicketForVerificationAsync("TICKET_Tickets", "TICKET-321");
        Assert.Equal("TICKET-321", verified.Id);
        Assert.Equal("bug", verified.TicketType);
        Assert.Contains("TestLoomREF", verified.Description);
        Assert.Empty(verified.Comments);
        Assert.Equal(new[] { "owner1" }, verified.Assignees);
        Assert.Single(calls);
        Assert.DoesNotContain(calls, x => x.EndsWith("/comments", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "TICKET_Ticket", false)]
    [InlineData("TICKET_Tickets/TICKET-321", null, false)]
    [InlineData("TICKET_Tickets/TICKET-321", "defect", false)]
    [InlineData("OTHER/TICKET-321", "TICKET_Ticket", false)]
    [InlineData("TICKET_Tickets/TICKET-321", "TICKET_Ticket", true)]
    [InlineData("TICKET-321", "TICKET_Ticket", true)]
    public async Task BrowserVerificationRequiresResponseIdentityAndEstaWorkItemType(
        string? id, string? type, bool expectedValid)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            data = new { id, type = "workitems", attributes = new { type, TICKET_tck_type = "bug" } }
        });
        var client = Client(_ => Json(json));
        if (expectedValid)
            Assert.Equal("TICKET-321", (await client.GetTicketForVerificationAsync("TICKET_Tickets", "TICKET-321")).Id);
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTicketForVerificationAsync("TICKET_Tickets", "TICKET-321"));
    }

    [Fact]
    public async Task RejectsResponseWithDifferentResourceIdBeforeCaching()
    {
        var client = Client(_ => Json("""{"data":{"id":"TICKET_Tickets/TICKET-999","attributes":{"status":"new"}}}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTicketAsync("TICKET_Tickets", "TICKET-123"));
    }

    [Fact]
    public async Task ReadsCustomMultiAssigneeAndNormalizesLinkedWorkItemTargetIds()
    {
        var client = Client(req => req.RequestUri!.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal)
            ? Json("""{"data":[]}""")
            : Json("""
              {"data":{"type":"workitems","id":"TICKET_Tickets/TICKET-123",
              "attributes":{"TICKET_tck_type":"bug","TICKET_tkc_assign_to":[{"id":"operatorA"},{"id":"operatorB"}]},
              "relationships":{"linkedWorkItems":{"data":[
                {"type":"linkedworkitems","id":"TICKET_Tickets/TICKET-123/relates_to/TICKET_Tickets/TICKET-456"},
                {"type":"linkedworkitems","id":"TICKET_Tickets/TICKET-123/blocks/TICKET_Tickets/TICKET-789"}
              ]}}}}
              """));
        var snapshot = await client.GetTicketAsync("TICKET_Tickets", "TICKET-123");
        Assert.Equal(new[] { "operatorA", "operatorB" }, snapshot.CustomAssignees);
        Assert.Equal(new[] { "TICKET-456", "TICKET-789" }, snapshot.LinkedWorkItems);
    }

    [Fact]
    public async Task CorporateShape_ReadsCustomAssigneeRelationshipAndFourComments()
    {
        // Synthetic fixture based on the 29 Oct 2026 company-PC audit:
        // TICKET_Ticket/new_request, assigned, priority 25.0, normal severity,
        // four comments of which two contain text. Not copied from production.
        var requestedPaths = new List<string>();
        var client = Client(req =>
        {
            requestedPaths.Add(req.RequestUri!.AbsolutePath);
            if (req.RequestUri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal))
                return Json("""
                    {"data":[
                      {"id":"TICKET_Tickets/TICKET-19266/1","attributes":{"text":{"type":"text/html","value":"First text"},"created":"2026-10-29T09:00:00Z"},"relationships":{"author":{"data":{"type":"users","id":"users/author1"}}}},
                      {"id":"TICKET_Tickets/TICKET-19266/2","attributes":{"created":"2026-10-29T09:01:00Z"}},
                      {"id":"TICKET_Tickets/TICKET-19266/3","attributes":{"text":{"type":"text/html","value":"Second text"}}},
                      {"id":"TICKET_Tickets/TICKET-19266/4","attributes":{}}
                    ]}
                    """);
            return Json("""
                {"data":{"type":"workitems","id":"TICKET_Tickets/TICKET-19266",
                  "attributes":{"type":"TICKET_Ticket","TICKET_tck_type":"new_request",
                    "status":"assigned","priority":"25.0","severity":"normal",
                    "TICKET_tkc_assign_to":["users/ownerA"]},
                  "relationships":{
                    "assignee":{"data":[{"type":"users","id":"users/ownerA"}]},
                    "TICKET_tkc_assign_to":{"data":[
                       {"type":"users","id":"users/ownerA"},{"type":"users","id":"users/ownerB"}]},
                    "linkedWorkItems":{"data":[]}
                  }}}
                """);
        });
        var snapshot = await client.GetTicketAsync("TICKET_Tickets", "TICKET-19266");
        Assert.Equal("new_request", snapshot.TicketType);
        Assert.Equal("assigned", snapshot.Status);
        Assert.Equal(3, PolarionCriticalityService.ParsePriority(snapshot.Priority));
        Assert.Equal("normal", snapshot.Severity);
        Assert.Equal(new[] { "ownerA", "ownerB" }, snapshot.CustomAssignees);
        Assert.Equal(new[] { "ownerA" }, snapshot.Assignees);
        Assert.Equal(4, snapshot.Comments.Count);
        Assert.Equal("author1", Assert.Single(snapshot.Comments, c => c.Id.EndsWith("/1", StringComparison.Ordinal)).AuthorId);
        Assert.Equal(2, snapshot.Comments.Count(c => !string.IsNullOrWhiteSpace(c.Text)));
        Assert.Equal(2, requestedPaths.Count);
        Assert.DoesNotContain(requestedPaths, p => p.Contains("getFieldsMetadata", StringComparison.Ordinal));
        Assert.DoesNotContain(requestedPaths, p => p.Contains("backlinked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BrowserCorrelationSearchReturnsOnlyProjectScopedValidatedIds()
    {
        var calls = 0;
        var client = Client(req =>
        {
            calls++;
            Assert.Contains("query=", req.RequestUri!.Query);
            return Json("""{"data":[{"type":"workitems","id":"TICKET_Tickets/TICKET-321"}],"meta":{"totalCount":1}}""");
        });
        var ids = await client.SearchBrowserCorrelationAsync(
            "TICKET_Tickets", "TestLoomREF" + new string('A', 32));
        Assert.Equal(new[] { "TICKET-321" }, ids);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BrowserCorrelationSearchAcceptsValidatedShortIdsAndRejectsPartialResults()
    {
        var marker = "TestLoomREF" + new string('F', 32);
        var client = Client(_ => Json("""
            {"data":[{"id":"TICKET-101","type":"workitems"},
                     {"id":"TICKET_Tickets/TICKET-102","type":"workitems"}],
             "meta":{"totalCount":2}}
            """));
        var tickets = await client.SearchBrowserCorrelationAsync("TICKET_Tickets", marker);
        Assert.Equal(new[] { "TICKET-101", "TICKET-102" }, tickets);

        var partial = Client(_ => Json("""
            {"data":[{"id":"TICKET-101"}],
             "meta":{"totalCount":1,"errors":[{"title":"Incomplete query"}]}}
            """));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            partial.SearchBrowserCorrelationAsync("TICKET_Tickets", marker));

        var foreign = Client(_ => Json("""{"data":[{"id":"OTHER/TICKET-101"}]}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            foreign.SearchBrowserCorrelationAsync("TICKET_Tickets", marker));
    }

    [Fact]
    public async Task BrowserCorrelationSearchRejectsTruncatedCandidateSet()
    {
        var client = Client(_ => Json("""{"data":[],"meta":{"totalCount":26}}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.SearchBrowserCorrelationAsync("TICKET_Tickets", "TestLoomREF" + new string('B', 32)));
    }

    [Fact]
    public async Task BrowserCorrelationSearchRejectsServerCappedPage()
    {
        var marker = "TestLoomREF" + new string('B', 32);
        var nextPage = Client(_ => Json("""{"data":[{"id":"TICKET-101"}],"links":{"next":"?page=2"}}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() => nextPage.SearchBrowserCorrelationAsync("TICKET_Tickets", marker));
        var objectLink = Client(_ => Json("""{"data":[{"id":"TICKET-101"}],"links":{"next":{"href":"?page=2"}}}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() => objectLink.SearchBrowserCorrelationAsync("TICKET_Tickets", marker));
        var reportedTotal = Client(_ => Json("""{"data":[{"id":"TICKET-101"}],"meta":{"totalCount":2}}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() => reportedTotal.SearchBrowserCorrelationAsync("TICKET_Tickets", marker));
    }

    [Fact]
    public async Task RefusesToCacheTruncatedCriticalRelationshipFromHttp200()
    {
        var requests = 0;
        var client = Client(_ =>
        {
            requests++;
            return Json("""
                {"data":{"type":"workitems","id":"TICKET_Tickets/TICKET-123",
                  "attributes":{"TICKET_tck_type":"bug"},
                  "relationships":{"linkedWorkItems":{
                    "data":[{"type":"linkedworkitems","id":"TICKET_Tickets/TICKET-123/relates_to/TICKET_Tickets/TICKET-5"}],
                    "meta":{"totalCount":2}
                  }}}}
                """);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTicketAsync("TICKET_Tickets", "TICKET-123"));
        Assert.Equal(1, requests); // No comments GET and no incomplete snapshot.
    }

    [Fact]
    public async Task RefusesPartialWorkItemDataEvenWhenServerReturnsHttp200()
    {
        var client = Client(_ => Json("""
            {"meta":{"errors":[{"status":403,"title":"Relationship not readable"}]},
             "data":{"id":"TICKET_Tickets/TICKET-123","attributes":{"TICKET_tck_type":"bug"}}}
            """));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetTicketAsync("TICKET_Tickets", "TICKET-123"));
    }

    [Fact]
    public async Task CappedCommentPageWithoutNextLinkContinuesUsingTotalCount()
    {
        var requests = 0;
        var client = Client(_ =>
        {
            requests++;
            return requests == 1
                ? Json("""{"data":[{"id":"TICKET-123/1","attributes":{"text":{"value":"first"}}}],"meta":{"totalCount":2}}""")
                : Json("""{"data":[{"id":"TICKET-123/2","attributes":{"text":{"value":"second"}}}]}""");
        });
        var comments = await client.GetCommentsAsync("TICKET_Tickets", "TICKET-123");
        Assert.Equal(2, comments.Count);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task KeepsFirstPageTotalAcrossLaterPagesWithNoMeta()
    {
        var requests = 0;
        var client = Client(_ =>
        {
            requests++;
            return requests switch
            {
                1 => Json("""{"data":[{"id":"TICKET-123/3"}],"meta":{"totalCount":3}}"""),
                2 => Json("""{"data":[{"id":"TICKET-123/1"}]}"""),
                _ => Json("""{"data":[{"id":"TICKET-123/2"}]}""")
            };
        });
        var comments = await client.GetCommentsAsync("TICKET_Tickets", "TICKET-123");
        Assert.Equal(3, comments.Count);
        Assert.Equal(3, requests);
        Assert.Equal(new[] { "TICKET-123/1", "TICKET-123/2", "TICKET-123/3" },
            comments.Select(x => x.Id));
    }

    [Fact]
    public async Task RefusesPaginationWhenPolarionChangesTotalMidRead()
    {
        var requests = 0;
        var client = Client(_ => ++requests == 1
            ? Json("""{"data":[{"id":"TICKET-123/1"}],"meta":{"totalCount":3}}""")
            : Json("""{"data":[{"id":"TICKET-123/2"}],"meta":{"totalCount":4}}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetCommentsAsync("TICKET_Tickets", "TICKET-123"));
    }

    [Fact]
    public async Task RefusesIncompleteCommentsWhenReportedTotalCannotBeRead()
    {
        var requests = 0;
        var client = Client(_ =>
        {
            requests++;
            return requests == 1
                ? Json("""{"data":[{"id":"TICKET-123/1"}],"meta":{"totalCount":2}}""")
                : Json("""{"data":[],"meta":{"totalCount":2}}""");
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetCommentsAsync("TICKET_Tickets", "TICKET-123"));
    }

    [Fact]
    public async Task RejectsInvalidIdentifiersBeforeSendingRequests()
    {
        var called = false;
        var client = Client(_ => { called = true; return Json("{}"); });
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetTicketAsync("TICKET_Tickets", "../admin"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetTicketAsync("../TICKET", "TICKET-123"));
        Assert.False(called);
    }

    [Fact]
    public async Task NoIncompleteSnapshotWhenCommentFetchFails()
    {
        var client = Client(req => req.RequestUri!.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json("""{"data":{"id":"TICKET-123","attributes":{"status":"closed"}}}"""));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetTicketAsync("TICKET_Tickets", "TICKET-123"));
    }

    [Fact]
    public async Task RejectsRepeatedCommentPagesInsteadOfCachingPartialHistory()
    {
        var client = Client(_ => Json("{\"data\":[" +
            string.Join(",", Enumerable.Range(0, 100).Select(_ => "{\"id\":\"duplicate\"}")) + "]}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetCommentsAsync("TICKET_Tickets", "TICKET-123"));
    }

    [Fact]
    public async Task FollowsServerNextLinkEvenWhenFirstPageIsShort()
    {
        var calls = 0;
        var client = Client(req =>
        {
            calls++;
            if (calls == 1)
                return Json("""{"data":[{"id":"TICKET_Tickets/TICKET-123/1","attributes":{"text":{"value":"one"}}}],"links":{"next":"https://example.invalid/ticketing/rest/v1/projects/TICKET_Tickets/workitems/TICKET-123/comments?page%5Bnumber%5D=2"}}""");
            return Json("""{"data":[{"id":"TICKET_Tickets/TICKET-123/2","attributes":{"text":{"value":"two"}}}],"links":{"next":null}}""");
        });
        var comments = await client.GetCommentsAsync("TICKET_Tickets", "TICKET-123");
        Assert.Equal(2, comments.Count);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RejectsForeignNextLinkToProtectBearerToken()
    {
        var calls = 0;
        var client = Client(_ =>
        {
            calls++;
            return Json("""{"data":[{"id":"TICKET_Tickets/TICKET-123/1"}],"links":{"next":"https://evil.invalid/steal"}}""");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetCommentsAsync("TICKET_Tickets", "TICKET-123"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void HttpsRootAndTokenAreMandatory()
    {
        using var http = new HttpClient(new StubHandler(_ => Json("{}")));
        Assert.Throws<ArgumentException>(() => new PolarionReadClient(http, new Uri("http://example.invalid/api"), "token"));
        Assert.Throws<ArgumentException>(() => new PolarionReadClient(http, new Uri(ApiRoot), ""));
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
