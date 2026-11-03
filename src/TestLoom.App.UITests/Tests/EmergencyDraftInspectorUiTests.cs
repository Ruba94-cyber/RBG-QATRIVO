using System.Security.Cryptography;
using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Verifica sul RBG - QATRIVO.exe reale che le copie di recupero rimangano locali,
/// consultabili e immutate. 31 record validi + 1 corrotto, per verificare
/// paginazione a 30 elementi, errore sicuro e isolamento dal dataset.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
[Trait("Category", "Recovery")]
public sealed class EmergencyDraftInspectorUiTests : UiTestBase
{
    public EmergencyDraftInspectorUiTests()
        : base(CreaWorkspaceConBozze()) { }

    private static TestWorkspace CreaWorkspaceConBozze()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(EmergencyDraftInspectorUiTests));
        for (var i = 0; i < 31; i++)
            FailedSaveDraftService.Save(ws.WorkspaceDir,
                i % 2 == 0 ? "change-request" : "segnalazione",
                Guid.NewGuid(), i + 1, "QA FlaUI",
                new { Titolo = "DRAFT_QA_" + i.ToString("D2") },
                "Errore testato senza perdita dati");

        var damaged = Path.Combine(FailedSaveDraftService.LocalDirectory(ws.WorkspaceDir), "zz_corrupt.json");
        File.WriteAllText(damaged, "{ invalid");
        return ws;
    }

    private (MainShellPage Shell, FailedSaveDraftInspectorPage Dialog) Open()
    {
        var window = App.WaitForMainWindow();
        var shell = new MainShellPage(window);
        var settings = shell.GoToImpostazioni();
        settings.VaiA("generale");
        var dialog = new Post653ImpostazioniPage(window, App).EsaminaBozze();
        Wait.ForCondition(() => dialog.Status.Contains("Pagina 1", StringComparison.OrdinalIgnoreCase),
            "bozze locali lette nel visualizzatore");
        return (shell, dialog);
    }

    [Fact]
    public void Apertura_ElencoMostraIFileAncheCorrotti_AnteprimaNonAutomatica()
    {
        try
        {
            var (_, dialog) = Open();
            Assert.Contains("30 bozze mostrate", dialog.Status);
            Assert.Equal(32, Directory.EnumerateFiles(
                FailedSaveDraftService.LocalDirectory(Workspace.WorkspaceDir), "*.json").Count());
            Assert.Contains("NON LEGGIBILE", dialog.List.Items[0].Name ?? "");
            dialog.SelectRow(1);
            Assert.Contains("Revisione attesa:", dialog.Details);
            Assert.Contains("Anteprima non caricata", dialog.Preview);
            Assert.True(dialog.CanPreview);
            dialog.Close();
            Wait.ForCondition(() => App.IsWindowClosedByContent("DraftInspector_Title_Text"),
                "chiusura visualizzatore bozze");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Apertura_ElencoMostraIFileAncheCorrotti_AnteprimaNonAutomatica), ex); }
    }

    [Fact]
    public void BozzaIntegra_PreviewMostraPayloadMaNonModificaLaCopia()
    {
        try
        {
            var directory = FailedSaveDraftService.LocalDirectory(Workspace.WorkspaceDir);
            var baseline = Directory.EnumerateFiles(directory, "*.json")
                .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

            var (_, dialog) = Open();
            dialog.SelectRow(1);
            dialog.LoadPreview();
            Wait.ForCondition(() => dialog.Preview.Contains("DRAFT_QA_", StringComparison.Ordinal),
                "anteprima JSON di una bozza valida");
            Assert.Equal(baseline.Count, Directory.EnumerateFiles(directory, "*.json").Count());
            Assert.All(baseline, pair =>
                Assert.Equal(pair.Value, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key)))));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(BozzaIntegra_PreviewMostraPayloadMaNonModificaLaCopia), ex); }
    }

    [Fact]
    public void BozzaCorrotta_NonHaPreviewEDialogoRestaNavigabile()
    {
        try
        {
            var (_, dialog) = Open();
            dialog.SelectRow(0);
            Assert.False(dialog.CanPreview);
            Assert.Contains("non è leggibile", dialog.Preview, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(FailedSaveDraftService.LocalDirectory(Workspace.WorkspaceDir),
                "zz_corrupt.json")));
            dialog.SelectRow(1);
            Assert.True(dialog.CanPreview);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(BozzaCorrotta_NonHaPreviewEDialogoRestaNavigabile), ex); }
    }

    [Fact]
    public void Paginazione_SecondaPaginaEIndietro_NonCancellaBozze()
    {
        try
        {
            var directory = FailedSaveDraftService.LocalDirectory(Workspace.WorkspaceDir);
            var (_, dialog) = Open();
            Assert.False(dialog.CanPrevious);
            Assert.True(dialog.CanNext);

            dialog.Next();
            Wait.ForCondition(() => dialog.Status.Contains("Pagina 2"), "seconda pagina");
            Assert.Contains("2 bozze mostrate", dialog.Status);
            Assert.True(dialog.CanPrevious);

            dialog.Previous();
            Wait.ForCondition(() => dialog.Status.Contains("Pagina 1"), "ritorno prima pagina");
            Assert.Contains("30 bozze mostrate", dialog.Status);
            Assert.Equal(32, Directory.EnumerateFiles(directory, "*.json").Count());
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Paginazione_SecondaPaginaEIndietro_NonCancellaBozze), ex); }
    }

    [Fact]
    public void Aggiorna_InserisceUnaNuovaBozzaSenzaPubblicarlaSulDataset()
    {
        try
        {
            var (_, dialog) = Open();
            var workspaceFiles = Directory.EnumerateFiles(Workspace.WorkspaceDir, "*", SearchOption.AllDirectories)
                .Select(path => path).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var nuovoUid = Guid.NewGuid();
            FailedSaveDraftService.Save(Workspace.WorkspaceDir, "change-request", nuovoUid, 42,
                "QA FlaUI", new { Titolo = "DRAFT_QA_NUOVA" }, "Test refresh");
            dialog.Refresh();
            Wait.ForCondition(() => dialog.List.Items.Any(item =>
                (item.Name ?? "").Contains(nuovoUid.ToString("N"), StringComparison.OrdinalIgnoreCase)),
                "la nuova bozza locale è comparsa dopo il refresh");
            Assert.Equal(33, Directory.EnumerateFiles(
                FailedSaveDraftService.LocalDirectory(Workspace.WorkspaceDir), "*.json").Count());
            Assert.True(workspaceFiles.SetEquals(Directory.EnumerateFiles(
                Workspace.WorkspaceDir, "*", SearchOption.AllDirectories)),
                "La bozza locale non deve creare documenti condivisi");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Aggiorna_InserisceUnaNuovaBozzaSenzaPubblicarlaSulDataset), ex); }
    }

    [Fact]
    public void ChiusuraFinestra_ConsenteNavigazioneNormaleDopoLIspezione()
    {
        try
        {
            var (shell, dialog) = Open();
            dialog.SelectRow(1);
            dialog.Close();
            Wait.ForCondition(() => App.IsWindowClosedByContent("DraftInspector_Title_Text"),
                "finestra inspector chiusa");
            shell.GoToHome();
            Assert.False(shell.HasNavigationError);
            shell.GoToTestList();
            Assert.False(shell.HasNavigationError);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ChiusuraFinestra_ConsenteNavigazioneNormaleDopoLIspezione), ex); }
    }
}
