using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Rende uno snapshot di grafico a una dimensione (card, finestra, export, foglio Excel) e prepara l'esportazione
/// PNG ad alta risoluzione: scala >= 2 e larghezza >= 1600 px, intestazione con titolo, filtri attivi e data/ora,
/// metadati PNG (titolo, filtri, data) per chi riapre il file.</summary>
public static class GraficoRenderer
{
    /// <summary>Larghezza minima in pixel dell'export PNG (il grafico e' sempre rigenerato, non ingrandito).</summary>
    public const int LarghezzaMinimaEsportazionePx = 1600;

    /// <summary>Scala minima dell'export: 2 pixel per DIP anche quando la larghezza di partenza supera 800 DIP.</summary>
    public const double ScalaMinimaEsportazione = 2;

    public static byte[] Render(DatiGrafico dati, TipoGrafico tipo, string titolo, DimensioniGrafico dimensioni, IntestazioneGrafico? intestazione = null, bool sfondoTrasparente = false)
    {
        var larghezza = dimensioni.Larghezza;
        var altezza = dimensioni.Altezza;
        var scala = (float)dimensioni.SicuraScala;
        return dati switch
        {
            DatiCopertura copertura => ChartRenderer.CoperturaPerTipo(tipo, titolo, copertura.Bucket, larghezza, altezza, scala, intestazione, sfondoTrasparente),
            DatiAndamento andamento => ChartRenderer.AndamentoNonConformita(titolo, andamento.Punti, larghezza, altezza, tipo == TipoGrafico.Area, scala, intestazione, sfondoTrasparente),
            DatiEsiti esiti when tipo == TipoGrafico.Ciambella =>
                ChartRenderer.DonutEsitiPerSottoveicolo(titolo, esiti.PerSottoveicolo, esiti.PerEsito, larghezza, altezza, scala, intestazione, sfondoTrasparente),
            DatiEsiti esiti =>
                ChartRenderer.SottoveicoloEsitiPerTipo(tipo, titolo, esiti.PerVeicoloEsito, larghezza, altezza, scala, intestazione, sfondoTrasparente),
            _ => throw new ArgumentOutOfRangeException(nameof(dati), dati, "Tipo di dati grafico non supportato."),
        };
    }

    /// <summary>Dimensioni dell'export: stesso layout della card o della finestra, a scala almeno 2 e con almeno
    /// <see cref="LarghezzaMinimaEsportazionePx"/> pixel di larghezza.</summary>
    public static DimensioniGrafico DimensioniEsportazione(double larghezza, double altezza)
    {
        var scala = Math.Max(ScalaMinimaEsportazione, LarghezzaMinimaEsportazionePx / Math.Max(1d, larghezza));
        return new DimensioniGrafico(larghezza, altezza, scala);
    }

    /// <summary>PNG ad alta risoluzione con titolo, filtri attivi e data/ora nell'immagine e nei metadati.</summary>
    public static byte[] EsportaPng(GraficoSnapshot grafico, double larghezza, double altezza, string filtri, DateTime generatoIl)
    {
        var dataOra = FormataDataOra(generatoIl);
        var intestazione = new IntestazioneGrafico(string.IsNullOrWhiteSpace(filtri) ? LocalizationService.Instance.Traduci("GraficoNessunFiltro") : filtri, dataOra);
        var png = Render(grafico.Dati, grafico.Tipo, grafico.Titolo, DimensioniEsportazione(larghezza, altezza), intestazione, sfondoTrasparente: true);
        return PngMetadati.Aggiungi(png,
            ("Title", grafico.Titolo),
            ("Description", intestazione.Filtri),
            ("Creation Time", generatoIl.ToString("yyyy-MM-dd'T'HH:mm:ss")));
    }

    /// <summary>Nome file proposto dal dialogo di salvataggio: data + titolo del grafico (caratteri non validi sostituiti).</summary>
    public static string NomeFileProposto(string titolo, DateTime quando) =>
        $"{quando:yyyy-MM-dd}_{SanificaNome(titolo)}.png";

    public static string FormataDataOra(DateTime quando) => quando.ToString("dd/MM/yyyy HH:mm");

    internal static string SanificaNome(string testo)
    {
        var invalidi = Path.GetInvalidFileNameChars();
        var pulito = new string((testo ?? "").Select(c => invalidi.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(pulito) ? "grafico" : pulito;
    }
}
