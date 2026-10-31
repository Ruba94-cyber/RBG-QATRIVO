namespace TestLoom.Core;

/// <summary>
/// Industry-neutral, immutable blueprint for initial editable test templates.
/// Public UI and private adapters can translate the same definition into their
/// own persistence models without duplicating presets or importing other data.
/// </summary>
public sealed record TestTemplateBlueprint(
    string Name,
    string Icon,
    IReadOnlyList<TestSectionBlueprint> Sections);

public sealed record TestSectionBlueprint(
    string Name,
    IReadOnlyList<TestFieldBlueprint> Fields);

public sealed record TestFieldBlueprint(string Name, bool Required = false);

public static class TestLoomTemplateBlueprints
{
    public static IReadOnlyList<TestTemplateBlueprint> Create(string presetId)
    {
        var preset = TestLoomWorkspacePresets.Find(presetId)
            ?? throw new ArgumentException("Unknown workspace preset.", nameof(presetId));

        var contextualField = preset.Id switch
        {
            "software-qa" => "Execution environment",
            "hardware" => "Device identifier",
            "manufacturing" => "Lot or batch number",
            "laboratory" => "Sample identifier",
            _ => "Reference"
        };

        return Array.AsReadOnly(preset.SuggestedTestTemplates
            .Select(name => new TestTemplateBlueprint(
                name,
                "🧪",
                Array.AsReadOnly(new[]
                {
                    new TestSectionBlueprint(
                        "Test details",
                        Array.AsReadOnly(new[]
                        {
                            new TestFieldBlueprint("Objective"),
                            new TestFieldBlueprint(contextualField),
                            new TestFieldBlueprint("Acceptance criteria"),
                        }))
                })))
            .ToArray());
    }
}
