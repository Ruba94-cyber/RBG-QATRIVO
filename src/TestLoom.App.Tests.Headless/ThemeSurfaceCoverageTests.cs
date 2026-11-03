using System.Text.RegularExpressions;

namespace TestLoom.App.Tests;

/// <summary>
/// Prevent regressions where a white/light hardcoded surface or dark foreground
/// becomes invisible when the user switches to Dark at runtime.
/// </summary>
public sealed class ThemeSurfaceCoverageTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "TestLoom.App", "App.axaml")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "TestLoom.App");
    }

    [Theory]
    [InlineData("GraficoCard.axaml")]
    [InlineData("GraficoWindow.axaml")]
    [InlineData("OperatoriView.axaml")]
    [InlineData("ListaTestoEditorWindow.axaml")]
    [InlineData("OrdinaValoriWindow.axaml")]
    [InlineData("PolarionSelezioneDialog.axaml")]
    [InlineData("RecoveryBinView.axaml")]
    public void PannelliPrincipali_SenzaColoriChiariFissi(string name)
    {
        var xaml = File.ReadAllText(Path.Combine(Root(), "Views", name));
        var unthemed = Regex.Matches(xaml,
            @"(?:Foreground|Background|BorderBrush)=""(?:#(?:FFFFFF|F8FAFC|E2E8F0|CBD5E1|94A3B8|1E293B|166534|B45309|DC2626))""",
            RegexOptions.IgnoreCase);
        Assert.True(unthemed.Count == 0,
            $"{name}: {unthemed.Count} fixed light-theme colors, use DynamicResource instead.");
    }


    [Fact]
    public void TutteLeView_SenzaTestiScuriOBackdropChiariFissi()
    {
        // Explicit semantic-palette audit across all views (including dialogs and editors).
        // Dark sidebar and translucent modal dimming overlays are intentionally exempt:
        // they do not use light backgrounds under dark-mode text.
        var violations = new List<string>();
        var darkText = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "#1E293B", "#334155", "#166534", "#B45309", "#DC2626"
        };
        var lightSurfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "#FFFFFF", "#F8FAFC", "#F1F5F9", "#EEF2FF", "#E0E7FF", "#DCF8FAFC"
        };
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "Views"), "*.axaml",
                     SearchOption.AllDirectories))
        {
            var xaml = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(xaml,
                         "(?<attribute>Foreground|Background|BorderBrush)=\"(?<value>#[0-9a-fA-F]{6,8}|White|Black)\""))
            {
                var attribute = match.Groups["attribute"].Value;
                var color = match.Groups["value"].Value;
                if ((attribute == "Foreground" && darkText.Contains(color)) ||
                    ((attribute == "Background" || attribute == "BorderBrush") && lightSurfaces.Contains(color)))
                    violations.Add($"{Path.GetFileName(file)}: {attribute}={color}");
            }
        }
        Assert.True(violations.Count == 0,
            "Use DynamicResource for theme-dependent text and surface colors:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void ChartCanvas_UsaSfondoBiancoPerLaGraficaRenderizzataConInchiostroScuro()
    {
        var app = File.ReadAllText(Path.Combine(Root(), "App.axaml"));
        var card = File.ReadAllText(Path.Combine(Root(), "Views", "GraficoCard.axaml"));
        var window = File.ReadAllText(Path.Combine(Root(), "Views", "GraficoWindow.axaml"));
        Assert.Equal(2, Regex.Matches(app, "x:Key=\"ChartCanvasBrush\">#FFFFFF").Count);
        Assert.Contains("Background=\"{DynamicResource ChartCanvasBrush}\"", card);
        Assert.Contains("Background=\"{DynamicResource ChartCanvasBrush}\"", window);
    }

    [Fact]
    public void LoadingOverlay_UsaUnTokenPerVariante()
    {
        var view = File.ReadAllText(Path.Combine(Root(), "Views", "MainWindow.axaml"));
        var app = File.ReadAllText(Path.Combine(Root(), "App.axaml"));
        Assert.Contains("Background=\"{DynamicResource LoadingOverlayBrush}\"", view);
        Assert.Equal(2, Regex.Matches(app, "x:Key=\"LoadingOverlayBrush\"").Count);
    }
}
