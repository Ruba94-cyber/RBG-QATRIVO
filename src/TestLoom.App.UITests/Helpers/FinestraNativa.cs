using System.Runtime.InteropServices;
using TestLoom.App.UITests.Infrastructure;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Helpers;

/// <summary>
/// Dimensionamento di una finestra TestLoom alle misure logiche volute (per esempio lo schermo G2 1280x776) tramite
/// API Win32 native. Le misure vengono convertite con la scala DPI della finestra (200% sul PC G2), come nei test
/// di layout su schermo piccolo.
/// </summary>
public static class FinestraNativa
{
    private const int SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>Attiva la consapevolezza DPI del processo di test (idempotente): le misure native sono in pixel fisici.</summary>
    public static void AbilitaDpiAware() => SetProcessDPIAware();

    /// <summary>Rettangolo nativo (Win32, pixel fisici) della finestra, per la diagnostica dei test di dimensione.</summary>
    public static System.Drawing.Rectangle RettangoloNativo(Window finestra)
    {
        var hwnd = finestra.FrameworkAutomationElement.NativeWindowHandle;
        if (hwnd == IntPtr.Zero) return System.Drawing.Rectangle.Empty;
        GetWindowRect(hwnd, out var r);
        return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>Porta la finestra fuori dallo stato massimizzato e la dimensiona a <paramref name="larghezza"/>x<paramref name="altezza"/>
    /// logici; ritorna quando il rettangolo nativo raggiunge la misura richiesta.</summary>
    public static void RidimensionaLogici(Window finestra, int larghezza, int altezza)
    {
        var hwnd = finestra.FrameworkAutomationElement.NativeWindowHandle;
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("handle nativo della finestra non disponibile");
        ShowWindow(hwnd, SwRestore);
        Wait.ForCondition(() => !IsZoomed(hwnd), "la finestra esce dallo stato massimizzato", TimeSpan.FromSeconds(10));
        var scala = GetDpiForWindow(hwnd) / 96.0;
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, (int)Math.Round(larghezza * scala), (int)Math.Round(altezza * scala), SwpNoZOrder | SwpNoActivate);
        Wait.ForCondition(() =>
        {
            GetWindowRect(hwnd, out var r);
            return Math.Abs((r.Right - r.Left) - larghezza * scala) < 3 * scala &&
                   Math.Abs((r.Bottom - r.Top) - altezza * scala) < 3 * scala;
        }, $"la finestra raggiunge {larghezza}x{altezza} logici", TimeSpan.FromSeconds(15));
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
