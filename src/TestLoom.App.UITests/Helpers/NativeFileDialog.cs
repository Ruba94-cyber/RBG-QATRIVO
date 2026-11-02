using System.Runtime.InteropServices;
using System.Text;
using TestLoom.App.UITests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Keyboard = FlaUI.Core.Input.Keyboard;

namespace TestLoom.App.UITests.Helpers;

/// <summary>
/// Pilota la finestra nativa di scelta file di Windows (IFileOpenDialog) aperta da StorageProvider.OpenFilePickerAsync
/// (allegati e import log WU/UnitLogger): non e' un controllo Avalonia, quindi non ha AutomationId dell'app. Si usano gli
/// identificatori stabili della finestra di dialogo comune di Windows: "1148" = campo "Nome file" (il ComboBox esterno
/// e il suo Edit interno hanno lo stesso id: si usa l'Edit), "1" = pulsante "Apri" (SplitButton), "2" = "Annulla".
/// Come un operatore: prima si naviga nella cartella del file (percorso di cartella + Invio), poi si seleziona il file
/// per nome e si conferma con Invio. Il testo va nell'Edit con WM_SETTEXT e l'Invio con PostMessage, entrambi diretti
/// all'handle dell'Edit: non dipendono dal focus e non possono finire nella finestra principale di TestLoom.
/// </summary>
public static class NativeFileDialog
{
    private const string NomeFileId = "1148";
    private const string ApriId = "1";
    private const string AnnullaId = "2";

    private const uint WmSetText = 0x000C;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int VkReturn = 0x0D;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    /// <summary>Sceglie il file sintetico indicato: naviga nella sua cartella, lo seleziona per nome e conferma.
    /// In caso di errore chiude il dialogo con "Annulla" (InvokePattern, nessun tasto).</summary>
    public static void SceglieFile(TestLoomApplication app, string percorsoAssoluto, TimeSpan? timeout = null)
    {
        if (!File.Exists(percorsoAssoluto))
            throw new FileNotFoundException("File di prova sintetico mancante", percorsoAssoluto);
        var cartella = Path.GetDirectoryName(percorsoAssoluto)!;
        var nomeFile = Path.GetFileName(percorsoAssoluto);
        Window? dialog = null;
        try
        {
            dialog = app.WaitForWindowByContent(NomeFileId, timeout ?? TimeSpan.FromSeconds(15));
            var campo = CampoNomeFile(dialog);

            // 1) Navigazione: il percorso della cartella + Invio. La finestra resta aperta nella cartella del file.
            ImpostaValore(campo, cartella);
            InviaInvio(campo);
            if (!Wait.TryForCondition(() => dialog.FindFirstDescendant(cf => cf.ByName(nomeFile)) != null, TimeSpan.FromSeconds(8)))
            {
                ApriPulsante(dialog);
                Wait.ForCondition(() => dialog.FindFirstDescendant(cf => cf.ByName(nomeFile)) != null,
                    $"file '{nomeFile}' elencato nella cartella di scelta", TimeSpan.FromSeconds(10));
            }

            // 2) Selezione per nome + Invio: la finestra si chiude con il file scelto.
            ImpostaValore(campo, nomeFile);
            InviaInvio(campo);
            if (!Wait.TryForCondition(() => app.IsWindowClosedByContent(NomeFileId), TimeSpan.FromSeconds(5)))
                ApriPulsante(dialog);
            Wait.ForCondition(() => app.IsWindowClosedByContent(NomeFileId), "chiusura della finestra di scelta file", TimeSpan.FromSeconds(15));
        }
        catch (Exception)
        {
            if (dialog != null) AnnullaSeAperta(app, dialog);
            throw;
        }
    }

    /// <summary>Edit del campo "Nome file" (AutomationId 1148 e ControlType Edit): il ComboBox che lo contiene ha lo stesso id.</summary>
    private static AutomationElement CampoNomeFile(Window dialog) =>
        Wait.Until(
            () => dialog.FindFirstDescendant(cf => cf.ByAutomationId(NomeFileId).And(cf.ByControlType(ControlType.Edit))),
            e => e != null, TimeSpan.FromSeconds(10), "campo 'Nome file' (Edit, AutomationId 1148) della finestra di scelta")!;

    /// <summary>Scrive il testo nell'Edit con WM_SETTEXT (diretto all'handle) e lo verifica; senza handle ripiega su
    /// ValuePattern, verificando allo stesso modo il testo nativo.</summary>
    private static void ImpostaValore(AutomationElement campo, string valore)
    {
        var hwnd = campo.FrameworkAutomationElement.NativeWindowHandle;
        if (hwnd != IntPtr.Zero)
        {
            SendMessage(hwnd, WmSetText, IntPtr.Zero, valore);
            if (TestoNativo(hwnd) == valore) return;
        }
        if (campo.Patterns.Value.TryGetPattern(out var pattern))
            pattern.SetValue(valore);
        Wait.ForCondition(() => TestoNativoOValore(campo) == valore,
            "testo impostato nel campo 'Nome file' (WM_SETTEXT o ValuePattern)", TimeSpan.FromSeconds(5));
    }

    private static string TestoNativoOValore(AutomationElement campo)
    {
        var hwnd = campo.FrameworkAutomationElement.NativeWindowHandle;
        if (hwnd != IntPtr.Zero) return TestoNativo(hwnd);
        return campo.Patterns.Value.TryGetPattern(out var pattern) ? pattern.Value.Value ?? "" : "";
    }

    private static string TestoNativo(IntPtr hwnd)
    {
        var testo = new StringBuilder(1024);
        GetWindowText(hwnd, testo, testo.Capacity);
        return testo.ToString();
    }

    /// <summary>Invio inviato con PostMessage all'handle dell'Edit: arriva al dialogo senza dipendere dal focus.</summary>
    private static void InviaInvio(AutomationElement campo)
    {
        var hwnd = campo.FrameworkAutomationElement.NativeWindowHandle;
        if (hwnd == IntPtr.Zero)
        {
            // Nessun handle: ultima risorsa, con il focus sul campo e la finestra di scelta in primo piano.
            campo.Focus();
            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
            return;
        }
        PostMessage(hwnd, WmKeyDown, new IntPtr(VkReturn), new IntPtr(0x001C0001));
        PostMessage(hwnd, WmKeyUp, new IntPtr(VkReturn), new IntPtr(unchecked((int)0xC01C0001)));
    }

    /// <summary>Preme "Apri" con InvokePattern (ripiego se l'Invio non ha avuto effetto).</summary>
    private static void ApriPulsante(Window dialog)
    {
        var apri = dialog.FindFirstDescendant(cf => cf.ByAutomationId(ApriId));
        if (apri != null && apri.Patterns.Invoke.TryGetPattern(out var invoke))
            invoke.Invoke();
    }

    /// <summary>Chiude il dialogo con "Annulla" (InvokePattern), senza tasti: gli errori sono ignorati perche'
    /// l'eccezione originale del test e' quella da riportare.</summary>
    private static void AnnullaSeAperta(TestLoomApplication app, Window dialog)
    {
        try
        {
            if (app.IsWindowClosedByContent(NomeFileId)) return;
            var annulla = dialog.FindFirstDescendant(cf => cf.ByAutomationId(AnnullaId));
            if (annulla != null && annulla.Patterns.Invoke.TryGetPattern(out var invoke))
                invoke.Invoke();
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
