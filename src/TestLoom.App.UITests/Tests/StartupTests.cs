using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

[Collection("TestLoom UI")]
[Trait("Category", "Smoke")]
public sealed class StartupTests : UiTestBase
{
    public StartupTests() : base(TestWorkspace.CreateLoggedIn(nameof(StartupTests))) { }

    [Fact]
    public void Avvio_ConWorkspaceEOperatoreGiaConfigurati_ApreDirettamenteLaHome()
    {
        try
        {
            var window = App.WaitForMainWindow();
            Assert.Equal("TestLoom", window.Title);

            var shell = new MainShellPage(window);
            Assert.True(MainShellPage.IsShown(window), "La sidebar di navigazione principale dovrebbe essere visibile dopo un login già persistito.");

            var home = new HomePage(shell.PageHost);
            Assert.True(home.IsTitleVisible);
            Assert.False(shell.HasNavigationError, "Nessun errore di navigazione dovrebbe comparire all'avvio.");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Avvio_ConWorkspaceEOperatoreGiaConfigurati_ApreDirettamenteLaHome), ex);
        }
    }

    [Fact]
    public void Avvio_SecondaIstanzaSullaStessaMacchina_NonApreUnaSecondaFinestra()
    {
        try
        {
            var window = App.WaitForMainWindow();
            Assert.Equal(1, App.ListTopLevelWindowNames().Count(t => t == "TestLoom"));

            // SingleInstanceService (§6.7): un secondo processo TestLoom.exe sullo stesso PC non deve mai aprire
            // una seconda finestra — chiede alla prima di attivarsi e termina da solo. Verificato lanciando
            // davvero un secondo processo, non solo leggendo il codice: è una garanzia anti-corruzione-dati
            // (due processi che scrivono sulla stessa cartella condivisa senza coordinarsi) troppo critica per
            // fidarsi di un'ispezione statica.
            var secondExePath = Infrastructure.TestLoomExeLocator.Resolve();
            var secondProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = secondExePath,
                Arguments = $"--config \"{Workspace.ConfigPath}\"",
                UseShellExecute = false,
            })!;

            Wait.ForCondition(() => secondProcess.HasExited, "uscita del secondo processo TestLoom (single-instance)", TimeSpan.FromSeconds(15));
            Assert.True(secondProcess.HasExited);
            Assert.Equal(1, App.ListTopLevelWindowNames().Count(t => t == "TestLoom"));
            Assert.True(App.IsRunning, "Il primo processo deve restare in esecuzione dopo il tentativo di doppio avvio.");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Avvio_SecondaIstanzaSullaStessaMacchina_NonApreUnaSecondaFinestra), ex);
        }
    }
}
