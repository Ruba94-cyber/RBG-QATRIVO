using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>Editor Change Request (ChangeRequestEditorView), stessa MainWindow della lista Anomalie/CR. Una
/// CR nuova auto-seleziona l'unico template CR disponibile (stesso principio del template minimo Test di
/// TestWorkspace: un solo template evita l'ambiguita' di piu' pulsanti con lo stesso AutomationId).</summary>
public sealed class ChangeRequestEditorPage
{
    private readonly AutomationElement _scope;

    public ChangeRequestEditorPage(AutomationElement scope)
    {
        _scope = scope;
        ElementFinders.WaitById(scope, "Cr_Title_Text");
    }

    public void SetTitolo(string testo) => ElementFinders.WaitTextBoxById(_scope, "Cr_Titolo_TextBox").SetText(testo);
    public void SetDescrizione(string testo) => ElementFinders.WaitTextBoxById(_scope, "Cr_Descrizione_TextBox").SetText(testo);

    /// <summary>La sezione "Ambito impattato" (componente/Veicoli/Strumenti) e' obbligatoria: SalvaCommand rifiuta il
    /// salvataggio con "Seleziona almeno un veicolo, un componente o uno strumento impattato" se resta vuota (vedi
    /// il dialog "Campi obbligatori mancanti"). A differenza di TestEditorPage.ChooseEcu (sempre un
    /// SelezionaDialog), qui "Aggiungi componente..." apre MultiSelectPickerFlyout: SOTTO 10 voci un Flyout costruito
    /// a runtime con CheckBox anonime (solo Content, NESSUN AutomationId — MultiSelectPickerFlyout.cs, vedi
    /// COPERTURA_FLAUI.md "il flyout di selezione multipla non ha Page Object dedicato"), SOPRA soglia lo
    /// stesso SelezionaDialog di ChooseEcu. Le due componente del workspace restano sotto soglia: si cerca la CheckBox
    /// per testo (Content) e il pulsante di conferma tramite AutomationId, nella
    /// stessa finestra principale (il Flyout di Avalonia si renderizza nell'OverlayLayer della finestra che lo
    /// apre, non in una finestra separata). Il fixture CR ha due componente, entrambe sotto la soglia.</summary>
    public void AggiungiEcu(TestLoomApplication app, string ecuName)
    {
        ElementFinders.WaitButtonById(_scope, "Cr_AggiungiEcu_Button").SafeInvoke();
        var check = Wait.Until(
            () => _scope.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.CheckBox).And(cf.ByName(ecuName))),
            _ => true, description: $"CheckBox '{ecuName}' nel flyout 'Scegli componente'");
        check.AsCheckBox().Toggle();
        ElementFinders.WaitButtonById(_scope, "MultiSelectPicker_Add_Button").SafeInvoke();
    }

    public void RimuoviEcu(string ecuName) =>
        ElementFinders.WaitButtonById(_scope, $"Cr_RimuoviEcu_{ecuName}").SafeInvoke();

    public string StatoTesto => ElementFinders.WaitById(_scope, "Cr_Stato_Text").Name ?? string.Empty;

    public void Salva() => ElementFinders.WaitButtonById(_scope, "Cr_Salva_Button").SafeInvoke();
    public void TornaAllaLista() => ElementFinders.WaitButtonById(_scope, "Cr_TornaAllaLista_Button").SafeInvoke();

    /// <summary>Hint "sola lettura" (Cr_SolaLettura_Text): visibile finche' l'editor e' aperto da Apri, prima di Modifica.</summary>
    public bool InSolaLettura => Visibile("Cr_SolaLettura_Text");

    public bool ModificaVisibile => Visibile("Cr_Modifica_Button");

    public bool MotivoTransizioneVisibile => Visibile("Cr_MotivoTransizione_Text");

    /// <summary>Testo del banner di errore (vuoto se il banner non e' mostrato).</summary>
    public string Errore => Visibile("Cr_Errore_Text") ? (ElementFinders.WaitById(_scope, "Cr_Errore_Text").Name ?? string.Empty) : string.Empty;

    /// <summary>Preme Modifica: l'editor si riapre in modifica (vedi ChangeRequestEditorFlow.EntraInModifica).</summary>
    public void Modifica() => ElementFinders.WaitButtonById(_scope, "Cr_Modifica_Button").SafeInvoke();

    public void ImpostaMotivoTransizione(string testo) => ElementFinders.WaitTextBoxById(_scope, "Cr_MotivoTransizione_Text").SetText(testo);

    private bool Visibile(string automationId)
    {
        var el = ElementFinders.TryFindById(_scope, automationId);
        return el != null && el.BoundingRectangle.Width > 0 && el.BoundingRectangle.Height > 0;
    }
}
