using System.Text;

namespace TestLoom.Core;

/// <summary>Requirement × test matrix (CSV): which tests cover which requirement and how they last ended.</summary>
public static class TraceabilityMatrix
{
    public static string ExportCsv(WorkspaceDocument workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        WorkspaceStore.Validate(workspace);
        var tests = workspace.TestCases.Where(t => !t.Archived).ToArray();
        var output = new StringBuilder();
        Row(output, new[] { "Requirement ID", "Requirement title" }.Concat(tests.Select(t => t.Title)).ToArray());
        foreach (var requirement in workspace.Requirements)
            Row(output, new[] { requirement.Key, requirement.Title }
                .Concat(tests.Select(t => t.RequirementIds.Contains(requirement.Id) ? t.LatestResult : "")).ToArray());
        return output.ToString();
    }

    private static void Row(StringBuilder builder, string[] values)
    {
        builder.AppendJoin(",", values.Select(Escape));
        builder.Append("\r\n");
    }

    private static string Escape(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
