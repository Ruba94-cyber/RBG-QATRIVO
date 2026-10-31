using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class TestLoomWorkspacePresetsTests
{
    [Fact]
    public void EachPresetHasStableUniqueIdentifierAndUsableLabels()
    {
        var presets = TestLoomWorkspacePresets.All;
        Assert.Equal(5, presets.Count);
        Assert.Equal(presets.Count, presets.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var preset in presets)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Id));
            Assert.False(string.IsNullOrWhiteSpace(preset.SubjectLabel));
            Assert.False(string.IsNullOrWhiteSpace(preset.ComponentLabel));
            Assert.False(string.IsNullOrWhiteSpace(preset.PlanLabel));
            Assert.NotNull(TestLoomWorkspacePresets.Find(preset.Id.ToUpperInvariant()));
        }
    }

    [Fact]
    public void BlankPresetDoesNotPrepopulateTemplates()
    {
        Assert.Empty(TestLoomWorkspacePresets.Find("blank")!.SuggestedTestTemplates);
    }

    [Fact]
    public void SuggestedTemplatesAreUniqueWithinPreset()
    {
        foreach (var preset in TestLoomWorkspacePresets.All)
            Assert.Equal(preset.SuggestedTestTemplates.Count,
                preset.SuggestedTestTemplates.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
