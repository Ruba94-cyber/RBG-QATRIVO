using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;
using Xunit.Abstractions;

namespace TestLoom.App.UITests.Tests;

/// <summary>Layout delle schermate 6.7.0 su schermo piccolo (Panasonic G2: 1920x1200 al 150% = 1280x800 logici,
/// area di lavoro 1280x776). Per ogni pagina: i controlli interattivi chiave devono essere nella finestra (dopo
/// scorrimento se stanno in una scroll viewer), con dimensione non nulla, senza sovrapporsi ai vicini noti; le
/// azioni principali (Salva, Esporta, selettore di modalita') devono essere raggiungibili. Una schermata per pagina
/// in .ai/artifacts/schermo-piccolo-670/ per la revisione umana. I problemi di layout reali sono raccolti tutti
/// insieme e il test li elenca; i test con problemi aperti sono marcati Category=BugAperto.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Layout")]
public sealed class SchermoPiccolo670UiTests : UiTestBase
{
    private const int LarghezzaG2 = 1280;
    private const int AltezzaG2 = 776;
    private const int AltezzaMinimaGrigliaLogica = 100;
    private const int TolleranzaPixel = 2;
    private static readonly ConcurrentDictionary<string, string?> TestRasPerWorkspace = new();
    private readonly ITestOutputHelper _out;
    private readonly List<string> _problemi = new();
    private double _scala = 1.0;
    // Budget per test: allo scadere il watchdog termina TestLoom, cosi' le chiamate UIA pendenti falliscono invece di
    // bloccare la coda FlaUI condivisa (incidente: un popup aperto sull'Anomalie ha tenuto il run ~40 minuti).
    private static readonly TimeSpan BudgetTest = TimeSpan.FromSeconds(150);
    private Timer? _watchdog;

    public SchermoPiccolo670UiTests(ITestOutputHelper output)
        : base(CreaWorkspace()) => _out = output;

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateDemoCompleta(nameof(SchermoPiccolo670UiTests));
        // Il Test RAS UnitLogger con piu' operazioni della demo (stesso criterio della galleria screenshot): id letto
        // prima del lancio di TestLoom, come negli altri test che seminano il workspace.
        // La demo non contiene Test di tipo RAS UnitLogger: se ne semina uno con un'esecuzione e due condizioni.
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var uidRas = state.Master.TestTemplate.Load().FirstOrDefault(t => t.Nome == TestTemplateDefaults.NomeRasUdt)?.Uid
            ?? throw new InvalidOperationException("template RAS UnitLogger assente nella demo");
        const string idRas = "RAS-UnitLogger-LAYOUT-01";
        var tc = new TestCase
        {
            Id = idRas,
            DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now, ModificatoDa = "UITests", CreatoDa = "UITests",
            Componente = "Componente-TEST-BCM", Veicolo = "", TestTemplateUid = uidRas,
            Descrizione = "Test RAS UnitLogger fittizio per il controllo di layout G2",
        };
        var esecuzione = new Esecuzione
        {
            Iterazione = 1, Data = DateTime.Now, Operatore = "UITests", Esito = Esito.Conforme,
            Note = "Esecuzione fittizia per il layout.", Matricola = "VF1RASLAYOUT1",
            Dettagli = new DettagliTestSnapshot(),
        };
        esecuzione.Condizioni.Add(new Condizione { Esito = Esito.Conforme, Componente = new EcuSelection(new List<string> { "Componente-TEST-BCM" }), Funzionalita = "Operazione RAS fittizia", Nota = "Condizione 1." });
        esecuzione.Condizioni.Add(new Condizione { Esito = Esito.Conforme, Componente = new EcuSelection(new List<string> { "Componente-TEST-BCM" }), Funzionalita = "Operazione RAS fittizia", Nota = "Condizione 2." });
        tc.Esecuzioni.Add(esecuzione);
        state.TestCases.Save(tc, isNew: true);
        TestRasPerWorkspace[ws.RootDir] = idRas;
        return ws;
    }

    // ------------------------------------------------------------------ pagine

    [Fact]
    public void Home_KpiGraficiComandiEAltriFiltriTempo_SchermoG2()
    {
        try
        {
            var f = Prepara();
            var shell = new MainShellPage(f, App);
            shell.NavigateTo("home");
            Controllo(f, "Home_Title_Text");
            Controllo(f, "Home_KpiChangeRequest_Button");

            var prefissi = f.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Select(b => SafeId(b))
                .Where(id => id.StartsWith("Home_Grafico_", StringComparison.Ordinal) && id.EndsWith("_Apri_Button", StringComparison.Ordinal))
                .Select(id => id[..^"Apri_Button".Length])
                .Distinct()
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            if (prefissi.Count == 0) Problema("Home: nessun grafico attivo con il comando Apri");
            foreach (var p in prefissi.Take(2))
            {
                var comandi = new[] { p + "Apri_Button", p + "EsportaPng_Button", p + "Copia_Button" };
                foreach (var id in comandi) Controllo(f, id, comandi.Where(c => c != id).ToArray());
            }

            var altriFiltri = ElementFinders.TryFindById(f, "Home_FiltriAvanzati_Apri_Button");
            if (altriFiltri == null) Problema("Home: pulsante 'Filtri avanzati' non trovato");
            else
            {
                Controllo(f, altriFiltri, "Home_KpiChangeRequest_Button");
                altriFiltri.AsButton().SafeInvoke();
                Wait.TryForCondition(() => ElementFinders.ExistsById(f, "Home_FiltroTempoOperazione_ComboBox"), TimeSpan.FromSeconds(5));
            }
            Controllo(f, "Home_FiltroTempoOperazione_ComboBox", "Home_FiltroTempoOperazione_TextBox");
            Controllo(f, "Home_FiltroTempoOperazione_TextBox", "Home_FiltroTempoOperazione_ComboBox");
            Controllo(f, "Home_FiltroTempoTotaleTest_ComboBox", "Home_FiltroTempoTotaleTest_TextBox");
            Controllo(f, "Home_FiltroTempoTotaleTest_TextBox", "Home_FiltroTempoTotaleTest_ComboBox");
            Controllo(f, "Home_FiltroGamma_Button");
            Fotografa(f, "01_home");
            Verifica(nameof(Home_KpiGraficiComandiEAltriFiltriTempo_SchermoG2));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Home_KpiGraficiComandiEAltriFiltriTempo_SchermoG2), ex); }
    }

    [Fact]
    public void Report_SelettoreModalitaFiltriEsportazioniEMatrice_SchermoG2()
    {
        try
        {
            var f = Prepara();
            new MainShellPage(f, App).NavigateTo("report");
            // Il Report carica i dati in background (Report_Caricamento_Indicatore): la matrice compare a caricamento
            // finito, quindi si attende prima di ispezionare il layout (un controllo immediato dava un falso positivo).
            Wait.ForCondition(() => ElementFinders.ExistsById(f, "Report_ModalitaMatrice_ComboBox"),
                "selettore modalita' della matrice nel Report", TimeSpan.FromSeconds(30));
            Wait.ForCondition(() => !ElementFinders.ExistsById(f, "Report_Caricamento_Indicatore"),
                "fine caricamento del Report", TimeSpan.FromSeconds(30));
            Controllo(f, "Report_ModalitaMatrice_ComboBox", "Report_FiltroTemplate_Button");
            Controllo(f, "Report_FiltroTemplate_Button");
            Controllo(f, "Report_FiltroTempoOperazione_ComboBox", "Report_FiltroTempoOperazione_TextBox");
            Controllo(f, "Report_FiltroTempoOperazione_TextBox", "Report_FiltroTempoOperazione_ComboBox");
            Controllo(f, "Report_FiltroTempoTotaleTest_ComboBox", "Report_FiltroTempoTotaleTest_TextBox");
            Controllo(f, "Report_FiltroTempoTotaleTest_TextBox", "Report_FiltroTempoTotaleTest_ComboBox");
            Controllo(f, "Report_MatriceAggiungiCampoX_Button", "Report_MatriceAggiungiCampoY_Button");
            Controllo(f, "Report_MatriceAggiungiCampoY_Button", "Report_MatriceAggiungiCampoX_Button");
            ControlloSeCompare(f, "Report_MatriceLegenda_Panel");
            Controllo(f, "Report_EsportaExcel_Button", "Report_EsportaPdf_Button");
            Controllo(f, "Report_EsportaPdf_Button", "Report_EsportaExcel_Button");
            Fotografa(f, "02_report_alto");

            // Modalita' "Tempo": il selettore della base dei tempi deve comparire e restare raggiungibile.
            var modalita = ElementFinders.WaitComboBoxById(f, "Report_ModalitaMatrice_ComboBox");
            modalita.Expand();
            var voceTempo = modalita.Items.FirstOrDefault(i => i.Text.Contains("Tempo", StringComparison.OrdinalIgnoreCase));
            if (voceTempo == null) Problema("Report: nessuna voce 'Tempo' nel selettore della modalita' della matrice");
            else
            {
                voceTempo.Select();
                modalita.Collapse();
                Controllo(f, "Report_BaseTempo_ComboBox", "Report_ModalitaMatrice_ComboBox");
            }
            Fotografa(f, "03_report_matrice_tempo");
            Verifica(nameof(Report_SelettoreModalitaFiltriEsportazioniEMatrice_SchermoG2));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Report_SelettoreModalitaFiltriEsportazioniEMatrice_SchermoG2), ex); }
    }

    [Fact]
    public void EditorTestRasUdt_RigaOperazioneConCampoDurata_SchermoG2()
    {
        try
        {
            var f = Prepara();
            var id = TestRasPerWorkspace.GetValueOrDefault(Workspace.RootDir);
            if (id == null) throw new InvalidOperationException("la demo completa non contiene un Test di tipo RAS UnitLogger con operazioni");
            var lista = new MainShellPage(f, App).GoToTestList();
            lista.Search(id);
            var editor = lista.OpenRowById(id);
            editor.EnterEditMode();
            Wait.ForCondition(() => editor.CanSave, "editor del Test RAS UnitLogger in modifica");

            Controllo(f, "Editor_Save_Button", "Editor_Cancel_Button");
            Controllo(f, "Editor_Cancel_Button", "Editor_Save_Button");

            var durata = Wait.ForElement(() => f.FindAllDescendants()
                    .FirstOrDefault(e => SafeId(e).StartsWith("Editor_Condizione_", StringComparison.Ordinal) && SafeId(e).EndsWith("_Durata_TextBox", StringComparison.Ordinal)),
                "campo Durata nella riga operazione del Test RAS UnitLogger");
            var prefisso = SafeId(durata)[..^"Durata_TextBox".Length];
            CheckElemento(f, durata, "Durata operazione (RAS UnitLogger)", new[] { prefisso + "DurataMeno5_Button" });
            Controllo(f, prefisso + "DurataMeno5_Button", prefisso + "DurataPiu5_Button");
            Controllo(f, prefisso + "DurataPiu5_Button", prefisso + "DurataMeno5_Button");
            Fotografa(f, "04_editor_test_ras_udt_operazioni");
            Verifica(nameof(EditorTestRasUdt_RigaOperazioneConCampoDurata_SchermoG2));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(EditorTestRasUdt_RigaOperazioneConCampoDurata_SchermoG2), ex); }
    }

    [Fact]
    public void Impostazioni_TutteLeSchede_SchermoG2()
    {
        try
        {
            var f = Prepara();
            var schede = new[] { "generale", "operatori", "strumenti", "tipiditest", "componente", "requisiti", "veicoli", "funzionalita", "archivio" };
            var impostazioni = new MainShellPage(f, App).GoToImpostazioni();
            foreach (var scheda in schede)
            {
                var sezioneFallita = $"scheda {scheda}";
                try
                {
                    impostazioni.VaiA(scheda);
                    Controllo(f, $"Impostazioni_Sub_{scheda}");
                    foreach (var salva in PulsantiConNome(f, "Salva")) CheckElemento(f, salva, $"{sezioneFallita}: Salva", Array.Empty<string>());
                    if (scheda == "generale")
                    {
                        Controllo(f, "Impostazioni_Dati_VerificaLocale_Button");
                        Controllo(f, "Impostazioni_DurataObbligatoria_CheckBox");
                        Controllo(f, "Impostazioni_SalvaPoliticaDurata_Button");
                    }
                    if (scheda == "funzionalita") Controllo(f, "Funz_Modifica_Button");
                    if (scheda == "veicoli") Controllo(f, "Veic_Modifica_Button");
                    Fotografa(f, $"05_impostazioni_{scheda}");
                }
                catch (Exception ex) { Problema($"{sezioneFallita}: {ex.Message}"); }
            }
            Verifica(nameof(Impostazioni_TutteLeSchede_SchermoG2));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Impostazioni_TutteLeSchede_SchermoG2), ex); }
    }

    /// <summary>"Componente e funzionalita'" del veicolo (6.7.2: finestra 900x660 non ridimensionabile, aperta da Veicoli).
    /// Su G2 la finestra deve stare nella finestra principale, i pulsanti devono essere visibili, la casella di una
    /// componente si attiva in modifica e Annulla con conferma riporta alla sola lettura senza salvare nulla.</summary>
    [Fact]
    public void Veicoli_FinestraAssociazioniEcu_SchermoG2()
    {
        try
        {
            var f = Prepara();
            new MainShellPage(f, App).GoToImpostazioni().VaiAVeicoli();
            // Selezione per clic sul testo della prima riga: le righe DataGrid non espongono SelectionItem.
            var griglia = ElementFinders.WaitById(f, "GridPrincipale");
            Wait.ForElement(() => griglia.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
                    .FirstOrDefault()?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text)),
                "prima riga della griglia Veicoli").Click();
            Wait.ForCondition(() => ElementFinders.WaitButtonById(f, "Veic_Associazioni_Button").IsEnabled,
                "pulsante componente e funzionalita' abilitato dopo la selezione");
            Controllo(f, "Veic_Associazioni_Button");
            ElementFinders.WaitButtonById(f, "Veic_Associazioni_Button").SafeInvoke();

            var finestra = App.WaitForWindowByContent("Assoc_ElencoEcu_Lista", TimeSpan.FromSeconds(20));
            // Misura nativa (rettangolo esterno) della finestra principale e di quella componente e funzionalita'.
            GetWindowRect(Hwnd(f), out var rettPrincipale);
            GetWindowRect(Hwnd(finestra), out var rettDialogo);
            _out.WriteLine($"Finestra principale nativa ({rettPrincipale.Left},{rettPrincipale.Top})-({rettPrincipale.Right},{rettPrincipale.Bottom}), " +
                $"Componente e funzionalita' ({rettDialogo.Left},{rettDialogo.Top})-({rettDialogo.Right},{rettDialogo.Bottom})");
            if (rettDialogo.Left < rettPrincipale.Left - TolleranzaPixel || rettDialogo.Top < rettPrincipale.Top - TolleranzaPixel ||
                rettDialogo.Right > rettPrincipale.Right + TolleranzaPixel || rettDialogo.Bottom > rettPrincipale.Bottom + TolleranzaPixel)
                Problema("finestra componente e funzionalita' fuori dalla finestra principale su G2");
            Controllo(finestra, "Assoc_Titolo_Text");
            Controllo(finestra, "Assoc_ElencoEcu_Lista");
            Controllo(finestra, "Assoc_Modifica_Button");
            Controllo(finestra, "Assoc_Chiudi_Button");
            var elenco = ElementFinders.WaitById(finestra, "Assoc_ElencoEcu_Lista");
            if (elenco.BoundingRectangle.Height < AltezzaMinimaGrigliaLogica * _scala)
                Problema($"elenco componente troppo basso ({elenco.BoundingRectangle.Height} px) nella finestra componente e funzionalita' su G2");

            // Modifica, alterna la prima componente, Annulla con conferma: nessuna modifica deve restare.
            ElementFinders.WaitButtonById(finestra, "Assoc_Modifica_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(finestra, "Assoc_Annulla_Button"),
                "modifica attiva nella finestra componente e funzionalita'", TimeSpan.FromSeconds(20));
            AutomationElement Casella() => Wait.ForElement(
                () => ElementFinders.WaitById(finestra, "Assoc_ElencoEcu_Lista")
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox)).FirstOrDefault(),
                "casella della prima componente nell'elenco");
            var nomeEcu = Casella().Name;
            var statoIniziale = Casella().AsCheckBox().IsChecked;
            Wait.ForCondition(() => Casella().IsEnabled, $"casella di '{nomeEcu}' abilitata in modifica");
            Casella().AsCheckBox().Toggle();
            Wait.ForCondition(() => Casella().AsCheckBox().IsChecked != statoIniziale, $"casella di '{nomeEcu}' alternata");
            ElementFinders.WaitButtonById(finestra, "Assoc_Annulla_Button").SafeInvoke();
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button", TimeSpan.FromSeconds(10))).ConfirmYes();
            Wait.ForCondition(() => ElementFinders.ExistsById(finestra, "Assoc_Modifica_Button"),
                "ritorno alla sola lettura dopo Annulla", TimeSpan.FromSeconds(20));
            Wait.ForCondition(() => Casella().AsCheckBox().IsChecked == statoIniziale,
                $"stato di '{nomeEcu}' ripristinato dopo Annulla", TimeSpan.FromSeconds(15));

            ElementFinders.WaitButtonById(finestra, "Assoc_Chiudi_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Assoc_ElencoEcu_Lista"), "finestra componente e funzionalita' chiusa", TimeSpan.FromSeconds(15));
            Verifica(nameof(Veicoli_FinestraAssociazioniEcu_SchermoG2));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Veicoli_FinestraAssociazioniEcu_SchermoG2), ex); }
    }

    [Fact]
    public void ListaTest_FiltriAvanzatiEAzioni_SchermoG2()
    {
        try
        {
            var f = Prepara();
            var lista = new MainShellPage(f, App).GoToTestList();
            Controllo(f, "TestList_New_Button", "TestList_Search_TextBox");
            Controllo(f, "TestList_Search_TextBox", "TestList_New_Button");
            lista.ApriFiltriAvanzati();
            Controllo(f, "TestList_FiltroFunzionalita_Button");
            Controllo(f, "TestList_FiltroVeicolo_Button");
            Controllo(f, "TestList_FiltroComponente_Button");
            Controllo(f, "TestList_FiltroTipoTest_Button");
            Controllo(f, "TestList_FiltroImportatoUdt_CheckBox");
            Fotografa(f, "06_lista_test_filtri_avanzati");
            Verifica(nameof(ListaTest_FiltriAvanzatiEAzioni_SchermoG2));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ListaTest_FiltriAvanzatiEAzioni_SchermoG2), ex); }
    }

    [Fact]
    public void Anomalie_FiltriAzioniEGriglia_SchermoG2()
    {
        try
        {
            var f = Prepara();
            var anomalie = new MainShellPage(f, App).GoToAnomalie();
            anomalie.EspandiFiltri();
            ChiudiPopup(f);
            Controllo(f, "Anomalie_NuovaChangeRequest_Button", "Anomalie_NuovaSegnalazioneRete_Button");
            Controllo(f, "Anomalie_NuovaSegnalazioneRete_Button", "Anomalie_NuovaChangeRequest_Button");
            Controllo(f, "Anomalie_FiltroFunzionalita_Button");
            Controllo(f, "Anomalie_FiltroVeicolo_Button");
            Controllo(f, "Anomalie_FiltroComponente_Button");
            CheckGriglia(f, "Anomalie_Righe_DataGrid");
            Fotografa(f, "07_anomalie");
            Verifica(nameof(Anomalie_FiltriAzioniEGriglia_SchermoG2));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Anomalie_FiltriAzioniEGriglia_SchermoG2), ex); }
    }

    // ------------------------------------------------------------------ finestra G2

    /// <summary>Attende la shell, ripristina la finestra e la porta a 1280x776 logici (pixel fisici = logici * DPI/96).</summary>
    private Window Prepara()
    {
        var livelloDpi = RendiProcessoDpiAware();
        Assert.True(livelloDpi is 1 or 2, $"processo di test non DPI-aware (livello {livelloDpi}): misure UIA non affidabili");
        _watchdog ??= new Timer(_ =>
        {
            _out.WriteLine($"WATCHDOG: budget di {BudgetTest.TotalSeconds:0}s superato, TestLoom terminato");
            Task.Run(() => { try { App.Dispose(); } catch { } });
        }, null, BudgetTest, Timeout.InfiniteTimeSpan);
        var f = App.WaitForMainWindow(TimeSpan.FromSeconds(30));
        Wait.ForCondition(() => MainShellPage.IsShown(f), "shell principale visibile", TimeSpan.FromSeconds(30));
        RidimensionaG2(f);
        ChiudiPopup(f);
        _scala = GetDpiForWindow(Hwnd(f)) / 96.0;
        _out.WriteLine($"Finestra G2: {LarghezzaG2}x{AltezzaG2} logici, scala {_scala:0.00}, finestra UIA {Fmt(f.BoundingRectangle)}");
        return f;
    }

    private static void RidimensionaG2(Window f)
    {
        var hwnd = Hwnd(f);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("handle nativo della finestra principale non disponibile");
        ShowWindow(hwnd, SwRestore);
        Wait.ForCondition(() => !IsZoomed(hwnd), "la finestra esce dallo stato massimizzato", TimeSpan.FromSeconds(10));
        var scala = GetDpiForWindow(hwnd) / 96.0;
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, (int)Math.Round(LarghezzaG2 * scala), (int)Math.Round(AltezzaG2 * scala), SwpNoZOrder | SwpNoActivate);
        Wait.ForCondition(() =>
        {
            GetWindowRect(hwnd, out var r);
            return Math.Abs((r.Right - r.Left) - LarghezzaG2 * scala) < 3 * scala &&
                   Math.Abs((r.Bottom - r.Top) - AltezzaG2 * scala) < 3 * scala;
        }, "la finestra raggiunge 1280x776 logici", TimeSpan.FromSeconds(15));
    }

    // ------------------------------------------------------------------ controlli

    private void Controllo(Window f, string automationId, params string[] vicini)
    {
        var el = ElementFinders.TryFindById(f, automationId);
        if (el == null) { Problema($"{automationId}: controllo non trovato nella pagina"); return; }
        CheckElemento(f, el, automationId, vicini);
    }

    /// <summary>Controllo per elementi condizionali (es. legenda della matrice): se assenti nello
    /// stato attuale della pagina non e' un problema di layout; se presenti vengono verificati come gli altri.</summary>
    private void ControlloSeCompare(Window f, string automationId)
    {
        var el = ElementFinders.TryFindById(f, automationId);
        if (el == null) { _out.WriteLine($"[non presente nello stato attuale, non verificato] {automationId}"); return; }
        CheckElemento(f, el, automationId, Array.Empty<string>());
    }

    private void Controllo(Window f, AutomationElement el, params string[] vicini) =>
        CheckElemento(f, el, el.Name ?? SafeId(el), vicini);

    private void CheckElemento(Window f, AutomationElement el, string etichetta, IEnumerable<string> vicini)
    {
        try { PortaInVista(f, el); }
        catch (Exception ex) { Problema($"{etichetta}: {ex.Message}"); }

        var r = el.BoundingRectangle;
        var w = f.BoundingRectangle;
        if (r.Width <= 0 || r.Height <= 0) { Problema($"{etichetta}: dimensione nulla {Fmt(r)}"); return; }
        if (!Dentro(w, r)) Problema($"{etichetta} {Fmt(r)} esce dalla finestra {Fmt(w)}");
        else if (el.Properties.IsOffscreen.ValueOrDefault) Problema($"{etichetta} {Fmt(r)} non visibile (offscreen) anche dopo lo scorrimento");

        foreach (var id in vicini.Where(v => !string.IsNullOrEmpty(v)))
        {
            var v = ElementFinders.TryFindById(f, id);
            if (v == null || v.Properties.IsOffscreen.ValueOrDefault) continue;
            var vr = v.BoundingRectangle;
            if (vr.Width <= 0 || vr.Height <= 0 || !Dentro(w, vr)) continue;
            if (Interseca(r, vr)) Problema($"{etichetta} {Fmt(r)} si sovrappone a {id} {Fmt(vr)}");
        }
    }

    private void CheckGriglia(Window f, string automationId)
    {
        var g = ElementFinders.TryFindById(f, automationId);
        if (g == null) { Problema($"{automationId}: griglia non trovata"); return; }
        CheckElemento(f, g, automationId, Array.Empty<string>());
        var altezza = g.BoundingRectangle.Height;
        if (altezza < AltezzaMinimaGrigliaLogica * _scala)
            Problema($"{automationId}: griglia troppo bassa {altezza:0} px (minimo {AltezzaMinimaGrigliaLogica * _scala:0} px)");
    }

    private static IEnumerable<AutomationElement> PulsantiConNome(Window f, string nome) =>
        f.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).Where(b => b.Name == nome);

    private static AutomationElement? TrovaPulsantePerNomeParziale(Window f, string frammento) =>
        f.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b => (b.Name ?? "").Contains(frammento, StringComparison.OrdinalIgnoreCase));

    /// <summary>Porta l'elemento nella finestra: scroll item se disponibile, altrimenti scorrimento della scroll viewer
    /// piu' esterna che lo contiene. Nessuno sleep fisso: ogni passo attende la condizione con timeout breve.</summary>
    private static void PortaInVista(Window f, AutomationElement el)
    {
        if (Visibile(f, el)) return;
        if (el.Patterns.ScrollItem.PatternOrDefault is { } voce)
        {
            voce.ScrollIntoView();
            if (Wait.TryForCondition(() => Visibile(f, el), TimeSpan.FromSeconds(2))) return;
        }
        AutomationElement? scorrevole = null;
        for (var p = el.Parent; p != null; p = p.Parent)
            if (p.Patterns.Scroll.PatternOrDefault != null) scorrevole = p;
        if (scorrevole == null) throw new InvalidOperationException("nessuna scroll viewer contiene l'elemento");
        for (var percento = 0.0; percento <= 100; percento += 10)
        {
            scorrevole.Patterns.Scroll.PatternOrDefault!.SetScrollPercent(ScrollPatternConstants.NoScroll, percento);
            if (Wait.TryForCondition(() => Visibile(f, el), TimeSpan.FromMilliseconds(300))) return;
        }
        throw new InvalidOperationException("non portabile nella finestra scorrendo la pagina");
    }

    private static bool Visibile(Window f, AutomationElement el)
    {
        if (el.Properties.IsOffscreen.ValueOrDefault) return false;
        var r = el.BoundingRectangle;
        return r.Width > 0 && r.Height > 0 && Dentro(f.BoundingRectangle, r);
    }

    private static bool Dentro(Rectangle w, Rectangle r) =>
        r.Left >= w.Left - 1 && r.Top >= w.Top - 1 && r.Right <= w.Right + 1 && r.Bottom <= w.Bottom + 1;

    private static bool Interseca(Rectangle a, Rectangle b) =>
        Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > TolleranzaPixel &&
        Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) > TolleranzaPixel;

    // ------------------------------------------------------------------ esito

    private void Problema(string descrizione)
    {
        _problemi.Add(descrizione);
        _out.WriteLine("[PROBLEMA] " + descrizione);
    }

    private void Verifica(string nomeTest)
    {
        if (_problemi.Count == 0) return;
        Assert.Fail($"{nomeTest}: {_problemi.Count} problema/i di layout su G2 (1280x776 logici):\n- " + string.Join("\n- ", _problemi));
    }

    /// <summary>Chiude eventuali popup/menu aperti (es. selettori multi-scelta) con Esc, prima di ispezionare la pagina.</summary>
    private static void ChiudiPopup(Window f)
    {
        try { f.Focus(); } catch { }
        for (var i = 0; i < 2; i++) FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
    }

    public override void Dispose()
    {
        _watchdog?.Dispose();
        base.Dispose();
    }

    private void Fotografa(Window f, string nome)
    {
        ChiudiPopup(f);
        var cartella = Path.Combine(RadiceRepo(), ".ai", "artifacts", "schermo-piccolo-670");
        Directory.CreateDirectory(cartella);
        var percorso = Path.Combine(cartella, nome + ".png");
        using var immagine = f.Capture();
        immagine.Save(percorso, ImageFormat.Png);
        _out.WriteLine($"Schermata: {percorso}");
    }

    private static string RadiceRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TestLoom.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("radice del repository non trovata da " + AppContext.BaseDirectory);
    }

    private static string Fmt(Rectangle r) => $"({r.Left},{r.Top} {r.Width}x{r.Height})";

    private static string SafeId(AutomationElement e)
    {
        try { return e.AutomationId ?? ""; }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ Win32 (processo DPI-aware)

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessDpiAwarenessContext(IntPtr valore);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr valore);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int comando);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr dopo, int x, int y, int cx, int cy, uint flag);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out RettangoNativo rettangolo);

    [StructLayout(LayoutKind.Sequential)]
    private struct RettangoNativo { public int Left; public int Top; public int Right; public int Bottom; }

    private const int SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);

    /// <summary>Rende il processo di test DPI-aware (per-monitor v2, fallback system) prima di ogni misura UIA.</summary>
    private static int RendiProcessoDpiAware()
    {
        if (GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) == 0 && !SetProcessDpiAwarenessContext(DpiAwarenessPerMonitorV2))
            SetProcessDPIAware();
        return GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
    }

    private static IntPtr Hwnd(Window w)
    {
        try { return w.FrameworkAutomationElement.NativeWindowHandle; }
        catch { return IntPtr.Zero; }
    }
}
