using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Copertura delle segnalazioni generate da un'operazione Non Conforme nel Test: correzione in Conforme prima del
/// Salva (fix ddceda9, riproduce l'effetto sul disco), esclusione con motivazione obbligatoria, annulla esclusione
/// fino alla nuova anomalia, collegamento a un'anomalia aperta gia' esistente. Il workspace contiene una NC aperta
/// seminata, candidata al collegamento; i conteggi di segnalazioni e anomalie su disco sono verificati ad ogni test.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class TestSegnalazioniCoverageUiTests : UiTestBase
{
    private const string NcAperta = "NC-QA-APERTA-01";

    public TestSegnalazioniCoverageUiTests() : base(CreaWorkspaceConNcAperta()) { }

    private static TestWorkspace CreaWorkspaceConNcAperta()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(TestSegnalazioniCoverageUiTests));
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        TestCoverageSeed.SeedNonConformitaAperta(state, NcAperta, "Anomalia aperta seminata per il collegamento - QA");
        return workspace;
    }

    /// <summary>Crea e salva un Test con un'operazione, lo riapre in modifica e porta l'operazione a Non Conforme con
    /// problema osservato: il pannello Anomalie deve mostrare la segnalazione da classificare.</summary>
    private TestEditorPage TestConNonConformita(string descrizione)
    {
        var lista = CreateTestCaseFlow.Run(App, new MainShellPage(App.WaitForMainWindow()), descrizione, TestWorkspace.DefaultEcuName);
        var editor = lista.OpenOnlyRow();
        editor.EnterEditMode();
        editor.SetEsitoCondizione(0, "Non Conforme");
        editor.SetDescrizioneProblema(0, "Problema osservato durante l'esecuzione - QA");
        Wait.ForCondition(() => editor.HaSegnalazioniDaClassificare, "segnalazione da classificare nel pannello Anomalie");
        return editor;
    }

    private TestListPage RicaricaLista() => new(App.WaitForMainWindow(), App.WaitForMainWindow());

    /// <summary>BugAperto (asserzione finale): dopo aver riportato a Conforme un'operazione Non Conforme, prima del Salva la
    /// segnalazione nata in sessione resta nel pannello Anomalie con "Nuova anomalia" (la scarto avviene solo al Salva).
    /// Il Salva riesce, su disco non resta alcuna segnalazione o anomalia e alla riapertura il pannello e' vuoto: queste
    /// verifiche passano. Atteso: il pannello si svuota gia' al ritorno a Conforme.</summary>
    [Fact]
    [Trait("Category", "BugAperto")]
    public void NonConformeCorrettaInConforme_PrimaDelSalva_NonLasciaSegnalazioniSulDisco()
    {
        try
        {
            var editor = TestConNonConformita("QA fix - NC poi Conforme");
            var segnalazioniPrima = TestCoverageSeed.FileSegnalazioni(Workspace).Count;
            var ncPrima = TestCoverageSeed.FileNonConformita(Workspace).Count;

            editor.SetEsitoCondizione(0, "Conforme");
            // Il cambio a Conforme apre il dialogo modale "Cambio esito" (la Non Conformita' aveva un problema scritto): il
            // pannello deve svuotarsi GIA' mentre il dialogo e' aperto, cioe' prima della risposta dell'utente. Si misura lo
            // stato in quel momento, senza attesa di ripiego; l'asserzione arriva dopo, con il salvataggio reale.
            var pannelloPulitoSubitoDopoIlCambio = Wait.TryForCondition(() => !editor.HaSegnalazioniDaClassificare, TimeSpan.FromSeconds(15));
            // "Rimuovi" (Si): la motivazione della Non Conformita' non serve piu'. Il Salva parte solo dopo la risposta.
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura del dialogo Cambio esito");

            editor.Save();
            var salvato = Wait.TryForCondition(() => ElementFinders.ExistsById(App.WaitForMainWindow(), "TestList_Title_Text"), TimeSpan.FromSeconds(20));
            Assert.True(salvato, "Con l'esito tornato Conforme il salvataggio deve completarsi (nessuna segnalazione bloccante).");

            Assert.Equal(segnalazioniPrima, TestCoverageSeed.FileSegnalazioni(Workspace).Count);
            Assert.Equal(ncPrima, TestCoverageSeed.FileNonConformita(Workspace).Count);
            var condizione = TestCoverageSeed.LeggiTestCase(TestCoverageSeed.FileTestCase(Workspace).Single()).Esecuzioni[^1].Condizioni.Single();
            Assert.Equal(Esito.Conforme, condizione.Esito);
            Assert.Null(condizione.SegnalazioneUid);

            var riaperto = RicaricaLista().OpenOnlyRow();
            Assert.False(riaperto.HaSegnalazioniDaClassificare, "alla riapertura il pannello Anomalie non mostra segnalazioni");
            Assert.True(pannelloPulitoSubitoDopoIlCambio,
                "Dopo il ritorno a Conforme, con il dialogo 'Cambio esito' ancora aperto, la segnalazione nata in sessione deve sparire dal pannello Anomalie.");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(NonConformeCorrettaInConforme_PrimaDelSalva_NonLasciaSegnalazioniSulDisco), ex); }
    }

    /// <summary>BugAperto: dopo "Escludi" con motivazione, Salva compare una seconda segnalazione "Da classificare" sulla
    /// stessa operazione (CaricaPannelloAnomalie con creaMancanti: ENuovoPassaggioANok tratta una segnalazione Esclusa
    /// nata nella sessione come passaggio chiuso) e il salvataggio e' rifiutato. Atteso: salvataggio con una sola
    /// segnalazione Esclusa su disco e nessuna anomalia creata.</summary>
    [Fact]
    [Trait("Category", "BugAperto")]
    public void Escludi_ConMotivazione_SalvaLaSegnalazioneEsclusaSenzaCreareAnomalie()
    {
        try
        {
            var editor = TestConNonConformita("QA escludi - con motivazione");
            const string motivo = "Segnalazione non pertinente a questo Test - QA";
            var ncPrima = TestCoverageSeed.FileNonConformita(Workspace).Count;

            editor.EscludiSegnalazione(0, motivo);
            Wait.ForCondition(() => editor.SegnalazioneEsclusa(0), "la segnalazione compare esclusa, con il pulsante di annullamento");
            editor.Save();
            Assert.Equal(1, RicaricaLista().RowCount());

            var seg = TestCoverageSeed.LeggiSegnalazione(Assert.Single(TestCoverageSeed.FileSegnalazioni(Workspace)));
            Assert.Equal(StatoSegnalazione.Esclusa, seg.Stato);
            Assert.Equal(motivo, seg.MotivazioneEsclusione);
            Assert.Null(seg.AnomaliaUid);
            Assert.Equal(ncPrima, TestCoverageSeed.FileNonConformita(Workspace).Count);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Escludi_ConMotivazione_SalvaLaSegnalazioneEsclusaSenzaCreareAnomalie), ex); }
    }

    [Fact]
    public void Escludi_SenzaMotivazione_MostraErroreELaSegnalazioneRestaDaClassificare()
    {
        try
        {
            var editor = TestConNonConformita("QA escludi - senza motivazione");

            editor.EscludiSegnalazione(0, "");
            Wait.ForCondition(() => editor.HasValidationErrors, "errore di validazione: la motivazione e' obbligatoria");

            Assert.False(editor.SegnalazioneEsclusa(0), "senza motivazione la segnalazione non viene esclusa");
            Assert.Equal(1, editor.NumeroSegnalazioniDaClassificare());
            Assert.Empty(TestCoverageSeed.FileSegnalazioni(Workspace));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Escludi_SenzaMotivazione_MostraErroreELaSegnalazioneRestaDaClassificare), ex); }
    }

    [Fact]
    public void AnnullaEsclusione_RiportaLaSegnalazioneDaClassificareEPuoDiventareNuovaAnomalia()
    {
        try
        {
            var editor = TestConNonConformita("QA esclusione - annulla e classifica");
            var ncPrima = TestCoverageSeed.FileNonConformita(Workspace).Count;

            editor.EscludiSegnalazione(0, "Motivo provvisorio - QA");
            Wait.ForCondition(() => editor.SegnalazioneEsclusa(0), "segnalazione esclusa");

            editor.AnnullaEsclusioneSegnalazione(0);
            Wait.ForCondition(() => !editor.SegnalazioneEsclusa(0) && editor.NumeroSegnalazioniDaClassificare() == 1,
                "la segnalazione torna da classificare dopo 'Annulla esclusione'");

            editor.SetSeveritaSegnalazione(0, "Media");
            editor.CreaAnomaliaDaSegnalazione(0);
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura conferma creazione anomalia");

            editor.Save();
            Assert.Equal(1, RicaricaLista().RowCount());

            var ncDopo = TestCoverageSeed.FileNonConformita(Workspace).Select(TestCoverageSeed.LeggiNonConformita).ToList();
            Assert.Equal(ncPrima + 1, ncDopo.Count);
            var seg = TestCoverageSeed.LeggiSegnalazione(Assert.Single(TestCoverageSeed.FileSegnalazioni(Workspace)));
            Assert.Equal(StatoSegnalazione.Classificata, seg.Stato);
            Assert.NotNull(seg.AnomaliaUid);
            Assert.Contains(ncDopo, nc => nc.Uid == seg.AnomaliaUid);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(AnnullaEsclusione_RiportaLaSegnalazioneDaClassificareEPuoDiventareNuovaAnomalia), ex); }
    }

    /// <summary>BugAperto: "Collega" a un'anomalia aperta esistente: la segnalazione risulta classificata, ma dopo il Salva
    /// l'anomalia su disco non riporta il riferimento al Test (ProveCollegate vuoto). Il pannello Salva ricarica
    /// l'anomalia dal repository (TutteLeAnomalieCorrenti) invece della copia modificata, che non e' nel pannello finche'
    /// il collegamento non e' persistito. Atteso: ProveCollegate contiene il Test e la segnalazione punta all'anomalia.</summary>
    [Fact]
    [Trait("Category", "BugAperto")]
    public void Collega_AdAnomaliaApertaEsistente_NonCreaUnaNuovaNcEAggiornaIlRiferimento()
    {
        try
        {
            var editor = TestConNonConformita("QA collega - anomalia esistente");
            var ncPrima = TestCoverageSeed.FileNonConformita(Workspace).Count;

            editor.CollegaSegnalazioneAdAnomalia(0, NcAperta);
            Wait.ForCondition(() => !editor.HaSegnalazioniDaClassificare,
                "la segnalazione collegata esce dal pannello da classificare");

            editor.Save();
            Assert.Equal(1, RicaricaLista().RowCount());

            Assert.Equal(ncPrima, TestCoverageSeed.FileNonConformita(Workspace).Count);
            var idTest = TestCoverageSeed.LeggiTestCase(TestCoverageSeed.FileTestCase(Workspace).Single()).Id;
            var nc = TestCoverageSeed.LeggiNonConformita(
                TestCoverageSeed.FileNonConformita(Workspace).Single(f => Path.GetFileName(f) == $"nc_{NcAperta}.json"));
            Assert.Contains(nc.ProveCollegate, prova => prova.TestId == idTest);

            var seg = TestCoverageSeed.LeggiSegnalazione(Assert.Single(TestCoverageSeed.FileSegnalazioni(Workspace)));
            Assert.Equal(StatoSegnalazione.Classificata, seg.Stato);
            Assert.Equal(nc.Uid, seg.AnomaliaUid);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Collega_AdAnomaliaApertaEsistente_NonCreaUnaNuovaNcEAggiornaIlRiferimento), ex); }
    }
}
