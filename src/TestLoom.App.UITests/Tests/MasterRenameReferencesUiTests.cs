using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.Tests;

/// <summary>Rinomina un componente dal pannello UI dedicato e verifica che i riferimenti persistiti in un Test e in
/// una Funzionalità seguano la nuova chiave master.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "MasterData")]
public sealed class MasterRenameReferencesUiTests : UiTestBase
{
    private const string EcuOriginale = "Componente-RINOMINA-RIFERIMENTI-QA";
    private const string EcuNuova = "Componente-RINOMINATA-RIFERIMENTI-QA";
    private const string NomeFunzionalita = "Funzionalita-Riferimento-Rinomina-QA";
    private const string TestId = "TC-RENAMED-componente-REFERENCE-QA";

    public MasterRenameReferencesUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(MasterRenameReferencesUiTests));
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        var componente = state.Master.Componente.Load();
        componente.Add(new Componente { Nome = EcuOriginale, ModificatoDa = "UITests" });
        state.Master.Componente.Save(componente);

        var funzionalita = state.Master.Funzionalita.Load();
        funzionalita.Add(new Funzionalita
        {
            Nome = NomeFunzionalita,
            TutteLeEcu = false,
            EcuAssociate = { EcuOriginale },
            ModificatoDa = "UITests",
        });
        state.Master.Funzionalita.Save(funzionalita);

        TestCoverageSeed.SeedTest(state, TestId, "Test con componente da rinominare",
            TestCoverageSeed.UidTemplatePrincipale(state), EcuOriginale, Esito.Conforme);
        return workspace;
    }

    [Fact]
    public void Master_RinominaEcu_AggiornaIlCatalogoEIRiferimentiPersistiti()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editorEcu = new MainShellPage(window).GoToImpostazioni().VaiAEcu();
            var pannello = new MasterUnionePannelloPage(App, window, "Componente");
            pannello.Espandi();
            pannello.SelezionaVoce(EcuOriginale);
            pannello.Rinomina(EcuNuova);
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura della conferma della rinomina componente");

            Wait.ForCondition(() => editorEcu.ChiaviVisibili().Contains(EcuNuova), "la griglia componente mostra il nuovo nome dopo il refresh");
            Assert.DoesNotContain(EcuOriginale, editorEcu.ChiaviVisibili());
            var paths = new DataPaths(Workspace.WorkspaceDir);
            // I cataloghi master sono Phase D: leggere i vecchi JSON di bootstrap qui darebbe uno snapshot stale.
            var catalogo = new MasterDataRepository<Componente>(paths.EcuFile).Load();
            Assert.Contains(catalogo, e => e.Nome == EcuNuova);
            Assert.DoesNotContain(catalogo, e => e.Nome == EcuOriginale);

            var funzionalita = new MasterDataRepository<Funzionalita>(paths.FunzionalitaFile).Load();
            var voceFunzionalita = Assert.Single(funzionalita, f => f.Nome == NomeFunzionalita);
            Assert.Contains(voceFunzionalita.EcuAssociate, e => e.Equals(EcuNuova, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(voceFunzionalita.EcuAssociate, e => e.Equals(EcuOriginale, StringComparison.OrdinalIgnoreCase));

            var test = TestCoverageSeed.TrovaTestCase(Workspace, TestId);
            Assert.NotNull(test);
            Assert.Contains(test.Componente, e => e.Equals(EcuNuova, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(test.Componente, e => e.Equals(EcuOriginale, StringComparison.OrdinalIgnoreCase));
            Assert.Null(pannello.Errore);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Master_RinominaEcu_AggiornaIlCatalogoEIRiferimentiPersistiti), ex);
        }
    }
}
