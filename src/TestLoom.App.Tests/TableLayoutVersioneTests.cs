using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.4.6: le larghezze di colonna salvate fino alla 6.4.5 (versione 0) potevano essere quelle schiacciate
/// da una finestra piccola e catturate a un semplice click su una riga: non vanno piu' riapplicate.</summary>
public class TableLayoutVersioneTests
{
    [Fact]
    public void LayoutSenzaVersione_NonRiapplicaLeLarghezze()
    {
        var vecchio = new TableLayoutPreference { Colonne = [new() { Indice = 4, Ordine = 4, Larghezza = 40, Visibile = true }] };
        Assert.False(TableLayoutPreferences.LarghezzeApplicabili(vecchio));
    }

    [Fact]
    public void LayoutVersioneCorrente_RiapplicaLeLarghezze()
    {
        var nuovo = new TableLayoutPreference { Versione = TableLayoutPreferences.VersioneLayoutCorrente };
        Assert.True(TableLayoutPreferences.LarghezzeApplicabili(nuovo));
    }

    [Fact]
    public void AppConfig_PersisteLaVersioneDelLayout_EIlVecchioJsonLeggeZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_TableLayoutVersione", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new AppConfigService(Path.Combine(dir, "config.json"));
            var config = new AppConfig();
            config.LayoutTabelle["Mario|Master|functions"] = new TableLayoutPreference
            {
                Versione = TableLayoutPreferences.VersioneLayoutCorrente,
                Colonne = [new() { Indice = 0, Ordine = 0, Larghezza = 220, Visibile = true }]
            };
            config.LayoutTabelle["Mario|Master|componente"] = new TableLayoutPreference
            {
                Colonne = [new() { Indice = 0, Ordine = 0, Larghezza = 40, Visibile = true }]
            };
            service.Save(config);

            var letto = service.Load();
            Assert.Equal(TableLayoutPreferences.VersioneLayoutCorrente, letto.LayoutTabelle["Mario|Master|functions"].Versione);
            Assert.Equal(0, letto.LayoutTabelle["Mario|Master|componente"].Versione);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
