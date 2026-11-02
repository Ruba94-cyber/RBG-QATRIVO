using System.Globalization;
using TestLoom.App;
using Xunit;

namespace TestLoom.App.Tests;

public class AltezzaResiduaConverterTests
{
    private static double Conv(object? v, string? p = "400") =>
        (double)AltezzaResiduaConverter.Instance.Convert(v, typeof(double), p, CultureInfo.InvariantCulture)!;

    [Theory]
    [InlineData(817.0, 417.0)]
    [InlineData(500.0, 160.0)]
    [InlineData(1200.0, 800.0)]
    public void Altezza_meno_riserva_con_minimo(double altezza, double atteso) => Assert.Equal(atteso, Conv(altezza), 6);

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(-5.0)]
    public void Altezza_non_valida_usa_massimo_primo_layout(double altezza) => Assert.Equal(420, Conv(altezza));

    [Fact]
    public void Parametro_assente_usa_riserva_predefinita() => Assert.Equal(417, Conv(817.0, null), 6);
}

public class EtichettaRuoloLocalizzataConverterTests
{
    private static string? Conv(string? etichetta, string param) =>
        (string?)EtichettaRuoloLocalizzataConverter.Instance.Convert(new object?[] { etichetta, "x" }, typeof(string), param, CultureInfo.InvariantCulture);

}
