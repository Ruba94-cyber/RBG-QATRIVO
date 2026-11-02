using System.Text.RegularExpressions;

namespace TestLoom.App.Tests;

/// <summary>Ogni finestra (principale e dialoghi) ha uno sfondo opaco. Senza Background la finestra Avalonia resta
/// trasparente e lascia vedere cio' che c'e' sotto: e' cio' che si vedeva sul dialogo "Campi: Pianificazione" (6.6.1).
/// Il test garantisce che lo sfondo arrivi dallo stile "Window" con un token opaco, e che nessuna finestra lo tolga o lo
/// renda trasparente con Background o TransparencyLevelHint.</summary>
public class XamlSfondoFinestreTests
{
    private static string Radice()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "TestLoom.App", "Views"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, "src", "TestLoom.App");
    }

    private static IEnumerable<string> FinestreXaml() =>
        Directory.EnumerateFiles(Path.Combine(Radice(), "Views"), "*.axaml")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"<Window\b"));

    [Fact]
    public void StileWindow_ImpostaLoSfondoDalTokenOpaco()
    {
        var stili = File.ReadAllText(Path.Combine(Radice(), "Styles.axaml"));
        Assert.Matches("<Style Selector=\"Window\">\\s*<Setter Property=\"Background\" Value=\"\\{DynamicResource WindowBackgroundBrush\\}\" />", stili);
    }

    [Fact]
    public void TokenWindowBackgroundBrush_EUnColoreOpaco()
    {
        var app = File.ReadAllText(Path.Combine(Radice(), "App.axaml"));
        var token = Regex.Match(app, "<SolidColorBrush x:Key=\"WindowBackgroundBrush\">#([0-9A-Fa-f]{6}|FF[0-9A-Fa-f]{6})</SolidColorBrush>");
        Assert.True(token.Success, "WindowBackgroundBrush deve essere un SolidColorBrush esadecimale opaco (#RRGGBB o #FFRRGGBB).");
    }

    [Fact]
    public void NessunaFinestra_HaSfondoTrasparenteNeTrasparenzaDellaFinestra()
    {
        var violazioni = new List<string>();
        foreach (var f in FinestreXaml())
        {
            var nome = Path.GetFileNameWithoutExtension(f);
            var apertura = Regex.Match(File.ReadAllText(f), @"<Window\b[^>]*>", RegexOptions.Singleline).Value;
            if (Regex.IsMatch(apertura, "Background=\"(Transparent|\\{x:Null\\}|#00[0-9A-Fa-f]{6})\""))
                violazioni.Add($"{nome}: Background trasparente sulla Window (lascia vedere la finestra sotto)");
            var hint = Regex.Match(apertura, "TransparencyLevelHint=\"([^\"]*)\"");
            if (hint.Success && hint.Groups[1].Value != "None")
                violazioni.Add($"{nome}: TransparencyLevelHint=\"{hint.Groups[1].Value}\" (la finestra non deve essere trasparente)");
        }
        Assert.True(violazioni.Count == 0, string.Join("\n", violazioni));
    }
}
