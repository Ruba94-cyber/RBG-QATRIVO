using System.Text.RegularExpressions;
using TestLoom.Core;

namespace TestLoom.Core.Tests;

/// <summary>
/// Guards the Italian UI: every user-visible English literal in the desktop sources must have a translation,
/// so a new button or message cannot silently ship untranslated.
/// </summary>
public sealed class LocalizationCoverageTests
{
    private static readonly string[] Ignored =
    {
        "TestLoom", "Tester", "CSV file", "HTML report", "ZIP backup", "Unsupported workspace schema version",
        "workspace.json", "testloom-report.html",
    };

    private static string? DesktopDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "TestLoom.Desktop");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void EveryVisibleDesktopStringHasAnItalianTranslation()
    {
        var directory = DesktopDirectory();
        if (directory is null) return; // sources not shipped next to the binaries

        var q = '"'.ToString();
        var patterns = new[]
        {
            new Regex(@"\bL\(" + q + "(?<s>[^" + q + "]+)" + q + @"\)"),
            new Regex("Content = " + q + "(?<s>[^" + q + "]+)" + q),
            new Regex(@"\bText = " + q + "(?<s>[^" + q + "]+)" + q),
            new Regex("Watermark = " + q + "(?<s>[^" + q + "]+)" + q),
            new Regex(@"SectionHeading\(" + q + "(?<s>[^" + q + "]+)" + q + @"\)"),
            new Regex(@"AddTab\(tabs, " + q + "(?<s>[^" + q + "]+)" + q),
            new Regex(@"SetStatus\(" + q + "(?<s>[^" + q + "]+)" + q),
        };
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(directory, "*.cs"))
        {
            if (file.EndsWith("AboutWindow.cs", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            foreach (var pattern in patterns)
                foreach (Match match in pattern.Matches(text))
                {
                    var literal = match.Groups["s"].Value;
                    if (Ignored.Contains(literal) || !literal.Any(char.IsLetter)) continue;
                    if (WorkspaceLocalization.TMessage(literal, "it") == literal) missing.Add(literal);
                }
        }
        Assert.True(missing.Count == 0, "Untranslated UI text:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EnumNamesShownInTheUiAreTranslated()
    {
        foreach (var name in Enum.GetNames<WorkspacePriority>()
                     .Concat(Enum.GetNames<WorkspaceDefectSeverity>())
                     .Concat(Enum.GetNames<WorkspaceDefectStatus>())
                     .Concat(Enum.GetNames<ExecutionResult>()))
            Assert.NotEqual(name, WorkspaceLocalization.T(name, "it"));
    }
}
