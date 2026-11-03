using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Read-only Ticketing JSON:API client. Intentionally exposes no POST/PUT/PATCH/DELETE.
/// All HTTP requests go to a validated HTTPS API root and carry the token per request,
/// not in default HttpClient headers or logs.
/// </summary>
public sealed class PolarionReadClient
{
    private const int PageSize = 100;
    private const int MaxCommentPages = 100;
    private static readonly Regex ProjectPattern = new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex TicketPattern = new("^[A-Za-z][A-Za-z0-9_]*-[0-9]+$", RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly Uri _apiRoot;
    private readonly string _token;

    public PolarionReadClient(HttpClient http, Uri apiRoot, string bearerToken)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentNullException.ThrowIfNull(apiRoot);
        if (!apiRoot.IsAbsoluteUri || apiRoot.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(apiRoot.Query) || !string.IsNullOrEmpty(apiRoot.Fragment) ||
            !string.IsNullOrEmpty(apiRoot.UserInfo))
            throw new ArgumentException("Ticketing API root must be an absolute HTTPS URL without credentials, query or fragment.", nameof(apiRoot));
        if (string.IsNullOrWhiteSpace(bearerToken) || bearerToken.Contains('\r') || bearerToken.Contains('\n'))
            throw new ArgumentException("A valid bearer token is required.", nameof(bearerToken));
        _apiRoot = new Uri(apiRoot.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _token = bearerToken;
    }

    /// <summary>Windows proxy authentication without sending Windows credentials to Ticketing itself.</summary>
    public static HttpClient CreateWindowsProxyHttpClient() => new(new HttpClientHandler
    {
        DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>Full detail for NC/CR UI and caches, including the complete comment history.</summary>
    public Task<PolarionTicketSnapshot> GetTicketAsync(string projectId, string workItemId,
        CancellationToken cancellationToken = default) =>
        GetTicketCoreAsync(projectId, workItemId, includeComments: true, cancellationToken);

    /// <summary>
    /// Minimal verified read for browser-created TICKET reconciliation. A new ticket
    /// may be readable before the comments endpoint is ready or authorized. The
    /// correlation marker, subtype, created date and assignees require only GET
    /// workitems/{id}; no comment history is needed or cached by this method.
    /// </summary>
    public Task<PolarionTicketSnapshot> GetTicketForVerificationAsync(string projectId, string workItemId,
        CancellationToken cancellationToken = default) =>
        GetTicketCoreAsync(projectId, workItemId, includeComments: false, cancellationToken);

    /// <summary>Verifies authenticated read access to the project without modifying Ticketing.</summary>
    public async Task ProbeProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || !ProjectPattern.IsMatch(projectId))
            throw new ArgumentException("Invalid Ticketing project ID.", nameof(projectId));
        try
        {
            using var document = await GetJsonAsync("projects/" + Uri.EscapeDataString(projectId) +
                "/workitems?page%5Bsize%5D=1&fields%5Bworkitems%5D=id", cancellationToken).ConfigureAwait(false);
            if (Child(document.RootElement, "data").ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Ticketing project response is missing the work item array.");
            var errors = Child(Child(document.RootElement, "meta"), "errors");
            if (errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                throw new InvalidDataException("Ticketing returned partial project data.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Ticketing project response is not valid JSON.", ex);
        }
    }

    private async Task<PolarionTicketSnapshot> GetTicketCoreAsync(
        string projectId, string workItemId, bool includeComments, CancellationToken cancellationToken)
    {
        ValidateIds(projectId, workItemId);
        var path = "projects/" + Uri.EscapeDataString(projectId) + "/workitems/" + Uri.EscapeDataString(workItemId);
        using var document = await GetJsonAsync(path + "?fields%5Bworkitems%5D=%40all", cancellationToken).ConfigureAwait(false);
        var item = DataObject(document.RootElement);
        var attrs = Child(item, "attributes");
        EnsureCompleteWorkItem(document.RootElement, item);
        if (item.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Ticketing work item response is missing the data object.");
        // JSON:API resource IDs are normally composite: "TICKET_Tickets/TICKET-123".
        // The cache and UI always use the short Work Item identifier.
        var responseId = Value(item, "id");
        if (!includeComments && (string.IsNullOrWhiteSpace(responseId) ||
            !string.Equals(Value(attrs, "type"), "TICKET_Ticket", StringComparison.Ordinal)))
            throw new InvalidDataException("Ticketing verification requires a work item ID and TICKET_Ticket type.");
        var resourceId = responseId ?? workItemId;
        if (!string.Equals(resourceId, workItemId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(resourceId, projectId + "/" + workItemId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ticketing returned a work item ID different from the requested ticket.");

        var comments = includeComments
            ? await GetCommentsAsync(projectId, workItemId, cancellationToken).ConfigureAwait(false)
            : new List<PolarionCommentSnapshot>();
        return new PolarionTicketSnapshot
        {
            ProjectId = projectId,
            Id = workItemId,
            Title = Value(attrs, "title"),
            Description = RichText(attrs, "description"),
            TicketType = Value(attrs, "TICKET_tck_type"),
            Status = Value(attrs, "status"),
            Resolution = Value(attrs, "resolution"),
            Priority = Value(attrs, "priority"),
            Severity = Value(attrs, "severity"),
            Created = Date(attrs, "created"),
            Updated = Date(attrs, "updated"),
            ResolvedOn = Date(attrs, "resolvedOn"),
            TicketEndDate = Value(attrs, "TICKET_ticket_end_date"),
            WorkingUnit = RichText(attrs, "TICKET_working_unit"),
            Invoice = RichText(attrs, "TICKET_invoice"),
            Components = AttributeIds(attrs, "TICKET_component"),
            Componenti = AttributeIds(attrs, "TICKET_ecu"),
            RetrievedAtUtc = DateTimeOffset.UtcNow,
            Assignees = UserIds(RelationIds(item, "assignee")),
            // Ticketing project custom fields can appear in attributes or as JSON:API
            // relationships depending on REST representation/version. Neither affects
            // TestLoom's own assigned operator.
            CustomAssignees = UserIds(AttributeIds(attrs, "TICKET_tkc_assign_to")
                .Concat(RelationIds(item, "TICKET_tkc_assign_to"))),
            LinkedWorkItems = LinkedTicketIds(item),
            Comments = comments
        };
    }

    /// <summary>
    /// Bounded, read-only discovery of candidate TICKET IDs containing a unique marker in
    /// the Ticketing description index. Search results are NOT proof of creation:
    /// the caller MUST GET each candidate and verify the full description, type and date.
    /// If the result set exceeds one page, refuse to make an automatic association.
    /// </summary>
    public async Task<IReadOnlyList<string>> SearchBrowserCorrelationAsync(
        string projectId, string correlationMarker, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || !ProjectPattern.IsMatch(projectId) ||
            !Regex.IsMatch(correlationMarker ?? "", @"^TestLoomREF[A-F0-9]{32}$"))
            throw new ArgumentException("Invalid project or TICKET browser correlation marker.");
        const int limit = 25;
        var query = "description:" + correlationMarker;
        var path = "projects/" + Uri.EscapeDataString(projectId) + "/workitems?query=" +
            Uri.EscapeDataString(query) + "&page%5Bsize%5D=" + limit +
            "&fields%5Bworkitems%5D=id,title,TICKET_tck_type,created";
        using var json = await GetJsonAsync(path, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        var items = Child(root, "data");
        if (items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("TICKET browser candidate query did not return an array.");
        var partialErrors = Child(Child(root, "meta"), "errors");
        if (partialErrors.ValueKind == JsonValueKind.Array && partialErrors.GetArrayLength() > 0)
            throw new InvalidDataException("TICKET candidate query returned partial results.");
        var count = Child(Child(root, "meta"), "totalCount");
        if (count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var total) && total > items.GetArrayLength())
            throw new InvalidDataException("TICKET handoff query is ambiguous: too many candidates.");
        var next = Child(Child(root, "links"), "next");
        if (next.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) &&
            (next.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(next.GetString())))
            throw new InvalidDataException("TICKET handoff query may be truncated; refusing automatic association.");
        if (items.GetArrayLength() >= limit)
            throw new InvalidDataException("TICKET handoff query may be truncated; refusing automatic association.");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.EnumerateArray())
        {
            var id = Value(item, "id");
            var prefix = projectId + "/";
            // Some JSON:API adapters return project-qualified work-item IDs,
            // while legacy-compatible endpoints return the short ID. Accept
            // either only when it is a validated ticket in this project.
            if (id is null)
                throw new InvalidDataException("Ticketing browser search omitted work-item identity.");
            var shortId = id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? id[prefix.Length..] : id;
            if (!TicketPattern.IsMatch(shortId))
                throw new InvalidDataException("Ticketing browser search returned invalid or foreign work-item identity.");
            result.Add(shortId);
        }
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<List<PolarionCommentSnapshot>> GetCommentsAsync(string projectId, string workItemId, CancellationToken cancellationToken = default)
    {
        ValidateIds(projectId, workItemId);
        var path = "projects/" + Uri.EscapeDataString(projectId) + "/workitems/" + Uri.EscapeDataString(workItemId)
            + "/comments?fields%5Bworkitem_comments%5D=%40all&page%5Bsize%5D=" + PageSize;
        var result = new List<PolarionCommentSnapshot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? expectedTotal = null;
        for (var page = 1; page <= MaxCommentPages; page++)
        {
            using var document = await GetJsonAsync(path, cancellationToken).ConfigureAwait(false);
            var data = Child(document.RootElement, "data");
            if (data.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Ticketing comments response is missing the data array.");
            var count = 0;
            foreach (var comment in data.EnumerateArray())
            {
                count++;
                var id = Value(comment, "id");
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidDataException("Ticketing comment is missing its identifier.");
                if (!seen.Add(id))
                    throw new InvalidDataException("Ticketing returned repeated comment IDs across pages; refusing to cache incomplete history.");
                var attrs = Child(comment, "attributes");
                result.Add(new PolarionCommentSnapshot
                {
                    Id = id,
                    AuthorId = UserIds(RelationIds(comment, "author")).FirstOrDefault(),
                    Created = Date(attrs, "created"),
                    Text = RichText(attrs, "text") ?? ""
                });
            }
            // A server may cap the response below the requested page size.
            // Respect links.next first, then meta.totalCount when links are absent.
            var next = Value(Child(document.RootElement, "links"), "next");
            var pageTotal = IntValue(Child(document.RootElement, "meta"), "totalCount");
            if (pageTotal is { } total)
            {
                if (expectedTotal is not null && expectedTotal != total)
                    throw new InvalidDataException("Ticketing comment total changed during pagination.");
                expectedTotal = total;
            }
            if (string.IsNullOrWhiteSpace(next) && (expectedTotal is null || result.Count >= expectedTotal)
                && count < PageSize)
            {
                SortComments(result);
                return result;
            }
            if (!string.IsNullOrWhiteSpace(next))
            {
                // Enforce the existing API-root and HTTPS restrictions inside GetJsonAsync.
                // A server-supplied pagination URL must never redirect our bearer token.
                path = next;
            }
            else if (count == 0)
            {
                if (expectedTotal is > 0 && result.Count < expectedTotal)
                    throw new InvalidDataException("Ticketing comments ended before the reported total; refusing an incomplete snapshot.");
                SortComments(result);
                return result;
            }
            else
            {
                // Numbered-page fallback for Ticketing versions without links.next.
                path = "projects/" + Uri.EscapeDataString(projectId) + "/workitems/" +
                    Uri.EscapeDataString(workItemId) +
                    "/comments?fields%5Bworkitem_comments%5D=%40all&page%5Bsize%5D=" + PageSize +
                    "&page%5Bnumber%5D=" + (page + 1);
            }
        }
        throw new InvalidDataException("Ticketing comment history exceeded the supported page limit; refusing to cache incomplete data.");
    }

    private async Task<JsonDocument> GetJsonAsync(string relativePath, CancellationToken cancellationToken)
    {
        var uri = new Uri(_apiRoot, relativePath);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != _apiRoot.Host || uri.Port != _apiRoot.Port ||
            !uri.AbsolutePath.StartsWith(_apiRoot.AbsolutePath, StringComparison.Ordinal))
            throw new InvalidOperationException("Ticketing API request would leave the configured API root.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // No HTTP redirects with bearer credentials: the factory explicitly disables them.
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateIds(string project, string id)
    {
        if (string.IsNullOrWhiteSpace(project) || !ProjectPattern.IsMatch(project))
            throw new ArgumentException("Invalid Ticketing project ID.", nameof(project));
        if (string.IsNullOrWhiteSpace(id) || !TicketPattern.IsMatch(id))
            throw new ArgumentException("Invalid Ticketing work item ID.", nameof(id));
    }

    private static JsonElement Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var child) ? child : default;

    private static JsonElement DataObject(JsonElement root) => Child(root, "data");

    private static string? Value(JsonElement parent, string name)
    {
        var item = Child(parent, name);
        return item.ValueKind switch
        {
            JsonValueKind.String => item.GetString(),
            JsonValueKind.Number => item.GetRawText(),
            _ => null
        };
    }

    private static string? RichText(JsonElement parent, string name)
    {
        var node = Child(parent, name);
        return node.ValueKind == JsonValueKind.String ? node.GetString() : Value(node, "value");
    }

    private static DateTimeOffset? Date(JsonElement parent, string name) =>
        DateTimeOffset.TryParse(Value(parent, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value : null;

    /// <summary>
    /// Ticketing can return HTTP 200 while a relationship is truncated or a field
    /// is hidden due to permission errors. Never cache an apparently complete
    /// read-only TICKET when an important field may be missing.
    /// </summary>
    private static void EnsureCompleteWorkItem(JsonElement root, JsonElement item)
    {
        var errors = Child(Child(root, "meta"), "errors");
        if (errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() != 0)
            throw new InvalidDataException("Ticketing reported partial work-item data.");
        foreach (var name in new[] { "assignee", "TICKET_tkc_assign_to", "linkedWorkItems" })
        {
            var relation = Child(Child(item, "relationships"), name);
            if (relation.ValueKind != JsonValueKind.Object) continue;
            var total = IntValue(Child(relation, "meta"), "totalCount");
            if (total is null) continue;
            var data = Child(relation, "data");
            var received = data.ValueKind == JsonValueKind.Array ? data.GetArrayLength()
                : data.ValueKind == JsonValueKind.Object ? 1 : 0;
            if (total > received)
                throw new InvalidDataException("Ticketing returned truncated " + name + " relationship.");
        }
    }

    private static void SortComments(List<PolarionCommentSnapshot> comments) =>
        comments.Sort((left, right) =>
        {
            var byDate = Nullable.Compare(left.Created, right.Created);
            return byDate != 0 ? byDate : StringComparer.Ordinal.Compare(left.Id, right.Id);
        });

    private static int? IntValue(JsonElement parent, string name)
    {
        var value = Child(parent, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : null;
    }

    private static List<string> AttributeIds(JsonElement attributes, string name)
    {
        var value = Child(attributes, name);
        var result = new List<string>();
        void Add(JsonElement item)
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var id = Value(item, "id") ?? Value(item, "login") ??
                    Value(item, "name") ?? Value(item, "value");
                if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Add(item);
        else Add(value);
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> UserIds(IEnumerable<string> references)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            if (string.IsNullOrWhiteSpace(reference)) continue;
            var id = reference.Trim();
            // The same user can be returned as "operator" or "users/operator".
            // Normalize only this documented resource prefix; other slash-bearing
            // reference forms remain untouched instead of guessing their identity.
            if (id.StartsWith("users/", StringComparison.OrdinalIgnoreCase))
                id = id["users/".Length..];
            if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
        }
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> LinkedTicketIds(JsonElement workItem)
    {
        var values = RelationIds(workItem, "linkedWorkItems");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // JSON:API linkedworkitems resource IDs often contain source ID, link role and
        // target project/ID. Do not misrepresent the whole link ID as the target ticket.
        foreach (var value in values)
        {
            var matches = Regex.Matches(value, @"[A-Za-z][A-Za-z0-9_]*-[0-9]+");
            if (matches.Count > 0) result.Add(matches[^1].Value);
        }
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> RelationIds(JsonElement item, string name)
    {
        var relationship = Child(Child(item, "relationships"), name);
        var data = Child(relationship, "data");
        var result = new List<string>();
        if (data.ValueKind == JsonValueKind.Object)
        {
            var id = Value(data, "id");
            if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
        }
        else if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var relation in data.EnumerateArray())
            {
                var id = Value(relation, "id");
                if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
