using System.Globalization;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Xunit;

namespace TestLoom.App.Tests;

/// <summary>Il menu del ruolo in Operatori mostra l'etichetta localizzata (non il nome grezzo dell'enum),
/// ma il valore salvato resta <see cref="RuoloOperatore"/>: il round-trip etichetta -> ruolo deve restituire
/// lo stesso enum selezionato.</summary>
public class OperatoriRuoloEtichetteTests
{
    private static string? Etichetta(object? ruolo) =>
        (string?)RuoloOperatoreLabelConverter.Instance.Convert(ruolo, typeof(string), null, CultureInfo.InvariantCulture);

    private static RuoloOperatore? RuoloDaEtichetta(string etichetta, IEnumerable<RuoloOperatore> ruoli) =>
        ruoli.Cast<RuoloOperatore?>().FirstOrDefault(r => Etichetta(r) == etichetta);

    [Theory]
    [InlineData("it")]
    [InlineData("en")]
    [InlineData("fr")]
    public void Ogni_ruolo_ha_etichetta_localizzata_non_vuota_e_diversa_dal_nome_enum(string lingua)
    {
        var loc = LocalizationService.Instance;
        var originale = loc.Lingua;
        try
        {
            loc.Lingua = lingua;
            var ruoli = Enum.GetValues<RuoloOperatore>();
            var etichette = ruoli.Select(r => Etichetta(r)).ToList();

            Assert.All(etichette, e => Assert.False(string.IsNullOrWhiteSpace(e)));
            Assert.Equal(ruoli.Length, etichette.Distinct().Count());
            Assert.Equal(ruoli.Select(r => r.Label()), etichette);
            // Standard e Viewer hanno nomi enum diversi dall'etichetta: il menu non deve mostrare il nome grezzo.
            Assert.NotEqual(nameof(RuoloOperatore.Standard), Etichetta(RuoloOperatore.Standard));
            Assert.NotEqual(nameof(RuoloOperatore.Viewer), Etichetta(RuoloOperatore.Viewer));
        }
        finally { loc.Lingua = originale; }
    }

    [Fact]
    public void Etichetta_italiana_del_ruolo_standard_e_operatore()
    {
        var loc = LocalizationService.Instance;
        var originale = loc.Lingua;
        try
        {
            loc.Lingua = "it";
            Assert.Equal("Operatore", Etichetta(RuoloOperatore.Standard));
            Assert.Equal("Viewer (sola lettura)", Etichetta(RuoloOperatore.Viewer));
        }
        finally { loc.Lingua = originale; }
    }

    [Fact]
    public void Round_trip_etichetta_riporta_allo_stesso_ruolo_enum_per_ogni_valore()
    {
        var loc = LocalizationService.Instance;
        var originale = loc.Lingua;
        try
        {
            loc.Lingua = "it";
            var ruoli = Enum.GetValues<RuoloOperatore>();
            foreach (var salvato in ruoli)
            {
                // Simula la selezione nella ComboBox: l'elemento scelto è l'enum, la voce mostrata è l'etichetta.
                var mostrata = Etichetta(salvato)!;
                Assert.Equal(salvato, RuoloDaEtichetta(mostrata, ruoli));
            }
        }
        finally { loc.Lingua = originale; }
    }

    [Fact]
    public void Valore_non_enum_o_nullo_non_produce_etichetta()
    {
        Assert.Null(Etichetta(null));
        Assert.Null(Etichetta("Master"));
    }
}
