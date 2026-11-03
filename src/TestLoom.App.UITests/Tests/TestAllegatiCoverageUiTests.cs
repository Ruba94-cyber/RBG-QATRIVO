using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Rimozione di un allegato gia' salvato, senza il picker nativo: il Test e il suo file vengono seminati prima dell'avvio
/// di RBG - QATRIVO.exe nel layout reale (allegati/test/&lt;uid&gt;/&lt;nome&gt;, vedi DataPaths.AllegatiDir). Il test verifica la scheda,
/// il Test salvato su disco e lo spostamento del file nella cartella di recupero (il percorso originale non esiste piu').
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class TestAllegatiCoverageUiTests : UiTestBase
{
    private const string IdTest = "TC-QA-ALL-1";
    private const string NomeAllegato = "allegato_seminato_qa.zip";
    private const string ContenutoAllegato = "Allegato sintetico QA seminato\n";

    public TestAllegatiCoverageUiTests() : base(CreaWorkspaceConAllegato()) { }

    private static TestWorkspace CreaWorkspaceConAllegato()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(TestAllegatiCoverageUiTests));
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        var test = new TestCase
        {
            Id = IdTest,
            DescrizioneBreve = "Test con allegato gia' salvato",
            TestTemplateUid = TestCoverageSeed.UidTemplatePrincipale(state),
            CreatoDa = TestWorkspace.DefaultOperatorName,
            ModificatoDa = "UITests",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        };
        test.Componente.Add(TestWorkspace.DefaultEcuName);
        test.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1,
            Data = DateTime.Now,
            Operatore = "UITests",
            Esito = Esito.Conforme,
            // Ogni operazione deve avere un componente del Test (regola di validazione al Salva): come nella creazione reale.
            Condizioni = new List<Condizione> { new() { Esito = Esito.Conforme, Componente = { TestWorkspace.DefaultEcuName } } },
        });
        test.Allegati.Add(new Allegato
        {
            NomeFile = NomeAllegato,
            CaricatoDa = TestWorkspace.DefaultOperatorName,
            DataCaricamento = DateTime.Now,
            DimensioneByte = ContenutoAllegato.Length,
        });
        state.TestCases.Save(test, true);

        var cartella = new DataPaths(workspace.WorkspaceDir).AllegatiDir(test);
        Directory.CreateDirectory(cartella);
        File.WriteAllText(Path.Combine(cartella, NomeAllegato), ContenutoAllegato);
        return workspace;
    }

    [Fact]
    public void Allegato_Rimosso_SparisceDallaSchedaEDalTestAlSalvataggio()
    {
        try
        {
            var riaperto = new MainShellPage(App.WaitForMainWindow()).GoToTestList().OpenRowById(IdTest);
            riaperto.EnterEditMode();
            Wait.ForCondition(() => riaperto.HaAllegato(NomeAllegato), "allegato seminato visibile nella scheda Allegati");

            riaperto.RimuoviAllegato(NomeAllegato);
            Wait.ForCondition(() => !riaperto.HaAllegato(NomeAllegato), "allegato rimosso dalla scheda");
            riaperto.Save();

            TestCase? test = null;
            string? originale = null;
            var paths = new DataPaths(Workspace.WorkspaceDir);
            Wait.ForCondition(() =>
            {
                test = TestCoverageSeed.TrovaTestCase(Workspace, IdTest);
                if (test == null || test.Allegati.Count != 0) return false;
                originale = Path.Combine(paths.AllegatiDir(test), NomeAllegato);
                return !File.Exists(originale);
            }, "rimozione allegato persistita e file spostato nel recupero");

            Assert.NotNull(test);
            Assert.Empty(test.Allegati);
            Assert.False(File.Exists(originale), "il file dell'allegato non resta al percorso originale: e' spostato nel recupero");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Allegato_Rimosso_SparisceDallaSchedaEDalTestAlSalvataggio), ex); }
    }
}
