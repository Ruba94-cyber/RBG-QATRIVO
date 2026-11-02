using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.Setup;

namespace TestLoom.App.Tests;

/// <summary>The templates of the first-run "starting point" (kind of testing).</summary>
public sealed class StartingPointTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "TestLoom-StartingPoint-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    [Theory]
    [InlineData("software-qa", 3)]
    [InlineData("hardware", 3)]
    [InlineData("manufacturing", 3)]
    [InlineData("laboratory", 3)]
    [InlineData("blank", 1)]
    public void EachStartingPointBuildsItsOwnTemplates(string presetId, int expected)
    {
        var templates = TestLoomTemplateFactory.Create(presetId);

        Assert.Equal(expected, templates.Count);
        Assert.Equal(expected, templates.Select(t => t.Nome).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(expected, templates.Select(t => t.Uid).Distinct().Count());
        Assert.All(templates, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Nome));
            Assert.NotEmpty(t.Sezioni);
            Assert.Empty(t.StrumentiPreselezionati); // no tool of another workflow is pre-selected
        });
    }

    [Fact]
    public void EveryKnownStartingPointCanBeBuilt()
    {
        foreach (var preset in TestLoomWorkspacePresets.All)
            Assert.NotEmpty(TestLoomTemplateFactory.Create(preset.Id));
    }

    [Fact]
    public void TwoCallsGiveIndependentTemplates()
    {
        var first = TestLoomTemplateFactory.Create("software-qa");
        var second = TestLoomTemplateFactory.Create("software-qa");
        first[0].Nome = "Changed";
        first[0].Sezioni.Clear();

        Assert.NotEqual("Changed", second[0].Nome);
        Assert.NotEmpty(second[0].Sezioni);
        Assert.NotEqual(first[0].Uid, second[0].Uid);
    }

    [Fact]
    public void TheTemplatesCarrySectionsAndFieldsOfTheStartingPoint()
    {
        var template = TestLoomTemplateFactory.Create("manufacturing")[0];

        var custom = template.Sezioni.Single(s => s.Nome == "Test details");
        Assert.Contains(custom.Campi, f => f.Nome == "Lot or batch number");
        Assert.Equal(Enumerable.Range(0, custom.Campi.Count), custom.Campi.Select(f => f.Ordine));
    }

    [Fact]
    public void SavedTemplatesComeBackFromTheFolder()
    {
        Directory.CreateDirectory(_folder);
        var file = new DataPaths(_folder).TestTemplateFile;
        var written = TestLoomTemplateFactory.Create("laboratory").ToList();

        new MasterDataRepository<TestTemplate>(file).Save(written);
        var read = new MasterDataRepository<TestTemplate>(file).Load();

        Assert.Equal(written.Select(t => t.Nome), read.Select(t => t.Nome));
        Assert.Equal(written.Select(t => t.Uid), read.Select(t => t.Uid));
    }
}
