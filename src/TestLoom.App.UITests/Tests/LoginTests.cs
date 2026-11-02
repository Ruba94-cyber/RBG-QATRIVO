using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
public sealed class LoginTests : UiTestBase
{
    public LoginTests() : base(TestWorkspace.CreateLoggedOut(nameof(LoginTests))) { }

    [Fact]
    public void Login_ConNomeEPinCorretti_ApreLaHome()
    {
        try
        {
            var window = App.WaitForMainWindow();
            Wait.ForCondition(() => LoginPage.IsShown(window), "schermata di login all'avvio (nessun operatore ricordato)");

            var login = new LoginPage(window);
            login.LoginAs(TestWorkspace.DefaultOperatorName, TestWorkspace.DefaultOperatorPin);

            Wait.ForCondition(() => MainShellPage.IsShown(window), "shell principale dopo login corretto");
            var home = new HomePage(new MainShellPage(window).PageHost);
            Assert.True(home.IsTitleVisible);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Login_ConNomeEPinCorretti_ApreLaHome), ex);
        }
    }

    [Fact]
    public void Login_ConPinErrato_MostraErroreERestaSullaSchermataDiAccesso()
    {
        try
        {
            var window = App.WaitForMainWindow();
            Wait.ForCondition(() => LoginPage.IsShown(window), "schermata di login all'avvio");

            var login = new LoginPage(window);
            login.LoginAs(TestWorkspace.DefaultOperatorName, "0000");

            Wait.ForCondition(() => login.HasError, "messaggio di errore PIN non corretto");
            Assert.False(MainShellPage.IsShown(window), "Un PIN errato non deve aprire la Home.");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Login_ConPinErrato_MostraErroreERestaSullaSchermataDiAccesso), ex);
        }
    }

    [Trait("Category", "Keyboard")]
    [Fact]
    public void Login_TabDopoOperatore_SpostaIlFocusSulCampoPin()
    {
        try
        {
            var window = App.WaitForMainWindow();
            Wait.ForCondition(() => LoginPage.IsShown(window), "schermata di login all'avvio");

            var login = new LoginPage(window);
            login.SelectOperator(TestWorkspace.DefaultOperatorName);

            var comboBox = window.FindFirstDescendant(cf => cf.ByAutomationId("Login_Operatore_ComboBox"));
            comboBox!.Focus();
            ElementActions.PressKey(window, FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);

            var pinTextBox = window.FindFirstDescendant(cf => cf.ByAutomationId("Login_Pin_TextBox"));
            Wait.ForCondition(() => App.SafeFocusedElementId() == "Login_Pin_TextBox" || (pinTextBox != null && pinTextBox.Properties.HasKeyboardFocus.ValueOrDefault),
                "focus sul campo PIN dopo Tab", TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Login_TabDopoOperatore_SpostaIlFocusSulCampoPin), ex);
        }
    }
}
