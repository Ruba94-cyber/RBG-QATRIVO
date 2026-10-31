using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class StepPasteParserTests
{
    [Fact]
    public void ReadsTwoColumnRowsInOrder()
    {
        var r = StepPasteParser.Parse("Open app\tHome shown\r\nSelect item\tDetails shown\r\n");
        Assert.True(r.Success);
        Assert.Equal(2, r.Steps.Count);
        Assert.Equal("Open app", r.Steps[0].Action);
        Assert.Equal("Details shown", r.Steps[1].ExpectedResult);
    }

    [Fact]
    public void HeaderIsSkippedAndSwappedColumnsAreHonoured()
    {
        var r = StepPasteParser.Parse("Expected result\tAction\nHome shown\tOpen app");
        Assert.True(r.Success);
        Assert.Single(r.Steps);
        Assert.Equal("Open app", r.Steps[0].Action);
        Assert.Equal("Home shown", r.Steps[0].ExpectedResult);
    }

    [Fact]
    public void QuotedCellsKeepTabsQuotesAndNewlines()
    {
        var r = StepPasteParser.Parse("\"Line 1\nLine \"\"2\"\"\ttab\"\t\"Ok\"");
        Assert.True(r.Success);
        Assert.Equal("Line 1\nLine \"2\"\ttab", r.Steps[0].Action);
        Assert.Equal("Ok", r.Steps[0].ExpectedResult);
    }

    [Theory]
    [InlineData("only one column")]
    [InlineData("a\tb\tc")]
    [InlineData("a\t")]
    [InlineData("\"unterminated\tb")]
    public void AnyInvalidRowRejectsTheWholePaste(string text)
    {
        var r = StepPasteParser.Parse("good\tfine\n" + text);
        Assert.False(r.Success);
        Assert.Empty(r.Steps);
        Assert.Equal(StepPasteParser.PasteError.InvalidColumns, r.Error);
    }

    [Fact]
    public void BoundsAreEnforced()
    {
        var many = string.Join("\n", Enumerable.Range(0, StepPasteParser.MaxRows + 1).Select(i => $"a{i}\tb{i}"));
        Assert.Equal(StepPasteParser.PasteError.TooManyRows, StepPasteParser.Parse(many).Error);
        var longRow = new string('x', StepPasteParser.MaxRowChars + 1) + "\tb";
        Assert.Equal(StepPasteParser.PasteError.RowTooLong, StepPasteParser.Parse(longRow).Error);
    }

    [Fact]
    public void EmptyTextIsAnEmptySuccess()
    {
        Assert.True(StepPasteParser.Parse("  \n ").Success);
        Assert.Empty(StepPasteParser.Parse(null).Steps);
    }
}
