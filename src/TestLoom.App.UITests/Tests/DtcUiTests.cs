using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>6.5.0 punto 7: test Banco basati su requisito/componente/guasto. Copre (1) impostazione di template "Funzionalita
/// non obbligatoria" persistita, (2) pannello guasto del requisito Extra (lista virtualizzata) in Impostazioni,
/// (3) pagina Avanzamento guasto: totale N/M, righe per guasto, stati e filtro. I dati sono seminati via AppState
/// (stessa tecnica degli altri test UI), mai pilotando l'app per crearli.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
public sealed class DtcUiTests : UiTestBase
{
    private const string Req = "REQ-Extra-1";
    private const string Componente = TestWorkspace.DefaultEcuName;

    public DtcUiTests() : base(CreaWorkspace()) { }

    internal static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(DtcUiTests));
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);

        var ecuList = state.Master.Componente.Load();
        ecuList.First(e => e.Nome == Componente).VeicoliAssociati = new List<string> { TestWorkspace.DefaultVehicleName };
        state.Master.Componente.Save(ecuList);

        var req = new Requisito
        {
            Id = Req, Testo = "Requisito Extra con guasto", ApplicabileBanco = true, ModificatoDa = "UITests",
            Guasto = new List<DtcRequisito>
            {
                new() { Codice = "P0001", Componente = Componente }, new() { Codice = "P0002", Componente = Componente }, new() { Codice = "P0003", Componente = Componente },
            }
        };
        RequisitoApplicabilita.ImpostaTipo(req, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(req, new[] { TestWorkspace.DefaultVehicleName });
        state.Master.Requisiti.Save(new List<Requisito> { req });

        var tc = new TestCase { Id = "TC-guasto-1", Componente = Componente, RequisitoId = { Req }, Veicolo = TestWorkspace.DefaultVehicleName };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = DateTime.Now.AddHours(-2), Esito = Esito.Conforme, Matricola = "V-guasto",
            Condizioni =
            {
                // P0001: validazione + devalidazione conformi -> Completo
                new Condizione { Guasto = "P0001", DtcRequisitoId = Req, DtcPasso = PassoDtc.Validazione, Esito = Esito.Conforme, Componente = Componente },
                new Condizione { Guasto = "P0001", DtcRequisitoId = Req, DtcPasso = PassoDtc.Devalidazione, Esito = Esito.Conforme, Componente = Componente },
                // P0002: solo validazione conforme -> Validato
                new Condizione { Guasto = "P0002", DtcRequisitoId = Req, DtcPasso = PassoDtc.Validazione, Esito = Esito.Conforme, Componente = Componente },
                // P0003: validazione non conforme -> Non conforme
                new Condizione { Guasto = "P0003", DtcRequisitoId = Req, DtcPasso = PassoDtc.Validazione, Esito = Esito.NonConforme, Componente = Componente, DescrizioneProblema = "x" },
            },
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = Req, Valore = PunteggioConformita.Conforme } },
        });
        state.TestCases.Save(tc, true);
        return ws;
    }

    private static (MasterDataEditorPage Editor, AutomationElement Window) ApriRequisitoD4d(TestLoomApplication app)
    {
        var window = app.WaitForMainWindow();
        var imp = new MainShellPage(window).GoToImpostazioni();
        imp.VaiA("requisitid4d");
        var editor = new MasterDataEditorPage(window, "Requis");
        Wait.ForCondition(() => TrovaTesto(editor.Grid, "Requisito Extra con guasto") != null, "requisito Extra in griglia");
        TrovaTesto(editor.Grid, "Requisito Extra con guasto")!.Click();
        ElementFinders.WaitById(window, "Requis_Guasto_Expander").Patterns.ExpandCollapse.PatternOrDefault?.Expand();
        return (editor, window);
    }

    internal static void ApriDettaglioDtcInAudit(TestLoomApplication app, out AutomationElement window)
    {
        var w = app.WaitForMainWindow();
        var audit = new MainShellPage(w, app).GoToAudit();
        audit.SelezionaVeicolo(TestWorkspace.DefaultVehicleName);
        Wait.ForCondition(() => ElementFinders.ExistsById(w, $"Audit_Extra_Req_{Req}"), "requisito Extra con guasto nella sezione Extra (calcolo in background)");
        ElementFinders.WaitButtonById(w, $"Audit_Extra_Req_{Req}").SafeInvoke();
        ElementFinders.WaitById(w, "Audit_Guasto_Lista");
        window = w;
    }

    [Fact]
    public void Impostazioni_PannelloDtcDelRequisitoD4d_SoloGestione_SenzaStatoNeAvanzamento()
    {
        try
        {
            var (editor, window) = ApriRequisitoD4d(App);
            ElementFinders.WaitById(window, "Requis_Guasto_Voce_P0001");
            ElementFinders.WaitById(window, "Requis_Guasto_Voce_P0003");
            // Gestione dati, non stato: nessun riepilogo/stato/filtri di avanzamento in Impostazioni.
            Assert.False(ElementFinders.ExistsById(window, "Requis_Guasto_Riepilogo_Text"));
            Assert.False(ElementFinders.ExistsById(window, "Requis_Guasto_SoloNonCompleti_Check"));
            Assert.DoesNotContain("Completo", TuttoIlTesto(ElementFinders.WaitById(window, "Requis_Guasto_Voce_P0001")));
            Assert.False(ElementFinders.ExistsById(window, "Requis_Guasto_Aggiungi_Button")); // sola lettura
            editor.IniziaModifica();
            ElementFinders.WaitById(window, "Requis_Guasto_Aggiungi_Button");
            ElementFinders.WaitById(window, "Requis_Guasto_Importa_Button");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Impostazioni_PannelloDtcDelRequisitoD4d_SoloGestione_SenzaStatoNeAvanzamento), ex); }
    }

    [Fact]
    public void AuditD4d_DrillDownDtc_Stato_Riepilogo_PassoMancante_UltimoTest_EFiltri()
    {
        try
        {
            ApriDettaglioDtcInAudit(App, out var window);
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Audit_Guasto_Riepilogo_Text").Name == "1/3 guasto completi", "riepilogo 1/3 guasto completi");
            var filtro = ElementFinders.WaitTextBoxById(window, "Audit_Guasto_Filtro_TextBox");
            filtro.SetText("P0001");
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Audit_Guasto_Voce_P0001") && TuttoIlTesto(ElementFinders.WaitById(window, "Audit_Guasto_Voce_P0001")).Contains("Completo"), "P0001 Completo");
            filtro.SetText("P0002");
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Audit_Guasto_Voce_P0002"), "riga P0002");
            var p2 = TuttoIlTesto(ElementFinders.WaitById(window, "Audit_Guasto_Voce_P0002"));
            Assert.Contains("Validato", p2);
            Assert.Contains("manca: devalidazione", p2);
            Assert.Contains("TC-guasto-1", p2); // ultimo test
            filtro.SetText("P0003");
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Audit_Guasto_Voce_P0003"), "riga P0003");
            Assert.Contains("Non conforme", TuttoIlTesto(ElementFinders.WaitById(window, "Audit_Guasto_Voce_P0003")));
            filtro.SetText("");

            // Altri filtri: collassati all'apertura con riepilogo compatto.
            var altri = ElementFinders.WaitById(window, "Audit_Guasto_AltriFiltri_Expander");
            Assert.Equal(ExpandCollapseState.Collapsed, altri.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value);
            Assert.Contains("0 attivi", TuttoIlTesto(altri));

            filtro.SetText("P0002");
            Wait.ForCondition(() => !ElementFinders.ExistsById(window, "Audit_Guasto_Voce_P0001") && ElementFinders.ExistsById(window, "Audit_Guasto_Voce_P0002"), "filtro testo applicato");
            ElementFinders.WaitButtonById(window, "Audit_Guasto_Azzera_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Audit_Guasto_Voce_P0001"), "Azzera ripristina la lista");

            ElementFinders.WaitById(window, "Audit_Guasto_SoloNonCompleti_Check").AsCheckBox().Toggle();
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Audit_Guasto_Titolo_Text").Name.Contains("(2/3)"), "solo non completi nasconde il completo (2/3)");
            Assert.False(ElementFinders.ExistsById(window, "Audit_Guasto_Voce_P0001"));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(AuditD4d_DrillDownDtc_Stato_Riepilogo_PassoMancante_UltimoTest_EFiltri), ex); }
    }

    internal static string TuttoIlTesto(AutomationElement el) =>
        string.Join(" | ", el.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name)) + " | " + el.Name;

    internal static AutomationElement? TrovaTesto(AutomationElement scope, string testo) =>
        scope.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).FirstOrDefault(t => t.Name == testo);
}
