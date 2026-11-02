using System.Text.RegularExpressions;

namespace TestLoom.App.Tests;

/// <summary>Regressione 6.4.6: una DataGridTextColumn con StringFormat legata TwoWay (default) puo' riscrivere nel
/// record il testo formattato riletto come valore. Con "dd/MM/yy HH:mm" su UltimaModifica il timestamp veniva
/// alterato in memoria e il Salva successivo falliva con un falso conflitto "modificato da un'altra postazione".
/// Ogni colonna formattata deve quindi essere esplicitamente Mode=OneWay.</summary>
public class XamlColonneFormattateTests
{
    private static string DirViste()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "TestLoom.App", "Views"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, "src", "TestLoom.App", "Views");
    }

    [Fact]
    public void ColonneDataGridConStringFormat_SonoSempreOneWay()
    {
        var violazioni = new List<string>();
        foreach (var f in Directory.EnumerateFiles(DirViste(), "*.axaml"))
        {
            var testo = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(testo, @"<DataGridTextColumn\b.*?/>", RegexOptions.Singleline))
            {
                var binding = Regex.Match(m.Value, @"\sBinding=""\{Binding [^""]*""");
                if (binding.Success && binding.Value.Contains("StringFormat") && !binding.Value.Contains("Mode=OneWay"))
                    violazioni.Add($"{Path.GetFileName(f)}: {binding.Value.Trim()}");
            }
        }
        Assert.True(violazioni.Count == 0, "Colonne formattate senza Mode=OneWay:\n" + string.Join("\n", violazioni));
    }
}
