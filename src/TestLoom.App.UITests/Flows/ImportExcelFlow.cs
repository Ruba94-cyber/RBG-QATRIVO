using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Flows;

/// <summary>
/// Import Excel di una sotto-sezione master data già in modifica, fino all'anteprima:
/// "Importa Excel" → avviso di formato (ConfirmDialog, "Continua") → dialog di sistema "Apri" (file .xlsx temporaneo)
/// → opzioni foglio/riga (<c>ImportExcelOpzioniWindow</c>, "Importa") → anteprima (ConfirmDialog
/// "Anteprima importazione Excel", con "Importa" / "Annulla"). Il flusso si ferma sull'anteprima: confermare o
/// annullare è decisione del singolo test. Nulla viene salvato su disco da questo flusso.
/// </summary>
public static class ImportExcelFlow
{
    /// <summary>Messaggio che identifica l'anteprima tra i ConfirmDialog (la guida ha un testo diverso).</summary>
    public const string MarcatoreAnteprima = "Righe lette:";

    public static ConfirmDialogPage ApriAnteprima(TestLoomApplication app, AutomationElement pagina, string importaButtonId, string percorsoFile)
    {
        ElementFinders.WaitButtonById(pagina, importaButtonId).SafeInvoke();

        // Avviso di formato: "Continua" (Confirm_Yes_Button) apre il dialog di sistema.
        var guida = app.WaitForWindow(w => ElementFinders.TryFindById(w, "Confirm_Message_Text") is { } m
            && (m.Name ?? "").Contains("Colonne:", StringComparison.Ordinal), descrizione: "avviso di formato import Excel");
        new ConfirmDialogPage(guida).ConfirmYes();

        NativeFileDialogPage.Attendi(app).ConfermaPercorso(percorsoFile);

        // Opzioni foglio/riga: "Importa" passa il file all'import vero e proprio.
        var opzioni = app.WaitForWindow(w => ElementFinders.TryFindById(w, "ImportExcel_Importa_Button") != null,
            descrizione: "finestra opzioni import Excel (foglio/riga)");
        ElementFinders.WaitButtonById(opzioni, "ImportExcel_Importa_Button").SafeInvoke();

        // Anteprima: la finestra di conferma con il riepilogo (righe lette, nuove, saltate).
        var anteprima = app.WaitForWindow(w => ElementFinders.TryFindById(w, "Confirm_Message_Text") is { } m
            && (m.Name ?? "").Contains(MarcatoreAnteprima, StringComparison.Ordinal), descrizione: "anteprima import Excel");
        return new ConfirmDialogPage(anteprima);
    }

    /// <summary>Attende che nessuna finestra mostri più l'anteprima (dopo Importa o Annulla).</summary>
    public static void AttendiChiusuraAnteprima(TestLoomApplication app) =>
        Wait.ForCondition(() => app.TopLevelWindows().All(w => ElementFinders.TryFindById(w, "Confirm_Message_Text") is not { } m
            || !(m.Name ?? "").Contains(MarcatoreAnteprima, StringComparison.Ordinal)),
            "chiusura dell'anteprima import Excel");
}
