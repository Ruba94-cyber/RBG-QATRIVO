using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class FieldCopyTransactionTests
{
    [Fact]
    public void ApplyAndUndoPreservesExistingMissingAndEmptyFieldState()
    {
        var fields = new Dictionary<string, string>
        {
            ["existing"] = "before",
            ["empty"] = ""
        };

        var undo = FieldCopyTransaction.Apply(fields, new[]
        {
            new KeyValuePair<string, string>("existing", "after"),
            new KeyValuePair<string, string>("missing", "added"),
            new KeyValuePair<string, string>("empty", "filled"),
        });

        Assert.Equal("after", fields["existing"]);
        Assert.Equal("added", fields["missing"]);
        Assert.Equal("filled", fields["empty"]);
        Assert.True(undo.IsUnchangedAfterApply);

        undo.Undo();

        Assert.Equal("before", fields["existing"]);
        Assert.Equal("", fields["empty"]);
        Assert.False(fields.ContainsKey("missing"));
    }

    [Fact]
    public void ModifiedDestinationPreventsPartialUndo()
    {
        var fields = new Dictionary<string, string> { ["a"] = "old", ["b"] = "older" };
        var undo = FieldCopyTransaction.Apply(fields, new[]
        {
            new KeyValuePair<string, string>("a", "new"),
            new KeyValuePair<string, string>("b", "newer")
        });
        fields["b"] = "manual change";

        Assert.False(undo.IsUnchangedAfterApply);
        Assert.Throws<InvalidOperationException>(() => undo.Undo());
        Assert.Equal("new", fields["a"]);
        Assert.Equal("manual change", fields["b"]);
    }

    [Fact]
    public void DuplicateKeysRestoreOriginalValueRatherThanIntermediateValue()
    {
        var fields = new Dictionary<string, string> { ["key"] = "start" };
        var undo = FieldCopyTransaction.Apply(fields, new[]
        {
            new KeyValuePair<string, string>("key", "first"),
            new KeyValuePair<string, string>("key", "last")
        });

        Assert.Equal("last", fields["key"]);
        Assert.True(undo.IsUnchangedAfterApply);
        undo.Undo();
        Assert.Equal("start", fields["key"]);
    }

    [Fact]
    public void CaseInsensitiveDictionaryUsesItsOwnKeyComparer()
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Name"] = "old" };
        var undo = FieldCopyTransaction.Apply(fields, new[]
        {
            new KeyValuePair<string, string>("name", "new")
        });

        Assert.Single(fields);
        Assert.True(undo.IsUnchangedAfterApply);
        undo.Undo();
        Assert.Equal("old", fields["NAME"]);
    }

    [Fact]
    public void EnumerationFailureDoesNotChangeDestination()
    {
        var fields = new Dictionary<string, string> { ["key"] = "untouched" };

        IEnumerable<KeyValuePair<string, string>> FailingSequence()
        {
            yield return new KeyValuePair<string, string>("key", "wrong");
            throw new IOException("Source failed before materialization");
        }

        Assert.Throws<IOException>(() => FieldCopyTransaction.Apply(fields, FailingSequence()));
        Assert.Equal("untouched", fields["key"]);
    }

    [Fact]
    public void EmptyCopyLeavesDestinationUntouched()
    {
        var fields = new Dictionary<string, string> { ["key"] = "same" };
        var undo = FieldCopyTransaction.Apply(fields, Array.Empty<KeyValuePair<string, string>>());
        Assert.True(undo.IsUnchangedAfterApply);
        undo.Undo();
        Assert.Equal("same", fields["key"]);
    }
}
