using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Verifica nella vera UI che "Copia dalla precedente" NON sia sinonimo
/// di Ripeti, non erediti risultati di prova e non modifichi la logica
/// di validazione/calcolo degli esiti della 6.5.3.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
[Trait("Category", "CRUD")]
public sealed class Post653CopiaOperazioneUiTests : UiTestBase
{
    public Post653CopiaOperazioneUiTests()
        : base(TestWorkspace.CreateLoggedIn(nameof(Post653CopiaOperazioneUiTests))) { }

    private (MainShellPage Shell, TestEditorPage Editor) NuovoTest(string nome)
    {
        var shell = new MainShellPage(App.WaitForMainWindow());
        var editor = shell.GoToTestList().ClickNew();
        editor.ChooseTemplate();
        editor.GoToTab(CreateTestCaseFlow.TabDettagli);
        editor.SetDescrizioneBreve(nome);
        editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);
        editor.GoToTab(2);
        Wait.ForCondition(() => editor.NumeroCondizioni() == 1, "prima operazione creata");
        return (shell, editor);
    }

    [Fact]
    public void CopiaPrecedente_CreaOperazioneAutonoma_ConEsitoSempreDaEseguire()
    {
        try
        {
            var (_, ed) = NuovoTest("QA Copia operazione - autonomia");
            var prima = ed.GetUidCondizione(0);
            ed.SetEsitoCondizione(0, "Conforme");
            ed.SetNotaCondizione(0, "Evidenza di test da NON copiare");

            ed.CopiaDallaPrecedente();

            Wait.ForCondition(() => ed.NumeroCondizioni() == 2, "seconda operazione presente");
            Assert.NotEqual(prima, ed.GetUidCondizione(1));
            Assert.Contains("Da eseguire", ed.EsitoCondizioneTesto(1), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("", ed.GetNotaCondizione(1));
            Assert.Equal("Evidenza di test da NON copiare", ed.GetNotaCondizione(0));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CopiaPrecedente_CreaOperazioneAutonoma_ConEsitoSempreDaEseguire), ex); }
    }

    [Fact]
    public void CopiaPrecedente_DueCopieConsecutive_NonCondividonoIdentita()
    {
        try
        {
            var (_, ed) = NuovoTest("QA Copia operazione - due volte");
            ed.CopiaDallaPrecedente();
            Wait.ForCondition(() => ed.NumeroCondizioni() == 2, "prima copia");
            ed.CopiaDallaPrecedente();
            Wait.ForCondition(() => ed.NumeroCondizioni() == 3, "seconda copia");

            var ids = Enumerable.Range(0, 3).Select(ed.GetUidCondizione).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.All(Enumerable.Range(0, 3),
                i => Assert.Contains("Da eseguire", ed.EsitoCondizioneTesto(i), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CopiaPrecedente_DueCopieConsecutive_NonCondividonoIdentita), ex); }
    }

    [Fact]
    public void CopiaPrecedente_RimuovereLaCopiaNonCancellaLOriginale()
    {
        try
        {
            var (_, ed) = NuovoTest("QA Copia operazione - rimozione");
            ed.SetNotaCondizione(0, "Restare");
            ed.CopiaDallaPrecedente();
            Wait.ForCondition(() => ed.NumeroCondizioni() == 2, "copia creata");
            ed.RimuoviCondizione(1);
            Wait.ForCondition(() => ed.NumeroCondizioni() == 1, "copia rimossa");

            Assert.Equal("Restare", ed.GetNotaCondizione(0));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CopiaPrecedente_RimuovereLaCopiaNonCancellaLOriginale), ex); }
    }

    [Fact]
    public void CopiaPrecedente_SalvaEApri_PersistenzaSenzaLegameDiRipetizione()
    {
        try
        {
            var (shell, ed) = NuovoTest("QA Copia operazione - persistenza");
            ed.SetNotaCondizione(0, "Solo prima condizione");
            ed.CopiaDallaPrecedente();
            Wait.ForCondition(() => ed.NumeroCondizioni() == 2, "copia indipendente");
            ed.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            ed.Save();

            var lista = new TestListPage(App.WaitForMainWindow(), App.WaitForMainWindow());
            var riaperto = lista.OpenOnlyRow();
            Wait.ForCondition(() => riaperto.NumeroCondizioni() == 2, "due operazioni recuperate dal disco");

            var storage = Directory.EnumerateFiles(
                Path.Combine(Workspace.WorkspaceDir, "testcases"), "testcase_*.json",
                SearchOption.AllDirectories).ToList();
            Assert.Single(storage);
            var test = JsonIo.TryRead<TestCase>(storage[0], out var error);
            Assert.Null(error);
            Assert.NotNull(test);
            var condizioni = test!.Esecuzioni[^1].Condizioni;
            Assert.Equal(2, condizioni.Count);
            Assert.All(condizioni, c => Assert.Null(c.OperazioneOrigineUid));
            Assert.Equal("Solo prima condizione", condizioni[0].Nota);
            Assert.Equal("", condizioni[1].Nota);
            Assert.Equal(Esito.DaEseguire, condizioni[1].Esito);
            Assert.False(shell.HasNavigationError);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CopiaPrecedente_SalvaEApri_PersistenzaSenzaLegameDiRipetizione), ex); }
    }

    [Fact]
    public void RipetiERipartiDaPrecedente_SonoAzioniDistinteNelloStessoEditor()
    {
        try
        {
            var (_, ed) = NuovoTest("QA Ripeti vs copia");
            ed.RipetiCondizione(0);
            Wait.ForCondition(() => ed.NumeroCondizioni() == 2, "secondo tentativo");
            ed.CopiaDallaPrecedente();
            Wait.ForCondition(() => ed.NumeroCondizioni() == 3, "operazione indipendente");
            Assert.Equal(3, Enumerable.Range(0, 3).Select(ed.GetUidCondizione).Distinct().Count());
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(RipetiERipartiDaPrecedente_SonoAzioniDistinteNelloStessoEditor), ex); }
    }

    [Fact]
    public void CopiaPrecedente_AnnullaAnteprima_NonAggiungeOperazioni()
    {
        try
        {
            var (_, ed) = NuovoTest("QA Copia operazione - annulla anteprima");
            ed.ApriAnteprimaCopia();
            Wait.ForCondition(() => ed.AnteprimaCopiaVisibile, "anteprima dei campi visibile");
            Assert.Equal(1, ed.NumeroCondizioni());

            ed.AnnullaAnteprimaCopia();

            Wait.ForCondition(() => !ed.AnteprimaCopiaVisibile, "anteprima chiusa");
            Assert.Equal(1, ed.NumeroCondizioni());
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CopiaPrecedente_AnnullaAnteprima_NonAggiungeOperazioni), ex); }
    }

    [Fact]
    public void CopiaPrecedente_UndoPrimaDelSalvataggio_RimuoveSoloLaCopia()
    {
        try
        {
            var (_, ed) = NuovoTest("QA Copia operazione - undo");
            var originale = ed.GetUidCondizione(0);
            ed.CopiaDallaPrecedente();
            Wait.ForCondition(() => ed.NumeroCondizioni() == 2, "copia aggiunta");

            ed.AnnullaCopia();

            Wait.ForCondition(() => ed.NumeroCondizioni() == 1, "copia annullata");
            Assert.Equal(originale, ed.GetUidCondizione(0));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CopiaPrecedente_UndoPrimaDelSalvataggio_RimuoveSoloLaCopia), ex); }
    }
}
