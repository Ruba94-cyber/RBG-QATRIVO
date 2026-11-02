using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using SkiaSharp;

namespace TestLoom.App.Tests;

/// <summary>Card dei grafici (Category "Grafici", senza piattaforma grafica): altezza entro il 45% della pagina, export PNG
/// con dimensioni e metadati, salvataggio con registrazione nella Cronologia export, copia negli appunti.</summary>
[Trait("Category", "Grafici")]
public class GraficiCardTests
{
    private static ChartRenderizzato CartaEsiti(Action<string>? registra = null) => new()
    {
        Id = HomeChartCatalog.IdDonut,
        Tipo = TipoGrafico.Ciambella,
        Titolo = "Esiti per sottoveicolo",
        Dati = new DatiEsiti(
            new List<(string Nome, long Valore)> { ("Componente-A", 10), ("Componente-B", 4) },
            new List<(Esito Esito, long Valore)> { (Esito.Conforme, 12), (Esito.NonConforme, 2) },
            new List<(string Nome, Esito Esito, long Valore)> { ("Componente-A", Esito.Conforme, 8), ("Componente-A", Esito.NonConforme, 2), ("Componente-B", Esito.Conforme, 4) }),
        Filtri = "Componente: componente-A",
        Registra = registra,
    };

    [Fact]
    public void AltezzaGrafico_MetaLarghezzaMaNonOltreIl45PercentoDellaPagina()
    {
        var card = CartaEsiti();
        card.ImpostaLarghezza(1000);
        card.ImpostaAltezzaVisibile(672);
        Assert.Equal(672 * ChartRenderizzato.FrazioneAltezzaMassima, card.AltezzaGrafico, 3);

        card.ImpostaLarghezza(490);
        Assert.Equal(245, card.AltezzaGrafico, 3);

        card.ImpostaAltezzaVisibile(200);
        Assert.Equal(160, card.AltezzaGrafico, 3);
    }

    [Fact]
    public async Task PngAsync_DimensioneDellaCardScalaDueEMinimoMilleseicento()
    {
        var card = CartaEsiti();
        card.ImpostaLarghezza(600);
        card.ImpostaAltezzaVisibile(672);

        var png = await card.PngAsync();

        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        Assert.True(bmp.Width >= GraficoRenderer.LarghezzaMinimaEsportazionePx, $"larghezza {bmp.Width}");
        Assert.Equal(GraficoRenderer.DimensioniEsportazione(600, card.AltezzaGrafico).PixelAltezza, bmp.Height);
        Assert.Equal(0, bmp.GetPixel(bmp.Width - 1, bmp.Height - 1).Alpha);
        Assert.Equal("Esiti per sottoveicolo", PngMetadati.Leggi(png)["Title"]);
        Assert.Equal("Componente: componente-A", PngMetadati.Leggi(png)["Description"]);
    }

    [Fact]
    public async Task SalvaPngAsync_ScriveIlFileERegistraLaCronologiaExport()
    {
        var registrati = new List<string>();
        var card = CartaEsiti(registrati.Add);
        card.ImpostaLarghezza(600);
        card.ImpostaAltezzaVisibile(672);
        var percorso = Path.Combine(Path.GetTempPath(), "p661-grafici-card-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            Assert.True(await card.SalvaPngAsync(percorso));
            Assert.True(File.Exists(percorso));
            Assert.Equal(new[] { percorso }, registrati);
            using var bmp = SKBitmap.Decode(File.ReadAllBytes(percorso));
            Assert.True(bmp.Width >= GraficoRenderer.LarghezzaMinimaEsportazionePx);
            Assert.NotNull(card.Messaggio);
            Assert.Contains(Path.GetFileName(percorso), card.Messaggio);
        }
        finally
        {
            try { File.Delete(percorso); } catch { }
        }
    }

    [Fact]
    public async Task CopiaAppuntiAsync_PassaAllaVistaIlPngDellaCard()
    {
        var card = CartaEsiti();
        card.ImpostaLarghezza(600);
        card.ImpostaAltezzaVisibile(672);
        byte[]? ricevuto = null;

        Assert.True(await card.CopiaAppuntiAsync(png => { ricevuto = png; return Task.CompletedTask; }));

        Assert.NotNull(ricevuto);
        using var bmp = SKBitmap.Decode(ricevuto);
        Assert.True(bmp.Width >= GraficoRenderer.LarghezzaMinimaEsportazionePx);
        Assert.NotNull(card.Messaggio);
    }

    [Fact]
    public void Snapshot_DellaCardHaIdTitoloEDati()
    {
        var card = CartaEsiti();
        var snapshot = card.Snapshot;
        Assert.NotNull(snapshot);
        Assert.Equal(HomeChartCatalog.IdDonut, snapshot.Id);
        Assert.Equal("Esiti per sottoveicolo", snapshot.Titolo);
        Assert.Same(card.Dati, snapshot.Dati);
        Assert.Equal(TipoGrafico.Ciambella, snapshot.Tipo);
    }

    [Theory]
    [InlineData(HomeChartCatalog.IdCopertura, "Copertura")]
    [InlineData(HomeChartCatalog.IdAndamento, "Andamento")]
    [InlineData(HomeChartCatalog.IdDonut, "Esiti")]
    public void AutomationId_PulsantiConIlNomeDelGrafico(string id, string nome)
    {
        var card = new ChartRenderizzato { Id = id, Pagina = "Home" };
        Assert.Equal(nome, card.Nome);
        Assert.Equal($"Home_Grafico_{nome}_Apri_Button", card.ApriAutomationId);
        Assert.Equal($"Home_Grafico_{nome}_EsportaPng_Button", card.EsportaAutomationId);
        Assert.Equal($"Home_Grafico_{nome}_Copia_Button", card.CopiaAutomationId);
        Assert.Equal($"Home_Chart_{id}", card.AutomationId);
    }
}
