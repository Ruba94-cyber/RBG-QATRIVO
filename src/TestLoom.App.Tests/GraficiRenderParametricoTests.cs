using System.Buffers.Binary;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using SkiaSharp;

namespace TestLoom.App.Tests;

/// <summary>Rendering parametrico dei grafici (Category "Grafici"): dimensioni dei PNG per dimensione e scala, legenda a
/// destra o sotto in base allo spazio, caratteri mai sotto 12 DIP, esportazione PNG con intestazione e metadati.</summary>
[Trait("Category", "Grafici")]
public class GraficiRenderParametricoTests
{
    private static readonly (string Nome, long Valore)[] Sottoveicoli =
    {
        ("Componente-Motore-Alpha", 120), ("Componente-Freno-Beta", 90), ("Componente-Sterzo-Gamma", 70), ("Componente-Cambio-Delta", 55),
        ("Componente-Carrozzeria-Epsilon", 40), ("Componente-Clima-Zeta", 31), ("Componente-Infotainment-Eta", 22), ("Componente-Sospensioni-Theta", 12),
        ("Componente-Luci-Iota", 9), ("Componente-Porte-Kappa", 5),
    };

    private static readonly (Esito Esito, long Valore)[] Esiti =
    {
        (Esito.Conforme, 150), (Esito.ConformeConDeviazioni, 25), (Esito.NonConforme, 18), (Esito.DaRivedere, 6),
        (Esito.NonApplicabile, 4), (Esito.DaEseguire, 30), (Esito.NonEseguitoNA, 2),
    };

    private static IEnumerable<(string Nome, Esito Esito, long Valore)> VeicoloEsito()
    {
        foreach (var (nome, valore) in Sottoveicoli)
            foreach (var (esito, quota) in Esiti.Take(3))
                yield return (nome, esito, Math.Max(1, valore * quota / 200));
    }

    private static DatiEsiti DatiEsiti() => new(Sottoveicoli.ToList(), Esiti.ToList(), VeicoloEsito().ToList());

    private static DatiCopertura DatiCopertura() => new(60, 12, 9, 4);

    private static DatiAndamento DatiAndamento()
    {
        var inizio = new DateTime(2026, 1, 1);
        var punti = Enumerable.Range(0, 40)
            .Select(i => (inizio.AddDays(i * 3), Aperte: 20 - i / 3 + (i % 4), Chiuse: i / 2))
            .ToList();
        return new DatiAndamento(punti);
    }

    private static SKBitmap Decodifica(byte[] png)
    {
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    public static IEnumerable<object[]> DimensioniEScale() => new[]
    {
        new object[] { 320d, 240d, 1d },
        new object[] { 490d, 302d, 1d },
        new object[] { 1000d, 302d, 1.5d },
        new object[] { 900d, 480d, 2d },
        new object[] { 1600d, 900d, 1d },
    };

    [Theory]
    [MemberData(nameof(DimensioniEScale))]
    public void Render_DimensioniPixelCoerentiConLaScala(double larghezza, double altezza, double scala)
    {
        var dim = new DimensioniGrafico(larghezza, altezza, scala);
        var png = GraficoRenderer.Render(DatiCopertura(), TipoGrafico.BarreVerticali, "Copertura", dim);
        using var bmp = Decodifica(png);
        Assert.Equal(dim.PixelLarghezza, bmp.Width);
        Assert.Equal(dim.PixelAltezza, bmp.Height);
    }

    public static IEnumerable<object[]> TuttiITipi()
    {
        foreach (var tipo in new[]
        {
            TipoGrafico.BarreVerticali, TipoGrafico.BarreOrizzontali, TipoGrafico.Torta, TipoGrafico.Ciambella,
            TipoGrafico.Treemap, TipoGrafico.Linea, TipoGrafico.Area,
        })
            foreach (var dim in new[] { (320d, 240d), (490d, 302d), (1000d, 302d), (700d, 700d) })
                yield return new object[] { tipo, dim.Item1, dim.Item2 };
    }

    [Theory]
    [MemberData(nameof(TuttiITipi))]
    public void Render_OgniTipoAOgniDimensione_ProduceUnPngDelleDimensioniRichieste(TipoGrafico tipo, double larghezza, double altezza)
    {
        var dim = new DimensioniGrafico(larghezza, altezza, 1.25);
        foreach (DatiGrafico dati in new DatiGrafico[] { DatiCopertura(), DatiAndamento(), DatiEsiti() })
        {
            if (dati is DatiAndamento && tipo is not (TipoGrafico.Linea or TipoGrafico.Area)) continue;
            if (dati is DatiCopertura && tipo is TipoGrafico.Linea or TipoGrafico.Area) continue;
            var png = GraficoRenderer.Render(dati, tipo, "Titolo di prova", dim);
            using var bmp = Decodifica(png);
            Assert.Equal(dim.PixelLarghezza, bmp.Width);
            Assert.Equal(dim.PixelAltezza, bmp.Height);
        }
    }

    [Fact]
    public void Legenda_AFianco_SeCEroSpazio_ESottoAltrimenti()
    {
        using var font = ChartRenderer.CreaFont(12);
        var voci = Sottoveicoli.Select(s => new VoceLegenda($"{s.Nome} ({s.Valore})", SKColors.Red)).ToList();

        // Card alla larghezza reale del Home a 1280x672: area utile larga, la legenda entra a destra.
        var largo = ChartRenderer.DisponiLegenda(1000, 302, 46, voci, font, corpoMinimoDestra: 195);
        Assert.True(largo.ADestra, "1000x302: la legenda deve stare a destra");
        Assert.DoesNotContain(largo.Voci, v => v.Testo.StartsWith("+"));

        // Card di 490 DIP con 10 voci: il grafico conserva 260 DIP, piu' del minimo, quindi la legenda resta a destra.
        var medio = ChartRenderer.DisponiLegenda(490, 302, 46, voci, font, corpoMinimoDestra: 195);
        Assert.True(medio.ADestra, "490x302: spazio sufficiente per la legenda a destra");

        // Larghezza che non lascia al grafico il minimo: la legenda va sotto il grafico, senza sovrapposizioni.
        var stretto = ChartRenderer.DisponiLegenda(330, 302, 46, voci, font, corpoMinimoDestra: 195);
        Assert.False(stretto.ADestra, "330x302: la legenda deve stare sotto");
        Assert.True(stretto.Legenda.Top >= stretto.Corpo.Bottom, "la legenda non sovrappone il grafico");

        // Finestra alta e stretta: sotto il grafico.
        var verticale = ChartRenderer.DisponiLegenda(300, 600, 46, voci, font, corpoMinimoDestra: 180);
        Assert.False(verticale.ADestra);
    }

    [Fact]
    public void Legenda_TroncaConUnaVoceAltre_SoloSeLoSpazioNonBasta()
    {
        using var font = ChartRenderer.CreaFont(12);
        var molte = Enumerable.Range(1, 60).Select(i => new VoceLegenda($"Voce di legenda numero {i}", SKColors.Blue)).ToList();
        var disposizione = ChartRenderer.DisponiLegenda(420, 220, 46, molte, font, corpoMinimoDestra: 195);
        var ultima = disposizione.Voci[^1];
        Assert.StartsWith("+", ultima.Testo);
        Assert.Equal(molte.Count, disposizione.Voci.Count - 1 + int.Parse(ultima.Testo[1..].Split(' ')[0]));
        var altezzaArea = 220f - 46f - 12f;
        Assert.True(disposizione.Corpo.Height >= altezzaArea * 0.55f - 1, "il grafico resta almeno al 55% dell'altezza disponibile");
    }

    [Fact]
    public void Font_NonScendeMaiSottoDodiciDip()
    {
        Assert.True(ChartRenderer.CreaFont(8).Size >= 12);
        Assert.True(ChartRenderer.CreaFont(11).Size >= 12);
        Assert.True(ChartRenderer.CreaFont(13, grassetto: true).Size >= 12);
        Assert.Equal(ChartRenderer.FontMinimoDip, ChartRenderer.CreaFont(0).Size);
    }

    [Fact]
    public void TroncaAPixel_RestaNeiLimiti()
    {
        using var font = ChartRenderer.CreaFont(12);
        const string testo = "Componente-Infotainment-Eta-Lunghissimo-Nome-Di-Prova";
        var troncato = ChartRenderer.TroncaAPixel(font, testo, 120);
        Assert.True(font.MeasureText(troncato) <= 120);
        Assert.EndsWith("…", troncato);
        Assert.Equal(testo, ChartRenderer.TroncaAPixel(font, testo, 2000));
    }

    [Fact]
    public void Donut_NellaCardDelHome_LaLegendaCompareAFianco_ConIColoriDegliEsiti()
    {
        var png = ChartRenderer.DonutEsitiPerSottoveicolo("Esiti", Sottoveicoli.ToList(), Esiti.ToList(), 1000, 302, 1);
        using var bmp = Decodifica(png);
        // Il campione "Conforme" e' nella parte destra (legenda): cerca il colore esatto dell'esito in quella zona.
        var conforme = ChartRenderer.ColoreEsito(Esito.Conforme);
        var contati = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = bmp.Width / 2; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) == conforme) contati++;
        Assert.True(contati > 20, $"legenda a destra senza campioni colore: {contati}");
    }

    [Fact]
    public void EsportaPng_IntestazioneFiltriEData_MetadatiELarghezzaMinima()
    {
        var generatoIl = new DateTime(2026, 10, 11, 15, 42, 0);
        var grafico = new GraficoSnapshot(HomeChartCatalog.IdCopertura, "Copertura requisiti", DatiCopertura(), TipoGrafico.BarreVerticali);
        var png = GraficoRenderer.EsportaPng(grafico, 600, 300, "Componente: Motore; Veicolo: Alpha", generatoIl);

        using var bmp = Decodifica(png);
        Assert.True(bmp.Width >= GraficoRenderer.LarghezzaMinimaEsportazionePx, $"larghezza {bmp.Width}");

        var metadati = PngMetadati.Leggi(png);
        Assert.Equal("Copertura requisiti", metadati["Title"]);
        Assert.Equal("Componente: Motore; Veicolo: Alpha", metadati["Description"]);
        Assert.Equal("2026-10-11T15:42:00", metadati["Creation Time"]);
        Assert.All(ContaChunk(png, "iTXt"), crcOk => Assert.True(crcOk));
    }

    [Fact]
    public void EsportaPng_SenzaFiltri_IntestazioneConLaDicituraNessunFiltro()
    {
        var grafico = new GraficoSnapshot(HomeChartCatalog.IdAndamento, "Andamento", DatiAndamento(), TipoGrafico.Linea);
        var png = GraficoRenderer.EsportaPng(grafico, 800, 320, "", new DateTime(2026, 1, 2, 3, 4, 0));
        var metadati = PngMetadati.Leggi(png);
        Assert.False(string.IsNullOrWhiteSpace(metadati["Description"]));
    }

    [Theory]
    [InlineData(600, 2.666)]
    [InlineData(1000, 2d)]
    [InlineData(400, 4d)]
    public void DimensioniEsportazione_ScalaAlmenoDueELarghezzaAlmenoMilleseicento(double larghezza, double scalaAttesa)
    {
        var dim = GraficoRenderer.DimensioniEsportazione(larghezza, 300);
        Assert.Equal(scalaAttesa, dim.Scala, 2);
        Assert.True(dim.PixelLarghezza >= Math.Min(1600, larghezza * 2));
    }

    [Fact]
    public void PngMetadati_AggiungeIChunkConCrcValido_SenzaAlterareLImmagine()
    {
        var originale = new DatiCopertura(1, 2, 3, 4);
        var png = GraficoRenderer.Render(originale, TipoGrafico.BarreVerticali, "T", new DimensioniGrafico(300, 200, 1));
        var conMetadati = PngMetadati.Aggiungi(png, ("Title", "àèì — prova"));
        Assert.Equal("àèì — prova", PngMetadati.Leggi(conMetadati)["Title"]);
        Assert.All(ContaChunk(conMetadati, "iTXt"), crcOk => Assert.True(crcOk));
        using var bmpOriginale = Decodifica(png);
        using var bmpConMetadati = Decodifica(conMetadati);
        Assert.Equal(bmpOriginale.Width, bmpConMetadati.Width);
        Assert.Equal(bmpOriginale.Height, bmpConMetadati.Height);
    }

    /// <summary>Controlla il CRC di ogni chunk del tipo indicato (true = corretto).</summary>
    private static IEnumerable<bool> ContaChunk(byte[] png, string tipoCercato)
    {
        var pos = 8;
        while (pos + 12 <= png.Length)
        {
            var lunghezza = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos, 4));
            var tipo = Encoding.ASCII.GetString(png, pos + 4, 4);
            var corpo = png.AsSpan(pos + 4, 4 + lunghezza);
            var crcAtteso = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + lunghezza, 4));
            if (tipo == tipoCercato) yield return Crc32(corpo) == crcAtteso;
            if (tipo == "IEND") yield break;
            pos += 12 + lunghezza;
        }
    }

    private static uint Crc32(ReadOnlySpan<byte> dati)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in dati)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
