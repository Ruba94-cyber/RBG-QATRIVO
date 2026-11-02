using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// UIA3 true-app conflict regression: an external OneDrive-like fork appears
/// after editor opened, but before Save. Never contacts the actual corporate PC.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Concurrency")]
[Trait("Category", "Regression")]
public sealed class ConflictCopyLateArrivalUiTests : UiTestBase
{
    public ConflictCopyLateArrivalUiTests()
        : base(CreateFixture()) { }

    private static TestWorkspace CreateFixture()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(ConflictCopyLateArrivalUiTests));
        using var state = new AppState(new AppConfigService(ws.ConfigPath),
            runBackgroundMaintenance: false);
        var list = state.Master.TestTemplate.Load();
        list.Add(new TestTemplate { Nome = "QA-CR-CONCURRENCY", IsChangeRequest = true,
            ModificatoDa = "FlaUI" });
        state.Master.TestTemplate.Save(list);
        return ws;
    }

    [Fact]
    public void CrSalva_SeArrivaForkOneDriveDuranteEditor_NonSovrascriveEConservaBozza()
    {
        try
        {
            var main = App.WaitForMainWindow();
            var editor = new MainShellPage(main).GoToAnomalie().NuovaChangeRequest();
            editor.SetTitolo("QA-COPY-CONFLICT-ORIGINAL");
            editor.SetDescrizione("CR per regressione fork inter-postazione");
            editor.AggiungiEcu(App, TestWorkspace.DefaultEcuName);
            editor.Salva();
            var dir = new DataPaths(Workspace.WorkspaceDir).ChangeRequestDir;
            Wait.ForCondition(() => Directory.EnumerateFiles(dir, "cr_*.json").Any(),
                "CR iniziale salvata nella fixture");
            var original = Assert.Single(Directory.EnumerateFiles(dir, "cr_*.json"));
            var cr = JsonIo.TryRead<ChangeRequest>(original, out var error)!;
            Assert.Null(error);

            var late = original.Replace(".json", " (Laptop-2).json", StringComparison.OrdinalIgnoreCase);
            var altered = EntityStateFingerprint.Clone(cr);
            altered.Titolo = "VERSIONE_DIVERGENTE_ALTRA_POSTAZIONE";
            JsonIo.WriteAtomic(late, altered);
            var oldBytes = File.ReadAllBytes(original);
            var forkBytes = File.ReadAllBytes(late);

            editor.SetTitolo("MODIFICA_LOCALE_NON_PERDERE");
            editor.Salva();
            Wait.ForCondition(() =>
                (ElementFinders.WaitById(main, "Cr_Errore_Text").Name ?? "")
                    .Contains("conflitto", StringComparison.OrdinalIgnoreCase) ||
                (ElementFinders.WaitById(main, "Cr_Errore_Text").Name ?? "")
                    .Contains("divergenti", StringComparison.OrdinalIgnoreCase),
                "avviso salvataggio CR con fork visibile");

            Assert.Equal(oldBytes, File.ReadAllBytes(original));
            Assert.Equal(forkBytes, File.ReadAllBytes(late));
            var draftDir = FailedSaveDraftService.LocalDirectory(Workspace.WorkspaceDir);
            Wait.ForCondition(() => Directory.Exists(draftDir) &&
                Directory.EnumerateFiles(draftDir, "change-request_*.json").Any(),
                "bozza non pubblicata salvata solo localmente");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(
                nameof(CrSalva_SeArrivaForkOneDriveDuranteEditor_NonSovrascriveEConservaBozza), ex);
        }
    }
}
