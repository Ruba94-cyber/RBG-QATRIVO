namespace TestLoom.App.Tests;

/// <summary>Guards only visible product labels: historical .NET assembly, data path and keys remain TestLoom.</summary>
public sealed class RbgDitmBrandingTextTests
{
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "TestLoom.App", "TestLoom.App.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "TestLoom.App");
    }

    [Fact]
    public void NomeProdottoTitoloDiFinestraEAssemblyCompatibile()
    {
        var root = SourceRoot();
        var project = File.ReadAllText(Path.Combine(root, "TestLoom.App.csproj"));
        var window = File.ReadAllText(Path.Combine(root, "Views", "MainWindow.axaml"));
        var app = File.ReadAllText(Path.Combine(root, "App.axaml.cs"));
        Assert.Contains("<Product>RBG - QATRIVO</Product>", project);
        Assert.Contains("<AssemblyName>TestLoom</AssemblyName>", project);
        Assert.Contains("Title=\"RBG - QATRIVO\"", window);
        Assert.Contains("new NativeMenuItem(\"Apri RBG - QATRIVO\")", app);
    }

    [Fact]
    public void EtichettePrincipali_LocalizzateIT_EN_FR()
    {
        var root = SourceRoot();
        var primary = File.ReadAllText(Path.Combine(root, "Services", "LocalizationService.cs"));
        var french = File.ReadAllText(Path.Combine(root, "Services", "LocalizationService.Fr.cs"));
        foreach (var key in new[] { "OnboardingFine", "BenvenutoTitolo", "AccediTitolo", "TestLoomVersionePrefix", "AvvioTitolo" })
        {
            var label = "[\"" + key + "\"]";
            Assert.Contains(label, primary);
            Assert.Contains(label, french);
            var pLine = primary.Split('\n').First(x => x.Contains(label, StringComparison.Ordinal));
            var frLine = french.Split('\n').First(x => x.Contains(label, StringComparison.Ordinal));
            Assert.Contains("RBG - QATRIVO", pLine);
            Assert.Contains("RBG - QATRIVO", frLine);
        }
    }
}
