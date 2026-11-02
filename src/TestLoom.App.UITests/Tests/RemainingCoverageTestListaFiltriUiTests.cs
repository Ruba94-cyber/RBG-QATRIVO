using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Filtri secondari della Lista Test non ancora esercitati: Severita', Priorita', Stato anomalia (NC aperta collegata
/// al Test), Veicolo, Funzionalita', Requisito, intervallo di date e riferimento Ticketing. Tre Test seminati con
/// attributi diversi (A: severita' Alta, priorita' Critical, NC aperta con riferimento Ticketing; B: severita' Bassa,
/// priorita' Minor, NC gia' conclusa, creato nel 2024; C: senza severita', priorita' Normal, nessuna NC). Ogni filtro deve
/// lasciare visibile un sottoinsieme osservabile di righe.
///
/// Intervallo di date: i CalendarDatePicker della Lista Test si verificano per testo digitato, per calendario e per data
/// non valida (IntervalloDate_DigitatoNelTesto_..., IntervalloDate_DataDigitataNonValida_...). La vista salvata con
/// DataDa/DataA resta coperta dal suo test, che passa per ApplicaVista.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Navigation")]
public sealed class RemainingCoverageTestListaFiltriUiTests : UiTestBase
{
    private const string TestA = "TC-FLT-0001";
    private const string TestB = "TC-FLT-0002";
    private const string TestC = "TC-FLT-0003";
    private const string NcA = "NC-FLT-0001";
    private const string NcB = "NC-FLT-0002";
    private const string NomeVistaPeriodo = "Periodo FLT";

    public RemainingCoverageTestListaFiltriUiTests() : base(CreaWorkspaceConFiltri()) { }

    private static TestWorkspace CreaWorkspaceConFiltri()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(RemainingCoverageTestListaFiltriUiTests));

        using (var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false))
        {
            var template = TestCoverageSeed.UidTemplatePrincipale(state);

            var a = SalvaTest(state, TestA, "Test con severita' alta e priorita' critica", template,
                DateTime.Now, SeveritaAnomalia.High, PrioritaOperativa.Critical, "Veicolo-FLT-A", "Funz-FLT-A", "REQ-FLT-A");
            var b = SalvaTest(state, TestB, "Test con severita' bassa creato nel 2024", template,
                new DateTime(2024, 6, 1), SeveritaAnomalia.Low, PrioritaOperativa.Minor, "Veicolo-FLT-B", "Funz-FLT-B", "REQ-FLT-B");
            SalvaTest(state, TestC, "Test senza severita' ne' requisito", template,
                DateTime.Now, null, PrioritaOperativa.Normal, "Veicolo-FLT-C", "Funz-FLT-C", null);

            // NC aperta collegata al Test A, con un riferimento Ticketing (alimenta Stato anomalia e Ticketing).
            state.NonConformita.Save(new NonConformita
            {
                Id = NcA,
                TestCaseId = a.Id,
                TestUid = a.Uid,
                Descrizione = "NC aperta del Test A",
                Owner = TestWorkspace.DefaultOperatorName,
                StatoWorkflow = StatoAnomalia.InVerifica,
                Severita = SeveritaAnomalia.Medium,
                DataApertura = DateTime.Now,
                CollegamentiPolarion = new List<CollegamentoPolarion> { new() { Codice = "PL-FLT-0001" } },
            });
            // NC gia' conclusa sul Test B: non conta per "Stato anomalia" (solo le NC aperte).
            state.NonConformita.Save(new NonConformita
            {
                Id = NcB,
                TestCaseId = b.Id,
                TestUid = b.Uid,
                Descrizione = "NC gia' conclusa del Test B",
                Owner = TestWorkspace.DefaultOperatorName,
                StatoWorkflow = StatoAnomalia.Conclusa,
                Severita = SeveritaAnomalia.Low,
                DataApertura = DateTime.Now.AddDays(-30),
            });
        }

        // Vista salvata con intervallo di date: scritta a valle del ciclo di vita di AppState per non essere sovrascritta.
        var configService = new AppConfigService(workspace.ConfigPath);
        var config = configService.Load();
        config.VisteSalvate["test-list"] = new List<VistaSalvataFiltri>
        {
            new()
            {
                Nome = NomeVistaPeriodo,
                Valori = new Dictionary<string, string?>
                {
                    ["DataDa"] = "2024-05-01T00:00:00.0000000",
                    ["DataA"] = "2025-01-01T00:00:00.0000000",
                },
            },
        };
        configService.Save(config);
        return workspace;
    }

    private static TestCase SalvaTest(AppState state, string id, string descrizione, Guid template, DateTime creazione,
        SeveritaAnomalia? severita, PrioritaOperativa priorita, string veicolo, string funzionalita, string? requisito)
    {
        var tc = new TestCase
        {
            Id = id,
            DescrizioneBreve = descrizione,
            TestTemplateUid = template,
            CreatoDa = TestWorkspace.DefaultOperatorName,
            ModificatoDa = "UITests",
            DataCreazione = creazione,
            UltimaModifica = creazione,
            Severita = severita,
            Priorita = priorita,
            Veicolo = veicolo,
        };
        tc.Componente.Add(TestWorkspace.DefaultEcuName);
        tc.Veicoli.Add(veicolo);
        tc.Funzionalita.Add(funzionalita);
        if (requisito != null) tc.RequisitoId.Add(requisito);
        state.TestCases.Save(tc, true);
        return tc;
    }

    private Window Finestra() => App.WaitForMainWindow();

    private TestListPage Lista() => new MainShellPage(Finestra()).GoToTestList();

    private static IReadOnlyList<string> IdOrdinati(TestListPage lista) =>
        lista.VisibleRowIds().OrderBy(id => id, StringComparer.Ordinal).ToList();

    /// <summary>Severita', Priorita', Stato anomalia, Funzionalita' e Requisito vivono in "Filtri avanzati": si espande solo se chiuso.</summary>
    private void ApriFiltriAvanzati()
    {
        if (ElementFinders.ExistsById(Finestra(), "TestList_FiltroStatoLavoro_Button")) return;
        ElementFinders.WaitButtonById(Finestra(), "TestList_FiltriAvanzati_Button").SafeInvoke();
        Wait.ForCondition(() => ElementFinders.ExistsById(Finestra(), "TestList_FiltroStatoLavoro_Button"), "filtri avanzati espansi");
    }

    /// <summary>Chiude "Filtri avanzati" se aperto: con il pannello espanso la griglia e' piu' bassa e mostra meno righe,
    /// quindi i conteggi vanno letti a pannello chiuso (il filtro resta attivo).</summary>
    private void ChiudiFiltriAvanzati()
    {
        if (!ElementFinders.ExistsById(Finestra(), "TestList_FiltroStatoLavoro_Button")) return;
        ElementFinders.WaitButtonById(Finestra(), "TestList_FiltriAvanzati_Button").SafeInvoke();
        Wait.ForCondition(() => !ElementFinders.ExistsById(Finestra(), "TestList_FiltroStatoLavoro_Button"), "filtri avanzati chiusi");
    }

    /// <summary>Apre un filtro multi-selezione dal suo pulsante, sceglie una voce e chiude il selettore con Esc.</summary>
    private void ApriFiltroMultiSelect(string automationId, string voce)
    {
        ElementFinders.WaitButtonById(Finestra(), automationId).SafeInvoke();
        var filtro = new FiltroMultiSelectPage(App);
        filtro.SelezionaVoce(voce);
        filtro.ChiudiConEscape(voce);
    }

    private void RimuoviFiltri() =>
        ElementFinders.WaitButtonById(Finestra(), "TestList_RimuoviFiltri_Button").SafeInvoke();

    [Fact]
    public void FiltroSeverita_Alta_MostraSoloIlTestConSeveritaAlta()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 3, "tre Test seminati visibili");

            ApriFiltriAvanzati();
            ApriFiltroMultiSelect("TestList_FiltroSeverita_Button", "Alta");
            ChiudiFiltriAvanzati();
            Wait.ForCondition(() => lista.RowCount() == 1, "filtro Severita' 'Alta': resta un Test");
            Assert.Equal(new[] { TestA }, IdOrdinati(lista));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltroSeverita_Alta_MostraSoloIlTestConSeveritaAlta), ex); }
    }

    [Fact]
    public void FiltroPriorita_Critical_MostraSoloIlTestCritico()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 3, "tre Test seminati visibili");

            ApriFiltriAvanzati();
            ApriFiltroMultiSelect("TestList_FiltroPriorita_Button", "Critical");
            ChiudiFiltriAvanzati();
            Wait.ForCondition(() => lista.RowCount() == 1, "filtro Priorita' 'Critical': resta un Test");
            Assert.Equal(new[] { TestA }, IdOrdinati(lista));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltroPriorita_Critical_MostraSoloIlTestCritico), ex); }
    }

    [Fact]
    public void FiltroStatoAnomalia_InVerifica_MostraSoloIlTestConNcAperta()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 3, "tre Test seminati visibili");

            ApriFiltriAvanzati();
            ApriFiltroMultiSelect("TestList_FiltroStatoAnomalia_Button", "In verifica");
            ChiudiFiltriAvanzati();
            // Il Test B ha una NC conclusa: il filtro considera solo le anomalie aperte.
            Wait.ForCondition(() => lista.RowCount() == 1, "filtro Stato anomalia 'In verifica': resta il Test con NC aperta");
            Assert.Equal(new[] { TestA }, IdOrdinati(lista));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltroStatoAnomalia_InVerifica_MostraSoloIlTestConNcAperta), ex); }
    }

    [Fact]
    public void FiltroVeicolo_UnVeicoloSpecifico_MostraSoloIlTestDiQuelVeicolo()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 3, "tre Test seminati visibili");

            ApriFiltroMultiSelect("TestList_FiltroVeicolo_Button", "Veicolo-FLT-B");
            Wait.ForCondition(() => lista.RowCount() == 1, "filtro Veicolo 'Veicolo-FLT-B': resta un Test");
            Assert.Equal(new[] { TestB }, IdOrdinati(lista));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltroVeicolo_UnVeicoloSpecifico_MostraSoloIlTestDiQuelVeicolo), ex); }
    }

    [Fact]
    public void FiltroFunzionalita_UnaFunzionalita_MostraSoloIlTestCheLaCopre()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 3, "tre Test seminati visibili");

            ApriFiltriAvanzati();
            ApriFiltroMultiSelect("TestList_FiltroFunzionalita_Button", "Funz-FLT-C");
            ChiudiFiltriAvanzati();
            Wait.ForCondition(() => lista.RowCount() == 1, "filtro Funzionalita' 'Funz-FLT-C': resta un Test");
            Assert.Equal(new[] { TestC }, IdOrdinati(lista));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltroFunzionalita_UnaFunzionalita_MostraSoloIlTestCheLaCopre), ex); }
    }

    [Fact]
    public void FiltroRequisito_UnRequisito_MostraSoloIlTestCheLoCollega()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 3, "tre Test seminati visibili");

            ApriFiltriAvanzati();
            ApriFiltroMultiSelect("TestList_FiltroRequisito_Button", "REQ-FLT-B");
            ChiudiFiltriAvanzati();
            Wait.ForCondition(() => lista.RowCount() == 1, "filtro Requisito 'REQ-FLT-B': resta un Test");
            Assert.Equal(new[] { TestB }, IdOrdinati(lista));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltroRequisito_UnRequisito_MostraSoloIlTestCheLoCollega), ex); }
    }

    [Fact]
    public void IntervalloDate_DigitatoNelTesto_ESceltoDalCalendario_FiltranoLeRighe()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 3, "tre Test seminati visibili");
            // I campi data stanno nei filtri avanzati; il conteggio si fa a pannello chiuso, perche' con il pannello aperto
            // la lista si virtualizza e mostra solo le righe visibili (stesso criterio degli altri test di questa classe).
            // Testo: il Test B (creato il 01/06/2024) entra con l'estremo inclusivo; la fine al 31/12/2024 lascia solo lui.
            ApriFiltriAvanzati();
            lista.ImpostaData("TestList_FiltroDataDa_DatePicker", "01/06/2024");
            ChiudiFiltriAvanzati();
            Wait.ForCondition(() => lista.RowCount() == 3, "dal 01/06/2024 restano tutti e tre (estremo incluso)");

            ApriFiltriAvanzati();
            lista.ImpostaData("TestList_FiltroDataA_DatePicker", "31/12/2024");
            ChiudiFiltriAvanzati();
            Wait.ForCondition(() => lista.RowCount() == 1, "fino al 31/12/2024 resta solo il Test B");
            Assert.Equal(new[] { TestB }, IdOrdinati(lista));

            // Calendario: il campo Da si apre su giugno 2024; il 15/06 esclude il Test B e non resta nessun Test.
            ApriFiltriAvanzati();
            lista.ScegliDataDalCalendario(App, "TestList_FiltroDataDa_DatePicker", 15);
            ChiudiFiltriAvanzati();
            Wait.ForCondition(() => lista.IsEmpty, "dal 15/06/2024 nessun Test resta nell'intervallo");

            // Campo svuotato: il vincolo Da scompare e resta solo "fino al 31/12/2024", cioe' il Test B.
            ApriFiltriAvanzati();
            lista.ImpostaData("TestList_FiltroDataDa_DatePicker", "");
            ChiudiFiltriAvanzati();
            Wait.ForCondition(() => lista.RowCount() == 1, "campo Da svuotato: resta solo il Test B fino al 31/12/2024");
            Assert.Equal(new[] { TestB }, IdOrdinati(lista));

            RimuoviFiltri();
            Wait.ForCondition(() => lista.RowCount() == 3, "Rimuovi filtri azzera le date digitate e scelte");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(IntervalloDate_DigitatoNelTesto_ESceltoDalCalendario_FiltranoLeRighe), ex); }
    }

    [Fact]
    public void IntervalloDate_DataDigitataNonValida_NonApplicaIlVincoloEMostraIlMessaggio()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 3, "tre Test seminati visibili");
            ApriFiltriAvanzati();
            lista.ImpostaData("TestList_FiltroDataA_DatePicker", "31/12/2024");
            ChiudiFiltriAvanzati();
            Wait.ForCondition(() => lista.RowCount() == 1, "fino al 31/12/2024 resta solo il Test B");

            // Digitata con la tastiera e confermata con Invio: data incompleta, nessun vincolo, campo segnalato (pannello aperto).
            ApriFiltriAvanzati();
            lista.DigitaData("TestList_FiltroDataA_DatePicker", "31/12/20");
            Wait.ForCondition(() => lista.MessaggioDataNonValidaVisibile, "messaggio 'data non valida' sotto i filtri");
            ChiudiFiltriAvanzati();
            Wait.ForCondition(() => lista.RowCount() == 3, "una data non valida non filtra: tornano tutti e tre");

            // Una data valida corregge il campo e toglie il messaggio.
            ApriFiltriAvanzati();
            lista.ImpostaData("TestList_FiltroDataA_DatePicker", "31/12/2024");
            Wait.ForCondition(() => !lista.MessaggioDataNonValidaVisibile, "la data corretta toglie il messaggio");
            ChiudiFiltriAvanzati();
            Wait.ForCondition(() => lista.RowCount() == 1, "la data corretta riapplica il filtro");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(IntervalloDate_DataDigitataNonValida_NonApplicaIlVincoloEMostraIlMessaggio), ex); }
    }

    [Fact]
    public void IntervalloDate_VistaSalvataConDataDaEA_EscludeITestFuoriPeriodo_ERimuoviFiltriRipristina()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 3, "tre Test seminati visibili");

            ElementFinders.WaitComboBoxById(Finestra(), "TestList_VisteSalvate_ComboBox").SelectByText(NomeVistaPeriodo);
            Wait.ForCondition(() => lista.RowCount() == 1, "l'intervallo 01/05/2024 - 01/01/2025 lascia solo il Test del 2024");
            Assert.Equal(new[] { TestB }, IdOrdinati(lista));

            RimuoviFiltri();
            Wait.ForCondition(() => lista.RowCount() == 3, "Rimuovi filtri azzera l'intervallo di date");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(IntervalloDate_VistaSalvataConDataDaEA_EscludeITestFuoriPeriodo_ERimuoviFiltriRipristina), ex); }
    }

    [Fact]
    public void FiltroPolarion_ConESenzaRiferimento_SeparaILeDueSerie()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 3, "tre Test seminati visibili");

            ElementFinders.WaitComboBoxById(Finestra(), "TestList_FiltroPolarion_ComboBox").SelectByText("Con riferimento Ticketing");
            Wait.ForCondition(() => lista.RowCount() == 1, "filtro 'Con riferimento Ticketing': resta il Test con NC collegata a Ticketing");
            Assert.Equal(new[] { TestA }, IdOrdinati(lista));

            ElementFinders.WaitComboBoxById(Finestra(), "TestList_FiltroPolarion_ComboBox").SelectByText("Senza riferimento Ticketing");
            Wait.ForCondition(() => lista.RowCount() == 2, "filtro 'Senza riferimento Ticketing': restano i due Test senza collegamento");
            Assert.Equal(new[] { TestB, TestC }, IdOrdinati(lista));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltroPolarion_ConESenzaRiferimento_SeparaILeDueSerie), ex); }
    }

    [Fact]
    public void FiltriSecondariInAnd_SeveritaBassaEVeicoloDelTestA_NonLasciaNessunTest()
    {
        try
        {
            var lista = Lista();
            Wait.ForCondition(() => lista.RowCount() == 3, "tre Test seminati visibili");

            ApriFiltriAvanzati();
            ApriFiltroMultiSelect("TestList_FiltroSeverita_Button", "Bassa");
            ApriFiltroMultiSelect("TestList_FiltroVeicolo_Button", "Veicolo-FLT-A");
            ChiudiFiltriAvanzati();
            // I filtri diversi si combinano in AND: nessun Test ha severita' Bassa E veicolo del Test A.
            Wait.ForCondition(() => lista.RowCount() == 0 && ElementFinders.ExistsById(Finestra(), "TestList_Empty_Text"),
                "nessun Test soddisfa severita' Bassa e veicolo del Test A");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltriSecondariInAnd_SeveritaBassaEVeicoloDelTestA_NonLasciaNessunTest), ex); }
    }
}
