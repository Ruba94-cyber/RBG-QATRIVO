using SkiaSharp;

namespace TestLoom.App.Services;

/// <summary>Dimensioni di un rendering di grafico: larghezza e altezza in DIP (unita' logiche, come nei controlli
/// Avalonia) e scala (pixel per DIP). Il disegno avviene sempre in DIP: a qualunque scala un carattere da 12 DIP
/// resta da 12 DIP logici (24 pixel a scala 2), quindi la leggibilita' non dipende dalla risoluzione di resa.</summary>
public readonly record struct DimensioniGrafico(double Larghezza, double Altezza, double Scala = 1)
{
    public int PixelLarghezza => Math.Max(1, (int)Math.Round(Larghezza * SicuraScala));
    public int PixelAltezza => Math.Max(1, (int)Math.Round(Altezza * SicuraScala));
    internal double SicuraScala => Scala > 0 ? Scala : 1;
}

/// <summary>Righe di intestazione degli export (filtri attivi e data/ora di generazione). Assente nelle card e nella
/// finestra grafico, dove titolo e filtri sono gia' mostrati dalla pagina o dalla barra del titolo.</summary>
public sealed record IntestazioneGrafico(string Filtri, string DataOra);

/// <summary>Voce della legenda: testo, colore del campione (null = solo testo) e, se <see cref="Intestazione"/>, titolo di
/// gruppo senza campione (es. "Veicolo" sopra i suoi valori).</summary>
internal readonly record struct VoceLegenda(string Testo, SKColor? Colore, bool Intestazione = false);

/// <summary>Disposizione calcolata della legenda: area residua per il grafico, area della legenda, numero di colonne e
/// voci effettivamente visibili (con una voce "+N altre" se troncate).</summary>
internal sealed record DisposizioneLegenda(SKRect Corpo, SKRect Legenda, bool ADestra, int Colonne, int RigheColonna, IReadOnlyList<VoceLegenda> Voci);

/// <summary>Motore comune dei grafici: canvas in DIP, intestazione, legenda a destra se c'e' spazio (altrimenti sotto) e
/// troncamento dei testi misurati sui pixel reali. Ogni chart disegna nelle aree restituite qui, non in coordinate fisse.</summary>
public static partial class ChartRenderer
{
    /// <summary>Carattere minimo (DIP) per etichette, valori e legende: nessun testo dei grafici scende sotto questa soglia.</summary>
    public const float FontMinimoDip = 12f;

    private const float Margine = 14f;
    private const float MargineBasso = 12f;
    private const float AltezzaRiga = 18f;
    private const float SpazioLegenda = 18f;
    private const float LarghezzaCampione = 12f;
    private const float LarghezzaColonnaMax = 260f;

    // Il tipo di carattere si risolve una sola volta: FromFamilyName interroga il gestore dei font di sistema a ogni chiamata, e
    // CreaFont ne chiamava una per ogni scritta di ogni grafico. SKTypeface e' immutabile e condivisibile; ogni SKFont resta nuovo.
    private static readonly Lazy<SKTypeface> CarattereRegolareCondiviso = new(() => SKTypeface.FromFamilyName("Segoe UI"));
    private static readonly Lazy<SKTypeface> CarattereGrassettoCondiviso = new(() => SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold));

    internal static SKTypeface CaratteRegolare() => CarattereRegolareCondiviso.Value;
    internal static SKTypeface CaratteGrassetto() => CarattereGrassettoCondiviso.Value;

    /// <summary>Carattere del grafico: la dimensione richiesta non scende mai sotto <see cref="FontMinimoDip"/>.</summary>
    internal static SKFont CreaFont(float dimensione, bool grassetto = false) =>
        new(grassetto ? CaratteGrassetto() : CaratteRegolare(), Math.Max(dimensione, FontMinimoDip));

    /// <summary>Crea la superficie alla dimensione in pixel, la pulisce di bianco, disegna in DIP e codifica il PNG.</summary>
    internal static byte[] RendiPng(DimensioniGrafico dimensioni, Action<SKCanvas, float, float> disegna, bool sfondoTrasparente = false)
    {
        var larghezza = (float)dimensioni.Larghezza;
        var altezza = (float)dimensioni.Altezza;
        using var surface = SKSurface.Create(new SKImageInfo(dimensioni.PixelLarghezza, dimensioni.PixelAltezza));
        var canvas = surface.Canvas;
        canvas.Clear(sfondoTrasparente ? SKColors.Transparent : ColSfondo);
        // Scala per asse: i pixel arrotondati restano coerenti con le coordinate logiche (differenza < 1 px).
        canvas.Scale(dimensioni.PixelLarghezza / larghezza, dimensioni.PixelAltezza / altezza);
        disegna(canvas, larghezza, altezza);
        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Titolo (e, negli export, filtri attivi e data) in alto. Restituisce la y dove inizia l'area del grafico.</summary>
    internal static float DisegnaIntestazione(SKCanvas canvas, string titolo, IntestazioneGrafico? intestazione, float larghezza)
    {
        using var fontTitolo = CreaFont(20, grassetto: true);
        using var paintTesto = new SKPaint { Color = ColTesto, IsAntialias = true };
        canvas.DrawText(TroncaAPixel(fontTitolo, titolo, larghezza - 40), 20, 30, SKTextAlign.Left, fontTitolo, paintTesto);
        if (intestazione == null) return 46f;
        using var fontFiltri = CreaFont(13);
        using var fontData = CreaFont(FontMinimoDip);
        canvas.DrawText(TroncaAPixel(fontFiltri, intestazione.Filtri, larghezza - 40), 20, 52, SKTextAlign.Left, fontFiltri, paintTesto);
        canvas.DrawText(TroncaAPixel(fontData, intestazione.DataOra, larghezza - 40), 20, 70, SKTextAlign.Left, fontData, paintTesto);
        return 84f;
    }

    /// <summary>Tronca il testo con "…" in modo che la sua larghezza misurata non superi <paramref name="maxPixel"/>.</summary>
    internal static string TroncaAPixel(SKFont font, string testo, float maxPixel)
    {
        if (string.IsNullOrEmpty(testo) || font.MeasureText(testo) <= maxPixel) return testo ?? "";
        const string ellissi = "…";
        var lunghezza = testo.Length;
        while (lunghezza > 1 && font.MeasureText(testo[..lunghezza] + ellissi) > maxPixel) lunghezza--;
        return lunghezza <= 1 ? ellissi : testo[..lunghezza] + ellissi;
    }

    /// <summary>Calcola area del grafico e area della legenda. A destra se, con al massimo tre colonne, la legenda entra
    /// in altezza e lascia al grafico almeno <paramref name="corpoMinimoDestra"/> DIP di larghezza; altrimenti sotto il
    /// grafico, in colonne. Se anche sotto il grafico resterebbe sotto il 55% dell'altezza, la legenda viene troncata con
    /// una voce "+N altre" (la card resta leggibile; la finestra grafico e l'export mostrano tutte le voci).</summary>
    internal static DisposizioneLegenda DisponiLegenda(float larghezza, float altezza, float yInizio, IReadOnlyList<VoceLegenda> voci, SKFont font, float corpoMinimoDestra)
    {
        var area = new SKRect(Margine, yInizio, larghezza - Margine, altezza - MargineBasso);
        if (voci.Count == 0)
            return new DisposizioneLegenda(area, SKRect.Empty, false, 0, 0, Array.Empty<VoceLegenda>());

        var colonnaLarghezza = LarghezzaColonna(voci, font);
        var righeMassimeDestra = Math.Max(1, (int)Math.Floor(area.Height / AltezzaRiga));
        for (var colonne = 1; colonne <= 3; colonne++)
        {
            var righe = (int)Math.Ceiling(voci.Count / (double)colonne);
            if (righe > righeMassimeDestra) continue;
            var legendaLarghezza = colonne * colonnaLarghezza + SpazioLegenda;
            var corpoLarghezza = area.Width - legendaLarghezza;
            if (corpoLarghezza < corpoMinimoDestra) break;
            var corpo = new SKRect(area.Left, area.Top, area.Left + corpoLarghezza, area.Bottom);
            var legenda = new SKRect(corpo.Right + SpazioLegenda, area.Top, area.Right, area.Top + righe * AltezzaRiga);
            return new DisposizioneLegenda(corpo, legenda, true, colonne, righe, voci);
        }

        // Sotto il grafico: in colonne che occupano tutta la larghezza.
        var colonneSotto = Math.Max(1, (int)Math.Floor(area.Width / colonnaLarghezza));
        var righeSotto = (int)Math.Ceiling(voci.Count / (double)colonneSotto);
        var altezzaLegenda = righeSotto * AltezzaRiga;
        var altezzaCorpo = area.Height - altezzaLegenda - SpazioLegenda;
        if (altezzaCorpo >= area.Height * 0.55)
        {
            var corpo = new SKRect(area.Left, area.Top, area.Right, area.Bottom - altezzaLegenda - SpazioLegenda);
            var legenda = new SKRect(area.Left, corpo.Bottom + SpazioLegenda, area.Right, area.Bottom);
            return new DisposizioneLegenda(corpo, legenda, false, colonneSotto, righeSotto, voci);
        }

        // Troncamento: la legenda usa al massimo il 45% dell'altezza, una riga resta per "+N altre".
        var righeConsentite = Math.Max(1, (int)Math.Floor((area.Height * 0.45f - SpazioLegenda) / AltezzaRiga));
        var slot = Math.Max(colonneSotto, righeConsentite * colonneSotto);
        var visibili = voci.Count <= slot ? voci.ToList() : voci.Take(slot - 1).ToList();
        if (visibili.Count < voci.Count)
            visibili.Add(new VoceLegenda(string.Format(LocalizationService.Instance.Traduci("GraficoAltreVociFmt"), voci.Count - visibili.Count), null));
        var righeTroncate = (int)Math.Ceiling(visibili.Count / (double)colonneSotto);
        var altezzaTroncata = righeTroncate * AltezzaRiga;
        var corpoTroncato = new SKRect(area.Left, area.Top, area.Right, area.Bottom - altezzaTroncata - SpazioLegenda);
        var legendaTroncata = new SKRect(area.Left, corpoTroncato.Bottom + SpazioLegenda, area.Right, area.Bottom);
        return new DisposizioneLegenda(corpoTroncato, legendaTroncata, false, colonneSotto, righeTroncate, visibili);
    }

    /// <summary>Disegna le voci della legenda nelle colonne calcolate (riempimento per colonne: i gruppi restano contigui).</summary>
    internal static void DisegnaLegenda(SKCanvas canvas, DisposizioneLegenda disposizione, SKFont font, SKPaint paintTesto)
    {
        if (disposizione.Voci.Count == 0 || disposizione.Legenda.IsEmpty) return;
        var colonnaLarghezza = disposizione.Colonne > 0 ? disposizione.Legenda.Width / disposizione.Colonne : disposizione.Legenda.Width;
        using var campione = new SKPaint { IsAntialias = true };
        using var paintIntestazione = new SKPaint { Color = ColAssi, IsAntialias = true };
        for (var i = 0; i < disposizione.Voci.Count; i++)
        {
            var voce = disposizione.Voci[i];
            var colonna = i / Math.Max(1, disposizione.RigheColonna);
            var riga = i % Math.Max(1, disposizione.RigheColonna);
            var x = disposizione.Legenda.Left + colonna * colonnaLarghezza;
            var y = disposizione.Legenda.Top + riga * AltezzaRiga + 13f;
            var testoX = x;
            if (voce.Colore is { } colore)
            {
                campione.Color = colore;
                canvas.DrawRect(new SKRect(x, y - 10, x + LarghezzaCampione, y + 2), campione);
                testoX = x + LarghezzaCampione + 6;
            }
            var testo = TroncaAPixel(font, voce.Testo, colonnaLarghezza - (testoX - x) - 8);
            canvas.DrawText(testo, testoX, y, SKTextAlign.Left, font, voce.Intestazione ? paintIntestazione : paintTesto);
        }
    }

    private static float LarghezzaColonna(IReadOnlyList<VoceLegenda> voci, SKFont font)
    {
        var massima = 0f;
        foreach (var voce in voci)
            massima = Math.Max(massima, font.MeasureText(voce.Testo));
        var campione = voci.Any(v => v.Colore != null) ? LarghezzaCampione + 6 : 0;
        return Math.Clamp(massima + campione + 16, 120f, LarghezzaColonnaMax);
    }

    /// <summary>Area di disegno (DIP) quando la legenda non e' presente: tutto lo spazio sotto l'intestazione.</summary>
    internal static SKRect AreaSenzaLegenda(float larghezza, float altezza, float yInizio) =>
        new(Margine, yInizio, larghezza - Margine, altezza - MargineBasso);

    /// <summary>Etichetta del campione per le categorie con valore, con percentuale sul totale.</summary>
    internal static string EtichettaVoce(string nome, long valore, long totale, int max) =>
        $"{Tronca(nome, max)} ({valore}, {(totale > 0 ? valore * 100.0 / totale : 0):0.#}%)";
}
