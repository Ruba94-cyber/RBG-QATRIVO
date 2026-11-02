using Avalonia;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Dialoghi (6.6.1): tetto di altezza e larghezza = area di lavoro dello schermo - 48 DIP, in DIP con la scala.</summary>
public class AdattamentoDialogoTests
{
    [Theory]
    [InlineData(1164, 1.5, 728)]   // Panasonic G2: 1164 px fisici di area di lavoro, scala 150%
    [InlineData(1040, 1.0, 992)]
    [InlineData(1000, 0.0, 952)]   // scala non valida: nessuna divisione per zero
    public void AltezzaMassima_SottraeIlMargineDallAreaDiLavoroLogica(int pixel, double scala, double atteso) =>
        Assert.Equal(atteso, AdattamentoDialogo.AltezzaMassima(pixel, scala), 3);

    [Theory]
    [InlineData(1920, 1.5, 1232)]
    [InlineData(1920, 1.0, 1872)]
    [InlineData(1920, 0.0, 1872)]  // scala non valida: come a scala 1
    public void LarghezzaMassima_SottraeIlMargineDallAreaDiLavoroLogica(int pixel, double scala, double atteso) =>
        Assert.Equal(atteso, AdattamentoDialogo.LarghezzaMassima(pixel, scala), 3);

    [Fact]
    public void Caso1280x672_FhdAl150PercentoMenoBarraDelleApplicazioni()
    {
        // 1920x1008 px fisici = 1280x672 logici a 150%: il dialogo resta entro 624 DIP in altezza e 1232 in larghezza.
        Assert.Equal(624, AdattamentoDialogo.AltezzaMassima(1008, 1.5), 3);
        Assert.Equal(1232, AdattamentoDialogo.LarghezzaMassima(1920, 1.5), 3);
        // Centrato: (1920 - 1232 * 1.5) / 2 = 36 px in orizzontale, (1008 - 624 * 1.5) / 2 = 36 px in verticale.
        Assert.Equal(new PixelPoint(36, 36), AdattamentoDialogo.Centra(new PixelRect(0, 0, 1920, 1008), 1.5, 1232, 624));
    }

    [Fact]
    public void Centra_RispettaLOrigineDellAreaDiLavoroDelloSchermoSecondario()
    {
        // Schermo secondario a destra: area che parte da x = 1920; finestra 800x600 DIP a scala 1.
        Assert.Equal(new PixelPoint(2480, 220), AdattamentoDialogo.Centra(new PixelRect(1920, 0, 1920, 1040), 1.0, 800, 600));
    }

    [Fact]
    public void Centra_NonScendeSottoZeroQuandoLaFinestraSuperaLArea()
    {
        Assert.Equal(new PixelPoint(0, 0), AdattamentoDialogo.Centra(new PixelRect(0, 0, 800, 600), 1.0, 1000, 700));
    }
}
