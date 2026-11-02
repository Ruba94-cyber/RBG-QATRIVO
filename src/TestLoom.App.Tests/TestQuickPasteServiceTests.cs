using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class TestQuickPasteServiceTests
{
    private static readonly string[] Functions = ["Diagnostics", "Calibration"];

    [Fact]
    public void ExcelTsvWithHeadersSupportsBothColumnOrders()
    {
        var normal = TestQuickPasteService.Parse("Function\tNotes\nDiagnostics\tRead sensors\nCalibration\tRun routine", Functions, "");
        Assert.True(normal.Success);
        Assert.Equal(2, normal.Operations.Count);
        Assert.Equal("Read sensors", normal.Operations[0].Note);
        Assert.Equal("Calibration", normal.Operations[1].Function);

        var reversed = TestQuickPasteService.Parse("Nota\tFunzionalità\nVerifica sensori\tDiagnostics", Functions, "");
        Assert.True(reversed.Success);
        Assert.Equal("Verifica sensori", Assert.Single(reversed.Operations).Note);
        Assert.Equal("Diagnostics", Assert.Single(reversed.Operations).Function);
    }

    [Fact]
    public void PlainNotesAndHeaderlessTsvStayCompatible()
    {
        var plain = TestQuickPasteService.Parse("One note\nSecond note", Functions, "Diagnostics");
        Assert.Equal(2, plain.Operations.Count);
        Assert.All(plain.Operations, row => Assert.Equal("Diagnostics", row.Function));
        var tsv = TestQuickPasteService.Parse("Calibration\tSecond operation", Functions, "Diagnostics");
        Assert.Equal("Calibration", Assert.Single(tsv.Operations).Function);
    }

    [Fact]
    public void UnexpectedOutcomeColumnIsRejectedAtomically()
    {
        var result = TestQuickPasteService.Parse("Function\tNotes\tOutcome\nDiagnostics\tValid\tConforme", Functions, "");
        Assert.False(result.Success);
        Assert.Equal(TestQuickPasteService.ParseError.InvalidColumns, result.Error);
        Assert.Empty(result.Operations);
    }

    [Fact]
    public void UnknownFunctionAndOversizedBatchesDoNotReturnPartialOperations()
    {
        var invalid = TestQuickPasteService.Parse("Diagnostics\tOkay\nUnknown\tNot okay", Functions, "");
        Assert.Equal(TestQuickPasteService.ParseError.UnknownFunction, invalid.Error);
        Assert.Empty(invalid.Operations);
        var huge = TestQuickPasteService.Parse(string.Join("\n", Enumerable.Repeat("a", 51)), Functions, "");
        Assert.Equal(TestQuickPasteService.ParseError.TooManyRows, huge.Error);
        Assert.Empty(huge.Operations);
    }
    [Fact]
    public void QuotedExcelCellsKeepEmbeddedNewlinesTabsAndDoubleQuotesInOneOperation()
    {
        var content = "Function\tNotes\r\nDiagnostics\t\"Line one\r\nLine\two with \"\"quotes\"\"\"\r\nCalibration\tNext";
        var parsed = TestQuickPasteService.Parse(content, Functions, "");
        Assert.True(parsed.Success);
        Assert.Equal(2, parsed.Operations.Count);
        Assert.Equal("Line one\r\nLine\two with \"quotes\"", parsed.Operations[0].Note);
        Assert.Equal("Calibration", parsed.Operations[1].Function);
    }

    [Theory]
    [InlineData("Function\tNotes\nDiagnostics\t\"Unterminated")]
    [InlineData("Function\tNotes\nDiagnostics\t\"Closed\"extra")]
    [InlineData("Function\tNotes\nDiagnostics\t\"Closed\"\textra")]
    [InlineData("Function\tNotes\nDiagnostics\tSample\tUnexpected")]
    public void InvalidQuotedOrExtraColumnsAreRejectedWithoutPartialRows(string content)
    {
        var parsed = TestQuickPasteService.Parse(content, Functions, "");
        Assert.False(parsed.Success);
        Assert.Empty(parsed.Operations);
    }

    [Fact]
    public void UnknownFunctionReportsTheExcelRowAfterMultilineQuotedCell()
    {
        var source = "Function\tNotes\r\nDiagnostics\t\"First line\r\nSecond line\"\r\nUnknown\tNo match";
        var parsed = TestQuickPasteService.Parse(source, Functions, "Diagnostics");
        Assert.False(parsed.Success);
        Assert.Equal(TestQuickPasteService.ParseError.UnknownFunction, parsed.Error);
        Assert.Equal("Unknown", parsed.Detail);
        Assert.Equal(3, parsed.RowNumber);
        Assert.Empty(parsed.Operations);
    }

    [Fact]
    public void InvalidQuotedTextReportsLogicalRowWithoutPartialOperations()
    {
        var parsed = TestQuickPasteService.Parse(
            "Function\tNotes\r\nDiagnostics\tValid\r\nCalibration\t\"Unclosed", Functions, "");
        Assert.False(parsed.Success);
        Assert.Equal(TestQuickPasteService.ParseError.InvalidColumns, parsed.Error);
        Assert.Equal(3, parsed.RowNumber);
        Assert.Empty(parsed.Operations);
    }

    [Fact]
    public void MoreThanFiftyRowsReportsTheFirstDisallowedRow()
    {
        var parsed = TestQuickPasteService.Parse(
            string.Join("\n", Enumerable.Repeat("notes", 51)), Functions, "Diagnostics");
        Assert.False(parsed.Success);
        Assert.Equal(TestQuickPasteService.ParseError.TooManyRows, parsed.Error);
        Assert.Equal(51, parsed.RowNumber);
        Assert.Empty(parsed.Operations);
    }

    [Fact]
    public void HeaderAloneCreatesNoOperations()
    {
        var parsed = TestQuickPasteService.Parse("Function\tNotes\r\n", Functions, "");
        Assert.True(parsed.Success);
        Assert.Empty(parsed.Operations);
    }

    [Fact]
    public void ExactlyFiftyLogicalRowsAreAllowedEvenWhenNotesContainLineBreaks()
    {
        var rows = new List<string> { "Function\tNotes" };
        rows.AddRange(Enumerable.Range(1, 50).Select(n => $"Diagnostics\t\"Step {n}\r\nextra detail\""));
        var parsed = TestQuickPasteService.Parse(string.Join("\r\n", rows), Functions, "");
        Assert.True(parsed.Success);
        Assert.Equal(50, parsed.Operations.Count);
    }

}
