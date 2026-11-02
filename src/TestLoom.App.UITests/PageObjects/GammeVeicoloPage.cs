using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Keyboard = FlaUI.Core.Input.Keyboard;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>
/// Gamme veicolari, annidate nella pagina Veicoli (Impostazioni → Veicoli). L'Expander "VeicGammeExpander" parte
/// chiuso; dentro c'è la griglia inline "VeicGamma_Griglia" con "+ Nuovo", Modifica/Annulla/Salva e l'archiviazione
/// per riga ("VeicGamma_RigaArchivia_Button"). La griglia non è "GridPrincipale": le righe si cercano solo dentro di lei.
/// </summary>
public sealed class GammeVeicoloPage
{
    private readonly Window _window;

    public GammeVeicoloPage(Window window) => _window = window;

    /// <summary>Espande la sezione Gamme e attende la griglia inline.</summary>
    public void Espandi()
    {
        var expander = ElementFinders.WaitById(_window, "VeicGammeExpander");
        var pattern = expander.Patterns.ExpandCollapse.PatternOrDefault;
        if (pattern != null && pattern.ExpandCollapseState.Value != ExpandCollapseState.Expanded) pattern.Expand();
        ElementFinders.WaitById(_window, "VeicGamma_Griglia");
    }

    public bool InModifica => ElementFinders.ExistsById(_window, "VeicGamma_Salva_Button");

    public void IniziaModifica()
    {
        ElementFinders.WaitButtonById(_window, "VeicGamma_Modifica_Button").SafeInvoke();
        Wait.ForCondition(() => InModifica, "gamme in modifica dopo Modifica");
    }

    public void Nuovo() => ElementFinders.WaitButtonById(_window, "VeicGamma_Nuovo_Button").SafeInvoke();

    public void Annulla() => ElementFinders.WaitButtonById(_window, "VeicGamma_Annulla_Button").SafeInvoke();

    public void Salva() => ElementFinders.WaitButtonById(_window, "VeicGamma_Salva_Button").SafeInvoke();

    /// <summary>Banner di errore della sezione Gamme (es. "Nome obbligatorio"), o null se non mostrato.</summary>
    public string? MessaggioErrore => ElementFinders.TryFindById(_window, "VeicGamma_MessaggioErrore_Text")?.Name;

    /// <summary>Nomi delle righe visibili nella griglia inline, nell'ordine in cui sono mostrate.</summary>
    public IReadOnlyList<string> NomiVisibili() =>
        Griglia.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Select(TestoPrimaCella).ToList();

    /// <summary>Scrive il nome dell'ultima riga della griglia (la riga creata con "+ Nuovo"): F2 sulla prima cella apre
    /// l'editor, il testo si digita e Invio conferma la cella (stesso schema di MasterDataEditorPage).</summary>
    public void ScriviNomeUltimaRiga(string valore)
    {
        var riga = Wait.ForElement(
            () => Griglia.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).LastOrDefault(),
            "riga nuova nella griglia delle gamme");
        ModificaCella(riga, valore);
    }

    /// <summary>Rinomina una riga esistente: sostituisce tutto il testo della prima cella con <paramref name="nuovo"/>.</summary>
    public void RinominaRiga(string vecchio, string nuovo) => ModificaCella(RigaConNome(vecchio), nuovo, sostituisci: true);

    /// <summary>Archivia la riga con il nome indicato tramite il pulsante "Archivia" della riga (solo in modifica).</summary>
    public void ArchiviaRiga(string nome) =>
        ElementFinders.WaitByIdWithinRow(RigaConNome(nome), "VeicGamma_RigaArchivia_Button").AsButton().SafeInvoke();

    private AutomationElement Griglia => ElementFinders.WaitById(_window, "VeicGamma_Griglia");

    private AutomationElement RigaConNome(string nome) =>
        Wait.ForElement(
            () => Griglia.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
                .FirstOrDefault(riga => string.Equals(TestoPrimaCella(riga), nome, StringComparison.Ordinal)),
            $"riga delle gamme con nome '{nome}'");

    private static void ModificaCella(AutomationElement riga, string valore, bool sostituisci = false)
    {
        // Un clic sulla cella (non Focus()) seleziona la riga e sposta la cella corrente della griglia: senza, F2 aprirebbe
        // l'editor sulla prima riga invece che su quella appena creata.
        riga.FindAllChildren().First().Click();
        Keyboard.Type(VirtualKeyShort.F2);
        var editor = Wait.ForElement(
            () => riga.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)),
            "editor della cella Nome della gamma");
        editor.Focus();
        if (sostituisci)
        {
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(VirtualKeyShort.DELETE);
        }
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
