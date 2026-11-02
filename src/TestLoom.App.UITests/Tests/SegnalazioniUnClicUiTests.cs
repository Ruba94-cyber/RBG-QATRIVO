using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Segnalazioni nel Test "a un clic" (feedback 2026-10-10), sull'app reale: il pulsante primario della riga
/// dichiara cosa succedera' ("Crea NC" oppure "Collega a NC-xxxx") e un clic lo esegue. Il workspace contiene gia' una
/// NC aperta di un altro Test con la stessa componente, la stessa Funzionalita' e lo stesso problema: il primo test deve
/// crearne una nuova (NC 1 -> 2), il secondo deve collegare quella esistente senza crearne altre (NC resta 1).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class SegnalazioniUnClicUiTests : UiTestBase
{
    private const string FunzionalitaQa = "Lettura guasto QA";
    private const string ProblemaQa = "Risposta errata QA a un clic";
    private const string TestAltroId = "TC-QA-SIMILE";

    public SegnalazioniUnClicUiTests() : base(CreaWorkspaceConNcSimile()) { }

    private static TestWorkspace CreaWorkspaceConNcSimile()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(SegnalazioniUnClicUiTests), funzionalitaNelTemplate: true);
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        var condizione = new Condizione
        {
            Esito = Esito.Conforme, Componente = new EcuSelection(new[] { TestWorkspace.DefaultEcuName }),
            Funzionalita = FunzionalitaQa, Nota = "Operazione di un altro Test"
        };
        var esecuzione = new Esecuzione { Iterazione = 1, Data = DateTime.Now.AddHours(-3), Operatore = "UITests" };
        esecuzione.Condizioni.Add(condizione);
        esecuzione.SincronizzaEsito();
        state.TestCases.Save(new TestCase
        {
            Id = TestAltroId, DescrizioneBreve = "Test con NC simile - QA",
            Componente = { TestWorkspace.DefaultEcuName }, Veicolo = TestWorkspace.DefaultVehicleName,
            Veicoli = { TestWorkspace.DefaultVehicleName }, Funzionalita = { FunzionalitaQa }, Assegnatario = "UITests",
            StatoLavoro = StatoLavoroTest.InCorso, DataCreazione = DateTime.Now.AddHours(-3),
            UltimaModifica = DateTime.Now.AddHours(-3), ModificatoDa = "UITests",
            Esecuzioni = new List<Esecuzione> { esecuzione }
        }, isNew: true);
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-0001", TestCaseId = TestAltroId, EsecuzioneIterazione = 1, DataApertura = DateTime.Now.AddDays(-1),
            Owner = "UITests", Severita = SeveritaAnomalia.Medium, StatoWorkflow = StatoAnomalia.Confermata,
            Descrizione = NonConformitaRulesService.TitoloDaOperazione(FunzionalitaQa, TestWorkspace.DefaultEcuName, ProblemaQa, "Operazione 1")
        });
        return workspace;
    }

    [Fact]
    public void EsitoNonConforme_RigaMostraCreaNc_UnClicPiuConfermaCreaLaNcCollegataAlTest()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);

            var editor = shell.GoToTestList().ClickNew();
            editor.ChooseTemplate();
            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            editor.SetDescrizioneBreve("Segnalazione a un clic, nessuna NC simile - QA");
            editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);
            editor.GoToTab(2);
            editor.SetEsitoCondizione(0, "Non Conforme");
            editor.SetDescrizioneProblema(0, "Problema diverso osservato - QA");

            Wait.ForCondition(() => editor.HaSegnalazioniDaClassificare,
                "un'operazione Non Conforme genera una segnalazione da classificare nel pannello Anomalie del Test");
            Assert.Equal(1, editor.NumeroSegnalazioniDaClassificare());
            Assert.Equal("Crea NC", editor.TestoPrimariaSegnalazione(0));

            editor.SetSeveritaSegnalazione(0, "Media");
            editor.ClickPrimariaSegnalazione(0);
            var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            Assert.False(string.IsNullOrWhiteSpace(conferma.Message));
            conferma.ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura del dialog di conferma");
            Wait.ForCondition(() => editor.NumeroSegnalazioniDaClassificare() == 0, "la segnalazione risulta classificata e la riga sparisce");

            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();

            // Il seme contiene gia' NC-0001: la nuova NC deve esserci in aggiunta, collegata a questo Test.
            var anomalie = shell.GoToAnomalie();
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 300) == 2,
                "la NC creata dal pulsante primario compare in Anomalie/CR accanto a quella seminata");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(EsitoNonConforme_RigaMostraCreaNc_UnClicPiuConfermaCreaLaNcCollegataAlTest), ex);
        }
    }

    [Fact]
    public void EsitoNonConformeSimileAUnaNcAperta_RigaMostraCollegaA_UnClicCollegaSenzaNuovaNc()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);

            var editor = shell.GoToTestList().ClickNew();
            editor.ChooseTemplate();
            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            editor.SetDescrizioneBreve("Segnalazione a un clic, NC simile esistente - QA");
            editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);
            editor.AddFunzionalitaRapida(FunzionalitaQa);
            editor.GoToTab(2);
            editor.SetEsitoCondizione(0, "Non Conforme");
            editor.SetDescrizioneProblema(0, ProblemaQa);

            Wait.ForCondition(() => editor.HaSegnalazioniDaClassificare,
                "un'operazione Non Conforme genera una segnalazione da classificare nel pannello Anomalie del Test");
            // Il problema e' stato scritto DOPO l'esito: il suggerimento si aggiorna mentre si scrive, senza ricostruire la riga.
            Wait.ForCondition(() => editor.TestoPrimariaSegnalazione(0) == "Collega a NC-0001",
                "scritto il problema, il pulsante primario della riga diventa 'Collega a NC-0001'");
            Assert.Equal("Collega a NC-0001", editor.TestoPrimariaSegnalazione(0));

            // Collegare la NC esistente non chiede conferma: il clic basta.
            editor.ClickPrimariaSegnalazione(0);
            Wait.ForCondition(() => editor.NumeroSegnalazioniDaClassificare() == 0, "la segnalazione risulta collegata e la riga sparisce");

            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();

            var anomalie = shell.GoToAnomalie();
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 300) == 1,
                "nessuna NC nuova: la segnalazione e' stata collegata a NC-0001 gia' aperta");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(EsitoNonConformeSimileAUnaNcAperta_RigaMostraCollegaA_UnClicCollegaSenzaNuovaNc), ex);
        }
    }
}
