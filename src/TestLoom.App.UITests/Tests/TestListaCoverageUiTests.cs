using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Copertura della Lista Test non ancora esercitata: ogni filtro (Esito, Stato lavoro, Tipo test, Assegnati a me,
/// Creati da me), salvataggio/applicazione/eliminazione di una vista salvata (verificata nella configurazione
/// locale isolata), archiviazione multipla, archiviazione dall'editor con ripristino e cancellazione definitiva
/// dall'Archivio, creazione con un secondo tipo test. Quattro Test seminati con attributi diversi, prima dell'avvio.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class TestListaCoverageUiTests : UiTestBase
{
    private const string NomeVista = "Solo conformi QA";
    private const string Conforme = "TC-QA-0001";
    private const string NonConforme = "TC-QA-0002";
    private const string SenzaEcu = "TC-QA-0003";
    private const string SecondoTemplate = "TC-QA-0004";

    public TestListaCoverageUiTests() : base(CreaWorkspaceConTestDiProva()) { }

    private static TestWorkspace CreaWorkspaceConTestDiProva()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(TestListaCoverageUiTests));
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        var templatePrincipale = TestCoverageSeed.UidTemplatePrincipale(state);
        var templateSecondo = TestCoverageSeed.AggiungiTemplateSecondario(state);

        TestCoverageSeed.SeedTest(state, Conforme, "Conforme in corso assegnato all'operatore", templatePrincipale, TestWorkspace.DefaultEcuName,
            Esito.Conforme, StatoLavoroTest.InCorso, assegnatario: TestWorkspace.DefaultOperatorName, creatoDa: TestWorkspace.DefaultOperatorName);
        TestCoverageSeed.SeedTest(state, NonConforme, "Non conforme pianificato di un altro utente", templatePrincipale, TestWorkspace.DefaultEcuName,
            Esito.NonConforme, StatoLavoroTest.Pianificato, creatoDa: "Altro Utente");
        TestCoverageSeed.SeedTest(state, SenzaEcu, "Da eseguire senza componente", templatePrincipale, null,
            Esito.DaEseguire, StatoLavoroTest.InAttesa, creatoDa: "Altro Utente");
        TestCoverageSeed.SeedTest(state, SecondoTemplate, "Conforme con secondo tipo test", templateSecondo, TestWorkspace.DefaultEcuName,
            Esito.Conforme, StatoLavoroTest.Pianificato, creatoDa: TestWorkspace.DefaultOperatorName);
        return workspace;
    }

    private Window Finestra() => App.WaitForMainWindow();

    private TestListPage Lista() => new MainShellPage(Finestra()).GoToTestList();

    private TestListPage RicaricaLista() => new(Finestra(), Finestra());

    private static IReadOnlyList<string> IdOrdinati(TestListPage lista) =>
        lista.VisibleRowIds().OrderBy(id => id, StringComparer.Ordinal).ToList();

    /// <summary>Apre un filtro multi-selezione dal suo pulsante, sceglie una voce e chiude il selettore con Esc.</summary>
    private void ApriFiltroMultiSelect(string automationId, string voce)
    {
        ElementFinders.WaitButtonById(Finestra(), automationId).SafeInvoke();
        new FiltroMultiSelectPage(App).SelezionaVoce(voce);
        ElementActions.PressKey(Finestra(), VirtualKeyShort.ESCAPE);
        Wait.ForCondition(() => App.IsWindowClosedByContent("FiltroMultiSelect_Lista"), "chiusura del selettore multiplo");
    }

    private void RimuoviFiltri() =>
        ElementFinders.WaitButtonById(Finestra(), "TestList_RimuoviFiltri_Button").SafeInvoke();

    /// <summary>Chiude "Filtri avanzati" se aperto: con il pannello espanso la griglia e' piu' bassa e mostra meno righe,
    /// quindi i conteggi delle righe visibili vanno letti a pannello chiuso (il filtro resta attivo).</summary>
    private void ChiudiFiltriAvanzati()
    {
        if (!ElementFinders.ExistsById(Finestra(), "TestList_FiltroStatoLavoro_Button")) return;
        ElementFinders.WaitButtonById(Finestra(), "TestList_FiltriAvanzati_Button").SafeInvoke();
        Wait.ForCondition(() => !ElementFinders.ExistsById(Finestra(), "TestList_FiltroStatoLavoro_Button"), "filtri avanzati chiusi");
    }

    /// <summary>Un'azione di massa puo' chiedere conferma e poi mostrare l'esito ("OK"): si premono i dialoghi di
    /// conferma che compaiono, finche' la condizione attesa non si verifica.</summary>
    private void ConfermaDialoghiFinoA(Func<bool> esitoAtteso, string descrizione)
    {
        Wait.ForCondition(() =>
        {
            try
            {
                var dialogo = App.WaitForWindowByContent("Confirm_Yes_Button", TimeSpan.FromMilliseconds(300));
                new ConfirmDialogPage(dialogo).ConfirmYes();
            }
            catch (TimeoutException) { }
            return esitoAtteso();
        }, descrizione);
    }

    /// <summary>Riga dell'Archivio che mostra il testo indicato (l'Id del Test). I pulsanti per riga condividono lo stesso
    /// AutomationId e l'Archivio elenca anche i template archiviati: si agisce solo sulla riga giusta.</summary>
    private AutomationElement RigaArchivio(AutomationElement griglia, string frammento, string pulsanteId)
    {
        var testo = Wait.Until(
            () => griglia.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
                .FirstOrDefault(e => (e.Name ?? "").Contains(frammento, StringComparison.Ordinal)),
            e => e != null, null, $"riga Archivio '{frammento}'")!;
        for (var corrente = testo.Parent; corrente != null; corrente = corrente.Parent)
        {
            if (corrente.FindFirstDescendant(cf => cf.ByAutomationId(pulsanteId)) != null) return corrente;
        }
        throw new InvalidOperationException($"Nessuna riga Archivio con il pulsante '{pulsanteId}' per '{frammento}'.");
    }

    private static void PremiNellaRiga(AutomationElement riga, string pulsanteId) =>
        riga.FindFirstDescendant(cf => cf.ByAutomationId(pulsanteId))!.AsButton().SafeInvoke();

    /// <summary>I filtri del tipo test e dello stato lavoro vivono in "Filtri avanzati": si espande solo se chiuso.</summary>
    private void ApriFiltriAvanzati()
    {
        if (ElementFinders.ExistsById(Finestra(), "TestList_FiltroStatoLavoro_Button")) return;
        ElementFinders.WaitButtonById(Finestra(), "TestList_FiltriAvanzati_Button").SafeInvoke();
        Wait.ForCondition(() => ElementFinders.ExistsById(Finestra(), "TestList_FiltroStatoLavoro_Button"), "filtri avanzati espansi");
    }

    /// <summary>Seleziona una riga della griglia per Id (SelectionItem della riga; in fallback Ctrl+click per aggiungerla).</summary>
    private void SelezionaRiga(string id, bool aggiungi = false)
    {
        var cella = Wait.Until(
            () => Finestra().FindAllDescendants(cf => cf.ByAutomationId("TestList_Row_Id_Text")).FirstOrDefault(e => e.AsLabel().Text == id),
            e => e != null, null, $"riga Test '{id}'")!;
        var selezione = ContenitoreRiga(cella).Patterns.SelectionItem.PatternOrDefault;
        if (selezione == null)
        {
            if (aggiungi)
            {
                using (FlaUI.Core.Input.Keyboard.Pressing(VirtualKeyShort.CONTROL))
                    cella.Click();
            }
            else cella.Click();
            return;
        }
        if (aggiungi) selezione.AddToSelection();
        else selezione.Select();
    }

    /// <summary>Risale dalla cella Id al primo antenato che contiene anche il pulsante "Apri" della stessa riga.</summary>
    private static AutomationElement ContenitoreRiga(AutomationElement cellaId)
    {
        var corrente = cellaId.Parent;
        for (var i = 0; i < 10 && corrente != null; i++, corrente = corrente.Parent)
        {
            if (corrente.FindFirstDescendant(cf => cf.ByAutomationId("TestList_Open_Button")) != null)
                return corrente;
        }
        return cellaId.Parent ?? cellaId;
    }

    /// <summary>Test archiviato dall'editor (conferma), poi Impostazioni > Archivio aperto e pronto per Ripristina/Elimina.</summary>
    private void ArchiviaDallEditorEApriArchivio(string id)
    {
        var editor = Lista().OpenRowById(id);
        editor.EnterEditMode();
        editor.Archivia();
        new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
        Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura conferma archiviazione");

        var listaDopo = RicaricaLista();
        Wait.ForCondition(() => listaDopo.RowCount() == 3, "il Test archiviato esce dalla Lista Test");

        new MainShellPage(Finestra()).GoToImpostazioni().VaiA("archivio");
        Wait.ForCondition(() => ElementFinders.ExistsById(Finestra(), "Archivio_Title_Text"), "pagina Archivio caricata");
    }

    private static AutomationElement? TestoNellaGriglia(AutomationElement griglia, string frammento) =>
        griglia.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
            .FirstOrDefault(e => (e.Name ?? "").Contains(frammento, StringComparison.Ordinal));

    [Fact]
    public void FiltroEsito_MostraSoloITestConLEsitoScelto_ERimuoviFiltriRipristina()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 4, "quattro Test seminati visibili");

            ApriFiltroMultiSelect("TestList_FiltroEsito_Button", "Conforme");
            Wait.ForCondition(() => lista.RowCount() == 2, "filtro Esito 'Conforme': restano due Test");
            Assert.Equal(new[] { Conforme, SecondoTemplate }, IdOrdinati(lista));

            RimuoviFiltri();
            Wait.ForCondition(() => lista.RowCount() == 4, "Rimuovi filtri ripristina l'elenco completo");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltroEsito_MostraSoloITestConLEsitoScelto_ERimuoviFiltriRipristina), ex); }
    }

    [Fact]
    public void FiltroStatoLavoro_InCorso_MostraSoloIlTestInCorso()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 4, "quattro Test seminati visibili");

            ApriFiltriAvanzati();
            ApriFiltroMultiSelect("TestList_FiltroStatoLavoro_Button", "In corso");
            Wait.ForCondition(() => lista.RowCount() == 1, "filtro Stato lavoro 'In corso': resta un Test");
            Assert.Equal(new[] { Conforme }, IdOrdinati(lista));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltroStatoLavoro_InCorso_MostraSoloIlTestInCorso), ex); }
    }

    [Fact]
    public void FiltroTipoTest_SecondoTemplate_MostraSoloIlTestDiQuelTipo()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 4, "quattro Test seminati visibili");

            ApriFiltriAvanzati();
            ApriFiltroMultiSelect("TestList_FiltroTipoTest_Button", TestCoverageSeed.NomeTemplateSecondario);
            Wait.ForCondition(() => lista.RowCount() == 1, "filtro Tipo test: resta il Test del secondo template");
            Assert.Equal(new[] { SecondoTemplate }, IdOrdinati(lista));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltroTipoTest_SecondoTemplate_MostraSoloIlTestDiQuelTipo), ex); }
    }

    [Fact]
    public void FiltriRapidiAssegnatiAMeECreatiDaMe_SiAttivanoESiDisattivano()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 4, "quattro Test seminati visibili");

            ElementFinders.WaitById(Finestra(), "TestList_FiltroAssegnatiAMe_ToggleButton").AsToggleButton().Toggle();
            Wait.ForCondition(() => lista.RowCount() == 1, "'Assegnati a me' lascia solo il Test assegnato all'operatore");
            Assert.Equal(new[] { Conforme }, IdOrdinati(lista));
            ElementFinders.WaitById(Finestra(), "TestList_FiltroAssegnatiAMe_ToggleButton").AsToggleButton().Toggle();
            Wait.ForCondition(() => lista.RowCount() == 4, "disattivato 'Assegnati a me' torna l'elenco completo");

            ElementFinders.WaitById(Finestra(), "TestList_FiltroCreatiDaMe_ToggleButton").AsToggleButton().Toggle();
            Wait.ForCondition(() => lista.RowCount() == 2, "'Creati da me' lascia i Test creati dall'operatore");
            Assert.Equal(new[] { Conforme, SecondoTemplate }, IdOrdinati(lista));
            ElementFinders.WaitById(Finestra(), "TestList_FiltroCreatiDaMe_ToggleButton").AsToggleButton().Toggle();
            Wait.ForCondition(() => lista.RowCount() == 4, "disattivato 'Creati da me' torna l'elenco completo");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltriRapidiAssegnatiAMeECreatiDaMe_SiAttivanoESiDisattivano), ex); }
    }

    [Fact]
    public void VistaSalvata_SalvaApplicaEElimina_EResteCoerenteConLaConfigurazioneLocale()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 4, "quattro Test seminati visibili");

            ApriFiltroMultiSelect("TestList_FiltroEsito_Button", "Conforme");
            Wait.ForCondition(() => lista.RowCount() == 2, "filtro Esito 'Conforme' attivo");

            ElementFinders.WaitButtonById(Finestra(), "TestList_SalvaVista_Button").SafeInvoke();
            var dialogoNome = App.WaitForWindowByContent("TextEdit_Content_TextBox");
            ElementFinders.WaitTextBoxById(dialogoNome, "TextEdit_Content_TextBox").SetText(NomeVista);
            ElementFinders.WaitButtonById(dialogoNome, "TextEdit_Save_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("TextEdit_Content_TextBox"), "chiusura del dialogo nome vista");
            Wait.ForCondition(() => File.ReadAllText(Workspace.ConfigPath).Contains(NomeVista, StringComparison.Ordinal),
                "vista salvata nella configurazione locale del workspace");

            RimuoviFiltri();
            Wait.ForCondition(() => lista.RowCount() == 4, "filtri rimossi prima di applicare la vista");

            ElementFinders.WaitComboBoxById(Finestra(), "TestList_VisteSalvate_ComboBox").SelectByText(NomeVista);
            Wait.ForCondition(() => lista.RowCount() == 2, "applicare la vista salvata ripristina il filtro Esito");
            Assert.Equal(new[] { Conforme, SecondoTemplate }, IdOrdinati(lista));

            ElementFinders.WaitButtonById(Finestra(), "TestList_EliminaVista_Button").SafeInvoke();
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura conferma eliminazione vista");
            Wait.ForCondition(() => !File.ReadAllText(Workspace.ConfigPath).Contains(NomeVista, StringComparison.Ordinal),
                "vista eliminata dalla configurazione locale del workspace");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(VistaSalvata_SalvaApplicaEElimina_EResteCoerenteConLaConfigurazioneLocale), ex); }
    }

    [Fact]
    public void ArchiviaSelezionati_UnTest_EscoDallaListaESalvaComeArchiviato()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 4, "quattro Test seminati visibili");

            // Una sola riga selezionata: la selezione multipla con la tastiera non e' esposta via UIA in questa griglia.
            SelezionaRiga(NonConforme);
            ElementFinders.WaitButtonById(Finestra(), "TestList_ArchiviaSelezionati_Button").SafeInvoke();
            ConfermaDialoghiFinoA(() => lista.RowCount() == 3, "il Test archiviato esce dalla lista (tre Test rimasti)");

            Assert.Equal(new[] { Conforme, SenzaEcu, SecondoTemplate }, IdOrdinati(lista));
            Assert.True(TestCoverageSeed.EArchiviato(Workspace, NonConforme), "il Test archiviato risulta tombstone sul disco (cancellazione logica)");
            Assert.False(TestCoverageSeed.EArchiviato(Workspace, SenzaEcu), "gli altri Test non vanno archiviati");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ArchiviaSelezionati_UnTest_EscoDallaListaESalvaComeArchiviato), ex); }
    }

    [Fact]
    public void ArchiviaSelezionati_DueTestSelezionati_EsconoDallaListaESonoTombstone()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 4, "quattro Test seminati visibili");

            SelezionaRiga(NonConforme);
            SelezionaRiga(SenzaEcu, aggiungi: true);
            ElementFinders.WaitButtonById(Finestra(), "TestList_ArchiviaSelezionati_Button").SafeInvoke();
            ConfermaDialoghiFinoA(() => lista.RowCount() == 2,
                "i due Test selezionati escono dalla lista dopo la conferma");

            Assert.Equal(new[] { Conforme, SecondoTemplate }, IdOrdinati(lista));
            Assert.True(TestCoverageSeed.EArchiviato(Workspace, NonConforme),
                "il primo Test selezionato diventa tombstone");
            Assert.True(TestCoverageSeed.EArchiviato(Workspace, SenzaEcu),
                "il secondo Test selezionato diventa tombstone");
            Assert.False(TestCoverageSeed.EArchiviato(Workspace, Conforme),
                "il Test non selezionato resta attivo");
            Assert.False(TestCoverageSeed.EArchiviato(Workspace, SecondoTemplate),
                "l'altro Test non selezionato resta attivo");
            Assert.Null(TestCoverageSeed.TrovaTestCase(Workspace, NonConforme));
            Assert.Null(TestCoverageSeed.TrovaTestCase(Workspace, SenzaEcu));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ArchiviaSelezionati_DueTestSelezionati_EsconoDallaListaESonoTombstone), ex);
        }
    }

    [Fact]
    public void ArchiviaDallEditor_PoiRipristinaDallArchivio_TornaInListaEAttivo()
    {
        try
        {
            ArchiviaDallEditorEApriArchivio(NonConforme);
            var griglia = ElementFinders.WaitById(Finestra(), "Archivio_Grid");
            Wait.ForCondition(() => TestoNellaGriglia(griglia, NonConforme) != null, "il Test archiviato compare in Archivio");

            PremiNellaRiga(RigaArchivio(griglia, NonConforme, "Archivio_Ripristina_Button"), "Archivio_Ripristina_Button");
            Wait.ForCondition(() => TestoNellaGriglia(griglia, NonConforme) == null, "il Test non compare piu' in Archivio dopo Ripristina");

            var listaFinale = Lista();
            Wait.ForCondition(() => listaFinale.RowCount() == 4, "il Test ripristinato torna nella Lista Test");
            Assert.False(TestCoverageSeed.EArchiviato(Workspace, NonConforme), "dopo Ripristina il file non e' piu' un tombstone");
            Assert.NotNull(TestCoverageSeed.TrovaTestCase(Workspace, NonConforme));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ArchiviaDallEditor_PoiRipristinaDallArchivio_TornaInListaEAttivo), ex); }
    }

    [Fact]
    public void ArchivioEliminaDefinitivamente_RimuoveIlFileDelTestDalDisco()
    {
        try
        {
            ArchiviaDallEditorEApriArchivio(NonConforme);
            var griglia = ElementFinders.WaitById(Finestra(), "Archivio_Grid");
            Wait.ForCondition(() => TestoNellaGriglia(griglia, NonConforme) != null, "il Test archiviato compare in Archivio");
            Assert.True(TestCoverageSeed.EArchiviato(Workspace, NonConforme), "precondizione: il Test e' archiviato (tombstone) prima della cancellazione");

            PremiNellaRiga(RigaArchivio(griglia, NonConforme, "Archivio_EliminaDefinitivamente_Button"), "Archivio_EliminaDefinitivamente_Button");
            // La rimozione fisica della copia nel cestino passa dalla transazione/journal del workspace e puo' essere
            // differita: qui si verificano l'esito visibile e l'assenza di un Test leggibile (la copia non e' asserita).
            ConfermaDialoghiFinoA(() => TestoNellaGriglia(griglia, NonConforme) == null,
                "il Test eliminato definitivamente non compare piu' in Archivio");
            Assert.Null(TestCoverageSeed.TrovaTestCase(Workspace, NonConforme));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ArchivioEliminaDefinitivamente_RimuoveIlFileDelTestDalDisco), ex); }
    }

    [Fact]
    public void FiltriEcuCreatoDaEAssegnatario_OgniFiltroRestringeLElencoERimuoveTornaTutto()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 4, "quattro Test seminati visibili");

            ApriFiltroMultiSelect("TestList_FiltroComponente_Button", TestWorkspace.DefaultEcuName);
            Wait.ForCondition(() => lista.RowCount() == 3, "filtro componente: esce il Test senza componente");
            Assert.Equal(new[] { Conforme, NonConforme, SecondoTemplate }, IdOrdinati(lista));
            RimuoviFiltri();
            Wait.ForCondition(() => lista.RowCount() == 4, "rimossi i filtri, tornano tutti i Test");

            ApriFiltriAvanzati();
            ApriFiltroMultiSelect("TestList_FiltroCreatoDa_Button", "Altro Utente");
            ChiudiFiltriAvanzati();
            Wait.ForCondition(() => lista.RowCount() == 2, "filtro Creato da: restano i Test creati dall'altro utente");
            Assert.Equal(new[] { NonConforme, SenzaEcu }, IdOrdinati(lista));
            RimuoviFiltri();
            Wait.ForCondition(() => lista.RowCount() == 4, "rimossi i filtri dopo Creato da");

            ApriFiltroMultiSelect("TestList_FiltroAssegnatario_Button", TestWorkspace.DefaultOperatorName);
            Wait.ForCondition(() => lista.RowCount() == 1, "filtro Assegnatario: resta il Test assegnato all'operatore");
            Assert.Equal(new[] { Conforme }, IdOrdinati(lista));
            RimuoviFiltri();
            Wait.ForCondition(() => lista.RowCount() == 4, "rimossi i filtri dopo Assegnatario");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltriEcuCreatoDaEAssegnatario_OgniFiltroRestringeLElencoERimuoveTornaTutto), ex); }
    }

    [Fact]
    public void CreaTest_SceltoIlSecondoTemplate_SalvaUnTestConQuelTipoENonIlPrincipale()
    {
        try
        {
            var templatePrincipale = TestCoverageSeed.TrovaTestCase(Workspace, Conforme)!.TestTemplateUid;
            var templateSecondo = TestCoverageSeed.TrovaTestCase(Workspace, SecondoTemplate)!.TestTemplateUid;

            var editor = Lista().ClickNew();
            editor.ChooseTemplateByName(TestCoverageSeed.NomeTemplateSecondario);
            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            editor.SetDescrizioneBreve("QA secondo template - nuovo");
            editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);
            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();

            var listaDopo = RicaricaLista();
            Wait.ForCondition(() => listaDopo.RowCount() == 5, "il nuovo Test compare nella Lista Test");

            var nuovo = TestCoverageSeed.FileTestCase(Workspace)
                .Select(TestCoverageSeed.LeggiTestCase)
                .Single(t => t.DescrizioneBreve == "QA secondo template - nuovo");
            Assert.Equal(templateSecondo, nuovo.TestTemplateUid);
            Assert.NotEqual(templatePrincipale, nuovo.TestTemplateUid);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CreaTest_SceltoIlSecondoTemplate_SalvaUnTestConQuelTipoENonIlPrincipale), ex); }
    }
}
