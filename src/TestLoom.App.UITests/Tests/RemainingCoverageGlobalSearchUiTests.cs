using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using static TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Ctrl+K (ricerca globale) per i domini non ancora coperti: Non Conformita' (Anomalie), Change Request e Segnalazioni
/// ancora da classificare. Ogni test seleziona un risultato con il marcatore del suo testo e verifica la destinazione:
/// la pagina Anomalie per una NC, l'editor della CR per una Change Request, l'editor del Test con il pannello delle
/// segnalazioni da classificare per una Segnalazione. Il primo caso componente/Test e' gia' coperto da
/// ShellHomeSettingsCoverageShellUiTests.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Navigation")]
public sealed class RemainingCoverageGlobalSearchUiTests : UiTestBase
{
    private const string NcId = "NC-GS-0001";
    private const string NcTesto = "Anomalia ricerca globale QA";
    private const string CrId = "CR-GS-0001";
    private const string CrTitolo = "CR ricerca globale QA";
    private const string TestSegnalazione = "TC-GS-0002";
    private const string ProblemaSegnalazione = "Problema segnalazione ricerca QA";

    public RemainingCoverageGlobalSearchUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(RemainingCoverageGlobalSearchUiTests));
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);

        state.NonConformita.Save(new NonConformita
        {
            Id = NcId,
            TestCaseId = "TC-GS-0001",
            Descrizione = NcTesto,
            Owner = TestWorkspace.DefaultOperatorName,
            StatoWorkflow = StatoAnomalia.InVerifica,
            Severita = SeveritaAnomalia.Medium,
            Priorita = PrioritaOperativa.Normal,
            DataApertura = DateTime.Now,
        });

        var templates = state.Master.TestTemplate.Load();
        var templateCr = new TestTemplate { Nome = "CR Template GS", IsChangeRequest = true, ModificatoDa = "UITests" };
        templates.Add(templateCr);
        state.Master.TestTemplate.Save(templates);
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = CrId,
            Titolo = CrTitolo,
            Descrizione = "Descrizione della CR cercata con Ctrl+K",
            TemplateUid = templateCr.Uid,
            EcuImpattate = new() { TestWorkspace.DefaultEcuName },
            Responsabile = "",
            Stato = StatoChangeRequest.Aperta,
        });

        // Segnalazione ancora da classificare: Test con una condizione Non Conforme collegata alla segnalazione.
        var condizione = new Condizione
        {
            Esito = Esito.NonConforme,
            Componente = new EcuSelection(new[] { TestWorkspace.DefaultEcuName }),
            Nota = "Operazione con segnalazione da classificare",
            DescrizioneProblema = ProblemaSegnalazione,
            Funzionalita = "Funz-GS",
        };
        var segnalazione = new SegnalazioneDaClassificare
        {
            TestId = TestSegnalazione,
            CondizioneUid = condizione.Uid,
            EsecuzioneIterazione = 1,
            Stato = StatoSegnalazione.DaClassificare,
        };
        condizione.SegnalazioneUid = segnalazione.Uid;
        var esecuzione = new Esecuzione
        {
            Iterazione = 1,
            Data = DateTime.Now.AddHours(-3),
            Operatore = "UITests",
            Matricola = "VINQA000000000003",
        };
        esecuzione.Condizioni.Add(condizione);
        esecuzione.SincronizzaEsito();
        state.TestCases.Save(new TestCase
        {
            Id = TestSegnalazione,
            DescrizioneBreve = "Test con segnalazione da classificare - QA",
            Componente = { TestWorkspace.DefaultEcuName },
            Veicolo = TestWorkspace.DefaultVehicleName,
            Veicoli = { TestWorkspace.DefaultVehicleName },
            Assegnatario = "UITests",
            StatoLavoro = StatoLavoroTest.InCorso,
            DataCreazione = DateTime.Now.AddHours(-3),
            UltimaModifica = DateTime.Now.AddHours(-3),
            ModificatoDa = "UITests",
            Esecuzioni = new List<Esecuzione> { esecuzione },
        }, isNew: true);
        state.Segnalazioni.Save(segnalazione);

        return workspace;
    }

    private Window Finestra() => App.WaitForMainWindow();

    /// <summary>Apre Ctrl+K, digita il testo e attende il risultato che lo contiene.</summary>
    private AutomationElement CercaERiporta(Window window, string testo)
    {
        new MainShellPage(window).DismissNotificationsIfShown();
        ApriRicercaGlobaleConCtrlK(window);
        ElementFinders.WaitTextBoxById(window, "Main_RicercaGlobale_TextBox").SetText(testo);
        Wait.ForCondition(() => RisultatoRicerca(window, testo) != null,
            $"risultato '{testo}' nella ricerca globale", TimeSpan.FromSeconds(20));
        return RisultatoRicerca(window, testo)!;
    }

    [Fact]
    public void CtrlK_CercaUnaNonConformita_ClickApreLaPaginaAnomalie()
    {
        try
        {
            var window = Finestra();
            var risultato = CercaERiporta(window, NcTesto);

            // Il risultato mostra l'Id della NC come titolo e la descrizione come sottotitolo.
            Assert.True(risultato.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
                .Any(t => t.Name == NcId), "il risultato riporta l'Id della Non Conformita'");

            risultato.AsButton().SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"),
                "la pagina Anomalie si apre dal risultato della NC", TimeSpan.FromSeconds(15));
            Wait.ForCondition(() => !VisibileInUia(ElementFinders.TryFindById(window, "Main_RicercaGlobale_TextBox")),
                "la ricerca globale si chiude dopo il click sul risultato", TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CtrlK_CercaUnaNonConformita_ClickApreLaPaginaAnomalie), ex); }
    }

    [Fact]
    public void CtrlK_CercaUnaChangeRequest_ClickApreLEditorDellaCr()
    {
        try
        {
            var window = Finestra();
            var risultato = CercaERiporta(window, CrTitolo);

            Assert.True(risultato.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
                .Any(t => t.Name == "Change Request"), "il risultato e' etichettato come Change Request");

            risultato.AsButton().SafeInvoke();
            Wait.ForCondition(() => ElementFinders.TryFindById(window, "Cr_Title_Text")?.Name == CrId,
                "l'editor della Change Request si apre dal risultato", TimeSpan.FromSeconds(15));
            Assert.Equal(CrTitolo, ElementFinders.WaitTextBoxById(window, "Cr_Titolo_TextBox").Text);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CtrlK_CercaUnaChangeRequest_ClickApreLEditorDellaCr), ex); }
    }

    [Fact]
    public void CtrlK_CercaUnaSegnalazioneDaClassificare_ClickApreIlTestConLaSegnalazione()
    {
        try
        {
            var window = Finestra();
            var risultato = CercaERiporta(window, ProblemaSegnalazione);

            Assert.True(risultato.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
                .Any(t => t.Name == "Segnalazione"), "il risultato e' etichettato come Segnalazione");

            risultato.AsButton().SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Editor_SegnalazioniDaClassificare_Text"),
                "l'editor del Test si apre con il pannello delle segnalazioni da classificare", TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CtrlK_CercaUnaSegnalazioneDaClassificare_ClickApreIlTestConLaSegnalazione), ex); }
    }
}
