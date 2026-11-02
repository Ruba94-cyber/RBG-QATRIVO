using System.Runtime.InteropServices;

namespace TestLoom.App.Services;

/// <summary>
/// Avviso Windows non modale: fa lampeggiare il pulsante della barra applicazioni della finestra principale finché
/// l'operatore non la riporta in primo piano. Fuori da Windows non fa nulla. Un eventuale fallimento viene registrato
/// in CrashLogService e non interrompe mai l'app.
/// </summary>
public static class NotificaTaskbarService
{
    private const uint FlashwCaption = 0x00000001;
    private const uint FlashwTray = 0x00000002;
    private const uint FlashwTimerNoFg = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWInfo
    {
        public uint CbSize;
        public IntPtr Hwnd;
        public uint DwFlags;
        public uint UCount;
        public uint DwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWInfo pwfi);

    /// <summary>Fa lampeggiare la finestra indicata finché non torna in primo piano. Handle zero = nessuna operazione.</summary>
    public static void Lampeggia(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        try
        {
            var info = new FlashWInfo
            {
                CbSize = (uint)Marshal.SizeOf<FlashWInfo>(),
                Hwnd = hwnd,
                DwFlags = FlashwCaption | FlashwTray | FlashwTimerNoFg,
                UCount = 0,
                DwTimeout = 0,
            };
            FlashWindowEx(ref info);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("NotificaTaskbarService.Lampeggia", ex);
        }
    }
}
