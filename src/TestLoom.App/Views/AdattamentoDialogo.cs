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
        var larghezzaMassima = LarghezzaMassima(area.Width, scala);
        var altezzaMassima = AltezzaMassima(area.Height, scala);
        if (larghezzaMassima > dialogo.MinWidth) dialogo.MaxWidth = larghezzaMassima;
        if (altezzaMassima > dialogo.MinHeight) dialogo.MaxHeight = altezzaMassima;

        if (grande && LayoutAdattivo.ModoCorrente <= ModoLayout.Compatto)
        {
            dialogo.WindowState = WindowState.Maximized;
            return;
        }
        var larghezza = dialogo.Bounds.Width;
        var altezza = dialogo.Bounds.Height;
        if (larghezza <= 0 || altezza <= 0) return;
        if (larghezza > larghezzaMassima && larghezzaMassima > dialogo.MinWidth) dialogo.Width = larghezzaMassima;
        if (altezza > altezzaMassima && altezzaMassima > dialogo.MinHeight) dialogo.Height = altezzaMassima;
        dialogo.Position = Centra(area, scala, Math.Min(larghezza, larghezzaMassima), Math.Min(altezza, altezzaMassima));
    }

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
