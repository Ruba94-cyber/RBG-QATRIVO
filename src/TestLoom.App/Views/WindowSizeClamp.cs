using Avalonia.Controls;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>Limita le dimensioni predefinite di una finestra all'area di lavoro dello schermo che la contiene, senza
/// mai scendere sotto MinWidth/MinHeight. Le finestre con dimensioni fisse (es. 1400x860) su 1024x700 o 1366x768
/// uscivano dallo schermo; qui si riducono solo quando serve, quindi su schermi grandi la dimensione predefinita
/// resta invariata.
///
/// Le dimensioni di <see cref="Window.Width"/>/<see cref="Window.Height"/> sono DIP, mentre <c>WorkingArea</c> e' in
/// pixel fisici: la conversione usa <c>Screen.Scaling</c>. Il margine sottrae lo spazio della cornice/barra del titolo
/// che Width/Height non includono.</summary>
public static class WindowSizeClamp
{
    /// <summary>Spazio in DIP riservato a cornice e barra del titolo, per lato, oltre l'area di lavoro.</summary>
    public const double MargineCornice = 48;

    /// <summary>Calcola una dimensione (larghezza o altezza) in DIP. Funzione pura, testabile senza finestre.</summary>
    /// <param name="richiesta">Dimensione predefinita della finestra in DIP.</param>
    /// <param name="minimo">MinWidth/MinHeight in DIP: il risultato non scende mai sotto questo valore.</param>
    /// <param name="areaPixel">Dimensione dell'area di lavoro dello schermo in pixel fisici.</param>
    /// <param name="scala">Fattore di scala dello schermo (1.0, 1.25, 1.5...).</param>
    /// <param name="margine">Spazio in DIP da lasciare per la cornice.</param>
    public static double Dimensione(double richiesta, double minimo, int areaPixel, double scala, double margine = MargineCornice)
    {
        if (double.IsNaN(richiesta) || richiesta <= 0) return richiesta;
        var s = scala > 0 ? scala : 1;
        var massimo = areaPixel / s - margine;
        var minimoValido = Math.Max(0, double.IsNaN(minimo) ? 0 : minimo);
        return Math.Max(minimoValido, Math.Min(richiesta, massimo));
    }

    /// <summary>Applica il limite alla finestra alla prima apertura, usando lo schermo che la contiene (o il primario).</summary>
    public static void Applica(Window finestra)
    {
        finestra.Opened += (_, _) =>
        {
            try
            {
                var schermo = finestra.Screens.ScreenFromWindow(finestra) ?? finestra.Screens.Primary;
                if (schermo == null) return;
                var area = schermo.WorkingArea;
                var larghezza = finestra.Width > 0 ? finestra.Width : finestra.Bounds.Width;
                var altezza = finestra.Height > 0 ? finestra.Height : finestra.Bounds.Height;
                if (larghezza > 0)
                    finestra.Width = Dimensione(larghezza, finestra.MinWidth, area.Width, schermo.Scaling);
                if (altezza > 0)
                    finestra.Height = Dimensione(altezza, finestra.MinHeight, area.Height, schermo.Scaling);
            }
            catch (Exception ex)
            {
                // Il ridimensionamento e' un'ottimizzazione di comodita': un errore non deve impedire di mostrare la finestra.
                CrashLogService.Registra("Limite dimensioni finestra allo schermo", ex);
            }
        };
    }
}
