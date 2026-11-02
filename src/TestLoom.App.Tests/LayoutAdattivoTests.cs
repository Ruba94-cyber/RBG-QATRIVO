using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.6.1: modo di layout dalla dimensione client, con isteresi di 16 px, e valori dei token per modo.</summary>
public class LayoutAdattivoTests
{
    private static StatoLayout StatoDi(ModoLayout modo) => new((int)modo, (int)modo);

    [Theory]
    [InlineData(1280, 672, ModoLayout.Compatto)]  // caso peggiore: FHD al 150% meno barra delle applicazioni
    [InlineData(1280, 800, ModoLayout.Compatto)]  // PC di sviluppo: 2560x1600 al 200%
    [InlineData(1366, 768, ModoLayout.Standard)]
    [InlineData(1920, 1080, ModoLayout.Ampio)]
    [InlineData(1024, 600, ModoLayout.Minimo)]
    [InlineData(2560, 1440, ModoLayout.Ampio)]
    [InlineData(1599, 900, ModoLayout.Standard)]  // sotto 1600 non e' Ampio
    [InlineData(1600, 899, ModoLayout.Standard)]  // sotto 900 non e' Ampio
    [InlineData(1365, 900, ModoLayout.Compatto)]  // sotto 1366 non e' Standard
    [InlineData(1099, 900, ModoLayout.Minimo)]    // sotto 1100 e' Minimo
    [InlineData(1200, 639, ModoLayout.Minimo)]    // sotto 640 in altezza e' Minimo
    [InlineData(1100, 640, ModoLayout.Compatto)]  // soglie esatte di Minimo
    public void SenzaStatoPrecedente_ValgonoLeSogliEsatte(double larghezza, double altezza, ModoLayout atteso) =>
        Assert.Equal(atteso, LayoutAdattivo.Modo(larghezza, altezza));

    [Theory]
    // Da Standard si scende a Compatto solo quando la larghezza e' 16 px sotto 1366, cioe' sotto 1350.
    [InlineData(ModoLayout.Standard, 1350, 768, ModoLayout.Standard)]
    [InlineData(ModoLayout.Standard, 1349, 768, ModoLayout.Compatto)]
    // Da Compatto si sale a Standard appena la larghezza raggiunge la soglia esatta, 1366.
    [InlineData(ModoLayout.Compatto, 1365, 768, ModoLayout.Compatto)]
    [InlineData(ModoLayout.Compatto, 1366, 768, ModoLayout.Standard)]
    // Altezza: da Standard si scende sotto 760 - 16 = 744.
    [InlineData(ModoLayout.Standard, 1400, 744, ModoLayout.Standard)]
    [InlineData(ModoLayout.Standard, 1400, 743, ModoLayout.Compatto)]
    // Ampio: si scende a Standard sotto 1600 - 16 = 1584.
    [InlineData(ModoLayout.Ampio, 1584, 1000, ModoLayout.Ampio)]
    [InlineData(ModoLayout.Ampio, 1583, 1000, ModoLayout.Standard)]
    // Minimo: si esce verso Compatto a 1100 esatti; si torna in Minimo sotto 1100 - 16 = 1084 (dal Compatto).
    [InlineData(ModoLayout.Minimo, 1099, 700, ModoLayout.Minimo)]
    [InlineData(ModoLayout.Minimo, 1100, 700, ModoLayout.Compatto)]
    [InlineData(ModoLayout.Compatto, 1084, 700, ModoLayout.Compatto)]
    [InlineData(ModoLayout.Compatto, 1083, 700, ModoLayout.Minimo)]
    public void ConStatoPrecedente_LIsteresiTrattieneIlModoAiBordi(ModoLayout precedente, double larghezza, double altezza, ModoLayout atteso) =>
        Assert.Equal(atteso, LayoutAdattivo.Calcola(larghezza, altezza, StatoDi(precedente)).Modo);

    [Fact]
    public void Isteresi_E_DiSedici_Pixel() => Assert.Equal(16, LayoutAdattivo.Isteresi);

    [Fact]
    public void L_ordineDeiModi_VaDalPiuPiccoloAlPiuAmpio()
    {
        Assert.True(ModoLayout.Minimo < ModoLayout.Compatto);
        Assert.True(ModoLayout.Compatto < ModoLayout.Standard);
        Assert.True(ModoLayout.Standard < ModoLayout.Ampio);
    }

    [Fact]
    public void TokenDiStandardEAmpio_MantengonoLAspettoDiOggi()
    {
        var standard = LayoutAdattivo.Token(ModoLayout.Standard);
        var ampio = LayoutAdattivo.Token(ModoLayout.Ampio);
        Assert.Equal(28, standard.PagePadding);
        Assert.Equal(28, ampio.PagePadding);
        Assert.Equal(16, standard.CardPadding);
        Assert.Equal(20, standard.SpaziaturaSezioni);
        Assert.Equal(22, standard.TitoloPagina);
        Assert.Equal(26, standard.KpiNumero);
        Assert.Equal(22, standard.KpiNumeroPiccolo);
        Assert.Equal(240, standard.LarghezzaSidebar);
        Assert.Equal(200, standard.AltezzaMinimaTabella);
        // Standard resta sui 1180 di CR (il massimo piu' largo oggi tra le pagine); Ampio allarga a 1600.
        Assert.Equal(1180, standard.MaxLarghezzaPagina);
        Assert.Equal(1600, ampio.MaxLarghezzaPagina);
    }

    [Fact]
    public void TokenDiCompattoEMinimo_RiduconoPaddingTitoliKpiESidebar()
    {
        var compatto = LayoutAdattivo.Token(ModoLayout.Compatto);
        var minimo = LayoutAdattivo.Token(ModoLayout.Minimo);
        Assert.Equal(16, compatto.PagePadding);
        Assert.Equal(12, compatto.CardPadding);
        Assert.Equal(18, compatto.TitoloPagina);
        Assert.Equal(20, compatto.KpiNumero);
        Assert.Equal(20, compatto.KpiNumeroPiccolo);
        Assert.Equal(200, compatto.LarghezzaSidebar);
        // Spaziatura tra sezioni ridotta di circa il 40% (20 -> 12).
        Assert.True(compatto.SpaziaturaSezioni <= 0.6 * LayoutAdattivo.Token(ModoLayout.Standard).SpaziaturaSezioni);
        Assert.Equal(12, minimo.PagePadding);
        Assert.Equal(10, minimo.CardPadding);
        Assert.Equal(64, minimo.LarghezzaSidebar);
    }

    [Theory]
    [InlineData(ModoLayout.Ampio, "ampio")]
    [InlineData(ModoLayout.Standard, "standard")]
    [InlineData(ModoLayout.Compatto, "compatto")]
    public void UnModoConUnaSolaClasse(ModoLayout modo, string classe) =>
        Assert.Equal(new[] { classe }, LayoutAdattivo.Classi(modo));

    [Fact]
    public void Minimo_ImplicaAncheCompatto()
    {
        Assert.Equal(new[] { "compatto", "minimo" }, LayoutAdattivo.Classi(ModoLayout.Minimo));
    }

    [Fact]
    public void TutteLeClassi_Coprono_Ogni_ClasseUsataDaUnModo()
    {
        foreach (var modo in Enum.GetValues<ModoLayout>())
            foreach (var classe in LayoutAdattivo.Classi(modo))
                Assert.Contains(classe, LayoutAdattivo.TutteLeClassi);
    }

    [Fact]
    public void StatoLayout_IlModoEIlLivelloPiuBassoDeiDueAssi()
    {
        Assert.Equal(ModoLayout.Ampio, new StatoLayout(3, 3).Modo);
        Assert.Equal(ModoLayout.Standard, new StatoLayout(3, 2).Modo);
        Assert.Equal(ModoLayout.Compatto, new StatoLayout(2, 1).Modo);
        Assert.Equal(ModoLayout.Minimo, new StatoLayout(0, 3).Modo);
    }
}
