using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>6.4.6: Esc chiude gli overlay della finestra principale (ricerca globale, centro notifiche, aiuto)
/// anche quando il fuoco e' su un controllo che consumerebbe il tasto (es. la casella di ricerca).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Keyboard")]
public sealed class EscChiudeOverlayTests : UiTestBase
{
    public EscChiudeOverlayTests() : base(TestWorkspace.CreateLoggedIn(nameof(EscChiudeOverlayTests))) { }

    [Fact]
    public void Esc_ChiudeLaRicercaGlobaleConFuocoNellaCasella()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.DismissNotificationsIfShown();

            ElementFinders.WaitById(window, "Main_RicercaGlobale_Button").SafeInvoke();
            var casella = ElementFinders.WaitById(window, "Main_RicercaGlobale_TextBox");
            casella.Focus();

            ElementActions.PressKey(window, FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);

            Wait.ForCondition(() => !ElementFinders.ExistsById(window, "Main_RicercaGlobale_TextBox"),
                "la ricerca globale si chiude con Esc", TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Esc_ChiudeLaRicercaGlobaleConFuocoNellaCasella), ex);
        }
    }
}
