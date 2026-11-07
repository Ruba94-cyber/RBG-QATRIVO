using TestLoom.App.Models;
using SkiaSharp;

namespace TestLoom.App.Services;

/// <summary>Disegna grafici reali (non segnaposto) come PNG, riusabili sia nell'export Excel (immagine
/// incorporata) sia nel PDF (§7.6: "almeno un paio di grafici chiave" dentro il file generato), sia nelle card e
/// nella finestra grafico della Home.
/// <para>
/// Rendering parametrico: ogni funzione riceve larghezza e altezza in DIP e una scala (pixel per DIP). L'area del
/// grafico e la legenda si calcolano dalle dimensioni (legenda a destra se c'e' spazio, sotto altrimenti) e tutti
/// i testi restano almeno a <see cref="FontMinimoDip"/> DIP.
/// </para>
/// </summary>
public static partial class ChartRenderer
{
    private static readonly SKColor ColConforme = new(56, 178, 138);
    private static readonly SKColor ColDeviazioni = new(224, 168, 46);
    private static readonly SKColor ColNonConforme = new(214, 69, 69);
    private static readonly SKColor ColNonTestato = new(148, 163, 184);
    private static readonly SKColor ColDaRivedere = new(37, 99, 235);
    /// <summary>Tema scuro per il render a schermo (per thread: impostato da <see cref="GraficoRenderer.Render"/>).
    /// Le esportazioni (PNG, report) non lo impostano e restano sempre inchiostro scuro su bianco.</summary>
    [ThreadStatic] internal static bool TemaScuro;
    private static SKColor ColAssi => TemaScuro ? new SKColor(148, 163, 184) : new SKColor(71, 85, 105);
    private static SKColor ColTesto => TemaScuro ? new SKColor(226, 232, 240) : new SKColor(30, 41, 59);
    /// <summary>Colore dello sfondo, usato anche per i separatori tra segmenti.</summary>
    private static SKColor ColSfondo => TemaScuro ? new SKColor(20, 27, 35) : SKColors.White;

    /// <summary>Tavolozza per l'anello esterno (Sottoveicolo): il numero di sottoveicoli è dinamico e non ha
    /// un significato semantico fisso (a differenza dell'Esito), quindi basta un ciclo di colori distinti.</summary>
    private static readonly SKColor[] PaletteSottoveicolo =
    {
        new(37, 99, 235), new(217, 119, 6), new(5, 150, 105), new(219, 39, 119),
        new(124, 58, 237), new(8, 145, 178), new(202, 138, 4), new(220, 38, 38),
        new(75, 85, 99), new(13, 148, 136),
        new(234, 88, 12), new(101, 163, 13), new(136, 19, 55),
    };

    public static SKColor ColoreEsito(Models.Esito esito) => esito switch
    {
        Models.Esito.Conforme => ColConforme,
        Models.Esito.ConformeConDeviazioni => ColDeviazioni,
        Models.Esito.NonConforme => ColNonConforme,
        Models.Esito.DaRivedere => ColDaRivedere,
        _ => ColNonTestato,
    };

    public static byte[] BarreCopertura(string titolo, CoverageBucket bucket, double width = 720, double height = 380,
        float scala = 1f, IntestazioneGrafico? intestazione = null, bool sfondoTrasparente = false) =>
        RendiPng(new DimensioniGrafico(width, height, scala), (canvas, w, h) =>
        {
            var inizio = DisegnaIntestazione(canvas, titolo, intestazione, w);
            var dati = new (string Nome, long Valore, SKColor Colore)[]
            {
                ("Conforme", bucket.Conformi, ColConforme),
                ("Con deviazioni", bucket.ConformiConDeviazioni, ColDeviazioni),
                ("Non conforme", bucket.NonConformi, ColNonConforme),
                ("Non testato", bucket.NonTestati, ColNonTestato),
            };

            var max = Math.Max(1L, dati.Max(d => d.Valore));
            var totale = Math.Max(1L, dati.Sum(d => d.Valore));
            // Le categorie sono scritte sotto le barre: non serve una legenda separata.
            var cima = inizio + 26f; var base_ = h - 30f;
            var sinistra = Margine + 30f; var destra = w - Margine;
            var areaBarra = (destra - sinistra) / dati.Length;
            var larghezzaBarra = areaBarra * 0.5f;

            using var fontValore = CreaFont(13);
            using var fontCategoria = CreaFont(13);
            using var paintTesto = new SKPaint { Color = ColTesto, IsAntialias = true };
            using var paintAsse = new SKPaint { Color = ColAssi, StrokeWidth = 1.5f, IsAntialias = true };
            canvas.DrawLine(sinistra, cima, sinistra, base_, paintAsse);
            canvas.DrawLine(sinistra, base_, destra, base_, paintAsse);

            for (int i = 0; i < dati.Length; i++)
            {
                var (nome, valore, colore) = dati[i];
                var x = sinistra + i * areaBarra + (areaBarra - larghezzaBarra) / 2;
                var altezzaBarra = (base_ - cima) * (float)(valore / (double)max);
                var y = base_ - altezzaBarra;

                using var paintBarra = new SKPaint { Color = colore, IsAntialias = true };
                canvas.DrawRoundRect(new SKRect(x, y, x + larghezzaBarra, base_), 6, 6, paintBarra);

                var pct = valore * 100.0 / totale;
                canvas.DrawText(TroncaAPixel(fontValore, $"{valore} ({pct:0.#}%)", areaBarra - 4), x + larghezzaBarra / 2, y - 6, SKTextAlign.Center, fontValore, paintTesto);
                canvas.DrawText(TroncaAPixel(fontCategoria, nome, areaBarra - 4), x + larghezzaBarra / 2, base_ + 19, SKTextAlign.Center, fontCategoria, paintTesto);
            }
        }, sfondoTrasparente);

    public static byte[] AndamentoNonConformita(string titolo, IReadOnlyList<(DateTime Data, int Aperte, int Chiuse)> punti,
        double width = 720, double height = 380, bool area = false, float scala = 1f, IntestazioneGrafico? intestazione = null, bool sfondoTrasparente = false) =>
        RendiPng(new DimensioniGrafico(width, height, scala), (canvas, w, h) =>
        {
            var inizio = DisegnaIntestazione(canvas, titolo, intestazione, w);
            using var fontTesto = CreaFont(FontMinimoDip);
            using var paintTesto = new SKPaint { Color = ColTesto, IsAntialias = true };
            using var paintAsse = new SKPaint { Color = ColAssi, StrokeWidth = 1.5f, IsAntialias = true };

            var voci = new List<VoceLegenda> { new("Aperte", ColNonConforme), new("Chiuse", ColConforme) };
            var disposizione = DisponiLegenda(w, h, inizio, voci, fontTesto, corpoMinimoDestra: 220);
            var corpo = disposizione.Corpo;
            var topGrafico = corpo.Top + 8; var baseGrafico = corpo.Bottom - 24;
            var sinistra = corpo.Left + 8; var destra = corpo.Right - 8;
            canvas.DrawLine(sinistra, topGrafico, sinistra, baseGrafico, paintAsse);
            canvas.DrawLine(sinistra, baseGrafico, destra, baseGrafico, paintAsse);

            if (punti.Count == 0)
            {
                canvas.DrawText(TroncaAPixel(fontTesto, "Nessun dato disponibile nel periodo selezionato.", corpo.Width - 20),
                    sinistra + 10, (topGrafico + baseGrafico) / 2, SKTextAlign.Left, fontTesto, paintTesto);
            }
            else
            {
                var maxY = Math.Max(1, punti.Max(p => Math.Max(p.Aperte, p.Chiuse)));
                var passoX = punti.Count > 1 ? (destra - sinistra) / (punti.Count - 1) : 0;

                void DisegnaLinea(Func<(DateTime, int Aperte, int Chiuse), int> selettore, SKColor colore)
                {
                    using var paint = new SKPaint { Color = colore, StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke };
                    using var path = new SKPath();
                    for (int i = 0; i < punti.Count; i++)
                    {
                        var x = sinistra + i * passoX;
                        var y = baseGrafico - (baseGrafico - topGrafico) * (selettore(punti[i]) / (float)maxY);
                        if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
                    }
                    if (area && punti.Count > 1)
                    {
                        // Variante "area" (tipo grafico selezionabile in Home): stessa serie, riempimento traslucido
                        // fino all'asse X sotto la linea.
                        using var fill = new SKPath(path);
                        fill.LineTo(sinistra + (punti.Count - 1) * passoX, baseGrafico);
                        fill.LineTo(sinistra, baseGrafico);
                        fill.Close();
                        using var paintFill = new SKPaint { Color = colore.WithAlpha(70), IsAntialias = true, Style = SKPaintStyle.Fill };
                        canvas.DrawPath(fill, paintFill);
                    }
                    canvas.DrawPath(path, paint);
                }

                DisegnaLinea(p => p.Aperte, ColNonConforme);
                DisegnaLinea(p => p.Chiuse, ColConforme);

                canvas.DrawText(punti[0].Data.ToString("dd/MM/yy"), sinistra, baseGrafico + 19, SKTextAlign.Left, fontTesto, paintTesto);
                canvas.DrawText(punti[^1].Data.ToString("dd/MM/yy"), destra, baseGrafico + 19, SKTextAlign.Right, fontTesto, paintTesto);
            }

            DisegnaLegenda(canvas, disposizione, fontTesto, paintTesto);
        }, sfondoTrasparente);

    /// <summary>Compatibilità per i caller storici che hanno già aggregati int. Il percorso scalabile usa
    /// direttamente l'overload long sottostante, senza cast o saturazioni.</summary>
    public static byte[] DonutEsitiPerSottoveicolo(string titolo,
        IReadOnlyList<(string Nome, int Valore)> perSottoveicolo,
        IReadOnlyList<(Models.Esito Esito, int Valore)> perEsito,
        double width = 640, double height = 440, float scala = 1f, IntestazioneGrafico? intestazione = null, bool sfondoTrasparente = false) =>
        DonutEsitiPerSottoveicolo(
            titolo,
            perSottoveicolo.Select(x => (x.Nome, (long)x.Valore)).ToList(),
            perEsito.Select(x => (x.Esito, (long)x.Valore)).ToList(),
            width,
            height,
            scala,
            intestazione,
            sfondoTrasparente);

    /// <summary>Ciambella a 2 livelli (Addendum §18): anello esterno = distribuzione per Sottoveicolo, anello
    /// interno = distribuzione per Esito, sullo stesso insieme di test filtrati. I due anelli sono indipendenti
    /// (non annidati/sunburst): ciascuno mostra la propria scomposizione, non una gerarchia allineata segmento
    /// per segmento — più leggibile e sufficiente a "vedere insieme" volumi per veicolo ed esiti.</summary>
    public static byte[] DonutEsitiPerSottoveicolo(string titolo,
        IReadOnlyList<(string Nome, long Valore)> perSottoveicolo,
        IReadOnlyList<(Models.Esito Esito, long Valore)> perEsito,
        double width = 640, double height = 440, float scala = 1f, IntestazioneGrafico? intestazione = null, bool sfondoTrasparente = false) =>
        RendiPng(new DimensioniGrafico(width, height, scala), (canvas, w, h) =>
        {
            var inizio = DisegnaIntestazione(canvas, titolo, intestazione, w);
            using var fontTesto = CreaFont(FontMinimoDip);
            using var paintTesto = new SKPaint { Color = ColTesto, IsAntialias = true };

            var esternoCompleto = perSottoveicolo.Where(s => s.Valore > 0).ToList();
            var interno = perEsito.Where(e => e.Valore > 0).ToList();
            var totaleInterno = Math.Max(1L, interno.Sum(e => e.Valore));

            // Anello esterno limitato alle prime 8 voci (già ordinate per valore decrescente dal chiamante) più un
            // aggregato "Altri": con Sottoveicolo a scala 1000+ (Addendum prestazioni 2026-09-20) disegnare un
            // DrawArc antialiasing per ciascuna delle 1000+ fette, quasi tutte invisibili spicchi sub-pixel, era il
            // costo dominante di apertura Home — la legenda mostrava già solo le prime 8, quindi l'anello stesso
            // non comunicava nulla in più oltre quella soglia, solo tempo di rendering.
            const int MaxSegmentiSottoveicolo = 8;
            var esterno = esternoCompleto.Take(MaxSegmentiSottoveicolo)
                .Select((s, i) => (s.Nome, s.Valore, Colore: PaletteSottoveicolo[i % PaletteSottoveicolo.Length])).ToList();
            var altriValore = esternoCompleto.Skip(MaxSegmentiSottoveicolo).Sum(s => s.Valore);
            if (altriValore > 0) esterno.Add(("Altri", altriValore, ColNonTestato));
            var totaleEsterno = Math.Max(1L, esterno.Sum(s => s.Valore));

            // Legenda: gruppo Veicolo (anello esterno) poi gruppo Esito (anello interno), come nella resa storica.
            var voci = new List<VoceLegenda>();
            if (esterno.Count > 0)
            {
                voci.Add(new(LocalizationService.Instance.Traduci("GraficoVeicoloLabel"), null, true));
                voci.AddRange(esterno.Select(s => new VoceLegenda(EtichettaVoce(s.Nome, s.Valore, totaleEsterno, 28), s.Colore)));
            }
            if (interno.Count > 0)
            {
                voci.Add(new(LocalizationService.Instance.Traduci("GraficoEsitoLabel"), null, true));
                voci.AddRange(interno.Select(e => new VoceLegenda(EtichettaVoce(e.Esito.Label(), e.Valore, totaleInterno, 40), ColoreEsito(e.Esito))));
            }
            var disposizione = DisponiLegenda(w, h, inizio, voci, fontTesto, corpoMinimoDestra: Math.Max(180f, 0.8f * (h - inizio - MargineBasso)));
            var corpo = disposizione.Corpo;

            if (esterno.Count == 0 && interno.Count == 0)
            {
                canvas.DrawText(TroncaAPixel(fontTesto, "Nessun dato disponibile nel filtro selezionato.", corpo.Width - 10),
                    corpo.Left + 6, corpo.MidY, SKTextAlign.Left, fontTesto, paintTesto);
            }
            else
            {
                var raggioDisponibile = Math.Min(corpo.Width, corpo.Height) / 2f - 6f;
                if (raggioDisponibile > 12f)
                {
                    // Spessore proporzionale al raggio, con un intervallo di 7 DIP tra i due anelli.
                    var spessore = Math.Clamp(raggioDisponibile * 0.26f, 10f, 38f);
                    var raggioEsterno = raggioDisponibile - spessore / 2f;
                    var raggioInterno = raggioEsterno - (spessore + 7f);
                    var cx = corpo.MidX; var cy = corpo.MidY;
                    DisegnaAnello(canvas, cx, cy, raggioEsterno, spessore, esterno.Select(s => (s.Valore, s.Colore)).ToList());
                    if (raggioInterno - spessore / 2f > 4f)
                        DisegnaAnello(canvas, cx, cy, raggioInterno, spessore, interno.Select(e => (e.Valore, ColoreEsito(e.Esito))).ToList());
                }
            }

            DisegnaLegenda(canvas, disposizione, fontTesto, paintTesto);
        }, sfondoTrasparente);

    private static void DisegnaAnello(SKCanvas canvas, float cx, float cy, float raggio, float spessore, IReadOnlyList<(long Valore, SKColor Colore)> segmenti)
    {
        var totale = Math.Max(1L, segmenti.Sum(s => s.Valore));
        var rect = new SKRect(cx - raggio, cy - raggio, cx + raggio, cy + raggio);
        var angolo = -90f;
        // Un solo SKPaint riusato per tutti i segmenti (solo il colore cambia a ogni iterazione), non uno per
        // segmento: con centinaia/migliaia di categorie distinte (Sottoveicolo a scala 1000+, Addendum
        // prestazioni 2026-09-20) allocare e disporre un SKPaint nativo per ognuna dominava il tempo di
        // rendering del grafico a ogni apertura Home — stesso identico output visivo, solo il costo cambia.
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = spessore };
        foreach (var (valore, colore) in segmenti)
        {
            var sweep = (float)(360.0 * valore / totale);
            paint.Color = colore;
            canvas.DrawArc(rect, angolo, Math.Max(sweep - 1.5f, 0), false, paint);
            angolo += sweep;
        }
    }
}
