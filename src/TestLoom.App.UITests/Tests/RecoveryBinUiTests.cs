using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Impostazioni → Recovery con copie reali nel cestino: due Test cancellati (il file è rimosso dal workspace e archiviato con
/// <see cref="DomainRecoveryService.ArchiveOnly"/>). Uno si ripristina, l'altro si purga. Ogni esito si verifica sul disco
/// (file del Test e envelope in recovery-bin), non solo sull'elenco.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
[Trait("Area", "Recovery")]
public sealed class RecoveryBinUiTests : UiTestBase
{
    private const string IdRipristino = "QA-REC-1";
    private const string IdPurga = "QA-REC-2";

    public RecoveryBinUiTests() : base(CreaWorkspaceConCopieDiRecupero()) { }

    /// <summary>Crea due Test, li cancella dal workspace e li archivia nel cestino di recupero come farebbe TestLoom.</summary>
    private static TestWorkspace CreaWorkspaceConCopieDiRecupero()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(RecoveryBinUiTests), seedTestFixtures: true);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var template = TestCoverageSeed.UidTemplatePrincipale(state);
        TestCoverageSeed.SeedTest(state, IdRipristino, "Test da ripristinare", template, TestWorkspace.DefaultEcuName, Esito.Conforme);
        TestCoverageSeed.SeedTest(state, IdPurga, "Test da purgare", template, TestWorkspace.DefaultEcuName, Esito.Conforme);
        foreach (var id in new[] { IdRipristino, IdPurga })
        {
            var sorgente = FileDelTest(ws, id);
            DomainRecoveryService.ArchiveOnly(state.Paths, sorgente, DomainRecoveryCategory.TestCase, "UITests");
            File.Delete(sorgente);
        }
        return ws;
    }

    private static string FileDelTest(TestWorkspace ws, string id) =>
        TestCoverageSeed.FileTestCase(ws).Single(f => Path.GetFileName(f).Equals($"testcase_{id}.json", StringComparison.Ordinal));

    /// <summary>Record di recupero del Test indicato, letti dal cestino persistito con un AppState nuovo.</summary>
    private DomainRecoveryRecord? RecordPer(string id)
    {
        using var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
        return DomainRecoveryService.GetAll(state.Paths).SingleOrDefault(r => Path.GetFileName(r.OriginalRelativePath) == $"testcase_{id}.json");
    }

    private RecoveryBinPage ApriCestino()
    {
        var window = App.WaitForMainWindow();
        new MainShellPage(window).GoToImpostazioni().VaiA("recovery");
        var cestino = new RecoveryBinPage(window);
        cestino.Attendi();
        return cestino;
    }

    [Fact]
    public void Recovery_Ripristina_RiportaIlTestNelWorkspaceEMarcaLaCopiaRipristinata()
    {
        try
        {
            var cestino = ApriCestino();
            cestino.Ripristina(cestino.RigaPer($"testcase_{IdRipristino}.json"));
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();

            Wait.ForCondition(() => TestCoverageSeed.TrovaTestCase(Workspace, IdRipristino) != null, "il Test ripristinato torna nel workspace");
            Wait.ForCondition(() => RecordPer(IdRipristino)?.RestoredUtc.HasValue == true, "il record di recupero è marcato ripristinato");
            Assert.NotNull(TestCoverageSeed.TrovaTestCase(Workspace, IdRipristino));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Recovery_Ripristina_RiportaIlTestNelWorkspaceEMarcaLaCopiaRipristinata), ex);
        }
    }

    [Fact]
    public void Recovery_Purga_ConfermaNoNonCancella_ConfermaSiRimuoveLaCopia()
    {
        try
        {
            var cestino = ApriCestino();
            cestino.Purga(cestino.RigaPer($"testcase_{IdPurga}.json"));
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmNo();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura della conferma dopo No");
            Assert.NotNull(RecordPer(IdPurga));

            cestino.Purga(cestino.RigaPer($"testcase_{IdPurga}.json"));
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => RecordPer(IdPurga) == null, "la copia di recupero purgata non esiste più nel cestino");
            Assert.Null(TestCoverageSeed.TrovaTestCase(Workspace, IdPurga));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Recovery_Purga_ConfermaNoNonCancella_ConfermaSiRimuoveLaCopia), ex);
        }
    }

    [Fact]
    public void Recovery_PurgaAnnullata_DopoAggiornaMantieneRigaEEnvelopePersistito()
    {
        try
        {
            var nomeEnvelope = $"testcase_{IdPurga}.json";
            var cestino = ApriCestino();
            cestino.Purga(cestino.RigaPer(nomeEnvelope));
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmNo();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura della conferma dopo No");

            Assert.NotNull(RecordPer(IdPurga));
            Assert.NotNull(cestino.RigaPer(nomeEnvelope));
            Assert.Null(TestCoverageSeed.TrovaTestCase(Workspace, IdPurga));

            ElementFinders.WaitButtonById(App.WaitForMainWindow(), "Recovery_Aggiorna_Button").SafeInvoke();

            Assert.NotNull(cestino.RigaPer(nomeEnvelope));
            Assert.NotNull(RecordPer(IdPurga));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Recovery_PurgaAnnullata_DopoAggiornaMantieneRigaEEnvelopePersistito), ex);
        }
    }
}
