using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>6.5.0 punto 7 (seconda parte): schede Requisiti / Requisiti Extra in Impostazioni, sezioni Standard/Extra
/// separate + filtro Tipo nell'Audit, selettore guasto dell'operazione nell'editor Test (template "Funzionalita non
/// obbligatoria") e riga selezionata dell'editor Requisiti dopo "Modifica". Seed via AppState.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
public sealed class DtcSchedeAuditEditorUiTests : UiTestBase
{
    private const string ReqStd = "REQ-STD-1";
    private const string ReqD4d = "REQ-Extra-9";
    private static readonly Guid CondUid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string Componente = TestWorkspace.DefaultEcuName;
    private const string Veh = TestWorkspace.DefaultVehicleName;

    public DtcSchedeAuditEditorUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(DtcSchedeAuditEditorUiTests));
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);

        var componente = state.Master.Componente.Load();
        componente.First(e => e.Nome == Componente).VeicoliAssociati = new List<string> { Veh };
        state.Master.Componente.Save(componente);

        var tpl = state.Master.TestTemplate.Load().First(t => t.Nome == TestWorkspace.DefaultTemplateName);

        var std = new Requisito { Id = ReqStd, Testo = "Requisito standard", ApplicabileBanco = true, ModificatoDa = "UITests" };
        var extra = new Requisito
        {
            Id = ReqD4d, Testo = "Requisito Extra", ApplicabileBanco = true, ModificatoDa = "UITests",
            Guasto = new List<DtcRequisito> { new() { Codice = "P0100", Componente = Componente }, new() { Codice = "P0200", Componente = Componente } }
        };
        RequisitoApplicabilita.ImpostaTipo(extra, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(extra, new[] { Veh });
        state.Master.Requisiti.Save(new List<Requisito> { std, extra });

        // Test con una operazione ancora da eseguire (per il selettore guasto) e un test con esiti sui due requisiti.
        var tc = new TestCase
        {
            Id = "TC-guasto-EDIT", DescrizioneBreve = "Test guasto", Componente = Componente, Veicolo = Veh, Veicoli = { Veh },
            RequisitoId = { ReqD4d }, TestTemplateUid = tpl.Uid, ModificatoDa = "UITests"
        };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = DateTime.Now, Esito = Esito.DaEseguire, Matricola = "V-guasto",
            Condizioni = { new Condizione { Uid = CondUid, Esito = Esito.DaEseguire, Componente = new EcuSelection(new[] { Componente }) } }
        });
        state.TestCases.Save(tc, true);

        // Test con lo strumento guasto Injection e nessuna operazione: per il blocco "Aggiungi guasto" (6.5.1 fase 4).
        var strumenti = state.Master.Strumenti.Load();
        if (!strumenti.Any(x => x.Nome == "Guasto Injection")) { strumenti.Add(new StrumentoDiagnostico { Nome = "Guasto Injection", Tipo = TipoStrumento.Comportamento, ModificatoDa = "UITests" }); state.Master.Strumenti.Save(strumenti); }
        var tcInj = new TestCase
        {
            Id = "TC-guasto-INJ", DescrizioneBreve = "Test guasto Injection", Componente = Componente, Veicolo = Veh, Veicoli = { Veh },
            RequisitoId = { ReqD4d }, StrumentiUsati = { "Guasto Injection" }, TestTemplateUid = tpl.Uid, ModificatoDa = "UITests"
        };
        tcInj.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.DaEseguire, Matricola = "V-INJ" });
        state.TestCases.Save(tcInj, true);

        var tcAudit = new TestCase { Id = "TC-AUDIT-MIX", DescrizioneBreve = "Audit", Componente = Componente, Veicolo = Veh, Veicoli = { Veh }, RequisitoId = { ReqStd, ReqD4d } };
        tcAudit.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V",
            PunteggiRequisiti =
            {
                new PunteggioRequisito { RequisitoId = ReqStd, Valore = PunteggioConformita.Conforme },
                new PunteggioRequisito { RequisitoId = ReqD4d, Valore = PunteggioConformita.Conforme },
            }
        });
        state.TestCases.Save(tcAudit, true);
        return ws;
    }

    private static AutomationElement? TrovaTesto(AutomationElement scope, string testo) =>
        scope.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).FirstOrDefault(t => t.Name == testo);

    [Fact]
    public void Impostazioni_DueSchedeSeparate_StandardENonD4d_EViceversa()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var imp = new MainShellPage(window).GoToImpostazioni();
            Assert.True(imp.HaSottoSezione("requisiti"));
            Assert.True(imp.HaSottoSezione("requisitid4d"));

            imp.VaiA("requisiti");
            var std = new MasterDataEditorPage(window, "Requis");
            Wait.ForCondition(() => TrovaTesto(std.Grid, ReqStd) != null, "requisito standard nella scheda Requisiti");
            Assert.Null(TrovaTesto(std.Grid, ReqD4d));

            imp.VaiA("requisitid4d");
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Requis_Titolo_Text").Name == "Requisiti Extra", "titolo scheda Extra");
            var extra = new MasterDataEditorPage(window, "Requis");
            Wait.ForCondition(() => TrovaTesto(extra.Grid, ReqD4d) != null, "requisito Extra nella scheda Extra");
            Assert.Null(TrovaTesto(extra.Grid, ReqStd));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Impostazioni_DueSchedeSeparate_StandardENonD4d_EViceversa), ex); }
    }

    [Fact]
    public void SchedaD4d_ModificaMantieneLaRigaSelezionata_EMostraPannelloDtcModificabile_NuovoCreaD4d()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var imp = new MainShellPage(window).GoToImpostazioni();
            imp.VaiA("requisitid4d");
            var editor = new MasterDataEditorPage(window, "Requis");
            Wait.ForCondition(() => TrovaTesto(editor.Grid, "Requisito Extra") != null, "riga Extra");
            TrovaTesto(editor.Grid, "Requisito Extra")!.Click();
            ElementFinders.WaitById(window, "Requis_Guasto_Expander").Patterns.ExpandCollapse.PatternOrDefault?.Expand();
            ElementFinders.WaitById(window, "Requis_Guasto_Voce_P0100");
            Assert.False(ElementFinders.ExistsById(window, "Requis_Guasto_Aggiungi_Button"));
            ElementFinders.WaitById(window, "Requis_Tipo_Label");   // 6.5.1: tipo fisso, etichetta di sola lettura (nessun pulsante di cambio tipo)

            editor.IniziaModifica();
            // Dopo "Modifica" la riga selezionata mantiene l'istanza: il pannello guasto resta visibile con le sue voci
            // e diventa modificabile (aggiunta e import disponibili).
            ElementFinders.WaitById(window, "Requis_Guasto_Aggiungi_Button");
            ElementFinders.WaitById(window, "Requis_Guasto_Importa_Button");
            ElementFinders.WaitById(window, "Requis_Guasto_Voce_P0200");
            ElementFinders.WaitById(window, "Requis_ImportaDtcMassivo_Button");

            // 6.5.1: albero Veicolo -> componente -> guasto; componente e Veicolo si scelgono dai menu a tendina (non si digitano).
            ElementFinders.WaitById(window, "Requis_Guasto_Albero");
            ElementFinders.WaitById(window, "Requis_Guasto_Veicolo_senza");                      // gruppo delle voci 6.5.0 senza veicolo
            ElementFinders.WaitById(window, "Requis_Guasto_NodoVeicolo_Button");
            ElementFinders.WaitById(window, "Requis_Guasto_NodoEcu_Button");
            ElementFinders.WaitComboBoxById(window, "Requis_Guasto_Componente_Combo").SelectByText(Componente);
            ElementFinders.WaitTextBoxById(window, "Requis_Guasto_Codice_TextBox").SetText("P0300");
            ElementFinders.WaitButtonById(window, "Requis_Guasto_Aggiungi_Button").SafeInvoke();
            ElementFinders.WaitById(window, "Requis_Guasto_Voce_P0300");

            // Stessa componente, ma sotto il veicolo dichiarato dal requisito: voce e AutomationId distinti (sv_ecu_codice).
            var id = (string s) => s.Replace(' ', '_');
            ElementFinders.WaitComboBoxById(window, "Requis_Guasto_Veicolo_Combo").SelectByText(Veh);
            ElementFinders.WaitTextBoxById(window, "Requis_Guasto_Codice_TextBox").SetText("P0300");
            ElementFinders.WaitButtonById(window, "Requis_Guasto_Aggiungi_Button").SafeInvoke();
            // La lista e virtualizzata (altezza fissa): si chiude il gruppo "(senza veicolo)" per portare in vista il nuovo gruppo.
            ElementFinders.WaitButtonById(window, "Requis_Guasto_Veicolo_senza_Toggle").SafeInvoke();
            ElementFinders.WaitById(window, $"Requis_Guasto_Veicolo_{id(Veh)}");
            ElementFinders.WaitById(window, $"Requis_Guasto_Componente_{id(Veh)}_{id(Componente)}");
            ElementFinders.WaitById(window, $"Requis_Guasto_Voce_{id(Veh)}_{id(Componente)}_P0300");
            ElementFinders.WaitById(window, $"Requis_Guasto_Rimuovi_{id(Veh)}_{id(Componente)}_P0300");
            editor.Salva();
            Wait.ForCondition(() => !editor.InModifica, "salvataggio completato");

            using var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
            var salvato = state.Master.Requisiti.Load().Single(r => r.Id == ReqD4d);
            var voci = state.Master.RequisitiDtc.Leggi(salvato).Voci;
            Assert.Contains(voci, v => v.Codice == "P0300" && string.IsNullOrEmpty(v.Veicolo));   // i guasti stanno nell archivio, non nel requisito
            Assert.Contains(voci, v => v.Codice == "P0300" && v.Veicolo == Veh);                  // stesso codice, veicolo diverso = voce distinta
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(SchedaD4d_ModificaMantieneLaRigaSelezionata_EMostraPannelloDtcModificabile_NuovoCreaD4d), ex); }
    }

    [Fact]
    public void Audit_SezioniStandardED4dSeparate_EFiltroTipoRequisito()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var audit = new MainShellPage(window, App).GoToAudit();
            audit.SelezionaVeicolo(Veh);
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Audit_Sezione_Standard_Text"), "sezione Standard");
            ElementFinders.WaitById(window, "Audit_Sezione_Extra_Text");
            // Avanzamento guasto dei requisiti Extra nella sezione Extra (calcolato in background): 0/2 guasto completi.
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Audit_RiepilogoD4d_Text").Name.Contains("0/2 guasto completi"), "riepilogo guasto nella sezione Extra");

            // Filtro: solo Extra -> sparisce la sezione Standard.
            ElementFinders.WaitButtonById(window, "Audit_FiltroTipoRequisito_Button").SafeInvoke();
            var filtro = new FiltroMultiSelectPage(App);
            filtro.SelezionaVoce("Extra");
            Wait.ForCondition(() => !ElementFinders.ExistsById(window, "Audit_Sezione_Standard_Text"), "sezione Standard nascosta dal filtro");
            Assert.True(ElementFinders.ExistsById(window, "Audit_Sezione_Extra_Text"));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Audit_SezioniStandardED4dSeparate_EFiltroTipoRequisito), ex); }
    }

    private static string IdRiga(string codice, string? requisito) =>
        System.Text.RegularExpressions.Regex.Replace(codice + "_" + Componente + "_" + Veh + (requisito == null ? "" : "_" + requisito), "[^A-Za-z0-9]", "_");

    [Fact]
    public void EditorTest_BloccoAggiungiDtc_DaElencoEManuale_GeneraLeCoppieEPersiste()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var lista = new MainShellPage(window).GoToTestList();
            var editor = lista.OpenRowById("TC-guasto-INJ");
            editor.EnterEditMode();

            // componente preselezionata (unica del test); guasto dall'elenco dei Extra collegati -> requisito di provenienza.
            ElementFinders.WaitComboBoxById(window, "Editor_DtcInjection_Veicolo_Combo");
            ElementFinders.WaitComboBoxById(window, "Editor_DtcInjection_Componente_Combo");
            ElementFinders.WaitComboBoxById(window, "Editor_DtcInjection_Guasto_Combo").Select($"P0100 · {Componente} · {ReqD4d}");
            ElementFinders.WaitButtonById(window, "Editor_DtcInjection_Aggiungi_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, $"Editor_DtcInjection_Riga_{IdRiga("P0100", ReqD4d)}_Codice_Text"), "riga del guasto da elenco");
            ElementFinders.WaitById(window, $"Editor_DtcInjection_Riga_{IdRiga("P0100", ReqD4d)}_Componente_Combo");
            ElementFinders.WaitById(window, $"Editor_DtcInjection_Riga_{IdRiga("P0100", ReqD4d)}_Requisito_Combo");
            ElementFinders.WaitById(window, $"Editor_DtcInjection_Riga_{IdRiga("P0100", ReqD4d)}_Rimuovi_Button");

            // guasto manuale senza requisito.
            ElementFinders.WaitTextBoxById(window, "Editor_DtcInjection_Manuale_Text").SetText("P0999");
            ElementFinders.WaitById(window, "Editor_DtcInjection_Requisito_Combo");
            ElementFinders.WaitButtonById(window, "Editor_DtcInjection_Aggiungi_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, $"Editor_DtcInjection_Riga_{IdRiga("P0999", null)}_Codice_Text"), "riga del guasto manuale");

            editor.Save();
            Wait.ForCondition(() => !editor.CanSave, "salvataggio dell'editor");

            using var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
            var ops = state.TestCases.GetById("TC-guasto-INJ")!.Esecuzioni[^1].Condizioni;
            Assert.Equal(4, ops.Count);
            Assert.All(ops, c => Assert.Equal("Guasto Injection", c.Strumento));
            Assert.Equal(new[] { PassoDtc.Validazione, PassoDtc.Devalidazione, PassoDtc.Validazione, PassoDtc.Devalidazione }, ops.Select(c => c.DtcPasso!.Value).ToArray());
            Assert.Equal(new[] { ReqD4d, ReqD4d, null, null }, ops.Select(c => c.DtcRequisitoId).ToArray());
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(EditorTest_BloccoAggiungiDtc_DaElencoEManuale_GeneraLeCoppieEPersiste), ex); }
    }

    [Fact]
    public void EditorTest_SelettoreDtcDellOperazione_ImpostaDtcPassoEPersiste()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var lista = shell.GoToTestList();
            var editor = lista.OpenRowById("TC-guasto-EDIT");
            editor.EnterEditMode();

            var combo = ElementFinders.WaitComboBoxById(window, $"Editor_Condizione_{CondUid}_Guasto_Combo");
            combo.Select($"P0200 · {Componente} · {ReqD4d}");
            // Dopo la scelta compare il passo (default Validazione) e il pulsante Rimuovi guasto.
            var passo = ElementFinders.WaitComboBoxById(window, $"Editor_Condizione_{CondUid}_DtcPasso_Combo");
            Assert.NotNull(passo);
            ElementFinders.WaitById(window, $"Editor_Condizione_{CondUid}_DtcRimuovi_Button");
            // Riepilogo di sola lettura: stato del guasto e N/M del requisito Extra (calcolo in background).
            Wait.ForCondition(() =>
            {
                var t = ElementFinders.TryFindById(window, $"Editor_Condizione_{CondUid}_DtcStato_Text")?.Name ?? "";
                return t.Contains("Da fare") && t.Contains($"{ReqD4d} 0/2");
            }, "riepilogo stato guasto nell'operazione");
            passo.Select("Devalidazione");

            var esito = ElementFinders.WaitComboBoxById(window, $"Editor_Condizione_{CondUid}_Esito_Combo");
            esito.Select("Conforme");
            editor.Save();
            Wait.ForCondition(() => !editor.CanSave, "salvataggio dell'editor");
            // La scrittura su disco puo' concludersi dopo la scomparsa del pulsante Salva: si attende il dato persistito, non solo la UI.
            Wait.ForCondition(() =>
            {
                using var verifica = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
                return verifica.TestCases.GetById("TC-guasto-EDIT")?.Esecuzioni[^1].Condizioni.SingleOrDefault()?.Guasto == "P0200";
            }, "Guasto dell'operazione scritto nel JSON salvato");

            using var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
            var c = state.TestCases.GetById("TC-guasto-EDIT")!.Esecuzioni[^1].Condizioni.Single();
            var tcSalvato = state.TestCases.GetById("TC-guasto-EDIT")!;
            Assert.True(c.Guasto == "P0200", "JSON salvato: " + System.IO.File.ReadAllText(state.TestCases.FilePathOf(tcSalvato)));
            Assert.Equal(ReqD4d, c.DtcRequisitoId);
            Assert.Equal(PassoDtc.Devalidazione, c.DtcPasso);
            Assert.Equal(Esito.Conforme, c.Esito);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(EditorTest_SelettoreDtcDellOperazione_ImpostaDtcPassoEPersiste), ex); }
    }
}
