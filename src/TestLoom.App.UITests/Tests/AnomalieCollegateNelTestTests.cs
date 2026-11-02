using System.Linq;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Gestione delle anomalie collegate direttamente dalla pagina Test (punto 3 della 6.5.0): elenco con
/// stato/priorita/assegnatario, apertura del dialogo UNICO di dettaglio (lo stesso della pagina Anomalie),
/// modifica dei campi e chiusura con motivazione obbligatoria. Il caso d'uso e' quello delle segnalazioni dalla
/// rete su cui sono stati fatti test: si aggiorna/chiude l'anomalia dal test, poi si conclude il test.
/// I test sono seminati via repository (come NonConformitaWorkflowTests) per concentrarsi sul pannello.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class AnomalieCollegateNelTestTests : UiTestBase
{
    private const string TestId = "TC-QA-ANOM";

    public AnomalieCollegateNelTestTests() : base(CreaWorkspace(2)) { }

    /// <summary>Un Test con <paramref name="numeroAnomalie"/> operazioni Non Conformi, ciascuna con segnalazione
    /// gia' classificata e anomalia collegata (Severita Media, Owner "Mario"). NC-QA-0001 e' la piu' vecchia:
    /// l'elenco le ordina per DataApertura decrescente, quindi la prima riga e' l'ultima seminata.</summary>
    internal static TestWorkspace CreaWorkspace(int numeroAnomalie, bool ownerUnivoco = false)
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(AnomalieCollegateNelTestTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        var condizioni = new List<Condizione>();
        var segnalazioni = new List<SegnalazioneDaClassificare>();
        var anomalie = new List<NonConformita>();
        var baseData = DateTime.Now.AddHours(-2);
        for (var i = 1; i <= numeroAnomalie; i++)
        {
            var condizione = new Condizione
            {
                Esito = Esito.NonConforme, Componente = new EcuSelection(new[] { TestWorkspace.DefaultEcuName }),
                Nota = $"Operazione {i}", DescrizioneProblema = $"Problema {i}"
            };
            var segnalazione = new SegnalazioneDaClassificare
            {
                TestId = TestId, CondizioneUid = condizione.Uid, EsecuzioneIterazione = 1,
                Stato = StatoSegnalazione.Classificata, AnomaliaUid = Guid.NewGuid()
            };
            condizione.SegnalazioneUid = segnalazione.Uid;
            condizioni.Add(condizione);
            segnalazioni.Add(segnalazione);
            anomalie.Add(new NonConformita
            {
                Uid = segnalazione.AnomaliaUid!.Value, Id = $"NC-QA-{i:0000}", TestCaseId = TestId,
                EsecuzioneIterazione = 1, DataApertura = baseData.AddMinutes(i), Descrizione = $"Segnalazione {i}",
                Severita = SeveritaAnomalia.Medium, Priorita = PrioritaOperativa.Normal, Owner = ownerUnivoco ? $"Owner-{i:0000}" : "Mario",
                Destinatario = DestinatarioSegnalazione.Supplier, CausaProbabile = CausaProbabileUdt.Componente, StatoDettaglio = StatoDettaglioNc.InFix,
                StatoWorkflow = StatoAnomalia.Confermata,
                ProveCollegate = new() { new RiferimentoProva { TestId = TestId, EsecuzioneIterazione = 1, CondizioneUid = condizione.Uid } }
            });
        }

        var esecuzione = new Esecuzione
        {
            Iterazione = 1, Data = DateTime.Now.AddHours(-3), Operatore = TestWorkspace.DefaultOperatorName, Matricola = "VINQA000000000001"
        };
        esecuzione.Condizioni.AddRange(condizioni);
        esecuzione.SincronizzaEsito();
        state.TestCases.Save(new TestCase
        {
            Id = TestId, DescrizioneBreve = "Test con anomalie collegate - QA",
            Componente = { TestWorkspace.DefaultEcuName }, Veicolo = TestWorkspace.DefaultVehicleName,
            Veicoli = { TestWorkspace.DefaultVehicleName }, Assegnatario = TestWorkspace.DefaultOperatorName,
            StatoLavoro = StatoLavoroTest.InCorso, DataCreazione = DateTime.Now.AddHours(-3),
            UltimaModifica = DateTime.Now.AddHours(-3), ModificatoDa = "UITests",
            Esecuzioni = new List<Esecuzione> { esecuzione }
        }, isNew: true);
        foreach (var s in segnalazioni) state.Segnalazioni.Save(s);
        foreach (var nc in anomalie) state.NonConformita.Save(nc);
        return workspace;
    }

    private TestAnomaliePanel ApriTest(bool inModifica = false)
    {
        var window = App.WaitForMainWindow();
        var shell = new MainShellPage(window);
        var editor = shell.GoToTestList().OpenOnlyRow();
        // Il pulsante "Modifica stato" funziona anche in sola lettura; la modifica del Test serve solo al prompt di avanzamento.
        if (inModifica) editor.EnterEditMode();
        Massimizza(window);
        var pannello = new TestAnomaliePanel(window);
        Wait.ForCondition(() => pannello.NumeroRigheRealizzate > 0, "il pannello mostra le anomalie collegate al test");
        pannello.PortaInVista();
        return pannello;
    }

    /// <summary>Finestra massimizzata: righe realizzate e posizioni indipendenti dalla dimensione di partenza.</summary>
    internal static void Massimizza(FlaUI.Core.AutomationElements.Window window)
    {
        try { window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized); }
        catch (Exception) { /* best effort */ }
    }

    internal void ConfermaEAttendiChiusuraDialogo()
    {
        var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
        conferma.ConfirmYes();
        Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura del dialog di conferma");
    }

    [Fact]
    public void Elenco_RigheCompatteInSolaLettura_UnSoloContenitore_SenzaEditingInline()
    {
        try
        {
            var pannello = ApriTest();

            pannello.AttendiPrimaRiga("NC-QA-0002", "l'anomalia piu' recente e' la prima dell'elenco del test");
            Wait.ForCondition(() => pannello.NumeroRigheRealizzate == 2, "entrambe le righe compatte sono realizzate");
            Assert.Equal(1, pannello.NumeroContenitoriElenco());
            Wait.ForCondition(() => pannello.TryOwner(0) == "Mario", "assegnatario della prima riga");
            Assert.False(string.IsNullOrWhiteSpace(pannello.TryStato(0)));
            Assert.False(string.IsNullOrWhiteSpace(pannello.TryPriorita(0)));
            // Niente piu' editing inline per riga: combo/TextBox di stato, owner e motivazione non esistono nell'elenco.
            var window = App.WaitForMainWindow();
            Assert.False(ElementFinders.ExistsById(window, "Test_Anomalia_Stato_Combo"));
            Assert.False(ElementFinders.ExistsById(window, "Test_Anomalia_Owner_TextBox"));
            Assert.False(ElementFinders.ExistsById(window, "Test_Anomalia_MotivoChiusura_TextBox"));
            Assert.False(ElementFinders.ExistsById(window, "Test_Anomalia_Chiudi_Button"));
            // "Genera pacchetto TICKET" resta raggiungibile (un solo pulsante nel pannello, non per riga).
            Assert.True(ElementFinders.ExistsById(window, "Test_Anomalie_GeneraPacchettoEsta_Button"));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Elenco_RigheCompatteInSolaLettura_UnSoloContenitore_SenzaEditingInline), ex);
        }
    }

    [Fact]
    public void ModificaStato_DalTest_ApreLoStessoPopup_ModificaAssegnatarioESalva()
    {
        try
        {
            var pannello = ApriTest();
            pannello.AttendiPrimaRiga("NC-QA-0002", "anomalie collegate visibili");

            var dettaglio = pannello.ApriDettaglioDi(App, "NC-QA-0002");
            Assert.Equal("Mario", dettaglio.Owner);
            dettaglio.ImpostaOwner("Giulia");
            dettaglio.Salva();
            Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId), "il pop-up si chiude dopo Salva");

            // Il test viene ricaricato nell'editor: la riga mostra il nuovo assegnatario.
            Wait.ForCondition(() => pannello.IdVisibili().FirstOrDefault() == "NC-QA-0002" && pannello.TryOwner(0) == "Giulia",
                "l'elenco del test riflette l'assegnatario salvato dal pop-up");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ModificaStato_DalTest_ApreLoStessoPopup_ModificaAssegnatarioESalva), ex);
        }
    }

    [Fact]
    public void ChiusuraDalPopup_SenzaMotivazione_Rifiutata_ConMotivazione_ChiudeSoloQuellaAnomalia()
    {
        try
        {
            var pannello = ApriTest();
            pannello.AttendiPrimaRiga("NC-QA-0002", "anomalie collegate visibili");

            var dettaglio = pannello.ApriDettaglioDi(App, "NC-QA-0002");
            dettaglio.Chiudi(); // nessuna motivazione: stesso controllo della pagina Anomalie, nessuna scorciatoia
            Wait.ForCondition(() => TestAnomaliePanel.ErroreDialogo(dettaglio).Length > 0,
                "senza motivazione la chiusura e' rifiutata con un errore nel pop-up");
            Assert.False(App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId));

            dettaglio.ImpostaMotivoChiusura("Segnalazione dalla rete verificata, nessun altro test necessario");
            dettaglio.Chiudi();
            ConfermaEAttendiChiusuraDialogo();
            Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId), "il pop-up si chiude dopo la chiusura");

            pannello.AttendiPrimaRiga("NC-QA-0001", "resta aperta solo l'altra anomalia del test");
            Assert.False(pannello.NessunaAperta);

            var ultimo = pannello.ApriDettaglioDi(App, "NC-QA-0001");
            ultimo.ImpostaMotivoChiusura("Verificata anche la seconda segnalazione");
            ultimo.Chiudi();
            ConfermaEAttendiChiusuraDialogo();
            Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId), "il pop-up si chiude dopo la seconda chiusura");
            Wait.ForCondition(() => pannello.NessunaAperta && pannello.NumeroRigheRealizzate == 0,
                "nessuna anomalia aperta rimasta nel test");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ChiusuraDalPopup_SenzaMotivazione_Rifiutata_ConMotivazione_ChiudeSoloQuellaAnomalia), ex);
        }
    }

    [Fact]
    public void ChiusuraDellUltimaAnomalia_InModifica_ProponeAvanzamentoAConcluso()
    {
        try
        {
            var pannello = ApriTest(inModifica: true);
            pannello.AttendiPrimaRiga("NC-QA-0002", "anomalie collegate visibili");

            var primo = pannello.ApriDettaglioDi(App, "NC-QA-0002");
            primo.ImpostaMotivoChiusura("Prima segnalazione verificata");
            primo.Chiudi();
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId), "chiusura del primo pop-up");
            System.Threading.Thread.Sleep(1500);
            if (!App.IsWindowClosedByContent("Confirm_Yes_Button"))
                throw new Exception("Conferma inattesa dopo la prima chiusura: " + new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).Message);
            // Non e' l'ultima: nessuna proposta di avanzamento (il prossimo dialogo e' quello del secondo pop-up).
            pannello.AttendiPrimaRiga("NC-QA-0001", "resta una sola anomalia aperta");

            var ultimo = pannello.ApriDettaglioDi(App, "NC-QA-0001");
            ultimo.ImpostaMotivoChiusura("Ultima segnalazione verificata");
            ultimo.Chiudi();
            // Conferma della chiusura: subito dopo si chiude il pop-up e compare la conferma di avanzamento (stesso id),
            // quindi qui non si attende la scomparsa del dialogo di conferma ma quella del pop-up.
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId), "chiusura del pop-up dell'ultima anomalia");

            // Ultima anomalia chiusa: stessa proposta di avanzamento a Concluso che esisteva con la chiusura inline.
            var avanza = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            Assert.False(string.IsNullOrWhiteSpace(avanza.Message));
            avanza.ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura della conferma di avanzamento");
            Wait.ForCondition(() => pannello.NessunaAperta, "nessuna anomalia aperta dopo l'avanzamento");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ChiusuraDellUltimaAnomalia_InModifica_ProponeAvanzamentoAConcluso), ex);
        }
    }

    [Fact]
    public void Tastiera_TabDalPulsanteModificaStato_ProseguePulsanteDellaRigaSuccessiva_ShiftTabTorna()
    {
        try
        {
            var pannello = ApriTest();
            pannello.AttendiPrimaRiga("NC-QA-0002", "anomalie collegate visibili");
            Wait.ForCondition(() => pannello.NumeroRigheRealizzate == 2, "entrambe le righe realizzate");

            pannello.FocusPulsante("NC-QA-0002");
            Wait.ForCondition(() => pannello.IdConFocus() == "Test_Anomalia_Dettaglio_Button" && pannello.IdRigaConFocus() == "NC-QA-0002",
                "focus sul pulsante della prima riga");

            // TabNavigation=Continue: il Tab raggiunge il pulsante della riga successiva (al massimo qualche stop intermedio).
            var passi = 0;
            while (!(pannello.IdConFocus() == "Test_Anomalia_Dettaglio_Button" && pannello.IdRigaConFocus() == "NC-QA-0001") && passi++ < 6)
                pannello.Tab();
            Assert.Equal("NC-QA-0001", pannello.IdRigaConFocus());
            Assert.Equal("Test_Anomalia_Dettaglio_Button", pannello.IdConFocus());

            var indietro = 0;
            while (!(pannello.IdConFocus() == "Test_Anomalia_Dettaglio_Button" && pannello.IdRigaConFocus() == "NC-QA-0002") && indietro++ < 6)
                pannello.Tab(indietro: true);
            Assert.Equal("NC-QA-0002", pannello.IdRigaConFocus());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Tastiera_TabDalPulsanteModificaStato_ProseguePulsanteDellaRigaSuccessiva_ShiftTabTorna), ex);
        }
    }
}
