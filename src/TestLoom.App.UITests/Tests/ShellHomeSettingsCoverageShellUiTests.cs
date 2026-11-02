using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using static TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Copertura Shell mancante: logout/rientro, Ctrl+K (ricerca globale con risultato, senza risultati, Test creato),
/// Ctrl+1..7 per le sezioni, Alt+Sinistra, e pulsanti principali interamente visibili alle dimensioni 1280x768 e
/// minima (1000x640). Le funzioni gia' coperte da EscChiudeOverlayTests, NavigazioneIndietroUiTests e
/// CrossModuleNavigationMatrixUiTests non vengono ripetute.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Coverage")]
[Trait("Area", "Shell")]
public sealed class ShellHomeSettingsCoverageShellUiTests : UiTestBase
{
    public ShellHomeSettingsCoverageShellUiTests() : base(TestWorkspace.CreateLoggedIn(nameof(ShellHomeSettingsCoverageShellUiTests))) { }

    [Fact]
    public void Logout_DaMenuAccount_TornaAllAccessoERientraConPin()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.DismissNotificationsIfShown();

            // Logout: il menu account "Esci" riporta alla schermata di accesso (verificato dentro Logout()).
            shell.Logout();
            Assert.True(LoginPage.IsShown(window), "dopo Esci deve comparire la schermata di accesso");

            new LoginPage(window).LoginAs(TestWorkspace.DefaultOperatorName, TestWorkspace.DefaultOperatorPin);
            Wait.ForCondition(() => IdVisibile(window, "Home_Title_Text"), "Home visibile dopo il nuovo accesso", TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Logout_DaMenuAccount_TornaAllAccessoERientraConPin), ex); }
    }

    [Fact]
    public void CtrlK_CercaUnaEcu_EnterApreIlPrimoRisultatoEChiudeLaRicerca()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).DismissNotificationsIfShown();

            ApriRicercaGlobaleConCtrlK(window);
            ElementFinders.WaitTextBoxById(window, "Main_RicercaGlobale_TextBox").SetText("Componente-QA");
            Wait.ForCondition(() => RisultatoRicerca(window, TestWorkspace.DefaultEcuName) != null,
                $"risultato '{TestWorkspace.DefaultEcuName}' nella ricerca globale", TimeSpan.FromSeconds(20));

            // Invio con il fuoco nella casella: apre il primo risultato (componente -> sezione Impostazioni) e chiude l'overlay.
            ElementActions.PressKey(window, VirtualKeyShort.ENTER);
            Wait.ForCondition(() => !VisibileInUia(ElementFinders.TryFindById(window, "Main_RicercaGlobale_TextBox")),
                "la ricerca globale si chiude dopo Invio", TimeSpan.FromSeconds(10));
            Wait.ForCondition(() => IdVisibile(window, "Impostazioni_Sidebar_List"), "sezione Impostazioni aperta dal risultato", TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CtrlK_CercaUnaEcu_EnterApreIlPrimoRisultatoEChiudeLaRicerca), ex); }
    }

    [Fact]
    public void CtrlK_SenzaCorrispondenze_MostraMessaggioVuotoEEscChiude()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).DismissNotificationsIfShown();

            ApriRicercaGlobaleConCtrlK(window);
            ElementFinders.WaitTextBoxById(window, "Main_RicercaGlobale_TextBox").SetText("zz-nessuna-corrispondenza-qa");
            Wait.ForCondition(() => TestoVisibile(window, "Nessun risultato trovato.") != null,
                "messaggio 'Nessun risultato trovato.' nella ricerca globale", TimeSpan.FromSeconds(20));
            Assert.Null(RisultatoRicerca(window, "zz-nessuna-corrispondenza-qa"));

            ElementActions.PressKey(window, VirtualKeyShort.ESCAPE);
            Wait.ForCondition(() => !VisibileInUia(ElementFinders.TryFindById(window, "Main_RicercaGlobale_TextBox")),
                "la ricerca globale si chiude con Esc", TimeSpan.FromSeconds(10));
            Wait.ForCondition(() => !VisibileInUia(ElementFinders.TryFindById(window, "Main_RicercaGlobale_InCorso")),
                "indicatore di ricerca in corso spento a fine ricerca", TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CtrlK_SenzaCorrispondenze_MostraMessaggioVuotoEEscChiude), ex); }
    }

    [Fact]
    public void CtrlK_TestAppenaCreato_ApreLEditorDalRisultato()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.DismissNotificationsIfShown();

            var marcatore = "QaRicerca" + Guid.NewGuid().ToString("N")[..8];
            CreateTestCaseFlow.Run(App, shell, marcatore, TestWorkspace.DefaultEcuName);

            ApriRicercaGlobaleConCtrlK(window);
            ElementFinders.WaitTextBoxById(window, "Main_RicercaGlobale_TextBox").SetText(marcatore);
            Wait.ForCondition(() => RisultatoRicerca(window, marcatore) != null,
                "il Test appena creato compare nella ricerca globale", TimeSpan.FromSeconds(30));

            RisultatoRicerca(window, marcatore)!.AsButton().SafeInvoke();
            Wait.ForCondition(() => IdVisibile(window, "Editor_Title_Text"), "editor del Test aperto dal risultato", TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CtrlK_TestAppenaCreato_ApreLEditorDalRisultato), ex); }
    }

    [Fact]
    public void AltSinistra_TornaAllaSezionePrecedente()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.DismissNotificationsIfShown();
            shell.GoToAnomalie();

            // Il fuoco va sulla barra laterale prima del tasto: una casella o una griglia della pagina aperta
            // consumerebbe Alt+Sinistra prima della scorciatoia della finestra (MainWindow.OnKeyDown).
            ElementFinders.WaitById(window, "Main_Indietro_Button").Focus();
            var fuocoPrima = App.SafeFocusedElementId() ?? "?";
            PremiCombinazione(window, VirtualKeyShort.ALT, VirtualKeyShort.LEFT);
            var tornata = Wait.TryForCondition(() => IdVisibile(window, "Home_Title_Text"), TimeSpan.FromSeconds(15));
            Assert.True(tornata,
                $"Alt+Sinistra non riporta alla Home (fuoco prima del tasto: '{fuocoPrima}', dopo: '{App.SafeFocusedElementId() ?? "?"}')");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(AltSinistra_TornaAllaSezionePrecedente), ex); }
    }

    // Regressione sulla specifica (commento di MainWindow.OnKeyDown: "Alt+← come nei browser"): con il fuoco in una casella
    // di testo della pagina la scorciatoia deve comunque tornare indietro.
    [Fact]
    public void AltSinistra_ConFuocoNellaCasellaDellaPagina_TornaIndietroComeNeiBrowser()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.DismissNotificationsIfShown();
            shell.GoToAnomalie();

            var casella = ElementFinders.WaitById(window, "Anomalie_FiltroTesto_Input");
            casella.Focus();
            var fuoco = App.SafeFocusedElementId();
            Assert.True(fuoco == "Anomalie_FiltroTesto_Input",
                $"precondizione non soddisfatta: il fuoco non e' sulla casella della pagina (fuoco='{fuoco ?? "?"}')");

            PremiCombinazioneSenzaRifocalizzare(window, VirtualKeyShort.ALT, VirtualKeyShort.LEFT);
            var tornata = Wait.TryForCondition(() => IdVisibile(window, "Home_Title_Text"), TimeSpan.FromSeconds(10));
            Assert.True(tornata,
                "Alt+Sinistra con il fuoco nella casella 'Anomalie_FiltroTesto_Input' non torna alla Home: " +
                "il controllo di testo consuma il tasto prima di MainWindow.OnKeyDown");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(AltSinistra_ConFuocoNellaCasellaDellaPagina_TornaIndietroComeNeiBrowser), ex); }
    }


    // 1280x768 esterni (cornice inclusa) entrano nell'area di lavoro di questo PC (1280x800 in coordinate UIA): la misura
    // e' quella esterna impostata con SetWindowPos, poi si verificano anche i pulsanti al minimo di contenuto dell'app.
    [Fact]
    public void Finestra1280x768_PulsantiPrincipaliInteramenteVisibili()
    {
        try
        {
            VerificaPulsantiPrincipali(1280, 768, esatta: true);
            VerificaPulsantiPrincipali(1000, 640, esatta: false);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Finestra1280x768_PulsantiPrincipaliInteramenteVisibili), ex); }
    }

    [Fact]
    public void FinestraMinima1000x640_PulsantiPrincipaliInteramenteVisibili()
    {
        try { VerificaPulsantiPrincipali(1000, 640, esatta: false); }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FinestraMinima1000x640_PulsantiPrincipaliInteramenteVisibili), ex); }
    }

    /// <summary>Ridimensiona la finestra e verifica che i pulsanti principali di ogni sezione restino interi nella
    /// finestra. Raccoglie tutti gli esiti prima di asserire, cosi' una sola esecuzione elenca ogni controllo tagliato.</summary>
    private void VerificaPulsantiPrincipali(int larghezza, int altezza, bool esatta)
    {
        var window = App.WaitForMainWindow();
        var shell = new MainShellPage(window);
        shell.DismissNotificationsIfShown();
        // Richiesta limitata all'area di lavoro: min(richiesta, area). La misura verificata e' quella ESTERNA impostata con
        // SetWindowPos (GetWindowRect): BoundingRectangle e' l'area client, piu' piccola della cornice (13x35 px su questo PC),
        // e confrontarla con la richiesta esterna dava un falso scarto 1267x733 contro 1280x768.
        var (areaLarghezza, areaAltezza) = AreaDiLavoroLogica(window);
        var larghezzaUtile = Math.Min(larghezza, (int)Math.Floor(areaLarghezza));
        var altezzaUtile = Math.Min(altezza, (int)Math.Floor(areaAltezza));
        PortaFinestra(window, larghezzaUtile, altezzaUtile);
        var misura = DimensioniEsterne(window);
        // La misura deve coincidere con quella utile (esatta), oppure non essere inferiore al minimo di contenuto.
        var dimensioniOk = esatta
            ? Math.Abs(misura.Larghezza - larghezzaUtile) <= 4 && Math.Abs(misura.Altezza - altezzaUtile) <= 4
            : misura.Larghezza >= larghezzaUtile - 4 && misura.Altezza >= altezzaUtile - 4;
        Assert.True(dimensioniOk,
            $"Dimensione esterna {misura.Larghezza}x{misura.Altezza} (richiesta {larghezza}x{altezza}, utile {larghezzaUtile}x{altezzaUtile}, " +
            $"esatta={esatta}). {DescriviGeometria(window)}.");

        var errori = new List<string>();
        Verifica(window, errori, "Shell", "Main_AccountMenu_Button");
        Verifica(window, errori, "Shell", "Main_RicercaGlobale_Button");
        shell.GoToHome();
        Verifica(window, errori, "Home", "Home_Personalizza_Button");
        shell.GoToTestList();
        Verifica(window, errori, "Test", "TestList_New_Button");
        shell.GoToAnomalie();
        Verifica(window, errori, "Anomalie", "Anomalie_NuovaChangeRequest_Button");
        shell.GoToImpostazioni();
        Verifica(window, errori, "Impostazioni", "Impostazioni_Sub_generale");

        Assert.True(errori.Count == 0,
            $"Pulsanti tagliati alla finestra {misura.Larghezza}x{misura.Altezza} (richiesta {larghezza}x{altezza}):\n - " + string.Join("\n - ", errori));
    }

    private static void Verifica(Window window, List<string> errori, string pagina, string automationId)
    {
        var el = ElementFinders.TryFindById(window, automationId);
        if (!DentroFinestra(window, el))
            errori.Add($"{pagina}: '{automationId}' {Descrivi(el)} fuori da {Descrivi(window)}");
    }

    private static string Descrivi(AutomationElement? e)
    {
        if (e == null) return "assente";
        var r = e.BoundingRectangle;
        return $"({r.Left},{r.Top} {r.Width}x{r.Height})";
    }
}
