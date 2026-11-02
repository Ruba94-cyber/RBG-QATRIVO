using System;
using System.Collections.Generic;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Navigazione incrociata tra Anomalie/CR, Test, Segnalazione di rete e Change Request, e ritorno con
/// Alt+Left (come Indietro della shell): riga NC -> Test (Apri test / Modifica test), riga NC -> Segnalazione
/// (Modifica segnalazione), riga CR -> editor (Apri / Modifica), Test di validazione della CR -> Test, editor CR
/// -> Anomalie con Alt+Left, e conservazione di ricerca e filtri al ritorno. Nessuna suite precedente percorre
/// questi passaggi (COPERTURA_FLAUI.md, NavigazioneIndietroUiTests copre solo le sezioni principali).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Navigation")]
public sealed class AnomalieCrCoverageNavigazioneUiTests : UiTestBase
{
    private const string TestNav = "TC-QA-NAV";
    private const string NcTest = "NC-QA-0001";
    private const string NcSegnalazione = "NC-QA-0002";

    public AnomalieCrCoverageNavigazioneUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(AnomalieCrCoverageNavigazioneUiTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        var templates = state.Master.TestTemplate.Load();
        templates.Add(new TestTemplate { Nome = "CR Template QA", IsChangeRequest = true, ModificatoDa = "UITests" });
        state.Master.TestTemplate.Save(templates);
        var templateCr = templates.Single(t => t.Nome == "CR Template QA");

        var testNav = new TestCase
        {
            Id = TestNav, DescrizioneBreve = "Test per la navigazione QA", Componente = { TestWorkspace.DefaultEcuName },
            Veicolo = TestWorkspace.DefaultVehicleName, Veicoli = { TestWorkspace.DefaultVehicleName },
            Assegnatario = TestWorkspace.DefaultOperatorName, StatoLavoro = StatoLavoroTest.InCorso, DataCreazione = DateTime.Now.AddDays(-2),
            UltimaModifica = DateTime.Now.AddDays(-2), ModificatoDa = "UITests",
        };
        state.TestCases.Save(testNav, isNew: true);

        // TestUid come quando il collegamento nasce dal dialogo: la riga NC risolve il Test per uid (ApriTest visibile).
        state.NonConformita.Save(new NonConformita
        {
            Id = NcTest, TestCaseId = TestNav, TestUid = testNav.Uid, DataApertura = DateTime.UtcNow.AddMinutes(-3),
            Severita = SeveritaAnomalia.Medium, StatoWorkflow = StatoAnomalia.InVerifica,
            Descrizione = "NC collegata al test di navigazione",
        });
        state.NonConformita.Save(new NonConformita
        {
            Id = NcSegnalazione, DataApertura = DateTime.UtcNow.AddMinutes(-2), Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.InVerifica, Priorita = PrioritaOperativa.Normal,
            Descrizione = "Segnalazione da aprire dalla riga", DescrizioneBreveSegnalata = "Sintesi navigazione",
            DaSegnalazioneRete = true, EcuSegnalate = new EcuSelection(new[] { TestWorkspace.DefaultEcuName }),
            FunzionalitaSegnalate = new() { "Infotainment" }, NumeriPraticaRete = new() { "PRATICA-NAV-1" },
            VinCliente = new() { "ZFA0000000000NAV1" }, Licenze = new() { "LIC-NAV-1" },
        });

        void Cr(string id, string titolo, params CollegamentoValidazioneCr[] validazioni)
        {
            var cr = new ChangeRequest
            {
                Id = id, Titolo = titolo, Descrizione = "Descrizione " + titolo, TemplateUid = templateCr.Uid,
                EcuImpattate = new() { TestWorkspace.DefaultEcuName }, Stato = StatoChangeRequest.Aperta,
            };
            cr.TestValidazione.AddRange(validazioni);
            state.ChangeRequests.Save(cr);
        }

        Cr("CR-QA-0001", "CR Navigazione semplice");
        Cr("CR-QA-0003", "CR Navigazione con test", new CollegamentoValidazioneCr { TestId = TestNav, Obbligatorio = true });
        return workspace;
    }

    private static bool VisibileById(Window window, string automationId)
    {
        var el = ElementFinders.TryFindById(window, automationId);
        return el != null && el.BoundingRectangle.Width > 0 && el.BoundingRectangle.Height > 0;
    }

    /// <summary>Alt+Left, come il pulsante Indietro della shell (NavigazioneIndietroUiTests). Il focus va prima sul
    /// pulsante Indietro della shell (controllo non di testo): un campo di testo con il focus puo' assorbire la
    /// combinazione prima che arrivi alla finestra, che la gestisce in MainWindow.KeyDown.</summary>
    private void AltSinistra(Window window)
    {
        ElementFinders.WaitById(window, "Main_Indietro_Button").Focus();
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.LEFT);
    }

    private void CercaNc(Window window, string id)
    {
        var anomalie = new MainShellPage(window).GoToAnomalie();
        anomalie.Cerca(id);
        Wait.ForCondition(() => anomalie.TotaleFiltrato() == 1, $"la NC {id} compare cercandola per Id");
    }

    /// <summary>Revisioni salvate nel file della CR, lette dal disco: aprire una CR in sola lettura non ne aggiunge.</summary>
    private int RevisioniSalvate(string id) =>
        (JsonIo.TryRead<ChangeRequest>(new DataPaths(Workspace.WorkspaceDir).ChangeRequestFile(id), out var errore)
            ?? throw new InvalidOperationException($"CR {id} non leggibile da disco: {errore}")).Revisioni.Count;

    [Fact]
    public void RigaNc_ApriTest_ApreIlTestCollegatoEAltSinistraTornaAlleAnomalie()
    {
        try
        {
            var window = App.WaitForMainWindow();
            CercaNc(window, NcTest);
            ElementFinders.WaitButtonById(window, "Anomalie_Riga_Dettaglio_Button").SafeInvoke();
            var dettaglio = App.WaitForWindowByContent("Anomalie_Riga_ApriTest_Button");
            Assert.False(ElementFinders.WaitTextBoxById(dettaglio, "AnomaliaDettaglio_Owner_TextBox").IsEnabled,
                "Apri mantiene il dettaglio in sola lettura anche per chi può modificare");
            ElementFinders.WaitButtonById(dettaglio, "Anomalie_Riga_ApriTest_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Editor_Title_Text"), "Apri test apre l'editor del Test collegato");

            AltSinistra(window);
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "Alt+Left riporta alla pagina Anomalie/CR");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(RigaNc_ApriTest_ApreIlTestCollegatoEAltSinistraTornaAlleAnomalie), ex);
        }
    }

    [Fact]
    public void RigaNc_NonMostraPiuModificaTestEApriTestRestaNelDettaglio()
    {
        try
        {
            var window = App.WaitForMainWindow();
            CercaNc(window, NcTest);
            Assert.Null(window.FindFirstDescendant(cf => cf.ByAutomationId("Anomalie_Riga_ModificaTest_Button")));
            ElementFinders.WaitButtonById(window, "Anomalie_Riga_Dettaglio_Button").SafeInvoke();
            var dettaglio = App.WaitForWindowByContent("Anomalie_Riga_ApriTest_Button");
            Assert.True(ElementFinders.WaitButtonById(dettaglio, "Anomalie_Riga_ApriTest_Button").IsEnabled,
                "il test collegato si apre dal dettaglio dell'anomalia");
            ElementFinders.WaitButtonById(dettaglio, "AnomaliaDettaglio_Annulla_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Anomalie_Riga_ApriTest_Button"), "chiusura del dettaglio NC");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(RigaNc_NonMostraPiuModificaTestEApriTestRestaNelDettaglio), ex);
        }
    }

    [Fact]
    public void RigaNc_ModificaSegnalazione_ApreLEditorSegnalazioneEAltSinistraTorna()
    {
        try
        {
            var window = App.WaitForMainWindow();
            CercaNc(window, NcSegnalazione);
            ElementFinders.WaitButtonById(window, "Anomalie_Riga_ModificaRapida_Button").SafeInvoke();
            var modificaRapida = App.WaitForWindowByContent("Anomalie_NcModificaRapida_ModificaSegnalazione_Button");
            ElementFinders.WaitButtonById(modificaRapida, "Anomalie_NcModificaRapida_ModificaSegnalazione_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Segnalazione_Title_Text"),
                "la Modifica rapida conserva il percorso per l'editor della segnalazione di rete");

            AltSinistra(window);
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "Alt+Left riporta alla pagina Anomalie/CR");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(RigaNc_ModificaSegnalazione_ApreLEditorSegnalazioneEAltSinistraTorna), ex);
        }
    }

    [Fact]
    public void RigaCr_Apri_ApreEditorSolaLetturaETornaAllaListaSenzaModifiche()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var revisioniPrima = RevisioniSalvate("CR-QA-0001");
            var editor = ChangeRequestEditorFlow.ApriInSolaLettura(window, "CR-QA-0001", "Navigazione semplice");

            // Apri non apre piu' la finestra di dettaglio: l'editor e' in sola lettura, con Modifica disponibile a un Master
            // ma stato e Salva bloccati.
            Assert.True(App.IsWindowClosedByContent("Anomalie_Cr_Dettaglio_Titolo_Text"), "Apri non apre piu' la finestra di dettaglio");
            Wait.ForCondition(() => editor.InSolaLettura, "Apri apre l'editor della CR in sola lettura");
            Wait.ForCondition(() => editor.ModificaVisibile, "in sola lettura Modifica e' disponibile per il Master");
            Assert.False(ElementFinders.WaitComboBoxById(window, "Cr_Stato_Combo").IsEnabled, "in sola lettura lo stato non e' modificabile");
            Assert.False(ElementFinders.WaitButtonById(window, "Cr_Salva_Button").IsEnabled, "in sola lettura Salva e' disabilitato");

            editor.TornaAllaLista();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "Torna alla lista riporta alla pagina Anomalie");
            Assert.True(App.IsWindowClosedByContent("Confirm_Yes_Button"), "tornare alla lista senza modifiche non chiede conferme");
            Assert.Equal(revisioniPrima, RevisioniSalvate("CR-QA-0001"));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(RigaCr_Apri_ApreEditorSolaLetturaETornaAllaListaSenzaModifiche), ex);
        }
    }

    [Fact]
    public void RigaCr_ApriEModifica_ApreLEditorInModificaEAltSinistraTornaAlleAnomalie()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = ChangeRequestEditorFlow.ApriInModifica(window, "CR-QA-0001", "Navigazione semplice");
            Assert.False(editor.InSolaLettura, "Modifica riapre l'editor della CR in modifica");

            AltSinistra(window);
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "Alt+Left dall'editor CR riporta alle Anomalie/CR");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(RigaCr_ApriEModifica_ApreLEditorInModificaEAltSinistraTornaAlleAnomalie), ex);
        }
    }

    [Fact]
    public void TestDiValidazioneDellaCr_ApriTest_ApreIlTestEAltSinistraTornaAllaCr()
    {
        try
        {
            var window = App.WaitForMainWindow();
            ChangeRequestEditorFlow.ApriInModifica(window, "CR-QA-0003", "Navigazione con test");
            ElementFinders.WaitButtonById(window, $"Cr_ApriTest_{TestNav}").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Editor_Title_Text"), "il test di validazione si apre nell'editor Test");

            AltSinistra(window);
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Cr_Title_Text"), "Alt+Left riporta all'editor della Change Request");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(TestDiValidazioneDellaCr_ApriTest_ApreIlTestEAltSinistraTornaAllaCr), ex);
        }
    }

    [Fact]
    public void EditorCr_AltSinistra_TornaAllaPaginaAnomalieDaCuiSieApre()
    {
        try
        {
            var window = App.WaitForMainWindow();
            ChangeRequestEditorFlow.ApriInModifica(window, "CR-QA-0001", "Navigazione semplice");
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Cr_Title_Text"), "l'editor CR e' aperto");

            AltSinistra(window);
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "Alt+Left torna alla pagina Anomalie/CR");
            Wait.ForCondition(() => VisibileById(window, "Anomalie_SchedaChangeRequest_Toggle"),
                "la pagina Anomalie/CR ha di nuovo le sue schede");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(EditorCr_AltSinistra_TornaAllaPaginaAnomalieDaCuiSieApre), ex);
        }
    }

    [Fact]
    public void Anomalie_RicercaEFiltri_SiConservanoDopoIlRitornoDalTest()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            anomalie.Cerca(NcTest);
            Wait.ForCondition(() => anomalie.TotaleFiltrato() == 1, "la ricerca per Id lascia la sola NC collegata al test");

            ElementFinders.WaitButtonById(window, "Anomalie_Riga_Dettaglio_Button").SafeInvoke();
            var dettaglio = App.WaitForWindowByContent("Anomalie_Riga_ApriTest_Button");
            ElementFinders.WaitButtonById(dettaglio, "Anomalie_Riga_ApriTest_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Editor_Title_Text"), "si apre il Test collegato");
            AltSinistra(window);
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "Alt+Left torna alle Anomalie");

            Wait.ForCondition(() => ElementFinders.WaitTextBoxById(window, "Anomalie_FiltroTesto_Input").Text == NcTest,
                "la ricerca digitata resta nel campo dopo il ritorno");
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 1,
                "la lista filtrata resta la stessa dopo il ritorno");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Anomalie_RicercaEFiltri_SiConservanoDopoIlRitornoDalTest), ex);
        }
    }
}
