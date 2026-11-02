using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Wait = TestLoom.App.UITests.Infrastructure.Wait;
using static TestLoom.App.UITests.Helpers.CaselleUia;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Funzionalita' limitata ai veicoli di un componente (6.7.2). Nella pagina Funzionalita', il picker del componente associata mostra
/// "tutti i veicoli" oppure "N di M veicoli" e un elenco dei veicoli montati da modificare. Un Master restringe la
/// funzionalita' a un solo veicolo del componente, salva: la restrizione resta visibile riaprendo la pagina e dopo un riavvio
/// di TestLoom. Il picker è costruito nel code-behind senza AutomationId: i suoi controlli si scelgono per il nome del componente
/// e dei veicoli (dati seminati) e per il testo del pulsante "Veicoli…".
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
[Trait("Area", "Funzionalita")]
public sealed class FunzionalitaVeicoliPerEcuUiTests : IDisposable
{
    private const string EcuBase = TestWorkspace.DefaultEcuName;
    private const string VeicoloA = TestWorkspace.DefaultVehicleName;
    private const string VeicoloB = "Veicolo-QA-02";
    private const string Funzione = "Funz-Restr";
    private const string PulsanteVeicoli = "Veicoli…";
    private const string RiepilogoTutti = "tutti i veicoli";
    private const string RiepilogoUnoDiDue = "1 di 2 veicoli";

    private readonly TestWorkspace _workspace;

    public FunzionalitaVeicoliPerEcuUiTests() => _workspace = CreaWorkspace();

    public void Dispose() => _workspace.Dispose();

    /// <summary>Due veicoli montati sul componente di base, e una funzionalità valida solo su quell'componente senza restrizioni.</summary>
    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(FunzionalitaVeicoliPerEcuUiTests), seedTestFixtures: true);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var veicoli = state.Master.Veicoli.Load();
        veicoli.Add(new Veicolo { Nome = VeicoloB, ModificatoDa = "UITests" });
        state.Master.Veicoli.Save(veicoli);
        var componente = state.Master.Componente.Load();
        componente.Single(e => e.Nome == EcuBase).VeicoliAssociati = new List<string> { VeicoloA, VeicoloB };
        state.Master.Componente.Save(componente);
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = Funzione, TutteLeEcu = false, EcuAssociate = new List<string> { EcuBase }, ModificatoDa = "UITests" },
        });
        return ws;
    }

    // ---------------------------------------------------------------- picker

    /// <summary>Clic sul pulsante componente della riga della funzionalità. Il picker è un flyout: in UI Automation compare come un
    /// pannello (Pane) discendente della finestra principale, che contiene la casella del componente, il riepilogo e "Veicoli…".</summary>
    private static AutomationElement ApriPickerEcu(Window principale, AutomationElement griglia, string nomeFunzione, string nomeEcu)
    {
        var riga = Wait.ForElement(() => RigaConTesto(griglia, nomeFunzione), $"riga della funzionalita' '{nomeFunzione}'", TimeSpan.FromSeconds(20));
        Wait.ForElement(() => riga.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(nomeEcu))),
            $"pulsante componente '{nomeEcu}' nella riga '{nomeFunzione}'", TimeSpan.FromSeconds(20)).SafeInvoke();
        return Wait.ForElement(() => TrovaPicker(principale, nomeEcu), $"picker componente '{nomeEcu}' aperto", TimeSpan.FromSeconds(15));
    }

    /// <summary>Il pannello più interno che contiene la casella del componente: il picker stesso, e non i pannelli di layout che lo contengono.</summary>
    private static AutomationElement? TrovaPicker(AutomationElement principale, string nomeEcu) =>
        principale.FindAllDescendants(cf => cf.ByControlType(ControlType.Pane))
            .FirstOrDefault(p => CasellaPresente(p, nomeEcu)
                && !p.FindAllDescendants(cf => cf.ByControlType(ControlType.Pane)).Any(d => CasellaPresente(d, nomeEcu)));

    private static bool CasellaPresente(AutomationElement contenitore, string nome) =>
        contenitore.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox).And(cf.ByName(nome))) != null;

    /// <summary>Apre l'elenco dei veicoli montati del componente con il pulsante "Veicoli…" del picker.</summary>
    private static void ApriElencoVeicoli(AutomationElement picker)
    {
        Wait.ForElement(() => picker.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(PulsanteVeicoli))),
            "pulsante 'Veicoli…' del picker", TimeSpan.FromSeconds(10)).SafeInvoke();
        Wait.ForElement(() => CasellaVeicolo(picker, VeicoloA), $"casella del veicolo '{VeicoloA}' nell'elenco", TimeSpan.FromSeconds(10));
    }

    private static AutomationElement CasellaVeicolo(AutomationElement picker, string nomeVeicolo) =>
        Wait.ForElement(() => picker.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox).And(cf.ByName(nomeVeicolo))).FirstOrDefault(),
            $"casella del veicolo '{nomeVeicolo}' nel picker", TimeSpan.FromSeconds(10));

    /// <summary>Chiude il picker con Esc (sulla finestra principale) e attende che il pannello non sia più presente.</summary>
    private static void ChiudiPicker(Window principale, AutomationElement picker, string nomeEcu)
    {
        ElementActions.PressKey(principale, VirtualKeyShort.ESCAPE);
        Wait.ForCondition(() => TrovaPicker(principale, nomeEcu) == null, "picker componente chiuso con Esc", TimeSpan.FromSeconds(10));
    }
}
