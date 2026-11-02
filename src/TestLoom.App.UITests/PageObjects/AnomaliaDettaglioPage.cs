using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>Dialogo "Dettaglio anomalia" (AnomaliaDettaglioWindow), aperto dal bottone 'Dettaglio' della riga
/// (pagina Anomalie) o dal bottone 'Dettaglio' dell'anomalia collegata (pagina Test). E' una vera Window modale:
/// si trova come finestra di primo livello tramite uno dei suoi AutomationId.</summary>
public sealed class AnomaliaDettaglioPage
{
    public const string SalvaId = "AnomaliaDettaglio_Salva_Button";

    public Window Window { get; }

    public AnomaliaDettaglioPage(Window window) => Window = window;

    public string Owner => ElementFinders.WaitTextBoxById(Window, "AnomaliaDettaglio_Owner_TextBox").Text;

    public void ImpostaOwner(string owner) =>
        ElementFinders.WaitTextBoxById(Window, "AnomaliaDettaglio_Owner_TextBox").SetText(owner);

    public void EntraInModifica() =>
        ElementFinders.WaitButtonById(Window, "AnomaliaDettaglio_EntraInModifica_Button").SafeInvoke();

    public void ImpostaMotivoChiusura(string motivo) =>
        ElementFinders.WaitTextBoxById(Window, "AnomaliaDettaglio_MotivoChiusura_TextBox").SetText(motivo);

    public void ImpostaMotivoRiapertura(string motivo) =>
        ElementFinders.WaitTextBoxById(Window, "AnomaliaDettaglio_MotivoRiapertura_TextBox").SetText(motivo);

    public void Salva() => ElementFinders.WaitButtonById(Window, SalvaId).SafeInvoke();

    public bool HaRicaricaVersioneCorrente =>
        ElementFinders.ExistsById(Window, "AnomaliaDettaglio_RicaricaDopoConflitto_Button");

    public void RicaricaVersioneCorrente() =>
        ElementFinders.WaitButtonById(Window, "AnomaliaDettaglio_RicaricaDopoConflitto_Button").SafeInvoke();

    public void Chiudi() => ElementFinders.WaitButtonById(Window, "AnomaliaDettaglio_Chiudi_Button").SafeInvoke();
    public void Riapri() => ElementFinders.WaitButtonById(Window, "AnomaliaDettaglio_Riapri_Button").SafeInvoke();
    public void Elimina() => ElementFinders.WaitButtonById(Window, "AnomaliaDettaglio_Elimina_Button").SafeInvoke();
    public void Annulla() => ElementFinders.WaitButtonById(Window, "AnomaliaDettaglio_Annulla_Button").SafeInvoke();

    public bool HaRiapri => ElementFinders.ExistsById(Window, "AnomaliaDettaglio_Riapri_Button");
    public bool HaChiudi => ElementFinders.ExistsById(Window, "AnomaliaDettaglio_Chiudi_Button");
}
