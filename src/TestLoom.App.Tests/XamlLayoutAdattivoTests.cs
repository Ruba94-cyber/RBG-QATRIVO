using System.Globalization;
using System.Text.RegularExpressions;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.6.1: il layout adattivo nei file XAML. I token di App.axaml coincidono con i valori di Standard, le pagine
/// elencate non hanno MaxWidth o padding fissi, i KPI di Home e Audit usano UniformGrid con il convertitore di colonne,
/// gli overlay della shell sono legati all'altezza della finestra e il "Indietro" della sidebar non ha colore inline.</summary>
public class XamlLayoutAdattivoTests
{
    private static string Radice()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "TestLoom.App", "Views"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, "src", "TestLoom.App");
    }

    private static string Vista(string nome) => Path.Combine(Radice(), "Views", nome);

    [Fact]
    public void TokenInAppAxaml_CoincidonoConIValoriDiStandard()
    {
        var app = File.ReadAllText(Path.Combine(Radice(), "App.axaml"));
        var standard = LayoutAdattivo.Token(ModoLayout.Standard);
        var attesi = new Dictionary<string, double>
        {
            ["PagePadding"] = standard.PagePadding,
            ["CardPadding"] = standard.CardPadding,
            ["SpaziaturaSezioni"] = standard.SpaziaturaSezioni,
            ["TitoloPagina"] = standard.TitoloPagina,
            ["KpiNumero"] = standard.KpiNumero,
            ["KpiNumeroPiccolo"] = standard.KpiNumeroPiccolo,
            ["LarghezzaSidebar"] = standard.LarghezzaSidebar,
            ["MaxLarghezzaPagina"] = standard.MaxLarghezzaPagina,
            ["AltezzaMinimaTabella"] = standard.AltezzaMinimaTabella,
        };
        foreach (var (chiave, valore) in attesi)
        {
            var m = Regex.Match(app, "<(?:Thickness|x:Double) x:Key=\"" + chiave + "\">([\\d.]+)<");
            Assert.True(m.Success, $"Token {chiave} non definito in App.axaml");
            Assert.Equal(valore, double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 6);
        }
    }

    [Theory]
    [InlineData("HomeView.axaml")]
    [InlineData("AuditView.axaml")]
    [InlineData("RecoveryBinView.axaml")]
    [InlineData("ImpostazioniView.axaml")]
    [InlineData("ArchivioView.axaml")]
    [InlineData("CampoSistemaDefinizioneView.axaml")]
    [InlineData("EcuView.axaml")]
    [InlineData("FunzionalitaView.axaml")]
    [InlineData("GammaView.axaml")]
    [InlineData("OperatoriView.axaml")]
    [InlineData("RequisitiView.axaml")]
    [InlineData("StrumentiView.axaml")]
    [InlineData("TestTemplateView.axaml")]
    [InlineData("VeicoliView.axaml")]
    public void VisteDelLayoutAdattivo_UsanoIlPaddingDiPaginaDinamico(string file) =>
        Assert.DoesNotContain("StaticResource PagePadding", File.ReadAllText(Vista(file)));

    [Theory]
    [InlineData("HomeView.axaml")]
    [InlineData("AuditView.axaml")]
    [InlineData("RecoveryBinView.axaml")]
    [InlineData("ImpostazioniView.axaml")]
    public void PagineConPaddingDiPagina_NonHannoIl28Fisso(string file) =>
        Assert.DoesNotMatch("(Padding|Margin)=\"28\"", File.ReadAllText(Vista(file)));

    [Theory]
    [InlineData("HomeView.axaml")]
    [InlineData("AuditView.axaml")]
    [InlineData("RecoveryBinView.axaml")]
    [InlineData("ChangeRequestEditorView.axaml")]
    [InlineData("SegnalazioneReteEditorView.axaml")]
    public void PagineConLarghezzaMassima_UsanoIlToken(string file)
    {
        var testo = File.ReadAllText(Vista(file));
        Assert.DoesNotMatch("MaxWidth=\"(1150|1180|1000|900)\"", testo);
        Assert.Contains("MaxWidth=\"{DynamicResource MaxLarghezzaPagina}\"", testo);
    }

    [Theory]
    [InlineData("HomeView.axaml", "SezioneKpi", "150|6")]
    [InlineData("AuditView.axaml", "GrigliaKpi", "120|7")]   // 6.6.1 matrice: sette KPI su una riga a 1024 DIP (in compatto)
    public void KpiDiHomeEAudit_UsanoUniformGridConConvertitoreDiColonne(string file, string nome, string parametro)
    {
        var testo = File.ReadAllText(Vista(file));
        Assert.Contains("<UniformGrid x:Name=\"" + nome + "\" Columns=", testo);
        Assert.Contains("Converter={x:Static local:ColonneDaLarghezzaConverter.Instance}, ConverterParameter=" + parametro, testo);
    }

    [Fact]
    public void NumeriDeiKpi_UsanoIToken()
    {
        var home = File.ReadAllText(Vista("HomeView.axaml"));
        var audit = File.ReadAllText(Vista("AuditView.axaml"));
        Assert.DoesNotContain("FontSize=\"26\"", home);
        Assert.DoesNotContain("FontSize=\"22\" FontWeight=\"Bold\"", audit);
        Assert.Contains("FontSize=\"{DynamicResource KpiNumeroPiccolo}\" FontWeight=\"Bold\"", audit);
    }

    [Fact]
    public void OverlayDellaShell_SonoLegatiAllAltezzaDellaFinestra()
    {
        var testo = File.ReadAllText(Vista("MainWindow.axaml"));
        // Ricerca globale, aiuto, notifiche, tour guidato.
        Assert.Equal(4, Regex.Matches(testo, "MaxHeight=\"\\{Binding #MainRoot\\.Bounds\\.Height, Converter=\\{x:Static local:AltezzaResiduaConverter\\.Instance\\}").Count);
        Assert.DoesNotMatch("MaxHeight=\"(680|700|720)\"", testo);
    }

    [Fact]
    public void PulsanteIndietroDellaSidebar_NonHaColoreInlineESiAppoggiaAllaClasse()
    {
        var testo = File.ReadAllText(Vista("MainWindow.axaml"));
        var bottone = Regex.Match(testo, "<Button[^>]*SidebarIndietro[^>]*>").Value;
        Assert.Contains("SidebarLink", bottone);
        Assert.DoesNotContain("Foreground=", bottone);
        Assert.Contains("Width=\"{DynamicResource LarghezzaSidebar}\"", testo);
    }

    [Fact]
    public void StiliCompattiETMinimo_PulsantiEControlliRestanoAlti32()
    {
        var stili = File.ReadAllText(Path.Combine(Radice(), "Styles.axaml"));
        var regole = Regex.Matches(stili, "<Style Selector=\"Window\\.compatto (Button|TextBox|ComboBox)\\.\\w+\">(.*?)</Style>", RegexOptions.Singleline);
        Assert.True(regole.Count >= 6, "regole compatte attese: " + regole.Count);
        foreach (Match regola in regole)
            Assert.Contains("MinHeight\" Value=\"32\"", regola.Groups[2].Value);
    }
}
