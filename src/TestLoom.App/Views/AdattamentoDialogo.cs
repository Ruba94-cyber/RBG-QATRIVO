using Avalonia;
using Avalonia.Controls;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>Unico punto per le finestre secondarie (6.4.7 per le piccole, 6.6.1 per tutte). Una finestra registrata riceve
/// le classi del modo di layout corrente (Window.compatto / Window.minimo valgono anche per i dialoghi) e, all'apertura,
/// viene limitata all'area di lavoro dello schermo che la contiene meno <see cref="Margine"/> DIP per lato, poi centrata.
/// Le finestre "grandi" (editor, diff) in modo compatto o minimo si aprono massimizzate. Tutte le finestre restano
/// ridimensionabili, comprese quelle di dialogo e ConfirmDialog.</summary>
public static class AdattamentoDialogo
{
    /// <summary>Margine in DIP tra il dialogo e i bordi dell'area di lavoro.</summary>
    public const double Margine = 48;

    /// <summary>Registra una finestra secondaria. <paramref name="grande"/> = editor o diff che in modo compatto o minimo
    /// si aprono massimizzati invece che di dimensione fissa.</summary>
    public static void Registra(Window dialogo, bool grande = false)
    {
        // Every standard modal dialog uses this central helper. Log its routed UI
        // interactions without duplicating handlers already installed by a caller.
        UiInteractionTraceService.Attach(dialogo, "Dialog." + dialogo.GetType().Name);
        foreach (var classe in LayoutAdattivo.Classi(LayoutAdattivo.ModoCorrente)) dialogo.Classes.Add(classe);
        dialogo.Opened += (_, _) => Adatta(dialogo, grande);
    }

    private static void Adatta(Window dialogo, bool grande)
    {
        var schermo = dialogo.Screens.ScreenFromWindow(dialogo) ?? dialogo.Screens.Primary;
        if (schermo == null) return;
        var area = schermo.WorkingArea;
        var scala = schermo.Scaling;
        var larghezzaMassima = Math.Max(1, LarghezzaMassima(area.Width, scala));
        var altezzaMassima = Math.Max(1, AltezzaMassima(area.Height, scala));

        // Un MinWidth/MinHeight del dialogo maggiore del viewport impediva ad Avalonia di
        // applicare MaxWidth/MaxHeight: sui portatili 13" i pulsanti della cornice (inclusa X)
        // finivano fuori dall'area visibile. Il limite fisico dello schermo ha precedenza.
        dialogo.MinWidth = MinimoCompatibile(dialogo.MinWidth, larghezzaMassima);
        dialogo.MinHeight = MinimoCompatibile(dialogo.MinHeight, altezzaMassima);
        dialogo.MaxWidth = larghezzaMassima;
        dialogo.MaxHeight = altezzaMassima;

        // Gli editor / dashboard grandi usano tutto il viewport anche quando il modo
        // adattivo e' rimasto Normale (es. 13" con scaling elevato).
        var larghezzaRichiesta = dialogo.Width > 0 ? dialogo.Width : dialogo.Bounds.Width;
        var altezzaRichiesta = dialogo.Height > 0 ? dialogo.Height : dialogo.Bounds.Height;
        if (grande && (LayoutAdattivo.ModoCorrente <= ModoLayout.Compatto ||
                       larghezzaRichiesta > larghezzaMassima || altezzaRichiesta > altezzaMassima))
        {
            dialogo.WindowState = WindowState.Maximized;
            return;
        }
        if (larghezzaRichiesta <= 0 || altezzaRichiesta <= 0) return;
        if (larghezzaRichiesta > larghezzaMassima) dialogo.Width = larghezzaMassima;
        if (altezzaRichiesta > altezzaMassima) dialogo.Height = altezzaMassima;
        dialogo.Position = Centra(area, scala,
            Math.Min(larghezzaRichiesta, larghezzaMassima), Math.Min(altezzaRichiesta, altezzaMassima));
    }

    /// <summary>La dimensione minima desiderata non puo' impedire il contenimento nello schermo.</summary>
    internal static double MinimoCompatibile(double minimoRichiesto, double massimoDisponibile) =>
        Math.Min(Math.Max(0, minimoRichiesto), Math.Max(1, massimoDisponibile));

    /// <summary>Altezza logica massima di un dialogo: area di lavoro in pixel fisici, convertita in DIP, meno il margine.</summary>
    internal static double AltezzaMassima(int altezzaAreaLavoroPixel, double scala) => DaPixelADip(altezzaAreaLavoroPixel, scala) - Margine;

    /// <summary>Larghezza logica massima di un dialogo: area di lavoro in pixel fisici, convertita in DIP, meno il margine.</summary>
    internal static double LarghezzaMassima(int larghezzaAreaLavoroPixel, double scala) => DaPixelADip(larghezzaAreaLavoroPixel, scala) - Margine;

    /// <summary>Posizione in pixel fisici che centra una finestra di dimensioni logiche date nell'area di lavoro.</summary>
    internal static PixelPoint Centra(PixelRect area, double scala, double larghezza, double altezza)
    {
        var s = scala > 0 ? scala : 1;
        var x = area.X + (int)Math.Max(0, (area.Width - larghezza * s) / 2);
        var y = area.Y + (int)Math.Max(0, (area.Height - altezza * s) / 2);
        return new PixelPoint(x, y);
    }

    private static double DaPixelADip(int pixel, double scala) => scala > 0 ? pixel / scala : pixel;
}
