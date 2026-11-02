using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Prima copertura FlaUI per l'avviso "Test senza componente" della Home (Home_AvvisoTestSenzaEcu_*, gia'
/// dotato di AutomationId ma mai esercitato) e per il badge/filtro "Importato da UnitLogger" della Lista Test
/// (TestList_Row_ImportatoUdt_Badge/TestList_FiltroImportatoUdt_CheckBox, novita' 6.4.0). Semina i Test Case
/// direttamente via TestCaseRepository.Save (stessa tecnica di TestLoom.App.Tests, es.
/// ArchivioViewModelTests), non tramite il wizard UI: qui l'obiettivo e' il comportamento a valle
/// (avviso/badge/filtro), non la creazione stessa.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Navigation")]
public sealed class HomeDashboardTests : UiTestBase
{
    private const string TestSenzaEcuId = "TC-SENZA-componente-QA";
    private const string TestImportatoUdtId = "TC-IMPORTATO-UnitLogger-QA";

    public HomeDashboardTests() : base(CreaWorkspaceConTestSeminati()) { }

    private static TestWorkspace CreaWorkspaceConTestSeminati()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(HomeDashboardTests), seedTestFixtures: false);
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        // Nessun'componente assegnata (EcuSelection di default, vuota): conta per HomeViewModel.TestSenzaEcu.
        state.TestCases.Save(new TestCase { Id = TestSenzaEcuId, DescrizioneBreve = "Test QA senza componente", ModificatoDa = "UITests" }, isNew: true);

        // Origine=ImportStatoUdt: la lista mostra il badge "Da UnitLogger" e lo trova con il filtro dedicato.
        state.TestCases.Save(new TestCase { Id = TestImportatoUdtId, DescrizioneBreve = "Test QA importato da UnitLogger", Origine = OrigineTest.ImportStatoUdt, ModificatoDa = "UITests" }, isNew: true);

        return workspace;
    }

    [Fact]
    public void AvvisoTestSenzaEcu_ComparInHome_ENavigaAllaListaFiltrata()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var home = shell.GoToHome();
            Assert.True(home.IsTitleVisible);

            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Home_AvvisoTestSenzaEcu_Border"),
                "l'avviso 'Test senza componente' compare in Home con almeno un Test senza componente seminato");

            ElementFinders.WaitButtonById(window, "Home_AvvisoTestSenzaEcu_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "TestList_Title_Text"),
                "il click sull'avviso naviga alla Lista Test");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(AvvisoTestSenzaEcu_ComparInHome_ENavigaAllaListaFiltrata), ex);
        }
    }

    [Fact]
    public void BadgeEFiltroImportatoDaUdt_TrovaSoloIlTestSeminatoConQuellOrigine()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var testList = shell.GoToTestList();

            testList.Search(TestImportatoUdtId);
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "TestList_Row_ImportatoUdt_Badge"),
                "il badge 'Importato da UnitLogger' compare sulla riga del test seminato con quell'origine");

            testList.Search("");
            // La checkbox vive dentro "Filtri avanzati" (collassato di default, TestCaseListView.axaml):
            // va aperto prima, altrimenti il controllo non è nell'albero UIA.
            testList.ApriFiltriAvanzati();
            ElementFinders.WaitById(window, "TestList_FiltroImportatoUdt_CheckBox").SafeInvoke();
            Wait.ForCondition(() => testList.RowCount() == 1, "il filtro 'Solo importati da UnitLogger' isola il solo test seminato con quell'origine");
            Assert.Contains(TestImportatoUdtId, testList.VisibleRowIds());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(BadgeEFiltroImportatoDaUdt_TrovaSoloIlTestSeminatoConQuellOrigine), ex);
        }
    }
}
