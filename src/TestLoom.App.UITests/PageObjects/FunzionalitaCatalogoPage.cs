using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Keyboard = FlaUI.Core.Input.Keyboard;
using static TestLoom.App.UITests.Helpers.CaselleUia;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>
/// Parti della pagina Funzionalita' non coperte dal Page Object generico: la sezione Categorie (Expander chiuso di default,
/// griglia inline "Funz_Categorie_Griglia") e il flyout "Componenti associati" della singola riga (pulsante
/// "Funz_EcuAssociate_Button", con una casella per componente e la casella "Tutti i componenti").
/// </summary>
public sealed class FunzionalitaCatalogoPage
{
    private readonly TestLoomApplication _app;
    private readonly Window _window;

    public FunzionalitaCatalogoPage(TestLoomApplication app, Window window)
    {
        _app = app;
        _window = window;
    }

    // ---------------------------------------------------------------- categorie

    /// <summary>Espande la sezione Categorie e attende la griglia inline.</summary>
    public void EspandiCategorie()
    {
        var expander = ElementFinders.WaitById(_window, "FunzCategorieExpander");
        var pattern = expander.Patterns.ExpandCollapse.PatternOrDefault;
        if (pattern != null && pattern.ExpandCollapseState.Value != ExpandCollapseState.Expanded) pattern.Expand();
        ElementFinders.WaitById(_window, "Funz_Categorie_Griglia");
    }

    public bool CategorieInModifica => ElementFinders.ExistsById(_window, "Funz_Categorie_Salva_Button");

    public void IniziaModificaCategorie()
    {
        ElementFinders.WaitButtonById(_window, "Funz_Categorie_Modifica_Button").SafeInvoke();
        Wait.ForCondition(() => CategorieInModifica, "categorie in modifica dopo Modifica");
    }

    public void NuovaCategoria() => ElementFinders.WaitButtonById(_window, "Funz_Categorie_Nuovo_Button").SafeInvoke();

    public void AnnullaCategorie() => ElementFinders.WaitButtonById(_window, "Funz_Categorie_Annulla_Button").SafeInvoke();

    public void SalvaCategorie() => ElementFinders.WaitButtonById(_window, "Funz_Categorie_Salva_Button").SafeInvoke();

    public string? ErroreCategorie => ElementFinders.TryFindById(_window, "Funz_Categorie_MessaggioErrore_Text")?.Name;

    public IReadOnlyList<string> NomiCategorieVisibili() =>
        Griglia().FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Select(TestoPrimaCella).ToList();

    /// <summary>Scrive il nome dell'ultima riga (quella creata con "+ Nuovo") passando dalla cella, come le altre griglie inline.</summary>
    public void ScriviNomeUltimaCategoria(string valore)
    {
        var riga = Wait.ForElement(
            () => Griglia().FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).LastOrDefault(),
            "riga nuova nella griglia delle categorie");
        ModificaCella(riga, valore);
    }

    private AutomationElement Griglia() => ElementFinders.WaitById(_window, "Funz_Categorie_Griglia");

    // ---------------------------------------------------------------- Componenti associati (flyout della riga)

    /// <summary>Apre il flyout "Componenti associati" della riga della funzionalità e attende la casella del componente indicata.</summary>
    public void ApriFlyoutEcu(string nomeFunzione, string ecuAttesa)
    {
        var riga = RigaFunzionalita(nomeFunzione);
        Wait.ForElement(() => riga.FindFirstDescendant(cf => cf.ByAutomationId("Funz_EcuAssociate_Button")),
            $"pulsante Componenti associati della riga '{nomeFunzione}'", TimeSpan.FromSeconds(20)).AsButton().SafeInvoke();
        Wait.ForElement(() => TrovaPicker(ecuAttesa), $"flyout Componenti associati con la casella '{ecuAttesa}'", TimeSpan.FromSeconds(15));
    }

    /// <summary>Imposta la casella di un componente nel flyout aperto (spunta o toglie la voce).</summary>
    public void ImpostaEcu(string nomeEcu, bool accesa, string ecuAttesa)
    {
        var picker = Wait.ForElement(() => TrovaPicker(ecuAttesa), "flyout Componenti associati aperto");
        var casella = Wait.ForElement(() => picker.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox).And(cf.ByName(nomeEcu))).FirstOrDefault(),
            $"casella del componente '{nomeEcu}' nel flyout", TimeSpan.FromSeconds(10));
        ImpostaSpunta(casella, accesa);
    }

    /// <summary>Testo del pulsante Componenti associati della riga (il riepilogo dei componenti scelte, es. "Componente-QA-01, componente-QA-02").</summary>
    public string RiepilogoEcuRiga(string nomeFunzione) =>
        RigaFunzionalita(nomeFunzione).FindFirstDescendant(cf => cf.ByAutomationId("Funz_EcuAssociate_Button"))?.Name ?? string.Empty;

    /// <summary>Chiude il flyout con Esc sulla finestra principale e attende che il pannello sparisca.</summary>
    public void ChiudiFlyoutEcu(string ecuAttesa)
    {
        ElementActions.PressKey(_window, VirtualKeyShort.ESCAPE);
        Wait.ForCondition(() => TrovaPicker(ecuAttesa) == null, "flyout Componenti associati chiuso con Esc", TimeSpan.FromSeconds(10));
    }

    private AutomationElement RigaFunzionalita(string nome) =>
        Wait.ForElement(() => RigaConTesto(ElementFinders.WaitById(_window, "GridPrincipale"), nome),
            $"riga della funzionalita' '{nome}'", TimeSpan.FromSeconds(20));

    /// <summary>Il pannello più interno che contiene la casella del componente indicata (il flyout, non i pannelli di layout).</summary>
    private AutomationElement? TrovaPicker(string nomeEcu) =>
        _window.FindAllDescendants(cf => cf.ByControlType(ControlType.Pane))
            .FirstOrDefault(p => p.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox).And(cf.ByName(nomeEcu))) != null
                && !p.FindAllDescendants(cf => cf.ByControlType(ControlType.Pane))
                    .Any(d => d.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox).And(cf.ByName(nomeEcu))) != null));

    // ---------------------------------------------------------------- utilità

    private static void ModificaCella(AutomationElement riga, string valore)
    {
        riga.FindAllChildren().First().Click();
        Keyboard.Type(VirtualKeyShort.F2);
        var editor = Wait.ForElement(
            () => riga.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)),
            "editor della cella Nome");
        editor.Focus();
        Keyboard.Type(valore);
        Wait.ForCondition(() => editor.AsTextBox().Text == valore, $"testo '{valore}' digitato nell'editor della cella");
        Keyboard.Type(VirtualKeyShort.ENTER);
        Wait.ForCondition(() => riga.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)) == null,
            "editor della cella chiuso dopo Invio");
    }

    private static string TestoPrimaCella(AutomationElement riga)
    {
        var cella = riga.FindAllChildren().FirstOrDefault();
        return cella?.FindFirstDescendant(cf => cf.ByAutomationId("CellTextBlock"))?.Name ?? string.Empty;
    }
}
