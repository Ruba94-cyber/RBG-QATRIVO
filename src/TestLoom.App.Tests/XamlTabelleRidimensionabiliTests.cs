using System.Text.RegularExpressions;

namespace TestLoom.App.Tests;

/// <summary>Ogni tabella (DataGrid) deve avere colonne ridimensionabili e un TableId stabile e univoco (layout,
/// larghezze incluse, salvati fra le sessioni); ogni colonna Star deve avere un MinWidth, cosi' sotto una certa
/// larghezza la griglia scorre in orizzontale invece di schiacciare le colonne.</summary>
public class XamlTabelleRidimensionabiliTests
{
    /// <summary>Eccezioni motivate (nome file, DataGrid senza TableId/ridimensionamento). Vuota: tutte le tabelle
    /// dell'app sono a colonne ridimensionabili e persistenti.</summary>
    private static readonly HashSet<string> EccezioniAmmesse = new(StringComparer.OrdinalIgnoreCase);

    private static string DirViste()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "TestLoom.App", "Views"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, "src", "TestLoom.App", "Views");
    }

    private static IEnumerable<(string File, string Tag, string Corpo)> Griglie()
    {
        foreach (var f in Directory.EnumerateFiles(DirViste(), "*.axaml"))
        {
            var testo = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(testo, @"<DataGrid(?=[\s>])[^>]*>", RegexOptions.Singleline))
            {
                var fine = testo.IndexOf("</DataGrid>", m.Index, StringComparison.Ordinal);
                yield return (Path.GetFileName(f), m.Value, fine < 0 ? "" : testo[m.Index..fine]);
            }
        }
    }

    [Fact]
    public void OgniDataGrid_HaColonneRidimensionabiliETableId()
    {
        var griglie = Griglie().ToList();
        Assert.True(griglie.Count >= 14, $"attese almeno 14 DataGrid, trovate {griglie.Count}");
        var violazioni = new List<string>();
        foreach (var (file, tag, _) in griglie)
        {
            if (EccezioniAmmesse.Contains(file)) continue;
            if (!Regex.IsMatch(tag, @"\bCanUserResizeColumns=""True""")) violazioni.Add($"{file}: manca CanUserResizeColumns=\"True\"");
            if (!Regex.IsMatch(tag, @"TableLayoutPreferences\.TableId=""[^""]+""")) violazioni.Add($"{file}: manca TableLayoutPreferences.TableId");
        }
        Assert.True(violazioni.Count == 0, string.Join("\n", violazioni));
    }

    [Fact]
    public void TableId_SonoUnivoci()
    {
        var ids = Griglie().Select(g => Regex.Match(g.Tag, @"TableLayoutPreferences\.TableId=""([^""]+)""")).Where(m => m.Success)
            .Select(m => m.Groups[1].Value).ToList();
        var duplicati = ids.GroupBy(i => i, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicati.Count == 0, "TableId duplicati: " + string.Join(", ", duplicati));
    }

    [Fact]
    public void OgniDataGrid_ConsenteIlRiordinoE_NcECrUsanoLayoutPersistente()
    {
        var cartellaViste = DirViste();
        var stili = File.ReadAllText(Path.Combine(Directory.GetParent(cartellaViste)!.FullName, "Styles.axaml"));
        Assert.Matches(@"<Setter\s+Property=""CanUserReorderColumns""\s+Value=""True""\s*/>", stili);

        var testoViste = string.Join("\n", Directory.EnumerateFiles(cartellaViste, "*.axaml").Select(File.ReadAllText));
        Assert.DoesNotContain("CanUserReorderColumns=\"False\"", testoViste, StringComparison.Ordinal);
        Assert.DoesNotContain("CanUserReorder=\"False\"", testoViste, StringComparison.Ordinal);

        var ncCr = File.ReadAllText(Path.Combine(cartellaViste, "NonConformitaView.axaml"));
        Assert.Contains("TableLayoutPreferences.TableId=\"anomalie\"", ncCr, StringComparison.Ordinal);
        Assert.Contains("TableLayoutPreferences.TableId=\"change-requests\"", ncCr, StringComparison.Ordinal);
        Assert.Contains("CanUserReorderColumns=\"True\"", ncCr, StringComparison.Ordinal);
        Assert.DoesNotContain("FrozenColumnCount=", ncCr, StringComparison.Ordinal);
    }

    [Fact]
    public void OgniFinestraXaml_ConsenteIlRidimensionamento()
    {
        var fileViste = Directory.EnumerateFiles(DirViste(), "*.axaml").ToList();
        var finestre = fileViste.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"<Window(?=[\s>])[^>]*>", RegexOptions.Singleline)
                .Select(m => (File: Path.GetFileName(f), Tag: m.Value)))
            .ToList();
        var stili = File.ReadAllText(Path.Combine(Directory.GetParent(DirViste())!.FullName, "Styles.axaml"));

        Assert.NotEmpty(finestre);
        Assert.Matches(@"<Setter\s+Property=""CanResize""\s+Value=""True""\s*/>", stili);
        var nonRidimensionabili = finestre.Where(f => Regex.IsMatch(f.Tag, @"\bCanResize=""False""", RegexOptions.IgnoreCase))
            .Select(f => f.File + ": " + f.Tag).ToList();
        Assert.True(nonRidimensionabili.Count == 0, string.Join("\n", nonRidimensionabili));
    }

    [Fact]
    public void ColonneStarEColonneConMaxWidth_HannoMinWidthOSonoRidimensionabili()
    {
        var violazioni = new List<string>();
        foreach (var (file, _, corpo) in Griglie())
            foreach (Match m in Regex.Matches(corpo, @"<DataGrid(?:Text|Template|CheckBox)Column\b[^>]*?(?<!-)>", RegexOptions.Singleline))
            {
                var c = m.Value;
                if (Regex.IsMatch(c, @"\sWidth=""[0-9.]*\*""") && !Regex.IsMatch(c, @"\sMinWidth=""[1-9]"))
                    violazioni.Add($"{file}: colonna Star senza MinWidth: {c[..Math.Min(c.Length, 90)]}");
                if (Regex.IsMatch(c, @"\sMaxWidth=""")) violazioni.Add($"{file}: colonna con MaxWidth (impedisce di allargarla): {c[..Math.Min(c.Length, 90)]}");
                if (Regex.IsMatch(c, @"\sCanUserResize=""False""")) violazioni.Add($"{file}: colonna non ridimensionabile: {c[..Math.Min(c.Length, 90)]}");
            }
        Assert.True(violazioni.Count == 0, string.Join("\n", violazioni));
    }
}
