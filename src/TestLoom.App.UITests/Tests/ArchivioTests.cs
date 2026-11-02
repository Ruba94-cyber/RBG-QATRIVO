using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.Tests;

/// <summary>Prima copertura FlaUI per Impostazioni -> Archivio: fino a questa iterazione la sotto-sezione
/// aveva 0 AutomationId e nessun test navigava oltre l'apertura generica (ImpostazioniNavigazioneTests). Semina
/// un componente gia' archiviata (stesso repository/tecnica diretta di MasterDataAzioniModificaTests) e verifica che
/// "Ripristina" la faccia sparire dall'Archivio.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class ArchivioTests : UiTestBase
{
    private const string EcuArchiviata = "Componente-Archiviata-QA";

    public ArchivioTests() : base(CreaWorkspaceConEcuArchiviata()) { }

    private static TestWorkspace CreaWorkspaceConEcuArchiviata()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(ArchivioTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        var componente = state.Master.Componente.Load();
        componente.Add(new Componente { Nome = EcuArchiviata, Archiviata = true, ModificatoDa = "UITests" });
        state.Master.Componente.Save(componente);
        return workspace;
    }

    [Fact]
    public void Ripristina_UnaEcuArchiviata_LaRimuoveDallArchivio()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var impostazioni = shell.GoToImpostazioni();
            impostazioni.VaiA("archivio");

            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Archivio_Title_Text"), "pagina Archivio caricata");
            var griglia = ElementFinders.WaitById(window, "Archivio_Grid");
            Wait.ForCondition(() => TrovaTesto(griglia, EcuArchiviata) != null, "il componente archiviata seminata compare in Archivio");

            ElementFinders.WaitButtonById(window, "Archivio_Ripristina_Button").SafeInvoke();

            Wait.ForCondition(() => TrovaTesto(griglia, EcuArchiviata) == null,
                "il componente non compare piu' in Archivio dopo Ripristina");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Ripristina_UnaEcuArchiviata_LaRimuoveDallArchivio), ex);
        }
    }

    private static FlaUI.Core.AutomationElements.AutomationElement? TrovaTesto(FlaUI.Core.AutomationElements.AutomationElement scope, string testo) =>
        scope.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).FirstOrDefault(e => e.Name == testo);
}
