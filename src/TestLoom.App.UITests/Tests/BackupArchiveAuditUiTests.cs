using System.IO.Compression;
using System.Security.Cryptography;
using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// True UIA3 tests for the read-only ZIP verification in Settings.
/// All inputs are synthetic and live outside the shared test workspace.
/// ZIPs are NEVER extracted by the action under test.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
[Trait("Category", "Backup")]
public sealed class BackupArchiveAuditUiTests : UiTestBase
{
    public BackupArchiveAuditUiTests()
        : base(TestWorkspace.CreateLoggedIn(nameof(BackupArchiveAuditUiTests))) { }

    private Post653ImpostazioniPage OpenSettings()
    {
        var window = App.WaitForMainWindow();
        var settings = new MainShellPage(window).GoToImpostazioni();
        settings.VaiA("generale");
        return new Post653ImpostazioniPage(window, App);
    }

    private string CreateZip(bool valid)
    {
        var localDir = WorkspaceLocalPaths.GetBackupDirectory(Workspace.WorkspaceDir);
        Directory.CreateDirectory(localDir);
        var path = Path.Combine(localDir, "TestLoom_snapshot_20261010_123000.zip");
        if (valid)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var stream = new StreamWriter(zip.CreateEntry("snapshot-manifest.txt").Open()))
                stream.Write("TestLoom QA snapshot");
            using (var stream = new StreamWriter(zip.CreateEntry("nonconformita/nc_1.json").Open()))
                stream.Write("{\"Id\":\"NC-TEST\"}");
        }
        else File.WriteAllBytes(path, new byte[] { 1, 3, 3, 7 });
        return path;
    }

    [Fact]
    public void ZipValido_InterfacciaConfermaHashSenzaEstrazione()
    {
        try
        {
            var path = CreateZip(true);
            var bytes = File.ReadAllBytes(path);
            var ui = OpenSettings();
            ui.VerificaUltimoBackup();
            Wait.ForCondition(() => ui.MessaggioBackupVerifica.Contains("Backup locale verificato"),
                "audit ZIP locale terminato", TimeSpan.FromSeconds(30));
            Assert.Contains("SHA-256", ui.MessaggioBackupVerifica);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.False(File.Exists(Path.Combine(
                Workspace.WorkspaceDir, "nonconformita", "nc_1.json")),
                "La verifica di un archivio ZIP non deve estrarre alcun documento.");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ZipValido_InterfacciaConfermaHashSenzaEstrazione), ex); }
    }

    [Fact]
    public void ZipCorrotto_MostraErroreENonCancellaLoSnapshot()
    {
        try
        {
            var path = CreateZip(false);
            var before = File.ReadAllBytes(path);
            var ui = OpenSettings();
            ui.VerificaUltimoBackup();
            Wait.ForCondition(() => ui.ErroreTesto.Contains("Verifica ZIP NON SUPERATA"),
                "avviso archivio corrotto", TimeSpan.FromSeconds(30));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ZipCorrotto_MostraErroreENonCancellaLoSnapshot), ex); }
    }

    [Fact]
    public void NessunoZip_VisualizzaMessaggioSenzaCreareCartellaBackup()
    {
        try
        {
            var backup = WorkspaceLocalPaths.GetBackupDirectory(Workspace.WorkspaceDir);
            // Workspace sintetico: nessun backup viene creato dalla fixture.
            Assert.False(Directory.Exists(backup) &&
                Directory.EnumerateFiles(backup, "TestLoom_snapshot_*.zip").Any());
            var ui = OpenSettings();
            ui.VerificaUltimoBackup();
            Wait.ForCondition(() => ui.ErroreTesto.Contains("Nessun", StringComparison.OrdinalIgnoreCase),
                "avviso assenza backup", TimeSpan.FromSeconds(30));
            Assert.Empty(Directory.Exists(backup)
                ? Directory.EnumerateFiles(backup, "TestLoom_snapshot_*.zip")
                : Array.Empty<string>());
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(NessunoZip_VisualizzaMessaggioSenzaCreareCartellaBackup), ex); }
    }
}
