using System.Globalization;
using System.Net;
using System.Text;

namespace TestLoom.Core;

/// <summary>
/// Self-contained, printable HTML report (no scripts, no external resources). Every value is HTML-encoded.
/// </summary>
public static class WorkspaceHtmlReport
{
    public static string Generate(WorkspaceDocument workspace, DateTimeOffset? generatedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        WorkspaceStore.Validate(workspace);
        var stats = WorkspaceStatistics.Calculate(workspace);
        var coverage = RequirementCoverage.Calculate(workspace);
        var when = (generatedUtc ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var keys = workspace.Requirements.ToDictionary(r => r.Id, r => r.Key);

        var h = new StringBuilder();
        h.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        h.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">");
        h.Append("<title>").Append(E(workspace.Name)).Append(" – test report</title><style>");
        h.Append("body{font:14px/1.45 system-ui,Segoe UI,sans-serif;margin:2rem;color:#1b1f23}h1{margin:0 0 .25rem}");
        h.Append("table{border-collapse:collapse;width:100%;margin:.5rem 0 1.5rem}th,td{border:1px solid #c9d1d9;padding:.35rem .5rem;text-align:left;vertical-align:top}");
        h.Append("th{background:#f0f3f6}.kpi{display:inline-block;margin:0 1rem .5rem 0;padding:.5rem .8rem;border:1px solid #c9d1d9;border-radius:6px}");
        h.Append(".kpi b{display:block;font-size:1.4rem}.p{color:#1a7f37}.f{color:#cf222e}.b{color:#9a6700}@media print{body{margin:1cm}}");
        h.Append("</style></head><body>");
        h.Append("<h1>").Append(E(workspace.Name)).Append("</h1><div>Generated ").Append(when).Append("</div><h2>Summary</h2>");
        Kpi(h, "Tests", stats.ActiveTests, ""); Kpi(h, "Passed", stats.Passed, "p"); Kpi(h, "Failed", stats.Failed, "f");
        Kpi(h, "Blocked", stats.Blocked, "b"); Kpi(h, "Not run", stats.NotRun, "");
        h.Append("<div class=\"kpi\"><b>").Append(stats.PassRatePercent.ToString("0.#", CultureInfo.InvariantCulture)).Append("%</b>Pass rate</div>");
        h.Append("<div class=\"kpi\"><b>").Append(stats.RequirementCoveragePercent.ToString("0.#", CultureInfo.InvariantCulture)).Append("%</b>Requirement coverage</div>");
        Kpi(h, "Open defects", stats.OpenDefects, stats.OpenCriticalDefects > 0 ? "f" : "");

        h.Append("<h2>Test cases</h2><table><tr><th>Title</th><th>Template</th><th>Latest outcome</th><th>Requirements</th><th>Steps</th></tr>");
        foreach (var t in workspace.TestCases.Where(t => !t.Archived))
        {
            h.Append("<tr><td>").Append(E(t.Title)).Append("</td><td>").Append(E(t.TemplateName)).Append("</td><td>")
             .Append(E(t.LatestResult)).Append("</td><td>").Append(E(string.Join(", ", t.RequirementIds.Select(id => keys[id]))))
             .Append("</td><td>").Append(t.Steps.Count.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>");
        }
        h.Append("</table><h2>Requirements traceability</h2><table><tr><th>Key</th><th>Title</th><th>Tests</th><th>Passing</th><th>Failing</th><th>Blocked</th></tr>");
        foreach (var r in coverage.Items)
            h.Append("<tr><td>").Append(E(r.Key)).Append("</td><td>").Append(E(r.Title)).Append("</td><td>").Append(r.LinkedTests)
             .Append("</td><td>").Append(r.PassingTests).Append("</td><td>").Append(r.FailingTests).Append("</td><td>").Append(r.BlockedTests).Append("</td></tr>");
        h.Append("</table><h2>Defects</h2><table><tr><th>Key</th><th>Title</th><th>Severity</th><th>Status</th></tr>");
        foreach (var d in workspace.Defects)
            h.Append("<tr><td>").Append(E(d.Key)).Append("</td><td>").Append(E(d.Title)).Append("</td><td>").Append(d.Severity)
             .Append("</td><td>").Append(d.Status).Append("</td></tr>");
        h.Append("</table></body></html>");
        return h.ToString();
    }

    private static void Kpi(StringBuilder h, string label, int value, string css) =>
        h.Append("<div class=\"kpi\"><b").Append(css.Length == 0 ? "" : " class=\"" + css + "\"").Append('>')
         .Append(value.ToString(CultureInfo.InvariantCulture)).Append("</b>").Append(label).Append("</div>");

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
}
