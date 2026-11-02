using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Prima copertura FlaUI per Impostazioni -> Recovery (Master): apertura e pulsante "Aggiorna" su un
/// cestino vuoto. Seminare un vero envelope di recovery (file eliminato realmente, poi recuperabile) richiede
/// DomainRecoveryService e non la sola scrittura diretta di un repository come per le altre Master data — GAP
/// annotato in COPERTURA_FLAUI.md, non affrontato in questa iterazione.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Navigation")]
public sealed class RecoveryBinNavigazioneTests : UiTestBase
{
    public RecoveryBinNavigazioneTests() : base(TestWorkspace.CreateLoggedIn(nameof(RecoveryBinNavigazioneTests))) { }

    [Fact]
    public void PaginaRecovery_SiApre_EAggiornaNonLanciaEccezioni()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var impostazioni = shell.GoToImpostazioni();
            Assert.True(impostazioni.HaSottoSezione("recovery"), "'Recovery' dovrebbe comparire per un operatore Master.");

            impostazioni.VaiA("recovery");
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Recovery_Title_Text"), "pagina Recovery caricata");

            ElementFinders.WaitButtonById(window, "Recovery_Aggiorna_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Recovery_Title_Text"), "la pagina resta stabile dopo Aggiorna");
            Assert.False(shell.HasNavigationError, "Errore di navigazione sulla pagina Recovery.");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(PaginaRecovery_SiApre_EAggiornaNonLanciaEccezioni), ex);
        }
    }
}
