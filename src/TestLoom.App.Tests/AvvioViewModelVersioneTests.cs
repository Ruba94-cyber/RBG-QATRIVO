using TestLoom.App.ViewModels;
using Xunit;

namespace TestLoom.App.Tests;

public class AvvioViewModelVersioneTests
{
    [Theory]
    [InlineData("7.2.0+a1b2c3d", "v7.2.0")]
    [InlineData("7.2.0", "v7.2.0")]
    [InlineData("8.0.0-beta.1+abc", "v8.0.0-beta.1")]
    [InlineData("  7.2.0 + abc ", "v7.2.0")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("+solo-hash", "")]
    [InlineData(null, "")]
    public void FormattaVersione_rimuove_il_suffisso_di_build(string? informazionale, string atteso) =>
        Assert.Equal(atteso, AvvioViewModel.FormattaVersione(informazionale));

    [Fact]
    public void VersioneApplicazione_non_espone_l_hash_di_build()
    {
        var versione = AvvioViewModel.VersioneApplicazione;
        Assert.DoesNotContain('+', versione);
        Assert.False(string.IsNullOrWhiteSpace(versione));
        Assert.StartsWith("v", versione);
    }
}
