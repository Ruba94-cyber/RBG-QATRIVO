using System.Text.RegularExpressions;

namespace TestLoom.App.Tests;

/// <summary>6.4.7 e 6.6.1: su Panasonic G2 (1280x800 logici, Windows in modalita' tablet) le finestre ridimensionabili si
/// aprono a tutto schermo e i pulsanti in basso finiscono sotto la barra delle applicazioni. Dal 6.6.1 non ci sono
/// esenzioni dalla registrazione: ogni finestra secondaria passa da AdattamentoDialogo (tetto = area di lavoro - 48 DIP,
/// classi del modo di layout). Tutte le finestre restano ridimensionabili dopo l'apertura.
/// I pulsanti di conferma (stile Primary) non stanno dentro uno ScrollViewer: vanno in un footer fuori dallo scroll.</summary>
public class XamlDialoghiSchermoPiccoloTests
{
    /// <summary>La finestra principale non e' un dialogo: il layout adattivo la gestisce in MainWindow.</summary>
    private const string FinestraPrincipale = "MainWindow";

    private static string DirViste()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "TestLoom.App", "Views"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, "src", "TestLoom.App", "Views");
    }

    private static IEnumerable<string> FinestreXaml() =>
        Directory.EnumerateFiles(DirViste(), "*.axaml")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"<Window\b"));

    [Fact]
    public void OgniFinestraSecondariaERegistrataInAdattamentoDialogo()
    {
        var violazioni = new List<string>();
        foreach (var f in FinestreXaml())
        {
            var nome = Path.GetFileNameWithoutExtension(f);
            if (nome == FinestraPrincipale) continue;
            var codice = f + ".cs";
            var testoCodice = File.Exists(codice) ? File.ReadAllText(codice) : "";
            if (!testoCodice.Contains("AdattamentoDialogo.Registra(this"))
                violazioni.Add($"{nome}: manca AdattamentoDialogo.Registra(this) nel code-behind");
        }
        Assert.True(violazioni.Count == 0, string.Join("\n", violazioni));
    }

    [Fact]
    public void OgniFinestraSecondaria_ConsenteIlRidimensionamento()
    {
        var violazioni = new List<string>();
        foreach (var f in FinestreXaml())
        {
            var nome = Path.GetFileNameWithoutExtension(f);
            if (nome == FinestraPrincipale) continue;
            var apertura = Regex.Match(File.ReadAllText(f), @"<Window\b[^>]*>", RegexOptions.Singleline).Value;
            if (Regex.IsMatch(apertura, @"\bCanResize=""False""", RegexOptions.IgnoreCase))
                violazioni.Add($"{nome}: finestra con CanResize=\"False\"");
        }
        Assert.True(violazioni.Count == 0, string.Join("\n", violazioni));
    }

    [Fact]
    public void PulsantiDiConfermaNonStannoDentroUnoScrollViewer()
    {
        var violazioni = new List<string>();
        foreach (var f in FinestreXaml())
        {
            var nome = Path.GetFileNameWithoutExtension(f);
            if (PrimarioDentroUnoScrollViewer(File.ReadAllText(f))) violazioni.Add($"{nome}: pulsante Primary dentro uno ScrollViewer (spostarlo nel footer)");
        }
        Assert.True(violazioni.Count == 0, string.Join("\n", violazioni));
    }

    /// <summary>Vero se un pulsante con Classes="Primary" compare tra un tag ScrollViewer aperto e il suo tag di chiusura.</summary>
    internal static bool PrimarioDentroUnoScrollViewer(string xaml)
    {
        var tag = new Regex(@"<(/?)ScrollViewer\b[^>]*?(/?)>");
        var profondita = 0;
        var posizione = 0;
        foreach (Match m in tag.Matches(xaml))
        {
            if (profondita > 0 && xaml.Substring(posizione, m.Index - posizione).Contains("Classes=\"Primary\"")) return true;
            posizione = m.Index + m.Length;
            if (m.Groups[1].Value == "/") profondita--;
            else if (m.Groups[2].Value != "/") profondita++;
        }
        return false;
    }
}
