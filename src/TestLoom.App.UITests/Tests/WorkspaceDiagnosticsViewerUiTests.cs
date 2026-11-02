using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Role-based FlaUI verification: this is a read-only diagnostics viewer,
/// not a way to gain write privileges.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
[Trait("Category", "Permissions")]
public sealed class WorkspaceDiagnosticsViewerUiTests : UiTestBase
{
    public WorkspaceDiagnosticsViewerUiTests()
        : base(TestWorkspace.CreateLoggedInAs(nameof(WorkspaceDiagnosticsViewerUiTests),
            RuoloOperatore.Viewer)) { }

    private (MainShellPage Shell, WorkspaceDiagnosticsPage Page) Open()
    {
        var w = App.WaitForMainWindow();
        var shell = new MainShellPage(w);
        shell.GoToImpostazioni().VaiA("generale");
        var page = new Post653ImpostazioniPage(w, App).ApriCentroIntegrita();
        Wait.ForCondition(() => page.Status.Contains("Fotografia locale completata"),
            "fotografia locale disponibile al ruolo Viewer");
        return (shell, page);
    }

    [Fact]
    public void Viewer_ApreLaDiagnosticaSenzaAverePermessiDiScrittura()
    {
        try
        {
            var (shell, page) = Open();
            Assert.Contains("OneDrive", page.Details);
            Assert.Contains("NON certificano", page.Details);
            Assert.True(page.CanRefresh);
            page.Close();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Integrity_Title_Text"),
                "finestra integrità chiusa");
            shell.GoToTestList();
            Assert.False(ElementFinders.WaitButtonById(App.WaitForMainWindow(),
                "TestList_New_Button").IsEnabled);
            // I permessi Viewer sono applicati dal layer utente già esistente;
            // WorkspaceWritePolicy verifica la cartella e non conosce il ruolo dell'operatore.
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Viewer_ApreLaDiagnosticaSenzaAverePermessiDiScrittura), ex); }
    }

    [Fact]
    public void Viewer_AggiornaEDopoChiusuraNavigaSenzaScrivere()
    {
        try
        {
            var baseline = Directory.EnumerateFiles(Workspace.WorkspaceDir, "*.json")
                .ToDictionary(file => file, file => File.ReadAllBytes(file));
            var (shell, page) = Open();
            page.Refresh();
            Wait.ForCondition(() => page.CanRefresh && page.Status.Contains("completata"),
                "aggiornamento metadata diagnostica Viewer");
            page.Close();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Integrity_Title_Text"),
                "finestra integrità chiusa");
            shell.GoToHome();
            Assert.False(shell.HasNavigationError);
            Assert.All(baseline, kv => Assert.True(kv.Value.SequenceEqual(File.ReadAllBytes(kv.Key))));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Viewer_AggiornaEDopoChiusuraNavigaSenzaScrivere), ex); }
    }
}
