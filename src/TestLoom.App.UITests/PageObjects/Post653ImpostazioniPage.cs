using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>
/// Page Object delle funzioni post-6.5.3 nelle Impostazioni generali.
/// Usa solo AutomationId stabili e controlli reali UIA3: nessun accesso a ViewModel
/// in-process, nessun click a coordinate e nessuna dipendenza da testo tradotto.
/// </summary>
public sealed class Post653ImpostazioniPage
{
    private readonly Window _window;
    private readonly TestLoomApplication _app;

    public Post653ImpostazioniPage(Window window, TestLoomApplication app)
    {
        _window = window;
        _app = app;
        ElementFinders.WaitById(_window, "Impostazioni_Sidebar_List");
        ElementFinders.WaitById(_window, "Impostazioni_Dati_VerificaLocale_Button");
    }

    public bool ViewerAttivaVisibile => ElementFinders.ExistsById(_window, "Impostazioni_Dati_ViewerAttiva_Button");
    public bool ViewerDisattivaVisibile => ElementFinders.ExistsById(_window, "Impostazioni_Dati_ViewerDisattiva_Button");

    public string StatoLocale =>
        ElementFinders.WaitById(_window, "Impostazioni_StatoSincronizzazione_Text").AsLabel().Text;

    public void AttivaViewer(bool confirm)
    {
        ElementFinders.WaitButtonById(_window, "Impostazioni_Dati_ViewerAttiva_Button").SafeInvoke();
        Confirm(confirm);
    }

    public void DisattivaViewer(bool confirm)
    {
        ElementFinders.WaitButtonById(_window, "Impostazioni_Dati_ViewerDisattiva_Button").SafeInvoke();
        Confirm(confirm);
    }

    public void RichiediPinDati(bool confirm)
    {
        ElementFinders.WaitButtonById(_window, "Impostazioni_Dati_MantieniLocali_Button").SafeInvoke();
        Confirm(confirm);
    }

    private void Confirm(bool yes)
    {
        var popup = new ConfirmDialogPage(_app.WaitForWindowByContent("Confirm_Yes_Button"));
        if (yes) popup.ConfirmYes();
        else popup.ConfirmNo();
        Wait.ForCondition(() => _app.IsWindowClosedByContent("Confirm_Yes_Button"),
            "chiusura della conferma modale Impostazioni");
    }

    public void VerificaDatiLocali() =>
        ElementFinders.WaitButtonById(_window, "Impostazioni_Dati_VerificaLocale_Button").SafeInvoke();

    public void CreaBackupLocale() =>
        ElementFinders.WaitButtonById(_window, "Impostazioni_Backup_Crea_Button").SafeInvoke();

    public void VerificaUltimoBackup() =>
        ElementFinders.WaitButtonById(_window, "Impostazioni_Backup_VerificaZip_Button").SafeInvoke();

    public string MessaggioBackupVerifica =>
        ElementFinders.WaitById(_window, "Impostazioni_Messaggio_Text").AsLabel().Text;

    public void ImpostaPercorsoBackupEsterno(string path) =>
        ElementFinders.WaitTextBoxById(_window, "Impostazioni_Backup_EsternoPercorso_TextBox").SetText(path);

    public string PercorsoBackupEsterno =>
        ElementFinders.WaitTextBoxById(_window, "Impostazioni_Backup_EsternoPercorso_TextBox").Text;

    public void SalvaPercorsoBackupEsterno() =>
        ElementFinders.WaitButtonById(_window, "Impostazioni_Backup_SalvaPercorso_Button").SafeInvoke();

    public void CopiaUltimoBackupEsterno() =>
        ElementFinders.WaitButtonById(_window, "Impostazioni_Backup_CopiaEsterno_Button").SafeInvoke();

    public WorkspaceDiagnosticsPage ApriCentroIntegrita()
    {
        ElementFinders.WaitButtonById(_window, "Impostazioni_CentroIntegrita_Button").SafeInvoke();
        return new WorkspaceDiagnosticsPage(_app.WaitForWindowByContent("Integrity_Title_Text"));
    }

    public FailedSaveDraftInspectorPage EsaminaBozze()
    {
        ElementFinders.WaitButtonById(_window, "Impostazioni_Bozze_Esamina_Button").SafeInvoke();
        return new FailedSaveDraftInspectorPage(_app.WaitForWindowByContent("DraftInspector_Title_Text"));
    }

    public bool BozzeEmergenzaAccessibili =>
        ElementFinders.ExistsById(_window, "Impostazioni_Bozze_Apri_Button");

    public string ErroreTesto =>
        ElementFinders.WaitById(_window, "Impostazioni_Errore_Text").AsLabel().Text;
}
