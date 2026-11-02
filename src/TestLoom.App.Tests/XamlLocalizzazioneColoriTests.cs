using System.Reflection;
using System.Text.RegularExpressions;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>UX2 (2026-10): ogni chiave di localizzazione usata dai binding XAML esiste in IT ed EN, e la legenda
/// della matrice Audit (risorse in App.axaml) usa gli stessi colori delle celle (CellaMatriceAuditVisual.Colore).</summary>
public sealed class XamlLocalizzazioneColoriTests
{
    private static string DirSorgenti()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "TestLoom.App", "Views"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, "src", "TestLoom.App");
    }

    [Fact]
    public void ChiaviLocalizzazioneUsateInXaml_EsistonoInItEEn()
    {
        var campo = typeof(LocalizationService).GetField("Dizionario", BindingFlags.NonPublic | BindingFlags.Static)!;
        var diz = (System.Collections.IDictionary)campo.GetValue(null)!;
        var re = new Regex(@"TraduciConverter\.Instance\}, ConverterParameter=(\w+)\}");
        var mancanti = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(DirSorgenti(), "Views"), "*.axaml"))
        foreach (Match m in re.Matches(File.ReadAllText(f)))
        {
            var k = m.Groups[1].Value;
            if (!diz.Contains(k)) { mancanti.Add($"{Path.GetFileName(f)}:{k}"); continue; }
            var (it, en) = ((string, string))diz[k]!;
            if (string.IsNullOrEmpty(it) || string.IsNullOrEmpty(en)) mancanti.Add($"{Path.GetFileName(f)}:{k} (vuota)");
        }
        Assert.True(mancanti.Count == 0, "Chiavi mancanti: " + string.Join(", ", mancanti.Distinct()));
    }

    [Fact]
    public void LegendaAudit_UsaGliStessiColoriDelleCelle()
    {
        var app = File.ReadAllText(Path.Combine(DirSorgenti(), "App.axaml"));
        string Risorsa(string chiave) =>
            Regex.Match(app, $@"x:Key=""{chiave}"">(#[0-9A-Fa-f]{{6}})<").Groups[1].Value.ToUpperInvariant();

        CellaMatriceAuditVisual Cella(StatoCellaAudit s, PunteggioConformita? p = null) => new() { Stato = s, Punteggio = p };
        var attesi = new Dictionary<string, string>
        {
            ["AuditCellaDaTestareBrush"] = Cella(StatoCellaAudit.DaTestare).Colore,
            ["AuditCellaTestCreatoBrush"] = Cella(StatoCellaAudit.TestCreatoNonEseguito).Colore,
            ["AuditCellaConflittoBrush"] = Cella(StatoCellaAudit.Conflitto).Colore,
            ["AuditCellaPropagabileBrush"] = Cella(StatoCellaAudit.PropagabileNonConfermato).Colore,
            ["AuditCellaBassoBrush"] = Cella(StatoCellaAudit.Valutato, PunteggioConformita.NonConforme).Colore,
            ["AuditCellaMedioBrush"] = Cella(StatoCellaAudit.Valutato, PunteggioConformita.ParzialmenteConforme).Colore,
            ["AuditCellaOttimoBrush"] = Cella(StatoCellaAudit.Valutato, PunteggioConformita.Conforme).Colore,
            ["AuditCellaNaBrush"] = Cella(StatoCellaAudit.Valutato, PunteggioConformita.NonApplicabile).Colore,
        };
        foreach (var (chiave, hex) in attesi)
            Assert.Equal(hex.ToUpperInvariant(), Risorsa(chiave));
    }
}
