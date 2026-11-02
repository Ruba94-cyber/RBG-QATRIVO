using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>Editor Segnalazione rete (SegnalazioneReteEditorView): una NC senza Test collegato, aperta da
/// "Nuova segnalazione rete" nella pagina Anomalie/CR.</summary>
public sealed class SegnalazioneReteEditorPage
{
    private readonly AutomationElement _scope;

    public SegnalazioneReteEditorPage(AutomationElement scope)
    {
        _scope = scope;
        ElementFinders.WaitById(scope, "Segnalazione_Title_Text");
    }

    public void AggiungiNumeroPratica(string valore)
    {
        ElementFinders.WaitTextBoxById(_scope, "Segnalazione_NumeroPratica_Input").SetText(valore);
        ElementFinders.WaitButtonById(_scope, "Segnalazione_NumeroPratica_Aggiungi_Button").SafeInvoke();
    }

    public void AggiungiVin(string valore)
    {
        ElementFinders.WaitTextBoxById(_scope, "Segnalazione_Matricola_Input").SetText(valore);
        ElementFinders.WaitButtonById(_scope, "Segnalazione_Matricola_Aggiungi_Button").SafeInvoke();
    }

    public void AggiungiLicenza(string valore)
    {
        ElementFinders.WaitTextBoxById(_scope, "Segnalazione_Licenza_Input").SetText(valore);
        ElementFinders.WaitButtonById(_scope, "Segnalazione_Licenza_Aggiungi_Button").SafeInvoke();
    }

    /// <summary>La sezione "Ambito impattato" (componente/Veicoli/Funzionalita') e' obbligatoria: SalvaCommand
    /// rifiuta il salvataggio con "Indica almeno un elemento di ambito: componente, Veicolo, Funzionalità o
    /// Strumento" se resta vuota (dialog "Campi obbligatori mancanti"). Come
    /// ChangeRequestEditorPage.AggiungiEcu: sotto soglia (10 voci) "Aggiungi componente..." apre
    /// MultiSelectPickerFlyout con CheckBox anonime (nessun AutomationId, vedi COPERTURA_FLAUI.md), cercate
    /// per Content/Nome; il pulsante di conferma usa invece il suo AutomationId stabile.</summary>
    public void AggiungiEcu(TestLoomApplication app, string ecuName)
    {
        ElementFinders.WaitButtonById(_scope, "Segnalazione_AggiungiEcu_Button").SafeInvoke();
        var check = Wait.Until(
            () => _scope.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.CheckBox).And(cf.ByName(ecuName))),
            _ => true, description: $"CheckBox '{ecuName}' nel flyout 'Scegli componente'");
        check.AsCheckBox().Toggle();
        ElementFinders.WaitButtonById(_scope, "MultiSelectPicker_Add_Button").SafeInvoke();
    }

    public void SetDescrizioneBreve(string testo) => ElementFinders.WaitTextBoxById(_scope, "Segnalazione_DescrizioneBreve_TextBox").SetText(testo);
    public void SetDescrizione(string testo) => ElementFinders.WaitTextBoxById(_scope, "Segnalazione_Descrizione_TextBox").SetText(testo);

    public void SetSeverita(string etichetta) => ElementFinders.WaitComboBoxById(_scope, "Segnalazione_Severita_Combo").SelectByText(etichetta);
    public void SetPriorita(string etichetta) => ElementFinders.WaitComboBoxById(_scope, "Segnalazione_Priorita_Combo").SelectByText(etichetta);
    public void SetStato(string etichetta) => ElementFinders.WaitComboBoxById(_scope, "Segnalazione_Stato_Combo").SelectByText(etichetta);

    /// <summary>Digita senza premere Invio/Aggiungi (il Salva deve comunque considerarli inseriti).</summary>
    public void DigitaVinSenzaInvio(string valore) => ElementFinders.WaitTextBoxById(_scope, "Segnalazione_Matricola_Input").SetText(valore);
    public void DigitaNumeroPraticaSenzaInvio(string valore) => ElementFinders.WaitTextBoxById(_scope, "Segnalazione_NumeroPratica_Input").SetText(valore);
    public void DigitaLicenzaSenzaInvio(string valore) => ElementFinders.WaitTextBoxById(_scope, "Segnalazione_Licenza_Input").SetText(valore);

    /// <summary>Salva nell'intestazione (sempre visibile, accanto a Torna).</summary>
    public void SalvaDaHeader() => ElementFinders.WaitButtonById(_scope, "Segnalazione_Salva_Header_Button").SafeInvoke();
    public bool HaSalvaHeader => ElementFinders.ExistsById(_scope, "Segnalazione_Salva_Header_Button");
    public bool HaBannerErrore => ElementFinders.ExistsById(_scope, "Segnalazione_MessaggioErrore_Chiudi_Button");
    public void ChiudiBannerErrore() => ElementFinders.WaitButtonById(_scope, "Segnalazione_MessaggioErrore_Chiudi_Button").SafeInvoke();

    public void Salva() => ElementFinders.WaitButtonById(_scope, "Segnalazione_Salva_Button").SafeInvoke();
    public void Annulla() => ElementFinders.WaitButtonById(_scope, "Segnalazione_Annulla_Button").SafeInvoke();
}
