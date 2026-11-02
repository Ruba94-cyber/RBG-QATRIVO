using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Real UIA3 consent flow; recovery candidates stay under local app data,
/// never in the shared authoritative corporate workspace.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Recovery")]
[Trait("Category", "Regression")]
public sealed class DraftRecoveryPreparationUiTests : UiTestBase
{
    private const string Id = "CR-QA-LOCAL-RECOVERY";
    public DraftRecoveryPreparationUiTests() : base(Create()) { }

    private static TestWorkspace Create()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(DraftRecoveryPreparationUiTests));
        var root = new DataPaths(ws.WorkspaceDir);
        var original = new ChangeRequest { Id = Id, Titolo = "Originale",
            RevisioneDati = 3 };
        JsonIo.WriteAtomic(root.ChangeRequestFile(Id), original);
        var proposed = EntityStateFingerprint.Clone(original);
        proposed.Titolo = "PROPOSTA_LOCAL_ONLY";
        FailedSaveDraftService.Save(ws.WorkspaceDir, "change-request",
            original.Uid, 3, "QA", proposed, "Conflitto", baseSnapshot: original);
        return ws;
    }

    private FailedSaveDraftInspectorPage Open()
    {
        var main = App.WaitForMainWindow();
        new MainShellPage(main).GoToImpostazioni().VaiA("generale");
        var page = new Post653ImpostazioniPage(main, App).EsaminaBozze();
        Wait.ForCondition(() => page.Status.Contains("Pagina 1"), "bozza disponibile");
        page.SelectRow(0);
        return page;
    }

    [Fact]
    public void PreparaRecupero_AnnullaConferma_NonCreaCopia()
    {
        try
        {
            var folder = Path.Combine(FailedSaveDraftService.LocalDirectory(Workspace.WorkspaceDir),
                "prepared");
            var page = Open();
            page.PrepareLocal();
            var popup = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            popup.ConfirmNo();
            Wait.ForCondition(() => page.PreparedStatus.Contains("annullata"),
                "preparazione annullata senza creare file");
            Assert.False(Directory.Exists(folder));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(PreparaRecupero_AnnullaConferma_NonCreaCopia), ex);
        }
    }

    [Fact]
    public void PreparaRecupero_ConfermaCreaSoloCopiaNonAutorevole()
    {
        try
        {
            var canonical = new DataPaths(Workspace.WorkspaceDir).ChangeRequestFile(Id);
            var bytes = File.ReadAllBytes(canonical);
            var page = Open();
            page.PrepareLocal();
            var popup = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            popup.ConfirmYes();
            Wait.ForCondition(() => page.PreparedStatus.Contains("Copia locale NON applicata"),
                "candidate staged in local app data");
            var folder = Path.Combine(FailedSaveDraftService.LocalDirectory(Workspace.WorkspaceDir),
                "prepared");
            Assert.Single(Directory.EnumerateFiles(folder, "NON_AUTOREVOLE_*.json"));
            Assert.Equal(bytes, File.ReadAllBytes(canonical));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(PreparaRecupero_ConfermaCreaSoloCopiaNonAutorevole), ex);
        }
    }
}
