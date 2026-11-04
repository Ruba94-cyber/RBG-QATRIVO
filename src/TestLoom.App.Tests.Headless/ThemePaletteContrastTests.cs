using System.Globalization;
using System.Xml.Linq;

namespace TestLoom.App.Tests;

/// <summary>
/// Regression gate for the accessible theme palette: every contrast pair is computed from
/// the actual Avalonia ThemeDictionaries, never from a duplicated color palette.
/// Contrast is measured against normal-size text (WCAG AA >= 4.5:1).
/// </summary>
public sealed class ThemePaletteContrastTests
{
    private static string AppXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "TestLoom.App", "App.axaml")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "TestLoom.App", "App.axaml");
    }

    private static Dictionary<string, string> Palette(string variant)
    {
        var doc = XDocument.Load(AppXaml());
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var theme = doc.Descendants().Single(e =>
            e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(x + "Key") == variant);
        return theme.Descendants()
            .Where(e => e.Name.LocalName == "SolidColorBrush" && e.Attribute(x + "Key") is not null)
            .ToDictionary(e => (string)e.Attribute(x + "Key")!, e => e.Value.Trim());
    }

    private static double Luminance(string color)
    {
        var hex = color.TrimStart('#');
        Assert.Equal(6, hex.Length);
        var values = Enumerable.Range(0, 3).Select(i =>
        {
            var v = int.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber) / 255.0;
            return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
        }).ToArray();
        return .2126 * values[0] + .7152 * values[1] + .0722 * values[2];
    }

    private static double Ratio(string foreground, string background)
    {
        var a = Luminance(foreground);
        var b = Luminance(background);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void TestoPrincipaleSecondarioESplash_RaggiungonoWcagAa(string variant)
    {
        var p = Palette(variant);
        var pairs = new (string Foreground, string Background)[]
        {
            ("PrimaryTextBrush", "WindowBackgroundBrush"),
            ("PrimaryTextBrush", "SurfaceBrush"),
            ("SecondaryTextBrush", "SurfaceBrush"),
            ("TextMutedBrush", "SurfaceMutedBrush"),
            ("PrimaryTextBrush", "ChipBackgroundBrush"),
            ("TextOnDarkBrush", "SplashBackgroundBrush"),
            ("TextOnDarkSoftBrush", "SplashBackgroundBrush"),
            ("TextOnDarkMutedBrush", "SplashBackgroundBrush")
        };
        foreach (var (fg, bg) in pairs)
            Assert.True(Ratio(p[fg], p[bg]) >= 4.5,
                $"{variant} {fg} on {bg} has {Ratio(p[fg], p[bg]):F2}:1, below WCAG AA");
    }

    [Fact]
    public void AuditDialoghiGeneratiInCodeBehind_ErroreEAvvisoRaggiungonoWcagAaInDark()
    {
        var p = Palette("Dark");
        var source = File.ReadAllText(Path.Combine(Path.GetDirectoryName(AppXaml())!, "Views", "AuditView.axaml.cs"));
        Assert.Contains("private static IBrush PennelloErrore", source);
        Assert.Contains("private static IBrush PennelloAvviso", source);
        Assert.Contains("#F17D82", source);
        Assert.Contains("#E2AD54", source);
        foreach (var surface in new[] { "WindowBackgroundBrush", "SurfaceBrush", "SurfaceMutedBrush" })
        {
            Assert.True(Ratio("#F17D82", p[surface]) >= 4.5,
                $"Audit error text has low contrast on {surface}");
            Assert.True(Ratio("#E2AD54", p[surface]) >= 4.5,
                $"Audit warning text has low contrast on {surface}");
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void PulsantiESegnalazioni_RaggiungonoWcagAa(string variant)
    {
        var p = Palette(variant);
        Assert.True(Ratio("#FFFFFF", p["PrimaryButtonBrush"]) >= 4.5,
            $"{variant}: white button text on PrimaryButtonBrush insufficient");
        Assert.True(Ratio("#FFFFFF", p["PrimaryHoverBrush"]) >= 4.5,
            $"{variant}: white button hover text insufficient");
        Assert.True(Ratio("#FFFFFF", p["SelectionBrush"]) >= 4.5,
            $"{variant}: selected row text insufficient");
        Assert.True(Ratio(p["WarningTextBrush"], p["WarningBackgroundBrush"]) >= 4.5,
            $"{variant}: warning label contrast insufficient");
        Assert.True(Ratio(p["SuccessBrush"], p["SuccessBackgroundBrush"]) >= 4.5,
            $"{variant}: success label contrast insufficient");
    }
}
