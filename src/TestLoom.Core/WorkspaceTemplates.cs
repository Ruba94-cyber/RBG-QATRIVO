namespace TestLoom.Core;

/// <summary>Template helpers: seeding from preset blueprints and checking required fields on a test.</summary>
public static class WorkspaceTemplates
{
    public static string FieldKey(string section, string field) => section + "/" + field;

    public static WorkspaceTemplate FromBlueprint(TestTemplateBlueprint blueprint)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        return new WorkspaceTemplate
        {
            Name = blueprint.Name,
            Icon = blueprint.Icon,
            Sections = blueprint.Sections.Select(s => new WorkspaceTemplateSection
            {
                Name = s.Name,
                Fields = s.Fields.Select(f => new WorkspaceTemplateField { Name = f.Name, Required = f.Required }).ToList()
            }).ToList()
        };
    }

    /// <summary>Adds the preset's blueprints that the workspace does not have yet (matched by name). Returns how many were added.</summary>
    public static int SeedFromPreset(WorkspaceDocument workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var added = 0;
        foreach (var blueprint in TestLoomTemplateBlueprints.Create(workspace.PresetId))
        {
            if (workspace.Templates.Any(t => string.Equals(t.Name, blueprint.Name, StringComparison.OrdinalIgnoreCase))) continue;
            workspace.Templates.Add(FromBlueprint(blueprint));
            added++;
        }
        return added;
    }

    public static WorkspaceTemplate? Find(WorkspaceDocument workspace, string? name) =>
        string.IsNullOrWhiteSpace(name) ? null
            : workspace.Templates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<(string Section, string Field, bool Required, string Key)> Fields(WorkspaceTemplate template) =>
        template.Sections.SelectMany(s => s.Fields.Select(f => (s.Name, f.Name, f.Required, FieldKey(s.Name, f.Name))));

    /// <summary>Keys of required template fields that are empty on this test.</summary>
    public static IReadOnlyList<string> MissingRequired(WorkspaceTestCase test, WorkspaceTemplate? template)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (template is null) return Array.Empty<string>();
        return Fields(template)
            .Where(f => f.Required && !(test.FieldValues.TryGetValue(f.Key, out var v) && !string.IsNullOrWhiteSpace(v)))
            .Select(f => f.Key).ToArray();
    }

    /// <summary>Sets or clears one field value (an empty value removes the entry).</summary>
    public static void SetValue(WorkspaceTestCase test, string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (string.IsNullOrWhiteSpace(value)) test.FieldValues.Remove(key);
        else test.FieldValues[key] = value.Trim();
    }
}
