using Avalonia;
using Avalonia.Media;

namespace TestLoom.App.Views;

/// <summary>Colori semantici per i controlli costruiti nel code-behind: seguono il tema chiaro/scuro invece di un colore fisso
/// (Firebrick su sfondo scuro ha poco contrasto). Ricadono sul colore storico se la risorsa non è disponibile.</summary>
internal static class UiTema
{
    public static IBrush Errore => Risolvi("DangerBrush", Brushes.Firebrick);
    public static IBrush Separatore => Risolvi("BorderStrongBrush", Brushes.Gray);

    private static IBrush Risolvi(string chiave, IBrush fallback)
    {
        var app = Application.Current;
        object? r = null;
        return app != null && app.Resources.TryGetResource(chiave, app.ActualThemeVariant, out r) && r is IBrush b ? b : fallback;
    }
}
