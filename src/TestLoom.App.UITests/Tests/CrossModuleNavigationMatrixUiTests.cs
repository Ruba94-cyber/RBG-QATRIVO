using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Matrice parametrica di regressione: ogni scenario avvia il vero RBG - QATRIVO.exe su un
/// workspace temporaneo separato, verifica che il contenuto previsto sia REALMENTE
/// presente e che MainViewModel non mostri eccezioni di navigazione.
/// Nessun comando modifica dati operativi, ad eccezione dei filtri locali UI.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Navigation")]
[Trait("Category", "Regression")]
public sealed class CrossModuleNavigationMatrixUiTests : UiTestBase
{
    public CrossModuleNavigationMatrixUiTests()
        : base(TestWorkspace.CreateLoggedIn(nameof(CrossModuleNavigationMatrixUiTests))) { }

    [Theory]
    [InlineData("home", "Home_Title_Text")]
    [InlineData("test", "TestList_Title_Text")]
    [InlineData("anomalie", "Anomalie_Title_Text")]
    [InlineData("report", "Report_Page_ScrollViewer")]
    [InlineData("audit", "Audit_Title_Text")]
    [InlineData("impostazioni", "Impostazioni_Sidebar_List")]
    public void OgniSezionePrincipale_SiApreERitornaAllaHome(string id, string marker)
    {
        try
        {
            var w = App.WaitForMainWindow();
            var shell = new MainShellPage(w);
            shell.NavigateTo(id);
            ElementFinders.WaitById(w, marker);
            Assert.False(shell.HasNavigationError, $"Navigazione fallita: {id}");

            shell.GoToHome();
            Assert.True(ElementFinders.ExistsById(w, "Home_Title_Text"));
            Assert.False(shell.HasNavigationError, $"Ritorno da: {id}");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(OgniSezionePrincipale_SiApreERitornaAllaHome), ex, step: id, searchedAutomationId: marker); }
    }

    [Theory]
    [InlineData("generale", "Impostazioni_Dati_VerificaLocale_Button")]
    [InlineData("archivio", "Impostazioni_Sidebar_List")]
    [InlineData("recovery", "Impostazioni_Sidebar_List")]
    [InlineData("operatori", "GridPrincipale")]
    [InlineData("requisiti", "GridPrincipale")]
    [InlineData("funzionalita", "GridPrincipale")]
    [InlineData("componente", "GridPrincipale")]
    [InlineData("veicoli", "GridPrincipale")]
    [InlineData("strumenti", "GridPrincipale")]
    [InlineData("tipiditest", "GridPrincipale")]
    public void OgniSottosezioneImpostazioni_SiCaricaESiPuoRiaprire(string id, string marker)
    {
        try
        {
            var w = App.WaitForMainWindow();
            var shell = new MainShellPage(w);
            var imp = shell.GoToImpostazioni();
            imp.VaiA(id);
            ElementFinders.WaitById(w, marker);
            Assert.False(shell.HasNavigationError, $"Errore sottosezione: {id}");

            imp.VaiA("generale");
            ElementFinders.WaitById(w, "Impostazioni_Dati_VerificaLocale_Button");
            Assert.False(shell.HasNavigationError, $"Errore dopo ritorno da sottosezione: {id}");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(OgniSottosezioneImpostazioni_SiCaricaESiPuoRiaprire), ex, step: id, searchedAutomationId: marker); }
    }

    [Theory]
    [InlineData("Anomalie_SchedaTutte_Toggle")]
    [InlineData("Anomalie_SchedaAnomalie_Toggle")]
    [InlineData("Anomalie_SchedaChangeRequest_Toggle")]
    public void Anomalie_SchedeSonoNavigabiliSenzaPerderePagina(string tabId)
    {
        try
        {
            var w = App.WaitForMainWindow();
            var shell = new MainShellPage(w);
            shell.GoToAnomalie();
            ElementFinders.WaitById(w, tabId).SafeInvoke();
            Assert.True(ElementFinders.ExistsById(w, "Anomalie_Title_Text"));
            Assert.False(shell.HasNavigationError);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Anomalie_SchedeSonoNavigabiliSenzaPerderePagina), ex, searchedAutomationId: tabId); }
    }

    [Fact]
    public void TestCase_RicercaSenzaRisultatiNonNascondeLaNavigazione()
    {
        try
        {
            var w = App.WaitForMainWindow();
            var shell = new MainShellPage(w);
            var lista = shell.GoToTestList();
            lista.Search("stringa-assente-qa-98ea8c07");
            Wait.ForCondition(() => lista.RowCount() == 0, "nessun TestCase corrispondente");
            Assert.False(shell.HasNavigationError);
            shell.GoToHome();
            Assert.True(ElementFinders.ExistsById(w, "Home_Title_Text"));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(TestCase_RicercaSenzaRisultatiNonNascondeLaNavigazione), ex); }
    }
}
