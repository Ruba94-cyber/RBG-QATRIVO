using FlaUI.Core.AutomationElements;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using System.Runtime.InteropServices;

namespace TestLoom.App.UITests.PageObjects;

/// <summary>
/// Pagina Report — matrice configurabile, marcatura manuale N.A. (Addendum piano 6.4.0, punto 4: "nella
/// matrice che vado a creare nei report devo poter mettere anche NA nel caso in cui non sia applicabile").
///
/// Gli AutomationId delle celle sono dinamici (Riga/Colonna sanitizzati — vedi CellaMatriceReport.AutomationId
/// in ReportMatriceService.cs), quindi questa page object li ricostruisce con la stessa regola invece di
/// indovinarli: solo lettere/cifre restano, ogni altro carattere diventa '_'. Il menu "Segna N.A."/"Rimuovi
/// N.A." è un ContextMenu (click destro), letto direttamente dalla finestra principale come già fatto per il
/// menu account (MainShellPage.Logout) — non è un Flyout separato. Il dialogo della nota è invece TextEditDialog,
/// una vera finestra top-level separata (stesso pattern del dialogo "Propaga" di AuditPage).
///
/// Scoperta empirica (in aggiunta a quella già in README.md su ListBox.ItemTemplate): il Border radice del
/// DataTemplate della cella NON compare nella vista "controllo" di UI Automation usata da FindFirstDescendant
/// (verificato dal vivo con FlaUI — il suo AutomationId resta invisibile anche se impostato), mentre il
/// TextBlock interno (ControlType Text) sì. Questa page object cerca quindi sempre l'AutomationId del
/// TextBlock (suffisso "_Text", CellaMatriceReport.AutomationIdTesto), sia per leggere il testo sia per il
/// click destro: cliccare sul TextBlock attiva comunque il ContextMenu impostato sul Border contenitore
/// (bubbling standard di Avalonia), quindi non serve individuare il Border stesso.
/// </summary>
public sealed class ReportPage
{
    private readonly Window _window;
    private readonly AutomationElement _root;
    private readonly TestLoomApplication _app;

    public ReportPage(Window window, AutomationElement root, TestLoomApplication app)
    {
        _window = window;
        _root = root;
        _app = app;
    }

    public static bool IsShown(AutomationElement root) => ElementFinders.ExistsById(root, "Report_MatriceAsseX_ComboBox");

    private static string Sanitizza(string valore) => new(valore.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
    private static string CellaId(string riga, string colonna) => $"ReportMatrice_Cella_{Sanitizza(riga)}_{Sanitizza(colonna)}_Text";

    public bool CellaVisibile(string riga, string colonna) => ElementFinders.ExistsById(_root, CellaId(riga, colonna));

    public AutomationElement AttendiCella(string riga, string colonna) => ElementFinders.WaitById(_root, CellaId(riga, colonna));

    /// <summary>La matrice sta dentro il ScrollViewer esterno della pagina, sotto i Filtri: un click
    /// sintetizzato da FlaUI su un elemento fuori dall'area visibile (bounding rect calcolato dal layout ma
    /// scorso oltre il viewport) colpisce fisicamente qualunque cosa sia davvero a schermo in quel punto, non
    /// la cella. Calcolare a priori la percentuale di scroll esatta è fragile (altezza dei pannelli variabile
    /// col numero di dimensioni/filtri disponibili); più robusto spazzolare l'intervallo 0-100% un passo alla
    /// volta, ritentando il click destro ad ogni passo finché il menu non compare — stesso principio di
    /// tolleranza-a-un-tentativo-sfortunato già usato da Wait.Until.</summary>
    private void Scorri(int percentoVerticale)
    {
        var scrollPattern = ElementFinders.WaitById(_window, "Report_Page_ScrollViewer").Patterns.Scroll.PatternOrDefault
            ?? throw new InvalidOperationException("Il pannello del Report non espone il pattern UIA Scroll.");
        scrollPattern.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, percentoVerticale);
    }

    /// <summary>Testo mostrato dalla cella ("N.A.", una percentuale, o "— (0/N)"): letto da
    /// AutomationProperties.Name sulla cella stessa (root del DataTemplate), stessa tecnica già verificata da
    /// AuditPage.TooltipCella/NonConformitaPage.TestoPaginazione per un elemento non ripetuto in una ListBox.</summary>
    public string TestoCella(string riga, string colonna) => AttendiCella(riga, colonna).Name ?? "";

    /// <summary>Apre il ContextMenu della cella e restituisce la voce richiesta.</summary>
    private AutomationElement ApriMenuCella(string riga, string colonna, string automationIdVoce)
    {
        var percento = -8;
        return Wait.ForElement(() =>
        {
            percento = percento >= 100 ? 0 : Math.Min(100, percento + 8);
            Scorri(percento);

            // Reacquisire il peer dopo lo scroll: l'AutomationElement precedente può mantenere un rettangolo
            // fuori viewport. Cliccare solo nella porzione visibile della cella, intersecata con la pagina e la
            // finestra TestLoom, così input sintetizzato non può finire sulla taskbar quando il layout è virtualizzato.
            var cella = ElementFinders.TryFindById(_window, CellaId(riga, colonna));
            var viewport = ElementFinders.TryFindById(_window, "Report_Page_ScrollViewer");
            if (cella == null || viewport == null || cella.Properties.IsOffscreen.ValueOrDefault || viewport.Properties.IsOffscreen.ValueOrDefault)
                return null;

            var cellaRect = cella.BoundingRectangle;
            var viewportRect = viewport.BoundingRectangle;
            var finestraRect = _window.BoundingRectangle;
            var left = Math.Max(cellaRect.Left, Math.Max(viewportRect.Left, finestraRect.Left));
            var top = Math.Max(cellaRect.Top, Math.Max(viewportRect.Top, finestraRect.Top));
            var right = Math.Min(cellaRect.Right, Math.Min(viewportRect.Right, finestraRect.Right));
            var bottom = Math.Min(cellaRect.Bottom, Math.Min(viewportRect.Bottom, finestraRect.Bottom));
            if (right - left < 2 || bottom - top < 2) return null;

            var hwnd = _window.FrameworkAutomationElement.NativeWindowHandle;
            if (hwnd == IntPtr.Zero) return null;
            SetForegroundWindow(hwnd);
            if (GetForegroundWindow() != hwnd) return null;

            var punto = new System.Drawing.Point(left + (right - left) / 2, top + (bottom - top) / 2);
            FlaUI.Core.Input.Mouse.RightClick(punto);
            return ElementFinders.TryFindById(_window, automationIdVoce);
        }, $"voce di menu '{automationIdVoce}' dopo click destro sulla cella ({riga}, {colonna})", TimeSpan.FromSeconds(30));
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>Apre il ContextMenu e ne attiva da tastiera l'unica voce visibile (Giu' + Invio). Causa radice
    /// (diagnosticata 2026-10-08): il MenuItem Avalonia non espone InvokePattern, quindi SafeInvoke ripiegava su un
    /// click reale al centro della voce; a 1280x800 la cella in basso apre il menu a Y~752-780, sotto la barra
    /// delle applicazioni di Windows, e il click colpiva la taskbar invece del menu (che si chiudeva senza eseguire
    /// il Command). La tastiera non dipende dalla posizione a schermo: il menu aperto ha il focus e delle due voci
    /// solo una ha IsVisible=true (Segna N.A. oppure Rimuovi N.A.), quindi Giu' la seleziona.</summary>
    private void ApriEAttivaVoceMenu(string riga, string colonna, string automationIdVoce)
    {
        ApriMenuCella(riga, colonna, automationIdVoce);
        Thread.Sleep(100); // assestamento del menu prima della tastiera
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.DOWN);
        Thread.Sleep(100);
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
    }

    /// <summary>Marca la cella come N.A. con una nota opzionale: click destro sulla cella, voce di menu "Segna
    /// N.A.", poi il dialogo TextEditDialog (finestra separata) per la nota.</summary>
    public void SegnaNA(string riga, string colonna, string nota)
    {
        ApriEAttivaVoceMenu(riga, colonna, "ReportMatrice_SegnaNA_MenuItem");
        var dialogo = _app.WaitForWindowByContent("TextEdit_Save_Button");
        ElementFinders.WaitTextBoxById(dialogo, "TextEdit_Content_TextBox").SetText(nota);
        ElementFinders.WaitButtonById(dialogo, "TextEdit_Save_Button").SafeInvoke();
        Wait.ForCondition(() => _app.IsWindowClosedByContent("TextEdit_Save_Button"), "chiusura dialogo nota N.A.");
    }

    /// <summary>Apre "Ordina colonne/righe…", seleziona il valore e lo sposta su di una posizione, conferma con OK.</summary>
    public void SpostaSu(bool righe, string valore)
    {
        ElementFinders.WaitButtonById(_window, righe ? "Report_OrdinaRighe_Button" : "Report_OrdinaColonne_Button").SafeInvoke();
        var dialogo = _app.WaitForWindowByContent("OrdinaValori_Ok_Button");
        var lista = ElementFinders.WaitListBoxById(dialogo, "OrdinaValori_Lista");
        lista.Items.First(i => i.Text == valore).Select();
        ElementFinders.WaitButtonById(dialogo, "OrdinaValori_Su_Button").SafeInvoke();
        ElementFinders.WaitButtonById(dialogo, "OrdinaValori_Ok_Button").SafeInvoke();
        Wait.ForCondition(() => _app.IsWindowClosedByContent("OrdinaValori_Ok_Button"), "chiusura dialogo ordine valori");
    }

    /// <summary>Colonne indicate, ordinate per posizione orizzontale delle loro intestazioni di totale.</summary>
    public IReadOnlyList<string> OrdineColonne(params string[] colonne) => colonne
        .Select(c => (Colonna: c, Elemento: ElementFinders.WaitById(_root, $"ReportMatrice_TotaleColonna_{Sanitizza(c)}")))
        .OrderBy(x => x.Elemento.BoundingRectangle.Left)
        .Select(x => x.Colonna)
        .ToList();

    /// <summary>Rimuove la marcatura N.A. dalla cella: click destro, voce di menu "Rimuovi N.A." (nessun
    /// dialogo, l'azione è immediata).</summary>
    public void RimuoviNA(string riga, string colonna) =>
        ApriEAttivaVoceMenu(riga, colonna, "ReportMatrice_RimuoviNA_MenuItem");

    // ---------- Esportazione: dialog di sistema Salva con nome, file temporaneo scelto dal test ----------

    /// <summary>Clic su "Esporta Excel": restituisce il dialog di sistema di salvataggio (il file lo sceglie il test).</summary>
    public NativeFileDialogPage EsportaExcel()
    {
        ElementFinders.WaitButtonById(_window, "Report_EsportaExcel_Button").SafeInvoke();
        // Dialogo unico di export (sezioni, colonne, filtri): si conferma con le impostazioni di partenza, poi compare il Salva con nome.
        var opzioni = _app.WaitForWindowByContent("EsportaOpzioni_Esporta");
        ElementFinders.WaitButtonById(opzioni, "EsportaOpzioni_Esporta").SafeInvoke();
        return NativeFileDialogPage.Attendi(_app);
    }

    /// <summary>Clic su "Esporta PDF": restituisce il dialog di sistema di salvataggio.</summary>
    public NativeFileDialogPage EsportaPdf()
    {
        ElementFinders.WaitButtonById(_window, "Report_EsportaPdf_Button").SafeInvoke();
        return NativeFileDialogPage.Attendi(_app);
    }

    /// <summary>Vero se la cronologia dei report generati mostra il nome del file indicato.</summary>
    public bool CronologiaMostra(string nomeFile) =>
        _window.FindFirstDescendant(cf => cf.ByName(nomeFile)) != null;

    // ---------- Ordinamento righe/colonne: dialogo OrdinaValori (finestra separata) ----------

    /// <summary>Apre "Ordina righe…" o "Ordina colonne…" e restituisce la finestra del dialogo.</summary>
    public Window ApriDialogoOrdine(bool righe)
    {
        ElementFinders.WaitButtonById(_window, righe ? "Report_OrdinaRighe_Button" : "Report_OrdinaColonne_Button").SafeInvoke();
        return _app.WaitForWindowByContent("OrdinaValori_Ok_Button");
    }

    /// <summary>Seleziona un valore nella lista del dialogo "Ordina" e lo sposta in su di una posizione.</summary>
    public void SpostaSuNelDialogo(Window dialogo, string valore)
    {
        ElementFinders.WaitListBoxById(dialogo, "OrdinaValori_Lista").SelectByText(valore);
        ElementFinders.WaitButtonById(dialogo, "OrdinaValori_Su_Button").SafeInvoke();
    }

    /// <summary>Annulla il dialogo "Ordina": l'ordine della matrice non deve cambiare.</summary>
    public void AnnullaDialogoOrdine(Window dialogo)
    {
        ElementFinders.WaitButtonById(dialogo, "OrdinaValori_Annulla_Button").SafeInvoke();
        Wait.ForCondition(() => _app.IsWindowClosedByContent("OrdinaValori_Ok_Button"), "chiusura del dialogo Ordina (Annulla)");
    }

    /// <summary>Conferma il dialogo "Ordina" (OK) e attende la chiusura.</summary>
    public void ConfermaDialogoOrdine(Window dialogo)
    {
        ElementFinders.WaitButtonById(dialogo, "OrdinaValori_Ok_Button").SafeInvoke();
        Wait.ForCondition(() => _app.IsWindowClosedByContent("OrdinaValori_Ok_Button"), "chiusura del dialogo Ordina (OK)");
    }

    // ---------- Filtro template e dimensioni della matrice ----------

    /// <summary>Apre il flyout del filtro template (selezione multipla) e restituisce il contenuto.</summary>
    public FiltroMultiSelectPage ApriFiltroTemplate()
    {
        ElementFinders.WaitButtonById(_window, "Report_FiltroTemplate_Button").SafeInvoke();
        return new FiltroMultiSelectPage(_app);
    }

    /// <summary>Testo riepilogo del filtro template ("Tutti" o i nomi scelti).</summary>
    public string TestoFiltroTemplate => ElementFinders.WaitById(_window, "Report_FiltroTemplate_Button").Name ?? string.Empty;

    /// <summary>Sceglie la modalità del contenuto delle celle per etichetta ("Stato %", "Stato", "Test %", "Tempo totale", "Tempo medio").</summary>
    public void ImpostaModalitaMatrice(string etichetta) =>
        ElementFinders.WaitComboBoxById(_window, "Report_ModalitaMatrice_ComboBox").SelectByText(etichetta);

    /// <summary>Mostra o nasconde una singola voce di stato senza cambiare il conteggio sottostante.</summary>
    public void ImpostaStatoNascosto(string stato, bool nascosto)
    {
        var checkBox = ElementFinders.WaitById(_window, $"Report_Matrice_Nascondi{stato}_CheckBox").AsCheckBox();
        if (checkBox.IsChecked != nascosto)
            checkBox.Toggle();
    }

    /// <summary>Base dei tempi, visibile solo nelle modalità Tempo: "Tutte le esecuzioni" oppure "Ultimo stato".</summary>
    public void ImpostaBaseTempo(string etichetta) =>
        ElementFinders.WaitComboBoxById(_window, "Report_BaseTempo_ComboBox").SelectByText(etichetta);

    /// <summary>Attende che la cella mostri un testo che soddisfa la condizione (il ricalcolo avviene in background dopo ogni cambio).</summary>
    public void AttendiTestoCella(string riga, string colonna, Func<string, bool> condizione, string descrizione) =>
        Wait.ForCondition(() => CellaVisibile(riga, colonna) && condizione(TestoCella(riga, colonna)),
            $"cella ({riga}, {colonna}): {descrizione}");

    /// <summary>Filtro "Tempo operazione" del Report: operatore ("maggiore di" / "minore di") e minuti (vuoto = nessun filtro).</summary>
    public void ImpostaFiltroTempoOperazione(string operatore, string minuti)
    {
        ElementFinders.WaitComboBoxById(_window, "Report_FiltroTempoOperazione_ComboBox").SelectByText(operatore);
        ElementFinders.WaitTextBoxById(_window, "Report_FiltroTempoOperazione_TextBox").SetText(minuti);
    }

    /// <summary>Filtro "Tempo totale Test" del Report: operatore ("maggiore di" / "minore di") e minuti (vuoto = nessun filtro).</summary>
    public void ImpostaFiltroTempoTotaleTest(string operatore, string minuti)
    {
        ElementFinders.WaitComboBoxById(_window, "Report_FiltroTempoTotaleTest_ComboBox").SelectByText(operatore);
        ElementFinders.WaitTextBoxById(_window, "Report_FiltroTempoTotaleTest_TextBox").SetText(minuti);
    }

    /// <summary>Sceglie la dimensione dell'asse X (colonne della matrice) dal combo, per etichetta.</summary>
    public void ImpostaAsseX(string etichetta) =>
        ElementFinders.WaitComboBoxById(_window, "Report_MatriceAsseX_ComboBox").SelectByText(etichetta);

    /// <summary>Sceglie la dimensione dell'asse Y (righe della matrice) dal combo, per etichetta.</summary>
    public void ImpostaAsseY(string etichetta) =>
        ElementFinders.WaitComboBoxById(_window, "Report_MatriceAsseY_ComboBox").SelectByText(etichetta);

    /// <summary>Aggiunge un campo all'asse colonne (pulsante "+ Aggiungi campo" dell'asse X, 6.6.1).</summary>
    public void AggiungiCampoColonne() => ElementFinders.WaitButtonById(_window, "Report_MatriceAggiungiCampoX_Button").SafeInvoke();

    /// <summary>Passa il contenuto delle celle a "Stato" (voce italiana del selettore "Contenuto delle celle", 6.6.1).</summary>
    public void ImpostaModalitaStato() =>
        ElementFinders.WaitComboBoxById(_window, "Report_ModalitaMatrice_ComboBox").SelectByText("Stato");

    /// <summary>Apre la sezione "Filtri avanzati" del Report (chiusa di default): pulsante con AutomationId Report_FiltriAvanzati_Apri_Button.</summary>
    public void ApriFiltriAvanzati()
    {
        if (ElementFinders.ExistsById(_window, "Report_FiltriAvanzati_Nascondi_Button"))
            return;
        var btn = ElementFinders.TryFindById(_window, "Report_FiltriAvanzati_Apri_Button");
        if (btn != null)
        {
            btn.SafeInvoke();
            return;
        }
        var fallback = _root.FindFirstDescendant(cf => cf.ByName("Filtri avanzati"));
        fallback?.SafeInvoke();
    }
}
