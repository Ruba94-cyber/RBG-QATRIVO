using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class CoreFieldNameNormalizerTests
{
    [Theory]
    [InlineData("Field.Name / A-B", "FIELDNAMEAB")]
    [InlineData("(test): 1_2", "TEST12")]
    [InlineData("", "")]
    [InlineData("aB cD", "ABCD")]
    [InlineData("Résumé 2", "RÉSUMÉ2")]
    public void Normalize_RemovesPunctuationAndPreservesUnicode(string input, string expected) =>
        Assert.Equal(expected, FieldNameNormalizer.Normalize(input));

    [Fact]
    public void Normalize_DoesNotConfuseSeparateNumericVersions() =>
        Assert.NotEqual(FieldNameNormalizer.Normalize("Version 1"),
            FieldNameNormalizer.Normalize("Version 10"));
}
