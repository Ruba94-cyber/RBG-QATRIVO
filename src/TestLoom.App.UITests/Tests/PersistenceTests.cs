using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Il workflow di persistenza richiesto esplicitamente (Fase 16): avvia TestLoom su un workspace di test, crea e
/// salva un Test Case, chiude TestLoom, lo riapre sullo STESSO workspace e verifica che il Test creato sia ancora
/// lì — sia dalla UI (Lista Test) sia leggendo direttamente il JSON scritto su disco, senza sostituire la
/// verifica UI con quella di storage (Fase 22: la UI resta la fonte di verità di cosa vede l'utente).
/// Questa classe gestisce il proprio ciclo di vita invece di ereditare da UiTestBase perché deve riavviare
/// TestLoomApplication a metà test mantenendo lo stesso TestWorkspace tra le due sessioni.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Persistence")]
public sealed class PersistenceTests : IDisposable
{
    private readonly TestWorkspace _workspace;

    public PersistenceTests() => _workspace = TestWorkspace.CreateLoggedIn(nameof(PersistenceTests));

    [Fact]
    public void TestCaseCreato_SopravviveAChiusuraERiaperturaDiDitm()
    {
        string createdId;
        using (var app1 = TestLoomApplication.Launch(_workspace))
        {
            try
            {
                var window1 = app1.WaitForMainWindow();
                var shell1 = new MainShellPage(window1);
                CreateTestCaseFlow.Run(app1, shell1, "QA smoke - persistenza dopo riavvio", TestWorkspace.DefaultEcuName);

                var testList1 = shell1.GoToTestList();
                createdId = testList1.VisibleRowIds().Single();
            }
            catch (Exception ex)
            {
                var dir = FailureArtifacts.Capture(app1, nameof(TestCaseCreato_SopravviveAChiusuraERiaperturaDiDitm), ex, step: "sessione 1 - creazione");
                throw new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
            }
        } // app1 chiuso qui: Dispose chiude la finestra e rilascia il mutex single-instance.

        // Verifica anche lo storage sottostante (Fase 22), senza sostituire la verifica UI più sotto: il file
        // JSON del Test deve esistere davvero nella cartella condivisa del workspace, non solo "sembrare"
        // presente perché un indice locale non ancora invalidato lo mostra ancora.
        var testCaseFiles = Directory.EnumerateFiles(_workspace.WorkspaceDir, $"testcase_{createdId}.json", SearchOption.AllDirectories).ToList();
        Assert.True(testCaseFiles.Count > 0, $"Il file JSON del Test {createdId} dovrebbe esistere sotto {_workspace.WorkspaceDir}.");

        using var app2 = TestLoomApplication.Launch(_workspace);
        try
        {
            var window2 = app2.WaitForMainWindow();
            var shell2 = new MainShellPage(window2);
            var testList2 = shell2.GoToTestList();

            Assert.Contains(createdId, testList2.VisibleRowIds());

            // OpenOnlyRow (non OpenRowById): il workspace di test contiene di proposito un solo Test, quindi
            // la garanzia è equivalente e non dipende dal contenitore di riga della DataGrid, che l'albero UI
            // Automation di Avalonia non espone in modo abbastanza affidabile da risalire dalla cella Id al
            // bottone "Apri" della stessa riga quando le righe sono più di una (vedi README, "Gap residui").
            var editor = testList2.OpenOnlyRow();
            Assert.Equal(createdId, editor.Title);
        }
        catch (Exception ex)
        {
            var dir = FailureArtifacts.Capture(app2, nameof(TestCaseCreato_SopravviveAChiusuraERiaperturaDiDitm), ex, step: "sessione 2 - verifica dopo riavvio");
            throw new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
        }
    }

    public void Dispose() => _workspace.Dispose();
}
