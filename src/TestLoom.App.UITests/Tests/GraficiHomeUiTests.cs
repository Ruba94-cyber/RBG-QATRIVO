using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.WindowsAPI;
using System.Runtime.InteropServices;

namespace TestLoom.App.UITests.Tests;

/// <summary>Grafici della Home: "Apri" apre la finestra del grafico (Esc la chiude) e "Esporta PNG" apre il dialogo nativo
/// di salvataggio. Il test non completa l'esportazione: annulla il dialogo.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Coverage")]
[Trait("Area", "Home")]
public sealed class GraficiHomeUiTests : UiTestBase
{
    private const uint CfDibV5 = 17;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr memory);
    public GraficiHomeUiTests() : base(TestWorkspace.CreateLoggedIn(nameof(GraficiHomeUiTests))) { }

    [Fact]
    public void Grafico_ApriEsportaPng_DallaCardDellaHome()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.DismissNotificationsIfShown();
            shell.GoToHome();
            var home = new HomePage(window);
            home.SelezionaGrafico("donut");

            home.ApriGraficoButton("donut").SafeInvoke();
            var finestra = App.WaitForWindowByContent("GraficoFinestra_Immagine", TimeSpan.FromSeconds(20));
            Assert.NotNull(finestra);
            FlaUI.Core.Input.Keyboard.Type(VirtualKeyShort.ESCAPE);
            // Chiusura verificata come per gli altri dialoghi: per contenuto tra le finestre di primo livello,
            // non per lo stato dell'elemento catturato prima di Esc.
            Wait.ForCondition(() => App.IsWindowClosedByContent("GraficoFinestra_Immagine"), "la finestra grafico si chiude con Esc", TimeSpan.FromSeconds(10));

            home.EsportaPngGraficoButton("donut").SafeInvoke();
            // Il dialogo di salvataggio e' un common dialog (#32770) figlio della finestra principale, non una finestra di primo
            // livello: si attende con lo stesso mezzo degli altri export (Report, Audit, Anomalie), poi si annulla senza scrivere.
            var dialogo = NativeFileDialogPage.Attendi(App);
            Assert.Contains("PNG", dialogo.Titolo, StringComparison.OrdinalIgnoreCase);
            dialogo.Annulla();
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Grafico_ApriEsportaPng_DallaCardDellaHome), ex); }
    }

    [Fact]
    public void Grafico_Copia_PubblicaDibV5ETrasparenzaNegliAppuntiWindows()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.DismissNotificationsIfShown();
            shell.GoToHome();
            var home = new HomePage(window);
            home.SelezionaGrafico("donut");

            home.CopiaGraficoButton("donut").SafeInvoke();
            byte[]? dib = null;
            byte[]? png = null;
            var pngFormat = RegisterClipboardFormat("PNG");
            Wait.ForCondition(() =>
            {
                dib = LeggiFormatoAppunti(CfDibV5);
                png = pngFormat == 0 ? null : LeggiFormatoAppunti(pngFormat);
                return dib is { Length: > 124 } && png is { Length: > 8 };
            }, "immagine PNG e DIBV5 nella clipboard", TimeSpan.FromSeconds(30));

            var dibData = dib!;
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png![..8]);
            Assert.Equal(124, BitConverter.ToInt32(dibData, 0));
            Assert.Equal(32, BitConverter.ToInt16(dibData, 14));
            Assert.Equal(unchecked((int)0xFF000000), BitConverter.ToInt32(dibData, 52));
            var width = BitConverter.ToInt32(dibData, 4);
            var height = BitConverter.ToInt32(dibData, 8);
            Assert.True(width > 0 && height > 0);
            var alphaZero = false;
            var pixelColor = false;
            for (var offset = 124; offset < dibData.Length; offset += 4)
            {
                var alpha = dibData[offset + 3];
                alphaZero |= alpha == 0;
                pixelColor |= alpha > 0 && (dibData[offset] != 0 || dibData[offset + 1] != 0 || dibData[offset + 2] != 0);
                if (alphaZero && pixelColor) break;
            }
            Assert.True(alphaZero, "la DIBV5 conserva pixel completamente trasparenti");
            Assert.True(pixelColor, "la DIBV5 contiene pixel del grafico e non un'immagine nera");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Grafico_Copia_PubblicaDibV5ETrasparenzaNegliAppuntiWindows), ex); }
    }

    private static byte[]? LeggiFormatoAppunti(uint formato)
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var memoria = GetClipboardData(formato);
            if (memoria == IntPtr.Zero) return null;
            var dimensione = checked((int)GlobalSize(memoria).ToUInt64());
            if (dimensione <= 0) return null;
            var puntatore = GlobalLock(memoria);
            if (puntatore == IntPtr.Zero) return null;
            try
            {
                var dati = new byte[dimensione];
                Marshal.Copy(puntatore, dati, 0, dimensione);
                return dati;
            }
            finally { GlobalUnlock(memoria); }
        }
        finally { CloseClipboard(); }
    }
}
