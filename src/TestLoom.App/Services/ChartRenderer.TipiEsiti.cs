using System.Globalization;
using TestLoom.App.Models;
using SkiaSharp;

namespace TestLoom.App.Services;

/// <summary>Grafici "Esiti per Veicolo" nei tipi non a ciambella: l'esito resta visibile in ogni
/// rappresentazione (barre impilate, torta a spicchi per esito aggregato, treemap a fasce). Funzioni pure e thread-safe;
/// il costo e' limitato dal raggruppamento "Altri" (vedi <see cref="HomeChartCatalog"/>) anche con 1000+ veicoli.
/// Legenda a destra se c'e' spazio, sotto il grafico altrimenti; i valori sui segmenti compaiono solo se entrano.</summary>
public static partial class ChartRenderer
{
    /// <summary>Ordine fisso di impilamento/legenda degli esiti (semantico: dal migliore al peggiore, poi i neutri).</summary>
    private static readonly Esito[] OrdineEsiti =
    {
        Esito.Conforme, Esito.ConformeConDeviazioni, Esito.NonConforme, Esito.DaRivedere,
        Esito.NonApplicabile, Esito.DaEseguire, Esito.NonEseguitoNA,
    };

    private static int IndiceEsito(Esito e)
    {
        var i = Array.IndexOf(OrdineEsiti, e);
        return i >= 0 ? i : OrdineEsiti.Length - 1;
    }

    internal sealed record RigaVeicolo(string Nome, long[] PerEsito)
    {
        public long Totale => PerEsito.Sum();
    }

    internal sealed record ArcoStatoTorta(Esito Esito, long Valore, double Inizio, double Ampiezza, double PercentualeVeicolo);
    internal sealed record ArcoVeicoloTorta(string Nome, long Valore, SKColor Colore, double Inizio, double Ampiezza,
        IReadOnlyList<ArcoStatoTorta> Stati);
    internal sealed record SpicchioEsitoTorta(Esito Esito, long Valore, double Inizio, double Ampiezza, double PercentualeTotale);

    /// <summary>Aggrega le righe (veicolo, esito, n) per veicolo (nome case-insensitive), ordina per totale
    /// decrescente e oltre <paramref name="max"/> somma gli esiti del resto in "Altri". Valori &lt;= 0 scartati.</summary>
    internal static List<RigaVeicolo> PreparaVeicoliEsiti(IEnumerable<(string Nome, Esito Esito, long Valore)> righe, int max)
    {
        var mappa = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (nome, esito, valore) in righe)
        {
            if (valore <= 0) continue;
            var chiave = string.IsNullOrWhiteSpace(nome) ? "—" : nome;
            if (!mappa.TryGetValue(chiave, out var arr)) mappa[chiave] = arr = new long[OrdineEsiti.Length];
            arr[IndiceEsito(esito)] += valore;
        }
        var ordinate = mappa.Select(kv => new RigaVeicolo(kv.Key, kv.Value))
            .OrderByDescending(r => r.Totale).ThenBy(r => r.Nome, StringComparer.OrdinalIgnoreCase).ToList();
        if (ordinate.Count <= max) return ordinate;
        var testa = ordinate.Take(max).ToList();
        var altri = new long[OrdineEsiti.Length];
        foreach (var r in ordinate.Skip(max))
            for (int k = 0; k < altri.Length; k++) altri[k] += r.PerEsito[k];
        testa.Add(new RigaVeicolo(HomeChartCatalog.EtichettaAltri, altri));
        return testa;
    }

    /// <summary>Distribuzione degli esiti per veicolo nel tipo scelto (esclusa la ciambella, che ha il proprio
    /// disegno): BarreOrizzontali/BarreVerticali impilate per esito, Torta piena con uno spicchio per esito aggregato,
    /// Treemap con ogni tessera suddivisa in fasce per esito. Linea/Area: una serie per esito
    /// con i veicoli sull'asse X (Area: serie impilate).</summary>
    public static byte[] SottoveicoloEsitiPerTipo(TipoGrafico tipo, string titolo,
        IReadOnlyList<(string Nome, Esito Esito, long Valore)> righe, double width = 900, double height = 480,
        float scala = 1f, IntestazioneGrafico? intestazione = null, bool sfondoTrasparente = false)
    {
        var max = tipo == TipoGrafico.Treemap ? HomeChartCatalog.MaxVociTreemap : HomeChartCatalog.MaxVociBarreTorta;
        var veicoli = PreparaVeicoliEsiti(righe, max);

        return RendiPng(new DimensioniGrafico(width, height, scala), (canvas, w, h) =>
        {
            var inizio = DisegnaIntestazione(canvas, titolo, intestazione, w);
            using var fontTesto = CreaFont(FontMinimoDip);
            using var paintTesto = new SKPaint { Color = ColTesto, IsAntialias = true };

            if (veicoli.Count == 0)
            {
                canvas.DrawText("Nessun dato disponibile nel filtro selezionato.", Margine + 6, inizio + 20, SKTextAlign.Left, fontTesto, paintTesto);
                return;
            }

            switch (tipo)
            {
                case TipoGrafico.BarreOrizzontali: DisegnaBarreEsitiOrizzontali(canvas, veicoli, w, h, inizio, fontTesto, paintTesto); break;
                case TipoGrafico.Treemap: DisegnaTreemapEsiti(canvas, veicoli, w, h, inizio, fontTesto, paintTesto); break;
                case TipoGrafico.Torta: DisegnaTortaEsiti(canvas, veicoli, w, h, inizio, fontTesto, paintTesto, tortaPiena: true); break;
                case TipoGrafico.Ciambella: DisegnaTortaEsiti(canvas, veicoli, w, h, inizio, fontTesto, paintTesto, tortaPiena: false); break;
                case TipoGrafico.Linea:
                case TipoGrafico.Area: DisegnaSerieEsiti(canvas, veicoli, tipo == TipoGrafico.Area, w, h, inizio, fontTesto, paintTesto); break;
                default: DisegnaBarreEsitiVerticali(canvas, veicoli, w, h, inizio, fontTesto, paintTesto); break;
            }
        }, sfondoTrasparente);
    }

    private static SKColor ColoreTestoSu(SKColor sfondo) =>
        (0.299 * sfondo.Red + 0.587 * sfondo.Green + 0.114 * sfondo.Blue) > 150 ? ColTesto : SKColors.White;

    private static long[] TotaliPerEsito(IReadOnlyList<RigaVeicolo> veicoli)
    {
        var t = new long[OrdineEsiti.Length];
        foreach (var v in veicoli) for (int k = 0; k < t.Length; k++) t[k] += v.PerEsito[k];
        return t;
    }

    /// <summary>Voci di legenda degli esiti con almeno un valore (Esito + conteggio, colore dell'esito).</summary>
    private static List<VoceLegenda> VociLegendaEsiti(long[] totaliEsito)
    {
        var voci = new List<VoceLegenda>();
        for (int k = 0; k < OrdineEsiti.Length; k++)
            if (totaliEsito[k] > 0) voci.Add(new VoceLegenda($"{OrdineEsiti[k].Label()} ({totaliEsito[k]})", ColoreEsito(OrdineEsiti[k])));
        return voci;
    }

    /// <summary>Pianifica gli spicchi della torta piena aggregando ogni esito su tutti i veicoli.</summary>
    internal static List<SpicchioEsitoTorta> PianificaSpicchiTortaEsiti(IReadOnlyList<RigaVeicolo> veicoli)
    {
        var totali = TotaliPerEsito(veicoli);
        var totale = totali.Sum();
        if (totale <= 0) return new();

        var risultati = new List<SpicchioEsitoTorta>();
        var angolo = -90d;
        for (var k = 0; k < OrdineEsiti.Length; k++)
        {
            var valore = totali[k];
            if (valore <= 0) continue;
            var ampiezza = 360d * valore / totale;
            risultati.Add(new SpicchioEsitoTorta(OrdineEsiti[k], valore, angolo, ampiezza,
                100d * valore / totale));
            angolo += ampiezza;
        }
        return risultati;
    }

    /// <summary>Legenda visibile della torta: una voce per ogni esito che compare nel grafico.</summary>
    internal static List<VoceLegenda> VociLegendaSpicchiTortaEsiti(IReadOnlyList<SpicchioEsitoTorta> spicchi) =>
        spicchi.Select(s => new VoceLegenda(
            string.Format(CultureInfo.CurrentCulture, "{0} ({1}, {2:0.#}%)", s.Esito.Label(), s.Valore, s.PercentualeTotale),
            ColoreEsito(s.Esito))).ToList();

    /// <summary>Pianifica gli archi annidati (veicolo → esito) sullo stesso totale. I figli condividono gli angoli
    /// del rispettivo settore padre; le ampiezze e le percentuali sono esposte per verificarle senza rasterizzare il PNG.</summary>
    internal static List<ArcoVeicoloTorta> PianificaTortaEsiti(IReadOnlyList<RigaVeicolo> veicoli)
    {
        var totaleGlobale = veicoli.Sum(v => v.Totale);
        if (totaleGlobale <= 0) return new();

        var risultati = new List<ArcoVeicoloTorta>(veicoli.Count);
        var angoloVeicolo = -90d;
        for (var i = 0; i < veicoli.Count; i++)
        {
            var veicolo = veicoli[i];
            if (veicolo.Totale <= 0) continue;
            var ampiezzaVeicolo = 360d * veicolo.Totale / totaleGlobale;
            var coloreVeicolo = veicolo.Nome == HomeChartCatalog.EtichettaAltri && i == veicoli.Count - 1 && veicoli.Count > HomeChartCatalog.MaxVociBarreTorta
                ? ColNonTestato
                : PaletteSottoveicolo[1 + i % (PaletteSottoveicolo.Length - 1)]; // salta il blu (= esito Da rivedere)
            var stati = new List<ArcoStatoTorta>();
            var angoloStato = angoloVeicolo;
            for (var k = 0; k < OrdineEsiti.Length; k++)
            {
                var valore = veicolo.PerEsito[k];
                if (valore <= 0) continue;
                var ampiezzaStato = ampiezzaVeicolo * valore / veicolo.Totale;
                stati.Add(new ArcoStatoTorta(OrdineEsiti[k], valore, angoloStato, ampiezzaStato,
                    100d * valore / veicolo.Totale));
                angoloStato += ampiezzaStato;
            }
            risultati.Add(new ArcoVeicoloTorta(veicolo.Nome, veicolo.Totale, coloreVeicolo,
                angoloVeicolo, ampiezzaVeicolo, stati));
            angoloVeicolo += ampiezzaVeicolo;
        }
        return risultati;
    }

    /// <summary>Legenda annidata: il colore del titolo identifica il settore veicolo, mentre ogni riga figlia
    /// riporta esplicitamente veicolo, esito, conteggio e percentuale calcolata sul totale di quel veicolo.</summary>
    internal static List<VoceLegenda> VociLegendaTortaEsiti(IReadOnlyList<ArcoVeicoloTorta> veicoli)
    {
        if (veicoli.Count == 0) return new();
        var loc = LocalizationService.Instance;
        var totaleGlobale = veicoli.Sum(v => v.Valore);
        var voci = new List<VoceLegenda>
        {
            new(loc.Traduci("GraficoEsitiVeicoloLegenda"), null, true),
        };
        foreach (var veicolo in veicoli)
        {
            voci.Add(new VoceLegenda(EtichettaVoce(veicolo.Nome, veicolo.Valore, totaleGlobale, 28), veicolo.Colore, true));
            foreach (var stato in veicolo.Stati)
            {
                var etichetta = string.Format(CultureInfo.CurrentCulture,
                    loc.Traduci("GraficoEsitiVeicoloVoceFmt"), veicolo.Nome, stato.Esito.Label(), stato.Valore, stato.PercentualeVeicolo);
                voci.Add(new VoceLegenda(etichetta, ColoreEsito(stato.Esito)));
            }
        }
        return voci;
    }

    private static void DisegnaBarreEsitiOrizzontali(SKCanvas canvas, List<RigaVeicolo> veicoli, float larghezza, float altezza,
        float inizio, SKFont font, SKPaint paintTesto)
    {
        var disposizione = DisponiLegenda(larghezza, altezza, inizio, VociLegendaEsiti(TotaliPerEsito(veicoli)), font, corpoMinimoDestra: 260f);
        DisegnaLegenda(canvas, disposizione, font, paintTesto);
        var corpo = disposizione.Corpo;

        var etichette = veicoli.Select(v => Tronca(v.Nome, 24)).ToList();
        var etichettaMax = Math.Min(160f, etichette.Max(e => font.MeasureText(e)));
        var sinistra = corpo.Left + etichettaMax + 14f;
        var destra = corpo.Right - 56f;
        var max = Math.Max(1L, veicoli.Max(v => v.Totale));
        var passo = Math.Min(34f, (corpo.Height - 10f) / veicoli.Count);
        var spessore = passo * 0.72f;
        var larghezzaUtile = destra - sinistra;
        using var paintSeg = new SKPaint { IsAntialias = true };
        using var paintTestoSeg = new SKPaint { IsAntialias = true };
        for (int i = 0; i < veicoli.Count; i++)
        {
            var v = veicoli[i];
            var y = corpo.Top + 4f + i * passo;
            var x = sinistra;
            for (int k = 0; k < OrdineEsiti.Length; k++)
            {
                if (v.PerEsito[k] <= 0) continue;
                var w = Math.Max(1.5f, larghezzaUtile * (float)(v.PerEsito[k] / (double)max));
                paintSeg.Color = ColoreEsito(OrdineEsiti[k]);
                canvas.DrawRect(new SKRect(x, y, x + w, y + spessore), paintSeg);
                var valore = v.PerEsito[k].ToString();
                if (w >= 26f && spessore >= 15f && font.MeasureText(valore) <= w - 6f)
                {
                    paintTestoSeg.Color = ColoreTestoSu(paintSeg.Color);
                    canvas.DrawText(valore, x + w / 2f, y + spessore / 2f + 4f, SKTextAlign.Center, font, paintTestoSeg);
                }
                x += w;
                // separatore sottile tra segmenti adiacenti
                paintSeg.Color = SKColors.White;
                canvas.DrawRect(new SKRect(x - 0.75f, y, x, y + spessore), paintSeg);
            }
            canvas.DrawText(etichette[i], sinistra - 8, y + spessore - 4, SKTextAlign.Right, font, paintTesto);
            canvas.DrawText(v.Totale.ToString(), x + 6, y + spessore - 4, SKTextAlign.Left, font, paintTesto);
        }
    }

    private static void DisegnaBarreEsitiVerticali(SKCanvas canvas, List<RigaVeicolo> veicoli, float larghezza, float altezza,
        float inizio, SKFont font, SKPaint paintTesto)
    {
        var disposizione = DisponiLegenda(larghezza, altezza, inizio, VociLegendaEsiti(TotaliPerEsito(veicoli)), font, corpoMinimoDestra: 260f);
        DisegnaLegenda(canvas, disposizione, font, paintTesto);
        var corpo = disposizione.Corpo;

        var sinistra = corpo.Left + 14f;
        var destra = corpo.Right - 8f;
        var baseY = corpo.Bottom - 24f;
        var alto = corpo.Top + 22f;
        var max = Math.Max(1L, veicoli.Max(v => v.Totale));
        var passo = (destra - sinistra) / veicoli.Count;
        var larghezzaBarra = Math.Min(70f, passo * 0.72f);
        var altezzaUtile = baseY - alto;
        using var paintSeg = new SKPaint { IsAntialias = true };
        using var paintTestoSeg = new SKPaint { IsAntialias = true };
        using var paintAsse = new SKPaint { Color = ColAssi, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawLine(sinistra, baseY, destra, baseY, paintAsse);
        for (int i = 0; i < veicoli.Count; i++)
        {
            var v = veicoli[i];
            var xs = sinistra + i * passo + (passo - larghezzaBarra) / 2f;
            var y = baseY;
            for (int k = 0; k < OrdineEsiti.Length; k++)
            {
                if (v.PerEsito[k] <= 0) continue;
                var h = Math.Max(1.5f, altezzaUtile * (float)(v.PerEsito[k] / (double)max));
                paintSeg.Color = ColoreEsito(OrdineEsiti[k]);
                canvas.DrawRect(new SKRect(xs, y - h, xs + larghezzaBarra, y), paintSeg);
                var valore = v.PerEsito[k].ToString();
                if (h >= 18f && larghezzaBarra >= 26f && font.MeasureText(valore) <= larghezzaBarra - 6f)
                {
                    paintTestoSeg.Color = ColoreTestoSu(paintSeg.Color);
                    canvas.DrawText(valore, xs + larghezzaBarra / 2f, y - h / 2f + 4f, SKTextAlign.Center, font, paintTestoSeg);
                }
                y -= h;
                paintSeg.Color = SKColors.White;
                canvas.DrawRect(new SKRect(xs, y, xs + larghezzaBarra, y + 0.75f), paintSeg);
            }
            canvas.DrawText(v.Totale.ToString(), xs + larghezzaBarra / 2f, y - 5f, SKTextAlign.Center, font, paintTesto);
            canvas.DrawText(TroncaAPixel(font, v.Nome, passo - 4f), xs + larghezzaBarra / 2f, baseY + 18f, SKTextAlign.Center, font, paintTesto);
        }
    }

    private static void DisegnaTortaEsiti(SKCanvas canvas, List<RigaVeicolo> veicoli, float larghezza, float altezza,
        float inizio, SKFont font, SKPaint paintTesto, bool tortaPiena)
    {
        if (tortaPiena)
        {
            var spicchi = PianificaSpicchiTortaEsiti(veicoli);
            var disposizionePiena = DisponiLegenda(larghezza, altezza, inizio,
                VociLegendaSpicchiTortaEsiti(spicchi), font,
                corpoMinimoDestra: Math.Max(180f, 0.8f * (altezza - inizio - 12f)));
            var corpoPieno = disposizionePiena.Corpo;
            var raggio = Math.Min(corpoPieno.Width, corpoPieno.Height) / 2f - 6f;
            if (raggio > 12f)
            {
                var cx = corpoPieno.MidX;
                var cy = corpoPieno.MidY;
                var segmenti = spicchi.Select(s => (s.Inizio, s.Ampiezza, Colore: ColoreEsito(s.Esito))).ToList();
                DisegnaTortaStatiPiena(canvas, cx, cy, raggio, segmenti);
                DisegnaEtichetteSpicchiTorta(canvas, cx, cy, raggio, spicchi, font);
            }
            DisegnaLegenda(canvas, disposizionePiena, font, paintTesto);
            return;
        }

        var archiVeicolo = PianificaTortaEsiti(veicoli);
        var voci = VociLegendaTortaEsiti(archiVeicolo);
        var disposizione = DisponiLegenda(larghezza, altezza, inizio, voci, font, corpoMinimoDestra: Math.Max(180f, 0.8f * (altezza - inizio - 12f)));
        var corpo = disposizione.Corpo;
        var raggioDisponibile = Math.Min(corpo.Width, corpo.Height) / 2f - 6f;
        if (raggioDisponibile > 12f)
        {
            var spessore = Math.Clamp(raggioDisponibile * 0.26f, 10f, 38f);
            var raggioEsterno = raggioDisponibile - spessore / 2f;          // settore esterno = veicolo/modello
            var raggioInterno = raggioEsterno - (spessore + 2f);            // settore interno = esito per veicolo
            var cx = corpo.MidX; var cy = corpo.MidY;
            var raggioEtichetteStato = raggioInterno;

            var stati = archiVeicolo.SelectMany(v => v.Stati.Select(s => (s.Inizio, s.Ampiezza, Colore: ColoreEsito(s.Esito)))).ToList();
            DisegnaAnelloTorta(canvas, cx, cy, raggioInterno, spessore, stati);
            DisegnaAnelloTorta(canvas, cx, cy, raggioEsterno, spessore,
                archiVeicolo.Select(v => (v.Inizio, v.Ampiezza, v.Colore)));
            DisegnaConfiniVeicolo(canvas, cx, cy, raggioInterno - spessore / 2f,
                raggioEsterno + spessore / 2f, archiVeicolo);

            // Ogni conteggio è centrato sul segmento dello stato nell'anello interno della ciambella.
            if (spessore >= 18f && raggioEtichetteStato > 4f)
            {
                using var pv = new SKPaint { IsAntialias = true };
                foreach (var veicolo in archiVeicolo)
                foreach (var stato in veicolo.Stati)
                {
                    if (stato.Ampiezza >= 14.0)
                    {
                        var m = (stato.Inizio + stato.Ampiezza / 2.0) * Math.PI / 180.0;
                        var testo = stato.Valore.ToString(CultureInfo.CurrentCulture);
                        var ampiezzaRad = stato.Ampiezza * Math.PI / 180.0;
                        var larghezzaDisponibile = Math.Min(raggioEtichetteStato * ampiezzaRad, 2f * raggioEtichetteStato);
                        var altezzaDisponibile = spessore - 4f;
                        if (font.MeasureText(testo) + 4f <= larghezzaDisponibile && font.Size <= altezzaDisponibile)
                        {
                            pv.Color = ColoreTestoSu(ColoreEsito(stato.Esito));
                            canvas.DrawText(testo, (float)(cx + raggioEtichetteStato * Math.Cos(m)), (float)(cy + raggioEtichetteStato * Math.Sin(m)) + 4f, SKTextAlign.Center, font, pv);
                        }
                    }
                }
            }
        }

        DisegnaLegenda(canvas, disposizione, font, paintTesto);
    }

    private static void DisegnaEtichetteSpicchiTorta(SKCanvas canvas, float cx, float cy, float raggio,
        IReadOnlyList<SpicchioEsitoTorta> spicchi, SKFont font)
    {
        if (raggio < 18f) return;
        var raggioEtichetta = raggio * 0.62f;
        using var paint = new SKPaint { IsAntialias = true };
        foreach (var spicchio in spicchi)
        {
            if (spicchio.Ampiezza < 14d) continue;
            var angolo = (spicchio.Inizio + spicchio.Ampiezza / 2d) * Math.PI / 180d;
            var testo = spicchio.Valore.ToString(CultureInfo.CurrentCulture);
            var larghezzaDisponibile = Math.Min(raggioEtichetta * spicchio.Ampiezza * Math.PI / 180d,
                2f * raggioEtichetta);
            if (font.MeasureText(testo) + 4f > larghezzaDisponibile || font.Size > raggio * 0.7f) continue;
            paint.Color = ColoreTestoSu(ColoreEsito(spicchio.Esito));
            canvas.DrawText(testo,
                (float)(cx + raggioEtichetta * Math.Cos(angolo)),
                (float)(cy + raggioEtichetta * Math.Sin(angolo)) + 4f,
                SKTextAlign.Center, font, paint);
        }
    }

    private static void DisegnaTortaStatiPiena(SKCanvas canvas, float cx, float cy, float raggio,
        IReadOnlyList<(double Inizio, double Ampiezza, SKColor Colore)> segmenti)
    {
        if (segmenti.Count == 0) return;
        if (segmenti.Count == 1)
        {
            using var paintSingolo = new SKPaint { Color = segmenti[0].Colore, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawCircle(cx, cy, raggio, paintSingolo);
            return;
        }

        var rect = new SKRect(cx - raggio, cy - raggio, cx + raggio, cy + raggio);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        foreach (var segmento in segmenti)
        {
            paint.Color = segmento.Colore;
            canvas.DrawArc(rect, (float)segmento.Inizio, (float)segmento.Ampiezza, true, paint);
        }

        // Separatori sottili migliorano la leggibilita senza sottrarre un angolo fisso a ogni spicchio:
        // il margine fisso cancellava gli esiti con ampiezza inferiore a 1,5 gradi.
        using var separatore = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
        };
        for (var i = 0; i < segmenti.Count; i++)
        {
            var corrente = segmenti[i];
            var precedente = segmenti[(i + segmenti.Count - 1) % segmenti.Count];
            // Evita che le linee di separazione coprano spicchi molto stretti.
            if (corrente.Ampiezza < 2d || precedente.Ampiezza < 2d) continue;
            var angolo = corrente.Inizio * Math.PI / 180d;
            canvas.DrawLine(cx, cy,
                (float)(cx + raggio * Math.Cos(angolo)),
                (float)(cy + raggio * Math.Sin(angolo)), separatore);
        }
    }

    private static void DisegnaAnelloTorta(SKCanvas canvas, float cx, float cy, float raggio, float spessore,
        IEnumerable<(double Inizio, double Ampiezza, SKColor Colore)> segmenti)
    {
        var rect = new SKRect(cx - raggio, cy - raggio, cx + raggio, cy + raggio);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = spessore };
        foreach (var segmento in segmenti)
        {
            paint.Color = segmento.Colore;
            canvas.DrawArc(rect, (float)segmento.Inizio, Math.Max((float)segmento.Ampiezza - 1.5f, 0f), false, paint);
        }
    }

    private static void DisegnaConfiniVeicolo(SKCanvas canvas, float cx, float cy, float raggioInizio, float raggioFine,
        IReadOnlyList<ArcoVeicoloTorta> veicoli)
    {
        if (veicoli.Count < 2) return;
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        foreach (var veicolo in veicoli)
        {
            var angolo = veicolo.Inizio * Math.PI / 180.0;
            var x1 = (float)(cx + raggioInizio * Math.Cos(angolo));
            var y1 = (float)(cy + raggioInizio * Math.Sin(angolo));
            var x2 = (float)(cx + raggioFine * Math.Cos(angolo));
            var y2 = (float)(cy + raggioFine * Math.Sin(angolo));
            canvas.DrawLine(x1, y1, x2, y2, paint);
        }
    }

    private static void DisegnaTreemapEsiti(SKCanvas canvas, List<RigaVeicolo> veicoli, float larghezza, float altezza,
        float inizio, SKFont font, SKPaint paintTesto)
    {
        var disposizione = DisponiLegenda(larghezza, altezza, inizio, VociLegendaEsiti(TotaliPerEsito(veicoli)), font, corpoMinimoDestra: 260f);
        DisegnaLegenda(canvas, disposizione, font, paintTesto);
        var corpo = disposizione.Corpo;
        var area = new SKRect(corpo.Left, corpo.Top + 2f, corpo.Right, corpo.Bottom);
        var rettangoli = LayoutTreemap(veicoli.Select(v => v.Totale).ToList(), area);
        using var paintFill = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        using var paintBordo = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        using var paintPillola = new SKPaint { Color = new SKColor(15, 23, 42, 215), IsAntialias = true };
        using var paintBianco = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var paintVal = new SKPaint { IsAntialias = true };
        for (int i = 0; i < veicoli.Count; i++)
        {
            var r = rettangoli[i];
            var v = veicoli[i];
            if (r.Width <= 0 || r.Height <= 0) continue;
            // fasce proporzionali agli esiti, lungo il lato maggiore della tessera
            var orizzontale = r.Width >= r.Height;
            var pos = orizzontale ? r.Left : r.Top;
            var totale = (double)v.Totale;
            for (int k = 0; k < OrdineEsiti.Length; k++)
            {
                if (v.PerEsito[k] <= 0) continue;
                var quota = (float)((orizzontale ? r.Width : r.Height) * v.PerEsito[k] / totale);
                var fascia = orizzontale
                    ? new SKRect(pos, r.Top, pos + quota, r.Bottom)
                    : new SKRect(r.Left, pos, r.Right, pos + quota);
                paintFill.Color = ColoreEsito(OrdineEsiti[k]);
                canvas.DrawRect(fascia, paintFill);
                var spazio = orizzontale ? fascia.Width : fascia.Height;
                var testo = v.PerEsito[k].ToString();
                if (spazio >= 22f && r.Height >= 24f && r.Width >= 40f && font.MeasureText(testo) <= spazio - 4f)
                {
                    paintVal.Color = ColoreTestoSu(paintFill.Color);
                    var tx = orizzontale ? fascia.MidX : fascia.Right - 5f;
                    var ty = orizzontale ? r.Bottom - 6f : fascia.MidY + 4f;
                    canvas.DrawText(testo, tx, ty, orizzontale ? SKTextAlign.Center : SKTextAlign.Right, font, paintVal);
                }
                pos += quota;
            }
            canvas.DrawRect(r, paintBordo);
            if (r.Width < 60 || r.Height < 22) continue;
            var etichetta = TroncaAPixel(font, $"{v.Nome} · {v.Totale}", r.Width - 24f);
            var larg = Math.Min(r.Width - 8, font.MeasureText(etichetta) + 10);
            canvas.DrawRoundRect(new SKRect(r.Left + 4, r.Top + 4, r.Left + 4 + larg, r.Top + 22), 3, 3, paintPillola);
            canvas.Save();
            canvas.ClipRect(new SKRect(r.Left + 4, r.Top + 4, r.Left + 4 + larg, r.Top + 22));
            canvas.DrawText(etichetta, r.Left + 9, r.Top + 18, SKTextAlign.Left, font, paintBianco);
            canvas.Restore();
        }
    }

    /// <summary>Linea: una polilinea per esito (valore per veicolo). Area: aree impilate per esito.</summary>
    private static void DisegnaSerieEsiti(SKCanvas canvas, List<RigaVeicolo> veicoli, bool area, float larghezza, float altezza,
        float inizio, SKFont font, SKPaint paintTesto)
    {
        var disposizione = DisponiLegenda(larghezza, altezza, inizio, VociLegendaEsiti(TotaliPerEsito(veicoli)), font, corpoMinimoDestra: 260f);
        DisegnaLegenda(canvas, disposizione, font, paintTesto);
        var corpo = disposizione.Corpo;

        var sinistra = corpo.Left + 44f;
        var destra = corpo.Right - 16f;
        var alto = corpo.Top + 14f;
        var baseY = corpo.Bottom - 26f;
        var n = veicoli.Count;
        var max = Math.Max(1L, area ? veicoli.Max(v => v.Totale) : veicoli.Max(v => v.PerEsito.Max()));
        var altezzaUtile = Math.Max(1f, baseY - alto);
        float X(int i) => n == 1 ? (sinistra + destra) / 2f : sinistra + 12f + i * (destra - sinistra - 24f) / (n - 1);
        float Y(double v) => baseY - altezzaUtile * (float)(v / max);
        using var paintAsse = new SKPaint { Color = ColAssi, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawLine(sinistra, baseY, destra, baseY, paintAsse);
        canvas.DrawLine(sinistra, alto, sinistra, baseY, paintAsse);
        canvas.DrawText(max.ToString(), sinistra - 6, alto + 4, SKTextAlign.Right, font, paintTesto);
        canvas.DrawText("0", sinistra - 6, baseY + 4, SKTextAlign.Right, font, paintTesto);
        var passo = (destra - sinistra) / n;
        for (int i = 0; i < n; i++)
            canvas.DrawText(TroncaAPixel(font, veicoli[i].Nome, passo - 4f), X(i), baseY + 18f, SKTextAlign.Center, font, paintTesto);

        using var linea = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, StrokeJoin = SKStrokeJoin.Round };
        using var riempi = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var punto = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        var cumulato = new double[n];
        for (int k = 0; k < OrdineEsiti.Length; k++)
        {
            if (veicoli.All(v => v.PerEsito[k] <= 0)) continue;
            var colore = ColoreEsito(OrdineEsiti[k]);
            var sotto = (double[])cumulato.Clone();
            for (int i = 0; i < n; i++) cumulato[i] += veicoli[i].PerEsito[k];
            var valori = area ? cumulato : veicoli.Select(v => (double)v.PerEsito[k]).ToArray();
            using var path = new SKPath();
            for (int i = 0; i < n; i++) { if (i == 0) path.MoveTo(X(i), Y(valori[i])); else path.LineTo(X(i), Y(valori[i])); }
            if (area)
            {
                using var chiusa = new SKPath(path);
                for (int i = n - 1; i >= 0; i--) chiusa.LineTo(X(i), Y(sotto[i]));
                chiusa.Close();
                riempi.Color = colore;
                canvas.DrawPath(chiusa, riempi);
            }
            linea.Color = area ? SKColors.White : colore;
            linea.StrokeWidth = area ? 1f : 2.5f;
            canvas.DrawPath(path, linea);
            if (!area)
            {
                punto.Color = colore;
                for (int i = 0; i < n; i++)
                    if (veicoli[i].PerEsito[k] > 0) canvas.DrawCircle(X(i), Y(valori[i]), 3.5f, punto);
            }
        }
    }
}
