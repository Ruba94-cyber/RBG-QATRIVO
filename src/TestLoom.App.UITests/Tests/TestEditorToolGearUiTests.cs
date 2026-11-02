using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// UIA3: the gear next to a tool inside the Test opens the dedicated tool window, which also lets the operator choose which
/// per-operation fields are visible in THIS Test (presentation only). Synthetic tool and data only.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "EditorQuick")]
public sealed class TestEditorToolGearUiTests : UiTestBase
{
    private const string Strumento = "ToolQA";
    private const string CampoA = "Campo A";
    private const string CampoB = "Campo B";

    public TestEditorToolGearUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(TestEditorToolGearUiTests));
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var tool = new StrumentoDiagnostico { Nome = Strumento, ModificatoDa = "UITests" };
        tool.Campi.Add(new DefinizioneCampoStrumento { Nome = CampoA, Ordine = 1, Ambito = TipoAmbitoCampo.PerOperazione });
        tool.Campi.Add(new DefinizioneCampoStrumento { Nome = CampoB, Ordine = 2, Ambito = TipoAmbitoCampo.PerOperazione });
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico> { tool });
        var templates = state.Master.TestTemplate.Load();
        var template = templates.First(t => !t.Archiviata);
        template.Sezioni[0].Campi.Add(new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.StrumentiUsati });
        template.StrumentiPreselezionati = new List<string> { Strumento };
        state.Master.TestTemplate.Save(templates);
        return ws;
    }

    private static string Scelta(string campo) => $"CampiStrumento_Visibile_{Strumento}::{campo}_Check";

    [Fact]
    public void GearOpensToolWindowAndPerTestFieldVisibilityIsRememberedWithoutTouchingData()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToTestList().ClickNew();
            editor.ChooseTemplate();
            editor.SwitchToFullMode();
            editor.GoToTab(1);

            var gear = $"Editor_Strumento_{Strumento}_Rotellina_Button";
            Wait.ForCondition(() => ElementFinders.ExistsById(window, gear), "gear shown next to the tool");
            ElementFinders.WaitButtonById(window, gear).SafeInvoke();
            var dialog = App.WaitForWindowByContent("CampiStrumento_Annulla_Button");

            Assert.True(ElementFinders.ExistsById(dialog, Scelta(CampoA)));
            Assert.True(ElementFinders.ExistsById(dialog, Scelta(CampoB)));
            var campoB = ElementFinders.WaitById(dialog, Scelta(CampoB));
            Assert.Equal(ToggleState.On, campoB.Patterns.Toggle.Pattern.ToggleState.Value);
            campoB.Patterns.Toggle.Pattern.Toggle(); // hide "Campo B" in this Test
            ElementFinders.WaitButtonById(dialog, "CampiStrumento_Salva_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("CampiStrumento_Annulla_Button"), "tool window closed after Salva");

            ElementFinders.WaitButtonById(window, gear).SafeInvoke();
            var riaperta = App.WaitForWindowByContent("CampiStrumento_Annulla_Button");
            Assert.Equal(ToggleState.Off,
                ElementFinders.WaitById(riaperta, Scelta(CampoB)).Patterns.Toggle.Pattern.ToggleState.Value);
            Assert.Equal(ToggleState.On,
                ElementFinders.WaitById(riaperta, Scelta(CampoA)).Patterns.Toggle.Pattern.ToggleState.Value);
            ElementFinders.WaitButtonById(riaperta, "CampiStrumento_Annulla_Button").SafeInvoke();
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(GearOpensToolWindowAndPerTestFieldVisibilityIsRememberedWithoutTouchingData), ex);
        }
    }
}
