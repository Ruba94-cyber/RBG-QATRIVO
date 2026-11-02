using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Feedback utente 6.5.0, punto 6 (notifica inutile): un operatore che crea un Test risultandone
/// l'assegnatario NON deve ricevere la notifica "Test assegnato" per un'azione che ha appena compiuto lui.
/// Il Test e' creato dalla UI reale (stesso flow dei test CRUD); dopo la chiusura dell'app si legge il ledger
/// notifiche condiviso con un AppState isolato. La parte "autore diverso => la notifica parte" e' coperta a
/// livello ViewModel (NotificaAutoreAssegnatarioTests): il ComboBox Assegnatario dell'editor non ha ancora un
/// AutomationId stabile e il file dell'editor non e' di proprieta' di questa modifica.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
public sealed class NotificaAssegnazioneAutoreUiTests : UiTestBase
{
    public NotificaAssegnazioneAutoreUiTests() : base(TestWorkspace.CreateLoggedIn(nameof(NotificaAssegnazioneAutoreUiTests))) { }

    [Fact]
    public void CreaTest_AutoreAssegnatario_NessunaNotificaDiAssegnazione()
    {
        try
        {
            var shell = new MainShellPage(App.WaitForMainWindow());
            var lista = CreateTestCaseFlow.Run(App, shell, "QA notifica autore", TestWorkspace.DefaultEcuName);
            Assert.Equal(1, lista.RowCount());

            App.Dispose(); // rilascia il lock singolo e i file: da qui si legge il workspace dall'esterno

            using var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
            var test = Assert.Single(state.TestCases.GetAll());
            Assert.Equal(TestWorkspace.DefaultOperatorName, test.Assegnatario); // precondizione: autore = assegnatario
            Assert.DoesNotContain(state.Notifiche.GetAll(TestWorkspace.DefaultOperatorName), n => n.Tipo == "Assegnazione");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(CreaTest_AutoreAssegnatario_NessunaNotificaDiAssegnazione), ex);
        }
    }
}
