using System.Security.Cryptography;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Suite E2E del codice post-6.5.3 con vero TestLoom.exe/Avalonia/UIA3,
/// workspace temporaneo per test, nessun accesso a OneDrive reale.
/// Verifica che i controlli di sicurezza agiscano anche sul filesystem.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
[Trait("Category", "Safety")]
public sealed class Post653ImpostazioniSafetyUiTests : UiTestBase
{
    public Post653ImpostazioniSafetyUiTests()
        : base(TestWorkspace.CreateLoggedIn(nameof(Post653ImpostazioniSafetyUiTests))) { }

    private (MainShellPage Shell, Post653ImpostazioniPage Settings) Apri()
    {
        var window = App.WaitForMainWindow();
        var shell = new MainShellPage(window);
        var imp = shell.GoToImpostazioni();
        imp.VaiA("generale");
        return (shell, new Post653ImpostazioniPage(window, App));
    }

    [Fact]
    public void Visualizzatore_AnnullaNonAbilitaIlBlocco()
    {
        try
        {
            var (_, ui) = Apri();
            Assert.True(ui.ViewerAttivaVisibile);
            Assert.False(WorkspaceReadOnlyService.IsEnabled(Workspace.WorkspaceDir));

            ui.AttivaViewer(confirm: false);

            Assert.False(WorkspaceReadOnlyService.IsEnabled(Workspace.WorkspaceDir));
            Assert.True(ui.ViewerAttivaVisibile);
            Assert.False(ui.ViewerDisattivaVisibile);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Visualizzatore_AnnullaNonAbilitaIlBlocco), ex); }
    }

    [Fact]
    public void Visualizzatore_ConfermaBloccaLeScrittureEPermetteLaConsultazione()
    {
        try
        {
            var (shell, ui) = Apri();
            ui.AttivaViewer(confirm: true);

            Wait.ForCondition(() => WorkspaceReadOnlyService.IsEnabled(Workspace.WorkspaceDir)
                && ui.ViewerDisattivaVisibile, "modalita sola lettura persistita");
            Assert.False(WorkspaceWritePolicy.CanWrite(new DataPaths(Workspace.WorkspaceDir)));
            Assert.Contains("lettura", ui.StatoLocale, StringComparison.OrdinalIgnoreCase);

            // Il viewer non deve nascondere le pagine o perdere dati.
            var lista = shell.GoToTestList();
            Assert.True(ElementFinders.ExistsById(App.WaitForMainWindow(), "TestList_Title_Text"));
            Assert.False(shell.HasNavigationError);
            shell.GoToHome();
            Assert.False(shell.HasNavigationError);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Visualizzatore_ConfermaBloccaLeScrittureEPermetteLaConsultazione), ex); }
    }

    [Fact]
    public void Visualizzatore_AnnullaDisattivazioneMantieneBlocco()
    {
        try
        {
            var (_, ui) = Apri();
            ui.AttivaViewer(confirm: true);
            Wait.ForCondition(() => ui.ViewerDisattivaVisibile, "pulsante disattivazione visualizzatore");

            ui.DisattivaViewer(confirm: false);
            Assert.True(WorkspaceReadOnlyService.IsEnabled(Workspace.WorkspaceDir));
            Assert.True(ui.ViewerDisattivaVisibile);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Visualizzatore_AnnullaDisattivazioneMantieneBlocco), ex); }
    }

    [Fact]
    public void Visualizzatore_ConfermaDisattivazioneRipristinaIlPreflightNormale()
    {
        try
        {
            var (_, ui) = Apri();
            ui.AttivaViewer(confirm: true);
            Wait.ForCondition(() => WorkspaceReadOnlyService.IsEnabled(Workspace.WorkspaceDir),
                "marker sola lettura locale scritto");

            ui.DisattivaViewer(confirm: true);

            Wait.ForCondition(() => !WorkspaceReadOnlyService.IsEnabled(Workspace.WorkspaceDir)
                && ui.ViewerAttivaVisibile, "blocco locale disattivato");
            Assert.Contains("cloud non verificato", ui.StatoLocale, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Visualizzatore_ConfermaDisattivazioneRipristinaIlPreflightNormale), ex); }
    }

    [Fact]
    public void VerificaLocale_NonDichiaraLaSincronizzazioneCloudComeCompletata()
    {
        try
        {
            var (_, ui) = Apri();
            ui.VerificaDatiLocali();

            Assert.Contains("cloud non verificato", ui.StatoLocale, StringComparison.OrdinalIgnoreCase);
            Assert.False(WorkspaceReadOnlyService.IsEnabled(Workspace.WorkspaceDir));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(VerificaLocale_NonDichiaraLaSincronizzazioneCloudComeCompletata), ex); }
    }

    [Fact]
    public void PinningDati_AnnullaConferma_NonToccaGliAllegati()
    {
        try
        {
            var allegato = Path.Combine(Workspace.WorkspaceDir, "allegati", "test", "QA", "evidenza.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(allegato)!);
            File.WriteAllText(allegato, "contenuto");
            var attr = File.GetAttributes(allegato);
            var (_, ui) = Apri();

            ui.RichiediPinDati(confirm: false);

            Assert.Equal(attr, File.GetAttributes(allegato));
            Assert.Equal("contenuto", File.ReadAllText(allegato));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(PinningDati_AnnullaConferma_NonToccaGliAllegati), ex); }
    }

    [Fact]
    public void BackupEsterno_CopiaVerificataConHash_EConservaLoSnapshotLocale()
    {
        try
        {
            var (_, ui) = Apri();
            var outDir = Path.Combine(Workspace.RootDir, "backup-qa-esterno");

            ui.CreaBackupLocale();
            var sourceFolder = WorkspaceLocalPaths.GetBackupDirectory(Workspace.WorkspaceDir);
            Wait.ForCondition(() => Directory.Exists(sourceFolder) &&
                Directory.EnumerateFiles(sourceFolder, "TestLoom_snapshot_*.zip").Any(),
                "snapshot ZIP locale pubblicato", TimeSpan.FromSeconds(45));
            var before = Directory.EnumerateFiles(sourceFolder, "TestLoom_snapshot_*.zip")
                .OrderByDescending(File.GetLastWriteTimeUtc).First();
            var beforeHash = SHA256.HashData(File.ReadAllBytes(before));

            ui.ImpostaPercorsoBackupEsterno(outDir);
            ui.SalvaPercorsoBackupEsterno();
            ui.CopiaUltimoBackupEsterno();

            Wait.ForCondition(() => Directory.Exists(outDir) &&
                Directory.EnumerateFiles(outDir, "TestLoom_snapshot_*.zip").Any(),
                "copia ZIP esterna verificata", TimeSpan.FromSeconds(30));
            var mirrored = Directory.EnumerateFiles(outDir, "TestLoom_snapshot_*.zip").Single();
            Assert.True(beforeHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(mirrored))),
                "L'hash SHA-256 della copia ZIP deve corrispondere al backup locale.");
            Assert.True(File.Exists(before), "Il backup locale non deve essere rimosso.");
            Assert.Empty(Directory.EnumerateFiles(outDir, "*.partial"));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(BackupEsterno_CopiaVerificataConHash_EConservaLoSnapshotLocale), ex); }
    }

    [Fact]
    public void BackupEsterno_RifiutaDestinazioneInternaAlDataset_ConservandoLoSnapshotLocale()
    {
        try
        {
            var (_, ui) = Apri();
            ui.CreaBackupLocale();
            var sourceFolder = WorkspaceLocalPaths.GetBackupDirectory(Workspace.WorkspaceDir);
            Wait.ForCondition(() => Directory.Exists(sourceFolder) &&
                Directory.EnumerateFiles(sourceFolder, "TestLoom_snapshot_*.zip").Any(),
                "ZIP locale creato");
            var destination = Path.Combine(Workspace.WorkspaceDir, "backup-vietato");
            ui.ImpostaPercorsoBackupEsterno(destination);
            ui.CopiaUltimoBackupEsterno();

            Wait.ForCondition(() => !string.IsNullOrWhiteSpace(ui.ErroreTesto),
                "messaggio di rifiuto del percorso insicuro");
            Assert.False(Directory.Exists(destination));
            Assert.NotEmpty(Directory.EnumerateFiles(sourceFolder, "*.zip"));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(BackupEsterno_RifiutaDestinazioneInternaAlDataset_ConservandoLoSnapshotLocale), ex); }
    }

    [Fact]
    public void BackupEsterno_IlPercorsoSiSalvaNellaConfigurazioneDiQuestaPostazione()
    {
        try
        {
            var (_, ui) = Apri();
            var dest = Path.Combine(Workspace.RootDir, "solo-test");
            ui.ImpostaPercorsoBackupEsterno(dest);
            ui.SalvaPercorsoBackupEsterno();

            Wait.ForCondition(() => new AppConfigService(Workspace.ConfigPath).Load().PercorsoBackupEsterno == dest,
                "percorso di copia esterna nella configurazione isolata");
            Assert.Equal(dest, ui.PercorsoBackupEsterno);
            Assert.True(ui.BozzeEmergenzaAccessibili);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(BackupEsterno_IlPercorsoSiSalvaNellaConfigurazioneDiQuestaPostazione), ex); }
    }
}
