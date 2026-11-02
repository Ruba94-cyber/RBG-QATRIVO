using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

[Collection("TestLoom UI")]
[Trait("Category", "Dialogs")]
public sealed class TestEditorChiusuraLockUiTests : UiTestBase
{
    private const string TestId = "TC-LOCK-CLOSE-UI";

    public TestEditorChiusuraLockUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(TestEditorChiusuraLockUiTests));
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        state.TestCases.Save(new TestCase
        {
            Id = TestId,
            DescrizioneBreve = "Test chiusura e rilascio lock",
            Componente = new EcuSelection { TestWorkspace.DefaultEcuName },
            Veicolo = TestWorkspace.DefaultVehicleName,
            Veicoli = { TestWorkspace.DefaultVehicleName },
            Assegnatario = TestWorkspace.DefaultOperatorName,
            StatoLavoro = StatoLavoroTest.InCorso,
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
            ModificatoDa = "UITests",
        }, isNew: true);
        return workspace;
    }

    [Fact]
    public void ChiusuraTestInModifica_AttendeIlRilascioDelLockPrimaDiTerminare()
    {
        FileStream? lockTrattenuto = null;
        try
        {
            using var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
            var testCase = Assert.Single(state.TestCases.GetAll());
            var lockPath = LockService.LockPathFor(state.TestCases.PercorsoPer(testCase));

            var window = App.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToTestList().OpenOnlyRow();
            editor.EnterEditMode();
            Wait.ForCondition(() => File.Exists(lockPath), "acquisizione del lock Test in modifica");
            Wait.ForCondition(() =>
            {
                try
                {
                    lockTrattenuto = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    return true;
                }
                catch (IOException) { return false; }
            }, "acquisizione esclusiva del file lock Test", TimeSpan.FromSeconds(10));
            Assert.NotNull(lockTrattenuto);

            window.Close();
            Assert.True(App.IsRunning, "TestLoom deve restare aperto finché il retry del lock Test è in attesa.");
            Assert.Contains(App.ListTopLevelWindowNames(), titolo => titolo.StartsWith("TestLoom", StringComparison.Ordinal));
            Assert.False(editor.SaveEnabled, "l'editor deve congelare il salvataggio durante il rilascio del lock in chiusura");
            Assert.False(editor.CancelEnabled, "l'editor deve congelare l'annullamento durante il rilascio del lock in chiusura");

            lockTrattenuto.Dispose();
            lockTrattenuto = null;
            Wait.ForCondition(() => !File.Exists(lockPath), "rilascio del lock Test prima della chiusura", TimeSpan.FromSeconds(15));
            Wait.ForCondition(() => !App.IsRunning, "chiusura dell'app dopo il rilascio del lock Test", TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ChiusuraTestInModifica_AttendeIlRilascioDelLockPrimaDiTerminare), ex);
        }
        finally
        {
            lockTrattenuto?.Dispose();
        }
    }
}
