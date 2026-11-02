using System.Drawing;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Helpers;

/// <summary>
/// Contenuto reale di un'immagine UIA: cattura dell'elemento e campionamento dei pixel (uno ogni <see cref="Passo"/>).
/// Serve a distinguere un grafico disegnato da una zona vuota o bianca: il controllo non si ferma alla presenza
/// dell'elemento. Condiviso dai test di render dei grafici della Home e dalla finestra "Apri" del grafico.
/// </summary>
public static class ImmaginePixel
{
    /// <summary>Passo di campionamento in pixel della cattura.</summary>
    public const int Passo = 3;

    /// <summary>Misura di una cattura: rettangolo dell'elemento, colori distinti campionati e pixel non bianchi.</summary>
    public readonly record struct Misura(Rectangle Rettangolo, int Colori, int NonBianchi)
    {
        public override string ToString() =>
            $"rettangolo {Rettangolo.Width}x{Rettangolo.Height} a ({Rettangolo.Left},{Rettangolo.Top}), " +
            $"colori distinti {Colori}, pixel non bianchi {NonBianchi}";
    }

    /// <summary>Misura l'elemento immagine. Un elemento senza area (0 px) ha misura zero, senza eccezione.</summary>
    public static Misura Di(AutomationElement img)
    {
        var rett = img.BoundingRectangle;
        if (rett.Width <= 0 || rett.Height <= 0) return new Misura(rett, 0, 0);
        using var bmp = img.Capture();
        var colori = new HashSet<int>();
        var nonBianchi = 0;
        for (var y = 0; y < bmp.Height; y += Passo)
            for (var x = 0; x < bmp.Width; x += Passo)
            {
                var pixel = bmp.GetPixel(x, y);
                colori.Add(pixel.ToArgb());
                if (pixel.R < 250 || pixel.G < 250 || pixel.B < 250) nonBianchi++;
            }
        return new Misura(rett, colori.Count, nonBianchi);
    }
}
