using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Il visualizzatore di copie fallite è permesso in lettura anche al Viewer;
/// i suoi permessi non si estendono mai alla pubblicazione di un record.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
[Trait("Category", "Recovery")]
public sealed class EmergencyDraftInspectorViewerUiTests : UiTestBase
{
    public EmergencyDraftInspectorViewerUiTests()
        : base(TestWorkspace.CreateLoggedInAs(nameof(EmergencyDraftInspectorViewerUiTests),
            RuoloOperatore.Viewer)) { }

    private (MainShellPage Shell, FailedSaveDraftInspectorPage Dialog) Open()
    {
        var window = App.WaitForMainWindow();
        var shell = new MainShellPage(window);
        shell.GoToImpostazioni().VaiA("generale");
        var dialog = new Post653ImpostazioniPage(window, App).EsaminaBozze();
        Wait.ForCondition(() => dialog.Status.Contains("Pagina 1"),
            "visualizzatore sola lettura disponibile a un Viewer");
        return (shell, dialog);
    }

    [Fact]
    public void Viewer_SenzaBozze_VedeFinestraVuotaSenzaCreareDirectoryLocali()
    {
        try
        {
            var directory = FailedSaveDraftService.LocalDirectory(Workspace.WorkspaceDir);
            Assert.False(Directory.Exists(directory));
            var (_, dialog) = Open();
            Assert.Empty(dialog.List.Items);
            Assert.False(dialog.CanPreview);
            Assert.False(dialog.CanNext);
            Assert.False(Directory.Exists(directory));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Viewer_SenzaBozze_VedeFinestraVuotaSenzaCreareDirectoryLocali), ex); }
    }

    [Fact]
    public void Viewer_PuoLeggereUnaBozzaLocaleMaNonPubblicaDati()
    {
        try
        {
            var existing = Directory.EnumerateFiles(Workspace.WorkspaceDir, "*", SearchOption.AllDirectories)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var path = FailedSaveDraftService.Save(Workspace.WorkspaceDir, "change-request",
                Guid.NewGuid(), 9, "QA Viewer",
                new { Titolo = "BOZZA_VIEWER_READONLY" }, "Cloud non raggiungibile");

            var (_, dialog) = Open();
            dialog.SelectRow(0);
            dialog.LoadPreview();
            Wait.ForCondition(() => dialog.Preview.Contains("BOZZA_VIEWER_READONLY"),
                "anteprima locale della bozza");
            Assert.True(File.Exists(path));
            Assert.True(existing.SetEquals(Directory.EnumerateFiles(
                Workspace.WorkspaceDir, "*", SearchOption.AllDirectories)));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Viewer_PuoLeggereUnaBozzaLocaleMaNonPubblicaDati), ex); }
    }

    [Fact]
    public void AnteprimaNonMostraPayloadCambiatoDopoLaSelezione()
    {
        try
        {
            var path = FailedSaveDraftService.Save(Workspace.WorkspaceDir, "change-request",
                Guid.NewGuid(), 8, "QA Viewer",
                new { Titolo = "ORIGINALE" }, "Errore");
            var (_, dialog) = Open();
            dialog.SelectRow(0);

            // La stessa bozza viene alterata esternamente mentre il dialogo è aperto:
            // non deve sembrare l'anteprima dello snapshot originariamente elencato.
            var current = JsonIo.TryRead<FailedSaveDraftService.DraftEnvelope>(path, out var error)!;
            Assert.Null(error);
            current.PayloadJson = """{"Titolo":"MUTATO"}""";
            JsonIo.WriteAtomic(path, current);
            dialog.LoadPreview();

            Wait.ForCondition(() => dialog.Preview.Contains("cambiata", StringComparison.OrdinalIgnoreCase),
                "rilevamento bozza modificata dopo il listing");
            Assert.DoesNotContain("MUTATO", dialog.Preview);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(AnteprimaNonMostraPayloadCambiatoDopoLaSelezione), ex); }
    }

    [Fact]
    public void Viewer_NonVisualizzaBozzeDiAltriWorkspace()
    {
        try
        {
            var otherRoot = Workspace.WorkspaceDir + "-not-this-workspace";
            var otherDraft = FailedSaveDraftService.Save(otherRoot,
                "segnalazione", Guid.NewGuid(), 3, "QA", new { Titolo = "SECRET_OTHER_WS" }, "Error");
            try
            {
                var (_, dialog) = Open();
                Assert.Empty(dialog.List.Items);
                Assert.DoesNotContain("SECRET_OTHER_WS", dialog.Preview);
                Assert.True(File.Exists(otherDraft));
            }
            finally
            {
                var dir = FailedSaveDraftService.LocalDirectory(otherRoot);
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Viewer_NonVisualizzaBozzeDiAltriWorkspace), ex); }
    }
}
