using System.Globalization;
using TestLoom.App;

namespace TestLoom.App.Tests;

/// <summary>6.6.1: convertitori del layout adattivo. Colonne dei KPI (Home 6 elementi, Audit 7) con colonna minima
/// 150 DIP e righe bilanciate (mai una riga orfana), e parametro "riserva|minimo" di AltezzaResiduaConverter.</summary>
public class ColonneDaLarghezzaConverterTests
{
    private const double MinColonna = 150;

    [Theory]
    [InlineData(1600, 6, 6)]  // Ampio: 6 KPI su una riga
    [InlineData(1600, 7, 7)]  // Ampio: 7 KPI su una riga
    [InlineData(1070, 6, 6)]  // Home in Standard 1366 (contenuto 1070): resta su una riga, come oggi
    [InlineData(1070, 7, 7)]  // Audit in Standard 1366: resta su una riga, come oggi
    [InlineData(1048, 6, 6)]  // Home in Compatto 1280x800
    [InlineData(1048, 7, 4)]  // Audit in Compatto 1280x800: 4 + 3, mai 6 + 1
    [InlineData(936, 6, 6)]   // Home in Minimo 1024x600
    [InlineData(936, 7, 4)]   // Audit in Minimo 1024x600: 4 + 3
    [InlineData(700, 6, 3)]   // 4 colonne entrano: 6 voci diventano 3 + 3
    [InlineData(600, 6, 3)]   // come sopra, con 4 colonne che entrano
    [InlineData(300, 6, 2)]   // 2 colonne: 2 + 2 + 2
    [InlineData(150, 6, 1)]   // una colonna: 6 righe
    [InlineData(0, 6, 6)]     // primo layout (larghezza 0): tutte le colonne
    [InlineData(double.NaN, 7, 7)]
    public void Calcola_RigheBilanciate_DaLarghezzaMinimaEMassimo(double larghezza, int elementi, int atteso) =>
        Assert.Equal(atteso, TestLoom.App.ColonneDaLarghezzaConverter.Calcola(larghezza, MinColonna, elementi));

    [Theory]
    [InlineData(6, 4, 3)]     // 6 voci con 4 colonne: 3 + 3
    [InlineData(7, 6, 4)]     // 7 voci con 6 colonne: 4 + 3
    [InlineData(5, 4, 3)]     // 5 voci con 4 colonne: 3 + 2
    [InlineData(9, 4, 3)]     // 9 voci con 4 colonne: 3 + 3 + 3
    public void Calcola_NonLasciaMaiRigheOrfane(int elementi, int colonneCheEntrano, int atteso) =>
        Assert.Equal(atteso, TestLoom.App.ColonneDaLarghezzaConverter.Calcola(colonneCheEntrano * MinColonna, MinColonna, elementi));

    [Fact]
    public void Convert_LeggeIlParametroMinColonnaEMassimo()
    {
        var colonne = TestLoom.App.ColonneDaLarghezzaConverter.Instance.Convert(1070.0, typeof(int), "150|6", CultureInfo.InvariantCulture);
        Assert.Equal(6, colonne);
    }

    [Fact]
    public void Convert_SenzaValoreNumericoUsaTutteLeColonne()
    {
        var colonne = TestLoom.App.ColonneDaLarghezzaConverter.Instance.Convert(null, typeof(int), "150|6", CultureInfo.InvariantCulture);
        Assert.Equal(6, colonne);
    }
}

public class AltezzaResiduaConverterParametriTests
{
    private static double Conv(double altezza, string? parametro) =>
        (double)TestLoom.App.AltezzaResiduaConverter.Instance.Convert(altezza, typeof(double), parametro, CultureInfo.InvariantCulture)!;

    [Fact]
    public void RiservaEMinimo_DalParametroRiserva_Barra_Minimo() => Assert.Equal(870, Conv(1000, "130|240"), 6);

    [Fact]
    public void Minimo_DalParametroVienePrimaDellaRiserva() => Assert.Equal(240, Conv(300, "130|240"), 6);

    [Fact]
    public void SoloRiserva_MantieneIlMinimoPredefinito() => Assert.Equal(160, Conv(300, "140"), 6);

    [Fact]
    public void Overlay_CentratoConRiservaDi80() => Assert.Equal(420, Conv(500, "80|240"), 6);
}
