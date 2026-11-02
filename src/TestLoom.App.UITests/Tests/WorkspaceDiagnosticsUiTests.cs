using System.Security.Cryptography;
using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Integrazione UIA3 senza simulare dati o cloud: dialogo Avalonia reale, fixture
/// temporanea e asserzioni sui byte del documento operativo dopo la diagnostica.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
[Trait("Category", "Diagnostics")]
public sealed class WorkspaceDiagnosticsUiTests : UiTestBase
{
    public WorkspaceDiagnosticsUiTests()
        : base(TestWorkspace.CreateLoggedIn(nameof(WorkspaceDiagnosticsUiTests))) { }

    private WorkspaceDiagnosticsPage Open()
    {
        var window = App.WaitForMainWindow();
        var settings = new MainShellPage(window).GoToImpostazioni();
        settings.VaiA("generale");
        var dialog = new Post653ImpostazioniPage(window, App).ApriCentroIntegrita();
        Wait.ForCondition(() => dialog.Status.Contains("Fotografia locale completata"),
            "inventario dati locali completo");
        return dialog;
    }

    [Fact]
    public void CentroIntegrita_AperturaMostraAvvisoCloudNonVerificato()
    {
        try
        {
            var page = Open();
            Assert.Contains("CENTRO INTEGRITÀ TestLoom", page.Details);
            Assert.Contains("Snapshot ZIP locali", page.Details);
            Assert.Contains("Bozze di emergenza locali", page.Details);
            Assert.Contains("OneDrive", page.Details);
            Assert.Contains("NON certificano", page.Details);
            Assert.Contains("Sincronizzazione della cartella condivisa NON verificata", page.Status);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CentroIntegrita_AperturaMostraAvvisoCloudNonVerificato), ex); }
    }

    [Fact]
    public void CentroIntegrita_AllegatiRimangonoInalteratiDopoRefresh()
    {
        try
        {
            var root = new DataPaths(Workspace.WorkspaceDir);
            var attachment = Path.Combine(root.AllegatiRootDir, "test", "QA", "immutable.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(attachment)!);
            File.WriteAllText(attachment, "NO-CHANGES-TO-ATTACHMENTS");
            var before = SHA256.HashData(File.ReadAllBytes(attachment));
            var timestamp = File.GetLastWriteTimeUtc(attachment);

            var page = Open();
            Assert.Contains("Allegati elencati (solo metadati)", page.Details);
            page.Refresh();
            Wait.ForCondition(() => page.CanRefresh && page.Status.Contains("completata"),
                "diagnostica aggiornata");

            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(attachment)));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(attachment));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CentroIntegrita_AllegatiRimangonoInalteratiDopoRefresh), ex); }
    }

    [Fact]
    public void CentroIntegrita_AggiornaConteggioBozze_SenzaRipubblicarle()
    {
        try
        {
            var draft1 = FailedSaveDraftService.Save(Workspace.WorkspaceDir, "change-request",
                Guid.NewGuid(), 3, "QA", new { Uid = Guid.NewGuid(), Id = "QA-CR1" }, "offline");
            var page = Open();
            Assert.Contains("Bozze di emergenza locali: 1", page.Details);
            var filesBefore = Directory.EnumerateFiles(Workspace.WorkspaceDir, "*",
                SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var draft2 = FailedSaveDraftService.Save(Workspace.WorkspaceDir, "segnalazione",
                Guid.NewGuid(), 4, "QA", new { Id = "QA-NC2" }, "conflitto");
            page.Refresh();
            Wait.ForCondition(() => page.Details.Contains("Bozze di emergenza locali: 2"),
                "due bozze locali dopo refresh");

            Assert.True(File.Exists(draft1));
            Assert.True(File.Exists(draft2));
            Assert.True(filesBefore.SetEquals(Directory.EnumerateFiles(
                Workspace.WorkspaceDir, "*", SearchOption.AllDirectories)));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CentroIntegrita_AggiornaConteggioBozze_SenzaRipubblicarle), ex); }
    }

    [Fact]
    public void CentroIntegrita_RilevaBloccoVisualizzatoreSenzaToccareModelli()
    {
        try
        {
            var root = Workspace.WorkspaceDir;
            var before = Directory.EnumerateFiles(root, "*.json")
                .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

            WorkspaceReadOnlyService.SetEnabled(root, true);
            try
            {
                var page = Open();
                Assert.Contains("Modalità visualizzatore su questo PC: attiva", page.Details);
                Assert.All(before, file =>
                    Assert.Equal(file.Value, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Key)))));
            }
            finally { WorkspaceReadOnlyService.SetEnabled(root, false); }
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CentroIntegrita_RilevaBloccoVisualizzatoreSenzaToccareModelli), ex); }
    }

    [Fact]
    public void CentroIntegrita_ChiusuraConsenteRitornoAllaHome()
    {
        try
        {
            var page = Open();
            page.Close();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Integrity_Title_Text"),
                "finestra centro integrità chiusa");
            var shell = new MainShellPage(App.WaitForMainWindow());
            shell.GoToHome();
            Assert.False(shell.HasNavigationError);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CentroIntegrita_ChiusuraConsenteRitornoAllaHome), ex); }
    }
}
