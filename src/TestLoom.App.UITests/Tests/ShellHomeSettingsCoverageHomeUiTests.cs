using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using static TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Home: KPI "Change request aperte" cliccabile verso Anomalie / CR, e "Personalizza Home -> Ripristina predefinito"
/// che riporta le sezioni nascoste. Hide/show e riordino gia' coperti da HomePersonalizzazioneUiTests.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Coverage")]
[Trait("Area", "Home")]
public sealed class ShellHomeSettingsCoverageHomeUiTests : UiTestBase
{
    public ShellHomeSettingsCoverageHomeUiTests() : base(TestWorkspace.CreateLoggedIn(nameof(ShellHomeSettingsCoverageHomeUiTests))) { }

    internal static void ApriPersonalizzazione(Window window)
    {
        if (IdVisibile(window, "Home_Layout_grafici_CheckBox")) return;
        ElementFinders.WaitButtonById(window, "Home_Personalizza_Button").SafeInvoke();
        Wait.ForCondition(() => IdVisibile(window, "Home_Layout_grafici_CheckBox"), "pannello Personalizza Home aperto", TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void KpiChangeRequestAperte_ApreAnomalieCR()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.DismissNotificationsIfShown();
            shell.GoToHome();

            var kpi = Wait.ForElement(() => VisibilePerId(window, "Home_KpiChangeRequest_Button"),
                "KPI 'Change request aperte' visibile", TimeSpan.FromSeconds(20));
            Assert.True(TestoContieneVisibile(kpi, "Change request aperte"), "la card riporta l'etichetta 'Change request aperte'");

            kpi.AsButton().SafeInvoke();
            Wait.ForCondition(() => IdVisibile(window, "Anomalie_Title_Text"), "il KPI CR apre Anomalie / CR", TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(KpiChangeRequestAperte_ApreAnomalieCR), ex); }
    }

    [Fact]
    public void PersonalizzaHome_RipristinaPredefinito_RiportaLeSezioniNascoste()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.DismissNotificationsIfShown();
            shell.GoToHome();
            ApriPersonalizzazione(window);

            var grafici = ElementFinders.WaitById(window, "Home_Layout_grafici_CheckBox").AsCheckBox();
            if (grafici.IsChecked == true) grafici.SafeInvoke();
            Wait.ForCondition(() => !IdVisibile(window, "Home_Sezione_grafici"),
                "la sezione grafici sparisce dopo averla nascosta", TimeSpan.FromSeconds(15));

            ElementFinders.WaitButtonById(window, "Home_Layout_Reset_Button").SafeInvoke();
            Wait.ForCondition(() => IdVisibile(window, "Home_Sezione_grafici")
                    && ElementFinders.WaitById(window, "Home_Layout_grafici_CheckBox").AsCheckBox().IsChecked == true,
                "dopo 'Ripristina predefinito' la sezione grafici torna visibile e spuntata", TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(PersonalizzaHome_RipristinaPredefinito_RiportaLeSezioniNascoste), ex); }
    }
}
