using Avalonia.Media;
using TestLoom.App.Models;
using TestLoom.App.Services;
using SkiaSharp;

namespace TestLoom.App.ViewModels;

/// <summary>Voce accessibile della legenda completa espansa sotto il grafico.</summary>
public sealed record VoceLegendaCompletaGrafico(string Testo, IBrush? Colore, bool Intestazione);

internal static class GraficoLegendaCompleta
{
    public static IReadOnlyList<VoceLegendaCompletaGrafico> Crea(DatiGrafico? dati, TipoGrafico tipo)
    {
        if (tipo != TipoGrafico.Torta || dati is not DatiEsiti esiti)
            return Array.Empty<VoceLegendaCompletaGrafico>();

        var modelli = ChartRenderer.PreparaVeicoliEsiti(esiti.PerVeicoloEsito, HomeChartCatalog.MaxVociBarreTorta);
        var archi = ChartRenderer.PianificaTortaEsiti(modelli);
        return ChartRenderer.VociLegendaTortaEsiti(archi)
            .Select(voce => new VoceLegendaCompletaGrafico(voce.Testo,
                CreaBrush(voce.Intestazione ? null : voce.Colore), voce.Intestazione))
            .ToArray();
    }

    private static IBrush? CreaBrush(SKColor? colore)
    {
        if (colore is not { } c) return null;
        return new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));
    }
}
