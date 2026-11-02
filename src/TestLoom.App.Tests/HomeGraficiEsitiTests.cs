using System.Diagnostics;
using System.Globalization;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using SkiaSharp;

namespace TestLoom.App.Tests;

/// <summary>Nei grafici "Esiti per Veicolo" non a ciambella l'esito resta visibile (barre impilate,
/// torta a spicchi aggregati con dettaglio modello/stato nella legenda estesa, treemap a fasce).</summary>
public sealed class HomeGraficiEsitiTests
{
    private static readonly TipoGrafico[] Tipi =
        { TipoGrafico.BarreVerticali, TipoGrafico.BarreOrizzontali, TipoGrafico.Torta, TipoGrafico.Treemap, TipoGrafico.Linea, TipoGrafico.Area };

    private static List<(string Nome, Esito Esito, long Valore)> Dati() => new()
    {
        ("Veicolo A", Esito.Conforme, 40), ("Veicolo A", Esito.NonConforme, 10), ("Veicolo A", Esito.ConformeConDeviazioni, 5),
        ("Veicolo B", Esito.Conforme, 20), ("Veicolo B", Esito.NonEseguitoNA, 15),
        ("Veicolo C", Esito.NonConforme, 12), ("Veicolo C", Esito.DaRivedere, 6),
    };

    private static Dictionary<SKColor, int> ConteggiColori(byte[] png)
    {
        using var bmp = SKBitmap.Decode(png);
        var d = new Dictionary<SKColor, int>();
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                d[c] = d.GetValueOrDefault(c) + 1;
            }
        return d;
    }

    private static (float CentroX, float CentroY, float Raggio) GeometriaDisco(byte[] png, IEnumerable<SKColor> colori)
    {
        using var bmp = SKBitmap.Decode(png);
        var bersagli = colori.ToHashSet();
        var minimoX = bmp.Width;
        var massimoX = -1;
        var minimoY = bmp.Height;
        var massimoY = -1;
        // La legenda sta a destra; limita il riconoscimento all'area del grafico per non
        // includere i campioni colorati della legenda nel riquadro del disco.
        var limiteX = (int)(bmp.Width * 0.65f);
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < limiteX; x++)
                if (bersagli.Contains(bmp.GetPixel(x, y)))
                {
                    minimoX = Math.Min(minimoX, x);
                    massimoX = Math.Max(massimoX, x);
                    minimoY = Math.Min(minimoY, y);
                    massimoY = Math.Max(massimoY, y);
                }

        if (massimoX < minimoX || massimoY < minimoY)
            throw new InvalidOperationException("Nessun disco colorato trovato nel PNG della torta.");
        var centroX = (minimoX + massimoX) / 2f;
        var centroY = (minimoY + massimoY) / 2f;
        var raggio = Math.Min(massimoX - minimoX, massimoY - minimoY) / 2f;
        return (centroX, centroY, raggio);
    }

    private static void Salva(TipoGrafico tipo, byte[] png)
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "AGENTS.md"))) dir = Path.GetDirectoryName(dir);
            if (dir == null) return;
            var out_ = Path.Combine(dir, ".ai", "artifacts", "release-6.7.13", "grafici-anteprime");
            Directory.CreateDirectory(out_);
            File.WriteAllBytes(Path.Combine(out_, $"esiti-per-veicolo-{tipo}.png"), png);
        }
        catch { /* best effort: solo anteprima per ispezione */ }
    }

    [Theory]
    [InlineData(TipoGrafico.BarreVerticali)]
    [InlineData(TipoGrafico.BarreOrizzontali)]
    [InlineData(TipoGrafico.Torta)]
    [InlineData(TipoGrafico.Treemap)]
    [InlineData(TipoGrafico.Linea)]
    [InlineData(TipoGrafico.Area)]
    public void OgniTipo_MostraIColoriDegliEsiti(TipoGrafico tipo)
    {
        var png = ChartRenderer.SottoveicoloEsitiPerTipo(tipo, "Esiti per Veicolo", Dati());
        Salva(tipo, png);
        var colori = ConteggiColori(png);
        foreach (var e in new[] { Esito.Conforme, Esito.ConformeConDeviazioni, Esito.NonConforme, Esito.NonEseguitoNA })
            Assert.True(colori.GetValueOrDefault(ChartRenderer.ColoreEsito(e)) > 200, $"{tipo}: colore esito {e} troppo poco presente");
    }

    [Fact]
    public void Esiti_Assenti_NonCompaionoNelGrafico()
    {
        var solo = new List<(string, Esito, long)> { ("A", Esito.Conforme, 10), ("B", Esito.Conforme, 5) };
        foreach (var tipo in Tipi)
        {
            var colori = ConteggiColori(ChartRenderer.SottoveicoloEsitiPerTipo(tipo, "t", solo));
            Assert.True(colori.GetValueOrDefault(ChartRenderer.ColoreEsito(Esito.Conforme)) > 200, tipo.ToString());
            Assert.Equal(0, colori.GetValueOrDefault(ChartRenderer.ColoreEsito(Esito.NonConforme)));
        }
    }

    [Fact]
    public void Altri_SommaGliEsitiDelResto()
    {
        var righe = new List<(string, Esito, long)>();
        for (int i = 0; i < 30; i++)
        {
            righe.Add(($"V{i:D2}", Esito.Conforme, 100 - i));
            righe.Add(($"V{i:D2}", Esito.NonConforme, 1));
        }
        var v = ChartRenderer.PreparaVeicoliEsiti(righe, 12);
        Assert.Equal(13, v.Count);
        var altri = v[^1];
        Assert.Equal(HomeChartCatalog.EtichettaAltri, altri.Nome);
        var resto = Enumerable.Range(12, 18).ToList();
        Assert.Equal(resto.Sum(i => 100L - i), altri.PerEsito[0]);   // Conforme
        Assert.Equal(18, altri.PerEsito[2]);                          // NonConforme
        Assert.Equal(righe.Sum(r => r.Item3), v.Sum(x => x.Totale)); // nessun test perso
    }

    [Fact]
    public void Torta_GliArchiStatoPartizionanoOgniArcoVeicolo()
    {
        var veicoli = ChartRenderer.PreparaVeicoliEsiti(Dati(), 12);
        var archi = ChartRenderer.PianificaTortaEsiti(veicoli);

        Assert.Equal(new[] { "Veicolo A", "Veicolo B", "Veicolo C" }, archi.Select(a => a.Nome));
        Assert.Equal(360d, archi.Sum(a => a.Ampiezza), 10);
        for (var i = 0; i < archi.Count; i++)
        {
            var veicolo = archi[i];
            Assert.Equal(veicoli[i].Totale, veicolo.Valore);
            Assert.Equal(veicolo.Ampiezza, veicolo.Stati.Sum(s => s.Ampiezza), 10);
            Assert.Equal(100d, veicolo.Stati.Sum(s => s.PercentualeVeicolo), 10);
            Assert.Equal(veicolo.Inizio, veicolo.Stati[0].Inizio, 10);
            for (var j = 1; j < veicolo.Stati.Count; j++)
                Assert.Equal(veicolo.Stati[j - 1].Inizio + veicolo.Stati[j - 1].Ampiezza, veicolo.Stati[j].Inizio, 10);
        }

        var conformeA = Assert.Single(archi[0].Stati, s => s.Esito == Esito.Conforme);
        Assert.Equal(40, conformeA.Valore);
        Assert.Equal(100d * 40 / 55, conformeA.PercentualeVeicolo, 10);
        Assert.Equal(360d * 40 / 108, conformeA.Ampiezza, 10);
    }

    [Fact]
    public void LegendaTorta_DettagliataConservaITotaliPerModelloEStato()
    {
        var dati = Dati();
        var veicoli = ChartRenderer.PreparaVeicoliEsiti(dati, 12);
        var archi = ChartRenderer.PianificaTortaEsiti(veicoli);

        Assert.Equal(dati.Sum(r => r.Valore), archi.Sum(a => a.Valore));
        Assert.Equal(dati.Sum(r => r.Valore), archi.SelectMany(a => a.Stati).Sum(s => s.Valore));
        Assert.Equal(dati.Sum(r => r.Valore), veicoli.Sum(v => v.Totale));
    }

    [Fact]
    public void Torta_UsaSpicchiAggregatiPerEsitoInUnSoloDisco()
    {
        var righe = new List<(string Nome, Esito Esito, long Valore)>
        {
            ("Veicolo A", Esito.Conforme, 70),
            ("Veicolo B", Esito.Conforme, 20),
            ("Veicolo B", Esito.NonConforme, 10),
        };
        var veicoli = ChartRenderer.PreparaVeicoliEsiti(righe, 12);
        var spicchi = ChartRenderer.PianificaSpicchiTortaEsiti(veicoli);

        Assert.Equal(new[] { Esito.Conforme, Esito.NonConforme }, spicchi.Select(s => s.Esito));
        Assert.Equal(new long[] { 90, 10 }, spicchi.Select(s => s.Valore));
        Assert.Equal(360d, spicchi.Sum(s => s.Ampiezza), 10);
        Assert.Equal(324d, spicchi[0].Ampiezza, 10);
        Assert.Equal(90d, spicchi[0].PercentualeTotale, 10);
        Assert.Equal(spicchi[0].Inizio + spicchi[0].Ampiezza, spicchi[1].Inizio, 10);

        var legenda = ChartRenderer.VociLegendaSpicchiTortaEsiti(spicchi);
        Assert.Equal(2, legenda.Count);
        Assert.Equal(ChartRenderer.ColoreEsito(Esito.Conforme), legenda[0].Colore);
        Assert.Equal(ChartRenderer.ColoreEsito(Esito.NonConforme), legenda[1].Colore);
        Assert.DoesNotContain("Veicolo", string.Join(" ", legenda.Select(v => v.Testo)), StringComparison.Ordinal);

        var png = ChartRenderer.SottoveicoloEsitiPerTipo(TipoGrafico.Torta, "Esiti", righe);
        var colori = spicchi.Select(s => ChartRenderer.ColoreEsito(s.Esito))
            .Concat(ChartRenderer.PianificaTortaEsiti(veicoli).Select(v => v.Colore));
        var (cx, cy, raggio) = GeometriaDisco(png, colori);
        using var bitmap = SKBitmap.Decode(png);
        var coloreCentro = bitmap.GetPixel((int)(cx + raggio * 0.2f), (int)cy);
        var coloreVersoBordo = bitmap.GetPixel((int)(cx + raggio * 0.85f), (int)cy);
        Assert.Equal(ChartRenderer.ColoreEsito(Esito.Conforme), coloreCentro);
        Assert.Equal(coloreCentro, coloreVersoBordo);
    }

    [Fact]
    public void Torta_ConUnaCategoriaDisegnaUnDiscoIntero()
    {
        var righe = new List<(string Nome, Esito Esito, long Valore)> { ("Veicolo A", Esito.Conforme, 5) };
        var veicoli = ChartRenderer.PreparaVeicoliEsiti(righe, 12);
        var spicchio = Assert.Single(ChartRenderer.PianificaSpicchiTortaEsiti(veicoli));

        Assert.Equal(360d, spicchio.Ampiezza, 10);
        Assert.Equal(100d, spicchio.PercentualeTotale, 10);
        var colori = ConteggiColori(ChartRenderer.SottoveicoloEsitiPerTipo(TipoGrafico.Torta, "Esiti", righe));
        Assert.True(colori.GetValueOrDefault(ChartRenderer.ColoreEsito(Esito.Conforme)) > 10_000);
    }

    [Fact]
    public void Torta_MantieneVisibileUnoSpicchioInferioreAUnGradoEmezzo()
    {
        var righe = new List<(string Nome, Esito Esito, long Valore)>
        {
            ("Modello principale", Esito.Conforme, 299),
            ("Modello raro", Esito.NonConforme, 1),
        };
        var veicoli = ChartRenderer.PreparaVeicoliEsiti(righe, 12);
        var raro = Assert.Single(ChartRenderer.PianificaSpicchiTortaEsiti(veicoli), s => s.Esito == Esito.NonConforme);
        Assert.InRange(raro.Ampiezza, 1.1d, 1.3d);

        var png = ChartRenderer.SottoveicoloEsitiPerTipo(TipoGrafico.Torta, "Esito raro", righe);
        var colori = Enum.GetValues<Esito>().Select(ChartRenderer.ColoreEsito);
        var (cx, cy, raggio) = GeometriaDisco(png, colori);
        using var bitmap = SKBitmap.Decode(png);
        var conteggioSpicchioRaro = 0;
        var coloreRaro = ChartRenderer.ColoreEsito(Esito.NonConforme);
        var raggio2 = raggio * raggio;
        for (var y = (int)(cy - raggio); y <= cy + raggio; y++)
        for (var x = (int)(cx - raggio); x <= cx + raggio; x++)
        {
            var dx = x - cx;
            var dy = y - cy;
            if (dx * dx + dy * dy <= raggio2 && bitmap.GetPixel(x, y) == coloreRaro)
                conteggioSpicchioRaro++;
        }
        Assert.True(conteggioSpicchioRaro > 5, $"Lo spicchio raro dovrebbe restare visibile; pixel pieni: {conteggioSpicchioRaro}.");
    }

    [Fact]
    public void Torta_DodiciModelliPiuAltri_HannoTuttiSettoriPadreDistinti()
    {
        var righe = Enumerable.Range(0, 13)
            .Select(i => ($"Modello {i:00}", Esito.Conforme, 1L))
            .ToArray();
        var modelli = ChartRenderer.PreparaVeicoliEsiti(righe, HomeChartCatalog.MaxVociBarreTorta);
        var archi = ChartRenderer.PianificaTortaEsiti(modelli);

        Assert.Equal(13, archi.Count);
        Assert.Equal(13, archi.Select(a => a.Colore).Distinct().Count());
        Assert.Equal(HomeChartCatalog.EtichettaAltri, archi[^1].Nome);
    }

    [Theory]
    [InlineData("it", "it-IT", "Modello → Stato (conteggio, % del modello)", "Conforme: 40 (72,7%) · Veicolo A")]
    [InlineData("en", "en-US", "Model → Status (count, % of model)", "Pass: 40 (72.7%) · Veicolo A")]
    [InlineData("fr", "fr-FR", "Modèle → État (nombre, % du modèle)", "Conforme : 40 (72,7 %) · Veicolo A")]
    public void Torta_LegendaMostraModelloStatoConteggioEQuotaNellaLinguaSelezionata(
        string lingua, string cultura, string intestazioneAttesa, string voceAttesa)
    {
        var loc = LocalizationService.Instance;
        var linguaOriginale = loc.Lingua;
        var culturaOriginale = CultureInfo.CurrentCulture;
        var culturaUiOriginale = CultureInfo.CurrentUICulture;
        try
        {
            loc.Lingua = lingua;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultura);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);

            var veicoli = ChartRenderer.PreparaVeicoliEsiti(Dati(), 12);
            var voci = ChartRenderer.VociLegendaTortaEsiti(ChartRenderer.PianificaTortaEsiti(veicoli));

            Assert.Equal(intestazioneAttesa, voci[0].Testo);
            Assert.True(voci[1].Intestazione);
            Assert.Contains(voci, v => v.Testo == voceAttesa && !v.Intestazione);
        }
        finally
        {
            loc.Lingua = linguaOriginale;
            CultureInfo.CurrentCulture = culturaOriginale;
            CultureInfo.CurrentUICulture = culturaUiOriginale;
        }
    }

    [Fact]
    public void Torta_LegendaEspandibileConservaTuttiIModelliEStatiAncheQuandoLaLegendaRasterVieneAccorciata()
    {
        var righe = Enumerable.Range(0, 13)
            .SelectMany(i => Enum.GetValues<Esito>().Select(esito => ($"Modello {i:00}", esito, 1L)))
            .ToArray();
        var dati = new DatiEsiti(Array.Empty<(string Nome, long Valore)>(), Array.Empty<(Esito Esito, long Valore)>(), righe);

        var legenda = GraficoLegendaCompleta.Crea(dati, TipoGrafico.Torta);

        Assert.Equal(105, legenda.Count);
        Assert.Equal(13, legenda.Count(voce => voce.Intestazione && voce.Testo != legenda[0].Testo));
        Assert.All(legenda.Where(voce => voce.Intestazione), voce => Assert.Null(voce.Colore));
        Assert.Contains(legenda, voce => !voce.Intestazione && voce.Colore != null);
        Assert.Contains(legenda, voce => voce.Intestazione && voce.Testo.Contains(HomeChartCatalog.EtichettaAltri, StringComparison.Ordinal));
        Assert.Contains(legenda, voce => !voce.Intestazione && voce.Testo.Contains(HomeChartCatalog.EtichettaAltri, StringComparison.Ordinal));
    }

    [Fact]
    public void TortaSenzaDati_RenderValidoESenzaArchiOLegenda()
    {
        var veicoli = ChartRenderer.PreparaVeicoliEsiti(Array.Empty<(string, Esito, long)>(), 12);
        var archi = ChartRenderer.PianificaTortaEsiti(veicoli);
        Assert.Empty(archi);
        Assert.Empty(ChartRenderer.PianificaSpicchiTortaEsiti(veicoli));
        Assert.Empty(ChartRenderer.VociLegendaTortaEsiti(archi));

        var png = ChartRenderer.SottoveicoloEsitiPerTipo(TipoGrafico.Torta, "Vuoto", Array.Empty<(string, Esito, long)>());
        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        Assert.Equal(900, bitmap.Width);
        Assert.Equal(480, bitmap.Height);
    }

    [Fact]
    public void Raggruppa_UnisceNomiCaseInsensitive_EScartaZeri()
    {
        var v = ChartRenderer.PreparaVeicoliEsiti(new[] { ("x", Esito.Conforme, 2L), ("X", Esito.Conforme, 3L), ("y", Esito.Conforme, 0L) }, 12);
        var uno = Assert.Single(v);
        Assert.Equal(5, uno.Totale);
    }

    [Fact]
    public void DatiVuoti_ProduconoPngSenzaEccezioni()
    {
        foreach (var tipo in Tipi)
        {
            var png = ChartRenderer.SottoveicoloEsitiPerTipo(tipo, "Vuoto", Array.Empty<(string, Esito, long)>());
            Assert.True(png.Length > 100 && png[1] == (byte)'P');
            png = ChartRenderer.SottoveicoloEsitiPerTipo(tipo, "Zeri", new[] { ("A", Esito.Conforme, 0L) });
            Assert.True(png.Length > 100);
        }
    }

    [Fact]
    public void MilleVeicoli_RenderLimitato_NessunaEccezione()
    {
        var esiti = new[] { Esito.Conforme, Esito.NonConforme, Esito.ConformeConDeviazioni, Esito.NonEseguitoNA };
        var righe = Enumerable.Range(0, 1500).SelectMany(i => esiti.Select((e, k) => ($"Veicolo-{i:D4}", e, (long)((i + k) % 7) + 1))).ToList();
        foreach (var tipo in Tipi)
        {
            var sw = Stopwatch.StartNew();
            var png = ChartRenderer.SottoveicoloEsitiPerTipo(tipo, "Mille", righe);
            Assert.True(png.Length > 100);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"{tipo}: {sw.ElapsedMilliseconds} ms");
        }
    }
}
