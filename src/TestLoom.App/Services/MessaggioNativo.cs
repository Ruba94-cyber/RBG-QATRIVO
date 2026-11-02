using System.Runtime.InteropServices;

namespace TestLoom.App.Services;

/// <summary>Finestra di errore di sistema (Win32) per un avvio impossibile: non dipende da Avalonia, che in quel momento può
/// non esistere ancora o non funzionare. Non lancia mai: un errore qui non deve nascondere quello originale.</summary>
internal static class MessaggioNativo
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr finestra, string testo, string titolo, uint tipo);

    /// <summary>Dice all'utente che l'avvio è fallito e dove trovare i dettagli (crash.log). Resta bloccante finché non la chiude.</summary>
    public static void MostraErroreAvvio()
    {
        try
        {
            var loc = LocalizationService.Instance;
            var percorso = Path.Combine(CrashLogService.LogDirectory, "crash.log");
            MessageBoxW(IntPtr.Zero, string.Format(loc.Traduci("AvvioFallitoMessaggioFmt"), percorso),
                loc.Traduci("AvvioFallitoTitolo"), MbOk | MbIconError);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MessaggioNativo: {ex}");
        }
    }
}
