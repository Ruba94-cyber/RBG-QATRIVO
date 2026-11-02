using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

public class IconeSezioniServiceTests
{
    private static NavItem Voce(string id, string icona) => new() { Id = id, Titolo = id, Icona = icona, Crea = () => null! };

    private static List<NavItem> Applicate(AppConfig config, string ambito = IconeSezioniService.AmbitoPrincipale)
    {
        var voci = new List<NavItem> { Voce("home", "🏠"), Voce("test", "🧪") };
        IconeSezioniService.Applica(voci, ambito, config);
        return voci;
    }

    [Fact]
    public void Default_SenzaOverride_UsaIconaPredefinita()
    {
        var voci = Applicate(new AppConfig());

        Assert.Equal("🏠", voci[0].IconaEffettiva);
        Assert.False(voci[0].HaIconaPersonalizzata);
        Assert.Equal("main:home", voci[0].ChiaveIcona);
    }

    [Fact]
    public void Override_DaConfigApplicatoSoloAllaVoceGiusta()
    {
        var config = new AppConfig { IconeSezioni = new() { ["main:home"] = "🚗" } };

        var voci = Applicate(config);

        Assert.Equal("🚗", voci[0].IconaEffettiva);
        Assert.True(voci[0].HaIconaPersonalizzata);
        Assert.Equal("🧪", voci[1].IconaEffettiva);
        // Stesso id in un altro ambito non e' toccato.
        Assert.Equal("🏠", Applicate(config, IconeSezioniService.AmbitoImpostazioni)[0].IconaEffettiva);
    }

    [Fact]
    public void Imposta_SalvaInConfigEAggiornaLaVoce()
    {
        var config = new AppConfig();
        var voci = Applicate(config);

        Assert.True(IconeSezioniService.Imposta(config, voci[1], "⭐"));

        Assert.Equal("⭐", voci[1].IconaEffettiva);
        Assert.Equal("⭐", config.IconeSezioni!["main:test"]);
    }

    [Fact]
    public void Imposta_IconaPredefinita_EquivaleARipristino()
    {
        var config = new AppConfig();
        var voci = Applicate(config);
        IconeSezioniService.Imposta(config, voci[0], "⭐");

        Assert.True(IconeSezioniService.Imposta(config, voci[0], "🏠"));

        Assert.Null(config.IconeSezioni);
        Assert.False(voci[0].HaIconaPersonalizzata);
    }

    [Fact]
    public void Ripristina_RimuoveSoloLaVoceEAzzeraIlDizionarioQuandoVuoto()
    {
        var config = new AppConfig();
        var voci = Applicate(config);
        IconeSezioniService.Imposta(config, voci[0], "⭐");
        IconeSezioniService.Imposta(config, voci[1], "🔔");

        IconeSezioniService.Ripristina(config, voci[0]);
        Assert.Equal("🏠", voci[0].IconaEffettiva);
        Assert.Single(config.IconeSezioni!);

        IconeSezioniService.Ripristina(config, voci[1]);
        Assert.Null(config.IconeSezioni);
    }

    [Fact]
    public void RipristinaTutte_AzzeraOverrideDiTutteLeVoci()
    {
        var config = new AppConfig();
        var voci = Applicate(config);
        IconeSezioniService.Imposta(config, voci[0], "⭐");
        IconeSezioniService.Imposta(config, voci[1], "🔔");

        IconeSezioniService.RipristinaTutte(config, voci);

        Assert.Null(config.IconeSezioni);
        Assert.All(voci, v => Assert.False(v.HaIconaPersonalizzata));
        Assert.Equal("🧪", voci[1].IconaEffettiva);
    }

    [Fact]
    public void ConfigLegacySenzaCampo_SiCaricaConIconePredefinite()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{\"Lingua\":\"it\",\"OperatoreAttivo\":\"x\"}", JsonIo.Options)!;

        Assert.Null(config.IconeSezioni);
        Assert.Equal("🏠", Applicate(config)[0].IconaEffettiva);
    }

    [Fact]
    public void CampoNullo_NonVieneScrittoInConfigJson()
    {
        var json = JsonSerializer.Serialize(new AppConfig(), JsonIo.Options);
        Assert.DoesNotContain("IconeSezioni", json);

        var conOverride = JsonSerializer.Serialize(new AppConfig { IconeSezioni = new() { ["main:home"] = "⭐" } }, JsonIo.Options);
        var riletto = JsonSerializer.Deserialize<AppConfig>(conOverride, JsonIo.Options)!;
        Assert.Equal("⭐", riletto.IconeSezioni!["main:home"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    [InlineData("a b")]
    [InlineData("\n")]
    [InlineData("🏠🧪")]
    [InlineData("12345678901234567")]
    public void ValoreNonValido_InConfigVieneIgnorato(string valore)
    {
        var config = new AppConfig { IconeSezioni = new() { ["main:home"] = valore } };

        var voci = Applicate(config);

        Assert.Equal("🏠", voci[0].IconaEffettiva);
        Assert.False(IconeSezioniService.Valida(valore));
    }

    [Fact]
    public void Imposta_ValoreNonValido_NonToccaNulla()
    {
        var config = new AppConfig();
        var voci = Applicate(config);

        Assert.False(IconeSezioniService.Imposta(config, voci[0], "xx"));
        Assert.False(IconeSezioniService.Imposta(config, voci[0], null));

        Assert.Null(config.IconeSezioni);
        Assert.Equal("🏠", voci[0].IconaEffettiva);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("★")]
    [InlineData("⚠️")]
    [InlineData("👨‍👩‍👧")]
    public void Valida_AccettaUnSoloGrafema(string valore) => Assert.True(IconeSezioniService.Valida(valore));

    [Fact]
    public void Galleria_ContieneSoloIconeValideSenzaDuplicati()
    {
        Assert.All(IconeSezioniService.Galleria, g => Assert.True(IconeSezioniService.Valida(g), g));
        Assert.Equal(IconeSezioniService.Galleria.Count, IconeSezioniService.Galleria.Distinct().Count());
    }
}
