using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Keyboard = FlaUI.Core.Input.Keyboard;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>
/// Pannello "Unisci, rinomina o dividi la voce selezionata" delle sotto-sezioni master data (Funzionalita, componente, Veicoli,
/// Requisiti, Operatori, Strumenti): stessi controlli con prefisso diverso (<c>FunzBtnUnisci</c>, <c>ComponenteBtnRinomina</c>...).
/// Il pannello è visibile solo fuori dalla modifica e solo a un Master, e le sue azioni si attivano dopo aver selezionato una
/// riga della griglia principale ("GridPrincipale"). Ogni operazione chiede una conferma ("Confirm_Yes_Button") e poi lavora
/// in background: il chiamante verifica l'esito sui file, non solo sulla griglia.
/// </summary>
public sealed class MasterUnionePannelloPage
{
    private readonly TestLoomApplication _app;
    private readonly Window _window;
    private readonly string _prefisso;

    public MasterUnionePannelloPage(TestLoomApplication app, Window window, string prefisso)
    {
        _app = app;
        _window = window;
        _prefisso = prefisso;
    }

    /// <summary>Espande il pannello (chiuso di default) e attende i suoi controlli.</summary>
    public void Espandi()
    {
        var expander = ElementFinders.WaitById(_window, $"{_prefisso}UnioneExpander");
        var pattern = expander.Patterns.ExpandCollapse.PatternOrDefault;
        if (pattern != null && pattern.ExpandCollapseState.Value != ExpandCollapseState.Expanded) pattern.Expand();
        // Con il pannello aperto e nessuna riga selezionata compare solo il testo guida: i pulsanti appaiono alla selezione.
        Wait.ForCondition(() => ElementFinders.WaitById(_window, $"{_prefisso}UnioneExpander").FindAllChildren().Length > 1,
            "pannello Unisci/Rinomina/Dividi aperto");
    }

    /// <summary>Messaggio del banner di errore del pannello (es. rinomina su un nome già esistente), o null.</summary>
    public string? Errore => ElementFinders.TryFindById(_window, $"{_prefisso}UnioneErrore_Text")?.Name;

    /// <summary>Messaggio del banner di esito dopo un'operazione riuscita, o null.</summary>
    public string? Esito => ElementFinders.TryFindById(_window, $"{_prefisso}UnioneEsito_Text")?.Name;

    /// <summary>Seleziona la riga con la chiave indicata (prima colonna) con un clic sulla cella: abilita le azioni del pannello.</summary>
    public void SelezionaVoce(string chiave)
    {
        var griglia = ElementFinders.WaitById(_window, "GridPrincipale");
        var riga = Wait.ForElement(
            () => griglia.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
                .FirstOrDefault(r => string.Equals(TestoPrimaCella(r), chiave, StringComparison.Ordinal)),
            $"riga '{chiave}' nella griglia");
        riga.FindAllChildren().First().Click();
        Wait.ForCondition(() => ElementFinders.ExistsById(_window, $"{_prefisso}BtnUnisci"), "azioni del pannello dopo la selezione");
    }

    /// <summary>Unisce la voce selezionata in <paramref name="destinazione"/> (scelta dal combo "Unisci in") e conferma.</summary>
    public void UnisciIn(string destinazione)
    {
        ElementFinders.WaitComboBoxById(_window, $"{_prefisso}ComboUnisciIn").SelectByText(destinazione);
        ElementFinders.WaitButtonById(_window, $"{_prefisso}BtnUnisci").SafeInvoke();
        ConfermaSi();
    }

    /// <summary>Rinomina la voce selezionata in <paramref name="nuovoNome"/> e conferma.</summary>
    public void Rinomina(string nuovoNome)
    {
        ElementFinders.WaitById(_window, $"{_prefisso}TextRinominaIn").AsTextBox().SetText(nuovoNome);
        ElementFinders.WaitButtonById(_window, $"{_prefisso}BtnRinomina").SafeInvoke();
        ConfermaSi();
    }

    /// <summary>Divide la voce selezionata nelle voci indicate (separate da virgola nel campo) e conferma.</summary>
    public void Dividi(params string[] nuoveVoci)
    {
        ElementFinders.WaitById(_window, $"{_prefisso}TextDividiIn").AsTextBox().SetText(string.Join(", ", nuoveVoci));
        ElementFinders.WaitButtonById(_window, $"{_prefisso}BtnDividi").SafeInvoke();
        ConfermaSi();
    }

    /// <summary>Clic su "Rinomina" senza conferma: serve a verificare che il ConfirmDialog compaia e che "No" non scriva nulla.</summary>
    public void RinominaSenzaConfermare(string nuovoNome)
    {
        ElementFinders.WaitById(_window, $"{_prefisso}TextRinominaIn").AsTextBox().SetText(nuovoNome);
        ElementFinders.WaitButtonById(_window, $"{_prefisso}BtnRinomina").SafeInvoke();
    }

    /// <summary>Risponde Sì al ConfirmDialog dell'operazione appena avviata.</summary>
    public void ConfermaSi() => new ConfirmDialogPage(_app.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();

    /// <summary>Risponde No al ConfirmDialog dell'operazione appena avviata.</summary>
    public void ConfermaNo() => new ConfirmDialogPage(_app.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmNo();

    private static string TestoPrimaCella(AutomationElement riga)
    {
        var cella = riga.FindAllChildren().FirstOrDefault();
        return cella?.FindFirstDescendant(cf => cf.ByAutomationId("CellTextBlock"))?.Name ?? string.Empty;
    }
}
