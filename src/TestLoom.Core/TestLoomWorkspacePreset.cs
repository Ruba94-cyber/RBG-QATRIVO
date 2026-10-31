namespace TestLoom.Core;

/// <summary>
/// Domain-neutral metadata for a future public first-run workflow.
/// Construction only: it does not alter existing persisted data or workspaces.
/// </summary>
public sealed record TestLoomWorkspacePreset(
    string Id,
    string DisplayName,
    string SubjectLabel,
    string ComponentLabel,
    string PlanLabel,
    IReadOnlyList<string> SuggestedTestTemplates);

/// <summary>
/// Initial neutral workspace vocabularies. The public onboarding UI can select
/// one of these without making any industry-specific schema assumptions.
/// </summary>
public static class TestLoomWorkspacePresets
{
    private static readonly IReadOnlyList<TestLoomWorkspacePreset> AllPresets =
        Array.AsReadOnly(new[]
        {
            new TestLoomWorkspacePreset(
                "software-qa", "Software QA", "Application", "Module", "Test suite",
                Array.AsReadOnly(new[] { "Smoke test", "Regression test", "Compatibility test" })),
            new TestLoomWorkspacePreset(
                "hardware", "Hardware Engineering", "Device", "Component", "Test plan",
                Array.AsReadOnly(new[] { "Functional verification", "Endurance test", "Safety check" })),
            new TestLoomWorkspacePreset(
                "manufacturing", "Manufacturing Quality", "Product", "Assembly", "Inspection plan",
                Array.AsReadOnly(new[] { "Incoming inspection", "In-process inspection", "Final inspection" })),
            new TestLoomWorkspacePreset(
                "laboratory", "Research Laboratory", "Sample", "Parameter", "Test protocol",
                Array.AsReadOnly(new[] { "Procedure verification", "Measurement series", "Repeatability check" })),
            new TestLoomWorkspacePreset(
                "blank", "Blank workspace", "Item", "Component", "Test plan",
                Array.Empty<string>())
        });

    public static IReadOnlyList<TestLoomWorkspacePreset> All => AllPresets;

    public static TestLoomWorkspacePreset? Find(string id) =>
        AllPresets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
