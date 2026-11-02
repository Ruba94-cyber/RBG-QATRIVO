using Avalonia.Media.Imaging;

namespace TestLoom.App.ViewModels;

/// <summary>Decodifica dei PNG dei grafici in bitmap Avalonia. Un PNG non decodificabile (o un host senza piattaforma
/// grafica, ad es. i test unitari puri) restituisce null invece di eccezioni.</summary>
internal static class GraficoBitmap
{
    internal static Bitmap? Decodifica(byte[] png)
    {
        try { using var ms = new MemoryStream(png); return new Bitmap(ms); }
        catch { return null; }
    }
}
