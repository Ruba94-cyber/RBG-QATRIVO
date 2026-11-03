using Avalonia.Styling;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class ThemeAppearancePreferenceTests
{
    [Fact]
    public void NuovaConfigurazione_RestaChiaraPerCompatibilita()
    {
        var config = new AppConfig();
        Assert.Equal("Light", config.TemaInterfaccia);
        Assert.Equal(ThemeVariant.Light, ThemeAppearanceService.FromPreference(config.TemaInterfaccia));
    }

    [Theory]
    [InlineData("Light", "Light")]
    [InlineData("Dark", "Dark")]
    [InlineData("System", "Default")]
    [InlineData("dark", "Dark")]
    [InlineData(null, "Light")]
    [InlineData("valore-sconosciuto", "Light")]
    public void PreferenzaTema_RisoltaSenzaEccezioni(string? scelta, string expected)
    {
        Assert.Equal(expected, ThemeAppearanceService.FromPreference(scelta).ToString());
    }
}
