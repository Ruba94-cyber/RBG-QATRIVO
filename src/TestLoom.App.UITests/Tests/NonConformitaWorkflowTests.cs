using System.Linq;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura P1 per il workflow di una Non Conformita' (chiudi/riapri/elimina): fino a questa
/// iterazione nessun test guidava questi comandi dalla UI reale (solo NonConformitaRulesService a livello
/// unit in TestLoom.App.Tests). Semina una NC gia' con Severita' impostata (SalvaNc la richiede obbligatoria)
/// via NonConformitaRepository.Save — stessa tecnica diretta di TestWorkspace/StrumentiOrdineUiTests, non la
/// UI — per concentrarsi sui comandi di workflow invece che sulla creazione stessa della NC (creata sempre a
/// valle di un esito Non Conforme dentro un Test, gia' indirettamente esercitato da AuditPropagazioneTests/
/// TestEditorOperazioniTests).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class NonConformitaWorkflowTests : UiTestBase
{
    private const string NcId = "NC-QA-0001";

    public NonConformitaWorkflowTests() : base(CreaWorkspaceConNc()) { }

    private static TestWorkspace CreaWorkspaceConNc()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(NonConformitaWorkflowTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        state.NonConformita.Save(new NonConformita
        {
            Id = NcId,
            TestCaseId = "",
            DataApertura = DateTime.UtcNow,
            Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.InVerifica,
            Descrizione = "NC seminata per il test di workflow",
            Owner = "",
        });
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-QA-0002",
            TestCaseId = "",
            DataApertura = DateTime.UtcNow.AddMinutes(-1),
            Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.InVerifica,
            Descrizione = "NC con ticket TICKET per il test dei filtri",
            DaSegnalazioneRete = true,
            CollegamentiPolarion = { new CollegamentoPolarion { Codice = "TICKET-12345", Url = "https://ticketing.example/TICKET-12345" } },
        });
        return workspace;
    }

    [Fact]
    public void FiltroTicketEsta_DistingueNcConESenzaTicket_EsponeLinkDiretto()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 2,
                "le due NC seed compaiono prima di applicare il filtro TICKET");
            anomalie.EspandiFiltri();

            var filtro = ElementFinders.WaitComboBoxById(window, "Anomalie_FiltroTicketEsta_Combo");
            filtro.SelectByText("Con ticket TICKET sulla NC");
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 1,
                "il filtro con ticket TICKET lascia una sola NC");
            Assert.Contains("NC-QA-0002", anomalie.IdRighe());
            var linkTicket = window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
                .FirstOrDefault(b => (b.Name ?? "") == "TICKET-12345");
            Assert.NotNull(linkTicket);
            Assert.True(linkTicket!.IsEnabled, "il ticket TICKET con URL web valida è un link attivo dalla tabella");
            var dettaglioNc = ElementFinders.WaitById(window, "Anomalie_Riga_Dettaglio_Button").BoundingRectangle;
            var modificaRapidaNc = ElementFinders.WaitById(window, "Anomalie_Riga_ModificaRapida_Button").BoundingRectangle;
            Assert.True(dettaglioNc.Width > 0 && modificaRapidaNc.Width > 0,
                "le due azioni comuni Dettaglio e Modifica rapida sono visibili nella riga NC");
            Assert.True(modificaRapidaNc.Left > dettaglioNc.Left && Math.Abs(dettaglioNc.Top - modificaRapidaNc.Top) < 2,
                "le azioni della riga NC sono allineate sulla stessa riga anche a 1280×800");

            filtro = ElementFinders.WaitComboBoxById(window, "Anomalie_FiltroTicketEsta_Combo");
            filtro.SelectByText("Senza ticket TICKET sulla NC");
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 1,
                "il filtro senza ticket TICKET lascia una sola NC");
            Assert.Contains(NcId, anomalie.IdRighe());
            Assert.DoesNotContain("NC-QA-0002", anomalie.IdRighe());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(FiltroTicketEsta_DistingueNcConESenzaTicket_EsponeLinkDiretto), ex);
        }
    }

    [Fact]
    public void ChiudiRiapriElimina_NcSeminata_SeguonoIComandiDiWorkflow()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var anomalie = shell.GoToAnomalie();
            anomalie.Cerca(NcId);
            Wait.ForCondition(() => anomalie.TotaleFiltrato() == 1, "la NC seminata compare cercandola per Id");

            // Chiudi richiede un motivo obbligatorio (ChiudiAnomalia nel dialogo di dettaglio: Errore senza).
            var dettaglio = ApriDettaglio(window);
            dettaglio.ImpostaMotivoChiusura("Verificata e risolta in QA");
            dettaglio.Chiudi();

            ConfermaEAttendiChiusuraDialogo();
            Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId), "il dialogo di dettaglio si chiude dopo la chiusura NC");

            // Il filtro "Stato" della pagina Anomalie/CR default a "Aperta" (NonConformitaViewModel,
            // filtroStato = StatoNonConformita.Aperta di default): appena chiusa, la riga esce dal filtro
            // corrente. Nessun AutomationId sul combo/link di reset di questo filtro specifico (a differenza del
            // suo equivalente per le CR, Anomalie_Cr_RimuoviFiltri_Button): selezioniamo "Chiusa" per ritrovarla.
            SelezionaFiltroStato(window, "Chiusa");
            dettaglio = ApriDettaglio(window);
            Wait.ForCondition(() => dettaglio.HaRiapri, "dopo la chiusura il dialogo mostra 'Riapri anomalia' al posto di 'Chiudi'");

            // Anche la riapertura richiede un motivo scritto.
            dettaglio.ImpostaMotivoRiapertura("Il difetto si ripresenta in QA");
            dettaglio.Riapri();
            ConfermaEAttendiChiusuraDialogo();
            Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId), "il dialogo di dettaglio si chiude dopo la riapertura");

            // Simmetrico: dopo la riapertura la NC torna "Aperta" e sparisce dal filtro "Chiusa" appena
            // impostato sopra.
            SelezionaFiltroStato(window, "Aperta");
            dettaglio = ApriDettaglio(window);
            Wait.ForCondition(() => dettaglio.HaChiudi, "dopo la riapertura torna visibile 'Chiudi anomalia'");

            // Master-only: l'operatore seminato da TestWorkspace.CreateLoggedIn e' sempre Master.
            dettaglio.Elimina();
            ConfermaEAttendiChiusuraDialogo();
            Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId), "il dialogo di dettaglio si chiude dopo l'eliminazione");

            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 300) == 0,
                "la NC eliminata non compare piu' cercandola per Id");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ChiudiRiapriElimina_NcSeminata_SeguonoIComandiDiWorkflow), ex);
        }
    }

    /// <summary>Fattore tra pixel logici di Avalonia e coordinate UIA del processo di test (come
    /// MasterDataLayoutSchermoPiccoloTests.ScalaCoordinateUia): 1.0 se il processo non e' DPI-aware.</summary>
    private static double ScalaCoordinateUia(Window window)
    {
        try
        {
            var hwnd = window.Properties.NativeWindowHandle.ValueOrDefault;
            var awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
            return awareness switch
            {
                0 => 1.0,
                1 => GetDpiForSystem() / 96.0,
                _ => GetDpiForWindow(hwnd) / 96.0,
            };
        }
        catch { return 1.0; }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr valore);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    [Fact]
    public void Dettaglio_ModificaOwnerESalva_DialogoEntroLoSchermoEValorePersistito()
    {
        var window = App.WaitForMainWindow();
        var shell = new MainShellPage(window);
        var anomalie = shell.GoToAnomalie();
        anomalie.Cerca(NcId);
        Wait.ForCondition(() => anomalie.TotaleFiltrato() == 1, "la NC seminata compare cercandola per Id");

        var dettaglio = ApriDettaglio(window);
        var r = dettaglio.Window.BoundingRectangle;
        // I limiti sono in pixel logici: le coordinate UIA sono fisiche quando il processo di test e' DPI-aware (display al 200%).
        var scala = ScalaCoordinateUia(dettaglio.Window);
        Assert.True(r.Width <= 1280 * scala && r.Height <= 800 * scala,
            $"il dialogo deve stare entro 1280x800 logici (era {r.Width / scala:0}x{r.Height / scala:0} logici, scala {scala:0.00})");

        dettaglio.ImpostaOwner("Mario Rossi");
        dettaglio.Salva();
        Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId), "il dialogo si chiude dopo Salva");

        var riaperto = ApriDettaglio(window);
        Wait.ForCondition(() => riaperto.Owner == "Mario Rossi", "l'Owner salvato e' mostrato alla riapertura");
        riaperto.Annulla();
        Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId), "il dialogo si chiude con Annulla");
    }

    private AnomaliaDettaglioPage ApriDettaglio(FlaUI.Core.AutomationElements.Window window)
    {
        ElementFinders.WaitButtonById(window, "Anomalie_Riga_Dettaglio_Button").SafeInvoke();
        var dettaglio = new AnomaliaDettaglioPage(App.WaitForWindowByContent(AnomaliaDettaglioPage.SalvaId));
        dettaglio.EntraInModifica();
        return dettaglio;
    }

    private void ConfermaEAttendiChiusuraDialogo()
    {
        var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
        conferma.ConfirmYes();
        Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura del dialog di conferma");
    }

    /// <summary>Seleziona il filtro stato delle NC usando il suo AutomationId dedicato, senza confonderlo
    /// con il filtro stato CR presente nella stessa schermata.</summary>
    private static void SelezionaFiltroStato(FlaUI.Core.AutomationElements.Window window, string valore)
    {
        ElementFinders.WaitComboBoxById(window, "Anomalie_FiltroStato_Combo").SelectByText(valore);
    }
}
