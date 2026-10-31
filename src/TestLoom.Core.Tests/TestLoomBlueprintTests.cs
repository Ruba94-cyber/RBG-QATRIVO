using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class TestLoomBlueprintTests
{
    [Theory]
    [InlineData("software-qa")]
    [InlineData("hardware")]
    [InlineData("manufacturing")]
    [InlineData("laboratory")]
    public void GenericProfilesProduceThreeEditableTemplates(string presetId)
    {
        var blueprints = TestLoomTemplateBlueprints.Create(presetId);
        Assert.Equal(3, blueprints.Count);
        Assert.Equal(3, blueprints.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(blueprints, blueprint =>
        {
            Assert.Single(blueprint.Sections);
            Assert.Equal(3, blueprint.Sections[0].Fields.Count);
            Assert.All(blueprint.Sections[0].Fields, field => Assert.False(string.IsNullOrWhiteSpace(field.Name)));
        });
    }

    [Fact]
    public void BlankProfileHasNoForcedTemplates() =>
        Assert.Empty(TestLoomTemplateBlueprints.Create("blank"));

    [Fact]
    public void InvalidProfileThrows() =>
        Assert.Throws<ArgumentException>(() => TestLoomTemplateBlueprints.Create("unknown"));
}
