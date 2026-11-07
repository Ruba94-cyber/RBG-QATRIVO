using TestLoom.App.Models;
using SkiaSharp;

namespace TestLoom.App.Services;

/// <summary>Rappresentazioni alternative dei grafici categorici della Home (barre orizzontali, torta,
/// treemap) a partire da aggregati gia' calcolati: nessuna query, solo disegno. Tutte le funzioni sono pure e
/// thread-safe (una SKSurface per chiamata), quindi eseguibili fuori dal thread UI.</summary>
public static partial class ChartRenderer
{
    public readonly record struct VoceGrafico(string Nome, long Valore, SKColor Colore);

    /// <summary>Copertura requisiti nel tipo scelto (BarreVerticali = resa storica).</summary>
    public static byte[] CoperturaPerTipo(TipoGrafico tipo, string titolo, CoverageBucket bucket, double width = 900, double height = 380,
        float scala = 1f, IntestazioneGrafico? intestazione = null, bool sfondoTrasparente = false)
    {
        if (tipo == TipoGrafico.BarreVerticali) return BarreCopertura(titolo, bucket, width, height, scala, intestazione, sfondoTrasparente);
        var voci = new[]
        {
            new VoceGrafico("Conforme", bucket.Conformi, ColConforme),
            new VoceGrafico("Con deviazioni", bucket.ConformiConDeviazioni, ColDeviazioni),
            new VoceGrafico("Non conforme", bucket.NonConformi, ColNonConforme),
            new VoceGrafico("Non testato", bucket.NonTestati, ColNonTestato),
        };
        return Categorico(tipo == TipoGrafico.Ciambella ? TipoGrafico.Torta : tipo, titolo, voci, width, height, scala, intestazione, sfondoTrasparente);
    }

    /// <summary>Distribuzione per sottoveicolo nel tipo scelto. Le voci oltre la soglia del tipo sono
    /// raggruppate in "Altri" (vedi <see cref="HomeChartCatalog.RaggruppaAltri"/>): il costo di rendering
    /// resta limitato anche con 1000+ categorie.</summary>
    public static byte[] SottoveicoloPerTipo(TipoGrafico tipo, string titolo, IReadOnlyList<(string Nome, long Valore)> perSottoveicolo,
        double width = 900, double height = 480, float scala = 1f, IntestazioneGrafico? intestazione = null, bool sfondoTrasparente = false)
    {
        var max = tipo == TipoGrafico.Treemap ? HomeChartCatalog.MaxVociTreemap : HomeChartCatalog.MaxVociBarreTorta;
        var raggruppate = HomeChartCatalog.RaggruppaAltri(perSottoveicolo, max);
        var voci = raggruppate.Select((v, i) => new VoceGrafico(v.Nome, v.Valore,
            v.Nome == HomeChartCatalog.EtichettaAltri && i == raggruppate.Count - 1 && raggruppate.Count > max
                ? ColNonTestato
                : PaletteSottoveicolo[i % PaletteSottoveicolo.Length])).ToList();
        return Categorico(tipo == TipoGrafico.Ciambella ? TipoGrafico.Torta : tipo, titolo, voci, width, height, scala, intestazione, sfondoTrasparente);
    }

    public static byte[] Categorico(TipoGrafico tipo, string titolo, IReadOnlyList<VoceGrafico> vociIn, double width, double height,
        float scala = 1f, IntestazioneGrafico? intestazione = null, bool sfondoTrasparente = false)
    {
        var voci = vociIn.Where(v => v.Valore > 0).ToList();
        return RendiPng(new DimensioniGrafico(width, height, scala), (canvas, w, h) =>
        {
            var inizio = DisegnaIntestazione(canvas, titolo, intestazione, w);
            using var fontTesto = CreaFont(13);
            using var paintTesto = new SKPaint { Color = ColTesto, IsAntialias = true };

            if (voci.Count == 0)
            {
                canvas.DrawText("Nessun dato disponibile nel filtro selezionato.", Margine + 6, inizio + 20, SKTextAlign.Left, fontTesto, paintTesto);
                return;
            }

            var totale = Math.Max(1L, voci.Sum(v => v.Valore));
            switch (tipo)
            {
                case TipoGrafico.BarreOrizzontali: DisegnaBarreOrizzontali(canvas, voci, totale, w, h, inizio, fontTesto, paintTesto); break;
                case TipoGrafico.Treemap: DisegnaTreemap(canvas, voci, totale, w, h, inizio, fontTesto); break;
                default: DisegnaTorta(canvas, voci, totale, w, h, inizio, fontTesto, paintTesto); break;
            }
        }, sfondoTrasparente);
    }

    private static string Tronca(string testo, int max) => testo.Length <= max ? testo : testo[..(max - 1)] + "…";

    private static void DisegnaBarreOrizzontali(SKCanvas canvas, List<VoceGrafico> voci, long totale, float larghezza, float altezza,
        float inizio, SKFont font, SKPaint paintTesto)
    {
        var etichette = voci.Select(v => Tronca(v.Nome, 24)).ToList();
        var etichettaMax = Math.Min(160f, etichette.Max(e => font.MeasureText(e)));
        var sinistra = etichettaMax + 18f;
        const float destra = 110f;
        var max = Math.Max(1L, voci.Max(v => v.Valore));
        var passo = Math.Min(34f, (altezza - inizio - 16f) / voci.Count);
        var spessore = passo * 0.68f;
        var larghezzaUtile = larghezza - sinistra - destra;
        using var paintBarra = new SKPaint { IsAntialias = true };
        for (int i = 0; i < voci.Count; i++)
        {
            var v = voci[i];
            var y = inizio + 8f + i * passo;
            var w = Math.Max(2f, larghezzaUtile * (float)(v.Valore / (double)max));
            paintBarra.Color = v.Colore;
            canvas.DrawRoundRect(new SKRect(sinistra, y, sinistra + w, y + spessore), 4, 4, paintBarra);
            canvas.DrawText(TroncaAPixel(font, etichette[i], sinistra - 10), sinistra - 8, y + spessore - 4, SKTextAlign.Right, font, paintTesto);
            var testoValore = $"{v.Valore} ({v.Valore * 100.0 / totale:0.#}%)";
            canvas.DrawText(TroncaAPixel(font, testoValore, larghezza - (sinistra + w + 6) - 6), sinistra + w + 6, y + spessore - 4, SKTextAlign.Left, font, paintTesto);
        }
    }

    private static void DisegnaTorta(SKCanvas canvas, List<VoceGrafico> voci, long totale, float larghezza, float altezza,
        float inizio, SKFont font, SKPaint paintTesto)
    {
        var disposizione = DisponiLegenda(larghezza, altezza, inizio,
            voci.Select(v => new VoceLegenda(EtichettaVoce(v.Nome, v.Valore, totale, 28), v.Colore)).ToList(),
            font, corpoMinimoDestra: Math.Max(180f, 0.8f * (altezza - inizio - 12f)));
        var corpo = disposizione.Corpo;
        var raggio = Math.Max(4f, Math.Min(corpo.Width, corpo.Height) / 2f - 6f);
        var cx = corpo.MidX; var cy = corpo.MidY;
        var rect = new SKRect(cx - raggio, cy - raggio, cx + raggio, cy + raggio);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        var angolo = -90f;
        foreach (var v in voci)
        {
            var sweep = (float)(360.0 * v.Valore / totale);
            paint.Color = v.Colore;
            canvas.DrawArc(rect, angolo, sweep, true, paint);
            angolo += sweep;
        }
        DisegnaLegenda(canvas, disposizione, font, paintTesto);
    }

    private static void DisegnaTreemap(SKCanvas canvas, List<VoceGrafico> voci, long totale, float larghezza, float altezza,
        float inizio, SKFont font)
    {
        var ordinate = voci.OrderByDescending(v => v.Valore).ToList();
        var area = new SKRect(Margine, inizio + 4f, larghezza - Margine, altezza - 12f);
        var rettangoli = LayoutTreemap(ordinate.Select(v => v.Valore).ToList(), area);
        using var paintFill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var paintBordo = new SKPaint { Color = ColSfondo, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        using var paintTestoBianco = new SKPaint { Color = SKColors.White, IsAntialias = true };
        for (int i = 0; i < ordinate.Count; i++)
        {
            var r = rettangoli[i];
            paintFill.Color = ordinate[i].Colore;
            canvas.DrawRect(r, paintFill);
            canvas.DrawRect(r, paintBordo);
            if (r.Width < 70 || r.Height < 40) continue;
            canvas.Save();
            canvas.ClipRect(SKRect.Inflate(r, -4, -2));
            canvas.DrawText(TroncaAPixel(font, ordinate[i].Nome, r.Width - 16), r.Left + 8, r.Top + 18, SKTextAlign.Left, font, paintTestoBianco);
            canvas.DrawText(TroncaAPixel(font, $"{ordinate[i].Valore} ({ordinate[i].Valore * 100.0 / totale:0.#}%)", r.Width - 16),
                r.Left + 8, r.Top + 36, SKTextAlign.Left, font, paintTestoBianco);
            canvas.Restore();
        }
    }

    /// <summary>Treemap "squarified" (Bruls et al.): <paramref name="valoriDecrescenti"/> (tutti &gt; 0, ordinati
    /// in modo decrescente) -> rettangoli che riempiono esattamente <paramref name="area"/>, con area
    /// proporzionale al valore. Pubblico per essere verificabile da unit test.</summary>
    public static SKRect[] LayoutTreemap(IReadOnlyList<long> valoriDecrescenti, SKRect area)
    {
        var risultato = new SKRect[valoriDecrescenti.Count];
        // Valori <= 0 ignorati (rettangolo vuoto): evitano divisioni per zero (Infinity/NaN) nel rapporto d'aspetto.
        var indici = new List<int>(valoriDecrescenti.Count);
        double somma = 0;
        for (int k = 0; k < valoriDecrescenti.Count; k++)
            if (valoriDecrescenti[k] > 0) { indici.Add(k); somma += valoriDecrescenti[k]; }
        if (indici.Count == 0 || somma <= 0 || area.Width <= 0 || area.Height <= 0) return risultato;
        if (indici.Count != valoriDecrescenti.Count)
        {
            var compatti = LayoutTreemap(indici.Select(k => valoriDecrescenti[k]).ToList(), area);
            for (int k = 0; k < indici.Count; k++) risultato[indici[k]] = compatti[k];
            return risultato;
        }

        var scala = (double)area.Width * area.Height / somma;
        var aree = new double[valoriDecrescenti.Count];
        for (int k = 0; k < aree.Length; k++) aree[k] = valoriDecrescenti[k] * scala;

        double x = area.Left, y = area.Top, w = area.Width, h = area.Height;
        int i = 0;
        while (i < aree.Length)
        {
            var lato = Math.Min(w, h);
            int j = i; double sommaRiga = 0; double peggiore = double.MaxValue;
            while (j < aree.Length)
            {
                var nuovaSomma = sommaRiga + aree[j];
                var rmax = aree[i]; var rmin = aree[j];
                var peggioreNuovo = Math.Max(lato * lato * rmax / (nuovaSomma * nuovaSomma), nuovaSomma * nuovaSomma / (lato * lato * rmin));
                if (j > i && peggioreNuovo > peggiore) break;
                sommaRiga = nuovaSomma; peggiore = peggioreNuovo; j++;
            }

            if (w >= h)
            {
                var larghezzaRiga = sommaRiga / h;
                var cy = y;
                for (int k = i; k < j; k++)
                {
                    var altezza = aree[k] / larghezzaRiga;
                    risultato[k] = new SKRect((float)x, (float)cy, (float)(x + larghezzaRiga), (float)(cy + altezza));
                    cy += altezza;
                }
                x += larghezzaRiga; w -= larghezzaRiga;
            }
            else
            {
                var altezzaRiga = sommaRiga / w;
                var cx = x;
                for (int k = i; k < j; k++)
                {
                    var larghezza = aree[k] / altezzaRiga;
                    risultato[k] = new SKRect((float)cx, (float)y, (float)(cx + larghezza), (float)(y + altezzaRiga));
                    cx += larghezza;
                }
                y += altezzaRiga; h -= altezzaRiga;
            }
            i = j;
        }
        return risultato;
    }
}
