using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.Tests;

/// <summary>G2 (6.4.5): PIN e chiave di recupero non sono piu' nelle righe della griglia Operatori ma nel pannello
/// di dettaglio dell'operatore selezionato, visibile solo a un Master.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
public sealed class OperatoriDettaglioPinTests : UiTestBase
{
    private const string OperatoreSeminato = "Operatore Seed";

    public OperatoriDettaglioPinTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(OperatoriDettaglioPinTests));
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        var operatori = state.Master.Operatori.Load();
        var seminato = new Operatore
        {
            Nome = OperatoreSeminato, Iniziali = "OS", PinHash = PinService.Hash("4242"),
            RecoveryCodeHash = PinService.Hash(PinService.GeneraCodiceRecupero()),
            Ruolo = RuoloOperatore.Standard, ModificatoDa = "UITests",
        };
        RuoloSigilloService.Sigilla(seminato);
        operatori.Add(seminato);
        state.Master.Operatori.Save(operatori);
        return workspace;
    }

    [Fact]
    public void Master_SelezionaOperatore_MostraPannelloPinConPinMascherato()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToImpostazioni().VaiA("operatori");
            var editor = new MasterDataEditorPage(window, "Oper");

            // Il Border non espone un nodo di automazione: il suggerimento "seleziona un operatore" prova il pannello.
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Oper_PinSeleziona_Text"), "pannello PIN riservato al Master");
            Assert.False(ElementFinders.ExistsById(window, "Oper_PinAttuale_TextBox"), "Senza selezione non c'e' alcun PIN da mostrare.");

            Wait.ForCondition(() => editor.Grid.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Any(e => (e.Name ?? "").Contains(OperatoreSeminato, StringComparison.OrdinalIgnoreCase)), "riga operatore in griglia");
            editor.Grid.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .First(e => (e.Name ?? "").Contains(OperatoreSeminato, StringComparison.OrdinalIgnoreCase)).Click();

            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Oper_PinAttuale_TextBox"), "dettaglio PIN dell'operatore selezionato");
            Assert.True(ElementFinders.ExistsById(window, "Oper_MostraPin_Check"));
            Assert.True(ElementFinders.ExistsById(window, "Oper_ImpostaPin_Button"));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Master_SelezionaOperatore_MostraPannelloPinConPinMascherato), ex);
        }
    }
}
