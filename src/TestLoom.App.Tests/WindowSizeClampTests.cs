using TestLoom.App.Views;
using Xunit;

namespace TestLoom.App.Tests;

/// <summary>Funzione pura di <see cref="WindowSizeClamp.Dimensione"/>: area di lavoro in pixel fisici, scala in DIP.
/// Il margine di cornice (<see cref="WindowSizeClamp.MargineCornice"/>, 48 DIP) viene sottratto all'altezza e alla larghezza.</summary>
public class WindowSizeClampTests
{
    // MainWindow: 1400x860 predefinito, MinWidth 1000, MinHeight 560.
    private const double LarghezzaPredefinita = 1400;
    private const double AltezzaPredefinita = 860;
    private const double MinLarghezza = 1000;
    private const double MinAltezza = 560;

    [Theory]
    // 1024x700 (Area di lavoro: 1024 px di larghezza)
    [InlineData(1024, 1.00, 1000)]
    [InlineData(1024, 1.25, 1000)]
    [InlineData(1024, 1.50, 1000)]
    // 1366x768
    [InlineData(1366, 1.00, 1318)]
    [InlineData(1366, 1.25, 1044.8)]
    [InlineData(1366, 1.50, 1000)]
    // 1920x1080
    [InlineData(1920, 1.00, 1400)]
    [InlineData(1920, 1.25, 1400)]
    [InlineData(1920, 1.50, 1232)]
    public void Larghezza_limitata_all_area_di_lavoro_senza_scendere_sotto_il_minimo(int areaPixel, double scala, double atteso) =>
        Assert.Equal(atteso, WindowSizeClamp.Dimensione(LarghezzaPredefinita, MinLarghezza, areaPixel, scala), 6);

    [Theory]
    // 1024x700
    [InlineData(700, 1.00, 652)]
    [InlineData(700, 1.25, 560)]
    [InlineData(700, 1.50, 560)]
    // 1366x768
    [InlineData(768, 1.00, 720)]
    [InlineData(768, 1.25, 566.4)]
    [InlineData(768, 1.50, 560)]
    // 1920x1080
    [InlineData(1080, 1.00, 860)]
    [InlineData(1080, 1.25, 816)]
    [InlineData(1080, 1.50, 672)]
    public void Altezza_limitata_all_area_di_lavoro_senza_scendere_sotto_il_minimo(int areaPixel, double scala, double atteso) =>
        Assert.Equal(atteso, WindowSizeClamp.Dimensione(AltezzaPredefinita, MinAltezza, areaPixel, scala), 6);

    [Fact]
    public void Su_schermo_grande_al_100_percento_la_dimensione_predefinita_non_cambia() =>
        Assert.Equal(LarghezzaPredefinita, WindowSizeClamp.Dimensione(LarghezzaPredefinita, MinLarghezza, 2560, 1.0));

    [Fact]
    public void Scala_non_valida_viene_trattata_come_uno() =>
        Assert.Equal(1318, WindowSizeClamp.Dimensione(1400, 1000, 1366, 0), 6);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-10.0)]
    [InlineData(double.NaN)]
    public void Dimensione_non_positiva_o_non_numerica_viene_restituita_invariata(double richiesta) =>
        Assert.Equal(richiesta, WindowSizeClamp.Dimensione(richiesta, 100, 1024, 1.0));

    [Fact]
    public void Minimo_maggiore_dell_area_vince_sul_limite_per_non_violare_MinWidth() =>
        Assert.Equal(900, WindowSizeClamp.Dimensione(1400, 900, 800, 1.0));
}
