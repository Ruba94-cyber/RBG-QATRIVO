using System.Runtime.CompilerServices;

namespace TestLoom.App.Tests;

/// <summary>Garanzia architetturale: il progetto TestLoom.App.Tests non deve ospitare test che creano una piattaforma
/// Avalonia headless. Quella piattaforma crea un'Application che resta impostata per tutto il processo di test e cambia
/// il comportamento dei test eseguiti dopo (MainViewModel e NonConformitaViewModel passano in modalita' "app reale").
/// I test headless vivono nel progetto TestLoom.App.Tests.Headless, in un processo proprio.</summary>
public class ProgettoTestPrincipaleSenzaHeadlessTests
{
    // Token che indicano l'uso della piattaforma headless o della sua Application globale in codice (le righe di
    // commento non contano).
    private static readonly string[] TokenVietati =
    {
        "Avalonia.Headless",
        "LayoutHeadlessSessione",
        "AppBuilder",
        "UseHeadless(",
        "SetupWithoutStarting(",
        "Application.Current",
    };

    [Fact]
    public void Assembly_NonReferenziaAvaloniaHeadless()
    {
        var riferimenti = typeof(ProgettoTestPrincipaleSenzaHeadlessTests).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(riferimenti, a => a.Name != null && a.Name.StartsWith("Avalonia.Headless", StringComparison.Ordinal));
    }

    [Fact]
    public void Sorgenti_NonUsanoLaPiattaformaHeadless()
    {
        var cartella = CartellaSorgentiTest();
        var questoFile = Path.GetFileName(PercorsoQuestoFile());
        var violazioni = new List<string>();

        foreach (var file in Directory.EnumerateFiles(cartella, "*.cs", SearchOption.AllDirectories))
        {
            var relativo = Path.GetRelativePath(cartella, file);
            if (relativo.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relativo.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(file) == questoFile)
                continue;

            var righe = File.ReadAllLines(file);
            for (var i = 0; i < righe.Length; i++)
            {
                var riga = righe[i].TrimStart();
                if (riga.StartsWith("//", StringComparison.Ordinal) || riga.StartsWith("/*", StringComparison.Ordinal)
                    || riga.StartsWith("*", StringComparison.Ordinal))
                    continue;

                foreach (var token in TokenVietati)
                {
                    if (riga.Contains(token, StringComparison.Ordinal))
                        violazioni.Add($"{relativo}:{i + 1} contiene '{token}'");
                }
            }
        }

        Assert.True(violazioni.Count == 0,
            "Questi test usano la piattaforma headless (spostarli in TestLoom.App.Tests.Headless):\n" + string.Join("\n", violazioni));
    }

    /// <summary>Cartella dei sorgenti del progetto di test, ricavata dal percorso di questo file compilato.</summary>
    private static string CartellaSorgentiTest() => Path.GetDirectoryName(PercorsoQuestoFile())!;

    private static string PercorsoQuestoFile([CallerFilePath] string percorso = "") => percorso;
}
