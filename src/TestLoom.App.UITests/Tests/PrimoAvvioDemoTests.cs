using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
public sealed class PrimoAvvioDemoTests : UiTestBase
{
    public PrimoAvvioDemoTests() : base(TestWorkspace.CreateEmptyFirstRun(nameof(PrimoAvvioDemoTests))) { }

    [Fact]
    public void CaricaDemoCompleta_MostraSubitoMessaggioEdElencoOperatori_SenzaRicaricare()
    {
        try
        {
            var window = App.WaitForMainWindow();
            // All'avvio la cartella di lavoro ha gia' la struttura creata da TestLoom: come l'utente reale si punta
            // il campo Cartella su una cartella davvero vuota.
            var vuota = Path.Combine(Workspace.RootDir, "vuota");
            Directory.CreateDirectory(vuota);
            ElementFinders.WaitTextBoxById(window, "Login_Cartella_TextBox").SetText(vuota);
            ElementFinders.WaitButtonById(window, "Login_CaricaDemoCompleta_Button").SafeInvoke();

            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Login_DemoMessaggio_Text"),
                "messaggio di esito demo senza premere il pulsante di ricarica", TimeSpan.FromSeconds(90));
            Wait.ForCondition(() => LoginPage.IsShown(window),
                "elenco operatori/login visibile senza premere il pulsante di ricarica");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(CaricaDemoCompleta_MostraSubitoMessaggioEdElencoOperatori_SenzaRicaricare), ex);
        }
    }
}
