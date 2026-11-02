using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Card "Conflitti fra postazioni" nella vera UI: due journal di device sintetici producono un fork su una NC
/// (stessa base, esiti diversi) nella cartella di test; il Master lo vede e lo chiude mantenendo la versione corrente.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
[Trait("Category", "MultiPc")]
public sealed class RecordForkUiTests : UiTestBase
{
    private readonly string _localA = Path.Combine(Path.GetTempPath(), "TestLoom_UITests", "RecordForkA_" + Guid.NewGuid().ToString("N"));
    private readonly string _localB = Path.Combine(Path.GetTempPath(), "TestLoom_UITests", "RecordForkB_" + Guid.NewGuid().ToString("N"));

    public RecordForkUiTests()
        : base(TestWorkspace.CreateLoggedIn(nameof(RecordForkUiTests))) { }

    private void CreaForkNc(string id)
    {
        var paths = new DataPaths(Workspace.WorkspaceDir);
        var repoA = new NonConformitaRepository(paths) { RecordJournal = new DomainRecordJournalService(paths, _localA, "PC-A-QA") };
        repoA.Save(new NonConformita
        {
            Id = id, Owner = "Base", Descrizione = "Fork QA", Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.Confermata, DataApertura = DateTime.Now
        });
        var path = paths.NonConformitaFile(id);
        var baseBytes = File.ReadAllBytes(path);
        var a = repoA.GetById(id)!.Clona();
        a.Owner = "Da PC A";
        repoA.Save(a, a.RevisioneDati);

        File.WriteAllBytes(path, baseBytes); // OneDrive consegna in ritardo la base all'altro PC
        var repoB = new NonConformitaRepository(paths) { RecordJournal = new DomainRecordJournalService(paths, _localB, "PC-B-QA") };
        repoB.RefreshPaths(new[] { path });
        var b = repoB.GetById(id)!.Clona();
        b.Owner = "Da PC B";
        repoB.Save(b, b.RevisioneDati);
    }

    [Fact]
    public void Master_VedeIlConflittoELoChiudeMantenendoLaVersioneCorrente()
    {
        try
        {
            CreaForkNc("NC-FORK-UI");
            var window = App.WaitForMainWindow();
            var settings = new MainShellPage(window).GoToImpostazioni();
            settings.VaiA("generale");

            ElementFinders.WaitButtonById(window, "RecordFork_Aggiorna_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "RecordFork_MantieniCorrente_Button"),
                "conflitto NC visibile nella card");
            Assert.Contains(window.FindAllDescendants(), e => (e.Name ?? "").Contains("NC-FORK-UI", StringComparison.Ordinal));

            ElementFinders.WaitButtonById(window, "RecordFork_MantieniCorrente_Button").SafeInvoke();
            var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            conferma.ConfirmYes();

            Wait.ForCondition(() => ElementFinders.ExistsById(window, "RecordFork_Nessuno_Text") &&
                                    !ElementFinders.ExistsById(window, "RecordFork_MantieniCorrente_Button"),
                "conflitto chiuso");
            var corrente = JsonIo.TryRead<NonConformita>(new DataPaths(Workspace.WorkspaceDir).NonConformitaFile("NC-FORK-UI"), out _);
            Assert.Equal("Da PC B", corrente!.Owner); // mantenuta la versione corrente, nessuna riscrittura
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Master_VedeIlConflittoELoChiudeMantenendoLaVersioneCorrente), ex); }
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var dir in new[] { _localA, _localB })
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
