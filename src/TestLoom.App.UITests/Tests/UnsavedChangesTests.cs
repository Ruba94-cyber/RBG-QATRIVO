using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using TestLoom.App.Services;

namespace TestLoom.App.UITests.Tests;

/// <summary>Dirty-state alla chiusura dell'applicazione (Fase 17): MainWindow.OnClosing chiede conferma solo
/// se MainViewModel.HaModificheNonSalvate() è vera (TestCaseEditorViewModel.ModificheNonSalvate) — verificato
/// qui chiudendo davvero la finestra (WM_CLOSE via FlaUI Window.Close(), non un evento simulato) mentre un
/// Test nuovo è a metà compilazione.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Dialogs")]
public sealed class UnsavedChangesTests : UiTestBase
{
    public UnsavedChangesTests() : base(TestWorkspace.CreateLoggedIn(nameof(UnsavedChangesTests))) { }

    [Fact]
    public void ChiusuraFinestra_ConModificheNonSalvate_ChiedeConfermaEAnnullaTeneApertaLApp()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);

            var testList = shell.GoToTestList();
            var editor = testList.ClickNew();
            editor.ChooseTemplate();
            editor.GoToTab(1);
            editor.SetDescrizioneBreve("QA smoke - modifiche non salvate alla chiusura");

            window.Close();

            var confirmWindow = App.WaitForWindowByContent("Confirm_Yes_Button");
            var confirm = new ConfirmDialogPage(confirmWindow);
            Assert.False(string.IsNullOrWhiteSpace(confirm.Message));
            confirm.ConfirmNo();

            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura del dialog di conferma dopo 'No'");
            Assert.True(App.IsRunning, "Rispondendo 'No' alla conferma, TestLoom deve restare aperto con le modifiche ancora presenti.");
            Assert.Contains("TestLoom", App.ListTopLevelWindowNames());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ChiusuraFinestra_ConModificheNonSalvate_ChiedeConfermaEAnnullaTeneApertaLApp), ex);
        }
    }

    [Fact]
    public void ChiusuraFinestra_SenzaModifiche_AttendeIlRilascioDelLockMasterData()
    {
        FileStream? lockTrattenuto = null;
        try
        {
            var window = App.WaitForMainWindow();
            var impostazioni = new MainShellPage(window).GoToImpostazioni();
            var editor = impostazioni.VaiARequisiti();
            editor.IniziaModifica();
            Wait.ForCondition(() => editor.InModifica,
                "editor Requisiti in modifica prima di trattenere il lock", TimeSpan.FromSeconds(10));

            var lockPath = LockService.LockPathFor(Path.Combine(Workspace.WorkspaceDir, "requisiti.json"));
            Wait.ForCondition(() => File.Exists(lockPath), "acquisizione del lock del catalogo Requisiti");
            Wait.ForCondition(() =>
            {
                try
                {
                    lockTrattenuto = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    return true;
                }
                catch (IOException) { return false; }
            }, "acquisizione esclusiva del lock del catalogo Requisiti", TimeSpan.FromSeconds(10));
            Assert.NotNull(lockTrattenuto);

            // Il file lock tenuto da questo processo costringe il rilascio applicativo ad attendere il retry.
            // WM_CLOSE deve essere annullato sincronicamente finché il ViewModel non ha completato l'I/O.
            window.Close();
            Assert.True(App.IsRunning, "TestLoom deve restare in esecuzione mentre il rilascio lock è in attesa.");
            Assert.Contains(App.ListTopLevelWindowNames(), titolo => titolo.StartsWith("TestLoom", StringComparison.Ordinal));

            lockTrattenuto.Dispose();
            lockTrattenuto = null;
            Wait.ForCondition(() => !File.Exists(lockPath), "rilascio del lock prima della chiusura della finestra", TimeSpan.FromSeconds(15));
            Wait.ForCondition(() => !App.IsRunning,
                "chiusura effettiva dell'app dopo il rilascio del lock", TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ChiusuraFinestra_SenzaModifiche_AttendeIlRilascioDelLockMasterData), ex);
        }
        finally
        {
            lockTrattenuto?.Dispose();
        }
    }
}
