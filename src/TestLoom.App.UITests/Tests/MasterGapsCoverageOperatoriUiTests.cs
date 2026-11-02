using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Copertura FlaUI del pannello PIN e chiave di recupero dell'operatore selezionato (Impostazioni, Operatori), non ancora
/// esercitato: impostazione di un nuovo PIN dal pannello e salvataggio (il PIN persistito verifica il nuovo valore e non
/// quello vecchio), rigenerazione della sola chiave di recupero (il PIN resta invariato). Il pannello e' abilitato solo in
/// modifica: ogni scenario entra in modifica e salva. Seme: un operatore Standard con PIN 4242 e una chiave di recupero.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
[Trait("Category", "CRUD")]
public sealed class MasterGapsCoverageOperatoriUiTests : UiTestBase
{
    private const string Operatore = "Operatore PIN QA";
    private const string PinVecchio = "4242";
    private const string PinNuovo = "5150";

    public MasterGapsCoverageOperatoriUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(MasterGapsCoverageOperatoriUiTests));
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var operatori = state.Master.Operatori.Load();
        var seminato = new Operatore
        {
            Nome = Operatore, Iniziali = "OP", PinHash = PinService.Hash(PinVecchio),
            RecoveryCodeHash = PinService.Hash(PinService.GeneraCodiceRecupero()),
            Ruolo = RuoloOperatore.Standard, ModificatoDa = "UITests",
        };
        RuoloSigilloService.Sigilla(seminato);
        operatori.Add(seminato);
        state.Master.Operatori.Save(operatori);
        return ws;
    }

    /// <summary>Operatore come persistito nel catalogo (repository reale, letto dal disco a ogni controllo).</summary>
    private Operatore Persistito() =>
        new MasterDataRepository<Operatore>(new DataPaths(Workspace.WorkspaceDir).OperatoriFile).Load()
            .Single(o => o.Nome == Operatore);

    /// <summary>Apre Impostazioni > Operatori, entra in modifica e seleziona l'operatore seminato (clic sul nome, come
    /// in OperatoriDettaglioPinTests), poi attende il pannello PIN dell'operatore selezionato.</summary>
    private static MasterDataEditorPage ApriPannelloOperatore(Window window)
    {
        new MainShellPage(window).GoToImpostazioni().VaiA("operatori");
        var editor = new MasterDataEditorPage(window, "Oper");
        editor.IniziaModifica();
        Wait.ForElement(() => editor.Grid.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .FirstOrDefault(e => string.Equals(e.Name, Operatore, StringComparison.Ordinal)),
            $"riga dell'operatore '{Operatore}' in griglia").Click();
        Wait.ForCondition(() => ElementFinders.ExistsById(window, "Oper_ImpostaPin_Button"),
            "pannello PIN dell'operatore selezionato");
        return editor;
    }

    [Fact]
    public void Operatore_ImpostaNuovoPinDalPannello_SalvaEIlPinPersistitoVerificaIlNuovoValore()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = ApriPannelloOperatore(window);

            ElementFinders.WaitTextBoxById(window, "Oper_NuovoPin_TextBox").SetText(PinNuovo);
            ElementFinders.WaitButtonById(window, "Oper_ImpostaPin_Button").SafeInvoke();

            editor.Salva();
            Wait.ForCondition(editor.PuoModificare, "ritorno in sola lettura dopo Salva");
            Wait.ForCondition(() => PinService.Verifica(PinNuovo, Persistito().PinHash),
                "il nuovo PIN e' persistito nel catalogo operatori");
            Assert.False(PinService.Verifica(PinVecchio, Persistito().PinHash), "il PIN precedente non deve piu' verificare");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Operatore_ImpostaNuovoPinDalPannello_SalvaEIlPinPersistitoVerificaIlNuovoValore), ex);
        }
    }

    [Fact]
    public void Operatore_RigeneraChiaveRecupero_CambiaIlCodiceSenzaToccareIlPin()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = ApriPannelloOperatore(window);
            var chiavePrima = Persistito().RecoveryCodeHash;

            ElementFinders.WaitButtonById(window, "Oper_RigeneraChiave_Button").SafeInvoke();
            editor.Salva();
            Wait.ForCondition(editor.PuoModificare, "ritorno in sola lettura dopo Salva");

            Wait.ForCondition(() => Persistito().RecoveryCodeHash != chiavePrima,
                "chiave di recupero rigenerata e persistita");
            Assert.True(PinService.Verifica(PinVecchio, Persistito().PinHash), "rigenerare la chiave di recupero non cambia il PIN");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Operatore_RigeneraChiaveRecupero_CambiaIlCodiceSenzaToccareIlPin), ex);
        }
    }
}
