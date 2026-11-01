using System.Text;

namespace TestLoom.Core;

public sealed record CsvImportResult(int Added, int Skipped, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Imports requirements and tests from CSV (comma or semicolon separated, UTF-8, Excel quoting). The import is
/// all-or-nothing: if any row is invalid nothing is added. Rows that duplicate an existing key/title are skipped.
/// </summary>
public static class WorkspaceCsvImport
{
    public const int MaxChars = 5_000_000;
    public const int MaxRows = 5000;

    public static CsvImportResult ImportRequirements(WorkspaceDocument workspace, string? csv)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var table = Parse(csv, out var error);
        if (error is not null) return Fail(error);
        var key = Column(table, "requirement id", "id", "key", "codice", "id requisito");
        var title = Column(table, "title", "titolo");
        var description = Column(table, "description", "descrizione");
        if (key < 0 || title < 0) return Fail("The file needs the columns 'Requirement ID' and 'Title'.");

        var errors = new List<string>();
        var toAdd = new List<WorkspaceRequirement>();
        var skipped = 0;
        var seen = new HashSet<string>(workspace.Requirements.Select(r => r.Key.Trim()), StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < table.Count; i++)
        {
            var row = table[i];
            var k = Cell(row, key); var t = Cell(row, title);
            if (k.Length == 0 && t.Length == 0) continue;
            if (k.Length == 0 || t.Length == 0 || k.Length > 80 || t.Length > 250) { errors.Add($"Row {i + 1}: invalid ID or title."); continue; }
            if (!seen.Add(k)) { skipped++; continue; }
            toAdd.Add(new WorkspaceRequirement { Key = k, Title = t, Description = Cell(row, description) });
        }
        if (errors.Count > 0) return new CsvImportResult(0, 0, errors);
        return Apply(workspace, toAdd.Count, skipped, () => workspace.Requirements.AddRange(toAdd),
            () => { foreach (var r in toAdd) workspace.Requirements.Remove(r); });
    }

    public static CsvImportResult ImportTests(WorkspaceDocument workspace, string? csv)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var table = Parse(csv, out var error);
        if (error is not null) return Fail(error);
        var title = Column(table, "title", "titolo");
        var template = Column(table, "template", "modello");
        var description = Column(table, "description", "descrizione");
        var priority = Column(table, "priority", "priorità", "priorita");
        var tags = Column(table, "tags", "etichette");
        var owner = Column(table, "owner", "responsabile");
        var requirements = Column(table, "requirement ids", "requirement id", "requisiti");
        if (title < 0) return Fail("The file needs a 'Title' column.");

        var byKey = workspace.Requirements.ToDictionary(r => r.Key.Trim(), r => r.Id, StringComparer.OrdinalIgnoreCase);
        var existing = new HashSet<string>(workspace.TestCases.Select(t => t.Title.Trim()), StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var toAdd = new List<WorkspaceTestCase>();
        var skipped = 0;
        for (var i = 1; i < table.Count; i++)
        {
            var row = table[i];
            var t = Cell(row, title);
            if (t.Length == 0 && row.All(c => c.Trim().Length == 0)) continue;
            if (t.Length == 0 || t.Length > 250) { errors.Add($"Row {i + 1}: invalid title."); continue; }
            if (!existing.Add(t)) { skipped++; continue; }

            var test = new WorkspaceTestCase { Title = t, TemplateName = Cell(row, template), Description = Cell(row, description), Owner = Cell(row, owner) };
            if (test.Owner.Length > 120) { errors.Add($"Row {i + 1}: owner is too long."); continue; }
            var p = Cell(row, priority);
            if (p.Length > 0)
            {
                if (!TryPriority(p, out var parsed)) { errors.Add($"Row {i + 1}: unknown priority '{p}'."); continue; }
                test.Priority = parsed;
            }
            test.Tags = Split(Cell(row, tags)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (test.Tags.Any(x => x.Length > 40) || test.Tags.Count > 50) { errors.Add($"Row {i + 1}: invalid tags."); continue; }
            var bad = false;
            foreach (var reference in Split(Cell(row, requirements)))
            {
                if (!byKey.TryGetValue(reference, out var id)) { errors.Add($"Row {i + 1}: unknown requirement '{reference}'."); bad = true; break; }
                if (!test.RequirementIds.Contains(id)) test.RequirementIds.Add(id);
            }
            if (!bad) toAdd.Add(test);
        }
        if (errors.Count > 0) return new CsvImportResult(0, 0, errors);
        return Apply(workspace, toAdd.Count, skipped, () => workspace.TestCases.AddRange(toAdd),
            () => { foreach (var t in toAdd) workspace.TestCases.Remove(t); });
    }

    private static CsvImportResult Apply(WorkspaceDocument workspace, int added, int skipped, Action add, Action undo)
    {
        if (added == 0) return new CsvImportResult(0, skipped, Array.Empty<string>());
        add();
        try { WorkspaceStore.Validate(workspace); }
        catch (InvalidDataException ex) { undo(); return Fail("The imported data is not valid: " + ex.Message); }
        return new CsvImportResult(added, skipped, Array.Empty<string>());
    }

    private static CsvImportResult Fail(string message) => new(0, 0, new[] { message });

    private static bool TryPriority(string text, out WorkspacePriority priority)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "low" or "bassa": priority = WorkspacePriority.Low; return true;
            case "medium" or "media": priority = WorkspacePriority.Medium; return true;
            case "high" or "alta": priority = WorkspacePriority.High; return true;
            case "critical" or "critica": priority = WorkspacePriority.Critical; return true;
        }
        priority = default;
        return false;
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int Column(List<string[]> table, params string[] names)
    {
        if (table.Count == 0) return -1;
        for (var c = 0; c < table[0].Length; c++)
            if (names.Contains(table[0][c].Trim().TrimStart('﻿').ToLowerInvariant())) return c;
        return -1;
    }

    private static string Cell(string[] row, int index)
    {
        if (index < 0 || index >= row.Length) return "";
        var value = row[index].Trim();
        // Undo the apostrophe our own exports prepend to formula-looking text.
        if (value.Length > 1 && value[0] == '\'' && "=+-@".Contains(value[1])) value = value[1..];
        return value;
    }

    /// <summary>Minimal RFC 4180 reader with delimiter detection; rejects oversized input.</summary>
    internal static List<string[]> Parse(string? csv, out string? error)
    {
        error = null;
        var rows = new List<string[]>();
        if (string.IsNullOrWhiteSpace(csv)) { error = "The file is empty."; return rows; }
        if (csv.Length > MaxChars) { error = "The file is too large."; return rows; }
        csv = csv.TrimStart('﻿');
        var firstLine = csv.Split('\n')[0];
        var delimiter = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';

        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == delimiter) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n')
            {
                row.Add(cell.ToString()); cell.Clear();
                rows.Add(row.ToArray()); row.Clear();
                if (rows.Count > MaxRows + 1) { error = "Too many rows."; return rows; }
            }
            else cell.Append(c);
        }
        if (quoted) { error = "The file has an unclosed quote."; return rows; }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        if (rows.Count > MaxRows + 1) error = "Too many rows.";
        return rows;
    }
}
