using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TestLoom.App.Services;

public enum AutofillOutcome { NotLaunched, OpenedNotFilled, Filled }

public sealed record AutofillResult(AutofillOutcome Outcome, string Detail);

/// <summary>Minimal DevTools-protocol session: one command, its JSON "result" back.</summary>
internal interface ICdpSession : IAsyncDisposable
{
    Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct);
}

/// <summary>
/// Writes ONLY title and description into the Ticketing "create work item" form opened in the operator's own
/// Edge/Chrome (own SSO login, dedicated TestLoom profile). Verified on the corporate Ticketing: the form ignores
/// every form_field_* URL parameter, but the DOM fields accept the values. TestLoom never presses Save and never
/// sends REST writes; the debugging port is bound to 127.0.0.1 on a random port and no data goes in any URL.
/// </summary>
public static class PolarionFormAutofill
{
    private static int _knownPort;
    private static readonly object LaunchGate = new();

    /// <summary>Full flow: browser session + navigate + wait for the form + fill + read back.</summary>
    public static async Task<AutofillResult> RunAsync(Uri formUrl, string title, string description,
        CancellationToken ct, TimeSpan? timeout = null, string? browserExe = null)
    {
        ICdpSession? session;
        try { session = await OpenBrowserSessionAsync(ct, browserExe).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticTraceService.Registra("TICKET.Autofill", "not-launched kind=" + ex.GetType().Name);
            return new AutofillResult(AutofillOutcome.NotLaunched, ex.GetType().Name);
        }
        if (session is null) return new AutofillResult(AutofillOutcome.NotLaunched, "browser-not-found");
        try
        {
            return await FillAsync(session, formUrl, title, description,
                timeout ?? TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticTraceService.Registra("TICKET.Autofill", "session-failed kind=" + ex.GetType().Name);
            return new AutofillResult(AutofillOutcome.OpenedNotFilled, ex.GetType().Name);
        }
        finally { await session.DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>Ticketing's single-page app often ignores the first deep link and shows the previously open page
    /// (also outside TestLoom): the same link works when requested again. Up to this many extra navigations.</summary>
    private const int MaxRenavigations = 3;

    internal static async Task<AutofillResult> FillAsync(ICdpSession session, Uri formUrl, string title,
        string description, TimeSpan timeout, TimeSpan poll, CancellationToken ct, TimeSpan? renavigateAfter = null)
    {
        var gap = renavigateAfter ?? TimeSpan.FromSeconds(6);
        await session.SendAsync("Page.navigate", new { url = formUrl.AbsoluteUri }, ct).ConfigureAwait(false);
        var lastNavigation = DateTime.UtcNow;
        var renavigations = 0;
        var deadline = DateTime.UtcNow + timeout;
        var last = "form-not-found";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var json = await EvalAsync(session, BuildFillScript(title, description), ct).ConfigureAwait(false);
            if (json is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var found = root.GetProperty("found").GetBoolean();
                    var t = root.GetProperty("title").GetBoolean();
                    var d = root.GetProperty("description").GetBoolean();
                    if (found && t && d) return new AutofillResult(AutofillOutcome.Filled, "ok");
                    last = found ? "title=" + t + " description=" + d : "form-not-found";
                    if (!found && renavigations < MaxRenavigations && DateTime.UtcNow - lastNavigation >= gap &&
                        IsWrongPolarionPage(root, formUrl))
                    {
                        renavigations++;
                        lastNavigation = DateTime.UtcNow;
                        DiagnosticTraceService.Registra("TICKET.Autofill", "renavigate attempt=" + renavigations);
                        await session.SendAsync("Page.navigate", new { url = formUrl.AbsoluteUri }, ct)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    last = "unreadable-script-result";
                }
            }
            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(poll, ct).ConfigureAwait(false);
        }
        DiagnosticTraceService.Registra("TICKET.Autofill", "opened-not-filled detail=" + last);
        return new AutofillResult(AutofillOutcome.OpenedNotFilled, last);
    }

    /// <summary>True when the tab is on the Ticketing host but not on the create form (Ticketing showed the previous
    /// page). A login page on another host, or an unknown address, never triggers a new navigation.</summary>
    private static bool IsWrongPolarionPage(JsonElement result, Uri formUrl)
    {
        if (!result.TryGetProperty("href", out var h) || h.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(h.GetString(), UriKind.Absolute, out var current)) return false;
        return string.Equals(current.Host, formUrl.Host, StringComparison.OrdinalIgnoreCase) &&
            current.AbsolutePath.StartsWith("/ticketing", StringComparison.OrdinalIgnoreCase) &&
            !new[] { "login", "auth", "sso", "signin", "logon" }.Any(w =>
                current.AbsolutePath.Contains(w, StringComparison.OrdinalIgnoreCase)) &&
            current.AbsoluteUri.IndexOf("form_mode=create", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static async Task<string?> EvalAsync(ICdpSession session, string expression, CancellationToken ct)
    {
        var result = await session.SendAsync("Runtime.evaluate",
            new { expression, returnByValue = true }, ct).ConfigureAwait(false);
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("result", out var inner) &&
            inner.ValueKind == JsonValueKind.Object && inner.TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.String)
            return value.GetString();
        return null;
    }

    /// <summary>
    /// Idempotent: finds the visible "Title:" input and the rich-text description editor (anchored on the
    /// stable GWT debug id), writes only when both exist, then reads both back. Values travel as JSON literals.
    /// </summary>
    internal static string BuildFillScript(string title, string description) =>
        "(function(T,D){var out={found:false,title:false,description:false,href:''};try{out.href=String(location.href);" +
        "function vis(e){var r=e.getBoundingClientRect();return r.width>0&&r.height>0;}" +
        "function lab(e){var l=e.getAttribute('aria-label')||'';var p=e,n=0;" +
        "while(!l&&p&&n<5){var s=p.previousElementSibling;if(s&&s.innerText&&s.innerText.length<60)l=s.innerText;p=p.parentElement;n++;}" +
        "return (l||'').replace(/\\s+/g,' ').trim();}" +
        "var ti=[].slice.call(document.querySelectorAll('input')).filter(function(e){return vis(e)&&/^title:?$/i.test(lab(e));})[0];" +
        "var fr=document.getElementById('gwt-debug-ticketing-rte-area-debug-id');var body=null;" +
        "try{body=fr&&fr.contentDocument&&fr.contentDocument.body;}catch(x){}" +
        "if(!ti||!body||!body.isContentEditable&&body.ownerDocument.designMode!=='on')return JSON.stringify(out);" +
        "out.found=true;" +
        "if(ti.value!==T){ti.focus();Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(ti,T);" +
        "ti.dispatchEvent(new Event('input',{bubbles:true}));ti.dispatchEvent(new Event('change',{bubbles:true}));ti.dispatchEvent(new Event('blur'));}" +
        "out.title=(ti.value===T);" +
        "var doc=body.ownerDocument;var marker=(D.match(/TestLoomREF[A-F0-9]{32}/)||[''])[0];" +
        "if(!marker||body.innerText.indexOf(marker)<0){body.focus();doc.execCommand('selectAll',false,null);" +
        "var html=D.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').split(/\\r?\\n/).join('<br>');" +
        "if(!doc.execCommand('insertHTML',false,html)){doc.execCommand('insertText',false,D);}}" +
        "out.description=marker?(body.innerText.indexOf(marker)>=0):(body.innerText.trim().length>0);" +
        "}catch(e){}return JSON.stringify(out);})(" +
        JsonSerializer.Serialize(title) + "," + JsonSerializer.Serialize(description) + ")";

    // ---------- real browser session (localhost DevTools endpoint) ----------

    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static string? FindBrowser() => new[]
    {
        Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "", "Microsoft", "Edge", "Application", "msedge.exe"),
        Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? "", "Microsoft", "Edge", "Application", "msedge.exe"),
        Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? "", "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "", "Google", "Chrome", "Application", "chrome.exe")
    }.FirstOrDefault(File.Exists);

    private static int ReadActivePort(string profile)
    {
        try
        {
            var file = Path.Combine(profile, "DevToolsActivePort");
            if (!File.Exists(file)) return 0;
            return int.TryParse(File.ReadLines(file).FirstOrDefault(), out var p) && p is > 0 and < 65536 ? p : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static async Task<bool> PortAliveAsync(int port, CancellationToken ct)
    {
        try
        {
            using var r = await Http.GetAsync($"http://127.0.0.1:{port}/json/version", ct).ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }

    private static async Task<string?> FirstPageSocketAsync(int port, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"http://127.0.0.1:{port}/json", ct).ConfigureAwait(false));
        foreach (var t in doc.RootElement.EnumerateArray())
            if (t.TryGetProperty("type", out var type) && type.GetString() == "page" &&
                t.TryGetProperty("webSocketDebuggerUrl", out var ws))
                return ws.GetString();
        return null;
    }

    private static async Task<ICdpSession?> OpenBrowserSessionAsync(CancellationToken ct, string? browserExe)
    {
        string? socketUrl = null;
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TestLoom", "PolarionEdgeProfile");
        // A TestLoom browser left open by a previous TestLoom run still owns the profile: reuse its debugging port
        // (Chromium records it in DevToolsActivePort) instead of launching a second, blocked instance.
        var port = _knownPort > 0 ? _knownPort : ReadActivePort(profile);
        if (port > 0 && await PortAliveAsync(port, ct).ConfigureAwait(false))
        {
            // Our TestLoom browser is still running: open a fresh tab in it.
            using var req = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/json/new?about:blank");
            using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            socketUrl = doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var w) ? w.GetString() : null;
        }
        else
        {
            var exe = browserExe ?? FindBrowser();
            if (exe is null) return null;
            Directory.CreateDirectory(profile);
            // Port 0 = the browser picks a free port and records it in DevToolsActivePort. The previous
            // file belongs to a dead instance (a live one was reused above): drop it before launching.
            try { File.Delete(Path.Combine(profile, "DevToolsActivePort")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            lock (LaunchGate)
            {
                Process.Start(new ProcessStartInfo(exe,
                    $"--remote-debugging-port=0 --user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check about:blank")
                { UseShellExecute = false });
            }
            for (var i = 0; i < 40 && socketUrl is null; i++)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                port = ReadActivePort(profile);
                if (port <= 0) continue;
                try { socketUrl = await FirstPageSocketAsync(port, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
            }
            if (socketUrl is null) return null;
            _knownPort = port;
        }
        if (socketUrl is null) return null;
        var ws = new ClientWebSocket();
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await ws.ConnectAsync(new Uri(socketUrl), connectTimeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ws.Dispose();
            throw new TimeoutException("DevTools connection timed out.");
        }
        _knownPort = port;
        return new WebSocketCdpSession(ws);
    }

    private sealed class WebSocketCdpSession(ClientWebSocket socket) : ICdpSession
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
        private int _id;

        public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _id);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(CommandTimeout);
            try { return await SendCoreAsync(id, method, parameters, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("DevTools command timed out: " + method);
            }
        }

        private async Task<JsonElement> SendCoreAsync(int id, string method, object? parameters, CancellationToken ct)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } });
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                        throw new IOException("DevTools connection closed.");
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                using var doc = JsonDocument.Parse(ms.ToArray());
                if (doc.RootElement.TryGetProperty("id", out var got) && got.GetInt32() == id)
                    return doc.RootElement.TryGetProperty("result", out var result) ? result.Clone() : default;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { }
            socket.Dispose();
        }
    }
}
